// S4b-1: animation evaluation, ported from ufbx v0.23.1 `ufbx.c`.
//
// Function inventory (C line ranges):
//   ufbxi_override_less_than_prop            (ufbx.c:25637-25642)
//   ufbxi_override_equals_to_prop            (ufbx.c:25644-25649)
//   ufbxi_find_prop_override                 (ufbx.c:25651-25672)
//   ufbxi_find_element_prop_overrides        (ufbx.c:25674-25687)
//   ufbxi_anim_layer_combine_ctx             (ufbx.c:25689-25695)
//   ufbxi_pow_abs                            (ufbx.c:25697-25703)
//   ufbxi_combine_anim_layer                 (ufbx.c:25705-25757)
//   ufbxi_anim_layer_might_contain_id        (ufbx.c:25759-25765)
//   ufbxi_evaluate_props                     (ufbx.c:25767-25826)
//   ufbxi_evaluate_connected_prop            (ufbx.c:25830-25853)
//   ufbxi_prop_iter / init / next            (ufbx.c:25855-25932)
//   ufbxi_evaluate_selected_props            (ufbx.c:25934-25982)
//   ufbxi_extrapolate_curve                  (ufbx.c:25985-26050)
//   ufbx_find_prop_len + find_real/vec3/int/bool/string/blob (ufbx.c:30643-30718)
//     with their `strlen` forms (ufbx.c:33151-33156)
//   the element/scene lookup group (ufbx.c:30720-30833, 31413-31484) is ported in
//     Parse/SceneFind.cs and Parse/SceneFinalize.cs; the public wrappers forward from here
//   ufbx_evaluate_curve / _flags             (ufbx.c:30835-30922)
//   ufbx_evaluate_anim_value_real/vec3(_flags) (ufbx.c:30924-30957)
//   ufbx_evaluate_prop(_flags)(_len)         (ufbx.c:30959-30997)
//   ufbx_evaluate_props(_flags)              (ufbx.c:30999-31031)
//   ufbxi_transform_props_* tables           (ufbx.c:31038-31068)
//   ufbx_evaluate_transform(_flags)          (ufbx.c:31033-31168)
//   ufbx_evaluate_blend_weight(_flags)       (ufbx.c:31170-31184)
//
// Conventions (PORTING_NOTES.md):
//  * C's interned-string pointer comparisons (`a->prop_name.data == prop->name.data`) are
//    content equality here (`UfbxiStr.Equal`), as in the earlier waves.
//  * C walks `layer->anim_props` with a raw pointer past `count` into a NULL sentinel slot
//    (ufbx.c:22260 allocates `count + 1`); the port's array has exactly `count` entries, so an
//    index out of range is the sentinel. C's loop guards all advance steps with
//    `aprop->element == element`, and the sentinel's element is NULL, so bounding the walk at
//    the array end is equivalent.
//  * `ufbxi_macro_lower_bound_eq`/`_upper_bound_eq` are transcribed literally (same contract as
//    Parse/SceneBuild.cs:1160-1198, re-declared here because that copy is file-private).
//  * Element identity uses `ReferenceEquals`, matching C's pointer equality on the loaded scene.

using System;

namespace Ufbx.NET
{
    internal static class UfbxiEvaluate
    {
        // ==================================================================
        // Search macros (ufbx.c:1188-1229)
        // ==================================================================

        static void LowerBoundEq<T>(T[] data, int begin, int size, int linearSize,
            Func<T, bool> less, Func<T, bool> eq, ref int result)
        {
            int lo = begin, hi = size;
            while (hi - lo > linearSize) {
                int mid = lo + (hi - lo) / 2;
                if (less(data[mid])) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (eq(data[lo])) { result = lo; break; }
            }
        }

        static void UpperBoundEq<T>(T[] data, int begin, int size, int linearSize,
            Func<T, bool> eq, ref int result)
        {
            int lo = begin, hi = size;
            for (int step = 1; step < 100 && hi - lo > step; step *= 2) {
                if (!eq(data[lo + step])) { hi = lo + step; break; }
                lo += step;
            }
            while (hi - lo > linearSize) {
                int mid = lo + (hi - lo) / 2;
                if (eq(data[mid])) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (!eq(data[lo])) break;
            }
            result = lo;
        }

        // ==================================================================
        // Prop overrides (ufbx.c:25637-25687)
        // ==================================================================

        // C: ufbxi_override_less_than_prop (ufbx.c:25637-25642). NOTE: C returns `strcmp(...)`
        // from a `bool` function, so *any* non-zero result (negative included) is `true`.
        static bool OverrideLessThanProp(UfbxPropOverride over, uint elementId, UfbxProp prop)
        {
            if (over.ElementId != elementId) return over.ElementId < elementId;
            if (over.InternalKey != prop.InternalKey) return over.InternalKey < prop.InternalKey;
            return UfbxiProperties.Strcmp(over.PropName, prop.Name) != 0;
        }

        // C: ufbxi_override_equals_to_prop (ufbx.c:25644-25649).
        static bool OverrideEqualsToProp(UfbxPropOverride over, uint elementId, UfbxProp prop)
        {
            if (over.ElementId != elementId) return false;
            if (over.InternalKey != prop.InternalKey) return false;
            return UfbxiProperties.Strcmp(over.PropName, prop.Name) == 0;
        }

        // C: ufbxi_find_prop_override (ufbx.c:25651-25672).
        static bool FindPropOverride(UfbxPropOverride[] overrides, uint elementId, ref UfbxProp prop)
        {
            int count = overrides != null ? overrides.Length : 0;
            int ix = -1; // C: SIZE_MAX sentinel; -1 == "not found" in the port

            // The search only reads the prop, so snapshot it to use inside the search lambdas.
            UfbxProp target = prop;

            LowerBoundEq(overrides ?? Array.Empty<UfbxPropOverride>(), 0, count, 16,
                a => OverrideLessThanProp(a, elementId, target),
                a => OverrideEqualsToProp(a, elementId, target),
                ref ix);

            if (ix >= 0) {
                UfbxPropOverride over = overrides[ix];
                const uint clearFlags = (uint)UfbxPropFlags.NoValue | (uint)UfbxPropFlags.NotFound;
                prop.Flags = (UfbxPropFlags)(((uint)prop.Flags & ~clearFlags) | (uint)UfbxPropFlags.Overridden);
                prop.ValueVec4 = over.Value;
                prop.ValueReal3 = 0.0;
                prop.ValueInt = over.ValueInt;
                prop.ValueStr = over.ValueStr;
                // C: value_blob aliases value_str (data/length copied verbatim).
                prop.ValueBlob = string.IsNullOrEmpty(over.ValueStr)
                    ? null
                    : UfbxiRawStr.ToBytes(over.ValueStr, 0, over.ValueStr.Length);
                return true;
            } else {
                return false;
            }
        }

        // C: ufbxi_find_element_prop_overrides (ufbx.c:25674-25687). Returns the half-open
        // [begin, end) range within `overrides`; the port has no pointer-slice representation.
        static void FindElementPropOverrides(UfbxPropOverride[] overrides, uint elementId,
            out int begin, out int end)
        {
            UfbxPropOverride[] data = overrides ?? Array.Empty<UfbxPropOverride>();
            int count = data.Length;
            begin = count;
            end = begin;

            LowerBoundEq(data, 0, count, 32,
                a => a.ElementId < elementId,
                a => a.ElementId == elementId,
                ref begin);

            UpperBoundEq(data, begin, count, 32,
                a => a.ElementId == elementId,
                ref end);
        }

        // ==================================================================
        // Layer combining (ufbx.c:25689-25765)
        // ==================================================================

        // C: ufbxi_pow_abs (ufbx.c:25697-25703).
        static double PowAbs(double v, double e)
        {
            if (e <= 0.0) return 1.0;
            if (e >= 1.0) return v;
            double sign = v < 0.0 ? -1.0 : 1.0;
            return sign * UfbxMath.Pow(v * sign, e);
        }

        // C: ufbxi_anim_layer_combine_ctx (ufbx.c:25689-25695).
        sealed class AnimLayerCombineCtx
        {
            public UfbxAnim Anim;
            public UfbxElement Element;
            public double Time;
            public UfbxRotationOrder RotationOrder;
            public bool HasRotationOrder;
        }

        // C: ufbxi_combine_anim_layer (ufbx.c:25705-25757). `result` is the destination vec3 and
        // `value` the freshly evaluated one; C writes through pointers, the port returns it.
        static UfbxVec3 CombineAnimLayer(AnimLayerCombineCtx ctx, UfbxAnimLayer layer, double weight,
            string propName, UfbxVec3 result, UfbxVec3 value)
        {
            if (layer.ComposeRotation && layer.Blended
                && UfbxiStr.Equal(propName, UfbxiStrings.Lcl_Rotation) && !ctx.HasRotationOrder) {
                UfbxProp rp = EvaluatePropFlagsLen(ctx.Anim, ctx.Element,
                    UfbxiStrings.RotationOrder, UfbxiStrings.RotationOrder.Length, ctx.Time, 0);
                // NOTE: Defaults to 0 (UFBX_ROTATION_XYZ) gracefully if property is not found
                if (rp.ValueInt >= 0 && rp.ValueInt <= (long)UfbxRotationOrder.Spheric) {
                    ctx.RotationOrder = (UfbxRotationOrder)rp.ValueInt;
                } else {
                    ctx.RotationOrder = UfbxRotationOrder.Xyz;
                }
                ctx.HasRotationOrder = true;
            }

            if (layer.Additive) {
                if (layer.ComposeScale && UfbxiStr.Equal(propName, UfbxiStrings.Lcl_Scaling)) {
                    result.X *= PowAbs(value.X, weight);
                    result.Y *= PowAbs(value.Y, weight);
                    result.Z *= PowAbs(value.Z, weight);
                } else if (layer.ComposeRotation && UfbxiStr.Equal(propName, UfbxiStrings.Lcl_Rotation)) {
                    UfbxQuat a = UfbxQuat.EulerToQuat(result, ctx.RotationOrder);
                    UfbxQuat b = UfbxQuat.EulerToQuat(value, ctx.RotationOrder);
                    b = UfbxQuat.Slerp(UfbxQuat.Identity, b, weight);
                    UfbxQuat res = UfbxQuat.Mul(a, b);
                    result = UfbxQuat.ToEuler(res, ctx.RotationOrder);
                } else {
                    result.X += value.X * weight;
                    result.Y += value.Y * weight;
                    result.Z += value.Z * weight;
                }
            } else if (layer.Blended) {
                double resWeight = 1.0 - weight;
                if (layer.ComposeScale && UfbxiStr.Equal(propName, UfbxiStrings.Lcl_Scaling)) {
                    result.X = PowAbs(result.X, resWeight) * PowAbs(value.X, weight);
                    result.Y = PowAbs(result.Y, resWeight) * PowAbs(value.Y, weight);
                    result.Z = PowAbs(result.Z, resWeight) * PowAbs(value.Z, weight);
                } else if (layer.ComposeRotation && UfbxiStr.Equal(propName, UfbxiStrings.Lcl_Rotation)) {
                    UfbxQuat a = UfbxQuat.EulerToQuat(result, ctx.RotationOrder);
                    UfbxQuat b = UfbxQuat.EulerToQuat(value, ctx.RotationOrder);
                    UfbxQuat res = UfbxQuat.Slerp(a, b, weight);
                    result = UfbxQuat.ToEuler(res, ctx.RotationOrder);
                } else {
                    result.X = result.X * resWeight + value.X * weight;
                    result.Y = result.Y * resWeight + value.Y * weight;
                    result.Z = result.Z * resWeight + value.Z * weight;
                }
            } else {
                result = value;
            }
            return result;
        }

        // C: ufbxi_anim_layer_might_contain_id (ufbx.c:25759-25765). The range test is unsigned
        // arithmetic in C, so `id - min` wrapping below zero makes it fail; `unchecked` keeps
        // the port identical.
        internal static bool AnimLayerMightContainId(UfbxAnimLayer layer, uint id)
        {
            uint idMask = 4 - 1; // C: ufbxi_arraycount(layer->_element_id_bitmask) - 1, array of 4
            uint[] mask = layer.ElementIdBitmask;
            unchecked {
                bool ok = (uint)(id - layer.MinElementId) <= (uint)(layer.MaxElementId - layer.MinElementId);
                ok &= (mask[(id >> 5) & idMask] & (1u << (int)(id & 31))) != 0;
                return ok;
            }
        }

        // ==================================================================
        // Curve evaluation (ufbx.c:30835-30922, 25985-26050)
        // ==================================================================

        // C: ufbx_evaluate_curve_flags (ufbx.c:30840-30922).
        internal static double EvaluateCurveFlags(UfbxAnimCurve curve, double time,
            double defaultValue, uint flags)
        {
            if (curve == null) return defaultValue;
            UfbxKeyframe[] keys = curve.Keyframes;
            int count = keys != null ? keys.Length : 0;
            if (count <= 1) {
                if (count == 1) {
                    return keys[0].Value;
                } else {
                    return defaultValue;
                }
            }

            if ((flags & (uint)UfbxEvaluateFlags.NoExtrapolation) == 0) {
                if (time < curve.MinTime || time > curve.MaxTime) {
                    return ExtrapolateCurve(curve, time, flags);
                }
            }

            int begin = 0;
            int end = count;
            while (end - begin >= 8) {
                int mid = (begin + end) >> 1;
                if (keys[mid].Time <= time) {
                    begin = mid + 1;
                } else {
                    end = mid;
                }
            }

            end = count;
            for (; begin < end; begin++) {
                UfbxKeyframe next = keys[begin];
                if (next.Time <= time) continue;

                // First keyframe
                if (begin == 0) return next.Value;

                UfbxKeyframe prev = keys[begin - 1];

                // Exact keyframe
                if (prev.Time == time) return prev.Value;

                double rcpDelta = 1.0 / (next.Time - prev.Time);
                double t = (time - prev.Time) * rcpDelta;

                switch (prev.Interpolation) {

                case UfbxInterpolation.ConstantPrev:
                    return prev.Value;

                case UfbxInterpolation.ConstantNext:
                    return next.Value;

                case UfbxInterpolation.Linear:
                    return prev.Value * (1.0 - t) + next.Value * t;

                case UfbxInterpolation.Cubic: {
                    double x1 = prev.Right.Dx * rcpDelta;
                    double x2 = 1.0 - next.Left.Dx * rcpDelta;
                    t = UfbxiSceneOpts.FindCubicBezierT(x1, x2, t);

                    double t2 = t * t, t3 = t2 * t;
                    double u = 1.0 - t, u2 = u * u, u3 = u2 * u;

                    double y0 = prev.Value;
                    double y3 = next.Value;
                    double y1 = y0 + prev.Right.Dy;
                    double y2 = y3 - next.Left.Dy;

                    return u3 * y0 + 3.0 * (u2 * t * y1 + u * t2 * y2) + t3 * y3;
                }

                default:
                    // C: ufbxi_unreachable("Bad interpolation mode"); the port falls through with
                    // the same value C returns.
                    return 0.0;

                }
            }

            // Last keyframe
            return keys[count - 1].Value;
        }

        // C: ufbx_evaluate_curve (ufbx.c:30835-30838).
        internal static double EvaluateCurve(UfbxAnimCurve curve, double time, double defaultValue)
        {
            return EvaluateCurveFlags(curve, time, defaultValue, 0);
        }

        // C: ufbxi_extrapolate_curve (ufbx.c:25985-26050). Recursion is bounded because the
        // recursive call always passes UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION.
        static double ExtrapolateCurve(UfbxAnimCurve curve, double realTime, uint flags)
        {
            UfbxKeyframe[] keys = curve.Keyframes;
            bool pre = realTime < curve.MinTime;
            UfbxKeyframe key;
            UfbxExtrapolation ext;
            if (pre) {
                key = keys[0];
                ext = curve.PreExtrapolation;
            } else {
                key = keys[keys.Length - 1];
                ext = curve.PostExtrapolation;
            }

            if (ext.Mode == UfbxExtrapolationMode.Constant) {
                return key.Value;
            } else if (ext.Mode == UfbxExtrapolationMode.Slope) {
                UfbxTangent tangent = pre ? key.Right : key.Left;
                return key.Value + tangent.Dy * ((realTime - key.Time) / tangent.Dx);
            } else if (ext.RepeatCount == 0) {
                return key.Value;
            }

            // Perform all operations in KTime ticks to be frame perfect
            double scale = (double)curve.Scene.Metadata.KtimeSecond;
            double minTime = UfbxMath.Rint(curve.MinTime * scale);
            double maxTime = UfbxMath.Rint(curve.MaxTime * scale);
            double time = realTime * scale;

            double delta = pre ? minTime - time : time - maxTime;
            double duration = maxTime - minTime;

            // Require at least one KTime unit
            if (!(duration >= 1.0)) return key.Value;

            double rep = delta / duration;
            double repN = UfbxMath.Floor(rep);
            double repD = delta - repN * duration;

            if (ext.RepeatCount > 0 && repN >= (double)ext.RepeatCount) {
                // Clamp to the repeat count to handle mirroring
                repN = (double)(ext.RepeatCount - 1);
                repD = duration;
            }

            if (ext.Mode == UfbxExtrapolationMode.Mirror) {
                double repParity = repN * 0.5 - UfbxMath.Floor(repN * 0.5);
                if (repParity <= 0.25) {
                    repD = duration - repD;
                }
            }

            if (pre) repD = duration - repD;
            double newTime = (minTime + repD) / scale;

            double value = EvaluateCurveFlags(curve, newTime, key.Value,
                flags | (uint)UfbxEvaluateFlags.NoExtrapolation);

            if (ext.Mode == UfbxExtrapolationMode.RepeatRelative) {
                double valDelta = keys[keys.Length - 1].Value - keys[0].Value;
                if (pre) valDelta = -valDelta;
                value += valDelta * (repN + 1.0);
            }

            return value;
        }

        // C: ufbx_evaluate_anim_value_real_flags (ufbx.c:30934-30943).
        internal static double EvaluateAnimValueRealFlags(UfbxAnimValue animValue, double time, uint flags)
        {
            if (animValue == null) return 0.0;

            double res = animValue.DefaultValue.X;
            if (animValue.Curves[0] != null) res = EvaluateCurveFlags(animValue.Curves[0], time, res, flags);
            return res;
        }

        // C: ufbx_evaluate_anim_value_real (ufbx.c:30924-30927).
        internal static double EvaluateAnimValueReal(UfbxAnimValue animValue, double time)
        {
            return EvaluateAnimValueRealFlags(animValue, time, 0);
        }

        // C: ufbx_evaluate_anim_value_vec3_flags (ufbx.c:30945-30957).
        internal static UfbxVec3 EvaluateAnimValueVec3Flags(UfbxAnimValue animValue, double time, uint flags)
        {
            if (animValue == null) return UfbxVec3.Zero;

            UfbxVec3 res = animValue.DefaultValue;
            if (animValue.Curves[0] != null) res.X = EvaluateCurveFlags(animValue.Curves[0], time, res.X, flags);
            if (animValue.Curves[1] != null) res.Y = EvaluateCurveFlags(animValue.Curves[1], time, res.Y, flags);
            if (animValue.Curves[2] != null) res.Z = EvaluateCurveFlags(animValue.Curves[2], time, res.Z, flags);
            return res;
        }

        // C: ufbx_evaluate_anim_value_vec3 (ufbx.c:30929-30932).
        internal static UfbxVec3 EvaluateAnimValueVec3(UfbxAnimValue animValue, double time)
        {
            return EvaluateAnimValueVec3Flags(animValue, time, 0);
        }

        // ==================================================================
        // Property lookup, public form (ufbx.c:30643-30718)
        // ==================================================================

        // C: ufbxi_cmp_prop_less_ref (ufbx.c:18571-18574).
        static bool CmpPropLessRef(UfbxProp a, string name, uint key)
        {
            if (a.InternalKey != key) return a.InternalKey < key;
            return UfbxiStr.Less(a.Name, name);
        }

        // C: ufbx_find_prop_len (ufbx.c:30643-30658). Unlike `ufbxi_find_prop_with_key` this
        // walks `defaults` only after a miss and has no NO_VALUE filter, and the search key is
        // compared with the interned-string ordering used by the sorted lists.
        internal static bool FindPropLen(UfbxProps props, string name, int nameLen, out UfbxProp prop)
        {
            string nameStr = nameLen == name.Length ? name : name.Substring(0, nameLen);
            uint key = UfbxiProperties.GetNameKey(name, nameLen);

            prop = default;
            while (props != null) {
                UfbxProp[] data = props.Props ?? Array.Empty<UfbxProp>();
                int count = props.Props != null ? props.Props.Length : 0;
                int index = -1; // C: SIZE_MAX
                LowerBoundEq(data, 0, count, 4,
                    a => CmpPropLessRef(a, nameStr, key),
                    a => a.InternalKey == key && UfbxiStr.Equal(a.Name, nameStr),
                    ref index);
                if (index >= 0) {
                    prop = data[index];
                    return true;
                }

                props = props.Defaults;
            }

            return false;
        }

        // C: ufbx_find_prop (ufbx.c:33157).
        internal static bool FindProp(UfbxProps props, string name, out UfbxProp prop)
        {
            return FindPropLen(props, name, name != null ? name.Length : 0, out prop);
        }

        // ==================================================================
        // Property evaluation (ufbx.c:25767-25982)
        // ==================================================================

        // C: ufbxi_find_anim_prop_start (ufbx.c:19328-19334), index form. The port's
        // `layer.AnimProps` has exactly `count` entries, so -1 is C's NULL return.
        static int FindAnimPropStartIndex(UfbxAnimLayer layer, UfbxElement element)
        {
            UfbxAnimProp[] data = layer.AnimProps ?? Array.Empty<UfbxAnimProp>();
            int size = data.Length;

            int index = -1; // C: SIZE_MAX

            LowerBoundEq(data, 0, size, 16,
                a => a.Element.ElementId < element.ElementId,
                a => a.Element.ElementId == element.ElementId,
                ref index);

            return index;
        }

        // C: ufbxi_evaluate_props (ufbx.c:25767-25826). Mutates `props` in place, like C.
        static void EvaluateProps(UfbxAnim anim, UfbxElement element, double time,
            UfbxProp[] props, int numProps, uint flags)
        {
            AnimLayerCombineCtx combineCtx = new AnimLayerCombineCtx();
            combineCtx.Anim = anim;
            combineCtx.Element = element;
            combineCtx.Time = time;

            uint elementId = element.ElementId;
            UfbxAnimLayer[] layers = anim.Layers ?? Array.Empty<UfbxAnimLayer>();
            int numLayers = layers.Length;
            for (int layerIx = 0; layerIx < numLayers; layerIx++) {
                UfbxAnimLayer layer = layers[layerIx];
                if (!AnimLayerMightContainId(layer, elementId)) continue;

                // Find the weight for the current layer
                double weight = layerIx < (anim.OverrideLayerWeights != null ? anim.OverrideLayerWeights.Length : 0)
                    ? anim.OverrideLayerWeights[layerIx]
                    : layer.Weight;
                if (layer.WeightIsAnimated && layer.Blended) {
                    int weightIx = FindAnimPropStartIndex(layer, layer);
                    if (weightIx >= 0) {
                        weight = EvaluateAnimValueRealFlags(
                            layer.AnimProps[weightIx].AnimValue, time, flags) / 100.0;
                        if (weight < 0.0) weight = 0.0;
                        if (weight > (double)0.99999f) weight = 1.0; // C: 0.99999f (ufbx.c:25785)
                    }
                }

                int apropIx = FindAnimPropStartIndex(layer, element);
                if (apropIx < 0) continue;

                UfbxAnimProp[] apropData = layer.AnimProps;
                int apropCount = apropData.Length;

                for (int i = 0; i < numProps; i++) {
                    UfbxProp prop = props[i];

                    // Don't evaluate on top of overridden properties
                    if ((prop.Flags & UfbxPropFlags.Overridden) != 0) continue;

                    // Connections override animation by default
                    if ((prop.Flags & UfbxPropFlags.Connected) != 0 && !anim.IgnoreConnections) continue;

                    // Skip until we reach `aprop >= prop`
                    // NOTE: C relies on the NULL sentinel past `anim_props.count`; the port stops
                    // at the array end instead (see the file header).
                    while (apropIx < apropCount && ReferenceEquals(apropData[apropIx].Element, element)
                        && apropData[apropIx].InternalKey < prop.InternalKey) apropIx++;
                    if (apropIx >= apropCount) continue;
                    if (!UfbxiStr.Equal(apropData[apropIx].PropName, prop.Name)) {
                        while (apropIx < apropCount && ReferenceEquals(apropData[apropIx].Element, element)
                            && UfbxiProperties.Strcmp(apropData[apropIx].PropName, prop.Name) < 0) apropIx++;
                        if (apropIx >= apropCount) continue;
                    }

                    if (UfbxiStr.Equal(apropData[apropIx].PropName, prop.Name)) {
                        UfbxVec3 v = EvaluateAnimValueVec3Flags(apropData[apropIx].AnimValue, time, flags);
                        if (layerIx == 0) {
                            prop.ValueVec3 = v;
                        } else {
                            prop.ValueVec3 = CombineAnimLayer(combineCtx, layer, weight, prop.Name,
                                prop.ValueVec3, v);
                        }
                    }

                    props[i] = prop;
                }
            }

            for (int i = 0; i < numProps; i++) {
                UfbxProp prop = props[i];
                if ((prop.Flags & UfbxPropFlags.Overridden) != 0) continue;
                prop.ValueInt = UfbxiBinaryArray.F64ToInt64(prop.ValueReal);
                props[i] = prop;
            }
        }

        // C: ufbxi_evaluate_connected_prop (ufbx.c:25830-25853). Recursion is bounded because
        // the recursive `ufbx_evaluate_prop_flags_len()` never sees a connected property again.
        static void EvaluateConnectedProp(ref UfbxProp prop, UfbxAnim anim, UfbxElement element,
            string name, double time, uint flags)
        {
            UfbxConnection conn = UfbxiSceneBuild.FindPropConnection(element, name);

            for (int i = 0; i < 1000 && conn != null; i++) {
                UfbxConnection nextConn = UfbxiSceneBuild.FindPropConnection(conn.Src, conn.SrcProp);
                if (nextConn == null) break;
                conn = nextConn;
            }

            // Found a non-cyclic connection
            if (conn != null && UfbxiSceneBuild.FindPropConnection(conn.Src, conn.SrcProp) == null) {
                UfbxProp ep = EvaluatePropFlagsLen(anim, conn.Src, conn.SrcProp,
                    conn.SrcProp != null ? conn.SrcProp.Length : 0, time, flags);
                prop.ValueVec4 = ep.ValueVec4;
                prop.ValueInt = ep.ValueInt;
                prop.ValueStr = ep.ValueStr;
                prop.ValueBlob = ep.ValueBlob;
            } else {
                // Connection not found, maybe it's animated?
                prop.Flags = (UfbxPropFlags)((uint)prop.Flags & ~(uint)UfbxPropFlags.Connected);
            }
        }

        // C: ufbxi_prop_iter (ufbx.c:25855-25859). Merges the element's own props with the
        // overrides that apply to it, both sorted by (key, name). C's `ufbx_prop tmp` member
        // exists only so the override case can hand out a pointer; the port returns the same
        // value by value, so no field is needed (`ufbxi_next_prop`'s result is always copied
        // out before the next call).
        sealed class PropIter
        {
            public UfbxProp[] Props;
            public int PropIx;
            public int PropEnd;
            public UfbxPropOverride[] Overrides;
            public int OverIx;
            public int OverEnd;
        }

        // C: ufbxi_init_prop_iter_slow (ufbx.c:25861-25872).
        static void InitPropIterSlow(PropIter iter, UfbxAnim anim, UfbxElement element)
        {
            iter.Props = element.Props.Props ?? Array.Empty<UfbxProp>();
            iter.PropIx = 0;
            iter.PropEnd = iter.Props.Length;

            FindElementPropOverrides(anim.PropOverrides, element.ElementId, out int begin, out int end);
            iter.Overrides = anim.PropOverrides ?? Array.Empty<UfbxPropOverride>();
            iter.OverIx = begin;
            iter.OverEnd = end;
        }

        // C: ufbxi_init_prop_iter (ufbx.c:25874-25882).
        static PropIter InitPropIter(UfbxAnim anim, UfbxElement element)
        {
            PropIter iter = new PropIter();
            iter.Props = element.Props.Props ?? Array.Empty<UfbxProp>();
            iter.PropIx = 0;
            iter.PropEnd = iter.Props.Length;
            iter.Overrides = Array.Empty<UfbxPropOverride>();
            iter.OverIx = 0;
            iter.OverEnd = 0;
            if (anim.PropOverrides != null && anim.PropOverrides.Length > 0) {
                InitPropIterSlow(iter, anim, element);
            }
            return iter;
        }

        // C: ufbxi_next_prop_slow (ufbx.c:25884-25922).
        static bool NextPropSlow(PropIter iter, out UfbxProp prop)
        {
            prop = default;
            if (iter.PropIx == iter.PropEnd && iter.OverIx == iter.OverEnd) return false;

            // We can use `UINT32_MAX` as a terminating key (aka prefix) as prop names must
            // be valid UTF-8 and the byte sequence "\xff\xff\xff\xff" is not valid.
            uint propKey = iter.PropIx != iter.PropEnd ? iter.Props[iter.PropIx].InternalKey : uint.MaxValue;
            uint overKey = iter.OverIx != iter.OverEnd ? iter.Overrides[iter.OverIx].InternalKey : uint.MaxValue;

            int cmp = 0;
            if (propKey != overKey) {
                cmp = propKey < overKey ? -1 : 1;
            } else {
                cmp = UfbxiProperties.Strcmp(iter.Props[iter.PropIx].Name, iter.Overrides[iter.OverIx].PropName);
            }

            if (cmp >= 0) {
                UfbxPropOverride over = iter.Overrides[iter.OverIx];
                UfbxProp dst = default;
                dst.Name = over.PropName;
                dst.InternalKey = over.InternalKey;
                dst.Type = UfbxPropType.Unknown;
                dst.Flags = UfbxPropFlags.Overridden;
                dst.ValueStr = over.ValueStr;
                dst.ValueBlob = string.IsNullOrEmpty(over.ValueStr)
                    ? null
                    : UfbxiRawStr.ToBytes(over.ValueStr, 0, over.ValueStr.Length);
                dst.ValueInt = over.ValueInt;
                dst.ValueVec4 = over.Value;
                iter.OverIx++;
                if (cmp == 0) {
                    iter.PropIx++;
                }
                prop = dst;
                return true;
            } else {
                prop = iter.Props[iter.PropIx++];
                return true;
            }
        }

        // C: ufbxi_next_prop (ufbx.c:25924-25932).
        static bool NextProp(PropIter iter, out UfbxProp prop)
        {
            if (iter.OverIx == iter.OverEnd) {
                prop = default;
                if (iter.PropIx == iter.PropEnd) return false;
                prop = iter.Props[iter.PropIx++];
                return true;
            } else {
                return NextPropSlow(iter, out prop);
            }
        }

        // C: ufbxi_evaluate_selected_props (ufbx.c:25934-25982).
        static UfbxProps EvaluateSelectedProps(UfbxAnim anim, UfbxElement element, double time,
            UfbxProp[] props, string[] propNames, int maxProps, uint flags)
        {
            string name = propNames[0];
            uint key = UfbxiProperties.GetNameKey(name, name.Length);
            int numProps = 0;

            int nameIx = 0;

            PropIter iter = InitPropIter(anim, element);
            UfbxProp prop;
            while (NextProp(iter, out prop)) {
                while (nameIx < maxProps) {
                    if (key > prop.InternalKey) break;
                    if (UfbxiStr.Equal(name, prop.Name)) {
                        if ((prop.Flags & UfbxPropFlags.Connected) != 0 && !anim.IgnoreConnections) {
                            UfbxProp dst = prop;
                            numProps++;
                            EvaluateConnectedProp(ref dst, anim, element, name, time, flags);
                            props[numProps - 1] = dst;
                        } else if ((prop.Flags & (UfbxPropFlags.Animated | UfbxPropFlags.Overridden)) != 0) {
                            props[numProps++] = prop;
                        }
                        break;
                    } else if (UfbxiProperties.Strcmp(name, prop.Name) < 0) {
                        nameIx++;
                        if (nameIx < maxProps) {
                            name = propNames[nameIx];
                            key = UfbxiProperties.GetNameKey(name, name.Length);
                        }
                    } else {
                        break;
                    }
                }
            }

            EvaluateProps(anim, element, time, props, numProps, flags);

            UfbxProp[] result = new UfbxProp[numProps];
            Array.Copy(props, result, numProps);
            UfbxProps propList = new UfbxProps();
            propList.Props = result;
            propList.NumAnimated = numProps;
            propList.Defaults = element.Props;
            return propList;
        }

        // ==================================================================
        // Public entry points (ufbx.c:30959-31184)
        // ==================================================================

        // C: ufbx_evaluate_prop_flags_len (ufbx.c:30964-30997).
        internal static UfbxProp EvaluatePropFlagsLen(UfbxAnim anim, UfbxElement element,
            string name, int nameLen, double time, uint flags)
        {
            UfbxProp result;
            bool found = FindPropLen(element.Props, name, nameLen, out result);
            UfbxProp foundProp = result;
            if (!found) {
                result = default;
                result.Name = nameLen == name.Length ? name : name.Substring(0, nameLen);
                result.InternalKey = UfbxiProperties.GetNameKey(name, nameLen);
                result.Flags = UfbxPropFlags.NotFound;
                result.ValueStr = string.Empty;   // C: ufbxi_empty_char
                result.ValueBlob = null;          // C: ufbx_empty_blob
            }

            if (anim.PropOverrides != null && anim.PropOverrides.Length > 0) {
                FindPropOverride(anim.PropOverrides, element.ElementId, ref result);
                return result;
            }

            if ((result.Flags & (UfbxPropFlags.Animated | UfbxPropFlags.Connected)) == 0) return result;

            if ((foundProp.Flags & UfbxPropFlags.Connected) != 0 && !anim.IgnoreConnections) {
                EvaluateConnectedProp(ref result, anim, element, foundProp.Name, time, flags);
            }

            UfbxProp[] single = new UfbxProp[1];
            single[0] = result;
            EvaluateProps(anim, element, time, single, 1, flags);

            return single[0];
        }

        // C: ufbx_evaluate_prop_len (ufbx.c:30959-30962).
        internal static UfbxProp EvaluatePropLen(UfbxAnim anim, UfbxElement element,
            string name, int nameLen, double time)
        {
            return EvaluatePropFlagsLen(anim, element, name, nameLen, time, 0);
        }

        // C: ufbx_evaluate_prop (ufbx.c:33163).
        internal static UfbxProp EvaluateProp(UfbxAnim anim, UfbxElement element, string name, double time)
        {
            return EvaluatePropFlagsLen(anim, element, name, name != null ? name.Length : 0, time, 0);
        }

        // C: ufbx_evaluate_props_flags (ufbx.c:31004-31031). `buffer` is filled in place up to
        // its length, exactly like C fills `buffer_size` slots.
        internal static UfbxProps EvaluatePropsFlags(UfbxAnim anim, UfbxElement element, double time,
            UfbxProp[] buffer, uint flags)
        {
            UfbxProps ret = new UfbxProps();
            ret.Props = Array.Empty<UfbxProp>();
            if (element == null) return ret;

            int numAnim = 0;
            PropIter iter = InitPropIter(anim, element);
            UfbxProp prop;
            while (NextProp(iter, out prop)) {
                if ((prop.Flags & (UfbxPropFlags.Animated | UfbxPropFlags.Overridden | UfbxPropFlags.Connected)) == 0) continue;
                if (numAnim >= buffer.Length) break;

                UfbxProp dst = prop;
                buffer[numAnim++] = dst;

                if ((prop.Flags & UfbxPropFlags.Connected) != 0 && !anim.IgnoreConnections) {
                    EvaluateConnectedProp(ref dst, anim, element, prop.Name, time, flags);
                    buffer[numAnim - 1] = dst;
                }
            }

            EvaluateProps(anim, element, time, buffer, numAnim, flags);

            UfbxProp[] result = new UfbxProp[numAnim];
            Array.Copy(buffer, result, numAnim);
            ret.Props = result;
            ret.NumAnimated = numAnim;
            ret.Defaults = element.Props;
            return ret;
        }

        // C: ufbx_evaluate_props (ufbx.c:30999-31002).
        internal static UfbxProps EvaluatePropsPublic(UfbxAnim anim, UfbxElement element, double time,
            UfbxProp[] buffer)
        {
            return EvaluatePropsFlags(anim, element, time, buffer, 0);
        }

        // C: ufbxi_transform_props_all / _rotation / _scale / _rotation_scale
        // (ufbx.c:31038-31068). NOTE: C requires these lists to be sorted by name (asserted under
        // UFBX_REGRESSION at 25941); the order below is the ufbx.c order, which is that order.
        static readonly string[] TransformPropsAll = {
            UfbxiStrings.Lcl_Rotation,
            UfbxiStrings.Lcl_Scaling,
            UfbxiStrings.Lcl_Translation,
            UfbxiStrings.PostRotation,
            UfbxiStrings.PreRotation,
            UfbxiStrings.RotationOffset,
            UfbxiStrings.RotationOrder,
            UfbxiStrings.RotationPivot,
            UfbxiStrings.ScalingOffset,
            UfbxiStrings.ScalingPivot,
        };

        static readonly string[] TransformPropsRotation = {
            UfbxiStrings.Lcl_Rotation,
            UfbxiStrings.PostRotation,
            UfbxiStrings.PreRotation,
            UfbxiStrings.RotationOrder,
        };

        static readonly string[] TransformPropsScale = {
            UfbxiStrings.Lcl_Scaling,
        };

        static readonly string[] TransformPropsRotationScale = {
            UfbxiStrings.Lcl_Rotation,
            UfbxiStrings.Lcl_Scaling,
            UfbxiStrings.PostRotation,
            UfbxiStrings.PreRotation,
            UfbxiStrings.RotationOrder,
        };

        // C: ufbx_evaluate_transform_flags (ufbx.c:31070-31168).
        internal static UfbxTransform EvaluateTransformFlags(UfbxAnim anim, UfbxNode node,
            double time, uint flags)
        {
            if (node == null) return UfbxTransform.Identity;
            if (anim == null) return node.LocalTransform;
            if (node.IsRoot) return node.LocalTransform;

            if ((flags & (uint)UfbxTransformFlags.ExplicitIncludes) == 0) {
                flags |= (uint)UfbxTransformFlags.IncludeRotation
                    | (uint)UfbxTransformFlags.IncludeScale
                    | (uint)UfbxTransformFlags.IncludeTranslation;
            }

            string[] propNames = TransformPropsAll;
            int numPropNames = TransformPropsAll.Length;
            uint components = flags & ((uint)UfbxTransformFlags.IncludeRotation
                | (uint)UfbxTransformFlags.IncludeScale
                | (uint)UfbxTransformFlags.IncludeTranslation);
            if (components == ((uint)UfbxTransformFlags.IncludeRotation | (uint)UfbxTransformFlags.IncludeScale)) {
                propNames = TransformPropsRotationScale;
                numPropNames = TransformPropsRotationScale.Length;
            } else if (components == (uint)UfbxTransformFlags.IncludeRotation) {
                propNames = TransformPropsRotation;
                numPropNames = TransformPropsRotation.Length;
            } else if (components == (uint)UfbxTransformFlags.IncludeScale) {
                propNames = TransformPropsScale;
                numPropNames = TransformPropsScale.Length;
            } else if (components == 0) {
                return UfbxTransform.Identity;
            }

            bool hasTranslationScale = false;
            UfbxVec3 translationScale = default;
            UfbxVec3 scaleFactor = new UfbxVec3(1.0, 1.0, 1.0);
            bool useScaleFactor = false;

            if (node.Parent != null
                && (flags & ((uint)UfbxTransformFlags.IncludeScale | (uint)UfbxTransformFlags.IncludeTranslation)) != 0) {
                UfbxNode parent = node.Parent;

                if ((flags & (uint)UfbxTransformFlags.IgnoreComponentwiseScale) == 0 && parent.InheritScaleNode != null) {
                    UfbxNode p = parent.InheritScaleNode;

                    if (node.IsScaleHelper) {
                        useScaleFactor = true;
                    }

                    while (p != null && p.ScaleHelper != null) {
                        UfbxProp scale = EvaluateProp(anim, p.ScaleHelper, UfbxiStrings.Lcl_Scaling, time);
                        scaleFactor.X *= scale.ValueVec3.X;
                        scaleFactor.Y *= scale.ValueVec3.Y;
                        scaleFactor.Z *= scale.ValueVec3.Z;
                        p = p.InheritScaleNode;
                    }
                }

                if (parent.ScaleHelper != null && (flags & (uint)UfbxTransformFlags.IgnoreScaleHelper) == 0) {
                    UfbxProp helperScale = EvaluateProp(anim, parent.ScaleHelper, UfbxiStrings.Lcl_Scaling, time);
                    if ((helperScale.Flags & UfbxPropFlags.NotFound) != 0) {
                        helperScale.ValueVec3 = new UfbxVec3(1.0, 1.0, 1.0);
                    }
                    helperScale.ValueVec3.X *= scaleFactor.X;
                    helperScale.ValueVec3.Y *= scaleFactor.Y;
                    helperScale.ValueVec3.Z *= scaleFactor.Z;
                    translationScale = helperScale.ValueVec3;
                    hasTranslationScale = true;
                }
            }

            uint evalFlags = 0;
            if ((flags & (uint)UfbxTransformFlags.NoExtrapolation) != 0) {
                evalFlags |= (uint)UfbxEvaluateFlags.NoExtrapolation;
            }

            UfbxProp[] buf = new UfbxProp[TransformPropsAll.Length];
            UfbxProps props = EvaluateSelectedProps(anim, node, time, buf, propNames, numPropNames, evalFlags);
            UfbxRotationOrder order = (UfbxRotationOrder)UfbxiProperties.FindEnum(props,
                UfbxiStrings.RotationOrder, (long)UfbxRotationOrder.Xyz, (long)UfbxRotationOrder.Spheric);

            UfbxTransform transform = default;
            if ((components & (uint)UfbxTransformFlags.IncludeTranslation) != 0) {
                transform = UfbxiSceneUpdate.GetTransform(props, order, node,
                    hasTranslationScale ? (UfbxVec3?)translationScale : null);
            } else {
                transform.Translation = UfbxVec3.Zero;
                if ((components & (uint)UfbxTransformFlags.IncludeRotation) != 0) {
                    transform.Rotation = UfbxiSceneUpdate.GetRotation(props, order, node);
                } else {
                    transform.Rotation = UfbxQuat.Identity;
                }
                if ((components & (uint)UfbxTransformFlags.IncludeScale) != 0) {
                    transform.Scale = UfbxiSceneUpdate.GetScale(props, node);
                } else {
                    transform.Scale = new UfbxVec3(1.0, 1.0, 1.0);
                }
            }

            if (useScaleFactor) {
                transform.Scale.X *= scaleFactor.X;
                transform.Scale.Y *= scaleFactor.Y;
                transform.Scale.Z *= scaleFactor.Z;
            }
            return transform;
        }

        // C: ufbx_evaluate_transform (ufbx.c:31033-31036).
        internal static UfbxTransform EvaluateTransform(UfbxAnim anim, UfbxNode node, double time)
        {
            return EvaluateTransformFlags(anim, node, time, 0);
        }

        // C: ufbx_evaluate_blend_weight_flags (ufbx.c:31175-31184).
        internal static double EvaluateBlendWeightFlags(UfbxAnim anim, UfbxBlendChannel channel,
            double time, uint flags)
        {
            string[] propNames = { UfbxiStrings.DeformPercent };

            UfbxProp[] buf = new UfbxProp[1];
            UfbxProps props = EvaluateSelectedProps(anim, channel, time, buf, propNames, propNames.Length, flags);
            return UfbxiProperties.FindReal(props, UfbxiStrings.DeformPercent, channel.Weight * 100.0) * 0.01;
        }

        // C: ufbx_evaluate_blend_weight (ufbx.c:31170-31173).
        internal static double EvaluateBlendWeight(UfbxAnim anim, UfbxBlendChannel channel, double time)
        {
            return EvaluateBlendWeightFlags(anim, channel, time, 0);
        }
    }

    // C: the public `ufbx_evaluate_*` entry points and the property/element lookup group
    // (`ufbx_find_*`, `ufbx_get_*`) they belong to in ufbx.c: the evaluation section
    // (30643-30833, 30835-31184) and its NUL-terminated forwarders (33150-33168), plus the
    // material/shader/pose lookups at 31413-31484. Pure forwarding over the `Parse/**` internals.
    public static class UfbxEvaluate
    {
        // C: ufbx_evaluate_curve (ufbx.c:30835-30838).
        public static double Curve(UfbxAnimCurve curve, double time, double defaultValue)
            => UfbxiEvaluate.EvaluateCurve(curve, time, defaultValue);

        // C: ufbx_evaluate_curve_flags (ufbx.c:30840-30922).
        public static double CurveFlags(UfbxAnimCurve curve, double time, double defaultValue, uint flags)
            => UfbxiEvaluate.EvaluateCurveFlags(curve, time, defaultValue, flags);

        // C: ufbx_evaluate_anim_value_real (ufbx.c:30924-30927).
        public static double AnimValueReal(UfbxAnimValue animValue, double time)
            => UfbxiEvaluate.EvaluateAnimValueReal(animValue, time);

        // C: ufbx_evaluate_anim_value_real_flags (ufbx.c:30934-30943).
        public static double AnimValueRealFlags(UfbxAnimValue animValue, double time, uint flags)
            => UfbxiEvaluate.EvaluateAnimValueRealFlags(animValue, time, flags);

        // C: ufbx_evaluate_anim_value_vec3 (ufbx.c:30929-30932).
        public static UfbxVec3 AnimValueVec3(UfbxAnimValue animValue, double time)
            => UfbxiEvaluate.EvaluateAnimValueVec3(animValue, time);

        // C: ufbx_evaluate_anim_value_vec3_flags (ufbx.c:30945-30957).
        public static UfbxVec3 AnimValueVec3Flags(UfbxAnimValue animValue, double time, uint flags)
            => UfbxiEvaluate.EvaluateAnimValueVec3Flags(animValue, time, flags);

        // C: ufbx_evaluate_prop (ufbx.c:33163).
        public static UfbxProp Prop(UfbxAnim anim, UfbxElement element, string name, double time)
            => UfbxiEvaluate.EvaluateProp(anim, element, name, time);

        // C: ufbx_evaluate_prop_len (ufbx.c:30959-30962).
        public static UfbxProp PropLen(UfbxAnim anim, UfbxElement element, string name, int nameLen, double time)
            => UfbxiEvaluate.EvaluatePropLen(anim, element, name, nameLen, time);

        // C: ufbx_evaluate_prop_flags (ufbx.c:33164).
        public static UfbxProp PropFlags(UfbxAnim anim, UfbxElement element, string name, double time, uint flags)
            => UfbxiEvaluate.EvaluatePropFlagsLen(anim, element, name, name != null ? name.Length : 0, time, flags);

        // C: ufbx_evaluate_prop_flags_len (ufbx.c:30964-30997).
        public static UfbxProp PropFlagsLen(UfbxAnim anim, UfbxElement element, string name, int nameLen,
            double time, uint flags)
            => UfbxiEvaluate.EvaluatePropFlagsLen(anim, element, name, nameLen, time, flags);

        // C: ufbx_evaluate_props (ufbx.c:30999-31002).
        public static UfbxProps Props(UfbxAnim anim, UfbxElement element, double time, UfbxProp[] buffer)
            => UfbxiEvaluate.EvaluatePropsPublic(anim, element, time, buffer);

        // C: ufbx_evaluate_props_flags (ufbx.c:31004-31031).
        public static UfbxProps PropsFlags(UfbxAnim anim, UfbxElement element, double time,
            UfbxProp[] buffer, uint flags)
            => UfbxiEvaluate.EvaluatePropsFlags(anim, element, time, buffer, flags);

        // C: ufbx_evaluate_transform (ufbx.c:31033-31036).
        public static UfbxTransform Transform(UfbxAnim anim, UfbxNode node, double time)
            => UfbxiEvaluate.EvaluateTransform(anim, node, time);

        // C: ufbx_evaluate_transform_flags (ufbx.c:31070-31168).
        public static UfbxTransform TransformFlags(UfbxAnim anim, UfbxNode node, double time, uint flags)
            => UfbxiEvaluate.EvaluateTransformFlags(anim, node, time, flags);

        // C: ufbx_evaluate_blend_weight (ufbx.c:31170-31173).
        public static double BlendWeight(UfbxAnim anim, UfbxBlendChannel channel, double time)
            => UfbxiEvaluate.EvaluateBlendWeight(anim, channel, time);

        // C: ufbx_evaluate_blend_weight_flags (ufbx.c:31175-31184).
        public static double BlendWeightFlags(UfbxAnim anim, UfbxBlendChannel channel, double time, uint flags)
            => UfbxiEvaluate.EvaluateBlendWeightFlags(anim, channel, time, flags);

        // C: ufbx_find_prop (ufbx.c:33157).
        public static bool FindProp(UfbxProps props, string name, out UfbxProp prop)
            => UfbxiEvaluate.FindProp(props, name, out prop);

        // C: ufbx_find_prop_len (ufbx.c:30643-30658).
        public static bool FindPropLen(UfbxProps props, string name, int nameLen, out UfbxProp prop)
            => UfbxiEvaluate.FindPropLen(props, name, nameLen, out prop);

        // C: ufbx_find_real_len (ufbx.c:30660-30668).
        public static double FindRealLen(UfbxProps props, string name, int nameLen, double def)
        {
            UfbxProp prop;
            if (FindPropLen(props, name, nameLen, out prop)) return prop.ValueReal;
            return def;
        }

        // C: ufbx_find_real (ufbx.c:33155).
        public static double FindReal(UfbxProps props, string name, double def)
            => FindRealLen(props, name, name != null ? name.Length : 0, def);

        // C: ufbx_find_vec3_len (ufbx.c:30670-30678).
        public static UfbxVec3 FindVec3Len(UfbxProps props, string name, int nameLen, UfbxVec3 def)
        {
            UfbxProp prop;
            if (FindPropLen(props, name, nameLen, out prop)) return prop.ValueVec3;
            return def;
        }

        // C: ufbx_find_int_len (ufbx.c:30680-30688).
        public static long FindIntLen(UfbxProps props, string name, int nameLen, long def)
        {
            UfbxProp prop;
            if (FindPropLen(props, name, nameLen, out prop)) return prop.ValueInt;
            return def;
        }

        // C: ufbx_find_bool_len (ufbx.c:30690-30698).
        public static bool FindBoolLen(UfbxProps props, string name, int nameLen, bool def)
        {
            UfbxProp prop;
            if (FindPropLen(props, name, nameLen, out prop)) return prop.ValueInt != 0;
            return def;
        }

        // C: ufbx_find_string_len (ufbx.c:30700-30708).
        public static string FindStringLen(UfbxProps props, string name, int nameLen, string def)
        {
            UfbxProp prop;
            if (FindPropLen(props, name, nameLen, out prop)) return prop.ValueStr;
            return def;
        }

        // C: ufbx_find_blob_len (ufbx.c:30710-30718).
        public static byte[] FindBlobLen(UfbxProps props, string name, int nameLen, byte[] def)
        {
            UfbxProp prop;
            if (FindPropLen(props, name, nameLen, out prop)) return prop.ValueBlob;
            return def;
        }

        // The `strlen(name)` forms of the accessors above (ufbx.c:33151-33156).

        // C: ufbx_find_vec3 (ufbx.c:33152).
        public static UfbxVec3 FindVec3(UfbxProps props, string name, UfbxVec3 def)
            => FindVec3Len(props, name, name != null ? name.Length : 0, def);

        // C: ufbx_find_int (ufbx.c:33153).
        public static long FindInt(UfbxProps props, string name, long def)
            => FindIntLen(props, name, name != null ? name.Length : 0, def);

        // C: ufbx_find_bool (ufbx.c:33154).
        public static bool FindBool(UfbxProps props, string name, bool def)
            => FindBoolLen(props, name, name != null ? name.Length : 0, def);

        // C: ufbx_find_string (ufbx.c:33155).
        public static string FindString(UfbxProps props, string name, string def)
            => FindStringLen(props, name, name != null ? name.Length : 0, def);

        // C: ufbx_find_blob (ufbx.c:33156).
        public static byte[] FindBlob(UfbxProps props, string name, byte[] def)
            => FindBlobLen(props, name, name != null ? name.Length : 0, def);

        // ==================================================================
        // Element and scene lookup (ufbx.c:30720-30833, 31413-31484, 33157-33168)
        // ==================================================================

        // C: ufbx_find_prop_concat (ufbx.c:30720-30736). C takes `ufbx_string parts[num_parts]` and
        // returns `NULL` on a miss; the port's miss value is `default(UfbxProp)`, whose `Name` is
        // null -- the same test the internal call sites use (Parse/SceneUpdate.cs:940-951).
        public static bool FindPropConcat(UfbxProps props, string[] parts, int numParts, out UfbxProp prop)
        {
            UfbxiConcatPart[] concat = new UfbxiConcatPart[numParts];
            for (int i = 0; i < numParts; i++) {
                concat[i] = new UfbxiConcatPart(parts != null && i < parts.Length ? parts[i] : null);
            }
            prop = UfbxiSceneFinalize.FindPropConcat(props, concat, numParts);
            return prop.Name != null;
        }

        // C: ufbx_get_prop_element (ufbx.c:30751-30756). C passes a `ufbx_prop*` but reads only
        // `prop->name.data`, so the port takes the value.
        public static UfbxElement GetPropElement(UfbxElement element, UfbxProp prop, UfbxElementType type)
            => UfbxiSceneFinalize.GetPropElement(element, prop, type);

        // C: ufbx_find_prop_element_len (ufbx.c:30758-30766).
        public static UfbxElement FindPropElementLen(UfbxElement element, string name, int nameLen, UfbxElementType type)
            => UfbxiSceneFind.FindPropElementLen(element, name, nameLen, type);

        // C: ufbx_find_prop_element (ufbx.c:33157).
        public static UfbxElement FindPropElement(UfbxElement element, string name, UfbxElementType type)
            => FindPropElementLen(element, name, name != null ? name.Length : 0, type);

        // C: ufbx_find_element_len (ufbx.c:30738-30749).
        public static UfbxElement FindElementLen(UfbxScene scene, UfbxElementType type, string name, int nameLen)
            => UfbxiSceneFind.FindElementLen(scene, type, name, nameLen);

        // C: ufbx_find_element (ufbx.c:33158).
        public static UfbxElement FindElement(UfbxScene scene, UfbxElementType type, string name)
            => FindElementLen(scene, type, name, name != null ? name.Length : 0);

        // C: ufbx_find_node_len (ufbx.c:30768-30771).
        public static UfbxNode FindNodeLen(UfbxScene scene, string name, int nameLen)
            => UfbxiSceneFind.FindNodeLen(scene, name, nameLen);

        // C: ufbx_find_node (ufbx.c:33159).
        public static UfbxNode FindNode(UfbxScene scene, string name)
            => FindNodeLen(scene, name, name != null ? name.Length : 0);

        // C: ufbx_find_anim_stack_len (ufbx.c:30773-30776).
        public static UfbxAnimStack FindAnimStackLen(UfbxScene scene, string name, int nameLen)
            => UfbxiSceneFind.FindAnimStackLen(scene, name, nameLen);

        // C: ufbx_find_anim_stack (ufbx.c:33160).
        public static UfbxAnimStack FindAnimStack(UfbxScene scene, string name)
            => FindAnimStackLen(scene, name, name != null ? name.Length : 0);

        // C: ufbx_find_material_len (ufbx.c:30778-30781).
        public static UfbxMaterial FindMaterialLen(UfbxScene scene, string name, int nameLen)
            => UfbxiSceneFind.FindMaterialLen(scene, name, nameLen);

        // C: ufbx_find_material (ufbx.c:33161).
        public static UfbxMaterial FindMaterial(UfbxScene scene, string name)
            => FindMaterialLen(scene, name, name != null ? name.Length : 0);

        // C: ufbx_find_anim_prop_len (ufbx.c:30783-30798).
        public static UfbxAnimProp FindAnimPropLen(UfbxAnimLayer layer, UfbxElement element, string prop, int propLen)
            => UfbxiSceneFind.FindAnimPropLen(layer, element, prop, propLen);

        // C: ufbx_find_anim_prop (ufbx.c:33162).
        public static UfbxAnimProp FindAnimProp(UfbxAnimLayer layer, UfbxElement element, string prop)
            => FindAnimPropLen(layer, element, prop, prop != null ? prop.Length : 0);

        // C: ufbx_find_anim_props (ufbx.c:30800-30820). C returns a view into `layer->anim_props`;
        // the port returns a copied slice (`null` == C's `{ NULL, 0 }`), as the other list-valued
        // lookups in this file already do.
        public static UfbxAnimProp[] FindAnimProps(UfbxAnimLayer layer, UfbxElement element)
            => UfbxiSceneFind.FindAnimProps(layer, element);

        // C: ufbx_get_compatible_matrix_for_normals (ufbx.c:30822-30833).
        public static UfbxMatrix GetCompatibleMatrixForNormals(UfbxNode node)
            => UfbxiSceneFind.GetCompatibleMatrixForNormals(node);

        // C: ufbx_find_prop_texture_len (ufbx.c:31422-31431).
        public static UfbxTexture FindPropTextureLen(UfbxMaterial material, string name, int nameLen)
            => UfbxiSceneFinalize.FindPropTexture(material, UfbxiSceneFind.SafeString(name, nameLen));

        // C: ufbx_find_prop_texture (ufbx.c:33165).
        public static UfbxTexture FindPropTexture(UfbxMaterial material, string name)
            => FindPropTextureLen(material, name, name != null ? name.Length : 0);

        // C: ufbx_find_shader_prop_bindings_len (ufbx.c:31442-31469).
        public static UfbxShaderPropBinding[] FindShaderPropBindingsLen(UfbxShader shader, string name, int nameLen)
            => UfbxiSceneFinalize.FindShaderPropBindings(shader, UfbxiSceneFind.SafeString(name, nameLen));

        // C: ufbx_find_shader_prop_bindings (ufbx.c:33167).
        public static UfbxShaderPropBinding[] FindShaderPropBindings(UfbxShader shader, string name)
            => FindShaderPropBindingsLen(shader, name, name != null ? name.Length : 0);

        // C: ufbx_find_shader_prop_len (ufbx.c:31433-31440). The first binding's material property,
        // or the empty string when no binding matches.
        public static string FindShaderPropLen(UfbxShader shader, string name, int nameLen)
        {
            UfbxShaderPropBinding[] bindings = FindShaderPropBindingsLen(shader, name, nameLen);
            if (bindings != null && bindings.Length > 0) return bindings[0].MaterialProp;
            return string.Empty;
        }

        // C: ufbx_find_shader_prop (ufbx.c:33166).
        public static string FindShaderProp(UfbxShader shader, string name)
            => FindShaderPropLen(shader, name, name != null ? name.Length : 0);

        // C: ufbx_find_shader_texture_input_len (ufbx.c:31471-31484).
        public static UfbxShaderTextureInput FindShaderTextureInputLen(UfbxShaderTexture shader, string name, int nameLen)
            => UfbxiSceneFinalize.FindShaderTextureInput(shader, UfbxiSceneFind.SafeString(name, nameLen));

        // C: ufbx_find_shader_texture_input (ufbx.c:31471 body via 33168).
        public static UfbxShaderTextureInput FindShaderTextureInput(UfbxShaderTexture shader, string name)
            => FindShaderTextureInputLen(shader, name, name != null ? name.Length : 0);

        // C: ufbx_get_bone_pose (ufbx.c:31413-31420).
        public static UfbxBonePose GetBonePose(UfbxPose pose, UfbxNode node)
            => UfbxiSceneFinalize.GetBonePose(pose, node);
    }
}
