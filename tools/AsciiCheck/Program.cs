// Isolated verifier for the ported ASCII FBX tokenizer and array-element parsers
// (src/Ufbx.NET/Parse/Ascii.cs + AsciiState.cs, ufbx.c:9400-10220).
//
// This version compiles against the GENUINE Parse/Stream.cs (UfbxiStream),
// Parse/InputStreams.cs (UfbxMemoryInputStream / UfbxFileInputStream) and
// Parse/UfbxiContext.cs from the library -- there are no test-local stand-ins. The ASCII
// reader therefore runs on the real IO window (Buffer/BeginIndex/Position/Remaining/
// YieldSize/DataOffset/ReadBuffer/ReadBufferSize), the real report-progress path, and the
// real string pool / tmp_stack / exporter state on UfbxiContext.
//
// What it checks:
//  (R) REPLAY-AND-DIFF against the C oracle tools/ascii_oracle.txt: every snippet is run
//      through ufbxi_ascii_next_token on a real ufbxi_context, under five IO geometries
//      (memory load, three chunked streams, a short-read stream). The token signature, the
//      END-vs-EOF outcome and the failure/description text are compared byte-for-byte. This
//      is the core: it exercises the real windowing / src_yield / retain semantics the
//      previous stand-in harness never drove.
//  (I) the SAME replay driven through the LIBRARY's own input implementations --
//      UfbxMemoryInputStream and a file-backed UfbxFileInputStream over a real temp .fbx --
//      under the three deterministic streaming geometries, cross-checked against the oracle.
//  (A) array element bytes (eager fast readers + deferred array task) against values computed
//      independently (long/double.Parse, explicit inf/nan/-0 bit patterns) and the C caller's
//      eager/deferred split, now over the real window,
//  (W) windowing edge cases that only the real stream exposes: retain_buf across refills,
//      short reads via a real UfbxInputStream, and progress CANCELLATION, which with the real
//      UfbxiStream surfaces as a thrown UfbxError (not the stand-in's silent '\0'),
//  (D) deferred array task failure modes.
//
// Run: dotnet run --project tools/AsciiCheck -c Release -- [path/to/ascii_oracle.txt]

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace Ufbx.NET.Tests
{
    static class AsciiCheck
    {
        static int s_Pass;
        static int s_Fail;
        static bool s_Verbose;

        static void Ok(bool cond, string what)
        {
            if (cond) {
                s_Pass++;
            } else {
                s_Fail++;
                Console.WriteLine("  FAIL: " + what);
            }
        }

        static byte[] B(string s)
        {
            byte[] b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = unchecked((byte)s[i]);
            return b;
        }

        static string Str(byte[] b, int offset, int length)
        {
            char[] cs = new char[length];
            for (int i = 0; i < length; i++) cs[i] = (char)b[offset + i];
            return new string(cs);
        }

        static byte At(byte[] buf, int index) => (uint)index < (uint)buf.Length ? buf[index] : (byte)0;

        static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);
        static long Bits(float v) => unchecked((long)BitConverter.SingleToInt32Bits(v));

        // -------------------------------------------------------------------
        // A real UfbxInputStream that counts reads and can force short reads, so the ASCII
        // reader's refill path runs on the genuine stream abstraction (UfbxiStream.Input).
        // -------------------------------------------------------------------
        sealed class CountedMemoryStream : UfbxInputStream
        {
            readonly byte[] data;
            public int Pos;
            public int Calls;
            public int ChunkLimit;   // 0 = hand back the full request; else cap at this many

            public CountedMemoryStream(byte[] data, int pos) { this.data = data; Pos = pos; }

            // C: ufbxi_memory_read() -- returns min(count, left), EOF by 0.
            public override int Read(byte[] buffer, int offset, int count)
            {
                Calls++;
                if (ChunkLimit != 0 && count > ChunkLimit) count = ChunkLimit;
                int left = data.Length - Pos;
                int n = count < left ? count : left;
                if (n > 0) Array.Copy(data, Pos, buffer, offset, n);
                Pos += n;
                return n;
            }

            public override ulong Size() => (ulong)data.Length;
        }

        // -------------------------------------------------------------------
        // Harness: build a tokenizer over the REAL stream/context types, seeded exactly the
        // way ufbxi_begin_parse()'s ASCII branch seeds uc->ascii (ufbx.c:11218-11221) after
        // the loader has loaded the initial window. `streaming` selects read_fn != NULL
        // (chunked) vs the memory-load path (read_fn == NULL, whole buffer in the window).
        // -------------------------------------------------------------------
        sealed class Harness
        {
            public UfbxiAscii Ascii;
            public UfbxiStream Stream;
            public UfbxiContext Ctx;
            public CountedMemorySource Source;   // progress + read accounting probe
            public byte[] Data;
        }

        // Wraps the counted input stream + progress callback bookkeeping.
        sealed class CountedMemorySource
        {
            public CountedMemoryStream Input;
            public int ProgressReports;
            public bool Cancel;        // when true the progress cb returns Cancel
            public int CancelAfter = int.MaxValue;   // cancel after this many reports
        }

        static Harness MakeStreaming(byte[] data, int readBufferSize, ulong interval, int chunkLimit = 0)
        {
            return MakeCore(data, readBufferSize, interval, true, chunkLimit, 0);
        }

        static Harness MakeMemory(byte[] data, ulong interval = ulong.MaxValue, int windowBytes = 0)
        {
            return MakeCore(data, 0x4000, interval, false, 0, windowBytes);
        }

        static Harness MakeCore(byte[] data, int readBufferSize, ulong interval, bool streaming,
            int chunkLimit, int windowBytes)
        {
            UfbxiStream stream = new UfbxiStream();
            UfbxiContext ctx = new UfbxiContext();
            ctx.DoubleParseFlags = UfbxiAscii.ParseDoubleInitFlags;
            // Real string pool so the name/string interning hooks are live.
            ctx.InitStringPool(new UfbxError(), 1024);

            UfbxiAscii ua = new UfbxiAscii();
            ua.Ctx = ctx;
            ua.Stream = stream;
            ctx.Stream = stream;

            CountedMemorySource src = new CountedMemorySource();

            int len = data.Length;
            int first;
            if (streaming) {
                first = readBufferSize < len ? readBufferSize : len;
                byte[] readBuf = new byte[readBufferSize];
                if (first > 0) Array.Copy(data, 0, readBuf, 0, first);

                src.Input = new CountedMemoryStream(data, first);
                src.Input.ChunkLimit = chunkLimit;

                stream.Input = src.Input;
                stream.OptReadBufferSize = readBufferSize;
                stream.ReadBuffer = readBuf;
                stream.ReadBufferSize = readBufferSize;
                stream.Buffer = readBuf;
                stream.BeginIndex = 0;
                stream.Position = 0;
                stream.Remaining = first;
                stream.YieldSize = 0;
            } else {
                // Memory-load path: read_fn == NULL, the initial window is the whole buffer
                // (windowBytes>0 shrinks it only for targeted windowing tests).
                first = windowBytes != 0 && windowBytes < len ? windowBytes : len;
                byte[] window = new byte[first];
                Array.Copy(data, 0, window, 0, first);
                stream.Input = null;
                stream.Buffer = window;
                stream.BeginIndex = 0;
                stream.Position = 0;
                stream.Remaining = first;
                stream.YieldSize = 0;
            }

            stream.ProgressInterval = interval;

            // Install a real progress cb so report_progress is exercised; it counts and can
            // cancel (which the real UfbxiStream turns into a thrown UfbxParseError, not the
            // '\0' the stand-in returned).
            UfbxProgressCb cb = new UfbxProgressCb();
            cb.Fn = (user, prog) => {
                src.ProgressReports++;
                if (src.Cancel && src.ProgressReports >= src.CancelAfter) {
                    return UfbxProgressResult.Cancel;
                }
                return UfbxProgressResult.Continue;
            };
            stream.ProgressCb = cb;

            ua.ResetFromStream(ctx, stream);
            return new Harness { Ascii = ua, Stream = stream, Ctx = ctx, Source = src, Data = data };
        }

        // Same streaming geometry as MakeStreaming, but the initial window is filled by a
        // genuine read through the LIBRARY's own UfbxInputStream implementation (the way
        // ufbxi_load_initial_data() does it), and every later refill goes through the same
        // real object. Used by (I) to cross-check UfbxMemoryInputStream and
        // UfbxFileInputStream against the C oracle.
        static Harness MakeRealStream(byte[] data, UfbxInputStream input, int readBufferSize, ulong interval)
        {
            UfbxiStream stream = new UfbxiStream();
            UfbxiContext ctx = new UfbxiContext();
            ctx.DoubleParseFlags = UfbxiAscii.ParseDoubleInitFlags;
            ctx.InitStringPool(new UfbxError(), 1024);

            UfbxiAscii ua = new UfbxiAscii();
            ua.Ctx = ctx;
            ua.Stream = stream;
            ctx.Stream = stream;

            byte[] readBuf = new byte[readBufferSize];
            int want = readBufferSize < data.Length ? readBufferSize : data.Length;
            int got = 0;
            while (got < want) {
                int g = input.Read(readBuf, got, want - got);
                if (g <= 0) break;
                got += g;
            }

            stream.Input = input;
            stream.OptReadBufferSize = readBufferSize;
            stream.ReadBuffer = readBuf;
            stream.ReadBufferSize = readBufferSize;
            stream.Buffer = readBuf;
            stream.BeginIndex = 0;
            stream.Position = 0;
            stream.Remaining = got;
            stream.YieldSize = 0;
            stream.ProgressInterval = interval;

            ua.ResetFromStream(ctx, stream);
            return new Harness { Ascii = ua, Stream = stream, Ctx = ctx, Source = null, Data = data };
        }

        // -------------------------------------------------------------------
        // (I) cross-check the LIBRARY real input implementations against the oracle:
        // UfbxMemoryInputStream (in-memory-backed) and UfbxFileInputStream (file-backed),
        // replayed under the three deterministic streaming geometries (modes 1..3).
        // -------------------------------------------------------------------
        static void TestRealInputStreams()
        {
            Console.WriteLine("[I] library UfbxInputStream implementations vs oracle");
            if (s_Sigs == null) {
                Console.WriteLine("  SKIP: oracle not loaded");
                return;
            }

            string tmp = Path.Combine(Path.GetTempPath(),
                "ufbx_ascii_check_" + Guid.NewGuid().ToString("N") + ".fbx");
            int cases = 0;
            try {
                foreach (KeyValuePair<int, byte[]> kv in s_SnipData) {
                    int snip = kv.Key;
                    byte[] data = kv.Value;
                    File.WriteAllBytes(tmp, data);

                    foreach (int mode in new int[] { 1, 2, 3 }) {
                        int buf;
                        ulong interval;
                        if (mode == 1) { buf = 0x4000; interval = 0x4000; }
                        else if (mode == 2) { buf = 7; interval = 7; }
                        else { buf = 64; interval = 3; }

                        {
                            UfbxMemoryInputStream mis = new UfbxMemoryInputStream(data, data.Length);
                            Harness h = MakeRealStream(data, mis, buf, interval);
                            ReplayOn(h, snip, mode, "mem-stream snip=" + snip + " mode=" + mode);
                        }
                        {
                            UfbxFileInputStream fis = new UfbxFileInputStream(tmp);
                            Harness h = MakeRealStream(data, fis, buf, interval);
                            ReplayOn(h, snip, mode, "file-stream snip=" + snip + " mode=" + mode);
                            fis.Close();
                        }
                        cases += 2;
                    }
                }
                Console.WriteLine("  replayed " + cases + " real-stream (snippet,mode) cases"
                    + " over UfbxMemoryInputStream + UfbxFileInputStream");
            } finally {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }

        // -------------------------------------------------------------------
        // (R) Tokenizer replay-and-diff against the C oracle
        // -------------------------------------------------------------------

        // Build the token signature exactly as the oracle's sig_token() does. Each output char
        // is one raw byte (DOM 约定: 1 char == 1 byte), so the caller hex-encodes it.
        static string SigToken(StringBuilder sb, UfbxiAsciiToken tok)
        {
            sb.Append(tok.Type);
            switch (tok.Type) {
            case UfbxiAscii.AsciiName:
                sb.Append('/');
                for (int i = 0; i < tok.NameLen; i++) sb.Append((char)At(tok.StrData, i));
                sb.Append('/');
                sb.Append(tok.NameLen.ToString(CultureInfo.InvariantCulture));
                break;
            case UfbxiAscii.AsciiBareWord:
            case UfbxiAscii.AsciiString:
                sb.Append('/');
                for (int i = 0; i < tok.StrLen; i++) sb.Append((char)At(tok.StrData, i));
                break;
            case UfbxiAscii.AsciiInt:
                sb.Append('/');
                sb.Append(tok.Negative ? '-' : '+');
                sb.Append('/');
                sb.Append(tok.I64.ToString(CultureInfo.InvariantCulture));
                break;
            case UfbxiAscii.AsciiFloat:
                sb.Append('/');
                sb.Append(Bits(tok.F64).ToString("x16", CultureInfo.InvariantCulture));
                break;
            default:
                break;
            }
            sb.Append(' ');
            return null;
        }

        static string HexOf(string raw)
        {
            StringBuilder sb = new StringBuilder(raw.Length * 2);
            for (int i = 0; i < raw.Length; i++) sb.Append(((byte)raw[i]).ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static byte[] FromHex(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) {
                b[i] = (byte)((HexVal(hex[2 * i]) << 4) | HexVal(hex[2 * i + 1]));
            }
            return b;
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }

        sealed class OracleSig
        {
            public int Snip;
            public int Mode;
            public bool Ended;
            public string HexSig;
        }

        static Harness MakeForMode(byte[] data, int mode)
        {
            // Mirrors tools/ascii_oracle.c g_modes[] exactly.
            switch (mode) {
            case 0: return MakeMemory(data);
            case 1: return MakeStreaming(data, 0x4000, 0x4000);
            case 2: return MakeStreaming(data, 7, 7);
            case 3: return MakeStreaming(data, 64, 3);
            case 4: return MakeStreaming(data, 8, 0x4000, 3);
            default: return null;
            }
        }

        // A (snip, mode) replay case: input bytes + expected signature + optional error text.
        sealed class ReplayCase
        {
            public int Snip;
            public int Mode;
            public byte[] Data;
            public bool Ended;
            public string HexSig;
            public bool HasError;
            public string Desc;   // hex
            public string Cond;   // hex
        }

        static long Key(int snip, int mode) { return ((long)snip << 8) | (uint)mode; }

        static Dictionary<int, byte[]> s_SnipData;
        static Dictionary<long, OracleSig> s_Sigs;
        static Dictionary<long, ReplayCase> s_Cases;

        // Load tools/ascii_oracle.txt (S / SIG / ERR records) into the shared tables.
        static bool LoadOracle(string oraclePath)
        {
            if (!File.Exists(oraclePath)) return false;
            s_SnipData = new Dictionary<int, byte[]>();
            s_Sigs = new Dictionary<long, OracleSig>();
            s_Cases = new Dictionary<long, ReplayCase>();

            foreach (string line in File.ReadLines(oraclePath)) {
                if (line.Length == 0) continue;
                string[] f = line.Split(' ');
                switch (f[0]) {
                case "S": {
                    int snip = int.Parse(f[1], CultureInfo.InvariantCulture);
                    s_SnipData[snip] = FromHex(f[3]);
                    break;
                }
                case "SIG": {
                    int snip = int.Parse(f[1], CultureInfo.InvariantCulture);
                    int mode = int.Parse(f[2], CultureInfo.InvariantCulture);
                    OracleSig s = new OracleSig();
                    s.Snip = snip; s.Mode = mode;
                    s.Ended = f[3] == "1";
                    s.HexSig = f[5];
                    s_Sigs[Key(snip, mode)] = s;
                    break;
                }
                case "ERR": {
                    int snip = int.Parse(f[1], CultureInfo.InvariantCulture);
                    int mode = int.Parse(f[2], CultureInfo.InvariantCulture);
                    if (s_Sigs.TryGetValue(Key(snip, mode), out OracleSig base0)) {
                        ReplayCase rc = new ReplayCase();
                        rc.Snip = snip; rc.Mode = mode;
                        rc.Data = s_SnipData[snip];
                        rc.Ended = base0.Ended; rc.HexSig = base0.HexSig;
                        rc.HasError = true; rc.Desc = f[3]; rc.Cond = f[4];
                        s_Cases[Key(snip, mode)] = rc;
                    }
                    break;
                }
                }
            }
            return true;
        }

        // Run `h`'s tokenizer to exhaustion and diff against the oracle records for
        // (snip, mode): signature bytes, END-vs-EOF outcome, throw-vs-not-throw, error text.
        static void ReplayOn(Harness h, int snip, int mode, string tag)
        {
            if (!s_Sigs.TryGetValue(Key(snip, mode), out OracleSig os)) {
                Ok(false, tag + ": no oracle SIG record");
                return;
            }
            s_Cases.TryGetValue(Key(snip, mode), out ReplayCase rc);

            StringBuilder sb = new StringBuilder();
            bool ended = false;
            bool threw = false;
            string message = null;
            try {
                h.Ascii.NextToken();
                for (int i = 0; i < 8192; i++) {
                    if (h.Ascii.Token.Type == UfbxiAscii.AsciiEnd) { ended = true; break; }
                    SigToken(sb, h.Ascii.Token);
                    h.Ascii.NextToken();
                }
            } catch (UfbxParseError e) {
                threw = true;
                message = e.Message;
            }

            string gotHex = HexOf(sb.ToString());

            bool sigOk = gotHex == os.HexSig;
            Ok(sigOk, tag + " token signature matches (got " + gotHex.Substring(0, Math.Min(48, gotHex.Length))
                + "... want " + os.HexSig.Substring(0, Math.Min(48, os.HexSig.Length)) + "...)");
            Ok(ended == os.Ended, tag + " END/EOF flag " + ended + " == " + os.Ended);

            if (rc != null && rc.HasError) {
                Ok(threw, tag + " tokenizer threw as the C oracle failed");
                if (threw) {
                    // The port throws the human description when C set one (a *_msg check),
                    // otherwise the stringified condition (a plain ufbxi_check).
                    string wantDesc = Str(FromHex(rc.Desc), 0, FromHex(rc.Desc).Length);
                    string wantCond = Str(FromHex(rc.Cond), 0, FromHex(rc.Cond).Length);
                    string want = wantDesc.Length > 0 ? wantDesc : wantCond;
                    Ok(message == want, tag + " error text matches: got '" + message + "' want '" + want + "'");
                }
            } else {
                Ok(!threw, tag + " tokenizer did not throw (C oracle completed)");
            }
        }

        static void TestOracleReplay(string oraclePath)
        {
            Console.WriteLine("[R] tokenizer replay-and-diff vs C oracle");
            if (!LoadOracle(oraclePath)) {
                Console.WriteLine("  SKIP: oracle file not found: " + oraclePath);
                return;
            }

            foreach (KeyValuePair<long, OracleSig> kv in s_Sigs) {
                OracleSig os = kv.Value;
                if (!s_SnipData.TryGetValue(os.Snip, out byte[] data)) continue;

                Harness h = MakeForMode(data, os.Mode);
                if (h == null) { Ok(false, "unknown mode " + os.Mode); continue; }

                ReplayOn(h, os.Snip, os.Mode, "snip=" + os.Snip + " mode=" + os.Mode);
            }
            Console.WriteLine("  replayed " + s_Sigs.Count + " (snippet,mode) cases");
        }

        // -------------------------------------------------------------------
        // (A) Array element bytes vs independently-computed expectations
        // -------------------------------------------------------------------

        static int F64ToI32(double v)
        {
            if (Math.Abs(v) <= (double)int.MaxValue) return (int)v;
            return v >= 0.0 ? int.MaxValue : int.MinValue;
        }

        static long F64ToI64(double v)
        {
            if (Math.Abs(v) <= (double)long.MaxValue) return (long)v;
            return v >= 0.0 ? long.MaxValue : long.MinValue;
        }

        sealed class ArrayParse
        {
            public byte[] Data;
            public int NumValues;
            public int DeferredValues;
            public int ElemSize;
            public bool TaskOk;
            public string TaskError;
            public int SpanCount;
            public bool Retained;
        }

        static ArrayParse ParseArray(UfbxiAscii ua, char type, long count, bool defer, UfbxiAsciiBuf tmpBuf)
        {
            int elemSize = type == 'i' || type == 'f' ? 4 : 8;
            ArrayParse res = new ArrayParse();
            res.ElemSize = elemSize;

            Ok(ua.Accept(UfbxiAscii.AsciiName), "array: property name accepted");
            Ok(ua.Accept('*'), "array: '*' accepted");
            Ok(ua.Accept(UfbxiAscii.AsciiInt), "array: element count accepted");
            Ok(ua.PrevToken.I64 == count, "array: declared count " + ua.PrevToken.I64 + " == " + count);
            Ok(ua.Accept('{'), "array: '{' accepted");
            Ok(ua.Accept(UfbxiAscii.AsciiName), "array: type name accepted");

            if (defer) {
                ua.StoreArray(tmpBuf);
                res.SpanCount = ua.TmpAsciiSpans.Count;
                res.Retained = ua.SrcIsRetained;
            }

            ua.Ctx.TmpStack.PushSizeZero(8, 1);

            int numValues = 0;
            for (;;) {
                int numRead = 0;
                if (type == 'f' || type == 'd') {
                    ua.ReadFloatArray(type, out numRead);
                } else if (type == 'i' || type == 'l') {
                    ua.ReadIntArray(type, out numRead);
                }
                numValues += numRead;

                if (ua.Accept(UfbxiAscii.AsciiInt)) {
                    PushPrevInt(ua, type);
                } else if (ua.Accept(UfbxiAscii.AsciiFloat)) {
                    PushPrevFloat(ua, type);
                } else if (ua.Accept(UfbxiAscii.AsciiBareWord)) {
                    PushPrevBareWord(ua, type);
                } else {
                    break;
                }

                numValues++;
                if (!ua.Accept(',')) break;
            }

            Ok(ua.Accept('}'), "array: closing '}' accepted");

            int deferred = defer ? (int)count - 1 : 0;
            res.NumValues = numValues;
            res.DeferredValues = deferred;
            res.Data = new byte[(numValues + deferred) * elemSize];
            if (numValues > 0) ua.Ctx.TmpStack.PopSize(elemSize, numValues, res.Data, 0);

            if (deferred > 0) {
                UfbxiAsciiArrayTask task = new UfbxiAsciiArrayTask();
                task.ArrData = res.Data;
                task.ArrDataOffset = numValues * elemSize;
                task.ArrType = type;
                task.ArrSize = deferred;
                task.Spans = ua.TmpAsciiSpans.ToArray();
                task.NumSpans = task.Spans.Length;
                task.Offset = 0;
                res.TaskOk = UfbxiAscii.ArrayTaskFn(task, out res.TaskError);
                ua.TmpAsciiSpans.Clear();
            }

            ua.Ctx.TmpStack.PopSize(8, 1, null, 0);
            return res;
        }

        static void PushPrevInt(UfbxiAscii ua, char type)
        {
            UfbxiAsciiToken tok = ua.PrevToken;
            long val = tok.I64;
            double fsign = val == 0 && tok.Negative ? -1.0 : 1.0;
            switch (type) {
            case 'i': { int p = ua.Ctx.TmpStack.PushSize(4, 1); BinaryPrimitives.WriteInt32LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), unchecked((int)val)); break; }
            case 'l': { int p = ua.Ctx.TmpStack.PushSize(8, 1); BinaryPrimitives.WriteInt64LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), val); break; }
            case 'f': { int p = ua.Ctx.TmpStack.PushSize(4, 1); BinaryPrimitives.WriteUInt32LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), UfbxBitUtil.SingleToBits((float)val * (float)fsign)); break; }
            case 'd': { int p = ua.Ctx.TmpStack.PushSize(8, 1); BinaryPrimitives.WriteUInt64LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), unchecked((ulong)UfbxBitUtil.BitsOf((double)val * (double)fsign))); break; }
            default: break;
            }
        }

        static void PushPrevFloat(UfbxiAscii ua, char type)
        {
            double val = ua.PrevToken.F64;
            switch (type) {
            case 'i': { int p = ua.Ctx.TmpStack.PushSize(4, 1); BinaryPrimitives.WriteInt32LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), F64ToI32(val)); break; }
            case 'l': { int p = ua.Ctx.TmpStack.PushSize(8, 1); BinaryPrimitives.WriteInt64LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), F64ToI64(val)); break; }
            case 'f': { int p = ua.Ctx.TmpStack.PushSize(4, 1); BinaryPrimitives.WriteUInt32LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), UfbxBitUtil.SingleToBits((float)val)); break; }
            case 'd': { int p = ua.Ctx.TmpStack.PushSize(8, 1); BinaryPrimitives.WriteUInt64LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), unchecked((ulong)UfbxBitUtil.BitsOf(val))); break; }
            default: break;
            }
        }

        // Test-local mirror of `ufbxi_parse_inf_nan()` (ufbx.c:1545-1599) for the caller's
        // BARE_WORD array branch; the real helper is private to Parse/Numeric.cs (not owned here).
        static bool TestParseInfNan(string s, out double result, out int pEnd)
        {
            result = 0.0;
            pEnd = 0;
            int p = 0;
            bool negative = false;
            if (p < s.Length && (s[p] == '+' || s[p] == '-')) {
                negative = s[p] == '-';
                p++;
            }
            uint topBits = 0;
            if (s.Length - p >= 3 && s[p] >= '0' && s[p] <= '9' && s[p + 1] == '.' && s[p + 2] == '#') {
                p += 3;
                if (ScanIgnorecase(s, p, "inf")) { p += 3; topBits = 0x7ff0; }
                else if (ScanIgnorecase(s, p, "nan") || ScanIgnorecase(s, p, "ind")) { p += 3; topBits = 0x7ff8; }
                else return false;
                while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            } else {
                if (ScanIgnorecase(s, p, "nan")) {
                    p += 3;
                    topBits = 0x7ff8;
                    if (p < s.Length && s[p] == '(') {
                        p++;
                        while (p < s.Length && s[p] != ')') {
                            char c = s[p];
                            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
                            p++;
                        }
                        if (p == s.Length) return false;
                        p++;
                    }
                } else if (ScanIgnorecase(s, p, "inf")) {
                    p += ScanIgnorecase(s, p + 3, "inity") ? 8 : 3;
                    topBits = 0x7ff0;
                }
            }
            pEnd = p;
            topBits |= negative ? 0x8000u : 0u;
            result = BitConverter.Int64BitsToDouble(unchecked((long)(((ulong)topBits) << 48)));
            return true;
        }

        static bool ScanIgnorecase(string s, int start, string word)
        {
            if (start + word.Length > s.Length) return false;
            for (int i = 0; i < word.Length; i++) {
                char a = s[start + i], b = word[i];
                if (a >= 'A' && a <= 'Z') a = (char)(a - 'A' + 'a');
                if (a != b) return false;
            }
            return true;
        }

        static void PushPrevBareWord(UfbxiAscii ua, char type)
        {
            UfbxiAsciiToken tok = ua.PrevToken;
            long val = 0;
            double valF = 0.0;
            if (tok.StrLen >= 1) {
                val = (sbyte)At(tok.StrData, 0);
                valF = (double)val;
                if (tok.StrLen > 1 && tok.StrLen < 64) {
                    string text = Str(tok.StrData, 0, tok.StrLen);
                    if (TestParseInfNan(text, out double infNan, out int end) && end == tok.StrLen) {
                        val = 0;
                        valF = infNan;
                    }
                }
            }
            switch (type) {
            case 'i': { int p = ua.Ctx.TmpStack.PushSize(4, 1); BinaryPrimitives.WriteInt32LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), unchecked((int)val)); break; }
            case 'l': { int p = ua.Ctx.TmpStack.PushSize(8, 1); BinaryPrimitives.WriteInt64LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), val); break; }
            case 'f': { int p = ua.Ctx.TmpStack.PushSize(4, 1); BinaryPrimitives.WriteUInt32LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), UfbxBitUtil.SingleToBits((float)valF)); break; }
            case 'd': { int p = ua.Ctx.TmpStack.PushSize(8, 1); BinaryPrimitives.WriteUInt64LittleEndian(ua.Ctx.TmpStack.Data.AsSpan(p), unchecked((ulong)UfbxBitUtil.BitsOf(valF))); break; }
            default: break;
            }
        }

        static long ReadI64(byte[] data, int index) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(index * 8));
        static int ReadI32(byte[] data, int index) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(index * 4));
        static double ReadF64(byte[] data, int index) => UfbxBitUtil.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(index * 8)));
        static float ReadF32(byte[] data, int index) => UfbxBitUtil.BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4)));

        struct Val
        {
            public string Text;
            public long I64;
            public double F64;
            public int I32;
            public float F32;
        }

        static Val MakeVal(string text)
        {
            Val v = new Val();
            v.Text = text;
            if (text == "1.#INF") {
                v.F64 = double.PositiveInfinity;
            } else if (text == "-1.#INF") {
                v.F64 = double.NegativeInfinity;
            } else if (text == "1.#IND" || text == "-nan(ind)") {
                v.F64 = text[0] == '-' ? -double.NaN : double.NaN;
            } else if (text == "1e400" || text == "inf") {
                v.F64 = double.PositiveInfinity;
            } else if (text == "1e-400") {
                v.F64 = 0.0;
            } else {
                v.F64 = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            v.F32 = (float)v.F64;

            bool intLike = true;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c < '0' || c > '9') {
                    if (!(i == 0 && (c == '-' || c == '+'))) intLike = false;
                }
            }
            if (intLike) {
                v.I64 = long.Parse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            } else {
                v.I64 = F64ToI64(v.F64);
            }
            v.I32 = unchecked((int)v.I64);
            return v;
        }

        static readonly string[] IntTexts = new string[]
        {
            "12345678", "7", "-1", "0", "-0", "999999999", "-2147483648", "2147483647",
            "9223372036854775807", "-9223372036854775808", "10", "00012", "-42", "5",
            "1000000000000", "-999999", "3", "4", "5", "6",
        };

        static readonly string[] FloatTexts = new string[]
        {
            "1.5", "-0.0", "0.1", "3.141592653589793", "1.5e3", "-2.5e-10",
            "12345678901234567890.5", "1e300", "1e-320", "42", "7",
            "1.#INF", "-1.#INF", "-nan(ind)", "1e400", "1e-400", "-0", "0.5", "2", "inf",
        };

        static string[] Dups(string[] src, int times)
        {
            string[] dst = new string[src.Length * times];
            for (int t = 0; t < times; t++) {
                for (int i = 0; i < src.Length; i++) dst[t * src.Length + i] = src[i];
            }
            return dst;
        }

        static string ArrayText(string name, string[] texts, string sep)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(name).Append(": *").Append(texts.Length).Append(" {\n\t\ta: ");
            for (int i = 0; i < texts.Length; i++) {
                if (i != 0) sb.Append(sep);
                if (i % 5 == 4) sb.Append("\n\t\t");
                sb.Append(texts[i]);
            }
            sb.Append("\n\t}\n");
            return sb.ToString();
        }

        static void TestArrays()
        {
            Console.WriteLine("[A] int/float array parsing vs expected values (real window)");
            string intText = ArrayText("Vertices", IntTexts, ",");
            string floatText = ArrayText("Normals", FloatTexts, " , ");

            string[] bigInts = Dups(IntTexts, 4);
            string[] bigFloats = Dups(FloatTexts, 4);
            string bigIntText = ArrayText("Weights", bigInts, ",");
            string bigIntNlText = ArrayText("Weights", bigInts, ",\n\t\t");
            string bigFloatText = ArrayText("Normals", bigFloats, " , ");

            foreach (int bufSize in new int[] { 0x4000, 1024, 64, 7 }) {
                foreach (ulong interval in new ulong[] { 0x4000, 7, 3 }) {
                    string tag = "buf=" + bufSize + " interval=" + interval;

                    {
                        Harness h = MakeStreaming(B(intText), bufSize, interval);
                        h.Ascii.NextToken();
                        ArrayParse r = ParseArray(h.Ascii, 'i', IntTexts.Length, false, null);
                        Ok(r.NumValues == IntTexts.Length, tag + " int32: all values parsed eagerly, got " + r.NumValues);
                        bool ok = r.Data.Length == IntTexts.Length * 4;
                        for (int i = 0; ok && i < IntTexts.Length; i++) ok = ReadI32(r.Data, i) == MakeVal(IntTexts[i]).I32;
                        Ok(ok, tag + " int32 element bytes");
                    }

                    {
                        Harness h = MakeStreaming(B(intText), bufSize, interval);
                        h.Ascii.NextToken();
                        ArrayParse r = ParseArray(h.Ascii, 'l', IntTexts.Length, false, null);
                        bool ok = r.Data.Length == IntTexts.Length * 8;
                        for (int i = 0; ok && i < IntTexts.Length; i++) ok = ReadI64(r.Data, i) == MakeVal(IntTexts[i]).I64;
                        Ok(ok, tag + " int64 element bytes");
                    }

                    foreach (char type in new char[] { 'd', 'f' }) {
                        Harness h = MakeStreaming(B(floatText), bufSize, interval);
                        h.Ascii.NextToken();
                        ArrayParse r = ParseArray(h.Ascii, type, FloatTexts.Length, false, null);
                        bool ok = r.Data.Length == FloatTexts.Length * (type == 'd' ? 8 : 4);
                        for (int i = 0; ok && i < FloatTexts.Length; i++) {
                            Val v = MakeVal(FloatTexts[i]);
                            ok = type == 'd' ? Bits(ReadF64(r.Data, i)) == Bits(v.F64) : Bits(ReadF32(r.Data, i)) == Bits(v.F32);
                        }
                        Ok(ok, tag + " " + type + " array element bytes match oracle");
                        Ok(r.NumValues == FloatTexts.Length, tag + " " + type + ": value count " + r.NumValues);
                    }

                    foreach (char type in new char[] { 'i', 'l', 'f', 'd' }) {
                        bool isInt = type == 'i' || type == 'l';
                        string text = isInt ? (type == 'i' ? bigIntNlText : bigIntText) : bigFloatText;
                        string[] texts = isInt ? bigInts : bigFloats;
                        Harness h = MakeStreaming(B(text), bufSize, interval);
                        h.Ascii.NextToken();
                        UfbxiAsciiBuf tmpBuf = new UfbxiAsciiBuf();
                        ArrayParse r = ParseArray(h.Ascii, type, texts.Length, true, tmpBuf);
                        Ok(r.TaskOk, tag + " " + type + ": deferred task succeeded (" + r.TaskError + ")");
                        Ok(r.NumValues == 1, tag + " " + type + ": exactly one eager value, got " + r.NumValues);
                        Ok(r.DeferredValues == texts.Length - 1, tag + " " + type + ": deferred count " + r.DeferredValues);
                        Ok(r.SpanCount >= 1, tag + " " + type + ": spans collected " + r.SpanCount);
                        bool ok = r.Data.Length == texts.Length * (type == 'i' || type == 'f' ? 4 : 8);
                        for (int i = 0; ok && i < texts.Length; i++) {
                            Val v = MakeVal(texts[i]);
                            switch (type) {
                            case 'i': ok = ReadI32(r.Data, i) == v.I32; break;
                            case 'l': ok = ReadI64(r.Data, i) == v.I64; break;
                            case 'f': ok = Bits(ReadF32(r.Data, i)) == Bits(v.F32); break;
                            case 'd': ok = Bits(ReadF64(r.Data, i)) == Bits(v.F64); break;
                            }
                        }
                        Ok(ok, tag + " " + type + ": deferred element bytes match oracle");
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // (W) Windowing edge cases only the real stream exposes
        // -------------------------------------------------------------------

        static void TestWindowing()
        {
            Console.WriteLine("[W] real-window edge cases");

            // A token longer than the read buffer: real refills, str_data grows.
            foreach (int bufSize in new int[] { 7, 64 }) {
                string word = new string('x', 400);
                Harness h = MakeStreaming(B("K: " + word + " \"s" + word + "\"\n"), bufSize, 0x4000);
                h.Ascii.NextToken();
                Ok(h.Ascii.Accept(UfbxiAscii.AsciiName), "long-token: name accepted");
                Ok(h.Ascii.Token.Type == UfbxiAscii.AsciiBareWord && Str(h.Ascii.Token.StrData, 0, h.Ascii.Token.StrLen) == word,
                    "bare word spanning refills, size=" + bufSize);
                Ok(h.Ascii.Token.StrCap >= 400, "token buffer grown past 400");
                h.Ascii.NextToken();
                Ok(h.Ascii.Token.Type == UfbxiAscii.AsciiString && Str(h.Ascii.Token.StrData, 0, h.Ascii.Token.StrLen) == "s" + word,
                    "string spanning refills, size=" + bufSize);
            }

            // retain_buf: a deferred array stored over a tiny read buffer parks its chunks in
            // the caller's retain buffer (src_is_retained) and the spans re-assemble the array
            // text from the first comma to the closing '}'.
            {
                string[] texts = Dups(IntTexts, 4);
                string text = ArrayText("Vertices", texts, ",");
                Harness h = MakeStreaming(B(text), 7, 7);
                h.Ascii.NextToken();
                UfbxiAsciiBuf tmpBuf = new UfbxiAsciiBuf();
                Ok(h.Ascii.Accept(UfbxiAscii.AsciiName), "retain: name");
                h.Ascii.Accept('*');
                h.Ascii.Accept(UfbxiAscii.AsciiInt);
                h.Ascii.Accept('{');
                h.Ascii.Accept(UfbxiAscii.AsciiName);
                h.Ascii.StoreArray(tmpBuf);
                Ok(h.Ascii.SrcIsRetained, "src_is_retained set once the read buffer was replaced by tmp_buf");
                Ok(h.Ascii.SrcBuf == tmpBuf, "src_buf points at the retain buffer");
                Ok(h.Ascii.RetainBuf == null, "retain_buf released after store_array");

                int total = 0;
                foreach (UfbxiAsciiSpan span in h.Ascii.TmpAsciiSpans) total += span.Length;
                int firstValue = text.IndexOf(texts[0], StringComparison.Ordinal);
                int comma = text.IndexOf(',', firstValue);
                int brace = text.LastIndexOf('}');
                int expect = brace - comma + 1;
                Ok(total == expect, "span bytes " + total + " == " + expect);
                Ok(h.Ascii.TmpAsciiSpans.Count > 1, "multiple spans across refills (" + h.Ascii.TmpAsciiSpans.Count + ")");
                string joined = string.Join("", Array.ConvertAll(h.Ascii.TmpAsciiSpans.ToArray(),
                    sp => Str(sp.Source, sp.Offset, sp.Length)));
                Ok(joined == text.Substring(comma, expect), "span text == source range");
                Ok((char)At(h.Ascii.SrcBuffer, h.Ascii.Src) == '}', "src parked on the closing '}'");

                UfbxiAsciiArrayTask task = new UfbxiAsciiArrayTask();
                task.ArrData = new byte[texts.Length * 4];
                task.ArrDataOffset = 4;
                task.ArrType = 'i';
                task.ArrSize = texts.Length - 1;
                task.Spans = h.Ascii.TmpAsciiSpans.ToArray();
                task.NumSpans = task.Spans.Length;
                task.Offset = 0;
                BinaryPrimitives.WriteInt32LittleEndian(task.ArrData.AsSpan(0), MakeVal(texts[0]).I32);
                Ok(UfbxiAscii.ArrayTaskImp(task), "retain: task parses the retained spans");
                bool ok = true;
                for (int i = 1; ok && i < texts.Length; i++) ok = ReadI32(task.ArrData, i) == MakeVal(texts[i]).I32;
                Ok(ok, "retain: retained-span parse matches the oracle");
            }

            // Short reads via the real input: chunked reader hands back 3 of the 8 requested
            // bytes per refill, yet no byte is skipped and the token stream is unaffected.
            {
                byte[] text = B("A: 1111111111, 2\n");
                Harness h = MakeStreaming(text, 8, 0x4000, 3);
                h.Ascii.NextToken();
                StringBuilder sb = new StringBuilder();
                bool ended = false;
                for (int i = 0; i < 64; i++) {
                    if (h.Ascii.Token.Type == UfbxiAscii.AsciiEnd) { ended = true; break; }
                    SigToken(sb, h.Ascii.Token);
                    h.Ascii.NextToken();
                }
                Ok(ended && sb.ToString() == "N/A/1 I/+/1111111111 , I/+/2 ",
                    "short-read: identical tokens, got '" + sb + "'");
                Ok(h.Source.Input.Calls > 1, "short-read: multiple real refills, calls=" + h.Source.Input.Calls);

                // And the same bytes via the memory-load path produce the same signature.
                Harness mem = MakeMemory(text);
                mem.Ascii.NextToken();
                StringBuilder sb2 = new StringBuilder();
                for (int i = 0; i < 64; i++) {
                    if (mem.Ascii.Token.Type == UfbxiAscii.AsciiEnd) break;
                    SigToken(sb2, mem.Ascii.Token);
                    mem.Ascii.NextToken();
                }
                Ok(sb2.ToString() == sb.ToString(), "short-read: streaming and memory-load agree");
            }

            // Progress cancellation with the REAL stream surfaces as a thrown UfbxParseError
            // (UfbxiStream.ReportProgress throws on Cancel) -- the stand-in silently returned a
            // '\0' END token instead, which is NOT what the real loader does.
            {
                Harness h = MakeStreaming(B("A: 1\nB: 2\n"), 64, 4);
                h.Source.Cancel = true;
                h.Source.CancelAfter = 1;
                bool threw = false;
                string msg = null;
                try {
                    h.Ascii.NextToken();
                } catch (UfbxParseError e) {
                    threw = true;
                    msg = e.Message;
                }
                Ok(threw, "cancel: real stream throws instead of yielding '\\0'");
                Ok(msg == "Cancelled", "cancel: error text 'Cancelled', got '" + msg + "'");
                Ok(h.Stream.ProgressCb != null && h.Source.ProgressReports >= 1, "cancel: progress was queried");
            }

            // Interval-sized yields: a long bare word over a tiny window drives many yields,
            // each of which calls the real report-progress.
            {
                Harness h = MakeStreaming(B("A: " + new string('b', 200) + "\n"), 64, 7);
                h.Ascii.NextToken();
                int afterName = h.Source.ProgressReports;
                h.Ascii.NextToken();
                Ok(h.Source.ProgressReports - afterName >= 20,
                    "interval-sized yields (reports=" + (h.Source.ProgressReports - afterName) + ")");
                Ok(h.Ascii.Token.Type == UfbxiAscii.AsciiBareWord && Str(h.Ascii.Token.StrData, 0, h.Ascii.Token.StrLen) == new string('b', 200),
                    "word token across interval yields, len " + h.Ascii.Token.StrLen);
            }

            // read_fn == NULL / memory-load: the window IS the whole buffer, and the refill
            // past it parks on the static empty buffer (ufbx.c:9445-9449).
            {
                byte[] text = B("A: 1\nB: 2\n");
                Harness h = MakeMemory(text);
                bool ended = false;
                h.Ascii.NextToken();
                for (int i = 0; i < 64; i++) {
                    if (h.Ascii.Token.Type == UfbxiAscii.AsciiEnd) { ended = true; break; }
                    h.Ascii.NextToken();
                }
                Ok(ended, "memory-load: stream ends at the buffer boundary");
                Ok(h.Stream.Buffer != null && h.Stream.Buffer.Length == 1 && h.Stream.Buffer[0] == 0,
                    "memory-load: window parked on the empty buffer");
            }
        }

        // -------------------------------------------------------------------
        // String-pool hook wiring: NAME tokens intern to canonical pooled instances
        // -------------------------------------------------------------------

        static void TestPoolHooks()
        {
            Console.WriteLine("[P] string-pool hooks");

            byte[] data = B("FBXHeaderExtension:  {\n\tFBXVersion: 7700\n}\nSomeRandomName: \"x\"\n");
            Harness h = MakeMemory(data);
            h.Ascii.NextToken();

            // Intern the first NAME token: it is a known FBX constant, so the result must be the
            // canonical instance (ReferenceEquals) -- C's `name == ufbxi_...` pointer compare.
            Ok(h.Ascii.Token.Type == UfbxiAscii.AsciiName, "pool: first token is a NAME");
            string interned = h.Ascii.InternName(h.Ascii.Token);
            Ok(interned == "FBXHeaderExtension", "pool: interned name text, got '" + interned + "'");

            // Interning the same bytes again returns the same canonical instance (pointer identity).
            string again = h.Ascii.InternName(h.Ascii.Token);
            Ok(ReferenceEquals(interned, again), "pool: interning is idempotent by pointer");

            // A name that is a known constant aliases the UfbxiStrings entry.
            bool isConst = false;
            string[] all = UfbxiStrings.All;
            for (int i = 0; i < all.Length; i++) {
                if (all[i] == "FBXVersion") { isConst = true; break; }
            }
            Ok(isConst, "pool: FBXVersion is a canonical constant name");

            // 's'/'S'/'C' array-string interning hook round-trips raw bytes.
            string s = "raw-bytes-\xC3\xA9";
            bool raw = true;
            string s2 = s;
            Ok(h.Ascii.InternStringValue(ref s2, raw), "pool: InternStringValue ok");
            Ok(s2 == s, "pool: string value interned (reference-identical to the canonical copy)");
        }

        // -------------------------------------------------------------------
        // (D) Deferred array task failure modes
        // -------------------------------------------------------------------

        static UfbxiAsciiArrayTask TaskFor(char type, string arrayBody, int arrSize, byte[][] spanSources)
        {
            int elemSize = type == 'i' || type == 'f' ? 4 : 8;
            UfbxiAsciiArrayTask t = new UfbxiAsciiArrayTask();
            t.ArrData = new byte[arrSize * elemSize];
            t.ArrDataOffset = 0;
            t.ArrType = type;
            t.ArrSize = arrSize;
            UfbxiAsciiSpan[] spans = new UfbxiAsciiSpan[spanSources.Length];
            for (int i = 0; i < spanSources.Length; i++) {
                spans[i] = new UfbxiAsciiSpan { Source = spanSources[i], Offset = 0, Length = spanSources[i].Length };
            }
            t.Spans = spans;
            t.NumSpans = spans.Length;
            t.Offset = 0;
            return t;
        }

        static void TestTaskFailure()
        {
            Console.WriteLine("[D] deferred array task failure modes");

            {
                UfbxiAsciiArrayTask t = TaskFor('i', "1,2,3}", 4, new[] { B("1,2,3}") });
                Ok(!UfbxiAscii.ArrayTaskImp(t), "too few values -> false (offset != arr_size)");
                Ok(t.Offset == 3, "counted 3 values, got " + t.Offset);
            }
            {
                UfbxiAsciiArrayTask t = TaskFor('i', "", 3, new[] { B("1,2,3}") });
                Ok(UfbxiAscii.ArrayTaskImp(t), "exact count -> true");
                Ok(ReadI32(t.ArrData, 0) == 1 && ReadI32(t.ArrData, 1) == 2 && ReadI32(t.ArrData, 2) == 3, "values written");
            }
            {
                UfbxiAsciiArrayTask t = TaskFor('i', "", 3, new[] { B("1,2,3,4}") });
                Ok(!UfbxiAscii.ArrayTaskImp(t), "too many values -> false");
            }
            {
                UfbxiAsciiArrayTask t = TaskFor('d', "", 3, new[] { B("1,2,\"x\"}") });
                Ok(!UfbxiAscii.ArrayTaskImp(t), "string inside an array -> false");
            }
            {
                string longValue = new string('7', 200);
                UfbxiAsciiArrayTask t = TaskFor('d', "", 2, new[] { B(longValue + ",2}") });
                Ok(!UfbxiAscii.ArrayTaskImp(t), "value longer than the 128-byte staging buffer -> false");
            }
            {
                UfbxiAsciiArrayTask t = TaskFor('l', "", 3, new[] { B("1, ; c\n"), B("2,"), B("3 }") });
                Ok(UfbxiAscii.ArrayTaskImp(t), "multi-span values with comments");
                Ok(ReadI64(t.ArrData, 0) == 1 && ReadI64(t.ArrData, 1) == 2 && ReadI64(t.ArrData, 2) == 3, "multi-span values written");
            }
            {
                UfbxiAsciiArrayTask t = TaskFor('i', "", 2, new[] { B("12"), B("34,5}") });
                Ok(UfbxiAscii.ArrayTaskImp(t), "value split across spans");
                Ok(ReadI32(t.ArrData, 0) == 1234 && ReadI32(t.ArrData, 1) == 5, "split value bytes");
            }
            {
                UfbxiAsciiArrayTask t = TaskFor('i', "", 3, new[] { B("1, ; c\n2,3}") });
                Ok(!UfbxiAscii.ArrayTaskImp(t), "comment inside a bulk-parsed int range -> false (as in C)");
            }
            {
                UfbxiAsciiArrayTask t = TaskFor('d', "", 3, new[] { B("1.5, ; c\n2.5,3.5}") });
                Ok(UfbxiAscii.ArrayTaskImp(t), "comment in a bulk float range -> true (parse_double sets *end)");
                Ok(Bits(ReadF64(t.ArrData, 0)) == Bits(1.5) && Bits(ReadF64(t.ArrData, 1)) == Bits(2.5)
                    && Bits(ReadF64(t.ArrData, 2)) == Bits(3.5), "float values after a comment");
            }
        }

        static void RunSection(string name, Action test)
        {
            int p0 = s_Pass, f0 = s_Fail;
            test();
            Console.WriteLine("  section " + name + ": " + (s_Pass - p0) + " passed, " + (s_Fail - f0) + " failed");
        }

        static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string oracle = "tools/ascii_oracle.txt";
            foreach (string a in args) {
                if (a == "-v") s_Verbose = true;
                else oracle = a;
            }

            RunSection("R oracle replay-and-diff", () => TestOracleReplay(oracle));
            RunSection("I real input impls (mem+file)", () => TestRealInputStreams());
            RunSection("A array readers", () => TestArrays());
            RunSection("W windowing", () => TestWindowing());
            RunSection("P string-pool hooks", () => TestPoolHooks());
            RunSection("D deferred task failures", () => TestTaskFailure());

            Console.WriteLine();
            Console.WriteLine("AsciiCheck: " + s_Pass + " passed, " + s_Fail + " failed");
            return s_Fail == 0 ? 0 : 1;
        }
    }
}
