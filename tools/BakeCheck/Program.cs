// BakeCheck: differential verification for batch J (the animation-baking chain).
//
// Replays the record stream of tools/bake_oracle.c (built with zig, `#include "ufbx.c"`, so it
// runs the original `ufbxi_bake_*` chain and the `ufbx_bake_anim()` / `ufbx_find_baked_*` /
// `ufbx_evaluate_baked_*` ABI) through src/Ufbx.NET/Parse/Bake.cs + src/Ufbx.NET/Api/UfbxBakeApi.cs.
// For every oracle line the port renders the *same grammar* from its own values and the two lines
// are compared as text, so a mismatch is reported as "oracle line / port line" and the first
// differing token is the diverging field. Each (file, variant) is baked lazily the first time a
// record mentions it, so both sides walk the same 30 `ufbx_bake_opts` variants in the same order
// (MakeOpts() below is a 1:1 mirror of make_variant() in the oracle).
//
// What each record proves
// ----------------------
//   S  the corpus loads the same way (counts + the `scene.anim` presence that gates every bake),
//      including under the non-default `ufbx_inherit_mode_handling` modes of the last 14 corpus
//      entries -- modes 1/2 are the only ways to get `is_scale_helper` nodes at all, which is what
//      makes the helper half of ufbxi_bake_node_imp() (sibling re-bake stack, scale_helper keys,
//      inherit_scale_node compensation) reachable for the N/Q/F records below
//   B  ufbx_bake_anim(): the `!anim` resolution branch, the return/error pair, the resolved
//      anim's identity (layer/weight/override counts), and every scalar of ufbx_baked_anim
//      (nodes/elements counts, playback begin/end/duration, key_time_min/max) -- i.e.
//      ufbxi_bake_anim()'s time bookkeeping (ufbx.c:27695-27710)
//   O  PORTING_NOTES.md #9: the caller's opts object must be byte-identical after the call, so
//      `bc->opts = *user_opts` really is a value copy and the five effective defaults
//      (27717-27721) never land on the caller's struct
//   T  input only: the oracle's 16-entry sample time table, stored for the Q/R sweeps (this keeps
//      ufbx_nextafter() out of the differential as a *rule* while still pinning the exact double
//      bits the sweeps use)
//   N  ufbxi_bake_node_imp()/ufbxi_finalize_bake_times()/ufbxi_bake_postprocess_{vec3,quat}():
//      per-node identity, the three constant-channel flags, the three key counts, FNV over each
//      whole key list (every time, value component and flag bit) and the first key explicitly
//   Q  ufbx_evaluate_baked_vec3()/ufbx_evaluate_baked_quat() over the node's own key times, their
//      nextafter() neighbours and the global table -- the exactly-at-key and adjacent-double
//      probes are what reach the `time == prev->time`, `prev[-1].time == time` and
//      STEP_LEFT/STEP_RIGHT branches
//   E/P  ufbxi_bake_element()/ufbxi_bake_anim_prop(): element id, prop count, per-prop name (FNV
//      over the raw bytes), constant_value, key count + full key hash + first key
//   R  ufbx_evaluate_baked_vec3() over a baked prop's keys
//   F  ufbx_find_baked_node()/[_by_typed_id] and ufbx_find_baked_element()/[_by_element_id]
//      swept over every scene node and element (hits and misses)
//
// Usage (from C:/Workspace/_analyze_ufbx, so the `data/...` corpus paths resolve):
//   dotnet run --project C:/Workspace/ufbx-cs/tools/BakeCheck -c Release -- \
//       C:/Workspace/ufbx-cs/tools/bake_oracle.txt C:/Workspace/ufbx-cs/tools/bake_corpus.txt
// `-o <path>` writes the port's full record stream (like S4bCheck) so the two files can be
// compared with diff/cmp after a run.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace BakeCheck
{
    static class Program
    {
        const int NumVariants = 30;
        const int NumTimes = 16;
        const int MaxSweepTimes = NumTimes + 4 * 3 * 3;

        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x100000001b3UL;

        static ulong hash = FnvBasis;
        static int mismatches;
        static int checkedRecords;
        static int inputRecords;
        static readonly List<string> DiffOracle = new List<string>();
        static readonly List<string> DiffPort = new List<string>();

        static List<string> corpus;
        static List<int> corpusModes;
        static UfbxScene[] scenes;
        static UfbxError[] loadErrors;
        static Ctx[,] cache;
        static readonly List<string> Lines = new List<string>();

        sealed class Ctx
        {
            public UfbxBakedAnim Bake;
            public UfbxError Error;
            public UfbxBakeOpts CallerOpts;   // null for the NULL-opts variant
            public bool ExplicitAnim;
            public double[] Times;
        }

        static int Main(string[] args)
        {
            if (args.Length < 2) {
                Console.Error.WriteLine("usage: BakeCheck <bake_oracle.txt> <bake_corpus.txt> [-o port.txt]");
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
            corpusModes = new List<int>();
            foreach (string raw in File.ReadLines(corpusPath)) {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                // "<mode> <path>": mode is the `ufbx_inherit_mode_handling` the scene is loaded
                // with, mirroring the oracle's argv pairs (see RenderS / EnsureScene).
                int sp = line.IndexOf(' ');
                if (sp < 0) throw new Exception("corpus line without mode: " + line);
                corpusModes.Add(int.Parse(line.Substring(0, sp), CultureInfo.InvariantCulture));
                corpus.Add(line.Substring(sp + 1).Trim().Replace('\\', '/'));
            }
            if (corpusModes.Count != corpus.Count) throw new Exception("corpus/mode desync");
            scenes = new UfbxScene[corpus.Count];
            loadErrors = new UfbxError[corpus.Count];
            cache = new Ctx[corpus.Count, NumVariants];

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

                if (t[0] == "T") {
                    // Input only: the time table is C's, consumed by the Q/R sweeps.
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

            Console.WriteLine("BakeCheck: records " + checkedRecords + " input(T) " + inputRecords
                + " mismatches " + mismatches);
            Console.WriteLine(mismatches == 0 ? "ALL MATCH" : "FAIL");
            return mismatches == 0 ? 0 : 1;
        }

        static string Truncate(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "...";

        static string Render(int fi, string[] t)
        {
            switch (t[0]) {
                case "S": return RenderS(fi, t);
                case "B": return RenderB(fi, t);
                case "O": return RenderO(fi, t);
                case "T": StoreTimes(fi, t); return "T " + fi + " " + t[2] + " " + t[3] + " " + t[4];
                case "N": return RenderN(fi, t);
                case "Q": return RenderQ(fi, t);
                case "E": return RenderE(fi, t);
                case "P": return RenderP(fi, t);
                case "R": return RenderR(fi, t);
                case "F": return RenderF(fi, t);
                default: throw new Exception("unknown record " + t[0]);
            }
        }

        // ------------------------------------------------------------------
        // Variant table (mirror of make_variant() in tools/bake_oracle.c)
        // ------------------------------------------------------------------

        static UfbxBakeOpts MakeOpts(int vi, out bool nullOpts, out bool explicitAnim)
        {
            nullOpts = vi == 0;
            explicitAnim = (vi % 2) == 1;
            UfbxBakeOpts o = new UfbxBakeOpts();
            switch (vi) {
                case 0:
                case 1:
                    break;
                case 2: o.ResampleRate = 5.0; break;
                case 3: o.ResampleRate = 60.0; break;
                case 4: o.ResampleRate = 120.0; o.MinimumSampleRate = 100.0; break;
                case 5: o.MinimumSampleRate = 1.0; break;
                case 6: o.MaximumSampleRate = 2.0; break;
                case 7: o.MaximumSampleRate = 24.0; o.ResampleRate = 100.0; break;
                case 8: o.TrimStartTime = true; break;
                case 9: o.SkipNodeTransforms = true; break;
                case 10: o.BakeTransformProps = true; break;
                case 11: o.NoResampleRotation = true; break;
                case 12: o.IgnoreLayerWeightAnimation = true; break;
                case 13: o.MaxKeyframeSegments = 1; break;
                case 14: o.MaxKeyframeSegments = 3; break;
                case 15: o.MaxKeyframeSegments = 1000; break;
                case 16: o.StepHandling = UfbxBakeStepHandling.Default; break;
                case 17: o.StepHandling = UfbxBakeStepHandling.CustomDuration; break;
                case 18: o.StepHandling = UfbxBakeStepHandling.CustomDuration;
                    o.StepCustomDuration = 0.1; break;
                case 19: o.StepHandling = UfbxBakeStepHandling.CustomDuration;
                    o.StepCustomDuration = 0.05; o.StepCustomEpsilon = 0.5; break;
                case 20: o.StepHandling = UfbxBakeStepHandling.IdenticalTime; break;
                case 21: o.StepHandling = UfbxBakeStepHandling.AdjacentDouble; break;
                case 22: o.StepHandling = UfbxBakeStepHandling.Ignore; break;
                case 23: o.KeyReductionEnabled = true; break;
                case 24: o.KeyReductionEnabled = true; o.KeyReductionRotation = true; break;
                case 25: o.KeyReductionEnabled = true; o.KeyReductionThreshold = 0.1;
                    o.KeyReductionPasses = 8; break;
                case 26: o.KeyReductionEnabled = true; o.KeyReductionThreshold = -1.0; break;
                case 27: o.KeyReductionEnabled = true; o.KeyReductionPasses = 1; break;
                case 28: o.EvaluateFlags = 1u; break;   // UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION
                case 29: o.TrimStartTime = true; o.ResampleRate = 15.0; o.MaximumSampleRate = 10.0;
                    o.StepHandling = UfbxBakeStepHandling.IdenticalTime;
                    o.KeyReductionEnabled = true; o.KeyReductionRotation = true;
                    o.KeyReductionPasses = 2;
                    o.NoResampleRotation = true; o.BakeTransformProps = true;
                    o.MaxKeyframeSegments = 5; o.EvaluateFlags = 1u;
                    break;
            }
            return o;
        }

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
                opts.InheritModeHandling = (UfbxInheritModeHandling)corpusModes[fi];
                UfbxError err = new UfbxError();
                scenes[fi] = UfbxApi.LoadFile(corpus[fi], opts, err);
                loadErrors[fi] = err;
            }
            return scenes[fi];
        }

        static Ctx EnsureVariant(int fi, int vi)
        {
            Ctx c = cache[fi, vi];
            if (c != null) return c;
            UfbxScene scene = EnsureScene(fi);
            // The oracle skips every variant of a scene without `anim`: C dereferences the
            // resolved anim in ufbxi_bake_anim_imp (ufbx.c:27723).
            if (scene == null || scene.Anim == null) return null;

            UfbxBakeOpts opts = MakeOpts(vi, out bool nullOpts, out bool explicitAnim);
            UfbxAnim anim = explicitAnim ? scene.Anim : null;
            UfbxError err = new UfbxError();
            UfbxBakedAnim bake = UfbxBakeApi.BakeAnim(scene, anim, nullOpts ? null : opts, err);
            c = new Ctx();
            c.Bake = bake;
            c.Error = err;
            c.CallerOpts = nullOpts ? null : opts;
            c.ExplicitAnim = explicitAnim;
            cache[fi, vi] = c;
            return c;
        }

        static int VI(string[] t) => int.Parse(t[2], CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Records
        // ------------------------------------------------------------------

        // S <fi> <mode> <ok> <nodes> <elements> <meshes> <has_anim>
        static string RenderS(int fi, string[] t)
        {
            int mode = int.Parse(t[2], CultureInfo.InvariantCulture);
            if (mode != corpusModes[fi]) throw new Exception("corpus mode " + corpusModes[fi]
                + " != oracle mode " + mode + " for file " + fi);
            UfbxScene scene = EnsureScene(fi);
            if (scene == null) return "S " + fi + " " + mode + " 0 0 0 0 0 " + (int)loadErrors[fi].Type;
            return "S " + fi + " " + mode + " 1 " + scene.Nodes.Length + " " + scene.Elements.Length + " "
                + scene.Meshes.Length + " " + (scene.Anim != null ? 1 : 0);
        }

        // B <fi> <vi> <animArg> <ret> <errtype> <layers> <weights> <propOverr> <transOverr>
        //   <tb> <te> <numNodes> <numElems> <ptb> <pte> <pd> <ktmin> <ktmax>
        static string RenderB(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            UfbxAnim anim = EnsureScene(fi).Anim;
            UfbxBakedAnim bake = c != null ? c.Bake : null;
            StringBuilder sb = new StringBuilder();
            sb.Append("B ").Append(fi).Append(' ').Append(vi).Append(' ')
                .Append(c != null && c.ExplicitAnim ? 1 : 0).Append(' ')
                .Append(bake != null ? 1 : 0).Append(' ')
                .Append(c != null ? (int)c.Error.Type : 0).Append(' ');
            sb.Append(anim.Layers != null ? anim.Layers.Length : 0).Append(' ');
            sb.Append(anim.OverrideLayerWeights != null ? anim.OverrideLayerWeights.Length : 0).Append(' ');
            sb.Append(anim.PropOverrides != null ? anim.PropOverrides.Length : 0).Append(' ');
            sb.Append(anim.TransformOverrides != null ? anim.TransformOverrides.Length : 0).Append(' ');
            H(sb, anim.TimeBegin); sb.Append(' '); H(sb, anim.TimeEnd); sb.Append(' ');
            sb.Append(bake != null && bake.Nodes != null ? bake.Nodes.Length : 0).Append(' ');
            sb.Append(bake != null && bake.Elements != null ? bake.Elements.Length : 0).Append(' ');
            H(sb, bake != null ? bake.PlaybackTimeBegin : 0.0); sb.Append(' ');
            H(sb, bake != null ? bake.PlaybackTimeEnd : 0.0); sb.Append(' ');
            H(sb, bake != null ? bake.PlaybackDuration : 0.0); sb.Append(' ');
            H(sb, bake != null ? bake.KeyTimeMin : 0.0); sb.Append(' ');
            H(sb, bake != null ? bake.KeyTimeMax : 0.0);
            return sb.ToString();
        }

        // O <fi> <vi> <nullOpts> [17 fields] -- the caller's opts must be untouched.
        static string RenderO(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            UfbxBakeOpts o = c != null ? c.CallerOpts : null;
            StringBuilder sb = new StringBuilder();
            sb.Append("O ").Append(fi).Append(' ').Append(vi).Append(' ').Append(o == null ? 1 : 0);
            if (o == null) return sb.ToString();
            sb.Append(' ').Append(o.TrimStartTime ? 1 : 0);
            sb.Append(' '); H(sb, o.ResampleRate);
            sb.Append(' '); H(sb, o.MinimumSampleRate);
            sb.Append(' '); H(sb, o.MaximumSampleRate);
            sb.Append(' ').Append(o.BakeTransformProps ? 1 : 0);
            sb.Append(' ').Append(o.SkipNodeTransforms ? 1 : 0);
            sb.Append(' ').Append(o.NoResampleRotation ? 1 : 0);
            sb.Append(' ').Append(o.IgnoreLayerWeightAnimation ? 1 : 0);
            sb.Append(' ').Append(o.MaxKeyframeSegments);
            sb.Append(' ').Append((int)o.StepHandling);
            sb.Append(' '); H(sb, o.StepCustomDuration);
            sb.Append(' '); H(sb, o.StepCustomEpsilon);
            sb.Append(' ').Append(o.EvaluateFlags);
            sb.Append(' ').Append(o.KeyReductionEnabled ? 1 : 0);
            sb.Append(' ').Append(o.KeyReductionRotation ? 1 : 0);
            sb.Append(' '); H(sb, o.KeyReductionThreshold);
            sb.Append(' ').Append(o.KeyReductionPasses);
            return sb.ToString();
        }

        static void StoreTimes(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            if (c == null) return;
            if (c.Times == null) c.Times = new double[NumTimes];
            int ti = int.Parse(t[3], CultureInfo.InvariantCulture);
            c.Times[ti] = Bits(t[4]);
        }

        // N <fi> <vi> <ni> <typedId> <elemId> <ct> <cr> <cs> <nt> <nr> <ns> <zT> <zR> <zS> [keys]
        static string RenderN(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            int ni = int.Parse(t[3], CultureInfo.InvariantCulture);
            UfbxBakedNode[] nodes = c?.Bake?.Nodes ?? Array.Empty<UfbxBakedNode>();
            if (ni < 0 || ni >= nodes.Length) return "! node " + ni + " out of range " + nodes.Length;
            UfbxBakedNode node = nodes[ni];

            UfbxBakedVec3[] tr = node.TranslationKeys ?? Array.Empty<UfbxBakedVec3>();
            UfbxBakedQuat[] ro = node.RotationKeys ?? Array.Empty<UfbxBakedQuat>();
            UfbxBakedVec3[] sc = node.ScaleKeys ?? Array.Empty<UfbxBakedVec3>();

            hash = FnvBasis; HashVec3(tr); ulong ht = hash;
            hash = FnvBasis; HashQuat(ro); ulong hr = hash;
            hash = FnvBasis; HashVec3(sc); ulong hs = hash;

            StringBuilder sb = new StringBuilder();
            sb.Append("N ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ni).Append(' ')
                .Append(node.TypedId).Append(' ').Append(node.ElementId).Append(' ')
                .Append(node.ConstantTranslation ? 1 : 0).Append(' ')
                .Append(node.ConstantRotation ? 1 : 0).Append(' ')
                .Append(node.ConstantScale ? 1 : 0).Append(' ')
                .Append(tr.Length).Append(' ').Append(ro.Length).Append(' ').Append(sc.Length).Append(' ');
            Z(sb, ht); sb.Append(' '); Z(sb, hr); sb.Append(' '); Z(sb, hs);
            AppendFirstVec3(sb, tr);
            AppendFirstQuat(sb, ro);
            AppendFirstVec3(sb, sc);
            return sb.ToString();
        }

        // Q <fi> <vi> <ni> <z> <i>
        static string RenderQ(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            int ni = int.Parse(t[3], CultureInfo.InvariantCulture);
            UfbxBakedNode[] nodes = c?.Bake?.Nodes ?? Array.Empty<UfbxBakedNode>();
            if (ni < 0 || ni >= nodes.Length) return "! node " + ni + " out of range " + nodes.Length;
            UfbxBakedVec3[] tr = nodes[ni].TranslationKeys ?? Array.Empty<UfbxBakedVec3>();
            UfbxBakedQuat[] ro = nodes[ni].RotationKeys ?? Array.Empty<UfbxBakedQuat>();
            UfbxBakedVec3[] sc = nodes[ni].ScaleKeys ?? Array.Empty<UfbxBakedVec3>();

            double[] times = new double[MaxSweepTimes];
            Array.Copy(c.Times, times, NumTimes);
            int n = NumTimes;
            n = AppendKeyTimesV3(tr, times, n);
            n = AppendKeyTimesQ(ro, times, n);
            n = AppendKeyTimesV3(sc, times, n);

            hash = FnvBasis;
            long num = 0;
            for (int i = 0; i < n; i++) {
                if (tr.Length > 0) {
                    UfbxVec3 v = UfbxBakeApi.EvaluateBakedVec3(tr, times[i]);
                    HDouble(v.X); HDouble(v.Y); HDouble(v.Z);
                    num++;
                }
                if (ro.Length > 0) {
                    UfbxQuat q = UfbxBakeApi.EvaluateBakedQuat(ro, times[i]);
                    HDouble(q.X); HDouble(q.Y); HDouble(q.Z); HDouble(q.W);
                    num++;
                }
                if (sc.Length > 0) {
                    UfbxVec3 v = UfbxBakeApi.EvaluateBakedVec3(sc, times[i]);
                    HDouble(v.X); HDouble(v.Y); HDouble(v.Z);
                    num++;
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("Q ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ni).Append(' ');
            Z(sb, hash);
            sb.Append(' ').Append(num);
            return sb.ToString();
        }

        // E <fi> <vi> <ei> <elemId> <numProps>
        static string RenderE(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            int ei = int.Parse(t[3], CultureInfo.InvariantCulture);
            UfbxBakedElement[] elements = c?.Bake?.Elements ?? Array.Empty<UfbxBakedElement>();
            if (ei < 0 || ei >= elements.Length) return "! element " + ei + " out of range " + elements.Length;
            UfbxBakedElement element = elements[ei];
            return "E " + fi + " " + vi + " " + ei + " " + element.ElementId + " "
                + (element.Props != null ? element.Props.Length : 0);
        }

        // P <fi> <vi> <ei> <pi> <zName> <const> <numKeys> <zKeys> [first key]
        static string RenderP(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            int ei = int.Parse(t[3], CultureInfo.InvariantCulture);
            int pi = int.Parse(t[4], CultureInfo.InvariantCulture);
            UfbxBakedElement[] elements = c?.Bake?.Elements ?? Array.Empty<UfbxBakedElement>();
            if (ei < 0 || ei >= elements.Length) return "! element " + ei + " out of range " + elements.Length;
            UfbxBakedProp[] props = elements[ei].Props ?? Array.Empty<UfbxBakedProp>();
            if (pi < 0 || pi >= props.Length) return "! prop " + pi + " out of range " + props.Length;
            UfbxBakedVec3[] keys = props[pi].Keys ?? Array.Empty<UfbxBakedVec3>();

            hash = FnvBasis; HashVec3(keys);
            StringBuilder sb = new StringBuilder();
            sb.Append("P ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ei).Append(' ')
                .Append(pi).Append(' ');
            Z(sb, FnvStr(props[pi].Name));
            sb.Append(' ').Append(props[pi].ConstantValue ? 1 : 0).Append(' ').Append(keys.Length).Append(' ');
            Z(sb, hash);
            AppendFirstVec3(sb, keys);
            return sb.ToString();
        }

        // R <fi> <vi> <ei> <pi> <z> <i>
        static string RenderR(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            int ei = int.Parse(t[3], CultureInfo.InvariantCulture);
            int pi = int.Parse(t[4], CultureInfo.InvariantCulture);
            UfbxBakedElement[] elements = c?.Bake?.Elements ?? Array.Empty<UfbxBakedElement>();
            if (ei < 0 || ei >= elements.Length) return "! element " + ei + " out of range " + elements.Length;
            UfbxBakedProp[] props = elements[ei].Props ?? Array.Empty<UfbxBakedProp>();
            if (pi < 0 || pi >= props.Length) return "! prop " + pi + " out of range " + props.Length;
            UfbxBakedVec3[] keys = props[pi].Keys ?? Array.Empty<UfbxBakedVec3>();

            double[] times = new double[MaxSweepTimes];
            Array.Copy(c.Times, times, NumTimes);
            int n = AppendKeyTimesV3(keys, times, NumTimes);

            hash = FnvBasis;
            long num = 0;
            if (keys.Length > 0) {
                for (int i = 0; i < n; i++) {
                    UfbxVec3 v = UfbxBakeApi.EvaluateBakedVec3(keys, times[i]);
                    HDouble(v.X); HDouble(v.Y); HDouble(v.Z);
                    num++;
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("R ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ei).Append(' ')
                .Append(pi).Append(' ');
            Z(sb, hash);
            sb.Append(' ').Append(num);
            return sb.ToString();
        }

        // F <fi> <vi> <z> <i>
        static string RenderF(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            if (c == null || c.Bake == null) return "! no bake";
            UfbxScene scene = EnsureScene(fi);

            hash = FnvBasis;
            long num = 0;
            for (int i = 0; i < scene.Nodes.Length; i++) HashFindNode(c.Bake, scene.Nodes[i], ref num);
            for (int i = 0; i < scene.Elements.Length; i++) HashFindElement(c.Bake, scene.Elements[i], ref num);

            StringBuilder sb = new StringBuilder();
            sb.Append("F ").Append(fi).Append(' ').Append(vi).Append(' ');
            Z(sb, hash);
            sb.Append(' ').Append(num);
            return sb.ToString();
        }

        // C: hash_find_node() -- both lookup forms must agree with each other and the array.
        static void HashFindNode(UfbxBakedAnim bake, UfbxNode node, ref long num)
        {
            UfbxBakedNode? a = UfbxBakeApi.FindBakedNode(bake, node);
            UfbxBakedNode? b = UfbxBakeApi.FindBakedNodeByTypedId(bake, node.TypedId);
            HU32(a.HasValue ? 1u : 0u);
            HU32(b.HasValue ? 1u : 0u);
            if (a.HasValue) {
                UfbxBakedNode n = a.Value;
                UfbxBakedVec3[] tr = n.TranslationKeys ?? Array.Empty<UfbxBakedVec3>();
                HU32(n.TypedId);
                HU32(n.ElementId);
                HU32((uint)tr.Length);
                HDouble(tr.Length > 0 ? tr[0].Time : 0.0);
                HDouble(tr.Length > 0 ? tr[tr.Length - 1].Time : 0.0);
                HU32((uint)(n.RotationKeys != null ? n.RotationKeys.Length : 0));
                HU32((uint)(n.ScaleKeys != null ? n.ScaleKeys.Length : 0));
                HByte(n.ConstantTranslation ? 1 : 0);
                HByte(n.ConstantRotation ? 1 : 0);
                HByte(n.ConstantScale ? 1 : 0);
            }
            if (b.HasValue) {
                HU32(b.Value.TypedId);
                HU32(b.Value.ElementId);
                HByte(b.Value.ConstantTranslation ? 1 : 0);
            }
            num += 2;
        }

        static void HashFindElement(UfbxBakedAnim bake, UfbxElement element, ref long num)
        {
            UfbxBakedElement? a = UfbxBakeApi.FindBakedElement(bake, element);
            UfbxBakedElement? b = UfbxBakeApi.FindBakedElementByElementId(bake, element.ElementId);
            HU32(a.HasValue ? 1u : 0u);
            HU32(b.HasValue ? 1u : 0u);
            if (a.HasValue) {
                UfbxBakedProp[] props = a.Value.Props ?? Array.Empty<UfbxBakedProp>();
                HU32(a.Value.ElementId);
                HSz(props.Length);
                HSz(props.Length > 0 ? (props[0].Keys != null ? props[0].Keys.Length : 0) : 0);
                HDouble(props.Length > 0 && props[0].Keys != null && props[0].Keys.Length > 0
                    ? props[0].Keys[0].Time : 0.0);
            }
            if (b.HasValue) {
                HU32(b.Value.ElementId);
                HSz(b.Value.Props != null ? b.Value.Props.Length : 0);
            }
            UfbxBakedElement? miss = UfbxBakeApi.FindBakedElementByElementId(bake, uint.MaxValue);
            HU32(miss.HasValue ? 1u : 0u);
            num += 3;
        }

        // ------------------------------------------------------------------
        // Sweeps / hashing (byte-for-byte the oracle's helpers)
        // ------------------------------------------------------------------

        static int AppendKeyTimesV3(UfbxBakedVec3[] keys, double[] times, int n)
        {
            keys = keys ?? Array.Empty<UfbxBakedVec3>();
            for (int i = 0; i < 4; i++) {
                int ix = i == 2 ? keys.Length / 2 : (i == 3 ? keys.Length - 1 : i);
                if (ix < 0 || ix >= keys.Length) continue;
                double t = keys[ix].Time;
                times[n++] = t;
                times[n++] = UfbxMath.NextAfter(t, double.PositiveInfinity);
                times[n++] = UfbxMath.NextAfter(t, double.NegativeInfinity);
            }
            return n;
        }

        static int AppendKeyTimesQ(UfbxBakedQuat[] keys, double[] times, int n)
        {
            keys = keys ?? Array.Empty<UfbxBakedQuat>();
            for (int i = 0; i < 4; i++) {
                int ix = i == 2 ? keys.Length / 2 : (i == 3 ? keys.Length - 1 : i);
                if (ix < 0 || ix >= keys.Length) continue;
                double t = keys[ix].Time;
                times[n++] = t;
                times[n++] = UfbxMath.NextAfter(t, double.PositiveInfinity);
                times[n++] = UfbxMath.NextAfter(t, double.NegativeInfinity);
            }
            return n;
        }

        static void HashVec3(UfbxBakedVec3[] keys)
        {
            for (int i = 0; i < keys.Length; i++) {
                HDouble(keys[i].Time);
                HDouble(keys[i].Value.X);
                HDouble(keys[i].Value.Y);
                HDouble(keys[i].Value.Z);
                HU32((uint)keys[i].Flags);
            }
        }

        static void HashQuat(UfbxBakedQuat[] keys)
        {
            for (int i = 0; i < keys.Length; i++) {
                HDouble(keys[i].Time);
                HDouble(keys[i].Value.X);
                HDouble(keys[i].Value.Y);
                HDouble(keys[i].Value.Z);
                HDouble(keys[i].Value.W);
                HU32((uint)keys[i].Flags);
            }
        }

        static void HByte(int b) { hash = (hash ^ ((ulong)b & 0xff)) * FnvPrime; }
        static void HU64(ulong u) { for (int k = 0; k < 8; k++) HByte((int)((u >> (k * 8)) & 0xff)); }
        static void HU32(uint u) { for (int k = 0; k < 4; k++) HByte((int)((u >> (k * 8)) & 0xff)); }
        static void HSz(long v) { HU64(unchecked((ulong)v)); }
        static void HDouble(double d) { HU64(unchecked((ulong)BitConverter.DoubleToInt64Bits(d))); }

        static ulong FnvStr(string s)
        {
            byte[] bytes = UfbxiRawStr.ToBytes(s ?? "");
            ulong h = FnvBasis;
            for (int i = 0; i < bytes.Length; i++) h = (h ^ bytes[i]) * FnvPrime;
            return h;
        }

        // ------------------------------------------------------------------
        // Formatting
        // ------------------------------------------------------------------

        static void H(StringBuilder sb, double d) {
            sb.Append(((ulong)BitConverter.DoubleToInt64Bits(d)).ToString("x16", CultureInfo.InvariantCulture));
        }

        static void Z(StringBuilder sb, ulong u) {
            sb.Append(u.ToString("x16", CultureInfo.InvariantCulture));
        }

        static void AppendFirstVec3(StringBuilder sb, UfbxBakedVec3[] keys)
        {
            if (keys.Length == 0) return;
            UfbxBakedVec3 k = keys[0];
            sb.Append(' '); Z(sb, Bits(k.Time));
            sb.Append(' '); Z(sb, Bits(k.Value.X));
            sb.Append(' '); Z(sb, Bits(k.Value.Y));
            sb.Append(' '); Z(sb, Bits(k.Value.Z));
            sb.Append(' ').Append((uint)k.Flags);
        }

        static void AppendFirstQuat(StringBuilder sb, UfbxBakedQuat[] keys)
        {
            if (keys.Length == 0) return;
            UfbxBakedQuat k = keys[0];
            sb.Append(' '); Z(sb, Bits(k.Time));
            sb.Append(' '); Z(sb, Bits(k.Value.X));
            sb.Append(' '); Z(sb, Bits(k.Value.Y));
            sb.Append(' '); Z(sb, Bits(k.Value.Z));
            sb.Append(' '); Z(sb, Bits(k.Value.W));
            sb.Append(' ').Append((uint)k.Flags);
        }

        static ulong Bits(double d) => unchecked((ulong)BitConverter.DoubleToInt64Bits(d));

        static double Bits(string hex) =>
            BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(hex, 16)));
    }
}
