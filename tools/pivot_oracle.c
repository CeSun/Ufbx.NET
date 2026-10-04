// Batch O oracle: the `pivot_handling` load-option dimension -- ufbxi_pre_finalize_scene's pivot
// block (ufbx.c:18329-18457), its `required` gate (ufbx.c:18120) and
// `scene->metadata.pivot_handling` (ufbx.c:23736).
//
// Built with `#include "ufbx.c"`, so it runs the original
//   ufbxi_pre_finalize_scene                          (ufbx.c:18115-18542)
//   its `required` gate                               (ufbx.c:18117-18124)
//   the pivot block                                   (ufbx.c:18329-18457)
//   ufbxi_pivot_nonzero / ufbxi_pivot_div             (ufbx.c:18091-18106)
//   ufbxi_init_synthetic_vec3_prop                    (ufbx.c:12475-12487)
//   ufbxi_sort_properties / ufbxi_deduplicate_properties
//   scene->metadata.pivot_handling                    (ufbx.c:23736)
// The C# twin is src/Ufbx/Parse/SceneBuild.cs:464-583 (`UfbxiSceneBuild.PreFinalizeScene`), repla-
// yed by tools/PivotCheck.
//
// Compile (the only recognised configuration, see HANDOFF_batch_O.md section 0):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx pivot_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o pivot_oracle.exe
// Run with the CWD at C:/Workspace/_analyze_ufbx (the corpus paths are relative to it) and the
// oracle/corpus passed by ABSOLUTE path -- MSYS re-encodes non-ASCII argv, and a relative corpus
// would resolve against the CWD anyway:
//   (cd C:/Workspace/_analyze_ufbx && C:/Workspace/ufbx-cs/tools/pivot_oracle.exe \
//        --list C:/Workspace/ufbx-cs/tools/pivot_corpus.txt > C:/Workspace/ufbx-cs/tools/pivot_oracle.txt)
// Do NOT copy the .exe into the shared reference tree: it is read-only, and running it by absolute
// path from that CWD produces byte-identical output.
//
// DESIGN: the variant table lives *only* here. Each variant re-emits what it fed to the loader as
// an `I` record, so the harness rebuilds the same options from those records instead of mirroring
// `k_variants[]` in a second language (same pattern as batches M and N).
//
// Grammar (space separated, one record per line; `<z>` = 64-bit lowercase hex -- for a real it is
// the IEEE bit pattern so -0.0 and NaN are distinguishable -- `<x>` = lowercase hex of raw bytes,
// an empty byte run is the single token `-`):
//   I <fi> <vi> <ph> <re> <gth>
//           input description of the variant (INPUT ONLY).
//             ph  = ufbx_load_opts.pivot_handling                 0 RETAIN / 1 ADJUST_TO_PIVOT /
//                   2 ADJUST_TO_ROTATION_PIVOT
//             re  = ufbx_load_opts.pivot_handling_retain_empties
//             gth = ufbx_load_opts.geometry_transform_handling    0 PRESERVE / 1 HELPER_NODES /
//                   2 MODIFY_GEOMETRY / 3 MODIFY_GEOMETRY_NO_FALLBACK
//   A <fi> <vi> <ok> <type> <dlen> <dx> <ilen> <ix>
//           the outcome of `ufbx_load_file` and the reported `ufbx_error`, byte for byte.
//   N <fi> <vi> <nodes> <metaPh>
//           scene->nodes.count and scene->metadata.pivot_handling -- the one field the pivot block
//           writes outside the nodes.
//   P <fi> <vi> <ni> <name> <parent> <depth> <hasAdj> <apt.x> <apt.y> <apt.z> <hasGt>
//           <gtt.x> <gtt.y> <gtt.z> <gtr.x> <gtr.y> <gtr.z> <gtr.w> <gts.x> <gts.y> <gts.z>
//           <rp.x> <rp.y> <rp.z> <sp.x> <sp.y> <sp.z> <so.x> <so.y> <so.z> <gt.x> <gt.y> <gt.z>
//           <scaleHelper> <geoHelper>
//           one node of the FINAL scene, in `scene->nodes` order -- every node, not just the ones
//           the pivot block touched, so a wrong "which nodes does it touch" predicate shows up as a
//           different record count and not only as a different hash.
//             name   = hex of node->name bytes
//             parent = parent's typed_id, -1 for the root
//             hasAdj = node->has_adjust_transform
//             apt    = node->adjust_pre_translation  <-- the pivot block's main output
//                      (ufbx.c:18440); note `ufbxt_hash_scene()` does NOT hash this field, so it is
//                      only visible here.
//             hasGt  = node->has_geometry_transform
//             gtt/gtr/gts = node->geometry_transform (a ufbx_transform: translation, rotation
//                      quaternion, scale)
//             rp/sp/so = RotationPivot / ScalingPivot / ScalingOffset as read back from
//                      node->props (the block rewrites these, ufbx.c:18386-18427)
//             gt     = GeometricTranslation read back from node->props
//   G <fi> <vi> <mi> <nv> <zv>
//           one mesh: mesh->num_vertices and the FNV of its raw vertex bytes. Present because
//           MODIFY_GEOMETRY / MODIFY_GEOMETRY_NO_FALLBACK push the pivot adjustment into the mesh
//           geometry itself.
//   V <fi> <vi> <ok> <z>
//           end to end: the golden generator's own `ufbxt_hash_scene()`.
//
// NOT COVERED (PORTING_NOTES.md #4):
//  * `ufbxi_push_zero()` / `ufbxi_push()` failing inside the pivot block (ufbx.c:18385/18397/
//    18418) -- the arena OOM tail; managed allocation cannot fail in the reference build.
//  * `ufbx_assert(!skip_geometry_transform)` (ufbx.c:18382): NDEBUG, and it is unreachable anyway
//    because `skip_geometry_transform` is only ever set under ADJUST_TO_ROTATION_PIVOT.

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

static void pz(uint64_t v) { printf(" %016llx", (unsigned long long) v); }

static void pzd(ufbx_real v)
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
	int ph;     // ufbx_pivot_handling: 0 RETAIN / 1 ADJUST_TO_PIVOT / 2 ADJUST_TO_ROTATION_PIVOT
	int re;     // pivot_handling_retain_empties
	int gth;    // ufbx_geometry_transform_handling: 0..3
} variant;

#define V(ph, re, gth) { ph, re, gth }

static const variant k_variants[] = {
	//   ph re gth
	V(    0,  0,  0 ), //  0 golden shape: default load opts (RETAIN + PRESERVE)
	V(    1,  0,  0 ), //  1 ADJUST_TO_PIVOT
	V(    1,  1,  0 ), //  2 ADJUST_TO_PIVOT, retain_empties (no effect: only read under ROTATION)
	V(    2,  0,  0 ), //  3 ADJUST_TO_ROTATION_PIVOT
	V(    2,  1,  0 ), //  4 ADJUST_TO_ROTATION_PIVOT + retain_empties (empties: skip -> can_modify)
	V(    0,  0,  1 ), //  5 RETAIN + helper nodes (control for the gth axis)
	V(    1,  0,  1 ), //  6 ADJUST_TO_PIVOT + helper nodes
	V(    2,  0,  1 ), //  7 ADJUST_TO_ROTATION_PIVOT + helper nodes
	V(    2,  1,  1 ), //  8 ... + retain_empties
	V(    0,  0,  2 ), //  9 RETAIN + modify geometry (control)
	V(    1,  0,  2 ), // 10 ADJUST_TO_PIVOT + modify geometry
	V(    2,  0,  2 ), // 11 ADJUST_TO_ROTATION_PIVOT + modify geometry
	V(    2,  1,  2 ), // 12 ... + retain_empties
	V(    0,  0,  3 ), // 13 RETAIN + modify geometry no fallback (control)
	V(    1,  0,  3 ), // 14 ADJUST_TO_PIVOT + no fallback  (instanced => can_modify_gt = false)
	V(    2,  0,  3 ), // 15 ADJUST_TO_ROTATION_PIVOT + no fallback
	V(    2,  1,  3 ), // 16 ... + retain_empties
	V(    1,  1,  1 ), // 17 ADJUST_TO_PIVOT + helper nodes + retain_empties
	V(    1,  1,  2 ), // 18 ADJUST_TO_PIVOT + modify geometry + retain_empties
	V(    1,  1,  3 ), // 19 ADJUST_TO_PIVOT + no fallback + retain_empties
	V(    0,  1,  0 ), // 20 RETAIN + retain_empties (control: re is inert under RETAIN)
	V(    0,  1,  2 ), // 21 RETAIN + retain_empties + modify geometry
	V(    0,  1,  3 ), // 22 RETAIN + retain_empties + no fallback
	V(    0,  1,  1 ), // 23 RETAIN + retain_empties + helper nodes
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
	printf("N %zu %zu %zu %d\n", fi, vi, scene->nodes.count, (int) scene->metadata.pivot_handling);
}

static void emit_P(size_t fi, size_t vi, size_t ni, const ufbx_node *node)
{
	printf("P %zu %zu %zu", fi, vi, ni);
	phex(node->name.data, node->name.length);
	printf(" %d %u %d",
		node->parent ? (int) node->parent->typed_id : -1,
		node->node_depth,
		node->has_adjust_transform ? 1 : 0);
	pvec(node->adjust_pre_translation);
	printf(" %d", node->has_geometry_transform ? 1 : 0);
	// geometry_transform is a ufbx_transform: translation, rotation quaternion, scale.
	pvec(node->geometry_transform.translation);
	pzd(node->geometry_transform.rotation.x); pzd(node->geometry_transform.rotation.y);
	pzd(node->geometry_transform.rotation.z); pzd(node->geometry_transform.rotation.w);
	pvec(node->geometry_transform.scale);
	// The four properties the pivot block reads / rewrites.
	pvec(ufbx_find_vec3(&node->props, "RotationPivot", ufbx_zero_vec3));
	pvec(ufbx_find_vec3(&node->props, "ScalingPivot", ufbx_zero_vec3));
	pvec(ufbx_find_vec3(&node->props, "ScalingOffset", ufbx_zero_vec3));
	pvec(ufbx_find_vec3(&node->props, "GeometricTranslation", ufbx_zero_vec3));
	printf(" %d %d\n", node->is_scale_helper ? 1 : 0, node->is_geometry_transform_helper ? 1 : 0);
}

static void emit_G(size_t fi, size_t vi, size_t mi, const ufbx_mesh *mesh)
{
	printf("G %zu %zu %zu %zu", fi, vi, mi, mesh->num_vertices);
	pz(hh_bytes(FNV_BASIS, mesh->vertices.data, mesh->num_vertices * sizeof(ufbx_vec3)));
	printf("\n");
}

static void emit_V(size_t fi, size_t vi, const ufbx_scene *scene)
{
	printf("V %zu %zu %d", fi, vi, scene != NULL ? 1 : 0);
	pz(scene != NULL ? ufbxt_hash_scene(scene, NULL) : 0);
	printf("\n");
}

// ------------------------------------------------------------------
// Driver
// ------------------------------------------------------------------

int main(int argc, char **argv)
{
	const char *list = argc > 2 && strcmp(argv[1], "--list") == 0 ? argv[2] : NULL;
	if (!list) { fprintf(stderr, "usage: pivot_oracle --list <corpus.txt>\n"); return 2; }

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
			printf("I %zu %zu %d %d %d\n", fi, vi, v->ph, v->re, v->gth);

			ufbx_load_opts load_opts = { 0 };
			load_opts.load_external_files = true;
			load_opts.ignore_missing_external_files = true;
			load_opts.target_axes = ufbx_axes_right_handed_y_up;
			load_opts.target_unit_meters = 1.0f;
			load_opts.pivot_handling = (ufbx_pivot_handling) v->ph;
			load_opts.pivot_handling_retain_empties = v->re != 0;
			load_opts.geometry_transform_handling = (ufbx_geometry_transform_handling) v->gth;

			ufbx_error error;
			ufbx_scene *scene = ufbx_load_file(path, &load_opts, &error);
			emit_A(fi, vi, scene != NULL, &error);
			if (!scene) { emit_V(fi, vi, NULL); continue; }

			emit_N(fi, vi, scene);
			for (size_t ni = 0; ni < scene->nodes.count; ni++) {
				emit_P(fi, vi, ni, scene->nodes.data[ni]);
			}
			for (size_t mi = 0; mi < scene->meshes.count; mi++) {
				emit_G(fi, vi, mi, scene->meshes.data[mi]);
			}
			emit_V(fi, vi, scene);

			ufbx_free_scene(scene);
		}

		fi++;
	}
	fclose(lf);
	return 0;
}
