// Public DOM accessors, ported from ufbx v0.23.1 (ufbx.c):
//   ufbx_dom_is_array / ufbx_dom_array_size (33085-33092),
//   ufbx_dom_as_int32_list / _int64_ / _float_ / _double_ / _real_ / _blob_list (33093-33146),
//   ufbx_dom_find_len (32965-32972), ufbx_dom_find (33169).
//
// Representation: `ufbx_dom_value::value_blob` is a compact little-endian element array
// (PORTING_NOTES.md "DOM/数组数据约定"), so C's `(int32_t*)value_blob.data` reinterpretation
// becomes a decode here. C returns a non-owning view (`{ data, count }` with `data` aliasing
// the blob, `{ NULL, 0 }` when the type does not match); the port returns `null` for that
// empty list, and `count == blob.Length / element_size` either way.
//
// `ufbx_blob` values in C are `{ data, size }` records; the port packs an ARRAY_BLOB payload as
// `[8-byte little-endian size][size bytes]` per element, so `AsBlobList` walks that form and
// returns one `byte[]` per entry.

using System;
using System.Buffers.Binary;

namespace Ufbx.NET
{
    // C: the `ufbx_dom_*` free functions.
    public static class UfbxDom
    {
        // C: ufbx_dom_is_array() -- true for the array value types, which are contiguous in
        // `ufbx_dom_value_type` from ARRAY_I32 to ARRAY_BLOB (ARRAY_IGNORED is not an array).
        public static bool IsArray(UfbxDomNode node)
        {
            if (node == null || node.Values == null || node.Values.Length != 1) return false;
            UfbxDomValueType type = node.Values[0].Type;
            return type >= UfbxDomValueType.ArrayI32 && type <= UfbxDomValueType.ArrayBlob;
        }

        // C: ufbx_dom_array_size() -- 0 for anything that is not an array.
        public static int ArraySize(UfbxDomNode node)
        {
            return IsArray(node) ? unchecked((int)node.Values[0].ValueInt) : 0;
        }

        // C: ufbx_dom_as_int32_list()
        public static int[] AsInt32List(UfbxDomNode node)
        {
            byte[] blob = ArrayBlob(node, UfbxDomValueType.ArrayI32);
            if (blob == null) return null;
            int[] list = new int[blob.Length / 4];
            for (int i = 0; i < list.Length; i++) list[i] = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(blob, i * 4, 4));
            return list;
        }

        // C: ufbx_dom_as_int64_list()
        public static long[] AsInt64List(UfbxDomNode node)
        {
            byte[] blob = ArrayBlob(node, UfbxDomValueType.ArrayI64);
            if (blob == null) return null;
            long[] list = new long[blob.Length / 8];
            for (int i = 0; i < list.Length; i++) list[i] = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(blob, i * 8, 8));
            return list;
        }

        // C: ufbx_dom_as_float_list()
        public static float[] AsFloatList(UfbxDomNode node)
        {
            byte[] blob = ArrayBlob(node, UfbxDomValueType.ArrayF32);
            if (blob == null) return null;
            float[] list = new float[blob.Length / 4];
            for (int i = 0; i < list.Length; i++) {
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(blob, i * 4, 4));
                list[i] = UfbxBitUtil.BitsToSingle(bits);
            }
            return list;
        }

        // C: ufbx_dom_as_double_list()
        public static double[] AsDoubleList(UfbxDomNode node)
        {
            byte[] blob = ArrayBlob(node, UfbxDomValueType.ArrayF64);
            if (blob == null) return null;
            return DecodeDoubles(blob);
        }

        // C: ufbx_dom_as_real_list() -- `sizeof(ufbx_real) == sizeof(double)` in this build, so
        // the type it selects is ARRAY_F64 (ufbx.h:154-159, PORTING_NOTES.md).
        public static double[] AsRealList(UfbxDomNode node)
        {
            byte[] blob = ArrayBlob(node, UfbxDomValueType.ArrayF64);
            if (blob == null) return null;
            return DecodeDoubles(blob);
        }

        static double[] DecodeDoubles(byte[] blob)
        {
            double[] list = new double[blob.Length / 8];
            for (int i = 0; i < list.Length; i++) {
                ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(blob, i * 8, 8));
                list[i] = UfbxBitUtil.FromInt64(unchecked((long)bits));
            }
            return list;
        }

        // C: ufbx_dom_as_blob_list()
        public static byte[][] AsBlobList(UfbxDomNode node)
        {
            byte[] blob = ArrayBlob(node, UfbxDomValueType.ArrayBlob);
            if (blob == null) return null;

            int count = 0;
            for (int offset = 0; offset < blob.Length; ) {
                if (offset + 8 > blob.Length) break;
                long size = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(blob, offset, 8));
                offset += 8 + (int)size;
                count++;
            }

            byte[][] list = new byte[count][];
            int src = 0;
            for (int i = 0; i < count; i++) {
                long size = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(blob, src, 8));
                src += 8;
                byte[] data = new byte[(int)size];
                Array.Copy(blob, src, data, 0, (int)size);
                src += (int)size;
                list[i] = data;
            }
            return list;
        }

        static byte[] ArrayBlob(UfbxDomNode node, UfbxDomValueType type)
        {
            if (node == null || node.Values == null || node.Values.Length != 1) return null;
            UfbxDomValue value = node.Values[0];
            if (value.Type != type) return null;
            return value.ValueBlob;
        }

        // C: ufbx_dom_find_len(parent, name, name_len)
        public static UfbxDomNode FindLen(UfbxDomNode parent, string name, int nameLen)
        {
            if (parent == null || parent.Children == null) return null;
            if (name == null) name = string.Empty;
            if (nameLen > name.Length) nameLen = name.Length;
            for (int i = 0; i < parent.Children.Length; i++) {
                UfbxDomNode child = parent.Children[i];
                string childName = child.Name ?? string.Empty;
                if (childName.Length == nameLen && string.CompareOrdinal(childName, 0, name, 0, nameLen) == 0) return child;
            }
            return null;
        }

        // C: ufbx_dom_find(parent, name) == ufbx_dom_find_len(parent, name, strlen(name))
        public static UfbxDomNode Find(UfbxDomNode parent, string name)
        {
            return FindLen(parent, name, name != null ? name.Length : 0);
        }
    }
}
