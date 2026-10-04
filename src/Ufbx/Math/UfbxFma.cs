using System;

namespace Ufbx
{
    // Historical note: this file previously implemented a correctly-rounded
    // software FMA because the first golden build (zig cc -O2, native CPU)
    // contracted `x*y ± z` into hardware VFMADD, and the math kernels were
    // ported to match that single-rounding semantics.
    //
    // The golden reference has since been REGENERATED with explicit
    // no-contraction flags (zig cc -O2 -mcpu=x86_64 -ffp-contract=off), which:
    //   1. is reproducible on any machine (native-CPU contraction silently
    //      changes the golden depending on the host CPU's FMA support),
    //   2. matches upstream ufbx CI semantics (MSVC x64 and GCC/Clang x86-64
    //      baseline targets never contract),
    //   3. is empirically indistinguishable at the scene-hash level: plain vs
    //      fused builds produced identical hashes on the test corpus; only
    //      21/72000 synthetic extreme-bit vectors differed.
    //
    // Fma(a, b, c) is therefore kept as a named seam but reduces to the same
    // three IEEE operations the C source text describes. All 29 call sites in
    // UfbxMath.cs stay source-faithful to extra/ufbx_math.c.
    internal static partial class UfbxMath
    {
        public static double Fma(double a, double b, double c)
        {
            return a * b + c;
        }
    }
}
