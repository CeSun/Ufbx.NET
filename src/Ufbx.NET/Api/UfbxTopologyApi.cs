// S4c public façade: mesh topology, normal mapping and single-index vertex accessors.
// Pure forwarding layer over `UfbxTopology` (Parse/Topology.cs) -- the C bodies live there,
// this file only mirrors the `ufbx_` prefix of the C ABI (ufbx.h:5651-5695, 5757-5769).
//
// Not exposed here:
//  - ufbx_generate_indices (ufbx.h:5741) -- lives on UfbxGeometryApi (Parse/Subdivide.cs).
//
// `ufbx_panic*` maps to `ref UfbxPanic` (the caller owns the storage, C has no allocation).
// The plain forms pass no panic: C forwards `panic == NULL` into the same body, which the
// internals reproduce with a scratch `UfbxPanic` (see the header of Parse/Topology.cs).

namespace Ufbx.NET
{
    public static class UfbxTopologyApi
    {
        // ------------------------------------------------------------------
        // Triangulation and topology (ufbx.c:32400-32507, 33173-33182)
        // ------------------------------------------------------------------

        // C: ufbx_catch_triangulate_face (ufbx.h:5656). Returns the number of triangles.
        public static uint CatchTriangulateFace(ref UfbxPanic panic, uint[] indices, int numIndices, UfbxMesh mesh, UfbxFace face)
            => UfbxTopology.CatchTriangulateFace(ref panic, indices, numIndices, mesh, face);

        // C: ufbx_triangulate_face (ufbx.h:5657).
        public static uint TriangulateFace(uint[] indices, int numIndices, UfbxMesh mesh, UfbxFace face)
            => UfbxTopology.TriangulateFace(indices, numIndices, mesh, face);

        // C: ufbx_catch_compute_topology (ufbx.h:5660).
        public static void CatchComputeTopology(ref UfbxPanic panic, UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo)
            => UfbxTopology.CatchComputeTopology(ref panic, mesh, topo, numTopo);

        // C: ufbx_compute_topology (ufbx.h:5661).
        public static void ComputeTopology(UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo)
            => UfbxTopology.ComputeTopology(mesh, topo, numTopo);

        // C: ufbx_catch_topo_next_vertex_edge (ufbx.h:5667).
        public static uint CatchTopoNextVertexEdge(ref UfbxPanic panic, UfbxTopoEdge[] topo, int numTopo, uint index)
            => UfbxTopology.CatchTopoNextVertexEdge(ref panic, topo, numTopo, index);

        // C: ufbx_topo_next_vertex_edge (ufbx.h:5668).
        public static uint TopoNextVertexEdge(UfbxTopoEdge[] topo, int numTopo, uint index)
            => UfbxTopology.TopoNextVertexEdge(topo, numTopo, index);

        // C: ufbx_catch_topo_prev_vertex_edge (ufbx.h:5671).
        public static uint CatchTopoPrevVertexEdge(ref UfbxPanic panic, UfbxTopoEdge[] topo, int numTopo, uint index)
            => UfbxTopology.CatchTopoPrevVertexEdge(ref panic, topo, numTopo, index);

        // C: ufbx_topo_prev_vertex_edge (ufbx.h:5672).
        public static uint TopoPrevVertexEdge(UfbxTopoEdge[] topo, int numTopo, uint index)
            => UfbxTopology.TopoPrevVertexEdge(topo, numTopo, index);

        // ------------------------------------------------------------------
        // Normals (ufbx.c:32509-32625, 33185)
        // ------------------------------------------------------------------

        // C: ufbx_catch_get_weighted_face_normal (ufbx.h:5676).
        public static UfbxVec3 CatchGetWeightedFaceNormal(ref UfbxPanic panic, UfbxVertexVec3 positions, UfbxFace face)
            => UfbxTopology.CatchGetWeightedFaceNormal(ref panic, positions, face, -1);

        // C: ufbx_get_weighted_face_normal (ufbx.h:5677).
        public static UfbxVec3 GetWeightedFaceNormal(UfbxVertexVec3 positions, UfbxFace face)
            => UfbxTopology.GetWeightedFaceNormal(positions, face);

        // C: ufbx_catch_generate_normal_mapping (ufbx.h:5681-5683). Returns the number of normals.
        public static int CatchGenerateNormalMapping(ref UfbxPanic panic, UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo,
            uint[] normalIndices, int numNormalIndices, bool assumeSmooth)
            => UfbxTopology.CatchGenerateNormalMapping(ref panic, mesh, topo, numTopo, normalIndices, numNormalIndices, assumeSmooth);

        // C: ufbx_generate_normal_mapping (ufbx.h:5684-5686).
        public static int GenerateNormalMapping(UfbxMesh mesh, UfbxTopoEdge[] topo, int numTopo,
            uint[] normalIndices, int numNormalIndices, bool assumeSmooth)
            => UfbxTopology.GenerateNormalMapping(mesh, topo, numTopo, normalIndices, numNormalIndices, assumeSmooth);

        // C: ufbx_catch_compute_normals (ufbx.h:5690-5692).
        public static void CatchComputeNormals(ref UfbxPanic panic, UfbxMesh mesh, UfbxVertexVec3 positions,
            uint[] normalIndices, int numNormalIndices, UfbxVec3[] normals, int numNormals)
            => UfbxTopology.CatchComputeNormals(ref panic, mesh, positions, normalIndices, numNormalIndices, normals, numNormals);

        // C: ufbx_compute_normals (ufbx.h:5693-5695).
        public static void ComputeNormals(UfbxMesh mesh, UfbxVertexVec3 positions,
            uint[] normalIndices, int numNormalIndices, UfbxVec3[] normals, int numNormals)
            => UfbxTopology.ComputeNormals(mesh, positions, normalIndices, numNormalIndices, normals, numNormals);

        // ------------------------------------------------------------------
        // Single-index vertex accessors (ufbx.c:33001-33040, ufbx.h:5757-5769)
        // ------------------------------------------------------------------

        // C: ufbx_catch_get_vertex_real (ufbx.h:5757).
        public static double CatchGetVertexReal(ref UfbxPanic panic, UfbxVertexReal v, int index)
            => UfbxTopology.CatchGetVertexReal(ref panic, v, index);

        // C: ufbx_get_vertex_real (ufbx.h:5763).
        public static double GetVertexReal(UfbxVertexReal v, int index)
            => UfbxTopology.GetVertexReal(v, index);

        // C: ufbx_catch_get_vertex_vec2 (ufbx.h:5758).
        public static UfbxVec2 CatchGetVertexVec2(ref UfbxPanic panic, UfbxVertexVec2 v, int index)
            => UfbxTopology.CatchGetVertexVec2(ref panic, v, index);

        // C: ufbx_get_vertex_vec2 (ufbx.h:5764).
        public static UfbxVec2 GetVertexVec2(UfbxVertexVec2 v, int index)
            => UfbxTopology.GetVertexVec2(v, index);

        // C: ufbx_catch_get_vertex_vec3 (ufbx.h:5759).
        public static UfbxVec3 CatchGetVertexVec3(ref UfbxPanic panic, UfbxVertexVec3 v, int index)
            => UfbxTopology.CatchGetVertexVec3(ref panic, v, index);

        // C: ufbx_get_vertex_vec3 (ufbx.h:5765).
        public static UfbxVec3 GetVertexVec3(UfbxVertexVec3 v, int index)
            => UfbxTopology.GetVertexVec3(v, index);

        // C: ufbx_catch_get_vertex_vec4 (ufbx.h:5760).
        public static UfbxVec4 CatchGetVertexVec4(ref UfbxPanic panic, UfbxVertexVec4 v, int index)
            => UfbxTopology.CatchGetVertexVec4(ref panic, v, index);

        // C: ufbx_get_vertex_vec4 (ufbx.h:5766).
        public static UfbxVec4 GetVertexVec4(UfbxVertexVec4 v, int index)
            => UfbxTopology.GetVertexVec4(v, index);

        // C: ufbx_catch_get_vertex_w_vec3 (ufbx.c:33033, ufbx.h:5768).
        public static double CatchGetVertexWVec3(ref UfbxPanic panic, UfbxVertexVec3 v, int index)
            => UfbxTopology.CatchGetVertexWVec3(ref panic, v, index);

        // C: ufbx_get_vertex_w_vec3 (ufbx.h:5769).
        public static double GetVertexWVec3(UfbxVertexVec3 v, int index)
            => UfbxTopology.GetVertexWVec3(v, index);

        // ------------------------------------------------------------------
        // Face lookup (ufbx.c:32389-32398, ufbx.h:5651)
        // ------------------------------------------------------------------

        // C: ufbx_find_face_index (ufbx.c:32389-32398). `long` is C's `size_t`: an index above
        // `UINT32_MAX` reports `UFBX_NO_INDEX` before the mesh is touched.
        public static uint FindFaceIndex(UfbxMesh mesh, long index)
            => UfbxTopology.FindFaceIndex(mesh, index);
    }
}
