// S2 legacy (pre-6000 FBX) object readers and filename manipulation, ported from
// ufbx v0.23.1 ufbx.c:
//   ufbxi_legacy_prop + the four prop tables   (15941-15987)
//   ufbxi_read_legacy_prop                     (15989-16049)
//   ufbxi_read_legacy_props                    (16051-16071)
//   ufbxi_read_legacy_material                 (16073-16089)
//   ufbxi_read_legacy_link                     (16091-16120)
//   ufbxi_read_legacy_light / _camera          (16122-16150)
//   ufbxi_read_legacy_limb_node                (16152-16170)
//   ufbxi_read_legacy_mesh                     (16172-16330)
//   ufbxi_read_legacy_media                    (16332-16347)
//   ufbxi_read_legacy_model                    (16349-16420)
//   ufbxi_trim_delimiters                      (16486-16497)
//   ufbxi_init_file_paths                      (16499-16528)
//
// Owned by the S2-objects porting agent (2026-10-03 wave). Cross-module calls:
//   * UfbxiGeometry.ProcessIndices / ReadTruncatedArrayI32 (pinned in Parse/Geometry.cs)
//   * the remaining geometry readers are stubbed through `NotPortedGeom`/the
//     `UfbxiObjects.ReadTransformMatrix` stub until Parse/Geometry.cs lands
//   * `ufbxi_read_take_prop_channel` (ufbx.c:15590) belongs to the anim/take line
using System;

namespace Ufbx.NET
{
    // C: ufbxi_legacy_prop (ufbx.c:15941-15946).
    internal struct UfbxiLegacyProp
    {
        public string PropName;   // C: const char *prop_name
        public UfbxPropType PropType;
        public string NodeName;   // C: const char *node_name
        public string NodeFmt;    // C: const char *node_fmt
    }

    internal static class UfbxiLegacy
    {
        // C: ufbxi_legacy_light_props[] (ufbx.c:15948-15957). "Must be alphabetically sorted!"
        static readonly UfbxiLegacyProp[] LegacyLightProps = new UfbxiLegacyProp[] {
            new UfbxiLegacyProp { PropName = UfbxiStrings.CastLight,   PropType = UfbxPropType.Boolean, NodeName = UfbxiStrings.CastLight,   NodeFmt = "L" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.CastShadows, PropType = UfbxPropType.Boolean, NodeName = UfbxiStrings.CastShadows, NodeFmt = "L" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.Color,       PropType = UfbxPropType.Color,   NodeName = UfbxiStrings.Color,       NodeFmt = "RRR" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.ConeAngle,   PropType = UfbxPropType.Number,  NodeName = UfbxiStrings.ConeAngle,   NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.HotSpot,     PropType = UfbxPropType.Number,  NodeName = UfbxiStrings.HotSpot,     NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.Intensity,   PropType = UfbxPropType.Number,  NodeName = UfbxiStrings.Intensity,   NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.LightType,   PropType = UfbxPropType.Integer, NodeName = UfbxiStrings.LightType,   NodeFmt = "L" },
        };

        // C: ufbxi_legacy_camera_props[] (ufbx.c:15959-15972). "Must be alphabetically sorted!"
        static readonly UfbxiLegacyProp[] LegacyCameraProps = new UfbxiLegacyProp[] {
            new UfbxiLegacyProp { PropName = UfbxiStrings.ApertureMode,    PropType = UfbxPropType.Integer, NodeName = UfbxiStrings.ApertureMode,        NodeFmt = "L" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.AspectH,         PropType = UfbxPropType.Number,  NodeName = UfbxiStrings.AspectH,             NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.AspectRatioMode, PropType = UfbxPropType.Integer, NodeName = "AspectType",                     NodeFmt = "L" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.AspectW,         PropType = UfbxPropType.Number,  NodeName = UfbxiStrings.AspectW,             NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.FieldOfView,     PropType = UfbxPropType.Number,  NodeName = "Aperture",                       NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.FieldOfViewX,    PropType = UfbxPropType.Number,  NodeName = "FieldOfViewXProperty",           NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.FieldOfViewY,    PropType = UfbxPropType.Number,  NodeName = "FieldOfViewYProperty",           NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.FilmHeight,      PropType = UfbxPropType.Number,  NodeName = "CameraAperture",                 NodeFmt = "_R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.FilmSqueezeRatio, PropType = UfbxPropType.Number, NodeName = "SqueezeRatio",                   NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.FilmWidth,       PropType = UfbxPropType.Number,  NodeName = "CameraAperture",                 NodeFmt = "R_" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.FocalLength,     PropType = UfbxPropType.Number,  NodeName = UfbxiStrings.FocalLength,         NodeFmt = "R" },
        };

        // C: ufbxi_legacy_bone_props[] (ufbx.c:15974-15977). "Must be alphabetically sorted!"
        static readonly UfbxiLegacyProp[] LegacyBoneProps = new UfbxiLegacyProp[] {
            new UfbxiLegacyProp { PropName = UfbxiStrings.Size, PropType = UfbxPropType.Number, NodeName = UfbxiStrings.Size, NodeFmt = "R" },
        };

        // C: ufbxi_legacy_material_props[] (ufbx.c:15979-15987). "Must be alphabetically
        // sorted!" (NOTE: `ShadingModel` really is UFBX_PROP_COLOR in the C table.)
        static readonly UfbxiLegacyProp[] LegacyMaterialProps = new UfbxiLegacyProp[] {
            new UfbxiLegacyProp { PropName = UfbxiStrings.AmbientColor,  PropType = UfbxPropType.Color,  NodeName = "Ambient",             NodeFmt = "RRR" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.DiffuseColor,  PropType = UfbxPropType.Color,  NodeName = "Diffuse",             NodeFmt = "RRR" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.EmissiveColor, PropType = UfbxPropType.Color,  NodeName = "Emissive",            NodeFmt = "RRR" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.ShadingModel,  PropType = UfbxPropType.Color,  NodeName = UfbxiStrings.ShadingModel, NodeFmt = "S" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.Shininess,     PropType = UfbxPropType.Number, NodeName = "Shininess",           NodeFmt = "R" },
            new UfbxiLegacyProp { PropName = UfbxiStrings.SpecularColor, PropType = UfbxPropType.Color,  NodeName = "Specular",            NodeFmt = "RRR" },
        };

        // C: ufbxi_read_legacy_prop (ufbx.c:15989-16049).
        static bool ReadLegacyProp(UfbxiNode node, ref UfbxProp prop, UfbxiLegacyProp legacyProp)
        {
            int valueIx = 0;
            uint flags = 0;

            string fmt = legacyProp.NodeFmt;
            for (int fmtIx = 0; fmtIx < fmt.Length; fmtIx++) {
                char c = fmt[fmtIx];
                switch (c) {
                case 'L':
                    // C: ufbx_assert(value_ix == 0)
                    if (!node.GetValL(fmtIx, out long vi)) return false;
                    prop.ValueInt = vi;
                    prop.ValueReal = (double)vi; // C: (ufbx_real)value_int
                    prop.SetRealAt(1, 0.0);
                    prop.SetRealAt(2, 0.0);
                    prop.SetRealAt(3, 0.0);
                    prop.ValueStr = string.Empty;
                    prop.ValueBlob = null; // C: ufbx_empty_blob
                    flags |= (uint)UfbxPropFlags.ValueInt;
                    valueIx++;
                    break;
                case 'R':
                    // C: ufbx_assert(value_ix < 4)
                    if (!node.GetValR(fmtIx, out double rv)) return false;
                    prop.SetRealAt(valueIx, rv);
                    if (valueIx == 0) {
                        prop.ValueInt = UfbxiBinaryArray.F64ToInt64(prop.ValueReal); // C: ufbxi_f64_to_i64
                        prop.SetRealAt(1, 0.0);
                        prop.SetRealAt(2, 0.0);
                        prop.SetRealAt(3, 0.0);
                        prop.ValueStr = string.Empty;
                        prop.ValueBlob = null; // C: ufbx_empty_blob
                    }
                    flags &= ~(uint)(UfbxPropFlags.ValueReal | UfbxPropFlags.ValueVec2
                        | UfbxPropFlags.ValueVec3 | UfbxPropFlags.ValueVec4);
                    flags |= (uint)UfbxPropFlags.ValueReal << valueIx;
                    valueIx++;
                    break;
                case 'S':
                    if (!node.GetValS(fmtIx, out string sv)) return false;
                    prop.ValueStr = sv;
                    // `vals[fmt_ix]` is known to be a string, fetch non-sanitized blob directly
                    prop.ValueBlob = UfbxiSanitizedString.ToBlob(node.Vals[fmtIx].S.RawData, node.Vals[fmtIx].S.RawLength);
                    prop.ValueReal = 0.0;
                    prop.SetRealAt(1, 0.0);
                    prop.SetRealAt(2, 0.0);
                    prop.SetRealAt(3, 0.0);
                    prop.ValueInt = 0;
                    flags |= (uint)UfbxPropFlags.ValueStr;
                    valueIx++;
                    break;
                case '_':
                    break;
                default:
                    // C: ufbxi_unreachable("Unhandled legacy fmt")
                    UfbxiFail.FailNoDesc("Unhandled legacy fmt");
                    break;
                }
            }

            prop.Flags = (UfbxPropFlags)flags;

            return true;
        }

        // C: ufbxi_read_legacy_props (ufbx.c:16051-16071). `props` must have room for
        // `legacyProps.Length` items. NOTE: C overwrites `prop->flags` (set by
        // ufbxi_read_legacy_prop) with 0 here (ufbx.c:16065) — copied verbatim.
        static int ReadLegacyProps(UfbxiNode node, UfbxProp[] props, UfbxiLegacyProp[] legacyProps)
        {
            int numProps = 0;
            for (int legacyIx = 0; legacyIx < legacyProps.Length; legacyIx++) {
                UfbxiLegacyProp legacyProp = legacyProps[legacyIx];
                UfbxProp prop = default;

                UfbxiNode n = node.FindChildStrCmp(legacyProp.NodeName);
                if (n == null) continue;
                if (!ReadLegacyProp(n, ref prop, legacyProp)) continue;

                prop.Name = legacyProp.PropName;
                prop.InternalKey = UfbxiProperties.GetNameKey(prop.Name, prop.Name.Length);
                prop.Flags = (UfbxPropFlags)0;
                prop.Type = legacyProp.PropType;
                props[numProps++] = prop;
            }

            return numProps;
        }

        // C: ufbxi_read_legacy_material (ufbx.c:16073-16089).
        internal static void ReadLegacyMaterial(UfbxiContext uc, UfbxiNode node, out ulong fbxId, string name)
        {
            UfbxMaterial material = UfbxiReadElement.PushSyntheticElement<UfbxMaterial>(uc, out fbxId,
                node, name, UfbxElementType.Material);

            UfbxProp[] tmpProps = new UfbxProp[LegacyMaterialProps.Length];
            int numProps = ReadLegacyProps(node, tmpProps, LegacyMaterialProps);

            material.ShadingModelName = string.Empty;

            // C: ufbxi_push_copy(&uc->result, ufbx_prop, num_props, tmp_props)
            UfbxProp[] props = new UfbxProp[numProps];
            Array.Copy(tmpProps, props, numProps);
            material.Props.Props = props;

            material.ShaderPropPrefix = string.Empty;
        }

        // C: ufbxi_read_legacy_link (ufbx.c:16091-16120).
        internal static void ReadLegacyLink(UfbxiContext uc, UfbxiNode node, out ulong fbxId, string name)
        {
            UfbxSkinCluster cluster = UfbxiReadElement.PushSyntheticElement<UfbxSkinCluster>(uc, out fbxId,
                node, name, UfbxElementType.SkinCluster);

            // TODO(C): Merge with ufbxi_read_skin_cluster(), at least partially?
            UfbxiValueArray indices = node.FindArray(UfbxiStrings.Indexes, 'i');
            UfbxiValueArray weights = node.FindArray(UfbxiStrings.Weights, 'r');

            if (indices != null && weights != null) {
                UfbxiFail.CheckNoDesc(indices.Size == weights.Size, "indices->size == weights->size");
                cluster.NumWeights = indices.Size;
                // C aliases the DOM bytes as uint32/ufbx_real; the port materializes them
                // (bit-exact for little-endian arrays).
                cluster.Vertices = UfbxiObjects.ReadUint32Array(indices, indices.Size);
                cluster.Weights = UfbxiObjects.ReadRealArray(weights, indices.Size);
            }

            UfbxiValueArray transform = node.FindArray(UfbxiStrings.Transform, 'r');
            UfbxiValueArray transformLink = node.FindArray(UfbxiStrings.TransformLink, 'r');
            if (transform != null && transformLink != null) {
                UfbxiFail.CheckNoDesc(transform.Size >= 16, "transform->size >= 16");
                UfbxiFail.CheckNoDesc(transformLink.Size >= 16, "transform_link->size >= 16");

                // C: ufbxi_read_transform_matrix (ufbx.c:13960) — S2-geometry stub.
                UfbxiGeometry.ReadTransformMatrix(out cluster.MeshNodeToBone, transform);
                UfbxiGeometry.ReadTransformMatrix(out cluster.BindToWorld, transformLink);
            }
        }

        // C: ufbxi_read_legacy_light (ufbx.c:16122-16135).
        internal static void ReadLegacyLight(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxLight light = UfbxiReadElement.PushElement<UfbxLight>(uc, info, UfbxElementType.Light);

            UfbxProp[] tmpProps = new UfbxProp[LegacyLightProps.Length];
            int numProps = ReadLegacyProps(node, tmpProps, LegacyLightProps);

            UfbxProp[] props = new UfbxProp[numProps];
            Array.Copy(tmpProps, props, numProps);
            light.Props.Props = props;
        }

        // C: ufbxi_read_legacy_camera (ufbx.c:16137-16150).
        internal static void ReadLegacyCamera(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxCamera camera = UfbxiReadElement.PushElement<UfbxCamera>(uc, info, UfbxElementType.Camera);

            UfbxProp[] tmpProps = new UfbxProp[LegacyCameraProps.Length];
            int numProps = ReadLegacyProps(node, tmpProps, LegacyCameraProps);

            UfbxProp[] props = new UfbxProp[numProps];
            Array.Copy(tmpProps, props, numProps);
            camera.Props.Props = props;
        }

        // C: ufbxi_read_legacy_limb_node (ufbx.c:16152-16170).
        internal static void ReadLegacyLimbNode(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxBone bone = UfbxiReadElement.PushElement<UfbxBone>(uc, info, UfbxElementType.Bone);

            UfbxProp[] tmpProps = new UfbxProp[LegacyBoneProps.Length];
            int numProps = 0;

            UfbxiNode propNode = node.FindChildStrCmp("Properties");
            if (propNode != null) {
                numProps = ReadLegacyProps(propNode, tmpProps, LegacyBoneProps);
            }

            UfbxProp[] props = new UfbxProp[numProps];
            Array.Copy(tmpProps, props, numProps);
            bone.Props.Props = props;
        }

        // C: ufbxi_read_legacy_mesh (ufbx.c:16172-16330).
        internal static void ReadLegacyMesh(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            // Only read polygon meshes, ignore eg. NURBS without error
            UfbxiNode nodeVertices = node.FindChild(UfbxiStrings.Vertices);
            UfbxiNode nodeIndices = node.FindChild(UfbxiStrings.PolygonVertexIndex);
            if (nodeVertices == null || nodeIndices == null) return;

            UfbxMesh mesh = UfbxiReadElement.PushElement<UfbxMesh>(uc, info, UfbxElementType.Mesh);

            // C: ufbxi_read_synthetic_blend_shapes (ufbx.c:13073) — S2-geometry.
            UfbxiGeometry.ReadSyntheticBlendShapes(uc, node, info);

            // C: ufbxi_patch_mesh_reals (ufbx.c:13215) — S2-geometry.
            UfbxiGeometry.PatchMeshReals(mesh);

            if (uc.Opts.IgnoreGeometry) return;

            UfbxiValueArray vertices = nodeVertices.GetArray('r');
            UfbxiValueArray indices = nodeIndices.GetArray('i');
            UfbxiFail.CheckNoDesc(vertices != null && indices != null, "vertices && indices");
            UfbxiFail.CheckNoDesc(vertices.Size % 3 == 0, "vertices->size % 3 == 0");

            mesh.NumVertices = vertices.Size / 3;
            mesh.NumIndices = indices.Size;

            // C: uint32_t *index_data = (uint32_t*)indices->data; the retain_dom branch
            // (ufbx.c:16199-16202) copies before modifying. The port always materializes a
            // fresh array, so the DOM buffer keeps the file bytes either way — under
            // retain_dom identical to C; without retain_dom C's in-place modification is
            // unobservable (the DOM is discarded).
            uint[] indexData = UfbxiObjects.ReadUint32Array(indices, indices.Size);

            UfbxVec3[] verticesArr = UfbxiObjects.ReadVec3Array(vertices, mesh.NumVertices);

            mesh.Vertices = verticesArr;
            mesh.VertexIndices = indexData;

            mesh.VertexPosition.Exists = true;
            mesh.VertexPosition.Values = verticesArr;
            mesh.VertexPosition.Indices = indexData;
            mesh.VertexPosition.UniquePerVertex = true;

            // Check/make sure that the last index is negated (last of polygon)
            if (mesh.NumIndices > 0) {
                if ((int)indexData[mesh.NumIndices - 1] >= 0) {
                    if (uc.Opts.Strict) UfbxiFail.FailNoDesc("Non-negated last index");
                    indexData[mesh.NumIndices - 1] = ~indexData[mesh.NumIndices - 1];
                }
            }

            // C: ufbxi_process_indices (ufbx.c:13135) — pinned in Parse/Geometry.cs.
            UfbxiGeometry.ProcessIndices(uc, mesh, indexData);

            // Normals are either per-vertex or per-index in legacy FBX files?
            // If the version is 5000 prefer per-vertex, otherwise per-index...
            UfbxiValueArray normals = node.FindArray(UfbxiStrings.Normals, 'r');
            if (normals != null) {
                int numNormals = normals.Size / 3;
                bool perVertex = numNormals == mesh.NumVertices;
                bool perIndex = numNormals == mesh.NumIndices;
                if (perVertex && (!perIndex || uc.Version == 5000)) {
                    mesh.VertexNormal.Exists = true;
                    mesh.VertexNormal.Values = UfbxiObjects.ReadVec3Array(normals, numNormals);
                    mesh.VertexNormal.Indices = indexData;
                    mesh.VertexNormal.UniquePerVertex = true;
                } else if (perIndex) {
                    if (mesh.NumIndices > uc.MaxConsecutiveIndices) uc.MaxConsecutiveIndices = mesh.NumIndices;
                    mesh.VertexNormal.Exists = true;
                    mesh.VertexNormal.Values = UfbxiObjects.ReadVec3Array(normals, numNormals);
                    mesh.VertexNormal.Indices = UfbxiObjects.SentinelIndexConsecutive;
                    mesh.VertexNormal.UniquePerVertex = false;
                }
            }

            // Optional UV values are stored pretty much like a modern vertex element
            UfbxiNode uvInfo = node.FindChild(UfbxiStrings.GeometryUVInfo);
            if (uvInfo != null) {
                UfbxUvSet set = default;
                set.Index = 0;
                set.Name = string.Empty; // C: name.data = ufbxi_empty_char (length stays 0)
                // C: ufbxi_read_vertex_element (ufbx.c:12737) for `GeometryUVInfo`, fmt 'r', 2.
                UfbxiGeometry.ReadVertexElementVec2(uc, mesh, uvInfo, ref set.VertexUv,
                    UfbxiStrings.TextureUV, UfbxiStrings.TextureUVVerticeIndex, null);

                mesh.UvSets = new UfbxUvSet[] { set };
                mesh.VertexUv = set.VertexUv;
            }

            // Material indices
            {
                string mapping = null;
                UfbxiFail.CheckNoDesc(node.FindValS(UfbxiStrings.MaterialAssignation, out mapping),
                    "ufbxi_find_val1(node, ufbxi_MaterialAssignation, \"C\", (char**)&mapping)");
                if (mapping == UfbxiStrings.ByPolygon) {
                    // C: ufbxi_read_truncated_array (ufbx.c:12924) — pinned in Parse/Geometry.cs.
                    mesh.FaceMaterial = UfbxiGeometry.ReadTruncatedArrayI32(uc, node, UfbxiStrings.Materials, (uint)mesh.NumFaces);
                } else if (mapping == UfbxiStrings.AllSame) {
                    UfbxiValueArray arr = node.FindArray(UfbxiStrings.Materials, 'i');
                    uint material = 0;
                    if (arr != null && arr.Size >= 1) {
                        material = unchecked((uint)arr.GetInt32(0));
                    }

                    // mesh->face_material.count = mesh->num_faces; the sentinel variant
                    // aliases the shared 1-element static array (count lives in NumFaces).
                    if (material == 0) {
                        mesh.FaceMaterial = UfbxiObjects.SentinelIndexZero;
                    } else {
                        uint[] faceMaterial = new uint[mesh.NumFaces];
                        for (int i = 0; i < mesh.NumFaces; i++) faceMaterial[i] = material;
                        mesh.FaceMaterial = faceMaterial;
                    }
                }
            }

            ulong skinFbxId = 0;
            UfbxSkinDeformer skin = null;

            // Materials, Skin Clusters
            for (uint i = 0; i < node.NumChildren; i++) {
                UfbxiNode child = node.Children[i];
                if (child.Name == UfbxiStrings.Material) {
                    string typeAndName, type, name;
                    UfbxiFail.CheckNoDesc(child.GetValRawString(0, out typeAndName),
                        "ufbxi_get_val1(child, \"s\", &type_and_name)");
                    UfbxiFbxId.SplitTypeAndName(uc, typeAndName, out type, out name);
                    ReadLegacyMaterial(uc, child, out ulong fbxId, name);
                    UfbxiReadElement.ConnectOo(uc, fbxId, info.FbxId);
                } else if (child.Name == UfbxiStrings.Link) {
                    string typeAndName, type, name;
                    UfbxiFail.CheckNoDesc(child.GetValRawString(0, out typeAndName),
                        "ufbxi_get_val1(child, \"s\", &type_and_name)");
                    UfbxiFbxId.SplitTypeAndName(uc, typeAndName, out type, out name);
                    ReadLegacyLink(uc, child, out ulong fbxId, name);

                    ulong nodeFbxId = UfbxiFbxId.SyntheticIdFromString(uc, typeAndName);
                    UfbxiFail.CheckNoDesc(nodeFbxId != 0, "node_fbx_id");
                    UfbxiReadElement.ConnectOo(uc, nodeFbxId, fbxId);
                    if (skin == null) {
                        skin = UfbxiReadElement.PushSyntheticElement<UfbxSkinDeformer>(uc, out skinFbxId,
                            null, info.Name, UfbxElementType.SkinDeformer);
                        UfbxiReadElement.ConnectOo(uc, skinFbxId, info.FbxId);
                    }
                    UfbxiReadElement.ConnectOo(uc, fbxId, skinFbxId);
                }
            }

            mesh.SkinnedIsLocal = true;
            mesh.SkinnedPosition = mesh.VertexPosition;
            mesh.SkinnedNormal = mesh.VertexNormal;

            // C: ufbxi_patch_mesh_reals (ufbx.c:13215) — S2-geometry.
            UfbxiGeometry.PatchMeshReals(mesh);
        }

        // C: ufbxi_read_legacy_media (ufbx.c:16332-16347).
        internal static void ReadLegacyMedia(UfbxiContext uc, UfbxiNode node)
        {
            UfbxiNode videos = node.FindChild(UfbxiStrings.Video);
            if (videos != null) {
                for (uint i = 0; i < videos.NumChildren; i++) {
                    UfbxiNode child = videos.Children[i];
                    UfbxiElementInfo videoInfo = new UfbxiElementInfo();
                    UfbxiFail.CheckNoDesc(child.GetValS(0, out videoInfo.Name),
                        "ufbxi_get_val1(child, \"S\", &video_info.name)");
                    videoInfo.FbxId = UfbxiFbxId.PushSyntheticId(uc);
                    videoInfo.DomNode = UfbxiDom.GetDomNode(uc, node);

                    UfbxiObjects.ReadVideo(uc, child, videoInfo);
                }
            }
        }

        // C: ufbxi_read_legacy_model (ufbx.c:16349-16420).
        internal static void ReadLegacyModel(UfbxiContext uc, UfbxiNode node)
        {
            UfbxiFail.CheckNoDesc(node.GetValRawString(0, out string typeAndName),
                "ufbxi_get_val1(node, \"s\", &type_and_name)");
            UfbxiFbxId.SplitTypeAndName(uc, typeAndName, out string type, out string name);

            UfbxiElementInfo info = new UfbxiElementInfo();
            info.FbxId = UfbxiFbxId.SyntheticIdFromString(uc, typeAndName);
            UfbxiFail.CheckNoDesc(info.FbxId != 0, "info.fbx_id");
            info.Name = name;
            info.DomNode = UfbxiDom.GetDomNode(uc, node);

            UfbxNode elemNode = UfbxiReadElement.PushElement<UfbxNode>(uc, info, UfbxElementType.Node);
            uc.TmpNodeIds.Add(elemNode.ElementId);

            UfbxiElementInfo attribInfo = new UfbxiElementInfo();
            attribInfo.FbxId = UfbxiFbxId.PushSyntheticId(uc);
            attribInfo.Name = name;
            attribInfo.DomNode = info.DomNode;

            // If we make unused connections it doesn't matter..
            UfbxiReadElement.ConnectOo(uc, attribInfo.FbxId, info.FbxId);

            string attribType = string.Empty; // C: ufbxi_empty_char
            UfbxiNode typeNode = node.FindChild(UfbxiStrings.Type);
            if (typeNode != null) {
                typeNode.GetValC(0, out attribType); // C: ufbxi_ignore(ufbxi_find_val1(...))
            }

            bool hasAttrib = true;
            if (attribType == UfbxiStrings.Light) {
                ReadLegacyLight(uc, node, attribInfo);
            } else if (attribType == UfbxiStrings.Camera) {
                ReadLegacyCamera(uc, node, attribInfo);
            } else if (attribType == UfbxiStrings.LimbNode) {
                ReadLegacyLimbNode(uc, node, attribInfo);
            } else if (node.FindChild(UfbxiStrings.Vertices) != null) {
                ReadLegacyMesh(uc, node, attribInfo);
            } else {
                hasAttrib = false;
            }

            // Mark the node as having an attribute so property connections can be forwarded
            if (hasAttrib) {
                UfbxiFbxId.InsertFbxAttr(uc, info.FbxId, attribInfo.FbxId);
            }

            // Children are represented as an array of strings
            UfbxiValueArray children = node.FindArray(UfbxiStrings.Children, 's');
            if (children != null) {
                for (int i = 0; i < children.Size; i++) {
                    ulong childFbxId = UfbxiFbxId.SyntheticIdFromString(uc, children.GetString(i));
                    UfbxiFail.CheckNoDesc(childFbxId != 0, "child_fbx_id");
                    UfbxiReadElement.ConnectOo(uc, childFbxId, info.FbxId);
                }
            }

            // Non-take animation channels
            for (uint i = 0; i < node.NumChildren; i++) {
                UfbxiNode child = node.Children[i];
                if (child.Name == UfbxiStrings.Channel) {
                    if (child.GetValS(0, out string channelName)) {
                        if (uc.LegacyImplicitAnimLayerId == 0) {
                            // Defer creation so we won't be the first animation stack..
                            uc.LegacyImplicitAnimLayerId = UfbxiFbxId.PushSyntheticId(uc);
                        }
                        // C: ufbxi_read_take_prop_channel (ufbx.c:15590).
                        UfbxiAnimReader.ReadTakePropChannel(uc, child, info.FbxId,
                            uc.LegacyImplicitAnimLayerId, channelName);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Filename manipulation
        // ------------------------------------------------------------------

        // C: ufbxi_trim_delimiters (ufbx.c:16486-16497).
        internal static int TrimDelimiters(UfbxiContext uc, string data, int length)
        {
            for (; length > 0; length--) {
                char c = data[length - 1];
                bool isSeparator = c == '/' || c == uc.Opts.PathSeparator;
                if (isSeparator) {
                    length--;
                    break;
                }
            }
            return length;
        }

        // C: ufbxi_init_file_paths (ufbx.c:16499-16528).
        internal static void InitFilePaths(UfbxiContext uc)
        {
            string filename = null;
            int filenameLength = 0;
            byte[] rawFilename = null;
            int rawFilenameSize = 0;

            if (uc.Opts.Filename.Length > 0) {
                filename = uc.Opts.Filename;
                filenameLength = filename.Length;
            } else if (uc.Opts.RawFilename != null && uc.Opts.RawFilename.Length > 0) {
                // C: (const char*)uc->opts.raw_filename.data — the port's raw strings carry
                // one byte per char, so the conversion is lossless.
                filename = UfbxiSanitizedString.FromBytes(uc.Opts.RawFilename, 0, uc.Opts.RawFilename.Length);
                filenameLength = uc.Opts.RawFilename.Length;
            }

            if (uc.Opts.RawFilename != null && uc.Opts.RawFilename.Length > 0) {
                rawFilename = uc.Opts.RawFilename;
                rawFilenameSize = rawFilename.Length;
            } else if (filenameLength > 0) {
                rawFilename = UfbxiSanitizedString.ToBlob(filename, filenameLength);
                rawFilenameSize = filenameLength;
            }

            // C: ufbxi_push_string_place_str(&uc->string_pool, &uc->scene.metadata.filename,
            // false) — (NULL, 0) pools to the canonical empty string.
            if (filename == null) filename = string.Empty;
            uc.StringPool.PushStringPlaceStr(ref filename, false);
            uc.StringPool.PushStringPlaceBlob(ref rawFilename, ref rawFilenameSize, true);

            uc.Scene.Metadata.Filename = filename;
            uc.Scene.Metadata.RawFilename = rawFilename;

            uc.Scene.Metadata.RelativeRoot = filename.Substring(0, TrimDelimiters(uc, filename, filenameLength));

            if (rawFilename != null) {
                int rawRootLength = TrimDelimiters(uc,
                    UfbxiSanitizedString.FromBytes(rawFilename, 0, rawFilenameSize), rawFilenameSize);
                byte[] rawRelativeRoot = new byte[rawRootLength];
                Array.Copy(rawFilename, rawRelativeRoot, rawRootLength);
                uc.Scene.Metadata.RawRelativeRoot = rawRelativeRoot;
            } else {
                uc.Scene.Metadata.RawRelativeRoot = null; // C: {NULL, 0}
            }
        }

        // S2-geometry seam helper — see Parse/Geometry.cs (ufbx.c:12662-14108).
        static void NotPortedGeom(string reader)
        {
            throw new UfbxiReaderNotPortedException(reader);
        }
    }
}
