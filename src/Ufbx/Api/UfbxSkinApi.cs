// Public façade: the `ufbx.h` "Skinning" section (ufbx.h:5598-5622) -- the skin vertex matrix
// accessor and the blend shape/deformer vertex-offset helpers. Pure forwarding layer: the C bodies
// live in Parse/SceneOpts.cs (`UfbxiSceneOpts.CatchGetSkinVertexMatrix` = ufbx.c:31936-32026,
// `GetBlendShapeOffsetIndex` = 32028-32041, `GetBlendShapeVertexOffset` = 32043-32048,
// `GetBlendVertexOffset` = 32050-32068, `AddBlendShapeVertexOffsets` = 32070-32089,
// `AddBlendVertexOffsets` = 32091-32103), so the S4a SK/BSI/BSV/BVO/BAV differentials keep covering
// the same implementations through these names.
//
// Conventions:
//  * `ufbx_panic*` maps to `ref UfbxPanic` (the caller owns the storage; C allocates nothing).
//    `ufbx_get_skin_vertex_matrix` is C's inline wrapper over the catch form with `panic == NULL`
//    (ufbx.h:5601-5603), which the internal reproduces with a scratch panic.
//  * `size_t vertex`: the skin accessor compares it **unsigned** in C (31939, 31941), so it stays a
//    `long` down to the internal, where the tests are `unchecked((ulong))` comparisons. The blend
//    helpers truncate first -- C's very first act is `uint32_t vertex_ix = (uint32_t)vertex`
//    (32034) -- so narrowing here with `unchecked((int)vertex)` keeps exactly the bits C keeps.
//  * `ufbx_vec3 *vertices` + `size_t num_vertices`: the C body only ever touches
//    `vertices[index]` for `index < num_vertices` (32082-32087), which is the port's
//    `vertices[0 + index]`; the internal's extra `offset` parameter exists for the in-place
//    evaluate path that works on a window of a larger array, and the public form passes 0.
//  * `ufbx_real` -> `double`, `size_t num_vertices` -> `int` (PORTING_NOTES.md "size_t -> int").
//  * C's `ufbx_assert(shape)` in `ufbx_add_blend_shape_vertex_offsets` is not a reachable input for
//    the port either: `weight == 0.0` and `vertices == null` return first, exactly as in C, and a
//    NULL shape with non-zero weight throws instead of reading a NULL pointer.

namespace Ufbx
{
    public static class UfbxSkinApi
    {
        // ------------------------------------------------------------------
        // Skinning (ufbx.c:31936-32026)
        // ------------------------------------------------------------------

        // C: ufbx_catch_get_skin_vertex_matrix (ufbx.h:5600). Returns the identity matrix when the
        // vertex is out of bounds or unskinned and `fallback` is absent.
        public static UfbxMatrix CatchGetSkinVertexMatrix(ref UfbxPanic panic, UfbxSkinDeformer skin, long vertex, UfbxMatrix? fallback)
            => UfbxiSceneOpts.CatchGetSkinVertexMatrix(ref panic, skin, vertex, fallback);

        // C: ufbx_get_skin_vertex_matrix (ufbx.h:5601-5603, inline over the catch form).
        public static UfbxMatrix GetSkinVertexMatrix(UfbxSkinDeformer skin, long vertex, UfbxMatrix? fallback)
            => UfbxiSceneOpts.GetSkinVertexMatrix(skin, vertex, fallback);

        // ------------------------------------------------------------------
        // Blend offsets (ufbx.c:32028-32103)
        // ------------------------------------------------------------------

        // C: ufbx_get_blend_shape_offset_index (ufbx.h:5607). `UFBX_NO_INDEX` when the vertex is not
        // part of the shape.
        public static uint GetBlendShapeOffsetIndex(UfbxBlendShape shape, long vertex)
            => UfbxiSceneOpts.GetBlendShapeOffsetIndex(shape, unchecked((int)vertex));

        // C: ufbx_get_blend_shape_vertex_offset (ufbx.h:5611). Zero vector when not included.
        public static UfbxVec3 GetBlendShapeVertexOffset(UfbxBlendShape shape, long vertex)
            => UfbxiSceneOpts.GetBlendShapeVertexOffset(shape, unchecked((int)vertex));

        // C: ufbx_get_blend_vertex_offset (ufbx.h:5615). Depends on the animated blend weights.
        public static UfbxVec3 GetBlendVertexOffset(UfbxBlendDeformer blend, long vertex)
            => UfbxiSceneOpts.GetBlendVertexOffset(blend, unchecked((int)vertex));

        // C: ufbx_add_blend_shape_vertex_offsets (ufbx.h:5618).
        public static void AddBlendShapeVertexOffsets(UfbxBlendShape shape, UfbxVec3[] vertices, int numVertices, double weight)
            => UfbxiSceneOpts.AddBlendShapeVertexOffsets(shape, vertices, 0, numVertices, weight);

        // C: ufbx_add_blend_vertex_offsets (ufbx.h:5622). Depends on the animated blend weights.
        public static void AddBlendVertexOffsets(UfbxBlendDeformer blend, UfbxVec3[] vertices, int numVertices, double weight)
            => UfbxiSceneOpts.AddBlendVertexOffsets(blend, vertices, 0, numVertices, weight);
    }
}
