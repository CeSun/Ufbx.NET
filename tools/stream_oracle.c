// Batch L oracle: the stream / stdio / open ABI family.
//
// Built with `#include "ufbx.c"`, so it runs the original
//   ufbxi_file_context / ufbxi_begin_file_context() / ufbxi_end_file_context()  (ufbx.c:6941-6975)
//   ufbxi_fopen() (the `_WIN32` branch)                                        (ufbx.c:6981-7065)
//   ufbxi_stdio_read/skip/size/close + ufbxi_stdio_init()/stdio_open()         (ufbx.c:7082-7141)
//   ufbxi_memory_read/skip/size/close                                          (ufbx.c:7186-7237)
//   ufbx_default_open_file() / ufbx_open_file()/_ctx() / ufbx_open_memory()/_ctx()  (30414-30503)
//   ufbx_load_stream()/_prefix() / ufbx_load_stdio()/_prefix()                 (30536-30584)
// plus the deferred open of `ufbxi_load_imp()` (ufbx.c:25216-25252) through `ufbx_load_file()`.
// The C# twin is src/Ufbx/Parse/StreamOpen.cs + src/Ufbx/Parse/InputStreams.cs +
// src/Ufbx/Api/UfbxApi.cs, replayed by tools/StreamCheck.
//
// DESIGN: the variant table lives *only* here. Every variant re-emits what it handed to the ABI as
// `I`/`Ip`/`Id`/`S` records, so the harness rebuilds the same payload, path, prefix and options
// from the records instead of mirroring `k_variants[]`, `build_payload()` or `path_bytes()` in a
// second language. Same pattern as bake's `T` records and create_anim's `I`-family.
//
// Two kinds of stream feed each variant:
//   REAL   -- the stream is ufbx's own: `ufbx_open_memory()` / `ufbx_open_file()`, so the port's
//             UfbxMemoryInputStream / UfbxFileInputStream (Parse/InputStreams.cs) are what the
//             loader talks to. The harness must use `UfbxApi.OpenMemory()` / `OpenFile()` too.
//   SCRIPT -- the stream is the rule-based `script_stream` below, replayed verbatim by the port,
//             so the loader's IO *timing* and its error paths ("IO error" / "Truncated file" /
//             "Empty file" / the two `ufbxi_skip_bytes()` branches) are pinned independently of
//             either side's built-in stream.
// Either way the stream is wrapped by `wrap_stream`, which forwards to the inner stream and logs
// every callback; `ufbx_load_stdio()` is the one exception (no `ufbx_stream` to wrap), so it emits
// `-1 -` for `H` and `-1`s for `R`.
//
// Grammar (space separated, one record per line; `<z>` = 16 hex digits of an FNV-1a-64, `<x>` =
// lowercase hex of raw bytes, an empty byte run is the single token `-`):
//   S  <fi> <vi> <kind> <arg> <len> <z>        the payload bytes; <z> is their FNV, so a mismatch
//                                              here means the two sides disagree about the INPUT
//   I  <fi> <vi> <mode> <pl> <plArg> <plLen> <prefix> <optsNull> <noCopy> <closeCb> <ctx>
//      <mutate> <script> <dirty> <errNull> <path> <pathLen> <nulTerm> <progress> <readBuf>
//      <cbFail> <defaultCb> <withDefault>
//                                              <pathLen> is the RESOLVED byte length handed to the
//                                              open ABI, -1 for C's SIZE_MAX (strlen inside)
//                                              the variant, resolved; INPUT ONLY
//   Ip <fi> <vi> <len> <x>                     the path bytes (`-` when the variant has no path)
//   Id <fi> <vi> <len> <x>                     the prefix bytes (`-` when there is no prefix)
//   A  <fi> <vi> <ok> <wired> <type> <dlen> <dx> <ilen> <ix>
//                                              the ABI call's result: NULL/non-NULL, whether the
//                                              out `ufbx_stream` got its `read_fn` wired (`-`
//                                              where the ABI has no out-stream), and the *bytes* of
//                                              the reported error -- which is what separates
//                                              "Invalid UTF-8" (no info payload, ufbx.c:7019) from
//                                              "File not found" (info = the path, 7060-7062), and
//                                              both from the "Failed to open file" /
//                                              "Failed to load" defaults `ufbxi_fix_error_type()`
//                                              substitutes (6960-6975, 25622)
//   C  <fi> <vi> <type> <dlen> <dx> <ilen> <ix> | `-`
//                                              the CALLER's own `ufbx_error` after the call: a
//                                              successful `ufbx_open_file()` clears it (6971) and
//                                              `ufbx_load_stdio(NULL)` does not touch it at all
//                                              (30544) -- the two asymmetries this batch is about
//   K  <fi> <vi> <cursor>                      the FILE*/FileStream position after the load; only
//                                              the stdio variants have one (`close_fn == NULL`,
//                                              30546). -1 = not applicable.
//   M  <fi> <vi> <nclose> <dlen> <dx> <size>   what `ufbx_close_memory_cb` received: with
//                                              `no_copy` it is the CALLER's buffer (so a later
//                                              write is visible), otherwise the internal copy
//   F  <fi> <vi> <pathLen> <len> <x> <type> <origLen> <ox>
//                                              what a custom `open_file_cb` received -- the
//                                              RESOLVED `path_len` (ufbx.c:25239, the bug this
//                                              batch fixes), `info->type` and
//                                              `info->original_filename`
//   R  <fi> <vi> <nread> <nskip> <nsize> <nclose>   callback counts by kind
//   P  <fi> <vi> <ix> <bytesRead> <bytesTotal>     `ufbx_progress_cb` invocation ix
//   H  <fi> <vi> <calls> <z>                   IO callback log: count and FNV over
//                                              (kind, arg, ret) triples
//   Q  <fi> <vi> <ix> <kind> <arg> <ret>       the same log in full, only when calls <= 64
//                                              (kind: 0 read, 1 skip, 2 size, 3 close)
//   V  <fi> <vi> <z>                           end-to-end: the loaded scene hashed by the golden
//                                              generator's own ufbxt_hash_scene()
//
// Usage (run from C:/Workspace/_analyze_ufbx so the `data/...` paths resolve):
//   stream_oracle.exe (<file>)... > stream_oracle.txt
// Build (mandatory flags, see PORTING_NOTES "浮点语义"; UFBX_EXTERNAL_MATH is #defined below so a
// rebuild cannot forget it):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx \
//       C:/Workspace/ufbx-cs/tools/stream_oracle.c \
//       C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o C:/Workspace/ufbx-cs/tools/stream_oracle.exe

#ifndef UFBX_EXTERNAL_MATH
#define UFBX_EXTERNAL_MATH
#endif
#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

// The golden generator's own scene hasher, included exactly as `test/hash_scene.c:6` does.
#include "test/hash_scene.h"

// -- Output helpers

static const uint64_t FNV_BASIS = UINT64_C(0xcbf29ce484222325);
static const uint64_t FNV_PRIME = UINT64_C(0x100000001b3);

static uint64_t hh_bytes(uint64_t h, const void *data, size_t size) {
	const uint8_t *p = (const uint8_t*)data;
	for (size_t i = 0; i < size; i++) {
		h ^= (uint64_t) p[i];
		h *= FNV_PRIME;
	}
	return h;
}

static uint64_t hh_u64(uint64_t h, uint64_t v) { return hh_bytes(h, &v, 8); }

static void pz(uint64_t v) { printf(" %016llx", (unsigned long long) v); }

// ` <length> <hex>` -- always one token for the bytes so an empty run cannot shift the columns.
static void pstr(const char *data, size_t length) {
	printf(" %zu ", length);
	if (length == 0) { printf("-"); return; }
	if (!data) { printf("null"); return; }
	for (size_t i = 0; i < length; i++) printf("%02x", (unsigned char) data[i]);
}

static void phex(const char *data, size_t length) {
	printf(" %zu ", length);
	if (length == 0) { printf("-"); return; }
	if (!data) { printf("null"); return; }
	for (size_t i = 0; i < length; i++) printf("%02x", (unsigned char) data[i]);
}

// ` <type> <dlen> <dhex> <ilen> <ihex>`
static void perr(const ufbx_error *e) {
	printf(" %d", (int) e->type);
	pstr(e->description.data, e->description.length);
	printf(" %zu ", e->info_length);
	if (e->info_length == 0) { printf("-"); }
	else { for (size_t i = 0; i < e->info_length; i++) printf("%02x", (unsigned char) e->info[i]); }
}

// -- Payload construction

enum {
	PK_WHOLE = 0,    // the whole corpus file
	PK_HEAD,         // the first <arg> bytes (a truncated file)
	PK_GARBAGE,      // a fixed 40-byte non-FBX blob
	PK_EMPTY,        // zero bytes
	PK_ZEROS,        // <arg> zero bytes
	PK_ASCII_HEAD,   // "; FBX 7.5.0 project file\n\n" -- ascii format, nothing else
};

static const char k_garbage[40] = "\x01\x02\x03 not an fbx file at all, no..";
static const char k_ascii_head[] = "; FBX 7.5.0 project file\n\n";

typedef struct { uint8_t *data; size_t size; } blob;

// Always a fresh malloc'd copy: the `mutate` variants write into the caller's buffer after the
// stream was opened, which is the whole point of `no_copy`.
static blob build_payload(int kind, int arg, const blob *whole)
{
	blob b = { 0, 0 };
	switch (kind) {
	case PK_WHOLE:
		b.size = whole->size;
		b.data = (uint8_t*) malloc(b.size + 1);
		memcpy(b.data, whole->data, b.size);
		break;
	case PK_HEAD:
		b.size = (size_t) arg < whole->size ? (size_t) arg : whole->size / 2;
		b.data = (uint8_t*) malloc(b.size + 1);
		memcpy(b.data, whole->data, b.size);
		break;
	case PK_GARBAGE:
		b.size = strlen(k_garbage);
		b.data = (uint8_t*) malloc(b.size + 1);
		memcpy(b.data, k_garbage, b.size);
		break;
	case PK_EMPTY:
		b.size = 0;
		b.data = (uint8_t*) malloc(1);
		break;
	case PK_ZEROS:
		b.size = (size_t) arg;
		b.data = (uint8_t*) malloc(b.size + 1);
		memset(b.data, 0, b.size);
		break;
	case PK_ASCII_HEAD:
		b.size = sizeof(k_ascii_head) - 1;
		b.data = (uint8_t*) malloc(b.size + 1);
		memcpy(b.data, k_ascii_head, b.size);
		break;
	default:
		b.data = (uint8_t*) malloc(1);
		break;
	}
	if (b.data) b.data[b.size] = 0;
	return b;
}

// -- Reading the corpus

// The corpus paths are UTF-8 byte strings (one of them is non-ASCII), so `fopen()` cannot be used:
// go through ufbx's own UTF-8 -> UTF-16 decoder. A failure here shows up as a payload digest the
// port does not reproduce, i.e. it is caught rather than masked.
static FILE *fopen_utf8(const char *path, size_t path_len)
{
	ufbxi_file_context fc; // ufbxi_uninit
	ufbxi_begin_file_context(&fc, (ufbx_open_file_context) NULL, NULL);
	FILE *f = ufbxi_fopen(&fc, path, path_len, true);
	ufbxi_end_file_context(&fc, NULL, f != NULL);
	return f;
}

static blob read_whole(const char *path, size_t path_len)
{
	blob b = { 0, 0 };
	FILE *f = fopen_utf8(path, path_len);
	if (!f) return b;
	if (fseek(f, 0, SEEK_END) == 0) {
		long n = ftell(f);
		if (n > 0) {
			b.size = (size_t) n;
			b.data = (uint8_t*) malloc(b.size + 1);
			rewind(f);
			size_t got = fread(b.data, 1, b.size, f);
			b.size = got;
			if (b.data) b.data[b.size] = 0;
		}
	}
	fclose(f);
	return b;
}

// -- Path variants

enum {
	PATH_NONE = 0,
	PATH_VALID,         // the corpus path, explicit length
	PATH_VALID_MAX,     // the corpus path, `path_len == SIZE_MAX` -> strlen() inside the ABI
	PATH_MISSING,       // the corpus path + ".missing"
	PATH_BAD_LEAD,      // 0xff 0xfe prefix: "Bad UTF-8 character, fail early" (ufbx.c:7014)
	PATH_BAD_MID,       // a stray continuation byte 0x80 in the middle
	PATH_TRUNC_SEQ,     // the first two bytes of a 3-byte sequence, cut by path_len
	PATH_SURROGATE,     // UTF-8-encoded lone surrogate U+D800 (the lax decoder accepts it)
	PATH_EMOJI,         // U+1F602 -- the 4-byte branch, and the surrogate-pair emit
	PATH_EMPTY,         // "" with length 0
	PATH_NUL_JUNK,      // "<path>\0junk" with the FULL length -> "File not found"
	PATH_NUL_JUNK_MAX,  // the same bytes with SIZE_MAX -> strlen stops at the NUL -> opens
	PATH_LONG,          // >255 bytes, so ufbxi_fopen() takes the heap `wpath` branch (6986-6991)
};

static char *path_bytes(int kind, const char *rel, size_t rel_len, size_t *out_len)
{
	char *p;
	size_t n;
	switch (kind) {
	case PATH_VALID:
	case PATH_VALID_MAX:
		p = (char*) malloc(rel_len + 1); memcpy(p, rel, rel_len); p[rel_len] = 0; n = rel_len;
		break;
	case PATH_MISSING: {
		static const char suf[] = ".missing";
		n = rel_len + sizeof(suf) - 1;
		p = (char*) malloc(n + 1); memcpy(p, rel, rel_len);
		memcpy(p + rel_len, suf, sizeof(suf) - 1); p[n] = 0;
		break;
	}
	case PATH_BAD_LEAD: {
		static const char pre[] = "\xff\xfe";
		n = sizeof(pre) - 1 + rel_len;
		p = (char*) malloc(n + 1); memcpy(p, pre, sizeof(pre) - 1);
		memcpy(p + sizeof(pre) - 1, rel, rel_len); p[n] = 0;
		break;
	}
	case PATH_BAD_MID: {
		static const char mid[] = "data/\x80 rest of the path";
		n = sizeof(mid) - 1;
		p = (char*) malloc(n + 1); memcpy(p, mid, n); p[n] = 0;
		break;
	}
	case PATH_TRUNC_SEQ: {
		static const char s[] = "data/\xe4\xb8";
		n = sizeof(s) - 1;
		p = (char*) malloc(n + 1); memcpy(p, s, n); p[n] = 0;
		break;
	}
	case PATH_SURROGATE: {
		static const char s[] = "data/\xed\xa0\x80";
		n = sizeof(s) - 1;
		p = (char*) malloc(n + 1); memcpy(p, s, n); p[n] = 0;
		break;
	}
	case PATH_EMOJI: {
		static const char s[] = "data/\xf0\x9f\x98\x82.fbx";
		n = sizeof(s) - 1;
		p = (char*) malloc(n + 1); memcpy(p, s, n); p[n] = 0;
		break;
	}
	case PATH_EMPTY:
		p = (char*) malloc(1); p[0] = 0; n = 0;
		break;
	case PATH_NUL_JUNK:
	case PATH_NUL_JUNK_MAX: {
		static const char junk[] = "junk-that-is-not-part-of-the-path";
		n = rel_len + 1 + sizeof(junk) - 1;
		p = (char*) malloc(n + 1); memcpy(p, rel, rel_len); p[rel_len] = 0;
		memcpy(p + rel_len + 1, junk, sizeof(junk) - 1); p[n] = 0;
		break;
	}
	case PATH_LONG: {
		n = rel_len + 300;
		p = (char*) malloc(n + 1); memcpy(p, rel, rel_len);
		memset(p + rel_len, '_', 300); p[n] = 0;
		break;
	}
	default:
		p = (char*) malloc(1); p[0] = 0; n = 0;
		break;
	}
	*out_len = n;
	return p;
}

// -- The scripted stream (SCRIPT mode)

// A deterministic function of the *call index*: both sides must issue the callbacks in the same
// order with the same arguments, which is exactly what the H/Q records pin.
enum {
	SR_PERFECT = 0,   // short-read-free: read returns min(max_size, remaining)
	SR_CHUNK1000,     // never more than 1000 bytes per read
	SR_CHUNK1,        // one byte per read
	SR_CHUNK3,        // three bytes per read
	SR_FAIL5,         // read #5 returns SIZE_MAX -> "IO error"
	SR_EOF3,          // read #3 returns 0 -> premature EOF -> "Truncated file"
	SR_NOSKIP,        // no skip_fn at all -> ufbxi_skip_bytes() reads and discards
	SR_NOSIZE,        // no size_fn
	SR_SKIPFAIL,      // skip_fn present but always fails -> "Truncated file"
	SR_SIZE0,         // size_fn returns 0 (unknown)
	SR_SIZEMAX,       // size_fn returns UINT64_MAX -> ufbxi_check(total != UINT64_MAX) (25256)
	SR_EMPTY,         // every read returns 0 -> "Empty file"
	SR_FAIL0,         // the very first read returns SIZE_MAX
	SR_EOF0,          // the very first read returns 0
};

typedef struct {
	const uint8_t *data;
	size_t size;
	size_t pos;
	int rule;
	int n_read, n_skip, n_size;
} script_stream;

static size_t script_read(void *user, void *data, size_t max_size)
{
	script_stream *s = (script_stream*) user;
	int ix = s->n_read++;

	if (s->rule == SR_FAIL0) return SIZE_MAX;
	if (s->rule == SR_FAIL5 && ix == 5) return SIZE_MAX;
	if (s->rule == SR_EMPTY || s->rule == SR_EOF0) return 0;
	if (s->rule == SR_EOF3 && ix == 3) return 0;

	size_t avail = s->size - s->pos;
	size_t cap = avail;
	if (s->rule == SR_CHUNK1000) cap = 1000;
	else if (s->rule == SR_CHUNK1) cap = 1;
	else if (s->rule == SR_CHUNK3) cap = 3;

	size_t n = max_size < cap ? max_size : cap;
	if (n > avail) n = avail;
	memcpy(data, s->data + s->pos, n);
	s->pos += n;
	return n;
}

static bool script_skip(void *user, size_t size)
{
	script_stream *s = (script_stream*) user;
	s->n_skip++;
	if (s->rule == SR_SKIPFAIL) return false;
	if (s->size - s->pos < size) return false;
	s->pos += size;
	return true;
}

static uint64_t script_size(void *user)
{
	script_stream *s = (script_stream*) user;
	s->n_size++;
	if (s->rule == SR_SIZE0) return 0;
	if (s->rule == SR_SIZEMAX) return UINT64_MAX;
	return (uint64_t) s->size;
}

// BLIND SPOT (equivalent-by-construction): leaving `close_fn` NULL here would be the faithful
// spelling of a C caller that owns nothing -- the loader then never calls any close callback
// (`if (uc->close_fn)` at ufbx.c:25613) and the IO log shows zero closes. The port cannot express
// that state: PORTING_NOTES #8 models the callback quad as a `UfbxInputStream` whose `Close()` is
// an always-present virtual method, so "no close_fn" and "close_fn that does nothing" are the same
// object. Supplying a no-op close_fn instead pins what IS expressible on both sides -- that the
// loader closes exactly once, at the very end, on both the success and the failure path. The
// NULL-close_fn behaviour itself is covered observably by the stdio variants (close=false leaves
// the FILE* open and its position readable -- the `K` record).
static void script_close(void *user)
{
	(void) user;
}

// -- The logging wrapper

enum { IO_READ = 0, IO_SKIP = 1, IO_SIZE = 2, IO_CLOSE = 3 };
#define IO_MAX 4096

static int s_io_kind[IO_MAX];
static uint64_t s_io_arg[IO_MAX];
static uint64_t s_io_ret[IO_MAX];
static int s_io_n;
static int s_nread, s_nskip, s_nsize, s_nclose;
static uint64_t s_io_hash;

static void io_reset(void)
{
	s_io_n = 0;
	s_nread = s_nskip = s_nsize = s_nclose = 0;
	s_io_hash = FNV_BASIS;
}

static void io_log(int kind, uint64_t arg, uint64_t ret)
{
	if (kind == IO_READ) s_nread++;
	else if (kind == IO_SKIP) s_nskip++;
	else if (kind == IO_SIZE) s_nsize++;
	else s_nclose++;
	if (s_io_n < IO_MAX) {
		s_io_kind[s_io_n] = kind;
		s_io_arg[s_io_n] = arg;
		s_io_ret[s_io_n] = ret;
		s_io_n++;
	}
	s_io_hash = hh_u64(hh_u64(hh_u64(s_io_hash, (uint64_t) kind), arg), ret);
}

typedef struct { ufbx_stream inner; } wrap_stream;

static size_t wrap_read(void *user, void *data, size_t max_size)
{
	wrap_stream *w = (wrap_stream*) user;
	size_t r = w->inner.read_fn(w->inner.user, data, max_size);
	io_log(IO_READ, (uint64_t) max_size, (uint64_t) r);
	return r;
}

static bool wrap_skip(void *user, size_t size)
{
	wrap_stream *w = (wrap_stream*) user;
	bool ok = w->inner.skip_fn(w->inner.user, size);
	io_log(IO_SKIP, (uint64_t) size, ok ? 1u : 0u);
	return ok;
}

static uint64_t wrap_size(void *user)
{
	wrap_stream *w = (wrap_stream*) user;
	uint64_t r = w->inner.size_fn(w->inner.user);
	io_log(IO_SIZE, 0, r);
	return r;
}

static void wrap_close(void *user)
{
	wrap_stream *w = (wrap_stream*) user;
	io_log(IO_CLOSE, 0, 0);
	if (w->inner.close_fn) w->inner.close_fn(w->inner.user);
}

// NULL for the optional callbacks is meaningful (`ufbxi_skip_bytes()` has two branches, and
// `ufbxi_load_imp()` only queries the size when `size_fn` is set), so the wrapper mirrors it.
static void wrap_init(ufbx_stream *out, wrap_stream *w)
{
	out->read_fn = wrap_read;
	out->skip_fn = w->inner.skip_fn ? wrap_skip : NULL;
	out->size_fn = w->inner.size_fn ? wrap_size : NULL;
	out->close_fn = w->inner.close_fn ? wrap_close : NULL;
	out->user = w;
}

// -- Progress log

#define PROG_MAX 4096
static uint64_t s_prog_read[PROG_MAX], s_prog_total[PROG_MAX];
static int s_prog_n;

static void prog_reset(void) { s_prog_n = 0; }

static ufbx_progress_result progress_cb(void *user, const ufbx_progress *progress)
{
	(void) user;
	if (s_prog_n < PROG_MAX) {
		s_prog_read[s_prog_n] = progress->bytes_read;
		s_prog_total[s_prog_n] = progress->bytes_total;
		s_prog_n++;
	}
	return UFBX_PROGRESS_CONTINUE;
}

// -- Memory close callback

static int s_memclose_n;
static size_t s_memclose_size;
static uint64_t s_memclose_hash;
static uint8_t *s_memclose_bytes;

static void memclose_reset(void) {
	free(s_memclose_bytes);
	s_memclose_bytes = NULL;
	s_memclose_n = 0; s_memclose_size = 0; s_memclose_hash = 0;
}

// The whole blob is copied out: with `no_copy` this is the CALLER's buffer (so a later write shows
// up here and in the FNV), with a copy it is ufbx's internal one.
static void close_memory_cb(void *user, void *data, size_t data_size)
{
	(void) user;
	free(s_memclose_bytes);
	s_memclose_bytes = (uint8_t*) malloc(data_size + 1);
	if (data_size > 0 && data) memcpy(s_memclose_bytes, data, data_size);
	if (s_memclose_bytes) s_memclose_bytes[data_size] = 0;
	s_memclose_n++;
	s_memclose_size = data_size;
	s_memclose_hash = hh_bytes(FNV_BASIS, data, data_size);
}

// -- Custom open_file_cb (the deferred-open branch)

static int s_cb_n;
static char *s_cb_path;
static size_t s_cb_path_len;
static uint32_t s_cb_type;
static char *s_cb_orig;
static size_t s_cb_orig_len;
static bool s_cb_fail;

static void cb_reset(void) {
	free(s_cb_path); free(s_cb_orig);
	s_cb_n = 0; s_cb_path = NULL; s_cb_path_len = 0;
	s_cb_type = 0; s_cb_orig = NULL; s_cb_orig_len = 0;
}

static bool my_open_file(void *user, ufbx_stream *stream, const char *path, size_t path_len, const ufbx_open_file_info *info)
{
	s_cb_n++;
	s_cb_path = (char*) malloc(path_len + 1);
	memcpy(s_cb_path, path, path_len);
	s_cb_path[path_len] = 0;
	s_cb_path_len = path_len;
	s_cb_type = (uint32_t) info->type;
	s_cb_orig_len = info->original_filename.size;
	s_cb_orig = (char*) malloc(s_cb_orig_len + 1);
	if (info->original_filename.data) memcpy(s_cb_orig, info->original_filename.data, s_cb_orig_len);
	s_cb_orig[s_cb_orig_len] = 0;
	if (s_cb_fail) return false;
	return ufbx_default_open_file(user, stream, path, path_len, info);
}

// -- Variants

enum {
	SM_MEM_REAL = 0,       // ufbx_open_memory[_ctx]() + ufbx_load_stream[_prefix]()
	SM_MEM_ONLY,           // ufbx_open_memory[_ctx]() + a fixed probe, no load
	SM_FILE_REAL,          // ufbx_open_file[_ctx]() + ufbx_load_stream()
	SM_FILE_ONLY,          // ufbx_open_file[_ctx]() + a fixed probe
	SM_STDIO,              // fopen() + ufbx_load_stdio[_prefix]()
	SM_STDIO_NULL,         // ufbx_load_stdio(NULL, ...)
	SM_SCRIPT,             // the rule-based stream + ufbx_load_stream[_prefix]()
	SM_LOAD_FILE_CB,       // ufbx_load_file() with a custom open_file_cb (deferred open)
	SM_LOAD_FILE_DEFAULT,  // ufbx_load_file() through the default callback
};

typedef struct {
	int mode;
	int payload, payload_arg;
	int prefix;         // requested; resolved to <= size/2
	int opts_null;      // NULL opts to the open ABI
	int no_copy;
	int close_cb;
	int ctx;            // use the _ctx ABI with a non-NULL context
	int mutate;         // flip a payload byte after the stream was opened
	int script;         // SR_* rule
	int dirty;          // pre-dirty the caller's ufbx_error
	int err_null;       // NULL error out-param
	int path;           // PATH_*
	int nul_term;       // ufbx_open_file_opts.filename_null_terminated
	int progress;
	int read_buf;       // ufbx_load_opts.read_buffer_size (0 = default)
	int cb_fail;        // the custom open_file_cb always fails
	int default_cb;     // open_file_cb.fn = &ufbx_default_open_file
	int with_default;   // open_main_file_with_default
} sv;

static const sv k_variants[] = {
	// -- REAL memory streams: pins Parse/InputStreams.cs (ufbxi_memory_read/skip/size)
	/*  0 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/*  1 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // no_copy
	/*  2 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // close_cb
	/*  3 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/*  4 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // NULL opts = all-zero
	/*  5 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // _ctx
	/*  6 */ { SM_MEM_REAL, PK_HEAD, 64, 0, 0, 1, 0, 0, 1, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // no_copy + mutate
	/*  7 */ { SM_MEM_REAL, PK_HEAD, 64, 0, 0, 0, 0, 0, 1, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // copy + mutate (control)
	/*  8 */ { SM_MEM_REAL, PK_HEAD, 64, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // control for 6/7
	/*  9 */ { SM_MEM_REAL, PK_HEAD, 64, 0, 0, 1, 1, 0, 1, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // mutate seen by close_cb
	/* 10 */ { SM_MEM_REAL, PK_HEAD, 64, 0, 0, 1, 1, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // control for 9
	/* 11 */ { SM_MEM_REAL, PK_EMPTY, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // "Empty file"
	/* 12 */ { SM_MEM_REAL, PK_GARBAGE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },// "Unrecognized file format"
	/* 13 */ { SM_MEM_REAL, PK_ZEROS, 100, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 14 */ { SM_MEM_REAL, PK_ASCII_HEAD, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 15 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 1, 0, 0, 0, 0 }, // progress
	/* 16 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 64, 0, 0, 0 }, // tiny read buffer
	/* 17 */ { SM_MEM_REAL, PK_WHOLE, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // _prefix
	/* 18 */ { SM_MEM_REAL, PK_WHOLE, 0, 4096, 0, 1, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },

	// -- open_memory() alone: the callbacks, without the loader in the way
	/* 19 */ { SM_MEM_ONLY, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 20 */ { SM_MEM_ONLY, PK_WHOLE, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 21 */ { SM_MEM_ONLY, PK_WHOLE, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 22 */ { SM_MEM_ONLY, PK_HEAD, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },

	// -- REAL file streams + the whole `ufbxi_fopen()` path table
	/* 23 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID, 0, 0, 0, 0, 0, 0 },
	/* 24 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID_MAX, 0, 0, 0, 0, 0, 0 },
	/* 25 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_MISSING, 0, 0, 0, 0, 0, 0 },
	/* 26 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_BAD_LEAD, 0, 0, 0, 0, 0, 0 },
	/* 27 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_BAD_MID, 0, 0, 0, 0, 0, 0 },
	/* 28 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_TRUNC_SEQ, 0, 0, 0, 0, 0, 0 },
	/* 29 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_SURROGATE, 0, 0, 0, 0, 0, 0 },
	/* 30 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_EMOJI, 0, 0, 0, 0, 0, 0 },
	/* 31 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_EMPTY, 0, 0, 0, 0, 0, 0 },
	/* 32 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NUL_JUNK, 0, 0, 0, 0, 0, 0 },
	/* 33 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NUL_JUNK_MAX, 0, 0, 0, 0, 0, 0 },
	/* 34 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_LONG, 0, 0, 0, 0, 0, 0 },
	/* 35 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, PATH_VALID, 0, 0, 0, 0, 0, 0 }, // dirty -> cleared
	/* 36 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, PATH_MISSING, 0, 0, 0, 0, 0, 0 }, // NULL error
	/* 37 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, PATH_VALID, 0, 0, 0, 0, 0, 0 }, // _ctx + NULL opts
	/* 38 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID, 1, 0, 0, 0, 0, 0 }, // nul_term (no-op on _WIN32)
	/* 39 */ { SM_FILE_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, PATH_MISSING, 0, 0, 0, 0, 0, 0 }, // dirty -> "Failed to open file"
	/* 40 */ { SM_FILE_ONLY, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID, 0, 0, 0, 0, 0, 0 },
	/* 41 */ { SM_FILE_ONLY, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_MISSING, 0, 0, 0, 0, 0, 0 },
	/* 42 */ { SM_FILE_ONLY, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_BAD_LEAD, 0, 0, 0, 0, 0, 0 },
	/* 43 */ { SM_FILE_ONLY, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, PATH_VALID, 0, 0, 0, 0, 0, 0 },

	// -- stdio: `close_fn == NULL`, so the caller can see where the loader stopped
	/* 44 */ { SM_STDIO, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 45 */ { SM_STDIO, PK_WHOLE, 0, 32, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 46 */ { SM_STDIO, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 1, 0, 0, 0, 0 },
	/* 47 */ { SM_STDIO, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 0, 64, 0, 0, 0 },
	/* 48 */ { SM_STDIO_NULL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 }, // error untouched

	// -- SCRIPT: the loader's IO timing and its error paths, independent of either stream impl
	/* 49 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_PERFECT, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 50 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_CHUNK1000, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 51 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_CHUNK1, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 52 */ { SM_SCRIPT, PK_WHOLE, 0, 32, 0, 0, 0, 0, 0, SR_CHUNK3, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 53 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_FAIL5, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 54 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_EOF3, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 55 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_NOSKIP, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 56 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_NOSIZE, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 57 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_SKIPFAIL, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 58 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_SIZE0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 59 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_SIZEMAX, 0, 0, PATH_NONE, 0, 1, 0, 0, 0, 0 },
	/* 60 */ { SM_SCRIPT, PK_EMPTY, 0, 0, 0, 0, 0, 0, 0, SR_EMPTY, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 61 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_FAIL0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 62 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_EOF0, 0, 0, PATH_NONE, 0, 0, 0, 0, 0, 0 },
	/* 63 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_CHUNK1000, 0, 0, PATH_NONE, 0, 1, 0, 0, 0, 0 },

	// -- the deferred open of `ufbxi_load_imp()` (ufbx.c:25216-25252)
	/* 64 */ { SM_LOAD_FILE_CB, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID, 0, 0, 0, 0, 0, 0 },
	/* 65 */ { SM_LOAD_FILE_CB, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, PATH_VALID, 0, 0, 0, 0, 0, 0 },
	/* 66 */ { SM_LOAD_FILE_CB, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_MISSING, 0, 0, 0, 1, 0, 0 },
	/* 67 */ { SM_LOAD_FILE_CB, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID, 0, 0, 0, 1, 0, 0 }, // cb fails
	/* 68 */ { SM_LOAD_FILE_DEFAULT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID, 0, 0, 0, 0, 0, 1 },
	/* 69 */ { SM_LOAD_FILE_DEFAULT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_VALID, 0, 0, 0, 0, 1, 0 },
	/* 70 */ { SM_LOAD_FILE_DEFAULT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_MISSING, 0, 0, 0, 0, 0, 1 },
	/* 71 */ { SM_LOAD_FILE_DEFAULT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_BAD_LEAD, 0, 0, 0, 0, 0, 1 },

	// -- progress at a 256-byte interval, which does fire on every corpus file
	/* 72 */ { SM_MEM_REAL, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 2, 0, 0, 0, 0 },
	/* 73 */ { SM_MEM_REAL, PK_WHOLE, 0, 32, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 2, 0, 0, 0, 0 },
	/* 74 */ { SM_SCRIPT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, SR_CHUNK1000, 0, 0, PATH_NONE, 0, 2, 0, 0, 0, 0 },
	/* 75 */ { SM_STDIO, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NONE, 0, 2, 0, 0, 0, 0 },

	// -- the deferred open with an interior '\0': `ufbx_load_file()` always spells `path_len` as
	// SIZE_MAX, so the ABI resolves it with strlen() to 38 BEFORE the callback sees it
	// (ufbx.c:25230-25233, the batch-L trap). The `F` record is the only place the resolved
	// value is observable, and it must be 38 -- not the 74 bytes of the buffer.
	/* 76 */ { SM_LOAD_FILE_CB, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NUL_JUNK, 0, 0, 0, 0, 0, 0 },
	/* 77 */ { SM_LOAD_FILE_DEFAULT, PK_WHOLE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, PATH_NUL_JUNK, 0, 0, 0, 0, 0, 1 },
};
#define NUM_VARIANTS (sizeof(k_variants)/sizeof(k_variants[0]))

// -- Call site

static void dirty_error(ufbx_error *e)
{
	memset(e, 0, sizeof(*e));
	e->type = UFBX_ERROR_TRUNCATED_FILE;
	e->description.data = "Stale description";
	e->description.length = strlen("Stale description");
	memcpy(e->info, "stale info", 10);
	e->info_length = 10;
}

// The `ufbx_stream` out-parameter: `read_fn` is the one callback C requires.
static int stream_wired(const ufbx_stream *s) {
	return s && s->read_fn ? 1 : 0;
}

static ufbx_load_opts golden_load_opts(const sv *v, const char *rel, size_t rel_len)
{
	ufbx_load_opts o; // ufbxi_uninit
	memset(&o, 0, sizeof(o));
	o.load_external_files = true;
	o.ignore_missing_external_files = true;
	o.evaluate_caches = true;
	o.evaluate_skinning = true;
	o.target_axes = ufbx_axes_right_handed_y_up;
	o.target_unit_meters = 1.0f;
	// The stream variants have no `load_filename`, so pin the external-file base explicitly:
	// identical input on both sides.
	o.filename.data = rel;
	o.filename.length = rel_len;
	// progress: 0 = off, 1 = the default 0x4000 interval, 2 = a 256-byte interval. The default
	// never fires on the small corpus files (the whole payload lands in one read buffer, so
	// `ufbxi_yield()` is never reached), which is why the 256-byte variants exist: they force
	// `ufbxi_resume_progress()` to report, pinning bytes_read/bytes_total -- including the
	// `progress_bytes_total == 0` start of `ufbx_load_stream_prefix()` (30574-30579 vs 30517).
	if (v->progress) {
		o.progress_cb.fn = &progress_cb;
		if (v->progress >= 2) o.progress_interval_hint = 256;
	}
	if (v->read_buf) o.read_buffer_size = (size_t) v->read_buf;
	return o;
}

static void dump_variant(int fi, int vi, const blob *whole, const char *rel, size_t rel_len)
{
	const sv *v = &k_variants[vi];
	io_reset(); prog_reset(); memclose_reset(); cb_reset();
	s_cb_fail = v->cb_fail != 0;

	blob pl = build_payload(v->payload, v->payload_arg, whole);

	size_t pfx = 0;
	if (v->prefix > 0) {
		size_t want = (size_t) v->prefix;
		size_t half = pl.size / 2;
		pfx = want < half ? want : half;
	}

	size_t path_len = 0;
	char *path = NULL;
	if (v->path != PATH_NONE) path = path_bytes(v->path, rel, rel_len, &path_len);

	// The resolved `path_len` is part of the input record: PATH_*_MAX spell it as SIZE_MAX (-1)
	// and let the ABI apply strlen() (ufbx.c:30430), everything else uses the explicit byte count.
	ptrdiff_t plen = (ptrdiff_t) path_len;
	if (v->path == PATH_VALID_MAX || v->path == PATH_NUL_JUNK_MAX) plen = -1;

	// ---- input records
	printf("S %d %d %d %d %zu", fi, vi, v->payload, v->payload_arg, pl.size);
	pz(hh_bytes(FNV_BASIS, pl.data, pl.size));
	printf("\n");

	printf("I %d %d %d %d %d %zu %zu %d %d %d %d %d %d %d %d %d %zd %d %d %d %d %d %d\n",
		fi, vi, v->mode, v->payload, v->payload_arg, pl.size, pfx,
		v->opts_null, v->no_copy, v->close_cb, v->ctx, v->mutate, v->script,
		v->dirty, v->err_null, v->path, plen, v->nul_term, v->progress, v->read_buf,
		v->cb_fail, v->default_cb, v->with_default);

	if (v->path != PATH_NONE) {
		printf("Ip %d %d", fi, vi);
		phex(path, path_len);
		printf("\n");
	} else {
		printf("Ip %d %d 0 -\n", fi, vi);
	}

	if (pfx > 0) {
		printf("Id %d %d", fi, vi);
		phex((const char*) pl.data, pfx);
		printf("\n");
	} else {
		printf("Id %d %d 0 -\n", fi, vi);
	}

	// ---- the call
	ufbx_error error; // ufbxi_uninit
	memset(&error, 0, sizeof(error));
	if (v->dirty) dirty_error(&error);
	ufbx_error *p_error = v->err_null ? NULL : &error;

	ufbx_load_opts lopts = golden_load_opts(v, rel, rel_len);
	ufbx_open_file_opts fopen_opts; // ufbxi_uninit
	memset(&fopen_opts, 0, sizeof(fopen_opts));
	fopen_opts.filename_null_terminated = v->nul_term != 0;

	ufbx_open_memory_opts mopts; // ufbxi_uninit
	memset(&mopts, 0, sizeof(mopts));
	mopts.no_copy = v->no_copy != 0;
	if (v->close_cb) {
		mopts.close_cb.fn = &close_memory_cb;
	}

	// `ctx` here only selects the `_ctx` spelling of the ABI; the context value itself is always
	// NULL. C's `ufbx_open_file_context` is an opaque `uintptr_t` naming a parent ALLOCATOR
	// (`(ufbx_open_file_context)&uc->ator_tmp`, ufbx.c:25234) that `ufbxi_begin_file_context()`
	// immediately dereferences and copies out (6948-6958), so a synthetic non-zero value would be
	// a wild read, not a test. Which allocator the metadata block is freed through is not
	// observable in the port (PORTING_NOTES.md #4), so this is an equivalent-by-construction gap.
	ufbx_open_file_context ctx = (ufbx_open_file_context) NULL;

	ufbx_stream inner; // ufbxi_uninit
	memset(&inner, 0, sizeof(inner));
	wrap_stream wrap; // ufbxi_uninit
	memset(&wrap, 0, sizeof(wrap));
	ufbx_stream stream; // ufbxi_uninit
	memset(&stream, 0, sizeof(stream));
	script_stream script; // ufbxi_uninit
	memset(&script, 0, sizeof(script));

	FILE *file = NULL;
	ufbx_scene *scene = NULL;
	int ok = 0;
	int wired = -1;
	int logged = 1;      // 0 = the stream was not wrapped (ufbx_load_stdio())
	long long cursor = -1;

	// `mutate` flips a byte of the CALLER's buffer. It is applied *after* the stream is opened, so
	// only a `no_copy` stream (which aliases that buffer) can see it -- everything else must read
	// the bytes that were there at open time.
	switch (v->mode) {
	case SM_MEM_REAL:
	case SM_MEM_ONLY: {
		size_t skip = pfx;
		size_t stream_size = pl.size - pfx;
		const ufbx_open_memory_opts *user_mopts = v->opts_null ? NULL : &mopts;
		if (v->ctx) ok = ufbx_open_memory_ctx(&inner, ctx, pl.data + skip, stream_size, user_mopts, p_error) ? 1 : 0;
		else ok = ufbx_open_memory(&inner, pl.data + skip, stream_size, user_mopts, p_error) ? 1 : 0;
		wired = stream_wired(&inner);
		if (v->mutate && pl.size > 4) pl.data[3] ^= 0x55;
		if (!ok) break;
		wrap.inner = inner;
		wrap_init(&stream, &wrap);
		if (v->mode == SM_MEM_ONLY) {
			// A fixed probe: the four callbacks, with no loader in between.
			char tmp[64];
			stream.read_fn(stream.user, tmp, 4);
			stream.read_fn(stream.user, tmp, 9);
			if (stream.skip_fn) stream.skip_fn(stream.user, 3);
			if (stream.size_fn) stream.size_fn(stream.user);
			stream.read_fn(stream.user, tmp, 4);
			if (stream.close_fn) stream.close_fn(stream.user);
		} else {
			// The *_ONLY variants report the OPEN, not a load: there is no scene to report.
			scene = pfx > 0 ? ufbx_load_stream_prefix(&stream, pl.data, pfx, &lopts, p_error)
			                : ufbx_load_stream(&stream, &lopts, p_error);
			ok = scene != NULL;
		}
		break;
	}
	case SM_FILE_REAL:
	case SM_FILE_ONLY: {
		const ufbx_open_file_opts *user_fopts = v->opts_null ? NULL : &fopen_opts;
		// `plen == -1` is the SIZE_MAX spelling: the ABI applies strlen() itself (ufbx.c:30430).
		size_t fpath_len = plen < 0 ? (size_t) -1 : (size_t) plen;
		if (v->ctx) ok = ufbx_open_file_ctx(&inner, ctx, path, fpath_len, user_fopts, p_error) ? 1 : 0;
		else ok = ufbx_open_file(&inner, path, fpath_len, user_fopts, p_error) ? 1 : 0;
		wired = stream_wired(&inner);
		if (!ok) break;
		wrap.inner = inner;
		wrap_init(&stream, &wrap);
		if (v->mode == SM_FILE_ONLY) {
			char tmp[64];
			stream.read_fn(stream.user, tmp, 4);
			stream.read_fn(stream.user, tmp, 9);
			if (stream.skip_fn) stream.skip_fn(stream.user, 3);
			if (stream.size_fn) stream.size_fn(stream.user);
			stream.read_fn(stream.user, tmp, 4);
			if (stream.close_fn) stream.close_fn(stream.user);
		} else {
			scene = ufbx_load_stream(&stream, &lopts, p_error);
			ok = scene != NULL;
		}
		break;
	}
	case SM_STDIO: {
		file = fopen_utf8(rel, rel_len);
		if (!file) { ok = 0; break; }
		if (pfx > 0) {
			char *tmp = (char*) malloc(pfx + 1);
			size_t got = fread(tmp, 1, pfx, file);
			memset(tmp + got, 0, pfx - got);
			scene = ufbx_load_stdio_prefix(file, tmp, pfx, &lopts, p_error);
			free(tmp);
		} else {
			scene = ufbx_load_stdio(file, &lopts, p_error);
		}
		ok = scene != NULL;
		logged = 0;
		if (fseek(file, 0, SEEK_CUR) == 0) cursor = (long long) ftell(file);
		fclose(file);
		break;
	}
	case SM_STDIO_NULL: {
		// `if (!file_void) return NULL;` (ufbx.c:30544) -- no `ufbxi_load()` runs at all, so the
		// caller's dirty `ufbx_error` must come back untouched.
		scene = ufbx_load_stdio(NULL, &lopts, p_error);
		ok = scene != NULL;
		logged = 0;
		break;
	}
	case SM_SCRIPT: {
		script.data = pl.data;
		script.size = pl.size;
		script.pos = pfx;
		script.rule = v->script;
		inner.read_fn = script_read;
		inner.skip_fn = v->script == SR_NOSKIP ? NULL : script_skip;
		inner.size_fn = v->script == SR_NOSIZE ? NULL : script_size;
		inner.close_fn = script_close;
		inner.user = &script;
		wrap.inner = inner;
		wrap_init(&stream, &wrap);
		if (pfx > 0) scene = ufbx_load_stream_prefix(&stream, pl.data, pfx, &lopts, p_error);
		else scene = ufbx_load_stream(&stream, &lopts, p_error);
		ok = scene != NULL;
		break;
	}
	case SM_LOAD_FILE_CB:
	case SM_LOAD_FILE_DEFAULT: {
		if (v->default_cb) lopts.open_file_cb.fn = &ufbx_default_open_file;
		else if (v->mode == SM_LOAD_FILE_CB) lopts.open_file_cb.fn = &my_open_file;
		lopts.open_main_file_with_default = v->with_default != 0;
		scene = ufbx_load_file(path, &lopts, p_error);
		ok = scene != NULL;
		logged = 0;
		break;
	}
	default:
		break;
	}

	// ---- records
	printf("A %d %d %d", fi, vi, ok);
	if (wired >= 0) printf(" %d", wired);
	else printf(" -");
	if (p_error) perr(p_error);
	else printf(" - - - - -");
	printf("\n");

	printf("C %d %d", fi, vi);
	if (p_error) perr(p_error);
	else printf(" -");
	printf("\n");

	printf("K %d %d %lld\n", fi, vi, cursor);

	if (v->mode == SM_MEM_ONLY || v->mode == SM_MEM_REAL || v->mode == SM_FILE_ONLY || v->mode == SM_FILE_REAL) {
		printf("M %d %d %d", fi, vi, s_memclose_n);
		if (s_memclose_n > 0) {
			phex((const char*) s_memclose_bytes, s_memclose_size);
			printf(" %zu", s_memclose_size);
		} else {
			printf(" 0 - 0");
		}
		printf(" %016llx\n", (unsigned long long) s_memclose_hash);
	} else {
		printf("M %d %d 0 0 - 0 %016llx\n", fi, vi, (unsigned long long) 0);
	}

	if (v->mode == SM_LOAD_FILE_CB) {
		printf("F %d %d", fi, vi);
		if (s_cb_n > 0) {
			printf(" %zu", s_cb_path_len);
			phex(s_cb_path, s_cb_path_len);
			printf(" %u", s_cb_type);
			phex(s_cb_orig, s_cb_orig_len);
		} else {
			printf(" -1 0 - 0 0 -");
		}
		printf("\n");
	}

	if (logged) {
		printf("R %d %d %d %d %d %d\n", fi, vi, s_nread, s_nskip, s_nsize, s_nclose);
		printf("H %d %d %d", fi, vi, s_io_n);
		pz(s_io_hash);
		printf("\n");
		if (s_io_n <= 64) {
			for (int i = 0; i < s_io_n; i++) {
				printf("Q %d %d %d %d %llu %llu\n", fi, vi, i, s_io_kind[i],
					(unsigned long long) s_io_arg[i], (unsigned long long) s_io_ret[i]);
			}
		}
	} else {
		printf("R %d %d -1 -1 -1 -1\n", fi, vi);
		printf("H %d %d -1 -\n", fi, vi);
	}

	if (v->progress) {
		for (int i = 0; i < s_prog_n; i++) {
			printf("P %d %d %d %llu %llu\n", fi, vi, i,
				(unsigned long long) s_prog_read[i], (unsigned long long) s_prog_total[i]);
		}
	}

	if (scene) {
		printf("V %d %d", fi, vi);
		pz(ufbxt_hash_scene(scene, NULL));
		printf("\n");
		ufbx_free_scene(scene);
	}

	free(s_cb_path); s_cb_path = NULL;
	free(s_cb_orig); s_cb_orig = NULL;
	free(s_memclose_bytes); s_memclose_bytes = NULL;
	if (path) free(path);
	if (pl.data) free(pl.data);
}

// The corpus is read from a FILE, not from argv: `data/synthetic_a\xce\xb2\xe3\x82\xab\xf0\x9f
// \x98\x82_7500_ascii.fbx` is the only non-ASCII path in the tree and is one of the inputs this
// batch is about, and MSYS re-encodes non-ASCII argv on its way to the Windows process -- the
// oracle would be handed `a6 c2 a5 ab 3f 3f` instead of `ce b2 e3 82 ab f0 9f 98 82`. Reading the
// bytes off disk keeps them exactly as the filesystem spells them. (Same convention as
// tools/load_oracle.c, whose corpus list is read with fopen() for the same reason.)
static char **read_list(const char *path, size_t *out_count)
{
	FILE *f = fopen(path, "rb");
	if (!f) return NULL;
	char **names = NULL;
	size_t n = 0, cap = 0;
	char line[1024];
	while (fgets(line, (int) sizeof(line), f)) {
		size_t len = strlen(line);
		while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r')) line[--len] = '\0';
		if (len == 0 || line[0] == '#') continue;
		if (n == cap) {
			cap = cap ? cap * 2 : 16;
			names = (char**) realloc(names, cap * sizeof(char*));
		}
		names[n] = (char*) malloc(len + 1);
		memcpy(names[n], line, len + 1);
		n++;
	}
	fclose(f);
	*out_count = n;
	return names;
}

int main(int argc, char **argv)
{
	if (argc < 2) {
		fprintf(stderr, "usage: stream_oracle.exe --list <corpus.txt> | (<file>)...\n");
		return 2;
	}

	char **names = NULL;
	size_t count = 0;
	if (argc >= 3 && strcmp(argv[1], "--list") == 0) {
		names = read_list(argv[2], &count);
		if (!names) {
			fprintf(stderr, "cannot open list: %s\n", argv[2]);
			return 2;
		}
	} else {
		names = &argv[1];
		count = (size_t) (argc - 1);
	}

	for (size_t i = 0; i < count; i++) {
		const int fi = (int) i;
		const char *rel = names[i];
		blob whole = read_whole(rel, strlen(rel));
		if (!whole.data || whole.size == 0) {
			fprintf(stderr, "cannot read: %s\n", rel);
			continue;
		}
		for (int vi = 0; vi < (int) NUM_VARIANTS; vi++) {
			dump_variant(fi, vi, &whole, rel, strlen(rel));
		}
		free(whole.data);
	}

	if (argc >= 3 && strcmp(argv[1], "--list") == 0) {
		for (size_t i = 0; i < count; i++) free(names[i]);
		free(names);
	}
	return 0;
}
