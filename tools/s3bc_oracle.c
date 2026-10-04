// C reference oracle for the S3b (scene finalize mid-half) + S3c (finalize_scene / update
// chain) modules of the ufbx -> C# port. `#include "ufbx.c"` to reach the internals.
//
// Loads real FBX files with the golden opts of test/hash_scene.c load_scene() (ufbx.c full
// build incl. ufbxi_finalize_scene + ufbxi_update_scene + modify_geometry) and dumps
// differential records the C# harness (tools/S3bcCheck) replays through `UfbxApi.LoadFile`:
//
//   S  <fi> <ok> <elements> <nodes> <meshes> <materials> <textures> <texture_files>
//      <num_shader_textures>
//   M  <fi> <mat_ix> <n> (<mat_prop_fnv> <tex_elem_id> <file_index>)*    material->textures
//   P  <fi> <mat_ix> <m|p> <map_ix> <has> <vx:h> <vy:h> <vz:h> <vw:h> <vint> <tex_id>
//      <tex_enabled> <components>                                        fbx/pbr maps
//   E  <fi> <mat_ix> <feature_ix> <enabled> <explicit>                   material features
//   T  <fi> <ix> <index> <fn_fnv> <abs_fnv> <rel_fnv> <content_size> <content_fnv>
//                                                                        texture_files
//   N  <fi> <typed_id> <parent|-> <depth> <name_fnv>                     scene.nodes order
//   X  <fi> <typed_id> <m00..m23:h>                                      node_to_parent
//   W  <fi> <typed_id> <m00..m23:h>                                      node_to_world
//   LT <fi> <typed_id> <tx:h> <ty:h> <tz:h> <qx:h> <qy:h> <qz:h> <qw:h> <sx:h> <sy:h> <sz:h>
//                                                                        local_transform
//   V  <fi> <mesh_typed_id> <num_vertices> <vertices_fnv> <first8...>    vertex positions
//                                                                        (post modify_geometry)
//
// `<h>` = 16 hex digits of the IEEE-754 double bits; `<*_fnv>` = FNV-1a-64 over the raw
// UTF-8/raw bytes (basis cbf29ce484222325). The vertex fnv covers every vertex's 3 doubles as
// little-endian bits in order (so any evaluate-order or value divergence flips it), followed
// by the first 8 vertices as explicit bits.
//
// Build (mandatory flags, see PORTING_NOTES "浮点语义"):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
//       tools/s3bc_oracle.c -o tools/s3bc_oracle.exe
// Run (from C:/Workspace/_analyze_ufbx so the data/ paths resolve):
//   C:/Workspace/ufbx-cs/tools/s3bc_oracle.exe \
//     data/maya_slime_7500_binary.fbx ... > C:/Workspace/ufbx-cs/tools/s3bc_oracle.txt

#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static const uint64_t FNV_BASIS = UINT64_C(0xcbf29ce484222325);

static uint64_t fnv_bytes(const void *data, size_t size) {
	const uint8_t *p = (const uint8_t*)data;
	uint64_t h = FNV_BASIS;
	for (size_t i = 0; i < size; i++) {
		h ^= p[i];
		h *= UINT64_C(0x100000001b3);
	}
	return h;
}

static uint64_t fnv_str(ufbx_string str) {
	return fnv_bytes(str.data, str.length);
}

static uint64_t fnv_d(double d) {
	return fnv_bytes(&d, 8);
}

static void dbits(double v) {
	uint64_t u;
	memcpy(&u, &v, 8);
	printf("%016llx", (unsigned long long)u);
}

static void dump_matrix(const ufbx_matrix *m) {
	for (int i = 0; i < 12; i++) {
		dbits(m->v[i]);
		printf(i == 11 ? "" : " ");
	}
}

static void dump_mesh_vertices(int fi, ufbx_mesh *mesh) {
	uint64_t h = FNV_BASIS;
	size_t num = mesh->vertex_position.values.count;
	const ufbx_vec3 *v = mesh->vertex_position.values.data;
	for (size_t i = 0; i < num; i++) {
		uint64_t parts[3];
		memcpy(&parts[0], &v[i].x, 8);
		memcpy(&parts[1], &v[i].y, 8);
		memcpy(&parts[2], &v[i].z, 8);
		for (int k = 0; k < 3; k++) {
			h ^= (uint32_t)(parts[k] & 0xffu); h *= UINT64_C(0x100000001b3);
			h ^= (uint32_t)((parts[k] >> 8) & 0xffu); h *= UINT64_C(0x100000001b3);
			h ^= (uint32_t)((parts[k] >> 16) & 0xffu); h *= UINT64_C(0x100000001b3);
			h ^= (uint32_t)((parts[k] >> 24) & 0xffu); h *= UINT64_C(0x100000001b3);
			h ^= (uint32_t)((parts[k] >> 32) & 0xffu); h *= UINT64_C(0x100000001b3);
			h ^= (uint32_t)((parts[k] >> 40) & 0xffu); h *= UINT64_C(0x100000001b3);
			h ^= (uint32_t)((parts[k] >> 48) & 0xffu); h *= UINT64_C(0x100000001b3);
			h ^= (uint32_t)((parts[k] >> 56) & 0xffu); h *= UINT64_C(0x100000001b3);
		}
	}

	printf("V %d %u %zu %016llx", fi, mesh->element.typed_id, num,
		(unsigned long long)h);
	size_t first = num < 8 ? num : 8;
	for (size_t i = 0; i < first; i++) {
		printf(" ");
		dbits(v[i].x); printf(" ");
		dbits(v[i].y); printf(" ");
		dbits(v[i].z);
	}
	printf("\n");
}

static void dump_scene(int fi, ufbx_scene *scene) {
	printf("S %d 1 %zu %zu %zu %zu %zu %zu %u\n", fi,
		scene->elements.count,
		scene->nodes.count,
		scene->meshes.count,
		scene->materials.count,
		scene->textures.count,
		scene->texture_files.count,
		(unsigned)scene->metadata.num_shader_textures);

	// Materials: textures (sorted by material_prop) + every fbx/pbr map + features
	for (size_t mi = 0; mi < scene->materials.count; mi++) {
		ufbx_material *mat = scene->materials.data[mi];

		printf("M %d %zu %zu", fi, mi, mat->textures.count);
		for (size_t i = 0; i < mat->textures.count; i++) {
			ufbx_material_texture *tex = &mat->textures.data[i];
			uint32_t tex_id = tex->texture ? tex->texture->element.element_id : UINT32_MAX;
			printf(" %016llx %u %u", (unsigned long long)fnv_str(tex->material_prop),
				tex_id, tex->texture ? tex->texture->file_index : UINT32_MAX);
		}
		printf("\n");

		for (size_t i = 0; i < UFBX_MATERIAL_FBX_MAP_COUNT; i++) {
			ufbx_material_map *map = &mat->fbx.maps[i];
			printf("P %d %zu m %zu %d ", fi, mi, i, map->has_value ? 1 : 0);
			dbits(map->value_vec4.x); printf(" ");
			dbits(map->value_vec4.y); printf(" ");
			dbits(map->value_vec4.z); printf(" ");
			dbits(map->value_vec4.w); printf(" %lld %u %d %d\n",
				(long long)map->value_int,
				map->texture ? map->texture->element.element_id : UINT32_MAX,
				map->texture_enabled ? 1 : 0, (int)map->value_components);
		}
		for (size_t i = 0; i < UFBX_MATERIAL_PBR_MAP_COUNT; i++) {
			ufbx_material_map *map = &mat->pbr.maps[i];
			printf("P %d %zu p %zu %d ", fi, mi, i, map->has_value ? 1 : 0);
			dbits(map->value_vec4.x); printf(" ");
			dbits(map->value_vec4.y); printf(" ");
			dbits(map->value_vec4.z); printf(" ");
			dbits(map->value_vec4.w); printf(" %lld %u %d %d\n",
				(long long)map->value_int,
				map->texture ? map->texture->element.element_id : UINT32_MAX,
				map->texture_enabled ? 1 : 0, (int)map->value_components);
		}
		for (size_t i = 0; i < UFBX_MATERIAL_FEATURE_COUNT; i++) {
			printf("E %d %zu %zu %d %d\n", fi, mi, i,
				mat->features.features[i].enabled ? 1 : 0,
				mat->features.features[i].is_explicit ? 1 : 0);
		}
	}

	// Texture files (insertion order == map item order, see ufbxi_pop_texture_files)
	for (size_t i = 0; i < scene->texture_files.count; i++) {
		ufbx_texture_file *file = &scene->texture_files.data[i];
		printf("T %d %zu %u %016llx %016llx %016llx %zu %016llx\n", fi, i, file->index,
			(unsigned long long)fnv_str(file->filename),
			(unsigned long long)fnv_str(file->absolute_filename),
			(unsigned long long)fnv_str(file->relative_filename),
			file->content.size,
			(unsigned long long)(file->content.size ? fnv_bytes(file->content.data, file->content.size) : FNV_BASIS));
	}

	// Nodes: order, hierarchy, transforms (post ufbxi_update_scene / adjust transforms)
	for (size_t i = 0; i < scene->nodes.count; i++) {
		ufbx_node *node = scene->nodes.data[i];
		if (node->parent) {
			printf("N %d %u %u %u %016llx\n", fi, node->typed_id, node->parent->typed_id,
				node->node_depth, (unsigned long long)fnv_str(node->name));
		} else {
			printf("N %d %u - %u %016llx\n", fi, node->typed_id,
				node->node_depth, (unsigned long long)fnv_str(node->name));
		}

		printf("X %d %u ", fi, node->typed_id);
		dump_matrix(&node->node_to_parent);
		printf("\n");

		printf("W %d %u ", fi, node->typed_id);
		dump_matrix(&node->node_to_world);
		printf("\n");

		printf("LT %d %u ", fi, node->typed_id);
		dbits(node->local_transform.translation.x); printf(" ");
		dbits(node->local_transform.translation.y); printf(" ");
		dbits(node->local_transform.translation.z); printf(" ");
		dbits(node->local_transform.rotation.x); printf(" ");
		dbits(node->local_transform.rotation.y); printf(" ");
		dbits(node->local_transform.rotation.z); printf(" ");
		dbits(node->local_transform.rotation.w); printf(" ");
		dbits(node->local_transform.scale.x); printf(" ");
		dbits(node->local_transform.scale.y); printf(" ");
		dbits(node->local_transform.scale.z);
		printf("\n");
	}

	// Mesh vertex positions (post ufbxi_modify_geometry)
	for (size_t i = 0; i < scene->meshes.count; i++) {
		dump_mesh_vertices(fi, scene->meshes.data[i]);
	}
}

int main(int argc, char **argv) {
	if (argc < 2) {
		fprintf(stderr, "usage: s3bc_oracle.exe <file.fbx>...\n");
		return 2;
	}

	for (int fi = 0; fi < argc - 1; fi++) {
		const char *filename = argv[fi + 1];

		// Golden opts of test/hash_scene.c load_scene() (frame 0: plain load).
		ufbx_load_opts opts = { 0 };
		opts.load_external_files = true;
		opts.ignore_missing_external_files = true;
		opts.evaluate_caches = true;
		opts.evaluate_skinning = true;
		opts.target_axes = ufbx_axes_right_handed_y_up;
		opts.target_unit_meters = 1.0f;

		ufbx_error error;
		ufbx_scene *scene = ufbx_load_file(filename, &opts, &error);
		if (!scene) {
			printf("S %d 0 %d\n", fi, (int)error.type);
			continue;
		}

		dump_scene(fi, scene);
		ufbx_free_scene(scene);
	}
	return 0;
}
