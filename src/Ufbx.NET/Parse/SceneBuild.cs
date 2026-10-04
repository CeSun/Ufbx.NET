// Scene-build (first half) ported from ufbx v0.23.1 ufbx.c. Owned by the S3a porting agent
// (2026-10-03 wave). Scope (C line numbers):
//   ufbxi_update_vertex_first_index        (16675-16688)
//   ufbxi_finalize_mesh                    (16690-16764)
//   ufbxi_pivot_nonzero / ufbxi_pivot_div  (18091-18106)
//   ufbxi_pre_finalize_scene               (18115-18542)
//   ufbxi_find_element_by_fbx_id           (18546-18553)
//   cmp_name_element_less(_ref)            (18555-18569)
//   cmp_prop_less_ref / _concat            (18571-18581)
//   ufbxi_sort_name_elements               (18583-18589)
//   ufbxi_cmp_node_less / sort_node_ptrs   (18591-18617)
//   cmp_tmp_material_texture_less/sort     (18619-18632)
//   ufbxi_cmp_connection_less / sort       (18637-18652)
//   ufbxi_find_attribute_fbx_id            (18654-18662)
//   ufbxi_resolve_connections              (18664-18779)
//   ufbxi_add_connections_to_elements      (18781-18911)
//   ufbxi_linearize_nodes                  (18913-18993)
//   find_dst/src_connections               (18996-19032)
//   ufbxi_get_element_node                 (19034-19044)
//   ufbxi_fetch_dst/src_elements           (19046-19120)
//   ufbxi_fetch_dst/src_element            (19122-19148)
//   ufbxi_fetch_textures                   (19150-19172)
//   ufbxi_fetch_mesh_materials             (19174-19196)
//   ufbxi_fetch_deformers                  (19198-19118)
//   ufbxi_fetch_blend_keyframes            (19220-19238)
//   ufbxi_fetch_texture_layers             (19240-19261)
//   ufbxi_prop_connection_less / find      (19263-19282)
//   ufbxi_patch_index_pointer              (19284-19291)
//   ufbxi_cmp_anim_prop_less / sort        (19293-19305)
//   ufbxi_material_texture_less / sort     (19307-19319)
//   ufbxi_bone_pose_less                   (19321-19326)
//   ufbxi_find_anim_prop_start             (19328-19334)
//   ufbxi_sort_bone_poses                  (19336-19342)
//   ufbxi_sort_skin_weights                (19344-19355)
//   ufbxi_blend_keyframe_less / sort       (19357-19369)
//
// Conventions (PORTING_NOTES.md):
//  * C element pointer order == `element_id` order. Every `ufbx_element* <` comparison and
//    every pointer-keyed binary search over elements uses `ElementId`.
//  * `ufbx_real` is `double`; no `System.Math`/`MathF`, no reordered float evaluation.
//  * `ufbxi_stable_sort` / the `ufbxi_macro_stable_sort` macro are reproduced by
//    `UfbxiSort.StableSort` (blocked insertion + bottom-up ping-pong merge); the binary
//    search macros are transcribed literally in `LowerBoundEq`/`UpperBoundEq`.
//  * Error sites: plain `ufbxi_check` -> `CheckNoDesc`, `ufbxi_check_msg` -> `CheckMsg`.
//  * `ufbx_assert` is compiled out in the reference build (`-DNDEBUG`), so pure assertions
//    are NOT turned into failures; they stay as comments.
//
// Out of scope (finalize-half) functions this file references are reported by the caller; the
// module never calls into `ufbxi_finalize_scene` (S3b).
using System;
using System.Collections.Generic;

namespace Ufbx
{
    // C: ufbxi_pre_connection (ufbx.c:18067-18069).
    internal struct UfbxiPreConnection
    {
        public UfbxElement Src;
        public UfbxElement Dst;
    }

    // C: ufbxi_pre_node (ufbx.c:18071-18080).
    internal struct UfbxiPreNode
    {
        public bool HasConstantScale;
        public bool HasRecursiveScaleHelper;
        public bool HasSkinDeformer;
        public UfbxVec3 ConstantScale;
        public uint ElementId;
        public uint FirstChild;
        public uint NextChild;
        public uint Parent;
    }

    // C: ufbxi_pre_mesh (ufbx.c:18082-18084).
    internal struct UfbxiPreMesh
    {
        public bool HasSkinDeformer;
    }

    // C: ufbxi_pre_anim_value (ufbx.c:18086-18089).
    internal struct UfbxiPreAnimValue
    {
        public bool HasConstantValue;
        public UfbxVec3 ConstantValue;
    }

    // C: ufbxi_tmp_material_texture (ufbx.c:6332-6336). Value type: pushed/pop'd by value and
    // sorted by `ufbxi_sort_tmp_material_textures`.
    internal struct UfbxiTmpMaterialTexture
    {
        public int MaterialId;   // C: int32_t material_id
        public int TextureId;    // C: int32_t texture_id
        public string PropName;  // C: ufbx_string prop_name
    }

    // C: ufbx_connection_list (ufbx.h) — a (data, count) view into the scene-wide connection
    // array. The port adds the offset because its arrays are already the element's slice.
    internal struct UfbxConnectionList
    {
        public UfbxConnection[] Data;
        public int Offset;
        public int Count;

        internal int End => Offset + Count;
    }

    internal static class UfbxiSceneBuild
    {
        // ==================================================================
        // ufbxi_patch_zero / ufbxi_finalize_mesh (ufbx.c:16670-16764)
        // ==================================================================

        // C: ufbxi_patch_zero(dst, src) (ufbx.c:16670-16673). The assertion
        // (`dst == 0 || dst == src`) is a no-op in the reference build; the assignment is all.
        static void PatchZero(ref int dst, int src) => dst = src;

        // C: ufbxi_max32 (ufbx.c). No shared port helper exists, so keep it local.
        static uint MaxU32(uint a, uint b) => a > b ? a : b;

        // C: ufbx_vec3.v[index] — the union-array view of a `ufbx_vec3`.
        static double Vec3At(UfbxVec3 v, uint index)
        {
            switch (index) {
            case 1: return v.Y;
            case 2: return v.Z;
            default: return v.X;
            }
        }

        // C: `pre_value->constant_value.v[index] = ...`
        static void Vec3SetAt(ref UfbxVec3 v, uint index, double value)
        {
            switch (index) {
            case 1: v.Y = value; break;
            case 2: v.Z = value; break;
            default: v.X = value; break;
            }
        }

        // C: ufbxi_update_vertex_first_index (ufbx.c:16675-16688).
        internal static void UpdateVertexFirstIndex(UfbxMesh mesh)
        {
            uint[] first = mesh.VertexFirstIndex;
            if (first != null) {
                for (int i = 0; i < first.Length; i++) first[i] = UfbxConstants.NoIndex;
            }

            uint numVertices = (uint)mesh.NumVertices;
            uint[] indices = mesh.VertexIndices;
            if (indices == null) return;
            for (int ix = 0; ix < mesh.NumIndices; ix++) {
                uint vx = indices[ix];
                if (vx < numVertices && mesh.VertexFirstIndex[vx] == UfbxConstants.NoIndex) {
                    mesh.VertexFirstIndex[vx] = (uint)ix;
                }
            }
        }

        // C: ufbxi_finalize_mesh (ufbx.c:16690-16764). The C `buf`/`error` arguments only
        // matter for allocation failure, which the port's arrays cannot have.
        internal static void FinalizeMesh(UfbxMesh mesh)
        {
            if (mesh.Vertices == null || mesh.Vertices.Length == 0) {
                mesh.Vertices = mesh.VertexPosition.Values;
            }
            if (mesh.VertexIndices == null || mesh.VertexIndices.Length == 0) {
                mesh.VertexIndices = mesh.VertexPosition.Indices;
            }

            PatchZero(ref mesh.NumVertices, mesh.Vertices != null ? mesh.Vertices.Length : 0);
            PatchZero(ref mesh.NumIndices, mesh.VertexIndices != null ? mesh.VertexIndices.Length : 0);
            PatchZero(ref mesh.NumFaces, mesh.Faces != null ? mesh.Faces.Length : 0);

            if (mesh.NumTriangles == 0 || mesh.MaxFaceTriangles == 0) {
                int numTriangles = 0;
                int maxFaceTriangles = 0;
                int[] numBadFaces = new int[3];
                if (mesh.Faces != null) {
                    for (int fi = 0; fi < mesh.Faces.Length; fi++) {
                        UfbxFace face = mesh.Faces[fi];
                        if (face.NumIndices >= 3) {
                            int tris = (int)face.NumIndices - 2;
                            numTriangles += tris;
                            if (tris > maxFaceTriangles) maxFaceTriangles = tris;
                        } else {
                            numBadFaces[face.NumIndices]++;
                        }
                    }
                }

                PatchZero(ref mesh.NumTriangles, numTriangles);
                PatchZero(ref mesh.MaxFaceTriangles, maxFaceTriangles);
                PatchZero(ref mesh.NumEmptyFaces, numBadFaces[0]);
                PatchZero(ref mesh.NumPointFaces, numBadFaces[1]);
                PatchZero(ref mesh.NumLineFaces, numBadFaces[2]);
            }

            if (!mesh.SkinnedPosition.Exists) {
                mesh.SkinnedIsLocal = true;
                mesh.SkinnedPosition = mesh.VertexPosition;
                mesh.SkinnedNormal = mesh.VertexNormal;
            }

            if (mesh.VertexFirstIndex == null || mesh.VertexFirstIndex.Length == 0) {
                mesh.VertexFirstIndex = new uint[mesh.NumVertices];
                UpdateVertexFirstIndex(mesh);
            }

            if ((mesh.UvSets == null || mesh.UvSets.Length == 0) && mesh.VertexUv.Exists) {
                UfbxUvSet uvSet = default;
                uvSet.Name = string.Empty;
                uvSet.VertexUv = mesh.VertexUv;
                uvSet.VertexTangent = mesh.VertexTangent;
                uvSet.VertexBitangent = mesh.VertexBitangent;

                mesh.UvSets = new UfbxUvSet[1];
                mesh.UvSets[0] = uvSet;
            }

            if ((mesh.ColorSets == null || mesh.ColorSets.Length == 0) && mesh.VertexColor.Exists) {
                UfbxColorSet colorSet = default;
                colorSet.Name = string.Empty;
                colorSet.VertexColor = mesh.VertexColor;

                mesh.ColorSets = new UfbxColorSet[1];
                mesh.ColorSets[0] = colorSet;
            }

            UfbxiGeometry.PatchMeshReals(mesh);
        }

        // ==================================================================
        // ufbxi_pre_finalize_scene (ufbx.c:18091-18542)
        // ==================================================================

        // C: ufbxi_pivot_nonzero (ufbx.c:18091-18096).
        internal static bool PivotNonzero(UfbxVec3 offset)
        {
            const double epsilon = 0.0009765625;
            return UfbxMath.Abs(offset.X) >= epsilon
                || UfbxMath.Abs(offset.Y) >= epsilon
                || UfbxMath.Abs(offset.Z) >= epsilon;
        }

        // C: ufbxi_pivot_div (ufbx.c:18098-18106).
        internal static double PivotDiv(double offset, double initialScale)
        {
            const double epsilon = 0.0078125;
            if (UfbxMath.Abs(initialScale) >= epsilon) {
                return offset / initialScale;
            } else {
                return offset;
            }
        }

        // C: ufbxi_pre_finalize_scene (ufbx.c:18115-18542).
        internal static void PreFinalizeScene(UfbxiContext uc)
        {
            bool required = false;
            if (uc.Opts.GeometryTransformHandling == UfbxGeometryTransformHandling.HelperNodes
                || uc.Opts.GeometryTransformHandling == UfbxGeometryTransformHandling.ModifyGeometry) required = true;
            if (uc.Opts.InheritModeHandling == UfbxInheritModeHandling.HelperNodes
                || uc.Opts.InheritModeHandling == UfbxInheritModeHandling.Compensate
                || uc.Opts.InheritModeHandling == UfbxInheritModeHandling.CompensateNoFallback) required = true;
            if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToPivot
                || uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToRotationPivot) required = true;

            if (!required) return;

            uint numElements = uc.NumElements;
            int numNodes = uc.TmpNodeIds.Count;

            // C: ufbxi_push_pop(&uc->tmp_parse, &uc->tmp_element_ptrs, ufbx_element*, num_elements).
            // The port keeps `TmpElementPtrs` as the persistent element registry (PORTING_NOTES:
            // the arena "pop" is not ported); taking the first `num_elements` is the snapshot C
            // stores, and the later helper elements stay out of this local array exactly like C.
            UfbxElement[] elements = new UfbxElement[numElements];
            for (int i = 0; i < numElements; i++) elements[i] = uc.TmpElementPtrs[i];

            int numConnections = uc.TmpConnections.Count;
            // C: ufbxi_push_peek(...) — copies, does NOT pop (the helper connections appended
            // during this function are consumed later by ufbxi_resolve_connections).
            List<UfbxiTmpConnection> tmpConnections = uc.TmpConnections;

            UfbxiPreConnection[] preConnections = new UfbxiPreConnection[numConnections];
            uint[] instanceCounts = new uint[numElements];
            bool[] modifyNotSupported = new bool[numElements];
            UfbxElementType[] nodeAttribType = new UfbxElementType[numNodes];
            bool[] hasUnscaledChildren = new bool[numNodes];
            UfbxiPreNode[] preNodes = new UfbxiPreNode[numNodes];

            int numMeshes = uc.TmpTypedElementCount[(int)UfbxElementType.Mesh];
            UfbxiPreMesh[] preMeshes = new UfbxiPreMesh[numMeshes];

            int numAnimValues = uc.TmpTypedElementCount[(int)UfbxElementType.AnimValue];
            UfbxiPreAnimValue[] preAnimValues = new UfbxiPreAnimValue[numAnimValues];

            ulong[] fbxIds = new ulong[numElements];
            for (int i = 0; i < numElements; i++) fbxIds[i] = uc.TmpElementFbxIds[i];

            // C writes these as *float* literals into `const ufbx_real` (double), so the values are
            // the widened float32 results, not the decimal doubles: (double)0.01f =
            // 0.009999999776482582 is strictly below the double 0.01 that an ASCII "Lcl Scaling 0.01"
            // parses to, which is what makes `fabs(scale.x) <= compensate_epsilon` false on
            // maya_human_ik_7400_ascii.fbx under COMPENSATE. Keep the `f` suffixes.
            const double scaleEpsilon = (double)0.001f;
            const double pivotEpsilon = (double)0.001f;
            const double compensateEpsilon = (double)0.01f;

            for (int i = 0; i < numElements; i++) {
                UfbxElement element = elements[i];
                uint id = element.TypedId;

                if (element.Type == UfbxElementType.Node) {
                    ref UfbxiPreNode preNode = ref preNodes[id];
                    preNode.HasConstantScale = true;
                    preNode.ConstantScale = UfbxiProperties.FindVec3(element.Props, UfbxiStrings.Lcl_Scaling, 1.0, 1.0, 1.0);
                    preNode.ElementId = element.ElementId;
                    preNode.FirstChild = ~0u;
                    preNode.NextChild = ~0u;
                    preNode.Parent = ~0u;
                }
                if (element.Type == UfbxElementType.AnimValue) {
                    ref UfbxiPreAnimValue preValue = ref preAnimValues[id];
                    preValue.HasConstantValue = true;
                    preValue.ConstantValue.X = UfbxiProperties.FindReal(element.Props, UfbxiStrings.X, double.NaN);
                    preValue.ConstantValue.X = UfbxiProperties.FindReal(element.Props, UfbxiStrings.d_X, preValue.ConstantValue.X);
                    preValue.ConstantValue.Y = UfbxiProperties.FindReal(element.Props, UfbxiStrings.Y, double.NaN);
                    preValue.ConstantValue.Y = UfbxiProperties.FindReal(element.Props, UfbxiStrings.d_Y, preValue.ConstantValue.Y);
                    preValue.ConstantValue.Z = UfbxiProperties.FindReal(element.Props, UfbxiStrings.Z, double.NaN);
                    preValue.ConstantValue.Z = UfbxiProperties.FindReal(element.Props, UfbxiStrings.d_Z, preValue.ConstantValue.Z);
                }
            }

            // First connection pass (ufbx.c:18197-18281).
            for (int i = 0; i < numConnections; i++) {
                UfbxiTmpConnection tmp = tmpConnections[i];
                UfbxElement src = FindElementByFbxIdIn(uc, elements, tmp.Src);
                UfbxElement dst = FindElementByFbxIdIn(uc, elements, tmp.Dst);
                preConnections[i].Src = src;
                preConnections[i].Dst = dst;
                if (src == null || dst == null) continue;

                if (tmp.SrcProp.Length == 0 && tmp.DstProp.Length == 0) {
                    // Count number of instances of each attribute
                    if (dst.Type == UfbxElementType.Node) {
                        UfbxNode dstNode = (UfbxNode)dst;

                        if (src.Type >= UfbxElementType.FirstAttrib && src.Type <= UfbxElementType.LastAttrib) {
                            uint count = ++instanceCounts[src.ElementId];
                            nodeAttribType[dst.TypedId] = count == 1 ? src.Type : UfbxElementType.Unknown;

                            // These must match what can be transformed in `ufbxi_modify_geometry()`
                            switch (src.Type) {
                            case UfbxElementType.Mesh:
                            case UfbxElementType.LineCurve:
                            case UfbxElementType.NurbsCurve:
                            case UfbxElementType.NurbsSurface:
                                break; // Nop, supported
                            default:
                                modifyNotSupported[dst.ElementId] = true;
                                break;
                            }
                        }

                        if (src.Type == UfbxElementType.Node) {
                            UfbxNode srcNode = (UfbxNode)src;
                            ref UfbxiPreNode preDst = ref preNodes[dstNode.TypedId];
                            ref UfbxiPreNode preSrc = ref preNodes[srcNode.TypedId];

                            // Remember parent and add children into a linked list
                            if (preSrc.Parent == ~0u) {
                                preSrc.Parent = dstNode.TypedId;
                                preSrc.NextChild = preDst.FirstChild;
                                preDst.FirstChild = srcNode.TypedId;
                            }

                            if (uc.Opts.InheritModeHandling != UfbxInheritModeHandling.Preserve) {
                                if (!dstNode.IsRoot && srcNode.OriginalInheritMode != UfbxInheritMode.Normal) {
                                    hasUnscaledChildren[dst.TypedId] = true;
                                }
                            }
                        }
                    } else if (dst.Type == UfbxElementType.Mesh) {
                        if (src.Type == UfbxElementType.SkinDeformer) {
                            ref UfbxiPreMesh preMesh = ref preMeshes[dst.TypedId];
                            preMesh.HasSkinDeformer = true;
                        }
                    }
                } else if (tmp.SrcProp.Length == 0 && tmp.DstProp.Length != 0) {
                    string dstProp = tmp.DstProp;
                    if (dst.Type == UfbxElementType.AnimValue && src.Type == UfbxElementType.AnimCurve) {
                        UfbxAnimCurve srcCurve = (UfbxAnimCurve)src;
                        uint index = 0;
                        if (dstProp == UfbxiStrings.Y || dstProp == UfbxiStrings.d_Y) {
                            index = 1;
                        } else if (dstProp == UfbxiStrings.Z || dstProp == UfbxiStrings.d_Z) {
                            index = 2;
                        }

                        ref UfbxiPreAnimValue preValue = ref preAnimValues[dst.TypedId];
                        if (srcCurve.MaxValue - srcCurve.MinValue >= scaleEpsilon) {
                            preValue.HasConstantValue = false;
                        } else {
                            double constantValue = (srcCurve.MinValue + srcCurve.MaxValue) * 0.5;
                            if (UfbxMath.IsNan(Vec3At(preValue.ConstantValue, index))) {
                                Vec3SetAt(ref preValue.ConstantValue, index, constantValue);
                            }
                            if (UfbxMath.Abs(Vec3At(preValue.ConstantValue, index) - constantValue) > scaleEpsilon) {
                                preValue.HasConstantValue = false;
                            }
                        }
                    }
                }
            }

            // Second connection pass (ufbx.c:18283-18327).
            for (int i = 0; i < numConnections; i++) {
                UfbxiTmpConnection tmp = tmpConnections[i];
                UfbxElement src = preConnections[i].Src;
                UfbxElement dst = preConnections[i].Dst;
                if (src == null || dst == null) continue;

                if (tmp.SrcProp.Length == 0 && tmp.DstProp.Length == 0) {
                    // Count maximum number of instanced attributes in a node
                    if (dst.Type == UfbxElementType.Node) {
                        if (src.Type >= UfbxElementType.FirstAttrib && src.Type <= UfbxElementType.LastAttrib) {
                            instanceCounts[dst.ElementId] = MaxU32(instanceCounts[dst.ElementId], instanceCounts[src.ElementId]);
                            if (src.Type == UfbxElementType.Mesh) {
                                if (preMeshes[src.TypedId].HasSkinDeformer) {
                                    preNodes[dst.TypedId].HasSkinDeformer = true;
                                }
                            }
                        } else if (src.Type == UfbxElementType.SkinDeformer) {
                            preNodes[dst.TypedId].HasSkinDeformer = true;
                        }
                    }
                } else if (tmp.SrcProp.Length == 0 && tmp.DstProp.Length != 0) {
                    if (dst.Type == UfbxElementType.Node) {
                        if (src.Type == UfbxElementType.AnimValue) {
                            if (tmp.DstProp == UfbxiStrings.Lcl_Scaling) {
                                ref UfbxiPreNode preNode = ref preNodes[dst.TypedId];
                                if (preNode.HasConstantScale) {
                                    UfbxiPreAnimValue preValue = preAnimValues[src.TypedId];
                                    if (!preValue.HasConstantValue) {
                                        preNode.HasConstantScale = false;
                                    } else {
                                        double error = 0.0;
                                        error += UfbxMath.Abs(preValue.ConstantValue.X - preNode.ConstantScale.X);
                                        error += UfbxMath.Abs(preValue.ConstantValue.Y - preNode.ConstantScale.Y);
                                        error += UfbxMath.Abs(preValue.ConstantValue.Z - preNode.ConstantScale.Z);
                                        if (error >= scaleEpsilon) {
                                            preNode.HasConstantScale = false;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Pivot handling (ufbx.c:18329-18457).
            if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToPivot
                || uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToRotationPivot) {
                for (int i = 0; i < numNodes; i++) {
                    UfbxiPreNode preNode = preNodes[i];
                    UfbxNode node = (UfbxNode)elements[preNode.ElementId];

                    UfbxVec3 rotationPivot = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.RotationPivot, 0.0, 0.0, 0.0);
                    UfbxVec3 scalingPivot = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.ScalingPivot, 0.0, 0.0, 0.0);
                    UfbxVec3 scalingOffset = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.ScalingOffset, 0.0, 0.0, 0.0);

                    bool shouldModifyPivot = false;
                    if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToPivot) {
                        shouldModifyPivot = !UfbxiProperties.IsVec3Zero(rotationPivot);
                    } else if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToRotationPivot) {
                        shouldModifyPivot = PivotNonzero(rotationPivot) || PivotNonzero(scalingPivot) || PivotNonzero(scalingOffset);
                    }

                    if (shouldModifyPivot) {
                        bool skipGeometryTransform = false;
                        bool canModifyGeometryTransform = true;
                        if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToRotationPivot) {
                            if (nodeAttribType[node.TypedId] == UfbxElementType.Empty) {
                                if (!uc.Opts.PivotHandlingRetainEmpties) {
                                    skipGeometryTransform = true;
                                } else {
                                    canModifyGeometryTransform = false;
                                }
                            }
                        }

                        if (uc.Opts.GeometryTransformHandling == UfbxGeometryTransformHandling.ModifyGeometryNoFallback) {
                            if (instanceCounts[node.ElementId] > 1 || modifyNotSupported[node.ElementId]) {
                                canModifyGeometryTransform = false;
                            }
                        }
                        // Currently, geometry transform messes up skinning
                        if (preNode.HasSkinDeformer) {
                            canModifyGeometryTransform = false;
                        }

                        bool canModifyPivot = true;
                        if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToPivot) {
                            double err = 0.0;
                            err += UfbxMath.Abs(rotationPivot.X - scalingPivot.X);
                            err += UfbxMath.Abs(rotationPivot.Y - scalingPivot.Y);
                            err += UfbxMath.Abs(rotationPivot.Z - scalingPivot.Z);
                            if (err > pivotEpsilon) {
                                canModifyPivot = false;
                            }
                        }

                        if (canModifyPivot && (canModifyGeometryTransform || skipGeometryTransform)) {
                            UfbxVec3 geometricTranslation = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.GeometricTranslation, 0.0, 0.0, 0.0);

                            UfbxVec3 childOffset = UfbxVec3.Zero;
                            int numProps = node.Props.Props != null ? node.Props.Props.Length : 0;
                            int newPropCount = numProps;
                            UfbxProp[] newProps = null;
                            if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToPivot) {
                                // C: ufbx_assert(!skip_geometry_transform)
                                childOffset = UfbxVec3.Neg3(rotationPivot);
                                geometricTranslation = UfbxVec3.Add3(geometricTranslation, childOffset);

                                newProps = new UfbxProp[numProps + 3];
                                if (numProps > 0) Array.Copy(node.Props.Props, 0, newProps, 0, numProps);

                                UfbxiReadElement.InitSyntheticVec3Prop(ref newProps[newPropCount++], UfbxiStrings.RotationPivot, UfbxVec3.Zero, UfbxPropType.Vector);
                                UfbxiReadElement.InitSyntheticVec3Prop(ref newProps[newPropCount++], UfbxiStrings.ScalingPivot, UfbxVec3.Zero, UfbxPropType.Vector);
                                UfbxiReadElement.InitSyntheticVec3Prop(ref newProps[newPropCount++], UfbxiStrings.GeometricTranslation, geometricTranslation, UfbxPropType.Vector);
                            } else if (uc.Opts.PivotHandling == UfbxPivotHandling.AdjustToRotationPivot) {
                                UfbxVec3 initialScale = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.Lcl_Scaling, 1.0, 1.0, 1.0);
                                UfbxVec3 scaledOffset = UfbxVec3.Sub3(UfbxVec3.Add3(scalingOffset, scalingPivot), rotationPivot);
                                UfbxVec3 unscaledOffset;
                                unscaledOffset.X = PivotDiv(scaledOffset.X, initialScale.X);
                                unscaledOffset.Y = PivotDiv(scaledOffset.Y, initialScale.Y);
                                unscaledOffset.Z = PivotDiv(scaledOffset.Z, initialScale.Z);

                                // Convert `scaled_offset + S*unscaled_offset` to FBX scaling pivot and offset.
                                UfbxVec3 newScalingPivot = unscaledOffset;
                                UfbxVec3 newScalingOffset = UfbxVec3.Sub3(scaledOffset, newScalingPivot);
                                childOffset = UfbxVec3.Sub3(unscaledOffset, scalingPivot);

                                newProps = new UfbxProp[numProps + 4];
                                if (numProps > 0) Array.Copy(node.Props.Props, 0, newProps, 0, numProps);

                                UfbxiReadElement.InitSyntheticVec3Prop(ref newProps[newPropCount++], UfbxiStrings.RotationPivot, UfbxVec3.Zero, UfbxPropType.Vector);
                                UfbxiReadElement.InitSyntheticVec3Prop(ref newProps[newPropCount++], UfbxiStrings.ScalingPivot, newScalingPivot, UfbxPropType.Vector);
                                UfbxiReadElement.InitSyntheticVec3Prop(ref newProps[newPropCount++], UfbxiStrings.ScalingOffset, newScalingOffset, UfbxPropType.Vector);
                                if (!skipGeometryTransform) {
                                    geometricTranslation = UfbxVec3.Add3(geometricTranslation, childOffset);
                                    UfbxiReadElement.InitSyntheticVec3Prop(ref newProps[newPropCount++], UfbxiStrings.GeometricTranslation, geometricTranslation, UfbxPropType.Vector);
                                }
                            }

                            // C sets `count = new_prop_count` while the allocation stays larger;
                            // the port's arrays carry the count in `Length`, so shrink to match.
                            if (newPropCount != newProps.Length) {
                                Array.Resize(ref newProps, newPropCount);
                            }
                            node.Props.Props = newProps;
                            UfbxiProperties.SortProperties(node.Props.Props);
                            UfbxiProperties.DeduplicateProperties(node.Props);

                            node.AdjustPreTranslation = UfbxVec3.Add3(node.AdjustPreTranslation, rotationPivot);
                            node.HasAdjustTransform = true;
                            uint ix = preNode.FirstChild;
                            while (ix != ~0u) {
                                UfbxiPreNode preChild = preNodes[ix];
                                UfbxNode child = (UfbxNode)elements[preChild.ElementId];

                                child.AdjustPreTranslation = UfbxVec3.Add3(child.AdjustPreTranslation, childOffset);
                                child.HasAdjustTransform = true;

                                ix = preChild.NextChild;
                            }
                        }
                    }
                }
            }

            // Geometry transform helpers (ufbx.c:18459-18476).
            for (int i = 0; i < numElements; i++) {
                UfbxElement element = elements[i];
                ulong fbxId = fbxIds[i];

                if (element.Type == UfbxElementType.Node) {
                    UfbxNode node = (UfbxNode)element;
                    bool requiresHelperNode = false;
                    if (uc.Opts.GeometryTransformHandling == UfbxGeometryTransformHandling.HelperNodes) {
                        requiresHelperNode = true;
                    } else if (uc.Opts.GeometryTransformHandling == UfbxGeometryTransformHandling.ModifyGeometry) {
                        // Setup a geometry transform helper for nodes that have instanced attributes
                        requiresHelperNode = instanceCounts[i] > 1 || modifyNotSupported[i];
                    }
                    if (requiresHelperNode) {
                        UfbxiReadElement.SetupGeometryTransformHelper(uc, node, fbxId);
                    }
                }
            }

            // Scale helpers (ufbx.c:18478-18539).
            for (int i = 0; i < numElements; i++) {
                UfbxElement element = elements[i];
                ulong fbxId = fbxIds[i];

                if (element.Type == UfbxElementType.Node) {
                    UfbxNode node = (UfbxNode)element;
                    if (hasUnscaledChildren[node.TypedId] && node.ScaleHelper == null) {
                        UfbxiPreNode preNode = preNodes[node.TypedId];
                        double reflect = uc.Opts.InheritModeHandling == UfbxInheritModeHandling.Compensate
                            ? preNode.ConstantScale.X : 1.0;
                        UfbxVec3 scale = preNode.ConstantScale;
                        double dx = UfbxMath.Abs(scale.X - reflect);
                        double dy = UfbxMath.Abs(scale.Y - reflect);
                        double dz = UfbxMath.Abs(scale.Z - reflect);
                        if ((dx + dy + dz >= scaleEpsilon || !preNode.HasConstantScale || UfbxMath.Abs(scale.X) <= compensateEpsilon)
                                && uc.Opts.InheritModeHandling != UfbxInheritModeHandling.CompensateNoFallback) {
                            UfbxiReadElement.SetupScaleHelper(uc, node, fbxId);

                            // If we added a geometry transform helper that may scale further helpers
                            // recursively for all child nodes using `UFBX_INHERIT_MODE_COMPONENTWISE_SCALE`
                            // This is guaranteed to terminate as `ufbxi_pre_node` may only have one parent,
                            // meaning any cycles must contain `node` itself.
                            uint ix = preNode.FirstChild;
                            while (ix != ~0u && ix != node.TypedId) {
                                UfbxiPreNode preChild = preNodes[ix];
                                UfbxNode child = (UfbxNode)elements[preChild.ElementId];

                                if (preChild.Parent != node.TypedId || child.OriginalInheritMode == UfbxInheritMode.ComponentwiseScale) {
                                    if (!preChild.HasRecursiveScaleHelper && child.OriginalInheritMode != UfbxInheritMode.Normal) {
                                        preChild.HasRecursiveScaleHelper = true;
                                        preNodes[ix] = preChild;

                                        ulong childFbxId = fbxIds[preChild.ElementId];
                                        UfbxiReadElement.SetupScaleHelper(uc, child, childFbxId);
                                        child.IsScaleCompensateParent = false;

                                        // Traverse to children if any
                                        if (preChild.FirstChild != ~0u) {
                                            ix = preChild.FirstChild;
                                            continue;
                                        }
                                    }
                                }

                                // Move to next child, popping parents until we find one
                                while (preChild.NextChild == ~0u) {
                                    ix = preChild.Parent;
                                    if (ix == node.TypedId) break;
                                    preChild = preNodes[ix];
                                }
                                if (ix != node.TypedId) {
                                    ix = preChild.NextChild;
                                }
                            }
                        } else if (uc.Opts.InheritModeHandling == UfbxInheritModeHandling.Compensate
                                || uc.Opts.InheritModeHandling == UfbxInheritModeHandling.CompensateNoFallback) {
                            if (UfbxMath.Abs(scale.X - 1.0) >= scaleEpsilon) {
                                node.IsScaleCompensateParent = true;
                            }
                        }
                    }
                }
            }
        }

        // ==================================================================
        // Element lookup + comparison/sort helpers (ufbx.c:18546-18662)
        // ==================================================================

        // C: ufbxi_find_element_by_fbx_id (ufbx.c:18546-18553). C indexes
        // `uc->scene.elements.data`; the port's `TmpElementPtrs` is the same element_id-ordered
        // registry and is valid before `ufbxi_finalize_scene` populates `scene.elements`.
        internal static UfbxElement FindElementByFbxId(UfbxiContext uc, ulong fbxId)
        {
            int index = UfbxiFbxId.FindFbxId(uc, fbxId);
            if (index < 0) return null;
            return uc.TmpElementPtrs[(int)uc.FbxIdMap.Items[index].ElementId];
        }

        // C: the local-array variant used while `ufbxi_pre_finalize_scene` holds its snapshot.
        static UfbxElement FindElementByFbxIdIn(UfbxiContext uc, UfbxElement[] elements, ulong fbxId)
        {
            int index = UfbxiFbxId.FindFbxId(uc, fbxId);
            if (index < 0) return null;
            return elements[(int)uc.FbxIdMap.Items[index].ElementId];
        }

        // C: ufbxi_cmp_name_element_less (ufbx.c:18555-18561).
        internal static bool CmpNameElementLess(UfbxNameElement a, UfbxNameElement b)
        {
            if (a.InternalKey != b.InternalKey) return a.InternalKey < b.InternalKey;
            int cmp = UfbxiProperties.Strcmp(a.Name, b.Name);
            if (cmp != 0) return cmp < 0;
            return (int)a.Type < (int)b.Type;
        }

        // C: ufbxi_cmp_name_element_less_ref (ufbx.c:18563-18569).
        internal static bool CmpNameElementLessRef(UfbxNameElement a, string name, UfbxElementType type, uint key)
        {
            if (a.InternalKey != key) return a.InternalKey < key;
            int cmp = UfbxiStr.Cmp(a.Name, name);
            if (cmp != 0) return cmp < 0;
            return (int)a.Type < (int)type;
        }

        // C: ufbxi_cmp_prop_less_ref (ufbx.c:18571-18575).
        internal static bool CmpPropLessRef(UfbxProp a, string name, uint key)
        {
            if (a.InternalKey != key) return a.InternalKey < key;
            return UfbxiStr.Less(a.Name, name);
        }

        // C: ufbxi_cmp_prop_less_concat (ufbx.c:18577-18581).
        internal static bool CmpPropLessConcat(UfbxProp a, UfbxiConcatPart[] parts, int numParts, uint key)
        {
            if (a.InternalKey != key) return a.InternalKey < key;
            return UfbxiStr.ConcatStrCmp(a.Name, parts, numParts) < 0;
        }

        // C: ufbxi_sort_name_elements (ufbx.c:18583-18589).
        internal static void SortNameElements(UfbxNameElement[] nameElems, int count)
        {
            UfbxNameElement[] tmp = new UfbxNameElement[count];
            UfbxiSort.StableSort(32, nameElems, tmp, count,
                (object user, UfbxNameElement a, UfbxNameElement b) => CmpNameElementLess(a, b), null);
        }

        // C: ufbxi_cmp_node_less (ufbx.c:18591-18609).
        internal static bool CmpNodeLess(UfbxNode a, UfbxNode b)
        {
            if (a.NodeDepth != b.NodeDepth) return a.NodeDepth < b.NodeDepth;
            if (a.Parent != null && b.Parent != null) {
                uint aPid = a.Parent.ElementId, bPid = b.Parent.ElementId;
                if (aPid != bPid) return aPid < bPid;
            }
            // C: ufbx_assert(a->parent == NULL && b->parent == NULL) in the else branch.
            if (a.IsGeometryTransformHelper != b.IsGeometryTransformHelper) {
                // Sort geometry transform helpers always before rest of the children.
                return (a.IsGeometryTransformHelper ? 1u : 0u) > (b.IsGeometryTransformHelper ? 1u : 0u);
            }
            if (a.IsScaleHelper != b.IsScaleHelper) {
                // Sort scale helpers after geometry transform helpers.
                return (a.IsScaleHelper ? 1u : 0u) > (b.IsScaleHelper ? 1u : 0u);
            }
            return a.ElementId < b.ElementId;
        }

        // C: ufbxi_sort_node_ptrs (ufbx.c:18611-18617). Sorts an array of `ufbx_node*`.
        internal static void SortNodePtrs(UfbxNode[] nodes, int count)
        {
            UfbxNode[] tmp = new UfbxNode[count];
            UfbxiSort.StableSort(32, nodes, tmp, count,
                (object user, UfbxNode a, UfbxNode b) => CmpNodeLess(a, b), null);
        }

        // C: ufbxi_cmp_tmp_material_texture_less (ufbx.c:18619-18624).
        internal static bool CmpTmpMaterialTextureLess(UfbxiTmpMaterialTexture a, UfbxiTmpMaterialTexture b)
        {
            if (a.MaterialId != b.MaterialId) return a.MaterialId < b.MaterialId;
            if (a.TextureId != b.TextureId) return a.TextureId < b.TextureId;
            return UfbxiStr.Less(a.PropName, b.PropName);
        }

        // C: ufbxi_sort_tmp_material_textures (ufbx.c:18626-18632).
        internal static void SortTmpMaterialTextures(UfbxiTmpMaterialTexture[] matTexs, int count)
        {
            UfbxiTmpMaterialTexture[] tmp = new UfbxiTmpMaterialTexture[count];
            UfbxiSort.StableSort(32, matTexs, tmp, count,
                (object user, UfbxiTmpMaterialTexture a, UfbxiTmpMaterialTexture b) => CmpTmpMaterialTextureLess(a, b), null);
        }

        // C: ufbxi_cmp_connection_less (ufbx.c:18637-18645). `index` selects which end is the
        // primary key: 0 -> src, 1 -> dst. The `index ^ 1` prop is the tie-breaker.
        internal static bool CmpConnectionLess(UfbxConnection a, UfbxConnection b, int index)
        {
            UfbxElement aElem = index == 0 ? a.Src : a.Dst;
            UfbxElement bElem = index == 0 ? b.Src : b.Dst;
            if (!ReferenceEquals(aElem, bElem)) return aElem.ElementId < bElem.ElementId;
            string aProp0 = index == 0 ? a.SrcProp : a.DstProp;
            string bProp0 = index == 0 ? b.SrcProp : b.DstProp;
            int cmp = UfbxiProperties.Strcmp(aProp0, bProp0);
            if (cmp != 0) return cmp < 0;
            string aProp1 = index == 0 ? a.DstProp : a.SrcProp;
            string bProp1 = index == 0 ? b.DstProp : b.SrcProp;
            cmp = UfbxiProperties.Strcmp(aProp1, bProp1);
            return cmp < 0;
        }

        // C: ufbxi_sort_connections (ufbx.c:18647-18652).
        internal static void SortConnections(UfbxConnection[] connections, int count, int index)
        {
            UfbxConnection[] tmp = new UfbxConnection[count];
            UfbxiSort.StableSort(32, connections, tmp, count,
                (object user, UfbxConnection a, UfbxConnection b) => CmpConnectionLess(a, b, index), null);
        }

        // C: ufbxi_find_attribute_fbx_id (ufbx.c:18654-18662).
        internal static ulong FindAttributeFbxId(UfbxiContext uc, ulong nodeFbxId)
        {
            uint hash = UfbxiHash.Hash64(nodeFbxId);
            int index = uc.FbxAttrMap.Find(hash, nodeFbxId);
            if (index >= 0) {
                return uc.FbxAttrMap.Items[index].AttrFbxId;
            }
            return nodeFbxId;
        }

        // ==================================================================
        // ufbxi_resolve_connections (ufbx.c:18664-18779)
        // ==================================================================

        internal static void ResolveConnections(UfbxiContext uc)
        {
            int numConnections = uc.TmpConnections.Count;
            UfbxiTmpConnection[] tmpConnections = uc.TmpConnections.ToArray();
            uc.TmpConnections.Clear(); // C: ufbxi_push_pop + ufbxi_buf_free(&uc->tmp_connections)

            // NOTE: We truncate this array in case not all connections are resolved
            UfbxConnection[] connectionsSrc = new UfbxConnection[numConnections];

            // HACK: Translate property connections from node to attribute if the property name
            // is not included in the known node properties and is not a property of the node.
            if (uc.Version > 0 && uc.Version < 7000) {
                for (int i = 0; i < numConnections; i++) {
                    UfbxiTmpConnection tmp = tmpConnections[i];
                    if (tmp.SrcProp.Length > 0 && !UfbxiProperties.IsNodePropertyName(uc, tmp.SrcProp)) {
                        UfbxElement src = FindElementByFbxId(uc, tmp.Src);
                        if (src == null || !UfbxiProperties.TryFindPropLen(src.Props, tmp.SrcProp, out _, out _)) {
                            tmp.Src = FindAttributeFbxId(uc, tmp.Src);
                        }
                    }
                    if (tmp.DstProp.Length > 0 && !UfbxiProperties.IsNodePropertyName(uc, tmp.DstProp)) {
                        UfbxElement dst = FindElementByFbxId(uc, tmp.Dst);
                        if (dst == null || !UfbxiProperties.TryFindPropLen(dst.Props, tmp.DstProp, out _, out _)) {
                            tmp.Dst = FindAttributeFbxId(uc, tmp.Dst);
                        }
                    }
                    tmpConnections[i] = tmp;
                }
            }

            int count = 0;
            for (int i = 0; i < numConnections; i++) {
                UfbxiTmpConnection tmp = tmpConnections[i];
                UfbxElement src = FindElementByFbxId(uc, tmp.Src);
                UfbxElement dst = FindElementByFbxId(uc, tmp.Dst);
                if (src == null || dst == null) continue;

                if (!uc.Opts.DisableQuirks) {
                    // Some exporters connect arbitrary non-nodes to root breaking further code, ignore those connections here!
                    if (dst.Type == UfbxElementType.Node && src.Type != UfbxElementType.Node && ((UfbxNode)dst).IsRoot) {
                        UfbxiFail.CheckNoDesc(
                            UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.BadElementConnectedToRoot,
                                src.ElementId, "Non-node element connected to root"),
                            "ufbxi_warnf_tag(UFBX_WARNING_BAD_ELEMENT_CONNECTED_TO_ROOT, src->element_id, \"Non-node element connected to root\")");
                        continue;
                    }
                }

                // Remap connections to geometry transform helpers if necessary, see `ufbxi_setup_geometry_transform_helper()` for how these are setup.
                if (uc.HasGeometryTransformNodes) {
                    if (dst.Type == UfbxElementType.Node && src.Type >= UfbxElementType.FirstAttrib && src.Type <= UfbxElementType.LastAttrib) {
                        UfbxNode node = (UfbxNode)dst;
                        if (node.HasGeometryTransform) {
                            UfbxiNodeExtra extra = uc.GetElementExtra<UfbxiNodeExtra>(node.ElementId);
                            // C: ufbx_assert(extra); ufbx_assert(dst->type == NODE && is_geometry_transform_helper)
                            dst = uc.TmpElementPtrs[(int)extra.GeometryHelperId];
                        }
                    }
                }

                // Remap connections to scale helpers if necessary, see `ufbxi_setup_scale_helper()` for how these are setup.
                if (uc.HasScaleHelperNodes) {
                    if (dst.Type == UfbxElementType.Node) {
                        UfbxNode dstNode = (UfbxNode)dst;
                        if (dstNode.ScaleHelper != null) {
                            if (src.Type == UfbxElementType.Node) {
                                UfbxNode srcNode = (UfbxNode)src;
                                if (!srcNode.IsScaleHelper && srcNode.OriginalInheritMode == UfbxInheritMode.Normal) {
                                    dst = dstNode.ScaleHelper;
                                }
                            } else if (src.Type == UfbxElementType.AnimValue) {
                                if (tmp.DstProp == UfbxiStrings.Lcl_Scaling) {
                                    dst = dstNode.ScaleHelper;
                                }
                            } else {
                                dst = dstNode.ScaleHelper;
                            }
                        }
                    } else if (src.Type == UfbxElementType.Node) {
                        UfbxNode srcNode = (UfbxNode)src;
                        if (srcNode.ScaleHelper != null) {
                            if (dst.Type == UfbxElementType.SkinCluster) {
                                src = srcNode.ScaleHelper;
                            }
                        }
                    }
                }

                // Translate deformers to point to the geometry in 6100, we don't need to worry about
                // blend shapes here as they're always connected synthetically in older files.
                if (uc.Version > 0 && uc.Version < 7000 && dst.Type == UfbxElementType.Node) {
                    if (src.Type == UfbxElementType.SkinDeformer || src.Type == UfbxElementType.CacheDeformer) {
                        ulong dstId = FindAttributeFbxId(uc, tmp.Dst);
                        UfbxElement dstElem = FindElementByFbxId(uc, dstId);
                        if (dstElem != null) {
                            dst = dstElem;
                        }
                    }
                }

                UfbxConnection conn = new UfbxConnection();
                conn.Src = src;
                conn.Dst = dst;
                conn.SrcProp = tmp.SrcProp;
                conn.DstProp = tmp.DstProp;
                connectionsSrc[count++] = conn;
            }

            if (count != connectionsSrc.Length) {
                Array.Resize(ref connectionsSrc, count);
            }
            uc.Scene.ConnectionsSrc = connectionsSrc;
            uc.Scene.ConnectionsDst = (UfbxConnection[])connectionsSrc.Clone();

            SortConnections(uc.Scene.ConnectionsSrc, uc.Scene.ConnectionsSrc.Length, 0);
            SortConnections(uc.Scene.ConnectionsDst, uc.Scene.ConnectionsDst.Length, 1);
        }

        // ==================================================================
        // ufbxi_add_connections_to_elements (ufbx.c:18781-18911)
        // ==================================================================

        internal static void AddConnectionsToElements(UfbxiContext uc)
        {
            UfbxConnection[] connSrc = uc.Scene.ConnectionsSrc ?? Array.Empty<UfbxConnection>();
            UfbxConnection[] connDst = uc.Scene.ConnectionsDst ?? Array.Empty<UfbxConnection>();
            int connSrcLen = connSrc.Length;
            int connDstLen = connDst.Length;

            int connSrcIx = 0, connDstIx = 0;

            // C iterates `uc->scene.elements.data` (count == num_elements, element_id order).
            // `TmpElementPtrs` is the same list before finalize populates `scene.elements`.
            UfbxElement[] elements = uc.TmpElementPtrs.ToArray();
            for (int elemIx = 0; elemIx < elements.Length; elemIx++) {
                UfbxElement elem = elements[elemIx];
                uint id = elem.ElementId;

                while (connSrcIx < connSrcLen && connSrc[connSrcIx].Src.ElementId < id) connSrcIx++;
                while (connDstIx < connDstLen && connDst[connDstIx].Dst.ElementId < id) connDstIx++;
                int srcEnd = connSrcIx, dstEnd = connDstIx;

                while (srcEnd < connSrcLen && connSrc[srcEnd].Src.ElementId == id) srcEnd++;
                while (dstEnd < connDstLen && connDst[dstEnd].Dst.ElementId == id) dstEnd++;

                elem.ConnectionsSrc = Slice(connSrc, connSrcIx, srcEnd - connSrcIx);
                elem.ConnectionsDst = Slice(connDst, connDstIx, dstEnd - connDstIx);

                // Setup animated properties
                {
                    UfbxProp[] props = elem.Props.Props;
                    int propEnd = props != null ? props.Length : 0;
                    int prop = 0;
                    int copyStart = 0;
                    bool needsCopy = false;
                    int numAnimated = 0, numSynthetic = 0;
                    List<UfbxProp> stack = new List<UfbxProp>();

                    for (;;) {
                        // Scan to the next animation connection
                        for (; connDstIx < dstEnd; connDstIx++) {
                            if (connDst[connDstIx].DstProp.Length == 0) continue;
                            if (connDst[connDstIx].SrcProp.Length > 0) break;
                            if (connDst[connDstIx].Src.Type == UfbxElementType.AnimValue) break;
                        }

                        string name = string.Empty;
                        if (connDstIx < dstEnd) {
                            name = connDst[connDstIx].DstProp;
                        }
                        if (name.Length == 0) break;

                        // NOTE: "Animated" properties also include connected ones as we need
                        // to resolve them during evaluation
                        numAnimated++;

                        UfbxAnimValue animValue = null;
                        uint flags = 0;
                        for (; connDstIx < dstEnd && connDst[connDstIx].DstProp == name; connDstIx++) {
                            if (connDst[connDstIx].SrcProp.Length > 0) {
                                flags |= (uint)UfbxPropFlags.Connected;
                            } else if (connDst[connDstIx].Src.Type == UfbxElementType.AnimValue) {
                                animValue = (UfbxAnimValue)connDst[connDstIx].Src;
                                flags |= (uint)UfbxPropFlags.Animated;
                            }
                        }

                        uint key = UfbxiProperties.GetNameKey(name, name.Length);
                        while (prop != propEnd && UfbxiProperties.NameKeyLess(props[prop], name, name.Length, key)) prop++;

                        if (prop != propEnd && props[prop].Name == name) {
                            props[prop].Flags = (UfbxPropFlags)((uint)props[prop].Flags | flags);
                        } else {
                            // Animated property that is not in the element property list
                            // Copy the preceding properties to the stack, then push a
                            // synthetic property for the animated property.
                            for (int k = copyStart; k < prop; k++) stack.Add(props[k]);
                            copyStart = prop;
                            needsCopy = true;

                            // Let's hope we can find the property in the defaults at least
                            bool hasDefProp = false;
                            UfbxProp defProp = default;
                            if (elem.Props.Defaults != null) {
                                int di = UfbxiProperties.FindPropIndex(elem.Props.Defaults, name, key, true, out UfbxProps owner);
                                if (di >= 0) {
                                    defProp = owner.Props[di];
                                    hasDefProp = true;
                                }
                            } else if (animValue != null) {
                                UfbxProp animDefProp = default;
                                // Hack a couple of common types
                                UfbxPropType type = UfbxPropType.Unknown;
                                if (name == UfbxiStrings.Lcl_Translation) type = UfbxPropType.Translation;
                                else if (name == UfbxiStrings.Lcl_Rotation) type = UfbxPropType.Rotation;
                                else if (name == UfbxiStrings.Lcl_Scaling) {
                                    type = UfbxPropType.Scaling;
                                    animDefProp.ValueVec3 = new UfbxVec3(1.0, 1.0, 1.0);
                                }
                                // Property values are only defined in anim_props on legacy files
                                if (uc.Version < 6000) {
                                    animDefProp.ValueVec3 = animValue.DefaultValue;
                                }
                                animDefProp.Type = type;
                                defProp = animDefProp;
                                hasDefProp = true;
                            } else {
                                flags |= (uint)UfbxPropFlags.NoValue;
                            }

                            UfbxProp newProp = default;
                            if (hasDefProp) newProp = defProp;
                            flags |= (uint)newProp.Flags;
                            newProp.Flags = (UfbxPropFlags)((uint)UfbxPropFlags.Animatable | (uint)UfbxPropFlags.Synthetic | flags);
                            newProp.Name = name;
                            newProp.InternalKey = key;
                            newProp.ValueStr = string.Empty;
                            newProp.ValueBlob = null; // C: ufbx_empty_blob
                            stack.Add(newProp);
                            numSynthetic++;
                        }
                    }

                    // Copy the properties if necessary
                    if (needsCopy) {
                        for (int k = copyStart; k < propEnd; k++) stack.Add(props[k]);
                        elem.Props.Props = stack.ToArray();
                    }
                    elem.Props.NumAnimated = numAnimated;
                }

                connSrcIx = srcEnd;
                connDstIx = dstEnd;
            }
        }

        // C: a (data, count) view slice; C's `element->connections_*.data = conn_src` inside the
        // bigger array. The port materializes the sub-array (the connection objects are shared).
        static UfbxConnection[] Slice(UfbxConnection[] src, int offset, int count)
        {
            UfbxConnection[] dst = new UfbxConnection[count];
            for (int i = 0; i < count; i++) dst[i] = src[offset + i];
            return dst;
        }

        // ==================================================================
        // ufbxi_linearize_nodes (ufbx.c:18913-18993)
        // ==================================================================

        internal static void LinearizeNodes(UfbxiContext uc)
        {
            int numNodes = uc.TmpNodeIds.Count;
            uint[] nodeIds = uc.TmpNodeIds.ToArray();
            uc.TmpNodeIds.Clear();

            UfbxNode[] nodePtrs = new UfbxNode[numNodes];
            for (int i = 0; i < numNodes; i++) {
                nodePtrs[i] = (UfbxNode)uc.TmpElementPtrs[(int)nodeIds[i]];
            }

            uc.Scene.RootNode = nodePtrs[0];

            // C: `node_offsets` (the per-type byte-offset list) is a finalize-scene remapping
            // input; the port's element objects already carry `typed_id`, and the final node
            // array is `nodePtrs`, so the offsets carry no port-side observable.

            // Hook up the parent nodes, we'll assume that there's no cycles at this point
            for (int ni = 0; ni < numNodes; ni++) {
                UfbxNode node = nodePtrs[ni];

                // Pre-6000 files don't have any explicit root connections so they must always
                // be connected to the root..
                if (node.Parent == null && !(uc.Opts.AllowNodesOutOfRoot && uc.Version >= 6000)) {
                    if (node != uc.Scene.RootNode) {
                        node.Parent = uc.Scene.RootNode;
                    }
                }

                UfbxConnection[] conns = node.ConnectionsDst;
                if (conns != null) {
                    for (int ci = 0; ci < conns.Length; ci++) {
                        UfbxConnection conn = conns[ci];
                        if (conn.SrcProp.Length > 0 || conn.DstProp.Length > 0) continue;
                        if (conn.Src.Type != UfbxElementType.Node) continue;
                        ((UfbxNode)conn.Src).Parent = node;
                    }
                }
            }

            // Count the parent depths and child amounts
            for (int ni = 0; ni < numNodes; ni++) {
                UfbxNode node = nodePtrs[ni];
                uint depth = 0;

                for (UfbxNode p = node.Parent; p != null; p = p.Parent) {
                    depth += p.NodeDepth + 1;
                    if (p.NodeDepth > 0) break;
                    UfbxiFail.CheckMsg(depth <= (uint)numNodes, "Cyclic node hierarchy");
                }

                if (uc.Opts.NodeDepthLimit > 0) {
                    UfbxiFail.CheckMsg(depth <= uc.Opts.NodeDepthLimit, "Node depth limit exceeded");
                }
                node.NodeDepth = depth;

                // Second pass to cache the depths to avoid O(n^2)
                for (UfbxNode p = node.Parent; p != null; p = p.Parent) {
                    if (--depth <= p.NodeDepth) break;
                    p.NodeDepth = depth;
                }
            }

            SortNodePtrs(nodePtrs, numNodes);

            for (int i = 0; i < numNodes; i++) {
                UfbxNode node = nodePtrs[i];
                node.TypedId = (uint)i;
            }

            uc.Scene.Nodes = nodePtrs;
        }

        // ==================================================================
        // find_dst/src_connections (ufbx.c:18996-19032)
        // ==================================================================

        // C: ufbxi_macro_lower_bound_eq (ufbx.c:1188-1204), transcribed literally. Leaves
        // `result` untouched when no equal element is found (exactly like the macro). Internal so
        // the public lookup entry points ported in Parse/SceneFind.cs reuse this transcription
        // instead of re-declaring the macro.
        internal static void LowerBoundEq<T>(T[] data, int begin, int size, int linearSize,
            Func<T, bool> less, Func<T, bool> eq, ref int result)
        {
            int lo = begin, hi = size;
            while (hi - lo > linearSize) {
                int mid = lo + (hi - lo) / 2;
                if (less(data[mid])) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (eq(data[lo])) { result = lo; break; }
            }
        }

        // C: ufbxi_macro_upper_bound_eq (ufbx.c:1206-1229), transcribed literally.
        internal static void UpperBoundEq<T>(T[] data, int begin, int size, int linearSize,
            Func<T, bool> eq, ref int result)
        {
            int lo = begin, hi = size;
            for (int step = 1; step < 100 && hi - lo > step; step *= 2) {
                if (!eq(data[lo + step])) { hi = lo + step; break; }
                lo += step;
            }
            while (hi - lo > linearSize) {
                int mid = lo + (hi - lo) / 2;
                if (eq(data[mid])) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (!eq(data[lo])) break;
            }
            result = lo;
        }

        // C: ufbxi_find_dst_connections (ufbx.c:18996-19013).
        internal static UfbxConnectionList FindDstConnections(UfbxElement element, string prop)
        {
            if (prop == null) prop = string.Empty;

            UfbxConnection[] data = element.ConnectionsDst ?? Array.Empty<UfbxConnection>();
            int size = data.Length;

            int begin = size, end = begin;

            LowerBoundEq(data, 0, size, 32,
                c => UfbxiProperties.Strcmp(c.DstProp, prop) < 0,
                c => c.DstProp == prop && c.SrcProp.Length == 0,
                ref begin);

            UpperBoundEq(data, begin, size, 32,
                c => c.DstProp == prop && c.SrcProp.Length == 0,
                ref end);

            return new UfbxConnectionList { Data = data, Offset = begin, Count = end - begin };
        }

        // C: ufbxi_find_src_connections (ufbx.c:19015-19032).
        internal static UfbxConnectionList FindSrcConnections(UfbxElement element, string prop)
        {
            if (prop == null) prop = string.Empty;

            UfbxConnection[] data = element.ConnectionsSrc ?? Array.Empty<UfbxConnection>();
            int size = data.Length;

            int begin = size, end = begin;

            LowerBoundEq(data, 0, size, 32,
                c => UfbxiProperties.Strcmp(c.SrcProp, prop) < 0,
                c => c.SrcProp == prop && c.DstProp.Length == 0,
                ref begin);

            UpperBoundEq(data, begin, size, 32,
                c => c.SrcProp == prop && c.DstProp.Length == 0,
                ref end);

            return new UfbxConnectionList { Data = data, Offset = begin, Count = end - begin };
        }

        // C: ufbxi_get_element_node (ufbx.c:19034-19044).
        internal static UfbxElement GetElementNode(UfbxElement element)
        {
            if (element == null) return null;
            if (element.Type == UfbxElementType.Node) {
                UfbxNode node = (UfbxNode)element;
                if (node.IsGeometryTransformHelper) return node.Parent;
                return null;
            } else {
                return element.Instances != null && element.Instances.Length > 0 ? element.Instances[0] : null;
            }
        }

        // ==================================================================
        // ufbxi_fetch_* (ufbx.c:19046-19261)
        // ==================================================================

        // C: ufbxi_fetch_dst_elements (ufbx.c:19046-19082).
        internal static UfbxElement[] FetchDstElements(UfbxiContext uc, UfbxElement element,
            bool searchNode, bool ignoreDuplicates, string prop, UfbxElementType srcType)
        {
            List<UfbxElement> list = new List<UfbxElement>();
            if (ignoreDuplicates) uc.EnsureTmpElementFlag();

            do {
                UfbxConnectionList conns = FindDstConnections(element, prop);
                for (int i = conns.Offset; i < conns.End; i++) {
                    UfbxConnection conn = conns.Data[i];
                    if (conn.Src.Type == srcType) {
                        if (ignoreDuplicates) {
                            uint elementId = conn.Src.ElementId;
                            if (uc.TmpElementFlag[elementId] != 0) {
                                UfbxiFail.CheckNoDesc(
                                    UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.DuplicateConnection, elementId,
                                        "Duplicate connection to %u", new UfbxiVaList().AddUInt(element.ElementId)),
                                    "ufbxi_warnf_tag(UFBX_WARNING_DUPLICATE_CONNECTION, element_id, \"Duplicate connection to %u\", element->element_id)");
                                continue;
                            }
                            uc.TmpElementFlag[elementId] = 1;
                        }
                        list.Add(conn.Src);
                    }
                }
            } while (searchNode && (element = GetElementNode(element)) != null);

            UfbxElement[] result = list.ToArray();

            if (ignoreDuplicates) {
                for (int i = 0; i < result.Length; i++) {
                    uc.TmpElementFlag[result[i].ElementId] = 0;
                }
            }

            return result;
        }

        // C: ufbxi_fetch_src_elements (ufbx.c:19084-19120).
        internal static UfbxElement[] FetchSrcElements(UfbxiContext uc, UfbxElement element,
            bool searchNode, bool ignoreDuplicates, string prop, UfbxElementType dstType)
        {
            List<UfbxElement> list = new List<UfbxElement>();
            if (ignoreDuplicates) uc.EnsureTmpElementFlag();

            do {
                UfbxConnectionList conns = FindSrcConnections(element, prop);
                for (int i = conns.Offset; i < conns.End; i++) {
                    UfbxConnection conn = conns.Data[i];
                    if (conn.Dst.Type == dstType) {
                        if (ignoreDuplicates) {
                            uint elementId = conn.Dst.ElementId;
                            if (uc.TmpElementFlag[elementId] != 0) {
                                UfbxiFail.CheckNoDesc(
                                    UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.DuplicateConnection, elementId,
                                        "Duplicate connection to %u", new UfbxiVaList().AddUInt(element.ElementId)),
                                    "ufbxi_warnf_tag(UFBX_WARNING_DUPLICATE_CONNECTION, element_id, \"Duplicate connection to %u\", element->element_id)");
                                continue;
                            }
                            uc.TmpElementFlag[elementId] = 1;
                        }
                        list.Add(conn.Dst);
                    }
                }
            } while (searchNode && (element = GetElementNode(element)) != null);

            UfbxElement[] result = list.ToArray();

            if (ignoreDuplicates) {
                for (int i = 0; i < result.Length; i++) {
                    uc.TmpElementFlag[result[i].ElementId] = 0;
                }
            }

            return result;
        }

        // C: ufbxi_fetch_dst_element (ufbx.c:19122-19134).
        internal static UfbxElement FetchDstElement(UfbxElement element, bool searchNode, string prop, UfbxElementType srcType)
        {
            do {
                UfbxConnectionList conns = FindDstConnections(element, prop);
                for (int i = conns.Offset; i < conns.End; i++) {
                    UfbxConnection conn = conns.Data[i];
                    if (conn.Src.Type == srcType) {
                        return conn.Src;
                    }
                }
            } while (searchNode && (element = GetElementNode(element)) != null);

            return null;
        }

        // C: ufbxi_fetch_src_element (ufbx.c:19136-19148).
        internal static UfbxElement FetchSrcElement(UfbxElement element, bool searchNode, string prop, UfbxElementType dstType)
        {
            do {
                UfbxConnectionList conns = FindSrcConnections(element, prop);
                for (int i = conns.Offset; i < conns.End; i++) {
                    UfbxConnection conn = conns.Data[i];
                    if (conn.Dst.Type == dstType) {
                        return conn.Dst;
                    }
                }
            } while (searchNode && (element = GetElementNode(element)) != null);

            return null;
        }

        // C: ufbxi_fetch_textures (ufbx.c:19150-19172).
        internal static UfbxMaterialTexture[] FetchTextures(UfbxiContext uc, UfbxElement element, bool searchNode)
        {
            List<UfbxMaterialTexture> list = new List<UfbxMaterialTexture>();

            do {
                UfbxConnection[] conns = element.ConnectionsDst;
                if (conns != null) {
                    for (int i = 0; i < conns.Length; i++) {
                        UfbxConnection conn = conns[i];
                        if (conn.SrcProp.Length > 0) continue;
                        if (conn.Src.Type == UfbxElementType.Texture) {
                            UfbxMaterialTexture tex = new UfbxMaterialTexture();
                            tex.ShaderProp = conn.DstProp;
                            tex.MaterialProp = conn.DstProp;
                            tex.Texture = (UfbxTexture)conn.Src;
                            list.Add(tex);
                        }
                    }
                }
            } while (searchNode && (element = GetElementNode(element)) != null);

            return list.ToArray();
        }

        // C: ufbxi_fetch_mesh_materials (ufbx.c:19174-19196).
        internal static UfbxMaterial[] FetchMeshMaterials(UfbxiContext uc, UfbxElement element, bool searchNode)
        {
            List<UfbxMaterial> list = new List<UfbxMaterial>();

            do {
                UfbxConnectionList conns = FindDstConnections(element, null);
                for (int i = conns.Offset; i < conns.End; i++) {
                    UfbxConnection conn = conns.Data[i];
                    if (conn.Src.Type == UfbxElementType.Material) {
                        list.Add((UfbxMaterial)conn.Src);
                    }
                }

                if (list.Count > 0) break;
            } while (searchNode && (element = GetElementNode(element)) != null);

            return list.ToArray();
        }

        // C: ufbxi_fetch_deformers (ufbx.c:19198-19218).
        internal static UfbxElement[] FetchDeformers(UfbxiContext uc, UfbxElement element, bool searchNode)
        {
            List<UfbxElement> list = new List<UfbxElement>();

            do {
                UfbxConnection[] conns = element.ConnectionsDst;
                if (conns != null) {
                    for (int i = 0; i < conns.Length; i++) {
                        UfbxConnection conn = conns[i];
                        if (conn.SrcProp.Length > 0) continue;
                        UfbxElementType type = conn.Src.Type;
                        if (type == UfbxElementType.SkinDeformer || type == UfbxElementType.BlendDeformer || type == UfbxElementType.CacheDeformer) {
                            list.Add(conn.Src);
                        }
                    }
                }
            } while (searchNode && (element = GetElementNode(element)) != null);

            return list.ToArray();
        }

        // C: ufbxi_fetch_blend_keyframes (ufbx.c:19220-19238).
        internal static UfbxBlendKeyframe[] FetchBlendKeyframes(UfbxiContext uc, UfbxElement element)
        {
            List<UfbxBlendKeyframe> list = new List<UfbxBlendKeyframe>();

            UfbxConnectionList conns = FindDstConnections(element, null);
            for (int i = conns.Offset; i < conns.End; i++) {
                UfbxConnection conn = conns.Data[i];
                if (conn.Src.Type == UfbxElementType.BlendShape) {
                    UfbxBlendKeyframe key = new UfbxBlendKeyframe();
                    key.Shape = (UfbxBlendShape)conn.Src;
                    list.Add(key);
                }
            }

            return list.ToArray();
        }

        // C: ufbxi_fetch_texture_layers (ufbx.c:19240-19261).
        internal static UfbxTextureLayer[] FetchTextureLayers(UfbxiContext uc, UfbxElement element)
        {
            List<UfbxTextureLayer> list = new List<UfbxTextureLayer>();

            UfbxConnectionList conns = FindDstConnections(element, null);
            for (int i = conns.Offset; i < conns.End; i++) {
                UfbxConnection conn = conns.Data[i];
                if (conn.Src.Type == UfbxElementType.Texture) {
                    UfbxTexture texture = (UfbxTexture)conn.Src;
                    UfbxTextureLayer layer = new UfbxTextureLayer();
                    layer.Texture = texture;
                    layer.Alpha = UfbxiProperties.FindReal(texture.Props, UfbxiStrings.Texture_alpha, 1.0);
                    layer.BlendMode = (UfbxBlendMode)UfbxiProperties.FindEnum(texture.Props, UfbxiStrings.BlendMode,
                        (long)UfbxBlendMode.Replace, (long)UfbxBlendMode.Overlay);
                    list.Add(layer);
                }
            }

            return list.ToArray();
        }

        // ==================================================================
        // prop connection + index pointer (ufbx.c:19263-19291)
        // ==================================================================

        // C: ufbxi_prop_connection_less (ufbx.c:19263-19268).
        internal static bool PropConnectionLess(UfbxConnection a, string prop)
        {
            int cmp = UfbxiProperties.Strcmp(a.DstProp, prop);
            if (cmp != 0) return cmp < 0;
            return a.SrcProp.Length == 0;
        }

        // C: ufbxi_find_prop_connection (ufbx.c:19270-19282).
        internal static UfbxConnection FindPropConnection(UfbxElement element, string prop)
        {
            if (prop == null) prop = string.Empty;

            UfbxConnection[] data = element.ConnectionsDst ?? Array.Empty<UfbxConnection>();
            int size = data.Length;

            int index = int.MaxValue; // C: SIZE_MAX

            LowerBoundEq(data, 0, size, 32,
                c => PropConnectionLess(c, prop),
                c => c.DstProp == prop && c.SrcProp.Length > 0,
                ref index);

            return index < int.MaxValue ? data[index] : null;
        }

        // C: ufbxi_patch_index_pointer (ufbx.c:19284-19291). The sentinel arrays exist twice in
        // the port (`UfbxiGeometry` — used by the modern reader — and `UfbxiObjects` — used by
        // the legacy reader); both are matched by reference so either source path is patched.
        internal static void PatchIndexPointer(UfbxiContext uc, ref uint[] pIndex)
        {
            if (ReferenceEquals(pIndex, UfbxiGeometry.SentinelIndexZero)
                || ReferenceEquals(pIndex, UfbxiObjects.SentinelIndexZero)) {
                pIndex = uc.ZeroIndices;
            } else if (ReferenceEquals(pIndex, UfbxiGeometry.SentinelIndexConsecutive)
                || ReferenceEquals(pIndex, UfbxiObjects.SentinelIndexConsecutive)) {
                pIndex = uc.ConsecutiveIndices;
            }
        }

        // ==================================================================
        // anim props / material textures / bone poses / skin weights (ufbx.c:19293-19369)
        // ==================================================================

        // C: ufbxi_cmp_anim_prop_less (ufbx.c:19293-19298).
        internal static bool CmpAnimPropLess(UfbxAnimProp a, UfbxAnimProp b)
        {
            if (a.Element.ElementId != b.Element.ElementId) return a.Element.ElementId < b.Element.ElementId;
            if (a.InternalKey != b.InternalKey) return a.InternalKey < b.InternalKey;
            return UfbxiStr.Less(a.PropName, b.PropName);
        }

        // C: ufbxi_sort_anim_props (ufbx.c:19300-19305).
        internal static void SortAnimProps(UfbxAnimProp[] aprops, int count)
        {
            UfbxAnimProp[] tmp = new UfbxAnimProp[count];
            UfbxiSort.StableSort(32, aprops, tmp, count,
                (object user, UfbxAnimProp a, UfbxAnimProp b) => CmpAnimPropLess(a, b), null);
        }

        // C: ufbxi_material_texture_less (ufbx.c:19307-19312).
        internal static bool MaterialTextureLess(object user, UfbxMaterialTexture a, UfbxMaterialTexture b)
        {
            return UfbxiStr.Less(a.MaterialProp, b.MaterialProp);
        }

        // C: ufbxi_sort_material_textures (ufbx.c:19314-19319).
        internal static void SortMaterialTextures(UfbxMaterialTexture[] textures, int count)
        {
            UfbxMaterialTexture[] tmp = new UfbxMaterialTexture[count];
            UfbxiSort.StableSort(32, textures, tmp, count, MaterialTextureLess, null);
        }

        // C: ufbxi_bone_pose_less (ufbx.c:19321-19326).
        internal static bool BonePoseLess(object user, UfbxBonePose a, UfbxBonePose b)
        {
            return a.BoneNode.TypedId < b.BoneNode.TypedId;
        }

        // C: ufbxi_find_anim_prop_start (ufbx.c:19328-19334).
        internal static UfbxAnimProp FindAnimPropStart(UfbxAnimLayer layer, UfbxElement element)
        {
            UfbxAnimProp[] data = layer.AnimProps ?? Array.Empty<UfbxAnimProp>();
            int size = data.Length;

            int index = int.MaxValue; // C: SIZE_MAX

            LowerBoundEq(data, 0, size, 16,
                a => a.Element.ElementId < element.ElementId,
                a => a.Element.ElementId == element.ElementId,
                ref index);

            return index != int.MaxValue ? data[index] : null;
        }

        // C: ufbxi_sort_bone_poses (ufbx.c:19336-19342).
        internal static void SortBonePoses(UfbxPose pose)
        {
            UfbxBonePose[] data = pose.BonePoses;
            if (data == null) return;
            int count = data.Length;
            UfbxBonePose[] tmp = new UfbxBonePose[count];
            UfbxiSort.StableSort(16, data, tmp, count, BonePoseLess, null);
        }

        // C: ufbxi_sort_skin_weights (ufbx.c:19344-19355). C sorts each vertex's weight range
        // in place; the port copies the range out, sorts, and copies it back (same algorithm,
        // same relative order).
        internal static void SortSkinWeights(UfbxSkinDeformer skin)
        {
            UfbxSkinVertex[] vertices = skin.Vertices;
            UfbxSkinWeight[] weights = skin.Weights;
            if (vertices == null || weights == null) return;

            for (int i = 0; i < vertices.Length; i++) {
                UfbxSkinVertex v = vertices[i];
                int begin = (int)v.WeightBegin;
                int numWeights = (int)v.NumWeights;
                if (numWeights <= 0) continue;

                UfbxSkinWeight[] sub = new UfbxSkinWeight[numWeights];
                for (int k = 0; k < numWeights; k++) sub[k] = weights[begin + k];

                UfbxSkinWeight[] tmp = new UfbxSkinWeight[numWeights];
                UfbxiSort.StableSort(32, sub, tmp, numWeights,
                    (object user, UfbxSkinWeight a, UfbxSkinWeight b) => a.Weight > b.Weight, null);

                for (int k = 0; k < numWeights; k++) weights[begin + k] = sub[k];
            }
        }

        // C: ufbxi_blend_keyframe_less (ufbx.c:19357-19362).
        internal static bool BlendKeyframeLess(object user, UfbxBlendKeyframe a, UfbxBlendKeyframe b)
        {
            return a.TargetWeight < b.TargetWeight;
        }

        // C: ufbxi_sort_blend_keyframes (ufbx.c:19364-19369).
        internal static void SortBlendKeyframes(UfbxBlendKeyframe[] keyframes, int count)
        {
            UfbxBlendKeyframe[] tmp = new UfbxBlendKeyframe[count];
            UfbxiSort.StableSort(32, keyframes, tmp, count, BlendKeyframeLess, null);
        }
    }
}
