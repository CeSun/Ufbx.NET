// One-off probe for the S4b-1 single-ULP divergence: re-runs the body of
// ufbxi_evaluate_props() with a printf per (layer, prop) step for one element, using the very
// same internal helpers the real code calls, so the trace is authoritative.
//
// Build:
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
//       tools/_s4b_trace.c -o tools/_s4b_trace.exe
// Run (from C:/Workspace/_analyze_ufbx):
//   tools/_s4b_trace.exe data/maya_anim_layers_acc_7500_binary.fbx <element_id> <time>

#include "ufbx.c"

static void dbits(double v) { uint64_t u; memcpy(&u, &v, sizeof(u)); printf("%016llx", (unsigned long long)u); }

int main(int argc, char **argv)
{
	uint32_t want = (uint32_t)strtoul(argv[2], NULL, 10);
	double time = strtod(argv[3], NULL);

	ufbx_load_opts opts = { 0 };
	opts.load_external_files = true;
	opts.ignore_missing_external_files = true;
	opts.evaluate_caches = true;
	opts.evaluate_skinning = true;
	opts.target_axes = ufbx_axes_right_handed_y_up;
	opts.target_unit_meters = 1.0f;

	ufbx_scene *scene = ufbx_load_file(argv[1], &opts, NULL);
	if (!scene) return 1;
	ufbx_anim *anim = scene->anim;

	ufbx_element *elem = NULL;
	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t i = 0; i < scene->elements_by_type[type].count; i++) {
			if (scene->elements_by_type[type].data[i]->element_id == want) elem = scene->elements_by_type[type].data[i];
		}
	}
	if (!elem) { printf("no element %u\n", want); return 1; }

	printf("TIME "); dbits(time); printf(" elem %u\n", want);
	printf("E %u props=%zu\n", want, elem->props.props.count);
	for (size_t p = 0; p < elem->props.props.count; p++) {
		ufbx_prop *pr = &elem->props.props.data[p];
		printf("  p%zu \"%s\" key=%08x flags=%08x", p, pr->name.data, pr->_internal_key, (uint32_t)pr->flags);
		printf(" v=("); dbits(pr->value_vec4.x); printf(","); dbits(pr->value_vec4.y); printf(","); dbits(pr->value_vec4.z); printf(","); dbits(pr->value_vec4.w); printf(")\n");
	}

	printf("layers=%zu\n", anim->layers.count);
	for (size_t li = 0; li < anim->layers.count; li++) {
		ufbx_anim_layer *layer = anim->layers.data[li];
		printf("L%zu id=%u weight=", li, layer->element.element_id); dbits(layer->weight);
		printf(" anim_w=%d blended=%d additive=%d c_scale=%d c_rot=%d min=%u max=%u props=%zu contains=%d\n",
			(int)layer->weight_is_animated, (int)layer->blended, (int)layer->additive,
			(int)layer->compose_scale, (int)layer->compose_rotation,
			layer->_min_element_id, layer->_max_element_id, layer->anim_props.count,
			(int)ufbxi_anim_layer_might_contain_id(layer, want));
		for (size_t a = 0; a < layer->anim_props.count; a++) {
			ufbx_anim_prop *ap = &layer->anim_props.data[a];
			printf("   a%zu elem=%u ptr=%p key=%08x name=\"%s\"\n", a, ap->element->element_id,
				(void*)ap->element, ap->_internal_key, ap->prop_name.data);
		}
		ufbx_anim_prop *start = ufbxi_find_anim_prop_start(layer, elem);
		printf("   start=%td\n", start ? start - layer->anim_props.data : (ptrdiff_t)-1);
	}

	// Rebuild the buffer exactly like ufbx_evaluate_props_flags() (ufbx.c:31011-31023), but stop
	// before ufbxi_evaluate_props() so the trace below owns the accumulation.
	ufbx_prop tbuf[512];
	size_t num_anim = 0;
	{
		ufbx_prop buf[512];
		ufbx_props ev = ufbx_evaluate_props_flags(anim, elem, time, buf, 512, 0);
		printf("evaluated=%zu\n", ev.props.count);
		for (size_t i = 0; i < ev.props.count; i++) {
			ufbx_prop *pr = &ev.props.data[i];
			printf("  e%zu \"%s\" v=(", i, pr->name.data);
			dbits(pr->value_vec4.x); printf(","); dbits(pr->value_vec4.y); printf(","); dbits(pr->value_vec4.z); printf(","); dbits(pr->value_vec4.w); printf(")\n");
		}

		ufbxi_prop_iter iter;
		ufbxi_init_prop_iter(&iter, anim, elem);
		const ufbx_prop *prop = NULL;
		while ((prop = ufbxi_next_prop(&iter)) != NULL) {
			if (!(prop->flags & (UFBX_PROP_FLAG_ANIMATED|UFBX_PROP_FLAG_OVERRIDDEN|UFBX_PROP_FLAG_CONNECTED))) continue;
			if (num_anim >= 512) break;
			ufbx_prop *dst = &tbuf[num_anim++];
			*dst = *prop;
			if ((prop->flags & UFBX_PROP_FLAG_CONNECTED) != 0 && !anim->ignore_connections) {
				ufbxi_evaluate_connected_prop(dst, anim, elem, prop->name.data, time, 0);
			}
		}
	}

	for (size_t li = 0; li < anim->layers.count; li++) {
		ufbx_anim_layer *layer = anim->layers.data[li];
		if (!ufbxi_anim_layer_might_contain_id(layer, want)) { printf("T L%zu skip-id\n", li); continue; }
		ufbx_real weight = li < anim->override_layer_weights.count ? anim->override_layer_weights.data[li] : layer->weight;
		if (layer->weight_is_animated && layer->blended) {
			ufbx_anim_prop *wap = ufbxi_find_anim_prop_start(layer, &layer->element);
			if (wap) {
				weight = ufbx_evaluate_anim_value_real_flags(wap->anim_value, time, 0) / (ufbx_real)100.0;
				if (weight < 0.0f) weight = 0.0f;
				if (weight > 0.99999f) weight = 1.0f;
			}
		}
		ufbx_anim_prop *aprop = ufbxi_find_anim_prop_start(layer, elem);
		if (!aprop) { printf("T L%zu w=", li); dbits(weight); printf(" no-aprop\n"); continue; }
		printf("T L%zu w=", li); dbits(weight); printf(" start=%td\n", aprop - layer->anim_props.data);
		for (size_t i = 0; i < num_anim; i++) {
			ufbx_prop *prop = &tbuf[i];
			if ((prop->flags & UFBX_PROP_FLAG_OVERRIDDEN) != 0) continue;
			if ((prop->flags & UFBX_PROP_FLAG_CONNECTED) != 0 && !anim->ignore_connections) continue;
			while (aprop->element == elem && aprop->_internal_key < prop->_internal_key) aprop++;
			if (aprop->prop_name.data != prop->name.data) {
				while (aprop->element == elem && strcmp(aprop->prop_name.data, prop->name.data) < 0) aprop++;
			}
			ptrdiff_t off = aprop - layer->anim_props.data;
			bool same_elem = aprop->element == elem;
			printf("   p%zu \"%s\" key=%08x -> aprop=%td same_elem=%d aname=\"%s\" match=%d",
				i, prop->name.data, prop->_internal_key, off, (int)same_elem,
				aprop->prop_name.data ? aprop->prop_name.data : (char*)0,
				(int)(aprop->prop_name.data == prop->name.data));
			if (aprop->prop_name.data == prop->name.data) {
				ufbx_vec3 v = ufbx_evaluate_anim_value_vec3_flags(aprop->anim_value, time, 0);
				printf(" v=("); dbits(v.x); printf(","); dbits(v.y); printf(","); dbits(v.z);
				printf(") before=("); dbits(prop->value_vec3.x); printf(","); dbits(prop->value_vec3.y); printf(","); dbits(prop->value_vec3.z); printf(")");
				if (li == 0) prop->value_vec3 = v;
				else {
					ufbxi_anim_layer_combine_ctx ctx; memset(&ctx, 0, sizeof(ctx));
					ctx.anim = anim; ctx.element = elem; ctx.time = time;
					if (strcmp(prop->name.data, ufbxi_Lcl_Rotation) == 0 &&
						(layer->additive || layer->blended) && layer->compose_rotation) {
						ufbx_rotation_order ro = UFBX_ROTATION_ORDER_XYZ;
						ufbx_quat a = ufbx_euler_to_quat(prop->value_vec3, ro);
						ufbx_quat b = ufbx_euler_to_quat(v, ro);
						printf(" q a=("); dbits(a.w); printf(","); dbits(a.x); printf(","); dbits(a.y); printf(","); dbits(a.z);
						printf(") b=("); dbits(b.w); printf(","); dbits(b.x); printf(","); dbits(b.y); printf(","); dbits(b.z); printf(")");
						if (layer->additive) {
							ufbx_quat bs = ufbx_quat_slerp(ufbx_identity_quat, b, weight);
							ufbx_quat res = ufbxi_mul_quat(a, bs);
							printf(" bs=("); dbits(bs.w); printf(","); dbits(bs.x); printf(","); dbits(bs.y); printf(","); dbits(bs.z);
							printf(") res=("); dbits(res.w); printf(","); dbits(res.x); printf(","); dbits(res.y); printf(","); dbits(res.z);
							printf(")");
							{
								// Inline the XYZ branch of ufbx_quat_to_euler() so the atan2
								// arguments are visible next to the results.
								double qw = res.w, qx = res.x, qy = res.y, qz = res.z;
								double tt = 2.0 * (qw*qy - qx*qz);
								double az_y = 2.0*(qw*qz + qx*qy), az_x = 2.0*(qw*qw + qx*qx) - 1.0;
								double ax_y = -2.0*(qw*qx + qy*qz), ax_x = 2.0*(qw*qw + qz*qz) - 1.0;
								printf(" args t="); dbits(tt);
								printf(" az=("); dbits(az_y); printf(","); dbits(az_x);
								printf(") ax=("); dbits(ax_y); printf(","); dbits(ax_x); printf(")");
								// `ufbx_atan2`/`ufbx_asin` are the macros ufbx actually calls; the
								// plain `atan2`/`asin` are the CRT's, which is a different build.
								printf(" c_atan2_az="); dbits(ufbx_atan2(az_y, az_x));
								printf(" c_atan2_ax="); dbits(ufbx_atan2(ax_y, ax_x));
								printf(" c_asin_t="); dbits(ufbx_asin(tt));
								printf(" crt_atan2_az="); dbits(atan2(az_y, az_x));
								printf(" crt_asin_t="); dbits(asin(tt));
							}
							printf(" e=("); ufbx_vec3 e = ufbx_quat_to_euler(res, ro);
							dbits(e.x); printf(","); dbits(e.y); printf(","); dbits(e.z); printf(")");
						}
					}
					ufbxi_combine_anim_layer(&ctx, layer, weight, prop->name.data, &prop->value_vec3, &v);
					printf(" after=("); dbits(prop->value_vec3.x); printf(","); dbits(prop->value_vec3.y); printf(","); dbits(prop->value_vec3.z); printf(")");
				}
			}
			printf("\n");
		}
	}

	printf("trace-final\n");	for (size_t i = 0; i < num_anim; i++) {
		ufbx_prop *pr = &tbuf[i];
		printf("  t%zu \"%s\" v=(", i, pr->name.data);
		dbits(pr->value_vec4.x); printf(","); dbits(pr->value_vec4.y); printf(","); dbits(pr->value_vec4.z); printf(","); dbits(pr->value_vec4.w); printf(")\n");
	}
	ufbx_free_scene(scene);
	return 0;
}
