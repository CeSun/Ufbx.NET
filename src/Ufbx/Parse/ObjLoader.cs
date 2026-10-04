// OBJ/MTL loader, ported from ufbx v0.23.1 ufbx.c:16766-18063 ("-- .obj file").
//
// Covered C functions (each carries its ufbx.c line range in a comment):
//   ufbxi_obj_pop_props (16776)  ufbxi_obj_push_mesh (16806)  ufbxi_obj_flush_mesh (16844)
//   ufbxi_obj_init (16861)       ufbxi_obj_free (16901)       ufbxi_obj_read_line (16924)
//   ufbxi_obj_span_token (16982) ufbxi_obj_tokenize (16998)   ufbxi_obj_tokenize_line (17066)
//   ufbxi_obj_parse_vertex (17073) ufbxi_obj_parse_index (17109) ufbxi_obj_parse_indices (17167)
//   ufbxi_obj_parse_multi_indices (17297) ufbxi_parse_hex (17305) ufbxi_obj_parse_comment (17325)
//   ufbxi_obj_parse_material (17366) ufbxi_obj_pop_vertices (17411) ufbxi_obj_setup_attrib (17433)
//   ufbxi_obj_pad_colors (17482) ufbxi_obj_pop_meshes (17497)  ufbxi_obj_parse_file (17680)
//   ufbxi_obj_flush_material (17762) ufbxi_obj_parse_prop (17776) ufbxi_obj_parse_mtl_map (17851)
//   ufbxi_obj_parse_mtl (17903)  ufbxi_obj_load_mtl (17935)    ufbxi_obj_load (18028)
//   ufbxi_mtl_load (18038)
//
// REPRESENTATION (PORTING_NOTES.md #4, "DOM/数组数据约定"):
//  - C drives the reader straight off the raw byte window (`uc->data`/`data_size`/`yield_size`
//    /`eof`). The port keeps the identical state in `UfbxiStream.Buffer/BeginIndex/Position/
//    Remaining/YieldSize/Eof`; consuming a line advances `Position`/`Remaining` exactly like
//    `uc->data += n; uc->data_size -= n`. The `uc->data_size += uc->yield_size` of
//    `ufbxi_obj_init` (ufbx.c:16881) is `UfbxiStream.PauseProgress()` — OBJ never reads
//    `yield_size`, so folding it in and zeroing it is behaviourally identical.
//  - The current line is a raw-byte `string`; tokens are (offset, length) pairs into it, which
//    reproduces C's `const char *data` arithmetic (including the inter-token whitespace a
//    `ufbxi_obj_span_token` covers) without aliasing the read buffer.
//  - Every `tmp_*` buffer is a `List<T>`: `ufbxi_push` is `Add`, `ubfxi_pop(n)` a tail
//    `RemoveRange` (the arena addresses/order they carry are not observable).
//
// OUT OF SCOPE SEAM: `ufbxi_obj_pop_meshes` calls `ufbxi_finalize_mesh` (ufbx.c:16690), which
// lives in the scene-build agent's range (16535-16775) and is not ported. It is stubbed with
// `UfbxiReaderNotPortedException` (see `FinalizeMeshNotPorted`); the face-group code that C
// runs *after* finalize is still written out so the wiring is complete once it lands.
using System;
using System.Collections.Generic;

namespace Ufbx
{
    internal static class UfbxiObj
    {
        // C: ufbxi_obj_attrib_stride (ufbx.c:16770-16772).
        static readonly byte[] AttribStride = { 3, 2, 3, 4 };

        // C: ufbxi_obj_attrib (ufbx.c:6346-6351).
        const uint AttribPosition = 0;
        const uint AttribUv = 1;
        const uint AttribNormal = 2;
        const uint AttribColor = 3;

        // C: UFBXI_OBJ_NUM_ATTRIBS (ufbx.c:6353).
        const uint NumAttribs = 3;

        // C: SIZE_MAX, used as "rest of the line" for `ufbxi_obj_span_token`.
        const int SizeMax = int.MaxValue;

        // C: the `ufbxi_obj_cmd1/2/3` command-key macros (ufbx.c:17407-17409).
        static uint Cmd1(char a) { return (uint)a << 24; }
        static uint Cmd2(char a, char b) { return (uint)a << 24 | (uint)b << 16; }

        // C: ufbx_real_list (a `ufbx_real*` + count). `Data` carries C's 4 leading zero reals
        // (`data[0..3] = 0; data += 4`), so the values are `Data[4 .. 4+Count)`.
        internal struct UfbxiObjRealList
        {
            public double[] Data;
            public int Count;
        }

        // ==================================================================
        // Entry points (suggested Load.cs wiring targets)
        // ==================================================================

        // C: ufbxi_obj_load (ufbx.c:18028-18036).
        internal static void ObjLoad(UfbxiContext uc)
        {
            ObjInit(uc);
            ObjParseFile(uc);
            UfbxiLegacy.InitFilePaths(uc);
            ObjLoadMtl(uc);
        }

        // C: ufbxi_mtl_load (ufbx.c:18038-18045).
        internal static void MtlLoad(UfbxiContext uc)
        {
            ObjInit(uc);
            UfbxiLegacy.InitFilePaths(uc);
            ObjParseMtl(uc);
        }

        // ==================================================================
        // ufbxi_obj_pop_props (ufbx.c:16776-16804)
        // ==================================================================

        // Moves the last `count` items of `uc->obj.tmp_props` into `dst->props` and normalizes
        // each property exactly like C.
        static void ObjPopProps(UfbxiContext uc, UfbxProps dst, int count)
        {
            UfbxProp[] props = new UfbxProp[count];
            int start = uc.Obj.TmpProps.Count - count;
            for (int i = 0; i < count; i++) props[i] = uc.Obj.TmpProps[start + i];
            uc.Obj.TmpProps.RemoveRange(start, count);
            UfbxiFail.CheckNoDesc(props != null, "props.data");

            for (int i = 0; i < count; i++) {
                UfbxProp prop = props[i];
                prop.InternalKey = UfbxiProperties.GetNameKey(prop.Name, prop.Name.Length);
                if (prop.ValueStr == null || prop.ValueStr.Length == 0) {
                    prop.ValueStr = string.Empty;
                }
                if (prop.ValueInt == 0) {
                    prop.ValueInt = UfbxiBinaryArray.F64ToInt64(prop.ValueReal0);
                }
                if ((prop.ValueBlob == null || prop.ValueBlob.Length == 0) && prop.ValueStr.Length > 0) {
                    prop.ValueBlob = UfbxiRawStr.ToBytes(prop.ValueStr);
                }
                props[i] = prop;
            }

            if (count > 1) {
                UfbxiProperties.SortProperties(props);
                UfbxProps wrap = new UfbxProps();
                wrap.Props = props;
                UfbxiProperties.DeduplicateProperties(wrap);
                props = wrap.Props;
            }

            dst.Props = props;
        }

        // ==================================================================
        // ufbxi_obj_push_mesh (ufbx.c:16806-16842)
        // ==================================================================

        static void ObjPushMesh(UfbxiContext uc)
        {
            UfbxiObjState obj = uc.Obj;
            UfbxiObjMesh mesh = new UfbxiObjMesh();
            obj.TmpMeshes.Add(mesh);
            obj.Mesh = mesh;

            for (int i = 0; i < NumAttribs; i++) {
                mesh.VertexRange[i].MinIx = ulong.MaxValue;
            }

            string name = "";
            if (uc.Opts.ObjSplitGroups && obj.Group.Length > 0) {
                name = obj.Group;
            } else if (!uc.Opts.ObjMergeObjects && obj.Object.Length > 0) {
                name = obj.Object;
            } else if (!uc.Opts.ObjMergeGroups && obj.Group.Length > 0) {
                name = obj.Group;
            }

            mesh.FbxNode = UfbxiReadElement.PushSyntheticElement<UfbxNode>(uc, out mesh.FbxNodeId,
                null, name, UfbxElementType.Node);
            mesh.FbxMesh = UfbxiReadElement.PushSyntheticElement<UfbxMesh>(uc, out mesh.FbxMeshId,
                null, name, UfbxElementType.Mesh);
            UfbxiFail.CheckNoDesc(mesh.FbxNode != null && mesh.FbxMesh != null, "mesh->fbx_node && mesh->fbx_mesh");

            UfbxVertexVec3 position = mesh.FbxMesh.VertexPosition;
            position.UniquePerVertex = true;
            mesh.FbxMesh.VertexPosition = position;

            uc.TmpNodeIds.Add(mesh.FbxNode.ElementId);

            obj.FaceMaterial = UfbxConstants.NoIndex;
            obj.FaceGroup = 0;
            obj.FaceGroupDirty = true;
            obj.MaterialDirty = true;

            UfbxiReadElement.ConnectOo(uc, mesh.FbxMeshId, mesh.FbxNodeId);
            UfbxiReadElement.ConnectOo(uc, mesh.FbxNodeId, 0);
        }

        // ==================================================================
        // ufbxi_obj_flush_mesh (ufbx.c:16844-16859)
        // ==================================================================

        static void ObjFlushMesh(UfbxiContext uc)
        {
            if (uc.Obj.Mesh == null) return;

            int numProps = uc.Obj.TmpProps.Count;
            ObjPopProps(uc, uc.Obj.Mesh.FbxMesh.Props, numProps);

            UfbxFaceGroup[] groups = uc.Obj.TmpFaceGroupInfos.ToArray();
            uc.Obj.TmpFaceGroupInfos.Clear();
            UfbxiFail.CheckNoDesc(groups != null, "groups");

            uc.Obj.Mesh.FbxMesh.FaceGroups = groups;
        }

        // ==================================================================
        // ufbxi_obj_init (ufbx.c:16861-16899) / ufbxi_obj_free (ufbx.c:16901-16922)
        // ==================================================================

        static void ObjInit(UfbxiContext uc)
        {
            // PORT: C creates the `fbx_id_map` (used by ufbxi_insert_fbx_id below) in
            // `ufbxi_load()` (ufbx.c:25557-25562) for every format; the port created the S1
            // maps lazily in `ufbxi_read_root`, so OBJ/MTL must do it explicitly here.
            uc.S1InitMaps();

            uc.FromAscii = true;
            uc.Obj.Initialized = true;

            // C assigns `tmp_*.ator` here (ufbx.c:16867-16878): allocator bookkeeping only, no
            // port-side observable (PORTING_NOTES.md #4).

            // .obj parsing does its own yield logic:
            // C: uc->data_size += uc->yield_size.
            uc.Stream.PauseProgress();

            uc.Obj.Object = string.Empty;
            uc.Obj.Group = string.Empty;

            // C: ufbxi_map_init(&uc->obj.group_map, &uc->ator_tmp, ufbxi_map_cmp_const_char_ptr, NULL).
            // The port comparator keys on the pooled string's pointer id; the item size is C's
            // sizeof(ufbxi_obj_group_entry) = 16 (allocation bookkeeping only).
            uc.Obj.GroupMap = new UfbxiMap<UfbxiObjGroupEntry, ulong>(
                UfbxiMapCmps.ConstCharPtr<UfbxiObjGroupEntry>(e => UfbxiPtrIdTable.IdOf(e.Name)), 16, uc.Error);

            // Add a nameless root node with the root ID
            UfbxiElementInfo rootInfo = new UfbxiElementInfo();
            rootInfo.FbxId = uc.RootId;
            rootInfo.Name = string.Empty;
            UfbxNode root = UfbxiReadElement.PushElement<UfbxNode>(uc, rootInfo, UfbxElementType.Node);
            UfbxiFail.CheckNoDesc(root != null, "root");
            UfbxiRoot.SetupRootNode(uc, root);
            uc.TmpNodeIds.Add(root.ElementId);
        }

        // C: ufbxi_obj_free (ufbx.c:16901-16922). The port has no arenas to free; kept as the
        // counterpart of C so the call sequence is obvious if it is ever wired to FreeTemp.
        static void ObjFree(UfbxiContext uc)
        {
            if (!uc.Obj.Initialized) return;
        }

        // ==================================================================
        // ufbxi_obj_read_line (ufbx.c:16924-16980)
        // ==================================================================

        // Reads one line (terminated by '\n') into `uc.Obj.Line` and consumes it from the
        // stream. C's memchr over `uc->data_size - offset` maps to a scan of `Buffer` from
        // `Position + offset`.
        static void ObjReadLine(UfbxiContext uc)
        {
            UfbxiStream stream = uc.Stream;

            int offset = 0;
            for (;;) {
                int begin = stream.Position + offset;
                int remaining = stream.Remaining - offset;
                int end = -1;
                if (stream.Buffer != null) {
                    for (int i = 0; i < remaining; i++) {
                        if (stream.Buffer[begin + i] == (byte)'\n') { end = begin + i; break; }
                    }
                }

                if (end < 0) {
                    if (stream.Eof) {
                        offset = stream.Remaining;
                        uc.Obj.Eof = true;
                        break;
                    } else {
                        long newCapL = Math.Max(1L, (long)stream.Remaining * 2);
                        int newCap = newCapL > int.MaxValue ? int.MaxValue : (int)newCapL;
                        stream.Refill(newCap, false);
                        continue;
                    }
                }

                offset += (end - begin) + 1;

                // Handle line continuations
                int esc = end;
                if (esc > begin && stream.Buffer[esc - 1] == (byte)'\r') esc--;
                if (esc > begin && stream.Buffer[esc - 1] == (byte)'\\') {
                    continue;
                }

                break;
            }

            int lineLen = offset;

            uc.Obj.Line = UfbxiRawStr.FromBytes(stream.Buffer, stream.Position, lineLen);
            stream.Position += lineLen;
            stream.Remaining -= lineLen;

            uc.Obj.ReadProgress += (ulong)lineLen;
            if (uc.Obj.ReadProgress >= uc.ProgressInterval) {
                UfbxiFail.CheckNoDesc(stream.ReportProgress(), "ufbxi_report_progress(uc)");
                uc.Obj.ReadProgress %= uc.ProgressInterval;
            }

            if (uc.Obj.Eof) {
                // C: copy to `uc->tmp` and append '\n', length++ (the EOF line has no newline).
                uc.Obj.Line = uc.Obj.Line + "\n";
            }
        }

        // ==================================================================
        // ufbxi_obj_span_token (ufbx.c:16982-16996)
        // ==================================================================

        // The span from `startToken` through `endToken`, inclusive, as a token over the line
        // (offset from start, length through end). C returns a `ufbx_string` over the same
        // bytes; the port keeps the offset form so `Match`/`parse_double` can still slice it.
        static UfbxiObjToken ObjSpanToken(UfbxiContext uc, int startToken, int endToken)
        {
            if (endToken > uc.Obj.NumTokens - 1) endToken = uc.Obj.NumTokens - 1;

            UfbxiObjToken start = uc.Obj.Tokens[startToken];
            UfbxiObjToken end = uc.Obj.Tokens[endToken];
            int numBetween = end.Offset - start.Offset;

            UfbxiObjToken result;
            result.Offset = start.Offset;
            result.Length = numBetween + end.Length;
            return result;
        }

        // ==================================================================
        // ufbxi_obj_tokenize (ufbx.c:16998-17064) / ufbxi_obj_tokenize_line (17066-17071)
        // ==================================================================

        // C reads bytes past a token's end (the delimiter); `LineAt` yields the same byte when
        // in range and 0 otherwise, mirroring the port's `UfbxiAscii.At` convention.
        static char LineAt(string line, int index)
        {
            return (uint)index < (uint)line.Length ? line[index] : '\0';
        }

        static string TokenString(UfbxiContext uc, UfbxiObjToken tok)
        {
            return uc.Obj.Line.Substring(tok.Offset, tok.Length);
        }

        static byte[] TokenBytes(UfbxiContext uc, UfbxiObjToken tok)
        {
            return UfbxiRawStr.ToBytes(uc.Obj.Line, tok.Offset, tok.Length);
        }

        static void ObjTokenize(UfbxiContext uc)
        {
            string line = uc.Obj.Line;
            uc.Obj.NumTokens = 0;
            uc.Obj.Tokens.Clear();

            int ptr = 0;
            int end = line.Length;

            for (;;) {
                char c;

                // Skip whitespace
                for (;;) {
                    c = LineAt(line, ptr);
                    if (c == ' ' || c == '\t' || c == '\r') {
                        ptr++;
                        continue;
                    }

                    // Treat line continuations as whitespace
                    if (c == '\\') {
                        int p = ptr + 1;
                        if (LineAt(line, p) == '\r') p++;
                        if (LineAt(line, p) == '\n' && p < end - 1) {
                            ptr = p + 1;
                            continue;
                        }
                    }

                    break;
                }

                c = LineAt(line, ptr);
                if (c == '\n') break;
                if (c == '#' && uc.Obj.NumTokens > 0) break;

                int index = uc.Obj.NumTokens++;
                // C grows `uc->obj.tokens`/`tokens_cap` (ufbxi_grow_array); the List grows itself.
                if (index < uc.Obj.TokensCap) { /* reused slot */ }

                int tokStart = ptr;

                // Treat comment start as a single token
                if (c == '#') {
                    ptr++;
                    uc.Obj.Tokens.Add(new UfbxiObjToken { Offset = tokStart, Length = 1 });
                    continue;
                }

                for (;;) {
                    c = LineAt(line, ++ptr);

                    if (UfbxiAscii.IsSpace(c)) {
                        break;
                    }

                    if (c == '\\') {
                        int p = ptr + 1;
                        if (LineAt(line, p) == '\r') p++;
                        if (LineAt(line, p) == '\n' && p < end - 1) {
                            break;
                        }
                    }
                }

                uc.Obj.Tokens.Add(new UfbxiObjToken { Offset = tokStart, Length = ptr - tokStart });
            }
        }

        static void ObjTokenizeLine(UfbxiContext uc)
        {
            ObjReadLine(uc);
            ObjTokenize(uc);
        }

        // ==================================================================
        // ufbxi_obj_parse_vertex (ufbx.c:17073-17107)
        // ==================================================================

        static void ObjParseVertex(UfbxiContext uc, uint attrib, int offset)
        {
            if (uc.Opts.IgnoreGeometry) return;

            List<double> dst = uc.Obj.TmpVertices[attrib];
            int numValues = AttribStride[attrib];
            uc.Obj.VertexCount[attrib]++;

            int readValues = numValues;
            if (attrib == AttribColor) {
                if ((ulong)(offset + readValues) > (ulong)uc.Obj.NumTokens) {
                    readValues = 3;
                }
            }
            UfbxiFail.CheckNoDesc((ulong)(offset + readValues) <= (ulong)uc.Obj.NumTokens,
                "offset + read_values <= uc->obj.num_tokens");

            uint parseFlags = uc.DoubleParseFlags;
            for (int i = 0; i < numValues; i++) dst.Add(0.0);
            int baseIndex = dst.Count - numValues;

            for (int i = 0; i < readValues; i++) {
                UfbxiObjToken tok = uc.Obj.Tokens[offset + i];
                byte[] bytes = TokenBytes(uc, tok);
                double val = UfbxiNumeric.ParseDouble(new ReadOnlySpan<byte>(bytes, 0, bytes.Length), out int end, parseFlags);
                UfbxiFail.CheckNoDesc(end == tok.Length, "end == str.data + str.length");
                dst[baseIndex + i] = val;
            }

            if (readValues < numValues) {
                // C: ufbx_assert(read_values + 1 == num_values); ufbx_assert(attrib == COLOR);
                dst[baseIndex + readValues] = 1.0;
            }
        }

        // ==================================================================
        // ufbxi_obj_parse_index (ufbx.c:17109-17165)
        // ==================================================================

        static void ObjParseIndex(UfbxiContext uc, ref UfbxiObjToken s, uint attrib)
        {
            string line = uc.Obj.Line;
            int ptr = s.Offset;
            int end = ptr + s.Length;

            bool negative = false;
            if (LineAt(line, ptr) == '-') {
                negative = true;
                ptr++;
            }

            // As .obj indices are never zero we can detect missing indices
            // by simply not writing to it.
            ulong index = 0;
            for (; ptr != end; ptr++) {
                char c = LineAt(line, ptr);
                if (c >= '0' && c <= '9') {
                    UfbxiFail.CheckNoDesc(index < ulong.MaxValue / 10 - 10, "index < UINT64_MAX / 10 - 10");
                    index = index * 10 + (ulong)(c - '0');
                } else if (c == '/') {
                    ptr++;
                    break;
                }
            }

            if (negative) {
                ulong count = uc.Obj.VertexCount[attrib];
                index = index <= count ? count - index : ulong.MaxValue;
            } else {
                // Corrects to zero based indices and wraps 0 to UINT64_MAX (missing)
                index -= 1;
            }

            UfbxiObjFastIndices fastIndices = uc.Obj.FastIndices[attrib];
            if (fastIndices.NumLeft == 0) {
                // C: ufbxi_push(&uc->obj.tmp_indices[attrib], uint64_t, 128) and points the
                // fast cursor at it; the port appends to the List, so only the 128-slot
                // bookkeeping is kept (see UfbxiObjFastIndices).
                fastIndices.NumLeft = 128;
            }
            uc.Obj.TmpIndices[attrib].Add(index);
            fastIndices.NumLeft--;

            UfbxiObjMesh mesh = uc.Obj.Mesh;

            if (index != ulong.MaxValue) {
                UfbxiObjIndexRange range = mesh.VertexRange[attrib];
                range.MinIx = Math.Min(range.MinIx, index);
                range.MaxIx = Math.Max(range.MaxIx, index);
            }

            s.Offset = ptr;
            s.Length = end - ptr;
        }

        // ==================================================================
        // ufbxi_obj_parse_indices (ufbx.c:17167-17295)
        // ==================================================================

        static void ObjParseIndices(UfbxiContext uc, int tokenBegin, int numTokens)
        {
            UfbxiObjState obj = uc.Obj;

            bool flushMesh = false;
            if (obj.ObjectDirty) {
                if (!uc.Opts.ObjMergeObjects) {
                    flushMesh = true;
                }
                obj.ObjectDirty = false;
            }

            if (obj.GroupDirty) {
                if (((obj.Object.Length == 0 || uc.Opts.ObjMergeObjects) && !uc.Opts.ObjMergeGroups) || uc.Opts.ObjSplitGroups) {
                    flushMesh = true;
                }
                obj.GroupDirty = false;
                obj.FaceGroupDirty = true;
            }

            if (obj.Mesh == null || flushMesh) {
                ObjFlushMesh(uc);
                ObjPushMesh(uc);
            }
            UfbxiObjMesh mesh = obj.Mesh;

            if (obj.MaterialDirty) {
                if (obj.UsemtlFbxId != 0) {
                    int entryIndex = UfbxiFbxId.FindFbxId(uc, obj.UsemtlFbxId);
                    // C: ufbx_assert(entry)
                    if (entryIndex < 0) UfbxiFail.FailNoDesc("entry");
                    UfbxiFbxIdEntry entry = uc.FbxIdMap.Items[entryIndex];
                    if (mesh.UsemtlBase == 0 || entry.UserId < mesh.UsemtlBase) {
                        UfbxiReadElement.ConnectOo(uc, obj.UsemtlFbxId, mesh.FbxNodeId);

                        uint index = ++obj.UsemtlIndex;
                        UfbxiFail.CheckNoDesc(index < uint.MaxValue, "index < UINT32_MAX");
                        entry.UserId = index;
                        uc.FbxIdMap.Items[entryIndex] = entry;

                        if (mesh.UsemtlBase == 0) {
                            mesh.UsemtlBase = index;
                        }
                        obj.FaceMaterial = index - mesh.UsemtlBase;
                    }
                    obj.FaceMaterial = entry.UserId - mesh.UsemtlBase;
                } else {
                    obj.FaceMaterial = UfbxConstants.NoIndex;
                }
            }

            // EARLY RETURN: Rest of the function should only be related to geometry!
            if (uc.Opts.IgnoreGeometry) return;

            if (numTokens == 0 && !uc.Opts.AllowEmptyFaces) {
                UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.EmptyFaceRemoved,
                    UfbxiWarnings.NoElementId, "Empty face has been removed");
                return;
            }

            if (obj.FaceGroupDirty) {
                string name = string.Empty;
                if (obj.Group.Length > 0 && (obj.Object.Length > 0 || uc.Opts.ObjMergeGroups) && !uc.Opts.ObjSplitGroups) {
                    name = obj.Group;
                }

                uint hash = UfbxiPtrIdTable.HashOf(name);
                int entryIndex = obj.GroupMap.Find(hash, UfbxiPtrIdTable.IdOf(name));
                if (entryIndex < 0) {
                    entryIndex = obj.GroupMap.Insert(hash, UfbxiPtrIdTable.IdOf(name));
                    UfbxiFail.CheckNoDesc(entryIndex >= 0, "entry");
                    UfbxiObjGroupEntry fresh = new UfbxiObjGroupEntry();
                    fresh.Name = name;
                    fresh.MeshId = 0;
                    fresh.LocalId = 0;
                    obj.GroupMap.Items[entryIndex] = fresh;
                }
                UfbxiObjGroupEntry entry = obj.GroupMap.Items[entryIndex];

                uint meshId = mesh.FbxMesh.ElementId;
                if (entry.MeshId != meshId) {
                    uint id = mesh.NumGroups++;
                    entry.MeshId = meshId;
                    entry.LocalId = id;
                    obj.GroupMap.Items[entryIndex] = entry;

                    UfbxFaceGroup group = new UfbxFaceGroup();
                    group.Id = 0;
                    group.Name = name;
                    obj.TmpFaceGroupInfos.Add(group);
                }

                obj.FaceGroup = entry.LocalId;

                if (!obj.HasFaceGroup) {
                    obj.HasFaceGroup = true;
                    for (int i = 0; i < obj.TmpFaces.Count; i++) obj.TmpFaceGroup.Add(0);
                }

                obj.FaceGroupDirty = false;
            }

            int numIndices = numTokens;
            UfbxiFail.CheckNoDesc((ulong)(uint.MaxValue - (uint)mesh.NumIndices) >= (ulong)numIndices,
                "UINT32_MAX - mesh->num_indices >= num_indices");

            UfbxFace face = new UfbxFace();
            face.IndexBegin = (uint)mesh.NumIndices;
            face.NumIndices = (uint)numIndices;
            obj.TmpFaces.Add(face);

            mesh.NumFaces++;
            mesh.NumIndices += numIndices;

            obj.TmpFaceMaterial.Add(obj.FaceMaterial);

            if (obj.HasFaceSmoothing) {
                obj.TmpFaceSmoothing.Add(obj.FaceSmoothing);
            }

            if (obj.HasFaceGroup) {
                obj.TmpFaceGroup.Add(obj.FaceGroup);
            }

            for (int ix = 0; ix < numIndices; ix++) {
                UfbxiObjToken tok = obj.Tokens[tokenBegin + ix];
                for (uint attrib = 0; attrib < NumAttribs; attrib++) {
                    ObjParseIndex(uc, ref tok, attrib);
                }
            }
        }

        // ==================================================================
        // ufbxi_obj_parse_multi_indices (ufbx.c:17297-17303)
        // ==================================================================

        static void ObjParseMultiIndices(UfbxiContext uc, int window)
        {
            for (int begin = 1; begin + window <= uc.Obj.NumTokens; begin++) {
                ObjParseIndices(uc, begin, window);
            }
        }

        // ==================================================================
        // ufbxi_parse_hex (ufbx.c:17305-17323)
        // ==================================================================

        static uint ParseHex(string digits, int offset, int length)
        {
            uint value = 0;

            for (int i = 0; i < length; i++) {
                char c = digits[offset + i];
                uint v = 0;
                if (c >= '0' && c <= '9') {
                    v = (uint)(c - '0');
                } else if (c >= 'A' && c <= 'F') {
                    v = (uint)(c - 'A') + 10;
                } else if (c >= 'a' && c <= 'f') {
                    v = (uint)(c - 'a') + 10;
                }
                value = (value << 4) | v;
            }

            return value;
        }

        // ==================================================================
        // ufbxi_obj_parse_comment (ufbx.c:17325-17364)
        // ==================================================================

        static void ObjParseComment(UfbxiContext uc)
        {
            UfbxiObjState obj = uc.Obj;

            if (obj.NumTokens >= 3 && UfbxiStr.Equal(TokenString(uc, obj.Tokens[1]), "MRGB")) {
                ulong numColor = obj.VertexCount[AttribColor];

                // Pop standard vertex colors and replace them with MRGB colors
                if (numColor > obj.MrgbVertexCount) {
                    int numPop = (int)(numColor - obj.MrgbVertexCount);
                    RemoveTail(obj.TmpColorValid, numPop);
                    RemoveTail(obj.TmpVertices[AttribColor], numPop * 4);
                    obj.VertexCount[AttribColor] -= (ulong)numPop;
                }

                UfbxiObjToken mrgb = obj.Tokens[2];
                for (int i = 0; (ulong)(i + 8) <= (ulong)mrgb.Length; i += 8) {
                    uint hex = ParseHex(obj.Line, mrgb.Offset + i, 8);
                    obj.TmpVertices[AttribColor].Add((double)((hex >> 16) & 0xff) / 255.0);
                    obj.TmpVertices[AttribColor].Add((double)((hex >> 8) & 0xff) / 255.0);
                    obj.TmpVertices[AttribColor].Add((double)((hex >> 0) & 0xff) / 255.0);
                    obj.TmpVertices[AttribColor].Add((double)((hex >> 24) & 0xff) / 255.0);
                    obj.TmpColorValid.Add(true);
                }

                obj.HasVertexColor = true;
            }

            if (!uc.Opts.DisableQuirks) {
                if (UfbxiLoad.Match(obj.Line, 0, obj.Line.Length, "\\s*#\\s*File exported by ZBrush.*")) {
                    if (obj.Mesh == null) {
                        uc.Opts.ObjMergeGroups = true;
                    }
                }
            }
        }

        // ==================================================================
        // ufbxi_obj_parse_material (ufbx.c:17366-17405)
        // ==================================================================

        static void ObjParseMaterial(UfbxiContext uc)
        {
            UfbxiObjState obj = uc.Obj;
            obj.MaterialDirty = true;

            // Allow empty `usemtl` lines to specify "no material".
            if (obj.NumTokens < 2) {
                obj.UsemtlFbxId = 0;
                return;
            }

            string name = TokenString(uc, ObjSpanToken(uc, 1, SizeMax));

            uc.StringPool.PushStringPlaceStr(ref name, false);

            ulong fbxId = UfbxiFbxId.SyntheticIdFromString(uc, name);
            UfbxiFail.CheckNoDesc(fbxId != 0, "fbx_id");

            int entryIndex = UfbxiFbxId.FindFbxId(uc, fbxId);

            obj.UsemtlFbxId = fbxId;

            if (entryIndex < 0) {
                UfbxiElementInfo info = new UfbxiElementInfo();
                info.FbxId = fbxId;
                info.Name = name;

                UfbxMaterial material = UfbxiReadElement.PushElement<UfbxMaterial>(uc, info, UfbxElementType.Material);
                UfbxiFail.CheckNoDesc(material != null, "material");

                material.ShaderType = UfbxShaderType.WavefrontMtl;
                material.ShadingModelName = string.Empty;
                material.ShaderPropPrefix = string.Empty;

                uint id = material.ElementId;
                // C: ufbxi_grow_array(&uc->ator_tmp, &uc->obj.tmp_materials, &uc->obj.tmp_materials_cap, id + 1).
                while (obj.TmpMaterials.Count <= id) obj.TmpMaterials.Add(null);
                obj.TmpMaterials[(int)id] = material;
                if (obj.TmpMaterialsCap < obj.TmpMaterials.Count) obj.TmpMaterialsCap = obj.TmpMaterials.Count;
            }
        }

        // ==================================================================
        // ufbxi_obj_pop_vertices (ufbx.c:17411-17431)
        // ==================================================================

        // Pops the last `count` reals of `tmp_vertices[attrib]` (from `min_index` on), preceded
        // by C's four zero reals (`data[0..3] = 0; data += 4`).
        static void ObjPopVertices(UfbxiContext uc, ref UfbxiObjRealList dst, uint attrib, ulong minIndex)
        {
            List<double> list = uc.Obj.TmpVertices[attrib];
            int stride = AttribStride[attrib];
            UfbxiFail.CheckNoDesc(minIndex < (ulong)(list.Count / stride),
                "min_index < uc->obj.tmp_vertices[attrib].num_items / stride");

            int count = list.Count - (int)((ulong)minIndex * (ulong)stride);
            double[] data = new double[count + 4];
            data[0] = 0.0;
            data[1] = 0.0;
            data[2] = 0.0;
            data[3] = 0.0;

            int start = list.Count - count;
            for (int i = 0; i < count; i++) data[4 + i] = list[start + i];
            list.RemoveRange(start, count);

            dst.Data = data;
            dst.Count = count;
        }

        // ==================================================================
        // ufbxi_obj_setup_attrib (ufbx.c:17433-17480)
        // ==================================================================

        struct UfbxiObjSetupAttrib
        {
            public bool Set;
            public int NumValues;
            public double[] Data;
            public uint[] Indices;
        }

        static UfbxiObjSetupAttrib ObjSetupAttrib(UfbxiContext uc, UfbxiObjMesh mesh, ulong[] tmpIndices,
            UfbxiObjRealList data, uint attrib, bool nonDisjoint, bool required)
        {
            UfbxiObjState obj = uc.Obj;
            int numIndices = mesh.NumIndices;
            int stride = AttribStride[attrib];
            int numValues = data.Count / stride;

            ulong meshMinIx = mesh.VertexRange[attrib].MinIx;
            if (numIndices == 0 || numValues == 0 || meshMinIx == ulong.MaxValue) {
                UfbxiFail.CheckNoDesc(numIndices == 0 || !required, "num_indices == 0 || !required");

                // Pop indices without copying if the attribute is not used
                RemoveTail(obj.TmpIndices[attrib], numIndices);
                return default(UfbxiObjSetupAttrib);
            }

            ulong minIndex = nonDisjoint ? 0 : meshMinIx;

            {
                List<ulong> source = obj.TmpIndices[attrib];
                int start = source.Count - numIndices;
                for (int i = 0; i < numIndices; i++) tmpIndices[i] = source[start + i];
                source.RemoveRange(start, numIndices);
            }

            uint[] dstIndices = new uint[numIndices];
            UfbxiFail.CheckNoDesc(dstIndices != null, "dst_indices");

            for (int i = 0; i < numIndices; i++) {
                ulong ix = tmpIndices[i];
                if (ix != ulong.MaxValue) {
                    ix -= minIndex;
                    UfbxiFail.CheckNoDesc(ix < uint.MaxValue, "ix < UINT32_MAX");
                }
                if (ix < (ulong)numValues) {
                    dstIndices[i] = (uint)ix;
                } else {
                    UfbxiReadElement.FixIndex(uc, dstIndices, i, (uint)ix, (ulong)numValues);
                }
            }

            UfbxiObjSetupAttrib result;
            result.Set = true;
            result.NumValues = numValues;
            result.Data = data.Data;
            result.Indices = dstIndices;
            return result;
        }

        // ==================================================================
        // ufbxi_obj_pad_colors (ufbx.c:17482-17495)
        // ==================================================================

        static void ObjPadColors(UfbxiContext uc, ulong numVertices)
        {
            if (uc.Opts.IgnoreGeometry) return;

            ulong numColors = uc.Obj.VertexCount[AttribColor];
            if (numVertices > numColors) {
                int numPad = (int)(numVertices - numColors);
                for (int i = 0; i < numPad * 4; i++) uc.Obj.TmpVertices[AttribColor].Add(0.0);
                for (int i = 0; i < numPad; i++) uc.Obj.TmpColorValid.Add(false);
                uc.Obj.VertexCount[AttribColor] += (ulong)numPad;
            }
        }

        // ==================================================================
        // ufbxi_obj_pop_meshes (ufbx.c:17497-17678)
        // ==================================================================

        static void ObjPopMeshes(UfbxiContext uc)
        {
            UfbxiObjState obj = uc.Obj;

            int numMeshes = obj.TmpMeshes.Count;
            UfbxiObjMesh[] meshes = obj.TmpMeshes.ToArray();
            obj.TmpMeshes.Clear();
            UfbxiFail.CheckNoDesc(meshes != null, "meshes");

            if (obj.HasVertexColor) {
                ObjPadColors(uc, obj.VertexCount[AttribPosition]);
            }

            // Pop unused fast indices
            for (uint i = 0; i < NumAttribs; i++) {
                // C: ufbxi_pop(&uc->obj.tmp_indices[i], uint64_t, fast_indices[i].num_left, NULL).
                // The port never materializes the unused 128-slot tail (see UfbxiObjFastIndices),
                // so there is nothing to pop here.
            }

            // Check if the file has disjoint vertices
            bool[] nonDisjoint = new bool[NumAttribs];
            ulong[] nextMin = new ulong[NumAttribs];
            UfbxiObjRealList[] vertices = new UfbxiObjRealList[4];
            bool[] colorValid = null;

            int maxIndices = 0;

            for (int i = 0; i < numMeshes; i++) {
                UfbxiObjMesh mesh = meshes[i];
                maxIndices = Math.Max(maxIndices, mesh.NumIndices);
                for (uint attrib = 0; attrib < NumAttribs; attrib++) {
                    UfbxiObjIndexRange range = mesh.VertexRange[attrib];
                    if (range.MinIx > range.MaxIx) continue;
                    if (range.MinIx < nextMin[attrib]) {
                        nonDisjoint[attrib] = true;
                    }
                    nextMin[attrib] = range.MaxIx + 1;
                }
            }

            ulong[] tmpIndices = new ulong[maxIndices];
            UfbxiFail.CheckNoDesc(tmpIndices != null, "tmp_indices");

            for (uint attrib = 0; attrib < NumAttribs; attrib++) {
                if (!nonDisjoint[attrib]) continue;
                ObjPopVertices(uc, ref vertices[attrib], attrib, 0);
            }
            if (obj.HasVertexColor && nonDisjoint[AttribPosition]) {
                ObjPopVertices(uc, ref vertices[AttribColor], AttribColor, 0);
                colorValid = PopBools(obj.TmpColorValid, vertices[AttribColor].Count / 4);
                UfbxiFail.CheckNoDesc(colorValid != null, "color_valid");
            }

            for (int i = numMeshes; i > 0; i--) {
                UfbxiObjMesh mesh = meshes[i - 1];

                UfbxMesh fbxMesh = mesh.FbxMesh;

                int numFaces = mesh.NumFaces;

                if (!uc.Opts.IgnoreGeometry) {
                    for (uint attrib = 0; attrib < NumAttribs; attrib++) {
                        if (nonDisjoint[attrib]) continue;
                        ulong minIx = mesh.VertexRange[attrib].MinIx;
                        if (minIx < ulong.MaxValue) {
                            ObjPopVertices(uc, ref vertices[attrib], attrib, minIx);
                        }
                    }
                    if (obj.HasVertexColor && !nonDisjoint[AttribPosition]) {
                        ulong minIx = mesh.VertexRange[AttribPosition].MinIx;
                        UfbxiFail.CheckNoDesc(minIx < ulong.MaxValue, "min_ix < UINT64_MAX");
                        ObjPopVertices(uc, ref vertices[AttribColor], AttribColor, minIx);
                        colorValid = PopBools(obj.TmpColorValid, vertices[AttribColor].Count / 4);
                        UfbxiFail.CheckNoDesc(colorValid != null, "color_valid");
                    }

                    fbxMesh.NumFaces = numFaces;

                    fbxMesh.Faces = PopFaces(obj.TmpFaces, numFaces);
                    fbxMesh.FaceMaterial = PopU32(obj.TmpFaceMaterial, numFaces);

                    UfbxiFail.CheckNoDesc(fbxMesh.Faces != null, "fbx_mesh->faces.data");
                    UfbxiFail.CheckNoDesc(fbxMesh.FaceMaterial != null, "fbx_mesh->face_material.data");

                    if (obj.HasFaceSmoothing) {
                        fbxMesh.FaceSmoothing = PopBools(obj.TmpFaceSmoothing, numFaces);
                        UfbxiFail.CheckNoDesc(fbxMesh.FaceSmoothing != null, "fbx_mesh->face_smoothing.data");
                    }

                    if (obj.HasFaceGroup) {
                        if (mesh.NumGroups > 1) {
                            fbxMesh.FaceGroup = PopU32(obj.TmpFaceGroup, numFaces);
                            UfbxiFail.CheckNoDesc(fbxMesh.FaceGroup != null, "fbx_mesh->face_group.data");
                        } else {
                            RemoveTail(obj.TmpFaceGroup, numFaces);
                        }
                    }

                    {
                        UfbxiObjSetupAttrib r = ObjSetupAttrib(uc, mesh, tmpIndices, vertices[AttribPosition],
                            AttribPosition, nonDisjoint[AttribPosition], true);
                        if (r.Set) {
                            UfbxVertexVec3 position = fbxMesh.VertexPosition;
                            position.Exists = true;
                            position.Values = ToVec3Array(r.Data, r.NumValues);
                            position.Indices = r.Indices;
                            fbxMesh.VertexPosition = position;
                        }
                    }
                    {
                        UfbxiObjSetupAttrib r = ObjSetupAttrib(uc, mesh, tmpIndices, vertices[AttribUv],
                            AttribUv, nonDisjoint[AttribUv], false);
                        if (r.Set) {
                            UfbxVertexVec2 uv = fbxMesh.VertexUv;
                            uv.Exists = true;
                            uv.Values = ToVec2Array(r.Data, r.NumValues);
                            uv.Indices = r.Indices;
                            fbxMesh.VertexUv = uv;
                        }
                    }
                    {
                        UfbxiObjSetupAttrib r = ObjSetupAttrib(uc, mesh, tmpIndices, vertices[AttribNormal],
                            AttribNormal, nonDisjoint[AttribNormal], false);
                        if (r.Set) {
                            UfbxVertexVec3 normal = fbxMesh.VertexNormal;
                            normal.Exists = true;
                            normal.Values = ToVec3Array(r.Data, r.NumValues);
                            normal.Indices = r.Indices;
                            fbxMesh.VertexNormal = normal;
                        }
                    }

                    if (obj.HasVertexColor) {
                        // C: ufbx_assert(color_valid)
                        UfbxiFail.CheckNoDesc(colorValid != null, "color_valid");
                        bool hasColor = false;
                        bool allValid = true;
                        uint[] positionIndices = fbxMesh.VertexPosition.Indices;
                        int maxIndex = fbxMesh.VertexPosition.Values != null ? fbxMesh.VertexPosition.Values.Length : 0;
                        if (positionIndices != null) {
                            for (int ix = 0; ix < positionIndices.Length; ix++) {
                                uint pIx = positionIndices[ix];
                                if (pIx < (uint)maxIndex) {
                                    if (colorValid[pIx]) {
                                        hasColor = true;
                                    } else {
                                        allValid = false;
                                    }
                                }
                            }
                        }

                        if (hasColor) {
                            UfbxVertexVec4 color = fbxMesh.VertexColor;
                            color.Exists = true;
                            color.Values = ToVec4Array(vertices[AttribColor].Data, vertices[AttribColor].Count / 4);
                            color.Indices = fbxMesh.VertexPosition.Indices;
                            color.UniquePerVertex = true;
                            fbxMesh.VertexColor = color;

                            if (!allValid) {
                                uint[] indices = fbxMesh.VertexColor.Indices;
                                indices = CopyU32(indices, mesh.NumIndices);
                                UfbxiFail.CheckNoDesc(indices != null, "indices");

                                int numValues = fbxMesh.VertexColor.Values != null ? fbxMesh.VertexColor.Values.Length : 0;
                                for (int ix = 0; ix < mesh.NumIndices; ix++) {
                                    uint pIx = indices[ix];
                                    if (pIx >= (uint)numValues || !colorValid[pIx]) {
                                        UfbxiReadElement.FixIndex(uc, indices, ix, pIx, (ulong)numValues);
                                    }
                                }

                                UfbxVertexVec4 fixedColor = fbxMesh.VertexColor;
                                fixedColor.Indices = indices;
                                fbxMesh.VertexColor = fixedColor;
                            }
                        }
                    }
                }

                // ---------------------------------------------------------------
                // C: ufbxi_finalize_mesh(&uc->result, &uc->error, fbx_mesh) (ufbx.c:17643).
                // Ported in Parse/SceneBuild.cs (S3a).
                // ---------------------------------------------------------------
                UfbxiSceneBuild.FinalizeMesh(fbxMesh);

                if (uc.RetainMeshParts) {
                    fbxMesh.FaceGroupParts = new UfbxMeshPart[mesh.NumGroups];
                    UfbxiFail.CheckNoDesc(fbxMesh.FaceGroupParts != null, "fbx_mesh->face_group_parts.data");
                }

                if (mesh.NumGroups > 1) {
                    UfbxiGeometry.UpdateFaceGroups(uc, fbxMesh, false);
                } else if (mesh.NumGroups == 1) {
                    fbxMesh.FaceGroup = UfbxiGeometry.SentinelIndexZero;
                    // NOTE: Consecutive and zero indices are always allocated so we can skip doing it here,
                    // see HACK(consecutiv-faces)..
                    if (fbxMesh.FaceGroupParts != null && fbxMesh.FaceGroupParts.Length > 0) {
                        UfbxMeshPart part = fbxMesh.FaceGroupParts[0];
                        part.NumFaces = fbxMesh.NumFaces;
                        part.NumFaces = numFaces;
                        part.NumEmptyFaces = fbxMesh.NumEmptyFaces;
                        part.NumPointFaces = fbxMesh.NumPointFaces;
                        part.NumLineFaces = fbxMesh.NumLineFaces;
                        part.NumTriangles = fbxMesh.NumTriangles;
                        part.FaceIndices = UfbxiGeometry.SentinelIndexConsecutive;
                        fbxMesh.FaceGroupParts[0] = part;
                    }
                }

                // HACK(consecutive-faces): Prepare for finalize to re-use a consecutive/zero
                // index buffer for face materials..
                uc.MaxZeroIndices = Math.Max(uc.MaxZeroIndices, numFaces);
                uc.MaxConsecutiveIndices = Math.Max(uc.MaxConsecutiveIndices, numFaces);
            }
        }

        // (ufbxi_finalize_mesh is ported in Parse/SceneBuild.cs; the stub below is kept only
        // as documentation of the former seam and is no longer called.)

        // ==================================================================
        // ufbxi_obj_parse_file (ufbx.c:17680-17760)
        // ==================================================================

        static void ObjParseFile(UfbxiContext uc)
        {
            UfbxiObjState obj = uc.Obj;

            while (!obj.Eof) {
                ObjTokenizeLine(uc);
                int numTokens = obj.NumTokens;
                if (numTokens == 0) continue;

                string cmd = TokenString(uc, obj.Tokens[0]);
                uint key = UfbxiProperties.GetNameKey(cmd, cmd.Length);
                if (key == Cmd1('v')) {
                    ObjParseVertex(uc, AttribPosition, 1);
                    if (numTokens >= 7) {
                        ulong numVertices = obj.VertexCount[AttribPosition];
                        obj.HasVertexColor = true;
                        ObjPadColors(uc, numVertices - 1);
                        if (obj.VertexCount[AttribColor] < numVertices) {
                            // C: ufbx_assert(uc->obj.vertex_count[COLOR] == num_vertices - 1)
                            ObjParseVertex(uc, AttribColor, 4);
                            obj.TmpColorValid.Add(true);
                        }
                    }
                } else if (key == Cmd2('v', 't')) {
                    ObjParseVertex(uc, AttribUv, 1);
                } else if (key == Cmd2('v', 'n')) {
                    ObjParseVertex(uc, AttribNormal, 1);
                } else if (key == Cmd1('f')) {
                    ObjParseIndices(uc, 1, obj.NumTokens - 1);
                } else if (key == Cmd1('p')) {
                    ObjParseMultiIndices(uc, 1);
                } else if (key == Cmd1('l')) {
                    ObjParseMultiIndices(uc, 2);
                } else if (key == Cmd1('s')) {
                    if (numTokens >= 2) {
                        obj.HasFaceSmoothing = true;
                        obj.FaceSmoothing = !UfbxiStr.Equal(TokenString(uc, obj.Tokens[1]), "off");

                        // Fill in previously missed face smoothing data
                        if (obj.TmpFaceSmoothing.Count == 0 && obj.TmpFaces.Count > 0) {
                            for (int i = 0; i < obj.TmpFaces.Count; i++) obj.TmpFaceSmoothing.Add(false);
                        }
                    }
                } else if (key == Cmd1('o')) {
                    if (numTokens >= 2) {
                        string name = TokenString(uc, ObjSpanToken(uc, 1, SizeMax));
                        uc.StringPool.PushStringPlaceStr(ref name, false);
                        obj.Object = name;
                        obj.ObjectDirty = true;
                    }
                } else if (key == Cmd1('g')) {
                    if (numTokens >= 2) {
                        string name = TokenString(uc, ObjSpanToken(uc, 1, SizeMax));
                        uc.StringPool.PushStringPlaceStr(ref name, false);
                        obj.Group = name;
                        obj.GroupDirty = true;
                    } else {
                        obj.Group = string.Empty;
                        obj.GroupDirty = true;
                    }
                } else if (key == Cmd1('#')) {
                    ObjParseComment(uc);
                } else if (UfbxiStr.Equal(cmd, "mtllib")) {
                    UfbxiFail.CheckNoDesc(obj.NumTokens >= 2, "uc->obj.num_tokens >= 2");
                    UfbxiObjToken lib = ObjSpanToken(uc, 1, SizeMax);
                    obj.MtllibRelativePath = TokenBytes(uc, lib);
                } else if (UfbxiStr.Equal(cmd, "usemtl")) {
                    ObjParseMaterial(uc);
                } else if (!uc.Opts.DisableQuirks && key == 0) {
                    // ZBrush exporter seems to end the files with '\0', sometimes..
                } else {
                    UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.UnknownObjDirective,
                        UfbxiWarnings.NoElementId, "Unknown .obj directive, skipped line");
                }
            }

            ObjFlushMesh(uc);
            ObjPopMeshes(uc);
        }

        // ==================================================================
        // ufbxi_obj_flush_material (ufbx.c:17762-17774)
        // ==================================================================

        static void ObjFlushMaterial(UfbxiContext uc)
        {
            UfbxiObjState obj = uc.Obj;
            if (obj.UsemtlFbxId == 0) return;

            int entryIndex = UfbxiFbxId.FindFbxId(uc, obj.UsemtlFbxId);
            // C: ufbx_assert(entry)
            if (entryIndex < 0) UfbxiFail.FailNoDesc("entry");
            uint elementId = uc.FbxIdMap.Items[entryIndex].ElementId;
            UfbxMaterial material = obj.TmpMaterials[(int)elementId];

            int numProps = obj.TmpProps.Count;
            ObjPopProps(uc, material.Props, numProps);
        }

        // ==================================================================
        // ufbxi_obj_parse_prop (ufbx.c:17776-17849)
        // ==================================================================

        static void ObjParseProp(UfbxiContext uc, string name, int start, bool includeRest, out int next)
        {
            UfbxiObjState obj = uc.Obj;

            if (start >= obj.NumTokens) {
                next = start;
                return;
            }

            UfbxProp prop = new UfbxProp();
            prop.Name = name;

            uc.StringPool.PushStringPlaceStr(ref prop.Name, false);

            uint flags = (uint)UfbxPropFlags.ValueStr;

            int numReals = 0;
            for (; numReals < 4; numReals++) {
                if (start + numReals >= obj.NumTokens) break;
                UfbxiObjToken tok = obj.Tokens[start + numReals];

                byte[] bytes = TokenBytes(uc, tok);
                double val = UfbxiNumeric.ParseDouble(new ReadOnlySpan<byte>(bytes, 0, bytes.Length), out int end, uc.DoubleParseFlags);
                if (end != tok.Length) break;

                prop.SetRealAt(numReals, val);
                if (numReals == 0) {
                    prop.ValueInt = UfbxiBinaryArray.F64ToInt64(val);
                    flags |= (uint)UfbxPropFlags.ValueInt;
                }
            }

            int numArgs = 0;
            if (!includeRest) {
                for (; start + numArgs < obj.NumTokens - 1; numArgs++) {
                    UfbxiObjToken argTok = obj.Tokens[start + numArgs];
                    if (UfbxiLoad.Match(obj.Line, argTok.Offset, argTok.Length, "-[A-Za-z][\\-A-Za-z0-9_]*")) break;
                }
            }

            if (numArgs > 0 || includeRest) {
                UfbxiObjToken span = ObjSpanToken(uc, start, includeRest ? SizeMax : start + numArgs - 1);
                prop.ValueStr = TokenString(uc, span);
                prop.ValueBlob = TokenBytes(uc, span);

                uc.StringPool.PushStringPlaceStr(ref prop.ValueStr, false);
                int blobLength = prop.ValueBlob != null ? prop.ValueBlob.Length : 0;
                uc.StringPool.PushStringPlaceBlob(ref prop.ValueBlob, ref blobLength, true);
            } else {
                prop.ValueStr = string.Empty;
            }

            if (numReals > 0) {
                flags = (uint)UfbxPropFlags.ValueReal << (numReals - 1);
            } else {
                if (UfbxiProperties.Strcmp(prop.ValueStr, "on") == 0) {
                    prop.ValueInt = 1;
                    prop.ValueReal0 = 1.0;
                    flags |= (uint)UfbxPropFlags.ValueInt;
                } else if (UfbxiProperties.Strcmp(prop.ValueStr, "off") == 0) {
                    prop.ValueInt = 0;
                    prop.ValueReal0 = 0.0;
                    flags |= (uint)UfbxPropFlags.ValueInt;
                }
            }

            prop.Flags = (UfbxPropFlags)flags;

            obj.TmpProps.Add(prop);

            next = start + numArgs;
        }

        // ==================================================================
        // ufbxi_obj_parse_mtl_map (ufbx.c:17851-17901)
        // ==================================================================

        static void ObjParseMtlMap(UfbxiContext uc, int prefixLen)
        {
            UfbxiObjState obj = uc.Obj;
            if (obj.NumTokens < 2) return;

            int numProps = 1;
            ObjParseProp(uc, "obj|args", 1, true, out _);

            int start = 1;
            for (; start + 1 < obj.NumTokens; ) {
                UfbxiObjToken tok = obj.Tokens[start];
                if (UfbxiLoad.Match(obj.Line, tok.Offset, tok.Length, "-[A-Za-z][\\-A-Za-z0-9_]*")) {
                    UfbxiObjToken sub = new UfbxiObjToken();
                    sub.Offset = tok.Offset + 1;
                    sub.Length = tok.Length - 1;
                    string subName = TokenString(uc, sub);
                    ObjParseProp(uc, subName, start + 1, false, out start);
                    numProps++;
                } else {
                    break;
                }
            }

            UfbxiObjToken texTok = ObjSpanToken(uc, start, SizeMax);
            string texStr = TokenString(uc, texTok);
            byte[] texRaw = TokenBytes(uc, texTok);

            uc.StringPool.PushStringPlaceStr(ref texStr, false);
            int texRawLength = texRaw.Length;
            uc.StringPool.PushStringPlaceBlob(ref texRaw, ref texRawLength, true);

            ulong fbxId;
            UfbxTexture texture = UfbxiReadElement.PushSyntheticElement<UfbxTexture>(uc, out fbxId,
                null, "", UfbxElementType.Texture);
            UfbxiFail.CheckNoDesc(texture != null, "texture");

            texture.Filename = string.Empty;
            texture.AbsoluteFilename = string.Empty;
            texture.UvSet = string.Empty;

            texture.RelativeFilename = texStr;
            texture.RawRelativeFilename = texRaw;

            ObjPopProps(uc, texture.Props, numProps);

            string prop = TokenString(uc, obj.Tokens[0]);
            // C: ufbx_assert(prop.length >= prefix_len)
            prop = prop.Substring(prefixLen);
            uc.StringPool.PushStringPlaceStr(ref prop, false);

            if (obj.UsemtlFbxId != 0) {
                UfbxiReadElement.ConnectOp(uc, fbxId, obj.UsemtlFbxId, prop);
            }
        }

        // ==================================================================
        // ufbxi_obj_parse_mtl (ufbx.c:17903-17933)
        // ==================================================================

        static void ObjParseMtl(UfbxiContext uc)
        {
            UfbxiObjState obj = uc.Obj;
            obj.Mesh = null;
            obj.UsemtlFbxId = 0;

            while (!obj.Eof) {
                ObjTokenizeLine(uc);
                int numTokens = obj.NumTokens;
                if (numTokens == 0) continue;

                string cmd = TokenString(uc, obj.Tokens[0]);
                if (UfbxiStr.Equal(cmd, "newmtl")) {
                    // HACK: Reuse mesh material parsing, but don't allow for empty material name
                    UfbxiFail.CheckNoDesc(obj.NumTokens >= 2, "uc->obj.num_tokens >= 2");
                    ObjFlushMaterial(uc);
                    ObjParseMaterial(uc);
                } else if (cmd.Length > 4 && cmd.Substring(0, 4) == "map_") {
                    ObjParseMtlMap(uc, 4);
                } else if (cmd.Length == 4 && (cmd == "bump" || cmd == "disp" || cmd == "norm")) {
                    ObjParseMtlMap(uc, 0);
                } else if (cmd.Length == 1 && cmd[0] == '#') {
                    // Implement .mtl magic comment handling here if necessary
                } else {
                    // C: ufbxi_obj_parse_prop(uc, uc->obj.tokens[0], 1, true, NULL)
                    ObjParseProp(uc, cmd, 1, true, out _);
                }
            }

            ObjFlushMaterial(uc);
        }

        // ==================================================================
        // ufbxi_obj_load_mtl (ufbx.c:17935-18026)
        // ==================================================================

        static void ObjLoadMtl(UfbxiContext uc)
        {
            UfbxiStream stream = uc.Stream;

            // HACK: Reset everything and switch to loading the .mtl file globally
            if (stream.Input != null) {
                stream.Input.Close();
            }

            stream.Input = null;
            stream.Buffer = null;
            stream.BeginIndex = 0;
            stream.Position = 0;
            stream.Remaining = 0;
            stream.YieldSize = 0;
            stream.Eof = false;
            uc.Obj.Eof = false;

            if (uc.Opts.ObjMtlData != null && uc.Opts.ObjMtlData.Length > 0) {
                stream.Buffer = uc.Opts.ObjMtlData;
                stream.BeginIndex = 0;
                stream.Position = 0;
                stream.Remaining = uc.Opts.ObjMtlData.Length;
                ObjParseMtl(uc);
                return;
            }

            UfbxiLoad.UfbxiOpenFileStream opened = new UfbxiLoad.UfbxiOpenFileStream();
            bool hasStream = false;
            bool needsStream = false;
            byte[] streamPath = null;
            int streamPathLength = 0;

            if (uc.Opts.OpenFileCb.Fn != null) {
                if (uc.Opts.ObjMtlPath != null && uc.Opts.ObjMtlPath.Length > 0) {
                    hasStream = UfbxiSceneFiles.OpenFile(uc.Opts.OpenFileCb, opened,
                        uc.Opts.ObjMtlPath, uc.Opts.ObjMtlPath.Length, null, 0, UfbxOpenFileType.ObjMtl);
                    streamPath = UfbxiRawStr.ToBytes(uc.Opts.ObjMtlPath);
                    streamPathLength = uc.Opts.ObjMtlPath.Length;
                    needsStream = true;
                    if (!hasStream) {
                        UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.MissingExternalFile, UfbxiWarnings.NoElementId,
                            "Could not open .mtl file: %s", new UfbxiVaList().AddStr(uc.Opts.ObjMtlPath));
                    }
                }

                if (!hasStream && uc.Opts.LoadExternalFiles &&
                    uc.Obj.MtllibRelativePath != null && uc.Obj.MtllibRelativePath.Length > 0) {
                    UfbxiSceneFiles.UfbxiStrblob src = new UfbxiSceneFiles.UfbxiStrblob();
                    src.Blob = uc.Obj.MtllibRelativePath;
                    UfbxiSceneFiles.UfbxiStrblob dst = new UfbxiSceneFiles.UfbxiStrblob();
                    UfbxiSceneFiles.ResolveRelativeFilename(uc, ref dst, src, true);

                    byte[] dstBlob = dst.Blob != null ? dst.Blob : Array.Empty<byte>();
                    string dstStr = UfbxiRawStr.FromBytes(dstBlob, 0, dstBlob.Length);
                    hasStream = UfbxiSceneFiles.OpenFile(uc.Opts.OpenFileCb, opened, dstStr, dstBlob.Length,
                        uc.Obj.MtllibRelativePath, 0, UfbxOpenFileType.ObjMtl);
                    streamPath = uc.Obj.MtllibRelativePath;
                    streamPathLength = streamPath.Length;
                    needsStream = true;
                    if (!hasStream) {
                        UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.MissingExternalFile, UfbxiWarnings.NoElementId,
                            "Could not open .mtl file: %s", new UfbxiVaList().AddStr(dstStr));
                    }
                }

                string path = uc.Scene.Metadata.Filename != null ? uc.Scene.Metadata.Filename : string.Empty;
                if (!hasStream && uc.Opts.LoadExternalFiles && uc.Opts.ObjSearchMtlByFilename && path.Length > 4) {
                    if (UfbxiLoad.Match(path, path.Length - 4, 4, "\\c.obj")) {
                        char[] copy = path.ToCharArray();
                        copy[path.Length - 3] = copy[path.Length - 3] == 'O' ? 'M' : 'm';
                        copy[path.Length - 2] = copy[path.Length - 2] == 'B' ? 'T' : 't';
                        copy[path.Length - 1] = copy[path.Length - 1] == 'J' ? 'L' : 'l';
                        string copyStr = new string(copy);
                        hasStream = UfbxiSceneFiles.OpenFile(uc.Opts.OpenFileCb, opened, copyStr, path.Length,
                            null, 0, UfbxOpenFileType.ObjMtl);
                        if (hasStream) {
                            UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.ImplicitMtl, UfbxiWarnings.NoElementId,
                                "Opened .mtl file derived from .obj filename: %s", new UfbxiVaList().AddStr(copyStr));
                        }
                    }
                }
            }

            if (hasStream) {
                // Adopt `opened` to the read callbacks for the duration of the .mtl parse.
                stream.Input = opened;
                try {
                    ObjParseMtl(uc);
                } finally {
                    opened.Close();
                    stream.Input = null;
                }
            } else if (needsStream && !uc.Opts.IgnoreMissingExternalFiles) {
                UfbxiPrint.SetErrInfo(uc.Error, streamPath, 0, streamPathLength);
                UfbxiFail.FailMsg("ufbxi_obj_load_mtl()", "External file not found");
            }
        }

        // ==================================================================
        // Small list/array helpers
        // ==================================================================

        static void RemoveTail<T>(List<T> list, int count)
        {
            if (count <= 0) return;
            list.RemoveRange(list.Count - count, count);
        }

        static UfbxFace[] PopFaces(List<UfbxFace> list, int count)
        {
            UfbxFace[] result = new UfbxFace[count];
            int start = list.Count - count;
            for (int i = 0; i < count; i++) result[i] = list[start + i];
            list.RemoveRange(start, count);
            return result;
        }

        static uint[] PopU32(List<uint> list, int count)
        {
            uint[] result = new uint[count];
            int start = list.Count - count;
            for (int i = 0; i < count; i++) result[i] = list[start + i];
            list.RemoveRange(start, count);
            return result;
        }

        static bool[] PopBools(List<bool> list, int count)
        {
            bool[] result = new bool[count];
            int start = list.Count - count;
            for (int i = 0; i < count; i++) result[i] = list[start + i];
            list.RemoveRange(start, count);
            return result;
        }

        static uint[] CopyU32(uint[] source, int count)
        {
            uint[] result = new uint[count];
            if (source != null && count > 0) Array.Copy(source, result, count);
            return result;
        }

        // The raw reals of a `ufbxi_obj_setup_attrib` result reinterpreted as the attribute's
        // vector type: C aliases `data.data` (`ufbx_real*`) as `ufbx_vecN*` (ufbx.c:17460).
        static UfbxVec3[] ToVec3Array(double[] data, int numValues)
        {
            UfbxVec3[] values = new UfbxVec3[numValues];
            for (int i = 0; i < numValues; i++) {
                int o = 4 + i * 3;
                values[i] = new UfbxVec3(data[o + 0], data[o + 1], data[o + 2]);
            }
            return values;
        }

        static UfbxVec2[] ToVec2Array(double[] data, int numValues)
        {
            UfbxVec2[] values = new UfbxVec2[numValues];
            for (int i = 0; i < numValues; i++) {
                int o = 4 + i * 2;
                values[i] = new UfbxVec2(data[o + 0], data[o + 1]);
            }
            return values;
        }

        static UfbxVec4[] ToVec4Array(double[] data, int numValues)
        {
            UfbxVec4[] values = new UfbxVec4[numValues];
            for (int i = 0; i < numValues; i++) {
                int o = 4 + i * 4;
                values[i] = new UfbxVec4(data[o + 0], data[o + 1], data[o + 2], data[o + 3]);
            }
            return values;
        }
    }
}
