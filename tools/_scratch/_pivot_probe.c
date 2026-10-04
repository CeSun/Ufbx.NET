// Batch O corpus probe: report, per file, which of the `pivot_handling` branches the
// file can actually reach. Built on the PUBLIC API only (loaded with default opts, i.e.
// UFBX_PIVOT_HANDLING_NONE, so `node->props` still holds the original pivots).
//
// Build (from C:/Workspace/_analyze_ufbx):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//     -I C:/Workspace/_analyze_ufbx _pivot_probe.c extra/ufbx_math.c -o _pivot_probe.exe
// Run:
//   ./_pivot_probe.exe --list <corpus.txt>      (paths relative to C:/Workspace/_analyze_ufbx)

#if !defined(UFBX_EXTERNAL_MATH)
#define UFBX_EXTERNAL_MATH
#endif
#include "ufbx.c"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int is_zero3(ufbx_vec3 v) { return v.x == 0.0 && v.y == 0.0 && v.z == 0.0; }

static int pivot_nonzero(ufbx_vec3 v)
{
	const double eps = 0.0009765625;
	return ufbx_fabs(v.x) >= eps || ufbx_fabs(v.y) >= eps || ufbx_fabs(v.z) >= eps;
}

static double abs_sum_diff(ufbx_vec3 a, ufbx_vec3 b)
{
	return ufbx_fabs(a.x - b.x) + ufbx_fabs(a.y - b.y) + ufbx_fabs(a.z - b.z);
}

int main(int argc, char **argv)
{
	const char *list = NULL;
	for (int i = 1; i < argc; i++) {
		if (!strcmp(argv[i], "--list") && i + 1 < argc) list = argv[++i];
	}
	if (!list) { fprintf(stderr, "usage: _pivot_probe --list <file>\n"); return 2; }

	FILE *f = fopen(list, "rb");
	if (!f) { fprintf(stderr, "cannot open %s\n", list); return 2; }

	char path[1024];
	// columns: nodes rpNz spNz soNz rotNeScl emptyPivot skinnedPivot instanced geoTrans
	//          minNzPivot nearEqPivots path
	//   minNzPivot  = smallest |component| over every non-zero RotationPivot/ScalingPivot/
	//                 ScalingOffset component in the file -- tells us whether any pivot is
	//                 "non-zero but below ufbxi_pivot_nonzero's 0.0009765625" (mutation
	//                 C_atp_pred_epsilon).
	//   nearEqPivots = nodes where 0 < |rp-sp|_1 <= pivot_epsilon(0.001f): the only input on which
	//                 `ufbxi_pivot_div`'s epsilon value can matter (mutation C_eps_zero).
	printf("# nodes rpNz spNz soNz rotNeScl emptyPivot skinnedPivot instanced geoTrans minNzPivot nearEqPivots path\n");
	while (fgets(path, sizeof(path), f)) {
		size_t len = strlen(path);
		while (len > 0 && (path[len - 1] == '\n' || path[len - 1] == '\r')) path[--len] = 0;
		if (len == 0 || path[0] == '#') continue;

		ufbx_error error;
		ufbx_scene *scene = ufbx_load_file(path, NULL, &error);
		if (!scene) {
			printf("load-error %s\n", path);
			continue;
		}

		size_t rp_nz = 0, sp_nz = 0, so_nz = 0, rot_ne_scl = 0;
		size_t empty_pivot = 0, skinned_pivot = 0, instanced = 0, geo_trans = 0;
		size_t near_eq = 0;
		double min_nz = 1e300;
		int have_min_nz = 0;

		for (size_t i = 0; i < scene->nodes.count; i++) {
			ufbx_node *node = scene->nodes.data[i];
			ufbx_vec3 rp = ufbx_find_vec3(&node->props, "RotationPivot", ufbx_zero_vec3);
			ufbx_vec3 sp = ufbx_find_vec3(&node->props, "ScalingPivot", ufbx_zero_vec3);
			ufbx_vec3 so = ufbx_find_vec3(&node->props, "ScalingOffset", ufbx_zero_vec3);

			int rp_nz_i = pivot_nonzero(rp);
			int sp_nz_i = pivot_nonzero(sp);
			int so_nz_i = pivot_nonzero(so);
			int any = rp_nz_i || sp_nz_i || so_nz_i;

			rp_nz += rp_nz_i ? 1 : 0;
			sp_nz += sp_nz_i ? 1 : 0;
			so_nz += so_nz_i ? 1 : 0;
			double err = abs_sum_diff(rp, sp);
			if (rp_nz_i && err > (double)0.001f) rot_ne_scl++;
			if (rp_nz_i && err > 0.0 && err <= (double)0.001f) near_eq++;

			// Smallest non-zero pivot component anywhere in the file.
			ufbx_vec3 pivots[3] = { rp, sp, so };
			for (int k = 0; k < 3; k++) {
				ufbx_real c[3] = { pivots[k].x, pivots[k].y, pivots[k].z };
				for (int j = 0; j < 3; j++) {
					double a = ufbx_fabs(c[j]);
					if (a > 0.0 && a < min_nz) { min_nz = a; have_min_nz = 1; }
				}
			}

			if (any && node->attrib_type == UFBX_ELEMENT_EMPTY) empty_pivot++;
			if (any && node->mesh && node->mesh->skin_deformers.count > 0) skinned_pivot++;
			if (node->mesh && node->mesh->instances.count > 1) instanced++;
			if (node->has_geometry_transform) geo_trans++;
		}

		printf("%zu %zu %zu %zu %zu %zu %zu %zu %zu %.17g %s %s\n",
			scene->nodes.count, rp_nz, sp_nz, so_nz, rot_ne_scl,
			empty_pivot, skinned_pivot, instanced, geo_trans,
			have_min_nz ? min_nz : -1.0, near_eq ? "yes" : "no", path);

		ufbx_free_scene(scene);
	}
	fclose(f);
	return 0;
}
