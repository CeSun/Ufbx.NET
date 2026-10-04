// Decisive probe: which atan2 does the C *reference* actually use?
//
// ufbx.c:238-277 -- with UFBX_EXTERNAL_MATH *not* defined (the config of s4b_oracle.c and
// test/hash_scene.c, hence of tools/golden_hashes.txt), UFBX_MATH_PREFIX is defined as an EMPTY
// macro, so `ufbx_atan2` token-pastes to plain `atan2`, i.e. the CRT. zig cc defaults to
// x86_64-windows-gnu and links api-ms-win-crt-math (UCRT = Intel IML kernels), not fdlibm.
// extra/ufbx_math.c is the *other* candidate (LIBM/fdlibm-derived, and the source the ported
// UfbxMath is transcribed from) and is only reachable with -DUFBX_EXTERNAL_MATH + this TU linked.
//
// Prints, for the exact failing case plus a small-ratio sweep, both implementations side by side
// so the port can be aligned against the one the goldens actually come from.
//
// build: zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off \
//          -I C:/Workspace/_analyze_ufbx -I C:/Workspace/_analyze_ufbx/extra \
//          C:/Workspace/ufbx-cs/tools/_s4b_mathref.c \
//          -o C:/Workspace/ufbx-cs/tools/_s4b_mathref.exe
// run:   C:/Workspace/ufbx-cs/tools/_s4b_mathref.exe > C:/Workspace/ufbx-cs/tools/_s4b_mathref.txt

#define _USE_MATH_DEFINES
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <math.h>

#define UFBX_STATIC
#include "ufbx_math.c"

static uint64_t bits_of(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static double from_bits(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }

static uint64_t st = 0x9e3779b97f4a7c15ull;
static uint64_t nxt(void) { st ^= st << 13; st ^= st >> 7; st ^= st << 17; return st; }

static void report(const char *tag, double y, double x) {
	uint64_t yb = bits_of(y), xb = bits_of(x);
	double crtm = atan2(y, x);
	double sw = ufbx_atan2(y, x);
	double crta = atan(y / x);
	double swa = ufbx_atan(y / x);
	long double ld = atan2l(y, x);
	printf("%s y=%016llx x=%016llx crt=%016llx sw=%016llx %s | atan(y/x) crt=%016llx sw=%016llx ld=%016llx %s\n",
		tag,
		(unsigned long long)yb, (unsigned long long)xb,
		(unsigned long long)bits_of(crtm), (unsigned long long)bits_of(sw),
		bits_of(crtm) == bits_of(sw) ? "EQ" : "NEQ",
		(unsigned long long)bits_of(crta), (unsigned long long)bits_of(swa),
		(unsigned long long)bits_of((double)ld),
		bits_of(sw) == bits_of((double)ld) ? "sw=correctly-rounded" : "sw!=cr");
}

int main(void)
{
	// The exact case localised by tools/_s4b_atan2.c / _s4b_trace.c:
	// element 2 "Lcl Rotation" z, layer-combine atan2(az, ax); oracle afb, port afc.
	report("CASE", from_bits(0x3f3a819a7af95c38ull), from_bits(0x3fefffffd416aeecull));

	printf("--- survey: CRT vs ufbx_math over the mathvec distributions (1-arg) ---\n");
	{
		static const char *f1[] = { "sqrt", "fabs", "rint", "floor", "ceil", "sin", "cos", "tan",
			"asin", "acos", "atan" };
		for (int fi = 0; fi < 11; fi++) {
			int neq = 0;
			uint64_t first_c = 0, first_s = 0, first_arg = 0;
			for (int i = 0; i < 40000; i++) {
				// Same five bit-pattern regions tools/mathvec_impl.c generates vectors from.
				uint64_t u = nxt();
				uint64_t b;
				switch (i % 5) {
				case 0: b = (u & 0x7ff0000000000000ull) | (nxt() & 1); break;
				case 1: b = u & 0x4330000000000000ull; break;
				case 2: b = (u & 0x4030000000000000ull) | (nxt() & 0x000fffffffffffffull); break;
				case 3: b = (u & 0x7ff0000000000000ull) | (nxt() & 0x000fffffffffffffull);
					if ((b >> 52) == 0x7ff) b &= ~0x000fffffffffffffull; break;
				default: b = (u & 0x3ff0000000000000ull) | (nxt() & 0x000fffffffffffffull); break;
				}
				double x = from_bits(b);
				double c, s;
				if (!strcmp(f1[fi], "sqrt")) { c = sqrt(x); s = ufbx_sqrt(x); }
				else if (!strcmp(f1[fi], "fabs")) { c = fabs(x); s = ufbx_fabs(x); }
				else if (!strcmp(f1[fi], "rint")) { c = rint(x); s = ufbx_rint(x); }
				else if (!strcmp(f1[fi], "floor")) { c = floor(x); s = ufbx_floor(x); }
				else if (!strcmp(f1[fi], "ceil")) { c = ceil(x); s = ufbx_ceil(x); }
				else if (!strcmp(f1[fi], "sin")) { c = sin(x); s = ufbx_sin(x); }
				else if (!strcmp(f1[fi], "cos")) { c = cos(x); s = ufbx_cos(x); }
				else if (!strcmp(f1[fi], "tan")) { c = tan(x); s = ufbx_tan(x); }
				else if (!strcmp(f1[fi], "asin")) { c = asin(x); s = ufbx_asin(x); }
				else if (!strcmp(f1[fi], "acos")) { c = acos(x); s = ufbx_acos(x); }
				else { c = atan(x); s = ufbx_atan(x); }
				if (bits_of(c) != bits_of(s)) {
					neq++;
					if (neq == 1) { first_c = bits_of(c); first_s = bits_of(s); first_arg = b; }
				}
			}
			printf("1arg %-6s total=40000 neq=%d%s", f1[fi], neq, neq ? "" : "\n");
			if (neq) printf(" first x=%016llx crt=%016llx sw=%016llx\n",
				(unsigned long long)first_arg, (unsigned long long)first_c, (unsigned long long)first_s);
		}
	}

	printf("--- survey: 2-arg ---\n");
	{
		static const char *f2[] = { "atan2", "pow", "copysign", "fmin", "fmax", "nextafter" };
		for (int fi = 0; fi < 6; fi++) {
			int neq = 0;
			uint64_t fa = 0, fb = 0, first_c = 0, first_s = 0;
			for (int i = 0; i < 40000; i++) {
				uint64_t yb, xb;
				{ uint64_t u = nxt(); xb = (u & 0x400fffffffffffffull) | (nxt() & 0x000fffffffffffffull); }
				{ uint64_t u = nxt(); yb = (u & 0x400fffffffffffffull) | (nxt() & 0x000fffffffffffffull); }
				if (i % 3 == 1) { xb = (xb & 0x800fffffffffffffull) | 0x3ff0000000000000ull; }
				if (i % 3 == 2) { yb = (yb & 0x800fffffffffffffull) | 0x3ff0000000000000ull; }
				double x = from_bits(xb), y = from_bits(yb);
				double c, s;
				if (!strcmp(f2[fi], "atan2")) { c = atan2(y, x); s = ufbx_atan2(y, x); }
				else if (!strcmp(f2[fi], "pow")) { c = pow(x, y); s = ufbx_pow(x, y); }
				else if (!strcmp(f2[fi], "copysign")) { c = copysign(x, y); s = ufbx_copysign(x, y); }
				else if (!strcmp(f2[fi], "fmin")) { c = fmin(x, y); s = ufbx_fmin(x, y); }
				else if (!strcmp(f2[fi], "fmax")) { c = fmax(x, y); s = ufbx_fmax(x, y); }
				else { c = nextafter(x, y); s = ufbx_nextafter(x, y); }
				if (bits_of(c) != bits_of(s)) {
					neq++;
					if (neq == 1) { fa = xb; fb = yb; first_c = bits_of(c); first_s = bits_of(s); }
				}
			}
			printf("2arg %-10s total=40000 neq=%d%s", f2[fi], neq, neq ? "" : "\n");
			if (neq) printf(" first x=%016llx y=%016llx crt=%016llx sw=%016llx\n",
				(unsigned long long)fa, (unsigned long long)fb,
				(unsigned long long)first_c, (unsigned long long)first_s);
		}
	}

	printf("--- sweep: |y|<<|x| (the regime quat_to_euler reaches) ---\n");
	int neq = 0, total = 0;
	for (int i = 0; i < 20000; i++) {
		uint64_t yb = (nxt() & 0x000fffffffffffffull) | (0x3c0ull + ((nxt() & 0x3f) << 40));
		uint64_t xb = (nxt() & 0x000fffffffffffffull) | (0x3fe0000000000000ull + ((nxt() % 3) << 48));
		double y = from_bits(yb), x = from_bits(xb);
		if (bits_of(atan2(y, x)) != bits_of(ufbx_atan2(y, x))) neq++;
		total++;
		if (i < 12) report("SW", y, x);
	}
	printf("sweep total=%d crt_vs_ufbx_math_neq=%d\n", total, neq);

	printf("--- asin / atan / pow spot check ---\n");
	for (int i = 0; i < 6; i++) {
		double v = from_bits(0x3e9098c3df7c42faull + (uint64_t)i);
		printf("single %016llx atan crt=%016llx sw=%016llx %s | asin crt=%016llx sw=%016llx %s\n",
			(unsigned long long)bits_of(v),
			(unsigned long long)bits_of(atan(v)), (unsigned long long)bits_of(ufbx_atan(v)),
			bits_of(atan(v)) == bits_of(ufbx_atan(v)) ? "EQ" : "NEQ",
			(unsigned long long)bits_of(asin(v)), (unsigned long long)bits_of(ufbx_asin(v)),
			bits_of(asin(v)) == bits_of(ufbx_asin(v)) ? "EQ" : "NEQ");
	}
	{
		double base = 0.99999998319999869, ex = 1.00000003;
		printf("pow crt=%016llx sw=%016llx %s\n",
			(unsigned long long)bits_of(pow(base, ex)),
			(unsigned long long)bits_of(ufbx_pow(base, ex)),
			bits_of(pow(base, ex)) == bits_of(ufbx_pow(base, ex)) ? "EQ" : "NEQ");
	}
	return 0;
}
