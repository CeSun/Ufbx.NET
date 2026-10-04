// S4aCheck: isolated verification for the S4a geometry-cache / external-file / axes-unit /
// skinning-entry module.
//
// What it proves
// --------------
// The S4a module is not yet wired into `UfbxiToplevel.SceneBuild()` (that seam is owned by the
// orchestrator, see COORDINATION.md), so GraphCheck/LoadCheck/DomCheck only show it does not
// regress the load path. This harness drives the ported functions directly over an input/answer
// corpus produced by `tools/s4a_oracle.c` (which `#include`s the frozen ufbx.c and calls the
// original `ufbxi_*` / `ufbx_*` functions). Every record is an independent differential test of:
//
//   * ufbxi_load_geometry_cache -> cache_load_file (pc2 / mc / xml + xml frame files) and the
//     resulting channel/frame layout (LOAD / CH / FR records)
//   * ufbx_sample_geometry_cache_vec3 / ufbx_read_geometry_cache_real (SMP records)
//   * ufbxi_find_cubic_bezier_t (BZ records)
//   * ufbx_get_skin_vertex_matrix (SK records)
//   * ufbx_catch_get_skin_vertex_matrix with a live ufbx_panic (SKC records)
//   * ufbx_get_blend_shape_offset_index / _vertex_offset / _blend_vertex_offset /
//     add_blend_vertex_offsets (BSI / BSV / BVO / BAV records)
//   * ufbx_add_blend_shape_vertex_offsets (BSA records)
//
// Usage: dotnet run --project tools/S4aCheck -c Release -- [tools/s4a_oracle.txt]
//
// The oracle was produced from the working directory `tools/` so the recorded filenames are
// relative (`tools/s4a_inputs/...`); the harness runs from the repo root and therefore prepends
// nothing (the invoker runs it from the repo root, see the report).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ufbx;

namespace S4aCheck
{
    static class Program
    {
        static int fails;
        static int passes;
        static readonly List<string> failLines = new List<string>();

        static int Main(string[] args)
        {
            string oraclePath = args.Length > 0 ? args[0] : "tools/s4a_oracle.txt";
            if (!File.Exists(oraclePath)) {
                Console.Error.WriteLine("oracle not found: " + oraclePath);
                return 2;
            }

            // Cache records are grouped: the oracle emits all BZ/SK/BSI/... records first, then
            // one LOAD block per input path. Re-derive the input paths from the LOAD root field so
            // the harness loads exactly the same files (the oracle records the path it was given).
            var cachePaths = new List<string>();
            foreach (string raw in File.ReadLines(oraclePath)) {
                if (raw.StartsWith("LOAD ")) {
                    string[] t = raw.TrimEnd('\r', '\n').Split(' ');
                    // The root filename is the input path we passed (or "-" on failure).
                    string path = HexToStr(t[2]);
                    if (path != null) cachePaths.Add(path);
                } else {
                    break;
                }
            }

            int lineNo = 0;
            int cacheIx = -1;
            UfbxGeometryCache currentCache = null;

            foreach (string raw in File.ReadLines(oraclePath)) {
                lineNo++;
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0) continue;
                string[] t = line.Split(' ');
                string rec = t[0];
                bool ok;
                try {
                    switch (rec) {
                        case "LOAD": ok = DoLoad(t, ref cacheIx, ref currentCache); break;
                        case "CH": ok = DoCh(t, currentCache); break;
                        case "FR": ok = DoFr(t, currentCache); break;
                        case "SMP": ok = DoSmp(t, currentCache); break;
                        case "BZ": ok = ChkHex(t[4], UfbxiSceneOpts.FindCubicBezierT(D(t[1]), D(t[2]), D(t[3]))); break;
                        case "SK": ok = DoSk(t); break;
                        case "SKC": ok = DoSkc(t); break;
                        case "BSI": ok = ChkInt(t[3], (long)UfbxSkinApi.GetBlendShapeOffsetIndex(BlendShape(), int.Parse(t[2]))); break;
                        case "BSV": ok = DoBsv(t); break;
                        case "BVO": ok = DoBvo(t); break;
                        case "BAV": ok = DoBav(t); break;
                        case "BSA": ok = DoBsa(t); break;
                        case "RD": ok = DoRd(line, t); break;
                        case "RDO": ok = DoRdo(line, t); break;
                        case "DONE": ok = true; break;
                        default: Console.Error.WriteLine("unknown record " + rec + " at line " + lineNo); return 2;
                    }
                } catch (Exception e) {
                    Console.Error.WriteLine("EXCEPTION on line " + lineNo + " (" + rec + "): " + e);
                    return 2;
                }

                if (ok) {
                    passes++;
                } else {
                    fails++;
                    if (failLines.Count < 30) failLines.Add(lineNo + ": " + line);
                }
            }

            Console.WriteLine("S4A CHECK " + (fails == 0 ? "PASS" : "FAIL"));
            Console.WriteLine("records checked: " + (passes + fails));
            Console.WriteLine("pass:            " + passes);
            Console.WriteLine("fail:            " + fails);
            foreach (string f in failLines) Console.WriteLine("  DIVERGE " + f);
            return fails == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- helpers

        static double D(string hex) => BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber)));

        static bool ChkHex(string hex, double value)
        {
            ulong want = ulong.Parse(hex, NumberStyles.HexNumber);
            ulong got = (ulong)BitConverter.DoubleToInt64Bits(value);
            return want == got;
        }

        static bool ChkInt(string want, long got) => long.Parse(want) == got;

        // The oracle prints a raw byte buffer as lowercase hex, and a zero-length one as a bare `-`
        // (puts_hex() in tools/s4a_oracle.c:73-76), so the port's `UfbxPanic.Message` (one char per
        // byte, UfbxiRawStr.FromBytes) has to be re-encoded the same way to be comparable.
        static string ToHex(string raw)
        {
            if (raw == null || raw.Length == 0) return "-";
            const string digits = "0123456789abcdef";
            char[] chars = new char[raw.Length * 2];
            for (int i = 0; i < raw.Length; i++) {
                byte b = (byte)raw[i];
                chars[i * 2 + 0] = digits[b >> 4];
                chars[i * 2 + 1] = digits[b & 0xF];
            }
            return new string(chars);
        }

        static bool ChkMsg(string hex, string raw) => ToHex(raw) == hex;

        // The oracle prints an empty C `ufbx_string` (data = ufbxi_empty_char, length = 0) as a
        // bare `-`, so `-` decodes to the empty string, not to null. See puts_hex() in
        // tools/s4a_oracle.c: a zero-length string is indistinguishable from ufbx_empty_string,
        // which is what ufbxi_cache_setup_channels assigns (ufbx.c:24595).
        static string HexToStr(string hex)
        {
            if (hex == "-") return string.Empty;
            int n = hex.Length / 2;
            char[] chars = new char[n];
            for (int i = 0; i < n; i++) chars[i] = (char)byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
            return new string(chars);
        }

        // ---------------------------------------------------------------- cache records

        static bool DoLoad(string[] t, ref int cacheIx, ref UfbxGeometryCache cache)
        {
            cacheIx++;
            frCursor.Clear(); // frame records restart per LOAD block
            if (t[1] == "0") { cache = null; return true; } // oracle load failed: nothing to compare

            string path = HexToStr(t[2]);
            string localPath = Local(path);
            var opts = UfbxGeometryCacheOpts.CreateDefault();
            // Through the public façade (`UfbxGeometryCacheApi`) so the forwarders themselves are
            // covered by this differential, not just the internal bodies.
            cache = UfbxGeometryCacheApi.LoadGeometryCache(localPath, opts, null);
            if (cache == null) return false;
            if (cache.RootFilename != path) return false;
            if (cache.Channels.Length != int.Parse(t[3])) return false;
            if (cache.Frames.Length != int.Parse(t[4])) return false;
            if ((cache.ExtraInfo != null ? cache.ExtraInfo.Length : 0) != int.Parse(t[5])) return false;
            return true;
        }

        static bool DoCh(string[] t, UfbxGeometryCache cache)
        {
            int ix = int.Parse(t[1]);
            if (cache == null || ix >= cache.Channels.Length) return false;
            UfbxCacheChannel ch = cache.Channels[ix];
            if (ch.Name != HexToStr(t[2])) return false;
            if ((int)ch.Interpretation != int.Parse(t[3])) return false;
            if (ch.InterpretationName != HexToStr(t[4])) return false;
            if ((int)ch.MirrorAxis != int.Parse(t[5])) return false;
            if (!ChkHex(t[6], ch.ScaleFactor)) return false;
            if (ch.Frames.Length != int.Parse(t[7])) return false;
            return true;
        }

        static bool DoFr(string[] t, UfbxGeometryCache cache)
        {
            int chanIx = int.Parse(t[1]);
            if (cache == null || chanIx >= cache.Channels.Length) return false;
            UfbxCacheChannel ch = cache.Channels[chanIx];
            // The oracle emits frames per channel in order; match by walking.
            if (!frCursor.TryGetValue(chanIx, out int f)) { f = 0; }
            if (f >= ch.Frames.Length) return false;
            UfbxCacheFrame fr = ch.Frames[f];
            frCursor[chanIx] = f + 1;

            if (!ChkHex(t[2], fr.Time)) return false;
            if ((int)fr.FileFormat != int.Parse(t[3])) return false;
            if ((int)fr.DataEncoding != int.Parse(t[4])) return false;
            if ((long)fr.DataOffset != long.Parse(t[5])) return false;
            if (fr.DataCount != uint.Parse(t[6])) return false;
            if (fr.DataElementBytes != uint.Parse(t[7])) return false;
            if ((long)fr.DataTotalBytes != long.Parse(t[8])) return false;
            return true;
        }

        static readonly Dictionary<int, int> frCursor = new Dictionary<int, int>();

        static bool DoSmp(string[] t, UfbxGeometryCache cache)
        {
            int chanIx = int.Parse(t[1]);
            if (cache == null || chanIx >= cache.Channels.Length) return false;
            UfbxCacheChannel ch = cache.Channels[chanIx];
            double time = D(t[2]);
            int n = int.Parse(t[3]);

            UfbxVec3[] data = new UfbxVec3[8];
            int numRead = UfbxGeometryCacheApi.SampleGeometryCacheVec3(ch, time, data, 4, null);
            if (numRead != n) return false;
            double[] flat = new double[n * 3];
            for (int i = 0; i < n; i++) { flat[i * 3 + 0] = data[i].X; flat[i * 3 + 1] = data[i].Y; flat[i * 3 + 2] = data[i].Z; }
            for (int k = 0; k < n * 3; k++) {
                if (!ChkHex(t[4 + k], flat[k])) return false;
            }
            return true;
        }

        // Recreate the exact synthetic skin deformer the oracle built.
        static UfbxSkinDeformer Skin()
        {
            var node0 = new UfbxNode();
            var node1 = new UfbxNode();

            var cluster0 = new UfbxSkinCluster();
            cluster0.BoneNode = node0;
            cluster0.GeometryToWorld = UfbxMatrix.Identity;
            cluster0.GeometryToWorld.M03 = 1.5;
            cluster0.GeometryToWorldTransform = UfbxMatrix.ToTransform(cluster0.GeometryToWorld);

            var cluster1 = new UfbxSkinCluster();
            cluster1.BoneNode = node1;
            cluster1.GeometryToWorld = UfbxMatrix.Identity;
            cluster1.GeometryToWorld.M00 = 2.0;
            cluster1.GeometryToWorldTransform = UfbxMatrix.ToTransform(cluster1.GeometryToWorld);

            var weights = new UfbxSkinWeight[6];
            weights[0] = new UfbxSkinWeight { ClusterIndex = 0, Weight = 0.75 };
            weights[1] = new UfbxSkinWeight { ClusterIndex = 1, Weight = 0.25 };
            weights[2] = new UfbxSkinWeight { ClusterIndex = 0, Weight = 1.0 };
            weights[3] = new UfbxSkinWeight { ClusterIndex = 0, Weight = 0.4 };
            weights[4] = new UfbxSkinWeight { ClusterIndex = 1, Weight = 0.6 };
            weights[5] = new UfbxSkinWeight { ClusterIndex = 0, Weight = 0.0 };

            var vertices = new UfbxSkinVertex[3];
            vertices[0] = new UfbxSkinVertex { WeightBegin = 0, NumWeights = 2, DqWeight = 0.0 };
            vertices[1] = new UfbxSkinVertex { WeightBegin = 2, NumWeights = 1, DqWeight = 1.0 };
            vertices[2] = new UfbxSkinVertex { WeightBegin = 3, NumWeights = 3, DqWeight = 0.5 };

            var skin = new UfbxSkinDeformer();
            skin.Clusters = new UfbxSkinCluster[] { cluster0, cluster1 };
            skin.Weights = weights;
            skin.Vertices = vertices;
            return skin;
        }

        static UfbxSkinDeformer cachedSkin;
        static UfbxSkinDeformer SkinCached => cachedSkin ??= Skin();

        static bool DoSk(string[] t)
        {
            int v = int.Parse(t[1]);
            if (!ChkInt(t[2], SkinCached.Vertices[v].NumWeights)) return false;
            if (!ChkHex(t[3], SkinCached.Vertices[v].DqWeight)) return false;
            UfbxMatrix m = UfbxSkinApi.GetSkinVertexMatrix(SkinCached, v, null);
            double[] mm = new double[] {
                m.M00, m.M10, m.M20, m.M01, m.M11, m.M21, m.M02, m.M12, m.M22, m.M03, m.M13, m.M23
            };
            for (int i = 0; i < 12; i++) {
                if (!ChkHex(t[4 + i], mm[i])) return false;
            }
            return true;
        }

        // Recreate the skin deformer of dump_skin_catch() (tools/s4a_oracle.c:272-329): the same two
        // clusters as Skin(), but 4 weights (two zero, one 0.3, one exactly UFBX_EPSILON) and 3
        // vertices, so SKC reaches the panic site, the `total_weight <= 0` fallback branch, the
        // `rcp_weight == 0` sub-branch and -- via the 2^32 / SIZE_MAX probes -- the unsigned
        // `size_t vertex` bounds tests.
        static UfbxSkinDeformer cachedSkinCatch;
        static readonly long[] SkcVertex = { 0, 0, 1, 2, 1, 3, 4, -1L, 1L << 32, (1L << 32) + 4, 0, 3 };
        static readonly int[] SkcFallback = { 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0 };
        static readonly int[] SkcPreset = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1 };

        static UfbxSkinDeformer SkinCatch()
        {
            if (cachedSkinCatch != null) return cachedSkinCatch;

            var cluster0 = new UfbxSkinCluster();
            cluster0.BoneNode = new UfbxNode();
            cluster0.GeometryToWorld = UfbxMatrix.Identity;
            cluster0.GeometryToWorld.M03 = 1.5;
            cluster0.GeometryToWorldTransform = UfbxMatrix.ToTransform(cluster0.GeometryToWorld);

            var cluster1 = new UfbxSkinCluster();
            cluster1.BoneNode = new UfbxNode();
            cluster1.GeometryToWorld = UfbxMatrix.Identity;
            cluster1.GeometryToWorld.M00 = 2.0;
            cluster1.GeometryToWorldTransform = UfbxMatrix.ToTransform(cluster1.GeometryToWorld);

            var weights = new UfbxSkinWeight[4];
            weights[0] = new UfbxSkinWeight { ClusterIndex = 0, Weight = 0.0 };
            weights[1] = new UfbxSkinWeight { ClusterIndex = 1, Weight = 0.0 };
            weights[2] = new UfbxSkinWeight { ClusterIndex = 0, Weight = 0.3 };
            // C: UFBX_EPSILON for double (ufbx.c:70-72) -- > 0 so the fallback branch is skipped, but
            // not > UFBX_EPSILON (strict), which is the only way into `rcp_weight = 0` (31991).
            weights[3] = new UfbxSkinWeight { ClusterIndex = 1, Weight = 1.4916681462400413e-154 };

            var vertices = new UfbxSkinVertex[3];
            vertices[0] = new UfbxSkinVertex { WeightBegin = 0, NumWeights = 2, DqWeight = 0.0 };
            vertices[1] = new UfbxSkinVertex { WeightBegin = 2, NumWeights = 1, DqWeight = 0.5 };
            vertices[2] = new UfbxSkinVertex { WeightBegin = 3, NumWeights = 1, DqWeight = 0.0 };

            var skin = new UfbxSkinDeformer();
            skin.Clusters = new UfbxSkinCluster[] { cluster0, cluster1 };
            skin.Weights = weights;
            skin.Vertices = vertices;
            cachedSkinCatch = skin;
            return skin;
        }

        // The oracle's `fallback`: identity with m03 = -7.25 and m11 = 3.0.
        static UfbxMatrix SkcFallbackMatrix()
        {
            UfbxMatrix fallback = UfbxMatrix.Identity;
            fallback.M03 = -7.25;
            fallback.M11 = 3.0;
            return fallback;
        }

        static bool DoSkc(string[] t)
        {
            int ix = int.Parse(t[1]);
            if (!ChkInt(t[2], SkcFallback[ix])) return false;

            UfbxPanic panic = default;
            if (SkcPreset[ix] != 0) {
                panic.DidPanic = true;
                panic.MessageLength = 6;
                panic.Message = "PRESET";
            }

            UfbxMatrix? fallback = SkcFallback[ix] != 0 ? SkcFallbackMatrix() : (UfbxMatrix?)null;
            UfbxMatrix m = UfbxSkinApi.CatchGetSkinVertexMatrix(ref panic, SkinCatch(), SkcVertex[ix], fallback);

            if (!ChkInt(t[3], panic.DidPanic ? 1 : 0)) return false;
            if (!ChkInt(t[4], panic.MessageLength)) return false;
            if (!ChkMsg(t[5], panic.Message)) return false;

            double[] mm = new double[] {
                m.M00, m.M10, m.M20, m.M01, m.M11, m.M21, m.M02, m.M12, m.M22, m.M03, m.M13, m.M23
            };
            for (int i = 0; i < 12; i++) {
                if (!ChkHex(t[6 + i], mm[i])) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- blend shapes

        static UfbxBlendShape cachedShape;
        static UfbxBlendShape BlendShape()
        {
            if (cachedShape != null) return cachedShape;
            var shape = new UfbxBlendShape();
            shape.NumOffsets = 3;
            shape.OffsetVertices = new uint[] { 1, 3, 5 };
            shape.PositionOffsets = new UfbxVec3[] {
                new UfbxVec3(1.0, 2.0, 3.0), new UfbxVec3(-1.0, 0.5, 0.25), new UfbxVec3(0.0, -2.0, 4.0)
            };
            shape.OffsetWeights = new double[] { 0.5, 2.0 };
            cachedShape = shape;
            return shape;
        }

        static UfbxBlendDeformer cachedBlend;
        static UfbxBlendDeformer Blend()
        {
            if (cachedBlend != null) return cachedBlend;
            var keys = new UfbxBlendKeyframe[2];
            keys[0] = new UfbxBlendKeyframe { Shape = BlendShape(), EffectiveWeight = 0.5 };
            keys[1] = new UfbxBlendKeyframe { Shape = BlendShape(), EffectiveWeight = -0.25 };
            var chan = new UfbxBlendChannel();
            chan.Keyframes = keys;
            var blend = new UfbxBlendDeformer();
            blend.Channels = new UfbxBlendChannel[] { chan };
            cachedBlend = blend;
            return blend;
        }

        static bool DoBsv(string[] t)
        {
            UfbxVec3 off = UfbxSkinApi.GetBlendShapeVertexOffset(BlendShape(), int.Parse(t[2]));
            return ChkHex(t[3], off.X) && ChkHex(t[4], off.Y) && ChkHex(t[5], off.Z);
        }

        static bool DoBvo(string[] t)
        {
            UfbxVec3 off = UfbxSkinApi.GetBlendVertexOffset(Blend(), int.Parse(t[1]));
            return ChkHex(t[2], off.X) && ChkHex(t[3], off.Y) && ChkHex(t[4], off.Z);
        }

        static bool DoBav(string[] t)
        {
            UfbxVec3[] verts = new UfbxVec3[6];
            UfbxSkinApi.AddBlendVertexOffsets(Blend(), verts, 6, 1.0);
            if (t[4] != "18") return false;
            for (int i = 0; i < 6; i++) {
                if (!ChkHex(t[5 + i * 3 + 0], verts[i].X)) return false;
                if (!ChkHex(t[5 + i * 3 + 1], verts[i].Y)) return false;
                if (!ChkHex(t[5 + i * 3 + 2], verts[i].Z)) return false;
            }
            return true;
        }

        // The probes of dump_blend_add_shape() (tools/s4a_oracle.c:398-430), given as
        // (pass NULL buffer?, num_vertices, weight).
        static readonly int[] BsaNull = { 0, 0, 0, 0, 1 };
        static readonly int[] BsaNumVertices = { 6, 6, 4, 6, 6 };
        static readonly double[] BsaWeight = { 1.0, 0.0, 1.0, 0.25, 1.0 };

        static bool DoBsa(string[] t)
        {
            int ix = int.Parse(t[1]);
            if (!ChkInt(t[2], BsaNull[ix])) return false;
            if (!ChkInt(t[3], BsaNumVertices[ix])) return false;
            if (!ChkHex(t[4], BsaWeight[ix])) return false;

            UfbxVec3[] verts = new UfbxVec3[6];
            UfbxSkinApi.AddBlendShapeVertexOffsets(BlendShape(),
                BsaNull[ix] != 0 ? null : verts, BsaNumVertices[ix], BsaWeight[ix]);

            for (int i = 0; i < 6; i++) {
                if (!ChkHex(t[5 + i * 3 + 0], verts[i].X)) return false;
                if (!ChkHex(t[5 + i * 3 + 1], verts[i].Y)) return false;
                if (!ChkHex(t[5 + i * 3 + 2], verts[i].Z)) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- synthetic cache reads (RD / RDO)

        // Mirror of tools/s4a_oracle.c:dump_read_cases(). Both sides build the same synthetic
        // cache "file" in memory, hand-build the ufbx_cache_frame / ufbx_cache_channel records,
        // and serve the bytes through a user open-file callback whose read/skip/close requests are
        // folded into a hash. That call log is the only way to see the 512-real read chunking
        // (ufbx.c:32791) and the UFBXI_MAX_SKIP_SIZE seek chunking (ufbx.c:54) at all: the decoded
        // values are chunk-size independent, because mirror_ix keeps its global mod-3 phase across
        // chunks (32838). Every call goes through the public UfbxGeometryCacheApi façade.
        const int RdFileSize = 20000;
        const int RdOutDoubles = 1200;
        const ulong FnvBasis = 0xcbf29ce484222325UL;
        const ulong FnvPrime = 0x100000001b3UL;

        static ulong rdFnv;
        static byte[] rdFile;
        static int rdAvail;
        static long rdPos;
        static bool rdInfinite, rdHaveSkip, rdErrorFirst;
        static int rdReads, rdSkips;
        static readonly Dictionary<int, string> rdLines = new Dictionary<int, string>();

        static void Fnv1(byte b) { rdFnv ^= b; rdFnv *= FnvPrime; }
        static void Fnv8(ulong v) { for (int i = 0; i < 8; i++) Fnv1((byte)((v >> (i * 8)) & 0xff)); }
        static ulong RdBits(double v) => unchecked((ulong)BitConverter.DoubleToInt64Bits(v));
        static string RdHex(ulong v) => v.ToString("x16");

        //   fn fmt enc dcount doff nblk mirror scale8 flags weight8 outc trunc time16
        static readonly long[][] RdCases = new long[][] {
            new long[] { 0, 4, 1,   200,          0, 1, 0,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 2,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 3,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 2, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 2, 2,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 4, 2,   200,          0, 1, 2,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 3, 1,  1200,          0, 1, 1,  8,   8,  8, 1200,   0,  0 },
            new long[] { 0, 3, 1,  1027,          0, 1, 1,  8,   8,  8, 1027,   0,  0 },
            new long[] { 0, 1, 1,   512,          0, 1, 1,  8,   8,  8,  512,   0,  0 },
            new long[] { 0, 1, 1,   513,          0, 1, 1,  8,   8,  8,  513,   0,  0 },
            new long[] { 1, 4, 1,   200,          0, 1, 1,  8,   8,  8,  200,   0,  0 },
            new long[] { 1, 4, 1,   200,          0, 1, 0,  8,   8,  8,  200,  16,  0 },
            new long[] { 1, 4, 1,   200,          0, 1, 1,  8,   8,  8,  200,  16,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1,  8,   9,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 0, 12,   8,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1, 12,   8,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 2,  0,   8,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1,  8,  78,  3,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1,  8,  12,  6,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          3, 1, 1,  8,   0,  8,  600,   0,  0 },
            new long[] { 0, 2, 1,   200,       9000, 1, 1,  8,   0,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200, 3221225479, 1, 1, 8,  40,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200, 1073741824, 1, 1, 8,  40,  8,  600,   0,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600, 100,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600,4800,  0 },
            new long[] { 0, 4, 1,   200,          0, 1, 1,  8,  24,  8,  600,   0,  0 },
            new long[] { 0, 4, 0,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 },
            new long[] { 0, 0, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 },
            new long[] { 2, 4, 1,   200,          0, 3, 1,  8,   8,  8,  600,   0,  8 },
            new long[] { 2, 4, 1,   200,          0, 3, 1,  8,   8,  8,  600,   0, -8 },
            new long[] { 2, 4, 1,   200,          0, 3, 1,  8,   8,  8,  600,   0, 80 },
            new long[] { 2, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 },
            new long[] { 3, 4, 1,   200,          0, 2, 1,  8,   8,  8,  200,   0,  8 },
            new long[] { 2, 4, 1,   200,          0, 2, 1,  8,  76,  4,  600,   0,  8 },
        };

        // C: rd_val() -- small dyadic values, exact in float and double, so the records do not
        // depend on any libm behaviour.
        static double RdVal(long b, long i)
        {
            return (double)(((b * 61 + i * 37 + 11) % 251) - 100) * 0.125;
        }

        // C: rd_read_fn / rd_skip_fn / rd_close_fn over the synthetic file (bytes served modulo
        // RdFileSize so an "infinite" stream still has well-defined content).
        sealed class RdStream : UfbxInputStream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                rdReads++;
                Fnv1((byte)'r');
                Fnv8(unchecked((ulong)count));
                if (rdErrorFirst && rdReads == 1) { Fnv8(ulong.MaxValue); return -1; } // C: SIZE_MAX
                long avail = rdInfinite ? count : (rdPos < rdAvail ? rdAvail - rdPos : 0);
                int n = count < avail ? count : (int)avail;
                for (int i = 0; i < n; i++) buffer[offset + i] = rdFile[(int)((rdPos + i) % RdFileSize)];
                rdPos += n;
                Fnv8(unchecked((ulong)n));
                return n;
            }

            public override bool CanSkip => rdHaveSkip;

            public override bool Skip(int size)
            {
                rdSkips++;
                Fnv1((byte)'s');
                Fnv8(unchecked((ulong)(long)size));
                bool ok = rdInfinite || rdPos + size <= rdAvail;
                if (ok) rdPos += size;
                Fnv1(ok ? (byte)1 : (byte)0);
                return ok;
            }

            public override void Close() { Fnv1((byte)'c'); }
        }

        static bool RdOpenCb(object user, UfbxInputStream stream, string path, int pathLength, UfbxOpenFileInfo info)
        {
            rdPos = 0; rdReads = 0; rdSkips = 0;
            ((UfbxiLoad.UfbxiOpenFileStream)stream).Attach(new RdStream());
            return true;
        }

        // Run case `ci` and return its "RD ..." line, caching the matching "RDO ..." line.
        static string RunRdCase(int ci)
        {
            long[] c = RdCases[ci];
            int fn = (int)c[0], fmt = (int)c[1], enc = (int)c[2];
            long dcount = c[3], doff = c[4];
            int nblk = (int)c[5], mirror = (int)c[6];
            double scale = c[7] / 8.0;
            long flags = c[8];
            double weight = c[9] / 8.0;
            long outc = c[10], trunc = c[11];
            double time = c[12] / 16.0;

            int esz = (fmt == 1 || fmt == 2) ? 4 : 8;
            long realsPb = (fmt == 2 || fmt == 4) ? dcount * 3 : dcount;
            long stride = realsPb * esz;

            rdFile = new byte[RdFileSize];
            byte[] raw = new byte[8];
            for (int b = 0; b < nblk; b++) {
                for (long i = 0; i < realsPb; i++) {
                    double v = RdVal(b, i);
                    Array.Clear(raw, 0, raw.Length);
                    if (esz == 8) {
                        ulong u = RdBits(v);
                        for (int j = 0; j < 8; j++) raw[j] = (byte)((u >> (j * 8)) & 0xff);
                    } else {
                        uint u = unchecked((uint)BitConverter.SingleToInt32Bits((float)v));
                        for (int j = 0; j < 4; j++) raw[j] = (byte)((u >> (j * 8)) & 0xff);
                    }
                    ulong p = unchecked((ulong)(doff + (long)b * stride + i * esz));
                    for (int j = 0; j < esz; j++) {
                        rdFile[(int)((p + (ulong)j) % RdFileSize)] = enc == 2 ? raw[esz - 1 - j] : raw[j];
                    }
                }
            }
            long av = doff + (long)nblk * stride - trunc;
            rdAvail = av < 0 ? 0 : (av > RdFileSize ? RdFileSize : (int)av);

            UfbxCacheFrame[] fr = new UfbxCacheFrame[nblk];
            for (int b = 0; b < nblk; b++) {
                UfbxCacheFrame f = new UfbxCacheFrame();
                f.Time = b;
                f.Filename = "synth.rd";
                f.FileFormat = UfbxCacheFileFormat.Mc;
                f.MirrorAxis = (UfbxMirrorAxis)mirror;
                f.ScaleFactor = scale;
                f.DataFormat = (UfbxCacheDataFormat)fmt;
                f.DataEncoding = (UfbxCacheDataEncoding)enc;
                f.DataOffset = unchecked((ulong)(doff + (long)b * stride));
                f.DataCount = unchecked((uint)dcount);
                f.DataElementBytes = (uint)esz;
                f.DataTotalBytes = unchecked((ulong)stride);
                fr[b] = f;
            }

            UfbxGeometryCacheDataOpts opts = new UfbxGeometryCacheDataOpts();
            rdHaveSkip = (flags & 8) != 0;
            rdInfinite = (flags & 32) != 0;
            rdErrorFirst = (flags & 16) != 0;
            opts.IgnoreTransform = (flags & 1) != 0;
            opts.Additive = (flags & 2) != 0;
            opts.UseWeight = (flags & 4) != 0;
            opts.Weight = weight;
            opts.OpenFileCb = new UfbxOpenFileCb();
            opts.OpenFileCb.Fn = RdOpenCb;

            bool isVec3 = fn == 1 || fn == 3;
            double[] outReal = new double[RdOutDoubles];
            UfbxVec3[] outVec3 = new UfbxVec3[RdOutDoubles / 3];
            if ((flags & 64) != 0) {
                for (int i = 0; i < RdOutDoubles; i++) outReal[i] = 1000.5;
                for (int i = 0; i < RdOutDoubles / 3; i++) {
                    UfbxVec3 v = new UfbxVec3();
                    v.X = 1000.5; v.Y = 1000.5; v.Z = 1000.5;
                    outVec3[i] = v;
                }
            }

            rdFnv = FnvBasis;
            rdPos = 0; rdReads = 0; rdSkips = 0;
            int n;
            if (fn == 0) {
                n = UfbxGeometryCacheApi.ReadGeometryCacheReal(fr[0], outReal, (int)outc, opts);
            } else if (fn == 1) {
                n = UfbxGeometryCacheApi.ReadGeometryCacheVec3(fr[0], outVec3, (int)outc, opts);
            } else {
                UfbxCacheChannel chan = new UfbxCacheChannel();
                chan.Name = "chan";
                chan.Interpretation = UfbxCacheInterpretation.VertexPosition;
                chan.InterpretationName = string.Empty;
                chan.Frames = fr;
                chan.MirrorAxis = (UfbxMirrorAxis)mirror;
                chan.ScaleFactor = scale;
                if (fn == 2) n = UfbxGeometryCacheApi.SampleGeometryCacheReal(chan, time, outReal, (int)outc, opts);
                else n = UfbxGeometryCacheApi.SampleGeometryCacheVec3(chan, time, outVec3, (int)outc, opts);
            }

            ulong callHash = rdFnv;
            rdFnv = FnvBasis;
            for (int i = 0; i < RdOutDoubles; i++) {
                double d;
                if (isVec3) {
                    UfbxVec3 v = outVec3[i / 3];
                    d = i % 3 == 0 ? v.X : (i % 3 == 1 ? v.Y : v.Z);
                } else {
                    d = outReal[i];
                }
                Fnv8(RdBits(d));
            }
            long outReals = isVec3 ? outc * 3 : outc;

            // C: the RDO record proves the caller's opts survived the read; `ufbx_read_geometry_cache_real`
            // works on a copy (ufbx.c:32711-32718) and an interpolated sample only mutates that copy.
            rdLines[ci] = "RDO " + ci
                + " " + (opts.IgnoreTransform ? 1 : 0)
                + " " + (opts.Additive ? 1 : 0)
                + " " + (opts.UseWeight ? 1 : 0)
                + " " + (opts.OpenFileCb != null && opts.OpenFileCb.Fn == RdOpenCb ? 1 : 0)
                + " " + (opts.OpenFileCb != null && opts.OpenFileCb.User == null ? 1 : 0)
                + " " + RdHex(RdBits(opts.Weight));

            return "RD " + ci + " " + n + " " + outReals + " " + RdHex(callHash) + " " + RdHex(rdFnv)
                + " " + rdReads + " " + rdSkips;
        }

        static bool DoRd(string line, string[] t)
        {
            int ci = int.Parse(t[1]);
            string mine = RunRdCase(ci);
            if (mine != line) {
                failLinesRd(mine, line);
                return false;
            }
            return true;
        }

        static bool DoRdo(string line, string[] t)
        {
            int ci = int.Parse(t[1]);
            if (!rdLines.TryGetValue(ci, out string mine)) return false;
            if (mine != line) {
                failLinesRd(mine, line);
                return false;
            }
            return true;
        }

        static void failLinesRd(string mine, string want)
        {
            if (failLines.Count < 30) {
                failLines.Add("  RD mine: " + mine);
                failLines.Add("  RD  want: " + want);
            }
        }

        // Run from the repo root; the oracle recorded paths relative to `tools/`. Re-anchor the
        // recorded path under the repo root if it starts with `tools/`.
        static string Local(string path)
        {
            return path;
        }
    }
}
