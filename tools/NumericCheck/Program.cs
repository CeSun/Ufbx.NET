// NumericCheck: bit-exactness harness for the ported numeric text->double layer
// (src/Ufbx.NET/Parse/Numeric.cs), driven by the C oracle `tools/numeric_oracle.c`
// (output `tools/numeric_oracle.txt`), built with the mandatory reference flags
//   zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx
//
// Run: dotnet run --project tools/NumericCheck -c Release -- [path/to/numeric_oracle.txt]
//
// The oracle is a single translation unit with ufbx.c, so it calls the real
// `static ufbxi_*` internals: ufbxi_parse_double, ufbxi_parse_inf_nan,
// ufbxi_bigint_mad / _div / _shift_left / _mul_pow5 / _extract_high,
// ufbxi_shift_right_round. Every record carries its own input, so this file replays
// instead of re-generating the corpus.
//
// What each record type proves:
//   G/O   the input table is byte-identical (FNV-64 digest) and the reference build really
//         does enable UFBXI_PARSE_DOUBLE_ALLOW_FAST_PATH (ufbx.c:1795-1805), which is what
//         Ascii.cs pins as ParseDoubleInitFlags.
//   W     corpus bytes only (fed to the digest).
//   D     ufbxi_parse_double with ALLOW_FAST_PATH, canonical order: 16319 strings covering
//         every numeric literal style FBX text writes (all widths, signs, leading zeros,
//         exponent forms, .5/5., inf/nan/garbage, >400-digit runs that saturate max_limbs,
//         subnormals, ULP ties), the NUL/token-edge families (a pushed '\0' inside max_length
//         as produced by the ASCII tokenizer at ufbx.c:9781, NUL in the middle, and strings
//         that stop exactly at the buffer edge with no NUL) plus a seeded fuzz set, diffed on
//         double bits, consumed length and the acceptance boundary.
//   N     same with flags = 0: forces the bigint path even where the fast path would answer,
//         so both routes have to agree with C independently. Empirically C's fast path and
//         C's bigint path agree bit-for-bit on every corpus string, which is only true if both
//         round correctly; a broken bigint (verified by mutation) makes exactly the fast-path
//         cases diverge here, so this pass is what stops the fast path from masking a bug.
//   B     same with AS_BINARY32 (ufbx.c:9792) -- the 24-bit / exp-bias-127 encoding.
//   C     same as D, but the C side scribbles the scratch arrays' stack region with garbage
//         before EVERY call. The port keeps one reused buffer instead, so this is the direct
//         test of the agreed "stale value is legitimately read" model (ufbx.c:587-591,1605).
//   R     same as D but iterated in reverse, i.e. with a different predecessor in the reused
//         buffer. Any D vs R movement in C must be reproduced by the port the same way.
//   M/S/P/V/H/T/I  the bigint and rounding helpers driven directly, including the states
//         ufbxi_parse_double only reaches on rare inputs (limb carry growth, the qhat
//         correction and restore branches of _div, shift amounts that span several limbs).
//         The whole 24-limb buffer is compared, not just the logical value, so a stale write
//         or an off-by-one length is visible -- that is what settles the ufbx.c:1385/1397
//         ("b = *bigint" then "b.limbs[b.length++]") aliasing question.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace NumericCheck
{
    static class Program
    {
        // C: UFBXI_PARSE_DOUBLE_ALLOW_FAST_PATH / AS_BINARY32 (ufbx.c:1531-1534)
        const uint FlagsFastPath = 1u;
        const uint FlagsNone = 0u;
        const uint FlagsBinary32 = 2u;

        static int s_checks, s_passed, s_failed;
        static readonly List<string> s_failures = new List<string>();
        static readonly Dictionary<string, int> s_byTag = new Dictionary<string, int>();
        static readonly Dictionary<string, int> s_failByTag = new Dictionary<string, int>();
        static int s_tagCount;

        static void Check(string tag, string name, bool ok, string detail = null)
        {
            s_checks++;
            if (ok) {
                s_passed++;
            } else {
                s_failed++;
                if (!s_failByTag.ContainsKey(tag)) s_failByTag[tag] = 0;
                s_failByTag[tag] = s_failByTag[tag] + 1;
                if (s_failures.Count < 40) s_failures.Add(name + (detail == null ? "" : ": " + detail));
            }
        }

        static void CountTag(string tag)
        {
            if (!s_byTag.ContainsKey(tag)) { s_byTag[tag] = 0; s_tagCount++; }
            s_byTag[tag] = s_byTag[tag] + 1;
        }

        // -- hex helpers

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static byte[] ParseHexBytes(string s)
        {
            byte[] b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) {
                b[i] = (byte)((HexVal(s[2 * i]) << 4) | HexVal(s[2 * i + 1]));
            }
            return b;
        }

        static uint[] ParseLimbs(string hex, int cap)
        {
            uint[] l = new uint[cap];
            for (int i = 0; i < cap; i++) {
                uint v = 0;
                for (int j = 0; j < 8; j++) v = (v << 4) | (uint)HexVal(hex[i * 8 + j]);
                l[i] = v;
            }
            return l;
        }

        static string LimbsHex(uint[] l)
        {
            StringBuilder sb = new StringBuilder(l.Length * 8);
            for (int i = 0; i < l.Length; i++) sb.Append(l[i].ToString("x8", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static string Hex64(ulong v) { return v.ToString("x16", CultureInfo.InvariantCulture); }

        static ulong ParseHex64(string s)
        {
            return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        static int ParseI(string s)
        {
            return int.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        // -- corpus state

        static readonly List<byte[]> Corpus = new List<byte[]>();
        static int s_numPrime;
        static int s_expCases;
        static string s_expDigest;
        static uint s_expFlags;
        static bool s_haveFlags;
        static int s_numZ;

        static ulong FnvStep(ulong digest, ulong v)
        {
            // C: g_digest ^= (uint64_t)n; g_digest *= 1099511628211ull; -- only the low byte
            // of a small n can change, which is exactly what the XOR does.
            digest ^= v;
            return unchecked(digest * 1099511628211ul);
        }

        static void PrimeScratch()
        {
            // C: prime_scratch() (tools/numeric_oracle.c) -- parse the primer prefix so both
            // sides enter a pass with the same residue in the reused limb buffers.
            for (int i = 0; i < s_numPrime && i < Corpus.Count; i++) {
                byte[] s = Corpus[i];
                int pe;
                UfbxiNumeric.ParseDouble(new ReadOnlySpan<byte>(s, 0, s.Length), out pe, FlagsFastPath);
            }
        }

        static uint FlagsForTag(string tag)
        {
            switch (tag) {
                case "N": return FlagsNone;
                case "B": return FlagsBinary32;
                default: return FlagsFastPath;
            }
        }

        static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : FindOracle();
            if (path == null || !File.Exists(path)) {
                Console.WriteLine("numericcheck: tools/numeric_oracle.txt not found");
                return 1;
            }

            string prevPass = null;
            ulong digest = 1469598103934665603ul;
            int wSeen = 0;

            foreach (string raw in File.ReadLines(path)) {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] t = line.Split(' ');
                string tag = t[0];
                CountTag(tag);

                try {
                    switch (tag) {
                        case "G": {
                            s_expCases = ParseI(t[1]);
                            s_numPrime = ParseI(t[2]);
                            s_expDigest = t[3];
                            break;
                        }
                        case "O": {
                            s_expFlags = (uint)ParseI(t[1]);
                            s_haveFlags = true;
                            break;
                        }
                        case "W": {
                            byte[] b = ParseHexBytes(t[2]);
                            Corpus.Add(b);
                            digest = FnvStep(digest, (ulong)b.Length);
                            for (int i = 0; i < b.Length; i++) digest = FnvStep(digest, b[i]);
                            wSeen++;
                            break;
                        }
                        case "Z": {
                            s_numZ++;
                            break;
                        }
                        case "D":
                        case "N":
                        case "B":
                        case "C":
                        case "R": {
                            if (tag != prevPass) { PrimeScratch(); prevPass = tag; }
                            ReplayDouble(tag, t);
                            break;
                        }
                        case "M": ReplayMad(t); break;
                        case "S": ReplayShift(t); break;
                        case "P": ReplayMulPow5(t); break;
                        case "V": ReplayDiv(t); break;
                        case "H": ReplayExtract(t); break;
                        case "T": ReplayRound(t); break;
                        case "I": ReplayInfNan(t); break;
                        default: {
                            Check("X", "unknown record", false, line);
                            break;
                        }
                    }
                } catch (Exception e) {
                    Check(tag, "record " + (t.Length > 1 ? t[1] : "?") + " threw", false,
                        e.GetType().Name + " " + e.Message);
                }
            }

            Check("G", "oracle reports no errors", s_numZ == 0, s_numZ + " Z records");
            Check("G", "corpus case count", wSeen == s_expCases, "got " + wSeen + " want " + s_expCases);
            Check("G", "corpus digest", digest.ToString("x16", CultureInfo.InvariantCulture) == s_expDigest,
                "got " + digest.ToString("x16", CultureInfo.InvariantCulture) + " want " + s_expDigest);
            // C: ufbxi_parse_double_init_flags() (ufbx.c:1795-1805) -- the loader only uses the
            // fast path when double evaluation and round-to-nearest hold; Ascii.cs pins it as a
            // constant, so the reference build has to actually answer 1.
            Check("O", "reference fast-path flag", s_haveFlags && s_expFlags == FlagsFastPath,
                "got " + s_expFlags + " want " + FlagsFastPath);

            Console.WriteLine("numericcheck: " + s_checks + " checks, " + s_passed + " passed, " + s_failed + " failed");
            foreach (string f in s_failures) Console.WriteLine("  FAIL " + f);
            StringBuilder sb = new StringBuilder();
            string[] order = { "D", "N", "B", "C", "R", "M", "S", "P", "V", "H", "T", "I" };
            foreach (string k in order) {
                int c;
                if (s_byTag.TryGetValue(k, out c)) sb.Append(' ').Append(k).Append('=').Append(c);
            }
            Console.WriteLine("numericcheck: records" + sb.ToString());
            if (s_failed != 0) {
                StringBuilder fb = new StringBuilder();
                foreach (string k in order) {
                    int c;
                    if (s_failByTag.TryGetValue(k, out c)) fb.Append(' ').Append(k).Append('=').Append(c);
                }
                Console.WriteLine("numericcheck: failed-checks" + fb.ToString());
            }
            return s_failed == 0 ? 0 : 1;
        }

        // -- replay of one ufbxi_parse_double record

        static void ReplayDouble(string tag, string[] t)
        {
            int idx = ParseI(t[1]);
            string expBits = t[2];
            int expConsumed = ParseI(t[3]);
            string expOk = t[4];

            byte[] s = Corpus[idx];
            int pEnd;
            double got = UfbxiNumeric.ParseDouble(new ReadOnlySpan<byte>(s, 0, s.Length), out pEnd, FlagsForTag(tag));
            string gotBits = Hex64(unchecked((ulong)BitConverter.DoubleToInt64Bits(got)));

            Check(tag, tag + " " + idx + " bits", gotBits == expBits,
                Ctx(s) + " got " + gotBits + " want " + expBits);
            Check(tag, tag + " " + idx + " consumed", pEnd == expConsumed,
                Ctx(s) + " got " + pEnd + " want " + expConsumed);
            bool gotOk = pEnd == s.Length;
            Check(tag, tag + " " + idx + " ok", (gotOk ? "1" : "0") == expOk,
                Ctx(s) + " got " + (gotOk ? "1" : "0") + " want " + expOk);
        }

        static string Ctx(byte[] s)
        {
            if (s.Length > 64) return "(len " + s.Length + " " + HexOf(s, 0, 24) + "...)";
            return "(len " + s.Length + " " + HexOf(s, 0, s.Length) + ")";
        }

        static string HexOf(byte[] b, int off, int len)
        {
            StringBuilder sb = new StringBuilder(len * 2);
            for (int i = 0; i < len; i++) sb.Append(b[off + i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // -- direct helper probes

        static void ReplayMad(string[] t)
        {
            int idx = ParseI(t[1]);
            int cap = ParseI(t[2]);
            int inLen = ParseI(t[3]);
            UfbxiNumeric.Bigint b = MakeBigint(t[4], cap, inLen);
            ulong mult = ParseHex64(t[5]);
            ulong add = ParseHex64(t[6]);
            string expLen = t[7];
            string expHex = t[8];

            // C: ufbxi_bigint_mad (ufbx.c:1382-1402) copies the struct (`b = *bigint`, 1385),
            // grows b.length in the append loop (1397) and writes it back (1401).
            UfbxiNumeric.BigintMad(ref b, mult, add);

            Check("M", "M " + idx + " length", b.Length.ToString() == expLen, "got " + b.Length + " want " + expLen);
            Check("M", "M " + idx + " limbs", LimbsHex(b.Limbs) == expHex, "got " + LimbsHex(b.Limbs) + " want " + expHex);
        }

        static void ReplayShift(string[] t)
        {
            int idx = ParseI(t[1]);
            int cap = ParseI(t[2]);
            int inLen = ParseI(t[3]);
            UfbxiNumeric.Bigint b = MakeBigint(t[4], cap, inLen);
            uint amount = (uint)ParseI(t[5]);
            string expLen = t[6];
            string expHex = t[7];

            // C: ufbxi_bigint_shift_left (ufbx.c:1460-1488): bigint->length is updated in
            // place (1466) while every index below keeps using the copied b.length (1467,1468,
            // 1480) -- and limbs[1]/limbs[2] are read even when length is 1 (1470-1471).
            UfbxiNumeric.BigintShiftLeft(ref b, amount);

            Check("S", "S " + idx + " length", b.Length.ToString() == expLen, "got " + b.Length + " want " + expLen);
            Check("S", "S " + idx + " limbs", LimbsHex(b.Limbs) == expHex, "got " + LimbsHex(b.Limbs) + " want " + expHex);
        }

        static void ReplayMulPow5(string[] t)
        {
            int idx = ParseI(t[1]);
            int cap = ParseI(t[2]);
            int inLen = ParseI(t[3]);
            UfbxiNumeric.Bigint b = MakeBigint(t[4], cap, inLen);
            uint power = (uint)ParseI(t[5]);
            string expLen = t[6];
            string expHex = t[7];

            // C: ufbxi_bigint_mul_pow5 (ufbx.c:1452-1458)
            UfbxiNumeric.BigintMulPow5(ref b, power);

            Check("P", "P " + idx + " length", b.Length.ToString() == expLen, "got " + b.Length + " want " + expLen);
            Check("P", "P " + idx + " limbs", LimbsHex(b.Limbs) == expHex, "got " + LimbsHex(b.Limbs) + " want " + expHex);
        }

        static void ReplayDiv(string[] t)
        {
            int idx = ParseI(t[1]);
            int cap = ParseI(t[2]);
            int n = ParseI(t[3]);
            int m = ParseI(t[4]);
            UfbxiNumeric.Bigint u = MakeBigint(t[5], cap, n + m);
            UfbxiNumeric.Bigint v = MakeBigint(t[6], cap, n);
            UfbxiNumeric.Bigint q = MakeBigint(t[7], cap, 0);
            string expTail = t[8];
            string expQLen = t[9];
            string expQHex = t[10];
            string expUHex = t[11];

            // C: ufbxi_bigint_div (ufbx.c:1404-1450)
            bool tail = UfbxiNumeric.BigintDiv(ref q, ref u, ref v);

            Check("V", "V " + idx + " tail", (tail ? "1" : "0") == expTail, "got " + (tail ? "1" : "0") + " want " + expTail);
            Check("V", "V " + idx + " qlen", q.Length.ToString() == expQLen, "got " + q.Length + " want " + expQLen);
            Check("V", "V " + idx + " quotient", LimbsHex(q.Limbs) == expQHex, "got " + LimbsHex(q.Limbs) + " want " + expQHex);
            Check("V", "V " + idx + " dividend-remainder", LimbsHex(u.Limbs) == expUHex, "got " + LimbsHex(u.Limbs) + " want " + expUHex);
        }

        static void ReplayExtract(string[] t)
        {
            int idx = ParseI(t[1]);
            int cap = ParseI(t[2]);
            int inLen = ParseI(t[3]);
            UfbxiNumeric.Bigint b = MakeBigint(t[4], cap, inLen);
            int expIn = ParseI(t[5]);
            bool tailIn = t[6] == "1";
            string expBits = t[7];
            string expOut = t[8];
            string expTail = t[9];

            int exponent = expIn;
            bool tail = tailIn;
            int lenBefore = b.Length;
            // C: ufbxi_bigint_extract_high takes the bigint BY VALUE (ufbx.c:1494), so it can
            // never change the caller's length; asserted here to pin the aliasing model.
            ulong got = UfbxiNumeric.BigintExtractHigh(ref b, ref exponent, ref tail);

            Check("H", "H " + idx + " bits", Hex64(got) == expBits, "got " + Hex64(got) + " want " + expBits);
            Check("H", "H " + idx + " exponent", exponent.ToString() == expOut, "got " + exponent + " want " + expOut);
            Check("H", "H " + idx + " tail", (tail ? "1" : "0") == expTail, "got " + (tail ? "1" : "0") + " want " + expTail);
            Check("H", "H " + idx + " length unchanged", b.Length == lenBefore, "got " + b.Length + " want " + lenBefore);
        }

        static void ReplayRound(string[] t)
        {
            int idx = ParseI(t[1]);
            ulong value = ParseHex64(t[2]);
            uint shift = (uint)ParseI(t[3]);
            bool tail = t[4] == "1";
            string expOut = t[5];

            // C: ufbxi_shift_right_round (ufbx.c:1516-1529) -- the round-half-to-even site.
            ulong got = UfbxiNumeric.ShiftRightRound(value, shift, tail);

            Check("T", "T " + idx + " result", Hex64(got) == expOut,
                "value=" + Hex64(value) + " shift=" + shift + " tail=" + (tail ? 1 : 0) +
                " got " + Hex64(got) + " want " + expOut);
        }

        static void ReplayInfNan(string[] t)
        {
            int idx = ParseI(t[1]);
            byte[] s = ParseHexBytes(t[2]);
            bool expOk = t[3] == "1";
            string expBits = t[4];
            int expEnd = ParseI(t[5]);

            double result;
            int pEnd;
            // C: ufbxi_parse_inf_nan (ufbx.c:1545-1599)
            bool ok = UfbxiNumeric.ParseInfNan(new ReadOnlySpan<byte>(s, 0, s.Length), out result, out pEnd);

            Check("I", "I " + idx + " ok", ok == expOk, "hex " + HexOf(s, 0, s.Length) + " got " + (ok ? 1 : 0) + " want " + (expOk ? 1 : 0));
            if (expOk) {
                // Only observable on success: C leaves both out-params untouched when it
                // returns false, so the port's 0/0.0 there is never read (ufbx.c:1667-1673).
                string bits = Hex64(unchecked((ulong)BitConverter.DoubleToInt64Bits(result)));
                Check("I", "I " + idx + " bits", bits == expBits,
                    "hex " + HexOf(s, 0, s.Length) + " got " + bits + " want " + expBits);
                Check("I", "I " + idx + " end", pEnd == expEnd,
                    "hex " + HexOf(s, 0, s.Length) + " got " + pEnd + " want " + expEnd);
            }
        }

        static UfbxiNumeric.Bigint MakeBigint(string hex, int cap, int len)
        {
            UfbxiNumeric.Bigint b = UfbxiNumeric.Bigint.Make(ParseLimbs(hex, cap));
            b.Length = len;
            return b;
        }

        static string FindOracle()
        {
            string[] cands = {
                "tools/numeric_oracle.txt",
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "numeric_oracle.txt"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "numeric_oracle.txt"),
            };
            foreach (string c in cands) {
                if (File.Exists(c)) return c;
            }
            return null;
        }
    }
}
