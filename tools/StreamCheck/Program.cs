// StreamCheck: differential verification for batch L (the stream / stdio / open ABI family).
//
// Replays the record stream of tools/stream_oracle.c (built with zig, `#include "ufbx.c"`, so it
// runs the original `ufbxi_fopen()` / `ufbxi_stdio_*` / `ufbxi_memory_*` / `ufbx_open_*` /
// `ufbx_load_stream*` / `ufbx_load_stdio*` / the deferred open of `ufbxi_load_imp()`) through
// src/Ufbx/Parse/StreamOpen.cs + src/Ufbx/Parse/InputStreams.cs + src/Ufbx/Api/UfbxApi.cs.
//
// The oracle owns the whole variant table and re-emits the payload, the path, the prefix and every
// option as `S`/`I`/`Ip`/`Id` records, so this harness has no mirror of `k_variants[]`,
// `build_payload()` or `path_bytes()`: it rebuilds them from the records and calls the port with
// them. For every oracle line the port renders the *same grammar* from its own values and the two
// lines are compared as text, so a mismatch is reported as "oracle line / port line" and the first
// differing token is the diverging field.
//
// What each record proves
// ----------------------
//   S   the two sides built byte-identical INPUT (payload kind/arg/length + FNV)
//   I/Ip/Id  input only: the variant, the path bytes and the prefix bytes
//   A   the ABI call's outcome: NULL/non-NULL, whether the out stream got wired, and the *bytes* of
//       the reported error -- which is what separates "Invalid UTF-8" (no info payload) from
//       "File not found" (info = the raw path) and both from the "Failed to open file" /
//       "Failed to load" defaults `ufbxi_fix_error_type()` substitutes
//   C   the CALLER's own UfbxError after the call: a successful `OpenFile()` clears it
//       (ufbx.c:6971) and `LoadStdio(null)` does not touch it at all (ufbx.c:30544)
//   K   the FileStream position after a stdio load -- `close_fn == NULL` (ufbx.c:30546) means ufbx
//       must not close the handle, so the caller can see where it stopped
//   M   what `ufbx_close_memory_cb` received: with `no_copy` that is the CALLER's buffer
//   F   what a custom `open_file_cb` received -- the RESOLVED `path_len` (ufbx.c:25239), the
//       `info->type` and `info->original_filename`
//   R/H/Q  the IO callback log: counts by kind, an FNV over (kind, arg, ret), and the full log when
//       it is short. This is what pins the loader's IO *timing*, not just its result.
//   P   the progress callback log: bytes_read/bytes_total, including the `progress_bytes_total == 0`
//       start of `ufbx_load_stream_prefix()`
//   V   end-to-end: the loaded scene hashed by UfbxHashScene (the transcription of
//       test/hash_scene.h the goldens use)
//
// Usage (from C:/Workspace/_analyze_ufbx, so the `data/...` corpus paths resolve):
//   dotnet run --project C:/Workspace/ufbx-cs/tools/StreamCheck -c Release -- \
//       C:/Workspace/ufbx-cs/tools/stream_oracle.txt \
//       C:/Workspace/ufbx-cs/tools/stream_corpus.txt [-o port.txt]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx;

namespace StreamCheck
{
    static class Program
    {
        const int NumVariants = 78;

        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x00000100000001b3UL;

        // The oracle's IO_MAX (tools/stream_oracle.c): how much of the callback log is retained.
        const int IoMax = 4096;

        // -- Modes (mirror of the oracle's SM_* enum)
        const int SM_MEM_REAL = 0;
        const int SM_MEM_ONLY = 1;
        const int SM_FILE_REAL = 2;
        const int SM_FILE_ONLY = 3;
        const int SM_STDIO = 4;
        const int SM_STDIO_NULL = 5;
        const int SM_SCRIPT = 6;
        const int SM_LOAD_FILE_CB = 7;
        const int SM_LOAD_FILE_DEFAULT = 8;

        // -- Payload kinds
        const int PK_WHOLE = 0;
        const int PK_HEAD = 1;
        const int PK_GARBAGE = 2;
        const int PK_EMPTY = 3;
        const int PK_ZEROS = 4;
        const int PK_ASCII_HEAD = 5;

        // -- Script rules (mirror of the oracle's SR_* enum)
        const int SR_PERFECT = 0;
        const int SR_CHUNK1000 = 1;
        const int SR_CHUNK1 = 2;
        const int SR_CHUNK3 = 3;
        const int SR_FAIL5 = 4;
        const int SR_EOF3 = 5;
        const int SR_NOSKIP = 6;
        const int SR_NOSIZE = 7;
        const int SR_SKIPFAIL = 8;
        const int SR_SIZE0 = 9;
        const int SR_SIZEMAX = 10;
        const int SR_EMPTY = 11;
        const int SR_FAIL0 = 12;
        const int SR_EOF0 = 13;

        static int mismatches;
        static int checkedRecords;
        static int inputRecords;
        static readonly List<string> DiffOracle = new List<string>();
        static readonly List<string> DiffPort = new List<string>();
        static readonly List<string> Lines = new List<string>();

        static List<string> corpus;
        static byte[][] wholeFiles;
        static Ctx[,] cache;
        static VariantInput[,] inputs;

        // ------------------------------------------------------------------
        // Per-variant state
        // ------------------------------------------------------------------

        sealed class IoRec
        {
            public int Kind;
            public ulong Arg;
            public ulong Ret;
        }

        sealed class VariantInput
        {
            public int Mode, Payload, PayloadArg, PayloadLen, Prefix;
            public int OptsNull, NoCopy, CloseCb, Ctx, Mutate, Script;
            public int Dirty, ErrNull, Path, PathLen, NulTerm, Progress, ReadBuf;
            public int CbFail, DefaultCb, WithDefault;
            public string PathBytes;
            public byte[] PrefixBytes;
            public byte[] PayloadBytes;
        }

        sealed class Ctx
        {
            public UfbxScene Scene;
            public UfbxError Error;          // null when the variant passes a NULL error
            public bool Ok;
            public int Wired = -1;           // -1 = the ABI has no out-stream
            public long Cursor = -1;
            public bool Logged = true;

            public int CloseN;
            public byte[] CloseBytes;
            public int CloseSize;
            public ulong CloseHash;

            public int CbN;
            public bool CbFail;
            public int CbPathLen;
            public string CbPath;
            public int CbType;
            public int CbOrigLen;
            public string CbOrig;

            public readonly List<IoRec> Io = new List<IoRec>();
            public int NRead, NSkip, NSize, NClose;
            public readonly List<ulong[]> Prog = new List<ulong[]>();

            // The oracle keeps counting forever but only STORES the first IO_MAX entries
            // (`io_log()`, tools/stream_oracle.c) -- `R` is uncapped while `H`'s call count and the
            // `Q` detail lines are. The digest is accumulated over every call either way, which is
            // why it cannot be recomputed from the truncated list.
            public ulong IoHashAcc = FnvBasis;

            public void Log(int kind, ulong arg, ulong ret)
            {
                if (kind == 0) NRead++;
                else if (kind == 1) NSkip++;
                else if (kind == 2) NSize++;
                else NClose++;
                IoHashAcc = HU64(HU64(HU64(IoHashAcc, (ulong) kind), arg), ret);
                if (Io.Count < IoMax) Io.Add(new IoRec { Kind = kind, Arg = arg, Ret = ret });
            }

            public ulong IoHash()
            {
                return IoHashAcc;
            }
        }

        // ------------------------------------------------------------------
        // The logging wrapper (the port's UfbxInputStream callbacks)
        // ------------------------------------------------------------------

        sealed class LogStream : UfbxInputStream
        {
            readonly UfbxInputStream inner;
            readonly Ctx ctx;

            public LogStream(UfbxInputStream inner, Ctx ctx)
            {
                this.inner = inner;
                this.ctx = ctx;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int r = inner.Read(buffer, offset, count);
                ctx.Log(0, (ulong) count, unchecked((ulong) r));
                return r;
            }

            // C's wrapper passes skip_fn/size_fn/close_fn through only when the inner stream has
            // them (`ufbxi_skip_bytes()` has a second branch for `skip_fn == NULL`).
            public override bool CanSkip => inner.CanSkip;

            public override bool Skip(int size)
            {
                bool ok = inner.Skip(size);
                ctx.Log(1, (ulong) size, ok ? 1UL : 0UL);
                return ok;
            }

            public override ulong Size()
            {
                ulong r = inner.Size();
                ctx.Log(2, 0, r);
                return r;
            }

            public override void Close()
            {
                ctx.Log(3, 0, 0);
                inner.Close();
            }
        }

        // ------------------------------------------------------------------
        // The scripted stream: the rule is replayed verbatim from the oracle
        // ------------------------------------------------------------------

        sealed class ScriptStream : UfbxInputStream
        {
            readonly byte[] data;
            readonly int size;
            readonly int rule;
            int pos;
            int nread, nskip, nsize;

            public ScriptStream(byte[] data, int size, int startPos, int rule)
            {
                this.data = data;
                this.size = size;
                this.rule = rule;
                pos = startPos;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int ix = nread++;
                if (rule == SR_FAIL0) return -1;                        // C: SIZE_MAX
                if (rule == SR_FAIL5 && ix == 5) return -1;
                if (rule == SR_EMPTY || rule == SR_EOF0) return 0;
                if (rule == SR_EOF3 && ix == 3) return 0;

                int avail = size - pos;
                int cap = avail;
                if (rule == SR_CHUNK1000) cap = 1000;
                else if (rule == SR_CHUNK1) cap = 1;
                else if (rule == SR_CHUNK3) cap = 3;

                int n = count < cap ? count : cap;
                if (n > avail) n = avail;
                if (n > 0) Array.Copy(data, pos, buffer, offset, n);
                pos += n;
                return n;
            }

            public override bool CanSkip => rule != SR_NOSKIP;

            public override bool Skip(int skipSize)
            {
                nskip++;
                if (rule == SR_SKIPFAIL) return false;
                if (size - pos < skipSize) return false;
                pos += skipSize;
                return true;
            }

            // C only consults `size_fn` for the progress total, so "no size_fn" and "size returns
            // 0" are the same observable; both are modelled as 0.
            public override ulong Size()
            {
                nsize++;
                if (rule == SR_SIZE0) return 0;
                if (rule == SR_SIZEMAX) return ulong.MaxValue;
                return (ulong) size;
            }
        }

        // ------------------------------------------------------------------
        // Driver
        // ------------------------------------------------------------------

        static int Main(string[] args)
        {
            if (args.Length < 2) {
                Console.Error.WriteLine("usage: StreamCheck <stream_oracle.txt> "
                    + "<stream_corpus.txt> [-o port.txt]");
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

            // Raw bytes, not File.ReadLines(): the port's `filename` is the raw-byte string model
            // (1 char == 1 byte, see UfbxiRawStr) -- exactly the UTF-8 `const char *` the oracle
            // reads off disk. Decoding the list as text would turn the non-ASCII corpus entry into
            // code points > 0xff, which PathToUtf16() then reads as stray continuation bytes.
            corpus = new List<string>();
            byte[] raw = File.ReadAllBytes(corpusPath);
            int begin = 0;
            for (int i = 0; i <= raw.Length; i++) {
                if (i < raw.Length && raw[i] != (byte) '\n') continue;
                int end = i;
                if (end > begin && raw[end - 1] == (byte) '\r') end--;
                if (end > begin) {
                    string line = UfbxiRawStr.FromBytes(raw, begin, end - begin);
                    if (line.Length > 0 && line[0] != '#') corpus.Add(line);
                }
                begin = i + 1;
            }
            wholeFiles = new byte[corpus.Count][];
            cache = new Ctx[corpus.Count, NumVariants];
            inputs = new VariantInput[corpus.Count, NumVariants];

            // The oracle runs from the ufbx checkout root, so relative paths resolve the same way.
            string root = Directory.Exists("data") ? Directory.GetCurrentDirectory()
                : @"C:\Workspace\_analyze_ufbx";
            Directory.SetCurrentDirectory(root);

            foreach (string rawLine in File.ReadLines(oraclePath)) {
                string line = rawLine.TrimEnd('\r', '\n');
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
                    case "S":
                    case "I":
                    case "Ip":
                    case "Id":
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

            Console.WriteLine("StreamCheck: records " + checkedRecords + " input " + inputRecords
                + " mismatches " + mismatches);
            Console.WriteLine(mismatches == 0 ? "ALL MATCH" : "FAIL");
            return mismatches == 0 ? 0 : 1;
        }

        static string Truncate(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "...";

        static string Render(int fi, string[] t)
        {
            switch (t[0]) {
                case "S": return RenderS(fi, t);
                case "I": return StoreInput(fi, t);
                case "Ip": return StorePath(fi, t);
                case "Id": return StorePrefix(fi, t);
                case "A": return RenderA(fi, t);
                case "C": return RenderC(fi, t);
                case "K": return RenderK(fi, t);
                case "M": return RenderM(fi, t);
                case "F": return RenderF(fi, t);
                case "R": return RenderR(fi, t);
                case "H": return RenderH(fi, t);
                case "Q": return RenderQ(fi, t);
                case "P": return RenderP(fi, t);
                case "V": return RenderV(fi, t);
                default: throw new Exception("unknown record " + t[0]);
            }
        }

        // ------------------------------------------------------------------
        // Input records
        // ------------------------------------------------------------------

        static byte[] WholeFile(int fi)
        {
            if (wholeFiles[fi] == null) {
                string native = NativePath(corpus[fi]);
                wholeFiles[fi] = File.ReadAllBytes(native);
            }
            return wholeFiles[fi];
        }

        static string NativePath(string rawPath)
        {
            string utf16 = UfbxiStreamOpen.PathToUtf16(rawPath, rawPath.Length);
            return (utf16 ?? rawPath).Replace('/', Path.DirectorySeparatorChar);
        }

        static byte[] BuildPayload(VariantInput v, byte[] whole)
        {
            switch (v.Payload) {
                case PK_WHOLE: {
                    byte[] b = new byte[whole.Length];
                    Array.Copy(whole, b, b.Length);
                    return b;
                }
                case PK_HEAD: {
                    int n = v.PayloadArg < whole.Length ? v.PayloadArg : whole.Length / 2;
                    byte[] b = new byte[n];
                    Array.Copy(whole, b, n);
                    return b;
                }
                case PK_GARBAGE:
                    return Encoding.ASCII.GetBytes(
                        "\x01\x02\x03 not an fbx file at all, no..");
                case PK_EMPTY:
                    return new byte[0];
                case PK_ZEROS:
                    return new byte[v.PayloadArg];
                case PK_ASCII_HEAD:
                    return Encoding.ASCII.GetBytes("; FBX 7.5.0 project file\n\n");
                default:
                    return new byte[0];
            }
        }

        // S <fi> <vi> <kind> <arg> <len> <hash>
        static string RenderS(int fi, string[] t)
        {
            int vi = VI(t);
            // The oracle emits `S` (the input digest) BEFORE `I` (the option vector), so this is
            // the first record of a variant: build the payload from the `S` record itself, which
            // carries exactly what build_payload() needs (kind, arg). `I` then replaces it with
            // the full VariantInput and rebuilds the same bytes.
            VariantInput v = inputs[fi, vi];
            if (v == null) {
                v = new VariantInput();
                v.Payload = Num(t, 3);
                v.PayloadArg = Num(t, 4);
                v.PayloadBytes = BuildPayload(v, WholeFile(fi));
                inputs[fi, vi] = v;
            }
            byte[] payload = v.PayloadBytes;
            StringBuilder sb = new StringBuilder();
            sb.Append("S ").Append(fi).Append(' ').Append(vi);
            sb.Append(' ').Append(v.Payload).Append(' ').Append(v.PayloadArg);
            sb.Append(' ').Append(payload.Length);
            Z(sb, HBytes(FnvBasis, payload));
            return sb.ToString();
        }

        static int VI(string[] t) => int.Parse(t[2], CultureInfo.InvariantCulture);

        // I <fi> <vi> <mode> <pl> <plArg> <plLen> <prefix> <optsNull> <noCopy> <closeCb> <ctx>
        //   <mutate> <script> <dirty> <errNull> <path> <nulTerm> <progress> <readBuf>
        //   <cbFail> <defaultCb> <withDefault>
        static string StoreInput(int fi, string[] t)
        {
            int vi = VI(t);
            VariantInput v = new VariantInput();
            v.Mode = Num(t, 3);
            v.Payload = Num(t, 4);
            v.PayloadArg = Num(t, 5);
            v.PayloadLen = Num(t, 6);
            v.Prefix = Num(t, 7);
            v.OptsNull = Num(t, 8);
            v.NoCopy = Num(t, 9);
            v.CloseCb = Num(t, 10);
            v.Ctx = Num(t, 11);
            v.Mutate = Num(t, 12);
            v.Script = Num(t, 13);
            v.Dirty = Num(t, 14);
            v.ErrNull = Num(t, 15);
            v.Path = Num(t, 16);
            v.PathLen = Num(t, 17);
            v.NulTerm = Num(t, 18);
            v.Progress = Num(t, 19);
            v.ReadBuf = Num(t, 20);
            v.CbFail = Num(t, 21);
            v.DefaultCb = Num(t, 22);
            v.WithDefault = Num(t, 23);
            v.PayloadBytes = BuildPayload(v, WholeFile(fi));
            inputs[fi, vi] = v;
            StringBuilder sb = new StringBuilder();
            sb.Append("I ").Append(fi).Append(' ').Append(vi);
            for (int i = 3; i <= 23; i++) sb.Append(' ').Append(t[i]);
            return sb.ToString();
        }

        static int Num(string[] t, int i) => int.Parse(t[i], CultureInfo.InvariantCulture);

        static string StorePath(int fi, string[] t)
        {
            int vi = VI(t);
            inputs[fi, vi].PathBytes = ParseHex(t[3], t[4]);
            return "Ip " + fi + " " + vi + " " + t[3] + " " + t[4];
        }

        static string StorePrefix(int fi, string[] t)
        {
            int vi = VI(t);
            inputs[fi, vi].PrefixBytes = ParseHexBytes(t[3], t[4]);
            return "Id " + fi + " " + vi + " " + t[3] + " " + t[4];
        }

        // ------------------------------------------------------------------
        // The call
        // ------------------------------------------------------------------

        static Ctx EnsureVariant(int fi, int vi)
        {
            Ctx c = cache[fi, vi];
            if (c != null) return c;
            VariantInput v = inputs[fi, vi];
            if (v == null) throw new Exception("no input for variant " + vi + " of file " + fi);
            c = new Ctx();
            cache[fi, vi] = c;

            string rel = corpus[fi];
            byte[] payload = v.PayloadBytes;
            int pfx = v.PrefixBytes != null ? v.PrefixBytes.Length : 0;

            UfbxError error = new UfbxError();
            if (v.Dirty != 0) {
                error.Type = UfbxErrorType.TruncatedFile;
                error.Description = "Stale description";
                error.Info = "stale info";
                error.InfoLength = 10;
            }
            c.Error = v.ErrNull != 0 ? null : error;

            UfbxLoadOpts lopts = GoldenLoadOpts(v, c, rel);

            switch (v.Mode) {
                case SM_MEM_REAL:
                case SM_MEM_ONLY: {
                    // C hands `open_memory()` the payload from the prefix onwards. With no prefix
                    // the port gets the array itself, which is what makes `no_copy` aliasing (and
                    // therefore the `mutate` variants) observable.
                    byte[] memData = payload;
                    int memSize = payload.Length;
                    if (pfx > 0) {
                        memSize = payload.Length - pfx;
                        memData = new byte[memSize];
                        Array.Copy(payload, pfx, memData, 0, memSize);
                    }

                    UfbxOpenMemoryOpts mopts = new UfbxOpenMemoryOpts();
                    mopts.NoCopy = v.NoCopy != 0;
                    if (v.CloseCb != 0) {
                        mopts.CloseCb = new UfbxCloseMemoryCb();
                        mopts.CloseCb.Fn = CloseMemoryCb;
                        mopts.CloseCb.User = c;
                    }

                    UfbxInputStream mem;
                    bool ok;
                    if (v.Ctx != 0) {
                        ok = UfbxApi.OpenMemoryCtx(out mem, (IntPtr) 0, memData, memSize,
                            v.OptsNull != 0 ? null : mopts, c.Error);
                    } else {
                        ok = UfbxApi.OpenMemory(out mem, memData, memSize,
                            v.OptsNull != 0 ? null : mopts, c.Error);
                    }
                    c.Wired = mem != null ? 1 : 0;
                    c.Ok = ok;

                    // C flips a byte of the CALLER's buffer *after* the stream was opened.
                    if (v.Mutate != 0 && payload.Length > 4) payload[3] ^= 0x55;
                    if (!ok) return c;

                    LogStream log = new LogStream(mem, c);
                    if (v.Mode == SM_MEM_ONLY) {
                        // The probe is the whole test: `A` reports open_memory()'s own return
                        // value, not a scene, so `Ok` must stay as the ABI left it.
                        Probe(log);
                        return c;
                    }
                    if (pfx > 0) c.Scene = UfbxApi.LoadStreamPrefix(log, v.PrefixBytes, pfx, lopts, c.Error);
                    else c.Scene = UfbxApi.LoadStream(log, lopts, c.Error);
                    c.Ok = c.Scene != null;
                    return c;
                }
                case SM_FILE_REAL:
                case SM_FILE_ONLY: {
                    UfbxOpenFileOpts fopts = new UfbxOpenFileOpts();
                    fopts.FilenameNullTerminated = v.NulTerm != 0;

                    UfbxInputStream file;
                    bool ok;
                    if (v.Ctx != 0) {
                        ok = UfbxApi.OpenFileCtx(out file, (IntPtr) 0, v.PathBytes, v.PathLen,
                            v.OptsNull != 0 ? null : fopts, c.Error);
                    } else {
                        ok = UfbxApi.OpenFile(out file, v.PathBytes, v.PathLen,
                            v.OptsNull != 0 ? null : fopts, c.Error);
                    }
                    c.Wired = file != null ? 1 : 0;
                    c.Ok = ok;
                    if (!ok) return c;

                    LogStream log = new LogStream(file, c);
                    if (v.Mode == SM_FILE_ONLY) {
                        Probe(log);
                        return c;
                    }
                    c.Scene = UfbxApi.LoadStream(log, lopts, c.Error);
                    c.Ok = c.Scene != null;
                    return c;
                }
                case SM_STDIO: {
                    FileStream fs = null;
                    try {
                        fs = new FileStream(NativePath(rel), FileMode.Open, FileAccess.Read);
                    } catch (Exception) {
                        c.Ok = false;
                        return c;
                    }
                    try {
                        if (pfx > 0) {
                            byte[] tmp = new byte[pfx];
                            int got = fs.Read(tmp, 0, pfx);
                            if (got < pfx) Array.Clear(tmp, got, pfx - got);
                            c.Scene = UfbxApi.LoadStdioPrefix(fs, tmp, pfx, lopts, c.Error);
                        } else {
                            c.Scene = UfbxApi.LoadStdio(fs, lopts, c.Error);
                        }
                        c.Ok = c.Scene != null;
                        c.Cursor = fs.Position;
                    } finally {
                        fs.Dispose();
                    }
                    c.Logged = false;
                    return c;
                }
                case SM_STDIO_NULL: {
                    // `if (!file_void) return NULL;` (ufbx.c:30544): no load runs at all, so the
                    // caller's dirty error must come back untouched.
                    c.Scene = UfbxApi.LoadStdio(null, lopts, c.Error);
                    c.Ok = c.Scene != null;
                    c.Logged = false;
                    return c;
                }
                case SM_SCRIPT: {
                    ScriptStream script = new ScriptStream(payload, payload.Length, pfx, v.Script);
                    LogStream log = new LogStream(script, c);
                    if (pfx > 0) c.Scene = UfbxApi.LoadStreamPrefix(log, v.PrefixBytes, pfx, lopts, c.Error);
                    else c.Scene = UfbxApi.LoadStream(log, lopts, c.Error);
                    c.Ok = c.Scene != null;
                    return c;
                }
                default: {
                    c.CbFail = v.CbFail != 0;
                    if (v.DefaultCb != 0) lopts.OpenFileCb.Fn = UfbxiLoad.DefaultOpenFile;
                    else if (v.Mode == SM_LOAD_FILE_CB) lopts.OpenFileCb.Fn = MyOpenFile;
                    lopts.OpenFileCb.User = c;
                    lopts.OpenMainFileWithDefault = v.WithDefault != 0;
                    c.Scene = UfbxApi.LoadFile(v.PathBytes, lopts, c.Error);
                    c.Ok = c.Scene != null;
                    c.Logged = false;
                    return c;
                }
            }
        }

        // The fixed *_ONLY probe: the four callbacks with no loader in between.
        static void Probe(UfbxInputStream s)
        {
            byte[] tmp = new byte[64];
            s.Read(tmp, 0, 4);
            s.Read(tmp, 0, 9);
            if (s.CanSkip) s.Skip(3);
            s.Size();
            s.Read(tmp, 0, 4);
            s.Close();
        }

        static UfbxLoadOpts GoldenLoadOpts(VariantInput v, Ctx c, string rel)
        {
            UfbxLoadOpts o = new UfbxLoadOpts();
            o.LoadExternalFiles = true;
            o.IgnoreMissingExternalFiles = true;
            o.EvaluateCaches = true;
            o.EvaluateSkinning = true;
            o.TargetAxes = UfbxCoordinateAxes.RightHandedYUp;
            o.TargetUnitMeters = 1.0;
            o.Filename = rel;
            if (v.Progress != 0) {
                o.ProgressCb = new UfbxProgressCb();
                o.ProgressCb.Fn = ProgressCb;
                o.ProgressCb.User = c;
                if (v.Progress >= 2) o.ProgressIntervalHint = 256;
            }
            if (v.ReadBuf != 0) o.ReadBufferSize = v.ReadBuf;
            return o;
        }

        // -- Callbacks

        static void CloseMemoryCb(object user, byte[] data, int dataSize)
        {
            Ctx c = (Ctx) user;
            c.CloseN++;
            c.CloseSize = dataSize;
            c.CloseBytes = new byte[dataSize];
            if (dataSize > 0 && data != null) Array.Copy(data, c.CloseBytes, dataSize);
            c.CloseHash = HBytes(FnvBasis, c.CloseBytes);
        }

        static UfbxProgressResult ProgressCb(object user, UfbxProgress progress)
        {
            ((Ctx) user).Prog.Add(new ulong[] { progress.BytesRead, progress.BytesTotal });
            return UfbxProgressResult.Continue;
        }

        // The deferred-open branch: record what C's `ufbxi_open_file()` (ufbx.c:16652-16667) hands
        // the callback, then delegate to the port's `ufbx_default_open_file()`.
        static bool MyOpenFile(object user, UfbxInputStream stream, string path, int pathLength,
            UfbxOpenFileInfo info)
        {
            Ctx c = (Ctx) user;
            c.CbN++;
            c.CbPathLen = pathLength;
            c.CbPath = path;
            c.CbType = (int) info.Type;
            c.CbOrig = info.OriginalFilename != null
                ? UfbxiRawStr.FromBytes(info.OriginalFilename, 0, info.OriginalFilename.Length) : "";
            c.CbOrigLen = info.OriginalFilename != null ? info.OriginalFilename.Length : 0;
            if (c.CbFail) return false;
            if (!UfbxApi.DefaultOpenFile(user, out UfbxInputStream input, path, pathLength, info)) {
                return false;
            }
            // C writes the callbacks into the `ufbx_stream` the loader handed over; the port's
            // holder takes the stream instance instead.
            ((UfbxiLoad.UfbxiOpenFileStream) stream).Attach(input);
            return true;
        }

        // ------------------------------------------------------------------
        // Records
        // ------------------------------------------------------------------

        // A <fi> <vi> <ok> <wired> <type> <dlen> <dx> <ilen> <ix>
        static string RenderA(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("A ").Append(fi).Append(' ').Append(vi);
            sb.Append(c.Ok ? " 1 " : " 0 ");
            sb.Append(c.Wired >= 0 ? c.Wired.ToString(CultureInfo.InvariantCulture) : "-");
            if (c.Error != null) PErr(sb, c.Error);
            else sb.Append(" - - - - -");
            return sb.ToString();
        }

        // C <fi> <vi> <type> <dlen> <dx> <ilen> <ix> | `-`
        static string RenderC(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("C ").Append(fi).Append(' ').Append(vi);
            if (c.Error != null) PErr(sb, c.Error);
            else sb.Append(" -");
            return sb.ToString();
        }

        // K <fi> <vi> <cursor>
        static string RenderK(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            return "K " + fi + " " + vi + " " + c.Cursor.ToString(CultureInfo.InvariantCulture);
        }

        // M <fi> <vi> <nclose> <dlen> <dx> <size> <hash>
        static string RenderM(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("M ").Append(fi).Append(' ').Append(vi).Append(' ').Append(c.CloseN);
            if (c.CloseN > 0) {
                PBytes(sb, c.CloseBytes, c.CloseSize);
                sb.Append(' ').Append(c.CloseSize);
            } else {
                sb.Append(" 0 - 0");
            }
            Z(sb, c.CloseHash);
            return sb.ToString();
        }

        // F <fi> <vi> <pathLen> <len> <hex> <type> <origLen> <ox>
        static string RenderF(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("F ").Append(fi).Append(' ').Append(vi);
            if (c.CbN > 0) {
                sb.Append(' ').Append(c.CbPathLen);
                PBytes(sb, UfbxiRawStr.ToBytes(c.CbPath ?? ""), c.CbPathLen);
                sb.Append(' ').Append(c.CbType);
                PBytes(sb, UfbxiRawStr.ToBytes(c.CbOrig ?? ""), c.CbOrigLen);
            } else {
                sb.Append(" -1 0 - 0 0 -");
            }
            return sb.ToString();
        }

        // R <fi> <vi> <nread> <nskip> <nsize> <nclose>
        static string RenderR(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("R ").Append(fi).Append(' ').Append(vi);
            if (!c.Logged) { sb.Append(" -1 -1 -1 -1"); return sb.ToString(); }
            sb.Append(' ').Append(c.NRead).Append(' ').Append(c.NSkip)
              .Append(' ').Append(c.NSize).Append(' ').Append(c.NClose);
            return sb.ToString();
        }

        // H <fi> <vi> <calls> <hash>
        static string RenderH(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("H ").Append(fi).Append(' ').Append(vi);
            if (!c.Logged) { sb.Append(" -1 -"); return sb.ToString(); }
            sb.Append(' ').Append(c.Io.Count);
            Z(sb, c.IoHash());
            return sb.ToString();
        }

        // Q <fi> <vi> <ix> <kind> <arg> <ret>
        static string RenderQ(int fi, string[] t)
        {
            int vi = VI(t);
            int ix = int.Parse(t[3], CultureInfo.InvariantCulture);
            Ctx c = EnsureVariant(fi, vi);
            IoRec r = ix < c.Io.Count ? c.Io[ix] : null;
            StringBuilder sb = new StringBuilder();
            sb.Append("Q ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ix);
            if (r == null) { sb.Append(" -1 0 0"); return sb.ToString(); }
            sb.Append(' ').Append(r.Kind).Append(' ').Append(r.Arg).Append(' ').Append(r.Ret);
            return sb.ToString();
        }

        // P <fi> <vi> <ix> <bytesRead> <bytesTotal>
        static string RenderP(int fi, string[] t)
        {
            int vi = VI(t);
            int ix = int.Parse(t[3], CultureInfo.InvariantCulture);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("P ").Append(fi).Append(' ').Append(vi).Append(' ').Append(ix);
            ulong[] p = ix < c.Prog.Count ? c.Prog[ix] : null;
            if (p == null) { sb.Append(" 0 0"); return sb.ToString(); }
            sb.Append(' ').Append(p[0]).Append(' ').Append(p[1]);
            return sb.ToString();
        }

        // V <fi> <vi> <hash>
        static string RenderV(int fi, string[] t)
        {
            int vi = VI(t);
            Ctx c = EnsureVariant(fi, vi);
            StringBuilder sb = new StringBuilder();
            sb.Append("V ").Append(fi).Append(' ').Append(vi);
            if (c.Scene == null) { sb.Append(" NONE"); return sb.ToString(); }
            Z(sb, UfbxHashScene.HashScene(c.Scene));
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Formatting (mirrors of the oracle's pstr()/perr()/phex()/pz())
        // ------------------------------------------------------------------

        static void Z(StringBuilder sb, ulong v)
        {
            sb.Append(' ').Append(v.ToString("x16", CultureInfo.InvariantCulture));
        }

        // ` <length> <hex>` -- an empty run is the single token `-`.
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
            sb.Append(' ').Append((int) e.Type);
            PBytes(sb, UfbxiRawStr.ToBytes(e.Description ?? ""),
                UfbxiRawStr.ToBytes(e.Description ?? "").Length);
            sb.Append(' ').Append(e.InfoLength).Append(' ');
            byte[] info = UfbxiRawStr.ToBytes(e.Info ?? "");
            if (e.InfoLength <= 0) { sb.Append('-'); return; }
            for (int i = 0; i < e.InfoLength && i < info.Length; i++) {
                sb.Append(info[i].ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        static byte[] ParseHexBytes(string lenToken, string hexToken)
        {
            if (hexToken == "-") return new byte[0];
            int len = int.Parse(lenToken, CultureInfo.InvariantCulture);
            byte[] b = new byte[len];
            for (int i = 0; i < len; i++) {
                b[i] = byte.Parse(hexToken.Substring(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
            }
            return b;
        }

        static string ParseHex(string lenToken, string hexToken)
        {
            byte[] b = ParseHexBytes(lenToken, hexToken);
            return UfbxiRawStr.FromBytes(b, 0, b.Length);
        }

        static ulong HBytes(ulong h, byte[] b)
        {
            if (b == null) return h;
            for (int i = 0; i < b.Length; i++) h = (h ^ b[i]) * FnvPrime;
            return h;
        }

        static ulong HU64(ulong h, ulong v)
        {
            for (int k = 0; k < 8; k++) h = (h ^ ((v >> (k * 8)) & 0xff)) * FnvPrime;
            return h;
        }
    }
}
