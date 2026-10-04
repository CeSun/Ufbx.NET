// Scene hash, ported from test/hash_scene.h (ufbx v0.23.1, C reference
// `test/hash_scene.exe`). This is the verification oracle for the whole port:
// a FNV-1a-64 over every field of `ufbx_scene` in exactly the C traversal order
// and with exactly the C byte widths.
//
// Naming: `ufbxt_hash_*` / `ufbxt_*_imp` -> methods of `UfbxHashScene`.
//
// Fidelity notes (bit-exactness critical):
// - `ufbxt_hash_pod_imp` hashes the raw little-endian bytes of a 1/2/4/8-byte
//   value in reverse memory order on LE machines (`reverse = !big_endian`),
//   i.e. most-significant byte of the VALUE first, independent of platform.
// - `ufbx_real` is `double` in the golden build (UFBX_REAL_IS_FLOAT not set),
//   so every `hash_real` is an 8-byte pod.
// - NaN is canonicalized: float NaN -> UINT32_MAX bytes, double/real NaN ->
//   UINT64_MAX bytes. -0.0 and ±Inf are hashed as their normal bit patterns.
// - `size_t` -> `int` per PORTING_NOTES; hashed as an 8-byte unsigned value.
// - `hash_string` hashes `length` (8 bytes) then `length + 1` data bytes: the
//   trailing NUL terminator IS hashed. Strings are the raw-byte representation
//   (`UfbxiRawStr`, one char per byte), so the feed is `(byte)v[i]` with no
//   re-encoding; a C# null maps to C's `ufbx_empty_string` and hashes one NUL.
// - C enums are 4-byte ints; C `bool` is 1 byte (0/1); `int64_t` pods are 8 bytes.
// - Element/pointer references are hashed as `element_id` (uint32) or
//   UINT32_MAX when null (`ufbxt_hash_element_ref_imp`).
// - The C dump machinery (push/pop tags, hex dumps) has no effect on the hash
//   state and is intentionally not ported.
// - `ufbxt_hash_real(h, v->adjust_mirror_axis)` (hash_scene.h:451) converts the
//   C `ufbx_mirror_axis` enum to `ufbx_real` at the call site; mirrored as
//   `(double)value` (hash_scene.h:451, ufbx.h:951).
//
// Conventions this port imposes (the C# model deviates from C raw memory here;
// the loader must produce these representations for bit-exactness):
// - `UfbxShaderTextureInput.Prop/TextureProp/TextureEnabledProp` are value-type
//   `UfbxProp` structs (C: nullable `ufbx_prop*`); a missing prop is represented
//   by `Name == null` (C: `prop == NULL`).
// - `UfbxDomValue.ValueBlob` for `ARRAY_F32`/`ARRAY_F64` is raw little-endian
//   element data (matches C: pointer to packed float/double array).
//   For `ARRAY_BLOB` C stores a packed array of `ufbx_blob` {data,size} structs
//   (pointers!), which a byte[] cannot express; this hash reads records encoded
//   as [8-byte LE size][size bytes payload] repeated `count` times, which
//   reproduces the same hash bytes C produces for each entry. See report gap G2.

using System;
using System.Buffers.Binary;
using System.Text;

namespace Ufbx
{
    public sealed class UfbxHashScene
    {
        // C: ufbxt_hash_init: h->state = UINT64_C(0xcbf29ce484222325)
        public const ulong InitialState = 0xcbf29ce484222325UL;

        // C: UINT64_C(0x00000100000001B3) (FNV-1a 64-bit prime)
        private const ulong FnvPrime = 0x00000100000001B3UL;

        private ulong _state;

        public UfbxHashScene()
        {
            _state = InitialState;
        }

        public ulong State => _state; // C: h->state

        // C: ufbxt_hash_scene(v, NULL)
        public static ulong HashScene(UfbxScene scene)
        {
            var h = new UfbxHashScene();
            h.HashSceneImp(scene);
            return h._state;
        }

        // -- Core mixing (C: ufbxt_hash_data / ufbxt_hash_endian_data)

        // C: ufbxt_hash_data(h, data, size)
        internal void HashData(byte[] data, int offset, int size)
        {
            ulong state = _state;
            for (int i = 0; i < size; i++)
            {
                state = (state ^ (ulong)data[offset + i]) * FnvPrime;
            }
            _state = state;
        }

        // C: ufbxt_hash_pod_imp -- raw bytes of a 1/2/4/8-byte value,
        // most-significant byte of the value first.
        private void HashValueBytes(ulong value, int size)
        {
            ulong state = _state;
            for (int shift = (size - 1) * 8; shift >= 0; shift -= 8)
            {
                state = (state ^ ((value >> shift) & 0xffUL)) * FnvPrime;
            }
            _state = state;
        }

        internal void HashBool(bool v) // C: ufbxt_hash_pod on `bool` (sizeof == 1)
        {
            HashValueBytes(v ? 1UL : 0UL, 1);
        }

        internal void HashU32(uint v) // C: ufbxt_hash_pod on uint32_t / enums (int)
        {
            HashValueBytes(v, 4);
        }

        internal void HashI32(int v) // C: ufbxt_hash_pod on int32_t
        {
            HashValueBytes(unchecked((uint)v), 4);
        }

        internal void HashU64(ulong v) // C: ufbxt_hash_pod on uint64_t / int64_t
        {
            HashValueBytes(v, 8);
        }

        internal void HashSizeT(int v) // C: ufbxt_hash_size_t_imp (size_t -> uint64_t)
        {
            HashU64(unchecked((ulong)v));
        }

        // -- Numeric leaves

        // C: ufbxt_hash_float_imp -- `v == v` is false for qNaN and sNaN alike;
        // BitConverter keeps the raw bits, so no quieting happens here either.
        internal void HashFloat(float v)
        {
            if (!float.IsNaN(v))
            {
                HashU32(unchecked((uint)BitConverter.SingleToInt32Bits(v)));
            }
            else
            {
                HashU32(uint.MaxValue);
            }
        }

        // C: ufbxt_hash_double_imp
        internal void HashDouble(double v)
        {
            if (!double.IsNaN(v))
            {
                HashU64(unchecked((ulong)BitConverter.DoubleToInt64Bits(v)));
            }
            else
            {
                HashU64(ulong.MaxValue);
            }
        }

        // C: ufbxt_hash_real_imp. `ufbx_real` is `double` in the golden build,
        // so this is the same 8-byte pod with UINT64_MAX NaN canonicalization.
        internal void HashReal(double v)
        {
            if (!double.IsNaN(v))
            {
                HashU64(unchecked((ulong)BitConverter.DoubleToInt64Bits(v)));
            }
            else
            {
                HashU64(ulong.MaxValue);
            }
        }

        // -- Geometric leaves (C: ufbxt_hash_vec2/3/4/quat/transform/matrix_imp)

        internal void HashVec2(UfbxVec2 v)
        {
            HashReal(v.X);
            HashReal(v.Y);
        }

        internal void HashVec3(UfbxVec3 v)
        {
            HashReal(v.X);
            HashReal(v.Y);
            HashReal(v.Z);
        }

        internal void HashVec4(UfbxVec4 v)
        {
            HashReal(v.X);
            HashReal(v.Y);
            HashReal(v.Z);
            HashReal(v.W);
        }

        internal void HashQuat(UfbxQuat v)
        {
            HashReal(v.X);
            HashReal(v.Y);
            HashReal(v.Z);
            HashReal(v.W);
        }

        internal void HashTransform(UfbxTransform v) // C: ufbxt_hash_transform_imp
        {
            HashVec3(v.Translation);
            HashQuat(v.Rotation);
            HashVec3(v.Scale);
        }

        // C: ufbxt_hash_matrix_imp -- cols[0..3], each hashed as vec3.
        internal void HashMatrix(UfbxMatrix v)
        {
            HashVec3(v.GetCol(0));
            HashVec3(v.GetCol(1));
            HashVec3(v.GetCol(2));
            HashVec3(v.GetCol(3));
        }

        // -- String / blob / reference leaves

        // C: ufbxt_hash_string_imp -- hash length (size_t), then length+1 data bytes
        // including the trailing NUL. The port's `ufbx_string` is a raw-byte string
        // (one char per byte, `UfbxiRawStr` in Util/Utf8.cs), so `(byte)v[i]` is
        // exactly C's `(uint8_t)data[i]`; re-encoding as UTF-8 here would shift every
        // non-ASCII name's hash.
        internal void HashString(string v)
        {
            int length = v?.Length ?? 0;
            HashSizeT(length);
            ulong state = _state;
            for (int i = 0; i < length; i++)
            {
                state = (state ^ (ulong)(byte)v[i]) * FnvPrime;
            }
            state *= FnvPrime; // the NUL terminator
            _state = state;
        }

        // C: ufbxt_hash_blob_imp -- hash size (size_t), then exactly size bytes.
        internal void HashBlob(byte[] v)
        {
            int size = v?.Length ?? 0;
            HashSizeT(size);
            if (size > 0)
            {
                HashData(v, 0, size);
            }
        }

        // C: ufbxt_hash_element_ref_imp
        private void HashElementRefValue(UfbxElement elem)
        {
            uint id = elem != null ? elem.ElementId : uint.MaxValue;
            HashU32(id);
        }

        internal void HashElementRef(UfbxElement elem) // tagged variant
        {
            HashElementRefValue(elem);
        }

        // C: ufbxt_hash_prop_ref_imp. See header note: value-type UfbxProp in
        // this port, `Name == null` represents the C NULL pointer.
        internal void HashPropRef(UfbxProp v)
        {
            if (v.Name == null)
            {
                HashU32(uint.MaxValue);
            }
            else
            {
                HashString(v.Name);
            }
        }

        // C: ufbxt_hash_connection_imp
        private void HashConnectionValue(UfbxConnection v)
        {
            HashElementRefValue(v.Src);
            HashElementRefValue(v.Dst);
            HashString(v.SrcProp);
            HashString(v.DstProp);
        }

        // -- List helpers (C: ufbxt_hash_list / ufbxt_hash_list_ptr / ufbxt_hash_array)
        // Bits: hash count as size_t (list/array-of-known-length skip the count
        // only for `hash_array`), then each element with the element function.

        private static int Count<T>(T[] a) => a?.Length ?? 0;

        // C: ufbxt_hash_list(h, v, func_imp) -- count + items by value.
        // The per-item hash is inlined at each call site to keep the exact order.

        // -- Properties (C: ufbxt_hash_prop_imp / ufbxt_hash_props_imp)

        private void HashPropValue(in UfbxProp v)
        {
            HashString(v.Name);
            HashU32(v.InternalKey);          // C: uint32_t _internal_key
            HashU32(unchecked((uint)(int)v.Type));   // C: enum (int)
            HashU32(unchecked((uint)(int)v.Flags));  // C: enum (int)
            HashString(v.ValueStr);
            HashBlob(v.ValueBlob);
            HashU64(unchecked((ulong)v.ValueInt));   // C: int64_t
            HashVec4(v.ValueVec4);
        }

        internal void HashProp(UfbxProp v)
        {
            HashPropValue(v);
        }

        // C: ufbxt_hash_props_imp
        internal void HashProps(UfbxProps v)
        {
            int count = Count(v.Props);
            HashSizeT(count);
            for (int i = 0; i < count; i++)
            {
                HashPropValue(v.Props[i]);
            }
            HashSizeT(v.NumAnimated);
            if (v.Defaults != null)
            {
                HashProps(v.Defaults);
            }
        }

        // -- DOM (C: ufbxt_hash_dom_value_imp / ufbxt_hash_dom_node_imp)

        internal void HashDomValue(UfbxDomValue v)
        {
            HashU32(unchecked((uint)(int)v.Type));
            HashString(v.ValueStr);
            int count = unchecked((int)v.ValueInt); // C: size_t count = (size_t)v->value_int
            byte[] blob = v.ValueBlob;
            if (v.Type == UfbxDomValueType.ArrayBlob)
            {
                // C reinterprets the blob bytes as packed `ufbx_blob` structs
                // {data,size}. See header note G2 for the byte[] encoding
                // convention: [8-byte LE size][size bytes] per entry.
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    uint size = (uint)BinaryPrimitives.ReadInt64LittleEndian(
                        new ReadOnlySpan<byte>(blob, offset, 8));
                    offset += 8;
                    HashSizeT(unchecked((int)size));
                    HashData(blob, offset, unchecked((int)size));
                    offset += unchecked((int)size);
                }
            }
            else if (v.Type == UfbxDomValueType.ArrayF32)
            {
                for (int i = 0; i < count; i++)
                {
                    float f = BinaryPrimitives.ReadSingleLittleEndian(
                        new ReadOnlySpan<byte>(blob, i * 4, 4));
                    HashFloat(f);
                }
            }
            else if (v.Type == UfbxDomValueType.ArrayF64)
            {
                for (int i = 0; i < count; i++)
                {
                    double d = BinaryPrimitives.ReadDoubleLittleEndian(
                        new ReadOnlySpan<byte>(blob, i * 8, 8));
                    HashDouble(d);
                }
            }
            else
            {
                HashBlob(blob);
            }
            HashU64(unchecked((ulong)v.ValueInt));
            HashDouble(v.ValueFloat);
        }

        internal void HashDomNode(UfbxDomNode v)
        {
            HashString(v.Name);
            int childCount = Count(v.Children);
            HashSizeT(childCount);
            for (int i = 0; i < childCount; i++)
            {
                HashDomNode(v.Children[i]);
            }
            int valueCount = Count(v.Values);
            HashSizeT(valueCount);
            for (int i = 0; i < valueCount; i++)
            {
                HashDomValue(v.Values[i]);
            }
        }

        // -- Element base (C: ufbxt_hash_element_imp)

        internal void HashElement(UfbxElement v)
        {
            HashString(v.Name);
            HashProps(v.Props);
            HashU32(v.ElementId);
            HashU32(v.TypedId);
            int instanceCount = Count(v.Instances);
            HashSizeT(instanceCount);
            for (int i = 0; i < instanceCount; i++)
            {
                HashElementRefValue(v.Instances[i]);
            }
            HashU32(unchecked((uint)(int)v.Type));
            int srcCount = Count(v.ConnectionsSrc);
            HashSizeT(srcCount);
            for (int i = 0; i < srcCount; i++)
            {
                HashConnectionValue(v.ConnectionsSrc[i]);
            }
            int dstCount = Count(v.ConnectionsDst);
            HashSizeT(dstCount);
            for (int i = 0; i < dstCount; i++)
            {
                HashConnectionValue(v.ConnectionsDst[i]);
            }
        }

        // C: ufbxt_hash_unknown_imp
        internal void HashUnknown(UfbxUnknown v)
        {
            HashString(v.FbxType); // C: v->type
            HashString(v.SuperType);
            HashString(v.SubType);
        }

        // -- Node (C: ufbxt_hash_node_imp, hash_scene.h:422-462)

        internal void HashNode(UfbxNode v)
        {
            HashElementRef(v.Parent);
            int childCount = Count(v.Children);
            HashSizeT(childCount);
            for (int i = 0; i < childCount; i++)
            {
                HashElementRefValue(v.Children[i]);
            }
            HashElementRef(v.Mesh);
            HashElementRef(v.Light);
            HashElementRef(v.Camera);
            HashElementRef(v.Bone);
            HashElementRef(v.Attrib);
            HashElementRef(v.GeometryTransformHelper);
            HashElementRef(v.ScaleHelper);
            HashU32(unchecked((uint)(int)v.AttribType));
            int attribCount = Count(v.AllAttribs);
            HashSizeT(attribCount);
            for (int i = 0; i < attribCount; i++)
            {
                HashElementRefValue(v.AllAttribs[i]);
            }
            HashU32(unchecked((uint)(int)v.InheritMode));
            HashU32(unchecked((uint)(int)v.OriginalInheritMode));
            HashTransform(v.LocalTransform);
            HashTransform(v.GeometryTransform);
            HashVec3(v.InheritScale);
            HashU32(unchecked((uint)(int)v.RotationOrder));
            HashVec3(v.EulerRotation);
            HashMatrix(v.NodeToParent);
            HashMatrix(v.NodeToWorld);
            HashMatrix(v.GeometryToNode);
            HashMatrix(v.GeometryToWorld);
            HashMatrix(v.UnscaledNodeToWorld);
            HashQuat(v.AdjustPreRotation);
            HashReal(v.AdjustPreScale);
            HashQuat(v.AdjustPostRotation);
            HashReal(v.AdjustPostScale);
            // C: ufbxt_hash_real(h, v->adjust_mirror_axis) -- enum converted to
            // ufbx_real at the C call site (hash_scene.h:451).
            HashReal((double)v.AdjustMirrorAxis);
            HashBool(v.Visible);
            HashBool(v.IsRoot);
            HashBool(v.HasGeometryTransform);
            HashBool(v.HasRootAdjustTransform);
            HashBool(v.HasAdjustTransform);
            HashBool(v.IsGeometryTransformHelper);
            HashBool(v.IsScaleHelper);
            HashBool(v.IsScaleCompensateParent);
            HashU32(v.NodeDepth);
            int materialCount = Count(v.Materials);
            HashSizeT(materialCount);
            for (int i = 0; i < materialCount; i++)
            {
                HashElementRefValue(v.Materials[i]);
            }
        }

        // -- Vertex attributes (C: ufbxt_hash_vertex_*_imp, hash_scene.h:464-507)

        private void HashVertexReal(UfbxVertexReal v)
        {
            HashBool(v.Exists);
            int valueCount = Count(v.Values);
            HashSizeT(valueCount);
            for (int i = 0; i < valueCount; i++)
            {
                HashReal(v.Values[i]);
            }
            int indexCount = Count(v.Indices);
            HashSizeT(indexCount);
            for (int i = 0; i < indexCount; i++)
            {
                HashU32(v.Indices[i]);
            }
            HashSizeT(v.ValueReals);
            HashBool(v.UniquePerVertex);
            int wCount = Count(v.ValuesW);
            HashSizeT(wCount);
            for (int i = 0; i < wCount; i++)
            {
                HashReal(v.ValuesW[i]);
            }
        }

        private void HashVertexVec2(UfbxVertexVec2 v)
        {
            HashBool(v.Exists);
            int valueCount = Count(v.Values);
            HashSizeT(valueCount);
            for (int i = 0; i < valueCount; i++)
            {
                HashVec2(v.Values[i]);
            }
            int indexCount = Count(v.Indices);
            HashSizeT(indexCount);
            for (int i = 0; i < indexCount; i++)
            {
                HashU32(v.Indices[i]);
            }
            HashSizeT(v.ValueReals);
            HashBool(v.UniquePerVertex);
            int wCount = Count(v.ValuesW);
            HashSizeT(wCount);
            for (int i = 0; i < wCount; i++)
            {
                HashReal(v.ValuesW[i]);
            }
        }

        private void HashVertexVec3(UfbxVertexVec3 v)
        {
            HashBool(v.Exists);
            int valueCount = Count(v.Values);
            HashSizeT(valueCount);
            for (int i = 0; i < valueCount; i++)
            {
                HashVec3(v.Values[i]);
            }
            int indexCount = Count(v.Indices);
            HashSizeT(indexCount);
            for (int i = 0; i < indexCount; i++)
            {
                HashU32(v.Indices[i]);
            }
            HashSizeT(v.ValueReals);
            HashBool(v.UniquePerVertex);
            int wCount = Count(v.ValuesW);
            HashSizeT(wCount);
            for (int i = 0; i < wCount; i++)
            {
                HashReal(v.ValuesW[i]);
            }
        }

        private void HashVertexVec4(UfbxVertexVec4 v)
        {
            HashBool(v.Exists);
            int valueCount = Count(v.Values);
            HashSizeT(valueCount);
            for (int i = 0; i < valueCount; i++)
            {
                HashVec4(v.Values[i]);
            }
            int indexCount = Count(v.Indices);
            HashSizeT(indexCount);
            for (int i = 0; i < indexCount; i++)
            {
                HashU32(v.Indices[i]);
            }
            HashSizeT(v.ValueReals);
            HashBool(v.UniquePerVertex);
            int wCount = Count(v.ValuesW);
            HashSizeT(wCount);
            for (int i = 0; i < wCount; i++)
            {
                HashReal(v.ValuesW[i]);
            }
        }

        // C: ufbxt_hash_uv_set_imp / ufbxt_hash_color_set_imp
        private void HashUvSet(UfbxUvSet v)
        {
            HashString(v.Name);
            HashU32(v.Index);
            HashVertexVec2(v.VertexUv);
            HashVertexVec3(v.VertexTangent);
            HashVertexVec3(v.VertexBitangent);
        }

        private void HashColorSet(UfbxColorSet v)
        {
            HashString(v.Name);
            HashU32(v.Index);
            HashVertexVec4(v.VertexColor);
        }

        // C: ufbxt_hash_mesh_part_imp
        private void HashMeshPart(UfbxMeshPart v)
        {
            HashU32(v.Index);
            HashSizeT(v.NumFaces);
            HashSizeT(v.NumTriangles);
            HashSizeT(v.NumEmptyFaces);
            HashSizeT(v.NumPointFaces);
            HashSizeT(v.NumLineFaces);
            int faceCount = Count(v.FaceIndices);
            HashSizeT(faceCount);
            for (int i = 0; i < faceCount; i++)
            {
                HashU32(v.FaceIndices[i]);
            }
        }

        // C: ufbxt_hash_face_group_imp
        private void HashFaceGroup(UfbxFaceGroup v)
        {
            HashI32(v.Id);
            HashString(v.Name);
        }

        // C: ufbxt_hash_subdivision_weight_range_imp / weight / result
        private void HashSubdivisionWeightRange(UfbxSubdivisionWeightRange v)
        {
            HashU32(v.WeightBegin);
            HashU32(v.NumWeights);
        }

        private void HashSubdivisionWeight(UfbxSubdivisionWeight v)
        {
            HashReal(v.Weight);
            HashU32(v.Index);
        }

        internal void HashSubdivisionResult(UfbxSubdivisionResult v)
        {
            int n;
            n = Count(v.SourceVertexRanges);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSubdivisionWeightRange(v.SourceVertexRanges[i]);
            n = Count(v.SourceVertexWeights);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSubdivisionWeight(v.SourceVertexWeights[i]);
            n = Count(v.SkinClusterRanges);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSubdivisionWeightRange(v.SkinClusterRanges[i]);
            n = Count(v.SkinClusterWeights);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSubdivisionWeight(v.SkinClusterWeights[i]);
        }

        // C: ufbxt_hash_face_imp / ufbxt_hash_edge_imp
        private void HashFace(UfbxFace v)
        {
            HashU32(v.IndexBegin);
            HashU32(v.NumIndices);
        }

        private void HashEdge(UfbxEdge v)
        {
            HashU32(v.A);
            HashU32(v.B);
        }

        // -- Mesh (C: ufbxt_hash_mesh_imp, hash_scene.h:576-638)

        internal void HashMesh(UfbxMesh v)
        {
            HashSizeT(v.NumVertices);
            HashSizeT(v.NumIndices);
            HashSizeT(v.NumFaces);
            HashSizeT(v.NumTriangles);
            HashSizeT(v.NumEdges);

            int n;
            n = Count(v.Faces);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashFace(v.Faces[i]);
            n = Count(v.FaceSmoothing);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBool(v.FaceSmoothing[i]);
            n = Count(v.FaceMaterial);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.FaceMaterial[i]);
            n = Count(v.FaceGroup);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.FaceGroup[i]);
            n = Count(v.FaceHole);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBool(v.FaceHole[i]);
            HashSizeT(v.MaxFaceTriangles);
            HashSizeT(v.NumEmptyFaces);
            HashSizeT(v.NumPointFaces);
            HashSizeT(v.NumLineFaces);

            n = Count(v.Edges);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashEdge(v.Edges[i]);
            n = Count(v.EdgeSmoothing);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBool(v.EdgeSmoothing[i]);
            n = Count(v.EdgeCrease);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashReal(v.EdgeCrease[i]);
            n = Count(v.EdgeVisibility);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBool(v.EdgeVisibility[i]);

            n = Count(v.VertexIndices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.VertexIndices[i]);
            n = Count(v.Vertices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashVec3(v.Vertices[i]);
            n = Count(v.VertexFirstIndex);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.VertexFirstIndex[i]);

            HashVertexVec3(v.VertexPosition);
            HashVertexVec3(v.VertexNormal);
            HashVertexVec2(v.VertexUv);
            HashVertexVec3(v.VertexTangent);
            HashVertexVec3(v.VertexBitangent);
            HashVertexVec4(v.VertexColor);
            HashVertexReal(v.VertexCrease);

            n = Count(v.UvSets);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashUvSet(v.UvSets[i]);
            n = Count(v.ColorSets);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashColorSet(v.ColorSets[i]);

            n = Count(v.Materials);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Materials[i]);
            n = Count(v.FaceGroups);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashFaceGroup(v.FaceGroups[i]);

            n = Count(v.MaterialParts);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashMeshPart(v.MaterialParts[i]);
            n = Count(v.FaceGroupParts);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashMeshPart(v.FaceGroupParts[i]);

            HashBool(v.SkinnedIsLocal);
            HashVertexVec3(v.SkinnedPosition);
            HashVertexVec3(v.SkinnedNormal);

            n = Count(v.SkinDeformers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.SkinDeformers[i]);
            n = Count(v.BlendDeformers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.BlendDeformers[i]);
            n = Count(v.CacheDeformers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.CacheDeformers[i]);
            n = Count(v.AllDeformers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.AllDeformers[i]);

            HashU32(v.SubdivisionPreviewLevels);
            HashU32(v.SubdivisionRenderLevels);
            HashU32(unchecked((uint)(int)v.SubdivisionDisplayMode));
            HashU32(unchecked((uint)(int)v.SubdivisionBoundary));
            HashU32(unchecked((uint)(int)v.SubdivisionUvBoundary));

            HashBool(v.SubdivisionEvaluated);
            if (v.SubdivisionResult != null) HashSubdivisionResult(v.SubdivisionResult);
            HashBool(v.FromTessellatedNurbs);
        }

        // -- Other node attributes (hash_scene.h:640-771)

        internal void HashLight(UfbxLight v)
        {
            HashVec3(v.Color);
            HashReal(v.Intensity);
            HashVec3(v.LocalDirection);
            HashU32(unchecked((uint)(int)v.Type));
            HashU32(unchecked((uint)(int)v.Decay));
            HashU32(unchecked((uint)(int)v.AreaShape));
            HashReal(v.InnerAngle);
            HashReal(v.OuterAngle);
            HashBool(v.CastLight);
            HashBool(v.CastShadows);
        }

        internal void HashCamera(UfbxCamera v)
        {
            HashBool(v.ResolutionIsPixels);
            HashVec2(v.Resolution);
            HashVec2(v.FieldOfViewDeg);
            HashVec2(v.FieldOfViewTan);
            HashU32(unchecked((uint)(int)v.AspectMode));
            HashU32(unchecked((uint)(int)v.ApertureMode));
            HashU32(unchecked((uint)(int)v.GateFit));
            HashU32(unchecked((uint)(int)v.ApertureFormat));
            HashReal(v.FocalLengthMm);
            HashVec2(v.FilmSizeInch);
            HashVec2(v.ApertureSizeInch);
            HashReal(v.SqueezeRatio);
        }

        internal void HashBone(UfbxBone v)
        {
            HashReal(v.Radius);
            HashReal(v.RelativeLength);
            HashBool(v.IsRoot);
        }

        internal void HashEmpty(UfbxEmpty v)
        {
            // C: ufbxt_hash_empty_imp -- no own fields.
        }

        // C: ufbxt_hash_nurbs_basis_imp
        private void HashNurbsBasis(UfbxNurbsBasis v)
        {
            HashU32(v.Order);
            HashU32(unchecked((uint)(int)v.Topology));
            int n;
            n = Count(v.KnotVector);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashReal(v.KnotVector[i]);
            HashReal(v.TMin);
            HashReal(v.TMax);
            n = Count(v.Spans);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashReal(v.Spans[i]);
            HashBool(v.Is2D);
            HashSizeT(v.NumWrapControlPoints);
            HashBool(v.Valid);
        }

        // C: ufbxt_hash_line_segment_imp
        private void HashLineSegment(UfbxLineSegment v)
        {
            HashU32(v.IndexBegin);
            HashU32(v.NumIndices);
        }

        internal void HashLineCurve(UfbxLineCurve v)
        {
            HashVec3(v.Color);
            int n;
            n = Count(v.ControlPoints);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashVec3(v.ControlPoints[i]);
            n = Count(v.PointIndices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.PointIndices[i]);
            n = Count(v.Segments);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashLineSegment(v.Segments[i]);
            HashBool(v.FromTessellatedNurbs);
        }

        internal void HashNurbsCurve(UfbxNurbsCurve v)
        {
            HashNurbsBasis(v.Basis);
            int n = Count(v.ControlPoints);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashVec4(v.ControlPoints[i]);
        }

        internal void HashNurbsSurface(UfbxNurbsSurface v)
        {
            HashNurbsBasis(v.BasisU);
            HashNurbsBasis(v.BasisV);
            HashSizeT(v.NumControlPointsU);
            HashSizeT(v.NumControlPointsV);
            int n = Count(v.ControlPoints);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashVec4(v.ControlPoints[i]);
            HashU32(v.SpanSubdivisionU);
            HashU32(v.SpanSubdivisionV);
            HashBool(v.FlipNormals);
            HashElementRef(v.Material);
        }

        internal void HashNurbsTrimSurface(UfbxNurbsTrimSurface v) { }
        internal void HashNurbsTrimBoundary(UfbxNurbsTrimBoundary v) { }
        internal void HashProceduralGeometry(UfbxProceduralGeometry v) { }

        internal void HashStereoCamera(UfbxStereoCamera v)
        {
            HashElementRef(v.Left);
            HashElementRef(v.Right);
        }

        internal void HashCameraSwitcher(UfbxCameraSwitcher v) { }

        internal void HashMarker(UfbxMarker v)
        {
            HashU32(unchecked((uint)(int)v.Type));
        }

        // C: ufbxt_hash_lod_level_imp
        private void HashLodLevel(UfbxLodLevel v)
        {
            HashReal(v.Distance);
            HashU32(unchecked((uint)(int)v.Display));
        }

        internal void HashLodGroup(UfbxLodGroup v)
        {
            HashBool(v.RelativeDistances);
            int n = Count(v.LodLevels);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashLodLevel(v.LodLevels[i]);
            HashBool(v.IgnoreParentTransform);
            HashBool(v.UseDistanceLimit);
            HashReal(v.DistanceLimitMin);
            HashReal(v.DistanceLimitMax);
        }

        // -- Deformers (hash_scene.h:773-881)

        // C: ufbxt_hash_skin_vertex_imp / ufbxt_hash_skin_weight_imp
        private void HashSkinVertex(UfbxSkinVertex v)
        {
            HashU32(v.WeightBegin);
            HashU32(v.NumWeights);
            HashReal(v.DqWeight);
        }

        private void HashSkinWeight(UfbxSkinWeight v)
        {
            HashU32(v.ClusterIndex);
            HashReal(v.Weight);
        }

        internal void HashSkinDeformer(UfbxSkinDeformer v)
        {
            HashU32(unchecked((uint)(int)v.SkinningMethod));
            int n;
            n = Count(v.Clusters);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Clusters[i]);
            n = Count(v.Vertices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSkinVertex(v.Vertices[i]);
            n = Count(v.Weights);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSkinWeight(v.Weights[i]);
            HashSizeT(v.MaxWeightsPerVertex);
            HashSizeT(v.NumDqWeights);
            n = Count(v.DqVertices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.DqVertices[i]);
            n = Count(v.DqWeights);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashReal(v.DqWeights[i]);
        }

        internal void HashSkinCluster(UfbxSkinCluster v)
        {
            HashElementRef(v.BoneNode);
            HashMatrix(v.GeometryToBone);
            HashMatrix(v.MeshNodeToBone);
            HashMatrix(v.BindToWorld);
            HashMatrix(v.GeometryToWorld);
            HashTransform(v.GeometryToWorldTransform);
            HashSizeT(v.NumWeights);
            int n;
            n = Count(v.Vertices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.Vertices[i]);
            n = Count(v.Weights);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashReal(v.Weights[i]);
        }

        internal void HashBlendDeformer(UfbxBlendDeformer v)
        {
            int n = Count(v.Channels);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Channels[i]);
        }

        // C: ufbxt_hash_blend_keyframe_imp
        private void HashBlendKeyframe(UfbxBlendKeyframe v)
        {
            HashElementRef(v.Shape);
            HashReal(v.TargetWeight);
            HashReal(v.EffectiveWeight);
        }

        internal void HashBlendChannel(UfbxBlendChannel v)
        {
            HashReal(v.Weight);
            int n = Count(v.Keyframes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBlendKeyframe(v.Keyframes[i]);
        }

        internal void HashBlendShape(UfbxBlendShape v)
        {
            HashSizeT(v.NumOffsets);
            int n;
            n = Count(v.OffsetVertices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.OffsetVertices[i]);
            n = Count(v.PositionOffsets);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashVec3(v.PositionOffsets[i]);
            n = Count(v.NormalOffsets);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashVec3(v.NormalOffsets[i]);
        }

        // -- Geometry cache (hash_scene.h:837-881)

        internal void HashCacheFrame(UfbxCacheFrame v)
        {
            HashString(v.Channel);
            HashDouble(v.Time);
            HashU32(unchecked((uint)(int)v.FileFormat));
            HashU32(unchecked((uint)(int)v.DataFormat));
            HashU32(unchecked((uint)(int)v.DataEncoding));
            HashU64(v.DataOffset);
            HashU32(v.DataCount);
            HashU32(v.DataElementBytes);
            HashU64(v.DataTotalBytes);
        }

        internal void HashCacheChannel(UfbxCacheChannel v)
        {
            HashString(v.Name);
            HashU32(unchecked((uint)(int)v.Interpretation));
            HashString(v.InterpretationName);
            int n = Count(v.Frames);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCacheFrame(v.Frames[i]);
        }

        internal void HashGeometryCache(UfbxGeometryCache v)
        {
            int n;
            n = Count(v.Channels);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCacheChannel(v.Channels[i]);
            n = Count(v.Frames);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCacheFrame(v.Frames[i]);
            n = Count(v.ExtraInfo);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashString(v.ExtraInfo[i]);
        }

        internal void HashCacheDeformer(UfbxCacheDeformer v)
        {
            HashString(v.Channel);
            HashElementRef(v.File);
        }

        internal void HashCacheFile(UfbxCacheFile v)
        {
            HashString(v.AbsoluteFilename);
            HashString(v.RelativeFilename);
            HashBlob(v.RawAbsoluteFilename);
            HashBlob(v.RawRelativeFilename);
            if (v.ExternalCache != null) HashGeometryCache(v.ExternalCache);
        }

        // -- Materials (hash_scene.h:883-938)

        // C: ufbxt_hash_material_map_imp
        private void HashMaterialMap(UfbxMaterialMap v)
        {
            HashVec4(v.ValueVec4);
            HashU64(unchecked((ulong)v.ValueInt)); // C: int64_t
            HashElementRef(v.Texture);
            HashBool(v.HasValue);
            HashBool(v.TextureEnabled);
            HashValueBytes(v.ValueComponents, 1); // C: uint8_t
        }

        // C: ufbxt_hash_material_feature_imp
        private void HashMaterialFeature(UfbxMaterialFeatureInfo v)
        {
            HashBool(v.Enabled);
            HashBool(v.IsExplicit);
        }

        // C: ufbxt_hash_material_texture_imp
        private void HashMaterialTexture(UfbxMaterialTexture v)
        {
            HashString(v.MaterialProp);
            HashString(v.ShaderProp);
            HashElementRef(v.Texture);
        }

        internal void HashMaterial(UfbxMaterial v)
        {
            for (int i = 0; i < UfbxEnumCounts.UfbxMaterialFbxMap; i++)
            {
                HashMaterialMap(v.Fbx.Maps[i]);
            }
            for (int i = 0; i < UfbxEnumCounts.UfbxMaterialPbrMap; i++)
            {
                HashMaterialMap(v.Pbr.Maps[i]);
            }
            for (int i = 0; i < UfbxEnumCounts.UfbxMaterialFeature; i++)
            {
                HashMaterialFeature(v.Features.Features[i]);
            }
            HashU32(unchecked((uint)(int)v.ShaderType));
            HashElementRef(v.Shader);
            HashString(v.ShadingModelName);
            HashString(v.ShaderPropPrefix);
            int n = Count(v.Textures);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashMaterialTexture(v.Textures[i]);
        }

        // -- Textures / shaders (hash_scene.h:940-1022)

        // C: ufbxt_hash_texture_layer_imp
        private void HashTextureLayer(UfbxTextureLayer v)
        {
            HashElementRef(v.Texture);
            HashU32(unchecked((uint)(int)v.BlendMode));
            HashReal(v.Alpha);
        }

        // C: ufbxt_hash_shader_texture_input_imp
        private void HashShaderTextureInput(UfbxShaderTextureInput v)
        {
            HashString(v.Name);
            HashVec4(v.ValueVec4);
            HashU64(unchecked((ulong)v.ValueInt)); // C: int64_t
            HashString(v.ValueStr);
            HashBlob(v.ValueBlob);
            HashElementRef(v.Texture);
            HashBool(v.TextureEnabled);
            HashPropRef(v.Prop);
            HashPropRef(v.TextureProp);
            HashPropRef(v.TextureEnabledProp);
        }

        internal void HashShaderTexture(UfbxShaderTexture v)
        {
            HashU32(unchecked((uint)(int)v.Type));
            HashString(v.ShaderName);
            HashU64(v.ShaderTypeId);
            int n = Count(v.Inputs);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashShaderTextureInput(v.Inputs[i]);
            HashString(v.ShaderSource);
            HashBlob(v.RawShaderSource);
            HashElementRef(v.MainTexture);
            HashU64(unchecked((ulong)v.MainTextureOutputIndex)); // C: int64_t
            HashString(v.PropPrefix);
        }

        internal void HashTexture(UfbxTexture v)
        {
            HashU32(unchecked((uint)(int)v.Type));
            HashString(v.AbsoluteFilename);
            HashString(v.RelativeFilename);
            HashBlob(v.RawAbsoluteFilename);
            HashBlob(v.RawRelativeFilename);
            HashBlob(v.Content);
            HashElementRef(v.Video);
            HashU32(v.FileIndex);
            HashBool(v.HasFile);
            int n = Count(v.Layers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashTextureLayer(v.Layers[i]);
            HashString(v.UvSet);
            HashU32(unchecked((uint)(int)v.WrapU));
            HashU32(unchecked((uint)(int)v.WrapV));
            HashBool(v.HasUvTransform);
            HashTransform(v.UvTransform);
            HashMatrix(v.TextureToUv);
            HashMatrix(v.UvToTexture);
            n = Count(v.FileTextures);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.FileTextures[i]);
            if (v.Shader != null) HashShaderTexture(v.Shader);
        }

        internal void HashVideo(UfbxVideo v)
        {
            HashString(v.AbsoluteFilename);
            HashString(v.RelativeFilename);
            HashBlob(v.RawAbsoluteFilename);
            HashBlob(v.RawRelativeFilename);
            HashBlob(v.Content);
        }

        internal void HashShader(UfbxShader v)
        {
            HashU32(unchecked((uint)(int)v.Type));
            int n = Count(v.Bindings);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Bindings[i]);
        }

        // C: ufbxt_hash_shader_prop_binding_imp
        private void HashShaderPropBinding(UfbxShaderPropBinding v)
        {
            HashString(v.ShaderProp);
            HashString(v.MaterialProp);
        }

        internal void HashShaderBinding(UfbxShaderBinding v)
        {
            int n = Count(v.PropBindings);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashShaderPropBinding(v.PropBindings[i]);
        }

        // -- Animation (hash_scene.h:1035-1132)

        // C: ufbxt_hash_prop_override_imp
        private void HashPropOverride(UfbxPropOverride v)
        {
            HashU32(v.ElementId);
            HashU32(v.InternalKey); // C: uint32_t _internal_key
            HashString(v.PropName);
            HashVec4(v.Value);
            HashString(v.ValueStr);
            HashU64(unchecked((ulong)v.ValueInt)); // C: int64_t
        }

        // C: ufbxt_hash_transform_override_imp
        private void HashTransformOverride(UfbxTransformOverride v)
        {
            HashU32(v.NodeId);
            HashTransform(v.Transform);
        }

        internal void HashAnim(UfbxAnim v)
        {
            HashDouble(v.TimeBegin);
            HashDouble(v.TimeEnd);
            int n;
            n = Count(v.Layers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Layers[i]);
            n = Count(v.OverrideLayerWeights);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU64(unchecked((ulong)BitConverter.DoubleToInt64Bits(v.OverrideLayerWeights[i])));
            n = Count(v.PropOverrides);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashPropOverride(v.PropOverrides[i]);
            n = Count(v.TransformOverrides);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashTransformOverride(v.TransformOverrides[i]);
            HashBool(v.IgnoreConnections);
            HashBool(v.Custom);
        }

        internal void HashAnimStack(UfbxAnimStack v)
        {
            HashDouble(v.TimeBegin);
            HashDouble(v.TimeEnd);
            int n = Count(v.Layers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Layers[i]);
            HashAnim(v.Anim);
        }

        // C: ufbxt_hash_anim_prop_imp
        private void HashAnimProp(UfbxAnimProp v)
        {
            HashElementRef(v.Element);
            HashU32(v.InternalKey); // C: uint32_t _internal_key
            HashString(v.PropName);
            HashElementRef(v.AnimValue);
        }

        internal void HashAnimLayer(UfbxAnimLayer v)
        {
            HashReal(v.Weight);
            HashBool(v.WeightIsAnimated);
            HashBool(v.Blended);
            HashBool(v.Additive);
            HashBool(v.ComposeRotation);
            HashBool(v.ComposeScale);
            int n = Count(v.AnimValues);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.AnimValues[i]);
            n = Count(v.AnimProps);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashAnimProp(v.AnimProps[i]);
            HashAnim(v.Anim);
            HashU32(v.MinElementId);   // C: uint32_t _min_element_id
            HashU32(v.MaxElementId);   // C: uint32_t _max_element_id
            HashU32(v.ElementIdBitmask[0]);
            HashU32(v.ElementIdBitmask[1]);
            HashU32(v.ElementIdBitmask[2]);
            HashU32(v.ElementIdBitmask[3]);
        }

        internal void HashAnimValue(UfbxAnimValue v)
        {
            HashVec3(v.DefaultValue);
            // C: ufbxt_hash_array(h, v->curves, ...) -- fixed `ufbx_anim_curve *curves[3]`.
            HashElementRefValue(v.Curves[0]);
            HashElementRefValue(v.Curves[1]);
            HashElementRefValue(v.Curves[2]);
        }

        // C: ufbxt_hash_tangent_imp
        private void HashTangent(UfbxTangent v)
        {
            HashFloat(v.Dx);
            HashFloat(v.Dy);
        }

        // C: ufbxt_hash_keyframe_imp
        private void HashKeyframe(UfbxKeyframe v)
        {
            HashDouble(v.Time);
            HashReal(v.Value);
            HashU32(unchecked((uint)(int)v.Interpolation));
            HashTangent(v.Left);
            HashTangent(v.Right);
        }

        internal void HashAnimCurve(UfbxAnimCurve v)
        {
            int n = Count(v.Keyframes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashKeyframe(v.Keyframes[i]);
            HashReal(v.MinValue);
            HashReal(v.MaxValue);
        }

        // -- Collections / misc (hash_scene.h:1134-1222)

        internal void HashDisplayLayer(UfbxDisplayLayer v)
        {
            int n = Count(v.Nodes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Nodes[i]);
            HashBool(v.Visible);
            HashBool(v.Frozen);
            HashVec3(v.UiColor);
        }

        internal void HashSelectionSet(UfbxSelectionSet v)
        {
            int n = Count(v.Nodes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Nodes[i]);
        }

        internal void HashSelectionNode(UfbxSelectionNode v)
        {
            HashElementRef(v.TargetNode);
            HashElementRef(v.TargetMesh);
            HashBool(v.IncludeNode);
            int n;
            n = Count(v.Vertices);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.Vertices[i]);
            n = Count(v.Edges);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.Edges[i]);
            n = Count(v.Faces);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashU32(v.Faces[i]);
        }

        internal void HashCharacter(UfbxCharacter v) { }

        // C: ufbxt_hash_constraint_target_imp
        private void HashConstraintTarget(UfbxConstraintTarget v)
        {
            HashElementRef(v.Node);
            HashReal(v.Weight);
            HashTransform(v.Transform);
        }

        internal void HashConstraint(UfbxConstraint v)
        {
            HashU32(unchecked((uint)(int)v.Type));
            HashString(v.TypeName);
            HashElementRef(v.Node);
            int n = Count(v.Targets);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashConstraintTarget(v.Targets[i]);
            HashReal(v.Weight);
            HashBool(v.Active);
            // C: ufbxt_hash_array over bool[3] (no count hashed, exactly 3 entries).
            HashBool(v.ConstrainTranslation[0]);
            HashBool(v.ConstrainTranslation[1]);
            HashBool(v.ConstrainTranslation[2]);
            HashBool(v.ConstrainRotation[0]);
            HashBool(v.ConstrainRotation[1]);
            HashBool(v.ConstrainRotation[2]);
            HashBool(v.ConstrainScale[0]);
            HashBool(v.ConstrainScale[1]);
            HashBool(v.ConstrainScale[2]);
            HashTransform(v.TransformOffset);
            HashVec3(v.AimVector);
            HashU32(unchecked((uint)(int)v.AimUpType));
            HashElementRef(v.AimUpNode);
            HashVec3(v.AimUpVector);
            HashElementRef(v.IkEffector);
            HashElementRef(v.IkEndNode);
            HashVec3(v.IkPoleVector);
        }

        internal void HashAudioLayer(UfbxAudioLayer v)
        {
            int n = Count(v.Clips);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElementRefValue(v.Clips[i]);
        }

        internal void HashAudioClip(UfbxAudioClip v)
        {
            HashString(v.AbsoluteFilename);
            HashString(v.RelativeFilename);
            HashBlob(v.RawAbsoluteFilename);
            HashBlob(v.RawRelativeFilename);
            HashBlob(v.Content);
        }

        // C: ufbxt_hash_bone_pose_imp
        private void HashBonePose(UfbxBonePose v)
        {
            HashElementRef(v.BoneNode);
            HashMatrix(v.BoneToWorld);
        }

        internal void HashPose(UfbxPose v)
        {
            HashBool(v.IsBindPose);
            int n = Count(v.BonePoses);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBonePose(v.BonePoses[i]);
        }

        internal void HashMetadataObject(UfbxMetadataObject v) { }

        // C: ufbxt_hash_texture_file_imp
        private void HashTextureFile(UfbxTextureFile v)
        {
            HashU32(v.Index);
            HashString(v.AbsoluteFilename);
            HashString(v.RelativeFilename);
            HashBlob(v.RawAbsoluteFilename);
            HashBlob(v.RawRelativeFilename);
            HashBlob(v.Content);
        }

        // C: ufbxt_hash_name_element_imp
        private void HashNameElement(UfbxNameElement v)
        {
            HashString(v.Name);
            HashU32(unchecked((uint)(int)v.Type));
            HashU32(v.InternalKey); // C: uint32_t _internal_key
            HashElementRef(v.Element);
        }

        // C: ufbxt_hash_application_imp
        private void HashApplication(UfbxApplication v)
        {
            HashString(v.Vendor);
            HashString(v.Name);
            HashString(v.Version);
        }

        // C: ufbxt_hash_metadata_imp (hash_scene.h:1251-1264)
        internal void HashMetadata(UfbxMetadata v)
        {
            HashBool(v.Ascii);
            HashU32(v.Version);
            HashString(v.Creator);
            HashBool(v.IsUnsafe);
            HashBool(v.BigEndian);
            HashU32(unchecked((uint)(int)v.Exporter));
            HashU32(v.ExporterVersion);
            HashProps(v.SceneProps);
            HashApplication(v.OriginalApplication);
            HashApplication(v.LatestApplication);
            // C: ufbxt_hash_array(h, v->has_warning, pod_imp) -- fixed
            // bool[UFBX_WARNING_TYPE_COUNT], one byte each, no count hashed.
            for (int i = 0; i < v.HasWarning.Length; i++)
            {
                HashBool(v.HasWarning[i]);
            }
        }

        // C: ufbxt_hash_coordinate_axes_imp
        private void HashCoordinateAxes(UfbxCoordinateAxes v)
        {
            HashU32(unchecked((uint)(int)v.Right));
            HashU32(unchecked((uint)(int)v.Up));
            HashU32(unchecked((uint)(int)v.Front));
        }

        // C: ufbxt_hash_scene_settings_imp
        internal void HashSceneSettings(UfbxSceneSettings v)
        {
            HashProps(v.Props);
            HashCoordinateAxes(v.Axes);
            HashReal(v.UnitMeters);
            HashDouble(v.FramesPerSecond);
            HashVec3(v.AmbientColor);
            HashU32(unchecked((uint)(int)v.TimeMode));
            HashU32(unchecked((uint)(int)v.TimeProtocol));
            HashU32(unchecked((uint)(int)v.SnapMode));
            HashU32(unchecked((uint)(int)v.OriginalAxisUp));
            HashReal(v.OriginalUnitMeters);
        }

        // -- Scene traversal (C: ufbxt_hash_scene_imp, hash_scene.h:1293-1352)

        internal void HashSceneImp(UfbxScene v)
        {
            HashMetadata(v.Metadata);
            HashSceneSettings(v.Settings);
            HashElementRef(v.RootNode);
            HashAnim(v.Anim);

            int n;
            n = Count(v.Elements);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashElement(v.Elements[i]);

            n = Count(v.Unknowns);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashUnknown(v.Unknowns[i]);

            n = Count(v.Nodes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashNode(v.Nodes[i]);

            n = Count(v.Meshes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashMesh(v.Meshes[i]);

            n = Count(v.Lights);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashLight(v.Lights[i]);

            n = Count(v.Cameras);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCamera(v.Cameras[i]);

            n = Count(v.Bones);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBone(v.Bones[i]);

            n = Count(v.Empties);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashEmpty(v.Empties[i]);

            n = Count(v.LineCurves);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashLineCurve(v.LineCurves[i]);

            n = Count(v.NurbsCurves);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashNurbsCurve(v.NurbsCurves[i]);

            n = Count(v.NurbsSurfaces);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashNurbsSurface(v.NurbsSurfaces[i]);

            n = Count(v.NurbsTrimSurfaces);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashNurbsTrimSurface(v.NurbsTrimSurfaces[i]);

            n = Count(v.NurbsTrimBoundaries);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashNurbsTrimBoundary(v.NurbsTrimBoundaries[i]);

            n = Count(v.ProceduralGeometries);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashProceduralGeometry(v.ProceduralGeometries[i]);

            n = Count(v.StereoCameras);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashStereoCamera(v.StereoCameras[i]);

            n = Count(v.CameraSwitchers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCameraSwitcher(v.CameraSwitchers[i]);

            n = Count(v.Markers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashMarker(v.Markers[i]);

            n = Count(v.LodGroups);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashLodGroup(v.LodGroups[i]);

            n = Count(v.SkinDeformers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSkinDeformer(v.SkinDeformers[i]);

            n = Count(v.SkinClusters);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSkinCluster(v.SkinClusters[i]);

            n = Count(v.BlendDeformers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBlendDeformer(v.BlendDeformers[i]);

            n = Count(v.BlendChannels);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBlendChannel(v.BlendChannels[i]);

            n = Count(v.BlendShapes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashBlendShape(v.BlendShapes[i]);

            n = Count(v.CacheDeformers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCacheDeformer(v.CacheDeformers[i]);

            n = Count(v.CacheFiles);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCacheFile(v.CacheFiles[i]);

            n = Count(v.Materials);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashMaterial(v.Materials[i]);

            n = Count(v.Textures);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashTexture(v.Textures[i]);

            n = Count(v.Videos);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashVideo(v.Videos[i]);

            n = Count(v.Shaders);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashShader(v.Shaders[i]);

            n = Count(v.ShaderBindings);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashShaderBinding(v.ShaderBindings[i]);

            n = Count(v.AnimStacks);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashAnimStack(v.AnimStacks[i]);

            n = Count(v.AnimLayers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashAnimLayer(v.AnimLayers[i]);

            n = Count(v.AnimValues);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashAnimValue(v.AnimValues[i]);

            n = Count(v.AnimCurves);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashAnimCurve(v.AnimCurves[i]);

            n = Count(v.DisplayLayers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashDisplayLayer(v.DisplayLayers[i]);

            n = Count(v.SelectionSets);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSelectionSet(v.SelectionSets[i]);

            n = Count(v.SelectionNodes);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashSelectionNode(v.SelectionNodes[i]);

            n = Count(v.Characters);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashCharacter(v.Characters[i]);

            n = Count(v.Constraints);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashConstraint(v.Constraints[i]);

            n = Count(v.AudioLayers);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashAudioLayer(v.AudioLayers[i]);

            n = Count(v.AudioClips);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashAudioClip(v.AudioClips[i]);

            n = Count(v.Poses);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashPose(v.Poses[i]);

            n = Count(v.MetadataObjects);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashMetadataObject(v.MetadataObjects[i]);

            n = Count(v.TextureFiles);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashTextureFile(v.TextureFiles[i]);

            n = Count(v.ConnectionsSrc);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashConnectionValue(v.ConnectionsSrc[i]);

            n = Count(v.ConnectionsDst);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashConnectionValue(v.ConnectionsDst[i]);

            n = Count(v.ElementsByName);
            HashSizeT(n);
            for (int i = 0; i < n; i++) HashNameElement(v.ElementsByName[i]);

            if (v.DomRoot != null) HashDomNode(v.DomRoot);
        }
    }
}
