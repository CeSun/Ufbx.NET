// Scratch: is the reference (zig cc / windows-gnu) atan2 more or less accurate than the
// fdlibm `atan(y/x)` composition the port implements? Uses long double as an independent
// higher-precision reference for the true value.
// Build: zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -o _s4b_atan2.exe _s4b_atan2.c
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <stdint.h>

static uint64_t b64(double v) { uint64_t u; memcpy(&u, &v, 8); return u; }
static double f64(uint64_t u) { double v; memcpy(&v, &u, 8); return v; }

int main(void)
{
	printf("case1: y=3f3a819a7af95c38 x=3fefffffd416aeec\n");
	{
		double y = f64(0x3f3a819a7af95c38ULL), x = f64(0x3fefffffd416aeecULL);
		long double ref = atan2l((long double)y, (long double)x);
		double a = atan2(y, x);
		double b = atan(fabs(y / x));
		printf("  ref_ld %Lf\n  atan2  %016llx err=%.3e\n  atan(y/x) %016llx err=%.3e\n",
			ref, (unsigned long long)b64(a), (double)(fabsl((long double)a - ref)),
			(unsigned long long)b64(b), (double)(fabsl((long double)b - ref)));
	}

	// Region probe: |y|/|x| small (what quat_to_euler produces), |x| near 2.
	uint64_t sb = 0x243f6a8885a308d3ULL;
	int neq = 0, ref_a = 0, ref_b = 0, both = 0, n = 0;
	for (int i = 0; i < 20000; i++) {
		sb = sb * 6364136223846793005ULL + 1442695040888963407ULL;
		uint64_t uy = 0x3e00000000000000ULL | ((sb >> 11) & 0xfffffffffffffULL);
		uint64_t ux = 0x3fffffffffffffffULL & (0x3fe0000000000000ULL | ((sb >> 37) & 0x1fffffffffffffULL));
		double y = f64(uy), x = f64(ux);
		double a = atan2(y, x), b = atan(fabs(y / x));
		long double ref = atan2l((long double)y, (long double)x);
		n++;
		if (a != b) neq++;
		if (fabsl((long double)a - ref) < fabsl((long double)b - ref) - 1e-25L) ref_a++;
		else if (fabsl((long double)b - ref) < fabsl((long double)a - ref) - 1e-25L) ref_b++;
		else both++;
	}
	printf("sweep n=%d neq=%d atan2_closer=%d atan_composition_closer=%d equal=%d\n",
		n, neq, ref_a, ref_b, both);
	return 0;
}
