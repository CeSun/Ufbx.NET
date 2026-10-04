#include "ufbx.c"
#include <stdio.h>
static void pd(double d) { uint64_t u; memcpy(&u, &d, 8); printf("%016llx ", (unsigned long long)u); }
static void pv3(const char *tag, ufbx_vertex_vec3 v) {
	printf(" %s vals:", tag); for (size_t k = 0; k < v.values.count; k++) { pd(v.values.data[k].x); pd(v.values.data[k].y); pd(v.values.data[k].z); }
	printf("idx:"); for (size_t k = 0; k < v.indices.count; k++) printf(" %u", v.indices.data[k]);
	printf(" ur=%d upv=%d exists=%d\n", v.value_reals, v.unique_per_vertex ? 1 : 0, v.exists ? 1 : 0);
}
int main(int argc, char **argv) {
	ufbx_load_opts opts = { 0 };
	opts.load_external_files = true;
	opts.ignore_missing_external_files = true;
	opts.evaluate_caches = true;
	opts.evaluate_skinning = true;
	opts.target_axes = ufbx_axes_right_handed_y_up;
	opts.target_unit_meters = 1.0f;
	ufbx_error error;
	ufbx_scene *v = ufbx_load_file(argv[1], &opts, &error);
	if (!v) { printf("FAIL\n"); return 1; }
	for (size_t i = 0; i < v->meshes.count; i++) {
		ufbx_mesh *m = v->meshes.data[i];
		printf("mesh %zu local=%d\n", i, m->skinned_is_local ? 1 : 0);
		pv3("sp", m->skinned_position);
		pv3("sn", m->skinned_normal);
	}
	return 0;
}
