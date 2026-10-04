// Binary DOM construction, ported from ufbx.c v0.23.1:
//   ufbxi_begin_parse (11189-11236), ufbxi_binary_parse_node (8959-8933),
//   ufbxi_get_dom_node_imp/ufbxi_get_dom_node (10699-10712), ufbxi_retain_dom_node
//   (10715-10807), ufbxi_retain_toplevel/ufbxi_retain_toplevel_child (10809-10849).
//
// Representation (PORTING_NOTES.md "DOM/数组数据约定"):
//  - C pushes `ufbxi_node` items onto `uc->tmp_stack` and pops them into the parent's
//    `children` array. C multiplexes that one byte stack between node items, `ufbx_dom_value`
//    items and ASCII array bytes, but every use is LIFO-balanced inside its own function, so
//    the port's typed `List<T>` stacks (UfbxiContext.PushNode/PopNodes/…) are equivalent.
//  - Array payloads stay raw little-endian `byte[]` (`UfbxiValueArray.Data`/`Offset`); string
//    arrays ('s'/'S'/'C') use `Strings`. `UfbxDomValue.ValueBlob` copies the element bytes, and
//    for ARRAY_BLOB packs each string as `[8-byte LE size][bytes]` because a `byte[]` cannot
//    hold C's `ufbx_blob[]` pointer records (the same convention `UfbxHashScene` unpacks).
//  - C allocates the DOM out of `uc->result` (arena, never freed until scene free). The port
//    just `new`s; nothing observable changes, since the arena's layout is only visible through
//    pointer order and `dom_node_map` is lookup-only (never iterated).
//
// Not reproduced (C behaviour this port cannot match by construction, and no corpus file
// reaches): an explicit array header whose `arr_info` element type is a string type
// ('s'/'S'/'C'). C then writes 4/8-byte elements into a `sizeof(ufbx_string)`-strided
// *uninitialized* arena buffer and the DOM blob reads whatever the arena held there; the port
// reads zeros. See the note on `ParseNodeArray`.

using System;

namespace Ufbx.NET
{
    internal static partial class UfbxiDom
    {
        // C: #define UFBXI_MAX_NODE_DEPTH 32 (ufbx.c:52)
        internal const int MaxNodeDepth = 32;

        // C: UFBXI_BINARY_MAGIC_SIZE / UFBXI_BINARY_HEADER_SIZE (ufbx.c:9395-9397)
        const int BinaryMagicSize = 22;
        const int BinaryHeaderSize = 27;

        // C: static const char ufbxi_binary_magic[] = "Kaydara FBX Binary  \x00\x1a"
        static readonly byte[] BinaryMagic = MakeMagic();

        static byte[] MakeMagic()
        {
            const string text = "Kaydara FBX Binary  ";
            byte[] magic = new byte[BinaryMagicSize];
            for (int i = 0; i < text.Length; i++) magic[i] = (byte)text[i];
            magic[text.Length] = 0x00;
            magic[text.Length + 1] = 0x1a;
            return magic;
        }

        // ------------------------------------------------------------------
        // Header
        // ------------------------------------------------------------------

        // C: ufbxi_begin_parse() (ufbx.c:11189-11236). Peeks the 27-byte header: the binary
        // magic selects the binary reader, anything else is ASCII.
        internal static void BeginParse(UfbxiContext uc)
        {
            UfbxiStream stream = uc.Stream;
            int header = stream.PeekBytes(BinaryHeaderSize);
            byte[] buf = stream.Buffer;

            bool binary = true;
            for (int i = 0; i < BinaryMagicSize; i++) {
                if (buf[header + i] != BinaryMagic[i]) {
                    binary = false;
                    break;
                }
            }

            if (binary) {
                // C: the byte after the magic indicates endianness
                uc.FileBigEndian = buf[header + BinaryMagicSize] != 0;

                int versionOffset = header + BinaryMagicSize + 1;
                byte[] versionWord = buf;
                if (uc.FileBigEndian) {
                    versionWord = UfbxiBinaryArray.SwapEndian(uc, versionWord, versionOffset, 1, 4);
                    UfbxiFail.CheckNoDesc(versionWord != null, "ufbxi_swap_endian(uc, version_word, 1, 4)");
                    versionOffset = 0;
                }
                uc.Version = UfbxiBinaryArray.LE.U32(versionWord, versionOffset);

                uc.SureFbx = true;
                stream.ConsumeBytes(BinaryHeaderSize);
            } else {
                uc.FromAscii = true;

                // C: memset(&uc->ascii, 0, ...) then seed src/src_yield/src_end from the buffer
                if (uc.Ascii == null) uc.Ascii = new UfbxiAscii();
                uc.Ascii.ResetFromStream(uc, stream);
                uc.Ascii.NextToken();

                // C: default to version 7400 if not found in the header
                if (uc.Version > 0) {
                    uc.SureFbx = true;
                } else {
                    if (!uc.Opts.Strict) uc.Version = 7400;
                    UfbxiFail.CheckMsg(uc.Version > 0, "Not an FBX file");
                }
            }
        }

        // ------------------------------------------------------------------
        // Node parsing
        // ------------------------------------------------------------------

        // C: ufbxi_does_overflow(total, a, b) (ufbx.c:3651-3658). Only operands reaching the
        // top 32 bits are treated as overflow candidates; below that C trusts the product.
        static bool DoesOverflow(ulong total, ulong a, ulong b)
        {
            if (((a | b) >> 32) != 0) {
                if (a != 0 && total / a != b) return true;
            }
            return false;
        }

        // C: the two `ufbxi_parse_toplevel*()` call sites dispatch on `uc->from_ascii`
        // (ufbx.c:11240-11244, 11269-11273, 11380-11384), which `ufbxi_begin_parse()` sets from
        // the file magic. Same signature on both readers, so the driver only calls this.
        internal static void ParseNode(UfbxiContext uc, uint depth, UfbxiParseState parentState, out bool end, bool recursive)
        {
            if (uc.FromAscii) {
                ParseNodeAscii(uc, depth, parentState, out end, recursive);
            } else {
                ParseNodeBinary(uc, depth, parentState, out end, recursive);
            }
        }

        // C: ufbxi_binary_parse_node(uc, depth, parent_state, p_end, tmp_buf, recursive)
        // (ufbx.c:8959-8993). Throws instead of returning 0 (PORTING_NOTES.md #3).
        internal static void ParseNodeBinary(UfbxiContext uc, uint depth, UfbxiParseState parentState, out bool end, bool recursive)
        {
            UfbxiFail.CheckNoDesc(depth < MaxNodeDepth, "depth < UFBXI_MAX_NODE_DEPTH");

            UfbxiStream stream = uc.Stream;
            bool isPost7500 = uc.Version >= 7500;
            int headerSize = isPost7500 ? 25 : 13;

            // Parse the node header; post-7500 versions use 64-bit header fields.
            int header = stream.ReadBytes(headerSize);
            byte[] headerBytes = stream.Buffer;
            UfbxiFail.CheckNoDesc(headerBytes != null, "ufbxi_read_bytes(uc, header_size)");

            byte[] words = headerBytes;
            int wordsOffset = header;
            ulong endOffset, numValues64, valuesLen;
            int nameLen;

            if (isPost7500) {
                if (uc.FileBigEndian) {
                    words = UfbxiBinaryArray.SwapEndian(uc, words, wordsOffset, 3, 8);
                    UfbxiFail.CheckNoDesc(words != null, "ufbxi_swap_endian(uc, header_words, 3, 8)");
                    wordsOffset = 0;
                }
                endOffset = UfbxiBinaryArray.LE.U64(words, wordsOffset + 0);
                numValues64 = UfbxiBinaryArray.LE.U64(words, wordsOffset + 8);
                valuesLen = UfbxiBinaryArray.LE.U64(words, wordsOffset + 16);
                nameLen = UfbxiBinaryArray.LE.U8(headerBytes, header + 24);
            } else {
                if (uc.FileBigEndian) {
                    words = UfbxiBinaryArray.SwapEndian(uc, words, wordsOffset, 3, 4);
                    UfbxiFail.CheckNoDesc(words != null, "ufbxi_swap_endian(uc, header_words, 3, 4)");
                    wordsOffset = 0;
                }
                endOffset = UfbxiBinaryArray.LE.U32(words, wordsOffset + 0);
                numValues64 = UfbxiBinaryArray.LE.U32(words, wordsOffset + 4);
                valuesLen = UfbxiBinaryArray.LE.U32(words, wordsOffset + 8);
                nameLen = UfbxiBinaryArray.LE.U8(headerBytes, header + 12);
            }

            UfbxiFail.CheckNoDesc(numValues64 <= uint.MaxValue, "num_values64 <= UINT32_MAX");
            uint numValues = unchecked((uint)numValues64);

            // Zero `end_offset` and `name_len` is a NULL-sentinel terminating a node list.
            if (endOffset == 0 && nameLen == 0) {
                end = true;
                return;
            }

            // Update the estimated end offset if possible.
            if (endOffset > stream.ProgressBytesTotal) {
                stream.ProgressBytesTotal = endOffset;
            }

            // C: ufbxi_push_zero(&uc->tmp_stack, ufbxi_node, 1) — popped by the parent.
            UfbxiNode node = new UfbxiNode();
            uc.PushNode(node);

            // Parse and intern the name into the string pool (raw: node names stay unsanitized,
            // `ufbxi_retain_dom_node` sanitizes the DOM copy instead).
            int namePos = stream.ReadBytes(nameLen);
            string rawName = UfbxiRawStr.FromBytes(stream.Buffer, namePos, nameLen);
            string name = uc.StringPool.PushString(rawName, 0, nameLen, true);
            UfbxiFail.CheckNoDesc(name != null, "ufbxi_push_string(&uc->string_pool, name, name_len, NULL, true)");
            node.NameLen = nameLen;
            node.Name = name;

            ulong valuesEndOffset = stream.GetReadOffset() + valuesLen;

            UfbxiArrayInfo arrInfo;
            if (UfbxiParseStateMachine.IsArrayNode(uc, parentState, name, out arrInfo)) {
                ParseNodeArray(uc, node, arrInfo, numValues);
            } else {
                ParseNodeValues(uc, node, parentState, name, numValues);
            }

            // Skip remaining values: the list may have been truncated, or there may be values
            // after an array.
            ulong offset = stream.GetReadOffset();
            UfbxiFail.CheckNoDesc(offset <= valuesEndOffset, "offset <= values_end_offset");
            if (offset < valuesEndOffset) {
                stream.SkipBytes(valuesEndOffset - offset);
            }

            if (recursive) {
                UfbxiParseState parseState = UfbxiParseStateMachine.Update(parentState, node.Name);
                uint numChildren = 0;
                for (;;) {
                    ulong currentOffset = stream.GetReadOffset();
                    if (currentOffset >= endOffset) {
                        UfbxiFail.CheckNoDesc(currentOffset == endOffset || endOffset == 0,
                            "current_offset == end_offset || end_offset == 0");
                        break;
                    }

                    bool childEnd;
                    ParseNode(uc, depth + 1, parseState, out childEnd, true);
                    if (childEnd) break;
                    numChildren++;
                }

                // C: ufbxi_push_pop(tmp_buf, &uc->tmp_stack, ufbxi_node, num_children)
                node.NumChildren = numChildren;
                if (numChildren > 0) {
                    node.Children = uc.PopNodes((int)numChildren);
                }
            } else {
                uc.HasNextChild = stream.GetReadOffset() < endOffset;
            }

            end = false;
        }

        // C: the `ufbxi_is_array_node()` branch of ufbxi_binary_parse_node (ufbx.c:9027-9251).
        static void ParseNodeArray(UfbxiContext uc, UfbxiNode node, UfbxiArrayInfo arrInfo, uint numValues)
        {
            UfbxiStream stream = uc.Stream;

            // 'r' normalizes to 'd'/'f' by build, and bool arrays 'b' normalize to 'c' here;
            // they are postprocessed below based on `arr_info.type` instead.
            char dstType = UfbxiArrayType.Normalize(arrInfo.Type, 'c');

            UfbxiValueArray arr = new UfbxiValueArray();
            node.ValueTypeMask = (uint)UfbxiValueType.Array;
            node.Array = arr;
            arr.Type = UfbxiArrayType.Normalize(arrInfo.Type, 'b');

            // Valid FBX files end in a 13/25 byte NULL record, so 13 bytes are always peekable.
            int data = stream.PeekBytes(13);
            byte[] window = stream.Buffer;
            UfbxiFail.CheckNoDesc(window != null, "ufbxi_peek_bytes(uc, 13)");

            // The first byte is the explicit array type char (post-7000); pre-7000 stores the
            // elements as ordinary properties, so the char is then a property type.
            char c = (char)window[data];

            // C: HACK — override the type if the array is empty or its contents are ignored.
            if (numValues == 0) c = '0';
            if (dstType == '-') c = '-';

            // C: ufbx.c:9057 -- `deferred` is declared before the type dispatch because the
            // bool postprocess below is skipped for a task-deferred array (it runs in the task).
            bool deferred = false;

            if (c == 'c' || c == 'b' || c == 'i' || c == 'l' || c == 'f' || c == 'd') {
                byte[] arrWords = window;
                int arrWordsOffset = data + 1;
                if (uc.FileBigEndian) {
                    arrWords = UfbxiBinaryArray.SwapEndian(uc, arrWords, arrWordsOffset, 3, 4);
                    UfbxiFail.CheckNoDesc(arrWords != null, "ufbxi_swap_endian(uc, arr_words, 3, 4)");
                    arrWordsOffset = 0;
                }

                char srcType = c;
                uint size = UfbxiBinaryArray.LE.U32(arrWords, arrWordsOffset + 0);
                uint encoding = UfbxiBinaryArray.LE.U32(arrWords, arrWordsOffset + 4);
                uint encodedSize = UfbxiBinaryArray.LE.U32(arrWords, arrWordsOffset + 8);
                stream.ConsumeBytes(13);

                // Normalize the source type too, but leave the UFBX-specific 'r' alone so that
                // converting it fails later instead of silently changing elements.
                if (srcType != 'r') srcType = UfbxiArrayType.Normalize(srcType, 'c');
                int srcElemSize = UfbxiArrayType.SizeOf(srcType);
                ulong decodedDataSize64 = (ulong)srcElemSize * (ulong)size;
                UfbxiFail.CheckNoDesc(!DoesOverflow(decodedDataSize64, (ulong)srcElemSize, (ulong)size),
                    "!ufbxi_does_overflow(decoded_data_size, src_elem_size, size)");
                // Port-only guard: a managed `byte[]`/`string[]` cannot hold more than
                // `Array.MaxLength` (< int.MaxValue) items, so C's `ufbxi_push_array_data()`
                // allocation cannot be materialised. On the reference machine that allocation
                // succeeds and the *next* checks of ufbxi_binary_parse_node decide the outcome,
                // so reproduce them here:
                //   * DEFLATE (encoding 1): `ufbx_inflate()` returns `int` in this port, so it
                //     can never produce `decoded_data_size > int.MaxValue` bytes and C's msg
                //     check `ufbxi_check_msg(res == decoded_data_size, "Bad DEFLATE data")`
                //     (ufbx.c:9220) fails;
                //   * encoding 0: the plain `ufbxi_check(encoded_size == decoded_data_size)`
                //     (ufbx.c:9147) fails;
                //   * any other encoding: the plain `ufbxi_fail("Bad array encoding")`
                //     (ufbx.c:9223).
                if (decodedDataSize64 > int.MaxValue) {
                    if (encoding == 1) UfbxiFail.FailMsg("res == decoded_data_size", "Bad DEFLATE data");
                    if (encoding == 0) UfbxiFail.CheckNoDesc(false, "encoded_size == decoded_data_size");
                    UfbxiFail.FailNoDesc("Bad array encoding");
                }
                int decodedDataSize = unchecked((int)decodedDataSize64);

                // C: ufbxi_push_array_data(uc, &arr_info, size, tmp_buf). Element count here is
                // `size` from the array header.
                byte[] arrData;
                int arrOffset;
                UfbxiFail.CheckNoDesc(UfbxiBinaryArray.PushArrayData(uc, arrInfo, unchecked((int)size), out arrData, out arrOffset),
                    "ufbxi_push_array_data(uc, &arr_info, size, tmp_buf)");

                ulong arrBegin = stream.GetReadOffset();
                UfbxiFail.CheckNoDesc(ulong.MaxValue - encodedSize > arrBegin, "UINT64_MAX - encoded_size > arr_begin");
                ulong arrEnd = arrBegin + encodedSize;
                if (arrEnd > stream.ProgressBytesTotal) {
                    stream.ProgressBytesTotal = arrEnd;
                }

                // Threading. C deflates in a task when `uc->parse_threaded` — which only happens
                // with a user-supplied thread pool — and the task body (ufbxi_deflate_task_fn,
                // ufbx.c:8912-8956) is: inflate into the decoded buffer, convert unless it already
                // is the destination, then postprocess a bool array.
                //
                // C asks the ring for a task *first* (ufbx.c:9094) and only then reads the encoded
                // bytes into it; when `ufbxi_thread_pool_create_task()` declines — no pool, or a
                // full ring — `deferred` stays false and the ordinary inline path below runs. So
                // "task obtained" and "deferred" are the same condition here, exactly as in C.
                UfbxiTask deflateTask = null;
                if (uc.ParseThreaded && encoding == 1 && encodedSize >= MinThreadedDeflateBytes &&
                    !uc.FileBigEndian && !uc.LocalBigEndian && uc.ThreadPool != null) {
                    deflateTask = uc.ThreadPool.CreateTask(DeflateTaskFn);
                    if (deflateTask != null) deferred = true;
                }

                // If source and destination types match and the build is binary-compatible with
                // the format, decode straight into the array buffer; otherwise a temporary
                // buffer holds the decoded elements until conversion. `decodedData`/`decodedOffset`
                // is C's `decoded_data` pointer, which already sits past the PAD_BEGIN padding,
                // so a non-zero offset is still the in-place destination.
                // The one port-only spill is DEFLATE into a padded array: `UfbxInflate` writes
                // from index 0 of the buffer it is handed, so it decodes into a fresh array that
                // the step below copies verbatim instead of converting.
                byte[] decodedData = arrData;
                int decodedOffset = arrOffset;
                bool spill = encoding == 1 && arrOffset != 0;
                if (deferred) {
                    if (srcType != dstType) spill = true;
                } else if (srcType != dstType || uc.LocalBigEndian != uc.FileBigEndian) {
                    spill = true;
                }
                if (spill) {
                    decodedData = new byte[decodedDataSize];
                    decodedOffset = 0;
                }
                if (deferred) {
                    // C: ufbx.c:9110-9119 -- with a memory input the task points straight at the
                    // buffered region and the stream is skipped; otherwise the encoded bytes are
                    // copied out first. The port always copies, since a `byte[]` cannot alias the
                    // middle of the read buffer.
                    byte[] encoded = new byte[encodedSize];
                    if (stream.Input == null && stream.YieldSize + stream.Remaining >= (int)encodedSize) {
                        Array.Copy(stream.Buffer, stream.Position, encoded, 0, (int)encodedSize);
                        stream.SkipBytes(encodedSize);
                    } else {
                        stream.ReadTo(encoded, 0, unchecked((int)encodedSize));
                    }
                    UfbxInflateRetain retain = uc.GetInflateRetain();
                    UfbxiInflate.InflateInitRetain(retain);

                    // C: ufbxi_deflate_task t; t->... = ...; task->data = t;
                    //    ufbxi_thread_pool_run_task(&uc->thread_pool, task) (ufbx.c:9096-9129).
                    // Nothing inflates yet: whoever owns the context decides when that happens,
                    // normally from `run_fn` via `ufbx_thread_pool_run_task()`.
                    deflateTask.Data = new UfbxiDeflateTask {
                        EncodedSize = unchecked((int)encodedSize),
                        SrcElemSize = srcElemSize,
                        ArraySize = unchecked((int)size),
                        SrcType = srcType,
                        DstType = dstType,
                        ArrType = arr.Type,
                        EncodedData = encoded,
                        DecodedData = decodedData,
                        DstData = arrData,
                        DstOffset = arrOffset,
                        InflateRetain = retain,
                    };
                    uc.ThreadPool.RunTask(deflateTask);
                } else if (encoding == 0) {
                    // Plain binary data.
                    UfbxiFail.CheckNoDesc(encodedSize == decodedDataSize, "encoded_size == decoded_data_size");

                    // If the array sits in the current read buffer and we have to convert
                    // anyway, use that buffer as the decoded source instead of copying.
                    bool decodedIsTmp = decodedData != arrData || decodedOffset != arrOffset;
                    if (stream.YieldSize + stream.Remaining >= encodedSize && decodedIsTmp) {
                        if (encodedSize > (uint)stream.YieldSize) {
                            stream.Remaining += stream.YieldSize;
                            stream.YieldSize = unchecked((int)encodedSize);
                            stream.Remaining -= stream.YieldSize;
                        }

                        decodedData = stream.Buffer;
                        decodedOffset = stream.Position;
                        stream.ConsumeBytes(unchecked((int)encodedSize));
                    } else {
                        stream.ReadTo(decodedData, decodedOffset, unchecked((int)encodedSize));
                    }
                } else if (encoding == 1) {
                    // DEFLATE. `ufbx_inflate` writes from index 0 of its destination, which
                    // `decodedOffset == 0` guarantees: a padded array always takes the temporary.
                    stream.PauseProgress();
                    // Port-only guard: `UfbxInflate` (like `ufbx_inflate`) writes from index 0 of
                    // the destination, so the padded array must have taken the temporary above.
                    // C has no such check (it passes an interior pointer).
                    UfbxiFail.CheckNoDesc(decodedOffset == 0, "decoded_data written from index 0");

                    UfbxInflateInput input = new UfbxInflateInput();
                    input.TotalSize = unchecked((int)encodedSize);
                    input.NoHeader = false;
                    input.NoChecksum = false;
                    input.InternalFastBits = 0;

                    if (stream.ProgressCb != null && stream.ProgressCb.Fn != null) {
                        input.ProgressCb = stream.ProgressCb;
                        input.ProgressSizeBefore = arrBegin;
                        input.ProgressSizeAfter = stream.ProgressBytesTotal - arrEnd;
                        input.ProgressIntervalHint = stream.ProgressInterval;
                    } else {
                        input.ProgressCb = new UfbxProgressCb();
                        input.ProgressSizeBefore = 0;
                        input.ProgressSizeAfter = 0;
                        input.ProgressIntervalHint = 0;
                    }

                    // The bytes the stream has buffered start at `Position`; `ufbx_inflate` wants
                    // a buffer indexed from 0, so hand it a copy of the region it may read from.
                    // When the array is larger than that region inflate continues through the
                    // input stream (`read_fn`), and it may clobber `read_buffer` freely, since
                    // every byte in it is consumed.
                    int available = stream.Remaining;
                    if (encodedSize > (uint)available) {
                        int prefix = available < 0 ? 0 : available;
                        byte[] encoded = new byte[prefix];
                        Array.Copy(stream.Buffer, stream.Position, encoded, 0, prefix);
                        input.Data = encoded;
                        input.DataSize = prefix;
                        input.Buffer = stream.ReadBuffer;
                        input.BufferSize = stream.ReadBufferSize;
                        if (stream.Input != null) {
                            UfbxInputStream inputStream = stream.Input;
                            input.ReadFn = (user, data2, size2) => ((UfbxInputStream)user).Read(data2, 0, size2);
                            input.ReadUser = inputStream;
                        }
                        stream.DataOffset += encodedSize - (ulong)prefix;
                        stream.Position += prefix;
                        stream.Remaining = 0;
                    } else {
                        byte[] encoded = new byte[encodedSize];
                        Array.Copy(stream.Buffer, stream.Position, encoded, 0, (int)encodedSize);
                        input.Data = encoded;
                        input.DataSize = (int)encodedSize;
                        stream.Position += (int)encodedSize;
                        stream.Remaining -= (int)encodedSize;
                        UfbxiFail.CheckNoDesc(stream.ResumeProgress(), "ufbxi_resume_progress(uc)");
                    }

                    int res = UfbxiInflate.UfbxInflate(decodedData, decodedDataSize, input, uc.GetInflateRetain());
                    UfbxiFail.CheckMsg(res != -28, "Cancelled");
                    UfbxiFail.CheckMsg(res == decodedDataSize, "Bad DEFLATE data");
                } else {
                    UfbxiFail.FailNoDesc("Bad array encoding");
                }

                // Convert the decoded array if necessary. C skips this for a deferred array:
                // `ufbxi_deflate_task_fn` converts (and postprocesses) as part of the task.
                if (!deferred && (decodedData != arrData || decodedOffset != arrOffset)) {
                    if (srcType == dstType && uc.LocalBigEndian == uc.FileBigEndian) {
                        // Port-only spill (see above): C decoded at `arr_data`, so the bytes move
                        // verbatim rather than through the element converter.
                        Array.Copy(decodedData, decodedOffset, arrData, arrOffset, decodedDataSize);
                    } else {
                        UfbxiFail.CheckNoDesc(UfbxiBinaryArray.BinaryConvertArray(uc, srcType, dstType,
                                decodedData, decodedOffset, arrData, arrOffset, unchecked((int)size)),
                            "ufbxi_binary_convert_array(uc, src_type, dst_type, decoded_data, arr_data, size)");
                    }
                }

                arr.Data = arrData;
                arr.Offset = arrOffset;
                arr.Size = unchecked((int)size);
            } else if (c == '0' || c == '-') {
                // Ignore the array. C points `arr->data` at a shared non-NULL dummy buffer; the
                // port uses an empty array, and `size == 0` keeps every blob read in range.
                arr.Type = c == '-' ? '-' : dstType;
                arr.Data = new byte[0];
                arr.Offset = 0;
                arr.Size = 0;
            } else {
                // Pre-7000: one property per element, `num_values` of them.
                if (UfbxiBinaryArray.IsStringArrayType(arrInfo.Type)) {
                    string[] strings;
                    int stringsOffset;
                    UfbxiFail.CheckNoDesc(PushStringArrayData(arrInfo, unchecked((int)numValues), out strings, out stringsOffset),
                        "ufbxi_push_array_data(uc, &arr_info, num_values, tmp_buf)");
                    UfbxiFail.CheckNoDesc(UfbxiBinaryArray.BinaryParseMultivalueStringArray(uc, dstType, strings, stringsOffset,
                            unchecked((int)numValues)),
                        "ufbxi_binary_parse_multivalue_array(uc, dst_type, arr_data, num_values, tmp_buf)");
                    arr.Strings = strings;
                    arr.Offset = stringsOffset;
                    arr.Size = unchecked((int)numValues);
                } else {
                    byte[] arrData;
                    int arrOffset;
                    UfbxiFail.CheckNoDesc(UfbxiBinaryArray.PushArrayData(uc, arrInfo, unchecked((int)numValues), out arrData, out arrOffset),
                        "ufbxi_push_array_data(uc, &arr_info, num_values, tmp_buf)");
                    UfbxiFail.CheckNoDesc(UfbxiBinaryArray.BinaryParseMultivalueArray(uc, dstType, arrData, arrOffset,
                            unchecked((int)numValues)),
                        "ufbxi_binary_parse_multivalue_array(uc, dst_type, arr_data, num_values, tmp_buf)");
                    arr.Data = arrData;
                    arr.Offset = arrOffset;
                    arr.Size = unchecked((int)numValues);
                }
            }

            // Post-process boolean arrays.
            if (!deferred && arrInfo.Type == 'b') {
                UfbxiBinaryArray.PostprocessBoolArray(arr.Data, arr.Offset, arr.Size);
            }
        }

        // C: `UFBXI_MIN_THREADED_DEFLATE_BYTES` (ufbx.c:60; UFBX_REGRESSION/UFBX_EXTENSIVE_THREADING
        // redefine it to 2 at ufbx.c:1015). Kept for the threading predicate.
        const uint MinThreadedDeflateBytes = 256u;

        // C: struct ufbxi_deflate_task (ufbx.c:8899-8910). The port keeps the destination as an
        // array plus an offset, since there is no way to spell C's padded interior pointer.
        sealed class UfbxiDeflateTask
        {
            public int EncodedSize;
            public int SrcElemSize;
            public int ArraySize;
            public char SrcType;
            public char DstType;
            public char ArrType;
            public byte[] EncodedData;
            public byte[] DecodedData;
            public byte[] DstData;
            public int DstOffset;
            public UfbxInflateRetain InflateRetain;
        }

        // C: ufbxi_deflate_task_fn() (ufbx.c:8912-8956). Failures go through `task->error`, which
        // `ufbxi_thread_pool_update_finished()` turns into `error.description` (ufbx.c:6043-6046)
        // and `ufbxi_thread_pool_wait_imp()` into a failure of the whole load. Running it inline
        // used to be the only behaviour the port had; it now happens only when the ring declines
        // the task, where C's own inline inflate takes over.
        static bool DeflateTaskFn(UfbxiTask task)
        {
            return DeflateTaskImp((UfbxiDeflateTask)task.Data, out task.Error);
        }

        static bool DeflateTaskImp(UfbxiDeflateTask t, out string error)
        {
            error = null;
            int decodedDataSize = t.SrcElemSize * t.ArraySize;

            UfbxInflateInput input = new UfbxInflateInput();
            input.TotalSize = t.EncodedSize;
            input.Data = t.EncodedData;
            input.DataSize = t.EncodedData.Length;
            input.NoHeader = false;
            input.NoChecksum = false;
            input.InternalFastBits = 0;
            input.ProgressCb = new UfbxProgressCb();
            input.ProgressSizeBefore = 0;
            input.ProgressSizeAfter = 0;
            input.ProgressIntervalHint = 0;
            input.Buffer = null;
            input.BufferSize = 0;
            input.ReadFn = null;
            input.ReadUser = null;

            // `UfbxInflate` writes from index 0 of the array it is handed; when the destination is
            // the padded array buffer the caller already spilled into a separate `DecodedData`,
            // so `UfbxInflate` runs there and the step below copies it into place.
            byte[] inflated = t.DecodedData;
            if (t.DstOffset != 0 && t.SrcType == t.DstType) inflated = new byte[decodedDataSize];

            int res = UfbxiInflate.UfbxInflate(inflated, decodedDataSize, input, t.InflateRetain);
            // C: ufbxi_deflate_task_fn() (ufbx.c:8935-8940).
            if (res == -28) { error = "Cancelled"; return false; }
            if (res != decodedDataSize) { error = "Bad DEFLATE data"; return false; }

            if (inflated != t.DstData) {
                if (t.SrcType == t.DstType) {
                    Array.Copy(inflated, 0, t.DstData, t.DstOffset, decodedDataSize);
                } else {
                    if (!UfbxiBinaryArray.BinaryConvertArray(null, t.SrcType, t.DstType,
                            inflated, 0, t.DstData, t.DstOffset, t.ArraySize)) {
                        error = "Failed to convert array";
                        return false;
                    }
                }
            }

            if (t.ArrType == 'b') {
                UfbxiBinaryArray.PostprocessBoolArray(t.DstData, t.DstOffset, t.ArraySize);
            }
            return true;
        }

        // C: ufbxi_push_array_data() (ufbx.c:8867-8888) for the string element types, where the
        // buffer holds `ufbx_string` items instead of raw bytes, so `offset` is an *element*
        // index. No `ufbxi_is_array_node()` branch combines a string type with PAD_BEGIN, so the
        // padding path is dead in practice; it is kept so the stride math matches C's
        // `array_type_size('s') == sizeof(ufbx_string)`.
        static bool PushStringArrayData(UfbxiArrayInfo info, int size, out string[] data, out int offset)
        {
            data = null;
            offset = 0;
            // C's `size` is a `size_t` and the caller's count is a `uint32_t`, so read it
            // unsigned: a count above int.MaxValue must fail the managed allocation (plain
            // `ufbxi_check(arr_data)`) instead of wrapping negative.
            long count = unchecked((uint)size);
            if ((info.Flags & UfbxiArrayFlags.PadBegin) != 0) {
                if (count > int.MaxValue - 4) return false;
                count += 4;
                offset = 4;
            }
            if (count > int.MaxValue) return false;
            data = new string[count];
            return true;
        }

        // C: the non-array branch of ufbxi_binary_parse_node (ufbx.c:9253-9351): up to
        // UFBXI_MAX_NON_ARRAY_VALUES plainly typed property values.
        static void ParseNodeValues(UfbxiContext uc, UfbxiNode node, UfbxiParseState parentState, string name, uint numValues)
        {
            UfbxiStream stream = uc.Stream;

            if (numValues > UfbxiNode.MaxNonArrayValues) numValues = UfbxiNode.MaxNonArrayValues;
            UfbxiValue[] vals = new UfbxiValue[numValues];
            node.Vals = vals;

            uint typeMask = 0;
            for (int i = 0; i < (int)numValues; i++) {
                int data = stream.PeekBytes(13);
                byte[] window = stream.Buffer;
                UfbxiFail.CheckNoDesc(window != null, "ufbxi_peek_bytes(uc, 13)");

                char type = (char)window[data];
                byte[] value = window;
                int valueOffset = data + 1;
                if (uc.FileBigEndian) {
                    value = UfbxiBinaryArray.SwapEndianValue(uc, value, valueOffset, type, out valueOffset);
                    UfbxiFail.CheckNoDesc(value != null, "ufbxi_swap_endian_value(uc, value, type)");
                }

                switch (type) {

                // C reads the byte through `value[0]` of a `const char*` after a `(uint8_t)`
                // cast: zero-extended to the number.
                case 'C':
                case 'B':
                case 'Z':
                    typeMask |= (uint)UfbxiValueType.Number << (i * 2);
                    vals[i].F = (double)(vals[i].I = (long)UfbxiBinaryArray.LE.U8(value, valueOffset));
                    stream.ConsumeBytes(2);
                    break;

                case 'Y':
                    typeMask |= (uint)UfbxiValueType.Number << (i * 2);
                    vals[i].F = (double)(vals[i].I = UfbxiBinaryArray.LE.I16(value, valueOffset));
                    stream.ConsumeBytes(3);
                    break;

                case 'I':
                    typeMask |= (uint)UfbxiValueType.Number << (i * 2);
                    vals[i].F = (double)(vals[i].I = UfbxiBinaryArray.LE.I32(value, valueOffset));
                    stream.ConsumeBytes(5);
                    break;

                case 'L':
                    typeMask |= (uint)UfbxiValueType.Number << (i * 2);
                    vals[i].I = UfbxiBinaryArray.LE.I64(value, valueOffset);
                    vals[i].F = (double)vals[i].I;
                    stream.ConsumeBytes(9);
                    break;

                case 'F':
                    typeMask |= (uint)UfbxiValueType.Number << (i * 2);
                    // C: vals[i].i = ufbxi_f64_to_i64(vals[i].f = ufbxi_read_f32(value))
                    vals[i].F = UfbxiBinaryArray.LE.F32(value, valueOffset);
                    vals[i].I = UfbxiBinaryArray.F64ToInt64(vals[i].F);
                    stream.ConsumeBytes(5);
                    break;

                case 'D':
                    typeMask |= (uint)UfbxiValueType.Number << (i * 2);
                    vals[i].F = UfbxiBinaryArray.LE.F64(value, valueOffset);
                    vals[i].I = UfbxiBinaryArray.F64ToInt64(vals[i].F);
                    stream.ConsumeBytes(9);
                    break;

                case 'S':
                case 'R': {
                    uint length = UfbxiBinaryArray.LE.U32(value, valueOffset);
                    stream.ConsumeBytes(5);
                    // Port-only guard: the port cannot index a read longer than int.MaxValue.
                    // C reaches `ufbxi_read_bytes(uc, length)` -> `ufbxi_refill(length, true)`,
                    // whose FIRST check is the plain `!uc->eof` (ufbx.c:6705). For a buffered
                    // file/memory input the whole file is already read and `uc->eof` is set, so
                    // that check fails with no description; only a stream still mid-file reaches
                    // the later msg site `data_size >= size` ("Truncated file", ufbx.c:6759).
                    if (length > int.MaxValue) {
                        UfbxiFail.CheckNoDesc(!stream.Eof, "!uc->eof");
                        UfbxiFail.FailMsg("data_size >= size", "Truncated file");
                    }
                    int strPos = stream.ReadBytes(unchecked((int)length));
                    string str = UfbxiRawStr.FromBytes(stream.Buffer, strPos, unchecked((int)length));
                    UfbxiFail.CheckNoDesc(str != null, "ufbxi_read_bytes(uc, length)");

                    if (length == 0) {
                        // C: both pointers are `ufbxi_empty_char` with length 0, i.e. a
                        // *present* empty string (so 'S' reads succeed).
                        vals[i].S.RawData = string.Empty;
                        vals[i].S.Utf8Data = string.Empty;
                        vals[i].S.RawLength = 0;
                        vals[i].S.Utf8Length = 0;
                    } else {
                        bool nonAscii;
                        uint hash = UfbxiHash.HashStringCheckAscii(str, 0, unchecked((int)length), out nonAscii);
                        bool raw = UfbxiParseStateMachine.IsRawString(uc, parentState, name, i);
                        UfbxiFail.CheckNoDesc(uc.StringPool.PushSanitizedString(ref vals[i].S, str, 0,
                                unchecked((int)length), hash, nonAscii, raw),
                            "ufbxi_push_sanitized_string(&uc->string_pool, &vals[i].s, str, length, hash, non_ascii, raw)");
                    }

                    typeMask |= (uint)UfbxiValueType.String << (i * 2);
                    break;
                }

                // Arrays in a non-array node are treated as no values at all and skipped.
                case 'c':
                case 'b':
                case 'i':
                case 'l':
                case 'f':
                case 'd': {
                    uint encodedSize = UfbxiBinaryArray.LE.U32(value, valueOffset + 8);
                    stream.ConsumeBytes(13);
                    stream.SkipBytes(encodedSize);
                    break;
                }

                default:
                    UfbxiFail.FailNoDesc("Bad value type"); break;

                }
            }

            node.ValueTypeMask = unchecked((uint)(ushort)typeMask);
        }

        // ------------------------------------------------------------------
        // DOM retention
        // ------------------------------------------------------------------

        // C: ufbxi_get_dom_node_imp() (ufbx.c:10699-10706)
        internal static UfbxDomNode GetDomNodeImp(UfbxiContext uc, UfbxiNode node)
        {
            return uc.FindDomNode(node);
        }

        // C: ufbxi_get_dom_node() (ufbx.c:10708-10712)
        internal static UfbxDomNode GetDomNode(UfbxiContext uc, UfbxiNode node)
        {
            if (!uc.Opts.RetainDom) return null;
            return GetDomNodeImp(uc, node);
        }

        // C: ufbxi_retain_dom_node(uc, node, p_dom_node) (ufbx.c:10715-10807). The port returns
        // the node instead of writing through an out pointer (C's `NULL` argument just discards
        // it, and the return value is the `int` success flag).
        internal static UfbxDomNode RetainDomNode(UfbxiContext uc, UfbxiNode node)
        {
            UfbxDomNode dst = new UfbxDomNode();
            uc.PushTmpDomNode(dst);

            // C: dst->name = node->name (raw), then re-interned sanitized into the pool. The
            // pool may shorten the string, which is why the DOM name can differ from
            // `ufbxi_node.name`.
            dst.Name = node.Name;

            uc.SetDomNodeMapping(node, dst);

            string domName = dst.Name;
            UfbxiFail.CheckNoDesc(uc.StringPool.PushStringPlaceStr(ref domName, false),
                "ufbxi_push_string_place_str(&uc->string_pool, &dst->name, false)");
            dst.Name = domName;

            if (node.ValueTypeMask == (uint)UfbxiValueType.Array) {
                UfbxiValueArray arr = node.Array;
                UfbxDomValue val = new UfbxDomValue();

                int elemSize = UfbxiArrayType.SizeOf(arr.Type);
                val.ValueStr = string.Empty;                     // C: ufbxi_empty_char, length 0
                val.ValueInt = arr.Size;
                val.ValueFloat = (double)val.ValueInt;
                val.ValueBlob = DomArrayBlob(arr, elemSize);

                switch (arr.Type) {
                case 'c': val.Type = UfbxDomValueType.Blob; break;
                case 'b': val.Type = UfbxDomValueType.Blob; break;
                case 'i': val.Type = UfbxDomValueType.ArrayI32; break;
                case 'l': val.Type = UfbxDomValueType.ArrayI64; break;
                case 'f': val.Type = UfbxDomValueType.ArrayF32; break;
                case 'd': val.Type = UfbxDomValueType.ArrayF64; break;
                case 's': val.Type = UfbxDomValueType.ArrayBlob; break;
                case 'C': val.Type = UfbxDomValueType.ArrayBlob; break;
                case '-': val.Type = UfbxDomValueType.ArrayIgnored; break;
                default: UfbxiFail.FailNoDesc("Bad array type"); break;
                }

                dst.Values = new UfbxDomValue[] { val };
            } else {
                int count = 0;
                UfbxDomValue[] vals = new UfbxDomValue[UfbxiNode.MaxNonArrayValues];
                for (int ix = 0; ix < UfbxiNode.MaxNonArrayValues; ix++) {
                    uint mask = (node.ValueTypeMask >> (2 * ix)) & 0x3;
                    if (mask == 0) break;

                    UfbxDomValue val = new UfbxDomValue();
                    val.ValueStr = string.Empty;                 // C: ufbxi_empty_char, length 0

                    if (mask == (uint)UfbxiValueType.String) {
                        val.Type = UfbxDomValueType.String;
                        // C: ufbxi_ignore(...) on both reads -- 'S' fails for raw strings,
                        // leaving value_str at {empty_char, 0}.
                        string str;
                        if (node.GetValS(ix, out str)) val.ValueStr = str;
                        byte[] blob;
                        if (node.GetValBlob(ix, out blob)) val.ValueBlob = blob;
                    } else {
                        // Port-only guard: C asserts `mask == UFBXI_VALUE_NUMBER` here
                        // (ufbx.c:10784, a no-op/abort assert, not an error site); `mask` can
                        // only be String or Number at this point, and String is handled above.
                        UfbxiFail.CheckNoDesc(mask == (uint)UfbxiValueType.Number, "mask == UFBXI_VALUE_NUMBER");
                        val.Type = UfbxDomValueType.Number;
                        val.ValueInt = node.Vals[ix].I;
                        val.ValueFloat = node.Vals[ix].F;
                    }

                    vals[count++] = val;
                }

                // C: dst->values.count = ix; push_pop(&uc->result, &uc->tmp_stack, ..., ix)
                dst.Values = new UfbxDomValue[count];
                Array.Copy(vals, 0, dst.Values, 0, count);
            }

            if (node.NumChildren > 0) {
                for (int i = 0; i < node.NumChildren; i++) {
                    RetainDomNode(uc, node.Children[i]);
                }
                // C: ufbxi_push_pop(&uc->result, &uc->tmp_dom_nodes, ufbx_dom_node*, num_children)
                dst.Children = uc.PopTmpDomNodes((int)node.NumChildren);
            }

            return dst;
        }

        // The `value_blob` of an array DOM value: C aliases `arr->data` for `arr->size *
        // array_type_size(arr->type)` bytes. String arrays are `ufbx_blob[]` pointer records in
        // C, which a `byte[]` cannot hold, so they are packed as
        // `[8-byte LE size][payload bytes]` per element (PORTING_NOTES.md loader 口径 G2).
        static byte[] DomArrayBlob(UfbxiValueArray arr, int elemSize)
        {
            if (arr.Strings != null) {
                int total = 0;
                for (int i = 0; i < arr.Size; i++) {
                    string s = arr.Strings[arr.Offset + i];
                    total += 8 + (s != null ? s.Length : 0);
                }
                byte[] packed = new byte[total];
                int o = 0;
                for (int i = 0; i < arr.Size; i++) {
                    string s = arr.Strings[arr.Offset + i];
                    long size = s != null ? s.Length : 0;
                    for (int k = 0; k < 8; k++) packed[o + k] = (byte)((ulong)size >> (8 * k));
                    o += 8;
                    if (s != null) {
                        for (int k = 0; k < s.Length; k++) packed[o + k] = unchecked((byte)s[k]);
                        o += s.Length;
                    }
                }
                return packed;
            }

            byte[] blob = new byte[arr.Size * elemSize];
            if (blob.Length > 0) {
                Array.Copy(arr.Data, arr.Offset, blob, 0, blob.Length);
            }
            return blob;
        }

        // C: ufbxi_retain_toplevel(uc, node) (ufbx.c:10809-10840). Called with a node for each
        // top-level node, then with `null` to finish; the final call returns `dom_root`.
        internal static UfbxDomNode RetainToplevel(UfbxiContext uc, UfbxiNode node)
        {
            if (uc.DomParseNumChildren > 0) {
                uc.DomParseToplevel.Children = uc.PopTmpDomNodes(uc.DomParseNumChildren);
                uc.DomParseNumChildren = 0;
            }

            if (node != null) {
                uc.DomParseToplevel = RetainDomNode(uc, node);
                return null;
            }

            uc.DomParseToplevel = null;

            // C: collect the remaining top-level nodes into the synthetic `dom_root`.
            UfbxDomNode[] nodes = uc.PopTmpDomNodes(uc.TmpDomNodeCount);
            UfbxDomNode domRoot = new UfbxDomNode();
            domRoot.Name = string.Empty;      // C: ufbxi_empty_char, length 0
            domRoot.Children = nodes;
            return domRoot;
        }

        // C: ufbxi_retain_toplevel_child(uc, child) (ufbx.c:10842-10849)
        internal static void RetainToplevelChild(UfbxiContext uc, UfbxiNode child)
        {
            // Port-only guard: C asserts `uc->dom_parse_toplevel` (ufbx.c:10844, a no-op/abort
            // assert, not an error site).
            UfbxiFail.CheckNoDesc(uc.DomParseToplevel != null, "uc->dom_parse_toplevel");
            RetainDomNode(uc, child);
            uc.DomParseNumChildren++;
        }
    }
}
