using System;

namespace Ufbx.NET
{
    // Bit-level double/float access, mirroring the `ufbxm_bits` union and the
    // ufbxm_to_bits/ufbxm_from_bits/ufbxm_hi/ufbxm_zero_lo/ufbxm_set_hi helpers
    // in extra/ufbx_math.c:99-135 (little-endian layout: hi = high 32 bits).
    internal static class UfbxBitUtil
    {
        public static int Hi(double x)
        {
            return unchecked((int)(BitConverter.DoubleToInt64Bits(x) >> 32));
        }

        public static uint Lo(double x)
        {
            return unchecked((uint)BitConverter.DoubleToInt64Bits(x));
        }

        public static double FromBits(int hi, uint lo)
        {
            return BitConverter.Int64BitsToDouble(((long)(uint)hi << 32) | (long)lo);
        }

        public static double ZeroLo(double x)
        {
            return FromBits(Hi(x), 0u);
        }

        public static double SetHi(double x, int hi)
        {
            return FromBits(hi, Lo(x));
        }

        public static float BitsToSingle(uint bits)
        {
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        public static uint SingleToBits(float value)
        {
            return unchecked((uint)BitConverter.SingleToInt32Bits(value));
        }

        public static long BitsOf(double value)
        {
            return BitConverter.DoubleToInt64Bits(value);
        }

        public static double FromInt64(long bits)
        {
            return BitConverter.Int64BitsToDouble(bits);
        }

        // Set the quiet bit of a NaN, mirroring what the x86 FP instructions do.
        public static double QuietNaN(double value)
        {
            return FromInt64(BitsOf(value) | unchecked((long)0x0008000000000000ul));
        }

        // Port of ufbxi_lzcnt32/ufbxi_lzcnt64 (ufbx.c:940-967), the portable
        // de Bruijn variant. ufbx's MSVC path uses _BitScanReverse64, which is
        // identical for non-zero input; zero is undefined behaviour there, so this
        // mirrors the portable answer (32/64) rather than garbage.
        private static readonly byte[] Lzcnt32Table =
        {
            31, 22, 30, 21, 18, 10, 29, 2, 20, 17, 15, 13, 9, 6, 28, 1, 23, 19, 11, 3, 16, 14, 7, 24, 12, 4, 8, 25, 5, 26, 27, 0,
        };

        private static readonly byte[] Lzcnt64Table =
        {
            63, 16, 62, 7, 15, 36, 61, 3, 6, 14, 22, 26, 35, 47, 60, 2, 9, 5, 28, 11, 13, 21, 42,
            19, 25, 31, 34, 40, 46, 52, 59, 1, 17, 8, 37, 4, 23, 27, 48, 10, 29, 12, 43, 20, 32, 41,
            53, 18, 38, 24, 49, 30, 44, 33, 54, 39, 50, 45, 55, 51, 56, 57, 58, 0,
        };

        public static int LeadingZeroCount32(uint v)
        {
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return Lzcnt32Table[(v * 0x07c4acddu) >> 27];
        }

        public static int LeadingZeroCount64(ulong v)
        {
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v |= v >> 32;
            return Lzcnt64Table[(v * 0x03f79d71b4cb0a89ul) >> 58];
        }
    }
}
