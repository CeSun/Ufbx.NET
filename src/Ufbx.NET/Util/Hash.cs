using System;

namespace Ufbx.NET
{
    // Ported from ufbx.c v0.23.1 "Hash functions" (ufbx.c:4711-4827).
    //
    // Hashes are computed over raw bytes. The `string` overloads assume the canonical
    // raw-byte string representation (one char per byte, see `UfbxiRawStr` in Utf8.cs),
    // so `(byte)str[i]` == C's `(uint8_t)str[i]` and both overloads agree bit-for-bit.

    internal static class UfbxiHash
    {
        // C: UINT32_C(0x9e3779b9) (ufbx.c:4714)
        const uint Seed = 0x9e3779b9u;

        // C: UINT32_C(0x7feb352d) (ufbx.c:4733)
        const uint MixMul = 0x7feb352du;

        // C: UINT32_C(0x846ca68b) (ufbx.c:4793)
        const uint MixMul2 = 0x846ca68bu;

        // C: ufbxi_read_u32(str) (ufbx.c:793) — unaligned little-endian 32-bit load.
        static uint ReadU32(byte[] data, int index)
        {
            return (uint)data[index] | ((uint)data[index + 1] << 8) | ((uint)data[index + 2] << 16) | ((uint)data[index + 3] << 24);
        }

        static uint ReadU32Str(string data, int index)
        {
            return (uint)data[index] | ((uint)data[index + 1] << 8) | ((uint)data[index + 2] << 16) | ((uint)data[index + 3] << 24);
        }

        // C: ufbxi_hash_string (ufbx.c:4711-4736).
        internal static uint HashString(byte[] str, int offset, int length)
        {
            uint hash = unchecked((uint)length);
            uint seed = Seed;
            int p = offset;
            int len = length;
            if (length >= 4) {
                do {
                    uint word = ReadU32(str, p);
                    hash = ((hash << 5 | hash >> 27) ^ word) * seed;
                    p += 4;
                    len -= 4;
                } while (len >= 4);

                uint tailWord = ReadU32(str, p + len - 4);
                hash = ((hash << 5 | hash >> 27) ^ tailWord) * seed;
            } else {
                uint word = 0;
                if (length >= 1) word |= (uint)(byte)str[offset + 0] << 0;
                if (length >= 2) word |= (uint)(byte)str[offset + 1] << 8;
                if (length >= 3) word |= (uint)(byte)str[offset + 2] << 16;
                hash = ((hash << 5 | hash >> 27) ^ word) * seed;
            }
            hash ^= hash >> 16;
            hash *= MixMul;
            hash ^= hash >> 15;
            return hash;
        }

        // C: ufbxi_hash_string (ufbx.c:4711-4736), raw-byte `string` input.
        internal static uint HashString(string str, int offset, int length)
        {
            uint hash = unchecked((uint)length);
            uint seed = Seed;
            int p = offset;
            int len = length;
            if (length >= 4) {
                do {
                    uint word = ReadU32Str(str, p);
                    hash = ((hash << 5 | hash >> 27) ^ word) * seed;
                    p += 4;
                    len -= 4;
                } while (len >= 4);

                uint tailWord = ReadU32Str(str, p + len - 4);
                hash = ((hash << 5 | hash >> 27) ^ tailWord) * seed;
            } else {
                uint word = 0;
                if (length >= 1) word |= (uint)(byte)str[offset + 0] << 0;
                if (length >= 2) word |= (uint)(byte)str[offset + 1] << 8;
                if (length >= 3) word |= (uint)(byte)str[offset + 2] << 16;
                hash = ((hash << 5 | hash >> 27) ^ word) * seed;
            }
            hash ^= hash >> 16;
            hash *= MixMul;
            hash ^= hash >> 15;
            return hash;
        }

        // C: ufbxi_hash_string_check_ascii (ufbx.c:4739-4786), byte input.
        // NOTE (C): "_Must_ match `ufbxi_hash_string()`" (ufbx.c:4738); only the
        // non-ASCII/NUL detection is added, so the two intentionally agree on the hash
        // but disagree with the naive "any byte >= 0x80" test for length < 4 tails.
        internal static uint HashStringCheckAscii(byte[] str, int offset, int length, out bool nonAscii)
        {
            uint asciiMask = 0;
            uint zeroMask = 0;

            // C: ufbx_assert(length > 0) (ufbx.c:4744) — no call site hashes an empty string.

            uint hash = unchecked((uint)length);
            uint seed = Seed;
            int p = offset;
            int len = length;
            if (length >= 4) {
                do {
                    uint word = ReadU32(str, p);
                    asciiMask |= word;
                    zeroMask |= 0x80808080u - word;

                    hash = ((hash << 5 | hash >> 27) ^ word) * seed;
                    p += 4;
                    len -= 4;
                } while (len >= 4);

                uint tailWord = ReadU32(str, p + len - 4);
                asciiMask |= tailWord;
                zeroMask |= 0x80808080u - tailWord;

                hash = ((hash << 5 | hash >> 27) ^ tailWord) * seed;
            } else {
                uint word = 0;
                if (length >= 1) word |= (uint)(byte)str[offset + 0] << 0;
                if (length >= 2) word |= (uint)(byte)str[offset + 1] << 8;
                if (length >= 3) word |= (uint)(byte)str[offset + 2] << 16;

                asciiMask |= word;
                // C: (UINT32_C(0x80808080) >> ((4u - length) * 8u)) - word — the mask keeps the
                // unused high bytes of the partial word from producing a bogus zero-byte hit.
                zeroMask |= (0x80808080u >> (int)((4u - unchecked((uint)length)) * 8u)) - word;

                hash = ((hash << 5 | hash >> 27) ^ word) * seed;
            }

            // C: "If any character has high bit set or is zero we're not ASCII" (ufbx.c:4776)
            nonAscii = ((asciiMask | zeroMask) & 0x80808080u) != 0;

            hash ^= hash >> 16;
            hash *= MixMul;
            hash ^= hash >> 15;

            return hash;
        }

        // C: ufbxi_hash_string_check_ascii (ufbx.c:4739-4786), raw-byte `string` input.
        internal static uint HashStringCheckAscii(string str, int offset, int length, out bool nonAscii)
        {
            uint asciiMask = 0;
            uint zeroMask = 0;

            uint hash = unchecked((uint)length);
            uint seed = Seed;
            int p = offset;
            int len = length;
            if (length >= 4) {
                do {
                    uint word = ReadU32Str(str, p);
                    asciiMask |= word;
                    zeroMask |= 0x80808080u - word;

                    hash = ((hash << 5 | hash >> 27) ^ word) * seed;
                    p += 4;
                    len -= 4;
                } while (len >= 4);

                uint tailWord = ReadU32Str(str, p + len - 4);
                asciiMask |= tailWord;
                zeroMask |= 0x80808080u - tailWord;

                hash = ((hash << 5 | hash >> 27) ^ tailWord) * seed;
            } else {
                uint word = 0;
                if (length >= 1) word |= (uint)(byte)str[offset + 0] << 0;
                if (length >= 2) word |= (uint)(byte)str[offset + 1] << 8;
                if (length >= 3) word |= (uint)(byte)str[offset + 2] << 16;

                asciiMask |= word;
                zeroMask |= (0x80808080u >> (int)((4u - unchecked((uint)length)) * 8u)) - word;

                hash = ((hash << 5 | hash >> 27) ^ word) * seed;
            }

            nonAscii = ((asciiMask | zeroMask) & 0x80808080u) != 0;

            hash ^= hash >> 16;
            hash *= MixMul;
            hash ^= hash >> 15;

            return hash;
        }

        // C: ufbxi_hash32 (ufbx.c:4788-4796).
        internal static uint Hash32(uint x)
        {
            x ^= x >> 16;
            x *= MixMul;
            x ^= x >> 15;
            x *= MixMul2;
            x ^= x >> 16;
            return x;
        }

        // C: ufbxi_hash64 (ufbx.c:4798-4806).
        internal static uint Hash64(ulong x)
        {
            x ^= x >> 32;
            x *= 0xd6e8feb86659fd93ul;
            x ^= x >> 32;
            x *= 0xd6e8feb86659fd93ul;
            x ^= x >> 32;
            return unchecked((uint)x);
        }

        // C: ufbxi_hash_uptr (ufbx.c:4808-4819), UFBXI_UINTPTR_SIZE == 8 path (x64 build).
        // "Pointer" keys are modelled as stable integer ids (see UfbxiPtr in Map.cs), so
        // this hashes that id; C hashes the raw address.
        internal static uint HashUptr(ulong ptr)
        {
            return Hash64(ptr);
        }

        // C: ufbxi_hash_ptr_id (ufbx.c:4821-4825) — "Trivial reduction is fine: Only `ptr`
        // or `id` is defined", i.e. exactly one of the two fields is meaningful.
        internal static uint HashPtrId(UfbxiPtrId id)
        {
            return HashUptr(id.Ptr) ^ Hash64(id.Id);
        }

        // C: #define ufbxi_hash_ptr(ptr) ufbxi_hash_uptr((uintptr_t)(ptr)) (ufbx.c:4827).
        internal static uint HashPtr(ulong ptr)
        {
            return HashUptr(ptr);
        }
    }
}
