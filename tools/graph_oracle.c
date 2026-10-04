// C reference oracle for the S1 pipeline of the ufbx -> C# port: the element / property /
// connection graph that `ufbxi_read_root()` (ufbx.c:15847) has built by the time the load
// spine reaches the scene-build seam.
//
// It is `tools/load_oracle.c` extended past the load spine. Instead of calling the whole
// `ufbxi_load()` (which would run `ufbxi_pre_finalize_scene()`, the first function of S3), it
// reproduces the two functions the port already replays and stops one call earlier:
//
//   ufbxi_load()      init prefix                 ufbx.c:25482-25607
//   ufbxi_load_imp()  up to the stop point        ufbx.c:25212-25327
//   STOP                                          before ufbxi_pre_finalize_scene() at 25328
//
// At that point every S1 data structure is still alive:
//   uc->tmp_element_ptrs      ufbx_element**   push order, index == element_id
//   uc->tmp_element_fbx_ids   uint64_t*        aligned with the pointers above
//   uc->num_elements          number of elements
//   uc->tmp_connections       ufbxi_tmp_connection { src, dst, src_prop, dst_prop } (ufbx.c:6300)
//   element->props            lives in uc->result, already sorted + deduplicated
// (`ufbxi_pre_finalize_scene` is what pops `tmp_element_ptrs`/`tmp_element_fbx_ids` at
// ufbx.c:18129/18165, so they must still hold the graph at the stop point.)
//
// Reconnaissance for this file (and the record grammar) is the "交接" section of
// COORDINATION.md. Version < 6000 takes `ufbxi_read_legacy_root()` (ufbx.c:16423), which S1
// does not cover: it is still *run* (so the marker is not guessed) but the graph records are
// omitted and the port side skips those files.
//
//   zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx tools/graph_oracle.c -o tools/graph_oracle.exe
//   cd C:/Workspace/_analyze_ufbx && C:/Workspace/ufbx-cs/tools/graph_oracle.exe > C:/Workspace/ufbx-cs/tools/graph_oracle.txt
//
// Run from the ufbx checkout root so the `data/...` corpus paths resolve. `--list <file>`
// names a different newline-separated path list; any other argv is an explicit file list.
//
// Output grammar (space-separated tokens, one record per line, `<hex>` lowercase):
//
//   K <index> <len> <hex-or-->   the `ufbxi_strings[]` constant pool, emitted first
//   G <fi> <status> <version> <ascii> <num_elements> <num_connections> <err-contract>
//     status = 1 (modern, read_root returned ok) | 0 (modern, read_root failed) | legacy
//     <err-contract> = <type> <desc_len> <desc_in_info> <desc_digest> <desc_hex>
//                      <info_len> <info_digest> <info_hex>   (all zero/`-` on ok)
//     Mirrors load_oracle's `L` tail: on failure the public `ufbx_error` tail
//     (`ufbxi_fix_error_type` + the Unsupported-version rewrite, ufbx.c:25622-25629) is applied
//     to `uc->error` before it is printed, exactly like `ufbxi_load()` does.
//   E <fi> <elem_id> <type> <typed_id> <fbx_id> <name:len digest hex is_static> <num_props>
//   P <fi> <elem_id> <ix> <name:len digest hex is_static> <type> <flags> <key>
//     <int|-> <reals|-> <str|-> <blob|->
//   C <fi> <ix> <src> <dst> <src_prop:...:is_static> <dst_prop:...:is_static>
//
// Property values are dispatched by `ufbx_prop.flags` rather than read as raw union bytes: the
// legacy reader builds props on an uninitialised stack `ufbx_prop tmp_props[]`, so the union
// holds garbage. `<int>` is `value_int` (only with VALUE_INT); `<reals>` is the bit pattern of
// `value_real_arr[0..k]` for VALUE_REAL/VEC2/VEC3/VEC4, `k+1` 16-hex doubles concatenated into
// one token (ufbx_real == double in this build -- PORTING_NOTES.md), `-` otherwise; `<str>` and
// `<blob>` are payloads. Every value token is independent, so a prop missing a flag prints `-`
// without shifting the columns after it.
//
// `name`/`src_prop`/`dst_prop` additionally carry an `is_static` bit: 1 when the pointer is
// `ufbxi_empty_char` or an entry of `ufbxi_strings[]` (ufbx.c:5583), i.e. what the port maps to
// `UfbxiPtrIdTable.IsStatic` (same predicate as tools/dom_oracle.c).

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ufbx.c"

// ---------------------------------------------------------------------------
// Hashing / emission helpers (same shape as tools/load_oracle.c)
// ---------------------------------------------------------------------------

static uint64_t fnv64(const void *p, size_t n)
{
	const uint8_t *b = (const uint8_t*)p;
	uint64_t h = 0xcbf29ce484222325ull;
	for (size_t i = 0; i < n; i++) h = (h ^ b[i]) * 0x00000100000001b3ull;
	return h;
}

static char hex_buf[256];

static char *emit_hex_into(char *out, const void *p, size_t n)
{
	const uint8_t *b = (const uint8_t*)p;
	for (size_t i = 0; i < n; i++) sprintf(out + i * 2, "%02x", b[i]);
	out[n * 2] = 0;
	return out;
}

// ` <len> <digest> <hex-or--> `: bytes inline up to 64, digest-only beyond.
static void emit_payload(const char *data, size_t len)
{
	printf(" %zu %016llx", len, (unsigned long long)fnv64(data, len));
	if (data && len > 0 && len <= 64) printf(" %s", emit_hex_into(hex_buf, data, len));
	else printf(" -");
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

// The `ufbxi_strings[]` constant pool (ufbx.c:5583), emitted once as `K <index> <len> <hex>`
// lines before any graph record. The harness uses it to check that a name marked `is_static`
// really decodes to a pooled constant -- tools/ufbx_strings.tsv is NOT usable for this because
// its second column is the escaped C literal (`T\0\0`), not the logical `{data,len}` value.
static void emit_constants(void)
{
	for (size_t i = 0; i < ufbxi_arraycount(ufbxi_strings); i++) {
		ufbx_string s = ufbxi_strings[i];
		printf("K %zu %zu", i, s.length);
		if (s.data && s.length > 0 && s.length <= 64) printf(" %s\n", emit_hex_into(hex_buf, s.data, s.length));
		else printf(" -\n");
	}
}

// A name-like payload: ` <len> <digest> <hex-or--> <is_static>`.
static void emit_name(const char *data, size_t len)
{
	printf(" %zu %016llx", len, (unsigned long long)fnv64(data, len));
	if (data && len > 0 && len <= 64) printf(" %s", emit_hex_into(hex_buf, data, len));
	else printf(" -");
	printf(" %d", is_static_ptr(data) ? 1 : 0);
}

// The `ufbx_error` contract tail, laid out like tools/load_oracle.c (minus the state columns
// that file prints before it).
static void emit_error_contract(const ufbx_error *error)
{
	printf(" %d", (int)error->type);
	printf(" %zu %d", error->description.length, error->description.data == error->info ? 1 : 0);
	printf(" %016llx", (unsigned long long)fnv64(error->description.data, error->description.length));
	if (error->description.data && error->description.length > 0 && error->description.length <= 64) {
		printf(" %s", emit_hex_into(hex_buf, error->description.data, error->description.length));
	} else {
		printf(" -");
	}
	emit_payload(error->info, error->info_length);
}

// ---------------------------------------------------------------------------
// Load skeleton: ufbxi_load() init prefix (ufbx.c:25482-25607)
// ---------------------------------------------------------------------------

static void graph_init(ufbxi_context *uc, const ufbx_load_opts *user_opts, ufbx_inflate_retain *inflate_retain)
{
	// Test endianness
	{
		uint8_t buf[2];
		uint16_t val = 0xbbaa;
		memcpy(buf, &val, 2);
		uc->local_big_endian = buf[0] == 0xbb;
	}

	uc->double_parse_flags = ufbxi_parse_double_init_flags();

	if (user_opts) {
		uc->opts = *user_opts;
	} else {
		memset(&uc->opts, 0, sizeof(uc->opts));
	}

	if (uc->opts.file_size_estimate) {
		uc->progress_bytes_total = uc->opts.file_size_estimate;
	}

	if (uc->opts.ignore_all_content) {
		uc->opts.ignore_geometry = true;
		uc->opts.ignore_animation = true;
		uc->opts.ignore_embedded = true;
	}

	ufbxi_init_ator(&uc->error, &uc->ator_tmp, &uc->opts.temp_allocator, "temp");
	ufbxi_init_ator(&uc->error, &uc->ator_result, &uc->opts.result_allocator, "result");

	if (uc->opts.read_buffer_size == 0) {
		uc->opts.read_buffer_size = 0x4000;
	}
	if (uc->opts.read_buffer_size <= 32) {
		uc->opts.read_buffer_size = 32;
	}

	if (uc->opts.file_format_lookahead == 0) {
		uc->opts.file_format_lookahead = 0x4000;
	} else if (uc->opts.file_format_lookahead < UFBXI_MIN_FILE_FORMAT_LOOKAHEAD) {
		uc->opts.file_format_lookahead = UFBXI_MIN_FILE_FORMAT_LOOKAHEAD;
	}

	if (!uc->opts.path_separator) {
		uc->opts.path_separator = UFBX_PATH_SEPARATOR;
	}

	if (!uc->opts.progress_cb.fn || uc->opts.progress_interval_hint >= SIZE_MAX) {
		uc->progress_interval = SIZE_MAX;
	} else if (uc->opts.progress_interval_hint > 0) {
		uc->progress_interval = (size_t)uc->opts.progress_interval_hint;
	} else {
		uc->progress_interval = 0x4000;
	}

	if (!uc->opts.open_file_cb.fn) {
		uc->opts.open_file_cb.fn = &ufbx_default_open_file;
	}

	if (!uc->opts.thread_opts.memory_limit) {
		uc->opts.thread_opts.memory_limit = 32*1024*1024;
	}

	uc->synthetic_id_counter = UFBXI_SYNTHETIC_ID_START;

	uc->string_pool.error = &uc->error;
	ufbxi_map_init(&uc->string_pool.map, &uc->ator_tmp, &ufbxi_map_cmp_string, NULL);
	uc->string_pool.buf.ator = &uc->ator_result;
	uc->string_pool.buf.unordered = true;
	uc->string_pool.initial_size = 1024;
	uc->string_pool.error_handling = uc->opts.unicode_error_handling;

	ufbxi_map_init(&uc->prop_type_map, &uc->ator_tmp, &ufbxi_map_cmp_const_char_ptr, NULL);
	ufbxi_map_init(&uc->fbx_id_map, &uc->ator_tmp, &ufbxi_map_cmp_uint64, NULL);
	ufbxi_map_init(&uc->ptr_fbx_id_map, &uc->ator_tmp, &ufbxi_map_cmp_ptr_id, NULL);
	ufbxi_map_init(&uc->texture_file_map, &uc->ator_tmp, &ufbxi_map_cmp_const_char_ptr, NULL);
	ufbxi_map_init(&uc->anim_stack_map, &uc->ator_tmp, &ufbxi_map_cmp_const_char_ptr, NULL);
	ufbxi_map_init(&uc->fbx_attr_map, &uc->ator_tmp, &ufbxi_map_cmp_uint64, NULL);
	ufbxi_map_init(&uc->node_prop_set, &uc->ator_tmp, &ufbxi_map_cmp_const_char_ptr, NULL);
	ufbxi_map_init(&uc->dom_node_map, &uc->ator_tmp, &ufbxi_map_cmp_uintptr, NULL);

	uc->tmp.ator = &uc->ator_tmp;
	uc->tmp_parse.ator = &uc->ator_tmp;
	uc->tmp_stack.ator = &uc->ator_tmp;
	uc->tmp_connections.ator = &uc->ator_tmp;
	uc->tmp_node_ids.ator = &uc->ator_tmp;
	uc->tmp_elements.ator = &uc->ator_tmp;
	uc->tmp_element_offsets.ator = &uc->ator_tmp;
	uc->tmp_element_fbx_ids.ator = &uc->ator_tmp;
	uc->tmp_element_ptrs.ator = &uc->ator_tmp;
	for (size_t i = 0; i < UFBX_ELEMENT_TYPE_COUNT; i++) {
		uc->tmp_typed_element_offsets[i].ator = &uc->ator_tmp;
	}
	uc->tmp_mesh_textures.ator = &uc->ator_tmp;
	uc->tmp_full_weights.ator = &uc->ator_tmp;
	uc->tmp_dom_nodes.ator = &uc->ator_tmp;
	uc->tmp_element_id.ator = &uc->ator_tmp;
	uc->tmp_ascii_spans.ator = &uc->ator_tmp;

	for (size_t i = 0; i < UFBX_THREAD_GROUP_COUNT; i++) {
		uc->tmp_thread_parse[i].ator = &uc->ator_tmp;
		uc->tmp_thread_parse[i].unordered = true;
		uc->tmp_thread_parse[i].clearable = true;
	}

	uc->result.ator = &uc->ator_result;

	uc->tmp.unordered = true;
	uc->tmp_parse.unordered = true;
	uc->tmp_parse.clearable = true;
	uc->result.unordered = true;

	uc->warnings.error = &uc->error;
	uc->warnings.result = &uc->result;
	uc->warnings.tmp_stack.ator = &uc->ator_tmp;
	uc->string_pool.warnings = &uc->warnings;

	// Set zero size `swap_arr` to a non-NULL buffer so we can tell the difference between empty
	// array and an allocation failure.
	uc->swap_arr = (char*)ufbxi_zero_size_buffer;

	uc->inflate_retain = inflate_retain;
}

// ufbxi_load_imp() (ufbx.c:25212-25327), stopping before ufbxi_pre_finalize_scene() at 25328.
// Returns 1 if the stop point was reached (even with a partial graph on a read_root failure the
// caller still dumps), 0 if the load failed earlier.
static int graph_load_imp(ufbxi_context *uc, bool *p_legacy)
{
	// Check for deferred failure
	if (uc->deferred_failure) return 0;
	if (uc->deferred_load) {
		ufbx_stream stream = { 0 };
		ufbx_open_file_opts opts = { 0 };
		const char *filename = uc->load_filename;
		size_t filename_len = uc->load_filename_len;
		bool ok = false;
		if (filename_len == SIZE_MAX) {
			opts.filename_null_terminated = true;
			filename_len = strlen(filename);
		}
		if (uc->opts.filename.length == 0 || uc->opts.filename.data == NULL) {
			uc->opts.filename.data = filename;
			uc->opts.filename.length = filename_len;
		}
		ufbx_error error;
		error.type = UFBX_ERROR_NONE;
		if (uc->opts.open_main_file_with_default || uc->opts.open_file_cb.fn == &ufbx_default_open_file) {
			ufbx_open_file_context ctx = (ufbx_open_file_context)&uc->ator_tmp;
			ok = ufbx_open_file_ctx(&stream, ctx, filename, filename_len, &opts, &error);
		} else {
			ok = ufbxi_open_file(&uc->opts.open_file_cb, &stream, uc->load_filename, filename_len, NULL, &uc->ator_tmp, UFBX_OPEN_FILE_MAIN_MODEL);
		}
		if (!ok) {
			if (error.type != UFBX_ERROR_NONE) {
				uc->error = error;
			} else {
				ufbxi_set_err_info(&uc->error, filename, filename_len);
			}
			ufbxi_fail_msg("open_file_fn()", "File not found");
		}
		uc->read_fn = stream.read_fn;
		uc->skip_fn = stream.skip_fn;
		uc->size_fn = stream.size_fn;
		uc->close_fn = stream.close_fn;
		uc->read_user = stream.user;
	}

	if (uc->opts.progress_cb.fn && uc->progress_bytes_total == 0 && uc->size_fn) {
		uint64_t total = uc->size_fn(uc->read_user);
		ufbxi_check(total != UINT64_MAX);
		uc->progress_bytes_total = total;
	}

	ufbxi_check(uc->opts.path_separator >= 0x20 && uc->opts.path_separator <= 0x7e);

	ufbxi_check(ufbxi_fixup_opts_string(uc, &uc->opts.filename, false));
	ufbxi_check(ufbxi_fixup_opts_string(uc, &uc->opts.obj_mtl_path, true));
	ufbxi_check(ufbxi_fixup_opts_string(uc, &uc->opts.geometry_transform_helper_name, true));
	ufbxi_check(ufbxi_fixup_opts_string(uc, &uc->opts.scale_helper_name, true));

	ufbxi_check(ufbxi_thread_pool_init(&uc->thread_pool, &uc->error, &uc->ator_tmp, &uc->opts.thread_opts));

	if (!uc->opts.allow_unsafe) {
		ufbxi_check_msg(uc->opts.index_error_handling != UFBX_INDEX_ERROR_HANDLING_UNSAFE_IGNORE, "Unsafe options");
		ufbxi_check_msg(uc->opts.unicode_error_handling != UFBX_UNICODE_ERROR_HANDLING_UNSAFE_IGNORE, "Unsafe options");
	} else {
		uc->scene.metadata.is_unsafe = true;
	}

	if (uc->opts.index_error_handling == UFBX_INDEX_ERROR_HANDLING_NO_INDEX) {
		uc->scene.metadata.may_contain_no_index = true;
	}

	uc->retain_mesh_parts = !uc->opts.ignore_geometry && !uc->opts.skip_mesh_parts;
	uc->scene.metadata.may_contain_missing_vertex_position = uc->opts.allow_missing_vertex_position;
	uc->scene.metadata.may_contain_broken_elements = uc->opts.connect_broken_elements;

	uc->scene.metadata.creator.data = ufbxi_empty_char;

	uc->unit_scale = 1.0f;
	if (uc->data == NULL) {
		uc->data_begin = uc->data = ufbxi_zero_size_buffer;
	}

	uc->retain_vertex_w = (uc->opts.retain_dom || uc->opts.retain_vertex_attrib_w) && !uc->opts.ignore_geometry;

	ufbxi_check(ufbxi_load_strings(uc));
	ufbxi_check(ufbxi_load_maps(uc));
	ufbxi_check(ufbxi_determine_format(uc));

	ufbx_file_format format = uc->scene.metadata.file_format;

	if (format == UFBX_FILE_FORMAT_FBX) {
		ufbxi_check(ufbxi_begin_parse(uc));
		if (uc->version < 6000) {
			*p_legacy = true;
			ufbxi_check(ufbxi_read_legacy_root(uc));
		} else {
			ufbxi_check(ufbxi_read_root(uc));
		}
		if (!ufbxi_supports_version(uc->version)) {
			ufbxi_check(ufbxi_warnf(UFBX_WARNING_UNSUPPORTED_VERSION, "Unsupported FBX version (%u)", uc->version));
		}
		ufbxi_update_scene_metadata(&uc->scene.metadata);
		ufbxi_check(ufbxi_init_file_paths(uc));
	} else if (format == UFBX_FILE_FORMAT_OBJ) {
		ufbxi_check(ufbxi_obj_load(uc));
		ufbxi_update_scene_metadata(&uc->scene.metadata);
	} else if (format == UFBX_FILE_FORMAT_MTL) {
		ufbxi_check(ufbxi_mtl_load(uc));
		ufbxi_update_scene_metadata(&uc->scene.metadata);
	}

	// Fake DOM root if necessary
	if (uc->opts.retain_dom && !uc->scene.dom_root) {
		ufbx_dom_node *dom_root = ufbxi_push_zero(&uc->result, ufbx_dom_node, 1);
		ufbxi_check(dom_root);
		dom_root->name.data = ufbxi_empty_char;
		uc->scene.dom_root = dom_root;
	}

	// *** STOP *** -- ufbxi_pre_finalize_scene(uc) (ufbx.c:25328) is NOT called.
	return 1;
}

// ---------------------------------------------------------------------------
// Graph dump
// ---------------------------------------------------------------------------

static void dump_prop(int fi, uint32_t elem_id, size_t ix, const ufbx_prop *p)
{
	printf("P %d %u %zu", fi, elem_id, ix);
	emit_name(p->name.data, p->name.length);

	uint32_t flags = (uint32_t)p->flags;
	printf(" %d %x %x", (int)p->type, flags, (unsigned)p->_internal_key);

	// int
	if (flags & UFBX_PROP_FLAG_VALUE_INT) {
		printf(" %lld", (long long)p->value_int);
	} else {
		printf(" -");
	}

	// reals: n = 1/2/3/4 for REAL/VEC2/VEC3/VEC4, bit patterns concatenated into one token.
	int nreal = 0;
	if (flags & UFBX_PROP_FLAG_VALUE_REAL) nreal = 1;
	else if (flags & UFBX_PROP_FLAG_VALUE_VEC2) nreal = 2;
	else if (flags & UFBX_PROP_FLAG_VALUE_VEC3) nreal = 3;
	else if (flags & UFBX_PROP_FLAG_VALUE_VEC4) nreal = 4;
	if (nreal > 0) {
		printf(" ");
		for (int k = 0; k < nreal; k++) {
			uint64_t bits;
			memcpy(&bits, &p->value_real_arr[k], sizeof(bits));
			printf("%016llx", (unsigned long long)bits);
		}
	} else {
		printf(" -");
	}

	// str
	if (flags & UFBX_PROP_FLAG_VALUE_STR) {
		emit_payload(p->value_str.data, p->value_str.length);
	} else {
		printf(" -");
	}

	// blob
	if (flags & UFBX_PROP_FLAG_VALUE_BLOB) {
		emit_payload((const char*)p->value_blob.data, p->value_blob.size);
	} else {
		printf(" -");
	}

	printf("\n");
}

static void dump_graph(ufbxi_context *uc, int fi, int ok, bool legacy, const ufbx_error *contract)
{
	size_t num_elements = uc->num_elements;
	size_t ptr_items = uc->tmp_element_ptrs.num_items;
	size_t id_items = uc->tmp_element_fbx_ids.num_items;
	size_t num_conns = uc->tmp_connections.num_items;

	if (legacy) {
		// S1 does not cover the legacy reader: run it (above) and mark the file, but do not
		// emit graph records. The harness skips `legacy` G records.
		printf("G %d legacy %u %d %zu %zu", fi, uc->version, uc->from_ascii ? 1 : 0, num_elements, num_conns);
		if (ok) printf(" 0 0 0 0000000000000000 - 0 0000000000000000 -\n");
		else { emit_error_contract(contract); printf("\n"); }
		return;
	}

	printf("G %d %d %u %d %zu %zu", fi, ok ? 1 : 0, uc->version, uc->from_ascii ? 1 : 0,
		num_elements, num_conns);
	if (ok) {
		printf(" 0 0 0 0000000000000000 - 0 0000000000000000 -\n");
	} else {
		emit_error_contract(contract);
		printf("\n");
	}

	// Read the buffers without consuming them (`peek`), so the dump cannot perturb anything.
	size_t n = num_elements;
	if (ptr_items < n) { fprintf(stderr, "graph_oracle: %d: tmp_element_ptrs has %zu < %zu elements\n", fi, ptr_items, n); n = ptr_items; }
	if (id_items < n) { fprintf(stderr, "graph_oracle: %d: tmp_element_fbx_ids has %zu < %zu elements\n", fi, id_items, n); n = id_items; }

	ufbx_element **ptrs = n ? (ufbx_element**)malloc(n * sizeof(ufbx_element*)) : NULL;
	uint64_t *ids = n ? (uint64_t*)malloc(n * sizeof(uint64_t)) : NULL;
	if (n) {
		ufbxi_pop_size(&uc->tmp_element_ptrs, sizeof(ufbx_element*), n, ptrs, true);
		ufbxi_pop_size(&uc->tmp_element_fbx_ids, sizeof(uint64_t), n, ids, true);
	}

	for (size_t i = 0; i < n; i++) {
		ufbx_element *e = ptrs[i];
		printf("E %d %u %d %u %llu", fi, e->element_id, (int)e->type, e->typed_id,
			(unsigned long long)ids[i]);
		emit_name(e->name.data, e->name.length);
		printf(" %zu\n", e->props.props.count);

		for (size_t ix = 0; ix < e->props.props.count; ix++) {
			dump_prop(fi, e->element_id, ix, &e->props.props.data[ix]);
		}
	}

	free(ptrs);
	free(ids);

	if (num_conns) {
		ufbxi_tmp_connection *conns = (ufbxi_tmp_connection*)malloc(num_conns * sizeof(ufbxi_tmp_connection));
		if (conns) {
			ufbxi_pop_size(&uc->tmp_connections, sizeof(ufbxi_tmp_connection), num_conns, conns, true);
			for (size_t ix = 0; ix < num_conns; ix++) {
				ufbxi_tmp_connection *c = &conns[ix];
				printf("C %d %zu %llu %llu", fi, ix, (unsigned long long)c->src, (unsigned long long)c->dst);
				emit_name(c->src_prop.data, c->src_prop.length);
				emit_name(c->dst_prop.data, c->dst_prop.length);
				printf("\n");
			}
			free(conns);
		}
	}
}

// ---------------------------------------------------------------------------
// Per-file driver
// ---------------------------------------------------------------------------

static void run_file(const char *path, int fi)
{
	ufbxi_context uc; // ufbxi_uninit
	memset(&uc, 0, sizeof(ufbxi_context));
	uc.deferred_load = true;
	uc.load_filename = path;
	uc.load_filename_len = SIZE_MAX;

	ufbx_load_opts opts;
	memset(&opts, 0, sizeof(opts));

	ufbx_inflate_retain inflate_retain;
	inflate_retain.initialized = false;

	graph_init(&uc, &opts, &inflate_retain);

	bool legacy = false;
	int ok = graph_load_imp(&uc, &legacy);

	// Mirror the tail of ufbxi_load() (ufbx.c:25622-25629) so a failure record carries the same
	// public `ufbx_error` contract tools/load_oracle.c compares against.
	ufbx_error contract;
	memset(&contract, 0, sizeof(contract));
	if (!ok) {
		ufbxi_fix_error_type(&uc.error, "Failed to load", &contract);
		if (contract.type == UFBX_ERROR_UNKNOWN && uc.scene.metadata.file_format == UFBX_FILE_FORMAT_FBX
				&& !ufbxi_supports_version(uc.version)) {
			contract.description.data = "Unsupported version";
			contract.description.length = strlen("Unsupported version");
			contract.type = UFBX_ERROR_UNSUPPORTED_VERSION;
			ufbxi_fmt_err_info(&contract, "%u", uc.version);
		}
	}

	dump_graph(&uc, fi, ok, legacy, &contract);

	if (uc.close_fn) {
		uc.close_fn(uc.read_user);
	}
	ufbxi_free_temp(&uc);
	ufbxi_free_result(&uc);
}

int main(int argc, char **argv)
{
	const char *list_path = "C:/Workspace/ufbx-cs/tools/graph_corpus.txt";

	emit_constants();

	if (argc > 2 && !strcmp(argv[1], "--list")) {
		list_path = argv[2];
		argc = 1;
	}

	if (argc > 1) {
		for (int i = 1; i < argc; i++) run_file(argv[i], i - 1);
		return 0;
	}

	FILE *f = fopen(list_path, "rb");
	if (!f) {
		fprintf(stderr, "cannot open %s\n", list_path);
		return 2;
	}

	char line[4096];
	int fi = 0;
	while (fgets(line, sizeof(line), f)) {
		size_t len = strlen(line);
		while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r')) line[--len] = 0;
		if (len == 0) continue;
		run_file(line, fi++);
	}
	fclose(f);
	fprintf(stderr, "graph_oracle: %d files\n", fi);
	return 0;
}
