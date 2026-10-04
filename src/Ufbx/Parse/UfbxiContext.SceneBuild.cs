// Scene-build state appended to `ufbxi_context` by the S3a module (Parse/SceneBuild.cs),
// owned by the S3a porting agent (2026-10-03 wave).
//
// Append-only partial: never edit Parse/UfbxiContext.cs from here (see its NOTE). The names
// below are chosen so they cannot collide with the other reader modules.
namespace Ufbx
{
    internal sealed partial class UfbxiContext
    {
        // C: uint32_t *zero_indices / uint32_t *consecutive_indices (ufbx.c:6517-6518).
        // `ufbxi_finalize_scene` allocates them from `max_zero_indices` /
        // `max_consecutive_indices` (ufbx.c:22027-22039) and `ufbxi_patch_index_pointer()`
        // (ufbx.c:19284-19291) swaps them in for the shared sentinel arrays. In the port the
        // sentinels are the two `static readonly uint[]` instances in `UfbxiGeometry` and
        // `UfbxiObjects` (the duplicate is a known port artifact), so `PatchIndexPointer`
        // matches BOTH instances by reference.
        internal uint[] ZeroIndices;
        internal uint[] ConsecutiveIndices;

        // C: uint8_t *tmp_element_flag (ufbx.c:6440, allocated by `ufbxi_finalize_scene` at
        // ufbx.c:21678 with `ufbxi_push_zero(&uc->tmp, uint8_t, num_elements)`). The
        // duplicate-connection filter of `ufbxi_fetch_dst_elements`/`_src_elements`
        // (ufbx.c:19056/19094) reads and resets it per element. The port allocates it on
        // first use (the C allocation site is in the out-of-scope `ufbxi_finalize_scene`).
        internal byte[] TmpElementFlag;

        // C: `uc->tmp_element_flag[element_id]` — C zero-fills the whole array once per load,
        // and the fetch functions clear the bits they set, so lazily allocating a zeroed array
        // of `num_elements` is equivalent.
        internal void EnsureTmpElementFlag()
        {
            int numElements = (int)NumElements;
            if (TmpElementFlag == null || TmpElementFlag.Length < numElements) {
                TmpElementFlag = new byte[numElements];
            }
        }
    }
}
