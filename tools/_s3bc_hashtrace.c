// Batch divergence tracer: prints the cumulative FNV-1a-64 state after each top-level scene
// list, for every file on the command line. Mirror of tools/_S3Dbg (C#) `trace` mode.
// Build (mandatory flags):
//   zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
//     C:/Workspace/ufbx-cs/tools/_s3bc_hashtrace.c -o C:/Workspace/ufbx-cs/tools/_s3bc_hashtrace.exe
#include "ufbx.c"
#include "test/hash_scene.h"
#include <stdio.h>
static ufbxt_hash h;
static void show(const char *tag) { printf("%s %016llx\n", tag, (unsigned long long)h.state); }
// Besides the cumulative list state, each item is re-hashed from the initial FNV state
// (`I <tag> <index> <hash>`), so a mismatch names the exact diverging object.
#define HL(tag, list, fn) do { ufbxt_hash_list(&h, list, fn); show(tag); \
	for (size_t i = 0; i < (list).count; i++) { \
		ufbxt_hash ih; ufbxt_hash_init(&ih, NULL); fn(&ih, (list).data[i]); \
		printf("I %s %zu %016llx\n", tag, i, (unsigned long long)ih.state); \
	} } while (0)
#define HLP(tag, list, fn) do { ufbxt_hash_list_ptr(&h, list, fn); show(tag); \
	for (size_t i = 0; i < (list).count; i++) { \
		ufbxt_hash ih; ufbxt_hash_init(&ih, NULL); fn(&ih, &(list).data[i]); \
		printf("I %s %zu %016llx\n", tag, i, (unsigned long long)ih.state); \
	} } while (0)

// Field dump of every anim curve keyframe (env S3BC_KEYDUMP=1): raw bit patterns, so a
// C#/C mismatch names the field rather than just the object.
static uint32_t f2b(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
static uint64_t d2b(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }

// Field-level mesh dump (env S3BC_MESHDUMP=1): each mesh field group is hashed from the initial
// FNV state and printed as `G <mesh_index> <group> <hash>`, mirroring Dbg.MeshDump on the C# side.
#define MF(mi, name, code) do { ufbxt_hash fh; ufbxt_hash_init(&fh, NULL); code; \
	printf("G %zu %s %016llx\n", (size_t)(mi), name, (unsigned long long)fh.state); } while (0)
static void mesh_fields(const ufbx_scene *v) {
	for (size_t mi = 0; mi < v->meshes.count; mi++) {
		ufbx_mesh *m = v->meshes.data[mi];
		MF(mi, "counts", ufbxt_hash_size_t(&fh, m->num_vertices); ufbxt_hash_size_t(&fh, m->num_indices);
			ufbxt_hash_size_t(&fh, m->num_faces); ufbxt_hash_size_t(&fh, m->num_triangles);
			ufbxt_hash_size_t(&fh, m->num_edges););
		MF(mi, "faces", ufbxt_hash_list(&fh, m->faces, ufbxt_hash_face_imp));
		MF(mi, "face_smoothing", ufbxt_hash_list(&fh, m->face_smoothing, ufbxt_hash_pod_imp));
		MF(mi, "face_material", ufbxt_hash_list(&fh, m->face_material, ufbxt_hash_pod_imp));
		MF(mi, "face_group", ufbxt_hash_list(&fh, m->face_group, ufbxt_hash_pod_imp));
		MF(mi, "face_hole", ufbxt_hash_list(&fh, m->face_hole, ufbxt_hash_pod_imp));
		MF(mi, "face_counts", ufbxt_hash_size_t(&fh, m->max_face_triangles); ufbxt_hash_size_t(&fh, m->num_empty_faces);
			ufbxt_hash_size_t(&fh, m->num_point_faces); ufbxt_hash_size_t(&fh, m->num_line_faces););
		MF(mi, "edges", ufbxt_hash_list(&fh, m->edges, ufbxt_hash_edge_imp));
		MF(mi, "edge_smoothing", ufbxt_hash_list(&fh, m->edge_smoothing, ufbxt_hash_pod_imp));
		MF(mi, "edge_crease", ufbxt_hash_list(&fh, m->edge_crease, ufbxt_hash_real_imp));
		MF(mi, "edge_visibility", ufbxt_hash_list(&fh, m->edge_visibility, ufbxt_hash_pod_imp));
		MF(mi, "vertex_indices", ufbxt_hash_list(&fh, m->vertex_indices, ufbxt_hash_pod_imp));
		MF(mi, "vertices", ufbxt_hash_list(&fh, m->vertices, ufbxt_hash_vec3_imp));
		MF(mi, "vertex_first_index", ufbxt_hash_list(&fh, m->vertex_first_index, ufbxt_hash_pod_imp));
		MF(mi, "vertex_position", ufbxt_hash_vertex_vec3(&fh, &m->vertex_position));
		MF(mi, "vertex_normal", ufbxt_hash_vertex_vec3(&fh, &m->vertex_normal));
		MF(mi, "vertex_uv", ufbxt_hash_vertex_vec2(&fh, &m->vertex_uv));
		MF(mi, "vertex_tangent", ufbxt_hash_vertex_vec3(&fh, &m->vertex_tangent));
		MF(mi, "vertex_bitangent", ufbxt_hash_vertex_vec3(&fh, &m->vertex_bitangent));
		MF(mi, "vertex_color", ufbxt_hash_vertex_vec4(&fh, &m->vertex_color));
		MF(mi, "vertex_crease", ufbxt_hash_vertex_real(&fh, &m->vertex_crease));
		MF(mi, "uv_sets", ufbxt_hash_list_ptr(&fh, m->uv_sets, ufbxt_hash_uv_set_imp));
		MF(mi, "color_sets", ufbxt_hash_list_ptr(&fh, m->color_sets, ufbxt_hash_color_set_imp));
		MF(mi, "materials", ufbxt_hash_list(&fh, m->materials, ufbxt_hash_element_ref_imp));
		MF(mi, "face_groups", ufbxt_hash_list_ptr(&fh, m->face_groups, ufbxt_hash_face_group_imp));
		MF(mi, "material_parts", ufbxt_hash_list_ptr(&fh, m->material_parts, ufbxt_hash_mesh_part_imp));
		MF(mi, "face_group_parts", ufbxt_hash_list_ptr(&fh, m->face_group_parts, ufbxt_hash_mesh_part_imp));
		MF(mi, "skinned", ufbxt_hash_pod(&fh, m->skinned_is_local); ufbxt_hash_vertex_vec3(&fh, &m->skinned_position);
			ufbxt_hash_vertex_vec3(&fh, &m->skinned_normal););
		MF(mi, "skin_deformers", ufbxt_hash_list(&fh, m->skin_deformers, ufbxt_hash_element_ref_imp));
		MF(mi, "blend_deformers", ufbxt_hash_list(&fh, m->blend_deformers, ufbxt_hash_element_ref_imp));
		MF(mi, "cache_deformers", ufbxt_hash_list(&fh, m->cache_deformers, ufbxt_hash_element_ref_imp));
		MF(mi, "all_deformers", ufbxt_hash_list(&fh, m->all_deformers, ufbxt_hash_element_ref_imp));
		MF(mi, "subdivision", ufbxt_hash_pod(&fh, m->subdivision_preview_levels); ufbxt_hash_pod(&fh, m->subdivision_render_levels);
			ufbxt_hash_pod(&fh, m->subdivision_display_mode); ufbxt_hash_pod(&fh, m->subdivision_boundary);
			ufbxt_hash_pod(&fh, m->subdivision_uv_boundary););
		MF(mi, "subdivision_result", ufbxt_hash_pod(&fh, m->subdivision_evaluated);
			if (m->subdivision_result) ufbxt_hash_subdivision_result(&fh, m->subdivision_result););
		MF(mi, "tail", ufbxt_hash_pod(&fh, m->from_tessellated_nurbs););
	}
}

// Raw metadata + values for the per-vertex attribute buffers that can diverge (env S3BC_VECRAW=1):
// `Q <mesh> <field> <exists> <vc> <ic> <vreal> <uniq> <wc>` then one `q` line per value (hex bit
// patterns) and one `n` line per index. Mirrors Dbg.VecRaw on the C# side.
static void vv3_raw(size_t mi, const char *name, const ufbx_vertex_vec3 *p) {
	printf("Q %zu %s %d %zu %zu %zu %d %zu\n", mi, name, (int) p->exists, p->values.count,
		p->indices.count, p->value_reals, (int) p->unique_per_vertex, p->values_w.count);
	for (size_t i = 0; i < p->values.count; i++) {
		ufbx_vec3 v = p->values.data[i];
		printf("q %zu %s %zu %016llx %016llx %016llx\n", mi, name, i,
			(unsigned long long) d2b((double) v.x), (unsigned long long) d2b((double) v.y),
			(unsigned long long) d2b((double) v.z));
	}
	for (size_t i = 0; i < p->indices.count; i++) {
		printf("n %zu %s %zu %08x\n", mi, name, i, (unsigned) p->indices.data[i]);
	}
}

static void vec_raw(const ufbx_scene *v) {
	for (size_t mi = 0; mi < v->meshes.count; mi++) {
		ufbx_mesh *m = v->meshes.data[mi];
		printf("F %zu skin=%zu blend=%zu cache=%zu all=%zu\n", mi, m->skin_deformers.count,
			m->blend_deformers.count, m->cache_deformers.count, m->all_deformers.count);
		vv3_raw(mi, "vertex_position", &m->vertex_position);
		vv3_raw(mi, "vertex_normal", &m->vertex_normal);
		vv3_raw(mi, "vertex_tangent", &m->vertex_tangent);
		vv3_raw(mi, "vertex_bitangent", &m->vertex_bitangent);
		printf("S %zu skinned_is_local %d\n", mi, (int) m->skinned_is_local);
		vv3_raw(mi, "skinned_position", &m->skinned_position);
		vv3_raw(mi, "skinned_normal", &m->skinned_normal);
	}
}

// Same as vv3_raw() for the 2D attribute (env S3BC_UVDUMP=1), mirroring Dbg.VV2.
static void vv2_raw(size_t mi, const char *name, const ufbx_vertex_vec2 *p) {
	printf("U %zu %s %d %zu %zu %zu %d\n", mi, name, (int) p->exists, p->values.count,
		p->indices.count, p->value_reals, (int) p->unique_per_vertex);
	for (size_t i = 0; i < p->values.count; i++) {
		ufbx_vec2 uv = p->values.data[i];
		printf("u %zu %s %zu %016llx %016llx\n", mi, name, i,
			(unsigned long long) d2b((double) uv.x), (unsigned long long) d2b((double) uv.y));
	}
	for (size_t i = 0; i < p->indices.count; i++) {
		printf("v %zu %s %zu %08x\n", mi, name, i, (unsigned) p->indices.data[i]);
	}
}

static void uv_dump(const ufbx_scene *v) {
	for (size_t mi = 0; mi < v->meshes.count; mi++) {
		ufbx_mesh *m = v->meshes.data[mi];
		printf("H %zu num_vertices=%zu num_indices=%zu uv_sets=%zu color_sets=%zu\n", mi,
			m->num_vertices, m->num_indices, m->uv_sets.count, m->color_sets.count);
		vv2_raw(mi, "mesh_uv", &m->vertex_uv);
		char tag[64];
		for (size_t s = 0; s < m->uv_sets.count; s++) {
			ufbx_uv_set *set = &m->uv_sets.data[s];
			printf("Z %zu %zu index=%u name=[%.*s]\n", mi, s, set->index,
				(int) set->name.length, set->name.data);
			snprintf(tag, sizeof(tag), "set%zu_uv", s); vv2_raw(mi, tag, &set->vertex_uv);
			snprintf(tag, sizeof(tag), "set%zu_tan", s); vv3_raw(mi, tag, &set->vertex_tangent);
			snprintf(tag, sizeof(tag), "set%zu_bit", s); vv3_raw(mi, tag, &set->vertex_bitangent);
		}
	}
}

// `W <type> <0|1>` for every has_warning slot plus `R <i> type= elem= count= desc=[..]`
// per warning record (env S3BC_WARNDBG=1). Mirrors Dbg.WarnDump on the C# side.
static void warn_dump(const ufbx_scene *v) {
	for (size_t i = 0; i < UFBX_WARNING_TYPE_COUNT; i++) {
		printf("W %zu %d\n", i, (int) v->metadata.has_warning[i]);
	}
	for (size_t i = 0; i < v->metadata.warnings.count; i++) {
		ufbx_warning *w = &v->metadata.warnings.data[i];
		printf("R %zu type=%d elem=%llu count=%llu desc=[%.*s]\n", i, (int) w->type,
			(unsigned long long) w->element_id, (unsigned long long) w->count,
			(int) w->description.length, w->description.data);
	}
}

// Sub-field hashes for the three containers whose list-level state can diverge without any
// per-item hash differing: metadata (`M`), anim (`A`) and the element base struct (`E`).
#define XF(prefix, id, name, code) do { ufbxt_hash fh; ufbxt_hash_init(&fh, NULL); code; \
	printf(prefix " %zu %s %016llx\n", (size_t)(id), name, (unsigned long long)fh.state); } while (0)

static void meta_fields(const ufbx_scene *v) {
	const ufbx_metadata *m = &v->metadata;
	XF("M", 0, "ascii_version", ufbxt_hash_pod(&fh, m->ascii); ufbxt_hash_pod(&fh, m->version));
	XF("M", 0, "creator", ufbxt_hash_string(&fh, m->creator));
	XF("M", 0, "flags", ufbxt_hash_pod(&fh, m->is_unsafe); ufbxt_hash_pod(&fh, m->big_endian));
	XF("M", 0, "exporter", ufbxt_hash_pod(&fh, m->exporter); ufbxt_hash_pod(&fh, m->exporter_version));
	XF("M", 0, "scene_props", ufbxt_hash_props(&fh, &m->scene_props));
	XF("M", 0, "orig_app", ufbxt_hash_application(&fh, &m->original_application));
	XF("M", 0, "latest_app", ufbxt_hash_application(&fh, &m->latest_application));
	XF("M", 0, "has_warning", ufbxt_hash_array(&fh, m->has_warning, ufbxt_hash_pod_imp));
}

static void anim_fields(const ufbx_scene *v) {
	const ufbx_anim *a = v->anim;
	if (!a) return;
	XF("A", 0, "time", ufbxt_hash_double(&fh, a->time_begin); ufbxt_hash_double(&fh, a->time_end));
	XF("A", 0, "layers", ufbxt_hash_list(&fh, a->layers, ufbxt_hash_element_ref_imp));
	XF("A", 0, "weights", ufbxt_hash_list(&fh, a->override_layer_weights, ufbxt_hash_pod_imp));
	XF("A", 0, "prop_overrides", ufbxt_hash_list_ptr(&fh, a->prop_overrides, ufbxt_hash_prop_override_imp));
	XF("A", 0, "transform_overrides", ufbxt_hash_list_ptr(&fh, a->transform_overrides, ufbxt_hash_transform_override_imp));
	XF("A", 0, "flags", ufbxt_hash_pod(&fh, a->ignore_connections); ufbxt_hash_pod(&fh, a->custom));
}

static void elem_fields_at(size_t i, const ufbx_element *e) {
	XF("E", i, "name", ufbxt_hash_string(&fh, e->name));
	XF("E", i, "props", ufbxt_hash_props(&fh, &e->props));
	XF("E", i, "ids", ufbxt_hash_pod(&fh, e->element_id); ufbxt_hash_pod(&fh, e->typed_id));
	XF("E", i, "instances", ufbxt_hash_list(&fh, e->instances, ufbxt_hash_element_ref_imp));
	XF("E", i, "type", ufbxt_hash_pod(&fh, e->type));
	XF("E", i, "conn_src", ufbxt_hash_list(&fh, e->connections_src, ufbxt_hash_connection_imp));
	XF("E", i, "conn_dst", ufbxt_hash_list(&fh, e->connections_dst, ufbxt_hash_connection_imp));
}

static void elem_fields_all(const ufbx_scene *v) {
	for (size_t i = 0; i < v->elements.count; i++) elem_fields_at(i, v->elements.data[i]);
}

static void trace(ufbx_scene *v) {
	if (getenv("S3BC_MESHDUMP") != NULL) mesh_fields(v);
	if (getenv("S3BC_VECRAW") != NULL) vec_raw(v);
	if (getenv("S3BC_PARTDUMP") != NULL) {
		for (size_t mi = 0; mi < v->meshes.count; mi++) {
			ufbx_mesh *m = v->meshes.data[mi];
			for (int kind = 0; kind < 2; kind++) {
				ufbx_mesh_part_list *pl = kind == 0 ? &m->material_parts : &m->face_group_parts;
				for (size_t pi = 0; pi < pl->count; pi++) {
					ufbx_mesh_part *p = &pl->data[pi];
					printf("R %zu %s %zu idx=%u nf=%zu nt=%zu ne=%zu np=%zu nl=%zu fic=%zu [",
						mi, kind == 0 ? "mp" : "fg", pi, (unsigned)p->index, p->num_faces, p->num_triangles,
						p->num_empty_faces, p->num_point_faces, p->num_line_faces, p->face_indices.count);
					for (size_t fi = 0; fi < p->face_indices.count; fi++) printf("%u,", (unsigned)p->face_indices.data[fi]);
					printf("]\n");
				}
			}
		}
	}
	if (getenv("S3BC_KEYDUMP") != NULL) {
		for (size_t ci = 0; ci < v->anim_curves.count; ci++) {
			ufbx_anim_curve *c = v->anim_curves.data[ci];
			printf("A %zu %zu\n", ci, c->keyframes.count);
			for (size_t ki = 0; ki < c->keyframes.count; ki++) {
				ufbx_keyframe *k = &c->keyframes.data[ki];
				printf("K %zu %zu %016llx %016llx %d %08x %08x %08x %08x\n",
					ci, ki,
					(unsigned long long)d2b(k->time),
					(unsigned long long)d2b(k->value),
					(int)k->interpolation,
					(unsigned int)f2b(k->left.dx), (unsigned int)f2b(k->left.dy),
					(unsigned int)f2b(k->right.dx), (unsigned int)f2b(k->right.dy));
			}
			printf("N %zu %016llx %016llx\n", ci,
				(unsigned long long)d2b(c->min_value), (unsigned long long)d2b(c->max_value));
		}
	}
	ufbxt_hash_init(&h, NULL);
	meta_fields(v);
	ufbxt_hash_metadata(&h, &v->metadata); show("metadata");
	ufbxt_hash_scene_settings(&h, &v->settings); show("settings");
	ufbxt_hash_element_ref(&h, v->root_node); show("root");
	anim_fields(v);
	ufbxt_hash_anim(&h, v->anim); show("anim");
	elem_fields_all(v);
	HL("elements", v->elements, ufbxt_hash_element_imp);
	HL("unknowns", v->unknowns, ufbxt_hash_unknown_imp);
	HL("nodes", v->nodes, ufbxt_hash_node_imp);
	HL("meshes", v->meshes, ufbxt_hash_mesh_imp);
	HL("lights", v->lights, ufbxt_hash_light_imp);
	HL("cameras", v->cameras, ufbxt_hash_camera_imp);
	HL("bones", v->bones, ufbxt_hash_bone_imp);
	HL("empties", v->empties, ufbxt_hash_empty_imp);
	HL("line_curves", v->line_curves, ufbxt_hash_line_curve_imp);
	HL("nurbs_curves", v->nurbs_curves, ufbxt_hash_nurbs_curve_imp);
	HL("nurbs_surfaces", v->nurbs_surfaces, ufbxt_hash_nurbs_surface_imp);
	HL("trim", v->nurbs_trim_surfaces, ufbxt_hash_nurbs_trim_surface_imp);
	HL("trimb", v->nurbs_trim_boundaries, ufbxt_hash_nurbs_trim_boundary_imp);
	HL("procedural", v->procedural_geometries, ufbxt_hash_procedural_geometry_imp);
	HL("stereo", v->stereo_cameras, ufbxt_hash_stereo_camera_imp);
	HL("switchers", v->camera_switchers, ufbxt_hash_camera_switcher_imp);
	HL("markers", v->markers, ufbxt_hash_marker_imp);
	HL("lod", v->lod_groups, ufbxt_hash_lod_group_imp);
	HL("skins", v->skin_deformers, ufbxt_hash_skin_deformer_imp);
	HL("skin_clusters", v->skin_clusters, ufbxt_hash_skin_cluster_imp);
	HL("blend_def", v->blend_deformers, ufbxt_hash_blend_deformer_imp);
	HL("blend_channels", v->blend_channels, ufbxt_hash_blend_channel_imp);
	HL("blend_shapes", v->blend_shapes, ufbxt_hash_blend_shape_imp);
	HL("cache_def", v->cache_deformers, ufbxt_hash_cache_deformer_imp);
	HL("cache_files", v->cache_files, ufbxt_hash_cache_file_imp);
	HL("materials", v->materials, ufbxt_hash_material_imp);
	HL("textures", v->textures, ufbxt_hash_texture_imp);
	HL("videos", v->videos, ufbxt_hash_video_imp);
	HL("shaders", v->shaders, ufbxt_hash_shader_imp);
	HL("shader_bindings", v->shader_bindings, ufbxt_hash_shader_binding_imp);
	HL("anim_stacks", v->anim_stacks, ufbxt_hash_anim_stack_imp);
	HL("anim_layers", v->anim_layers, ufbxt_hash_anim_layer_imp);
	HL("anim_values", v->anim_values, ufbxt_hash_anim_value_imp);
	HL("anim_curves", v->anim_curves, ufbxt_hash_anim_curve_imp);
	HL("display_layers", v->display_layers, ufbxt_hash_display_layer_imp);
	HL("selection_sets", v->selection_sets, ufbxt_hash_selection_set_imp);
	HL("selection_nodes", v->selection_nodes, ufbxt_hash_selection_node_imp);
	HL("characters", v->characters, ufbxt_hash_character_imp);
	HL("constraints", v->constraints, ufbxt_hash_constraint_imp);
	HL("audio_layers", v->audio_layers, ufbxt_hash_audio_layer_imp);
	HL("audio_clips", v->audio_clips, ufbxt_hash_audio_clip_imp);
	HL("poses", v->poses, ufbxt_hash_pose_imp);
	HL("metadata_objects", v->metadata_objects, ufbxt_hash_metadata_object_imp);
	HLP("texture_files", v->texture_files, ufbxt_hash_texture_file_imp);
	HL("connections_src", v->connections_src, ufbxt_hash_connection_imp);
	HL("connections_dst", v->connections_dst, ufbxt_hash_connection_imp);
	HLP("elements_by_name", v->elements_by_name, ufbxt_hash_name_element_imp);
	show("FINAL");
}

// Full field-level scene dump (env S3BC_DUMPDIR=<dir>): uses hash_scene's own tagged dumper,
// which prints every hashed field, so a C#/C difference can be read as values, not hashes.
static void dump_scene(const char *path, const ufbx_scene *v) {
	const char *dir = getenv("S3BC_DUMPDIR");
	if (!dir) return;
	const char *base = strrchr(path, '/');
	if (!base) base = strrchr(path, '\\');
	base = base ? base + 1 : path;
	char name[1024];
	snprintf(name, sizeof(name), "%s/%s.dump", dir, base);
	FILE *f = fopen(name, "wb");
	if (!f) return;
	ufbxt_hash_scene(v, f);
	fclose(f);
}

// Isolate the topology helpers (env S3BC_TOPODBG=1): recompute compute_topology +
// generate_normal_mapping for every mesh and print the resulting normal count and the first
// index entries, to tell a helper divergence apart from a wiring divergence.
static void topo_dbg(const ufbx_scene *v) {
	for (size_t mi = 0; mi < v->meshes.count; mi++) {
		ufbx_mesh *m = v->meshes.data[mi];
		size_t ni = m->num_indices;
		if (ni == 0) { printf("T %zu nn=0 []\n", mi); continue; }
		ufbx_topo_edge *topo = (ufbx_topo_edge*) calloc(ni, sizeof(ufbx_topo_edge));
		uint32_t *nix = (uint32_t*) calloc(ni, sizeof(uint32_t));
		if (!topo || !nix) { printf("T %zu OOM\n", mi); free(topo); free(nix); continue; }
		ufbx_compute_topology(m, topo, ni);
		size_t nn = ufbx_generate_normal_mapping(m, topo, ni, nix, ni, false);
		printf("T %zu nn=%zu [", mi, nn);
		for (size_t i = 0; i < ni && i < 24; i++) printf("%u,", (unsigned) nix[i]);
		printf("]\n");
		free(topo); free(nix);
	}
}

// Cache channel probe (env S3BC_TOPODBG=1): what `ufbxi_evaluate_skinning` sees per mesh --
// the external channel interpretation, the frame value counts and how many vec3 the public
// sampler actually returns for the requested count.
static void chan_dbg(const ufbx_scene *v) {
	for (size_t i = 0; i < v->cache_files.count; i++) {
		ufbx_cache_file *cf = v->cache_files.data[i];
		printf("X %zu filename=[%.*s] extcache=%d abs=[%.*s]\n", i,
			(int) cf->filename.length, cf->filename.data, cf->external_cache ? 1 : 0,
			(int) cf->absolute_filename.length, cf->absolute_filename.data);
	}
	for (size_t i = 0; i < v->cache_deformers.count; i++) {
		ufbx_cache_deformer *cd = v->cache_deformers.data[i];
		printf("Y %zu want=[%.*s] file=%d fileext=%d extch=%d\n", i,
			(int) cd->channel.length, cd->channel.data, cd->file ? 1 : 0,
			cd->file && cd->file->external_cache ? 1 : 0, cd->external_channel ? 1 : 0);
	}
	for (size_t mi = 0; mi < v->meshes.count; mi++) {
		ufbx_mesh *m = v->meshes.data[mi];
		for (size_t ci = 0; ci < m->cache_deformers.count; ci++) {
			ufbx_cache_deformer *cd = m->cache_deformers.data[ci];
			ufbx_cache_channel *ch = cd->external_channel;
			ufbx_geometry_cache *gc = cd->file ? cd->file->external_cache : NULL;
			printf("N %zu %zu want=%.*s extch=%d chans=%d", mi, ci,
				(int) cd->channel.length, cd->channel.data, ch ? 1 : 0,
				gc ? (int) gc->channels.count : -1);
			if (gc) {
				for (size_t k = 0; k < gc->channels.count; k++) {
					printf(" [%zu]=%.*s", k, (int) gc->channels.data[k].name.length, gc->channels.data[k].name.data);
				}
			}
			printf("\n");
			if (!ch) { printf("D %zu %zu NULL\n", mi, ci); continue; }
			printf("D %zu %zu interp=%d frames=%zu dc0=%u sec0=%u req=%zu",
				mi, ci, (int) ch->interpretation, ch->frames.count,
				ch->frames.count > 0 ? ch->frames.data[0].data_count : 0u,
				ch->frames.count > 0 ? ch->frames.data[0].data_element_bytes : 0u,
				m->skinned_normal.values.count);
			if (ch->frames.count > 0 && ch->frames.data[0].data_count >= 3) {
				size_t nvec = ch->frames.data[0].data_count / 3 + 8;
				ufbx_vec3 *tmp = (ufbx_vec3*) calloc(nvec, sizeof(ufbx_vec3));
				ufbx_geometry_cache_data_opts co = { 0 };
				size_t got = ufbx_sample_geometry_cache_vec3(ch, 0.0, tmp, nvec, &co);
				printf(" got=%zu [%016llx,%016llx,%016llx]", got,
					(unsigned long long) d2b((double) tmp[0].x), (unsigned long long) d2b((double) tmp[0].y),
					(unsigned long long) d2b((double) tmp[0].z));
				free(tmp);
			}
			printf("\n");
		}
	}
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
	for (int i = 1; i < argc; i++) {
		printf("# %s\n", argv[i]);
		ufbx_scene *v = ufbx_load_file(argv[i], &opts, &error);
		if (!v) { printf("# FAIL type=%d desc=%.*s\n", (int)error.type, (int)error.description.length, error.description.data); continue; }
		if (getenv("S3BC_TOPODBG") != NULL) topo_dbg(v);
		if (getenv("S3BC_TOPODBG") != NULL) chan_dbg(v);
		if (getenv("S3BC_UVDUMP") != NULL) uv_dump(v);
		if (getenv("S3BC_WARNDBG") != NULL) warn_dump(v);
		trace(v);
		dump_scene(argv[i], v);
		ufbx_free_scene(v);
	}
	return 0;
}

static void nsub(ufbx_scene *v) {
	for (size_t i = 0; i < v->nodes.count; i++) {
		ufbx_node *n = v->nodes.data[i];
		ufbxt_hash_element_ref(&h, n->parent);
		ufbxt_hash_list(&h, n->children, ufbxt_hash_element_ref_imp);
		ufbxt_hash_element_ref(&h, n->mesh);
		ufbxt_hash_element_ref(&h, n->attrib);
		ufbxt_hash_element_ref(&h, n->geometry_transform_helper);
		ufbxt_hash_element_ref(&h, n->scale_helper);
		ufbxt_hash_pod(&h, n->attrib_type);
		ufbxt_hash_list(&h, n->all_attribs, ufbxt_hash_element_ref_imp);
		ufbxt_hash_pod(&h, n->inherit_mode);
		ufbxt_hash_pod(&h, n->original_inherit_mode);
		ufbxt_hash_transform(&h, n->local_transform);
		ufbxt_hash_transform(&h, n->geometry_transform);
		ufbxt_hash_vec3(&h, n->inherit_scale);
		ufbxt_hash_matrix(&h, n->node_to_parent);
		ufbxt_hash_matrix(&h, n->node_to_world);
		ufbxt_hash_matrix(&h, n->geometry_to_node);
		ufbxt_hash_matrix(&h, n->geometry_to_world);
		ufbxt_hash_matrix(&h, n->unscaled_node_to_world);
		ufbxt_hash_quat(&h, n->adjust_pre_rotation);
		ufbxt_hash_real(&h, n->adjust_pre_scale);
		ufbxt_hash_quat(&h, n->adjust_post_rotation);
		ufbxt_hash_real(&h, n->adjust_post_scale);
		ufbxt_hash_real(&h, n->adjust_mirror_axis);
		ufbxt_hash_pod(&h, n->visible);
		ufbxt_hash_pod(&h, n->has_geometry_transform);
		ufbxt_hash_pod(&h, n->has_root_adjust_transform);
		ufbxt_hash_pod(&h, n->has_adjust_transform);
		ufbxt_hash_pod(&h, n->is_scale_compensate_parent);
		ufbxt_hash_list(&h, n->materials, ufbxt_hash_element_ref_imp);
		printf("n%zu %016llx\n", i, (unsigned long long)h.state);
	}
}
