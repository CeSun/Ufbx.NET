// Parse-state machine and array node classification, ported from ufbx.c v0.23.1:
//   ufbxi_parse_state (ufbx.c:7906-7960), ufbxi_array_flags/ufbxi_array_info (7962-7972),
//   ufbxi_update_parse_state (7974-8082), ufbxi_is_array_node (8084-8498),
//   ufbxi_is_raw_string (8500-8597).
//
// The state machine is a coarse description of where in the FBX tree we are, used to
// resolve array type/allocation and whether a string must stay raw. Names compared by
// pointer in C are interned constants (UfbxiStrings); names compared with strcmp in C are
// written as literals here, exactly as in the source.

namespace Ufbx.NET
{
    // C: typedef enum ufbxi_parse_state
    internal enum UfbxiParseState
    {
        Root,                        // C: UFBXI_PARSE_ROOT
        FbxHeaderExtension,          // C: UFBXI_PARSE_FBX_HEADER_EXTENSION
        SceneInfo,                   // C: UFBXI_PARSE_SCENE_INFO
        Thumbnail,                   // C: UFBXI_PARSE_THUMBNAIL
        Definitions,                 // C: UFBXI_PARSE_DEFINITIONS
        Objects,                     // C: UFBXI_PARSE_OBJECTS
        Connections,                 // C: UFBXI_PARSE_CONNECTIONS
        Relations,                   // C: UFBXI_PARSE_RELATIONS
        Takes,                       // C: UFBXI_PARSE_TAKES
        FbxVersion,                  // C: UFBXI_PARSE_FBX_VERSION
        Model,                       // C: UFBXI_PARSE_MODEL
        Geometry,                    // C: UFBXI_PARSE_GEOMETRY
        NodeAttribute,               // C: UFBXI_PARSE_NODE_ATTRIBUTE
        LegacyModel,                 // C: UFBXI_PARSE_LEGACY_MODEL
        LegacyMedia,                 // C: UFBXI_PARSE_LEGACY_MEDIA
        LegacyVideo,                 // C: UFBXI_PARSE_LEGACY_VIDEO
        LegacySwitcher,              // C: UFBXI_PARSE_LEGACY_SWITCHER
        LegacyScenePersistence,      // C: UFBXI_PARSE_LEGACY_SCENE_PERSISTENCE
        References,                  // C: UFBXI_PARSE_REFERENCES
        Reference,                   // C: UFBXI_PARSE_REFERENCE
        AnimationCurve,              // C: UFBXI_PARSE_ANIMATION_CURVE
        Deformer,                    // C: UFBXI_PARSE_DEFORMER
        AssociateModel,              // C: UFBXI_PARSE_ASSOCIATE_MODEL
        LegacyLink,                  // C: UFBXI_PARSE_LEGACY_LINK
        Pose,                        // C: UFBXI_PARSE_POSE
        PoseNode,                    // C: UFBXI_PARSE_POSE_NODE
        Texture,                     // C: UFBXI_PARSE_TEXTURE
        Video,                       // C: UFBXI_PARSE_VIDEO
        LayeredTexture,              // C: UFBXI_PARSE_LAYERED_TEXTURE
        SelectionNode,               // C: UFBXI_PARSE_SELECTION_NODE
        Collection,                  // C: UFBXI_PARSE_COLLECTION
        Audio,                       // C: UFBXI_PARSE_AUDIO
        UnknownObject,               // C: UFBXI_PARSE_UNKNOWN_OBJECT
        LayerElementNormal,          // C: UFBXI_PARSE_LAYER_ELEMENT_NORMAL
        LayerElementBinormal,        // C: UFBXI_PARSE_LAYER_ELEMENT_BINORMAL
        LayerElementTangent,         // C: UFBXI_PARSE_LAYER_ELEMENT_TANGENT
        LayerElementUv,              // C: UFBXI_PARSE_LAYER_ELEMENT_UV
        LayerElementColor,           // C: UFBXI_PARSE_LAYER_ELEMENT_COLOR
        LayerElementVertexCrease,    // C: UFBXI_PARSE_LAYER_ELEMENT_VERTEX_CREASE
        LayerElementEdgeCrease,      // C: UFBXI_PARSE_LAYER_ELEMENT_EDGE_CREASE
        LayerElementSmoothing,       // C: UFBXI_PARSE_LAYER_ELEMENT_SMOOTHING
        LayerElementVisibility,      // C: UFBXI_PARSE_LAYER_ELEMENT_VISIBILITY
        LayerElementPolygonGroup,    // C: UFBXI_PARSE_LAYER_ELEMENT_POLYGON_GROUP
        LayerElementHole,            // C: UFBXI_PARSE_LAYER_ELEMENT_HOLE
        LayerElementMaterial,        // C: UFBXI_PARSE_LAYER_ELEMENT_MATERIAL
        LayerElementOther,           // C: UFBXI_PARSE_LAYER_ELEMENT_OTHER
        GeometryUvInfo,              // C: UFBXI_PARSE_GEOMETRY_UV_INFO
        Shape,                       // C: UFBXI_PARSE_SHAPE
        Take,                        // C: UFBXI_PARSE_TAKE
        TakeObject,                  // C: UFBXI_PARSE_TAKE_OBJECT
        Channel,                     // C: UFBXI_PARSE_CHANNEL
        Unknown,                     // C: UFBXI_PARSE_UNKNOWN
    }

    // C: typedef enum ufbxi_array_flags
    [System.Flags]
    internal enum UfbxiArrayFlags
    {
        None = 0,
        Result = 0x1,        // C: UFBXI_ARRAY_FLAG_RESULT — allocate from the result buffer
        TmpBuf = 0x2,        // C: UFBXI_ARRAY_FLAG_TMP_BUF — allocate from the long-term temp buffer
        PadBegin = 0x4,      // C: UFBXI_ARRAY_FLAG_PAD_BEGIN — 4 zero elements guard the head
        AccurateF32 = 0x8,   // C: UFBXI_ARRAY_FLAG_ACCURATE_F32 — must be bit-accurate 32-bit floats
    }

    // C: typedef struct ufbxi_array_info (ufbx.c:7968-7972)
    internal struct UfbxiArrayInfo
    {
        public char Type;      // C: char type — 'b','i','l','f','d','r','s','S','C','c' or '-' (ignore)
        public UfbxiArrayFlags Flags; // C: uint8_t flags

        public UfbxiArrayInfo(char type, UfbxiArrayFlags flags)
        {
            Type = type;
            Flags = flags;
        }
    }

    internal static class UfbxiParseStateMachine
    {
        // C: ufbxi_parse_state ufbxi_update_parse_state(ufbxi_parse_state parent, const char *name)
        internal static UfbxiParseState Update(UfbxiParseState parent, string name)
        {
            switch (parent) {

            case UfbxiParseState.Root:
                if (name == UfbxiStrings.FBXHeaderExtension) return UfbxiParseState.FbxHeaderExtension;
                if (name == UfbxiStrings.Definitions) return UfbxiParseState.Definitions;
                if (name == UfbxiStrings.Objects) return UfbxiParseState.Objects;
                if (name == UfbxiStrings.Connections) return UfbxiParseState.Connections;
                if (name == UfbxiStrings.Takes) return UfbxiParseState.Takes;
                if (name == UfbxiStrings.Model) return UfbxiParseState.LegacyModel;
                if (name == "References") return UfbxiParseState.References;
                if (name == "Relations") return UfbxiParseState.Relations;
                if (name == UfbxiStrings.Media) return UfbxiParseState.LegacyMedia;
                if (name == "Switcher") return UfbxiParseState.LegacySwitcher;
                if (name == "SceneGenericPersistence") return UfbxiParseState.LegacyScenePersistence;
                break;

            case UfbxiParseState.FbxHeaderExtension:
                if (name == UfbxiStrings.FBXVersion) return UfbxiParseState.FbxVersion;
                if (name == UfbxiStrings.SceneInfo) return UfbxiParseState.SceneInfo;
                break;

            case UfbxiParseState.SceneInfo:
                if (name == UfbxiStrings.Thumbnail) return UfbxiParseState.Thumbnail;
                break;

            case UfbxiParseState.Objects:
                if (name == UfbxiStrings.Model) return UfbxiParseState.Model;
                if (name == UfbxiStrings.Geometry) return UfbxiParseState.Geometry;
                if (name == UfbxiStrings.NodeAttribute) return UfbxiParseState.NodeAttribute;
                if (name == UfbxiStrings.AnimationCurve) return UfbxiParseState.AnimationCurve;
                if (name == UfbxiStrings.Deformer) return UfbxiParseState.Deformer;
                if (name == UfbxiStrings.Pose) return UfbxiParseState.Pose;
                if (name == UfbxiStrings.Texture) return UfbxiParseState.Texture;
                if (name == UfbxiStrings.Video) return UfbxiParseState.Video;
                if (name == UfbxiStrings.LayeredTexture) return UfbxiParseState.LayeredTexture;
                if (name == UfbxiStrings.SelectionNode) return UfbxiParseState.SelectionNode;
                if (name == UfbxiStrings.Collection) return UfbxiParseState.Collection;
                if (name == UfbxiStrings.Audio) return UfbxiParseState.Audio;
                return UfbxiParseState.UnknownObject;

            case UfbxiParseState.Model:
            case UfbxiParseState.Geometry:
                if (name.Length > 0 && name[0] == 'L') {
                    if (name == UfbxiStrings.LayerElementNormal) return UfbxiParseState.LayerElementNormal;
                    if (name == UfbxiStrings.LayerElementBinormal) return UfbxiParseState.LayerElementBinormal;
                    if (name == UfbxiStrings.LayerElementTangent) return UfbxiParseState.LayerElementTangent;
                    if (name == UfbxiStrings.LayerElementUV) return UfbxiParseState.LayerElementUv;
                    if (name == UfbxiStrings.LayerElementColor) return UfbxiParseState.LayerElementColor;
                    if (name == UfbxiStrings.LayerElementVertexCrease) return UfbxiParseState.LayerElementVertexCrease;
                    if (name == UfbxiStrings.LayerElementEdgeCrease) return UfbxiParseState.LayerElementEdgeCrease;
                    if (name == UfbxiStrings.LayerElementSmoothing) return UfbxiParseState.LayerElementSmoothing;
                    if (name == UfbxiStrings.LayerElementVisibility) return UfbxiParseState.LayerElementVisibility;
                    if (name == UfbxiStrings.LayerElementPolygonGroup) return UfbxiParseState.LayerElementPolygonGroup;
                    if (name == UfbxiStrings.LayerElementHole) return UfbxiParseState.LayerElementHole;
                    if (name == UfbxiStrings.LayerElementMaterial) return UfbxiParseState.LayerElementMaterial;
                    if (name.Length >= 12 && name.Substring(0, 12) == "LayerElement") return UfbxiParseState.LayerElementOther;
                }
                if (name == UfbxiStrings.Shape) return UfbxiParseState.Shape;
                break;

            case UfbxiParseState.Deformer:
                if (name == "AssociateModel") return UfbxiParseState.AssociateModel;
                break;

            case UfbxiParseState.LegacyMedia:
                if (name == UfbxiStrings.Video) return UfbxiParseState.LegacyVideo;
                break;

            case UfbxiParseState.LegacyVideo:
                return UfbxiParseState.Video;

            case UfbxiParseState.LegacyModel:
                if (name == UfbxiStrings.GeometryUVInfo) return UfbxiParseState.GeometryUvInfo;
                if (name == UfbxiStrings.Link) return UfbxiParseState.LegacyLink;
                if (name == UfbxiStrings.Channel) return UfbxiParseState.Channel;
                if (name == UfbxiStrings.Shape) return UfbxiParseState.Shape;
                break;

            case UfbxiParseState.Pose:
                if (name == UfbxiStrings.PoseNode) return UfbxiParseState.PoseNode;
                break;

            case UfbxiParseState.Takes:
                if (name == UfbxiStrings.Take) return UfbxiParseState.Take;
                break;

            case UfbxiParseState.Take:
                return UfbxiParseState.TakeObject;

            case UfbxiParseState.TakeObject:
                if (name == UfbxiStrings.Channel) return UfbxiParseState.Channel;
                break;

            case UfbxiParseState.Channel:
                if (name == UfbxiStrings.Channel) return UfbxiParseState.Channel;
                break;

            case UfbxiParseState.References:
                return UfbxiParseState.Reference;

            default:
                break;

            }

            return UfbxiParseState.Unknown;
        }

        // C: bool ufbxi_is_array_node(ufbxi_context*, ufbxi_parse_state, const char*, ufbxi_array_info*)
        internal static bool IsArrayNode(UfbxiContext uc, UfbxiParseState parent, string name, out UfbxiArrayInfo info)
        {
            UfbxiArrayFlags flags = UfbxiArrayFlags.None;

            // Retain all arrays if user wants the DOM representation
            if (uc.Opts.RetainDom) {
                flags |= UfbxiArrayFlags.Result;
            }

            bool ignoreGeometry = uc.Opts.IgnoreGeometry;
            bool ignoreAnimation = uc.Opts.IgnoreAnimation;
            bool ignoreEmbedded = uc.Opts.IgnoreEmbedded;
            bool retainDom = uc.Opts.RetainDom;

            // The accumulated `flags` (only the retain_dom Result bit so far) is combined
            // per branch, mirroring C's `info->flags = ...` vs `info->flags |= ...`.
            char type = '\0';

            switch (parent) {

            case UfbxiParseState.Thumbnail:
                if (name == UfbxiStrings.ImageData) {
                    flags |= UfbxiArrayFlags.Result;
                    info = new UfbxiArrayInfo('c', flags);
                    return true;
                }
                break;

            case UfbxiParseState.Geometry:
            case UfbxiParseState.Model:
                if (name == UfbxiStrings.Vertices) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.PolygonVertexIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.Edges) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', flags);
                    return true;
                } else if (name == UfbxiStrings.Indexes) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.Points) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.KnotVector) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.KnotVectorU) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.KnotVectorV) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.PointsIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.Normals) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                }
                break;

            case UfbxiParseState.LegacyModel:
                if (name == UfbxiStrings.Vertices) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.Normals) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.Materials) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.PolygonVertexIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.Children) {
                    info = new UfbxiArrayInfo('s', flags);
                    return true;
                }
                break;

            case UfbxiParseState.AnimationCurve:
                if (name == UfbxiStrings.KeyTime) {
                    info = new UfbxiArrayInfo(ignoreAnimation ? '-' : 'l', flags);
                    return true;
                } else if (name == UfbxiStrings.KeyValueFloat) {
                    info = new UfbxiArrayInfo(ignoreAnimation ? '-' : 'r', flags);
                    return true;
                } else if (name == UfbxiStrings.KeyAttrFlags) {
                    info = new UfbxiArrayInfo(ignoreAnimation ? '-' : 'i', flags);
                    return true;
                } else if (name == UfbxiStrings.KeyAttrDataFloat) {
                    // The float data in a keyframe attribute array is represented as integers
                    // in versions >= 7200 as some of the elements aren't actually floats (!)
                    type = uc.FromAscii && uc.Version >= 7200 ? 'i' : 'f';
                    if (ignoreAnimation) type = '-';
                    if (uc.FromAscii && uc.Version < 7200) {
                        flags |= UfbxiArrayFlags.AccurateF32;
                    }
                    info = new UfbxiArrayInfo(type, flags);
                    return true;
                } else if (name == UfbxiStrings.KeyAttrRefCount) {
                    info = new UfbxiArrayInfo(ignoreAnimation ? '-' : 'i', flags);
                    return true;
                }
                break;

            case UfbxiParseState.Texture:
                if (name == "ModelUVTranslation" || name == "ModelUVScaling" || name == "Cropping") {
                    info = new UfbxiArrayInfo(retainDom ? 'r' : '-', flags);
                    return true;
                }
                break;

            case UfbxiParseState.Video:
                if (name == UfbxiStrings.Content) {
                    info = new UfbxiArrayInfo(ignoreEmbedded ? '-' : 'C', flags);
                    return true;
                }
                break;

            case UfbxiParseState.LayeredTexture:
                if (name == UfbxiStrings.BlendModes) {
                    flags |= UfbxiArrayFlags.TmpBuf;
                    info = new UfbxiArrayInfo('i', flags);
                    return true;
                } else if (name == UfbxiStrings.Alphas) {
                    flags |= UfbxiArrayFlags.TmpBuf;
                    info = new UfbxiArrayInfo('r', flags);
                    return true;
                }
                break;

            case UfbxiParseState.SelectionNode:
                if (name == UfbxiStrings.VertexIndexArray) {
                    info = new UfbxiArrayInfo('i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.EdgeIndexArray) {
                    info = new UfbxiArrayInfo('i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.PolygonIndexArray) {
                    info = new UfbxiArrayInfo('i', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementNormal:
                if (name == UfbxiStrings.Normals) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.NormalsIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.NormalsW) {
                    info = new UfbxiArrayInfo(uc.RetainVertexW ? 'r' : '-', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementBinormal:
                if (name == UfbxiStrings.Binormals) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.BinormalsIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.BinormalsW) {
                    info = new UfbxiArrayInfo(uc.RetainVertexW ? 'r' : '-', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementTangent:
                if (name == UfbxiStrings.Tangents) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.TangentsIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.TangentsW) {
                    info = new UfbxiArrayInfo(uc.RetainVertexW ? 'r' : '-', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementUv:
                if (name == UfbxiStrings.UV) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.UVIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementColor:
                if (name == UfbxiStrings.Colors) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.ColorIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementVertexCrease:
                if (name == UfbxiStrings.VertexCrease) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.VertexCreaseIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementEdgeCrease:
                if (name == UfbxiStrings.EdgeCrease) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementSmoothing:
                if (name == UfbxiStrings.Smoothing) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'b', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementVisibility:
                if (name == UfbxiStrings.Visibility) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'b', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementPolygonGroup:
                if (name == UfbxiStrings.PolygonGroup) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementHole:
                if (name == UfbxiStrings.Hole) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'b', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementMaterial:
                if (name == UfbxiStrings.Materials) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.LayerElementOther:
                if (name == UfbxiStrings.TextureId) {
                    flags |= UfbxiArrayFlags.TmpBuf;
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', flags);
                    return true;
                } else if (name == UfbxiStrings.UV) {
                    info = new UfbxiArrayInfo(retainDom ? 'r' : '-', flags);
                    return true;
                } else if (name == UfbxiStrings.UVIndex) {
                    info = new UfbxiArrayInfo(retainDom ? 'i' : '-', flags);
                    return true;
                }
                break;

            case UfbxiParseState.GeometryUvInfo:
                if (name == UfbxiStrings.TextureUV) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                } else if (name == UfbxiStrings.TextureUVVerticeIndex) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                }
                break;

            case UfbxiParseState.Shape:
                if (name == UfbxiStrings.Indexes) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                }
                if (name == UfbxiStrings.Vertices) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                }
                if (name == UfbxiStrings.Normals) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result | UfbxiArrayFlags.PadBegin);
                    return true;
                }
                break;

            case UfbxiParseState.Deformer:
                if (name == UfbxiStrings.Transform) {
                    info = new UfbxiArrayInfo('r', flags);
                    return true;
                } else if (name == UfbxiStrings.TransformLink) {
                    info = new UfbxiArrayInfo('r', flags);
                    return true;
                } else if (name == UfbxiStrings.Indexes) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.Weights) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.BlendWeights) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.FullWeights) {
                    // C: info->flags = (uint8_t)(info->flags | (uc->blender_full_weights ? RESULT : TMP_BUF))
                    flags |= uc.BlenderFullWeights ? UfbxiArrayFlags.Result : UfbxiArrayFlags.TmpBuf;
                    info = new UfbxiArrayInfo('r', flags);
                    return true;
                } else if (name == "TransformAssociateModel") {
                    info = new UfbxiArrayInfo(retainDom ? 'r' : '-', flags);
                    return true;
                }
                break;

            case UfbxiParseState.AssociateModel:
                if (name == UfbxiStrings.Transform) {
                    info = new UfbxiArrayInfo(retainDom ? 'r' : '-', flags);
                    return true;
                }
                break;

            case UfbxiParseState.LegacyLink:
                if (name == UfbxiStrings.Transform) {
                    info = new UfbxiArrayInfo('r', flags);
                    return true;
                } else if (name == UfbxiStrings.TransformLink) {
                    info = new UfbxiArrayInfo('r', flags);
                    return true;
                } else if (name == UfbxiStrings.Indexes) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'i', UfbxiArrayFlags.Result);
                    return true;
                } else if (name == UfbxiStrings.Weights) {
                    info = new UfbxiArrayInfo(ignoreGeometry ? '-' : 'r', UfbxiArrayFlags.Result);
                    return true;
                }
                break;

            case UfbxiParseState.PoseNode:
                if (name == UfbxiStrings.Matrix) {
                    info = new UfbxiArrayInfo('r', flags);
                    return true;
                }
                break;

            case UfbxiParseState.Channel:
                if (name == UfbxiStrings.Key) {
                    info = new UfbxiArrayInfo(ignoreAnimation ? '-' : 'd', flags);
                    return true;
                }
                break;

            case UfbxiParseState.Audio:
                if (name == UfbxiStrings.Content) {
                    info = new UfbxiArrayInfo(ignoreEmbedded ? '-' : 'C', flags);
                    return true;
                }
                break;

            default:
                if (name == UfbxiStrings.BinaryData) {
                    info = new UfbxiArrayInfo(ignoreEmbedded ? '-' : 'C', flags);
                    return true;
                }
                break;

            }

            info = default;
            return false;
        }

        // C: bool ufbxi_is_raw_string(ufbxi_context*, ufbxi_parse_state, const char*, size_t index)
        internal static bool IsRawString(UfbxiContext uc, UfbxiParseState parent, string name, int index)
        {
            _ = index; // C: (void)index

            switch (parent) {

            case UfbxiParseState.Root:
                if (name == UfbxiStrings.Model) return true;
                if (name == "FileId") return true;
                break;

            case UfbxiParseState.FbxHeaderExtension:
                if (name == UfbxiStrings.SceneInfo) return true;
                break;

            case UfbxiParseState.Objects:
                return true;

            case UfbxiParseState.Connections:
            case UfbxiParseState.Relations:
                // Pre-7000 needs raw strings for "Name\x00\x01Type" pairs, post-7000 uses it only
                // for properties that are non-raw by default.
                return uc.Version < 7000;

            case UfbxiParseState.Model:
                if (name == UfbxiStrings.NodeAttributeName) return true;
                if (name == UfbxiStrings.Name) return true;
                break;

            case UfbxiParseState.Video:
                if (name == UfbxiStrings.Content) return true;
                break;

            case UfbxiParseState.Texture:
                if (name == "TextureName") return true;
                if (name == UfbxiStrings.Media) return true;
                break;

            case UfbxiParseState.Geometry:
                if (name == UfbxiStrings.NodeAttributeName) return true;
                if (name == UfbxiStrings.Name) return true;
                break;

            case UfbxiParseState.NodeAttribute:
                if (name == UfbxiStrings.NodeAttributeName) return true;
                if (name == UfbxiStrings.Name) return true;
                break;

            case UfbxiParseState.PoseNode:
                if (name == UfbxiStrings.Node) return true;
                break;

            case UfbxiParseState.SelectionNode:
                if (name == UfbxiStrings.Node) return true;
                break;

            case UfbxiParseState.UnknownObject:
                if (name == UfbxiStrings.NodeAttributeName) return true;
                if (name == UfbxiStrings.Name) return true;
                break;

            case UfbxiParseState.Collection:
                if (name == "Member") return true;
                break;

            case UfbxiParseState.Audio:
                if (name == UfbxiStrings.Content) return true;
                break;

            case UfbxiParseState.LegacyModel:
                if (name == UfbxiStrings.Material) return true;
                if (name == UfbxiStrings.Link) return true;
                if (name == UfbxiStrings.Name) return true;
                break;

            case UfbxiParseState.LegacySwitcher:
                if (name == "CameraIndexName") return true;
                break;

            case UfbxiParseState.LegacyScenePersistence:
                if (name == UfbxiStrings.SceneInfo) return true;
                break;

            case UfbxiParseState.Reference:
                if (name == "Object") return true;
                break;

            case UfbxiParseState.Take:
                if (name == UfbxiStrings.Model) return true;
                break;

            default:
                break;

            }

            return false;
        }
    }
}
