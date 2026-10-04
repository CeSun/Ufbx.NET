// OBJ/MTL loader state, ported from `ufbxi_obj_context` (ufbx.c:6399-6458) and the
// per-mesh helpers (ufbx.c:6356-6385).
//
// This is a `UfbxiContext` partial (append-only, per the S2 convention recorded in
// Parse/UfbxiContext.cs); it adds a single `Obj` field holding the OBJ module's own state so
// no shared file has to change. Every `uc->obj.*` access in ufbx.c maps to `uc.Obj.*`.
//
// ARRAY/BUFFER MAPPING (PORTING_NOTES.md #4): C's `ufbxi_buf tmp_*` stacks become `List<T>`;
// `num_items` is `Count`, `ufbxi_push` is `Add`, `ufbxi_pop(n)` is a tail `RemoveRange`.
using System;
using System.Collections.Generic;

namespace Ufbx
{
    // C: ufbxi_obj_index_range (ufbx.c:6356-6358). A class so the in-place
    // `range->min_ix = ...` updates of ufbxi_obj_parse_index (ufbx.c:17157-17158) need no
    // copy-back; the value is never compared by identity.
    internal sealed class UfbxiObjIndexRange
    {
        public ulong MinIx;   // C: uint64_t min_ix
        public ulong MaxIx;   // C: uint64_t max_ix
    }

    // C: ufbxi_obj_mesh (ufbx.c:6360-6374).
    internal sealed class UfbxiObjMesh
    {
        public int NumFaces;     // C: size_t num_faces
        public int NumIndices;   // C: size_t num_indices
        public readonly UfbxiObjIndexRange[] VertexRange = new UfbxiObjIndexRange[3]; // C: vertex_range[UFBXI_OBJ_NUM_ATTRIBS]

        public UfbxNode FbxNode; // C: ufbx_node *fbx_node
        public UfbxMesh FbxMesh; // C: ufbx_mesh *fbx_mesh

        public ulong FbxNodeId;  // C: uint64_t fbx_node_id
        public ulong FbxMeshId;  // C: uint64_t fbx_mesh_id

        public uint UsemtlBase;  // C: uint32_t usemtl_base

        public uint NumGroups;   // C: uint32_t num_groups

        public UfbxiObjMesh()
        {
            for (int i = 0; i < VertexRange.Length; i++) VertexRange[i] = new UfbxiObjIndexRange();
        }
    }

    // C: ufbxi_obj_group_entry (ufbx.c:6376-6380). Keyed by `name` (a pooled `const char*`);
    // the port keys on the pooled raw-byte string itself (UfbxiPtrIdTable id order).
    internal struct UfbxiObjGroupEntry
    {
        public string Name;     // C: const char *name
        public uint LocalId;    // C: uint32_t local_id
        public uint MeshId;     // C: uint32_t mesh_id
    }

    // C: ufbxi_obj_fast_indices (ufbx.c:6382-6385). C keeps a raw `uint64_t *indices` write
    // cursor into a 128-slot pre-reservation of `tmp_indices[attrib]`; the port always appends
    // to the List, so only `num_left` (the pre-reservation bookkeeping) is kept. It decides
    // when the next 128-slot chunk is pushed, which the List does not need, but the value is
    // still tracked so the "pop unused fast indices" step (ufbx.c:17508-17510) reads like C.
    internal sealed class UfbxiObjFastIndices
    {
        public int NumLeft;     // C: size_t num_left
    }

    // C: ufbx_face_group (ufbx.h) — { int32_t id; ufbx_string name; }.
    // (Model type UfbxFaceGroup is reused directly.)

    // C: ufbxi_obj_context (ufbx.c:6399-6458).
    internal sealed class UfbxiObjState
    {
        // Current line and tokens.
        // NOTE: `line` and `tokens` are not NULL-terminated nor UTF-8!
        // `line` is guaranteed to be terminated by a `\n`.
        public string Line;                        // C: ufbx_string line
        public readonly List<UfbxiObjToken> Tokens = new List<UfbxiObjToken>(); // C: ufbx_string *tokens
        public int TokensCap;                      // C: size_t tokens_cap
        public int NumTokens;                      // C: size_t num_tokens

        public readonly UfbxiObjFastIndices[] FastIndices = new UfbxiObjFastIndices[3]; // C: fast_indices[UFBXI_OBJ_NUM_ATTRIBS]

        public readonly ulong[] VertexCount = new ulong[4];          // C: vertex_count[UFBXI_OBJ_NUM_ATTRIBS_EXT]
        public readonly List<double>[] TmpVertices = new List<double>[4]; // C: tmp_vertices[UFBXI_OBJ_NUM_ATTRIBS_EXT]
        public readonly List<ulong>[] TmpIndices = new List<ulong>[4];    // C: tmp_indices[UFBXI_OBJ_NUM_ATTRIBS_EXT]
        public readonly List<bool> TmpColorValid = new List<bool>();      // C: tmp_color_valid
        public readonly List<UfbxFace> TmpFaces = new List<UfbxFace>();   // C: tmp_faces
        public readonly List<bool> TmpFaceSmoothing = new List<bool>();   // C: tmp_face_smoothing
        public readonly List<uint> TmpFaceGroup = new List<uint>();       // C: tmp_face_group
        public readonly List<UfbxFaceGroup> TmpFaceGroupInfos = new List<UfbxFaceGroup>(); // C: tmp_face_group_infos
        public readonly List<uint> TmpFaceMaterial = new List<uint>();    // C: tmp_face_material
        public readonly List<UfbxiObjMesh> TmpMeshes = new List<UfbxiObjMesh>(); // C: tmp_meshes
        public readonly List<UfbxProp> TmpProps = new List<UfbxProp>();   // C: tmp_props

        public UfbxiMap<UfbxiObjGroupEntry, ulong> GroupMap;   // C: ufbxi_map group_map

        public ulong ReadProgress;                 // C: size_t read_progress

        public UfbxiObjMesh Mesh;                  // C: ufbxi_obj_mesh *mesh

        public ulong UsemtlFbxId;                  // C: uint64_t usemtl_fbx_id
        public uint UsemtlIndex;                   // C: uint32_t usemtl_index

        public uint FaceMaterial;                  // C: uint32_t face_material

        public uint FaceGroup;                     // C: uint32_t face_group
        public bool HasFaceGroup;                  // C: bool has_face_group

        public bool FaceSmoothing;                 // C: bool face_smoothing
        public bool HasFaceSmoothing;              // C: bool has_face_smoothing

        public bool HasVertexColor;                // C: bool has_vertex_color
        public ulong MrgbVertexCount;              // C: size_t mrgb_vertex_count

        public bool Eof;                           // C: bool eof
        public bool Initialized;                   // C: bool initialized

        public byte[] MtllibRelativePath;          // C: ufbx_blob mtllib_relative_path

        public readonly List<UfbxMaterial> TmpMaterials = new List<UfbxMaterial>(); // C: ufbx_material **tmp_materials
        public int TmpMaterialsCap;                // C: size_t tmp_materials_cap

        public string Object = string.Empty;       // C: ufbx_string object
        public string Group = string.Empty;        // C: ufbx_string group
        public bool MaterialDirty;                 // C: bool material_dirty
        public bool ObjectDirty;                   // C: bool object_dirty
        public bool GroupDirty;                    // C: bool group_dirty
        public bool FaceGroupDirty;                // C: bool face_group_dirty

        public UfbxiObjState()
        {
            for (int i = 0; i < 3; i++) FastIndices[i] = new UfbxiObjFastIndices();
            for (int i = 0; i < 4; i++) {
                TmpVertices[i] = new List<double>();
                TmpIndices[i] = new List<ulong>();
            }
        }
    }

    // A `ufbx_string` token into `UfbxiObjState.Line` (offset + length instead of C's
    // `const char *data`). `ufbxi_obj_span_token` (ufbx.c:16982) and the index scanner
    // (ufbx.c:17109) both operate on the raw bytes of the line, so keeping offsets preserves
    // C's pointer arithmetic — including the inter-token whitespace a span covers.
    internal struct UfbxiObjToken
    {
        public int Offset;   // offset into Line; C: const char *data
        public int Length;   // C: size_t length
    }

    internal sealed partial class UfbxiContext
    {
        // C: ufbxi_obj_context obj (ufbx.c:6446ish) — the OBJ/MTL reader's private state.
        internal readonly UfbxiObjState Obj = new UfbxiObjState();
    }
}
