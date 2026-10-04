// Batch K oracle: the animation-creation chain (`ufbx_create_anim()` and its private helpers).
//
// Built with `#include "ufbx.c"` so it runs the original `ufbxi_check_string()` (ufbx.c:26506-26518),
// `ufbxi_push_anim_string()` (26520-26534), the three override comparators (26536-26558),
// `ufbxi_create_anim_imp()` (26560-26676) and the public `ufbx_create_anim()` (31202-31226), plus
// `ufbx_evaluate_scene()` on the anim it returns. The C# twin is src/Ufbx.NET/Parse/CreateAnim.cs +
// UfbxApi.CreateAnim, replayed by tools/CreateAnimCheck.
//
// DESIGN: the variant table lives *only* here. Every `ufbx_anim_opts` this oracle hands to
// `ufbx_create_anim()` is re-emitted as `I`-family input records, so the harness rebuilds the exact
// same opts from the records instead of mirroring the table (and `pick_element_id()` and friends) in
// a second language. Same pattern as bake's `T` records and UtilCheck's self-describing `FE` inputs.
//
// Grammar (space separated, one record per line; `<h>` = 16 lowercase hex digits of the IEEE-754
// bits of a double, `<z>` = 16 hex digits of an FNV-1a-64, `<x>` = lowercase hex of raw bytes, an
// empty byte run is the single token `-`):
//   S <fi> <nodes> <elements> <animLayers> <animPresent>       load summary; <fi> is the 0-based
//                                                corpus index (argv is 1-based)
//   U <fi> <ti> <time:h>                                       the 8 sample times the `V` records
//                                                evaluate at, emitted once per file
//   I <fi> <vi> <nullOpts> <ignoreConn> <numLayerIds> <numWeights> <numProps> <numTransforms>
//                                                the caller's `ufbx_anim_opts` shape
//   Ia <fi> <vi> <ix> <layerId>                                ufbx_anim_opts.layer_ids[ix]
//   Iw <fi> <vi> <ix> <weight:h>                                ufbx_anim_opts.override_layer_weights[ix]
//   Ip <fi> <vi> <ix> <elementId> <nameLen> <nameX> <strLen> <strX>
//      <x:h> <y:h> <z:h> <w:h> <valueInt>                       ufbx_anim_opts.prop_overrides[ix],
//                                                verbatim: the bytes the caller passed, including
//                                                invalid UTF-8, embedded NULs and >255-byte names
//   It <fi> <vi> <ix> <nodeId> <10 transform halves>            ufbx_anim_opts.transform_overrides[ix]
//   A <fi> <vi> <ok> <errType> <descLen> <descX> <infoLen> <infoX>
//      <layers> <weights> <props> <transforms> <custom> <ignoreConn>
//                                                the call outcome: NULL/non-NULL and the *bytes* of
//                                                ufbx_error's description + info, so "Invalid UTF-8"
//                                                vs "Duplicate override" vs the default "Failed to
//                                                create anim" and the `ufbxi_fmt_err_info()` payload
//                                                (incl. its 255-byte truncation and
//                                                `ufbxi_clean_string_utf8()` pass) are all pinned
//   L <fi> <vi> <ix> <typedId> <elementId> <nameLen> <nameX> <weight:h>
//                                                anim->layers[ix] identity, by typed_id (C compares
//                                                pointers; the port must resolve the same scene layer)
//   W <fi> <vi> <ix> <weight:h>                                anim->override_layer_weights[ix]
//   P <fi> <vi> <ix> <elementId> <internalKey> <nameLen> <nameX>
//      <x:h> <y:h> <z:h> <w:h> <valueInt> <strLen> <strX>       anim->prop_overrides[ix] in the FINAL
//                                                (evaluation) order, every field: name interning as a
//                                                content match against `ufbxi_strings[]`, the
//                                                value.x <-> value_int cross-fill (26604-26608) and
//                                                the `element_id, _internal_key, strcmp` sort order
//   T <fi> <vi> <ix> <nodeId> <10 transform halves>            anim->transform_overrides[ix] after the
//                                                `node_id` sort; tied ids pin the unstable permutation
//   O <fi> <vi> <before:z> <after:z> <same>                     caller's opts hashed before/after the
//                                                call (PORTING_NOTES.md #9: C copies the struct, so it
//                                                must be bit-identical afterwards)
//   V <fi> <vi> <ti> <hash:z>                                  end-to-end consumption: the created
//                                                anim evaluated against the real scene and hashed with
//                                                the golden generator's own `ufbxt_hash_scene()`
//                                                (test/hash_scene.h). `ufbxi_find_prop_override()`
//                                                binary-searches `prop_overrides` by
//                                                `element_id, prop_name`, so a wrong sort order or a
//                                                mis-interned name lands here as a different hash.
//
// Usage (run from C:/Workspace/_analyze_ufbx so the `data/...` paths resolve):
//   create_anim_oracle.exe (<file.fbx>)... > create_anim_oracle.txt
// Build (mandatory flags, see PORTING_NOTES "浮点语义"; UFBX_EXTERNAL_MATH is #defined below so a
// rebuild cannot forget it, and extra/ufbx_math.c provides the same libm transcriptions the port
// uses for the layer-combining path in ufbx_evaluate_scene()):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx \
//       C:/Workspace/ufbx-cs/tools/create_anim_oracle.c \
//       C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o C:/Workspace/ufbx-cs/tools/create_anim_oracle.exe

#ifndef UFBX_EXTERNAL_MATH
#define UFBX_EXTERNAL_MATH
#endif
#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

// The golden generator's own scene hasher, included exactly as `test/hash_scene.c:6` does.
#include "test/hash_scene.h"

#define MAX_PROPS 24
#define MAX_TRANSFORMS 16
#define MAX_LAYERS 16
#define MAX_WEIGHTS 17
#define NUM_TIMES 8

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
static uint64_t hh_i64(uint64_t h, int64_t v) { return hh_bytes(h, &v, 8); }
static uint64_t hh_d(uint64_t h, double d) { return hh_bytes(h, &d, 8); }
static uint64_t hh_u32(uint64_t h, uint32_t v) { return hh_bytes(h, &v, 4); }

static void pd(double v) {
	uint64_t u;
	memcpy(&u, &v, sizeof(u));
	printf(" %016llx", (unsigned long long) u);
}

static void pz(uint64_t v) {
	printf(" %016llx", (unsigned long long) v);
}

// ` <length> <hex>`, always one token for the bytes so an empty string cannot shift the columns.
// Raw bytes rather than a C string: names and value_strs in this corpus contain NULs, invalid UTF-8
// and bytes past the error-info truncation point.
static void pstr(const char *data, size_t length) {
	printf(" %zu ", length);
	if (length == 0) { printf("-"); return; }
	if (!data) { printf("null"); return; }
	for (size_t i = 0; i < length; i++) {
		printf("%02x", (unsigned char) data[i]);
	}
}

// -- Corpus loading

static ufbx_scene *load_golden_scene(const char *filename, ufbx_error *error)
{
	ufbx_load_opts opts = { 0 };
	opts.load_external_files = true;
	opts.ignore_missing_external_files = true;
	opts.evaluate_caches = true;
	opts.evaluate_skinning = true;
	opts.target_axes = ufbx_axes_right_handed_y_up;
	opts.target_unit_meters = 1.0f;
	return ufbx_load_file(filename, &opts, error);
}

// Whole frames, in-between fractions and values outside the anim range, so extrapolation of the
// overridden channels is exercised too. Emitted as `U` records: the harness replays these exact
// doubles rather than re-deriving them from the scene settings.
static size_t build_times(const ufbx_scene *scene, double *out, size_t max)
{
	const double fps = scene->settings.frames_per_second;
	const double tb = scene->anim ? scene->anim->time_begin : 0.0;
	static const double frames[] = { -13.0, -1.0, 0.0, 0.5, 1.0, 3.0, 7.0, 13.0 };
	size_t n = 0;
	for (size_t i = 0; i < ufbxi_arraycount(frames) && n < max; i++) {
		out[n++] = tb + frames[i] / fps;
	}
	return n;
}

// -- Override inputs

// A `ufbx_string` with an explicit length, so embedded NULs survive into the call instead of being
// silently strlen'd (the port documents that difference; here nothing is left implicit).
typedef struct { const char *data; size_t length; } lit;
#define LIT(s) { s, sizeof(s) - 1 }

// Chosen to reach every branch of the interning merge in ufbxi_create_anim_imp (26620-26643).
// `ufbxi_strings[]` is byte-sorted with first entry "AllSame" and last entry "d|Z", so:
//   * [0..7],[17] are global hits
//   * [8] ("Missing..."), [10] ("Aaa...", before the first entry), [12] ("AllSami", before
//     "AllSame"), [14] ("Lcl Translations") and [15]/[16] (past "d|Z") are arena copies
//   * [11] and [13] are exact boundary hits, [18] is the adjacent miss
//   * [9] ("zzz...") sorts past the whole table, so the merge cursor runs to the end and stays there
static const lit k_nm[] = {
	LIT("Lcl Translation"),
	LIT("Lcl Rotation"),
	LIT("Lcl Scaling"),
	LIT("RotationPivot"),
	LIT("InheritType"),
	LIT("Weight"),
	LIT("Visibility"),
	LIT("DeformPercent"),
	LIT("Missing Prop Name"),
	LIT("zzz After Everything"),
	LIT("Aaa Before Everything"),
	LIT("AllSame"),
	LIT("AllSami"),
	LIT("d|X"),
	LIT("d|Z"),
	LIT("d|["),
	LIT("a"),
	LIT("TimeMode"),
	LIT("Lcl Translations"),
};
#define NUM_NM (sizeof(k_nm)/sizeof(k_nm[0]))

static const lit k_nm_empty = { "", 0 };
static const lit k_nm_nul = { "Lcl\0Translation", 16 };
static const lit k_nm_bad = { "\xff\xfe" "bad name", 10 };

static const lit k_vs[] = {
	{ "", 0 },
	LIT("Hello"),
	{ "\xff\xfe invalid utf-8 tail", 25 },
	{ "embedded\0nul", 12 },
	LIT("Lcl Translation"),
};
#define NUM_VS (sizeof(k_vs)/sizeof(k_vs[0]))

// Long names, built at run time so they are clearly past `UFBX_ERROR_INFO_LENGTH`-1 = 255 bytes:
//   k_long_ascii  -- exercises the `ufbxi_vsnprintf()` clamp in ufbxi_fmt_err_info()
//   k_long_split  -- a 2-byte UTF-8 sequence landing exactly on the cut, so
//                    ufbxi_clean_string_utf8() rewrites the tail as '?'
static char k_long_ascii[361];
static char k_long_split[361];
static size_t k_long_ascii_len, k_long_split_len;

static void init_long_names(void)
{
	static const char frag[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz_-012";
	const size_t frag_len = sizeof(frag) - 1;
	size_t n = 0;
	while (n + frag_len < sizeof(k_long_ascii)) {
		memcpy(k_long_ascii + n, frag, frag_len);
		n += frag_len;
	}
	k_long_ascii[n] = '\0';
	k_long_ascii_len = n;

	// ASCII up to offset 215, then a dense run of 2-byte UTF-8 sequences (C3 A9 = U+00E9). The
	// format string is `element %u prop "%s"`, so the name starts at printed offset 8 + (digits of
	// element_id) + 7 and ufbxi_vsnprintf() clamps the buffer at 255 bytes. Putting the sequences
	// where the cut must land means every file exercises ufbxi_clean_string_utf8() on the truncated
	// tail, and the parity of the digit count decides whether the final byte is a lone C3 (rewritten
	// as '?') or a complete pair (kept) -- both are compared as bytes.
	n = 0;
	while (n < 215) { k_long_split[n++] = 'x'; }
	while (n + 2 <= sizeof(k_long_split) - 1) {
		k_long_split[n++] = (char) 0xc3;
		k_long_split[n++] = (char) 0xa9;
	}
	k_long_split[n] = '\0';
	k_long_split_len = n;
}

// Deliberately odd numeric shapes: `value.x` alone (must fill `value_int`), `value_int` alone (must
// fill `value.x`), both (neither write fires), neither, and non-integer/negative/extreme values.
typedef struct { double x, y, z, w; int64_t i; } num_shape;

static const num_shape k_shapes[] = {
	{ 0.0, 0.0, 0.0, 0.0, 0 },
	{ 3.5, 0.0, 0.0, 0.0, 0 },
	{ 0.0, 0.0, 0.0, 0.0, 7 },
	{ 2.0, 4.0, 6.0, 8.0, 5 },
	{ -1.25, 0.0, 0.0, 0.0, 0 },
	{ 0.5, -0.25, 1e300, -1e-300, 0 },
	{ -0.0, 0.0, 0.0, 0.0, -9 },
	{ 1e18, 0.0, 0.0, 0.0, 0 },
};
#define NUM_SHAPES (sizeof(k_shapes)/sizeof(k_shapes[0]))

// Real element ids where possible (so ufbx_evaluate_scene() consumes the override), plus out-of-range
// ones for the miss path. Slots that would wrap around `scene->elements[]` become synthetic instead,
// so a variant's overrides never collide just because the file has few elements: a collision must
// always come from the prop_kind that is designed to produce one.
static uint32_t pick_element_id(const ufbx_scene *scene, int slot)
{
	if (slot >= 100) return (uint32_t) (0xffff0000u + (unsigned) slot);
	size_t n = scene->elements.count;
	if (n == 0 || (size_t) slot >= n) return (uint32_t) (0xffff0000u + (unsigned) slot);
	return scene->elements.data[(size_t) slot]->element_id;
}

static uint32_t pick_node_id(const ufbx_scene *scene, int slot)
{
	if (slot >= 100) return (uint32_t) (0xffff0000u + (unsigned) slot);
	if (scene->nodes.count == 0) return (uint32_t) slot + 1u;
	size_t ix = (size_t) slot % scene->nodes.count;
	return scene->nodes.data[ix]->element.element_id;
}

// prop_kind: which name/value pattern the prop overrides of a variant use. Every one of them is
// emitted as `Ip` records, so the harness never has to know what these numbers mean.
enum {
	PK_GLOBAL = 0,        // all names present in ufbxi_strings[]
	PK_MIXED,             // alternating global / arena-copy names
	PK_EMPTY,             // empty names on distinct elements (the `prev_name` short-circuit)
	PK_DUP_SAME,          // same element + same name twice -> "Duplicate override"
	PK_DUP_ELEM,          // same name, distinct elements -> passes
	PK_BAD_NAME,          // invalid UTF-8 name -> "Invalid UTF-8"
	PK_BOUNDARY,          // table-boundary names (before/after/adjacent)
	PK_DUP_LONG,          // the >255-byte name twice -> truncated error info
	PK_LONG_OK,           // the >255-byte name once -> arena copy, no error
	PK_SHAPES,            // cycles k_shapes (value.x <-> value_int cross-fill)
	PK_STRINGS,           // cycles k_vs (non-empty value_str arena copy)
	PK_NUL_NAME,          // NUL inside the name with an explicit length -> UTF-8 error
	PK_SPLIT_LONG,        // the long name whose UTF-8 tail is cut by the info buffer
	PK_WIDE,              // everything at once, distinct elements -> passes
	PK_BAD_STR,           // invalid UTF-8 in value_str -> "Invalid UTF-8"
	PK_NUL_STR,           // NUL inside value_str with an explicit length -> UTF-8 error
};
typedef struct {
	int num_layers;        // -1 = all scene layers, 0 = none
	int weights_mode;      // 0 none, 1 matching count, 2 count+1 (mismatch), 3 zeros
	int num_props;         // 0 = none
	int prop_kind;
	int num_transforms;    // 0 = none
	int transform_kind;    // 0 distinct ids, 1 tied ids, 2 out-of-range ids
	int null_opts;         // pass a NULL `ufbx_anim_opts*`
	int ignore_conn;
	int layer_oob;         // append one layer_id past the end of scene->anim_layers[]
} variant_desc;

static const variant_desc k_variants[] = {
	/*  0 */ {  0, 0,  0, PK_GLOBAL,     0, 0, 0, 0, 0 },
	/*  1 */ { -1, 0,  0, PK_GLOBAL,     0, 0, 1, 0, 0 },   // NULL opts: the all-zero struct
	/*  2 */ {  1, 0,  0, PK_GLOBAL,     0, 0, 0, 0, 0 },
	/*  3 */ { -1, 0,  0, PK_GLOBAL,     0, 0, 0, 0, 0 },   // every scene layer
	/*  4 */ {  2, 1,  0, PK_GLOBAL,     0, 0, 0, 0, 0 },   // layers + matching weights
	/*  5 */ {  2, 2,  0, PK_GLOBAL,     0, 0, 0, 0, 0 },   // one weight too many -> error
	/*  6 */ {  0, 2,  0, PK_GLOBAL,     0, 0, 0, 0, 0 },   // weights with no layers -> error
	/*  7 */ { -1, 3,  0, PK_GLOBAL,     0, 0, 0, 0, 0 },   // weights all zero
	/*  8 */ { -1, 1,  0, PK_GLOBAL,     0, 0, 0, 1, 0 },   // ignore_connections
	/*  9 */ { -1, 0,  0, PK_GLOBAL,     0, 0, 0, 0, 1 },   // layer_ids out of bounds -> error
	/* 10 */ {  0, 0,  8, PK_GLOBAL,     0, 0, 0, 0, 0 },
	/* 11 */ {  2, 1, 10, PK_MIXED,      0, 0, 0, 0, 0 },
	/* 12 */ {  0, 0,  3, PK_EMPTY,      0, 0, 0, 0, 0 },
	/* 13 */ {  0, 0,  4, PK_DUP_SAME,   0, 0, 0, 0, 0 },
	/* 14 */ {  1, 0,  6, PK_DUP_ELEM,   0, 0, 0, 0, 0 },
	/* 15 */ {  0, 0,  2, PK_BAD_NAME,   0, 0, 0, 0, 0 },
	/* 16 */ {  0, 0,  1, PK_BAD_NAME,   0, 0, 0, 1, 0 },
	/* 17 */ {  1, 1,  6, PK_BOUNDARY,   0, 0, 0, 0, 0 },
	/* 18 */ {  0, 0,  2, PK_DUP_LONG,   0, 0, 0, 0, 0 },
	/* 19 */ {  0, 0,  1, PK_LONG_OK,    0, 0, 0, 0, 0 },
	/* 20 */ {  1, 0,  8, PK_SHAPES,     0, 0, 0, 0, 0 },
	/* 21 */ {  0, 0,  5, PK_STRINGS,    0, 0, 0, 0, 0 },
	/* 22 */ {  0, 0,  1, PK_NUL_NAME,   0, 0, 0, 0, 0 },
	/* 23 */ {  0, 0,  2, PK_SPLIT_LONG, 0, 0, 0, 0, 0 },
	/* 24 */ {  0, 0,  0, PK_GLOBAL,     4, 0, 0, 0, 0 },   // transform overrides, distinct ids
	/* 25 */ {  0, 0,  0, PK_GLOBAL,     6, 1, 0, 0, 0 },   // tied node ids (unstable permutation)
	/* 26 */ {  0, 0,  0, PK_GLOBAL,     3, 2, 0, 1, 0 },   // out-of-range ids + ignore_connections
	/* 27 */ {  0, 0,  6, PK_BAD_STR,    0, 0, 0, 0, 0 },
	/* 28 */ {  0, 0,  4, PK_NUL_STR,    0, 0, 0, 0, 0 },
	/* 29 */ {  2, 1, 12, PK_WIDE,       8, 1, 0, 0, 0 },   // layers + weights + props + transforms
	/* 30 */ {  3, 3, 20, PK_WIDE,       6, 0, 0, 1, 0 },   // wide sweep, zero weights
	/* 31 */ { -1, 1, 24, PK_MIXED,     16, 1, 0, 0, 0 },   // the largest combination
};
#define NUM_VARIANTS (sizeof(k_variants)/sizeof(k_variants[0]))

static const int k_prop_max = MAX_PROPS;

static lit name_for(const variant_desc *v, int ix)
{
	switch (v->prop_kind) {
	case PK_GLOBAL: return k_nm[ix % 8];
	case PK_MIXED: return (ix & 1) ? k_nm[8 + (ix % 3)] : k_nm[ix % 8];
	case PK_EMPTY: return k_nm_empty;
	case PK_DUP_SAME: return k_nm[1 + (ix / 2) % 4];
	case PK_DUP_ELEM: return k_nm[1 + (ix / 2) % 4];
	case PK_BAD_NAME: return k_nm_bad;
	case PK_BOUNDARY: return k_nm[9 + (ix % 6)];
	case PK_DUP_LONG: { lit l = { k_long_ascii, k_long_ascii_len }; return l; }
	case PK_LONG_OK: { lit l = { k_long_ascii, k_long_ascii_len }; return l; }
	case PK_SHAPES: return k_nm[ix % 8];
	case PK_STRINGS: return k_nm[ix % 8];
	case PK_NUL_NAME: return k_nm_nul;
	case PK_SPLIT_LONG: { lit l = { k_long_split, k_long_split_len }; return l; }
	case PK_WIDE: return k_nm[ix % NUM_NM];
	case PK_BAD_STR: return k_nm[ix % 8];
	case PK_NUL_STR: return k_nm[8 + (ix % 2)];
	default: return k_nm[0];
	}
}

static uint32_t element_for(const variant_desc *v, const ufbx_scene *scene, int ix)
{
	switch (v->prop_kind) {
	case PK_DUP_SAME: return pick_element_id(scene, 1);
	// Same repeated names as PK_DUP_SAME but one element each: the interning pass aliases the
	// consecutive equal names to a single instance, yet the duplicate check must NOT fire because
	// `element_id` differs. This is the case that makes C's pointer comparison sound.
	case PK_DUP_ELEM: return pick_element_id(scene, ix + 1);
	case PK_DUP_LONG: return pick_element_id(scene, 1);
	// A 10-digit synthetic id, so the "element %u prop \"%s\"" prefix is 25 bytes long and
	// ufbxi_vsnprintf() cuts the 2-byte UTF-8 run of k_long_split mid-sequence -- which is what
	// makes ufbxi_clean_string_utf8() rewrite the tail as '?'.
	case PK_SPLIT_LONG: return pick_element_id(scene, 100);
	case PK_EMPTY: return pick_element_id(scene, ix);
	case PK_WIDE: return pick_element_id(scene, ix % 2 ? ix : 100 + ix);
	default: return pick_element_id(scene, ix + 1);
	}
}

// The value_str choices. k_vs[2] (invalid UTF-8) and k_vs[3] (embedded NUL, explicit length) are
// rejected by ufbxi_check_string(), so they only appear in the kinds that expect an error; the
// passing kinds use k_vs[1]/k_vs[4] to reach the `value_str.length > 0` arena-copy branch.
static lit value_str_for(const variant_desc *v, int ix)
{
	switch (v->prop_kind) {
	case PK_STRINGS: return (ix & 1) ? k_vs[4] : k_vs[0];
	case PK_BAD_STR: return k_vs[2];
	case PK_NUL_STR: return k_vs[3];
	case PK_WIDE: return (ix & 1) ? k_vs[1] : k_vs[4];
	default: break;
	}
	if (ix == 3) return k_vs[4];
	return k_vs[0];
}

static const num_shape *shape_for(const variant_desc *v, int ix)
{
	if (v->prop_kind == PK_SHAPES) return &k_shapes[ix % NUM_SHAPES];
	if (v->prop_kind == PK_WIDE) return &k_shapes[(ix + 2) % NUM_SHAPES];
	return &k_shapes[(ix + 1) % NUM_SHAPES];
}

// -- Records

// The caller's `ufbx_anim_opts` as data: everything a `create_anim` call consumed, in caller order.
static void emit_inputs(int fi, int vi, const ufbx_anim_opts *o, int null_opts)
{
	printf("I %d %d %d %d %zu %zu %zu %zu", fi, vi, null_opts, o->ignore_connections ? 1 : 0,
		o->layer_ids.count, o->override_layer_weights.count,
		o->prop_overrides.count, o->transform_overrides.count);
	printf("\n");

	for (size_t i = 0; i < o->layer_ids.count; i++) {
		printf("Ia %d %d %zu %u\n", fi, vi, i, o->layer_ids.data[i]);
	}
	for (size_t i = 0; i < o->override_layer_weights.count; i++) {
		printf("Iw %d %d %zu", fi, vi, i);
		pd(o->override_layer_weights.data[i]);
		printf("\n");
	}
	for (size_t i = 0; i < o->prop_overrides.count; i++) {
		const ufbx_prop_override_desc *d = &o->prop_overrides.data[i];
		printf("Ip %d %d %zu %u", fi, vi, i, d->element_id);
		pstr(d->prop_name.data, d->prop_name.length);
		pstr(d->value_str.data, d->value_str.length);
		pd(d->value.x); pd(d->value.y); pd(d->value.z); pd(d->value.w);
		printf(" %lld", (long long) d->value_int);
		printf("\n");
	}
	for (size_t i = 0; i < o->transform_overrides.count; i++) {
		const ufbx_transform_override *d = &o->transform_overrides.data[i];
		printf("It %d %d %zu %u", fi, vi, i, d->node_id);
		pd(d->transform.translation.x); pd(d->transform.translation.y); pd(d->transform.translation.z);
		pd(d->transform.rotation.x); pd(d->transform.rotation.y);
		pd(d->transform.rotation.z); pd(d->transform.rotation.w);
		pd(d->transform.scale.x); pd(d->transform.scale.y); pd(d->transform.scale.z);
		printf("\n");
	}
}

static uint64_t hash_opts(uint64_t h, const ufbx_anim_opts *o)
{
	h = hh_u64(h, (uint64_t) o->layer_ids.count);
	for (size_t i = 0; i < o->layer_ids.count; i++) h = hh_u32(h, o->layer_ids.data[i]);
	h = hh_u64(h, (uint64_t) o->override_layer_weights.count);
	for (size_t i = 0; i < o->override_layer_weights.count; i++) h = hh_d(h, o->override_layer_weights.data[i]);
	h = hh_u64(h, (uint64_t) o->prop_overrides.count);
	for (size_t i = 0; i < o->prop_overrides.count; i++) {
		const ufbx_prop_override_desc *d = &o->prop_overrides.data[i];
		h = hh_u32(h, d->element_id);
		h = hh_u64(h, (uint64_t) d->prop_name.length);
		if (d->prop_name.data) h = hh_bytes(h, d->prop_name.data, d->prop_name.length);
		h = hh_d(h, d->value.x); h = hh_d(h, d->value.y); h = hh_d(h, d->value.z); h = hh_d(h, d->value.w);
		h = hh_i64(h, d->value_int);
		h = hh_u64(h, (uint64_t) d->value_str.length);
		if (d->value_str.data) h = hh_bytes(h, d->value_str.data, d->value_str.length);
	}
	h = hh_u64(h, (uint64_t) o->transform_overrides.count);
	for (size_t i = 0; i < o->transform_overrides.count; i++) {
		const ufbx_transform_override *d = &o->transform_overrides.data[i];
		h = hh_u32(h, d->node_id);
		h = hh_d(h, d->transform.translation.x); h = hh_d(h, d->transform.translation.y);
		h = hh_d(h, d->transform.translation.z);
		h = hh_d(h, d->transform.rotation.x); h = hh_d(h, d->transform.rotation.y);
		h = hh_d(h, d->transform.rotation.z); h = hh_d(h, d->transform.rotation.w);
		h = hh_d(h, d->transform.scale.x); h = hh_d(h, d->transform.scale.y);
		h = hh_d(h, d->transform.scale.z);
	}
	return hh_u32(h, o->ignore_connections ? 1u : 0u);
}

static void dump_anim(int fi, int vi, const ufbx_anim *anim)
{
	for (size_t i = 0; i < anim->layers.count; i++) {
		const ufbx_anim_layer *layer = anim->layers.data[i];
		printf("L %d %d %zu", fi, vi, i);
		if (!layer) {
			printf(" -1 0 0 -");
			pd(0.0);
			printf("\n");
			continue;
		}
		printf(" %d %u", (int) layer->element.typed_id, layer->element.element_id);
		pstr(layer->element.name.data, layer->element.name.length);
		pd(layer->weight);
		printf("\n");
	}

	for (size_t i = 0; i < anim->override_layer_weights.count; i++) {
		printf("W %d %d %zu", fi, vi, i);
		pd(anim->override_layer_weights.data[i]);
		printf("\n");
	}

	for (size_t i = 0; i < anim->prop_overrides.count; i++) {
		const ufbx_prop_override *o = &anim->prop_overrides.data[i];
		printf("P %d %d %zu %u %u", fi, vi, i, o->element_id, o->_internal_key);
		pstr(o->prop_name.data, o->prop_name.length);
		pd(o->value.x); pd(o->value.y); pd(o->value.z); pd(o->value.w);
		printf(" %lld", (long long) o->value_int);
		pstr(o->value_str.data, o->value_str.length);
		printf("\n");
	}

	for (size_t i = 0; i < anim->transform_overrides.count; i++) {
		const ufbx_transform_override *o = &anim->transform_overrides.data[i];
		printf("T %d %d %zu %u", fi, vi, i, o->node_id);
		pd(o->transform.translation.x); pd(o->transform.translation.y); pd(o->transform.translation.z);
		pd(o->transform.rotation.x); pd(o->transform.rotation.y);
		pd(o->transform.rotation.z); pd(o->transform.rotation.w);
		pd(o->transform.scale.x); pd(o->transform.scale.y); pd(o->transform.scale.z);
		printf("\n");
	}
}

// The arrays the caller's opts point at. One set per call, refilled every variant.
static uint32_t s_layer_ids[MAX_LAYERS];
static double s_weights[MAX_WEIGHTS];
static ufbx_prop_override_desc s_props[MAX_PROPS];
static ufbx_transform_override s_transforms[MAX_TRANSFORMS];

static void fill_opts(const variant_desc *v, const ufbx_scene *scene, ufbx_anim_opts *opts)
{
	memset(opts, 0, sizeof(*opts));
	memset(s_layer_ids, 0, sizeof(s_layer_ids));
	memset(s_weights, 0, sizeof(s_weights));
	memset(s_props, 0, sizeof(s_props));
	memset(s_transforms, 0, sizeof(s_transforms));

	const int num_scene_layers = (int) scene->anim_layers.count;

	int n_layers = v->num_layers < 0 ? num_scene_layers : v->num_layers;
	if (n_layers > num_scene_layers) n_layers = num_scene_layers;
	if (n_layers > MAX_LAYERS) n_layers = MAX_LAYERS;
	for (int i = 0; i < n_layers; i++) s_layer_ids[i] = (uint32_t) i;
	int total_ids = n_layers;
	if (v->layer_oob && total_ids < MAX_LAYERS) {
		// One index past the end of scene->anim_layers[], so the `index < anim_layers.count` check
		// at 26586 is the failure -- after any valid ids ahead of it have already been resolved.
		s_layer_ids[total_ids] = (uint32_t) (num_scene_layers + 3);
		total_ids++;
	}
	if (total_ids > 0) {
		opts->layer_ids.data = s_layer_ids;
		opts->layer_ids.count = (size_t) total_ids;
	}

	int n_weights = 0;
	if (v->weights_mode == 1 || v->weights_mode == 3) {
		n_weights = n_layers;
	} else if (v->weights_mode == 2) {
		// One more weight than layers: the count check at 26578 must fire. With zero layers this is
		// still a mismatch (0 != 1), which is the same branch from the other side.
		n_weights = n_layers + 1;
	}
	if (n_weights > MAX_WEIGHTS) n_weights = MAX_WEIGHTS;
	if (n_weights > 0) {
		for (int i = 0; i < n_weights; i++) {
			s_weights[i] = v->weights_mode == 3 ? 0.0 : 0.25 + 0.5 * (double) i;
		}
		opts->override_layer_weights.data = s_weights;
		opts->override_layer_weights.count = (size_t) n_weights;
	}

	int n_props = v->num_props;
	if (n_props > k_prop_max) n_props = k_prop_max;
	for (int i = 0; i < n_props; i++) {
		ufbx_prop_override_desc *d = &s_props[i];
		lit name = name_for(v, i);
		lit str = value_str_for(v, i);
		const num_shape *s = shape_for(v, i);
		d->element_id = element_for(v, scene, i);
		d->prop_name.data = name.data;
		d->prop_name.length = name.length;
		d->value_str.data = str.data;
		d->value_str.length = str.length;
		d->value.x = s->x; d->value.y = s->y; d->value.z = s->z; d->value.w = s->w;
		d->value_int = s->i;
	}
	if (n_props > 0) {
		opts->prop_overrides.data = s_props;
		opts->prop_overrides.count = (size_t) n_props;
	}

	int n_transforms = v->num_transforms;
	if (n_transforms > MAX_TRANSFORMS) n_transforms = MAX_TRANSFORMS;
	for (int i = 0; i < n_transforms; i++) {
		ufbx_transform_override *d = &s_transforms[i];
		if (v->transform_kind == 1) {
			d->node_id = pick_node_id(scene, (i / 2) + 1);
		} else if (v->transform_kind == 2) {
			d->node_id = pick_node_id(scene, 100 + i);
		} else {
			d->node_id = pick_node_id(scene, i + 1);
		}
		d->transform.translation.x = 1.5 + (double) i;
		d->transform.translation.y = -2.25;
		d->transform.translation.z = 0.0;
		d->transform.rotation.x = 0.0;
		d->transform.rotation.y = 0.7071067811865476;
		d->transform.rotation.z = 0.0;
		d->transform.rotation.w = 0.7071067811865475;
		d->transform.scale.x = 2.0;
		d->transform.scale.y = 0.5;
		d->transform.scale.z = 1.0 - 0.125 * (double) i;
	}
	if (n_transforms > 0) {
		opts->transform_overrides.data = s_transforms;
		opts->transform_overrides.count = (size_t) n_transforms;
	}

	opts->ignore_connections = v->ignore_conn != 0;
}

static void dump_variant(int fi, const ufbx_scene *scene, int vi, const double *times, size_t num_times)
{
	const variant_desc *v = &k_variants[vi];

	static ufbx_anim_opts opts;
	if (v->null_opts) {
		// The callee copies nothing (31208-31210), so the observable opts are the all-zero struct.
		memset(&opts, 0, sizeof(opts));
	} else {
		fill_opts(v, scene, &opts);
	}

	// `null_opts` is the NULL-pointer path: the input records describe the zeroed struct the callee
	// actually sees, and the harness passes a NULL `UfbxAnimOpts` to match.
	const ufbx_anim_opts *user_opts = v->null_opts ? NULL : &opts;
	emit_inputs(fi, vi, &opts, v->null_opts);

	uint64_t opts_before = hash_opts(FNV_BASIS, &opts);

	ufbx_error error;
	memset(&error, 0, sizeof(error));
	ufbx_anim *anim = ufbx_create_anim(scene, user_opts, &error);

	int ok = anim != NULL;
	printf("A %d %d %d %d", fi, vi, ok, (int) error.type);
	pstr(error.description.data, error.description.length);
	printf(" %zu ", error.info_length);
	if (error.info_length == 0) { printf("-"); }
	else { for (size_t i = 0; i < error.info_length; i++) printf("%02x", (unsigned char) error.info[i]); }
	printf(" %zu %zu %zu %zu %d %d", anim ? anim->layers.count : 0,
		anim ? anim->override_layer_weights.count : 0,
		anim ? anim->prop_overrides.count : 0,
		anim ? anim->transform_overrides.count : 0,
		anim ? (anim->custom ? 1 : 0) : 0,
		anim ? (anim->ignore_connections ? 1 : 0) : 0);
	printf("\n");

	uint64_t opts_after = hash_opts(FNV_BASIS, &opts);
	printf("O %d %d", fi, vi);
	pz(opts_before); pz(opts_after);
	printf(" %d", opts_before == opts_after ? 1 : 0);
	printf("\n");

	if (!anim) return;

	dump_anim(fi, vi, anim);

	// NULL evaluate opts (the all-zero struct): `ufbxi_evaluate_skinning()` is an S4c body the port
	// has not reached yet, and opts otherwise have no effect on what these records pin. Same choice
	// as tools/s4b_oracle.c, whose `V` records are the golden path.
	for (size_t ti = 0; ti < num_times; ti++) {
		ufbx_error ee;
		ufbx_scene *state = ufbx_evaluate_scene(scene, anim, times[ti], NULL, &ee);
		if (!state) {
			printf("V %d %d %zu FAIL\n", fi, vi, ti);
			continue;
		}
		printf("V %d %d %zu", fi, vi, ti);
		pz(ufbxt_hash_scene(state, NULL));
		printf("\n");
		ufbx_free_scene(state);
	}
}

int main(int argc, char **argv)
{
	if (argc < 2) {
		fprintf(stderr, "usage: create_anim_oracle.exe (<file.fbx>)...\n");
		return 2;
	}

	init_long_names();

	for (int ai = 1; ai < argc; ai++) {
		const int fi = ai - 1;
		ufbx_error error;
		memset(&error, 0, sizeof(error));
		ufbx_scene *scene = load_golden_scene(argv[ai], &error);
		if (!scene) {
			printf("S %d 0 0 0 0 FAIL\n", fi);
			fprintf(stderr, "load failed: %s: %s\n", argv[ai], error.description.data);
			continue;
		}

		printf("S %d %zu %zu %zu %d\n", fi, scene->nodes.count, scene->elements.count,
			scene->anim_layers.count, scene->anim ? 1 : 0);

		static double times[NUM_TIMES];
		size_t num_times = build_times(scene, times, NUM_TIMES);
		for (size_t ti = 0; ti < num_times; ti++) {
			printf("U %d %zu", fi, ti);
			pd(times[ti]);
			printf("\n");
		}

		for (int vi = 0; vi < (int) NUM_VARIANTS; vi++) {
			dump_variant(fi, scene, vi, times, num_times);
		}

		ufbx_free_scene(scene);
	}
	return 0;
}
