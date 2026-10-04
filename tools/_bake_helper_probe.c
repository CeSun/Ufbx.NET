// Batch J coverage probe: which animated files in the corpus actually contain the
// scale-helper machinery of ufbxi_bake_node_imp (ufbx.c:27280-27292, 27335-27348, 27477-27495)?
// Prints one line per file:
//   H <name> <anim> <layers> <animNodes> <helpers> <childrenOfHelper> <helperWithInheritParent>
//      <tLive> <tElse> <tAnim> <sLive> <sElse> <sAnim>
// where:
//   helpers               = nodes with is_scale_helper
//   childrenOfHelper      = non-helper nodes whose parent->scale_helper != NULL  (the R1 branch)
//   helperWithInheritParent = helpers whose parent->inherit_scale_node->scale_helper != NULL (R2)
//   tLive/tAnim           = of those, how many have a helper baked *before* them (typed_id order),
//                           and how many of those helpers are themselves animated -- i.e. the
//                           `helperIx >= 0` half of the translation block and its
//                           `!scaleHelperT->constant_scale` sub-branch
//   tElse                 = the complementary `helperIx < 0` half (`constant_scale_t =
//                           parent->scale_helper->inherit_scale`)
//   sLive/sElse/sAnim     = the same three for the scale block's `is_scale_helper` chain
//   tSAnim/sSAnim         = subset of tLive/sLive whose helper is animated on `Lcl Scaling`
//                           specifically -- the only data that can take `!constant_scale`
// `ufbxi_bake_anim()` bakes nodes in ascending `typed_id` order (props sorted by `sort_id`), so
// `typed_id` is the dominant term of the bake order; the sibling re-bake stack can only move a
// helper *earlier*, so `tElse`/`sElse` are an upper bound on that branch, not a proof.
// Build (zig only, per the project rule):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
//       C:/Workspace/ufbx-cs/tools/_bake_helper_probe.c -o C:/Workspace/ufbx-cs/tools/_bake_helper_probe.exe
#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static bool node_is_animated(const ufbx_scene *scene, const ufbx_node *node)
{
	if (!scene->anim) return false;
	for (size_t l = 0; l < scene->anim->layers.count; l++) {
		ufbx_anim_layer *layer = scene->anim->layers.data[l];
		if (!layer) continue;
		for (size_t p = 0; p < layer->anim_props.count; p++) {
			const ufbx_anim_prop *ap = &layer->anim_props.data[p];
			if (ap->element == (const ufbx_element*)node) return true;
		}
	}
	return false;
}

// The stricter predicate: animated *and* animated on `Lcl Scaling`, which is what makes
// `ufbxi_bake_node_imp` take the `!scale_helper_t->constant_scale` branch (resample the child's
// translation on the helper's own scale curve).
static bool prop_name_is(ufbx_string name, const char *lit)
{
	size_t n = strlen(lit);
	return name.data != NULL && name.length == n && memcmp(name.data, lit, n) == 0;
}

static bool node_is_scale_animated(const ufbx_scene *scene, const ufbx_node *node)
{
	if (!scene->anim) return false;
	for (size_t l = 0; l < scene->anim->layers.count; l++) {
		ufbx_anim_layer *layer = scene->anim->layers.data[l];
		if (!layer) continue;
		for (size_t p = 0; p < layer->anim_props.count; p++) {
			const ufbx_anim_prop *ap = &layer->anim_props.data[p];
			if (ap->element != (const ufbx_element*)node) continue;
			if (prop_name_is(ap->prop_name, "Lcl Scaling")) return true;
		}
	}
	return false;
}

static uint64_t d2b(double v)
{
	uint64_t u;
	memcpy(&u, &v, 8);
	return u;
}

static ufbx_scene *load_golden_scene_mode(const char *filename, int mode, ufbx_error *error)
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
	opts.inherit_mode_handling = (ufbx_inherit_mode_handling)mode;
	return ufbx_load_file(filename, &opts, error);
}

int main(int argc, char **argv)
{
	int mode = 0;
	int first = 1;
	if (argc > 1 && strncmp(argv[1], "--mode=", 7) == 0) {
		mode = atoi(argv[1] + 7);
		first = 2;
	}

	int dump_props = getenv("DUMP_PROPS") != NULL;
	for (int fi = first; fi < argc; fi++) {
		ufbx_error error;
		ufbx_scene *scene = load_golden_scene_mode(argv[fi], mode, &error);
		if (!scene) {
			printf("H %s loadfail %d\n", argv[fi], (int)error.type);
			continue;
		}

		size_t helpers = 0, children_of_helper = 0, helper_inherit_parent = 0, anim_nodes = 0;
		size_t t_live = 0, t_else = 0, t_anim = 0, s_live = 0, s_else = 0, s_anim = 0;
		size_t t_sanim = 0, s_sanim = 0;

		// DUMP_PROPS=1: the `constant_scale` input of the scale-helper decision, straight from the
		// element props the way ufbx.c:18180 reads them (`ufbxi_find_vec3(&element->props, ...)`).
		if (dump_props) {
			for (size_t i = 0; i < scene->nodes.count; i++) {
				ufbx_node *n = scene->nodes.data[i];
				if (!n) continue;
				ufbx_prop *ep = ufbx_find_prop(&n->element.props, "Lcl Scaling");
				printf("P %u %u parent=%d inherit=%d helper=%d elem=%d",
					n->element.typed_id, n->element.element_id,
					n->parent ? (int)n->parent->element.typed_id : -1,
					(int)n->original_inherit_mode, n->is_scale_helper ? 1 : 0,
					ep ? 1 : 0);
				if (ep) printf(" e=%016llx,%016llx,%016llx",
					(unsigned long long)d2b(ep->value_vec3.x),
					(unsigned long long)d2b(ep->value_vec3.y),
					(unsigned long long)d2b(ep->value_vec3.z));
				printf(" name=%s\n", n->name.data ? n->name.data : "");
			}
		}
		for (size_t i = 0; i < scene->nodes.count; i++) {
			ufbx_node *n = scene->nodes.data[i];
			if (!n) continue;
			if (n->is_scale_helper) helpers++;
			if (!n->is_scale_helper && n->parent && n->parent->scale_helper) {
				children_of_helper++;
				ufbx_node *h = n->parent->scale_helper;
				if (h->typed_id < n->typed_id) {
					t_live++;
					if (node_is_animated(scene, h)) t_anim++;
					if (node_is_scale_animated(scene, h)) t_sanim++;
				} else {
					t_else++;
				}
			}
			if (n->is_scale_helper && n->parent && n->parent->inherit_scale_node &&
				n->parent->inherit_scale_node->scale_helper) {
				helper_inherit_parent++;
				ufbx_node *h = n->parent->inherit_scale_node->scale_helper;
				if (h->typed_id < n->typed_id) {
					s_live++;
					if (node_is_animated(scene, h)) s_anim++;
					if (node_is_scale_animated(scene, h)) s_sanim++;
				} else {
					s_else++;
				}
			}
		}

		// Animated nodes, straight from the anim layers -- these are what ufbxi_bake_anim() bakes.
		if (scene->anim) {
			for (size_t l = 0; l < scene->anim->layers.count; l++) {
				ufbx_anim_layer *layer = scene->anim->layers.data[l];
				if (!layer) continue;
				for (size_t p = 0; p < layer->anim_props.count; p++) {
					const ufbx_anim_prop *ap = &layer->anim_props.data[p];
					if (!ap->element) continue;
					if (ap->element->type == UFBX_ELEMENT_NODE) { anim_nodes++; break; }
				}
			}
		}

		printf("H %s anim=%d layers=%zu animNodes=%zu helpers=%zu childrenOfHelper=%zu helperInheritParent=%zu tLive=%zu tElse=%zu tAnim=%zu tSAnim=%zu sLive=%zu sElse=%zu sAnim=%zu sSAnim=%zu\n",
			argv[fi], scene->anim ? 1 : 0,
			scene->anim ? scene->anim->layers.count : (size_t)0,
			anim_nodes, helpers, children_of_helper, helper_inherit_parent,
			t_live, t_else, t_anim, t_sanim, s_live, s_else, s_anim, s_sanim);

		ufbx_free_scene(scene);
	}
	return 0;
}
