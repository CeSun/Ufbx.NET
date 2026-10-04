// Geometry-reader state appended to `ufbxi_context` by the S2-geometry module
// (Parse/Geometry.cs). Append-only partial, like the other reader modules; see the
// NOTE in Parse/UfbxiContext.cs.
using System.Collections.Generic;

namespace Ufbx.NET
{
    internal sealed partial class UfbxiContext
    {
        // C: size_t max_zero_indices / max_consecutive_indices (ufbx.c:6515-6516).
        // "HACK(consecutive-faces)" bookkeeping: the largest count a zero/consecutive index
        // sentinel must cover; `ufbxi_finalize_scene` allocates the shared buffers from these
        // (ufbx.c:22029-22034). Sentinel representation: UfbxiGeometry.SentinelIndexZero /
        // SentinelIndexConsecutive (reference identity, like C's pointer identity).
        internal int MaxZeroIndices;
        internal int MaxConsecutiveIndices;

        // C: ufbxi_buf tmp_full_weights (ufbx.c:6530) — `ufbx_real_list` items pushed by
        // ufbxi_read_blend_channel (ufbx.c:14068) and ufbxi_read_synthetic_blend_shapes
        // (ufbx.c:13095), index-aligned with `scene.blend_channels`
        // (ufbx_assert at ufbx.c:21987, consumed at 21988). A null entry is C's
        // `{ NULL, 0 }` (no FullWeights array); an empty array is a present-but-empty list.
        internal readonly List<double[]> TmpFullWeights = new List<double[]>();

        // C: ufbxi_buf tmp_mesh_textures (ufbx.c:6529) — `ufbxi_tmp_mesh_texture` items
        // pushed by ufbxi_read_mesh (ufbx.c:13691) and moved into the mesh's
        // `ufbxi_mesh_extra` element payload (ufbx.c:13785).
        internal readonly List<UfbxiTmpMeshTexture> TmpMeshTextures = new List<UfbxiTmpMeshTexture>();

        // C: ufbxi_push_zero(&uc->tmp_mesh_textures, ufbxi_tmp_mesh_texture, 1)
        internal UfbxiTmpMeshTexture PushTmpMeshTexture()
        {
            UfbxiTmpMeshTexture tex = new UfbxiTmpMeshTexture();
            TmpMeshTextures.Add(tex);
            return tex;
        }

        // C: ufbxi_push_pop(&uc->tmp, &uc->tmp_mesh_textures, ufbxi_tmp_mesh_texture, n) —
        // the last `count` items, in order; the buffer is truncated.
        internal UfbxiTmpMeshTexture[] PopTmpMeshTextures(int count)
        {
            int start = TmpMeshTextures.Count - count;
            // Port-only guard: C's `ufbxi_pop_size()` asserts `num_items >= n` (ufbx.c:4184,
            // a no-op/abort assert, not an error site).
            UfbxiFail.CheckNoDesc(start >= 0, "tmp_mesh_textures.num_items >= n");
            UfbxiTmpMeshTexture[] items = new UfbxiTmpMeshTexture[count];
            for (int i = 0; i < count; i++) items[i] = TmpMeshTextures[start + i];
            TmpMeshTextures.RemoveRange(start, count);
            return items;
        }
    }

    // C: typedef struct ufbxi_tmp_mesh_texture (ufbx.c:6320-6325). Reference type: the item
    // is push_zero'd and then filled in place, and `ufbxi_push_pop` moves the reference.
    internal sealed class UfbxiTmpMeshTexture
    {
        public uint[] FaceTexture;  // C: uint32_t *face_texture
        public int NumFaces;        // C: size_t num_faces
        public string PropName;     // C: ufbx_string prop_name
        public bool AllSame;        // C: bool all_same
    }

    // C: typedef struct ufbxi_mesh_extra (ufbx.c:6327-6330) — the per-mesh element payload
    // stored via ufbxi_push_element_extra() (ufbx.c:13782).
    internal sealed class UfbxiMeshExtra
    {
        public UfbxiTmpMeshTexture[] TextureArr;  // C: ufbxi_tmp_mesh_texture *texture_arr
        public int TextureCount;                  // C: size_t texture_count
    }
}
