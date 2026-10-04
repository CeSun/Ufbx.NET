// S4bCheck: isolated differential verification for the S4b-1 animation-evaluation module.
//
// Replays the record stream of tools/s4b_oracle.c (built with zig, `#include "ufbx.c"`, so it
// runs the original `ufbx_evaluate_*` / `ufbxi_*` evaluation code) through the ported evaluators
// in src/Ufbx/Parse/Evaluate.cs and compares the two record streams line by line. Every line is
// produced by both sides with the same grammar (see the oracle header), so the first divergence
// is a real behavioural difference, not a formatting one.
//
// Covered entry points (ufbx.c:25637-26050, 30643-30718, 30835-31184):
//   P/Q  ufbx_evaluate_props_flags + ufbx_evaluate_prop_flags_len over every element of every
//        typed list, twice per time (with and without NO_EXTRAPOLATION)
//   R    ufbx_evaluate_transform_flags for every node x 14 flag combinations (component subsets,
//        scale-helper / componentwise-scale ignores, no-extrapolation)
//   B    ufbx_evaluate_blend_weight_flags for every blend channel
//   C    ufbx_evaluate_curve_flags for every curve at the swept time and at 8 range-relative
//        times (inside, exactly at, and just outside [min_time, max_time])
//   A    ufbx_evaluate_anim_value_real_flags / _vec3_flags for every anim value
//   L    layer metadata + ufbxi_anim_layer_might_contain_id over a window of ids + per-anim-prop
//        real/vec3 evaluation
//   D    ufbx_find_prop/real/vec3/int/bool/string/blob over every element x name sweep
//   X    ufbx_catch_get_vertex_real/vec2/vec3/vec4/w_vec3 + the plain inline forms over every
//        mesh attribute, including the index == indices.count panic probe
//   Y    the same catch forms over a synthetic attribute carrying UFBX_NO_INDEX and an index that
//        is out of range for `values` but in range for `values_w`
//   F    the scene/element lookup group -- ufbx_find_element(_len)/node/material/anim_stack,
//        find_prop_element/get_prop_element, find_anim_prop(s), find_prop_texture,
//        find_shader_prop(_bindings), find_shader_texture_input, get_bone_pose,
//        get_compatible_matrix_for_normals and find_face_index; 9 rollups per file (see
//        dump_scene_find())
//   V    ufbx_evaluate_scene at frames 1,4,9,...,81 hashed with the golden hasher -- i.e. the
//        authoritative scene hashes of tools/golden_hashes.txt, replayed per file
//
// Usage (from C:/Workspace/_analyze_ufbx, so the `data/...` corpus paths resolve):
//   dotnet run --project C:/Workspace/ufbx-cs/tools/S4bCheck -c Release -- \
//       C:/Workspace/ufbx-cs/tools/s4b_oracle.txt C:/Workspace/ufbx-cs/tools/s4b_corpus.txt
// `-v <fi> [<ti> [<kind>]]` after the two paths makes the port emit the same explicit `E ...`
// records the oracle emits with its own `-v`, for localising a mismatching rollup.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ufbx;

namespace S4bCheck
{
    static class Program
    {
        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x100000001b3UL;

        static ulong hash = FnvBasis;
        static readonly List<string> Lines = new List<string>();
        static int verbose;
        static string portDump;
        static int verboseFile = -1;
        static int verboseTime = -1;
        static char verboseKind = '\0';

        static readonly string[] PropNames = {
            "Lcl Translation", "Lcl Rotation", "Lcl Scaling",
            "RotationOrder", "PreRotation", "PostRotation",
            "RotationPivot", "RotationOffset", "ScalingPivot", "ScalingOffset",
            "GeometricTranslation", "GeometricRotation", "GeometricScaling",
            "DeformPercent", "DefaultAttributeIndex", "InheritType",
            "Color", "DiffuseColor", "EmissiveColor", "SpecularColor", "Shininess",
            "Intensity", "FarPlane", "NearPlane", "FieldOfView", "FilmWidth", "FilmHeight",
            "Size", "Name", "FileName", "CurrentTimeMarker",
            "Missing", "Missing Longer Name", "a", "ab", "abc", "z",
        };

        const int MaxProps = 512;

        static readonly double[] Frames = {
            -1000.0, -137.0, -64.0, -13.0, -3.0, -1.0, -0.5, 0.0, 0.25, 0.5, 1.0, 1.75,
            2.0, 3.0, 4.0, 5.5, 7.0, 9.0, 12.0, 13.0, 16.0, 21.0, 24.0, 33.0, 48.0, 64.0,
            100.0, 137.0, 250.0, 1000.0,
        };

        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "-q") return QuatChain(args);
            if (args.Length > 1 && args[0] == "-m") return MathSweep(args[1]);

            if (args.Length < 2) {
                Console.Error.WriteLine("usage: S4bCheck <oracle.txt> <corpus.txt> [-v fi [ti [kind]]]");
                return 2;
            }
            string oraclePath = args[0];
            string corpusPath = args[1];
            for (int i = 2; i < args.Length; i++) {
                if (args[i] == "-o" && i + 1 < args.Length) {
                    portDump = args[++i];
                    continue;
                }
                if (args[i] != "-v") continue;
                verbose = 1;
                if (i + 1 < args.Length) verboseFile = int.Parse(args[++i]);
                if (i + 1 < args.Length) verboseTime = int.Parse(args[++i]);
                if (i + 1 < args.Length) verboseKind = args[++i][0];
            }

            var corpus = new List<string>();
            foreach (string raw in File.ReadLines(corpusPath)) {
                string line = raw.Trim();
                if (line.Length > 0) corpus.Add(line.Replace('\\', '/'));
            }

            var expected = new List<string>();
            foreach (string raw in File.ReadAllLines(oraclePath)) {
                string line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith("E ")) continue;
                expected.Add(line);
            }

            Run(corpus);

            if (portDump != null) File.WriteAllLines(portDump, Lines);

            int mismatches = 0;
            int shown = 0;
            int n = Math.Max(expected.Count, Lines.Count);
            for (int i = 0; i < n; i++) {
                string e = i < expected.Count ? expected[i] : "<missing>";
                string a = i < Lines.Count ? Lines[i] : "<missing>";
                if (e == a) continue;
                mismatches++;
                if (shown < 25) {
                    Console.WriteLine("MISMATCH line " + (i + 1));
                    Console.WriteLine("  oracle: " + e);
                    Console.WriteLine("  port  : " + a);
                    shown++;
                }
            }

            Console.WriteLine("lines " + Lines.Count + " expected " + expected.Count
                + " mismatches " + mismatches);
            if (mismatches == 0) Console.WriteLine("ALL MATCH");
            return mismatches == 0 ? 0 : 1;
        }

        // Scratch mode: replay the additive `compose_rotation` quaternion chain of
        // ufbxi_combine_anim_layer (ufbx.c:25727-25732) from explicit IEEE-754 bit patterns, so a
        // single-ULP prop difference can be attributed to one math primitive. Mirrors the `q` line
        // of tools/_s4b_trace.c.
        static int QuatChain(string[] args)
        {
            static double Bits(string s) => BitConverter.Int64BitsToDouble(Convert.ToInt64(s, 16));
            static string D(double v) => ((ulong)BitConverter.DoubleToInt64Bits(v)).ToString("x16");

            UfbxVec3 before = new UfbxVec3(Bits(args[1]), Bits(args[2]), Bits(args[3]));
            UfbxVec3 v = new UfbxVec3(Bits(args[4]), Bits(args[5]), Bits(args[6]));
            double weight = Bits(args[7]);

            UfbxQuat a = UfbxQuat.EulerToQuat(before, UfbxRotationOrder.Xyz);
            UfbxQuat b = UfbxQuat.EulerToQuat(v, UfbxRotationOrder.Xyz);
            UfbxQuat bs = UfbxQuat.Slerp(UfbxQuat.Identity, b, weight);
            UfbxQuat res = UfbxQuat.Mul(a, bs);
            UfbxVec3 e = UfbxQuat.ToEuler(res, UfbxRotationOrder.Xyz);

            Console.Write(" q a=(" + D(a.W) + "," + D(a.X) + "," + D(a.Y) + "," + D(a.Z) + ")");
            Console.Write(" b=(" + D(b.W) + "," + D(b.X) + "," + D(b.Y) + "," + D(b.Z) + ")");
            Console.Write(" bs=(" + D(bs.W) + "," + D(bs.X) + "," + D(bs.Y) + "," + D(bs.Z) + ")");
            Console.Write(" res=(" + D(res.W) + "," + D(res.X) + "," + D(res.Y) + "," + D(res.Z) + ")");
            {
                double qw = res.W, qx = res.X, qy = res.Y, qz = res.Z;
                double tt = 2.0 * (qw * qy - qx * qz);
                double azY = 2.0 * (qw * qz + qx * qy), azX = 2.0 * (qw * qw + qx * qx) - 1.0;
                double axY = -2.0 * (qw * qx + qy * qz), axX = 2.0 * (qw * qw + qz * qz) - 1.0;
                Console.Write(" args t=" + D(tt) + " az=(" + D(azY) + "," + D(azX) + ") ax=(" + D(axY) + "," + D(axX) + ")");
                Console.Write(" port_atan2_az=" + D(UfbxMath.Atan2(azY, azX)) + " port_atan2_ax=" + D(UfbxMath.Atan2(axY, axX)));
                Console.Write(" port_asin_t=" + D(UfbxMath.Asin(tt)));
            }
            Console.WriteLine(" e=(" + D(e.X) + "," + D(e.Y) + "," + D(e.Z) + ")");
            return 0;
        }

        // Scratch mode: replay tools/_s4b_mathvec.c records (`atan2 <yhex> <xhex> <reshex>`)
        // through the ported libm transcription and report the divergence rate.
        static int MathSweep(string path)
        {
            int total = 0, bad = 0, shown = 0;
            foreach (string raw in File.ReadLines(path)) {
                string[] f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 4 || f[0] != "atan2") continue;
                double y = BitsD(f[1]), x = BitsD(f[2]);
                ulong want = Convert.ToUInt64(f[3], 16);
                ulong got = (ulong)BitConverter.DoubleToInt64Bits(UfbxMath.Atan2(y, x));
                total++;
                if (got == want) continue;
                bad++;
                if (shown < 20) {
                    Console.WriteLine("neq y=" + f[1] + " x=" + f[2] + " want=" + want.ToString("x16")
                        + " got=" + got.ToString("x16"));
                    shown++;
                }
            }
            Console.WriteLine("atan2 total " + total + " neq " + bad);
            return bad == 0 ? 0 : 1;
        }

        static double BitsD(string hex) =>
            BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(hex, 16)));

        static void Run(List<string> corpus)
        {
            for (int fi = 0; fi < corpus.Count; fi++) {
                string path = corpus[fi];
                UfbxLoadOpts opts = new UfbxLoadOpts();
                opts.LoadExternalFiles = true;
                opts.IgnoreMissingExternalFiles = true;
                opts.EvaluateCaches = true;
                opts.EvaluateSkinning = true;
                opts.TargetAxes = UfbxCoordinateAxes.RightHandedYUp;
                opts.TargetUnitMeters = 1.0;

                UfbxError error = new UfbxError();
                UfbxScene scene = UfbxApi.LoadFile(path, opts, error);
                if (scene == null) {
                    Lines.Add("S " + fi + " 0 0 0 0 0 0 0 0");
                    Console.Error.WriteLine("load failed: " + path + ": " + (error.Description ?? error.Type.ToString()));
                    continue;
                }

                int numElems = 0;
                for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                    UfbxElement[] list = scene.ElementsByType(type);
                    numElems += list != null ? list.Length : 0;
                }
                UfbxAnim anim = scene.Anim;
                Lines.Add("S " + fi + " " + numElems + " " + Len(scene.Nodes) + " " + Len(scene.AnimCurves)
                    + " " + Len(scene.AnimValues) + " " + Len(scene.BlendChannels) + " " + Len(scene.AnimLayers)
                    + " " + (anim != null ? Len(anim.PropOverrides) : 0) + " " + (anim != null ? 1 : 0));

                double[] times = BuildTimes(scene);
                for (int t = 0; t < times.Length; t++) {
                    Lines.Add("T " + fi + " " + t + " " + Dbits(times[t]));
                }

                DumpFind(fi, scene);
                DumpVertexAccess(fi, scene);
                DumpSynthAccess(fi);
                DumpSceneFind(fi, scene);
                for (int t = 0; t < times.Length; t++) {
                    DumpProps(fi, t, 0, scene, times[t], 0);
                    DumpProps(fi, t, 1, scene, times[t], (uint)UfbxEvaluateFlags.NoExtrapolation);
                    DumpTransforms(fi, t, scene, times[t]);
                    DumpBlendWeights(fi, t, scene, times[t]);
                    DumpCurves(fi, t, scene, times[t]);
                    DumpLayers(fi, t, scene, times[t]);
                }

                DumpSceneHash(fi, scene);

                // C: the `W` record of the oracle -- the source scene re-hashed after the
                // evaluation sweep, which must be unchanged by it.
                Lines.Add("W " + fi + " " + UfbxHashScene.HashScene(scene).ToString("x16"));

                UfbxApi.FreeScene(scene);
            }

            Lines.Add("DONE " + corpus.Count + " 0");
        }

        static int Len(Array a) => a != null ? a.Length : 0;

        // C: dump_scene_hash() -- the golden computation (test/hash_scene.c:127-141, frames i*i for
        // i = 1..9) replayed through UfbxApi.EvaluateScene and hashed with the same
        // test/hash_scene.h transcription the golden acceptance run uses. A matching `V` line is
        // therefore also a matching entry in tools/golden_hashes.txt.
        static void DumpSceneHash(int fi, UfbxScene scene)
        {
            UfbxAnim anim = scene.Anim;
            if (anim == null) return;
            double fps = scene.Settings.FramesPerSecond;
            double tb = anim.TimeBegin;
            if (!(fps > 0.0)) return;

            for (int i = 1; i <= 9; i++) {
                int frame = i * i;
                double time = tb + (double) frame / fps;
                UfbxError err = new UfbxError();
                UfbxScene state = UfbxApi.EvaluateScene(scene, null, time, null, err);
                if (state == null) {
                    Lines.Add("V " + fi + " " + frame + " ERR " + (int) err.Type);
                    continue;
                }
                Lines.Add("V " + fi + " " + frame + " " + UfbxHashScene.HashScene(state).ToString("x16"));
            }
        }

        // C: build_times() of the oracle, transcribed literally so the swept times are the same
        // doubles on both sides (the `T` records verify that they come out identical).
        static double[] BuildTimes(UfbxScene scene)
        {
            double fps = scene.Settings.FramesPerSecond;
            double tb = scene.Anim != null ? scene.Anim.TimeBegin : 0.0;
            var outTimes = new List<double>(128);
            for (int i = 0; i < Frames.Length && outTimes.Count < 128; i++) {
                outTimes.Add(tb + Frames[i] / fps);
            }
            if (scene.Anim != null && outTimes.Count + 4 <= 128) {
                outTimes.Add(scene.Anim.TimeBegin);
                outTimes.Add(scene.Anim.TimeEnd);
                outTimes.Add(scene.Anim.TimeBegin - 1.0 / fps);
                outTimes.Add(scene.Anim.TimeEnd + 1.0 / fps);
            }
            if (outTimes.Count + 4 <= 128) {
                outTimes.Add(0.0);
                outTimes.Add(-0.0);
                outTimes.Add(1e-7);
                outTimes.Add(1.0 / (fps * 3.0));
            }
            return outTimes.ToArray();
        }

        static bool VerboseHere(int fi, int ti, char kind)
        {
            if (verbose == 0 || fi != verboseFile) return false;
            if (verboseTime >= 0 && ti != verboseTime) return false;
            if (verboseKind != '\0' && verboseKind != kind) return false;
            return true;
        }

        // ==================================================================
        // Hashing (mirrors the oracle's hh_* helpers)
        // ==================================================================

        static void HBytes(byte[] data)
        {
            if (data == null) return;
            unchecked {
                for (int i = 0; i < data.Length; i++) {
                    hash ^= data[i];
                    hash *= FnvPrime;
                }
            }
        }

        // C: hh_bytes(str.data, str.length) over the interned one-byte-per-char strings.
        static void HStr(string s)
        {
            if (s == null) return;
            unchecked {
                for (int i = 0; i < s.Length; i++) {
                    hash ^= (byte)s[i];
                    hash *= FnvPrime;
                }
            }
        }

        static void HU32(uint v)
        {
            unchecked {
                for (int i = 0; i < 4; i++) { hash ^= (byte)(v >> (8 * i)); hash *= FnvPrime; }
            }
        }

        static void HI32(int v) => HU32(unchecked((uint)v));

        static void HU64(ulong v)
        {
            unchecked {
                for (int i = 0; i < 8; i++) { hash ^= (byte)(v >> (8 * i)); hash *= FnvPrime; }
            }
        }

        static void HI64(long v) => HU64(unchecked((ulong)v));

        static void HD(double d) => HU64(unchecked((ulong)BitConverter.DoubleToInt64Bits(d)));

        static ulong Take()
        {
            ulong h = hash;
            hash = FnvBasis;
            return h;
        }

        static void HProp(UfbxProp p)
        {
            HU32(p.InternalKey);
            HI32((int)p.Type);
            HU32(unchecked((uint)p.Flags));
            // C: hash of value_vec4 (the four doubles of the value union) + value_int + strings.
            UfbxVec4 v = p.ValueVec4;
            HD(v.X); HD(v.Y); HD(v.Z); HD(v.W);
            HI64(p.ValueInt);
            HStr(p.ValueStr);
            HU64(p.ValueStr != null ? (ulong)p.ValueStr.Length : 0UL);
            HBytes(p.ValueBlob);
            HU64(p.ValueBlob != null ? (ulong)p.ValueBlob.Length : 0UL);
        }

        static void HTransform(UfbxTransform t)
        {
            HD(t.Translation.X); HD(t.Translation.Y); HD(t.Translation.Z);
            HD(t.Rotation.X); HD(t.Rotation.Y); HD(t.Rotation.Z); HD(t.Rotation.W);
            HD(t.Scale.X); HD(t.Scale.Y); HD(t.Scale.Z);
        }

        static string Dbits(double v)
        {
            return unchecked(((ulong)BitConverter.DoubleToInt64Bits(v))).ToString("x16", CultureInfo.InvariantCulture);
        }

        static string Hbits(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);

        // Explicit `E` record for one property, formatted like the oracle's dump_prop().
        static string PropText(UfbxProp p)
        {
            UfbxVec4 v = p.ValueVec4;
            var sb = new System.Text.StringBuilder();
            sb.Append(p.InternalKey.ToString("x8", CultureInfo.InvariantCulture));
            sb.Append(' ').Append((int)p.Type).Append(' ').Append(unchecked((uint)p.Flags));
            sb.Append(' ').Append(Dbits(v.X)).Append(' ').Append(Dbits(v.Y));
            sb.Append(' ').Append(Dbits(v.Z)).Append(' ').Append(Dbits(v.W));
            sb.Append(' ').Append(p.ValueInt.ToString(CultureInfo.InvariantCulture));
            int len = p.ValueStr != null ? p.ValueStr.Length : 0;
            sb.Append(' ').Append(len);
            if (len > 0) {
                sb.Append(" \"");
                for (int i = 0; i < len; i++) {
                    char c = p.ValueStr[i];
                    sb.Append(c >= 0x20 && c < 0x7f ? c : '.');
                }
                sb.Append('"');
            }
            sb.Append(' ').Append(p.ValueBlob != null ? p.ValueBlob.Length : 0);
            return sb.ToString();
        }

        // ==================================================================
        // Record dumps (one per oracle record kind)
        // ==================================================================

        static void DumpProps(int fi, int ti, int variant, UfbxScene scene, double time, uint flags)
        {
            UfbxAnim anim = scene.Anim;
            UfbxProp[] buf = new UfbxProp[MaxProps];

            int total = 0;
            hash = FnvBasis;
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                UfbxElement[] list = scene.ElementsByType(type) ?? Array.Empty<UfbxElement>();
                for (int ei = 0; ei < list.Length; ei++) {
                    UfbxElement elem = list[ei];
                    UfbxProps props = UfbxEvaluate.PropsFlags(anim, elem, time, buf, flags);
                    total++;
                    HU32(elem.ElementId);
                    HU32((uint)type);
                    HU32((uint)ei);
                    HU32((uint)props.Props.Length);
                    HU32((uint)props.NumAnimated);
                    for (int i = 0; i < props.Props.Length; i++) HProp(props.Props[i]);
                    if (VerboseHere(fi, ti, 'P')) {
                        var sb = new System.Text.StringBuilder();
                        sb.Append("E ").Append(fi).Append(' ').Append(ti).Append(" P ").Append(variant)
                          .Append(' ').Append(elem.ElementId).Append(' ').Append(props.Props.Length).Append(' ');
                        for (int i = 0; i < props.Props.Length; i++) {
                            if (i > 0) sb.Append(" | ");
                            sb.Append(PropText(props.Props[i]));
                        }
                        Lines.Add(sb.ToString());
                    }
                }
            }
            Lines.Add("P " + fi + " " + ti + " " + variant + " " + Hbits(Take()) + " " + total);

            total = 0;
            hash = FnvBasis;
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                UfbxElement[] list = scene.ElementsByType(type) ?? Array.Empty<UfbxElement>();
                for (int ei = 0; ei < list.Length; ei++) {
                    UfbxElement elem = list[ei];
                    for (int n = 0; n < PropNames.Length; n++) {
                        string name = PropNames[n];
                        UfbxProp pr = UfbxEvaluate.PropFlagsLen(anim, elem, name, name.Length, time, flags);
                        total++;
                        HU32(elem.ElementId);
                        HU32((uint)n);
                        HProp(pr);
                        if (VerboseHere(fi, ti, 'Q')) {
                            Lines.Add("E " + fi + " " + ti + " Q " + variant + " " + elem.ElementId + " " + n
                                + " " + PropText(pr));
                        }
                    }
                }
            }
            Lines.Add("Q " + fi + " " + ti + " " + variant + " " + Hbits(Take()) + " " + total);
        }

        static readonly uint[] TransformFlagSet = {
            0,
            (uint)UfbxTransformFlags.ExplicitIncludes,
            (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeRotation,
            (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeScale,
            (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeTranslation,
            (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeRotation | (uint)UfbxTransformFlags.IncludeScale,
            (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeRotation | (uint)UfbxTransformFlags.IncludeTranslation,
            (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeScale | (uint)UfbxTransformFlags.IncludeTranslation,
            (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeRotation | (uint)UfbxTransformFlags.IncludeScale | (uint)UfbxTransformFlags.IncludeTranslation,
            (uint)UfbxTransformFlags.IgnoreScaleHelper,
            (uint)UfbxTransformFlags.IgnoreComponentwiseScale,
            (uint)UfbxTransformFlags.IgnoreScaleHelper | (uint)UfbxTransformFlags.IgnoreComponentwiseScale,
            (uint)UfbxTransformFlags.NoExtrapolation,
            (uint)UfbxTransformFlags.NoExtrapolation | (uint)UfbxTransformFlags.ExplicitIncludes | (uint)UfbxTransformFlags.IncludeRotation | (uint)UfbxTransformFlags.IncludeScale,
        };

        static void DumpTransforms(int fi, int ti, UfbxScene scene, double time)
        {
            UfbxAnim anim = scene.Anim;
            int total = 0;
            hash = FnvBasis;
            UfbxNode[] nodes = scene.Nodes ?? Array.Empty<UfbxNode>();
            for (int i = 0; i < nodes.Length; i++) {
                UfbxNode node = nodes[i];
                for (int f = 0; f < TransformFlagSet.Length; f++) {
                    UfbxTransform t = UfbxEvaluate.TransformFlags(anim, node, time, TransformFlagSet[f]);
                    total++;
                    HU32(node.ElementId);
                    HU32((uint)f);
                    HTransform(t);
                    if (VerboseHere(fi, ti, 'R')) {
                        Lines.Add("E " + fi + " " + ti + " R " + node.ElementId + " " + f + " "
                            + Dbits(t.Translation.X) + " " + Dbits(t.Translation.Y) + " " + Dbits(t.Translation.Z) + " "
                            + Dbits(t.Rotation.X) + " " + Dbits(t.Rotation.Y) + " " + Dbits(t.Rotation.Z) + " " + Dbits(t.Rotation.W) + " "
                            + Dbits(t.Scale.X) + " " + Dbits(t.Scale.Y) + " " + Dbits(t.Scale.Z));
                    }
                }
            }
            Lines.Add("R " + fi + " " + ti + " " + Hbits(Take()) + " " + total);
        }

        static void DumpBlendWeights(int fi, int ti, UfbxScene scene, double time)
        {
            UfbxAnim anim = scene.Anim;
            UfbxBlendChannel[] chans = scene.BlendChannels ?? Array.Empty<UfbxBlendChannel>();
            int total = 0;
            hash = FnvBasis;
            for (int i = 0; i < chans.Length; i++) {
                UfbxBlendChannel ch = chans[i];
                for (uint f = 0; f < 2; f++) {
                    double w = UfbxEvaluate.BlendWeightFlags(anim, ch, time,
                        f != 0 ? (uint)UfbxEvaluateFlags.NoExtrapolation : 0u);
                    total++;
                    HU32(ch.ElementId);
                    HU32(f);
                    HD(w);
                    if (VerboseHere(fi, ti, 'B')) {
                        Lines.Add("E " + fi + " " + ti + " B " + ch.ElementId + " " + f + " " + Dbits(w));
                    }
                }
            }
            Lines.Add("B " + fi + " " + ti + " " + Hbits(Take()) + " " + total);
        }

        static void DumpCurves(int fi, int ti, UfbxScene scene, double time)
        {
            int total = 0;
            hash = FnvBasis;
            UfbxAnimCurve[] curves = scene.AnimCurves ?? Array.Empty<UfbxAnimCurve>();
            for (int i = 0; i < curves.Length; i++) {
                UfbxAnimCurve curve = curves[i];
                double[] ct = new double[9];
                ct[0] = time;
                ct[1] = curve.MinTime;
                ct[2] = curve.MaxTime;
                ct[3] = curve.MinTime - 1.0;
                ct[4] = curve.MaxTime + 1.0;
                ct[5] = curve.MinTime - 0.25;
                ct[6] = curve.MaxTime + 0.25;
                ct[7] = curve.MinTime + (curve.MaxTime - curve.MinTime) * 0.5;
                ct[8] = curve.MinTime + (curve.MaxTime - curve.MinTime) * 0.5 + 1e-9;
                for (int c = 0; c < ct.Length; c++) {
                    for (uint f = 0; f < 2; f++) {
                        double v = UfbxEvaluate.CurveFlags(curve, ct[c], 1.2345678,
                            f != 0 ? (uint)UfbxEvaluateFlags.NoExtrapolation : 0u);
                        total++;
                        HU32(curve.ElementId);
                        HU32((uint)c);
                        HU32(f);
                        HD(ct[c]);
                        HD(v);
                        if (VerboseHere(fi, ti, 'C')) {
                            Lines.Add("E " + fi + " " + ti + " C " + curve.ElementId + " " + c + " " + f
                                + " " + Dbits(ct[c]) + " " + Dbits(v));
                        }
                    }
                }
            }
            Lines.Add("C " + fi + " " + ti + " " + Hbits(Take()) + " " + total);

            total = 0;
            hash = FnvBasis;
            UfbxAnimValue[] values = scene.AnimValues ?? Array.Empty<UfbxAnimValue>();
            for (int i = 0; i < values.Length; i++) {
                UfbxAnimValue av = values[i];
                for (uint f = 0; f < 2; f++) {
                    uint flags = f != 0 ? (uint)UfbxEvaluateFlags.NoExtrapolation : 0u;
                    double r = UfbxEvaluate.AnimValueRealFlags(av, time, flags);
                    UfbxVec3 v = UfbxEvaluate.AnimValueVec3Flags(av, time, flags);
                    total++;
                    HU32(av.ElementId);
                    HU32(f);
                    HD(r); HD(v.X); HD(v.Y); HD(v.Z);
                    if (VerboseHere(fi, ti, 'A')) {
                        Lines.Add("E " + fi + " " + ti + " A " + av.ElementId + " " + f + " "
                            + Dbits(r) + " " + Dbits(v.X) + " " + Dbits(v.Y) + " " + Dbits(v.Z));
                    }
                }
            }
            Lines.Add("A " + fi + " " + ti + " " + Hbits(Take()) + " " + total);
        }

        static void DumpLayers(int fi, int ti, UfbxScene scene, double time)
        {
            int total = 0;
            hash = FnvBasis;
            UfbxAnimLayer[] layers = scene.AnimLayers ?? Array.Empty<UfbxAnimLayer>();
            for (int i = 0; i < layers.Length; i++) {
                UfbxAnimLayer layer = layers[i];
                HU32(layer.ElementId);
                HD(layer.Weight);
                HU32(layer.WeightIsAnimated ? 1u : 0u);
                HU32(layer.Blended ? 1u : 0u);
                HU32(layer.Additive ? 1u : 0u);
                HU32(layer.ComposeRotation ? 1u : 0u);
                HU32(layer.ComposeScale ? 1u : 0u);
                HU32(layer.MinElementId);
                HU32(layer.MaxElementId);
                for (int k = 0; k < 4; k++) {
                    HU32(layer.ElementIdBitmask != null && k < layer.ElementIdBitmask.Length
                        ? layer.ElementIdBitmask[k] : 0u);
                }
                total++;

                for (long d = -8; d <= 8; d++) {
                    uint id = unchecked(layer.MinElementId + unchecked((uint)d));
                    bool ok = UfbxiEvaluate.AnimLayerMightContainId(layer, id);
                    HI64(d);
                    HU32(ok ? 1u : 0u);
                    total++;
                }

                UfbxAnimProp[] approp = layer.AnimProps ?? Array.Empty<UfbxAnimProp>();
                for (int a = 0; a < approp.Length; a++) {
                    UfbxAnimProp ap = approp[a];
                    HU32(ap.Element.ElementId);
                    HU32(ap.InternalKey);
                    HStr(ap.PropName);
                    double r = UfbxEvaluate.AnimValueRealFlags(ap.AnimValue, time, 0);
                    UfbxVec3 v = UfbxEvaluate.AnimValueVec3Flags(ap.AnimValue, time, 0);
                    HD(r); HD(v.X); HD(v.Y); HD(v.Z);
                    total++;
                    if (VerboseHere(fi, ti, 'L')) {
                        Lines.Add("E " + fi + " " + ti + " L " + ap.Element.ElementId + " " + a + " "
                            + Dbits(r) + " " + Dbits(v.X) + " " + Dbits(v.Y) + " " + Dbits(v.Z));
                    }
                }
            }
            Lines.Add("L " + fi + " " + ti + " " + Hbits(Take()) + " " + total);
        }

        static void DumpFind(int fi, UfbxScene scene)
        {
            int total = 0;
            hash = FnvBasis;
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                UfbxElement[] list = scene.ElementsByType(type) ?? Array.Empty<UfbxElement>();
                for (int ei = 0; ei < list.Length; ei++) {
                    UfbxElement elem = list[ei];
                    for (int n = 0; n < PropNames.Length; n++) {
                        string name = PropNames[n];
                        int len = name.Length;
                        bool found = UfbxEvaluate.FindPropLen(elem.Props, name, len, out UfbxProp p);
                        total++;
                        HU32(elem.ElementId);
                        HU32((uint)n);
                        HU32(found ? 1u : 0u);
                        if (found) HProp(p);
                        HD(UfbxEvaluate.FindRealLen(elem.Props, name, len, -987.654));
                        UfbxVec3 v = UfbxEvaluate.FindVec3Len(elem.Props, name, len, new UfbxVec3(1.5, 2.5, 3.5));
                        HD(v.X); HD(v.Y); HD(v.Z);
                        HI64(UfbxEvaluate.FindIntLen(elem.Props, name, len, -424242));
                        HU32(UfbxEvaluate.FindBoolLen(elem.Props, name, len, true) ? 1u : 0u);
                        string s = UfbxEvaluate.FindStringLen(elem.Props, name, len, "s");
                        HStr(s);
                        HU64(s != null ? (ulong)s.Length : 0UL);
                        byte[] b = UfbxEvaluate.FindBlobLen(elem.Props, name, len, Array.Empty<byte>());
                        HBytes(b);
                        HU64(b != null ? (ulong)b.Length : 0UL);
                    }
                }
            }
            Lines.Add("D " + fi + " " + Hbits(Take()) + " " + total);
        }

        // ==================================================================
        // X: vertex attribute accessors (mirrors dump_vertex_access())
        // ==================================================================

        static void HPanic(UfbxPanic p)
        {
            HU32(p.DidPanic ? 1u : 0u);
            HU64(unchecked((ulong)p.MessageLength));
            HStr(p.Message);
        }

        static int AccReal(UfbxVertexReal v, bool plain)
        {
            int count = Len(v.Indices);
            for (int i = 0; i <= count; i++) {
                UfbxPanic p = default;
                HD(UfbxTopologyApi.CatchGetVertexReal(ref p, v, i));
                HPanic(p);
                if (plain && i < count) HD(UfbxTopologyApi.GetVertexReal(v, i));
            }
            return count + 1;
        }

        static int AccVec2(UfbxVertexVec2 v, bool plain)
        {
            int count = Len(v.Indices);
            for (int i = 0; i <= count; i++) {
                UfbxPanic p = default;
                UfbxVec2 got = UfbxTopologyApi.CatchGetVertexVec2(ref p, v, i);
                HD(got.X); HD(got.Y);
                HPanic(p);
                if (plain && i < count) {
                    UfbxVec2 got2 = UfbxTopologyApi.GetVertexVec2(v, i);
                    HD(got2.X); HD(got2.Y);
                }
            }
            return count + 1;
        }

        static int AccVec3(UfbxVertexVec3 v, bool plain)
        {
            int count = Len(v.Indices);
            for (int i = 0; i <= count; i++) {
                UfbxPanic p = default;
                UfbxVec3 got = UfbxTopologyApi.CatchGetVertexVec3(ref p, v, i);
                HD(got.X); HD(got.Y); HD(got.Z);
                HPanic(p);
                if (plain && i < count) {
                    UfbxVec3 got3 = UfbxTopologyApi.GetVertexVec3(v, i);
                    HD(got3.X); HD(got3.Y); HD(got3.Z);
                }
            }
            return count + 1;
        }

        static int AccVec4(UfbxVertexVec4 v, bool plain)
        {
            int count = Len(v.Indices);
            for (int i = 0; i <= count; i++) {
                UfbxPanic p = default;
                UfbxVec4 got = UfbxTopologyApi.CatchGetVertexVec4(ref p, v, i);
                HD(got.X); HD(got.Y); HD(got.Z); HD(got.W);
                HPanic(p);
                if (plain && i < count) {
                    UfbxVec4 got4 = UfbxTopologyApi.GetVertexVec4(v, i);
                    HD(got4.X); HD(got4.Y); HD(got4.Z); HD(got4.W);
                }
            }
            return count + 1;
        }

        static int AccWVec3(UfbxVertexVec3 v, bool plain)
        {
            int count = Len(v.Indices);
            for (int i = 0; i <= count; i++) {
                UfbxPanic p = default;
                HD(UfbxTopologyApi.CatchGetVertexWVec3(ref p, v, i));
                HPanic(p);
                if (plain && i < count) HD(UfbxTopologyApi.GetVertexWVec3(v, i));
            }
            return count + 1;
        }

        static void DumpVertexAccess(int fi, UfbxScene scene)
        {
            int total = 0;
            hash = FnvBasis;
            UfbxMesh[] meshes = scene.Meshes ?? Array.Empty<UfbxMesh>();
            for (int mi = 0; mi < meshes.Length; mi++) {
                UfbxMesh mesh = meshes[mi];
                HU32(mesh.ElementId);
                total += AccReal(mesh.VertexCrease, true);
                total += AccVec2(mesh.VertexUv, true);
                total += AccVec3(mesh.VertexPosition, true);
                total += AccVec3(mesh.VertexNormal, true);
                total += AccVec3(mesh.VertexTangent, true);
                total += AccVec3(mesh.VertexBitangent, true);
                total += AccVec4(mesh.VertexColor, true);
                total += AccWVec3(mesh.VertexPosition, true);
                total += AccWVec3(mesh.VertexNormal, true);
            }
            Lines.Add("X " + fi + " " + Hbits(Take()) + " " + total);
        }

        // ==================================================================
        // Y: synthetic attribute accessors (mirrors dump_synth_access())
        //
        // The port's `Values.Length` *is* C's `values.count`, so the guarded layout C uses for a
        // UFBX_NO_INDEX read (`values.data[-1]`, the zero element at ufbx.c:28003) is modelled by
        // the accessors returning zero -- same value, no offset arithmetic.
        // ==================================================================

        static void DumpSynthAccess(int fi)
        {
            int total = 0;
            hash = FnvBasis;

            const int NumValues = 8;
            uint[] idx = { 0, 2, 7, UfbxConstants.NoIndex, 8, 3 };

            double[] r = new double[NumValues];
            UfbxVec2[] v2 = new UfbxVec2[NumValues];
            UfbxVec3[] v3 = new UfbxVec3[NumValues];
            UfbxVec4[] v4 = new UfbxVec4[NumValues];
            double[] w = new double[12];
            for (int i = 0; i < NumValues; i++) {
                double d = i * 1.75 - 3.25;
                r[i] = d;
                v2[i] = new UfbxVec2(d, -d);
                v3[i] = new UfbxVec3(d, d * 0.25, 1.0 / (1.0 + d));
                v4[i] = new UfbxVec4(d, d + 1.0, d - 1.0, (i & 1) != 0 ? 0.125 : -0.25);
            }
            for (int i = 0; i < w.Length; i++) w[i] = 100.0 + i;

            UfbxVertexReal vr = new UfbxVertexReal {
                Exists = true, Values = r, Indices = idx, ValueReals = 1,
            };
            UfbxVertexVec2 s2 = new UfbxVertexVec2 {
                Exists = true, Values = v2, Indices = idx, ValueReals = 2,
            };
            UfbxVertexVec3 s3 = new UfbxVertexVec3 {
                Exists = true, Values = v3, ValuesW = w, Indices = idx, ValueReals = 3,
            };
            UfbxVertexVec4 s4 = new UfbxVertexVec4 {
                Exists = true, Values = v4, Indices = idx, ValueReals = 4,
            };

            total += AccReal(vr, false);
            total += AccVec2(s2, false);
            total += AccVec3(s3, false);
            total += AccVec4(s4, false);
            total += AccWVec3(s3, false);

            Lines.Add("Y " + fi + " " + Hbits(Take()) + " " + total);
        }

        // ==================================================================
        // F: scene/element lookup group (mirrors dump_scene_find())
        //
        // Each part folds its probes in the oracle's exact order; `<i>` is the number of probes
        // folded, so a probe that is missing, duplicated or reordered shows up in the line.
        // ==================================================================

        // C: k_miss_names[] -- names that are not present in any corpus file (the miss path of every
        // lookup), plus one that collides with a real element type prefix.
        static readonly string[] MissNames = {
            "", "a", "ab", "abc", "Nope", "Nope Longer", "World::Root", "zzzzzzzz",
        };

        // C: k_conn_types[] -- the element types a property can name through a connection.
        static readonly UfbxElementType[] ConnTypes = {
            UfbxElementType.Node, UfbxElementType.Material,
            UfbxElementType.Texture, UfbxElementType.AnimValue,
        };

        static readonly UfbxProp[] NoProps = Array.Empty<UfbxProp>();
        static readonly UfbxElement[] NoElements = Array.Empty<UfbxElement>();

        // C: hh_element() -- element_id, or the sentinel for NULL.
        static void HElement(UfbxElement e) => HU32(e != null ? e.ElementId : 0xFFFFFFFFu);

        // C: hash_str() -- the bytes of a `ufbx_string` followed by its length.
        static void HStrLen(string s)
        {
            HStr(s);
            HU64(s != null ? (ulong)s.Length : 0UL);
        }

        // C: hash_matrix() -- the 12 stored doubles in m00..m23 order.
        static void HMatrix(UfbxMatrix m)
        {
            HD(m.M00); HD(m.M01); HD(m.M02); HD(m.M03);
            HD(m.M10); HD(m.M11); HD(m.M12); HD(m.M13);
            HD(m.M20); HD(m.M21); HD(m.M22); HD(m.M23);
        }

        static void HAnimProp(UfbxAnimProp ap)
        {
            HU32(ap != null ? 1u : 0u);
            if (ap != null) {
                HU32(ap.Element.ElementId);
                HStrLen(ap.PropName);
            }
        }

        static void HAnimPropList(UfbxAnimProp[] list)
        {
            int count = Len(list);
            HU64((ulong)count);
            if (count > 0) {
                HU32(list[0].Element.ElementId);
                HStrLen(list[0].PropName);
                HStrLen(list[count - 1].PropName);
            }
        }

        // C: prefix_mode_len() -- the full name, one byte short (a near-miss), or empty.
        static int PrefixModeLen(int length, int mode)
        {
            if (mode == 0) return length;
            if (mode == 1) return length > 0 ? length - 1 : 0;
            return 0;
        }

        // C: k_geom_rots[] / k_node_to_world[] -- the synthetic part-8 inputs. The corpus alone does
        // not probe the `geom_rot_mat` factor of ufbx_get_compatible_matrix_for_normals() (no corpus
        // node has a non-identity geometric rotation), so these pin it, together with the mirrored
        // (det < 0) and singular (det == 0) branches of ufbx_matrix_for_normals().
        static readonly UfbxQuat[] GeomRots = {
            new UfbxQuat { W =  1.0, X =  0.0, Y =  0.0, Z =  0.0 },
            new UfbxQuat { W =  0.5, X =  0.5, Y =  0.5, Z =  0.5 },
            new UfbxQuat { W =  0.5, X = -0.5, Y =  0.5, Z = -0.5 },
            new UfbxQuat { W =  0.0, X =  1.0, Y =  0.0, Z =  0.0 },
            new UfbxQuat { W =  0.7071067811865476, X =  0.7071067811865476, Y =  0.0, Z =  0.0 },
            new UfbxQuat { W =  0.5773502691896258, X =  0.5773502691896258, Y =  0.5773502691896258, Z =  0.5773502691896258 },
            new UfbxQuat { W =  0.1, X = -0.2, Y =  0.3, Z =  0.4 },
        };

        static readonly UfbxMatrix[] NodeToWorldCases = {
            new UfbxMatrix {
                M00 =  1.0, M10 =  0.0, M20 =  0.0,
                M01 =  0.0, M11 =  1.0, M21 =  0.0,
                M02 =  0.0, M12 =  0.0, M22 =  1.0,
                M03 =  0.0, M13 =  0.0, M23 =  0.0,
            },
            new UfbxMatrix {
                M00 =  0.6, M10 =  0.8, M20 =  0.0,
                M01 = -0.8, M11 =  0.6, M21 =  0.0,
                M02 =  0.0, M12 =  0.0, M22 =  2.0,
                M03 =  1.5, M13 = -2.5, M23 =  0.25,
            },
            new UfbxMatrix {
                M00 = -1.0, M10 =  0.0, M20 =  0.0,
                M01 =  0.0, M11 =  1.0, M21 =  0.0,
                M02 =  0.0, M12 =  0.0, M22 =  1.0,
                M03 =  3.0, M13 =  0.0, M23 = -4.0,
            },
            new UfbxMatrix {
                M00 =  0.0, M10 =  0.0, M20 =  0.0,
                M01 =  0.0, M11 =  0.0, M21 =  0.0,
                M02 =  0.0, M12 =  0.0, M22 =  0.0,
                M03 =  1.0, M13 =  2.0, M23 =  3.0,
            },
        };

        static void DumpSceneFind(int fi, UfbxScene scene)
        {
            int total;
            UfbxNode[] nodes = scene.Nodes ?? Array.Empty<UfbxNode>();

            // -- part 1: every element by its own name, under its own type (hit), under the next type
            // (must miss) and through the typed shorthands, in both the `_len` and `strlen` forms.
            total = 0;
            hash = FnvBasis;
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                UfbxElement[] list = scene.ElementsByType(type) ?? NoElements;
                for (int ei = 0; ei < list.Length; ei++) {
                    UfbxElement elem = list[ei];
                    int other = type + 1 == UfbxEnumCounts.UfbxElementType ? 0 : type + 1;
                    HU32(elem.ElementId);
                    for (int mode = 0; mode < 3; mode++) {
                        int len = PrefixModeLen(elem.Name.Length, mode);
                        HElement(UfbxEvaluate.FindElementLen(scene, (UfbxElementType)type, elem.Name, len));
                        HElement(UfbxEvaluate.FindElementLen(scene, (UfbxElementType)other, elem.Name, len));
                        total += 2;
                    }
                    HElement(UfbxEvaluate.FindNodeLen(scene, elem.Name, elem.Name.Length));
                    HElement(UfbxEvaluate.FindMaterialLen(scene, elem.Name, elem.Name.Length));
                    HElement(UfbxEvaluate.FindAnimStackLen(scene, elem.Name, elem.Name.Length));
                    HElement(UfbxEvaluate.FindNode(scene, elem.Name));
                    total += 4;
                }
            }
            Lines.Add("F " + fi + " 1 " + Hbits(Take()) + " " + total);

            // -- part 2: the miss sweep over every element type, both forms.
            total = 0;
            hash = FnvBasis;
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                for (int n = 0; n < MissNames.Length; n++) {
                    string name = MissNames[n];
                    HElement(UfbxEvaluate.FindElementLen(scene, (UfbxElementType)type, name, name.Length));
                    HElement(UfbxEvaluate.FindElement(scene, (UfbxElementType)type, name));
                    total += 2;
                }
            }
            Lines.Add("F " + fi + " 2 " + Hbits(Take()) + " " + total);

            // -- part 3: connected elements, by prop name and by prop (the `prop->name` path).
            total = 0;
            hash = FnvBasis;
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                UfbxElement[] list = scene.ElementsByType(type) ?? NoElements;
                for (int ei = 0; ei < list.Length; ei++) {
                    UfbxElement elem = list[ei];
                    UfbxProp[] props = elem.Props.Props ?? NoProps;
                    for (int pi = 0; pi < props.Length; pi++) {
                        UfbxProp p = props[pi];
                        HU32(elem.ElementId);
                        HU32((uint)pi);
                        for (int ct = 0; ct < ConnTypes.Length; ct++) {
                            UfbxElementType conn = ConnTypes[ct];
                            HElement(UfbxEvaluate.FindPropElementLen(elem, p.Name, p.Name.Length, conn));
                            HElement(UfbxEvaluate.GetPropElement(elem, p, conn));
                            total += 2;
                        }
                        HElement(UfbxEvaluate.FindPropElement(elem, p.Name, UfbxElementType.Texture));
                        total++;
                    }
                }
            }
            Lines.Add("F " + fi + " 3 " + Hbits(Take()) + " " + total);

            // -- part 4: animated properties of a layer: exact name, one byte short, empty, the
            // strlen form, a foreign element (the `element != key` branch) and the range form.
            total = 0;
            hash = FnvBasis;
            UfbxElement[] allElements = scene.Elements ?? NoElements;
            UfbxElement foreign = allElements.Length > 0 ? allElements[0] : null;
            UfbxAnimLayer[] layers = scene.AnimLayers ?? Array.Empty<UfbxAnimLayer>();
            for (int li = 0; li < layers.Length; li++) {
                UfbxAnimLayer layer = layers[li];
                HU32(layer.ElementId);
                UfbxAnimProp[] aps = layer.AnimProps ?? Array.Empty<UfbxAnimProp>();
                for (int a = 0; a < aps.Length; a++) {
                    UfbxAnimProp ap = aps[a];
                    UfbxElement elem = ap.Element;
                    HU32(elem.ElementId);
                    HU32((uint)a);
                    for (int mode = 0; mode < 3; mode++) {
                        int len = PrefixModeLen(ap.PropName.Length, mode);
                        HAnimProp(UfbxEvaluate.FindAnimPropLen(layer, elem, ap.PropName, len));
                        total++;
                    }
                    HAnimProp(UfbxEvaluate.FindAnimProp(layer, elem, ap.PropName));
                    total++;
                    if (foreign != null && !ReferenceEquals(foreign, elem)) {
                        HAnimProp(UfbxEvaluate.FindAnimPropLen(layer, foreign, ap.PropName, ap.PropName.Length));
                        total++;
                    }
                    HAnimPropList(UfbxEvaluate.FindAnimProps(layer, elem));
                    total++;
                }
                if (foreign != null) {
                    HAnimPropList(UfbxEvaluate.FindAnimProps(layer, foreign));
                    total++;
                }
            }
            Lines.Add("F " + fi + " 4 " + Hbits(Take()) + " " + total);

            // -- part 5: material textures and shader property bindings.
            total = 0;
            hash = FnvBasis;
            UfbxMaterial[] materials = scene.Materials ?? Array.Empty<UfbxMaterial>();
            for (int mi = 0; mi < materials.Length; mi++) {
                UfbxMaterial mat = materials[mi];
                HU32(mat.ElementId);
                UfbxMaterialTexture[] mts = mat.Textures ?? Array.Empty<UfbxMaterialTexture>();
                for (int t = 0; t < mts.Length; t++) {
                    string mp = mts[t].MaterialProp;
                    HElement(UfbxEvaluate.FindPropTextureLen(mat, mp, mp.Length));
                    HElement(UfbxEvaluate.FindPropTextureLen(mat, mp, PrefixModeLen(mp.Length, 1)));
                    total += 2;
                }
                UfbxProp[] mprops = mat.Props.Props ?? NoProps;
                for (int pi = 0; pi < mprops.Length; pi++) {
                    string name = mprops[pi].Name;
                    HElement(UfbxEvaluate.FindPropTextureLen(mat, name, name.Length));
                    total++;
                }
                for (int n = 0; n < MissNames.Length; n++) {
                    HElement(UfbxEvaluate.FindPropTexture(mat, MissNames[n]));
                    total++;
                }
                UfbxShader shader = mat.Shader;
                if (shader != null) {
                    HU32(shader.ElementId);
                    UfbxShaderBinding[] binds = shader.Bindings ?? Array.Empty<UfbxShaderBinding>();
                    for (int b = 0; b < binds.Length; b++) {
                        UfbxShaderPropBinding[] pbs =
                            binds[b].PropBindings ?? Array.Empty<UfbxShaderPropBinding>();
                        for (int pb = 0; pb < pbs.Length; pb++) {
                            string sp = pbs[pb].ShaderProp;
                            UfbxShaderPropBinding[] got =
                                UfbxEvaluate.FindShaderPropBindingsLen(shader, sp, sp.Length);
                            int gc = Len(got);
                            HU64((ulong)gc);
                            if (gc > 0) {
                                HStrLen(got[0].ShaderProp);
                                HStrLen(got[0].MaterialProp);
                                HStrLen(got[gc - 1].ShaderProp);
                            }
                            HStrLen(UfbxEvaluate.FindShaderPropLen(shader, sp, sp.Length));
                            HU64((ulong)Len(UfbxEvaluate.FindShaderPropBindingsLen(shader, sp,
                                PrefixModeLen(sp.Length, 1))));
                            total += 3;
                        }
                    }
                    for (int n = 0; n < MissNames.Length; n++) {
                        HU64((ulong)Len(UfbxEvaluate.FindShaderPropBindings(shader, MissNames[n])));
                        HStrLen(UfbxEvaluate.FindShaderProp(shader, MissNames[n]));
                        total += 2;
                    }
                }
            }
            Lines.Add("F " + fi + " 5 " + Hbits(Take()) + " " + total);

            // -- part 6: shader texture inputs, by their own name and by a miss name.
            total = 0;
            hash = FnvBasis;
            UfbxTexture[] textures = scene.Textures ?? Array.Empty<UfbxTexture>();
            for (int ti = 0; ti < textures.Length; ti++) {
                UfbxShaderTexture st = textures[ti].Shader;
                if (st == null) continue;
                HU32(textures[ti].ElementId);
                UfbxShaderTextureInput[] inputs = st.Inputs ?? Array.Empty<UfbxShaderTextureInput>();
                for (int ii = 0; ii < inputs.Length; ii++) {
                    string name = inputs[ii].Name;
                    UfbxShaderTextureInput got = UfbxEvaluate.FindShaderTextureInputLen(st, name, name.Length);
                    HU32(got != null ? 1u : 0u);
                    if (got != null) {
                        HStrLen(got.Name);
                        HD(got.ValueVec4.X); HD(got.ValueVec4.Y); HD(got.ValueVec4.Z); HD(got.ValueVec4.W);
                        HI64(got.ValueInt);
                        HStrLen(got.ValueStr);
                        HElement(got.Texture);
                        HU32(got.TextureEnabled ? 1u : 0u);
                    }
                    total++;
                }
                HU32(UfbxEvaluate.FindShaderTextureInput(st, MissNames[4]) != null ? 1u : 0u);
                total++;
            }
            Lines.Add("F " + fi + " 6 " + Hbits(Take()) + " " + total);

            // -- part 7: bone poses, for each pose's own nodes and for a foreign one.
            total = 0;
            hash = FnvBasis;
            UfbxPose[] poses = scene.Poses ?? Array.Empty<UfbxPose>();
            for (int pi = 0; pi < poses.Length; pi++) {
                UfbxPose pose = poses[pi];
                HU32(pose.ElementId);
                UfbxBonePose[] bps = pose.BonePoses ?? Array.Empty<UfbxBonePose>();
                for (int b = 0; b < bps.Length; b++) {
                    UfbxBonePose got = UfbxEvaluate.GetBonePose(pose, bps[b].BoneNode);
                    HU32(got != null ? 1u : 0u);
                    if (got != null) {
                        HU32(got.BoneNode.ElementId);
                        HU32(got.BoneNode.TypedId);
                    }
                    total++;
                }
                if (nodes.Length > 0) {
                    UfbxBonePose got = UfbxEvaluate.GetBonePose(pose, nodes[nodes.Length - 1]);
                    HU32(got != null ? 1u : 0u);
                    if (got != null) HU32(got.BoneNode.ElementId);
                    total++;
                }
            }
            Lines.Add("F " + fi + " 7 " + Hbits(Take()) + " " + total);

            // -- part 8: the compatible normal matrix, NULL node first (the identity form).
            total = 0;
            hash = FnvBasis;
            HMatrix(UfbxEvaluate.GetCompatibleMatrixForNormals(null));
            total++;
            for (int ni = 0; ni < nodes.Length; ni++) {
                HMatrix(UfbxEvaluate.GetCompatibleMatrixForNormals(nodes[ni]));
                total++;
            }
            for (int mi = 0; mi < NodeToWorldCases.Length; mi++) {
                for (int qi = 0; qi < GeomRots.Length; qi++) {
                    UfbxNode n = new UfbxNode();
                    n.NodeToWorld = NodeToWorldCases[mi];
                    UfbxTransform gt = new UfbxTransform();
                    gt.Rotation = GeomRots[qi];
                    n.GeometryTransform = gt;
                    HMatrix(UfbxEvaluate.GetCompatibleMatrixForNormals(n));
                    total++;
                }
            }
            Lines.Add("F " + fi + " 8 " + Hbits(Take()) + " " + total);

            // -- part 9: face index lookup (`UfbxTopologyApi.FindFaceIndex`). Every face of every
            // mesh is probed at its first index, at the previous face's end index, at its own end
            // and end+1, at its middle, plus the one-past-mesh and the three above-UINT32_MAX guard
            // probes (`-1` is C's `SIZE_MAX`, rejected by the same `ulong` compare) and NULL mesh.
            total = 0;
            hash = FnvBasis;
            UfbxMesh[] meshes = scene.Meshes ?? Array.Empty<UfbxMesh>();
            for (int mi = 0; mi < meshes.Length; mi++) {
                UfbxMesh mesh = meshes[mi];
                HU32(mesh.ElementId);
                HU32((uint) mesh.NumFaces);
                HU32((uint) mesh.NumIndices);
                UfbxFace[] faces = mesh.Faces ?? Array.Empty<UfbxFace>();
                for (int f = 0; f < faces.Length; f++) {
                    uint begin = faces[f].IndexBegin;
                    uint numIndices = faces[f].NumIndices;
                    uint end = unchecked(begin + numIndices);
                    HU32(UfbxTopologyApi.FindFaceIndex(mesh, begin));
                    HU32(UfbxTopologyApi.FindFaceIndex(mesh, begin + numIndices / 2));
                    HU32(UfbxTopologyApi.FindFaceIndex(mesh, end));
                    HU32(UfbxTopologyApi.FindFaceIndex(mesh, unchecked(end + 1)));
                    HU32(UfbxTopologyApi.FindFaceIndex(mesh, begin > 0 ? begin - 1 : begin));
                    total += 5;
                }
                HU32(UfbxTopologyApi.FindFaceIndex(mesh, mesh.NumIndices));
                HU32(UfbxTopologyApi.FindFaceIndex(mesh, (long) mesh.NumIndices + 1));
                HU32(UfbxTopologyApi.FindFaceIndex(mesh, uint.MaxValue));
                HU32(UfbxTopologyApi.FindFaceIndex(mesh, (long) uint.MaxValue + 1));
                HU32(UfbxTopologyApi.FindFaceIndex(mesh, -1));
                HU32(UfbxTopologyApi.FindFaceIndex(null, 0));
                total += 6;
            }
            Lines.Add("F " + fi + " 9 " + Hbits(Take()) + " " + total);
        }
    }
}
