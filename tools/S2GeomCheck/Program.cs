// S2GeomCheck: isolated verification for the S2 geometry readers (Parse/Geometry.cs).
//
// What it proves
// --------------
// `ufbxi_read_line()` (ufbx.c:13899) is the one S2 reader that mutates a retained DOM
// array in place: `line->point_indices.data = (uint32_t*)points_index->data` aliases the
// DOM blob (ufbx.c:10754), so the `~ix` / clamp fixups of ufbx.c:13931-13948 are visible
// through the DOM API. The C oracle (tools/dom_oracle.c, driving the *public* loader with
// retain_dom=true) shows those rewritten bytes for the two corpus files that contain line
// curves. The port's `DomArrayBlob` is a copy, so `UfbxiGeometry.ReadLine` mirrors every
// store into `arr.Data` AND the retained DOM blob.
//
// This harness rebuilds the retained DOM of exactly those files (fi 32 =
// max_curve_line_7500_binary.fbx, fi 55 = maya_tangent_clamped_7700_ascii.fbx) with the
// DomCheck BuildDom sequence, then drives `UfbxiGeometry.ReadLine` on the parsed
// `Geometry` node (the same node the C dispatcher would reach through
// `ufbxi_read_objects` -> Geometry/"Line"), and re-emits the oracle's N/V/D/T records for
// the whole retained DOM. Every record must match `tools/dom_oracle.txt` byte for byte --
// NO ledger: before ReadLine landed, exactly the six `PointsIndex` records of these two
// files diverged, so a full-record match IS the DOM-aliasing proof.
//
// Mutation rig: temporarily break the port (e.g. `ix = ~ix` -> `ix = 0u - ix` in
// UfbxiGeometry.ReadLine), rebuild, and this program must FAIL; restore and it must PASS.
//
// Usage: dotnet run --project tools/S2GeomCheck -c Release -- [tools/dom_oracle.txt] [ufbx root]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx;

namespace S2GeomCheck
{
    static class Program
    {
        // Must match tools/dom_oracle.c
        const ulong FnvBasis = 0xcbf29ce484222325ul;
        const ulong FnvPrime = 0x00000100000001b3ul;

        const int MaxInline = 32;      // oracle emit_payload()

        // C: ufbxi_load() -> ufbxi_read_root() (ufbx.c:25302)
        const uint LegacyVersionLimit = 6000;

        // C: the string pool is initialized with initial_size 1024 for a scene load
        // (ufbx.c:25553), which fixes the map geometry the pool's interning depends on.
        const uint PoolInitialSize = 1024;

        // The corpus file indices whose PointsIndex the C oracle shows post-mutation
        // (verified against tools/dom_oracle.txt: "V 32 197 ...", "V 32 207 ...",
        // "V 55 387 ...").
        static readonly int[] TargetFiles = new int[] { 32, 55 };

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

            List<string> files = ReadOracleFileList(cSourcePath);
            Dictionary<int, List<string>> expected = ReadOracleRecords(oraclePath);

            int totalDiffs = 0;
            foreach (int fi in TargetFiles) {
                if (fi >= files.Count) {
                    Console.WriteLine("BAD CONFIG: file index " + fi + " outside oracle list");
                    return 1;
                }
                string rel = files[fi];
                string path = Path.Combine(ufbxRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) {
                    Console.WriteLine("MISSING INPUT " + rel);
                    return 1;
                }

                List<string> got = new List<string>();
                RunFile(File.ReadAllBytes(path), fi, got);

                List<string> want;
                if (!expected.TryGetValue(fi, out want)) {
                    Console.WriteLine("no oracle records for file index " + fi + " (" + rel + ")");
                    return 1;
                }

                int diffs = Compare(fi, rel, want, got);
                if (diffs > 0) totalDiffs += diffs;
            }

            Console.WriteLine("divergent records: " + totalDiffs);
            bool pass = totalDiffs == 0;
            Console.WriteLine(pass ? "S2 GEOM CHECK PASS" : "S2 GEOM CHECK FAIL");
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
            string local = Path.Combine(oracleDir, "..", "data");
            if (Directory.Exists(local)) return Path.GetFullPath(oracleDir + Path.DirectorySeparatorChar + "..");
            const string fallback = @"C:\Workspace\_analyze_ufbx";
            return fallback;
        }

        // Same scrape as DomCheck: `g_default_files[]` in the oracle source, same order, so
        // the record's `fi` maps back to the input file.
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

        static Dictionary<int, List<string>> ReadOracleRecords(string oraclePath)
        {
            Dictionary<int, List<string>> records = new Dictionary<int, List<string>>();
            foreach (string line in File.ReadLines(oraclePath)) {
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                string tag = line.Substring(0, sp);
                if (tag != "N" && tag != "V" && tag != "D" && tag != "T") continue;
                int space = line.IndexOf(' ', sp + 1);
                int fi = int.Parse(line.Substring(sp + 1, space - sp - 1), CultureInfo.InvariantCulture);
                List<string> list;
                if (!records.TryGetValue(fi, out list)) {
                    list = new List<string>();
                    records[fi] = list;
                }
                list.Add(line);
            }
            return records;
        }

        // ------------------------------------------------------------------
        // One target file: retained DOM + parsed node tree, then ReadLine
        // ------------------------------------------------------------------

        static ulong numNodes;
        static ulong numValues;
        static ulong numStaticNames;

        static void RunFile(byte[] data, int fi, List<string> outRecords)
        {
            UfbxError error = new UfbxError();
            UfbxiPrint.ClearError(error);

            List<UfbxiNode> topNodes = new List<UfbxiNode>();
            UfbxDomNode domRoot = BuildDom(data, error, topNodes);

            // Locate every `Geometry` object node the C dispatcher (ufbxi_read_objects ->
            // Geometry/"Line", ufbx.c:14950+) would hand to ufbxi_read_line. A file can hold
            // several line curves (fi 32 has two: DOM nodes 197 and 207).
            List<UfbxiNode> geomNodes = new List<UfbxiNode>();
            FindNodes(topNodes, UfbxiStrings.Geometry, UfbxiStrings.PointsIndex, geomNodes);
            if (geomNodes.Count == 0) {
                Console.WriteLine("HARNESS ERROR fi " + fi + ": no Geometry node with a PointsIndex child");
                Environment.Exit(1);
            }

            // BuildDom publishes the context in `g_uc` as soon as it exists.
            UfbxiContext uc = g_uc;

            foreach (UfbxiNode geomNode in geomNodes) {
                // Mirror ufbxi_read_object's (ufbx.c:14972-14992) info construction; ReadLine
                // consumes none of the properties, so those are left empty.
                UfbxiElementInfo info = new UfbxiElementInfo();
                info.DomNode = UfbxiDom.GetDomNode(uc, geomNode);
                long fbxId;
                geomNode.GetValL(0, out fbxId);
                info.FbxId = UfbxiFbxId.ValidateFbxId(uc, unchecked((ulong)fbxId));
                string typeAndName, subTypeStr;
                geomNode.GetValRawString(1, out typeAndName);
                geomNode.GetValRawString(2, out subTypeStr);
                string typeStr, name;
                UfbxiFbxId.SplitTypeAndName(uc, typeAndName, out typeStr, out name);
                info.Name = name;

                // C: the ufbxi_map_init block of ufbxi_load() -- InsertFbxId (via
                // PushElement) needs FbxIdMap.
                uc.S1InitMaps();

                UfbxiGeometry.ReadLine(uc, geomNode, info);
            }

            numNodes = 0;
            numValues = 0;
            numStaticNames = 0;
            Walk(fi, domRoot, 0, ulong.MaxValue, outRecords);
            outRecords.Add("T " + fi + " " + numNodes + " " + numValues + " " + numStaticNames);
        }

        // Single-file driver state (BuildDom creates the context; the record walk and the
        // reader both need it).
        static UfbxiContext g_uc;

        // DomCheck.BuildDom verbatim (tools/DomCheck/Program.cs), plus the top-node list.
        static UfbxDomNode BuildDom(byte[] data, UfbxError error, List<UfbxiNode> topNodes)
        {
            UfbxiContext uc = new UfbxiContext();
            g_uc = uc;
            uc.Opts = new UfbxLoadOpts();
            uc.Opts.RetainDom = true;
            uc.InitDerivedOpts();
            uc.InitStringPool(error, PoolInitialSize);

            // C: ufbx_load_memory() (ufbx.c:30510-30519)
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
                topNodes.Add(node);
                UfbxiDom.RetainToplevel(uc, node);

                if (legacy || !uc.HasNextChild) continue;

                // C: ufbxi_parse_toplevel() 11306-11324 -- children of a non-requested
                // top-level node, parsed (and retained) in file order.
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

            UfbxDomNode domRoot = UfbxiDom.RetainToplevel(uc, null);
            return domRoot;
        }

        // Depth-first search: collect every node named `name` owning a child named `childName`.
        static void FindNodes(IEnumerable<UfbxiNode> nodes, string name, string childName, List<UfbxiNode> outNodes)
        {
            foreach (UfbxiNode n in nodes) {
                if (n == null) continue;
                if (n.Name == name && n.Children != null) {
                    foreach (UfbxiNode c in n.Children) {
                        if (c != null && c.Name == childName) { outNodes.Add(n); break; }
                    }
                }
                if (n.Children != null) {
                    FindNodes(n.Children, name, childName, outNodes);
                }
            }
        }

        // ------------------------------------------------------------------
        // Oracle record emission (DomCheck.Walk verbatim)
        // ------------------------------------------------------------------

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

        static void AppendPayload(StringBuilder sb, byte[] data, int len)
        {
            sb.Append(len).Append(' ').Append(Fnv64(data, 0, len).ToString("x16", CultureInfo.InvariantCulture)).Append(' ');
            if (data != null && len > 0 && len <= MaxInline) sb.Append(Hex(data, 0, len)).Append(' ');
            else sb.Append("- ");
        }

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
        // Comparison (no ledger: full-record equality)
        // ------------------------------------------------------------------

        static int Compare(int fi, string rel, List<string> want, List<string> got)
        {
            int diffs = 0;
            int count = Math.Max(want.Count, got.Count);
            for (int i = 0; i < count; i++) {
                string a = i < want.Count ? want[i] : "<missing>";
                string b = i < got.Count ? got[i] : "<missing>";
                if (a == b) continue;
                diffs++;
                if (diffs <= 8) {
                    Console.WriteLine("DIFF " + fi + " " + rel + " record " + i);
                    Console.WriteLine("  C:  " + a);
                    Console.WriteLine("  me: " + b);
                }
            }
            if (diffs > 8) {
                Console.WriteLine("DIFF " + fi + " " + rel + ": " + (diffs - 8) + " more records");
            }
            if (want.Count != got.Count) {
                Console.WriteLine("RECORD COUNT " + fi + " " + rel + ": oracle " + want.Count + " port " + got.Count);
                diffs++;
            }
            return diffs;
        }
    }
}
