// Scene-finalize driver + scene update chain, ported from ufbx v0.23.1 ufbx.c. Owned by the
// S3c porting agent (2026-10-03 wave). Scope (C line numbers):
//   ufbxi_push_anim                        (ufbx.c:21632-21642)
//   ufbxi_finalize_scene                   (ufbx.c:21644-22627)
//   ufbxi_add/sub_translate, mul_scale*    (ufbx.c:22631-22663)
//   ufbxi_mul_quat                         (ufbx.c:22665-22673)
//   ufbxi_add_weighted_vec3/quat/mat       (ufbx.c:22675-22696)  [private copies; the ones in
//                                        SceneOpts.cs are S4a-private]
//   ufbxi_mul_rotate / _quat / _inv_rotate (ufbx.c:22698-22744)
//   ufbxi_mirror_translation / _rotation   (ufbx.c:22748-22759)
//   ufbxi_get_geometry_transform           (ufbx.c:22761-22787)
//   ufbxi_get_rotation / get_scale         (ufbx.c:22789-22837)
//   ufbxi_get_transform                    (ufbx.c:22839-22908)
//   ufbxi_get_texture_transform            (ufbx.c:22910-22939)
//   ufbxi_get_constraint_transform         (ufbx.c:22941-22956)
//   ufbxi_update_node                      (ufbx.c:22958-23045)
//   ufbxi_update_light                     (ufbx.c:23047-23065)
//   ufbxi_aperture_formats / update_camera (ufbx.c:23067-23255)
//   ufbxi_update_bone                      (ufbx.c:23257-23267)
//   ufbxi_update_line_curve                (ufbx.c:23269-23272)
//   ufbxi_update_pose                      (ufbx.c:23274-23290)
//   ufbxi_update_skin_cluster              (ufbx.c:23292-23300)
//   ufbxi_update_blend_channel             (ufbx.c:23302-23345)
//   ufbxi_update_material / texture        (ufbx.c:23347-23372)
//   ufbxi_update_anim_stack                (ufbx.c:23374-23391)
//   ufbxi_update_display_layer             (ufbx.c:23393-23398)
//   ufbxi_find_bool3                       (ufbx.c:23400-23417)
//   ufbxi_update_constraint                (ufbx.c:23419-23491)
//   ufbxi_update_anim                      (ufbx.c:23493-23498)
//   ufbxi_update_initial_clusters          (ufbx.c:23526-23622)
//   ufbxi_find_axis / time_mode_fps        (ufbx.c:23624-23656)
//   ufbxi_axis_matrix                      (ufbx.c:23659-23677)
//   ufbxi_update_adjust_transforms         (ufbx.c:23679-23807)
//   ufbxi_update_scene                     (ufbx.c:23809-23870)
//   ufbxi_update_scene_metadata            (ufbx.c:23872-23881)
//   ufbxi_update_scene_settings            (ufbx.c:23906-23934)
// `ufbxi_mirror_matrix*` (23500-23524) and `ufbxi_round_if_near` (23892-23904) already live in
// Parse/SceneOpts.cs (S4a) and are called as-is. `ufbxi_update_scene_settings_obj`
// (23936-23947) is ported in Parse/GeometryCache.cs (S4a).
//
// Conventions (PORTING_NOTES.md):
//  * C element pointer order == `element_id` order (comparators use ElementId).
//  * `ufbx_real` is `double`; no `System.Math`/`MathF`; no reordered float evaluation.
//  * Error sites: plain `ufbxi_check` -> `CheckNoDesc`, `ufbxi_check_msg` -> `CheckMsg`.
//  * `ufbxi_push(buf, T, n)` becomes array/List allocation (PORTING_NOTES #4).
//  * The C anim-prop sentinel (a zeroed `ufbx_anim_prop` appended past `anim_props.count`) is
//    NOT stored in the port's arrays: C's binary searches never read past `count`, and the
//    port's comparators cannot dereference the NULL element (see FinalizeScene, anim layers).
using System;
using System.Collections.Generic;

namespace Ufbx.NET
{
    internal static class UfbxiSceneUpdate
    {
        // ==================================================================
        // Transform arithmetic (ufbx.c:22631-22759)
        // ==================================================================

        // C: ufbxi_add_translate (ufbx.c:22631-22636).
        static void AddTranslate(ref UfbxTransform t, UfbxVec3 v)
        {
            t.Translation.X += v.X;
            t.Translation.Y += v.Y;
            t.Translation.Z += v.Z;
        }

        // C: ufbxi_sub_translate (ufbx.c:22638-22643).
        static void SubTranslate(ref UfbxTransform t, UfbxVec3 v)
        {
            t.Translation.X -= v.X;
            t.Translation.Y -= v.Y;
            t.Translation.Z -= v.Z;
        }

        // C: ufbxi_mul_scale (ufbx.c:22645-22653).
        static void MulScale(ref UfbxTransform t, UfbxVec3 v)
        {
            t.Translation.X *= v.X;
            t.Translation.Y *= v.Y;
            t.Translation.Z *= v.Z;
            t.Scale.X *= v.X;
            t.Scale.Y *= v.Y;
            t.Scale.Z *= v.Z;
        }

        // C: ufbxi_mul_scale_real (ufbx.c:22655-22663).
        static void MulScaleReal(ref UfbxTransform t, double v)
        {
            t.Translation.X *= v;
            t.Translation.Y *= v;
            t.Translation.Z *= v;
            t.Scale.X *= v;
            t.Scale.Y *= v;
            t.Scale.Z *= v;
        }

        // C: ufbxi_mul_quat (ufbx.c:22665-22673).
        internal static UfbxQuat MulQuat(UfbxQuat a, UfbxQuat b)
        {
            UfbxQuat r;
            r.X = a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y;
            r.Y = a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X;
            r.Z = a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W;
            r.W = a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z;
            return r;
        }

        // C: ufbxi_mul_rotate (ufbx.c:22698-22712).
        static void MulRotate(ref UfbxTransform t, UfbxVec3 v, UfbxRotationOrder order)
        {
            if (UfbxiProperties.IsVec3Zero(v)) return;

            UfbxQuat q = UfbxQuat.EulerToQuat(v, order);
            if (t.Rotation.W != 1.0) {
                t.Rotation = MulQuat(q, t.Rotation);
            } else {
                t.Rotation = q;
            }

            if (!UfbxiProperties.IsVec3Zero(t.Translation)) {
                t.Translation = UfbxQuat.RotateVec3(q, t.Translation);
            }
        }

        // C: ufbxi_mul_rotate_quat (ufbx.c:22714-22727).
        static void MulRotateQuat(ref UfbxTransform t, UfbxQuat q)
        {
            if (UfbxiProperties.IsQuatIdentity(q)) return;

            if (t.Rotation.W != 1.0) {
                t.Rotation = MulQuat(q, t.Rotation);
            } else {
                t.Rotation = q;
            }

            if (!UfbxiProperties.IsVec3Zero(t.Translation)) {
                t.Translation = UfbxQuat.RotateVec3(q, t.Translation);
            }
        }

        // C: ufbxi_mul_inv_rotate (ufbx.c:22729-22744).
        static void MulInvRotate(ref UfbxTransform t, UfbxVec3 v, UfbxRotationOrder order)
        {
            if (UfbxiProperties.IsVec3Zero(v)) return;

            UfbxQuat q = UfbxQuat.EulerToQuat(v, order);
            q.X = -q.X; q.Y = -q.Y; q.Z = -q.Z;
            if (t.Rotation.W != 1.0) {
                t.Rotation = MulQuat(q, t.Rotation);
            } else {
                t.Rotation = q;
            }

            if (!UfbxiProperties.IsVec3Zero(t.Translation)) {
                t.Translation = UfbxQuat.RotateVec3(q, t.Translation);
            }
        }

        // C: ufbxi_mirror_translation (ufbx.c:22748-22752).
        static void MirrorTranslation(ref UfbxVec3 v, UfbxMirrorAxis axis)
        {
            switch ((int)axis - 1) {
            case 0: v.X = -v.X; break;
            case 1: v.Y = -v.Y; break;
            default: v.Z = -v.Z; break;
            }
        }

        // C: ufbxi_mirror_rotation (ufbx.c:22754-22759): negates v[axis%3] and v[(axis+1)%3].
        static void MirrorRotation(ref UfbxQuat q, UfbxMirrorAxis axis)
        {
            int ax = (int)axis;
            int i0 = ax % 3;
            int i1 = (ax + 1) % 3;
            if (i0 == 0) q.X = -q.X; else if (i0 == 1) q.Y = -q.Y; else q.Z = -q.Z;
            if (i1 == 0) q.X = -q.X; else if (i1 == 1) q.Y = -q.Y; else q.Z = -q.Z;
        }

        // C: ufbxi_unscaled_transform_to_matrix (ufbx.c near 22600s).
        internal static UfbxMatrix UnscaledTransformToMatrix(UfbxTransform t)
        {
            t.Scale.X = 1.0;
            t.Scale.Y = 1.0;
            t.Scale.Z = 1.0;
            return UfbxMatrix.FromTransform(t);
        }

        // C: ufbxi_matrix_all_zero (ufbx.c:11562-11569).
        internal static bool MatrixAllZero(UfbxMatrix m)
        {
            return m.M00 == 0.0 && m.M10 == 0.0 && m.M20 == 0.0
                && m.M01 == 0.0 && m.M11 == 0.0 && m.M21 == 0.0
                && m.M02 == 0.0 && m.M12 == 0.0 && m.M22 == 0.0
                && m.M03 == 0.0 && m.M13 == 0.0 && m.M23 == 0.0;
        }

        // C: ufbxi_one_vec3 (ufbx.c:5888).
        static readonly UfbxVec3 OneVec3 = new UfbxVec3(1.0, 1.0, 1.0);

        // ==================================================================
        // Property -> transform interpretation (ufbx.c:22761-22956)
        // ==================================================================

        // C: ufbxi_get_geometry_transform (ufbx.c:22761-22787).
        internal static UfbxTransform GetGeometryTransform(UfbxProps props, UfbxNode node)
        {
            UfbxVec3 translation = UfbxiProperties.FindVec3(props, UfbxiStrings.GeometricTranslation, 0.0, 0.0, 0.0);
            UfbxVec3 rotation = UfbxiProperties.FindVec3(props, UfbxiStrings.GeometricRotation, 0.0, 0.0, 0.0);
            UfbxVec3 scaling = UfbxiProperties.FindVec3(props, UfbxiStrings.GeometricScaling, 1.0, 1.0, 1.0);

            UfbxTransform t = new UfbxTransform(default, UfbxQuat.Identity, OneVec3);

            // WorldTransform = ParentWorldTransform * T * R * S * (OT * OR * OS)

            MulScale(ref t, scaling);
            MulRotate(ref t, rotation, UfbxRotationOrder.Xyz);
            AddTranslate(ref t, translation);

            if (node.HasAdjustTransform) {
                t.Translation.X *= node.AdjustTranslationScale;
                t.Translation.Y *= node.AdjustTranslationScale;
                t.Translation.Z *= node.AdjustTranslationScale;
            }

            if (node.AdjustMirrorAxis != 0) {
                MirrorTranslation(ref t.Translation, node.AdjustMirrorAxis);
                MirrorRotation(ref t.Rotation, node.AdjustMirrorAxis);
            }

            return t;
        }

        // C: ufbxi_get_rotation (ufbx.c:22789-22818).
        internal static UfbxQuat GetRotation(UfbxProps props, UfbxRotationOrder order, UfbxNode node)
        {
            UfbxVec3 rotation = UfbxiProperties.FindVec3(props, UfbxiStrings.Lcl_Rotation, 0.0, 0.0, 0.0);
            UfbxVec3 preRotation = UfbxiProperties.FindVec3(props, UfbxiStrings.PreRotation, 0.0, 0.0, 0.0);
            UfbxVec3 postRotation = UfbxiProperties.FindVec3(props, UfbxiStrings.PostRotation, 0.0, 0.0, 0.0);

            UfbxTransform t = new UfbxTransform(default, UfbxQuat.Identity, OneVec3);

            if (node.HasAdjustTransform) {
                MulRotateQuat(ref t, node.AdjustPostRotation);
            }

            if (node.UseRotationSpace) {
                MulInvRotate(ref t, postRotation, UfbxRotationOrder.Xyz);
                MulRotate(ref t, rotation, order);
                MulRotate(ref t, preRotation, UfbxRotationOrder.Xyz);
            } else {
                MulRotate(ref t, rotation, UfbxRotationOrder.Xyz);
            }

            if (node.HasAdjustTransform) {
                MulRotateQuat(ref t, node.AdjustPreRotation);
            }

            if (node.AdjustMirrorAxis != 0) {
                MirrorRotation(ref t.Rotation, node.AdjustMirrorAxis);
            }

            return t.Rotation;
        }

        // C: ufbxi_get_scale (ufbx.c:22820-22837).
        internal static UfbxVec3 GetScale(UfbxProps props, UfbxNode node)
        {
            UfbxVec3 scaling = UfbxiProperties.FindVec3(props, UfbxiStrings.Lcl_Scaling, 1.0, 1.0, 1.0);

            UfbxTransform t = new UfbxTransform(default, UfbxQuat.Identity, OneVec3);

            if (node.HasAdjustTransform) {
                MulScaleReal(ref t, node.AdjustPostScale);
            }

            MulScale(ref t, scaling);

            if (node.HasAdjustTransform) {
                MulScaleReal(ref t, node.AdjustPreScale);
            }

            return t.Scale;
        }

        // C: ufbxi_get_transform (ufbx.c:22839-22908). `translationScale` is C's
        // `const ufbx_vec3 *translation_scale` (null unless the parent has a scale helper).
        internal static UfbxTransform GetTransform(UfbxProps props, UfbxRotationOrder order, UfbxNode node, UfbxVec3? translationScale)
        {
            UfbxVec3 scalePivot = UfbxiProperties.FindVec3(props, UfbxiStrings.ScalingPivot, 0.0, 0.0, 0.0);
            UfbxVec3 rotPivot = UfbxiProperties.FindVec3(props, UfbxiStrings.RotationPivot, 0.0, 0.0, 0.0);
            UfbxVec3 scaleOffset = UfbxiProperties.FindVec3(props, UfbxiStrings.ScalingOffset, 0.0, 0.0, 0.0);
            UfbxVec3 rotOffset = UfbxiProperties.FindVec3(props, UfbxiStrings.RotationOffset, 0.0, 0.0, 0.0);

            UfbxVec3 translation = UfbxiProperties.FindVec3(props, UfbxiStrings.Lcl_Translation, 0.0, 0.0, 0.0);
            UfbxVec3 rotation = UfbxiProperties.FindVec3(props, UfbxiStrings.Lcl_Rotation, 0.0, 0.0, 0.0);
            UfbxVec3 scaling = UfbxiProperties.FindVec3(props, UfbxiStrings.Lcl_Scaling, 1.0, 1.0, 1.0);

            UfbxVec3 preRotation = UfbxiProperties.FindVec3(props, UfbxiStrings.PreRotation, 0.0, 0.0, 0.0);
            UfbxVec3 postRotation = UfbxiProperties.FindVec3(props, UfbxiStrings.PostRotation, 0.0, 0.0, 0.0);

            UfbxTransform t = new UfbxTransform(default, UfbxQuat.Identity, OneVec3);

            // WorldTransform = ParentWorldTransform * T * Roff * Rp * Rpre * R * Rpost * Rp-1 * Soff * Sp * S * Sp-1
            // NOTE: Rpost is inverted (!) after converting from PostRotation Euler angles

            if (translationScale.HasValue) {
                translation.X *= translationScale.Value.X;
                translation.Y *= translationScale.Value.Y;
                translation.Z *= translationScale.Value.Z;
            }

            if (node.HasAdjustTransform) {
                MulRotateQuat(ref t, node.AdjustPostRotation);
                MulScaleReal(ref t, node.AdjustPostScale);
            }

            SubTranslate(ref t, scalePivot);
            MulScale(ref t, scaling);
            AddTranslate(ref t, scalePivot);

            AddTranslate(ref t, scaleOffset);

            SubTranslate(ref t, rotPivot);
            if (node.UseRotationSpace) {
                MulInvRotate(ref t, postRotation, UfbxRotationOrder.Xyz);
                MulRotate(ref t, rotation, order);
                MulRotate(ref t, preRotation, UfbxRotationOrder.Xyz);
            } else {
                MulRotate(ref t, rotation, UfbxRotationOrder.Xyz);
            }
            AddTranslate(ref t, rotPivot);

            AddTranslate(ref t, rotOffset);

            AddTranslate(ref t, translation);

            if (node.HasAdjustTransform) {
                AddTranslate(ref t, node.AdjustPreTranslation);
                MulRotateQuat(ref t, node.AdjustPreRotation);
                MulScaleReal(ref t, node.AdjustPreScale);
                t.Translation.X *= node.AdjustTranslationScale;
                t.Translation.Y *= node.AdjustTranslationScale;
                t.Translation.Z *= node.AdjustTranslationScale;
            }

            if (node.AdjustMirrorAxis != 0) {
                MirrorTranslation(ref t.Translation, node.AdjustMirrorAxis);
                MirrorRotation(ref t.Rotation, node.AdjustMirrorAxis);
            }

            // C: ufbxi_regression_assert()s against get_rotation/get_scale -- compiled out.

            return t;
        }

        // C: ufbxi_get_texture_transform (ufbx.c:22910-22939).
        internal static UfbxTransform GetTextureTransform(UfbxProps props)
        {
            UfbxVec3 scalePivot = UfbxiProperties.FindVec3(props, UfbxiStrings.TextureScalingPivot, 0.0, 0.0, 0.0);
            UfbxVec3 rotPivot = UfbxiProperties.FindVec3(props, UfbxiStrings.TextureRotationPivot, 0.0, 0.0, 0.0);

            UfbxVec3 translation = UfbxiProperties.FindVec3(props, UfbxiStrings.Translation, 0.0, 0.0, 0.0);
            UfbxVec3 rotation = UfbxiProperties.FindVec3(props, UfbxiStrings.Rotation, 0.0, 0.0, 0.0);
            UfbxVec3 scaling = UfbxiProperties.FindVec3(props, UfbxiStrings.Scaling, 1.0, 1.0, 1.0);

            UfbxTransform t = new UfbxTransform(default, UfbxQuat.Identity, OneVec3);

            SubTranslate(ref t, scalePivot);
            MulScale(ref t, scaling);
            AddTranslate(ref t, scalePivot);

            SubTranslate(ref t, rotPivot);
            MulRotate(ref t, rotation, UfbxRotationOrder.Xyz);
            AddTranslate(ref t, rotPivot);

            AddTranslate(ref t, translation);

            if (UfbxiProperties.FindInt(props, UfbxiStrings.UVSwap, 0) != 0) {
                UfbxVec3 swapScale = new UfbxVec3(-1.0, 0.0, 0.0);
                UfbxVec3 swapRotate = new UfbxVec3(0.0, 0.0, -90.0);
                MulScale(ref t, swapScale);
                MulRotate(ref t, swapRotate, UfbxRotationOrder.Xyz);
            }

            return t;
        }

        // C: ufbxi_get_constraint_transform (ufbx.c:22941-22956).
        internal static UfbxTransform GetConstraintTransform(UfbxProps props)
        {
            UfbxVec3 translation = UfbxiProperties.FindVec3(props, UfbxiStrings.Translation, 0.0, 0.0, 0.0);
            UfbxVec3 rotation = UfbxiProperties.FindVec3(props, UfbxiStrings.Rotation, 0.0, 0.0, 0.0);
            UfbxVec3 rotationOffset = UfbxiProperties.FindVec3(props, UfbxiStrings.RotationOffset, 0.0, 0.0, 0.0);
            UfbxVec3 scaling = UfbxiProperties.FindVec3(props, UfbxiStrings.Scaling, 1.0, 1.0, 1.0);

            UfbxTransform t = new UfbxTransform(default, UfbxQuat.Identity, OneVec3);

            MulScale(ref t, scaling);
            MulRotate(ref t, rotation, UfbxRotationOrder.Xyz);
            MulRotate(ref t, rotationOffset, UfbxRotationOrder.Xyz);
            AddTranslate(ref t, translation);

            return t;
        }

        // ==================================================================
        // ufbxi_update_node (ufbx.c:22958-23045)
        // ==================================================================

        internal static void UpdateNode(UfbxNode node, UfbxTransformOverride[] overrides)
        {
            node.RotationOrder = (UfbxRotationOrder)UfbxiProperties.FindEnum(node.Props, UfbxiStrings.RotationOrder,
                (long)UfbxRotationOrder.Xyz, (long)UfbxRotationOrder.Spheric);
            node.EulerRotation = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.Lcl_Rotation, 0.0, 0.0, 0.0);

            if (!node.IsRoot) {
                bool rotationActive = UfbxiProperties.FindInt(node.Props, UfbxiStrings.RotationActive, 1) != 0;
                bool rotationLimitOnly = UfbxiProperties.FindInt(node.Props, UfbxiStrings.RotationSpaceForLimitOnly, 0) != 0;
                node.UseRotationSpace = rotationActive && !rotationLimitOnly;

                UfbxVec3? transformScale = null;
                if (node.Parent != null && node.Parent.ScaleHelper != null) {
                    transformScale = node.Parent.ScaleHelper.LocalTransform.Scale;
                }
                node.LocalTransform = GetTransform(node.Props, node.RotationOrder, node, transformScale);
                if (node.IsScaleHelper && node.Parent != null && node.Parent.InheritScaleNode != null) {
                    UfbxNode scaleParent = node.Parent.InheritScaleNode;
                    if (scaleParent.ScaleHelper != null) {
                        UfbxVec3 inheritScale = scaleParent.ScaleHelper.LocalTransform.Scale;
                        UfbxTransform lt = node.LocalTransform;
                        lt.Scale.X *= inheritScale.X;
                        lt.Scale.Y *= inheritScale.Y;
                        lt.Scale.Z *= inheritScale.Z;
                        node.LocalTransform = lt;
                    }
                }

                if (overrides != null && overrides.Length > 0) {
                    uint typedId = node.TypedId;
                    int overrideIx = -1; // C: SIZE_MAX
                    {
                        UfbxTransformOverride[] data = overrides;
                        int size = data.Length;
                        int lo = 0, hi = size;
                        while (hi - lo > 16) {
                            int mid = lo + (hi - lo) / 2;
                            if (data[mid].NodeId < typedId) {
                                lo = mid + 1;
                            } else {
                                hi = mid + 1;
                            }
                        }
                        for (; lo < hi; lo++) {
                            if (data[lo].NodeId == typedId) { overrideIx = lo; break; }
                        }
                    }
                    if (overrideIx >= 0) {
                        node.LocalTransform = overrides[overrideIx].Transform;
                    }
                }
                node.NodeToParent = UfbxMatrix.FromTransform(node.LocalTransform);
                node.GeometryTransform = GetGeometryTransform(node.Props, node);
            } else {
                node.GeometryTransform = UfbxTransform.Identity;
            }

            UfbxMatrix unscaledNodeToParent = UnscaledTransformToMatrix(node.LocalTransform);

            node.InheritScale = node.LocalTransform.Scale;

            UfbxNode parent = node.Parent;
            if (parent != null) {
                if (node.InheritMode == UfbxInheritMode.Normal) {
                    node.NodeToWorld = UfbxMatrix.Mul(parent.NodeToWorld, node.NodeToParent);
                    node.UnscaledNodeToWorld = UfbxMatrix.Mul(parent.NodeToWorld, unscaledNodeToParent);
                } else {
                    UfbxTransform transform = node.LocalTransform;

                    UfbxVec3 parentScale = OneVec3;
                    if (node.InheritScaleNode != null) {
                        parentScale = node.InheritScaleNode.InheritScale;
                    }

                    transform.Scale.X *= parentScale.X;
                    transform.Scale.Y *= parentScale.Y;
                    transform.Scale.Z *= parentScale.Z;
                    transform.Translation.X *= parent.InheritScale.X;
                    transform.Translation.Y *= parent.InheritScale.Y;
                    transform.Translation.Z *= parent.InheritScale.Z;

                    UfbxMatrix nodeToUnscaledParent = UfbxMatrix.FromTransform(transform);
                    UfbxMatrix unscaledNodeToUnscaledParent = UnscaledTransformToMatrix(transform);

                    node.InheritScale = transform.Scale;
                    node.NodeToWorld = UfbxMatrix.Mul(parent.UnscaledNodeToWorld, nodeToUnscaledParent);
                    node.UnscaledNodeToWorld = UfbxMatrix.Mul(parent.UnscaledNodeToWorld, unscaledNodeToUnscaledParent);
                }
            } else {
                node.NodeToWorld = node.NodeToParent;
                node.UnscaledNodeToWorld = unscaledNodeToParent;
            }

            if (!UfbxiProperties.IsTransformIdentity(node.GeometryTransform)) {
                node.GeometryToNode = UfbxMatrix.FromTransform(node.GeometryTransform);
                node.GeometryToWorld = UfbxMatrix.Mul(node.NodeToWorld, node.GeometryToNode);
                node.HasGeometryTransform = true;
            } else {
                node.GeometryToNode = UfbxMatrix.Identity;
                node.GeometryToWorld = node.NodeToWorld;
                node.HasGeometryTransform = false;
            }

            node.Visible = UfbxiProperties.FindInt(node.Props, UfbxiStrings.Visibility, 1) != 0;
        }

        // ==================================================================
        // ufbxi_update_light (ufbx.c:23047-23065)
        // ==================================================================

        internal static void UpdateLight(UfbxLight light)
        {
            light.Intensity = UfbxiProperties.FindReal(light.Props, UfbxiStrings.Intensity, 100.0) / 100.0;

            light.Color = UfbxiProperties.FindVec3(light.Props, UfbxiStrings.Color, 1.0, 1.0, 1.0);
            light.Type = (UfbxLightType)UfbxiProperties.FindEnum(light.Props, UfbxiStrings.LightType, 0, (long)UfbxLightType.Volume);
            light.Decay = (UfbxLightDecay)UfbxiProperties.FindEnum(light.Props, UfbxiStrings.DecayType, (long)UfbxLightDecay.None, (long)UfbxLightDecay.Cubic);
            light.AreaShape = (UfbxLightAreaShape)UfbxiProperties.FindEnum(light.Props, UfbxiStrings.AreaLightShape, 0, (long)UfbxLightAreaShape.Sphere);
            light.InnerAngle = UfbxiProperties.FindReal(light.Props, UfbxiStrings.HotSpot, 0.0);
            light.InnerAngle = UfbxiProperties.FindReal(light.Props, UfbxiStrings.InnerAngle, light.InnerAngle);
            light.OuterAngle = UfbxiProperties.FindReal(light.Props, UfbxiStrings.Cone_angle, 0.0);
            light.OuterAngle = UfbxiProperties.FindReal(light.Props, UfbxiStrings.ConeAngle, light.OuterAngle);
            light.OuterAngle = UfbxiProperties.FindReal(light.Props, UfbxiStrings.OuterAngle, light.OuterAngle);
            light.CastLight = UfbxiProperties.FindInt(light.Props, UfbxiStrings.CastLight, 1) != 0;
            light.CastShadows = UfbxiProperties.FindInt(light.Props, UfbxiStrings.CastShadows, 0) != 0;
        }

        // ==================================================================
        // ufbxi_update_camera (ufbx.c:23067-23255)
        // ==================================================================

        // C: ufbxi_aperture_format / ufbxi_aperture_formats[] (ufbx.c:23067-23085).
        struct UfbxiApertureFormat
        {
            public ushort FilmSizeX;
            public ushort FilmSizeY;
        }

        static readonly UfbxiApertureFormat[] ApertureFormats = new UfbxiApertureFormat[] {
            new UfbxiApertureFormat { FilmSizeX = 1000, FilmSizeY = 1000, }, // CUSTOM
            new UfbxiApertureFormat { FilmSizeX =  404, FilmSizeY =  295, }, // 16MM_THEATRICAL
            new UfbxiApertureFormat { FilmSizeX =  493, FilmSizeY =  292, }, // SUPER_16MM
            new UfbxiApertureFormat { FilmSizeX =  864, FilmSizeY =  630, }, // 35MM_ACADEMY
            new UfbxiApertureFormat { FilmSizeX =  816, FilmSizeY =  612, }, // 35MM_TV_PROJECTION
            new UfbxiApertureFormat { FilmSizeX =  980, FilmSizeY =  735, }, // 35MM_FULL_APERTURE
            new UfbxiApertureFormat { FilmSizeX =  825, FilmSizeY =  446, }, // 35MM_185_PROJECTION
            new UfbxiApertureFormat { FilmSizeX =  864, FilmSizeY =  732, }, // 35MM_ANAMORPHIC
            new UfbxiApertureFormat { FilmSizeX = 2066, FilmSizeY =  906, }, // 70MM_PROJECTION
            new UfbxiApertureFormat { FilmSizeX = 1485, FilmSizeY =  991, }, // VISTAVISION
            new UfbxiApertureFormat { FilmSizeX = 2080, FilmSizeY = 1480, }, // DYNAVISION
            new UfbxiApertureFormat { FilmSizeX = 2772, FilmSizeY = 2072, }, // IMAX
        };

        // C: UFBXI_MM_TO_INCH (ufbx.c:5896).
        const double MmToInch = 0.0393700787;

        internal static void UpdateCamera(UfbxScene scene, UfbxCamera camera)
        {
            camera.ProjectionMode = (UfbxProjectionMode)UfbxiProperties.FindEnum(camera.Props, UfbxiStrings.CameraProjectionType, 0, (long)UfbxProjectionMode.Orthographic);
            camera.AspectMode = (UfbxAspectMode)UfbxiProperties.FindEnum(camera.Props, UfbxiStrings.AspectRatioMode, 0, (long)UfbxAspectMode.FixedHeight);
            camera.ApertureMode = (UfbxApertureMode)UfbxiProperties.FindEnum(camera.Props, UfbxiStrings.ApertureMode, (long)UfbxApertureMode.Vertical, (long)UfbxApertureMode.FocalLength);
            camera.ApertureFormat = (UfbxApertureFormat)UfbxiProperties.FindEnum(camera.Props, UfbxiStrings.ApertureFormat, (long)UfbxApertureFormat.Custom, (long)UfbxApertureFormat.Imax);
            camera.GateFit = (UfbxGateFit)UfbxiProperties.FindEnum(camera.Props, UfbxiStrings.GateFit, 0, (long)UfbxGateFit.Stretch);

            camera.NearPlane = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.NearPlane, 0.0);
            camera.FarPlane = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FarPlane, 0.0);

            // Search both W/H and Width/Height but prefer the latter
            double aspectX = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.AspectW, 0.0);
            double aspectY = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.AspectH, 0.0);
            aspectX = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.AspectWidth, aspectX);
            aspectY = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.AspectHeight, aspectY);

            double fov = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FieldOfView, 0.0);
            double fovX = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FieldOfViewX, 0.0);
            double fovY = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FieldOfViewY, 0.0);

            double focalLength = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FocalLength, 0.0);
            double orthoExtent = scene.Metadata.OrthoSizeUnit * UfbxiProperties.FindReal(camera.Props, UfbxiStrings.OrthoZoom, 1.0);

            UfbxiApertureFormat format = ApertureFormats[(int)camera.ApertureFormat];
            UfbxVec2 filmSize = new UfbxVec2((double)format.FilmSizeX * 0.001, (double)format.FilmSizeY * 0.001);
            double squeezeRatio = camera.ApertureFormat == UfbxApertureFormat.Mm35Anamorphic ? 2.0 : 1.0;

            filmSize.X = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FilmWidth, filmSize.X);
            filmSize.Y = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FilmHeight, filmSize.Y);
            squeezeRatio = UfbxiProperties.FindReal(camera.Props, UfbxiStrings.FilmSqueezeRatio, squeezeRatio);

            if (aspectX <= 0.0 && aspectY <= 0.0) {
                aspectX = filmSize.X > 0.0 ? filmSize.X : 1.0;
                aspectY = filmSize.Y > 0.0 ? filmSize.Y : 1.0;
            } else if (aspectX <= 0.0) {
                if (filmSize.X > 0.0 && filmSize.Y > 0.0) {
                    aspectX = aspectY / filmSize.Y * filmSize.X;
                } else {
                    aspectX = aspectY;
                }
            } else if (aspectY <= 0.0) {
                if (filmSize.X > 0.0 && filmSize.Y > 0.0) {
                    aspectY = aspectX / filmSize.X * filmSize.Y;
                } else {
                    aspectY = aspectX;
                }
            }

            filmSize.Y *= squeezeRatio;

            // TODO: Should this be done always?
            orthoExtent *= scene.Metadata.GeometryScale;
            camera.NearPlane *= scene.Metadata.GeometryScale;
            camera.FarPlane *= scene.Metadata.GeometryScale;

            camera.FocalLengthMm = focalLength;
            camera.FilmSizeInch = filmSize;
            camera.SqueezeRatio = squeezeRatio;
            camera.OrthographicExtent = orthoExtent;

            switch (camera.AspectMode) {
            case UfbxAspectMode.WindowSize:
            case UfbxAspectMode.FixedRatio:
                camera.ResolutionIsPixels = false;
                camera.Resolution.X = aspectX;
                camera.Resolution.Y = aspectY;
                break;
            case UfbxAspectMode.FixedResolution:
                camera.ResolutionIsPixels = true;
                camera.Resolution.X = aspectX;
                camera.Resolution.Y = aspectY;
                break;
            case UfbxAspectMode.FixedWidth:
                camera.ResolutionIsPixels = true;
                camera.Resolution.X = aspectX;
                camera.Resolution.Y = aspectX * aspectY;
                break;
            case UfbxAspectMode.FixedHeight:
                camera.ResolutionIsPixels = true;
                camera.Resolution.X = aspectY * aspectX;
                camera.Resolution.Y = aspectY;
                break;
            default:
                // C: ufbxi_unreachable("Unexpected aspect mode")
                break;
            }

            double aspectRatio = camera.Resolution.X / camera.Resolution.Y;
            double filmRatio = filmSize.X / filmSize.Y;

            camera.AspectRatio = aspectRatio;

            UfbxGateFit effectiveFit = camera.GateFit;
            if (effectiveFit == UfbxGateFit.Fill) {
                effectiveFit = aspectRatio > filmRatio ? UfbxGateFit.Horizontal : UfbxGateFit.Vertical;
            } else if (effectiveFit == UfbxGateFit.Overscan) {
                effectiveFit = aspectRatio < filmRatio ? UfbxGateFit.Horizontal : UfbxGateFit.Vertical;
            }

            switch (effectiveFit) {
            case UfbxGateFit.None:
                camera.ApertureSizeInch = camera.FilmSizeInch;
                camera.OrthographicSize.X = orthoExtent;
                camera.OrthographicSize.Y = orthoExtent;
                break;
            case UfbxGateFit.Vertical:
                camera.ApertureSizeInch.X = camera.FilmSizeInch.Y * aspectRatio;
                camera.ApertureSizeInch.Y = camera.FilmSizeInch.Y;
                camera.OrthographicSize.X = orthoExtent * aspectRatio;
                camera.OrthographicSize.Y = orthoExtent;
                break;
            case UfbxGateFit.Horizontal:
                camera.ApertureSizeInch.X = camera.FilmSizeInch.X;
                camera.ApertureSizeInch.Y = camera.FilmSizeInch.X / aspectRatio;
                camera.OrthographicSize.X = orthoExtent;
                camera.OrthographicSize.Y = orthoExtent / aspectRatio;
                break;
            case UfbxGateFit.Fill:
            case UfbxGateFit.Overscan:
                // C: ufbxi_unreachable -- remapped to vertical/horizontal above.
                camera.ApertureSizeInch = camera.FilmSizeInch;
                camera.OrthographicSize.X = orthoExtent;
                camera.OrthographicSize.Y = orthoExtent;
                break;
            case UfbxGateFit.Stretch:
                camera.ApertureSizeInch = camera.FilmSizeInch;
                camera.OrthographicSize.X = orthoExtent;
                camera.OrthographicSize.Y = orthoExtent;
                break;
            default:
                break;
            }

            switch (camera.ApertureMode) {
            case UfbxApertureMode.HorizontalAndVertical:
                camera.FieldOfViewDeg.X = fovX;
                camera.FieldOfViewDeg.Y = fovY;
                camera.FieldOfViewTan.X = UfbxMath.Tan(fovX * (UfbxMathConsts.DegToRad * 0.5));
                camera.FieldOfViewTan.Y = UfbxMath.Tan(fovY * (UfbxMathConsts.DegToRad * 0.5));
                break;
            case UfbxApertureMode.Horizontal:
                camera.FieldOfViewDeg.X = fov;
                camera.FieldOfViewTan.X = UfbxMath.Tan(fov * (UfbxMathConsts.DegToRad * 0.5));
                camera.FieldOfViewTan.Y = camera.FieldOfViewTan.X / aspectRatio;
                camera.FieldOfViewDeg.Y = UfbxMath.Atan(camera.FieldOfViewTan.Y) * UfbxMathConsts.RadToDeg * 2.0;
                break;
            case UfbxApertureMode.Vertical:
                camera.FieldOfViewDeg.Y = fov;
                camera.FieldOfViewTan.Y = UfbxMath.Tan(fov * (UfbxMathConsts.DegToRad * 0.5));
                camera.FieldOfViewTan.X = camera.FieldOfViewTan.Y * aspectRatio;
                camera.FieldOfViewDeg.X = UfbxMath.Atan(camera.FieldOfViewTan.X) * UfbxMathConsts.RadToDeg * 2.0;
                break;
            case UfbxApertureMode.FocalLength:
                camera.FieldOfViewTan.X = camera.ApertureSizeInch.X / (camera.FocalLengthMm * MmToInch) * 0.5;
                camera.FieldOfViewTan.Y = camera.ApertureSizeInch.Y / (camera.FocalLengthMm * MmToInch) * 0.5;
                camera.FieldOfViewDeg.X = UfbxMath.Atan(camera.FieldOfViewTan.X) * UfbxMathConsts.RadToDeg * 2.0;
                camera.FieldOfViewDeg.Y = UfbxMath.Atan(camera.FieldOfViewTan.Y) * UfbxMathConsts.RadToDeg * 2.0;
                break;
            default:
                break;
            }

            if (camera.ProjectionMode == UfbxProjectionMode.Perspective) {
                camera.ProjectionPlane = camera.FieldOfViewTan;
            } else {
                camera.ProjectionPlane = camera.OrthographicSize;
            }
        }

        // ==================================================================
        // ufbxi_update_bone / ufbxi_update_line_curve (ufbx.c:23257-23272)
        // ==================================================================

        internal static void UpdateBone(UfbxScene scene, UfbxBone bone)
        {
            double unit = scene.Metadata.BonePropSizeUnit;

            bone.Radius = UfbxiProperties.FindReal(bone.Props, UfbxiStrings.Size, unit) / unit;
            if (scene.Metadata.BonePropLimbLengthRelative) {
                bone.RelativeLength = UfbxiProperties.FindReal(bone.Props, UfbxiStrings.LimbLength, 1.0);
            } else {
                bone.RelativeLength = 1.0;
            }
        }

        internal static void UpdateLineCurve(UfbxLineCurve line)
        {
            line.Color = UfbxiProperties.FindVec3(line.Props, UfbxiStrings.Color, 1.0, 1.0, 1.0);
        }

        // ==================================================================
        // ufbxi_update_pose / ufbxi_update_skin_cluster (ufbx.c:23274-23300)
        // ==================================================================

        internal static void UpdatePose(UfbxPose pose)
        {
            foreach (UfbxBonePose bone in pose.BonePoses) {
                UfbxNode node = bone.BoneNode;

                UfbxMatrix parentToWorld = UfbxMatrix.Identity;
                UfbxBonePose bonePose = UfbxiSceneFinalize.GetBonePose(pose, node.Parent);
                if (bonePose != null) {
                    parentToWorld = bonePose.BoneToWorld;
                } else if (node.Parent != null) {
                    parentToWorld = node.Parent.NodeToWorld;
                }

                UfbxMatrix worldToParent = UfbxMatrix.Invert(parentToWorld);
                bone.BoneToParent = UfbxMatrix.Mul(worldToParent, bone.BoneToWorld);
            }
        }

        internal static void UpdateSkinCluster(UfbxSkinCluster cluster)
        {
            if (cluster.BoneNode != null) {
                cluster.GeometryToWorld = UfbxMatrix.Mul(cluster.BoneNode.NodeToWorld, cluster.GeometryToBone);
            } else {
                cluster.GeometryToWorld = UfbxMatrix.Mul(cluster.BindToWorld, cluster.GeometryToBone);
            }
            cluster.GeometryToWorldTransform = UfbxMatrix.ToTransform(cluster.GeometryToWorld);
        }

        // ==================================================================
        // ufbxi_update_blend_channel (ufbx.c:23302-23345)
        // ==================================================================

        internal static void UpdateBlendChannel(UfbxBlendChannel channel)
        {
            double weight = UfbxiProperties.FindReal(channel.Props, UfbxiStrings.DeformPercent, 0.0) * 0.01;
            channel.Weight = weight;

            int numKeys = channel.Keyframes != null ? channel.Keyframes.Length : 0;
            if (numKeys > 0) {
                UfbxBlendKeyframe[] keys = channel.Keyframes;

                // Reset the effective weights to zero and find the split around zero
                int lastNegative = -1;
                for (int i = 0; i < numKeys; i++) {
                    keys[i].EffectiveWeight = 0.0;
                    if (keys[i].TargetWeight < 0.0) lastNegative = i;
                }

                // Find either the next or last keyframe away from zero
                UfbxBlendKeyframe zeroKey = new UfbxBlendKeyframe(); // C: { NULL }
                UfbxBlendKeyframe prev = zeroKey, next = zeroKey;
                if (weight > 0.0) {
                    if (lastNegative >= 0) prev = keys[lastNegative];
                    for (int i = lastNegative + 1; i < numKeys; i++) {
                        prev = next;
                        next = keys[i];
                        if (next.TargetWeight > weight) break;
                    }
                } else {
                    if (lastNegative + 1 < numKeys) prev = keys[lastNegative + 1];
                    for (int i = lastNegative; i >= 0; i--) {
                        prev = next;
                        next = keys[i];
                        if (next.TargetWeight < weight) break;
                    }
                }

                // Linearly interpolate between the endpoints with the weight
                double delta = next.TargetWeight - prev.TargetWeight;
                if (delta != 0.0) {
                    double t = (weight - prev.TargetWeight) / delta;
                    prev.EffectiveWeight = 1.0 - t;
                    next.EffectiveWeight = t;
                }
            }
        }

        // ==================================================================
        // ufbxi_update_material / ufbxi_update_texture (ufbx.c:23347-23372)
        // ==================================================================

        internal static void UpdateMaterial(UfbxScene scene, UfbxMaterial material)
        {
            if (material.Props.NumAnimated > 0) {
                UfbxiSceneFinalize.FetchMaps(scene, material);
            }
        }

        internal static void UpdateTexture(UfbxTexture texture)
        {
            texture.UvTransform = GetTextureTransform(texture.Props);
            if (!UfbxiProperties.IsTransformIdentity(texture.UvTransform)) {
                texture.HasUvTransform = true;
                texture.TextureToUv = UfbxMatrix.FromTransform(texture.UvTransform);
                texture.UvToTexture = UfbxMatrix.Invert(texture.TextureToUv);
            } else {
                texture.HasUvTransform = false;
                texture.TextureToUv = UfbxMatrix.Identity;
                texture.UvToTexture = UfbxMatrix.Identity;
            }
            texture.WrapU = (UfbxWrapMode)UfbxiProperties.FindEnum(texture.Props, UfbxiStrings.WrapModeU, 0, (long)UfbxWrapMode.Clamp);
            texture.WrapV = (UfbxWrapMode)UfbxiProperties.FindEnum(texture.Props, UfbxiStrings.WrapModeV, 0, (long)UfbxWrapMode.Clamp);

            if (texture.Shader != null) {
                UfbxiSceneFinalize.UpdateShaderTexture(texture, texture.Shader);
            }
        }

        // ==================================================================
        // ufbxi_update_anim_stack / display layer / find_bool3 (ufbx.c:23374-23417)
        // ==================================================================

        internal static void UpdateAnimStack(UfbxScene scene, UfbxAnimStack stack)
        {
            bool hasBegin = UfbxiProperties.TryFindProp(stack.Props, UfbxiStrings.LocalStart, out UfbxProp begin);
            bool hasEnd = UfbxiProperties.TryFindProp(stack.Props, UfbxiStrings.LocalStop, out UfbxProp end);
            if (!hasBegin || !hasEnd) {
                hasBegin = UfbxiProperties.TryFindProp(stack.Props, UfbxiStrings.ReferenceStart, out begin);
                hasEnd = UfbxiProperties.TryFindProp(stack.Props, UfbxiStrings.ReferenceStop, out end);
            }

            if (hasBegin && hasEnd) {
                stack.TimeBegin = (double)begin.ValueInt / (double)scene.Metadata.KtimeSecond;
                stack.TimeEnd = (double)end.ValueInt / (double)scene.Metadata.KtimeSecond;
            }

            stack.Anim.TimeBegin = stack.TimeBegin;
            stack.Anim.TimeEnd = stack.TimeEnd;
        }

        internal static void UpdateDisplayLayer(UfbxDisplayLayer layer)
        {
            layer.Visible = UfbxiProperties.FindInt(layer.Props, UfbxiStrings.Show, 1) != 0;
            layer.Frozen = UfbxiProperties.FindInt(layer.Props, UfbxiStrings.Freeze, 1) != 0;
            layer.UiColor = UfbxiProperties.FindVec3(layer.Props, UfbxiStrings.Color, (double)0.8f, (double)0.8f, (double)0.8f); // C: 0.8f (ufbx.c:23397)
        }

        // C: ufbxi_find_bool3 (ufbx.c:23400-23417). `name` is extended with 'X'/'Y'/'Z'.
        static void FindBool3(bool[] dst, UfbxProps props, string name, bool defaultValue)
        {
            if (dst == null) throw new InvalidOperationException("missing constraint bool3 array");
            long def = defaultValue ? 1 : 0;
            dst[0] = UfbxiSceneFinalize.FindIntLen(props, name + "X", def) != 0;
            dst[1] = UfbxiSceneFinalize.FindIntLen(props, name + "Y", def) != 0;
            dst[2] = UfbxiSceneFinalize.FindIntLen(props, name + "Z", def) != 0;
        }

        // ==================================================================
        // ufbxi_update_constraint (ufbx.c:23419-23491)
        // ==================================================================

        internal static void UpdateConstraint(UfbxConstraint constraint)
        {
            UfbxProps props = constraint.Props;
            UfbxConstraintType constraintType = constraint.Type;

            // C: the three bool[3] arrays are embedded fixed arrays; the port materializes
            // them on first use (the readers leave them null).
            if (constraint.ConstrainTranslation == null) constraint.ConstrainTranslation = new bool[3];
            if (constraint.ConstrainRotation == null) constraint.ConstrainRotation = new bool[3];
            if (constraint.ConstrainScale == null) constraint.ConstrainScale = new bool[3];

            constraint.TransformOffset = GetConstraintTransform(props);

            constraint.Weight = UfbxiProperties.FindReal(props, UfbxiStrings.Weight, 100.0) / 100.0;

            foreach (UfbxConstraintTarget target in constraint.Targets) {
                UfbxNode node = target.Node;

                double weightScale = 100.0;
                if (constraintType == UfbxConstraintType.SingleChainIk) {
                    // IK weights seem to be not scaled 100x?
                    weightScale = 1.0;
                }

                UfbxiConcatPart[] parts = new UfbxiConcatPart[2];
                parts[0] = new UfbxiConcatPart(node.Name);
                parts[1] = new UfbxiConcatPart(".Weight");
                UfbxProp prop = UfbxiSceneFinalize.FindPropConcat(props, parts, 2);
                target.Weight = (prop.Name != null ? prop.ValueReal : weightScale) / weightScale;

                if (constraintType == UfbxConstraintType.Parent) {
                    parts[1] = new UfbxiConcatPart(".Offset T");
                    prop = UfbxiSceneFinalize.FindPropConcat(props, parts, 2);
                    UfbxVec3 t = prop.Name != null ? prop.ValueVec3 : default;
                    parts[1] = new UfbxiConcatPart(".Offset R");
                    prop = UfbxiSceneFinalize.FindPropConcat(props, parts, 2);
                    UfbxVec3 r = prop.Name != null ? prop.ValueVec3 : default;
                    parts[1] = new UfbxiConcatPart(".Offset S");
                    prop = UfbxiSceneFinalize.FindPropConcat(props, parts, 2);
                    UfbxVec3 s = prop.Name != null ? prop.ValueVec3 : OneVec3;

                    target.Transform.Translation = t;
                    target.Transform.Rotation = UfbxQuat.EulerToQuat(r, UfbxRotationOrder.Xyz);
                    target.Transform.Scale = s;
                }
            }

            constraint.Active = UfbxiSceneFinalize.FindIntLen(props, "Active", 1) != 0;
            if (constraintType == UfbxConstraintType.Aim) {
                FindBool3(constraint.ConstrainRotation, props, "Affect", true);

                UfbxVec3 defaultAim = new UfbxVec3(1.0, 0.0, 0.0);
                UfbxVec3 defaultUp = new UfbxVec3(0.0, 1.0, 0.0);

                long upType = UfbxiSceneFinalize.FindIntLen(props, "WorldUpType", 0);
                if (upType >= 0 && upType < (long)UfbxConstraintAimUpType.None) {
                    constraint.AimUpType = (UfbxConstraintAimUpType)upType;
                }
                constraint.AimVector = UfbxiSceneFinalize.FindVec3Len(props, "AimVector", defaultAim);
                constraint.AimUpVector = UfbxiSceneFinalize.FindVec3Len(props, "UpVector", defaultUp);

            } else if (constraintType == UfbxConstraintType.Parent) {
                FindBool3(constraint.ConstrainTranslation, props, "AffectTranslation", true);
                FindBool3(constraint.ConstrainRotation, props, "AffectRotation", true);
                FindBool3(constraint.ConstrainScale, props, "AffectScale", false);
            } else if (constraintType == UfbxConstraintType.Position) {
                FindBool3(constraint.ConstrainTranslation, props, "Affect", true);
            } else if (constraintType == UfbxConstraintType.Rotation) {
                FindBool3(constraint.ConstrainRotation, props, "Affect", true);
            } else if (constraintType == UfbxConstraintType.Scale) {
                FindBool3(constraint.ConstrainScale, props, "Affect", true);
            } else if (constraintType == UfbxConstraintType.SingleChainIk) {
                constraint.ConstrainRotation[0] = true;
                constraint.ConstrainRotation[1] = true;
                constraint.ConstrainRotation[2] = true;
                constraint.IkPoleVector = UfbxiSceneFinalize.FindVec3Len(props, "PoleVectorType", default);
            }
        }

        // ==================================================================
        // ufbxi_update_anim (ufbx.c:23493-23498)
        // ==================================================================

        internal static void UpdateAnim(UfbxScene scene)
        {
            if (scene.AnimStacks != null && scene.AnimStacks.Length > 0) {
                scene.Anim = scene.AnimStacks[0].Anim;
            }
        }

        // ==================================================================
        // ufbxi_update_initial_clusters (ufbx.c:23526-23622)
        // ==================================================================

        internal static void UpdateInitialClusters(UfbxScene scene)
        {
            foreach (UfbxSkinCluster cluster in scene.SkinClusters) {
                cluster.GeometryToBone = cluster.MeshNodeToBone;
            }

            UfbxMirrorAxis mirrorAxis = scene.Metadata.MirrorAxis;
            double geometryScale = scene.Metadata.GeometryScale;

            // Space conversion for bind matrices
            {
                UfbxMatrix worldToUnits;
                double translationScale = 1.0;

                if (scene.Metadata.SpaceConversion == UfbxSpaceConversion.TransformRoot && scene.Metadata.MirrorAxis == UfbxMirrorAxis.None) {
                    worldToUnits = scene.RootNode.NodeToParent;
                } else {
                    UfbxTransform rootTransform;
                    rootTransform.Translation = default;
                    rootTransform.Rotation = scene.Metadata.RootRotation;
                    rootTransform.Scale.X = scene.Metadata.RootScale;
                    rootTransform.Scale.Y = scene.Metadata.RootScale;
                    rootTransform.Scale.Z = scene.Metadata.RootScale;
                    worldToUnits = UfbxMatrix.FromTransform(rootTransform);
                    translationScale = scene.Metadata.GeometryScale;
                }

                foreach (UfbxSkinCluster cluster in scene.SkinClusters) {
                    cluster.BindToWorld = UfbxMatrix.Mul(worldToUnits, cluster.BindToWorld);
                    cluster.BindToWorld.M03 *= translationScale;
                    cluster.BindToWorld.M13 *= translationScale;
                    cluster.BindToWorld.M23 *= translationScale;
                    UfbxiSceneOpts.MirrorMatrix(ref cluster.BindToWorld, mirrorAxis);
                }

                foreach (UfbxPose pose in scene.Poses) {
                    foreach (UfbxBonePose bonePose in pose.BonePoses) {
                        bonePose.BoneToWorld = UfbxMatrix.Mul(worldToUnits, bonePose.BoneToWorld);
                        bonePose.BoneToWorld.M03 *= translationScale;
                        bonePose.BoneToWorld.M13 *= translationScale;
                        bonePose.BoneToWorld.M23 *= translationScale;
                        UfbxiSceneOpts.MirrorMatrix(ref bonePose.BoneToWorld, mirrorAxis);
                    }
                }
            }

            // Patch initial `mesh_node_to_bone`
            foreach (UfbxSkinCluster cluster in scene.SkinClusters) {
                UfbxSkinDeformer skin = (UfbxSkinDeformer)UfbxiSceneBuild.FetchSrcElement(cluster, false, null, UfbxElementType.SkinDeformer);
                if (skin == null) continue;

                UfbxNode node = (UfbxNode)UfbxiSceneBuild.FetchSrcElement(skin, false, null, UfbxElementType.Node);
                if (node == null) {
                    UfbxMesh mesh = (UfbxMesh)UfbxiSceneBuild.FetchSrcElement(skin, false, null, UfbxElementType.Mesh);
                    if (mesh != null && mesh.Instances != null && mesh.Instances.Length > 0) {
                        node = mesh.Instances[0];
                    }
                }
                if (node == null) continue;

                // Normalize to the non-helper node
                if (node.IsGeometryTransformHelper) {
                    node = node.Parent;
                }

                if (MatrixAllZero(cluster.MeshNodeToBone)) {
                    // If `mesh_node_to_bone` is not explicitly specified compute it from bind pose.
                    UfbxMatrix worldToBind = UfbxMatrix.Invert(cluster.BindToWorld);
                    cluster.MeshNodeToBone = UfbxMatrix.Mul(worldToBind, node.NodeToWorld);
                } else {
                    // If `mesh_node_to_bone` is explicit, we may need to modify it for space conversion.
                    UfbxiSceneOpts.MirrorMatrix(ref cluster.MeshNodeToBone, mirrorAxis);
                    if (geometryScale != 1.0) {
                        cluster.MeshNodeToBone.M03 *= geometryScale;
                        cluster.MeshNodeToBone.M13 *= geometryScale;
                        cluster.MeshNodeToBone.M23 *= geometryScale;
                    }
                }

                // HACK: Account for geometry transforms by looking at the transform of the
                // helper node if one is present.
                if (node.GeometryTransformHelper != null) {
                    UfbxNode geoNode = node.GeometryTransformHelper;
                    cluster.GeometryToBone = UfbxMatrix.Mul(cluster.MeshNodeToBone, geoNode.NodeToParent);
                } else if (node.HasGeometryTransform) {
                    cluster.GeometryToBone = UfbxMatrix.Mul(cluster.MeshNodeToBone, node.GeometryToNode);
                } else {
                    cluster.GeometryToBone = cluster.MeshNodeToBone;
                }
            }
        }

        // ==================================================================
        // ufbxi_find_axis / time mode table (ufbx.c:23624-23656)
        // ==================================================================

        // C: ufbxi_find_axis (ufbx.c:23624-23635).
        static UfbxCoordinateAxis FindAxis(UfbxProps props, string axisName, string signName)
        {
            long axis = UfbxiProperties.FindInt(props, axisName, 3);
            long sign = UfbxiProperties.FindInt(props, signName, 2);

            switch (axis) {
            case 0: return sign > 0 ? UfbxCoordinateAxis.PositiveX : UfbxCoordinateAxis.NegativeX;
            case 1: return sign > 0 ? UfbxCoordinateAxis.PositiveY : UfbxCoordinateAxis.NegativeY;
            case 2: return sign > 0 ? UfbxCoordinateAxis.PositiveZ : UfbxCoordinateAxis.NegativeZ;
            default: return UfbxCoordinateAxis.Unknown;
            }
        }

        // C: ufbxi_time_mode_fps[] (ufbx.c:23637-23656).
        // The C table is `static const ufbx_real[]` but its literals are *float* constants, so the
        // inexact ones (29.97f, 23.976f, 59.94f) hold the widened float32 values, not the decimal
        // doubles. Keep the `f` suffixes.
        static readonly double[] TimeModeFps = new double[] {
            30.0,   // UFBX_TIME_MODE_DEFAULT
            120.0,  // UFBX_TIME_MODE_120_FPS
            100.0,  // UFBX_TIME_MODE_100_FPS
            60.0,   // UFBX_TIME_MODE_60_FPS
            50.0,   // UFBX_TIME_MODE_50_FPS
            48.0,   // UFBX_TIME_MODE_48_FPS
            30.0,   // UFBX_TIME_MODE_30_FPS
            30.0,   // UFBX_TIME_MODE_30_FPS_DROP
            (double)29.97f,  // UFBX_TIME_MODE_NTSC_DROP_FRAME
            (double)29.97f,  // UFBX_TIME_MODE_NTSC_FULL_FRAME
            25.0,   // UFBX_TIME_MODE_PAL
            24.0,   // UFBX_TIME_MODE_24_FPS
            1000.0, // UFBX_TIME_MODE_1000_FPS
            (double)23.976f, // UFBX_TIME_MODE_FILM_FULL_FRAME
            24.0,   // UFBX_TIME_MODE_CUSTOM
            96.0,   // UFBX_TIME_MODE_96_FPS
            72.0,   // UFBX_TIME_MODE_72_FPS
            (double)59.94f,  // UFBX_TIME_MODE_59_94_FPS
        };

        // ==================================================================
        // ufbxi_axis_matrix (ufbx.c:23659-23677)
        // ==================================================================

        // C: ufbxi_axis_matrix -- returns whether a non-identity matrix was needed.
        // `mat->cols[col].v[row]` == `M{row}{col}` in the port's scalar naming (see
        // UfbxMatrix in Math/Types.cs).
        internal static bool AxisMatrix(ref UfbxMatrix mat, UfbxCoordinateAxes src, UfbxCoordinateAxes dst)
        {
            uint srcX = (uint)src.Right;
            uint dstX = (uint)dst.Right;
            uint srcY = (uint)src.Up;
            uint dstY = (uint)dst.Up;
            uint srcZ = (uint)src.Front;
            uint dstZ = (uint)dst.Front;

            if (srcX == dstX && srcY == dstY && srcZ == dstZ) return false;

            // Remap axes (axis enum divided by 2) potentially flipping if the signs (enum parity) doesn't match
            mat = default;
            SetColRow(ref mat, srcX >> 1, dstX >> 1, ((srcX ^ dstX) & 1) == 0 ? 1.0 : -1.0);
            SetColRow(ref mat, srcY >> 1, dstY >> 1, ((srcY ^ dstY) & 1) == 0 ? 1.0 : -1.0);
            SetColRow(ref mat, srcZ >> 1, dstZ >> 1, ((srcZ ^ dstZ) & 1) == 0 ? 1.0 : -1.0);

            return true;
        }

        // C: `m->cols[col].v[row] = value` -- element (col, row) maps to M{row}{col}
        // (same layout rule as SceneOpts.MirrorElement).
        static void SetColRow(ref UfbxMatrix m, uint col, uint row, double value)
        {
            switch (col * 3 + row) {
            case 0: m.M00 = value; break;   // cols[0].v[0]
            case 1: m.M10 = value; break;   // cols[0].v[1]
            case 2: m.M20 = value; break;   // cols[0].v[2]
            case 3: m.M01 = value; break;   // cols[1].v[0]
            case 4: m.M11 = value; break;   // cols[1].v[1]
            case 5: m.M21 = value; break;   // cols[1].v[2]
            case 6: m.M02 = value; break;   // cols[2].v[0]
            case 7: m.M12 = value; break;   // cols[2].v[1]
            default: m.M22 = value; break;  // cols[2].v[2]
            }
        }

        // ==================================================================
        // ufbxi_update_adjust_transforms (ufbx.c:23679-23807)
        // ==================================================================

        internal static void UpdateAdjustTransforms(UfbxiContext uc, UfbxScene scene)
        {
            UfbxTransform rootTransform = UfbxTransform.Identity;
            if (!MatrixAllZero(uc.AxisMatrix)) {
                rootTransform = UfbxMatrix.ToTransform(uc.AxisMatrix);
            }
            rootTransform.Scale.X *= uc.UnitScale;
            rootTransform.Scale.Y *= uc.UnitScale;
            rootTransform.Scale.Z *= uc.UnitScale;

            UfbxSpaceConversion conversion = uc.Opts.SpaceConversion;

            UfbxQuat lightPostRotation = UfbxQuat.Identity;
            UfbxQuat cameraPostRotation = UfbxQuat.Identity;
            UfbxVec3 lightDirection = new UfbxVec3(0.0, -1.0, 0.0);
            bool hasLightTransform = false;
            bool hasCameraTransform = false;

            if (UfbxCoordinateAxes.IsValid(uc.Opts.TargetLightAxes)) {
                UfbxCoordinateAxes lightAxes = new UfbxCoordinateAxes(
                    UfbxCoordinateAxis.PositiveX,
                    UfbxCoordinateAxis.NegativeZ,
                    UfbxCoordinateAxis.PositiveY);
                UfbxMatrix mat = default;
                if (AxisMatrix(ref mat, uc.Opts.TargetLightAxes, lightAxes)) {
                    lightPostRotation = UfbxMatrix.ToTransform(mat).Rotation;

                    UfbxMatrix inv = UfbxMatrix.Invert(mat);
                    lightDirection = UfbxMatrix.TransformDirection(inv, lightDirection);
                    hasLightTransform = true;
                }
            }

            if (UfbxCoordinateAxes.IsValid(uc.Opts.TargetCameraAxes)) {
                UfbxCoordinateAxes cameraAxes = new UfbxCoordinateAxes(
                    UfbxCoordinateAxis.PositiveZ,
                    UfbxCoordinateAxis.PositiveY,
                    UfbxCoordinateAxis.NegativeX);
                UfbxMatrix mat = default;
                if (AxisMatrix(ref mat, uc.Opts.TargetCameraAxes, cameraAxes)) {
                    cameraPostRotation = UfbxMatrix.ToTransform(mat).Rotation;
                    hasCameraTransform = true;
                }
            }

            foreach (UfbxLight light in scene.Lights) {
                light.LocalDirection.X = 0.0;
                light.LocalDirection.Y = -1.0;
                light.LocalDirection.Z = 0.0;
            }

            scene.Metadata.SpaceConversion = conversion;
            scene.Metadata.GeometryTransformHandling = uc.Opts.GeometryTransformHandling;
            scene.Metadata.InheritModeHandling = uc.Opts.InheritModeHandling;
            scene.Metadata.PivotHandling = uc.Opts.PivotHandling;
            scene.Metadata.HandednessConversionAxis = uc.Opts.HandednessConversionAxis;

            double rootScale = UfbxVec3.Min3(rootTransform.Scale);
            if (conversion == UfbxSpaceConversion.ModifyGeometry) {
                scene.Metadata.GeometryScale = rootScale;
                scene.Metadata.RootScale = 1.0;
            } else {
                scene.Metadata.GeometryScale = 1.0;
                scene.Metadata.RootScale = rootScale;
            }
            scene.Metadata.RootRotation = rootTransform.Rotation;

            foreach (UfbxNode node in scene.Nodes) {
                node.AdjustPostRotation = UfbxQuat.Identity;
                node.AdjustPreRotation = UfbxQuat.Identity;
                node.AdjustPreScale = 1.0;
                node.AdjustPostScale = 1.0;
                node.AdjustTranslationScale = 1.0;

                if (conversion == UfbxSpaceConversion.AdjustTransforms) {
                    if (node.NodeDepth <= 1 && !node.IsRoot) {
                        node.AdjustPreRotation = rootTransform.Rotation;
                        node.AdjustPreScale = rootScale;
                        node.HasAdjustTransform = true;
                        node.HasRootAdjustTransform = true;
                    }
                } else if (conversion == UfbxSpaceConversion.ModifyGeometry) {
                    if (!node.IsRoot) {
                        if (node.NodeDepth <= 1) {
                            node.AdjustPreRotation = rootTransform.Rotation;
                        }
                        node.AdjustTranslationScale = rootScale;
                        node.HasAdjustTransform = true;
                    }
                }

                if (node.Parent != null) {
                    // We are not inheriting local scale, so propagate root scale manually and
                    // apply scale compensation if necessary.
                    UfbxNode parent = node.Parent;
                    if (parent.HasRootAdjustTransform && node.InheritMode == UfbxInheritMode.IgnoreParentScale) {
                        node.AdjustPostScale *= rootScale;
                        node.HasAdjustTransform = true;
                        node.HasRootAdjustTransform = true;
                    }
                    if (parent.IsScaleCompensateParent && node.OriginalInheritMode == UfbxInheritMode.IgnoreParentScale) {
                        UfbxVec3 scale = UfbxiProperties.FindVec3(parent.Props, UfbxiStrings.Lcl_Scaling, 1.0, 1.0, 1.0);
                        double size = scale.X;
                        if (UfbxMath.Abs(scale.Y - 1.0) < UfbxMath.Abs(size - 1.0)) size = scale.Y;
                        if (UfbxMath.Abs(scale.Z - 1.0) < UfbxMath.Abs(size - 1.0)) size = scale.Z;
                        node.AdjustPostScale *= 1.0 / size;
                        node.HasAdjustTransform = true;
                    }
                }

                if (node.AllAttribs != null && node.AllAttribs.Length == 1) {
                    if (hasLightTransform && node.Light != null) {
                        node.AdjustPostRotation = lightPostRotation;
                        node.Light.LocalDirection = lightDirection;
                        node.HasAdjustTransform = true;
                    }
                    if (hasCameraTransform && node.Camera != null) {
                        node.AdjustPostRotation = cameraPostRotation;
                        node.Camera.ProjectionAxes = uc.Opts.TargetCameraAxes;
                        node.HasAdjustTransform = true;
                    }
                }
            }
        }

        // ==================================================================
        // ufbxi_update_scene (ufbx.c:23809-23870)
        // ==================================================================

        internal static void UpdateScene(UfbxScene scene, bool initial, UfbxTransformOverride[] transformOverrides)
        {
            foreach (UfbxNode node in scene.Nodes) {
                UpdateNode(node, transformOverrides);
            }

            foreach (UfbxLight light in scene.Lights) {
                UpdateLight(light);
            }

            foreach (UfbxCamera camera in scene.Cameras) {
                UpdateCamera(scene, camera);
            }

            foreach (UfbxBone bone in scene.Bones) {
                UpdateBone(scene, bone);
            }

            foreach (UfbxLineCurve line in scene.LineCurves) {
                UpdateLineCurve(line);
            }

            if (initial) {
                UpdateInitialClusters(scene);

                foreach (UfbxPose pose in scene.Poses) {
                    UpdatePose(pose);
                }
            }

            foreach (UfbxSkinCluster cluster in scene.SkinClusters) {
                UpdateSkinCluster(cluster);
            }

            foreach (UfbxBlendChannel channel in scene.BlendChannels) {
                UpdateBlendChannel(channel);
            }

            foreach (UfbxTexture texture in scene.Textures) {
                UpdateTexture(texture);
            }

            UfbxiSceneFinalize.PropagateMainTextures(scene);

            foreach (UfbxMaterial material in scene.Materials) {
                UpdateMaterial(scene, material);
            }

            foreach (UfbxAnimStack stack in scene.AnimStacks) {
                UpdateAnimStack(scene, stack);
            }

            foreach (UfbxDisplayLayer layer in scene.DisplayLayers) {
                UpdateDisplayLayer(layer);
            }

            foreach (UfbxConstraint constraint in scene.Constraints) {
                UpdateConstraint(constraint);
            }

            UpdateAnim(scene);
        }

        // ==================================================================
        // ufbxi_update_scene_metadata / settings (ufbx.c:23872-23881, 23906-23934)
        // ==================================================================

        internal static void UpdateSceneMetadata(UfbxMetadata metadata)
        {
            UfbxProps props = metadata.SceneProps;
            metadata.OriginalApplication.Vendor = UfbxiSceneFinalize.FindStringLen(props, "Original|ApplicationVendor", string.Empty);
            metadata.OriginalApplication.Name = UfbxiSceneFinalize.FindStringLen(props, "Original|ApplicationName", string.Empty);
            metadata.OriginalApplication.Version = UfbxiSceneFinalize.FindStringLen(props, "Original|ApplicationVersion", string.Empty);
            metadata.LatestApplication.Vendor = UfbxiSceneFinalize.FindStringLen(props, "LastSaved|ApplicationVendor", string.Empty);
            metadata.LatestApplication.Name = UfbxiSceneFinalize.FindStringLen(props, "LastSaved|ApplicationName", string.Empty);
            metadata.LatestApplication.Version = UfbxiSceneFinalize.FindStringLen(props, "LastSaved|ApplicationVersion", string.Empty);
        }

        // C: ufbxi_pow10_targets (ufbx.c:23883-23890) -- local copy of the S4a table.
        static readonly double[] Pow10Targets = new double[] {
            0.0,
            1e-8, 1e-7, 1e-6, 1e-5,
            1e-4, 1e-3, 1e-2, 1e-1,
            1e+0, 1e+1, 1e+2, 1e+3,
            1e+4, 1e+5, 1e+6, 1e+7,
            1e+8, 1e+9,
        };

        internal static void UpdateSceneSettings(UfbxSceneSettings settings)
        {
            double unitScaleFactor = UfbxiProperties.FindReal(settings.Props, UfbxiStrings.UnitScaleFactor, 1.0);
            double originalUnitScaleFactor = UfbxiProperties.FindReal(settings.Props, UfbxiStrings.OriginalUnitScaleFactor, unitScaleFactor);

            settings.Axes.Up = FindAxis(settings.Props, UfbxiStrings.UpAxis, UfbxiStrings.UpAxisSign);
            settings.Axes.Front = FindAxis(settings.Props, UfbxiStrings.FrontAxis, UfbxiStrings.FrontAxisSign);
            settings.Axes.Right = FindAxis(settings.Props, UfbxiStrings.CoordAxis, UfbxiStrings.CoordAxisSign);
            settings.UnitMeters = UfbxiSceneOpts.RoundIfNear(Pow10Targets, Pow10Targets.Length, unitScaleFactor * 0.01);
            settings.OriginalUnitMeters = UfbxiSceneOpts.RoundIfNear(Pow10Targets, Pow10Targets.Length, originalUnitScaleFactor * 0.01);
            settings.FramesPerSecond = UfbxiProperties.FindReal(settings.Props, UfbxiStrings.CustomFrameRate, 24.0);
            settings.AmbientColor = UfbxiProperties.FindVec3(settings.Props, UfbxiStrings.AmbientColor, 0.0, 0.0, 0.0);
            settings.OriginalAxisUp = FindAxis(settings.Props, UfbxiStrings.OriginalUpAxis, UfbxiStrings.OriginalUpAxisSign);

            if (UfbxiSceneFinalize.FindPropLen(settings.Props, UfbxiStrings.DefaultCamera, out UfbxProp defaultCamera)) {
                settings.DefaultCamera = defaultCamera.ValueStr;
            } else {
                settings.DefaultCamera = string.Empty;
            }

            settings.TimeMode = (UfbxTimeMode)UfbxiProperties.FindEnum(settings.Props, UfbxiStrings.TimeMode, (long)UfbxTimeMode.Fps24, (long)UfbxTimeMode.Fps59_94);
            settings.TimeProtocol = (UfbxTimeProtocol)UfbxiProperties.FindEnum(settings.Props, UfbxiStrings.TimeProtocol, (long)UfbxTimeProtocol.Default, (long)UfbxTimeProtocol.Default);
            settings.SnapMode = (UfbxSnapMode)UfbxiProperties.FindEnum(settings.Props, UfbxiStrings.SnapOnFrameMode, (long)UfbxSnapMode.None, (long)UfbxSnapMode.SnapAndPlay);

            if (settings.TimeMode != UfbxTimeMode.Custom) {
                settings.FramesPerSecond = TimeModeFps[(int)settings.TimeMode];
            }
        }

        // ==================================================================
        // ufbxi_push_anim (ufbx.c:21632-21642)
        // ==================================================================

        static UfbxAnim PushAnim(UfbxAnimLayer[] layers)
        {
            UfbxAnim anim = new UfbxAnim();
            anim.Layers = layers;
            return anim;
        }

        // ==================================================================
        // ufbxi_finalize_scene (ufbx.c:21644-22627)
        // ==================================================================

        internal static void FinalizeScene(UfbxiContext uc)
        {
            UfbxScene scene = uc.Scene;
            int numElements = (int)uc.NumElements;

            // C: uc->scene.elements -- element_id order; the port's TmpElementPtrs is the same
            // registry (the scale-helper offset remap at ufbx.c:21662-21669 is a no-op here:
            // the port's elements reference each other directly).
            scene.Elements = new UfbxElement[numElements];
            for (int i = 0; i < numElements; i++) scene.Elements[i] = uc.TmpElementPtrs[i];

            // C: uc->tmp_element_flag = push_zero(num_elements)
            uc.EnsureTmpElementFlag();

            scene.Metadata.OriginalFilePath = UfbxiSceneFinalize.FindStringLen(scene.Metadata.SceneProps, "DocumentUrl", string.Empty);
            scene.Metadata.RawOriginalFilePath = UfbxiSceneFinalize.FindBlobLen(scene.Metadata.SceneProps, "DocumentUrl", null);

            // Resolve and add the connections to elements
            UfbxiSceneBuild.ResolveConnections(uc);
            UfbxiSceneBuild.AddConnectionsToElements(uc);
            UfbxiSceneBuild.LinearizeNodes(uc);

            // elements_by_type (ufbx.c:21689-21705): typed lists in creation order (== the
            // per-type typed_id order; NOTE: nodes' typed_id was rewritten to sorted order by
            // linearize_nodes, the typed list itself stays in creation order like C's offsets).
            // PORT LIMITATION: C keeps `scene.nodes` (sorted by ufbxi_linearize_nodes) and
            // `elements_by_type[UFBX_ELEMENT_NODE]` (creation order) as two lists; the port's
            // `UfbxScene.Nodes` backs both, so type NODE is skipped here to preserve the sorted
            // array every consumer iterates (nothing reads elements_by_type[NODE] in ufbx.c).
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                if (type == (int)UfbxElementType.Node) continue;
                List<UfbxElement> typed = new List<UfbxElement>();
                for (int i = 0; i < numElements; i++) {
                    if ((int)scene.Elements[i].Type == type) typed.Add(scene.Elements[i]);
                }
                scene.SetElementsByType(type, MaterializeTypedList(type, typed));
            }

            // Create named elements (ufbx.c:21707-21723)
            UfbxNameElement[] nameElements = new UfbxNameElement[numElements];
            for (int i = 0; i < numElements; i++) {
                UfbxElement elem = scene.Elements[i];
                UfbxNameElement nameElem = default;
                nameElem.Name = elem.Name;
                nameElem.Type = elem.Type;
                nameElem.InternalKey = UfbxiProperties.GetNameKey(elem.Name, elem.Name.Length);
                nameElem.Element = elem;
                nameElements[i] = nameElem;
            }
            UfbxiSceneBuild.SortNameElements(nameElements, numElements);
            scene.ElementsByName = nameElements;

            // Setup node children arrays and attribute pointers/lists (ufbx.c:21725-21788)
            {
                List<UfbxNode>[] children = new List<UfbxNode>[scene.Nodes.Length];
                foreach (UfbxNode node in scene.Nodes) {
                    UfbxNode parent = node.Parent;
                    if (parent != null) {
                        int pId = (int)parent.TypedId;
                        if (children[pId] == null) children[pId] = new List<UfbxNode>();
                        children[pId].Add(node);

                        if (node.IsGeometryTransformHelper) {
                            parent.GeometryTransformHelper = node;
                        }

                        // Force top-level nodes to have `UFBX_INHERIT_MODE_NORMAL` to make unit scaling work.
                        if (parent.IsRoot && uc.Opts.SpaceConversion == UfbxSpaceConversion.TransformRoot && uc.Opts.InheritModeHandling == UfbxInheritModeHandling.Preserve) {
                            node.OriginalInheritMode = UfbxInheritMode.Normal;
                            node.InheritMode = UfbxInheritMode.Normal;
                        }

                        // RrSs nodes inherit scale from their parent, Rrs ignore the scale of
                        // their _immediate_ parent, potentially multiple if chained.
                        if (node.OriginalInheritMode == UfbxInheritMode.ComponentwiseScale) {
                            node.InheritScaleNode = parent;
                        } else if (node.OriginalInheritMode == UfbxInheritMode.IgnoreParentScale) {
                            node.InheritScaleNode = parent.InheritScaleNode;
                        }
                    }

                    UfbxConnectionList conns = UfbxiSceneBuild.FindDstConnections(node, null);

                    List<UfbxElement> attribs = new List<UfbxElement>();
                    int attribCount = 0;
                    for (int i = conns.Offset; i < conns.End; i++) {
                        UfbxConnection conn = conns.Data[i];
                        UfbxElement elem = conn.Src;
                        UfbxElementType type = elem.Type;
                        if (!(type >= UfbxElementType.FirstAttrib && type <= UfbxElementType.LastAttrib)) continue;

                        int index = attribCount++;
                        if (index == 0) {
                            node.Attrib = elem;
                            node.AttribType = type;
                        } else {
                            if (index == 1) {
                                attribs.Add(node.Attrib);
                            }
                            attribs.Add(elem);
                        }

                        switch (type) {
                        case UfbxElementType.Mesh: node.Mesh = (UfbxMesh)elem; break;
                        case UfbxElementType.Light: node.Light = (UfbxLight)elem; break;
                        case UfbxElementType.Camera: node.Camera = (UfbxCamera)elem; break;
                        case UfbxElementType.Bone: node.Bone = (UfbxBone)elem; break;
                        }
                    }

                    if (attribCount > 1) {
                        node.AllAttribs = attribs.ToArray();
                    } else if (attribCount == 1) {
                        node.AllAttribs = new UfbxElement[] { node.Attrib };
                    } else {
                        node.AllAttribs = null;
                    }

                    node.Materials = CastArray<UfbxMaterial>(UfbxiSceneBuild.FetchDstElements(uc, node, false, false, null, UfbxElementType.Material));
                }

                for (int i = 0; i < scene.Nodes.Length; i++) {
                    if (children[i] != null) {
                        scene.Nodes[i].Children = children[i].ToArray();
                    }
                }
            }

            // Resolve bind pose bones that don't use the normal connection system
            // (ufbx.c:21790-21827)
            foreach (UfbxPose pose in scene.Poses) {
                UfbxiPoseExtra extra = uc.GetElementExtra<UfbxiPoseExtra>(pose.ElementId);
                UfbxiTmpBonePose[] tmpPoses = extra != null ? extra.BonePoses : null;
                int numBones = tmpPoses != null ? tmpPoses.Length : 0;

                List<UfbxBonePose> bonePoses = new List<UfbxBonePose>(numBones);

                // Filter only found bones
                for (int i = 0; i < numBones; i++) {
                    UfbxElement elem = UfbxiSceneBuild.FindElementByFbxId(uc, tmpPoses[i].BoneFbxId);
                    if (elem == null || elem.Type != UfbxElementType.Node) continue;

                    UfbxNode node = (UfbxNode)elem;
                    UfbxBonePose bone = new UfbxBonePose();
                    bone.BoneNode = node;
                    bone.BoneToWorld = tmpPoses[i].BoneToWorld;
                    bonePoses.Add(bone);

                    if (pose.IsBindPose) {
                        if (node.BindPose == null) {
                            node.BindPose = pose;
                        }

                        UfbxConnectionList nodeConns = UfbxiSceneBuild.FindSrcConnections(node, null);
                        for (int c = nodeConns.Offset; c < nodeConns.End; c++) {
                            UfbxConnection conn = nodeConns.Data[c];
                            if (conn.Dst.Type != UfbxElementType.SkinCluster) continue;
                            UfbxSkinCluster cluster = (UfbxSkinCluster)conn.Dst;
                            if (MatrixAllZero(cluster.BindToWorld)) {
                                cluster.BindToWorld = bone.BoneToWorld;
                            }
                        }
                    }
                }
                pose.BonePoses = bonePoses.ToArray();
                UfbxiSceneBuild.SortBonePoses(pose);
            }

            // Fetch pointers that may break elements

            // Setup node attribute instances (ufbx.c:21831-21837)
            for (int type = (int)UfbxElementType.FirstAttrib; type <= (int)UfbxElementType.LastAttrib; type++) {
                foreach (UfbxElement elem in scene.ElementsByType(type)) {
                    elem.Instances = CastArray<UfbxNode>(UfbxiSceneBuild.FetchSrcElements(uc, elem, false, true, null, UfbxElementType.Node));
                }
            }

            bool searchNode = uc.Version < 7000;

            foreach (UfbxSkinCluster cluster in scene.SkinClusters) {
                cluster.BoneNode = (UfbxNode)UfbxiSceneBuild.FetchDstElement(cluster, false, null, UfbxElementType.Node);
            }

            foreach (UfbxSkinDeformer skin in scene.SkinDeformers) {
                skin.Clusters = CastArray<UfbxSkinCluster>(UfbxiSceneBuild.FetchDstElements(uc, skin, false, true, null, UfbxElementType.SkinCluster));

                // Remove clusters without a valid `bone`
                if (!uc.Opts.ConnectBrokenElements) {
                    int numBroken = 0;
                    for (int i = 0; i < skin.Clusters.Length; i++) {
                        if (skin.Clusters[i].BoneNode == null) {
                            numBroken++;
                        } else if (numBroken > 0) {
                            skin.Clusters[i - numBroken] = skin.Clusters[i];
                        }
                    }
                    if (numBroken > 0) {
                        Array.Resize(ref skin.Clusters, skin.Clusters.Length - numBroken);
                    }
                }

                int totalWeights = 0;
                foreach (UfbxSkinCluster cluster in skin.Clusters) {
                    totalWeights += cluster.NumWeights; // C: overflow checked with SIZE_MAX
                }

                int numVertices = 0;

                // Iterate through meshes so we can pad the vertices to the largest one
                {
                    UfbxConnectionList conns = UfbxiSceneBuild.FindSrcConnections(skin, null);
                    for (int i = conns.Offset; i < conns.End; i++) {
                        UfbxConnection conn = conns.Data[i];
                        UfbxMesh mesh = null;
                        if (conn.DstProp.Length > 0) continue;
                        if (conn.Dst.Type == UfbxElementType.Mesh) {
                            mesh = (UfbxMesh)conn.Dst;
                        } else if (conn.Dst.Type == UfbxElementType.Node) {
                            UfbxNode node = (UfbxNode)conn.Dst;
                            if (node.GeometryTransformHelper != null) node = node.GeometryTransformHelper;
                            mesh = node.Mesh;
                        }
                        if (mesh == null) continue;
                        if (numVertices < mesh.NumVertices) numVertices = mesh.NumVertices;
                    }
                }

                if (!uc.Opts.SkipSkinVertices) {
                    skin.Vertices = new UfbxSkinVertex[numVertices];
                    skin.Weights = new UfbxSkinWeight[totalWeights];

                    bool retainAll = !uc.Opts.CleanSkinWeights;

                    // Count the number of weights per vertex
                    foreach (UfbxSkinCluster cluster in skin.Clusters) {
                        for (int i = 0; i < cluster.NumWeights; i++) {
                            uint vertex = cluster.Vertices[i];
                            if (vertex < (uint)numVertices && (retainAll || cluster.Weights[i] > 0.0)) {
                                skin.Vertices[vertex].NumWeights++;
                            }
                        }
                    }

                    double defaultDq = skin.SkinningMethod == UfbxSkinningMethod.DualQuaternion ? 1.0 : 0.0;

                    // Prefix sum to assign the vertex weight offsets and set up default DQ values
                    uint offset = 0;
                    uint maxWeights = 0;
                    for (int i = 0; i < numVertices; i++) {
                        skin.Vertices[i].WeightBegin = offset;
                        skin.Vertices[i].DqWeight = defaultDq;
                        uint numWeights = skin.Vertices[i].NumWeights;
                        offset += numWeights;
                        skin.Vertices[i].NumWeights = 0;

                        if (numWeights > maxWeights) maxWeights = numWeights;
                    }
                    skin.MaxWeightsPerVertex = (int)maxWeights;

                    // Copy the DQ weights to vertices
                    for (int i = 0; i < skin.NumDqWeights; i++) {
                        uint vertex = skin.DqVertices[i];
                        if (vertex < (uint)numVertices) {
                            skin.Vertices[vertex].DqWeight = skin.DqWeights[i];
                        }
                    }

                    // Copy the weights to vertices
                    uint clusterIndex = 0;
                    foreach (UfbxSkinCluster cluster in skin.Clusters) {
                        for (int i = 0; i < cluster.NumWeights; i++) {
                            uint vertex = cluster.Vertices[i];
                            if (vertex < (uint)numVertices && (retainAll || cluster.Weights[i] > 0.0)) {
                                uint localIndex = skin.Vertices[vertex].NumWeights++;
                                int index = (int)(skin.Vertices[vertex].WeightBegin + localIndex);
                                skin.Weights[index].ClusterIndex = (uint)clusterIndex;
                                skin.Weights[index].Weight = cluster.Weights[i];
                            }
                        }
                        clusterIndex++;
                    }

                    // Sort the vertex weights by descending weight value
                    UfbxiSceneBuild.SortSkinWeights(skin);
                }
            }

            foreach (UfbxBlendDeformer blend in scene.BlendDeformers) {
                blend.Channels = CastArray<UfbxBlendChannel>(UfbxiSceneBuild.FetchDstElements(uc, blend, false, true, null, UfbxElementType.BlendChannel));
            }

            foreach (UfbxCacheDeformer deformer in scene.CacheDeformers) {
                deformer.Channel = UfbxiSceneFinalize.FindStringLen(deformer.Props, "ChannelName", string.Empty);
                deformer.File = (UfbxCacheFile)UfbxiSceneBuild.FetchDstElement(deformer, false, null, UfbxElementType.CacheFile);
            }

            foreach (UfbxCacheFile cache in scene.CacheFiles) {
                cache.AbsoluteFilename = UfbxiSceneFinalize.FindStringLen(cache.Props, "CacheAbsoluteFileName", string.Empty);
                cache.RelativeFilename = UfbxiSceneFinalize.FindStringLen(cache.Props, "CacheFileName", string.Empty);

                cache.RawAbsoluteFilename = UfbxiSceneFinalize.FindBlobLen(cache.Props, "CacheAbsoluteFileName", null);
                cache.RawRelativeFilename = UfbxiSceneFinalize.FindBlobLen(cache.Props, "CacheFileName", null);

                long type = UfbxiSceneFinalize.FindIntLen(cache.Props, "CacheFileType", 0);
                if (type >= 0 && type <= (long)UfbxCacheFileFormat.Mc) {
                    cache.Format = (UfbxCacheFileFormat)type;
                }

                {
                    UfbxiSceneFiles.UfbxiStrblob filename = default, absolute = default, relative = default;
                    filename.Str = cache.Filename;
                    absolute.Str = cache.AbsoluteFilename;
                    relative.Str = cache.RelativeFilename;
                    UfbxiSceneFinalize.ResolveFilenames(uc, ref filename, ref absolute, ref relative, false);
                    cache.Filename = filename.Str;
                    cache.AbsoluteFilename = absolute.Str;
                    cache.RelativeFilename = relative.Str;
                }
                {
                    UfbxiSceneFiles.UfbxiStrblob filename = default, absolute = default, relative = default;
                    filename.Blob = cache.RawFilename;
                    absolute.Blob = cache.RawAbsoluteFilename;
                    relative.Blob = cache.RawRelativeFilename;
                    UfbxiSceneFinalize.ResolveFilenames(uc, ref filename, ref absolute, ref relative, true);
                    cache.RawFilename = filename.Blob;
                    cache.RawAbsoluteFilename = absolute.Blob;
                    cache.RawRelativeFilename = relative.Blob;
                }
            }

            // ufbxi_assert(tmp_full_weights.num_items == blend_channels.count) -- compiled out.
            {
                int chanIx = 0;
                foreach (UfbxBlendChannel channel in scene.BlendChannels) {
                    channel.Keyframes = UfbxiSceneBuild.FetchBlendKeyframes(uc, channel);
                    double[] fullWeights = chanIx < uc.TmpFullWeights.Count ? uc.TmpFullWeights[chanIx] : null;
                    int fullWeightsCount = fullWeights != null ? fullWeights.Length : 0;

                    for (int i = 0; i < channel.Keyframes.Length; i++) {
                        UfbxBlendKeyframe key = channel.Keyframes[i];
                        key.TargetWeight = 1.0;
                        if (i < fullWeightsCount) {
                            if (!uc.BlenderFullWeights) {
                                key.TargetWeight = fullWeights[i] / 100.0;
                            } else if (fullWeightsCount == key.Shape.NumOffsets) {
                                if (i == 0) {
                                    // Duplicate `index_data` for modification if we retain DOM
                                    if (uc.Opts.RetainDom) {
                                        fullWeights = (double[])fullWeights.Clone();
                                    }
                                    for (int k = 0; k < fullWeightsCount; k++) {
                                        fullWeights[k] /= 100.0;
                                    }
                                }
                                key.Shape.OffsetWeights = fullWeights;
                            }
                        }
                    }

                    UfbxiSceneBuild.SortBlendKeyframes(channel.Keyframes, channel.Keyframes.Length);
                    chanIx++;

                    if (channel.Keyframes.Length > 0) {
                        channel.TargetShape = channel.Keyframes[channel.Keyframes.Length - 1].Shape;
                    }
                }
            }

            {
                // Generate and patch procedural index buffers (ufbx.c:22027-22039)
                uint[] zeroIndices = new uint[uc.MaxZeroIndices];
                uint[] consecutiveIndices = new uint[uc.MaxConsecutiveIndices];
                for (int i = 0; i < uc.MaxConsecutiveIndices; i++) {
                    consecutiveIndices[i] = (uint)i;
                }

                uc.ZeroIndices = zeroIndices;
                uc.ConsecutiveIndices = consecutiveIndices;

                foreach (UfbxMesh mesh in scene.Meshes) {
                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.VertexPosition.Indices);
                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.VertexNormal.Indices);
                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.VertexColor.Indices);
                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.VertexCrease.Indices);
                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.FaceMaterial);
                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.FaceGroup);

                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.SkinnedPosition.Indices);
                    UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.SkinnedNormal.Indices);

                    for (int si = 0; mesh.UvSets != null && si < mesh.UvSets.Length; si++) {
                        UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.UvSets[si].VertexUv.Indices);
                        UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.UvSets[si].VertexBitangent.Indices);
                        UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.UvSets[si].VertexTangent.Indices);
                    }

                    for (int si = 0; mesh.ColorSets != null && si < mesh.ColorSets.Length; si++) {
                        UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.ColorSets[si].VertexColor.Indices);
                    }

                    // Generate normals if necessary
                    if (!mesh.VertexNormal.Exists && uc.Opts.GenerateMissingNormals) {
                        UfbxiSceneFinalize.GenerateNormals(uc, mesh);
                    }

                    // Assign first UV and color sets as the "canonical" ones
                    if (mesh.UvSets != null && mesh.UvSets.Length > 0) {
                        mesh.VertexUv = mesh.UvSets[0].VertexUv;
                        mesh.VertexBitangent = mesh.UvSets[0].VertexBitangent;
                        mesh.VertexTangent = mesh.UvSets[0].VertexTangent;
                    }
                    if (mesh.ColorSets != null && mesh.ColorSets.Length > 0) {
                        mesh.VertexColor = mesh.ColorSets[0].VertexColor;
                    }

                    if (mesh.FaceGroupParts != null && mesh.FaceGroupParts.Length == 1) {
                        UfbxiSceneBuild.PatchIndexPointer(uc, ref mesh.FaceGroupParts[0].FaceIndices);
                    }

                    mesh.Materials = UfbxiSceneBuild.FetchMeshMaterials(uc, mesh, true);

                    // Patch materials to instances if necessary
                    if (mesh.Materials.Length > 0) {
                        foreach (UfbxNode node in mesh.Instances != null ? mesh.Instances : Array.Empty<UfbxNode>()) {
                            if (node.Materials != null && node.Materials.Length < mesh.Materials.Length && mesh.Materials[0] != null) {
                                UfbxMaterial[] materials = new UfbxMaterial[mesh.Materials.Length];
                                for (int i = 0; i < node.Materials.Length; i++) {
                                    materials[i] = node.Materials[i];
                                }
                                for (int i = node.Materials.Length; i < mesh.Materials.Length; i++) {
                                    materials[i] = mesh.Materials[i];
                                }
                                node.Materials = materials;
                            }
                        }
                    }

                    if (uc.RetainMeshParts) {
                        int numParts = MaxInt(mesh.Materials.Length, 1);
                        mesh.MaterialParts = new UfbxMeshPart[numParts];
                    }

                    if (mesh.Materials.Length <= 1) {
                        // Use the shared consecutive index buffer for mesh faces if there's only one material
                        // See HACK(consecutive-faces) in `ufbxi_read_mesh()`.
                        if (mesh.MaterialParts != null && mesh.MaterialParts.Length > 0) {
                            UfbxMeshPart part = mesh.MaterialParts[0]; // struct: write back below
                            part.NumFaces = mesh.NumFaces;
                            part.NumTriangles = mesh.NumTriangles;
                            part.NumEmptyFaces = mesh.NumEmptyFaces;
                            part.NumPointFaces = mesh.NumPointFaces;
                            part.NumLineFaces = mesh.NumLineFaces;
                            part.FaceIndices = uc.ConsecutiveIndices;
                            mesh.MaterialPartUsageOrder = uc.ZeroIndices;
                            mesh.MaterialParts[0] = part;

                        }
                        if (mesh.Materials.Length == 1) {
                            mesh.FaceMaterial = uc.ZeroIndices;
                        } else {
                            mesh.FaceMaterial = null;
                        }
                    } else if (mesh.Materials.Length > 0) {
                        UfbxiSubdivide.FinalizeMeshMaterial(mesh);
                    }

                    // Fetch deformers
                    mesh.SkinDeformers = CastArray<UfbxSkinDeformer>(UfbxiSceneBuild.FetchDstElements(uc, mesh, searchNode, true, null, UfbxElementType.SkinDeformer));
                    mesh.BlendDeformers = CastArray<UfbxBlendDeformer>(UfbxiSceneBuild.FetchDstElements(uc, mesh, searchNode, true, null, UfbxElementType.BlendDeformer));
                    mesh.CacheDeformers = CastArray<UfbxCacheDeformer>(UfbxiSceneBuild.FetchDstElements(uc, mesh, searchNode, true, null, UfbxElementType.CacheDeformer));
                    mesh.AllDeformers = UfbxiSceneBuild.FetchDeformers(uc, mesh, searchNode);

                    // Vertex position must always exist if not explicitly allowed to be missing
                    if (!mesh.VertexPosition.Exists && !uc.Opts.AllowMissingVertexPosition) {
                        UfbxiFail.CheckNoDesc(mesh.NumIndices == 0, "mesh->num_indices == 0");
                        UfbxVertexVec3 vp = mesh.VertexPosition;
                        vp.Exists = true;
                        vp.UniquePerVertex = true;
                        mesh.VertexPosition = vp;
                        UfbxVertexVec3 sp = mesh.SkinnedPosition;
                        sp.Exists = true;
                        sp.UniquePerVertex = true;
                        mesh.SkinnedPosition = sp;
                    }

                    // Update metadata
                    if (mesh.MaxFaceTriangles > scene.Metadata.MaxFaceTriangles) {
                        scene.Metadata.MaxFaceTriangles = mesh.MaxFaceTriangles;
                    }
                }
            }

            foreach (UfbxStereoCamera stereo in scene.StereoCameras) {
                stereo.Left = (UfbxCamera)UfbxiSceneBuild.FetchDstElement(stereo, searchNode, UfbxiStrings.LeftCamera, UfbxElementType.Camera);
                stereo.Right = (UfbxCamera)UfbxiSceneBuild.FetchDstElement(stereo, searchNode, UfbxiStrings.RightCamera, UfbxElementType.Camera);
            }

            foreach (UfbxNurbsCurve curve in scene.NurbsCurves) {
                UfbxiSceneFinalize.FinalizeNurbsBasis(ref curve.Basis);
            }

            foreach (UfbxNurbsSurface surface in scene.NurbsSurfaces) {
                UfbxiSceneFinalize.FinalizeNurbsBasis(ref surface.BasisU);
                UfbxiSceneFinalize.FinalizeNurbsBasis(ref surface.BasisV);

                surface.Material = (UfbxMaterial)UfbxiSceneBuild.FetchDstElement(surface, true, null, UfbxElementType.Material);
            }

            foreach (UfbxAnimStack stack in scene.AnimStacks) {
                stack.Layers = CastArray<UfbxAnimLayer>(UfbxiSceneBuild.FetchDstElements(uc, stack, false, true, null, UfbxElementType.AnimLayer));

                stack.Anim = PushAnim(stack.Layers);
            }

            foreach (UfbxAnimLayer layer in scene.AnimLayers) {
                layer.AnimValues = CastArray<UfbxAnimValue>(UfbxiSceneBuild.FetchDstElements(uc, layer, false, true, null, UfbxElementType.AnimValue));

                layer.Anim = PushAnim(new UfbxAnimLayer[] { layer });

                uint minId = uint.MaxValue, maxId = 0;

                // Combine the animated properties with elements (potentially duplicates!)
                List<UfbxAnimProp> animProps = new List<UfbxAnimProp>();
                layer.ElementIdBitmask = new uint[4]; // C: _element_id_bitmask[4], zeroed
                foreach (UfbxAnimValue value in layer.AnimValues) {
                    UfbxConnection[] conns = value.ConnectionsSrc;
                    if (conns == null) continue;
                    for (int i = 0; i < conns.Length; i++) {
                        UfbxConnection ac = conns[i];
                        if (ac.SrcProp.Length == 0 && ac.DstProp.Length > 0) {
                            UfbxAnimProp aprop = new UfbxAnimProp();
                            uint id = ac.Dst.ElementId;
                            minId = MinUInt(minId, id);
                            maxId = MaxUInt(maxId, id);
                            uint idMask = 4 - 1; // C: arraycount(_element_id_bitmask) - 1
                            layer.ElementIdBitmask[(id >> 5) & idMask] |= 1u << (int)(id & 31);
                            aprop.AnimValue = value;
                            aprop.Element = ac.Dst;
                            aprop.InternalKey = UfbxiProperties.GetNameKey(ac.DstProp, ac.DstProp.Length);
                            aprop.PropName = ac.DstProp;
                            animProps.Add(aprop);
                        }
                    }
                }

                if (minId != uint.MaxValue) {
                    layer.MinElementId = minId;
                    layer.MaxElementId = maxId;
                }

                switch (UfbxiProperties.FindInt(layer.Props, UfbxiStrings.BlendMode, 0)) {
                case 0: // Additive
                    layer.Blended = true;
                    layer.Additive = true;
                    break;
                case 1: // Override
                    layer.Blended = false;
                    layer.Additive = false;
                    break;
                case 2: // Override Passthrough
                    layer.Blended = true;
                    layer.Additive = false;
                    break;
                default: // Unknown
                    layer.Blended = false;
                    layer.Additive = false;
                    break;
                }

                if (UfbxiProperties.TryFindProp(layer.Props, UfbxiStrings.Weight, out UfbxProp weightProp)) {
                    layer.Weight = weightProp.ValueReal / 100.0;
                    if (layer.Weight < 0.0) layer.Weight = 0.0;
                    if (layer.Weight > (double)0.99999f) layer.Weight = 1.0; // C: 0.99999f (ufbx.c:22244)
                    layer.WeightIsAnimated = (weightProp.Flags & UfbxPropFlags.Animated) != 0;
                } else {
                    layer.Weight = 1.0;
                    layer.WeightIsAnimated = false;
                }
                layer.ComposeRotation = UfbxiProperties.FindInt(layer.Props, UfbxiStrings.RotationAccumulationMode, 0) == 0;
                layer.ComposeScale = UfbxiProperties.FindInt(layer.Props, UfbxiStrings.ScaleAccumulationMode, 0) == 0;

                // C appends a zeroed sentinel anim prop past `count` so iteration can be
                // boundary-free; the port's comparators cannot dereference its NULL element,
                // and C's searches never read past `count`, so the port omits it.
                layer.AnimProps = animProps.ToArray();
                UfbxiSceneBuild.SortAnimProps(layer.AnimProps, layer.AnimProps.Length);
            }

            foreach (UfbxAnimValue value in scene.AnimValues) {
                // C: `curves[3]` is an embedded fixed array (always present); the port
                // materializes it here if the reader left it null (PORTING_NOTES: the hash
                // walks all three slots, so it must never be null).
                if (value.Curves == null) value.Curves = new UfbxAnimCurve[3];

                // TODO: Search for things like d|Visibility with a constructed name
                value.DefaultValue.X = UfbxiProperties.FindReal(value.Props, UfbxiStrings.X, value.DefaultValue.X);
                value.DefaultValue.X = UfbxiProperties.FindReal(value.Props, UfbxiStrings.d_X, value.DefaultValue.X);
                value.DefaultValue.Y = UfbxiProperties.FindReal(value.Props, UfbxiStrings.Y, value.DefaultValue.Y);
                value.DefaultValue.Y = UfbxiProperties.FindReal(value.Props, UfbxiStrings.d_Y, value.DefaultValue.Y);
                value.DefaultValue.Z = UfbxiProperties.FindReal(value.Props, UfbxiStrings.Z, value.DefaultValue.Z);
                value.DefaultValue.Z = UfbxiProperties.FindReal(value.Props, UfbxiStrings.d_Z, value.DefaultValue.Z);

                UfbxConnection[] conns = value.ConnectionsDst;
                if (conns != null) {
                    for (int i = 0; i < conns.Length; i++) {
                        UfbxConnection conn = conns[i];
                        if (conn.Src.Type == UfbxElementType.AnimCurve && conn.SrcProp.Length == 0) {
                            UfbxAnimCurve curve = (UfbxAnimCurve)conn.Src;

                            uint index = 0;
                            string name = conn.DstProp;
                            if (name == UfbxiStrings.Y || name == UfbxiStrings.d_Y) index = 1;
                            if (name == UfbxiStrings.Z || name == UfbxiStrings.d_Z) index = 2;

                            if (UfbxiProperties.TryFindPropLen(value.Props, conn.DstProp, out UfbxProps owner, out int propIndex)) {
                                double propReal = owner.Props[propIndex].ValueReal;
                                switch (index) {
                                case 0: value.DefaultValue.X = propReal; break;
                                case 1: value.DefaultValue.Y = propReal; break;
                                default: value.DefaultValue.Z = propReal; break;
                                }
                            }
                            if (value.Curves == null) value.Curves = new UfbxAnimCurve[3]; // C: embedded fixed array
                            value.Curves[index] = curve;
                        }
                    }
                }
            }

            foreach (UfbxAnimCurve curve in scene.AnimCurves) {
                if (curve.Keyframes != null && curve.Keyframes.Length > 0) {
                    curve.MinTime = curve.Keyframes[0].Time;
                    curve.MaxTime = curve.Keyframes[curve.Keyframes.Length - 1].Time;
                }
            }

            foreach (UfbxShader shader in scene.Shaders) {
                shader.Bindings = CastArray<UfbxShaderBinding>(UfbxiSceneBuild.FetchDstElements(uc, shader, false, false, null, UfbxElementType.ShaderBinding));

                if (UfbxiSceneFinalize.FindPropLen(shader.Props, "RenderAPI", out UfbxProp api)) {
                    if (api.ValueStr == "ARNOLD_SHADER_ID") {
                        shader.Type = UfbxShaderType.ArnoldStandardSurface;
                    } else if (api.ValueStr == "OSL") {
                        shader.Type = UfbxShaderType.OslStandardSurface;
                    } else if (api.ValueStr == "SFX_PBS_SHADER") {
                        shader.Type = UfbxShaderType.ShaderFxGraph;
                    }
                }
            }

            foreach (UfbxMaterial material in scene.Materials) {
                material.Shader = (UfbxShader)UfbxiSceneBuild.FetchSrcElement(material, false, null, UfbxElementType.Shader);

                if (material.ShadingModelName == "lambert" || material.ShadingModelName == "Lambert") {
                    material.ShaderType = UfbxShaderType.FbxLambert;
                } else if (material.ShadingModelName == "phong" || material.ShadingModelName == "Phong") {
                    material.ShaderType = UfbxShaderType.FbxPhong;
                }

                if (material.Shader != null) {
                    material.ShaderType = material.Shader.Type;
                } else {
                    if (uc.Opts.UseBlenderPbrMaterial && uc.Exporter == UfbxExporter.BlenderBinary && uc.ExporterVersion >= UfbxiProperties.PackVersion(4, 12, 0)) {
                        material.ShaderType = UfbxShaderType.BlenderPhong;
                    }

                    // TODO: Is this too strict?
                    if (material.ShaderType == UfbxShaderType.Unknown) {
                        uint classidA = (uint)(ulong)UfbxiSceneFinalize.FindIntLen(material.Props, "3dsMax|ClassIDa", 0);
                        uint classidB = (uint)(ulong)UfbxiSceneFinalize.FindIntLen(material.Props, "3dsMax|ClassIDb", 0);
                        if (classidA == 0x3d6b1cecu && classidB == 0xdeadc001u) {
                            material.ShaderType = UfbxShaderType.Max3dsPhysicalMaterial;
                            material.ShaderPropPrefix = "3dsMax|Parameters|";
                        } else if (classidA == 0xf1551e33u && classidB == 0x37fb1337u) {
                            material.ShaderType = UfbxShaderType.OpenPbrMaterial;
                            material.ShaderPropPrefix = "3dsMax|Parameters|";
                        } else if (classidA == 0x38420192u && classidB == 0x45fe4e1bu) {
                            material.ShaderType = UfbxShaderType.GltfMaterial;
                            material.ShaderPropPrefix = "3dsMax|";
                        } else if (classidA == 0xd00f1e00u && classidB == 0xbe77e500u) {
                            material.ShaderType = UfbxShaderType.Max3dsPbrMetalRough;
                            material.ShaderPropPrefix = "3dsMax|main|";
                        } else if (classidA == 0xd00f1e00u && classidB == 0x01dbad33u) {
                            material.ShaderType = UfbxShaderType.Max3dsPbrSpecGloss;
                            material.ShaderPropPrefix = "3dsMax|main|";
                        }
                    }
                }

                material.Textures = UfbxiSceneBuild.FetchTextures(uc, material, false);
            }

            // Ugh.. Patch the textures from meshes for legacy LayerElement-style textures
            // (ufbx.c:22367-22469)
            {
                foreach (UfbxMesh mesh in scene.Meshes) {
                    int numMaterials = mesh.Materials.Length;

                    UfbxiMeshExtra extra = uc.GetElementExtra<UfbxiMeshExtra>(mesh.ElementId);
                    if (extra == null) continue;
                    if (numMaterials == 0) continue;

                    UfbxTexture[] textures = CastArray<UfbxTexture>(UfbxiSceneBuild.FetchDstElements(uc, mesh, true, false, null, UfbxElementType.Texture));

                    List<UfbxiTmpMaterialTexture> matTexList = new List<UfbxiTmpMaterialTexture>();
                    int numMaterialTextures = 0;
                    for (int i = 0; i < extra.TextureCount; i++) {
                        UfbxiTmpMeshTexture tex = extra.TextureArr[i];
                        if (tex.AllSame) {
                            int textureId = tex.NumFaces > 0 ? (int)tex.FaceTexture[0] : 0;
                            if (textureId >= 0 && (uint)textureId < (uint)textures.Length) {
                                for (int m = 0; m < numMaterials; m++) {
                                    UfbxiTmpMaterialTexture matTex;
                                    matTex.MaterialId = m;
                                    matTex.TextureId = textureId;
                                    matTex.PropName = tex.PropName;
                                    matTexList.Add(matTex);
                                    numMaterialTextures++;
                                }
                            }
                        } else if (mesh.FaceMaterial != null && mesh.FaceMaterial.Length > 0) {
                            int numFaces = MinInt(tex.NumFaces, mesh.NumFaces);
                            int prevMaterial = -1;
                            int prevTexture = -1;
                            for (int fi = 0; fi < numFaces; fi++) {
                                int textureId = (int)tex.FaceTexture[fi];
                                int materialId = (int)mesh.FaceMaterial[fi];
                                if (textureId < 0 || (uint)textureId >= (uint)textures.Length) continue;
                                if (materialId < 0 || materialId >= numMaterials) continue;
                                if (materialId == prevMaterial && textureId == prevTexture) continue;
                                prevMaterial = materialId;
                                prevTexture = textureId;

                                UfbxiTmpMaterialTexture matTex;
                                matTex.MaterialId = materialId;
                                matTex.TextureId = textureId;
                                matTex.PropName = tex.PropName;
                                matTexList.Add(matTex);
                                numMaterialTextures++;
                            }
                        }
                    }

                    // Push a sentinel material texture to the end so we don't need to
                    // duplicate the material texture flushing code twice.
                    {
                        UfbxiTmpMaterialTexture matTex;
                        matTex.MaterialId = -1;
                        matTex.TextureId = -1;
                        matTex.PropName = string.Empty;
                        matTexList.Add(matTex);
                    }

                    UfbxiTmpMaterialTexture[] matTexs = matTexList.ToArray();
                    UfbxiSceneBuild.SortTmpMaterialTextures(matTexs, numMaterialTextures);

                    int prevMaterial2 = -2;
                    int prevTexture2 = -2;
                    string prevProp = null;
                    int numTexturesInMaterial = 0;
                    List<UfbxMaterialTexture> curTextures = new List<UfbxMaterialTexture>();
                    for (int i = 0; i < numMaterialTextures + 1; i++) {
                        UfbxiTmpMaterialTexture matTex = matTexs[i];
                        if (matTex.MaterialId != prevMaterial2) {
                            if (prevMaterial2 >= 0 && numTexturesInMaterial > 0) {
                                UfbxMaterial mat = mesh.Materials[prevMaterial2];
                                if (mat != null && (mat.Textures == null || mat.Textures.Length == 0)) {
                                    mat.Textures = curTextures.ToArray();
                                }
                                curTextures.Clear();

                                if (matTex.MaterialId < 0) break;
                                prevMaterial2 = matTex.MaterialId;
                                prevTexture2 = -1;
                                prevProp = null;
                                numTexturesInMaterial = 0;
                            } else {
                                if (matTex.MaterialId < 0) break;
                                prevMaterial2 = matTex.MaterialId;
                                prevTexture2 = -1;
                                prevProp = null;
                                numTexturesInMaterial = 0;
                            }
                        }
                        if (matTex.TextureId == prevTexture2 && ReferenceEquals(matTex.PropName, prevProp)) continue;
                        prevTexture2 = matTex.TextureId;
                        prevProp = matTex.PropName;

                        UfbxMaterialTexture texOut = new UfbxMaterialTexture();
                        texOut.Texture = textures[prevTexture2];
                        texOut.ShaderProp = texOut.MaterialProp = matTex.PropName;
                        curTextures.Add(texOut);
                        numTexturesInMaterial++;
                    }
                }
            }

            UfbxiSceneFinalize.ResolveFileContent(uc);

            foreach (UfbxTexture texture in scene.Textures) {
                UfbxiTextureExtra extra = uc.GetElementExtra<UfbxiTextureExtra>(texture.ElementId);

                if (UfbxiProperties.TryFindProp(texture.Props, UfbxiStrings.UVSet, out UfbxProp uvSet)) {
                    texture.UvSet = uvSet.ValueStr;
                } else {
                    texture.UvSet = string.Empty;
                }

                texture.Video = (UfbxVideo)UfbxiSceneBuild.FetchDstElement(texture, false, null, UfbxElementType.Video);
                if (texture.Video != null) {
                    texture.Content = texture.Video.Content;
                }

                UfbxiSceneFinalize.FinalizeShaderTexture(uc, texture);

                {
                    UfbxiSceneFiles.UfbxiStrblob filename = default, absolute = default, relative = default;
                    filename.Str = texture.Filename;
                    absolute.Str = texture.AbsoluteFilename;
                    relative.Str = texture.RelativeFilename;
                    UfbxiSceneFinalize.ResolveFilenames(uc, ref filename, ref absolute, ref relative, false);
                    texture.Filename = filename.Str;
                    texture.AbsoluteFilename = absolute.Str;
                    texture.RelativeFilename = relative.Str;
                }
                {
                    UfbxiSceneFiles.UfbxiStrblob filename = default, absolute = default, relative = default;
                    filename.Blob = texture.RawFilename;
                    absolute.Blob = texture.RawAbsoluteFilename;
                    relative.Blob = texture.RawRelativeFilename;
                    UfbxiSceneFinalize.ResolveFilenames(uc, ref filename, ref absolute, ref relative, true);
                    texture.RawFilename = filename.Blob;
                    texture.RawAbsoluteFilename = absolute.Blob;
                    texture.RawRelativeFilename = relative.Blob;
                }

                // Fetch layered texture layers and patch alphas/blend modes
                if (texture.Type == UfbxTextureType.Layered) {
                    texture.Layers = UfbxiSceneBuild.FetchTextureLayers(uc, texture);
                    if (extra != null) {
                        int num = MinInt(extra.Alphas != null ? extra.Alphas.Length : 0, texture.Layers.Length);
                        for (int i = 0; i < num; i++) {
                            UfbxTextureLayer layer = texture.Layers[i];
                            layer.Alpha = extra.Alphas[i];
                            texture.Layers[i] = layer;
                        }
                        num = MinInt(extra.BlendModes != null ? extra.BlendModes.Length : 0, texture.Layers.Length);
                        for (int i = 0; i < num; i++) {
                            int mode = extra.BlendModes[i];
                            if (mode >= 0 && mode < (int)UfbxBlendMode.Overlay) {
                                UfbxTextureLayer layer = texture.Layers[i];
                                layer.BlendMode = (UfbxBlendMode)mode;
                                texture.Layers[i] = layer;
                            }
                        }
                    }
                }

                UfbxiSceneFinalize.InsertTextureFile(uc, texture);
            }

            UfbxiSceneFinalize.PropagateMainTextures(scene);
            UfbxiSceneFinalize.PopTextureFiles(uc);

            // Second pass to fetch material maps (ufbx.c:22516-22540)
            foreach (UfbxMaterial material in scene.Materials) {
                UfbxiSceneBuild.SortMaterialTextures(material.Textures, material.Textures.Length);
                UfbxiSceneFinalize.FetchMaps(scene, material);

                // Fetch `ufbx_material_texture.shader_prop` names
                if (material.Shader != null) {
                    foreach (UfbxShaderBinding binding in material.Shader.Bindings) {
                        foreach (UfbxShaderPropBinding prop in binding.PropBindings) {
                            string name = prop.MaterialProp;

                            int index = int.MaxValue; // C: SIZE_MAX
                            {
                                UfbxMaterialTexture[] textures = material.Textures;
                                int size = textures.Length;
                                int lo = 0, hi = size;
                                while (hi - lo > 4) {
                                    int mid = lo + (hi - lo) / 2;
                                    if (UfbxiStr.Less(textures[mid].MaterialProp, name)) {
                                        lo = mid + 1;
                                    } else {
                                        hi = mid + 1;
                                    }
                                }
                                for (; lo < hi; lo++) {
                                    if (ReferenceEquals(textures[lo].MaterialProp, name)) { index = lo; break; }
                                }
                            }
                            for (; index < material.Textures.Length && ReferenceEquals(material.Textures[index].ShaderProp, name); index++) {
                                material.Textures[index].ShaderProp = prop.ShaderProp;
                            }
                        }
                    }
                }
            }

            foreach (UfbxDisplayLayer layer in scene.DisplayLayers) {
                layer.Nodes = CastArray<UfbxNode>(UfbxiSceneBuild.FetchDstElements(uc, layer, false, true, null, UfbxElementType.Node));
            }

            foreach (UfbxSelectionSet set in scene.SelectionSets) {
                set.Nodes = CastArray<UfbxSelectionNode>(UfbxiSceneBuild.FetchDstElements(uc, set, false, true, null, UfbxElementType.SelectionNode));
            }

            foreach (UfbxSelectionNode node in scene.SelectionNodes) {
                node.TargetNode = (UfbxNode)UfbxiSceneBuild.FetchDstElement(node, false, null, UfbxElementType.Node);
                node.TargetMesh = (UfbxMesh)UfbxiSceneBuild.FetchDstElement(node, false, null, UfbxElementType.Mesh);
                if (node.TargetMesh == null && node.TargetNode != null) {
                    node.TargetMesh = node.TargetNode.Mesh;
                } else if (node.TargetNode == null && node.TargetMesh != null && node.TargetMesh.Instances != null && node.TargetMesh.Instances.Length > 0) {
                    node.TargetNode = node.TargetMesh.Instances[0];
                }

                UfbxMesh mesh = node.TargetMesh;
                if (mesh != null) {
                    UfbxiSceneFinalize.ValidateIndices(uc, ref node.Vertices, mesh.NumVertices);
                    UfbxiSceneFinalize.ValidateIndices(uc, ref node.Edges, mesh.NumEdges);
                    UfbxiSceneFinalize.ValidateIndices(uc, ref node.Faces, mesh.NumFaces);
                }
            }

            foreach (UfbxConstraint constraint in scene.Constraints) {
                List<UfbxConstraintTarget> targets = new List<UfbxConstraintTarget>();

                // Find property connections in _both_ src and dst connections as they are inconsistent
                // in pre-7000 files. For example "Constrained Object" is a "PO" connection in 6100.
                UfbxConnection[] srcConns = constraint.ConnectionsSrc;
                if (srcConns != null) {
                    for (int i = 0; i < srcConns.Length; i++) {
                        UfbxConnection conn = srcConns[i];
                        if (conn.SrcProp.Length == 0 || conn.Dst.Type != UfbxElementType.Node) continue;
                        UfbxiSceneFinalize.AddConstraintProp(targets, constraint, (UfbxNode)conn.Dst, conn.SrcProp);
                    }
                }
                UfbxConnection[] dstConns = constraint.ConnectionsDst;
                if (dstConns != null) {
                    for (int i = 0; i < dstConns.Length; i++) {
                        UfbxConnection conn = dstConns[i];
                        if (conn.DstProp.Length == 0 || conn.Src.Type != UfbxElementType.Node) continue;
                        UfbxiSceneFinalize.AddConstraintProp(targets, constraint, (UfbxNode)conn.Src, conn.DstProp);
                    }
                }

                constraint.Targets = targets.ToArray();
            }

            foreach (UfbxAudioLayer layer in scene.AudioLayers) {
                layer.Clips = CastArray<UfbxAudioClip>(UfbxiSceneBuild.FetchDstElements(uc, layer, false, true, null, UfbxElementType.AudioClip));
            }

            foreach (UfbxLodGroup lod in scene.LodGroups) {
                UfbxiSceneFinalize.FinalizeLodGroup(lod);
            }

            UfbxiSceneFinalize.FetchFileTextures(uc);

            // NOTE: This will be patched over in `ufbxi_update_scene()` if there are `anim_layers`
            if (scene.AnimLayers.Length == 0) {
                scene.Anim = PushAnim(Array.Empty<UfbxAnimLayer>());
            }

            scene.Metadata.KtimeSecond = uc.KtimeSec;

            // Maya seems to use scale of 100/3, Blender binary uses exactly 33, ASCII has always value of 1.0
            if (uc.Version < 6000) {
                scene.Metadata.BonePropSizeUnit = 1.0;
            } else if (uc.Exporter == UfbxExporter.BlenderBinary) {
                scene.Metadata.BonePropSizeUnit = 33.0;
            } else if (uc.Exporter == UfbxExporter.BlenderAscii) {
                scene.Metadata.BonePropSizeUnit = 1.0;
            } else {
                scene.Metadata.BonePropSizeUnit = 100.0 / 3.0;
            }
            if (uc.Exporter == UfbxExporter.BlenderAscii) {
                scene.Metadata.BonePropLimbLengthRelative = false;
            } else {
                scene.Metadata.BonePropLimbLengthRelative = true;
            }
        }

        // ==================================================================
        // Sentinel index materialization (see the seam comment in Parse/Load.cs)
        // ==================================================================

        // The port's shared zero/consecutive buffers stand in for C's `uc->zero_indices` /
        // `uc->consecutive_indices`, whose per-list `count` lives outside the buffer. Once no
        // more identity checks can run, each sentinel-backed list gets a right-sized copy with
        // the same contents (all zeros, or 0..n-1), so array `Length` equals C's `count`.
        internal static void MaterializeSentinelIndexLists(UfbxiContext uc)
        {
            foreach (UfbxMesh mesh in uc.Scene.Meshes) {
                MaterializeIndexList(ref mesh.VertexPosition.Indices, uc, mesh.NumIndices);
                MaterializeIndexList(ref mesh.VertexNormal.Indices, uc, mesh.NumIndices);
                MaterializeIndexList(ref mesh.VertexColor.Indices, uc, mesh.NumIndices);
                MaterializeIndexList(ref mesh.VertexCrease.Indices, uc, mesh.NumIndices);
                MaterializeIndexList(ref mesh.FaceMaterial, uc, mesh.NumFaces);
                MaterializeIndexList(ref mesh.FaceGroup, uc, mesh.NumFaces);

                MaterializeIndexList(ref mesh.SkinnedPosition.Indices, uc, mesh.NumIndices);
                MaterializeIndexList(ref mesh.SkinnedNormal.Indices, uc, mesh.NumIndices);

                for (int i = 0; mesh.UvSets != null && i < mesh.UvSets.Length; i++) {
                    MaterializeIndexList(ref mesh.UvSets[i].VertexUv.Indices, uc, mesh.NumIndices);
                    MaterializeIndexList(ref mesh.UvSets[i].VertexBitangent.Indices, uc, mesh.NumIndices);
                    MaterializeIndexList(ref mesh.UvSets[i].VertexTangent.Indices, uc, mesh.NumIndices);
                }
                for (int i = 0; mesh.ColorSets != null && i < mesh.ColorSets.Length; i++) {
                    MaterializeIndexList(ref mesh.ColorSets[i].VertexColor.Indices, uc, mesh.NumIndices);
                }

                // C: ufbx.c:22070-22077 assigns the mesh-level uv/tangent/bitangent/color from
                // `uv_sets.data[0]`/`color_sets.data[0]` *after* those attributes have been
                // patched, so the copies inherit the patched index buffer. The port took those
                // copies in `UpdateScene` (SceneUpdate.cs:1855-1857) while both sides still
                // aliased the shared sentinel buffer, so the mesh-level fields have to be
                // materialized with the same logical count (`indices.count = num_indices`,
                // set once in `ufbxi_read_vertex_element`, ufbx.c:12763).
                MaterializeIndexList(ref mesh.VertexUv.Indices, uc, mesh.NumIndices);
                MaterializeIndexList(ref mesh.VertexTangent.Indices, uc, mesh.NumIndices);
                MaterializeIndexList(ref mesh.VertexBitangent.Indices, uc, mesh.NumIndices);

                // C: `part->face_indices.data = uc->consecutive_indices; count = num_faces`
                // and `material_part_usage_order.data = uc->zero_indices; count = 1`
                // (ufbx.c:22111-22133).
                if (mesh.MaterialParts != null && mesh.MaterialParts.Length > 0) {
                    MaterializeIndexList(ref mesh.MaterialParts[0].FaceIndices, uc, mesh.NumFaces);
                }
                MaterializeIndexList(ref mesh.MaterialPartUsageOrder, uc, 1);

                // C: ufbxi_patch_index_pointer(uc, &mesh->face_group_parts.data[0].face_indices.data)
                // (ufbx.c:22079-22080) swaps in the shared consecutive buffer while
                // `face_indices.count` stays at the part's own face count.
                if (mesh.FaceGroupParts != null && mesh.FaceGroupParts.Length == 1) {
                    UfbxMeshPart part = mesh.FaceGroupParts[0];
                    MaterializeIndexList(ref part.FaceIndices, uc, part.NumFaces);
                    mesh.FaceGroupParts[0] = part;
                }
            }
        }

        static void MaterializeIndexList(ref uint[] p, UfbxiContext uc, int count)
        {
            if (p == null) return;
            if (ReferenceEquals(p, uc.ZeroIndices)) {
                p = new uint[count]; // zero-filled, like C's shared zero buffer
            } else if (ReferenceEquals(p, uc.ConsecutiveIndices)) {
                uint[] copy = new uint[count];
                for (int i = 0; i < count; i++) copy[i] = (uint)i;
                p = copy;
            }
        }

        // C: element list -> typed list materialization (C's typed lists are views over the
        // same element pointers; the port converts the element array per concrete type).
        internal static T[] CastArray<T>(UfbxElement[] src) where T : class
        {
            if (src == null || src.Length == 0) return Array.Empty<T>();
            T[] dst = new T[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = (T)(object)src[i];
            return dst;
        }

        // `SetElementsByType` hard-casts to the concrete element arrays; materialize them.
        internal static UfbxElement[] MaterializeTypedList(int type, UfbxElement[] items)
        {
            switch (type) {
            case 0: return CastArray<UfbxUnknown>(items);
            case 1: return CastArray<UfbxNode>(items);
            case 2: return CastArray<UfbxMesh>(items);
            case 3: return CastArray<UfbxLight>(items);
            case 4: return CastArray<UfbxCamera>(items);
            case 5: return CastArray<UfbxBone>(items);
            case 6: return CastArray<UfbxEmpty>(items);
            case 7: return CastArray<UfbxLineCurve>(items);
            case 8: return CastArray<UfbxNurbsCurve>(items);
            case 9: return CastArray<UfbxNurbsSurface>(items);
            case 10: return CastArray<UfbxNurbsTrimSurface>(items);
            case 11: return CastArray<UfbxNurbsTrimBoundary>(items);
            case 12: return CastArray<UfbxProceduralGeometry>(items);
            case 13: return CastArray<UfbxStereoCamera>(items);
            case 14: return CastArray<UfbxCameraSwitcher>(items);
            case 15: return CastArray<UfbxMarker>(items);
            case 16: return CastArray<UfbxLodGroup>(items);
            case 17: return CastArray<UfbxSkinDeformer>(items);
            case 18: return CastArray<UfbxSkinCluster>(items);
            case 19: return CastArray<UfbxBlendDeformer>(items);
            case 20: return CastArray<UfbxBlendChannel>(items);
            case 21: return CastArray<UfbxBlendShape>(items);
            case 22: return CastArray<UfbxCacheDeformer>(items);
            case 23: return CastArray<UfbxCacheFile>(items);
            case 24: return CastArray<UfbxMaterial>(items);
            case 25: return CastArray<UfbxTexture>(items);
            case 26: return CastArray<UfbxVideo>(items);
            case 27: return CastArray<UfbxShader>(items);
            case 28: return CastArray<UfbxShaderBinding>(items);
            case 29: return CastArray<UfbxAnimStack>(items);
            case 30: return CastArray<UfbxAnimLayer>(items);
            case 31: return CastArray<UfbxAnimValue>(items);
            case 32: return CastArray<UfbxAnimCurve>(items);
            case 33: return CastArray<UfbxDisplayLayer>(items);
            case 34: return CastArray<UfbxSelectionSet>(items);
            case 35: return CastArray<UfbxSelectionNode>(items);
            case 36: return CastArray<UfbxCharacter>(items);
            case 37: return CastArray<UfbxConstraint>(items);
            case 38: return CastArray<UfbxAudioLayer>(items);
            case 39: return CastArray<UfbxAudioClip>(items);
            case 40: return CastArray<UfbxPose>(items);
            case 41: return CastArray<UfbxMetadataObject>(items);
            default: return items;
            }
        }

        static UfbxElement[] MaterializeTypedList(int type, List<UfbxElement> items)
            => MaterializeTypedList(type, items.ToArray());

        // C: ufbxi_min_sz / ufbxi_max_sz / ufbxi_min32 / ufbxi_max32 (ufbx.c:1103-1108).
        static int MinInt(int a, int b) => a < b ? a : b;
        static int MaxInt(int a, int b) => a < b ? b : a;
        static uint MinUInt(uint a, uint b) => a < b ? a : b;
        static uint MaxUInt(uint a, uint b) => a < b ? b : a;
    }
}
