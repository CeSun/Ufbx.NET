// C: the public scene/element lookup entry points of the evaluation section (ufbx.c:30738-30833):
//   ufbx_find_element_len            (30738-30749)
//   ufbx_get_prop_element            (30751-30756)
//   ufbx_find_prop_element_len       (30758-30766)
//   ufbx_find_node_len               (30768-30771)
//   ufbx_find_anim_stack_len         (30773-30776)
//   ufbx_find_material_len           (30778-30781)
//   ufbx_find_anim_prop_len          (30783-30798)
//   ufbx_find_anim_props             (30800-30820)
//   ufbx_get_compatible_matrix_for_normals (30822-30833)
// The NUL-terminated forms at ufbx.c:33157-33162 forward to these with `strlen(name)`; the port's
// public wrappers do the same (Api convention, PORTING_NOTES.md "文件布局").
//
// Notes on the C representations:
//  * `ufbxi_macro_lower_bound_eq`/`_upper_bound_eq` are the literal transcriptions in
//    Parse/SceneBuild.cs:1158-1198 (ufbx.c:1188-1229), reused here rather than transcribed again.
//  * C keys the `anim_props` searches by `ufbx_element*` pointer order; the port's ordering
//    surrogate is `ElementId`, the same one the sort and the internal
//    `ufbxi_find_anim_prop_start` already use (Parse/SceneBuild.cs:1527-1532, 1562-1575).
//  * C's `ufbx_anim_prop_list` is a `{ data, count }` view into the layer; the port returns a
//    copied slice, as `UfbxiSceneFinalize.FindShaderPropBindings` already does for
//    `ufbx_shader_prop_binding_list` (Parse/SceneFinalize.cs:2618-2675). `{ NULL, 0 }` is `null`.
//  * `ufbxi_safe_string(data, length)` (ufbx.c:5030-5034) is a (data, length) view with the empty
//    string for `length == 0` or NULL data; `SafeString` materializes that prefix. A caller that
//    passed C's `SIZE_MAX` got a string of length `SIZE_MAX`, which never compares equal to a real
//    interned name, so those entry points have no documented unbounded form -- pass the length.

using System;

namespace Ufbx
{
    internal static class UfbxiSceneFind
    {
        // C: ufbxi_safe_string (ufbx.c:5030-5034), as a materialized prefix string.
        internal static string SafeString(string data, int length)
        {
            if (data == null || length <= 0) return string.Empty;
            if (length >= data.Length) return data;
            return data.Substring(0, length);
        }

        // C: ufbx_find_element_len (ufbx.c:30738-30749). Binary search over the name-sorted
        // `elements_by_name` list with the (key, name, type) comparator used to sort it.
        internal static UfbxElement FindElementLen(UfbxScene scene, UfbxElementType type, string name, int nameLen)
        {
            if (scene == null) return null;

            string nameStr = SafeString(name, nameLen);
            uint key = UfbxiProperties.GetNameKey(nameStr, nameStr.Length);

            UfbxNameElement[] data = scene.ElementsByName ?? Array.Empty<UfbxNameElement>();
            int size = data.Length;

            int index = -1; // C: SIZE_MAX
            UfbxiSceneBuild.LowerBoundEq(data, 0, size, 16,
                a => UfbxiSceneBuild.CmpNameElementLessRef(a, nameStr, type, key),
                a => UfbxiStr.Equal(a.Name, nameStr) && a.Type == type,
                ref index);

            return index >= 0 ? data[index].Element : null;
        }

        // C: ufbx_find_node_len (ufbx.c:30768-30771).
        internal static UfbxNode FindNodeLen(UfbxScene scene, string name, int nameLen)
        {
            return (UfbxNode)FindElementLen(scene, UfbxElementType.Node, name, nameLen);
        }

        // C: ufbx_find_anim_stack_len (ufbx.c:30773-30776).
        internal static UfbxAnimStack FindAnimStackLen(UfbxScene scene, string name, int nameLen)
        {
            return (UfbxAnimStack)FindElementLen(scene, UfbxElementType.AnimStack, name, nameLen);
        }

        // C: ufbx_find_material_len (ufbx.c:30778-30781).
        internal static UfbxMaterial FindMaterialLen(UfbxScene scene, string name, int nameLen)
        {
            return (UfbxMaterial)FindElementLen(scene, UfbxElementType.Material, name, nameLen);
        }

        // C: ufbx_find_prop_element_len (ufbx.c:30758-30766). The property lookup is over the
        // element's own `props` chain (`&element->props` in C, which is never NULL); the resolved
        // prop's *name* then drives the connection search, so `name_len` does not reach
        // `ufbxi_fetch_dst_element` (which is what `UfbxiSceneFinalize.GetPropElement` models).
        internal static UfbxElement FindPropElementLen(UfbxElement element, string name, int nameLen, UfbxElementType type)
        {
            string nameStr = SafeString(name, nameLen);
            if (UfbxiEvaluate.FindPropLen(element.Props, nameStr, nameStr.Length, out UfbxProp prop)) {
                return UfbxiSceneFinalize.GetPropElement(element, prop, type);
            }
            return null;
        }

        // C: ufbx_find_anim_prop_len (ufbx.c:30783-30798). NOTE: C's `less` predicate compares the
        // element first and only then the property name, so an element that is not in the layer
        // resolves the search to whatever the binary search lands on -- transcribed literally.
        internal static UfbxAnimProp FindAnimPropLen(UfbxAnimLayer layer, UfbxElement element, string prop, int propLen)
        {
            if (layer == null || element == null) return null;

            string propStr = SafeString(prop, propLen);

            UfbxAnimProp[] data = layer.AnimProps ?? Array.Empty<UfbxAnimProp>();
            int size = data.Length;

            int index = -1; // C: SIZE_MAX
            UfbxiSceneBuild.LowerBoundEq(data, 0, size, 16,
                a => a.Element.ElementId != element.ElementId
                    ? a.Element.ElementId < element.ElementId
                    : UfbxiStr.Less(a.PropName, propStr),
                a => a.Element.ElementId == element.ElementId && UfbxiStr.Equal(a.PropName, propStr),
                ref index);

            return index >= 0 ? data[index] : null;
        }

        // C: ufbx_find_anim_props (ufbx.c:30800-30820). The lower bound leaves `begin` at
        // `count` when the element is absent, so the upper bound runs over the empty range and the
        // result is the empty list (`null` here, like C's `{ NULL, 0 }`).
        internal static UfbxAnimProp[] FindAnimProps(UfbxAnimLayer layer, UfbxElement element)
        {
            if (layer == null || element == null) return null;

            UfbxAnimProp[] data = layer.AnimProps ?? Array.Empty<UfbxAnimProp>();
            int size = data.Length;

            int begin = size, end = size;

            UfbxiSceneBuild.LowerBoundEq(data, 0, size, 16,
                a => a.Element.ElementId < element.ElementId,
                a => a.Element.ElementId == element.ElementId,
                ref begin);

            UfbxiSceneBuild.UpperBoundEq(data, begin, size, 16,
                a => a.Element.ElementId == element.ElementId,
                ref end);

            if (begin == end) return null;

            UfbxAnimProp[] result = new UfbxAnimProp[end - begin];
            Array.Copy(data, begin, result, 0, result.Length);
            return result;
        }

        // C: ufbx_get_compatible_matrix_for_normals (ufbx.c:30822-30833). The scale of the
        // geometry transform is dropped (`ufbx_identity_transform` with only its rotation
        // overwritten), so the matrix is the pure geometry rotation composed with `node_to_world`
        // and turned into a normal matrix.
        internal static UfbxMatrix GetCompatibleMatrixForNormals(UfbxNode node)
        {
            if (node == null) return UfbxMatrix.Identity;

            UfbxTransform geomRot = UfbxTransform.Identity;
            geomRot.Rotation = node.GeometryTransform.Rotation;
            UfbxMatrix geomRotMat = UfbxMatrix.FromTransform(geomRot);

            UfbxMatrix normMat = UfbxMatrix.Mul(node.NodeToWorld, geomRotMat);
            return UfbxMatrix.ForNormals(normMat);
        }
    }
}
