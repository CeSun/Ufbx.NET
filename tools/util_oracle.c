// C reference oracle for the ported Util layer (Util/Hash.cs, Util/Utf8.cs,
// Util/StringPool.cs). Included as a single translation unit with ufbx.c so the
// `static ufbxi_*` internals are callable directly; the C# side must reproduce
// these numbers bit-for-bit.
//
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx tools/util_oracle.c -o tools/util_oracle.exe
//
// Output grammar (one record per line, space-separated):
//   H <case> <hash-hex8>                 ufbxi_hash_string
//   A <case> <hash-hex8> <non_ascii>     ufbxi_hash_string_check_ascii
//   V <case> <valid_length>              ufbxi_utf8_valid_length
//   C <case> <key-hex8>                  ufbxi_get_concat_key (single part)
//   P <mode> <case> <rank> <out_length> <hexdata>   ufbxi_push_string_place
//   S <case> <raw_rank> <raw_len> <utf8_rank> <utf8_len> <hexraw> <hexutf8>
//                                        ufbxi_push_sanitized_string (the DOM value path)
//   F <num_strings> <fnv64-hex16>        ufbxi_strings[]: entry count plus an FNV-1a digest
//                                        over the constant contents in table order, which is
//                                        what UfbxiStrings.All has to reproduce
//   L <index> <len> <hexdata>            one ufbxi_strings[] entry, in table order
//   K <index> <matched> <rank> <len>     after the ufbxi_load_strings() interning, does a
//                                        heap copy of a name resolve back to the constant
//                                        pointer? Negative indices are names that are not
//                                        in the table and must therefore be pooled copies.
//   FE <ix> <dst_null> <dst_size> <err_null> <desc_null> <desc_hex> <info_len> <info_hex>
//                                        ufbx_format_error() (ufbx.c:30606-30642): every
//                                        input of the call plus C's return value and the
//                                        first max(dst_size,1) bytes of a 0xCD-filled
//                                        output buffer. See the `FE` block below for why
//                                        all of it has to be synthesized by hand.
// The table order is observable beyond the digest: ufbx.c:11418 asserts it is strictly
// ascending under ufbxi_str_less() (in a UFBX_REGRESSION build), and ufbx.c:26619 walks it
// linearly relying on that, so the port keeps UfbxiStrings.All in exactly this order.
//
// `rank` is the position of the returned pointer among the pointers handed out by the *same
// pool*, ordered by address: the port cannot reproduce raw addresses, only their relative
// order, and that order is what address-keyed comparisons depend on. It has to be per-pool,
// see the `GRP_` comment below.
//
// `ufbxi_empty_char` (a static in the translation unit) is reported as rank -2 and
// excluded from the ranked set: the relative order of the image's .rodata and the
// heap arena is unspecified in C itself (it is not an observable the port can or
// should reproduce), see PORTING_NOTES.md "C 指针序 ≡ 分配序".

#include "ufbx.c"

#define NUM_RAND 192
#define NUM_EXTRA 28
#define NUM_CASES (NUM_RAND + NUM_EXTRA)
#define MAX_LEN 40
#define NUM_MODES 5

static char g_buf[NUM_CASES * MAX_LEN];
static size_t g_off[NUM_CASES];
static size_t g_len[NUM_CASES];

// Hand-picked UTF-8 boundary cases the random corpus only hits by accident: valid
// 2/3/4-byte sequences, overlong encodings, surrogates, out-of-range code points,
// truncated tails, 5/6-byte leads, 0xfe/0xff, and embedded NULs.
// Encoded as hex so the C# side can reproduce the exact same input bytes.
static const char *g_extra_hex[NUM_EXTRA] = {
	"c3a9",
	"c280",
	"c1bf",
	"c080",
	"c2",
	"e282ac",
	"e0a080",
	"e09fff",
	"eda080",
	"ed9fbf",
	"ee8080",
	"efbfbd",
	"e282",
	"f09f9880",
	"f08f8080",
	"f48fbfbf",
	"f4908080",
	"f880",
	"fc80808080",
	"fe",
	"ff",
	"00",
	"410042",
	"616263c3616263",
	"c3c3c3c3c3c3c3c3",
	"e282ace282ace282ac",
	"f09f9880f09f9880f09f9880",
	"ffffffffffffffffffffffffffffffff",
};

static int hex_nibble(char c)
{
	if (c >= '0' && c <= '9') return c - '0';
	if (c >= 'a' && c <= 'f') return c - 'a' + 10;
	return -1;
}

static size_t hex_decode(const char *hex, char *dst, size_t cap)
{
	size_t n = 0;
	for (size_t i = 0; hex[i] && hex[i + 1]; i += 2) {
		int hi = hex_nibble(hex[i]), lo = hex_nibble(hex[i + 1]);
		ufbx_assert(hi >= 0 && lo >= 0);
		if (n == cap) break;
		dst[n++] = (char)((hi << 4) | lo);
	}
	return n;
}

static void gen_corpus(void)
{
	uint32_t st = 12345u;
	size_t p = 0;
	for (int i = 0; i < NUM_CASES; i++) {
		g_off[i] = p;
		if (i < NUM_RAND) {
			size_t len = (size_t)(i % MAX_LEN);
			g_len[i] = len;
			for (size_t j = 0; j < len; j++) {
				st = st * 1103515245u + 12345u;
				uint32_t r = (st >> 16) & 0xffu;
				uint32_t sel = st & 3u;
				if (sel == 0u) {
					r = 'a' + (r % 26u);          // name-ish ASCII
				} else if (sel == 1u) {
					r = 0x20u + (r % 0x5fu);      // printable ASCII
				} else if (sel == 2u) {
					r = 0xc0u + (r % 0x40u);      // UTF-8 lead bytes
				} else {
					r &= 0xffu;                   // any byte: NUL, continuation, 0xfe..
				}
				g_buf[p++] = (char)r;
			}
		} else {
			const char *hex = g_extra_hex[i - NUM_RAND];
			g_len[i] = hex_decode(hex, g_buf + p, MAX_LEN);
			p += g_len[i];
		}
	}
}

static void emit_hex(const char *s, size_t n)
{
	for (size_t j = 0; j < n; j++) printf("%02x", (uint8_t)s[j]);
}

// -- FE: ufbx_format_error (ufbx.c:30606-30642, ufbx.h:5336) ----------------------------
//
// The only public function of the error layer, and one that ufbx itself never calls (the
// symbol has no other reference in ufbx.c), so no golden and no load path reaches it. The
// reference build also compiles the error stack out (UFBXI_FEATURE_ERROR_STACK == 0,
// ufbx.c:170-172), so real errors always arrive with `stack_size == 0` and the frame loop
// is dead there too. Every input below is therefore hand-built; `desc_null`, `err_null` and
// `dst_null` reproduce C's three NULL pointers, which the port mirrors as C# `null`.
//
//   FE <ix> <dst_null> <dst_size> <err_null> <desc_null> <desc_hex> <info_len> <info_hex>
//      <stack_size> (<line> <func_hex> <fdesc_hex>){min(stack_size,8)} <ret> <out_hex>
//
// `<out_hex>` is the first `max(dst_size,1)` bytes of a buffer that *both* sides pre-fill
// with 0xCD: untouched bytes, the position of the NUL terminator and any write past the
// window are then all observable, which a zero-filled buffer would hide.
// Empty hex is `-`, like the other oracles. `desc_hex` carries the bytes *past* an embedded
// NUL on purpose: `%s` must stop at the NUL on both sides.

#define FE_FILL 0xcd
#define FE_CAP 512

typedef struct {
	uint32_t line;
	const char *fn;
	size_t fn_len;
	const char *ds;
	size_t ds_len;
} fe_frame;

static size_t g_fe_ix;

static void fe_hex_or_dash(const char *s, size_t n)
{
	if (n == 0) {
		printf("-");
		return;
	}
	emit_hex(s, n);
}

// `len == SIZE_MAX` means strlen(str), so plain literals do not need a hand-counted length.
static size_t fe_len(const char *str, size_t len)
{
	return len == SIZE_MAX ? strlen(str) : len;
}

static void fe_probe_span(int dst_null, size_t dst_size, int err_null,
	const char *desc, size_t desc_len, int desc_null,
	const char *info, size_t info_len, size_t info_span,
	uint32_t stack_size, const fe_frame *frames)
{
	ufbx_error err;
	memset(&err, 0, sizeof(err));
	if (!err_null) {
		err.description.data = desc_null ? NULL : desc;
		err.description.length = desc_null ? 0 : fe_len(desc, desc_len);
		err.info_length = info_len;
		if (info_span > 0) {
			if (info_span > UFBX_ERROR_INFO_LENGTH) {
				printf("FE BAD info_span=%zu\n", info_span);
				return;
			}
			memcpy(err.info, info, info_span);
		}
		err.stack_size = stack_size;
		size_t n = stack_size < UFBX_ERROR_STACK_MAX_DEPTH ? stack_size : UFBX_ERROR_STACK_MAX_DEPTH;
		for (size_t i = 0; i < n; i++) {
			err.stack[i].source_line = frames[i].line;
			err.stack[i].function.data = frames[i].fn;
			err.stack[i].function.length = fe_len(frames[i].fn, frames[i].fn_len);
			err.stack[i].description.data = frames[i].ds;
			err.stack[i].description.length = fe_len(frames[i].ds, frames[i].ds_len);
		}
	}

	char buf[FE_CAP];
	memset(buf, FE_FILL, sizeof(buf));

	size_t ret = ufbx_format_error(dst_null ? NULL : buf, dst_size, err_null ? NULL : &err);

	size_t dump = dst_size < 1 ? 1 : dst_size;
	if (dump > FE_CAP) dump = FE_CAP;
	size_t frames_out = stack_size < UFBX_ERROR_STACK_MAX_DEPTH ? stack_size : UFBX_ERROR_STACK_MAX_DEPTH;
	if (err_null) frames_out = 0;

	printf("FE %zu %d %zu %d %d ", g_fe_ix++, dst_null, dst_size, err_null, desc_null);
	fe_hex_or_dash(desc, desc_null || err_null ? 0 : fe_len(desc, desc_len));
	printf(" %zu ", err_null ? 0 : info_len);
	fe_hex_or_dash(info, err_null ? 0 : info_span);
	printf(" %u", err_null ? 0 : stack_size);
	for (size_t i = 0; i < frames_out; i++) {
		printf(" %u ", frames[i].line);
		fe_hex_or_dash(frames[i].fn, fe_len(frames[i].fn, frames[i].fn_len));
		printf(" ");
		fe_hex_or_dash(frames[i].ds, fe_len(frames[i].ds, frames[i].ds_len));
	}
	printf(" %zu ", ret);
	fe_hex_or_dash(buf, dst_null ? 0 : dump);
	printf("\n");
}

// The common case: `info` holds exactly `info_length` bytes, the C struct's own invariant.
static void fe_probe(int dst_null, size_t dst_size, int err_null,
	const char *desc, size_t desc_len, int desc_null,
	const char *info, size_t info_len,
	uint32_t stack_size, const fe_frame *frames)
{
	fe_probe_span(dst_null, dst_size, err_null, desc, desc_len, desc_null,
		info, info_len, info_len, stack_size, frames);
}

static const char *g_push[NUM_MODES][NUM_CASES];
static size_t g_push_len[NUM_MODES][NUM_CASES];
static const char *g_raw[NUM_CASES];
static const char *g_utf8[NUM_CASES];
static uint32_t g_raw_len[NUM_CASES];
static uint32_t g_utf8_len[NUM_CASES];

// Pointer identity/order is only comparable *within one pool*: `ufbxi_buf` gets its chunks
// from the shared allocator, and the CRT heap serves a request above its small-allocation
// threshold from a different (higher) segment, so a later pool's first chunk can land below
// an earlier pool's second chunk. Measured with `-DORACLE_ADDR` (see `dump_chunks`): the `S`
// pool's 8128B chunk out-ranks the `K` pool's 4032B chunk that was allocated after it.
// So every captured pointer carries the id of the pool it came from, and ranks are computed
// among same-pool pointers only (C: address order within one chunk chain).
enum {
	GRP_P0, GRP_P1, GRP_P2, GRP_P3, GRP_P4,
	GRP_SANITIZED,
	GRP_NAMES,
	NUM_GRPS
};

static uintptr_t g_ptrs[NUM_CASES * 12];
static int g_grps[NUM_CASES * 12];
static int g_num_ptrs;

// Statics (.rodata in this translation unit) are not orderable against the arena:
// `ufbxi_empty_char` and the `ufbxi_strings[]` constant data.
static bool is_static_ptr(const void *p)
{
	if (p == (const void*)&ufbxi_empty_char) return true;
	for (size_t i = 0; i < ufbxi_arraycount(ufbxi_strings); i++) {
		if (ufbxi_strings[i].data == p) return true;
	}
	return false;
}

// The interned pointers handed out by the `K` pass, kept for rank reporting.
static const char *g_name[16];
static int g_name_index[16];
static int g_name_matched[16];
static size_t g_name_len[16];
static int g_num_names;
static char g_name_scratch[128];

static void test_name(ufbxi_string_pool *pool, int index, const char *name, size_t n);

#ifdef ORACLE_ADDR
// Walk a pool's chunk list in allocation order (`root` then `next`): used to check whether
// C's address order across passes really is the passes' allocation order.
static void dump_chunks(const ufbxi_string_pool *pool)
{
	ufbxi_buf_chunk *c = pool->buf.chunks[0];
	if (c) c = c->root;
	for (; c; c = c->next) {
		fprintf(stderr, "  chunk %p size=%zu used=%zu\n", (void*)c->data, c->size, c->pushed_pos);
	}
}
#endif

static void collect_ptrs(void)
{
	g_num_ptrs = 0;
	for (int i = 0; i < NUM_CASES; i++) {
		for (int m = 0; m < NUM_MODES; m++) {
			if (g_push[m][i] && !is_static_ptr(g_push[m][i])) {
				g_ptrs[g_num_ptrs] = (uintptr_t)g_push[m][i];
				g_grps[g_num_ptrs] = GRP_P0 + m;
				g_num_ptrs++;
			}
		}
		if (g_raw[i] && !is_static_ptr(g_raw[i])) {
			g_ptrs[g_num_ptrs] = (uintptr_t)g_raw[i];
			g_grps[g_num_ptrs] = GRP_SANITIZED;
			g_num_ptrs++;
		}
		if (g_utf8[i] && !is_static_ptr(g_utf8[i])) {
			g_ptrs[g_num_ptrs] = (uintptr_t)g_utf8[i];
			g_grps[g_num_ptrs] = GRP_SANITIZED;
			g_num_ptrs++;
		}
	}
	for (int i = 0; i < g_num_names; i++) {
		if (g_name[i] && !is_static_ptr(g_name[i])) {
			g_ptrs[g_num_ptrs] = (uintptr_t)g_name[i];
			g_grps[g_num_ptrs] = GRP_NAMES;
			g_num_ptrs++;
		}
	}
	ufbx_assert(g_num_ptrs < (int)ufbxi_arraycount(g_ptrs));
}

static int rank_of(const void *p, int grp)
{
	if (is_static_ptr(p)) return -2;
	uintptr_t x = (uintptr_t)p;
	int rank = 0;
	for (int i = 0; i < g_num_ptrs; i++) {
		if (g_grps[i] == grp && g_ptrs[i] < x) rank++;
	}
	return rank;
}

static ufbx_error g_error;
static ufbxi_allocator g_ator;
static ufbxi_buf g_warn_result;
static ufbxi_warnings g_warnings;
static bool g_ator_ready;

static void setup_pool(ufbxi_string_pool *pool, ufbx_unicode_error_handling handling)
{
	memset(pool, 0, sizeof(*pool));
	if (!g_ator_ready) {
		ufbxi_init_ator(&g_error, &g_ator, NULL, "oracle");
		g_ator_ready = true;
	}
	memset(&g_warnings, 0, sizeof(g_warnings));
	g_warnings.error = &g_error;
	g_warnings.result = &g_warn_result;
	g_warn_result.ator = &g_ator;
	g_warnings.tmp_stack.ator = &g_ator;
	pool->error = &g_error;
	pool->buf.ator = &g_ator;
	pool->buf.unordered = true;
	ufbxi_map_init(&pool->map, &g_ator, &ufbxi_map_cmp_string, NULL);
	pool->initial_size = 64;
	pool->error_handling = handling;
	pool->warnings = &g_warnings;
}

// The port's default (ufbx.h:4474) is REPLACEMENT_CHARACTER; the other modes are
// the sanitizer's alternative outputs, all of which end up in scene strings.
static const ufbx_unicode_error_handling g_handling[NUM_MODES] = {
	UFBX_UNICODE_ERROR_HANDLING_REPLACEMENT_CHARACTER,
	UFBX_UNICODE_ERROR_HANDLING_UNDERSCORE,
	UFBX_UNICODE_ERROR_HANDLING_QUESTION_MARK,
	UFBX_UNICODE_ERROR_HANDLING_REMOVE,
	UFBX_UNICODE_ERROR_HANDLING_UNSAFE_IGNORE,
};

static void test_name(ufbxi_string_pool *pool, int index, const char *name, size_t n)
{
	if (n == 0 || n >= sizeof(g_name_scratch)) return;
	memcpy(g_name_scratch, name, n);
	const char *d = g_name_scratch;
	size_t out_length = n;
	if (!ufbxi_push_string_place(pool, &d, &out_length, true)) {
		printf("E4 %d %s\n", index, g_error.description.data);
		memset(&g_error, 0, sizeof(g_error));
		return;
	}
	int matched = 0;
	if (index >= 0) {
		matched = d == ufbxi_strings[index].data && out_length == ufbxi_strings[index].length;
	}
	g_name[g_num_names] = d;
	g_name_index[g_num_names] = index;
	g_name_matched[g_num_names] = matched;
	g_name_len[g_num_names] = out_length;
	g_num_names++;
	ufbx_assert(g_num_names < (int)ufbxi_arraycount(g_name));
}

int main(void)
{
	setvbuf(stdout, NULL, _IONBF, 0);
	gen_corpus();

	for (int i = 0; i < NUM_CASES; i++) {
		const char *s = g_buf + g_off[i];
		size_t n = g_len[i];
		printf("H %d %08x\n", i, ufbxi_hash_string(s, n));
		if (n > 0) {
			bool non_ascii = false;
			uint32_t h = ufbxi_hash_string_check_ascii(s, n, &non_ascii);
			printf("A %d %08x %d\n", i, h, (int)non_ascii);
			printf("V %d %zu\n", i, ufbxi_utf8_valid_length(s, n));
			ufbx_string part = { s, n };
			printf("C %d %08x\n", i, ufbxi_get_concat_key(&part, 1));
		}
	}

	// mode 0 interns with `raw` (no sanitizing), the rest sanitize
	for (int mode = 0; mode < NUM_MODES; mode++) {
		ufbxi_string_pool pool;
		setup_pool(&pool, g_handling[mode]);
		for (int i = 0; i < NUM_CASES; i++) {
			// `ufbxi_push_string_place` keeps `length` valid on both the plain and the
			// sanitized path (`ufbxi_push_string` only writes it when sanitizing).
			const char *d = g_buf + g_off[i];
			size_t out_length = g_len[i];
			if (!ufbxi_push_string_place(&pool, &d, &out_length, mode == 0)) {
				printf("E %d %d %s\n", mode, i, g_error.description.data);
				memset(&g_error, 0, sizeof(g_error));
				d = NULL;
				out_length = 0;
			}
			g_push[mode][i] = d;
			g_push_len[mode][i] = out_length;
		}
#ifdef ORACLE_ADDR
		fprintf(stderr, "CHUNKS P %d\n", mode);
		dump_chunks(&pool);
#endif
	}

	{
		ufbxi_string_pool pool;
		setup_pool(&pool, UFBX_UNICODE_ERROR_HANDLING_REPLACEMENT_CHARACTER);
		for (int i = 0; i < NUM_CASES; i++) {
			const char *s = g_buf + g_off[i];
			size_t n = g_len[i];
			if (n == 0) continue;
			ufbxi_sanitized_string str;
			memset(&str, 0, sizeof(str));
			bool non_ascii = false;
			uint32_t hash = ufbxi_hash_string_check_ascii(s, n, &non_ascii);
			if (!ufbxi_push_sanitized_string(&pool, &str, s, n, hash, non_ascii, false)) {
				printf("E2 %d %s\n", i, g_error.description.data);
				memset(&g_error, 0, sizeof(g_error));
				continue;
			}
			g_raw[i] = str.raw_data;
			g_utf8[i] = str.utf8_data;
			g_raw_len[i] = str.raw_length;
			g_utf8_len[i] = str.utf8_length;
		}
#ifdef ORACLE_ADDR
		fprintf(stderr, "CHUNKS S\n");
		dump_chunks(&pool);
#endif
	}

	// The mechanism the loader depends on: ufbxi_load_strings() (ufbx.c:11407-11422)
	// interns the `ufbxi_*` constants into the scene pool *without copying*, so that
	// interning a heap copy of the same bytes later hands back the constant pointer and
	// name comparisons can be done by address.
	{
		ufbxi_string_pool pool;
		setup_pool(&pool, UFBX_UNICODE_ERROR_HANDLING_REPLACEMENT_CHARACTER);

		uint64_t digest = 0xcbf29ce484222325ull;
		for (size_t i = 0; i < ufbxi_arraycount(ufbxi_strings); i++) {
			const ufbx_string *str = &ufbxi_strings[i];
			if (!ufbxi_push_string_imp(&pool, str->data, str->length, NULL, false, true)) {
				printf("E3 %zu %s\n", i, g_error.description.data);
				memset(&g_error, 0, sizeof(g_error));
			}
			// FNV-1a over the contents in declaration order, NUL separated: proves the
			// port's UfbxiStrings.All matches C's ufbxi_strings[] entry for entry.
			for (size_t j = 0; j < str->length; j++) {
				digest = (digest ^ (uint8_t)str->data[j]) * 0x00000100000001b3ull;
			}
			digest = (digest ^ 0u) * 0x00000100000001b3ull;
			printf("L %zu %zu ", i, str->length);
			emit_hex(str->data, str->length);
			printf("\n");
		}
		printf("F %zu %016llx\n", ufbxi_arraycount(ufbxi_strings), (unsigned long long)digest);

		for (size_t i = 0; i + 41 < ufbxi_arraycount(ufbxi_strings); i += 41) {
			test_name(&pool, (int)i, ufbxi_strings[i].data, ufbxi_strings[i].length);
		}
		{
			size_t last = ufbxi_arraycount(ufbxi_strings) - 1;
			test_name(&pool, (int)last, ufbxi_strings[last].data, ufbxi_strings[last].length);
		}
		test_name(&pool, -1, "ZqNotARealFbxName", 17);
		test_name(&pool, -2, "GeometryX", 9);
		// Deliberately 7 bytes: the trailing NUL is named data, not a terminator (the
		// sanitizer must not treat an embedded NUL as the end of the string).
		test_name(&pool, -3, "\xc3\xa9Name\0", 7);
		test_name(&pool, -4, "a", 1);
#ifdef ORACLE_ADDR
		fprintf(stderr, "CHUNKS K\n");
		dump_chunks(&pool);
#endif
	}

	collect_ptrs();

	for (int mode = 0; mode < NUM_MODES; mode++) {
		for (int i = 0; i < NUM_CASES; i++) {
			const char *d = g_push[mode][i];
			size_t n = g_push_len[mode][i];
			printf("P %d %d %d %zu ", mode, i, d ? rank_of(d, GRP_P0 + mode) : -1, n);
			if (n > g_len[i] * 3u + 8u) {
				printf("BAD src=%zu\n", g_len[i]);
				continue;
			}
			emit_hex(d, n);
			printf("\n");
		}
	}

	for (int i = 0; i < NUM_CASES; i++) {
		if (!g_raw[i]) continue;
		printf("S %d %d %u ", i, rank_of(g_raw[i], GRP_SANITIZED), g_raw_len[i]);
		printf("%d %u ", g_utf8[i] ? rank_of(g_utf8[i], GRP_SANITIZED) : -1, g_utf8_len[i]);
		emit_hex(g_raw[i], g_raw_len[i]);
		printf(" ");
		if (g_utf8[i]) emit_hex(g_utf8[i], g_utf8_len[i]);
		printf("\n");
	}

	for (int i = 0; i < g_num_names; i++) {
		printf("K %d %d %d %zu\n", g_name_index[i], g_name_matched[i], rank_of(g_name[i], GRP_NAMES), g_name_len[i]);
	}

	// -- FE: ufbx_format_error (see the block comment above the helpers)
	{
		static const fe_frame fr_one[] = {
			{ 1u, "ufbxi_foo", SIZE_MAX, "bar", SIZE_MAX },
		};
		static const fe_frame fr_w[] = {
			{ 0u, "a", SIZE_MAX, "b", SIZE_MAX },
			{ 1u, "ufbxi_cache_load_imp", SIZE_MAX, "File not found", SIZE_MAX },
			{ 42u, "ufbxi_parse_binary_node", SIZE_MAX, "Expected node end", SIZE_MAX },
			{ 123456u, "f", SIZE_MAX, "d", SIZE_MAX },
			{ 1234567u, "ufbxi_with_a_really_long_function_name_to_widen_the_line", SIZE_MAX, "desc", SIZE_MAX },
			{ 4294967295u, "g", SIZE_MAX, "h", SIZE_MAX },
			{ 999u, "nul\0hidden", 8, "pre\0post", 4 },
			{ 100000u, "z", SIZE_MAX, "y", SIZE_MAX },
		};
		static char info_max_minus_1[255];
		static char info_max[256];
		static const char info_nul[] = "short\0longer-text-here";
		for (size_t i = 0; i < sizeof(info_max_minus_1); i++) info_max_minus_1[i] = 'Z';
		for (size_t i = 0; i < sizeof(info_max); i++) info_max[i] = 'Z';

		// The three NULL guards of the signature. `dst_size == 0` is probed with *both* error
		// shapes: the `!dst_size` arm returns before the `!error` one writes `*dst = '\0'`, so
		// only the `dst_size == 0, error == NULL` pair can tell the two guards apart.
		fe_probe(1, 64, 0, "IO error", SIZE_MAX, 0, NULL, 0, 0, NULL);
		fe_probe(0, 0, 0, "IO error", SIZE_MAX, 0, NULL, 0, 0, NULL);
		fe_probe(0, 0, 1, NULL, 0, 1, NULL, 0, 0, NULL);
		fe_probe(0, 5, 1, NULL, 0, 1, NULL, 0, 0, NULL);
		fe_probe(0, 64, 1, NULL, 0, 1, NULL, 0, 0, NULL);

		// The description: C's NULL data falls back to "Unknown error", an empty *string*
		// prints as nothing, and `%s` stops at an embedded NUL even though the record keeps
		// the bytes after it.
		fe_probe(0, 64, 0, NULL, 0, 1, NULL, 0, 0, NULL);
		fe_probe(0, 64, 0, "", 0, 0, NULL, 0, 0, NULL);
		fe_probe(0, 64, 0, "IO error", SIZE_MAX, 0, NULL, 0, 0, NULL);
		fe_probe(0, 64, 0, "Missing file\0SHOULD-NOT-APPEAR", 30, 0, NULL, 0, 0, NULL);
		fe_probe(0, 20, 0, "A very long description that exceeds the buffer we pass in to exercise truncation", SIZE_MAX, 0, NULL, 0, 0, NULL);

		// The `0 < info_length < UFBX_ERROR_INFO_LENGTH` arm, including both boundaries and
		// a precision that runs past an embedded NUL.
		fe_probe(0, 64, 0, "IO error", SIZE_MAX, 0, "x", 1, 0, NULL);
		fe_probe(0, 64, 0, "IO error", SIZE_MAX, 0, "filename.fbx", 12, 0, NULL);
		fe_probe(0, 80, 0, "IO error", SIZE_MAX, 0, info_max_minus_1, 255, 0, NULL);
		fe_probe(0, 80, 0, "IO error", SIZE_MAX, 0, info_max, 256, 0, NULL);
		// 22 array bytes behind a precision of 20: the bytes past the embedded NUL *and* the
		// ones past `info_length` are both in the record, so a port that ignores either bound
		// prints them.
		fe_probe_span(0, 64, 0, "IO error", SIZE_MAX, 0, info_nul, 20, 22, 0, NULL);
		// The `%.*s` precision really binds: `info_length` is 4 while the array still holds
		// 12 readable bytes (legal for a hand-built error, and the only probe that fails when
		// the precision argument is dropped).
		fe_probe_span(0, 64, 0, "IO error", SIZE_MAX, 0, "filename.fbx", 4, 12, 0, NULL);

		// The stack frames: `%*u` min-width (short / exact / over), the
		// `min(stack_size, UFBX_ERROR_STACK_MAX_DEPTH)` clamp, and frames past a saturated
		// window -- where every further snprintf can only rewrite the same NUL byte.
		fe_probe(0, 256, 0, "IO error", SIZE_MAX, 0, NULL, 0, 1, fr_one);
		fe_probe(0, 256, 0, "IO error", SIZE_MAX, 0, NULL, 0, 2, fr_w);
		fe_probe(0, 256, 0, "IO error", SIZE_MAX, 0, NULL, 0, 1, fr_w + 3);
		fe_probe(0, 256, 0, "IO error", SIZE_MAX, 0, NULL, 0, 1, fr_w + 4);
		fe_probe(0, 256, 0, "IO error", SIZE_MAX, 0, NULL, 0, 1, fr_w + 5);
		fe_probe(0, 256, 0, "IO error", SIZE_MAX, 0, NULL, 0, 1, fr_w + 6);
		fe_probe(0, 512, 0, "IO error", SIZE_MAX, 0, NULL, 0, 8, fr_w);
		fe_probe(0, 512, 0, "IO error", SIZE_MAX, 0, NULL, 0, 9, fr_w);
		fe_probe(0, 512, 0, "IO error", SIZE_MAX, 0, NULL, 0, 11, fr_w);
		fe_probe(0, 30, 0, "IO error", SIZE_MAX, 0, "filename.fbx", 12, 4, fr_w);
		fe_probe(0, 512, 0, "IO error", SIZE_MAX, 0, "filename.fbx", 12, 8, fr_w);

		// The dst_size ladder around each arm's exact length (29 and 44 bytes): proves the
		// `offset = min(offset + num, dst_size - 1)` saturation and the NUL placement.
		for (size_t sz = 1; sz <= 33; sz++) {
			fe_probe(0, sz, 0, "IO error", SIZE_MAX, 0, NULL, 0, 0, NULL);
		}
		for (size_t sz = 40; sz <= 48; sz++) {
			fe_probe(0, sz, 0, "IO error", SIZE_MAX, 0, "filename.fbx", 12, 0, NULL);
		}
	}

#ifdef ORACLE_ADDR
	// Address dump to stderr (stdout records unchanged): used to investigate whether C's
	// address order really is the passes' allocation order.
	for (int mode = 0; mode < NUM_MODES; mode++) {
		for (int i = 0; i < NUM_CASES; i++) {
			if (g_push[mode][i]) fprintf(stderr, "A P %d %d %p\n", mode, i, (void*)g_push[mode][i]);
		}
	}
	for (int i = 0; i < NUM_CASES; i++) {
		fprintf(stderr, "A S %d raw %p utf8 %p\n", i, (void*)g_raw[i], (void*)g_utf8[i]);
	}
	for (int i = 0; i < g_num_names; i++) {
		fprintf(stderr, "A K %d %p\n", i, (void*)g_name[i]);
	}
#endif

	return 0;
}
