// Batch N oracle: the skinning evaluation body on the *evaluate* path -- ufbxi_evaluate_skinning
// reached from ufbxi_evaluate_imp (ufbx.c:26413-26419), with the load-path call
// (ufbx.c:25367-25373) as its control.
//
// Built with `#include "ufbx.c"`, so it runs the original
//   ufbxi_evaluate_skinning                                     (ufbx.c:25063-25177)
//   its load-path call site in ufbxi_load_imp                    (ufbx.c:25367-25373)
//   its evaluate-path call site in ufbxi_evaluate_imp            (ufbx.c:26413-26419)
//   ufbx_get_skin_vertex_matrix / ufbx_catch_get_skin_vertex_matrix (ufbx.c:31936-32026)
//   ufbx_get_blend_shape_offset_index / _vertex_offset           (ufbx.c:32028-32048)
//   ufbx_get_blend_vertex_offset                                 (ufbx.c:32050-32068)
//   ufbx_add_blend_shape_vertex_offsets / _blend_vertex_offsets  (ufbx.c:32070-32103)
//   ufbx_compute_topology / _generate_normal_mapping / _compute_normals (S4c, ufbx.c:33176 / 32588 / 32622)
//   ufbxi_evaluate_scene / ufbxi_evaluate_imp                    (ufbx.c:26454-26491 / 26113-26452)
// The C# twin is src/Ufbx.NET/Parse/SceneOpts.cs (`UfbxiSceneOpts.EvaluateSkinning`) driven from
// src/Ufbx.NET/Parse/Load.cs:144-150 and src/Ufbx.NET/Parse/EvaluateScene.cs, replayed by tools/SkinCheck.
//
// Compile (the only recognised configuration, see HANDOFF_batch_N.md section 0):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx skin_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o skin_oracle.exe
// Run with the CWD at C:/Workspace/_analyze_ufbx (the corpus paths are relative to it) and the
// oracle/corpus passed by ABSOLUTE path -- MSYS re-encodes non-ASCII argv, and a relative corpus
// would resolve against the CWD anyway:
//   (cd C:/Workspace/_analyze_ufbx && C:/Workspace/ufbx-cs/tools/skin_oracle.exe \
//        --list C:/Workspace/ufbx-cs/tools/skin_corpus.txt > C:/Workspace/ufbx-cs/tools/skin_oracle.txt)
// Do NOT copy the .exe into the shared reference tree: it is read-only, and running it by absolute
// path from that CWD produces byte-identical output.
//
// DESIGN: the variant table lives *only* here. Each variant re-emits what it fed to the loader and
// to `ufbx_evaluate_scene()` as an `I` record, so the harness rebuilds the same options from those
// records instead of mirroring `k_variants[]` in a second language (same pattern as batch M's `I`).
//
// Grammar (space separated, one record per line; `<z>` = FNV-1a-64 lowercase hex, `<x>` = lowercase
// hex of raw bytes, an empty byte run is the single token `-`):
//   I <fi> <vi> <frame> <loadExt> <loadCaches> <loadSkin> <evalSkin> <evalCaches> <evalExt> <flags>
//           input description of the variant (INPUT ONLY). frame < 0 means "do not call
//           ufbx_evaluate_scene at all" -- the records then describe the loaded base scene.
//   A <fi> <vi> <stage> <ok> <type> <dlen> <dx> <ilen> <ix>
//           one API outcome and the reported `ufbx_error`, byte for byte. stage: 0 = ufbx_load_file,
//           1 = ufbx_evaluate_scene (emitted only when frame >= 0).
//   K <fi> <vi> <mi> <local> <npos> <zpos> <exists> <uniq> <nnorm> <znorm> <nidx> <zidx> <vr> <gen> <nv>
//           one mesh of the FINAL scene, in `scene->meshes` order -- every mesh, not just the
//           skinned ones, so a wrong "which meshes does skinning touch" predicate shows up as a
//           different record count and not only as a different hash.
//             local  = mesh->skinned_is_local            (ufbx.c:25097 / 25135)
//             npos   = mesh->skinned_position.values.count
//             zpos   = FNV of the raw bytes of mesh->skinned_position.values
//             exists = mesh->skinned_normal.exists
//             uniq   = mesh->skinned_normal.unique_per_vertex
//             nnorm  = mesh->skinned_normal.values.count, znorm = FNV of its bytes
//             nidx   = mesh->skinned_normal.indices.count, zidx = FNV of its bytes
//             vr     = mesh->skinned_normal.value_reals
//             gen    = mesh->generated_normals
//             nv     = mesh->num_vertices
//   V <fi> <vi> <ok> <z>
//           end to end: the golden generator's own `ufbxt_hash_scene()` of the final scene. This is
//           the record that ties the batch to the goldens, which cover the load-path call only.
//
// NOT COVERED (PORTING_NOTES.md #4 and the feature-guard tail):
//  * `ufbxi_push()` failing (`ufbxi_check_err` -> "Failed to evaluate" / "Failed to load"), i.e. the
//    out-of-memory tail of both call sites: managed allocation cannot fail in the reference build.
//  * `UFBXI_FEATURE_SKINNING_EVALUATION == 0` (ufbx.c:25066/25170-25175): the guard is 1 in this
//    build (ufbx.c:90).
//  * A scene with no meshes: `max_skinned_indices` stays 0 and `ufbxi_push(buf_tmp, ..., 0)` is a
//    valid pointer; nothing is observable either way.

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
	int frame;          // < 0 => no ufbx_evaluate_scene() call at all
	int load_ext;       // ufbx_load_opts.load_external_files
	int load_caches;    // ufbx_load_opts.evaluate_caches
	int load_skin;      // ufbx_load_opts.evaluate_skinning
	int eval_skin;      // ufbx_evaluate_opts.evaluate_skinning
	int eval_caches;    // ufbx_evaluate_opts.evaluate_caches
	int eval_ext;       // ufbx_evaluate_opts.load_external_files
	uint32_t eval_flags;
} variant;

#define V(frame, load_ext, load_caches, load_skin, eval_skin, eval_caches, eval_ext, flags) \
	{ frame, load_ext, load_caches, load_skin, eval_skin, eval_caches, eval_ext, flags }

static const variant k_variants[] = {
	//   frame lext lcache lskin eskin ecache eext flags
	V(    -1,   1,   1,     0,    0,    0,    0,   0 ), //  0 nothing skinned at all (control)
	V(    -1,   1,   1,     1,    0,    0,    0,   0 ), //  1 load-path skinning only (golden shape)
	V(     0,   1,   1,     0,    0,    0,    0,   0 ), //  2 evaluate with skinning off everywhere
	V(     0,   1,   1,     1,    0,    0,    0,   0 ), //  3 evaluate, base already skinned at load
	V(     0,   1,   1,     0,    1,    0,    0,   0 ), //  4 evaluate-path skinning on a raw base
	V(     0,   1,   1,     1,    1,    0,    0,   0 ), //  5 both, time = time_begin
	V(     1,   1,   1,     1,    1,    0,    0,   0 ), //  6
	V(     9,   1,   1,     1,    1,    0,    0,   0 ), //  7
	V(    25,   1,   1,     1,    1,    0,    0,   0 ), //  8
	V(     9,   1,   1,     0,    1,    0,    0,   0 ), //  9 evaluate-only, several frames in
	V(     9,   1,   1,     1,    1,    1,    0,   0 ), // 10 + evaluate_caches
	V(     9,   1,   1,     1,    1,    0,    1,   0 ), // 11 + evaluate load_external_files
	V(     9,   1,   1,     1,    1,    1,    1,   0 ), // 12 both cache knobs
	V(     9,   1,   0,     1,    1,    1,    1,   0 ), // 13 load evaluate_caches off
	V(     9,   0,   1,     1,    1,    0,    0,   0 ), // 14 load external files off
	V(     9,   1,   1,     1,    1,    0,    0,   1 ), // 15 UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION
	V(     4,   1,   1,     1,    1,    0,    0,   1 ), // 16 same flag, another frame
};
static const size_t k_num_variants = sizeof(k_variants) / sizeof(k_variants[0]);

// ------------------------------------------------------------------
// Records
// ------------------------------------------------------------------

static void emit_A(size_t fi, size_t vi, int stage, bool ok, const ufbx_error *error)
{
	printf("A %zu %zu %d %d %d", fi, vi, stage, ok ? 1 : 0, (int) error->type);
	phex(error->description.data, error->description.length);
	phex(error->info, error->info_length);
	printf("\n");
}

// One mesh of the final scene. `skinned_position`/`skinned_normal` are the two fields
// `ufbxi_evaluate_skinning` writes (ufbx.c:25138 / 25143-25168) plus `generated_normals`.
static void emit_K(size_t fi, size_t vi, size_t mi, const ufbx_mesh *mesh)
{
	const ufbx_vertex_vec3 *pos = &mesh->skinned_position;
	const ufbx_vertex_vec3 *nrm = &mesh->skinned_normal;

	printf("K %zu %zu %zu %d %zu", fi, vi, mi, mesh->skinned_is_local ? 1 : 0,
		pos->values.count);
	pz(hh_bytes(FNV_BASIS, pos->values.data, pos->values.count * sizeof(ufbx_vec3)));
	printf(" %d %d %zu", nrm->exists ? 1 : 0, nrm->unique_per_vertex ? 1 : 0, nrm->values.count);
	pz(hh_bytes(FNV_BASIS, nrm->values.data, nrm->values.count * sizeof(ufbx_vec3)));
	printf(" %zu", nrm->indices.count);
	pz(hh_bytes(FNV_BASIS, nrm->indices.data, nrm->indices.count * sizeof(uint32_t)));
	printf(" %d %d %zu\n", (int) nrm->value_reals, mesh->generated_normals ? 1 : 0,
		mesh->num_vertices);
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
	if (!list) { fprintf(stderr, "usage: skin_oracle --list <corpus.txt>\n"); return 2; }

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

			// I: the input record. The harness rebuilds its options from these ten numbers.
			printf("I %zu %zu %d %d %d %d %d %d %d %u\n", fi, vi, v->frame, v->load_ext,
				v->load_caches, v->load_skin, v->eval_skin, v->eval_caches, v->eval_ext,
				v->eval_flags);

			ufbx_load_opts load_opts = { 0 };
			load_opts.load_external_files = v->load_ext != 0;
			load_opts.ignore_missing_external_files = true;
			load_opts.evaluate_caches = v->load_caches != 0;
			load_opts.evaluate_skinning = v->load_skin != 0;
			load_opts.target_axes = ufbx_axes_right_handed_y_up;
			load_opts.target_unit_meters = 1.0f;

			ufbx_error error;
			ufbx_scene *scene = ufbx_load_file(path, &load_opts, &error);
			emit_A(fi, vi, 0, scene != NULL, &error);
			if (!scene) {
				emit_V(fi, vi, NULL);
				continue;
			}

			if (v->frame >= 0) {
				ufbx_evaluate_opts eval_opts = { 0 };
				eval_opts.evaluate_skinning = v->eval_skin != 0;
				eval_opts.evaluate_caches = v->eval_caches != 0;
				eval_opts.load_external_files = v->eval_ext != 0;
				eval_opts.evaluate_flags = v->eval_flags;

				// test/hash_scene.c:134 -- the golden's own time formula.
				double time = scene->anim->time_begin +
					(double) v->frame / scene->settings.frames_per_second;

				ufbx_error eval_error;
				ufbx_scene *state = ufbx_evaluate_scene(scene, NULL, time, &eval_opts, &eval_error);
				emit_A(fi, vi, 1, state != NULL, &eval_error);
				ufbx_free_scene(scene);
				scene = state;
			}

			emit_V(fi, vi, scene);
			if (scene) {
				for (size_t mi = 0; mi < scene->meshes.count; mi++) {
					emit_K(fi, vi, mi, scene->meshes.data[mi]);
				}
				ufbx_free_scene(scene);
			}
		}
		fi++;
	}

	fclose(lf);
	return 0;
}
