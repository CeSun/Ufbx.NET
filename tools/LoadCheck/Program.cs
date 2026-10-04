// LoadCheck: bit-exactness harness for the ported load spine.
//
// It replays `tools/load_oracle.exe` (output `tools/load_oracle.txt`, path list
// `tools/load_corpus.txt` = every file under the ufbx checkout's `data/`, plus the synthetic
// edge cases in `tools/loadcheck_inputs/` and a few non-existent paths/directories that the
// corpus cannot contain: the file-open failure, the 0-byte "Empty file" case, a 22-byte binary
// magic that cannot fill the 27-byte header, and an ASCII file with only a version comment)
// against `UfbxApi.LoadFile()` and a direct `UfbxiLoad.Load()`, one record per file.
//
// The spine ends at the toplevel reader seam (`UfbxiToplevel.ReadRoot/ReadLegacyRoot/ObjLoad/
// MtlLoad` throws `UfbxiReaderNotPortedException`), so "success" cannot be compared directly: a
// file that C loads fully makes the port stop at the seam. The oracle therefore records the
// *context* state of the pre-seam code (`file_format`, `from_ascii`, `file_big_endian`,
// `sure_fbx`, `version`) plus the public `ufbx_error` contract, and this harness classifies each
// file into one of three comparable shapes:
//
//   C loaded the file        -> the port must reach the seam with the same pre-seam state.
//   C failed, port failed    -> both failed inside the spine: the error must be byte-identical.
//   C failed, port reached   -> allowed only if C's record *certifies* that C failed inside a
//                              toplevel reader, i.e. past the seam. The certificate is derived
//                              from where C writes those state fields:
//                                `metadata.file_format`  only at ufbx.c:11184 (end of
//                                    `ufbxi_determine_format()`, whose failure is pre-seam)
//                                `file_big_endian`       only at ufbx.c:11200 (begin_parse binary)
//                                `sure_fbx`              only at ufbx.c:11211/11228 (begin_parse)
//                                `version`               begin_parse at 11208/11230, and past the
//                                    seam at 9568/10455/11996; `from_ascii` at 11215 and 16863
//                              so `format == FBX && (sure_fbx || (ascii && version > 0))` proves
//                              `ufbxi_begin_parse()` returned and the failure is in `ufbxi_read_*`,
//                              and `format == OBJ|MTL` proves the dispatch reached `obj/mtl_load`.
//                              Everything else (notably `format == UNKNOWN`, and FBX with
//                              `version == 0 && !sure_fbx`, where `ufbxi_ascii_next_token()` failed
//                              inside `begin_parse`) is a pre-seam failure the port must reproduce.
//
// `format`/`big_endian`/`sure_fbx` are only ever written by pre-seam code, so they are compared on
// every record. `ascii`/`version` can also be written by the readers, so they are compared strictly
// only where both sides failed inside the spine; elsewhere a difference is reported as INFO.
//
// The same symmetry is checked in the other direction: if the port fails before the seam while C's
// record certifies that C got past it, the port is failing too early -- reported as a divergence
// when the errors differ, and counted as `seam underrun` (same error, earlier stop) when they match.
//
// Usage: dotnet run --project tools/LoadCheck -c Release -- [tools/load_oracle.txt] [ufbx root]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ufbx.NET;

namespace LoadCheck
{
    static class Program
    {
        // Must match tools/load_oracle.c
        const ulong FnvBasis = 0xcbf29ce484222325ul;
        const ulong FnvPrime = 0x00000100000001b3ul;
        const int MaxErrInline = 64;   // oracle emit_digest_and_hex()/emit_payload()

        // C: ufbx_file_format
        const int FormatUnknown = 0;
        const int FormatFbx = 1;
        const int FormatObj = 2;
        const int FormatMtl = 3;

        // C: `if (uc->version < 6000) ufbxi_read_legacy_root() else ufbxi_read_root()`
        // (ufbx.c:25303-25307).
        const uint LegacyVersionLimit = 6000;

        const int MaxReported = 12;

        // One oracle record, parsed positionally (see the grammar in tools/load_oracle.c).
        sealed class CRecord
        {
            public int fi;
            public bool ok;
            public int format;
            public bool ascii;
            public bool bigEndian;
            public bool sureFbx;
            public uint version;
            public string line;
        }

        // What the port did with one file.
        sealed class PortResult
        {
            public bool reachedSeam;
            // S3b/S3c (2026-10-03): the spine continues past `ufbxi_finalize_scene`, so
            // `UfbxiLoad.Load()` can now return a scene -- the new port-side counterpart of
            // "C loaded the file" (equivalent to `reachedSeam` in the record layout).
            public bool completedLoad;
            public string reader;
            public string crash;
            public UfbxiContext uc;
            public UfbxError error = new UfbxError();
            public string apiOutcome;
            public bool apiReachedSeam;
            public string apiCrash;
        }

        static int numDiff, numFiles, numFailed, numLoaded, numInfo, numUnderrun, numBothLoaded;
        static readonly Dictionary<string, int> pendingByReader = new Dictionary<string, int>();
        static readonly Dictionary<string, int> underrunByReader = new Dictionary<string, int>();

        static int Main(string[] args)
        {
            string oraclePath = args.Length > 0 ? args[0] : FindOracle();
            string oracleDir = Path.GetDirectoryName(Path.GetFullPath(oraclePath));
            string ufbxRoot = Path.GetFullPath(args.Length > 1 ? args[1] : FindUfbxRoot(oracleDir));
            string corpusPath = Path.Combine(oracleDir, "load_corpus.txt");

            if (!File.Exists(oraclePath)) {
                Console.WriteLine("missing oracle output: " + oraclePath);
                return 1;
            }
            if (!File.Exists(corpusPath)) {
                Console.WriteLine("missing corpus list: " + corpusPath);
                return 1;
            }

            List<string> files = ReadCorpus(corpusPath);
            Dictionary<int, CRecord> expected = ReadOracleRecords(oraclePath);
            if (expected.Count != files.Count) {
                Console.WriteLine("corpus/oracle mismatch: " + files.Count + " paths, " +
                    expected.Count + " records");
                return 1;
            }

            StringBuilder all = new StringBuilder();
            List<int> order = new List<int>(expected.Keys);
            order.Sort();

            // The oracle is run from the ufbx checkout root and hands C the corpus strings
            // verbatim, and `error.info` for a file-open failure *is* that string. Match both: same
            // working directory (so relative paths resolve the same way) and the exact same path
            // text, rather than a root-joined absolute path.
            Directory.SetCurrentDirectory(ufbxRoot);

            foreach (int fi in order) {
                CRecord c = expected[fi];
                string rel = files[fi];
                string path = Path.Combine(ufbxRoot, NativeRel(rel));
                // A missing input is only a problem where C managed to load it: the corpus has
                // deliberate non-existent paths (`ufbx_load_file()` on them is the file-open
                // failure path, and its `error.info` carries the path itself), so both sides have
                // to run on them.
                if (!File.Exists(path) && c.ok) {
                    Report(fi, rel, "MISSING INPUT (C loaded it)", c.line, "-");
                    numFailed++;
                    continue;
                }

                numFiles++;
                if (c.ok) numLoaded++;

                PortResult p = RunPort(rel);
                string got = EmitRecord(fi, p);
                all.Append(got).Append("\r\n");
                Compare(fi, rel, c, p, got);
            }

            File.WriteAllText(Path.Combine(oracleDir, "load_port.txt"), all.ToString());

            Console.WriteLine("files checked:                 " + numFiles);
            Console.WriteLine("C loaded (past the seam):      " + numLoaded);
            Console.WriteLine("port reached the seam:         " + numReachedSeam);
            Console.WriteLine("both sides loaded (S3 complete): " + numBothLoaded);
            Console.WriteLine("port failed inside the spine:  " + (numFiles - numReachedSeam - numBothLoaded));
            Console.WriteLine("  pre-seam error parity:       " + numParity);
            Console.WriteLine("  seam underrun, same error:   " + numUnderrun);
            foreach (KeyValuePair<string, int> kv in underrunByReader) {
                Console.WriteLine("      underrun at " + kv.Key + ": " + kv.Value);
            }
            Console.WriteLine("past-seam pending (unported):  " + numReaderPending);
            foreach (KeyValuePair<string, int> kv in pendingByReader) {
                Console.WriteLine("      stopped at " + kv.Key + ": " + kv.Value);
            }
            Console.WriteLine("past-seam state differences:   " + numInfo);
            foreach (KeyValuePair<string, int> kv in infoByColumn) {
                Console.WriteLine("      " + kv.Key + ": " + kv.Value);
            }
            Console.WriteLine("files with divergence:         " + numFailed);
            Console.WriteLine("divergent records:             " + numDiff);
            bool pass = numDiff == 0 && numFailed == 0;
            Console.WriteLine(pass ? "LOAD CHECK PASS" : "LOAD CHECK FAIL");
            return pass ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // Port side
        // ------------------------------------------------------------------

        // Two passes over the same file:
        //  - `UfbxApi.LoadFile()` gives the error a caller sees, i.e. after `ReportFailure()` has
        //    run `ufbxi_fix_error_type(&uc->error, "Failed to load", p_error)` and the
        //    "Unsupported version" rewrite (ufbx.c:25613-25630);
        //  - a context built the same way, driven through `UfbxiLoad.Load()` directly, exposes the
        //    state the spine leaves behind (which the API call cannot report, as it owns the
        //    context). The three lines of setup are `UfbxApi.LoadFile()`'s own (UfbxApi.cs), kept
        //    in sync deliberately rather than by reaching into the API's privates.
        static PortResult RunPort(string path)
        {
            PortResult p = new PortResult();

            try {
                UfbxScene scene = UfbxApi.LoadFile(path, new UfbxLoadOpts(), p.error);
                p.apiOutcome = scene != null ? "returned-scene" : "returned-null";
            } catch (UfbxiReaderNotPortedException) {
                p.apiOutcome = "seam";
                p.apiReachedSeam = true;
            } catch (Exception e) {
                p.apiOutcome = "crash";
                p.apiCrash = e.GetType().Name + ": " + e.Message;
            }

            UfbxiContext uc = new UfbxiContext();
            uc.Error = new UfbxError();
            uc.Stream = new UfbxiStream();
            uc.DeferredLoad = true;
            uc.LoadFilename = path;
            uc.LoadFilenameLen = -1;   // C: SIZE_MAX == "nul-terminated"
            try {
                UfbxiLoad.Load(uc, new UfbxLoadOpts(), new UfbxError());
                // S3b/S3c: the load now completes past `ufbxi_finalize_scene`.
                p.completedLoad = true;
            } catch (UfbxiReaderNotPortedException e) {
                p.reachedSeam = true;
                p.reader = e.Reader;
            } catch (UfbxParseError) {
                // C: `ufbxi_check`/`ufbxi_fail` inside the spine; `UfbxApi` turns it into the
                // error compared through Path A.
            } catch (Exception e) {
                p.crash = e.GetType().Name + ": " + e.Message;
            }
            p.uc = uc;
            return p;
        }

        // The port's record, in the oracle's column layout. `ok` is 1 when the spine ran to the
        // reader seam or (since S3b/S3c) all the way through the scene build: either way that is
        // the port-side counterpart of C loading the file.
        static string EmitRecord(int fi, PortResult p)
        {
            UfbxiContext uc = p.uc;
            bool loaded = p.reachedSeam || p.completedLoad;
            StringBuilder sb = new StringBuilder();
            sb.Append("L ").Append(fi)
                .Append(loaded ? " 1" : " 0")
                .Append(' ').Append((int)uc.Scene.Metadata.FileFormat)
                .Append(' ').Append(uc.FromAscii ? 1 : 0)
                .Append(' ').Append(uc.FileBigEndian ? 1 : 0)
                .Append(' ').Append(uc.SureFbx ? 1 : 0)
                .Append(' ').Append(uc.Version.ToString(CultureInfo.InvariantCulture));
            if (loaded) {
                sb.Append(" 0 0 0 0000000000000000 - 0 0000000000000000 -");
            } else {
                AppendErrorFields(sb, p.error);
            }
            return sb.ToString();
        }

        static void AppendErrorFields(StringBuilder sb, UfbxError error)
        {
            string desc = error.Description ?? "";
            byte[] descBytes = UfbxiRawStr.ToBytes(desc);
            int descLen = descBytes.Length;
            bool descInInfo = descBytes.Length > 0 && ReferenceEquals(error.Description, error.Info);

            sb.Append(' ').Append((int)error.Type)
                .Append(' ').Append(descLen)
                .Append(' ').Append(descInInfo ? 1 : 0)
                .Append(' ').Append(Fnv64(descBytes, 0, descLen).ToString("x16", CultureInfo.InvariantCulture));
            if (descLen > 0 && descLen <= MaxErrInline) sb.Append(' ').Append(Hex(descBytes, 0, descLen));
            else sb.Append(" -");

            byte[] infoBytes = UfbxiRawStr.ToBytes(error.Info ?? "");
            int infoLen = error.InfoLength;
            if (infoLen > infoBytes.Length) infoLen = infoBytes.Length;
            sb.Append(' ').Append(infoLen)
                .Append(' ').Append(Fnv64(infoBytes, 0, infoLen).ToString("x16", CultureInfo.InvariantCulture));
            if (infoLen > 0 && infoLen <= MaxErrInline) sb.Append(' ').Append(Hex(infoBytes, 0, infoLen));
            else sb.Append(" -");
        }

        // ------------------------------------------------------------------
        // Comparison
        // ------------------------------------------------------------------

        // Columns 3 (format), 5 (big_endian) and 6 (sure_fbx) are written only by pre-seam code,
        // so they are compared on every record; see the header for the write sites.
        static readonly int[] AlwaysStrict = { 3, 5, 6 };

        // Everything else: 2 (ok), 4 (ascii), 7 (version) and the error columns 8..15. Compared
        // when both sides failed inside the spine, where no reader has run on either side.
        static readonly int[] SpineStrict = { 2, 4, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        // Columns 4 (ascii) and 7 (version) can also be written by the toplevel readers
        // (ufbx.c:9568/10455/11996/16863), so a difference where one side ran past the seam is only
        // information.
        static readonly int[] SoftWhenPastSeam = { 4, 7 };

        static void Compare(int fi, string name, CRecord c, PortResult p, string got)
        {
            if (p.crash != null) {
                Report(fi, name, "PORT CRASH", c.line, p.crash);
                numDiff++;
                numFailed++;
                return;
            }
            if (p.apiOutcome == "crash") {
                Report(fi, name, "UfbxApi.LoadFile CRASH", c.line, p.apiCrash);
                numDiff++;
                numFailed++;
                return;
            }
            if (p.apiOutcome == "returned-scene") {
                // S3b/S3c: returning a scene is now the expected outcome whenever the direct
                // `UfbxiLoad.Load()` also completed; it is only a contradiction when C failed.
                if (!c.ok || !p.completedLoad) {
                    Report(fi, name, "PORT RETURNS A SCENE (the seam must throw)", c.line, got);
                    numDiff++;
                    numFailed++;
                    return;
                }
            }
            if (p.apiReachedSeam != p.reachedSeam && !p.completedLoad) {
                Report(fi, name, "API/DIRECT DISAGREE", c.line,
                    "UfbxApi.LoadFile seam=" + p.apiReachedSeam + ", direct UfbxiLoad.Load seam=" + p.reachedSeam);
                numDiff++;
                numFailed++;
                return;
            }

            string[] want = c.line.Split(' ');
            string[] have = got.Split(' ');

            for (int i = 0; i < AlwaysStrict.Length; i++) ColumnDiff(fi, name, c.line, want, have, AlwaysStrict[i]);

            bool cert = PastSeamCertificate(c);
            bool loaded = p.reachedSeam || p.completedLoad;
            if (p.reachedSeam) numReachedSeam++;

            if (c.ok) {
                if (!loaded) {
                    Report(fi, name, "PORT FAILS WHERE C LOADED", c.line, got);
                    numDiff++;
                    numFailed++;
                    return;
                }
                if (p.completedLoad) {
                    // S3b/S3c: both sides loaded the file end-to-end. The state columns were
                    // already compared above; the scene contents are golden's business.
                    numBothLoaded++;
                } else {
                    numReaderPending++;
                    Count(p.reader);
                }
                SoftColumns(fi, name, c.line, want, have);
                return;
            }

            if (loaded) {
                if (!cert) {
                    Report(fi, name, "PORT REACHES SEAM, C FAILED BEFORE IT", c.line, got);
                    numDiff++;
                    numFailed++;
                } else {
                    numReaderPending++;
                    Count(p.reader);
                    SoftColumns(fi, name, c.line, want, have);
                }
                return;
            }

            // Both failed inside the spine: byte-exact error contract and full state.
            int before = numDiff;
            for (int i = 0; i < SpineStrict.Length; i++) ColumnDiff(fi, name, c.line, want, have, SpineStrict[i]);
            if (numDiff == before) {
                if (cert) {
                    // C got past the seam while the port stopped earlier with the identical error:
                    // nothing a caller can observe, but it says the port is missing a check C has.
                    numUnderrun++;
                    Underrun(p.reader ?? "spine");
                } else {
                    numParity++;
                }
            } else {
                numFailed++;
            }
        }

        static void SoftColumns(int fi, string name, string wantLine, string[] want, string[] have)
        {
            for (int i = 0; i < SoftWhenPastSeam.Length; i++) {
                int col = SoftWhenPastSeam[i];
                if (want[col] == have[col]) continue;
                numInfo++;
                string key = ColumnName(col);
                int n;
                infoByColumn.TryGetValue(key, out n);
                infoByColumn[key] = n + 1;
                // A `from_ascii`/`version` difference is only expected where C ran past the seam;
                // the handful of version cases are listed by name so they can be checked against
                // the C source, the ascii ones (set by `ufbxi_obj_init()`, ufbx.c:16863) are not.
                if (col == 7 && n < MaxReported) {
                    Console.WriteLine("VERSION-DIVERGES " + fi + " " + name +
                        ": C " + want[col] + " (final), port " + have[col] + " (at the seam)");
                }
            }
        }

        static readonly Dictionary<string, int> infoByColumn = new Dictionary<string, int>();

        static void ColumnDiff(int fi, string name, string wantLine, string[] want, string[] have, int col)
        {
            string a = col < want.Length ? want[col] : "<short>";
            string b = col < have.Length ? have[col] : "<short>";
            if (a == b) return;
            numDiff++;
            Report(fi, name, "COLUMN " + col + " (" + ColumnName(col) + ")", wantLine, string.Join(" ", have));
        }

        static string ColumnName(int col)
        {
            switch (col) {
                case 2: return "ok/seam";
                case 3: return "format";
                case 4: return "ascii";
                case 5: return "big_endian";
                case 6: return "sure_fbx";
                case 7: return "version";
                case 8: return "error.type";
                case 9: return "error.description.length";
                case 10: return "error.description aliases info";
                case 11: return "error.description digest";
                case 12: return "error.description bytes";
                case 13: return "error.info_length";
                case 14: return "error.info digest";
                case 15: return "error.info bytes";
                default: return "column " + col;
            }
        }

        static void Report(int fi, string name, string kind, string wantLine, string got)
        {
            numReported++;
            if (numReported > MaxReported) {
                if (numReported == MaxReported + 1) Console.WriteLine("... more divergences suppressed");
                return;
            }
            Console.WriteLine(kind + " " + fi + " " + name);
            Console.WriteLine("  C:    " + wantLine);
            Console.WriteLine("  port: " + got);
        }

        static int numReported;
        static int numReachedSeam;
        static int numReaderPending;
        static int numParity;

        static void Count(string reader)
        {
            int n;
            pendingByReader.TryGetValue(reader, out n);
            pendingByReader[reader] = n + 1;
        }

        static void Underrun(string reader)
        {
            int n;
            underrunByReader.TryGetValue(reader, out n);
            underrunByReader[reader] = n + 1;
        }

        // Does C's record prove the failure happened inside a toplevel reader? See the header.
        static bool PastSeamCertificate(CRecord c)
        {
            switch (c.format) {
                case FormatUnknown:
                    // `ufbxi_determine_format()` failed at ufbx.c:11185, before its one write of
                    // `metadata.file_format`; everything up to it is spine.
                    return false;
                case FormatObj:
                case FormatMtl:
                    // `format` is set at ufbx.c:11184, the last thing before the dispatch at
                    // 25312-25318 calls `ufbxi_obj_load()`/`ufbxi_mtl_load()`.
                    return true;
                case FormatFbx:
                    if (c.sureFbx) return true;
                    // ASCII: `ufbxi_begin_parse()` defaults `version` to 7400 when it is still 0
                    // (ufbx.c:11230, non-strict) right before returning, so a non-zero version
                    // without `sure_fbx` still means `begin_parse()` completed and the failure is
                    // at ufbx.c:15899 in `ufbxi_read_root()` or later.
                    if (c.ascii && c.version > 0) return true;
                    // `version == 0 && !sure_fbx`: `ufbxi_ascii_next_token()` failed inside
                    // `ufbxi_begin_parse()` (ufbx.c:11223).
                    return false;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // Inputs
        // ------------------------------------------------------------------

        static string FindOracle()
        {
            string p = Path.GetFullPath("tools/load_oracle.txt");
            if (File.Exists(p)) return p;
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "load_oracle.txt"));
        }

        static string FindUfbxRoot(string oracleDir)
        {
            // tools/ lives in the port repo; the C corpus is in the sibling checkout.
            string local = Path.Combine(oracleDir, "..", "data");
            if (Directory.Exists(local)) return Path.GetFullPath(oracleDir + Path.DirectorySeparatorChar + "..");
            return @"C:\Workspace\_analyze_ufbx";
        }

        // Raw bytes, not `File.ReadLines()`: the port's `filename` is the raw-byte string model
        // (1 char == 1 byte, see UfbxiRawStr), i.e. exactly the UTF-8 `const char *` the oracle's
        // `ufbx_load_file()` receives. Decoding the list as text would turn
        // `data/synthetic_aβカ😂_7500_ascii.fbx` into code points 0x3B2/0x30AB/0x1F602, which
        // `UfbxiStreamOpen.PathToUtf16()` then reads as stray continuation bytes and rejects with
        // "Invalid UTF-8" -- a representation bug on this side, not a port divergence.
        static List<string> ReadCorpus(string path)
        {
            List<string> files = new List<string>();
            byte[] all = File.ReadAllBytes(path);
            int begin = 0;
            for (int i = 0; i <= all.Length; i++) {
                if (i < all.Length && all[i] != (byte)'\n') continue;
                int end = i;
                if (end > begin && all[end - 1] == (byte)'\r') end--;
                if (end > begin) files.Add(UfbxiRawStr.FromBytes(all, begin, end - begin));
                begin = i + 1;
            }
            return files;
        }

        // A raw-byte corpus path -> the UTF-16 path .NET's own file APIs want, via the port's own
        // lax UTF-8 decoder (the same decoder `UfbxiStreamOpen.Fopen()` uses to reach `_wfopen`).
        // Only the harness's `File.Exists()` diagnostic needs this: everything handed to the port
        // stays in the raw-byte form C sees.
        static string NativeRel(string rawRel)
        {
            string utf16 = UfbxiStreamOpen.PathToUtf16(rawRel, rawRel.Length);
            if (utf16 == null) return rawRel;
            return utf16.Replace('/', Path.DirectorySeparatorChar);
        }

        static Dictionary<int, CRecord> ReadOracleRecords(string oraclePath)
        {
            Dictionary<int, CRecord> records = new Dictionary<int, CRecord>();
            foreach (string line in File.ReadLines(oraclePath)) {
                if (line.Length == 0 || line[0] != 'L') continue;
                string[] f = line.Split(' ');
                if (f.Length != 16) {
                    Console.WriteLine("bad oracle record (" + f.Length + " columns): " + line);
                    continue;
                }
                CRecord r = new CRecord();
                r.line = line;
                r.fi = ParseInt(f[1]);
                r.ok = f[2] == "1";
                r.format = ParseInt(f[3]);
                r.ascii = f[4] == "1";
                r.bigEndian = f[5] == "1";
                r.sureFbx = f[6] == "1";
                r.version = uint.Parse(f[7], CultureInfo.InvariantCulture);
                records[r.fi] = r;
            }
            return records;
        }

        static int ParseInt(string s)
        {
            return int.Parse(s, CultureInfo.InvariantCulture);
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
    }
}
