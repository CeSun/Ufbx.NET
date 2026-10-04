// 定量确认「哪些 bake 选项组合会让 ufbx_quat_fix_antipodal() 真的翻转某个旋转关键帧」。
//
// 做法（唯一可信的办法）：把 ufbx.c 复制到私有副本 `ufbx_nofix.c` 并把它里面的
// `if (ufbx_quat_dot(q, reference) < 0.0f)` 改成 `if (false)`，然后**用同一份探针分别链接
// 原始 ufbx.c 与 ufbx_nofix.c**，逐行打印每个 baked 旋转关键帧的 IEEE 位模式，diff 两边输出。
// 不相等的行数 = 被翻转的关键帧数。
//
// ⚠️ 上一版（`_q_probe.c`）用「baked key 与同刻 ufbx_evaluate_transform() 的四元数点积 < 0」
// 作判据，得到 668 条，**那是错的**（实测 diff 为 0）。判据必须能真正区分前后状态。
//
// 编译（两份）：
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx _flip_probe.c C:/Workspace/_analyze_ufbx/ufbx.c \
//       C:/Workspace/_analyze_ufbx/extra/ufbx_math.c -o _flip_probe.exe
//   zig cc ... _flip_probe.c ufbx_nofix.c .../extra/ufbx_math.c -o _flip_probe_nofix.exe
// 运行（CWD = C:/Workspace/_analyze_ufbx）：<exe> --list <corpus.txt>
//
// 输出：R <fileIndex> <optIndex> <nodeIndex> <keyIndex> <timeBits> <x> <y> <z> <w>

#include "ufbx.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void pbits(double d)
{
	unsigned long long b;
	memcpy(&b, &d, 8);
	printf(" %016llx", b);
}

static void set_opts(ufbx_bake_opts *o, int ix)
{
	memset(o, 0, sizeof(*o));
	switch (ix) {
	case 0: break;                                              // default
	case 1: o->resample_rate = 2.0; break;
	case 2: o->resample_rate = 1.0; break;
	case 3: o->no_resample_rotation = true; break;
	case 4: o->max_keyframe_segments = 1; break;
	case 5: o->key_reduction_enabled = true; break;
	case 6: o->key_reduction_enabled = true; o->key_reduction_rotation = true;
	        o->key_reduction_threshold = 0.1; o->key_reduction_passes = 8; break;
	case 7: o->resample_rate = 1.0; o->no_resample_rotation = true; break;
	case 8: o->step_handling = UFBX_BAKE_STEP_HANDLING_IDENTICAL_TIME; break;
	case 9: o->minimum_sample_rate = 1000.0; break;              // effectively disables resampling
	case 10: o->resample_rate = 0.5; o->minimum_sample_rate = 1000.0; break;
	case 11: o->trim_start_time = true; o->resample_rate = 1.0; break;
	default: break;
	}
}

int main(int argc, char **argv)
{
	if (argc < 3 || strcmp(argv[1], "--list") != 0) {
		fprintf(stderr, "usage: %s --list <corpus.txt>\n", argv[0]);
		return 2;
	}
	FILE *f = fopen(argv[2], "rb");
	if (!f) { fprintf(stderr, "cannot open %s\n", argv[2]); return 2; }

	char path[1024];
	size_t fi = 0;
	while (fgets(path, sizeof(path), f)) {
		size_t len = strlen(path);
		while (len > 0 && (path[len - 1] == '\n' || path[len - 1] == '\r')) path[--len] = 0;
		if (len == 0 || path[0] == '#') continue;
		ufbx_load_opts opts = { 0 };
		ufbx_error error;
		ufbx_scene *scene = ufbx_load_file(path, &opts, &error);
		if (!scene) { fi++; continue; }
		if (scene->anim) {
			for (int oi = 0; oi < 12; oi++) {
				ufbx_bake_opts bopts;
				set_opts(&bopts, oi);
				ufbx_error berr;
				ufbx_baked_anim *bake = ufbx_bake_anim(scene, scene->anim, &bopts, &berr);
				if (!bake) continue;
				for (size_t ni = 0; ni < bake->nodes.count; ni++) {
					const ufbx_baked_node *bn = &bake->nodes.data[ni];
					for (size_t ki = 0; ki < bn->rotation_keys.count; ki++) {
						printf("R %zu %d %zu %zu", fi, oi, ni, ki);
						pbits(bn->rotation_keys.data[ki].time);
						pbits(bn->rotation_keys.data[ki].value.x);
						pbits(bn->rotation_keys.data[ki].value.y);
						pbits(bn->rotation_keys.data[ki].value.z);
						pbits(bn->rotation_keys.data[ki].value.w);
						printf("\n");
					}
				}
				ufbx_free_baked_anim(bake);
			}
		}
		ufbx_free_scene(scene);
		fi++;
	}
	fclose(f);
	return 0;
}
