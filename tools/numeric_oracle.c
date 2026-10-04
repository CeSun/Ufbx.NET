// C reference oracle for the ported numeric text->double layer
// (src/Ufbx.NET/Parse/Numeric.cs): ufbxi_parse_double, ufbxi_parse_inf_nan and the
// ufbxi_bigint_* multi-precision helpers. ufbx.c is a single translation unit, so
// including it makes every `static ufbxi_*` internal directly callable.
//
//   zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
//       tools/numeric_oracle.c -o tools/numeric_oracle.exe
//
// -mcpu=x86_64 -ffp-contract=off is MANDATORY (PORTING_NOTES.md "浮点语义"): the fast
// path in ufbxi_parse_double (ufbx.c:1677-1685) uses a hardware divide/multiply, and a
// native-CPU build can contract FP expressions, making the reference machine-dependent.
// UFBX_DEV / UFBX_UBSAN / UFBX_STATIC_ANALYSIS are deliberately NOT defined, so
// ufbxi_dev_assert() compiles to (void)0 (ufbx.c:1028-1032) and ufbxi_maybe_uninit()
// collapses to plain (value) (ufbx.c:587-591) -- the exact build the golden hashes come from.
//
// Output grammar (one record per line, space separated, all numbers lowercase hex where
// the field is named *hex* / *bits* / *val*):
//
//   G <num_cases> <num_prime> <fnv64>   corpus header: total case count, the size of the
//                                       primer prefix, and an FNV-1a-64 digest over every
//                                       case (length + bytes, canonical order). The replay
//                                       must reproduce the digest or the two sides are not
//                                       feeding the same bytes.
//   O <flags>                           ufbxi_parse_double_init_flags(): the flag set the
//                                       loader actually uses at ufbx.c:9791/9983.
//   W <idx> <hexbytes>                  corpus input table, canonical order (idx 0..N-1).
//   D <idx> <bits> <consumed> <ok>      ufbxi_parse_double(str,len,&end,ALLOW_FAST_PATH)
//                                       in canonical order, scratch re-primed first.
//   N <idx> <bits> <consumed> <ok>      the same with flags = 0 (forces the bigint path
//                                       even for values the fast path would take).
//   B <idx> <bits> <consumed> <ok>      the same with flags = AS_BINARY32 (ufbx.c:9792).
//   C <idx> <bits> <consumed> <ok>      flags = ALLOW_FAST_PATH, but the stack region the
//                                       three scratch arrays live in is scribbled with
//                                       garbage before EVERY call. This is the residue
//                                       test: ufbx.c:1605 declares the arrays uninitialized
//                                       and ufbxi_bigint_shift_left() (ufbx.c:1468-1478)
//                                       reads limbs beyond b.length, so a "zeroed memory"
//                                       port would show up here.
//   R <idx> <bits> <consumed> <ok>      flags = ALLOW_FAST_PATH, iterated in REVERSE order.
//                                       A value that moves between D and R is a value that
//                                       depends on what the previous parse left behind; the
//                                       port has to be order-sensitive in exactly the same
//                                       way, so the replay runs the reverse pass too.
//
// Direct helper probes (inputs are carried in the line, so the replay needs no second
// copy of the generator). PROBE_CAP limbs are always printed, in/out, so a stale write or
// an off-by-one length is visible even where it is outside the logical value.
//
//   M <idx> <cap> <inlen> <inhex> <mult> <add> <outlen> <outhex>       ufbxi_bigint_mad
//   S <idx> <cap> <inlen> <inhex> <amount> <outlen> <outhex>           ufbxi_bigint_shift_left
//   P <idx> <cap> <inlen> <inhex> <power> <outlen> <outhex>            ufbxi_bigint_mul_pow5
//   V <idx> <cap> <n> <m> <uin> <vin> <qin> <tail> <qlen> <qout> <uout> ufbxi_bigint_div
//   H <idx> <cap> <inlen> <inhex> <expin> <tailin> <bits> <expout> <tailout> extract_high
//   T <idx> <val> <shift> <tail> <out>                                 ufbxi_shift_right_round
//   I <idx> <hexbytes> <ok> <bits> <end>                               ufbxi_parse_inf_nan
//   Z <code>                                                           oracle self-error
//
// <consumed> is (end - str) and <ok> is (consumed == max_length), i.e. "the parse reached
// the buffer edge". The ASCII caller's own acceptance test is stricter (it compares
// end == str_data + str_len - 1 against the pushed '\0' at ufbx.c:9781/9789/9794), so the
// corpus deliberately includes both token shapes: strings whose last byte is a NUL inside
// max_length (ok = 0 by construction) and strings that stop exactly at the edge (ok = 1).
//
// probe_extract_high skips the states where the 64-bit window comes out zero: C computes
// ufbxi_lzcnt64(0), which is __builtin_clzll(0) and undefined (ufbx.c:937-939), so those
// rows are not a reference for anything. No ufbxi_parse_double input reaches them.
//
// Every pass re-runs the primer prefix (the first NUM_PRIME cases) before emitting, so
// each pass starts from a scratch state that is a pure function of the primer, never from
// whatever the process stack happened to hold.

#include "ufbx.c"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define NUM_CASES_MAX 20000
#define MAX_CASE 600
#define NUM_PRIME 8
#define PROBE_CAP 24

static char g_case[NUM_CASES_MAX][MAX_CASE];
static size_t g_case_len[NUM_CASES_MAX];
static size_t g_num_cases;

static uint32_t g_rng = 0x9E3779B9u;
static uint64_t g_digest = 1469598103934665603ull;

static uint32_t rnd_u(void)
{
	g_rng = g_rng * 1103515245u + 12345u;
	return g_rng;
}

static uint32_t rnd_below(uint32_t n)
{
	return (rnd_u() >> 8) % n;
}

static size_t add_case(const void *data, size_t n)
{
	if (n > MAX_CASE) n = MAX_CASE;
	if (g_num_cases >= NUM_CASES_MAX) {
		printf("Z case-overflow\n");
		exit(1);
	}
	memcpy(g_case[g_num_cases], data, n);
	g_case_len[g_num_cases] = n;
	g_digest ^= (uint64_t)n;
	g_digest *= 1099511628211ull;
	for (size_t i = 0; i < n; i++) {
		g_digest ^= (uint64_t)((const unsigned char*)data)[i];
		g_digest *= 1099511628211ull;
	}
	return g_num_cases++;
}

static size_t add_str(const char *s)
{
	return add_case(s, strlen(s));
}

static size_t add_nstr(const char *s, size_t n)
{
	return add_case(s, n);
}

// -- emitters

static void emit_hexbytes(const void *p, size_t n)
{
	const unsigned char *b = (const unsigned char*)p;
	for (size_t i = 0; i < n; i++) printf("%02x", b[i]);
}

static void emit_limbs(const uint32_t *l, int n)
{
	for (int i = 0; i < n; i++) printf("%08x", l[i]);
}

// Scribble over the stack region that ufbxi_parse_double()'s three 42-limb scratch arrays
// occupy (ufbx.c:1605). Called at the same nesting depth as the parse, so the frames alias.
static uint32_t clobber_seed = 0xC0FFEE00u;
static ufbxi_noinline void clobber_stack(void)
{
	volatile uint32_t buf[512];
	clobber_seed = clobber_seed * 1103515245u + 12345u;
	for (int i = 0; i < 512; i++) {
		buf[i] = clobber_seed ^ (uint32_t)i * 0x9E3779B9u;
	}
}

static ufbxi_noinline void one_parse(size_t idx, uint32_t flags, bool do_clobber)
{
	const char *s = g_case[idx];
	size_t len = g_case_len[idx];
	char *end = NULL;
	double v;
	uint64_t bits;
	size_t cons;

	if (do_clobber) clobber_stack();
	v = ufbxi_parse_double(s, len, &end, flags);
	memcpy(&bits, &v, sizeof(bits));
	cons = (size_t)(end - s);
	printf("%llu %016llx %llu %d\n",
		(unsigned long long)idx, (unsigned long long)bits,
		(unsigned long long)cons, cons == len ? 1 : 0);
}

static void prime_scratch(void)
{
	for (size_t i = 0; i < NUM_PRIME; i++) {
		char *end = NULL;
		(void)ufbxi_parse_double(g_case[i], g_case_len[i], &end, UFBXI_PARSE_DOUBLE_ALLOW_FAST_PATH);
	}
}

static void run_pass(char tag, uint32_t flags, bool reverse, bool do_clobber)
{
	// Every pass re-primes, so its scratch state is a pure function of the primer prefix
	// and identical on both sides of the differential.
	prime_scratch();
	if (reverse) {
		for (size_t i = g_num_cases; i-- > NUM_PRIME; ) {
			printf("%c ", tag);
			one_parse(i, flags, false);
		}
	} else {
		for (size_t i = NUM_PRIME; i < g_num_cases; i++) {
			printf("%c ", tag);
			one_parse(i, flags, do_clobber);
		}
	}
}

// -- corpus

static void add_repeat(char c, size_t n)
{
	char buf[MAX_CASE];
	if (n > MAX_CASE) n = MAX_CASE;
	memset(buf, c, n);
	add_case(buf, n);
}

static void build_corpus(void)
{
	// C: the primer. These are parsed (unchecked) before every pass, and they are chosen
	// so that they write the low limb slots of all three scratch arrays from in-range
	// values: long digit runs grow mantissa_limbs past index 4, and the large negative
	// exponents grow divisor_limbs past index 21 (5^300 is ~700 bits).
	add_repeat('9', 90);                                       // 0: 90 digits -> limb growth
	add_str("1e-300");                                         // 1: divisor 5^300
	add_str("1e300");                                          // 2: mul_pow5 on the mantissa
	add_str("1234567890123456789.12345678901234567890123456789e-100"); // 3
	add_str("0.5");                                            // 4
	add_str("1e-20");                                          // 5
	add_repeat('7', 61);                                       // 6
	add_str("7e-150");                                         // 7

	// -- literal styles that appear in FBX ASCII/binary text
	static const char *const fixed[] = {
		"0", "-0", "+0", "00", "000", "0000000", "007", "7", "1", "-1", "+1", "10", "100000",
		"0.5", ".5", "5.", "5.0", "0.0", "-0.0", "+0.0", ".", "-.", "+.", "..", "1.2.3", "00.500",
		"1e5", "1E5", "1e+5", "1e-5", "1e0", "1e", "1e-", "1e+", "+e", "e5", "E", "1e5e5", "1e5.5",
		"1e00000005", "1e007", "1e10000", "1e99999", "1e-99999", "1e500", "1e400", "1e310", "1e309",
		"1e308", "1e307", "1e22", "1e23", "1e-22", "1e-23", "1e-308", "1e-309", "1e-323", "1e-324",
		"1e-325", "1e-330", "1e-340", "1e1000", "1e-1000", "1e2147483647", "1e-2147483648",
		"2.2250738585072011e-308", "2.2250738585072014e-308", "4.9406564584124654e-324",
		"2.4703282292062327e-324", "2.4703282292062328e-324", "1.7976931348623157e308",
		"1.7976931348623159e308", "1.7976931348623159e309", "1.7976931348623157e310",
		"1.0000000000000005", "1.0000000000000015", "1.0000000000000025", "0.99999999999999995",
		"0.5000000000000001", "0.5000000000000000", "9007199254740992", "9007199254740993",
		"9007199254740994", "9007199254740995", "4503599627370497", "2251799813685249",
		"123456789012345678901234567890", "1234567890123456789", "12345678901234567890",
		"18446744073709551615", "18446744073709551616", "18446744073709551617",
		"0.30000000000000004", "0.1", "0.2", "0.3", "0.7", "1.1", "2.675", "-2.675",
		"3.4028234663852886e38", "3.402823567797336e38", "1.1754943508222875e38",
		"1e-45", "1e-46", "1.4e-45", "8.388608e37", "16777216", "16777217", "16777218",
		"inf", "INF", "Inf", "iNf", "iNF", "inF", "InfTy", "Infinity", "INFINITY", "infinity",
		"infin", "infinit", "infini", "inf(x)", "INF(", "inf(", "inity",
		"nan", "NAN", "NaN", "nAn", "nan()", "nan(1)", "nan(a)", "nan(Z)", "nan( )", "nan(",
		"nan1", "nan(abc)", "NAN(ABC)", "na", "n", "nan(-1)", "nan(_)", "nan(())",
		"+inf", "-inf", "+infinity", "-infinity", "+nan", "-nan", "-NAN(42)",
		"1.#INF", "1.#inf", "1.#NAN", "1.#nan", "1.#IND", "1.#ind", "2.#NAN", "0.#NAN", "9.#INF",
		"1.#", "1.#Q", "1.#NAN9", "1.#INF123", "-1.#INF", "+1.#NAN", "11.#NAN", "1.#INFx",
		// numbers that stop in front of an inf/nan-ish tail: ufbxi_parse_double re-scans the
		// WHOLE string (ufbx.c:1667), so these return +-0 with p_end == str, not a parse error.
		"1.5i", "1.5n", "1.5#", "1.5e3i", "1.5e3n", "1i", "12N", "0x1f", "1,5", "1 2", "1-2", "1+2",
		"1..", "1e5i", "1e5n", "5.INF", "1.2.3inf", "-inf9", "1.#INF.", "12inf", "123456789inf",
		"0x10", "1e5x", "1e5#INF", "5.#", "1.#NANe5",
		"", "-", "+", "e", "E", "#", "(", ")", " ", "\t", ",", ";", "x", "\n",
		"1\t2", "1 2 3", "- 1", "+ 1", "-1-", "--1", "1--", "1e--5", "1e+-5", "-+1", "+-1",
		"999999999999999999999999999999999999999999",
		"0000000000000000000000000000000000000000001",
		"0.000000000000000000000000000000000000000001",
		"100000000000000000000000000000000000000000",
		"1.00000000000000000000000000000000000000000e-300",
		"0.00000000000000000000000000000000000000001e-100",
		"50000000000000000000000000000000000000000000000",
	};
	for (size_t i = 0; i < sizeof(fixed)/sizeof(fixed[0]); i++) {
		add_str(fixed[i]);
	}

	// -- overlong digit runs: these push big_mantissa.length to max_limbs (ufbx.c:1603,1620)
	// and keep incrementing dec_exponent instead, which is the "stale length" territory.
	static const int long_lens[] = {
		15, 16, 17, 18, 19, 20, 21, 22, 26, 30, 34, 38, 42, 50, 58, 66, 74, 82, 90, 100,
		120, 150, 200, 250, 300, 350, 400, 430,
	};
	char tmp[MAX_CASE];
	for (size_t i = 0; i < sizeof(long_lens)/sizeof(long_lens[0]); i++) {
		int n = long_lens[i];
		add_repeat('9', (size_t)n);
		add_repeat('1', (size_t)n);
		add_repeat('0', (size_t)n);
		for (int j = 0; j < n; j++) tmp[j] = (char)('0' + (j % 10));
		add_case(tmp, (size_t)n);
		// decimal point in the middle: dec_exponent goes negative over the whole tail
		for (int j = 0; j < n; j++) tmp[j] = (char)('0' + ((j * 7) % 10));
		tmp[n / 2] = '.';
		add_case(tmp, (size_t)n);
		// leading zeros then significant digits
		for (int j = 0; j < n; j++) tmp[j] = '0';
		tmp[0] = '5';
		add_case(tmp, (size_t)n);
	}

	// -- long runs with exponents: forces the divisor path on a saturated mantissa
	for (int e = -500; e <= 500; e += 13) {
		int n = 20 + (abs(e) % 17);
		char *p = tmp;
		p += sprintf(p, "%d.", e);
		for (int j = 0; j < n; j++) *p++ = (char)('0' + (j % 10));
		*p = '\0';
		add_str(tmp);
		n = 40 + (abs(e) % 31);
		p = tmp;
		for (int j = 0; j < n; j++) *p++ = (char)('1' + (j % 9));
		p += sprintf(p, "e%d", e);
		add_str(tmp);
	}

	// -- tie / half-to-even families: 17 significant digits ending in 5, then 4999.../5000...
	// tails. These land on ULP boundaries where ufbxi_shift_right_round() has to break the
	// tie by r_odd/r_round/r_tail (ufbx.c:1523-1528).
	static const char *const tie_base[] = {
		"1.0000000000000005", "1.0000000000000015", "1.0000000000000025", "1.0000000000000035",
		"1.0000000000000045", "2.0000000000000005", "2.0000000000000015", "3.0000000000000005",
		"4.5035996273704965", "9007199254740992.5", "0.50000000000000006", "0.50000000000000005",
		"0.25000000000000006", "0.12500000000000003", "1.9999999999999999", "1.0000000000000001",
		"0.00000000000000000000000000000001", "1e-320", "1.5e-321", "2.5e-322", "5e-324",
		"1.00000000000000005e300", "1.50000000000000005e308", "5e-24", "5e-25", "1.5e-1",
	};
	for (size_t i = 0; i < sizeof(tie_base)/sizeof(tie_base[0]); i++) {
		add_str(tie_base[i]);
	}
	for (int k = 0; k < 900; k++) {
		char *p = tmp;
		int nd = 15 + (int)rnd_below(6);
		for (int j = 0; j < nd; j++) *p++ = (char)('0' + rnd_below(10));
		*p++ = '5';
		if (rnd_below(2)) { *p++ = '.'; *p++ = '5'; if (rnd_below(2)) { *p++ = '0'; *p++ = (char)('0' + rnd_below(10)); } }
		if (rnd_below(2)) p += sprintf(p, "e%d", (int)rnd_below(60) - 30);
		*p = '\0';
		add_str(tmp);
	}

	// -- grammar-guided random numbers (the shape FBX actually writes)
	for (int k = 0; k < 9000; k++) {
		char *p = tmp;
		if (rnd_below(2)) *p++ = rnd_below(2) ? '-' : '+';
		int ni = (int)rnd_below(20);
		if (rnd_below(6) == 0) { for (int j = 0; j < (int)rnd_below(4); j++) *p++ = '0'; }
		if (ni == 0 && rnd_below(3)) *p++ = '0';
		for (int j = 0; j < ni; j++) *p++ = (char)('0' + rnd_below(10));
		if (rnd_below(2)) {
			*p++ = '.';
			int nf = (int)rnd_below(24);
			for (int j = 0; j < nf; j++) *p++ = (char)('0' + rnd_below(10));
		}
		if (rnd_below(2)) {
			*p++ = rnd_below(2) ? 'e' : 'E';
			*p++ = rnd_below(2) ? '-' : '+';
			int ne = 1 + (int)rnd_below(3);
			for (int j = 0; j < ne; j++) *p++ = (char)('0' + rnd_below(10));
		}
		*p = '\0';
		add_str(tmp);
	}

	// -- fragment soup: mostly exercises the reject paths and the inf/nan entry gate
	static const char *const frag[] = {
		"0","1","2","5","9","+","-",".","e","E"," ","\t",",","x","i","I","n","N","f","a","t","y",
		"#","(",")","inf","nan","Infinity","1.#NAN","1.#INF","1.#IND","\0","\x01","\x7f","\x80",
		"\xff","0123456789","1e","e-","--","..","1.5","-0","E5","#INF","()",
	};
	const int nfrag = (int)(sizeof(frag)/sizeof(frag[0]));
	for (int k = 0; k < 5000; k++) {
		char *p = tmp;
		int parts = 1 + (int)rnd_below(10);
		for (int j = 0; j < parts; j++) {
			const char *f = frag[rnd_below((uint32_t)nfrag)];
			size_t fl = strlen(f);
			if ((size_t)(p - tmp) + fl >= MAX_CASE - 1) break;
			memcpy(p, f, fl);
			p += fl;
		}
		*p = '\0';
		add_nstr(tmp, (size_t)(p - tmp));
	}

	// -- NUL / token-edge shapes. ufbxi_parse_double() is driven from the ASCII parser over
	// tokens that carry a pushed '\0' *inside* max_length (ufbx.c:9781) and are only accepted
	// when the parse ends at that last byte (ufbx.c:9789/9794: end == str_data + str_len - 1),
	// so the NUL byte itself is inside the scanned window. Every other read in
	// ufbxi_parse_double() is bounded by `p != end` (ufbx.c:1613-1661), so a string that stops
	// exactly at the buffer edge with no NUL must also be covered.
	static const char *const nul_base[] = {
		"0", "1", "1.5", "-1.5", "+1.5", "1e5", "1E+5", "12345678901234567890",
		"1.7976931348623157e308", "5e-324", "inf", "nan", "infinity", "nan(abc)",
		"1.#INF", "1.#IND", "1.#NAN", "1.5e3", "0.0", ".5", "5.", "e5", ".", "-", "+",
		"123abc", "1.5i", "1.#", "1.#I", "nan(", "nan()", "9999999999999999999999999999",
		"-inf", "1.#IND5", "7e999", "0.5",
	};
	for (size_t i = 0; i < sizeof(nul_base)/sizeof(nul_base[0]); i++) {
		const char *s = nul_base[i];
		size_t l = strlen(s);
		char buf[MAX_CASE];
		if (l * 2 + 2 >= MAX_CASE) continue;
		// "<s>\0" with max_length covering the NUL
		memcpy(buf, s, l); buf[l] = '\0';
		add_case(buf, l + 1);
		// same bytes, but max_length stopping before the NUL: edge termination
		add_case(buf, l);
		// "\0<s>" NUL first
		buf[0] = '\0'; memcpy(buf + 1, s, l);
		add_case(buf, l + 1);
		// "<s>\0<s>" NUL in the middle
		memcpy(buf, s, l); buf[l] = '\0'; memcpy(buf + l + 1, s, l);
		add_case(buf, 2 * l + 1);
	}
	// random grammar strings carrying an embedded NUL at an arbitrary position
	for (int k = 0; k < 700; k++) {
		char *p = tmp;
		int ni = (int)rnd_below(12);
		if (rnd_below(2)) *p++ = rnd_below(2) ? '-' : '+';
		for (int j = 0; j < ni; j++) *p++ = (char)('0' + rnd_below(10));
		if (rnd_below(2)) { *p++ = '.'; int nf = (int)rnd_below(12); for (int j = 0; j < nf; j++) *p++ = (char)('0' + rnd_below(10)); }
		if (rnd_below(3) == 0) { *p++ = 'e'; *p++ = '-'; int ne = 1 + (int)rnd_below(2); for (int j = 0; j < ne; j++) *p++ = (char)('0' + rnd_below(10)); }
		size_t n = (size_t)(p - tmp);
		if (n == 0) { *p++ = '1'; n = 1; }
		size_t cut = rnd_below((uint32_t)(n + 1));
		char out[MAX_CASE];
		for (size_t j = 0; j < cut; j++) out[j] = tmp[j];
		out[cut] = '\0';
		for (size_t j = cut; j < n; j++) out[j + 1] = tmp[j];
		add_case(out, n + 1);
	}
}

// -- bigint probes

static void fill_state(uint32_t *limbs, int pattern)
{
	switch (pattern) {
	case 0: memset(limbs, 0, PROBE_CAP * sizeof(uint32_t)); break;
	case 1: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = 0xffffffffu; break;
	case 2: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = 1u; break;
	case 3: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = (i & 1) ? 0x5a5a5a5au : 0xa5a5a5a5u; break;
	case 4: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = (uint32_t)((i + 1) * 0x11111111); break;
	case 5: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = rnd_u(); break;
	case 6: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = (i & 1) ? 0u : 0x80000000u; break;
	case 7: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = 0x80000000u + (uint32_t)i; break;
	// the "residue" states: a short logical length over a dirty buffer, exactly what
	// ufbxi_bigint_shift_left() reads at ufbx.c:1470-1471 when b.length < 3.
	case 8: limbs[0] = 1u; for (int i = 1; i < PROBE_CAP; i++) limbs[i] = 0xdeadbeefu; break;
	case 9: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = 0xffffffffu >> i; break;
	case 10: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = (i < 3) ? 0u : 0xf0f0f0f0u; limbs[0] = 0x12345678u; break;
	case 11: for (int i = 0; i < PROBE_CAP; i++) limbs[i] = (uint32_t)(0xffffffffu / (uint32_t)(i + 2)); break;
	default: memset(limbs, 0, PROBE_CAP * sizeof(uint32_t)); break;
	}
}

static const uint32_t mad_lens[] = { 0u, 1u, 2u, 3u, 4u, 5u, 8u, 12u };

static void probe_mad(void)
{
	static const uint64_t edge_muls[] = {
		UINT64_C(0), UINT64_C(1), UINT64_C(5), UINT64_C(10), UINT64_C(0xffffffff),
		UINT64_C(0x100000000), UINT64_C(0x1ffffffff), UINT64_C(0x3ffffffffffffff),
		UINT64_C(0x7fffffffffffffff), UINT64_C(0xffffffffffffff), UINT64_C(0x1000000000000),
	};
	static const uint64_t edge_adds[] = {
		UINT64_C(0), UINT64_C(1), UINT64_C(9), UINT64_C(0xffffffff), UINT64_C(0x100000000),
		UINT64_C(0x1000000000000000), UINT64_C(0x3ffffffffffffff), UINT64_C(0x7fffffffffffffff),
		UINT64_C(999999999999999999),
	};
	int idx = 0;

	// the real multiplicands: ufbxi_pow5_tab[p] << p (ufbx.c:1625,1694,1762)
	for (uint32_t p = 0; p < 28; p++) {
		uint64_t m = ufbxi_pow5_tab[p] << p;
		for (size_t li = 0; li < sizeof(mad_lens)/sizeof(mad_lens[0]); li++) {
			for (int pat = 0; pat < 12; pat++) {
				uint32_t limbs[PROBE_CAP], in[PROBE_CAP];
				uint64_t a = edge_adds[(size_t)((p + li + pat) % (sizeof(edge_adds)/sizeof(edge_adds[0])))];
				fill_state(limbs, pat);
				memcpy(in, limbs, sizeof(in));
				ufbxi_bigint b = ufbxi_bigint_make(limbs, PROBE_CAP);
				b.length = mad_lens[li];
				ufbxi_bigint_mad(&b, m, a);
				printf("M %d %d %u ", idx, PROBE_CAP, mad_lens[li]);
				emit_limbs(in, PROBE_CAP);
				printf(" %016llx %016llx %u ", (unsigned long long)m, (unsigned long long)a, b.length);
				emit_limbs(limbs, PROBE_CAP);
				printf("\n");
				idx++;
			}
		}
	}
	// edge multiplicands / pure-append cases (the while(carry) loop at ufbx.c:1396-1400)
	for (size_t mi = 0; mi < sizeof(edge_muls)/sizeof(edge_muls[0]); mi++) {
		for (size_t ai = 0; ai < sizeof(edge_adds)/sizeof(edge_adds[0]); ai++) {
			for (size_t li = 0; li < sizeof(mad_lens)/sizeof(mad_lens[0]); li++) {
				uint32_t limbs[PROBE_CAP], in[PROBE_CAP];
				fill_state(limbs, (int)((mi + ai) % 12));
				memcpy(in, limbs, sizeof(in));
				ufbxi_bigint b = ufbxi_bigint_make(limbs, PROBE_CAP);
				b.length = mad_lens[li];
				ufbxi_bigint_mad(&b, edge_muls[mi], edge_adds[ai]);
				printf("M %d %d %u ", idx, PROBE_CAP, mad_lens[li]);
				emit_limbs(in, PROBE_CAP);
				printf(" %016llx %016llx %u ", (unsigned long long)edge_muls[mi], (unsigned long long)edge_adds[ai], b.length);
				emit_limbs(limbs, PROBE_CAP);
				printf("\n");
				idx++;
			}
		}
	}
}

static const uint32_t shift_amounts[] = {
	0u, 1u, 2u, 3u, 15u, 16u, 23u, 30u, 31u, 32u, 33u, 34u, 47u, 48u, 63u, 64u, 65u, 79u, 95u,
	96u, 97u, 111u, 127u, 128u, 129u, 143u, 159u, 160u, 191u, 192u, 223u, 255u, 256u, 287u, 319u, 351u,
};

static const uint32_t shift_lens[] = { 1u, 2u, 3u, 4u, 5u, 7u, 9u, 12u };

static void probe_shift(void)
{
	int idx = 0;
	for (size_t li = 0; li < sizeof(shift_lens)/sizeof(shift_lens[0]); li++) {
		for (int pat = 0; pat < 12; pat++) {
			for (size_t ai = 0; ai < sizeof(shift_amounts)/sizeof(shift_amounts[0]); ai++) {
				uint32_t amount = shift_amounts[ai];
				uint32_t words = amount / UFBXI_BIGINT_LIMB_BITS;
				uint32_t len = shift_lens[li];
				// keep inside the probe buffer: ufbx.c:1464 would be an OOB stack write
				if (len + words + 2 >= PROBE_CAP) continue;
				uint32_t limbs[PROBE_CAP], in[PROBE_CAP];
				fill_state(limbs, pat);
				memcpy(in, limbs, sizeof(in));
				ufbxi_bigint b = ufbxi_bigint_make(limbs, PROBE_CAP);
				b.length = len;
				ufbxi_bigint_shift_left(&b, amount);
				printf("S %d %d %u ", idx, PROBE_CAP, len);
				emit_limbs(in, PROBE_CAP);
				printf(" %u %u ", amount, b.length);
				emit_limbs(limbs, PROBE_CAP);
				printf("\n");
				idx++;
			}
		}
	}
}

static void probe_mul_pow5(void)
{
	static const uint32_t powers[] = {
		0u, 1u, 2u, 3u, 13u, 26u, 27u, 28u, 29u, 30u, 53u, 54u, 55u, 80u, 81u, 82u, 100u, 120u,
	};
	static const uint32_t lens[] = { 1u, 2u, 3u, 4u, 6u };
	int idx = 0;
	for (size_t li = 0; li < sizeof(lens)/sizeof(lens[0]); li++) {
		for (int pat = 0; pat < 12; pat++) {
			for (size_t pi = 0; pi < sizeof(powers)/sizeof(powers[0]); pi++) {
				uint32_t limbs[PROBE_CAP], in[PROBE_CAP];
				fill_state(limbs, pat);
				memcpy(in, limbs, sizeof(in));
				ufbxi_bigint b = ufbxi_bigint_make(limbs, PROBE_CAP);
				b.length = lens[li];
				ufbxi_bigint_mul_pow5(&b, powers[pi]);
				// ufbxi_bigint_mad would have written past capacity (assert is a no-op in a
				// release build), which the port cannot reproduce: only report sane sizes.
				if (b.length >= PROBE_CAP) { printf("Z pow5-overflow %d\n", idx); continue; }
				printf("P %d %d %u ", idx, PROBE_CAP, lens[li]);
				emit_limbs(in, PROBE_CAP);
				printf(" %u %u ", powers[pi], b.length);
				emit_limbs(limbs, PROBE_CAP);
				printf("\n");
				idx++;
			}
		}
	}
}

static void probe_div(void)
{
	int idx = 0;
	for (int trial = 0; trial < 900; trial++) {
		int n = 2 + (int)rnd_below(7);          // v->length, ufbxi_bigint_div needs n >= 2
		int m = 1 + (int)rnd_below(8);          // u->length - v->length, needs m >= 1
		int u_len = n + m;
		if (u_len + 1 >= PROBE_CAP) continue;
		uint32_t un[PROBE_CAP], vn[PROBE_CAP], qn[PROBE_CAP];
		uint32_t uin[PROBE_CAP], vin[PROBE_CAP];
		memset(un, 0, sizeof(un)); memset(vn, 0, sizeof(vn)); memset(qn, 0, sizeof(qn));
		for (int i = 0; i < u_len; i++) {
			// ufbx.c:1410 asserts the top limb of u has no high bit
			un[i] = (i == u_len - 1 && trial % 3 != 0) ? (rnd_u() & 0x7fffffffu) : rnd_u();
		}
		for (int i = 0; i < n; i++) {
			vn[i] = rnd_u();
		}
		// v_hi must have its high bit set (ufbx.c:1410); several flavours to drive the
		// qhat correction loop (ufbx.c:1416-1420) and the restore branch (1430-1439).
		uint32_t hi = 0x80000000u + (rnd_u() & 0x7fffffffu);
		switch (trial % 5) {
		case 0: hi = 0x80000000u; break;
		case 1: hi = 0x80000001u; break;
		case 2: hi = 0xffffffffu; break;
		case 3: hi = 0xc0000000u + (rnd_u() & 0x0fffffffu); break;
		default: hi = 0xfffffff0u + (rnd_u() & 0x0000000fu); break;
		}
		vn[n - 1] = hi;
		memcpy(uin, un, sizeof(un));
		memcpy(vin, vn, sizeof(vn));
		uint32_t qin[PROBE_CAP];
		memset(qin, 0, sizeof(qin));
		ufbxi_bigint q = ufbxi_bigint_make(qn, PROBE_CAP);
		ufbxi_bigint u = ufbxi_bigint_make(un, PROBE_CAP);
		ufbxi_bigint v = ufbxi_bigint_make(vn, PROBE_CAP);
		u.length = (uint32_t)u_len;
		v.length = (uint32_t)n;
		q.length = 0u;
		bool tail = ufbxi_bigint_div(&q, &u, &v);
		printf("V %d %d %d %d ", idx, PROBE_CAP, n, m);
		emit_limbs(uin, PROBE_CAP); printf(" ");
		emit_limbs(vin, PROBE_CAP); printf(" ");
		emit_limbs(qin, PROBE_CAP); printf(" ");
		printf("%d %u ", tail ? 1 : 0, q.length);
		emit_limbs(qn, PROBE_CAP); printf(" ");
		emit_limbs(un, PROBE_CAP);
		printf("\n");
		idx++;
	}
}

static void probe_extract(void)
{
	static const uint32_t lens[] = { 1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 12u, 14u, 20u };
	int idx = 0;
	for (size_t li = 0; li < sizeof(lens)/sizeof(lens[0]); li++) {
		for (int pat = 0; pat < 12; pat++) {
			for (int extra = 0; extra < 5; extra++) {
				uint32_t L = lens[li];
				uint32_t limbs[PROBE_CAP];
				// ufbxi_lzcnt64() is __builtin_clzll in this build (ufbx.c:937-939), so a
				// top-64-bits of zero is UB rather than a number: extra 1..4 keep something
				// in the top two limbs, and extra==3 deliberately probes the "shift >= 32"
				// path (top limb zero, the one below non-zero, which IS defined).
				fill_state(limbs, pat);
				if (extra == 1) limbs[L - 1] = 0x80000000u;
				if (extra == 2) { for (uint32_t i = 0; i < L; i++) limbs[i] = 0u; limbs[L - 1] = 1u; }
				if (extra == 3) { if (L < 2) continue; limbs[L - 1] = 0u; limbs[L - 2] = 0x80000001u; }
				if (extra == 4) { limbs[L - 1] |= 1u; }
				if (limbs[L - 1] == 0u && (L < 2 || limbs[L - 2] == 0u)) continue;
				ufbxi_bigint b = ufbxi_bigint_make(limbs, PROBE_CAP);
				b.length = L;
				int32_t exp_in = (int32_t)(idx % 7) * 100 - 300;
				bool tail_in = (idx & 1) != 0;
				int32_t exp = exp_in;
				bool tail = tail_in;
				uint64_t r = ufbxi_bigint_extract_high(b, &exp, &tail);
				printf("H %d %d %u ", idx, PROBE_CAP, b.length);
				emit_limbs(limbs, PROBE_CAP);
				printf(" %d %d %016llx %d %d\n", exp_in, tail_in ? 1 : 0,
					(unsigned long long)r, (int)exp, tail ? 1 : 0);
				idx++;
			}
		}
	}
}

static void probe_round(void)
{
	static const uint64_t vals[] = {
		UINT64_C(0), UINT64_C(1), UINT64_C(2), UINT64_C(3), UINT64_C(4), UINT64_C(5),
		UINT64_C(6), UINT64_C(7), UINT64_C(0x8000000000000000), UINT64_C(0x7fffffffffffffff),
		UINT64_C(0xffffffffffffffff), UINT64_C(0x5555555555555555), UINT64_C(0xaaaaaaaaaaaaaaaa),
		UINT64_C(0x000fffffffffffff), UINT64_C(0x0010000000000000), UINT64_C(0x0008000000000003),
		UINT64_C(0x000c000000000001), UINT64_C(0x0006000000000002), UINT64_C(0xfffffffffffff800),
	};
	static const uint32_t shifts[] = {
		0u, 1u, 2u, 3u, 4u, 5u, 10u, 17u, 23u, 24u, 32u, 33u, 40u, 52u, 53u, 54u, 63u, 64u, 65u, 70u, 100u,
	};
	int idx = 0;
	for (size_t vi = 0; vi < sizeof(vals)/sizeof(vals[0]); vi++) {
		for (size_t si = 0; si < sizeof(shifts)/sizeof(shifts[0]); si++) {
			for (int t = 0; t < 2; t++) {
				uint64_t r = ufbxi_shift_right_round(vals[vi], shifts[si], t != 0);
				printf("T %d %016llx %u %d %016llx\n", idx,
					(unsigned long long)vals[vi], shifts[si], t, (unsigned long long)r);
				idx++;
			}
		}
	}
}

static void probe_inf_nan(void)
{
	static const char *const cases[] = {
		"", "-", "+", ".", "0", "i", "I", "n", "N", "#", "1.", "1.#", "1.#I", "1.#IN", "1.#INF",
		"1.#NAN", "1.#IND", "1.#NAN0", "1.#INF9", "2.#inf", "9.#ind", "0.#nan",
		"inf", "INF", "Inf", "in", "inF", "infinity", "Infinity", "INFINITY", "infinit", "inity",
		"inf(", "INF()", "nan", "NAN", "nan()", "nan(", "nan(x)", "nan(1)", "nan(abc)", "nan(_)",
		"nan(a)", "nan(A)", "nan(0x)", "nan\t", "nan ", "nan()", "-nan", "+nan", "-inf", "+inf",
		"-infinity", "+Infinity", "nan9", "infx", "NaN)", "nan(())", "nan(123)", "1.5e3i", "1.5i",
		"12N", "0x1f", "nan(-)", "nan(aZ0)", "nan(Z)", "\xff\xfe", "\x80inf", "i\xffnf", "nan\xff",
	};
	for (size_t i = 0; i < sizeof(cases)/sizeof(cases[0]); i++) {
		const char *s = cases[i];
		size_t len = strlen(s);
		double result = 12345.0;
		char *end = (char*)s;
		bool ok = ufbxi_parse_inf_nan(&result, s, len, &end);
		uint64_t bits = 0;
		memcpy(&bits, &result, sizeof(bits));
		printf("I %llu ", (unsigned long long)i);
		emit_hexbytes(s, len);
		printf(" %d %016llx %llu\n", ok ? 1 : 0, (unsigned long long)bits, ok ? (unsigned long long)(end - s) : 0ull);
	}
	// plus every corpus case: the reject paths and the "(foo)" scan are cheap to cover
	for (size_t i = 0; i < g_num_cases; i++) {
		if (i % 4) continue;
		const char *s = g_case[i];
		size_t len = g_case_len[i];
		double result = 12345.0;
		char *end = (char*)s;
		bool ok = ufbxi_parse_inf_nan(&result, s, len, &end);
		uint64_t bits = 0;
		memcpy(&bits, &result, sizeof(bits));
		printf("I %llu ", (unsigned long long)g_num_cases + (unsigned long long)i);
		emit_hexbytes(s, len);
		printf(" %d %016llx %llu\n", ok ? 1 : 0, (unsigned long long)bits, ok ? (unsigned long long)(end - s) : 0ull);
	}
}

int main(void)
{
	setvbuf(stdout, NULL, _IOFBF, 1u << 20);

	build_corpus();

	printf("G %llu %d %016llx\n", (unsigned long long)g_num_cases, NUM_PRIME, (unsigned long long)g_digest);
	{
		uint32_t f = ufbxi_parse_double_init_flags();
		printf("O %u\n", f);
	}
	for (size_t i = 0; i < g_num_cases; i++) {
		printf("W %llu ", (unsigned long long)i);
		emit_hexbytes(g_case[i], g_case_len[i]);
		printf("\n");
	}

	run_pass('D', UFBXI_PARSE_DOUBLE_ALLOW_FAST_PATH, false, false);
	run_pass('N', 0u, false, false);
	run_pass('B', UFBXI_PARSE_DOUBLE_AS_BINARY32, false, false);
	run_pass('C', UFBXI_PARSE_DOUBLE_ALLOW_FAST_PATH, false, true);
	run_pass('R', UFBXI_PARSE_DOUBLE_ALLOW_FAST_PATH, true, false);

	probe_mad();
	probe_shift();
	probe_mul_pow5();
	probe_div();
	probe_extract();
	probe_round();
	probe_inf_nan();

	return 0;
}
