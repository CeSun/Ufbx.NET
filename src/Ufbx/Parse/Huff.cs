// DEFLATE Huffman tables, ported from ufbx.c v0.23.1 (ufbx.c:1852-2664).
//
// Symbol packing (ufbx.c:1933-1996): a `ufbxi_huff_sym` is a `uint16_t` whose bit fields are
//   [0:5]  total_bits (fast symbol) or extra_mask (long-table prefix entry)
//   [5]    end        (UFBXI_HUFF_SYM_END   = 0x20)
//   [6]    match      (UFBXI_HUFF_SYM_MATCH = 0x40)
//   [7]    fast       (UFBXI_HUFF_SYM_FAST  = 0x80)
//   [8:16] value      (literal byte / length index / distance index / code length /
//                      long_sym base offset "halved" / 8-bit code prefix)
// The `sym = (i << 8 | bits) + extra` arithmetic at ufbx.c:2368-2381 works because the
// DEFLATE lookup tables already store their flags in bit positions [5:8] and their extra bit
// count in [0:5], so adding `extra` to `sym` turns `bits` into `bits + extra_bits` (the
// total symbol bit count) and ORs in the MATCH/END flags.
//
// The port uses managed arrays for `ufbxi_huff_tree`; C's uninitialized stack arrays are
// zero-initialized here, which is equivalent because `ufbxi_huff_build_imp()` writes every
// element it later reads (the entries beyond `fast_mask` are never looked up, and
// `past_max_code[]`/`code_to_sorted[]` are fully written for all 16 lengths). The
// `UFBX_REGRESSION`-only clearing to `UFBXI_HUFF_UNINITIALIZED_SYM` (ufbx.c:2273-2285) and
// the matching asserts therefore have no observable effect and are not ported.

namespace Ufbx
{
    // C: ufbxi_huff_tree (ufbx.c:1998-2011)
    internal sealed class UfbxiHuffTree
    {
        // C: ufbxi_huff_sym fast_sym[UFBXI_HUFF_FAST_SIZE] (ufbx.c:1999)
        public readonly ushort[] FastSym = new ushort[UfbxiHuff.HuffFastSize];

        // C: ufbxi_huff_sym long_sym[UFBXI_HUFF_MAX_LONG_SYMS] (ufbx.c:2000)
        public readonly ushort[] LongSym = new ushort[UfbxiHuff.HuffMaxLongSyms];

        // C: ufbxi_huff_sym sorted_to_sym[UFBXI_HUFF_MAX_VALUE] (ufbx.c:2001)
        public readonly ushort[] SortedToSym = new ushort[UfbxiHuff.HuffMaxValue];

        // C: uint32_t extra_shift_base[UFBXI_HUFF_MAX_EXTRA_SYMS] (ufbx.c:2003) — [0:6] shift, [16:32] base
        public readonly uint[] ExtraShiftBase = new uint[UfbxiHuff.HuffMaxExtraSyms];

        // C: uint16_t extra_mask[UFBXI_HUFF_MAX_EXTRA_SYMS] (ufbx.c:2004)
        public readonly ushort[] ExtraMask = new ushort[UfbxiHuff.HuffMaxExtraSyms];

        // C: uint16_t past_max_code[UFBXI_HUFF_MAX_BITS] (ufbx.c:2006)
        public readonly ushort[] PastMaxCode = new ushort[UfbxiHuff.HuffMaxBits];

        // C: int16_t code_to_sorted[UFBXI_HUFF_MAX_BITS] (ufbx.c:2007)
        public readonly short[] CodeToSorted = new short[UfbxiHuff.HuffMaxBits];

        // C: uint32_t num_symbols (ufbx.c:2008)
        public uint NumSymbols;

        // C: uint32_t end_of_block_bits (ufbx.c:2010)
        public uint EndOfBlockBits;
    }

    // C: ufbxi_trees (ufbx.c:2013-2022) — the anonymous union with `trees[2]` is just the
    // two members, which is all the C code ever addresses.
    internal sealed class UfbxiTrees
    {
        public readonly UfbxiHuffTree LitLength = new UfbxiHuffTree(); // C: lit_length
        public readonly UfbxiHuffTree Dist = new UfbxiHuffTree();      // C: dist
        public uint FastBits;                                          // C: fast_bits
    }

    internal static class UfbxiHuff
    {
        // C: #define UFBXI_HUFF_MAX_BITS 16 (ufbx.c:1874)
        public const int HuffMaxBits = 16;

        // C: #define UFBXI_HUFF_MAX_VALUE 288 (ufbx.c:1875)
        public const int HuffMaxValue = 288;

        // C: #define UFBXI_HUFF_FAST_BITS 10 (ufbx.c:1876)
        public const uint HuffFastBits = 10;

        // C: #define UFBXI_HUFF_FAST_SIZE (1 << UFBXI_HUFF_FAST_BITS) (ufbx.c:1877)
        public const int HuffFastSize = 1 << (int)HuffFastBits;

        // C: #define UFBXI_HUFF_FAST_MASK (UFBXI_HUFF_FAST_SIZE - 1) (ufbx.c:1878)
        public const uint HuffFastMask = (uint)(HuffFastSize - 1);

        // C: #define UFBXI_HUFF_MAX_LONG_BITS 5 (ufbx.c:1879)
        public const int HuffMaxLongBits = 5;

        // C: #define UFBXI_HUFF_MAX_LONG_SYMS 380 (ufbx.c:1880)
        public const int HuffMaxLongSyms = 380;

        // C: #define UFBXI_HUFF_CODELEN_FAST_BITS 8 (ufbx.c:1882)
        public const uint HuffCodelenFastBits = 8;

        // C: #define UFBXI_HUFF_CODELEN_FAST_MASK ((1<<UFBXI_HUFF_CODELEN_FAST_BITS)-1) (ufbx.c:1883)
        public const uint HuffCodelenFastMask = (1u << (int)HuffCodelenFastBits) - 1;

        // C: #define UFBXI_HUFF_MAX_EXTRA_SYMS 32 (ufbx.c:1885)
        public const int HuffMaxExtraSyms = 32;

        // C: #define UFBXI_HUFF_CODELEN_SYMS 19 (ufbx.c:1887)
        public const uint HuffCodelenSyms = 19;

        // C: #define UFBXI_HUFF_MAX_COMBINED_SYMS 320 (ufbx.c:1888)
        public const int HuffMaxCombinedSyms = 320;

        // C: enum { UFBXI_HUFF_SYM_END = 0x20, UFBXI_HUFF_SYM_MATCH = 0x40, UFBXI_HUFF_SYM_FAST = 0x80 }
        //    (ufbx.c:1989-1993)
        public const int SymEnd = 0x20;
        public const int SymMatch = 0x40;
        public const int SymFast = 0x80;

        // C: #define UFBXI_HUFF_ERROR_SYM ((ufbxi_huff_sym)0x0120) (ufbx.c:1995)
        public const ushort ErrorSym = 0x0120;

        // C: #define ufbxi_huff_sym_total_bits(sym) ((uint32_t)(sym) & 0x1f) (ufbx.c:1984)
        public static uint SymTotalBits(ushort sym) { return (uint)sym & 0x1fu; }

        // C: #define ufbxi_huff_sym_long_mask(sym) ((uint32_t)(sym) & 0x1f) (ufbx.c:1985)
        public static uint SymLongMask(ushort sym) { return (uint)sym & 0x1fu; }

        // C: #define ufbxi_huff_sym_long_offset(sym) ((uint32_t)(sym) >> 7u) (ufbx.c:1986)
        public static uint SymLongOffset(ushort sym) { return (uint)sym >> 7; }

        // C: #define ufbxi_huff_sym_value(sym) ((uint32_t)(sym) >> 8u) (ufbx.c:1987)
        public static uint SymValue(ushort sym) { return (uint)sym >> 8; }

        // Lookup data: [0:5] extra bits [5:8] flags [16:32] base value
        // Generated by `misc/deflate_lut.py`
        // C: ufbxi_deflate_length_lut[] (ufbx.c:1854-1859)
        public static readonly uint[] DeflateLengthLut =
        {
            0x00000020, 0x00030040, 0x00040040, 0x00050040, 0x00060040, 0x00070040, 0x00080040, 0x00090040,
            0x000a0040, 0x000b0041, 0x000d0041, 0x000f0041, 0x00110041, 0x00130042, 0x00170042, 0x001b0042,
            0x001f0042, 0x00230043, 0x002b0043, 0x00330043, 0x003b0043, 0x00430044, 0x00530044, 0x00630044,
            0x00730044, 0x00830045, 0x00a30045, 0x00c30045, 0x00e30045, 0x01020040, 0x00010020, 0x00010020,
        };

        // C: ufbxi_deflate_dist_lut[] (ufbx.c:1860-1865)
        public static readonly uint[] DeflateDistLut =
        {
            0x00010000, 0x00020000, 0x00030000, 0x00040000, 0x00050001, 0x00070001, 0x00090002, 0x000d0002,
            0x00110003, 0x00190003, 0x00210004, 0x00310004, 0x00410005, 0x00610005, 0x00810006, 0x00c10006,
            0x01010007, 0x01810007, 0x02010008, 0x03010008, 0x04010009, 0x06010009, 0x0801000a, 0x0c01000a,
            0x1001000b, 0x1801000b, 0x2001000c, 0x3001000c, 0x4001000d, 0x6001000d, 0x00010020, 0x00010020,
        };

        // C: ufbxi_deflate_code_length_permutation[] (ufbx.c:1867-1869)
        public static readonly byte[] DeflateCodeLengthPermutation =
        {
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
        };

        // 0: Success
        // -1: Overfull
        // -2: Underfull
        // C: ufbxi_huff_build_imp() (ufbx.c:2252-2455)
        // `symBitsBegin` is C's pointer offset into `sym_bits` (used for the distance tree as
        // `code_lengths + num_lit_lengths`, ufbx.c:2660); symbol indices stay zero-based.
        public static int HuffBuildImp(UfbxiHuffTree tree, byte[] symBits, int symBitsBegin,
            uint symCount, uint[] symExtra, uint symExtraOffset, uint fastBits, uint[] bitsCounts)
        {
            int fastMask = (1 << (int)fastBits) - 1;

            tree.NumSymbols = symCount;

            uint nonzeroSymCount = symCount - bitsCounts[0];

            uint[] totalSyms = new uint[HuffMaxBits];  // C: uint32_t total_syms[..] (ufbx.c:2265)
            uint[] firstCode = new uint[HuffMaxBits];  // C: uint32_t first_code[..] (ufbx.c:2266)

            tree.CodeToSorted[0] = short.MaxValue;
            tree.PastMaxCode[0] = 0;
            totalSyms[0] = 0;

            uint lastValidPrefix = 0;

            // Resolve the maximum code per bit length and ensure that the tree is not
            // overfull or underfull.
            {
                int numCodesLeft = 1;
                uint code = 0;
                uint prevCount = 0;
                uint longOffset = 0;
                for (uint bits = 1; bits < HuffMaxBits; bits++)
                {
                    uint count = bitsCounts[bits];
                    code = (code + prevCount) << 1;
                    firstCode[bits] = code;
                    tree.PastMaxCode[bits] = (ushort)(code + count);

                    uint prevSyms = totalSyms[bits - 1];
                    totalSyms[bits] = prevSyms + count;

                    // Each bit level doubles the amount of codes and potentially removes some
                    numCodesLeft = (numCodesLeft << 1) - (int)count;
                    if (numCodesLeft < 0)
                    {
                        return -1;
                    }

                    if (count > 0 && bits > fastBits && bits - fastBits <= HuffMaxLongBits)
                    {
                        uint shift = bits - fastBits;
                        uint lastInclusive = numCodesLeft == 0 ? (1u << (int)shift) - 1u : 0u;
                        uint firstPrefix = code >> (int)shift;
                        uint lastPrefix = (code + count + lastInclusive) >> (int)shift;
                        uint mask = (1u << (int)shift) - 1u;
                        uint halfStep = 1u << (int)(shift - 1u);
                        for (uint prefix = firstPrefix; prefix < lastPrefix; prefix++)
                        {
                            uint revPrefix = UfbxiBitStream.BitReverse(prefix, fastBits);
                            tree.FastSym[revPrefix] = (ushort)(mask | (longOffset << 8));
                            longOffset += halfStep;
                        }

                        lastValidPrefix = lastPrefix;
                    }

                    if (count > 0)
                    {
                        tree.CodeToSorted[bits] = (short)((int)prevSyms - (int)code);
                    }
                    else
                    {
                        tree.CodeToSorted[bits] = short.MaxValue;
                    }
                    prevCount = count;
                }

                // All codes should be used if there's more than one symbol, if there's only one
                // symbol there should be only a single 1-bit code.
                if (nonzeroSymCount > 1 && numCodesLeft != 0)
                {
                    return -2;
                }
                else if (nonzeroSymCount == 1 && totalSyms[1] != 1)
                {
                    return -2;
                }

                // We should always have enough space for long symbols as we support up to 5
                // (UFBXI_HUFF_MAX_LONG_BITS) bits and the largest tree has 286 symbols. For each
                // bit we may waste at most 2^bits slots (conservative) and in the end we may
                // waste 2^5 slots giving us `286+2+4+8+16+32+32 = 380` (UFBXI_HUFF_MAX_LONG_SYMS)
                // C: ufbx_assert(long_offset <= UFBXI_HUFF_MAX_LONG_SYMS) — not ported (no-op).
            }

            tree.EndOfBlockBits = 0;
            uint numExtra = 0;
            tree.ExtraShiftBase[0] = 0;
            tree.ExtraMask[0] = 0;

            // Fill `fast_sym[]` with error symbols if necessary, we don't need to do this if we
            // have two or more symbols as the tree is guaranteed to be full, which means we will
            // populate the whole `fast_sym[]`
            if (nonzeroSymCount <= 1)
            {
                for (uint i = 0; i <= fastMask; i++)
                {
                    tree.FastSym[i] = ErrorSym;
                }
            }

            // Generate per-length sorted-to-symbol and fast lookup tables
            uint[] bitsIndex = new uint[HuffMaxBits]; // C: uint32_t bits_index[..] = { 0 } (ufbx.c:2363)
            for (uint i = 0; i < symCount; i++)
            {
                uint bits = symBits[symBitsBegin + i];
                if (bits == 0) continue;

                uint sym = i << 8 | bits;
                if (i >= symExtraOffset)
                {
                    uint extra = symExtra[i - symExtraOffset];
                    sym += extra;

                    // Store length/distance codes with extra values in a table.
                    // TODO: This is unnecessary for small values
                    if ((extra & 0xffff001f) != 0 && (extra & 0x20) == 0)
                    {
                        uint ix = ++numExtra;
                        tree.ExtraShiftBase[ix] = (extra & 0xffff0000) | bits;
                        tree.ExtraMask[ix] = (ushort)((1u << (int)(extra & 0x1f)) - 1);
                        sym = (sym & 0xff) | ix << 8;
                    }
                }

                uint index = bitsIndex[bits]++;
                uint sorted = totalSyms[bits - 1] + index;
                tree.SortedToSym[sorted] = (ushort)sym;

                // Reverse the code and fill all fast lookups with the reversed prefix
                uint code = firstCode[bits] + index;
                uint revCode = UfbxiBitStream.BitReverse(code, bits);

                if (bits <= fastBits)
                {
                    uint fastSym = sym;
                    // The `end` and `fast` flags are mutually exclusive
                    if ((fastSym & SymEnd) == 0)
                    {
                        fastSym |= SymFast;
                    }
                    uint hiMax = 1u << (int)(fastBits - bits);
                    for (uint hi = 0; hi < hiMax; hi++)
                    {
                        tree.FastSym[revCode | hi << (int)bits] = (ushort)fastSym;
                    }
                }
                else if (bits <= fastBits + HuffMaxLongBits && (code >> (int)(bits - fastBits)) < lastValidPrefix)
                {
                    uint fastSym = tree.FastSym[revCode & (uint)fastMask];
                    uint longBits = 0;

                    uint longMask = fastSym;
                    while (longBits < HuffMaxLongBits && (longMask & 1) != 0)
                    {
                        longMask >>= 1;
                        longBits += 1;
                    }

                    uint longBase = fastSym >> 7; // aka (fast_sym >> 8) * 2
                    uint loBits = bits - fastBits;
                    uint hiMax = 1u << (int)(longBits - loBits);
                    uint revSuffix = revCode >> (int)fastBits;
                    for (uint hi = 0; hi < hiMax; hi++)
                    {
                        tree.LongSym[longBase + (revSuffix | hi << (int)loBits)] = (ushort)sym;
                    }
                }
                else
                {
                    uint fastSym = (code >> (int)(bits - fastBits)) << 8;
                    tree.FastSym[revCode & (uint)fastMask] = (ushort)fastSym;
                }

                // Make sure the end-of-block symbol goes through the slow path
                // Also store the end-of-block code so we can interrupt decoding
                if (i == 256)
                {
                    tree.EndOfBlockBits = revCode;
                }
            }

            return 0;
        }

        // 0: Success
        // -1: Overfull
        // -2: Underfull
        // C: ufbxi_huff_build() (ufbx.c:2457-2474)
        public static int HuffBuild(UfbxiHuffTree tree, byte[] symBits, int symBitsBegin,
            uint symCount, uint[] symExtra, uint symExtraOffset, uint fastBits)
        {
            // Count the number of codes per bit length
            // `bits_counts[0]` contains the number of non-used symbols
            uint[] bitsCounts = new uint[HuffMaxBits];
            for (uint i = 0; i < symCount; i++)
            {
                uint bits = symBits[symBitsBegin + i];
                bitsCounts[bits]++;
            }

            return HuffBuildImp(tree, symBits, symBitsBegin, symCount, symExtra, symExtraOffset, fastBits, bitsCounts);
        }

        // C: ufbxi_huff_decode_bits() (ufbx.c:2476-2510)
        public static ushort HuffDecodeBits(UfbxiHuffTree tree, ulong bits, uint fastBits, uint fastMask)
        {
            ushort sym = tree.FastSym[(int)(bits & fastMask)];

            if ((sym & (SymFast | SymEnd)) != 0)
            {
                return sym;
            }

            uint tail = (uint)(bits >> (int)fastBits);
            uint longMask = SymLongMask(sym);
            if (longMask != 0)
            {
                sym = tree.LongSym[SymLongOffset(sym) + (tail & longMask)];
                return sym;
            }

            uint code = SymValue(sym);
            uint numBits = fastBits;
            for (;;)
            {
                code = code << 1 | (tail & 1);
                tail >>= 1;
                numBits++;

                if (code < tree.PastMaxCode[numBits])
                {
                    sym = tree.SortedToSym[(int)code + (int)tree.CodeToSorted[numBits]];
                    return sym;
                }
            }
        }

        // C: ufbxi_init_static_huff() (ufbx.c:2512-2540)
        public static void InitStaticHuff(UfbxiTrees trees, UfbxInflateInput input)
        {
            int err = 0;

            // Override `fast_bits` if necessary, this must always be valid as it's checked in the
            // beginning of `ufbx_inflate()`.
            if (input != null && input.InternalFastBits != 0)
            {
                trees.FastBits = (uint)input.InternalFastBits;
            }
            else
            {
                trees.FastBits = HuffFastBits;
            }

            // 0-143: 8 bits, 144-255: 9 bits, 256-279: 7 bits, 280-287: 8 bits
            byte[] litLengthBits = new byte[288];
            for (int i = 0; i < 144; i++) litLengthBits[i] = 8;
            for (int i = 144; i < 256; i++) litLengthBits[i] = 9;
            for (int i = 256; i < 280; i++) litLengthBits[i] = 7;
            for (int i = 280; i < 288; i++) litLengthBits[i] = 8;
            err |= HuffBuild(trees.LitLength, litLengthBits, 0, 288, DeflateLengthLut, 256, trees.FastBits);

            // "Distance codes 0-31 are represented by (fixed-length) 5-bit codes"
            byte[] distBits = new byte[32];
            for (int i = 0; i < 32; i++) distBits[i] = 5;
            err |= HuffBuild(trees.Dist, distBits, 0, 32, DeflateDistLut, 0, trees.FastBits);

            // Building the static trees cannot fail as we use pre-defined code lengths.
            // C: ufbxi_ignore(err); ufbx_assert(err == 0); — both are no-ops in the default
            // build, so the accumulated value is intentionally unused.
            _ = err;
        }

        // C: ufbxi_decode_dynamic_huff_bits() (ufbx.c:2542-2604)
        public static int DecodeDynamicHuffBits(UfbxiDeflateContext dc, UfbxiHuffTree huffCodeLength,
            byte[] codeLengths, uint numSymbols)
        {
            UfbxiBitStream s = dc.Stream;
            ulong bits = s.Bits;
            int left = s.Left;
            int data = s.ChunkPtr;

            uint symbolIndex = 0;
            byte prev = 0;
            while (symbolIndex < numSymbols)
            {
                s.BitRefill(ref bits, ref left, ref data);
                if (s.StopError != 0) return s.StopError;

                ushort sym = HuffDecodeBits(huffCodeLength, bits, HuffCodelenFastBits, HuffCodelenFastMask);
                if (sym == ErrorSym) return -21;

                uint inst = SymValue(sym);
                uint symLen = SymTotalBits(sym);

                bits >>= (int)symLen;
                left -= (int)symLen;

                if (inst <= 15)
                {
                    // "0 - 15: Represent code lengths of 0 - 15"
                    prev = (byte)inst;
                    codeLengths[symbolIndex++] = (byte)inst;
                }
                else if (inst == 16)
                {
                    // "16: Copy the previous code length 3 - 6 times. The next 2 bits indicate repeat length."
                    uint num = 3 + ((uint)bits & 0x3);
                    bits >>= 2;
                    left -= 2;
                    if (symbolIndex + num > numSymbols) return -18;
                    for (uint i = 0; i < num; i++) codeLengths[symbolIndex + i] = prev;
                    symbolIndex += num;
                }
                else if (inst == 17)
                {
                    // "17: Repeat a code length of 0 for 3 - 10 times. (3 bits of length)"
                    uint num = 3 + ((uint)bits & 0x7);
                    bits >>= 3;
                    left -= 3;
                    if (symbolIndex + num > numSymbols) return -19;
                    for (uint i = 0; i < num; i++) codeLengths[symbolIndex + i] = 0;
                    symbolIndex += num;
                    prev = 0;
                }
                else if (inst == 18)
                {
                    // "18: Repeat a code length of 0 for 11 - 138 times (7 bits of length)"
                    uint num = 11 + ((uint)bits & 0x7f);
                    bits >>= 7;
                    left -= 7;
                    if (symbolIndex + num > numSymbols) return -20;
                    for (uint i = 0; i < num; i++) codeLengths[symbolIndex + i] = 0;
                    symbolIndex += num;
                    prev = 0;
                }
                else
                {
                    return -6;
                }
            }

            s.Bits = bits;
            s.Left = left;
            s.ChunkPtr = data;

            return 0;
        }

        // C: ufbxi_init_dynamic_huff() (ufbx.c:2606-2664)
        public static int InitDynamicHuff(UfbxiDeflateContext dc, UfbxiTrees trees)
        {
            UfbxiBitStream s = dc.Stream;
            ulong bits = s.Bits;
            int left = s.Left;
            int data = s.ChunkPtr;
            s.BitRefill(ref bits, ref left, ref data);
            if (s.StopError != 0) return s.StopError;

            trees.FastBits = dc.FastBits;

            // The header contains the number of Huffman codes in each of the three trees.
            uint numLitLengths = 257 + (uint)(bits & 0x1f);
            uint numDists = 1 + (uint)(bits >> 5 & 0x1f);
            uint numCodeLengths = 4 + (uint)(bits >> 10 & 0xf);
            bits >>= 14;
            left -= 14;

            byte[] codeLengths = new byte[HuffMaxCombinedSyms];

            // Code lengths for the "code length" Huffman tree are represented literally
            // 3 bits in order of: 16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15 up to
            // `num_code_lengths`, rest of the code lengths are 0 (unused)
            for (int i = 0; i < HuffCodelenSyms; i++) codeLengths[i] = 0;

            for (uint lenI = 0; lenI < numCodeLengths; lenI++)
            {
                if (lenI == 14)
                {
                    s.BitRefill(ref bits, ref left, ref data);
                    if (s.StopError != 0) return s.StopError;
                }
                codeLengths[DeflateCodeLengthPermutation[lenI]] = (byte)((uint)bits & 0x7);
                bits >>= 3;
                left -= 3;
            }

            s.Bits = bits;
            s.Left = left;
            s.ChunkPtr = data;

            UfbxiHuffTree huffCodeLength = new UfbxiHuffTree();
            int err;

            // Build the temporary "code length" Huffman tree used to encode the actual
            // trees used to compress the data. Use that to build the literal/length and
            // distance trees.
            err = HuffBuild(huffCodeLength, codeLengths, 0, HuffCodelenSyms, null, int.MaxValue, HuffCodelenFastBits);
            if (err != 0) return -14 + 1 + err;

            err = DecodeDynamicHuffBits(dc, huffCodeLength, codeLengths, numLitLengths + numDists);
            if (err != 0) return err;

            err = HuffBuild(trees.LitLength, codeLengths, 0, numLitLengths, DeflateLengthLut, 256, dc.FastBits);
            if (err != 0) return err == -7 ? -28 : -16 + 1 + err;

            err = HuffBuild(trees.Dist, codeLengths, (int)numLitLengths, numDists, DeflateDistLut, 0, dc.FastBits);
            if (err != 0) return err == -7 ? -28 : -22 + 1 + err;

            return 0;
        }
    }
}
