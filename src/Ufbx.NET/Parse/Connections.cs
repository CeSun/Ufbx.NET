// Connection reader, ported from ufbx v0.23.1 ufbx.c:
//   ufbxi_read_connections (15239-15313)
//
// The reader only fills `uc->tmp_connections` (the port's `UfbxiContext.TmpConnections`); the
// scene-wide `ufbx_connection` lists are built later from it by `ufbxi_pre_finalize_scene()`
// (S2). The `ufbxi_get_val{1,3,4,5}` format strings map onto the `UfbxiNode.GetVal*`
// accessors: '_' ignores a value (no read), 'c'/'s' are the raw forms, 'C'/'S' the sanitized
// ones, and 'L' the 64-bit integer form.
using System;

namespace Ufbx.NET
{
    internal static class UfbxiConnections
    {
        // C: ufbxi_read_connections (ufbx.c:15239-15313).
        internal static void ReadConnections(UfbxiContext uc)
        {
            // Read the connections to the list first
            for (;;) {
                UfbxiNode node = UfbxiRoot.ParseToplevelChild(uc);
                if (node == null) break;

                string type;

                ulong srcId = 0, dstId = 0;
                string srcProp = string.Empty, dstProp = string.Empty;

                if (uc.Version < 7000) {
                    string srcName = null, dstName = null;
                    // Pre-7000 versions use Type::Name pairs as identifiers

                    if (!node.GetValRawChar(0, out type)) continue;

                    if (type == UfbxiStrings.OO) {
                        if (!node.GetValRawChar(1, out srcName) || !node.GetValRawChar(2, out dstName)) continue;
                    } else if (type == UfbxiStrings.OP) {
                        if (!node.GetValRawChar(1, out srcName) || !node.GetValRawChar(2, out dstName)
                            || !node.GetValRawString(3, out dstProp)) continue;
                    } else if (type == UfbxiStrings.PO) {
                        if (!node.GetValRawChar(1, out srcName) || !node.GetValRawString(2, out srcProp)
                            || !node.GetValRawChar(3, out dstName)) continue;
                    } else if (type == UfbxiStrings.PP) {
                        if (!node.GetValRawChar(1, out srcName) || !node.GetValRawString(2, out srcProp)
                            || !node.GetValRawChar(3, out dstName) || !node.GetValRawString(4, out dstProp)) continue;
                    } else {
                        // TODO: Strict mode?
                        continue;
                    }

                    if (srcProp.Length > 0) {
                        InternRaw(uc, ref srcProp);
                    }
                    if (dstProp.Length > 0) {
                        InternRaw(uc, ref dstProp);
                    }

                    srcId = UfbxiFbxId.SyntheticIdFromString(uc, srcName);
                    dstId = UfbxiFbxId.SyntheticIdFromString(uc, dstName);
                    UfbxiFail.CheckNoDesc(srcId != 0 && dstId != 0, "src_id && dst_id");

                } else {
                    // Post-7000 versions use proper unique 64-bit IDs

                    if (!node.GetValC(0, out type)) continue;

                    if (type == UfbxiStrings.OO) {
                        if (!node.GetValL(1, out long src) || !node.GetValL(2, out long dst)) continue;
                        srcId = unchecked((ulong)src);
                        dstId = unchecked((ulong)dst);
                    } else if (type == UfbxiStrings.OP) {
                        if (!node.GetValL(1, out long src) || !node.GetValL(2, out long dst)
                            || !node.GetValS(3, out dstProp)) continue;
                        srcId = unchecked((ulong)src);
                        dstId = unchecked((ulong)dst);
                    } else if (type == UfbxiStrings.PO) {
                        if (!node.GetValL(1, out long src) || !node.GetValS(2, out srcProp)
                            || !node.GetValL(3, out long dst)) continue;
                        srcId = unchecked((ulong)src);
                        dstId = unchecked((ulong)dst);
                    } else if (type == UfbxiStrings.PP) {
                        if (!node.GetValL(1, out long src) || !node.GetValS(2, out srcProp)
                            || !node.GetValL(3, out long dst) || !node.GetValS(4, out dstProp)) continue;
                        srcId = unchecked((ulong)src);
                        dstId = unchecked((ulong)dst);
                    } else {
                        // TODO: Strict mode?
                        continue;
                    }

                    srcId = UfbxiFbxId.ValidateFbxId(uc, srcId);
                    dstId = UfbxiFbxId.ValidateFbxId(uc, dstId);
                }

                uc.TmpConnections.Add(new UfbxiTmpConnection {
                    Src = srcId, Dst = dstId, SrcProp = srcProp, DstProp = dstProp,
                });
            }
        }

        // C: ufbxi_push_string_place_str(&uc->string_pool, &str, false).
        static void InternRaw(UfbxiContext uc, ref string str)
        {
            UfbxiFail.CheckNoDesc(str != null, "p_str");
            int outLength;
            string interned = uc.StringPool.PushString(str, 0, str.Length, out outLength, false);
            UfbxiFail.CheckNoDesc(interned != null, "ufbxi_push_string_place_str(&uc->string_pool, &str, false)");
            str = interned;
        }
    }
}
