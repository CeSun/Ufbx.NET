// Batch O differential harness: replays tools/pivot_oracle.c's record stream through the port's
// `pivot_handling` implementation and compares every record.
//
// Record grammar and why each kind exists: see the header of pivot_oracle.c. Summary:
//   I  input description (pivot_handling, pivot_handling_retain_empties,
//      geometry_transform_handling) -- the variant table lives ONLY in the oracle, so this is what
//      tells the harness what to feed the loader.
//   A  the outcome of the load plus the reported `ufbx_error`, byte for byte.
//   N  scene->nodes.count and scene->metadata.pivot_handling.
//   P  one node of the final scene: `adjust_pre_translation` (the pivot block's main output, and
//      the one field `ufbxt_hash_scene()` does NOT hash), the rewritten RotationPivot /
//      ScalingPivot / ScalingOffset / GeometricTranslation properties, the geometry transform and
//      the helper-node flags. Emitted for EVERY node, so a wrong "which nodes does the block
//      touch" predicate changes the record count and not just a hash.
//   G  one mesh: num_vertices and the FNV of its raw vertex bytes (MODIFY_GEOMETRY pushes the
//      adjustment into the geometry itself).
//   V  end to end: the golden generator's own `ufbxt_hash_scene()`.
//
// Why this batch exists at all: the goldens use default load opts, i.e.
// UFBX_PIVOT_HANDLING_RETAIN, so none of them ever enters the pivot block (ufbx.c:18329).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace PivotCheck
{
    static class Program
    {
        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x00000100000001b3UL;

        class Variant
        {
            public int Fi, Vi;
            public int Ph, Re, Gth;

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
                Console.WriteLine("usage: PivotCheck <oracle.txt> <corpus.txt> [-o out.txt]");
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
                v.Ph = int.Parse(t[3], CultureInfo.InvariantCulture);
                v.Re = int.Parse(t[4], CultureInfo.InvariantCulture);
                v.Gth = int.Parse(t[5], CultureInfo.InvariantCulture);

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
            Console.WriteLine("PivotCheck: records {0} input {1} mismatches {2}",
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
            loadOpts.PivotHandling = (UfbxPivotHandling) v.Ph;
            loadOpts.PivotHandlingRetainEmpties = v.Re != 0;
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
            if (scene.Nodes != null) {
                for (int ni = 0; ni < scene.Nodes.Length; ni++) pOut.Add(RenderP(v, ni, scene.Nodes[ni]));
            }
            if (scene.Meshes != null) {
                for (int mi = 0; mi < scene.Meshes.Length; mi++) pOut.Add(RenderG(v, mi, scene.Meshes[mi]));
            }
            pOut.Add(RenderV(v, scene));
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
            StringBuilder sb = new StringBuilder();
            sb.Append("N ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ');
            sb.Append(Len(scene.Nodes)).Append(' ').Append((int) scene.Metadata.PivotHandling);
            return sb.ToString();
        }

        static string RenderP(Variant v, int ni, UfbxNode node)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("P ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(ni);
            byte[] name = UfbxiRawStr.ToBytes(node.Name ?? "");
            PBytes(sb, name, name.Length);
            sb.Append(' ').Append(node.Parent != null ? (int) node.Parent.TypedId : -1);
            sb.Append(' ').Append(node.NodeDepth);
            sb.Append(node.HasAdjustTransform ? " 1" : " 0");
            PVec(sb, node.AdjustPreTranslation);
            sb.Append(node.HasGeometryTransform ? " 1" : " 0");
            PVec(sb, node.GeometryTransform.Translation);
            Z(sb, Bits(node.GeometryTransform.Rotation.X));
            Z(sb, Bits(node.GeometryTransform.Rotation.Y));
            Z(sb, Bits(node.GeometryTransform.Rotation.Z));
            Z(sb, Bits(node.GeometryTransform.Rotation.W));
            PVec(sb, node.GeometryTransform.Scale);
            // The four properties the pivot block reads / rewrites. Read back through the port's
            // own `ufbxi_find_vec3`, i.e. exactly the lookup the block itself uses.
            PVec(sb, UfbxiProperties.FindVec3(node.Props, "RotationPivot", 0.0, 0.0, 0.0));
            PVec(sb, UfbxiProperties.FindVec3(node.Props, "ScalingPivot", 0.0, 0.0, 0.0));
            PVec(sb, UfbxiProperties.FindVec3(node.Props, "ScalingOffset", 0.0, 0.0, 0.0));
            PVec(sb, UfbxiProperties.FindVec3(node.Props, "GeometricTranslation", 0.0, 0.0, 0.0));
            sb.Append(node.IsScaleHelper ? " 1" : " 0");
            sb.Append(node.IsGeometryTransformHelper ? " 1" : " 0");
            return sb.ToString();
        }

        static string RenderG(Variant v, int mi, UfbxMesh mesh)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("G ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(mi);
            sb.Append(' ').Append(mesh.NumVertices);
            Z(sb, HashVec3(mesh.Vertices));
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

        // FNV-1a-64 over the RAW BYTES: `ufbx_real` is `double` in the golden configuration, so
        // hashing the IEEE bits keeps -0.0 and NaN distinguishable.
        static ulong HashVec3(UfbxVec3[] a)
        {
            ulong h = FnvBasis;
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++) {
                h = HashDouble(h, a[i].X);
                h = HashDouble(h, a[i].Y);
                h = HashDouble(h, a[i].Z);
            }
            return h;
        }

        static ulong HashDouble(ulong h, double d)
        {
            ulong bits = Bits(d);
            for (int i = 0; i < 8; i++) {
                h = (h ^ (bits & 0xff)) * FnvPrime;
                bits >>= 8;
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

        // The corpus path spelled the way .NET wants it: through the port's own UTF-8 -> UTF-16
        // decoder, which is the one thing both sides agree about for non-ASCII names.
        static string NativeRel(string rawRel)
        {
            string utf16 = UfbxiStreamOpen.PathToUtf16(rawRel, rawRel.Length);
            if (utf16 == null) return rawRel;
            return utf16.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
