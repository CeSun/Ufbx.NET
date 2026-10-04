// 批 P 调查探针：scale helper 的语料分布。
//
// 目的（HANDOFF_batch_P.md §3.1）：在动工前测出三个数字，判断"animated scale helper"
// 到底是不是真缺口，还是早被 2179 个 golden 覆盖。
//
//   1. 有多少文件创建了 scale helper（`node->is_scale_helper`）
//   2. 其中 helper 的 scale 是动画的有多少（bake 侧 `!scale_helper_t->constant_scale`
//      分支的唯一入口，ufbx.c:27285 / 端口 Bake.cs:770）
//   3. node_depth / inherit_mode 的分布（有没有递归 helper，ufbx.c:18500-18540）
//
// 只用公开 API（`ufbx.h`），不 `#include "ufbx.c"` —— 这样探针本身不受内部布局影响。
//
// 编译：
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx _sh_probe.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o _sh_probe.exe
// 运行（CWD 必须是 C:/Workspace/_analyze_ufbx，路径相对 data/）：
//   _sh_probe.exe --list <corpus.txt>
//
// 输出：每文件一行
//   <nodes> <helpers> <helpersAnim> <nodesWithHelper> <recursiveHelper> <maxHelperDepth> \
//   <imNormal> <imCompw> <imIgnore> <anim> <bakeResampled> <bakeHelperNotConst> path

#include "ufbx.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if !defined(UFBX_EXTERNAL_MATH)
#define UFBX_EXTERNAL_MATH
#endif

static const char *k_inherit_names[] = { "PRESERVE", "HELPER_NODES", "COMPENSATE", "COMPENSATE_NO_FALLBACK", "IGNORE" };

static int has_animated_scale(ufbx_scene *scene, ufbx_node *node)
{
	if (!scene->anim) return 0;
	for (size_t li = 0; li < scene->anim->layers.count; li++) {
		const ufbx_anim_layer *layer = scene->anim->layers.data[li];
		if (layer == NULL) continue;
		ufbx_anim_prop *ap = ufbx_find_anim_prop(layer, &node->element, "Lcl Scaling");
		if (ap != NULL) return 1;
	}
	return 0;
}

static void run_one(const char *path, int ih)
{
	ufbx_load_opts opts = { 0 };
	opts.inherit_mode_handling = (ufbx_inherit_mode_handling)ih;

	ufbx_error error;
	ufbx_scene *scene = ufbx_load_file(path, &opts, &error);
	if (!scene) {
		printf("load-error %s\n", path);
		return;
	}

	size_t helpers = 0, helpers_anim = 0, nodes_with_helper = 0;
	size_t recursive = 0, max_depth = 0;
	size_t im_normal = 0, im_compw = 0, im_ignore = 0;

	for (size_t i = 0; i < scene->nodes.count; i++) {
		ufbx_node *node = scene->nodes.data[i];
		if (node->scale_helper) nodes_with_helper++;
		if (node->is_scale_helper) {
			helpers++;
			if (has_animated_scale(scene, node)) helpers_anim++;
			if (node->parent && node->parent->is_scale_helper) recursive++;
			if ((size_t)node->node_depth > max_depth) max_depth = (size_t)node->node_depth;
		}
		if (node->parent && node->parent->scale_helper) {
			switch (node->inherit_mode) {
			case UFBX_INHERIT_MODE_NORMAL: im_normal++; break;
			case UFBX_INHERIT_MODE_COMPONENTWISE_SCALE: im_compw++; break;
			case UFBX_INHERIT_MODE_IGNORE_PARENT_SCALE: im_ignore++; break;
			default: break;
			}
		}
	}

	// bake 侧：helper 的 scale 是否非常量（=> resample_translation，ufbx.c:27285）
	size_t bake_resampled = 0, bake_helper_not_const = 0;
	if (scene->anim) {
		ufbx_bake_opts bopts = { 0 };
		ufbx_error berr;
		ufbx_baked_anim *bake = ufbx_bake_anim(scene, scene->anim, &bopts, &berr);
		if (bake) {
			for (size_t i = 0; i < scene->nodes.count; i++) {
				ufbx_node *node = scene->nodes.data[i];
				if (node->is_scale_helper || !node->parent || !node->parent->scale_helper) continue;
				uint32_t tid = node->parent->scale_helper->typed_id;
				if (tid >= bake->nodes.count) continue;
				if (!bake->nodes.data[tid].constant_scale) {
					bake_helper_not_const++;
					bake_resampled++;
				}
			}
			ufbx_free_baked_anim(bake);
		}
	}

	printf("%zu %zu %zu %zu %zu %zu %zu %zu %zu %d %zu %zu %s\n",
		scene->nodes.count, helpers, helpers_anim, nodes_with_helper, recursive, max_depth,
		im_normal, im_compw, im_ignore, scene->anim != NULL ? 1 : 0,
		bake_resampled, bake_helper_not_const, path);

	ufbx_free_scene(scene);
}

int main(int argc, char **argv)
{
	if (argc < 3 || strcmp(argv[1], "--list") != 0) {
		fprintf(stderr, "usage: %s --list <corpus.txt>\n", argv[0]);
		return 2;
	}
	FILE *f = fopen(argv[2], "rb");
	if (!f) { fprintf(stderr, "cannot open %s\n", argv[2]); return 2; }

	printf("# nodes helpers helpersAnim nodesWithHelper recursive maxDepth imNormal imCompw imIgnore hasAnim bakeResampled bakeHelperNotConst path\n");

	for (int ih = 0; ih < 5; ih++) {
		printf("# --- inherit_mode_handling = %s ---\n", k_inherit_names[ih]);
		fseek(f, 0, SEEK_SET);
		char path[1024];
		while (fgets(path, sizeof(path), f)) {
			size_t len = strlen(path);
			while (len > 0 && (path[len - 1] == '\n' || path[len - 1] == '\r')) path[--len] = 0;
			if (len == 0 || path[0] == '#') continue;
			run_one(path, ih);
		}
	}
	fclose(f);
	return 0;
}
