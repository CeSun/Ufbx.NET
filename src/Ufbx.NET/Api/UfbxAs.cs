// Public element down-casts, ported from ufbx.c v0.23.1:
//   ufbx_as_unknown() .. ufbx_as_metadata_object() (ufbx.c:33042-33083)
//
// C's body is the same one-liner for all 42 element types: test `element->type` and cast. The test
// is on the discriminant rather than the managed runtime class on purpose -- `ufbx_element_type` is
// the observable the rest of the API keys off (`UfbxElement.Type` is set from it at load time), and
// an element whose class and discriminant disagreed would be a loader bug that this surfaces as a
// `InvalidCastException` rather than silently hiding behind `as`.

namespace Ufbx.NET
{
    // C: the `ufbx_as_*()` family (ufbx.h:5773-5814, ufbx.c:33042-33083)
    public static class UfbxAs
    {
        // C: ufbx_as_unknown (ufbx.c:33042)
        public static UfbxUnknown Unknown(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Unknown ? (UfbxUnknown)element : null;

        // C: ufbx_as_node (ufbx.c:33043)
        public static UfbxNode Node(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Node ? (UfbxNode)element : null;

        // C: ufbx_as_mesh (ufbx.c:33044)
        public static UfbxMesh Mesh(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Mesh ? (UfbxMesh)element : null;

        // C: ufbx_as_light (ufbx.c:33045)
        public static UfbxLight Light(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Light ? (UfbxLight)element : null;

        // C: ufbx_as_camera (ufbx.c:33046)
        public static UfbxCamera Camera(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Camera ? (UfbxCamera)element : null;

        // C: ufbx_as_bone (ufbx.c:33047)
        public static UfbxBone Bone(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Bone ? (UfbxBone)element : null;

        // C: ufbx_as_empty (ufbx.c:33048)
        public static UfbxEmpty Empty(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Empty ? (UfbxEmpty)element : null;

        // C: ufbx_as_line_curve (ufbx.c:33049)
        public static UfbxLineCurve LineCurve(UfbxElement element)
            => element != null && element.Type == UfbxElementType.LineCurve ? (UfbxLineCurve)element : null;

        // C: ufbx_as_nurbs_curve (ufbx.c:33050)
        public static UfbxNurbsCurve NurbsCurve(UfbxElement element)
            => element != null && element.Type == UfbxElementType.NurbsCurve ? (UfbxNurbsCurve)element : null;

        // C: ufbx_as_nurbs_surface (ufbx.c:33051)
        public static UfbxNurbsSurface NurbsSurface(UfbxElement element)
            => element != null && element.Type == UfbxElementType.NurbsSurface ? (UfbxNurbsSurface)element : null;

        // C: ufbx_as_nurbs_trim_surface (ufbx.c:33052)
        public static UfbxNurbsTrimSurface NurbsTrimSurface(UfbxElement element)
            => element != null && element.Type == UfbxElementType.NurbsTrimSurface ? (UfbxNurbsTrimSurface)element : null;

        // C: ufbx_as_nurbs_trim_boundary (ufbx.c:33053)
        public static UfbxNurbsTrimBoundary NurbsTrimBoundary(UfbxElement element)
            => element != null && element.Type == UfbxElementType.NurbsTrimBoundary ? (UfbxNurbsTrimBoundary)element : null;

        // C: ufbx_as_procedural_geometry (ufbx.c:33054)
        public static UfbxProceduralGeometry ProceduralGeometry(UfbxElement element)
            => element != null && element.Type == UfbxElementType.ProceduralGeometry ? (UfbxProceduralGeometry)element : null;

        // C: ufbx_as_stereo_camera (ufbx.c:33055)
        public static UfbxStereoCamera StereoCamera(UfbxElement element)
            => element != null && element.Type == UfbxElementType.StereoCamera ? (UfbxStereoCamera)element : null;

        // C: ufbx_as_camera_switcher (ufbx.c:33056)
        public static UfbxCameraSwitcher CameraSwitcher(UfbxElement element)
            => element != null && element.Type == UfbxElementType.CameraSwitcher ? (UfbxCameraSwitcher)element : null;

        // C: ufbx_as_marker (ufbx.c:33057)
        public static UfbxMarker Marker(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Marker ? (UfbxMarker)element : null;

        // C: ufbx_as_lod_group (ufbx.c:33058)
        public static UfbxLodGroup LodGroup(UfbxElement element)
            => element != null && element.Type == UfbxElementType.LodGroup ? (UfbxLodGroup)element : null;

        // C: ufbx_as_skin_deformer (ufbx.c:33059)
        public static UfbxSkinDeformer SkinDeformer(UfbxElement element)
            => element != null && element.Type == UfbxElementType.SkinDeformer ? (UfbxSkinDeformer)element : null;

        // C: ufbx_as_skin_cluster (ufbx.c:33060)
        public static UfbxSkinCluster SkinCluster(UfbxElement element)
            => element != null && element.Type == UfbxElementType.SkinCluster ? (UfbxSkinCluster)element : null;

        // C: ufbx_as_blend_deformer (ufbx.c:33061)
        public static UfbxBlendDeformer BlendDeformer(UfbxElement element)
            => element != null && element.Type == UfbxElementType.BlendDeformer ? (UfbxBlendDeformer)element : null;

        // C: ufbx_as_blend_channel (ufbx.c:33062)
        public static UfbxBlendChannel BlendChannel(UfbxElement element)
            => element != null && element.Type == UfbxElementType.BlendChannel ? (UfbxBlendChannel)element : null;

        // C: ufbx_as_blend_shape (ufbx.c:33063)
        public static UfbxBlendShape BlendShape(UfbxElement element)
            => element != null && element.Type == UfbxElementType.BlendShape ? (UfbxBlendShape)element : null;

        // C: ufbx_as_cache_deformer (ufbx.c:33064)
        public static UfbxCacheDeformer CacheDeformer(UfbxElement element)
            => element != null && element.Type == UfbxElementType.CacheDeformer ? (UfbxCacheDeformer)element : null;

        // C: ufbx_as_cache_file (ufbx.c:33065)
        public static UfbxCacheFile CacheFile(UfbxElement element)
            => element != null && element.Type == UfbxElementType.CacheFile ? (UfbxCacheFile)element : null;

        // C: ufbx_as_material (ufbx.c:33066)
        public static UfbxMaterial Material(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Material ? (UfbxMaterial)element : null;

        // C: ufbx_as_texture (ufbx.c:33067)
        public static UfbxTexture Texture(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Texture ? (UfbxTexture)element : null;

        // C: ufbx_as_video (ufbx.c:33068)
        public static UfbxVideo Video(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Video ? (UfbxVideo)element : null;

        // C: ufbx_as_shader (ufbx.c:33069)
        public static UfbxShader Shader(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Shader ? (UfbxShader)element : null;

        // C: ufbx_as_shader_binding (ufbx.c:33070)
        public static UfbxShaderBinding ShaderBinding(UfbxElement element)
            => element != null && element.Type == UfbxElementType.ShaderBinding ? (UfbxShaderBinding)element : null;

        // C: ufbx_as_anim_stack (ufbx.c:33071)
        public static UfbxAnimStack AnimStack(UfbxElement element)
            => element != null && element.Type == UfbxElementType.AnimStack ? (UfbxAnimStack)element : null;

        // C: ufbx_as_anim_layer (ufbx.c:33072)
        public static UfbxAnimLayer AnimLayer(UfbxElement element)
            => element != null && element.Type == UfbxElementType.AnimLayer ? (UfbxAnimLayer)element : null;

        // C: ufbx_as_anim_value (ufbx.c:33073)
        public static UfbxAnimValue AnimValue(UfbxElement element)
            => element != null && element.Type == UfbxElementType.AnimValue ? (UfbxAnimValue)element : null;

        // C: ufbx_as_anim_curve (ufbx.c:33074)
        public static UfbxAnimCurve AnimCurve(UfbxElement element)
            => element != null && element.Type == UfbxElementType.AnimCurve ? (UfbxAnimCurve)element : null;

        // C: ufbx_as_display_layer (ufbx.c:33075)
        public static UfbxDisplayLayer DisplayLayer(UfbxElement element)
            => element != null && element.Type == UfbxElementType.DisplayLayer ? (UfbxDisplayLayer)element : null;

        // C: ufbx_as_selection_set (ufbx.c:33076)
        public static UfbxSelectionSet SelectionSet(UfbxElement element)
            => element != null && element.Type == UfbxElementType.SelectionSet ? (UfbxSelectionSet)element : null;

        // C: ufbx_as_selection_node (ufbx.c:33077)
        public static UfbxSelectionNode SelectionNode(UfbxElement element)
            => element != null && element.Type == UfbxElementType.SelectionNode ? (UfbxSelectionNode)element : null;

        // C: ufbx_as_character (ufbx.c:33078)
        public static UfbxCharacter Character(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Character ? (UfbxCharacter)element : null;

        // C: ufbx_as_constraint (ufbx.c:33079)
        public static UfbxConstraint Constraint(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Constraint ? (UfbxConstraint)element : null;

        // C: ufbx_as_audio_layer (ufbx.c:33080)
        public static UfbxAudioLayer AudioLayer(UfbxElement element)
            => element != null && element.Type == UfbxElementType.AudioLayer ? (UfbxAudioLayer)element : null;

        // C: ufbx_as_audio_clip (ufbx.c:33081)
        public static UfbxAudioClip AudioClip(UfbxElement element)
            => element != null && element.Type == UfbxElementType.AudioClip ? (UfbxAudioClip)element : null;

        // C: ufbx_as_pose (ufbx.c:33082)
        public static UfbxPose Pose(UfbxElement element)
            => element != null && element.Type == UfbxElementType.Pose ? (UfbxPose)element : null;

        // C: ufbx_as_metadata_object (ufbx.c:33083)
        public static UfbxMetadataObject MetadataObject(UfbxElement element)
            => element != null && element.Type == UfbxElementType.MetadataObject ? (UfbxMetadataObject)element : null;
    }
}
