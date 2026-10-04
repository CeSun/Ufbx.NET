// Element data model, ported from ufbx v0.23.1 (ufbx.h).
// Covers: ufbx_connection, ufbx_element, ufbx_unknown.

namespace Ufbx
{
    // C: typedef struct ufbx_connection
    // NOTE: Reference type on purpose: ufbx.c creates connection entries first and patches
    // `src`/`dst`/`src_prop`/`dst_prop` through pointers later, and element lists are _views_
    // into the scene-wide connection arrays, so shared identity is required for bit-exactness.
    public sealed class UfbxConnection
    {
        public UfbxElement Src;      // C: src
        public UfbxElement Dst;      // C: dst
        public string SrcProp;       // C: src_prop
        public string DstProp;       // C: dst_prop

        // C: `*dst = *src` struct copy in ufbxi_evaluate_imp (ufbx.c:26140-26141).
        internal UfbxConnection Clone() => (UfbxConnection)MemberwiseClone();
    }

    // C: struct ufbx_element (the "base-class" header shared by every element)
    public abstract class UfbxElement
    {
        public string Name;                          // C: name
        public UfbxProps Props = new UfbxProps();    // C: props (embedded value in C, never NULL)
        public uint ElementId;                       // C: element_id
        public uint TypedId;                         // C: typed_id
        public UfbxNode[] Instances;                 // C: instances (ufbx_node_list)
        public UfbxElementType Type;                 // C: type
        public UfbxConnection[] ConnectionsSrc;      // C: connections_src (ufbx_connection_list)
        public UfbxConnection[] ConnectionsDst;      // C: connections_dst (ufbx_connection_list)
        public UfbxDomNode DomNode;                  // C: dom_node
        public UfbxScene Scene;                      // C: scene

        // C: the `union { ufbx_element element; ... }` header -- in this port the element
        // _is_ the base class, so `x->element.name` maps to `x.Element.Name`.
        public UfbxElement Element => this;

        // C: `memcpy(dst, src, ufbx_element_type_size[type])` in ufbxi_evaluate_imp
        // (ufbx.c:26157-26159): every field value copied, referenced buffers left shared.
        // `MemberwiseClone()` copies the *runtime* type, so this single method covers all 42
        // element classes; UfbxiEvaluateScene translates the element reference fields after it.
        internal UfbxElement CloneElement() => (UfbxElement)MemberwiseClone();
    }

    // C: struct ufbx_unknown
    public sealed class UfbxUnknown : UfbxElement
    {
        // C: type (ufbx_string, the FBX format type string). Renamed because the inherited
        // `Type` member holds the element type enum.
        public string FbxType;   // C: type
        public string SuperType; // C: super_type
        public string SubType;   // C: sub_type
    }
}
