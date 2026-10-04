// Scratch: characterize the atan()/atan2() divergence found by the S4b differential.
// Build: zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -o _s4b_atan.exe _s4b_atan.c -lm
// Run:   ./_s4b_atan.exe            -> fixed case + random sweep to stdout
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdint.h>

static void dbits(const char *tag, double v)
{
	uint64_t u; memcpy(&u, &v, sizeof(u));
	printf("%s %016llx\n", tag, (unsigned long long)u);
}

int main(void)
{
	// The failing case from ufbx_quat_to_euler() on maya_anim_layers_acc element 2.
	double y, x;
	{ uint64_t u = 0x3f3a819a7af95c38ULL; memcpy(&y, &u, 8); }
	{ uint64_t u = 0x3fefffffd416aeecULL; memcpy(&x, &u, 8); }
	double r = fabs(y / x);
	dbits("y", y);
	dbits("x", x);
	dbits("ratio", r);
	dbits("atan", atan(r));
	dbits("atan2", atan2(y, x));

	// Sweep: how often does musl's atan() disagree with the fdlibm table-driven form on
	// small arguments? Print the first few disagreements.
	uint64_t sb = 0x12345678ULL;
	int diff = 0;
	for (int i = 0; i < 200000; i++) {
		sb = sb * 6364136223846793005ULL + 1442695040888963407ULL;
		uint64_t m = (sb >> 11) & 0xfffffffffffffULL;
		double v;
		{ uint64_t u = 0x3e00000000000000ULL | m; memcpy(&v, &u, 8); } /* v in [2^-31, 2^-30) */
		double a = atan(v), b = atan(-v);
		(void)a; (void)b;
		double c = atan2(v, 1.0);
		if (a != c) { diff++; if (diff < 5) { printf("neq "); dbits("v", v); dbits("atan", a); dbits("atan2", c); } }
	}
	printf("sweep_neq %d\n", diff);
	return 0;
}
