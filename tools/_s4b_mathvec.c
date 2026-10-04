// Scratch: lossless two-argument atan/atan2 differential for the S4b wave.
// The shared tools/mathvec_impl.c packs both arguments with `xb | yb`, which is not decodable,
// so the ported Atan2 was never really swept against the reference libm. This emits
//   atan2 <yhex> <xhex> <reshex>
//   atan  <xhex> <reshex>
// over the regions ufbx_quat_to_euler() actually produces, and additionally reports how often
// the *reference* results are correctly rounded (long double as the higher-precision oracle).
// Build: zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off \
//        -o _s4b_mathvec.exe _s4b_mathvec.c
// Run:   ./_s4b_mathvec.exe > _s4b_atan_vectors.txt
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <math.h>

static uint64_t bits_of(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static double from_bits(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }

static uint64_t st = 0x2545f4914f6cdd1dULL;
static uint64_t rnd(void) { st ^= st << 13; st ^= st >> 7; st ^= st << 17; return st; }

// Uniform in [2^emin, 2^(emin+1)) with a random mantissa.
static double pow2_span(int e)
{
	uint64_t m = rnd() & 0xfffffffffffffULL;
	return from_bits(((uint64_t)(e + 1023) << 52) | m);
}

static long acc_ok, acc_bad, acc_unc;
static void check_acc(long double ref_true, double got)
{
	double a = fabs(got);
	if (!(a > 0.0)) { acc_ok++; return; }
	double ulp = from_bits(bits_of(a) + 1) - a;
	long double d = fabsl(ref_true - (long double)a);
	if (d < ulp * 0.4) acc_ok++;
	else if (d > ulp * 0.6) acc_bad++;
	else acc_unc++;
}

static void emit(double y, double x)
{
	double a2 = atan2(y, x);
	check_acc(atan2l((long double)y, (long double)x), a2);
	printf("atan2 %016llx %016llx %016llx\n",
		(unsigned long long)bits_of(y), (unsigned long long)bits_of(x), (unsigned long long)bits_of(a2));

	double r = fabs(y / x);
	double a1 = atan(r);
	check_acc(atanl((long double)r), a1);
	printf("atan %016llx %016llx\n", (unsigned long long)bits_of(r), (unsigned long long)bits_of(a1));
}

int main(void)
{
	// Region A: the quat_to_euler shape -- small |y|, |x| ~ 0.5..2.
	for (int i = 0; i < 60000; i++) {
		double y = pow2_span(-14 + (int)(rnd() % 10));
		double x = pow2_span(-1 + (int)(rnd() % 2));
		emit(rnd() & 1 ? -y : y, x);
	}
	// Region B: all four quadrants, moderate magnitudes (x<0 -> the pi - (z - pi_lo) path).
	for (int i = 0; i < 60000; i++) {
		double y = pow2_span(-3 + (int)(rnd() % 7));
		double x = pow2_span(-3 + (int)(rnd() % 7));
		uint64_t sb = rnd();
		emit(sb & 1 ? -y : y, sb & 2 ? -x : x);
	}
	// Region C: |y/x| near 1.
	for (int i = 0; i < 60000; i++) {
		double x = pow2_span((int)(rnd() % 5) - 2);
		double y = x * (1.0 + (double)(int64_t)(rnd() % 100000) / 500000.0);
		emit(y, x);
	}
	// Region D: exactly the failing case, plus neighbours.
	{
		double y = from_bits(0x3f3a819a7af95c38ULL), x = from_bits(0x3fefffffd416aeecULL);
		for (int j = -8; j <= 8; j++)
			for (int k = -8; k <= 8; k++)
				emit(from_bits(bits_of(y) + (uint64_t)j), from_bits(bits_of(x) + (uint64_t)k));
	}
	fprintf(stderr, "reference correctly rounded: ok=%ld bad=%ld uncertain=%ld\n", acc_ok, acc_bad, acc_unc);
	return 0;
}
