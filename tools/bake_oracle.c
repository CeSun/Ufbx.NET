// C reference oracle for batch J (the animation-baking chain) of the ufbx -> C# port.
// `#include "ufbx.c"` so the whole internal chain runs: ufbxi_bake_anim_imp/_bake_anim/
// _bake_node/_bake_element/_finalize_bake_times/_bake_postprocess_{vec3,quat}/_push_resampled_times
// (ufbx.c:26680-27773) plus the public entry points ufbx_bake_anim() and
// ufbx_evaluate_baked_{vec3,quat}() / ufbx_find_baked_{node,element}() (31250-31411).
//
// Loads real animated FBX files with the golden opts of test/hash_scene.c load_scene() and bakes
// each of them under NUM_VARIANTS (30) `ufbx_bake_opts` variants that cover every option-driven
// branch of the chain: NULL vs zeroed opts, the five effective defaults, resample/minimum/maximum
// sample rates, all five `ufbx_bake_step_handling` modes (incl. custom duration/epsilon pairs),
// trim_start_time, skip_node_transforms, bake_transform_props, no_resample_rotation,
// ignore_layer_weight_animation, max_keyframe_segments, key reduction (enabled/rotation/
// threshold/passes incl. the negative-threshold disable) and evaluate_flags=NO_EXTRAPOLATION.
// The C# harness (tools/BakeCheck) replays every record through src/Ufbx.NET/Parse/Bake.cs and
// src/Ufbx.NET/Api/UfbxBakeApi.cs and compares value by value.
//
// Output grammar (space separated, one record per line; `<h>` = lowercase 16 hex digits of the
// IEEE-754 bits of a double, `<z>` = 16 hex digits of an FNV-1a-64, `<i>`/`<u>` = decimal):
//   S <fi> <mode> <ok> <nodes> <elements> <meshes> [<anim>|<errtype>]   load summary
//                                                <mode> is the `ufbx_inherit_mode_handling` the
//                                                scene was loaded with (0 PRESERVE ... 4 IGNORE);
//                                                the harness loads the same file with the same mode
//   B <fi> <vi> <animArg> <ret> <errtype> <numLayers> <numLayerWeights> <numPropOverrides>
//     <numTransformOverrides> <animTb:h> <animTe:h>
//     <numNodes> <numElems> <ptb:h> <pte:h> <pd:h> <ktmin:h> <ktmax:h>
//                                                ufbx_bake_anim() result + all scalar fields.
//                                                <animArg> 0 = NULL anim (the `!anim` branch takes
//                                                scene->anim), 1 = scene->anim passed explicitly;
//                                                the four counts identify the resolved anim.
//   O <fi> <vi> <nullOpts> [<17 opts fields after the call>]    the PORTING_NOTES.md #9 control:
//                                                C value-copies the caller's opts (31260-31262) and
//                                                then writes the five defaults into the *copy*
//                                                (27717-27721), so the caller's struct must be
//                                                byte-identical to what this oracle set.
//   T <fi> <vi> <ti> <time:h>                                    the 16-entry sample time table
//                                                derived from the bake summary (see build_times());
//                                                Q/R records hash evaluations over this table plus
//                                                each key list's own key times.
//   N <fi> <vi> <ni> <typedId> <elemId> <ct> <cr> <cs> <nt> <nr> <ns> <zT> <zR> <zS> [<k0 ...>]
//                                                one baked node: identity, constant-channel flags,
//                                                key counts, FNV over each whole key list, and the
//                                                first key of every non-empty list as explicit bits
//   E <fi> <vi> <ei> <elemId> <numProps>                         one baked element
//   P <fi> <vi> <ei> <pi> <zName> <const> <numKeys> <zKeys> [<k0 ...>]   one baked prop
//   Q <fi> <vi> <ni> <z> <i>                     ufbx_evaluate_baked_vec3/_quat over the node's
//                                                three channels (non-empty only) x the time table
//                                                plus the channel's own key times and their
//                                                nextafter() neighbours (this is what pins the
//                                                `time == prev->time`, `prev[-1].time == time` and
//                                                STEP_LEFT/STEP_RIGHT branches)
//   R <fi> <vi> <ei> <pi> <z> <i>                the same sweep for ufbx_evaluate_baked_vec3 over
//                                                one baked prop's keys
//   F <fi> <vi> <z> <i>                          ufbx_find_baked_node()/[_by_typed_id]/
//                                                ufbx_find_baked_element()/[_by_element_id] swept
//                                                over every scene node and element (hit + miss)
//
// Empty key lists are deliberately NOT evaluated: C reads `keyframes.data[count - 1]`, i.e.
// `data[-1]` on NULL data, which is undefined behaviour and has no C# counterpart (the port
// throws); the counts are still compared through `N`/`P`.
//
// Build (mandatory flags, see PORTING_NOTES "浮点语义" -- UFBX_EXTERNAL_MATH is #defined below so a
// rebuild cannot forget it, and extra/ufbx_math.c provides the same libm transcriptions the port
// uses for ufbx_quat_slerp()/ufbx_nextafter()):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx \
//       tools/bake_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o tools/bake_oracle.exe
// Run (from C:/Workspace/_analyze_ufbx so the data/ paths resolve; each file is preceded by its
// inherit_mode_handling mode, which is the column tools/bake_corpus.txt grows in step 2):
//   C:/Workspace/ufbx-cs/tools/bake_oracle.exe \
//     0 data/maya_anim_extrapolation_7700_binary.fbx 0 data/... > C:/Workspace/ufbx-cs/tools/bake_oracle.txt

#ifndef UFBX_EXTERNAL_MATH
#define UFBX_EXTERNAL_MATH
#endif
#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static const uint64_t FNV_BASIS = UINT64_C(0xcbf29ce484222325);
static const uint64_t FNV_PRIME = UINT64_C(0x100000001b3);

static uint64_t g_fnv;

static void h_reset(void) { g_fnv = FNV_BASIS; }

static void h_byte(uint8_t b) {
	g_fnv ^= b;
	g_fnv *= FNV_PRIME;
}

static void h_bytes(const void *data, size_t size) {
	const uint8_t *p = (const uint8_t*)data;
	for (size_t i = 0; i < size; i++) h_byte(p[i]);
}

static void h_u64(uint64_t u) {
	for (int k = 0; k < 8; k++) h_byte((uint8_t)(u >> (k * 8)));
}

static void h_u32(uint32_t u) {
	for (int k = 0; k < 4; k++) h_byte((uint8_t)(u >> (k * 8)));
}

static void h_d(double d) {
	uint64_t u;
	memcpy(&u, &d, 8);
	h_u64(u);
}

static void h_sz(size_t v) { h_u64((uint64_t)v); }

static uint64_t fnv_bytes(const void *data, size_t size) {
	const uint8_t *p = (const uint8_t*)data;
	uint64_t h = FNV_BASIS;
	for (size_t i = 0; i < size; i++) {
		h ^= p[i];
		h *= FNV_PRIME;
	}
	return h;
}

static uint64_t fnv_str(ufbx_string str) {
	return fnv_bytes(str.data, str.length);
}

static void dbits(double v) {
	uint64_t u;
	memcpy(&u, &v, 8);
	printf("%016llx", (unsigned long long)u);
}

static void zbits(uint64_t u) {
	printf("%016llx", (unsigned long long)u);
}

// ---------------------------------------------------------------------------
// Variants
// ---------------------------------------------------------------------------

// The table the harness mirrors 1:1 in tools/BakeCheck/Program.cs.
// `explicit_anim` picks whether the `anim` argument is NULL (C resolves `scene->anim`) or
// `scene->anim` itself; every other field is written into a zero-initialised `ufbx_bake_opts`.
typedef struct {
	ufbx_bake_opts opts;
	bool null_opts;
	bool explicit_anim;
} bake_variant;

#define NUM_VARIANTS 30

static bake_variant make_variant(int vi)
{
	bake_variant v;
	memset(&v, 0, sizeof(v));
	v.null_opts = (vi == 0);
	v.explicit_anim = (vi % 2) == 1;

	ufbx_bake_opts *o = &v.opts;
	switch (vi) {
	case 0:  /* NULL opts, NULL anim: the zeroed-struct + defaults path */
		break;
	case 1:  /* zeroed opts (identical values, non-NULL pointer path), explicit anim */
		break;
	case 2:  o->resample_rate = 5.0; break;
	case 3:  o->resample_rate = 60.0; break;
	case 4:  o->resample_rate = 120.0; o->minimum_sample_rate = 100.0; break;
	case 5:  o->minimum_sample_rate = 1.0; break;
	case 6:  o->maximum_sample_rate = 2.0; break;
	case 7:  o->maximum_sample_rate = 24.0; o->resample_rate = 100.0; break;
	case 8:  o->trim_start_time = true; break;
	case 9:  o->skip_node_transforms = true; break;
	case 10: o->bake_transform_props = true; break;
	case 11: o->no_resample_rotation = true; break;
	case 12: o->ignore_layer_weight_animation = true; break;
	case 13: o->max_keyframe_segments = 1; break;
	case 14: o->max_keyframe_segments = 3; break;
	case 15: o->max_keyframe_segments = 1000; break;
	case 16: o->step_handling = UFBX_BAKE_STEP_HANDLING_DEFAULT; break;
	case 17: o->step_handling = UFBX_BAKE_STEP_HANDLING_CUSTOM_DURATION; break;
	case 18: o->step_handling = UFBX_BAKE_STEP_HANDLING_CUSTOM_DURATION;
		o->step_custom_duration = 0.1; break;
	case 19: o->step_handling = UFBX_BAKE_STEP_HANDLING_CUSTOM_DURATION;
		o->step_custom_duration = 0.05; o->step_custom_epsilon = 0.5; break;
	case 20: o->step_handling = UFBX_BAKE_STEP_HANDLING_IDENTICAL_TIME; break;
	case 21: o->step_handling = UFBX_BAKE_STEP_HANDLING_ADJACENT_DOUBLE; break;
	case 22: o->step_handling = UFBX_BAKE_STEP_HANDLING_IGNORE; break;
	case 23: o->key_reduction_enabled = true; break;
	case 24: o->key_reduction_enabled = true; o->key_reduction_rotation = true; break;
	case 25: o->key_reduction_enabled = true; o->key_reduction_threshold = 0.1;
		o->key_reduction_passes = 8; break;
	case 26: o->key_reduction_enabled = true; o->key_reduction_threshold = -1.0; break;
	case 27: o->key_reduction_enabled = true; o->key_reduction_passes = 1; break;
	case 28: o->evaluate_flags = UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION; break;
	case 29: o->trim_start_time = true; o->resample_rate = 15.0; o->maximum_sample_rate = 10.0;
		o->step_handling = UFBX_BAKE_STEP_HANDLING_IDENTICAL_TIME;
		o->key_reduction_enabled = true; o->key_reduction_rotation = true;
		o->key_reduction_passes = 2;
		o->no_resample_rotation = true; o->bake_transform_props = true;
		o->max_keyframe_segments = 5; o->evaluate_flags = UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION;
		break;
	default: break;
	}
	return v;
}

// The 17 scalar option fields of `ufbx_bake_opts` that the `O` record compares (the two
// `ufbx_allocator_opts` and the two `_begin_zero`/`_end_zero` sentinels are excluded: the port
// has no counterpart for allocator function pointers, see PORTING_NOTES.md, and the sentinels
// are the `ufbxi_check_opts_ptr()` test that managed objects cannot carry).
static void dump_opts(const ufbx_bake_opts *o)
{
	printf(" %d", o->trim_start_time ? 1 : 0);
	printf(" "); dbits(o->resample_rate);
	printf(" "); dbits(o->minimum_sample_rate);
	printf(" "); dbits(o->maximum_sample_rate);
	printf(" %d", o->bake_transform_props ? 1 : 0);
	printf(" %d", o->skip_node_transforms ? 1 : 0);
	printf(" %d", o->no_resample_rotation ? 1 : 0);
	printf(" %d", o->ignore_layer_weight_animation ? 1 : 0);
	printf(" %llu", (unsigned long long)o->max_keyframe_segments);
	printf(" %d", (int)o->step_handling);
	printf(" "); dbits(o->step_custom_duration);
	printf(" "); dbits(o->step_custom_epsilon);
	printf(" %u", (unsigned)o->evaluate_flags);
	printf(" %d", o->key_reduction_enabled ? 1 : 0);
	printf(" %d", o->key_reduction_rotation ? 1 : 0);
	printf(" "); dbits(o->key_reduction_threshold);
	printf(" %llu", (unsigned long long)o->key_reduction_passes);
}

// ---------------------------------------------------------------------------
// Key-list hashing
// ---------------------------------------------------------------------------

static void hash_vec3_list(const ufbx_baked_vec3_list *keys)
{
	for (size_t i = 0; i < keys->count; i++) {
		const ufbx_baked_vec3 *k = &keys->data[i];
		h_d(k->time);
		h_d(k->value.x);
		h_d(k->value.y);
		h_d(k->value.z);
		h_u32((uint32_t)k->flags);
	}
}

static void hash_quat_list(const ufbx_baked_quat_list *keys)
{
	for (size_t i = 0; i < keys->count; i++) {
		const ufbx_baked_quat *k = &keys->data[i];
		h_d(k->time);
		h_d(k->value.x);
		h_d(k->value.y);
		h_d(k->value.z);
		h_d(k->value.w);
		h_u32((uint32_t)k->flags);
	}
}

static void print_first_vec3(const ufbx_baked_vec3_list *keys)
{
	if (keys->count == 0) return;
	const ufbx_baked_vec3 *k = &keys->data[0];
	printf(" "); dbits(k->time);
	printf(" "); dbits(k->value.x);
	printf(" "); dbits(k->value.y);
	printf(" "); dbits(k->value.z);
	printf(" %u", (unsigned)k->flags);
}

static void print_first_quat(const ufbx_baked_quat_list *keys)
{
	if (keys->count == 0) return;
	const ufbx_baked_quat *k = &keys->data[0];
	printf(" "); dbits(k->time);
	printf(" "); dbits(k->value.x);
	printf(" "); dbits(k->value.y);
	printf(" "); dbits(k->value.z);
	printf(" "); dbits(k->value.w);
	printf(" %u", (unsigned)k->flags);
}

// ---------------------------------------------------------------------------
// Sample time table
// ---------------------------------------------------------------------------

// Exactly NUM_TIMES entries, in a fixed order and with no sorting or dedup: the harness derives
// the identical table from the `B` record's fields, so what is compared is the evaluation, not
// this construction. Around-range and exactly-at-range probes matter for the extrapolation and
// `time == prev->time` branches; the +-ULP neighbours make the `prev[-1].time == time` tests of
// ufbx_evaluate_baked_quat() (and the vec3 STEP_RIGHT variant) reachable.
#define NUM_TIMES 16

static void build_times(const ufbx_baked_anim *bake, double *out)
{
	const double kmin = bake->key_time_min, kmax = bake->key_time_max;
	const double pb = bake->playback_time_begin, pe = bake->playback_time_end;
	const double inf = UFBX_INFINITY;
	size_t n = 0;
	out[n++] = kmin;
	out[n++] = ufbx_nextafter(kmin, inf);
	out[n++] = ufbx_nextafter(kmin, -inf);
	out[n++] = kmax;
	out[n++] = ufbx_nextafter(kmax, inf);
	out[n++] = ufbx_nextafter(kmax, -inf);
	out[n++] = kmin + (kmax - kmin) * 0.5;
	out[n++] = pb;
	out[n++] = ufbx_nextafter(pb, inf);
	out[n++] = pe;
	out[n++] = ufbx_nextafter(pe, -inf);
	out[n++] = pb + (pe - pb) * 0.25;
	out[n++] = pb + (pe - pb) * 0.75;
	out[n++] = -1e9;
	out[n++] = 0.0;
	out[n++] = 1e9;
	ufbx_assert(n == NUM_TIMES);
}

// The key times of one channel, each with its two nextafter() neighbours, appended to `times`
// (which already holds the NUM_TIMES global entries). Index 0 == the first key, 1 == the second
// (so a two-key list is covered exactly), 2 == the middle, 3 == the last: this is what puts
// sample times exactly on and beside real keyframes.
static size_t append_key_times_v3(const ufbx_baked_vec3_list *keys, double *times, size_t n)
{
	const double inf = UFBX_INFINITY;
	static const size_t max = 4;
	for (size_t i = 0; i < max; i++) {
		size_t ix = i == 2 ? keys->count / 2 : (i == 3 ? keys->count - 1 : i);
		if (ix >= keys->count) continue;
		double t = keys->data[ix].time;
		times[n++] = t;
		times[n++] = ufbx_nextafter(t, inf);
		times[n++] = ufbx_nextafter(t, -inf);
	}
	return n;
}

static size_t append_key_times_q(const ufbx_baked_quat_list *keys, double *times, size_t n)
{
	const double inf = UFBX_INFINITY;
	static const size_t max = 4;
	for (size_t i = 0; i < max; i++) {
		size_t ix = i == 2 ? keys->count / 2 : (i == 3 ? keys->count - 1 : i);
		if (ix >= keys->count) continue;
		double t = keys->data[ix].time;
		times[n++] = t;
		times[n++] = ufbx_nextafter(t, inf);
		times[n++] = ufbx_nextafter(t, -inf);
	}
	return n;
}

#define MAX_SWEEP_TIMES (NUM_TIMES + 4 * 3 * 3)

// Evaluates every non-empty channel of a baked node over its sweep table. Both `ufbxi_check`
// free functions are pure, so the only divergence risk is the interpolation itself.
static void sweep_node(const ufbx_baked_node *node, const double *global_times, size_t *num_evals)
{
	double times[MAX_SWEEP_TIMES];
	for (size_t i = 0; i < NUM_TIMES; i++) times[i] = global_times[i];
	size_t n = NUM_TIMES;
	n = append_key_times_v3(&node->translation_keys, times, n);
	n = append_key_times_q(&node->rotation_keys, times, n);
	n = append_key_times_v3(&node->scale_keys, times, n);

	for (size_t i = 0; i < n; i++) {
		if (node->translation_keys.count > 0) {
			ufbx_vec3 v = ufbx_evaluate_baked_vec3(node->translation_keys, times[i]);
			h_d(v.x); h_d(v.y); h_d(v.z);
			(*num_evals)++;
		}
		if (node->rotation_keys.count > 0) {
			ufbx_quat q = ufbx_evaluate_baked_quat(node->rotation_keys, times[i]);
			h_d(q.x); h_d(q.y); h_d(q.z); h_d(q.w);
			(*num_evals)++;
		}
		if (node->scale_keys.count > 0) {
			ufbx_vec3 v = ufbx_evaluate_baked_vec3(node->scale_keys, times[i]);
			h_d(v.x); h_d(v.y); h_d(v.z);
			(*num_evals)++;
		}
	}
}

static void sweep_prop(const ufbx_baked_prop *prop, const double *global_times, size_t *num_evals)
{
	double times[MAX_SWEEP_TIMES];
	for (size_t i = 0; i < NUM_TIMES; i++) times[i] = global_times[i];
	size_t n = append_key_times_v3(&prop->keys, times, NUM_TIMES);

	for (size_t i = 0; i < n; i++) {
		if (prop->keys.count > 0) {
			ufbx_vec3 v = ufbx_evaluate_baked_vec3(prop->keys, times[i]);
			h_d(v.x); h_d(v.y); h_d(v.z);
			(*num_evals)++;
		}
	}
}

// ---------------------------------------------------------------------------
// ufbx_find_baked_{node,element}() sweep
// ---------------------------------------------------------------------------

static void hash_find_node(ufbx_baked_anim *bake, ufbx_node *node, size_t *num_finds)
{
	// Both forms must agree, and both must agree with a linear scan: C's `find_baked_node()`
	// forwards to `_by_typed_id()` after the NULL test.
	ufbx_baked_node *a = ufbx_find_baked_node(bake, node);
	ufbx_baked_node *b = ufbx_find_baked_node_by_typed_id(bake, node->typed_id);
	h_u32(a ? 1u : 0u);
	h_u32(b ? 1u : 0u);
	if (a) {
		h_u32(a->typed_id);
		h_u32(a->element_id);
		h_u32((uint32_t)a->translation_keys.count);
		h_d(a->translation_keys.count ? a->translation_keys.data[0].time : 0.0);
		h_d(a->translation_keys.count ? a->translation_keys.data[a->translation_keys.count - 1].time : 0.0);
		h_u32((uint32_t)a->rotation_keys.count);
		h_u32((uint32_t)a->scale_keys.count);
		h_byte(a->constant_translation ? 1 : 0);
		h_byte(a->constant_rotation ? 1 : 0);
		h_byte(a->constant_scale ? 1 : 0);
	}
	if (b) {
		h_u32(b->typed_id);
		h_u32(b->element_id);
		h_byte(b->constant_translation ? 1 : 0);
	}
	(*num_finds) += 2;
}

static void hash_find_element(ufbx_baked_anim *bake, ufbx_element *element, size_t *num_finds)
{
	ufbx_baked_element *a = ufbx_find_baked_element(bake, element);
	ufbx_baked_element *b = ufbx_find_baked_element_by_element_id(bake, element->element_id);
	h_u32(a ? 1u : 0u);
	h_u32(b ? 1u : 0u);
	if (a) {
		h_u32(a->element_id);
		h_sz(a->props.count);
		h_sz(a->props.count ? a->props.data[0].keys.count : 0);
		h_d(a->props.count ? a->props.data[0].keys.data[0].time : 0.0);
	}
	if (b) {
		h_u32(b->element_id);
		h_sz(b->props.count);
	}
	// A miss for an animated element would be a real bug; the sweep covers hits and misses
	// through every element id, so also probe an id that cannot be baked.
	ufbx_baked_element *miss = ufbx_find_baked_element_by_element_id(bake, UINT32_MAX);
	h_u32(miss ? 1u : 0u);
	(*num_finds) += 3;
}

// ---------------------------------------------------------------------------
// Per-variant dump
// ---------------------------------------------------------------------------

static void dump_variant(int fi, int vi, ufbx_scene *scene)
{
	bake_variant v = make_variant(vi);
	const ufbx_anim *anim = v.explicit_anim ? scene->anim : NULL;
	if (!scene->anim) return;   // C would dereference NULL in ufbxi_bake_anim_imp (27723)

	ufbx_bake_opts *popts = v.null_opts ? NULL : &v.opts;
	ufbx_error err;
	memset(&err, 0, sizeof(err));

	ufbx_baked_anim *bake = ufbx_bake_anim(scene, anim, popts, &err);

	const ufbx_anim *resolved = anim ? anim : scene->anim;
	printf("B %d %d %d %d %d %zu %zu %zu %zu ", fi, vi, anim ? 1 : 0, bake ? 1 : 0, (int)err.type,
		resolved->layers.count, resolved->override_layer_weights.count,
		resolved->prop_overrides.count, resolved->transform_overrides.count);
	dbits(resolved->time_begin); printf(" "); dbits(resolved->time_end);
	printf(" %zu %zu ", bake ? bake->nodes.count : (size_t)0, bake ? bake->elements.count : (size_t)0);
	dbits(bake ? bake->playback_time_begin : 0.0); printf(" ");
	dbits(bake ? bake->playback_time_end : 0.0); printf(" ");
	dbits(bake ? bake->playback_duration : 0.0); printf(" ");
	dbits(bake ? bake->key_time_min : 0.0); printf(" ");
	dbits(bake ? bake->key_time_max : 0.0); printf("\n");

	// The caller's struct after the call: must be exactly what make_variant() wrote.
	printf("O %d %d %d", fi, vi, v.null_opts ? 1 : 0);
	if (!v.null_opts) dump_opts(&v.opts);
	printf("\n");

	if (!bake) return;

	double global_times[NUM_TIMES];
	build_times(bake, global_times);
	for (size_t i = 0; i < NUM_TIMES; i++) {
		printf("T %d %d %zu ", fi, vi, i);
		dbits(global_times[i]);
		printf("\n");
	}

	for (size_t ni = 0; ni < bake->nodes.count; ni++) {
		const ufbx_baked_node *node = &bake->nodes.data[ni];
		h_reset(); hash_vec3_list(&node->translation_keys);
		uint64_t ht = g_fnv;
		h_reset(); hash_quat_list(&node->rotation_keys);
		uint64_t hr = g_fnv;
		h_reset(); hash_vec3_list(&node->scale_keys);
		uint64_t hs = g_fnv;

		printf("N %d %d %zu %u %u %d %d %d %zu %zu %zu ", fi, vi, ni,
			(unsigned)node->typed_id, (unsigned)node->element_id,
			node->constant_translation ? 1 : 0, node->constant_rotation ? 1 : 0,
			node->constant_scale ? 1 : 0,
			node->translation_keys.count, node->rotation_keys.count, node->scale_keys.count);
		zbits(ht); printf(" "); zbits(hr); printf(" "); zbits(hs);
		print_first_vec3(&node->translation_keys);
		print_first_quat(&node->rotation_keys);
		print_first_vec3(&node->scale_keys);
		printf("\n");

		h_reset();
		size_t num_evals = 0;
		sweep_node(node, global_times, &num_evals);
		printf("Q %d %d %zu ", fi, vi, ni);
		zbits(g_fnv);
		printf(" %zu\n", num_evals);
	}

	for (size_t ei = 0; ei < bake->elements.count; ei++) {
		const ufbx_baked_element *element = &bake->elements.data[ei];
		printf("E %d %d %zu %u %zu\n", fi, vi, ei, (unsigned)element->element_id, element->props.count);

		for (size_t pi = 0; pi < element->props.count; pi++) {
			const ufbx_baked_prop *prop = &element->props.data[pi];
			h_reset(); hash_vec3_list(&prop->keys);
			printf("P %d %d %zu %zu ", fi, vi, ei, pi);
			zbits(fnv_str(prop->name)); printf(" %d %zu ", prop->constant_value ? 1 : 0, prop->keys.count);
			zbits(g_fnv);
			print_first_vec3(&prop->keys);
			printf("\n");

			h_reset();
			size_t num_evals = 0;
			sweep_prop(prop, global_times, &num_evals);
			printf("R %d %d %zu %zu ", fi, vi, ei, pi);
			zbits(g_fnv);
			printf(" %zu\n", num_evals);
		}
	}

	h_reset();
	size_t num_finds = 0;
	for (size_t ni = 0; ni < scene->nodes.count; ni++) {
		hash_find_node(bake, scene->nodes.data[ni], &num_finds);
	}
	for (size_t ei = 0; ei < scene->elements.count; ei++) {
		hash_find_element(bake, scene->elements.data[ei], &num_finds);
	}
	printf("F %d %d ", fi, vi);
	zbits(g_fnv);
	printf(" %zu\n", num_finds);

	// The refcount API is a no-op in the port (PORTING_NOTES.md #4); retain then release once so
	// C keeps ownership of `bake` for the free below and the code path is at least exercised.
	ufbx_retain_baked_anim(bake);
	ufbx_free_baked_anim(bake);
	ufbx_free_baked_anim(bake);
}

// ---------------------------------------------------------------------------

static ufbx_scene *load_golden_scene(const char *filename, int mode, ufbx_error *error)
{
	ufbx_load_opts opts = { 0 };
	opts.load_external_files = true;
	opts.ignore_missing_external_files = true;
	opts.evaluate_caches = true;
	opts.evaluate_skinning = true;
	opts.target_axes = ufbx_axes_right_handed_y_up;
	opts.target_unit_meters = 1.0f;
	// 0 PRESERVE (the golden build's zeroed default), 1 HELPER_NODES, 2 COMPENSATE,
	// 3 COMPENSATE_NO_FALLBACK, 4 IGNORE -- see ufbx.h:3680-3702.
	// Only HELPER_NODES/COMPENSATE* ever create `is_scale_helper` nodes (ufbx.c:18484-18494), and
	// those are what make the scale-helper half of ufbxi_bake_node_imp() reachable.
	opts.inherit_mode_handling = (ufbx_inherit_mode_handling)mode;
	return ufbx_load_file(filename, &opts, error);
}

int main(int argc, char **argv)
{
	if (argc < 3 || (argc - 1) % 2 != 0) {
		fprintf(stderr, "usage: bake_oracle.exe (<mode> <file.fbx>)...\n");
		return 2;
	}

	for (int fi = 0, a = 1; a + 1 <= argc; fi++, a += 2) {
		int mode = atoi(argv[a]);
		const char *path = argv[a + 1];

		ufbx_error error;
		ufbx_scene *scene = load_golden_scene(path, mode, &error);
		if (!scene) {
			printf("S %d %d 0 0 0 0 0 %d\n", fi, mode, (int)error.type);
			fprintf(stderr, "load failed: %s: %s\n", path, error.description.data);
			continue;
		}

		printf("S %d %d 1 %zu %zu %zu %d\n", fi, mode, scene->nodes.count, scene->elements.count,
			scene->meshes.count, scene->anim ? 1 : 0);

		for (int vi = 0; vi < NUM_VARIANTS; vi++) {
			dump_variant(fi, vi, scene);
		}

		ufbx_free_scene(scene);
	}
	return 0;
}
