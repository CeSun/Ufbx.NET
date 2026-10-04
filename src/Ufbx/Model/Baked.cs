// Baked animation value types ported from ufbx v0.23.1 (ufbx.h:4510-4607).
//
// `UFBX_LIST_TYPE(ufbx_xxx_list, ufbx_xxx)` (data + count) maps to `UfbxXxx[]`
// per PORTING_NOTES.md. `ufbx_baked_key_flags` is in Enums.cs (`UfbxBakedKeyFlags`).

namespace Ufbx
{
    // C: ufbx_baked_vec3 (ufbx.h:4510-4514)
    public struct UfbxBakedVec3
    {
        public double Time;                   // C: time — keyframe time in seconds
        public UfbxVec3 Value;                // C: value — value at `Time`, linearly interpolatable
        public UfbxBakedKeyFlags Flags;       // C: ufbx_baked_key_flags flags
    }

    // C: ufbx_baked_quat (ufbx.h:4518-4522)
    public struct UfbxBakedQuat
    {
        public double Time;                   // C: time — keyframe time in seconds
        public UfbxQuat Value;                // C: value — value at `Time`, (spherically) linearly interpolatable
        public UfbxBakedKeyFlags Flags;       // C: ufbx_baked_key_flags flags
    }

    // Baked transform animation for a single node. (C: ufbx_baked_node, ufbx.h:4527-4548)
    public struct UfbxBakedNode
    {
        public uint TypedId;                  // C: typed_id — maps to scene.nodes[] (ufbx.h:4530)
        public uint ElementId;                // C: element_id — maps to scene.elements[]

        public bool ConstantTranslation;      // C: constant_translation (ufbx.h:4535)
        public bool ConstantRotation;         // C: constant_rotation
        public bool ConstantScale;            // C: constant_scale

        public UfbxBakedVec3[] TranslationKeys; // C: ufbx_baked_vec3_list translation_keys (ufbx.h:4542)
        public UfbxBakedQuat[] RotationKeys;    // C: ufbx_baked_quat_list rotation_keys
        public UfbxBakedVec3[] ScaleKeys;       // C: ufbx_baked_vec3_list scale_keys
    }

    // Baked property animation. (C: ufbx_baked_prop, ufbx.h:4553-4560)
    public struct UfbxBakedProp
    {
        public string Name;                   // C: ufbx_string name — eg. "Visibility" (ufbx.h:4555)
        public bool ConstantValue;            // C: constant_value
        public UfbxBakedVec3[] Keys;          // C: ufbx_baked_vec3_list keys
    }

    // Baked property animation for a single element. (C: ufbx_baked_element, ufbx.h:4565-4570)
    public struct UfbxBakedElement
    {
        public uint ElementId;                // C: element_id — maps to scene.elements[] (ufbx.h:4567)
        public UfbxBakedProp[] Props;         // C: ufbx_baked_prop_list props
    }

    // C: ufbx_baked_anim_metadata (ufbx.h:4574-4580)
    // NOTE: memory statistics are meaningless in the managed port; fields retained
    // for declaration-order parity.
    public struct UfbxBakedAnimMetadata
    {
        public int ResultMemoryUsed; // C: result_memory_used (size_t)
        public int TempMemoryUsed;   // C: temp_memory_used (size_t)
        public int ResultAllocs;     // C: result_allocs (size_t)
        public int TempAllocs;       // C: temp_allocs (size_t)
    }

    // Animation baked into linearly interpolated keyframes. (C: ufbx_baked_anim, ufbx.h:4584-4607)
    // See `ufbx_bake_anim()`.
    public class UfbxBakedAnim
    {
        public UfbxBakedNode[] Nodes;         // C: ufbx_baked_node_list nodes (ufbx.h:4590)
        public UfbxBakedElement[] Elements;   // C: ufbx_baked_element_list elements (ufbx.h:4593)

        public double PlaybackTimeBegin;      // C: playback_time_begin (ufbx.h:4596)
        public double PlaybackTimeEnd;        // C: playback_time_end
        public double PlaybackDuration;       // C: playback_duration

        public double KeyTimeMin;             // C: key_time_min (ufbx.h:4601)
        public double KeyTimeMax;             // C: key_time_max

        public UfbxBakedAnimMetadata Metadata; // C: ufbx_baked_anim_metadata metadata (ufbx.h:4605)
    }
}
