// Element creation, the object reader dispatch and the index fixups, ported from ufbx
// v0.23.1 ufbx.c:
//   ufbxi_push_element_size            (12348-12378)
//   ufbxi_push_synthetic_element_size  (12380-12410)
//   ufbxi_connect_oo / _op / _pp       (12415-12445)
//   ufbxi_init_synthetic_*_prop        (12447-12487)
//   ufbxi_setup_geometry_transform_helper (12508-12542)
//   ufbxi_setup_scale_helper           (12556-12595)
//   ufbxi_read_model                   (12597-12623)
//   ufbxi_read_element                 (12625-12631)
//   ufbxi_read_unknown                 (12633-12649)
//   ufbxi_read_bone                    (13968-13980)
//   ufbxi_read_marker                  (13982-13993)
//   ufbxi_read_object / ufbxi_read_objects (14950-15124)
//   ufbxi_fix_index / ufbxi_check_indices  (12662-12724)
//
// C allocates elements as `aligned_size` bytes out of the arena and casts the result to the
// concrete struct (`ufbxi_push_element(uc, info, type_name, type_enum)`). The port instead
// maps `UfbxElementType` to its C# class in `CreateElement`; the `size` argument therefore
// carries no information and is dropped (PORTING_NOTES.md "C 指针序 ≡ 分配序").
//
// Deep readers that belong to S2 (mesh/texture/material/... below) throw
// `UfbxiReaderNotPortedException` through `NotPorted`, exactly like the load spine's seam.
using System;

namespace Ufbx.NET
{
    internal static class UfbxiReadElement
    {
        // C: ufbxi_push_element_size (ufbx.c:12348-12378). Returns the new element; the
        // `typed_id` is the per-type counter before increment and `element_id` the global
        // counter, so `TmpElementPtrs[element_id] == elem` (PORTING_NOTES.md "C 指针序 ≡ 分配序").
        internal static UfbxElement PushElementSize(UfbxiContext uc, UfbxiElementInfo info, UfbxElementType type)
        {
            uint typedId = (uint)uc.TmpTypedElementCount[(int)type];
            uc.TmpTypedElementCount[(int)type] = (int)typedId + 1;
            uint elementId = uc.NumElements;
            uc.NumElements = elementId + 1;

            uc.TmpElementFbxIds.Add(info.FbxId);

            UfbxElement elem = CreateElement(type);
            elem.Type = type;
            elem.ElementId = elementId;
            elem.TypedId = typedId;
            elem.Name = info.Name;
            elem.Props = info.Props;
            elem.DomNode = info.DomNode;

            if (uc.PElementIdIndex >= 0) {
                uc.TmpElementIds[uc.PElementIdIndex] = elementId;
            }

            uc.TmpElementPtrs.Add(elem);

            UfbxiFbxId.InsertFbxId(uc, info.FbxId, elementId);
            return elem;
        }

        // C: ufbxi_push_synthetic_element_size (ufbx.c:12380-12410).
        internal static UfbxElement PushSyntheticElementSize(UfbxiContext uc, out ulong pFbxId,
            UfbxiNode node, string name, UfbxElementType type)
        {
            uint typedId = (uint)uc.TmpTypedElementCount[(int)type];
            uc.TmpTypedElementCount[(int)type] = (int)typedId + 1;
            uint elementId = uc.NumElements;
            uc.NumElements = elementId + 1;

            UfbxElement elem = CreateElement(type);
            elem.Type = type;
            elem.ElementId = elementId;
            elem.TypedId = typedId;
            elem.DomNode = UfbxiDom.GetDomNode(uc, node);
            if (name != null) {
                elem.Name = name;
            }

            uc.TmpElementPtrs.Add(elem);

            pFbxId = UfbxiFbxId.PushSyntheticId(uc);

            uc.TmpElementFbxIds.Add(pFbxId);
            UfbxiFbxId.InsertFbxId(uc, pFbxId, elementId);
            return elem;
        }

        // C: ufbxi_push_element(uc, info, type_name, type_enum) — the typed cast is the
        // generic parameter here.
        internal static T PushElement<T>(UfbxiContext uc, UfbxiElementInfo info, UfbxElementType type)
            where T : UfbxElement
        {
            return (T)PushElementSize(uc, info, type);
        }

        // C: ufbxi_push_synthetic_element(uc, p_fbx_id, node, name, type_name, type_enum).
        internal static T PushSyntheticElement<T>(UfbxiContext uc, out ulong pFbxId,
            UfbxiNode node, string name, UfbxElementType type)
            where T : UfbxElement
        {
            return (T)PushSyntheticElementSize(uc, out pFbxId, node, name, type);
        }

        // C: the `ufbx_element` union member selected by `ufbx_element_type`. Every enum
        // value maps to exactly one C# class (see Model/Elements and Model/UfbxMesh.cs).
        static UfbxElement CreateElement(UfbxElementType type)
        {
            switch (type) {
            case UfbxElementType.Unknown: return new UfbxUnknown();
            case UfbxElementType.Node: return new UfbxNode();
            case UfbxElementType.Mesh: return new UfbxMesh();
            case UfbxElementType.Light: return new UfbxLight();
            case UfbxElementType.Camera: return new UfbxCamera();
            case UfbxElementType.Bone: return new UfbxBone();
            case UfbxElementType.Empty: return new UfbxEmpty();
            case UfbxElementType.LineCurve: return new UfbxLineCurve();
            case UfbxElementType.NurbsCurve: return new UfbxNurbsCurve();
            case UfbxElementType.NurbsSurface: return new UfbxNurbsSurface();
            case UfbxElementType.NurbsTrimSurface: return new UfbxNurbsTrimSurface();
            case UfbxElementType.NurbsTrimBoundary: return new UfbxNurbsTrimBoundary();
            case UfbxElementType.ProceduralGeometry: return new UfbxProceduralGeometry();
            case UfbxElementType.StereoCamera: return new UfbxStereoCamera();
            case UfbxElementType.CameraSwitcher: return new UfbxCameraSwitcher();
            case UfbxElementType.Marker: return new UfbxMarker();
            case UfbxElementType.LodGroup: return new UfbxLodGroup();
            case UfbxElementType.SkinDeformer: return new UfbxSkinDeformer();
            case UfbxElementType.SkinCluster: return new UfbxSkinCluster();
            case UfbxElementType.BlendDeformer: return new UfbxBlendDeformer();
            case UfbxElementType.BlendChannel: return new UfbxBlendChannel();
            case UfbxElementType.BlendShape: return new UfbxBlendShape();
            case UfbxElementType.CacheDeformer: return new UfbxCacheDeformer();
            case UfbxElementType.CacheFile: return new UfbxCacheFile();
            case UfbxElementType.Material: return new UfbxMaterial();
            case UfbxElementType.Texture: return new UfbxTexture();
            case UfbxElementType.Video: return new UfbxVideo();
            case UfbxElementType.Shader: return new UfbxShader();
            case UfbxElementType.ShaderBinding: return new UfbxShaderBinding();
            case UfbxElementType.AnimStack: return new UfbxAnimStack();
            case UfbxElementType.AnimLayer: return new UfbxAnimLayer();
            case UfbxElementType.AnimValue: return new UfbxAnimValue();
            case UfbxElementType.AnimCurve: return new UfbxAnimCurve();
            case UfbxElementType.DisplayLayer: return new UfbxDisplayLayer();
            case UfbxElementType.SelectionSet: return new UfbxSelectionSet();
            case UfbxElementType.SelectionNode: return new UfbxSelectionNode();
            case UfbxElementType.Character: return new UfbxCharacter();
            case UfbxElementType.Constraint: return new UfbxConstraint();
            case UfbxElementType.AudioLayer: return new UfbxAudioLayer();
            case UfbxElementType.AudioClip: return new UfbxAudioClip();
            case UfbxElementType.Pose: return new UfbxPose();
            case UfbxElementType.MetadataObject: return new UfbxMetadataObject();
            default:
                // C: ufbxi_unreachable("Bad element type") — every enumerator is covered.
                UfbxiFail.FailNoDesc("Bad element type");
                return null;
            }
        }

        // C: ufbxi_connect_oo (ufbx.c:12415-12423). `ufbx_empty_string` is the pool's
        // canonical empty string (UfbxiPtrIdTable id 1), represented as `string.Empty`.
        internal static void ConnectOo(UfbxiContext uc, ulong src, ulong dst)
        {
            uc.TmpConnections.Add(new UfbxiTmpConnection {
                Src = src, Dst = dst, SrcProp = string.Empty, DstProp = string.Empty,
            });
        }

        // C: ufbxi_connect_op (ufbx.c:12425-12434).
        internal static void ConnectOp(UfbxiContext uc, ulong src, ulong dst, string prop)
        {
            uc.TmpConnections.Add(new UfbxiTmpConnection {
                Src = src, Dst = dst, SrcProp = string.Empty, DstProp = prop,
            });
        }

        // C: ufbxi_connect_pp (ufbx.c:12436-12445).
        internal static void ConnectPp(UfbxiContext uc, ulong src, ulong dst, string srcProp, string dstProp)
        {
            uc.TmpConnections.Add(new UfbxiTmpConnection {
                Src = src, Dst = dst, SrcProp = srcProp, DstProp = dstProp,
            });
        }

        // C: ufbxi_init_synthetic_int_prop (ufbx.c:12447-12459).
        internal static void InitSyntheticIntProp(ref UfbxProp dst, string name, long value, UfbxPropType type)
        {
            dst.Type = type;
            dst.Name = name;
            dst.ValueReal = (double)value;
            dst.Flags = UfbxPropFlags.Synthetic | UfbxPropFlags.ValueReal | UfbxPropFlags.ValueInt;
            dst.ValueInt = value;
            dst.ValueStr = string.Empty;

            // C: ufbxi_dev_assert(dst->name.length >= 4)
            dst.InternalKey = UfbxiProperties.GetNameKey(name, 4);
        }

        // C: ufbxi_init_synthetic_real_prop (ufbx.c:12461-12473).
        internal static void InitSyntheticRealProp(ref UfbxProp dst, string name, double value, UfbxPropType type)
        {
            dst.Type = type;
            dst.Name = name;
            dst.ValueReal = value;
            dst.Flags = UfbxPropFlags.Synthetic | UfbxPropFlags.ValueReal;
            // C: dst->value_int = (int64_t)value
            dst.ValueInt = UfbxiBinaryArray.F64ToInt64(value);
            dst.ValueStr = string.Empty;

            dst.InternalKey = UfbxiProperties.GetNameKey(name, 4);
        }

        // C: ufbxi_init_synthetic_vec3_prop (ufbx.c:12475-12487). `value_int` reads the
        // union's first double, i.e. `value_vec3.x`.
        internal static void InitSyntheticVec3Prop(ref UfbxProp dst, string name, UfbxVec3 value, UfbxPropType type)
        {
            dst.Type = type;
            dst.Name = name;
            dst.ValueVec3 = value;
            dst.Flags = UfbxPropFlags.Synthetic | UfbxPropFlags.ValueVec3;
            dst.ValueInt = UfbxiBinaryArray.F64ToInt64(dst.ValueReal);
            dst.ValueStr = string.Empty;

            dst.InternalKey = UfbxiProperties.GetNameKey(name, 4);
        }

        // C: ufbxi_setup_geometry_transform_helper (ufbx.c:12508-12542).
        internal static void SetupGeometryTransformHelper(UfbxiContext uc, UfbxNode node, ulong nodeFbxId)
        {
            UfbxVec3 geoTranslation = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.GeometricTranslation, 0.0, 0.0, 0.0);
            UfbxVec3 geoRotation = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.GeometricRotation, 0.0, 0.0, 0.0);
            UfbxVec3 geoScaling = UfbxiProperties.FindVec3(node.Props, UfbxiStrings.GeometricScaling, 1.0, 1.0, 1.0);
            if (!UfbxiProperties.IsVec3Zero(geoTranslation) || !UfbxiProperties.IsVec3Zero(geoRotation)
                || !UfbxiProperties.IsVec3One(geoScaling)) {

                ulong geoFbxId;
                UfbxNode geoNode = PushSyntheticElement<UfbxNode>(uc, out geoFbxId, null,
                    uc.Opts.GeometryTransformHelperName, UfbxElementType.Node);
                uc.TmpNodeIds.Add(geoNode.ElementId);
                geoNode.DomNode = node.DomNode;

                // C: ufbx_prop *props = ufbxi_push_zero(&uc->result, ufbx_prop, 3);
                UfbxProp[] props = new UfbxProp[3];
                InitSyntheticVec3Prop(ref props[0], UfbxiStrings.Lcl_Rotation, geoRotation, UfbxPropType.Rotation);
                InitSyntheticVec3Prop(ref props[1], UfbxiStrings.Lcl_Scaling, geoScaling, UfbxPropType.Scaling);
                InitSyntheticVec3Prop(ref props[2], UfbxiStrings.Lcl_Translation, geoTranslation, UfbxPropType.Translation);

                geoNode.Props.Props = props;

                node.HasGeometryTransform = true;
                geoNode.IsGeometryTransformHelper = true;

                ConnectOo(uc, geoFbxId, nodeFbxId);
                uc.HasGeometryTransformNodes = true;

                UfbxiNodeExtra extra = uc.PushElementExtra(node.ElementId, () => new UfbxiNodeExtra());
                extra.GeometryHelperId = geoNode.ElementId;
            }
        }

        // C: ufbxi_scale_helper_props[] (ufbx.c:12549-12554).
        static readonly string[] ScaleHelperPropNames = new string[] {
            UfbxiStrings.GeometricRotation,
            UfbxiStrings.GeometricScaling,
            UfbxiStrings.GeometricTranslation,
            UfbxiStrings.Lcl_Scaling,
        };

        static readonly UfbxVec3[] ScaleHelperPropDefaults = new UfbxVec3[] {
            new UfbxVec3(0.0, 0.0, 0.0),
            new UfbxVec3(1.0, 1.0, 1.0),
            new UfbxVec3(0.0, 0.0, 0.0),
            new UfbxVec3(1.0, 1.0, 1.0),
        };

        // C: ufbxi_setup_scale_helper (ufbx.c:12556-12595).
        internal static void SetupScaleHelper(UfbxiContext uc, UfbxNode node, ulong nodeFbxId)
        {
            ulong scaleFbxId;
            UfbxNode scaleNode = PushSyntheticElement<UfbxNode>(uc, out scaleFbxId, null,
                uc.Opts.ScaleHelperName, UfbxElementType.Node);
            uc.TmpNodeIds.Add(scaleNode.ElementId);
            scaleNode.DomNode = node.DomNode;

            node.ScaleHelper = scaleNode;
            scaleNode.IsScaleHelper = true;

            ConnectOo(uc, scaleFbxId, nodeFbxId);
            uc.HasScaleHelperNodes = true;

            UfbxiNodeExtra extra = uc.PushElementExtra(node.ElementId, () => new UfbxiNodeExtra());
            extra.ScaleHelperId = scaleNode.ElementId;

            int maxProps = ScaleHelperPropNames.Length;
            UfbxProp[] helperProps = new UfbxProp[maxProps];

            int numProps = 0;
            // C: ufbx_props props_copy = node->props; props_copy.defaults = NULL; — a struct
            // copy, so `node->props.defaults` itself is left untouched.
            UfbxProps propsCopy = new UfbxProps {
                Props = node.Props.Props, NumAnimated = node.Props.NumAnimated, Defaults = null,
            };
            for (int i = 0; i < maxProps; i++) {
                int index = UfbxiProperties.FindPropIndex(propsCopy, ScaleHelperPropNames[i],
                    UfbxiProperties.GetNameKey(ScaleHelperPropNames[i], ScaleHelperPropNames[i].Length),
                    true, out UfbxProps owner);
                if (index < 0) continue;

                // C: helper_props[num_props++] = *src_prop; then src_prop is zeroed in place
                // (the helper keeps the original values, the source prop keeps the default).
                UfbxProp srcProp = owner.Props[index];
                helperProps[numProps++] = srcProp;

                UfbxProp modified = srcProp;
                modified.ValueVec3 = ScaleHelperPropDefaults[i];
                modified.ValueInt = (long)modified.ValueVec3.X;
                owner.Props[index] = modified;
            }

            // C: scale_node->props.props.count = num_props (allocation stays max_props).
            if (numProps != maxProps) {
                Array.Resize(ref helperProps, numProps);
            }
            scaleNode.Props.Props = helperProps;
        }

        // C: ufbxi_read_model (ufbx.c:12597-12623).
        internal static void ReadModel(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxNode elemNode = PushElement<UfbxNode>(uc, info, UfbxElementType.Node);
            uc.TmpNodeIds.Add(elemNode.ElementId);

            long inheritType = UfbxiProperties.FindInt(elemNode.Props, UfbxiStrings.InheritType, -1);
            switch (inheritType) {
            case 0: // RrSs
                elemNode.OriginalInheritMode = UfbxInheritMode.ComponentwiseScale;
                break;
            case 2: // Rrs
                elemNode.OriginalInheritMode = UfbxInheritMode.IgnoreParentScale;
                break;
            default:
                break;
            }

            if (uc.Opts.InheritModeHandling == UfbxInheritModeHandling.Preserve) {
                elemNode.InheritMode = elemNode.OriginalInheritMode;
            } else if (uc.Opts.InheritModeHandling == UfbxInheritModeHandling.Ignore) {
                elemNode.OriginalInheritMode = UfbxInheritMode.Normal;
                elemNode.InheritMode = UfbxInheritMode.Normal;
            }
        }

        // C: ufbxi_read_element (ufbx.c:12625-12631) — the `size` argument is the C struct
        // size, subsumed by `CreateElement`.
        internal static void ReadElement(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info, UfbxElementType type)
        {
            PushElementSize(uc, info, type);
        }

        // C: ufbxi_read_unknown (ufbx.c:12633-12649).
        internal static void ReadUnknown(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo element,
            string type, string subType, string nodeName)
        {
            UfbxUnknown unknown = PushElement<UfbxUnknown>(uc, element, UfbxElementType.Unknown);
            unknown.FbxType = type;
            unknown.SubType = subType;
            unknown.SuperType = nodeName;

            // `type`, `sub_type` and `node_name` are raw strings so they may need to be sanitized.
            InternRaw(uc, ref unknown.FbxType);
            InternRaw(uc, ref unknown.SubType);
            InternRaw(uc, ref unknown.SuperType);
        }

        // C: ufbxi_push_string_place_str(&uc->string_pool, &str, false).
        static void InternRaw(UfbxiContext uc, ref string str)
        {
            UfbxiFail.CheckNoDesc(str != null, "p_str");
            int outLength;
            string interned = uc.StringPool.PushString(str, 0, str.Length, out outLength, false);
            UfbxiFail.CheckNoDesc(interned != null, "ufbxi_push_string_place_str(&uc->string_pool, &str, false)");
            str = interned;
        }

        // C: ufbxi_read_bone (ufbx.c:13968-13980).
        internal static void ReadBone(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info, string subType)
        {
            UfbxBone bone = PushElement<UfbxBone>(uc, info, UfbxElementType.Bone);
            if (subType == UfbxiStrings.Root) {
                bone.IsRoot = true;
            }
        }

        // C: ufbxi_read_marker (ufbx.c:13982-13993).
        internal static void ReadMarker(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info,
            UfbxMarkerType type)
        {
            UfbxMarker marker = PushElement<UfbxMarker>(uc, info, UfbxElementType.Marker);
            marker.Type = type;
        }

        // ==================================================================
        // ufbxi_read_object / ufbxi_read_objects (ufbx.c:14950-15124)
        // ==================================================================

        // C: ufbxi_read_object (ufbx.c:14950-15102). The branches that create a simple
        // `ufbxi_read_element` result are ported; the deep readers (mesh/texture/material/…)
        // stop at the S2 seam.
        internal static void ReadObject(UfbxiContext uc, UfbxiNode node)
        {
            UfbxiElementInfo info = new UfbxiElementInfo();
            info.DomNode = UfbxiDom.GetDomNode(uc, node);

            if (node.Name == UfbxiStrings.GlobalSettings) {
                UfbxiRoot.ReadGlobalSettings(uc, node);
                return;
            }

            string typeAndName, subTypeStr;

            // Failing to parse the object properties is not an error since there's some weird
            // objects mixed in every now and then. FBX 7000+ uses 64-bit unique IDs per object,
            // older versions use name/type pairs ("all strings are interned", so they work as IDs).
            if (uc.Version >= 7000) {
                long fbxId;
                if (!node.GetValL(0, out fbxId)
                    || !node.GetValRawString(1, out typeAndName)
                    || !node.GetValRawString(2, out subTypeStr)) return;
                info.FbxId = unchecked((ulong)fbxId);
                info.FbxId = UfbxiFbxId.ValidateFbxId(uc, info.FbxId);
            } else {
                if (!node.GetValRawString(0, out typeAndName)
                    || !node.GetValRawString(1, out subTypeStr)) return;
                info.FbxId = UfbxiFbxId.SyntheticIdFromString(uc, typeAndName);
                UfbxiFail.CheckNoDesc(info.FbxId != 0, "info.fbx_id");
            }

            // Remove the "Fbx" prefix from sub-types, remember to re-intern!
            if (subTypeStr.Length > 3 && string.CompareOrdinal(subTypeStr, 0, "Fbx", 0, 3) == 0) {
                string sub = subTypeStr.Substring(3);
                InternRaw(uc, ref sub);
                subTypeStr = sub;
            }

            string typeStr;
            string name;
            UfbxiFbxId.SplitTypeAndName(uc, typeAndName, out typeStr, out name);
            info.Name = name;

            string nodeName = node.Name;
            string subType = subTypeStr;
            UfbxiProperties.ReadProperties(uc, node, info.Props);
            info.Props.Defaults = UfbxiProperties.FindTemplate(uc, nodeName, subType);

            if (nodeName == UfbxiStrings.Model) {
                if (uc.Version < 7000) {
                    UfbxiObjects.ReadSyntheticAttribute(uc, node, info, typeStr, subType, nodeName);
                }
                ReadModel(uc, node, info);
            } else if (nodeName == UfbxiStrings.NodeAttribute) {
                if (subType == UfbxiStrings.Light) {
                    ReadElement(uc, node, info, UfbxElementType.Light);
                } else if (subType == UfbxiStrings.Camera) {
                    ReadElement(uc, node, info, UfbxElementType.Camera);
                } else if (subType == UfbxiStrings.LimbNode || subType == UfbxiStrings.Limb || subType == UfbxiStrings.Root) {
                    ReadBone(uc, node, info, subType);
                } else if (subType == UfbxiStrings.Null || subType == UfbxiStrings.Marker) {
                    ReadElement(uc, node, info, UfbxElementType.Empty);
                } else if (subType == UfbxiStrings.CameraStereo) {
                    ReadElement(uc, node, info, UfbxElementType.StereoCamera);
                } else if (subType == UfbxiStrings.CameraSwitcher) {
                    ReadElement(uc, node, info, UfbxElementType.CameraSwitcher);
                } else if (subType == UfbxiStrings.FKEffector) {
                    ReadMarker(uc, node, info, UfbxMarkerType.FkEffector);
                } else if (subType == UfbxiStrings.IKEffector) {
                    ReadMarker(uc, node, info, UfbxMarkerType.IkEffector);
                } else if (subType == UfbxiStrings.LodGroup) {
                    ReadElement(uc, node, info, UfbxElementType.LodGroup);
                } else {
                    ReadUnknown(uc, node, info, typeStr, subTypeStr, nodeName);
                }
            } else if (nodeName == UfbxiStrings.Geometry) {
                if (subType == UfbxiStrings.Mesh) {
                    UfbxiGeometry.ReadMesh(uc, node, info);
                } else if (subType == UfbxiStrings.Shape) {
                    UfbxiGeometry.ReadShape(uc, node, info);
                } else if (subType == UfbxiStrings.NurbsCurve) {
                    UfbxiGeometry.ReadNurbsCurve(uc, node, info);
                } else if (subType == UfbxiStrings.NurbsSurface) {
                    UfbxiGeometry.ReadNurbsSurface(uc, node, info);
                } else if (subType == UfbxiStrings.Line) {
                    UfbxiGeometry.ReadLine(uc, node, info);
                } else if (subType == UfbxiStrings.TrimNurbsSurface) {
                    ReadElement(uc, node, info, UfbxElementType.NurbsTrimSurface);
                } else if (subType == UfbxiStrings.Boundary) {
                    ReadElement(uc, node, info, UfbxElementType.NurbsTrimBoundary);
                } else {
                    ReadUnknown(uc, node, info, typeStr, subTypeStr, nodeName);
                }
            } else if (nodeName == UfbxiStrings.Deformer) {
                if (subType == UfbxiStrings.Skin) {
                    UfbxiGeometry.ReadSkin(uc, node, info);
                } else if (subType == UfbxiStrings.Cluster) {
                    UfbxiGeometry.ReadSkinCluster(uc, node, info);
                } else if (subType == UfbxiStrings.BlendShape) {
                    ReadElement(uc, node, info, UfbxElementType.BlendDeformer);
                } else if (subType == UfbxiStrings.BlendShapeChannel) {
                    UfbxiGeometry.ReadBlendChannel(uc, node, info);
                } else if (subType == UfbxiStrings.VertexCacheDeformer) {
                    ReadElement(uc, node, info, UfbxElementType.CacheDeformer);
                } else {
                    ReadUnknown(uc, node, info, typeStr, subTypeStr, nodeName);
                }
            } else if (nodeName == UfbxiStrings.Material) {
                UfbxiObjects.ReadMaterial(uc, node, info);
            } else if (nodeName == UfbxiStrings.Texture) {
                UfbxiObjects.ReadTexture(uc, node, info);
            } else if (nodeName == UfbxiStrings.LayeredTexture) {
                UfbxiObjects.ReadLayeredTexture(uc, node, info);
            } else if (nodeName == UfbxiStrings.Video) {
                UfbxiObjects.ReadVideo(uc, node, info);
            } else if (nodeName == UfbxiStrings.AnimationStack) {
                UfbxiAnimReader.ReadAnimStack(uc, node, info);
            } else if (nodeName == UfbxiStrings.AnimationLayer) {
                ReadElement(uc, node, info, UfbxElementType.AnimLayer);
            } else if (nodeName == UfbxiStrings.AnimationCurveNode) {
                ReadElement(uc, node, info, UfbxElementType.AnimValue);
            } else if (nodeName == UfbxiStrings.AnimationCurve) {
                UfbxiAnimReader.ReadAnimationCurve(uc, node, info);
            } else if (nodeName == UfbxiStrings.Pose) {
                UfbxiObjects.ReadPose(uc, node, info, subType);
            } else if (nodeName == UfbxiStrings.Implementation) {
                ReadElement(uc, node, info, UfbxElementType.Shader);
            } else if (nodeName == UfbxiStrings.BindingTable) {
                UfbxiObjects.ReadBindingTable(uc, node, info);
            } else if (nodeName == UfbxiStrings.Collection) {
                if (subType == UfbxiStrings.SelectionSet) {
                    UfbxiObjects.ReadSelectionSet(uc, node, info);
                }
            } else if (nodeName == UfbxiStrings.CollectionExclusive) {
                if (subType == UfbxiStrings.DisplayLayer) {
                    ReadElement(uc, node, info, UfbxElementType.DisplayLayer);
                }
            } else if (nodeName == UfbxiStrings.SelectionNode) {
                UfbxiObjects.ReadSelectionNode(uc, node, info);
            } else if (nodeName == UfbxiStrings.Constraint) {
                if (subType == UfbxiStrings.Character) {
                    UfbxiObjects.ReadCharacter(uc, node, info);
                } else {
                    UfbxiObjects.ReadConstraint(uc, node, info);
                }
            } else if (nodeName == UfbxiStrings.SceneInfo) {
                UfbxiProperties.ReadSceneInfo(uc, node);
            } else if (nodeName == UfbxiStrings.Cache) {
                ReadElement(uc, node, info, UfbxElementType.CacheFile);
            } else if (nodeName == UfbxiStrings.ObjectMetaData) {
                ReadElement(uc, node, info, UfbxElementType.MetadataObject);
            } else if (nodeName == UfbxiStrings.AudioLayer) {
                ReadElement(uc, node, info, UfbxElementType.AudioLayer);
            } else if (nodeName == UfbxiStrings.Audio) {
                UfbxiObjects.ReadAudioClip(uc, node, info);
            } else {
                ReadUnknown(uc, node, info, typeStr, subTypeStr, nodeName);
            }
        }

        // C: ufbxi_read_objects (ufbx.c:15104-15124). The threaded variant
        // (ufbxi_read_objects_threaded) requires a user-supplied thread pool, which the port
        // does not have (PORTING_NOTES.md #7), so `ReadRoot` checks `ThreadPoolEnabled` and
        // throws there instead.
        internal static void ReadObjects(UfbxiContext uc)
        {
            for (;;) {
                // Push a deferred element ID for tagging warnings
                uc.TmpElementIds.Add(UfbxConstants.NoIndex);
                uc.PElementIdIndex = uc.TmpElementIds.Count - 1;
                uc.Warnings.DeferredElementIdPlusOne = (uint)uc.TmpElementIds.Count;

                UfbxiNode node = UfbxiRoot.ParseToplevelChild(uc);
                if (node == null) break;

                ReadObject(uc, node);

                uc.Warnings.DeferredElementIdPlusOne = 0;
                uc.PElementIdIndex = -1;
            }
        }

        // ==================================================================
        // Index fixups (ufbx.c:12662-12724) — used by the S2 deep readers
        // ==================================================================

        // C: ufbxi_fix_index (ufbx.c:12662-12686). Writes `dst[dstIndex]` instead of `*p_dst`.
        internal static void FixIndex(UfbxiContext uc, uint[] dst, int dstIndex, uint index, ulong onePastMaxValue)
        {
            switch (uc.Opts.IndexErrorHandling) {
            case UfbxIndexErrorHandling.Clamp:
                UfbxiFail.CheckNoDesc(onePastMaxValue > 0, "one_past_max_val > 0");
                UfbxiFail.CheckNoDesc(onePastMaxValue <= uint.MaxValue, "one_past_max_val <= UINT32_MAX");
                dst[dstIndex] = (uint)onePastMaxValue - 1;
                UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.IndexClamped,
                    UfbxiWarnings.NoElementId, "Clamped index");
                break;
            case UfbxIndexErrorHandling.NoIndex:
                dst[dstIndex] = UfbxConstants.NoIndex;
                break;
            case UfbxIndexErrorHandling.AbortLoading:
                UfbxiPrint.FmtErrInfo(uc.Error, "%u (max %u)", new UfbxiVaList()
                    .AddUInt(index)
                    .AddUInt(onePastMaxValue != 0 ? (uint)(onePastMaxValue - 1) : 0u));
                UfbxiFail.FailMsg("UFBX_INDEX_ERROR_HANDLING_ABORT_LOADING", "Bad index");
                break;
            case UfbxIndexErrorHandling.UnsafeIgnore:
                dst[dstIndex] = index;
                break;
            default:
                // C: ufbxi_unreachable("Unhandled index_error_handling")
                UfbxiFail.FailNoDesc("Unhandled index_error_handling");
                break;
            }
        }

        // C: ufbxi_check_indices (ufbx.c:12688-12724). Returns the (possibly reallocated)
        // index buffer; `ownsIndices` says whether `indices` may be written in place.
        internal static uint[] CheckIndices(UfbxiContext uc, uint[] indices, bool ownsIndices,
            int numIndices, int numIndexers, int numElems)
        {
            // If the indices are truncated extend them with `UFBX_NO_INDEX`, the following
            // normalization pass handles them the same way as other out-of-bounds indices.
            if (numIndices < numIndexers) {
                uint[] newIndices = new uint[numIndexers];
                Array.Copy(indices, 0, newIndices, 0, numIndices);
                for (int i = numIndices; i < numIndexers; i++) {
                    newIndices[i] = UfbxConstants.NoIndex;
                }

                indices = newIndices;
                numIndices = numIndexers;
                ownsIndices = true;
            }

            // Normalize out-of-bounds indices.
            for (int i = 0; i < numIndices; i++) {
                uint ix = indices[i];
                if ((ulong)ix >= (ulong)numElems) {
                    // If the indices refer to an external buffer we need to allocate a
                    // separate buffer for them.
                    if (!ownsIndices) {
                        uint[] copy = new uint[numIndices];
                        Array.Copy(indices, 0, copy, 0, numIndices);
                        indices = copy;
                        ownsIndices = true;
                    }
                    FixIndex(uc, indices, i, ix, (ulong)numElems);
                }
            }

            return indices;
        }

        // The S2 seam: throws for readers that are out of scope for this port.
        static void NotPorted(string reader)
        {
            throw new UfbxiReaderNotPortedException(reader);
        }
    }
}
