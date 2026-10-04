// S4a partial of `ufbxi_context`, for the geometry cache / external file / axis-unit
// conversion / skinning bind matrix modules (ufbx.c:23936-25211, 25063-25177, 31862-32103).
//
// Field ownership: this file is append-only and only owns the fields these modules
// introduce. It must not duplicate names from UfbxiContext.cs or the other
// `UfbxiContext.<Module>.cs` files (verified against Geometry/Anim/Objects/Obj/SceneBuild).
//
// The geometry cache spin-off struct `ufbxi_cache_context` (ufbx.c:23987-24038) is a
// self-contained value type owned by GeometryCache.cs, not a partial of this class.

namespace Ufbx.NET
{
    internal sealed partial class UfbxiContext
    {
        // C: ufbx_mirror_axis mirror_axis (ufbx.c:6489). Set by `ufbxi_transform_to_axes()`
        // (ufbx.c:24962) when the axis matrix determinant is negative and handedness
        // conversion is requested; read by `ufbxi_load_external_cache()` (ufbx.c:24835) and
        // `ufbxi_update_adjust_transforms()` (S3c). Default UFBX_MIRROR_AXIS_NONE.
        internal UfbxMirrorAxis MirrorAxis;

        // C: ufbx_matrix axis_matrix (ufbx.c:6497). Written by `ufbxi_transform_to_axes()`
        // (ufbx.c:24957) and read by `ufbxi_update_adjust_transforms()` (ufbx.c:23682). The
        // all-zero default is C's zero-initialized struct; `UfbxiMatrix.AllZero` reproduces
        // `ufbxi_matrix_all_zero()` on it.
        internal UfbxMatrix AxisMatrix;
    }
}
