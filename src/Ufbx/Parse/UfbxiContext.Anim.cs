// S2 animation module state appended to `ufbxi_context`, owned by the S2-anim agent
// (2026-10-03 wave). Append-only partial: never edit Parse/UfbxiContext.cs from here.
namespace Ufbx
{
    internal sealed partial class UfbxiContext
    {
        // C: ufbxi_map anim_stack_map (ufbx.c:6499), items `ufbxi_tmp_anim_stack`
        // (ufbx.c:6389-6392), initialized in `ufbxi_load()` at ufbx.c:25560 with
        // `ufbxi_map_cmp_const_char_ptr`. Keyed by the pooled stack *name* pointer until
        // finalization; only ever looked up, never iterated (PORTING_NOTES.md
        // "C 指针序 ≡ 分配序"), so `UfbxiPtrIdTable.IdOf(name)` is the faithful key.
        internal UfbxiMap<UfbxiTmpAnimStack, ulong> AnimStackMap;

        // Lazy stand-in for the `ufbxi_map_init(&uc->anim_stack_map, ...)` call of
        // `ufbxi_load()` (ufbx.c:25560): the port creates the map on first use. The map is
        // only touched by `ufbxi_read_anim_stack()`/`ufbxi_read_take()`, both behind the
        // same load path, so first-use init is indistinguishable.
        internal void AnimInitStackMap()
        {
            if (AnimStackMap == null) {
                AnimStackMap = new UfbxiMap<UfbxiTmpAnimStack, ulong>(
                    UfbxiMapCmps.ConstCharPtr<UfbxiTmpAnimStack>(e => UfbxiPtrIdTable.IdOf(e.Name)),
                    16, Error);
            }
        }
    }

    // C: ufbxi_tmp_anim_stack (ufbx.c:6389-6392). Reference stack: C stores a
    // `ufbx_anim_stack *`, and the entry is filled right after `ufbxi_map_insert()`.
    internal struct UfbxiTmpAnimStack
    {
        public string Name;          // C: const char *name (pooled)
        public UfbxAnimStack Stack;  // C: ufbx_anim_stack *stack
    }
}
