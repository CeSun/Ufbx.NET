// S1 self-check harness.
//
// Drives the ported load spine (`UfbxiLoad.Load`) over corpus files. Because the load driver
// calls `UfbxiToplevel.SceneBuild(uc)` immediately after `UfbxiToplevel.ReadRoot(uc)`
// (src/Ufbx/Parse/Load.cs:858-863), the *reader name* of the thrown
// `UfbxiReaderNotPortedException` classifies each file exactly:
//
//   "ufbxi_pre/finalize_scene"  -> ReadRoot completed (aliases: ReadLegacyRoot completed too,
//                                  but legacy paths hit "ufbxi_parse_legacy_toplevel" first)
//   "ufbxi_read_mesh" / ...     -> ReadRoot reached an S2 deep reader (correct seam)
//   "ufbxi_obj_load"/"_mtl_load"-> non-FBX, out of scope
//
// On ReadRoot success it prints the S1 counters (elements / element pointers / fbx ids /
// node ids / connections / properties / templates) and the root id so a reader can sanity-check
// that the graph was really built.
//
// Usage:
//   dotnet run --project tools/S1SelfCheck -c Release -- [--root <dir>] [--all] [<relpath> ...]
//     --root <dir>   corpus root (default C:\Workspace\_analyze_ufbx)
//     --all          iterate tools/graph_corpus.txt instead of the explicit paths
//     <relpath>      one or more `data/..` paths relative to the root
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx;

namespace S1SelfCheck
{
    static class Program
    {
        sealed class Outcome
        {
            public string Kind;      // "read-root-ok" | "seam" | "parse-error" | "returned" | "crash"
            public string Reader;
            public string ErrorType;
            public string ErrorDescription;
            public string Crash;
            public ulong RootId;
            public uint NumElements;
            public int ElementPtrs;
            public int ElementFbxIds;
            public int NodeIds;
            public int Connections;
            public long Properties;
            public int Templates;
            public int TopNodes;
            public bool FromAscii;
            public uint Version;
        }

        static int Main(string[] args)
        {
            string root = @"C:\Workspace\_analyze_ufbx";
            bool all = false;
            bool dump = false;
            List<string> files = new List<string>();

            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "--root" && i + 1 < args.Length) {
                    root = args[++i];
                } else if (args[i] == "--all") {
                    all = true;
                } else if (args[i] == "--dump") {
                    dump = true;
                } else {
                    files.Add(args[i]);
                }
            }

            if (dump) return DumpMode(root, files);

            // With no explicit paths, sweep the whole graph corpus.
            if (files.Count == 0) all = true;

            if (all) {
                string corpusList = Path.Combine(FindRepoRoot(), "tools", "graph_corpus.txt");
                foreach (string line in File.ReadAllLines(corpusList)) {
                    string t = line.Trim();
                    if (t.Length > 0) files.Add(t);
                }
            }

            var okFiles = new List<string>();
            var seamCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var errorCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int crashCount = 0;
            int returnedCount = 0;

            var detail = new List<string>();

            foreach (string rel in files) {
                string path = Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel);
                Outcome o = Run(path);

                switch (o.Kind) {
                case "read-root-ok":
                    okFiles.Add(rel);
                    detail.Add(string.Format(CultureInfo.InvariantCulture,
                        "OK    {0}\n      version={1} ascii={2} elements={3} element_ptrs={4} fbx_ids={5} node_ids={6} connections={7} props={8} templates={9} top_nodes={10} root_id=0x{11:x16}",
                        rel, o.Version, o.FromAscii ? 1 : 0, o.NumElements, o.ElementPtrs, o.ElementFbxIds,
                        o.NodeIds, o.Connections, o.Properties, o.Templates, o.TopNodes, o.RootId));
                    break;
                case "seam":
                    Bump(seamCounts, o.Reader);
                    break;
                case "parse-error":
                    Bump(errorCounts, o.ErrorType + " | " + (o.ErrorDescription ?? "<no description>"));
                    Console.WriteLine("ERR   " + rel + " -> " + o.ErrorType + " | "
                        + (o.ErrorDescription ?? "<no description>"));
                    break;
                case "returned":
                    returnedCount++;
                    break;
                case "crash":
                    crashCount++;
                    Console.WriteLine("CRASH " + rel + " -> " + o.Crash);
                    break;
                }
            }

            Console.WriteLine("=== S1 self-check ===");
            Console.WriteLine("corpus root : " + root);
            Console.WriteLine("files       : " + files.Count);
            Console.WriteLine();
            Console.WriteLine("ReadRoot completed : " + okFiles.Count);
            foreach (string f in okFiles) Console.WriteLine("  " + f);
            Console.WriteLine();
            Console.WriteLine("Reader seam hits   : " + Sum(seamCounts));
            foreach (var kv in seamCounts) Console.WriteLine("  " + kv.Value.ToString().PadLeft(5) + "  " + kv.Key);
            Console.WriteLine();
            Console.WriteLine("Parse errors       : " + Sum(errorCounts));
            foreach (var kv in errorCounts) Console.WriteLine("  " + kv.Value.ToString().PadLeft(5) + "  " + kv.Key);
            Console.WriteLine();
            Console.WriteLine("Unexpected scene returns : " + returnedCount);
            Console.WriteLine("Unexpected crashes       : " + crashCount);
            Console.WriteLine();

            if (detail.Count > 0) {
                Console.WriteLine("=== Detail (ReadRoot completed) ===");
                foreach (string d in detail) Console.WriteLine(d);
            }

            // Non-zero exit only for crashes / unexpected success, so the harness can gate CI.
            return (crashCount == 0 && returnedCount == 0) ? 0 : 1;
        }

        // Reads a file and prints the S1 state in detail: the cached toplevel node names and
        // every element (type / name / id / typed id / props count).
        static int DumpMode(string root, List<string> files)
        {
            if (files.Count == 0) {
                Console.WriteLine("usage: --dump <relpath> [relpath ...]");
                return 2;
            }

            foreach (string rel in files) {
                string path = Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel);
                Console.WriteLine("=== " + rel + " ===");

                UfbxiContext uc = new UfbxiContext();
                uc.Error = new UfbxError();
                uc.Stream = new UfbxiStream();
                uc.DeferredLoad = true;
                uc.LoadFilename = path;
                uc.LoadFilenameLen = -1;

                Outcome o = new Outcome();
                try {
                    RunInto(uc, path, o);
                } catch (Exception e) {
                    Console.WriteLine("crash: " + e);
                    continue;
                }
                Console.WriteLine("outcome=" + o.Kind + (o.Reader != null ? " reader=" + o.Reader : ""));

                Console.WriteLine("top_nodes (" + uc.TopNodes.Count + "):");
                foreach (UfbxiNode n in uc.TopNodes) {
                    Console.WriteLine("  " + n.Name + " children=" + n.NumChildren);
                }

                // Records mirror tools/graph_oracle.c `dump_graph` byte-for-byte so the two dumps can
                // be diffed line-by-line. `E` = `E <id> <type> <typed_id> <fbx_id>` + emit_name +
                // ` <num_props>`; `P` = `P <elem_id> <ix>` + emit_name + ` <type> <flags:x> <key:x>`
                // + int + reals + emit_payload(str) + emit_payload(blob); `C` = `C <ix> <src> <dst>`
                // + emit_name(src_prop) + emit_name(dst_prop).
                Console.WriteLine("E-count " + uc.TmpElementPtrs.Count);
                for (int i = 0; i < uc.TmpElementPtrs.Count; i++) {
                    UfbxElement el = uc.TmpElementPtrs[i];
                    UfbxProp[] ps = el.Props != null ? el.Props.Props : null;
                    int pc = ps != null ? ps.Length : 0;
                    StringBuilder e = new StringBuilder("E ");
                    e.Append(el.ElementId).Append(' ').Append((int)el.Type).Append(' ').Append(el.TypedId)
                        .Append(' ').Append(uc.TmpElementFbxIds[i]);
                    AppendName(e, el.Name);
                    e.Append(' ').Append(pc);
                    Console.WriteLine(e.ToString());

                    for (int k = 0; k < pc; k++) {
                        UfbxProp p = ps[k];
                        StringBuilder b = new StringBuilder("P ");
                        b.Append(el.ElementId).Append(' ').Append(k);
                        AppendName(b, p.Name);
                        b.Append(' ').Append((int)p.Type).Append(' ').Append(((uint)p.Flags).ToString("x"))
                            .Append(' ').Append(p.InternalKey.ToString("x"));
                        if (((uint)p.Flags & (uint)UfbxPropFlags.ValueInt) != 0)
                            b.Append(' ').Append(p.ValueInt);
                        else
                            b.Append(" -");
                        int nreal = 0;
                        if (((uint)p.Flags & (uint)UfbxPropFlags.ValueReal) != 0) nreal = 1;
                        else if (((uint)p.Flags & (uint)UfbxPropFlags.ValueVec2) != 0) nreal = 2;
                        else if (((uint)p.Flags & (uint)UfbxPropFlags.ValueVec3) != 0) nreal = 3;
                        else if (((uint)p.Flags & (uint)UfbxPropFlags.ValueVec4) != 0) nreal = 4;
                        if (nreal > 0) {
                            b.Append(' ');
                            for (int r = 0; r < nreal; r++) b.Append(RealBits(p, r).ToString("x16"));
                        } else {
                            b.Append(" -");
                        }
                        if (((uint)p.Flags & (uint)UfbxPropFlags.ValueStr) != 0)
                            AppendPayload(b, p.ValueStr);
                        else
                            b.Append(" -");
                        if (((uint)p.Flags & (uint)UfbxPropFlags.ValueBlob) != 0)
                            AppendPayloadBlob(b, p.ValueBlob);
                        else
                            b.Append(" -");
                        Console.WriteLine(b.ToString());
                    }
                }

                Console.WriteLine("C-count " + uc.TmpConnections.Count);
                for (int i = 0; i < uc.TmpConnections.Count; i++) {
                    UfbxiTmpConnection c = uc.TmpConnections[i];
                    StringBuilder b = new StringBuilder("C ");
                    b.Append(i).Append(' ').Append(c.Src).Append(' ').Append(c.Dst);
                    AppendName(b, c.SrcProp);
                    AppendName(b, c.DstProp);
                    Console.WriteLine(b.ToString());
                }

                Console.WriteLine("root_id=0x" + uc.RootId.ToString("x16") + " templates="
                    + (uc.Templates != null ? uc.Templates.Length : 0));
                Console.WriteLine();
            }
            return 0;
        }

        // Shared by Run and DumpMode: drives the loader, fills the outcome kind.
        static void RunInto(UfbxiContext uc, string path, Outcome o)
        {
            try {
                UfbxiLoad.Load(uc, new UfbxLoadOpts(), new UfbxError());
                o.Kind = "returned";
            } catch (UfbxiReaderNotPortedException e) {
                if (e.Reader == "ufbxi_pre/finalize_scene") {
                    o.Kind = "read-root-ok";
                } else {
                    o.Kind = "seam";
                    o.Reader = e.Reader;
                }
            } catch (UfbxParseError e) {
                o.Kind = "parse-error";
                o.ErrorType = e.ErrorType.ToString();
                o.ErrorDescription = e.HasDescription ? e.Message : null;
            } catch (Exception e) {
                o.Kind = "crash";
                o.Crash = e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
            }
        }

        // Port strings are Latin1 (1 char == 1 byte), matching the C `char*` payloads.
        static byte[] StrToBytes(string s)
        {
            if (s == null) return null;
            byte[] b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
            return b;
        }

        // C: fnv64() in tools/graph_oracle.c:70.
        static ulong Fnv64(byte[] b)
        {
            ulong h = 0xcbf29ce484222325ul;
            if (b != null) for (int i = 0; i < b.Length; i++) h = (h ^ b[i]) * 0x00000100000001b3ul;
            return h;
        }

        // C: emit_name() -- ` <len> <digest> <hex-or--> <is_static>`.
        static void AppendName(StringBuilder sb, string name)
        {
            byte[] b = StrToBytes(name);
            int len = b != null ? b.Length : 0;
            sb.Append(' ').Append(len).Append(' ').Append(Fnv64(b).ToString("x16"));
            AppendHexToken(sb, b);
            sb.Append(' ').Append(UfbxiPtrIdTable.IsStatic(name) ? 1 : 0);
        }

        // C: emit_payload() for `value_str` -- ` <len> <digest> <hex-or-->`.
        static void AppendPayload(StringBuilder sb, string s)
        {
            byte[] b = StrToBytes(s);
            sb.Append(' ').Append(b != null ? b.Length : 0).Append(' ').Append(Fnv64(b).ToString("x16"));
            AppendHexToken(sb, b);
        }

        // C: emit_payload() for `value_blob`.
        static void AppendPayloadBlob(StringBuilder sb, byte[] b)
        {
            sb.Append(' ').Append(b != null ? b.Length : 0).Append(' ').Append(Fnv64(b).ToString("x16"));
            AppendHexToken(sb, b);
        }

        static void AppendHexToken(StringBuilder sb, byte[] b)
        {
            if (b != null && b.Length > 0 && b.Length <= 64) {
                sb.Append(' ');
                for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            } else {
                sb.Append(" -");
            }
        }

        // C: the oracle concatenates `p->value_real_arr[k]` bit patterns into one token.
        static ulong RealBits(UfbxProp p, int k)
        {
            double v;
            switch (k) {
            case 0: v = p.ValueReal0; break;
            case 1: v = p.ValueReal1; break;
            case 2: v = p.ValueReal2; break;
            default: v = p.ValueReal3; break;
            }
            return (ulong)BitConverter.DoubleToInt64Bits(v);
        }

        static void Bump(SortedDictionary<string, int> map, string key)
        {
            int v;
            map.TryGetValue(key, out v);
            map[key] = v + 1;
        }

        static int Sum(SortedDictionary<string, int> map)
        {
            int s = 0;
            foreach (var kv in map) s += kv.Value;
            return s;
        }

        static Outcome Run(string path)
        {
            Outcome o = new Outcome();
            UfbxiContext uc = new UfbxiContext();
            uc.Error = new UfbxError();
            uc.Stream = new UfbxiStream();
            uc.DeferredLoad = true;
            uc.LoadFilename = path;
            uc.LoadFilenameLen = -1;   // C: SIZE_MAX == "nul-terminated"

            RunInto(uc, path, o);

            o.RootId = uc.RootId;
            o.NumElements = uc.NumElements;
            o.ElementPtrs = uc.TmpElementPtrs.Count;
            o.ElementFbxIds = uc.TmpElementFbxIds.Count;
            o.NodeIds = uc.TmpNodeIds.Count;
            o.Connections = uc.TmpConnections.Count;
            o.Templates = uc.Templates != null ? uc.Templates.Length : 0;
            o.TopNodes = uc.TopNodes.Count;
            o.FromAscii = uc.FromAscii;
            o.Version = uc.Version;

            long props = 0;
            for (int i = 0; i < uc.TmpElementPtrs.Count; i++) {
                UfbxProps p = uc.TmpElementPtrs[i].Props;
                if (p != null && p.Props != null) props += p.Props.Length;
            }
            o.Properties = props;

            return o;
        }

        // `tools/S1SelfCheck/bin/...` -> repo root is four levels up from the base dir.
        static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++) {
                if (File.Exists(Path.Combine(dir, "ufbx-cs.sln"))) return dir;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            return Directory.GetCurrentDirectory();
        }
    }
}
