// Document object model and property data model, ported from ufbx v0.23.1 (ufbx.h).
// Covers: ufbx_dom_value, ufbx_dom_node, ufbx_prop, ufbx_props.

using System.Runtime.InteropServices;

namespace Ufbx.NET
{
    // C: typedef struct ufbx_dom_value
    public struct UfbxDomValue
    {
        public UfbxDomValueType Type;  // C: type
        public string ValueStr;        // C: value_str
        public byte[] ValueBlob;       // C: value_blob
        public long ValueInt;          // C: value_int
        public double ValueFloat;      // C: value_float
    }

    // C: struct ufbx_dom_node
    public sealed class UfbxDomNode
    {
        public string Name;              // C: name
        public UfbxDomNode[] Children;   // C: children (ufbx_dom_node_list)
        public UfbxDomValue[] Values;    // C: values (ufbx_dom_value_list)
    }

    // C: struct ufbx_prop
    // The C anonymous union of `value_real_arr[4] / value_real / value_vec2 / value_vec3 /
    // value_vec4` is reproduced with LayoutKind.Explicit so every member aliases the same
    // four doubles (offsets 48..72) exactly like the C layout.
    [StructLayout(LayoutKind.Explicit)]
    public struct UfbxProp
    {
        [FieldOffset(0)] public string Name; // C: name

        [FieldOffset(8)] public uint InternalKey; // C: _internal_key

        [FieldOffset(12)] public UfbxPropType Type;   // C: type
        [FieldOffset(16)] public UfbxPropFlags Flags; // C: flags

        [FieldOffset(24)] public string ValueStr;   // C: value_str
        [FieldOffset(32)] public byte[] ValueBlob;  // C: value_blob
        [FieldOffset(40)] public long ValueInt;     // C: value_int

        // C: union { ufbx_real value_real_arr[4]; ufbx_real value_real;
        //            ufbx_vec2 value_vec2; ufbx_vec3 value_vec3; ufbx_vec4 value_vec4; };
        [FieldOffset(48)] public double ValueReal;
        [FieldOffset(48)] public UfbxVec2 ValueVec2;
        [FieldOffset(48)] public UfbxVec3 ValueVec3;
        [FieldOffset(48)] public UfbxVec4 ValueVec4;
        [FieldOffset(48)] public double ValueReal0;
        [FieldOffset(56)] public double ValueReal1;
        [FieldOffset(64)] public double ValueReal2;
        [FieldOffset(72)] public double ValueReal3;

        // C: prop->value_real_arr[index]
        internal double RealAt(int index)
        {
            switch (index)
            {
                case 0: return ValueReal0;
                case 1: return ValueReal1;
                case 2: return ValueReal2;
                default: return ValueReal3;
            }
        }

        // C: &prop->value_real_arr[index] (address-of, so writes must go through the aliasing field)
        internal void SetRealAt(int index, double value)
        {
            switch (index)
            {
                case 0: ValueReal0 = value; break;
                case 1: ValueReal1 = value; break;
                case 2: ValueReal2 = value; break;
                case 3: ValueReal3 = value; break;
            }
        }
    }

    // C: struct ufbx_props
    public sealed class UfbxProps
    {
        public UfbxProp[] Props;   // C: props (ufbx_prop_list)
        public int NumAnimated;    // C: num_animated
        public UfbxProps Defaults; // C: defaults (ufbx_nullable ufbx_props *)

        // C: `ufbx_props` is embedded by value in every element / metadata / settings struct, so
        // `memcpy` gives the evaluated scene its own copy of the three fields while `props.data`
        // stays shared with the source scene (ufbx.c:26157-26159).
        internal UfbxProps CloneShallow() => (UfbxProps)MemberwiseClone();
    }
}
