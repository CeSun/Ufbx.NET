// DomCheck: bit-exactness harness for the ported DOM layer.
//
// It replays the records of `tools/dom_oracle.c` (output `tools/dom_oracle.txt`) line by line
// against the ported parser: `tools/dom_oracle.c` drives the *public* C loader with
// `retain_dom = true` and dumps every `ufbx_dom_node` / `ufbx_dom_value` / `ufbx_dom_*`
// accessor result, so this harness pins down node names (and whether a name came back as one
// of the `ufbxi_*` constants), value types and payloads, array element bytes and the accessor
// contract, for the whole corpus.
//
// Both DOM parsers are covered: `ufbxi_begin_parse` picks binary vs ASCII from the 22-byte
// magic, and the driver below then runs `ufbxi_binary_parse_node` or `ufbxi_ascii_parse_node`
// through the same `UfbxiDom.ParseNode` dispatch as the C loader. The counts of each are
// reported only as a sanity check on that dispatch.
//
// Driver equivalence (ufbx.c:11266-11326 vs the DOM retention at 10809-10849): C walks the
// top-level nodes name-driven, and retains each top node with `ufbxi_retain_toplevel()` before
// retaining its children (`ufbxi_retain_toplevel_child()`, eagerly for a skipped node, on
// demand for the requested one). `ufbxi_retain_toplevel()` always pops the pending children
// onto the *previously* retained top node, so children can only ever be attached in file
// order; the sequential loop below therefore produces the same tree. For a pre-6000 file C
// parses each top node recursively instead (`ufbxi_parse_legacy_toplevel`, ufbx.c:11375-11403)
// and `ufbxi_retain_dom_node()` recurses into the already-parsed children, which the `legacy`
// branch reproduces.
//
// Usage: dotnet run --project tools/DomCheck -c Release -- [tools/dom_oracle.txt] [ufbx root]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx;

namespace DomCheck
{
    static class Program
    {
        // Must match tools/dom_oracle.c
        const ulong FnvBasis = 0xcbf29ce484222325ul;
        const ulong FnvPrime = 0x00000100000001b3ul;

        // C: UFBXI_BINARY_MAGIC_SIZE (ufbx.c:9395)
        const int BinaryMagicSize = 22;
        const int BinaryHeaderSize = 27;

        const int MaxInline = 32;      // oracle emit_payload()
        const int MaxErrInline = 64;   // oracle S record

        // C: ufbxi_load() -> ufbxi_read_root()/ufbxi_read_legacy_root() (ufbx.c:25302)
        const uint LegacyVersionLimit = 6000;

        // C: the string pool is initialized with initial_size 1024 for a scene load
        // (ufbx.c:25553), which fixes the map geometry the pool's interning depends on.
        const uint PoolInitialSize = 1024;

        // C: ufbxi_fix_error_type(&uc->error, "Failed to load", p_error) (ufbx.c:25623)
        const string DefaultErrorDescription = "Failed to load";

        // Known-pending divergences: records the oracle produces with code the port has not
        // ported yet, kept so the rest of the corpus stays a real regression gate. A key that
        // stops diverging is reported as `LEDGER STALE`, so an entry cannot outlive its cause.
        // Key = the record's tag, file index and node id (`-` where a record has no node id).
        //
        // The six `PointsIndex` records: C retains the DOM blob over the *same* buffer the
        // element reader uses -- `line->point_indices.data = (uint32_t*)points_index->data`
        // (ufbx.c:13915) -- and `ufbxi_read_line()` then rewrites it in place, turning each
        // negative end-of-line marker into `~ix` and clamping out-of-range indices
        // (ufbx.c:13931-13948). So a file value of `-1` shows up in C's DOM as `0`, and `-5` as
        // `4`. The port has no element-reader layer yet, so its DOM still holds the file bytes;
        // these records go green when `ufbxi_read_line` lands. (Rule for the port: a retained
        // DOM array aliases the scene-build buffer, so any in-place fixup C makes to `arr->data`
        // is observable through the DOM API and must be reproduced.)
        //
        // The fi 64 `S` record is the open error-description item: with
        // `UFBXI_FEATURE_ERROR_STACK == 0` a plain `ufbxi_check(cond)` leaves
        // `error.description` unset and `ufbxi_fix_error_type()` reports "Failed to load", while
        // the port currently reports the condition text. Needs the check-vs-msg distinction in
        // the error layer (registered in COORDINATION.md), not a DOM fix.
        struct KnownDivergence {
            public string Key;
            public string ExpectPort;
            public string Reason;
            public bool Matched;
        }

        // `ExpectPort` pins what the port produces *today*: a ledger entry only excuses that
        // exact record, so a new ASCII-parsing or error-reporting regression behind the same key
        // still fails the run.
        static readonly KnownDivergence[] KnownDivergences = new KnownDivergence[] {
            new KnownDivergence {
                Key = "V 32 197",
                ExpectPort = "V 32 197 0 3 0000000000000007 401c000000000000 0 cbf29ce484222325 - 28 9b09ab92eda41bd0 000000000100000002000000030000000400000005000000ffffffff ",
                Reason = "ufbxi_read_line() in-place PointsIndex rewrite",
            },
            new KnownDivergence {
                Key = "D 32 197",
                ExpectPort = "D 32 197 1 7 7 9b09ab92eda41bd0 0 cbf29ce484222325 0 cbf29ce484222325 0 cbf29ce484222325 0 cbf29ce484222325 ",
                Reason = "the same array through ufbx_dom_as_i32_list()",
            },
            new KnownDivergence {
                Key = "V 32 207",
                ExpectPort = "V 32 207 0 3 000000000000000f 402e000000000000 0 cbf29ce484222325 - 60 58f68adf473feab6 - ",
                Reason = "ufbxi_read_line() in-place PointsIndex rewrite",
            },
            new KnownDivergence {
                Key = "D 32 207",
                ExpectPort = "D 32 207 1 15 15 58f68adf473feab6 0 cbf29ce484222325 0 cbf29ce484222325 0 cbf29ce484222325 0 cbf29ce484222325 ",
                Reason = "the same array through ufbx_dom_as_i32_list()",
            },
            new KnownDivergence {
                Key = "V 55 387",
                ExpectPort = "V 55 387 0 3 0000000000000005 4014000000000000 0 cbf29ce484222325 - 20 65cb214831f3714d 00000000010000000200000003000000fbffffff ",
                Reason = "ufbxi_read_line() in-place PointsIndex rewrite",
            },
            new KnownDivergence {
                Key = "D 55 387",
                ExpectPort = "D 55 387 1 5 5 65cb214831f3714d 0 cbf29ce484222325 0 cbf29ce484222325 0 cbf29ce484222325 0 cbf29ce484222325 ",
                Reason = "the same array through ufbx_dom_as_i32_list()",
            },
        };

        static int numKnown;

        static string KnownKey(string record)
        {
            string[] f = record.Split(' ');
            if (f.Length < 2) return null;
            return f[0] + " " + f[1] + " " + (f[0] == "S" ? "-" : f[2]);
        }

        static int KnownDiffIndex(string recordKey, string got)
        {
            string gotTrimmed = got.TrimEnd();
            for (int i = 0; i < KnownDivergences.Length; i++) {
                if (KnownDivergences[i].Key != recordKey) continue;
                if (KnownDivergences[i].ExpectPort.TrimEnd() != gotTrimmed) continue;
                return i;
            }
            return -1;
        }

        static ulong numNodes;
        static ulong numValues;
        static ulong numStaticNames;

        static int Main(string[] args)
        {
            string oraclePath = args.Length > 0 ? args[0] : FindOracle();
            string oracleDir = Path.GetDirectoryName(Path.GetFullPath(oraclePath));
            string ufbxRoot = args.Length > 1 ? args[1] : FindUfbxRoot(oracleDir);
            string cSourcePath = Path.Combine(oracleDir, "dom_oracle.c");

            if (!File.Exists(oraclePath)) {
                Console.WriteLine("missing oracle output: " + oraclePath);
                return 1;
            }
            if (!File.Exists(cSourcePath)) {
                Console.WriteLine("missing oracle source: " + cSourcePath);
                return 1;
            }

            // The corpus list is read from the oracle itself so the two sides can never drift.
            List<string> files = ReadOracleFileList(cSourcePath);
            if (files.Count == 0) {
                Console.WriteLine("no corpus files found in " + cSourcePath);
                return 1;
            }

            Dictionary<int, List<string>> expected = ReadOracleRecords(oraclePath, files.Count);

            StringBuilder all = new StringBuilder();
            int checkedFiles = 0, numBinary = 0, numAscii = 0, failedFiles = 0, totalDiffs = 0;

            for (int fi = 0; fi < files.Count; fi++) {
                string path = Path.Combine(ufbxRoot, files[fi].Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) {
                    Console.WriteLine("MISSING INPUT " + files[fi]);
                    failedFiles++;
                    continue;
                }

                byte[] data = File.ReadAllBytes(path);
                bool expectBinary = IsBinary(data);

                checkedFiles++;
                List<string> got = new List<string>();
                bool parsedAscii, formatDecided;
                RunFile(data, fi, got, out parsedAscii, out formatDecided);
                if (formatDecided && parsedAscii == expectBinary) {
                    Console.WriteLine("FORMAT MISMATCH " + files[fi] + ": magic says " +
                        (expectBinary ? "binary" : "ASCII") + ", the port parsed " +
                        (parsedAscii ? "ASCII" : "binary"));
                    failedFiles++;
                }
                if (expectBinary) numBinary++; else numAscii++;
                for (int i = 0; i < got.Count; i++) all.Append(got[i]).Append("\r\n");

                List<string> want;
                if (!expected.TryGetValue(fi, out want)) {
                    Console.WriteLine("no oracle records for file index " + fi + " (" + files[fi] + ")");
                    failedFiles++;
                    continue;
                }

                int diffs = Compare(fi, files[fi], want, got);
                if (diffs > 0) {
                    failedFiles++;
                    totalDiffs += diffs;
                }
            }

            File.WriteAllText(Path.Combine(oracleDir, "dom_port.txt"), all.ToString());

            int numStale = 0;
            for (int i = 0; i < KnownDivergences.Length; i++) {
                if (KnownDivergences[i].Matched) continue;
                Console.WriteLine("LEDGER STALE " + KnownDivergences[i].Key + ": " + KnownDivergences[i].Reason);
                numStale++;
            }
            if (numStale > 0) failedFiles++;

            Console.WriteLine("files checked:         " + checkedFiles + " (" + numBinary + " binary, " + numAscii + " ascii)");
            Console.WriteLine("known pending records: " + numKnown + " / " + KnownDivergences.Length);
            Console.WriteLine("files with divergence: " + failedFiles);
            Console.WriteLine("divergent records:     " + totalDiffs);
            bool pass = totalDiffs == 0 && failedFiles == 0;
            Console.WriteLine(pass ? "DOM CHECK PASS" : "DOM CHECK FAIL");
            return pass ? 0 : 1;
        }

        static string FindOracle()
        {
            string p = Path.GetFullPath("tools/dom_oracle.txt");
            if (File.Exists(p)) return p;
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dom_oracle.txt"));
        }

        static string FindUfbxRoot(string oracleDir)
        {
            // tools/ lives in the port repo; the C corpus is in the sibling checkout.
            string local = Path.Combine(oracleDir, "..", "data");
            if (Directory.Exists(local)) return Path.GetFullPath(oracleDir + Path.DirectorySeparatorChar + "..");
            const string fallback = @"C:\Workspace\_analyze_ufbx";
            return fallback;
        }

        // Scrape `g_default_files[]` from the oracle source: the same list, the same order,
        // so the `fi` index in the records maps back to the input file.
        static List<string> ReadOracleFileList(string cSourcePath)
        {
            string text = File.ReadAllText(cSourcePath);
            int anchor = text.IndexOf("g_default_files[]", StringComparison.Ordinal);
            List<string> files = new List<string>();
            if (anchor < 0) return files;
            int open = text.IndexOf('{', anchor);
            int close = text.IndexOf("};", open, StringComparison.Ordinal);
            if (open < 0 || close < 0) return files;
            for (int i = open; i < close; i++) {
                if (text[i] != '"') continue;
                int end = text.IndexOf('"', i + 1);
                if (end < 0 || end > close) break;
                files.Add(text.Substring(i + 1, end - i - 1));
                i = end;
            }
            return files;
        }

        // Group the oracle's records by file index, keeping their order.
        static Dictionary<int, List<string>> ReadOracleRecords(string oraclePath, int numFiles)
        {
            Dictionary<int, List<string>> records = new Dictionary<int, List<string>>();
            foreach (string line in File.ReadLines(oraclePath)) {
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                string tag = line.Substring(0, sp);
                if (tag != "S" && tag != "N" && tag != "V" && tag != "D" && tag != "T") continue;
                int space = line.IndexOf(' ', sp + 1);
                int fi = int.Parse(space < 0 ? line.Substring(sp + 1) : line.Substring(sp + 1, space - sp - 1),
                    CultureInfo.InvariantCulture);
                List<string> list;
                if (!records.TryGetValue(fi, out list)) {
                    list = new List<string>();
                    records[fi] = list;
                }
                list.Add(line);
            }
            return records;
        }

        static bool IsBinary(byte[] data)
        {
            if (data.Length < BinaryHeaderSize) return false;
            if (data[0] != (byte)'K') return false;
            const string text = "Kaydara FBX Binary  ";
            for (int i = 0; i < text.Length; i++) {
                if (data[i] != (byte)text[i]) return false;
            }
            return data[text.Length] == 0 && data[text.Length + 1] == 0x1a;
        }

        // ------------------------------------------------------------------
        // One corpus file
        // ------------------------------------------------------------------

        static void RunFile(byte[] data, int fi, List<string> outRecords, out bool parsedAscii, out bool formatDecided)
        {
            UfbxError error = new UfbxError();
            UfbxiPrint.ClearError(error);

            bool ok = true;
            UfbxDomNode domRoot = null;

            // BuildDom publishes the context here as soon as it exists, so a load that throws
            // mid-parse still reports which format `ufbxi_begin_parse` had picked.
            UfbxiContext[] slot = new UfbxiContext[1];
            try {
                BuildDom(data, error, slot, out domRoot);
            } catch (UfbxParseError e) {
                // C: plain `ufbxi_check(cond)`/`ufbxi_fail(desc)` leave `error.description`
                // unset, so `ufbxi_fix_error_type()` reports "Failed to load"; only
                // `ufbxi_check_msg`/`ufbxi_error_msg` carry a description. `UfbxParseError`
                // reproduces the split through `HasDescription` (Parse/Error.cs), so this mirror
                // of C's tail must honour it: fi 64 (`synthetic_bad_inf_nan_fail_7500_ascii.fbx`)
                // fails at a plain `ufbxi_check(end == ...)` in the ASCII tokenizer.
                ok = false;
                if (e.HasDescription) error.Description = e.Message;
                UfbxiPrint.FixErrorType(error, DefaultErrorDescription, null);
            }

            UfbxiContext uc = slot[0];
            numNodes = 0;
            numValues = 0;
            numStaticNames = 0;
            parsedAscii = uc != null && uc.FromAscii;

            // A failed load only reveals the port's choice when it took the ASCII branch:
            // `FromAscii` is set by `ufbxi_begin_parse()` for ASCII and left `false` for binary,
            // so `false` on a binary file that failed in the header is indistinguishable from
            // "never reached the parser" and would report a false mismatch.
            formatDecided = uc != null && (ok || uc.FromAscii);

            outRecords.Add(EmitSummary(fi, ok, error));

            if (ok) {
                if (domRoot != null) {
                    Walk(fi, domRoot, 0, ulong.MaxValue, outRecords);
                } else {
                    outRecords.Add("N " + fi + " 0 0 " + ulong.MaxValue + " 0 0 1 -");
                }
            }

            outRecords.Add("T " + fi + " " + numNodes + " " + numValues + " " + numStaticNames);
        }

        static string EmitSummary(int fi, bool ok, UfbxError error)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("S ").Append(fi).Append(' ').Append(ok ? 1 : 0);
            if (ok) {
                sb.Append(" 0 0 0 - 0 0000000000000000 -");
                return sb.ToString();
            }

            byte[] desc = UfbxiRawStr.ToBytes(error.Description ?? string.Empty);
            int descLen = error.Description == null ? 0 : error.Description.Length;
            bool descInInfo = !string.IsNullOrEmpty(error.Description) && ReferenceEquals(error.Description, error.Info);

            sb.Append(' ').Append((int)error.Type).Append(' ').Append(descLen).Append(' ').Append(descInInfo ? 1 : 0);
            if (desc != null && descLen > 0 && descLen <= MaxErrInline) sb.Append(' ').Append(Hex(desc, 0, descLen));
            else sb.Append(" -");

            byte[] info = UfbxiRawStr.ToBytes(error.Info ?? string.Empty);
            int infoLen = error.InfoLength;
            sb.Append(' ').Append(infoLen).Append(' ').Append(Fnv64(info, 0, infoLen).ToString("x16", CultureInfo.InvariantCulture));
            if (info != null && infoLen > 0 && infoLen <= MaxErrInline) sb.Append(' ').Append(Hex(info, 0, infoLen));
            else sb.Append(" -");
            return sb.ToString();
        }

        static UfbxiContext BuildDom(byte[] data, UfbxError error, UfbxiContext[] slot, out UfbxDomNode domRoot)
        {
            UfbxiContext uc = new UfbxiContext();
            slot[0] = uc;
            uc.Opts = new UfbxLoadOpts();
            uc.Opts.RetainDom = true;
            uc.InitDerivedOpts();
            uc.InitStringPool(error, PoolInitialSize);

            // C: ufbx_load_memory() (ufbx.c:30510-30519) -- the whole input is the buffer, so
            // `read_fn == NULL` and every yield window comes out of `Buffer`. No progress
            // callback, so `progress_interval` stays SIZE_MAX (ufbx.c:25531-25536).
            UfbxiStream stream = new UfbxiStream();
            stream.Buffer = data;
            stream.BeginIndex = 0;
            stream.Position = 0;
            stream.Remaining = data.Length;
            stream.YieldSize = 0;
            stream.DataOffset = 0;
            stream.ProgressBytesTotal = (ulong)data.Length;
            stream.ProgressInterval = ulong.MaxValue;
            uc.Stream = stream;

            UfbxiDom.BeginParse(uc);

            bool legacy = uc.Version < LegacyVersionLimit;
            for (;;) {
                bool end;
                UfbxiDom.ParseNode(uc, 0, UfbxiParseState.Root, out end, legacy);
                if (end) break;

                UfbxiNode node = uc.PopNode();
                UfbxiDom.RetainToplevel(uc, node);

                if (legacy || !uc.HasNextChild) continue;

                // C: ufbxi_parse_toplevel() 11306-11324 -- parse (and retain) the children of a
                // top-level node that is not the requested one, in file order.
                UfbxiParseState state = UfbxiParseStateMachine.Update(UfbxiParseState.Root, node.Name);
                uint numChildren = 0;
                for (;;) {
                    bool childEnd;
                    UfbxiDom.ParseNode(uc, 0, state, out childEnd, true);
                    if (childEnd) break;
                    numChildren++;
                }
                node.NumChildren = numChildren;
                UfbxiNode[] children = uc.PopNodes((int)numChildren);
                node.Children = children;
                for (uint i = 0; i < numChildren; i++) {
                    UfbxiDom.RetainToplevelChild(uc, children[i]);
                }
            }

            domRoot = UfbxiDom.RetainToplevel(uc, null);
            return uc;
        }

        // C: walk() in tools/dom_oracle.c -- pre-order N/V/D records.
        static void Walk(int fi, UfbxDomNode n, uint depth, ulong parent, List<string> outRecords)
        {
            ulong id = numNodes++;
            bool staticName = UfbxiPtrIdTable.IsStatic(n.Name);
            if (staticName) numStaticNames++;

            int nameLen = n.Name == null ? 0 : n.Name.Length;
            StringBuilder sb = new StringBuilder();
            sb.Append("N ").Append(fi).Append(' ').Append(id).Append(' ').Append(depth).Append(' ')
              .Append(parent).Append(' ')
              .Append(n.Children == null ? 0 : n.Children.Length).Append(' ')
              .Append(nameLen).Append(' ').Append(staticName ? 1 : 0).Append(' ');
            byte[] name = UfbxiRawStr.ToBytes(n.Name);
            if (name != null && nameLen > 0 && nameLen <= MaxInline) sb.Append(Hex(name, 0, nameLen));
            else sb.Append('-');
            outRecords.Add(sb.ToString());

            UfbxDomValue[] values = n.Values;
            if (values != null) {
                for (int i = 0; i < values.Length; i++) {
                    UfbxDomValue v = values[i];
                    sb = new StringBuilder();
                    ulong floatBits = unchecked((ulong)BitConverter.DoubleToInt64Bits(v.ValueFloat));
                    sb.Append("V ").Append(fi).Append(' ').Append(id).Append(' ').Append(i).Append(' ')
                      .Append((int)v.Type).Append(' ')
                      .Append(unchecked((ulong)v.ValueInt).ToString("x16", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(floatBits.ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
                    AppendPayload(sb, UfbxiRawStr.ToBytes(v.ValueStr), v.ValueStr == null ? 0 : v.ValueStr.Length);
                    AppendPayload(sb, v.ValueBlob, v.ValueBlob == null ? 0 : v.ValueBlob.Length);
                    outRecords.Add(sb.ToString());
                    numValues++;
                }
            }

            sb = new StringBuilder();
            sb.Append("D ").Append(fi).Append(' ').Append(id).Append(' ')
              .Append(UfbxDom.IsArray(n) ? 1 : 0).Append(' ').Append((ulong)UfbxDom.ArraySize(n)).Append(' ');
            AppendInt32List(sb, UfbxDom.AsInt32List(n));
            AppendInt64List(sb, UfbxDom.AsInt64List(n));
            AppendFloatList(sb, UfbxDom.AsFloatList(n));
            AppendDoubleList(sb, UfbxDom.AsDoubleList(n));
            AppendBlobList(sb, UfbxDom.AsBlobList(n));
            outRecords.Add(sb.ToString());

            if (n.Children != null) {
                for (int i = 0; i < n.Children.Length; i++) {
                    Walk(fi, n.Children[i], depth + 1, id, outRecords);
                }
            }
        }

        // ------------------------------------------------------------------
        // Record emission, matching the oracle's printf() formats
        // ------------------------------------------------------------------

        // C: emit_payload(): "<len> <digest> <hex | '-'> "
        static void AppendPayload(StringBuilder sb, byte[] data, int len)
        {
            sb.Append(len).Append(' ').Append(Fnv64(data, 0, len).ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
            if (data != null && len > 0 && len <= MaxInline) sb.Append(Hex(data, 0, len)).Append(' ');
            else sb.Append("- ");
        }

        // C: emit_list(): the digest is over the raw element bytes, so re-encode the port's
        // decoded list -- an accessor that misreads endianness shows up here.
        static void AppendInt32List(StringBuilder sb, int[] list)
        {
            int count = list == null ? 0 : list.Length;
            byte[] bytes = new byte[count * 4];
            for (int i = 0; i < count; i++) {
                uint v = unchecked((uint)list[i]);
                for (int k = 0; k < 4; k++) bytes[i * 4 + k] = (byte)(v >> (8 * k));
            }
            sb.Append(count).Append(' ').Append(Fnv64(bytes, 0, bytes.Length).ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
        }

        static void AppendInt64List(StringBuilder sb, long[] list)
        {
            int count = list == null ? 0 : list.Length;
            byte[] bytes = new byte[count * 8];
            for (int i = 0; i < count; i++) {
                ulong v = unchecked((ulong)list[i]);
                for (int k = 0; k < 8; k++) bytes[i * 8 + k] = (byte)(v >> (8 * k));
            }
            sb.Append(count).Append(' ').Append(Fnv64(bytes, 0, bytes.Length).ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
        }

        static void AppendFloatList(StringBuilder sb, float[] list)
        {
            int count = list == null ? 0 : list.Length;
            byte[] bytes = new byte[count * 4];
            for (int i = 0; i < count; i++) {
                uint v = UfbxBitUtil.SingleToBits(list[i]);
                for (int k = 0; k < 4; k++) bytes[i * 4 + k] = (byte)(v >> (8 * k));
            }
            sb.Append(count).Append(' ').Append(Fnv64(bytes, 0, bytes.Length).ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
        }

        static void AppendDoubleList(StringBuilder sb, double[] list)
        {
            int count = list == null ? 0 : list.Length;
            byte[] bytes = new byte[count * 8];
            for (int i = 0; i < count; i++) {
                ulong v = unchecked((ulong)BitConverter.DoubleToInt64Bits(list[i]));
                for (int k = 0; k < 8; k++) bytes[i * 8 + k] = (byte)(v >> (8 * k));
            }
            sb.Append(count).Append(' ').Append(Fnv64(bytes, 0, bytes.Length).ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
        }

        // C: emit_listb(): blobs are indirect, so hash each entry's bytes and size.
        static void AppendBlobList(StringBuilder sb, byte[][] list)
        {
            int count = list == null ? 0 : list.Length;
            ulong h = FnvBasis;
            for (int i = 0; i < count; i++) {
                byte[] b = list[i];
                h = Fnv64(b, 0, b == null ? 0 : b.Length) ^ (h * FnvPrime);
                h = (h ^ (ulong)(b == null ? 0 : b.Length)) * FnvPrime;
            }
            sb.Append(count).Append(' ').Append(h.ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
        }

        static ulong Fnv64(byte[] data, int offset, int length)
        {
            ulong h = FnvBasis;
            if (data != null) {
                for (int i = 0; i < length; i++) {
                    h = (h ^ data[offset + i]) * FnvPrime;
                }
            }
            return h;
        }

        static string Hex(byte[] data, int offset, int length)
        {
            StringBuilder sb = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++) {
                sb.Append(data[offset + i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Comparison
        // ------------------------------------------------------------------

        const int MaxReportedDiffs = 8;

        static int Compare(int fi, string name, List<string> want, List<string> got)
        {
            int diffs = 0;
            int count = Math.Max(want.Count, got.Count);
            for (int i = 0; i < count; i++) {
                string a = i < want.Count ? want[i] : "<missing>";
                string b = i < got.Count ? got[i] : "<missing>";
                if (a == b) continue;
                string key = KnownKey(a);
                int known = key != null ? KnownDiffIndex(key, b) : -1;
                if (known >= 0 && !KnownDivergences[known].Matched) {
                    KnownDivergences[known].Matched = true;
                    numKnown++;
                    Console.WriteLine("KNOWN " + key + " " + name + " record " + i + ": " +
                        KnownDivergences[known].Reason);
                    continue;
                }
                diffs++;
                if (diffs <= MaxReportedDiffs) {
                    Console.WriteLine("DIFF " + fi + " " + name + " record " + i);
                    Console.WriteLine("  C#: " + a);
                    Console.WriteLine("  me: " + b);
                }
            }
            if (diffs > MaxReportedDiffs) {
                Console.WriteLine("DIFF " + fi + " " + name + ": " + (diffs - MaxReportedDiffs) + " more records");
            }
            if (want.Count != got.Count) {
                Console.WriteLine("RECORD COUNT " + fi + " " + name + ": oracle " + want.Count + " port " + got.Count);
            }
            return diffs;
        }
    }
}
