// Isolated differential verifier for the ported ufbx inflate layer.
//
//   * tools/inflate_oracle.c is compiled with `zig cc` against ufbx.c v0.23.1 as a single
//     translation unit, so it answers with the real `static ufbxi_*` internals
//     (ufbx_inflate, ufbxi_adler32, ufbxi_bit_stream_init, ufbxi_bit_refill, ...).
//     `-- inflatecorpus` writes the case list to tools/inflate_corpus.txt, the oracle turns it
//     into tools/inflate_oracle.txt, and `-- inflatecheck` replays every case against the port
//     and diffs the answers. The corpus is regenerated and compared byte-for-byte first, so a
//     drifting harness cannot silently re-point records at stale answers.
//   * Independent second opinions stay on top of that: published reference values (adler32,
//     RFC 1951 fixed Huffman codes), System.IO.Compression as a compressor and decompressor,
//     and a hand-written RFC 1951 encoder used to craft valid and invalid blocks.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Ufbx;

namespace UfbxTests
{
    internal static class InflateCheck
    {
        static int s_total, s_pass, s_fail;

        static void Chk(bool cond, string name)
        {
            s_total++;
            if (cond)
            {
                s_pass++;
            }
            else
            {
                s_fail++;
                Console.WriteLine("  FAIL: " + name);
            }
        }

        static readonly Dictionary<int, int> s_shortCodes = new Dictionary<int, int>();

        static void NoteCode(string what, int code)
        {
            s_shortCodes.TryGetValue(code, out int n);
            s_shortCodes[code] = n + 1;
            _ = what;
        }

        // The corpus shared with tools/inflate_oracle.c. Every case below registers itself here
        // while it runs, so the record set and the executed checks cannot drift apart.
        static readonly Corpus s_corpus = new Corpus();
        static Oracle s_oracle;

        const string CorpusPath = "tools/inflate_corpus.txt";
        const string OraclePath = "tools/inflate_oracle.txt";

        static int RunAll()
        {
            RunAdler32();
            RunBitReverse();
            RunRoundTrip();
            RunEmptyInput();
            RunStaticHuffTables();
            RunCustomTrees();
            RunCraftedBlocks();
            RunCraftedErrors();
            RunBitStreamEquivalence();
            RunProgressCancel();
            RunBitFlipFuzz();
            return s_fail == 0 ? 0 : 1;
        }

        public static int DumpCorpus(string[] args)
        {
            RunAll();
            string path = args.Length > 1 ? args[1] : CorpusPath;
            var sb = new StringBuilder();
            foreach (string line in s_corpus.Lines())
            {
                sb.Append(line);
                sb.Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"inflatecorpus: {s_corpus.Streams.Count} streams, {s_corpus.As.Count} A, " +
                $"{s_corpus.Ds.Count} D, {s_corpus.Bs.Count} B, {s_corpus.Fs.Count} F records -> {path}");
            Console.WriteLine("inflatecorpus: digest " + Fnv.Hex(s_corpus.Digest()));
            return 0;
        }

        public static int Run(string[] args)
        {
            RunAll();

            var shortParts = new List<string>();
            foreach (KeyValuePair<int, int> kv in s_shortCodes) shortParts.Add($"{kv.Key}:{kv.Value}");
            shortParts.Sort();
            Console.WriteLine("  short read_fn error codes: " + string.Join(" ", shortParts));

            RunOracleDifferential(args);

            Console.WriteLine($"inflatecheck: {s_total} vectors, {s_pass} passed, {s_fail} failed");
            return s_fail == 0 ? 0 : 1;
        }

        // Third opinion over the corpus: replay every D record that has a known payload through
        // `System.IO.Compression` and report whether it agrees with the port. Used to attribute a
        // port/oracle divergence - if .NET agrees with the port but C differs, the gap is a
        // ufbx-specific semantic (chunk padding, truncation, error codes) rather than the DEFLATE
        // decoding itself. This mode never asserts, it only prints.
        public static int NetDiag(string[] args)
        {
            RunAll();

            int agree = 0, disagree = 0, failed = 0, skipped = 0;
            var bad = new List<string>();
            foreach (DRec r in s_corpus.Ds)
            {
                if (r.Payload == null || r.Payload.Length == 0) { skipped++; continue; }
                byte[] stream = CorpusStreamOf(r);
                if (stream == null) { skipped++; continue; }

                byte[] got;
                try
                {
                    // `no_header` streams are raw DEFLATE bodies; a zlib stream without a
                    // checksum tail has to be fed to `DeflateStream` with the 2-byte header
                    // stripped, because `ZLibStream` demands the Adler-32 that ufbx skipped.
                    byte[] body;
                    bool raw;
                    if (r.Nh) { body = stream; raw = true; }
                    else if (r.Nc) { body = Tail(stream, 2); raw = true; }
                    else { body = stream; raw = false; }

                    using var src = new MemoryStream(body);
                    using var dec = raw
                        ? (Stream)new DeflateStream(src, CompressionMode.Decompress)
                        : new ZLibStream(src, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    dec.CopyTo(outMs);
                    got = outMs.ToArray();
                }
                catch (Exception)
                {
                    failed++;
                    if (bad.Count < 20) bad.Add($"{r.Name}: .NET rejected the stream the port decoded");
                    continue;
                }

                if (got.Length == r.Payload.Length && SeqEqual(got, r.Payload, r.Payload.Length))
                {
                    agree++;
                }
                else
                {
                    disagree++;
                    if (bad.Count < 20) bad.Add($"{r.Name}: .NET produced {got.Length} bytes, payload is {r.Payload.Length}");
                }
            }
            foreach (string s in bad) Console.WriteLine("  netdiag: " + s);
            Console.WriteLine($"netdiag: {agree} agree, {disagree} disagree, {failed} rejected by .NET, " +
                $"{skipped} records without a plain payload");
            return 0;
        }

        // The stream bytes a D record was pointed at.
        static byte[] CorpusStreamOf(DRec r)
        {
            if (r.Stream < 0 || r.Stream >= s_corpus.Streams.Count) return null;
            return s_corpus.Streams[r.Stream];
        }

        static byte[] Tail(byte[] data, int skip)
        {
            if (skip >= data.Length) return new byte[0];
            byte[] r = new byte[data.Length - skip];
            Array.Copy(data, skip, r, 0, r.Length);
            return r;
        }

        // Resolve a harness data file relative to the repository root: `dotnet run` may leave the
        // working directory either at the repo root or at the project directory.
        static string ResolvePath(string rel)
        {
            if (File.Exists(rel)) return rel;
            string[] candidates = {
                Path.Combine("..", rel),
                Path.Combine("..", "..", rel),
                Path.Combine(AppContext.BaseDirectory, rel),
                Path.Combine(AppContext.BaseDirectory, "..", rel),
                Path.Combine(AppContext.BaseDirectory, "..", "..", rel),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", rel),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", rel),
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        // ---------------------------------------------------------------- C differential

        static void RequireOracleAgreement(string what, bool ok)
        {
            Chk(ok, what);
        }

        static void RunOracleDifferential(string[] args)
        {
            // 1. The committed corpus must be exactly what this harness generates today, otherwise
            //    the answers below describe different inputs than the records being replayed.
            string corpusFile = args.Length > 1 && args[1] != "" ? args[1] : ResolvePath(CorpusPath);
            string oracleFile = args.Length > 2 && args[2] != "" ? args[2] : ResolvePath(OraclePath);
            if (corpusFile == null)
            {
                Chk(false, $"corpus {CorpusPath} missing - run `dotnet run --project tools/InflateCheck -c Release" +
                    " -- inflatecorpus`");
                return;
            }
            if (oracleFile == null)
            {
                Chk(false, $"oracle answers {OraclePath} missing - run `tools/inflate_oracle.exe " +
                    "tools/inflate_corpus.txt tools/inflate_oracle.txt`");
                return;
            }

            var want = s_corpus.Lines();
            var have = new List<string>();
            foreach (string l in File.ReadAllLines(corpusFile))
            {
                string t = l.TrimEnd('\r');
                if (t.StartsWith("#") || t.Length == 0) continue;
                have.Add(t.Trim());
            }
            var wantTrimmed = new List<string>();
            foreach (string l in want)
            {
                string t = l.Trim();
                if (t.StartsWith("#") || t.Length == 0) continue;
                wantTrimmed.Add(t);
            }
            bool same = wantTrimmed.Count == have.Count;
            int firstDiff = -1;
            if (same)
            {
                for (int i = 0; i < wantTrimmed.Count; i++)
                {
                    if (wantTrimmed[i] != have[i]) { same = false; firstDiff = i; break; }
                }
            }
            Chk(same, $"corpus is not stale: {wantTrimmed.Count} generated lines match {corpusFile}" +
                (same ? "" : $" (count {have.Count}, first difference at line {firstDiff + 1}: regenerate the corpus)"));
            if (!same) return;

            var oracle = new Oracle();
            if (!oracle.Load(oracleFile))
            {
                Chk(false, $"oracle answers {oracleFile} contain no records");
                return;
            }
            Console.WriteLine($"  oracle: {oracle.NumD} D / {oracle.NumB} B / {oracle.NumF} F / " +
                $"{oracle.NumA} A answers from {oracleFile}");

            DiffA(oracle);
            DiffD(oracle);
            DiffB(oracle);
            DiffF(oracle);
        }

        static void DiffA(Oracle oracle)
        {
            foreach (ARec r in s_corpus.As)
            {
                string[] row = oracle.Get(r.Key);
                if (row == null || row.Length < 5)
                {
                    Chk(false, $"adler[{r.Key}] has no oracle answer");
                    continue;
                }
                uint c = (uint)ParseHex(row[4]);
                Chk(c == r.PortValue, $"adler32 stream {r.Sid}[{r.Off}..+{r.Len}] matches C: {c:x8} == {r.PortValue:x8}");
                if (r.Expect.HasValue)
                {
                    Chk(c == r.Expect.Value,
                        $"adler32 {r.ExpectSource}: C says {c:x8}, harness expects {r.Expect.Value:x8}");
                }
            }
        }

        static ulong ParseHex(string s)
        {
            ulong v = 0;
            foreach (char c in s)
            {
                int d = c >= 'a' ? c - 'a' + 10 : c - '0';
                v = (v << 4) | (ulong)(uint)d;
            }
            return v;
        }

        static void DiffD(Oracle oracle)
        {
            foreach (DRec r in s_corpus.Ds)
            {
                string[] row = oracle.Get(r.Key);
                if (row == null || row.Length < 8)
                {
                    Chk(false, $"{r.Name}: D record {r.Idx} has no oracle answer");
                    continue;
                }
                long cres = long.Parse(row[2], CultureInfo.InvariantCulture);
                string cdigest = row[3];
                ulong cCount = ulong.Parse(row[4], CultureInfo.InvariantCulture);
                bool cMono = row[5] == "1";
                bool cTotal = row[6] == "1";
                ulong cLast = ulong.Parse(row[7], CultureInfo.InvariantCulture);
                string cCbDigest = row.Length > 8 ? row[8] : Fnv.Hex(Fnv.Offset);

                if (!r.Ran)
                {
                    Chk(false, $"{r.Name}: port outcome was never captured");
                    continue;
                }

                // Independent intent check against the C answer first: a harness expectation that
                // contradicts ufbx.c is a harness bug, so it must show up here, not as a port FAIL.
                bool intent = true;
                string intentText = "";
                if (r.Payload != null)
                {
                    ulong wantDigest = DigestOf(r.Payload, r.DstSize);
                    intent = cres == r.Payload.Length && cdigest == Fnv.Hex(wantDigest);
                    intentText = $"C decodes to the {r.Payload.Length} byte payload";
                }
                else if (r.Err != int.MinValue)
                {
                    intent = cres == r.Err;
                    intentText = $"C reports {r.Err}";
                }
                else if (r.AnyNegative)
                {
                    intent = cres < 0;
                    intentText = "C reports an error";
                }
                Chk(intent, $"{r.Name}: harness intent \"{intentText}\" disagrees with C (res {cres}, dst digest {cdigest})");

                if (r.Threw)
                {
                    Chk(false, $"{r.Name}: the port threw where C returned {cres}");
                    continue;
                }

                bool same = cres == r.Res && cdigest == r.Digest;
                Chk(same, $"{r.Name}: port res/dst digest == C (port {r.Res} {r.Digest}, C {cres} {cdigest})");

                bool cbSame = cCount == r.CbCount && cMono == r.CbMonotone && cTotal == r.CbTotalOk &&
                    cLast == r.CbLast && cCbDigest == r.CbDigest;
                Chk(cbSame, $"{r.Name}: progress accounting == C (port {r.CbCount}/{r.CbMonotone}/{r.CbTotalOk}/" +
                    $"{r.CbLast}/{r.CbDigest}, C {cCount}/{cMono}/{cTotal}/{cLast}/{cCbDigest})");
            }
        }

        // FNV-1a is not invertible, so hash the whole zero-padded buffer (C digests all
        // `dst_size` bytes of the zero-initialised destination, not just the produced prefix).
        static ulong DigestOf(byte[] payload, int dstSize)
        {
            byte[] padded = new byte[dstSize];
            Array.Copy(payload, 0, padded, 0, payload.Length <= dstSize ? payload.Length : dstSize);
            return Fnv.Hash(Fnv.Offset, padded, dstSize);
        }

        static void DiffB(Oracle oracle)
        {
            foreach (BRec r in s_corpus.Bs)
            {
                string[] row = oracle.Get(r.Key);
                if (row == null || row.Length < 10)
                {
                    Chk(false, $"{r.Name}: B record {r.Idx} has no oracle answer");
                    continue;
                }
                long cSteps = long.Parse(row[2], CultureInfo.InvariantCulture);
                long cEof = long.Parse(row[3], CultureInfo.InvariantCulture);
                int cStop = int.Parse(row[4], CultureInfo.InvariantCulture);
                string cTrace = row[5];
                ulong cCount = ulong.Parse(row[6], CultureInfo.InvariantCulture);
                bool cMono = row[7] == "1";
                bool cTotal = row[8] == "1";
                ulong cLast = ulong.Parse(row[9], CultureInfo.InvariantCulture);
                string cCbDigest = row.Length > 10 ? row[10] : Fnv.Hex(Fnv.Offset);

                bool same = cSteps == r.Steps && cEof == r.EofStep && cStop == r.StopError && cTrace == r.Trace;
                Chk(same, $"{r.Name}: bit-stream driver trace == C (port {r.Steps} steps, eof {r.EofStep}, " +
                    $"stop {r.StopError}, {r.Trace}; C {cSteps} steps, eof {cEof}, stop {cStop}, {cTrace})");

                bool cbSame = cCount == r.CbCount && cMono == r.CbMonotone && cTotal == r.CbTotalOk &&
                    cLast == r.CbLast && cCbDigest == r.CbDigest;
                Chk(cbSame, $"{r.Name}: driver progress accounting == C (port {r.CbCount}/{r.CbMonotone}/" +
                    $"{r.CbTotalOk}/{r.CbLast}/{r.CbDigest}, C {cCount}/{cMono}/{cTotal}/{cLast}/{cCbDigest})");
            }
        }

        // ufbx.c:3268-3276: a stream that C accepts (and that was not decoded with
        // `no_checksum`) carries the Adler-32 of exactly the bytes it produced, so every accepted
        // flip is self-verifying: the port's own output must re-hash to the checksum that the
        // corrupted stream transmits. That is what replaces the naive "no corruption may decode".
        static void DiffF(Oracle oracle)
        {
            var all = new SortedDictionary<int, int>();
            int totalFlips = 0;
            foreach (FRec r in s_corpus.Fs)
            {
                int missing = 0, mismatch = 0, portAccepted = 0, cAccepted = 0, unverified = 0, exact = 0;
                string firstBad = "";
                string firstUnverified = "";
                var hist = new SortedDictionary<int, int>();
                for (int i = 0; i < r.Flips.Count; i++)
                {
                    long flip = r.Flips[i];
                    int byteIx = (int)(flip / 8);
                    int bit = (int)(flip % 8);
                    string[] row = oracle.Get(r.FlipKey(byteIx, bit));
                    if (row == null || row.Length < 6) { missing++; continue; }
                    long cres = long.Parse(row[4], CultureInfo.InvariantCulture);
                    string cdigest = row[5];
                    if (cres >= 0) cAccepted++;
                    else
                    {
                        int code = (int)cres;
                        hist.TryGetValue(code, out int n);
                        hist[code] = n + 1;
                        all.TryGetValue(code, out int m);
                        all[code] = m + 1;
                    }
                    if (r.Res[i] >= 0) portAccepted++;
                    if (cres != r.Res[i] || cdigest != r.Digests[i])
                    {
                        mismatch++;
                        if (firstBad == "") firstBad = $"byte {byteIx} bit {bit}: port {r.Res[i]} {r.Digests[i]}, C {cres} {cdigest}";
                    }
                    if (cres >= 0 && (r.Flags[i] & FlipChecksumOk) == 0)
                    {
                        unverified++;
                        if (firstUnverified == "") firstUnverified = $"byte {byteIx} bit {bit}";
                    }
                    if (cres >= 0 && (r.Flags[i] & FlipExactPayload) != 0) exact++;
                }
                totalFlips += r.Flips.Count;
                Chk(missing == 0, $"bit-flip fuzz {r.Name}: {missing} flips without an oracle answer");
                Chk(mismatch == 0, $"bit-flip fuzz {r.Name}: {mismatch} flips where the port differs from C (first: {firstBad})");
                Chk(portAccepted == cAccepted, $"bit-flip fuzz {r.Name}: the port accepts {portAccepted} corrupted streams, C accepts {cAccepted}");
                Chk(r.Nc || unverified == 0, $"bit-flip fuzz {r.Name}: {unverified} accepted streams failed their own Adler-32 (first: {firstUnverified})");

                var parts = new List<string>();
                foreach (KeyValuePair<int, int> kv in hist) parts.Add($"{kv.Key}:{kv.Value}");
                Console.WriteLine($"  fuzz {r.Name}: {r.Flips.Count} flips, {portAccepted} accepted by both " +
                    $"({exact} reproducing the pristine payload), C error codes " + string.Join(" ", parts.ToArray()));
                Chk(hist.Count >= 2, $"bit-flip fuzz {r.Name}: C reported {hist.Count} distinct error codes");
            }
            var allParts = new List<string>();
            foreach (KeyValuePair<int, int> kv in all) allParts.Add($"{kv.Key}:{kv.Value}");
            Console.WriteLine($"  fuzz total: {totalFlips} flips, C error-code histogram " + string.Join(" ", allParts.ToArray()));
            Chk(all.Count >= 8, $"bit-flip fuzz: the corpus exercises {all.Count} distinct C error codes (>= 8)");
        }

        const int FlipChecksumOk = 1;
        const int FlipExactPayload = 2;


        // ---------------------------------------------------------------- helpers

        static byte[] Bytes(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
            return b;
        }

        // Compare `b` against the first `bLength` bytes of `a`. C: `ufbx_inflate()` only ever
        // writes inside `[dst, dst + dst_size)` (ufbx.c:3142-3144, 3279) and returns the number of
        // bytes produced, so a destination buffer larger than the payload legitimately keeps
        // untouched slack; requiring `a.Length == bLength` here would compare the wrong thing.
        static bool SeqEqual(byte[] a, byte[] b, int bLength)
        {
            if (bLength < 0 || a.Length < bLength) return false;
            for (int i = 0; i < bLength; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        static byte[] Rand(int n, uint seed)
        {
            byte[] r = new byte[n];
            for (int i = 0; i < n; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                r[i] = (byte)(seed >> 23);
            }
            return r;
        }

        static byte[] ZlibCompress(byte[] payload, CompressionLevel level)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, level, true))
            {
                z.Write(payload, 0, payload.Length);
            }
            return ms.ToArray();
        }

        static byte[] RawDeflate(byte[] payload, CompressionLevel level)
        {
            using var ms = new MemoryStream();
            using (var d = new DeflateStream(ms, level, true))
            {
                d.Write(payload, 0, payload.Length);
            }
            return ms.ToArray();
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Array.Copy(a, r, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        static byte[] BeAdler(uint v)
        {
            return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        // Naive reference implementation: reduce after every byte.
        static uint RefAdler(byte[] data, int offset, int length)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < length; i++)
            {
                a = (a + data[offset + i]) % 65521u;
                b = (b + a) % 65521u;
            }
            return (b << 16) | a;
        }

        // C: ufbxi_memory_stream-style read_fn (ufbx.c:7199-7207) with a per-call cap.
        sealed class ChunkReader
        {
            readonly byte[] m_data;
            readonly int m_maxPerCall;
            public int Calls;
            public int TotalBytes;
            int m_pos;

            public ChunkReader(byte[] data, int maxPerCall)
            {
                m_data = data;
                m_maxPerCall = maxPerCall;
            }

            public int Read(object user, byte[] data, int size)
            {
                Calls++;
                int n = size < m_maxPerCall ? size : m_maxPerCall;
                int left = m_data.Length - m_pos;
                if (n > left) n = left;
                if (n > 0)
                {
                    Array.Copy(m_data, m_pos, data, 0, n);
                    m_pos += n;
                    TotalBytes += n;
                }
                return n;
            }

            // Bytes that were handed to the decoder through `ufbx_inflate_input::data` and are
            // therefore already consumed from the stream the `read_fn()` continues from.
            public void Skip(int n)
            {
                m_pos += n;
            }
        }

        enum Mode { WholeMemory, BlockingReader, ShortReader }

        // One raw `ufbxi_bit_refill()` driver configuration, as a corpus B record.
        sealed class Bcfg
        {
            public readonly string Name;
            public readonly byte[] Stream;
            public readonly int DataSize;
            public readonly long Step;
            public readonly int BufSz;
            public readonly ulong Hint;
            public readonly int CancelAt;

            public Bcfg(string name, byte[] stream, int dataSize, long step, int bufsz, ulong hint, int cancelAt)
            {
                Name = name;
                Stream = stream;
                DataSize = dataSize;
                Step = step;
                BufSz = bufsz;
                Hint = hint;
                CancelAt = cancelAt;
            }
        }

        static int ModeToOracle(Mode mode)
        {
            return mode == Mode.WholeMemory ? 0 : 1;
        }

        static long ModeToStep(Mode mode, int shortRead)
        {
            if (mode == Mode.WholeMemory) return 0;
            if (mode == Mode.BlockingReader) return CorpusConst.SizeMax;
            return shortRead;
        }

        // C: `setup_input()` + `setup_progress()` + `ufbx_inflate()` in tools/inflate_oracle.c.
        // The parameters are the corpus record fields verbatim, so the port and C are handed
        // byte-identical inputs and only the answers may differ.
        sealed class PortOut
        {
            public bool Threw;
            public int Res;
            public string Digest;
            public byte[] Dst;
            public ProgRec Prog;
            public ulong CbCount;
            public bool CbMonotone = true;
            public bool CbTotalOk = true;
            public ulong CbLast = ulong.MaxValue;
            public string CbDigest;
        }

        static PortOut RunPort(byte[] stream, bool noHeader, bool noChecksum, int fastBits,
            int mode, long step, int bufsz, int dstSize, ulong hint, int cancelAt)
        {
            var output = new PortOut();
            var input = new UfbxInflateInput();
            input.TotalSize = stream.Length;
            if (bufsz > 0)
            {
                input.Buffer = new byte[bufsz];
                input.BufferSize = bufsz;
            }
            if (mode == 0)
            {
                input.Data = stream;
                input.DataSize = stream.Length;
            }
            else
            {
                if (mode == 2)
                {
                    input.Data = stream;
                    input.DataSize = (int)step;
                }
                else
                {
                    input.Data = new byte[0];
                    input.DataSize = 0;
                }
                var reader = new ChunkReader(stream, CorpusConst.ReadCap(step));
                if (mode == 2) reader.Skip((int)step);
                input.ReadFn = reader.Read;
                input.ReadUser = reader;
            }
            input.NoHeader = noHeader;
            input.NoChecksum = noChecksum;
            input.InternalFastBits = fastBits;
            if (cancelAt >= -1)
            {
                input.ProgressIntervalHint = hint;
                var prog = new ProgRec();
                prog.CancelAt = cancelAt;
                prog.ExpectTotal = (ulong)stream.Length + input.ProgressSizeBefore + input.ProgressSizeAfter;
                input.ProgressCb.Fn = prog.Fn;
                input.ProgressCb.User = prog;
                output.Prog = prog;
            }

            var retain = new UfbxInflateRetain();
            retain.Initialized = false;
            byte[] dst = new byte[dstSize];
            output.Dst = dst;
            try
            {
                // Through the public façade (`UfbxInflateApi.Inflate`) so the forwarder itself is
                // covered by every record here, not just the internal body.
                output.Res = UfbxInflateApi.Inflate(dst, dstSize, input, retain);
            }
            catch (Exception)
            {
                output.Threw = true;
                return output;
            }
            output.Digest = Fnv.Hex(Fnv.Hash(Fnv.Offset, dst, dstSize));
            if (output.Prog != null)
            {
                output.CbCount = output.Prog.Count;
                output.CbMonotone = output.Prog.Monotone;
                output.CbTotalOk = output.Prog.TotalOk;
                output.CbLast = output.Prog.Last;
                output.CbDigest = Fnv.Hex(output.Prog.Digest);
            }
            else
            {
                output.CbDigest = Fnv.Hex(Fnv.Offset);
            }
            return output;
        }

        static int s_streamsDeduped;
        static readonly Dictionary<string, int> s_streamIndex = new Dictionary<string, int>();

        static int AddStream(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            const string digits = "0123456789abcdef";
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(digits[(data[i] >> 4) & 15]);
                sb.Append(digits[data[i] & 15]);
            }
            string key = sb.ToString();
            int sid;
            if (s_streamIndex.TryGetValue(key, out sid)) { s_streamsDeduped++; return sid; }
            s_streamIndex[key] = s_corpus.Streams.Count;
            return s_corpus.AddStream(data);
        }

        // Register a D record and capture the port outcome from an actual run.
        static DRec AddD(string name, byte[] stream, bool noHeader, bool noChecksum, int fastBits,
            int mode, long step, int bufsz, int dstSize, ulong hint, int cancelAt,
            byte[] payload, int err, bool anyNegative)
        {
            var r = new DRec();
            r.Idx = s_corpus.NextD();
            r.Name = name;
            r.Stream = AddStream(stream);
            r.Nh = noHeader;
            r.Nc = noChecksum;
            r.Fast = fastBits;
            r.Mode = mode;
            r.Step = step;
            r.BufSz = bufsz;
            r.DstSize = dstSize;
            r.Hint = hint;
            r.CancelAt = cancelAt;
            r.Payload = payload;
            r.Err = err;
            r.AnyNegative = anyNegative;
            s_corpus.Ds.Add(r);

            PortOut o = RunPort(stream, noHeader, noChecksum, fastBits, mode, step, bufsz, dstSize,
                hint, cancelAt);
            r.Ran = true;
            r.Threw = o.Threw;
            r.Res = o.Res;
            r.Digest = o.Digest;
            r.CbCount = o.CbCount;
            r.CbMonotone = o.CbMonotone;
            r.CbTotalOk = o.CbTotalOk;
            r.CbLast = o.CbLast;
            r.CbDigest = o.CbDigest;
            r.Dst = o.Dst;
            r.Prog = o.Prog;
            return r;
        }

        static BRec AddB(string name, byte[] stream, int dataSize, long step, int bufsz, ulong hint,
            int cancelAt, long maxSteps, int[] fields)
        {
            List<int> unused;
            return AddB(name, stream, dataSize, step, bufsz, hint, cancelAt, maxSteps, fields, out unused);
        }

        static BRec AddB(string name, byte[] stream, int dataSize, long step, int bufsz, ulong hint,
            int cancelAt, long maxSteps, int[] fields, out List<int> values)
        {
            var r = new BRec();
            r.Idx = s_corpus.NextB();
            r.Name = name;
            r.Stream = AddStream(stream);
            r.DataSize = dataSize;
            r.Step = step;
            r.BufSz = bufsz;
            r.Hint = hint;
            r.CancelAt = cancelAt;
            r.MaxSteps = maxSteps;
            r.Fields = fields;
            s_corpus.Bs.Add(r);
            values = fields != null ? new List<int>() : null;
            RunDriver(r, stream, values);
            return r;
        }

        // C: `run_fuzz_record()` - flip one bit of `stream`, inflate the whole thing from memory
        // into `dst_size` zeroed bytes and record exactly what the port produced. The record then
        // replays identically in C and DiffF compares res + destination digest per flip.
        static FRec AddF(string name, byte[] stream, byte[] payload, bool noHeader, bool noChecksum, int fastBits)
        {
            var r = new FRec();
            r.Idx = s_corpus.NextF();
            r.Name = name;
            r.Stream = AddStream(stream);
            r.Nh = noHeader;
            r.Nc = noChecksum;
            r.Fast = fastBits;
            r.DstSize = payload.Length == 0 ? 1 : payload.Length;
            r.Payload = payload;
            r.PayloadStream = AddStream(payload);
            s_corpus.Fs.Add(r);

            for (int byteIx = 0; byteIx < stream.Length; byteIx++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    byte[] src = (byte[])stream.Clone();
                    src[byteIx] ^= (byte)(1 << bit);
                    r.Flips.Add(byteIx * 8L + bit);

                    PortOut o = RunPort(src, noHeader, noChecksum, fastBits, 0, 0, 0, r.DstSize, 0,
                        CorpusConst.NoCallback);
                    if (o.Threw)
                    {
                        r.Res.Add(int.MinValue);
                        r.Digests.Add("threw");
                        r.Flags.Add(0);
                        continue;
                    }
                    r.Res.Add(o.Res);
                    r.Digests.Add(o.Digest);

                    int flags = 0;
                    if (o.Res >= 0)
                    {
                        // ufbx.c:3268-3276: acceptance without `no_checksum` means the four bytes
                        // the decoder read as Adler-32 (big-endian, ufbx.c:3269-3270) equal the
                        // checksum of exactly the bytes it produced.
                        uint refAdler = (uint)((src[src.Length - 4] << 24) | (src[src.Length - 3] << 16) |
                            (src[src.Length - 2] << 8) | src[src.Length - 1]);
                        uint produced = UfbxiInflate.UfbxiAdler32(o.Dst, 0, o.Res);
                        if (refAdler == produced) flags |= FlipChecksumOk;
                        if (o.Res == payload.Length && SeqEqual(o.Dst, payload, payload.Length))
                            flags |= FlipExactPayload;
                    }
                    r.Flags.Add(flags);
                }
            }
            return r;
        }

        // C: `run_driver_record()` - the same loop over the real `ufbxi_bit_refill()`, and the same
        // twelve decimal fields per step so the trace digests are comparable. `values` (optional)
        // collects the raw bit field of every step, which the harness compares against an
        // independent LSB-first reference reader.
        static void RunDriver(BRec r, byte[] stream, List<int> values)
        {
            var input = new UfbxInflateInput();
            input.TotalSize = stream.Length;
            if (r.BufSz > 0)
            {
                input.Buffer = new byte[r.BufSz];
                input.BufferSize = r.BufSz;
            }
            if (r.DataSize > 0)
            {
                input.Data = stream;
                input.DataSize = r.DataSize;
            }
            else
            {
                input.Data = new byte[0];
                input.DataSize = 0;
            }
            if (r.Step != 0 && r.DataSize < stream.Length)
            {
                var reader = new ChunkReader(stream, CorpusConst.ReadCap(r.Step));
                reader.Skip(r.DataSize);
                input.ReadFn = reader.Read;
                input.ReadUser = reader;
            }
            ProgRec prog = null;
            if (r.CancelAt >= -1)
            {
                input.ProgressIntervalHint = r.Hint;
                prog = new ProgRec();
                prog.CancelAt = r.CancelAt;
                prog.ExpectTotal = (ulong)stream.Length + input.ProgressSizeBefore + input.ProgressSizeAfter;
                input.ProgressCb.Fn = prog.Fn;
                input.ProgressCb.User = prog;
            }

            var s = new UfbxiBitStream();
            s.BitStreamInit(input);

            ulong bits = s.Bits;
            int left = s.Left;
            int data = s.ChunkPtr;
            ulong trace = Fnv.Offset;
            var line = new StringBuilder(96);

            for (long i = 0; i < r.MaxSteps; i++)
            {
                s.BitRefill(ref bits, ref left, ref data);

                // C: `s.chunk_begin` compared against `input->data`, `s->local_buffer`,
                // `input->buffer` in that order, 3 if none match. The port models `s->buffer` as
                // `ChunkArray` + offsets, and `s.Buffer` is the working buffer (the user buffer
                // when it is large enough, otherwise the internal 256-byte local buffer), so
                // "not the user buffer but equal to `Buffer`" is exactly "local buffer".
                bool isUserBuf = input.Buffer != null && ReferenceEquals(s.Buffer, input.Buffer);
                int arrayId = 3;
                if (input.DataSize > 0 && ReferenceEquals(s.ChunkArray, stream)) arrayId = 0;
                else if (!isUserBuf && ReferenceEquals(s.ChunkArray, s.Buffer)) arrayId = 1;
                else if (isUserBuf && ReferenceEquals(s.ChunkArray, input.Buffer)) arrayId = 2;

                line.Length = 0;
                AppendUlong(line, (ulong)i);
                line.Append(' ');
                AppendUlong(line, bits);
                line.Append(' ');
                AppendUlong(line, (ulong)(long)left);
                line.Append(' ');
                AppendUlong(line, (ulong)(long)(data - s.ChunkBegin));
                line.Append(' ');
                AppendUlong(line, (ulong)(long)(s.ChunkEnd - s.ChunkBegin));
                line.Append(' ');
                AppendUlong(line, (ulong)(long)(s.ChunkYield - s.ChunkBegin));
                line.Append(' ');
                AppendUlong(line, (ulong)(long)(s.ChunkRealEnd - s.ChunkBegin));
                line.Append(' ');
                AppendUlong(line, (ulong)(long)s.NumReadBeforeChunk);
                line.Append(' ');
                AppendUlong(line, (ulong)(long)s.InputLeft);
                line.Append(' ');
                AppendUlong(line, (ulong)(long)s.BufferSize);
                line.Append(' ');
                line.Append(arrayId.ToString(CultureInfo.InvariantCulture));
                line.Append(' ');
                line.Append(s.StopError.ToString(CultureInfo.InvariantCulture));
                line.Append('\n');
                trace = Fnv.HashString(trace, line.ToString());

                r.Steps++;
                if (s.StopError != 0)
                {
                    r.EofStep = i;
                    r.StopError = s.StopError;
                    break;
                }
                int consume = 1;
                if (r.Fields != null && r.Fields.Length > 0) consume = r.Fields[(int)(i % r.Fields.Length)];
                if (values != null) values.Add((int)(bits & (((ulong)1 << consume) - 1)));
                bits >>= consume;
                left -= consume;
            }

            r.Trace = Fnv.Hex(trace);
            r.Prog = prog;
            r.CbCount = prog == null ? 0UL : prog.Count;
            r.CbMonotone = prog == null || prog.Monotone;
            r.CbTotalOk = prog == null || prog.TotalOk;
            r.CbLast = prog == null ? ulong.MaxValue : prog.Last;
            r.CbDigest = Fnv.Hex(prog == null ? Fnv.Offset : prog.Digest);
        }

        static void AppendUlong(StringBuilder sb, ulong v)
        {
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        static void ExpectBytes(string name, byte[] payload, byte[] src, bool noHeader, bool noChecksum,
            int fastBits, Mode mode, int shortRead = 3)
        {
            int dstSize = payload.Length == 0 ? 1 : payload.Length;
            DRec rec = AddD(name, src, noHeader, noChecksum, fastBits, ModeToOracle(mode),
                ModeToStep(mode, shortRead), 0, dstSize, 0, CorpusConst.NoCallback, payload, int.MinValue, false);
            byte[] dst = rec.Dst;
            int res = rec.Res;
            bool ok = !rec.Threw && res == payload.Length && SeqEqual(dst, payload, payload.Length);
            Chk(ok, $"{name} [mode={mode} fast={fastBits} nh={noHeader} nc={noChecksum}] -> {res} (want {payload.Length})");
        }

        // ---------------------------------------------------------------- adler32

        static void CheckAdler(string label, byte[] data, int offset, int length, uint expect)
        {
            // The published constant is validated by the naive reference implementation before it
            // is used to judge anything, then cross-checked against C by the A record.
            uint reference = RefAdler(data, offset, length);
            Chk(reference == expect, $"reference adler32({label}) == {expect:x8} (naive mod-per-byte: {reference:x8})");
            uint got = UfbxiInflate.UfbxiAdler32(data, offset, length);
            Chk(got == expect, $"adler32({label})=={expect:x8} (got {got:x8})");
            var r = new ARec();
            r.Sid = AddStream(data);
            r.Off = offset;
            r.Len = length;
            r.PortValue = got;
            r.Expect = expect;
            r.ExpectSource = label;
            s_corpus.AddA(r);
        }

        static void RunAdler32()
        {
            // Published Adler-32 values: a = 1 + sum(bytes) mod 65521, b = sum of the running `a`
            // mod 65521, result = (b << 16) | a. Each one is cross-checked against the C answer.
            //   "a":            a = 1+97 = 0x62,          b = 0x62
            //   "abc":          a = 1+97+98+99 = 0x127,   b = 98+294+589 -> 0x24d
            //   "1234567890":   a = 1+525 = 0x20e,        b = 2860 = 0xb2c
            CheckAdler("\"\"", new byte[0], 0, 0, 0x00000001u);
            CheckAdler("\"a\"", Bytes("a"), 0, 1, 0x00620062u);
            CheckAdler("\"abc\"", Bytes("abc"), 0, 3, 0x024d0127u);
            CheckAdler("\"1234567890\"", Bytes("1234567890"), 0, 10, 0x0b2c020eu);

            int[] sizes = { 1, 2, 7, 15, 16, 17, 31, 63, 64, 65, 255, 256, 257, 5551, 5552, 5553, 65535, 65536, 100000 };
            foreach (int n in sizes)
            {
                byte[] buf = new byte[n];
                for (int i = 0; i < n; i++) buf[i] = (byte)(i * 31 + (i >> 8) * 7 + 1);
                uint got = UfbxiInflate.UfbxiAdler32(buf, 0, n);
                uint want = RefAdler(buf, 0, n);
                Chk(got == want, $"adler32(len={n}) == {want:x8} (got {got:x8})");
                if (n == 16 || n == 65 || n == 257 || n == 5553)
                {
                    var a = new ARec();
                    a.Sid = AddStream(buf);
                    a.Off = 0;
                    a.Len = n;
                    a.PortValue = got;
                    s_corpus.AddA(a);
                }

                if (n > 0)
                {
                    byte[] off = new byte[n + 13];
                    Array.Copy(buf, 0, off, 13, n);
                    uint gotOff = UfbxiInflate.UfbxiAdler32(off, 13, n);
                    Chk(gotOff == want, $"adler32(len={n}, offset=13) == {want:x8} (got {gotOff:x8})");
                    if (n == 17 || n == 64 || n == 256)
                    {
                        // C: `ufbxi_adler32(stream + off, len)` - the same window at an offset.
                        var a = new ARec();
                        a.Sid = AddStream(off);
                        a.Off = 13;
                        a.Len = n;
                        a.PortValue = gotOff;
                        s_corpus.AddA(a);
                    }
                }
            }

            byte[] allFf = new byte[70000];
            for (int i = 0; i < allFf.Length; i++) allFf[i] = 0xff;
            Chk(UfbxiInflate.UfbxiAdler32(allFf, 0, allFf.Length) == RefAdler(allFf, 0, allFf.Length),
                "adler32(0xff x 70000) worst-case accumulation");
        }

        // ---------------------------------------------------------------- bit reverse

        static uint NaiveReverse(uint v, int n)
        {
            uint r = 0;
            for (int i = 0; i < n; i++) r = r << 1 | (v >> i & 1u);
            return r;
        }

        static void RunBitReverse()
        {
            for (uint n = 1; n <= 16; n++)
            {
                for (uint v = 0; v < 256; v++)
                {
                    uint got = UfbxiBitStream.BitReverse(v, n);
                    uint want = NaiveReverse(v, (int)n);
                    if (got != want)
                    {
                        Chk(false, $"ufbxi_bit_reverse({v:x},{n}) == {want:x} (got {got:x})");
                        return;
                    }
                }
            }
            Chk(true, "ufbxi_bit_reverse matches a naive loop for v<256, n=1..16");

            Chk(UfbxiBitStream.BitReverse(0x1234u, 16) == 0x2c48u, "ufbxi_bit_reverse(0x1234,16)==0x2c48");
            Chk(UfbxiBitStream.BitReverse(0x0030u, 8) == 0x000cu, "ufbxi_bit_reverse(0x30,8)==0x0c");
            Chk(UfbxiBitStream.BitReverse(0x0000u, 7) == 0x0000u, "ufbxi_bit_reverse(0,7)==0");
            // 9-bit 0x0ff reversed is 0x1fe (the low bit moves to position 8), not 0x1ff.
            Chk(UfbxiBitStream.BitReverse(0x00ffu, 9) == 0x01feu, "ufbxi_bit_reverse(0xff,9)==0x1fe");
        }

        // ---------------------------------------------------------------- round trips

        static void RunRoundTrip()
        {
            var payloads = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("empty", new byte[0]),
                new KeyValuePair<string, byte[]>("one", Bytes("a")),
                new KeyValuePair<string, byte[]>("hello", Bytes("hello hello hello hello hello")),
                new KeyValuePair<string, byte[]>("text", Repeat("the quick brown fox jumps over the lazy dog. ", 200)),
                new KeyValuePair<string, byte[]>("zeros-100k", new byte[100 * 1024]),
                new KeyValuePair<string, byte[]>("random-64k", Rand(64 * 1024, 12345u)),
                new KeyValuePair<string, byte[]>("boundary-65535", Rand(65535, 7u)),
                new KeyValuePair<string, byte[]>("boundary-65536", Rand(65536, 7u)),
                new KeyValuePair<string, byte[]>("boundary-65537", Rand(65537, 7u)),
                new KeyValuePair<string, byte[]>("small-mixed", Rand(300, 99u)),
            };

            CompressionLevel[] levels = { CompressionLevel.Optimal, CompressionLevel.SmallestSize, CompressionLevel.NoCompression };
            int[] fastBits = { 0, 8, 10 };

            foreach (var kv in payloads)
            {
                foreach (CompressionLevel level in levels)
                {
                    byte[] zlib, raw;
                    if (kv.Value.Length == 0)
                    {
                        // System.IO.Compression writes no DEFLATE block at all for an empty
                        // payload, which leaves nothing for `ufbx_inflate()` to terminate on, so
                        // craft the two canonical empty blocks instead: a fixed-Huffman block
                        // carrying only the end-of-block code, and an empty stored block.
                        if (level == CompressionLevel.NoCompression)
                        {
                            var ws = new BitWriter();
                            ws.Field(1, 1);
                            ws.Field(0, 2);
                            ws.Align();
                            ws.Field(0, 16);
                            ws.Field(0xffff, 16);
                            raw = ws.ToArray();
                        }
                        else
                        {
                            var wf = new BitWriter();
                            wf.Field(1, 1);
                            wf.Field(1, 2);
                            EmitFixedOps(wf, new List<Op>());
                            raw = wf.ToArray();
                        }
                        zlib = FinishZlib(raw, kv.Value, true);
                    }
                    else
                    {
                        zlib = ZlibCompress(kv.Value, level);
                        raw = RawDeflate(kv.Value, level);
                    }
                    uint adler = RefAdler(kv.Value, 0, kv.Value.Length);
                    byte[] rawWithAdler = Concat(raw, BeAdler(adler));

                    foreach (int fb in fastBits)
                    {
                        ExpectBytes(kv.Key, kv.Value, zlib, false, false, fb, Mode.WholeMemory);
                        ExpectBytes(kv.Key, kv.Value, rawWithAdler, true, false, fb, Mode.WholeMemory);
                        ExpectBytes(kv.Key, kv.Value, raw, true, true, fb, Mode.WholeMemory);
                        ExpectBytes(kv.Key, kv.Value, zlib, false, true, fb, Mode.WholeMemory);
                        ExpectBytes(kv.Key, kv.Value, zlib, false, false, fb, Mode.BlockingReader);
                    }

                    // A short-reading read_fn cannot satisfy ufbx's refill contract: the chunk is
                    // padded with zeros once it cannot be filled (ufbx.c:2076-2085), so the decoder
                    // walks into the padding and the stream is corrupted. Which error code that
                    // produces is recorded per case and asserted against C by the D differential.
                    DRec s3 = AddD($"{kv.Key} short read_fn", zlib, false, false, 0, 1, 3, 0,
                        kv.Value.Length == 0 ? 1 : kv.Value.Length, 0, CorpusConst.NoCallback,
                        null, int.MinValue, true);
                    int res3 = s3.Threw ? int.MinValue : s3.Res;
                    NoteCode($"{kv.Key}/3B", res3);
                    Chk(!s3.Threw && s3.Res < 0, $"{kv.Key} short read_fn (3 bytes/call) errors (got {res3})");

                    DRec s1 = AddD($"{kv.Key} read_fn 1 byte", zlib, false, false, 0, 1, 1, 0,
                        kv.Value.Length == 0 ? 1 : kv.Value.Length, 0, CorpusConst.NoCallback,
                        null, int.MinValue, true);
                    int res1 = s1.Threw ? int.MinValue : s1.Res;
                    NoteCode($"{kv.Key}/1B", res1);
                    Chk(!s1.Threw && s1.Res < 0, $"{kv.Key} read_fn (1 byte/call) errors (got {res1})");
                }
            }
        }

        static byte[] Repeat(string s, int count)
        {
            byte[] one = Bytes(s);
            byte[] r = new byte[one.Length * count];
            for (int i = 0; i < count; i++) Array.Copy(one, 0, r, i * one.Length, one.Length);
            return r;
        }

        // ---------------------------------------------------------------- empty input

        // A zero-length stream. The only invariant this section can assert on its own is that it
        // fails: `ufbx_inflate()` reads at least a zlib header (ufbx.c:3161-3171) or a 3-bit block
        // header (ufbx.c:3174-3182) before it can produce anything, and neither is present. With
        // `total_size == 0` the stream is empty on both sides of the `data`/`read_fn` split
        // (`input_left = total_size - data_size`, ufbx.c:2108), so `ufbxi_bit_chunk_refill()` pads
        // the chunk once from `input->data`/the local buffer and reports EOF
        // (`stop_error = -31`) on the second pass (ufbx.c:2078-2085) - which byte of the zero
        // padding the header decodes from, and therefore which error code comes out, is exactly
        // what the D/B differential pins against C.
        static void RunEmptyInput()
        {
            byte[] empty = new byte[0];

            foreach (int fb in new[] { 0, 8, 10 })
            {
                DRec withHeader = AddD($"empty input, zlib header (fast={fb})", empty, false, false, fb, 0,
                    0, 0, 1, 0, CorpusConst.NoCallback, null, int.MinValue, true);
                Chk(!withHeader.Threw && withHeader.Res < 0,
                    $"empty input with a zlib header fails (fast={fb}, got {withHeader.Res})");

                DRec raw = AddD($"empty input, raw block (fast={fb})", empty, true, true, fb, 0,
                    0, 0, 1, 0, CorpusConst.NoCallback, null, int.MinValue, true);
                Chk(!raw.Threw && raw.Res < 0,
                    $"empty input without a zlib header fails (fast={fb}, got {raw.Res})");
            }

            // The same empty stream handed over by a `read_fn()` that immediately reports EOF.
            DRec viaReader = AddD("empty input via read_fn", empty, false, false, 0, 1,
                CorpusConst.SizeMax, 0, 1, 0, CorpusConst.NoCallback, null, int.MinValue, true);
            Chk(!viaReader.Threw && viaReader.Res < 0, $"empty input via read_fn fails (got {viaReader.Res})");

            DRec prefixAndReader = AddD("empty input, read_fn after a 0-byte prefix", empty, false, false, 0,
                2, 0, 0, 1, 0, CorpusConst.NoCallback, null, int.MinValue, true);
            Chk(!prefixAndReader.Threw && prefixAndReader.Res < 0,
                $"empty input with a 0-byte prefix fails (got {prefixAndReader.Res})");

            // Progress callbacks on an empty input: `ufbxi_bit_yield()` runs even though nothing
            // was ever read, and `bytes_read`/`bytes_total` must agree with C exactly (DiffD).
            DRec withProgress = AddD("empty input with progress", empty, false, false, 0, 1,
                CorpusConst.SizeMax, 0, 1, 16, CorpusConst.AlwaysContinue, null, int.MinValue, true);
            Chk(!withProgress.Threw && withProgress.Res < 0,
                $"empty input with progress fails (got {withProgress.Res}, {withProgress.CbCount} callback(s))");

            // A zero-size destination cannot be written to; the header check still comes first.
            DRec zeroDst = AddD("empty input, zero-size destination", empty, false, false, 0, 0,
                0, 0, 0, 0, CorpusConst.NoCallback, null, int.MinValue, true);
            Chk(!zeroDst.Threw && zeroDst.Res < 0, $"empty input into a 0-byte dst fails (got {zeroDst.Res})");

            // Raw bit-stream driver over the empty input: one padded chunk, then EOF.
            BRec drv = AddB("empty input driver", empty, 0, CorpusConst.SizeMax, 0, 0,
                CorpusConst.NoCallback, 1024, null);
            Chk(drv.StopError == -31, $"empty input driver stops with -31 after {drv.Steps} refills " +
                $"(got {drv.StopError})");
            Chk(drv.Steps < 1024, $"empty input driver terminates before the step limit ({drv.Steps})");

            BRec drvBuf = AddB("empty input driver, user buffer", empty, 0, CorpusConst.SizeMax, 1024, 0,
                CorpusConst.NoCallback, 1024, null);
            Chk(drvBuf.StopError == -31, $"empty input driver with a user buffer stops with -31 " +
                $"(got {drvBuf.StopError})");
        }

        // ---------------------------------------------------------------- static tables

        // Check a decoded static-tree symbol against RFC 1951 3.2.6 plus the packing rules of
        // `ufbxi_huff_build_imp()` (ufbx.c:2366-2420): `total_bits` includes the LUT extra bits,
        // the flags come from the LUT (plus FAST for non-END symbols that fit in `fast_bits`),
        // symbols carrying a base value are renumbered to a 1-based `extra_shift_base[]` index,
        // and the whole thing is stored in a `ufbxi_huff_sym` (`uint16_t`) so the sum of
        // `i << 8 | bits` and the LUT entry wraps: the end-of-block symbol 256 gets value 0 and
        // the invalid length symbols 286/287 get 222/223.
        static void CheckStaticSym(string what, UfbxiHuffTree tree, ushort sym, uint symIndex,
            uint codeLen, uint extra, uint fastBits, uint wantExtraIndex)
        {
            uint extraBits = extra & 0x1fu;
            uint wantTotalBits = (codeLen + extraBits) & 0x1fu;
            uint wantFlags = extra & 0xe0u;
            bool isEnd = (wantFlags & (uint)UfbxiHuff.SymEnd) != 0;
            bool wantEntry = !isEnd && (extra & 0xffff001fu) != 0u;
            if (!isEnd && codeLen <= fastBits) wantFlags |= (uint)UfbxiHuff.SymFast;
            uint wrapped = (uint)(ushort)(((symIndex << 8) | codeLen) + extra);
            uint wantValue = wantEntry ? wantExtraIndex : wrapped >> 8;

            Chk(UfbxiHuff.SymTotalBits(sym) == wantTotalBits,
                $"{what}: total_bits {UfbxiHuff.SymTotalBits(sym)} == {wantTotalBits}");
            Chk((sym & 0xe0u) == wantFlags, $"{what}: flags {(sym & 0xe0u):x} == {wantFlags:x}");
            Chk(UfbxiHuff.SymValue(sym) == wantValue,
                $"{what}: value {UfbxiHuff.SymValue(sym)} == {wantValue}");

            if (wantEntry)
            {
                uint v = UfbxiHuff.SymValue(sym);
                uint wantBase = (extra & 0xffff0000u) | codeLen;
                ushort wantMask = (ushort)((1u << (int)extraBits) - 1u);
                Chk(v == wantExtraIndex && tree.ExtraShiftBase[v] == wantBase && tree.ExtraMask[v] == wantMask,
                    $"{what}: extra[{v}] base {tree.ExtraShiftBase[v]:x8} mask {tree.ExtraMask[v]:x} == " +
                    $"want {wantBase:x8}/{wantMask:x} at {wantExtraIndex}");
            }
        }

        static void RunStaticHuffTables()
        {
            var trees = new UfbxiTrees();
            UfbxiHuff.InitStaticHuff(trees, null);
            Chk(trees.FastBits == UfbxiHuff.HuffFastBits, "static trees fast_bits == UFBXI_HUFF_FAST_BITS (10)");

            // RFC 1951 3.2.6 fixed literal/length codes.
            var table = new List<(int lo, int hi, int len, uint start)>
            {
                (0, 143, 8, 0x30),
                (144, 255, 9, 0x190),
                (256, 279, 7, 0x00),
                (280, 287, 8, 0xc0),
            };

            var lenOf = new int[288];
            var codeOf = new uint[288];
            foreach (var e in table)
            {
                for (int i = e.lo; i <= e.hi; i++)
                {
                    lenOf[i] = e.len;
                    codeOf[i] = e.start + (uint)(i - e.lo);
                }
            }

            // Cross-check the canonical bookkeeping the build derives.
            Chk(trees.LitLength.PastMaxCode[7] == 24 && trees.LitLength.PastMaxCode[8] == 200
                && trees.LitLength.PastMaxCode[9] == 512, "lit_length past_max_code[7..9] == 24,200,512");
            Chk(trees.LitLength.CodeToSorted[7] == 0 && trees.LitLength.CodeToSorted[8] == -24
                && trees.LitLength.CodeToSorted[9] == -224, "lit_length code_to_sorted[7..9] == 0,-24,-224");
            Chk(trees.LitLength.NumSymbols == 288, "lit_length num_symbols == 288");
            Chk(trees.LitLength.EndOfBlockBits == 0u, "lit_length end_of_block_bits == reverse(code 0, 7 bits) == 0");

            int fillerMismatch = 0;
            int extraIndex = 0;
            for (int i = 0; i < 288; i++)
            {
                uint rev = UfbxiBitStream.BitReverse(codeOf[i], (uint)lenOf[i]);
                ushort a = UfbxiHuff.HuffDecodeBits(trees.LitLength, rev, 10, 1023);
                ushort b = UfbxiHuff.HuffDecodeBits(trees.LitLength, rev | 0x155ul << 10, 10, 1023);
                ushort c = UfbxiHuff.HuffDecodeBits(trees.LitLength, rev | 0x2aaul << 10, 10, 1023);
                if (a != b || a != c) fillerMismatch++;

                uint extra = i >= 256 ? UfbxiHuff.DeflateLengthLut[i - 256] : 0u;
                bool isEnd = i == 256 || i >= 286;
                if (i >= 256 && !isEnd) extraIndex++;
                CheckStaticSym($"static lit_length sym {i}", trees.LitLength, a, (uint)i,
                    (uint)lenOf[i], extra, 10, (uint)extraIndex);
            }
            Chk(fillerMismatch == 0, "static lit_length: extra filler bits never change the decoded symbol");
            Chk(extraIndex == 29, $"static lit_length: 29 length codes with extra data (got {extraIndex})");
            Chk(trees.LitLength.ExtraShiftBase[29] != 0 && trees.LitLength.ExtraShiftBase[30] == 0,
                "lit_length extra table spans indices 1..29");

            // Two well-known reference symbols, spelled out rather than table-driven.
            CheckStaticSym("static lit 'A'", trees.LitLength,
                UfbxiHuff.HuffDecodeBits(trees.LitLength, UfbxiBitStream.BitReverse(0x71u, 8), 10, 1023),
                65u, 8, 0u, 10, 0u);
            CheckStaticSym("static EOB (256)", trees.LitLength,
                UfbxiHuff.HuffDecodeBits(trees.LitLength, 0u, 10, 1023), 256u, 7,
                UfbxiHuff.DeflateLengthLut[0], 10, 0u);
            CheckStaticSym("static length 257 (len 3)", trees.LitLength,
                UfbxiHuff.HuffDecodeBits(trees.LitLength, UfbxiBitStream.BitReverse(1u, 7), 10, 1023), 257u, 7,
                UfbxiHuff.DeflateLengthLut[1], 10, 1u);
            CheckStaticSym("static length 285 (len 258)", trees.LitLength,
                UfbxiHuff.HuffDecodeBits(trees.LitLength, UfbxiBitStream.BitReverse(0xc5u, 8), 10, 1023), 285u, 8,
                UfbxiHuff.DeflateLengthLut[29], 10, 29u);
            CheckStaticSym("static bad length 286", trees.LitLength,
                UfbxiHuff.HuffDecodeBits(trees.LitLength, UfbxiBitStream.BitReverse(0xc6u, 8), 10, 1023), 286u, 8,
                UfbxiHuff.DeflateLengthLut[30], 10, 0u);

            int distExtra = 0;
            for (int i = 0; i < 32; i++)
            {
                uint rev = UfbxiBitStream.BitReverse((uint)i, 5);
                ushort a = UfbxiHuff.HuffDecodeBits(trees.Dist, rev, 10, 1023);
                ushort b = UfbxiHuff.HuffDecodeBits(trees.Dist, rev | 0xfful << 10, 10, 1023);
                Chk(a == b, $"static dist sym {i}: filler independent");
                if (i < 30) distExtra++;
                CheckStaticSym($"static dist sym {i}", trees.Dist, a, (uint)i, 5, UfbxiHuff.DeflateDistLut[i],
                    10, (uint)distExtra);
            }
            Chk(trees.Dist.NumSymbols == 32, "dist num_symbols == 32");
            Chk(distExtra == 30, $"static dist: 30 distance codes with extra data (got {distExtra})");
            Chk(trees.Dist.ExtraShiftBase[30] != 0 && trees.Dist.ExtraShiftBase[31] == 0,
                "dist extra table spans indices 1..30");

            // fast_bits == 8 must still resolve every fixed code (long tables + sorted fallback).
            var slow = new UfbxiTrees();
            var input = new UfbxInflateInput();
            input.InternalFastBits = 8;
            UfbxiHuff.InitStaticHuff(slow, input);
            Chk(slow.FastBits == 8, "init_static_huff honours internal_fast_bits");
            int slowMismatch = 0;
            extraIndex = 0;
            for (int i = 0; i < 288; i++)
            {
                uint rev = UfbxiBitStream.BitReverse(codeOf[i], (uint)lenOf[i]);
                ushort a = UfbxiHuff.HuffDecodeBits(slow.LitLength, rev, 8, 255);

                // Filler above the 15th bit is never consulted: with `fast_bits == 8` the long
                // table (bits 8..12) and the `past_max_code` walk (bits 8..14) both consume the
                // tail, so only bits >= fast_bits + UFBXI_HUFF_MAX_BITS can differ.
                ushort b = UfbxiHuff.HuffDecodeBits(slow.LitLength, rev | 0x155ul << (8 + 15), 8, 255);
                if (a != b) slowMismatch++;

                uint extra = i >= 256 ? UfbxiHuff.DeflateLengthLut[i - 256] : 0u;
                bool isEnd = i == 256 || i >= 286;
                if (i >= 256 && !isEnd) extraIndex++;
                CheckStaticSym($"fast8 lit_length sym {i}", slow.LitLength, a, (uint)i,
                    (uint)lenOf[i], extra, 8, (uint)extraIndex);
            }
            Chk(slowMismatch == 0, "static lit_length with fast_bits=8 decodes all 288 codes");

            distExtra = 0;
            for (int i = 0; i < 32; i++)
            {
                uint rev = UfbxiBitStream.BitReverse((uint)i, 5);
                ushort a = UfbxiHuff.HuffDecodeBits(slow.Dist, rev, 8, 255);
                if (i < 30) distExtra++;
                CheckStaticSym($"fast8 dist sym {i}", slow.Dist, a, (uint)i, 5, UfbxiHuff.DeflateDistLut[i],
                    8, (uint)distExtra);
            }

            // Retain caching: the static trees are built once per retain object.
            var retain = new UfbxInflateRetain();
            retain.Initialized = false;
            UfbxiInflate.InflateInitRetain(retain);
            Chk(retain.Initialized, "ufbxi_inflate_init_retain sets initialized");
            UfbxiInflate.InflateInitRetain(retain);
            Chk(retain.Initialized, "ufbxi_inflate_init_retain is idempotent");
        }

        // ---------------------------------------------------------------- table build

        static void RunCustomTrees()
        {
            // Complete tree with 14- and 15-bit codes: C: lengths {1..13, 14, 15, 15}
            // (Kraft sum 1 - 2^-13 + 2^-14 + 2*2^-15 == 1).
            var bits = new List<byte>();
            for (int i = 1; i <= 14; i++) bits.Add((byte)i);
            bits.Add(15);
            bits.Add(15);
            byte[] symBits = bits.ToArray();
            int symCount = symBits.Length;

            // Canonical codes from the lengths (RFC 1951 3.2.2).
            int maxLen = 15;
            var blCount = new int[maxLen + 1];
            for (int i = 0; i < symCount; i++) blCount[symBits[i]]++;
            var nextCode = new uint[maxLen + 1];
            uint code = 0;
            for (int len = 1; len <= maxLen; len++)
            {
                code = (code + (uint)blCount[len - 1]) << 1;
                nextCode[len] = code;
            }
            var symCode = new uint[symCount];
            for (int i = 0; i < symCount; i++) symCode[i] = nextCode[symBits[i]]++;

            foreach (uint fastBits in new uint[] { 8, 10 })
            {
                var tree = new UfbxiHuffTree();
                var counts = new uint[UfbxiHuff.HuffMaxBits];
                for (int i = 0; i < symCount; i++) counts[symBits[i]]++;
                int err = UfbxiHuff.HuffBuildImp(tree, symBits, 0, (uint)symCount, null, int.MaxValue, fastBits, counts);
                Chk(err == 0, $"chain tree build (fast_bits={fastBits}) succeeds");

                int bad = 0;
                for (int i = 0; i < symCount; i++)
                {
                    uint rev = UfbxiBitStream.BitReverse(symCode[i], symBits[i]);
                    ushort s = UfbxiHuff.HuffDecodeBits(tree, rev, fastBits, (1u << (int)fastBits) - 1);
                    if ((uint)UfbxiHuff.SymValue(s) != (uint)i || UfbxiHuff.SymTotalBits(s) != symBits[i]) bad++;
                }
                Chk(bad == 0, $"chain tree (fast_bits={fastBits}): all {symCount} codes resolve, incl. 14/15-bit long codes");
            }

            // Overfull: three 1-bit codes.
            {
                var tree = new UfbxiHuffTree();
                int err = UfbxiHuff.HuffBuild(tree, new byte[] { 1, 1, 1 }, 0, 3, null, int.MaxValue, 10);
                Chk(err == -1, $"overfull table -> -1 (got {err})");
            }
            // Underfull: three 2-bit codes (one code left unused).
            {
                var tree = new UfbxiHuffTree();
                int err = UfbxiHuff.HuffBuild(tree, new byte[] { 2, 2, 2 }, 0, 3, null, int.MaxValue, 10);
                Chk(err == -2, $"underfull table -> -2 (got {err})");
            }
            // A single 1-bit code is legal (ufbx.c:2339) and leaves ErrorSym in the other slots.
            {
                var tree = new UfbxiHuffTree();
                int err = UfbxiHuff.HuffBuild(tree, new byte[] { 0, 0, 0, 1 }, 0, 4, null, int.MaxValue, 8);
                Chk(err == 0, $"single 1-bit code table -> 0 (got {err})");
                ushort even = UfbxiHuff.HuffDecodeBits(tree, 0x02, 8, 255);
                ushort odd = UfbxiHuff.HuffDecodeBits(tree, 0x03, 8, 255);
                Chk((even & UfbxiHuff.SymFast) != 0 && UfbxiHuff.SymValue(even) == 3, "single-code table: valid prefix decodes");
                Chk(odd == UfbxiHuff.ErrorSym, "single-code table: unused prefix decodes to ErrorSym");
            }
        }

        // ---------------------------------------------------------------- crafted blocks

        // Minimal RFC1951 encoder used to drive the decoder through paths that the zlib
        // oracle above cannot be forced into (repeat codes, long codes, invalid tables).
        sealed class BitWriter
        {
            readonly List<byte> m_bytes = new List<byte>();
            int m_cur;
            int m_pos;

            public void Bit(int b)
            {
                m_cur |= (b & 1) << m_pos;
                if (++m_pos == 8)
                {
                    m_bytes.Add((byte)m_cur);
                    m_cur = 0;
                    m_pos = 0;
                }
            }

            // Fixed-width field: least significant bit first.
            public void Field(ulong v, int n)
            {
                for (int i = 0; i < n; i++) Bit((int)(v >> i & 1));
            }

            // Huffman code: most significant bit first.
            public void Code(uint c, int n)
            {
                for (int i = n - 1; i >= 0; i--) Bit((int)(c >> i & 1));
            }

            public void Align()
            {
                if (m_pos != 0)
                {
                    m_bytes.Add((byte)m_cur);
                    m_cur = 0;
                    m_pos = 0;
                }
            }

            public void RawBytes(byte[] data)
            {
                Align();
                for (int i = 0; i < data.Length; i++) m_bytes.Add(data[i]);
            }

            public byte[] ToArray()
            {
                Align();
                return m_bytes.ToArray();
            }
        }

        // Canonical code lengths -> codes (RFC 1951 3.2.2).
        static uint[] CanonicalCodes(byte[] lengths)
        {
            int maxLen = 1;
            foreach (byte l in lengths) if (l > maxLen) maxLen = l;
            var count = new int[maxLen + 1];
            foreach (byte l in lengths) count[l]++;
            var next = new uint[maxLen + 1];
            uint code = 0;
            for (int len = 1; len <= maxLen; len++)
            {
                code = (code + (uint)count[len - 1]) << 1;
                next[len] = code;
            }
            var codes = new uint[lengths.Length];
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] != 0) codes[i] = next[lengths[i]]++;
            }
            return codes;
        }

        // RFC 1951 3.2.5 length / distance tables (independent of the ported LUTs).
        static readonly (int len, int extra)[] LengthTab =
        {
            (3,0),(4,0),(5,0),(6,0),(7,0),(8,0),(9,0),(10,0),(11,1),(13,1),(15,1),(17,1),(19,2),(23,2),(27,2),(31,2),
            (35,3),(43,3),(51,3),(59,3),(67,4),(83,4),(99,4),(115,4),(131,5),(163,5),(195,5),(227,5),(258,0),
        };

        static readonly (int dist, int extra)[] DistTab =
        {
            (1,0),(2,0),(3,0),(4,0),(5,1),(7,1),(9,2),(13,2),(17,3),(25,3),(33,4),(49,4),(65,5),(97,5),(129,6),(193,6),
            (257,7),(385,7),(513,8),(769,8),(1025,9),(1537,9),(2049,10),(3073,10),(4097,11),(6145,11),(8193,12),(12289,12),
            (16385,13),(24577,13),
        };

        static void FixedLitLenCodes(out int[] lengths, out uint[] codes)
        {
            lengths = new int[288];
            codes = new uint[288];
            for (int i = 0; i <= 143; i++) { lengths[i] = 8; codes[i] = (uint)(0x30 + i); }
            for (int i = 144; i <= 255; i++) { lengths[i] = 9; codes[i] = (uint)(0x190 + i - 144); }
            for (int i = 256; i <= 279; i++) { lengths[i] = 7; codes[i] = (uint)(i - 256); }
            for (int i = 280; i <= 287; i++) { lengths[i] = 8; codes[i] = (uint)(0xc0 + i - 280); }
        }

        enum OpKind { Lit, Match }

        struct Op
        {
            public OpKind Kind;
            public int Byte;
            public int Length;
            public int Dist;
            // Overrides for error tests: explicit lit/len and dist symbol numbers.
            public bool ForceLenSym;
            public int LenSym;
            public int LenExtra;
            public bool ForceDistSym;
            public int DistSym;
            public int DistExtra;
        }

        static Op L(int b) { return new Op { Kind = OpKind.Lit, Byte = b }; }
        static Op M(int length, int dist) { return new Op { Kind = OpKind.Match, Length = length, Dist = dist }; }

        static void EmitFixedOps(BitWriter w, List<Op> ops)
        {
            FixedLitLenCodes(out _, out uint[] codes);
            foreach (Op op in ops)
            {
                if (op.Kind == OpKind.Lit)
                {
                    w.Code(codes[op.Byte], op.Byte <= 143 ? 8 : 9);
                    continue;
                }
                int li = op.ForceLenSym ? op.LenSym - 257 : -1;
                int di = op.ForceDistSym ? op.DistSym : -1;
                if (li < 0)
                {
                    for (int i = LengthTab.Length - 1; i >= 0; i--)
                    {
                        if (LengthTab[i].len <= op.Length) { li = i; break; }
                    }
                }
                if (di < 0)
                {
                    for (int i = DistTab.Length - 1; i >= 0; i--)
                    {
                        if (DistTab[i].dist <= op.Dist) { di = i; break; }
                    }
                }
                w.Code(codes[257 + li], 257 + li <= 279 ? 7 : 8);
                if (li < LengthTab.Length && LengthTab[li].extra != 0)
                {
                    int extra = op.ForceLenSym ? op.LenExtra : op.Length - LengthTab[li].len;
                    w.Field((ulong)extra, LengthTab[li].extra);
                }
                // "Distance codes 0-31 are represented by (fixed-length) 5-bit codes" (MSB first).
                w.Code((uint)di, 5);
                if (di < DistTab.Length && DistTab[di].extra != 0)
                {
                    int extra = op.ForceDistSym ? op.DistExtra : op.Dist - DistTab[di].dist;
                    w.Field((ulong)extra, DistTab[di].extra);
                }
            }
            w.Code(codes[256], 7);
        }

        // Wrap a raw DEFLATE body as a zlib stream: 0x78 0x9c header (CM=8, CINFO=7, FCHECK ok,
        // no FDICT/FLEVEL), the body, then the big-endian adler32 of `payload`.
        static byte[] FinishZlib(byte[] body, byte[] payload, bool withAdler)
        {
            byte[] stream = Concat(new byte[] { 0x78, 0x9c }, body);
            if (!withAdler) return stream;
            return Concat(stream, BeAdler(RefAdler(payload, 0, payload.Length)));
        }

        static byte[] BuildStaticStream(List<Op> ops, byte[] payload, bool withAdler)
        {
            var w = new BitWriter();
            w.Field(1, 1);  // BFINAL
            w.Field(1, 2);  // BTYPE = fixed Huffman
            EmitFixedOps(w, ops);
            return FinishZlib(w.ToArray(), payload, withAdler);
        }

        // A dynamic block that transmits exactly the fixed-tree code lengths, exercising the
        // code-length repeat codes 16/17/18 and the HLIT/HDIST/HCLEN boundaries (both at their
        // maximum: 288 lit/len and 32 distance codes, 320 == UFBXI_HUFF_MAX_COMBINED_SYMS).
        static byte[] BuildRleDynamicStream(List<Op> ops, byte[] payload, bool withAdler)
        {
            byte[] combined = FixedCombinedLengths();
            List<(int sym, int extra, int bits)> insns = InsnsFromLengths(combined, out byte[] clLengths);
            return FinishZlib(EmitDynamicHeader(insns, clLengths, 288, 32, w => EmitFixedOps(w, ops)),
                payload, withAdler);
        }

        // The same 288+32 tree transmitted *without* any repeat instruction (one code-length
        // symbol per entry). It exists to attribute a `.NET` rejection of the RLE stream above:
        // if this variant is rejected as well, the cause is the HLIT/HDIST range (RFC 1951 3.2.7
        // quotes 257..286 and 1..30, while ufbx sizes its tables for 288/32 and does not
        // range-check them), not the 16/17/18 repeat decoder.
        static byte[] BuildLiteralDynamicStream(List<Op> ops, byte[] payload, bool withAdler)
        {
            byte[] combined = FixedCombinedLengths();
            var insns = new List<(int sym, int extra, int bits)>();
            for (int i = 0; i < combined.Length; i++) insns.Add((combined[i], 0, 0));
            byte[] clLengths = HuffmanCodeLengths(InstructionFreq(insns));
            return FinishZlib(EmitDynamicHeader(insns, clLengths, 288, 32, w => EmitFixedOps(w, ops)),
                payload, withAdler);
        }

        static int[] InstructionFreq(List<(int sym, int extra, int bits)> insns)
        {
            var freq = new int[19];
            foreach (var ins in insns) freq[ins.sym]++;
            return freq;
        }

        // The code lengths of the fixed (RFC 1951 3.2.6) tree, followed by the 32 5-bit
        // distance codes: 320 entries in total.
        static byte[] FixedCombinedLengths()
        {
            FixedLitLenCodes(out int[] litLenLengths, out _);
            byte[] combined = new byte[288 + 32];
            for (int i = 0; i < 288; i++) combined[i] = (byte)litLenLengths[i];
            for (int i = 0; i < 32; i++) combined[288 + i] = 5;
            return combined;
        }

        // Writes header + code-length instructions, then lets `emitData` write the block body.
        static byte[] EmitDynamicHeader(List<(int sym, int extra, int bits)> insns, byte[] clLengths,
            int numLitLen, int numDist, Action<BitWriter> emitData)
        {
            var w = new BitWriter();
            w.Field(1, 1);  // BFINAL
            w.Field(2, 2);  // BTYPE = dynamic Huffman
            w.Field((ulong)(numLitLen - 257), 5);
            w.Field((ulong)(numDist - 1), 5);
            w.Field(15, 4);  // HCLEN = 19 entries

            uint[] clCodes = CanonicalCodes(clLengths);
            byte[] perm = new byte[19];
            for (int i = 0; i < 19; i++) perm[i] = UfbxiHuff.DeflateCodeLengthPermutation[i];
            for (int i = 0; i < 19; i++) w.Field(clLengths[perm[i]], 3);

            foreach (var ins in insns)
            {
                w.Code(clCodes[ins.sym], clLengths[ins.sym]);
                if (ins.bits != 0) w.Field((ulong)ins.extra, ins.bits);
            }

            emitData(w);
            return w.ToArray();
        }

        static void RunCraftedBlocks()
        {
            // Payload built from the op list so the expected output is known exactly.
            var ops = new List<Op>();
            byte[] payloadExpected;
            {
                var outBuf = new List<byte>();
                foreach (byte b in Bytes("The rainbow"))
                {
                    ops.Add(L(b));
                    outBuf.Add(b);
                }
                // match: copy the first 4 bytes at distance 11
                ops.Add(M(4, 11));
                for (int i = 0; i < 4; i++) outBuf.Add(outBuf[outBuf.Count - 11]);
                // overlapping match with distance 1, length 8 (forces the byte-by-byte copy)
                ops.Add(L((byte)'!'));
                outBuf.Add((byte)'!');
                ops.Add(M(8, 1));
                for (int i = 0; i < 8; i++) outBuf.Add((byte)'!');
                // distance 3, length 8 (partial overlap)
                ops.Add(M(8, 3));
                for (int i = 0; i < 8; i++) outBuf.Add(outBuf[outBuf.Count - 3]);
                // length 16, distance 16 (the exact `min_dist` boundary of the 16-byte copy)
                ops.Add(L(0x41));
                outBuf.Add(0x41);
                ops.Add(L(0x42));
                outBuf.Add(0x42);
                ops.Add(M(16, 16));
                for (int i = 0; i < 16; i++) outBuf.Add(outBuf[outBuf.Count - 16]);
                // long match (max length 258) at a large distance
                while (outBuf.Count < 600) { ops.Add(L(0x5a)); outBuf.Add(0x5a); }
                ops.Add(M(258, 600));
                for (int i = 0; i < 258; i++) outBuf.Add(outBuf[outBuf.Count - 600]);
                payloadExpected = outBuf.ToArray();
            }

            byte[] staticStream = BuildStaticStream(ops, payloadExpected, true);
            CheckAgainstNet("crafted static block", staticStream, payloadExpected);
            foreach (int fb in new[] { 0, 8, 10 })
            {
                ExpectBytes("crafted static", payloadExpected, staticStream, false, false, fb, Mode.WholeMemory);
                ExpectBytes("crafted static", payloadExpected, staticStream, false, false, fb, Mode.BlockingReader);
            }

            byte[] dynamicStream = BuildRleDynamicStream(ops, payloadExpected, true);
            CheckNetAgreesOrRefuses("crafted RLE dynamic block", dynamicStream, payloadExpected);
            foreach (int fb in new[] { 0, 8, 10 })
            {
                ExpectBytes("crafted dynamic", payloadExpected, dynamicStream, false, false, fb, Mode.WholeMemory);
                ExpectBytes("crafted dynamic", payloadExpected, dynamicStream, false, false, fb, Mode.BlockingReader);
            }

            // The same 288/32 tree *without* repeat instructions: `.NET` rejects both variants (see
            // `CheckNetAgreesOrRefuses`), which proves its complaint is the HLIT/HDIST range
            // (RFC 1951 3.2.7 quotes 257..286 and 1..30 while ufbx sizes its tables for 288/32 and
            // never range-checks the header, ufbx.c:2618-2660) - not the 16/17/18 repeat decoder,
            // whose path is covered end-to-end by a stream `.NET` accepts
            // ("long-code dynamic block" below, which uses repeat 17/18 heavily).
            byte[] literalDynamicStream = BuildLiteralDynamicStream(ops, payloadExpected, true);
            CheckNetAgreesOrRefuses("crafted literal dynamic block", literalDynamicStream, payloadExpected);
            foreach (int fb in new[] { 0, 8, 10 })
            {
                ExpectBytes("crafted literal dynamic", payloadExpected, literalDynamicStream, false, false, fb,
                    Mode.WholeMemory);
                ExpectBytes("crafted literal dynamic", payloadExpected, literalDynamicStream, false, false, fb,
                    Mode.BlockingReader);
            }

            // A 258-byte match is the largest DEFLATE match; check the 16-byte copy loop
            // tail with distances 15..17 around the `min_dist` decision.
            foreach (int dist in new[] { 3, 15, 16, 17, 300 })
            {
                var o2 = new List<Op>();
                var exp = new List<byte>();
                for (int i = 0; i < 400; i++) { o2.Add(L((byte)(i * 7 + 3))); exp.Add((byte)(i * 7 + 3)); }
                o2.Add(M(258, dist));
                for (int i = 0; i < 258; i++) exp.Add(exp[exp.Count - dist]);
                byte[] st = BuildStaticStream(o2, exp.ToArray(), true);
                CheckAgainstNet($"crafted match len=258 dist={dist}", st, exp.ToArray());
                ExpectBytes($"crafted match len=258 dist={dist}", exp.ToArray(), st, false, false, 0, Mode.WholeMemory);
            }

            // Multi-block stream: static block followed by a stored block.
            {
                var w = new BitWriter();
                w.Field(0, 1);
                w.Field(1, 2);
                var o = new List<Op> { L((byte)'A'), L((byte)'B'), L((byte)'C') };
                EmitFixedOps(w, o);
                byte[] tail = Bytes("def-7890");
                w.Field(0, 1);   // BFINAL = 0
                w.Field(0, 2);   // BTYPE = stored
                w.Align();
                w.Field((ulong)tail.Length, 16);
                w.Field((ulong)(~tail.Length & 0xffff), 16);
                w.RawBytes(tail);
                var w2 = new BitWriter();
                w2.Field(1, 1);
                w2.Field(0, 2);
                w2.Align();
                byte[] last = Bytes("z");
                w2.Field((ulong)last.Length, 16);
                w2.Field((ulong)(~last.Length & 0xffff), 16);
                w2.RawBytes(last);
                byte[] payload = Concat(Concat(Bytes("ABC"), tail), last);
                byte[] stream = FinishZlib(Concat(w.ToArray(), w2.ToArray()), payload, true);
                CheckAgainstNet("multi-block static+stored+stored", stream, payload);
                ExpectBytes("multi-block static+stored+stored", payload, stream, false, false, 0, Mode.WholeMemory);
                ExpectBytes("multi-block static+stored+stored", payload, stream, false, false, 0, Mode.BlockingReader);
            }

            // A dynamic block whose lit/len table has codes up to 15 bits and whose distance tree
            // is the single-1-bit-code degenerate case (HDIST = 0), so inflate runs the `long_sym`
            // table (fast_bits=10) and the `past_max_code`/`code_to_sorted` fallback
            // (fast_bits=8, ufbx.c:2496-2509) end-to-end.
            {
                byte[] combined = new byte[257 + 1];
                combined[256] = 1;                                  // end of block: the 1-bit code
                for (int i = 0; i < 13; i++) combined[65 + i] = (byte)(2 + i);  // 2..14 bits
                combined[78] = 15;
                combined[79] = 15;
                combined[257] = 1;                                  // single distance code
                // Kraft sum: 2^-1 + sum(2^-2..2^-14) + 2*2^-15 == 1.

                List<(int sym, int extra, int bits)> insns = InsnsFromLengths(combined, out byte[] clLengths);
                byte[] litLen = new byte[257];
                Array.Copy(combined, litLen, 257);
                uint[] codes = CanonicalCodes(litLen);

                byte[] payload = new byte[15];
                for (int i = 0; i < 15; i++) payload[i] = (byte)(65 + i);
                byte[] st = FinishZlib(EmitDynamicHeader(insns, clLengths, 257, 1, w =>
                {
                    for (int i = 0; i < 15; i++) w.Code(codes[65 + i], combined[65 + i]);
                    w.Code(codes[256], 1);
                }), payload, true);

                CheckAgainstNet("long-code dynamic block", st, payload);
                foreach (int fb in new[] { 0, 8, 10 })
                {
                    ExpectBytes("long-code dynamic block", payload, st, false, false, fb, Mode.WholeMemory);
                    ExpectBytes("long-code dynamic block", payload, st, false, false, fb, Mode.BlockingReader);
                }
            }
        }

        static void CheckAgainstNet(string name, byte[] stream, byte[] expected)
        {
            bool ok;
            string why = "";
            try
            {
                using var ms = new MemoryStream(stream);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                z.CopyTo(outMs);
                byte[] got = outMs.ToArray();
                ok = got.Length == expected.Length && SeqEqual(got, expected, expected.Length);
                if (!ok) why = $", .NET produced {got.Length} of {expected.Length} bytes";
            }
            catch (Exception e)
            {
                ok = false;
                why = ", .NET threw " + e.GetType().Name + ": " + e.Message;
            }
            Chk(ok, $"zlib oracle agrees: {name}{why}");
        }

        // Cross-check for the two crafted streams whose dynamic header sits *outside* the range
        // .NET enforces but inside what ufbx accepts: RFC 1951 3.2.7 documents HLIT as 257..286 and
        // HDIST as 1..30, whereas ufbx reads `257 + bits&0x1f` / `1 + bits>>5` (ufbx.c:2618-2619)
        // with no upper bound and sizes its code-length array for the full 288 + 32 entries. So
        // .NET's System.IO.Compression rejects these with `InvalidDataException` while ufbx decodes
        // them. For such vectors the third opinion is only a *control*: either .NET agrees byte for
        // byte, or it refuses outright (before emitting anything, or a correct prefix). Anything
        // else - a wrong byte, a truncated-but-accepted stream - is a genuine divergence.
        static void CheckNetAgreesOrRefuses(string name, byte[] stream, byte[] expected)
        {
            byte[] got;
            Exception fail = null;
            using (var ms = new MemoryStream(stream))
            using (var z = new ZLibStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                var buf = new byte[4096];
                for (;;)
                {
                    int n;
                    try { n = z.Read(buf, 0, buf.Length); } catch (Exception e) { fail = e; break; }
                    if (n <= 0) break;
                    outMs.Write(buf, 0, n);
                }
                got = outMs.ToArray();
            }
            if (fail == null)
            {
                Chk(got.Length == expected.Length && SeqEqual(got, expected, expected.Length),
                    $"zlib oracle agrees: {name} (got {got.Length} of {expected.Length} bytes)");
                return;
            }
            Chk(got.Length <= expected.Length && SeqEqual(got, expected, got.Length),
                $"zlib oracle refusal prefix: {name} ({fail.GetType().Name} after {got.Length} bytes)");
            Console.WriteLine($"  note: {name}: .NET rejects it ({fail.GetType().Name}) after " +
                $"{got.Length}/{expected.Length} bytes - HLIT=288/HDIST=32 is legal for ufbx but not " +
                "for RFC 1951 3.2.7, so the differential against the C oracle is authoritative here");
        }

        // ---------------------------------------------------------------- crafted errors

        static void ExpectErr(string name, int want, byte[] stream, byte[] dst, bool noHeader,
            bool noChecksum, int fastBits, Mode mode)
        {
            DRec rec = AddD(name, stream, noHeader, noChecksum, fastBits, ModeToOracle(mode),
                ModeToStep(mode, 3), 0, dst.Length, 0, CorpusConst.NoCallback, null, want, false);
            int got = rec.Threw ? int.MinValue : rec.Res;
            if (rec.Threw) Console.WriteLine($"  note: {name} threw where C returned a code");
            Chk(got == want, $"{name} -> {want} (got {got})");
        }

        static void RunCraftedErrors()
        {
            var simpleOps = new List<Op> { L((byte)'A'), L((byte)'B'), M(3, 2) };
            // 'A','B' then a length-3 match at distance 2 -> 'A','B','A'.
            byte[] simplePayload = Bytes("ABABA");
            byte[] good = BuildStaticStream(simpleOps, simplePayload, true);

            // -10: literal destination overflow (three literals into a two-byte buffer).
            {
                var w = new BitWriter();
                w.Field(1, 1);
                w.Field(1, 2);
                EmitFixedOps(w, new List<Op> { L((byte)'A'), L((byte)'B'), L((byte)'C') });
                ExpectErr("literal dst overflow", -10, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()),
                    new byte[2], false, true, 0, Mode.WholeMemory);
            }

            // -29: invalid internal_fast_bits
            ExpectErr("fast_bits 9 rejected", -29, good, new byte[16], false, false, 9, Mode.WholeMemory);
            ExpectErr("fast_bits 11 rejected", -29, good, new byte[16], false, false, 11, Mode.WholeMemory);
            ExpectErr("fast_bits 1 rejected", 5, good, new byte[16], false, false, 1, Mode.WholeMemory);
            ExpectErr("fast_bits 0 means default", 5, good, new byte[16], false, false, 0, Mode.WholeMemory);

            // -1 / -2 / -3 / -30 / -9 : ZLIB header + checksum
            byte[] bad = (byte[])good.Clone();
            bad[0] = 0x77; // CM = 7 (not 8)
            ExpectErr("bad compression method", -1, bad, new byte[64], false, false, 0, Mode.WholeMemory);
            bad = (byte[])good.Clone();
            bad[1] = (byte)(bad[1] | 0x20); // FDICT
            ExpectErr("FDICT set", -2, bad, new byte[64], false, false, 0, Mode.WholeMemory);
            bad = (byte[])good.Clone();
            bad[1] = (byte)(bad[1] + 1);   // breaks FCHECK
            ExpectErr("bad FCHECK", -3, bad, new byte[64], false, false, 0, Mode.WholeMemory);
            // CINFO 8 with a valid FCHECK sum: 0x881c == 31 * 1124.
            bad = new byte[] { 0x88, 0x1c };
            ExpectErr("bad window size (CINFO 8)", -30, bad, new byte[64], false, false, 0, Mode.WholeMemory);
            bad = (byte[])good.Clone();
            bad[bad.Length - 1] ^= 0xff;
            ExpectErr("adler32 mismatch", -9, bad, new byte[64], false, false, 0, Mode.WholeMemory);

            // -7: reserved block type
            {
                var w = new BitWriter();
                w.Field(0, 1);
                w.Field(3, 2);
                ExpectErr("block type 3", -7, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()), new byte[64],
                    false, true, 0, Mode.WholeMemory);
            }

            // -4 / -6 / -5: stored blocks
            {
                // LEN 200 but only 8 real bytes behind it: the padded chunk (128 bytes) runs out
                // and there is no more input, so `ufbxi_bit_copy_bytes()` reports source overflow.
                var w = new BitWriter();
                w.Field(1, 1);
                w.Field(0, 2);
                w.Align();
                w.Field(200, 16);
                w.Field(~200 & 0xffff, 16);
                w.RawBytes(new byte[8]);
                ExpectErr("stored stream without data", -5, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()),
                    new byte[256], false, true, 0, Mode.WholeMemory);
            }
            {
                var w = new BitWriter();
                w.Field(1, 1);
                w.Field(0, 2);
                w.Align();
                w.Field(4, 16);
                w.Field(0x0004, 16); // NLEN != ~LEN
                w.RawBytes(Bytes("abcd"));
                ExpectErr("bad NLEN", -4, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()), new byte[64],
                    false, true, 0, Mode.WholeMemory);
            }
            {
                var w = new BitWriter();
                w.Field(1, 1);
                w.Field(0, 2);
                w.Align();
                w.Field(300, 16);
                w.Field(~300 & 0xffff, 16);
                w.RawBytes(new byte[300]);
                ExpectErr("stored overflow", -6, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()), new byte[100],
                    false, true, 0, Mode.WholeMemory);
            }

            // Fixed-Huffman block errors.
            {
                var o = new List<Op> { L((byte)'A'), L((byte)'B') };
                Op badOp = new Op { Kind = OpKind.Match, Length = 3, Dist = 2, ForceLenSym = true, LenSym = 286, ForceDistSym = false };
                o.Add(badOp);
                var w = new BitWriter();
                w.Field(1, 1);
                w.Field(1, 2);
                EmitFixedOps(w, o);
                ExpectErr("bad lit/len code 286", -13, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()), new byte[64],
                    false, true, 0, Mode.WholeMemory);
            }
            {
                var o = new List<Op> { L((byte)'A'), L((byte)'B') };
                o.Add(new Op { Kind = OpKind.Match, Length = 3, Dist = 2, ForceDistSym = true, DistSym = 30 });
                var w = new BitWriter();
                w.Field(1, 1);
                w.Field(1, 2);
                EmitFixedOps(w, o);
                ExpectErr("bad distance code 30", -11, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()), new byte[64],
                    false, true, 0, Mode.WholeMemory);
            }
            {
                var o = new List<Op> { L((byte)'A'), L((byte)'B') };
                o.Add(M(3, 50));
                var w = new BitWriter();
                w.Field(1, 1);
                w.Field(1, 2);
                EmitFixedOps(w, o);
                ExpectErr("match out of bounds", -12, Concat(new byte[] { 0x78, 0x9c }, w.ToArray()), new byte[64],
                    false, true, 0, Mode.WholeMemory);
            }

            // Dynamic header errors. `EmitDynamicHeader()` returns a raw DEFLATE body, so these
            // streams are decoded with `no_header` set (the ZLIB header would be read as part of
            // the block).
            {
                // The code-length alphabet is a complete 3-bit code over {0,5,7,8,9,16,17,18}
                // (`ClLengths()`), so instructions 0/16/17/18 can all be emitted.
                byte[] cl = ClLengths();

                // Repeat 18 overflow: 182 + 138 = 320 is legal, one more entry overshoots.
                var insns = new List<(int sym, int extra, int bits)>();
                int filled = 0;
                AppendZeros(insns, ref filled, 183);
                insns.Add((18, 127, 7));
                Chk(filled == 183, $"fill helper reaches 183 entries (got {filled})");
                byte[] st = EmitDynamicHeader(insns, cl, 288, 32, w => { });
                ExpectErr("repeat 18 overflow", -20, st, new byte[4096], true, true, 0, Mode.WholeMemory);

                // Repeat 17 overflow (318 + 10 > 320).
                insns = new List<(int sym, int extra, int bits)>();
                filled = 0;
                AppendZeros(insns, ref filled, 318);
                insns.Add((17, 7, 3));
                st = EmitDynamicHeader(insns, cl, 288, 32, w => { });
                ExpectErr("repeat 17 overflow", -19, st, new byte[4096], true, true, 0, Mode.WholeMemory);

                // Repeat 16 overflow (316 + 6 > 320); 16 repeats the previous length, so the
                // fill ends with a non-zero code length.
                insns = new List<(int sym, int extra, int bits)>();
                filled = 0;
                AppendZeros(insns, ref filled, 315);
                insns.Add((8, 0, 0));
                insns.Add((16, 3, 2));
                st = EmitDynamicHeader(insns, cl, 288, 32, w => { });
                ExpectErr("repeat 16 overflow", -18, st, new byte[4096], true, true, 0, Mode.WholeMemory);
            }
            {
                // Codelen table with a single 1-bit code: the other half of the code space is
                // pre-filled with `UFBXI_HUFF_ERROR_SYM` (ufbx.c:2356-2360) and a `1` bit hits it
                // (ufbx.c:2556 => -21).
                byte[] clLengths = new byte[19];
                clLengths[0] = 1;
                var insns = new List<(int sym, int extra, int bits)> { (0, 0, 0) };
                byte[] st = EmitDynamicHeader(insns, clLengths, 257, 1, w => w.Field(1, 1));
                ExpectErr("bad codelen code", -21, st, new byte[4096], true, true, 0, Mode.WholeMemory);
            }
            {
                // Codelen table overfull (three 1-bit codes among the permutation).
                byte[] clLengths = new byte[19];
                clLengths[16] = 1;
                clLengths[17] = 1;
                clLengths[18] = 1;
                var insns = new List<(int sym, int extra, int bits)> { (0, 0, 0) };
                byte[] st = EmitDynamicHeader(insns, clLengths, 257, 1, w => w.Field(1, 1));
                ExpectErr("codelen overfull", -14, st, new byte[4096], true, true, 0, Mode.WholeMemory);

                clLengths = new byte[19];
                clLengths[16] = 2;
                clLengths[17] = 2;
                clLengths[18] = 2;
                st = EmitDynamicHeader(insns, clLengths, 257, 1, w => w.Field(1, 1));
                ExpectErr("codelen underfull", -15, st, new byte[4096], true, true, 0, Mode.WholeMemory);
            }
            {
                // Lit/len table overfull: 200 symbols with a 1-bit code (Kraft sum 100).
                byte[] combined = new byte[288 + 32];
                for (int i = 0; i < 200; i++) combined[i] = 1;
                for (int i = 0; i < 32; i++) combined[288 + i] = 5;
                var insns = InsnsFromLengths(combined, out byte[] clLengths);
                byte[] st = EmitDynamicHeader(insns, clLengths, 288, 32, w => w.Field(1, 1));
                ExpectErr("litlen overfull", -16, st, new byte[4096], true, true, 0, Mode.WholeMemory);

                // Lit/len table underfull: three 2-bit codes leave a quarter of the space unused.
                combined = new byte[288 + 32];
                for (int i = 0; i < 3; i++) combined[i] = 2;
                for (int i = 0; i < 32; i++) combined[288 + i] = 5;
                insns = InsnsFromLengths(combined, out clLengths);
                st = EmitDynamicHeader(insns, clLengths, 288, 32, w => w.Field(1, 1));
                ExpectErr("litlen underfull", -17, st, new byte[4096], true, true, 0, Mode.WholeMemory);
            }
            {
                // Distance table overfull / underfull with a valid lit/len table.
                // `ufbxi_huff_build_imp()` returns -1 as soon as `num_codes_left` goes negative
                // (ufbx.c:2306-2309) and -2 when codes are left over at the end (ufbx.c:2337-2341);
                // `ufbxi_init_dynamic_huff()` maps them to -22 + 1 + err (ufbx.c:2660-2661), so
                // overfull is -22 and underfull is -23.
                FixedLitLenCodes(out int[] lengths, out _);
                byte[] combined = new byte[288 + 32];
                for (int i = 0; i < 288; i++) combined[i] = (byte)lengths[i];
                for (int i = 0; i < 32; i++) combined[288 + i] = 1;
                var insns = InsnsFromLengths(combined, out byte[] clLengths);
                byte[] st = EmitDynamicHeader(insns, clLengths, 288, 32, w => w.Field(1, 1));
                ExpectErr("dist overfull (32 x 1 bit)", -22, st, new byte[4096], true, true, 0, Mode.WholeMemory);

                // 32 x 2-bit codes have a Kraft sum of 8, i.e. they are overfull too (-28 codes left
                // after length 2 -> negative `num_codes_left`), not underfull.
                combined = new byte[288 + 32];
                for (int i = 0; i < 288; i++) combined[i] = (byte)lengths[i];
                for (int i = 0; i < 32; i++) combined[288 + i] = 2;
                insns = InsnsFromLengths(combined, out clLengths);
                st = EmitDynamicHeader(insns, clLengths, 288, 32, w => w.Field(1, 1));
                ExpectErr("dist 32 x 2 bits is overfull", -22, st, new byte[4096], true, true, 0, Mode.WholeMemory);

                // Genuinely underfull distance table: three 2-bit codes leave one quarter of the
                // code space unused (Kraft sum 3/4), with a complete lit/len table in front of it.
                combined = new byte[288 + 32];
                for (int i = 0; i < 288; i++) combined[i] = (byte)lengths[i];
                for (int i = 0; i < 3; i++) combined[288 + i] = 2;
                insns = InsnsFromLengths(combined, out clLengths);
                st = EmitDynamicHeader(insns, clLengths, 288, 32, w => w.Field(1, 1));
                ExpectErr("dist underfull (3 x 2 bits)", -23, st, new byte[4096], true, true, 0, Mode.WholeMemory);
            }

            // -31: read past EOF (ufbx.c:2076-2085). The stream is a valid dynamic header whose
            // zero padding keeps decoding as a 1-bit length code plus a 1-bit distance code, so
            // the decoder floods matches (3 bytes of output per 2 bits) and runs into the chunk
            // padding twice -- once in the padded chunk, again on the next refill.
            {
                byte[] combined = new byte[259 + 1];
                combined[65] = 2;    // literal 'A': code 2 (2 bits)
                combined[256] = 3;   // end of block: code 6 (3 bits), never all-zero
                combined[257] = 1;   // length symbol 3: the all-zero 1-bit code
                combined[258] = 3;   // filler to complete the Kraft sum
                combined[259] = 1;   // single distance code (distance 1)

                List<(int sym, int extra, int bits)> insns = InsnsFromLengths(combined, out byte[] clLengths);
                byte[] litLen = new byte[259];
                Array.Copy(combined, litLen, 259);
                uint[] codes = CanonicalCodes(litLen);
                byte[] st = EmitDynamicHeader(insns, clLengths, 259, 1, w => w.Code(codes[65], 2));
                ExpectErr("match flood past EOF via read_fn", -31, st, new byte[65536], true, true, 0, Mode.BlockingReader);
                ExpectErr("match flood past EOF, whole memory", -31, st, new byte[65536], true, true, 0, Mode.WholeMemory);
            }
        }

        // Emit code-length instructions that describe exactly `target` zero-length entries.
        static void AppendZeros(List<(int sym, int extra, int bits)> insns, ref int filled, int target)
        {
            while (filled < target)
            {
                int need = target - filled;
                if (need >= 11)
                {
                    int n = need > 138 ? 138 : need;
                    insns.Add((18, n - 11, 7));
                    filled += n;
                }
                else if (need >= 3)
                {
                    int n = need > 10 ? 10 : need;
                    insns.Add((17, n - 3, 3));
                    filled += n;
                }
                else
                {
                    insns.Add((0, 0, 0));
                    filled += 1;
                }
            }
        }

        static byte[] ClLengths()
        {
            byte[] clLengths = new byte[19];
            int[] clSyms = { 0, 5, 7, 8, 9, 16, 17, 18 };
            foreach (int s in clSyms) clLengths[s] = 3;
            return clLengths;
        }

        static List<(int sym, int extra, int bits)> RleInsns()
        {
            return InsnsFromLengths(FixedCombinedLengths(), out _);
        }

        static List<(int sym, int extra, int bits)> InsnsFromLengths(byte[] combined, out byte[] clLengths)
        {
            var insns = new List<(int sym, int extra, int bits)>();
            for (int idx = 0; idx < combined.Length;)
            {
                int c = combined[idx];
                int run = 1;
                while (idx + run < combined.Length && combined[idx + run] == c) run++;
                idx += run;
                if (c == 0)
                {
                    while (run >= 11)
                    {
                        int n = run > 138 ? 138 : run;
                        insns.Add((18, n - 11, 7));
                        run -= n;
                    }
                    while (run >= 3)
                    {
                        int n = run > 10 ? 10 : run;
                        insns.Add((17, n - 3, 3));
                        run -= n;
                    }
                    for (int k = 0; k < run; k++) insns.Add((0, 0, 0));
                }
                else
                {
                    insns.Add((c, 0, 0));
                    run -= 1;
                    while (run >= 3)
                    {
                        int n = run > 6 ? 6 : run;
                        insns.Add((16, n - 3, 2));
                        run -= n;
                    }
                    for (int k = 0; k < run; k++) insns.Add((c, 0, 0));
                }
            }

            // Give the code-length alphabet a real Huffman code over the instruction frequencies
            // (RFC 1951 3.2.7 caps it at 7 bits). A full binary tree keeps the Kraft sum at
            // exactly 1, so building the code-length tree never reports over/underfull.
            var freq = new int[19];
            foreach (var ins in insns) freq[ins.sym]++;
            clLengths = HuffmanCodeLengths(freq);
            return insns;
        }

        // Classic two-smallest-pairs Huffman over the 19-symbol code-length alphabet, returning
        // per-symbol code lengths (0 for the symbols the tree does not use).
        static byte[] HuffmanCodeLengths(int[] freq)
        {
            var lengths = new byte[freq.Length];
            var syms = new List<int>();
            for (int s = 0; s < freq.Length; s++) if (freq[s] != 0) syms.Add(s);
            if (syms.Count == 0) return lengths;
            if (syms.Count == 1)
            {
                // One code alone is only legal for the degenerate 1-bit tree; pair it with an
                // unused symbol so the alphabet stays complete.
                lengths[syms[0]] = 1;
                lengths[syms[0] == 0 ? 1 : 0] = 1;
                return lengths;
            }

            int n = syms.Count;
            var weight = new int[2 * n];
            var parent = new int[2 * n];
            var leafSym = new int[2 * n];
            for (int i = 0; i < n; i++)
            {
                weight[i] = freq[syms[i]];
                parent[i] = -1;
                leafSym[i] = syms[i];
            }
            int next = n;
            for (int merged = 0; merged < n - 1; merged++)
            {
                var roots = new List<int>();
                for (int i = 0; i < next; i++) if (parent[i] == -1) roots.Add(i);
                roots.Sort((x, y) => weight[x].CompareTo(weight[y]));
                int a = roots[0], b = roots[1];
                weight[next] = weight[a] + weight[b];
                leafSym[next] = -1;
                parent[a] = next;
                parent[b] = next;
                parent[next] = -1;
                next++;
            }

            int maxLen = 0;
            for (int i = 0; i < n; i++)
            {
                int len = 0;
                int p = parent[i];
                while (p != -1) { len++; p = parent[p]; }
                lengths[leafSym[i]] = (byte)len;
                if (len > maxLen) maxLen = len;
            }
            if (maxLen > 7) Chk(false, $"code-length Huffman needs {maxLen} bits (> 7)");
            return lengths;
        }

        // ---------------------------------------------------------------- bit stream

        static void RunBitStreamEquivalence()
        {
            byte[] src = Rand(5000, 4242u);

            var fields = new List<int>();
            for (int i = 0; i < 15; i++) fields.Add(1 + i % 13);

            var reference = new List<int>();
            {
                int pos = 0;
                int total = 0;
                foreach (int f in fields) total += f;
                foreach (int n in fields)
                {
                    int v = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int bit = (src[pos >> 3] >> (pos & 7)) & 1;
                        v |= bit << i;
                        pos++;
                    }
                    reference.Add(v);
                }
            }

            // Progress accounting state, filled by the "with-progress" configuration below.
            int progressCalls = 0;
            bool progressMonotonic = true;
            bool progressTotalOk = true;
            ulong progressLast = ulong.MaxValue;

            var configs = new List<KeyValuePair<string, Func<UfbxiBitStream>>>
            {
                new KeyValuePair<string, Func<UfbxiBitStream>>("whole-memory", () =>
                {
                    var s = new UfbxiBitStream();
                    var input = new UfbxInflateInput { TotalSize = src.Length, Data = src, DataSize = src.Length };
                    s.BitStreamInit(input);
                    return s;
                }),
                new KeyValuePair<string, Func<UfbxiBitStream>>("blocking-read-fn", () =>
                {
                    var s = new UfbxiBitStream();
                    var reader = new ChunkReader(src, int.MaxValue);
                    var input = new UfbxInflateInput { TotalSize = src.Length, Data = new byte[0], DataSize = 0, ReadFn = reader.Read, ReadUser = reader };
                    s.BitStreamInit(input);
                    return s;
                }),
                new KeyValuePair<string, Func<UfbxiBitStream>>("blocking+user-buffer", () =>
                {
                    var s = new UfbxiBitStream();
                    var reader = new ChunkReader(src, int.MaxValue);
                    var input = new UfbxInflateInput
                    {
                        TotalSize = src.Length,
                        Data = new byte[0],
                        DataSize = 0,
                        ReadFn = reader.Read,
                        ReadUser = reader,
                        Buffer = new byte[4096],
                        BufferSize = 4096,
                    };
                    s.BitStreamInit(input);
                    return s;
                }),
                new KeyValuePair<string, Func<UfbxiBitStream>>("with-progress", () =>
                {
                    var s = new UfbxiBitStream();
                    var reader = new ChunkReader(src, int.MaxValue);
                    var input = new UfbxInflateInput
                    {
                        TotalSize = src.Length,
                        Data = new byte[0],
                        DataSize = 0,
                        ReadFn = reader.Read,
                        ReadUser = reader,
                        ProgressIntervalHint = 64,
                    };
                    input.ProgressCb.Fn = (user, p) =>
                    {
                        progressCalls++;
                        if (p.BytesRead < progressLast) progressMonotonic = false;
                        progressLast = p.BytesRead;
                        if (p.BytesTotal != (ulong)src.Length) progressTotalOk = false;
                        return UfbxProgressResult.Continue;
                    };
                    s.BitStreamInit(input);
                    return s;
                }),
                new KeyValuePair<string, Func<UfbxiBitStream>>("initial-4096-slice", () =>
                {
                    var s = new UfbxiBitStream();
                    var input = new UfbxInflateInput { TotalSize = 4096, Data = src, DataSize = 4096 };
                    s.BitStreamInit(input);
                    return s;
                }),
                new KeyValuePair<string, Func<UfbxiBitStream>>("tiny-initial-chunk", () =>
                {
                    var s = new UfbxiBitStream();
                    var reader = new ChunkReader(src, int.MaxValue);
                    // `data`/`data_size` hand over the first 4 bytes, `read_fn()` continues after
                    // them (ufbx.c:2108: `input_left = total_size - data_size`).
                    reader.Skip(4);
                    var input = new UfbxInflateInput { TotalSize = src.Length, Data = src, DataSize = 4, ReadFn = reader.Read, ReadUser = reader };
                    s.BitStreamInit(input);
                    return s;
                }),
            };

            foreach (var cfg in configs)
            {
                UfbxiBitStream s = cfg.Value();
                ulong bits = s.Bits;
                int left = s.Left;
                int data = s.ChunkPtr;
                var got = new List<int>();
                foreach (int n in fields)
                {
                    s.BitRefill(ref bits, ref left, ref data);
                    got.Add((int)(bits & ((1ul << n) - 1)));
                    bits >>= n;
                    left -= n;
                }
                bool same = got.Count == reference.Count;
                if (same)
                {
                    for (int i = 0; i < got.Count; i++)
                    {
                        if (got[i] != reference[i]) { same = false; break; }
                    }
                }
                Chk(same, $"bit stream {cfg.Key}: field extraction matches the reference");
                Chk(left >= 0 && left <= 63, $"bit stream {cfg.Key}: left {left} in [0,63]");
            }

            // The same configurations as corpus records, so the whole refill trace (`bits`, `left`,
            // every chunk pointer, `input_left`, which array is live and `stop_error`) is diffed
            // against the real `ufbxi_bit_refill()`/`ufbxi_bit_yield()` in C, step by step.
            byte[] slice = new byte[4096];
            Array.Copy(src, slice, 4096);
            int[] fieldArr = fields.ToArray();
            var corpusConfigs = new List<Bcfg>
            {
                new Bcfg("whole-memory", src, src.Length, 0, 0, 0, CorpusConst.NoCallback),
                new Bcfg("blocking-read-fn", src, 0, CorpusConst.SizeMax, 0, 0, CorpusConst.NoCallback),
                new Bcfg("blocking+user-buffer", src, 0, CorpusConst.SizeMax, 4096, 0, CorpusConst.NoCallback),
                new Bcfg("user-buffer-256-ignored", src, 0, CorpusConst.SizeMax, 256, 0, CorpusConst.NoCallback),
                new Bcfg("with-progress", src, 0, CorpusConst.SizeMax, 0, 64, CorpusConst.AlwaysContinue),
                new Bcfg("initial-4096-slice", slice, slice.Length, 0, 0, 0, CorpusConst.NoCallback),
                new Bcfg("tiny-initial-chunk", src, 4, CorpusConst.SizeMax, 0, 0, CorpusConst.NoCallback),
                new Bcfg("prefix-64-plus-7byte-reader", src, 64, 7, 0, 0, CorpusConst.NoCallback),
            };
            foreach (Bcfg cc in corpusConfigs)
            {
                List<int> got;
                AddB("bit stream " + cc.Name, cc.Stream, cc.DataSize, cc.Step, cc.BufSz, cc.Hint,
                    cc.CancelAt, 15, fieldArr, out got);
                bool same = got != null && got.Count == reference.Count;
                if (same)
                {
                    for (int i = 0; i < got.Count; i++)
                    {
                        if (got[i] != reference[i]) { same = false; break; }
                    }
                }
                Chk(same, $"bit stream {cc.Name}: corpus field extraction matches the reference");
            }

            // `progress_interval_hint = 64` over a 5000-byte input driven one bit at a time: the
            // yields fire every 64 bytes (ufbx.c:2141-2145, 2174-2178) until the reader is drained,
            // after which the second padding pass reports EOF with -31 (ufbx.c:2076-2085).
            // A 1-bit step advances the chunk pointer by one byte every 8 refills (`data +=
            // (63 - left) >> 3`, ufbx.c:2194), so ~5000 bytes need more than `length * 8` steps;
            // the bound below is generous and the exact step where C stops is part of the trace.
            {
                BRec r = AddB("bit stream progress driver", src, 0, CorpusConst.SizeMax, 0, 64,
                    CorpusConst.AlwaysContinue, 45000, null);
                Chk(r.StopError == -31, $"bit stream progress driver reaches EOF (-31), got {r.StopError} after {r.Steps} steps");
                Chk(r.Prog != null && r.Prog.Count > 50, $"bit stream progress callback invoked {(r.Prog == null ? 0 : (long)r.Prog.Count)} times (>= 50)");
                Chk(r.Prog != null && r.Prog.RealMonotone, "bit stream progress bytes_read is monotonic");
                Chk(r.Prog != null && r.Prog.TotalOk, "bit stream progress bytes_total == total_size");
                // C: `num_read = num_read_before_chunk + (ptr - chunk_begin)` (ufbx.c:2156) keeps
                // walking into the zero padding the refill appends once the reader is drained
                // (ufbx.c:2078-2085, 128 bytes per pass), so the *last* callback may report
                // `bytes_read` past the end of the input. That is C's contract, not a port slip:
                // `tools/inflate_oracle.txt` answers `B 8 ... 82 0 1 5101 ...` for a 5000-byte
                // stream, and DiffB requires the port to produce the same value. What must hold is
                // that the overshoot is bounded by one padded chunk.
                Chk(r.Prog != null && r.Prog.Last <= (ulong)src.Length + 128UL,
                    $"bit stream progress bytes_read stays within one padded chunk of the end " +
                    $"({(r.Prog == null ? 0 : (long)r.Prog.Last)} <= {src.Length} + 128)");
            }

            // The 3-bytes-per-call reader cannot fill a chunk: C pads with zeros and reports
            // EOF on the second refill (ufbx.c:2078-2085).
            {
                BRec r = AddB("bit stream short-read driver", src, 0, 3, 0, 0, CorpusConst.NoCallback, 10000, null);
                Chk(r.StopError == -31, $"bit stream short-read reader ends with stop_error -31 (got {r.StopError} after {r.Steps} refills)");
            }

            // A reader that over-reads its contract (returning more bytes than requested) is
            // clamped to zero, exactly like C's `num_read > to_read` test against `size_t`
            // (ufbx.c:2069), and a negative return value is the same SIZE_MAX overshoot.
            {
                BRec r = AddB("bit stream overread driver", src, 0, CorpusConst.SizeMax, 0, 0,
                    CorpusConst.NoCallback, 40, null);
                _ = r;
                var s = new UfbxiBitStream();
                var input = new UfbxInflateInput
                {
                    TotalSize = src.Length,
                    Data = new byte[0],
                    DataSize = 0,
                    ReadFn = (user, data, size) => -1,
                    ReadUser = null,
                };
                s.BitStreamInit(input);
                ulong bits = s.Bits;
                int left = s.Left;
                int data = s.ChunkPtr;
                s.BitRefill(ref bits, ref left, ref data);
                Chk(s.InputLeft == src.Length, $"negative read_fn return is treated as an overshoot (input_left {s.InputLeft})");
                Chk(s.SeenEnd, "negative read_fn return still pads and flags the end");
            }
        }

        static void RunProgressCancel()
        {
            // Mostly-compressible payload: random *bytes* would be emitted as STORED blocks, which
            // `ufbxi_bit_copy_bytes()` (ufbx.c:2206-2250) slurps without ever calling
            // `ufbxi_bit_yield()`, so no progress callback would run. A small alphabet gives long
            // Huffman blocks and thus many yields.
            byte[] payload = new byte[40 * 1024];
            {
                uint seed = 7u;
                for (int i = 0; i < payload.Length; i++)
                {
                    seed = seed * 1664525u + 1013904223u;
                    payload[i] = (byte)('a' + (seed >> 23) % 6u);
                }
            }
            byte[] src = ZlibCompress(payload, CompressionLevel.Optimal);

            // C: `progress_cb` returning CANCEL makes `ufbxi_bit_yield()` set `stop_error = -28`
            // and redirect the stream to the zeroed local buffer (ufbx.c:2161-2171); the caller
            // returns it at ufbx.c:3158/3175/3241/3266. `cancel_at = limit - 1` because the oracle
            // cancels when `count > cancel_at`, i.e. on the (limit)-th call, matching
            // `seen >= limit` in the previous hand-written version.
            for (int limit = 1; limit <= 3; limit++)
            {
                DRec r = AddD($"progress cancel after {limit} callback(s)", src, false, false, 0, 1,
                    CorpusConst.SizeMax, 0, payload.Length, 64, limit - 1, null, -28, false);
                Chk(!r.Threw && r.Res == -28,
                    $"progress cancel after {limit} callback(s) -> -28 (got {r.Res}, {r.CbCount} calls)");
                Chk(r.CbCount == (ulong)limit,
                    $"progress cancel after {limit} callback(s): callback ran {r.CbCount} time(s)");
            }

            // Same stream, always continuing: must decode exactly (and call back often).
            {
                DRec r = AddD("progress continue whole stream", src, false, false, 0, 1,
                    CorpusConst.SizeMax, 0, payload.Length, 64, CorpusConst.AlwaysContinue,
                    payload, int.MinValue, false);
                Chk(!r.Threw && r.Res == payload.Length && SeqEqual(r.Dst, payload, payload.Length),
                    $"progress continue decodes the whole stream (res {r.Res})");
                Chk(r.CbCount > 100, $"progress callback invoked {r.CbCount} times with a 64-byte interval");
                Chk(r.CbTotalOk, "progress bytes_total == total_size + before + after");
                Chk(r.Prog != null && r.Prog.RealMonotone, "progress bytes_read increases monotonically");
            }
        }

        // ---------------------------------------------------------------- fuzz

        static byte[] Alphabet(int n, uint seed, int alphabet)
        {
            byte[] r = new byte[n];
            for (int i = 0; i < n; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                r[i] = (byte)('a' + (seed >> 23) % (uint)alphabet);
            }
            return r;
        }

        static void RunBitFlipFuzz()
        {
            // Three streams so the flips land in structurally different places: a dynamic-Huffman
            // zlib stream (code-length / tree / symbol paths, ufbx.c:2651-2661), the same stream
            // decoded with `no_checksum` (ufbx.c:3268: the Adler-32 gate is skipped, so far more
            // corruptions are accepted and their exact output bytes get compared), and a small
            // random payload that .NET emits as STORED blocks (the LEN/NLEN and copy paths,
            // ufbx.c:3190-3204 -> -4/-5/-6).
            byte[] huffPayload = Alphabet(1024, 8u, 6);
            byte[] huffStream = ZlibCompress(huffPayload, CompressionLevel.Optimal);
            byte[] storedPayload = Rand(160, 8u);
            byte[] storedStream = ZlibCompress(storedPayload, CompressionLevel.NoCompression);

            FRec huff = AddF("dynamic-huffman zlib stream", huffStream, huffPayload, false, false, 0);
            FRec huffNc = AddF("dynamic-huffman zlib stream, no checksum", huffStream, huffPayload,
                false, true, 0);
            FRec stored = AddF("stored-block zlib stream", storedStream, storedPayload, false, false, 0);

            Chk(huff.Flips.Count == huffStream.Length * 8,
                $"bit-flip fuzz {huff.Name}: {huff.Flips.Count} flips for {huffStream.Length} bytes");
            Chk(huffNc.Flips.Count == huff.Flips.Count,
                $"bit-flip fuzz {huffNc.Name}: {huffNc.Flips.Count} flips == {huff.Flips.Count}");
            Chk(stored.Flips.Count == storedStream.Length * 8,
                $"bit-flip fuzz {stored.Name}: {stored.Flips.Count} flips");

            // The port must never throw on corrupted input: C returns an error code for every one
            // of these (the per-flip equality is enforced against the oracle in DiffF).
            int threw = 0;
            string firstThrow = "";
            foreach (FRec r in new[] { huff, huffNc, stored })
            {
                for (int i = 0; i < r.Flips.Count; i++)
                {
                    if (r.Res[i] == int.MinValue)
                    {
                        threw++;
                        if (firstThrow == "") firstThrow = $"{r.Name} flip {r.Flips[i]}";
                    }
                }
            }
            Chk(threw == 0, $"bit-flip fuzz: {threw} runs threw instead of returning an error code (first: {firstThrow})");
        }
    }
}
