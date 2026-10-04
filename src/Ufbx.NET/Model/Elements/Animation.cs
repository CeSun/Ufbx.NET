// Animation element types ported from ufbx v0.23.1 (ufbx.h).
// Element header fields are carried by the base UfbxElement.

namespace Ufbx.NET
{
	// C: typedef struct ufbx_tangent (ufbx.h:3189)
	// Tangent vector at a keyframe, may be split into left/right.
	// NOTE: dx/dy are `float` in C (not ufbx_real).
	public struct UfbxTangent
	{
		public float Dx; // < Derivative in the time axis
		public float Dy; // < Derivative in the (curve specific) value axis
	}

	// C: typedef struct ufbx_keyframe (ufbx.h:3209)
	// Single real value at a specified time; interpolation is determined by the previous key.
	public struct UfbxKeyframe
	{
		public double Time;
		public double Value;
		public UfbxInterpolation Interpolation;
		public UfbxTangent Left;
		public UfbxTangent Right;
	}

	// C: typedef struct ufbx_extrapolation (ufbx.h:3183)
	public struct UfbxExtrapolation
	{
		public UfbxExtrapolationMode Mode;

		// Count used for repeating modes. Negative means infinite repetition.
		public int RepeatCount;
	}

	// C: typedef struct ufbx_anim (ufbx.h:3088)
	// Animation descriptor used for evaluating animation.
	public sealed class UfbxAnim
	{
		// Time begin/end for the animation, both may be zero if absent.
		public double TimeBegin;
		public double TimeEnd;

		// List of layers in the animation.
		public UfbxAnimLayer[] Layers;

		// Optional overrides for weights for each layer in Layers.
		public double[] OverrideLayerWeights;

		// Sorted by element_id, prop_name.
		public UfbxPropOverride[] PropOverrides;

		// Sorted by node_id.
		public UfbxTransformOverride[] TransformOverrides;

		// Evaluate connected properties as if they would not be connected.
		public bool IgnoreConnections;

		// Custom ufbx_anim created by ufbx_create_anim().
		public bool Custom;

		// C: ufbxi_translate_anim() (ufbx.c:26104-26110) `ufbxi_push_copy(ufbx_anim, 1, anim)`:
		// field-wise copy, buffers still shared with the source scene.
		internal UfbxAnim CloneShallow() => (UfbxAnim)MemberwiseClone();
	}

	// C: struct ufbx_anim_stack (ufbx.h:3090)
	public class UfbxAnimStack : UfbxElement
	{
		public double TimeBegin;
		public double TimeEnd;

		public UfbxAnimLayer[] Layers;
		public UfbxAnim Anim;
	}

	// C: typedef struct ufbx_anim_prop (ufbx.h:3112)
	public sealed class UfbxAnimProp
	{
		public UfbxElement Element;

		// C: uint32_t _internal_key
		public uint InternalKey;

		public string PropName;
		public UfbxAnimValue AnimValue;

		// C: `props[i] = layer->anim_props.data[i]` (ufbx.c:26352).
		internal UfbxAnimProp Clone() => (UfbxAnimProp)MemberwiseClone();
	}

	// C: struct ufbx_anim_layer (ufbx.h:3116)
	public class UfbxAnimLayer : UfbxElement
	{
		public double Weight;
		public bool WeightIsAnimated;
		public bool Blended;
		public bool Additive;
		public bool ComposeRotation;
		public bool ComposeScale;

		public UfbxAnimValue[] AnimValues;

		// Sorted by element, prop_name.
		public UfbxAnimProp[] AnimProps;

		public UfbxAnim Anim;

		// C: _min_element_id / _max_element_id / _element_id_bitmask[4]
		public uint MinElementId;
		public uint MaxElementId;
		public uint[] ElementIdBitmask;
	}

	// C: struct ufbx_anim_value (ufbx.h:3141)
	public class UfbxAnimValue : UfbxElement
	{
		public UfbxVec3 DefaultValue;

		// Fixed-size array of 3 curves (X/Y/Z), each possibly null.
		public UfbxAnimCurve[] Curves;
	}

	// C: struct ufbx_anim_curve (ufbx.h:3213)
	public class UfbxAnimCurve : UfbxElement
	{
		// List of keyframes that define the curve.
		public UfbxKeyframe[] Keyframes;

		// Extrapolation before/after the curve.
		public UfbxExtrapolation PreExtrapolation;
		public UfbxExtrapolation PostExtrapolation;

		// Value range for all the keyframes.
		public double MinValue;
		public double MaxValue;

		// Time range for all the keyframes.
		public double MinTime;
		public double MaxTime;
	}
}
