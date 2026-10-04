// C reference oracle for the ported ASCII FBX tokenizer / state machine
// (src/Ufbx/Parse/Ascii.cs + AsciiState.cs, ufbx.c:9400-10220).
//
// Included as a single translation unit with ufbx.c so the `static ufbxi_*` ASCII internals
// are callable directly: ufbxi_ascii_next_token / refill / yield / peek / next / skip_whitespace
// / parse_version run on a real ufbxi_context, exactly the windowing the C# port drives through
// the genuine UfbxiStream.
//
//   zig cc -O2 -std=c11 -DUFBX_ENABLE_ERROR_STACK -I C:/Workspace/_analyze_ufbx \
//       tools/ascii_oracle.c -o tools/ascii_oracle.exe
//
// Output grammar (one record per line, space-separated; every byte-bearing field is hex so
// NULs / CRLFs / non-ASCII survive the text file):
//   S <snip> <len> <hexbytes>                    the input bytes for a snippet (the C# side
//                                                replays exactly these, so snippets are defined
//                                                once, here)
//   SIG <snip> <mode> <ended> <len> <hexsig>     the token signature ufbxi_ascii_next_token()
//                                                produces for that snippet under the given IO
//                                                mode (see g_modes), plus whether the stream
//                                                reached UFBXI_ASCII_END; `hexsig` is the raw
//                                                signature bytes (token texts one char per byte)
//   ERR <snip> <mode> <desc-hex> <cond-hex>      emitted right after a SIG whose tokenizer
//                                                failed: the ufbx_error description (set only by
//                                                *_msg checks) and the innermost error-stack
//                                                frame's condition string (set by ufbxi_check).
//
// The C# harness compares its UfbxParseError.Message against `desc` when desc is non-empty
// (ufbxi_check_msg / check_return_msg -> the port throws the human message) and against `cond`
// otherwise (plain ufbxi_check -> the port throws the stringified condition). That mirrors
// PORTING_NOTES.md #3 exactly.
//
// IO modes (g_modes): how the window is seeded and whether ufbxi_ascii_refill() reads:
//   0  memory load      read_fn=NULL, the whole input is the initial window (ufbx_load_memory).
//                       The ASCII "everything past the buffer is EOF" quirk (ufbx.c:9445).
//   1  stream 16KB      read_fn streams, opts.read_buffer_size=0x4000, progress_interval=0x4000.
//   2  stream 7B        tiny buffer + interval 7 -> refills and interval-sized yields per token.
//   3  stream 64B       buffer 64, interval 3.
//   4  stream short     buffer 8 but the reader hands back 3 bytes at a time (partial reads,
//                       the "Very unoptimal for non-full-size reads" path, ufbx.c:9435).

#include "ufbx.c"

typedef struct {
	const char *hex;
} snippet_t;

// Every snippet is given as raw bytes in hex so the C and C# sides feed byte-identical input
// (NULs, CR, high bytes included).
static const char *g_snips[] = {
	// 0: header magic + Blender exporter line + FBXVersion (version 7700)
	"3b2046425820372e372e302070726f6a6563742066696c650a"
	"3b204372656174656420627920426c656e64657220322e34380a"
	"464258486561646572457874656e73696f6e3a20207b0a"
	"0946425856657273696f6e3a20373730300a7d0a",

	// 1: line comments interleaved with tokens, blank lines
	"3b20630a413a20310a3b20640a20200a423a20320a",

	// 2: nested braces, bare node list, '*' array marker
	"4e6f64653a207b0a433a207b0a443a202a33207b0a09613a20312c322c330a7d0a7d0a7d0a",

	// 3: P: property list with all scalar value types
	"503a20224e616d65222c202254797065222c2022222c20370a",

	// 4: typed array prefix tokens int[] / double[] / KTime[] / bool[] / char[]
	"696e745b5d3a20310a646f75626c655b5d3a20310a"
	"4b54696d655b5d3a20310a626f6f6c5b5d3a20310a636861725b5d3a2022726177220a",

	// 5: 64-bit ids at the extremes (int64 max/min, +5)
	"583a20393232333337323033363835343737353830372c202d393232333337323033363835343737353830382c202b350a",

	// 6: float dialect: inf/nan/-0/exponent forms
	"563a20312e3565332c202d302c20312e23494e462c20302e312c202d6e616e28696e64292c202d312e23494e440a",

	// 7: *-terminated array, count then closing brace
	"573a202a34207b0a09613a2031302c32302c33302c34300a7d0a",

	// 8: string escapes: &quot; &cr; &lf; &am; lone &
	"453a2022612671756f743b622663723b63266c663b6426616d3b65266626220a",

	// 9: quoted name with spaces -> NAME token
	"225472616e73706f727420546f6f6c2053657474696e6773223a207b207d0a",

	// 10: CRLF line endings throughout
	"3b2046425820372e352e300d0a413a20310d0a423a20320d0a",

	// 11: LF vs CRLF mixed + tabs
	"413a20310d0a0909423a20320a09433a20330a",

	// 12: truncated unterminated string (error: c != '\0')
	"583a2022616263",

	// 13: non-ASCII bytes in a string (latin1) and in a name
	"4e616d65c3a93a202276616c80c3c3220a",

	// 14: overlong integer (rejected by ufbxi_parse_int64 end check)
	"583a203132333435363738393031323334353637383930313233343536373839300a",

	// 15: 1.#QNAN (rejected: ufbxi_parse_inf_nan only knows inf/nan/ind)
	"583a20312e23514e414e0a",

	// 16: repeated-array, many values to force refills with small buffers
	"523a202a3634207b0a09613a20",
	// (body appended below at runtime: 64 ints "1,2,...,64")

	// 17: NUL byte embedded inside a bare-word token sequence
	"413a203100423a20320a",

	// 18: empty property list, stray tokens, colon-only
	"3a0a7b0a7d0a2c0a",

	// 19: bool-ish and char array values, negative + plus signs
	"623a202a34207b0a09613a20312c302c2d312c2b310a7d0a",
};

static const size_t g_num_snips = sizeof(g_snips) / sizeof(g_snips[0]);

// IO modes.
typedef struct {
	bool streaming;       // false = memory load (read_fn == NULL)
	size_t read_buffer_size;
	size_t progress_interval;
	size_t chunk_limit;   // 0 = full reads; else max bytes handed back per read_fn call
} mode_t;

static const mode_t g_modes[] = {
	{ false, 0x4000, (size_t)-1, 0 },
	{ true,  0x4000, 0x4000, 0 },
	{ true,  7,      7,      0 },
	{ true,  64,     3,      0 },
	{ true,  8,      0x4000, 3 },
};
static const size_t g_num_modes = sizeof(g_modes) / sizeof(g_modes[0]);

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
		if (hi < 0 || lo < 0) break;
		if (n == cap) break;
		dst[n++] = (char)((hi << 4) | lo);
	}
	return n;
}

static void emit_hex(const char *s, size_t n)
{
	for (size_t j = 0; j < n; j++) printf("%02x", (uint8_t)s[j]);
}

// The snippet's decoded bytes (the 64-int array is generated so all sides agree on the body).
static char g_data[32][4096];
static size_t g_len[32];

static void build_data(void)
{
	for (size_t i = 0; i < g_num_snips; i++) {
		g_len[i] = hex_decode(g_snips[i], g_data[i], sizeof(g_data[i]));
	}
	// Snippet 16: append ",v" separated 1..64 then close the array.
	{
		char *p = g_data[16] + g_len[16];
		char *end = g_data[16] + sizeof(g_data[16]) - 1;
		for (int v = 1; v <= 64 && p < end; v++) {
			int n = snprintf(p, (size_t)(end - p), "%s%d%s", v == 1 ? "" : ",", v, v == 64 ? "\n\t}\n" : "");
			if (n <= 0) break;
			p += n;
		}
		g_len[16] = (size_t)(p - g_data[16]);
	}
}

// Streaming read_fn user: an in-memory source at `pos`, capped to `chunk` per call.
typedef struct {
	const char *data;
	size_t size;
	size_t pos;
	size_t chunk;
} reader_t;

static size_t reader_read(void *user, void *buffer, size_t size)
{
	reader_t *r = (reader_t*)user;
	size_t left = r->size - r->pos;
	size_t n = size < left ? size : left;
	if (r->chunk != 0 && n > r->chunk) n = r->chunk;
	if (n > 0) memcpy(buffer, r->data + r->pos, n);
	r->pos += n;
	return n;
}

// Build the token signature the way the C# harness does, appending to `sig`.
static void sig_token(ufbxi_ascii_token *tok, char *sig, size_t cap, size_t *n)
{
	#define PUT(c) do { if (*n + 1 < cap) sig[(*n)++] = (char)(c); } while (0)
	#define PUTS(s, len) do { for (size_t _i = 0; _i < (len); _i++) PUT((s)[_i]); } while (0)
	PUT(tok->type);
	switch (tok->type) {
	case UFBXI_ASCII_NAME: {
		PUT('/'); PUTS(tok->str_data, tok->value.name_len);
		PUT('/');
		char tmp[32]; int tn = snprintf(tmp, sizeof(tmp), "%llu", (unsigned long long)tok->value.name_len);
		PUTS(tmp, (size_t)tn);
		break;
	}
	case UFBXI_ASCII_BARE_WORD:
	case UFBXI_ASCII_STRING:
		PUT('/'); PUTS(tok->str_data, tok->str_len);
		break;
	case UFBXI_ASCII_INT: {
		PUT('/'); PUT(tok->negative ? '-' : '+'); PUT('/');
		char tmp[32]; int tn = snprintf(tmp, sizeof(tmp), "%lld", (long long)tok->value.i64);
		PUTS(tmp, (size_t)tn);
		break;
	}
	case UFBXI_ASCII_FLOAT: {
		PUT('/');
		uint64_t bits; memcpy(&bits, &tok->value.f64, 8);
		char tmp[24]; int tn = snprintf(tmp, sizeof(tmp), "%016llx", (unsigned long long)bits);
		PUTS(tmp, (size_t)tn);
		break;
	}
	default: break;
	}
	PUT(' ');
	#undef PUTS
	#undef PUT
}

static void run_case(size_t snip, size_t mi, const mode_t *m)
{
	ufbxi_context uc;
	memset(&uc, 0, sizeof(uc));
	ufbxi_init_ator(&uc.error, &uc.ator_tmp, NULL, "ascii-oracle");
	uc.double_parse_flags = ufbxi_parse_double_init_flags();
	uc.opts.read_buffer_size = m->read_buffer_size;
	uc.progress_interval = m->progress_interval;

	const char *data = g_data[snip];
	size_t len = g_len[snip];

	static char g_readbuf[8192];
	reader_t reader = { data, len, 0, m->chunk_limit };

	size_t first;
	if (m->streaming) {
		uc.read_fn = &reader_read;
		uc.read_user = &reader;
		first = len < m->read_buffer_size ? len : m->read_buffer_size;
		if (first > 0) memcpy(g_readbuf, data, first);
		reader.pos = first;
		uc.read_buffer = g_readbuf;
		uc.read_buffer_size = m->read_buffer_size;
		uc.data = uc.data_begin = g_readbuf;
		uc.data_size = first;
		uc.yield_size = 0;
	} else {
		uc.read_fn = NULL;
		uc.data = uc.data_begin = data;
		uc.data_size = len;
		uc.yield_size = 0;
	}

	// C: ufbxi_begin_parse()'s ASCII seeding (ufbx.c:11218-11221).
	memset(&uc.ascii, 0, sizeof(uc.ascii));
	uc.ascii.src = uc.data;
	uc.ascii.src_yield = uc.data + uc.yield_size;
	uc.ascii.src_end = uc.data + uc.data_size + uc.yield_size;

	static char sig[16384];
	size_t sig_len = 0;

	bool ended = false;
	int rc = ufbxi_ascii_next_token(&uc, &uc.ascii.token);
	for (int i = 0; rc && i < 8192; i++) {
		char type = uc.ascii.token.type;
		if (type == UFBXI_ASCII_END) { ended = true; break; }
		sig_token(&uc.ascii.token, sig, sizeof(sig), &sig_len);
		rc = ufbxi_ascii_next_token(&uc, &uc.ascii.token);
	}

	printf("SIG %zu %zu %d %zu ", snip, mi, ended ? 1 : 0, sig_len);
	emit_hex(sig, sig_len);
	printf("\n");
	if (!rc) {
		const char *desc = uc.error.description.data ? uc.error.description.data : "";
		size_t desc_len = uc.error.description.length;
		const char *cond = "";
		size_t cond_len = 0;
		if (uc.error.stack_size > 0) {
			cond = uc.error.stack[uc.error.stack_size - 1].description.data;
			cond_len = uc.error.stack[uc.error.stack_size - 1].description.length;
		}
		printf("ERR %zu %zu ", snip, mi);
		emit_hex(desc, desc_len);
		printf(" ");
		emit_hex(cond, cond_len);
		printf("\n");
	}
}

int main(void)
{
	setvbuf(stdout, NULL, _IONBF, 0);
	build_data();

	for (size_t i = 0; i < g_num_snips; i++) {
		printf("S %zu %zu ", i, g_len[i]);
		emit_hex(g_data[i], g_len[i]);
		printf("\n");
	}

	for (size_t i = 0; i < g_num_snips; i++) {
		for (size_t m = 0; m < g_num_modes; m++) {
			run_case(i, m, &g_modes[m]);
		}
	}

	return 0;
}
