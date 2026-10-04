// S4c: mesh topology / normal mapping / normals / triangulation, ported from
// ufbx.c v0.23.1. Owned by the S4c porting agent (2026-10-03 wave).
//
// Covered C functions (ufbx.c line ranges in per-function comments):
//   ufbxi_ngon_project (28282), ufbxi_orient2d (28292), ufbxi_kd_check_point (28297),
//   ufbxi_kd_check_slow (28312), ufbxi_kd_check_fast (28353), ufbxi_kd_check (28400),
//   ufbxi_kd_index_less (28416), ufbxi_kd_build (28427), ufbxi_ngon_tri_weight (28482),
//   ufbxi_triangulate_ngon (28497), ufbxi_topo_less_index_prev_next (28700),
//   ufbxi_topo_less_index_index (28708), ufbxi_compute_topology (28715),
//   ufbxi_is_edge_smooth (28794),
//   ufbx_catch_triangulate_face (32400) / ufbx_triangulate_face (33173),
//   ufbx_catch_compute_topology (32485) / ufbx_compute_topology (33176),
//   ufbx_catch_topo_next_vertex_edge (32492) / ufbx_topo_next_vertex_edge (33179),
//   ufbx_catch_topo_prev_vertex_edge (32502) / ufbx_topo_prev_vertex_edge (33182),
//   ufbx_catch_get_weighted_face_normal (32509) / ufbx_get_weighted_face_normal (33185),
//   ufbx_catch_generate_normal_mapping (32542) / ufbx_generate_normal_mapping (32588),
//   ufbx_catch_compute_normals (32593) / ufbx_compute_normals (32622).
//   ufbx_get_vertex_vec3 / ufbx_get_vertex_real inline accessors (ufbx.h:5763-5766, 5769).
//   ufbx_catch_get_vertex_real / vec2 / vec3 / vec4 (ufbx.c:33001-33031),
//   ufbx_catch_get_vertex_w_vec3 (ufbx.c:33033).
//
// Representation notes:
//  - The triangulation KD-tree/ear-clip scratch buffer is ONE `uint[]` exactly like C's
//    caller-provided `indices` array; the kd region, the ear-clip `edges` window
//    (`C: indices + num_indices - face.num_indices * 2`) and the triangle output overlap
//    the same way (the <12-entry local-buffer path reproduces C's local_indices[12]).
//  - ufbxi_kd_build/ufbxi_stable_sort operate on sub-ranges of that buffer, so this file
//    carries an offset-based transcription of ufbxi_stable_sort (ufbx.c:1142-1186) instead
//    of the whole-array UfbxiSort.StableSort.
//  - Sentinel index buffers (Parse/Geometry.cs): a mesh may still carry
//    SentinelIndexZero/Consecutive before `ufbxi_patch_index_pointer` runs. C reads them
//    like normal arrays; the attrib accessors below resolve them by reference identity
//    (zero sentinel -> index 0, consecutive sentinel -> the position itself) to match the
//    patched buffers (`ufbx.c:22029-22038`: memset zero / `consecutive_indices[i] = i`).
//  - ufbx.h:5763-5766 read `values.data[(int32_t)indices.data[index]]`, where
//    `UFBX_NO_INDEX` casts to `-1` and reads the zero-guard element C places *before*
//    guarded arrays (`*positions++ = ufbx_zero_vec3`, ufbx.c:28003). Guarded arrays only
//    exist for tessellated/subdivided meshes whose indices are never NO_INDEX, so the
//    accessors return zero for NO_INDEX (documented divergence; unreachable otherwise).
//
// Floating point: expressions are transcribed in C's evaluation order; no System.Math.

using System;

namespace Ufbx.NET
{
    internal static class UfbxTopology
    {
        // C: UFBXI_KD_FAST_DEPTH (ufbx.c:56; the regression override at ufbx.c:1007-1008
        // sets 2 and is not part of the golden build).
        internal const int KdFastDepth = 6;

        // C: UFBX_NO_INDEX (ufbx.h:396).
        internal const uint NoIndex = UfbxConstants.NoIndex;

        // ------------------------------------------------------------------
        // Vertex attribute accessors (ufbx.h:5763-5766 inline semantics)
        // ------------------------------------------------------------------

        // C: `v->indices.data[index]` with sentinel resolution (see file header).
        static uint AttribIndex(uint[] indices, int index)
        {
            if (ReferenceEquals(indices, UfbxiGeometry.SentinelIndexZero)
                || ReferenceEquals(indices, UfbxiObjects.SentinelIndexZero)) {
                return 0;
            }
            if (ReferenceEquals(indices, UfbxiGeometry.SentinelIndexConsecutive)
                || ReferenceEquals(indices, UfbxiObjects.SentinelIndexConsecutive)) {
                return unchecked((uint)index);
            }
            return indices[index];
        }

        // C: ufbx_get_vertex_vec3 (ufbx.h:5765): `values.data[(int32_t)indices.data[index]]`.
        // `(int32_t)UFBX_NO_INDEX == -1` reads C's zero-guard element; see file header.
        internal static UfbxVec3 GetVertexVec3(UfbxVertexVec3 v, int index)
        {
            uint ix = AttribIndex(v.Indices, index);
            if (ix == NoIndex) return default;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_get_vertex_real (ufbx.h:5763).
        internal static double GetVertexReal(UfbxVertexReal v, int index)
        {
            uint ix = AttribIndex(v.Indices, index);
            if (ix == NoIndex) return 0.0;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_get_vertex_vec2 (ufbx.h:5764).
        internal static UfbxVec2 GetVertexVec2(UfbxVertexVec2 v, int index)
        {
            uint ix = AttribIndex(v.Indices, index);
            if (ix == NoIndex) return default;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_get_vertex_vec4 (ufbx.h:5766).
        internal static UfbxVec4 GetVertexVec4(UfbxVertexVec4 v, int index)
        {
            uint ix = AttribIndex(v.Indices, index);
            if (ix == NoIndex) return default;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_get_vertex_w_vec3 (ufbx.h:5769).
        internal static double GetVertexWVec3(UfbxVertexVec3 v, int index)
        {
            if (v.ValuesW == null || v.ValuesW.Length == 0) return 0.0;
            uint ix = AttribIndex(v.Indices, index);
            if (ix == NoIndex) return 0.0;
            return v.ValuesW[unchecked((int)ix)];
        }

        // ------------------------------------------------------------------
        // Panic plumbing (ufbx.c:3388-3410)
        // ------------------------------------------------------------------

        // C: ufbxi_panicf(panic, cond, ...) (ufbx.c:3409-3410) —
        // `(cond) ? false : (ufbxi_panicf_imp((panic), __VA_ARGS__), true)`. The imp body
        // (ufbx.c:3388-3407) early-outs when `panic->did_panic` is already set and otherwise
        // records the first panic message. The port models the non-NULL `panic` branch only:
        // C's panic == NULL branch routes the message to ufbx_panic_handler() (stderr +
        // assert unless the user overrides the macro), which is unreachable for valid inputs;
        // the plain public forms below use a scratch local panic (documented divergence).
        static bool Panicf(ref UfbxPanic panic, bool condition, string fmt, UfbxiVaList args)
        {
            if (condition) return false;
            UfbxiPrint.Panicf(ref panic, fmt, args);
            return true;
        }

        // ------------------------------------------------------------------
        // Triangulation: projection / KD-tree (ufbx.c:28253-28478)
        // ------------------------------------------------------------------

        // C: ufbxi_kd_node (ufbx.c:28255-28261).
        struct KdNode
        {
            public double Split;           // C: split
            public uint IndexPlusOne;      // C: index_plus_one (0 for empty)
            public uint SlowLeft;          // C: slow_left
            public uint SlowRight;         // C: slow_right
            public uint SlowEnd;           // C: slow_end
        }

        // C: ufbxi_kd_triangle (ufbx.c:28275-28280).
        struct KdTriangle
        {
            public double MinT0, MinT1;    // C: min_t[2]
            public double MaxT0, MaxT1;    // C: max_t[2]
            public UfbxVec2 P0, P1, P2;    // C: points[3]
            public uint I0, I1, I2;        // C: indices[3]
        }

        // C: ufbxi_ngon_context (ufbx.c:28255-28273). The C `kd_indices`/`kd_tmp` pointers
        // become offsets into the caller's `Buf` (kd_indices is always the buffer start).
        sealed class NgonContext
        {
            public uint[] Buf;             // C: caller `indices` buffer (kd region + edges + output)
            public UfbxVertexVec3 Positions; // C: positions
            public UfbxVec3 Axis0, Axis1, Axis2; // C: axes[3]
            public KdNode[] KdNodes = new KdNode[1 << (KdFastDepth + 1)]; // C: kd_nodes[1 << (UFBXI_KD_FAST_DEPTH + 1)]

            public UfbxVec3 CurAxisDir;    // C: cur_axis_dir
            public UfbxFace CurFace;       // C: cur_face
        }

        // C: ufbxi_ngon_project (ufbx.c:28282-28290).
        static UfbxVec2 NgonProject(NgonContext nc, uint index)
        {
            UfbxVec3 point = nc.Positions.Values[
                AttribIndex(nc.Positions.Indices, (int)(nc.CurFace.IndexBegin + index))];

            UfbxVec2 p;
            p.X = UfbxVec3.Dot3(nc.Axis0, point);
            p.Y = UfbxVec3.Dot3(nc.Axis1, point);
            return p;
        }

        // C: ufbxi_orient2d (ufbx.c:28292-28295) — sign-sensitive, transcribed verbatim.
        internal static double Orient2d(UfbxVec2 a, UfbxVec2 b, UfbxVec2 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        // C: ufbxi_kd_check_point (ufbx.c:28297-28309).
        static bool KdCheckPoint(NgonContext nc, ref KdTriangle tri, uint index)
        {
            if (index == tri.I0 || index == tri.I1 || index == tri.I2) return false;
            UfbxVec2 p = NgonProject(nc, index);

            UfbxVec2 tp0 = tri.P0, tp1 = tri.P1, tp2 = tri.P2;
            double u = Orient2d(p, tp0, tp1);
            double v = Orient2d(p, tp1, tp2);
            double w = Orient2d(p, tp2, tp0);

            if (u <= 0.0 && v <= 0.0 && w <= 0.0) return true;
            if (u >= 0.0 && v >= 0.0 && w >= 0.0) return true;
            return false;
        }

        // C: ufbxi_kd_check_slow (ufbx.c:28312-28350). The ufbxi_recursive_function depth
        // limit (32 - UFBXI_KD_FAST_DEPTH) compiles away outside regression builds.
        static bool KdCheckSlow(NgonContext nc, ref KdTriangle tri, uint begin, uint count, uint axis)
        {
            UfbxVertexVec3 pos = nc.Positions;
            uint[] kdIndices = nc.Buf;

            while (count > 0) {
                uint numLeft = count / 2;
                uint beginRight = begin + numLeft + 1;
                uint numRight = count - (numLeft + 1);

                uint index = kdIndices[begin + numLeft];
                UfbxVec3 point = pos.Values[AttribIndex(pos.Indices, (int)(nc.CurFace.IndexBegin + index))];
                double split = UfbxVec3.Dot3(point, AxisOf(nc, axis));
                bool hitLeft = axisMin(ref tri, axis) <= split;
                bool hitRight = axisMax(ref tri, axis) >= split;

                if (hitLeft && hitRight) {
                    if (KdCheckPoint(nc, ref tri, index)) {
                        return true;
                    }

                    if (KdCheckSlow(nc, ref tri, beginRight, numRight, axis ^ 1u)) {
                        return true;
                    }
                }

                axis ^= 1u;
                if (hitLeft) {
                    count = numLeft;
                } else {
                    begin = beginRight;
                    count = numRight;
                }
            }

            return false;
        }

        // The kd code indexes min_t/max_t/axes by `axis & 1`; these helpers keep the
        // C struct-array layout explicit.
        static double axisMin(ref KdTriangle tri, uint axis) => (axis & 1u) == 0 ? tri.MinT0 : tri.MinT1;
        static double axisMax(ref KdTriangle tri, uint axis) => (axis & 1u) == 0 ? tri.MaxT0 : tri.MaxT1;

        // C: ufbxi_kd_check_fast (ufbx.c:28353-28398). Recursion depth UFBXI_KD_FAST_DEPTH.
        static bool KdCheckFast(NgonContext nc, ref KdTriangle tri, uint kdIndex, uint axis, uint depth)
        {
            for (;;) {
                KdNode node = nc.KdNodes[kdIndex];
                if (node.IndexPlusOne == 0) return false;

                double minT = axisMin(ref tri, axis);
                double maxT = axisMax(ref tri, axis);
                bool hitLeft = minT <= node.Split;
                bool hitRight = maxT >= node.Split;

                uint side = hitLeft ? 0u : 1u;
                uint childKdIndex = kdIndex * 2 + 1 + side;
                if (hitLeft && hitRight) {

                    // Check for the point on the split plane
                    uint index = node.IndexPlusOne - 1;
                    if (KdCheckPoint(nc, ref tri, index)) {
                        return true;
                    }

                    // Recurse always to the right if we hit both sides
                    if (depth + 1 == KdFastDepth) {
                        if (KdCheckSlow(nc, ref tri, node.SlowRight, node.SlowEnd - node.SlowRight, axis ^ 1u)) {
                            return true;
                        }
                    } else {
                        if (KdCheckFast(nc, ref tri, childKdIndex + 1, axis ^ 1u, depth + 1)) {
                            return true;
                        }
                    }
                }

                depth++;
                axis ^= 1u;
                kdIndex = childKdIndex;

                if (depth == KdFastDepth) {
                    if (hitLeft) {
                        return KdCheckSlow(nc, ref tri, node.SlowLeft, node.SlowRight - node.SlowLeft, axis);
                    } else {
                        return KdCheckSlow(nc, ref tri, node.SlowRight, node.SlowEnd - node.SlowRight, axis);
                    }
                }
            }
        }

        // C: ufbxi_kd_check (ufbx.c:28400-28414). `points`/`indices` are windows of the
        // caller's scratch arrays; `pointOffset` is C's `point_indices + side`.
        static bool KdCheck(NgonContext nc, UfbxVec2[] points, int pointsOffset, uint[] pointIndices, int pointOffset)
        {
            KdTriangle tri;
            tri.P0 = points[pointsOffset + 0];
            tri.P1 = points[pointsOffset + 1];
            tri.P2 = points[pointsOffset + 2];
            tri.I0 = pointIndices[pointOffset + 0];
            tri.I1 = pointIndices[pointOffset + 1];
            tri.I2 = pointIndices[pointOffset + 2];
            tri.MinT0 = UfbxMath.FMin(UfbxMath.FMin(tri.P0.X, tri.P1.X), tri.P2.X);
            tri.MinT1 = UfbxMath.FMin(UfbxMath.FMin(tri.P0.Y, tri.P1.Y), tri.P2.Y);
            tri.MaxT0 = UfbxMath.FMax(UfbxMath.FMax(tri.P0.X, tri.P1.X), tri.P2.X);
            tri.MaxT1 = UfbxMath.FMax(UfbxMath.FMax(tri.P0.Y, tri.P1.Y), tri.P2.Y);
            return KdCheckFast(nc, ref tri, 0, 0, 0);
        }

        // C: ufbxi_kd_index_less (ufbx.c:28416-28424).
        static bool KdIndexLess(object user, uint a, uint b)
        {
            NgonContext nc = (NgonContext)user;
            UfbxVertexVec3 pos = nc.Positions;
            double da = UfbxVec3.Dot3(nc.CurAxisDir,
                pos.Values[AttribIndex(pos.Indices, (int)(nc.CurFace.IndexBegin + a))]);
            double db = UfbxVec3.Dot3(nc.CurAxisDir,
                pos.Values[AttribIndex(pos.Indices, (int)(nc.CurFace.IndexBegin + b))]);
            return da < db;
        }

        // C: ufbxi_stable_sort (ufbxi_macro_stable_sort, ufbx.c:1142-1186) transcribed over
        // sub-ranges `[dataOff, dataOff + size)` / `[tmpOff, tmpOff + size)` of two uint[]
        // buffers (C sorts `uint32_t` windows of one buffer). Same comparison/swap order as
        // UfbxiSort.StableSort, which must not be used here because of the windowing.
        static void StableSortU32Range(uint[] data, int dataOff, uint[] tmp, int tmpOff, int size, UfbxiLessFn<uint> lessFn, object lessUser)
        {
            uint[] src = tmp;
            int srcOff = tmpOff;
            uint[] dst = data;
            int dstOff = dataOff;
            int blockSize = 16; // C: ufbxi_stable_sort(sizeof(uint32_t), 16, ...)

            // Insertion sort in 16-element blocks
            for (int basis = 0; basis < size; basis += blockSize) {
                int iEnd = basis + blockSize;
                if (iEnd > size) iEnd = size;
                for (int i = basis + 1; i < iEnd; i++) {
                    // C: a = dst[i], b = dst[i - 1]
                    if (!lessFn(lessUser, dst[dstOff + i], dst[dstOff + i - 1])) continue;

                    int j = i - 1;
                    // C: mi_src[0] = mi_dst[mi_i]
                    src[srcOff] = dst[dstOff + i];
                    // C: first loop iteration shift (compare already passed)
                    dst[dstOff + i] = dst[dstOff + j];
                    for (; j != basis; --j) {
                        // C: a = mi_src[0], b = mi_dst[mi_j - 1]
                        if (!lessFn(lessUser, src[srcOff], dst[dstOff + j - 1])) break;
                        dst[dstOff + j] = dst[dstOff + j - 1];
                    }
                    dst[dstOff + j] = src[srcOff];
                }
            }

            // Merge sort ping-ponging between `data` and `tmp`
            for (; blockSize < size; blockSize *= 2) {
                uint[] swap = dst; dst = src; src = swap;
                int swapOff = dstOff; dstOff = srcOff; srcOff = swapOff;
                for (int basis = 0; basis < size; basis += blockSize * 2) {
                    int i = basis, iEnd = basis + blockSize;
                    int j = iEnd, jEnd = j + blockSize;
                    int k = basis;
                    if (iEnd > size) iEnd = size;
                    if (jEnd > size) jEnd = size;
                    // C: `(mi_i < mi_i_end) & (mi_j < mi_j_end)` — same result for bools.
                    while (i < iEnd && j < jEnd) {
                        // C: a = &mi_src[mi_j], b = &mi_src[mi_i] — inverted order.
                        if (lessFn(lessUser, src[srcOff + j], src[srcOff + i])) {
                            dst[dstOff + k] = src[srcOff + j];
                            j++;
                        } else {
                            dst[dstOff + k] = src[srcOff + i];
                            i++;
                        }
                        k++;
                    }

                    while (i < iEnd) dst[dstOff + k++] = src[srcOff + i++];
                    while (j < jEnd) dst[dstOff + k++] = src[srcOff + j++];
                }
            }

            // Copy the result to `data` if we ended up in `tmp`
            // C: `if (mi_dst != mi_data) memcpy(mi_data, mi_dst, sizeof(mi_type) * mi_size);`
            // — the copy source is mi_DST (where the last merge pass wrote). C compares
            // POINTERS; here `data`/`tmp` are windows of the SAME caller buffer, so the
            // ping-pong state must be compared as an (array, offset) pair.
            if (!ReferenceEquals(dst, data) || dstOff != dataOff) {
                Array.Copy(dst, dstOff, data, dataOff, size);
            }
        }

        // C: ufbxi_kd_build (ufbx.c:28427-28476). `indicesOff`/`tmpOff` are C's
        // `indices`/`tmp` pointers (offsets into nc->Buf's backing arrays).
        static void KdBuild(NgonContext nc, uint[] indices, int indicesOff, uint[] tmp, int tmpOff, uint num, uint axis, uint fastIndex, uint depth)
        {
            if (num == 0) return;

            UfbxVertexVec3 pos = nc.Positions;
            UfbxVec3 axisDir = AxisOf(nc, axis);
            UfbxFace face = nc.CurFace;

            nc.CurAxisDir = axisDir;
            nc.CurFace = face;

            // Sort the remaining indices based on the axis
            StableSortU32Range(indices, indicesOff, tmp, tmpOff, unchecked((int)num), KdIndexLess, nc);

            uint numLeft = num / 2;
            uint beginRight = numLeft + 1;
            uint numRight = num - beginRight;
            uint dstRight = numLeft + 1;
            if (depth < KdFastDepth) {
                uint skipLeft = 1u << (KdFastDepth - (int)depth - 1);
                dstRight = dstRight > skipLeft ? dstRight - skipLeft : 0;

                uint index = indices[indicesOff + numLeft];
                KdNode kd = nc.KdNodes[fastIndex];

                kd.Split = UfbxVec3.Dot3(axisDir,
                    pos.Values[AttribIndex(pos.Indices, (int)(face.IndexBegin + index))]);
                kd.IndexPlusOne = index + 1;

                if (depth + 1 == KdFastDepth) {
                    kd.SlowLeft = unchecked((uint)indicesOff);
                    kd.SlowRight = kd.SlowLeft + numLeft;
                    kd.SlowEnd = kd.SlowRight + numRight;
                } else {
                    kd.SlowLeft = uint.MaxValue;
                    kd.SlowRight = uint.MaxValue;
                    kd.SlowEnd = uint.MaxValue;
                }
                nc.KdNodes[fastIndex] = kd;
            }

            uint childFast = fastIndex * 2 + 1;
            KdBuild(nc, indices, indicesOff, tmp, tmpOff, numLeft, axis ^ 1u, childFast + 0, depth + 1);

            if (dstRight != beginRight) {
                // C: memmove(indices + dst_right, indices + begin_right, num_right * 4)
                Array.Copy(indices, indicesOff + (int)beginRight, indices, indicesOff + (int)dstRight, (int)numRight);
            }

            KdBuild(nc, indices, indicesOff + (int)dstRight, tmp, tmpOff, numRight, axis ^ 1u, childFast + 1, depth + 1);
        }

        // C: `nc->axes[axis]` accessor.
        static UfbxVec3 AxisOf(NgonContext nc, uint axis)
        {
            switch (axis) {
            case 0: return nc.Axis0;
            case 1: return nc.Axis1;
            default: return nc.Axis2;
            }
        }

        // ------------------------------------------------------------------
        // Triangulation: ear clipping (ufbx.c:28480-28698)
        // ------------------------------------------------------------------

        // C: ufbxi_ngon_tri_weight (ufbx.c:28482-28495). `p` is C's `points + k` window.
        static double NgonTriWeight(UfbxVec2[] points, int p)
        {
            UfbxVec2 p0 = points[p + 0], p1 = points[p + 1], p2 = points[p + 2];
            double orient = Orient2d(p0, p1, p2);
            if (orient <= 0.0) return -1.0;

            double a = UfbxVec2.DistSq2(p0, p1);
            double b = UfbxVec2.DistSq2(p1, p2);
            double c = UfbxVec2.DistSq2(p2, p0);
            double ab = (a + b - c) / UfbxMath.Sqrt(4.0 * a * b);
            double bc = (b + c - a) / UfbxMath.Sqrt(4.0 * b * c);
            double ca = (c + a - b) / UfbxMath.Sqrt(4.0 * c * a);
            return UfbxMath.FMax(UfbxMathConsts.Epsilon, 2.0 - UfbxMath.FMax(UfbxMath.FMax(ab, bc), ca));
        }

        // C: ufbxi_triangulate_ngon (ufbx.c:28497-28696). All scratch windows live in
        // `nc.Buf` exactly like C's `indices` argument.
        static uint TriangulateNgon(NgonContext nc, uint numIndices)
        {
            uint[] indices = nc.Buf;
            UfbxFace face = nc.CurFace;
            uint n = face.NumIndices;

            // Form an orthonormal basis to project the polygon into a 2D plane
            // C: ufbx_get_weighted_face_normal(&nc->positions, face) (ufbx.c:28503) — the
            // plain form (panic == NULL): the checks run and a failure yields the zero
            // vector via the panic handler. The port uses a scratch local panic; the panic
            // sites are unreachable for faces coming from a real mesh.
            UfbxPanic weightedPanic = default;
            UfbxVec3 normal = CatchGetWeightedFaceNormal(ref weightedPanic, nc.Positions, face, -1);
            double len = UfbxVec3.Length3(normal);
            if (len > UfbxMathConsts.Epsilon) {
                normal = UfbxVec3.Mul3(normal, 1.0 / len);
            } else {
                normal.X = 1.0;
                normal.Y = 0.0;
                normal.Z = 0.0;
            }

            UfbxVec3 axis;
            if (normal.X * normal.X < 0.5) {
                axis.X = 1.0;
                axis.Y = 0.0;
                axis.Z = 0.0;
            } else {
                axis.X = 0.0;
                axis.Y = 1.0;
                axis.Z = 0.0;
            }
            nc.Axis0 = UfbxVec3.Normalize3(UfbxVec3.Cross3(axis, normal));    // C: slow_normalized_cross3
            nc.Axis1 = UfbxVec3.Normalize3(UfbxVec3.Cross3(normal, nc.Axis0)); // C: slow_normalized_cross3
            nc.Axis2 = normal;

            // C: uint32_t *kd_indices = indices; uint32_t *kd_tmp = indices + face.num_indices;
            int kdIndicesOff = 0;
            int kdTmpOff = (int)face.NumIndices;

            // Collect all the reflex corners for intersection testing.
            int numKdIndices = 0;
            {
                UfbxVec2 a = NgonProject(nc, n - 1);
                UfbxVec2 b = NgonProject(nc, 0);
                for (uint i = 0; i < n; i++) {
                    uint next = i + 1 < n ? i + 1 : 0;
                    UfbxVec2 c = NgonProject(nc, next);

                    if (Orient2d(a, b, c) <= 0.0) {
                        indices[kdIndicesOff + numKdIndices++] = i;
                    }

                    a = b;
                    b = c;
                }
            }

            // Build a KD-tree of the vertices.
            uint numSkipIndices = (1u << (KdFastDepth + 1)) - 1;
            uint kdSlowIndices = unchecked((uint)numKdIndices) > numSkipIndices ? unchecked((uint)numKdIndices) - numSkipIndices : 0;
            // C: ufbx_assert(kd_slow_indices + face.num_indices * 2 <= num_indices) — the
            // caller's entry checks (ufbx.c:32405-32408) guarantee this.
            KdBuild(nc, indices, kdIndicesOff, indices, kdTmpOff, unchecked((uint)numKdIndices), 0, 0, 0);

            // C: uint32_t *edges = indices + num_indices - face.num_indices * 2;
            int edgesOff = (int)numIndices - (int)n * 2;

            // Initialize `edges` to be a connectivity structure where:
            //  `edges[2*i + 0]` is the previous vertex of `i`
            //  `edges[2*i + 1]` is the next vertex of `i`
            // When clipped we mark indices with the high bit (0x80000000)
            for (uint i = 0; i < n; i++) {
                indices[edgesOff + i * 2 + 0] = i > 0 ? i - 1 : n - 1;
                indices[edgesOff + i * 2 + 1] = i + 1 < n ? i + 1 : 0;
            }

            // Core of the ear clipping algorithm.
            // Iterate through the polygon corners looking for potential ears satisfying:
            //   - Angle must be less than 180deg
            //   - The triangle formed by the two edges must be contained within the polygon
            // As these properties change only locally between modifications we only need
            // to iterate the polygon once if we move backwards one step every time we clip an ear.
            uint indicesLeft = n;
            {
                uint[] pointIndices = { 0, 1, 2, 3 };
                double[] weights = new double[2];
                UfbxVec2[] points = new UfbxVec2[4];

                uint numSteps = 0;
                while (indicesLeft > 3) {
                    points[0] = NgonProject(nc, pointIndices[0]);
                    points[1] = NgonProject(nc, pointIndices[1]);
                    points[2] = NgonProject(nc, pointIndices[2]);
                    points[3] = NgonProject(nc, pointIndices[3]);

                    weights[0] = NgonTriWeight(points, 0);
                    weights[1] = NgonTriWeight(points, 1);

                    uint firstSide = weights[1] > weights[0] ? 1u : 0u;
                    bool clipped = false;
                    for (uint sideIx = 0; sideIx < 2; sideIx++) {
                        uint side = sideIx ^ firstSide;
                        if (!(weights[side] >= 0.0)) break;

                        // If there is no reflex angle contained within the triangle formed
                        // by `{ a, b, c }` connect the vertices `a - c` (prev, next) directly.
                        if (!KdCheck(nc, points, unchecked((int)side), pointIndices, unchecked((int)side))) {
                            uint ia = pointIndices[side + 0];
                            uint ib = pointIndices[side + 1];
                            uint ic = pointIndices[side + 2];

                            // Mark as clipped
                            indices[edgesOff + ib * 2 + 0] |= 0x80000000u;
                            indices[edgesOff + ib * 2 + 1] |= 0x80000000u;

                            indices[edgesOff + ic * 2 + 0] = ia;
                            indices[edgesOff + ia * 2 + 1] = ic;

                            indicesLeft -= 1;

                            // TODO: This may cause O(n^2) behavior!
                            numSteps = 0;

                            if (side == 1) {
                                pointIndices[2] = pointIndices[3];
                                pointIndices[3] = indices[edgesOff + pointIndices[3] * 2 + 1];
                            } else {
                                pointIndices[1] = pointIndices[0];
                                pointIndices[0] = indices[edgesOff + pointIndices[0] * 2 + 0];
                            }

                            clipped = true;
                            break;
                        }
                    }
                    if (clipped) continue;

                    // Continue forward
                    pointIndices[0] = pointIndices[1];
                    pointIndices[1] = pointIndices[2];
                    pointIndices[2] = pointIndices[3];
                    pointIndices[3] = indices[edgesOff + pointIndices[3] * 2 + 1];
                    numSteps++;

                    // If we have walked around the entire polygon it is irregular and
                    // ear cutting won't find any more triangles.
                    // TODO: This could be stricter?
                    if (numSteps >= n * 2) break;
                }

                // Fallback: Cut non-ears until the polygon is completed.
                // TODO: Could do something better here..
                uint ix = pointIndices[1];
                while (indicesLeft > 3) {
                    uint prev = indices[edgesOff + ix * 2 + 0];
                    uint next = indices[edgesOff + ix * 2 + 1];

                    // Mark as clipped
                    indices[edgesOff + ix * 2 + 0] |= 0x80000000u;
                    indices[edgesOff + ix * 2 + 1] |= 0x80000000u;

                    indices[edgesOff + prev * 2 + 1] = next;
                    indices[edgesOff + next * 2 + 0] = prev;

                    indicesLeft -= 1;
                    ix = next;
                }

                // Now we have a single triangle left at `ix`.
                indices[edgesOff + ix * 2 + 0] |= 0x80000000u;
                indices[edgesOff + ix * 2 + 1] |= 0x80000000u;
            }

            // Expand the adjacency information `edges` into proper triangles.
            // Care needs to be taken here as both refer to the same memory area:
            // The last 4 triangles may overlap in source and destination so we write
            // them to a stack buffer and copy them over in the end.
            uint maxTriangles = n - 2;
            uint numTriangles = 0, numLastTriangles = 0;
            uint[] lastTriangles = new uint[4 * 3];

            uint indexBegin = face.IndexBegin;
            for (uint ix = 0; ix < n; ix++) {
                uint prev = indices[edgesOff + ix * 2 + 0];
                uint next = indices[edgesOff + ix * 2 + 1];
                if ((prev & 0x80000000u) == 0) continue;

                // C: `uint32_t *dst = indices + num_triangles * 3;` then, when
                // `num_triangles + 4 >= max_triangles`, `dst` is REDIRECTED to
                // `last_triangles + num_last_triangles * 3` — a separate stack array.
                int dst;
                bool last = numTriangles + 4 >= maxTriangles;
                if (last) {
                    dst = (int)numLastTriangles * 3;
                    numLastTriangles++;
                } else {
                    dst = (int)numTriangles * 3;
                }

                uint[] dstBuf = last ? lastTriangles : indices;
                dstBuf[dst + 0] = indexBegin + (prev & 0x7fffffffu);
                dstBuf[dst + 1] = indexBegin + ix;
                dstBuf[dst + 2] = indexBegin + (next & 0x7fffffffu);
                numTriangles++;
            }

            // Copy over the last triangles
            // C: ufbx_assert(num_triangles == max_triangles);
            // C: memcpy(indices + (max_triangles - num_last_triangles) * 3, last_triangles, ...)
            Array.Copy(lastTriangles, 0, indices, (int)(maxTriangles - numLastTriangles) * 3, (int)numLastTriangles * 3);

            return numTriangles;
        }

        // ------------------------------------------------------------------
        // Topology (ufbx.c:28700-28827)
        // ------------------------------------------------------------------

        // C: ufbxi_topo_less_index_prev_next (ufbx.c:28700-28706) — int32_t compares.
        static bool TopoLessIndexPrevNext(object user, UfbxTopoEdge a, UfbxTopoEdge b)
        {
            if (unchecked((int)a.Prev) != unchecked((int)b.Prev)) return unchecked((int)a.Prev) < unchecked((int)b.Prev);
            return unchecked((int)a.Next) < unchecked((int)b.Next);
        }

        // C: ufbxi_topo_less_index_index (ufbx.c:28708-28713) — int32_t compares.
        static bool TopoLessIndexIndex(object user, UfbxTopoEdge a, UfbxTopoEdge b)
        {
            return unchecked((int)a.Index) < unchecked((int)b.Index);
        }

        // C: ufbxi_compute_topology (ufbx.c:28715-28792).
        static void ComputeTopologyImp(UfbxMesh mesh, UfbxTopoEdge[] topo)
        {
            int numIndices = mesh.NumIndices;

            // Temporarily use `prev` and `next` for vertices
            for (uint fi = 0; fi < (uint)mesh.NumFaces; fi++) {
                UfbxFace face = mesh.Faces[fi];
                for (uint pi = 0; pi < face.NumIndices; pi++) {
                    UfbxTopoEdge te = topo[face.IndexBegin + pi];
                    uint ni = (pi + 1) % face.NumIndices;
                    uint va = mesh.VertexIndices[face.IndexBegin + pi];
                    uint vb = mesh.VertexIndices[face.IndexBegin + ni];

                    if (vb < va) {
                        uint vt = va; va = vb; vb = vt;
                    }
                    te.Index = face.IndexBegin + pi;
                    te.Twin = NoIndex;
                    te.Edge = NoIndex;
                    te.Prev = va;
                    te.Next = vb;
                    te.Face = fi;
                    te.Flags = 0;
                    topo[face.IndexBegin + pi] = te;
                }
            }

            UfbxiSort.UnstableSort(topo, numIndices, TopoLessIndexPrevNext, null);

            if (mesh.Edges != null) {
                for (uint ei = 0; ei < (uint)mesh.NumEdges; ei++) {
                    UfbxEdge edge = mesh.Edges[ei];
                    uint va = mesh.VertexIndices[edge.A];
                    uint vb = mesh.VertexIndices[edge.B];
                    if (vb < va) {
                        uint vt = va; va = vb; vb = vt;
                    }

                    // C: ufbxi_macro_lower_bound_eq(ufbx_topo_edge, 32, &ix, topo, 0, num_indices,
                    //   (a->prev == va ? a->next < vb : a->prev < va), (a->prev == va && a->next == vb))
                    int ix = numIndices;
                    {
                        int lo = 0, hi = numIndices;
                        while (hi - lo > 32) {
                            int mid = lo + (hi - lo) / 2;
                            UfbxTopoEdge a = topo[mid];
                            if ((a.Prev == va ? unchecked((int)a.Next) < unchecked((int)vb) : unchecked((int)a.Prev) < unchecked((int)va))) {
                                lo = mid + 1;
                            } else {
                                hi = mid + 1;
                            }
                        }
                        for (; lo < hi; lo++) {
                            UfbxTopoEdge a = topo[lo];
                            if (a.Prev == va && a.Next == vb) { ix = lo; break; }
                        }
                    }

                    for (; ix < numIndices && topo[ix].Prev == va && topo[ix].Next == vb; ix++) {
                        UfbxTopoEdge te = topo[ix];
                        te.Edge = ei;
                        topo[ix] = te;
                    }
                }
            }

            // Connect paired edges
            for (int i0 = 0; i0 < numIndices; ) {
                int i1 = i0;

                uint a = topo[i0].Prev, b = topo[i0].Next;
                while (i1 + 1 < numIndices && topo[i1 + 1].Prev == a && topo[i1 + 1].Next == b) i1++;

                if (i1 == i0 + 1) {
                    topo[i0].Twin = topo[i1].Index;
                    topo[i1].Twin = topo[i0].Index;
                } else if (i1 > i0 + 1) {
                    for (int i = i0; i <= i1; i++) {
                        topo[i].Flags = unchecked((UfbxTopoFlags)((uint)topo[i].Flags | 1u)); // C: UFBX_TOPO_NON_MANIFOLD
                    }
                }

                i0 = i1 + 1;
            }

            UfbxiSort.UnstableSort(topo, numIndices, TopoLessIndexIndex, null);

            // Fix `prev` and `next` to the actual index values
            for (uint fi = 0; fi < (uint)mesh.NumFaces; fi++) {
                UfbxFace face = mesh.Faces[fi];
                for (uint i = 0; i < face.NumIndices; i++) {
                    UfbxTopoEdge to = topo[face.IndexBegin + i];
                    to.Prev = face.IndexBegin + (i + face.NumIndices - 1) % face.NumIndices;
                    to.Next = face.IndexBegin + (i + 1) % face.NumIndices;
                    topo[face.IndexBegin + i] = to;
                }
            }
        }

        // C: ufbxi_is_edge_smooth (ufbx.c:28794-28827).
        static bool IsEdgeSmooth(UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo, uint index, bool assumeSmooth)
        {
            if (mesh.EdgeSmoothing != null) {
                uint edge = topo[index].Edge;
                if (edge != NoIndex && mesh.EdgeSmoothing[edge]) return true;
            }

            if (mesh.FaceSmoothing != null) {
                if (mesh.FaceSmoothing[topo[index].Face]) return true;
                uint twin = topo[index].Twin;
                if (twin != NoIndex) {
                    if (mesh.FaceSmoothing[topo[twin].Face]) return true;
                }
            }

            if (mesh.EdgeSmoothing == null && mesh.FaceSmoothing == null && mesh.VertexNormal.Exists) {
                uint twin = topo[index].Twin;
                if (twin != NoIndex && mesh.VertexNormal.Exists) {
                    UfbxVec3 a0 = GetVertexVec3(mesh.VertexNormal, unchecked((int)index));
                    UfbxVec3 a1 = GetVertexVec3(mesh.VertexNormal, unchecked((int)topo[index].Next));
                    UfbxVec3 b0 = GetVertexVec3(mesh.VertexNormal, unchecked((int)topo[twin].Next));
                    UfbxVec3 b1 = GetVertexVec3(mesh.VertexNormal, unchecked((int)twin));
                    if (a0.X == b0.X && a0.Y == b0.Y && a0.Z == b0.Z) return true;
                    if (a1.X == b1.X && a1.Y == b1.Y && a1.Z == b1.Z) return true;
                }
            } else if (assumeSmooth) {
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Public API — catch forms (ufbx.c:32389-32626)
        // ------------------------------------------------------------------

        // C: ufbx_find_face_index (ufbx.c:32389-32398) is not ported here (find API).

        // C: ufbx_catch_triangulate_face (ufbx.c:32400-32483).
        internal static uint CatchTriangulateFace(ref UfbxPanic panic, uint[] indices, int numIndices, UfbxMesh mesh, UfbxFace face)
        {
            if (face.NumIndices < 3) return 0;

            int requiredIndices = ((int)face.NumIndices - 2) * 3;
            if (Panicf(ref panic, numIndices >= requiredIndices,
                "Face needs at least %zu indices for triangles, got space for %zu",
                new UfbxiVaList().AddSizeT(unchecked((ulong)requiredIndices)).AddSizeT(unchecked((ulong)numIndices)))) return 0;
            if (Panicf(ref panic, face.IndexBegin < (uint)mesh.NumIndices,
                "Face index begin (%u) out of bounds (%zu)",
                new UfbxiVaList().AddUInt(face.IndexBegin).AddSizeT(unchecked((ulong)mesh.NumIndices)))) return 0;
            if (Panicf(ref panic, (uint)mesh.NumIndices - face.IndexBegin >= face.NumIndices,
                "Face index end (%u + %u) out of bounds (%zu)",
                new UfbxiVaList().AddUInt(face.IndexBegin).AddUInt(face.NumIndices).AddSizeT(unchecked((ulong)mesh.NumIndices)))) return 0;

            if (face.NumIndices == 3) {
                // Fast case: Already a triangle
                indices[0] = face.IndexBegin + 0;
                indices[1] = face.IndexBegin + 1;
                indices[2] = face.IndexBegin + 2;
                return 1;
            } else if (face.NumIndices == 4) {
                // Quad: Split along the shortest axis unless a vertex crosses the axis
                uint i0 = face.IndexBegin + 0;
                uint i1 = face.IndexBegin + 1;
                uint i2 = face.IndexBegin + 2;
                uint i3 = face.IndexBegin + 3;
                UfbxVec3 v0 = mesh.VertexPosition.Values[AttribIndex(mesh.VertexPosition.Indices, (int)i0)];
                UfbxVec3 v1 = mesh.VertexPosition.Values[AttribIndex(mesh.VertexPosition.Indices, (int)i1)];
                UfbxVec3 v2 = mesh.VertexPosition.Values[AttribIndex(mesh.VertexPosition.Indices, (int)i2)];
                UfbxVec3 v3 = mesh.VertexPosition.Values[AttribIndex(mesh.VertexPosition.Indices, (int)i3)];

                UfbxVec3 a = UfbxVec3.Sub3(v2, v0);
                UfbxVec3 b = UfbxVec3.Sub3(v3, v1);

                UfbxVec3 na1 = UfbxVec3.Normalize3(UfbxVec3.Cross3(a, UfbxVec3.Sub3(v1, v0)));
                UfbxVec3 na3 = UfbxVec3.Normalize3(UfbxVec3.Cross3(a, UfbxVec3.Sub3(v0, v3)));
                UfbxVec3 nb0 = UfbxVec3.Normalize3(UfbxVec3.Cross3(b, UfbxVec3.Sub3(v1, v0)));
                UfbxVec3 nb2 = UfbxVec3.Normalize3(UfbxVec3.Cross3(b, UfbxVec3.Sub3(v2, v1)));

                double dotAa = UfbxVec3.Dot3(a, a);
                double dotBb = UfbxVec3.Dot3(b, b);
                double dotNa = UfbxVec3.Dot3(na1, na3);
                double dotNb = UfbxVec3.Dot3(nb0, nb2);

                bool splitA = dotAa <= dotBb;

                if (dotNa < 0.0 || dotNb < 0.0) {
                    splitA = dotNa >= dotNb;
                }

                if (splitA) {
                    indices[0] = i0;
                    indices[1] = i1;
                    indices[2] = i2;
                    indices[3] = i2;
                    indices[4] = i3;
                    indices[5] = i0;
                } else {
                    indices[0] = i1;
                    indices[1] = i2;
                    indices[2] = i3;
                    indices[3] = i3;
                    indices[4] = i0;
                    indices[5] = i1;
                }

                return 2;
            } else {
                NgonContext nc = new NgonContext();
                nc.Positions = mesh.VertexPosition;
                nc.CurFace = face;

                uint numIndicesU32 = numIndices < int.MaxValue ? unchecked((uint)numIndices) : uint.MaxValue; // C: < UINT32_MAX

                if (numIndicesU32 < 12) {
                    // C: uint32_t local_indices[12] — the buffer IS the output, so the local
                    // path needs a copy back into the caller's (shorter) array.
                    uint[] localIndices = new uint[12];
                    uint[] callerIndices = indices;
                    nc.Buf = localIndices;
                    uint numTris = TriangulateNgon(nc, 12);
                    Array.Copy(localIndices, callerIndices, (int)numTris * 3);
                    return numTris;
                } else {
                    nc.Buf = indices;
                    return TriangulateNgon(nc, numIndicesU32);
                }
            }
        }

        // C: ufbx_catch_compute_topology (ufbx.c:32485-32490).
        internal static void CatchComputeTopology(ref UfbxPanic panic, UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo)
        {
            if (Panicf(ref panic, numTopo >= mesh.NumIndices,
                "Required mesh.num_indices (%zu) indices, got %zu",
                new UfbxiVaList().AddSizeT(unchecked((ulong)mesh.NumIndices)).AddSizeT(unchecked((ulong)numTopo)))) return;

            ComputeTopologyImp(mesh, topo);
        }

        // C: ufbx_catch_topo_next_vertex_edge (ufbx.c:32492-32500).
        internal static uint CatchTopoNextVertexEdge(ref UfbxPanic panic, UfbxTopoEdge[] topo, int numTopo, uint index)
        {
            if (index == NoIndex) return NoIndex;
            if (Panicf(ref panic, (int)index < numTopo,
                "index (%u) out of bounds (%zu)",
                new UfbxiVaList().AddUInt(index).AddSizeT(unchecked((ulong)numTopo)))) return NoIndex;
            uint twin = topo[index].Twin;
            if (twin == NoIndex) return NoIndex;
            if (Panicf(ref panic, (int)twin < numTopo, "Corrupted topology structure", new UfbxiVaList())) return NoIndex;
            return topo[twin].Next;
        }

        // C: ufbx_catch_topo_prev_vertex_edge (ufbx.c:32502-32507).
        internal static uint CatchTopoPrevVertexEdge(ref UfbxPanic panic, UfbxTopoEdge[] topo, int numTopo, uint index)
        {
            if (index == NoIndex) return NoIndex;
            if (Panicf(ref panic, (int)index < numTopo,
                "index (%u) out of bounds (%zu)",
                new UfbxiVaList().AddUInt(index).AddSizeT(unchecked((ulong)numTopo)))) return NoIndex;
            return topo[topo[index].Prev].Twin;
        }

        // C: ufbx_catch_get_weighted_face_normal (ufbx.c:32509-32540).
        // `numIndicesCount` is C's `positions->indices.count` (the LOGICAL count; sentinel
        // buffers carry an implied count, see file header). Pass -1 to use the array length.
        internal static UfbxVec3 CatchGetWeightedFaceNormal(ref UfbxPanic panic, UfbxVertexVec3 positions, UfbxFace face, int indicesCount)
        {
            if (indicesCount < 0) {
                indicesCount = IndicesCount(positions.Indices);
            }
            if (Panicf(ref panic, face.IndexBegin <= (uint)indicesCount,
                "Face index begin (%u) out of bounds (%zu)",
                new UfbxiVaList().AddUInt(face.IndexBegin).AddSizeT(unchecked((ulong)indicesCount)))) return default;
            if (Panicf(ref panic, (uint)indicesCount - face.IndexBegin >= face.NumIndices,
                "Face index end (%u + %u) out of bounds (%zu)",
                new UfbxiVaList().AddUInt(face.IndexBegin).AddUInt(face.NumIndices).AddSizeT(unchecked((ulong)indicesCount)))) return default;

            return CatchGetWeightedFaceNormalCore(ref panic, positions, face);
        }

        static bool IsSentinelIndices(uint[] indices)
        {
            return ReferenceEquals(indices, UfbxiGeometry.SentinelIndexZero)
                || ReferenceEquals(indices, UfbxiGeometry.SentinelIndexConsecutive)
                || ReferenceEquals(indices, UfbxiObjects.SentinelIndexZero)
                || ReferenceEquals(indices, UfbxiObjects.SentinelIndexConsecutive);
        }

        // C: `v->indices.count` (the LOGICAL count; sentinel buffers carry an implied count).
        // A non-existent attribute has NULL `values`/`indices`, i.e. count 0.
        static int IndicesCount(uint[] indices)
        {
            if (indices == null) return 0;
            return IsSentinelIndices(indices) ? int.MaxValue : indices.Length;
        }

        // C: `v->values.count` (0 for the NULL list of a non-existent attribute).
        static int ValueCount<T>(T[] values) => values == null ? 0 : values.Length;

        // The check-free body shared by the catch form and internal callers.
        static UfbxVec3 CatchGetWeightedFaceNormalCore(ref UfbxPanic panic, UfbxVertexVec3 positions, UfbxFace face)
        {
            if (face.NumIndices < 3) {
                return default;
            } else if (face.NumIndices == 3) {
                UfbxVec3 a = GetVertexVec3(positions, (int)(face.IndexBegin + 0));
                UfbxVec3 b = GetVertexVec3(positions, (int)(face.IndexBegin + 1));
                UfbxVec3 c = GetVertexVec3(positions, (int)(face.IndexBegin + 2));
                return UfbxVec3.Cross3(UfbxVec3.Sub3(b, a), UfbxVec3.Sub3(c, a));
            } else if (face.NumIndices == 4) {
                UfbxVec3 a = GetVertexVec3(positions, (int)(face.IndexBegin + 0));
                UfbxVec3 b = GetVertexVec3(positions, (int)(face.IndexBegin + 1));
                UfbxVec3 c = GetVertexVec3(positions, (int)(face.IndexBegin + 2));
                UfbxVec3 d = GetVertexVec3(positions, (int)(face.IndexBegin + 3));
                return UfbxVec3.Cross3(UfbxVec3.Sub3(c, a), UfbxVec3.Sub3(d, b));
            } else {
                // Newell's Method
                UfbxVec3 result = default;
                for (uint i = 0; i < face.NumIndices; i++) {
                    uint next = i + 1 < face.NumIndices ? i + 1 : 0;
                    UfbxVec3 a = GetVertexVec3(positions, (int)(face.IndexBegin + i));
                    UfbxVec3 b = GetVertexVec3(positions, (int)(face.IndexBegin + next));
                    result.X += (a.Y - b.Y) * (a.Z + b.Z);
                    result.Y += (a.Z - b.Z) * (a.X + b.X);
                    result.Z += (a.X - b.X) * (a.Y + b.Y);
                }
                return result;
            }
        }

        // C: ufbx_catch_generate_normal_mapping (ufbx.c:32542-32586).
        internal static int CatchGenerateNormalMapping(ref UfbxPanic panic, UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo, uint[] normalIndices, int numNormalIndices, bool assumeSmooth)
        {
            uint nextIndex = 0;
            if (Panicf(ref panic, numNormalIndices >= mesh.NumIndices,
                "Expected at least mesh.num_indices (%zu), got %zu",
                new UfbxiVaList().AddSizeT(unchecked((ulong)mesh.NumIndices)).AddSizeT(unchecked((ulong)numNormalIndices)))) return 0;

            for (int i = 0; i < mesh.NumIndices; i++) {
                normalIndices[i] = NoIndex;
            }

            // Walk around vertices and merge around smooth edges
            for (uint vi = 0; vi < (uint)mesh.NumVertices; vi++) {
                uint originalStart = mesh.VertexFirstIndex[vi];
                if (originalStart == NoIndex) continue;
                uint start = originalStart, cur = start;

                for (;;) {
                    uint prev = CatchTopoNextVertexEdge(ref panic, topo, numTopo, cur);
                    if (!IsEdgeSmooth(mesh, topo, numTopo, cur, assumeSmooth)) start = cur;
                    if (prev == NoIndex) { start = cur; break; }
                    if (prev == originalStart) break;
                    cur = prev;
                }

                normalIndices[start] = nextIndex++;
                uint next = start;
                for (;;) {
                    next = CatchTopoPrevVertexEdge(ref panic, topo, numTopo, next);
                    if (next == NoIndex || next == start) break;

                    if (!IsEdgeSmooth(mesh, topo, numTopo, next, assumeSmooth)) {
                        ++nextIndex;
                    }
                    normalIndices[next] = nextIndex - 1;
                }
            }

            // Assign non-manifold indices
            for (int i = 0; i < mesh.NumIndices; i++) {
                if (normalIndices[i] == NoIndex) {
                    normalIndices[i] = nextIndex++;
                }
            }

            return unchecked((int)nextIndex);
        }

        // C: ufbx_catch_compute_normals (ufbx.c:32593-32620).
        internal static void CatchComputeNormals(ref UfbxPanic panic, UfbxMesh mesh, UfbxVertexVec3 positions, uint[] normalIndices, int numNormalIndices, UfbxVec3[] normals, int numNormals)
        {
            if (Panicf(ref panic, numNormalIndices >= mesh.NumIndices,
                "Expected at least mesh.num_indices (%zu), got %zu",
                new UfbxiVaList().AddSizeT(unchecked((ulong)mesh.NumIndices)).AddSizeT(unchecked((ulong)numNormalIndices)))) return;

            for (int i = 0; i < numNormals; i++) normals[i] = default;

            for (uint fi = 0; fi < (uint)mesh.NumFaces; fi++) {
                UfbxFace face = mesh.Faces[fi];
                // C: ufbx_get_weighted_face_normal(positions, face) (ufbx.c:32601) — the
                // plain form (panic == NULL), i.e. the checks run against a scratch panic
                // and a failure yields the zero vector without poisoning the outer panic.
                UfbxPanic facePanic = default;
                UfbxVec3 normal = CatchGetWeightedFaceNormal(ref facePanic, positions, face, -1);
                for (uint ix = 0; ix < face.NumIndices; ix++) {
                    uint index = normalIndices[face.IndexBegin + ix];

                    if (Panicf(ref panic, index < (uint)numNormals,
                        "Normal index (%u) out of bounds (%zu) at %zu",
                        new UfbxiVaList().AddUInt(index).AddSizeT(unchecked((ulong)numNormals)).AddSizeT(unchecked((ulong)ix)))) return;

                    UfbxVec3 n = normals[index];
                    normals[index] = UfbxVec3.Add3(n, normal);
                }
            }

            for (int i = 0; i < numNormals; i++) {
                double len = UfbxVec3.Length3(normals[i]);
                if (len > 0.0) {
                    normals[i].X /= len;
                    normals[i].Y /= len;
                    normals[i].Z /= len;
                }
            }
        }

        // ------------------------------------------------------------------
        // Public API — single-index vertex accessors (ufbx.c:33001-33040)
        // ------------------------------------------------------------------

        // C: ufbx_catch_get_vertex_real (ufbx.c:33001-33007).
        internal static double CatchGetVertexReal(ref UfbxPanic panic, UfbxVertexReal v, int index)
        {
            int indicesCount = IndicesCount(v.Indices);
            if (Panicf(ref panic, index < indicesCount, "index (%zu) out of range (%zu)",
                new UfbxiVaList().AddSizeT(unchecked((ulong)index)).AddSizeT(unchecked((ulong)indicesCount)))) return 0.0;
            uint ix = AttribIndex(v.Indices, index);
            if (Panicf(ref panic, ix < (uint)ValueCount(v.Values) || ix == NoIndex,
                "Corrupted or missing vertex attribute (%u) at %zu",
                new UfbxiVaList().AddUInt(ix).AddSizeT(unchecked((ulong)index)))) return 0.0;
            // C: `values.data[(int32_t)UFBX_NO_INDEX]` is the zero-guard element (see header).
            if (ix == NoIndex) return 0.0;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_catch_get_vertex_vec2 (ufbx.c:33009-33015).
        internal static UfbxVec2 CatchGetVertexVec2(ref UfbxPanic panic, UfbxVertexVec2 v, int index)
        {
            int indicesCount = IndicesCount(v.Indices);
            if (Panicf(ref panic, index < indicesCount, "index (%zu) out of range (%zu)",
                new UfbxiVaList().AddSizeT(unchecked((ulong)index)).AddSizeT(unchecked((ulong)indicesCount)))) return default;
            uint ix = AttribIndex(v.Indices, index);
            if (Panicf(ref panic, ix < (uint)ValueCount(v.Values) || ix == NoIndex,
                "Corrupted or missing vertex attribute (%u) at %zu",
                new UfbxiVaList().AddUInt(ix).AddSizeT(unchecked((ulong)index)))) return default;
            if (ix == NoIndex) return default;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_catch_get_vertex_vec3 (ufbx.c:33017-33023).
        internal static UfbxVec3 CatchGetVertexVec3(ref UfbxPanic panic, UfbxVertexVec3 v, int index)
        {
            int indicesCount = IndicesCount(v.Indices);
            if (Panicf(ref panic, index < indicesCount, "index (%zu) out of range (%zu)",
                new UfbxiVaList().AddSizeT(unchecked((ulong)index)).AddSizeT(unchecked((ulong)indicesCount)))) return default;
            uint ix = AttribIndex(v.Indices, index);
            if (Panicf(ref panic, ix < (uint)ValueCount(v.Values) || ix == NoIndex,
                "Corrupted or missing vertex attribute (%u) at %zu",
                new UfbxiVaList().AddUInt(ix).AddSizeT(unchecked((ulong)index)))) return default;
            if (ix == NoIndex) return default;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_catch_get_vertex_vec4 (ufbx.c:33025-33031).
        internal static UfbxVec4 CatchGetVertexVec4(ref UfbxPanic panic, UfbxVertexVec4 v, int index)
        {
            int indicesCount = IndicesCount(v.Indices);
            if (Panicf(ref panic, index < indicesCount, "index (%zu) out of range (%zu)",
                new UfbxiVaList().AddSizeT(unchecked((ulong)index)).AddSizeT(unchecked((ulong)indicesCount)))) return default;
            uint ix = AttribIndex(v.Indices, index);
            if (Panicf(ref panic, ix < (uint)ValueCount(v.Values) || ix == NoIndex,
                "Corrupted or missing vertex attribute (%u) at %zu",
                new UfbxiVaList().AddUInt(ix).AddSizeT(unchecked((ulong)index)))) return default;
            if (ix == NoIndex) return default;
            return v.Values[unchecked((int)ix)];
        }

        // C: ufbx_catch_get_vertex_w_vec3 (ufbx.c:33033-33040). Note that C bounds-checks
        // `ix` against `values.count` (the 3D list) while reading `values_w.data[ix]`.
        internal static double CatchGetVertexWVec3(ref UfbxPanic panic, UfbxVertexVec3 v, int index)
        {
            int indicesCount = IndicesCount(v.Indices);
            if (Panicf(ref panic, index < indicesCount, "index (%zu) out of range (%zu)",
                new UfbxiVaList().AddSizeT(unchecked((ulong)index)).AddSizeT(unchecked((ulong)indicesCount)))) return 0.0;
            if (v.ValuesW == null || v.ValuesW.Length == 0) return 0.0;
            uint ix = AttribIndex(v.Indices, index);
            // C: ufbx.c:33038 bounds `ix` against `values.count` while reading `values_w.data[ix]`.
            if (Panicf(ref panic, ix < (uint)ValueCount(v.Values) || ix == NoIndex,
                "Corrupted or missing vertex attribute (%u) at %zu",
                new UfbxiVaList().AddUInt(ix).AddSizeT(unchecked((ulong)index)))) return 0.0;
            if (ix == NoIndex) return 0.0;
            return v.ValuesW[unchecked((int)ix)];
        }

        // ------------------------------------------------------------------
        // Public API — plain forms (ufbx.c:32588-32625, 33173-33187)
        // ------------------------------------------------------------------

        // C: ufbx_triangulate_face (ufbx.c:33173-33175).
        internal static uint TriangulateFace(uint[] indices, int numIndices, UfbxMesh mesh, UfbxFace face)
        {
            UfbxPanic panic = default;
            return CatchTriangulateFace(ref panic, indices, numIndices, mesh, face);
        }

        // C: ufbx_compute_topology (ufbx.c:33176-33178).
        internal static void ComputeTopology(UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo)
        {
            UfbxPanic panic = default;
            CatchComputeTopology(ref panic, mesh, topo, numTopo);
        }

        // C: ufbx_topo_next_vertex_edge (ufbx.c:33179-33181).
        internal static uint TopoNextVertexEdge(UfbxTopoEdge[] topo, int numTopo, uint index)
        {
            UfbxPanic panic = default;
            return CatchTopoNextVertexEdge(ref panic, topo, numTopo, index);
        }

        // C: ufbx_topo_prev_vertex_edge (ufbx.c:33182-33184).
        internal static uint TopoPrevVertexEdge(UfbxTopoEdge[] topo, int numTopo, uint index)
        {
            UfbxPanic panic = default;
            return CatchTopoPrevVertexEdge(ref panic, topo, numTopo, index);
        }

        // C: ufbx_get_weighted_face_normal (ufbx.c:33185-33187).
        internal static UfbxVec3 GetWeightedFaceNormal(UfbxVertexVec3 positions, UfbxFace face)
        {
            UfbxPanic panic = default;
            return CatchGetWeightedFaceNormal(ref panic, positions, face, -1);
        }

        // C: ufbx_generate_normal_mapping (ufbx.c:32588-32591).
        internal static int GenerateNormalMapping(UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo, uint[] normalIndices, int numNormalIndices, bool assumeSmooth)
        {
            UfbxPanic panic = default;
            return CatchGenerateNormalMapping(ref panic, mesh, topo, numTopo, normalIndices, numNormalIndices, assumeSmooth);
        }

        // C: ufbx_compute_normals (ufbx.c:32622-32625).
        internal static void ComputeNormals(UfbxMesh mesh, UfbxVertexVec3 positions, uint[] normalIndices, int numNormalIndices, UfbxVec3[] normals, int numNormals)
        {
            UfbxPanic panic = default;
            CatchComputeNormals(ref panic, mesh, positions, normalIndices, numNormalIndices, normals, numNormals);
        }

        // ------------------------------------------------------------------
        // Face lookup (ufbx.c:32389-32398)
        // ------------------------------------------------------------------

        // C: ufbx_find_face_index (ufbx.c:32389-32398). `index` is C's `size_t`, and this is the
        // only entry point in the group whose guard needs more than `int`: `index > UINT32_MAX`
        // returns `UFBX_NO_INDEX` without touching the mesh, so `long` is what makes that branch
        // expressible. `face_ix` starts at C's `SIZE_MAX` and the macro leaves it there when no
        // face spans the index, which is `UFBX_NO_INDEX` again after the truncating cast.
        //
        // The `4` is the macro's `m_linear_size` (an element count, not a byte size);
        // `ufbxi_clamp_linear_threshold()` (ufbx.c:994-998) rewrites it to 2 only under
        // `UFBX_DEBUG_BINARY_SEARCH`/`UFBX_REGRESSION`, which the reference build does not define.
        //
        // The guard compares as `ulong` so it means the same thing as C's unsigned `size_t`
        // comparison: every `long` outside `[0, UINT32_MAX] -- which includes `-1`, the value C sees
        // as `SIZE_MAX` -- is rejected before the mesh is touched.
        internal static uint FindFaceIndex(UfbxMesh mesh, long index)
        {
            if (mesh == null || unchecked((ulong)index) > uint.MaxValue) return NoIndex;
            uint ix = unchecked((uint)index);

            UfbxFace[] faces = mesh.Faces ?? Array.Empty<UfbxFace>();

            int faceIx = -1; // C: SIZE_MAX
            UfbxiSceneBuild.LowerBoundEq(faces, 0, faces.Length, 4,
                a => unchecked(a.IndexBegin + a.NumIndices) <= ix,
                a => ix >= a.IndexBegin && ix < unchecked(a.IndexBegin + a.NumIndices),
                ref faceIx);

            return unchecked((uint)faceIx);
        }
    }
}
