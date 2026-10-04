// Animation creation: `ufbx_create_anim()` and its private helpers.
//
// C: ufbxi_check_string (ufbx.c:26506-26518), ufbxi_push_anim_string (26520-26534),
//    the three override comparators (26536-26558), ufbxi_create_anim_imp (26560-26676),
//    ufbx_create_anim (31202-31226).
//
// Deviations that cannot be expressed in this port:
// - `ufbxi_check_string()` takes a `ufbx_string` whose `length` may be `SIZE_MAX`, meaning
//   "strlen(data)". The port's caller-side strings are raw-byte `string`s that always carry an
//   explicit length (PORTING_NOTES 类型映射), so the implicit form is only reachable in C, where it
//   equals the caller's prefix up to the first NUL. Consequence: a name containing an embedded NUL
//   keeps its full length here, whereas C with `SIZE_MAX` would stop at the NUL.
// - `ufbxi_push_anim_string()` copies the bytes into the result arena and re-points the string at
//   the copy, which in C detaches the anim from the caller's memory. Managed strings are immutable
//   and GC-rooted, so the copy has no observable effect and the function is the identity here; what
//   *is* load-bearing is that it happens per override (see the interning note in InternPropNames).
// - `ufbxi_init_ator()`/`ufbxi_push_zero()`/`ufbxi_push_copy()` allocator plumbing and
//   `ufbxi_anim_imp` (`refcount` + `magic`, 26665-26673) have no managed counterpart, so
//   `ufbx_retain_anim()`/`ufbx_free_anim()` stay no-ops like the rest of that family
//   (PORTING_NOTES #4). `alloc == NULL` failures are likewise unreachable.
// - `(int64_t)dst->value.x` (26605) is undefined in C once the double leaves int64 range; the
//   unchecked `(long)` cast here yields 0x8000000000000000 in that case, as x86 `cvttsd2si` does.

using System;

namespace Ufbx.NET
{
    // C: ufbxi_create_anim_context (ufbx.c:26495-26504)
    internal sealed class UfbxiCreateAnimContext
    {
        // C: ufbx_error error -- the *internal* error of this call; `ufbxi_fix_error_type()` folds
        // it into the caller's `error` on the way out.
        public UfbxError Error = new UfbxError();

        public UfbxScene Scene;

        // C: ufbx_anim_opts opts -- a value copy of the caller's struct (PORTING_NOTES #9).
        public UfbxAnimOpts Opts = new UfbxAnimOpts();

        // C: ufbx_anim anim, addressed as `ufbx_anim *anim = &ac->anim`; `ufbx_create_anim()` hands
        // back `&imp->anim`, whose managed counterpart is this object.
        public UfbxAnim Anim = new UfbxAnim();
    }

    internal static class UfbxiCreateAnim
    {
        // C: const char ufbxi_empty_char[1] = { 0 } (ufbx.c:3370) == the port's string.Empty.
        const string EmptyChar = "";

        // C: ufbxi_check_string (ufbx.c:26506-26518). Validates UTF-8 and normalizes an empty
        // string to `ufbxi_empty_char` (so `{NULL, 0}` and `{"", 0}` become the same value).
        static string CheckString(UfbxiCreateAnimContext ac, string src)
        {
            int length = src != null ? src.Length : 0;
            string data = length != 0 ? src : EmptyChar;
            if (length > 0) {
                int validLength = UfbxiUtf8.ValidLengthStr(data, 0, length);
                // C: ufbxi_check_err_msg(error, valid_length == length, "Invalid UTF-8")
                UfbxiFail.CheckMsg(validLength == length, "Invalid UTF-8");
            }
            return data;
        }

        // C: ufbxi_prop_override_prop_name_less (ufbx.c:26536-26542). Sort by name only, so equal
        // names end up adjacent for the interning pass.
        static bool PropOverridePropNameLess(object user, UfbxPropOverride a, UfbxPropOverride b)
        {
            if (a.InternalKey != b.InternalKey) return a.InternalKey < b.InternalKey;
            return UfbxiStr.Less(a.PropName, b.PropName);
        }

        // C: ufbxi_prop_override_less (ufbx.c:26544-26551). The final order evaluation expects.
        // NOTE: the last term is C's `strcmp()` (26550), not `ufbxi_str_less()`; for NUL-free
        // names the two agree (NUL is the lowest byte, and memcmp-over-common-length then
        // shorter-first is exactly strcmp's ordering).
        static bool PropOverrideLess(object user, UfbxPropOverride a, UfbxPropOverride b)
        {
            if (a.ElementId != b.ElementId) return a.ElementId < b.ElementId;
            if (a.InternalKey != b.InternalKey) return a.InternalKey < b.InternalKey;
            return UfbxiStr.Cmp(a.PropName, b.PropName) < 0;
        }

        // C: ufbxi_transform_override_less (ufbx.c:26553-26558)
        static bool TransformOverrideLess(object user, UfbxTransformOverride a, UfbxTransformOverride b)
        {
            return a.NodeId < b.NodeId;
        }

        // C: ufbxi_create_anim_imp (ufbx.c:26560-26676)
        static bool CreateAnimImp(UfbxiCreateAnimContext ac)
        {
            UfbxScene scene = ac.Scene;
            UfbxAnim anim = ac.Anim;

            // C: ufbxi_init_ator(&ac->error, &ac->ator_result, &ac->opts.result_allocator, "result")
            // and `ac->result.unordered = true` (26565-26567) -- arena setup, see the header note.

            anim.IgnoreConnections = ac.Opts.IgnoreConnections;
            anim.Custom = true;

            uint[] layerIds = ac.Opts.LayerIds;
            int numLayers = layerIds != null ? layerIds.Length : 0;
            UfbxAnimLayer[] layers = new UfbxAnimLayer[numLayers];

            if (ac.Opts.OverrideLayerWeights != null && ac.Opts.OverrideLayerWeights.Length > 0) {
                double[] weights = ac.Opts.OverrideLayerWeights;
                UfbxiFail.CheckMsg(weights.Length == numLayers,
                    "override_layer_weights[] count must match layer_ids[] count");
                // C: push_copy(ufbx_real, num_layers, opts.override_layer_weights.data) -- the copy
                // length is `num_layers`, which the check above pins to `weights.Length`.
                double[] dst = new double[numLayers];
                Array.Copy(weights, 0, dst, 0, numLayers);
                anim.OverrideLayerWeights = dst;
            }

            UfbxAnimLayer[] sceneLayers = scene.AnimLayers ?? Array.Empty<UfbxAnimLayer>();
            for (int i = 0; i < numLayers; i++) {
                uint index = layerIds[i];
                UfbxiFail.CheckMsg(index < (uint)sceneLayers.Length, "layer_ids out of bounds");
                layers[i] = sceneLayers[index];
            }
            anim.Layers = layers;

            UfbxPropOverrideDesc[] srcOverrides = ac.Opts.PropOverrides;
            if (srcOverrides != null && srcOverrides.Length > 0) {
                int count = srcOverrides.Length;
                UfbxPropOverride[] dst = new UfbxPropOverride[count];

                for (int i = 0; i < count; i++) {
                    UfbxPropOverrideDesc src = srcOverrides[i];
                    UfbxPropOverride over = new UfbxPropOverride();

                    over.ElementId = src.ElementId;
                    over.Value = src.Value;
                    over.ValueInt = src.ValueInt;

                    // C: 26604-26608 -- `value.x` and `value_int` fill in for each other, so a
                    // scalar given only as a float still has an int form and vice versa.
                    if (over.Value.X != 0.0f && over.ValueInt == 0) {
                        over.ValueInt = (long)over.Value.X;
                    } else if (over.ValueInt != 0 && over.Value.X == 0.0f) {
                        over.Value.X = (double)over.ValueInt;
                    }

                    over.PropName = CheckString(ac, src.PropName);
                    over.ValueStr = CheckString(ac, src.ValueStr);

                    over.InternalKey = UfbxiProperties.GetNameKey(over.PropName, over.PropName.Length);
                    dst[i] = over;
                }

                UfbxiSort.UnstableSort(dst, count, PropOverridePropNameLess, null);
                InternPropNames(ac, dst);
                UfbxiSort.UnstableSort(dst, count, PropOverrideLess, null);

                for (int i = 1; i < count; i++) {
                    UfbxPropOverride prev = dst[i - 1];
                    UfbxPropOverride next = dst[i];
                    // C compares `prop_name.data` pointers (26651): after InternPropNames every
                    // equal pair has been aliased to one instance, so identity is the test C makes.
                    if (prev.ElementId == next.ElementId && ReferenceEquals(prev.PropName, next.PropName)) {
                        UfbxiPrint.FmtErrInfo(ac.Error, "element %u prop \"%s\"",
                            new UfbxiVaList().AddUInt(prev.ElementId).AddStr(prev.PropName));
                        UfbxiFail.FailMsg("Duplicate override", "Duplicate override");
                    }
                }

                anim.PropOverrides = dst;
            }

            UfbxTransformOverride[] srcTransforms = ac.Opts.TransformOverrides;
            if (srcTransforms != null && srcTransforms.Length > 0) {
                int count = srcTransforms.Length;
                UfbxTransformOverride[] dst = new UfbxTransformOverride[count];
                // C: push_copy(ufbx_transform_override, count, ...) -- a struct-wise copy, so the
                // anim does not alias the caller's array.
                for (int i = 0; i < count; i++) {
                    UfbxTransformOverride src = srcTransforms[i];
                    UfbxTransformOverride over = new UfbxTransformOverride();
                    over.NodeId = src.NodeId;
                    over.Transform = src.Transform;
                    dst[i] = over;
                }
                UfbxiSort.UnstableSort(dst, count, TransformOverrideLess, null);
                anim.TransformOverrides = dst;
            }

            // C: 26665-26673 -- `ufbxi_push(ufbxi_anim_imp, 1)`, `ufbxi_init_ref()` with the scene's
            // refcount as parent, `imp->anim = ac->anim`; no managed counterpart (header note).
            return true;
        }

        // C: ufbxi_create_anim_imp (ufbx.c:26620-26643), the body of the `ufbxi_for_list` loop.
        // `overrides` is already sorted by name, so every equal-name group is contiguous.
        static void InternPropNames(UfbxiCreateAnimContext ac, UfbxPropOverride[] overrides)
        {
            // C: `const ufbx_string *global_str = ufbxi_strings` walked forward only -- the array is
            // in `ufbxi_str_less` order, and the overrides are too, so one linear merge interns all
            // of them. `All` reproduces that order (see Parse/UfbxiStrings.cs).
            string[] global = UfbxiStrings.All;
            int globalIx = 0;
            string prevName = EmptyChar; // C: `ufbx_string prev_name = { ufbxi_empty_char }`

            for (int i = 0; i < overrides.Length; i++) {
                UfbxPropOverride over = overrides[i];

                // C: `if (over->value_str.length > 0) ufbxi_push_anim_string(ac, &over->value_str)`
                // -- an arena copy whose only purpose is to detach the anim from the caller's bytes;
                // managed strings are immutable, so there is nothing to do (header note).
                if (UfbxiStr.Equal(over.PropName, prevName)) {
                    over.PropName = prevName;
                    continue;
                }

                while (globalIx < global.Length && UfbxiStr.Less(global[globalIx], over.PropName)) {
                    globalIx++;
                }

                if (globalIx < global.Length && UfbxiStr.Equal(global[globalIx], over.PropName)) {
                    over.PropName = global[globalIx];
                }
                // C's else is `ufbxi_push_anim_string()`: a fresh buffer per distinct name. Here the
                // caller's instance stays put, which is why the duplicate check below can only be
                // made on names that this loop has just aliased.

                prevName = over.PropName;
            }
        }

        // C: ufbx_create_anim (ufbx.c:31202-31226). `ufbxi_check_opts_ptr(ufbx_anim, opts, error)`
        // (31204) is the `_begin_zero`/`_end_zero` sentinel guard again, not representable here.
        internal static UfbxAnim CreateAnimEntry(UfbxScene scene, UfbxAnimOpts userOpts, UfbxError pError)
        {
            // C: ufbx_assert(scene) (31205) -- a no-op in the reference build; the port reads
            // `scene->anim_layers` below and throws a NullReferenceException instead.
            UfbxiCreateAnimContext ac = new UfbxiCreateAnimContext();

            // C: `if (opts) { ac.opts = *opts; }` (31208-31210): NULL opts means the all-zero
            // struct. PORTING_NOTES #9 -- copy, never alias.
            if (userOpts != null) {
                ac.Opts = CopyOpts(userOpts);
            }

            ac.Scene = scene;

            bool ok;
            try {
                ok = CreateAnimImp(ac);
            } catch (UfbxParseError e) {
                // C: the `ufbxi_check_err_msg`/`ufbxi_fail_err_msg` sites write `ac->error` at the
                // failure point (only while `description` is still unset), plain `ufbxi_check_err`
                // writes nothing -- the two forms of PORTING_NOTES #3b.
                if (e.HasDescription && string.IsNullOrEmpty(ac.Error.Description)) {
                    ac.Error.Description = e.Message;
                }
                ok = false;
            }

            if (ok) {
                UfbxiPrint.ClearError(pError);
                return ac.Anim;
            }

            UfbxiPrint.FixErrorType(ac.Error, "Failed to create anim", pError);
            return null;
        }

        // C: `ufbx_anim_opts opts; if (opts) ac.opts = *opts;` (ufbx.c:31209). The arrays are not
        // mutated by this chain, but the copy is what keeps a caller's opts object comparable after
        // the call (PORTING_NOTES #9), so it stays a field-wise copy including the allocator.
        static UfbxAnimOpts CopyOpts(UfbxAnimOpts userOpts)
        {
            UfbxAnimOpts opts = new UfbxAnimOpts();
            opts.LayerIds = userOpts.LayerIds;
            opts.OverrideLayerWeights = userOpts.OverrideLayerWeights;
            opts.PropOverrides = userOpts.PropOverrides;
            opts.TransformOverrides = userOpts.TransformOverrides;
            opts.IgnoreConnections = userOpts.IgnoreConnections;
            opts.ResultAllocator = CopyAllocator(userOpts.ResultAllocator);
            return opts;
        }

        static UfbxAllocatorOpts CopyAllocator(UfbxAllocatorOpts src)
        {
            UfbxAllocatorOpts dst = new UfbxAllocatorOpts();
            if (src == null) return dst;
            dst.MemoryLimit = src.MemoryLimit;
            dst.AllocationLimit = src.AllocationLimit;
            dst.HugeThreshold = src.HugeThreshold;
            dst.MaxChunkSize = src.MaxChunkSize;
            dst.Allocator = CopyAllocatorFn(src.Allocator);
            return dst;
        }

        static UfbxAllocator CopyAllocatorFn(UfbxAllocator src)
        {
            UfbxAllocator dst = new UfbxAllocator();
            if (src == null) return dst;
            dst.AllocFn = src.AllocFn;
            dst.ReallocFn = src.ReallocFn;
            dst.FreeFn = src.FreeFn;
            dst.FreeAllocatorFn = src.FreeAllocatorFn;
            dst.User = src.User;
            return dst;
        }
    }
}
