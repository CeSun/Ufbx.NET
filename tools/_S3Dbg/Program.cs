// Scratch diagnostic for the golden-hash divergence hunt (S3b/S3c wave, continued).
//
//   S3Dbg goldens <golden_file> <data_dir>
//       replays every frame==0 golden entry through the port and prints
//       `MATCH|DIFF|ERR <path> <expected> <actual>`
//   S3Dbg trace <path>... | trace-list <file>
//       prints the cumulative FNV state after each top-level scene list plus one per-item
//       hash from the initial state (`I <tag> <index> <state>`), mirroring
//       tools/_s3bc_hashtrace.c 1:1, so a mismatch names the exact diverging object.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ufbx;
using S3DbgTrace;

class Dbg
{
    static UfbxHashSceneTrace h = new UfbxHashSceneTrace();

    static string Hex(ulong v) { return v.ToString("x16", CultureInfo.InvariantCulture); }

    static void Show(string tag) { Console.WriteLine(tag + " " + Hex(h.State)); }

    // Prints in the C tool's order (`HL`/`HLP` in tools/_s3bc_hashtrace.c): the cumulative state
    // after the whole list first, then one per-item hash from the initial state.
    static void HashList<T>(string tag, T[] items, Action<UfbxHashSceneTrace, T> fn)
    {
        int n = items != null ? items.Length : 0;
        h.HashSizeT(n);
        for (int i = 0; i < n; i++) fn(h, items[i]);
        Show(tag);
        for (int i = 0; i < n; i++) {
            var ih = new UfbxHashSceneTrace();
            fn(ih, items[i]);
            Console.WriteLine("I " + tag + " " + i.ToString(CultureInfo.InvariantCulture) + " " + Hex(ih.State));
        }
    }

    static void XF(string prefix, int id, string name, Action<UfbxHashSceneTrace> fn)
    {
        var fh = new UfbxHashSceneTrace();
        fn(fh);
        Console.WriteLine(prefix + " " + id.ToString(CultureInfo.InvariantCulture) + " " + name + " " + Hex(fh.State));
    }

    // Mirrors meta_fields()/anim_fields()/elem_fields_all() in tools/_s3bc_hashtrace.c.
    static void MetaFields(UfbxScene s)
    {
        var m = s.Metadata;
        XF("M", 0, "ascii_version", t => { t.HashBool(m.Ascii); t.HashU32(m.Version); });
        XF("M", 0, "creator", t => t.HashString(m.Creator));
        XF("M", 0, "flags", t => { t.HashBool(m.IsUnsafe); t.HashBool(m.BigEndian); });
        XF("M", 0, "exporter", t => { t.HashU32(unchecked((uint)(int)m.Exporter)); t.HashU32(m.ExporterVersion); });
        XF("M", 0, "scene_props", t => t.HashProps(m.SceneProps));
        XF("M", 0, "orig_app", t => t.HashApplication(m.OriginalApplication));
        XF("M", 0, "latest_app", t => t.HashApplication(m.LatestApplication));
        XF("M", 0, "has_warning", t => { for (int i = 0; i < m.HasWarning.Length; i++) t.HashBool(m.HasWarning[i]); });
    }

    static void AnimFields(UfbxScene s)
    {
        var a = s.Anim;
        if (a == null) return;
        XF("A", 0, "time", t => { t.HashDouble(a.TimeBegin); t.HashDouble(a.TimeEnd); });
        XF("A", 0, "layers", t => { int n = a.Layers != null ? a.Layers.Length : 0; t.HashSizeT(n);
            for (int i = 0; i < n; i++) t.HashElementRefValue(a.Layers[i]); });
        XF("A", 0, "weights", t => { int n = a.OverrideLayerWeights != null ? a.OverrideLayerWeights.Length : 0; t.HashSizeT(n);
            for (int i = 0; i < n; i++) t.HashU64(unchecked((ulong)BitConverter.DoubleToInt64Bits(a.OverrideLayerWeights[i]))); });
        XF("A", 0, "prop_overrides", t => HashSub(t, a.PropOverrides, (x, v) => x.HashPropOverride(v)));
        XF("A", 0, "transform_overrides", t => HashSub(t, a.TransformOverrides, (x, v) => x.HashTransformOverride(v)));
        XF("A", 0, "flags", t => { t.HashBool(a.IgnoreConnections); t.HashBool(a.Custom); });
    }

    static void HashSub<T>(UfbxHashSceneTrace t, T[] items, Action<UfbxHashSceneTrace, T> fn)
    {
        int n = items != null ? items.Length : 0;
        t.HashSizeT(n);
        for (int i = 0; i < n; i++) fn(t, items[i]);
    }

    static void ElemFields(UfbxScene s)
    {
        UfbxElement[] els = s.Elements;
        int n = els != null ? els.Length : 0;
        for (int i = 0; i < n; i++) {
            UfbxElement e = els[i];
            XF("E", i, "name", t => t.HashString(e.Name));
            XF("E", i, "props", t => t.HashProps(e.Props));
            XF("E", i, "ids", t => { t.HashU32(e.ElementId); t.HashU32(e.TypedId); });
            XF("E", i, "instances", t => HashSub(t, e.Instances, (x, v) => x.HashElementRefValue(v)));
            XF("E", i, "type", t => t.HashU32(unchecked((uint)(int)e.Type)));
            XF("E", i, "conn_src", t => HashSub(t, e.ConnectionsSrc, (x, v) => x.HashConnectionValue(v)));
            XF("E", i, "conn_dst", t => HashSub(t, e.ConnectionsDst, (x, v) => x.HashConnectionValue(v)));
        }
    }

    static UfbxScene LoadScene(string path)
    {
        var opts = new UfbxLoadOpts();
        opts.LoadExternalFiles = true; opts.IgnoreMissingExternalFiles = true;
        opts.EvaluateCaches = true; opts.EvaluateSkinning = true;
        opts.TargetAxes = UfbxCoordinateAxes.RightHandedYUp; opts.TargetUnitMeters = 1.0;
        return UfbxApi.LoadFile(path, opts, new UfbxError());
    }

    static void Trace(UfbxScene scene)
    {
        h = new UfbxHashSceneTrace();
        MetaFields(scene);
        h.HashMetadata(scene.Metadata); Show("metadata");
        h.HashSceneSettings(scene.Settings); Show("settings");
        h.HashElementRef(scene.RootNode); Show("root");
        AnimFields(scene);
        h.HashAnim(scene.Anim); Show("anim");
        ElemFields(scene);
        HashList("elements", scene.Elements, (t, v) => t.HashElement(v));
        HashList("unknowns", scene.Unknowns, (t, v) => t.HashUnknown(v));
        HashList("nodes", scene.Nodes, (t, v) => t.HashNode(v));
        HashList("meshes", scene.Meshes, (t, v) => t.HashMesh(v));
        HashList("lights", scene.Lights, (t, v) => t.HashLight(v));
        HashList("cameras", scene.Cameras, (t, v) => t.HashCamera(v));
        HashList("bones", scene.Bones, (t, v) => t.HashBone(v));
        HashList("empties", scene.Empties, (t, v) => t.HashEmpty(v));
        HashList("line_curves", scene.LineCurves, (t, v) => t.HashLineCurve(v));
        HashList("nurbs_curves", scene.NurbsCurves, (t, v) => t.HashNurbsCurve(v));
        HashList("nurbs_surfaces", scene.NurbsSurfaces, (t, v) => t.HashNurbsSurface(v));
        HashList("trim", scene.NurbsTrimSurfaces, (t, v) => t.HashNurbsTrimSurface(v));
        HashList("trimb", scene.NurbsTrimBoundaries, (t, v) => t.HashNurbsTrimBoundary(v));
        HashList("procedural", scene.ProceduralGeometries, (t, v) => t.HashProceduralGeometry(v));
        HashList("stereo", scene.StereoCameras, (t, v) => t.HashStereoCamera(v));
        HashList("switchers", scene.CameraSwitchers, (t, v) => t.HashCameraSwitcher(v));
        HashList("markers", scene.Markers, (t, v) => t.HashMarker(v));
        HashList("lod", scene.LodGroups, (t, v) => t.HashLodGroup(v));
        HashList("skins", scene.SkinDeformers, (t, v) => t.HashSkinDeformer(v));
        HashList("skin_clusters", scene.SkinClusters, (t, v) => t.HashSkinCluster(v));
        HashList("blend_def", scene.BlendDeformers, (t, v) => t.HashBlendDeformer(v));
        HashList("blend_channels", scene.BlendChannels, (t, v) => t.HashBlendChannel(v));
        HashList("blend_shapes", scene.BlendShapes, (t, v) => t.HashBlendShape(v));
        HashList("cache_def", scene.CacheDeformers, (t, v) => t.HashCacheDeformer(v));
        HashList("cache_files", scene.CacheFiles, (t, v) => t.HashCacheFile(v));
        HashList("materials", scene.Materials, (t, v) => t.HashMaterial(v));
        HashList("textures", scene.Textures, (t, v) => t.HashTexture(v));
        HashList("videos", scene.Videos, (t, v) => t.HashVideo(v));
        HashList("shaders", scene.Shaders, (t, v) => t.HashShader(v));
        HashList("shader_bindings", scene.ShaderBindings, (t, v) => t.HashShaderBinding(v));
        HashList("anim_stacks", scene.AnimStacks, (t, v) => t.HashAnimStack(v));
        HashList("anim_layers", scene.AnimLayers, (t, v) => t.HashAnimLayer(v));
        HashList("anim_values", scene.AnimValues, (t, v) => t.HashAnimValue(v));
        HashList("anim_curves", scene.AnimCurves, (t, v) => t.HashAnimCurve(v));
        HashList("display_layers", scene.DisplayLayers, (t, v) => t.HashDisplayLayer(v));
        HashList("selection_sets", scene.SelectionSets, (t, v) => t.HashSelectionSet(v));
        HashList("selection_nodes", scene.SelectionNodes, (t, v) => t.HashSelectionNode(v));
        HashList("characters", scene.Characters, (t, v) => t.HashCharacter(v));
        HashList("constraints", scene.Constraints, (t, v) => t.HashConstraint(v));
        HashList("audio_layers", scene.AudioLayers, (t, v) => t.HashAudioLayer(v));
        HashList("audio_clips", scene.AudioClips, (t, v) => t.HashAudioClip(v));
        HashList("poses", scene.Poses, (t, v) => t.HashPose(v));
        HashList("metadata_objects", scene.MetadataObjects, (t, v) => t.HashMetadataObject(v));
        HashList("texture_files", scene.TextureFiles, (t, v) => t.HashTextureFile(v));
        HashList("connections_src", scene.ConnectionsSrc, (t, v) => t.HashConnectionValue(v));
        HashList("connections_dst", scene.ConnectionsDst, (t, v) => t.HashConnectionValue(v));
        HashList("elements_by_name", scene.ElementsByName, (t, v) => t.HashNameElement(v));
        Show("FINAL");
    }

    static void TraceFile(string path)
    {
        Console.WriteLine("# " + path);
        UfbxScene scene;
        try { scene = LoadScene(path); }
        catch (Exception e) { Console.WriteLine("# EX " + e.GetType().Name + " " + e.Message); return; }
        if (scene == null) { Console.WriteLine("# FAIL"); return; }
        if (Environment.GetEnvironmentVariable("S3BC_KEYDUMP") == "1") KeyDump(scene);
        if (Environment.GetEnvironmentVariable("S3BC_MESHDUMP") == "1") MeshDump(scene);
        if (Environment.GetEnvironmentVariable("S3BC_VECRAW") == "1") VecRaw(scene);
        if (Environment.GetEnvironmentVariable("S3BC_TOPODBG") == "1") ChanDbg(scene);
        if (Environment.GetEnvironmentVariable("S3BC_UVDUMP") == "1") UvDump(scene);
        if (Environment.GetEnvironmentVariable("S3BC_WARNDBG") == "1") WarnDump(scene);
        if (Environment.GetEnvironmentVariable("S3BC_PARTDUMP") == "1") PartDump(scene);
        Trace(scene);
    }

    // Mirrors the S3BC_PARTDUMP block of tools/_s3bc_hashtrace.c.
    static void PartDump(UfbxScene scene)
    {
        UfbxMesh[] meshes = scene.Meshes;
        for (int mi = 0; mi < Cnt(meshes); mi++) {
            UfbxMesh m = meshes[mi];
            UfbxMeshPart[][] kinds = new UfbxMeshPart[][] { m.MaterialParts, m.FaceGroupParts };
            string[] names = new string[] { "mp", "fg" };
            for (int kind = 0; kind < 2; kind++) {
                UfbxMeshPart[] pl = kinds[kind];
                for (int pi = 0; pi < Cnt(pl); pi++) {
                    UfbxMeshPart p = pl[pi];
                    Console.Write("R " + mi + " " + names[kind] + " " + pi + " idx=" + p.Index +
                        " nf=" + p.NumFaces + " nt=" + p.NumTriangles + " ne=" + p.NumEmptyFaces +
                        " np=" + p.NumPointFaces + " nl=" + p.NumLineFaces +
                        " fic=" + Cnt(p.FaceIndices) + " [");
                    for (int fi = 0; fi < Cnt(p.FaceIndices); fi++) Console.Write(p.FaceIndices[fi] + ",");
                    Console.WriteLine("]");
                }
            }
        }
    }

    // Mirrors chan_dbg() in tools/_s3bc_hashtrace.c (env S3BC_TOPODBG=1): what
    // ufbxi_evaluate_skinning sees per mesh, plus how many vec3 the sampler returns.
    static void ChanDbg(UfbxScene scene)
    {
        UfbxCacheFile[] cfs = scene.CacheFiles;
        for (int i = 0; i < Cnt(cfs); i++) {
            Console.WriteLine("X " + i + " filename=[" + (cfs[i].Filename ?? "") + "] extcache=" +
                (cfs[i].ExternalCache != null ? "1" : "0") + " abs=[" + (cfs[i].AbsoluteFilename ?? "") + "]");
        }
        UfbxCacheDeformer[] all = scene.CacheDeformers;
        for (int i = 0; i < Cnt(all); i++) {
            Console.WriteLine("Y " + i + " want=[" + (all[i].Channel ?? "") + "] file=" +
                (all[i].File != null ? "1" : "0") + " fileext=" + (all[i].File != null && all[i].File.ExternalCache != null ? "1" : "0") +
                " extch=" + (all[i].ExternalChannel != null ? "1" : "0"));
        }
        UfbxMesh[] meshes = scene.Meshes;
        for (int mi = 0; mi < Cnt(meshes); mi++) {
            UfbxMesh m = meshes[mi];
            UfbxCacheDeformer[] cds = m.CacheDeformers;
            for (int ci = 0; cds != null && ci < cds.Length; ci++) {
                UfbxCacheChannel ch = cds[ci].ExternalChannel;
                UfbxGeometryCache gc = cds[ci].File != null ? cds[ci].File.ExternalCache : null;
                Console.WriteLine("N " + mi + " " + ci + " want=" + (cds[ci].Channel ?? "") + " extch=" +
                    (ch != null ? "1" : "0") + " chans=" + (gc != null ? Cnt(gc.Channels) : -1) +
                    (gc != null ? " " + string.Join(" ", System.Array.ConvertAll(gc.Channels, c => "[" + c.Name + "]")) : ""));
                if (ch == null) { Console.WriteLine("D " + mi + " " + ci + " NULL"); continue; }
                int nf = Cnt(ch.Frames);
                uint dc0 = nf > 0 ? ch.Frames[0].DataCount : 0u;
                uint sec0 = nf > 0 ? ch.Frames[0].DataElementBytes : 0u;
                Console.WriteLine("D " + mi + " " + ci + " interp=" + (int) ch.Interpretation + " frames=" + nf +
                    " dc0=" + dc0 + " sec0=" + sec0 + " req=" + Cnt(m.SkinnedNormal.Values));
                if (nf > 0 && dc0 >= 3) {
                    int nvec = (int) (dc0 / 3) + 8;
                    UfbxVec3[] tmp = new UfbxVec3[nvec];
                    for (int i = 0; i < nvec; i++) tmp[i] = UfbxVec3.Zero;
                    UfbxGeometryCacheDataOpts co = new UfbxGeometryCacheDataOpts();
                    int got = UfbxiGeometryCacheSample.SampleGeometryCacheVec3(ch, 0.0, tmp, 0, nvec, co);
                    Console.WriteLine("D " + mi + " " + ci + " got=" + got + " [" + DBits(tmp[0].X) + "," +
                        DBits(tmp[0].Y) + "," + DBits(tmp[0].Z) + "]");
                }
            }
        }
    }

    static int Cnt<T>(T[] a) { return a != null ? a.Length : 0; }

    // Mirrors mesh_fields() in tools/_s3bc_hashtrace.c: one `G <mesh> <group> <hash>` line per
    // mesh field group, each hashed from the initial FNV state.
    static void Field(int mi, string name, Action<UfbxHashSceneTrace> body)
    {
        var t = new UfbxHashSceneTrace();
        body(t);
        Console.WriteLine("G " + mi.ToString(CultureInfo.InvariantCulture) + " " + name + " " + Hex(t.State));
    }

    static void ListEach<T>(UfbxHashSceneTrace t, T[] items, Action<UfbxHashSceneTrace, T> fn)
    {
        int n = Cnt(items);
        t.HashSizeT(n);
        for (int i = 0; i < n; i++) fn(t, items[i]);
    }

    static void MeshDump(UfbxScene scene)
    {
        UfbxMesh[] meshes = scene.Meshes;
        int nm = Cnt(meshes);
        for (int mi = 0; mi < nm; mi++) {
            UfbxMesh m = meshes[mi];
            Field(mi, "counts", t => { t.HashSizeT(m.NumVertices); t.HashSizeT(m.NumIndices); t.HashSizeT(m.NumFaces); t.HashSizeT(m.NumTriangles); t.HashSizeT(m.NumEdges); });
            Field(mi, "faces", t => ListEach(t, m.Faces, (x, v) => x.HashFace(v)));
            Field(mi, "face_smoothing", t => ListEach(t, m.FaceSmoothing, (x, v) => x.HashBool(v)));
            Field(mi, "face_material", t => ListEach(t, m.FaceMaterial, (x, v) => x.HashU32(v)));
            Field(mi, "face_group", t => ListEach(t, m.FaceGroup, (x, v) => x.HashU32(v)));
            Field(mi, "face_hole", t => ListEach(t, m.FaceHole, (x, v) => x.HashBool(v)));
            Field(mi, "face_counts", t => { t.HashSizeT(m.MaxFaceTriangles); t.HashSizeT(m.NumEmptyFaces); t.HashSizeT(m.NumPointFaces); t.HashSizeT(m.NumLineFaces); });
            Field(mi, "edges", t => ListEach(t, m.Edges, (x, v) => x.HashEdge(v)));
            Field(mi, "edge_smoothing", t => ListEach(t, m.EdgeSmoothing, (x, v) => x.HashBool(v)));
            Field(mi, "edge_crease", t => ListEach(t, m.EdgeCrease, (x, v) => x.HashReal(v)));
            Field(mi, "edge_visibility", t => ListEach(t, m.EdgeVisibility, (x, v) => x.HashBool(v)));
            Field(mi, "vertex_indices", t => ListEach(t, m.VertexIndices, (x, v) => x.HashU32(v)));
            Field(mi, "vertices", t => ListEach(t, m.Vertices, (x, v) => x.HashVec3(v)));
            Field(mi, "vertex_first_index", t => ListEach(t, m.VertexFirstIndex, (x, v) => x.HashU32(v)));
            Field(mi, "vertex_position", t => t.HashVertexVec3(m.VertexPosition));
            Field(mi, "vertex_normal", t => t.HashVertexVec3(m.VertexNormal));
            Field(mi, "vertex_uv", t => t.HashVertexVec2(m.VertexUv));
            Field(mi, "vertex_tangent", t => t.HashVertexVec3(m.VertexTangent));
            Field(mi, "vertex_bitangent", t => t.HashVertexVec3(m.VertexBitangent));
            Field(mi, "vertex_color", t => t.HashVertexVec4(m.VertexColor));
            Field(mi, "vertex_crease", t => t.HashVertexReal(m.VertexCrease));
            Field(mi, "uv_sets", t => ListEach(t, m.UvSets, (x, v) => x.HashUvSet(v)));
            Field(mi, "color_sets", t => ListEach(t, m.ColorSets, (x, v) => x.HashColorSet(v)));
            Field(mi, "materials", t => ListEach(t, m.Materials, (x, v) => x.HashElementRefValue(v)));
            Field(mi, "face_groups", t => ListEach(t, m.FaceGroups, (x, v) => x.HashFaceGroup(v)));
            Field(mi, "material_parts", t => ListEach(t, m.MaterialParts, (x, v) => x.HashMeshPart(v)));
            Field(mi, "face_group_parts", t => ListEach(t, m.FaceGroupParts, (x, v) => x.HashMeshPart(v)));
            Field(mi, "skinned", t => { t.HashBool(m.SkinnedIsLocal); t.HashVertexVec3(m.SkinnedPosition); t.HashVertexVec3(m.SkinnedNormal); });
            Field(mi, "skin_deformers", t => ListEach(t, m.SkinDeformers, (x, v) => x.HashElementRefValue(v)));
            Field(mi, "blend_deformers", t => ListEach(t, m.BlendDeformers, (x, v) => x.HashElementRefValue(v)));
            Field(mi, "cache_deformers", t => ListEach(t, m.CacheDeformers, (x, v) => x.HashElementRefValue(v)));
            Field(mi, "all_deformers", t => ListEach(t, m.AllDeformers, (x, v) => x.HashElementRefValue(v)));
            Field(mi, "subdivision", t => { t.HashU32(m.SubdivisionPreviewLevels); t.HashU32(m.SubdivisionRenderLevels);
                t.HashU32(unchecked((uint)(int)m.SubdivisionDisplayMode)); t.HashU32(unchecked((uint)(int)m.SubdivisionBoundary));
                t.HashU32(unchecked((uint)(int)m.SubdivisionUvBoundary)); });
            Field(mi, "subdivision_result", t => { t.HashBool(m.SubdivisionEvaluated); if (m.SubdivisionResult != null) t.HashSubdivisionResult(m.SubdivisionResult); });
            Field(mi, "tail", t => t.HashBool(m.FromTessellatedNurbs));
        }
    }

    // Mirrors vec_raw() in tools/_s3bc_hashtrace.c: raw metadata and bit patterns of the
    // per-vertex attribute buffers that can diverge (`Q`/`q`/`n`/`S` lines, same order).
    static void VecRaw(UfbxScene scene)
    {
        UfbxMesh[] meshes = scene.Meshes;
        int nm = Cnt(meshes);
        for (int mi = 0; mi < nm; mi++) {
            UfbxMesh m = meshes[mi];
            Console.WriteLine("F " + mi.ToString(CultureInfo.InvariantCulture) + " skin=" + Cnt(m.SkinDeformers) +
                " blend=" + Cnt(m.BlendDeformers) + " cache=" + Cnt(m.CacheDeformers) + " all=" + Cnt(m.AllDeformers));
            VV3(mi, "vertex_position", m.VertexPosition);
            VV3(mi, "vertex_normal", m.VertexNormal);
            VV3(mi, "vertex_tangent", m.VertexTangent);
            VV3(mi, "vertex_bitangent", m.VertexBitangent);
            Console.WriteLine("S " + mi.ToString(CultureInfo.InvariantCulture) + " skinned_is_local " + (m.SkinnedIsLocal ? "1" : "0"));
            VV3(mi, "skinned_position", m.SkinnedPosition);
            VV3(mi, "skinned_normal", m.SkinnedNormal);
        }
    }

    static void VV3(int mi, string name, UfbxVertexVec3 p)
    {
        Console.WriteLine("Q " + mi + " " + name + " " + (p.Exists ? "1" : "0") + " " +
            Cnt(p.Values) + " " + Cnt(p.Indices) + " " + p.ValueReals + " " +
            (p.UniquePerVertex ? "1" : "0") + " " + Cnt(p.ValuesW));
        for (int i = 0; i < Cnt(p.Values); i++) {
            UfbxVec3 v = p.Values[i];
            Console.WriteLine("q " + mi + " " + name + " " + i + " " + DBits(v.X) + " " + DBits(v.Y) + " " + DBits(v.Z));
        }
        for (int i = 0; i < Cnt(p.Indices); i++) {
            Console.WriteLine("n " + mi + " " + name + " " + i + " " +
                p.Indices[i].ToString("x8", CultureInfo.InvariantCulture));
        }
    }

    static void VV2(int mi, string name, UfbxVertexVec2 p)
    {
        Console.WriteLine("U " + mi + " " + name + " " + (p.Exists ? "1" : "0") + " " +
            Cnt(p.Values) + " " + Cnt(p.Indices) + " " + p.ValueReals + " " +
            (p.UniquePerVertex ? "1" : "0"));
        for (int i = 0; i < Cnt(p.Values); i++) {
            UfbxVec2 uv = p.Values[i];
            Console.WriteLine("u " + mi + " " + name + " " + i + " " + DBits(uv.X) + " " + DBits(uv.Y));
        }
        for (int i = 0; i < Cnt(p.Indices); i++) {
            Console.WriteLine("v " + mi + " " + name + " " + i + " " +
                p.Indices[i].ToString("x8", CultureInfo.InvariantCulture));
        }
    }

    // Mirrors uv_dump() in tools/_s3bc_hashtrace.c (env S3BC_UVDUMP=1).
    static void UvDump(UfbxScene scene)
    {
        UfbxMesh[] meshes = scene.Meshes;
        int nm = Cnt(meshes);
        for (int mi = 0; mi < nm; mi++) {
            UfbxMesh m = meshes[mi];
            Console.WriteLine("H " + mi + " num_vertices=" + m.NumVertices + " num_indices=" + m.NumIndices +
                " uv_sets=" + Cnt(m.UvSets) + " color_sets=" + Cnt(m.ColorSets));
            VV2(mi, "mesh_uv", m.VertexUv);
            for (int s = 0; s < Cnt(m.UvSets); s++) {
                UfbxUvSet set = m.UvSets[s];
                Console.WriteLine("Z " + mi + " " + s + " index=" + set.Index + " name=[" + (set.Name ?? "") + "]");
                VV2(mi, "set" + s + "_uv", set.VertexUv);
                VV3(mi, "set" + s + "_tan", set.VertexTangent);
                VV3(mi, "set" + s + "_bit", set.VertexBitangent);
            }
        }
    }

    // Mirrors the S3BC_KEYDUMP block of tools/_s3bc_hashtrace.c: raw bit patterns of every
    // keyframe field, so a mismatch names the field instead of just the object.
    static string DBits(double d) { return BitConverter.DoubleToInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture); }
    static string FBits(float f) { return ((uint)BitConverter.SingleToInt32Bits(f)).ToString("x8", CultureInfo.InvariantCulture); }

    static void KeyDump(UfbxScene scene)
    {
        UfbxAnimCurve[] curves = scene.AnimCurves;
        int nc = curves != null ? curves.Length : 0;
        for (int ci = 0; ci < nc; ci++) {
            UfbxAnimCurve c = curves[ci];
            UfbxKeyframe[] kf = c.Keyframes;
            int n = kf != null ? kf.Length : 0;
            Console.WriteLine("A " + ci + " " + n);
            for (int ki = 0; ki < n; ki++) {
                Console.WriteLine("K " + ci + " " + ki + " " + DBits(kf[ki].Time) + " " + DBits(kf[ki].Value) +
                    " " + (int)kf[ki].Interpolation + " " + FBits(kf[ki].Left.Dx) + " " + FBits(kf[ki].Left.Dy) +
                    " " + FBits(kf[ki].Right.Dx) + " " + FBits(kf[ki].Right.Dy));
            }
            Console.WriteLine("N " + ci + " " + DBits(c.MinValue) + " " + DBits(c.MaxValue));
        }
    }

    // Mirrors warn_dump() in tools/_s3bc_hashtrace.c (env S3BC_WARNDBG=1).
    static void WarnDump(UfbxScene scene)
    {
        bool[] hw = scene.Metadata.HasWarning;
        int n = hw != null ? hw.Length : 0;
        for (int i = 0; i < n; i++) {
            Console.WriteLine("W " + i + " " + (hw[i] ? "1" : "0"));
        }
        UfbxWarning[] ws = scene.Metadata.Warnings;
        for (int i = 0; i < Cnt(ws); i++) {
            UfbxWarning w = ws[i];
            Console.WriteLine("R " + i + " type=" + (int)w.Type + " elem=" + w.ElementId +
                " count=" + w.Count + " desc=[" + (w.Description ?? "") + "]");
        }
    }

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "goldens") {
            string dataDir = args.Length > 2 ? args[2] : @"C:\Workspace\_analyze_ufbx";
            foreach (string raw in File.ReadAllLines(args[1])) {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new[] { ' ' }, 3);
                if (parts.Length != 3) continue;
                if (!ulong.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong want)) continue;
                if (!int.TryParse(parts[1], out int frame)) continue;
                if (frame != 0) continue;
                string full = Path.Combine(dataDir, parts[2].Replace('/', Path.DirectorySeparatorChar));
                UfbxScene scene;
                try { scene = LoadScene(full); }
                catch (Exception e) { Console.WriteLine("ERR " + parts[2] + " " + parts[0] + " " + e.GetType().Name); continue; }
                if (scene == null) { Console.WriteLine("ERR " + parts[2] + " " + parts[0] + " null"); continue; }
                ulong got = UfbxHashSceneTrace.HashScene(scene);
                Console.WriteLine((got == want ? "MATCH " : "DIFF ") + parts[2] + " " + parts[0] + " " + Hex(got));
            }
            return 0;
        }

        if (args.Length >= 2 && args[0] == "trace") {
            for (int i = 1; i < args.Length; i++) TraceFile(args[i]);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "trace-list") {
            foreach (string p in File.ReadAllLines(args[1])) {
                string path = p.Trim();
                if (path.Length == 0) continue;
                TraceFile(path);
            }
            return 0;
        }

        // `conns <path>...` -- print the resolved global connection list as
        // `C <i> <src_id> <dst_id> <src_type> <dst_type> [src_prop] [dst_prop]`, to compare against
        // the `connections_src:` section of the C field dump.
        if (args.Length >= 2 && args[0] == "conns") {
            for (int i = 1; i < args.Length; i++) {
                Console.WriteLine("# " + args[i]);
                UfbxScene scene;
                try { scene = LoadScene(args[i]); }
                catch (Exception e) { Console.WriteLine("# EX " + e.GetType().Name + " " + e.Message); continue; }
                if (scene == null) { Console.WriteLine("# FAIL"); continue; }
                UfbxConnection[] cs = scene.ConnectionsSrc;
                int n = Cnt(cs);
                for (int k = 0; k < n; k++) {
                    UfbxConnection c = cs[k];
                    Console.WriteLine("C " + k.ToString(CultureInfo.InvariantCulture) + " " +
                        Id(c.Src) + " " + Id(c.Dst) + " " + Type(c.Src) + " " + Type(c.Dst) + " " +
                        "[" + c.SrcProp + "] [" + c.DstProp + "]");
                }
            }
            return 0;
        }

        Console.Error.WriteLine("usage: S3Dbg goldens <golden_file> [data_dir] | trace <path>... | trace-list <file> | conns <path>...");
        return 4;
    }

    static string Id(UfbxElement e)
    {
        return e == null ? "null" : e.ElementId.ToString(CultureInfo.InvariantCulture);
    }

    static string Type(UfbxElement e)
    {
        return e == null ? "null" : ((int)e.Type).ToString(CultureInfo.InvariantCulture);
    }
}
