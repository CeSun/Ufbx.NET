// Corpus + C-oracle answer plumbing for tools/InflateCheck.
//
// The corpus is a plain text description of every inflate / bit-stream / adler32 / fuzz case the
// harness runs. It is written out verbatim by `dotnet run --project tools/InflateCheck -c Release
// -- inflatecorpus [path]` and consumed by tools/inflate_oracle.c, which answers with what the
// real C code (ufbx.c v0.23.1, single translation unit, so every `static ufbxi_*` internal is
// callable) produces. `inflatecheck` then replays the same records against the port and diffs the
// answers, so the assertions are made by ufbx.c itself rather than by this harness' guesses.
//
// Grammar (must stay in sync with tools/inflate_oracle.c):
//   S <sid> <hex>
//   A <sid> <off> <len>
//   D <idx> <sid> <nh> <nc> <fast> <mode> <step> <bufsz> <dst> <hint> <cancel_at>
//   B <idx> <sid> <data_size> <step> <bufsz> <hint> <cancel_at> <max_steps> <fields>
//   F <idx> <sid> <nh> <nc> <fast> <dst> <psid>
//
// Nothing here touches the ported code: it is test scaffolding only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace Ufbx.NET.Tests
{
    // Must match FNV_OFFSET / FNV_PRIME in tools/inflate_oracle.c.
    static class Fnv
    {
        public const ulong Offset = 14695981039346656037UL;
        public const ulong Prime = 1099511628211UL;

        public static ulong Hash(ulong h, byte[] data, int n)
        {
            for (int i = 0; i < n; i++)
            {
                h ^= (ulong)data[i];
                h *= Prime;
            }
            return h;
        }

        public static ulong HashString(ulong h, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                h ^= (ulong)(byte)s[i];
                h *= Prime;
            }
            return h;
        }

        public static string Hex(ulong v)
        {
            return v.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    // C: SIZE_MAX, spelled as a negative marker so the record fields stay integral.
    static class CorpusConst
    {
        public const long SizeMax = -1;
        public const int NoCallback = -2;
        public const int AlwaysContinue = -1;
        public const string SizeMaxText = "18446744073709551615";

        public static string Ulong(long v)
        {
            if (v == SizeMax) return SizeMaxText;
            return v.ToString(CultureInfo.InvariantCulture);
        }

        public static int ReadCap(long step)
        {
            // C: `n = min(size, r->step)` with `size` bounded by `buffer_size`/`input_left`, both
            // well below int.MaxValue in this corpus, so SIZE_MAX and int.MaxValue are equivalent.
            if (step >= (long)int.MaxValue) return int.MaxValue;
            if (step < 0) return int.MaxValue;
            return (int)step;
        }
    }

    sealed class ARec
    {
        public int Sid;
        public int Off;
        public int Len;
        public uint PortValue;
        // Independent expectation from the published Adler-32 definition, or null.
        public uint? Expect;
        public string ExpectSource;
        public string Key { get { return "A " + Sid + " " + Off + " " + Len; } }
    }

    sealed class DRec
    {
        public int Idx;
        public string Name;
        public int Stream;
        public bool Nh;
        public bool Nc;
        public int Fast;
        public int Mode;
        public long Step;
        public int BufSz;
        public int DstSize;
        public ulong Hint;
        public int CancelAt;

        // Harness-side intent, evaluated independently of the oracle answer.
        public byte[] Payload;      // exact output expected
        public int Err = int.MinValue;  // documented error code expected
        public bool AnyNegative;    // any error is fine, exact code not asserted

        // Captured port outcome.
        public bool Ran;
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

        public string Key { get { return "D " + Idx.ToString(CultureInfo.InvariantCulture); } }
        public string DumpLine(Corpus c)
        {
            return "D " + Idx + " " + Stream + " " + (Nh ? 1 : 0) + " " + (Nc ? 1 : 0) + " " + Fast +
                " " + Mode + " " + CorpusConst.Ulong(Step) + " " + BufSz + " " + DstSize + " " +
                Hint.ToString(CultureInfo.InvariantCulture) + " " + CancelAt;
        }
    }

    sealed class BRec
    {
        public int Idx;
        public string Name;
        public int Stream;
        public int DataSize;
        public long Step;
        public int BufSz;
        public ulong Hint;
        public int CancelAt;
        public long MaxSteps;
        public int[] Fields;    // null => consume 1 bit per step

        // Captured port outcome.
        public long Steps;
        public long EofStep = -1;
        public int StopError;
        public string Trace;
        public ProgRec Prog;
        public ulong CbCount;
        public bool CbMonotone = true;
        public bool CbTotalOk = true;
        public ulong CbLast = ulong.MaxValue;
        public string CbDigest;

        public string Key { get { return "B " + Idx.ToString(CultureInfo.InvariantCulture); } }
        public string DumpLine(Corpus c)
        {
            string fields = "-";
            if (Fields != null && Fields.Length > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < Fields.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Fields[i]);
                }
                fields = sb.ToString();
            }
            return "B " + Idx + " " + Stream + " " + DataSize + " " + CorpusConst.Ulong(Step) + " " +
                BufSz + " " + Hint.ToString(CultureInfo.InvariantCulture) + " " + CancelAt + " " +
                MaxSteps.ToString(CultureInfo.InvariantCulture) + " " + fields;
        }
    }

    sealed class FRec
    {
        public int Idx;
        public string Name;
        public int Stream;
        public bool Nh;
        public bool Nc;
        public int Fast;
        public int DstSize;
        // Stream holding the pristine payload this stream decodes to (-1 = unknown).
        public int PayloadStream = -1;
        public byte[] Payload;

        public readonly List<long> Flips = new List<long>();      // byte * 8 + bit
        public readonly List<int> Res = new List<int>();
        public readonly List<string> Digests = new List<string>();
        public readonly List<int> Flags = new List<int>();

        public string DumpLine(Corpus c)
        {
            return "F " + Idx + " " + Stream + " " + (Nh ? 1 : 0) + " " + (Nc ? 1 : 0) + " " + Fast +
                " " + DstSize + " " + PayloadStream;
        }
        public string FlipKey(int byteIx, int bit)
        {
            return "F " + Idx + " " + byteIx + " " + bit;
        }
    }

    sealed class Corpus
    {
        public readonly List<byte[]> Streams = new List<byte[]>();
        public readonly List<ARec> As = new List<ARec>();
        public readonly List<DRec> Ds = new List<DRec>();
        public readonly List<BRec> Bs = new List<BRec>();
        public readonly List<FRec> Fs = new List<FRec>();

        int m_numD, m_numB, m_numF;

        public int AddStream(byte[] data)
        {
            Streams.Add(data);
            return Streams.Count - 1;
        }

        public int AddA(ARec r) { As.Add(r); return As.Count - 1; }
        public int NextD() { return m_numD++; }
        public int NextB() { return m_numB++; }
        public int NextF() { return m_numF++; }

        static string HexOf(byte[] data)
        {
            // `-` is the grammar's spelling of an empty blob (whitespace separated tokens cannot
            // be empty); see tools/inflate_oracle.c.
            if (data.Length == 0) return "-";
            const string digits = "0123456789abcdef";
            var sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(digits[(data[i] >> 4) & 15]);
                sb.Append(digits[data[i] & 15]);
            }
            return sb.ToString();
        }

        // The exact text `tools/inflate_corpus.txt` must contain.
        public List<string> Lines()
        {
            var lines = new List<string>();
            lines.Add("# inflate differential corpus - generated by tools/InflateCheck (mode `inflatecorpus`)");
            lines.Add("# consumed by tools/inflate_oracle.c; do not edit by hand.");
            for (int i = 0; i < Streams.Count; i++)
            {
                lines.Add("S " + i + " " + HexOf(Streams[i]));
            }
            foreach (ARec r in As) lines.Add("A " + r.Sid + " " + r.Off + " " + r.Len);
            foreach (DRec r in Ds) lines.Add(r.DumpLine(this));
            foreach (BRec r in Bs) lines.Add(r.DumpLine(this));
            foreach (FRec r in Fs) lines.Add(r.DumpLine(this));
            return lines;
        }

        public ulong Digest()
        {
            ulong h = Fnv.Offset;
            foreach (string l in Lines())
            {
                h = Fnv.HashString(h, l);
                h = Fnv.HashString(h, "\n");
            }
            return h;
        }
    }

    // Answers produced by tools/inflate_oracle.exe over tools/inflate_corpus.txt.
    sealed class Oracle
    {
        readonly Dictionary<string, string[]> m_rows = new Dictionary<string, string[]>();
        public int NumRows;
        public int NumD, NumB, NumF, NumA;

        public bool Load(string path)
        {
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 2) continue;
                string key;
                switch (t[0])
                {
                    case "A": key = "A " + t[1] + " " + t[2] + " " + t[3]; NumA++; break;
                    case "D": key = "D " + t[1]; NumD++; break;
                    case "B": key = "B " + t[1]; NumB++; break;
                    case "F": key = "F " + t[1] + " " + t[2] + " " + t[3]; NumF++; break;
                    default: continue;  // V (verbose) and anything else
                }
                m_rows[key] = t;
                NumRows++;
            }
            return NumRows > 0;
        }

        public string[] Get(string key)
        {
            string[] row;
            m_rows.TryGetValue(key, out row);
            return row;
        }
    }

    // C: `progress_rec` in tools/inflate_oracle.c - the same accounting, same ASCII trace text.
    // `Monotone` deliberately mirrors C's field, which starts out comparing against
    // `(uint64_t)0 - 1` (tools/inflate_oracle.c `setup_progress`, mirroring
    // ufbx.c:2155-2158 usage) and therefore reports `false` for any callback that runs; the
    // harness additionally records `RealMonotone`, which is the invariant a reader would expect.
    sealed class ProgRec
    {
        public ulong Digest = Fnv.Offset;
        public ulong Count;
        public bool Monotone = true;
        public bool TotalOk = true;
        public ulong Last = ulong.MaxValue;
        public bool RealMonotone = true;
        public ulong ExpectTotal;
        public long CancelAt;

        public UfbxProgressResult Fn(object user, UfbxProgress p)
        {
            string line = p.BytesRead.ToString(CultureInfo.InvariantCulture) + " " +
                p.BytesTotal.ToString(CultureInfo.InvariantCulture) + "\n";
            Digest = Fnv.HashString(Digest, line);
            Count++;
            if (p.BytesRead < Last) Monotone = false;
            if (m_prev != 0 && p.BytesRead < m_prev) RealMonotone = false;
            m_prev = p.BytesRead;
            Last = p.BytesRead;
            if (p.BytesTotal != ExpectTotal) TotalOk = false;
            if (CancelAt >= 0 && (long)Count > CancelAt) return UfbxProgressResult.Cancel;
            return UfbxProgressResult.Continue;
        }

        ulong m_prev;
    }
}
