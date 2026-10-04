// S2 geometry/element readers, ported from ufbx.c:12662-14108.
// Owned by the S2-geometry porting agent (2026-10-03 wave).
//
// Covered C functions (ufbx.c line ranges in per-function comments):
//   ufbxi_fix_index / ufbxi_check_indices -> shared with S1 (UfbxiReadElement.FixIndex /
//     CheckIndices, Parse/ReadElement.cs) -- not duplicated here.
//   ufbxi_warn_polygon_mapping (12731), ufbxi_read_vertex_element (12737),
//   ufbxi_read_truncated_array (12924), uv/color/blend less + stable sorts (12958-13004),
//   ufbxi_read_shape (13006), ufbxi_read_synthetic_blend_shapes (13073),
//   ufbxi_process_indices (13135), ufbxi_patch_mesh_reals (13215), ufbxi_less_int32 (13242),
//   ufbxi_mesh_part_add_face (13251), ufbxi_assign_face_groups (13263),
//   ufbxi_update_face_groups (13395), ufbxi_read_mesh (13430), ufbxi_read_nurbs_topology
//   (13809), ufbxi_read_nurbs_curve (13821), ufbxi_read_nurbs_surface (13851),
//   ufbxi_read_transform_matrix (13960), ufbxi_read_line (13899), ufbxi_read_skin (13995),
//   ufbxi_read_skin_cluster (14027), ufbxi_read_blend_channel (14057).
//
// Representation notes:
//  - C aliases the parsed DOM array buffers into the scene (`mesh->vertices.data =
//    (ufbx_vec3*)arr->data`, ...). The port materializes typed arrays at each aliasing
//    site; materialization is a *copy*, which matches C exactly where C itself copies
//    (see the DOM rule below) and is unobservable where C mutates nothing.
//  - DOM/数组数据约定 (PORTING_NOTES.md): C's retained DOM blob ALIASES `arr->data`
//    (ufbx.c:10754), so in-place writes the readers make are visible through the DOM.
//    In-place write sites and their port handling:
//      * ufbxi_read_line (13914-13948): NO copy -- `line->point_indices.data` is the DOM
//        `PointsIndex` buffer; every store lands in the raw bytes (StorePointIndex below
//        mirrors each store into `arr.Data` and the retained DOM blob).
//      * ufbxi_check_indices at ufbx.c:12795 with `owns_indices == true`: out-of-bounds
//        fixes land in the DOM index buffer when the buffer was not truncated
//        (SyncFixedDomIndices below).
//      * ufbxi_read_shape (13050-13068): the unsorted-shape sort writes back into the DOM
//        `Indexes`/`Vertices`/`Normals` buffers (StoreU32Raw/StoreVec3Raw below).
//      * ufbxi_read_mesh (13463-13466) and legacy mesh (16196-16202): C *copies* first
//        (`ufbxi_push_copy`) -- the port's materialized array is that copy, DOM untouched.
//  - Sentinel index buffers (ufbx.c:12658-12660): `ufbxi_sentinel_index_zero` /
//    `_consecutive` are stored by pointer identity into list `.data` fields and patched to
//    real shared buffers by `ufbxi_patch_index_pointer` (ufbx.c:19284) during finalize
//    (ufbx.c:22029). The port keeps the sentinel VALUES and discriminates by reference
//    identity (`ReferenceEquals`); the implied list count for a sentinel is the reader's
//    count (mesh.NumFaces / mesh.NumIndices / part counts).

using System;
using System.Buffers.Binary;

namespace Ufbx.NET
{
    internal static class UfbxiGeometry
    {
        // ------------------------------------------------------------------
        // Sentinel index buffers (ufbx.c:12656-12660)
        // ------------------------------------------------------------------

        // C: static ufbx_real ufbxi_zero_element[8] -- only reachable through a dead branch
        // (`num_elems == 0` returns early at ufbx.c:12756), kept as a comment.

        // C: ufbxi_sentinel_index_zero (ufbx.c:12659) / ufbxi_sentinel_index_consecutive
        // (ufbx.c:12660). Reference identity is the discriminator; values match C's.
        internal static readonly uint[] SentinelIndexZero = new uint[] { 100000000 };
        internal static readonly uint[] SentinelIndexConsecutive = new uint[] { 123456789 };

        // C: UFBXI_FACE_GROUP_HASH_BITS (ufbx.c:59; not redefined for this build).
        const int FaceGroupHashBits = 8;

        // ------------------------------------------------------------------
        // Raw DOM array access helpers
        // ------------------------------------------------------------------

        // The retained DOM blob of `arrayNode`'s value, or null when the node was not
        // retained (no retain_dom). C: `val->value_blob.data = arr->data` (ufbx.c:10754).
        static byte[] RetainedDomBlob(UfbxiContext uc, UfbxiNode arrayNode)
        {
            if (!uc.Opts.RetainDom || arrayNode == null) return null;
            UfbxDomNode dom = uc.FindDomNode(arrayNode);
            if (dom == null || dom.Values == null || dom.Values.Length != 1) return null;
            return dom.Values[0].ValueBlob;
        }

        static void WriteDoubleLE(byte[] b, int o, double v)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), unchecked((ulong)UfbxBitUtil.BitsOf(v)));
        }

        // C: `line->point_indices.data[i] = ix` (ufbx.c:13944/13946) -- the model array AND
        // the DOM array bytes (which the retained blob aliases) all carry the write.
        static void StorePointIndex(UfbxiValueArray arr, byte[] domBlob, uint[] values, int index, uint value)
        {
            values[index] = value;
            BinaryPrimitives.WriteUInt32LittleEndian(arr.Data.AsSpan(arr.Offset + index * 4), value);
            if (domBlob != null) {
                BinaryPrimitives.WriteUInt32LittleEndian(domBlob.AsSpan(index * 4), value);
            }
        }

        // C: read_shape writes a sorted `uint32_t` back into `indices->data` (ufbx.c:13063).
        static void StoreU32Raw(UfbxiValueArray arr, byte[] domBlob, int index, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(arr.Data.AsSpan(arr.Offset + index * 4), value);
            if (domBlob != null) {
                BinaryPrimitives.WriteUInt32LittleEndian(domBlob.AsSpan(index * 4), value);
            }
        }

        // C: read_shape writes a sorted `ufbx_vec3` (24 raw bytes) back into the 'r' array
        // (ufbx.c:13064-13065).
        static void StoreVec3Raw(UfbxiValueArray arr, byte[] domBlob, int index, UfbxVec3 v)
        {
            int bo = arr.Offset + index * 24;
            WriteDoubleLE(arr.Data, bo + 0, v.X);
            WriteDoubleLE(arr.Data, bo + 8, v.Y);
            WriteDoubleLE(arr.Data, bo + 16, v.Z);
            if (domBlob != null) {
                int bb = index * 24;
                WriteDoubleLE(domBlob, bb + 0, v.X);
                WriteDoubleLE(domBlob, bb + 8, v.Y);
                WriteDoubleLE(domBlob, bb + 16, v.Z);
            }
        }

        // C: ufbxi_check_indices(..., owns_indices=true, ...) at ufbx.c:12795 fixed
        // out-of-bounds indices *in place* in `indices->data` -- the DOM array buffer --
        // whenever the buffer was not truncated (truncation reallocates, ufbx.c:12692-12704).
        // `buffer` is the port's materialization of `indicesArr`; `result` is what
        // UfbxiReadElement.CheckIndices returned. When they are the same instance the C fix
        // landed in the DOM bytes; mirror the delta into `arr.Data` and the retained blob.
        static void SyncFixedDomIndices(UfbxiContext uc, UfbxiNode indicesNode, UfbxiValueArray indicesArr,
            uint[] buffer, uint[] result)
        {
            if (!ReferenceEquals(buffer, result)) return;   // C reallocated => DOM untouched
            byte[] domBlob = RetainedDomBlob(uc, indicesNode);
            int count = indicesArr.Size < result.Length ? indicesArr.Size : result.Length;
            for (int i = 0; i < count; i++) {
                int bo = indicesArr.Offset + i * 4;
                uint cur = BinaryPrimitives.ReadUInt32LittleEndian(indicesArr.Data.AsSpan(bo));
                if (cur != result[i]) {
                    BinaryPrimitives.WriteUInt32LittleEndian(indicesArr.Data.AsSpan(bo), result[i]);
                    if (domBlob != null) {
                        BinaryPrimitives.WriteUInt32LittleEndian(domBlob.AsSpan(i * 4), result[i]);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Array materialization helpers (C: typed aliasing of `arr->data`)
        // ------------------------------------------------------------------

        static uint[] MaterializeU32(UfbxiValueArray arr, int count)
        {
            uint[] data = new uint[count];
            for (int i = 0; i < count; i++) {
                data[i] = BinaryPrimitives.ReadUInt32LittleEndian(arr.Data.AsSpan(arr.Offset + i * 4));
            }
            return data;
        }

        static double[] MaterializeReals(UfbxiValueArray arr, int count)
        {
            double[] data = new double[count];
            for (int i = 0; i < count; i++) data[i] = arr.GetDouble(i);
            return data;
        }

        static UfbxVec2[] MaterializeVec2s(UfbxiValueArray arr, int count)
        {
            UfbxVec2[] data = new UfbxVec2[count];
            for (int i = 0; i < count; i++) {
                data[i] = new UfbxVec2(arr.GetDouble(i * 2 + 0), arr.GetDouble(i * 2 + 1));
            }
            return data;
        }

        static UfbxVec3[] MaterializeVec3s(UfbxiValueArray arr, int count)
        {
            UfbxVec3[] data = new UfbxVec3[count];
            for (int i = 0; i < count; i++) {
                data[i] = new UfbxVec3(arr.GetDouble(i * 3 + 0), arr.GetDouble(i * 3 + 1), arr.GetDouble(i * 3 + 2));
            }
            return data;
        }

        static UfbxVec4[] MaterializeVec4s(UfbxiValueArray arr, int count)
        {
            UfbxVec4[] data = new UfbxVec4[count];
            for (int i = 0; i < count; i++) {
                data[i] = new UfbxVec4(arr.GetDouble(i * 4 + 0), arr.GetDouble(i * 4 + 1),
                    arr.GetDouble(i * 4 + 2), arr.GetDouble(i * 4 + 3));
            }
            return data;
        }

        // ------------------------------------------------------------------
        // ufbxi_process_indices (ufbx.c:13135-13213) -- pinned signature
        // ------------------------------------------------------------------

        // Called by ufbxi_read_mesh (13525) and ufbxi_read_legacy_mesh (16224, S2-objects
        // agent). `indexData` is the mutable polygon-vertex index buffer (~ix = polygon end);
        // the function rewrites end markers in place, exactly like C.
        internal static void ProcessIndices(UfbxiContext uc, UfbxMesh mesh, uint[] indexData)
        {
            int numIndices = mesh.NumIndices;

            // Count the number of faces and allocate the index list
            // Indices less than zero (~actual_index) ends a polygon
            int numTotalFaces = 0;
            for (int i = 0; i < numIndices; i++) {
                if ((int)indexData[i] < 0) numTotalFaces++;
            }
            UfbxFace[] faces = new UfbxFace[numTotalFaces];

            int numTriangles = 0;
            int maxFaceTriangles = 0;
            int[] numBadFaces = new int[3];

            int faceCount = 0;
            int faceBegin = 0;
            for (int i = 0; i < numIndices; i++) {
                uint ix = indexData[i];
                // Un-negate final indices of polygons
                if ((int)ix < 0) {
                    ix = ~ix;
                    indexData[i] = ix;
                    uint numFaceIndices = (uint)(i - faceBegin + 1);
                    faces[faceCount].IndexBegin = (uint)faceBegin;
                    faces[faceCount].NumIndices = numFaceIndices;
                    if (numFaceIndices >= 3) {
                        numTriangles += (int)numFaceIndices - 2;
                        if (maxFaceTriangles < (int)numFaceIndices - 2) maxFaceTriangles = (int)numFaceIndices - 2;
                    } else {
                        numBadFaces[numFaceIndices]++;
                    }
                    faceCount++;
                    faceBegin = i + 1;
                }
                UfbxiFail.CheckNoDesc((ulong)ix < (ulong)mesh.NumVertices, "(size_t)ix < mesh->num_vertices");
            }

            mesh.Faces = faces;
            mesh.VertexPosition.Indices = indexData;
            mesh.NumFaces = faceCount;
            mesh.NumTriangles = numTriangles;
            mesh.MaxFaceTriangles = maxFaceTriangles;
            mesh.NumEmptyFaces = numBadFaces[0];
            mesh.NumPointFaces = numBadFaces[1];
            mesh.NumLineFaces = numBadFaces[2];

            mesh.VertexFirstIndex = new uint[mesh.NumVertices];
            for (int i = 0; i < mesh.NumVertices; i++) {
                mesh.VertexFirstIndex[i] = UfbxConstants.NoIndex;
            }

            {
                int numVertices = mesh.NumVertices;
                uint[] vertexIndices = mesh.VertexIndices;   // C: mesh->vertex_indices.data == indexData
                uint[] vertexFirstIndex = mesh.VertexFirstIndex;
                for (int ix = 0; ix < numIndices; ix++) {
                    uint vx = vertexIndices[ix];
                    if (vx < (uint)numVertices) {
                        if (vertexFirstIndex[vx] == UfbxConstants.NoIndex) {
                            vertexFirstIndex[vx] = (uint)ix;
                        }
                    } else {
                        UfbxiReadElement.FixIndex(uc, vertexIndices, ix, vx, (ulong)numVertices);
                    }
                }
            }

            // HACK(consecutive-faces): Prepare for finalize to re-use a consecutive/zero
            // index buffer for face materials..
            if (mesh.NumFaces > uc.MaxZeroIndices) uc.MaxZeroIndices = mesh.NumFaces;
            if (mesh.NumFaces > uc.MaxConsecutiveIndices) uc.MaxConsecutiveIndices = mesh.NumFaces;
        }

        // ------------------------------------------------------------------
        // ufbxi_read_truncated_array (ufbx.c:12924-12956)
        // ------------------------------------------------------------------

        // Shared core: returns the matched array and `raw` bytes holding exactly `size`
        // elements (`elemSize` each), extended with the last element / zeros when truncated.
        // Returns null (and leaves `raw` null) when the array is missing, after warning.
        static UfbxiValueArray TruncatedArrayCore(UfbxiContext uc, UfbxiNode node, string name, char fmt,
            int size, int elemSize, out byte[] raw)
        {
            raw = null;
            UfbxiValueArray arr = node.FindArray(name, fmt);
            if (arr == null) {
                UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.MissingGeometryData, UfbxiWarnings.NoElementId,
                    "Missing geometry data: %s", new UfbxiVaList().AddStr(name));
                return null;
            }

            if (arr.Size < size) {
                UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.TruncatedArray, UfbxiWarnings.NoElementId,
                    "Truncated array: %s", new UfbxiVaList().AddStr(name));

                byte[] data = new byte[size * elemSize];
                Array.Copy(arr.Data, arr.Offset, data, 0, arr.Size * elemSize);
                // Extend the array with the last element if possible
                if (arr.Size > 0) {
                    int firstElem = arr.Offset + (arr.Size - 1) * elemSize;
                    for (int i = arr.Size; i < size; i++) {
                        Array.Copy(arr.Data, firstElem, data, i * elemSize, elemSize);
                    }
                }   // else: `new byte[]` is already zeroed (C: memset)
                raw = data;
            } else {
                byte[] data = new byte[size * elemSize];
                Array.Copy(arr.Data, arr.Offset, data, 0, size * elemSize);
                raw = data;
            }
            return arr;
        }

        // C: ufbxi_read_truncated_array(..., 'i', ...) -- pinned signature. Called by
        // ufbxi_read_legacy_mesh ufbx.c:16271 (face_material, name "Materials").
        // Returns the (possibly truncated-padded) array of exactly `size` elements.
        // Missing array => warn MISSING_GEOMETRY_DATA and return null (C returns the
        // unmodified NULL data with count kept as-is; the caller leaves the list empty).
        internal static uint[] ReadTruncatedArrayI32(UfbxiContext uc, UfbxiNode node, string name, uint size)
        {
            byte[] raw;
            if (TruncatedArrayCore(uc, node, name, 'i', (int)size, UfbxiArrayType.SizeOf('i'), out raw) == null) {
                return null;
            }
            uint[] data = new uint[size];
            for (int i = 0; i < (int)size; i++) {
                data[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
            }
            return data;
        }

        // C: ufbxi_read_truncated_array(..., 'r', ...) (edge_crease, ufbx.c:13604).
        static double[] ReadTruncatedArrayReal(UfbxiContext uc, UfbxiNode node, string name, int size)
        {
            byte[] raw;
            if (TruncatedArrayCore(uc, node, name, 'r', size, UfbxiArrayType.SizeOf('r'), out raw) == null) {
                return null;
            }
            double[] data = new double[size];
            for (int i = 0; i < size; i++) {
                ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(i * 8));
                data[i] = UfbxBitUtil.FromInt64(unchecked((long)bits));
            }
            return data;
        }

        // C: ufbxi_read_truncated_array(..., 'b', ...) (smoothing/visibility/hole).
        static bool[] ReadTruncatedArrayBool(UfbxiContext uc, UfbxiNode node, string name, int size)
        {
            byte[] raw;
            if (TruncatedArrayCore(uc, node, name, 'b', size, UfbxiArrayType.SizeOf('b'), out raw) == null) {
                return null;
            }
            bool[] data = new bool[size];
            for (int i = 0; i < size; i++) data[i] = raw[i] != 0;
            return data;
        }

        // ------------------------------------------------------------------
        // Sort comparison functions (ufbx.c:12958-12997, 13242-13247)
        // ------------------------------------------------------------------

        // C: ufbxi_uv_set_less (ufbx.c:12958-12963)
        static bool UvSetLess(object user, UfbxUvSet a, UfbxUvSet b) => a.Index < b.Index;

        // C: ufbxi_color_set_less (ufbx.c:12965-12970)
        static bool ColorSetLess(object user, UfbxColorSet a, UfbxColorSet b) => a.Index < b.Index;

        // C: ufbxi_blend_offset_less (ufbx.c:12992-12997)
        static bool BlendOffsetLess(object user, UfbxiBlendOffset a, UfbxiBlendOffset b) => a.Vertex < b.Vertex;

        // C: ufbxi_less_int32 (ufbx.c:13242-13247) -- SIGNED comparison view of the ids.
        static bool LessInt32(object user, uint a, uint b) => unchecked((int)a) < unchecked((int)b);

        // C: typedef struct ufbxi_blend_offset (ufbx.c:12986-12990)
        struct UfbxiBlendOffset
        {
            public uint Vertex;
            public UfbxVec3 PositionOffset;
            public UfbxVec3 NormalOffset;
        }

        // C: ufbxi_sort_uv_sets (ufbx.c:12972-12977) -- stable sort, insertion block 32.
        static void SortUvSets(UfbxUvSet[] sets, int count)
        {
            UfbxUvSet[] tmp = new UfbxUvSet[count];
            UfbxiSort.StableSort(32, sets, tmp, count, UvSetLess, null);
        }

        // C: ufbxi_sort_color_sets (ufbx.c:12979-12984)
        static void SortColorSets(UfbxColorSet[] sets, int count)
        {
            UfbxColorSet[] tmp = new UfbxColorSet[count];
            UfbxiSort.StableSort(32, sets, tmp, count, ColorSetLess, null);
        }

        // ------------------------------------------------------------------
        // ufbxi_warn_polygon_mapping (ufbx.c:12731-12735)
        // ------------------------------------------------------------------

        static void WarnPolygonMapping(UfbxiContext uc, string dataName, string mapping)
        {
            UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.MissingPolygonMapping, UfbxiWarnings.NoElementId,
                "Ignoring geometry '%s' with bad mapping mode '%s'",
                new UfbxiVaList().AddStr(dataName).AddStr(mapping));
        }

        // ------------------------------------------------------------------
        // Mapping / value find helpers (C: ufbxi_find_val1 spelled out per format)
        // ------------------------------------------------------------------

        // C: ufbxi_find_val1(node, name, "C", &mapping) -- sanitized 'C' string.
        static bool FindMappingSanitizedC(UfbxiNode node, ref string mapping)
        {
            UfbxiNode child = node.FindChild(UfbxiStrings.MappingInformationType);
            if (child == null) return false;
            string value;
            if (!child.GetValS(0, out value)) return false;
            mapping = value;
            return true;
        }

        // C: ufbxi_find_val1(node, name, "c", &mapping) -- raw 'c' string.
        static bool FindMappingRawC(UfbxiNode node, ref string mapping)
        {
            UfbxiNode child = node.FindChild(UfbxiStrings.MappingInformationType);
            if (child == null) return false;
            string value;
            if (!child.GetValRawString(0, out value)) return false;
            mapping = value;
            return true;
        }

        // C: ufbxi_find_val2(node, name, "II", &a, &b) (ufbx.c:~7845)
        static bool FindVal2I(UfbxiNode node, string name, out int a, out int b)
        {
            a = 0; b = 0;
            UfbxiNode child = node.FindChild(name);
            if (child == null) return false;
            return child.GetValI(0, out a) && child.GetValI(1, out b);
        }

        // C: ufbxi_find_val2(node, name, "ZZ", &a, &b) -- size_t values.
        static bool FindVal2Z64(UfbxiNode node, string name, out long a, out long b)
        {
            a = 0; b = 0;
            UfbxiNode child = node.FindChild(name);
            if (child == null) return false;
            return child.GetValZ64(0, out a) && child.GetValZ64(1, out b);
        }

        // C: ufbxi_find_val2(node, name, "CC", &a, &b) -- sanitized strings.
        static bool FindVal2S(UfbxiNode node, string name, out string a, out string b)
        {
            a = null; b = null;
            UfbxiNode child = node.FindChild(name);
            if (child == null) return false;
            return child.GetValS(0, out a) && child.GetValS(1, out b);
        }

        // ------------------------------------------------------------------
        // ufbxi_read_vertex_element (ufbx.c:12737-12922)
        // ------------------------------------------------------------------

        // The C function writes through a `ufbx_vertex_attrib*` cast of four different
        // concrete attribute types; the port splits the shared logic into `Core` (returning
        // the computed fields) and thin typed wrappers. Core result codes:
        const int AttribUntouched = 0;  // C: `return 1` before any write
        const int AttribZeroed = 1;     // C: memset(attrib, 0, ...) + warn + `return 1`
        const int AttribOk = 2;         // C: fell through to the end

        static int ReadVertexElementCore(UfbxiContext uc, UfbxMesh mesh, UfbxiNode node,
            string dataName, string indexName, string wName, int numComponents,
            out Array values, out uint[] indices, out bool uniquePerVertex, out double[] valuesW)
        {
            values = null;
            indices = null;
            uniquePerVertex = false;
            valuesW = null;

            UfbxiValueArray data = node.FindArray(dataName, 'r');

            UfbxiNode indicesNode = node.FindChild(indexName);
            UfbxiValueArray indicesArr = indicesNode != null ? indicesNode.GetArray('i') : null;

            if (!uc.Opts.Strict && data == null) return AttribUntouched;

            UfbxiFail.CheckNoDesc(data != null, "data");
            UfbxiFail.CheckNoDesc(data.Size % numComponents == 0, "data->size % num_components == 0");

            int numElems = data.Size / numComponents;

            // HACK: If there's no elements at all keep the attribute as NULL
            // (The `ufbxi_zero_element + 4` branch at ufbx.c:12776 is unreachable.)
            if (numElems == 0) return AttribUntouched;

            UfbxiFail.CheckNoDesc(numElems > 0 && numElems < int.MaxValue, "num_elems > 0 && num_elems < INT32_MAX");

            string mapping = string.Empty;
            FindMappingSanitizedC(node, ref mapping);

            // Data array is always used as-is (ufbx.c:12768-12777)
            switch (numComponents) {
            case 1: values = MaterializeReals(data, numElems); break;
            case 2: values = MaterializeVec2s(data, numElems); break;
            case 3: values = MaterializeVec3s(data, numElems); break;
            default: values = MaterializeVec4s(data, numElems); break;
            }

            // HACK: Some old exporters seem to use ByPolygon to mean ByPolygonVertex,
            // it should be quite safe to remap this
            if (mapping == UfbxiStrings.ByPolygon) {
                int remapNumIndices = indicesArr != null ? indicesArr.Size : numElems;
                if (remapNumIndices == mesh.NumIndices) {
                    mapping = UfbxiStrings.ByPolygonVertex;
                }
            }

            if (indicesArr != null) {
                int numIndices = indicesArr.Size;
                uint[] indexData = MaterializeU32(indicesArr, numIndices);

                if (mapping == UfbxiStrings.ByPolygonVertex) {
                    // Indexed by polygon vertex: We can use the provided indices directly.
                    // C passes owns_indices=true, so out-of-bounds fixes land in the DOM
                    // buffer (see SyncFixedDomIndices).
                    uint[] result = UfbxiReadElement.CheckIndices(uc, indexData, true, numIndices, mesh.NumIndices, numElems);
                    indices = result;
                    SyncFixedDomIndices(uc, indicesNode, indicesArr, indexData, result);

                } else if (mapping == UfbxiStrings.ByVertex || mapping == UfbxiStrings.ByVertice) {
                    // Indexed by vertex: Follow through the position index mapping to get the final indices.
                    uint[] newIndexData = new uint[mesh.NumIndices];
                    uint[] vertIx = mesh.VertexIndices;
                    for (int i = 0; i < mesh.NumIndices; i++) {
                        uint ix = vertIx[i];
                        if (ix < (uint)numIndices) {
                            newIndexData[i] = indexData[ix];
                        } else {
                            UfbxiReadElement.FixIndex(uc, newIndexData, i, ix, (ulong)numElems);
                        }
                    }
                    indices = UfbxiReadElement.CheckIndices(uc, newIndexData, true, mesh.NumIndices, mesh.NumIndices, numElems);
                    uniquePerVertex = true;

                } else if (mapping == UfbxiStrings.ByPolygon) {
                    // Indexed by polygon: Generate new indices based on polygons
                    uint[] newIndexData = new uint[mesh.NumIndices];
                    int numFaces = mesh.NumFaces;
                    for (int faceIx = 0; faceIx < numFaces; faceIx++) {
                        UfbxFace face = mesh.Faces[faceIx];
                        uint index = UfbxConstants.NoIndex;
                        if ((uint)faceIx < (uint)numIndices) {
                            index = indexData[faceIx];
                        }
                        if (index >= (uint)numElems) {
                            uint[] one = new uint[1] { index };
                            UfbxiReadElement.FixIndex(uc, one, 0, index, (ulong)numElems);
                            index = one[0];
                        }
                        for (int i = 0; i < (int)face.NumIndices; i++) {
                            newIndexData[(int)face.IndexBegin + i] = index;
                        }
                    }
                    indices = newIndexData;

                } else if (mapping == UfbxiStrings.AllSame) {
                    // Indexed by all same: Just use the shared zero index buffer for this.
                    if (mesh.NumIndices > uc.MaxZeroIndices) uc.MaxZeroIndices = mesh.NumIndices;
                    indices = SentinelIndexZero;
                    uniquePerVertex = true;

                } else {
                    // C: memset(attrib, 0, sizeof(ufbx_vertex_attrib))
                    WarnPolygonMapping(uc, dataName, mapping);
                    return AttribZeroed;
                }

            } else {

                if (mapping == UfbxiStrings.ByPolygonVertex) {
                    // Direct by polygon index: Use shared consecutive array if there's enough
                    // elements, otherwise use a unique truncated consecutive index array.
                    if (numElems >= mesh.NumIndices) {
                        if (mesh.NumIndices > uc.MaxConsecutiveIndices) uc.MaxConsecutiveIndices = mesh.NumIndices;
                        indices = SentinelIndexConsecutive;
                    } else {
                        uint[] indexData = new uint[mesh.NumIndices];
                        for (int i = 0; i < mesh.NumIndices; i++) {
                            indexData[i] = (uint)i;
                        }
                        indices = UfbxiReadElement.CheckIndices(uc, indexData, true, mesh.NumIndices, mesh.NumIndices, numElems);
                    }

                } else if (mapping == UfbxiStrings.ByVertex || mapping == UfbxiStrings.ByVertice) {
                    // Direct by vertex: We can re-use the position indices..
                    indices = UfbxiReadElement.CheckIndices(uc, mesh.VertexPosition.Indices, false,
                        mesh.NumIndices, mesh.NumIndices, numElems);
                    uniquePerVertex = true;

                } else if (mapping == UfbxiStrings.ByPolygon) {
                    // Direct by polygon: Generate new indices based on polygons
                    uint[] newIndexData = new uint[mesh.NumIndices];
                    uint numFaces = unchecked((uint)mesh.NumFaces);
                    for (uint faceIx = 0; faceIx < numFaces; faceIx++) {
                        UfbxFace face = mesh.Faces[faceIx];
                        for (int i = 0; i < (int)face.NumIndices; i++) {
                            newIndexData[(int)face.IndexBegin + i] = faceIx;
                        }
                    }
                    indices = UfbxiReadElement.CheckIndices(uc, newIndexData, true, mesh.NumIndices, mesh.NumIndices, numElems);

                } else if (mapping == UfbxiStrings.AllSame) {
                    // Direct by all same: This cannot fail as the index list is just zero.
                    if (mesh.NumIndices > uc.MaxZeroIndices) uc.MaxZeroIndices = mesh.NumIndices;
                    indices = SentinelIndexZero;
                    uniquePerVertex = true;

                } else {
                    // C: memset(attrib, 0, sizeof(ufbx_vertex_attrib))
                    WarnPolygonMapping(uc, dataName, mapping);
                    return AttribZeroed;
                }
            }

            // C: ufbx.c:12763 assigns `attrib->indices.count = mesh->num_indices` once, *before*
            // the mapping branches, so an attribute whose raw FBX index array is longer exposes
            // only the first `num_indices` entries (`ufbxi_check_indices` still validates all
            // `indices->size` of them). The port's array length is that count, so trim it here.
            if (indices != null && indices.Length > mesh.NumIndices &&
                !ReferenceEquals(indices, SentinelIndexZero) &&
                !ReferenceEquals(indices, SentinelIndexConsecutive)) {
                uint[] exposed = new uint[mesh.NumIndices];
                Array.Copy(indices, 0, exposed, 0, mesh.NumIndices);
                indices = exposed;
            }

            if (uc.Opts.RetainVertexAttribW && wName != null) {
                UfbxiValueArray wData = node.FindArray(wName, 'r');
                if (wData != null) {
                    if (wData.Size == numElems) {
                        valuesW = MaterializeReals(wData, wData.Size);
                    } else {
                        UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.BadVertexWAttribute, UfbxiWarnings.NoElementId,
                            "Bad W array size %s=%zu, %s=%zu", new UfbxiVaList()
                                .AddStr(wName)
                                .AddSizeT((ulong)wData.Size)
                                .AddStr(dataName)
                                .AddSizeT((ulong)numElems));
                    }
                }
            }

            return AttribOk;
        }

        // C: the `(ufbx_vertex_attrib*)&mesh->vertex_normal` cast family -- one wrapper per
        // concrete attribute type. The wrappers assign the fields in the order C's success
        // path leaves them (exists / indices / values / unique_per_vertex / values_w); on
        // `AttribZeroed` the whole attribute is reset (C: memset).

        static void ReadVertexElementVec3(UfbxiContext uc, UfbxMesh mesh, UfbxiNode node,
            ref UfbxVertexVec3 attrib, string dataName, string indexName, string wName)
        {
            Array values; uint[] indices; bool uniquePerVertex; double[] valuesW;
            int result = ReadVertexElementCore(uc, mesh, node, dataName, indexName, wName, 3,
                out values, out indices, out uniquePerVertex, out valuesW);
            if (result == AttribZeroed) { attrib = default(UfbxVertexVec3); return; }
            if (result == AttribUntouched) return;
            attrib.Exists = true;
            attrib.Values = (UfbxVec3[])values;
            attrib.Indices = indices;
            attrib.UniquePerVertex = uniquePerVertex;
            attrib.ValuesW = valuesW;
        }

        // NOTE: `internal` because ufbxi_read_legacy_mesh (Parse/Legacy.cs, the S2-objects
        // module) calls the vec2 variant for `GeometryUVInfo`, exactly like ufbx.c:16247.
        internal static void ReadVertexElementVec2(UfbxiContext uc, UfbxMesh mesh, UfbxiNode node,
            ref UfbxVertexVec2 attrib, string dataName, string indexName, string wName)
        {            Array values; uint[] indices; bool uniquePerVertex; double[] valuesW;
            int result = ReadVertexElementCore(uc, mesh, node, dataName, indexName, wName, 2,
                out values, out indices, out uniquePerVertex, out valuesW);
            if (result == AttribZeroed) { attrib = default(UfbxVertexVec2); return; }
            if (result == AttribUntouched) return;
            attrib.Exists = true;
            attrib.Values = (UfbxVec2[])values;
            attrib.Indices = indices;
            attrib.UniquePerVertex = uniquePerVertex;
            attrib.ValuesW = valuesW;
        }

        static void ReadVertexElementVec4(UfbxiContext uc, UfbxMesh mesh, UfbxiNode node,
            ref UfbxVertexVec4 attrib, string dataName, string indexName, string wName)
        {
            Array values; uint[] indices; bool uniquePerVertex; double[] valuesW;
            int result = ReadVertexElementCore(uc, mesh, node, dataName, indexName, wName, 4,
                out values, out indices, out uniquePerVertex, out valuesW);
            if (result == AttribZeroed) { attrib = default(UfbxVertexVec4); return; }
            if (result == AttribUntouched) return;
            attrib.Exists = true;
            attrib.Values = (UfbxVec4[])values;
            attrib.Indices = indices;
            attrib.UniquePerVertex = uniquePerVertex;
            attrib.ValuesW = valuesW;
        }

        static void ReadVertexElementReal(UfbxiContext uc, UfbxMesh mesh, UfbxiNode node,
            ref UfbxVertexReal attrib, string dataName, string indexName, string wName)
        {
            Array values; uint[] indices; bool uniquePerVertex; double[] valuesW;
            int result = ReadVertexElementCore(uc, mesh, node, dataName, indexName, wName, 1,
                out values, out indices, out uniquePerVertex, out valuesW);
            if (result == AttribZeroed) { attrib = default(UfbxVertexReal); return; }
            if (result == AttribUntouched) return;
            attrib.Exists = true;
            attrib.Values = (double[])values;
            attrib.Indices = indices;
            attrib.UniquePerVertex = uniquePerVertex;
            attrib.ValuesW = valuesW;
        }

        // C: typedef struct ufbxi_tangent_layer (ufbx.c:12651-12654)
        struct UfbxiTangentLayer
        {
            public UfbxVertexVec3 Elem;
            public uint Index;
        }

        // ------------------------------------------------------------------
        // ufbxi_read_shape (ufbx.c:13006-13071)
        // ------------------------------------------------------------------

        internal static void ReadShape(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxiNode nodeVertices = node.FindChild(UfbxiStrings.Vertices);
            UfbxiNode nodeIndices = node.FindChild(UfbxiStrings.Indexes);
            UfbxiNode nodeNormals = node.FindChild(UfbxiStrings.Normals);
            if (nodeVertices == null || nodeIndices == null) return;

            UfbxBlendShape shape = UfbxiReadElement.PushElement<UfbxBlendShape>(uc, info, UfbxElementType.BlendShape);

            if (uc.Opts.IgnoreGeometry) return;

            UfbxiValueArray vertices = nodeVertices.GetArray('r');
            UfbxiValueArray indices = nodeIndices.GetArray('i');

            UfbxiFail.CheckNoDesc(vertices != null && indices != null, "vertices && indices");
            UfbxiFail.CheckNoDesc(vertices.Size % 3 == 0, "vertices->size % 3 == 0");
            UfbxiFail.CheckNoDesc(indices.Size == vertices.Size / 3, "indices->size == vertices->size / 3");

            int numOffsets = indices.Size;
            uint[] offsetVertices = MaterializeU32(indices, numOffsets);
            UfbxVec3[] positionOffsets = MaterializeVec3s(vertices, numOffsets);

            shape.NumOffsets = numOffsets;
            shape.OffsetVertices = offsetVertices;
            shape.PositionOffsets = positionOffsets;

            UfbxVec3[] normalOffsets = null;
            UfbxiValueArray normals = null;
            if (nodeNormals != null) {
                normals = nodeNormals.GetArray('r');
                UfbxiFail.CheckNoDesc(normals != null && normals.Size == vertices.Size,
                    "normals && normals->size == vertices->size");
                normalOffsets = MaterializeVec3s(normals, numOffsets);
                shape.NormalOffsets = normalOffsets;
            }

            // Sort the blend shape vertices only if absolutely necessary
            bool sorted = true;
            for (int i = 1; i < numOffsets; i++) {
                if (offsetVertices[i - 1] > offsetVertices[i]) {
                    sorted = false;
                    break;
                }
            }

            if (!sorted) {
                // C: the sort copy lives on uc->tmp_stack; the writes back into
                // `shape->offset_vertices.data` / `position_offsets` / `normal_offsets` land
                // in the DOM array buffers (no copy there!), so mirror them raw.
                byte[] indicesBlob = RetainedDomBlob(uc, nodeIndices);
                byte[] verticesBlob = RetainedDomBlob(uc, nodeVertices);
                byte[] normalsBlob = nodeNormals != null ? RetainedDomBlob(uc, nodeNormals) : null;

                UfbxiBlendOffset[] offsets = new UfbxiBlendOffset[numOffsets];
                UfbxiBlendOffset[] offsetsTmp = new UfbxiBlendOffset[numOffsets];

                for (int i = 0; i < numOffsets; i++) {
                    offsets[i].Vertex = shape.OffsetVertices[i];
                    offsets[i].PositionOffset = shape.PositionOffsets[i];
                    if (nodeNormals != null) offsets[i].NormalOffset = shape.NormalOffsets[i];
                }

                // C: ufbxi_stable_sort(sizeof(ufbxi_blend_offset), 16, ...) (ufbx.c:13002)
                UfbxiSort.StableSort(16, offsets, offsetsTmp, numOffsets, BlendOffsetLess, null);

                for (int i = 0; i < numOffsets; i++) {
                    uint vertex = offsets[i].Vertex;
                    UfbxVec3 position = offsets[i].PositionOffset;
                    shape.OffsetVertices[i] = vertex;
                    StoreU32Raw(indices, indicesBlob, i, vertex);
                    shape.PositionOffsets[i] = position;
                    StoreVec3Raw(vertices, verticesBlob, i, position);
                    if (nodeNormals != null) {
                        UfbxVec3 normal = offsets[i].NormalOffset;
                        shape.NormalOffsets[i] = normal;
                        StoreVec3Raw(normals, normalsBlob, i, normal);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_read_synthetic_blend_shapes (ufbx.c:13073-13133)
        // ------------------------------------------------------------------

        internal static void ReadSyntheticBlendShapes(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxBlendDeformer deformer = null;
            ulong deformerFbxId = 0;

            for (int ci = 0; ci < node.NumChildren; ci++) {
                UfbxiNode n = node.Children[ci];
                if (n.Name != UfbxiStrings.Shape) continue;

                string name;
                UfbxiFail.CheckNoDesc(n.GetValS(0, out name), "ufbxi_get_val1(n, \"S\", &name)");

                if (deformer == null) {
                    deformer = UfbxiReadElement.PushSyntheticElement<UfbxBlendDeformer>(uc, out deformerFbxId,
                        n, name, UfbxElementType.BlendDeformer);
                    UfbxiReadElement.ConnectOo(uc, deformerFbxId, info.FbxId);
                }

                ulong channelFbxId;
                UfbxBlendChannel channel = UfbxiReadElement.PushSyntheticElement<UfbxBlendChannel>(uc,
                    out channelFbxId, n, name, UfbxElementType.BlendChannel);

                // C: ufbx_real_list weight_list = { NULL, 0 }; push_copy into tmp_full_weights
                uc.TmpFullWeights.Add(null);

                UfbxProp[] shapeProps = new UfbxProp[1];
                shapeProps[0].Name = UfbxiStrings.DeformPercent;
                shapeProps[0].InternalKey = UfbxiProperties.GetNameKeyC(UfbxiStrings.DeformPercent);
                shapeProps[0].Type = UfbxPropType.Number;
                shapeProps[0].ValueReal = 0.0;
                shapeProps[0].ValueStr = string.Empty;
                // C: value_blob = ufbx_empty_blob -> the port's empty blob is `null`.

                UfbxProp selfProp;
                if (FindPropPublic(info.Props, name, out selfProp)
                    && (selfProp.Type == UfbxPropType.Number || selfProp.Type == UfbxPropType.Integer)) {
                    shapeProps[0].ValueReal = selfProp.ValueReal;
                    UfbxiReadElement.ConnectPp(uc, info.FbxId, channelFbxId, name, shapeProps[0].Name);
                } else if (uc.Version < 6000) {
                    UfbxiReadElement.ConnectPp(uc, info.FbxId, channelFbxId, name, shapeProps[0].Name);
                }

                channel.Name = name;
                channel.Props.Props = shapeProps;

                UfbxiElementInfo shapeInfo = new UfbxiElementInfo();
                shapeInfo.FbxId = UfbxiFbxId.PushSyntheticId(uc);
                shapeInfo.Name = name;
                shapeInfo.DomNode = UfbxiDom.GetDomNode(uc, n);

                ReadShape(uc, n, shapeInfo);

                UfbxiReadElement.ConnectOo(uc, channelFbxId, deformerFbxId);
                UfbxiReadElement.ConnectOo(uc, shapeInfo.FbxId, channelFbxId);
            }
        }

        // C: ufbx_find_prop_len (ufbx.c:30643-30658) -- the PUBLIC prop lookup. Unlike the
        // internal ufbxi_find_prop it has NO UFBX_PROP_FLAG_NO_VALUE gate, so it is ported
        // here instead of reusing UfbxiProperties.TryFindProp.
        static bool FindPropPublic(UfbxProps props, string name, out UfbxProp prop)
        {
            uint key = UfbxiProperties.GetNameKey(name, name.Length);
            while (props != null) {
                UfbxProp[] data = props.Props;
                if (data != null) {
                    // ufbxi_macro_lower_bound_eq(ufbx_prop, 4, ...) (ufbx.c:1188-1204)
                    int lo = 0, hi = data.Length;
                    while (hi - lo > 4) {
                        int mid = lo + (hi - lo) / 2;
                        UfbxProp a = data[mid];
                        bool less;
                        if (a.InternalKey != key) {
                            less = a.InternalKey < key;                      // C: ufbxi_cmp_prop_less_ref
                        } else {
                            less = string.CompareOrdinal(a.Name, name) < 0;  // C: ufbxi_str_less
                        }
                        if (less) lo = mid + 1; else hi = mid + 1;
                    }
                    for (; lo < hi; lo++) {
                        UfbxProp a = data[lo];
                        if (a.InternalKey == key && a.Name == name) {
                            prop = a;
                            return true;
                        }
                    }
                }
                props = props.Defaults;
            }
            prop = default(UfbxProp);
            return false;
        }

        // ------------------------------------------------------------------
        // ufbxi_patch_mesh_reals (ufbx.c:13215-13236)
        // ------------------------------------------------------------------

        internal static void PatchMeshReals(UfbxMesh mesh)
        {
            mesh.VertexPosition.ValueReals = 3;
            mesh.VertexNormal.ValueReals = 3;
            mesh.VertexUv.ValueReals = 2;
            mesh.VertexTangent.ValueReals = 3;
            mesh.VertexBitangent.ValueReals = 3;
            mesh.VertexColor.ValueReals = 4;
            mesh.VertexCrease.ValueReals = 1;
            mesh.SkinnedPosition.ValueReals = 3;
            mesh.SkinnedNormal.ValueReals = 3;

            if (mesh.UvSets != null) {
                for (int i = 0; i < mesh.UvSets.Length; i++) {
                    mesh.UvSets[i].VertexUv.ValueReals = 2;
                    mesh.UvSets[i].VertexTangent.ValueReals = 3;
                    mesh.UvSets[i].VertexBitangent.ValueReals = 3;
                }
            }

            if (mesh.ColorSets != null) {
                for (int i = 0; i < mesh.ColorSets.Length; i++) {
                    mesh.ColorSets[i].VertexColor.ValueReals = 4;
                }
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_mesh_part_add_face (ufbx.c:13251-13261)
        // ------------------------------------------------------------------

        static void MeshPartAddFace(ref UfbxMeshPart part, uint numIndices)
        {
            part.NumFaces++;
            if (numIndices >= 3) {
                part.NumTriangles += (int)numIndices - 2;
            } else {
                // `num_empty/point/line_faces` are consecutive in C (static asserts
                // ufbx.c:13249-13250); spelled out here.
                switch (numIndices) {
                case 0: part.NumEmptyFaces++; break;
                case 1: part.NumPointFaces++; break;
                default: part.NumLineFaces++; break;
                }
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_assign_face_groups (ufbx.c:13263-13393)
        // ------------------------------------------------------------------

        // C: ufbxi_assign_face_groups(&uc->result, &uc->error, mesh, p_consecutive_indices,
        // retain_parts). The only caller passes `&uc->max_consecutive_indices`
        // (ufbx.c:13774), so the out-parameter is a `ref int` here.
        internal static void AssignFaceGroups(UfbxiContext uc, UfbxMesh mesh, ref int pConsecutiveIndices,
            bool retainParts)
        {
            int numFaces = mesh.NumFaces;
            UfbxiFail.CheckNoDesc(numFaces > 0, "num_faces > 0");
            UfbxiFail.CheckNoDesc((ulong)(uint)numFaces < uint.MaxValue, "num_faces < UINT32_MAX");
            UfbxiFail.CheckNoDesc(mesh.FaceGroup != null && mesh.FaceGroup.Length == numFaces,
                "mesh->face_group.count == num_faces");

            uint[] ids = new uint[numFaces];
            int numIds = 0;

            UfbxiIdGroup[] seenIds = new UfbxiIdGroup[1 << FaceGroupHashBits];

            uint seed = 2654435769u;
            uint rehashThreshold = 256;

            // Loosely deduplicate group IDs
            for (int i = 0; i < mesh.FaceGroup.Length; i++) {
                uint id = mesh.FaceGroup[i];
                uint idHash = unchecked((id * seed) >> (32 - FaceGroupHashBits));
                UfbxiIdGroup seen = seenIds[idHash];
                if (seen.Id != id || seen.Index == 0) {
                    seen.Id = id;
                    seen.Index++;
                    if (seen.Index > rehashThreshold) {
                        seed = unchecked(seed * seed);
                        rehashThreshold *= 2;
                    }
                    seenIds[idHash] = seen;
                    ids[numIds++] = id;
                }
            }

            // Sort and deduplicate remaining IDs (ufbxi_unstable_sort + ufbxi_less_int32)
            UfbxiSort.UnstableSort(ids, numIds, LessInt32, null);

            int numGroups = 0;
            for (int i = 0; i < numIds; ) {
                uint id = ids[i];
                ids[numGroups++] = id;
                do { i++; } while (i < numIds && ids[i] == id);
            }

            // Allocate group info structs
            UfbxFaceGroup[] groups = new UfbxFaceGroup[numGroups];
            for (int i = 0; i < numGroups; i++) {
                groups[i].Id = unchecked((int)ids[i]);
                groups[i].Name = string.Empty;   // C: ufbxi_empty_char
            }

            mesh.FaceGroups = groups;

            UfbxMeshPart[] parts = null;
            if (retainParts) {
                parts = new UfbxMeshPart[numGroups];
                mesh.FaceGroupParts = parts;
            }

            // Optimization: Use `consecutive_indices` for a single group
            if (numGroups == 1) {
                for (int i = 0; i < numFaces; i++) mesh.FaceGroup[i] = 0;

                if (parts != null) {
                    parts[0].FaceIndices = SentinelIndexConsecutive;   // implied count: numFaces
                    parts[0].NumEmptyFaces = mesh.NumEmptyFaces;
                    parts[0].NumPointFaces = mesh.NumPointFaces;
                    parts[0].NumLineFaces = mesh.NumLineFaces;
                    parts[0].NumFaces = numFaces;
                    parts[0].NumTriangles = mesh.NumTriangles;
                }

                if (numFaces > pConsecutiveIndices) pConsecutiveIndices = numFaces;
                return;
            }

            // C: memset(seen_ids, 0, sizeof(seen_ids))
            seenIds = new UfbxiIdGroup[1 << FaceGroupHashBits];

            // Count faces and triangles per group and reassign IDs
            int faceIx2 = 0;
            for (int i = 0; i < mesh.FaceGroup.Length; i++) {
                uint id = mesh.FaceGroup[i];
                uint idHash = unchecked((id * seed) >> (32 - FaceGroupHashBits));

                uint numIndices = mesh.Faces[faceIx2].NumIndices;

                int index;
                UfbxiIdGroup seen = seenIds[idHash];
                if (seen.Id == id && seen.Index > 0) {
                    index = (int)seen.Index - 1;
                } else {
                    int signedId = unchecked((int)id);
                    // ufbxi_macro_lower_bound_eq(ufbx_face_group, 8, ...) (ufbx.c:13359)
                    index = -1;   // C: SIZE_MAX
                    int lo = 0, hi = numGroups;
                    while (hi - lo > 8) {
                        int mid = lo + (hi - lo) / 2;
                        if (groups[mid].Id < signedId) lo = mid + 1; else hi = mid + 1;
                    }
                    for (; lo < hi; lo++) {
                        if (groups[lo].Id == signedId) { index = lo; break; }
                    }
                    // C: ufbx_assert(index < num_groups) -- guaranteed by the dedup above.
                    seen.Id = id;
                    seen.Index = (uint)index + 1;
                    seenIds[idHash] = seen;
                }

                if (parts != null) {
                    MeshPartAddFace(ref parts[index], numIndices);
                }

                mesh.FaceGroup[i] = (uint)index;
                faceIx2++;
            }

            if (parts == null) return;

            // Subdivide `ids` for per-group `face_indices` (C slices one buffer; the port
            // gives each part its own array -- the buffer identity is not observable)
            int[] faceIndicesCounts = new int[numGroups];
            for (int pi = 0; pi < numGroups; pi++) {
                parts[pi].Index = (uint)pi;
                parts[pi].FaceIndices = new uint[parts[pi].NumFaces];
                faceIndicesCounts[pi] = 0;
            }

            // Collect per-group faces
            uint faceIndex = 0;
            for (int i = 0; i < mesh.FaceGroup.Length; i++) {
                int pi = (int)mesh.FaceGroup[i];
                parts[pi].FaceIndices[faceIndicesCounts[pi]++] = faceIndex++;
            }
        }

        // C: typedef struct { uint32_t id, index; } ufbxi_id_group (ufbx.c:13238-13240)
        struct UfbxiIdGroup
        {
            public uint Id;
            public uint Index;
        }

        // ------------------------------------------------------------------
        // ufbxi_update_face_groups (ufbx.c:13395-13428)
        // ------------------------------------------------------------------

        // Also called by the OBJ loader (ufbx.c:17652) and the geometry-cache rebuild
        // (ufbx.c:29930) with their own meshes.
        internal static void UpdateFaceGroups(UfbxiContext uc, UfbxMesh mesh, bool needCopy)
        {
            int numFaces = mesh.Faces.Length;   // C: mesh->faces.count
            int numGroups = mesh.FaceGroupParts != null ? mesh.FaceGroupParts.Length : 0;
            if (numGroups == 0) return;

            if (needCopy) {
                mesh.FaceGroupParts = new UfbxMeshPart[numGroups];
            }

            for (int i = 0; i < numFaces; i++) {
                MeshPartAddFace(ref mesh.FaceGroupParts[mesh.FaceGroup[i]], mesh.Faces[i].NumIndices);
            }

            int[] faceIndicesCounts = new int[numGroups];
            uint partIndex = 0;
            for (int pi = 0; pi < numGroups; pi++) {
                UfbxMeshPart part = mesh.FaceGroupParts[pi];
                part.Index = partIndex++;
                part.FaceIndices = new uint[part.NumFaces];
                faceIndicesCounts[pi] = 0;
                mesh.FaceGroupParts[pi] = part;
            }

            for (uint i = 0; i < (uint)numFaces; i++) {
                int pi = (int)mesh.FaceGroup[i];
                mesh.FaceGroupParts[pi].FaceIndices[faceIndicesCounts[pi]++] = i;
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_read_mesh (ufbx.c:13430-13807)
        // ------------------------------------------------------------------

        internal static void ReadMesh(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxMesh mesh = UfbxiReadElement.PushElement<UfbxMesh>(uc, info, UfbxElementType.Mesh);

            // In up to version 7100 FBX files blend shapes are contained within the same geometry node
            if (uc.Version <= 7100) {
                ReadSyntheticBlendShapes(uc, node, info);
            }

            PatchMeshReals(mesh);

            // Sometimes there are empty meshes in FBX files?
            UfbxiNode nodeVertices = node.FindChild(UfbxiStrings.Vertices);
            UfbxiNode nodeIndices = node.FindChild(UfbxiStrings.PolygonVertexIndex);
            if (nodeVertices == null) return;

            if (uc.Opts.IgnoreGeometry) return;

            UfbxiValueArray vertices = nodeVertices.GetArray('r');
            UfbxiValueArray indices = nodeIndices != null ? nodeIndices.GetArray('i') : null;
            UfbxiValueArray edgeIndices = node.FindArray(UfbxiStrings.Edges, 'i');
            UfbxiFail.CheckNoDesc(vertices != null, "vertices");
            UfbxiFail.CheckNoDesc(nodeIndices == null || indices != null, "!node_indices || indices");
            UfbxiFail.CheckNoDesc(vertices.Size % 3 == 0, "vertices->size % 3 == 0");

            mesh.NumVertices = vertices.Size / 3;
            mesh.NumIndices = indices != null ? indices.Size : 0;

            uint[] indexData;
            if (indices != null) {
                indexData = MaterializeU32(indices, mesh.NumIndices);
            } else {
                indexData = null;
            }
            // C: Duplicate `index_data` for modification if we retain DOM
            // (ufbxi_push_copy at ufbx.c:13463-13466) -- the materialized array IS the copy,
            // so the DOM's PolygonVertexIndex keeps the file bytes. A NULL 0-size source
            // still yields a (non-NULL) empty buffer from ufbxi_push_copy.
            if (uc.Opts.RetainDom && indexData == null) {
                indexData = new uint[0];
            }

            UfbxVec3[] verticesArr = MaterializeVec3s(vertices, mesh.NumVertices);
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

            // Read edges before un-negating the indices
            if (edgeIndices != null) {
                int numEdges = edgeIndices.Size;
                UfbxEdge[] edges = new UfbxEdge[numEdges];

                int dstIx = 0;

                // Edges are represented using a single index into PolygonVertexIndex.
                // The edge is between two consecutive vertices in the polygon.
                for (int i = 0; i < numEdges; i++) {
                    uint indexIx = BinaryPrimitives.ReadUInt32LittleEndian(
                        edgeIndices.Data.AsSpan(edgeIndices.Offset + i * 4));
                    if (indexIx >= (uint)mesh.NumIndices) {
                        if (uc.Opts.Strict) UfbxiFail.FailNoDesc("Edge index out of bounds");
                        continue;
                    }
                    edges[dstIx].A = indexIx;
                    if ((int)indexData[indexIx] < 0) {
                        // Previous index is the last one of this polygon, rewind to first index.
                        while (indexIx > 0 && (int)indexData[indexIx - 1] >= 0) {
                            indexIx--;
                        }
                    } else {
                        // Connect to the next index in the same polygon
                        indexIx++;
                    }
                    UfbxiFail.CheckNoDesc(indexIx < (uint)mesh.NumIndices, "index_ix < mesh->num_indices");
                    edges[dstIx].B = indexIx;
                    dstIx++;
                }

                Array.Resize(ref edges, dstIx);
                mesh.Edges = edges;
                mesh.NumEdges = dstIx;
            }

            ProcessIndices(uc, mesh, indexData);

            // Count the number of UV/color sets
            int numUv = 0, numColor = 0, numBitangents = 0, numTangents = 0;
            for (int ci = 0; ci < node.NumChildren; ci++) {
                UfbxiNode n = node.Children[ci];
                if (n.Name == UfbxiStrings.LayerElementUV) numUv++;
                if (n.Name == UfbxiStrings.LayerElementColor) numColor++;
                if (n.Name == UfbxiStrings.LayerElementBinormal) numBitangents++;
                if (n.Name == UfbxiStrings.LayerElementTangent) numTangents++;
            }

            int numTextures = 0;

            // C: ufbxi_push_zero(&uc->tmp_stack, ufbxi_tangent_layer, ...) -- the empty-array
            // push_zero results are checked in C but always succeed.
            UfbxiTangentLayer[] bitangents = new UfbxiTangentLayer[numBitangents];
            UfbxiTangentLayer[] tangents = new UfbxiTangentLayer[numTangents];
            for (int i = 0; i < numBitangents; i++) bitangents[i] = new UfbxiTangentLayer();
            for (int i = 0; i < numTangents; i++) tangents[i] = new UfbxiTangentLayer();

            UfbxUvSet[] uvSets = new UfbxUvSet[numUv];
            UfbxColorSet[] colorSets = new UfbxColorSet[numColor];

            int uvCount = 0, colorCount = 0;
            int numBitangentsRead = 0, numTangentsRead = 0;
            int faceMaterialCount = 0;   // C: mesh->face_material.count
            int faceGroupCount = 0;      // C: mesh->face_group.count

            for (int ci = 0; ci < node.NumChildren; ci++) {
                UfbxiNode n = node.Children[ci];
                if (n.Name == null || n.Name.Length == 0 || n.Name[0] != 'L') continue; // All names start with 'LayerElement*'

                if (n.Name == UfbxiStrings.LayerElementNormal) {
                    if (mesh.VertexNormal.Exists) continue;
                    ReadVertexElementVec3(uc, mesh, n, ref mesh.VertexNormal,
                        UfbxiStrings.Normals, UfbxiStrings.NormalsIndex, UfbxiStrings.NormalsW);
                } else if (n.Name == UfbxiStrings.LayerElementBinormal) {
                    UfbxiTangentLayer layer = bitangents[numBitangentsRead++];

                    { int v; if (n.GetValI(0, out v)) layer.Index = unchecked((uint)v); }
                    ReadVertexElementVec3(uc, mesh, n, ref layer.Elem,
                        UfbxiStrings.Binormals, UfbxiStrings.BinormalsIndex, UfbxiStrings.BinormalsW);
                    bitangents[numBitangentsRead - 1] = layer;
                    if (!layer.Elem.Exists) numBitangentsRead--;

                } else if (n.Name == UfbxiStrings.LayerElementTangent) {
                    UfbxiTangentLayer layer = tangents[numTangentsRead++];

                    { int v; if (n.GetValI(0, out v)) layer.Index = unchecked((uint)v); }
                    ReadVertexElementVec3(uc, mesh, n, ref layer.Elem,
                        UfbxiStrings.Tangents, UfbxiStrings.TangentsIndex, UfbxiStrings.TangentsW);
                    tangents[numTangentsRead - 1] = layer;
                    if (!layer.Elem.Exists) numTangentsRead--;

                } else if (n.Name == UfbxiStrings.LayerElementUV) {
                    ref UfbxUvSet set = ref uvSets[uvCount];
                    uvCount++;

                    { int v; if (n.GetValI(0, out v)) set.Index = unchecked((uint)v); }
                    {
                        string name;
                        if (!FindValS(n, UfbxiStrings.Name, out name)) name = string.Empty;
                        set.Name = name;
                    }

                    ReadVertexElementVec2(uc, mesh, n, ref set.VertexUv,
                        UfbxiStrings.UV, UfbxiStrings.UVIndex, null);
                    if (!set.VertexUv.Exists) uvCount--;

                } else if (n.Name == UfbxiStrings.LayerElementColor) {
                    ref UfbxColorSet set = ref colorSets[colorCount];
                    colorCount++;

                    { int v; if (n.GetValI(0, out v)) set.Index = unchecked((uint)v); }
                    {
                        string name;
                        if (!FindValS(n, UfbxiStrings.Name, out name)) name = string.Empty;
                        set.Name = name;
                    }

                    ReadVertexElementVec4(uc, mesh, n, ref set.VertexColor,
                        UfbxiStrings.Colors, UfbxiStrings.ColorIndex, null);
                    if (!set.VertexColor.Exists) colorCount--;

                } else if (n.Name == UfbxiStrings.LayerElementVertexCrease) {
                    ReadVertexElementReal(uc, mesh, n, ref mesh.VertexCrease,
                        UfbxiStrings.VertexCrease, UfbxiStrings.VertexCreaseIndex, null);

                } else if (n.Name == UfbxiStrings.LayerElementEdgeCrease) {
                    string mapping = string.Empty;
                    FindMappingRawC(n, ref mapping);
                    if (mapping == UfbxiStrings.ByEdge) {
                        if (mesh.EdgeCrease != null && mesh.EdgeCrease.Length > 0) continue;
                        mesh.EdgeCrease = ReadTruncatedArrayReal(uc, n, UfbxiStrings.EdgeCrease, mesh.NumEdges);
                    } else {
                        WarnPolygonMapping(uc, UfbxiStrings.EdgeCrease, mapping);
                    }

                } else if (n.Name == UfbxiStrings.LayerElementSmoothing) {
                    string mapping = string.Empty;
                    FindMappingRawC(n, ref mapping);
                    if (mapping == UfbxiStrings.ByEdge) {
                        if (mesh.EdgeSmoothing != null && mesh.EdgeSmoothing.Length > 0) continue;
                        mesh.EdgeSmoothing = ReadTruncatedArrayBool(uc, n, UfbxiStrings.Smoothing, mesh.NumEdges);
                    } else if (mapping == UfbxiStrings.ByPolygon) {
                        if (mesh.FaceSmoothing != null && mesh.FaceSmoothing.Length > 0) continue;
                        mesh.FaceSmoothing = ReadTruncatedArrayBool(uc, n, UfbxiStrings.Smoothing, mesh.NumFaces);
                    } else {
                        WarnPolygonMapping(uc, UfbxiStrings.Smoothing, mapping);
                    }

                } else if (n.Name == UfbxiStrings.LayerElementVisibility) {
                    string mapping = string.Empty;
                    FindMappingRawC(n, ref mapping);
                    if (mapping == UfbxiStrings.ByEdge) {
                        if (mesh.EdgeVisibility != null && mesh.EdgeVisibility.Length > 0) continue;
                        mesh.EdgeVisibility = ReadTruncatedArrayBool(uc, n, UfbxiStrings.Visibility, mesh.NumEdges);
                    } else {
                        WarnPolygonMapping(uc, UfbxiStrings.Visibility, mapping);
                    }

                } else if (n.Name == UfbxiStrings.LayerElementMaterial) {
                    if (faceMaterialCount > 0) continue;
                    string mapping = string.Empty;
                    FindMappingRawC(n, ref mapping);
                    if (mapping == UfbxiStrings.ByPolygon) {
                        uint[] fm = ReadTruncatedArrayI32(uc, n, UfbxiStrings.Materials, (uint)mesh.NumFaces);
                        if (fm != null) {
                            mesh.FaceMaterial = fm;
                            faceMaterialCount = mesh.NumFaces;
                        }
                    } else if (mapping == UfbxiStrings.AllSame) {
                        UfbxiValueArray arr = n.FindArray(UfbxiStrings.Materials, 'i');
                        UfbxiFail.CheckNoDesc(arr != null && arr.Size >= 1, "arr && arr->size >= 1");
                        uint material = BinaryPrimitives.ReadUInt32LittleEndian(arr.Data.AsSpan(arr.Offset));
                        faceMaterialCount = mesh.NumFaces;
                        if (material == 0) {
                            mesh.FaceMaterial = SentinelIndexZero;
                        } else {
                            uint[] fm = new uint[mesh.NumFaces];
                            for (int i = 0; i < mesh.NumFaces; i++) fm[i] = material;
                            mesh.FaceMaterial = fm;
                        }
                    } else {
                        WarnPolygonMapping(uc, UfbxiStrings.Materials, mapping);
                    }

                } else if (n.Name == UfbxiStrings.LayerElementPolygonGroup) {
                    if (faceGroupCount > 0) continue;
                    string mapping = null;
                    UfbxiFail.CheckNoDesc(FindMappingRawC(n, ref mapping),
                        "ufbxi_find_val1(n, ufbxi_MappingInformationType, \"c\", (char**)&mapping)");
                    if (mapping == UfbxiStrings.ByPolygon) {
                        uint[] fg = ReadTruncatedArrayI32(uc, n, UfbxiStrings.PolygonGroup, (uint)mesh.NumFaces);
                        if (fg != null) {
                            mesh.FaceGroup = fg;
                            faceGroupCount = mesh.NumFaces;
                        }
                    }

                } else if (n.Name == UfbxiStrings.LayerElementHole) {
                    if (faceGroupCount > 0) continue;   // C: checks face_group here (ufbx.c:13660) -- preserved
                    string mapping = null;
                    UfbxiFail.CheckNoDesc(FindMappingRawC(n, ref mapping),
                        "ufbxi_find_val1(n, ufbxi_MappingInformationType, \"c\", (char**)&mapping)");
                    if (mapping == UfbxiStrings.ByPolygon) {
                        bool[] fh = ReadTruncatedArrayBool(uc, n, UfbxiStrings.Hole, mesh.NumFaces);
                        if (fh != null) mesh.FaceHole = fh;
                    }

                } else if (n.Name.Length >= 12 && string.CompareOrdinal(n.Name, 0, "LayerElement", 0, 12) == 0) {
                    // 6x00 stores textures in mesh geometry, eg. "LayerElementTexture",
                    // "LayerElementDiffuseFactorTextures", "LayerElementEmissive_Textures"...

                    // Make sure the name has no internal zero bytes
                    UfbxiFail.CheckNoDesc(n.Name.IndexOf('\0') < 0, "!memchr(n->name, '\\0', n->name_len)");

                    string propName = string.Empty;
                    if (n.Name.Length > 20 && n.Name.EndsWith("Textures", StringComparison.Ordinal)) {
                        propName = n.Name.Substring(12, n.Name.Length - 20);
                        if (propName[propName.Length - 1] == '_') {
                            propName = propName.Substring(0, propName.Length - 1);
                        }
                    } else if (n.Name == "LayerElementTexture") {
                        propName = "Diffuse";
                    }

                    if (propName.Length > 0) {
                        UfbxiFail.CheckNoDesc(uc.StringPool.PushStringPlaceStr(ref propName, false),
                            "ufbxi_push_string_place_str(&uc->string_pool, &prop_name, false)");
                        string mapping = null;
                        if (FindMappingRawC(n, ref mapping)) {
                            UfbxiValueArray arr = n.FindArray(UfbxiStrings.TextureId, 'i');

                            UfbxiTmpMeshTexture tex = uc.PushTmpMeshTexture();
                            if (arr != null) {
                                tex.FaceTexture = MaterializeU32(arr, arr.Size);
                                tex.NumFaces = arr.Size;
                            }
                            tex.PropName = propName;
                            tex.AllSame = mapping == UfbxiStrings.AllSame;
                            numTextures++;
                        }
                    }
                }
            }

            mesh.UvSets = uvSets;
            mesh.ColorSets = colorSets;

            // Always use a default zero material, this will be removed if no materials are found
            if (faceMaterialCount == 0) {
                if (mesh.NumFaces > uc.MaxZeroIndices) uc.MaxZeroIndices = mesh.NumFaces;
                mesh.FaceMaterial = SentinelIndexZero;
                faceMaterialCount = mesh.NumFaces;
            }

            if (uc.Opts.Strict) {
                UfbxiFail.CheckNoDesc(uvCount == numUv, "mesh->uv_sets.count == num_uv");
                UfbxiFail.CheckNoDesc(colorCount == numColor, "mesh->color_sets.count == num_color");
                UfbxiFail.CheckNoDesc(numBitangentsRead == numBitangents, "num_bitangents_read == num_bitangents");
                UfbxiFail.CheckNoDesc(numTangentsRead == numTangents, "num_tangents_read == num_tangents");
            }

            // Connect bitangents/tangents to UV sets
            for (int ci = 0; ci < node.NumChildren; ci++) {
                UfbxiNode n = node.Children[ci];
                if (n.Name != UfbxiStrings.Layer) continue;
                int uvSetIx = -1;
                int bitangentLayerIx = -1;
                int tangentLayerIx = -1;

                for (int cj = 0; cj < n.NumChildren; cj++) {
                    UfbxiNode c = n.Children[cj];
                    if (c.Name != UfbxiStrings.LayerElement) continue;

                    int typedIndex;
                    if (!FindValI(c, UfbxiStrings.TypedIndex, out typedIndex)) continue;
                    string type;
                    if (!FindValS(c, UfbxiStrings.Type, out type)) continue;

                    if (type == UfbxiStrings.LayerElementUV) {
                        for (int i = 0; i < uvCount; i++) {
                            if (uvSets[i].Index == (uint)typedIndex) {
                                uvSetIx = i;
                                break;
                            }
                        }
                    } else if (type == UfbxiStrings.LayerElementBinormal) {
                        for (int i = 0; i < numBitangentsRead; i++) {
                            if (bitangents[i].Index == (uint)typedIndex) {
                                bitangentLayerIx = i;
                                break;
                            }
                        }
                    } else if (type == UfbxiStrings.LayerElementTangent) {
                        for (int i = 0; i < numTangentsRead; i++) {
                            if (tangents[i].Index == (uint)typedIndex) {
                                tangentLayerIx = i;
                                break;
                            }
                        }
                    }
                }

                if (uvSetIx >= 0) {
                    if (bitangentLayerIx >= 0) {
                        uvSets[uvSetIx].VertexBitangent = bitangents[bitangentLayerIx].Elem;
                    }
                    if (tangentLayerIx >= 0) {
                        uvSets[uvSetIx].VertexTangent = tangents[tangentLayerIx].Elem;
                    }
                }
            }

            mesh.SkinnedIsLocal = true;
            mesh.SkinnedPosition = mesh.VertexPosition;
            mesh.SkinnedNormal = mesh.VertexNormal;

            PatchMeshReals(mesh);

            if (faceGroupCount > 0 && (mesh.FaceGroups == null || mesh.FaceGroups.Length == 0)) {
                AssignFaceGroups(uc, mesh, ref uc.MaxConsecutiveIndices, uc.RetainMeshParts);
            }

            // Sort UV and color sets by set index
            SortUvSets(uvSets, uvCount);
            SortColorSets(colorSets, colorCount);
            if (uvCount != uvSets.Length) Array.Resize(ref uvSets, uvCount);
            if (colorCount != colorSets.Length) Array.Resize(ref colorSets, colorCount);
            mesh.UvSets = uvSets;
            mesh.ColorSets = colorSets;

            if (numTextures > 0) {
                UfbxiMeshExtra extra = uc.PushElementExtra(mesh.ElementId, () => new UfbxiMeshExtra());
                extra.TextureCount = numTextures;
                extra.TextureArr = uc.PopTmpMeshTextures(numTextures);
            }

            // Subdivision

            {
                int levels;
                if (FindValI(node, UfbxiStrings.PreviewDivisionLevels, out levels)) {
                    mesh.SubdivisionPreviewLevels = unchecked((uint)levels);
                }
            }
            {
                int levels;
                if (FindValI(node, UfbxiStrings.RenderDivisionLevels, out levels)) {
                    mesh.SubdivisionRenderLevels = unchecked((uint)levels);
                }
            }
            {
                int smoothness;
                if (FindValI(node, UfbxiStrings.Smoothness, out smoothness)) {
                    if (smoothness >= 0 && smoothness <= (int)UfbxSubdivisionDisplayMode.Smooth) {
                        mesh.SubdivisionDisplayMode = (UfbxSubdivisionDisplayMode)smoothness;
                    }
                }
            }
            {
                int boundary;
                if (FindValI(node, UfbxiStrings.BoundaryRule, out boundary)) {
                    if (boundary >= 0 && boundary <= (int)UfbxSubdivisionBoundary.SharpCorners - 1) {
                        mesh.SubdivisionBoundary = (UfbxSubdivisionBoundary)(boundary + 1);
                    }
                }
            }
        }

        // C: ufbxi_find_val1(node, name, "I", &v) / ("S", &v) spelled out.
        static bool FindValI(UfbxiNode node, string name, out int value)
        {
            value = 0;
            UfbxiNode child = node.FindChild(name);
            if (child == null) return false;
            return child.GetValI(0, out value);
        }

        static bool FindValS(UfbxiNode node, string name, out string value)
        {
            value = null;
            UfbxiNode child = node.FindChild(name);
            if (child == null) return false;
            return child.GetValS(0, out value);
        }

        // ------------------------------------------------------------------
        // NURBS readers (ufbx.c:13809-13897)
        // ------------------------------------------------------------------

        // C: ufbxi_read_nurbs_topology (ufbx.c:13809-13819)
        static UfbxNurbsTopology ReadNurbsTopology(string form)
        {
            if (form == "Open") {
                return UfbxNurbsTopology.Open;
            } else if (form == "Closed") {
                return UfbxNurbsTopology.Closed;
            } else if (form == "Periodic") {
                return UfbxNurbsTopology.Periodic;
            }
            return UfbxNurbsTopology.Open;
        }

        // C: ufbxi_read_nurbs_curve (ufbx.c:13821-13849)
        internal static void ReadNurbsCurve(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxNurbsCurve nurbs = UfbxiReadElement.PushElement<UfbxNurbsCurve>(uc, info, UfbxElementType.NurbsCurve);

            int dimension = 3;

            {
                int order;
                UfbxiFail.CheckNoDesc(FindValI(node, UfbxiStrings.Order, out order),
                    "ufbxi_find_val1(node, ufbxi_Order, \"I\", &nurbs->basis.order)");
                nurbs.Basis.Order = unchecked((uint)order);
            }
            {
                int dim;
                if (FindValI(node, UfbxiStrings.Dimension, out dim)) dimension = dim;
            }
            {
                string form;
                UfbxiFail.CheckNoDesc(FindValS(node, UfbxiStrings.Form, out form),
                    "ufbxi_find_val1(node, ufbxi_Form, \"C\", (char**)&form)");
                nurbs.Basis.Topology = ReadNurbsTopology(form);
            }
            nurbs.Basis.Is2D = dimension == 2;

            if (!uc.Opts.IgnoreGeometry) {
                UfbxiValueArray points = node.FindArray(UfbxiStrings.Points, 'r');
                UfbxiValueArray knot = node.FindArray(UfbxiStrings.KnotVector, 'r');
                UfbxiFail.CheckNoDesc(points != null, "points");
                UfbxiFail.CheckNoDesc(knot != null, "knot");
                UfbxiFail.CheckNoDesc(points.Size % 4 == 0, "points->size % 4 == 0");

                nurbs.ControlPoints = MaterializeVec4s(points, points.Size / 4);
                nurbs.Basis.KnotVector = MaterializeReals(knot, knot.Size);
            }
        }

        // C: ufbxi_read_nurbs_surface (ufbx.c:13851-13897)
        internal static void ReadNurbsSurface(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxNurbsSurface nurbs = UfbxiReadElement.PushElement<UfbxNurbsSurface>(uc, info, UfbxElementType.NurbsSurface);

            int orderU = 0, orderV = 0;
            long dimensionU = 0, dimensionV = 0;
            int stepU = 0, stepV = 0;
            string formU, formV;

            UfbxiFail.CheckNoDesc(FindVal2I(node, UfbxiStrings.NurbsSurfaceOrder, out orderU, out orderV),
                "ufbxi_find_val2(node, ufbxi_NurbsSurfaceOrder, \"II\", &nurbs->basis_u.order, &nurbs->basis_v.order)");
            nurbs.BasisU.Order = unchecked((uint)orderU);
            nurbs.BasisV.Order = unchecked((uint)orderV);

            UfbxiFail.CheckNoDesc(FindVal2Z64(node, UfbxiStrings.Dimensions, out dimensionU, out dimensionV),
                "ufbxi_find_val2(node, ufbxi_Dimensions, \"ZZ\", &dimension_u, &dimension_v)");
            UfbxiFail.CheckNoDesc(FindVal2I(node, UfbxiStrings.Step, out stepU, out stepV),
                "ufbxi_find_val2(node, ufbxi_Step, \"II\", &step_u, &step_v)");
            UfbxiFail.CheckNoDesc(FindVal2S(node, UfbxiStrings.Form, out formU, out formV),
                "ufbxi_find_val2(node, ufbxi_Form, \"CC\", (char**)&form_u, (char**)&form_v)");
            {
                bool flip;
                if (FindValB(node, UfbxiStrings.FlipNormals, out flip)) nurbs.FlipNormals = flip;
            }

            // Support control point area up to 2^32, as a larger control point array cannot be represented in binary FBX.
            // This guards against users doing `dimension_u * dimension_v`, causing a 32-bit overflow.
            if (dimensionU > 0) {
                UfbxiFail.CheckNoDesc((ulong)dimensionV <= uint.MaxValue / (ulong)dimensionU,
                    "dimension_v <= UINT32_MAX / dimension_u");
            }

            nurbs.BasisU.Topology = ReadNurbsTopology(formU);
            nurbs.BasisV.Topology = ReadNurbsTopology(formV);
            nurbs.NumControlPointsU = unchecked((int)dimensionU);
            nurbs.NumControlPointsV = unchecked((int)dimensionV);
            nurbs.SpanSubdivisionU = stepU > 0 ? unchecked((uint)stepU) : 4u;
            nurbs.SpanSubdivisionV = stepV > 0 ? unchecked((uint)stepV) : 4u;

            if (!uc.Opts.IgnoreGeometry) {
                UfbxiValueArray points = node.FindArray(UfbxiStrings.Points, 'r');
                UfbxiValueArray knotU = node.FindArray(UfbxiStrings.KnotVectorU, 'r');
                UfbxiValueArray knotV = node.FindArray(UfbxiStrings.KnotVectorV, 'r');
                UfbxiFail.CheckNoDesc(points != null, "points");
                UfbxiFail.CheckNoDesc(knotU != null, "knot_u");
                UfbxiFail.CheckNoDesc(knotV != null, "knot_v");
                UfbxiFail.CheckNoDesc(points.Size % 4 == 0, "points->size % 4 == 0");
                UfbxiFail.CheckNoDesc((ulong)(points.Size / 4) == (ulong)dimensionU * (ulong)dimensionV,
                    "points->size / 4 == (size_t)dimension_u * (size_t)dimension_v");

                nurbs.ControlPoints = MaterializeVec4s(points, points.Size / 4);
                nurbs.BasisU.KnotVector = MaterializeReals(knotU, knotU.Size);
                nurbs.BasisV.KnotVector = MaterializeReals(knotV, knotV.Size);
            }
        }

        // C: ufbxi_find_val1(node, name, "B", &v)
        static bool FindValB(UfbxiNode node, string name, out bool value)
        {
            value = false;
            UfbxiNode child = node.FindChild(name);
            if (child == null) return false;
            return child.GetValB(0, out value);
        }

        // ------------------------------------------------------------------
        // ufbxi_read_line (ufbx.c:13899-13958) -- the DOM-aliasing reader
        // ------------------------------------------------------------------

        internal static void ReadLine(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxLineCurve line = UfbxiReadElement.PushElement<UfbxLineCurve>(uc, info, UfbxElementType.LineCurve);

            if (!uc.Opts.IgnoreGeometry) {
                UfbxiValueArray points = node.FindArray(UfbxiStrings.Points, 'r');
                UfbxiNode pointsIndexNode = node.FindChild(UfbxiStrings.PointsIndex);
                UfbxiValueArray pointsIndex = pointsIndexNode != null ? pointsIndexNode.GetArray('i') : null;
                UfbxiFail.CheckNoDesc(points != null, "points");
                UfbxiFail.CheckNoDesc(pointsIndex != null, "points_index");
                UfbxiFail.CheckNoDesc(points.Size % 3 == 0, "points->size % 3 == 0");

                if (points.Size > 0) {
                    int numControlPoints = points.Size / 3;
                    line.ControlPoints = MaterializeVec3s(points, numControlPoints);
                    int count = pointsIndex.Size;
                    // C: line->point_indices.data = (uint32_t*)points_index->data -- NO copy:
                    // every store below lands in the DOM array bytes (and the retained blob).
                    uint[] pointIndices = MaterializeU32(pointsIndex, count);
                    line.PointIndices = pointIndices;
                    byte[] domBlob = RetainedDomBlob(uc, pointsIndexNode);

                    UfbxiFail.CheckNoDesc(numControlPoints < int.MaxValue, "line->control_points.count < INT32_MAX");

                    // Count end points
                    int numSegments = 1;
                    if (count > 0) {
                        for (int i = 0; i < count - 1; i++) {
                            uint ix = pointIndices[i];
                            numSegments += (int)ix < 0 ? 1 : 0;
                        }
                    }

                    int prevEnd = 0;
                    UfbxLineSegment[] segments = new UfbxLineSegment[numSegments];
                    int segmentCount = 0;
                    for (int i = 0; i < count; i++) {
                        uint ix = pointIndices[i];
                        if ((int)ix < 0) {
                            ix = ~ix;
                            if (i + 1 < count) {
                                segments[segmentCount].IndexBegin = (uint)prevEnd;
                                segments[segmentCount].NumIndices = (uint)(i - prevEnd);
                                segmentCount++;
                                prevEnd = i;
                            }
                        }

                        if (ix < (uint)numControlPoints) {
                            StorePointIndex(pointsIndex, domBlob, pointIndices, i, ix);
                        } else {
                            uint[] one = new uint[1];
                            UfbxiReadElement.FixIndex(uc, one, 0, ix, (ulong)numControlPoints);
                            StorePointIndex(pointsIndex, domBlob, pointIndices, i, one[0]);
                        }
                    }

                    segments[segmentCount].IndexBegin = (uint)prevEnd;
                    segments[segmentCount].NumIndices = (uint)(count - prevEnd);
                    segmentCount++;
                    // C: ufbx_assert(line->segments.count == num_segments) -- holds by construction.
                    line.Segments = segments;
                }
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_read_transform_matrix (ufbx.c:13960-13966)
        // ------------------------------------------------------------------

        // Cross-module bridge: the S2-objects/S2-legacy call sites hold a DOM value array
        // (C passes `ufbx_real *data` straight from `ufbxi_find_val1(..., "r")`).
        internal static void ReadTransformMatrix(out UfbxMatrix m, UfbxiValueArray data)
        {
            m = default(UfbxMatrix);
            ReadTransformMatrix(ref m, MaterializeReals(data, 16));
        }

        static void ReadTransformMatrix(ref UfbxMatrix m, double[] data)
        {
            m.M00 = data[0]; m.M10 = data[1]; m.M20 = data[2];
            m.M01 = data[4]; m.M11 = data[5]; m.M21 = data[6];
            m.M02 = data[8]; m.M12 = data[9]; m.M22 = data[10];
            m.M03 = data[12]; m.M13 = data[13]; m.M23 = data[14];
        }

        // ------------------------------------------------------------------
        // ufbxi_read_skin (ufbx.c:13995-14025)
        // ------------------------------------------------------------------

        internal static void ReadSkin(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxSkinDeformer skin = UfbxiReadElement.PushElement<UfbxSkinDeformer>(uc, info, UfbxElementType.SkinDeformer);

            string skinningType;
            if (FindValS(node, UfbxiStrings.SkinningType, out skinningType)) {
                if (skinningType == "Rigid") {
                    skin.SkinningMethod = UfbxSkinningMethod.Rigid;
                } else if (skinningType == "Linear") {
                    skin.SkinningMethod = UfbxSkinningMethod.Linear;
                } else if (skinningType == "DualQuaternion") {
                    skin.SkinningMethod = UfbxSkinningMethod.DualQuaternion;
                } else if (skinningType == "Blend") {
                    skin.SkinningMethod = UfbxSkinningMethod.BlendedDqLinear;
                }
            }

            UfbxiValueArray indices = node.FindArray(UfbxiStrings.Indexes, 'i');
            UfbxiValueArray weights = node.FindArray(UfbxiStrings.BlendWeights, 'r');
            if (indices != null && weights != null) {
                // TODO strict: ufbxi_check(indices->size == weights->size);
                int numDqWeights = indices.Size < weights.Size ? indices.Size : weights.Size;
                skin.NumDqWeights = numDqWeights;
                skin.DqVertices = MaterializeU32(indices, numDqWeights);
                skin.DqWeights = MaterializeReals(weights, numDqWeights);
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_read_skin_cluster (ufbx.c:14027-14055)
        // ------------------------------------------------------------------

        internal static void ReadSkinCluster(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxSkinCluster cluster = UfbxiReadElement.PushElement<UfbxSkinCluster>(uc, info, UfbxElementType.SkinCluster);

            UfbxiValueArray indices = node.FindArray(UfbxiStrings.Indexes, 'i');
            UfbxiValueArray weights = node.FindArray(UfbxiStrings.Weights, 'r');

            if (indices != null && weights != null) {
                UfbxiFail.CheckNoDesc(indices.Size == weights.Size, "indices->size == weights->size");
                cluster.NumWeights = indices.Size;
                cluster.Vertices = MaterializeU32(indices, indices.Size);
                cluster.Weights = MaterializeReals(weights, indices.Size);
            }

            UfbxiValueArray transform = node.FindArray(UfbxiStrings.Transform, 'r');
            UfbxiValueArray transformLink = node.FindArray(UfbxiStrings.TransformLink, 'r');
            if (transform != null && transformLink != null) {
                UfbxiFail.CheckNoDesc(transform.Size >= 16, "transform->size >= 16");
                UfbxiFail.CheckNoDesc(transformLink.Size >= 16, "transform_link->size >= 16");

                ReadTransformMatrix(ref cluster.MeshNodeToBone, MaterializeReals(transform, 16));
                ReadTransformMatrix(ref cluster.BindToWorld, MaterializeReals(transformLink, 16));
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_read_blend_channel (ufbx.c:14057-14089)
        // ------------------------------------------------------------------

        internal static void ReadBlendChannel(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxBlendChannel channel = UfbxiReadElement.PushElement<UfbxBlendChannel>(uc, info, UfbxElementType.BlendChannel);

            double[] list = null;   // C: ufbx_real_list list = { NULL, 0 }
            UfbxiValueArray fullWeights = node.FindArray(UfbxiStrings.FullWeights, 'r');
            if (fullWeights != null) {
                list = MaterializeReals(fullWeights, fullWeights.Size);
            }
            uc.TmpFullWeights.Add(list);

            // Blender saves blend shapes with DeformPercent as a field, not a property.
            // However, the animations are mapped to the DeformPercent property.
            UfbxiNode deformPercent = node.FindChild(UfbxiStrings.DeformPercent);
            if ((channel.Props.Props == null || channel.Props.Props.Length == 0) && deformPercent != null) {
                UfbxProp[] shapeProps = new UfbxProp[1];
                shapeProps[0].Name = UfbxiStrings.DeformPercent;
                shapeProps[0].InternalKey = UfbxiProperties.GetNameKeyC(UfbxiStrings.DeformPercent);
                shapeProps[0].Type = UfbxPropType.Number;
                shapeProps[0].ValueStr = string.Empty;
                shapeProps[0].ValueReal = 100.0;   // C: 100.0f -- exact in double
                {
                    double valueReal;
                    if (deformPercent.GetValD(0, out valueReal)) shapeProps[0].ValueReal = valueReal;
                }
                channel.Props.Props = shapeProps;
            }
        }
    }
}
