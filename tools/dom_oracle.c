// C reference oracle for the DOM layer: the binary/ASCII node builders
// (`ufbxi_binary_parse_node` ufbx.c:8959-9395, `ufbxi_get_dom_node_imp`/
// `ufbxi_retain_dom_node` ufbx.c:10699-10760) plus the public `ufbx_dom_*` accessors.
//
// It drives the *public* loader with `retain_dom = true` and dumps the resulting
// `ufbx_dom_node` tree, so it pins down exactly what the ported DOM has to produce --
// including whether a name came back as one of the `ufbxi_*` constants (pointer identity),
// which is the part the C# port models with `UfbxiPtrIdTable`.
//
//   zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx tools/dom_oracle.c -o tools/dom_oracle.exe
//   cd C:/Workspace/_analyze_ufbx && C:/Workspace/ufbx-cs/tools/dom_oracle.exe > C:/Workspace/ufbx-cs/tools/dom_oracle.txt
//
// Run from the ufbx checkout root so the `data/...` paths resolve (argv can override with
// an explicit file list).
//
// Output grammar (one record per line, space-separated; `<hex>` is lowercase nibbles):
//   S <file> <ok> <err_type> <desc_len> <desc_in_info> <desc-hex> <info_len> <info_digest> <info-hex>
//                                        load summary; fixed zeros when it succeeded, so a
//                                        stale `ufbx_error` cannot leak into the comparison
//   N <file> <node> <depth> <parent> <num_children> <name_len> <name_static> <name-hex>
//                                        one DOM node, pre-order; `name_static` is 1 when
//                                        name.data is a .rodata constant (`ufbxi_strings[]`
//                                        or `ufbxi_empty_char`) rather than a pooled copy
//   V <file> <node> <index> <type> <int-hex16> <float-bits-hex16> <str_len> <str_digest> <str-hex> <blob_size> <blob_digest> <blob-hex>
//                                        one ufbx_dom_value, all five fields. For
//                                        ARRAY_BLOB the blob field carries the *logical*
//                                        payload ([8-byte LE size][bytes] per element), not
//                                        C's pointer-backed `ufbx_string[]` bytes -- see
//                                        emit_blob_array_payload().
//   D <file> <node> <is_array> <array_size> <i32_cnt> <i32_digest> <i64_cnt> <i64_digest> <f32_cnt> <f32_digest> <f64_cnt> <f64_digest> <blob_cnt> <blob_digest>
//                                        the ufbx_dom_* accessor contract for that node
//   T <file> <num_nodes> <num_values> <num_static_names>
//                                        per-file totals, so a truncated walk is loud
//
// Strings/blobs longer than 32 bytes are reported as length + FNV-1a-64 digest only (a
// 12MB embedded texture is a `BinaryData` blob); short ones get their bytes inline so the
// interesting boundaries stay readable.
//
// Big-endian and non-UTF-8 names are in the corpus on purpose: byte-exactness of the raw
// string model is what the port is being checked on.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ufbx.c"

static const char *g_default_files[] = {
	// Very old / unsupported versions: header layout and NULL-sentinel handling differ.
	"data/max6_teapot_3000_binary.fbx",
	"data/max7_cube_5000_binary.fbx",
	"data/synthetic_unsupported_cube_2000_binary.fbx",
	"data/synthetic_unsupported_cube_8000_ascii.fbx",
	// ASCII baselines.
	"data/blender_279_ball_6100_ascii.fbx",
	"data/maya_cube_7100_ascii.fbx",
	"data/synthetic_comment_cube_7500_ascii.fbx",
	"data/synthetic_binary_props_7500_ascii.fbx",
	"data/synthetic_base64_parse_7700_ascii.fbx",
	"data/synthetic_cursed_geometry_7700_ascii.fbx",
	// Binary: pre-7500 32-bit header vs 7500+ 64-bit header.
	"data/blender_279_ball_7400_binary.fbx",
	"data/blender_279_default_7400_binary.fbx",
	"data/maya_cube_7100_binary.fbx",
	"data/synthetic_color_suzanne_7500_binary.fbx",
	// Big-endian file: exercises ufbxi_swap_endian on both header and array data.
	"data/maya_cube_big_endian_7100_binary.fbx",
	// Compressed content (the DOM sits behind inflate) plus truncation inside a stream.
	"data/synthetic_truncated_compressed_fail_7400_binary.fbx",
	// Names that must resolve to `ufbxi_*` constants and ones that must not.
	"data/synthetic_aβカ😂_7500_ascii.fbx",
	"data/blender_279_unicode_7400_binary.fbx",
	// Malformed input: the DOM has to be built as far as it can be, then error out.
	"data/synthetic_broken_cluster_7500_ascii.fbx",
	"data/synthetic_broken_filename_7500_ascii.fbx",
	"data/synthetic_broken_material_6100_ascii.fbx",
	"data/synthetic_bad_inf_nan_7700_ascii.fbx",
	"data/synthetic_cube_nan_6100_ascii.fbx",
	"data/synthetic_legacy_nonzero_material_5800_ascii.fbx",
	"data/synthetic_tcdefinition_0_old_header_7100_ascii.fbx",
	// Large real file with deep nesting and big arrays.
	"data/blender_293_barbarian_7400_binary.fbx",

	// -- Widening set: the curated files above cover the interesting *code paths*, but only 26
	// trees. These are every 15th entry of `ls data/*.fbx` with the curated ones removed -- a
	// deterministic, name-order spread over exporters (blender/max/maya/motionbuilder/zbrush/
	// casegen), versions (6100/7100/7400/7500/7700) and both encodings -- to widen the *DOM
	// shapes* seen: NURBS and curve nodes, audio, anim layers, UV/colour sets, big-endian 7500,
	// `_combined` files, empty elements, unicode names and bad-inf/nan failures. Appended after
	// the originals so the `fi` index of the first 26 records cannot shift.
	"data/blender340_tangent_sign_7400_binary.fbx",
	"data/blender_279_nested_meshes_7400_binary.fbx",
	"data/blender_293_half_skinned_7400_binary.fbx",
	"data/blender_300_ngon_irregular_7400_binary.fbx",
	"data/casegen_rotation_order_7500_binary.fbx",
	"data/max2009_cube_anim_6100_ascii.fbx",
	"data/max_curve_line_7500_binary.fbx",
	"data/max_geometry_transform_types_6100_binary.fbx",
	"data/max_nurbs_curve_rational_7500_binary.fbx",
	"data/max_physical_material_textures_6100_binary.fbx",
	"data/max_tangent_sign_7700_binary.fbx",
	"data/maya_absolute_texture_7700_ascii.fbx",
	"data/maya_anim_interpolation_6100_ascii.fbx",
	"data/maya_anim_layers_over_acc_orders_7500_binary.fbx",
	"data/maya_audio_7700_ascii.fbx",
	"data/maya_blend_inbetween_7500_ascii.fbx",
	"data/maya_camera_light_axes_y_up_6100_binary.fbx",
	"data/maya_cone_7500_ascii.fbx",
	"data/maya_cube_big_endian_7500_binary.fbx",
	"data/maya_dq_weights_7500_binary.fbx",
	"data/maya_game_sausage_6100_binary_combined.fbx",
	"data/maya_huge_stepped_tangents_7700_ascii.fbx",
	"data/maya_keyframe_offset_7700_ascii.fbx",
	"data/maya_long_keyframes_7700_binary.fbx",
	"data/maya_notes_6100_ascii.fbx",
	"data/maya_nurbs_curve_multiplicity_7500_ascii.fbx",
	"data/maya_osl_properties_6100_ascii.fbx",
	"data/maya_polygon_hole_6100_ascii.fbx",
	"data/maya_rotation_order_7500_ascii.fbx",
	"data/maya_slime_7500_binary.fbx",
	"data/maya_tangent_clamped_7700_ascii.fbx",
	"data/maya_texture_blend_modes_7500_ascii.fbx",
	"data/maya_transformed_skin_7700_ascii.fbx",
	"data/maya_uv_and_color_sets_6100_ascii.fbx",
	"data/maya_uv_sets_7500_binary.fbx",
	"data/motionbuilder_cube_7700_binary.fbx",
	"data/motionbuilder_smoothing_7700_ascii.fbx",
	"data/motionbuilder_tangent_spline_7700_ascii.fbx",
	"data/synthetic_bad_inf_nan_fail_7500_ascii.fbx",
	"data/synthetic_empty_elements_7500_ascii.fbx",
	"data/synthetic_missing_mapping_7500_ascii.fbx",
	"data/synthetic_parent_directory_7700_ascii.fbx",
	"data/synthetic_tcdefinition_0_7700_ascii.fbx",
	"data/synthetic_unicode_error_identity_6100_ascii.fbx",
	"data/zbrush_d20_selection_set_6100_ascii.fbx",
	// String-pool edge cases: names that collide in the pool hash and in the id table, plus a
	// file whose embedded thumbnails go through the ASCII base64 array path.
	"data/synthetic_unicode_7500_binary.fbx",
	"data/synthetic_string_collision_7500_ascii.fbx",
	"data/synthetic_id_collision_7500_ascii.fbx",
	"data/motionbuilder_thumbnail_7700_ascii.fbx",
};

#define NUM_DEFAULT_FILES ((int)(sizeof(g_default_files)/sizeof(g_default_files[0])))

static uint64_t fnv64(const void *p, size_t n)
{
	const uint8_t *b = (const uint8_t*)p;
	uint64_t h = 0xcbf29ce484222325ull;
	for (size_t i = 0; i < n; i++) h = (h ^ b[i]) * 0x00000100000001b3ull;
	return h;
}

static char *emit_hex_into(char *out, const void *p, size_t n)
{
	const uint8_t *b = (const uint8_t*)p;
	for (size_t i = 0; i < n; i++) {
		sprintf(out + i * 2, "%02x", b[i]);
	}
	out[n * 2] = 0;
	return out;
}

static void emit_hex(const void *p, size_t n)
{
	const uint8_t *b = (const uint8_t*)p;
	for (size_t i = 0; i < n; i++) printf("%02x", b[i]);
}

// Hex scratch shared by emit_hex_into/emit_payload -- one caller at a time, like the C
// buffers this oracle style already uses. `emit_payload` needs it because its hex field sits
// mid-record, so the bytes must be emitted as one space-free token.
static char hex_buf[256];

// Emit `len digest hex-or-placeholder `: bytes inline up to 32, digest-only beyond (see
// header note). A payload with no bytes to show -- empty, NULL, or longer than 32 -- prints
// the `-` placeholder instead, and the field always ends in a space: these tokens sit
// mid-record, so printing nothing (or nothing between them) would merge two columns.
static void emit_payload(const char *data, size_t len)
{
	printf("%zu %016llx ", (size_t)len, (unsigned long long)fnv64(data, len));
	if (data && len > 0 && len <= 32) printf("%s ", emit_hex_into(hex_buf, data, len)); else printf("- ");
}

// C: the image's .rodata string addresses, which the port maps to `UfbxiPtrIdTable.IsStatic`.
static bool is_static_ptr(const char *p)
{
	if (!p) return false;
	if (p == (const char*)&ufbxi_empty_char) return true;
	for (size_t i = 0; i < ufbxi_arraycount(ufbxi_strings); i++) {
		if (ufbxi_strings[i].data == p) return true;
	}
	return false;
}

static size_t g_num_nodes;
static size_t g_num_values;
static size_t g_num_static_names;

static void emit_list(const char *tag, ufbx_int32_list l)
{
	(void)tag;
	printf("%zu %016llx ", l.count, (unsigned long long)fnv64(l.data, l.count * sizeof(int32_t)));
}
static void emit_list64(ufbx_int64_list l)
{
	printf("%zu %016llx ", l.count, (unsigned long long)fnv64(l.data, l.count * sizeof(int64_t)));
}
static void emit_listf(ufbx_float_list l)
{
	printf("%zu %016llx ", l.count, (unsigned long long)fnv64(l.data, l.count * sizeof(float)));
}
static void emit_listd(ufbx_double_list l)
{
	printf("%zu %016llx ", l.count, (unsigned long long)fnv64(l.data, l.count * sizeof(double)));
}
static void emit_listb(ufbx_blob_list l)
{
	// Blob payloads are indirect, so hash each blob's size and bytes.
	uint64_t h = 0xcbf29ce484222325ull;
	for (size_t i = 0; i < l.count; i++) {
		h = fnv64(l.data[i].data, l.data[i].size) ^ (h * 0x00000100000001b3ull);
		h = (h ^ (uint64_t)l.data[i].size) * 0x00000100000001b3ull;
	}
	printf("%zu %016llx ", l.count, (unsigned long long)h);
}

static void emit_blob_array_payload(const ufbx_dom_value *v)
{
	// `value_blob.data` is the `ufbx_string[]` of the array; `sizeof(ufbx_blob) ==
	// sizeof(ufbx_string)`, which is what `ufbx_dom_as_blob_list()` relies on.
	const ufbx_blob *blobs = (const ufbx_blob*)v->value_blob.data;
	size_t count = v->value_blob.size / sizeof(ufbx_blob);
	size_t total = 0;
	for (size_t i = 0; i < count; i++) total += 8 + blobs[i].size;
	uint8_t *packed = (uint8_t*)malloc(total ? total : 1);
	size_t o = 0;
	for (size_t i = 0; i < count; i++) {
		uint64_t size = (uint64_t)blobs[i].size;
		for (int k = 0; k < 8; k++) packed[o + k] = (uint8_t)(size >> (8 * k));
		o += 8;
		if (blobs[i].data) memcpy(packed + o, blobs[i].data, (size_t)size);
		o += (size_t)size;
	}
	emit_payload((const char*)packed, total);
	free(packed);
}

static void walk(const ufbx_dom_node *n, uint32_t depth, size_t parent, int fi)
{
	size_t id = g_num_nodes++;
	bool static_name = is_static_ptr(n->name.data);
	if (static_name) g_num_static_names++;

	printf("N %d %zu %u %zu %zu %zu %d ", fi, id, depth, parent, n->children.count, n->name.length, static_name ? 1 : 0);
	if (n->name.data && n->name.length > 0 && n->name.length <= 32) emit_hex(n->name.data, n->name.length); else printf("-");
	printf("\n");

	for (size_t i = 0; i < n->values.count; i++) {
		const ufbx_dom_value *v = &n->values.data[i];
		g_num_values++;
		uint64_t float_bits;
		memcpy(&float_bits, &v->value_float, sizeof(float_bits));
		printf("V %d %zu %zu %d %016llx %016llx ", fi, id, i, (int)v->type,
			(unsigned long long)v->value_int, (unsigned long long)float_bits);
		emit_payload(v->value_str.data, v->value_str.length);
		if (v->type == UFBX_DOM_VALUE_ARRAY_BLOB) {
			// C stores the `ufbx_string[]` behind `value_blob.data` (reinterpreted as
			// `ufbx_blob[]`), so the raw bytes there are *heap pointers*: ASLR makes them
			// differ between runs (verified: 48 records changed across two runs). Emit the
			// logical payload instead -- each element as [8-byte LE size][bytes], which is
			// exactly the packing the port uses for ARRAY_BLOB blobs. The `size == count *
			// sizeof(ufbx_string)` relation stays pinned by this node's `D` blob-list count.
			emit_blob_array_payload(v);
		} else {
			emit_payload(v->value_blob.data, v->value_blob.size);
		}
		printf("\n");
	}

	// The public accessor contract, which the port must reproduce from its own DOM values.
	{
		bool is_array = ufbx_dom_is_array(n);
		size_t arr_size = is_array ? ufbx_dom_array_size(n) : 0;
		printf("D %d %zu %d %zu ", fi, id, is_array ? 1 : 0, arr_size);
		emit_list("i32", ufbx_dom_as_int32_list(n));
		emit_list64(ufbx_dom_as_int64_list(n));
		emit_listf(ufbx_dom_as_float_list(n));
		emit_listd(ufbx_dom_as_double_list(n));
		emit_listb(ufbx_dom_as_blob_list(n));
		printf("\n");
	}

	for (size_t i = 0; i < n->children.count; i++) {
		walk(n->children.data[i], depth + 1, id, fi);
	}
}

static void run_file(const char *path, int fi)
{
	ufbx_error error;
	memset(&error, 0, sizeof(error));

	ufbx_load_opts opts;
	memset(&opts, 0, sizeof(opts));
	opts.retain_dom = true;

	ufbx_scene *scene = ufbx_load_file(path, &opts, &error);

	g_num_nodes = 0;
	g_num_values = 0;
	g_num_static_names = 0;

	// One uniform record per file: `S <file> <ok> <err_type> <desc_len> <desc_in_info>
	// <desc-hex> <info_len> <info_digest> <info-hex>`. Success carries fixed zeros so a
	// stale `ufbx_error` (only written on failure) cannot leak into the comparison.
	{
		bool ok = scene != NULL;
		size_t desc_len = ok ? 0 : error.description.length;
		const char *desc = ok ? NULL : error.description.data;
		// `description` normally aliases `info`, so report whether it does: the port keeps the
		// same two-field shape and that aliasing is observable in `UfbxError`.
		int desc_in_info = ok ? 0 : (error.description.data == error.info ? 1 : 0);
		// Every field is a token: an empty or too-long payload prints `-` rather than nothing,
		// so a record with a 0-byte description cannot shift the columns after it.
		printf("S %d %d %d %zu %d", fi, ok, ok ? 0 : (int)error.type, desc_len, desc_in_info);
		if (!ok && desc && desc_len > 0 && desc_len <= 64) printf(" %s", emit_hex_into(hex_buf, desc, desc_len));
		else printf(" -");
		if (ok) {
			printf(" 0 0000000000000000 -");
		} else {
			printf(" %zu %016llx", error.info_length, (unsigned long long)fnv64(error.info, error.info_length));
			if (error.info_length > 0 && error.info_length <= 64) printf(" %s", emit_hex_into(hex_buf, error.info, error.info_length));
			else printf(" -");
		}
		printf("\n");
	}

	if (scene) {
		// `dom_root` is the synthetic root; its children are the top-level nodes.
		if (scene->dom_root) {
			walk(scene->dom_root, 0, (size_t)-1, fi);
		} else {
			printf("N %d 0 0 -1 0 0 1 -\n", fi);
		}
	}
	printf("T %d %zu %zu %zu\n", fi, g_num_nodes, g_num_values, g_num_static_names);

	if (scene) ufbx_free_scene(scene);
}

int main(int argc, char **argv)
{
	if (argc > 1) {
		for (int i = 1; i < argc; i++) run_file(argv[i], i - 1);
	} else {
		for (int i = 0; i < NUM_DEFAULT_FILES; i++) run_file(g_default_files[i], i);
	}
	return 0;
}
