// Node data model, ported from ufbx v0.23.1 (ufbx.h).
// Covers: ufbx_node.
//
// NOTE: PORTING_NOTES.md says `ufbx_node` is "not an element subclass", but in C ufbx_node
// starts with the ufbx_element header and is cast to `ufbx_element*` throughout (it appears in
// `ufbx_element_list`, `ufbx_element.instances`, connections, `elements_by_type`, ...). Deriving
// from UfbxElement is what makes those views type-check in C#, so the class does derive.

namespace Ufbx
{
    // C: struct ufbx_node
    public sealed class UfbxNode : UfbxElement
    {
        // C: parent
        public UfbxNode Parent;

        // C: children (ufbx_node_list)
        public UfbxNode[] Children;

        // C: mesh / light / camera / bone
        public UfbxMesh Mesh;
        public UfbxLight Light;
        public UfbxCamera Camera;
        public UfbxBone Bone;

        // C: attrib (ufbx_nullable ufbx_element *)
        public UfbxElement Attrib;

        // C: geometry_transform_helper
        public UfbxNode GeometryTransformHelper;

        // C: scale_helper
        public UfbxNode ScaleHelper;

        // C: attrib_type
        public UfbxElementType AttribType;

        // C: all_attribs (ufbx_element_list)
        public UfbxElement[] AllAttribs;

        // C: inherit_mode / original_inherit_mode
        public UfbxInheritMode InheritMode;
        public UfbxInheritMode OriginalInheritMode;

        // C: local_transform / geometry_transform
        public UfbxTransform LocalTransform;
        public UfbxTransform GeometryTransform;

        // C: inherit_scale
        public UfbxVec3 InheritScale;

        // C: inherit_scale_node
        public UfbxNode InheritScaleNode;

        // C: rotation_order
        public UfbxRotationOrder RotationOrder;

        // C: euler_rotation
        public UfbxVec3 EulerRotation;

        // C: node_to_parent / node_to_world / geometry_to_node / geometry_to_world / unscaled_node_to_world
        public UfbxMatrix NodeToParent;
        public UfbxMatrix NodeToWorld;
        public UfbxMatrix GeometryToNode;
        public UfbxMatrix GeometryToWorld;
        public UfbxMatrix UnscaledNodeToWorld;

        // C: adjust_pre_translation / adjust_pre_rotation / adjust_pre_scale /
        //    adjust_post_rotation / adjust_post_scale / adjust_translation_scale /
        //    adjust_mirror_axis
        public UfbxVec3 AdjustPreTranslation;
        public UfbxQuat AdjustPreRotation;
        public double AdjustPreScale;
        public UfbxQuat AdjustPostRotation;
        public double AdjustPostScale;
        public double AdjustTranslationScale;
        public UfbxMirrorAxis AdjustMirrorAxis;

        // C: materials (ufbx_material_list)
        public UfbxMaterial[] Materials;

        // C: bind_pose
        public UfbxPose BindPose;

        // C: visible / is_root / has_geometry_transform / use_rotation_space /
        //    has_adjust_transform / has_root_adjust_transform /
        //    is_geometry_transform_helper / is_scale_helper / is_scale_compensate_parent
        public bool Visible;
        public bool IsRoot;
        public bool HasGeometryTransform;
        public bool UseRotationSpace;
        public bool HasAdjustTransform;
        public bool HasRootAdjustTransform;
        public bool IsGeometryTransformHelper;
        public bool IsScaleHelper;
        public bool IsScaleCompensateParent;

        // C: node_depth
        public uint NodeDepth;
    }
}
