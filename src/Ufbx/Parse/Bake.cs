// Animation baking for ufbx v0.23.1: the `ufbxi_bake_*` chain and the baked-animation public
// surface, exposed through Api/UfbxBakeApi.cs.
//
// Ported ranges (ufbx.c):
//   ufbxi_baked_anim_imp / bake context / bake prop     26680-26738
//   ufbxi_bake_prop_less / cmp_bake_time                26740-26762
//   ufbxi_bake_push_time / ufbxi_bake_times             26764-26821
//   ufbxi_transform_props and the three prop lists      26823-26838
//   ufbxi_in_list / sort_bake_times / finalize_bake_times 26840-26976
//   ufbxi_add/sub_epsilon / postprocess_step            26978-27023
//   ufbxi_bake_postprocess_vec3 / _quat                 27025-27207
//   ufbxi_bake_time_sample_time / push_resampled_times  27209-27239
//   ufbxi_bake_node_imp / ufbxi_bake_node               27241-27513
//   ufbxi_bake_anim_prop / ufbxi_bake_element           27515-27593
//   ufbxi_baked_node_less / baked_element_less / bake_anim 27595-27713
//   ufbxi_bake_anim_imp                                 27715-27773
//   ufbx_bake_anim                                      31250-31297
//   ufbx_retain_baked_anim / ufbx_free_baked_anim       31299-31317
//   ufbx_find_baked_node[_by_typed_id] / _element[...]  31320-31346
//   ufbx_evaluate_baked_vec3 / _quat                    31348-31411
//
// Configuration:
//   `UFBXI_FEATURE_ANIMATION_BAKING` is 1 in the reference build (ufbx.c:92-94: enabled unless
//   `UFBX_MINIMAL` or `UFBX_NO_ANIMATION_BAKING`; the `#else` tail of ufbx_bake_anim at
//   31289-31296 that reports `UFBX_ENABLE_ANIMATION_BAKING`/"Feature disabled" is therefore dead
//   in every oracle build, so the port always compiles the chain in and has no such call site.
//
// Port shape (PORTING_NOTES.md #4: no arenas, no manual result memory):
//   * The nine `ufbxi_buf` members and the two `ufbxi_allocator`s become managed lists and arrays.
//     Every `ufbxi_check_err(&bc->error, ptr)` in this chain guards an *allocation* result, and
//     managed allocation cannot fail quietly (it throws), so `bc->error` keeps type
//     UFBX_ERROR_NONE and `ufbxi_bake_anim_imp()` always returns 1. The failure tail
//     `ufbxi_fix_error_type(&bc->error, "Failed to bake anim", error)` (31284) is still written
//     out, as the counterpart of the C shape, exactly like UfbxApi.FreeScene().
//   * `ufbxi_baked_anim_imp` (26680-26684: refcount header + `magic`) has no counterpart, so
//     ufbx_retain_baked_anim()/ufbx_free_baked_anim() are the usual no-ops of that family.
//   * `ufbx_baked_anim_metadata` is filled from `ator_result.current_size`/`num_allocs` and their
//     temp counterparts (31262-31265 == 27762-27765); there is no allocator to measure, so the
//     port leaves the struct zeroed and the differential does not compare those four fields.
//   * `bc->baked_nodes` is a `ufbx_baked_node*` array indexed by `typed_id` and `nodes_to_bake` a
//     `bool` array of the same length (27615-27618). `UfbxBakedNode` is a value type here, so the
//     port stores an index into `tmp_nodes` and uses -1 for C's NULL. Neither array exists when
//     `skip_node_transforms` is set, which is the `bc->baked_nodes == NULL` state of C.
//   * `bc->tmp_prop` is the per-node scratch from which C carves several typed blocks (the three
//     time lists and the three key arrays) and which it `ufbxi_buf_clear()`s at the end of the
//     node (27473, 27551). Nothing reads those blocks after the clear, so the port hands out local
//     arrays per call and has no clear step.
//   * PORTING_NOTES.md #9: `bc->opts = *opts` (31260-31262) and `ufbxi_bake_anim_imp()` then writes
//     five defaults into that copy (27717-27721), so the caller's opts are value-copied here;
//     aliasing them would permanently change the caller's object.
//   * `ufbxi_grow_array(&bc->ator_tmp, &bc->tmp_arr, &bc->tmp_arr_size, ...)` (26850) only has to
//     provide at least `count` scratch slots for the stable sort; the capacity is unobservable.

using System;
using System.Collections.Generic;

namespace Ufbx
{
    // C: ufbxi_bake_time (ufbx.c:26688-26691)
    internal struct UfbxiBakeTime
    {
        public double Time;
        public uint Flags;
    }

    // C: ufbxi_bake_prop (ufbx.c:26733-26738)
    internal sealed class UfbxiBakeProp
    {
        public uint SortId;           // C: sort_id -- typed_id for nodes, UINT32_MAX for elements
        public uint ElementId;        // C: element_id
        public string PropName;      // C: const char *prop_name (interned name)
        public UfbxAnimValue AnimValue; // C: ufbx_anim_value *anim_value
    }

    // C: ufbxi_bake_context (ufbx.c:26695-26731)
    internal sealed class UfbxiBakeContext
    {
        // C: `ufbx_error error` of the `ufbxi_bake_context bc = { UFBX_ERROR_NONE }` (ufbx.c:31259).
        public UfbxError Error = new UfbxError();

        public UfbxScene Scene;       // C: scene
        public UfbxAnim Anim;         // C: anim
        public UfbxBakeOpts Opts;     // C: opts (the value copy of the caller's)

        // C: ufbxi_buf tmp_times (26703) -- the pending times of the element being baked.
        public readonly List<UfbxiBakeTime> TmpTimes = new List<UfbxiBakeTime>();

        // C: ufbxi_bake_time_list layer_weight_times (26710)
        public UfbxiBakeTime[] LayerWeightTimes = Array.Empty<UfbxiBakeTime>();

        // C: ufbxi_buf tmp_bake_props (26704)
        public readonly List<UfbxiBakeProp> TmpBakeProps = new List<UfbxiBakeProp>();

        // C: ufbxi_buf tmp_nodes / tmp_elements / tmp_props (26705-26707)
        public readonly List<UfbxBakedNode> TmpNodes = new List<UfbxBakedNode>();
        public readonly List<UfbxBakedElement> TmpElements = new List<UfbxBakedElement>();
        public readonly List<UfbxBakedProp> TmpProps = new List<UfbxBakedProp>();

        // C: ufbxi_buf tmp_bake_stack (26708) -- element ids still to bake, LIFO.
        public readonly List<uint> TmpBakeStack = new List<uint>();

        // C: char *tmp_arr / size_t tmp_arr_size (26715-26716) -- stable-sort scratch.
        public UfbxiBakeTime[] SortTmp;

        // C: ufbx_baked_node **baked_nodes (26712) -- index into TmpNodes, -1 for NULL.
        public int[] BakedNodeIndex;

        // C: bool *nodes_to_bake (26713)
        public bool[] NodesToBake;

        // C: double ktime_offset (26722)
        public double KtimeOffset;

        // C: time_begin / time_end / time_min / time_max (26724-26727)
        public double TimeBegin;
        public double TimeEnd;
        public double TimeMin;
        public double TimeMax;

        // C: ufbx_baked_anim bake (26729)
        public UfbxBakedAnim Bake = new UfbxBakedAnim();
    }

    internal static class UfbxiBake
    {
        // C: ufbxi_transform_props[] (ufbx.c:26823-26826)
        static readonly string[] TransformProps = {
            UfbxiStrings.Lcl_Translation, UfbxiStrings.Lcl_Rotation, UfbxiStrings.Lcl_Scaling,
            UfbxiStrings.PreRotation, UfbxiStrings.PostRotation, UfbxiStrings.RotationOffset,
            UfbxiStrings.ScalingOffset, UfbxiStrings.RotationPivot, UfbxiStrings.ScalingPivot,
            UfbxiStrings.RotationOrder,
        };

        // C: ufbxi_complex_translation_props[] (ufbx.c:26828-26830)
        static readonly string[] ComplexTranslationProps = {
            UfbxiStrings.ScalingPivot, UfbxiStrings.RotationPivot, UfbxiStrings.RotationOffset,
            UfbxiStrings.ScalingOffset,
        };

        // C: ufbxi_complex_rotation_props[] (ufbx.c:26832-26834)
        static readonly string[] ComplexRotationProps = {
            UfbxiStrings.PreRotation, UfbxiStrings.PostRotation, UfbxiStrings.RotationOrder,
        };

        // C: ufbxi_complex_rotation_sources[] (ufbx.c:26836-26838)
        static readonly string[] ComplexRotationSources = {
            UfbxiStrings.Lcl_Rotation, UfbxiStrings.PreRotation, UfbxiStrings.PostRotation,
            UfbxiStrings.RotationOrder,
        };

        // C: UFBX_BAKED_KEY_* as the `uint32_t` the chain uses (ufbx.c:26751-26753 and the
        // keyframe/extrapolation flags at ufbx.h:4510ff).
        const uint KeyStepLeft = 0x1u;
        const uint KeyStepRight = 0x2u;
        const uint KeyStepKey = 0x4u;
        const uint KeyKeyframe = 0x8u;
        const uint KeyReduced = 0x10u;

        const uint FlagIgnoreScaleHelper = 0x1u;
        const uint FlagIgnoreComponentwiseScale = 0x2u;
        const uint FlagExplicitIncludes = 0x4u;
        const uint FlagIncludeTranslation = 0x10u;
        const uint FlagIncludeRotation = 0x20u;
        const uint FlagIncludeScale = 0x40u;
        const uint FlagNoExtrapolation = 0x80u;

        // C: UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION (ufbx.h:4996)
        const uint EvaluateNoExtrapolation = 0x1u;

        // C: UFBX_FLT_EPSILON (ufbx.c:351-356) == FLT_EPSILON == 2^-23. The literal is the
        // ufbx.c fallback form; the round-trip lands on the same float either way.
        const float FltEpsilon = 1.192092896e-07f;

        // C: ufbxi_bake_prop_less (ufbx.c:26740-26749). C compares the interned `prop_name`
        // pointers first and only then `strcmp()`s different ones; the port's canonical name
        // instances make value equality the same test, and CompareOrdinal over the
        // one-byte-per-char strings is C's unsigned byte comparison.
        static bool BakePropLess(object user, UfbxiBakeProp a, UfbxiBakeProp b)
        {
            if (a.SortId != b.SortId) return a.SortId < b.SortId;
            if (a.ElementId != b.ElementId) return a.ElementId < b.ElementId;
            if (a.PropName != b.PropName) return string.CompareOrdinal(a.PropName, b.PropName) < 0;
            return false;
        }

        // C: ufbxi_cmp_bake_time (ufbx.c:26754-26762): time order, then the bit-twiddled step
        // order `0x1 (LEFT) < 0x0 < 0x2 (RIGHT)`.
        static int CmpBakeTime(UfbxiBakeTime a, UfbxiBakeTime b)
        {
            if (a.Time != b.Time) return a.Time < b.Time ? -1 : 1;
            uint aStep = a.Flags & 0x3u, bStep = b.Flags & 0x3u;
            if (aStep != bStep) return (aStep ^ 0x1u) < (bStep ^ 0x1u) ? -1 : 1;
            return 0;
        }

        // C: ufbxi_bake_push_time (ufbx.c:26764-26771). `ufbxi_push_fast` can only fail out of
        // memory, so the port's version cannot report 0.
        static void BakePushTime(UfbxiBakeContext bc, double time, uint flags)
        {
            UfbxiBakeTime key = new UfbxiBakeTime();
            key.Time = time;
            key.Flags = flags;
            bc.TmpTimes.Add(key);
        }

        // C: ufbxi_bake_times (ufbx.c:26773-26821). Collects the bake times of one animated
        // property: every source keyframe, one extra time for each step transition, and a
        // uniform resampling sweep for cubic (or forced-linear) sections.
        static void BakeTimes(UfbxiBakeContext bc, UfbxAnimValue animValue, bool resampleLinear, uint keyFlag)
        {
            double sampleRate = bc.Opts.ResampleRate;
            double minDuration = bc.Opts.MinimumSampleRate > 0.0 ? 1.0 / bc.Opts.MinimumSampleRate : 0.0;

            for (int curveIx = 0; curveIx < 3; curveIx++) {
                UfbxAnimCurve curve = animValue.Curves[curveIx];
                if (curve == null) continue;

                UfbxKeyframe[] keys = curve.Keyframes ?? Array.Empty<UfbxKeyframe>();
                int numKeys = keys.Length;
                for (int keyIx = 0; keyIx < numKeys; keyIx++) {
                    UfbxKeyframe a = keys[keyIx];
                    double aTime = a.Time;
                    BakePushTime(bc, aTime, keyFlag);
                    if (keyIx + 1 >= numKeys) break;
                    UfbxKeyframe b = keys[keyIx + 1];
                    double bTime = b.Time;

                    // Skip fully flat sections
                    if (a.Value == b.Value && a.Right.Dy == 0.0f && b.Left.Dy == 0.0f) continue;

                    if (a.Interpolation == UfbxInterpolation.ConstantPrev) {
                        BakePushTime(bc, bTime, KeyStepLeft);
                    } else if (a.Interpolation == UfbxInterpolation.ConstantNext) {
                        BakePushTime(bc, aTime, KeyStepRight);
                    } else if ((resampleLinear || a.Interpolation == UfbxInterpolation.Cubic) && sampleRate > 0.0) {
                        double duration = bTime - aTime;
                        if (duration <= minDuration) continue;

                        double factor = 1.0;
                        while (duration * sampleRate / factor >= (double)bc.Opts.MaxKeyframeSegments) {
                            factor *= 2.0;
                        }

                        double padding = 0.5 / sampleRate;
                        double start = UfbxMath.Ceil((aTime + padding) * sampleRate / factor) * factor;
                        double stop = bTime - padding;
                        for (int i = 0; i < bc.Opts.MaxKeyframeSegments; i++) {
                            double time = (start + (double)i * factor) / sampleRate;
                            if (time >= stop) break;
                            BakePushTime(bc, time, 0);
                        }
                    }
                }
            }
        }

        // C: ufbxi_in_list (ufbx.c:26840-26846) -- pointer identity over the interned names.
        static bool InList(string[] items, string item)
        {
            for (int i = 0; i < items.Length; i++) {
                if (items[i] == item) return true;
            }
            return false;
        }

        // C: ufbxi_sort_bake_times (ufbx.c:26848-26853): stable sort, insertion block 32.
        static void SortBakeTimes(UfbxiBakeContext bc, UfbxiBakeTime[] times, int count)
        {
            if (bc.SortTmp == null || bc.SortTmp.Length < count) {
                bc.SortTmp = new UfbxiBakeTime[count];
            }
            UfbxiSort.StableSort(32, times, bc.SortTmp, count,
                (object user, UfbxiBakeTime a, UfbxiBakeTime b) => CmpBakeTime(a, b) < 0, null);
        }

        // C: ufbxi_finalize_bake_times (ufbx.c:26855-26976). Moves the pending times out of
        // `tmp_times`, sorts them, merges duplicates, culls over-dense resampled keys, applies
        // `maximum_sample_rate` and widens the time range. Returns the `ufbxi_bake_time_list`
        // that C writes to `p_dst`.
        static UfbxiBakeTime[] FinalizeBakeTimes(UfbxiBakeContext bc)
        {
            // C: 26857-26859 -- append the pre-baked layer weight times.
            if (bc.LayerWeightTimes.Length > 0) {
                for (int i = 0; i < bc.LayerWeightTimes.Length; i++) {
                    bc.TmpTimes.Add(bc.LayerWeightTimes[i]);
                }
            }

            // C: 26861-26864 -- an element with no keys at all gets the playback range.
            if (bc.TmpTimes.Count == 0) {
                BakePushTime(bc, bc.TimeBegin, 0);
                BakePushTime(bc, bc.TimeEnd, 0);
            }

            // C: 26866-26868 -- `ufbxi_push_pop(&bc->tmp_prop, &bc->tmp_times, ...)`: the items
            // move to the per-node scratch and `tmp_times` is empty again.
            int numTimes = bc.TmpTimes.Count;
            UfbxiBakeTime[] times = bc.TmpTimes.ToArray();
            bc.TmpTimes.Clear();

            SortBakeTimes(bc, times, numTimes);

            // Deduplicate times (C: 26872-26895)
            if (numTimes > 0) {
                int dst = 0;
                UfbxiBakeTime prev = times[0];
                for (int src = 1; src < numTimes; src++) {
                    UfbxiBakeTime next = times[src];
                    // Merge keys with the same time and step flags `(0x1, 0x2)`
                    if (next.Time == prev.Time) {
                        if (((next.Flags ^ prev.Flags) & 0x3u) == 0) {
                            prev.Flags |= next.Flags;
                            continue;
                        } else if ((prev.Flags & KeyStepLeft) != 0) {
                            next.Flags |= KeyStepKey;
                        } else if ((next.Flags & KeyStepRight) != 0) {
                            prev.Flags |= KeyStepKey;
                        }
                    }

                    times[dst++] = prev;
                    prev = next;
                }
                times[dst++] = prev;
                numTimes = dst;
            }

            // Cull too close resampled keys, these may arise during merging multiple times
            // (C: 26897-26918)
            if (numTimes > 0) {
                double minDist = 0.25 / bc.Opts.ResampleRate;
                uint keepFlags = KeyStepLeft | KeyStepRight | KeyStepKey | KeyKeyframe;

                int dst = 0;
                for (int src = 0; src < numTimes; src++) {
                    UfbxiBakeTime cur = times[src];
                    double delta = double.PositiveInfinity; // C: UFBX_INFINITY

                    bool keep = true;
                    if ((cur.Flags & keepFlags) == 0) {
                        if (dst > 0) delta = cur.Time - times[dst - 1].Time;
                        if (src + 1 < numTimes) delta = UfbxMath.FMin(delta, times[src + 1].Time - cur.Time);
                        if (delta < minDist) keep = false;
                    }
                    if (keep) {
                        times[dst++] = cur;
                    }
                }
                numTimes = dst;
            }

            // Enforce maximum sample rate (C: 26920-26965)
            if (bc.Opts.MaximumSampleRate > 0.0) {
                double epsilon = 0.0078125 / bc.Opts.MaximumSampleRate;
                double sampleRate = bc.Opts.MaximumSampleRate;
                double maxInterval = 1.0 / bc.Opts.MaximumSampleRate;
                double minInterval = 1.0 / bc.Opts.MaximumSampleRate - epsilon;
                int dst = 0, src = 0;

                // Pre-expand constant keyframes
                for (int i = 0; i < numTimes; i++) {
                    if ((times[i].Flags & (KeyStepLeft | KeyStepRight)) != 0) {
                        double sign = (times[i].Flags & KeyStepLeft) != 0 ? -1.0 : 1.0;
                        double time = times[i].Time + sign * maxInterval;
                        if (i > 0) time = UfbxMath.FMax(time, times[i - 1].Time);
                        if (i + 1 < numTimes) time = UfbxMath.FMin(time, times[i + 1].Time);
                        times[i].Time = time;
                        times[i].Flags = KeyReduced;
                    }
                }

                UfbxiBakeTime prevTime = new UfbxiBakeTime();
                prevTime.Time = double.NegativeInfinity; // C: { -UFBX_INFINITY }
                prevTime.Flags = 0;
                while (src < numTimes) {
                    UfbxiBakeTime srcTime = times[src];
                    src++;

                    int startSrc = src;
                    UfbxiBakeTime nextTime = new UfbxiBakeTime();
                    nextTime.Time = UfbxMath.Ceil(srcTime.Time * sampleRate - epsilon) / sampleRate;
                    nextTime.Flags = KeyReduced;
                    while (src < numTimes && times[src].Time <= nextTime.Time + epsilon) {
                        src++;
                    }

                    if (src != startSrc || srcTime.Time - prevTime.Time <= minInterval) {
                        prevTime = nextTime;
                    } else {
                        prevTime = srcTime;
                    }

                    if (dst == 0 || prevTime.Time > times[dst - 1].Time) {
                        times[dst++] = prevTime;
                    }
                }

                numTimes = dst;
            }

            // C: 26967-26970 -- widen the baked key time range.
            if (numTimes > 0) {
                if (times[0].Time < bc.TimeMin) bc.TimeMin = times[0].Time;
                if (times[numTimes - 1].Time > bc.TimeMax) bc.TimeMax = times[numTimes - 1].Time;
            }

            if (numTimes == times.Length) return times;
            UfbxiBakeTime[] trimmed = new UfbxiBakeTime[numTimes];
            Array.Copy(times, trimmed, numTimes);
            return trimmed;
        }

        // C: ufbxi_add_epsilon (ufbx.c:26978)
        static double AddEpsilon(double a, double epsilon)
        {
            return a > 0 ? a * epsilon : a / epsilon;
        }

        // C: ufbxi_sub_epsilon (ufbx.c:26979)
        static double SubEpsilon(double a, double epsilon)
        {
            return a > 0 ? a / epsilon : a * epsilon;
        }

        // C: ufbxi_postprocess_step (ufbx.c:26981-27023). Moves a stepped key off the step
        // boundary per `step_handling` and reports whether the key survives.
        static bool PostprocessStep(UfbxiBakeContext bc, double prevTime, double nextTime,
            ref double pTime, UfbxBakedKeyFlags flags)
        {
            // C: ufbxi_dev_assert((flags & (STEP_LEFT|STEP_RIGHT)) != 0) -- a no-op in the
            // reference build, and true by construction at both call sites.
            bool left = (flags & UfbxBakedKeyFlags.StepLeft) != 0;

            double step = 0.001;
            double epsilon = 1.0 + FltEpsilon * 4.0f;

            double time = pTime;
            switch (bc.Opts.StepHandling) {
            case UfbxBakeStepHandling.Default:
                break;
            case UfbxBakeStepHandling.CustomDuration:
                step = bc.Opts.StepCustomDuration;
                epsilon = 1.0 + bc.Opts.StepCustomEpsilon;
                break;
            case UfbxBakeStepHandling.IdenticalTime:
                return true;
            case UfbxBakeStepHandling.AdjacentDouble:
                if (left) {
                    pTime = time = UfbxMath.NextAfter(time, double.NegativeInfinity);
                    return time > prevTime;
                } else {
                    pTime = time = UfbxMath.NextAfter(time, double.PositiveInfinity);
                    return time < nextTime;
                }
            case UfbxBakeStepHandling.Ignore:
                return false;
            default:
                // C: ufbxi_unreachable("Unhandled bake step handling") is ufbx_assert(0), a no-op
                // in the reference build, so control reaches `return false` (ufbx.c:27010-27011).
                return false;
            }

            if (left) {
                double minTime = UfbxMath.FMax(prevTime + step, AddEpsilon(prevTime, epsilon));
                pTime = time = UfbxMath.FMin(time - step, SubEpsilon(time, epsilon));
                return time > minTime;
            } else {
                double maxTime = UfbxMath.FMin(nextTime - step, SubEpsilon(nextTime, epsilon));
                pTime = time = UfbxMath.FMax(time + step, AddEpsilon(time, epsilon));
                return time < maxTime;
            }
        }

        // C: ufbxi_bake_postprocess_vec3 (ufbx.c:27025-27105). `src` is the caller's scratch
        // array and is rewritten in place, exactly as C rewrites `src.data`; its final content is
        // copied into the result (`ufbxi_push_copy(&bc->result, ...)`), which is what `p_dst`
        // receives. A zero-length input leaves both outputs at the `ufbxi_push_zero()` defaults.
        static void BakePostprocessVec3(UfbxiBakeContext bc, out UfbxBakedVec3[] dst,
            out bool constant, UfbxBakedVec3[] src)
        {
            if (src.Length == 0) {
                dst = null;
                constant = false;
                return;
            }

            // Offset times (C: 27029-27036)
            if (bc.KtimeOffset != 0.0) {
                double scale = (double)bc.Scene.Metadata.KtimeSecond;
                double offset = bc.KtimeOffset;
                for (int i = 0; i < src.Length; i++) {
                    UfbxBakedVec3 key = src[i];
                    key.Time = UfbxMath.Rint(key.Time * scale + offset) / scale;
                    src[i] = key;
                }
            }

            int count = src.Length;

            // Postprocess stepped tangents (C: 27038-27056)
            {
                int dstIx = 0;
                double prevTime = src[0].Time;
                for (int i = 0; i < count; i++) {
                    UfbxBakedVec3 cur = src[i];
                    double nextTime = i + 1 < count ? src[i + 1].Time : double.PositiveInfinity;
                    bool keep = true;
                    if ((cur.Flags & (UfbxBakedKeyFlags.StepLeft | UfbxBakedKeyFlags.StepRight)) != 0) {
                        double time = cur.Time;
                        keep = PostprocessStep(bc, prevTime, nextTime, ref time, cur.Flags);
                        cur.Time = time;
                    }
                    if (keep) {
                        src[dstIx] = cur;
                        dstIx++;
                        prevTime = cur.Time;
                    }
                }
                count = dstIx;
            }

            // Key reduction (C: 27058-27087)
            if (bc.Opts.KeyReductionEnabled) {
                double threshold = bc.Opts.KeyReductionThreshold * bc.Opts.KeyReductionThreshold;
                for (int pass = 0; pass < bc.Opts.KeyReductionPasses; pass++) {
                    int dstIx = 1;
                    for (int i = 1; i < count; i++) {
                        UfbxBakedVec3 prev = src[i - 1];
                        UfbxBakedVec3 cur = src[i];
                        if (i + 1 < count) {
                            UfbxBakedVec3 next = src[i + 1];
                            double delta = (cur.Time - prev.Time) / (next.Time - prev.Time);
                            UfbxVec3 tmp = UfbxVec3.Lerp3(prev.Value, next.Value, delta);
                            double error = 0.0;
                            error += (tmp.X - cur.Value.X) * (tmp.X - cur.Value.X);
                            error += (tmp.Y - cur.Value.Y) * (tmp.Y - cur.Value.Y);
                            error += (tmp.Z - cur.Value.Z) * (tmp.Z - cur.Value.Z);
                            if (error <= threshold) {
                                src[dstIx] = src[i + 1];
                                i += 1;
                                dstIx += 1;
                                continue;
                            }
                        }

                        src[dstIx] = src[i];
                        dstIx += 1;
                    }
                    if (dstIx == count) break;
                    count = dstIx;
                }
            }

            // Constant detection (C: 27089-27098)
            bool isConstant = true;
            UfbxVec3 refValue = src[0].Value;
            for (int i = 1; i < count; i++) {
                UfbxVec3 v = src[i].Value;
                if (v.X != refValue.X || v.Y != refValue.Y || v.Z != refValue.Z) {
                    isConstant = false;
                    break;
                }
            }
            constant = isConstant;

            dst = new UfbxBakedVec3[count];
            Array.Copy(src, dst, count);
        }

        // C: ufbxi_bake_postprocess_quat (ufbx.c:27107-27207). Same shape as the vec3 form, plus
        // the antipodal fix and the two error metrics of the reduction step.
        static void BakePostprocessQuat(UfbxiBakeContext bc, out UfbxBakedQuat[] dst,
            out bool constant, UfbxBakedQuat[] src)
        {
            if (src.Length == 0) {
                dst = null;
                constant = false;
                return;
            }

            // Offset times (C: 27111-27118)
            if (bc.KtimeOffset != 0.0) {
                double scale = (double)bc.Scene.Metadata.KtimeSecond;
                double offset = bc.KtimeOffset;
                for (int i = 0; i < src.Length; i++) {
                    UfbxBakedQuat key = src[i];
                    key.Time = UfbxMath.Rint(key.Time * scale + offset) / scale;
                    src[i] = key;
                }
            }

            int count = src.Length;

            // Postprocess stepped tangents (C: 27120-27138)
            {
                int dstIx = 0;
                double prevTime = src[0].Time;
                for (int i = 0; i < count; i++) {
                    UfbxBakedQuat cur = src[i];
                    double nextTime = i + 1 < count ? src[i + 1].Time : double.PositiveInfinity;
                    bool keep = true;
                    if ((cur.Flags & (UfbxBakedKeyFlags.StepLeft | UfbxBakedKeyFlags.StepRight)) != 0) {
                        double time = cur.Time;
                        keep = PostprocessStep(bc, prevTime, nextTime, ref time, cur.Flags);
                        cur.Time = time;
                    }
                    if (keep) {
                        prevTime = cur.Time;
                        src[dstIx] = cur;
                        dstIx++;
                    }
                }
                count = dstIx;
            }

            // Fix quaternion antipodality (C: 27140-27143)
            for (int i = 1; i < count; i++) {
                UfbxBakedQuat cur = src[i];
                cur.Value = UfbxQuat.FixAntipodal(cur.Value, src[i - 1].Value);
                src[i] = cur;
            }

            // Key reduction (C: 27145-27189)
            if (bc.Opts.KeyReductionEnabled) {
                double threshold = bc.Opts.KeyReductionThreshold * bc.Opts.KeyReductionThreshold;
                for (int pass = 0; pass < bc.Opts.KeyReductionPasses; pass++) {
                    int dstIx = 1;
                    for (int i = 1; i < count; i++) {
                        UfbxBakedQuat prev = src[i - 1];
                        UfbxBakedQuat cur = src[i];
                        if (i + 1 < count) {
                            UfbxBakedQuat next = src[i + 1];
                            double delta = (cur.Time - prev.Time) / (next.Time - prev.Time);
                            double error = 0.0;

                            if (bc.Opts.KeyReductionRotation) {
                                UfbxQuat tmp = UfbxQuat.Slerp(prev.Value, next.Value, delta);
                                error += (tmp.X - cur.Value.X) * (tmp.X - cur.Value.X);
                                error += (tmp.Y - cur.Value.Y) * (tmp.Y - cur.Value.Y);
                                error += (tmp.Z - cur.Value.Z) * (tmp.Z - cur.Value.Z);
                                error += (tmp.W - cur.Value.W) * (tmp.W - cur.Value.W);
                            } else {
                                error += (prev.Value.X - cur.Value.X) * (prev.Value.X - cur.Value.X);
                                error += (prev.Value.Y - cur.Value.Y) * (prev.Value.Y - cur.Value.Y);
                                error += (prev.Value.Z - cur.Value.Z) * (prev.Value.Z - cur.Value.Z);
                                error += (prev.Value.W - cur.Value.W) * (prev.Value.W - cur.Value.W);
                                error += (next.Value.X - cur.Value.X) * (next.Value.X - cur.Value.X);
                                error += (next.Value.Y - cur.Value.Y) * (next.Value.Y - cur.Value.Y);
                                error += (next.Value.Z - cur.Value.Z) * (next.Value.Z - cur.Value.Z);
                                error += (next.Value.W - cur.Value.W) * (next.Value.W - cur.Value.W);
                                error *= 0.5;
                            }

                            if (error <= threshold) {
                                src[dstIx] = src[i + 1];
                                i += 1;
                                dstIx += 1;
                                continue;
                            }
                        }

                        src[dstIx] = src[i];
                        dstIx += 1;
                    }
                    if (dstIx == count) break;
                    count = dstIx;
                }
            }

            // Constant detection (C: 27191-27200)
            bool isConstant = true;
            UfbxQuat refValue = src[0].Value;
            for (int i = 1; i < count; i++) {
                UfbxQuat v = src[i].Value;
                if (v.X != refValue.X || v.Y != refValue.Y || v.Z != refValue.Z || v.W != refValue.W) {
                    isConstant = false;
                    break;
                }
            }
            constant = isConstant;

            dst = new UfbxBakedQuat[count];
            Array.Copy(src, dst, count);
        }

        // C: ufbxi_bake_time_sample_time (ufbx.c:27209-27218)
        static double BakeTimeSampleTime(UfbxiBakeTime time)
        {
            // Move an infinitesimal step for stepped tangents
            if ((time.Flags & (KeyStepLeft | KeyStepRight)) != 0) {
                double dir = (time.Flags & KeyStepLeft) != 0 ? double.NegativeInfinity : double.PositiveInfinity;
                return UfbxMath.NextAfter(time.Time, dir);
            } else {
                return time.Time;
            }
        }

        // C: ufbxi_push_resampled_times (ufbx.c:27220-27239). Feeds the already-baked scale keys
        // of a scale helper back into `tmp_times` so the dependent node samples at the same times.
        static void PushResampledTimes(UfbxiBakeContext bc, UfbxBakedVec3[] keys)
        {
            int count = keys != null ? keys.Length : 0;
            for (int i = 0; i < count; i++) {
                UfbxBakedKeyFlags flags = keys[i].Flags;
                double time = keys[i].Time;
                if ((flags & UfbxBakedKeyFlags.StepLeft) != 0 && i + 1 < count &&
                    (keys[i + 1].Flags & UfbxBakedKeyFlags.StepKey) != 0) {
                    time = keys[i + 1].Time;
                } else if ((flags & UfbxBakedKeyFlags.StepRight) != 0 && i > 0 &&
                    (keys[i - 1].Flags & UfbxBakedKeyFlags.StepKey) != 0) {
                    time = keys[i - 1].Time;
                }
                UfbxiBakeTime t = new UfbxiBakeTime();
                t.Time = time;
                t.Flags = ((uint)flags) & 0x7u;
                bc.TmpTimes.Add(t);
            }
        }

        // C: ufbxi_bake_node_imp (ufbx.c:27241-27498). Bakes one node's translation/rotation/scale
        // by merging the three time sets and evaluating the transform at every merged time.
        static void BakeNodeImp(UfbxiBakeContext bc, uint elementId, UfbxiBakeProp[] props,
            int begin, int count)
        {
            // C: ufbx_assert(bc->baked_nodes && bc->nodes_to_bake) -- this is only reached when
            // `skip_node_transforms` is off, which is what allocates both arrays.
            UfbxNode node = (UfbxNode)bc.Scene.Elements[elementId];

            bool complexTranslation = false;
            bool complexRotation = false;

            for (int i = 0; i < ComplexTranslationProps.Length; i++) {
                string name = ComplexTranslationProps[i];
                // C: `ufbxi_find_prop()` -- the internal macro over `ufbxi_find_prop_with_key`
                // (ufbx.c:11476-11514), not the public `ufbx_find_prop_len()`: it skips
                // UFBX_PROP_FLAG_NO_VALUE props and searches `defaults` within one loop.
                if (UfbxiProperties.TryFindProp(node.Props, name, out UfbxProp prop) &&
                    !UfbxiProperties.IsVec3Zero(prop.ValueVec3)) {
                    complexTranslation = true;
                }
                for (int j = 0; j < count; j++) {
                    if (props[begin + j].PropName == name) {
                        complexTranslation = true;
                    }
                }
            }

            for (int i = 0; i < ComplexRotationProps.Length; i++) {
                string name = ComplexRotationProps[i];
                for (int j = 0; j < count; j++) {
                    if (props[begin + j].PropName == name) {
                        complexRotation = true;
                    }
                }
            }

            // Translation (C: 27275-27311)
            bool resampleTranslation = false;

            // Account for the _resampled_ scale helper scale animation to keep the
            // translation scale consistent with the parent scaling.
            UfbxBakedNode scaleHelperT = default;
            bool hasScaleHelperT = false;
            UfbxVec3 constantScaleT = new UfbxVec3(1.0, 1.0, 1.0);
            if (!node.IsScaleHelper && node.Parent != null && node.Parent.ScaleHelper != null) {
                int helperIx = bc.BakedNodeIndex[node.Parent.ScaleHelper.TypedId];
                if (helperIx >= 0) {
                    scaleHelperT = bc.TmpNodes[helperIx];
                    hasScaleHelperT = true;
                    if (!scaleHelperT.ConstantScale) {
                        resampleTranslation = true;
                    }
                    PushResampledTimes(bc, scaleHelperT.ScaleKeys);
                } else {
                    constantScaleT = node.Parent.ScaleHelper.InheritScale;
                }
            }

            if (complexTranslation) {
                for (int i = 0; i < count; i++) {
                    UfbxiBakeProp prop = props[begin + i];
                    // Literally any transform related property can affect complex translation
                    if (InList(TransformProps, prop.PropName)) {
                        bool resampleLinear = resampleTranslation || prop.PropName != UfbxiStrings.Lcl_Translation;
                        uint keyFlag = prop.PropName == UfbxiStrings.Lcl_Translation ? KeyKeyframe : 0u;
                        BakeTimes(bc, prop.AnimValue, resampleLinear, keyFlag);
                    }
                }
            } else {
                for (int i = 0; i < count; i++) {
                    UfbxiBakeProp prop = props[begin + i];
                    if (prop.PropName == UfbxiStrings.Lcl_Translation) {
                        BakeTimes(bc, prop.AnimValue, resampleTranslation, KeyKeyframe);
                    }
                }
            }

            UfbxiBakeTime[] timesT = FinalizeBakeTimes(bc);

            // Rotation (C: 27313-27329)
            if (complexRotation) {
                for (int i = 0; i < count; i++) {
                    UfbxiBakeProp prop = props[begin + i];
                    if (InList(ComplexRotationSources, prop.PropName)) {
                        bool resampleLinear = !bc.Opts.NoResampleRotation || prop.PropName != UfbxiStrings.Lcl_Rotation;
                        uint keyFlag = prop.PropName == UfbxiStrings.Lcl_Rotation ? KeyKeyframe : 0u;
                        BakeTimes(bc, prop.AnimValue, resampleLinear, keyFlag);
                    }
                }
            } else {
                for (int i = 0; i < count; i++) {
                    UfbxiBakeProp prop = props[begin + i];
                    if (prop.PropName == UfbxiStrings.Lcl_Rotation) {
                        BakeTimes(bc, prop.AnimValue, !bc.Opts.NoResampleRotation, KeyKeyframe);
                    }
                }
            }
            UfbxiBakeTime[] timesR = FinalizeBakeTimes(bc);

            // Scaling (C: 27331-27355)
            bool resampleScale = false;

            // Account for the resampled scale
            UfbxBakedNode scaleHelperS = default;
            bool hasScaleHelperS = false;
            UfbxVec3 constantScaleS = new UfbxVec3(1.0, 1.0, 1.0);
            if (node.IsScaleHelper && node.Parent != null && node.Parent.InheritScaleNode != null &&
                node.Parent.InheritScaleNode.ScaleHelper != null) {
                UfbxNode inheritHelper = node.Parent.InheritScaleNode.ScaleHelper;
                int helperIx = bc.BakedNodeIndex[inheritHelper.TypedId];
                if (helperIx >= 0) {
                    scaleHelperS = bc.TmpNodes[helperIx];
                    hasScaleHelperS = true;
                    if (!scaleHelperS.ConstantScale) {
                        resampleScale = true;
                    }
                    PushResampledTimes(bc, scaleHelperS.ScaleKeys);
                } else {
                    constantScaleS = inheritHelper.LocalTransform.Scale;
                }
            }

            for (int i = 0; i < count; i++) {
                UfbxiBakeProp prop = props[begin + i];
                if (prop.PropName == UfbxiStrings.Lcl_Scaling) {
                    BakeTimes(bc, prop.AnimValue, resampleScale, KeyKeyframe);
                }
            }
            UfbxiBakeTime[] timesS = FinalizeBakeTimes(bc);

            UfbxBakedVec3[] keysT = new UfbxBakedVec3[timesT.Length];
            UfbxBakedQuat[] keysR = new UfbxBakedQuat[timesR.Length];
            UfbxBakedVec3[] keysS = new UfbxBakedVec3[timesS.Length];

            int ixT = 0, ixR = 0, ixS = 0;
            while (ixT < timesT.Length || ixR < timesR.Length || ixS < timesS.Length) {
                UfbxiBakeTime bakeTime = new UfbxiBakeTime();
                bakeTime.Time = double.PositiveInfinity; // C: { UFBX_INFINITY }
                bakeTime.Flags = 0;
                uint flagsR = 0, flagsT = 0, flagsS = 0;

                uint flags = 0;
                if (ixR < timesR.Length) {
                    bakeTime = timesR[ixR];
                    flagsR = bakeTime.Flags;
                    bakeTime.Flags &= 0x7u;
                    flags |= FlagIncludeRotation;
                }
                if (ixT < timesT.Length) {
                    UfbxiBakeTime t = timesT[ixT];
                    int cmp = CmpBakeTime(t, bakeTime);
                    if (cmp <= 0) {
                        if (cmp < 0) {
                            bakeTime = t;
                            flags = 0;
                        }
                        bakeTime.Flags |= t.Flags & 0x7u;
                        flagsT = t.Flags;
                        flags |= FlagIncludeTranslation;
                    }
                }
                if (ixS < timesS.Length) {
                    UfbxiBakeTime t = timesS[ixS];
                    int cmp = CmpBakeTime(t, bakeTime);
                    if (cmp <= 0) {
                        if (cmp < 0) {
                            bakeTime = t;
                            flags = 0;
                        }
                        bakeTime.Flags |= t.Flags & 0x7u;
                        flagsS = t.Flags;
                        flags |= FlagIncludeScale;
                    }
                }

                flags |= FlagIgnoreScaleHelper | FlagIgnoreComponentwiseScale | FlagExplicitIncludes;
                if ((bc.Opts.EvaluateFlags & EvaluateNoExtrapolation) != 0) {
                    flags |= FlagNoExtrapolation;
                }

                double evalTime = BakeTimeSampleTime(bakeTime);
                UfbxTransform transform = UfbxEvaluate.TransformFlags(bc.Anim, node, evalTime, flags);

                if ((flags & FlagIncludeTranslation) != 0) {
                    if (hasScaleHelperT) {
                        UfbxVec3 scale = EvaluateBakedVec3(scaleHelperT.ScaleKeys, evalTime);
                        transform.Translation.X *= scale.X;
                        transform.Translation.Y *= scale.Y;
                        transform.Translation.Z *= scale.Z;
                    }

                    transform.Translation.X *= constantScaleT.X;
                    transform.Translation.Y *= constantScaleT.Y;
                    transform.Translation.Z *= constantScaleT.Z;

                    UfbxBakedVec3 key = new UfbxBakedVec3();
                    key.Time = bakeTime.Time;
                    key.Value = transform.Translation;
                    key.Flags = (UfbxBakedKeyFlags)(bakeTime.Flags | flagsT);
                    keysT[ixT] = key;
                    ixT++;
                }
                if ((flags & FlagIncludeRotation) != 0) {
                    UfbxBakedQuat key = new UfbxBakedQuat();
                    key.Time = bakeTime.Time;
                    key.Value = transform.Rotation;
                    key.Flags = (UfbxBakedKeyFlags)(bakeTime.Flags | flagsR);
                    keysR[ixR] = key;
                    ixR++;
                }
                if ((flags & FlagIncludeScale) != 0) {
                    if (hasScaleHelperS) {
                        UfbxVec3 scale = EvaluateBakedVec3(scaleHelperS.ScaleKeys, evalTime);
                        transform.Scale.X *= scale.X;
                        transform.Scale.Y *= scale.Y;
                        transform.Scale.Z *= scale.Z;
                    }

                    transform.Scale.X *= constantScaleS.X;
                    transform.Scale.Y *= constantScaleS.Y;
                    transform.Scale.Z *= constantScaleS.Z;

                    UfbxBakedVec3 key = new UfbxBakedVec3();
                    key.Time = bakeTime.Time;
                    key.Value = transform.Scale;
                    key.Flags = (UfbxBakedKeyFlags)(bakeTime.Flags | flagsS);
                    keysS[ixS] = key;
                    ixS++;
                }
            }

            // C: `ufbx_baked_node *baked_node = ufbxi_push_zero(&bc->tmp_nodes, ...)` -- the
            // pointer stays valid for the whole bake, so C fills it through the pointer; the port
            // fills a local and appends the finished value.
            UfbxBakedNode bakedNode = default;
            bakedNode.ElementId = node.ElementId;
            bakedNode.TypedId = node.TypedId;
            BakePostprocessVec3(bc, out bakedNode.TranslationKeys, out bakedNode.ConstantTranslation, keysT);
            BakePostprocessQuat(bc, out bakedNode.RotationKeys, out bakedNode.ConstantRotation, keysR);
            BakePostprocessVec3(bc, out bakedNode.ScaleKeys, out bakedNode.ConstantScale, keysS);

            bc.BakedNodeIndex[node.TypedId] = bc.TmpNodes.Count;
            bc.TmpNodes.Add(bakedNode);

            // C: ufbxi_buf_clear(&bc->tmp_prop) -- the times and key arrays are locals here.

            // If this node is a scale helper, make sure to bake its siblings and
            // potentially their scale helpers if they are not a part of the animation.
            // (C: 27475-27495)
            if (node.IsScaleHelper) {
                UfbxNode[] children = node.Parent.Children ?? Array.Empty<UfbxNode>();
                for (int i = 0; i < children.Length; i++) {
                    UfbxNode child = children[i];
                    if (child == node) continue;
                    if (!bc.NodesToBake[child.TypedId]) {
                        bc.NodesToBake[child.TypedId] = true;
                        bc.TmpBakeStack.Add(child.ElementId);
                    }
                    if (child.InheritScaleNode != null && child.InheritScaleNode.ScaleHelper != null &&
                        child.ScaleHelper != null &&
                        bc.NodesToBake[child.InheritScaleNode.ScaleHelper.TypedId]) {
                        // C: ufbx_assert(bc->baked_nodes[...]) -- it is baked, since it is marked.
                        if (!bc.NodesToBake[child.ScaleHelper.TypedId]) {
                            bc.NodesToBake[child.ScaleHelper.TypedId] = true;
                            bc.TmpBakeStack.Add(child.ScaleHelper.ElementId);
                        }
                    }
                }
            }
        }

        // C: ufbxi_bake_node (ufbx.c:27500-27513). Baking a node may enqueue further nodes, so
        // keep going until the dependency stack is empty.
        static void BakeNode(UfbxiBakeContext bc, uint elementId, UfbxiBakeProp[] props, int begin, int count)
        {
            BakeNodeImp(bc, elementId, props, begin, count);

            while (bc.TmpBakeStack.Count > 0) {
                // C: ufbxi_pop(&bc->tmp_bake_stack, uint32_t, 1, &child_id)
                int last = bc.TmpBakeStack.Count - 1;
                uint childId = bc.TmpBakeStack[last];
                bc.TmpBakeStack.RemoveAt(last);
                BakeNodeImp(bc, childId, null, 0, 0);
            }
        }

        // C: ufbxi_bake_anim_prop (ufbx.c:27515-27554). Bakes one animated property of an
        // element (the non-transform case).
        static void BakeAnimProp(UfbxiBakeContext bc, UfbxElement element, string propName,
            UfbxiBakeProp[] props, int begin, int count)
        {
            for (int i = 0; i < count; i++) {
                BakeTimes(bc, props[begin + i].AnimValue, false, KeyKeyframe);
            }

            UfbxiBakeTime[] times = FinalizeBakeTimes(bc);

            UfbxBakedVec3[] keys = new UfbxBakedVec3[times.Length];
            for (int i = 0; i < times.Length; i++) {
                UfbxiBakeTime bakeTime = times[i];
                double evalTime = BakeTimeSampleTime(bakeTime);
                // C: `ufbx_string name = { prop_name, strlen(prop_name) }` (27529-27531): the
                // port's one-byte-per-char names give the same length.
                UfbxProp prop = UfbxEvaluate.PropFlagsLen(bc.Anim, element, propName, propName.Length,
                    evalTime, bc.Opts.EvaluateFlags);
                UfbxBakedVec3 key = new UfbxBakedVec3();
                key.Time = bakeTime.Time;
                key.Value = prop.ValueVec3;
                key.Flags = (UfbxBakedKeyFlags)bakeTime.Flags;
                keys[i] = key;
            }

            // C: ufbxi_push_zero(&bc->tmp_props, ufbx_baked_prop, 1), filled through the pointer.
            UfbxBakedProp bakedProp = default;
            // C: `name.length = strlen(prop_name)`, `name.data = ufbxi_push_copy(&bc->result,
            // char, length + 1, prop_name)` -- the arena copy exists for the NUL-terminated
            // byte string; the port's string value is the same `length` bytes.
            bakedProp.Name = propName;
            BakePostprocessVec3(bc, out bakedProp.Keys, out bakedProp.ConstantValue, keys);
            bc.TmpProps.Add(bakedProp);

            // C: ufbxi_buf_clear(&bc->tmp_prop) -- locals here.
        }

        // C: ufbxi_bake_element (ufbx.c:27556-27593)
        static void BakeElement(UfbxiBakeContext bc, uint elementId, UfbxiBakeProp[] props, int begin, int count)
        {
            UfbxElement element = bc.Scene.Elements[elementId];
            if (element.Type == UfbxElementType.Node && !bc.Opts.SkipNodeTransforms) {
                BakeNode(bc, elementId, props, begin, count);
            }

            int b = begin;
            int end = begin + count;
            while (b < end) {
                string propName = props[b].PropName;
                int groupEnd = b + 1;
                while (groupEnd < end && props[groupEnd].PropName == propName) {
                    groupEnd++;
                }

                // Don't bake transform related props for nodes unless specifically requested
                if (element.Type == UfbxElementType.Node && !bc.Opts.BakeTransformProps &&
                    InList(TransformProps, propName)) {
                    b = groupEnd;
                    continue;
                }

                BakeAnimProp(bc, element, propName, props, b, groupEnd - b);
                b = groupEnd;
            }

            int numProps = bc.TmpProps.Count;
            if (numProps > 0) {
                UfbxBakedElement bakedElem = default;
                bakedElem.ElementId = element.ElementId;
                // C: ufbxi_push_pop(&bc->result, &bc->tmp_props, ufbx_baked_prop, num_props)
                bakedElem.Props = bc.TmpProps.ToArray();
                bc.TmpProps.Clear();
                bc.TmpElements.Add(bakedElem);
            }
        }

        // C: ufbxi_baked_node_less (ufbx.c:27595-27600)
        static bool BakedNodeLess(object user, UfbxBakedNode a, UfbxBakedNode b)
        {
            return a.TypedId < b.TypedId;
        }

        // C: ufbxi_baked_element_less (ufbx.c:27602-27607)
        static bool BakedElementLess(object user, UfbxBakedElement a, UfbxBakedElement b)
        {
            return a.ElementId < b.ElementId;
        }

        // C: ufbxi_bake_anim (ufbx.c:27609-27713)
        static bool BakeAnim(UfbxiBakeContext bc)
        {
            UfbxAnim anim = bc.Anim;
            UfbxScene scene = bc.Scene;

            int numNodesTyped = scene.Nodes != null ? scene.Nodes.Length : 0;
            if (!bc.Opts.SkipNodeTransforms) {
                // C: ufbxi_push_zero(...) of pointers/bools -- -1 is C's NULL pointer.
                bc.BakedNodeIndex = new int[numNodesTyped];
                for (int i = 0; i < numNodesTyped; i++) bc.BakedNodeIndex[i] = -1;
                bc.NodesToBake = new bool[numNodesTyped];
            }

            UfbxAnimLayer[] layers = anim.Layers ?? Array.Empty<UfbxAnimLayer>();
            for (int l = 0; l < layers.Length; l++) {
                UfbxAnimLayer layer = layers[l];
                UfbxAnimProp[] animProps = layer.AnimProps ?? Array.Empty<UfbxAnimProp>();
                for (int p = 0; p < animProps.Length; p++) {
                    UfbxAnimProp animProp = animProps[p];
                    UfbxiBakeProp prop = new UfbxiBakeProp();
                    bc.TmpBakeProps.Add(prop);

                    UfbxElement element = animProp.Element;

                    // Sort nodes by `typed_id` to make sure we process them in order.
                    if (element.Type == UfbxElementType.Node) {
                        if (bc.NodesToBake != null) {
                            bc.NodesToBake[element.TypedId] = true;
                        }
                        prop.SortId = element.TypedId;
                    } else {
                        prop.SortId = uint.MaxValue;
                    }

                    prop.ElementId = element.ElementId;
                    prop.PropName = animProp.PropName;
                    prop.AnimValue = animProp.AnimValue;
                }
            }

            int numProps = bc.TmpBakeProps.Count;
            // C: ufbxi_push_pop(&bc->tmp, &bc->tmp_bake_props, ufbxi_bake_prop, num_props)
            UfbxiBakeProp[] props = bc.TmpBakeProps.ToArray();
            bc.TmpBakeProps.Clear();

            UfbxiSort.UnstableSort(props, numProps, BakePropLess, null);

            // Pre-bake layer weight times (C: 27652-27674)
            if (!bc.Opts.IgnoreLayerWeightAnimation) {
                bool hasWeightTimes = false;
                for (int i = 0; i < numProps; i++) {
                    UfbxiBakeProp prop = props[i];
                    if (prop.PropName != UfbxiStrings.Weight) continue;
                    UfbxElement element = scene.Elements[prop.ElementId];
                    if (element.Type == UfbxElementType.AnimLayer) {
                        BakeTimes(bc, prop.AnimValue, true, 0);
                        hasWeightTimes = true;
                    }
                }

                if (hasWeightTimes) {
                    UfbxiBakeTime[] weightTimes = FinalizeBakeTimes(bc);
                    // C: ufbxi_push_copy(&bc->tmp, ufbxi_bake_time, ...) -- an independent copy,
                    // because `weight_times.data` lives in `tmp_prop` and is cleared below.
                    bc.LayerWeightTimes = (UfbxiBakeTime[])weightTimes.Clone();
                }
            }

            int begin = 0;
            while (begin < numProps) {
                uint elementId = props[begin].ElementId;
                int end = begin + 1;
                while (end < numProps && props[end].ElementId == elementId) {
                    end++;
                }
                BakeElement(bc, elementId, props, begin, end - begin);
                begin = end;
            }

            int numBakedNodes = bc.TmpNodes.Count;
            int numBakedElements = bc.TmpElements.Count;

            // C: ufbxi_push_pop(&bc->result, &bc->tmp_nodes, ...) -- the array keeps C's
            // "non-NULL even when empty" property (ufbxi_push(buf, T, 0) is a valid pointer).
            bc.Bake.Nodes = bc.TmpNodes.ToArray();
            bc.TmpNodes.Clear();
            bc.Bake.Elements = bc.TmpElements.ToArray();
            bc.TmpElements.Clear();

            UfbxiSort.UnstableSort(bc.Bake.Nodes, numBakedNodes, BakedNodeLess, null);
            UfbxiSort.UnstableSort(bc.Bake.Elements, numBakedElements, BakedElementLess, null);

            if (bc.TimeMin < bc.TimeMax) {
                bc.Bake.KeyTimeMin = bc.TimeMin;
                bc.Bake.KeyTimeMax = bc.TimeMax;
            }

            if (bc.TimeBegin < bc.TimeEnd) {
                bc.Bake.PlaybackTimeBegin = bc.TimeBegin;
                bc.Bake.PlaybackTimeEnd = bc.TimeEnd;
                bc.Bake.PlaybackDuration = bc.TimeEnd - bc.TimeBegin;
            }

            return true;
        }

        // C: ufbxi_bake_anim_imp (ufbx.c:27715-27773)
        static bool BakeAnimImp(UfbxiBakeContext bc, UfbxAnim anim)
        {
            // The five defaults are written into the *copy* of the caller's opts, which is why
            // CopyOpts() above is not optional.
            UfbxBakeOpts opts = bc.Opts;
            if (opts.ResampleRate <= 0.0) opts.ResampleRate = 30.0;
            if (opts.MinimumSampleRate <= 0.0) opts.MinimumSampleRate = 19.5;
            if (opts.MaxKeyframeSegments == 0) opts.MaxKeyframeSegments = 32;
            if (opts.KeyReductionThreshold == 0) opts.KeyReductionThreshold = 0.000001;
            if (opts.KeyReductionPasses == 0) opts.KeyReductionPasses = 4;

            if (opts.TrimStartTime && anim.TimeBegin > 0.0) {
                bc.KtimeOffset = -anim.TimeBegin * (double)bc.Scene.Metadata.KtimeSecond;
            }

            // C: ufbxi_init_ator(&bc->error, &bc->ator_tmp/ator_result, &bc->opts.*_allocator,
            // "temp"/"result") and the buf setup (27727-27745) -- allocator plumbing with no
            // managed counterpart; a bad allocator can only fail the C arena.

            bc.Anim = anim;
            if (anim.TimeBegin < anim.TimeEnd) {
                bc.TimeBegin = anim.TimeBegin;
                bc.TimeEnd = anim.TimeEnd;
            }
            bc.TimeMin = double.PositiveInfinity;
            bc.TimeMax = double.NegativeInfinity;

            if (!BakeAnim(bc)) return false;

            // C: ufbxi_init_ref(&bc->imp->refcount, UFBXI_BAKED_ANIM_IMP_MAGIC, NULL) and the
            // four metadata statistics (27760-27765) -- see the header note.
            return true;
        }

        // C: ufbx_bake_anim (ufbx.c:31250-31297). `ufbxi_check_opts_ptr()` (31254) is the same
        // `_begin_zero`/`_end_zero` sentinel guard as in UfbxApi.LoadMemory and is not
        // representable for the port's options type.
        internal static UfbxBakedAnim BakeAnimEntry(UfbxScene scene, UfbxAnim anim, UfbxBakeOpts userOpts, UfbxError pError)
        {
            // C: ufbx_assert(scene) (31252) -- a no-op in the reference build; the port reads
            // `scene->anim` below and throws a NullReferenceException instead.
            UfbxiBakeContext bc = new UfbxiBakeContext();

            // C: `if (!anim) { anim = scene->anim; }` (31255-31257)
            if (anim == null) {
                anim = scene.Anim;
            }

            // C: `ufbxi_bake_context bc = { UFBX_ERROR_NONE }; if (opts) bc.opts = *opts;`
            // (31259-31262): NULL opts means the all-zero struct, not `ufbx_default_bake_opts()`
            // (which is also all zeros for every field this chain reads; the effective defaults
            // come from BakeAnimImp above).
            bc.Opts = CopyOpts(userOpts);
            bc.Scene = scene;

            if (BakeAnimImp(bc, anim)) {
                UfbxiPrint.ClearError(pError);
                return bc.Bake;
            }

            UfbxiPrint.FixErrorType(bc.Error, "Failed to bake anim", pError);
            return null;
        }

        // C: `ufbx_bake_opts opts; if (user_opts) { opts = *user_opts; }` (ufbx.c:31260-31262)
        // combined with the five writes in ufbxi_bake_anim_imp (27717-27721). PORTING_NOTES.md #9.
        static UfbxBakeOpts CopyOpts(UfbxBakeOpts userOpts)
        {
            UfbxBakeOpts opts = new UfbxBakeOpts();
            if (userOpts == null) return opts;
            opts.TempAllocator = userOpts.TempAllocator;
            opts.ResultAllocator = userOpts.ResultAllocator;
            opts.TrimStartTime = userOpts.TrimStartTime;
            opts.ResampleRate = userOpts.ResampleRate;
            opts.MinimumSampleRate = userOpts.MinimumSampleRate;
            opts.MaximumSampleRate = userOpts.MaximumSampleRate;
            opts.BakeTransformProps = userOpts.BakeTransformProps;
            opts.SkipNodeTransforms = userOpts.SkipNodeTransforms;
            opts.NoResampleRotation = userOpts.NoResampleRotation;
            opts.IgnoreLayerWeightAnimation = userOpts.IgnoreLayerWeightAnimation;
            opts.MaxKeyframeSegments = userOpts.MaxKeyframeSegments;
            opts.StepHandling = userOpts.StepHandling;
            opts.StepCustomDuration = userOpts.StepCustomDuration;
            opts.StepCustomEpsilon = userOpts.StepCustomEpsilon;
            opts.EvaluateFlags = userOpts.EvaluateFlags;
            opts.KeyReductionEnabled = userOpts.KeyReductionEnabled;
            opts.KeyReductionRotation = userOpts.KeyReductionRotation;
            opts.KeyReductionThreshold = userOpts.KeyReductionThreshold;
            opts.KeyReductionPasses = userOpts.KeyReductionPasses;
            return opts;
        }

        // C: ufbx_retain_baked_anim / ufbx_free_baked_anim (ufbx.c:31299-31307 / 31309-31317).
        // Both recover the `ufbxi_baked_anim_imp` header from the pointer, check its magic and
        // walk the refcount; the managed bake has no header, so the pair is the same no-op as
        // UfbxApi.FreeScene(). The `if (!bake) return;` guards are the only observable.
        internal static void RetainBakedAnim(UfbxBakedAnim bake)
        {
        }

        internal static void FreeBakedAnim(UfbxBakedAnim bake)
        {
        }

        // C: ufbx_find_baked_node_by_typed_id (ufbx.c:31320-31326). C returns a pointer into
        // `bake->nodes.data`; the port returns a copy (see the façade note).
        internal static UfbxBakedNode? FindBakedNodeByTypedId(UfbxBakedAnim bake, uint typedId)
        {
            UfbxBakedNode[] data = bake.Nodes ?? Array.Empty<UfbxBakedNode>();
            int count = data.Length;

            int index = -1; // C: SIZE_MAX
            UfbxiSceneBuild.LowerBoundEq(data, 0, count, 8,
                a => a.TypedId < typedId,
                a => a.TypedId == typedId,
                ref index);
            return index >= 0 ? data[index] : (UfbxBakedNode?)null;
        }

        // C: ufbx_find_baked_node (ufbx.c:31328-31332)
        internal static UfbxBakedNode? FindBakedNode(UfbxBakedAnim bake, UfbxNode node)
        {
            if (bake == null || node == null) return null;
            return FindBakedNodeByTypedId(bake, node.TypedId);
        }

        // C: ufbx_find_baked_element_by_element_id (ufbx.c:31334-31340)
        internal static UfbxBakedElement? FindBakedElementByElementId(UfbxBakedAnim bake, uint elementId)
        {
            UfbxBakedElement[] data = bake.Elements ?? Array.Empty<UfbxBakedElement>();
            int count = data.Length;

            int index = -1; // C: SIZE_MAX
            UfbxiSceneBuild.LowerBoundEq(data, 0, count, 8,
                a => a.ElementId < elementId,
                a => a.ElementId == elementId,
                ref index);
            return index >= 0 ? data[index] : (UfbxBakedElement?)null;
        }

        // C: ufbx_find_baked_element (ufbx.c:31342-31346)
        internal static UfbxBakedElement? FindBakedElement(UfbxBakedAnim bake, UfbxElement element)
        {
            if (bake == null || element == null) return null;
            return FindBakedElementByElementId(bake, element.ElementId);
        }

        // C: ufbx_evaluate_baked_vec3 (ufbx.c:31348-31378). Binary search down to a window of 8
        // keyframes, then the linear scan that applies the step flags.
        internal static UfbxVec3 EvaluateBakedVec3(UfbxBakedVec3[] keyframes, double time)
        {
            int begin = 0;
            int end = keyframes.Length;
            UfbxBakedVec3[] keys = keyframes;
            while (end - begin >= 8) {
                int mid = (begin + end) >> 1;
                if (keys[mid].Time <= time) {
                    begin = mid + 1;
                } else {
                    end = mid;
                }
            }

            end = keyframes.Length;
            for (; begin < end; begin++) {
                UfbxBakedVec3 next = keys[begin];
                if (next.Time <= time) continue;
                if (begin == 0) return next.Value;

                int prevIx = begin - 1;
                // C: `prev > keys` is the pointer test that `prev` is not the first element.
                if (prevIx > 0 && (keys[prevIx].Flags & UfbxBakedKeyFlags.StepRight) != 0 &&
                    keys[prevIx - 1].Time == time) prevIx--;
                if (time == keys[prevIx].Time) return keys[prevIx].Value;
                double t = (time - keys[prevIx].Time) / (next.Time - keys[prevIx].Time);
                if ((keys[prevIx].Flags & UfbxBakedKeyFlags.StepLeft) != 0) t = 0.0;
                if ((next.Flags & UfbxBakedKeyFlags.StepRight) != 0) t = 1.0;
                return UfbxVec3.Lerp3(keys[prevIx].Value, next.Value, t);
            }

            // C: `keyframes.data[keyframes.count - 1].value` -- for an empty list that is C's
            // `data[-1]` read (undefined; NULL data makes it a crash), and the port indexes -1.
            return keys[keys.Length - 1].Value;
        }

        // C: ufbx_evaluate_baked_quat (ufbx.c:31380-31411). NOTE: the two `prev[-1]` tests here
        // are not the vec3 ones -- this form steps `prev` back unconditionally *before* the
        // interpolation parameter is computed, and only applies the step-right test *after* it, so
        // the flag test reads the shifted key. Transcribed literally.
        internal static UfbxQuat EvaluateBakedQuat(UfbxBakedQuat[] keyframes, double time)
        {
            int begin = 0;
            int end = keyframes.Length;
            UfbxBakedQuat[] keys = keyframes;
            while (end - begin >= 8) {
                int mid = (begin + end) >> 1;
                if (keys[mid].Time <= time) {
                    begin = mid + 1;
                } else {
                    end = mid;
                }
            }

            end = keyframes.Length;
            for (; begin < end; begin++) {
                UfbxBakedQuat next = keys[begin];
                if (next.Time <= time) continue;
                if (begin == 0) return next.Value;

                int prevIx = begin - 1;
                if (prevIx > 0 && keys[prevIx - 1].Time == time) prevIx--;
                if (time == keys[prevIx].Time) return keys[prevIx].Value;
                double t = (time - keys[prevIx].Time) / (next.Time - keys[prevIx].Time);
                if (prevIx > 0 && (keys[prevIx].Flags & UfbxBakedKeyFlags.StepRight) != 0 &&
                    keys[prevIx - 1].Time == time) prevIx--;
                if ((keys[prevIx].Flags & UfbxBakedKeyFlags.StepLeft) != 0) t = 0.0;
                if ((next.Flags & UfbxBakedKeyFlags.StepRight) != 0) t = 1.0;
                return UfbxQuat.Slerp(keys[prevIx].Value, next.Value, t);
            }

            return keys[keys.Length - 1].Value;
        }
    }
}
