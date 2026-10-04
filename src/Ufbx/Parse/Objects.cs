// S2 object readers (materials/textures/misc), ported from ufbx v0.23.1 ufbx.c
// 14537-14948 and 15132-15237. Owned by the S2-objects porting agent (2026-10-03 wave).
//
// Notes:
//  * `read_connections` (ufbx.c:15239-15313) is NOT here: it was already ported by the
//    S1 pipeline in Parse/Connections.cs and `UfbxiRoot.ReadRoot` calls it directly.
//  * `read_global_settings` (ufbx.c:14944-14948) is NOT here either: it lives in
//    Parse/Root.cs (`UfbxiRoot.ReadGlobalSettings`) and `ufbxi_read_object`'s port
//    (ReadElement.cs) dispatches to it.
//  * Deep geometry readers (mesh/nurbs/line/blend shapes/vertex elements/transform
//    matrix) belong to the S2-geometry module (Parse/Geometry.cs, ufbx.c:12662-14108).
//    They are stubbed below with their C reader names so GraphCheck classifies the
//    SKIP correctly; the orchestrator rewires the calls once Geometry.cs lands.
//  * Sentinel index buffers model C's shared static arrays (ufbx.c:12659-12660); see
//    the comment on `SentinelIndexZero` for the porting contract with S3.
using System;
using System.Collections.Generic;

namespace Ufbx
{
    // C: ufbxi_texture_extra (ufbx.c:6338-6344). C stores raw pointers into the DOM
    // array buffer (`ufbx_real *alphas`, `int32_t *blend_modes`); the port materializes
    // them into plain arrays (bit-exact, the DOM bytes are little-endian doubles/ints).
    // Consumed by S3 when patching layered texture layers (ufbx.c:22475-22507) via
    // `UfbxiContext.GetElementExtra<UfbxiTextureExtra>(texture.ElementId)`.
    internal sealed class UfbxiTextureExtra
    {
        public double[] Alphas;      // C: alphas / num_alphas
        public int[] BlendModes;     // C: blend_modes / num_blend_modes
    }

    // C: ufbxi_tmp_bone_pose (ufbx.c:6316-6319). C transports this array through the
    // `ufbx_pose.bone_poses` pointer ("HACK", ufbx.c:14684-14687) and S3 replaces it with
    // resolved `ufbx_bone_pose`s (ufbx.c:21794-21826). The port keeps the raw array in a
    // per-element extra (`UfbxiContext.GetElementExtra<UfbxiPoseExtra>(pose.ElementId)`)
    // and leaves `UfbxPose.BonePoses` null until S3 builds it.
    internal struct UfbxiTmpBonePose
    {
        public ulong BoneFbxId;      // C: uint64_t bone_fbx_id
        public UfbxMatrix BoneToWorld; // C: ufbx_matrix bone_to_world
    }

    internal sealed class UfbxiPoseExtra
    {
        public UfbxiTmpBonePose[] BonePoses; // C: the tmp array behind `pose->bone_poses`
    }

    internal static class UfbxiObjects
    {
        // ------------------------------------------------------------------
        // Sentinel index buffers (ufbx.c:12659-12660)
        // ------------------------------------------------------------------

        // C: ufbxi_sentinel_index_zero / ufbxi_sentinel_index_consecutive — shared
        // 1-element static arrays used as `*_list.data` with `count` borrowed from the
        // mesh (`face_material`, `vertex_normal.indices`, ...). The list count therefore
        // does NOT match the C# array length; the consumer must know the real count from
        // the mesh. `ufbxi_patch_index_pointer()` (ufbx.c:19284-19291, S3 at 22044+)
        // detects them *by pointer identity* and swaps in the generated zero/consecutive
        // buffers, so the port's S3 must match with `ReferenceEquals` against these exact
        // instances (they are the only two instances, `internal static readonly`).
        internal static readonly uint[] SentinelIndexZero = new uint[] { 100000000 };
        internal static readonly uint[] SentinelIndexConsecutive = new uint[] { 123456789 };

        // ------------------------------------------------------------------
        // Small array materialization helpers (C aliases the DOM bytes; the port
        // converts, which is bit-exact for little-endian numeric arrays)
        // ------------------------------------------------------------------

        // C: (ufbx_real*)arr->data — 'r' arrays are normalized to 'd'.
        internal static double[] ReadRealArray(UfbxiValueArray arr, int count)
        {
            double[] dst = new double[count];
            for (int i = 0; i < count; i++) dst[i] = arr.GetReal(i);
            return dst;
        }

        // C: (uint32_t*)arr->data on an 'i' (int32) array — reinterprets the bytes.
        internal static uint[] ReadUint32Array(UfbxiValueArray arr, int count)
        {
            uint[] dst = new uint[count];
            for (int i = 0; i < count; i++) dst[i] = unchecked((uint)arr.GetInt32(i));
            return dst;
        }

        // C: (ufbx_vec3*)arr->data — count vec3s = 3*count reals.
        internal static UfbxVec3[] ReadVec3Array(UfbxiValueArray arr, int count)
        {
            UfbxVec3[] dst = new UfbxVec3[count];
            for (int i = 0; i < count; i++) {
                dst[i] = new UfbxVec3(arr.GetReal(i * 3 + 0), arr.GetReal(i * 3 + 1), arr.GetReal(i * 3 + 2));
            }
            return dst;
        }

        // C: ufbxi_find_val1(node, name, 'b', &blob) — no dedicated helper in UfbxiNode.
        internal static bool FindValBlob(UfbxiNode node, string name, out byte[] value)
        {
            value = null;
            UfbxiNode child = node.FindChild(name);
            if (child == null) return false;
            return child.GetValBlob(0, out value);
        }

        // C: ufbxi_str_less (ufbx.c:4931-4937) over the port's one-byte-per-char strings.
        internal static bool StrLess(string a, string b)
        {
            int len = a.Length < b.Length ? a.Length : b.Length;
            int cmp = string.CompareOrdinal(a, 0, b, 0, len);
            if (cmp != 0) return cmp < 0;
            return a.Length < b.Length;
        }

        // ------------------------------------------------------------------
        // Readers
        // ------------------------------------------------------------------

        // C: ufbxi_read_material (ufbx.c:14537-14549).
        internal static void ReadMaterial(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxMaterial material = UfbxiReadElement.PushElement<UfbxMaterial>(uc, info, UfbxElementType.Material);

            if (!node.FindValS(UfbxiStrings.ShadingModel, out string shadingModelName)) {
                shadingModelName = string.Empty; // C: ufbx_empty_string
            }
            material.ShadingModelName = shadingModelName;

            material.ShaderPropPrefix = string.Empty;
        }

        // C: ufbxi_read_texture (ufbx.c:14551-14573).
        internal static void ReadTexture(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxTexture texture = UfbxiReadElement.PushElement<UfbxTexture>(uc, info, UfbxElementType.Texture);

            texture.Type = UfbxTextureType.File;

            texture.Filename = string.Empty;
            texture.AbsoluteFilename = string.Empty;
            texture.RelativeFilename = string.Empty;

            if (node.FindValS(UfbxiStrings.FileName, out string s)) texture.AbsoluteFilename = s;
            if (node.FindValS(UfbxiStrings.Filename, out s)) texture.AbsoluteFilename = s;
            if (node.FindValS(UfbxiStrings.RelativeFileName, out s)) texture.RelativeFilename = s;
            if (node.FindValS(UfbxiStrings.RelativeFilename, out s)) texture.RelativeFilename = s;

            if (FindValBlob(node, UfbxiStrings.FileName, out byte[] b)) texture.RawAbsoluteFilename = b;
            if (FindValBlob(node, UfbxiStrings.Filename, out b)) texture.RawAbsoluteFilename = b;
            if (FindValBlob(node, UfbxiStrings.RelativeFileName, out b)) texture.RawRelativeFilename = b;
            if (FindValBlob(node, UfbxiStrings.RelativeFilename, out b)) texture.RawRelativeFilename = b;
        }

        // C: ufbxi_read_layered_texture (ufbx.c:14575-14602).
        internal static void ReadLayeredTexture(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxTexture texture = UfbxiReadElement.PushElement<UfbxTexture>(uc, info, UfbxElementType.Texture);

            texture.Type = UfbxTextureType.Layered;

            texture.Filename = string.Empty;
            texture.AbsoluteFilename = string.Empty;
            texture.RelativeFilename = string.Empty;

            UfbxiTextureExtra extra = uc.PushElementExtra(texture.ElementId, () => new UfbxiTextureExtra());

            UfbxiValueArray alphas = node.FindArray(UfbxiStrings.Alphas, 'r');
            if (alphas != null) {
                extra.Alphas = ReadRealArray(alphas, alphas.Size);
            }

            UfbxiValueArray blendModes = node.FindArray(UfbxiStrings.BlendModes, 'i');
            if (blendModes != null) {
                extra.BlendModes = new int[blendModes.Size];
                for (int i = 0; i < blendModes.Size; i++) extra.BlendModes[i] = blendModes.GetInt32(i);
            }
        }

        // C: ufbxi_read_video (ufbx.c:14604-14627).
        internal static void ReadVideo(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxVideo video = UfbxiReadElement.PushElement<UfbxVideo>(uc, info, UfbxElementType.Video);

            video.Filename = string.Empty;
            video.AbsoluteFilename = string.Empty;
            video.RelativeFilename = string.Empty;

            if (node.FindValS(UfbxiStrings.FileName, out string s)) video.AbsoluteFilename = s;
            if (node.FindValS(UfbxiStrings.Filename, out s)) video.AbsoluteFilename = s;
            if (node.FindValS(UfbxiStrings.RelativeFileName, out s)) video.RelativeFilename = s;
            if (node.FindValS(UfbxiStrings.RelativeFilename, out s)) video.RelativeFilename = s;

            if (FindValBlob(node, UfbxiStrings.FileName, out byte[] b)) video.RawAbsoluteFilename = b;
            if (FindValBlob(node, UfbxiStrings.Filename, out b)) video.RawAbsoluteFilename = b;
            if (FindValBlob(node, UfbxiStrings.RelativeFileName, out b)) video.RawRelativeFilename = b;
            if (FindValBlob(node, UfbxiStrings.RelativeFilename, out b)) video.RawRelativeFilename = b;

            UfbxiNode contentNode = node.FindChild(UfbxiStrings.Content);
            // C: ufbxi_check(ufbxi_read_embedded_blob(uc, &video->content, content_node)) —
            // the check only guards allocation; the port's ReadEmbeddedBlob cannot fail.
            if (UfbxiProperties.ReadEmbeddedBlob(uc, contentNode, out byte[] data, out int size)) {
                video.Content = data;
            }
        }

        // C: ufbxi_read_pose (ufbx.c:14648-14690).
        internal static void ReadPose(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info, string subType)
        {
            UfbxPose pose = UfbxiReadElement.PushElement<UfbxPose>(uc, info, UfbxElementType.Pose);

            // TODO(C): What are the actual other types?
            pose.IsBindPose = subType == UfbxiStrings.BindPose;

            // C: tmp poses are pushed to `uc->tmp_stack` and moved out with
            // `ufbxi_push_pop` at the end; a plain list reproduces the order.
            List<UfbxiTmpBonePose> tmpPoses = new List<UfbxiTmpBonePose>();

            for (uint i = 0; i < node.NumChildren; i++) {
                UfbxiNode n = node.Children[i];
                if (n.Name != UfbxiStrings.PoseNode) continue;

                // Bones are linked with FBX names/IDs bypassing the connection system (!?)
                ulong fbxId = 0;
                if (uc.Version < 7000) {
                    // C: ufbxi_find_val1(n, ufbxi_Node, "c", &name) — raw char
                    UfbxiNode nameNode = n.FindChild(UfbxiStrings.Node);
                    if (nameNode == null || !nameNode.GetValRawChar(0, out string name)) continue;
                    fbxId = UfbxiFbxId.SyntheticIdFromString(uc, name);
                    UfbxiFail.CheckNoDesc(fbxId != 0, "fbx_id");
                } else {
                    // C: ufbxi_find_val1(n, ufbxi_Node, "L", &fbx_id) +
                    //    ufbxi_check(ufbxi_validate_fbx_id(uc, &fbx_id)) — validate_fbx_id
                    //    converts pointer ids in place and fails through its own check.
                    UfbxiNode idNode = n.FindChild(UfbxiStrings.Node);
                    if (idNode == null || !idNode.GetValL(0, out long id)) continue;
                    fbxId = unchecked((ulong)id);
                    fbxId = UfbxiFbxId.ValidateFbxId(uc, fbxId);
                }

                UfbxiValueArray matrix = n.FindArray(UfbxiStrings.Matrix, 'r');
                if (matrix == null) continue;
                UfbxiFail.CheckNoDesc(matrix.Size >= 16, "matrix->size >= 16");

                UfbxiTmpBonePose tmpPose = default;
                tmpPose.BoneFbxId = fbxId;
                UfbxiGeometry.ReadTransformMatrix(out tmpPose.BoneToWorld, matrix);
                tmpPoses.Add(tmpPose);
            }

            // C: HACK — the `ufbxi_tmp_bone_pose` array travels through the
            // `pose->bone_poses` pointer until S3 resolves it (ufbx.c:21794-21826).
            // The port parks it in a per-element extra; `pose.BonePoses` stays null.
            UfbxiPoseExtra extra = uc.PushElementExtra(pose.ElementId, () => new UfbxiPoseExtra());
            extra.BonePoses = tmpPoses.ToArray();
        }

        // C: ufbxi_sort_shader_prop_bindings (ufbx.c:14692-14698) — stable sort, linear
        // insertion block 32, ordered by `ufbxi_str_less(shader_prop)`.
        internal static void SortShaderPropBindings(UfbxShaderPropBinding[] bindings, int count)
        {
            UfbxShaderPropBinding[] tmp = new UfbxShaderPropBinding[count];
            UfbxiSort.StableSort(32, bindings, tmp, count,
                (object user, UfbxShaderPropBinding a, UfbxShaderPropBinding b) =>
                    StrLess(a.ShaderProp, b.ShaderProp), null);
        }

        // C: ufbxi_read_binding_table (ufbx.c:14701-14738).
        internal static void ReadBindingTable(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxShaderBinding bindings = UfbxiReadElement.PushElement<UfbxShaderBinding>(uc, info, UfbxElementType.ShaderBinding);

            List<UfbxShaderPropBinding> entries = new List<UfbxShaderPropBinding>();
            for (uint i = 0; i < node.NumChildren; i++) {
                UfbxiNode n = node.Children[i];
                if (n.Name != UfbxiStrings.Entry) continue;

                // C: ufbxi_get_val4(n, "SCSC", &src, &src_type, &dst, &dst_type)
                if (!(n.GetValS(0, out string src) && n.GetValC(1, out string srcType)
                    && n.GetValS(2, out string dst) && n.GetValC(3, out string dstType))) {
                    continue;
                }

                if (srcType == UfbxiStrings.FbxPropertyEntry && dstType == UfbxiStrings.FbxSemanticEntry) {
                    entries.Add(new UfbxShaderPropBinding {
                        MaterialProp = src,
                        ShaderProp = dst,
                    });
                } else if (srcType == UfbxiStrings.FbxSemanticEntry && dstType == UfbxiStrings.FbxPropertyEntry) {
                    entries.Add(new UfbxShaderPropBinding {
                        MaterialProp = dst,
                        ShaderProp = src,
                    });
                }
            }

            UfbxShaderPropBinding[] propBindings = entries.ToArray();
            bindings.PropBindings = propBindings;

            SortShaderPropBindings(propBindings, propBindings.Length);
        }

        // C: ufbxi_find_uint32_list (ufbx.c:14750-14757).
        internal static uint[] FindUint32List(UfbxiNode node, string name)
        {
            UfbxiValueArray arr = node.FindArray(name, 'i');
            if (arr != null) {
                return ReadUint32Array(arr, arr.Size);
            }
            return null;
        }

        // C: ufbxi_read_selection_set (ufbx.c:14740-14748).
        internal static void ReadSelectionSet(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxiReadElement.PushElement<UfbxSelectionSet>(uc, info, UfbxElementType.SelectionSet);
        }

        // C: ufbxi_read_selection_node (ufbx.c:14759-14774).
        internal static void ReadSelectionNode(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxSelectionNode sel = UfbxiReadElement.PushElement<UfbxSelectionNode>(uc, info, UfbxElementType.SelectionNode);

            // C: ufbxi_find_val1(node, ufbxi_IsTheNodeInSet, "I", &in_set)
            UfbxiNode inSetNode = node.FindChild(UfbxiStrings.IsTheNodeInSet);
            if (inSetNode != null && inSetNode.GetValI(0, out int inSet) && inSet != 0) {
                sel.IncludeNode = true;
            }

            sel.Vertices = FindUint32List(node, UfbxiStrings.VertexIndexArray);
            sel.Edges = FindUint32List(node, UfbxiStrings.EdgeIndexArray);
            sel.Faces = FindUint32List(node, UfbxiStrings.PolygonIndexArray);
        }

        // C: ufbxi_read_character (ufbx.c:14776-14786).
        internal static void ReadCharacter(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxiReadElement.PushElement<UfbxCharacter>(uc, info, UfbxElementType.Character);
            // TODO(C): There's some extremely cursed all-caps data in characters
        }

        // C: ufbxi_read_audio_clip (ufbx.c:14788-14801).
        internal static void ReadAudioClip(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxAudioClip audio = UfbxiReadElement.PushElement<UfbxAudioClip>(uc, info, UfbxElementType.AudioClip);

            audio.Filename = string.Empty;
            audio.AbsoluteFilename = string.Empty;
            audio.RelativeFilename = string.Empty;

            UfbxiNode contentNode = node.FindChild(UfbxiStrings.Content);
            // C: ufbxi_check(ufbxi_read_embedded_blob(...)) — allocation-only failure.
            if (UfbxiProperties.ReadEmbeddedBlob(uc, contentNode, out byte[] data, out int size)) {
                audio.Content = data;
            }
        }

        // C: ufbxi_constraint_types[] (ufbx.c:14803-14815).
        static readonly string[] ConstraintTypeNames = new string[] {
            "Aim",                     // C: UFBX_CONSTRAINT_AIM
            "Parent-Child",            // C: UFBX_CONSTRAINT_PARENT
            "Position From Positions", // C: UFBX_CONSTRAINT_POSITION
            "Rotation From Rotations", // C: UFBX_CONSTRAINT_ROTATION
            "Scale From Scales",       // C: UFBX_CONSTRAINT_SCALE
            "Single Chain IK",         // C: UFBX_CONSTRAINT_SINGLE_CHAIN_IK
        };

        static readonly UfbxConstraintType[] ConstraintTypes = new UfbxConstraintType[] {
            UfbxConstraintType.Aim,
            UfbxConstraintType.Parent,
            UfbxConstraintType.Position,
            UfbxConstraintType.Rotation,
            UfbxConstraintType.Scale,
            UfbxConstraintType.SingleChainIk,
        };

        // C: ufbxi_read_constraint (ufbx.c:14817-14838).
        internal static void ReadConstraint(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxConstraint constraint = UfbxiReadElement.PushElement<UfbxConstraint>(uc, info, UfbxElementType.Constraint);

            if (!node.FindValS(UfbxiStrings.Type, out string typeName)) {
                typeName = string.Empty; // C: ufbx_empty_string
            }
            constraint.TypeName = typeName;

            // C: strcmp(constraint->type_name.data, ctype->name)
            for (int i = 0; i < ConstraintTypeNames.Length; i++) {
                if (UfbxiProperties.Strcmp(typeName, ConstraintTypeNames[i]) == 0) {
                    constraint.Type = ConstraintTypes[i];
                    break;
                }
            }
        }

        // ------------------------------------------------------------------
        // C: ufbxi_read_synthetic_attribute (ufbx.c:14840-14942)
        // ------------------------------------------------------------------

        // C: ufbxi_read_synthetic_attribute (ufbx.c:14840-14942). `sub_type` arrives raw
        // (pooled) and `super_type` is the node name (`ufbxi_Model` in the legacy path).
        internal static void ReadSyntheticAttribute(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info,
            string typeStr, string subType, string superType)
        {
            // Some legacy (version 6000) files store mesh nodes without any `sub_type`.
            // There seems to be no robust indicator, so detect it from `Vertices` and
            // `PolygonVertexIndex`.
            if (subType.Length == 0) { // C: sub_type == ufbxi_empty_char
                UfbxiNode nodeVertices = node.FindChild(UfbxiStrings.Vertices);
                UfbxiNode nodeIndices = node.FindChild(UfbxiStrings.PolygonVertexIndex);
                if (nodeVertices != null && nodeIndices != null) {
                    subType = UfbxiStrings.Mesh;
                }
            }

            if ((subType.Length == 0 || subType == UfbxiStrings.Model) && typeStr == UfbxiStrings.Model) {
                // Plain model
                return;
            }

            // C: ufbxi_element_info attrib_info = *info; — a struct copy: the props
            // *struct* (and its defaults pointer) is copied, the props *array* is shared
            // until the split below replaces the attribute side.
            UfbxiElementInfo attribInfo = new UfbxiElementInfo();
            attribInfo.FbxId = info.FbxId;
            attribInfo.Name = info.Name;
            attribInfo.Props = new UfbxProps {
                Props = info.Props.Props,
                NumAnimated = info.Props.NumAnimated,
                Defaults = info.Props.Defaults,
            };
            attribInfo.DomNode = info.DomNode;

            attribInfo.FbxId = UfbxiFbxId.PushSyntheticId(uc);

            // Use type and name from NodeAttributeName if it exists *uniquely*
            UfbxiNode attribNameNode = node.FindChild(UfbxiStrings.NodeAttributeName);
            if (attribNameNode != null && attribNameNode.GetValRawString(0, out string typeAndName)) {
                // C: ufbxi_check(ufbxi_split_type_and_name(...)) — allocation-only check.
                UfbxiFbxId.SplitTypeAndName(uc, typeAndName, out string attribTypeStr, out string attribNameStr);
                if (attribNameStr.Length > 0) {
                    attribInfo.Name = attribNameStr;
                    ulong attribId = UfbxiFbxId.SyntheticIdFromString(uc, typeAndName);
                    UfbxiFail.CheckNoDesc(attribId != 0, "attrib_id");
                    if (info.FbxId != attribId && !UfbxiFbxId.FbxIdExists(uc, attribId)) {
                        attribInfo.FbxId = attribId;
                    }
                }
            }

            // 6x00: Link the node to the node attribute so property connections can be
            // redirected from connections if necessary. (C wraps this in ufbxi_check for
            // the allocation result only.)
            UfbxiFbxId.InsertFbxAttr(uc, info.FbxId, attribInfo.FbxId);

            // Split properties between the node and the attribute.
            // Consider all user properties as node properties.
            UfbxProp[] ps = info.Props.Props;
            int end = ps != null ? ps.Length : 0;
            List<UfbxProp> attribProps = new List<UfbxProp>();
            int dst = 0, src = 0;
            while (src < end) {
                if (!UfbxiProperties.IsNodePropertyName(uc, ps[src].Name)
                    && (ps[src].Flags & UfbxPropFlags.UserDefined) == 0) {
                    attribProps.Add(ps[src]); // C: ufbxi_push_copy(&uc->tmp_stack, ...)
                    src++;
                } else if (dst != src) {
                    ps[dst++] = ps[src++];
                } else {
                    dst++; src++;
                }
            }

            attribInfo.Props.Props = attribProps.ToArray();
            Array.Resize(ref ps, dst);
            info.Props.Props = ps;

            if (subType == UfbxiStrings.Mesh) {
                UfbxiGeometry.ReadMesh(uc, node, attribInfo);
            } else if (subType == UfbxiStrings.Light) {
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.Light);
            } else if (subType == UfbxiStrings.Camera) {
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.Camera);
            } else if (subType == UfbxiStrings.LimbNode || subType == UfbxiStrings.Limb || subType == UfbxiStrings.Root) {
                UfbxiReadElement.ReadBone(uc, node, attribInfo, subType);
            } else if (subType == UfbxiStrings.Null || subType == UfbxiStrings.Marker) {
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.Empty);
            } else if (subType == UfbxiStrings.NurbsCurve) {
                if (node.FindChild(UfbxiStrings.KnotVector) == null) return;
                UfbxiGeometry.ReadNurbsCurve(uc, node, attribInfo);
            } else if (subType == UfbxiStrings.NurbsSurface) {
                if (node.FindChild(UfbxiStrings.KnotVectorU) == null) return;
                if (node.FindChild(UfbxiStrings.KnotVectorV) == null) return;
                UfbxiGeometry.ReadNurbsSurface(uc, node, attribInfo);
            } else if (subType == UfbxiStrings.Line) {
                if (node.FindChild(UfbxiStrings.Points) == null) return;
                if (node.FindChild(UfbxiStrings.PointsIndex) == null) return;
                UfbxiGeometry.ReadLine(uc, node, attribInfo);
            } else if (subType == UfbxiStrings.TrimNurbsSurface) {
                if (node.FindChild(UfbxiStrings.Layer) == null) return;
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.NurbsTrimSurface);
            } else if (subType == UfbxiStrings.Boundary) {
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.NurbsTrimBoundary);
            } else if (subType == UfbxiStrings.CameraStereo) {
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.StereoCamera);
            } else if (subType == UfbxiStrings.CameraSwitcher) {
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.CameraSwitcher);
            } else if (subType == UfbxiStrings.FKEffector) {
                UfbxiReadElement.ReadMarker(uc, node, attribInfo, UfbxMarkerType.FkEffector);
            } else if (subType == UfbxiStrings.IKEffector) {
                UfbxiReadElement.ReadMarker(uc, node, attribInfo, UfbxMarkerType.IkEffector);
            } else if (subType == UfbxiStrings.LodGroup) {
                UfbxiReadElement.ReadElement(uc, node, attribInfo, UfbxElementType.LodGroup);
            } else {
                UfbxiReadElement.ReadUnknown(uc, node, attribInfo, typeStr, subType, superType);
            }

            UfbxiReadElement.ConnectOo(uc, attribInfo.FbxId, info.FbxId);
        }

        // ------------------------------------------------------------------
        // C: ufbxi_read_objects_threaded (ufbx.c:15132-15237)
        // ------------------------------------------------------------------

        // C: ufbxi_read_objects_threaded (ufbx.c:15132-15237). C dispatches here from
        // `ufbxi_read_root` whenever a pool is supplied (ufbx.c:15926-15929) rather than to
        // `ufbxi_read_objects`; the port mirrors that dispatch.
        //
        // The shapes that matter here are the ones the *callbacks* observe: one batch per
        // `UFBX_THREAD_GROUP_COUNT` ring entries, each batch's nodes read only after the group
        // holding their tasks has been waited on, and `wait_group`/`wait_all` at the exact points
        // C has them. Two pieces of C have no counterpart and are recorded as such:
        //   * the per-batch `tmp_buf` (`uc->tmp_thread_parse[]`) and the `tmp_buf`-based memory
        //     limit test (ufbx.c:15192) — the port allocates nodes individually, so there is no
        //     batch buffer to measure (PORTING_NOTES.md #4);
        //   * the `uc->ascii.src_buf == tmp_buf` rescue (ufbx.c:15157-15175) exists only because
        //     that buffer gets cleared while the tokenizer may still point into it.
        internal static void ReadObjectsThreaded(UfbxiContext uc)
        {
            UfbxiThreadPool pool = uc.ThreadPool;
            uint groupCount = UfbxiThreadPool.GroupCount;

            uc.ParseThreaded = true;

            // C: ufbxi_object_batch batches[UFBX_THREAD_GROUP_COUNT] (15135-15136)
            List<UfbxiNode>[] batches = new List<UfbxiNode>[groupCount];
            for (int i = 0; i < batches.Length; i++) batches[i] = new List<UfbxiNode>();

            bool parsedToEnd = false;
            uint emptyCount = 0;
            int batchIndex = 0;

            while (emptyCount < groupCount) {
                List<UfbxiNode> batch = batches[batchIndex];

                // C: ufbxi_thread_pool_wait_group (15140)
                pool.WaitGroup();

                if (batch.Count > 0) {
                    for (int i = 0; i < batch.Count; i++) {
                        // Push a deferred element ID for tagging warnings (ufbx.c:15151-15155)
                        uc.TmpElementIds.Add(UfbxConstants.NoIndex);
                        uc.PElementIdIndex = uc.TmpElementIds.Count - 1;
                        uc.Warnings.DeferredElementIdPlusOne = (uint)uc.TmpElementIds.Count;

                        UfbxiReadElement.ReadObject(uc, batch[i]);

                        uc.Warnings.DeferredElementIdPlusOne = 0;
                        uc.PElementIdIndex = -1;
                    }
                    batch.Clear();
                }

                if (!parsedToEnd) {
                    uint taskStart = pool.StartIndex;
                    // C: max_tasks = min(num_tasks / GROUPS, ufbxi_thread_pool_available_tasks())
                    // (15191-15193). Both bounds are about *tasks*, not nodes.
                    uint maxTasks = pool.NumTasks / groupCount;
                    uint available = pool.AvailableTasks();
                    if (available < maxTasks) maxTasks = available;

                    for (;;) {
                        UfbxiNode node = UfbxiRoot.ParseToplevelChildOwned(uc);
                        if (node == null) {
                            parsedToEnd = true;
                            break;
                        }
                        batch.Add(node);

                        uint numTasks = pool.StartIndex - taskStart;
                        if (numTasks >= maxTasks) break;
                    }
                }

                // C: ufbxi_thread_pool_flush_group (15223)
                pool.FlushGroup();

                if (batch.Count == 0) emptyCount += 1;
                batchIndex = (int)((batchIndex + 1) % groupCount);
            }

            // C: ufbxi_thread_pool_wait_all (15232)
            pool.WaitAll();

            uc.ParseThreaded = false;
        }

        // ------------------------------------------------------------------
        // Cross-module helpers (S2-geometry owns ufbx.c:12662-14108 — Parse/Geometry.cs).
        // ------------------------------------------------------------------
    }
}
