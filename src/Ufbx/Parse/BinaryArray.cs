// Binary array plumbing, ported from ufbx.c v0.23.1:
//   ufbxi_swap_endian (8601-8638), ufbxi_swap_endian_array (8640-8648),
//   ufbxi_swap_endian_value (8650-8662), ufbxi_binary_convert_array (8664-8757),
//   ufbxi_binary_parse_multivalue_array (8759-8865), ufbxi_push_array_data (8867-8888),
//   ufbxi_postprocess_bool_array (8890-8895),
//   ufbxi_f64_to_i32/ufbxi_f64_to_i64 (1112-1128).
//
// Representation: FBX array payloads are raw little-endian bytes in a `byte[]` (see
// UfbxiValueArray), so byte-level endian swapping and reinterpretation stay bit-identical
// to C. `SwapEndian*` return the shared `uc->SwapArr` scratch buffer at offset 0 — like
// C's returned pointer, the result is only valid until the next swap call.
//
// Not ported here: nothing. The string-array special case of
// ufbxi_binary_parse_multivalue_array ('s'/'S'/'C', ufbx.c:8776-8806) is
// BinaryParseMultivalueStringArray() below; it landed with the string pool.

using System;
using System.Buffers.Binary;

namespace Ufbx
{
    internal static class UfbxiBinaryArray
    {
        // Is this array type a per-element `ufbx_string` (so the payload is a `string[]`,
        // not raw bytes)? C: the `dst_type == 's' || 'S' || 'C'` test at ufbx.c:8780.
        internal static bool IsStringArrayType(char type)
        {
            return type == 's' || type == 'S' || type == 'C';
        }

        // C: the string-array special case of ufbxi_binary_parse_multivalue_array
        // (ufbx.c:8779-8807): pre-7000 properties read one by one into a `ufbx_string[]`.
        // `raw` is `dst_type == 's'` only, so 'S' interns a *sanitized* copy while 'C' skips
        // interning entirely and just keeps an owned copy of the bytes.
        internal static bool BinaryParseMultivalueStringArray(UfbxiContext uc, char dstType, string[] dst, int dstOffset, int size)
        {
            if (size == 0) return true;

            // Port-only guard: C's string special case is an `if` test, not an error site
            // (ufbx.c:8780); the extracted helper keeps it as an entry assertion.
            UfbxiFail.CheckNoDesc(dstType == 's' || dstType == 'S' || dstType == 'C', "dst_type == 's' || dst_type == 'S' || dst_type == 'C'");
            bool raw = dstType == 's';
            bool fileBigEndian = uc.FileBigEndian;
            UfbxiStream stream = uc.Stream;

            for (int i = 0; i < size; i++) {
                int pos = stream.PeekBytes(13);
                char type = (char)stream.Buffer[pos];
                // C: ufbxi_check(type == 'S' || type == 'R')
                UfbxiFail.CheckNoDesc(type == 'S' || type == 'R', "type == 'S' || type == 'R'");

                byte[] valBuf = stream.Buffer;
                int valOff = pos + 1;
                if (fileBigEndian) {
                    valBuf = SwapEndianValue(uc, valBuf, valOff, type, out valOff);
                    if (valBuf == null) return false;
                }

                uint length = LE.U32(valBuf, valOff);
                stream.ConsumeBytes(5);

                // Port-only guard: the port cannot index a read longer than int.MaxValue. C
                // reaches `ufbxi_read_bytes(uc, length)` -> `ufbxi_refill(length, true)`, whose
                // FIRST check is the plain `!uc->eof` (ufbx.c:6705): for a buffered file/memory
                // input the whole file is already read and `uc->eof` is set, so that check fails
                // with no description. Only a stream still mid-file reaches the later msg site
                // `data_size >= size` ("Truncated file", ufbx.c:6759). Reproduce both.
                if (length > int.MaxValue) {
                    UfbxiFail.CheckNoDesc(!stream.Eof, "!uc->eof");
                    UfbxiFail.FailMsg("data_size >= size", "Truncated file");
                }
                int strPos = stream.ReadBytes(unchecked((int)length));
                string str = UfbxiRawStr.FromBytes(stream.Buffer, strPos, unchecked((int)length));
                // C: ufbxi_check(d->data) — reading `length` bytes must succeed.
                UfbxiFail.CheckNoDesc(str != null, "d->data");

                if (dstType == 'C') {
                    // C: ufbxi_push_copy(buf, char, len, d->data) into `uc->result` when
                    // `size == 1 || opts.retain_dom`, else into the temp buffer. `FromBytes`
                    // already returns a freshly allocated string, i.e. that owned copy.
                    dst[dstOffset + i] = str;
                } else {
                    UfbxiFail.CheckNoDesc(uc.StringPool.PushStringPlaceStr(ref str, raw), "ufbxi_push_string_place_str(&uc->string_pool, d, raw)");
                    dst[dstOffset + i] = str;
                }
            }

            return true;
        }

        // Little-endian unaligned reads, matching ufbxi_read_u8/u16/u32/u64/f32/f64
        // (ufbx.c:745-796) on the little-endian reference build.
        // Not private: the binary DOM reader (Parse/DomNode.cs) unrolls the same header and
        // value words through these.
        internal static class LE
        {
            public static byte U8(byte[] b, int o) => b[o];
            public static sbyte I8(byte[] b, int o) => unchecked((sbyte)b[o]);
            public static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
            public static short I16(byte[] b, int o) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o));
            public static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
            public static int I32(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
            public static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
            public static long I64(byte[] b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o));
            public static float F32(byte[] b, int o) => UfbxBitUtil.BitsToSingle(U32(b, o));
            public static double F64(byte[] b, int o) => UfbxBitUtil.FromInt64(unchecked((long)U64(b, o)));
        }

        static void WriteU8(byte[] b, int o, byte v) => b[o] = v;
        static void WriteI32(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        static void WriteI64(byte[] b, int o, long v) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(o), v);
        static void WriteF32(byte[] b, int o, float v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), UfbxBitUtil.SingleToBits(v));
        static void WriteF64(byte[] b, int o, double v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), unchecked((ulong)UfbxBitUtil.BitsOf(v)));

        // C: int32_t ufbxi_f64_to_i32(double) (ufbx.c:1112-1119) — clamps instead of UB.
        internal static int F64ToInt32(double value)
        {
            if (UfbxMath.Abs(value) <= (double)int.MaxValue) {
                return (int)value;
            } else {
                return value >= 0.0 ? int.MaxValue : int.MinValue;
            }
        }

        // C: int64_t ufbxi_f64_to_i64(double) (ufbx.c:1121-1128)
        internal static long F64ToInt64(double value)
        {
            if (UfbxMath.Abs(value) <= (double)long.MaxValue) {
                return (long)value;
            } else {
                return value >= 0.0 ? long.MaxValue : long.MinValue;
            }
        }

        // C: char *ufbxi_swap_endian(uc, src, count, elem_size) (ufbx.c:8601-8638)
        // Returns the scratch buffer (offset 0), or null on overflow — C returns NULL.
        internal static byte[] SwapEndian(UfbxiContext uc, byte[] src, int srcOffset, int count, int elemSize)
        {
            long totalSize = (long)count * elemSize;
            if (totalSize > int.MaxValue) return null;
            int total = (int)totalSize;
            if (uc.SwapArrSize < total) {
                // C: ufbxi_grow_array(&uc->ator_tmp, &uc->swap_arr, &uc->swap_arr_size, total_size)
                int newCap = uc.SwapArrSize * 2;
                if (newCap < total) newCap = total;
                byte[] arr = new byte[newCap];
                if (uc.SwapArr != null) Array.Copy(uc.SwapArr, 0, arr, 0, uc.SwapArrSize < total ? uc.SwapArrSize : total);
                uc.SwapArr = arr;
                uc.SwapArrSize = newCap;
            }
            byte[] dst = uc.SwapArr;
            switch (elemSize) {
            case 2:
                for (int i = 0; i < count; i++) {
                    int d = i * 2, s = srcOffset + i * 2;
                    dst[d] = src[s + 1];
                    dst[d + 1] = src[s];
                }
                break;
            case 4:
                for (int i = 0; i < count; i++) {
                    int d = i * 4, s = srcOffset + i * 4;
                    dst[d] = src[s + 3];
                    dst[d + 1] = src[s + 2];
                    dst[d + 2] = src[s + 1];
                    dst[d + 3] = src[s];
                }
                break;
            case 8:
                for (int i = 0; i < count; i++) {
                    int d = i * 8, s = srcOffset + i * 8;
                    dst[d] = src[s + 7];
                    dst[d + 1] = src[s + 6];
                    dst[d + 2] = src[s + 5];
                    dst[d + 3] = src[s + 4];
                    dst[d + 4] = src[s + 3];
                    dst[d + 5] = src[s + 2];
                    dst[d + 6] = src[s + 1];
                    dst[d + 7] = src[s];
                }
                break;
            default:
                // Port-only guard: C's `ufbxi_swap_endian` hits `ufbxi_unreachable("Bad endian
                // swap size")` (ufbx.c:8632-8633) for any other element size, an assert/abort
                // rather than an error site. Callers only pass 2/4/8.
                UfbxiFail.FailNoDesc("Bad endian swap size");
                return null;
            }
            return dst;
        }

        // C: const char *ufbxi_swap_endian_array(uc, src, count, type) (ufbx.c:8640-8648)
        // `dstOffset` is C's returned pointer expressed as an offset into the returned array.
        internal static byte[] SwapEndianArray(UfbxiContext uc, byte[] src, int srcOffset, int count, char type, out int dstOffset)
        {
            switch (type) {
            case 'i': case 'f':
                dstOffset = 0;
                return SwapEndian(uc, src, srcOffset, count, 4);
            case 'l': case 'd':
                dstOffset = 0;
                return SwapEndian(uc, src, srcOffset, count, 8);
            default:
                dstOffset = srcOffset;
                return src;
            }
        }

        // C: const char *ufbxi_swap_endian_value(uc, src, type) (ufbx.c:8650-8662)
        // Shallow: swaps the header words, and for array types 3 four-byte words.
        internal static byte[] SwapEndianValue(UfbxiContext uc, byte[] src, int srcOffset, char type, out int dstOffset)
        {
            switch (type) {
            case 'Y': dstOffset = 0; return SwapEndian(uc, src, srcOffset, 1, 2);
            case 'I': case 'F': dstOffset = 0; return SwapEndian(uc, src, srcOffset, 1, 4);
            case 'L': case 'D': dstOffset = 0; return SwapEndian(uc, src, srcOffset, 1, 8);
            case 'S': case 'R': dstOffset = 0; return SwapEndian(uc, src, srcOffset, 1, 4);
            case 'i': case 'l': case 'f': case 'd': case 'b': dstOffset = 0; return SwapEndian(uc, src, srcOffset, 3, 4);
            default: dstOffset = srcOffset; return src;
            }
        }

        // C: int ufbxi_binary_convert_array(maybe_uc, src_type, dst_type, src, dst, size)
        // (ufbx.c:8864-8757). Reads `src[srcOffset .. +size*src_elem]`, writes
        // `dst[dstOffset .. +size*dst_elem]`.
        internal static bool BinaryConvertArray(UfbxiContext uc, char srcType, char dstType,
            byte[] src, int srcOffset, byte[] dst, int dstOffset, int size)
        {
            if (srcType == dstType) {
                // C asserts `maybe_uc` here: the only NULL-context caller is the DEFLATE
                // task, which always converts between different element types.
                src = SwapEndianArray(uc, src, srcOffset, size, srcType, out srcOffset);
                if (src == null) return false;
                Array.Copy(src, srcOffset, dst, dstOffset, size * UfbxiArrayType.SizeOf(dstType));
                return true;
            }

            if (uc != null && uc.FileBigEndian) {
                src = SwapEndianArray(uc, src, srcOffset, size, srcType, out srcOffset);
                if (src == null) return false;
            }

            switch (dstType) {

            case 'c':
                switch (srcType) {
                case 'i': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d++) WriteU8(dst, d, unchecked((byte)LE.I32(src, s))); break;
                case 'l': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d++) WriteU8(dst, d, unchecked((byte)LE.I64(src, s))); break;
                case 'f': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d++) WriteU8(dst, d, unchecked((byte)(int)LE.F32(src, s))); break;
                case 'd': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d++) WriteU8(dst, d, unchecked((byte)(int)LE.F64(src, s))); break;
                default: if (uc != null) UfbxiFail.FailNoDesc("Bad array source type"); return false;
                }
                break;

            case 'i':
                switch (srcType) {
                case 'c': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 1, d += 4) WriteI32(dst, d, (int)LE.I8(src, s)); break;
                case 'l': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d += 4) WriteI32(dst, d, unchecked((int)LE.I64(src, s))); break;
                case 'f': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d += 4) WriteI32(dst, d, F64ToInt32(LE.F32(src, s))); break;
                case 'd': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d += 4) WriteI32(dst, d, F64ToInt32(LE.F64(src, s))); break;
                default: if (uc != null) UfbxiFail.FailNoDesc("Bad array source type"); return false;
                }
                break;

            case 'l':
                switch (srcType) {
                case 'c': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 1, d += 8) WriteI64(dst, d, (long)LE.I8(src, s)); break;
                case 'i': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d += 8) WriteI64(dst, d, (long)LE.I32(src, s)); break;
                case 'f': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d += 8) WriteI64(dst, d, F64ToInt64(LE.F32(src, s))); break;
                case 'd': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d += 8) WriteI64(dst, d, F64ToInt64(LE.F64(src, s))); break;
                default: if (uc != null) UfbxiFail.FailNoDesc("Bad array source type"); return false;
                }
                break;

            case 'f':
                switch (srcType) {
                case 'c': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 1, d += 4) WriteF32(dst, d, (float)LE.I8(src, s)); break;
                case 'i': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d += 4) WriteF32(dst, d, (float)LE.I32(src, s)); break;
                case 'l': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d += 4) WriteF32(dst, d, (float)LE.I64(src, s)); break;
                case 'd': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d += 4) WriteF32(dst, d, (float)LE.F64(src, s)); break;
                default: if (uc != null) UfbxiFail.FailNoDesc("Bad array source type"); return false;
                }
                break;

            case 'd':
                switch (srcType) {
                case 'c': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 1, d += 8) WriteF64(dst, d, (double)LE.I8(src, s)); break;
                case 'i': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d += 8) WriteF64(dst, d, (double)LE.I32(src, s)); break;
                case 'l': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 8, d += 8) WriteF64(dst, d, (double)LE.I64(src, s)); break;
                case 'f': for (int k = 0, s = srcOffset, d = dstOffset; k < size; k++, s += 4, d += 8) WriteF64(dst, d, (double)LE.F32(src, s)); break;
                default: if (uc != null) UfbxiFail.FailNoDesc("Bad array source type"); return false;
                }
                break;

            default:
                return false;

            }

            return true;
        }

        // C: int ufbxi_binary_parse_multivalue_array(uc, dst_type, dst, size, tmp_buf)
        // (ufbx.c:8759-8865), numeric paths only. Reads pre-7000 FBX where each array
        // element is stored as an individually typed property.
        internal static bool BinaryParseMultivalueArray(UfbxiContext uc, char dstType, byte[] dst, int dstOffset, int size)
        {
            if (size == 0) return true;

            bool fileBigEndian = uc.FileBigEndian;
            UfbxiStream stream = uc.Stream;

            int base_ = 0;

            // C: ufbxi_convert_parse_fast — the common little-endian cases, one property
            // byte plus a fixed-size value per element. The peeked window index is only
            // valid until the next peek, so it is re-read (never cached) each iteration.
            if (!fileBigEndian) {
                int d = dstOffset;
                switch (dstType) {
                case 'i':
                    for (; base_ < size; base_++) {
                        int pos = stream.PeekBytes(13);
                        if (stream.Buffer[pos] != (byte)'I') break;
                        WriteI32(dst, d, LE.I32(stream.Buffer, pos + 1));
                        d += 4;
                        stream.ConsumeBytes(1 + 4);
                    }
                    break;
                case 'l':
                    for (; base_ < size; base_++) {
                        int pos = stream.PeekBytes(13);
                        if (stream.Buffer[pos] != (byte)'L') break;
                        WriteI64(dst, d, LE.I64(stream.Buffer, pos + 1));
                        d += 8;
                        stream.ConsumeBytes(1 + 8);
                    }
                    break;
                case 'f':
                    for (; base_ < size; base_++) {
                        int pos = stream.PeekBytes(13);
                        if (stream.Buffer[pos] != (byte)'F') break;
                        WriteF32(dst, d, LE.F32(stream.Buffer, pos + 1));
                        d += 4;
                        stream.ConsumeBytes(1 + 4);
                    }
                    break;
                case 'd':
                    for (; base_ < size; base_++) {
                        int pos = stream.PeekBytes(13);
                        if (stream.Buffer[pos] != (byte)'D') break;
                        WriteF64(dst, d, LE.F64(stream.Buffer, pos + 1));
                        d += 8;
                        stream.ConsumeBytes(1 + 8);
                    }
                    break;
                default:
                    break; // Fallthrough to the general path
                }

                // C: early return if everything was handled
                if (base_ == size) return true;
            }

            // C: ufbxi_convert_parse_switch — per-element typed properties, mixed allowed.
            int dd = dstOffset + base_ * UfbxiArrayType.SizeOf(dstType);
            for (int k = base_; k < size; k++) {
                int pos = stream.PeekBytes(13);
                byte[] window = stream.Buffer;
                char type = (char)window[pos];
                int valOff = pos + 1;

                byte[] valBuf;
                if (fileBigEndian) {
                    valBuf = SwapEndianValue(uc, window, valOff, type, out valOff);
                    if (valBuf == null) return false;
                } else {
                    valBuf = window;
                }

                // C: size_t val_size; (the `default` case below fails, so C never reads it).
                int valSize = 0;
                switch (type) {
                // C reads `*val` through a `const char*`, so single bytes sign-extend.
                case 'C':
                case 'B':
                    switch (dstType) {
                    case 'c': WriteU8(dst, dd, LE.U8(valBuf, valOff)); break;
                    case 'i': WriteI32(dst, dd, (int)LE.I8(valBuf, valOff)); break;
                    case 'l': WriteI64(dst, dd, (long)LE.I8(valBuf, valOff)); break;
                    case 'f': WriteF32(dst, dd, (float)LE.I8(valBuf, valOff)); break;
                    case 'd': WriteF64(dst, dd, (double)LE.I8(valBuf, valOff)); break;
                    default: return false;
                    }
                    valSize = 1 + 1;
                    break;
                case 'Y':
                    switch (dstType) {
                    case 'c': WriteU8(dst, dd, unchecked((byte)LE.I16(valBuf, valOff))); break;
                    case 'i': WriteI32(dst, dd, (int)LE.I16(valBuf, valOff)); break;
                    case 'l': WriteI64(dst, dd, (long)LE.I16(valBuf, valOff)); break;
                    case 'f': WriteF32(dst, dd, (float)LE.I16(valBuf, valOff)); break;
                    case 'd': WriteF64(dst, dd, (double)LE.I16(valBuf, valOff)); break;
                    default: return false;
                    }
                    valSize = 2 + 1;
                    break;
                case 'I':
                    switch (dstType) {
                    case 'c': WriteU8(dst, dd, unchecked((byte)LE.I32(valBuf, valOff))); break;
                    case 'i': WriteI32(dst, dd, LE.I32(valBuf, valOff)); break;
                    case 'l': WriteI64(dst, dd, (long)LE.I32(valBuf, valOff)); break;
                    case 'f': WriteF32(dst, dd, (float)LE.I32(valBuf, valOff)); break;
                    case 'd': WriteF64(dst, dd, (double)LE.I32(valBuf, valOff)); break;
                    default: return false;
                    }
                    valSize = 4 + 1;
                    break;
                case 'L':
                    switch (dstType) {
                    case 'c': WriteU8(dst, dd, unchecked((byte)LE.I64(valBuf, valOff))); break;
                    case 'i': WriteI32(dst, dd, unchecked((int)LE.I64(valBuf, valOff))); break;
                    case 'l': WriteI64(dst, dd, LE.I64(valBuf, valOff)); break;
                    case 'f': WriteF32(dst, dd, (float)LE.I64(valBuf, valOff)); break;
                    case 'd': WriteF64(dst, dd, (double)LE.I64(valBuf, valOff)); break;
                    default: return false;
                    }
                    valSize = 8 + 1;
                    break;
                case 'F':
                    switch (dstType) {
                    case 'c': WriteU8(dst, dd, unchecked((byte)(int)LE.F32(valBuf, valOff))); break;
                    case 'i': WriteI32(dst, dd, F64ToInt32(LE.F32(valBuf, valOff))); break;
                    case 'l': WriteI64(dst, dd, F64ToInt64(LE.F32(valBuf, valOff))); break;
                    case 'f': WriteF32(dst, dd, LE.F32(valBuf, valOff)); break;
                    case 'd': WriteF64(dst, dd, (double)LE.F32(valBuf, valOff)); break;
                    default: return false;
                    }
                    valSize = 4 + 1;
                    break;
                case 'D':
                    switch (dstType) {
                    case 'c': WriteU8(dst, dd, unchecked((byte)(int)LE.F64(valBuf, valOff))); break;
                    case 'i': WriteI32(dst, dd, F64ToInt32(LE.F64(valBuf, valOff))); break;
                    case 'l': WriteI64(dst, dd, F64ToInt64(LE.F64(valBuf, valOff))); break;
                    case 'f': WriteF32(dst, dd, (float)LE.F64(valBuf, valOff)); break;
                    case 'd': WriteF64(dst, dd, LE.F64(valBuf, valOff)); break;
                    default: return false;
                    }
                    valSize = 8 + 1;
                    break;
                default:
                    UfbxiFail.FailNoDesc("Bad multivalue array type");
                    break;
                }

                dd += UfbxiArrayType.SizeOf(dstType);
                stream.ConsumeBytes(valSize);
            }

            return true;
        }

        // C: void *ufbxi_push_array_data(uc, info, size, tmp_buf) (ufbx.c:8867-8888)
        // Returns the destination buffer and the offset of element 0; PAD_BEGIN keeps 4
        // elements of zeros before it, which is what makes C's `data[-1]` read 0.
        internal static bool PushArrayData(UfbxiContext uc, UfbxiArrayInfo info, int size, out byte[] data, out int offset)
        {
            data = null;
            offset = 0;
            int elemSize = UfbxiArrayType.SizeOf(info.Type);
            // C's `size` is a `size_t` and the caller's count is a `uint32_t`, so read it
            // unsigned: a count above int.MaxValue must fail the managed allocation (the plain
            // `ufbxi_check(arr_data)` site) instead of wrapping negative.
            long count = unchecked((uint)size);
            if ((info.Flags & UfbxiArrayFlags.PadBegin) != 0) {
                if (count > int.MaxValue - 4) return false;
                count += 4;
            }
            long total = count * elemSize;
            if (total > int.MaxValue) return false;
            data = new byte[(int)total];
            if ((info.Flags & UfbxiArrayFlags.PadBegin) != 0) {
                offset = 4 * elemSize;
            }
            return true;
        }

        // C: void ufbxi_postprocess_bool_array(char *data, size_t size) (ufbx.c:8890-8895)
        internal static void PostprocessBoolArray(byte[] data, int offset, int size)
        {
            for (int i = 0; i < size; i++) {
                data[offset + i] = (byte)(data[offset + i] != 0 ? 1 : 0);
            }
        }
    }
}
