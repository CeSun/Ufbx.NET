// Option structs ported from ufbx v0.23.1 (ufbx.h ~4688-5238).
//
// Conventions (PORTING_NOTES.md #28):
//  - All opts are `class`, fields in C declaration order, PascalCase.
//  - Plain `new UfbxXxxOpts()` reproduces the C `{ 0 }` zero-initialization
//    (nested value-struct fields are `new`-initialized to emulate embedded
//    zeroed structs; strings are "" for C NULL/empty ufbx_string).
//  - `CreateDefault()` returns the *effective* defaults the C implementation
//    applies when the caller passes a zeroed opts (there is no public
//    `ufbx_default_*_opts()` in v0.23.1; normalization happens inline, see the
//    ufbx.c line references at each site).
//  - `_begin_zero` / `_end_zero` zero-check sentinels are omitted (C# fields are
//    always initialized; the C "not cleared to zero" error cannot occur).
//  - `size_t` -> `int`, `uint64_t` -> `ulong`, `ufbx_real` -> `double`,
//    `ufbx_string` -> `string`, `ufbx_blob` -> `byte[]`.

namespace Ufbx
{
    // Options for `ufbx_load_file/memory/stream/stdio()` (C: ufbx_load_opts, ufbx.h:4690-4938)
    public class UfbxLoadOpts
    {
        public UfbxAllocatorOpts TempAllocator = new UfbxAllocatorOpts();   // C: temp_allocator (ufbx.h:4693)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator (ufbx.h:4694)
        public UfbxThreadOpts ThreadOpts = new UfbxThreadOpts();            // C: thread_opts (ufbx.h:4695)

        // Preferences
        public bool IgnoreGeometry;         // C: ignore_geometry (ufbx.h:4698)
        public bool IgnoreAnimation;        // C: ignore_animation
        public bool IgnoreEmbedded;         // C: ignore_embedded
        public bool IgnoreAllContent;       // C: ignore_all_content

        public bool EvaluateSkinning;       // C: evaluate_skinning (ufbx.h:4703)
        public bool EvaluateCaches;         // C: evaluate_caches

        public bool LoadExternalFiles;      // C: load_external_files (ufbx.h:4716)
        public bool IgnoreMissingExternalFiles; // C: ignore_missing_external_files

        public bool SkipSkinVertices;       // C: skip_skin_vertices (ufbx.h:4723)
        public bool SkipMeshParts;          // C: skip_mesh_parts
        public bool CleanSkinWeights;       // C: clean_skin_weights
        public bool UseBlenderPbrMaterial;  // C: use_blender_pbr_material (ufbx.h:4735)

        public bool DisableQuirks;          // C: disable_quirks (ufbx.h:4738)
        public bool Strict;                 // C: strict
        public bool ForceSingleThreadAsciiParsing; // C: force_single_thread_ascii_parsing

        public bool AllowUnsafe;            // C: ufbx_unsafe bool allow_unsafe (ufbx.h:4750)

        public UfbxIndexErrorHandling IndexErrorHandling; // C: index_error_handling (ufbx.h:4753)

        public bool ConnectBrokenElements;  // C: connect_broken_elements (ufbx.h:4758)
        public bool AllowNodesOutOfRoot;    // C: allow_nodes_out_of_root
        public bool AllowMissingVertexPosition; // C: allow_missing_vertex_position
        public bool AllowEmptyFaces;        // C: allow_empty_faces
        public bool GenerateMissingNormals; // C: generate_missing_normals
        public bool OpenMainFileWithDefault; // C: open_main_file_with_default (ufbx.h:4776)

        public char PathSeparator;          // C: path_separator (ufbx.h:4779)

        public uint NodeDepthLimit;         // C: node_depth_limit (ufbx.h:4785)

        public ulong FileSizeEstimate;      // C: file_size_estimate (ufbx.h:4788)

        public int ReadBufferSize;          // C: read_buffer_size (size_t) (ufbx.h:4791)

        public string Filename = "";        // C: ufbx_string filename (ufbx.h:4796)
        public byte[] RawFilename;          // C: ufbx_blob raw_filename (ufbx.h:4800)

        public UfbxProgressCb ProgressCb = new UfbxProgressCb(); // C: progress_cb (ufbx.h:4803)
        public ulong ProgressIntervalHint;  // C: progress_interval_hint (ufbx.h:4804)

        public UfbxOpenFileCb OpenFileCb = new UfbxOpenFileCb(); // C: open_file_cb (ufbx.h:4807)

        public UfbxGeometryTransformHandling GeometryTransformHandling; // C: geometry_transform_handling (ufbx.h:4811)
        public UfbxInheritModeHandling InheritModeHandling;             // C: inherit_mode_handling (ufbx.h:4815)
        public UfbxSpaceConversion SpaceConversion;                     // C: space_conversion (ufbx.h:4819)
        public UfbxPivotHandling PivotHandling;                         // C: pivot_handling (ufbx.h:4823)
        public bool PivotHandlingRetainEmpties;                         // C: pivot_handling_retain_empties (ufbx.h:4826)

        public UfbxMirrorAxis HandednessConversionAxis;                 // C: handedness_conversion_axis (ufbx.h:4829)
        public bool HandednessConversionRetainWinding;                  // C: handedness_conversion_retain_winding
        public bool ReverseWinding;                                     // C: reverse_winding (ufbx.h:4837)

        public UfbxCoordinateAxes TargetAxes;                           // C: target_axes (ufbx.h:4841)
        public double TargetUnitMeters;                                 // C: target_unit_meters (ufbx.h:4845)
        public UfbxCoordinateAxes TargetCameraAxes;                     // C: target_camera_axes (ufbx.h:4850)
        public UfbxCoordinateAxes TargetLightAxes;                      // C: target_light_axes (ufbx.h:4855)

        public string GeometryTransformHelperName = "";                 // C: ufbx_string geometry_transform_helper_name (ufbx.h:4859)
        public string ScaleHelperName = "";                             // C: ufbx_string scale_helper_name (ufbx.h:4863)

        public bool NormalizeNormals;                                   // C: normalize_normals (ufbx.h:4866)
        public bool NormalizeTangents;                                  // C: normalize_tangents

        public bool UseRootTransform;                                   // C: use_root_transform (ufbx.h:4872)
        public UfbxTransform RootTransform;                             // C: root_transform

        public double KeyClampThreshold;                                // C: key_clamp_threshold (ufbx.h:4876)

        public UfbxUnicodeErrorHandling UnicodeErrorHandling;           // C: unicode_error_handling (ufbx.h:4879)

        public bool RetainVertexAttribW;                                // C: retain_vertex_attrib_w (ufbx.h:4883)
        public bool RetainDom;                                          // C: retain_dom

        public UfbxFileFormat FileFormat;                               // C: file_format (ufbx.h:4889)
        public int FileFormatLookahead;                                 // C: file_format_lookahead (size_t) (ufbx.h:4893)
        public bool NoFormatFromContent;                                // C: no_format_from_content
        public bool NoFormatFromExtension;                              // C: no_format_from_extension

        public bool ObjSearchMtlByFilename;                             // C: obj_search_mtl_by_filename (ufbx.h:4907)
        public bool ObjMergeObjects;                                    // C: obj_merge_objects
        public bool ObjMergeGroups;                                     // C: obj_merge_groups
        public bool ObjSplitGroups;                                     // C: obj_split_groups
        public string ObjMtlPath = "";                                  // C: ufbx_string obj_mtl_path (ufbx.h:4922)
        public byte[] ObjMtlData;                                       // C: ufbx_blob obj_mtl_data
        public double ObjUnitMeters;                                    // C: obj_unit_meters (ufbx.h:4930)
        public UfbxCoordinateAxes ObjAxes;                              // C: obj_axes (ufbx.h:4935)

        // Effective defaults applied inline by the C loader (ufbx.c:25511-25545):
        //  - temp/result allocators: huge_threshold 1MB, max_chunk_size 16MB
        //      (ufbxi_init_ator, ufbx.c:6935-6937)
        //  - read_buffer_size   0 -> 0x4000     (ufbx.c:25514-25515)
        //  - file_format_lookahead 0 -> 0x4000  (ufbx.c:25521-25522)
        //  - path_separator     0 -> UFBX_PATH_SEPARATOR ('\\' on Windows,
        //      where the golden binaries were built; ufbx.c:601-605, 25527-25529)
        //  - thread_opts.num_tasks 0 -> 2048 (ufbx.c:6072-6074),
        //    thread_opts.memory_limit 0 -> 32MB (ufbx.c:25543-25545)
        // All other load_opts fields have a genuine zero default in C.
        public static UfbxLoadOpts CreateDefault()
        {
            var opts = new UfbxLoadOpts();
            opts.TempAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ThreadOpts = UfbxThreadOpts.CreateDefault();
            opts.ReadBufferSize = 0x4000;
            opts.FileFormatLookahead = 0x4000;
            opts.PathSeparator = '\\';
            // Note: `open_file_cb.fn` defaults to `ufbx_default_open_file`
            // (ufbx.c:25539-25541); the C# default implementation is wired up by
            // the public API layer, so Fn stays null here.
            return opts;
        }
    }

    // Options for `ufbx_evaluate_scene()` (C: ufbx_evaluate_opts, ufbx.h:4942-4962)
    public class UfbxEvaluateOpts
    {
        public UfbxAllocatorOpts TempAllocator = new UfbxAllocatorOpts();   // C: temp_allocator (ufbx.h:4945)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator

        public bool EvaluateSkinning;   // C: evaluate_skinning (ufbx.h:4948)
        public bool EvaluateCaches;     // C: evaluate_caches

        public uint EvaluateFlags;      // C: uint32_t evaluate_flags (ufbx.h:4953, see UfbxEvaluateFlags)

        public bool LoadExternalFiles;  // C: load_external_files (ufbx.h:4956)

        public UfbxOpenFileCb OpenFileCb = new UfbxOpenFileCb(); // C: open_file_cb (ufbx.h:4959)

        public static UfbxEvaluateOpts CreateDefault()
        {
            var opts = new UfbxEvaluateOpts();
            opts.TempAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            return opts;
        }
    }

    // C: ufbx_prop_override_desc (ufbx.h:4967-4979)
    public struct UfbxPropOverrideDesc
    {
        public uint ElementId;          // C: element_id (ufbx.h:4969)
        public string PropName;         // C: ufbx_string prop_name
        public UfbxVec4 Value;          // C: value
        public string ValueStr;         // C: ufbx_string value_str
        public long ValueInt;           // C: int64_t value_int
    }

    // C: ufbx_anim_opts (ufbx.h:4985-5009)
    public class UfbxAnimOpts
    {
        public uint[] LayerIds;                     // C: ufbx_const_uint32_list layer_ids (ufbx.h:4990)
        public double[] OverrideLayerWeights;       // C: ufbx_const_real_list override_layer_weights (ufbx.h:4993)
        public UfbxPropOverrideDesc[] PropOverrides; // C: ufbx_const_prop_override_desc_list prop_overrides (ufbx.h:4997)
        public UfbxTransformOverride[] TransformOverrides; // C: ufbx_const_transform_override_list transform_overrides (ufbx.h:5001)
        public bool IgnoreConnections;              // C: ignore_connections (ufbx.h:5004)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator (ufbx.h:5006)

        public static UfbxAnimOpts CreateDefault()
        {
            var opts = new UfbxAnimOpts();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            return opts;
        }
    }

    // C: ufbx_bake_opts (ufbx.h:5041-5117)
    public class UfbxBakeOpts
    {
        public UfbxAllocatorOpts TempAllocator = new UfbxAllocatorOpts();   // C: temp_allocator (ufbx.h:5044)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator

        public bool TrimStartTime;          // C: trim_start_time (ufbx.h:5052)
        public double ResampleRate;         // C: resample_rate (ufbx.h:5056), Default: 30
        public double MinimumSampleRate;    // C: minimum_sample_rate (ufbx.h:5062), Default: 19.5
        public double MaximumSampleRate;    // C: maximum_sample_rate (ufbx.h:5066), Default: unlimited (0)
        public bool BakeTransformProps;     // C: bake_transform_props (ufbx.h:5069)
        public bool SkipNodeTransforms;     // C: skip_node_transforms
        public bool NoResampleRotation;     // C: no_resample_rotation
        public bool IgnoreLayerWeightAnimation; // C: ignore_layer_weight_animation
        public int MaxKeyframeSegments;     // C: max_keyframe_segments (size_t) (ufbx.h:5083), Default: 32
        public UfbxBakeStepHandling StepHandling; // C: step_handling (ufbx.h:5086)
        public double StepCustomDuration;   // C: step_custom_duration (ufbx.h:5089)
        public double StepCustomEpsilon;    // C: step_custom_epsilon
        public uint EvaluateFlags;          // C: uint32_t evaluate_flags (ufbx.h:5098)
        public bool KeyReductionEnabled;    // C: key_reduction_enabled (ufbx.h:5101)
        public bool KeyReductionRotation;   // C: key_reduction_rotation
        public double KeyReductionThreshold; // C: key_reduction_threshold (ufbx.h:5109), Default: 0.000001
        public int KeyReductionPasses;      // C: key_reduction_passes (size_t) (ufbx.h:5114), Default: 4

        // Effective defaults applied inline by ufbxi_bake_anim_imp (ufbx.c:27717-27721):
        //   resample_rate <= 0 -> 30.0
        //   minimum_sample_rate <= 0 -> 19.5
        //   max_keyframe_segments == 0 -> 32
        //   key_reduction_threshold == 0 -> 0.000001
        //   key_reduction_passes == 0 -> 4
        public static UfbxBakeOpts CreateDefault()
        {
            var opts = new UfbxBakeOpts();
            opts.TempAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResampleRate = 30.0;
            opts.MinimumSampleRate = 19.5;
            opts.MaxKeyframeSegments = 32;
            opts.KeyReductionThreshold = 0.000001;
            opts.KeyReductionPasses = 4;
            return opts;
        }
    }

    // Options for `ufbx_tessellate_nurbs_curve()` (C: ufbx_tessellate_curve_opts, ufbx.h:5121-5131)
    public class UfbxTessellateCurveOpts
    {
        public UfbxAllocatorOpts TempAllocator = new UfbxAllocatorOpts();   // C: temp_allocator (ufbx.h:5124)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator
        public int SpanSubdivision;     // C: span_subdivision (size_t) (ufbx.h:5128), Default: 4

        // ufbx.c:27850-27851: span_subdivision <= 0 -> 4
        public static UfbxTessellateCurveOpts CreateDefault()
        {
            var opts = new UfbxTessellateCurveOpts();
            opts.TempAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.SpanSubdivision = 4;
            return opts;
        }
    }

    // Options for `ufbx_tessellate_nurbs_surface()` (C: ufbx_tessellate_surface_opts, ufbx.h:5135-5153)
    public class UfbxTessellateSurfaceOpts
    {
        public UfbxAllocatorOpts TempAllocator = new UfbxAllocatorOpts();   // C: temp_allocator (ufbx.h:5138)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator
        public int SpanSubdivisionU;    // C: span_subdivision_u (size_t) (ufbx.h:5146), Default: 4
        public int SpanSubdivisionV;    // C: span_subdivision_v (size_t), Default: 4
        public bool SkipMeshParts;      // C: skip_mesh_parts (ufbx.h:5150)

        // ufbx.c:27943-27947: span_subdivision_u/v <= 0 -> 4
        public static UfbxTessellateSurfaceOpts CreateDefault()
        {
            var opts = new UfbxTessellateSurfaceOpts();
            opts.TempAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.SpanSubdivisionU = 4;
            opts.SpanSubdivisionV = 4;
            return opts;
        }
    }

    // Options for `ufbx_subdivide_mesh()` (C: ufbx_subdivide_opts, ufbx.h:5157-5194)
    public class UfbxSubdivideOpts
    {
        public UfbxAllocatorOpts TempAllocator = new UfbxAllocatorOpts();   // C: temp_allocator (ufbx.h:5160)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator
        public UfbxSubdivisionBoundary Boundary;    // C: boundary (ufbx.h:5163)
        public UfbxSubdivisionBoundary UvBoundary;  // C: uv_boundary
        public bool IgnoreNormals;                  // C: ignore_normals (ufbx.h:5167)
        public bool InterpolateNormals;             // C: interpolate_normals
        public bool InterpolateTangents;            // C: interpolate_tangents (ufbx.h:5174)
        public bool EvaluateSourceVertices;         // C: evaluate_source_vertices
        public int MaxSourceVertices;               // C: max_source_vertices (size_t) (ufbx.h:5181)
        public bool EvaluateSkinWeights;            // C: evaluate_skin_weights
        public int MaxSkinWeights;                  // C: max_skin_weights (size_t)
        public int SkinDeformerIndex;               // C: skin_deformer_index (size_t) (ufbx.h:5191)

        // NOTE: Boundary/UvBoundary default `Default` (=0) meaning "copy from mesh"
        // (ufbx.c:29937-29942); max_* limits 0 mean unlimited (ufbx.c:29742,29755).
        public static UfbxSubdivideOpts CreateDefault()
        {
            var opts = new UfbxSubdivideOpts();
            opts.TempAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            return opts;
        }
    }

    // Options for `ufbx_load_geometry_cache()` (C: ufbx_geometry_cache_opts, ufbx.h:5198-5220)
    public class UfbxGeometryCacheOpts
    {
        public UfbxAllocatorOpts TempAllocator = new UfbxAllocatorOpts();   // C: temp_allocator (ufbx.h:5201)
        public UfbxAllocatorOpts ResultAllocator = new UfbxAllocatorOpts(); // C: result_allocator
        public UfbxOpenFileCb OpenFileCb = new UfbxOpenFileCb();            // C: open_file_cb (ufbx.h:5205)
        public double FramesPerSecond;              // C: frames_per_second (ufbx.h:5208), Default: 30
        public UfbxMirrorAxis MirrorAxis;           // C: mirror_axis (ufbx.h:5211)
        public bool UseScaleFactor;                 // C: use_scale_factor (ufbx.h:5214)
        public double ScaleFactor;                  // C: scale_factor (ufbx_real) (ufbx.h:5217)

        // ufbx.c:24752: frames_per_second <= 0 ? 30.0 : value
        public static UfbxGeometryCacheOpts CreateDefault()
        {
            var opts = new UfbxGeometryCacheOpts();
            opts.TempAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.ResultAllocator = UfbxAllocatorOpts.CreateDefault();
            opts.FramesPerSecond = 30.0;
            return opts;
        }
    }

    // Options for `ufbx_read_geometry_cache_TYPE()` (C: ufbx_geometry_cache_data_opts, ufbx.h:5224-5238)
    public class UfbxGeometryCacheDataOpts
    {
        public UfbxOpenFileCb OpenFileCb = new UfbxOpenFileCb(); // C: open_file_cb (ufbx.h:5228)
        public bool Additive;           // C: additive (ufbx.h:5230)
        public bool UseWeight;          // C: use_weight
        public double Weight;           // C: weight (ufbx_real)
        public bool IgnoreTransform;    // C: ignore_transform (ufbx.h:5235)

        public static UfbxGeometryCacheDataOpts CreateDefault()
        {
            return new UfbxGeometryCacheDataOpts();
        }
    }
}
