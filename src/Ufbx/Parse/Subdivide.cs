// S4c P1: NURBS evaluation / tessellation / mesh subdivision / index generation,
// ported from ufbx.c v0.23.1. Owned by the S4c porting agent (2026-10-03 wave).
//
// Covered C functions (ufbx.c line ranges in per-function comments):
//   ufbxi_nurbs_weight (27779), ufbxi_nurbs_deriv (27790),
//   ufbx_evaluate_nurbs_basis (32105), ufbx_evaluate_nurbs_curve (32176),
//   ufbx_evaluate_nurbs_surface (32222),
//   ufbxi_generate_indices (30115) / ufbx_generate_indices (32974),
//   ufbxi_finalize_mesh_material (21564), ufbxi_mesh_part_add_face (13251),
//   ufbxi_material_part_usage_less (21551),
//   ufbxi_subdivide_sum_vec2/3/4 (28899/28914/28930),
//   ufbxi_subdivision_weight_less (28947), ufbxi_subdivide_sum_vertex_weights (28956),
//   ufbxi_is_edge_split (29022), ufbxi_edge_crease (29044),
//   ufbxi_subdivide_layer (29052), ufbxi_subdivide_attrib (29472),
//   ufbxi_subdivision_copy_weights (29499), ufbxi_init_source_vertex_weights (29513),
//   ufbxi_init_skin_weights (29529), ufbxi_subdivide_weights (29556),
//   ufbxi_subdivide_vertex_crease (29604), ufbxi_subdivide_mesh_level (29639),
//   ufbxi_subdivide_mesh_imp (29935), ufbxi_subdivide_mesh (30044) / ufbx_subdivide_mesh (32627),
//   ufbxi_tessellate_nurbs_curve_imp (27848) / ufbx_tessellate_nurbs_curve (32290),
//   ufbxi_tessellate_nurbs_surface_imp (27941) / ufbx_tessellate_nurbs_surface (32328),
//   ufbx_free_mesh (32635) / ufbx_retain_mesh (32653).
//
// Representation notes:
//  - C's arena allocators (`ufbxi_buf` result/tmp/source, `ufbxi_allocator`) become plain
//    managed arrays; every `ufbxi_push` is a `new T[...]` and allocation failure paths are
//    not representable (documented divergence: OOM throws OutOfMemoryException instead of
//    flowing `ufbx_error`).
//  - C zero-guards tessellated/subdivided attribute arrays by pushing one extra zero
//    element *before* the data (`*positions++ = ufbx_zero_vec3`, ufbx.c:28003-28007;
//    `memset(new_values, 0, stride)`, ufbx.c:29439). The port allocates the data-only
//    array; NO_INDEX reads are handled by the accessor contract documented in
//    Parse/Topology.cs (subdivided/tessellated indices are never NO_INDEX).
//  - `*result = *mesh` (ufbx.c:29644) is a per-field shallow copy: C copies list pointers
//    and the embedded element header by value, so sharing the array references matches.
//  - The `ufbxi_map` of ufbxi_generate_indices is replaced by Dictionary<byte[], int> with
//    content equality. Output semantics are independent of hashing/probing: the mapped
//    index of a distinct vertex is `map.size++` at first insertion (ufbx.c:4632
//    `uint32_t index = map->size++`, returned as `(entry - items) / packed_size` where
//    items are stored compactly in insertion order, ufbx.c:4537), and the map
//    `aa_tree` overflow branch (scan > UFBXI_MAP_MAX_SCAN, ufbx.c:4652) indexes the same
//    insertion order. Only the entries[] slot layout is hash dependent, which is never
//    observable.
//  - Refcount plumbing (`ufbxi_init_ref`/`ufbxi_release_ref`, imp magics, arena
//    statistics in `ufbx_subdivision_result.result_memory_used`/`...allocs`) has no
//    managed counterpart: GC owns the lifetime, so those fields stay zero and
//    ufbx_free_mesh/ufbx_retain_mesh are no-ops (ufbx.c:32635-32655).
//  - ufbx_subdivision_result is a value struct in C (embedded in the mesh, ufbx.h), the
//    port models it as a class instance assigned to UfbxMesh.SubdivisionResult, matching
//    `result->subdivision_result = result_sub` (ufbx.c:29714-29716).
//  - Error sites: `ufbxi_check_err` (plain) -> UfbxiFail.CheckNoDesc, msg sites ->
//    UfbxiFail.CheckMsg, verbatim strings; the public entry points perform C's failure
//    tail `ufbxi_fix_error_type(&sc.error, "Failed to ...", error)` (ufbx.c:30069,
//    32313, 32365ish) in their catch block.
//
// Floating point: expressions are transcribed in C's evaluation order; no System.Math.
// C float literals (0.1f, 0.999f, 0.25f, ...) are kept as C# float literals so the
// promoted double values are bit-identical; `* 10.0` uses double 10.0 like C's
// `(ufbx_real)10.0`. NOTE: ufbx.c:29621 uses `0.1f` (float) but ufbx.c:29828 uses
// `(ufbx_real)0.1` (double) -- preserved exactly as written.

using System;
using System.Collections.Generic;

namespace Ufbx
{
    internal static class UfbxiSubdivide
    {
        // C: UFBXI_MAX_NURBS_ORDER (ufbx.c:64-65).
        internal const int MaxNurbsOrder = 128;

        // C: UFBX_NO_INDEX (ufbx.h:396).
        internal const uint NoIndex = UfbxConstants.NoIndex;

        // ==================================================================
        // NURBS basis evaluation (ufbx.c:32105-32288)
        // ==================================================================

        // C: ufbxi_nurbs_weight (ufbx.c:27779-27788).
        static double NurbsWeight(double[] knots, ulong knot, ulong degree, double u)
        {
            int count = knots.Length;
            if (knot >= (ulong)count) return 0.0;
            if ((ulong)count - knot < degree) return 0.0;
            double prevU = knots[(int)knot], nextU = knots[(int)(knot + degree)];
            if (prevU >= nextU) return 0.0;
            if (u <= prevU) return 0.0;
            if (u >= nextU) return 1.0;
            return (u - prevU) / (nextU - prevU);
        }

        // C: ufbxi_nurbs_deriv (ufbx.c:27790-27797).
        static double NurbsDeriv(double[] knots, ulong knot, ulong degree)
        {
            int count = knots.Length;
            if (knot >= (ulong)count) return 0.0;
            if ((ulong)count - knot < degree) return 0.0;
            double prevU = knots[(int)knot], nextU = knots[(int)(knot + degree)];
            if (prevU >= nextU) return 0.0;
            return (double)degree / (nextU - prevU);
        }

        // C: ufbx_evaluate_nurbs_basis (ufbx.c:32105-32174).
        //
        // Returns `knot - degree` (the base control point) or SIZE_MAX on invalid input.
        // The knot span search uses ufbxi_macro_lower_bound_eq (ufbx.c:1188-1204, linear
        // size 8): binary search on `a[1] <= u`, then linear scan for
        // `a[0] <= u && u < a[1]`. If no span matches, the macro leaves `knot` at its
        // initializer SIZE_MAX (ufbx.c:32117) and C proceeds with the wrapped
        // `knot - degree` (size_t arithmetic): `SIZE_MAX < degree` is false (unsigned), so
        // the function runs the recurrence with out-of-range knots (all weights 0.0) and
        // returns the wrapped value. The port keeps `knot` in ulong to reproduce the
        // wraparound bit-exactly.
        //
        // `num_weights < basis.order` or `weights == null` return the base WITHOUT writing
        // weights (ufbx.c:32136-32138). Derivatives are only computed when
        // `p == degree` (the last pass) and only written up to `num_derivatives`.
        internal static ulong EvaluateNurbsBasis(UfbxNurbsBasis basis, double u, double[] weights, int numWeights, double[] derivatives, int numDerivatives)
        {
            // C: `if (!basis) return SIZE_MAX` -- the port's struct cannot be null.
            if (basis.Order == 0) return ulong.MaxValue;    // C: SIZE_MAX
            if (!basis.Valid) return ulong.MaxValue;

            ulong degree = basis.Order - 1;

            double[] knots = basis.KnotVector;
            ulong knot = ulong.MaxValue;

            if (u <= basis.TMin) {
                knot = degree;
                u = basis.TMin;
            } else if (u >= basis.TMax) {
                knot = (ulong)knots.Length - degree - 2;
                u = basis.TMax;
            } else {
                // C: ufbxi_macro_lower_bound_eq(ufbx_real, 8, &knot, knots.data,
                //     0, knots.count - 1, (a[1] <= u), (a[0] <= u && u < a[1])).
                ulong lo = 0, hi = (ulong)knots.Length - 1;
                while (hi - lo > 8) {
                    ulong mid = lo + (hi - lo) / 2;
                    if (knots[(int)(mid + 1)] <= u) { lo = mid + 1; } else { hi = mid + 1; }
                }
                for (; lo < hi; lo++) {
                    if (knots[(int)lo] <= u && u < knots[(int)(lo + 1)]) { knot = lo; break; }
                }
            }

            if (knot < degree) return ulong.MaxValue;

            if (numDerivatives == 0) derivatives = null;
            if (numWeights < basis.Order) return knot - degree;
            if (weights == null) return knot - degree;

            weights[0] = 1.0;
            for (ulong p = 1; p <= degree; p++) {

                double prev = 0.0;
                double g = 1.0 - NurbsWeight(knots, knot - p + 1, p, u);
                double dg = 0.0;
                if (derivatives != null && p == degree) {
                    dg = NurbsDeriv(knots, knot - p + 1, p);
                }

                for (ulong i = p; i > 0; i--) {
                    double f = NurbsWeight(knots, knot - p + i, p, u);
                    double weight = weights[(int)(i - 1)];
                    weights[(int)i] = f * weight + g * prev;

                    if (derivatives != null && p == degree) {
                        double df = NurbsDeriv(knots, knot - p + i, p);
                        if (i < (ulong)numDerivatives) {
                            derivatives[(int)i] = df * weight - dg * prev;
                        }
                        dg = df;
                    }

                    prev = weight;
                    g = 1.0 - f;
                }

                weights[0] = g * prev;
                if (derivatives != null && p == degree) {
                    derivatives[0] = -dg * prev;
                }
            }

            return knot - degree;
        }

        // C: ufbx_evaluate_nurbs_curve (ufbx.c:32176-32220).
        internal static UfbxCurvePoint EvaluateNurbsCurve(UfbxNurbsCurve curve, double u)
        {
            UfbxCurvePoint result = default;

            if (curve == null) return result;

            double[] weights = new double[MaxNurbsOrder];   // C: ufbx_real weights[UFBXI_MAX_NURBS_ORDER]
            double[] derivs = new double[MaxNurbsOrder];
            ulong baseIx = EvaluateNurbsBasis(curve.Basis, u, weights, MaxNurbsOrder, derivs, MaxNurbsOrder);
            if (baseIx == ulong.MaxValue) return result;

            UfbxVec4 p = default;
            UfbxVec4 d = default;

            int order = (int)curve.Basis.Order;
            if (order > MaxNurbsOrder) return result;
            if (curve.ControlPoints.Length == 0) return result;

            for (int i = 0; i < order; i++) {
                int ix = (int)((baseIx + (ulong)i) % (ulong)curve.ControlPoints.Length);
                UfbxVec4 cp = curve.ControlPoints[ix];
                double weight = weights[i] * cp.W, deriv = derivs[i] * cp.W;

                p.X += cp.X * weight;
                p.Y += cp.Y * weight;
                p.Z += cp.Z * weight;
                p.W += weight;

                d.X += cp.X * deriv;
                d.Y += cp.Y * deriv;
                d.Z += cp.Z * deriv;
                d.W += deriv;
            }

            double rcpW = 1.0 / p.W;
            result.Valid = true;
            result.Position.X = p.X * rcpW;
            result.Position.Y = p.Y * rcpW;
            result.Position.Z = p.Z * rcpW;
            result.Derivative.X = (d.X - d.W * result.Position.X) * rcpW;
            result.Derivative.Y = (d.Y - d.W * result.Position.Y) * rcpW;
            result.Derivative.Z = (d.Z - d.W * result.Position.Z) * rcpW;
            return result;
        }

        // C: ufbx_evaluate_nurbs_surface (ufbx.c:32222-32288).
        internal static UfbxSurfacePoint EvaluateNurbsSurface(UfbxNurbsSurface surface, double u, double v)
        {
            UfbxSurfacePoint result = default;

            if (surface == null) return result;

            double[] weightsU = new double[MaxNurbsOrder], weightsV = new double[MaxNurbsOrder];
            double[] derivsU = new double[MaxNurbsOrder], derivsV = new double[MaxNurbsOrder];
            ulong baseU = EvaluateNurbsBasis(surface.BasisU, u, weightsU, MaxNurbsOrder, derivsU, MaxNurbsOrder);
            ulong baseV = EvaluateNurbsBasis(surface.BasisV, v, weightsV, MaxNurbsOrder, derivsV, MaxNurbsOrder);
            if (baseU == ulong.MaxValue || baseV == ulong.MaxValue) return result;

            UfbxVec4 p = default;
            UfbxVec4 du = default;
            UfbxVec4 dv = default;

            int numU = surface.NumControlPointsU;
            int numV = surface.NumControlPointsV;
            int orderU = (int)surface.BasisU.Order;
            int orderV = (int)surface.BasisV.Order;
            if (orderU > MaxNurbsOrder || orderV > MaxNurbsOrder) return result;
            if (numU == 0 || numV == 0) return result;

            for (int vi = 0; vi < orderV; vi++) {
                int vix = (int)((baseV + (ulong)vi) % (ulong)numV);
                double weightV = weightsV[vi], derivV = derivsV[vi];

                for (int ui = 0; ui < orderU; ui++) {
                    int uix = (int)((baseU + (ulong)ui) % (ulong)numU);
                    double weightU = weightsU[ui], derivU = derivsU[ui];
                    UfbxVec4 cp = surface.ControlPoints[vix * numU + uix];

                    double weight = weightU * weightV * cp.W;
                    double wderivU = derivU * weightV * cp.W;
                    double wderivV = derivV * weightU * cp.W;

                    p.X += cp.X * weight;
                    p.Y += cp.Y * weight;
                    p.Z += cp.Z * weight;
                    p.W += weight;

                    du.X += cp.X * wderivU;
                    du.Y += cp.Y * wderivU;
                    du.Z += cp.Z * wderivU;
                    du.W += wderivU;

                    dv.X += cp.X * wderivV;
                    dv.Y += cp.Y * wderivV;
                    dv.Z += cp.Z * wderivV;
                    dv.W += wderivV;
                }
            }

            double rcpW = 1.0 / p.W;
            result.Valid = true;
            result.Position.X = p.X * rcpW;
            result.Position.Y = p.Y * rcpW;
            result.Position.Z = p.Z * rcpW;
            result.DerivativeU.X = (du.X - du.W * result.Position.X) * rcpW;
            result.DerivativeU.Y = (du.Y - du.W * result.Position.Y) * rcpW;
            result.DerivativeU.Z = (du.Z - du.W * result.Position.Z) * rcpW;
            result.DerivativeV.X = (dv.X - dv.W * result.Position.X) * rcpW;
            result.DerivativeV.Y = (dv.Y - dv.W * result.Position.Y) * rcpW;
            result.DerivativeV.Z = (dv.Z - dv.W * result.Position.Z) * rcpW;
            return result;
        }

        // ==================================================================
        // Index generation (ufbx.c:30095-30235, public 32974-32982)
        // ==================================================================

        // C: ufbxi_align_to_mask (ufbx.c:3628-3631).
        static int AlignToMask(int value, int alignMask)
        {
            return value + ((0 - value) & alignMask);
        }

        // C: ufbxi_size_align_mask (ufbx.c:3633-3636) with
        // UFBX_MAXIMUM_ALIGNMENT == 8 (ufbx.c:858-859, 64-bit).
        static int SizeAlignMask(int size)
        {
            return ((size ^ (size - 1)) >> 1) & 7;
        }

        // C: ufbxi_map_cmp_vertex (ufbx.c:30095-30107): compares packed vertex bytes as
        // 8-byte words (little endian on x86-64). Equality is all that is observable, the
        // ordering only matters for the AA-tree overflow branch which indexes items in
        // insertion order anyway (see file header).
        static bool PackedVertexEquals(byte[] a, byte[] b, int size)
        {
            for (int i = 0; i < size; i++) {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        // Dictionary comparer over the first `size` bytes of the packed vertex buffer.
        // The hash value is never observable (see file header); FNV-1a keeps it cheap.
        sealed class PackedVertexComparer : IEqualityComparer<byte[]>
        {
            public readonly int Size;

            public PackedVertexComparer(int size) { Size = size; }

            public bool Equals(byte[] a, byte[] b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null) return false;
                return PackedVertexEquals(a, b, Size);
            }

            public int GetHashCode(byte[] data)
            {
                uint hash = 2166136261u;
                for (int i = 0; i < Size; i++) {
                    hash = (hash ^ data[i]) * 16777619u;
                }
                return unchecked((int)hash);
            }
        }

        // C: ufbxi_generate_indices (ufbx.c:30115-30235).
        //
        // Packs each index's vertices from all streams into `packed_vertex`, dedupes them
        // through the map, and writes `indices[i]`. Finally the deduplicated vertices are
        // copied back in place into each stream's buffer (ufbx.c:30208-30217), so the
        // caller's stream data becomes `result_vertices` unique vertices.
        //
        // The C allocator (`allocator` argument of the public form) has no managed
        // counterpart. Failure paths: `vertex_count < num_indices` (ufbx.c:30136-30141,
        // writes `info = "<i>"` and description "Truncated vertex stream"),
        // `packed_size == 0` (ufbx.c:30154-30157, "Zero vertex size"), map allocation
        // (not representable). Errors are reported through `error` like C, not exceptions,
        // matching the no-throw C failure tail `ufbxi_fix_error_type(error,
        // "Failed to generate indices", NULL)` (ufbx.c:30221).
        internal static int GenerateIndicesImp(UfbxVertexStream[] userStreams, int numStreams, uint[] indices, int numIndices, UfbxError error)
        {
            bool fail = false;
            int failStreamIx = -1;
            bool zeroSize = false;

            // C: local_streams[16] / local_packed_vertex[64] -- managed arrays grow.
            // C stores `packed_offset` inside ufbxi_vertex_stream; the port keeps it in
            // a parallel array (the Model's UfbxVertexStream mirrors the user-facing C
            // struct, not the internal one).
            UfbxVertexStream[] streams = null;
            int[] packedOffsets = null;
            if (numStreams > 16) {
                streams = new UfbxVertexStream[numStreams];
                packedOffsets = new int[numStreams];
            } else {
                streams = new UfbxVertexStream[numStreams > 0 ? numStreams : 0];
                packedOffsets = new int[numStreams > 0 ? numStreams : 0];
            }

            int packedSize = 0;
            if (!fail) {
                for (int i = 0; i < numStreams; i++) {
                    if (userStreams[i].VertexCount < numIndices) {
                        // C: ufbxi_fmt_err_info(error, "%zu", i);
                        //     ufbxi_report_err_msg(error, "user_streams[i].vertex_count < num_indices", "Truncated vertex stream");
                        if (error != null) {
                            UfbxiPrint.FmtErrInfo(error, "%zu", new UfbxiVaList().AddSizeT((ulong)i));
                            error.Description = "Truncated vertex stream";
                        }
                        failStreamIx = i;
                        fail = true;
                        break;
                    }

                    int vertexSize = userStreams[i].VertexSize;
                    int align = SizeAlignMask(vertexSize);
                    packedSize = AlignToMask(packedSize, align);
                    streams[i].Data = userStreams[i].Data;
                    streams[i].VertexCount = userStreams[i].VertexCount;
                    streams[i].VertexSize = vertexSize;
                    packedOffsets[i] = packedSize;
                    packedSize += vertexSize;
                }
                packedSize = AlignToMask(packedSize, 7);
            }

            if (!fail && packedSize == 0) {
                // C: ufbxi_report_err_msg(error, "packed_size != 0", "Zero vertex size");
                if (error != null) {
                    error.Description = "Zero vertex size";
                }
                zeroSize = true;
                fail = true;
            }

            byte[] packedVertex = null;
            if (!fail) {
                packedVertex = new byte[packedSize];
            }

            // C: ufbxi_map with cmp_fn = ufbxi_map_cmp_vertex, cmp_user = &packed_size.
            Dictionary<byte[], int> map = null;
            List<byte[]> mapItems = null;
            if (!fail) {
                map = new Dictionary<byte[], int>(new PackedVertexComparer(packedSize));
                mapItems = new List<byte[]>();
            }

            if (!fail) {
                // C: memset(packed_vertex, 0, packed_size) -- padding bytes stay zero.
                int[] readPos = new int[numStreams];
                for (int i = 0; i < numIndices; i++) {
                    for (int si = 0; si < numStreams; si++) {
                        int size = streams[si].VertexSize, offset = packedOffsets[si];
                        Array.Copy(streams[si].Data, readPos[si], packedVertex, offset, size);
                        readPos[si] += size;
                    }

                    if (!map.TryGetValue(packedVertex, out int index)) {
                        // C: entry = ufbxi_map_insert_size(...); memcpy(entry, packed_vertex, packed_size);
                        // The item storage is `items[index]` with index = map.size++ (ufbx.c:4632).
                        index = mapItems.Count;
                        byte[] item = new byte[packedSize];
                        Array.Copy(packedVertex, item, packedSize);
                        map.Add(item, index);
                        mapItems.Add(item);
                    }
                    indices[i] = (uint)index;
                }
            }

            int resultVertices = 0;
            if (!fail) {
                resultVertices = mapItems.Count;

                // C: copy the deduplicated vertices back into each stream buffer in place
                // (ufbx.c:30208-30217): `src = map.items + streams[si].packed_offset`,
                // stepping `packed_size` per vertex.
                for (int si = 0; si < numStreams; si++) {
                    int vertexSize = streams[si].VertexSize;
                    int offset = packedOffsets[si];
                    for (int i = 0; i < resultVertices; i++) {
                        Array.Copy(mapItems[i], offset, streams[si].Data, i * vertexSize, vertexSize);
                    }
                }

                UfbxiPrint.ClearError(error);
            } else {
                // C: ufbxi_fix_error_type(error, "Failed to generate indices", NULL)
                // (ufbx.c:30221). Description may already be set by the report sites above.
                if (error != null) {
                    UfbxiPrint.FixErrorType(error, "Failed to generate indices", error);
                }
                _ = failStreamIx;
                _ = zeroSize;
            }

            return resultVertices;
        }

        // ==================================================================
        // Mesh material parts (ufbx.c:13251, 21551-21624)
        // ==================================================================

        // C: ufbxi_mesh_part_add_face (ufbx.c:13251-13261). `num_empty/point/line_faces`
        // are consecutive in C (static asserts at 13249-13250); the port indexes them
        // explicitly.
        static void MeshPartAddFace(ref UfbxMeshPart part, uint numIndices)
        {
            part.NumFaces++;
            if (numIndices >= 3) {
                part.NumTriangles += (int)(numIndices - 2);
            } else {
                switch (numIndices) {
                    case 0: part.NumEmptyFaces++; break;
                    case 1: part.NumPointFaces++; break;
                    default: part.NumLineFaces++; break;
                }
            }
        }

        // C: ufbxi_material_part_usage_less (ufbx.c:21551-21562).
        static bool MaterialPartUsageLess(object user, uint a, uint b)
        {
            UfbxMeshPart[] parts = (UfbxMeshPart[])user;
            UfbxMeshPart pa = parts[a];
            UfbxMeshPart pb = parts[b];
            if (pa.FaceIndices == null || pa.FaceIndices.Length == 0 ||
                pb.FaceIndices == null || pb.FaceIndices.Length == 0) {
                int ca = pa.FaceIndices != null ? pa.FaceIndices.Length : 0;
                int cb = pb.FaceIndices != null ? pb.FaceIndices.Length : 0;
                if (ca == cb) return a < b;
                return ca > cb;
            }
            return pa.FaceIndices[0] < pb.FaceIndices[0];
        }

        // C: ufbxi_finalize_mesh_material (ufbx.c:21564-21624). The C `buf`/`error`
        // arguments only matter for allocation failure (not representable).
        internal static void FinalizeMeshMaterial(UfbxMesh mesh)
        {
            int numMaterials = mesh.Materials != null ? mesh.Materials.Length : 0;
            int numParts = mesh.MaterialParts != null ? mesh.MaterialParts.Length : 0;
            int numFaces = mesh.Faces != null ? mesh.Faces.Length : 0;

            UfbxMeshPart[] parts = mesh.MaterialParts;
            // C: ufbx_assert(!parts || (mesh->material_parts.count == num_materials) || ...)

            uint[] faceMaterial = mesh.FaceMaterial;

            // Count the number of faces and triangles per material
            for (int i = 0; i < numFaces; i++) {
                UfbxFace face = mesh.Faces[i];
                uint matIx = 0;

                if (faceMaterial != null) {
                    matIx = faceMaterial[i];
                    if (matIx >= numMaterials) {
                        faceMaterial[i] = 0;
                        matIx = 0;
                    }
                }

                if (parts != null) {
                    MeshPartAddFace(ref parts[matIx], face.NumIndices);
                }
            }

            if (parts != null) {
                // Allocate per-material buffers (clear `num_faces` to 0 to re-use it as
                // an index when fetching the face indices).
                uint partIndex = 0;
                for (int pi = 0; pi < numParts; pi++) {
                    UfbxMeshPart part = parts[pi];
                    part.Index = partIndex++;
                    int count = part.NumFaces;
                    part.FaceIndices = new uint[count];
                    part.NumFaces = 0;
                    parts[pi] = part;
                }

                // Fetch the per-material face indices
                for (int i = 0; i < numFaces; i++) {
                    uint matIx = faceMaterial != null ? faceMaterial[i] : 0;
                    if (matIx < (uint)numParts) {
                        UfbxMeshPart part = parts[matIx];
                        part.FaceIndices[part.NumFaces++] = (uint)i;
                        parts[matIx] = part;
                    }
                }

                mesh.MaterialPartUsageOrder = new uint[numParts];
                for (int i = 0; i < numParts; i++) {
                    mesh.MaterialPartUsageOrder[i] = (uint)i;
                }
                UfbxiSort.UnstableSort(mesh.MaterialPartUsageOrder, numParts, MaterialPartUsageLess, parts);
            }
        }

        // ==================================================================
        // Subdivision context (ufbx.c:28833-28897)
        // ==================================================================

        // C: ufbxi_subdivide_input (ufbx.c:28833-28836). `data` points either into a
        // flat real buffer (`double[]`, offset in reals) or into a weights array
        // (`SubdivVertexWeights[]`, offset in elements).
        struct SubdivideInput
        {
            public Array Data;      // C: const void *data
            public int Off;         // C: byte offset / stride units -> real (vec) or element (weights) offset
            public double Weight;   // C: weight
        }

        // C: ufbxi_subdivision_vertex_weights (ufbx.c:28864-28867). C stores a pointer
        // into a shared weight array; the port carries (array, offset) pairs.
        sealed class SubdivVertexWeights
        {
            public UfbxSubdivisionWeight[] Weights; // C: weights
            public int Offset;                      // C: pointer offset into the shared array
            public int NumWeights;                  // C: num_weights
        }

        // C: ufbxi_subdivide_layer_input (ufbx.c:28840-28854). C's `sum_fn`/`stride`
        // become `Kind` (2/3/4 = vec dims, -1 = vertex-weights mode).
        sealed class SubdivideLayerInput
        {
            public int Kind;                        // C: sum_fn + stride
            public Array Values;                    // C: values (double[] for vec, SubdivVertexWeights[] for weights)
            public uint[] Indices;                  // C: indices
            public UfbxSubdivisionBoundary Boundary;    // C: boundary
            public bool CheckSplitData;             // C: check_split_data
            public bool IgnoreIndices;              // C: ignore_indices
        }

        // C: ufbxi_subdivide_layer_output (ufbx.c:28856-28862).
        sealed class SubdivideLayerOutput
        {
            public Array Values;        // C: values (double[] or SubdivVertexWeights[])
            public int NumValues;       // C: num_values
            public uint[] Indices;      // C: indices (null when IgnoreIndices)
            public int NumIndices;      // C: num_indices
            public bool UniquePerVertex;    // C: unique_per_vertex
        }

        // C: ufbxi_subdivide_context (ufbx.c:28869-28897). The C `imp`/refcount and the
        // buf/allocator fields have no managed counterpart; `src_mesh_ptr` equals
        // `SrcMesh` by identity here.
        sealed class SubdivideContext
        {
            public UfbxMesh SrcMesh;        // C: src_mesh (value copy of the source mesh)
            public UfbxMesh DstMesh;        // C: dst_mesh
            public UfbxTopoEdge[] Topo;     // C: topo
            public int NumTopo;             // C: num_topo

            public UfbxSubdivideOpts Opts;  // C: opts

            public SubdivideInput[] Inputs;         // C: inputs
            public int InputsCap;                   // C: inputs_cap

            public double[] TmpVertexWeights;       // C: tmp_vertex_weights
            public UfbxSubdivisionWeight[] TmpWeights;  // C: tmp_weights
            public int TotalWeights;                // C: total_weights
            public int MaxVertexWeights;            // C: max_vertex_weights (SIZE_MAX -> -1)
        }

        // C: ufbxi_grow_array() on `sc->inputs` (ufbx.c:29088, 29311, 29330). Preserves
        // the previous contents like realloc.
        static SubdivideInput[] GrowInputs(SubdivideContext sc, int need)
        {
            if (sc.InputsCap >= need) return sc.Inputs;
            int cap = sc.InputsCap == 0 ? 32 : sc.InputsCap;
            while (cap < need) cap *= 2;
            var next = new SubdivideInput[cap];
            if (sc.Inputs != null) Array.Copy(sc.Inputs, next, sc.InputsCap);
            sc.Inputs = next;
            sc.InputsCap = cap;
            return next;
        }

        // ------------------------------------------------------------------
        // Sum functions (ufbx.c:28899-29020)
        // ------------------------------------------------------------------

        // C: ufbxi_subdivide_sum_vec2 (ufbx.c:28899-28912).
        static void SumVec2(SubdivideInput[] inputs, int numInputs, double[] dst, int dstOff)
        {
            double x = 0.0, y = 0.0;
            for (int i = 0; i < numInputs; i++) {
                double[] src = (double[])inputs[i].Data;
                int off = inputs[i].Off;
                double weight = inputs[i].Weight;
                x += src[off + 0] * weight;
                y += src[off + 1] * weight;
            }
            dst[dstOff + 0] = x;
            dst[dstOff + 1] = y;
        }

        // C: ufbxi_subdivide_sum_vec3 (ufbx.c:28914-28928).
        static void SumVec3(SubdivideInput[] inputs, int numInputs, double[] dst, int dstOff)
        {
            double x = 0.0, y = 0.0, z = 0.0;
            for (int i = 0; i < numInputs; i++) {
                double[] src = (double[])inputs[i].Data;
                int off = inputs[i].Off;
                double weight = inputs[i].Weight;
                x += src[off + 0] * weight;
                y += src[off + 1] * weight;
                z += src[off + 2] * weight;
            }
            dst[dstOff + 0] = x;
            dst[dstOff + 1] = y;
            dst[dstOff + 2] = z;
        }

        // C: ufbxi_subdivide_sum_vec4 (ufbx.c:28930-28945).
        static void SumVec4(SubdivideInput[] inputs, int numInputs, double[] dst, int dstOff)
        {
            double x = 0.0, y = 0.0, z = 0.0, w = 0.0;
            for (int i = 0; i < numInputs; i++) {
                double[] src = (double[])inputs[i].Data;
                int off = inputs[i].Off;
                double weight = inputs[i].Weight;
                x += src[off + 0] * weight;
                y += src[off + 1] * weight;
                z += src[off + 2] * weight;
                w += src[off + 3] * weight;
            }
            dst[dstOff + 0] = x;
            dst[dstOff + 1] = y;
            dst[dstOff + 2] = z;
            dst[dstOff + 3] = w;
        }

        // C: ufbxi_subdivision_weight_less (ufbx.c:28947-28954): weight descending,
        // index ascending. (`ufbxi_dev_assert(a.index != b.index)` is dev-only.)
        static bool SubdivisionWeightLess(object user, UfbxSubdivisionWeight a, UfbxSubdivisionWeight b)
        {
            if (a.Weight != b.Weight) return a.Weight > b.Weight;
            return a.Index < b.Index;
        }

        // C: ufbxi_subdivide_sum_vertex_weights (ufbx.c:28956-29013).
        //
        // Accumulates per-vertex weights through `sc->tmp_vertex_weights` (cleared back
        // to zero on the way out), compacts the touched vertices, unstable-sorts by
        // descending weight, optionally truncates + normalizes to
        // `sc->max_vertex_weights`, and stores the result in the shared weight array.
        static void SumVertexWeights(SubdivideContext sc, SubdivideInput[] inputs, int numInputs, SubdivVertexWeights[] dst, int dstOff)
        {
            double[] vertexWeights = sc.TmpVertexWeights;
            UfbxSubdivisionWeight[] tmpWeights = sc.TmpWeights;
            int numWeights = 0;

            for (int inputIx = 0; inputIx < numInputs; inputIx++) {
                SubdivVertexWeights src = ((SubdivVertexWeights[])inputs[inputIx].Data)[inputs[inputIx].Off];
                double inputWeight = inputs[inputIx].Weight;

                for (int weightIx = 0; weightIx < src.NumWeights; weightIx++) {
                    double weight = inputWeight * src.Weights[src.Offset + weightIx].Weight;
                    // C: `1.175494351e-38f` -- float literal promoted to double.
                    if (weight < 1.175494351e-38f) continue;

                    uint vx = src.Weights[src.Offset + weightIx].Index;

                    double prev = vertexWeights[vx];
                    vertexWeights[vx] = prev + weight;
                    if (prev == 0.0f) {
                        UfbxSubdivisionWeight w = tmpWeights[numWeights++];
                        w.Index = vx;
                        tmpWeights[numWeights - 1] = w;
                    }
                }
            }

            for (int i = 0; i < numWeights; i++) {
                uint vx = tmpWeights[i].Index;
                UfbxSubdivisionWeight w = tmpWeights[i];
                w.Weight = vertexWeights[vx];
                tmpWeights[i] = w;
                vertexWeights[vx] = 0.0f;
            }

            UfbxiSort.UnstableSort(tmpWeights, numWeights, SubdivisionWeightLess, null);

            if (sc.MaxVertexWeights != -1) {    // C: SIZE_MAX
                if (sc.MaxVertexWeights < numWeights) numWeights = sc.MaxVertexWeights;

                // Normalize weights
                double prefixWeight = 0.0;
                for (int i = 0; i < numWeights; i++) {
                    prefixWeight += tmpWeights[i].Weight;
                }
                for (int i = 0; i < numWeights; i++) {
                    UfbxSubdivisionWeight w = tmpWeights[i];
                    w.Weight /= prefixWeight;
                    tmpWeights[i] = w;
                }
            }

            sc.TotalWeights += numWeights;
            UfbxSubdivisionWeight[] weights = new UfbxSubdivisionWeight[numWeights];
            Array.Copy(tmpWeights, weights, numWeights);

            dst[dstOff].Weights = weights;
            dst[dstOff].Offset = 0;
            dst[dstOff].NumWeights = numWeights;
        }

        // C: sum_fn dispatch (ufbx.c:29138, 29177, 29183, 29198, 29415, 29430):
        // vec modes write into the flat real buffer, weights mode into the weights array.
        static void InvokeSum(SubdivideContext sc, SubdivideLayerInput input, SubdivideInput[] inputs, int numInputs, Array dst, int dstOff)
        {
            switch (input.Kind) {
                case 2:
                    SumVec2(inputs, numInputs, (double[])dst, dstOff);
                    break;
                case 3:
                    SumVec3(inputs, numInputs, (double[])dst, dstOff);
                    break;
                case 4:
                    SumVec4(inputs, numInputs, (double[])dst, dstOff);
                    break;
                default:
                    SumVertexWeights(sc, inputs, numInputs, (SubdivVertexWeights[])dst, dstOff);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Edge split / crease helpers (ufbx.c:29022-29050)
        // ------------------------------------------------------------------

        // C: ufbxi_is_edge_split (ufbx.c:29022-29042). The memcmp of attribute data is a
        // bit pattern comparison (NaN-safe): compare reals via their 64-bit patterns.
        static bool IsEdgeSplit(SubdivideLayerInput input, UfbxTopoEdge[] topo, uint index)
        {
            uint twin = topo[index].Twin;
            if (twin != NoIndex) {
                uint a0 = input.Indices[index];
                uint a1 = input.Indices[topo[index].Next];
                uint b0 = input.Indices[topo[twin].Next];
                uint b1 = input.Indices[twin];
                if (a0 == b0 && a1 == b1) return false;
                if (!input.CheckSplitData) return true;
                int stride = input.Kind;    // C: stride (in reals == dims)
                double[] values = (double[])input.Values;
                {
                    int da0 = (int)a0 * stride, da1 = (int)a1 * stride;
                    int db0 = (int)b0 * stride, db1 = (int)b1 * stride;
                    bool same = true;
                    for (int i = 0; i < stride; i++) {
                        if (BitConverter.DoubleToInt64Bits(values[da0 + i]) != BitConverter.DoubleToInt64Bits(values[db0 + i])) { same = false; break; }
                    }
                    if (same) {
                        same = true;
                        for (int i = 0; i < stride; i++) {
                            if (BitConverter.DoubleToInt64Bits(values[da1 + i]) != BitConverter.DoubleToInt64Bits(values[db1 + i])) { same = false; break; }
                        }
                        if (same) return false;
                    }
                }
                return true;
            }

            return false;
        }

        // C: ufbxi_edge_crease (ufbx.c:29044-29050).
        static double EdgeCrease(UfbxMesh mesh, bool split, UfbxTopoEdge[] topo, uint index)
        {
            if (topo[index].Twin == NoIndex) return 1.0f;
            if (split) return 1.0f;
            if (mesh.EdgeCrease != null && topo[index].Edge != NoIndex) return mesh.EdgeCrease[topo[index].Edge] * 10.0;
            return 0.0f;
        }

        // ==================================================================
        // Subdivide layer (ufbx.c:29052-29470)
        // ==================================================================

        // C: ufbxi_subdivide_layer (ufbx.c:29052-29470).
        //
        // Computes one subdivision pass over a single attribute: face points (average of
        // the face's attribute values), edge points (midpoint rules with creases), vertex
        // points (valence-based masks), then emits 4 output indices per input index:
        // [vertex, edge, face, prev-edge].
        static void SubdivideLayer(SubdivideContext sc, SubdivideLayerOutput output, SubdivideLayerInput input)
        {
            UfbxSubdivisionBoundary boundary = input.Boundary;

            UfbxMesh mesh = sc.SrcMesh;
            UfbxTopoEdge[] topo = sc.Topo;
            int numTopo = sc.NumTopo;

            bool weightsMode = input.Kind == -1;
            int stride = weightsMode ? 1 : input.Kind;  // C: stride (elements)

            uint[] edgeIndices = new uint[mesh.NumIndices];

            int numEdgeValues = 0;
            for (uint ix = 0; ix < (uint)mesh.NumIndices; ix++) {
                uint twin = topo[ix].Twin;
                if (twin < ix && !IsEdgeSplit(input, topo, ix)) {
                    edgeIndices[ix] = edgeIndices[twin];
                } else {
                    edgeIndices[ix] = (uint)numEdgeValues++;
                }
            }

            // C: values buffer layout [face_values | edge_values | vertex_values]
            // (ufbx.c:29073-29080), one element per (face, unique edge, vertex corner).
            int numFaces = mesh.Faces.Length;
            int numInitialValues = numEdgeValues + numFaces + mesh.NumIndices;
            Array values = weightsMode
                ? (Array)new SubdivVertexWeights[numInitialValues]
                : (Array)new double[numInitialValues * stride];

            int faceBase = 0;
            int edgeBase = faceBase + numFaces * stride;
            int vertexBase = edgeBase + numEdgeValues * stride;

            int numVertexValues = 0;

            uint[] vertexIndices = new uint[mesh.NumIndices];

            int minInputs = 32;
            if (mesh.MaxFaceTriangles + 2 > minInputs) minInputs = mesh.MaxFaceTriangles + 2;
            SubdivideInput[] inputs = GrowInputs(sc, minInputs);

            // Assume initially unique per vertex, remove if not the case
            output.UniquePerVertex = true;

            bool sharpCorners = false;
            bool sharpSplits = false;
            bool sharpAll = false;

            switch (boundary) {
                case UfbxSubdivisionBoundary.Default:
                case UfbxSubdivisionBoundary.SharpNone:
                case UfbxSubdivisionBoundary.Legacy:
                    // All smooth
                    break;
                case UfbxSubdivisionBoundary.SharpCorners:
                    sharpCorners = true;
                    break;
                case UfbxSubdivisionBoundary.SharpBoundary:
                    sharpCorners = true;
                    sharpSplits = true;
                    break;
                case UfbxSubdivisionBoundary.SharpInterior:
                    sharpAll = true;
                    break;
                default:
                    // C: ufbxi_unreachable("Bad boundary mode")
                    UfbxiFail.Fail("boundary == UFBX_SUBDIVISION_BOUNDARY_DEFAULT || sharp_corners || ...", UfbxErrorType.Unknown);
                    break;
            }

            // Mark unused indices as `UFBX_NO_INDEX` so we can patch non-manifold
            for (int i = 0; i < mesh.NumIndices; i++) {
                vertexIndices[i] = NoIndex;
            }

            // Face points
            for (int fi = 0; fi < numFaces; fi++) {
                UfbxFace face = mesh.Faces[fi];
                int dst = faceBase + fi * stride;

                double weight = 1.0f / (double)face.NumIndices;
                for (uint ci = 0; ci < face.NumIndices; ci++) {
                    uint ix = face.IndexBegin + ci;
                    inputs[ci].Data = input.Values;
                    inputs[ci].Off = (int)(input.Indices[ix] * stride);
                    inputs[ci].Weight = weight;
                }

                InvokeSum(sc, input, inputs, (int)face.NumIndices, values, dst);
            }

            // Edge points
            for (uint ix = 0; ix < (uint)mesh.NumIndices; ix++) {
                int dst = edgeBase + (int)edgeIndices[ix] * stride;

                uint twin = topo[ix].Twin;
                bool split = IsEdgeSplit(input, topo, ix);

                if (split || (topo[ix].Flags & UfbxTopoFlags.NonManifold) != 0) {
                    output.UniquePerVertex = false;
                }

                double crease = 0.0f;
                if (split || twin == NoIndex) {
                    crease = 1.0f;
                } else if (topo[ix].Edge != NoIndex && mesh.EdgeCrease != null) {
                    crease = mesh.EdgeCrease[topo[ix].Edge] * 10.0;
                }
                if (sharpAll) crease = 1.0f;

                int v0 = (int)(input.Indices[ix] * stride);
                int v1 = (int)(input.Indices[topo[ix].Next] * stride);

                // TODO: Unify
                if (twin < ix && !split) {
                    // Already calculated
                } else if (crease <= 0.0f) {
                    int f0 = (int)(topo[ix].Face * stride);
                    int f1 = (int)(topo[twin].Face * stride);
                    inputs[0].Data = input.Values;
                    inputs[0].Off = v0;
                    inputs[0].Weight = 0.25f;
                    inputs[1].Data = input.Values;
                    inputs[1].Off = v1;
                    inputs[1].Weight = 0.25f;
                    inputs[2].Data = values;
                    inputs[2].Off = f0;
                    inputs[2].Weight = 0.25f;
                    inputs[3].Data = values;
                    inputs[3].Off = f1;
                    inputs[3].Weight = 0.25f;
                    InvokeSum(sc, input, inputs, 4, values, dst);
                } else if (crease >= 1.0f) {
                    inputs[0].Data = input.Values;
                    inputs[0].Off = v0;
                    inputs[0].Weight = 0.5f;
                    inputs[1].Data = input.Values;
                    inputs[1].Off = v1;
                    inputs[1].Weight = 0.5f;
                    InvokeSum(sc, input, inputs, 2, values, dst);
                } else if (crease < 1.0f) {
                    int f0 = (int)(topo[ix].Face * stride);
                    int f1 = (int)(topo[twin].Face * stride);
                    double w0 = 0.25f + 0.25f * crease;
                    double w1 = 0.25f - 0.25f * crease;

                    inputs[0].Data = input.Values;
                    inputs[0].Off = v0;
                    inputs[0].Weight = w0;
                    inputs[1].Data = input.Values;
                    inputs[1].Off = v1;
                    inputs[1].Weight = w0;
                    inputs[2].Data = values;
                    inputs[2].Off = f0;
                    inputs[2].Weight = w1;
                    inputs[3].Data = values;
                    inputs[3].Off = f1;
                    inputs[3].Weight = w1;
                    InvokeSum(sc, input, inputs, 4, values, dst);
                }
            }

            // Vertex points
            for (int vi = 0; vi < mesh.NumVertices; vi++) {
                uint originalStart = mesh.VertexFirstIndex[vi];
                if (originalStart == NoIndex) continue;

                // Find a topological boundary, or if not found a split edge
                uint start = originalStart;
                for (uint cur = start; ; ) {
                    uint prev = UfbxTopology.TopoPrevVertexEdge(topo, numTopo, cur);
                    if (prev == NoIndex) { start = cur; break; }    // Topological boundary: Stop and use as start
                    if (IsEdgeSplit(input, topo, prev)) start = cur;    // Split edge: Consider as start
                    if (prev == originalStart) break;   // Loop: Stop, use original start or split if found
                    cur = prev;
                }

                originalStart = start;
                while (start != NoIndex) {
                    if (start != originalStart) {
                        output.UniquePerVertex = false;
                    }

                    int valueIndex = numVertexValues++;
                    int dst = vertexBase + valueIndex * stride;

                    // We need to compute the average crease value and keep track of
                    // two creased edges, if there's more we use the corner rule that
                    // does not need the information.
                    double totalCrease = 0.0f;
                    int numCrease = 0;
                    int numSplit = 0;
                    bool onBoundary = false;
                    bool nonManifold = false;
                    int creaseInputIndex0 = 0;  // C: size_t crease_input_indices[2]
                    int creaseInputIndex1 = 0;

                    // At start we always have two edges and a single face
                    uint startPrev = topo[start].Prev;
                    uint endEdge = topo[startPrev].Twin;
                    int valence = 2;

                    nonManifold |= (topo[start].Flags & UfbxTopoFlags.NonManifold) != 0;
                    nonManifold |= (topo[startPrev].Flags & UfbxTopoFlags.NonManifold) != 0;

                    int v0 = (int)(input.Indices[start] * stride);

                    int numInputs = 4;

                    {
                        int e0 = (int)(input.Indices[topo[start].Next] * stride);
                        int e1 = (int)(input.Indices[startPrev] * stride);
                        int f0 = (int)(topo[start].Face * stride);
                        inputs[0].Data = input.Values;
                        inputs[0].Off = v0;
                        inputs[0].Weight = 0.0;
                        inputs[1].Data = input.Values;
                        inputs[1].Off = e0;
                        inputs[1].Weight = 0.0;
                        inputs[2].Data = input.Values;
                        inputs[2].Off = e1;
                        inputs[2].Weight = 0.0;
                        inputs[3].Data = values;
                        inputs[3].Off = f0;
                        inputs[3].Weight = 0.0;
                    }

                    bool startSplit = IsEdgeSplit(input, topo, start);
                    bool prevSplit = endEdge != NoIndex && IsEdgeSplit(input, topo, endEdge);

                    // Either of the first two edges may be creased
                    // C: `crease_input_indices[num_crease++] = 1/2` -- indexed by the
                    // CURRENT count, not fixed slots (a start edge with no twin-less
                    // position records nothing, shifting the later entries).
                    double startCrease = EdgeCrease(mesh, startSplit, topo, start);
                    if (startCrease > 0.0f) {
                        totalCrease += startCrease;
                        if (numCrease == 0) creaseInputIndex0 = 1; else creaseInputIndex1 = 1;
                        numCrease++;
                    }
                    double prevCrease = EdgeCrease(mesh, prevSplit, topo, startPrev);
                    if (prevCrease > 0.0f) {
                        totalCrease += prevCrease;
                        if (numCrease == 0) creaseInputIndex0 = 2; else creaseInputIndex1 = 2;
                        numCrease++;
                    }

                    if (endEdge != NoIndex) {
                        if (prevSplit) {
                            numSplit++;
                        }
                    } else {
                        onBoundary = true;
                    }

                    UfbxiFail.CheckNoDesc(vertexIndices[start] == NoIndex, "vertex_indices[start] == UFBX_NO_INDEX");
                    vertexIndices[start] = (uint)valueIndex;

                    if (startSplit) {
                        // We need to special case if the first edge is split as we have
                        // handled it already in the code above..
                        start = UfbxTopology.TopoNextVertexEdge(topo, numTopo, start);
                        numSplit++;
                    } else {
                        // Follow vertex edges until we either hit a topological/split boundary
                        // or loop back to the left edge we accounted for in `start_prev`
                        uint cur = start;
                        for (; ; ) {
                            cur = UfbxTopology.TopoNextVertexEdge(topo, numTopo, cur);

                            // Topological boundary: Finished
                            if (cur == NoIndex) {
                                onBoundary = true;
                                start = NoIndex;
                                break;
                            }

                            nonManifold |= (topo[cur].Flags & UfbxTopoFlags.NonManifold) != 0;
                            UfbxiFail.CheckNoDesc(vertexIndices[cur] == NoIndex, "vertex_indices[cur] == UFBX_NO_INDEX");
                            vertexIndices[cur] = (uint)valueIndex;

                            bool split = IsEdgeSplit(input, topo, cur);

                            // Looped: Add the face from the other side still if not split
                            if (cur == endEdge && !split) {
                                GrowInputs(sc, numInputs + 1);
                                inputs = sc.Inputs;
                                int f0 = (int)(topo[cur].Face * stride);
                                inputs[numInputs].Data = values;
                                inputs[numInputs].Off = f0;
                                inputs[numInputs].Weight = 0.0;
                                start = NoIndex;
                                numInputs += 1;
                                break;
                            }

                            // Add the edge crease, this also handles boundaries as they
                            // have an implicit crease of 1.0 using `ufbxi_edge_crease()`
                            double curCrease = EdgeCrease(mesh, split, topo, cur);
                            if (curCrease > 0.0f) {
                                totalCrease += curCrease;
                                if (numCrease < 2) {
                                    if (numCrease == 0) creaseInputIndex0 = numInputs;
                                    else creaseInputIndex1 = numInputs;
                                }
                                numCrease++;
                            }

                            // Add the new edge and face to the sum
                            {
                                GrowInputs(sc, numInputs + 2);
                                inputs = sc.Inputs;

                                int e0 = (int)(input.Indices[topo[cur].Next] * stride);
                                int f0 = (int)(topo[cur].Face * stride);
                                inputs[numInputs + 0].Data = input.Values;
                                inputs[numInputs + 0].Off = e0;
                                inputs[numInputs + 0].Weight = 0.0;
                                inputs[numInputs + 1].Data = values;
                                inputs[numInputs + 1].Off = f0;
                                inputs[numInputs + 1].Weight = 0.0;
                                numInputs += 2;
                            }
                            valence++;

                            // If we landed at a split edge advance to the next one
                            // and continue from there in the outer loop
                            if (split) {
                                start = UfbxTopology.TopoNextVertexEdge(topo, numTopo, cur);
                                numSplit++;
                                break;
                            }
                        }
                    }

                    if (start == originalStart) start = NoIndex;

                    // Weights for various subdivision masks
                    double feWeight = 1.0f / (double)(valence * valence);
                    double vWeight = (double)(valence - 2) / (double)valence;

                    // Select the right subdivision mask depending on valence and crease
                    if (numCrease > 2
                        || (sharpCorners && valence == 2 && (numSplit > 0 || onBoundary))
                        || (sharpSplits && (numSplit > 0 || onBoundary))
                        || sharpAll
                        || nonManifold) {
                        // Corner: Copy as-is
                        inputs[0].Data = input.Values;
                        inputs[0].Off = v0;
                        inputs[0].Weight = 1.0f;
                        numInputs = 1;
                    } else if (numCrease == 2) {
                        // Boundary: Interpolate edge
                        totalCrease *= 0.5f;
                        if (totalCrease < 0.0f) totalCrease = 0.0f;
                        if (totalCrease > 1.0f) totalCrease = 1.0f;

                        inputs[0].Weight = vWeight * (1.0f - totalCrease) + 0.75f * totalCrease;
                        double few = feWeight * (1.0f - totalCrease);
                        for (int i = 1; i < numInputs; i++) {
                            inputs[i].Weight = few;
                        }

                        // Add weight to the creased edges
                        inputs[creaseInputIndex0].Weight += 0.125f * totalCrease;
                        inputs[creaseInputIndex1].Weight += 0.125f * totalCrease;
                    } else {
                        // Regular: Weighted sum with the accumulated edge/face points
                        inputs[0].Weight = vWeight;
                        for (int i = 1; i < numInputs; i++) {
                            inputs[i].Weight = feWeight;
                        }

                    }

                    if (mesh.VertexCrease.Exists) {
                        double v = UfbxTopology.GetVertexReal(mesh.VertexCrease, (int)originalStart);
                        v *= 10.0;
                        if (v > 0.0f) {
                            if (v > 1.0) v = 1.0f;

                            double iv = 1.0f - v;
                            inputs[0].Weight = 1.0f * v + inputs[0].Weight * iv;
                            for (int i = 1; i < numInputs; i++) {
                                inputs[i].Weight *= iv;
                            }
                        }
                    }

                    InvokeSum(sc, input, inputs, numInputs, values, dst);
                }
            }

            // Copy non-manifold vertex values as-is
            for (int oldIx = 0; oldIx < mesh.NumIndices; oldIx++) {
                uint ix = vertexIndices[oldIx];
                if (ix == NoIndex) {
                    ix = (uint)numVertexValues++;
                    vertexIndices[oldIx] = ix;
                    int src = (int)(input.Indices[oldIx] * stride);
                    int dst = vertexBase + (int)ix * stride;

                    inputs[0].Data = input.Values;
                    inputs[0].Off = src;
                    inputs[0].Weight = 1.0f;
                    InvokeSum(sc, input, inputs, 1, values, dst);
                }
            }

            int numValues = numEdgeValues + numFaces + numVertexValues;

            // C: push (num_values+1) elements and zero the first as a guard
            // (ufbx.c:29436-29440); the port skips the guard element (see file header).
            Array newValues = weightsMode
                ? (Array)new SubdivVertexWeights[numValues]
                : (Array)new double[numValues * stride];
            Array.Copy(values, 0, newValues, 0, numValues * stride);

            output.Values = newValues;
            output.NumValues = numValues;

            if (!input.IgnoreIndices) {
                uint[] newIndices = new uint[mesh.NumIndices * 4];

                uint faceStart = 0;
                uint edgeStart = faceStart + (uint)numFaces;
                uint vertStart = edgeStart + (uint)numEdgeValues;
                int pIx = 0;
                for (uint ix = 0; ix < (uint)mesh.NumIndices; ix++) {
                    newIndices[pIx + 0] = vertStart + vertexIndices[ix];
                    newIndices[pIx + 1] = edgeStart + edgeIndices[ix];
                    newIndices[pIx + 2] = faceStart + topo[ix].Face;
                    newIndices[pIx + 3] = edgeStart + edgeIndices[topo[ix].Prev];
                    pIx += 4;
                }
                output.Indices = newIndices;
                output.NumIndices = mesh.NumIndices * 4;
            } else {
                output.Indices = null;
                output.NumIndices = 0;
            }
        }

        // ------------------------------------------------------------------
        // Attribute drivers (ufbx.c:29472-29497)
        // ------------------------------------------------------------------

        // C: ufbxi_subdivide_attrib (ufbx.c:29472-29497). The typed attribute structs are
        // flattened to real buffers for the layer pass and written back afterwards.
        // `value_reals` must be 2..4 (C asserts); vertex-real attributes use the
        // VertexReal overload below (C reaches them through the same cast).

        static double[] Flatten(Array values, int dims)
        {
            int n = values.Length;
            double[] flat = new double[n * dims];
            switch (dims) {
                case 2: {
                    var src = (UfbxVec2[])values;
                    for (int i = 0; i < n; i++) { flat[i * 2 + 0] = src[i].X; flat[i * 2 + 1] = src[i].Y; }
                    break;
                }
                case 3: {
                    var src = (UfbxVec3[])values;
                    for (int i = 0; i < n; i++) { flat[i * 3 + 0] = src[i].X; flat[i * 3 + 1] = src[i].Y; flat[i * 3 + 2] = src[i].Z; }
                    break;
                }
                default: {
                    var src = (UfbxVec4[])values;
                    for (int i = 0; i < n; i++) { flat[i * 4 + 0] = src[i].X; flat[i * 4 + 1] = src[i].Y; flat[i * 4 + 2] = src[i].Z; flat[i * 4 + 3] = src[i].W; }
                    break;
                }
            }
            return flat;
        }

        static Array Unflatten(double[] flat, int dims)
        {
            int n = flat.Length / dims;
            switch (dims) {
                case 2: {
                    var dst = new UfbxVec2[n];
                    for (int i = 0; i < n; i++) { dst[i].X = flat[i * 2 + 0]; dst[i].Y = flat[i * 2 + 1]; }
                    return dst;
                }
                case 3: {
                    var dst = new UfbxVec3[n];
                    for (int i = 0; i < n; i++) { dst[i].X = flat[i * 3 + 0]; dst[i].Y = flat[i * 3 + 1]; dst[i].Z = flat[i * 3 + 2]; }
                    return dst;
                }
                default: {
                    var dst = new UfbxVec4[n];
                    for (int i = 0; i < n; i++) { dst[i].X = flat[i * 4 + 0]; dst[i].Y = flat[i * 4 + 1]; dst[i].Z = flat[i * 4 + 2]; dst[i].W = flat[i * 4 + 3]; }
                    return dst;
                }
            }
        }

        // C: `ufbxi_subdivide_attrib(sc, (ufbx_vertex_attrib*)attrib, boundary, check)`
        // for a vec2/3/4 attribute. Writes the subdivided values/indices back into
        // `attrib` (the caller passes the field by ref, matching C's pointer cast).
        static void SubdivideAttrib(SubdivideContext sc, ref UfbxVertexAttrib attrib, UfbxSubdivisionBoundary boundary, bool checkSplitData)
        {
            if (!attrib.Exists) return;

            int dims = attrib.ValueReals;

            SubdivideLayerInput input = new SubdivideLayerInput();
            input.Kind = dims;
            input.Values = Flatten(attrib.Values, dims);
            input.Indices = attrib.Indices;
            input.Boundary = boundary;
            input.CheckSplitData = checkSplitData;
            input.IgnoreIndices = false;

            SubdivideLayerOutput output = new SubdivideLayerOutput();
            SubdivideLayer(sc, output, input);

            attrib.Values = Unflatten((double[])output.Values, dims);
            attrib.Indices = output.Indices;
        }

        // Overload for the typed vertex-vec3 attributes (vertex_position/normal/tangent/...).
        static void SubdivideAttrib(SubdivideContext sc, ref UfbxVertexVec3 attrib, UfbxSubdivisionBoundary boundary, bool checkSplitData)
        {
            UfbxVertexAttrib va = default;
            va.Exists = attrib.Exists;
            va.Values = attrib.Values;
            va.Indices = attrib.Indices;
            va.ValueReals = attrib.ValueReals;
            va.UniquePerVertex = attrib.UniquePerVertex;
            SubdivideAttrib(sc, ref va, boundary, checkSplitData);
            attrib.Exists = va.Exists;
            attrib.Values = (UfbxVec3[])va.Values;
            attrib.Indices = va.Indices;
            attrib.ValueReals = va.ValueReals;
            attrib.UniquePerVertex = va.UniquePerVertex;
        }

        // Overload for the typed vertex-vec2 attributes (vertex_uv).
        static void SubdivideAttrib(SubdivideContext sc, ref UfbxVertexVec2 attrib, UfbxSubdivisionBoundary boundary, bool checkSplitData)
        {
            UfbxVertexAttrib va = default;
            va.Exists = attrib.Exists;
            va.Values = attrib.Values;
            va.Indices = attrib.Indices;
            va.ValueReals = attrib.ValueReals;
            va.UniquePerVertex = attrib.UniquePerVertex;
            SubdivideAttrib(sc, ref va, boundary, checkSplitData);
            attrib.Exists = va.Exists;
            attrib.Values = (UfbxVec2[])va.Values;
            attrib.Indices = va.Indices;
            attrib.ValueReals = va.ValueReals;
            attrib.UniquePerVertex = va.UniquePerVertex;
        }

        // Overload for the typed vertex-vec4 attributes (vertex_color).
        static void SubdivideAttrib(SubdivideContext sc, ref UfbxVertexVec4 attrib, UfbxSubdivisionBoundary boundary, bool checkSplitData)
        {
            UfbxVertexAttrib va = default;
            va.Exists = attrib.Exists;
            va.Values = attrib.Values;
            va.Indices = attrib.Indices;
            va.ValueReals = attrib.ValueReals;
            va.UniquePerVertex = attrib.UniquePerVertex;
            SubdivideAttrib(sc, ref va, boundary, checkSplitData);
            attrib.Exists = va.Exists;
            attrib.Values = (UfbxVec4[])va.Values;
            attrib.Indices = va.Indices;
            attrib.ValueReals = va.ValueReals;
            attrib.UniquePerVertex = va.UniquePerVertex;
        }

        // Overload for the typed vertex-real attributes (vertex_crease uses its own
        // routine; this overload exists for symmetry with C's cast to ufbx_vertex_attrib).
        static void SubdivideAttrib(SubdivideContext sc, ref UfbxVertexReal attrib, UfbxSubdivisionBoundary boundary, bool checkSplitData)
        {
            UfbxVertexAttrib va = default;
            va.Exists = attrib.Exists;
            va.Values = attrib.Values;
            va.Indices = attrib.Indices;
            va.ValueReals = attrib.ValueReals;
            va.UniquePerVertex = attrib.UniquePerVertex;
            SubdivideAttrib(sc, ref va, boundary, checkSplitData);
            attrib.Exists = va.Exists;
            attrib.Values = (double[])va.Values;
            attrib.Indices = va.Indices;
            attrib.ValueReals = va.ValueReals;
            attrib.UniquePerVertex = va.UniquePerVertex;
        }

        // ------------------------------------------------------------------
        // Weight propagation (ufbx.c:29499-29602)
        // ------------------------------------------------------------------

        // C: ufbxi_subdivision_copy_weights (ufbx.c:29499-29511).
        static SubdivVertexWeights[] SubdivisionCopyWeights(SubdivideContext sc, UfbxSubdivisionWeightRange[] ranges, UfbxSubdivisionWeight[] weights)
        {
            SubdivVertexWeights[] dst = new SubdivVertexWeights[ranges.Length];
            for (int i = 0; i < ranges.Length; i++) dst[i] = new SubdivVertexWeights();

            for (int i = 0; i < ranges.Length; i++) {
                UfbxSubdivisionWeightRange range = ranges[i];
                dst[i].Weights = weights;
                dst[i].Offset = (int)range.WeightBegin;
                dst[i].NumWeights = (int)range.NumWeights;
            }

            return dst;
        }

        // C: ufbxi_init_source_vertex_weights (ufbx.c:29513-29527).
        static SubdivVertexWeights[] InitSourceVertexWeights(SubdivideContext sc, int numVertices)
        {
            SubdivVertexWeights[] dst = new SubdivVertexWeights[numVertices];
            UfbxSubdivisionWeight[] weights = new UfbxSubdivisionWeight[numVertices];
            for (int i = 0; i < numVertices; i++) dst[i] = new SubdivVertexWeights();

            for (int i = 0; i < numVertices; i++) {
                dst[i].Weights = weights;
                dst[i].Offset = i;
                dst[i].NumWeights = 1;
                UfbxSubdivisionWeight w = weights[i];
                w.Index = (uint)i;
                w.Weight = 1.0f;
                weights[i] = w;
            }

            return dst;
        }

        // C: ufbxi_init_skin_weights (ufbx.c:29529-29554).
        static SubdivVertexWeights[] InitSkinWeights(SubdivideContext sc, int numVertices, UfbxSkinDeformer skin)
        {
            SubdivVertexWeights[] dst = new SubdivVertexWeights[numVertices];

            for (int i = 0; i < numVertices; i++) {
                dst[i] = new SubdivVertexWeights();
                UfbxSkinVertex vertex = skin.Vertices[i];
                // C: ufbxi_min_sz(sc->max_vertex_weights, vertex.num_weights) with
                // max_vertex_weights == SIZE_MAX meaning "unlimited".
                int numWeights = sc.MaxVertexWeights == -1
                    ? (int)vertex.NumWeights
                    : (sc.MaxVertexWeights < (long)vertex.NumWeights ? sc.MaxVertexWeights : (int)vertex.NumWeights);

                UfbxSubdivisionWeight[] weights = new UfbxSubdivisionWeight[numWeights];

                dst[i].Weights = weights;
                dst[i].Offset = 0;
                dst[i].NumWeights = numWeights;
                for (int wi = 0; wi < numWeights; wi++) {
                    UfbxSkinWeight skinWeight = skin.Weights[vertex.WeightBegin + wi];
                    UfbxSubdivisionWeight w = weights[wi];
                    w.Index = skinWeight.ClusterIndex;
                    w.Weight = skinWeight.Weight;
                    weights[wi] = w;
                }
            }

            return dst;
        }

        // C: ufbxi_subdivide_weights (ufbx.c:29556-29602).
        static void SubdivideWeights(SubdivideContext sc, out UfbxSubdivisionWeightRange[] ranges, out UfbxSubdivisionWeight[] weights, SubdivVertexWeights[] src)
        {
            SubdivideLayerInput input = new SubdivideLayerInput();
            input.Kind = -1;    // C: sum_fn = ufbxi_subdivide_sum_vertex_weights
            input.Values = src;
            input.Indices = sc.SrcMesh.VertexIndices;
            input.Boundary = sc.Opts.Boundary;
            input.CheckSplitData = false;
            input.IgnoreIndices = true;

            sc.TotalWeights = 0;

            SubdivideLayerOutput output = new SubdivideLayerOutput();
            SubdivideLayer(sc, output, input);

            int numVertices = output.NumValues;

            UfbxSubdivisionWeightRange[] dstRanges = new UfbxSubdivisionWeightRange[numVertices];
            UfbxSubdivisionWeight[] dstWeights = new UfbxSubdivisionWeight[sc.TotalWeights];

            SubdivVertexWeights[] srcWeights = (SubdivVertexWeights[])output.Values;

            int weightOffset = 0;
            for (int vi = 0; vi < numVertices; vi++) {
                SubdivVertexWeights ws = srcWeights[vi];

                dstRanges[vi].WeightBegin = (uint)weightOffset;
                dstRanges[vi].NumWeights = (uint)ws.NumWeights;
                Array.Copy(ws.Weights, ws.Offset, dstWeights, weightOffset, ws.NumWeights);
                weightOffset += ws.NumWeights;
            }

            ranges = dstRanges;
            weights = dstWeights;
        }

        // C: ufbxi_subdivide_vertex_crease (ufbx.c:29604-29637).
        static void SubdivideVertexCrease(SubdivideContext sc, ref UfbxVertexReal dst, UfbxVertexReal src)
        {
            int srcIndices = src.Indices.Length;
            int srcValues = src.Values.Length;

            dst.Values = new double[srcValues + 1];
            dst.Values[srcValues] = 0.0f;

            dst.Indices = new uint[srcIndices * 4];

            // Reduce the amount of vertex crease on each iteration
            for (int i = 0; i < srcValues; i++) {
                double crease = src.Values[i];
                // C: `0.999f`/`0.1f` are float literals (ufbx.c:29621-29622)
                if (crease < 0.999f) crease -= 0.1f;
                if (crease < 0.0f) crease = 0.0f;
                dst.Values[i] = crease;
            }

            // Write the crease at the vertex corner and zero (at `src_values`) on other ones
            uint zeroIndex = (uint)srcValues;
            for (int i = 0; i < srcIndices; i++) {
                dst.Indices[i * 4 + 0] = src.Indices[i];
                dst.Indices[i * 4 + 1] = zeroIndex;
                dst.Indices[i * 4 + 2] = zeroIndex;
                dst.Indices[i * 4 + 3] = zeroIndex;
            }
        }

        // ------------------------------------------------------------------
        // Mesh level (ufbx.c:29639-29933)
        // ------------------------------------------------------------------

        // C: `*result = *mesh` (ufbx.c:29644) -- per-field shallow copy including the
        // embedded element header. Array references are shared exactly like C shares the
        // list data pointers.
        static void CopyMeshFields(UfbxMesh dst, UfbxMesh src)
        {
            // C: ufbx_element header (shared base)
            dst.Name = src.Name;
            dst.Props = src.Props;
            dst.ElementId = src.ElementId;
            dst.TypedId = src.TypedId;
            dst.Instances = src.Instances;
            dst.Type = src.Type;
            dst.ConnectionsSrc = src.ConnectionsSrc;
            dst.ConnectionsDst = src.ConnectionsDst;
            dst.DomNode = src.DomNode;
            dst.Scene = src.Scene;

            // C: ufbx_mesh body (ufbx.h order)
            dst.NumVertices = src.NumVertices;
            dst.NumIndices = src.NumIndices;
            dst.NumFaces = src.NumFaces;
            dst.NumTriangles = src.NumTriangles;
            dst.NumEdges = src.NumEdges;

            dst.MaxFaceTriangles = src.MaxFaceTriangles;

            dst.NumEmptyFaces = src.NumEmptyFaces;
            dst.NumPointFaces = src.NumPointFaces;
            dst.NumLineFaces = src.NumLineFaces;

            dst.Faces = src.Faces;
            dst.FaceSmoothing = src.FaceSmoothing;
            dst.FaceMaterial = src.FaceMaterial;
            dst.FaceGroup = src.FaceGroup;
            dst.FaceHole = src.FaceHole;

            dst.Edges = src.Edges;
            dst.EdgeSmoothing = src.EdgeSmoothing;
            dst.EdgeCrease = src.EdgeCrease;
            dst.EdgeVisibility = src.EdgeVisibility;

            dst.VertexIndices = src.VertexIndices;
            dst.Vertices = src.Vertices;

            dst.VertexFirstIndex = src.VertexFirstIndex;

            dst.VertexPosition = src.VertexPosition;
            dst.VertexNormal = src.VertexNormal;
            dst.VertexUv = src.VertexUv;
            dst.VertexTangent = src.VertexTangent;
            dst.VertexBitangent = src.VertexBitangent;
            dst.VertexColor = src.VertexColor;
            dst.VertexCrease = src.VertexCrease;

            dst.UvSets = src.UvSets;
            dst.ColorSets = src.ColorSets;

            dst.Materials = src.Materials;

            dst.FaceGroups = src.FaceGroups;

            dst.MaterialParts = src.MaterialParts;
            dst.FaceGroupParts = src.FaceGroupParts;

            dst.MaterialPartUsageOrder = src.MaterialPartUsageOrder;

            dst.SkinnedIsLocal = src.SkinnedIsLocal;
            dst.SkinnedPosition = src.SkinnedPosition;
            dst.SkinnedNormal = src.SkinnedNormal;

            dst.SkinDeformers = src.SkinDeformers;
            dst.BlendDeformers = src.BlendDeformers;
            dst.CacheDeformers = src.CacheDeformers;
            dst.AllDeformers = src.AllDeformers;

            dst.SubdivisionPreviewLevels = src.SubdivisionPreviewLevels;
            dst.SubdivisionRenderLevels = src.SubdivisionRenderLevels;
            dst.SubdivisionDisplayMode = src.SubdivisionDisplayMode;
            dst.SubdivisionBoundary = src.SubdivisionBoundary;
            dst.SubdivisionUvBoundary = src.SubdivisionUvBoundary;

            dst.ReversedWinding = src.ReversedWinding;
            dst.GeneratedNormals = src.GeneratedNormals;

            dst.SubdivisionEvaluated = src.SubdivisionEvaluated;
            dst.SubdivisionResult = src.SubdivisionResult;

            dst.FromTessellatedNurbs = src.FromTessellatedNurbs;
        }

        // C: ufbxi_subdivide_mesh_level (ufbx.c:29639-29933).
        //
        // One subdivision pass over the whole mesh: topology, every attribute layer, the
        // creases, then quad faces / edges / per-face flags, and finally the mesh
        // finalize chain (material parts, mesh, face groups).
        static void SubdivideMeshLevel(SubdivideContext sc)
        {
            UfbxMesh mesh = sc.SrcMesh;
            UfbxMesh result = new UfbxMesh();
            CopyMeshFields(result, mesh);   // C: *result = *mesh
            sc.DstMesh = result;

            UfbxTopoEdge[] topo = new UfbxTopoEdge[mesh.NumIndices];
            UfbxTopology.ComputeTopology(mesh, topo, mesh.NumIndices);
            sc.Topo = topo;
            sc.NumTopo = mesh.NumIndices;

            SubdivideAttrib(sc, ref result.VertexPosition, sc.Opts.Boundary, false);

            // C: memset(&result->vertex_uv, 0, ...) etc. (ufbx.c:29654-29657)
            result.VertexUv = default;
            result.VertexTangent = default;
            result.VertexBitangent = default;
            result.VertexColor = default;

            // C: ufbxi_push_copy of the uv/color set arrays (ufbx.c:29659-29663) -- the
            // pass mutates the copies, not the source mesh's sets.
            result.UvSets = result.UvSets != null ? (UfbxUvSet[])result.UvSets.Clone() : new UfbxUvSet[0];
            result.ColorSets = result.ColorSets != null ? (UfbxColorSet[])result.ColorSets.Clone() : new UfbxColorSet[0];

            for (int i = 0; i < result.UvSets.Length; i++) {
                SubdivideAttrib(sc, ref result.UvSets[i].VertexUv, sc.Opts.UvBoundary, true);
                if (sc.Opts.InterpolateTangents) {
                    SubdivideAttrib(sc, ref result.UvSets[i].VertexTangent, sc.Opts.UvBoundary, true);
                    SubdivideAttrib(sc, ref result.UvSets[i].VertexBitangent, sc.Opts.UvBoundary, true);
                } else {
                    result.UvSets[i].VertexTangent = default;
                    result.UvSets[i].VertexBitangent = default;
                }
            }

            for (int i = 0; i < result.ColorSets.Length; i++) {
                SubdivideAttrib(sc, ref result.ColorSets[i].VertexColor, sc.Opts.UvBoundary, true);
            }

            if (result.UvSets.Length > 0) {
                result.VertexUv = result.UvSets[0].VertexUv;
                result.VertexBitangent = result.UvSets[0].VertexBitangent;
                result.VertexTangent = result.UvSets[0].VertexTangent;
            }
            if (result.ColorSets.Length > 0) {
                result.VertexColor = result.ColorSets[0].VertexColor;
            }

            if (sc.Opts.InterpolateNormals && !sc.Opts.IgnoreNormals) {
                SubdivideAttrib(sc, ref result.VertexNormal, sc.Opts.Boundary, true);
                UfbxVec3[] normalValues = result.VertexNormal.Values;
                for (int i = 0; i < normalValues.Length; i++) {
                    normalValues[i] = UfbxVec3.Normalize3(normalValues[i]);     // C: ufbxi_slow_normalize3
                }
                if (ReferenceEquals(mesh.SkinnedNormal.Values, mesh.VertexNormal.Values)) {
                    result.SkinnedNormal = result.VertexNormal;
                } else {
                    SubdivideAttrib(sc, ref result.SkinnedNormal, sc.Opts.Boundary, true);
                    UfbxVec3[] skinnedValues = result.SkinnedNormal.Values;
                    for (int i = 0; i < skinnedValues.Length; i++) {
                        skinnedValues[i] = UfbxVec3.Normalize3(skinnedValues[i]);
                    }
                }
            }

            if (result.VertexCrease.Exists) {
                SubdivideVertexCrease(sc, ref result.VertexCrease, mesh.VertexCrease);
            }

            if (ReferenceEquals(mesh.SkinnedPosition.Values, mesh.VertexPosition.Values)) {
                result.SkinnedPosition = result.VertexPosition;
            } else {
                SubdivideAttrib(sc, ref result.SkinnedPosition, sc.Opts.Boundary, false);
            }

            result.SubdivisionResult = new UfbxSubdivisionResult();

            if (sc.Opts.EvaluateSourceVertices || sc.Opts.EvaluateSkinWeights) {
                UfbxSubdivisionResult meshSub = mesh.SubdivisionResult;

                UfbxSkinDeformer skin = null;
                if (sc.Opts.EvaluateSkinWeights) {
                    if (mesh.SkinDeformers != null && mesh.SkinDeformers.Length > 0) {
                        UfbxiFail.CheckNoDesc(sc.Opts.SkinDeformerIndex < mesh.SkinDeformers.Length,
                            "sc->opts.skin_deformer_index < mesh->skin_deformers.count");
                        skin = mesh.SkinDeformers[sc.Opts.SkinDeformerIndex];
                    }
                }

                int maxWeights = 0;
                if (sc.Opts.EvaluateSourceVertices) {
                    if (maxWeights < mesh.NumVertices) maxWeights = mesh.NumVertices;
                }
                if (skin != null) {
                    if (maxWeights < skin.Clusters.Length) maxWeights = skin.Clusters.Length;
                }

                sc.TmpVertexWeights = new double[mesh.NumVertices];     // C: ufbxi_push_zero
                sc.TmpWeights = new UfbxSubdivisionWeight[maxWeights];

                if (sc.Opts.EvaluateSourceVertices) {
                    // C: max_source_vertices != 0 ? ... : SIZE_MAX
                    sc.MaxVertexWeights = sc.Opts.MaxSourceVertices != 0 ? sc.Opts.MaxSourceVertices : -1;

                    SubdivVertexWeights[] weights;
                    if (meshSub != null && meshSub.SourceVertexRanges != null && meshSub.SourceVertexRanges.Length > 0) {
                        weights = SubdivisionCopyWeights(sc, meshSub.SourceVertexRanges, meshSub.SourceVertexWeights);
                    } else {
                        weights = InitSourceVertexWeights(sc, mesh.NumVertices);
                    }

                    SubdivideWeights(sc, out var ranges, out var wts, weights);
                    result.SubdivisionResult.SourceVertexRanges = ranges;
                    result.SubdivisionResult.SourceVertexWeights = wts;
                }

                if (skin != null) {
                    sc.MaxVertexWeights = sc.Opts.MaxSkinWeights != 0 ? sc.Opts.MaxSkinWeights : -1;

                    SubdivVertexWeights[] weights;
                    if (meshSub != null && meshSub.SourceVertexRanges != null && meshSub.SourceVertexRanges.Length > 0) {
                        weights = SubdivisionCopyWeights(sc, meshSub.SkinClusterRanges, meshSub.SkinClusterWeights);
                    } else {
                        weights = InitSkinWeights(sc, mesh.NumVertices, skin);
                    }

                    SubdivideWeights(sc, out var ranges, out var wts, weights);
                    result.SubdivisionResult.SkinClusterRanges = ranges;
                    result.SubdivisionResult.SkinClusterWeights = wts;
                }

            }

            result.NumVertices = result.VertexPosition.Values.Length;
            result.NumIndices = mesh.NumIndices * 4;
            result.NumFaces = mesh.NumIndices;
            result.NumTriangles = mesh.NumIndices * 2;

            result.VertexIndices = result.VertexPosition.Indices;
            result.Vertices = result.VertexPosition.Values;

            result.Faces = new UfbxFace[result.NumFaces];

            for (int i = 0; i < result.NumFaces; i++) {
                result.Faces[i].IndexBegin = (uint)(i * 4);
                result.Faces[i].NumIndices = 4;
            }

            if (mesh.Edges != null) {
                result.NumEdges = mesh.NumEdges * 2 + result.NumFaces;
                result.Edges = new UfbxEdge[result.NumEdges];

                if (mesh.EdgeCrease != null) {
                    result.EdgeCrease = new double[result.NumEdges];
                }
                if (mesh.EdgeSmoothing != null) {
                    result.EdgeSmoothing = new bool[result.NumEdges];
                }
                if (mesh.EdgeVisibility != null) {
                    result.EdgeVisibility = new bool[result.NumEdges];
                }

                int di = 0;
                for (int i = 0; i < mesh.NumEdges; i++) {
                    UfbxEdge edge = mesh.Edges[i];
                    uint faceIx = topo[edge.A].Face;
                    UfbxFace face = mesh.Faces[faceIx];
                    uint offset = edge.A - face.IndexBegin;
                    uint next = (offset + 1) % face.NumIndices;

                    uint a = (face.IndexBegin + offset) * 4;
                    uint b = (face.IndexBegin + next) * 4;

                    result.Edges[di + 0].A = a;
                    result.Edges[di + 0].B = a + 1;
                    result.Edges[di + 1].A = b + 3;
                    result.Edges[di + 1].B = b;

                    if (mesh.EdgeCrease != null) {
                        // C: `(ufbx_real)0.1` -- double literal (ufbx.c:29828), unlike the
                        // float literal `0.1f` in ufbxi_subdivide_vertex_crease.
                        double crease = mesh.EdgeCrease[i];
                        if (crease < 0.999f) crease -= 0.1;
                        if (crease < 0.0f) crease = 0.0f;
                        result.EdgeCrease[di + 0] = crease;
                        result.EdgeCrease[di + 1] = crease;
                    }

                    if (mesh.EdgeSmoothing != null) {
                        result.EdgeSmoothing[di + 0] = mesh.EdgeSmoothing[i];
                        result.EdgeSmoothing[di + 1] = mesh.EdgeSmoothing[i];
                    }

                    if (mesh.EdgeVisibility != null) {
                        result.EdgeVisibility[di + 0] = mesh.EdgeVisibility[i];
                        result.EdgeVisibility[di + 1] = mesh.EdgeVisibility[i];
                    }

                    di += 2;
                }

                for (int fi = 0; fi < result.NumFaces; fi++) {
                    result.Edges[di].A = (uint)(fi * 4 + 1);
                    result.Edges[di].B = (uint)(fi * 4 + 2);

                    if (result.EdgeCrease != null) {
                        result.EdgeCrease[di] = 0.0f;
                    }

                    if (result.EdgeSmoothing != null) {
                        result.EdgeSmoothing[di + 0] = true;
                    }

                    if (result.EdgeVisibility != null) {
                        result.EdgeVisibility[di + 0] = false;
                    }

                    di++;
                }
            }

            if (mesh.FaceMaterial != null) {
                result.FaceMaterial = new uint[result.NumFaces];
            }
            if (mesh.FaceSmoothing != null) {
                result.FaceSmoothing = new bool[result.NumFaces];
            }
            if (mesh.FaceGroup != null) {
                result.FaceGroup = new uint[result.NumFaces];
            }
            if (mesh.FaceHole != null) {
                result.FaceHole = new bool[result.NumFaces];
            }

            if (result.MaterialParts != null && result.MaterialParts.Length > 0) {
                // C: ufbxi_push_zero of material_parts (ufbx.c:29888-29891)
                result.MaterialParts = new UfbxMeshPart[result.MaterialParts.Length];
            }

            int indexOffset = 0;
            for (int i = 0; i < mesh.NumFaces; i++) {
                UfbxFace face = mesh.Faces[i];

                if (mesh.FaceMaterial != null) {
                    uint mat = mesh.FaceMaterial[i];
                    for (uint ci = 0; ci < face.NumIndices; ci++) {
                        result.FaceMaterial[indexOffset + ci] = mat;
                    }
                }
                if (mesh.FaceSmoothing != null) {
                    bool flag = mesh.FaceSmoothing[i];
                    for (uint ci = 0; ci < face.NumIndices; ci++) {
                        result.FaceSmoothing[indexOffset + ci] = flag;
                    }
                }
                if (mesh.FaceGroup != null) {
                    uint group = mesh.FaceGroup[i];
                    for (uint ci = 0; ci < face.NumIndices; ci++) {
                        result.FaceGroup[indexOffset + ci] = group;
                    }
                }
                if (mesh.FaceHole != null) {
                    bool flag = mesh.FaceHole[i];
                    for (uint ci = 0; ci < face.NumIndices; ci++) {
                        result.FaceHole[indexOffset + ci] = flag;
                    }
                }
                indexOffset += (int)face.NumIndices;
            }

            // Will be filled in by `ufbxi_finalize_mesh()`.
            // C: result->vertex_first_index.count = 0 (ufbx.c:29926); the port drops the
            // stale array (FinalizeMesh rebuilds it).
            result.VertexFirstIndex = null;

            FinalizeMeshMaterial(result);
            UfbxiSceneBuild.FinalizeMesh(result);
            UfbxiGeometry.UpdateFaceGroups(null, result, true);     // C: ufbxi_update_face_groups(..., true)
        }

        // ------------------------------------------------------------------
        // Subdivision entry (ufbx.c:29935-30075)
        // ------------------------------------------------------------------

        // C: ufbxi_subdivide_mesh_imp (ufbx.c:29935-30042). The C arena juggling
        // (result/source/tmp buffers, ufbx.c:29945-29970) has no managed counterpart:
        // each level's `sc->src_mesh = sc->dst_mesh` becomes a reference move.
        static void SubdivideMeshImp(SubdivideContext sc, int level)
        {
            if (sc.Opts.Boundary == UfbxSubdivisionBoundary.Default) {
                sc.Opts.Boundary = sc.SrcMesh.SubdivisionBoundary;
            }

            if (sc.Opts.UvBoundary == UfbxSubdivisionBoundary.Default) {
                sc.Opts.UvBoundary = sc.SrcMesh.SubdivisionUvBoundary;
            }

            for (int i = 1; i < level; i++) {
                SubdivideMeshLevel(sc);
                sc.SrcMesh = sc.DstMesh;
            }

            SubdivideMeshLevel(sc);

            UfbxMesh mesh = sc.DstMesh;

            // Subdivision always results in a mesh that consists only of quads
            mesh.MaxFaceTriangles = 2;
            mesh.NumEmptyFaces = 0;
            mesh.NumPointFaces = 0;
            mesh.NumLineFaces = 0;

            if (!sc.Opts.InterpolateNormals) {
                mesh.VertexNormal = default;
                mesh.SkinnedNormal = default;
            }

            if (!sc.Opts.InterpolateNormals && !sc.Opts.IgnoreNormals) {

                UfbxTopoEdge[] topo = new UfbxTopoEdge[mesh.NumIndices];
                UfbxTopology.ComputeTopology(mesh, topo, mesh.NumIndices);

                uint[] normalIndices = new uint[mesh.NumIndices];

                int numNormals = UfbxTopology.GenerateNormalMapping(mesh, topo, mesh.NumIndices, normalIndices, mesh.NumIndices, true);
                if (numNormals == mesh.NumVertices) {
                    mesh.SkinnedNormal.UniquePerVertex = true;
                }

                // C: normal_data[0] = ufbx_zero_vec3 zero guard (ufbx.c:29999-30002);
                // the port skips the guard element (see file header).
                UfbxVec3[] normalData = new UfbxVec3[numNormals];

                UfbxTopology.ComputeNormals(mesh, mesh.SkinnedPosition, normalIndices, mesh.NumIndices, normalData, numNormals);

                mesh.GeneratedNormals = true;
                mesh.VertexNormal.Exists = true;
                mesh.VertexNormal.Values = normalData;
                mesh.VertexNormal.Indices = normalIndices;

                mesh.SkinnedNormal = mesh.VertexNormal;
            }

            // C: parent refcount selection (ufbx.c:30016-30021) and
            // `ufbxi_patch_mesh_reals` (ufbx.c:30023).
            UfbxiGeometry.PatchMeshReals(mesh);

            // C: imp push + `sc->imp->mesh = sc->dst_mesh` value copy + refcount init
            // (ufbx.c:30025-30039) -- the returned object IS sc.DstMesh here.
            // C: subdivision_result result/temp memory statistics (ufbx.c:30028-30031)
            // stay zero (no arenas).
            sc.DstMesh.SubdivisionEvaluated = true;
        }

        // C: ufbxi_subdivide_mesh (ufbx.c:30044-30075).
        internal static UfbxMesh SubdivideMeshEntry(UfbxMesh mesh, int level, UfbxSubdivideOpts userOpts)
        {
            SubdivideContext sc = new SubdivideContext();
            sc.Opts = CloneSubdivideOpts(userOpts);     // C: zeroed context + *user_opts

            // C: sc.src_mesh = *mesh is a value copy; the port shares the reference --
            // the source mesh is never mutated (every layer copies into a new result).
            sc.SrcMesh = mesh;

            SubdivideMeshImp(sc, level);

            return sc.DstMesh;
        }

        // C: `sc.opts = *user_opts` (ufbx.c:30048) / zeroed opts.
        static UfbxSubdivideOpts CloneSubdivideOpts(UfbxSubdivideOpts opts)
        {
            var clone = new UfbxSubdivideOpts();
            if (opts != null) {
                clone.TempAllocator = opts.TempAllocator;
                clone.ResultAllocator = opts.ResultAllocator;
                clone.Boundary = opts.Boundary;
                clone.UvBoundary = opts.UvBoundary;
                clone.IgnoreNormals = opts.IgnoreNormals;
                clone.InterpolateNormals = opts.InterpolateNormals;
                clone.InterpolateTangents = opts.InterpolateTangents;
                clone.EvaluateSourceVertices = opts.EvaluateSourceVertices;
                clone.MaxSourceVertices = opts.MaxSourceVertices;
                clone.EvaluateSkinWeights = opts.EvaluateSkinWeights;
                clone.MaxSkinWeights = opts.MaxSkinWeights;
                clone.SkinDeformerIndex = opts.SkinDeformerIndex;
            } else {
                // C: zeroed allocator opts; the port's defaults match C's NULL allocator
                // behavior.
                clone.TempAllocator = null;
                clone.ResultAllocator = null;
            }
            return clone;
        }

        // ==================================================================
        // NURBS tessellation (ufbx.c:27848-28247)
        // ==================================================================

        // C: ufbxi_tessellate_nurbs_curve_imp (ufbx.c:27848-27939). The C
        // ufbxi_tessellate_curve_context fields fold into parameters/locals; the
        // ufbxi_line_curve_imp refcount has no managed counterpart.
        internal static UfbxLineCurve TessellateNurbsCurveImp(UfbxNurbsCurve curve, UfbxTessellateCurveOpts opts)
        {
            if (opts.SpanSubdivision <= 0) {
                opts.SpanSubdivision = 4;
            }
            int numSub = opts.SpanSubdivision;

            UfbxiFail.CheckMsg(curve.Basis.Valid && curve.ControlPoints.Length > 0, "Bad NURBS geometry");

            int numSpans = curve.Basis.Spans.Length;

            // Check conservatively that we don't overflow anything
            // C: over_spans = num_spans * 2 * sizeof(ufbx_real); over = over_spans * num_sub
            // (ufbx.c:27867-27872) -- 64-bit size_t overflow is not representable.
            {
                ulong overSpans = (ulong)numSpans * 2 * 8;
                ulong over = overSpans * (ulong)numSub;
                UfbxiFail.CheckNoDesc(over / overSpans == (ulong)numSub, "!ufbxi_does_overflow(over, over_spans, num_sub)");
            }

            bool isOpen = curve.Basis.Topology == UfbxNurbsTopology.Open;

            int numIndices = numSpans + (numSpans - 1) * (numSub - 1);
            int numVertices = numIndices - (isOpen ? 0 : 1);
            UfbxiFail.CheckNoDesc(numIndices <= int.MaxValue, "num_indices <= INT32_MAX");

            uint[] indices = new uint[numIndices];
            UfbxVec3[] vertices = new UfbxVec3[numVertices];
            UfbxLineSegment[] segments = new UfbxLineSegment[1];

            for (int spanIx = 0; spanIx < numSpans; spanIx++) {
                int numSplits = spanIx + 1 == numSpans ? 1 : numSub;

                for (int subIx = 0; subIx < numSplits; subIx++) {
                    int ix = spanIx * numSub + subIx;

                    if (ix < numVertices) {
                        double u = curve.Basis.Spans[spanIx];
                        if (subIx > 0) {
                            double t = (double)subIx / (double)numSub;
                            u = u * (1.0f - t) + t * curve.Basis.Spans[spanIx + 1];
                        }

                        UfbxCurvePoint point = EvaluateNurbsCurve(curve, u);
                        vertices[ix] = point.Position;
                        indices[ix] = (uint)ix;
                    } else {
                        indices[ix] = 0;
                    }
                }
            }

            segments[0].IndexBegin = 0;
            segments[0].NumIndices = (uint)numIndices;

            UfbxLineCurve line = new UfbxLineCurve();
            line.Name = string.Empty;       // C: ufbxi_empty_char
            line.Type = UfbxElementType.LineCurve;
            line.TypedId = uint.MaxValue;
            line.ElementId = uint.MaxValue;

            line.Color.X = 1.0f;
            line.Color.Y = 1.0f;
            line.Color.Z = 1.0f;

            line.ControlPoints = vertices;
            line.PointIndices = indices;
            line.Segments = segments;

            line.FromTessellatedNurbs = true;

            // C: imp/refcount anchored to the scene (ufbx.c:27928-27936) -- no counterpart.
            return line;
        }

        // C: ufbxi_tessellate_nurbs_surface_imp (ufbx.c:27941-28247).
        internal static UfbxMesh TessellateNurbsSurfaceImp(UfbxNurbsSurface surface, UfbxTessellateSurfaceOpts opts)
        {
            if (opts.SpanSubdivisionU <= 0) {
                opts.SpanSubdivisionU = 4;
            }
            if (opts.SpanSubdivisionV <= 0) {
                opts.SpanSubdivisionV = 4;
            }

            int subU = opts.SpanSubdivisionU;
            int subV = opts.SpanSubdivisionV;

            UfbxiFail.CheckMsg(surface.BasisU.Valid && surface.BasisV.Valid
                && surface.NumControlPointsU > 0 && surface.NumControlPointsV > 0, "Bad NURBS geometry");

            bool openU = surface.BasisU.Topology == UfbxNurbsTopology.Open;
            bool openV = surface.BasisV.Topology == UfbxNurbsTopology.Open;

            int spansU = surface.BasisU.Spans.Length;
            int spansV = surface.BasisV.Spans.Length;

            // Check conservatively that we don't overflow anything
            // (ufbx.c:27973-27983; 64-bit size_t overflow is not representable)
            {
                ulong overSpansU = (ulong)spansU * 2 * 8;
                ulong overSpansV = (ulong)spansV * 2 * 8;
                ulong overU = overSpansU * (ulong)subU;
                ulong overV = overSpansV * (ulong)subV;
                ulong overUv = overU * overV;
                UfbxiFail.CheckNoDesc(overU / overSpansU == (ulong)subU, "!ufbxi_does_overflow(over_u, over_spans_u, sub_u)");
                UfbxiFail.CheckNoDesc(overV / overSpansV == (ulong)subV, "!ufbxi_does_overflow(over_v, over_spans_v, sub_v)");
                UfbxiFail.CheckNoDesc(overU == 0 || overUv / overU == overV, "!ufbxi_does_overflow(over_uv, over_u, over_v)");
            }

            int facesU = (spansU - 1) * subU;
            int facesV = (spansV - 1) * subV;

            int indicesU = spansU + (spansU - 1) * (subU - 1);
            int indicesV = spansV + (spansV - 1) * (subV - 1);

            int numFaces = facesU * facesV;
            int numIndices = indicesU * indicesV;
            UfbxiFail.CheckNoDesc(numIndices <= int.MaxValue, "num_indices <= INT32_MAX");

            uint[] positionIx = new uint[numIndices];
            // C: num_indices+1 sized with zero guards (ufbx.c:27996-28007); the port
            // allocates data-only arrays (see file header).
            UfbxVec3[] positions = new UfbxVec3[numIndices];
            UfbxVec3[] normals = new UfbxVec3[numIndices];
            UfbxVec2[] uvs = new UfbxVec2[numIndices];
            UfbxVec3[] tangents = new UfbxVec3[numIndices];
            UfbxVec3[] bitangents = new UfbxVec3[numIndices];

            uint numPositions = 0;

            for (int spanV = 0; spanV < spansV; spanV++) {
                int splitsV = spanV + 1 == spansV ? 1 : subV;

                for (int splitV = 0; splitV < splitsV; splitV++) {
                    int ixV = spanV * subV + splitV;

                    double v = surface.BasisV.Spans[spanV];
                    if (splitV > 0) {
                        double t = (double)splitV / (double)splitsV;
                        v = v * (1.0f - t) + t * surface.BasisV.Spans[spanV + 1];
                    }
                    double originalV = v;
                    if (spanV + 1 == spansV && !openV) {
                        v = surface.BasisV.Spans[0];
                    }

                    for (int spanU = 0; spanU < spansU; spanU++) {
                        int splitsU = spanU + 1 == spansU ? 1 : subU;
                        for (int splitU = 0; splitU < splitsU; splitU++) {
                            int ixU = spanU * subU + splitU;

                            double u = surface.BasisU.Spans[spanU];
                            if (splitU > 0) {
                                double t = (double)splitU / (double)splitsU;
                                u = u * (1.0f - t) + t * surface.BasisU.Spans[spanU + 1];
                            }
                            double originalU = u;
                            if (spanU + 1 == spansU && !openU) {
                                u = surface.BasisU.Spans[0];
                            }

                            UfbxSurfacePoint point = EvaluateNurbsSurface(surface, u, v);
                            UfbxVec3 pos = point.Position;

                            UfbxVec3 tangentU = UfbxVec3.Normalize3(point.DerivativeU);     // C: ufbxi_slow_normalize3
                            UfbxVec3 tangentV = UfbxVec3.Normalize3(point.DerivativeV);

                            // Check if there's any wrapped positions that we could match
                            int neighbor0 = 0, neighbor1 = 0, neighbor2 = 0, neighbor3 = 0, neighbor4 = 0;   // C: size_t neighbors[5]
                            int numNeighbors = 0;

                            if ((spanV == 0 && (spanU > 0 || splitU > 0)) || (spanU == 0 && (spanV > 0 || splitV > 0))) {
                                // Top/left
                                neighbor0 = 0;
                                numNeighbors = 1;
                            }
                            if (spanV + 1 == spansV) {
                                // Bottom
                                switch (numNeighbors) {
                                    case 0: neighbor0 = ixU; numNeighbors = 1; break;
                                    case 1: neighbor1 = ixU; numNeighbors = 2; break;
                                    default: neighbor2 = ixU; numNeighbors = 3; break;
                                }
                                if (spanU > 0 || splitU > 0) {
                                    switch (numNeighbors) {
                                        case 1: neighbor1 = ixV * indicesU; numNeighbors = 2; break;
                                        case 2: neighbor2 = ixV * indicesU; numNeighbors = 3; break;
                                        default: neighbor3 = ixV * indicesU; numNeighbors = 4; break;
                                    }
                                }
                            }
                            if (spanU + 1 == spansU) {
                                // Right
                                switch (numNeighbors) {
                                    case 0: neighbor0 = ixV * indicesU; numNeighbors = 1; break;
                                    case 1: neighbor1 = ixV * indicesU; numNeighbors = 2; break;
                                    case 2: neighbor2 = ixV * indicesU; numNeighbors = 3; break;
                                    case 3: neighbor3 = ixV * indicesU; numNeighbors = 4; break;
                                    default: neighbor4 = ixV * indicesU; numNeighbors = 5; break;
                                }
                                if (spanV > 0 || splitV > 0) {
                                    switch (numNeighbors) {
                                        case 1: neighbor1 = indicesU - 1; numNeighbors = 2; break;
                                        case 2: neighbor2 = indicesU - 1; numNeighbors = 3; break;
                                        case 3: neighbor3 = indicesU - 1; numNeighbors = 4; break;
                                        default: neighbor4 = indicesU - 1; numNeighbors = 5; break;
                                    }
                                }
                            }

                            int ix = ixV * indicesU + ixU;

                            uint posIx = numPositions;
                            for (int i = 0; i < numNeighbors; i++) {
                                int nbIx = i == 0 ? neighbor0 : i == 1 ? neighbor1 : i == 2 ? neighbor2 : i == 3 ? neighbor3 : neighbor4;
                                uint nbPosIx = positionIx[nbIx];
                                UfbxVec3 nbPos = positions[nbPosIx];
                                double dx = nbPos.X - pos.X;
                                double dy = nbPos.Y - pos.Y;
                                double dz = nbPos.Z - pos.Z;
                                double delta = dx * dx + dy * dy + dz * dz;
                                // C: `0.0000001f` -- float literal promoted to double
                                // (ufbx.c:28085).
                                if (delta < 0.0000001f) {   // TODO: Configurable / something more rigorous
                                    posIx = nbPosIx;
                                    break;
                                }
                            }

                            positionIx[ix] = posIx;
                            if (posIx == numPositions) {
                                positions[posIx] = pos;
                                numPositions = posIx + 1;
                            }
                            uvs[ix].X = originalU;
                            uvs[ix].Y = originalV;
                            tangents[ix] = tangentU;
                            bitangents[ix] = tangentV;
                        }
                    }
                }
            }

            UfbxFace[] faces = new UfbxFace[numFaces];
            uint[] vertexIx = new uint[numFaces * 4];
            uint[] attribIx = new uint[numFaces * 4];

            int faceIx = 0;
            int dstIndex = 0;

            int numTriangles = 0;

            for (int faceV = 0; faceV < facesV; faceV++) {
                for (int faceU = 0; faceU < facesU; faceU++) {

                    attribIx[dstIndex + 0] = (uint)((faceV + 0) * indicesU + (faceU + 0));
                    attribIx[dstIndex + 1] = (uint)((faceV + 0) * indicesU + (faceU + 1));
                    attribIx[dstIndex + 2] = (uint)((faceV + 1) * indicesU + (faceU + 1));
                    attribIx[dstIndex + 3] = (uint)((faceV + 1) * indicesU + (faceU + 0));

                    vertexIx[dstIndex + 0] = positionIx[attribIx[dstIndex + 0]];
                    vertexIx[dstIndex + 1] = positionIx[attribIx[dstIndex + 1]];
                    vertexIx[dstIndex + 2] = positionIx[attribIx[dstIndex + 2]];
                    vertexIx[dstIndex + 3] = positionIx[attribIx[dstIndex + 3]];

                    bool isTriangle = false;
                    for (int prevIx = 0; prevIx < 4; prevIx++) {
                        int nextIx = (prevIx + 1) % 4;
                        if (vertexIx[dstIndex + prevIx] == vertexIx[dstIndex + nextIx]) {
                            for (int i = nextIx; i < 3; i++) {
                                attribIx[dstIndex + i] = attribIx[dstIndex + i + 1];
                                vertexIx[dstIndex + i] = vertexIx[dstIndex + i + 1];
                            }
                            isTriangle = true;
                            break;
                        }
                    }

                    faces[faceIx].IndexBegin = (uint)dstIndex;
                    faces[faceIx].NumIndices = isTriangle ? 3u : 4u;
                    dstIndex += isTriangle ? 3 : 4;
                    numTriangles += isTriangle ? 1 : 2;
                    faceIx++;
                }
            }

            UfbxMesh mesh = new UfbxMesh();
            mesh.Name = string.Empty;       // C: ufbxi_empty_char
            mesh.Type = UfbxElementType.Mesh;
            mesh.TypedId = uint.MaxValue;
            mesh.ElementId = uint.MaxValue;

            // C: counts vs allocations (ufbx.c:28136-28203): `positions`/`normals` are
            // allocated for the whole grid but `count = num_positions`; `vertex_ix`/
            // `attrib_ix` are allocated for 4 corners per face but `count = dst_index`.
            // The port mirrors the *valid data region* as array length. NOTE: C's
            // `vertex_uv/vertex_tangent/vertex_bitangent.values.count = dst_index`
            // overstates the grid-sized buffers (all consumers index through
            // `.indices`, so the overstatement is unobservable); the port keeps the
            // true grid length there.
            if (numPositions != positions.Length) {
                var pos2 = new UfbxVec3[numPositions];
                Array.Copy(positions, pos2, numPositions);
                positions = pos2;
                var nrm2 = new UfbxVec3[numPositions];
                Array.Copy(normals, nrm2, numPositions);
                normals = nrm2;
            }
            if (dstIndex != vertexIx.Length) {
                var vix2 = new uint[dstIndex];
                Array.Copy(vertexIx, vix2, dstIndex);
                vertexIx = vix2;
                var aix2 = new uint[dstIndex];
                Array.Copy(attribIx, aix2, dstIndex);
                attribIx = aix2;
            }

            mesh.Vertices = positions;
            mesh.NumVertices = (int)numPositions;
            mesh.VertexIndices = vertexIx;

            mesh.Faces = faces;

            mesh.VertexPosition.Exists = true;
            mesh.VertexPosition.Values = positions;
            mesh.VertexPosition.Indices = vertexIx;
            mesh.VertexPosition.UniquePerVertex = true;

            mesh.VertexUv.Exists = true;
            mesh.VertexUv.Values = uvs;
            mesh.VertexUv.Indices = attribIx;

            mesh.VertexNormal.Exists = true;
            mesh.VertexNormal.Values = normals;
            mesh.VertexNormal.Indices = vertexIx;

            mesh.VertexTangent.Exists = true;
            mesh.VertexTangent.Values = tangents;
            mesh.VertexTangent.Indices = attribIx;

            mesh.VertexBitangent.Exists = true;
            mesh.VertexBitangent.Values = bitangents;
            mesh.VertexBitangent.Indices = attribIx;

            mesh.NumFaces = numFaces;
            mesh.NumTriangles = numTriangles;
            mesh.NumIndices = dstIndex;
            mesh.MaxFaceTriangles = 2;

            if (surface.Material != null) {
                mesh.FaceMaterial = new uint[numFaces];

                mesh.Materials = new UfbxMaterial[1];
                mesh.Materials[0] = surface.Material;
            }

            if (!opts.SkipMeshParts) {
                mesh.MaterialParts = new UfbxMeshPart[1];
            }

            FinalizeMeshMaterial(mesh);
            UfbxiSceneBuild.FinalizeMesh(mesh);

            mesh.GeneratedNormals = true;
            UfbxTopology.ComputeNormals(mesh, mesh.VertexPosition,
                mesh.VertexNormal.Indices, mesh.VertexNormal.Indices.Length,
                mesh.VertexNormal.Values, mesh.VertexNormal.Values.Length);

            if (surface.FlipNormals) {
                UfbxVec3[] normalValues = mesh.VertexNormal.Values;
                for (int i = 0; i < normalValues.Length; i++) {
                    normalValues[i].X *= -1.0f;
                    normalValues[i].Y *= -1.0f;
                    normalValues[i].Z *= -1.0f;
                }
            }

            // C: imp/refcount anchored to the scene (ufbx.c:28235-28244) -- no counterpart.
            mesh.SubdivisionEvaluated = true;

            return mesh;
        }
    }

    // ======================================================================
    // Public API (ufbx.c:32105-32288, 32290-32340, 32627-32655, 32974-32982)
    // ======================================================================

    // NURBS evaluation, tessellation, mesh subdivision and index generation,
    // mirroring the C public entry points. Failure tails
    // (`ufbxi_fix_error_type(&sc.error, "Failed to ...", error)`, ufbx.c:30069/30221/32313)
    // run in the catch blocks; `ufbxi_check_opts_ptr` (ufbx.c:30321-30326) tests C's
    // `_begin_zero`/`_end_zero` sentinels which the port's options types cannot have.
    public static class UfbxGeometryApi
    {
        // C: the shared failure tail of the tessellate/subdivide entry points.
        static void ReportGeometryError(UfbxParseError e, string defaultDesc, UfbxError pError)
        {
            UfbxError err = new UfbxError();
            if (e.HasDescription && string.IsNullOrEmpty(err.Description)) {
                err.Description = e.Message;
            }
            UfbxiPrint.FixErrorType(err, defaultDesc, pError);
        }

        // ------------------------------------------------------------------
        // C: ufbx_evaluate_nurbs_basis (ufbx.c:32105). Returns `knot - degree`, or -1
        // (C: SIZE_MAX) for invalid input / when the caller buffer is too small. The
        // unreachable no-span path returns C's wrapped `knot - degree` truncated to long.
        // `weights`/`derivatives` may be null (only the base is returned then).
        // ------------------------------------------------------------------
        public static long EvaluateNurbsBasis(UfbxNurbsBasis basis, double u, double[] weights, int numWeights, double[] derivatives, int numDerivatives)
        {
            ulong res = UfbxiSubdivide.EvaluateNurbsBasis(basis, u, weights, numWeights, derivatives, numDerivatives);
            return res == ulong.MaxValue ? -1 : unchecked((long)res);
        }

        // C: ufbx_evaluate_nurbs_curve (ufbx.c:32176).
        public static UfbxCurvePoint EvaluateNurbsCurve(UfbxNurbsCurve curve, double u)
        {
            return UfbxiSubdivide.EvaluateNurbsCurve(curve, u);
        }

        // C: ufbx_evaluate_nurbs_surface (ufbx.c:32222).
        public static UfbxSurfacePoint EvaluateNurbsSurface(UfbxNurbsSurface surface, double u, double v)
        {
            return UfbxiSubdivide.EvaluateNurbsSurface(surface, u, v);
        }

        // ------------------------------------------------------------------
        // C: ufbx_tessellate_nurbs_curve (ufbx.c:32290-32326). Returns null and fills
        // `error` on failure (C: "Failed to tessellate").
        // ------------------------------------------------------------------
        public static UfbxLineCurve TessellateNurbsCurve(UfbxNurbsCurve curve, UfbxTessellateCurveOpts opts, UfbxError error)
        {
            if (curve == null) return null;

            // C: tc.opts = opts ? *opts : {0}
            var optsCopy = new UfbxTessellateCurveOpts();
            if (opts != null) {
                optsCopy.TempAllocator = opts.TempAllocator;
                optsCopy.ResultAllocator = opts.ResultAllocator;
                optsCopy.SpanSubdivision = opts.SpanSubdivision;
            }

            try {
                return UfbxiSubdivide.TessellateNurbsCurveImp(curve, optsCopy);
            } catch (UfbxParseError e) {
                ReportGeometryError(e, "Failed to tessellate", error);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // C: ufbx_tessellate_nurbs_surface (ufbx.c:32328-32365). Returns null and fills
        // `error` on failure (C: "Failed to tessellate").
        // ------------------------------------------------------------------
        public static UfbxMesh TessellateNurbsSurface(UfbxNurbsSurface surface, UfbxTessellateSurfaceOpts opts, UfbxError error)
        {
            if (surface == null) return null;

            // C: tc.opts = opts ? *opts : {0}
            var optsCopy = new UfbxTessellateSurfaceOpts();
            if (opts != null) {
                optsCopy.TempAllocator = opts.TempAllocator;
                optsCopy.ResultAllocator = opts.ResultAllocator;
                optsCopy.SpanSubdivisionU = opts.SpanSubdivisionU;
                optsCopy.SpanSubdivisionV = opts.SpanSubdivisionV;
                optsCopy.SkipMeshParts = opts.SkipMeshParts;
            }

            try {
                return UfbxiSubdivide.TessellateNurbsSurfaceImp(surface, optsCopy);
            } catch (UfbxParseError e) {
                ReportGeometryError(e, "Failed to tessellate", error);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // C: ufbx_subdivide_mesh (ufbx.c:32627-32633). `level == 0` returns the input
        // mesh unchanged (C returns the same pointer; GC makes retain/free no-ops).
        // Returns null and fills `error` on failure (C: "Failed to subdivide").
        // ------------------------------------------------------------------
        public static UfbxMesh SubdivideMesh(UfbxMesh mesh, int level, UfbxSubdivideOpts opts, UfbxError error)
        {
            if (mesh == null) return null;
            if (level == 0) return mesh;

            try {
                return UfbxiSubdivide.SubdivideMeshEntry(mesh, level, opts);
            } catch (UfbxParseError e) {
                ReportGeometryError(e, "Failed to subdivide", error);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // C: ufbx_generate_indices (ufbx.c:32974-32982). Deduplicates the packed vertex
        // records of `streams`, writes `indices[i]` per input index and rewrites each
        // stream's buffer in place with the unique vertices. Returns the number of
        // unique vertices (0 on failure, with `error` filled: C's
        // "Failed to generate indices" default plus "Truncated vertex stream" /
        // "Zero vertex size" report sites). C's `allocator` argument has no managed
        // counterpart and is not represented.
        // ------------------------------------------------------------------
        public static int GenerateIndices(UfbxVertexStream[] streams, int numStreams, uint[] indices, int numIndices, UfbxError error)
        {
            // C: memset(error, 0, sizeof(ufbx_error)) -- reset the user's error object.
            if (error != null) {
                UfbxiPrint.ClearError(error);
            }
            return UfbxiSubdivide.GenerateIndicesImp(streams, numStreams, indices, numIndices, error);
        }

        // ------------------------------------------------------------------
        // C: ufbx_free_mesh (ufbx.c:32635-32647) / ufbx_retain_mesh (ufbx.c:32649-32655):
        // release/retain the tessellated/subdivided mesh's result arena. The port's
        // meshes are ordinary managed memory (see UfbxApi.FreeScene), so both are no-ops
        // kept for API symmetry; the null guard is the only observable.
        // ------------------------------------------------------------------
        public static void FreeMesh(UfbxMesh mesh)
        {
        }

        public static void RetainMesh(UfbxMesh mesh)
        {
        }
    }
}
