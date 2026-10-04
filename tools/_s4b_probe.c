// One-off probe: dump every animated prop of every element in file 3 (maya_anim_layers_acc)
// together with its connection/override flags, so the single-ULP P/Q/R divergence in the S4b
// differential can be narrowed to one prop + layer.
#include "ufbx.c"

int main(int argc, char **argv)
{
	ufbx_load_opts opts;
	ufbx_load_opts_init(&opts);
	opts.load_external_files = true;
	opts.ignore_missing_external_files = true;
	opts.evaluate_caches = true;
	opts.evaluate_skinning = true;
	opts.target_axes = ufbx_axes_right_handed_y_up;
	opts.target_unit_meters = 1.0;

	ufbx_scene *scene = ufbx_load_file(argv[1], &opts, NULL);
	if (!scene) return 1;
	printf("F %s anim=%d layers=%zu ignore_conn=%d overrides=%zu\n", argv[1],
		(int)scene->anim != 0, scene->anim.layers.count, (int)scene->anim.ignore_connections,
		scene->anim.prop_overrides.count);

	for (size_t i = 0; i < scene->anim.layers.count; i++) {
		ufbx_anim_layer *l = scene->anim.layers.data[i];
		printf("L %zu id=%u weight=%016llx animated=%d blended=%d additive=%d cscale=%d crot=%d range=%u..%u props=%zu\n",
			i, l->element.element_id, *(unsigned long long*)&l->weight,
			(int)l->weight_is_animated, (int)l->blended, (int)l->additive,
			(int)l->compose_scale, (int)l->compose_rotation,
			l->_min_element_id, l->_max_element_id, l->anim_props.count);
	}

	for (size_t i = 0; i < scene->anim_layers.count; i++) {
		ufbx_anim_layer *l = scene->anim_layers.data[i];
		printf("A id=%u weight=%016llx animated=%d blended=%d additive=%d cscale=%d crot=%d props=%zu\n",
			l->element.element_id, *(unsigned long long*)&l->weight,
			(int)l->weight_is_animated, (int)l->blended, (int)l->additive,
			(int)l->compose_scale, (int)l->compose_rotation, l->anim_props.count);
	}

	for (size_t type = 0; type < UFBX_ELEMENT_TYPE_COUNT; type++) {
		for (size_t ei = 0; ei < scene->elements_by_type[type].count; ei++) {
			ufbx_element *elem = scene->elements_by_type[type].data[ei];
			for (size_t p = 0; p < elem->props.props.count; p++) {
				const ufbx_prop *pr = &elem->props.props.data[p];
				if (!(pr->flags & (UFBX_PROP_FLAG_ANIMATED|UFBX_PROP_FLAG_CONNECTED|UFBX_PROP_FLAG_OVERRIDDEN))) continue;
				printf("P %u %zu %zu \"%s\" flags=%08x key=%08x v=%016llx,%016llx,%016llx\n",
					elem->element_id, type, ei, pr->name.data, (uint32_t)pr->flags, pr->_internal_key,
					*(unsigned long long*)&pr->value_vec3.x, *(unsigned long long*)&pr->value_vec3.y,
					*(unsigned long long*)&pr->value_vec3.z);
			}
		}
	}
	ufbx_free_scene(scene);
	return 0;
}
