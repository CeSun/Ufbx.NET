// S2 animation readers, ported from ufbx v0.23.1 ufbx.c. Owned by the S2-anim porting
// agent (2026-10-03 wave):
//   UFBXI_KEY_* flags          (14088-14107, typedef ufbxi_key_flags)
//   ufbxi_solve_auto_tangent   (14109-14170)
//   ufbxi_solve_auto_tangent_left  (14172-14193)
//   ufbxi_solve_auto_tangent_right (14195-14216)
//   ufbxi_solve_tcb            (14218-14228)
//   ufbxi_read_extrapolation   (14230-14258)
//   ufbxi_read_animation_curve (14260-14535)
//   ufbxi_read_anim_stack      (14629-14646)
//   ufbxi_double_to_char       (15317-15324)
//   ufbxi_read_take_anim_channel   (15326-15586)
//   ufbxi_read_take_prop_channel   (15590-15667)
//   ufbxi_read_take_object     (15669-15689)
//   ufbxi_read_take            (15691-15752)
// (ufbxi_read_takes, 15754-15767, already lives in Parse/Root.cs as UfbxiRoot.ReadTakes,
// which currently stops at a `ReadTake` stub -- the orchestrator rewires that stub to
// UfbxiAnimReader.ReadTake below.)
//
using System;
// Float fidelity (PORTING_NOTES.md 浮点语义): no contraction anywhere; every C site where a
// multiplication result feeds an addition is spelled `UfbxMath.Fma(a, b, c)` as a marker
// (reduces to a*b+c). All curve key values/interpolations/TCB parameters are `ufbx_real` =
// double; tangents and weights are `float`, exactly as in ufbx.h. No System.Math.
namespace Ufbx
{
    // C: typedef enum ufbxi_key_flags (ufbx.c:14088-14107).
    internal static class UfbxiKeyFlags
    {
        internal const uint InterpolationConstant = 0x2;
        internal const uint InterpolationLinear = 0x4;
        internal const uint InterpolationCubic = 0x8;
        internal const uint TangentAuto = 0x100;
        internal const uint TangentTcb = 0x200;
        internal const uint TangentUser = 0x400;
        internal const uint TangentBroken = 0x800;
        internal const uint ConstantNext = 0x100;
        internal const uint Clamp = 0x1000;
        internal const uint TimeIndependent = 0x2000;
        internal const uint ClampProgressive = 0x4000;
        internal const uint WeightedRight = 0x1000000;
        internal const uint WeightedNextLeft = 0x2000000;
        internal const uint VelocityRight = 0x10000000;
        internal const uint VelocityNextLeft = 0x20000000;
    }

    internal static class UfbxiAnimReader
    {
        // ==================================================================
        // Tangent solvers
        // ==================================================================

        // C: ufbxi_solve_auto_tangent (ufbx.c:14109-14170). Returns the tangent slope as
        // float; every intermediate keeps C's double precision.
        internal static float SolveAutoTangent(UfbxiContext uc, double prevTime, double time, double nextTime,
            double prevValue, double value, double nextValue, float weightLeft, float weightRight,
            float autoBias, uint flags)
        {
            // Clamp tangent to zero if near either left or right key
            if ((flags & UfbxiKeyFlags.Clamp) != 0) {
                if (UfbxMath.FMin(UfbxMath.Abs(prevValue - value), UfbxMath.Abs(nextValue - value))
                    <= uc.Opts.KeyClampThreshold) {
                    return 0.0f;
                }
            }

            // Time-independent: Set the initial slope to be the difference between the two keyframes.
            double slope = (nextValue - prevValue) / (nextTime - prevTime);

            // Non-time-independent tangents seem to blend between left/right tangent and the total difference.
            if ((flags & UfbxiKeyFlags.TimeIndependent) == 0) {
                double slopeLeft = (value - prevValue) / (time - prevTime);
                double slopeRight = (nextValue - value) / (nextTime - time);
                double delta = (time - prevTime) / (nextTime - prevTime);
                slope = UfbxMath.Fma(slope, 0.5,
                    UfbxMath.Fma(slopeLeft, 1.0 - delta, slopeRight * delta) * 0.5);

                double biasWeight = UfbxMath.Abs(autoBias) / 100.0;
                if (biasWeight > 0.0001) {
                    double biasTarget = autoBias > 0.0 ? slopeRight : slopeLeft;
                    double biasDelta = biasTarget - slope;
                    slope = UfbxMath.Fma(slope, 1.0 - biasWeight, biasTarget * biasWeight);

                    // Auto bias larger than 500 (positive or negative) adds an absolute
                    // value to the slope, determined by `((bias-500) / 100)^2 * 40`.
                    double absBiasWeight = biasWeight - 5.0;
                    if (absBiasWeight > 0.0) {
                        double biasSign = UfbxMath.Abs(biasDelta) > 0.00001 ? biasDelta : autoBias;
                        biasSign = biasSign > 0.0 ? 1.0 : -1.0;
                        slope = UfbxMath.Fma(absBiasWeight * absBiasWeight * biasSign, 40.0, slope);
                    }
                }
            }

            // Prevent overshooting by clamping the slope in case either
            // tangent goes above/below the endpoints.
            if ((flags & UfbxiKeyFlags.ClampProgressive) != 0) {
                // Split the slope to sign and a non-negative absolute value
                double slopeSign = slope >= 0.0 ? 1.0 : -1.0;
                double absSlope = slopeSign * slope;

                // Find limits for the absolute value of the slope
                double rangeLeft = weightLeft * (time - prevTime);
                double rangeRight = weightRight * (nextTime - time);
                double maxLeft = rangeLeft > 0.0 ? slopeSign * (value - prevValue) / rangeLeft : 0.0;
                double maxRight = rangeRight > 0.0 ? slopeSign * (nextValue - value) / rangeRight : 0.0;

                // Clamp negative values and NaNs to zero
                if (!(maxLeft > 0.0)) maxLeft = 0.0;
                if (!(maxRight > 0.0)) maxRight = 0.0;

                // Clamp the absolute slope from both sides
                if (absSlope > maxLeft) absSlope = maxLeft;
                if (absSlope > maxRight) absSlope = maxRight;

                slope = slopeSign * absSlope;
            }

            return (float)slope;
        }

        // C: ufbxi_solve_auto_tangent_left (ufbx.c:14172-14193). `weightLeft` is unused in C
        // (`(void)weight_left`).
        internal static float SolveAutoTangentLeft(UfbxiContext uc, double prevTime, double time,
            double prevValue, double value, float weightLeft, float autoBias, uint flags)
        {
            if ((flags & UfbxiKeyFlags.ClampProgressive) != 0) return 0.0f;
            if ((flags & UfbxiKeyFlags.Clamp) != 0) {
                if (UfbxMath.Abs(prevValue - value) <= uc.Opts.KeyClampThreshold) {
                    return 0.0f;
                }
            }

            double slope = (value - prevValue) / (time - prevTime);

            if ((flags & UfbxiKeyFlags.TimeIndependent) == 0) {
                double absBiasWeight = UfbxMath.Abs(autoBias) / 100.0 - 5.0;
                if (absBiasWeight > 0.0) {
                    double biasSign = autoBias > 0.0 ? 1.0 : -1.0;
                    slope = UfbxMath.Fma(absBiasWeight * absBiasWeight * biasSign, 40.0, slope);
                }
            }

            return (float)slope;
        }

        // C: ufbxi_solve_auto_tangent_right (ufbx.c:14195-14216). `weightRight` is unused in C.
        internal static float SolveAutoTangentRight(UfbxiContext uc, double time, double nextTime,
            double value, double nextValue, float weightRight, float autoBias, uint flags)
        {
            if ((flags & UfbxiKeyFlags.ClampProgressive) != 0) return 0.0f;
            if ((flags & UfbxiKeyFlags.Clamp) != 0) {
                if (UfbxMath.Abs(nextValue - value) <= uc.Opts.KeyClampThreshold) {
                    return 0.0f;
                }
            }

            double slope = (nextValue - value) / (nextTime - time);

            if ((flags & UfbxiKeyFlags.TimeIndependent) == 0) {
                double absBiasWeight = UfbxMath.Abs(autoBias) / 100.0 - 5.0;
                if (absBiasWeight > 0.0) {
                    double biasSign = autoBias > 0.0 ? 1.0 : -1.0;
                    slope = UfbxMath.Fma(absBiasWeight * absBiasWeight * biasSign, 40.0, slope);
                }
            }

            return (float)slope;
        }

        // C: ufbxi_solve_tcb (ufbx.c:14218-14228).
        internal static void SolveTcb(out float pSlopeLeft, out float pSlopeRight,
            double tension, double continuity, double bias, double slopeLeft, double slopeRight, bool edge)
        {
            double factor = edge ? 1.0 : 0.5;
            double d00 = factor * (1.0 - tension) * (1.0 + bias) * (1.0 - continuity);
            double d01 = factor * (1.0 - tension) * (1.0 - bias) * (1.0 + continuity);
            double d10 = factor * (1.0 - tension) * (1.0 + bias) * (1.0 + continuity);
            double d11 = factor * (1.0 - tension) * (1.0 - bias) * (1.0 - continuity);

            pSlopeLeft = (float)UfbxMath.Fma(d00, slopeLeft, d01 * slopeRight);
            pSlopeRight = (float)UfbxMath.Fma(d10, slopeLeft, d11 * slopeRight);
        }

        // ==================================================================
        // Extrapolation
        // ==================================================================

        // C: ufbxi_read_extrapolation (ufbx.c:14230-14258).
        static void ReadExtrapolation(ref UfbxExtrapolation extrapolation, UfbxiNode node, string name)
        {
            UfbxiNode child = node.FindChild(name);
            UfbxExtrapolationMode mode = UfbxExtrapolationMode.Constant;
            int repeatCount = -1;

            if (child != null) {
                // C: ufbxi_find_val1(child, ufbxi_Type, "I", &mode_ch)
                UfbxiNode typeChild = child.FindChild(UfbxiStrings.Type);
                if (typeChild != null && typeChild.GetValI(0, out int modeCh)) {
                    switch (modeCh) {
                    case 'A': mode = UfbxExtrapolationMode.RepeatRelative; break;
                    case 'C': mode = UfbxExtrapolationMode.Constant; break;
                    case 'K': mode = UfbxExtrapolationMode.Slope; break;
                    case 'M': mode = UfbxExtrapolationMode.Mirror; break;
                    case 'R': mode = UfbxExtrapolationMode.Repeat; break;
                    default: /* Unknown */ break;
                    }

                    // C: ufbxi_find_val1(child, ufbxi_Repetition, "I", &repeat_count)
                    UfbxiNode repetitionChild = child.FindChild(UfbxiStrings.Repetition);
                    if (repetitionChild != null && repetitionChild.GetValI(0, out int foundRepeatCount)) {
                        if (foundRepeatCount < 0) {
                            foundRepeatCount = -1;
                        }
                        repeatCount = foundRepeatCount;
                    }
                }
            }

            extrapolation.Mode = mode;
            extrapolation.RepeatCount = repeatCount;
        }

        // ==================================================================
        // Post-7000 animation curves
        // ==================================================================

        // C: ((float*)attrs->data)[k] -- `KeyAttrDataFloat` is read with `ufbxi_find_array(..., '?')`,
        // so the element type is not gated: C reads raw 4-byte units regardless of the array
        // type code. The port therefore reads the raw little-endian bytes too (the arrays have
        // been endian-swapped to little-endian before the readers see them).
        static float AttrFloat(UfbxiValueArray attrs, int floatIndex)
        {
            return UfbxBitUtil.BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    attrs.Data.AsSpan(attrs.Offset + 4 * floatIndex)));
        }

        // C: `uint32_t packed_weights; memcpy(&packed_weights, &p_attr[2], sizeof(uint32_t))`.
        static uint AttrU32(UfbxiValueArray attrs, int floatIndex)
        {
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                attrs.Data.AsSpan(attrs.Offset + 4 * floatIndex));
        }

        // C: ufbxi_read_animation_curve (ufbx.c:14260-14535).
        internal static void ReadAnimationCurve(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxAnimCurve curve = UfbxiReadElement.PushElement<UfbxAnimCurve>(uc, info, UfbxElementType.AnimCurve);

            ReadExtrapolation(ref curve.PreExtrapolation, node, UfbxiStrings.Pre_Extrapolation);
            ReadExtrapolation(ref curve.PostExtrapolation, node, UfbxiStrings.Post_Extrapolation);

            if (uc.Opts.IgnoreAnimation) return;

            UfbxiValueArray times = node.FindArray(UfbxiStrings.KeyTime, 'l');
            UfbxiFail.CheckNoDesc(times != null, "times = ufbxi_find_array(node, ufbxi_KeyTime, 'l')");
            UfbxiValueArray values = node.FindArray(UfbxiStrings.KeyValueFloat, 'r');
            UfbxiFail.CheckNoDesc(values != null, "values = ufbxi_find_array(node, ufbxi_KeyValueFloat, 'r')");
            UfbxiValueArray attrFlags = node.FindArray(UfbxiStrings.KeyAttrFlags, 'i');
            UfbxiFail.CheckNoDesc(attrFlags != null, "attr_flags = ufbxi_find_array(node, ufbxi_KeyAttrFlags, 'i')");
            UfbxiValueArray attrs = node.FindArray(UfbxiStrings.KeyAttrDataFloat, '?');
            UfbxiFail.CheckNoDesc(attrs != null, "attrs = ufbxi_find_array(node, ufbxi_KeyAttrDataFloat, '?')");
            UfbxiValueArray refs = node.FindArray(UfbxiStrings.KeyAttrRefCount, 'i');
            UfbxiFail.CheckNoDesc(refs != null, "refs = ufbxi_find_array(node, ufbxi_KeyAttrRefCount, 'i')");

            // Time and value arrays that define the keyframes should be parallel
            UfbxiFail.CheckNoDesc(times.Size == values.Size, "times->size == values->size");

            // Flags and attributes are run-length encoded where KeyAttrRefCount (refs)
            // is an array that describes how many times to repeat a given flag/attribute.
            // Attributes consist of 4 32-bit floating point values per key.
            UfbxiFail.CheckNoDesc(attrFlags.Size == refs.Size, "attr_flags->size == refs->size");
            UfbxiFail.CheckNoDesc((long)attrs.Size == (long)refs.Size * 4, "attrs->size == refs->size * 4u");

            int numKeys = times.Size;
            UfbxKeyframe[] keys = new UfbxKeyframe[numKeys];

            curve.Keyframes = keys;

            int flagIx = 0;        // C: p_flag
            int attrFloatIx = 0;   // C: p_attr, in 4-byte float units
            int refIx = 0;         // C: p_ref (p_ref_end == refs.Size)

            // The previous key defines the weight/slope of the left tangent
            float slopeLeft = 0.0f;
            float weightLeft = 0.333333f;

            double prevTime = 0.0;
            double nextTime = 0.0;

            int refsLeft = 0;
            if (numKeys > 0) {
                nextTime = (double)times.GetInt64(0) / uc.KtimeSecDouble;
                if (refIx < refs.Size) refsLeft = refs.GetInt32(refIx);
            }

            for (int i = 0; i < numKeys; i++) {
                UfbxiFail.CheckNoDesc(refsLeft > 0, "refs_left > 0");

                UfbxKeyframe key = keys[i];

                double value = values.GetDouble(i);
                if (i == 0) {
                    curve.MinValue = value;
                    curve.MaxValue = value;
                } else {
                    curve.MinValue = curve.MinValue < value ? curve.MinValue : value;
                    // C: ufbxi_max_real(a,b) == a < b ? b : a (ufbx.c:1110)
                    curve.MaxValue = curve.MaxValue < value ? value : curve.MaxValue;
                }

                key.Time = nextTime;
                key.Value = value;

                if (i + 1 < numKeys) {
                    nextTime = (double)times.GetInt64(i + 1) / uc.KtimeSecDouble;
                }

                uint flags = (uint)attrFlags.GetInt32(flagIx);

                float slopeRight = AttrFloat(attrs, attrFloatIx + 0);
                float weightRight = 0.333333f;
                float nextSlopeLeft = AttrFloat(attrs, attrFloatIx + 1);
                float nextWeightLeft = 0.333333f;

                if ((flags & (UfbxiKeyFlags.WeightedRight | UfbxiKeyFlags.WeightedNextLeft)) != 0) {
                    // At least one of the tangents is weighted. The weights are encoded as
                    // two 0.4 _decimal_ fixed point values that are packed into 32 bits and
                    // interpreted as a 32-bit float.
                    uint packedWeights = AttrU32(attrs, attrFloatIx + 2);

                    if ((flags & UfbxiKeyFlags.WeightedRight) != 0) {
                        // Right tangent is weighted
                        weightRight = (float)(packedWeights & 0xffff) * 0.0001f;
                    }

                    if ((flags & UfbxiKeyFlags.WeightedNextLeft) != 0) {
                        // Next left tangent is weighted
                        nextWeightLeft = (float)(packedWeights >> 16) * 0.0001f;
                    }
                }

                if ((flags & UfbxiKeyFlags.InterpolationConstant) != 0) {
                    // Constant interpolation: Set cubic tangents to flat.

                    if ((flags & UfbxiKeyFlags.ConstantNext) != 0) {
                        // Take constant value from next key
                        key.Interpolation = UfbxInterpolation.ConstantNext;
                    } else {
                        // Take constant value from the previous key
                        key.Interpolation = UfbxInterpolation.ConstantPrev;
                    }

                    weightRight = nextWeightLeft = 0.333333f;
                    slopeRight = nextSlopeLeft = 0.0f;

                } else if ((flags & UfbxiKeyFlags.InterpolationCubic) != 0) {
                    // Cubic interpolation
                    key.Interpolation = UfbxInterpolation.Cubic;

                    if ((flags & UfbxiKeyFlags.TangentTcb) != 0) {
                        double tcbSlopeLeft = 0.0;
                        double tcbSlopeRight = 0.0;
                        bool tcbEdge = false;
                        if (i > 0 && key.Time > prevTime) {
                            tcbSlopeLeft = (key.Value - values.GetDouble(i - 1)) / (key.Time - prevTime);
                        } else {
                            tcbEdge = true;
                        }
                        if (i + 1 < numKeys && nextTime > key.Time) {
                            tcbSlopeRight = (values.GetDouble(i + 1) - key.Value) / (nextTime - key.Time);
                        } else {
                            tcbEdge = true;
                        }

                        SolveTcb(out slopeLeft, out slopeRight,
                            AttrFloat(attrs, attrFloatIx + 0), AttrFloat(attrs, attrFloatIx + 1),
                            AttrFloat(attrs, attrFloatIx + 2),
                            tcbSlopeLeft, tcbSlopeRight, tcbEdge);

                        // TODO: How to handle these?
                        nextSlopeLeft = 0.0f;
                        nextWeightLeft = 0.333333f;
                    } else if ((flags & UfbxiKeyFlags.TangentUser) != 0) {
                        // User tangents

                        if ((flags & UfbxiKeyFlags.TangentBroken) != 0) {
                            // Broken tangents: No need to modify slopes
                        } else {
                            // Unified tangents: Use right slope for both sides
                            // TODO: ??? slope_left = slope_right;
                        }

                    } else {
                        // TODO: Auto break (0x800)

                        if (i > 0 && i + 1 < numKeys && key.Time > prevTime && nextTime > key.Time) {
                            if (UfbxMath.Abs(slopeLeft + slopeRight) <= 0.0001f) {
                                slopeLeft = slopeRight = SolveAutoTangent(uc,
                                    prevTime, key.Time, nextTime,
                                    values.GetDouble(i - 1), key.Value, values.GetDouble(i + 1),
                                    weightLeft, weightRight, slopeRight, flags);
                            } else {
                                slopeLeft = SolveAutoTangent(uc,
                                    prevTime, key.Time, nextTime,
                                    values.GetDouble(i - 1), key.Value, values.GetDouble(i + 1),
                                    weightLeft, weightRight, -slopeLeft, flags);
                                slopeRight = SolveAutoTangent(uc,
                                    prevTime, key.Time, nextTime,
                                    values.GetDouble(i - 1), key.Value, values.GetDouble(i + 1),
                                    weightLeft, weightRight, slopeRight, flags);
                            }
                        } else if (i > 0 && key.Time > prevTime) {
                            slopeLeft = slopeRight = SolveAutoTangentLeft(uc,
                                prevTime, key.Time,
                                values.GetDouble(i - 1), key.Value,
                                weightLeft, -slopeLeft, flags);
                        } else if (i + 1 < numKeys && nextTime > key.Time) {
                            slopeLeft = slopeRight = SolveAutoTangentRight(uc,
                                key.Time, nextTime,
                                key.Value, values.GetDouble(i + 1),
                                weightRight, slopeRight, flags);
                        } else {
                            // Only / invalid keyframe: Set both slopes to zero
                            slopeLeft = slopeRight = 0.0f;
                        }
                    }

                } else {
                    // Linear or unknown interpolation: Set cubic tangents to match
                    // the linear interpolation with weights of 1/3.
                    key.Interpolation = UfbxInterpolation.Linear;

                    weightRight = 0.333333f;
                    nextWeightLeft = 0.333333f;

                    if (nextTime > key.Time) {
                        double deltaTime = nextTime - key.Time;
                        if (deltaTime > 0.0) {
                            double slope = (values.GetDouble(i + 1) - key.Value) / deltaTime;
                            slopeRight = nextSlopeLeft = (float)slope;
                        } else {
                            slopeRight = nextSlopeLeft = 0.0f;
                        }
                    } else {
                        slopeRight = nextSlopeLeft = 0.0f;
                    }
                }

                // Set the tangents based on weights (dx relative to the time difference
                // between the previous/next key) and slope (simply d = slope * dx)
                if (key.Time > prevTime) {
                    double delta = key.Time - prevTime;
                    key.Left.Dx = (float)(weightLeft * delta);
                    key.Left.Dy = key.Left.Dx * slopeLeft;
                } else {
                    key.Left.Dx = 0.0f;
                    key.Left.Dy = 0.0f;
                }

                if (nextTime > key.Time) {
                    double delta = nextTime - key.Time;
                    key.Right.Dx = (float)(weightRight * delta);
                    key.Right.Dy = key.Right.Dx * slopeRight;
                } else {
                    key.Right.Dx = 0.0f;
                    key.Right.Dy = 0.0f;
                }

                slopeLeft = nextSlopeLeft;
                weightLeft = nextWeightLeft;
                prevTime = key.Time;

                // Decrement attribute refcount and potentially move to the next one.
                refsLeft--;
                if (refsLeft == 0) {
                    flagIx++;
                    attrFloatIx += 4;
                    refIx++;
                    if (refIx < refs.Size) refsLeft = refs.GetInt32(refIx);
                }

                keys[i] = key;
            }
        }

        // ==================================================================
        // Animation stacks
        // ==================================================================

        // C: ufbxi_read_anim_stack (ufbx.c:14629-14646). Registers the stack in
        // `uc->anim_stack_map` keyed by the pooled name pointer, so `ufbxi_read_take()`
        // (ufbx.c:15713-15723) can fill in the fallback times later.
        internal static void ReadAnimStack(UfbxiContext uc, UfbxiNode node, UfbxiElementInfo info)
        {
            UfbxAnimStack stack = UfbxiReadElement.PushElement<UfbxAnimStack>(uc, info, UfbxElementType.AnimStack);

            uc.AnimInitStackMap();
            UfbxiMap<UfbxiTmpAnimStack, ulong> map = uc.AnimStackMap;

            // C: uint32_t hash = ufbxi_hash_ptr(info->name.data)
            ulong key = UfbxiPtrIdTable.IdOf(info.Name);
            uint hash = UfbxiHash.HashPtr(key);

            int index = map.Find(hash, key);
            if (index < 0) {
                index = map.Insert(hash, key);
                UfbxiFail.CheckNoDesc(index >= 0, "entry");
                UfbxiTmpAnimStack entry = map.Items[index];
                entry.Name = info.Name;
                entry.Stack = stack;
                map.Items[index] = entry;
            }
        }

        // ==================================================================
        // Pre-7000 "Take" based animation
        // ==================================================================

        // C: ufbxi_double_to_char (ufbx.c:15317-15324).
        internal static char DoubleToChar(double value)
        {
            if (value >= 0.0 && value <= 127.0) {
                return (char)(int)value;
            } else {
                return '\0';
            }
        }

        // C: `&value->default_value.v[i]` -- write one component of the anim value default.
        static void SetDefaultComponent(ref UfbxVec3 defaultValue, int index, double value)
        {
            if (index == 0) defaultValue.X = value;
            else if (index == 1) defaultValue.Y = value;
            else defaultValue.Z = value;
        }

        // C: ufbxi_read_take_anim_channel (ufbx.c:15326-15586). `p_default` is C's
        // `ufbx_real *p_default` == `&value->default_value.v[defaultIndex]` at the single
        // call site.
        static void ReadTakeAnimChannel(UfbxiContext uc, UfbxiNode node, ulong valueFbxId, string name,
            UfbxAnimValue value, int defaultIndex)
        {
            // C: ufbxi_ignore(ufbxi_find_val1(node, ufbxi_Default, "R", p_default))
            {
                UfbxiNode defaultChild = node.FindChild(UfbxiStrings.Default);
                if (defaultChild != null && defaultChild.GetValR(0, out double defaultValue)) {
                    SetDefaultComponent(ref value.DefaultValue, defaultIndex, defaultValue);
                }
            }

            // Find the key array, early return with success if not found as we may have only a default
            UfbxiValueArray keys = node.FindArray(UfbxiStrings.Key, 'd');
            if (keys == null) return;

            ulong curveFbxId = 0;
            UfbxAnimCurve curve = UfbxiReadElement.PushSyntheticElement<UfbxAnimCurve>(uc, out curveFbxId,
                node, name, UfbxElementType.AnimCurve);

            UfbxiReadElement.ConnectOp(uc, curveFbxId, valueFbxId, curve.Name);

            ReadExtrapolation(ref curve.PreExtrapolation, node, UfbxiStrings.Pre_Extrapolation);
            ReadExtrapolation(ref curve.PostExtrapolation, node, UfbxiStrings.Post_Extrapolation);

            if (uc.Opts.IgnoreAnimation) return;

            int keyVer = 0;
            // C: ufbxi_ignore(ufbxi_find_val1(node, ufbxi_KeyVer, "I", &key_ver))
            {
                UfbxiNode keyVerChild = node.FindChild(UfbxiStrings.KeyVer);
                if (keyVerChild != null) keyVerChild.GetValI(0, out keyVer);
            }
            if (keyVer <= 0) {
                if (uc.Version < 5000) {
                    keyVer = 4003;
                } else if (uc.Version < 6000) {
                    keyVer = 4004;
                } else {
                    keyVer = 4005;
                }
            }

            int numKeys = 0;
            {
                UfbxiNode keyCountChild = node.FindChild(UfbxiStrings.KeyCount);
                bool found = keyCountChild != null && keyCountChild.GetValZ(0, out numKeys);
                UfbxiFail.CheckNoDesc(found, "ufbxi_find_val1(node, ufbxi_KeyCount, \"Z\", &num_keys)");
            }
            UfbxKeyframe[] keyframes = new UfbxKeyframe[numKeys];
            curve.Keyframes = keyframes;

            float slopeLeft = 0.0f;
            float weightLeft = 0.333333f;

            double nextTime = 0.0;
            double nextValue = 0.0;
            double prevTime = 0.0;

            // The pre-7000 keyframe data is stored as a _heterogenous_ array containing 64-bit integers,
            // floating point values, and _bare characters_. We cast all values to double and interpret them.
            int dataIx = 0;        // C: data
            int dataSize = keys.Size; // C: data_end - data

            if (numKeys > 0) {
                UfbxiFail.CheckNoDesc(dataSize - dataIx >= 2, "data_end - data >= 2");
                nextTime = keys.GetDouble(dataIx + 0) / uc.KtimeSecDouble;
                nextValue = keys.GetDouble(dataIx + 1);
            }

            for (int i = 0; i < numKeys; i++) {
                UfbxKeyframe key = keyframes[i];

                if (i == 0) {
                    curve.MinValue = nextValue;
                    curve.MaxValue = nextValue;
                } else {
                    curve.MinValue = curve.MinValue < nextValue ? curve.MinValue : nextValue;
                    // C: ufbxi_max_real(a,b) == a < b ? b : a (ufbx.c:1110)
                    curve.MaxValue = curve.MaxValue < nextValue ? nextValue : curve.MaxValue;
                }

                // First three values: Time, Value, InterpolationMode
                UfbxiFail.CheckNoDesc(dataSize - dataIx >= 3, "data_end - data >= 3");
                key.Time = nextTime;
                key.Value = nextValue;
                char mode = DoubleToChar(keys.GetDouble(dataIx + 2));
                dataIx += 3;

                float slopeRight = 0.0f;
                float weightRight = 0.333333f;
                float nextSlopeLeft = 0.0f;
                float nextWeightLeft = 0.333333f;
                bool autoSlope = false;

                if (mode == 'U') {
                    // Cubic interpolation
                    key.Interpolation = UfbxInterpolation.Cubic;

                    UfbxiFail.CheckNoDesc(dataSize - dataIx >= 1, "data_end - data >= 1");
                    char slopeMode = DoubleToChar(keys.GetDouble(dataIx));
                    dataIx += 1;

                    int numWeights = 1;
                    if (slopeMode == 's' || slopeMode == 'b') {
                        // Slope mode 's'/'b' (standard? broken?) always have two explicit slopes
                        UfbxiFail.CheckNoDesc(dataSize - dataIx >= 2, "data_end - data >= 2");
                        slopeRight = (float)keys.GetDouble(dataIx + 0);
                        nextSlopeLeft = (float)keys.GetDouble(dataIx + 1);
                        dataIx += 2;
                        // TODO: This looks very suspicious, but we have observed files with
                        // KeyVer=4002 -> followed by 'n', then next key
                        // KeyVer=4003 -> no weight mode, directly followed by key
                        // KeyVer=4004 -> followed by 'n', then next key
                        if (keyVer == 4003) {
                            numWeights = 0;
                        }
                    } else if (slopeMode == 'a') {
                        // Parameterless slope mode 'a' seems to appear in baked animations. Let's just assume
                        // automatic tangents for now as they're the least likely to break with
                        // objectionable artifacts. We need to defer the automatic tangent resolve
                        // until we have read the next time/value.
                        autoSlope = true;
                        if (keyVer <= 4004) {
                            numWeights = 0;
                        }
                    } else if (slopeMode == 'p') {
                        // TODO: What is this mode? It seems to have negative values sometimes?
                        // Also it seems to have _two_ trailing weights values, currently observed:
                        // `n,n` and `a,X,Y,n`...
                        // Ignore unknown values for now
                        autoSlope = true;
                        UfbxiFail.CheckNoDesc(dataSize - dataIx >= 2, "data_end - data >= 2");
                        dataIx += 2;
                        if (keyVer <= 4004) {
                            numWeights = 1;
                        } else {
                            numWeights = 2;
                        }
                    } else if (slopeMode == 'q') {
                        // TODO: What is this mode? It seems to have negative values sometimes?
                        // Also it seems to have _two_ trailing weights values, currently observed:
                        // `d,d` and `n`...
                        // Ignore unknown values for now
                        autoSlope = true;
                        UfbxiFail.CheckNoDesc(dataSize - dataIx >= 2, "data_end - data >= 2");
                        dataIx += 2;
                        if (keyVer <= 4004) {
                            numWeights = 1;
                        } else {
                            numWeights = 2;
                        }
                    } else if (slopeMode == 't') {
                        // TODO: What is this mode? It seems that it does not have any weights and the
                        // third value seems _tiny_ (around 1e-30?)
                        // TODO: This looks like simple TCB parameters, currently falling back to auto.
                        autoSlope = true;
                        UfbxiFail.CheckNoDesc(dataSize - dataIx >= 3, "data_end - data >= 3");
                        dataIx += 3;
                        numWeights = 0;
                    } else if (slopeMode == 'd') {
                        // TODO: What is this mode? It has a single parameter (currently observed `0`)
                        // and a single weight.
                        autoSlope = true;
                        UfbxiFail.CheckNoDesc(dataSize - dataIx >= 1, "data_end - data >= 1");
                        dataIx += 1;
                    } else {
                        UfbxiFail.FailNoDesc("Unknown slope mode");
                    }

                    for (; numWeights > 0; numWeights--) {
                        UfbxiFail.CheckNoDesc(dataSize - dataIx >= 1, "data_end - data >= 1");
                        char weightMode = DoubleToChar(keys.GetDouble(dataIx));
                        dataIx += 1;

                        if (weightMode == 'n') {
                            // Automatic weights (0.3333...)
                        } else if (weightMode == 'a') {
                            // Manual weights: RightWeight, NextLeftWeight
                            UfbxiFail.CheckNoDesc(dataSize - dataIx >= 2, "data_end - data >= 2");
                            weightRight = (float)keys.GetDouble(dataIx + 0);
                            nextWeightLeft = (float)keys.GetDouble(dataIx + 1);
                            dataIx += 2;
                        } else if (weightMode == 'l') {
                            // Next left tangent is weighted
                            UfbxiFail.CheckNoDesc(dataSize - dataIx >= 1, "data_end - data >= 1");
                            nextWeightLeft = (float)keys.GetDouble(dataIx);
                            dataIx += 1;
                        } else if (weightMode == 'r') {
                            // Right tangent is weighted
                            UfbxiFail.CheckNoDesc(dataSize - dataIx >= 1, "data_end - data >= 1");
                            weightRight = (float)keys.GetDouble(dataIx);
                            dataIx += 1;
                        } else if (weightMode == 'c') {
                            // TODO: What is this mode? At least it has no parameters so let's
                            // just assume automatic weights for the time being (0.3333...)
                        } else {
                            UfbxiFail.FailNoDesc("Unknown weight mode");
                        }
                    }

                } else if (mode == 'L') {
                    // Linear interpolation: No parameters
                    key.Interpolation = UfbxInterpolation.Linear;
                } else if (mode == 'C') {
                    // Constant interpolation: Single parameter (use prev/next)
                    if (keyVer >= 4004) {
                        UfbxiFail.CheckNoDesc(dataSize - dataIx >= 1, "data_end - data >= 1");
                        key.Interpolation = DoubleToChar(keys.GetDouble(dataIx)) == 'n'
                            ? UfbxInterpolation.ConstantNext : UfbxInterpolation.ConstantPrev;
                        dataIx += 1;
                    } else {
                        key.Interpolation = UfbxInterpolation.ConstantPrev;
                    }
                } else {
                    UfbxiFail.FailNoDesc("Unknown key mode");
                }

                // Retrieve next key and value
                if (i + 1 < numKeys) {
                    UfbxiFail.CheckNoDesc(dataSize - dataIx >= 2, "data_end - data >= 2");
                    nextTime = keys.GetDouble(dataIx + 0) / uc.KtimeSecDouble;
                    nextValue = keys.GetDouble(dataIx + 1);
                }

                if (autoSlope) {
                    if (i > 0) {
                        slopeLeft = slopeRight = SolveAutoTangent(uc,
                            prevTime, key.Time, nextTime,
                            keyframes[i - 1].Value, key.Value, nextValue,
                            weightLeft, weightRight, 0.0f,
                            UfbxiKeyFlags.ClampProgressive | UfbxiKeyFlags.TimeIndependent);
                    } else {
                        slopeLeft = slopeRight = 0.0f;
                    }
                }

                // Set up linear cubic tangents if necessary
                if (key.Interpolation == UfbxInterpolation.Linear) {
                    if (nextTime > key.Time) {
                        double slope = (nextValue - key.Value) / (nextTime - key.Time);
                        slopeRight = nextSlopeLeft = (float)slope;
                    } else {
                        slopeRight = nextSlopeLeft = 0.0f;
                    }
                }

                if (key.Time > prevTime) {
                    double delta = key.Time - prevTime;
                    key.Left.Dx = (float)(weightLeft * delta);
                    key.Left.Dy = key.Left.Dx * slopeLeft;
                } else {
                    key.Left.Dx = 0.0f;
                    key.Left.Dy = 0.0f;
                }

                if (nextTime > key.Time) {
                    double delta = nextTime - key.Time;
                    key.Right.Dx = (float)(weightRight * delta);
                    key.Right.Dy = key.Right.Dx * slopeRight;
                } else {
                    key.Right.Dx = 0.0f;
                    key.Right.Dy = 0.0f;
                }

                slopeLeft = nextSlopeLeft;
                weightLeft = nextWeightLeft;
                prevTime = key.Time;

                keyframes[i] = key;
            }

            UfbxiFail.CheckNoDesc(dataIx == dataSize, "data == data_end");
        }

        // C: ufbxi_read_take_prop_channel (ufbx.c:15590-15667). C wraps the body in a
        // `ufbxi_recursive_function` macro (depth 2); the direct C# recursion is equivalent
        // since the "Transform" branch recurses only with name="T"/"R"/"S", which can never
        // match the `name == ufbxi_Transform` guard again.
        // NOTE: `internal` because ufbxi_read_legacy_model (Parse/Legacy.cs) calls it for the
        // pre-7000 `Channel` nodes, exactly like ufbx.c:16414.
        internal static void ReadTakePropChannel(UfbxiContext uc, UfbxiNode node, ulong targetFbxId, ulong layerFbxId,
            string name)
        {
            if (name == UfbxiStrings.Transform) {
                // Pre-7000 have transform keyframes in a deeply nested structure,
                // flatten it to make it resemble post-7000 structure a bit closer:
                // old: Model: { Channel: "Transform" { Channel: "T" { Channel "X": { ... } } } }
                // new: Model: { Channel: "Lcl Translation" { Channel "X": { ... } } }

                for (int i = 0; i < node.NumChildren; i++) {
                    UfbxiNode child = node.Children[i];
                    if (child.Name != UfbxiStrings.Channel) continue;

                    string oldName;
                    UfbxiFail.CheckNoDesc(child.GetValC(0, out oldName),
                        "ufbxi_get_val1(child, \"C\", (char**)&old_name)");

                    string newName;
                    if (oldName == UfbxiStrings.T) newName = UfbxiStrings.Lcl_Translation;
                    else if (oldName == UfbxiStrings.R) newName = UfbxiStrings.Lcl_Rotation;
                    else if (oldName == UfbxiStrings.S) newName = UfbxiStrings.Lcl_Scaling;
                    else {
                        continue;
                    }

                    // Read child as a top-level property channel
                    ReadTakePropChannel(uc, child, targetFbxId, layerFbxId, newName);
                }

            } else {

                // Pre-6000 FBX files store blend shape keys with a " (Shape)" suffix
                if (uc.Version < 6000) {
                    const string suffix = " (Shape)";
                    if (name.Length > suffix.Length
                        && string.CompareOrdinal(name, name.Length - suffix.Length, suffix, 0, suffix.Length) == 0) {
                        name = name.Substring(0, name.Length - suffix.Length);
                        UfbxiFail.CheckNoDesc(uc.StringPool.PushStringPlaceStr(ref name, false),
                            "ufbxi_push_string_place_str(&uc->string_pool, &name, false)");
                    }
                }

                // Find 1-3 channel nodes that contain a `Key:` node
                UfbxiNode[] channelNodes = new UfbxiNode[3];
                string[] channelNames = new string[3];
                int numChannelNodes = 0;

                if (node.FindChild(UfbxiStrings.Key) != null || node.FindChild(UfbxiStrings.Default) != null) {
                    // Channel has only a single curve
                    channelNodes[0] = node;
                    channelNames[0] = name;
                    numChannelNodes = 1;
                } else {
                    // Channel is a compound of multiple curves
                    for (int i = 0; i < node.NumChildren; i++) {
                        UfbxiNode child = node.Children[i];
                        if (child.Name != UfbxiStrings.Channel) continue;
                        if (child.FindChild(UfbxiStrings.Key) == null
                            && child.FindChild(UfbxiStrings.Default) == null) continue;
                        if (!child.GetValC(0, out channelNames[numChannelNodes])) continue;
                        channelNodes[numChannelNodes] = child;
                        if (++numChannelNodes == 3) break;
                    }
                }

                // Early return: No valid channels found, not an error
                if (numChannelNodes == 0) return;

                ulong valueFbxId = 0;
                UfbxAnimValue value = UfbxiReadElement.PushSyntheticElement<UfbxAnimValue>(uc, out valueFbxId,
                    node, name, UfbxElementType.AnimValue);

                // Add a "virtual" connection between the animated property and the layer/target
                UfbxiReadElement.ConnectOo(uc, valueFbxId, layerFbxId);
                UfbxiReadElement.ConnectOp(uc, valueFbxId, targetFbxId, name);

                for (int i = 0; i < numChannelNodes; i++) {
                    ReadTakeAnimChannel(uc, channelNodes[i], valueFbxId, channelNames[i], value, i);
                }
            }
        }

        // C: ufbxi_read_take_object (ufbx.c:15669-15689).
        static void ReadTakeObject(UfbxiContext uc, UfbxiNode node, ulong layerFbxId)
        {
            // Takes are used only in pre-7000 FBX versions so objects are identified
            // by their unique Type::Name pair that we use as unique IDs through the
            // pooled interned string pointers.
            string typeAndName;
            UfbxiFail.CheckNoDesc(node.GetValRawChar(0, out typeAndName),
                "ufbxi_get_val1(node, \"c\", (char**)&type_and_name)");
            ulong targetFbxId = UfbxiFbxId.SyntheticIdFromString(uc, typeAndName);
            UfbxiFail.CheckNoDesc(targetFbxId != 0, "target_fbx_id");

            // Add all suitable Channels as animated properties
            for (int i = 0; i < node.NumChildren; i++) {
                UfbxiNode child = node.Children[i];
                if (child.Name != UfbxiStrings.Channel) continue;
                string name;
                if (!child.GetValS(0, out name)) continue;

                ReadTakePropChannel(uc, child, targetFbxId, layerFbxId, name);
            }
        }

        // C: ufbxi_read_take (ufbx.c:15691-15752).
        internal static void ReadTake(UfbxiContext uc, UfbxiNode node)
        {
            UfbxProp[] tmpProps = new UfbxProp[4];
            int numProps = 0;
            // C: memset(tmp_props, 0, sizeof(tmp_props)) -- a fresh array is zero already.

            long start = 0, stop = 0;
            {
                UfbxiNode child = node.FindChild(UfbxiStrings.LocalTime);
                // C: ufbxi_find_val2(node, ufbxi_LocalTime, "LL", &start, &stop)
                if (child != null && child.GetValL(0, out start) && child.GetValL(1, out stop)) {
                    UfbxiReadElement.InitSyntheticIntProp(ref tmpProps[numProps++], UfbxiStrings.LocalStart,
                        start, UfbxPropType.Integer);
                    UfbxiReadElement.InitSyntheticIntProp(ref tmpProps[numProps++], UfbxiStrings.LocalStop,
                        stop, UfbxPropType.Integer);
                }
            }
            {
                UfbxiNode child = node.FindChild(UfbxiStrings.ReferenceTime);
                if (child != null && child.GetValL(0, out start) && child.GetValL(1, out stop)) {
                    UfbxiReadElement.InitSyntheticIntProp(ref tmpProps[numProps++], UfbxiStrings.ReferenceStart,
                        start, UfbxPropType.Integer);
                    UfbxiReadElement.InitSyntheticIntProp(ref tmpProps[numProps++], UfbxiStrings.ReferenceStop,
                        stop, UfbxPropType.Integer);
                }
            }

            string name;
            UfbxiFail.CheckNoDesc(node.GetValC(0, out name), "ufbxi_get_val1(node, \"C\", (char**)&name)");

            // Hack: For post-7000 files we are only interested in the animation times
            // for fallback in case the information is missing in the stacks.
            if (uc.Version >= 7000) {
                uc.AnimInitStackMap();
                UfbxiMap<UfbxiTmpAnimStack, ulong> map = uc.AnimStackMap;

                ulong key = UfbxiPtrIdTable.IdOf(name);
                uint hash = UfbxiHash.HashPtr(key);
                int index = map.Find(hash, key);

                if (index >= 0) {
                    UfbxAnimStack entryStack = map.Items[index].Stack;
                    if (entryStack.Props.Props == null || entryStack.Props.Props.Length == 0) {
                        UfbxProp[] props = new UfbxProp[numProps];
                        Array.Copy(tmpProps, props, numProps);
                        entryStack.Props.Props = props;
                    }
                }

                return;
            }

            ulong stackFbxId = 0, layerFbxId = 0;

            // Treat the Take as a post-7000 version animation stack and layer.
            UfbxAnimStack stack = UfbxiReadElement.PushSyntheticElement<UfbxAnimStack>(uc, out stackFbxId,
                node, name, UfbxElementType.AnimStack);

            {
                UfbxProp[] props = new UfbxProp[numProps];
                Array.Copy(tmpProps, props, numProps);
                stack.Props.Props = props;
            }

            UfbxAnimLayer layer = UfbxiReadElement.PushSyntheticElement<UfbxAnimLayer>(uc, out layerFbxId,
                node, UfbxiStrings.BaseLayer, UfbxElementType.AnimLayer);

            UfbxiReadElement.ConnectOo(uc, layerFbxId, stackFbxId);

            // Read all properties of objects included in the take
            for (int i = 0; i < node.NumChildren; i++) {
                UfbxiNode child = node.Children[i];
                // TODO: Do some object types have another name?
                if (child.Name != UfbxiStrings.Model) continue;

                ReadTakeObject(uc, child, layerFbxId);
            }
        }
    }
}
