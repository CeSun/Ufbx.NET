// Internal parse context, ported from `struct ufbxi_context` (ufbx.c:6400-6640).
// C's context also owns the arena/buffer machinery (`ufbxi_buf`, `ufbxi_ator`,
// `tmp_stack`), which per PORTING_NOTES #4 is not ported: buffers become plain
// arrays/`List<T>` at the point of use. This class therefore carries the state the
// parser reads and writes, and grows as later modules are ported.
namespace Ufbx
{
    // NOTE(2026-10-03): `partial` since the S2 wave — each reader module adds its own
    // `uc->tmp_*`/state fields in its own file (`Parse/UfbxiContext.<Module>.cs`,
    // append-only) so parallel porting agents never edit this shared file.
    internal sealed partial class UfbxiContext
    {
        // C: uint32_t version (ufbx.c:6463) — FBX file format version (eg. 7400).
        public uint Version;

        // C: bool from_ascii (ufbx.c:6466)
        public bool FromAscii;

        // C: bool local_big_endian / file_big_endian (ufbx.c:6467-6468)
        // The C# port always runs on a little-endian .NET runtime for the purposes of
        // bit layout, but `file_big_endian` is real input state, so both are kept.
        public bool LocalBigEndian;
        public bool FileBigEndian;

        // C: bool retain_vertex_w (ufbx.c:6590), derived from the options by
        // `ufbxi_init_derived_opts()` before parsing starts.
        public bool RetainVertexW;

        // C: bool blender_full_weights (ufbx.c:6591)
        public bool BlenderFullWeights;

        // C: ufbx_load_opts opts (ufbx.c:6474)
        public UfbxLoadOpts Opts = new UfbxLoadOpts();

        // C: the option-derived context flags set in `ufbxi_load()` (ufbx.c:25292). Called
        // right after `opts` is finalized, before `ufbxi_load_strings()`/`ufbxi_begin_parse()`.
        internal void InitDerivedOpts()
        {
            RetainVertexW = (Opts.RetainDom || Opts.RetainVertexAttribW) && !Opts.IgnoreGeometry;
        }

        // C: ufbxi_context I/O window fields (ufbx.c:6409-6420) are wrapped by UfbxiStream.
        public UfbxiStream Stream;

        // C: size_t progress_interval (ufbx.c:6443, set at ufbx.c:25531-25536). Shared by the
        // binary reader's yield windows and the ASCII reader's `src_yield` stepping, so the
        // ASCII path reads it through `UfbxiStream.ProgressInterval`.
        public ulong ProgressInterval = ulong.MaxValue;

        // C: uint32_t double_parse_flags (ufbx.c:6472, set at ufbx.c:25489 from
        // `ufbxi_parse_double_init_flags()`). The ASCII numeric scanners read it through here.
        public uint DoubleParseFlags = UfbxiAscii.ParseDoubleInitFlags;

        // C: bool sure_fbx (ufbx.c:6465) — set once the binary magic or an ASCII `Name:` token
        // has confirmed this really is an FBX file.
        public bool SureFbx;

        // C: ufbx_exporter exporter (ufbx.c:6461, `UFBX_EXPORTER_UNKNOWN` by default).
        // The ASCII reader sets it to `UFBX_EXPORTER_BLENDER_ASCII` from `SkipWhitespace()`.
        public UfbxExporter Exporter = UfbxExporter.Unknown;

        // C: ufbxi_ascii ascii (ufbx.c:6534) — the tokenizer/state-machine window. Held here so
        // the whole `uc->ascii.*` access in ufbx.c has a single owner; see Parse/AsciiState.cs.
        public UfbxiAscii Ascii;

        // C: ufbxi_buf tmp_stack (ufbx.c:6525) — the shared parse scratch stack. The ASCII
        // array fast paths push their little-endian element bytes here and the DOM node/prop
        // readers pop them, so both layers must share one stack (PORTING_NOTES.md DOM 约定).
        public UfbxiAsciiTmpStack TmpStack = new UfbxiAsciiTmpStack();

        // C: ufbxi_string_pool string_pool (ufbx.c:6528) and ufbxi_warnings warnings
        // (ufbx.c:6530). The ASCII name/array-string tokens are interned into the pool so name
        // comparisons are by pointer identity against `UfbxiStrings` (PORTING_NOTES.md
        // "C 指针序 ≡ 分配序"). Constructed via `InitStringPool`.
        public UfbxiStringPool StringPool;
        public UfbxiWarnings Warnings;

        // C: uc->string_pool setup (ufbx.c:25550-25556) + ufbxi_load_strings (ufbx.c:11407).
        // `initialSize` is 1024 for a scene load. `error` is the `ufbx_error` the pool reports
        // into. Interning the `ufbxi_*` constants is what makes `ReferenceEquals(name,
        // UfbxiStrings.X)` the counterpart of C's `name == ufbxi_X`.
        internal UfbxiStringPool InitStringPool(UfbxError error, uint initialSize)
        {
            if (Warnings == null) {
                Warnings = new UfbxiWarnings { Error = error, Result = new UfbxiPushBuf() };
            }
            StringPool = new UfbxiStringPool(error, Warnings, new UfbxiPushBuf(), initialSize,
                Opts.UnicodeErrorHandling);
            UfbxiStringPool.LoadStrings(StringPool);
            return StringPool;
        }

        // C: ufbxi_node *top_nodes / size_t top_nodes_len / bool parsed_to_end /
        // ufbxi_node *top_node (ufbx.c:6549-6556) — the toplevel scan cache. The DOM builder
        // only needs `ParsedToEnd`; the cache itself arrives with the toplevel reader.
        internal bool ParsedToEnd;

        // C: bool has_next_child (ufbx.c:6558) — set by the non-recursive node parse: the node
        // ended before its `end_offset`, so it has children left to parse.
        internal bool HasNextChild;

        // C: ufbx_inflate_retain *inflate_retain (ufbx.c:6514) — one instance per load, reused
        // by every DEFLATE-encoded array so the static Huffman trees are built once.
        internal UfbxInflateRetain InflateRetain;

        internal UfbxInflateRetain GetInflateRetain()
        {
            if (InflateRetain == null) InflateRetain = new UfbxInflateRetain();
            return InflateRetain;
        }

        // C: bool parse_threaded (ufbx.c:6568) — only set inside
        // `ufbxi_read_objects_threaded()`, and only when the user supplied a thread pool
        // (`thread_opts.pool.run_fn/wait_fn`, ufbx.c:6069). The port has no thread pool, so
        // this is always false and the deferred-DEFLATE task never runs.
        internal bool ParseThreaded;

        // -- DOM retention state (ufbx.c:6501-6531)

        // C: ufbxi_buf tmp_stack, `ufbxi_node` items. C multiplexes node items, `ufbx_dom_value`
        // items and ASCII array bytes through one byte stack; every use is LIFO-balanced within
        // its own function, so a typed stack per item type is equivalent (PORTING_NOTES #4).
        readonly System.Collections.Generic.List<UfbxiNode> nodeStack = new System.Collections.Generic.List<UfbxiNode>();

        // C: ufbxi_buf tmp_dom_nodes (`ufbx_dom_node*` items). Genuinely cross-call: a node pushes
        // itself and its *parent* pops the children it pushed, in order.
        readonly System.Collections.Generic.List<UfbxDomNode> tmpDomNodes = new System.Collections.Generic.List<UfbxDomNode>();

        // C: ufbxi_dom_node_map dom_node_map (ufbx.c:6505) — keyed by the `ufbxi_node*` address.
        // C's map is looked up but never iterated, so reference identity is enough to reproduce
        // every observable (PORTING_NOTES.md "C 指针序 ≡ 分配序").
        readonly System.Collections.Generic.Dictionary<UfbxiNode, UfbxDomNode> domNodeMap
            = new System.Collections.Generic.Dictionary<UfbxiNode, UfbxDomNode>();

        // C: ufbx_dom_node *dom_parse_toplevel / size_t dom_parse_num_children (ufbx.c:6506-6507)
        internal UfbxDomNode DomParseToplevel;
        internal int DomParseNumChildren;

        // C: ufbxi_push(&uc->tmp_stack, ufbxi_node, 1)
        internal void PushNode(UfbxiNode node)
        {
            nodeStack.Add(node);
        }

        // C: ufbxi_pop(&uc->tmp_stack, ufbxi_node, 1, dst)
        internal UfbxiNode PopNode()
        {
            int last = nodeStack.Count - 1;
            UfbxiNode node = nodeStack[last];
            nodeStack.RemoveAt(last);
            return node;
        }

        // C: ufbxi_push_pop(buf, &uc->tmp_stack, ufbxi_node, n) — the last `n` items, in order.
        internal UfbxiNode[] PopNodes(int count)
        {
            int start = nodeStack.Count - count;
            // Port-only guard: C's `ufbxi_pop_size()` asserts `num_items >= n` (ufbx.c:4184,
            // a no-op/abort assert, not an error site).
            UfbxiFail.CheckNoDesc(start >= 0, "tmp_stack.num_items >= n");
            UfbxiNode[] items = new UfbxiNode[count];
            for (int i = 0; i < count; i++) items[i] = nodeStack[start + i];
            nodeStack.RemoveRange(start, count);
            return items;
        }

        // C: ufbxi_push_copy(&uc->tmp_dom_nodes, ufbx_dom_node*, 1, &dst)
        internal void PushTmpDomNode(UfbxDomNode node)
        {
            tmpDomNodes.Add(node);
        }

        // C: ufbxi_push_pop(&uc->result, &uc->tmp_dom_nodes, ufbx_dom_node*, n)
        internal UfbxDomNode[] PopTmpDomNodes(int count)
        {
            int start = tmpDomNodes.Count - count;
            // Port-only guard: C's `ufbxi_pop_size()` asserts `num_items >= n` (ufbx.c:4184,
            // a no-op/abort assert, not an error site).
            UfbxiFail.CheckNoDesc(start >= 0, "tmp_dom_nodes.num_items >= n");
            UfbxDomNode[] items = new UfbxDomNode[count];
            for (int i = 0; i < count; i++) items[i] = tmpDomNodes[start + i];
            tmpDomNodes.RemoveRange(start, count);
            return items;
        }

        internal int TmpDomNodeCount => tmpDomNodes.Count;

        // C: ufbxi_dom_mapping lookup/insert in ufbxi_retain_dom_node() (ufbx.c:10730-10740)
        internal void SetDomNodeMapping(UfbxiNode node, UfbxDomNode domNode)
        {
            domNodeMap[node] = domNode;
        }

        internal UfbxDomNode FindDomNode(UfbxiNode node)
        {
            UfbxDomNode domNode;
            if (node != null && domNodeMap.TryGetValue(node, out domNode)) return domNode;
            return null;
        }

        // C: char *swap_arr / size_t swap_arr_size (ufbx.c:6511-6512) — scratch for
        // ufbxi_swap_endian(); the returned buffer is only valid until the next swap.
        internal byte[] SwapArr;
        internal int SwapArrSize;

        // C: void **element_extra_arr / size_t element_extra_cap (ufbx.c:6545-6546)
        // Per-element scratch payload, indexed by element id. The concrete type is chosen
        // by the call site via `ufbxi_push_element_extra(uc, id, type)`.
        readonly System.Collections.Generic.List<object> elementExtra = new System.Collections.Generic.List<object>();

        // C: ufbxi_push_element_extra_size(uc, id, size) (ufbx.c:7873-7888)
        // `create` builds the zero-initialized payload; C zero-fills `size` bytes, so a
        // fresh C# object with default field values is the equivalent.
        internal T PushElementExtra<T>(uint id, System.Func<T> create) where T : class
        {
            if (elementExtra.Count <= id) {
                while (elementExtra.Count <= id) elementExtra.Add(null);
            }
            if (elementExtra[(int)id] is T existing) return existing;
            T extra = create();
            elementExtra[(int)id] = extra;
            return extra;
        }

        // C: ufbxi_get_element_extra(uc, id) (ufbx.c:7890-7896)
        internal T GetElementExtra<T>(uint id) where T : class
        {
            if (id < (uint)elementExtra.Count) return elementExtra[(int)id] as T;
            return null;
        }

        // ------------------------------------------------------------------
        // Load-driver state (added by the load spine, src/Ufbx/Parse/Load.cs)
        // ------------------------------------------------------------------

        // C: ufbx_error error (ufbx.c:6410) — the internal error the whole `ufbxi_load_imp()`
        // failure plumbing writes into, and the source of the user's `ufbx_error` after
        // `ufbxi_fix_error_type()` (ufbx.c:25623). The string pool and the allocator report
        // into the same object in C (ufbx.c:25549, 25500-25501), so `InitStringPool` below has
        // to be handed this instance.
        public UfbxError Error = new UfbxError();

        // C: ufbx_scene scene (ufbx.c:6478) — built in place in the context; the loader hands
        // out `&uc->scene_imp->scene` (a copy of this struct at ufbx.c:25585) on success.
        public UfbxScene Scene = new UfbxScene();

        // C: bool retain_mesh_parts (ufbx.c:6588, set at ufbx.c:25278).
        internal bool RetainMeshParts;

        // C: ufbx_real unit_scale (ufbx.c:6576, `uc->unit_scale = 1.0f` at ufbx.c:25286).
        // `ufbx_real` is `double` in the golden build (PORTING_NOTES "DOM/数组数据约定").
        internal double UnitScale;

        // C: uint64_t synthetic_id_counter (ufbx.c:6585, set to UFBXI_SYNTHETIC_ID_START at
        // ufbx.c:25547). Consumed by the element/id layers, which arrive with the readers.
        internal ulong SyntheticIdCounter;

        // C: bool deferred_failure / deferred_load / const char *load_filename /
        // size_t load_filename_len (ufbx.c:6625-6629) — `ufbx_load_file()` does not open the
        // file itself, it records the name and lets `ufbxi_load_imp()` open it once the opts
        // are known (ufbx.c:25215-25252).
        internal bool DeferredFailure;
        internal bool DeferredLoad;
        internal string LoadFilename;

        // C: ufbxi_map prop_type_map (ufbx.c:6503), items `ufbxi_prop_type_name`, keyed by the
        // pooled `const char*` (ufbxi_map_cmp_const_char_ptr). Filled by `ufbxi_load_maps()`
        // (ufbx.c:11742-11756) from the constant table `ufbxi_prop_type_names[]`
        // (ufbx.c:11432-11465); read by the property readers.
        internal UfbxiMap<UfbxiPropTypeName, ulong> PropTypeMap;

        // C: the `ufbxi_load()` string-pool setup (ufbx.c:25549-25556) *without* the
        // `ufbxi_load_strings()` interning, which `ufbxi_load_imp()` performs later
        // (ufbx.c:25291) — after `ufbxi_fixup_opts_string()` has already interned the option
        // strings (ufbx.c:25262-25265). Pool order is pointer order (PORTING_NOTES
        // "C 指针序 ≡ 分配序"), so the sequence matters; `InitStringPool` above keeps its
        // existing eager-intern behaviour for the DOM harness that calls it.
        internal UfbxiStringPool NewStringPool(UfbxError error, uint initialSize)
        {
            if (Warnings == null) {
                Warnings = new UfbxiWarnings { Error = error, Result = new UfbxiPushBuf() };
            }
            StringPool = new UfbxiStringPool(error, Warnings, new UfbxiPushBuf(), initialSize,
                Opts.UnicodeErrorHandling);
            return StringPool;
        }

        // C: size_t load_filename_len (ufbx.c:6626) alongside `load_filename`. -1 is this port's
        // SIZE_MAX, i.e. `ufbx_load_file()`'s "the name is nul-terminated, measure it"
        // (ufbx.c:30522 `ufbx_load_file_len(filename, SIZE_MAX, ...)`).
        internal int LoadFilenameLen;

        // C: ufbxi_thread_pool (ufbx.c:6476, `ufbxi_thread_pool_init()` ufbx.c:6067-6089).
        // `ThreadPoolEnabled`/`ThreadPoolNumTasks` are the derived flags the readers test; the
        // machinery itself (task ring, groups, `ufbx_thread_pool_context`) lives in
        // Parse/ThreadPool.cs and is reached through `ThreadPool`, non-null exactly when enabled.
        internal UfbxiThreadPool ThreadPool;
        internal bool ThreadPoolEnabled;
        internal uint ThreadPoolNumTasks;

        // ==================================================================
        // S1 pipeline state (added by the S1 agent: src/Ufbx/Parse/{FbxId,Properties,
        // Connections,ReadElement,Root}.cs). Mirrors the `ufbxi_context` fields the S1
        // functions read/write; the backing types live at the bottom of this file so the
        // isolated verifiers that compile UfbxiContext.cs (e.g. tools/AsciiCheck) keep
        // compiling without reaching into the S1 files. See COORDINATION.md #8.
        // ==================================================================

        // C: bool parsed_to_end is above; the toplevel scan cache is
        //   ufbxi_node *top_nodes / size_t top_nodes_len / ufbxi_node *top_node /
        //   size_t top_child_index (ufbx.c:6549-6556) and `ufbx_node legacy_node`
        //   (ufbx.c:6562). `TopChildIndex` is C's `top_child_index`, with -1 == SIZE_MAX
        //   ("children not parsed yet").
        internal readonly System.Collections.Generic.List<UfbxiNode> TopNodes
            = new System.Collections.Generic.List<UfbxiNode>();
        internal UfbxiNode TopNode;
        internal int TopChildIndex;
        internal UfbxiNode LegacyNode;

        // C: ufbxi_node top_child (ufbx.c:6553) — the single reused slot that
        // `ufbxi_parse_toplevel_child(uc, &node, NULL)` pops an on-demand child into. The port
        // keeps one stable `UfbxiNode` and copies the popped node's fields into it, preserving
        // C's "same address across calls" semantics (see S1/Root.cs `CopyNode`).
        internal UfbxiNode TopChild;

        // C: ufbx_element **tmp_element_ptrs / uint64_t *tmp_element_fbx_ids /
        // size_t num_elements (ufbx.c:6580-6582) and the per-type typed-id counters
        // `tmp_typed_element_offsets[type].num_items` (ufbx.c:6579). `NumElements` is the
        // element-id allocator; `TmpElementPtrs[i]` is the element with `element_id == i`
        // (PORTING_NOTES.md "C 指针序 ≡ 分配序").
        internal readonly System.Collections.Generic.List<UfbxElement> TmpElementPtrs
            = new System.Collections.Generic.List<UfbxElement>();
        internal readonly System.Collections.Generic.List<ulong> TmpElementFbxIds
            = new System.Collections.Generic.List<ulong>();
        internal uint NumElements;
        internal readonly int[] TmpTypedElementCount = new int[UfbxEnumCounts.UfbxElementType];

        // C: uint32_t *tmp_node_ids (ufbx.c:6577) — element ids of nodes, in push order.
        internal readonly System.Collections.Generic.List<uint> TmpNodeIds
            = new System.Collections.Generic.List<uint>();

        // C: ufbxi_tmp_connection *tmp_connections (ufbx.c:6581).
        internal readonly System.Collections.Generic.List<UfbxiTmpConnection> TmpConnections
            = new System.Collections.Generic.List<UfbxiTmpConnection>();

        // C: uint32_t *p_element_id / ufbxi_buf tmp_element_id (ufbx.c:6573-6574). The
        // deferred per-object element id that `ufbxi_read_objects()` pushes so warnings can
        // tag themselves; `PElementIdIndex` is C's non-NULL `p_element_id` (-1 == NULL).
        internal readonly System.Collections.Generic.List<uint> TmpElementIds
            = new System.Collections.Generic.List<uint>();
        internal int PElementIdIndex = -1;

        // C: ufbxi_map fbx_id_map / ptr_fbx_id_map / fbx_attr_map / node_prop_set
        // (ufbx.c:25558-25564). Created by `S1InitMaps` (the port's stand-in for the
        // `ufbxi_map_init` block of `ufbxi_load()`), keyed exactly like the C comparators.
        internal UfbxiMap<UfbxiFbxIdEntry, ulong> FbxIdMap;
        internal UfbxiMap<UfbxiPtrFbxIdEntry, UfbxiPtrId> PtrFbxIdMap;
        internal UfbxiMap<UfbxiFbxAttrEntry, ulong> FbxAttrMap;
        internal UfbxiMap<string, ulong> NodePropSet;

        // C: uint64_t root_id (ufbx.c:6565), int64_t ktime_sec / double ktime_sec_double
        // (ufbx.c:6569-6570), ufbxi_template *templates / size_t num_templates
        // (ufbx.c:6559-6560), bool read_legacy_settings (ufbx.c:6592),
        // uint64_t legacy_implicit_anim_layer_id (ufbx.c:6593), uint32_t exporter_version
        // (ufbx.c:6462), bool has_geometry_transform_nodes / has_scale_helper_nodes
        // (ufbx.c:6586-6587).
        internal ulong RootId;
        internal long KtimeSec;
        internal double KtimeSecDouble;
        internal UfbxiTemplate[] Templates;
        internal int NumTemplates;
        internal bool ReadLegacySettings;
        internal ulong LegacyImplicitAnimLayerId;
        internal uint ExporterVersion;
        internal bool HasGeometryTransformNodes;
        internal bool HasScaleHelperNodes;

        // C: the ufbxi_map_init() calls for the S1 maps (ufbx.c:25558-25564), run once at
        // the start of `ufbxi_read_root()`/`ufbxi_read_legacy_root()`. The load spine does
        // not create them because OBJ/MTL never reach S1 (the field declarations stay
        // append-only in UfbxiContext.cs instead of touching Parse/Load.cs).
        internal void S1InitMaps()
        {
            if (FbxIdMap == null) {
                FbxIdMap = new UfbxiMap<UfbxiFbxIdEntry, ulong>(
                    UfbxiMapCmps.UInt64<UfbxiFbxIdEntry>(e => e.FbxId), 24, Error);
            }
            if (PtrFbxIdMap == null) {
                PtrFbxIdMap = new UfbxiMap<UfbxiPtrFbxIdEntry, UfbxiPtrId>(
                    UfbxiMapCmps.PtrId<UfbxiPtrFbxIdEntry>(e => e.PtrId), 24, Error);
            }
            if (FbxAttrMap == null) {
                FbxAttrMap = new UfbxiMap<UfbxiFbxAttrEntry, ulong>(
                    UfbxiMapCmps.UInt64<UfbxiFbxAttrEntry>(e => e.NodeFbxId), 16, Error);
            }
            if (NodePropSet == null) {
                NodePropSet = new UfbxiMap<string, ulong>(
                    UfbxiMapCmps.ConstCharPtr<string>(s => UfbxiPtrIdTable.IdOf(s)), 8, Error);
            }
        }
    }

    // C: ufbxi_tmp_connection (ufbx.c:6300-6306). Value type: the port pushes/pops these by
    // value like C's `ufbxi_push(&uc->tmp_connections, ufbxi_tmp_connection, 1)`.
    internal struct UfbxiTmpConnection
    {
        public ulong Src;
        public ulong Dst;
        public string SrcProp;
        public string DstProp;
    }

    // C: ufbxi_fbx_id_entry (ufbx.c:6284-6288).
    internal struct UfbxiFbxIdEntry
    {
        public ulong FbxId;
        public uint ElementId;
        public uint UserId;
    }

    // C: ufbxi_ptr_fbx_id_entry (ufbx.c:6290-6293).
    internal struct UfbxiPtrFbxIdEntry
    {
        public UfbxiPtrId PtrId;
        public ulong FbxId;
    }

    // C: ufbxi_fbx_attr_entry (ufbx.c:6295-6298).
    internal struct UfbxiFbxAttrEntry
    {
        public ulong NodeFbxId;
        public ulong AttrFbxId;
    }

    // C: ufbxi_template (ufbx.c:6279-6283). Reference type: `ufbxi_read_definitions()` pushes
    // these and `ufbxi_find_template()` hands the embedded `props` back by pointer.
    internal sealed class UfbxiTemplate
    {
        public string Type;      // C: const char *type
        public string SubType;   // C: ufbx_string sub_type
        public UfbxProps Props;  // C: ufbx_props props
    }

    // C: ufbxi_element_info (ufbx.c:6308-6313). Reference type: it is filled in by the caller
    // and consumed by `ufbxi_push_element_size()`.
    internal sealed class UfbxiElementInfo
    {
        public ulong FbxId;        // C: uint64_t fbx_id
        public string Name;        // C: ufbx_string name
        public UfbxProps Props;    // C: ufbx_props props
        public UfbxDomNode DomNode; // C: ufbx_dom_node *dom_node

        internal UfbxiElementInfo()
        {
            Name = string.Empty;
            Props = new UfbxProps();
        }
    }

    // C: ufbxi_node_extra (ufbx.c:12503-12506) — the per-node payload `ufbxi_setup_*_helper`
    // stores through `ufbxi_push_element_extra()`.
    internal sealed class UfbxiNodeExtra
    {
        public uint GeometryHelperId;
        public uint ScaleHelperId;
    }
}
