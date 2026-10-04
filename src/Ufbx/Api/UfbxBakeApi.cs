// Public façade: the animation-baking entry points.
// Pure forwarding layer over `UfbxiBake` (Parse/Bake.cs) -- the C bodies live there, this file
// only mirrors the `ufbx_` prefix of the C ABI (ufbx.h:5139-5238).
//
// Covered ABI names (ufbx.c:31250-31411):
//   ufbx_bake_anim                              -> BakeAnim
//   ufbx_retain_baked_anim / ufbx_free_baked_anim -> RetainBakedAnim / FreeBakedAnim
//   ufbx_find_baked_node[_by_typed_id]          -> FindBakedNode / FindBakedNodeByTypedId
//   ufbx_find_baked_element[_by_element_id]     -> FindBakedElement / FindBakedElementByElementId
//   ufbx_evaluate_baked_vec3 / _quat            -> EvaluateBakedVec3 / EvaluateBakedQuat
//
// `ufbx_bake_anim()` returns `ufbx_baked_anim*` (NULL on failure) and takes
// `const ufbx_bake_opts*` (NULL means the zeroed struct, which is what `ufbx_default_bake_opts()`
// also gives); the opts are value-copied inside, so the caller's object never sees the five
// default writes of `ufbxi_bake_anim_imp()`.
//
// Deviations from C that cannot be expressed here:
// - `ufbx_baked_node`/`ufbx_baked_element` are value types in this port, and C's `find_*`
//   functions hand back a *pointer into* `bake->nodes.data` / `bake->elements.data`. The port
//   returns a copy: identity (`&found->element_id == &bake->nodes.data[i]`) and writes through
//   the returned pointer have no counterpart. A miss is `null`, like C's NULL return.
// - `ufbx_evaluate_baked_vec3()`/`_quat()` take `ufbx_baked_*_list` by value; the port takes the
//   array. For an empty list C reads `data[count - 1]`, i.e. `data[-1]` -- undefined behaviour
//   that dereferences (or crashes on) NULL data; the port indexes -1 and throws.
// - `ufbx_bake_anim()` starts with `ufbx_assert(scene)` (31252), which is a no-op in the
//   reference build; a NULL scene is undefined in C and a NullReferenceException here.
// - `ufbx_retain_baked_anim()`/`ufbx_free_baked_anim()` walk the `ufbxi_baked_anim_imp` refcount
//   header; PORTING_NOTES.md #4, so they are no-ops like UfbxApi.FreeScene().

namespace Ufbx
{
    public static class UfbxBakeApi
    {
        // C: ufbx_bake_anim (ufbx.c:31250-31297).
        public static UfbxBakedAnim BakeAnim(UfbxScene scene, UfbxAnim anim, UfbxBakeOpts opts, UfbxError error)
            => UfbxiBake.BakeAnimEntry(scene, anim, opts, error);

        // C: ufbx_retain_baked_anim (ufbx.c:31299-31307).
        public static void RetainBakedAnim(UfbxBakedAnim bake)
            => UfbxiBake.RetainBakedAnim(bake);

        // C: ufbx_free_baked_anim (ufbx.c:31309-31317).
        public static void FreeBakedAnim(UfbxBakedAnim bake)
            => UfbxiBake.FreeBakedAnim(bake);

        // C: ufbx_find_baked_node_by_typed_id (ufbx.c:31320-31326).
        public static UfbxBakedNode? FindBakedNodeByTypedId(UfbxBakedAnim bake, uint typedId)
            => UfbxiBake.FindBakedNodeByTypedId(bake, typedId);

        // C: ufbx_find_baked_node (ufbx.c:31328-31332).
        public static UfbxBakedNode? FindBakedNode(UfbxBakedAnim bake, UfbxNode node)
            => UfbxiBake.FindBakedNode(bake, node);

        // C: ufbx_find_baked_element_by_element_id (ufbx.c:31334-31340).
        public static UfbxBakedElement? FindBakedElementByElementId(UfbxBakedAnim bake, uint elementId)
            => UfbxiBake.FindBakedElementByElementId(bake, elementId);

        // C: ufbx_find_baked_element (ufbx.c:31342-31346).
        public static UfbxBakedElement? FindBakedElement(UfbxBakedAnim bake, UfbxElement element)
            => UfbxiBake.FindBakedElement(bake, element);

        // C: ufbx_evaluate_baked_vec3 (ufbx.c:31348-31378).
        public static UfbxVec3 EvaluateBakedVec3(UfbxBakedVec3[] keyframes, double time)
            => UfbxiBake.EvaluateBakedVec3(keyframes, time);

        // C: ufbx_evaluate_baked_quat (ufbx.c:31380-31411).
        public static UfbxQuat EvaluateBakedQuat(UfbxBakedQuat[] keyframes, double time)
            => UfbxiBake.EvaluateBakedQuat(keyframes, time);
    }
}
