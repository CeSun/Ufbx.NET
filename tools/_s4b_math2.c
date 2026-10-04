// Scratch: differential of the reference ufbx *software* math (extra/ufbx_math.c, which is what
// `#include "ufbx.c"` binds `ufbx_atan/ufbx_atan2/...` to unless UFBX_EXTERNAL_MATH) against the
// ported UfbxMath. The shared tools/mathvec_impl.c builds with UFBX_EXTERNAL_MATH (so it compares
// against the CRT) and packs two arguments with `xb | yb` (not decodable), which is why the
// atan/atan2 transcription gap survived until the S4b evaluation differential.
//
// Records (all bit patterns, lossless):
//   atan  <xhex> <reshex>
//   atan2 <yhex> <xhex> <reshex>
//   asin  <xhex> <reshex>
//   pow   <xhex> <yhex> <reshex>
//
// Build: zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
//        -o _s4b_math2.exe _s4b_math2.c
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#define UFBX_MATH_PREFIX ufbxi_
#include "ufbx.c"

static uint64_t bits_of(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static double from_bits(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }
static uint64_t st = 0x2545f4914f6cdd1dULL;
static uint64_t rnd(void) { st ^= st << 13; st ^= st >> 7; st ^= st << 17; return st; }
static double pow2_span(int e) { return from_bits(((uint64_t)(e + 1023) << 52) | (rnd() & 0xfffffffffffffULL)); }

int main(void)
{
	// Region A: what ufbx_quat_to_euler() feeds atan2 -- small |y|, |x| just under 2.
	for (int i = 0; i < 40000; i++) {
		double y = pow2_span(-20 + (int)(rnd() % 16));
		double x = pow2_span(-1 + (int)(rnd() % 2));
		if (rnd() & 1) y = -y;
		printf("atan2 %016llx %016llx %016llx\n", (unsigned long long)bits_of(y), (unsigned long long)bits_of(x), (unsigned long long)bits_of(ufbx_atan2(y, x)));
		double r = fabs(y / x);
		printf("atan %016llx %016llx\n", (unsigned long long)bits_of(r), (unsigned long long)bits_of(ufbx_atan(r)));
	}
	// Region B: all four quadrants and ratios near 1 (the atanhi/atanlo kernel switch points).
	for (int i = 0; i < 40000; i++) {
		double y = pow2_span(-3 + (int)(rnd() % 7));
		double x = pow2_span(-3 + (int)(rnd() % 7));
		uint64_t sb = rnd();
		y = sb & 1 ? -y : y;
		x = sb & 2 ? -x : x;
		printf("atan2 %016llx %016llx %016llx\n", (unsigned long long)bits_of(y), (unsigned long long)bits_of(x), (unsigned long long)bits_of(ufbx_atan2(y, x)));
	}
	for (int i = 0; i < 40000; i++) {
		double x = pow2_span((int)(rnd() % 5) - 2);
		double y = x * (1.0 + (double)(int64_t)(rnd() % 100000) / 500000.0);
		printf("atan2 %016llx %016llx %016llx\n", (unsigned long long)bits_of(y), (unsigned long long)bits_of(x), (unsigned long long)bits_of(ufbx_atan2(y, x)));
	}
	// Region C: |x| <= 1 for asin, and the exact failing case plus neighbours.
	for (int i = 0; i < 40000; i++) {
		double x = (double)(int64_t)rnd() / (double)INT64_MAX;
		printf("asin %016llx %016llx\n", (unsigned long long)bits_of(x), (unsigned long long)bits_of(ufbx_asin(x)));
	}
	{
		double y = from_bits(0x3f3a819a7af95c38ULL), x = from_bits(0x3fefffffd416aeecULL);
		for (int j = -16; j <= 16; j++) {
			for (int k = -16; k <= 16; k++) {
				double yy = from_bits(bits_of(y) + (uint64_t)j), xx = from_bits(bits_of(x) + (uint64_t)k);
				printf("atan2 %016llx %016llx %016llx\n", (unsigned long long)bits_of(yy), (unsigned long long)bits_of(xx), (unsigned long long)bits_of(ufbx_atan2(yy, xx)));
				double r = fabs(yy / xx);
				printf("atan %016llx %016llx\n", (unsigned long long)bits_of(r), (unsigned long long)bits_of(ufbx_atan(r)));
			}
		}
	}
	return 0;
}
