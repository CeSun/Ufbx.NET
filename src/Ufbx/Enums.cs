// All `typedef enum` types ported from ufbx v0.23.1 (ufbx.h).
//
// Conventions (PORTING_NOTES.md):
//  - `UFBX_XXX_VALUE` -> PascalCase member with the UFBX_XXX_ / UFBX_ prefix stripped.
//  - Values keep the explicit C numeric values where present; otherwise sequential from 0.
//  - `UFBX_ENUM_FORCE_WIDTH(...)` / `UFBX_FLAG_FORCE_WIDTH(...)` sentinels
//    (`UFBX_XXX_FORCE_32BIT = 0x7fffffff`) are omitted; the underlying type is `int`.
//  - C alias entries (e.g. UFBX_ELEMENT_TYPE_FIRST_ATTRIB) are kept as duplicate-valued
//    members, which C# enums support.

namespace Ufbx
{
    // C: ufbx_rotation_order
    // Order in which Euler-angle rotation axes are applied for a transform.
    // NOTE from C header: the order in the name refers to the order of axes *applied*,
    // not the multiplication order: eg. `UFBX_ROTATION_ORDER_XYZ` is `Z*Y*X`.
    public enum UfbxRotationOrder
    {
        Xyz,        // C: UFBX_ROTATION_ORDER_XYZ
        Xzy,        // C: UFBX_ROTATION_ORDER_XZY
        Yzx,        // C: UFBX_ROTATION_ORDER_YZX
        Yxz,        // C: UFBX_ROTATION_ORDER_YXZ
        Zxy,        // C: UFBX_ROTATION_ORDER_ZXY
        Zyx,        // C: UFBX_ROTATION_ORDER_ZYX
        Spheric,    // C: UFBX_ROTATION_ORDER_SPHERIC
    }

    // C: ufbx_dom_value_type
    public enum UfbxDomValueType
    {
        Number,         // C: UFBX_DOM_VALUE_NUMBER
        String,         // C: UFBX_DOM_VALUE_STRING
        Blob,           // C: UFBX_DOM_VALUE_BLOB
        ArrayI32,       // C: UFBX_DOM_VALUE_ARRAY_I32
        ArrayI64,       // C: UFBX_DOM_VALUE_ARRAY_I64
        ArrayF32,       // C: UFBX_DOM_VALUE_ARRAY_F32
        ArrayF64,       // C: UFBX_DOM_VALUE_ARRAY_F64
        ArrayBlob,      // C: UFBX_DOM_VALUE_ARRAY_BLOB
        ArrayIgnored,   // C: UFBX_DOM_VALUE_ARRAY_IGNORED
    }

    // C: ufbx_prop_type
    // Data type contained within the property. All the data fields are always populated
    // regardless of type, so there's no need to switch by type usually eg.
    // `prop->value_real` and `prop->value_int` have the same value (well, close) if
    // `prop->type == UFBX_PROP_INTEGER`. String values are not converted from/to.
    public enum UfbxPropType
    {
        Unknown,            // C: UFBX_PROP_UNKNOWN
        Boolean,            // C: UFBX_PROP_BOOLEAN
        Integer,            // C: UFBX_PROP_INTEGER
        Number,             // C: UFBX_PROP_NUMBER
        Vector,             // C: UFBX_PROP_VECTOR
        Color,              // C: UFBX_PROP_COLOR
        ColorWithAlpha,     // C: UFBX_PROP_COLOR_WITH_ALPHA
        String,             // C: UFBX_PROP_STRING
        DateTime,           // C: UFBX_PROP_DATE_TIME
        Translation,        // C: UFBX_PROP_TRANSLATION
        Rotation,           // C: UFBX_PROP_ROTATION
        Scaling,            // C: UFBX_PROP_SCALING
        Distance,           // C: UFBX_PROP_DISTANCE
        Compound,           // C: UFBX_PROP_COMPOUND
        Blob,               // C: UFBX_PROP_BLOB
        Reference,          // C: UFBX_PROP_REFERENCE
    }

    // C: ufbx_prop_flags
    // Property flags: advanced information about properties, not usually needed.
    public enum UfbxPropFlags
    {
        // Supports animation.
        // NOTE: ufbx ignores this and allows animations on non-animatable properties.
        Animatable = 0x1,       // C: UFBX_PROP_FLAG_ANIMATABLE

        // User defined (custom) property.
        UserDefined = 0x2,      // C: UFBX_PROP_FLAG_USER_DEFINED

        // Hidden in UI.
        Hidden = 0x4,           // C: UFBX_PROP_FLAG_HIDDEN

        // Disallow modification from UI for components.
        LockX = 0x10,           // C: UFBX_PROP_FLAG_LOCK_X
        LockY = 0x20,           // C: UFBX_PROP_FLAG_LOCK_Y
        LockZ = 0x40,           // C: UFBX_PROP_FLAG_LOCK_Z
        LockW = 0x80,           // C: UFBX_PROP_FLAG_LOCK_W

        // Disable animation from components.
        MuteX = 0x100,          // C: UFBX_PROP_FLAG_MUTE_X
        MuteY = 0x200,          // C: UFBX_PROP_FLAG_MUTE_Y
        MuteZ = 0x400,          // C: UFBX_PROP_FLAG_MUTE_Z
        MuteW = 0x800,          // C: UFBX_PROP_FLAG_MUTE_W

        // Property created by ufbx when an element has a connected `ufbx_anim_prop`
        // but doesn't contain the `ufbx_prop` it's referring to.
        // NOTE: The property may have been found in the templated defaults.
        Synthetic = 0x1000,     // C: UFBX_PROP_FLAG_SYNTHETIC

        // The property has at least one `ufbx_anim_prop` in some layer.
        Animated = 0x2000,      // C: UFBX_PROP_FLAG_ANIMATED

        // Used by `ufbx_evaluate_prop()` to indicate the the property was not found.
        NotFound = 0x4000,      // C: UFBX_PROP_FLAG_NOT_FOUND

        // The property is connected to another one.
        // This use case is relatively rare so `ufbx_prop` does not track connections
        // directly. You can find connections from `ufbx_element.connections_dst` where
        // `ufbx_connection.dst_prop` is this property and `ufbx_connection.src_prop` is defined.
        Connected = 0x8000,     // C: UFBX_PROP_FLAG_CONNECTED

        // The value of this property is undefined (represented as zero).
        NoValue = 0x10000,      // C: UFBX_PROP_FLAG_NO_VALUE

        // This property has been overridden by the user.
        // See `ufbx_anim.prop_overrides` for more information.
        Overridden = 0x20000,   // C: UFBX_PROP_FLAG_OVERRIDDEN

        // Value type.
        // `REAL/VEC2/VEC3/VEC4` are mutually exclusive but may coexist with eg. `STRING`
        // in some rare cases where the string defines the unit for the vector.
        ValueReal = 0x100000,   // C: UFBX_PROP_FLAG_VALUE_REAL
        ValueVec2 = 0x200000,   // C: UFBX_PROP_FLAG_VALUE_VEC2
        ValueVec3 = 0x400000,   // C: UFBX_PROP_FLAG_VALUE_VEC3
        ValueVec4 = 0x800000,   // C: UFBX_PROP_FLAG_VALUE_VEC4
        ValueInt = 0x1000000,   // C: UFBX_PROP_FLAG_VALUE_INT
        ValueStr = 0x2000000,   // C: UFBX_PROP_FLAG_VALUE_STR
        ValueBlob = 0x4000000,  // C: UFBX_PROP_FLAG_VALUE_BLOB
    }

    // C: ufbx_element_type
    // Element is the lowest level representation of the FBX file in ufbx.
    // An element contains type, id, name, and properties (see `ufbx_props`).
    // Elements may be connected to each other arbitrarily via `ufbx_connection`.
    // Each value documents the corresponding C struct.
    public enum UfbxElementType
    {
        Unknown = 0,                // C: UFBX_ELEMENT_UNKNOWN (`ufbx_unknown`)
        Node = 1,                   // C: UFBX_ELEMENT_NODE (`ufbx_node`)
        Mesh = 2,                   // C: UFBX_ELEMENT_MESH (`ufbx_mesh`)
        Light = 3,                  // C: UFBX_ELEMENT_LIGHT (`ufbx_light`)
        Camera = 4,                 // C: UFBX_ELEMENT_CAMERA (`ufbx_camera`)
        Bone = 5,                   // C: UFBX_ELEMENT_BONE (`ufbx_bone`)
        Empty = 6,                  // C: UFBX_ELEMENT_EMPTY (`ufbx_empty`)
        LineCurve = 7,              // C: UFBX_ELEMENT_LINE_CURVE (`ufbx_line_curve`)
        NurbsCurve = 8,             // C: UFBX_ELEMENT_NURBS_CURVE (`ufbx_nurbs_curve`)
        NurbsSurface = 9,           // C: UFBX_ELEMENT_NURBS_SURFACE (`ufbx_nurbs_surface`)
        NurbsTrimSurface = 10,      // C: UFBX_ELEMENT_NURBS_TRIM_SURFACE (`ufbx_nurbs_trim_surface`)
        NurbsTrimBoundary = 11,     // C: UFBX_ELEMENT_NURBS_TRIM_BOUNDARY (`ufbx_nurbs_trim_boundary`)
        ProceduralGeometry = 12,    // C: UFBX_ELEMENT_PROCEDURAL_GEOMETRY (`ufbx_procedural_geometry`)
        StereoCamera = 13,          // C: UFBX_ELEMENT_STEREO_CAMERA (`ufbx_stereo_camera`)
        CameraSwitcher = 14,        // C: UFBX_ELEMENT_CAMERA_SWITCHER (`ufbx_camera_switcher`)
        Marker = 15,                // C: UFBX_ELEMENT_MARKER (`ufbx_marker`)
        LodGroup = 16,              // C: UFBX_ELEMENT_LOD_GROUP (`ufbx_lod_group`)
        SkinDeformer = 17,          // C: UFBX_ELEMENT_SKIN_DEFORMER (`ufbx_skin_deformer`)
        SkinCluster = 18,           // C: UFBX_ELEMENT_SKIN_CLUSTER (`ufbx_skin_cluster`)
        BlendDeformer = 19,         // C: UFBX_ELEMENT_BLEND_DEFORMER (`ufbx_blend_deformer`)
        BlendChannel = 20,          // C: UFBX_ELEMENT_BLEND_CHANNEL (`ufbx_blend_channel`)
        BlendShape = 21,            // C: UFBX_ELEMENT_BLEND_SHAPE (`ufbx_blend_shape`)
        CacheDeformer = 22,         // C: UFBX_ELEMENT_CACHE_DEFORMER (`ufbx_cache_deformer`)
        CacheFile = 23,             // C: UFBX_ELEMENT_CACHE_FILE (`ufbx_cache_file`)
        Material = 24,              // C: UFBX_ELEMENT_MATERIAL (`ufbx_material`)
        Texture = 25,               // C: UFBX_ELEMENT_TEXTURE (`ufbx_texture`)
        Video = 26,                 // C: UFBX_ELEMENT_VIDEO (`ufbx_video`)
        Shader = 27,                // C: UFBX_ELEMENT_SHADER (`ufbx_shader`)
        ShaderBinding = 28,         // C: UFBX_ELEMENT_SHADER_BINDING (`ufbx_shader_binding`)
        AnimStack = 29,             // C: UFBX_ELEMENT_ANIM_STACK (`ufbx_anim_stack`)
        AnimLayer = 30,             // C: UFBX_ELEMENT_ANIM_LAYER (`ufbx_anim_layer`)
        AnimValue = 31,             // C: UFBX_ELEMENT_ANIM_VALUE (`ufbx_anim_value`)
        AnimCurve = 32,             // C: UFBX_ELEMENT_ANIM_CURVE (`ufbx_anim_curve`)
        DisplayLayer = 33,          // C: UFBX_ELEMENT_DISPLAY_LAYER (`ufbx_display_layer`)
        SelectionSet = 34,          // C: UFBX_ELEMENT_SELECTION_SET (`ufbx_selection_set`)
        SelectionNode = 35,         // C: UFBX_ELEMENT_SELECTION_NODE (`ufbx_selection_node`)
        Character = 36,             // C: UFBX_ELEMENT_CHARACTER (`ufbx_character`)
        Constraint = 37,            // C: UFBX_ELEMENT_CONSTRAINT (`ufbx_constraint`)
        AudioLayer = 38,            // C: UFBX_ELEMENT_AUDIO_LAYER (`ufbx_audio_layer`)
        AudioClip = 39,             // C: UFBX_ELEMENT_AUDIO_CLIP (`ufbx_audio_clip`)
        Pose = 40,                  // C: UFBX_ELEMENT_POSE (`ufbx_pose`)
        MetadataObject = 41,        // C: UFBX_ELEMENT_METADATA_OBJECT (`ufbx_metadata_object`)

        // C: UFBX_ELEMENT_TYPE_FIRST_ATTRIB = UFBX_ELEMENT_MESH (alias)
        FirstAttrib = 2,
        // C: UFBX_ELEMENT_TYPE_LAST_ATTRIB = UFBX_ELEMENT_LOD_GROUP (alias)
        LastAttrib = 16,
    }

    // C: ufbx_inherit_mode
    // Inherit type specifies how hierarchial node transforms are combined.
    // This only affects the final scaling, as rotation and translation are always
    // inherited correctly.
    // NOTE: These don't map to `"InheritType"` property as there may be new ones for
    // compatibility with various exporters.
    public enum UfbxInheritMode
    {
        // Normal matrix composition of hierarchy: `R*S*r*s`.
        //   child.node_to_world = parent.node_to_world * child.node_to_parent;
        Normal,                 // C: UFBX_INHERIT_MODE_NORMAL

        // Ignore parent scale when computing the transform: `R*r*s`.
        // Also known as "Segment scale compensate" in some software.
        IgnoreParentScale,      // C: UFBX_INHERIT_MODE_IGNORE_PARENT_SCALE

        // Apply parent scale component-wise: `R*r*S*s`.
        ComponentwiseScale,     // C: UFBX_INHERIT_MODE_COMPONENTWISE_SCALE
    }

    // C: ufbx_mirror_axis
    // Axis used to mirror transformations for handedness conversion.
    public enum UfbxMirrorAxis
    {
        None,   // C: UFBX_MIRROR_AXIS_NONE
        X,      // C: UFBX_MIRROR_AXIS_X
        Y,      // C: UFBX_MIRROR_AXIS_Y
        Z,      // C: UFBX_MIRROR_AXIS_Z
    }

    // C: ufbx_subdivision_display_mode
    public enum UfbxSubdivisionDisplayMode
    {
        Disabled,       // C: UFBX_SUBDIVISION_DISPLAY_DISABLED
        Hull,           // C: UFBX_SUBDIVISION_DISPLAY_HULL
        HullAndSmooth,  // C: UFBX_SUBDIVISION_DISPLAY_HULL_AND_SMOOTH
        Smooth,         // C: UFBX_SUBDIVISION_DISPLAY_SMOOTH
    }

    // C: ufbx_subdivision_boundary
    public enum UfbxSubdivisionBoundary
    {
        Default,        // C: UFBX_SUBDIVISION_BOUNDARY_DEFAULT
        Legacy,         // C: UFBX_SUBDIVISION_BOUNDARY_LEGACY
        // OpenSubdiv: `VTX_BOUNDARY_EDGE_AND_CORNER` / `FVAR_LINEAR_CORNERS_ONLY`
        SharpCorners,   // C: UFBX_SUBDIVISION_BOUNDARY_SHARP_CORNERS
        // OpenSubdiv: `VTX_BOUNDARY_EDGE_ONLY` / `FVAR_LINEAR_NONE`
        SharpNone,      // C: UFBX_SUBDIVISION_BOUNDARY_SHARP_NONE
        // OpenSubdiv: `FVAR_LINEAR_BOUNDARIES`
        SharpBoundary,  // C: UFBX_SUBDIVISION_BOUNDARY_SHARP_BOUNDARY
        // OpenSubdiv: `FVAR_LINEAR_ALL`
        SharpInterior,  // C: UFBX_SUBDIVISION_BOUNDARY_SHARP_INTERIOR
    }

    // C: ufbx_light_type
    // The kind of light source
    public enum UfbxLightType
    {
        // Single point at local origin, at `node->world_transform.position`
        Point,          // C: UFBX_LIGHT_POINT

        // Infinite directional light pointing locally towards `light->local_direction`
        // For global: `ufbx_transform_direction(&node->node_to_world, light->local_direction)`
        Directional,    // C: UFBX_LIGHT_DIRECTIONAL

        // Cone shaped light towards `light->local_direction`, between `light->inner/outer_angle`.
        Spot,           // C: UFBX_LIGHT_SPOT

        // Area light, shape specified by `light->area_shape`
        Area,           // C: UFBX_LIGHT_AREA

        // Volumetric light source
        Volume,         // C: UFBX_LIGHT_VOLUME
    }

    // C: ufbx_light_decay
    // How fast does the light intensity decay at a distance
    public enum UfbxLightDecay
    {
        None = 0,       // C: UFBX_LIGHT_DECAY_NONE (1, no decay)
        Linear = 1,     // C: UFBX_LIGHT_DECAY_LINEAR (1 / d)
        Quadratic = 2,  // C: UFBX_LIGHT_DECAY_QUADRATIC (1 / d^2, physically accurate)
        Cubic = 3,      // C: UFBX_LIGHT_DECAY_CUBIC (1 / d^3)
    }

    // C: ufbx_light_area_shape
    public enum UfbxLightAreaShape
    {
        Rectangle,  // C: UFBX_LIGHT_AREA_SHAPE_RECTANGLE
        Sphere,     // C: UFBX_LIGHT_AREA_SHAPE_SPHERE
    }

    // C: ufbx_projection_mode
    public enum UfbxProjectionMode
    {
        Perspective,    // C: UFBX_PROJECTION_MODE_PERSPECTIVE
        Orthographic,   // C: UFBX_PROJECTION_MODE_ORTHOGRAPHIC
    }

    // C: ufbx_aspect_mode
    // Method of specifying the rendering resolution from properties.
    // NOTE: Handled internally by ufbx, ignore unless you interpret `ufbx_props` directly!
    public enum UfbxAspectMode
    {
        // No defined resolution
        WindowSize,         // C: UFBX_ASPECT_MODE_WINDOW_SIZE

        // `"AspectWidth"` and `"AspectHeight"` are relative to each other
        FixedRatio,         // C: UFBX_ASPECT_MODE_FIXED_RATIO

        // `"AspectWidth"` and `"AspectHeight"` are both pixels
        FixedResolution,    // C: UFBX_ASPECT_MODE_FIXED_RESOLUTION

        // `"AspectWidth"` is pixels, `"AspectHeight"` is relative to width
        FixedWidth,         // C: UFBX_ASPECT_MODE_FIXED_WIDTH

        // `"AspectHeight"` is pixels, `"AspectWidth"` is relative to height
        FixedHeight,        // C: UFBX_ASPECT_MODE_FIXED_HEIGHT
    }

    // C: ufbx_aperture_mode
    // Method of specifying the field of view from properties.
    // NOTE: Handled internally by ufbx, ignore unless you interpret `ufbx_props` directly!
    public enum UfbxApertureMode
    {
        // Use separate `"FieldOfViewX"` and `"FieldOfViewY"` as horizontal/vertical FOV angles
        HorizontalAndVertical,  // C: UFBX_APERTURE_MODE_HORIZONTAL_AND_VERTICAL

        // Use `"FieldOfView"` as horizontal FOV angle, derive vertical angle via aspect ratio
        Horizontal,             // C: UFBX_APERTURE_MODE_HORIZONTAL

        // Use `"FieldOfView"` as vertical FOV angle, derive horizontal angle via aspect ratio
        Vertical,               // C: UFBX_APERTURE_MODE_VERTICAL

        // Compute the field of view from the render gate size and focal length
        FocalLength,            // C: UFBX_APERTURE_MODE_FOCAL_LENGTH
    }

    // C: ufbx_gate_fit
    // Method of specifying the render gate size from properties.
    // NOTE: Handled internally by ufbx, ignore unless you interpret `ufbx_props` directly!
    public enum UfbxGateFit
    {
        // Use the film/aperture size directly as the render gate
        None,       // C: UFBX_GATE_FIT_NONE

        // Fit the render gate to the height of the film, derive width from aspect ratio
        Vertical,   // C: UFBX_GATE_FIT_VERTICAL

        // Fit the render gate to the width of the film, derive height from aspect ratio
        Horizontal, // C: UFBX_GATE_FIT_HORIZONTAL

        // Fit the render gate so that it is fully contained within the film gate
        Fill,       // C: UFBX_GATE_FIT_FILL

        // Fit the render gate so that it fully contains the film gate
        Overscan,   // C: UFBX_GATE_FIT_OVERSCAN

        // Stretch the render gate to match the film gate
        Stretch,    // C: UFBX_GATE_FIT_STRETCH
    }

    // C: ufbx_aperture_format
    // Camera film/aperture size defaults.
    // NOTE: Handled internally by ufbx, ignore unless you interpret `ufbx_props` directly!
    public enum UfbxApertureFormat
    {
        Custom,             // C: UFBX_APERTURE_FORMAT_CUSTOM (use `"FilmWidth"` and `"FilmHeight"`)
        Mm16Theatrical,     // C: UFBX_APERTURE_FORMAT_16MM_THEATRICAL (0.404 x 0.295 inches)
        Super16Mm,          // C: UFBX_APERTURE_FORMAT_SUPER_16MM (0.493 x 0.292 inches)
        Mm35Academy,        // C: UFBX_APERTURE_FORMAT_35MM_ACADEMY (0.864 x 0.630 inches)
        Mm35TvProjection,   // C: UFBX_APERTURE_FORMAT_35MM_TV_PROJECTION (0.816 x 0.612 inches)
        Mm35FullAperture,   // C: UFBX_APERTURE_FORMAT_35MM_FULL_APERTURE (0.980 x 0.735 inches)
        Mm35185Projection,  // C: UFBX_APERTURE_FORMAT_35MM_185_PROJECTION (0.825 x 0.446 inches)
        Mm35Anamorphic,     // C: UFBX_APERTURE_FORMAT_35MM_ANAMORPHIC (0.864 x 0.732 inches, squeeze ratio: 2)
        Mm70Projection,     // C: UFBX_APERTURE_FORMAT_70MM_PROJECTION (2.066 x 0.906 inches)
        VistaVision,        // C: UFBX_APERTURE_FORMAT_VISTAVISION (1.485 x 0.991 inches)
        Dynavision,         // C: UFBX_APERTURE_FORMAT_DYNAVISION (2.080 x 1.480 inches)
        Imax,               // C: UFBX_APERTURE_FORMAT_IMAX (2.772 x 2.072 inches)
    }

    // C: ufbx_coordinate_axis
    public enum UfbxCoordinateAxis
    {
        PositiveX,  // C: UFBX_COORDINATE_AXIS_POSITIVE_X
        NegativeX,  // C: UFBX_COORDINATE_AXIS_NEGATIVE_X
        PositiveY,  // C: UFBX_COORDINATE_AXIS_POSITIVE_Y
        NegativeY,  // C: UFBX_COORDINATE_AXIS_NEGATIVE_Y
        PositiveZ,  // C: UFBX_COORDINATE_AXIS_POSITIVE_Z
        NegativeZ,  // C: UFBX_COORDINATE_AXIS_NEGATIVE_Z
        Unknown,    // C: UFBX_COORDINATE_AXIS_UNKNOWN
    }

    // C: ufbx_nurbs_topology
    public enum UfbxNurbsTopology
    {
        // The endpoints are not connected.
        Open,       // C: UFBX_NURBS_TOPOLOGY_OPEN

        // Repeats first `ufbx_nurbs_basis.order - 1` control points after the end.
        Periodic,   // C: UFBX_NURBS_TOPOLOGY_PERIODIC

        // Repeats the first control point after the end.
        Closed,     // C: UFBX_NURBS_TOPOLOGY_CLOSED
    }

    // C: ufbx_marker_type
    // Tracking marker for effectors
    public enum UfbxMarkerType
    {
        Unknown,    // C: UFBX_MARKER_UNKNOWN (unknown marker type)
        FkEffector, // C: UFBX_MARKER_FK_EFFECTOR (FK (Forward Kinematics) effector)
        IkEffector, // C: UFBX_MARKER_IK_EFFECTOR (IK (Inverse Kinematics) effector)
    }

    // C: ufbx_lod_display
    // LOD level display mode.
    public enum UfbxLodDisplay
    {
        UseLod, // C: UFBX_LOD_DISPLAY_USE_LOD (display the LOD level if the distance is appropriate)
        Show,   // C: UFBX_LOD_DISPLAY_SHOW (always display the LOD level)
        Hide,   // C: UFBX_LOD_DISPLAY_HIDE (never display the LOD level)
    }

    // C: ufbx_skinning_method
    // Method to evaluate the skinning on a per-vertex level
    public enum UfbxSkinningMethod
    {
        // Linear blend skinning: Blend transformation matrices by vertex weights
        Linear,             // C: UFBX_SKINNING_METHOD_LINEAR

        // One vertex should have only one bone attached
        Rigid,              // C: UFBX_SKINNING_METHOD_RIGID

        // Convert the transformations to dual quaternions and blend in that space
        DualQuaternion,     // C: UFBX_SKINNING_METHOD_DUAL_QUATERNION

        // Blend between `UFBX_SKINNING_METHOD_LINEAR` and `UFBX_SKINNING_METHOD_BLENDED_DQ_LINEAR`.
        // The blend weight can be found either per-vertex in `ufbx_skin_vertex.dq_weight`
        // or in `ufbx_skin_deformer.dq_vertices/dq_weights` (indexed by vertex).
        BlendedDqLinear,    // C: UFBX_SKINNING_METHOD_BLENDED_DQ_LINEAR
    }

    // C: ufbx_cache_file_format
    public enum UfbxCacheFileFormat
    {
        Unknown,    // C: UFBX_CACHE_FILE_FORMAT_UNKNOWN (unknown cache file format)
        Pc2,        // C: UFBX_CACHE_FILE_FORMAT_PC2 (.pc2 point cache file)
        Mc,         // C: UFBX_CACHE_FILE_FORMAT_MC (.mc/.mcx Maya cache file)
    }

    // C: ufbx_cache_data_format
    public enum UfbxCacheDataFormat
    {
        Unknown,        // C: UFBX_CACHE_DATA_FORMAT_UNKNOWN (unknown data format)
        RealFloat,      // C: UFBX_CACHE_DATA_FORMAT_REAL_FLOAT (`float data[]`)
        Vec3Float,      // C: UFBX_CACHE_DATA_FORMAT_VEC3_FLOAT (`struct { float x, y, z; } data[]`)
        RealDouble,     // C: UFBX_CACHE_DATA_FORMAT_REAL_DOUBLE (`double data[]`)
        Vec3Double,     // C: UFBX_CACHE_DATA_FORMAT_VEC3_DOUBLE (`struct { double x, y, z; } data[]`)
    }

    // C: ufbx_cache_data_encoding
    public enum UfbxCacheDataEncoding
    {
        Unknown,        // C: UFBX_CACHE_DATA_ENCODING_UNKNOWN (unknown data encoding)
        LittleEndian,   // C: UFBX_CACHE_DATA_ENCODING_LITTLE_ENDIAN (contiguous little-endian array)
        BigEndian,      // C: UFBX_CACHE_DATA_ENCODING_BIG_ENDIAN (contiguous big-endian array)
    }

    // C: ufbx_cache_interpretation
    // Known interpretations of geometry cache data.
    public enum UfbxCacheInterpretation
    {
        // Unknown interpretation, see `ufbx_cache_channel.interpretation_name` for more information.
        Unknown,            // C: UFBX_CACHE_INTERPRETATION_UNKNOWN

        // Generic "points" interpretation, FBX SDK default. Usually fine to interpret
        // as vertex positions if no other cache channels are specified.
        Points,             // C: UFBX_CACHE_INTERPRETATION_POINTS

        // Vertex positions.
        VertexPosition,     // C: UFBX_CACHE_INTERPRETATION_VERTEX_POSITION

        // Vertex normals.
        VertexNormal,       // C: UFBX_CACHE_INTERPRETATION_VERTEX_NORMAL
    }

    // C: ufbx_shader_type
    // Shading model type
    public enum UfbxShaderType
    {
        // Unknown shading model
        Unknown,                    // C: UFBX_SHADER_UNKNOWN

        // FBX builtin diffuse material
        FbxLambert,                 // C: UFBX_SHADER_FBX_LAMBERT

        // FBX builtin diffuse+specular material
        FbxPhong,                   // C: UFBX_SHADER_FBX_PHONG

        // Open Shading Language standard surface
        // https://github.com/Autodesk/standard-surface
        OslStandardSurface,         // C: UFBX_SHADER_OSL_STANDARD_SURFACE

        // Arnold standard surface
        ArnoldStandardSurface,      // C: UFBX_SHADER_ARNOLD_STANDARD_SURFACE

        // 3ds Max Physical Material
        Max3dsPhysicalMaterial,     // C: UFBX_SHADER_3DS_MAX_PHYSICAL_MATERIAL

        // 3ds Max PBR (Metal/Rough) material
        Max3dsPbrMetalRough,        // C: UFBX_SHADER_3DS_MAX_PBR_METAL_ROUGH

        // 3ds Max PBR (Spec/Gloss) material
        Max3dsPbrSpecGloss,         // C: UFBX_SHADER_3DS_MAX_PBR_SPEC_GLOSS

        // 3ds glTF Material
        GltfMaterial,               // C: UFBX_SHADER_GLTF_MATERIAL

        // 3ds OpenPBR Material
        OpenPbrMaterial,            // C: UFBX_SHADER_OPENPBR_MATERIAL

        // Stingray ShaderFX shader graph.
        // Contains a serialized `"ShaderGraph"` in `ufbx_props`.
        ShaderFxGraph,              // C: UFBX_SHADER_SHADERFX_GRAPH

        // Variation of the FBX phong shader that can recover PBR properties like
        // `metalness` or `roughness` from the FBX non-physical values.
        // NOTE: Enable `ufbx_load_opts.use_blender_pbr_material`.
        BlenderPhong,               // C: UFBX_SHADER_BLENDER_PHONG

        // Wavefront .mtl format shader (used by .obj files)
        WavefrontMtl,               // C: UFBX_SHADER_WAVEFRONT_MTL
    }

    // C: ufbx_material_fbx_map
    // FBX builtin material properties, matches maps in `ufbx_material_fbx_maps`
    public enum UfbxMaterialFbxMap
    {
        DiffuseFactor,              // C: UFBX_MATERIAL_FBX_DIFFUSE_FACTOR
        DiffuseColor,               // C: UFBX_MATERIAL_FBX_DIFFUSE_COLOR
        SpecularFactor,             // C: UFBX_MATERIAL_FBX_SPECULAR_FACTOR
        SpecularColor,              // C: UFBX_MATERIAL_FBX_SPECULAR_COLOR
        SpecularExponent,           // C: UFBX_MATERIAL_FBX_SPECULAR_EXPONENT
        ReflectionFactor,           // C: UFBX_MATERIAL_FBX_REFLECTION_FACTOR
        ReflectionColor,            // C: UFBX_MATERIAL_FBX_REFLECTION_COLOR
        TransparencyFactor,         // C: UFBX_MATERIAL_FBX_TRANSPARENCY_FACTOR
        TransparencyColor,          // C: UFBX_MATERIAL_FBX_TRANSPARENCY_COLOR
        EmissionFactor,             // C: UFBX_MATERIAL_FBX_EMISSION_FACTOR
        EmissionColor,              // C: UFBX_MATERIAL_FBX_EMISSION_COLOR
        AmbientFactor,              // C: UFBX_MATERIAL_FBX_AMBIENT_FACTOR
        AmbientColor,               // C: UFBX_MATERIAL_FBX_AMBIENT_COLOR
        NormalMap,                  // C: UFBX_MATERIAL_FBX_NORMAL_MAP
        Bump,                       // C: UFBX_MATERIAL_FBX_BUMP
        BumpFactor,                 // C: UFBX_MATERIAL_FBX_BUMP_FACTOR
        DisplacementFactor,         // C: UFBX_MATERIAL_FBX_DISPLACEMENT_FACTOR
        Displacement,               // C: UFBX_MATERIAL_FBX_DISPLACEMENT
        VectorDisplacementFactor,   // C: UFBX_MATERIAL_FBX_VECTOR_DISPLACEMENT_FACTOR
        VectorDisplacement,         // C: UFBX_MATERIAL_FBX_VECTOR_DISPLACEMENT
    }

    // C: ufbx_material_pbr_map
    // Known PBR material properties, matches maps in `ufbx_material_pbr_maps`
    public enum UfbxMaterialPbrMap
    {
        BaseFactor,                     // C: UFBX_MATERIAL_PBR_BASE_FACTOR
        BaseColor,                      // C: UFBX_MATERIAL_PBR_BASE_COLOR
        Roughness,                      // C: UFBX_MATERIAL_PBR_ROUGHNESS
        Metalness,                      // C: UFBX_MATERIAL_PBR_METALNESS
        DiffuseRoughness,               // C: UFBX_MATERIAL_PBR_DIFFUSE_ROUGHNESS
        SpecularFactor,                 // C: UFBX_MATERIAL_PBR_SPECULAR_FACTOR
        SpecularColor,                  // C: UFBX_MATERIAL_PBR_SPECULAR_COLOR
        SpecularIor,                    // C: UFBX_MATERIAL_PBR_SPECULAR_IOR
        SpecularAnisotropy,             // C: UFBX_MATERIAL_PBR_SPECULAR_ANISOTROPY
        SpecularRotation,               // C: UFBX_MATERIAL_PBR_SPECULAR_ROTATION
        TransmissionFactor,             // C: UFBX_MATERIAL_PBR_TRANSMISSION_FACTOR
        TransmissionColor,              // C: UFBX_MATERIAL_PBR_TRANSMISSION_COLOR
        TransmissionDepth,              // C: UFBX_MATERIAL_PBR_TRANSMISSION_DEPTH
        TransmissionScatter,            // C: UFBX_MATERIAL_PBR_TRANSMISSION_SCATTER
        TransmissionScatterAnisotropy,  // C: UFBX_MATERIAL_PBR_TRANSMISSION_SCATTER_ANISOTROPY
        TransmissionDispersion,         // C: UFBX_MATERIAL_PBR_TRANSMISSION_DISPERSION
        TransmissionRoughness,          // C: UFBX_MATERIAL_PBR_TRANSMISSION_ROUGHNESS
        TransmissionExtraRoughness,     // C: UFBX_MATERIAL_PBR_TRANSMISSION_EXTRA_ROUGHNESS
        TransmissionPriority,           // C: UFBX_MATERIAL_PBR_TRANSMISSION_PRIORITY
        TransmissionEnableInAov,        // C: UFBX_MATERIAL_PBR_TRANSMISSION_ENABLE_IN_AOV
        SubsurfaceFactor,               // C: UFBX_MATERIAL_PBR_SUBSURFACE_FACTOR
        SubsurfaceColor,                // C: UFBX_MATERIAL_PBR_SUBSURFACE_COLOR
        SubsurfaceRadius,               // C: UFBX_MATERIAL_PBR_SUBSURFACE_RADIUS
        SubsurfaceScale,                // C: UFBX_MATERIAL_PBR_SUBSURFACE_SCALE
        SubsurfaceAnisotropy,           // C: UFBX_MATERIAL_PBR_SUBSURFACE_ANISOTROPY
        SubsurfaceTintColor,            // C: UFBX_MATERIAL_PBR_SUBSURFACE_TINT_COLOR
        SubsurfaceType,                 // C: UFBX_MATERIAL_PBR_SUBSURFACE_TYPE
        SheenFactor,                    // C: UFBX_MATERIAL_PBR_SHEEN_FACTOR
        SheenColor,                     // C: UFBX_MATERIAL_PBR_SHEEN_COLOR
        SheenRoughness,                 // C: UFBX_MATERIAL_PBR_SHEEN_ROUGHNESS
        CoatFactor,                     // C: UFBX_MATERIAL_PBR_COAT_FACTOR
        CoatColor,                      // C: UFBX_MATERIAL_PBR_COAT_COLOR
        CoatRoughness,                  // C: UFBX_MATERIAL_PBR_COAT_ROUGHNESS
        CoatIor,                        // C: UFBX_MATERIAL_PBR_COAT_IOR
        CoatAnisotropy,                 // C: UFBX_MATERIAL_PBR_COAT_ANISOTROPY
        CoatRotation,                   // C: UFBX_MATERIAL_PBR_COAT_ROTATION
        CoatNormal,                     // C: UFBX_MATERIAL_PBR_COAT_NORMAL
        CoatAffectBaseColor,            // C: UFBX_MATERIAL_PBR_COAT_AFFECT_BASE_COLOR
        CoatAffectBaseRoughness,        // C: UFBX_MATERIAL_PBR_COAT_AFFECT_BASE_ROUGHNESS
        ThinFilmFactor,                 // C: UFBX_MATERIAL_PBR_THIN_FILM_FACTOR
        ThinFilmThickness,              // C: UFBX_MATERIAL_PBR_THIN_FILM_THICKNESS
        ThinFilmIor,                    // C: UFBX_MATERIAL_PBR_THIN_FILM_IOR
        EmissionFactor,                 // C: UFBX_MATERIAL_PBR_EMISSION_FACTOR
        EmissionColor,                  // C: UFBX_MATERIAL_PBR_EMISSION_COLOR
        Opacity,                        // C: UFBX_MATERIAL_PBR_OPACITY
        IndirectDiffuse,                // C: UFBX_MATERIAL_PBR_INDIRECT_DIFFUSE
        IndirectSpecular,               // C: UFBX_MATERIAL_PBR_INDIRECT_SPECULAR
        NormalMap,                      // C: UFBX_MATERIAL_PBR_NORMAL_MAP
        TangentMap,                     // C: UFBX_MATERIAL_PBR_TANGENT_MAP
        DisplacementMap,                // C: UFBX_MATERIAL_PBR_DISPLACEMENT_MAP
        MatteFactor,                    // C: UFBX_MATERIAL_PBR_MATTE_FACTOR
        MatteColor,                     // C: UFBX_MATERIAL_PBR_MATTE_COLOR
        AmbientOcclusion,               // C: UFBX_MATERIAL_PBR_AMBIENT_OCCLUSION
        Glossiness,                     // C: UFBX_MATERIAL_PBR_GLOSSINESS
        CoatGlossiness,                 // C: UFBX_MATERIAL_PBR_COAT_GLOSSINESS
        TransmissionGlossiness,         // C: UFBX_MATERIAL_PBR_TRANSMISSION_GLOSSINESS
    }

    // C: ufbx_material_feature
    // Known material features
    public enum UfbxMaterialFeature
    {
        Pbr,                            // C: UFBX_MATERIAL_FEATURE_PBR
        Metalness,                      // C: UFBX_MATERIAL_FEATURE_METALNESS
        Diffuse,                        // C: UFBX_MATERIAL_FEATURE_DIFFUSE
        Specular,                       // C: UFBX_MATERIAL_FEATURE_SPECULAR
        Emission,                       // C: UFBX_MATERIAL_FEATURE_EMISSION
        Transmission,                   // C: UFBX_MATERIAL_FEATURE_TRANSMISSION
        Coat,                           // C: UFBX_MATERIAL_FEATURE_COAT
        Sheen,                          // C: UFBX_MATERIAL_FEATURE_SHEEN
        Opacity,                        // C: UFBX_MATERIAL_FEATURE_OPACITY
        AmbientOcclusion,               // C: UFBX_MATERIAL_FEATURE_AMBIENT_OCCLUSION
        Matte,                          // C: UFBX_MATERIAL_FEATURE_MATTE
        Unlit,                          // C: UFBX_MATERIAL_FEATURE_UNLIT
        Ior,                            // C: UFBX_MATERIAL_FEATURE_IOR
        DiffuseRoughness,               // C: UFBX_MATERIAL_FEATURE_DIFFUSE_ROUGHNESS
        TransmissionRoughness,          // C: UFBX_MATERIAL_FEATURE_TRANSMISSION_ROUGHNESS
        ThinWalled,                     // C: UFBX_MATERIAL_FEATURE_THIN_WALLED
        Caustics,                       // C: UFBX_MATERIAL_FEATURE_CAUSTICS
        ExitToBackground,               // C: UFBX_MATERIAL_FEATURE_EXIT_TO_BACKGROUND
        InternalReflections,            // C: UFBX_MATERIAL_FEATURE_INTERNAL_REFLECTIONS
        DoubleSided,                    // C: UFBX_MATERIAL_FEATURE_DOUBLE_SIDED
        RoughnessAsGlossiness,          // C: UFBX_MATERIAL_FEATURE_ROUGHNESS_AS_GLOSSINESS
        CoatRoughnessAsGlossiness,      // C: UFBX_MATERIAL_FEATURE_COAT_ROUGHNESS_AS_GLOSSINESS
        TransmissionRoughnessAsGlossiness, // C: UFBX_MATERIAL_FEATURE_TRANSMISSION_ROUGHNESS_AS_GLOSSINESS
    }

    // C: ufbx_texture_type
    public enum UfbxTextureType
    {
        // Texture associated with an image file/sequence. `texture->filename` and
        // `texture->relative_filename` contain the texture's path. If the file
        // has embedded content `texture->content` may hold `texture->content_size`
        // bytes of raw image data.
        File,           // C: UFBX_TEXTURE_FILE

        // The texture consists of multiple texture layers blended together.
        Layered,        // C: UFBX_TEXTURE_LAYERED

        // Reserved as these _should_ exist in FBX files.
        Procedural,     // C: UFBX_TEXTURE_PROCEDURAL

        // Node in a shader graph.
        // Use `ufbx_texture.shader` for more information.
        Shader,         // C: UFBX_TEXTURE_SHADER
    }

    // C: ufbx_blend_mode
    // Blend modes to combine layered textures with, compatible with common blend
    // mode definitions in many art programs. Simpler blend modes have equations
    // specified below where `src` is the layer to composite over `dst`.
    // See eg. https://www.w3.org/TR/2013/WD-compositing-1-20131010/#blendingseparable
    public enum UfbxBlendMode
    {
        Translucent,    // C: UFBX_BLEND_TRANSLUCENT (`src` effects result alpha)
        Additive,       // C: UFBX_BLEND_ADDITIVE (`src + dst`)
        Multiply,       // C: UFBX_BLEND_MULTIPLY (`src * dst`)
        Multiply2x,     // C: UFBX_BLEND_MULTIPLY_2X (`2 * src * dst`)
        Over,           // C: UFBX_BLEND_OVER (`src * src_alpha + dst * (1-src_alpha)`)
        Replace,        // C: UFBX_BLEND_REPLACE (`src` replace the contents)
        Dissolve,       // C: UFBX_BLEND_DISSOLVE (`random() + src_alpha >= 1.0 ? src : dst`)
        Darken,         // C: UFBX_BLEND_DARKEN (`min(src, dst)`)
        ColorBurn,      // C: UFBX_BLEND_COLOR_BURN (`src > 0 ? 1 - min(1, (1-dst) / src) : 0`)
        LinearBurn,     // C: UFBX_BLEND_LINEAR_BURN (`src + dst - 1`)
        DarkerColor,    // C: UFBX_BLEND_DARKER_COLOR (`value(src) < value(dst) ? src : dst`)
        Lighten,        // C: UFBX_BLEND_LIGHTEN (`max(src, dst)`)
        Screen,         // C: UFBX_BLEND_SCREEN (`1 - (1-src)*(1-dst)`)
        ColorDodge,     // C: UFBX_BLEND_COLOR_DODGE (`src < 1 ? dst / (1 - src)` : (dst>0?1:0)`)
        LinearDodge,    // C: UFBX_BLEND_LINEAR_DODGE (`src + dst`)
        LighterColor,   // C: UFBX_BLEND_LIGHTER_COLOR (`value(src) > value(dst) ? src : dst`)
        SoftLight,      // C: UFBX_BLEND_SOFT_LIGHT (https://www.w3.org/TR/2013/WD-compositing-1-20131010/#blendingsoftlight)
        HardLight,      // C: UFBX_BLEND_HARD_LIGHT (https://www.w3.org/TR/2013/WD-compositing-1-20131010/#blendinghardlight)
        VividLight,     // C: UFBX_BLEND_VIVID_LIGHT (combination of `COLOR_DODGE` and `COLOR_BURN`)
        LinearLight,    // C: UFBX_BLEND_LINEAR_LIGHT (combination of `LINEAR_DODGE` and `LINEAR_BURN`)
        PinLight,       // C: UFBX_BLEND_PIN_LIGHT (combination of `DARKEN` and `LIGHTEN`)
        HardMix,        // C: UFBX_BLEND_HARD_MIX (produces primary colors depending on similarity)
        Difference,     // C: UFBX_BLEND_DIFFERENCE (`abs(src - dst)`)
        Exclusion,      // C: UFBX_BLEND_EXCLUSION (`dst + src - 2 * src * dst`)
        Subtract,       // C: UFBX_BLEND_SUBTRACT (`dst - src`)
        Divide,         // C: UFBX_BLEND_DIVIDE (`dst / src`)
        Hue,            // C: UFBX_BLEND_HUE (replace hue)
        Saturation,     // C: UFBX_BLEND_SATURATION (replace saturation)
        Color,          // C: UFBX_BLEND_COLOR (replace hue and saturatio)
        Luminosity,     // C: UFBX_BLEND_LUMINOSITY (replace value)
        Overlay,        // C: UFBX_BLEND_OVERLAY (same as `HARD_LIGHT` but with `src` and `dst` swapped)
    }

    // C: ufbx_wrap_mode
    // Blend modes to combine layered textures with, compatible with common blend
    public enum UfbxWrapMode
    {
        Repeat, // C: UFBX_WRAP_REPEAT (repeat the texture past the [0,1] range)
        Clamp,  // C: UFBX_WRAP_CLAMP (clamp the normalized texture coordinates to [0,1])
    }

    // C: ufbx_shader_texture_type
    public enum UfbxShaderTextureType
    {
        Unknown,        // C: UFBX_SHADER_TEXTURE_UNKNOWN

        // Select an output of a multi-output shader.
        // HINT: If this type is used the `ufbx_shader_texture.main_texture` and
        // `ufbx_shader_texture.main_texture_output_index` fields are set.
        SelectOutput,   // C: UFBX_SHADER_TEXTURE_SELECT_OUTPUT

        // Open Shading Language (OSL) shader.
        // https://github.com/AcademySoftwareFoundation/OpenShadingLanguage
        Osl,            // C: UFBX_SHADER_TEXTURE_OSL
    }

    // C: ufbx_interpolation
    // Animation curve segment interpolation mode between two keyframes
    public enum UfbxInterpolation
    {
        ConstantPrev,   // C: UFBX_INTERPOLATION_CONSTANT_PREV (hold previous key value)
        ConstantNext,   // C: UFBX_INTERPOLATION_CONSTANT_NEXT (hold next key value)
        Linear,         // C: UFBX_INTERPOLATION_LINEAR (linear interpolation between two keys)
        Cubic,          // C: UFBX_INTERPOLATION_CUBIC (cubic interpolation, see `ufbx_tangent`)
    }

    // C: ufbx_extrapolation_mode
    public enum UfbxExtrapolationMode
    {
        Constant,       // C: UFBX_EXTRAPOLATION_CONSTANT (use the value of the first/last keyframe)
        Repeat,         // C: UFBX_EXTRAPOLATION_REPEAT (repeat the whole animation curve)
        Mirror,         // C: UFBX_EXTRAPOLATION_MIRROR (repeat with mirroring)
        Slope,          // C: UFBX_EXTRAPOLATION_SLOPE (use the tangent of the last keyframe to linearly extrapolate)
        RepeatRelative, // C: UFBX_EXTRAPOLATION_REPEAT_RELATIVE (repeat the animation curve but connect the first and last keyframe values)
    }

    // C: ufbx_constraint_type
    // Type of property constrain eg. position or look-at
    public enum UfbxConstraintType
    {
        Unknown,        // C: UFBX_CONSTRAINT_UNKNOWN
        Aim,            // C: UFBX_CONSTRAINT_AIM
        Parent,         // C: UFBX_CONSTRAINT_PARENT
        Position,       // C: UFBX_CONSTRAINT_POSITION
        Rotation,       // C: UFBX_CONSTRAINT_ROTATION
        Scale,          // C: UFBX_CONSTRAINT_SCALE

        // Inverse kinematic chain to a single effector `ufbx_constraint.ik_effector`.
        // `targets` optionally contains a list of pole targets!
        SingleChainIk,  // C: UFBX_CONSTRAINT_SINGLE_CHAIN_IK
    }

    // C: ufbx_constraint_aim_up_type
    // Method to determine the up vector in aim constraints
    public enum UfbxConstraintAimUpType
    {
        Scene,      // C: UFBX_CONSTRAINT_AIM_UP_SCENE (align the up vector to the scene global up vector)
        ToNode,     // C: UFBX_CONSTRAINT_AIM_UP_TO_NODE (aim the up vector at `ufbx_constraint.aim_up_node`)
        AlignNode,  // C: UFBX_CONSTRAINT_AIM_UP_ALIGN_NODE (copy the up vector from `ufbx_constraint.aim_up_node`)
        Vector,     // C: UFBX_CONSTRAINT_AIM_UP_VECTOR (use `ufbx_constraint.aim_up_vector` as the up vector)
        None,       // C: UFBX_CONSTRAINT_AIM_UP_NONE (don't align the up vector to anything)
    }

    // C: ufbx_constraint_ik_pole_type
    // Method to determine the up vector in aim constraints
    public enum UfbxConstraintIkPoleType
    {
        Vector, // C: UFBX_CONSTRAINT_IK_POLE_VECTOR (use towards calculated from `ufbx_constraint.targets`)
        Node,   // C: UFBX_CONSTRAINT_IK_POLE_NODE (use `ufbx_constraint.ik_pole_vector` directly)
    }

    // C: ufbx_exporter
    public enum UfbxExporter
    {
        Unknown,        // C: UFBX_EXPORTER_UNKNOWN
        FbxSdk,         // C: UFBX_EXPORTER_FBX_SDK
        BlenderBinary,  // C: UFBX_EXPORTER_BLENDER_BINARY
        BlenderAscii,   // C: UFBX_EXPORTER_BLENDER_ASCII
        MotionBuilder,  // C: UFBX_EXPORTER_MOTION_BUILDER
        UfbxWrite,      // C: UFBX_EXPORTER_UFBX_WRITE
    }

    // C: ufbx_file_format
    public enum UfbxFileFormat
    {
        Unknown,    // C: UFBX_FILE_FORMAT_UNKNOWN (unknown file format)
        Fbx,        // C: UFBX_FILE_FORMAT_FBX (.fbx Kaydara/Autodesk FBX file)
        Obj,        // C: UFBX_FILE_FORMAT_OBJ (.obj Wavefront OBJ file)
        Mtl,        // C: UFBX_FILE_FORMAT_MTL (.mtl Wavefront MTL (Material template library) file)
    }

    // C: ufbx_warning_type
    // Warning about a non-fatal issue in the file (see `ufbx_warning`).
    public enum UfbxWarningType
    {
        // Missing external file file (for example .mtl for Wavefront .obj file or a
        // geometry cache)
        MissingExternalFile,        // C: UFBX_WARNING_MISSING_EXTERNAL_FILE

        // Loaded a Wavefront .mtl file derived from the filename instead of a proper
        // `mtllib` statement.
        ImplicitMtl,                // C: UFBX_WARNING_IMPLICIT_MTL

        // Truncated array has been auto-expanded.
        TruncatedArray,             // C: UFBX_WARNING_TRUNCATED_ARRAY

        // Geometry data has been defined but has no data.
        MissingGeometryData,        // C: UFBX_WARNING_MISSING_GEOMETRY_DATA

        // Duplicated connection between two elements that shouldn't have.
        DuplicateConnection,        // C: UFBX_WARNING_DUPLICATE_CONNECTION

        // Vertex 'W' attribute length differs from main attribute.
        BadVertexWAttribute,        // C: UFBX_WARNING_BAD_VERTEX_W_ATTRIBUTE

        // Missing polygon mapping type.
        MissingPolygonMapping,      // C: UFBX_WARNING_MISSING_POLYGON_MAPPING

        // Unsupported version, loaded but may be incorrect.
        // If the loading fails `UFBX_ERROR_UNSUPPORTED_VERSION` is issued instead.
        UnsupportedVersion,         // C: UFBX_WARNING_UNSUPPORTED_VERSION

        // Out-of-bounds index has been clamped to be in-bounds.
        // HINT: You can use `ufbx_index_error_handling` to adjust behavior.
        IndexClamped,               // C: UFBX_WARNING_INDEX_CLAMPED

        // Non-UTF8 encoded strings.
        // HINT: You can use `ufbx_unicode_error_handling` to adjust behavior.
        BadUnicode,                 // C: UFBX_WARNING_BAD_UNICODE

        // Invalid base64-encoded embedded content ignored.
        BadBase64Content,           // C: UFBX_WARNING_BAD_BASE64_CONTENT

        // Non-node element connected to root.
        BadElementConnectedToRoot,  // C: UFBX_WARNING_BAD_ELEMENT_CONNECTED_TO_ROOT

        // Duplicated object ID in the file, connections will be wrong.
        DuplicateObjectId,          // C: UFBX_WARNING_DUPLICATE_OBJECT_ID

        // Empty face has been removed.
        // Use `ufbx_load_opts.allow_empty_faces` if you want to allow them.
        EmptyFaceRemoved,           // C: UFBX_WARNING_EMPTY_FACE_REMOVED

        // Unknown .obj file directive.
        UnknownObjDirective,        // C: UFBX_WARNING_UNKNOWN_OBJ_DIRECTIVE

        // C: UFBX_WARNING_TYPE_FIRST_DEDUPLICATED = UFBX_WARNING_INDEX_CLAMPED (alias).
        // Warnings from this one onwards are deduplicated, see `ufbx_warning.count`.
        FirstDeduplicated = 8,
    }

    // C: ufbx_thumbnail_format
    public enum UfbxThumbnailFormat
    {
        Unknown,    // C: UFBX_THUMBNAIL_FORMAT_UNKNOWN (unknown format)
        Rgb24,      // C: UFBX_THUMBNAIL_FORMAT_RGB_24 (8-bit RGB pixels, in memory R,G,B)
        Rgba32,     // C: UFBX_THUMBNAIL_FORMAT_RGBA_32 (8-bit RGBA pixels, in memory R,G,B,A)
    }

    // C: ufbx_space_conversion
    // Specify how unit / coordinate system conversion should be performed.
    // Affects how `ufbx_load_opts.target_axes` and `ufbx_load_opts.target_unit_meters` work,
    // has no effect if neither is specified.
    public enum UfbxSpaceConversion
    {
        // Store the space conversion transform in the root node.
        // Sets `ufbx_node.local_transform` of the root node.
        TransformRoot,      // C: UFBX_SPACE_CONVERSION_TRANSFORM_ROOT

        // Perform the conversion by using "adjust" transforms.
        // Compensates for the transforms using `ufbx_node.adjust_pre_rotation` and
        // `ufbx_node.adjust_pre_scale`. You don't need to account for these unless
        // you are manually building transforms from `ufbx_props`.
        AdjustTransforms,   // C: UFBX_SPACE_CONVERSION_ADJUST_TRANSFORMS

        // Perform the conversion by scaling geometry in addition to adjusting transforms.
        // Compensates transforms like `UFBX_SPACE_CONVERSION_ADJUST_TRANSFORMS` but
        // applies scaling to geometry as well.
        ModifyGeometry,     // C: UFBX_SPACE_CONVERSION_MODIFY_GEOMETRY
    }

    // C: ufbx_geometry_transform_handling
    // How to handle FBX node geometry transforms.
    // FBX nodes can have "geometry transforms" that affect only the attached meshes,
    // but not the children. This is not allowed in many scene representations so
    // ufbx provides some ways to simplify them.
    // Geometry transforms can also be used to transform any other attributes such
    // as lights or cameras.
    public enum UfbxGeometryTransformHandling
    {
        // Preserve the geometry transforms as-is.
        // To be correct for all files you have to use `ufbx_node.geometry_transform`,
        // `ufbx_node.geometry_to_node`, or `ufbx_node.geometry_to_world` to compensate
        // for any potential geometry transforms.
        Preserve,                   // C: UFBX_GEOMETRY_TRANSFORM_HANDLING_PRESERVE

        // Add helper nodes between the nodes and geometry where needed.
        // The created nodes have `ufbx_node.is_geometry_transform_helper` set and are
        // named `ufbx_load_opts.geometry_transform_helper_name`.
        HelperNodes,                // C: UFBX_GEOMETRY_TRANSFORM_HANDLING_HELPER_NODES

        // Modify the geometry of meshes attached to nodes with geometry transforms.
        // Will add helper nodes like `UFBX_GEOMETRY_TRANSFORM_HANDLING_HELPER_NODES` if
        // necessary, for example if there are multiple instances of the same mesh with
        // geometry transforms.
        ModifyGeometry,             // C: UFBX_GEOMETRY_TRANSFORM_HANDLING_MODIFY_GEOMETRY

        // Modify the geometry of meshes attached to nodes with geometry transforms.
        // NOTE: This will not work correctly for instanced geometry.
        ModifyGeometryNoFallback,   // C: UFBX_GEOMETRY_TRANSFORM_HANDLING_MODIFY_GEOMETRY_NO_FALLBACK
    }

    // C: ufbx_inherit_mode_handling
    // How to handle FBX transform inherit modes.
    public enum UfbxInheritModeHandling
    {
        // Preserve inherit mode in `ufbx_node.inherit_mode`.
        // NOTE: To correctly handle all scenes you would need to handle the
        // non-standard inherit modes.
        Preserve,               // C: UFBX_INHERIT_MODE_HANDLING_PRESERVE

        // Create scale helper nodes parented to nodes that need special inheritance.
        // Scale helper nodes will have `ufbx_node.is_scale_helper` and parents of
        // scale helpers will have `ufbx_node.scale_helper` pointing to it.
        HelperNodes,            // C: UFBX_INHERIT_MODE_HANDLING_HELPER_NODES

        // Attempt to compensate for bone scale by inversely scaling children.
        // NOTE: This only works for uniform non-animated scaling, if scale is
        // non-uniform or animated, ufbx will add scale helpers in the same way
        // as `UFBX_INHERIT_MODE_HANDLING_HELPER_NODES`.
        Compensate,             // C: UFBX_INHERIT_MODE_HANDLING_COMPENSATE

        // Attempt to compensate for bone scale by inversely scaling children.
        // Will never create helper nodes.
        CompensateNoFallback,   // C: UFBX_INHERIT_MODE_HANDLING_COMPENSATE_NO_FALLBACK

        // Ignore non-standard inheritance modes.
        // Forces all nodes to have `UFBX_INHERIT_MODE_NORMAL` regardless of the
        // inherit mode specified in the file. This can be useful for emulating
        // results from importers/programs that don't support inherit modes.
        Ignore,                 // C: UFBX_INHERIT_MODE_HANDLING_IGNORE
    }

    // C: ufbx_pivot_handling
    // How to handle FBX transform pivots.
    public enum UfbxPivotHandling
    {
        // Take pivots into account when computing the transform.
        Retain,                 // C: UFBX_PIVOT_HANDLING_RETAIN

        // Translate objects to be located at their pivot.
        // NOTE: Only applied if rotation and scaling pivots are equal.
        // NOTE: Results in geometric translation. Use `ufbx_geometry_transform_handling`
        // to interpret these in a standard scene graph.
        AdjustToPivot,          // C: UFBX_PIVOT_HANDLING_ADJUST_TO_PIVOT

        // Translate objects to be located at their rotation pivot.
        // NOTE: Results in geometric translation. Use `ufbx_geometry_transform_handling`
        // to interpret these in a standard scene graph.
        // NOTE: By default the original transforms of empties are not retained when using this,
        // use `ufbx_load_opts.pivot_handling_retain_empties` to prevent adjusting these pivots.
        AdjustToRotationPivot,  // C: UFBX_PIVOT_HANDLING_ADJUST_TO_ROTATION_PIVOT
    }

    // C: ufbx_time_mode
    public enum UfbxTimeMode
    {
        Default,        // C: UFBX_TIME_MODE_DEFAULT
        Fps120,         // C: UFBX_TIME_MODE_120_FPS
        Fps100,         // C: UFBX_TIME_MODE_100_FPS
        Fps60,          // C: UFBX_TIME_MODE_60_FPS
        Fps50,          // C: UFBX_TIME_MODE_50_FPS
        Fps48,          // C: UFBX_TIME_MODE_48_FPS
        Fps30,          // C: UFBX_TIME_MODE_30_FPS
        Fps30Drop,      // C: UFBX_TIME_MODE_30_FPS_DROP
        NtscDropFrame,  // C: UFBX_TIME_MODE_NTSC_DROP_FRAME
        NtscFullFrame,  // C: UFBX_TIME_MODE_NTSC_FULL_FRAME
        Pal,            // C: UFBX_TIME_MODE_PAL
        Fps24,          // C: UFBX_TIME_MODE_24_FPS
        Fps1000,        // C: UFBX_TIME_MODE_1000_FPS
        FilmFullFrame,  // C: UFBX_TIME_MODE_FILM_FULL_FRAME
        Custom,         // C: UFBX_TIME_MODE_CUSTOM
        Fps96,          // C: UFBX_TIME_MODE_96_FPS
        Fps72,          // C: UFBX_TIME_MODE_72_FPS
        Fps59_94,       // C: UFBX_TIME_MODE_59_94_FPS
    }

    // C: ufbx_time_protocol
    public enum UfbxTimeProtocol
    {
        Smpte,      // C: UFBX_TIME_PROTOCOL_SMPTE
        FrameCount, // C: UFBX_TIME_PROTOCOL_FRAME_COUNT
        Default,    // C: UFBX_TIME_PROTOCOL_DEFAULT
    }

    // C: ufbx_snap_mode
    public enum UfbxSnapMode
    {
        None,       // C: UFBX_SNAP_MODE_NONE
        Snap,       // C: UFBX_SNAP_MODE_SNAP
        Play,       // C: UFBX_SNAP_MODE_PLAY
        SnapAndPlay,// C: UFBX_SNAP_MODE_SNAP_AND_PLAY
    }

    // C: ufbx_topo_flags
    public enum UfbxTopoFlags
    {
        NonManifold = 0x1,  // C: UFBX_TOPO_NON_MANIFOLD (edge with three or more faces)
    }

    // C: ufbx_open_file_type
    public enum UfbxOpenFileType
    {
        MainModel,      // C: UFBX_OPEN_FILE_MAIN_MODEL (main model file)
        GeometryCache,  // C: UFBX_OPEN_FILE_GEOMETRY_CACHE (unknown geometry cache file)
        ObjMtl,         // C: UFBX_OPEN_FILE_OBJ_MTL (.mtl material library file)
    }

    // C: ufbx_error_type
    // Error causes (and `UFBX_ERROR_NONE` for no error).
    public enum UfbxErrorType
    {
        // No error, operation has been performed successfully.
        None,                   // C: UFBX_ERROR_NONE

        // Unspecified error, most likely caused by an invalid FBX file or a file
        // that contains something ufbx can't handle.
        Unknown,                // C: UFBX_ERROR_UNKNOWN

        // File not found.
        FileNotFound,           // C: UFBX_ERROR_FILE_NOT_FOUND

        // Empty file.
        EmptyFile,              // C: UFBX_ERROR_EMPTY_FILE

        // External file not found.
        // See `ufbx_load_opts.load_external_files` for more information.
        ExternalFileNotFound,   // C: UFBX_ERROR_EXTERNAL_FILE_NOT_FOUND

        // Out of memory (allocator returned `NULL`).
        OutOfMemory,            // C: UFBX_ERROR_OUT_OF_MEMORY

        // `ufbx_allocator_opts.memory_limit` exhausted.
        MemoryLimit,            // C: UFBX_ERROR_MEMORY_LIMIT

        // `ufbx_allocator_opts.allocation_limit` exhausted.
        AllocationLimit,        // C: UFBX_ERROR_ALLOCATION_LIMIT

        // File ended abruptly.
        TruncatedFile,          // C: UFBX_ERROR_TRUNCATED_FILE

        // IO read error.
        // eg. returning `SIZE_MAX` from `ufbx_stream.read_fn` or stdio `ferror()` condition.
        Io,                     // UFBX_ERROR_IO

        // User cancelled the loading via `ufbx_load_opts.progress_cb` returning `UFBX_PROGRESS_CANCEL`.
        Cancelled,              // C: UFBX_ERROR_CANCELLED

        // Could not detect file format from file data or filename.
        UnrecognizedFileFormat, // C: UFBX_ERROR_UNRECOGNIZED_FILE_FORMAT

        // Options struct (eg. `ufbx_load_opts`) is not cleared to zero.
        UninitializedOptions,   // C: UFBX_ERROR_UNINITIALIZED_OPTIONS

        // The vertex streams in `ufbx_generate_indices()` are empty.
        ZeroVertexSize,         // C: UFBX_ERROR_ZERO_VERTEX_SIZE

        // Vertex stream passed to `ufbx_generate_indices()`.
        TruncatedVertexStream,  // C: UFBX_ERROR_TRUNCATED_VERTEX_STREAM

        // Invalid UTF-8 encountered in a file when loading with `UFBX_UNICODE_ERROR_HANDLING_ABORT_LOADING`.
        InvalidUtf8,            // C: UFBX_ERROR_INVALID_UTF8

        // Feature needed for the operation has been compiled out.
        FeatureDisabled,        // C: UFBX_ERROR_FEATURE_DISABLED

        // Attempting to tessellate an invalid NURBS object.
        // See `ufbx_nurbs_basis.valid`.
        BadNurbs,               // C: UFBX_ERROR_BAD_NURBS

        // Out of bounds index in the file when loading with `UFBX_INDEX_ERROR_HANDLING_ABORT_LOADING`.
        BadIndex,               // C: UFBX_ERROR_BAD_INDEX

        // Node is deeper than `ufbx_load_opts.node_depth_limit` in the hierarchy.
        NodeDepthLimit,         // C: UFBX_ERROR_NODE_DEPTH_LIMIT

        // Error parsing ASCII array in a thread.
        ThreadedAsciiParse,     // C: UFBX_ERROR_THREADED_ASCII_PARSE

        // Unsafe options specified without enabling `ufbx_load_opts.allow_unsafe`.
        UnsafeOptions,          // C: UFBX_ERROR_UNSAFE_OPTIONS

        // Duplicated override property in `ufbx_create_anim()`
        DuplicateOverride,      // C: UFBX_ERROR_DUPLICATE_OVERRIDE

        // Unsupported file format version.
        // ufbx still tries to load files with unsupported versions, see `UFBX_WARNING_UNSUPPORTED_VERSION`.
        UnsupportedVersion,     // C: UFBX_ERROR_UNSUPPORTED_VERSION
    }

    // C: ufbx_progress_result
    // Progress result returned from `ufbx_progress_fn()` callback.
    // Determines whether ufbx should continue or abort the loading.
    public enum UfbxProgressResult
    {
        // Continue loading the file.
        Continue = 0x100,   // C: UFBX_PROGRESS_CONTINUE

        // Cancel loading and fail with `UFBX_ERROR_CANCELLED`.
        Cancel = 0x200,     // C: UFBX_PROGRESS_CANCEL
    }

    // C: ufbx_index_error_handling
    public enum UfbxIndexErrorHandling
    {
        // Clamp to a valid value.
        Clamp,          // C: UFBX_INDEX_ERROR_HANDLING_CLAMP

        // Set bad indices to `UFBX_NO_INDEX`.
        // This is the recommended way if you need to deal with files with gaps in information.
        // HINT: If you use this `ufbx_get_vertex_TYPE()` functions will return zero
        // on invalid indices instead of failing.
        NoIndex,        // C: UFBX_INDEX_ERROR_HANDLING_NO_INDEX

        // Fail loading entierely when encountering a bad index.
        AbortLoading,   // C: UFBX_INDEX_ERROR_HANDLING_ABORT_LOADING

        // Pass bad indices through as-is.
        // Requires `ufbx_load_opts.allow_unsafe`.
        // UNSAFE: Breaks any API guarantees regarding indexes being in bounds and makes
        // `ufbx_get_vertex_TYPE()` memory-unsafe to use.
        UnsafeIgnore,   // C: UFBX_INDEX_ERROR_HANDLING_UNSAFE_IGNORE
    }

    // C: ufbx_unicode_error_handling
    public enum UfbxUnicodeErrorHandling
    {
        // Replace errors with U+FFFD "Replacement Character"
        ReplacementCharacter,   // C: UFBX_UNICODE_ERROR_HANDLING_REPLACEMENT_CHARACTER

        // Replace errors with '_' U+5F "Low Line"
        Underscore,             // C: UFBX_UNICODE_ERROR_HANDLING_UNDERSCORE

        // Replace errors with '?' U+3F "Question Mark"
        QuestionMark,           // C: UFBX_UNICODE_ERROR_HANDLING_QUESTION_MARK

        // Remove errors from the output
        Remove,                 // C: UFBX_UNICODE_ERROR_HANDLING_REMOVE

        // Fail loading on encountering an Unicode error
        AbortLoading,           // C: UFBX_UNICODE_ERROR_HANDLING_ABORT_LOADING

        // Ignore and pass-through non-UTF-8 string data.
        // Requires `ufbx_load_opts.allow_unsafe`.
        // UNSAFE: Breaks API guarantee that `ufbx_string` is UTF-8 encoded.
        UnsafeIgnore,           // C: UFBX_UNICODE_ERROR_HANDLING_UNSAFE_IGNORE
    }

    // C: ufbx_baked_key_flags
    public enum UfbxBakedKeyFlags
    {
        // This keyframe represents a constant step from the left side
        StepLeft = 0x1,     // C: UFBX_BAKED_KEY_STEP_LEFT

        // This keyframe represents a constant step from the right side
        StepRight = 0x2,    // C: UFBX_BAKED_KEY_STEP_RIGHT

        // This keyframe is the main part of a step
        // Bordering either `UFBX_BAKED_KEY_STEP_LEFT` or `UFBX_BAKED_KEY_STEP_RIGHT`.
        StepKey = 0x4,      // C: UFBX_BAKED_KEY_STEP_KEY

        // This keyframe is a real keyframe in the source animation
        Keyframe = 0x8,     // C: UFBX_BAKED_KEY_KEYFRAME

        // This keyframe has been reduced by maximum sample rate.
        // See `ufbx_bake_opts.maximum_sample_rate`.
        Reduced = 0x10,     // C: UFBX_BAKED_KEY_REDUCED
    }

    // C: ufbx_evaluate_flags
    // Flags to control nanimation evaluation functions.
    public enum UfbxEvaluateFlags
    {
        // Do not extrapolate past the keyframes.
        NoExtrapolation = 0x1,  // C: UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION
    }

    // C: ufbx_bake_step_handling
    // Specifies how to handle stepped tangents.
    public enum UfbxBakeStepHandling
    {
        // One millisecond default step duration, with potential extra slack for converting to `float`.
        Default,            // C: UFBX_BAKE_STEP_HANDLING_DEFAULT

        // Use a custom interpolation duration for the constant step.
        // See `ufbx_bake_opts.step_custom_duration` and optionally `ufbx_bake_opts.step_custom_epsilon`.
        CustomDuration,     // C: UFBX_BAKE_STEP_HANDLING_CUSTOM_DURATION

        // Stepped keyframes are represented as keyframes at the exact same time.
        // Use flags `UFBX_BAKED_KEY_STEP_LEFT` and `UFBX_BAKED_KEY_STEP_RIGHT` to differentiate
        // between the primary key and edge limits.
        IdenticalTime,      // C: UFBX_BAKE_STEP_HANDLING_IDENTICAL_TIME

        // Represent stepped keyframe times as the previous/next representable `double` value.
        // Using this and robust linear interpolation will handle stepped tangents correctly
        // without having to look at the key flags.
        // NOTE: Casting these values to `float` or otherwise modifying them can collapse
        // the keyframes to have the identical time.
        AdjacentDouble,     // C: UFBX_BAKE_STEP_HANDLING_ADJACENT_DOUBLE

        // Treat all stepped tangents as linearly interpolated.
        Ignore,             // C: UFBX_BAKE_STEP_HANDLING_IGNORE
    }

    // C: ufbx_transform_flags
    // Flags to control `ufbx_evaluate_transform_flags()`.
    public enum UfbxTransformFlags
    {
        // Ignore parent scale helper.
        IgnoreScaleHelper = 0x1,            // C: UFBX_TRANSFORM_FLAG_IGNORE_SCALE_HELPER

        // Ignore componentwise scale.
        // Note that if you don't specify this, ufbx will have to potentially
        // evaluate the entire parent chain in the worst case.
        IgnoreComponentwiseScale = 0x2,     // C: UFBX_TRANSFORM_FLAG_IGNORE_COMPONENTWISE_SCALE

        // Require explicit components
        ExplicitIncludes = 0x4,             // C: UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES

        // If `UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES`: Evaluate `ufbx_transform.translation`.
        IncludeTranslation = 0x10,          // C: UFBX_TRANSFORM_FLAG_INCLUDE_TRANSLATION

        // If `UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES`: Evaluate `ufbx_transform.rotation`.
        IncludeRotation = 0x20,             // C: UFBX_TRANSFORM_FLAG_INCLUDE_ROTATION

        // If `UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES`: Evaluate `ufbx_transform.scale`.
        IncludeScale = 0x40,                // C: UFBX_TRANSFORM_FLAG_INCLUDE_SCALE

        // Do not extrapolate keyframes.
        // See `UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION`.
        NoExtrapolation = 0x80,             // C: UFBX_TRANSFORM_FLAG_NO_EXTRAPOLATION
    }

    // C: UFBX_ENUM_TYPE(name, prefix, last) -> `UFBX_XXX_COUNT` macros.
    // Count == value of the last member + 1 (the FORCE_32BIT sentinels are excluded).
    public static class UfbxEnumCounts
    {
        public const int UfbxRotationOrder = 7;                         // C: UFBX_ROTATION_ORDER_COUNT
        public const int UfbxDomValueType = 9;                          // C: UFBX_DOM_VALUE_TYPE_COUNT
        public const int UfbxPropType = 16;                             // C: UFBX_PROP_TYPE_COUNT
        public const int UfbxElementType = 42;                          // C: UFBX_ELEMENT_TYPE_COUNT
        public const int UfbxInheritMode = 3;                           // C: UFBX_INHERIT_MODE_COUNT
        public const int UfbxMirrorAxis = 4;                            // C: UFBX_MIRROR_AXIS_COUNT
        public const int UfbxSubdivisionDisplayMode = 4;                // C: UFBX_SUBDIVISION_DISPLAY_MODE_COUNT
        public const int UfbxSubdivisionBoundary = 6;                   // C: UFBX_SUBDIVISION_BOUNDARY_COUNT
        public const int UfbxLightType = 5;                             // C: UFBX_LIGHT_TYPE_COUNT
        public const int UfbxLightDecay = 4;                            // C: UFBX_LIGHT_DECAY_COUNT
        public const int UfbxLightAreaShape = 2;                        // C: UFBX_LIGHT_AREA_SHAPE_COUNT
        public const int UfbxProjectionMode = 2;                        // C: UFBX_PROJECTION_MODE_COUNT
        public const int UfbxAspectMode = 5;                            // C: UFBX_ASPECT_MODE_COUNT
        public const int UfbxApertureMode = 4;                          // C: UFBX_APERTURE_MODE_COUNT
        public const int UfbxGateFit = 6;                               // C: UFBX_GATE_FIT_COUNT
        public const int UfbxApertureFormat = 12;                       // C: UFBX_APERTURE_FORMAT_COUNT
        public const int UfbxCoordinateAxis = 7;                        // C: UFBX_COORDINATE_AXIS_COUNT
        public const int UfbxNurbsTopology = 3;                         // C: UFBX_NURBS_TOPOLOGY_COUNT
        public const int UfbxMarkerType = 3;                            // C: UFBX_MARKER_TYPE_COUNT
        public const int UfbxLodDisplay = 3;                            // C: UFBX_LOD_DISPLAY_COUNT
        public const int UfbxSkinningMethod = 4;                        // C: UFBX_SKINNING_METHOD_COUNT
        public const int UfbxCacheFileFormat = 3;                       // C: UFBX_CACHE_FILE_FORMAT_COUNT
        public const int UfbxCacheDataFormat = 5;                       // C: UFBX_CACHE_DATA_FORMAT_COUNT
        public const int UfbxCacheDataEncoding = 3;                     // C: UFBX_CACHE_DATA_ENCODING_COUNT
        public const int UfbxCacheInterpretation = 4;                   // C: UFBX_CACHE_INTERPRETATION_COUNT
        public const int UfbxShaderType = 13;                           // C: UFBX_SHADER_TYPE_COUNT
        public const int UfbxMaterialFbxMap = 20;                       // C: UFBX_MATERIAL_FBX_MAP_COUNT
        public const int UfbxMaterialPbrMap = 56;                       // C: UFBX_MATERIAL_PBR_MAP_COUNT
        public const int UfbxMaterialFeature = 23;                      // C: UFBX_MATERIAL_FEATURE_COUNT
        public const int UfbxTextureType = 4;                           // C: UFBX_TEXTURE_TYPE_COUNT
        public const int UfbxBlendMode = 31;                            // C: UFBX_BLEND_MODE_COUNT
        public const int UfbxWrapMode = 2;                              // C: UFBX_WRAP_MODE_COUNT
        public const int UfbxShaderTextureType = 3;                     // C: UFBX_SHADER_TEXTURE_TYPE_COUNT
        public const int UfbxInterpolation = 4;                         // C: UFBX_INTERPOLATION_COUNT
        public const int UfbxExtrapolationMode = 5;                     // C: UFBX_EXTRAPOLATION_MODE_COUNT
        public const int UfbxConstraintType = 7;                        // C: UFBX_CONSTRAINT_TYPE_COUNT
        public const int UfbxConstraintAimUpType = 5;                   // C: UFBX_CONSTRAINT_AIM_UP_TYPE_COUNT
        public const int UfbxConstraintIkPoleType = 2;                  // C: UFBX_CONSTRAINT_IK_POLE_TYPE_COUNT
        public const int UfbxExporter = 6;                              // C: UFBX_EXPORTER_COUNT
        public const int UfbxFileFormat = 4;                            // C: UFBX_FILE_FORMAT_COUNT
        public const int UfbxWarningType = 15;                          // C: UFBX_WARNING_TYPE_COUNT
        public const int UfbxThumbnailFormat = 3;                       // C: UFBX_THUMBNAIL_FORMAT_COUNT
        public const int UfbxSpaceConversion = 3;                       // C: UFBX_SPACE_CONVERSION_COUNT
        public const int UfbxGeometryTransformHandling = 4;             // C: UFBX_GEOMETRY_TRANSFORM_HANDLING_COUNT
        public const int UfbxInheritModeHandling = 5;                   // C: UFBX_INHERIT_MODE_HANDLING_COUNT
        public const int UfbxPivotHandling = 3;                         // C: UFBX_PIVOT_HANDLING_COUNT
        public const int UfbxTimeMode = 18;                             // C: UFBX_TIME_MODE_COUNT
        public const int UfbxTimeProtocol = 3;                          // C: UFBX_TIME_PROTOCOL_COUNT
        public const int UfbxSnapMode = 4;                              // C: UFBX_SNAP_MODE_COUNT
        public const int UfbxOpenFileType = 3;                          // C: UFBX_OPEN_FILE_TYPE_COUNT
        public const int UfbxErrorType = 24;                            // C: UFBX_ERROR_TYPE_COUNT
        public const int UfbxIndexErrorHandling = 4;                    // C: UFBX_INDEX_ERROR_HANDLING_COUNT
        public const int UfbxUnicodeErrorHandling = 6;                  // C: UFBX_UNICODE_ERROR_HANDLING_COUNT
        public const int UfbxBakeStepHandling = 5;                      // C: UFBX_BAKE_STEP_HANDLING_COUNT
    }

    // Miscellaneous C constants from ufbx.h that the data model refers to.
    public static class UfbxConstants
    {
        // C: UFBX_NO_INDEX ((uint32_t)~0u) — sentinel value used to represent a missing index.
        public const uint NoIndex = 0xFFFFFFFFu;

        // C: UFBX_THREAD_GROUP_COUNT — number of thread groups to use if threading is enabled.
        // A thread group processes a number of tasks and is then waited and potentially
        // re-used later. In essence, this controls the granularity of threading.
        public const int ThreadGroupCount = 4;

        // C: UFBX_ERROR_STACK_MAX_DEPTH
        public const int ErrorStackMaxDepth = 8;

        // C: UFBX_PANIC_MESSAGE_LENGTH
        public const int PanicMessageLength = 128;

        // C: UFBX_ERROR_INFO_LENGTH
        public const int ErrorInfoLength = 256;

        // C: UFBX_VERSION / UFBX_HEADER_VERSION == ufbx_pack_version(0, 23, 1)
        public const uint HeaderVersion = 0u * 1000000u + 23u * 1000u + 1u;

        // C: UFBX_SOURCE_VERSION (ufbx.c:877) == ufbx_pack_version(0, 23, 1) — the *source*
        // file's version, which `ufbx_format_error()` prints as "ufbx v%u.%u.%u". It is a
        // separate macro from UFBX_HEADER_VERSION (ufbx.h:270) precisely so a mismatched
        // header/source pair is visible in error messages.
        public const uint SourceVersion = 0u * 1000000u + 23u * 1000u + 1u;
    }
}
