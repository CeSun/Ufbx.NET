// Batch P differential harness: replays tools/sh_oracle.c's record stream through the port's
// scale-helper implementation and compares every record.
//
// Record grammar and why each kind exists: see the header of sh_oracle.c. Summary:
//   I  input description (inherit_mode_handling, geometry_transform_handling, bake) -- the variant
//      table lives ONLY in the oracle, so this is what tells the harness what to feed the loader.
//   A  the outcome of the load plus the reported `ufbx_error`, byte for byte.
//   N  scene-wide helper counts (incl. `numRecursive`, which is 0 for every file in `data/`).
//   S  one node of the final scene: `is_scale_helper`, `scale_helper`, `inherit_mode`,
//      `is_scale_compensate_parent`, the moved-out `Lcl Scaling`, `inherit_scale_node`.
//      Emitted for EVERY node.
//   X  `ufbx_evaluate_transform()` at three sample times -- this is what exercises
//      ufbx.c:22969-22979 (the parent's *helper* scale folded into the child translation), which
//      the static scene hash cannot see.
//   V  end to end: the golden generator's own `ufbxt_hash_scene()`.
//   B  one baked node -- the animated-helper branch (ufbx.c:27285) plus the resampled
//      translation / scale keys it produces.
//
// Why this batch exists at all (measured, see tools/_scratch/_sh_probe.txt): under
// `inherit_mode_handling = PRESERVE` -- the default, and the only value the 2179 goldens use,
// because `test/hash_scene.c` passes zeroed load opts -- **zero** of the 690 `data/*.fbx` files
// creates a scale helper. So unlike batches N and O this really is uncovered ground.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx;

namespace ShCheck
{
    static class Program
    {
        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x00000100000001b3UL;

        class Variant
        {
            public int Fi, Vi;
            public int Imh, Gth, Bake;

            public readonly List<string> Out = new List<string>();
        }

        static int Main(string[] args)
        {
            string oraclePath = null, corpusPath = null, outPath = null;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "-o" && i + 1 < args.Length) outPath = args[++i];
                else if (oraclePath == null) oraclePath = args[i];
                else if (corpusPath == null) corpusPath = args[i];
            }
            if (oraclePath == null || corpusPath == null) {
                Console.WriteLine("usage: ShCheck <oracle.txt> <corpus.txt> [-o out.txt]");
                return 2;
            }

            List<string> files = ReadCorpus(corpusPath);
            List<string> oracle = new List<string>(File.ReadAllLines(oraclePath));

            List<string> port = new List<string>();
            int mismatches = 0, records = 0, inputRecords = 0;
            List<string> diffs = new List<string>();

            int ax = 0;
            while (ax < oracle.Count) {
                string[] t = oracle[ax].Split(' ');
                if (t.Length == 0 || string.IsNullOrEmpty(t[0])) break;
                if (t[0] != "I") {
                    Console.WriteLine("expected I at oracle line " + (ax + 1) + ": " + oracle[ax]);
                    return 2;
                }

                Variant v = new Variant();
                v.Fi = int.Parse(t[1], CultureInfo.InvariantCulture);
                v.Vi = int.Parse(t[2], CultureInfo.InvariantCulture);
                v.Imh = int.Parse(t[3], CultureInfo.InvariantCulture);
                v.Gth = int.Parse(t[4], CultureInfo.InvariantCulture);
                v.Bake = int.Parse(t[5], CultureInfo.InvariantCulture);

                // `I` is echoed verbatim: it IS the input record.
                port.Add(oracle[ax]);
                ax++;
                inputRecords++;

                RunVariant(v, files[v.Fi]);

                // Everything else until the next `I` (or EOF) is output: compare in order.
                int expected = 0;
                while (ax < oracle.Count) {
                    string[] h = oracle[ax].Split(' ');
                    if (h.Length > 0 && h[0] == "I") break;
                    string mine = expected < v.Out.Count ? v.Out[expected] : "<missing>";
                    records++;
                    if (expected >= v.Out.Count || oracle[ax] != mine) {
                        mismatches++;
                        if (diffs.Count < 40) {
                            diffs.Add("line " + (ax + 1) + "\n  oracle: " + oracle[ax] + "\n  port  : " + mine);
                        }
                    }
                    port.Add(mine);
                    expected++;
                    ax++;
                }
                for (int k = expected; k < v.Out.Count; k++) {
                    mismatches++;
                    if (diffs.Count < 40) diffs.Add("extra port record: " + v.Out[k]);
                    port.Add(v.Out[k]);
                    records++;
                }
            }

            if (outPath != null) File.WriteAllLines(outPath, port.ToArray());

            for (int i = 0; i < diffs.Count; i++) Console.WriteLine(diffs[i]);
            Console.WriteLine("ShCheck: records {0} input {1} mismatches {2}",
                records, inputRecords, mismatches);
            Console.WriteLine(mismatches == 0 ? "ALL MATCH" : "FAIL");
            return mismatches == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // One variant
        // ------------------------------------------------------------------

        static void RunVariant(Variant v, string rawRel)
        {
            List<string> pOut = v.Out;

            UfbxLoadOpts loadOpts = new UfbxLoadOpts();
            loadOpts.LoadExternalFiles = true;
            loadOpts.IgnoreMissingExternalFiles = true;
            loadOpts.TargetAxes = UfbxCoordinateAxes.RightHandedYUp;
            loadOpts.TargetUnitMeters = 1.0;
            loadOpts.InheritModeHandling = (UfbxInheritModeHandling) v.Imh;
            loadOpts.GeometryTransformHandling = (UfbxGeometryTransformHandling) v.Gth;

            UfbxError error = new UfbxError();
            UfbxScene scene = null;
            try {
                scene = UfbxApi.LoadFile(NativeRel(rawRel), loadOpts, error);
            } catch (Exception e) {
                // Nothing in this batch should escape: the load spine turns its failures into the
                // reported `ufbx_error`. A raw exception here is therefore a divergence.
                pOut.Add("EXCEPTION " + e.GetType().Name + " " + e.Message);
            }
            pOut.Add(RenderA(v, scene, error));

            if (scene == null) {
                pOut.Add(RenderV(v, null));
                return;
            }

            pOut.Add(RenderN(v, scene));
            UfbxNode[] nodes = scene.Nodes;
            if (nodes != null) {
                for (int ni = 0; ni < nodes.Length; ni++) pOut.Add(RenderS(v, ni, nodes[ni]));
            }

            UfbxAnim anim = scene.Anim;
            if (anim != null && nodes != null) {
                double begin = anim.TimeBegin, end = anim.TimeEnd;
                double[] times = new double[3];
                times[0] = begin;
                times[1] = begin + (end - begin) * 0.37;
                times[2] = end;
                for (int tix = 0; tix < 3; tix++) {
                    for (int ni = 0; ni < nodes.Length; ni++) {
                        UfbxTransform t = UfbxiEvaluate.EvaluateTransform(anim, nodes[ni], times[tix]);
                        pOut.Add(RenderX(v, ni, tix, times[tix], t));
                    }
                }
            }

            pOut.Add(RenderV(v, scene));

            if (v.Bake != 0 && anim != null) {
                UfbxBakeOpts bakeOpts = new UfbxBakeOpts();
                UfbxError berr = new UfbxError();
                UfbxBakedAnim bake = null;
                try {
                    bake = UfbxBakeApi.BakeAnim(scene, anim, bakeOpts, berr);
                } catch (Exception e) {
                    pOut.Add("EXCEPTION " + e.GetType().Name + " " + e.Message);
                }
                if (bake == null) {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("A ").Append(v.Fi).Append(' ').Append(v.Vi).Append(" 0 ").Append((int) berr.Type);
                    PErr(sb, berr);
                    pOut.Add(sb.ToString());
                } else {
                    UfbxBakedNode[] bn = bake.Nodes;
                    for (int ni = 0; ni < (bn != null ? bn.Length : 0); ni++) pOut.Add(RenderB(v, ni, bn[ni]));
                }
            }
        }

        // ------------------------------------------------------------------
        // Records
        // ------------------------------------------------------------------

        static string RenderA(Variant v, UfbxScene scene, UfbxError error)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("A ").Append(v.Fi).Append(' ').Append(v.Vi);
            sb.Append(scene != null ? " 1 " : " 0 ");
            sb.Append((int) error.Type);
            PErr(sb, error);
            return sb.ToString();
        }

        static string RenderN(Variant v, UfbxScene scene)
        {
            UfbxNode[] nodes = scene.Nodes;
            int helpers = 0, withHelper = 0, recursive = 0, compensateParent = 0;
            if (nodes != null) {
                for (int i = 0; i < nodes.Length; i++) {
                    UfbxNode n = nodes[i];
                    if (n.IsScaleHelper) helpers++;
                    if (n.ScaleHelper != null) withHelper++;
                    if (n.IsScaleHelper && n.Parent != null && n.Parent.IsScaleHelper) recursive++;
                    if (n.IsScaleCompensateParent) compensateParent++;
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("N ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ');
            sb.Append(Len(nodes)).Append(' ').Append(helpers).Append(' ').Append(withHelper);
            sb.Append(' ').Append(recursive).Append(' ').Append(compensateParent);
            return sb.ToString();
        }

        static string RenderS(Variant v, int ni, UfbxNode node)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("S ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(ni);
            byte[] name = UfbxiRawStr.ToBytes(node.Name ?? "");
            PBytes(sb, name, name.Length);
            sb.Append(' ').Append(node.Parent != null ? (int) node.Parent.TypedId : -1);
            sb.Append(' ').Append(node.NodeDepth);
            sb.Append(node.IsScaleHelper ? " 1" : " 0");
            sb.Append(' ').Append(node.ScaleHelper != null ? (int) node.ScaleHelper.TypedId : -1);
            sb.Append(' ').Append((int) node.InheritMode);
            sb.Append(' ').Append((int) node.OriginalInheritMode);
            sb.Append(node.IsScaleCompensateParent ? " 1" : " 0");
            sb.Append(node.IsGeometryTransformHelper ? " 1" : " 0");
            PVec(sb, node.LocalTransform.Scale);
            sb.Append(' ').Append(node.InheritScaleNode != null ? (int) node.InheritScaleNode.TypedId : -1);
            return sb.ToString();
        }

        static string RenderX(Variant v, int ni, int tix, double time, UfbxTransform t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("X ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(ni).Append(' ').Append(tix);
            Z(sb, Bits(time));
            PVec(sb, t.Translation);
            PReal(sb, t.Rotation.X); PReal(sb, t.Rotation.Y);
            PReal(sb, t.Rotation.Z); PReal(sb, t.Rotation.W);
            PVec(sb, t.Scale);
            return sb.ToString();
        }

        static string RenderV(Variant v, UfbxScene scene)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("V ").Append(v.Fi).Append(' ').Append(v.Vi);
            sb.Append(scene != null ? " 1" : " 0");
            Z(sb, scene != null ? UfbxHashScene.HashScene(scene) : 0);
            return sb.ToString();
        }

        static string RenderB(Variant v, int ni, UfbxBakedNode bn)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("B ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(ni);
            sb.Append(bn.ConstantTranslation ? " 1" : " 0");
            sb.Append(bn.ConstantRotation ? " 1" : " 0");
            sb.Append(bn.ConstantScale ? " 1" : " 0");
            sb.Append(' ').Append(Len(bn.TranslationKeys));
            sb.Append(' ').Append(Len(bn.RotationKeys));
            sb.Append(' ').Append(Len(bn.ScaleKeys));
            Z(sb, HashVec3Keys(bn.TranslationKeys));
            Z(sb, HashQuatKeys(bn.RotationKeys));
            Z(sb, HashVec3Keys(bn.ScaleKeys));
            for (int which = 0; which < 4; which++) {
                UfbxBakedVec3[] keys = which < 2 ? bn.TranslationKeys : bn.ScaleKeys;
                int count = Len(keys);
                int ix = (which & 1) != 0 && count > 0 ? count - 1 : 0;
                if (count == 0) { sb.Append(" -"); PReal(sb, 0.0); PReal(sb, 0.0); PReal(sb, 0.0); }
                else { Z(sb, Bits(keys[ix].Time)); PVec(sb, keys[ix].Value); }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Helpers (mirrors of the oracle's pz()/phex()/hh_bytes())
        // ------------------------------------------------------------------

        static ulong Bits(double d) { return unchecked((ulong) BitConverter.DoubleToInt64Bits(d)); }

        static void Z(StringBuilder sb, ulong v)
        {
            sb.Append(' ').Append(v.ToString("x16", CultureInfo.InvariantCulture));
        }

        static void PReal(StringBuilder sb, double d) { Z(sb, Bits(d)); }

        static void PVec(StringBuilder sb, UfbxVec3 v)
        {
            PReal(sb, v.X); PReal(sb, v.Y); PReal(sb, v.Z);
        }

        static int Len<T>(T[] a) { return a != null ? a.Length : 0; }

        static ulong HashDouble(ulong h, double d)
        {
            ulong bits = Bits(d);
            for (int i = 0; i < 8; i++) {
                h = (h ^ (bits & 0xff)) * FnvPrime;
                bits >>= 8;
            }
            return h;
        }

        static ulong HashU32(ulong h, uint v)
        {
            for (int i = 0; i < 4; i++) {
                h = (h ^ (v & 0xff)) * FnvPrime;
                v >>= 8;
            }
            return h;
        }

        static ulong HashVec3Keys(UfbxBakedVec3[] keys)
        {
            ulong h = FnvBasis;
            if (keys == null) return h;
            for (int i = 0; i < keys.Length; i++) {
                h = HashDouble(h, keys[i].Time);
                h = HashDouble(h, keys[i].Value.X);
                h = HashDouble(h, keys[i].Value.Y);
                h = HashDouble(h, keys[i].Value.Z);
                h = HashU32(h, (uint) keys[i].Flags);
            }
            return h;
        }

        static ulong HashQuatKeys(UfbxBakedQuat[] keys)
        {
            ulong h = FnvBasis;
            if (keys == null) return h;
            for (int i = 0; i < keys.Length; i++) {
                h = HashDouble(h, keys[i].Time);
                h = HashDouble(h, keys[i].Value.X);
                h = HashDouble(h, keys[i].Value.Y);
                h = HashDouble(h, keys[i].Value.Z);
                h = HashDouble(h, keys[i].Value.W);
                h = HashU32(h, (uint) keys[i].Flags);
            }
            return h;
        }

        static void PBytes(StringBuilder sb, byte[] b, int length)
        {
            sb.Append(' ').Append(length).Append(' ');
            if (length <= 0 || b == null) { sb.Append('-'); return; }
            for (int i = 0; i < length && i < b.Length; i++) {
                sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        static void PErr(StringBuilder sb, UfbxError e)
        {
            byte[] desc = UfbxiRawStr.ToBytes(e.Description ?? "");
            PBytes(sb, desc, desc.Length);
            sb.Append(' ').Append(e.InfoLength).Append(' ');
            byte[] info = UfbxiRawStr.ToBytes(e.Info ?? "");
            if (e.InfoLength <= 0) { sb.Append('-'); return; }
            for (int i = 0; i < e.InfoLength && i < info.Length; i++) {
                sb.Append(info[i].ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        // Raw bytes, not `File.ReadLines()`: the corpus entry is a UTF-8 byte string, exactly what
        // the oracle reads. Decoding it as text would turn non-ASCII names into code points > 0xff.
        static List<string> ReadCorpus(string path)
        {
            List<string> files = new List<string>();
            byte[] all = File.ReadAllBytes(path);
            int begin = 0;
            for (int i = 0; i <= all.Length; i++) {
                if (i < all.Length && all[i] != (byte) '\n') continue;
                int end = i;
                if (end > begin && all[end - 1] == (byte) '\r') end--;
                if (end > begin && all[begin] == (byte) '#') { begin = i + 1; continue; }
                if (end > begin) files.Add(UfbxiRawStr.FromBytes(all, begin, end - begin));
                begin = i + 1;
            }
            return files;
        }

        static string NativeRel(string rawRel)
        {
            string utf16 = UfbxiStreamOpen.PathToUtf16(rawRel, rawRel.Length);
            if (utf16 == null) return rawRel;
            return utf16.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
