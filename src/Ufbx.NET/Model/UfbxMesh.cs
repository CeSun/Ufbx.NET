// Mesh data model, ported from ufbx v0.23.1 (ufbx.h).
// Covers: ufbx_vertex_attrib, ufbx_vertex_real/vec2/vec3/vec4, ufbx_uv_set, ufbx_color_set,
//         ufbx_edge, ufbx_face, ufbx_mesh_part, ufbx_face_group, ufbx_subdivision_*.

using System;

namespace Ufbx.NET
{
    // C: typedef struct ufbx_vertex_attrib
    // `values` is `ufbx_void_list` in C (untyped alias of the concrete attribute buffer),
    // so it is exposed as System.Array holding the same typed array as the concrete fields.
    public struct UfbxVertexAttrib
    {
        public bool Exists;          // C: exists
        public Array Values;         // C: values (ufbx_void_list)
        public uint[] Indices;       // C: indices (ufbx_uint32_list)
        public int ValueReals;       // C: value_reals
        public bool UniquePerVertex; // C: unique_per_vertex
        public double[] ValuesW;     // C: values_w (ufbx_real_list)
    }

    // C: typedef struct ufbx_vertex_real (1D vertex attribute)
    public struct UfbxVertexReal
    {
        public bool Exists;           // C: exists
        public double[] Values;       // C: values (ufbx_real_list)
        public uint[] Indices;        // C: indices (ufbx_uint32_list)
        public int ValueReals;        // C: value_reals
        public bool UniquePerVertex;  // C: unique_per_vertex
        public double[] ValuesW;      // C: values_w (ufbx_real_list)

        // C++ only: UFBX_VERTEX_ATTRIB_IMPL(ufbx_real) -- values.data[indices.data[index]]
        internal double At(uint index) => Values[Indices[index]];
    }

    // C: typedef struct ufbx_vertex_vec2 (2D vertex attribute)
    public struct UfbxVertexVec2
    {
        public bool Exists;
        public UfbxVec2[] Values;
        public uint[] Indices;
        public int ValueReals;
        public bool UniquePerVertex;
        public double[] ValuesW;

        internal UfbxVec2 At(uint index) => Values[Indices[index]];
    }

    // C: typedef struct ufbx_vertex_vec3 (3D vertex attribute)
    public struct UfbxVertexVec3
    {
        public bool Exists;
        public UfbxVec3[] Values;
        public uint[] Indices;
        public int ValueReals;
        public bool UniquePerVertex;
        public double[] ValuesW;

        internal UfbxVec3 At(uint index) => Values[Indices[index]];
    }

    // C: typedef struct ufbx_vertex_vec4 (4D vertex attribute)
    public struct UfbxVertexVec4
    {
        public bool Exists;
        public UfbxVec4[] Values;
        public uint[] Indices;
        public int ValueReals;
        public bool UniquePerVertex;
        public double[] ValuesW;

        internal UfbxVec4 At(uint index) => Values[Indices[index]];
    }

    // C: typedef struct ufbx_uv_set
    public struct UfbxUvSet
    {
        public string Name;              // C: name
        public uint Index;               // C: index
        public UfbxVertexVec2 VertexUv;  // C: vertex_uv
        public UfbxVertexVec3 VertexTangent;   // C: vertex_tangent
        public UfbxVertexVec3 VertexBitangent; // C: vertex_bitangent
    }

    // C: typedef struct ufbx_color_set
    public struct UfbxColorSet
    {
        public string Name;             // C: name
        public uint Index;              // C: index
        public UfbxVertexVec4 VertexColor; // C: vertex_color
    }

    // C: typedef struct ufbx_edge
    // C union { struct { uint32_t a, b; }; uint32_t indices[2]; } -> A/B (IndicesAt(i) == (A,B)[i])
    public struct UfbxEdge
    {
        public uint A; // C: a
        public uint B; // C: b

        // C: indices[i]
        internal uint IndexAt(int index) => index == 0 ? A : B;
    }

    // C: typedef struct ufbx_face
    public struct UfbxFace
    {
        public uint IndexBegin;  // C: index_begin
        public uint NumIndices;  // C: num_indices
    }

    // C: typedef struct ufbx_mesh_part
    public struct UfbxMeshPart
    {
        public uint Index;            // C: index
        public int NumFaces;          // C: num_faces
        public int NumTriangles;      // C: num_triangles
        public int NumEmptyFaces;     // C: num_empty_faces
        public int NumPointFaces;     // C: num_point_faces
        public int NumLineFaces;      // C: num_line_faces
        public uint[] FaceIndices;    // C: face_indices (ufbx_uint32_list)
    }

    // C: typedef struct ufbx_face_group
    public struct UfbxFaceGroup
    {
        public int Id;      // C: id
        public string Name; // C: name
    }

    // C: typedef struct ufbx_subdivision_weight_range
    public struct UfbxSubdivisionWeightRange
    {
        public uint WeightBegin; // C: weight_begin
        public uint NumWeights;  // C: num_weights
    }

    // C: typedef struct ufbx_subdivision_weight
    public struct UfbxSubdivisionWeight
    {
        public double Weight; // C: weight
        public uint Index;    // C: index
    }

    // C: typedef struct ufbx_subdivision_result
    public sealed class UfbxSubdivisionResult
    {
        public int ResultMemoryUsed; // C: result_memory_used
        public int TempMemoryUsed;   // C: temp_memory_used
        public int ResultAllocs;     // C: result_allocs
        public int TempAllocs;       // C: temp_allocs

        public UfbxSubdivisionWeightRange[] SourceVertexRanges; // C: source_vertex_ranges
        public UfbxSubdivisionWeight[] SourceVertexWeights;     // C: source_vertex_weights

        public UfbxSubdivisionWeightRange[] SkinClusterRanges;  // C: skin_cluster_ranges
        public UfbxSubdivisionWeight[] SkinClusterWeights;      // C: skin_cluster_weights
    }

    // C: struct ufbx_mesh
    public sealed class UfbxMesh : UfbxElement
    {
        public int NumVertices;      // C: num_vertices
        public int NumIndices;       // C: num_indices
        public int NumFaces;         // C: num_faces
        public int NumTriangles;     // C: num_triangles
        public int NumEdges;         // C: num_edges

        public int MaxFaceTriangles; // C: max_face_triangles

        public int NumEmptyFaces;    // C: num_empty_faces
        public int NumPointFaces;    // C: num_point_faces
        public int NumLineFaces;     // C: num_line_faces

        public UfbxFace[] Faces;        // C: faces (ufbx_face_list)
        public bool[] FaceSmoothing;    // C: face_smoothing (ufbx_bool_list)
        public uint[] FaceMaterial;     // C: face_material (ufbx_uint32_list)
        public uint[] FaceGroup;        // C: face_group (ufbx_uint32_list)
        public bool[] FaceHole;         // C: face_hole (ufbx_bool_list)

        public UfbxEdge[] Edges;        // C: edges (ufbx_edge_list)
        public bool[] EdgeSmoothing;    // C: edge_smoothing (ufbx_bool_list)
        public double[] EdgeCrease;     // C: edge_crease (ufbx_real_list)
        public bool[] EdgeVisibility;   // C: edge_visibility (ufbx_bool_list)

        public uint[] VertexIndices;      // C: vertex_indices (ufbx_uint32_list)
        public UfbxVec3[] Vertices;       // C: vertices (ufbx_vec3_list)

        public uint[] VertexFirstIndex;   // C: vertex_first_index (ufbx_uint32_list)

        public UfbxVertexVec3 VertexPosition;   // C: vertex_position
        public UfbxVertexVec3 VertexNormal;     // C: vertex_normal
        public UfbxVertexVec2 VertexUv;         // C: vertex_uv
        public UfbxVertexVec3 VertexTangent;    // C: vertex_tangent
        public UfbxVertexVec3 VertexBitangent;  // C: vertex_bitangent
        public UfbxVertexVec4 VertexColor;      // C: vertex_color
        public UfbxVertexReal VertexCrease;     // C: vertex_crease

        public UfbxUvSet[] UvSets;          // C: uv_sets (ufbx_uv_set_list)
        public UfbxColorSet[] ColorSets;    // C: color_sets (ufbx_color_set_list)

        public UfbxMaterial[] Materials;    // C: materials (ufbx_material_list)

        public UfbxFaceGroup[] FaceGroups;  // C: face_groups (ufbx_face_group_list)

        public UfbxMeshPart[] MaterialParts;    // C: material_parts (ufbx_mesh_part_list)
        public UfbxMeshPart[] FaceGroupParts;   // C: face_group_parts (ufbx_mesh_part_list)

        public uint[] MaterialPartUsageOrder;   // C: material_part_usage_order (ufbx_uint32_list)

        public bool SkinnedIsLocal;             // C: skinned_is_local
        public UfbxVertexVec3 SkinnedPosition;  // C: skinned_position
        public UfbxVertexVec3 SkinnedNormal;    // C: skinned_normal

        public UfbxSkinDeformer[] SkinDeformers;    // C: skin_deformers (ufbx_skin_deformer_list)
        public UfbxBlendDeformer[] BlendDeformers;  // C: blend_deformers (ufbx_blend_deformer_list)
        public UfbxCacheDeformer[] CacheDeformers;  // C: cache_deformers (ufbx_cache_deformer_list)
        public UfbxElement[] AllDeformers;          // C: all_deformers (ufbx_element_list)

        public uint SubdivisionPreviewLevels;           // C: subdivision_preview_levels
        public uint SubdivisionRenderLevels;            // C: subdivision_render_levels
        public UfbxSubdivisionDisplayMode SubdivisionDisplayMode;   // C: subdivision_display_mode
        public UfbxSubdivisionBoundary SubdivisionBoundary;         // C: subdivision_boundary
        public UfbxSubdivisionBoundary SubdivisionUvBoundary;       // C: subdivision_uv_boundary

        public bool ReversedWinding;    // C: reversed_winding
        public bool GeneratedNormals;   // C: generated_normals

        public bool SubdivisionEvaluated;           // C: subdivision_evaluated
        public UfbxSubdivisionResult SubdivisionResult; // C: subdivision_result

        public bool FromTessellatedNurbs;   // C: from_tessellated_nurbs
    }
}
