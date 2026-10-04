// UtilCheck: bit-exactness harness for the ported Util layer, driven by the C oracle
// `tools/util_oracle.c` (output `tools/util_oracle.txt`).
//
// The oracle is compiled as a single translation unit with ufbx.c, so it calls the real
// `static ufbxi_*` internals: ufbxi_hash_string, ufbxi_hash_string_check_ascii,
// ufbxi_utf8_valid_length, ufbxi_get_concat_key, ufbxi_push_string_place over all five
// ufbx_unicode_error_handling modes, and ufbxi_push_sanitized_string.
//
// Run: dotnet run --project tools/UtilCheck -c Release -- [path/to/util_oracle.txt]
//
// What each record type proves:
//   H/A/V/C  the pure functions (hash, ascii detection, utf8 prefix, concat key) agree on
//            a 192-case corpus of random byte strings, including embedded NULs, overlong
//            and surrogate sequences and truncated tails.
//   P        the pool hands out the same bytes and the same (possibly sanitized-shortened)
//            length for every string, and the *rank* of the interned pointer -- the number
//            of distinct-or-duplicate pooled pointers with a lower address -- matches, so
//            C's address-ordered comparisons (pointer-keyed maps, `ufbxi_str_less` ties)
//            are reproducible from UfbxiPtrIdTable. Rank -2 marks `ufbxi_empty_char`, whose
//            order against arena memory is unspecified in C and not reproduced (see
//            PORTING_NOTES.md "C 指针序 ≡ 分配序").
//   S        the DOM value path: raw and sanitized strings, their lengths, whether
//            utf8_data aliases raw_data (equal rank) or is NULL, and the bytes.
//   FE       ufbx_format_error() (ufbx.c:30606-30642) through the public façade
//            UfbxErrorApi, over hand-built errors: the three NULL guards of the signature,
//            C's NULL-vs-empty `description`, the `0 < info_length < 256` branch at both
//            boundaries, a `%.*s` precision that binds before the end of the array, the
//            `%*u` frame lines and the `min(stack_size, 8)` clamp -- plus a dst_size ladder
//            around each branch's exact length. Both sides pre-fill the output buffer with
//            0xCD, so the return value, the NUL position and untouched bytes all compare.
//            Nothing else can cover this function: ufbx never calls it internally, and the
//            reference build compiles the error stack out, so real errors arrive with
//            stack_size == 0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ufbx.NET;

namespace UtilCheck
{
    static class Program
    {
        // Must match tools/util_oracle.c
        const int NumRand = 192;
        const int NumExtra = 28;
        const int NumCases = NumRand + NumExtra;
        const int MaxLen = 40;
        const int NumModes = 5;

        // The oracle's hand-picked UTF-8 boundary corpus, as hex so both sides feed
        // exactly the same bytes.
        static readonly string[] ExtraHex = {
            "c3a9",
            "c280",
            "c1bf",
            "c080",
            "c2",
            "e282ac",
            "e0a080",
            "e09fff",
            "eda080",
            "ed9fbf",
            "ee8080",
            "efbfbd",
            "e282",
            "f09f9880",
            "f08f8080",
            "f48fbfbf",
            "f4908080",
            "f880",
            "fc80808080",
            "fe",
            "ff",
            "00",
            "410042",
            "616263c3616263",
            "c3c3c3c3c3c3c3c3",
            "e282ace282ace282ac",
            "f09f9880f09f9880f09f9880",
            "ffffffffffffffffffffffffffffffff",
        };

        static readonly UfbxUnicodeErrorHandling[] Modes = {
            UfbxUnicodeErrorHandling.ReplacementCharacter,
            UfbxUnicodeErrorHandling.Underscore,
            UfbxUnicodeErrorHandling.QuestionMark,
            UfbxUnicodeErrorHandling.Remove,
            UfbxUnicodeErrorHandling.UnsafeIgnore,
        };

        static int s_checks, s_passed, s_failed;
        static readonly List<string> s_failures = new List<string>();

        static void Check(string name, bool ok, string detail = null)
        {
            s_checks++;
            if (ok) {
                s_passed++;
            } else {
                s_failed++;
                if (s_failures.Count < 60) s_failures.Add(name + (detail == null ? "" : ": " + detail));
            }
        }

        // -- Corpus (identical LCG to the oracle)

        static readonly byte[][] GBytes = new byte[NumCases][];
        static readonly string[] GStr = new string[NumCases];
        static readonly int[] GLen = new int[NumCases];

        static void GenCorpus()
        {
            uint st = 12345u;
            for (int i = 0; i < NumCases; i++) {
                byte[] b;
                if (i < NumRand) {
                    int len = i % MaxLen;
                    b = new byte[len];
                    for (int j = 0; j < len; j++) {
                        st = unchecked(st * 1103515245u + 12345u);
                        uint r = (st >> 16) & 0xffu;
                        uint sel = st & 3u;
                        if (sel == 0u) {
                            r = (uint)('a') + (r % 26u);
                        } else if (sel == 1u) {
                            r = 0x20u + (r % 0x5fu);
                        } else if (sel == 2u) {
                            r = 0xc0u + (r % 0x40u);
                        } else {
                            r &= 0xffu;
                        }
                        b[j] = (byte)r;
                    }
                } else {
                    b = ParseHex(ExtraHex[i - NumRand]);
                }
                GBytes[i] = b;
                GLen[i] = b.Length;
                GStr[i] = UfbxiRawStr.FromBytes(b, 0, b.Length);
            }
        }

        static byte[] ParseHex(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) {
                b[i] = (byte)((HexNibble(hex[i * 2 + 0]) << 4) | HexNibble(hex[i * 2 + 1]));
            }
            return b;
        }

        static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            return c - 'a' + 10;
        }

        static string Hex(byte[] data, int length)
        {
            char[] chars = new char[length * 2];
            const string Digits = "0123456789abcdef";
            for (int i = 0; i < length; i++) {
                chars[i * 2 + 0] = Digits[data[i] >> 4];
                chars[i * 2 + 1] = Digits[data[i] & 0xf];
            }
            return new string(chars);
        }

        static string HexOf(string rawBytes, int length)
        {
            if (rawBytes == null) return "";
            byte[] data = UfbxiRawStr.ToBytes(rawBytes, 0, length);
            return Hex(data, length);
        }

        // -- Oracle records

        class PRec { public int Rank, Length; public string Hex; }
        class SRec { public int RawRank, RawLength, Utf8Rank, Utf8Length; public string RawHex, Utf8Hex; }
        class KRec { public int Index, Matched, Rank, Length; }

        // One `FE` record: the full input of a ufbx_format_error() call plus C's expected
        // return value and output buffer. Self-describing on purpose -- the probe table would
        // otherwise live twice, once in C and once here, and only the C side is authoritative.
        class FeFrame { public uint Line; public string Fn, Desc; }
        class FeRec {
            public int Ix, DstNull, DstSize, ErrNull, DescNull;
            public string Desc, Info;
            public int InfoLen;
            public uint StackSize;
            public FeFrame[] Frames;
            public int Ret;
            public string OutHex;
        }

        static readonly List<FeRec> ExpFE = new List<FeRec>();

        static readonly uint[] ExpH = new uint[NumCases];
        static readonly uint[] ExpA = new uint[NumCases];
        static readonly bool[] ExpANonAscii = new bool[NumCases];
        static readonly int[] ExpV = new int[NumCases];
        static readonly uint[] ExpC = new uint[NumCases];
        static readonly bool[] ExpHasAVC = new bool[NumCases];

        static readonly PRec[,] ExpP = new PRec[NumModes, NumCases];
        static readonly SRec[] ExpS = new SRec[NumCases];
        static readonly List<KRec> ExpK = new List<KRec>();
        static readonly List<string> ExpL = new List<string>();
        static int s_expStringCount = -1;
        static string s_expStringDigest;

        static int s_numE, s_oracleLines;

        static uint ParseHash(string s)
        {
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        static int ParseInt(string s)
        {
            return int.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        // The oracle's `-` is an empty byte buffer (see emit_hex/fe_hex_or_dash); a string
        // field is one char per byte, so an embedded NUL stays data rather than a terminator.
        static string HexOrDash(string hex)
        {
            if (hex == "-") return "";
            byte[] b = ParseHex(hex);
            return UfbxiRawStr.FromBytes(b, 0, b.Length);
        }

        // C: the `FE` grammar of tools/util_oracle.c --
        //   FE <ix> <dst_null> <dst_size> <err_null> <desc_null> <desc_hex> <info_len>
        //      <info_hex> <stack_size> (<line> <func_hex> <fdesc_hex>){min(stack_size,8)}
        //      <ret> <out_hex>
        static FeRec ParseFe(string[] t)
        {
            FeRec r = new FeRec();
            r.Ix = ParseInt(t[1]);
            r.DstNull = ParseInt(t[2]);
            r.DstSize = ParseInt(t[3]);
            r.ErrNull = ParseInt(t[4]);
            r.DescNull = ParseInt(t[5]);
            r.Desc = HexOrDash(t[6]);
            r.InfoLen = ParseInt(t[7]);
            r.Info = HexOrDash(t[8]);
            r.StackSize = unchecked((uint)ulong.Parse(t[9], CultureInfo.InvariantCulture));
            int depth = UfbxConstants.ErrorStackMaxDepth;
            int frames = r.ErrNull != 0 ? 0 : (r.StackSize < (uint)depth ? (int)r.StackSize : depth);
            r.Frames = new FeFrame[frames];
            for (int i = 0; i < frames; i++) {
                r.Frames[i] = new FeFrame {
                    Line = unchecked((uint)ulong.Parse(t[10 + i * 3], CultureInfo.InvariantCulture)),
                    Fn = HexOrDash(t[10 + i * 3 + 1]),
                    Desc = HexOrDash(t[10 + i * 3 + 2]),
                };
            }
            r.Ret = ParseInt(t[10 + frames * 3]);
            r.OutHex = t[11 + frames * 3];
            return r;
        }

        static bool LoadOracle(string path)
        {
            foreach (string line in File.ReadAllLines(path)) {
                if (line.Length == 0) continue;
                string[] t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                s_oracleLines++;
                switch (t[0]) {
                    case "H": {
                        int i = ParseInt(t[1]);
                        ExpH[i] = ParseHash(t[2]);
                        break;
                    }
                    case "A": {
                        int i = ParseInt(t[1]);
                        ExpA[i] = ParseHash(t[2]);
                        ExpANonAscii[i] = t[3] != "0";
                        ExpHasAVC[i] = true;
                        break;
                    }
                    case "V": {
                        int i = ParseInt(t[1]);
                        if (!ExpHasAVC[i]) { s_oracleLines = -1; return false; }
                        ExpV[i] = ParseInt(t[2]);
                        break;
                    }
                    case "C": {
                        int i = ParseInt(t[1]);
                        if (!ExpHasAVC[i]) { s_oracleLines = -1; return false; }
                        ExpC[i] = ParseHash(t[2]);
                        break;
                    }
                    case "P": {
                        int m = ParseInt(t[1]), i = ParseInt(t[2]);
                        PRec r = new PRec();
                        r.Rank = ParseInt(t[3]);
                        r.Length = ParseInt(t[4]);
                        r.Hex = t.Length > 5 ? t[5] : "";
                        ExpP[m, i] = r;
                        break;
                    }
                    case "S": {
                        int i = ParseInt(t[1]);
                        SRec r = new SRec();
                        r.RawRank = ParseInt(t[2]);
                        r.RawLength = ParseInt(t[3]);
                        r.Utf8Rank = ParseInt(t[4]);
                        r.Utf8Length = ParseInt(t[5]);
                        r.RawHex = t[6];
                        r.Utf8Hex = t.Length > 7 ? t[7] : "";
                        ExpS[i] = r;
                        break;
                    }
                    case "F":
                        s_expStringCount = ParseInt(t[1]);
                        s_expStringDigest = t[2];
                        break;
                    case "L": {
                        int i = ParseInt(t[1]);
                        while (ExpL.Count <= i) ExpL.Add(null);
                        ExpL[i] = t.Length > 3 ? t[3] : "";
                        break;
                    }
                    case "K": {
                        KRec r = new KRec();
                        r.Index = ParseInt(t[1]);
                        r.Matched = ParseInt(t[2]);
                        r.Rank = ParseInt(t[3]);
                        r.Length = ParseInt(t[4]);
                        ExpK.Add(r);
                        break;
                    }
                    case "FE":
                        ExpFE.Add(ParseFe(t));
                        break;
                    case "E":
                    case "E2":
                    case "E3":
                    case "E4":
                        s_numE++;
                        break;
                    default:
                        return false;
                }
            }
            return s_oracleLines > 0;
        }

        // C: the `FE` pass -- ufbx_format_error() (ufbx.c:30606-30642) over hand-built
        // errors, through the public façade UfbxErrorApi so the forwarding layer itself is
        // covered too.
        // Both sides pre-fill the output buffer with 0xCD (C: FE_FILL) so untouched bytes, the
        // NUL terminator and any write past dst_size are all observable.
        static void CheckFormatErrors()
        {
            foreach (FeRec r in ExpFE) {
                string tag = "FE " + r.Ix;

                UfbxError err = null;
                if (r.ErrNull == 0) {
                    err = new UfbxError();
                    // C's NULL `description.data` is `null` here; C's `ufbxi_empty_char` is
                    // "" -- the two print differently, so the distinction has to survive.
                    err.Description = r.DescNull != 0 ? null : r.Desc;
                    err.InfoLength = r.InfoLen;
                    err.Info = r.Info;
                    err.StackSize = r.StackSize;
                    for (int i = 0; i < r.Frames.Length; i++) {
                        UfbxErrorFrame frame = default(UfbxErrorFrame);
                        frame.SourceLine = r.Frames[i].Line;
                        frame.Function = r.Frames[i].Fn;
                        frame.Description = r.Frames[i].Desc;
                        err.Stack[i] = frame;
                    }
                }

                int len = r.DstSize < 1 ? 1 : r.DstSize;
                byte[] dst = new byte[len];
                for (int i = 0; i < len; i++) dst[i] = 0xcd;

                int ret;
                try {
                    ret = UfbxErrorApi.FormatError(r.DstNull != 0 ? null : dst, r.DstSize, err);
                } catch (Exception e) {
                    Check(tag, false, e.GetType().Name + " " + e.Message);
                    continue;
                }
                Check(tag + " ret", ret == r.Ret, "got " + ret + " want " + r.Ret);
                string hex = r.DstNull != 0 ? "-" : Hex(dst, len);
                if (hex != r.OutHex) {
                    Check(tag + " buf", false, "got " + hex + " want " + r.OutHex);
                } else {
                    Check(tag + " buf", true);
                }
            }
        }

        // -- Pointer ranks

        // C: GRP_* -- one rank domain per pool, see the comment in tools/util_oracle.c.
        const int GrpP0 = 0;
        const int GrpSanitized = 5;
        const int GrpNames = 6;
        const int NumGrps = 7;

        // C: g_ptrs[] / g_grps[] / rank_of() -- rank = number of same-pool pointers with a lower
        // address, which in the port is the number with a lower id (ids are assigned in
        // interning order == C chunk offset order).
        static readonly List<ulong>[] RankedIds = new List<ulong>[NumGrps];

        static void AddRanked(string s, int grp)
        {
            if (s == null) return;
            if (UfbxiPtrIdTable.IsStatic(s)) return;
            ulong id = UfbxiPtrIdTable.IdOf(s);
            List<ulong> list = RankedIds[grp];
            if (list == null) list = RankedIds[grp] = new List<ulong>();
            list.Add(id);
        }

        static int RankOf(string s, int grp)
        {
            if (s == null) return -1;
            if (UfbxiPtrIdTable.IsStatic(s)) return -2;
            ulong id = UfbxiPtrIdTable.IdOf(s);
            int rank = 0;
            List<ulong> list = RankedIds[grp];
            if (list != null) {
                for (int i = 0; i < list.Count; i++) {
                    if (list[i] < id) rank++;
                }
            }
            return rank;
        }

        // C: setup_pool()
        static UfbxiStringPool NewPool(UfbxError error, UfbxiPushBuf warnResult, UfbxUnicodeErrorHandling handling)
        {
            UfbxiWarnings ws = new UfbxiWarnings();
            ws.Error = error;
            ws.Result = warnResult;
            UfbxiPushBuf buf = new UfbxiPushBuf();
            // C: ufbxi_map_init(&pool->map, ator, &ufbxi_map_cmp_string, NULL), initial_size = 64
            return new UfbxiStringPool(error, ws, buf, 64, handling);
        }

        static int Main(string[] args)
        {
            string oraclePath = args.Length > 0 ? args[0] : FindOracle();
            if (oraclePath == null) {
                Console.WriteLine("utilcheck: tools/util_oracle.txt not found");
                return 1;
            }
            if (!LoadOracle(oraclePath)) {
                Console.WriteLine("utilcheck: unreadable oracle " + oraclePath);
                return 1;
            }
            Check("oracle reports no errors", s_numE == 0, s_numE + " E records");

            GenCorpus();

            UfbxError error = new UfbxError();
            // C: g_warn_result -- one warning description buffer shared by every pool, never reset
            UfbxiPushBuf warnResult = new UfbxiPushBuf();

            CheckPureFunctions();
            CheckStrHelpers();
            CheckFormatErrors();

            // C: the `P` pass -- mode 0 interns with `raw`, the rest sanitize.
            string[,] pushRef = new string[NumModes, NumCases];
            int[,] pushLen = new int[NumModes, NumCases];
            for (int m = 0; m < NumModes; m++) {
                UfbxiStringPool pool = NewPool(error, warnResult, Modes[m]);
                for (int i = 0; i < NumCases; i++) {
                    string d = GStr[i];
                    int n = GLen[i];
                    bool threw = false;
                    try {
                        pool.PushStringPlace(ref d, ref n, m == 0);
                    } catch (Exception e) {
                        threw = true;
                        d = null;
                        n = 0;
                        Check("push_string_place P" + m + "/" + i, false, e.GetType().Name + " " + e.Message);
                    }
                    if (threw) continue;
                    pushRef[m, i] = d;
                    pushLen[m, i] = n;
                }
            }

            // C: the `S` pass -- ufbxi_push_sanitized_string over a fresh pool.
            string[] rawRef = new string[NumCases];
            string[] utf8Ref = new string[NumCases];
            int[] rawLen = new int[NumCases];
            int[] utf8Len = new int[NumCases];
            {
                UfbxiStringPool pool = NewPool(error, warnResult, UfbxUnicodeErrorHandling.ReplacementCharacter);
                for (int i = 0; i < NumCases; i++) {
                    int n = GLen[i];
                    if (n == 0) continue;
                    bool nonAscii;
                    uint hash = UfbxiHash.HashStringCheckAscii(GStr[i], 0, n, out nonAscii);
                    UfbxiSanitizedString sanitized = default(UfbxiSanitizedString);
                    try {
                        pool.PushSanitizedString(ref sanitized, GStr[i], 0, n, hash, nonAscii, false);
                    } catch (Exception e) {
                        Check("push_sanitized_string S" + i, false, e.GetType().Name + " " + e.Message);
                        continue;
                    }
                    rawRef[i] = sanitized.RawData;
                    utf8Ref[i] = sanitized.Utf8Data;
                    rawLen[i] = sanitized.RawLength;
                    utf8Len[i] = sanitized.Utf8Length;
                }
            }

            // C: the `F`/`K` pass -- intern the constants the way ufbxi_load_strings()
            // (ufbx.c:11407-11422) does, then intern a heap copy of a sample of them and of
            // some names that are not in the table.
            Check("F string count", s_expStringCount == UfbxiStrings.All.Length,
                "got " + UfbxiStrings.All.Length + " want " + s_expStringCount);
            Check("F string digest", StringsDigest(UfbxiStrings.All) == s_expStringDigest,
                "got " + StringsDigest(UfbxiStrings.All) + " want " + s_expStringDigest);
            Check("L record count", ExpL.Count == UfbxiStrings.All.Length,
                "got " + UfbxiStrings.All.Length + " want " + ExpL.Count);
            for (int i = 0; i < UfbxiStrings.All.Length && i < ExpL.Count; i++) {
                string s = UfbxiStrings.All[i];
                string hex = HexOf(s, s.Length);
                Check("L " + i + " " + hex, hex == ExpL[i], "got " + hex + " want " + ExpL[i]);
            }

            // C: ufbx_assert(ufbxi_str_less(reg_prev, *str)) in ufbxi_load_strings()
            // (ufbx.c:11418, UFBX_REGRESSION builds) -- the table has to be strictly ascending,
            // because ufbx.c:26619 resolves prop names against it with a linear walk that only
            // works on a sorted table.
            bool sorted = true;
            for (int i = 1; i < UfbxiStrings.All.Length; i++) {
                if (!UfbxiStr.Less(UfbxiStrings.All[i - 1], UfbxiStrings.All[i])) {
                    sorted = false;
                    Check("L sorted at " + i, false, HexOf(UfbxiStrings.All[i - 1], UfbxiStrings.All[i - 1].Length) +
                        " !< " + HexOf(UfbxiStrings.All[i], UfbxiStrings.All[i].Length));
                    break;
                }
            }
            Check("L table strictly sorted", sorted);

            List<string> nameRef = new List<string>();
            List<int> nameLen = new List<int>();
            {
                UfbxiStringPool pool = NewPool(error, warnResult, UfbxUnicodeErrorHandling.ReplacementCharacter);
                UfbxiStringPool.LoadStrings(pool);
                string[] all = UfbxiStrings.All;
                for (int i = 0; i + 41 < all.Length; i += 41) RunName(pool, nameRef, nameLen, i);
                RunName(pool, nameRef, nameLen, all.Length - 1);
                RunRawName(pool, nameRef, nameLen, "ZqNotARealFbxName");
                RunRawName(pool, nameRef, nameLen, "GeometryX");
                // C: "\xc3\xa9Name\0" -- the raw-byte form of the UTF-8 encoding of 'é',
                // with a trailing NUL that is named data (7 bytes, not a terminator).
                RunRawName(pool, nameRef, nameLen, "Ã©Name\0");
                RunRawName(pool, nameRef, nameLen, "a");
            }
            Check("K record count", nameRef.Count == ExpK.Count, "got " + nameRef.Count + " want " + ExpK.Count);

            // C: collect_ptrs() -- same multiset, same order (the order is irrelevant to the
            // rank, but keeping it identical makes the two runs comparable).
            for (int i = 0; i < NumCases; i++) {
                for (int m = 0; m < NumModes; m++) AddRanked(pushRef[m, i], GrpP0 + m);
                AddRanked(rawRef[i], GrpSanitized);
                AddRanked(utf8Ref[i], GrpSanitized);
            }
            for (int i = 0; i < nameRef.Count; i++) AddRanked(nameRef[i], GrpNames);

            CheckPushRecords(pushRef, pushLen);
            CheckSanitizedRecords(rawRef, utf8Ref, rawLen, utf8Len);
            CheckNameRecords(nameRef, nameLen);

            Console.WriteLine("utilcheck: " + s_checks + " checks, " + s_passed + " passed, " + s_failed + " failed");
            foreach (string f in s_failures) Console.WriteLine("  FAIL " + f);
            return s_failed == 0 ? 0 : 1;
        }

        static string FindOracle()
        {
            string[] candidates = {
                "tools/util_oracle.txt",
                Path.Combine("..", "util_oracle.txt"),
                "util_oracle.txt",
            };
            foreach (string c in candidates) {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        // H/A/V/C records: the pure functions.
        static void CheckPureFunctions()
        {
            for (int i = 0; i < NumCases; i++) {
                byte[] b = GBytes[i];
                string s = GStr[i];
                int n = GLen[i];

                uint hashStr = UfbxiHash.HashString(s, 0, n);
                Check("H " + i, hashStr == ExpH[i], "got " + hashStr.ToString("x8") + " want " + ExpH[i].ToString("x8"));
                // The two overloads must never disagree: `ufbx_string` is one representation
                // in C, so a divergence here would be a port-side bug rather than a C rule.
                Check("H bytes " + i, UfbxiHash.HashString(b, 0, n) == hashStr);

                if (n == 0) continue;

                bool nonAscii;
                uint aHash = UfbxiHash.HashStringCheckAscii(s, 0, n, out nonAscii);
                Check("A " + i + " hash", aHash == ExpA[i], "got " + aHash.ToString("x8") + " want " + ExpA[i].ToString("x8"));
                Check("A " + i + " non_ascii", nonAscii == ExpANonAscii[i], "got " + nonAscii + " want " + ExpANonAscii[i]);
                bool bNonAscii;
                Check("A bytes " + i, UfbxiHash.HashStringCheckAscii(b, 0, n, out bNonAscii) == aHash && bNonAscii == nonAscii);

                int valid = UfbxiUtf8.ValidLengthStr(s, 0, n);
                Check("V " + i, valid == ExpV[i], "got " + valid + " want " + ExpV[i]);
                Check("V bytes " + i, UfbxiUtf8.ValidLength(b, 0, n) == valid);

                uint key = UfbxiStr.GetConcatKey(new UfbxiConcatPart[] { new UfbxiConcatPart(s, n) }, 1);
                Check("C " + i, key == ExpC[i], "got " + key.ToString("x8") + " want " + ExpC[i].ToString("x8"));
            }
        }

        // ufbxi_str_* are memcmp-shaped; C's memcmp over the raw-byte representation is
        // exactly string.CompareOrdinal, so check the ported helpers against it.
        static void CheckStrHelpers()
        {
            string[] samples = { "", "a", "ab", "abc", "ABC", "\u00ff", "\u00ff\u0000", "a\u0000b", "AllSame", "Alphas" };
            foreach (string a in samples) {
                foreach (string bb in samples) {
                    int ordinal = string.CompareOrdinal(a, bb);
                    int cmp = UfbxiStr.Cmp(a, bb);
                    Check("str_cmp " + HexOf(a, a.Length) + " " + HexOf(bb, bb.Length),
                        Math.Sign(ordinal) == cmp && (a.Length == bb.Length || (ordinal < 0) == UfbxiStr.Less(a, bb)));
                    Check("str_equal " + HexOf(a, a.Length) + " " + HexOf(bb, bb.Length),
                        UfbxiStr.Equal(a, bb) == (ordinal == 0));
                }
            }
        }

        // C: the FNV-1a digest over ufbxi_strings[] contents, in declaration order, each
        // followed by a NUL separator -- proves UfbxiStrings.All matches entry for entry.
        static string StringsDigest(string[] all)
        {
            ulong h = 0xcbf29ce484222325ul;
            for (int i = 0; i < all.Length; i++) {
                string s = all[i];
                for (int j = 0; j < s.Length; j++) {
                    h = unchecked((h ^ (byte)s[j]) * 0x00000100000001b3ul);
                }
                h = unchecked(h * 0x00000100000001b3ul);
            }
            return h.ToString("x16");
        }

        // C: g_name_scratch -- a heap-side copy, so the address differs from the constant's
        static string FreshCopy(string s)
        {
            return UfbxiRawStr.FromBytes(UfbxiRawStr.ToBytes(s, 0, s.Length), 0, s.Length);
        }

        static void RunName(UfbxiStringPool pool, List<string> refs, List<int> lens, int index)
        {
            RunRawName(pool, refs, lens, UfbxiStrings.All[index]);
        }

        static void RunRawName(UfbxiStringPool pool, List<string> refs, List<int> lens, string name)
        {
            string d = FreshCopy(name);
            int n = d.Length;
            pool.PushStringPlace(ref d, ref n, true);
            refs.Add(d);
            lens.Add(n);
        }

        static void CheckNameRecords(List<string> refs, List<int> lens)
        {
            for (int i = 0; i < refs.Count && i < ExpK.Count; i++) {
                KRec e = ExpK[i];
                Check("K " + e.Index + " rank", RankOf(refs[i], GrpNames) == e.Rank, "got " + RankOf(refs[i], GrpNames) + " want " + e.Rank);
                Check("K " + e.Index + " length", lens[i] == e.Length, "got " + lens[i] + " want " + e.Length);
                if (e.Index >= 0) {
                    string constant = UfbxiStrings.All[e.Index];
                    bool matched = ReferenceEquals(refs[i], constant) && lens[i] == constant.Length;
                    Check("K " + e.Index + " resolved to constant", matched == (e.Matched != 0),
                        "got " + matched + " want " + (e.Matched != 0) + " (" + HexOf(constant, constant.Length) + ")");
                }
            }
        }

        static void CheckPushRecords(string[,] pushRef, int[,] pushLen)
        {
            for (int m = 0; m < NumModes; m++) {
                for (int i = 0; i < NumCases; i++) {
                    PRec e = ExpP[m, i];
                    string name = "P " + m + " " + i;
                    if (e == null) {
                        Check(name + " present", false, "no oracle record");
                        continue;
                    }
                    Check(name + " rank", RankOf(pushRef[m, i], GrpP0 + m) == e.Rank, "got " + RankOf(pushRef[m, i], GrpP0 + m) + " want " + e.Rank);
                    Check(name + " length", pushLen[m, i] == e.Length, "got " + pushLen[m, i] + " want " + e.Length);
                    Check(name + " data", HexOf(pushRef[m, i], e.Length) == e.Hex,
                        "got " + HexOf(pushRef[m, i], pushLen[m, i]) + " want " + e.Hex);
                }
            }
        }

        static void CheckSanitizedRecords(string[] rawRef, string[] utf8Ref, int[] rawLen, int[] utf8Len)
        {
            for (int i = 0; i < NumCases; i++) {
                SRec e = ExpS[i];
                if (e == null) {
                    // C only records the case for non-empty input
                    Check("S " + i + " absent", rawRef[i] == null && utf8Ref[i] == null);
                    continue;
                }
                Check("S " + i + " raw_rank", RankOf(rawRef[i], GrpSanitized) == e.RawRank, "got " + RankOf(rawRef[i], GrpSanitized) + " want " + e.RawRank);
                Check("S " + i + " raw_len", rawLen[i] == e.RawLength, "got " + rawLen[i] + " want " + e.RawLength);
                Check("S " + i + " raw", HexOf(rawRef[i], e.RawLength) == e.RawHex, "got " + HexOf(rawRef[i], rawLen[i]) + " want " + e.RawHex);
                Check("S " + i + " utf8_rank", RankOf(utf8Ref[i], GrpSanitized) == e.Utf8Rank, "got " + RankOf(utf8Ref[i], GrpSanitized) + " want " + e.Utf8Rank);
                Check("S " + i + " utf8_len", utf8Len[i] == e.Utf8Length, "got " + utf8Len[i] + " want " + e.Utf8Length);
                Check("S " + i + " utf8", HexOf(utf8Ref[i], e.Utf8Length) == e.Utf8Hex, "got " + HexOf(utf8Ref[i], utf8Len[i]) + " want " + e.Utf8Hex);
            }
        }
    }
}
