// Toplevel scan, root reader and the root-level leaves, ported from ufbx v0.23.1 ufbx.c:
//   ufbxi_parse_toplevel_child_imp (11238-11247)
//   ufbxi_parse_toplevel           (11249-11326)
//   ufbxi_parse_toplevel_child     (11328-11373)
//   ufbxi_read_global_settings     (14944-14948)
//   ufbxi_read_take / read_takes   (15691-15767)
//   ufbxi_read_legacy_settings     (15769-15819)
//   ufbxi_setup_root_node          (15830-15840)
//   ufbxi_read_root                (15847-15939)
//   ufbxi_read_legacy_root         (16423-16482) — dispatch skeleton only
//
// `uc->top_node` / `top_nodes` / `top_child_index` mirror C exactly: `TopChildIndex == -1` is
// C's `SIZE_MAX` ("the toplevel node matched but its children are parsed on demand"), `0` the
// cached-node state. `uc->top_nodes` is a `List<UfbxiNode>`, so C's `&uc->top_nodes[i]`
// pointer-stability problem (grow_array may move the array) does not exist here.
//
// C's `ufbxi_pop(&uc->tmp_stack, ufbxi_node, 1, dst)` copies a node *value* into `dst`; the
// port's `UfbxiNode` is a reference type, so `ParseToplevelChild` copies the popped node's
// fields into the stable reused `uc.TopChild` to keep C's single-address semantics.
using System;

namespace Ufbx.NET
{
    internal static class UfbxiRoot
    {
        // C: ufbxi_parse_toplevel_child_imp (ufbx.c:11238-11247) — the `recursive = true`
        // node parse shared by the two toplevel walkers.
        static void ParseToplevelChildImp(UfbxiContext uc, UfbxiParseState state)
        {
            bool end;
            UfbxiDom.ParseNode(uc, 0, state, out end, true);
        }

        // C: ufbxi_parse_toplevel (ufbx.c:11249-11326).
        internal static void ParseToplevel(UfbxiContext uc, string name)
        {
            for (int i = 0; i < uc.TopNodes.Count; i++) {
                UfbxiNode cached = uc.TopNodes[i];
                if (cached.Name == name) {
                    uc.TopNode = cached;
                    uc.TopChildIndex = 0;
                    return;
                }
            }

            // Reached end and not found in cache
            if (uc.ParsedToEnd) {
                uc.TopNode = null;
                uc.TopChildIndex = 0;
                return;
            }

            for (;;) {
                // Parse the next top-level node
                bool end;
                UfbxiDom.ParseNode(uc, 0, UfbxiParseState.Root, out end, false);

                // Top-level node not found
                if (end) {
                    uc.TopNode = null;
                    uc.TopChildIndex = 0;
                    uc.ParsedToEnd = true;
                    if (uc.Opts.RetainDom) {
                        UfbxiDom.RetainToplevel(uc, null);
                    }

                    // Not needed anymore (C: ufbxi_buf_free(&uc->tmp_parse))
                    return;
                }

                UfbxiNode node = uc.PopNode();
                uc.TopNodes.Add(node);
                if (uc.Opts.RetainDom) {
                    UfbxiDom.RetainToplevel(uc, node);
                }

                // Return if we parsed the right one
                if (node.Name == name) {
                    uc.TopNode = node;
                    uc.TopChildIndex = -1;   // C: SIZE_MAX
                    return;
                }

                // If not we need to parse all the children of the node for later
                uint numChildren = 0;
                UfbxiParseState state = UfbxiParseStateMachine.Update(UfbxiParseState.Root, node.Name);
                if (uc.HasNextChild) {
                    for (;;) {
                        bool childEnd;
                        UfbxiDom.ParseNode(uc, 0, state, out childEnd, true);
                        if (childEnd) break;
                        numChildren++;
                    }
                }

                node.NumChildren = numChildren;
                if (numChildren > 0) {
                    node.Children = uc.PopNodes((int)numChildren);
                }

                if (uc.Opts.RetainDom) {
                    for (int i = 0; i < numChildren; i++) {
                        UfbxiDom.RetainToplevelChild(uc, node.Children[i]);
                    }
                }
            }
        }

        // C: ufbxi_pop(&uc->tmp_stack, ufbxi_node, 1, dst) — copy the popped node's value.
        static void CopyNode(UfbxiNode dst, UfbxiNode src)
        {
            dst.Name = src.Name;
            dst.NumChildren = src.NumChildren;
            dst.NameLen = src.NameLen;
            dst.ValueTypeMask = src.ValueTypeMask;
            dst.Children = src.Children;
            dst.Array = src.Array;
            dst.Vals = src.Vals;
        }

        // C: ufbxi_parse_toplevel_child (ufbx.c:11328-11373) with `tmp_buf == NULL` (the only
        // form the S1 readers use).
        internal static UfbxiNode ParseToplevelChild(UfbxiContext uc)
        {
            return ParseToplevelChild(uc, false);
        }

        // The S2 form (ufbx.c:11328-11373 with a non-NULL `tmp_buf`), used only by
        // ufbxi_read_objects_threaded (15184): C pushes each child into the caller's buffer so a
        // whole batch can be held before any of it is read. Allocating a node per child is the
        // same thing here — unlike `ParseToplevelChild` above, the result outlives the next call.
        internal static UfbxiNode ParseToplevelChildOwned(UfbxiContext uc)
        {
            return ParseToplevelChild(uc, true);
        }

        internal static UfbxiNode ParseToplevelChild(UfbxiContext uc, bool owned)
        {
            // Top-level node not found
            if (uc.TopNode == null) return null;

            if (uc.TopChildIndex == -1) {
                // Parse children on demand
                UfbxiParseState state = UfbxiParseStateMachine.Update(UfbxiParseState.Root, uc.TopNode.Name);
                bool end;
                UfbxiDom.ParseNode(uc, 0, state, out end, true);
                if (end) return null;

                // C: parse to either the reused `uc->top_child` or push into `tmp_buf`.
                UfbxiNode popped = uc.PopNode();
                UfbxiNode dst = uc.TopChild;
                if (owned || dst == null) {
                    dst = new UfbxiNode();
                    if (!owned) uc.TopChild = dst;
                }
                CopyNode(dst, popped);

                if (uc.Opts.RetainDom) {
                    UfbxiDom.RetainToplevelChild(uc, dst);
                }
                return dst;
            } else {
                // Iterate already parsed nodes
                int childIndex = uc.TopChildIndex;
                if (childIndex == (int)uc.TopNode.NumChildren) {
                    return null;
                } else {
                    uc.TopChildIndex++;
                    return uc.TopNode.Children[childIndex];
                }
            }
        }

        // C: ufbxi_read_global_settings (ufbx.c:14944-14948).
        internal static void ReadGlobalSettings(UfbxiContext uc, UfbxiNode node)
        {
            UfbxiProperties.ReadProperties(uc, node, uc.Scene.Settings.Props);
        }

        // C: ufbxi_read_takes (ufbx.c:15754-15767).
        internal static void ReadTakes(UfbxiContext uc)
        {
            for (;;) {
                UfbxiNode node = ParseToplevelChild(uc);
                if (node == null) break;

                if (node.Name == UfbxiStrings.Take) {
                    ReadTake(uc, node);
                }
            }
        }

        // C: ufbxi_read_take (ufbx.c:15691-15752) — ported in Parse/AnimReader.cs.
        internal static void ReadTake(UfbxiContext uc, UfbxiNode node)
        {
            UfbxiAnimReader.ReadTake(uc, node);
        }

        // C: ufbxi_read_legacy_settings (ufbx.c:15769-15819).
        internal static void ReadLegacySettings(UfbxiContext uc, UfbxiNode node)
        {
            if (uc.ReadLegacySettings) return;
            uc.ReadLegacySettings = true;

            UfbxProp[] tmpProps = new UfbxProp[2];
            int numProps = 0;

            UfbxiNode frameRate = node.FindChildStrCmp("FrameRate");
            if (frameRate != null) {
                double fps = 0.0;
                if (!frameRate.GetValD(0, out fps)) {
                    string str;
                    if (frameRate.GetValS(0, out str)) {
                        byte[] bytes = UfbxiSanitizedString.ToBlob(str, str.Length);
                        double val = UfbxiNumeric.ParseDouble(bytes, out int end, uc.DoubleParseFlags);
                        if (end == str.Length) {
                            fps = val;
                        }
                    }
                }
                if (fps > 0.0) {
                    UfbxiReadElement.InitSyntheticRealProp(ref tmpProps[numProps++], UfbxiStrings.CustomFrameRate,
                        fps, UfbxPropType.Number);
                    UfbxiReadElement.InitSyntheticRealProp(ref tmpProps[numProps++], UfbxiStrings.TimeMode,
                        (double)(int)UfbxTimeMode.Custom, UfbxPropType.Integer);
                }
            }

            if (numProps > 0) {
                UfbxProps props = uc.Scene.Settings.Props;
                int numExisting = props.Props != null ? props.Props.Length : 0;

                int newCount = numProps + numExisting;
                UfbxProp[] newProps = new UfbxProp[newCount];

                Array.Copy(tmpProps, 0, newProps, 0, numProps);
                if (numExisting > 0) {
                    Array.Copy(props.Props, 0, newProps, numProps, numExisting);
                }

                UfbxiProperties.SortProperties(newProps);
                props.Props = newProps;
                UfbxiProperties.DeduplicateProperties(props);

                UfbxiFail.CheckNoDesc(props.Props != null, "uc->scene.settings.props.props.data");
            }
        }

        // C: ufbxi_setup_root_node (ufbx.c:15830-15840).
        internal static void SetupRootNode(UfbxiContext uc, UfbxNode root)
        {
            if (uc.Opts.UseRootTransform) {
                root.LocalTransform = uc.Opts.RootTransform;
                root.NodeToParent = UfbxMatrix.FromTransform(uc.Opts.RootTransform);
            } else {
                root.LocalTransform = UfbxTransform.Identity;
                root.NodeToParent = UfbxMatrix.Identity;
            }
            root.IsRoot = true;
        }

        // C: ufbxi_read_root (ufbx.c:15847-15939).
        internal static void ReadRoot(UfbxiContext uc)
        {
            // C creates the S1 maps in `ufbxi_load()` before `ufbxi_load_imp()` (ufbx.c:25557-25562).
            uc.S1InitMaps();

            // FBXHeaderExtension: Some metadata (optional)
            ParseToplevel(uc, UfbxiStrings.FBXHeaderExtension);
            UfbxiProperties.ReadHeaderExtension(uc);

            // The ASCII exporter version is stored in top-level
            if (uc.Exporter == UfbxExporter.BlenderAscii) {
                ParseToplevel(uc, UfbxiStrings.Creator);
                if (uc.TopNode != null) {
                    string creator;
                    if (uc.TopNode.GetValS(0, out creator)) {
                        uc.Scene.Metadata.Creator = creator;
                    }
                }
            }

            // Resolve the exporter before continuing
            UfbxiProperties.MatchExporter(uc);
            if (uc.Version < 7000) {
                UfbxiProperties.InitNodePropNames(uc);
            }
            // Don't allow changing version from this point onwards
            if (uc.Ascii != null) {
                uc.Ascii.FoundVersion = true;
            }

            // Document: Read root ID
            if (uc.Version >= 7000) {
                ParseToplevel(uc, UfbxiStrings.Documents);
                UfbxiProperties.ReadDocument(uc);
            } else {
                // Pre-7000: Root node has a specific type-name pair "Model::Scene"
                // (or reversed in binary). Use the interned name as ID as usual.
                string rootName = uc.FromAscii ? "Model::Scene" : "Scene\x00\x01Model";
                rootName = uc.StringPool.PushStringImp(rootName, 0, 12, out int ignored, false, true);
                UfbxiFail.CheckNoDesc(rootName != null, "root_name");
                uc.RootId = UfbxiFbxId.SyntheticIdFromString(uc, rootName);
                UfbxiFail.CheckNoDesc(uc.RootId != 0, "uc->root_id");
            }

            // Add a nameless root node with the root ID
            {
                UfbxiElementInfo rootInfo = new UfbxiElementInfo();
                rootInfo.FbxId = uc.RootId;
                rootInfo.Name = string.Empty;
                UfbxNode root = UfbxiReadElement.PushElement<UfbxNode>(uc, rootInfo, UfbxElementType.Node);
                SetupRootNode(uc, root);
                uc.TmpNodeIds.Add(root.ElementId);
            }

            // Definitions: Object type counts and property templates (optional)
            ParseToplevel(uc, UfbxiStrings.Definitions);
            UfbxiProperties.ReadDefinitions(uc);

            // Objects: Actual scene data
            ParseToplevel(uc, UfbxiStrings.Objects);
            if (!uc.SureFbx) {
                // If the file is a bit iffy about being a real FBX file reject it if
                // even the objects are not found.
                UfbxiFail.CheckMsg(uc.TopNode != null, "Not an FBX file");
            }
            if (uc.ThreadPoolEnabled) {
                // C: ufbxi_read_objects_threaded (ufbx.c:15132-15237). The port has no thread
                // pool (PORTING_NOTES #7): `ReadObjectsThreaded` degenerates to the same
                // sequential walk as `ufbxi_read_objects`.
                UfbxiObjects.ReadObjectsThreaded(uc);
            } else {
                UfbxiReadElement.ReadObjects(uc);
            }

            // Connections: Relationships between nodes
            ParseToplevel(uc, UfbxiStrings.Connections);
            UfbxiConnections.ReadConnections(uc);

            // Takes: Pre-7000 animation data
            ParseToplevel(uc, UfbxiStrings.Takes);
            ReadTakes(uc);

            // Check if there's a top-level GlobalSettings that we skimmed over
            ParseToplevel(uc, UfbxiStrings.GlobalSettings);
            if (uc.TopNode != null) {
                ReadGlobalSettings(uc, uc.TopNode);
            }

            // Version5: Pre-6000 settings
            ParseToplevel(uc, UfbxiStrings.Version5);
            if (uc.TopNode != null) {
                UfbxiNode settings = uc.TopNode.FindChildStrCmp("Settings");
                if (settings != null) {
                    ReadLegacySettings(uc, settings);
                }
            }

            // Force parsing all the nodes by parsing a toplevel that cannot be found
            if (uc.Opts.RetainDom) {
                ParseToplevel(uc, null);
            }
        }

        // C: ufbxi_parse_legacy_toplevel (ufbx.c:11375-11406). Note this differs from
        // `ufbxi_parse_toplevel` (11249): the legacy walker parses exactly one node per call
        // and keeps it in the reused `uc->legacy_node` storage (`top_nodes_len == 0`), so the
        // port stores it in `uc.LegacyNode` instead of appending to `uc.TopNodes`.
        internal static void ParseLegacyToplevel(UfbxiContext uc)
        {
            // C: ufbx_assert(uc->top_nodes_len == 0)
            bool end;
            UfbxiDom.ParseNode(uc, 0, UfbxiParseState.Root, out end, true);

            // Top-level node not found
            if (end) {
                uc.TopNode = null;
                uc.TopChildIndex = 0;
                uc.ParsedToEnd = true;
                return;
            }

            // C: ufbxi_pop(&uc->tmp_stack, ufbxi_node, 1, &uc->legacy_node)
            uc.LegacyNode = uc.PopNode();
            uc.TopChildIndex = 0;
            uc.TopNode = uc.LegacyNode;

            if (uc.Opts.RetainDom) {
                UfbxiDom.RetainToplevel(uc, uc.LegacyNode);
            }
        }

        // C: ufbxi_read_legacy_root (ufbx.c:16423-16482). Only the dispatch skeleton is
        // erected this round: `ParseLegacyToplevel` (the very first statement of the loop)
        // throws, so the legacy leaves (`ufbxi_read_legacy_media/model`, `_take_object`)
        // remain S2/S3.
        internal static void ReadLegacyRoot(UfbxiContext uc)
        {
            uc.S1InitMaps();
            UfbxiProperties.InitNodePropNames(uc);

            // Some legacy FBX files have an `Fbx_Root` node that could be used as the root
            // node. However no other formats have root node with transforms so it might be
            // better to leave it as-is and create an empty one.
            {
                ulong rootId;
                UfbxNode root = UfbxiReadElement.PushSyntheticElement<UfbxNode>(uc, out rootId, null,
                    string.Empty, UfbxElementType.Node);
                uc.RootId = rootId;
                SetupRootNode(uc, root);
                uc.TmpNodeIds.Add(root.ElementId);
            }

            // NOTE: `ufbxi_read_header_extension()` is optional so use default KTime definition
            uc.KtimeSec = 46186158000L;
            uc.KtimeSecDouble = (double)uc.KtimeSec;

            for (;;) {
                ParseLegacyToplevel(uc);
                if (uc.TopNode == null) break;

                UfbxiNode node = uc.TopNode;
                if (node.Name == UfbxiStrings.FBXHeaderExtension) {
                    UfbxiProperties.ReadHeaderExtension(uc);
                } else if (node.Name == UfbxiStrings.Media) {
                    UfbxiLegacy.ReadLegacyMedia(uc, node);
                } else if (node.Name == UfbxiStrings.Takes) {
                    ReadTakes(uc);
                } else if (node.Name == UfbxiStrings.Model) {
                    UfbxiLegacy.ReadLegacyModel(uc, node);
                } else if (UfbxiProperties.Strcmp(node.Name, "Settings") == 0) {
                    ReadLegacySettings(uc, node);
                }
            }

            if (uc.Opts.RetainDom) {
                UfbxiDom.RetainToplevel(uc, null);
            }

            // Create the implicit animation stack if necessary
            if (uc.LegacyImplicitAnimLayerId != 0) {
                UfbxiElementInfo layerInfo = new UfbxiElementInfo();
                layerInfo.FbxId = uc.LegacyImplicitAnimLayerId;
                string layerName = "(internal)";
                int outLength;
                layerName = uc.StringPool.PushString(layerName, 0, layerName.Length, out outLength, true);
                UfbxiFail.CheckNoDesc(layerName != null, "ufbxi_push_string_place_str(&uc->string_pool, &layer_info.name, true)");
                layerInfo.Name = layerName;
                UfbxAnimLayer layer = UfbxiReadElement.PushElement<UfbxAnimLayer>(uc, layerInfo, UfbxElementType.AnimLayer);

                // C: `ufbxi_element_info stack_info = layer_info;` copies the struct, so the new
                // fbx id does not change `layer_info.fbx_id` (which the connection below uses).
                UfbxiElementInfo stackInfo = new UfbxiElementInfo {
                    FbxId = UfbxiFbxId.PushSyntheticId(uc),
                    Name = layerInfo.Name,
                    Props = layerInfo.Props,
                    DomNode = layerInfo.DomNode,
                };
                UfbxiReadElement.PushElement<UfbxAnimStack>(uc, stackInfo, UfbxElementType.AnimStack);

                UfbxiReadElement.ConnectOo(uc, layerInfo.FbxId, stackInfo.FbxId);
            }
        }

        // The S2/S3 seam: throws for readers that are out of scope for this port.
        static void NotPorted(string reader)
        {
            throw new UfbxiReaderNotPortedException(reader);
        }
    }
}
