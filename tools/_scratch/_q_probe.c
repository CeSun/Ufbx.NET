// 批 Q 调查探针：bake 侧两个待定缺口的实际覆盖情况。
//
//   1. 180° 翻转的连续四元数 —— `ufbx_quat_fix_antipodal()`（ufbx.c:31527-31532）在
//      `ufbxi_bake_...` 里的调用点（ufbx.c:27140-27143）。判据：bake 出来的旋转关键帧
//      `key.value` 与同一时刻直接求值得到的四元数 `q` 若 `ufbx_quat_dot(key, q) < 0`，
//      说明这一帧被翻转过 —— 也就是说这一支真的被执行了。
//   2. "动画区间严格内嵌于 bake 区间" —— 统计每条 anim curve 的 keyframe 时间跨度是否
//      严格落在 [anim->time_begin, anim->time_end] 之内（严格 = 至少一端留有余量），
//      这正是 bake 需要在区间两端做外插的情形。
//
// 只用公开 API，链接 ufbx.c 作为独立 TU（不 `#include "ufbx.c"`）。
//
// 编译：
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx _q_probe.c C:/Workspace/_analyze_ufbx/ufbx.c \
//       C:/Workspace/_analyze_ufbx/extra/ufbx_math.c -o _q_probe.exe
// 运行（CWD = C:/Workspace/_analyze_ufbx）：_q_probe.exe --list <corpus.txt>
//
// 输出：<nodes> <bakedRotKeys> <flips> <flipNodes> <curves> <curvesStrictInside> \
//       <maxLeadIn> <maxTrailOut> <anim> path
//   flips             = 被 fix_antipodal 翻转过的旋转关键帧数（0 = 这一支没被执行）
//   curvesStrictInside= keyframe 跨度严格内嵌于 [time_begin, time_end] 的 curve 数
//   maxLeadIn         = 全文件最大的 (curve 首键 - time_begin)
//   maxTrailOut       = 全文件最大的 (time_end - curve 末键)

#include "ufbx.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

static void run_one(const char *path)
{
	ufbx_load_opts opts = { 0 };
	ufbx_error error;
	ufbx_scene *scene = ufbx_load_file(path, &opts, &error);
	if (!scene) { printf("load-error %s\n", path); return; }

	size_t rot_keys = 0, flips = 0, flip_nodes = 0;
	size_t curves = 0, inside = 0;
	double max_lead = 0.0, max_trail = 0.0;

	if (scene->anim) {
		double begin = scene->anim->time_begin, end = scene->anim->time_end;

		// ---- 1. antipodal flips ----
		ufbx_bake_opts bopts = { 0 };
		ufbx_error berr;
		ufbx_baked_anim *bake = ufbx_bake_anim(scene, scene->anim, &bopts, &berr);
		if (bake) {
			for (size_t ni = 0; ni < bake->nodes.count; ni++) {
				const ufbx_baked_node *bn = &bake->nodes.data[ni];
				if (ni >= scene->nodes.count) break;
				ufbx_node *node = scene->nodes.data[ni];
				bool node_flipped = false;
				for (size_t ki = 0; ki < bn->rotation_keys.count; ki++) {
					rot_keys++;
					double t = bn->rotation_keys.data[ki].time;
					ufbx_transform tr = ufbx_evaluate_transform(scene->anim, node, t);
					ufbx_real d =
						bn->rotation_keys.data[ki].value.x * tr.rotation.x +
						bn->rotation_keys.data[ki].value.y * tr.rotation.y +
						bn->rotation_keys.data[ki].value.z * tr.rotation.z +
						bn->rotation_keys.data[ki].value.w * tr.rotation.w;
					if (d < 0.0) { flips++; node_flipped = true; }
				}
				if (node_flipped) flip_nodes++;
			}
			ufbx_free_baked_anim(bake);
		}

		// ---- 2. curves strictly inside the anim range ----
		for (size_t li = 0; li < scene->anim->layers.count; li++) {
			const ufbx_anim_layer *layer = scene->anim->layers.data[li];
			if (!layer) continue;
			for (size_t pi = 0; pi < layer->anim_props.count; pi++) {
				const ufbx_anim_prop *ap = &layer->anim_props.data[pi];
				if (!ap->anim_value) continue;
				for (size_t ci = 0; ci < 3; ci++) {
					const ufbx_anim_curve *curve = ap->anim_value->curves[ci];
					if (!curve || curve->keyframes.count == 0) continue;
					curves++;
					double kmin = curve->keyframes.data[0].time;
					double kmax = curve->keyframes.data[0].time;
					for (size_t ki = 1; ki < curve->keyframes.count; ki++) {
						double t = curve->keyframes.data[ki].time;
						if (t < kmin) kmin = t;
						if (t > kmax) kmax = t;
					}
					double lead = kmin - begin, trail = end - kmax;
					// Strictly inside: at least one end has a real gap (not just FP noise).
					if (lead > 1e-9 || trail > 1e-9) inside++;
					if (lead > max_lead) max_lead = lead;
					if (trail > max_trail) max_trail = trail;
				}
			}
		}
	}

	printf("%zu %zu %zu %zu %zu %zu %.17g %.17g %d %s\n",
		scene->nodes.count, rot_keys, flips, flip_nodes, curves, inside,
		max_lead, max_trail, scene->anim != NULL ? 1 : 0, path);

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
	printf("# nodes rotKeys flips flipNodes curves inside maxLeadIn maxTrailOut hasAnim path\n");
	char path[1024];
	while (fgets(path, sizeof(path), f)) {
		size_t len = strlen(path);
		while (len > 0 && (path[len - 1] == '\n' || path[len - 1] == '\r')) path[--len] = 0;
		if (len == 0 || path[0] == '#') continue;
		run_one(path);
	}
	fclose(f);
	return 0;
}
