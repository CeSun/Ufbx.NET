// Batch P oracle: scale helper nodes -- `ufbxi_setup_scale_helper` (ufbx.c:12556-12602), the
// creation loop in `ufbxi_pre_finalize_scene` (ufbx.c:18478-18539), the connection remap
// (ufbx.c:18720-18745), the evaluate-side consumption (ufbx.c:22969-22979) and the bake-side
// consumption (ufbx.c:27280-27352 / 27421-27458).
//
// WHY THIS WAVE EXISTS (measured, not guessed -- see tools/_scratch/_sh_probe.txt):
//   inherit_mode_handling = PRESERVE (the default, and the ONLY value the 2179 goldens exercise
//   because `test/hash_scene.c` passes zeroed load opts) creates **zero** scale helpers in all
//   690 `data/*.fbx` files. So unlike batches N and O, this really is an uncovered region:
//     * helpers appear only under HELPER_NODES (255 nodes / 139 with animated Lcl Scaling)
//       and COMPENSATE (46 / 39).
//     * the bake-side `!scale_helper_t->constant_scale` branch (ufbx.c:27285) is reached in
//       5 files, all `maya_game_sausage_*_deform` plus `maya_mixed_inherit_mode_7700_ascii`.
//     * `has_recursive_scale_helper` (ufbx.c:18500-18540) is reached in **zero** files -- a
//       corpus gap, not a port defect.
//
// Built with `#include "ufbx.c"`, so it runs the original
//   ufbxi_setup_scale_helper                       (ufbx.c:12556-12602)
//   the creation loop                              (ufbx.c:18478-18539)
//   the connection remap                           (ufbx.c:18720-18745)
//   ufbxi_evaluate_transform (helper scale path)   (ufbx.c:22969-22979)
//   ufbxi_bake_node (scale_helper_t / _s)          (ufbx.c:27280-27352, 27421-27458)
// The C# twin is src/Ufbx.NET/Parse/SceneBuild.cs:605-668 + src/Ufbx.NET/Parse/Bake.cs:757-777/825-845,
// replayed by tools/ShCheck.
//
// Compile (the only recognised configuration, see HANDOFF_batch_P.md section 0):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx sh_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o sh_oracle.exe
// Run with the CWD at C:/Workspace/_analyze_ufbx (the corpus paths are relative to it) and the
// oracle/corpus passed by ABSOLUTE path -- MSYS re-encodes non-ASCII argv:
//   (cd C:/Workspace/_analyze_ufbx && C:/Workspace/ufbx-cs/tools/sh_oracle.exe \
//        --list C:/Workspace/ufbx-cs/tools/sh_corpus.txt > C:/Workspace/ufbx-cs/tools/sh_oracle.txt)
// Do NOT copy the .exe into the shared reference tree: it is read-only, and running it by absolute
// path from that CWD produces byte-identical output.
//
// DESIGN: the variant table lives *only* here. Each variant re-emits what it fed to the loader as
// an `I` record, so the harness rebuilds the same options from those records instead of mirroring
// `k_variants[]` in a second language (same pattern as batches M/N/O).
//
// Grammar (space separated, one record per line; `<z>` = 64-bit lowercase hex -- for a real it is
// the IEEE bit pattern so -0.0 and NaN are distinguishable -- `<x>` = lowercase hex of raw bytes,
// an empty byte run is the single token `-`):
//   I <fi> <vi> <imh> <gth> <bake>
//           input description of the variant (INPUT ONLY).
//             imh  = ufbx_load_opts.inherit_mode_handling  0 PRESERVE / 1 HELPER_NODES /
//                    2 COMPENSATE / 3 COMPENSATE_NO_FALLBACK / 4 IGNORE
//             gth  = ufbx_load_opts.geometry_transform_handling  0 PRESERVE / 1 HELPER_NODES /
//                    2 MODIFY_GEOMETRY / 3 MODIFY_GEOMETRY_NO_FALLBACK
//             bake = emit bake records (0/1)
//   A <fi> <vi> <ok> <type> <dlen> <dx> <ilen> <ix>
//           the outcome of `ufbx_load_file` and the reported `ufbx_error`, byte for byte.
//   N <fi> <vi> <nodes> <numHelpers> <numWithHelper> <numRecursive> <numCompensateParent>
//           scene-wide counts. `numRecursive` is the corpus-gap counter (ufbx.c:18500-18540):
//           it is 0 for every file in `data/`.
//   S <fi> <vi> <ni> <name> <parent> <depth> <isScaleHelper> <helperTid> <inheritMode>
//           <origInheritMode> <isScaleCompensateParent> <isGeoHelper> <lclS.x> <lclS.y> <lclS.z>
//           <inheritScaleNodeTid>
//           one node of the FINAL scene, in `scene->nodes` order -- every node, not just the
//           helpers, so a wrong "which nodes get a helper" predicate shows up as a different
//           record VALUE (the count is fixed by `N`) and not only as a different hash.
//             helperTid          = node->scale_helper ? typed_id : -1
//             inheritScaleNodeTid= node->inherit_scale_node ? typed_id : -1
//             lclS               = node->local_transform.scale (moved into the helper by
//                                  ufbxi_setup_scale_helper, ufbx.c:12587-12589)
//   X <fi> <vi> <ni> <tix> <t> <tx> <ty> <tz> <qx> <qy> <qz> <qw> <sx> <sy> <sz>
//           `ufbx_evaluate_transform()` at one of three sample times (only when the file has an
//           animation). This is what exercises ufbx.c:22969-22979 -- the parent's *helper* scale
//           being folded into the child translation -- which the static scene hash cannot see.
//   V <fi> <vi> <ok> <z>
//           end to end: the golden generator's own `ufbxt_hash_scene()` (it hashes
//           `scale_helper`, `is_scale_helper`, `is_scale_compensate_parent` and every transform
//           matrix -- test/hash_scene.h:432/458/459/444-447).
//   B <fi> <vi> <ni> <constT> <constR> <constS> <nT> <nR> <nS> <hashT> <hashR> <hashS>
//           <t0> <t0.x> <t0.y> <t0.z> <tN> ... <s0> ... <sN> ...
//           one baked node (only when `bake` is set). `hash*` is FNV-1a over every key's
//           time / value / flags; the first and last T and S keys are spelled out so a
//           difference localises instead of only showing up as a different hash.
//
// NOT COVERED (PORTING_NOTES.md #4):
//  * `ufbxi_push_synthetic_element()` / `ufbxi_push()` failing inside
//    `ufbxi_setup_scale_helper` -- the arena OOM tail; managed allocation cannot fail.

#if !defined(UFBX_EXTERNAL_MATH)
#define UFBX_EXTERNAL_MATH
#endif
#include "ufbx.c"

// The golden generator's own scene hasher, included exactly as `test/hash_scene.c:6` does.
#include "test/hash_scene.h"

// ------------------------------------------------------------------
// Output helpers
// ------------------------------------------------------------------

static const uint64_t FNV_BASIS = UINT64_C(0xcbf29ce484222325);
static const uint64_t FNV_PRIME = UINT64_C(0x100000001b3);

static uint64_t hh_bytes(uint64_t h, const void *data, size_t size)
{
	const uint8_t *p = (const uint8_t*) data;
	if (!p) return h;
	for (size_t i = 0; i < size; i++) {
		h ^= (uint64_t) p[i];
		h *= FNV_PRIME;
	}
	return h;
}

static uint64_t hh_real(uint64_t h, ufbx_real v)
{
	uint64_t bits;
	memcpy(&bits, &v, 8);
	return hh_bytes(h, &bits, 8);
}

static uint64_t hh_u32(uint64_t h, uint32_t v)
{
	return hh_bytes(h, &v, 4);
}

static void pz(uint64_t v) { printf(" %016llx", (unsigned long long) v); }

static void pzd(ufbx_real v)
{
	uint64_t bits;
	memcpy(&bits, &v, 8);
	pz(bits);
}

static void pvd(double v)
{
	uint64_t bits;
	memcpy(&bits, &v, 8);
	pz(bits);
}

static void pvec(ufbx_vec3 v) { pzd(v.x); pzd(v.y); pzd(v.z); }

static void phex(const char *data, size_t length)
{
	printf(" %zu ", length);
	if (length == 0) { printf("-"); return; }
	if (!data) { printf("null"); return; }
	for (size_t i = 0; i < length; i++) printf("%02x", (unsigned char) data[i]);
}

// ------------------------------------------------------------------
// Variants
// ------------------------------------------------------------------

typedef struct {
	int imh;    // ufbx_inherit_mode_handling: 0..4
	int gth;    // ufbx_geometry_transform_handling: 0..3
	int bake;   // emit bake records
} variant;

#define V(imh, gth, bake) { imh, gth, bake }

static const variant k_variants[] = {
	//  imh gth bake
	V(    0,  0,  0 ), //  0 golden shape: PRESERVE -- creates NO scale helpers (the whole point)
	V(    1,  0,  0 ), //  1 HELPER_NODES -- the main creation path
	V(    2,  0,  0 ), //  2 COMPENSATE -- `ref = constant_scale.x` instead of 1.0
	V(    3,  0,  0 ), //  3 COMPENSATE_NO_FALLBACK -- gate short-circuits, no helpers
	V(    4,  0,  0 ), //  4 IGNORE -- no helpers
	V(    1,  0,  1 ), //  5 HELPER_NODES + bake -- the animated-helper branch (ufbx.c:27285)
	V(    2,  0,  1 ), //  6 COMPENSATE + bake
	V(    0,  0,  1 ), //  7 PRESERVE + bake (control: bake with zero helpers)
	V(    3,  0,  1 ), //  8 COMPENSATE_NO_FALLBACK + bake (control)
	V(    4,  0,  1 ), //  9 IGNORE + bake (control)
	V(    1,  1,  0 ), // 10 HELPER_NODES + geometry helper nodes -- aims at the recursive path
	V(    1,  1,  1 ), // 11 ... + bake
	V(    1,  2,  0 ), // 12 HELPER_NODES + MODIFY_GEOMETRY
	V(    1,  3,  1 ), // 13 HELPER_NODES + MODIFY_GEOMETRY_NO_FALLBACK + bake
};
static const size_t k_num_variants = sizeof(k_variants) / sizeof(k_variants[0]);

// ------------------------------------------------------------------
// Records
// ------------------------------------------------------------------

static void emit_A(size_t fi, size_t vi, bool ok, const ufbx_error *error)
{
	printf("A %zu %zu %d %d", fi, vi, ok ? 1 : 0, (int) error->type);
	phex(error->description.data, error->description.length);
	phex(error->info, error->info_length);
	printf("\n");
}

static void emit_N(size_t fi, size_t vi, const ufbx_scene *scene)
{
	size_t helpers = 0, with_helper = 0, recursive = 0, compensate_parent = 0;
	for (size_t ni = 0; ni < scene->nodes.count; ni++) {
		const ufbx_node *node = scene->nodes.data[ni];
		if (node->is_scale_helper) helpers++;
		if (node->scale_helper) with_helper++;
		if (node->is_scale_helper && node->parent && node->parent->is_scale_helper) recursive++;
		if (node->is_scale_compensate_parent) compensate_parent++;
	}
	printf("N %zu %zu %zu %zu %zu %zu %zu\n",
		fi, vi, scene->nodes.count, helpers, with_helper, recursive, compensate_parent);
}

static void emit_S(size_t fi, size_t vi, size_t ni, const ufbx_node *node)
{
	printf("S %zu %zu %zu", fi, vi, ni);
	phex(node->name.data, node->name.length);
	printf(" %d %u %d %d %d %d %d %d",
		node->parent ? (int) node->parent->typed_id : -1,
		node->node_depth,
		node->is_scale_helper ? 1 : 0,
		node->scale_helper ? (int) node->scale_helper->typed_id : -1,
		(int) node->inherit_mode,
		(int) node->original_inherit_mode,
		node->is_scale_compensate_parent ? 1 : 0,
		node->is_geometry_transform_helper ? 1 : 0);
	pvec(node->local_transform.scale);
	printf(" %d\n", node->inherit_scale_node ? (int) node->inherit_scale_node->typed_id : -1);
}

static void emit_X(size_t fi, size_t vi, size_t ni, size_t tix, double time, const ufbx_transform *t)
{
	printf("X %zu %zu %zu %zu", fi, vi, ni, tix);
	pvd(time);
	pvec(t->translation);
	pzd(t->rotation.x); pzd(t->rotation.y); pzd(t->rotation.z); pzd(t->rotation.w);
	pvec(t->scale);
	printf("\n");
}

static void emit_V(size_t fi, size_t vi, const ufbx_scene *scene)
{
	printf("V %zu %zu %d", fi, vi, scene != NULL ? 1 : 0);
	pz(scene != NULL ? ufbxt_hash_scene(scene, NULL) : 0);
	printf("\n");
}

static uint64_t hash_vec3_keys(uint64_t h, const ufbx_baked_vec3 *keys, size_t count)
{
	for (size_t i = 0; i < count; i++) {
		uint64_t bits;
		memcpy(&bits, &keys[i].time, 8);
		h = hh_bytes(h, &bits, 8);
		h = hh_real(h, keys[i].value.x);
		h = hh_real(h, keys[i].value.y);
		h = hh_real(h, keys[i].value.z);
		h = hh_u32(h, (uint32_t) keys[i].flags);
	}
	return h;
}

static uint64_t hash_quat_keys(uint64_t h, const ufbx_baked_quat *keys, size_t count)
{
	for (size_t i = 0; i < count; i++) {
		uint64_t bits;
		memcpy(&bits, &keys[i].time, 8);
		h = hh_bytes(h, &bits, 8);
		h = hh_real(h, keys[i].value.x);
		h = hh_real(h, keys[i].value.y);
		h = hh_real(h, keys[i].value.z);
		h = hh_real(h, keys[i].value.w);
		h = hh_u32(h, (uint32_t) keys[i].flags);
	}
	return h;
}

static void emit_B(size_t fi, size_t vi, size_t ni, const ufbx_baked_node *bn)
{
	printf("B %zu %zu %zu %d %d %d %zu %zu %zu",
		fi, vi, ni,
		bn->constant_translation ? 1 : 0,
		bn->constant_rotation ? 1 : 0,
		bn->constant_scale ? 1 : 0,
		bn->translation_keys.count, bn->rotation_keys.count, bn->scale_keys.count);
	pz(hash_vec3_keys(FNV_BASIS, bn->translation_keys.data, bn->translation_keys.count));
	pz(hash_quat_keys(FNV_BASIS, bn->rotation_keys.data, bn->rotation_keys.count));
	pz(hash_vec3_keys(FNV_BASIS, bn->scale_keys.data, bn->scale_keys.count));
	// First and last translation / scale keys, spelled out so a difference localises.
	for (int which = 0; which < 4; which++) {
		const ufbx_baked_vec3 *keys = which < 2 ? bn->translation_keys.data : bn->scale_keys.data;
		size_t count = which < 2 ? bn->translation_keys.count : bn->scale_keys.count;
		size_t ix = (which & 1) && count > 0 ? count - 1 : 0;
		if (count == 0) { printf(" -"); pzd(0); pzd(0); pzd(0); }
		else { pvd(keys[ix].time); pvec(keys[ix].value); }
	}
	printf("\n");
}

// ------------------------------------------------------------------
// Driver
// ------------------------------------------------------------------

int main(int argc, char **argv)
{
	const char *list = argc > 2 && strcmp(argv[1], "--list") == 0 ? argv[2] : NULL;
	if (!list) { fprintf(stderr, "usage: sh_oracle --list <corpus.txt>\n"); return 2; }

	FILE *lf = fopen(list, "rb");
	if (!lf) { fprintf(stderr, "cannot open corpus list\n"); return 2; }

	char path[2048];
	size_t fi = 0;
	while (fgets(path, sizeof(path), lf)) {
		size_t len = strlen(path);
		while (len > 0 && (path[len - 1] == '\n' || path[len - 1] == '\r')) path[--len] = 0;
		if (len == 0 || path[0] == '#') continue; // `ReadCorpus()` in the harness skips `#` too.

		for (size_t vi = 0; vi < k_num_variants; vi++) {
			const variant *v = &k_variants[vi];

			// I: the input record. The harness rebuilds its options from these three numbers.
			printf("I %zu %zu %d %d %d\n", fi, vi, v->imh, v->gth, v->bake);

			ufbx_load_opts load_opts = { 0 };
			load_opts.load_external_files = true;
			load_opts.ignore_missing_external_files = true;
			load_opts.target_axes = ufbx_axes_right_handed_y_up;
			load_opts.target_unit_meters = 1.0f;
			load_opts.inherit_mode_handling = (ufbx_inherit_mode_handling) v->imh;
			load_opts.geometry_transform_handling = (ufbx_geometry_transform_handling) v->gth;

			ufbx_error error;
			ufbx_scene *scene = ufbx_load_file(path, &load_opts, &error);
			emit_A(fi, vi, scene != NULL, &error);
			if (!scene) { emit_V(fi, vi, NULL); continue; }

			emit_N(fi, vi, scene);
			for (size_t ni = 0; ni < scene->nodes.count; ni++) {
				emit_S(fi, vi, ni, scene->nodes.data[ni]);
			}

			if (scene->anim) {
				double begin = scene->anim->time_begin, end = scene->anim->time_end;
				double times[3];
				times[0] = begin;
				times[1] = begin + (end - begin) * 0.37;
				times[2] = end;
				for (size_t tix = 0; tix < 3; tix++) {
					for (size_t ni = 0; ni < scene->nodes.count; ni++) {
						ufbx_transform t = ufbx_evaluate_transform(scene->anim, scene->nodes.data[ni], times[tix]);
						emit_X(fi, vi, ni, tix, times[tix], &t);
					}
				}
			}

			emit_V(fi, vi, scene);

			if (v->bake && scene->anim) {
				ufbx_bake_opts bake_opts = { 0 };
				ufbx_error berr;
				ufbx_baked_anim *bake = ufbx_bake_anim(scene, scene->anim, &bake_opts, &berr);
				if (!bake) {
					printf("A %zu %zu 0 %d", fi, vi, (int) berr.type);
					phex(berr.description.data, berr.description.length);
					phex(berr.info, berr.info_length);
					printf("\n");
				} else {
					for (size_t ni = 0; ni < bake->nodes.count; ni++) {
						emit_B(fi, vi, ni, &bake->nodes.data[ni]);
					}
					ufbx_free_baked_anim(bake);
				}
			}

			ufbx_free_scene(scene);
		}

		fi++;
	}
	fclose(lf);
	return 0;
}
