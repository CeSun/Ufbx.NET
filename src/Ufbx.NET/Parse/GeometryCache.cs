// Geometry cache + external file loader, ported from ufbx v0.23.1 ufbx.c:
//
//   ufbxi_xml_* (xml tag/attrib/document + parser)        (ufbx.c:7243-7674)
//   ufbxi_parse_uint32_radix                              (ufbx.c:1830-1846)
//   ufbxi_update_scene_settings_obj                       (ufbx.c:23936-23947)
//   ufbxi_geometry_cache_imp / ufbxi_cache_tmp_channel    (ufbx.c:23953-23973)
//   ufbxi_cache_context                                   (ufbx.c:23987-24038)
//   ufbxi_cache_read / cache_skip                         (ufbx.c:24040-24120)
//   ufbxi_cache_mc_read_tag/u32/u64                       (ufbx.c:24124-24160)
//   ufbxi_cache_load_mc                                   (ufbx.c:24166-24247)
//   ufbxi_cache_load_pc2                                  (ufbx.c:24249-24296)
//   ufbxi_tmp_channel_less / cache_sort_tmp_channels      (ufbx.c:24298-24310)
//   ufbxi_cache_load_xml_imp / cache_load_xml / _file     (ufbx.c:24312-24444)
//   ufbxi_cache_try_open_file                             (ufbx.c:24446-24462)
//   ufbxi_cache_load_frame_files                          (ufbx.c:24464-24547)
//   ufbxi_cmp_cache_frame_less / cache_sort_frames        (ufbx.c:24549-24566)
//   ufbxi_cache_interpretation_names / cache_setup_channels (ufbx.c:24568-24641)
//   ufbxi_cache_load_imp / cache_load                     (ufbx.c:24644-24724)
//   ufbxi_load_geometry_cache (public)                    (ufbx.c:24726-24763)
//   ufbxi_free_geometry_cache_imp                         (ufbx.c:24765-24769)
//   ufbxi_external_file / less_external_file              (ufbx.c:24795-24819)
//   ufbxi_load_external_cache                             (ufbx.c:24821-24875)
//   ufbxi_find_external_file / load_external_files        (ufbx.c:24877-24952)
//
// MAPPING 口径:
//  - `ufbx_string` -> `string` (one char per byte, see UfbxiRawStr); `ufbx_blob` -> `byte[]`.
//  - C's `ufbxi_buf`/arena (tmp / tmp_stack / result) is NOT ported (PORTING_NOTES #4): the
//    `tmp_stack` push/pop sequences become typed `List<T>` scratch inside `UfbxiCacheContext`,
//    which preserves the exact ordering (`ufbxi_push_pop` takes the last `n` items in order).
//  - C's `ufbxi_check_err(&cc->error, x)` sites are plain failures (no description); the msg
//    form `ufbxi_check_err_msg(&cc->error, x, msg)` carries `msg`. `UfbxiCacheContext.Check`
//    records them into `cc.Error` and returns false (`ok`), exactly like C's `return 0`.
//  - The geometry cache operates on a `ufbx_stream` quad; the port reuses
//    `UfbxiLoad.UfbxiOpenFileStream` + `UfbxiSceneFiles.OpenFile` (the same stdio wiring).
//  - `cc->pos`/`cc->pos_end` are offsets (`PosInBuf`/`PosEndInBuf`) into `cc.Buffer`, mirroring
//    C's pointer arithmetic on the same buffer object.
using System;
using System.Collections.Generic;

namespace Ufbx.NET
{
    // ------------------------------------------------------------------
    // XML document model + parser (ufbx.c:7243-7674)
    // ------------------------------------------------------------------

    // C: ufbxi_xml_attrib (ufbx.c:7247-7250). Reference type: `ufbxi_xml_find_attrib` hands
    // back a pointer into the tag's attrib array.
    internal sealed class UfbxiXmlAttrib
    {
        public string Name;   // C: ufbx_string name
        public string Value;  // C: ufbx_string value
    }

    // C: ufbxi_xml_tag (ufbx.c:7252-7261).
    internal sealed class UfbxiXmlTag
    {
        public string Name = string.Empty;  // C: ufbx_string name
        public string Text = string.Empty;  // C: ufbx_string text
        public UfbxiXmlAttrib[] Attribs = Array.Empty<UfbxiXmlAttrib>();  // C: attribs/num_attribs
        public UfbxiXmlTag[] Children = Array.Empty<UfbxiXmlTag>();       // C: children/num_children
    }

    // C: ufbxi_xml_document (ufbx.c:7263-7266).
    internal sealed class UfbxiXmlDocument
    {
        public UfbxiXmlTag Root;  // C: root
    }

    // C: ufbxi_xml_context (ufbx.c:7268-7289). The arena buffers (`tmp_stack`/`result`) are
    // replaced by typed stacks (PORTING_NOTES #4).
    internal sealed class UfbxiXmlContext
    {
        public UfbxError Error;  // C: ufbx_error error

        // C: ufbxi_buf tmp_stack (`ufbxi_xml_tag` items + `ufbxi_xml_attrib` items). The C
        // stack interleaves both item kinds, but every push/pop is LIFO-balanced within one
        // frame (tags for children, attribs for the current tag), so a per-kind stack is
        // equivalent. `ufbxi_push_pop` moves the last `n` items, and the parent pops exactly
        // the children it pushed.
        public readonly List<UfbxiXmlTag> TmpStackTags = new List<UfbxiXmlTag>();
        public readonly List<UfbxiXmlAttrib> TmpStackAttribs = new List<UfbxiXmlAttrib>();

        public UfbxiXmlDocument Doc;  // C: doc

        // C: ufbx_read_fn *read_fn / void *read_user — the port's input stream.
        public UfbxInputStream ReadStream;

        // C: char *tok / tok_cap / tok_len — the token byte buffer, one char per byte. The port
        // keeps a growable `List<byte>` (NUL-terminated like C).
        public readonly List<byte> Tok = new List<byte>();
        public int TokLen;

        // C: const char *pos, *pos_end / char data[4096]. `Data` is the refill window,
        // `PosInData` is `pos - data`, `PosEnd` is `pos_end - data`.
        public byte[] Data = Array.Empty<byte>();
        public int PosInData;
        public int PosEnd;

        public bool IoError;  // C: io_error
    }

    // C: the xml ctype flags (ufbx.c:7291-7298).
    internal static class UfbxiXmlCtype
    {
        public const uint Whitespace = 0x1;
        public const uint SingleQuote = 0x2;
        public const uint DoubleQuote = 0x4;
        public const uint NameEnd = 0x8;
        public const uint TagStart = 0x10;
        public const uint EndOfFile = 0x20;

        // C: ufbxi_xml_ctype[256] (ufbx.c:7301-7304), generated by misc/gen_xml_ctype.py.
        // The first 64 entries are listed verbatim; entries beyond index 63 are zero.
        static readonly byte[] Table = MakeTable();
        static byte[] MakeTable()
        {
            byte[] t = new byte[256];
            byte[] head = new byte[] {
                32,0,0,0,0,0,0,0,0,9,9,0,0,9,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
                9,0,12,0,0,0,0,10,0,0,0,0,0,0,0,8,0,0,0,0,0,0,0,0,0,0,0,0,16,8,8,8,
            };
            Array.Copy(head, t, head.Length);
            return t;
        }

        public static uint Get(byte c) => Table[c];
    }

    internal static class UfbxiXml
    {
        // C: ufbxi_xml_refill (ufbx.c:7306-7315).
        internal static void Refill(UfbxiXmlContext xc)
        {
            byte[] buf = new byte[4096];
            int num = xc.ReadStream.Read(buf, 0, buf.Length);
            if (num < 0 || num < buf.Length) xc.IoError = true;
            xc.Data = buf;
            if (num < buf.Length) {
                // C: xc->data[num++] = '\0' — the extra NUL byte extends the window by one.
                buf[num] = 0;
                num++;
            }
            xc.PosInData = 0;
            xc.PosEnd = num;
        }

        // C: ufbxi_xml_advance (ufbx.c:7317-7320). `++xc->pos == xc->pos_end`.
        internal static void Advance(UfbxiXmlContext xc)
        {
            xc.PosInData++;
            if (xc.PosInData == xc.PosEnd) Refill(xc);
        }

        // C: *xc->pos.
        static byte Cur(UfbxiXmlContext xc) => xc.Data[xc.PosInData];

        // C: ufbxi_xml_push_token_char (ufbx.c:7322-7329).
        // C writes `xc->tok[xc->tok_len++] = c` into a reusable scratch buffer; `tok_len = 0`
        // rewinds it. The port keeps that by overwriting at index `TokLen` (growing on demand)
        // instead of appending, so a token after a `tok_len = 0` never sees stale bytes.
        static void PushTokenChar(UfbxiXmlContext xc, byte c)
        {
            if (xc.TokLen < xc.Tok.Count) xc.Tok[xc.TokLen] = c;
            else xc.Tok.Add(c);
            xc.TokLen++;
        }

        // C: ufbxi_xml_accept (ufbx.c:7331-7339).
        static bool Accept(UfbxiXmlContext xc, byte ch)
        {
            if (Cur(xc) == ch) { Advance(xc); return true; }
            return false;
        }

        // C: ufbxi_xml_skip_while (ufbx.c:7341-7346).
        static void SkipWhile(UfbxiXmlContext xc, uint ctypes)
        {
            while ((UfbxiXmlCtype.Get(Cur(xc)) & ctypes) != 0) Advance(xc);
        }

        // C: ufbxi_xml_skip_until_string (ufbx.c:7348-7380).
        static void SkipUntilString(UfbxiXmlContext xc, UfbxiXmlTag dstTag, string suffix)
        {
            xc.TokLen = 0;
            int ix = 0, matchLen = 0;
            int suffixLen = suffix.Length;
            byte[] buf = new byte[16];
            int wrapMask = buf.Length - 1;
            for (;;) {
                byte c = Cur(xc);
                if (c == 0) throw new UfbxParseError("Truncated file", true);
                Advance(xc);
                if (ix >= suffixLen) {
                    PushTokenChar(xc, buf[(ix - suffixLen) & wrapMask]);
                }

                buf[ix++ & wrapMask] = c;
                matchLen = 0;
                for (; matchLen < suffixLen; matchLen++) {
                    if (buf[(ix - suffixLen + matchLen) & wrapMask] != (byte)suffix[matchLen]) break;
                }
                if (matchLen == suffixLen) break;
            }

            PushTokenChar(xc, 0);
            if (dstTag != null) {
                dstTag.Text = UfbxiRawStr.FromBytes(xc.Tok.ToArray(), 0, xc.TokLen - 1);
            }
        }

        // C: ufbxi_xml_read_until (ufbx.c:7382-7457). `dstTag` + `dstIsName` select which field
        // of the tag/attrib receives the token (`null` dst means "discard").
        static void ReadUntil(UfbxiXmlContext xc, UfbxiXmlTag dstTag, bool dstIsName, UfbxiXmlAttrib dstAttrib, bool attribIsName, uint ctypes)
        {
            xc.TokLen = 0;
            for (;;) {
                byte c = Cur(xc);

                if (c == (byte)'&') {
                    int entityBegin = xc.TokLen;
                    for (;;) {
                        Advance(xc);
                        c = Cur(xc);
                        if (c == 0) throw new UfbxParseError("Failed to load", false);
                        if (c == (byte)';') break;
                        PushTokenChar(xc, c);
                    }
                    Advance(xc);
                    PushTokenChar(xc, 0);

                    // C: `char *entity = xc->tok + entity_begin;` — the entity text is the token
                    // bytes [entity_begin, TokLen-1) (before the NUL).
                    byte[] tokBytes = xc.Tok.ToArray();
                    int entityEnd = xc.TokLen - 1;
                    xc.TokLen = entityBegin;
                    xc.Tok.RemoveRange(entityBegin, xc.Tok.Count - entityBegin);

                    if (entityBegin < entityEnd && tokBytes[entityBegin] == (byte)'#') {
                        ulong code;
                        if (entityBegin + 1 < entityEnd && tokBytes[entityBegin + 1] == (byte)'x') {
                            code = UfbxiParseRadix.Uint32(tokBytes, entityBegin + 2, entityEnd, 16);
                        } else {
                            code = UfbxiParseRadix.Uint32(tokBytes, entityBegin + 1, entityEnd, 10);
                        }

                        byte[] bytes = new byte[5];
                        if (code < 0x80) {
                            bytes[0] = (byte)code;
                        } else if (code < 0x800) {
                            bytes[0] = (byte)(0xc0 | (code >> 6));
                            bytes[1] = (byte)(0x80 | (code & 0x3f));
                        } else if (code < 0x10000) {
                            bytes[0] = (byte)(0xe0 | (code >> 12));
                            bytes[1] = (byte)(0x80 | ((code >> 6) & 0x3f));
                            bytes[2] = (byte)(0x80 | (code & 0x3f));
                        } else {
                            bytes[0] = (byte)(0xf0 | (code >> 18));
                            bytes[1] = (byte)(0x80 | ((code >> 12) & 0x3f));
                            bytes[2] = (byte)(0x80 | ((code >> 6) & 0x3f));
                            bytes[3] = (byte)(0x80 | (code & 0x3f));
                        }
                        for (int b = 0; b < bytes.Length && bytes[b] != 0; b++) {
                            PushTokenChar(xc, bytes[b]);
                        }
                    } else {
                        byte ch = 0;
                        if (EntityEquals(tokBytes, entityBegin, entityEnd, "lt")) ch = (byte)'<';
                        else if (EntityEquals(tokBytes, entityBegin, entityEnd, "quot")) ch = (byte)'"';
                        else if (EntityEquals(tokBytes, entityBegin, entityEnd, "amp")) ch = (byte)'&';
                        else if (EntityEquals(tokBytes, entityBegin, entityEnd, "apos")) ch = (byte)'\'';
                        else if (EntityEquals(tokBytes, entityBegin, entityEnd, "gt")) ch = (byte)'>';
                        if (ch != 0) PushTokenChar(xc, ch);
                    }
                } else {
                    if ((UfbxiXmlCtype.Get(c) & ctypes) != 0) break;
                    if (c == 0) throw new UfbxParseError("Truncated file", true);
                    PushTokenChar(xc, c);
                    Advance(xc);
                }
            }

            PushTokenChar(xc, 0);
            if (dstTag != null) {
                string text = UfbxiRawStr.FromBytes(xc.Tok.ToArray(), 0, xc.TokLen - 1);
                if (dstIsName) dstTag.Name = text; else dstTag.Text = text;
            } else if (dstAttrib != null) {
                string text = UfbxiRawStr.FromBytes(xc.Tok.ToArray(), 0, xc.TokLen - 1);
                if (attribIsName) dstAttrib.Name = text; else dstAttrib.Value = text;
            }
        }

        // C: strcmp(entity, "...").
        static bool EntityEquals(byte[] tok, int begin, int end, string name)
        {
            if (end - begin != name.Length) return false;
            for (int i = 0; i < name.Length; i++) {
                if (tok[begin + i] != (byte)name[i]) return false;
            }
            return true;
        }

        // C: ufbxi_xml_parse_tag (ufbx.c:7460-7578). Returns false in C on a malformed tag;
        // the port throws (PORTING_NOTES #3). `p_closing` becomes `out bool closing`.
        static bool ParseTag(UfbxiXmlContext xc, int depth, out bool pClosing, string opening)
        {
            if (depth >= UfbxiCacheConsts.MaxXmlDepth) throw new UfbxParseError("Failed to load", false);

            pClosing = false;
            if (!Accept(xc, (byte)'<')) {
                if (Cur(xc) == 0) {
                    pClosing = true;
                } else {
                    ReadUntil(xc, null, false, null, false, UfbxiXmlCtype.TagStart | UfbxiXmlCtype.EndOfFile);
                    bool hasText = false;
                    for (int i = 0; i < xc.TokLen; i++) {
                        if ((UfbxiXmlCtype.Get(xc.Tok[i]) & UfbxiXmlCtype.Whitespace) == 0) { hasText = true; break; }
                    }

                    if (hasText) {
                        UfbxiXmlTag tag = new UfbxiXmlTag();
                        xc.TmpStackTags.Add(tag);
                        tag.Name = string.Empty;
                        tag.Text = UfbxiRawStr.FromBytes(xc.Tok.ToArray(), 0, xc.TokLen - 1);
                    }
                }
                return true;
            }

            if (Accept(xc, (byte)'/')) {
                ReadUntil(xc, null, false, null, false, UfbxiXmlCtype.NameEnd);
                // C: `opening && !strcmp(xc->tok, opening)`.
                string tok = UfbxiRawStr.FromBytes(xc.Tok.ToArray(), 0, xc.TokLen - 1);
                if (!(opening != null && tok == opening)) throw new UfbxParseError("Failed to load", false);
                SkipWhile(xc, UfbxiXmlCtype.Whitespace);
                if (!Accept(xc, (byte)'>')) return false;
                pClosing = true;
                return true;
            } else if (Accept(xc, (byte)'!')) {
                if (Accept(xc, (byte)'[')) {
                    string cdata = "CDATA[";
                    for (int i = 0; i < cdata.Length; i++) {
                        if (!Accept(xc, (byte)cdata[i])) return false;
                    }

                    UfbxiXmlTag tag = new UfbxiXmlTag();
                    xc.TmpStackTags.Add(tag);
                    SkipUntilString(xc, tag, "]]>");
                    tag.Name = string.Empty;
                } else if (Accept(xc, (byte)'-')) {
                    if (!Accept(xc, (byte)'-')) return false;
                    SkipUntilString(xc, null, "-->");
                } else {
                    // TODO: !DOCTYPE
                    SkipUntilString(xc, null, ">");
                }
                return true;
            } else if (Accept(xc, (byte)'?')) {
                SkipUntilString(xc, null, "?>");
                return true;
            }

            UfbxiXmlTag tag2 = new UfbxiXmlTag();
            xc.TmpStackTags.Add(tag2);
            ReadUntil(xc, tag2, true, null, false, UfbxiXmlCtype.NameEnd);
            tag2.Text = string.Empty;

            bool hasChildren = false;

            int numAttribs = 0;
            for (;;) {
                SkipWhile(xc, UfbxiXmlCtype.Whitespace);
                if (Accept(xc, (byte)'/')) {
                    if (!Accept(xc, (byte)'>')) return false;
                    break;
                } else if (Accept(xc, (byte)'>')) {
                    hasChildren = true;
                    break;
                } else {
                    UfbxiXmlAttrib attrib = new UfbxiXmlAttrib();
                    xc.TmpStackAttribs.Add(attrib);
                    ReadUntil(xc, null, false, attrib, true, UfbxiXmlCtype.NameEnd);
                    SkipWhile(xc, UfbxiXmlCtype.Whitespace);
                    if (!Accept(xc, (byte)'=')) return false;
                    SkipWhile(xc, UfbxiXmlCtype.Whitespace);
                    uint quoteCtype = 0;
                    if (Accept(xc, (byte)'"')) {
                        quoteCtype = UfbxiXmlCtype.DoubleQuote;
                    } else if (Accept(xc, (byte)'\'')) {
                        quoteCtype = UfbxiXmlCtype.SingleQuote;
                    } else {
                        throw new UfbxParseError("Bad attrib value", true);
                    }
                    ReadUntil(xc, null, false, attrib, false, quoteCtype);
                    Advance(xc);
                    numAttribs++;
                }
            }

            // C: tag->attribs = ufbxi_push_pop(&xc->result, &xc->tmp_stack, ufbxi_xml_attrib, n).
            // The popped items are the last `numAttribs` pushed.
            tag2.Attribs = PopAttribs(xc, numAttribs);

            if (hasChildren) {
                int childrenBegin = xc.TmpStackTags.Count;
                for (;;) {
                    bool closing;
                    ParseTag(xc, depth + 1, out closing, tag2.Name);
                    if (closing) break;
                }

                int numChildren = xc.TmpStackTags.Count - childrenBegin;
                tag2.Children = PopTags(xc, numChildren);
            }

            return true;
        }

        // C: `ufbxi_push_pop(&xc->result, &xc->tmp_stack, ufbxi_xml_tag, n)` — take the last `n`.
        static UfbxiXmlTag[] PopTags(UfbxiXmlContext xc, int count)
        {
            int start = xc.TmpStackTags.Count - count;
            UfbxiXmlTag[] items = new UfbxiXmlTag[count];
            for (int i = 0; i < count; i++) items[i] = xc.TmpStackTags[start + i];
            xc.TmpStackTags.RemoveRange(start, count);
            return items;
        }

        static UfbxiXmlAttrib[] PopAttribs(UfbxiXmlContext xc, int count)
        {
            int start = xc.TmpStackAttribs.Count - count;
            UfbxiXmlAttrib[] items = new UfbxiXmlAttrib[count];
            for (int i = 0; i < count; i++) items[i] = xc.TmpStackAttribs[start + i];
            xc.TmpStackAttribs.RemoveRange(start, count);
            return items;
        }

        // C: ufbxi_xml_parse_root (ufbx.c:7580-7604).
        static void ParseRoot(UfbxiXmlContext xc)
        {
            UfbxiXmlTag tag = new UfbxiXmlTag();
            tag.Name = string.Empty;
            tag.Text = string.Empty;

            for (;;) {
                bool closing;
                ParseTag(xc, 0, out closing, null);
                if (closing) break;
            }

            tag.Children = PopTags(xc, xc.TmpStackTags.Count);

            UfbxiXmlDocument doc = new UfbxiXmlDocument();
            doc.Root = tag;
            xc.Doc = doc;
        }

        // C: ufbxi_load_xml (ufbx.c:7614-7648). Returns null on failure (C returns NULL and
        // stashes `xc->error` into `error`).
        internal static UfbxiXmlDocument LoadXml(UfbxiXmlOpts opts, UfbxError error)
        {
            UfbxiXmlContext xc = new UfbxiXmlContext();
            xc.Error = new UfbxError();
            xc.ReadStream = opts.ReadStream;

            if (opts.PrefixLength > 0) {
                byte[] data = new byte[opts.PrefixLength];
                Array.Copy(opts.Prefix, opts.PrefixOffset, data, 0, opts.PrefixLength);
                xc.Data = data;
                xc.PosInData = 0;
                xc.PosEnd = opts.PrefixLength;
            } else {
                Refill(xc);
            }

            bool ok;
            try {
                ParseRoot(xc);
                ok = true;
            } catch (UfbxParseError) {
                ok = false;
            }

            if (ok) return xc.Doc;

            if (error != null) {
                error.Type = xc.Error.Type;
                error.Description = xc.Error.Description;
                error.Info = xc.Error.Info;
                error.InfoLength = xc.Error.InfoLength;
            }
            return null;
        }

        // C: ufbxi_free_xml (ufbx.c:7650-7654) — drops the whole document tree (GC handles it).
        internal static void FreeXml(UfbxiXmlDocument doc)
        {
        }

        // C: ufbxi_xml_find_child (ufbx.c:7656-7664).
        internal static UfbxiXmlTag FindChild(UfbxiXmlTag tag, string name)
        {
            for (int i = 0; i < tag.Children.Length; i++) {
                if (tag.Children[i].Name == name) return tag.Children[i];
            }
            return null;
        }

        // C: ufbxi_xml_find_attrib (ufbx.c:7666-7674).
        internal static UfbxiXmlAttrib FindAttrib(UfbxiXmlTag tag, string name)
        {
            for (int i = 0; i < tag.Attribs.Length; i++) {
                if (tag.Attribs[i].Name == name) return tag.Attribs[i];
            }
            return null;
        }
    }

    // Arguments of `ufbxi_load_xml` (C: ufbxi_xml_load_opts, ufbx.c:7606-7612).
    internal sealed class UfbxiXmlOpts
    {
        public UfbxInputStream ReadStream;  // C: read_fn / read_user
        public byte[] Prefix;               // C: const char *prefix
        public int PrefixOffset;
        public int PrefixLength;            // C: prefix_length
    }

    // C: ufbxi_parse_uint32_radix (ufbx.c:1830-1846). `str` is a byte slice [begin,end); the
    // loop stops at the first byte that is not a digit in `radix` (a NUL terminator ends it in C,
    // which the explicit `end` plays here).
    internal static class UfbxiParseRadix
    {
        internal static uint Uint32(byte[] str, int begin, int end, uint radix)
        {
            uint value = 0;
            for (int p = begin; ; p++) {
                char c = p < end ? (char)str[p] : '\0';
                if (c >= '0' && c <= '9') {
                    value = value * radix + (uint)(c - '0');
                } else if (radix == 16 && (c >= 'a' && c <= 'f')) {
                    value = value * radix + (uint)(c + (10 - 'a'));
                } else if (radix == 16 && (c >= 'A' && c <= 'F')) {
                    value = value * radix + (uint)(c + (10 - 'A'));
                } else {
                    break;
                }
            }
            return value;
        }
    }

    // C: little-endian scalar readers (ufbx.c:804-835). `ufbxi_read_u32` / `ufbxi_read_f32` read
    // in the machine byte order; the reference build is x86_64 (little-endian).
    internal static class UfbxiCacheRead
    {
        // C: ufbxi_read_u32 (ufbx.c:804-812).
        internal static uint U32(byte[] p, int offset)
        {
            return (uint)(
                (uint)p[offset + 0] << 0 |
                (uint)p[offset + 1] << 8 |
                (uint)p[offset + 2] << 16 |
                (uint)p[offset + 3] << 24);
        }

        internal static ulong U64(byte[] p, int offset)
        {
            return (ulong)U32(p, offset) | ((ulong)U32(p, offset + 4) << 32);
        }

        // C: ufbxi_read_f32 (ufbx.c:824-829) — reinterpret the little-endian bits as a float.
        internal static double F32(byte[] p, int offset)
        {
            uint u = U32(p, offset);
            float f = BitConverter.Int32BitsToSingle(unchecked((int)u));
            return f;
        }

        // C: ufbxi_read_f64 (ufbx.c:831-836) — reinterpret the little-endian bits as a double.
        internal static double F64(byte[] p, int offset)
        {
            ulong u = U64(p, offset);
            return BitConverter.Int64BitsToDouble(unchecked((long)u));
        }
    }

    // C: ufbxi_cache_data_format_size[] (ufbx.c:24162-24164).
    internal static class UfbxiCacheConsts
    {
        public static readonly byte[] DataFormatSize = new byte[] { 0, 4, 12, 8, 24 };

        // C: #define UFBXI_MAX_SKIP_SIZE 0x40000000 (ufbx.c:54) — the largest single skip request.
        // `UFBX_REGRESSION` redefines it to 128 (ufbx.c:1000-1002); the reference build is a
        // normal `-DNDEBUG` one, so it keeps 0x40000000.
        public const ulong MaxSkipSize = 0x40000000;

        // C: UFBXI_MAX_XML_DEPTH (ufbx.c:85).
        public const int MaxXmlDepth = 32;

        // C: UFBXI_CACHE_IMP_MAGIC (ufbx.c:6691) — `ufbxi_cache_mc_tag('C','H','A','C')`.
        public const uint CacheImpMagic = ('C' << 24) | ('H' << 16) | ('A' << 8) | 'C';
    }

    // C: ufbxi_cache_interpretation_name table (ufbx.c:24568-24577).
    internal struct UfbxiCacheInterpretationName
    {
        public UfbxCacheInterpretation Interpretation;  // C: interpretation
        public string Pattern;                          // C: const char *pattern
    }

    // C: ufbxi_geometry_cache_imp (ufbx.c:23953-23960). Owns the result `ufbx_geometry_cache`.
    internal sealed class UfbxiGeometryCacheImp
    {
        public UfbxGeometryCache Cache;  // C: ufbx_geometry_cache cache
        public uint Magic;               // C: uint32_t magic
        public bool OwnedByScene;        // C: bool owned_by_scene
        // C: ufbxi_buf string_buf — arena only, not ported.
    }

    // C: ufbxi_cache_tmp_channel (ufbx.c:23964-23973).
    internal sealed class UfbxiCacheTmpChannel
    {
        public string Name;              // C: ufbx_string name
        public string Interpretation;    // C: ufbx_string interpretation
        public uint SampleRate;          // C: uint32_t sample_rate
        public uint StartTime;           // C: uint32_t start_time
        public uint EndTime;             // C: uint32_t end_time
        public uint CurrentTime;         // C: uint32_t current_time
        public uint ConsecutiveFails;    // C: uint32_t consecutive_fails
        public bool TryLoad;             // C: bool try_load
    }

    // C: ufbxi_cache_xml_type / ufbxi_cache_xml_format (ufbx.c:23975-23985).
    internal enum UfbxiCacheXmlType
    {
        None,
        FilePerFrame,
        SingleFile,
    }

    internal enum UfbxiCacheXmlFormat
    {
        None,
        Mcc,
        Mcx,
    }

    // C: ufbxi_cache_context (ufbx.c:23987-24038). The arena buffers and allocator bookkeeping
    // are not ported (PORTING_NOTES #4); `tmp_stack` becomes a typed `List<UfbxCacheFrame>`,
    // `tmp_stack_strings` the string pushes (extra_info), and `cc->error` the port's `UfbxError`.
    internal sealed class UfbxiCacheContext
    {
        public UfbxError Error = new UfbxError();  // C: ufbx_error error
        public string Filename;                    // C: ufbx_string filename
        public bool OwnedByScene;                  // C: bool owned_by_scene
        public bool IgnoreIfNotFound;              // C: bool ignore_if_not_found

        public UfbxGeometryCacheOpts Opts = new UfbxGeometryCacheOpts();  // C: opts

        // C: ufbxi_buf result / tmp / tmp_stack — only the `tmp_stack` item order is observable
        // (`ufbxi_push_pop`), reproduced by these lists.
        public readonly List<UfbxCacheFrame> TmpStack = new List<UfbxCacheFrame>();
        public readonly List<string> TmpStackStrings = new List<string>();

        public UfbxiCacheTmpChannel[] Channels = Array.Empty<UfbxiCacheTmpChannel>();  // C: channels/num_channels
        public int NumChannels;

        public UfbxiStringPool StringPool;   // C: ufbxi_string_pool string_pool
        public UfbxOpenFileCb OpenFileCb = new UfbxOpenFileCb();  // C: open_file_cb

        public double FramesPerSecond;       // C: double frames_per_second

        public string StreamFilename;        // C: ufbx_string stream_filename
        public UfbxInputStream Stream;       // C: ufbx_stream stream

        public bool McFor8;                  // C: bool mc_for8

        public bool XmlLoaded;               // C: bool xml_loaded
        public string XmlFilename;           // C: ufbx_string xml_filename
        public uint XmlTicksPerFrame;        // C: uint32_t xml_ticks_per_frame
        public UfbxiCacheXmlType XmlType;    // C: xml_type
        public UfbxiCacheXmlFormat XmlFormat;// C: xml_format

        public string ChannelName;           // C: ufbx_string channel_name

        public byte[] NameBuf = Array.Empty<byte>(); // C: char *name_buf / size_t name_cap

        public ulong FileOffset;             // C: uint64_t file_offset
        public byte[] PosBuf;                // C: const char* window underlying cc->pos / pos_end

        public UfbxGeometryCache Cache = new UfbxGeometryCache();  // C: ufbx_geometry_cache cache
        public UfbxiGeometryCacheImp Imp;                          // C: ufbxi_geometry_cache_imp *imp

        public readonly byte[] Buffer = new byte[128];  // C: char buffer[128]
        public int BufferLen;                            // C: pos_end - buffer
        public int BufferPos;                            // C: pos - buffer

        // The live read window: either the temporary 128-byte `Buffer` or an XML prefix array.
        internal byte[] Window = Array.Empty<byte>();
        internal int WindowLength;   // C: pos_end - window_base

        // -- error helpers (mirror ufbxi_check_err / *_err_msg writing into cc->error) --

        // C: ufbxi_check_err(&cc->error, cond) -> ufbxi_fail_err: plain failure (no description).
        public bool Check(bool cond, string condText)
        {
            if (!cond) return false;
            return true;
        }

        // C: ufbxi_fail_err(&cc->error, desc): no description.
        public bool Fail()
        {
            return false;
        }

        // C: ufbxi_check_err_msg(&cc->error, cond, msg).
        public bool CheckMsg(bool cond, string msg)
        {
            if (!cond) { SetDescription(msg); return false; }
            return true;
        }

        // C: ufbxi_fail_err_msg(&cc->error, desc, msg).
        public bool FailMsg(string msg)
        {
            SetDescription(msg);
            return false;
        }

        // C: ufbxi_fail_err_info + ufbxi_fail_err_msg (ufbx.c:24666-24667): records the failing
        // filename into `error.info` before the message (`ufbx_error.info` is a UTF-8 char buffer,
        // so this port reuses `UfbxiPrint.SetErrInfo`).
        public bool FailInfoMsg(string info, int infoLen, string msg)
        {
            if (info != null) UfbxiPrint.SetErrInfo(Error, info);
            SetDescription(msg);
            return false;
        }


        // C: ufbxi_fail_imp_err writes `description` only if still unset.
        void SetDescription(string msg)
        {
            if (string.IsNullOrEmpty(Error.Description)) Error.Description = msg;
        }
    }

    // C: ufbxi_external_file_type (ufbx.c:24797-24799).
    internal enum UfbxiExternalFileType
    {
        GeometryCache,
    }

    // C: ufbxi_external_file (ufbx.c:24801-24808).
    internal sealed class UfbxiExternalFile
    {
        public UfbxiExternalFileType Type;   // C: type
        public string Filename;              // C: ufbx_string filename
        public string AbsoluteFilename;      // C: ufbx_string absolute_filename
        public int Index;                    // C: size_t index
        public object Data;                  // C: void *data
        public int DataSize;                 // C: size_t data_size
    }

    // ------------------------------------------------------------------
    // Geometry cache loading (ufbx.c:23936-24952)
    // ------------------------------------------------------------------

    internal static class UfbxiGeometryCacheLoader
    {
        // C: ufbxi_update_scene_settings_obj (ufbx.c:23936-23947).
        internal static void UpdateSceneSettingsObj(UfbxiContext uc)
        {
            UfbxSceneSettings settings = uc.Scene.Settings;
            settings.OriginalUnitMeters = settings.UnitMeters = uc.Opts.ObjUnitMeters;
            if (UfbxCoordinateAxes.IsValid(uc.Opts.ObjAxes)) {
                settings.Axes = uc.Opts.ObjAxes;
            } else {
                settings.Axes.Right = UfbxCoordinateAxis.Unknown;
                settings.Axes.Up = UfbxCoordinateAxis.Unknown;
                settings.Axes.Front = UfbxCoordinateAxis.Unknown;
            }
        }

        // C: ufbxi_cache_read (ufbx.c:24040-24082).
        static bool CacheRead(UfbxiCacheContext cc, byte[] dst, int dstOffset, int size, bool allowEof)
        {
            // C: buffered = min((size_t)(cc->pos_end - cc->pos), size); memcpy(dst, cc->pos, buffered).
            int pos = cc.BufferPos;
            int buffered = Math.Min(cc.BufferLen - pos, size);
            Array.Copy(cc.Buffer, pos, dst, dstOffset, buffered);
            cc.BufferPos += buffered;
            size -= buffered;
            cc.FileOffset += (ulong)buffered;
            if (size == 0) return true;
            dstOffset += buffered;

            if (size >= cc.Buffer.Length) {
                int numRead = cc.Stream.Read(dst, dstOffset, size);
                if (numRead < 0) numRead = 0; // C: read_fn returns SIZE_MAX on error
                if (!cc.CheckMsg(numRead <= size, "IO error")) return false;
                if (!allowEof) {
                    if (!cc.CheckMsg(numRead == size, "Truncated file")) return false;
                }
                cc.FileOffset += (ulong)numRead;
                size -= numRead;
                dstOffset += numRead;
            } else {
                int numRead = cc.Stream.Read(cc.Buffer, 0, cc.Buffer.Length);
                if (numRead < 0) numRead = 0;
                if (!cc.CheckMsg(numRead <= cc.Buffer.Length, "IO error")) return false;
                if (!allowEof) {
                    if (!cc.CheckMsg(numRead >= size, "Truncated file")) return false;
                }
                cc.BufferPos = 0;
                cc.BufferLen = cc.Buffer.Length;

                Array.Copy(cc.Buffer, 0, dst, dstOffset, size);
                cc.BufferPos += size;
                cc.FileOffset += (ulong)size;

                int numWritten = Math.Min(size, numRead);
                size -= numWritten;
                dstOffset += numWritten;
            }

            if (size > 0) {
                Array.Clear(dst, dstOffset, size);
            }

            return true;
        }

        // C: ufbxi_cache_skip (ufbx.c:24084-24120).
        static bool CacheSkip(UfbxiCacheContext cc, ulong size)
        {
            cc.FileOffset += size;

            ulong buffered = Math.Min((ulong)(cc.BufferLen - cc.BufferPos), size);
            cc.BufferPos += (int)buffered;
            size -= buffered;

            if (cc.Stream.CanSkip) {
                while (size >= UfbxiCacheConsts.MaxSkipSize) {
                    size -= UfbxiCacheConsts.MaxSkipSize;
                    if (!cc.CheckMsg(cc.Stream.Skip((int)(UfbxiCacheConsts.MaxSkipSize - 1)), "Truncated file")) return false;

                    // Check that we can read at least one byte in case the file is broken
                    // and causes us to seek indefinitely forwards as `fseek()` does not
                    // report if we hit EOF...
                    byte[] singleByte = new byte[1];
                    int numRead = cc.Stream.Read(singleByte, 0, 1);
                    if (numRead < 0) numRead = int.MaxValue; // SIZE_MAX collapses to "> 1"
                    if (!cc.CheckMsg(numRead <= 1, "IO error")) return false;
                    if (!cc.CheckMsg(numRead == 1, "Truncated file")) return false;
                }

                if (size > 0) {
                    if (!cc.CheckMsg(cc.Stream.Skip((int)size), "Truncated file")) return false;
                }
            } else {
                byte[] skipBuf = new byte[2048];
                while (size > 0) {
                    int toSkip = (int)Math.Min(size, (ulong)skipBuf.Length);
                    size -= (ulong)toSkip;
                    // C: `ufbxi_check_err_msg(.., cc->stream.read_fn(..), ..)` (ufbx.c:24116) tests the
                    // byte count for *truth*, so only a zero-length read fails: a short read and even a
                    // `SIZE_MAX` error return are accepted and the loop just makes progress on `size`.
                    if (!cc.CheckMsg(cc.Stream.Read(skipBuf, 0, toSkip) != 0, "Truncated file")) return false;
                }
            }

            return true;
        }

        // C: #define ufbxi_cache_mc_tag(a,b,c,d) (ufbx.c:24122).
        static uint McTag(char a, char b, char c, char d)
        {
            return (uint)a << 24 | (uint)b << 16 | (uint)c << 8 | (uint)d;
        }

        // C: ufbxi_cache_mc_read_tag (ufbx.c:24124-24133).
        static bool CacheMcReadTag(UfbxiCacheContext cc, out uint pTag)
        {
            byte[] buf = new byte[4];
            pTag = 0;
            if (!cc.Check(CacheRead(cc, buf, 0, 4, true), "ufbxi_cache_read")) return false;
            pTag = (uint)buf[0] << 24 | (uint)buf[1] << 16 | (uint)buf[2] << 8 | (uint)buf[3];
            if (pTag == McTag('F', 'O', 'R', '8')) {
                cc.McFor8 = true;
            }
            return true;
        }

        // C: ufbxi_cache_mc_read_u32 (ufbx.c:24135-24144).
        static bool CacheMcReadU32(UfbxiCacheContext cc, out uint pValue)
        {
            byte[] buf = new byte[4];
            pValue = 0;
            if (!cc.Check(CacheRead(cc, buf, 0, 4, false), "ufbxi_cache_read")) return false;
            pValue = (uint)buf[0] << 24 | (uint)buf[1] << 16 | (uint)buf[2] << 8 | (uint)buf[3];
            if (cc.McFor8) {
                if (!cc.Check(CacheRead(cc, buf, 0, 4, false), "ufbxi_cache_read")) return false;
            }
            return true;
        }

        // C: ufbxi_cache_mc_read_u64 (ufbx.c:24146-24160).
        static bool CacheMcReadU64(UfbxiCacheContext cc, out ulong pValue)
        {
            pValue = 0;
            if (!cc.McFor8) {
                uint v32;
                if (!cc.Check(CacheMcReadU32(cc, out v32), "ufbxi_cache_mc_read_u32")) return false;
                pValue = v32;
            } else {
                byte[] buf = new byte[8];
                if (!cc.Check(CacheRead(cc, buf, 0, 8, false), "ufbxi_cache_read")) return false;
                uint hi = (uint)buf[0] << 24 | (uint)buf[1] << 16 | (uint)buf[2] << 8 | (uint)buf[3];
                uint lo = (uint)buf[4] << 24 | (uint)buf[5] << 16 | (uint)buf[6] << 8 | (uint)buf[7];
                pValue = (ulong)hi << 32 | (ulong)lo;
            }
            return true;
        }

        // C: ufbxi_cache_load_mc (ufbx.c:24166-24247).
        static bool CacheLoadMc(UfbxiCacheContext cc)
        {
            uint version = 0, timeStart = 0, timeEnd = 0;
            uint count = 0, time = 0;
            byte[] skipBuf = new byte[8];

            for (;;) {
                uint tag;
                ulong size;
                if (!cc.Check(CacheMcReadTag(cc, out tag), "ufbxi_cache_mc_read_tag")) return false;
                if (tag == 0) break;

                if (tag == McTag('C', 'A', 'C', 'H') || tag == McTag('M', 'Y', 'C', 'H')) {
                    continue;
                }
                if (cc.McFor8) {
                    if (!cc.Check(CacheRead(cc, skipBuf, 0, 4, false), "ufbxi_cache_read")) return false;
                }

                if (!cc.Check(CacheMcReadU64(cc, out size), "ufbxi_cache_mc_read_u64")) return false;
                ulong begin = cc.FileOffset;

                ulong alignment = cc.McFor8 ? 8u : 4u;

                UfbxCacheDataFormat format = UfbxCacheDataFormat.Unknown;
                switch (tag) {
                    case var t when t == McTag('F', 'O', 'R', '4'): cc.McFor8 = false; break;
                    case var t when t == McTag('F', 'O', 'R', '8'): cc.McFor8 = true; break;
                    case var t when t == McTag('V', 'R', 'S', 'N'):
                        if (!cc.Check(CacheMcReadU32(cc, out version), "ufbxi_cache_mc_read_u32")) return false;
                        break;
                    case var t when t == McTag('S', 'T', 'I', 'M'):
                        if (!cc.Check(CacheMcReadU32(cc, out timeStart), "ufbxi_cache_mc_read_u32")) return false;
                        time = timeStart;
                        break;
                    case var t when t == McTag('E', 'T', 'I', 'M'):
                        if (!cc.Check(CacheMcReadU32(cc, out timeEnd), "ufbxi_cache_mc_read_u32")) return false;
                        break;
                    case var t when t == McTag('T', 'I', 'M', 'E'):
                        if (!cc.Check(CacheMcReadU32(cc, out time), "ufbxi_cache_mc_read_u32")) return false;
                        break;
                    case var t when t == McTag('C', 'H', 'N', 'M'): {
                        if (!cc.Check(size > 0 && size < ulong.MaxValue, "size > 0 && size < SIZE_MAX")) return false;
                        int length = (int)(size - 1);
                        int paddedLength = (int)((size + alignment - 1) & ~(alignment - 1));
                        byte[] nameBuf = new byte[paddedLength];
                        if (!cc.Check(CacheRead(cc, nameBuf, 0, paddedLength, false), "ufbxi_cache_read")) return false;
                        cc.NameBuf = nameBuf;
                        // C: cc->channel_name.data = cc->name_buf; .length = length;
                        //    ufbxi_push_string_place_str(&cc->string_pool, &cc->channel_name, false)
                        int channelNameLen;
                        cc.ChannelName = cc.StringPool.PushString(UfbxiRawStr.FromBytes(nameBuf, 0, length), 0, length, out channelNameLen, false);
                    } break;
                    case var t when t == McTag('S', 'I', 'Z', 'E'):
                        if (!cc.Check(CacheMcReadU32(cc, out count), "ufbxi_cache_mc_read_u32")) return false;
                        break;
                    case var t when t == McTag('F', 'V', 'C', 'A'): format = UfbxCacheDataFormat.Vec3Float; break;
                    case var t when t == McTag('D', 'V', 'C', 'A'): format = UfbxCacheDataFormat.Vec3Double; break;
                    case var t when t == McTag('F', 'B', 'C', 'A'): format = UfbxCacheDataFormat.RealFloat; break;
                    case var t when t == McTag('D', 'B', 'C', 'A'): format = UfbxCacheDataFormat.RealDouble; break;
                    case var t when t == McTag('D', 'B', 'L', 'A'): format = UfbxCacheDataFormat.RealDouble; break;
                    default: return cc.FailMsg("Unknown tag");
                }

                if (format != UfbxCacheDataFormat.Unknown) {
                    UfbxCacheFrame frame = new UfbxCacheFrame();
                    cc.TmpStack.Add(frame);

                    uint elemSize = UfbxiCacheConsts.DataFormatSize[(int)format];
                    ulong totalSize = (ulong)elemSize * count;
                    if (!cc.Check(size >= (ulong)elemSize * count, "size >= elem_size * count")) return false;

                    frame.Channel = cc.ChannelName;
                    frame.Time = (double)time * (1.0 / 6000.0);
                    frame.Filename = cc.StreamFilename;
                    frame.DataFormat = format;
                    frame.DataEncoding = UfbxCacheDataEncoding.BigEndian;
                    frame.DataOffset = cc.FileOffset;
                    frame.DataCount = count;
                    frame.DataElementBytes = elemSize;
                    frame.DataTotalBytes = totalSize;
                    frame.FileFormat = UfbxCacheFileFormat.Mc;

                    ulong end = begin + ((size + alignment - 1) & ~(alignment - 1));
                    if (!cc.Check(end >= cc.FileOffset, "end >= cc->file_offset")) return false;
                    ulong left = end - cc.FileOffset;
                    if (!cc.Check(CacheSkip(cc, left), "ufbxi_cache_skip")) return false;
                }
            }

            return true;
        }

        // C: ufbxi_cache_load_pc2 (ufbx.c:24249-24296).
        static bool CacheLoadPc2(UfbxiCacheContext cc)
        {
            byte[] header = new byte[32];
            if (!cc.Check(CacheRead(cc, header, 0, header.Length, false), "ufbxi_cache_read")) return false;

            uint version = UfbxiCacheRead.U32(header, 12);
            uint numPoints = UfbxiCacheRead.U32(header, 16);
            double startFrame = UfbxiCacheRead.F32(header, 20);
            double framesPerSample = UfbxiCacheRead.F32(header, 24);
            uint numSamples = UfbxiCacheRead.U32(header, 28);

            // C: (void)version;

            UfbxCacheFrame[] frames = new UfbxCacheFrame[numSamples];
            for (int i = 0; i < numSamples; i++) frames[i] = new UfbxCacheFrame();
            cc.TmpStack.AddRange(frames);

            ulong totalPoints = (ulong)numPoints * numSamples;
            if (!cc.Check(totalPoints < ulong.MaxValue / 12, "total_points < UINT64_MAX / 12")) return false;

            ulong offset = cc.FileOffset;

            // Skip almost to the end of the data and try to read one byte as there's
            // nothing after the data so we can't detect EOF..
            if (totalPoints > 0) {
                byte[] lastByte = new byte[1];
                if (!cc.Check(CacheSkip(cc, totalPoints * 12 - 1), "ufbxi_cache_skip")) return false;
                if (!cc.Check(CacheRead(cc, lastByte, 0, 1, false), "ufbxi_cache_read")) return false;
            }

            for (uint i = 0; i < numSamples; i++) {
                UfbxCacheFrame frame = frames[i];

                double sampleFrame = startFrame + (double)i * framesPerSample;
                frame.Channel = cc.ChannelName;
                frame.Time = sampleFrame / cc.FramesPerSecond;
                frame.Filename = cc.StreamFilename;
                frame.DataFormat = UfbxCacheDataFormat.Vec3Float;
                frame.DataEncoding = UfbxCacheDataEncoding.LittleEndian;
                frame.DataOffset = offset;
                frame.DataCount = numPoints;
                frame.DataElementBytes = 12;
                frame.DataTotalBytes = (ulong)numPoints * 12;
                frame.FileFormat = UfbxCacheFileFormat.Pc2;
                offset += (ulong)numPoints * 12;
            }

            return true;
        }

        // C: ufbxi_tmp_channel_less (ufbx.c:24298-24303).
        static bool TmpChannelLess(object user, UfbxiCacheTmpChannel a, UfbxiCacheTmpChannel b)
        {
            return UfbxiStr.Less(a.Name, b.Name);
        }

        // C: ufbxi_cache_sort_tmp_channels (ufbx.c:24305-24310).
        static bool CacheSortTmpChannels(UfbxiCacheContext cc, UfbxiCacheTmpChannel[] channels, int count)
        {
            UfbxiCacheTmpChannel[] tmp = new UfbxiCacheTmpChannel[count];
            UfbxiSort.StableSort(16, channels, tmp, count, TmpChannelLess, null);
            return true;
        }

        // C: ufbxi_cache_load_xml_imp (ufbx.c:24312-24401).
        static bool CacheLoadXmlImp(UfbxiCacheContext cc, UfbxiXmlDocument doc)
        {
            if (!cc.Check(!cc.XmlLoaded, "!cc->xml_loaded")) return false;

            cc.XmlLoaded = true;
            cc.XmlTicksPerFrame = 250;
            cc.XmlFilename = cc.StreamFilename;

            UfbxiXmlTag tagRoot = UfbxiXml.FindChild(doc.Root, "Autodesk_Cache_File");
            if (tagRoot != null) {
                UfbxiXmlTag tagType = UfbxiXml.FindChild(tagRoot, "cacheType");
                UfbxiXmlTag tagFps = UfbxiXml.FindChild(tagRoot, "cacheTimePerFrame");
                UfbxiXmlTag tagChannels = UfbxiXml.FindChild(tagRoot, "Channels");

                int numExtra = 0;
                int extraBegin = cc.TmpStackStrings.Count;
                foreach (UfbxiXmlTag tag in tagRoot.Children) {
                    if (tag.Children.Length != 1) continue;
                    if (tag.Name != "extra") continue;
                    cc.TmpStackStrings.Add(tag.Children[0].Text);
                    numExtra++;
                }
                cc.Cache.ExtraInfo = PopStrings(cc, extraBegin, numExtra);

                if (tagType != null) {
                    UfbxiXmlAttrib type = UfbxiXml.FindAttrib(tagType, "Type");
                    UfbxiXmlAttrib format = UfbxiXml.FindAttrib(tagType, "Format");
                    if (type != null) {
                        if (type.Value == "OneFilePerFrame") {
                            cc.XmlType = UfbxiCacheXmlType.FilePerFrame;
                        } else if (type.Value == "OneFile") {
                            cc.XmlType = UfbxiCacheXmlType.SingleFile;
                        }
                    }
                    if (format != null) {
                        if (format.Value == "mcc") {
                            cc.XmlFormat = UfbxiCacheXmlFormat.Mcc;
                        } else if (format.Value == "mcx") {
                            cc.XmlFormat = UfbxiCacheXmlFormat.Mcx;
                        }
                    }
                }

                if (tagFps != null) {
                    UfbxiXmlAttrib fps = UfbxiXml.FindAttrib(tagFps, "TimePerFrame");
                    if (fps != null) {
                        uint value = UfbxiParseRadix.Uint32(UfbxiRawStr.ToBytes(fps.Value), 0, fps.Value.Length, 10);
                        if (value > 0) {
                            cc.XmlTicksPerFrame = value;
                        }
                    }
                }

                if (tagChannels != null) {
                    cc.Channels = new UfbxiCacheTmpChannel[tagChannels.Children.Length];
                    for (int i = 0; i < cc.Channels.Length; i++) cc.Channels[i] = new UfbxiCacheTmpChannel();
                    cc.NumChannels = 0;

                    foreach (UfbxiXmlTag tag in tagChannels.Children) {
                        UfbxiXmlAttrib name = UfbxiXml.FindAttrib(tag, "ChannelName");
                        UfbxiXmlAttrib type = UfbxiXml.FindAttrib(tag, "ChannelType");
                        UfbxiXmlAttrib interpretation = UfbxiXml.FindAttrib(tag, "ChannelInterpretation");
                        if (!(name != null && type != null && interpretation != null)) continue;

                        UfbxiCacheTmpChannel channel = cc.Channels[cc.NumChannels++];
                        channel.Name = name.Value;
                        channel.Interpretation = interpretation.Value;

                        UfbxiXmlAttrib samplingRate = UfbxiXml.FindAttrib(tag, "SamplingRate");
                        UfbxiXmlAttrib startTime = UfbxiXml.FindAttrib(tag, "StartTime");
                        UfbxiXmlAttrib endTime = UfbxiXml.FindAttrib(tag, "EndTime");
                        if (samplingRate != null && startTime != null && endTime != null) {
                            channel.SampleRate = UfbxiParseRadix.Uint32(UfbxiRawStr.ToBytes(samplingRate.Value), 0, samplingRate.Value.Length, 10);
                            channel.StartTime = UfbxiParseRadix.Uint32(UfbxiRawStr.ToBytes(startTime.Value), 0, startTime.Value.Length, 10);
                            channel.EndTime = UfbxiParseRadix.Uint32(UfbxiRawStr.ToBytes(endTime.Value), 0, endTime.Value.Length, 10);
                            channel.CurrentTime = channel.StartTime;
                            channel.TryLoad = true;
                        }
                    }
                }
            }

            if (!cc.Check(CacheSortTmpChannels(cc, cc.Channels, cc.NumChannels), "ufbxi_cache_sort_tmp_channels")) return false;
            return true;
        }

        // C: `ufbxi_push_pop(&cc->result, &cc->tmp_stack, ufbx_string, n)` — take the last `n`.
        static string[] PopStrings(UfbxiCacheContext cc, int begin, int count)
        {
            string[] items = new string[count];
            for (int i = 0; i < count; i++) items[i] = cc.TmpStackStrings[begin + i];
            cc.TmpStackStrings.RemoveRange(begin, count);
            return items;
        }

        // C: ufbxi_cache_load_xml (ufbx.c:24403-24419).
        static bool CacheLoadXml(UfbxiCacheContext cc)
        {
            UfbxiXmlOpts opts = new UfbxiXmlOpts();
            opts.ReadStream = cc.Stream;
            opts.Prefix = cc.Window;
            opts.PrefixOffset = cc.BufferPos;
            opts.PrefixLength = cc.WindowLength - cc.BufferPos;
            UfbxiXmlDocument doc = UfbxiXml.LoadXml(opts, cc.Error);
            if (!cc.Check(doc != null, "doc")) return false;

            bool xmlOk = CacheLoadXmlImp(cc, doc);
            UfbxiXml.FreeXml(doc);
            if (!cc.Check(xmlOk, "xml_ok")) return false;

            return true;
        }

        // C: ufbxi_cache_load_file (ufbx.c:24421-24444).
        static bool CacheLoadFile(UfbxiCacheContext cc, string filename)
        {
            cc.StreamFilename = filename;

            // Assume all files have at least 16 bytes of header
            int magicLen = cc.Stream.Read(cc.Buffer, 0, 16);
            if (magicLen < 0) magicLen = 0;
            if (!cc.CheckMsg(magicLen <= 16, "IO error")) return false;
            if (!cc.CheckMsg(magicLen == 16, "Truncated file")) return false;
            cc.BufferPos = 0;
            cc.BufferLen = 16;

            cc.FileOffset = 0;

            if (Match(cc.Buffer, 0, 16, "POINTCACHE2", 11)) {
                if (!cc.Check(CacheLoadPc2(cc), "ufbxi_cache_load_pc2")) return false;
            } else if (Match(cc.Buffer, 0, 16, "FOR4", 4) || Match(cc.Buffer, 0, 16, "FOR8", 4)) {
                if (!cc.Check(CacheLoadMc(cc), "ufbxi_cache_load_mc")) return false;
            } else {
                if (!cc.Check(CacheLoadXml(cc), "ufbxi_cache_load_xml")) return false;
            }

            return true;
        }

        // C: memcmp(buffer, magic, len) == 0.
        static bool Match(byte[] buffer, int offset, int bufferLength, string magic, int magicLen)
        {
            for (int i = 0; i < magicLen; i++) {
                if (buffer[offset + i] != (byte)magic[i]) return false;
            }
            return true;
        }

        // C: ufbxi_cache_try_open_file (ufbx.c:24446-24462).
        static bool CacheTryOpenFile(UfbxiCacheContext cc, string filename, byte[] originalFilename, out bool pFound)
        {
            pFound = false;
            cc.Stream = new UfbxiLoad.UfbxiOpenFileStream();
            UfbxiLoad.UfbxiOpenFileStream stream = (UfbxiLoad.UfbxiOpenFileStream)cc.Stream;
            if (!UfbxiSceneFiles.OpenFile(cc.OpenFileCb, stream, filename, filename.Length, originalFilename, 0, UfbxOpenFileType.GeometryCache)) {
                return true;
            }

            bool ok = CacheLoadFile(cc, filename);
            pFound = true;

            stream.Close();

            return ok;
        }

        // C: ufbxi_cache_load_frame_files (ufbx.c:24464-24547).
        static bool CacheLoadFrameFiles(UfbxiCacheContext cc)
        {
            if (cc.XmlFilename == null || cc.XmlFilename.Length == 0) return true;

            string extension;
            switch (cc.XmlFormat) {
                case UfbxiCacheXmlFormat.Mcc: extension = "mc"; break;
                case UfbxiCacheXmlFormat.Mcx: extension = "mcx"; break;
                default: return true;
            }

            // Find the prefix before `.xml`
            int prefixLen = cc.XmlFilename.Length;
            byte[] xmlBytes = UfbxiRawStr.ToBytes(cc.XmlFilename);
            for (int i = prefixLen; i > 0; --i) {
                if (xmlBytes[i - 1] == (byte)'.') {
                    prefixLen = i - 1;
                    break;
                }
            }

            // C: filename.data = name_buf (prefix_len bytes + suffix built with snprintf).
            string prefix = UfbxiRawStr.FromBytes(xmlBytes, 0, prefixLen);

            if (cc.XmlType == UfbxiCacheXmlType.SingleFile) {
                string filename = prefix + "." + extension;
                bool found;
                if (!cc.Check(CacheTryOpenFile(cc, filename, null, out found), "ufbxi_cache_try_open_file")) return false;
            } else if (cc.XmlType == UfbxiCacheXmlType.FilePerFrame) {
                uint lowestTime = 0;
                for (;;) {
                    // Find the first `time >= lowest_time` value that has data in some channel
                    uint time = uint.MaxValue;
                    foreach (UfbxiCacheTmpChannel chan in cc.Channels) {
                        if (chan == null) continue;
                        if (!chan.TryLoad || chan.ConsecutiveFails > 10) continue;
                        uint sampleRate = chan.SampleRate != 0 ? chan.SampleRate : cc.XmlTicksPerFrame;
                        if (chan.CurrentTime < lowestTime) {
                            uint delta = (lowestTime - chan.CurrentTime - 1) / sampleRate;
                            chan.CurrentTime += delta * sampleRate;
                            if (uint.MaxValue - chan.CurrentTime >= sampleRate) {
                                chan.CurrentTime += sampleRate;
                            } else {
                                chan.TryLoad = false;
                                continue;
                            }
                        }
                        if (chan.CurrentTime <= chan.EndTime) {
                            time = Math.Min(time, chan.CurrentTime);
                        }
                    }
                    if (time == uint.MaxValue) break;

                    // Try to load a file at the specified frame/tick
                    uint frame = time / cc.XmlTicksPerFrame;
                    uint tick = time % cc.XmlTicksPerFrame;
                    string filename;
                    if (tick == 0) {
                        filename = prefix + "Frame" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + extension;
                    } else {
                        filename = prefix + "Frame" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "Tick" + tick.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + extension;
                    }
                    bool found;
                    if (!cc.Check(CacheTryOpenFile(cc, filename, null, out found), "ufbxi_cache_try_open_file")) return false;

                    // Update channel status
                    foreach (UfbxiCacheTmpChannel chan in cc.Channels) {
                        if (chan == null) continue;
                        if (chan.CurrentTime == time) {
                            chan.ConsecutiveFails = found ? 0 : chan.ConsecutiveFails + 1;
                        }
                    }

                    lowestTime = time + 1;
                }
            }

            return true;
        }

        // C: ufbxi_cmp_cache_frame_less (ufbx.c:24549-24559).
        static bool CmpCacheFrameLess(object user, UfbxCacheFrame a, UfbxCacheFrame b)
        {
            if (!ReferenceEquals(a.Channel, b.Channel)) {
                // Channel names should be interned
                return UfbxiStr.Less(a.Channel, b.Channel);
            }
            return a.Time < b.Time;
        }

        // C: ufbxi_cache_sort_frames (ufbx.c:24561-24566).
        static bool CacheSortFrames(UfbxiCacheContext cc, UfbxCacheFrame[] frames, int count)
        {
            UfbxCacheFrame[] tmp = new UfbxCacheFrame[count];
            UfbxiSort.StableSort(16, frames, tmp, count, CmpCacheFrameLess, null);
            return true;
        }

        // C: ufbxi_cache_interpretation_names[] (ufbx.c:24573-24577).
        static readonly UfbxiCacheInterpretationName[] InterpretationNames = new UfbxiCacheInterpretationName[] {
            new UfbxiCacheInterpretationName { Interpretation = UfbxCacheInterpretation.Points, Pattern = "\\cpoints?" },
            new UfbxiCacheInterpretationName { Interpretation = UfbxCacheInterpretation.VertexPosition, Pattern = "\\cpositions?" },
            new UfbxiCacheInterpretationName { Interpretation = UfbxCacheInterpretation.VertexNormal, Pattern = "\\cnormals?" },
        };

        // C: ufbxi_cache_setup_channels (ufbx.c:24579-24641).
        static bool CacheSetupChannels(UfbxiCacheContext cc)
        {
            UfbxiCacheTmpChannel[] channels = cc.Channels;
            int tmpEnd = cc.NumChannels;

            int begin = 0, numChannels = 0;
            UfbxCacheFrame[] frames = cc.Cache.Frames;
            UfbxCacheChannel[] outChannels = new UfbxCacheChannel[frames.Length];
            int tmpChan = 0;
            while (begin < frames.Length) {
                UfbxCacheFrame frame = frames[begin];
                int end = begin + 1;
                while (end < frames.Length && ReferenceEquals(frames[end].Channel, frame.Channel)) {
                    end++;
                }

                UfbxCacheChannel chan = new UfbxCacheChannel();
                outChannels[numChannels] = chan;

                chan.Name = frame.Channel;
                chan.InterpretationName = "";
                // C: chan->frames.data = frame; chan->frames.count = end - begin — a slice into the
                // same frame array (the port materializes a copy of the same elements).
                UfbxCacheFrame[] chanFrames = new UfbxCacheFrame[end - begin];
                for (int i = 0; i < chanFrames.Length; i++) chanFrames[i] = frames[begin + i];
                chan.Frames = chanFrames;

                while (tmpChan < tmpEnd && UfbxiStr.Less(channels[tmpChan].Name, chan.Name)) {
                    tmpChan++;
                }
                if (tmpChan < tmpEnd && UfbxiStr.Equal(channels[tmpChan].Name, chan.Name)) {
                    chan.InterpretationName = channels[tmpChan].Interpretation;
                }

                if (frame.FileFormat == UfbxCacheFileFormat.Pc2) {
                    chan.Interpretation = UfbxCacheInterpretation.VertexPosition;
                } else {
                    foreach (UfbxiCacheInterpretationName name in InterpretationNames) {
                        if (UfbxiLoad.Match(chan.InterpretationName, 0, chan.InterpretationName.Length, name.Pattern)) {
                            chan.Interpretation = name.Interpretation;
                            break;
                        }
                    }
                }

                UfbxMirrorAxis mirrorAxis = UfbxMirrorAxis.None;
                double scaleFactor = 1.0;
                if (chan.Interpretation != UfbxCacheInterpretation.Unknown) {
                    mirrorAxis = cc.Opts.MirrorAxis;
                    if (cc.Opts.UseScaleFactor) {
                        scaleFactor = cc.Opts.ScaleFactor;
                    }
                }
                chan.MirrorAxis = mirrorAxis;
                chan.ScaleFactor = scaleFactor;
                foreach (UfbxCacheFrame f in chan.Frames) {
                    f.MirrorAxis = mirrorAxis;
                    f.ScaleFactor = scaleFactor;
                }

                numChannels++;
                begin = end;
            }

            UfbxCacheChannel[] result = new UfbxCacheChannel[numChannels];
            for (int i = 0; i < numChannels; i++) result[i] = outChannels[i];
            cc.Cache.Channels = result;

            return true;
        }

        // C: ufbxi_cache_load_imp (ufbx.c:24644-24698).
        static bool CacheLoadImp(UfbxiCacheContext cc, string filename)
        {
            cc.ChannelName = "";

            if (cc.OpenFileCb.Fn == null) {
                cc.OpenFileCb.Fn = UfbxiLoad.DefaultOpenFile;
            }

            bool found;
            if (!cc.Check(CacheTryOpenFile(cc, filename, null, out found), "ufbxi_cache_try_open_file")) return false;
            if (!found) {
                return cc.FailInfoMsg(filename, filename.Length, "File not found");
            }

            cc.Cache.RootFilename = cc.StreamFilename;

            if (!cc.Check(CacheLoadFrameFiles(cc), "ufbxi_cache_load_frame_files")) return false;

            int numFrames = cc.TmpStack.Count;
            UfbxCacheFrame[] frameArr = new UfbxCacheFrame[numFrames];
            for (int i = 0; i < numFrames; i++) frameArr[i] = cc.TmpStack[i];
            cc.TmpStack.Clear();
            cc.Cache.Frames = frameArr;

            if (!cc.Check(CacheSortFrames(cc, cc.Cache.Frames, cc.Cache.Frames.Length), "ufbxi_cache_sort_frames")) return false;
            if (!cc.Check(CacheSetupChannels(cc), "ufbxi_cache_setup_channels")) return false;

            // Must be last allocation!
            cc.Imp = new UfbxiGeometryCacheImp();
            cc.Imp.Cache = cc.Cache;
            cc.Imp.Magic = UfbxiCacheConsts.CacheImpMagic;
            cc.Imp.OwnedByScene = cc.OwnedByScene;

            return true;
        }

        // C: ufbxi_cache_load (ufbx.c:24700-24724).
        static UfbxGeometryCache CacheLoad(UfbxiCacheContext cc, string filename)
        {
            bool ok = CacheLoadImp(cc, filename);

            if (ok) {
                return cc.Imp.Cache;
            } else {
                UfbxiPrint.FixErrorType(cc.Error, "Failed to load geometry cache", null);
                return null;
            }
        }

        // C: ufbxi_load_geometry_cache (ufbx.c:24726-24763).
        internal static UfbxGeometryCache LoadGeometryCache(string filename, UfbxGeometryCacheOpts userOpts, UfbxError pError)
        {
            UfbxGeometryCacheOpts opts;
            if (userOpts != null) {
                opts = userOpts;
            } else {
                opts = new UfbxGeometryCacheOpts();
            }

            UfbxiCacheContext cc = new UfbxiCacheContext();
            cc.Error = new UfbxError();
            cc.Opts = opts;
            cc.OpenFileCb = opts.OpenFileCb;

            cc.StringPool = new UfbxiStringPool(cc.Error, null, new UfbxiPushBuf(), 64, UfbxUnicodeErrorHandling.ReplacementCharacter);

            cc.FramesPerSecond = opts.FramesPerSecond > 0.0 ? opts.FramesPerSecond : 30.0;

            UfbxGeometryCache cache = CacheLoad(cc, filename);
            if (pError != null) {
                if (cache != null) {
                    UfbxiPrint.ClearError(pError);
                } else {
                    pError.Type = cc.Error.Type;
                    pError.Description = cc.Error.Description;
                    pError.Info = cc.Error.Info;
                    pError.InfoLength = cc.Error.InfoLength;
                }
            }
            return cache;
        }

        // C: ufbxi_free_geometry_cache_imp (ufbx.c:24765-24769) — arena release only.
        internal static void FreeGeometryCacheImp(UfbxiGeometryCacheImp imp)
        {
            // C: ufbx_assert(imp->magic == UFBXI_CACHE_IMP_MAGIC);
        }

        // ------------------------------------------------------------------
        // External files (ufbx.c:24795-24952)
        // ------------------------------------------------------------------

        // C: ufbxi_less_external_file (ufbx.c:24810-24819).
        static bool LessExternalFile(object user, UfbxiExternalFile a, UfbxiExternalFile b)
        {
            if (a.Type != b.Type) return a.Type < b.Type;
            int cmp = UfbxiStr.Cmp(a.Filename, b.Filename);
            if (cmp != 0) return cmp < 0;
            if (a.Index != b.Index) return a.Index < b.Index;
            return false;
        }

        // C: ufbxi_load_external_cache (ufbx.c:24821-24875).
        internal static bool LoadExternalCache(UfbxiContext uc, UfbxiExternalFile file)
        {
            UfbxiCacheContext cc = new UfbxiCacheContext();
            cc.Error = new UfbxError();
            cc.OwnedByScene = true;

            cc.OpenFileCb = uc.Opts.OpenFileCb;
            cc.FramesPerSecond = uc.Scene.Settings.FramesPerSecond;

            // Temporarily "borrow" the string pool for the geometry cache.
            cc.StringPool = uc.StringPool;
            cc.Cache = new UfbxGeometryCache();

            cc.Opts.MirrorAxis = uc.MirrorAxis;
            cc.Opts.UseScaleFactor = true;
            cc.Opts.ScaleFactor = uc.Scene.Metadata.GeometryScale;

            UfbxGeometryCache cache = CacheLoad(cc, file.Filename);
            if (cache == null) {
                if (cc.Error.Type == UfbxErrorType.FileNotFound) {
                    // C: memset(&cc.error, 0, sizeof(cc.error)) then retry with absolute filename.
                    cc.Error = new UfbxError();
                    cache = CacheLoad(cc, file.AbsoluteFilename);
                }
            }

            if (cache == null) {
                if (cc.Error.Type == UfbxErrorType.FileNotFound) {
                    if (uc.Opts.IgnoreMissingExternalFiles) {
                        UfbxiFail.CheckNoDesc(UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.MissingExternalFile,
                            UfbxiWarnings.NoElementId, "Failed to open geometry cache: %s",
                            new UfbxiVaList().AddStr(file.Filename)), "ufbxi_warnf(UFBX_WARNING_MISSING_EXTERNAL_FILE, ...)");
                        return true;
                    } else {
                        cc.Error.Type = UfbxErrorType.ExternalFileNotFound;
                        cc.Error.Description = "External file not found";
                    }
                }

                uc.Error = cc.Error;
                return false;
            }

            file.Data = cache;
            return true;
        }

        // C: ufbxi_find_external_file (ufbx.c:24877-24884) — lower_bound_eq over the sorted file
        // list, matching `type` and the interned filename string. C's `a->filename.data == name`
        // pointer test is *content* equality here because the port does not intern strings across
        // the FBX and cache files.
        static UfbxiExternalFile FindExternalFile(UfbxiExternalFile[] files, int numFiles, UfbxiExternalFileType type, string name)
        {
            // C: ufbxi_macro_lower_bound_eq(ufbxi_external_file, 32, ...) (ufbx.c:1188-1204):
            // binary search with `hi = mid + 1`, then a linear `eq`-only scan with no early exit
            // on `less`.
            const int linearSize = 32; // C: ufbxi_clamp_linear_threshold(32)
            int lo = 0, hi = numFiles;
            while (hi - lo > linearSize) {
                int mid = lo + (hi - lo) / 2;
                UfbxiExternalFile a = files[mid];
                if (type != a.Type ? type < a.Type : UfbxiStr.Less(a.Filename, name)) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                UfbxiExternalFile a = files[lo];
                if (a.Type == type && UfbxiStr.Equal(a.Filename, name)) return a;
            }
            return null;
        }

        // C: ufbxi_load_external_files (ufbx.c:24886-24952).
        internal static bool LoadExternalFiles(UfbxiContext uc)
        {
            int numFiles = 0;

            // Gather external files to deduplicate them
            List<UfbxiExternalFile> tmp = new List<UfbxiExternalFile>();
            foreach (UfbxCacheFile cache in uc.Scene.CacheFiles) {
                if (cache.Filename != null && cache.Filename.Length > 0) {
                    UfbxiExternalFile file = new UfbxiExternalFile();
                    file.Index = numFiles++;
                    file.Type = UfbxiExternalFileType.GeometryCache;
                    file.Filename = cache.Filename;
                    file.AbsoluteFilename = cache.AbsoluteFilename;
                    tmp.Add(file);
                }
            }

            // Sort and load the external files
            UfbxiExternalFile[] files = tmp.ToArray();
            UfbxiSort.UnstableSort(files, files.Length, LessExternalFile, null);

            UfbxiExternalFileType prevType = UfbxiExternalFileType.GeometryCache;
            string prevName = null;
            foreach (UfbxiExternalFile file in files) {
                // C: `file->filename.data == prev_name` compares interned string pointers;
                // content equality is the equivalent test in the port.
                if (file.Type == prevType && UfbxiStr.Equal(file.Filename, prevName)) continue;
                if (file.Type == UfbxiExternalFileType.GeometryCache) {
                    UfbxiFail.CheckNoDesc(LoadExternalCache(uc, file), "ufbxi_load_external_cache");
                }
                prevName = file.Filename;
                prevType = file.Type;
            }

            // Patch the loaded files
            foreach (UfbxCacheFile cache in uc.Scene.CacheFiles) {
                UfbxiExternalFile file = FindExternalFile(files, files.Length,
                    UfbxiExternalFileType.GeometryCache, cache.Filename);
                if (file != null && file.Data != null) {
                    cache.ExternalCache = (UfbxGeometryCache)file.Data;
                }
            }

            // Patch the geometry deformers
            foreach (UfbxCacheDeformer deformer in uc.Scene.CacheDeformers) {
                if (deformer.File == null || deformer.File.ExternalCache == null) continue;
                UfbxGeometryCache cache = deformer.File.ExternalCache;
                deformer.ExternalCache = cache;

                // HACK: It seems like channels may be connected even if the name is wrong
                // and they work when exporting from Marvelous to Maya...
                if (cache.Channels.Length == 1) {
                    deformer.ExternalChannel = cache.Channels[0];
                } else {
                    string channel = deformer.Channel;
                    // C: ufbxi_macro_lower_bound_eq(ufbx_cache_channel, 16, &ix, cache->channels.data,
                    // 0, cache->channels.count, less, eq) (ufbx.c:1188-1204): binary-search down to
                    // the (unclamped) threshold of 16 elements with `hi = mid + 1`, then scan the
                    // whole remaining range for the equality test -- there is no early exit on
                    // `less`, so a name that sorts before `channel` does not end the scan.
                    const int linearSize = 16; // C: ufbxi_clamp_linear_threshold(16)
                    int lo = 0, hi = cache.Channels.Length;
                    while (hi - lo > linearSize) {
                        int mid = lo + (hi - lo) / 2;
                        if (UfbxiStr.Less(cache.Channels[mid].Name, channel)) {
                            lo = mid + 1;
                        } else {
                            hi = mid + 1;
                        }
                    }
                    int ix = -1;
                    for (; lo < hi; lo++) {
                        // C: `a->name.data == channel.data` compares the pointers of two strings
                        // interned in `uc->string_pool`, so it means content equality here (the
                        // port's `string` instances are not interned across the FBX and cache
                        // files); same predicate as `UfbxiStr.Equal` in `CacheSetupChannels`.
                        if (UfbxiStr.Equal(cache.Channels[lo].Name, channel)) {
                            ix = lo;
                            break;
                        }
                    }
                    if (ix >= 0) deformer.ExternalChannel = cache.Channels[ix];
                }
            }

            return true;
        }
    }

    // ------------------------------------------------------------------
    // Geometry cache reading / sampling (ufbx.c:32704-32963)
    // ------------------------------------------------------------------

    // C: ufbxi_geometry_cache_buffer (ufbx.c:32696-32702) — the endian-swap scratch shared by
    // `ufbx_read_geometry_cache_real`. The C union overlaps `src.f64[N]` / `src.f32[2N]` / the
    // destination `dst[N]`; the port keeps one `double[]` scratch and reads into it directly.
    internal static class UfbxiGeometryCacheSample
    {
        // C: #define UFBXI_GEOMETRY_CACHE_BUFFER_SIZE 512 (ufbx.c:62) — the per-iteration element
        // count of the read loop below, so `buffer` is 512 doubles (4096 bytes) and a frame is read
        // in chunks of at most 512 reals. The chunking is not observable in the values it produces
        // (`mirror_ix` is re-based by `-= num_read` at ufbx.c:32838, which preserves the global
        // mod-3 phase of the negated elements across chunks), but it does fix the size of every
        // `read_fn` call and the scratch footprint, so it is kept identical to C. Note the
        // unrelated 4096 at ufbx.c:32770: that is the byte-wise *skip* scratch of the seek loop,
        // not this buffer.
        const int GeometryCacheBufferSize = 512;

        // C: `ufbx_geometry_cache_data_opts opts; if (user_opts) { opts = *user_opts; } else
        // { memset(&opts, 0, sizeof(opts)); }` (ufbx.c:32711-32718, same in
        // ufbx_sample_geometry_cache_real at 32878-32885). The value copy is observable: both
        // writers below assign into `opts` (`open_file_cb.fn` in read, `use_weight`/`weight`/
        // `additive` in the interpolated sample), and in C those writes land on the copy, never on
        // the caller's struct. The port's opts are reference types, so they have to be copied here
        // or a single `ufbx_sample_geometry_cache_*()` would permanently reweight the caller's opts.
        static UfbxGeometryCacheDataOpts CopyOpts(UfbxGeometryCacheDataOpts userOpts)
        {
            UfbxGeometryCacheDataOpts opts = new UfbxGeometryCacheDataOpts();
            if (userOpts == null) return opts;
            opts.Additive = userOpts.Additive;
            opts.UseWeight = userOpts.UseWeight;
            opts.Weight = userOpts.Weight;
            opts.IgnoreTransform = userOpts.IgnoreTransform;
            UfbxOpenFileCb cb = new UfbxOpenFileCb();
            if (userOpts.OpenFileCb != null) {
                cb.Fn = userOpts.OpenFileCb.Fn;
                cb.User = userOpts.OpenFileCb.User;
            }
            opts.OpenFileCb = cb;
            return opts;
        }

        // C: ufbx_read_geometry_cache_real (ufbx.c:32704-32867).
        internal static int ReadGeometryCacheReal(UfbxCacheFrame frame, double[] data, int dataOffset, int count, UfbxGeometryCacheDataOpts userOpts)
        {
            if (frame == null || count == 0) return 0;
            if (data == null) return 0;

            UfbxGeometryCacheDataOpts opts = CopyOpts(userOpts);

            if (opts.OpenFileCb.Fn == null) {
                opts.OpenFileCb.Fn = UfbxiLoad.DefaultOpenFile;
            }

            bool useDouble = false;

            int srcCount = 0;

            switch (frame.DataFormat) {
                case UfbxCacheDataFormat.Unknown: srcCount = 0; break;
                case UfbxCacheDataFormat.RealFloat: srcCount = (int)frame.DataCount; break;
                case UfbxCacheDataFormat.Vec3Float: srcCount = (int)frame.DataCount * 3; break;
                case UfbxCacheDataFormat.RealDouble: srcCount = (int)frame.DataCount; useDouble = true; break;
                case UfbxCacheDataFormat.Vec3Double: srcCount = (int)frame.DataCount * 3; useDouble = true; break;
                default: srcCount = 0; break;
            }

            bool srcBigEndian = false;
            switch (frame.DataEncoding) {
                case UfbxCacheDataEncoding.Unknown: return 0;
                case UfbxCacheDataEncoding.LittleEndian: srcBigEndian = false; break;
                case UfbxCacheDataEncoding.BigEndian: srcBigEndian = true; break;
                default: srcBigEndian = false; break;
            }

            // Test endianness (C: memcpy a 0xbbaa uint16 and inspect the first byte). The
            // reference build is x86_64 => little-endian => dst_big_endian == false.
            const bool dstBigEndian = false;

            if (srcCount == 0) return 0;
            srcCount = Math.Min(srcCount, count);

            UfbxiLoad.UfbxiOpenFileStream stream = new UfbxiLoad.UfbxiOpenFileStream();
            if (!UfbxiSceneFiles.OpenFile(opts.OpenFileCb, stream, frame.Filename, frame.Filename.Length, null, 0, UfbxOpenFileType.GeometryCache)) {
                return 0;
            }

            // Skip to the correct point in the file
            ulong offset = frame.DataOffset;
            if (stream.CanSkip) {
                while (offset > 0) {
                    int toSkip = (int)Math.Min(offset, UfbxiCacheConsts.MaxSkipSize);
                    if (!stream.Skip(toSkip)) break;
                    offset -= (ulong)toSkip;
                }
            } else {
                byte[] buffer = new byte[4096];
                while (offset > 0) {
                    int toSkip = (int)Math.Min(offset, (ulong)buffer.Length);
                    int numRead = stream.Read(buffer, 0, toSkip);
                    if (numRead != toSkip) break;
                    offset -= (ulong)toSkip;
                }
            }

            // Failed to skip all the way
            if (offset > 0) {
                stream.Close();
                return 0;
            }

            int dst = dataOffset;
            int mirrorIx = (int)frame.MirrorAxis - 1;
            byte[] readBuf = new byte[GeometryCacheBufferSize * 8];
            double[] bufferDst = new double[GeometryCacheBufferSize];
            while (srcCount > 0) {
                int toRead = Math.Min(srcCount, GeometryCacheBufferSize);
                srcCount -= toRead;
                int numRead = 0;
                if (useDouble) {
                    int bytesRead = stream.Read(readBuf, 0, toRead * 8);
                    if (bytesRead < 0) bytesRead = 0;
                    numRead = bytesRead / 8;
                    if (srcBigEndian != dstBigEndian) {
                        for (int i = 0; i < numRead; i++) {
                            byte t;
                            int v = i * 8;
                            t = readBuf[v + 0]; readBuf[v + 0] = readBuf[v + 7]; readBuf[v + 7] = t;
                            t = readBuf[v + 1]; readBuf[v + 1] = readBuf[v + 6]; readBuf[v + 6] = t;
                            t = readBuf[v + 2]; readBuf[v + 2] = readBuf[v + 5]; readBuf[v + 5] = t;
                            t = readBuf[v + 3]; readBuf[v + 3] = readBuf[v + 4]; readBuf[v + 4] = t;
                        }
                    }
                    for (int i = 0; i < numRead; i++) {
                        bufferDst[i] = (double)UfbxiCacheRead.F64(readBuf, i * 8);
                    }
                } else {
                    int bytesRead = stream.Read(readBuf, 0, toRead * 4);
                    if (bytesRead < 0) bytesRead = 0;
                    numRead = bytesRead / 4;
                    if (srcBigEndian != dstBigEndian) {
                        for (int i = 0; i < numRead; i++) {
                            byte t;
                            int v = i * 4;
                            t = readBuf[v + 0]; readBuf[v + 0] = readBuf[v + 3]; readBuf[v + 3] = t;
                            t = readBuf[v + 1]; readBuf[v + 1] = readBuf[v + 2]; readBuf[v + 2] = t;
                        }
                    }
                    for (int i = 0; i < numRead; i++) {
                        bufferDst[i] = (double)BitConverter.Int32BitsToSingle(unchecked((int)UfbxiCacheRead.U32(readBuf, i * 4)));
                    }
                }

                if (!opts.IgnoreTransform) {
                    double scale = frame.ScaleFactor;
                    if (scale != 1.0) {
                        for (int i = 0; i < numRead; i++) {
                            bufferDst[i] *= scale;
                        }
                    }
                    if (frame.MirrorAxis != UfbxMirrorAxis.None) {
                        while (mirrorIx < numRead) {
                            bufferDst[mirrorIx] = -bufferDst[mirrorIx];
                            mirrorIx += 3;
                        }
                        mirrorIx -= numRead;
                    }
                }

                if (data != null) {
                    double weight = opts.UseWeight ? opts.Weight : 1.0;
                    if (opts.Additive) {
                        for (int i = 0; i < numRead; i++) {
                            data[dst + i] += bufferDst[i] * weight;
                        }
                    } else {
                        for (int i = 0; i < numRead; i++) {
                            data[dst + i] = bufferDst[i] * weight;
                        }
                    }
                    dst += numRead;
                }

                if (numRead != toRead) break;
            }

            stream.Close();

            return dst - dataOffset;
        }

        // C: ufbx_read_geometry_cache_vec3 (ufbx.c:32941-32951). In C `data` is cast to
        // `ufbx_real*` and `count*3` contiguous reals land in the vec3 array; the port reads into
        // a flat buffer and unpacks.
        internal static int ReadGeometryCacheVec3(UfbxCacheFrame frame, UfbxVec3[] data, int dataOffset, int count, UfbxGeometryCacheDataOpts opts)
        {
            if (frame == null || count == 0) return 0;
            if (data == null) return 0;
            double[] flat = new double[count * 3];
            int numRead = ReadGeometryCacheReal(frame, flat, 0, count * 3, opts);
            int vec3Read = numRead / 3;
            // C: `ufbx_read_geometry_cache_real(frame, (ufbx_real*)data, count*3, opts)`
            // (ufbx.c:32949) writes every real it read, so a partial trailing vector (numRead not
            // a multiple of 3) still has its first one or two components written into data[count-1]
            // even though the return value rounds down.
            for (int i = 0; i < numRead; i++) {
                data[dataOffset + i / 3] = Vec3Set(data[dataOffset + i / 3], i % 3, flat[i]);
            }
            return vec3Read;
        }

        // C: ufbx_sample_geometry_cache_real (ufbx.c:32869-32939).
        internal static int SampleGeometryCacheReal(UfbxCacheChannel channel, double time, double[] data, int dataOffset, int count, UfbxGeometryCacheDataOpts userOpts)
        {
            if (channel == null || count == 0) return 0;
            if (data == null) return 0;
            if (channel.Frames.Length == 0) return 0;

            UfbxGeometryCacheDataOpts opts = CopyOpts(userOpts);

            int begin = 0;
            int end = channel.Frames.Length;
            UfbxCacheFrame[] frames = channel.Frames;
            while (end - begin >= 8) {
                int mid = (begin + end) >> 1;
                if (frames[mid].Time < time) {
                    begin = mid + 1;
                } else {
                    end = mid;
                }
            }

            const double eps = 0.00000001;

            end = channel.Frames.Length;
            for (; begin < end; begin++) {
                UfbxCacheFrame next = frames[begin];
                if (next.Time < time) continue;

                // First keyframe
                if (begin == 0) {
                    return ReadGeometryCacheReal(next, data, dataOffset, count, opts);
                }

                UfbxCacheFrame prev = frames[begin - 1];

                // Snap to exact frames if near
                if (UfbxMath.Abs(next.Time - time) < eps) {
                    return ReadGeometryCacheReal(next, data, dataOffset, count, opts);
                }
                if (UfbxMath.Abs(prev.Time - time) < eps) {
                    return ReadGeometryCacheReal(prev, data, dataOffset, count, opts);
                }

                double rcpDelta = 1.0 / (next.Time - prev.Time);
                double t = (time - prev.Time) * rcpDelta;

                double originalWeight = opts.UseWeight ? opts.Weight : 1.0;

                opts.UseWeight = true;
                opts.Weight = originalWeight * (1.0 - t);
                int numPrev = ReadGeometryCacheReal(prev, data, dataOffset, count, opts);

                opts.Additive = true;
                opts.Weight = originalWeight * t;
                return ReadGeometryCacheReal(next, data, dataOffset, numPrev, opts);
            }

            // Last frame
            UfbxCacheFrame last = frames[end - 1];
            return ReadGeometryCacheReal(last, data, dataOffset, count, opts);
        }

        // C: ufbx_sample_geometry_cache_vec3 (ufbx.c:32953-32963).
        internal static int SampleGeometryCacheVec3(UfbxCacheChannel channel, double time, UfbxVec3[] data, int dataOffset, int count, UfbxGeometryCacheDataOpts opts)
        {
            if (channel == null || count == 0) return 0;
            if (data == null) return 0;
            // C: reads count*3 reals into the vec3 array (contiguous doubles in the port).
            double[] flat = new double[count * 3];
            int numRead = SampleGeometryCacheReal(channel, time, flat, 0, count * 3, opts);
            int vec3Read = numRead / 3;
            // C: same flat-cast partial trailing vector as ufbx_read_geometry_cache_vec3 (32949).
            for (int i = 0; i < numRead; i++) {
                data[dataOffset + i / 3] = Vec3Set(data[dataOffset + i / 3], i % 3, flat[i]);
            }
            return vec3Read;
        }

        static UfbxVec3 Vec3Set(UfbxVec3 v, int axis, double value)
        {
            switch (axis) {
                case 0: v.X = value; break;
                case 1: v.Y = value; break;
                default: v.Z = value; break;
            }
            return v;
        }
    }
}

