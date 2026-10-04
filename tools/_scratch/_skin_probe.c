// Throwaway probe (batch N): classify data/*.fbx by deformer content so the skin corpus is chosen
// from evidence rather than from file names.
//
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx _skin_probe.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o _skin_probe.exe
// Run from C:/Workspace/_analyze_ufbx with `--list <file list>`.
//
// Output: `<mesh> <skin> <blend> <cache> <verts> <indices> <path>`

#if !defined(UFBX_EXTERNAL_MATH)
#define UFBX_EXTERNAL_MATH
#endif
#include "ufbx.c"

int main(int argc, char **argv)
{
	const char *list = argc > 2 && strcmp(argv[1], "--list") == 0 ? argv[2] : NULL;
	if (!list) { fprintf(stderr, "usage: _skin_probe --list <file>\n"); return 2; }

	FILE *lf = fopen(list, "rb");
	if (!lf) { fprintf(stderr, "cannot open list\n"); return 2; }

	char path[1024];
	while (fgets(path, sizeof(path), lf)) {
		size_t len = strlen(path);
		while (len > 0 && (path[len - 1] == '\n' || path[len - 1] == '\r')) path[--len] = 0;
		if (len == 0) continue;

		ufbx_load_opts opts = { 0 };
		opts.load_external_files = true;
		opts.ignore_missing_external_files = true;
		opts.evaluate_caches = true;
		opts.evaluate_skinning = true;
		opts.target_axes = ufbx_axes_right_handed_y_up;
		opts.target_unit_meters = 1.0f;

		ufbx_error error;
		ufbx_scene *scene = ufbx_load_file(path, &opts, &error);
		if (!scene) {
			printf("- - - - - - load-error %s\n", path);
			continue;
		}

		size_t num_skin = scene->skin_deformers.count;
		size_t num_blend = scene->blend_deformers.count;
		size_t num_cache = scene->cache_deformers.count;
		size_t max_verts = 0, max_indices = 0;
		// The two shapes the batch-N mutation sweep could not bite on:
		//   max_skin_per_mesh -- C takes `skin_deformers.data[0]` only, with a
		//     "TODO: What should we do about multiple skins??" (ufbx.c:25127).
		//   zero_vert_deformer -- a mesh that has deformers but num_vertices == 0, which
		//     `ufbxi_evaluate_skinning` skips outright (ufbx.c:25079).
		size_t max_skin_per_mesh = 0, zero_vert_deformer = 0;
		for (size_t i = 0; i < scene->meshes.count; i++) {
			ufbx_mesh *mesh = scene->meshes.data[i];
			if (mesh->num_vertices > max_verts) max_verts = mesh->num_vertices;
			if (mesh->num_indices > max_indices) max_indices = mesh->num_indices;
			if (mesh->skin_deformers.count > max_skin_per_mesh) {
				max_skin_per_mesh = mesh->skin_deformers.count;
			}
			bool has_deformer = mesh->skin_deformers.count > 0 || mesh->blend_deformers.count > 0 ||
				mesh->cache_deformers.count > 0;
			if (has_deformer && mesh->num_vertices == 0) zero_vert_deformer++;
		}
		printf("%zu %zu %zu %zu %zu %zu %zu %zu %s\n", scene->meshes.count, num_skin, num_blend,
			num_cache, max_verts, max_indices, max_skin_per_mesh, zero_vert_deformer, path);
		ufbx_free_scene(scene);
	}

	fclose(lf);
	return 0;
}
