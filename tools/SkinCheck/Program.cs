// Batch N differential harness: replays tools/skin_oracle.c's record stream through the port's
// skinning evaluation body and compares every record.
//
// Record grammar and why each kind exists: see the header of skin_oracle.c. Summary:
//   I  input description (frame, and the load/evaluate option bits) -- the variant table lives ONLY
//      in the oracle, so this is what tells the harness what to feed the loader and
//      `UfbxApi.EvaluateScene()`.
//   A  one API outcome plus the reported `ufbx_error`, byte for byte (stage 0 = load, 1 = evaluate).
//   K  one mesh of the final scene: the two fields `ufbxi_evaluate_skinning` writes
//      (`skinned_position` / `skinned_normal`) plus `skinned_is_local` and `generated_normals`.
//      Emitted for EVERY mesh, so a wrong "which meshes are skinned" predicate changes the record
//      count and not just a hash.
//   V  end to end: the golden generator's own `ufbxt_hash_scene()` of the final scene.
//
// The load-path call (ufbx.c:25367-25373) is what the 2179 goldens already prove; what this
// harness adds is the *evaluate*-path call at ufbx.c:26413-26419, which `test/hash_scene.c:136`
// never reaches because it passes NULL evaluate opts.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace SkinCheck
{
    static class Program
    {
        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x00000100000001b3UL;

        class Variant
        {
            public int Fi, Vi;
            public int Frame;
            public int LoadExt, LoadCaches, LoadSkin;
            public int EvalSkin, EvalCaches, EvalExt;
            public uint EvalFlags;

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
                Console.WriteLine("usage: SkinCheck <oracle.txt> <corpus.txt> [-o out.txt]");
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
                v.Frame = int.Parse(t[3], CultureInfo.InvariantCulture);
                v.LoadExt = int.Parse(t[4], CultureInfo.InvariantCulture);
                v.LoadCaches = int.Parse(t[5], CultureInfo.InvariantCulture);
                v.LoadSkin = int.Parse(t[6], CultureInfo.InvariantCulture);
                v.EvalSkin = int.Parse(t[7], CultureInfo.InvariantCulture);
                v.EvalCaches = int.Parse(t[8], CultureInfo.InvariantCulture);
                v.EvalExt = int.Parse(t[9], CultureInfo.InvariantCulture);
                v.EvalFlags = uint.Parse(t[10], CultureInfo.InvariantCulture);

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
            Console.WriteLine("SkinCheck: records {0} input {1} mismatches {2}",
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
            loadOpts.LoadExternalFiles = v.LoadExt != 0;
            loadOpts.IgnoreMissingExternalFiles = true;
            loadOpts.EvaluateCaches = v.LoadCaches != 0;
            loadOpts.EvaluateSkinning = v.LoadSkin != 0;
            loadOpts.TargetAxes = UfbxCoordinateAxes.RightHandedYUp;
            loadOpts.TargetUnitMeters = 1.0;

            UfbxError error = new UfbxError();
            UfbxScene scene = null;
            try {
                scene = UfbxApi.LoadFile(NativeRel(rawRel), loadOpts, error);
            } catch (Exception e) {
                // Nothing in this batch should escape: both the load spine and the evaluate spine
                // turn their failures into the reported `ufbx_error`. A raw exception here is
                // therefore a divergence (this is how the pre-batch-N `NotPorted` throw showed up).
                pOut.Add("EXCEPTION " + e.GetType().Name + " " + e.Message);
            }
            pOut.Add(RenderA(v, 0, scene, error));

            if (scene == null) {
                pOut.Add(RenderV(v, null));
                return;
            }

            if (v.Frame >= 0) {
                UfbxEvaluateOpts evalOpts = new UfbxEvaluateOpts();
                evalOpts.EvaluateSkinning = v.EvalSkin != 0;
                evalOpts.EvaluateCaches = v.EvalCaches != 0;
                evalOpts.LoadExternalFiles = v.EvalExt != 0;
                evalOpts.EvaluateFlags = v.EvalFlags;

                // test/hash_scene.c:134 -- the golden's own time formula.
                double time = scene.Anim.TimeBegin + (double) v.Frame / scene.Settings.FramesPerSecond;

                UfbxError evalError = new UfbxError();
                UfbxScene state = null;
                try {
                    state = UfbxApi.EvaluateScene(scene, null, time, evalOpts, evalError);
                } catch (Exception e) {
                    pOut.Add("EXCEPTION " + e.GetType().Name + " " + e.Message);
                }
                pOut.Add(RenderA(v, 1, state, evalError));
                // C frees the source scene here (test/hash_scene.c:141); the port's FreeScene() is
                // the documented no-op and the evaluated scene retains the source on its own.
                scene = state;
            }

            pOut.Add(RenderV(v, scene));
            if (scene != null && scene.Meshes != null) {
                for (int mi = 0; mi < scene.Meshes.Length; mi++) pOut.Add(RenderK(v, mi, scene.Meshes[mi]));
            }
        }

        // ------------------------------------------------------------------
        // Records
        // ------------------------------------------------------------------

        static string RenderA(Variant v, int stage, UfbxScene scene, UfbxError error)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("A ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(stage);
            sb.Append(scene != null ? " 1 " : " 0 ");
            sb.Append((int) error.Type);
            PErr(sb, error);
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

        static string RenderK(Variant v, int mi, UfbxMesh mesh)
        {
            UfbxVertexVec3 pos = mesh.SkinnedPosition;
            UfbxVertexVec3 nrm = mesh.SkinnedNormal;

            StringBuilder sb = new StringBuilder();
            sb.Append("K ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(mi);
            sb.Append(mesh.SkinnedIsLocal ? " 1 " : " 0 ").Append(Len(pos.Values));
            Z(sb, HashVec3(pos.Values));
            sb.Append(nrm.Exists ? " 1" : " 0");
            sb.Append(nrm.UniquePerVertex ? " 1 " : " 0 ").Append(Len(nrm.Values));
            Z(sb, HashVec3(nrm.Values));
            sb.Append(' ').Append(Len(nrm.Indices));
            Z(sb, HashU32(nrm.Indices));
            sb.Append(' ').Append(nrm.ValueReals);
            sb.Append(mesh.GeneratedNormals ? " 1 " : " 0 ").Append(mesh.NumVertices);
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Helpers (mirrors of the oracle's pz()/phex()/hh_bytes())
        // ------------------------------------------------------------------

        static void Z(StringBuilder sb, ulong v)
        {
            sb.Append(' ').Append(v.ToString("x16", CultureInfo.InvariantCulture));
        }

        static int Len<T>(T[] a) { return a != null ? a.Length : 0; }

        // FNV-1a-64 over the RAW BYTES of the `ufbx_vec3` array: `ufbx_real` is `double` in the
        // golden configuration, so hashing the IEEE bits keeps -0.0 and NaN distinguishable.
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
            ulong bits = unchecked((ulong) BitConverter.DoubleToInt64Bits(d));
            for (int i = 0; i < 8; i++) {
                h = (h ^ (bits & 0xff)) * FnvPrime;
                bits >>= 8;
            }
            return h;
        }

        static ulong HashU32(uint[] a)
        {
            ulong h = FnvBasis;
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++) {
                uint x = a[i];
                for (int k = 0; k < 4; k++) {
                    h = (h ^ (x & 0xff)) * FnvPrime;
                    x >>= 8;
                }
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
