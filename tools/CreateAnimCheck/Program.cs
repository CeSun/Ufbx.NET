// CreateAnimCheck: differential verification for batch K (the animation-creation chain).
//
// Replays the record stream of tools/create_anim_oracle.c (built with zig, `#include "ufbx.c"`, so
// it runs the original `ufbxi_check_string()` / `ufbxi_push_anim_string()` / the three override
// comparators / `ufbxi_create_anim_imp()` / `ufbx_create_anim()` ABI) through
// src/Ufbx/Parse/CreateAnim.cs + UfbxApi.CreateAnim.
//
// The oracle owns the whole variant table and re-emits the `ufbx_anim_opts` it handed to
// `ufbx_create_anim()` as `I`-family *input* records, so this harness has no mirror of
// `k_variants[]`, `name_for()`, `element_for()` or `pick_element_id()`: it rebuilds the opts from
// the records and calls the port with them. For every oracle line the port renders the *same
// grammar* from its own values and the two lines are compared as text, so a mismatch is reported as
// "oracle line / port line" and the first differing token is the diverging field.
//
// What each record proves
// ----------------------
//   S  the corpus loads the same way (node/element/anim-layer counts and the `scene.anim` presence)
//   U  input only: the oracle's 8 sample times, stored for the `V` sweep (keeps
//      `settings.frames_per_second`/`anim.time_begin` out of the differential as a *rule* while
//      still pinning the exact double bits the sweep uses)
//   I/Ia/Iw/Ip/It  input only: the caller's `ufbx_anim_opts`, verbatim, including invalid UTF-8,
//      embedded NULs, >255-byte names, out-of-range layer ids, both NULL-ish string forms and every
//      numeric shape of the value.x <-> value_int cross-fill
//   A  ufbx_create_anim(): the NULL/non-NULL return, the error type, and the *bytes* of
//      `error.description` and `error.info` -- which is what pins the three `ufbxi_check_err_msg`
//      sites ("Invalid UTF-8", "layer_ids out of bounds", the weight count match), the
//      `ufbxi_fail_err_msg("Duplicate override")` site, and `ufbxi_fix_error_type()`'s
//      "Failed to create anim" default, including ufbxi_fmt_err_info()'s 255-byte clamp and the
//      ufbxi_clean_string_utf8() pass over the truncated tail
//   O  PORTING_NOTES.md #9: the caller's opts object must be bit-identical after the call, so
//      `ac.opts = *opts` really is a value copy
//   L  the `layer_ids[]` resolution: `anim->layers[i] == scene->anim_layers[id]`, compared by
//      typed_id/element_id/name/weight instead of C's pointer
//   W  the `override_layer_weights` copy (length `num_layers`, i.e. the checked count)
//   P  ufbxi_create_anim_imp()'s override pipeline end to end: the name-only unstable sort, the
//      forward-only merge over the byte-sorted `ufbxi_strings[]` (an interned name shows up as a
//      content match against the global entry), the value.x <-> value_int cross-fill, and the final
//      `element_id, _internal_key, strcmp` order
//   T  the transform_override copy + `node_id` sort; tied ids pin the unstable permutation
//   V  end-to-end consumption: `ufbx_evaluate_scene()` with the created anim, hashed by
//      UfbxHashScene (the transcription of test/hash_scene.h the goldens use).
//      `ufbxi_find_prop_override()` binary-searches `prop_overrides`, so a wrong sort order, a
//      mis-interned name or a lost transform override lands here as a different scene hash.
//
// Usage (from C:/Workspace/_analyze_ufbx, so the `data/...` corpus paths resolve):
//   dotnet run --project C:/Workspace/ufbx-cs/tools/CreateAnimCheck -c Release -- \
//       C:/Workspace/ufbx-cs/tools/create_anim_oracle.txt \
//       C:/Workspace/ufbx-cs/tools/create_anim_corpus.txt [-o port.txt]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx;

namespace CreateAnimCheck
{
    static class Program
    {
        const int NumVariants = 32;

        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x00000100000001b3UL;

        static int mismatches;
        static int checkedRecords;
        static int inputRecords;
        static readonly List<string> DiffOracle = new List<string>();
        static readonly List<string> DiffPort = new List<string>();
        static readonly List<string> Lines = new List<string>();

        static List<string> corpus;
        static UfbxScene[] scenes;
        static UfbxError[] loadErrors;
        static readonly Dictionary<int, double[]> timesByFile = new Dictionary<int, double[]>();
        static Ctx[,] cache;
        static VariantInput[,] inputs;

        sealed class PropIn {
            public uint ElementId;
            public string Name;
            public string Str;
            public double X, Y, Z, W;
            public long ValueInt;
        }

        sealed class TransformIn {
            public uint NodeId;
            public double[] T = new double[10];
        }

        sealed class VariantInput {
            public bool NullOpts;
            public bool IgnoreConn;
            public readonly List<uint> LayerIds = new List<uint>();
            public readonly List<double> Weights = new List<double>();
            public readonly List<PropIn> Props = new List<PropIn>();
            public readonly List<TransformIn> Transforms = new List<TransformIn>();

            public UfbxAnimOpts Build()
            {
                UfbxAnimOpts o = new UfbxAnimOpts();
                o.LayerIds = LayerIds.ToArray();
                o.OverrideLayerWeights = Weights.ToArray();
                if (Props.Count > 0) {
                    UfbxPropOverrideDesc[] arr = new UfbxPropOverrideDesc[Props.Count];
                    for (int i = 0; i < arr.Length; i++) {
                        PropIn p = Props[i];
                        UfbxPropOverrideDesc d = new UfbxPropOverrideDesc();
                        d.ElementId = p.ElementId;
                        d.PropName = p.Name;
                        d.ValueStr = p.Str;
                        d.Value = new UfbxVec4(p.X, p.Y, p.Z, p.W);
                        d.ValueInt = p.ValueInt;
                        arr[i] = d;
                    }
                    o.PropOverrides = arr;
                }
                if (Transforms.Count > 0) {
                    UfbxTransformOverride[] arr = new UfbxTransformOverride[Transforms.Count];
                    for (int i = 0; i < arr.Length; i++) {
                        TransformIn src = Transforms[i];
                        UfbxTransformOverride t = new UfbxTransformOverride();
                        t.NodeId = src.NodeId;
                        UfbxTransform tr = new UfbxTransform();
                        tr.Translation = new UfbxVec3(src.T[0], src.T[1], src.T[2]);
                        tr.Rotation = new UfbxQuat(src.T[3], src.T[4], src.T[5], src.T[6]);
                        tr.Scale = new UfbxVec3(src.T[7], src.T[8], src.T[9]);
                        t.Transform = tr;
                        arr[i] = t;
                    }
                    o.TransformOverrides = arr;
                }
                o.IgnoreConnections = IgnoreConn;
                return o;
            }
        }

        sealed class Ctx {
            public UfbxAnim Anim;
            public UfbxError Error;
            public UfbxAnimOpts CallerOpts;   // null for the NULL-opts variant
            public ulong OptsBefore;
            public ulong OptsAfter;
        }

        static int Main(string[] args)
        {
            if (args.Length < 2) {
                Console.Error.WriteLine("usage: CreateAnimCheck <create_anim_oracle.txt> "
                    + "<create_anim_corpus.txt> [-o port.txt]");
                return 2;
            }
            string oraclePath = args[0];
            string corpusPath = args[1];
            string portDump = null;
            for (int i = 2; i < args.Length; i++) {
                if (args[i] == "-o" && i + 1 < args.Length) portDump = args[++i];
            }
            if (!File.Exists(oraclePath)) {
                Console.Error.WriteLine("oracle not found: " + oraclePath);
                return 2;
            }

            corpus = new List<string>();
            foreach (string raw in File.ReadLines(corpusPath)) {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                corpus.Add(line.Trim().Replace('\\', '/'));
            }
            scenes = new UfbxScene[corpus.Count];
            loadErrors = new UfbxError[corpus.Count];
            cache = new Ctx[corpus.Count, NumVariants];
            inputs = new VariantInput[corpus.Count, NumVariants];

            foreach (string raw in File.ReadAllLines(oraclePath)) {
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0) continue;
                string[] t = line.Split(' ');
                int fi = int.Parse(t[1], CultureInfo.InvariantCulture);
                string port;
                try {
                    port = Render(fi, t);
                } catch (Exception e) {
                    port = "! " + e.GetType().Name + ": " + e.Message;
                }
                Lines.Add(port);

                switch (t[0]) {
                    case "U":
                    case "I":
                    case "Ia":
                    case "Iw":
                    case "Ip":
                    case "It":
                        // Input only: the oracle's own opts and time table, consumed by `A`/`V`.
                        inputRecords++;
                        continue;
                }
                checkedRecords++;
                if (port == line) continue;
                mismatches++;
                if (DiffOracle.Count < 25) {
                    DiffOracle.Add(Truncate(line));
                    DiffPort.Add(Truncate(port));
                }
            }

            if (portDump != null) File.WriteAllLines(portDump, Lines);

            for (int i = 0; i < DiffOracle.Count; i++) {
                Console.WriteLine("MISMATCH #" + (i + 1));
                Console.WriteLine("  oracle: " + DiffOracle[i]);
                Console.WriteLine("  port  : " + DiffPort[i]);
            }

            Console.WriteLine("CreateAnimCheck: records " + checkedRecords + " input " + inputRecords
                + " mismatches " + mismatches);
            Console.WriteLine(mismatches == 0 ? "ALL MATCH" : "FAIL");
            return mismatches == 0 ? 0 : 1;
        }

        static string Truncate(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "...";

        static string Render(int fi, string[] t)
        {
            switch (t[0]) {
                case "S": return RenderS(fi, t);
                case "U": return StoreTimes(fi, t);
                case "I": return StoreInput(fi, t);
                case "Ia": return AddLayerId(fi, t);
                case "Iw": return AddWeight(fi, t);
                case "Ip": return AddProp(fi, t);
                case "It": return AddTransform(fi, t);
                case "A": return RenderA(fi, t);
                case "O": return RenderO(fi, t);
                case "L": return RenderL(fi, t);
                case "W": return RenderW(fi, t);
                case "P": return RenderP(fi, t);
                case "T": return RenderT(fi, t);
                case "V": return RenderV(fi, t);
                default: throw new Exception("unknown record " + t[0]);
            }
        }

        // ------------------------------------------------------------------
        // Corpus and input records
        // ------------------------------------------------------------------

        static UfbxScene EnsureScene(int fi)
        {
            if (fi >= corpus.Count) return null;
            if (scenes[fi] == null && loadErrors[fi] == null) {
                UfbxLoadOpts opts = new UfbxLoadOpts();
                opts.LoadExternalFiles = true;
                opts.IgnoreMissingExternalFiles = true;
                opts.EvaluateCaches = true;
                opts.EvaluateSkinning = true;
                opts.TargetAxes = UfbxCoordinateAxes.RightHandedYUp;
                opts.TargetUnitMeters = 1.0;
                UfbxError err = new UfbxError();
                scenes[fi] = UfbxApi.LoadFile(corpus[fi], opts, err);
                loadErrors[fi] = err;
            }
            return scenes[fi];
        }

        static string StoreTimes(int fi, string[] t)
        {
            int ti = int.Parse(t[2], CultureInfo.InvariantCulture);
            if (!timesByFile.TryGetValue(fi, out double[] times)) {
                times = new double[8];
                timesByFile[fi] = times;
            }
            times[ti] = Bits(t[3]);
            return "U " + fi + " " + ti + " " + t[3];
        }

        static VariantInput Input(int fi, int vi)
        {
            VariantInput v = inputs[fi, vi];
            if (v == null) throw new Exception("input record for variant " + vi + " of file " + fi
                + " before its `I` header");
            return v;
        }

        static string StoreInput(int fi, string[] t)
        {
            int vi = int.Parse(t[2], CultureInfo.InvariantCulture);
            VariantInput v = new VariantInput();
            v.NullOpts = t[3] != "0";
            v.IgnoreConn = t[4] != "0";
            inputs[fi, vi] = v;
            return "I " + fi + " " + vi + " " + t[3] + " " + t[4] + " " + t[5] + " " + t[6] + " "
                + t[7] + " " + t[8];
        }

        static string AddLayerId(int fi, string[] t)
        {
            int vi = int.Parse(t[2], CultureInfo.InvariantCulture);
            Input(fi, vi).LayerIds.Add(uint.Parse(t[4], CultureInfo.InvariantCulture));
            return "Ia " + fi + " " + vi + " " + t[3] + " " + t[4];
        }

        static string AddWeight(int fi, string[] t)
        {
            int vi = int.Parse(t[2], CultureInfo.InvariantCulture);
            Input(fi, vi).Weights.Add(Bits(t[4]));
            return "Iw " + fi + " " + vi + " " + t[3] + " " + t[4];
        }

        static string AddProp(int fi, string[] t)
        {
            int vi = int.Parse(t[2], CultureInfo.InvariantCulture);
            PropIn p = new PropIn();
            p.ElementId = uint.Parse(t[4], CultureInfo.InvariantCulture);
            p.Name = ParseStr(t[5], t[6]);
            p.Str = ParseStr(t[7], t[8]);
            p.X = Bits(t[9]);
            p.Y = Bits(t[10]);
            p.Z = Bits(t[11]);
            p.W = Bits(t[12]);
            p.ValueInt = long.Parse(t[13], CultureInfo.InvariantCulture);
            Input(fi, vi).Props.Add(p);
            return "Ip " + fi + " " + vi + " " + t[3] + " " + t[4] + " " + t[5] + " " + t[6] + " "
                + t[7] + " " + t[8] + " " + t[9] + " " + t[10] + " " + t[11] + " " + t[12] + " " + t[13];
        }

        static string AddTransform(int fi, string[] t)
        {
            int vi = int.Parse(t[2], CultureInfo.InvariantCulture);
            TransformIn tr = new TransformIn();
            tr.NodeId = uint.Parse(t[4], CultureInfo.InvariantCulture);
            for (int i = 0; i < 10; i++) tr.T[i] = Bits(t[5 + i]);
            Input(fi, vi).Transforms.Add(tr);
            return "It " + fi + " " + vi + " " + t[3] + " " + t[4] + " " + t[5] + " " + t[6] + " "
                + t[7] + " " + t[8] + " " + t[9] + " " + t[10] + " " + t[11] + " " + t[12] + " "
                + t[13] + " " + t[14];
        }

        // ------------------------------------------------------------------
        // The call
        // ------------------------------------------------------------------

        static Ctx EnsureVariant(int fi, int vi)
        {
            Ctx c = cache[fi, vi];
            if (c != null) return c;
            UfbxScene scene = EnsureScene(fi);
            if (scene == null) return null;

            VariantInput v = Input(fi, vi);
            UfbxAnimOpts opts = v.Build();
            c = new Ctx();
            c.OptsBefore = HashOpts(opts);
            UfbxError err = new UfbxError();
            c.Anim = UfbxApi.CreateAnim(scene, v.NullOpts ? null : opts, err);
            c.Error = err;
            c.CallerOpts = v.NullOpts ? null : opts;
            c.OptsAfter = HashOpts(opts);
            cache[fi, vi] = c;
            return c;
        }

        static int VI(string[] t) => int.Parse(t[2], CultureInfo.InvariantCulture);

        // S <fi> <nodes> <elements> <animLayers> <animPresent>
        static string RenderS(int fi, string[] t)
        {
            UfbxScene scene = EnsureScene(fi);
            if (scene == null) return "S " + fi + " 0 0 0 0 FAIL";
            return "S " + fi + " " + scene.Nodes.Length + " " + scene.Elements.Length + " "
                + (scene.AnimLayers == null ? 0 : scene.AnimLayers.Length) + " "
                + (scene.Anim != null ? 1 : 0);
        }

        // A <fi> <vi> <ok> <errtype> <desclen> <deschex> <infolen> <infohex>
        //   <layers> <weights> <props> <transforms> <custom> <ignoreconn>
        static string RenderA(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("A ").Append(fi).Append(' ').Append(vi);
            if (c == null || c.Anim == null) {
                sb.Append(" 0 ");
                sb.Append(c == null ? 0 : (int) c.Error.Type);
                PStr(sb, c == null ? "" : c.Error.Description);
                PInfo(sb, c == null ? 0 : c.Error.InfoLength, c == null ? "" : c.Error.Info);
                sb.Append(" 0 0 0 0 0 0");
                return sb.ToString();
            }
            UfbxAnim anim = c.Anim;
            sb.Append(" 1 ").Append((int) c.Error.Type);
            PStr(sb, c.Error.Description);
            PInfo(sb, c.Error.InfoLength, c.Error.Info);
            sb.Append(' ').Append(Len(anim.Layers));
            sb.Append(' ').Append(Len(anim.OverrideLayerWeights));
            sb.Append(' ').Append(Len(anim.PropOverrides));
            sb.Append(' ').Append(Len(anim.TransformOverrides));
            sb.Append(anim.Custom ? " 1" : " 0");
            sb.Append(anim.IgnoreConnections ? " 1" : " 0");
            return sb.ToString();
        }

        // O <fi> <vi> <before> <after> <same>
        static string RenderO(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("O ").Append(fi).Append(' ').Append(vi);
            if (c == null) { Z(sb, FnvBasis); Z(sb, FnvBasis); sb.Append(" 1"); return sb.ToString(); }
            Z(sb, c.OptsBefore);
            Z(sb, c.OptsAfter);
            sb.Append(c.OptsBefore == c.OptsAfter ? " 1" : " 0");
            return sb.ToString();
        }

        // L <fi> <vi> <ix> <typedId> <elementId> <nameLen> <nameHex> <weight>
        static string RenderL(int fi, string[] t)
        {
            int vi = VI(t);
            int ix = int.Parse(t[3], CultureInfo.InvariantCulture);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("L ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ix);
            UfbxAnimLayer layer = c == null || c.Anim == null || c.Anim.Layers == null
                ? null : c.Anim.Layers[ix];
            if (layer == null) {
                sb.Append(" -1 0 0 -");
                H(sb, 0.0);
                return sb.ToString();
            }
            sb.Append(' ').Append(layer.TypedId).Append(' ').Append(layer.ElementId);
            PStr(sb, layer.Name);
            H(sb, layer.Weight);
            return sb.ToString();
        }

        // W <fi> <vi> <ix> <weight>
        static string RenderW(int fi, string[] t)
        {
            int vi = VI(t);
            int ix = int.Parse(t[3], CultureInfo.InvariantCulture);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("W ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ix);
            H(sb, c == null || c.Anim == null || c.Anim.OverrideLayerWeights == null
                ? 0.0 : c.Anim.OverrideLayerWeights[ix]);
            return sb.ToString();
        }

        // P <fi> <vi> <ix> <elementId> <internalKey> <nameLen> <nameHex>
        //   <x> <y> <z> <w> <valueInt> <strLen> <strHex>
        static string RenderP(int fi, string[] t)
        {
            int vi = VI(t);
            int ix = int.Parse(t[3], CultureInfo.InvariantCulture);
            Ctx c = EnsureVariant(fi, vi);
            UfbxPropOverride[] all = c == null ? null : c.Anim == null ? null : c.Anim.PropOverrides;
            UfbxPropOverride o = all == null || ix >= all.Length ? null : all[ix];
            StringBuilder sb = new StringBuilder();
            sb.Append("P ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ix);
            if (o == null) { sb.Append(" 0 0 0 - 0000000000000000 0000000000000000 0000000000000000 0000000000000000 0 0 -"); return sb.ToString(); }
            sb.Append(' ').Append(o.ElementId).Append(' ').Append(o.InternalKey);
            PStr(sb, o.PropName);
            H(sb, o.Value.X); H(sb, o.Value.Y); H(sb, o.Value.Z); H(sb, o.Value.W);
            sb.Append(' ').Append(o.ValueInt);
            PStr(sb, o.ValueStr);
            return sb.ToString();
        }

        // T <fi> <vi> <ix> <nodeId> <10 transform halves>
        static string RenderT(int fi, string[] t)
        {
            int vi = VI(t);
            int ix = int.Parse(t[3], CultureInfo.InvariantCulture);
            Ctx c = EnsureVariant(fi, vi);
            UfbxTransformOverride[] all = c == null ? null : c.Anim == null ? null : c.Anim.TransformOverrides;
            UfbxTransformOverride o = all == null || ix >= all.Length ? null : all[ix];
            StringBuilder sb = new StringBuilder();
            sb.Append("T ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ix);
            if (o == null) { sb.Append(" 0"); for (int i = 0; i < 10; i++) H(sb, 0.0); return sb.ToString(); }
            sb.Append(' ').Append(o.NodeId);
            H(sb, o.Transform.Translation.X); H(sb, o.Transform.Translation.Y);
            H(sb, o.Transform.Translation.Z);
            H(sb, o.Transform.Rotation.X); H(sb, o.Transform.Rotation.Y);
            H(sb, o.Transform.Rotation.Z); H(sb, o.Transform.Rotation.W);
            H(sb, o.Transform.Scale.X); H(sb, o.Transform.Scale.Y); H(sb, o.Transform.Scale.Z);
            return sb.ToString();
        }

        // V <fi> <vi> <ti> <hash>
        static string RenderV(int fi, string[] t)
        {
            int vi = VI(t);
            int ti = int.Parse(t[3], CultureInfo.InvariantCulture);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("V ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ti);
            if (c == null || c.Anim == null) { sb.Append(" NONE"); return sb.ToString(); }
            UfbxScene scene = EnsureScene(fi);
            // NULL evaluate opts, mirroring the oracle: `ufbxi_evaluate_skinning()` is an S4c body
            // the port has not reached, and the opts have no other effect on these records.
            UfbxError err = new UfbxError();
            UfbxScene state = UfbxApi.EvaluateScene(scene, c.Anim, timesByFile[fi][ti], null, err);
            if (state == null) { sb.Append(" FAIL"); return sb.ToString(); }
            Z(sb, UfbxHashScene.HashScene(state));
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // FNV over the caller's opts (mirror of hash_opts() in the oracle)
        // ------------------------------------------------------------------

        static ulong HashOpts(UfbxAnimOpts o)
        {
            ulong h = FnvBasis;
            uint[] ids = o.LayerIds ?? Array.Empty<uint>();
            h = HU64(h, (ulong) ids.Length);
            for (int i = 0; i < ids.Length; i++) h = HU32(h, ids[i]);
            double[] ws = o.OverrideLayerWeights ?? Array.Empty<double>();
            h = HU64(h, (ulong) ws.Length);
            for (int i = 0; i < ws.Length; i++) h = HD(h, ws[i]);
            UfbxPropOverrideDesc[] ps = o.PropOverrides ?? Array.Empty<UfbxPropOverrideDesc>();
            h = HU64(h, (ulong) ps.Length);
            for (int i = 0; i < ps.Length; i++) {
                UfbxPropOverrideDesc d = ps[i];
                h = HU32(h, d.ElementId);
                byte[] name = UfbxiRawStr.ToBytes(d.PropName ?? "");
                h = HU64(h, (ulong) name.Length);
                h = HBytes(h, name);
                h = HD(h, d.Value.X); h = HD(h, d.Value.Y); h = HD(h, d.Value.Z); h = HD(h, d.Value.W);
                h = HI64(h, d.ValueInt);
                byte[] str = UfbxiRawStr.ToBytes(d.ValueStr ?? "");
                h = HU64(h, (ulong) str.Length);
                h = HBytes(h, str);
            }
            UfbxTransformOverride[] ts = o.TransformOverrides ?? Array.Empty<UfbxTransformOverride>();
            h = HU64(h, (ulong) ts.Length);
            for (int i = 0; i < ts.Length; i++) {
                UfbxTransformOverride d = ts[i];
                h = HU32(h, d.NodeId);
                h = HD(h, d.Transform.Translation.X); h = HD(h, d.Transform.Translation.Y);
                h = HD(h, d.Transform.Translation.Z);
                h = HD(h, d.Transform.Rotation.X); h = HD(h, d.Transform.Rotation.Y);
                h = HD(h, d.Transform.Rotation.Z); h = HD(h, d.Transform.Rotation.W);
                h = HD(h, d.Transform.Scale.X); h = HD(h, d.Transform.Scale.Y);
                h = HD(h, d.Transform.Scale.Z);
            }
            return HU32(h, o.IgnoreConnections ? 1u : 0u);
        }

        static ulong HBytes(ulong h, byte[] b) {
            for (int i = 0; i < b.Length; i++) h = (h ^ b[i]) * FnvPrime;
            return h;
        }
        static ulong HU64(ulong h, ulong v) { for (int k = 0; k < 8; k++) h = (h ^ ((v >> (k * 8)) & 0xff)) * FnvPrime; return h; }
        static ulong HI64(ulong h, long v) => HU64(h, unchecked((ulong) v));
        static ulong HD(ulong h, double d) => HU64(h, unchecked((ulong) BitConverter.DoubleToInt64Bits(d)));
        static ulong HU32(ulong h, uint v) { for (int k = 0; k < 4; k++) h = (h ^ ((v >> (k * 8)) & 0xff)) * FnvPrime; return h; }

        // ------------------------------------------------------------------
        // Formatting
        // ------------------------------------------------------------------

        static int Len(Array a) => a == null ? 0 : a.Length;

        // ` <16 hex>` -- the oracle's pd(), which always leads with the separator.
        static void H(StringBuilder sb, double d) {
            sb.Append(' ').Append(((ulong) BitConverter.DoubleToInt64Bits(d)).ToString("x16", CultureInfo.InvariantCulture));
        }

        static void Z(StringBuilder sb, ulong u) {
            sb.Append(' ').Append(u.ToString("x16", CultureInfo.InvariantCulture));
        }

        // ` <length> <hex>`, an empty byte run as the single token `-` -- the oracle's pstr().
        static void PStr(StringBuilder sb, string s) {
            byte[] b = UfbxiRawStr.ToBytes(s ?? "");
            sb.Append(' ').Append(b.Length).Append(' ');
            if (b.Length == 0) { sb.Append('-'); return; }
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        // ` <info_length> <hex>`: `Info` is a NUL-terminated buffer in C, so it is hashed as the
        // first `InfoLength` bytes only.
        static void PInfo(StringBuilder sb, int length, string info) {
            sb.Append(' ').Append(length).Append(' ');
            if (length == 0) { sb.Append('-'); return; }
            byte[] b = UfbxiRawStr.ToBytes(info ?? "");
            for (int i = 0; i < length && i < b.Length; i++) {
                sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        static string ParseStr(string lenToken, string hexToken) {
            if (hexToken == "-") return "";
            int len = int.Parse(lenToken, CultureInfo.InvariantCulture);
            byte[] b = new byte[len];
            for (int i = 0; i < len; i++) {
                b[i] = byte.Parse(hexToken.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return UfbxiRawStr.FromBytes(b, 0, len);
        }

        static double Bits(string hex) =>
            BitConverter.Int64BitsToDouble(unchecked((long) Convert.ToUInt64(hex, 16)));
    }
}
