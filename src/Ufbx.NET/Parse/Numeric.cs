using System;

namespace Ufbx.NET
{
    // Ported from ufbx.c v0.23.1 "Float parsing" section (lines ~1351-1840).
    // Bit-exact port of the bigint-based double parser used for ASCII FBX.

    internal static class UfbxiNumeric
    {
        public const int BigintLimbBits = 32;
        public const int BigintAccumBits = BigintLimbBits * 2;
        public const uint BigintLimbMax = 0xffffffffu;

        public const uint ParseDoubleAllowFastPath = 0x1;
        public const uint ParseDoubleAsBinary32 = 0x2;

        private const int MaxLimbs = 14;
        private const int LimbArraySize = 42;

        // Limb arrays are fixed-size on the C side (stack arrays); use a
        // single reusable buffer per parse to avoid allocations.
        //
        // Visibility: `internal` rather than `private` so tools/NumericCheck can drive the
        // helpers one at a time against the C oracle (ufbxi_bigint_mad / _div / _shift_left
        // are `static` in a single-translation-unit ufbx.c, so tools/numeric_oracle.c reaches
        // them the same way). No behaviour depends on this.
        internal struct Bigint
        {
            internal uint[] Limbs;
            internal int Capacity;
            internal int Length;

            internal static Bigint Make(uint[] limbs)
            {
                Bigint bi;
                bi.Limbs = limbs;
                bi.Capacity = limbs.Length;
                bi.Length = 0;
                return bi;
            }
        }

        internal static readonly ulong[] Pow5Tab =
        {
            0x1ul, 0x5ul, 0x19ul, 0x7dul, 0x271ul, 0xc35ul, 0x3d09ul, 0x1312dul, 0x5f5e1ul,
            0x1dcd65ul, 0x9502f9ul, 0x2e90eddul, 0xe8d4a51ul, 0x48c27395ul, 0x16bcc41e9ul, 0x71afd498dul,
            0x2386f26fc1ul, 0xb1a2bc2ec5ul, 0x3782dace9d9ul, 0x1158e460913dul, 0x56bc75e2d631ul, 0x1b1ae4d6e2ef5ul,
            0x878678326eac9ul, 0x2a5a058fc295edul, 0xd3c21bcecceda1ul, 0x422ca8b0a00a425ul, 0x14adf4b7320334b9ul, 0x6765c793fa10079dul,
        };

        private static readonly double[] Pow10TabF64 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
        };

        // C declares three independent 42-limb stack arrays (ufbx.c:1605). They are
        // never re-zeroed, so limbs beyond `length` keep whatever a previous parse
        // left behind, and `ufbxi_bigint_shift_left` reads those stale limbs. One
        // persistent buffer per thread reproduces that residue and avoids the
        // per-call allocations a fresh array would cause.
        [ThreadStatic] private static uint[] _mantissaLimbs;
        [ThreadStatic] private static uint[] _divisorLimbs;
        [ThreadStatic] private static uint[] _quotientLimbs;

        private static uint[] GetLimbArray(ref uint[] field)
        {
            if (field == null) field = new uint[LimbArraySize];
            return field;
        }

        internal static void BigintMad(ref Bigint bigint, ulong multiplicand, ulong addend)
        {
            // assert((multiplicand | addend) >> (AccumBits - 1) == 0)
            uint mLo = (uint)multiplicand;
            uint mHi = (uint)(multiplicand >> BigintLimbBits);
            ulong carry = addend;
            int length = bigint.Length;
            uint[] limbs = bigint.Limbs;
            for (int i = 0; i < length; i++)
            {
                ulong limb = limbs[i];
                ulong lo = limb * mLo + (carry & BigintLimbMax);
                ulong hi = limb * mHi;
                limbs[i] = (uint)lo;
                carry = (carry >> 32) + (lo >> 32) + hi;
            }
            while (carry != 0)
            {
                limbs[length++] = (uint)carry;
                // assert(length < capacity)
                carry >>= 32;
            }
            bigint.Length = length;
        }

        internal static bool BigintDiv(ref Bigint q, ref Bigint u, ref Bigint v)
        {
            int n = v.Length;
            int m = u.Length - n;
            uint vHi = v.Limbs[v.Length - 1];
            uint[] un = u.Limbs;
            uint[] vn = v.Limbs;
            // assert(n >= 2 && m >= 1 && v_hi >> (LimbBits - 1) != 0 && un[n+m - 1] >> (LimbBits - 1) == 0)
            un[n + m] = 0;
            q.Length = 0;
            for (int j = m - 1; j >= 0; j--)
            {
                ulong uHi = ((ulong)un[n + j] << BigintLimbBits) | un[n + j - 1];
                ulong t;
                ulong qhat = uHi / vHi, rhat = uHi % vHi;
                while ((qhat >> BigintLimbBits) != 0 || qhat * vn[n - 2] > ((rhat << BigintLimbBits) | un[j + n - 2]))
                {
                    qhat -= 1;
                    rhat += vHi;
                    if ((rhat >> BigintLimbBits) != 0) break;
                }
                uint carry = 0;
                for (int i = 0; i < n; i++)
                {
                    ulong p = qhat * vn[i];
                    t = (ulong)un[i + j] - carry - (uint)p;
                    un[i + j] = (uint)t;
                    carry = (uint)((p >> BigintLimbBits) - (t >> BigintLimbBits));
                }
                t = (ulong)un[j + n] - carry;
                un[j + n] = (uint)t;
                if ((t >> BigintLimbBits) != 0)
                {
                    qhat -= 1;
                    carry = 0;
                    for (int i = 0; i < n; i++)
                    {
                        t = (ulong)un[i + j] + vn[i] + carry;
                        un[i + j] = (uint)t;
                        carry = (uint)(t >> BigintLimbBits);
                    }
                    un[j + n] += carry;
                }
                q.Limbs[j] = (uint)qhat;
                if (qhat != 0 && q.Length == 0)
                {
                    // assert(j + 1 < q.Capacity)
                    q.Length = j + 1;
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (un[i] != 0) return true;
            }
            return false;
        }

        internal static void BigintMulPow5(ref Bigint b, uint power)
        {
            for (; power > 27; power -= 27)
            {
                BigintMad(ref b, Pow5Tab[27], 0);
            }
            BigintMad(ref b, Pow5Tab[power], 0);
        }

        internal static void BigintShiftLeft(ref Bigint bigint, uint amount)
        {
            uint words = amount / BigintLimbBits, bits = amount % BigintLimbBits;
            // Copy mirrors `ufbxi_bigint b = *bigint` (ufbx.c:1463): b.length keeps
            // the OLD length while bigint.Length is updated in place (ufbx.c:1466),
            // and every later index below is computed from that old value.
            Bigint b = bigint;
            // assert(b.length + words + 1 < b.capacity && b.capacity >= 4)
            int bitsDown = BigintLimbBits - (int)bits - 1;
            bigint.Length += (int)words + (b.Limbs[b.Length - 1] >> 1 >> bitsDown != 0 ? 1 : 0);
            b.Limbs[b.Length] = 0;
            if (b.Length <= 3 && words <= 3)
            {
                // ufbxi_maybe_uninit always yields `value` in release builds
                // (ufbx.c:588-590), so limbs[1]/limbs[2] are read unconditionally:
                // whatever the persistent buffer holds, including stale limbs.
                uint l0 = b.Limbs[0];
                uint l1 = b.Limbs[1];
                uint l2 = b.Limbs[2];
                b.Limbs[0] = 0;
                b.Limbs[1] = 0;
                b.Limbs[2] = 0;
                b.Limbs[(int)words + 0] = l0 << (int)bits;
                b.Limbs[(int)words + 1] = (l1 << (int)bits) | (l0 >> 1 >> bitsDown);
                b.Limbs[(int)words + 2] = (l2 << (int)bits) | (l1 >> 1 >> bitsDown);
                b.Limbs[(int)words + 3] = (l2 >> 1 >> bitsDown);
            }
            else
            {
                for (int i = b.Length + 1; i-- > 1; )
                {
                    b.Limbs[i + (int)words] = (b.Limbs[i] << (int)bits) | (b.Limbs[i - 1] >> 1 >> bitsDown);
                }
                b.Limbs[(int)words] = b.Limbs[0] << (int)bits;
                for (int i = 0; i < (int)words; i++)
                {
                    b.Limbs[i] = 0;
                }
            }
        }

        internal static uint BigintTopLimb(ref Bigint b, int index)
        {
            return index < b.Length ? b.Limbs[b.Length - 1 - index] : 0;
        }

        internal static ulong BigintExtractHigh(ref Bigint b, ref int exponent, ref bool tail)
        {
            // assert(b.length != 0)
            ulong result = 0;
            const int limbCount = 64 / BigintLimbBits;
            for (int i = 0; i < limbCount; i++)
            {
                result = (result << BigintLimbBits) | BigintTopLimb(ref b, i);
            }
            int shift = UfbxBitUtil.LeadingZeroCount64(result);
            result <<= shift;
            uint lo = BigintTopLimb(ref b, limbCount);
            if (shift > 0)
            {
                result |= lo >> (BigintLimbBits - shift);
            }
            tail |= (uint)(lo << shift) != 0;
            for (int i = limbCount + 1; i < b.Length; i++)
            {
                tail |= BigintTopLimb(ref b, i) != 0;
            }
            exponent += b.Length * BigintLimbBits - shift - 1;
            return result;
        }

        internal static ulong ShiftRightRound(ulong value, uint shift, bool tail)
        {
            if (shift == 0) return value;
            if (shift > 64) return 0;
            ulong result = value >> ((int)shift - 1);
            ulong tailMask = (1ul << ((int)shift - 1)) - 1;

            bool rOdd = (result & 0x2) != 0;
            bool rRound = (result & 0x1) != 0;
            bool rTail = tail || (value & tailMask) != 0;
            ulong roundBit = (rRound && (rOdd || rTail)) ? 1ul : 0ul;

            return (result >> 1) + roundBit;
        }

        // Port of `ufbxi_scan_ignorecase` (ufbx.c:1536-1543); `fmt` must be lowercase
        // and `p` is the remaining slice up to the end of input.
        internal static bool ScanIgnorecase(ReadOnlySpan<byte> p, string fmt)
        {
            for (int i = 0; i < fmt.Length; i++)
            {
                if (i >= p.Length) return false;
                if ((p[i] | 0x20) != (byte)fmt[i]) return false;
            }
            return true;
        }

        // Port of `ufbxi_parse_inf_nan` (ufbx.c:1545-1599). Returns false when the
        // C function returns false. C leaves *p_result and *p_end UNWRITTEN in that case
        // (ufbx.c:1564, 1579, 1583); the port assigns 0/0.0 up front instead, which is
        // unobservable because ufbxi_parse_double only reads them on success
        // (ufbx.c:1667-1670) and overwrites p_end at ufbx.c:1673 otherwise.
        internal static bool ParseInfNan(ReadOnlySpan<byte> str, out double pResult, out int pEndOffset)
        {
            pResult = 0.0;
            pEndOffset = 0;
            bool negative = false;
            int p = 0, end = str.Length;
            if (p != end && (str[p] == (byte)'+' || str[p] == (byte)'-'))
            {
                negative = str[p++] == (byte)'-';
            }

            uint topBits = 0;
            if (end - p >= 3 && str[p] >= (byte)'0' && str[p] <= (byte)'9' && str[p + 1] == (byte)'.' && str[p + 2] == (byte)'#')
            {
                // Legacy MSVC 1.#NAN
                p += 3;
                if (ScanIgnorecase(str.Slice(p), "inf"))
                {
                    p += 3;
                    topBits = 0x7ff0;
                }
                else if (ScanIgnorecase(str.Slice(p), "nan") || ScanIgnorecase(str.Slice(p), "ind"))
                {
                    p += 3;
                    topBits = 0x7ff8;
                }
                else
                {
                    return false;
                }
                while (p != end && str[p] >= (byte)'0' && str[p] <= (byte)'9')
                {
                    p++;
                }
            }
            else
            {
                // Standard
                if (ScanIgnorecase(str.Slice(p), "nan"))
                {
                    p += 3;
                    topBits = 0x7ff8;
                    if (p != end && str[p] == (byte)'(')
                    {
                        p++;
                        while (p != end && str[p] != (byte)')')
                        {
                            byte c = str[p];
                            if (!((c >= (byte)'0' && c <= (byte)'9') || (c >= (byte)'a' && c <= (byte)'z') || (c >= (byte)'A' && c <= (byte)'Z')))
                            {
                                return false;
                            }
                            p++;
                        }
                        if (p == end) return false;
                        p++;
                    }
                }
                else if (ScanIgnorecase(str.Slice(p), "inf"))
                {
                    p += ScanIgnorecase(str.Slice(p + 3), "inity") ? 8 : 3;
                    topBits = 0x7ff0;
                }
                // Note: C falls through here without matching, yielding top_bits 0
                // (i.e. +-0.0) and p_end = p; it does not return false.
            }

            pEndOffset = p;
            topBits |= negative ? 0x8000u : 0u;
            ulong bits = (ulong)topBits << 48;
            pResult = BitConverter.Int64BitsToDouble(unchecked((long)bits));
            return true;
        }

        // Port of `ufbxi_parse_double`. `pEnd` is the offset in bytes where
        // parsing stopped; if the function returns false an unrecoverable
        // internal state occurred (should not happen).
        public static double ParseDouble(ReadOnlySpan<byte> str, out int pEnd, uint flags)
        {
            uint[] mantissaLimbs = GetLimbArray(ref _mantissaLimbs);
            uint[] divisorLimbs = GetLimbArray(ref _divisorLimbs);
            uint[] quotientLimbs = GetLimbArray(ref _quotientLimbs);

            Bigint bigMantissa = Bigint.Make(mantissaLimbs);
            Bigint bigQuotient = Bigint.Make(quotientLimbs);
            int decExponent = 0, hasDot = 0;
            bool negative = false, tail = false, digitsValid = true;
            ulong digits = 0;
            uint numDigits = 0;

            int p = 0, end = str.Length;
            if (p != end && (str[p] == (byte)'+' || str[p] == (byte)'-'))
            {
                negative = str[p++] == (byte)'-';
            }
            while (p != end)
            {
                byte c = str[p];
                if (c >= (byte)'0' && c <= (byte)'9')
                {
                    if (bigMantissa.Length < MaxLimbs)
                    {
                        digits = digits * 10 + (ulong)(c - (byte)'0');
                        numDigits++;
                        if (numDigits >= 18)
                        {
                            // assert(num_digits < Pow5Tab.Length)
                            BigintMad(ref bigMantissa, Pow5Tab[numDigits] << (int)numDigits, digits);
                            digits = 0;
                            numDigits = 0;
                            digitsValid = false;
                        }
                        decExponent -= hasDot;
                    }
                    else
                    {
                        decExponent += 1 - hasDot;
                    }
                    p++;
                }
                else if (c == (byte)'.' && hasDot == 0)
                {
                    hasDot = 1;
                    p++;
                }
                else
                {
                    break;
                }
            }
            if (p != end && (str[p] == (byte)'e' || str[p] == (byte)'E'))
            {
                p++;
                bool expNegative = false;
                if (p != end && (str[p] == (byte)'+' || str[p] == (byte)'-'))
                {
                    expNegative = str[p] == (byte)'-';
                    p++;
                }
                int exp = 0;
                while (p != end)
                {
                    byte c = str[p];
                    if (c >= (byte)'0' && c <= (byte)'9')
                    {
                        p++;
                        exp = exp * 10 + (c - (byte)'0');
                        if (exp >= 10000) break;
                    }
                    else
                    {
                        break;
                    }
                }
                decExponent += expNegative ? -exp : exp;
            }

            if (p != end)
            {
                byte c = str[p];
                if (c == (byte)'#' || c == (byte)'i' || c == (byte)'I' || c == (byte)'n' || c == (byte)'N')
                {
                    if (ParseInfNan(str, out double infNanResult, out int infNanEnd))
                    {
                        pEnd = infNanEnd;
                        return infNanResult;
                    }
                }
            }

            pEnd = p;

            // Both power of 10 and integer are exactly representable as doubles.
            // Powers of 10 are factored as 2*5, and 2^N can be always exactly represented.
            if ((flags & ParseDoubleAllowFastPath) != 0 && bigMantissa.Length == 0 && decExponent >= -22 && decExponent <= 22 && (digits >> 53) == 0)
            {
                double value;
                if (decExponent < 0)
                {
                    value = (double)digits / Pow10TabF64[-decExponent];
                }
                else
                {
                    value = (double)digits * Pow10TabF64[decExponent];
                }
                return negative ? -value : value;
            }

            if (bigMantissa.Length == 0)
            {
                bigMantissa.Limbs[0] = (uint)digits;
                bigMantissa.Limbs[1] = (uint)(digits >> 32);
                bigMantissa.Length = (digits >> 32) != 0 ? 2 : digits != 0 ? 1 : 0;
                if (bigMantissa.Length == 0) return negative ? -0.0 : 0.0;
            }
            else
            {
                // assert(num_digits < Pow5Tab.Length)
                BigintMad(ref bigMantissa, Pow5Tab[numDigits] << (int)numDigits, digits);
            }

            int encSignShift = 63;
            int encMantissaBits = 53;
            int encMaxExponent = 1023;
            if ((flags & ParseDoubleAsBinary32) != 0)
            {
                encSignShift = 31;
                encMantissaBits = 24;
                encMaxExponent = 127;
            }

            int exponent = 0;
            if (decExponent < 0)
            {
                if (decExponent + bigMantissa.Length * 10 <= -325) return negative ? -0.0 : 0.0;

                Bigint bigDivisor = Bigint.Make(divisorLimbs);
                uint pow5 = (uint)-decExponent;
                uint initialPow5 = pow5 <= 27 ? pow5 : 27;
                ulong pow5Value = Pow5Tab[initialPow5];
                pow5 -= initialPow5;
                exponent += decExponent;

                if (pow5 == 0 && digitsValid && (digits >> 63) == 0)
                {
                    int divisorZeros = UfbxBitUtil.LeadingZeroCount64(pow5Value);
                    ulong mantissaZeros = (ulong)(UfbxBitUtil.LeadingZeroCount64(digits) - 1);
                    ulong divisorBits = pow5Value << divisorZeros;
                    ulong mantissaBits = digits << (int)mantissaZeros;
                    bigDivisor.Limbs[0] = (uint)divisorBits;
                    bigDivisor.Limbs[1] = (uint)(divisorBits >> 32);
                    bigDivisor.Length = 2;
                    bigMantissa.Limbs[0] = 0;
                    bigMantissa.Limbs[1] = 0;
                    bigMantissa.Limbs[2] = (uint)mantissaBits;
                    bigMantissa.Limbs[3] = (uint)(mantissaBits >> 32);
                    bigMantissa.Length = 4;
                    exponent += divisorZeros - (int)mantissaZeros - 64;
                }
                else
                {
                    bigDivisor.Limbs[0] = (uint)pow5Value;
                    bigDivisor.Limbs[1] = (uint)(pow5Value >> 32);
                    bigDivisor.Length = (pow5Value >> 32) != 0 ? 2 : 1;
                    if (pow5 > 0)
                    {
                        BigintMulPow5(ref bigDivisor, pow5);
                    }

                    int divisorZeros = UfbxBitUtil.LeadingZeroCount32(bigDivisor.Limbs[bigDivisor.Length - 1]);
                    if (bigDivisor.Length == 1) divisorZeros += BigintLimbBits;
                    BigintShiftLeft(ref bigDivisor, (uint)divisorZeros);
                    int divisorBits = bigDivisor.Length * BigintLimbBits;

                    int mantissaZeros = UfbxBitUtil.LeadingZeroCount32(bigMantissa.Limbs[bigMantissa.Length - 1]);
                    int mantissaBits = bigMantissa.Length * BigintLimbBits - mantissaZeros;
                    int mantissaMinBits = divisorBits + encMantissaBits + 2;
                    int mantissaShift = mantissaBits < mantissaMinBits ? mantissaMinBits - mantissaBits : 0;
                    // Align mantissa to never have a high bit, this means we can skip the first digit during division.
                    mantissaShift += ((mantissaShift - mantissaZeros) & (BigintLimbBits - 1)) == 0 ? 1 : 0;
                    if (mantissaShift > 0)
                    {
                        BigintShiftLeft(ref bigMantissa, (uint)mantissaShift);
                    }
                    exponent += divisorZeros - mantissaShift;
                }

                tail = BigintDiv(ref bigQuotient, ref bigMantissa, ref bigDivisor);
                bigMantissa = bigQuotient;
            }
            else if (decExponent > 0)
            {
                if (decExponent + (bigMantissa.Length - 1) * 9 >= 310) return negative ? double.NegativeInfinity : double.PositiveInfinity;

                exponent += decExponent;
                BigintMulPow5(ref bigMantissa, (uint)decExponent);
            }

            ulong mantissa = BigintExtractHigh(ref bigMantissa, ref exponent, ref tail);
            ulong signBit = (ulong)(negative ? 1ul : 0ul) << encSignShift;

            int mantissaShiftBits = 64 - encMantissaBits;
            if (exponent > encMaxExponent)
            {
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
            }
            else if (exponent <= -encMaxExponent)
            {
                mantissaShiftBits += -encMaxExponent + 1 - exponent;
                exponent = -encMaxExponent + 1;
            }

            mantissa = ShiftRightRound(mantissa, (uint)mantissaShiftBits, tail);
            if (mantissa == 0) return negative ? -0.0 : 0.0;

            ulong bits = mantissa;
            bits += (ulong)(exponent + encMaxExponent - 1) << (encMantissaBits - 1);
            bits |= signBit;

            if ((flags & ParseDoubleAsBinary32) != 0)
            {
                uint bitsLo = (uint)bits;
                return UfbxBitUtil.BitsToSingle(bitsLo);
            }
            else
            {
                return BitConverter.Int64BitsToDouble(unchecked((long)bits));
            }
        }
    }
}
