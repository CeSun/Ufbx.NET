// GraphCheck: bit-exactness harness for the ported S1 pipeline (the element / property /
// connection graph built by `ufbxi_read_root()`, ufbx.c:15847).
//
// It replays `tools/graph_oracle.exe` (output `tools/graph_oracle.txt`, path list
// `tools/graph_corpus.txt`) against the port. The port half is still PENDING (the S1 readers
// land on a separate branch), so the port is judged **against the oracle outcome**:
//
//   port throws UfbxiReaderNotPortedException               -> SKIP (counted, reported per reader)
//   port throws a parse error where the oracle also fails    -> EXPECTED REJECT (both refuse it)
//   port throws a parse error where the oracle succeeded     -> FAIL (port rejects what C accepts)
//   port returns a scene where the oracle failed            -> FAIL (port accepts what C rejects)
//   port returns a scene where the oracle succeeded         -> strict compare (seam still unported)
//
// The `G` record carries the oracle outcome (`ok`, and the public error contract on failure), so
// `RunPort()` reads it: a corpus file that C itself rejects is *supposed* to make the port throw.
// Every `EXPECTED REJECT` is printed, and any non-`NotPorted` throw the oracle does not back is a
// real FAIL. A returned scene would mean the readers are done; the strict element/property/
// connection comparison is only wired once that happens. The
// discriminating work of this harness in PENDING mode is the **oracle structural self-check**:
// a set of invariants the C record stream must satisfy no matter what, which is what turns the
// three mutation experiments (see COORDINATION.md "交接" item 9) into graph-level failures
// instead of vacuous PASSes.
//
// Structural invariants checked on tools/graph_oracle.txt (violations => GRAPH CHECK FAIL):
//   * every record has the exact token shape for its type (`G` 15, `E` 11, `C` 13, `P` fixed
//     11 + flag-dependent value tokens);
//   * every payload/name prints `len digest hex` (or `len digest -`), the digest is 16 lowercase
//     hex, the hex token is lowercase, its byte length matches `len`, and it *re-hashes* to the
//     printed digest (catches a hex emitter that drifts from its own hash);
//   * `E` records are exactly the element ids `0..num_elements-1` in push order (C assigns
//     `element_id = uc->num_elements++` in `ufbxi_push_element_size`, ufbx.c:12353);
//   * `E.typed_id` is the per-type push counter, so it must run `0,1,2,...` within each type;
//   * `E.num_props` equals the number of `P` records for that element, and `P.ix` runs
//     `0..num_props-1`;
//   * each element's property names are unique (C sorts + `ufbxi_deduplicate_properties`,
//     ufbx.c:11881/11925) -- catches "props dedup disabled";
//   * property value tokens agree with `ufbx_prop.flags` (INT => `value_int`, REAL/VECn =>
//     `n` packed 16-hex doubles, STR/BLOB => the payload), and no prop carries two numeric
//     value flags;
//   * every connection endpoint (`src`/`dst`) is the `fbx_id` of some `E` record (C stores raw
//     fbx ids in `ufbxi_tmp_connection`, ufbx.c:6300) -- catches "fbx_id column printed as
//     element_id";
//   * every name marked `is_static` decodes to an entry of `ufbxi_strings[]` (loaded from
//     tools/ufbx_strings.tsv); catches a static name whose bytes were altered (case change);
//   * `G.num_elements`/`G.num_connections` equal the `E`/`C` record counts; legacy `G` records
//     have no graph records.
//
// Usage: dotnet run --project tools/GraphCheck -c Release -- [tools/graph_oracle.txt] [ufbx root]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx;

namespace GraphCheck
{
    static class Program
    {
        // Must match tools/graph_oracle.c
        const ulong FnvBasis = 0xcbf29ce484222325ul;
        const ulong FnvPrime = 0x00000100000001b3ul;
        const int InlineLimit = 64;

        // C: ufbx_prop_flags (ufbx.h:530-536)
        const uint FlagValueReal = 0x100000;
        const uint FlagValueVec2 = 0x200000;
        const uint FlagValueVec3 = 0x400000;
        const uint FlagValueVec4 = 0x800000;
        const uint FlagValueInt  = 0x1000000;
        const uint FlagValueStr  = 0x2000000;
        const uint FlagValueBlob = 0x4000000;

        const int MaxReported = 12;

        sealed class GRec
        {
            public int fi;
            public string status;       // "0" | "1" | "legacy"
            public uint version;
            public int ascii;
            public long numElements;
            public long numConnections;
            // For legacy records: the C error type from the trailing contract (0 = C accepted).
            // `dump_graph` emits no E/P/C records for legacy files, so this is all we can compare.
            public int legacyErrorType;
            public string line;
        }

        sealed class ERec
        {
            public int fi;
            public uint elemId;
            public int type;
            public uint typedId;
            public ulong fbxId;
            public int nameLen;
            public ulong nameDigest;
            public byte[] name;
            public bool nameStatic;
            public long numProps;
            public string line;
        }

        sealed class PRec
        {
            public int ix;
            public int nameLen;
            public ulong nameDigest;
            public byte[] name;
            public bool nameStatic;
            public int type;
            public uint flags;
            public uint key;
            public bool hasInt;
            public long valueInt;
            public int numReals;
            public string realsToken;
            public bool hasStr;
            public bool hasBlob;
            public string line;
        }

        sealed class FileGraph
        {
            public GRec g;
            public readonly List<ERec> elements = new List<ERec>();
            public readonly List<List<PRec>> props = new List<List<PRec>>();
            public readonly List<PRec> flatProps = new List<PRec>();
            public readonly List<string[]> connections = new List<string[]>();
            public readonly List<string> connectionLines = new List<string>();
        }

        static int numViolations, numPrintedViolations;
        static readonly List<string> violationKinds = new List<string>();

        static int numFiles, numSkip, numPortFail, numExpectedReject, numSeamReached, numSceneReturned, numAfterHorizon;
        static int numLegacy, numLegacyAgree, numLegacyNotPorted;
        static readonly List<string> seamFiles = new List<string>();
        static readonly Dictionary<string, int> skipByReader = new Dictionary<string, int>();

        static readonly HashSet<string> knownConstants = new HashSet<string>();
        static long endpointTotal, endpointResolved;

        static int Main(string[] args)
        {
            string oraclePath = args.Length > 0 ? args[0] : FindOracle();
            string oracleDir = Path.GetDirectoryName(Path.GetFullPath(oraclePath));
            string ufbxRoot = Path.GetFullPath(args.Length > 1 ? args[1] : FindUfbxRoot(oracleDir));
            string corpusPath = Path.Combine(oracleDir, "graph_corpus.txt");

            if (!File.Exists(oraclePath)) { Console.WriteLine("missing oracle output: " + oraclePath); return 1; }
            if (!File.Exists(corpusPath)) { Console.WriteLine("missing corpus list: " + corpusPath); return 1; }

            List<string> files = ReadCorpus(corpusPath);
            Dictionary<int, FileGraph> graphs = ReadOracleRecords(oraclePath);

            Console.WriteLine("known constants: " + knownConstants.Count);
            Console.WriteLine("corpus files:  " + files.Count);
            Console.WriteLine("oracle files:  " + graphs.Count);

            // ---- oracle structural self-check ----
            foreach (KeyValuePair<int, FileGraph> kv in graphs) CheckGraph(kv.Key, kv.Value);
            CheckCorpusCoverage(files, graphs);

            // ---- port half (PENDING: read_root is not ported yet) ----
            Directory.SetCurrentDirectory(ufbxRoot);
            for (int fi = 0; fi < files.Count; fi++) {
                FileGraph g;
                graphs.TryGetValue(fi, out g);
                RunPort(fi, files[fi], g);
            }

            Console.WriteLine();
            Console.WriteLine("oracle structural violations:  " + numViolations);
            Console.WriteLine("  C endpoints resolved:        " + endpointResolved + " / " + endpointTotal);
            Console.WriteLine("port files checked:            " + numFiles);
            Console.WriteLine("  SKIP (seam not ported):      " + numSkip);
            Console.WriteLine("      reached the pre_finalize_scene seam: " + numSeamReached);
            Console.WriteLine("  SCENE RETURNED (S3 complete, strict compare pending): " + numSceneReturned);
            Console.WriteLine("  AFTER-HORIZON REJECT (needs full-load oracle):         " + numAfterHorizon);
            foreach (string sf in seamFiles) Console.WriteLine("          " + sf);
            foreach (KeyValuePair<string, int> kv in skipByReader) {
                Console.WriteLine("      stopped at " + kv.Key + ": " + kv.Value);
            }
            Console.WriteLine("  EXPECTED REJECT (G ok=0):    " + numExpectedReject);
            Console.WriteLine("  LEGACY (outcome-only):       " + numLegacy + " (agree " + numLegacyAgree
                + ", not-ported " + numLegacyNotPorted + ")");
            Console.WriteLine("  FAIL (unexpected):           " + numPortFail);
            // Files the port neither skipped nor contradicted the oracle on -- i.e. those it
            // returned a scene for and strict-compared. Zero until the seam is ported.
            Console.WriteLine("  PASS (strict compared):      " + (numFiles - numSkip - numPortFail - numExpectedReject
                - (numLegacy - numLegacyNotPorted)));
            bool pass = numViolations == 0 && numPortFail == 0;
            Console.WriteLine(pass ? "GRAPH CHECK PASS" : "GRAPH CHECK FAIL");
            return pass ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // Oracle parsing
        // ------------------------------------------------------------------

        static Dictionary<int, FileGraph> ReadOracleRecords(string oraclePath)
        {
            Dictionary<int, FileGraph> graphs = new Dictionary<int, FileGraph>();
            FileGraph cur = null;
            int lineNo = 0;
            foreach (string raw in File.ReadLines(oraclePath)) {
                lineNo++;
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] f = line.Split(' ');
                int fi;
                if (f[0] == "K") {
                    if (f.Length != 4) { Violation("K token count " + f.Length + " (want 4) at line " + lineNo); continue; }
                    if (f[3] != "-") knownConstants.Add(f[3]);
                    continue;
                }
                if (f[0] == "G") {
                    if (f.Length != 15) { Violation("G token count " + f.Length + " (want 15) at line " + lineNo); continue; }
                    fi = ParseInt(f[1]);
                    cur = new FileGraph();
                    cur.g = new GRec {
                        fi = fi, status = f[2],
                        version = uint.Parse(f[3], CultureInfo.InvariantCulture),
                        ascii = ParseInt(f[4]),
                        numElements = ParseLong(f[5]),
                        numConnections = ParseLong(f[6]),
                        legacyErrorType = f[2] == "legacy" ? ParseInt(f[7]) : 0,
                        line = line,
                    };
                    graphs[fi] = cur;
                } else if (f[0] == "E") {
                    if (cur == null) { Violation("E before G at line " + lineNo); continue; }
                    if (f.Length != 11) { Violation("E token count " + f.Length + " (want 11) at line " + lineNo); continue; }
                    fi = ParseInt(f[1]);
                    if (fi != cur.g.fi) { Violation("E fi " + fi + " != current G " + cur.g.fi + " at line " + lineNo); continue; }
                    int nameLen; ulong nameDig; string nameHex; bool nameStatic; byte[] nameBytes;
                    ParseName(f, 6, "E.name", out nameLen, out nameDig, out nameHex, out nameStatic, out nameBytes);
                    ERec e = new ERec {
                        fi = fi, elemId = uint.Parse(f[2], CultureInfo.InvariantCulture),
                        type = ParseInt(f[3]), typedId = uint.Parse(f[4], CultureInfo.InvariantCulture),
                        fbxId = ulong.Parse(f[5], CultureInfo.InvariantCulture),
                        nameLen = nameLen, nameDigest = nameDig, name = nameBytes, nameStatic = nameStatic,
                        numProps = ParseLong(f[10]), line = line,
                    };
                    cur.elements.Add(e);
                    cur.props.Add(new List<PRec>());
                } else if (f[0] == "P") {
                    if (cur == null) { Violation("P before G at line " + lineNo); continue; }
                    ParseProp(cur, f, lineNo, line);
                } else if (f[0] == "C") {
                    if (cur == null) { Violation("C before G at line " + lineNo); continue; }
                    if (f.Length != 13) { Violation("C token count " + f.Length + " (want 13) at line " + lineNo); continue; }
                    cur.connections.Add(f);
                    cur.connectionLines.Add(line);
                } else {
                    Violation("unknown record type '" + f[0] + "' at line " + lineNo);
                }
            }
            return graphs;
        }

        static void ParseProp(FileGraph g, string[] f, int lineNo, string line)
        {
            // P fi elem_id ix <name(4)> type flags key <int> <reals> [str] [blob]
            int at = 1;
            int fi = ParseInt(f[at++]);
            uint elemId = uint.Parse(f[at++], CultureInfo.InvariantCulture);
            int ix = ParseInt(f[at++]);
            int nameLen; ulong nameDig; string nameHex; bool nameStatic; byte[] nameBytes;
            ParseName(f, at, "P.name", out nameLen, out nameDig, out nameHex, out nameStatic, out nameBytes);
            at += 4;
            if (f.Length < at + 3) { Violation("P too short at line " + lineNo); return; }
            int type = ParseInt(f[at++]);
            uint flags = ParseHex32(f[at++]);
            uint key = ParseHex32(f[at++]);

            PRec p = new PRec {
                ix = ix, nameLen = nameLen, nameDigest = nameDig, name = nameBytes, nameStatic = nameStatic,
                type = type, flags = flags, key = key, line = line,
            };

            // int
            if (at >= f.Length) { Violation("P missing int token at line " + lineNo); return; }
            string intTok = f[at++];
            p.hasInt = (flags & FlagValueInt) != 0;
            if (p.hasInt) {
                if (intTok == "-") Violation("P has INT flag but '-' int at line " + lineNo);
                else p.valueInt = ParseLong(intTok);
            } else if (intTok != "-") {
                Violation("P has no INT flag but int token '" + intTok + "' at line " + lineNo);
            }

            // reals
            if (at >= f.Length) { Violation("P missing reals token at line " + lineNo); return; }
            string realsTok = f[at++];
            int nreal = 0;
            if ((flags & FlagValueReal) != 0) nreal = 1;
            else if ((flags & FlagValueVec2) != 0) nreal = 2;
            else if ((flags & FlagValueVec3) != 0) nreal = 3;
            else if ((flags & FlagValueVec4) != 0) nreal = 4;
            p.numReals = nreal;
            p.realsToken = realsTok;
            if (nreal > 0) {
                if (realsTok == "-") Violation("P has vector flag but '-' reals at line " + lineNo);
                else if (realsTok.Length != 16 * nreal) Violation("P reals token length " + realsTok.Length + " (want " + 16 * nreal + ") at line " + lineNo);
                else if (!IsLowerHex(realsTok)) Violation("P reals token not lowercase hex at line " + lineNo);
            } else if (realsTok != "-") {
                Violation("P has no vector flag but reals token present at line " + lineNo);
            }

            // str
            p.hasStr = (flags & FlagValueStr) != 0;
            at = ParseOptionalPayload(f, at, p.hasStr, "P.str", lineNo);
            if (at < 0) return;

            // blob
            p.hasBlob = (flags & FlagValueBlob) != 0;
            at = ParseOptionalPayload(f, at, p.hasBlob, "P.blob", lineNo);
            if (at < 0) return;

            if (at != f.Length) Violation("P trailing tokens (" + (f.Length - at) + ") at line " + lineNo);

            // NOTE: a prop may carry two "numeric" value flags at once -- C sets VALUE_REAL for
            // numbers and VALUE_INT for integer types, so e.g. an integer prop has
            // `VALUE_REAL|VALUE_INT` (0x1100000) with both the int and the reals token populated.
            // The per-flag checks above are therefore independent, not exclusive.

            int idx = (int)elemId;
            if (idx < 0 || idx >= g.props.Count) { Violation("P elem_id " + elemId + " out of range at line " + lineNo); return; }
            g.props[idx].Add(p);
            g.flatProps.Add(p);
        }

        // Parse one value slot: 3 tokens when present, else exactly one '-'.
        static int ParseOptionalPayload(string[] f, int at, bool present, string ctx, int lineNo)
        {
            if (present) {
                if (at + 3 > f.Length) { Violation(ctx + " truncated at line " + lineNo); return -1; }
                CheckPayload(f[at], f[at + 1], f[at + 2], ctx + " at line " + lineNo);
                return at + 3;
            } else {
                if (at >= f.Length) { Violation(ctx + " missing '-' at line " + lineNo); return -1; }
                if (f[at] != "-") Violation(ctx + " expected '-' but got '" + f[at] + "' at line " + lineNo);
                return at + 1;
            }
        }

        // name = len digest hex-or-- static
        static void ParseName(string[] f, int at, string ctx, out int len, out ulong dig, out string hex, out bool isStatic, out byte[] bytes)
        {
            len = 0; dig = 0; hex = "-"; isStatic = false; bytes = null;
            if (at + 4 > f.Length) { Violation(ctx + " truncated"); return; }
            CheckPayload(f[at], f[at + 1], f[at + 2], ctx, out len, out dig, out hex, out bytes);
            string stat = f[at + 3];
            if (stat != "0" && stat != "1") Violation(ctx + " static flag '" + stat + "'");
            isStatic = stat == "1";
            if (isStatic && len > 0 && hex != "-" && knownConstants.Count > 0 && !knownConstants.Contains(hex)) {
                Violation(ctx + " static name not in ufbxi_strings[]: " + hex);
            }
        }

        static void CheckPayload(string lenTok, string digTok, string hexTok, string ctx)
        {
            int len; ulong dig; string hex; byte[] b;
            CheckPayload(lenTok, digTok, hexTok, ctx, out len, out dig, out hex, out b);
        }

        static void CheckPayload(string lenTok, string digTok, string hexTok, string ctx, out int len, out ulong dig, out string hex, out byte[] bytes)
        {
            len = 0; dig = 0; hex = "-"; bytes = null;
            if (!int.TryParse(lenTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out len)) {
                Violation(ctx + " bad len '" + lenTok + "'"); return;
            }
            if (digTok.Length != 16 || !IsLowerHex(digTok)) { Violation(ctx + " bad digest token '" + digTok + "'"); return; }
            dig = ulong.Parse(digTok, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            hex = hexTok;
            if (hexTok == "-") {
                if (!(len == 0 || len > InlineLimit)) Violation(ctx + " '-' hex with len " + len);
                return;
            }
            if (hexTok.Length != len * 2) { Violation(ctx + " hex length " + hexTok.Length + " != 2*" + len); return; }
            if (!IsLowerHex(hexTok)) { Violation(ctx + " hex not lowercase at " + ctx); return; }
            bytes = FromHex(hexTok);
            ulong got = Fnv64(bytes, 0, bytes.Length);
            if (got != dig) Violation(ctx + " digest mismatch: printed " + digTok + " recomputed " + got.ToString("x16", CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------
        // Structural checks
        // ------------------------------------------------------------------

        static void CheckGraph(int fi, FileGraph g)
        {
            if (g.g == null) { Violation("fi " + fi + ": no G record"); return; }
            bool legacy = g.g.status == "legacy";
            if (!legacy && g.g.status != "0" && g.g.status != "1") Violation("fi " + fi + ": bad G status '" + g.g.status + "'");

            if (legacy) {
                if (g.elements.Count != 0 || g.connections.Count != 0) Violation("fi " + fi + ": legacy G has graph records");
                return;
            }

            // E records: element ids 0..n-1 in push order; typed_id per-type counter.
            Dictionary<int, long> typedCount = new Dictionary<int, long>();
            for (int i = 0; i < g.elements.Count; i++) {
                ERec e = g.elements[i];
                if (e.elemId != (uint)i) Violation("fi " + fi + ": E[" + i + "].elem_id " + e.elemId + " (want " + i + ")");
                if (e.type < 0 || e.type >= 64) Violation("fi " + fi + ": E[" + i + "].type " + e.type + " out of range");
                long tc;
                typedCount.TryGetValue(e.type, out tc);
                if (e.typedId != (uint)tc) Violation("fi " + fi + ": E[" + i + "].typed_id " + e.typedId + " (want " + tc + " for type " + e.type + ")");
                typedCount[e.type] = tc + 1;

                List<PRec> ps = g.props[i];
                if (e.numProps != ps.Count) Violation("fi " + fi + ": E " + i + " num_props " + e.numProps + " != " + ps.Count + " P records");

                HashSet<string> names = new HashSet<string>();
                for (int k = 0; k < ps.Count; k++) {
                    PRec p = ps[k];
                    if (p.ix != k) Violation("fi " + fi + ": E " + i + " P.ix " + p.ix + " (want " + k + ")");
                    string key = p.nameLen + ":" + p.nameDigest.ToString("x16", CultureInfo.InvariantCulture);
                    if (!names.Add(key)) Violation("fi " + fi + ": E " + i + " duplicate prop name " + key);
                }
            }

            if (g.g.numElements != g.elements.Count) Violation("fi " + fi + ": G.num_elements " + g.g.numElements + " != " + g.elements.Count + " E records");
            if (g.g.numConnections != g.connections.Count) Violation("fi " + fi + ": G.num_connections " + g.g.numConnections + " != " + g.connections.Count + " C records");

            // Connections: `ufbxi_tmp_connection.src/dst` are raw fbx ids (ufbx.c:6300), so the
            // endpoints of a connection must name elements that exist. Dangling connections do
            // occur legitimately (a few exporters reference skipped objects; C drops them in
            // `ufbxi_resolve_connections`), so a single unresolved endpoint is not an error --
            // but a file whose connections resolve *nowhere* means the fbx_id column is wrong
            // (this is the "fbx_id printed as element_id" guard).
            //
            // NOTE: fbx_id is only comparable *within one oracle run*. For ASCII/legacy files C
            // derives it from the interned name pointer when that pointer is below
            // UFBXI_MAXIMUM_FAST_POINTER_ID = 0x4000000000000000 (`ufbxi_synthetic_id_from_string`,
            // ufbx.c:12246-12254), so the value is ASLR-dependent: two runs of the oracle produce
            // different fbx ids for the same file. The check below stays sound because both the `E`
            // and `C` records it compares come from the *same* run; any future port-side comparison
            // must compare fbx ids structurally (endpoint graph), never by raw value.
            HashSet<ulong> fbxIds = new HashSet<ulong>();
            for (int i = 0; i < g.elements.Count; i++) fbxIds.Add(g.elements[i].fbxId);
            long resolvedInFile = 0;
            for (int i = 0; i < g.connections.Count; i++) {
                string[] c = g.connections[i];
                if (c[2] != i.ToString(CultureInfo.InvariantCulture)) Violation("fi " + fi + ": C[" + i + "].ix " + c[2]);
                ulong src = ulong.Parse(c[3], CultureInfo.InvariantCulture);
                ulong dst = ulong.Parse(c[4], CultureInfo.InvariantCulture);
                endpointTotal += 2;
                if (fbxIds.Contains(src)) { endpointResolved++; resolvedInFile++; }
                if (fbxIds.Contains(dst)) { endpointResolved++; resolvedInFile++; }
                // connection prop names
                CheckConnectionName(fi, i, c, 5);
                CheckConnectionName(fi, i, c, 9);
            }
            if (g.connections.Count > 0 && resolvedInFile == 0) {
                Violation("fi " + fi + ": all " + g.connections.Count + " connections are dangling (fbx_id column wrong?)");
            }
        }

        static void CheckConnectionName(int fi, int ci, string[] c, int at)
        {
            int len; ulong dig; string hex; bool isStatic; byte[] bytes;
            ParseName(c, at, "C[" + ci + "].prop", out len, out dig, out hex, out isStatic, out bytes);
        }

        static void CheckCorpusCoverage(List<string> files, Dictionary<int, FileGraph> graphs)
        {
            if (files.Count != graphs.Count) Violation("corpus/oracle mismatch: " + files.Count + " paths, " + graphs.Count + " records");
            for (int i = 0; i < files.Count; i++) {
                if (!graphs.ContainsKey(i)) Violation("fi " + i + " (" + files[i] + "): no oracle record");
            }
        }

        // ------------------------------------------------------------------
        // Port side
        // ------------------------------------------------------------------

        static void RunPort(int fi, string path, FileGraph g)
        {
            numFiles++;
            // The oracle outcome decides what "correct" means for this file: a `G` record with
            // `ok=0` is a file C itself refuses, so the port is *supposed* to throw for it.
            bool oracleAccepted = g != null && g.g.status == "1";
            bool oracleRejected = g != null && g.g.status == "0";
            UfbxiContext uc = new UfbxiContext();
            uc.Error = new UfbxError();
            uc.Stream = new UfbxiStream();
            uc.DeferredLoad = true;
            uc.LoadFilename = path;
            uc.LoadFilenameLen = -1;   // C: SIZE_MAX

            // Legacy (version < 6000) files: `dump_graph` emits no E/P/C records for them
            // (see tools/graph_oracle.c), so only the success/failure outcome is comparable.
            // Object-level comparison of legacy files is out of scope until the oracle dumps
            // them; they are covered by LoadCheck's error contract and by the golden hashes.
            if (g != null && g.g.status == "legacy") {
                bool cRejected = g.g.legacyErrorType != 0;
                numLegacy++;
                try {
                    UfbxiLoad.Load(uc, new UfbxLoadOpts(), new UfbxError());
                    if (cRejected) {
                        numPortFail++;
                        Report(fi, path, "LEGACY: PORT ACCEPTS WHERE C REJECTS",
                            "legacy err type " + g.g.legacyErrorType, "port returned a scene");
                    } else {
                        numLegacyAgree++;
                    }
                } catch (UfbxiReaderNotPortedException e) {
                    numSkip++;
                    numLegacyNotPorted++;
                    int n; skipByReader.TryGetValue(e.Reader, out n);
                    skipByReader[e.Reader] = n + 1;
                } catch (UfbxParseError) {
                    if (cRejected) {
                        // Both sides refuse the file. C's contract for legacy files carries a
                        // raw ini error type that LoadCheck already compares byte-for-byte, so
                        // only the outcome (reject) is asserted here.
                        numLegacyAgree++;
                    } else {
                        numPortFail++;
                        Report(fi, path, "LEGACY: PORT REJECTS WHERE C ACCEPTS", "legacy err type 0", "-");
                    }
                } catch (Exception e) {
                    numPortFail++;
                    Report(fi, path, "LEGACY PORT CRASH", "-", e.GetType().Name + ": " + e.Message);
                }
                return;
            }

            try {
                UfbxiLoad.Load(uc, new UfbxLoadOpts(), new UfbxError());
                // S3b/S3c (2026-10-03): the load spine continues past `ufbxi_finalize_scene`,
                // so a returned scene is the expected outcome for every `G ok=1` file and is
                // counted separately -- the strict element/property/connection comparison
                // remains this harness's next milestone. A returned scene for an `ok=0` file
                // is still a contradiction (the port accepted what C rejects).
                if (oracleRejected) {
                    numPortFail++;
                    Report(fi, path, "PORT RETURNS A SCENE WHERE C REJECTS", "G ok=0", "-");
                } else {
                    numSceneReturned++;
                }
            } catch (UfbxiReaderNotPortedException e) {
                numSkip++;
                if (e.Reader != null && e.Reader.Contains("finalize_scene")) {
                    numSeamReached++;
                    seamFiles.Add(path);
                }
                int n; skipByReader.TryGetValue(e.Reader, out n);
                skipByReader[e.Reader] = n + 1;
            } catch (UfbxParseError e) {
                if (oracleRejected) {
                    // Both sides refuse the file (`G ok=0`), so the port is not diverging -- a
                    // file C rejects is expected to make the spine throw. Reported, not a FAIL.
                    numExpectedReject++;
                    Report(fi, path, "EXPECTED REJECT (C also rejects)", "G ok=0", e.Message);
                } else {
                    // NOTE (S3b/S3c, 2026-10-03): `G ok` covers `ufbxi_read_root()` only (the
                    // graph oracle stops before `ufbxi_pre_finalize_scene`). A parse error in
                    // the port may be exactly what full C does (e.g. "Cyclic node hierarchy"
                    // in ufbxi_linearize_nodes, verified against `ufbx_load_file`) or a real
                    // divergence -- the G record cannot tell, so this is counted separately.
                    numAfterHorizon++;
                    Report(fi, path, "PORT FAILS AFTER ORACLE HORIZON (G ok covers read_root only)",
                        oracleAccepted ? "G ok=1" : "-", e.Message);
                }
            } catch (Exception e) {
                numPortFail++;
                Report(fi, path, "PORT CRASH", "-", e.GetType().Name + ": " + e.Message);
            }
        }

        static void Report(int fi, string path, string kind, string want, string got)
        {
            if (numReported >= MaxReported) return;
            numReported++;
            Console.WriteLine(kind + " " + fi + " " + path);
            Console.WriteLine("  C:    " + want);
            Console.WriteLine("  port: " + got);
        }
        static int numReported;

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        static void Violation(string msg)
        {
            numViolations++;
            if (numPrintedViolations < 40) {
                numPrintedViolations++;
                Console.WriteLine("VIOLATION " + msg);
            } else if (numPrintedViolations == 40) {
                numPrintedViolations++;
                Console.WriteLine("... more violations suppressed");
            }
        }

        static List<string> ReadCorpus(string path)
        {
            List<string> files = new List<string>();
            foreach (string line in File.ReadLines(path)) {
                if (line.Length == 0) continue;
                files.Add(line.TrimEnd('\r'));
            }
            return files;
        }

        static string FindOracle()
        {
            string p = Path.GetFullPath("tools/graph_oracle.txt");
            if (File.Exists(p)) return p;
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "graph_oracle.txt"));
        }

        static string FindUfbxRoot(string oracleDir)
        {
            string local = Path.Combine(oracleDir, "..", "data");
            if (Directory.Exists(local)) return Path.GetFullPath(oracleDir + Path.DirectorySeparatorChar + "..");
            return @"C:\Workspace\_analyze_ufbx";
        }

        static bool IsLowerHex(string s)
        {
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        static byte[] FromHex(string s)
        {
            byte[] b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) {
                b[i] = (byte)((HexVal(s[i * 2]) << 4) | HexVal(s[i * 2 + 1]));
            }
            return b;
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            return c - 'a' + 10;
        }

        static string Hex(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static ulong Fnv64(byte[] data, int offset, int length)
        {
            ulong h = FnvBasis;
            if (data != null) {
                for (int i = 0; i < length; i++) h = (h ^ data[offset + i]) * FnvPrime;
            }
            return h;
        }

        static int ParseInt(string s) { return int.Parse(s, CultureInfo.InvariantCulture); }
        static long ParseLong(string s) { return long.Parse(s, CultureInfo.InvariantCulture); }
        static uint ParseHex32(string s) { return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture); }
    }
}
