// ufbxi_evaluate_imp / ufbxi_evaluate_scene -- the scene-evaluation pass of ufbx v0.23.1
// (ufbx.c:26054-26491), reached from UfbxApi.EvaluateScene (C: ufbx_evaluate_scene,
// ufbx.c:31186-31200). This is what `frame > 0` in test/hash_scene.c exercises.
//
// C evaluates a scene by *copying* it. `ufbxi_eval_context` keeps a copy of the `ufbx_scene`
// struct, an element buffer of `metadata.element_buffer_size` bytes that receives a memcpy of
// every element, and the `(src_element, dst_element)` address pair that lets
// `ufbxi_translate_element()` rebase any source pointer into the copy by pure arithmetic. Then
// every list, map and nested record that holds an element pointer is rebuilt inside the copy,
// each element's animated properties are evaluated into a fresh buffer, and `ufbxi_update_scene()`
// recomputes the derived values. The source scene is retained by the result (its refcount is made
// the parent of the new one) because un-animated properties, geometry buffers and DOM nodes are
// still read from it.
//
// Port shape (PORTING_NOTES.md #4: no arenas and no manual result memory):
//  * Address rebasing becomes a source-element -> clone dictionary. `UfbxElement.CloneElement()`
//    is the managed memcpy. The dictionary is filled for *all* elements before any pointer is
//    translated, which is what makes the port's two-phase order (allocate, then translate)
//    equivalent to C's single interleaved loop: C's rebasing is arithmetic over the buffer and
//    cannot observe whether a clone's contents have been filled in yet.
//  * The `ufbxi_check_err()` guards on every `ufbxi_push` are the arena's out-of-memory failure,
//    which managed allocation cannot produce (it throws), so the "Failed to evaluate" error tail
//    of ufbxi_evaluate_scene() is unreachable. It is kept as the counterpart of the C shape, like
//    UfbxApi.FreeScene().
//  * `ufbxi_scene_imp` refcounting and the `result_memory_used`/`result_allocs`/`temp_*`
//    statistics (ufbx.c:26424-26444) have no counterpart: the collector owns the lifetime of the
//    shared source buffers, and `test/hash_scene.h:1251-1266` does not hash the statistics.
//  * C's per-element connection lists are *views* into the two scene-wide arrays, so translation
//    only rebases the base pointer (ufbx.c:26164-26165). The port already copies them at load time
//    (Parse/SceneBuild.cs:956-957), so it copies translated values per element instead. The values
//    are identical; only the never-read-back object identity differs, as connections are not
//    mutated after translation.
//  * Lists C does *not* rebuild keep addressing the source scene there too -- notably
//    `ufbx_skin_weight.cluster`, `ufbx_mesh_part`, `ufbx_uv_set` and every geometry buffer. That
//    source-pointer property is part of C's observable behaviour (the source scene is retained
//    precisely for it), so the shallow copy reproduces it rather than "fixing" it by translating
//    more than ufbx.c does.

using System;
using System.Collections.Generic;

namespace Ufbx.NET
{
    // C: ufbxi_eval_context (ufbx.c:26054-26076)
    internal sealed class UfbxiEvalContext
    {
        public UfbxScene SrcScene;      // C: src_scene
        public UfbxScene Scene;         // C: scene
        public UfbxAnim Anim;           // C: anim
        public UfbxEvaluateOpts Opts;   // C: opts
        public double Time;             // C: time
        // C: `error` of the `ufbxi_eval_context ec = { 0 }` in ufbx_evaluate_scene (ufbx.c:31191).
        public UfbxError Error = new UfbxError();

        // C: `char *src_element` / `char *dst_element`, replaced by an explicit mapping.
        internal readonly Dictionary<UfbxElement, UfbxElement> Map = new Dictionary<UfbxElement, UfbxElement>();

        // C: ufbxi_translate_element (ufbx.c:26078-26081)
        public UfbxElement Translate(UfbxElement elem)
        {
            if (elem == null) return null;
            return Map[elem];
        }

        // C: `ufbxi_translate_element()` at a typed call site, i.e. with the C cast.
        public T TranslateAs<T>(T elem) where T : UfbxElement
        {
            return elem == null ? null : (T)Translate(elem);
        }
    }

    internal static class UfbxiEvaluateScene
    {
        // C: ufbxi_evaluate_scene (ufbx.c:26454-26491)
        internal static UfbxScene EvaluateScene(UfbxiEvalContext ec, UfbxScene scene, UfbxAnim anim,
            double time, UfbxEvaluateOpts userOpts, UfbxError pError)
        {
            // C: `ec->opts = *user_opts` else `memset(&ec->opts, 0, sizeof(ec->opts))` -- NULL opts
            // means every option off, in particular `evaluate_skinning` and `evaluate_caches`.
            ec.Opts = userOpts ?? UfbxEvaluateOpts.CreateDefault();
            ec.SrcScene = scene;
            ec.Anim = anim != null ? anim : scene.Anim;
            ec.Time = time;

            if (EvaluateImp(ec)) {
                UfbxiPrint.ClearError(pError);
                return ec.Scene;
            }

            UfbxiPrint.FixErrorType(ec.Error, "Failed to evaluate", pError);
            return null;
        }

        // C: ufbxi_evaluate_imp (ufbx.c:26113-26452)
        static bool EvaluateImp(UfbxiEvalContext ec)
        {
            UfbxScene src = ec.SrcScene;
            UfbxScene sc = ec.Scene = src.CloneShallow();
            int numElements = src.Elements != null ? src.Elements.Length : 0;

            // C: 26118-26120 -- `element_data` (the memcpy destination) and `scene.elements.data`.
            UfbxElement[] dstElements = new UfbxElement[numElements];
            for (int i = 0; i < numElements; i++) {
                UfbxElement se = src.Elements[i];
                UfbxElement de = se.CloneElement();
                de.Props = se.Props.CloneShallow();
                ec.Map[se] = de;
                dstElements[i] = de;
            }
            sc.Elements = dstElements;

            // C: 26127-26130 -- each `elements_by_type[i].data` is pushed with the source count and
            // then filled by `typed_id` in the element loop. `typed_id == index` holds by
            // construction (the loader sorts each typed list), so the port fills positionally;
            // `MaterializeTypedList` gives each list its concrete array type, and a zero-count list
            // is a non-NULL empty array exactly as `ufbxi_push(0)` is a valid pointer.
            for (int type = 0; type < UfbxEnumCounts.UfbxElementType; type++) {
                UfbxElement[] translated = TranslateList(ec, src.ElementsByType(type));
                sc.SetElementsByType(type, UfbxiSceneUpdate.MaterializeTypedList(type, translated));
            }

            // C: 26132-26146 -- two scene-wide connection arrays, each an independent copy with its
            // own translated `src`/`dst`. `connections_src.count == connections_dst.count` always
            // holds (Parse/SceneBuild.cs:946-947 clones one array from the other), which is what
            // lets C size both of them by `connections_dst.count`.
            sc.ConnectionsSrc = TranslateConnections(ec, src.ConnectionsSrc);
            sc.ConnectionsDst = TranslateConnections(ec, src.ConnectionsDst);

            // C: 26148-26149 -- `elements_by_name` re-pushed with each `element` rebased.
            sc.ElementsByName = TranslateElementsByName(ec, src.ElementsByName);

            // C: 26151-26152
            sc.RootNode = ec.TranslateAs(sc.RootNode);
            sc.Anim = TranslateAnim(ec, sc.Anim);

            // C: 26154-26172. The `memcpy(dst, src, size)` part and the `elements` /
            // `elements_by_type` stores happened above (see the port shape note).
            for (int i = 0; i < numElements; i++) {
                UfbxElement de = dstElements[i];

                de.ConnectionsSrc = TranslateConnections(ec, de.ConnectionsSrc);
                de.ConnectionsDst = TranslateConnections(ec, de.ConnectionsDst);

                if (de.Instances != null && de.Instances.Length > 0) {
                    de.Instances = TranslateList(ec, de.Instances);
                }
            }

            // C: 26174-26199
            foreach (UfbxNode node in sc.Nodes) {
                node.Parent = ec.TranslateAs(node.Parent);
                node.Children = TranslateList(ec, node.Children);

                node.Attrib = ec.TranslateAs(node.Attrib);
                node.Mesh = ec.TranslateAs(node.Mesh);
                node.Light = ec.TranslateAs(node.Light);
                node.Camera = ec.TranslateAs(node.Camera);
                node.Bone = ec.TranslateAs(node.Bone);
                node.InheritScaleNode = ec.TranslateAs(node.InheritScaleNode);
                node.ScaleHelper = ec.TranslateAs(node.ScaleHelper);
                node.BindPose = ec.TranslateAs(node.BindPose);

                // C: with more than one attribute the list is a real array, and with exactly one
                // `all_attribs.data` is made to alias the `attrib` field itself -- so the single
                // entry has to be the already translated attribute, which is what the field now is.
                if (node.AllAttribs != null && node.AllAttribs.Length > 1) {
                    node.AllAttribs = TranslateList(ec, node.AllAttribs);
                } else if (node.AllAttribs != null && node.AllAttribs.Length == 1) {
                    node.AllAttribs = new UfbxElement[] { node.Attrib };
                }

                node.GeometryTransformHelper = ec.TranslateAs(node.GeometryTransformHelper);

                node.Materials = TranslateList(ec, node.Materials);
            }

            // C: 26201-26209
            foreach (UfbxMesh mesh in sc.Meshes) {
                mesh.Materials = TranslateList(ec, mesh.Materials);
                mesh.SkinDeformers = TranslateList(ec, mesh.SkinDeformers);
                mesh.BlendDeformers = TranslateList(ec, mesh.BlendDeformers);
                mesh.CacheDeformers = TranslateList(ec, mesh.CacheDeformers);
                mesh.AllDeformers = TranslateList(ec, mesh.AllDeformers);
            }

            // C: 26211-26215
            foreach (UfbxStereoCamera stereo in sc.StereoCameras) {
                stereo.Left = ec.TranslateAs(stereo.Left);
                stereo.Right = ec.TranslateAs(stereo.Right);
            }

            // C: 26217-26220
            foreach (UfbxSkinDeformer skin in sc.SkinDeformers) {
                skin.Clusters = TranslateList(ec, skin.Clusters);
            }

            // C: 26222-26225
            foreach (UfbxSkinCluster cluster in sc.SkinClusters) {
                cluster.BoneNode = ec.TranslateAs(cluster.BoneNode);
            }

            // C: 26227-26230
            foreach (UfbxBlendDeformer blend in sc.BlendDeformers) {
                blend.Channels = TranslateList(ec, blend.Channels);
            }

            // C: 26232-26244. The keyframe copy is required, not just faithful:
            // ufbxi_update_blend_channel() rewrites `effective_weight` in place (ufbx.c:23314).
            foreach (UfbxBlendChannel chan in sc.BlendChannels) {
                if (chan.Keyframes != null) {
                    UfbxBlendKeyframe[] keys = new UfbxBlendKeyframe[chan.Keyframes.Length];
                    for (int i = 0; i < keys.Length; i++) {
                        UfbxBlendKeyframe key = chan.Keyframes[i].Clone();
                        key.Shape = ec.TranslateAs(key.Shape);
                        keys[i] = key;
                    }
                    chan.Keyframes = keys;
                }
                chan.TargetShape = ec.TranslateAs(chan.TargetShape);
            }

            // C: 26246-26249
            foreach (UfbxCacheDeformer deformer in sc.CacheDeformers) {
                deformer.File = ec.TranslateAs(deformer.File);
            }

            // C: 26251-26263
            foreach (UfbxMaterial material in sc.Materials) {
                material.Shader = ec.TranslateAs(material.Shader);

                // C: `fbx`/`pbr`/`features` are inline union storage in `ufbx_material`, so the
                // element memcpy owns a fresh set of each. Required: ufbxi_translate_maps() writes
                // `texture` into the maps (ufbx.c:26097-26102) and ufbxi_fetch_maps() memsets and
                // refills all three groups (ufbx.c:20130-20132).
                if (material.Fbx != null) material.Fbx = TranslateFbxMaps(ec, material.Fbx.CloneMaps());
                if (material.Pbr != null) material.Pbr = TranslatePbrMaps(ec, material.Pbr.CloneMaps());
                if (material.Features != null) material.Features = material.Features.CloneFeatures();

                if (material.Textures != null) {
                    UfbxMaterialTexture[] textures = new UfbxMaterialTexture[material.Textures.Length];
                    for (int i = 0; i < textures.Length; i++) {
                        UfbxMaterialTexture tex = material.Textures[i].Clone();
                        tex.Texture = ec.TranslateAs(tex.Texture);
                        textures[i] = tex;
                    }
                    material.Textures = textures;
                }
            }

            // C: 26265-26290
            foreach (UfbxTexture texture in sc.Textures) {
                texture.Video = ec.TranslateAs(texture.Video);

                if (texture.Layers != null) {
                    UfbxTextureLayer[] layers = new UfbxTextureLayer[texture.Layers.Length];
                    for (int i = 0; i < layers.Length; i++) {
                        UfbxTextureLayer layer = texture.Layers[i].Clone();
                        layer.Texture = ec.TranslateAs(layer.Texture);
                        layers[i] = layer;
                    }
                    texture.Layers = layers;
                }

                texture.FileTextures = TranslateList(ec, texture.FileTextures);

                if (texture.Shader != null) {
                    UfbxShaderTexture shader = texture.Shader.CloneShallow();
                    texture.Shader = shader;
                    // C: `ufbxi_push_copy()` of the input records. `texture` and the `*_prop`
                    // pointers are deliberately left addressing the source scene here, exactly as
                    // in C; ufbxi_update_shader_texture() re-finds them from the evaluated
                    // properties (ufbx.c:20498-20530), which is why they need a writable copy.
                    if (shader.Inputs != null) {
                        UfbxShaderTextureInput[] inputs = new UfbxShaderTextureInput[shader.Inputs.Length];
                        for (int i = 0; i < inputs.Length; i++) inputs[i] = shader.Inputs[i].Clone();
                        shader.Inputs = inputs;
                    }
                }
            }

            // C: 26292-26295
            foreach (UfbxShader shader in sc.Shaders) {
                shader.Bindings = TranslateList(ec, shader.Bindings);
            }

            // C: 26297-26308
            foreach (UfbxDisplayLayer layer in sc.DisplayLayers) {
                layer.Nodes = TranslateList(ec, layer.Nodes);
            }

            // C: 26310-26320
            foreach (UfbxSelectionSet set in sc.SelectionSets) {
                set.Nodes = TranslateList(ec, set.Nodes);
            }

            // C: 26322-26332
            foreach (UfbxSelectionNode node in sc.SelectionNodes) {
                node.TargetNode = ec.TranslateAs(node.TargetNode);
                node.TargetMesh = ec.TranslateAs(node.TargetMesh);
            }

            // C: 26334-26350
            foreach (UfbxConstraint constraint in sc.Constraints) {
                constraint.Node = ec.TranslateAs(constraint.Node);
                constraint.AimUpNode = ec.TranslateAs(constraint.AimUpNode);
                constraint.IkEffector = ec.TranslateAs(constraint.IkEffector);
                constraint.IkEndNode = ec.TranslateAs(constraint.IkEndNode);

                if (constraint.Targets != null) {
                    UfbxConstraintTarget[] targets = new UfbxConstraintTarget[constraint.Targets.Length];
                    for (int i = 0; i < targets.Length; i++) {
                        UfbxConstraintTarget target = constraint.Targets[i].Clone();
                        target.Node = ec.TranslateAs(target.Node);
                        targets[i] = target;
                    }
                    constraint.Targets = targets;
                }
            }

            // C: 26352-26355
            foreach (UfbxAudioLayer layer in sc.AudioLayers) {
                layer.Clips = TranslateList(ec, layer.Clips);
            }

            // C: 26357-26363
            foreach (UfbxAnimStack stack in sc.AnimStacks) {
                stack.Layers = TranslateList(ec, stack.Layers);
                stack.Anim = TranslateAnim(ec, stack.Anim);
            }

            // C: 26365-26379. NOTE: `layer->anim` is *not* translated, so it keeps addressing the
            // source scene's `ufbx_anim`; test/hash_scene.h:1094 hashes it, where it is observable
            // only through `element_id`s (identical in both scenes) and the source's untouched
            // `time_begin`/`time_end`.
            foreach (UfbxAnimLayer layer in sc.AnimLayers) {
                layer.AnimValues = TranslateList(ec, layer.AnimValues);
                if (layer.AnimProps != null) {
                    UfbxAnimProp[] props = new UfbxAnimProp[layer.AnimProps.Length];
                    for (int i = 0; i < props.Length; i++) {
                        UfbxAnimProp prop = layer.AnimProps[i].Clone();
                        prop.Element = ec.Translate(prop.Element);
                        prop.AnimValue = ec.TranslateAs(prop.AnimValue);
                        props[i] = prop;
                    }
                    layer.AnimProps = props;
                }
            }

            // C: 26381-26385
            foreach (UfbxPose pose in sc.Poses) {
                if (pose.BonePoses != null) {
                    UfbxBonePose[] bones = new UfbxBonePose[pose.BonePoses.Length];
                    for (int i = 0; i < bones.Length; i++) {
                        UfbxBonePose bone = pose.BonePoses[i].Clone();
                        bone.BoneNode = ec.TranslateAs(bone.BoneNode);
                        bones[i] = bone;
                    }
                    pose.BonePoses = bones;
                }
            }

            // C: 26387 -- the animation that is actually evaluated: a copy of the caller's (or the
            // scene's) `ufbx_anim` whose layer list addresses the clones.
            ec.Anim = TranslateAnim(ec, ec.Anim);

            // C: 26389-26393. `curves[3]` is inline storage in C, so the clone needs its own triple
            // before the three slots are rebased.
            foreach (UfbxAnimValue value in sc.AnimValues) {
                if (value.Curves != null) {
                    UfbxAnimCurve[] curves = new UfbxAnimCurve[3];
                    for (int i = 0; i < 3; i++) curves[i] = ec.TranslateAs(value.Curves[i]);
                    value.Curves = curves;
                }
            }

            EvaluateElementProps(ec, src, sc);

            // C: 26411 -- recompute everything derived from the evaluated properties.
            UfbxiSceneUpdate.UpdateScene(sc, false, ec.Anim.TransformOverrides);

            // C: 26413-26419. Same body as the load-path call at ufbx.c:25367-25373
            // (`Parse/Load.cs`), only against the *evaluated* copy and at `ec->time`:
            //   ufbx_geometry_cache_data_opts cache_opts = { 0 };
            //   cache_opts.open_file_cb = ec->opts.open_file_cb;
            //   ufbxi_check_err(&ec->error, ufbxi_evaluate_skinning(&ec->scene, &ec->error,
            //       &ec->result, &ec->tmp, ec->time,
            //       ec->opts.load_external_files && ec->opts.evaluate_caches, &cache_opts));
            // The `ufbxi_check_err()` tail is the arena's out-of-memory failure, which managed
            // allocation cannot produce (PORTING_NOTES #4), so `EvaluateSkinning` returns void and
            // the "Failed to evaluate" error path of `ufbxi_evaluate_scene()` stays unreachable.
            // NOTE: the golden harness never reaches this branch -- `test/hash_scene.c:136` passes
            // NULL eval opts, so `evaluate_skinning` is false there and the goldens only prove the
            // load-path call. This branch is proved by tools/SkinCheck instead.
            if (ec.Opts.EvaluateSkinning) {
                UfbxGeometryCacheDataOpts cacheOpts = new UfbxGeometryCacheDataOpts();
                cacheOpts.OpenFileCb = ec.Opts.OpenFileCb;
                UfbxiSceneOpts.EvaluateSkinning(ec.Scene, ec.Error, ec.Time,
                    ec.Opts.LoadExternalFiles && ec.Opts.EvaluateCaches, cacheOpts);
            }

            // C: 26445-26447 -- `(*p_elem)->scene = &imp->scene`.
            foreach (UfbxElement elem in sc.Elements) {
                elem.Scene = sc;
            }

            return true;
        }

        // C: the `ufbx_anim anim = *ec->anim;` copy and the property evaluation loop
        // (ufbx.c:26394-26409). Each element's buffer is `num_animated + num_overrides` wide, and
        // `props.defaults` is pointed back at the *source* scene's element properties.
        static void EvaluateElementProps(UfbxiEvalContext ec, UfbxScene src, UfbxScene sc)
        {
            UfbxPropOverride[] over = ec.Anim.PropOverrides;
            int overIx = 0;
            int overEnd = over != null ? over.Length : 0;

            foreach (UfbxElement elem in sc.Elements) {
                int numAnimated = elem.Props.NumAnimated;

                // C: the sliding window over the `element_id`-sorted overrides. `elements` is
                // sorted by `element_id` too, so the window only ever moves forward.
                int overBegin = overIx;
                while (overIx < overEnd && over[overIx].ElementId == elem.ElementId) overIx++;
                int numOverride = overIx - overBegin;

                numAnimated += numOverride;
                if (numAnimated == 0) continue;

                // C: `anim.prop_overrides.data = over - num_override; anim.prop_overrides.count =
                // num_override;` on the local struct copy. The window has to be a real view because
                // `UfbxiEvaluate.EvaluatePropFlagsLen()` branches on the override count being
                // non-zero: with the full array an element that has no overrides would take that
                // early-out and lose its animation.
                UfbxAnim anim = ec.Anim;
                if (overEnd > 0) {
                    anim = anim.CloneShallow();
                    anim.PropOverrides = Slice(over, overBegin, numOverride);
                }

                UfbxProp[] props = new UfbxProp[numAnimated];
                UfbxProps evaluated = UfbxiEvaluate.EvaluatePropsFlags(anim, elem, ec.Time, props,
                    ec.Opts.EvaluateFlags);
                evaluated.Defaults = src.Elements[elem.ElementId].Props;
                elem.Props = evaluated;
            }
        }

        // C: ufbxi_translate_anim (ufbx.c:26104-26110)
        static UfbxAnim TranslateAnim(UfbxiEvalContext ec, UfbxAnim anim)
        {
            if (anim == null) return null;
            UfbxAnim dst = anim.CloneShallow();
            dst.Layers = TranslateList(ec, dst.Layers);
            return dst;
        }

        // C: ufbxi_translate_element_list (ufbx.c:26083-26095)
        static T[] TranslateList<T>(UfbxiEvalContext ec, T[] src) where T : UfbxElement
        {
            if (src == null || src.Length == 0) return src;
            T[] dst = new T[src.Length];
            for (int i = 0; i < dst.Length; i++) dst[i] = (T)ec.Translate(src[i]);
            return dst;
        }

        // C: ufbxi_evaluate_imp (ufbx.c:26132-26146, 26164-26165); see the file header for why the
        // per-element lists are copies here rather than rebased views.
        static UfbxConnection[] TranslateConnections(UfbxiEvalContext ec, UfbxConnection[] src)
        {
            if (src == null) return null;
            UfbxConnection[] dst = new UfbxConnection[src.Length];
            for (int i = 0; i < dst.Length; i++) {
                UfbxConnection conn = src[i].Clone();
                conn.Src = ec.Translate(conn.Src);
                conn.Dst = ec.Translate(conn.Dst);
                dst[i] = conn;
            }
            return dst;
        }

        // C: ufbxi_evaluate_imp (ufbx.c:26148-26149). `ufbx_name_element` is a value record whose
        // only pointer is `element`.
        static UfbxNameElement[] TranslateElementsByName(UfbxiEvalContext ec, UfbxNameElement[] src)
        {
            if (src == null) return null;
            UfbxNameElement[] dst = new UfbxNameElement[src.Length];
            for (int i = 0; i < dst.Length; i++) {
                UfbxNameElement named = src[i];
                named.Element = ec.Translate(named.Element);
                dst[i] = named;
            }
            return dst;
        }

        // C: ufbxi_translate_maps (ufbx.c:26097-26102) over an already copied map set.
        static UfbxMaterialFbxMaps TranslateFbxMaps(UfbxiEvalContext ec, UfbxMaterialFbxMaps maps)
        {
            foreach (UfbxMaterialMap map in maps.Maps) map.Texture = ec.TranslateAs(map.Texture);
            return maps;
        }

        static UfbxMaterialPbrMaps TranslatePbrMaps(UfbxiEvalContext ec, UfbxMaterialPbrMaps maps)
        {
            foreach (UfbxMaterialMap map in maps.Maps) map.Texture = ec.TranslateAs(map.Texture);
            return maps;
        }

        // C: `ufbxi_sub_ptr(over, num)` .. `+num`, i.e. an array view. The port's prop iterator
        // takes an array, so the window is copied -- `num_override` is bounded by the caller's
        // total override count, which is zero for every plain loaded scene.
        static UfbxPropOverride[] Slice(UfbxPropOverride[] src, int begin, int count)
        {
            if (count == 0) return Array.Empty<UfbxPropOverride>();
            UfbxPropOverride[] dst = new UfbxPropOverride[count];
            Array.Copy(src, begin, dst, 0, count);
            return dst;
        }
    }
}
