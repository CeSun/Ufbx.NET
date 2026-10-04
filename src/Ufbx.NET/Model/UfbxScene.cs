// Scene data model, ported from ufbx v0.23.1 (ufbx.h).
// Covers: ufbx_application, ufbx_warning, ufbx_thumbnail, ufbx_metadata,
//         ufbx_scene_settings, ufbx_name_element, ufbx_scene.

namespace Ufbx.NET
{
    // C: typedef struct ufbx_application
    public struct UfbxApplication
    {
        public string Vendor;   // C: vendor
        public string Name;     // C: name
        public string Version;  // C: version
    }

    // C: typedef struct ufbx_warning
    // Reference type: ufbx.c keeps `ufbx_warning*` pointers around to deduplicate warnings by
    // incrementing `count` on an already-pushed entry.
    public sealed class UfbxWarning
    {
        public UfbxWarningType Type;   // C: type
        public string Description;     // C: description
        public uint ElementId;         // C: element_id
        public int Count;              // C: count
    }

    // C: typedef struct ufbx_thumbnail
    public sealed class UfbxThumbnail
    {
        public UfbxProps Props = new UfbxProps();  // C: props (embedded value in C, never NULL)

        public uint Width;   // C: width
        public uint Height;  // C: height

        public UfbxThumbnailFormat Format;  // C: format

        public byte[] Data;  // C: data (ufbx_blob)
    }

    // C: typedef struct ufbx_metadata
    public sealed class UfbxMetadata
    {
        public UfbxWarning[] Warnings;   // C: warnings (ufbx_warning_list)

        public bool Ascii;               // C: ascii
        public uint Version;             // C: version

        public UfbxFileFormat FileFormat; // C: file_format

        public bool MayContainNoIndex;                // C: may_contain_no_index
        public bool MayContainMissingVertexPosition;  // C: may_contain_missing_vertex_position
        public bool MayContainBrokenElements;         // C: may_contain_broken_elements
        public bool IsUnsafe;                         // C: is_unsafe

        public bool[] HasWarning = new bool[UfbxEnumCounts.UfbxWarningType]; // C: has_warning[UFBX_WARNING_TYPE_COUNT]

        public string Creator;      // C: creator
        public bool BigEndian;      // C: big_endian

        public string Filename;     // C: filename
        public string RelativeRoot; // C: relative_root

        public byte[] RawFilename;      // C: raw_filename (ufbx_blob)
        public byte[] RawRelativeRoot;  // C: raw_relative_root

        public UfbxExporter Exporter;   // C: exporter
        public uint ExporterVersion;    // C: exporter_version

        public UfbxProps SceneProps = new UfbxProps(); // C: scene_props (embedded value in C)

        public UfbxApplication OriginalApplication;  // C: original_application
        public UfbxApplication LatestApplication;    // C: latest_application

        public UfbxThumbnail Thumbnail = new UfbxThumbnail();  // C: thumbnail (embedded value in C)

        public bool GeometryIgnored;  // C: geometry_ignored
        public bool AnimationIgnored; // C: animation_ignored
        public bool EmbeddedIgnored;  // C: embedded_ignored

        public int MaxFaceTriangles;  // C: max_face_triangles

        public int ResultMemoryUsed;  // C: result_memory_used
        public int TempMemoryUsed;    // C: temp_memory_used
        public int ResultAllocs;      // C: result_allocs
        public int TempAllocs;        // C: temp_allocs

        public int ElementBufferSize;  // C: element_buffer_size
        public int NumShaderTextures;  // C: num_shader_textures

        public double BonePropSizeUnit;         // C: bone_prop_size_unit
        public bool BonePropLimbLengthRelative; // C: bone_prop_limb_length_relative

        public double OrthoSizeUnit;  // C: ortho_size_unit

        public long KtimeSecond;      // C: ktime_second

        public string OriginalFilePath;      // C: original_file_path
        public byte[] RawOriginalFilePath;   // C: raw_original_file_path

        public UfbxSpaceConversion SpaceConversion;                   // C: space_conversion
        public UfbxGeometryTransformHandling GeometryTransformHandling; // C: geometry_transform_handling
        public UfbxInheritModeHandling InheritModeHandling;            // C: inherit_mode_handling
        public UfbxPivotHandling PivotHandling;                        // C: pivot_handling
        public UfbxMirrorAxis HandednessConversionAxis;                // C: handedness_conversion_axis

        public UfbxQuat RootRotation;  // C: root_rotation
        public double RootScale;       // C: root_scale

        public UfbxMirrorAxis MirrorAxis;  // C: mirror_axis

        public double GeometryScale;   // C: geometry_scale
    }

    // C: typedef struct ufbx_scene_settings
    public sealed class UfbxSceneSettings
    {
        public UfbxProps Props = new UfbxProps();  // C: props (embedded value in C, never NULL)

        public UfbxCoordinateAxes Axes;   // C: axes

        public double UnitMeters;         // C: unit_meters
        public double FramesPerSecond;    // C: frames_per_second

        public UfbxVec3 AmbientColor;     // C: ambient_color
        public string DefaultCamera;      // C: default_camera

        public UfbxTimeMode TimeMode;         // C: time_mode
        public UfbxTimeProtocol TimeProtocol; // C: time_protocol
        public UfbxSnapMode SnapMode;         // C: snap_mode

        public UfbxCoordinateAxis OriginalAxisUp; // C: original_axis_up

        public double OriginalUnitMeters;     // C: original_unit_meters
    }

    // C: typedef struct ufbx_name_element
    public struct UfbxNameElement
    {
        public string Name;              // C: name
        public UfbxElementType Type;     // C: type
        public uint InternalKey;         // C: _internal_key
        public UfbxElement Element;      // C: element
    }

    // C: struct ufbx_scene
    public sealed class UfbxScene
    {
        public UfbxMetadata Metadata = new UfbxMetadata();   // C: metadata (embedded value in C)
        public UfbxSceneSettings Settings = new UfbxSceneSettings(); // C: settings (embedded value in C)

        public UfbxNode RootNode;   // C: root_node

        public UfbxAnim Anim;       // C: anim

        // C: the anonymous union of typed element lists (aliased with `elements_by_type[]`)
        public UfbxUnknown[] Unknowns;                    // C: unknowns
        public UfbxNode[] Nodes;                          // C: nodes
        public UfbxMesh[] Meshes;                         // C: meshes
        public UfbxLight[] Lights;                        // C: lights
        public UfbxCamera[] Cameras;                      // C: cameras
        public UfbxBone[] Bones;                          // C: bones
        public UfbxEmpty[] Empties;                       // C: empties
        public UfbxLineCurve[] LineCurves;                // C: line_curves
        public UfbxNurbsCurve[] NurbsCurves;              // C: nurbs_curves
        public UfbxNurbsSurface[] NurbsSurfaces;          // C: nurbs_surfaces
        public UfbxNurbsTrimSurface[] NurbsTrimSurfaces;  // C: nurbs_trim_surfaces
        public UfbxNurbsTrimBoundary[] NurbsTrimBoundaries; // C: nurbs_trim_boundaries
        public UfbxProceduralGeometry[] ProceduralGeometries; // C: procedural_geometries
        public UfbxStereoCamera[] StereoCameras;          // C: stereo_cameras
        public UfbxCameraSwitcher[] CameraSwitchers;      // C: camera_switchers
        public UfbxMarker[] Markers;                      // C: markers
        public UfbxLodGroup[] LodGroups;                  // C: lod_groups
        public UfbxSkinDeformer[] SkinDeformers;          // C: skin_deformers
        public UfbxSkinCluster[] SkinClusters;            // C: skin_clusters
        public UfbxBlendDeformer[] BlendDeformers;        // C: blend_deformers
        public UfbxBlendChannel[] BlendChannels;          // C: blend_channels
        public UfbxBlendShape[] BlendShapes;              // C: blend_shapes
        public UfbxCacheDeformer[] CacheDeformers;        // C: cache_deformers
        public UfbxCacheFile[] CacheFiles;                // C: cache_files
        public UfbxMaterial[] Materials;                  // C: materials
        public UfbxTexture[] Textures;                    // C: textures
        public UfbxVideo[] Videos;                        // C: videos
        public UfbxShader[] Shaders;                      // C: shaders
        public UfbxShaderBinding[] ShaderBindings;        // C: shader_bindings
        public UfbxAnimStack[] AnimStacks;                // C: anim_stacks
        public UfbxAnimLayer[] AnimLayers;                // C: anim_layers
        public UfbxAnimValue[] AnimValues;                // C: anim_values
        public UfbxAnimCurve[] AnimCurves;                // C: anim_curves
        public UfbxDisplayLayer[] DisplayLayers;          // C: display_layers
        public UfbxSelectionSet[] SelectionSets;          // C: selection_sets
        public UfbxSelectionNode[] SelectionNodes;        // C: selection_nodes
        public UfbxCharacter[] Characters;                // C: characters
        public UfbxConstraint[] Constraints;              // C: constraints
        public UfbxAudioLayer[] AudioLayers;              // C: audio_layers
        public UfbxAudioClip[] AudioClips;                // C: audio_clips
        public UfbxPose[] Poses;                          // C: poses
        public UfbxMetadataObject[] MetadataObjects;      // C: metadata_objects

        // C: ufbx_element_list elements_by_type[UFBX_ELEMENT_TYPE_COUNT]; -- the union view over
        // the typed lists above. C# arrays are covariant for reads but each list keeps its own
        // concrete array type, so the view is exposed as get/set helpers indexed by
        // `UfbxElementType` (0..41).
        internal UfbxElement[] ElementsByType(int type)
        {
            switch (type)
            {
                case 0: return Unknowns;
                case 1: return Nodes;
                case 2: return Meshes;
                case 3: return Lights;
                case 4: return Cameras;
                case 5: return Bones;
                case 6: return Empties;
                case 7: return LineCurves;
                case 8: return NurbsCurves;
                case 9: return NurbsSurfaces;
                case 10: return NurbsTrimSurfaces;
                case 11: return NurbsTrimBoundaries;
                case 12: return ProceduralGeometries;
                case 13: return StereoCameras;
                case 14: return CameraSwitchers;
                case 15: return Markers;
                case 16: return LodGroups;
                case 17: return SkinDeformers;
                case 18: return SkinClusters;
                case 19: return BlendDeformers;
                case 20: return BlendChannels;
                case 21: return BlendShapes;
                case 22: return CacheDeformers;
                case 23: return CacheFiles;
                case 24: return Materials;
                case 25: return Textures;
                case 26: return Videos;
                case 27: return Shaders;
                case 28: return ShaderBindings;
                case 29: return AnimStacks;
                case 30: return AnimLayers;
                case 31: return AnimValues;
                case 32: return AnimCurves;
                case 33: return DisplayLayers;
                case 34: return SelectionSets;
                case 35: return SelectionNodes;
                case 36: return Characters;
                case 37: return Constraints;
                case 38: return AudioLayers;
                case 39: return AudioClips;
                case 40: return Poses;
                case 41: return MetadataObjects;
                default: return null;
            }
        }

        // C: `ufbx_element_list *list = &scene.elements_by_type[type]` followed by `list->data = ...`
        internal void SetElementsByType(int type, UfbxElement[] list)
        {
            switch (type)
            {
                case 0: Unknowns = (UfbxUnknown[])list; break;
                case 1: Nodes = (UfbxNode[])list; break;
                case 2: Meshes = (UfbxMesh[])list; break;
                case 3: Lights = (UfbxLight[])list; break;
                case 4: Cameras = (UfbxCamera[])list; break;
                case 5: Bones = (UfbxBone[])list; break;
                case 6: Empties = (UfbxEmpty[])list; break;
                case 7: LineCurves = (UfbxLineCurve[])list; break;
                case 8: NurbsCurves = (UfbxNurbsCurve[])list; break;
                case 9: NurbsSurfaces = (UfbxNurbsSurface[])list; break;
                case 10: NurbsTrimSurfaces = (UfbxNurbsTrimSurface[])list; break;
                case 11: NurbsTrimBoundaries = (UfbxNurbsTrimBoundary[])list; break;
                case 12: ProceduralGeometries = (UfbxProceduralGeometry[])list; break;
                case 13: StereoCameras = (UfbxStereoCamera[])list; break;
                case 14: CameraSwitchers = (UfbxCameraSwitcher[])list; break;
                case 15: Markers = (UfbxMarker[])list; break;
                case 16: LodGroups = (UfbxLodGroup[])list; break;
                case 17: SkinDeformers = (UfbxSkinDeformer[])list; break;
                case 18: SkinClusters = (UfbxSkinCluster[])list; break;
                case 19: BlendDeformers = (UfbxBlendDeformer[])list; break;
                case 20: BlendChannels = (UfbxBlendChannel[])list; break;
                case 21: BlendShapes = (UfbxBlendShape[])list; break;
                case 22: CacheDeformers = (UfbxCacheDeformer[])list; break;
                case 23: CacheFiles = (UfbxCacheFile[])list; break;
                case 24: Materials = (UfbxMaterial[])list; break;
                case 25: Textures = (UfbxTexture[])list; break;
                case 26: Videos = (UfbxVideo[])list; break;
                case 27: Shaders = (UfbxShader[])list; break;
                case 28: ShaderBindings = (UfbxShaderBinding[])list; break;
                case 29: AnimStacks = (UfbxAnimStack[])list; break;
                case 30: AnimLayers = (UfbxAnimLayer[])list; break;
                case 31: AnimValues = (UfbxAnimValue[])list; break;
                case 32: AnimCurves = (UfbxAnimCurve[])list; break;
                case 33: DisplayLayers = (UfbxDisplayLayer[])list; break;
                case 34: SelectionSets = (UfbxSelectionSet[])list; break;
                case 35: SelectionNodes = (UfbxSelectionNode[])list; break;
                case 36: Characters = (UfbxCharacter[])list; break;
                case 37: Constraints = (UfbxConstraint[])list; break;
                case 38: AudioLayers = (UfbxAudioLayer[])list; break;
                case 39: AudioClips = (UfbxAudioClip[])list; break;
                case 40: Poses = (UfbxPose[])list; break;
                case 41: MetadataObjects = (UfbxMetadataObject[])list; break;
            }
        }

        public UfbxTextureFile[] TextureFiles;   // C: texture_files (ufbx_texture_file_list)

        public UfbxElement[] Elements;           // C: elements (ufbx_element_list)
        public UfbxConnection[] ConnectionsSrc;  // C: connections_src (ufbx_connection_list)
        public UfbxConnection[] ConnectionsDst;  // C: connections_dst (ufbx_connection_list)

        public UfbxNameElement[] ElementsByName; // C: elements_by_name (ufbx_name_element_list)

        public UfbxDomNode DomRoot;              // C: dom_root

        // C: `ec->scene = ec->src_scene` (ufbx.c:26115) -- the struct copy whose list fields are
        // then replaced one by one while the *source* scene keeps serving as the read side.
        // `Metadata`/`Settings`/`DomRoot`/`TextureFiles` stay shared: C memcpy'd those embedded
        // structs by value, but nothing on the evaluation path writes into them, so a shared
        // copy is observationally identical (and the port has no arena statistics to update).
        internal UfbxScene CloneShallow() => (UfbxScene)MemberwiseClone();
    }
}
