// C reference oracle for the S4a geometry-cache / external-file / axes-unit / skinning-entry
// module of the ufbx -> C# port. `#include "ufbx.c"` to reach the internal `ufbxi_*` functions
// and the public geometry-cache API they back.
//
// Scope:
//   * ufbxi_load_geometry_cache -> ufbxi_cache_load -> cache_load_file -> cache_load_mc /
//     cache_load_pc2 / cache_load_xml(_imp) / cache_load_frame_files / cache_setup_channels
//     (ufbx.c:23936-24763): dump root filename, channel count/name/interpretation/
//     interpretation_name/mirror/scale, and every frame's channel/time/filename/format/encoding/
//     offset/count/element_bytes/total_bytes.
//   * ufbx_sample_geometry_cache_vec3 (ufbx.c:32953) and ufbx_read_geometry_cache_real
//     (ufbx.c:32704): sample each channel at a few times and dump the raw doubles (hex bits).
//   * the same four read/sample entry points over a synthetic in-memory cache file served by a
//     user open_file_cb (dump_read_cases(), kinds RD/RDO): the only coverage of the 512-real
//     chunking, of mirroring, scaling, the endian swap, both seek paths, truncated and failing
//     reads, and of the caller-side `ufbx_geometry_cache_data_opts` copy.
//   * ufbxi_find_cubic_bezier_t (ufbx.c:25022): Newton-Raphson vectors.
//   * ufbx_get_skin_vertex_matrix (ufbx.h:5601 -> ufbx.c:31936): synthetic skin-deformer
//     vectors (dual-quaternion + linear blend), dumped as matrix bits.
//   * ufbx_catch_get_skin_vertex_matrix (ufbx.h:5600) with a live ufbx_panic (dump_skin_catch(),
//     kind SKC): the fallback / rcp_weight branches and the unsigned `size_t vertex` bounds.
//   * ufbx_get_blend_shape_offset_index / _vertex_offset / _blend_vertex_offset /
//     add_blend_(shape_)vertex_offsets (ufbx.c:32028-32103): synthetic vectors, plus dedicated
//     ufbx_add_blend_shape_vertex_offsets probes (dump_blend_add_shape(), kind BSA).
//
// Build (zig, matching the other oracles):
//   zig cc -O2 -DNDEBUG -fno-strict-aliasing -std=c11 -mcpu=x86_64 -ffp-contract=off \
//       -I C:/Workspace/_analyze_ufbx tools/s4a_oracle.c -o tools/s4a_oracle.exe
// Run (from the repo root, so the recorded paths are repo-relative; the S4aCheck harness reads
// the paths back out of the LOAD records and replays them from the same cwd):
//   tools/s4a_oracle.exe \
//     tools/s4a_inputs/synth_a.pc2 tools/s4a_inputs/synth_b.pc2 \
//     tools/s4a_inputs/sine_mcsd_oversample/cache.xml \
//     tools/s4a_inputs/sine_mxmd_oversample/cache.xml \
//     tools/s4a_inputs/sine_mcmf_undersample/cache.xml \
//     tools/s4a_inputs/sine_xml_parse/cache.xml \
//     tools/s4a_inputs/sine_mcsd_oversample/cache.mc \
//     > tools/s4a_oracle.txt
// (The corpus = 2 synthesized PointCache2 files [tools/gen_pc2.py] + 5 real Maya caches copied
//  read-only out of `_analyze_ufbx/data/caches`; the last is the .mc file the OneFile xml points
//  at, loaded directly so cache_load_mc is exercised both through the xml and on its own.)
//
// Output grammar (space separated, one record per line; `<h>` = lowercase 16 hex digits of the
// IEEE-754 bits of a double; `<i>` = decimal int; `<s>` = raw bytes as hex, or `-` for empty):
//   LOAD <ok> <root> <num_channels> <num_frames> <num_extra>
//   CH   <ix> <name> <interp> <interp_name> <mirror> <scale:h>
//   FR   <chan_ix> <time:h> <fmt> <enc> <off> <count> <elem_bytes> <total_bytes>
//   SMP  <chan_ix> <time:h> <n> <d0:h> <d1:h> ...          ufbx_sample_geometry_cache_vec3
//   SV   <t:h> <n> <d0:h> ...                              ufbx_read_geometry_cache_real
//   BZ   <p1:h> <p2:h> <x0:h> <r:h>                        ufbxi_find_cubic_bezier_t
//   SK   <nw> <dw:h> <sx:h> ... <m0:h> ... <m11:h>         ufbx_get_skin_vertex_matrix
//   SKC  <ix> <fb> <did> <msglen> <msg> <m0:h> ... <m11:h>  ufbx_catch_get_skin_vertex_matrix
//   BSI  <num_offsets> <vertex> <idx>                      ufbx_get_blend_shape_offset_index
//   BSV  <num_offsets> <vertex> <x:h> <y:h> <z:h>          ufbx_get_blend_shape_vertex_offset
//   BVO  <vertex> <x:h> <y:h> <z:h>                        ufbx_get_blend_vertex_offset
//   BAV  <num_offsets> <num_vertices> <weight:h> <n> <d0:h> ...  add_blend_vertex_offsets
//   BSA  <ix> <nul> <nv> <weight:h> <d0:h> ... <d17:h>       add_blend_shape_vertex_offsets
//   RD   <case> <n> <out_reals> <call_hash> <data_hash> <reads> <skips>   see dump_read_cases()
//   RDO  <case> <ign_tr> <add> <use_w> <fn> <user> <weight:h>            caller opts after a read

#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static void dbits(double v) {
	uint64_t u;
	memcpy(&u, &v, 8);
	printf("%016llx", (unsigned long long)u);
}

static void puts_hex(const char *data, size_t len) {
	if (len == 0) { putchar('-'); return; }
	for (size_t i = 0; i < len; i++) printf("%02x", (unsigned char)data[i]);
}

static int g_fail = 0;

// ---------------------------------------------------------------------------
// Cache loading / sampling
// ---------------------------------------------------------------------------

static ufbx_string str_c(const char *s)
{
	ufbx_string r;
	r.data = s;
	r.length = strlen(s);
	return r;
}

static void dump_cache(const char *path)
{
	ufbx_error error = { 0 };
	ufbx_geometry_cache_opts opts = { 0 };
	opts.frames_per_second = 30.0;

	ufbx_geometry_cache *cache = ufbxi_load_geometry_cache(str_c(path), &opts, &error);
	if (!cache) {
		printf("LOAD 0 %s - - -\n", error.description.data ? error.description.data : "?");
		g_fail++;
		return;
	}

	printf("LOAD 1 ");
	puts_hex(cache->root_filename.data, cache->root_filename.length);
	printf(" %zu %zu %zu\n", cache->channels.count, cache->frames.count, cache->extra_info.count);

	for (size_t i = 0; i < cache->channels.count; i++) {
		ufbx_cache_channel *ch = &cache->channels.data[i];
		printf("CH %zu ", i);
		puts_hex(ch->name.data, ch->name.length);
		printf(" %d ", (int)ch->interpretation);
		puts_hex(ch->interpretation_name.data, ch->interpretation_name.length);
		printf(" %d ", (int)ch->mirror_axis);
		dbits(ch->scale_factor);
		printf(" %zu\n", ch->frames.count);

		ufbx_cache_frame *frames = ch->frames.data;
		for (size_t f = 0; f < ch->frames.count; f++) {
			ufbx_cache_frame *fr = &frames[f];
			printf("FR %zu ", i);
			dbits(fr->time);
			printf(" %d %d %llu %u %u %llu\n",
				(int)fr->file_format, (int)fr->data_encoding,
				(unsigned long long)fr->data_offset, fr->data_count,
				fr->data_element_bytes, (unsigned long long)fr->data_total_bytes);
		}

		// Sample a few normalized times across the channel.
		double sample_times[5] = { 0.0, 0.25, 0.5, 1.0, 2.5 };
		for (int t = 0; t < 5; t++) {
			ufbx_vec3 data[8];
			memset(data, 0, sizeof(data));
			size_t n = ufbx_sample_geometry_cache_vec3(ch, sample_times[t], data, 4, NULL);
			printf("SMP %zu ", i);
			dbits(sample_times[t]);
			printf(" %zu", n);
			for (size_t k = 0; k < n * 3 && k < 24; k++) {
				printf(" ");
				dbits(((double*)data)[k]);
			}
			printf("\n");
		}
	}

	ufbx_free_geometry_cache(cache);
}

// ---------------------------------------------------------------------------
// find_cubic_bezier_t
// ---------------------------------------------------------------------------

static void dump_bezier(void)
{
	// Deterministic pseudo-random p1/p2/x0 triples; cover 0<t<1, boundaries, and
	// convergence / non-convergence cases.
	static const double p1s[] = { 0.0, 0.25, 0.3333, 0.5, 0.6667, 0.75, 1.0, -1.0, 2.0, 0.9999 };
	static const double p2s[] = { 0.0, 0.1, 0.42, 0.5, 0.58, 0.9, 1.0, -0.5, 1.5, 0.0001 };
	int n = 0;
	for (size_t i = 0; i < sizeof(p1s)/sizeof(p1s[0]); i++) {
		for (size_t j = 0; j < sizeof(p2s)/sizeof(p2s[0]); j++) {
			for (int k = 0; k <= 4; k++) {
				double x0 = (double)k * 0.25;
				double r = ufbxi_find_cubic_bezier_t(p1s[i], p2s[j], x0);
				printf("BZ ");
				dbits(p1s[i]); printf(" ");
				dbits(p2s[j]); printf(" ");
				dbits(x0); printf(" ");
				dbits(r); printf("\n");
				n++;
			}
		}
	}
	// Extra dense sweep to cross 200 vectors.
	for (int i = 0; i < 60; i++) {
		double p1 = (double)i / 59.0;
		double p2 = 1.0 - p1;
		double x0 = (double)(i % 7) / 6.0 - 0.5;
		double r = ufbxi_find_cubic_bezier_t(p1, p2, x0);
		printf("BZ ");
		dbits(p1); printf(" ");
		dbits(p2); printf(" ");
		dbits(x0); printf(" ");
		dbits(r); printf("\n");
		n++;
	}
}

// ---------------------------------------------------------------------------
// Skinning helpers (synthetic)
// ---------------------------------------------------------------------------

static void dump_skin(void)
{
	// Build a small synthetic skin deformer: 2 clusters, 3 vertices with mixed dq weights.
	ufbx_node node0 = { 0 }, node1 = { 0 };
	ufbx_skin_cluster cluster0 = { 0 }, cluster1 = { 0 };
	ufbx_skin_cluster *clusters[2] = { &cluster0, &cluster1 };

	cluster0.bone_node = &node0;
	cluster0.geometry_to_world = ufbx_identity_matrix;
	cluster0.geometry_to_world.m03 = 1.5;
	cluster0.geometry_to_world_transform = ufbx_matrix_to_transform(&cluster0.geometry_to_world);

	cluster1.bone_node = &node1;
	cluster1.geometry_to_world = ufbx_identity_matrix;
	cluster1.geometry_to_world.m00 = 2.0;
	cluster1.geometry_to_world_transform = ufbx_matrix_to_transform(&cluster1.geometry_to_world);

	ufbx_skin_weight weights[6];
	weights[0].cluster_index = 0; weights[0].weight = 0.75;
	weights[1].cluster_index = 1; weights[1].weight = 0.25;
	weights[2].cluster_index = 0; weights[2].weight = 1.0;
	weights[3].cluster_index = 0; weights[3].weight = 0.4;
	weights[4].cluster_index = 1; weights[4].weight = 0.6;
	weights[5].cluster_index = 0; weights[5].weight = 0.0;

	ufbx_skin_vertex vertices[3];
	vertices[0].weight_begin = 0; vertices[0].num_weights = 2; vertices[0].dq_weight = 0.0;
	vertices[1].weight_begin = 2; vertices[1].num_weights = 1; vertices[1].dq_weight = 1.0;
	vertices[2].weight_begin = 3; vertices[2].num_weights = 3; vertices[2].dq_weight = 0.5;

	ufbx_skin_deformer skin = { 0 };
	skin.clusters.data = clusters; skin.clusters.count = 2;
	skin.weights.data = weights; skin.weights.count = 6;
	skin.vertices.data = vertices; skin.vertices.count = 3;

	for (size_t v = 0; v < 3; v++) {
		ufbx_matrix m = ufbx_get_skin_vertex_matrix(&skin, v, NULL);
		printf("SK %zu %u ", v, vertices[v].num_weights);
		dbits(vertices[v].dq_weight);
		const double *mm = (const double*)m.v;
		for (int i = 0; i < 12; i++) { printf(" "); dbits(mm[i]); }
		printf("\n");
	}
}

// ---------------------------------------------------------------------------
// ufbx_catch_get_skin_vertex_matrix + ufbx_panic (record kind SKC)
//
// The SK block above only reaches the three well-formed vertices through the panic-less inline
// wrapper, so these were dark: the `ufbxi_panicf()` site (ufbx.c:31939) and its `%zu` formatting,
// the `did_panic` early-out of ufbxi_panicf_imp (3390), the `total_weight <= 0` fallback branch
// (31982-31988), the `rcp_weight` normalization (31990-32003) and its `rcp_weight == 0` sub-branch,
// and -- most importantly -- the fact that **both** bounds tests are unsigned in C (31939, 31941).
// A port that narrows `size_t vertex` to `uint32_t` first answers those probes as if the high bits
// never existed; the 2^32 probes below are exactly that trap.
//
//   SKC <ix> <fb> <did_panic> <msglen> <msg> <m0:h> ... <m11:h>
//     <ix>        index into g_skc below
//     <fb>        whether a non-NULL fallback matrix was passed (the translation is recognizable)
//     <msg>       panic.message as hex, `-` when message_length is 0
// ---------------------------------------------------------------------------

struct skc_probe { size_t vertex; int fb; int pre; };

static const struct skc_probe g_skc[] = {
	{ 0, 1, 0 }, // unskinned vertex -> the fallback matrix
	{ 0, 0, 0 }, // unskinned vertex, no fallback -> identity
	{ 1, 0, 0 }, // total weight 0.3 -> rcp_weight normalization, dq < 1 mixes both parts
	{ 2, 0, 0 }, // total weight == UFBX_EPSILON -> the rcp_weight == 0 branch
	{ 1, 1, 0 }, // fallback passed but the vertex IS skinned -> fallback ignored
	{ 3, 0, 0 }, // vertex == count -> panic
	{ 4, 0, 0 }, // vertex == count + 1 -> panic
	{ (size_t)-1, 0, 0 }, // SIZE_MAX: the unsigned compare must reject it
	{ 0x100000000ull, 0, 0 }, // 2^32: truncating to uint32_t first would read vertex 0
	{ 0x100000004ull, 0, 0 }, // 2^32 + 4: same trap, different digits in the message
	{ 0, 1, 1 }, // preset panic + a valid call -> nothing may be written to `panic`
	{ 3, 0, 1 }, // preset AND out of bounds -> the panicf call itself must be skipped (3390)
};

static void dump_skin_catch(void)
{
	ufbx_node node0 = { 0 }, node1 = { 0 };
	ufbx_skin_cluster cluster0 = { 0 }, cluster1 = { 0 };
	ufbx_skin_cluster *clusters[2] = { &cluster0, &cluster1 };

	cluster0.bone_node = &node0;
	cluster0.geometry_to_world = ufbx_identity_matrix;
	cluster0.geometry_to_world.m03 = 1.5;
	cluster0.geometry_to_world_transform = ufbx_matrix_to_transform(&cluster0.geometry_to_world);

	cluster1.bone_node = &node1;
	cluster1.geometry_to_world = ufbx_identity_matrix;
	cluster1.geometry_to_world.m00 = 2.0;
	cluster1.geometry_to_world_transform = ufbx_matrix_to_transform(&cluster1.geometry_to_world);

	ufbx_skin_weight weights[4];
	weights[0].cluster_index = 0; weights[0].weight = 0.0;
	weights[1].cluster_index = 1; weights[1].weight = 0.0;
	weights[2].cluster_index = 0; weights[2].weight = 0.3;
	// Exactly UFBX_EPSILON (ufbx.c:70-72): `total_weight > 0` so the early return is skipped, while
	// `ufbx_fabs(total_weight) > UFBX_EPSILON` is false (the test is strict), which is the only way
	// to reach the `rcp_weight = 0` arm of ufbx.c:31991. dq_weight stays 0 so the dual-quaternion
	// normalization -- which would turn that 0 into a 0 * infinity NaN -- is not entered either.
	weights[3].cluster_index = 1; weights[3].weight = 1.4916681462400413e-154;

	ufbx_skin_vertex vertices[3];
	vertices[0].weight_begin = 0; vertices[0].num_weights = 2; vertices[0].dq_weight = 0.0;
	vertices[1].weight_begin = 2; vertices[1].num_weights = 1; vertices[1].dq_weight = 0.5;
	vertices[2].weight_begin = 3; vertices[2].num_weights = 1; vertices[2].dq_weight = 0.0;

	ufbx_skin_deformer skin = { 0 };
	skin.clusters.data = clusters; skin.clusters.count = 2;
	skin.weights.data = weights; skin.weights.count = 4;
	skin.vertices.data = vertices; skin.vertices.count = 3;

	ufbx_matrix fallback = ufbx_identity_matrix;
	fallback.m03 = -7.25;
	fallback.m11 = 3.0;

	for (size_t ix = 0; ix < sizeof(g_skc) / sizeof(g_skc[0]); ix++) {
		const struct skc_probe *p = &g_skc[ix];

		ufbx_panic panic;
		memset(&panic, 0, sizeof(panic));
		if (p->pre) {
			panic.did_panic = true;
			panic.message_length = 6;
			memcpy(panic.message, "PRESET", 6);
		}
		ufbx_matrix m = ufbx_catch_get_skin_vertex_matrix(&panic, &skin, p->vertex, p->fb ? &fallback : NULL);
		printf("SKC %zu %d %d %zu ", ix, p->fb, panic.did_panic ? 1 : 0, panic.message_length);
		puts_hex(panic.message, panic.message_length);
		const double *mm = (const double*)m.v;
		for (int i = 0; i < 12; i++) { printf(" "); dbits(mm[i]); }
		printf("\n");
	}
}

static void dump_blend(void)
{
	// Synthetic blend shape: offsets at vertices 1, 3, 5.
	uint32_t offset_vertices[3] = { 1, 3, 5 };
	ufbx_vec3 position_offsets[3] = {
		{ 1.0, 2.0, 3.0 }, { -1.0, 0.5, 0.25 }, { 0.0, -2.0, 4.0 },
	};
	double offset_weights[2] = { 0.5, 2.0 };

	ufbx_blend_shape shape = { 0 };
	shape.num_offsets = 3;
	shape.offset_vertices.data = offset_vertices;
	shape.position_offsets.data = position_offsets;
	shape.offset_weights.data = offset_weights;
	shape.offset_weights.count = 2;

	int probes[5] = { 0, 1, 3, 4, 5 };
	for (int i = 0; i < 5; i++) {
		uint32_t idx = ufbx_get_blend_shape_offset_index(&shape, probes[i]);
		printf("BSI %d %d %u\n", 3, probes[i], idx);
		ufbx_vec3 off = ufbx_get_blend_shape_vertex_offset(&shape, probes[i]);
		printf("BSV %d %d ", 3, probes[i]);
		dbits(off.x); printf(" "); dbits(off.y); printf(" "); dbits(off.z); printf("\n");
	}

	// Blend deformer with two keyframes.
	ufbx_blend_keyframe keys[2] = { 0 };
	keys[0].shape = &shape; keys[0].effective_weight = 0.5;
	keys[1].shape = &shape; keys[1].effective_weight = -0.25;
	ufbx_blend_channel chan = { 0 };
	chan.keyframes.data = keys; chan.keyframes.count = 2;
	ufbx_blend_channel *chans[1] = { &chan };
	ufbx_blend_deformer blend = { 0 };
	blend.channels.data = chans; blend.channels.count = 1;

	ufbx_vec3 bvo = ufbx_get_blend_vertex_offset(&blend, 3);
	printf("BVO %d ", 3);
	dbits(bvo.x); printf(" "); dbits(bvo.y); printf(" "); dbits(bvo.z); printf("\n");

	// add_blend_vertex_offsets over 6 vertices.
	ufbx_vec3 verts[6];
	for (int i = 0; i < 6; i++) { verts[i].x = 0; verts[i].y = 0; verts[i].z = 0; }
	ufbx_add_blend_vertex_offsets(&blend, verts, 6, 1.0);
	printf("BAV %d %d ", 3, 6);
	dbits(1.0); printf(" %d", 18);
	for (int i = 0; i < 6; i++) {
		printf(" "); dbits(verts[i].x);
		printf(" "); dbits(verts[i].y);
		printf(" "); dbits(verts[i].z);
	}
	printf("\n");
}

// ---------------------------------------------------------------------------
// ufbx_add_blend_shape_vertex_offsets (record kind BSA)
//
// BAV above only reaches this through ufbx_add_blend_vertex_offsets, so the shape form's own
// entries -- the `weight == 0.0` and `!vertices` early returns (ufbx.c:32072-32073), the
// `index < num_vertices` filter (32082) and the per-offset `offset_weights` multiply that only
// applies while `i < weights.count` (32083-32086) -- get their own probes here.
//
//   BSA <ix> <nul> <nv> <weight:h> <d0:h> ... <d17:h>
//     <nul>  1 = pass a NULL vertex buffer (C returns without touching anything; the printed
//            buffer is the freshly zeroed one both sides start from)
//     <nv>   ufbx_add_blend_shape_vertex_offsets' num_vertices
// ---------------------------------------------------------------------------

static void dump_blend_add_shape(void)
{
	uint32_t offset_vertices[3] = { 1, 3, 5 };
	ufbx_vec3 position_offsets[3] = {
		{ 1.0, 2.0, 3.0 }, { -1.0, 0.5, 0.25 }, { 0.0, -2.0, 4.0 },
	};
	double offset_weights[2] = { 0.5, 2.0 };

	ufbx_blend_shape shape = { 0 };
	shape.num_offsets = 3;
	shape.offset_vertices.data = offset_vertices;
	shape.position_offsets.data = position_offsets;
	shape.offset_weights.data = offset_weights;
	shape.offset_weights.count = 2;

	struct { int nul; size_t nv; double w; } probes[5] = {
		{ 0, 6, 1.0 }, { 0, 6, 0.0 }, { 0, 4, 1.0 }, { 0, 6, 0.25 }, { 1, 6, 1.0 },
	};

	for (size_t ix = 0; ix < 5; ix++) {
		ufbx_vec3 verts[6];
		for (int i = 0; i < 6; i++) { verts[i].x = 0; verts[i].y = 0; verts[i].z = 0; }
		ufbx_add_blend_shape_vertex_offsets(&shape, probes[ix].nul ? NULL : verts, probes[ix].nv, probes[ix].w);
		printf("BSA %zu %d %zu ", ix, probes[ix].nul, probes[ix].nv);
		dbits(probes[ix].w);
		for (int i = 0; i < 6; i++) {
			printf(" "); dbits(verts[i].x);
			printf(" "); dbits(verts[i].y);
			printf(" "); dbits(verts[i].z);
		}
		printf("\n");
	}
}

// ---------------------------------------------------------------------------
// Synthetic ufbx_read_geometry_cache_{real,vec3} / ufbx_sample_geometry_cache_{real,vec3}
// over an in-memory "file" served through a user ufbx_open_file_cb (record kind RD).
//
// Why this is needed: none of it is reachable from dump_cache(). Every frame in the S4a
// corpus is at most 36 reals (FR records) and every channel has mirror_axis == NONE (all 12
// CH records), so these are dark in the corpus:
//   * the 512-real read chunking (UFBXI_GEOMETRY_CACHE_BUFFER_SIZE, ufbx.c:62, used at 32791)
//   * the mirror negation and its cross-chunk phase (ufbx.c:32832-32838)
//   * the scale_factor multiply (ufbx.c:32827-32831)
//   * the seek without skip_fn, i.e. the char buffer[4096] loop (ufbx.c:32767-32777)
//   * the seek with skip_fn, chunked by UFBXI_MAX_SKIP_SIZE = 0x40000000 (ufbx.c:54, 32759-32765)
//   * the endian swap for float and double (ufbx.c:32798-32805, 32813-32820)
//   * the bytes_read == SIZE_MAX guard (ufbx.c:32811) and the truncated-read break (32856)
//   * additive / weight blending of two chunked reads inside a sample (32840-32852)
//   * the flat (ufbx_real*) cast of the *_vec3 wrappers, which lets a partial trailing
//     vector be written even though the return value rounds down (32949, 32961)
// The call log folds the size of every read_fn()/skip_fn() request, which is the only way
// the chunk *granularity* itself (512 vs 4096, 0x40000000 vs 0x7fffffff) is observable: the
// values a frame decodes to are chunk-size independent, because mirror_ix is re-based by
// -= num_read (32838) and keeps its global mod-3 phase.
//
//   RD <case> <n> <out_reals> <call_hash> <data_hash> <reads> <skips>
//     <n>          the size_t the ABI returned, in elements (reals or vec3)
//     <out_reals>  n * 1 or 3, i.e. how many ufbx_real the caller asked for
//     <call_hash>  FNV-1a-64 over, in call order, 'r' + requested_bytes + bytes_returned per
//                  read_fn(), 's' + requested_bytes + ok per skip_fn(), and a final 'c' per
//                  close_fn(). 64-bit quantities are folded low byte first. An error read
//                  folds SIZE_MAX as bytes_returned.
//     <data_hash>  FNV-1a-64 over the IEEE-754 bit pattern of every one of the 1200 doubles
//                  of the output region (400 ufbx_vec3), including the untouched tail, so an
//                  out-of-bounds write on either side shows up.
//
// Data: element (block b, index i) is ((b*61 + i*37 + 11) % 251 - 100) * 0.125 -- small
// dyadic values, exact in float and double, so the records are CRT-independent. The file is
// 20000 bytes; a case whose data_offset exceeds it runs in "infinite" mode, where reads and
// skips always succeed and bytes are served modulo the array (both sides implement the same
// rule, so the wrapped bytes are still well defined).
// ---------------------------------------------------------------------------

#define RD_FILE_SIZE 20000
#define RD_OUT_DOUBLES 1200

static uint64_t g_fnv;
static void fnv1(unsigned char b) { g_fnv ^= b; g_fnv *= 0x100000001b3ull; }
static void fnv8(uint64_t v) { for (int i = 0; i < 8; i++) fnv1((unsigned char)((v >> (i * 8)) & 0xff)); }
static uint64_t rd_bits(double v) { uint64_t u; memcpy(&u, &v, 8); return u; }

static unsigned char g_rd_file[RD_FILE_SIZE];
static double g_rd_out[RD_OUT_DOUBLES];
static size_t g_rd_avail;
static size_t g_rd_pos;
static int g_rd_infinite, g_rd_have_skip, g_rd_error_first, g_rd_reads, g_rd_skips;

static size_t rd_read_fn(void *user, void *dst, size_t size)
{
	g_rd_reads++;
	fnv1('r'); fnv8(size);
	if (g_rd_error_first && g_rd_reads == 1) { fnv8((uint64_t)-1); return (size_t)-1; }
	size_t avail = g_rd_infinite ? size : (g_rd_pos < g_rd_avail ? g_rd_avail - g_rd_pos : 0);
	size_t n = size < avail ? size : avail;
	char *d = (char*)dst;
	for (size_t i = 0; i < n; i++) d[i] = (char)g_rd_file[(g_rd_pos + i) % RD_FILE_SIZE];
	g_rd_pos += n;
	fnv8(n);
	return n;
}

static bool rd_skip_fn(void *user, size_t size)
{
	g_rd_skips++;
	fnv1('s'); fnv8(size);
	bool ok = g_rd_infinite || (g_rd_pos + size <= g_rd_avail);
	if (ok) g_rd_pos += size;
	fnv1(ok ? 1 : 0);
	return ok;
}

static void rd_close_fn(void *user) { fnv1('c'); }

static bool rd_open_cb(void *user, ufbx_stream *stream, const char *path, size_t path_len, const ufbx_open_file_info *info)
{
	g_rd_pos = 0; g_rd_reads = 0; g_rd_skips = 0;
	stream->read_fn = rd_read_fn;
	stream->skip_fn = g_rd_have_skip ? rd_skip_fn : NULL;
	stream->size_fn = NULL;
	stream->close_fn = rd_close_fn;
	stream->user = NULL;
	return true;
}

static double rd_val(unsigned long long b, unsigned long long i)
{
	return (double)((long long)((b * 61ull + i * 37ull + 11ull) % 251ull) - 100) * 0.125;
}

//   fn fmt enc dcount doff nblk mirror scale8 flags weight8 outc trunc time16
// fmt: 1 REAL_FLOAT, 2 VEC3_FLOAT, 3 REAL_DOUBLE, 4 VEC3_DOUBLE (ufbx.h:2128-2136)
// enc: 1 LITTLE_ENDIAN, 2 BIG_ENDIAN; mirror: 1 X, 2 Y, 3 Z
// flags: 1 ignore_transform, 2 additive, 4 use_weight, 8 have_skip, 16 error_first_read,
//        32 infinite_file, 64 prefill_output
static const long long g_rd_cases[][13] = {
	{ 0, 4, 1,   200,          0, 1, 0,  8,   8,  8,  600,   0,  0 }, // 600 reals = chunks 512+88, no mirror
	{ 0, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 }, // mirror X across the chunk boundary
	{ 0, 4, 1,   200,          0, 1, 2,  8,   8,  8,  600,   0,  0 }, // mirror Y
	{ 0, 4, 1,   200,          0, 1, 3,  8,   8,  8,  600,   0,  0 }, // mirror Z
	{ 0, 2, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 }, // float path, mirror X
	{ 0, 2, 2,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 }, // float path, endian swap + mirror X
	{ 0, 4, 2,   200,          0, 1, 2,  8,   8,  8,  600,   0,  0 }, // double path, endian swap + mirror Y
	{ 0, 3, 1,  1200,          0, 1, 1,  8,   8,  8, 1200,   0,  0 }, // three chunks: 512+512+176
	{ 0, 3, 1,  1027,          0, 1, 1,  8,   8,  8, 1027,   0,  0 }, // three chunks, tail of 3
	{ 0, 1, 1,   512,          0, 1, 1,  8,   8,  8,  512,   0,  0 }, // exactly one chunk
	{ 0, 1, 1,   513,          0, 1, 1,  8,   8,  8,  513,   0,  0 }, // chunk boundary + 1 element
	{ 1, 4, 1,   200,          0, 1, 1,  8,   8,  8,  200,   0,  0 }, // vec3 wrapper over the same data
	{ 1, 4, 1,   200,          0, 1, 0,  8,   8,  8,  200,  16,  0 }, // truncated mid-vector: partial write
	{ 1, 4, 1,   200,          0, 1, 1,  8,   8,  8,  200,  16,  0 }, // partial write + mirror phase
	{ 0, 4, 1,   200,          0, 1, 1,  8,   9,  8,  600,   0,  0 }, // ignore_transform beats mirror
	{ 0, 4, 1,   200,          0, 1, 0, 12,   8,  8,  600,   0,  0 }, // scale 1.5
	{ 0, 4, 1,   200,          0, 1, 1, 12,   8,  8,  600,   0,  0 }, // scale 1.5 then mirror X
	{ 0, 4, 1,   200,          0, 1, 2,  0,   8,  8,  600,   0,  0 }, // scale 0
	{ 0, 4, 1,   200,          0, 1, 1,  8,  78,  3,  600,   0,  0 }, // additive + weight 0.375 on a prefilled buffer
	{ 0, 4, 1,   200,          0, 1, 1,  8,  12,  6,  600,   0,  0 }, // non-additive weight 0.75
	{ 0, 4, 1,   200,          3, 1, 1,  8,   0,  8,  600,   0,  0 }, // misaligned offset, read-seek of 3 bytes
	{ 0, 2, 1,   200,       9000, 1, 1,  8,   0,  8,  600,   0,  0 }, // read-seek in 4096-byte chunks
	{ 0, 4, 1,   200, 3221225479, 1, 1, 8,  40,  8,  600,   0,  0 }, // 0x40000000*3+7: three max skips + tail
	{ 0, 4, 1,   200, 1073741824, 1, 1, 8,  40,  8,  600,   0,  0 }, // exactly one max skip (the >= edge)
	{ 0, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600, 100,  0 }, // short read inside the second chunk
	{ 0, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600,4800,  0 }, // empty data: read returns 0
	{ 0, 4, 1,   200,          0, 1, 1,  8,  24,  8,  600,   0,  0 }, // first read returns SIZE_MAX
	{ 0, 4, 0,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 }, // unknown encoding
	{ 0, 0, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 }, // unknown format
	{ 2, 4, 1,   200,          0, 3, 1,  8,   8,  8,  600,   0,  8 }, // sample between two chunked frames
	{ 2, 4, 1,   200,          0, 3, 1,  8,   8,  8,  600,   0, -8 }, // before the first frame
	{ 2, 4, 1,   200,          0, 3, 1,  8,   8,  8,  600,   0, 80 }, // after the last frame
	{ 2, 4, 1,   200,          0, 1, 1,  8,   8,  8,  600,   0,  0 }, // single-frame channel
	{ 3, 4, 1,   200,          0, 2, 1,  8,   8,  8,  200,   0,  8 }, // sample_vec3 over two chunked frames, mirror X
	{ 2, 4, 1,   200,          0, 2, 1,  8,  76,  4,  600,   0,  8 }, // caller weight 0.5 through an interpolated sample
};

static void dump_read_cases(void)
{
	for (size_t ci = 0; ci < sizeof(g_rd_cases) / sizeof(g_rd_cases[0]); ci++) {
		const long long *c = g_rd_cases[ci];
		int fn = (int)c[0], fmt = (int)c[1], enc = (int)c[2];
		long long dcount = c[3], doff = c[4];
		int nblk = (int)c[5], mirror = (int)c[6];
		double scale = (double)c[7] / 8.0;
		long long flags = c[8];
		double weight = (double)c[9] / 8.0;
		long long outc = c[10], trunc = c[11];
		double time = (double)c[12] / 16.0;

		size_t esz = (fmt == 1 || fmt == 2) ? 4 : 8;
		long long reals_pb = (fmt == 2 || fmt == 4) ? dcount * 3 : dcount;
		long long stride = reals_pb * (long long)esz;

		memset(g_rd_file, 0, sizeof(g_rd_file));
		for (int b = 0; b < nblk; b++) {
			for (long long i = 0; i < reals_pb; i++) {
				double v = rd_val((unsigned long long)b, (unsigned long long)i);
				unsigned char raw[8];
				memset(raw, 0, sizeof(raw));
				if (esz == 8) {
					uint64_t u = rd_bits(v);
					for (int j = 0; j < 8; j++) raw[j] = (unsigned char)((u >> (j * 8)) & 0xff);
				} else {
					float f = (float)v;
					uint32_t u;
					memcpy(&u, &f, 4);
					for (int j = 0; j < 4; j++) raw[j] = (unsigned char)((u >> (j * 8)) & 0xff);
				}
				unsigned long long p = (unsigned long long)(doff + (long long)b * stride + i * (long long)esz);
				for (size_t j = 0; j < esz; j++) {
					g_rd_file[(p + j) % RD_FILE_SIZE] = (enc == 2) ? raw[esz - 1 - j] : raw[j];
				}
			}
		}
		long long av = doff + (long long)nblk * stride - trunc;
		g_rd_avail = av < 0 ? 0 : (size_t)av;
		if (g_rd_avail > RD_FILE_SIZE) g_rd_avail = RD_FILE_SIZE;

		ufbx_cache_frame fr[4];
		memset(fr, 0, sizeof(fr));
		for (int b = 0; b < nblk; b++) {
			fr[b].time = (double)b;
			fr[b].filename = str_c("synth.rd");
			fr[b].file_format = UFBX_CACHE_FILE_FORMAT_MC;
			fr[b].mirror_axis = (ufbx_mirror_axis)mirror;
			fr[b].scale_factor = (ufbx_real)scale;
			fr[b].data_format = (ufbx_cache_data_format)fmt;
			fr[b].data_encoding = (ufbx_cache_data_encoding)enc;
			fr[b].data_offset = (uint64_t)(doff + (long long)b * stride);
			fr[b].data_count = (uint32_t)dcount;
			fr[b].data_element_bytes = (uint32_t)esz;
			fr[b].data_total_bytes = (uint64_t)stride;
		}

		ufbx_geometry_cache_data_opts opts;
		memset(&opts, 0, sizeof(opts));
		memset(g_rd_out, 0, sizeof(g_rd_out));
		g_rd_have_skip = (flags & 8) != 0;
		g_rd_infinite = (flags & 32) != 0;
		g_rd_error_first = (flags & 16) != 0;
		opts.ignore_transform = (flags & 1) != 0;
		opts.additive = (flags & 2) != 0;
		opts.use_weight = (flags & 4) != 0;
		opts.weight = (ufbx_real)weight;
		opts.open_file_cb.fn = rd_open_cb;

		if (flags & 64) {
			for (int i = 0; i < RD_OUT_DOUBLES; i++) g_rd_out[i] = 1000.5;
		}
		g_rd_pos = 0; g_rd_reads = 0; g_rd_skips = 0;
		g_fnv = 0xcbf29ce484222325ull;

		size_t n = 0;
		switch (fn) {
		case 0: n = ufbx_read_geometry_cache_real(&fr[0], (ufbx_real*)g_rd_out, (size_t)outc, &opts); break;
		case 1: n = ufbx_read_geometry_cache_vec3(&fr[0], (ufbx_vec3*)g_rd_out, (size_t)outc, &opts); break;
		case 2:
		case 3: {
			ufbx_cache_channel chan;
			memset(&chan, 0, sizeof(chan));
			chan.name = str_c("chan");
			chan.interpretation = UFBX_CACHE_INTERPRETATION_VERTEX_POSITION;
			chan.frames.data = fr;
			chan.frames.count = (size_t)nblk;
			chan.mirror_axis = (ufbx_mirror_axis)mirror;
			chan.scale_factor = (ufbx_real)scale;
			if (fn == 2) n = ufbx_sample_geometry_cache_real(&chan, time, (ufbx_real*)g_rd_out, (size_t)outc, &opts);
			else n = ufbx_sample_geometry_cache_vec3(&chan, time, (ufbx_vec3*)g_rd_out, (size_t)outc, &opts);
			break;
		}
		default: break;
		}

		uint64_t call_hash = g_fnv;
		g_fnv = 0xcbf29ce484222325ull;
		for (int i = 0; i < RD_OUT_DOUBLES; i++) fnv8(rd_bits(g_rd_out[i]));
		uint64_t data_hash = g_fnv;

		long long out_reals = (fn == 1 || fn == 3) ? outc * 3 : outc;
		printf("RD %zu %llu %llu ", ci, (unsigned long long)n, (unsigned long long)out_reals);
		printf("%016llx %016llx %d %d\n",
			(unsigned long long)call_hash, (unsigned long long)data_hash, g_rd_reads, g_rd_skips);

		// The caller's opts must survive untouched: C copies *user_opts into a local struct
		// (ufbx.c:32711-32718) and writes open_file_cb.fn / use_weight / weight / additive on the
		// copy only. Re-check after the call so a port that mutates the caller's object diverges.
		printf("RDO %zu %d %d %d %d %d ", ci, opts.ignore_transform, opts.additive, opts.use_weight,
			opts.open_file_cb.fn == rd_open_cb ? 1 : 0, opts.open_file_cb.user == NULL ? 1 : 0);
		printf("%016llx\n", (unsigned long long)rd_bits((double)opts.weight));
	}
}
int main(int argc, char **argv)
{
	dump_bezier();
	dump_skin();
	dump_blend();
	for (int i = 1; i < argc; i++) {
		dump_cache(argv[i]);
	}

	// Emitted last so the pre-existing records stay a byte-identical prefix of the output.
	dump_read_cases();
	dump_skin_catch();
	dump_blend_add_shape();

	printf("DONE %d\n", g_fail);
	return 0;
}
