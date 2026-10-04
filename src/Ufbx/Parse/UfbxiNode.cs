// FBX DOM node model and value accessors, ported from ufbx.c v0.23.1:
//   ufbxi_node (ufbx.c:6183-6200), ufbxi_value / ufbxi_value_array (6172-6181),
//   ufbxi_sanitized_string (4917-4923), ufbxi_value_type (6165-6170),
//   node operations (7688-7871).
//
// C# mapping notes (deliberate, record before extending):
//  - C's `ufbxi_value` is a union of {f,i} and the sanitized string; reads are always
//    gated by the per-value 2-bit type in `ValueTypeMask`, so a plain struct holding all
//    three members is equivalent.
//  - C's `ufbxi_node` is stored in contiguous arrays and passed around as a pointer.
//    `UfbxiNode` is a `class` so pointer semantics map to references; children are
//    `UfbxiNode[]` (C: `ufbxi_node *children` + `num_children`).
//  - C's `void *data` in `ufbxi_value_array` points at raw little-endian element bytes for
//    numeric arrays and at `ufbx_string[]` for string arrays. Both are reproduced: numeric
//    arrays keep a `byte[]` (so endian swap / bit-accurate reinterpreting stay possible) and
//    string arrays a `string[]`. `Offset` is the byte offset of element 0; PAD_BEGIN arrays
//    leave 4 elements of zeros before it, which is what makes C's `data[-1]` reads return 0.
//  - C compares interned names by pointer; the string pool canonicalizes equal strings, so
//    C# string `==` (content equality) has the same observable result.

using System;
using System.Buffers.Binary;

namespace Ufbx
{
    // C: typedef enum ufbxi_value_type (ufbx.c:6165-6170)
    internal enum UfbxiValueType
    {
        None = 0,   // C: UFBXI_VALUE_NONE
        Number = 1, // C: UFBXI_VALUE_NUMBER
        String = 2, // C: UFBXI_VALUE_STRING
        Array = 3,  // C: UFBXI_VALUE_ARRAY
    }

    // C: typedef struct ufbxi_sanitized_string (ufbx.c:4917-4923)
    internal struct UfbxiSanitizedString
    {
        public string RawData;    // C: const char *raw_data — original bytes; one char per byte
        public string Utf8Data;   // C: const char *utf8_data — sanitized, may alias RawData or be null
        public int RawLength;     // C: uint32_t raw_length
        public int Utf8Length;    // C: uint32_t utf8_length

        // DOM raw strings carry one byte per char (see UfbxiStrings), so a blob is the low
        // byte of each char. `utf8_data` may alias `raw_data`.
        internal static byte[] ToBlob(string raw, int length)
        {
            if (raw == null) return null;
            if (length > raw.Length) length = raw.Length;
            byte[] blob = new byte[length];
            for (int i = 0; i < length; i++) blob[i] = unchecked((byte)raw[i]);
            return blob;
        }

        internal static string FromBytes(byte[] data, int offset, int length)
        {
            char[] chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)data[offset + i];
            return new string(chars);
        }
    }

    // C: typedef union ufbxi_value (ufbx.c:6172-6175)
    internal struct UfbxiValue
    {
        public double F;                      // C: double f
        public long I;                        // C: int64_t i
        public UfbxiSanitizedString S;        // C: ufbxi_sanitized_string s
    }

    // FBX type-code helpers (C: ufbxi_array_type_size ufbx.c:7688-7703,
    //                        ufbxi_normalize_array_type ufbx.c:7680-7686)
    internal static class UfbxiArrayType
    {
        // C: sizeof(ufbx_string) on the 64-bit reference build; only numeric strides are
        // observable in the C# port, string arrays are held as `string[]`.
        const int StringElementSize = 16;

        // C: size_t ufbxi_array_type_size(char type)
        internal static int SizeOf(char type)
        {
            switch (type) {
            case 'r': return 8;                  // C: sizeof(ufbx_real) == sizeof(double)
            case 'b': return 1;                  // C: sizeof(bool)
            case 'c': return 1;                  // C: sizeof(uint8_t)
            case 'i': return 4;                  // C: sizeof(int32_t)
            case 'l': return 8;                  // C: sizeof(int64_t)
            case 'f': return 4;                  // C: sizeof(float)
            case 'd': return 8;                  // C: sizeof(double)
            case 's': return StringElementSize;  // C: sizeof(ufbx_string)
            case 'S': return StringElementSize;
            case 'C': return StringElementSize;
            default: return 1;
            }
        }

        // C: char ufbxi_normalize_array_type(char type, char bool_type)
        // `UFBX_REAL_TYPE` is `double` by default (ufbx.h:154-159), so 'r' -> 'd'.
        internal static char Normalize(char type, char boolType)
        {
            switch (type) {
            case 'r': return 'd';
            case 'b': return boolType;
            default: return type;
            }
        }
    }

    // C: typedef struct ufbxi_value_array (ufbx.c:6177-6181)
    internal sealed class UfbxiValueArray
    {
        public byte[] Data;       // C: void *data — numeric/bool arrays (little-endian bytes)
        public string[] Strings;  // C: void *data — 's'/'S'/'C' arrays of `ufbx_string`
        public int Offset;        // bytes from the start of `Data` to element 0 (PAD_BEGIN: 4 elements)
        public int Size;          // C: size_t size — element count
        public char Type;         // C: char type — FBX type code 'b','c','i','l','f','d','s','S','C'

        public int ElementSize => UfbxiArrayType.SizeOf(Type);

        int ByteOffset(int index) => Offset + index * ElementSize;

        // netstandard2.1's BinaryPrimitives has no ReadSingle/ReadDouble, so floats are
        // reinterpreted from their raw bits (see UfbxBitUtil).
        float ReadSingle(int bo) => UfbxBitUtil.BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(bo)));
        double ReadDouble(int bo) => UfbxBitUtil.FromInt64(unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(bo))));

        // The typed array element accessors below (`GetReal/GetFloat/GetDouble/GetInt32/
        // GetInt64`) have no single C site: C reinterprets `arr->data` with a raw cast such as
        // `((ufbx_real*)arr->data)[i]`, so its element type is chosen by the caller, not checked.
        // The port needs an explicit dispatch to reinterpret the little-endian `byte[]`, and
        // throws on an unexpected element type. Those throws are port-only guards; C would read
        // the wrong interpretation silently instead.

        // C: ((ufbx_real*)arr->data)[index] — arrays of type 'r' are normalized to 'd'/'f'.
        public double GetReal(int index)
        {
            int bo = ByteOffset(index);
            switch (Type) {
            case 'd': return ReadDouble(bo);
            case 'f': return ReadSingle(bo);
            case 'l': return BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(bo));
            case 'i': return BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(bo));
            default: UfbxiFail.FailNoDesc("Bad array real type"); return 0;
            }
        }

        public float GetFloat(int index)
        {
            int bo = ByteOffset(index);
            switch (Type) {
            case 'f': return ReadSingle(bo);
            case 'd': return (float)ReadDouble(bo);
            default: UfbxiFail.FailNoDesc("Bad array float type"); return 0;
            }
        }

        public double GetDouble(int index)
        {
            int bo = ByteOffset(index);
            switch (Type) {
            case 'd': return ReadDouble(bo);
            case 'f': return ReadSingle(bo);
            default: UfbxiFail.FailNoDesc("Bad array double type"); return 0;
            }
        }

        // C: ((int32_t*)arr->data)[index]
        public int GetInt32(int index)
        {
            int bo = ByteOffset(index);
            switch (Type) {
            case 'i': return BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(bo));
            case 'l': return unchecked((int)BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(bo)));
            case 'c': return Data[bo];
            case 'b': return Data[bo];
            default: UfbxiFail.FailNoDesc("Bad array int32 type"); return 0;
            }
        }

        public long GetInt64(int index)
        {
            int bo = ByteOffset(index);
            switch (Type) {
            case 'l': return BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(bo));
            case 'i': return BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(bo));
            default: UfbxiFail.FailNoDesc("Bad array int64 type"); return 0;
            }
        }

        public byte GetByte(int index) => Data[ByteOffset(index)];

        public bool GetBool(int index) => Data[ByteOffset(index)] != 0;

        public string GetString(int index) => Strings[index];
    }

    // C: struct ufbxi_node (ufbx.c:6183-6200)
    internal sealed class UfbxiNode
    {
        // C: #define UFBXI_MAX_NON_ARRAY_VALUES 8 (ufbx.c:51)
        internal const int MaxNonArrayValues = 8;

        public string Name;                 // C: const char *name (pooled)
        public uint NumChildren;            // C: uint32_t num_children
        public int NameLen;                 // C: uint8_t name_len
        public uint ValueTypeMask;          // C: uint16_t value_type_mask
        public UfbxiNode[] Children;        // C: ufbxi_node *children
        public UfbxiValueArray Array;       // C: union { ufbxi_value_array *array; ... }
        public UfbxiValue[] Vals;           // C: union { ...; ufbxi_value *vals; }

        // C: ufbxi_find_child(ufbxi_node*, const char*) (ufbx.c:7707-7714)
        internal UfbxiNode FindChild(string name)
        {
            for (int i = 0; i < NumChildren; i++) {
                if (Children[i].Name == name) return Children[i];
            }
            return null;
        }

        // C: ufbxi_find_child_strcmp(ufbxi_node*, const char*) (ufbx.c:7861-7869)
        // Compares the leading byte first; content equality is the observable result.
        internal UfbxiNode FindChildStrCmp(string name)
        {
            if (name == null || name.Length == 0) return null;
            char leading = name[0];
            for (int i = 0; i < NumChildren; i++) {
                string cname = Children[i].Name;
                if (cname == null || cname.Length == 0 || cname[0] != leading) continue;
                if (cname == name) return Children[i];
            }
            return null;
        }

        // C: ufbxi_get_val_type(node, ix) (ufbx.c:7716-7719)
        public UfbxiValueType GetValType(int ix)
        {
            return (UfbxiValueType)((ValueTypeMask >> (ix * 2)) & 0x3);
        }

        // C: ufbxi_get_val_at(node, ix, fmt, v) (ufbx.c:7725-7784).
        // C's `void *v` becomes one typed accessor per format char; each keeps C's gate
        // (number formats require UFBXI_VALUE_NUMBER, string formats UFBXI_VALUE_STRING)
        // and returns false on a type mismatch, exactly like the switch in C.

        // fmt 'I': int32_t
        public bool GetValI(int ix, out int value)
        {
            value = 0;
            if (GetValType(ix) != UfbxiValueType.Number) return false;
            value = unchecked((int)Vals[ix].I);
            return true;
        }

        // fmt 'L': int64_t
        public bool GetValL(int ix, out long value)
        {
            value = 0;
            if (GetValType(ix) != UfbxiValueType.Number) return false;
            value = Vals[ix].I;
            return true;
        }

        // fmt 'F': float
        public bool GetValF(int ix, out float value)
        {
            value = 0f;
            if (GetValType(ix) != UfbxiValueType.Number) return false;
            value = (float)Vals[ix].F;
            return true;
        }

        // fmt 'D': double
        public bool GetValD(int ix, out double value)
        {
            value = 0.0;
            if (GetValType(ix) != UfbxiValueType.Number) return false;
            value = Vals[ix].F;
            return true;
        }

        // fmt 'R': ufbx_real
        public bool GetValR(int ix, out double value) => GetValD(ix, out value);

        // fmt 'B': bool
        public bool GetValB(int ix, out bool value)
        {
            value = false;
            if (GetValType(ix) != UfbxiValueType.Number) return false;
            value = Vals[ix].I != 0;
            return true;
        }

        // fmt 'Z': size_t (mapped to int for array lengths/indices)
        public bool GetValZ(int ix, out int value)
        {
            value = 0;
            if (GetValType(ix) != UfbxiValueType.Number) return false;
            long i = Vals[ix].I;
            if (i < 0) return false;
            value = (int)i;
            return true;
        }

        // fmt 'Z' with the full C `size_t` width: ufbx.c:7737-7743 stores the int64 value
        // untruncated on 64-bit builds, so callers that range-check before storing must use this.
        public bool GetValZ64(int ix, out long value)
        {
            value = 0;
            if (GetValType(ix) != UfbxiValueType.Number) return false;
            long i = Vals[ix].I;
            if (i < 0) return false;
            value = i;
            return true;
        }

        // fmt 'S': ufbx_string, sanitized UTF-8 (C: returns false when unsanitized)
        public bool GetValS(int ix, out string value)
        {
            value = null;
            if (GetValType(ix) != UfbxiValueType.String) return false;
            UfbxiSanitizedString src = Vals[ix].S;
            if (src.Utf8Data == null) return false;
            value = src.Utf8Data;
            return true;
        }

        // fmt 's': ufbx_string, raw
        public bool GetValRawString(int ix, out string value)
        {
            value = null;
            if (GetValType(ix) != UfbxiValueType.String) return false;
            value = Vals[ix].S.RawData;
            return true;
        }

        // fmt 'C': const char*, sanitized
        public bool GetValC(int ix, out string value) => GetValS(ix, out value);

        // fmt 'c': const char*, raw (unchecked)
        public bool GetValRawChar(int ix, out string value) => GetValRawString(ix, out value);

        // fmt 'b': ufbx_blob — the raw bytes of the string
        public bool GetValBlob(int ix, out byte[] value)
        {
            value = null;
            if (GetValType(ix) != UfbxiValueType.String) return false;
            UfbxiSanitizedString src = Vals[ix].S;
            value = UfbxiSanitizedString.ToBlob(src.RawData, src.RawLength);
            return true;
        }

        // C: ufbxi_get_array(node, fmt) (ufbx.c:7786-7795)
        internal UfbxiValueArray GetArray(char fmt)
        {
            if (ValueTypeMask != (uint)UfbxiValueType.Array) return null;
            UfbxiValueArray array = Array;
            if (fmt != '?') {
                fmt = UfbxiArrayType.Normalize(fmt, 'b');
                if (array.Type != fmt) return null;
            }
            return array;
        }

        // C: ufbxi_find_array(node, name, fmt) (ufbx.c:7854-7859)
        internal UfbxiValueArray FindArray(string name, char fmt)
        {
            UfbxiNode child = FindChild(name);
            if (child == null) return null;
            return child.GetArray(fmt);
        }

        // C: ufbxi_find_val1(node, name, fmt, v) (ufbx.c:7837-7843) and 'L' variant
        internal bool FindValL(string name, out long value)
        {
            value = 0;
            UfbxiNode child = FindChild(name);
            if (child == null) return false;
            return child.GetValL(0, out value);
        }

        internal bool FindValD(string name, out double value)
        {
            value = 0.0;
            UfbxiNode child = FindChild(name);
            if (child == null) return false;
            return child.GetValD(0, out value);
        }

        internal bool FindValS(string name, out string value)
        {
            value = null;
            UfbxiNode child = FindChild(name);
            if (child == null) return false;
            return child.GetValS(0, out value);
        }
    }
}
