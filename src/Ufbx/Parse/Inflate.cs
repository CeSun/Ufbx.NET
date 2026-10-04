// DEFLATE inflate, ported from ufbx.c v0.23.1 (ufbx.c:1871-3280).
//
// Covers `ufbxi_deflate_context`, `ufbxi_adler32()`, `ufbxi_inflate_block_slow()`,
// `ufbxi_inflate_block_fast()`, `ufbxi_inflate_init_retain()` and the public entry point
// `ufbx_inflate()`. The bit/chunk reader they drive is in BitStream.cs and the Huffman
// tables in Huff.cs.
//
// Error codes are the negative `ptrdiff_t` values documented at ufbx.c:3106-3134 and are
// returned verbatim (C: `ufbx_inflate()` returns them, `ufbxi_deflate_threaded_imp()`
// ufbx.c:8935-8941 and ufbxi_binary_parse_array ufbx.c:9220 turn them into the messages
// "Cancelled" for -28 and "Bad DEFLATE data" otherwise).
//
// The pointer-based output window (`out_begin`/`out_ptr`/`out_end`) becomes (array, index)
// triplets; `OutBegin` is always the array offset the destination starts at.
//
// Configuration note: the golden build is x64 with SSE enabled, so `ufbxi_adler32()` runs
// its vectorized accumulation there. The SSE block (ufbx.c:2688-2747) and the generic
// little-endian block (ufbx.c:2748-2793) are exact speedups of the scalar tail that is
// ported here (they only change when the modulo is applied, and the 380368439 chunk bound
// guarantees the 64-bit sums never wrap in between), so the checksum is bit-identical.

using System.Runtime.CompilerServices;

namespace Ufbx
{
    // C: ufbxi_deflate_context (ufbx.c:2031-2038)
    internal sealed class UfbxiDeflateContext
    {
        public readonly UfbxiBitStream Stream = new UfbxiBitStream(); // C: ufbxi_bit_stream stream

        public uint FastBits;                                          // C: uint32_t fast_bits

        public byte[] OutArray;   // C: char *out_begin base buffer
        public int OutBegin;      // C: char *out_begin
        public int OutPtr;        // C: char *out_ptr
        public int OutEnd;        // C: char *out_end
    }

    internal static class UfbxiInflate
    {
        // C: #define UFBXI_INFLATE_FAST_MIN_IN 8 (ufbx.c:1871)
        const int InflateFastMinIn = 8;

        // C: #define UFBXI_INFLATE_FAST_MIN_OUT 2 (ufbx.c:1872)
        const int InflateFastMinOut = 2;

        // C: ufbxi_inflate_retain_imp (ufbx.c:2024-2029) reinterprets the opaque
        // `uint64_t data[1024]` blob of `ufbx_inflate_retain` (ufbx.h:4446-4449) as
        // `{ bool initialized; ufbxi_trees static_trees; }`. The port keeps the public
        // `Initialized` flag and hangs the cached trees beside the object instead of
        // aliasing its bytes.
        // NOTE (C): `ufbx_inflate()` casts and dereferences `retain` without a NULL check
        // (ufbx.c:3137, 3216); a NULL retain is undefined behaviour there and throws here.
        static readonly ConditionalWeakTable<UfbxInflateRetain, UfbxiTrees> s_retainTrees =
            new ConditionalWeakTable<UfbxInflateRetain, UfbxiTrees>();

        static UfbxiTrees RetainTrees(UfbxInflateRetain retain)
        {
            return s_retainTrees.GetValue(retain, _ => new UfbxiTrees());
        }

        // C: ufbxi_adler32() (ufbx.c:2666-2805)
        public static uint UfbxiAdler32(byte[] data, int offset, int size)
        {
            ulong a = 1, b = 0;
            int p = 0;

            // Adler-32 consists of two running sums modulo 65521. As an optimization
            // we can accumulate N sums before applying the modulo, where N depends on
            // the size of the type holding the sum.
            // C: sizeof(ufbxi_fast_uint) == 8 ? 380368439u : 5552u; ufbxi_fast_uint is
            // `size_t` (ufbx.c:900-904) and the golden build is 64-bit.
            const ulong NumBeforeWrap = 380368439u;

            ulong sizeLeft = (ulong)size;
            while (sizeLeft > 0)
            {
                ulong num = sizeLeft <= NumBeforeWrap ? sizeLeft : NumBeforeWrap;
                sizeLeft -= num;
                int end = p + (int)num;

                // C: ufbx.c:2682-2686 pre-aligns the pointer to 16 bytes and
                // ufbx.c:2688-2793 accumulates the aligned part with SSE/word-parallel
                // code; both only shuffle work between vector lanes and the scalar loop
                // below, whose 64-bit sums provably do not wrap for `num <= 380368439`.

                while (p != end)
                {
                    a += data[offset + p]; b += a;
                    p++;
                }

                a %= 65521u;
                b %= 65521u;
            }

            return (uint)((b << 16) | (a & 0xffff));
        }

        // C: #define ufbxi_copy_16_bytes(dst, src) (ufbx.c:885/894: SSE movdqu or
        // memcpy(dst, src, 16)). Only ever called with a non-overlapping 16-byte window
        // (guaranteed by `distance >= min_dist`, see the callers below).
        static void Copy16Bytes(byte[] arr, int dst, int src)
        {
            System.Array.Copy(arr, src, arr, dst, 16);
        }

        // C: ufbxi_inflate_block_slow() (ufbx.c:2807-2907)
        // Returns 0 on end-of-block, 1 when `max_symbols` ran out, negative on error.
        public static int InflateBlockSlow(UfbxiDeflateContext dc, UfbxiTrees trees, ulong maxSymbols)
        {
            UfbxiBitStream s = dc.Stream;
            byte[] outArr = dc.OutArray;
            int out_ptr = dc.OutPtr;
            int out_begin = dc.OutBegin;
            int out_end = dc.OutEnd;

            uint fastBits = trees.FastBits;
            uint fastMask = (1u << (int)fastBits) - 1;

            ulong bits = s.Bits;
            int left = s.Left;
            int data = s.ChunkPtr;

            for (;;)
            {
                if (maxSymbols-- == 0) break;

                s.BitRefill(ref bits, ref left, ref data);
                if (s.StopError != 0) return s.StopError;
                ulong symBits = bits;

                ushort sym0 = UfbxiHuff.HuffDecodeBits(trees.LitLength, bits, fastBits, fastMask);

                uint sym0Bits = UfbxiHuff.SymTotalBits(sym0);

                bits >>= (int)sym0Bits;
                left -= (int)sym0Bits;
                if ((sym0 & UfbxiHuff.SymEnd) != 0)
                {
                    if (UfbxiHuff.SymValue(sym0) != 0) return -13;

                    dc.OutPtr = out_ptr;
                    s.Bits = bits;
                    s.Left = left;
                    s.ChunkPtr = data;
                    return 0;
                }
                else if ((sym0 & UfbxiHuff.SymMatch) == 0)
                {
                    if (out_ptr == out_end) return -10;
                    outArr[out_ptr++] = (byte)UfbxiHuff.SymValue(sym0);
                    continue;
                }

                uint sym0Value = UfbxiHuff.SymValue(sym0);
                uint lenShiftBase = trees.LitLength.ExtraShiftBase[sym0Value];
                ushort lenMask = trees.LitLength.ExtraMask[sym0Value];
                uint length = (lenShiftBase >> 16) + (uint)(UfbxiBitStream.WrapShr64(symBits, lenShiftBase) & lenMask);

                ushort sym1 = UfbxiHuff.HuffDecodeBits(trees.Dist, bits, fastBits, fastMask);
                if ((sym1 & UfbxiHuff.SymEnd) != 0) return -11;

                uint sym1Bits = UfbxiHuff.SymTotalBits(sym1);

                bits >>= (int)sym1Bits;
                left -= (int)sym1Bits;

                uint sym1Value = UfbxiHuff.SymValue(sym1);
                uint distShiftBase = trees.Dist.ExtraShiftBase[sym1Value];
                ushort distMask = trees.Dist.ExtraMask[sym1Value];
                uint distance = (distShiftBase >> 16)
                    + (uint)(UfbxiBitStream.WrapShr64(symBits, distShiftBase + (uint)sym0) & distMask);

                // Bounds checking
                ulong outSpace = (ulong)(out_end - out_ptr);
                if ((long)distance > (long)(out_ptr - out_begin) || (ulong)length > outSpace)
                {
                    return -12;
                }

                // Copy the match
                int src = out_ptr - (int)distance;
                int dst = out_ptr;
                int end = dst + (int)length;
                out_ptr += (int)length;

                if (outSpace >= (ulong)length + 16ul)
                {
                    uint minDist = length < 16 ? length : 16;
                    if (distance >= minDist)
                    {
                        Copy16Bytes(outArr, dst, src);
                        while (length > 16)
                        {
                            src += 16;
                            dst += 16;
                            length -= 16;
                            Copy16Bytes(outArr, dst, src);
                        }
                    }
                    else
                    {
                        while (dst != end)
                        {
                            outArr[dst++] = outArr[src++];
                        }
                    }
                }
                else
                {
                    while (dst != end)
                    {
                        outArr[dst++] = outArr[src++];
                    }
                }
            }

            dc.OutPtr = out_ptr;
            s.Bits = bits;
            s.Left = left;
            s.ChunkPtr = data;
            return 1;
        }

        // Optimized version of `ufbxi_inflate_block_slow()`.
        // Has a lot of assumptions (see asserts) and does not call _any_ (even forceinlined)
        // functions. C: ufbxi_inflate_block_fast() (ufbx.c:2912-3095).
        //
        // C: ufbxi_dev_assert() (ufbx.c:2917-2920) is a no-op in the default build; the
        // conditions it checks are the preconditions `ufbx_inflate()` establishes at
        // ufbx.c:3229-3232 (stop_error clear, fast_bits == 10, >= 8 bytes of chunk and
        // >= 2 bytes of output available).
        public static int InflateBlockFast(UfbxiDeflateContext dc, UfbxiTrees trees)
        {
            UfbxiBitStream s = dc.Stream;
            byte[] outArr = dc.OutArray;
            int out_ptr = dc.OutPtr;
            int out_begin = dc.OutBegin;
            int out_end = dc.OutEnd - InflateFastMinOut;

            UfbxiHuffTree treeLitLength = trees.LitLength;
            UfbxiHuffTree treeDist = trees.Dist;

            ulong bits = s.Bits;
            int left = s.Left;
            int data = s.ChunkPtr;
            int dataEnd = s.ChunkYield - InflateFastMinIn;

            ulong sym01Bits;
            ushort sym0, sym1;
            ulong refillBits = s.ReadU64(data);

            // C: #define ufbxi_fast_inflate_refill_and_decode() (ufbx.c:2938-2944) and
            // #define ufbxi_fast_inflate_should_continue() (ufbx.c:2946-2947); the macros
            // mutate locals, so they are expanded inline at every call site below.

            // -- ufbxi_fast_inflate_refill_and_decode()
            bits |= refillBits << left;
            data += (63 - left) >> 3;
            left |= 56;
            sym01Bits = bits;
            sym0 = treeLitLength.FastSym[(int)(sym01Bits & UfbxiHuff.HuffFastMask)];
            sym1 = ((sym0 & UfbxiHuff.SymMatch) != 0 ? treeDist : treeLitLength)
                .FastSym[(int)(UfbxiBitStream.WrapShr64(sym01Bits, (uint)sym0) & UfbxiHuff.HuffFastMask)];
            refillBits = s.ReadU64(data);
            // --

            for (;;)
            {
                if ((sym0 & sym1 & UfbxiHuff.SymFast) != 0)
                {
                    bits = UfbxiBitStream.WrapShr64(sym01Bits, (uint)(sym0 + sym1));
                    left -= (sym0 + sym1) & 0x3f;

                    if (((sym0 | sym1) & UfbxiHuff.SymMatch) == 0)
                    {
                        // Literal, Literal
                        // -> Output the two literals and loop back to start.

                        outArr[out_ptr] = (byte)UfbxiHuff.SymValue(sym0);
                        outArr[out_ptr + 1] = (byte)UfbxiHuff.SymValue(sym1);
                        out_ptr += 2;

                        // -- ufbxi_fast_inflate_refill_and_decode()
                        bits |= refillBits << left;
                        data += (63 - left) >> 3;
                        left |= 56;
                        sym01Bits = bits;
                        sym0 = treeLitLength.FastSym[(int)(sym01Bits & UfbxiHuff.HuffFastMask)];
                        sym1 = ((sym0 & UfbxiHuff.SymMatch) != 0 ? treeDist : treeLitLength)
                            .FastSym[(int)(UfbxiBitStream.WrapShr64(sym01Bits, (uint)sym0) & UfbxiHuff.HuffFastMask)];
                        refillBits = s.ReadU64(data);
                        // --
                        if ((((long)dataEnd - data) | ((long)out_end - out_ptr)) >= 0) continue;
                        break;
                    }
                    else if ((sym0 & UfbxiHuff.SymMatch) == 0)
                    {
                        // Literal, Match, (Distance)
                        // -> Output a single literal, decode the missing distance and fall
                        //    through to match.

                        outArr[out_ptr] = (byte)UfbxiHuff.SymValue(sym0);
                        out_ptr += 1;

                        sym01Bits = UfbxiBitStream.WrapShr64(sym01Bits, (uint)sym0);

                        // This must fit as literals never have extra bits and the match length
                        // is fast so:
                        // 10 (lit) + 10 (len code) + 5 (len extra) + 15 (dist code) + 13 (dist
                        // extra) = 53 <= 56
                        sym0 = sym1;
                        sym1 = treeDist.FastSym[(int)(bits & UfbxiHuff.HuffFastMask)];

                        if ((sym1 & UfbxiHuff.SymFast) == 0)
                        {
                            // Slow sym1
                            if ((sym1 & UfbxiHuff.SymEnd) != 0) return -11;
                            uint tail = (uint)(bits >> (int)UfbxiHuff.HuffFastBits);
                            uint longMask = UfbxiHuff.SymLongMask(sym1);
                            sym1 = treeDist.LongSym[UfbxiHuff.SymLongOffset(sym1) + (tail & longMask)];
                            if ((sym1 & UfbxiHuff.SymEnd) != 0) return -11;
                        }

                        bits = UfbxiBitStream.WrapShr64(bits, (uint)sym1);
                        left -= sym1 & 0x3f;
                    }
                    else
                    {
                        // Match, Distance
                        // -> Fall through to match copy.
                    }
                }
                else
                {
                    if ((sym0 & (UfbxiHuff.SymFast | UfbxiHuff.SymEnd)) == 0)
                    {
                        // Slow sym0
                        uint tail = (uint)(sym01Bits >> (int)UfbxiHuff.HuffFastBits);
                        uint longMask = UfbxiHuff.SymLongMask(sym0);
                        sym0 = treeLitLength.LongSym[UfbxiHuff.SymLongOffset(sym0) + (tail & longMask)];
                    }

                    uint sym0Bits = UfbxiHuff.SymTotalBits(sym0);
                    bits >>= (int)sym0Bits;
                    left -= (int)sym0Bits;

                    if ((sym0 & UfbxiHuff.SymEnd) != 0)
                    {
                        if (UfbxiHuff.SymValue(sym0) != 0) return -13;
                        dc.OutPtr = out_ptr;
                        s.Bits = bits;
                        s.Left = left;
                        s.ChunkPtr = data;
                        return 0;
                    }

                    if ((sym0 & UfbxiHuff.SymMatch) != 0)
                    {
                        sym1 = treeDist.FastSym[(int)(bits & UfbxiHuff.HuffFastMask)];

                        if ((sym1 & UfbxiHuff.SymFast) == 0)
                        {
                            // Slow sym1
                            if ((sym1 & UfbxiHuff.SymEnd) != 0) return -11;
                            uint tail = (uint)(bits >> (int)UfbxiHuff.HuffFastBits);
                            uint longMask = UfbxiHuff.SymLongMask(sym1);
                            sym1 = treeDist.LongSym[UfbxiHuff.SymLongOffset(sym1) + (tail & longMask)];
                            if ((sym1 & UfbxiHuff.SymEnd) != 0) return -11;
                        }

                        bits = UfbxiBitStream.WrapShr64(bits, (uint)sym1);
                        left -= sym1 & 0x3f;
                    }
                    else
                    {
                        outArr[out_ptr++] = (byte)UfbxiHuff.SymValue(sym0);

                        // -- ufbxi_fast_inflate_refill_and_decode()
                        bits |= refillBits << left;
                        data += (63 - left) >> 3;
                        left |= 56;
                        sym01Bits = bits;
                        sym0 = treeLitLength.FastSym[(int)(sym01Bits & UfbxiHuff.HuffFastMask)];
                        sym1 = ((sym0 & UfbxiHuff.SymMatch) != 0 ? treeDist : treeLitLength)
                            .FastSym[(int)(UfbxiBitStream.WrapShr64(sym01Bits, (uint)sym0) & UfbxiHuff.HuffFastMask)];
                        refillBits = s.ReadU64(data);
                        // --
                        if ((((long)dataEnd - data) | ((long)out_end - out_ptr)) >= 0) continue;
                        break;
                    }
                }

                uint sym0Value = UfbxiHuff.SymValue(sym0);
                uint lenShiftBase = trees.LitLength.ExtraShiftBase[sym0Value];
                ushort lenMask = trees.LitLength.ExtraMask[sym0Value];
                uint length = (lenShiftBase >> 16) + (uint)(UfbxiBitStream.WrapShr64(sym01Bits, lenShiftBase) & lenMask);

                uint sym1Value = UfbxiHuff.SymValue(sym1);
                uint distShiftBase = trees.Dist.ExtraShiftBase[sym1Value];
                ushort distMask = trees.Dist.ExtraMask[sym1Value];
                uint distance = (distShiftBase >> 16)
                    + (uint)(UfbxiBitStream.WrapShr64(sym01Bits, distShiftBase + (uint)sym0) & distMask);

                // -- ufbxi_fast_inflate_refill_and_decode()
                bits |= refillBits << left;
                data += (63 - left) >> 3;
                left |= 56;
                sym01Bits = bits;
                sym0 = treeLitLength.FastSym[(int)(sym01Bits & UfbxiHuff.HuffFastMask)];
                sym1 = ((sym0 & UfbxiHuff.SymMatch) != 0 ? treeDist : treeLitLength)
                    .FastSym[(int)(UfbxiBitStream.WrapShr64(sym01Bits, (uint)sym0) & UfbxiHuff.HuffFastMask)];
                refillBits = s.ReadU64(data);
                // --

                // Bounds checking: We don't actually handle the error here, just bail out to
                // the slow implementation
                long dstSpace = (long)out_end - out_ptr - (long)length + InflateFastMinOut;
                long srcSpace = (long)out_ptr - out_begin - (long)distance;
                if ((dstSpace | srcSpace) < 0)
                {
                    return -12;
                }

                int src = out_ptr - (int)distance;
                int dst = out_ptr;
                int end = dst + (int)length;
                out_ptr += (int)length;

                // Copy the match

                uint minDist = length < 16 ? length : 16;
                if (distance >= minDist && dstSpace >= 16)
                {
                    Copy16Bytes(outArr, dst, src);
                    while (length > 16)
                    {
                        src += 16;
                        dst += 16;
                        length -= 16;
                        Copy16Bytes(outArr, dst, src);
                    }
                }
                else
                {
                    while (dst != end)
                    {
                        outArr[dst++] = outArr[src++];
                    }
                }

                if ((((long)dataEnd - data) | ((long)out_end - out_ptr)) >= 0) continue;
                break;
            }

            dc.OutPtr = out_ptr;
            s.Bits = bits;
            s.Left = left;
            s.ChunkPtr = data;
            return 1;
        }

        // C: ufbxi_inflate_init_retain() (ufbx.c:3097-3104)
        public static void InflateInitRetain(UfbxInflateRetain retain)
        {
            if (!retain.Initialized)
            {
                UfbxiHuff.InitStaticHuff(RetainTrees(retain), null);
                retain.Initialized = true;
            }
        }

        // Returns actual number of decompressed bytes or a negative error code, see the
        // list at ufbx.c:3106-3134.
        // C: ufbx_inflate() (ufbx.c:3106-3280) — public entry point; C returns `ptrdiff_t`,
        // which is `int` for every buffer size this port can address (PORTING_NOTES: size_t->int).
        public static int UfbxInflate(byte[] dst, int dstSize, UfbxInflateInput input, UfbxInflateRetain retain)
        {
            int err;
            UfbxiDeflateContext dc = new UfbxiDeflateContext();
            UfbxiBitStream s = dc.Stream;
            s.BitStreamInit(input);
            dc.OutArray = dst;
            dc.OutBegin = 0;
            dc.OutPtr = 0;
            dc.OutEnd = dstSize;
            if (input.InternalFastBits != 0)
            {
                dc.FastBits = (uint)input.InternalFastBits;
                if (dc.FastBits < 1 || dc.FastBits == 9 || dc.FastBits > 10) return -29;
            }
            else
            {
                // TODO: Profile this
                dc.FastBits = input.TotalSize > 2048 ? 10u : 8u;
            }

            ulong bits = s.Bits;
            int left = s.Left;
            int data = s.ChunkPtr;

            s.BitRefill(ref bits, ref left, ref data);
            if (s.StopError != 0) return s.StopError;

            // Zlib header
            if (!input.NoHeader)
            {
                uint cmf = (uint)(bits & 0xff);
                uint flg = (uint)(bits >> 8) & 0xff;
                bits >>= 16;
                left -= 16;

                if ((cmf & 0xf) != 0x8) return -1;
                if ((flg & 0x20) != 0) return -2;
                if ((cmf << 8 | flg) % 31u != 0) return -3;
                if ((cmf >> 4) > 7) return -30;
            }

            for (;;)
            {
                s.BitRefill(ref bits, ref left, ref data);
                if (s.StopError != 0) return s.StopError;

                // Block header: [0:1] BFINAL [1:3] BTYPE
                uint header = (uint)bits & 0x7;
                bits >>= 3;
                left -= 3;

                uint type = header >> 1;
                if (type == 0)
                {
                    // Round up to the next byte
                    int alignBits = left & 0x7;
                    bits >>= alignBits;
                    left -= alignBits;

                    uint len = (uint)(bits & 0xffff);
                    uint nlen = (uint)((bits >> 16) & 0xffff);
                    if ((len ^ nlen) != 0xffff) return -4;
                    if ((long)(dc.OutEnd - dc.OutPtr) < (long)len) return -6;
                    bits >>= 32;
                    left -= 32;

                    s.Bits = bits;
                    s.Left = left;
                    s.ChunkPtr = data;

                    // Copy `len` bytes of literal data
                    if (!s.BitCopyBytes(dc.OutArray, dc.OutPtr, (int)len)) return -5;

                    dc.OutPtr += (int)len;
                }
                else if (type <= 2)
                {
                    s.Bits = bits;
                    s.Left = left;
                    s.ChunkPtr = data;

                    UfbxiTrees treeData = new UfbxiTrees();
                    UfbxiTrees trees;
                    if (type == 1)
                    {
                        // Static Huffman: Initialize the trees once and cache them in `retain`.
                        if (!retain.Initialized)
                        {
                            UfbxiHuff.InitStaticHuff(RetainTrees(retain), input);
                            retain.Initialized = true;
                        }
                        trees = RetainTrees(retain);
                    }
                    else
                    {
                        // Dynamic Huffman
                        err = UfbxiHuff.InitDynamicHuff(dc, treeData);
                        if (err != 0) return err;
                        trees = treeData;
                    }

                    for (;;)
                    {
                        bool fastViable = trees.FastBits == UfbxiHuff.HuffFastBits
                            && dc.OutEnd - dc.OutPtr >= InflateFastMinOut;

                        // `ufbxi_inflate_block_fast()` needs a bit more upfront setup,
                        // see asserts on top of the function
                        if (fastViable && s.ChunkYield - s.ChunkPtr >= InflateFastMinIn)
                        {
                            err = InflateBlockFast(dc, trees);
                        }
                        else
                        {
                            err = InflateBlockSlow(dc, trees, fastViable ? 32ul : ulong.MaxValue);
                        }

                        if (err < 0) return err;

                        // `ufbxi_inflate_block()` returns normally on cancel so check it here
                        if (s.StopError != 0) return s.StopError;

                        if (err == 0) break;
                    }
                }
                else
                {
                    // 0b11 - reserved (error)
                    return -7;
                }

                bits = s.Bits;
                left = s.Left;
                data = s.ChunkPtr;

                // BFINAL: End of stream
                if ((header & 1) != 0) break;
            }

            // Check Adler-32
            {
                // Round up to the next byte
                int alignBits = left & 0x7;
                bits >>= alignBits;
                left -= alignBits;
                s.BitRefill(ref bits, ref left, ref data);
                if (s.StopError != 0) return s.StopError;

                if (!input.NoChecksum)
                {
                    uint reference = (uint)bits;
                    reference = (reference >> 24) | ((reference >> 8) & 0xff00) | ((reference << 8) & 0xff0000) | (reference << 24);

                    uint checksum = UfbxiAdler32(dc.OutArray, dc.OutBegin, dc.OutPtr - dc.OutBegin);
                    if (reference != checksum)
                    {
                        return -9;
                    }
                }
            }

            return dc.OutPtr - dc.OutBegin;
        }
    }
}
