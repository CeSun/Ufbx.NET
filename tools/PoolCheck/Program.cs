// Batch M differential harness: replays tools/pool_oracle.c's record stream through the port's
// thread pool and compares every record.
//
// Record grammar and why each kind exists: see the header of pool_oracle.c. Summary:
//   I  input description (num_tasks, memory limit, pool mode, init/run/free rules, payload
//      mutation) -- the variant table lives ONLY in the oracle, so this is what tells the harness
//      what to feed the loader.
//   S  the payload bytes, as an FNV digest: proves the two sides agree about the INPUT.
//   B  one thread-pool callback invocation, in call order, with its arguments.
//   U  one `ufbx_thread_pool_get_user_ptr()` observation (NULL / the sentinel / anything else).
//   A  ufbx_load_memory()'s outcome and the reported `ufbx_error`, byte for byte.
//   E  end to end: the golden generator's own scene hash.
//
// The port's pool callbacks are invoked synchronously by the port's own loader, exactly like C's
// callbacks are invoked by `ufbxi_thread_pool_flush_group()`/`_wait_imp()`, so everything above is
// deterministic on both sides. Real concurrency order is NOT observable here and must not be: what
// the harness compares is the argument stream, which is a property of the *loader's* task pattern.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace PoolCheck
{
    static class Program
    {
        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x00000100000001b3UL;

        // Mirror of the oracle's enum values; the oracle sends them numerically in `I`.
        const int PoolFull = 0, PoolNoRun = 1, PoolNoWait = 2, PoolNone = 3;
        const int InitNone = 0, InitSet = 1, InitFail = 2;
        // The oracle's `run` enum keeps `RUN_NONE` (index 1) even though no variant uses it
        // any more, so these ordinals are not 0..4.
        const int RunExec = 0, RunNone = 1, RunMod = 2, RunReverse = 3, RunTwice = 4, RunHalf = 5;
        const int FreeNone = 0, FreeGet = 1;
        const int MutNone = 0, MutTrunc = 1, MutByteflip = 2;

        // The one object ever stored through `UfbxApi.ThreadPoolSetUserPtr()`; identity comparison
        // is this port's counterpart of C comparing the `void *` it stored.
        static readonly object Sentinel = new object();

        class Variant
        {
            public int Fi, Vi;
            public int NumTasks;
            public int MemLimit;
            public int PoolMode, InitMode, RunMode, FreeMode, Mutate;

            public int Seq;
            public UfbxScene Scene;
            public readonly List<string> Out = new List<string>();
        }

        static Variant s_cur;

        static int Main(string[] args)
        {
            string oraclePath = null, corpusPath = null, outPath = null;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "-o" && i + 1 < args.Length) outPath = args[++i];
                else if (oraclePath == null) oraclePath = args[i];
                else if (corpusPath == null) corpusPath = args[i];
            }
            if (oraclePath == null || corpusPath == null) {
                Console.WriteLine("usage: PoolCheck <oracle.txt> <corpus.txt> [-o out.txt]");
                return 2;
            }

            List<string> files = ReadCorpus(corpusPath);
            byte[][] payloads = new byte[files.Count][];
            for (int i = 0; i < files.Count; i++) payloads[i] = ReadFileBytes(files[i]);

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
                v.NumTasks = int.Parse(t[3], CultureInfo.InvariantCulture);
                v.MemLimit = int.Parse(t[4], CultureInfo.InvariantCulture);
                v.PoolMode = int.Parse(t[5], CultureInfo.InvariantCulture);
                v.InitMode = int.Parse(t[6], CultureInfo.InvariantCulture);
                v.RunMode = int.Parse(t[7], CultureInfo.InvariantCulture);
                v.FreeMode = int.Parse(t[8], CultureInfo.InvariantCulture);
                v.Mutate = int.Parse(t[9], CultureInfo.InvariantCulture);
                port.Add(oracle[ax]);  // I is echoed verbatim: it IS the input record
                ax++;
                inputRecords++;

                byte[] payload = BuildPayload(payloads[v.Fi], v.Mutate);
                port.Add(RenderS(v, payload));
                inputRecords++;
                if (ax >= oracle.Count || oracle[ax] != port[port.Count - 1]) {
                    diffs.Add("INPUT-DIVERGENCE " + port[port.Count - 1] + " vs " +
                        (ax < oracle.Count ? oracle[ax] : "<eof>"));
                    mismatches++;
                }
                ax++;

                s_cur = v;
                RunVariant(v, payload);

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
                // Any extra output the port produced beyond what the oracle emitted.
                for (int k = expected; k < v.Out.Count; k++) {
                    mismatches++;
                    if (diffs.Count < 40) diffs.Add("extra port record: " + v.Out[k]);
                    port.Add(v.Out[k]);
                    records++;
                }
            }

            if (outPath != null) File.WriteAllLines(outPath, port.ToArray());

            for (int i = 0; i < diffs.Count; i++) Console.WriteLine(diffs[i]);
            Console.WriteLine("PoolCheck: records {0} input {1} mismatches {2}",
                records, inputRecords, mismatches);
            Console.WriteLine(mismatches == 0 ? "ALL MATCH" : "FAIL");
            return mismatches == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // One variant
        // ------------------------------------------------------------------

        static void RunVariant(Variant v, byte[] payload)
        {
            List<string> pOut = v.Out;

            UfbxLoadOpts opts = new UfbxLoadOpts();
            if (v.PoolMode != PoolNone) {
                opts.ThreadOpts.NumTasks = v.NumTasks;
                opts.ThreadOpts.MemoryLimit = v.MemLimit;
                if (v.PoolMode != PoolNoWait) opts.ThreadOpts.Pool.WaitFn = WaitFn;
                if (v.PoolMode != PoolNoRun) opts.ThreadOpts.Pool.RunFn = RunFn;
                if (v.InitMode != InitNone) opts.ThreadOpts.Pool.InitFn = InitFn;
                if (v.FreeMode != FreeNone) opts.ThreadOpts.Pool.FreeFn = FreeFn;
            }

            UfbxError error = new UfbxError();
            UfbxScene scene = null;
            try {
                scene = UfbxApi.LoadMemory(payload, payload.Length, opts, error);
            } catch (Exception e) {
                // Nothing in this batch should escape: the load spine turns its failures into the
                // reported `ufbx_error`. A raw exception here is therefore a divergence.
                pOut.Add("EXCEPTION " + e.GetType().Name + " " + e.Message);
            }
            v.Scene = scene;

            pOut.Add(RenderA(v, scene, error));
            pOut.Add(RenderE(v, scene));
        }

        // ------------------------------------------------------------------
        // The pool callbacks (they run inline, like the oracle's)
        // ------------------------------------------------------------------

        static void EmitB(int kind, ulong a, ulong b, ulong c)
        {
            Variant v = s_cur;
            v.Out.Add(string.Format(CultureInfo.InvariantCulture,
                "B {0} {1} {2} {3} {4} {5} {6}", v.Fi, v.Vi, v.Seq++, kind, a, b, c));
        }

        // phase: 0 init pre-set, 1 init post-set, 2 run, 3 wait, 4 free.
        // token: 0 = null, 1 = sentinel, 2 = anything else.
        static void EmitU(int phase)
        {
            Variant v = s_cur;
            object o = UfbxApi.ThreadPoolGetUserPtr(CurrentCtx);
            int token = 2;
            if (o == null) token = 0;
            else if (ReferenceEquals(o, Sentinel)) token = 1;
            v.Out.Add(string.Format(CultureInfo.InvariantCulture,
                "U {0} {1} {2} {3} {4}", v.Fi, v.Vi, v.Seq++, phase, token));
        }

        static nint CurrentCtx;

        static bool InitFn(object user, nint ctx, UfbxThreadPoolInfo info)
        {
            CurrentCtx = ctx;
            EmitB(0, info.MaxConcurrentTasks, 0, 0);
            if (s_cur.InitMode == InitNone) return true;
            EmitU(0);
            UfbxApi.ThreadPoolSetUserPtr(ctx, Sentinel);
            EmitU(1);
            return s_cur.InitMode != InitFail;
        }

        static void RunFn(object user, nint ctx, uint group, uint startIndex, uint count)
        {
            CurrentCtx = ctx;
            EmitB(1, group, startIndex, count);
            EmitU(2);
            switch (s_cur.RunMode) {
            case RunNone:
                return;
            case RunExec:
                for (uint i = 0; i < count; i++) UfbxApi.ThreadPoolRunTask(ctx, startIndex + i);
                break;
            case RunTwice:
                for (uint i = 0; i < count; i++) {
                    UfbxApi.ThreadPoolRunTask(ctx, startIndex + i);
                    UfbxApi.ThreadPoolRunTask(ctx, startIndex + i);
                }
                break;
            case RunReverse:
                for (uint i = count; i-- > 0; ) UfbxApi.ThreadPoolRunTask(ctx, startIndex + i);
                break;
            case RunMod:
                // `index % num_tasks` (ufbx.c:6011): adding 2048 changes nothing observable, and
                // with num_tasks == 1 every index lands on slot 0.
                for (uint i = 0; i < count; i++) UfbxApi.ThreadPoolRunTask(ctx, startIndex + i + 2048);
                break;
            case RunHalf:
                if (count > 0) UfbxApi.ThreadPoolRunTask(ctx, startIndex);
                break;
            }
        }

        static void WaitFn(object user, nint ctx, uint group, uint maxIndex)
        {
            CurrentCtx = ctx;
            EmitB(2, group, maxIndex, 0);
            EmitU(3);
        }

        static void FreeFn(object user, nint ctx)
        {
            CurrentCtx = ctx;
            EmitB(3, 0, 0, 0);
            if (s_cur.FreeMode == FreeGet) EmitU(4);
        }

        // ------------------------------------------------------------------
        // Records
        // ------------------------------------------------------------------

        static string RenderS(Variant v, byte[] payload)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("S ").Append(v.Fi).Append(' ').Append(v.Vi).Append(' ').Append(payload.Length);
            Z(sb, HBytes(FnvBasis, payload));
            return sb.ToString();
        }

        static string RenderA(Variant v, UfbxScene scene, UfbxError error)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("A ").Append(v.Fi).Append(' ').Append(v.Vi);
            sb.Append(scene != null ? " 1 " : " 0 ");
            sb.Append((int) error.Type);
            PErr(sb, error);
            return sb.ToString();
        }

        static string RenderE(Variant v, UfbxScene scene)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("E ").Append(v.Fi).Append(' ').Append(v.Vi);
            sb.Append(scene != null ? " 1" : " 0");
            Z(sb, scene != null ? UfbxHashScene.HashScene(scene) : 0);
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Helpers (mirrors of the oracle's pz()/phex())
        // ------------------------------------------------------------------

        static void Z(StringBuilder sb, ulong v)
        {
            sb.Append(' ').Append(v.ToString("x16", CultureInfo.InvariantCulture));
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

        static ulong HBytes(ulong h, byte[] b)
        {
            if (b == null) return h;
            for (int i = 0; i < b.Length; i++) h = (h ^ b[i]) * FnvPrime;
            return h;
        }

        static byte[] BuildPayload(byte[] raw, int mutate)
        {
            byte[] p = new byte[raw.Length];
            Array.Copy(raw, 0, p, 0, raw.Length);
            if (mutate == MutTrunc) {
                byte[] t = new byte[raw.Length / 2];
                Array.Copy(p, 0, t, 0, t.Length);
                return t;
            }
            if (mutate == MutByteflip) {
                int at = p.Length / 2;
                p[at] = unchecked((byte) (p[at] ^ 0xff));
            }
            return p;
        }

        // Raw bytes, not `File.ReadLines()`: the corpus entry is a UTF-8 byte string, exactly what
        // the oracle reads. Decoding it as text would turn `data/synthetic_aβカ😂_7500_ascii.fbx`
        // into code points > 0xff (see the ReadCorpus() note in tools/LoadCheck/Program.cs).
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

        static byte[] ReadFileBytes(string rawRel)
        {
            string native = NativeRel(rawRel);
            return File.ReadAllBytes(native);
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
