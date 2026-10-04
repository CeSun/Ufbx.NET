// C reference oracle for the S4b-1 animation-evaluation module of the ufbx -> C# port.
// `#include "ufbx.c"` so the internal `ufbxi_*` helpers (prop iterator, layer combining) are
// exercised through the public `ufbx_evaluate_*` entry points they back.
//
// Loads real animated FBX files with the golden opts of test/hash_scene.c load_scene() and
// replays every evaluation entry point of ufbx.c:30643-31184 over a spread of times (inside,
// outside and exactly at the keyframe ranges, so extrapolation/mirroring is covered), emitting
// one FNV-1a-64 rollup per (file, time, kind) plus explicit records for a bounded prefix so a
// mismatch is localisable. The C# harness (tools/S4bCheck) parses the times back out of the `T`
// records (so the frame->time arithmetic is not part of this differential) and replays the same
// calls through the ported evaluators.
//
// Output grammar (space separated, one record per line; `<h>` = lowercase 16 hex digits of the
// IEEE-754 bits of a double, `<z>` = 16 hex digits of an FNV-1a-64, `<i>` = decimal):
//   S  <fi> <num_elems> <num_nodes> <num_curves> <num_anim_values> <num_blend_channels>
//      <num_anim_layers> <num_prop_overrides>                     per-file summary
//   T  <fi> <ti> <time:h>                                         time table (replayed by port)
//   P  <fi> <ti> <z> <i>                                          ufbx_evaluate_props[flags]
//   Q  <fi> <ti> <z> <i>                                          ufbx_evaluate_prop[_flags]
//   R  <fi> <ti> <z> <i>                                          ufbx_evaluate_transform[flags]
//   B  <fi> <ti> <z> <i>                                          ufbx_evaluate_blend_weight
//   C  <fi> <ti> <z> <i>                                          ufbx_evaluate_curve[_flags]
//   A  <fi> <ti> <z> <i>                                          ufbx_evaluate_anim_value_*
//   L  <fi> <ti> <z> <i>                                          layer weight + might_contain_id
//   D  <fi> <z> <i>                                               ufbx_find_* (time independent)
//   X  <fi> <z> <i>                                               ufbx_(catch_)get_vertex_* per mesh
//                                                                 attribute, incl. the index==count
//                                                                 panic probe (see dump_vertex_access)
//   Y  <fi> <z> <i>                                               same accessors over a synthetic
//                                                                 attribute (NO_INDEX / corrupted
//                                                                 index; see dump_synth_access)
//   F  <fi> <part> <z> <i>                                        scene/element lookup group
//                                                                 (ufbx.c:30720-30833, 31413-31484,
//                                                                 32389-32398;
//                                                                 part 1 element-by-name, 2 miss sweep,
//                                                                 3 prop->element, 4 anim props,
//                                                                 5 material texture + shader
//                                                                 bindings, 6 shader texture inputs,
//                                                                 7 bone poses, 8 compatible normal
//                                                                 matrices, 9 face index lookup;
//                                                                 see dump_scene_find)
//   V  <fi> <frame> <z>                                           ufbx_evaluate_scene + ufbxt_hash_scene
//                                                                 (frame = i*i, i = 1..9; `<z>` is
//                                                                 the golden hash in tools/golden_hashes.txt)
//   W  <fi> <z>                                                   source scene re-hashed after the V
//                                                                 sweep (must equal its frame-0 golden)
//   E  <fi> <ti> <kind> <elem_id> <sub> <props...>                explicit record (only with -v)
//   DONE <num_files> <fail>
//
// `<z>` for a kind is the FNV-1a-64 of every sub-record for that (file, time, kind) in the
// iteration order documented at each dump site; `<i>` is the number of sub-records folded in.
//
// Build (mandatory flags, see PORTING_NOTES "浮点语义"):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx \
//       tools/s4b_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o tools/s4b_oracle.exe
// Run (from C:/Workspace/_analyze_ufbx so the data/ paths resolve):
//   C:/Workspace/ufbx-cs/tools/s4b_oracle.exe \
//     data/maya_anim_extrapolation_7700_binary.fbx ... > C:/Workspace/ufbx-cs/tools/s4b_oracle.txt

// MANDATORY, and the reason `extra/ufbx_math.c` must be on the link line: this is the config of
// the golden generator itself (`test/hash_scene.c:284` defines the very same macro, and
// `misc/run_tests.py:1541` compiles `["test/hash_scene.c", "extra/ufbx_math.c"]`).
// Without it ufbx.c:238-277 defines `UFBX_MATH_PREFIX` as an *empty* macro, so `ufbx_atan2`
// token-pastes to plain `atan2` and the oracle silently evaluates against the host CRT instead
// (zig cc is x86_64-windows-gnu -> api-ms-win-crt-math = UCRT/Intel IML). The two differ by
// 1 ULP on ~20% of moderate-magnitude `atan2` inputs, which is exactly the call `ufbx_quat_to_euler`
// makes on the additive/blended `compose_rotation` layer path, and produced 243 phantom
// "port divergences" before this line was added. `#define` here, not on the command line, so the
// oracle cannot be rebuilt into the wrong configuration by forgetting a flag.
#define UFBX_EXTERNAL_MATH

#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

// The golden generator's own scene hasher (`test/hash_scene.h`, included exactly as
// `test/hash_scene.c:6` does). The `V` records below hash an evaluated scene with it, which makes
// them directly comparable with the authoritative `tools/golden_hashes.txt` entries -- i.e. a `V`
// line is both the differential control for the port's clone/evaluate pass and a check that this
// oracle is built in the right configuration.
#include "test/hash_scene.h"

static const uint64_t FNV_BASIS = UINT64_C(0xcbf29ce484222325);
static const uint64_t FNV_PRIME = UINT64_C(0x100000001b3);

static uint64_t g_hash = FNV_BASIS;

static void hh_bytes(const void *data, size_t size) {
	const uint8_t *p = (const uint8_t*)data;
	for (size_t i = 0; i < size; i++) {
		g_hash ^= p[i];
		g_hash *= FNV_PRIME;
	}
}

static void hh_u64(uint64_t v) { hh_bytes(&v, 8); }
static void hh_i64(int64_t v) { hh_bytes(&v, 8); }
static void hh_d(double d) { hh_bytes(&d, 8); }
static void hh_u32(uint32_t v) { hh_bytes(&v, 4); }
static void hh_i32(int32_t v) { hh_bytes(&v, 4); }

static uint64_t hash_take(void) {
	uint64_t h = g_hash;
	g_hash = FNV_BASIS;
	return h;
}

static void dbits(double v) {
	uint64_t u;
	memcpy(&u, &v, 8);
	printf("%016llx", (unsigned long long)u);
}

static void hbits(uint64_t v) {
	printf("%016llx", (unsigned long long)v);
}

// -- Explicit record support (`-v`): only used by the localisation path, so the default output
// stays small while a single (file, time, kind) can still be dumped value by value.

static int g_verbose = 0;
static int g_verbose_file = -1;
static int g_verbose_time = -1;
static char g_verbose_kind = 0;

static bool verbose_here(int fi, int ti, char kind) {
	return g_verbose && fi == g_verbose_file && (g_verbose_time < 0 || ti == g_verbose_time)
		&& (g_verbose_kind == 0 || g_verbose_kind == kind);
}

static void dump_prop(const ufbx_prop *p) {
	printf("%08x ", p->_internal_key);
	printf("%d %d ", (int)p->type, (uint32_t)p->flags);
	dbits(p->value_vec4.x); printf(" ");
	dbits(p->value_vec4.y); printf(" ");
	dbits(p->value_vec4.z); printf(" ");
	dbits(p->value_vec4.w); printf(" ");
	printf("%lld ", (long long)p->value_int);
	printf("%zu", p->value_str.length);
	if (p->value_str.length > 0) {
		printf(" \"");
		for (size_t i = 0; i < p->value_str.length; i++) {
			uint8_t c = (uint8_t)p->value_str.data[i];
			putchar(c >= 0x20 && c < 0x7f ? c : '.');
		}
		printf("\"");
	}
	printf(" %zu", p->value_blob.size);
}

// Fold one property into the running hash. Order-sensitive and covers every observable field.
static void hash_prop(const ufbx_prop *p) {
	hh_u32(p->_internal_key);
	hh_i32((int32_t)p->type);
	hh_u32((uint32_t)p->flags);
	hh_d(p->value_vec4.x);
	hh_d(p->value_vec4.y);
	hh_d(p->value_vec4.z);
	hh_d(p->value_vec4.w);
	hh_i64(p->value_int);
	hh_bytes(p->value_str.data, p->value_str.length);
	hh_u64((uint64_t)p->value_str.length);
	hh_bytes(p->value_blob.data, p->value_blob.size);
	hh_u64((uint64_t)p->value_blob.size);
}

static void hash_transform(const ufbx_transform *t) {
	hh_d(t->translation.x); hh_d(t->translation.y); hh_d(t->translation.z);
	hh_d(t->rotation.x); hh_d(t->rotation.y); hh_d(t->rotation.z); hh_d(t->rotation.w);
	hh_d(t->scale.x); hh_d(t->scale.y); hh_d(t->scale.z);
}

// -- Corpus / option setup (identical to test/hash_scene.c load_scene())

static ufbx_scene *load_golden_scene(const char *filename, ufbx_error *error)
{
	ufbx_load_opts opts = { 0 };
	opts.load_external_files = true;
	opts.ignore_missing_external_files = true;
	opts.evaluate_caches = true;
	opts.evaluate_skinning = true;
	opts.target_axes = ufbx_axes_right_handed_y_up;
	opts.target_unit_meters = 1.0f;
	return ufbx_load_file(filename, &opts, error);
}

// Times to evaluate: a mix of whole frames (the golden path), half/quarter frames (interpolation
// inside segments) and values far outside [time_begin, time_end] (extrapolation, mirroring and
// the repeat-count clamps). `t` is written through `out` in iteration order.
static size_t build_times(const ufbx_scene *scene, double *out, size_t max)
{
	const double fps = scene->settings.frames_per_second;
	const double tb = scene->anim ? scene->anim->time_begin : 0.0;
	static const double frames[] = {
		-1000.0, -137.0, -64.0, -13.0, -3.0, -1.0, -0.5, 0.0, 0.25, 0.5, 1.0, 1.75,
		2.0, 3.0, 4.0, 5.5, 7.0, 9.0, 12.0, 13.0, 16.0, 21.0, 24.0, 33.0, 48.0, 64.0,
		100.0, 137.0, 250.0, 1000.0,
	};
	size_t n = 0;
	for (size_t i = 0; i < ufbxi_arraycount(frames) && n < max; i++) {
		out[n++] = tb + frames[i] / fps;
	}
	// Exact range endpoints and their neighbours: these hit the `time == min/max` branches and
	// the "first/last keyframe" returns of ufbx_evaluate_curve_flags().
	if (scene->anim && n + 4 <= max) {
		out[n++] = scene->anim->time_begin;
		out[n++] = scene->anim->time_end;
		out[n++] = scene->anim->time_begin - 1.0 / fps;
		out[n++] = scene->anim->time_end + 1.0 / fps;
	}
	// Degenerate times exercising pow_abs/interp edge cases.
	if (n + 4 <= max) {
		out[n++] = 0.0;
		out[n++] = -0.0;
		out[n++] = 1e-7;
		out[n++] = 1.0 / (fps * 3.0);
	}
	return n;
}

// Property names used by the time-independent `ufbx_find_*` sweep and by the single-prop
// evaluation sweep (`Q`). All are real FBX prop names so the sorted searches are exercised.
static const char *const k_prop_names[] = {
	"Lcl Translation", "Lcl Rotation", "Lcl Scaling",
	"RotationOrder", "PreRotation", "PostRotation",
	"RotationPivot", "RotationOffset", "ScalingPivot", "ScalingOffset",
	"GeometricTranslation", "GeometricRotation", "GeometricScaling",
	"DeformPercent", "DefaultAttributeIndex", "InheritType",
	"Color", "DiffuseColor", "EmissiveColor", "SpecularColor", "Shininess",
	"Intensity", "FarPlane", "NearPlane", "FieldOfView", "FilmWidth", "FilmHeight",
	"Size", "Name", "FileName", "CurrentTimeMarker",
	"Missing", "Missing Longer Name", "a", "ab", "abc", "z",
};

#define MAX_PROPS 512

// `ufbx_evaluate_props_flags()` over every element of the scene, in (type, index) order.
static void dump_props(int fi, int ti, int variant, const ufbx_scene *scene, double time, uint32_t flags)
{
	ufbx_prop buf[MAX_PROPS];
	size_t total = 0;
	g_hash = FNV_BASIS;

	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t ei = 0; ei < scene->elements_by_type[type].count; ei++) {
			ufbx_element *elem = scene->elements_by_type[type].data[ei];
			ufbx_props props = ufbx_evaluate_props_flags(scene->anim, elem, time, buf, MAX_PROPS, flags);
			total++;
			hh_u32(elem->element_id);
			hh_u32((uint32_t)type);
			hh_u32((uint32_t)ei);
			hh_u32((uint32_t)props.props.count);
			hh_u32((uint32_t)props.num_animated);
			for (size_t i = 0; i < props.props.count; i++) {
				hash_prop(&props.props.data[i]);
			}
			if (verbose_here(fi, ti, 'P')) {
				printf("E %d %d P %d %u %zu ", fi, ti, variant, elem->element_id, props.props.count);
				for (size_t i = 0; i < props.props.count; i++) {
					if (i > 0) printf(" | ");
					dump_prop(&props.props.data[i]);
				}
				printf("\n");
			}
		}
	}
	printf("P %d %d %d ", fi, ti, variant); hbits(hash_take()); printf(" %zu\n", total);

	// `ufbx_evaluate_prop_flags_len()` over a fixed name sweep table per element. The name table
	// covers present names, absent names (the NOT_FOUND construction) and short names (the
	// `len < 4` key branch).
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t ei = 0; ei < scene->elements_by_type[type].count; ei++) {
			ufbx_element *elem = scene->elements_by_type[type].data[ei];
			for (size_t n = 0; n < ufbxi_arraycount(k_prop_names); n++) {
				const char *name = k_prop_names[n];
				size_t len = strlen(name);
				ufbx_prop pr = ufbx_evaluate_prop_flags_len(scene->anim, elem, name, len, time, flags);
				total++;
				hh_u32(elem->element_id);
				hh_u32((uint32_t)n);
				hash_prop(&pr);
				if (verbose_here(fi, ti, 'Q')) {
					printf("E %d %d Q %d %u %zu ", fi, ti, variant, elem->element_id, n);
					dump_prop(&pr);
					printf("\n");
				}
			}
		}
	}
	printf("Q %d %d %d ", fi, ti, variant); hbits(hash_take()); printf(" %zu\n", total);
}

static void dump_transforms(int fi, int ti, const ufbx_scene *scene, double time)
{
	static const uint32_t k_flags[] = {
		0,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_ROTATION,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_SCALE,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_TRANSLATION,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_ROTATION | UFBX_TRANSFORM_FLAG_INCLUDE_SCALE,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_ROTATION | UFBX_TRANSFORM_FLAG_INCLUDE_TRANSLATION,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_SCALE | UFBX_TRANSFORM_FLAG_INCLUDE_TRANSLATION,
		UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_ROTATION | UFBX_TRANSFORM_FLAG_INCLUDE_SCALE | UFBX_TRANSFORM_FLAG_INCLUDE_TRANSLATION,
		UFBX_TRANSFORM_FLAG_IGNORE_SCALE_HELPER,
		UFBX_TRANSFORM_FLAG_IGNORE_COMPONENTWISE_SCALE,
		UFBX_TRANSFORM_FLAG_IGNORE_SCALE_HELPER | UFBX_TRANSFORM_FLAG_IGNORE_COMPONENTWISE_SCALE,
		UFBX_TRANSFORM_FLAG_NO_EXTRAPOLATION,
		UFBX_TRANSFORM_FLAG_NO_EXTRAPOLATION | UFBX_TRANSFORM_FLAG_EXPLICIT_INCLUDES | UFBX_TRANSFORM_FLAG_INCLUDE_ROTATION | UFBX_TRANSFORM_FLAG_INCLUDE_SCALE,
	};

	size_t total = 0;
	g_hash = FNV_BASIS;
	for (size_t i = 0; i < scene->nodes.count; i++) {
		ufbx_node *node = scene->nodes.data[i];
		for (size_t f = 0; f < ufbxi_arraycount(k_flags); f++) {
			ufbx_transform t = ufbx_evaluate_transform_flags(scene->anim, node, time, k_flags[f]);
			total++;
			hh_u32(node->element.element_id);
			hh_u32((uint32_t)f);
			hash_transform(&t);
			if (verbose_here(fi, ti, 'R')) {
				printf("E %d %d R %u %zu ", fi, ti, node->element.element_id, f);
				dbits(t.translation.x); printf(" "); dbits(t.translation.y); printf(" "); dbits(t.translation.z); printf(" ");
				dbits(t.rotation.x); printf(" "); dbits(t.rotation.y); printf(" "); dbits(t.rotation.z); printf(" "); dbits(t.rotation.w); printf(" ");
				dbits(t.scale.x); printf(" "); dbits(t.scale.y); printf(" "); dbits(t.scale.z);
				printf("\n");
			}
		}
	}
	printf("R %d %d ", fi, ti); hbits(hash_take()); printf(" %zu\n", total);
}

static void dump_blend_weights(int fi, int ti, const ufbx_scene *scene, double time)
{
	size_t total = 0;
	g_hash = FNV_BASIS;
	for (size_t i = 0; i < scene->blend_channels.count; i++) {
		ufbx_blend_channel *ch = scene->blend_channels.data[i];
		for (uint32_t f = 0; f < 2; f++) {
			double w = ufbx_evaluate_blend_weight_flags(scene->anim, ch, time, f ? UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION : 0);
			total++;
			hh_u32(ch->element.element_id);
			hh_u32(f);
			hh_d(w);
			if (verbose_here(fi, ti, 'B')) {
				printf("E %d %d B %u %u ", fi, ti, ch->element.element_id, f);
				dbits(w); printf("\n");
			}
		}
	}
	printf("B %d %d ", fi, ti); hbits(hash_take()); printf(" %zu\n", total);
}

static void dump_curves(int fi, int ti, const ufbx_scene *scene, double time)
{
	size_t total = 0;

	// Curve evaluation at the swept time plus at each curve's own range boundaries: the
	// boundary times are what drive `ufbxi_extrapolate_curve`'s mirror/repeat arithmetic.
	g_hash = FNV_BASIS;
	for (size_t i = 0; i < scene->anim_curves.count; i++) {
		ufbx_anim_curve *curve = scene->anim_curves.data[i];
		double ct[9];
		size_t cn = 0;
		ct[cn++] = time;
		ct[cn++] = curve->min_time;
		ct[cn++] = curve->max_time;
		ct[cn++] = curve->min_time - 1.0;
		ct[cn++] = curve->max_time + 1.0;
		ct[cn++] = curve->min_time - 0.25;
		ct[cn++] = curve->max_time + 0.25;
		ct[cn++] = curve->min_time + (curve->max_time - curve->min_time) * 0.5;
		ct[cn++] = curve->min_time + (curve->max_time - curve->min_time) * 0.5 + 1e-9;
		for (size_t c = 0; c < cn; c++) {
			for (uint32_t f = 0; f < 2; f++) {
				double v = ufbx_evaluate_curve_flags(curve, ct[c], 1.2345678, f ? UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION : 0);
				total++;
				hh_u32(curve->element.element_id);
				hh_u32((uint32_t)c);
				hh_u32(f);
				hh_d(ct[c]);
				hh_d(v);
				if (verbose_here(fi, ti, 'C')) {
					printf("E %d %d C %u %zu %u ", fi, ti, curve->element.element_id, c, f);
					dbits(ct[c]); printf(" "); dbits(v); printf("\n");
				}
			}
		}
	}
	printf("C %d %d ", fi, ti); hbits(hash_take()); printf(" %zu\n", total);

	// The `A` rollup counts only its own sub-records, like every other emitter here.
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t i = 0; i < scene->anim_values.count; i++) {
		ufbx_anim_value *av = scene->anim_values.data[i];
		for (uint32_t f = 0; f < 2; f++) {
			uint32_t flags = f ? UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION : 0;
			double r = ufbx_evaluate_anim_value_real_flags(av, time, flags);
			ufbx_vec3 v = ufbx_evaluate_anim_value_vec3_flags(av, time, flags);
			total++;
			hh_u32(av->element.element_id);
			hh_u32(f);
			hh_d(r);
			hh_d(v.x); hh_d(v.y); hh_d(v.z);
			if (verbose_here(fi, ti, 'A')) {
				printf("E %d %d A %u %u ", fi, ti, av->element.element_id, f);
				dbits(r); printf(" "); dbits(v.x); printf(" "); dbits(v.y); printf(" "); dbits(v.z); printf("\n");
			}
		}
	}
	printf("A %d %d ", fi, ti); hbits(hash_take()); printf(" %zu\n", total);
}

static void dump_layers(int fi, int ti, const ufbx_scene *scene, double time)
{
	// Exercises `ufbxi_anim_layer_might_contain_id` and the animated-layer-weight lookup inside
	// ufbxi_evaluate_props() by evaluating the props of every element each layer mentions.
	size_t total = 0;
	g_hash = FNV_BASIS;
	for (size_t i = 0; i < scene->anim_layers.count; i++) {
		ufbx_anim_layer *layer = scene->anim_layers.data[i];
		hh_u32(layer->element.element_id);
		hh_d(layer->weight);
		hh_u32((uint32_t)layer->weight_is_animated);
		hh_u32((uint32_t)layer->blended);
		hh_u32((uint32_t)layer->additive);
		hh_u32((uint32_t)layer->compose_rotation);
		hh_u32((uint32_t)layer->compose_scale);
		hh_u32(layer->_min_element_id);
		hh_u32(layer->_max_element_id);
		for (size_t k = 0; k < 4; k++) hh_u32(layer->_element_id_bitmask[k]);
		total++;

		// Bitmask reachability for a window of ids around the layer's range (mirrors the
		// unsigned-wraparound predicate of ufbxi_anim_layer_might_contain_id).
		for (int64_t d = -8; d <= 8; d++) {
			uint32_t id = layer->_min_element_id + (uint32_t)d;
			bool ok = ufbxi_anim_layer_might_contain_id(layer, id);
			hh_i64(d);
			hh_u32(ok ? 1u : 0u);
			total++;
		}

		for (size_t a = 0; a < layer->anim_props.count; a++) {
			ufbx_anim_prop *ap = &layer->anim_props.data[a];
			hh_u32(ap->element->element_id);
			hh_u32(ap->_internal_key);
			hh_bytes(ap->prop_name.data, ap->prop_name.length);
			double r = ufbx_evaluate_anim_value_real_flags(ap->anim_value, time, 0);
			ufbx_vec3 v = ufbx_evaluate_anim_value_vec3_flags(ap->anim_value, time, 0);
			hh_d(r); hh_d(v.x); hh_d(v.y); hh_d(v.z);
			total++;
			if (verbose_here(fi, ti, 'L')) {
				printf("E %d %d L %u %zu ", fi, ti, ap->element->element_id, a);
				dbits(r); printf(" "); dbits(v.x); printf(" "); dbits(v.y); printf(" "); dbits(v.z);
				printf("\n");
			}
		}
	}
	printf("L %d %d ", fi, ti); hbits(hash_take()); printf(" %zu\n", total);
}

static void dump_find(int fi, const ufbx_scene *scene)
{
	// Time-independent: ufbx_find_prop/real/vec3/int/bool/string/blob over every element's own
	// props plus the default chain, using the name sweep table.
	size_t total = 0;
	g_hash = FNV_BASIS;
	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t ei = 0; ei < scene->elements_by_type[type].count; ei++) {
			ufbx_element *elem = scene->elements_by_type[type].data[ei];
			for (size_t n = 0; n < ufbxi_arraycount(k_prop_names); n++) {
				const char *name = k_prop_names[n];
				size_t len = strlen(name);
				ufbx_prop *p = ufbx_find_prop_len(&elem->props, name, len);
				total++;
				hh_u32(elem->element_id);
				hh_u32((uint32_t)n);
				hh_u32(p ? 1u : 0u);
				if (p) hash_prop(p);
				hh_d(ufbx_find_real_len(&elem->props, name, len, -987.654));
				ufbx_vec3 v = ufbx_find_vec3_len(&elem->props, name, len, (ufbx_vec3){ { 1.5, 2.5, 3.5 } });
				hh_d(v.x); hh_d(v.y); hh_d(v.z);
				hh_i64(ufbx_find_int_len(&elem->props, name, len, -424242));
				hh_u32(ufbx_find_bool_len(&elem->props, name, len, true) ? 1u : 0u);
				ufbx_string s = ufbx_find_string_len(&elem->props, name, len, (ufbx_string){ "s", 1 });
				hh_bytes(s.data, s.length);
				hh_u64((uint64_t)s.length);
				ufbx_blob b = ufbx_find_blob_len(&elem->props, name, len, (ufbx_blob){ NULL, 0 });
				hh_bytes(b.data, b.size);
				hh_u64((uint64_t)b.size);
			}
		}
	}
	printf("D %d ", fi); hbits(hash_take()); printf(" %zu\n", total);
}

// =====================================================================
// Scene/element lookup group: ufbx.c:30720-30833 and 31413-31484, i.e.
// ufbx_find_element_len / ufbx_find_node_len / ufbx_find_material_len / ufbx_find_anim_stack_len,
// ufbx_get_prop_element / ufbx_find_prop_element_len, ufbx_find_anim_prop_len /
// ufbx_find_anim_props, ufbx_find_prop_texture_len, ufbx_find_shader_prop(_bindings)_len,
// ufbx_find_shader_texture_input_len, ufbx_get_bone_pose and
// ufbx_get_compatible_matrix_for_normals, plus the `strlen` forms of ufbx.c:33157-33162.
//
// One `F <fi> <part> <z> <i>` rollup per part (8 parts, documented at each block) so a mismatch
// localises to a group without needing explicit records. Everything here is time-independent and
// runs on the source scene, like `D`.
// =====================================================================

// Names that are not expected to occur as element names: the miss path of the binary searches,
// including the `len < 4` key branch ("", "a", "ab", "abc").
static const char *const k_miss_names[] = {
	"", "a", "ab", "abc", "Nope", "Nope Longer", "World::Root", "zzzzzzzz",
};

static const ufbx_element_type k_conn_types[] = {
	UFBX_ELEMENT_NODE, UFBX_ELEMENT_MATERIAL, UFBX_ELEMENT_TEXTURE, UFBX_ELEMENT_ANIM_VALUE,
};

static void hh_element(const ufbx_element *e) {
	hh_u32(e ? e->element_id : 0xFFFFFFFFu);
}

static void hash_str(ufbx_string s) {
	hh_bytes(s.data, s.length);
	hh_u64((uint64_t) s.length);
}

static void hash_matrix(const ufbx_matrix *m) {
	hh_d(m->m00); hh_d(m->m01); hh_d(m->m02); hh_d(m->m03);
	hh_d(m->m10); hh_d(m->m11); hh_d(m->m12); hh_d(m->m13);
	hh_d(m->m20); hh_d(m->m21); hh_d(m->m22); hh_d(m->m23);
}

static void hh_aprop(const ufbx_anim_prop *ap) {
	hh_u32(ap ? 1u : 0u);
	if (ap) {
		hh_u32(ap->element->element_id);
		hash_str(ap->prop_name);
	}
}

static void hh_aprop_list(ufbx_anim_prop_list list) {
	hh_u64((uint64_t) list.count);
	if (list.count > 0) {
		hh_u32(list.data[0].element->element_id);
		hash_str(list.data[0].prop_name);
		hash_str(list.data[list.count - 1].prop_name);
	}
}

// `ufbxi_safe_string()` prefix modes: full length, one byte short (a near-miss name), and length
// zero (C's empty string regardless of `data`). An over-long length is not probed: C would read
// past the stored name, which is not representable by the port's strings.
static size_t prefix_mode_len(size_t length, size_t mode)
{
	if (mode == 0) return length;
	if (mode == 1) return length > 0 ? length - 1 : 0;
	return 0;
}

// Synthetic inputs for the last `F` part. Not decoration: no corpus node in tools/s4b_corpus.txt has
// a non-identity `geometry_transform.rotation`, so the real-node probes alone cannot see the
// `geom_rot_mat` factor (verified by mutation -- replacing it with the identity left every
// `F <fi> 8` record unchanged). The sweep exercises that factor, the mirrored (`det < 0`) and
// singular (`det == 0`) branches of `ufbx_matrix_for_normals()`, and the arithmetic of
// `ufbx_transform_to_matrix()` away from the identity quaternion. All three are pure add/mul, so the
// records stay CRT-independent.

static const ufbx_quat k_geom_rots[] = {
	{ .w =  1.0, .x =  0.0, .y =  0.0, .z =  0.0 },
	{ .w =  0.5, .x =  0.5, .y =  0.5, .z =  0.5 },
	{ .w =  0.5, .x = -0.5, .y =  0.5, .z = -0.5 },
	{ .w =  0.0, .x =  1.0, .y =  0.0, .z =  0.0 },
	{ .w =  0.7071067811865476, .x =  0.7071067811865476, .y =  0.0, .z =  0.0 },
	{ .w =  0.5773502691896258, .x =  0.5773502691896258, .y =  0.5773502691896258, .z =  0.5773502691896258 },
	{ .w =  0.1, .x = -0.2, .y =  0.3, .z =  0.4 },
};

static const ufbx_matrix k_node_to_world[] = {
	{ .m00 =  1.0, .m10 =  0.0, .m20 =  0.0,
	  .m01 =  0.0, .m11 =  1.0, .m21 =  0.0,
	  .m02 =  0.0, .m12 =  0.0, .m22 =  1.0,
	  .m03 =  0.0, .m13 =  0.0, .m23 =  0.0 },
	{ .m00 =  0.6, .m10 =  0.8, .m20 =  0.0,
	  .m01 = -0.8, .m11 =  0.6, .m21 =  0.0,
	  .m02 =  0.0, .m12 =  0.0, .m22 =  2.0,
	  .m03 =  1.5, .m13 = -2.5, .m23 =  0.25 },
	{ .m00 = -1.0, .m10 =  0.0, .m20 =  0.0,
	  .m01 =  0.0, .m11 =  1.0, .m21 =  0.0,
	  .m02 =  0.0, .m12 =  0.0, .m22 =  1.0,
	  .m03 =  3.0, .m13 =  0.0, .m23 = -4.0 },
	{ .m00 =  0.0, .m10 =  0.0, .m20 =  0.0,
	  .m01 =  0.0, .m11 =  0.0, .m21 =  0.0,
	  .m02 =  0.0, .m12 =  0.0, .m22 =  0.0,
	  .m03 =  1.0, .m13 =  2.0, .m23 =  3.0 },
};

static void dump_scene_find(int fi, const ufbx_scene *scene)
{
	size_t total;

	// -- part 1: look up every element by its own name, under its own type and under the next
	// type (which must not match), and through the three typed shorthands.
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t ei = 0; ei < scene->elements_by_type[type].count; ei++) {
			ufbx_element *elem = scene->elements_by_type[type].data[ei];
			size_t other = (type + 1 == UFBX_ELEMENT_TYPE_COUNT) ? 0 : type + 1;
			hh_u32(elem->element_id);
			for (size_t mode = 0; mode < 3; mode++) {
				size_t len = prefix_mode_len(elem->name.length, mode);
				hh_element(ufbx_find_element_len(scene, (ufbx_element_type) type, elem->name.data, len));
				hh_element(ufbx_find_element_len(scene, (ufbx_element_type) other, elem->name.data, len));
				total += 2;
			}
			ufbx_node *node = ufbx_find_node_len(scene, elem->name.data, elem->name.length);
			hh_element(node ? &node->element : NULL);
			ufbx_material *mat = ufbx_find_material_len(scene, elem->name.data, elem->name.length);
			hh_element(mat ? &mat->element : NULL);
			ufbx_anim_stack *stack = ufbx_find_anim_stack_len(scene, elem->name.data, elem->name.length);
			hh_element(stack ? &stack->element : NULL);
			ufbx_node *node2 = ufbx_find_node(scene, elem->name.data);
			hh_element(node2 ? &node2->element : NULL);
			total += 4;
		}
	}
	printf("F %d 1 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 2: the miss sweep, over every element type, in both the `_len` and `strlen` forms.
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t n = 0; n < ufbxi_arraycount(k_miss_names); n++) {
			const char *name = k_miss_names[n];
			hh_element(ufbx_find_element_len(scene, (ufbx_element_type) type, name, strlen(name)));
			hh_element(ufbx_find_element(scene, (ufbx_element_type) type, name));
			total += 2;
		}
	}
	printf("F %d 2 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 3: connected elements, by prop name (`ufbx_find_prop_element_len`) and by prop
	// pointer (`ufbx_get_prop_element`), which is the `prop->name.data` path.
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t ei = 0; ei < scene->elements_by_type[type].count; ei++) {
			ufbx_element *elem = scene->elements_by_type[type].data[ei];
			for (size_t pi = 0; pi < elem->props.props.count; pi++) {
				ufbx_prop *p = &elem->props.props.data[pi];
				hh_u32(elem->element_id);
				hh_u32((uint32_t) pi);
				for (size_t ct = 0; ct < ufbxi_arraycount(k_conn_types); ct++) {
					hh_element(ufbx_find_prop_element_len(elem, p->name.data, p->name.length, k_conn_types[ct]));
					hh_element(ufbx_get_prop_element(elem, p, k_conn_types[ct]));
					total += 2;
				}
				hh_element(ufbx_find_prop_element(elem, p->name.data, UFBX_ELEMENT_TEXTURE));
				total++;
			}
		}
	}
	printf("F %d 3 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 4: animated properties of a layer. Each of the layer's own props is probed at the
	// exact name, one byte short, empty, and against a foreign element (the `element != key`
	// branch of the comparator), plus the range form.
	total = 0;
	g_hash = FNV_BASIS;
	const ufbx_element *foreign = scene->elements.count > 0 ? scene->elements.data[0] : NULL;
	for (size_t li = 0; li < scene->anim_layers.count; li++) {
		ufbx_anim_layer *layer = scene->anim_layers.data[li];
		hh_u32(layer->element.element_id);
		for (size_t a = 0; a < layer->anim_props.count; a++) {
			ufbx_anim_prop *ap = &layer->anim_props.data[a];
			ufbx_element *elem = ap->element;
			hh_u32(elem->element_id);
			hh_u32((uint32_t) a);
			for (size_t mode = 0; mode < 3; mode++) {
				size_t len = prefix_mode_len(ap->prop_name.length, mode);
				hh_aprop(ufbx_find_anim_prop_len(layer, elem, ap->prop_name.data, len));
				total++;
			}
			hh_aprop(ufbx_find_anim_prop(layer, elem, ap->prop_name.data));
			total++;
			if (foreign && foreign != elem) {
				hh_aprop(ufbx_find_anim_prop_len(layer, foreign, ap->prop_name.data, ap->prop_name.length));
				total++;
			}
			hh_aprop_list(ufbx_find_anim_props(layer, elem));
			total++;
		}
		if (foreign) {
			hh_aprop_list(ufbx_find_anim_props(layer, foreign));
			total++;
		}
	}
	printf("F %d 4 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 5: material texture and shader property bindings.
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t mi = 0; mi < scene->materials.count; mi++) {
		ufbx_material *mat = scene->materials.data[mi];
		hh_u32(mat->element.element_id);
		for (size_t t = 0; t < mat->textures.count; t++) {
			ufbx_material_texture *mt = &mat->textures.data[t];
			hh_element((const ufbx_element*) ufbx_find_prop_texture_len(mat, mt->material_prop.data, mt->material_prop.length));
			hh_element((const ufbx_element*) ufbx_find_prop_texture_len(mat, mt->material_prop.data,
				prefix_mode_len(mt->material_prop.length, 1)));
			total += 2;
		}
		for (size_t pi = 0; pi < mat->element.props.props.count; pi++) {
			ufbx_prop *p = &mat->element.props.props.data[pi];
			hh_element((const ufbx_element*) ufbx_find_prop_texture_len(mat, p->name.data, p->name.length));
			total++;
		}
		for (size_t n = 0; n < ufbxi_arraycount(k_miss_names); n++) {
			hh_element((const ufbx_element*) ufbx_find_prop_texture(mat, k_miss_names[n]));
			total++;
		}
		ufbx_shader *shader = mat->shader;
		if (shader) {
			hh_u32(shader->element.element_id);
			for (size_t b = 0; b < shader->bindings.count; b++) {
				ufbx_shader_binding *bind = shader->bindings.data[b];
				for (size_t pb = 0; pb < bind->prop_bindings.count; pb++) {
					ufbx_shader_prop_binding *pbb = &bind->prop_bindings.data[pb];
					ufbx_shader_prop_binding_list got =
						ufbx_find_shader_prop_bindings_len(shader, pbb->shader_prop.data, pbb->shader_prop.length);
					hh_u64((uint64_t) got.count);
					if (got.count > 0) {
						hash_str(got.data[0].shader_prop);
						hash_str(got.data[0].material_prop);
						hash_str(got.data[got.count - 1].shader_prop);
					}
					hash_str(ufbx_find_shader_prop_len(shader, pbb->shader_prop.data, pbb->shader_prop.length));
					hh_u64((uint64_t) ufbx_find_shader_prop_bindings_len(shader, pbb->shader_prop.data,
						prefix_mode_len(pbb->shader_prop.length, 1)).count);
					total += 3;
				}
			}
			for (size_t n = 0; n < ufbxi_arraycount(k_miss_names); n++) {
				ufbx_shader_prop_binding_list got = ufbx_find_shader_prop_bindings(shader, k_miss_names[n]);
				hh_u64((uint64_t) got.count);
				hash_str(ufbx_find_shader_prop(shader, k_miss_names[n]));
				total += 2;
			}
		}
	}
	printf("F %d 5 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 6: shader texture inputs (the `inputs` list sorted by name, ufbx.c:31471-31484).
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t ti = 0; ti < scene->textures.count; ti++) {
		ufbx_texture *tex = scene->textures.data[ti];
		ufbx_shader_texture *st = tex->shader;
		if (!st) continue;
		hh_u32(tex->element.element_id);
		for (size_t ii = 0; ii < st->inputs.count; ii++) {
			ufbx_shader_texture_input *in = &st->inputs.data[ii];
			ufbx_shader_texture_input *got = ufbx_find_shader_texture_input_len(st, in->name.data, in->name.length);
			hh_u32(got ? 1u : 0u);
			if (got) {
				hash_str(got->name);
				hh_d(got->value_vec4.x); hh_d(got->value_vec4.y);
				hh_d(got->value_vec4.z); hh_d(got->value_vec4.w);
				hh_i64(got->value_int);
				hash_str(got->value_str);
				hh_element((const ufbx_element*) got->texture);
				hh_u32(got->texture_enabled ? 1u : 0u);
			}
			total++;
		}
		hh_u32(ufbx_find_shader_texture_input(st, k_miss_names[4]) ? 1u : 0u);
		total++;
	}
	printf("F %d 6 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 7: bone poses (`ufbx_get_bone_pose`, ufbx.c:31413-31420), including a foreign node.
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t pi = 0; pi < scene->poses.count; pi++) {
		ufbx_pose *pose = scene->poses.data[pi];
		hh_u32(pose->element.element_id);
		for (size_t b = 0; b < pose->bone_poses.count; b++) {
			ufbx_bone_pose *bp = &pose->bone_poses.data[b];
			ufbx_bone_pose *got = ufbx_get_bone_pose(pose, bp->bone_node);
			hh_u32(got ? 1u : 0u);
			if (got) {
				hh_u32(got->bone_node->element.element_id);
				hh_u32(got->bone_node->typed_id);
			}
			total++;
		}
		if (scene->nodes.count > 0) {
			ufbx_bone_pose *got = ufbx_get_bone_pose(pose, scene->nodes.data[scene->nodes.count - 1]);
			hh_u32(got ? 1u : 0u);
			if (got) hh_u32(got->bone_node->element.element_id);
			total++;
		}
	}
	printf("F %d 7 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 8: `ufbx_get_compatible_matrix_for_normals` (ufbx.c:30822-30833) over every node,
	// plus the NULL-node identity form. Pure arithmetic (no libm), so it is CRT-independent.
	total = 0;
	g_hash = FNV_BASIS;
	ufbx_matrix identity = ufbx_get_compatible_matrix_for_normals(NULL);
	hash_matrix(&identity);
	total++;
	for (size_t ni = 0; ni < scene->nodes.count; ni++) {
		ufbx_matrix m = ufbx_get_compatible_matrix_for_normals(scene->nodes.data[ni]);
		hash_matrix(&m);
		total++;
	}
	for (size_t mi = 0; mi < ufbxi_arraycount(k_node_to_world); mi++) {
		for (size_t qi = 0; qi < ufbxi_arraycount(k_geom_rots); qi++) {
			ufbx_node n;
			memset(&n, 0, sizeof(n));
			n.node_to_world = k_node_to_world[mi];
			n.geometry_transform.rotation = k_geom_rots[qi];
			ufbx_matrix m = ufbx_get_compatible_matrix_for_normals(&n);
			hash_matrix(&m);
			total++;
		}
	}
	printf("F %d 8 ", fi); hbits(hash_take()); printf(" %zu\n", total);

	// -- part 9: `ufbx_find_face_index` (ufbx.c:32389-32398). Every face of every mesh is probed at
	// its first index, at the end index of the previous face (the boundary that must resolve to
	// this face, not the previous one), at the one-past-its-own-end index (which must resolve to the
	// *next* face), at its middle, and at the one-past-the-whole-mesh index. The three out-of-range
	// probes (`UINT32_MAX`, `UINT32_MAX + 1` and `(size_t) -1` == `SIZE_MAX`) take the early-return
	// branch, and the NULL-mesh probe the other half of the same guard.
	total = 0;
	g_hash = FNV_BASIS;
	for (size_t mi = 0; mi < scene->meshes.count; mi++) {
		ufbx_mesh *mesh = scene->meshes.data[mi];
		hh_u32(mesh->element.element_id);
		hh_u32((uint32_t) mesh->num_faces);
		hh_u32((uint32_t) mesh->num_indices);
		for (size_t f = 0; f < mesh->faces.count; f++) {
			ufbx_face *face = &mesh->faces.data[f];
			const size_t begin = face->index_begin;
			const size_t end = (size_t) face->index_begin + face->num_indices;
			hh_u32(ufbx_find_face_index(mesh, begin));
			hh_u32(ufbx_find_face_index(mesh, begin + face->num_indices / 2));
			hh_u32(ufbx_find_face_index(mesh, end));
			hh_u32(ufbx_find_face_index(mesh, end + 1));
			hh_u32(ufbx_find_face_index(mesh, begin > 0 ? begin - 1 : begin));
			total += 5;
		}
		hh_u32(ufbx_find_face_index(mesh, (size_t) mesh->num_indices));
		hh_u32(ufbx_find_face_index(mesh, (size_t) mesh->num_indices + 1));
		hh_u32(ufbx_find_face_index(mesh, (size_t) 0xFFFFFFFFu));
		hh_u32(ufbx_find_face_index(mesh, (size_t) 0xFFFFFFFFu + (size_t) 1));
		hh_u32(ufbx_find_face_index(mesh, (size_t) -1));
		hh_u32(ufbx_find_face_index(NULL, 0));
		total += 6;
	}
	printf("F %d 9 ", fi); hbits(hash_take()); printf(" %zu\n", total);
}

// =====================================================================
// Vertex attribute accessors: ufbx_catch_get_vertex_real/vec2/vec3/vec4 (ufbx.c:33001-33031),
// ufbx_catch_get_vertex_w_vec3 (ufbx.c:33033) and the plain inline forms (ufbx.h:5763-5769).
// =====================================================================

// Every read is folded together with its `ufbx_panic` state (flag, length and message bytes), and
// each attribute is read once past its end (`index == indices.count`), which is the out-of-range
// probe. No other record kind reaches `ufbxi_panicf`, so this is the differential control for the
// port's panic plumbing (`ufbxi_vprint`/`ufbxi_vsnprintf` included) as well as for the indexing.
static size_t cstr_len(const char *s, size_t max) {
	size_t n = 0;
	while (n < max && s[n]) n++;
	return n;
}

static void hash_panic(const ufbx_panic *p) {
	hh_u32(p->did_panic ? 1u : 0u);
	hh_u64((uint64_t) p->message_length);
	hh_bytes(p->message, cstr_len(p->message, sizeof(p->message)));
}

static size_t acc_real(const ufbx_vertex_real *v, int plain) {
	for (size_t i = 0; i <= v->indices.count; i++) {
		ufbx_panic p; memset(&p, 0, sizeof(p));
		hh_d(ufbx_catch_get_vertex_real(&p, v, i));
		hash_panic(&p);
		if (plain && i < v->indices.count) hh_d(ufbx_get_vertex_real(v, i));
	}
	return v->indices.count + 1;
}

static size_t acc_vec2(const ufbx_vertex_vec2 *v, int plain) {
	for (size_t i = 0; i <= v->indices.count; i++) {
		ufbx_panic p; memset(&p, 0, sizeof(p));
		ufbx_vec2 got = ufbx_catch_get_vertex_vec2(&p, v, i);
		hh_d(got.x); hh_d(got.y);
		hash_panic(&p);
		if (plain && i < v->indices.count) {
			ufbx_vec2 got2 = ufbx_get_vertex_vec2(v, i);
			hh_d(got2.x); hh_d(got2.y);
		}
	}
	return v->indices.count + 1;
}

static size_t acc_vec3(const ufbx_vertex_vec3 *v, int plain) {
	for (size_t i = 0; i <= v->indices.count; i++) {
		ufbx_panic p; memset(&p, 0, sizeof(p));
		ufbx_vec3 got = ufbx_catch_get_vertex_vec3(&p, v, i);
		hh_d(got.x); hh_d(got.y); hh_d(got.z);
		hash_panic(&p);
		if (plain && i < v->indices.count) {
			ufbx_vec3 got3 = ufbx_get_vertex_vec3(v, i);
			hh_d(got3.x); hh_d(got3.y); hh_d(got3.z);
		}
	}
	return v->indices.count + 1;
}

static size_t acc_vec4(const ufbx_vertex_vec4 *v, int plain) {
	for (size_t i = 0; i <= v->indices.count; i++) {
		ufbx_panic p; memset(&p, 0, sizeof(p));
		ufbx_vec4 got = ufbx_catch_get_vertex_vec4(&p, v, i);
		hh_d(got.x); hh_d(got.y); hh_d(got.z); hh_d(got.w);
		hash_panic(&p);
		if (plain && i < v->indices.count) {
			ufbx_vec4 got4 = ufbx_get_vertex_vec4(v, i);
			hh_d(got4.x); hh_d(got4.y); hh_d(got4.z); hh_d(got4.w);
		}
	}
	return v->indices.count + 1;
}

// `ufbx_get_vertex_w_vec3()` reads `values_w`, which is only filled when
// `ufbx_load_opts.retain_vertex_attrib_w` is set (ufbx.c:12908-12921) -- the golden opts do not, so
// `X` exercises the `values_w.count == 0` early return plus the index check, while `Y` builds an
// attribute that does carry `values_w`.
static size_t acc_w_vec3(const ufbx_vertex_vec3 *v, int plain) {
	for (size_t i = 0; i <= v->indices.count; i++) {
		ufbx_panic p; memset(&p, 0, sizeof(p));
		hh_d(ufbx_catch_get_vertex_w_vec3(&p, v, i));
		hash_panic(&p);
		if (plain && i < v->indices.count) hh_d(ufbx_get_vertex_w_vec3(v, i));
	}
	return v->indices.count + 1;
}

static void dump_vertex_access(int fi, const ufbx_scene *scene)
{
	size_t total = 0;
	g_hash = FNV_BASIS;
	for (size_t mi = 0; mi < scene->meshes.count; mi++) {
		ufbx_mesh *mesh = scene->meshes.data[mi];
		hh_u32(mesh->element.element_id);
		total += acc_real(&mesh->vertex_crease, 1);
		total += acc_vec2(&mesh->vertex_uv, 1);
		total += acc_vec3(&mesh->vertex_position, 1);
		total += acc_vec3(&mesh->vertex_normal, 1);
		total += acc_vec3(&mesh->vertex_tangent, 1);
		total += acc_vec3(&mesh->vertex_bitangent, 1);
		total += acc_vec4(&mesh->vertex_color, 1);
		total += acc_w_vec3(&mesh->vertex_position, 1);
		total += acc_w_vec3(&mesh->vertex_normal, 1);
	}
	printf("X %d ", fi); hbits(hash_take()); printf(" %zu\n", total);
}

// Synthetic attribute accessor sweep (`Y`): the two branches a loaded scene cannot produce.
//   * an index entry of UFBX_NO_INDEX -- C reads `values.data[-1]`, the zero-guard element ufbx
//     places before guarded arrays (ufbx.c:28003), which the port models by returning zero;
//   * an index that is out of range for `values` but in range for `values_w`, i.e. exactly the
//     list `ufbx_catch_get_vertex_w_vec3` bounds against (ufbx.c:33038 checks `values.count` and
//     then reads `values_w[ix]`).
// Only the catch forms run here: the plain inline accessors have no bounds check at all, so an
// out-of-range read would be undefined in C and is not representable by the port's arrays (whose
// length *is* C's `values.count`). The plain forms over in-range data are covered by `X`.
static void dump_synth_access(int fi)
{
	size_t total = 0;
	g_hash = FNV_BASIS;

	// One extra element at [0] as the UFBX_NO_INDEX guard; `values.data = buf + 1`.
	enum { NumValues = 8 };
	static uint32_t idx[6] = { 0, 2, 7, UFBX_NO_INDEX, 8, 3 };
	ufbx_real rbuf[NumValues + 1];
	ufbx_vec2 v2buf[NumValues + 1];
	ufbx_vec3 v3buf[NumValues + 1];
	ufbx_vec4 v4buf[NumValues + 1];
	ufbx_real wbuf[12 + 1];

	memset(rbuf, 0, sizeof(rbuf));
	memset(v2buf, 0, sizeof(v2buf));
	memset(v3buf, 0, sizeof(v3buf));
	memset(v4buf, 0, sizeof(v4buf));
	memset(wbuf, 0, sizeof(wbuf));
	for (size_t i = 0; i < NumValues; i++) {
		const double d = (double)i * 1.75 - 3.25;
		rbuf[i + 1] = (ufbx_real) d;
		v2buf[i + 1].x = (ufbx_real) d; v2buf[i + 1].y = (ufbx_real) -d;
		v3buf[i + 1].x = (ufbx_real) d;
		v3buf[i + 1].y = (ufbx_real) (d * 0.25);
		v3buf[i + 1].z = (ufbx_real) (1.0 / (1.0 + d));
		v4buf[i + 1].x = (ufbx_real) d;
		v4buf[i + 1].y = (ufbx_real) (d + 1.0);
		v4buf[i + 1].z = (ufbx_real) (d - 1.0);
		v4buf[i + 1].w = (i & 1) ? (ufbx_real) 0.125 : (ufbx_real) -0.25;
	}
	for (size_t i = 0; i < 12; i++) wbuf[i + 1] = (ufbx_real) (100.0 + (double) i);

	ufbx_vertex_real vr; memset(&vr, 0, sizeof(vr));
	vr.exists = true;
	vr.values.data = rbuf + 1; vr.values.count = NumValues;
	vr.indices.data = idx; vr.indices.count = ufbxi_arraycount(idx);
	vr.value_reals = 1;

	ufbx_vertex_vec2 v2; memset(&v2, 0, sizeof(v2));
	v2.exists = true;
	v2.values.data = v2buf + 1; v2.values.count = NumValues;
	v2.indices.data = idx; v2.indices.count = ufbxi_arraycount(idx);
	v2.value_reals = 2;

	ufbx_vertex_vec3 v3; memset(&v3, 0, sizeof(v3));
	v3.exists = true;
	v3.values.data = v3buf + 1; v3.values.count = NumValues;
	v3.values_w.data = wbuf + 1; v3.values_w.count = 12;
	v3.indices.data = idx; v3.indices.count = ufbxi_arraycount(idx);
	v3.value_reals = 3;

	ufbx_vertex_vec4 v4; memset(&v4, 0, sizeof(v4));
	v4.exists = true;
	v4.values.data = v4buf + 1; v4.values.count = NumValues;
	v4.indices.data = idx; v4.indices.count = ufbxi_arraycount(idx);
	v4.value_reals = 4;

	total += acc_real(&vr, 0);
	total += acc_vec2(&v2, 0);
	total += acc_vec3(&v3, 0);
	total += acc_vec4(&v4, 0);
	total += acc_w_vec3(&v3, 0);

	printf("Y %d ", fi); hbits(hash_take()); printf(" %zu\n", total);
}

// Scene-state hashes (`V`): the golden computation of tools/gen_golden.sh, one record per
// (file, frame). `test/hash_scene.c:127-141` evaluates for `frame > 0` only, with frames `i*i` for
// i = 1..9, NULL opts (so no skinning, no caches, no evaluate flags) and
// `time = anim->time_begin + frame / settings.frames_per_second`, then hashes the *evaluated* scene
// with `ufbxt_hash_scene()`. The emitted hash is therefore the number stored in
// tools/golden_hashes.txt for that (file, frame) -- so `V` both gates the port's evaluate pass and
// proves this oracle is built in the golden generator's configuration.
static void dump_scene_hash(int fi, ufbx_scene *scene)
{
	if (!scene->anim) return;
	const double fps = scene->settings.frames_per_second;
	const double tb = scene->anim->time_begin;
	if (!(fps > 0.0)) return;

	for (int i = 1; i <= 9; i++) {
		const int frame = i * i;
		const double time = tb + (double) frame / fps;
		ufbx_error error;
		ufbx_scene *state = ufbx_evaluate_scene(scene, NULL, time, NULL, &error);
		if (!state) {
			printf("V %d %d ERR %d\n", fi, frame, (int) error.type);
			continue;
		}
		printf("V %d %d ", fi, frame); hbits(ufbxt_hash_scene(state, NULL)); printf("\n");
		ufbx_free_scene(state);
	}
}

int main(int argc, char **argv)
{
	int first_file_arg = 1;
	for (int i = 1; i < argc; i++) {
		if (!strcmp(argv[i], "-v")) {
			// -v <file-index> [<time-index> [<kind>]]: dump explicit records instead of (on top
			// of) the hashed rollups, for localising a mismatch found by S4bCheck.
			g_verbose = 1;
			if (i + 1 < argc) g_verbose_file = atoi(argv[++i]);
			if (i + 1 < argc) g_verbose_time = atoi(argv[++i]);
			if (i + 1 < argc) g_verbose_kind = argv[++i][0];
			first_file_arg = i + 1;
			break;
		}
	}

	int fi = 0;
	for (int i = first_file_arg; i < argc; i++) {
		ufbx_error error;
		ufbx_scene *scene = load_golden_scene(argv[i], &error);
		if (!scene) {
			printf("S %d 0 0 0 0 0 0 0 0\n", fi);
			fprintf(stderr, "load failed: %s: %s\n", argv[i], error.description.data);
			fi++;
			continue;
		}

		size_t num_elems = 0;
		for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
			num_elems += scene->elements_by_type[type].count;
		}

		printf("S %d %zu %zu %zu %zu %zu %zu %zu %d\n", fi,
			num_elems, scene->nodes.count, scene->anim_curves.count,
			scene->anim_values.count, scene->blend_channels.count, scene->anim_layers.count,
			scene->anim ? scene->anim->prop_overrides.count : 0,
			scene->anim ? 1 : 0);

		static double times[128];
		size_t num_times = build_times(scene, times, ufbxi_arraycount(times));
		if (num_times == 0) { printf("S %d empty_times\n", fi); ufbx_free_scene(scene); fi++; continue; }

		for (size_t t = 0; t < num_times; t++) {
			printf("T %d %zu ", fi, t); dbits(times[t]); printf("\n");
		}

		dump_find(fi, scene);
		dump_vertex_access(fi, scene);
		dump_synth_access(fi);
		dump_scene_find(fi, scene);
		for (size_t t = 0; t < num_times; t++) {
			dump_props(fi, (int)t, 0, scene, times[t], 0);
			dump_props(fi, (int)t, 1, scene, times[t], UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION);
			dump_transforms(fi, (int)t, scene, times[t]);
			dump_blend_weights(fi, (int)t, scene, times[t]);
			dump_curves(fi, (int)t, scene, times[t]);
			dump_layers(fi, (int)t, scene, times[t]);
		}

		dump_scene_hash(fi, scene);

		// Source-scene integrity after the evaluation sweep (`W`): `ufbxi_evaluate_imp()` only ever
		// writes into freshly pushed memory, so the source scene still hashes to its frame-0 golden
		// here. A port that shares inline material/map/feature storage with its clones corrupts the
		// source during `ufbxi_fetch_maps()` and fails this line while still passing `V` -- the
		// mutation control that makes the aliasing rules of the clone visible.
		printf("W %d ", fi); hbits(ufbxt_hash_scene(scene, NULL)); printf("\n");

		ufbx_free_scene(scene);
		fi++;
	}

	printf("DONE %d 0\n", fi);
	return 0;
}
