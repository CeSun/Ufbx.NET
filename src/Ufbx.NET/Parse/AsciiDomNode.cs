// ASCII DOM node construction, ported from ufbx.c v0.23.1:
//   ufbxi_ascii_parse_node (10280-10690), ufbxi_setup_base64 (10219-10234),
//   ufbxi_decode_base64 (10236-10277).
//
// Faithfulness notes worth keeping in mind before editing:
//  * C's `tmp_buf` parameter (the caller's scratch buffer: `&uc->tmp` from
//    `ufbxi_parse_toplevel()`/`ufbxi_parse_legacy_toplevel()`, and `&uc->result` for
//    `retain_dom` arrays via `UFBXI_ARRAY_FLAG_RESULT`/`TMP_BUF`) is not reproduced. It only
//    chooses *which arena* the array payload, the `ufbx_string` items and the `vals` copy live
//    in; the DOM reads them through `data`/`size`, never through an arena address, so the port
//    just allocates the destination `byte[]`/`string[]`/`UfbxiValue[]`.
//  * C multiplexes `uc->tmp_stack` between `ufbxi_node` items, array element bytes,
//    `ufbx_string` items and the 8-byte alignment helper. Everything except the node items is
//    LIFO-balanced inside this function, so node items use `UfbxiContext.PushNode()`/
//    `PopNodes()`, string items a local list, and element bytes the shared byte stack
//    `UfbxiContext.TmpStack` -- which is also what `ufbxi_ascii_read_{int,float}_array()`
//    pushes to (PORTING_NOTES #4, same reasoning as the binary reader).
//  * The alignment helper push (`ufbxi_push_size_zero(&uc->tmp_stack, 8, 1)`) and its pop are
//    kept even though C only needs it to satisfy `ufbxi_push_fast()`'s alignment assert: the
//    item counts it contributes must be absent from `num_values`, and it is popped *after* the
//    element bytes (LIFO), so dropping it would shift the byte stack.
//  * C's threaded array branch (10569-10577: `ufbxi_ascii_store_array()` + a
//    `ufbxi_ascii_array_task`) requires `uc->parse_threaded`, which is only ever set when the
//    user supplies a thread pool, so with the default options it never fires. With a pool it is
//    the second task producer after the binary reader's DEFLATE task; see Parse/ThreadPool.cs
//    for the ring itself and `AsciiArrayTaskFn` below for the adapter the ring calls.
//    Not modelled: C's single `tmp_buf` per *batch* (the port gives each deferred array its own,
//    because there is no batch buffer to clear — PORTING_NOTES.md #4).
//  * `ufbxi_ascii_parse_node()` never writes `*p_end = false` (the callers initialise `end`);
//    the port's `out bool end` assigns `false` on every non-terminal path, which is the same
//    observable value. See `ParseNode()` in Parse/DomNode.cs.
//  * No FP contraction: `(float)val * (float)fsign` and `(double)val * fsign` stay two
//    operations, matching C's separate cast and multiply (PORTING_NOTES.md 浮点语义).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Ufbx.NET
{
    internal static partial class UfbxiDom
    {
        // C: `ufbxi_setup_base64()` (ufbx.c:10219-10234) builds a 256-entry table in
        // `uc->tmp` on first use. The contents are fixed, so a shared readonly instance is
        // equivalent: nothing observes the table's address.
        static readonly byte[] Base64Table = MakeBase64Table();

        static byte[] MakeBase64Table()
        {
            byte[] table = new byte[256];
            for (int i = 0; i < 256; i++) table[i] = 0x80;
            for (int c = 'A'; c <= 'Z'; c++) table[c] = (byte)(c - 'A');
            for (int c = 'a'; c <= 'z'; c++) table[c] = (byte)(26 + (c - 'a'));
            for (int c = '0'; c <= '9'; c++) table[c] = (byte)(52 + (c - '0'));
            table['+'] = 62;
            table['/'] = 63;
            table['='] = 0x40;
            return table;
        }

        // ------------------------------------------------------------------
        // Node parsing
        // ------------------------------------------------------------------

        // C: ufbxi_ascii_parse_node(uc, depth, parent_state, p_end, tmp_buf, recursive)
        // (ufbx.c:10280-10690). Throws instead of returning 0 (PORTING_NOTES.md #3).
        internal static void ParseNodeAscii(UfbxiContext uc, uint depth, UfbxiParseState parentState, out bool end, bool recursive)
        {
            UfbxiAscii ua = uc.Ascii;

            if (ua.Token.Type == '}') {
                ua.NextToken();
                end = true;
                return;
            }

            if (ua.Token.Type == UfbxiAscii.AsciiEnd) {
                // C: ufbxi_check_msg(depth == 0, "Truncated file") -- `*_msg` carries the human
                // readable description (ufbx.c:6655, 3415-3425), a plain `ufbxi_check` does not.
                UfbxiFail.CheckMsg(depth == 0, "Truncated file");
                end = true;
                return;
            }

            // Parse the name eg. "Node:" token and intern the name
            UfbxiFail.CheckNoDesc(depth < MaxNodeDepth, "depth < UFBXI_MAX_NODE_DEPTH");
            if (!uc.SureFbx && depth == 0 && ua.Token.Type != UfbxiAscii.AsciiName) {
                // C: ufbxi_fail_msg("Expected a 'Name:' token", "Not an FBX file") -- the
                // description is the second argument (ufbx.c:6657).
                UfbxiFail.FailMsg("Expected a 'Name:' token", "Not an FBX file");
            }
            UfbxiFail.CheckNoDesc(ua.Accept(UfbxiAscii.AsciiName), "ufbxi_ascii_accept(uc, UFBXI_ASCII_NAME)");
            int nameLen = ua.PrevToken.NameLen;
            UfbxiFail.CheckNoDesc(nameLen <= 0xff, "name_len <= 0xff");
            string name = ua.InternName(ua.PrevToken);
            UfbxiFail.CheckNoDesc(name != null, "name");

            // Push the parsed node into the `tmp_stack` buffer, the nodes will be popped by
            // calling code after its done parsing all of it's children.
            UfbxiNode node = new UfbxiNode();
            uc.PushNode(node);
            node.Name = name;
            node.NameLen = nameLen;

            bool inAsciiArray = false;

            uint numValues = 0;
            uint typeMask = 0;
            // C: `uint32_t deferred_size` (ufbx.c:10544) — the array elements left for the
            // deferred (threaded) task to fill; 0 means everything was parsed eagerly.
            uint deferredSize = 0;
            UfbxiAsciiBuf asciiStoreBuf = null;

            // C: `int arr_type` -- 0 means "not an array", otherwise an FBX type code.
            int arrType = 0;
            int arrElemSize = 0;
            bool arrError = false;
            UfbxiArrayInfo arrInfo = default(UfbxiArrayInfo);

            UfbxiValueArray arr = null;
            // C: the `ufbx_string` items of an 's'/'S'/'C' array, pushed onto `uc->tmp_stack`;
            // see the header note on why the port keeps them in a local list.
            List<string> strItems = null;

            // Check if the values of the node we're parsing currently should be treated as an
            // array.
            if (UfbxiParseStateMachine.IsArrayNode(uc, parentState, name, out arrInfo)) {
                UfbxiArrayFlags flags = arrInfo.Flags;
                arrType = UfbxiArrayType.Normalize(arrInfo.Type, 'b');

                arr = new UfbxiValueArray();
                node.ValueTypeMask = (uint)UfbxiValueType.Array;
                node.Array = arr;
                arr.Type = unchecked((char)arrType);

                // Parse array values using strtof() if the array destination is 32-bit float
                // since KeyAttrDataFloat packs integer data (!) into floating point values so we
                // should try to be as exact as possible.
                if ((flags & UfbxiArrayFlags.AccurateF32) != 0) ua.ParseAsF32 = true;

                arrElemSize = UfbxiArrayType.SizeOf(unchecked((char)arrType));

                if (arrType != '-') {
                    // Force alignment for array contents: This allows us to use
                    // `ufbxi_push_fast()` in fast parsing functions.
                    ua.TmpStack.PushSizeZero(8, 1);

                    // Pad with 4 zero elements to make indexing with `-1` safe.
                    if ((flags & UfbxiArrayFlags.PadBegin) != 0) {
                        ua.TmpStack.PushSizeZero(arrElemSize, 4);
                        numValues += 4;
                    }
                }

                if (IsStringArrayType(arrType)) strItems = new List<string>();
            }

            // Some fields in ASCII may have leading commas eg. `Content: , "base64-string"`
            if (ua.Token.Type == ',') {
                // HACK: If we are parsing an "array" that should be ignored, ie. `Content` when
                // `opts.ignore_embedded == true` try to skip the next token string if possible.
                if (arrType == '-') {
                    if (!ua.TryIgnoreString()) ua.NextToken();
                } else {
                    ua.NextToken();
                }
            }

            UfbxiParseState parseState = UfbxiParseStateMachine.Update(parentState, node.Name);
            UfbxiValue[] vals = new UfbxiValue[UfbxiNode.MaxNonArrayValues];

            // NOTE: Infinite loop to allow skipping the comma parsing via `continue`.
            for (;;) {
                UfbxiAsciiToken tok = ua.PrevToken;

                if (arrType != 0) {
                    int numRead = 0;
                    if (arrType == 'f' || arrType == 'd') {
                        ua.ReadFloatArray(unchecked((char)arrType), out numRead);
                    } else if (arrType == 'i' || arrType == 'l') {
                        ua.ReadIntArray(unchecked((char)arrType), out numRead);
                    }
                    UfbxiFail.CheckNoDesc(uint.MaxValue - numValues > (uint)numRead, "UINT32_MAX - num_values > num_read");
                    numValues += (uint)numRead;
                }

                if (ua.Accept(UfbxiAscii.AsciiString)) {

                    if (arrType != 0) {
                        if (arrType == 's' || arrType == 'S' || arrType == 'C') {
                            UfbxiFail.CheckNoDesc(strItems != null, "ufbxi_push(&uc->tmp_stack, ufbx_string, 1)");
                            if (arrType == 'C') {
                                // C pushes `capacity` bytes onto the destination buffer and
                                // decodes base64 into it (`buf = retain_dom ? &uc->result :
                                // tmp_buf`), so the payload is a fresh array here too.
                                long capacity = (long)tok.StrLen / 4 * 3 + 3;
                                // C: `v->data = ufbxi_push(buf, char, capacity); ufbxi_check(v->data);`
                                // (ufbx.c:10408-10409) -- a plain allocation check, no description.
                                if (capacity > int.MaxValue) UfbxiFail.FailNoDesc("v->data");
                                byte[] decoded = new byte[(int)capacity];
                                int decodedLen = DecodeBase64(uc, tok.StrData, tok.StrLen, decoded, ref arrError);
                                strItems.Add(UfbxiSanitizedString.FromBytes(decoded, 0, decodedLen));
                            } else {
                                bool raw = arrType == 's';
                                // Port-only guard: C assigns `v->data = tok->str_data` without a
                                // check (ufbx.c:10413); there is no error site, so no description.
                                UfbxiFail.CheckNoDesc(tok.StrData != null, "v->data");
                                string s = UfbxiRawStr.FromBytes(tok.StrData, 0, tok.StrLen);
                                UfbxiFail.CheckNoDesc(ua.InternStringValue(ref s, raw),
                                    "ufbxi_push_string_place_str(&uc->string_pool, v, raw)");
                                strItems.Add(s);
                            }
                        } else {
                            // Ignore strings in non-string arrays, decrement `num_values` as it
                            // will be incremented after the loop iteration is done to ignore it.
                            numValues--;
                        }
                    } else if (numValues < UfbxiNode.MaxNonArrayValues) {
                        int ix = (int)numValues;
                        typeMask |= (uint)UfbxiValueType.String << (ix * 2);

                        byte[] strData = tok.StrData;
                        int length = tok.StrLen;
                        UfbxiFail.CheckNoDesc(strData != null, "str");

                        if (length == 0) {
                            vals[ix].S.RawData = string.Empty;
                            vals[ix].S.Utf8Data = string.Empty;
                            vals[ix].S.RawLength = 0;
                            vals[ix].S.Utf8Length = 0;
                        } else {
                            string str = UfbxiRawStr.FromBytes(strData, 0, length);
                            bool nonAscii;
                            uint hash = UfbxiHash.HashStringCheckAscii(str, 0, length, out nonAscii);
                            bool raw = UfbxiParseStateMachine.IsRawString(uc, parentState, name, ix);
                            UfbxiFail.CheckNoDesc(uc.StringPool.PushSanitizedString(ref vals[ix].S, str, 0,
                                    length, hash, nonAscii, raw),
                                "ufbxi_push_sanitized_string(&uc->string_pool, &v->s, str, length, hash, non_ascii, raw)");
                        }
                    }

                } else if (ua.Accept(UfbxiAscii.AsciiInt)) {
                    long val = tok.I64;
                    // `-0` has `i64 == 0` but a leading '-': keep the sign through the float.
                    double fsign = val == 0 && tok.Negative ? -1.0 : 1.0;

                    switch (arrType) {

                    case 0:
                        // Parse version from comment if there was no magic comment
                        if (!ua.FoundVersion && parseState == UfbxiParseState.FbxVersion && numValues == 0) {
                            if (val >= 6000 && val <= 10000) {
                                ua.FoundVersion = true;
                                uc.Version = unchecked((uint)val);
                            }
                        }

                        if (numValues < UfbxiNode.MaxNonArrayValues) {
                            int ix = (int)numValues;
                            typeMask |= (uint)UfbxiValueType.Number << (ix * 2);
                            vals[ix].I = val;
                            vals[ix].F = (double)val * fsign;
                        }
                        break;

                    case 'b': PushBool(ua.TmpStack, val != 0); break;
                    case 'c': PushByte(ua.TmpStack, unchecked((byte)val)); break;
                    case 'i': PushInt32(ua.TmpStack, unchecked((int)val)); break;
                    case 'l': PushInt64(ua.TmpStack, val); break;
                    case 'f': PushSingle(ua.TmpStack, (float)val * (float)fsign); break;
                    case 'd': PushDouble(ua.TmpStack, (double)val * fsign); break;
                    case '-': numValues--; break;

                    default:
                        UfbxiFail.FailNoDesc("Bad array dst type");
                        break;

                    }

                } else if (ua.Accept(UfbxiAscii.AsciiFloat)) {
                    double val = tok.F64;

                    switch (arrType) {

                    case 0:
                        if (numValues < UfbxiNode.MaxNonArrayValues) {
                            int ix = (int)numValues;
                            typeMask |= (uint)UfbxiValueType.Number << (ix * 2);
                            // C: v->i = ufbxi_f64_to_i64(v->f = val)
                            vals[ix].F = val;
                            vals[ix].I = UfbxiBinaryArray.F64ToInt64(val);
                        }
                        break;

                    case 'b': PushBool(ua.TmpStack, val != 0); break;
                    case 'c': PushByte(ua.TmpStack, unchecked((byte)val)); break;
                    case 'i': PushInt32(ua.TmpStack, UfbxiBinaryArray.F64ToInt32(val)); break;
                    case 'l': PushInt64(ua.TmpStack, UfbxiBinaryArray.F64ToInt64(val)); break;
                    case 'f': PushSingle(ua.TmpStack, (float)val); break;
                    case 'd': PushDouble(ua.TmpStack, val); break;
                    case '-': numValues--; break;

                    default:
                        UfbxiFail.FailNoDesc("Bad array dst type");
                        break;

                    }

                } else if (ua.Accept(UfbxiAscii.AsciiBareWord)) {

                    long val = 0;
                    double valF = 0.0;
                    if (tok.StrLen >= 1) {
                        // C: `val = (int64_t)tok->str_data[0]` -- `str_data` is `char*`, and the
                        // reference build's `char` is signed, so bytes >= 0x80 sign-extend.
                        val = (long)(sbyte)tok.StrData[0];
                        valF = (double)val;
                        if (tok.StrLen > 1 && tok.StrLen < 64) {
                            // Try to parse the bare word as NAN/INF. C copies the token into a
                            // 64-byte buffer and NUL-terminates it, then requires the whole
                            // token to be consumed; `ParseInfNan` only reads within its span.
                            double infNan;
                            int infNanEnd;
                            if (UfbxiNumeric.ParseInfNan(new ReadOnlySpan<byte>(tok.StrData, 0, tok.StrLen),
                                    out infNan, out infNanEnd) && infNanEnd == tok.StrLen) {
                                val = 0;
                                valF = infNan;
                            }
                        }
                    }

                    switch (arrType) {

                    case 0:
                        if (numValues < UfbxiNode.MaxNonArrayValues) {
                            int ix = (int)numValues;
                            typeMask |= (uint)UfbxiValueType.Number << (ix * 2);
                            vals[ix].I = val;
                            vals[ix].F = valF;
                        }
                        break;

                    case 'b': PushBool(ua.TmpStack, val != 0); break;
                    case 'c': PushByte(ua.TmpStack, unchecked((byte)val)); break;
                    case 'i': PushInt32(ua.TmpStack, unchecked((int)val)); break;
                    case 'l': PushInt64(ua.TmpStack, val); break;
                    case 'f': PushSingle(ua.TmpStack, (float)valF); break;
                    case 'd': PushDouble(ua.TmpStack, valF); break;
                    case '-': numValues--; break;

                    default:
                        UfbxiFail.FailNoDesc("Bad array dst type");
                        break;

                    }

                } else if (ua.Accept('*')) {
                    // Parse a post-7000 ASCII array eg. "*3 { 1,2,3 }"
                    UfbxiFail.CheckNoDesc(!inAsciiArray, "!in_ascii_array");
                    UfbxiFail.CheckNoDesc(ua.Accept(UfbxiAscii.AsciiInt), "ufbxi_ascii_accept(uc, UFBXI_ASCII_INT)");

                    if (ua.Accept('{')) {
                        UfbxiFail.CheckNoDesc(ua.Accept(UfbxiAscii.AsciiName), "ufbxi_ascii_accept(uc, UFBXI_ASCII_NAME)");
                        inAsciiArray = true;

                        if (arrType == '-') {
                            // Optimized array skipping
                            ua.SkipUntil('}');
                        } else if (uc.ParseThreaded && !uc.Opts.ForceSingleThreadAsciiParsing &&
                                !ua.ParseAsF32 &&
                                (arrType == 'i' || arrType == 'l' || arrType == 'f' || arrType == 'd')) {
                            // Don't bother with small arrays due to fixed overhead.
                            // C: `int64_t count = ua->prev_token.value.i64` (ufbx.c:10562), read
                            // straight after accepting UFBXI_ASCII_INT.
                            long count = ua.PrevToken.I64;
                            if (count >= MinThreadedAsciiValues && count <= uint.MaxValue) {
                                deferredSize = (uint)count - 1;
                                // C hands the caller's `tmp_buf` to `ufbxi_ascii_store_array()`
                                // (ufbx.c:10576) so the spans outlive the read window; the port
                                // owns one per deferred array instead of one per batch — there
                                // is no batch buffer to clear (PORTING_NOTES.md #4) and the task
                                // holds it until it runs.
                                asciiStoreBuf = new UfbxiAsciiBuf();
                                ua.StoreArray(asciiStoreBuf);
                            }
                        }
                    }

                    // NOTE: This `continue` skips incrementing `num_values` and parsing
                    // a comma, continuing to parse the values in the array.
                    continue;
                } else {
                    break;
                }

                // Add value and keep parsing if there's a comma. This part may be skipped if we
                // enter an array block.
                numValues++;
                UfbxiFail.CheckNoDesc(numValues < uint.MaxValue, "num_values < UINT32_MAX");
                if (!ua.Accept(',')) break;
            }

            // Close the ASCII array if we are in one
            if (inAsciiArray) {
                UfbxiFail.CheckNoDesc(ua.Accept('}'), "ufbxi_ascii_accept(uc, '}')");
            }

            ua.ParseAsF32 = false;

            if (arrType != 0) {
                int padElements = (arrInfo.Flags & UfbxiArrayFlags.PadBegin) != 0 ? 4 : 0;

                if (arrType == '-') {
                    // C: arr->data = NULL, arr->size = 0. `RetainDomNode()` only reads `Size`
                    // for a '-' array (`SizeOf('-') == 1`), so null buffers are equivalent.
                    arr.Size = 0;
                } else if (IsStringArrayType(arrType)) {
                    // C: `ufbxi_push_pop_size(arr_buf, &uc->tmp_stack, sizeof(ufbx_string),
                    // num_values)` moves the `ufbx_string` records, with the PAD_BEGIN slots
                    // first. `array_type_size()` gives the string types a stride of
                    // `sizeof(ufbx_string)`, so in the port the byte stack holds only the
                    // alignment helper and the pad while the payloads are the local list.
                    int numItems = (int)numValues - padElements;
                    // Port-only guards: C's `ufbxi_pop_size()`/`ufbxi_push_pop_size()` assert
                    // `num_items >= n` (ufbx.c:4184, a no-op/abort assert, not an error site);
                    // these stand in for that accounting on the port's local string list.
                    UfbxiFail.CheckNoDesc(numItems >= 0, "num_values >= pad_elements");
                    UfbxiFail.CheckNoDesc(strItems != null && strItems.Count >= numItems, "tmp_stack.num_items >= n");

                    int start = (int)strItems.Count - numItems;
                    if (arrError) {
                        // C: `ufbxi_pop_size(&uc->tmp_stack, arr_elem_size, num_values, NULL,
                        // false); num_values = 0;` -- bad base64 content drops the *whole* array,
                        // including the bytes decoded before the failure and any elements that
                        // were themselves fine. C's `size = num_values - 4` would underflow for a
                        // PAD_BEGIN type, which is unreachable: padding is only used by the
                        // numeric array types.
                        strItems.RemoveRange(start, numItems);
                        arr.Strings = null;
                        arr.Offset = 0;
                        arr.Size = 0;
                    } else {
                        string[] strings = new string[(int)numValues];
                        for (int i = 0; i < numItems; i++) strings[padElements + i] = strItems[start + i];
                        strItems.RemoveRange(start, numItems);
                        arr.Strings = strings;
                        arr.Offset = padElements;
                        arr.Size = numItems;
                    }

                    // Pop the PAD_BEGIN placeholder bytes and the alignment helper.
                    ua.TmpStack.PopSize(arrElemSize, padElements, null, 0);
                    ua.TmpStack.PopSize(8, 1, null, 0);
                } else {
                    // C: `arr_data = ufbxi_push_size(arr_buf, arr_elem_size, num_values +
                    // deferred_size)`; only the eagerly parsed `num_values` elements come off the
                    // stack, the rest are the deferred task's destination.
                    long totalValues = numValues;
                    if (deferredSize > 0) {
                        UfbxiFail.CheckNoDesc(deferredSize < uint.MaxValue - numValues,
                            "deferred_size < UINT32_MAX - num_values");
                        totalValues = (long)numValues + deferredSize;
                    }
                    long total = totalValues * arrElemSize;
                    // C: `arr_data = ufbxi_push_pop_size(...)`/`ufbxi_push_size(...)` followed by
                    // `ufbxi_check(arr_data)` (ufbx.c:10620-10622) -- a plain allocation check.
                    if (total > int.MaxValue) UfbxiFail.FailNoDesc("arr_data");

                    byte[] arrData;
                    if (arrError) {
                        // A bad base64 payload is dropped whole: the elements are popped without
                        // being kept and the array reads as empty (`ufbxi_zero_size_buffer`).
                        // Only the 'C' string type can set `arr_error`, so this stays on the
                        // numeric path for C's sake and keeps the byte stack balanced.
                        ua.TmpStack.PopSize(arrElemSize, (int)numValues, null, 0);
                        numValues = 0;
                        arrData = new byte[0];
                    } else {
                        arrData = new byte[(int)total];
                        ua.TmpStack.PopSize(arrElemSize, (int)numValues, arrData, 0);
                    }

                    // Port-only guard: C computes `num_values + deferred_size - 4` with no check
                    // (ufbx.c:10625); the port's element count is signed.
                    UfbxiFail.CheckNoDesc(totalValues >= (uint)padElements, "num_values + deferred_size >= pad_elements");
                    if (padElements != 0) {
                        arr.Offset = padElements * arrElemSize;
                        arr.Size = (int)totalValues - padElements;
                    } else {
                        arr.Offset = 0;
                        arr.Size = (int)totalValues;
                    }
                    arr.Data = arrData;

                    // Pop alignment helper
                    ua.TmpStack.PopSize(8, 1, null, 0);

                    // C: deferred parsing (ufbx.c:10636-10657). `ufbxi_ascii_store_array()`
                    // bufferred the remaining bytes as spans above; the task walks them later.
                    if (deferredSize > 0) {
                        int numSpans = ua.TmpAsciiSpans.Count;
                        UfbxiAsciiSpan[] spans = ua.TmpAsciiSpans.ToArray();
                        ua.TmpAsciiSpans.Clear();

                        UfbxiAsciiArrayTask t = new UfbxiAsciiArrayTask();
                        // C: `t.arr_data = (char*)arr_data + num_values * arr_elem_size` (10640) —
                        // the destination starts after the eagerly parsed elements.
                        t.ArrData = arrData;
                        t.ArrDataOffset = (int)numValues * arrElemSize;
                        t.ArrType = unchecked((char)arrType);
                        t.ArrSize = (int)deferredSize;
                        t.NumSpans = numSpans;
                        t.Spans = spans;
                        t.Offset = 0;

                        UfbxiTask task = uc.ThreadPool != null
                            ? uc.ThreadPool.CreateTask(AsciiArrayTaskFn) : null;
                        if (task != null) {
                            task.Data = t;
                            uc.ThreadPool.RunTask(task);
                        } else {
                            // C: the `else` of `if (task)` (ufbx.c:10654-10656) — no pool, so the
                            // array is parsed right here with the same error text.
                            UfbxiFail.CheckMsg(UfbxiAscii.ArrayTaskImp(t), "Threaded ASCII parse error");
                        }
                    }
                }
            } else {
                if (numValues > UfbxiNode.MaxNonArrayValues) numValues = UfbxiNode.MaxNonArrayValues;
                node.ValueTypeMask = unchecked((uint)(ushort)typeMask);
                node.Vals = new UfbxiValue[(int)numValues];
                Array.Copy(vals, 0, node.Vals, 0, (int)numValues);
            }

            // Recursively parse the children of this node. Update the parse state to provide
            // context for child node parsing.
            if (ua.Accept('{')) {
                if (recursive) {
                    uint numChildren = 0;
                    for (;;) {
                        bool childEnd;
                        ParseNodeAscii(uc, depth + 1, parseState, out childEnd, recursive);
                        if (childEnd) break;
                        numChildren++;
                    }

                    // Pop children from `tmp_stack` to a contiguous array
                    node.NumChildren = numChildren;
                    if (numChildren > 0) {
                        node.Children = uc.PopNodes((int)numChildren);
                    }
                }

                uc.HasNextChild = true;
            } else {
                uc.HasNextChild = false;
            }

            end = false;
        }

        // C: `UFBXI_MIN_THREADED_ASCII_VALUES` (ufbx.c:61; UFBX_REGRESSION/UFBX_EXTENSIVE_THREADING
        // redefine it to 2 at ufbx.c:1019). Kept for the threading predicate.
        const long MinThreadedAsciiValues = 64;

        // C: ufbxi_ascii_array_task_fn (ufbx.c:10145-10153) as a `ufbxi_task_fn`, which is the
        // form the ring needs; failures travel out through `task->error`.
        static bool AsciiArrayTaskFn(UfbxiTask task)
        {
            return UfbxiAscii.ArrayTaskFn((UfbxiAsciiArrayTask)task.Data, out task.Error);
        }

        // C: the string element types of `ufbxi_array_type_size()`, whose items are
        // `ufbx_string` records rather than plain bytes.
        static bool IsStringArrayType(int arrType)
        {
            return arrType == 's' || arrType == 'S' || arrType == 'C';
        }

        // ------------------------------------------------------------------
        // tmp_stack element pushes (C: ufbxi_push(&uc->tmp_stack, T, 1) with the value written
        // in place). Item counts follow C: `ufbxi_push_size()` bumps `num_items` even for the
        // alignment helper and the PAD_BEGIN zeros.
        // ------------------------------------------------------------------

        static void PushBool(UfbxiAsciiTmpStack stack, bool value)
        {
            int pos = stack.PushSize(1, 1);
            stack.Data[pos] = value ? (byte)1 : (byte)0;
        }

        static void PushByte(UfbxiAsciiTmpStack stack, byte value)
        {
            int pos = stack.PushSize(1, 1);
            stack.Data[pos] = value;
        }

        static void PushInt32(UfbxiAsciiTmpStack stack, int value)
        {
            int pos = stack.PushSize(4, 1);
            BinaryPrimitives.WriteInt32LittleEndian(stack.Data.AsSpan(pos, 4), value);
        }

        static void PushInt64(UfbxiAsciiTmpStack stack, long value)
        {
            int pos = stack.PushSize(8, 1);
            BinaryPrimitives.WriteInt64LittleEndian(stack.Data.AsSpan(pos, 8), value);
        }

        static void PushSingle(UfbxiAsciiTmpStack stack, float value)
        {
            int pos = stack.PushSize(4, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(stack.Data.AsSpan(pos, 4), UfbxBitUtil.SingleToBits(value));
        }

        static void PushDouble(UfbxiAsciiTmpStack stack, double value)
        {
            int pos = stack.PushSize(8, 1);
            BinaryPrimitives.WriteUInt64LittleEndian(stack.Data.AsSpan(pos, 8), unchecked((ulong)UfbxBitUtil.BitsOf(value)));
        }

        // ------------------------------------------------------------------
        // Base64
        // ------------------------------------------------------------------

        // C: ufbxi_decode_base64(uc, p_result, src, src_length, p_failed) (ufbx.c:10236-10277).
        // The port returns the decoded length instead of writing `p_result->{data,length}`;
        // `dst` is C's pushed destination buffer of at least `src_length / 4 * 3 + 3` bytes, so
        // the writes stay in range: the group loop emits 3 bytes per 4 consumed input bytes and
        // the padding adjustment only ever shortens the result.
        static int DecodeBase64(UfbxiContext uc, byte[] src, int srcLength, byte[] dst, ref bool failed)
        {
            byte[] table = Base64Table;
            uint errorMask = 0, padError = 0;

            int p = 0;
            for (int i = 0; i + 4 <= srcLength; i += 4) {
                uint a = table[src[i + 0]];
                uint b = table[src[i + 1]];
                uint c = table[src[i + 2]];
                uint d = table[src[i + 3]];
                padError = errorMask;
                errorMask |= a | b | c | d;

                dst[p + 0] = unchecked((byte)((a << 2) | (b >> 4)));
                dst[p + 1] = unchecked((byte)((b << 4) | (c >> 2)));
                dst[p + 2] = unchecked((byte)((c << 6) | d));
                p += 3;
            }

            if (srcLength >= 4) {
                int end = srcLength - 4;
                uint padding = 0;
                padding |= src[end + 0] == (byte)'=' ? 0x8u : 0x0u;
                padding |= src[end + 1] == (byte)'=' ? 0x4u : 0x0u;
                padding |= src[end + 2] == (byte)'=' ? 0x2u : 0x0u;
                padding |= src[end + 3] == (byte)'=' ? 0x1u : 0x0u;
                if (padding <= 0x1) p -= (int)padding;   // "xxx=" or "xxxx"
                else if (padding == 0x3) p -= 2;         // "xx=="
                else padError |= 0x40;                   // anything else
            }

            if (((errorMask & 0x80) != 0 || (padError & 0x40) != 0 || srcLength % 4 != 0) && !failed) {
                UfbxiFail.CheckNoDesc(UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.BadBase64Content,
                        UfbxiWarnings.NoElementId, "Ignored bad base64 embedded content"),
                    "ufbxi_warnf(UFBX_WARNING_BAD_BASE64_CONTENT, ...)");
                failed = true;
            }

            return p;
        }
    }
}
