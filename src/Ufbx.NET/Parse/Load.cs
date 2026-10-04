// Load driver, ported from ufbx.c v0.23.1:
//   ufbxi_load()                 (ufbx.c:25478-25632)
//   ufbxi_load_imp()             (ufbx.c:25212-25455)
//   ufbxi_determine_format()     (ufbx.c:11126-11187)
//   ufbxi_is_format()            (ufbx.c:11092-11121)
//   ufbxi_next_line()            (ufbx.c:10853-10876)
//   ufbxi_match_skip/imp/match() (ufbx.c:10879-11089)
//   ufbxi_fixup_opts_string()    (ufbx.c:25179-25193)
//   ufbxi_load_strings()         (ufbx.c:11407-11424)  -> UfbxiStringPool.LoadStrings
//   ufbxi_load_maps()            (ufbx.c:11742-11756)
//   ufbxi_supports_version()     (ufbx.c:15842-15845)
//   ufbxi_thread_pool_init()     (ufbx.c:6067-6089)
//   ufbxi_free_temp()            (ufbx.c:25437-25455)
//   ufbxi_prop_type_names[]      (ufbx.c:11426-11465)
// plus the deferred file open of `ufbx_load_file()` (ufbx.c:25215-25252).
//
// SCOPE: this is the load *spine*. It stops at the toplevel scene-content readers
// (`ufbxi_read_root` / `ufbxi_read_legacy_root` / `ufbxi_obj_load` / `ufbxi_mtl_load`),
// which are the single narrow seam declared in `UfbxiToplevel` below: every seam method
// throws `UfbxiReaderNotPortedException`, so nothing can silently "succeed" with an empty
// scene. `UfbxiDom.BeginParse` (Parse/DomNode.cs) is reused for `ufbxi_begin_parse()`; it is
// NOT duplicated here.
//
// ERROR 口径 (see Parse/Error.cs): new failure sites use `UfbxiFail.CheckMsg/FailMsg` for C's
// `ufbxi_check_msg`/`ufbxi_fail_msg` (these carry `error.description`) and
// `UfbxiFail.CheckNoDesc/FailNoDesc` for C's plain `ufbxi_check`/`ufbxi_fail` (these carry
// none, so `ufbxi_fix_error_type()` reports the default "Failed to load"). The single place
// that turns a failure into a `UfbxError` is the top-level catch of `UfbxApi` (src/Ufbx.NET/Api),
// which reproduces the tail of `ufbxi_load()` (ufbx.c:25613-25630).
//
// Not ported on purpose (PORTING_NOTES.md #4): the arenas/`ufbxi_buf` bookkeeping. `C 指针序 ≡
// 分配序` is reproduced instead by the order in which this file drives the string pool:
// `ufbxi_load()` sets the pool up (25549-25556), `ufbxi_fixup_opts_string()` interns the
// option strings (25262-25265), `ufbxi_load_strings()` interns the `ufbxi_*` constants
// (25294) and `ufbxi_load_maps()` the property type names (25295) -- in exactly that order.

using System;

namespace Ufbx.NET
{
    // The dedicated internal marker for "the load spine reached functionality that is out of
    // scope for this port" (the scene-content readers and everything past them). It is
    // deliberately NOT a `UfbxParseError`: C has no such failure, so it must never be turned
    // into a `ufbx_error`. `UfbxApi` re-exports it, and the differential harness treats a
    // file that reaches it as "reader seam reached".
    internal sealed class UfbxiReaderNotPortedException : Exception
    {
        // C: the name of the function that would have continued.
        public readonly string Reader;

        public UfbxiReaderNotPortedException(string reader)
            : base("ufbx reader not ported: " + reader)
        {
            Reader = reader;
        }
    }

    // C: the toplevel scene-content readers -- the single seam of this port.
    //   ufbxi_read_root()        (ufbx.c:15847)  FBX >= 6000 DOM tree
    //   ufbxi_read_legacy_root() (ufbx.c:15665)  FBX < 6000 legacy tree
    //   ufbxi_obj_load()         (ufbx.c:17795)  OBJ
    //   ufbxi_mtl_load()         (ufbx.c:18060ish) MTL
    // Everything they pull in (definitions, objects, connections, properties, elements, scene
    // build) is out of scope; each stub reports through `NotPorted`.
    internal static class UfbxiToplevel
    {
        internal static void ReadRoot(UfbxiContext uc)
        {
            UfbxiRoot.ReadRoot(uc);
        }

        internal static void ReadLegacyRoot(UfbxiContext uc)
        {
            UfbxiRoot.ReadLegacyRoot(uc);
        }

        internal static void ObjLoad(UfbxiContext uc)
        {
            UfbxiObj.ObjLoad(uc);
        }

        internal static void MtlLoad(UfbxiContext uc)
        {
            UfbxiObj.MtlLoad(uc);
        }

        // C: the scene build past the readers (ufbx.c:25328-25373). `ufbxi_pre_finalize_scene`
        // (18115) is ported in Parse/SceneBuild.cs; the finalize/update chain is ported in
        // Parse/SceneFinalize.cs + Parse/SceneUpdate.cs; `ufbxi_update_scene_settings_obj`
        // (ufbx.c:23936-23947) is ported in Parse/GeometryCache.cs (S4a).
        internal static void SceneBuild(UfbxiContext uc)
        {
            // C: ufbxi_update_scene_metadata(&uc->scene.metadata); (ufbx.c:25310)
            UfbxiSceneUpdate.UpdateSceneMetadata(uc.Scene.Metadata);
            // C: ufbxi_check(ufbxi_init_file_paths(uc)); (ufbx.c:25311, ported for OBJ/MTL in
            // Parse/Legacy.cs -- `metadata.filename`/`relative_root` feed resolve_filenames)
            UfbxiLegacy.InitFilePaths(uc);

            // C: ufbxi_check(ufbxi_pre_finalize_scene(uc)); ufbxi_buf_free(&uc->tmp_parse);
            UfbxiSceneBuild.PreFinalizeScene(uc);

            // C: ufbxi_check(ufbxi_finalize_scene(uc)); (ufbx.c:25333)
            UfbxiSceneUpdate.FinalizeScene(uc);

            // C: ufbxi_update_scene_settings(&uc->scene.settings); (ufbx.c:25335)
            UfbxiSceneUpdate.UpdateSceneSettings(uc.Scene.Settings);
            if (uc.Scene.Metadata.FileFormat == UfbxFileFormat.Obj) {
                // C: ufbxi_update_scene_settings_obj(uc); (ufbx.c:23936-23947, ported by S4a
                // in Parse/GeometryCache.cs)
                UfbxiGeometryCacheLoader.UpdateSceneSettingsObj(uc);
            }

            // C: axis conversion (ufbx.c:25340-25343).
            if (UfbxCoordinateAxes.IsValid(uc.Opts.TargetAxes)) {
                UfbxiSceneOpts.TransformToAxes(uc, uc.Opts.TargetAxes);
            }

            // C: unit conversion (ufbx.c:25345-25348).
            if (uc.Opts.TargetUnitMeters > 0.0) {
                UfbxiFail.CheckNoDesc(UfbxiSceneOpts.ScaleUnits(uc, uc.Opts.TargetUnitMeters),
                    "ufbxi_scale_units(uc, uc->opts.target_unit_meters)");
            }

            // C: ufbxi_update_adjust_transforms(uc, &uc->scene); (ufbx.c:25351)
            UfbxiSceneUpdate.UpdateAdjustTransforms(uc, uc.Scene);

            // C: ufbxi_check(ufbxi_modify_geometry(uc)); ufbxi_postprocess_scene(uc); (25353-25354)
            UfbxiSceneFinalize.ModifyGeometry(uc);
            UfbxiSceneFinalize.PostprocessScene(uc);

            // C: ufbxi_update_scene(&uc->scene, true, NULL, 0); (ufbx.c:25356)
            UfbxiSceneUpdate.UpdateScene(uc.Scene, true, null);

            // C: force a non-NULL anim pointer (ufbx.c:25358-25361).
            if (uc.Scene.Anim == null) {
                uc.Scene.Anim = new UfbxAnim();
            }            // C: if (uc->opts.load_external_files) ufbxi_check(ufbxi_load_external_files(uc));
            // (ufbx.c:25363-25365)
            if (uc.Opts.LoadExternalFiles) {
                UfbxiFail.CheckNoDesc(UfbxiGeometryCacheLoader.LoadExternalFiles(uc),
                    "ufbxi_load_external_files(uc)");
            }

            // C: evaluate skinning if requested (ufbx.c:25367-25373).
            if (uc.Opts.EvaluateSkinning) {
                UfbxGeometryCacheDataOpts cacheOpts = new UfbxGeometryCacheDataOpts();
                cacheOpts.OpenFileCb = uc.Opts.OpenFileCb;
                UfbxiSceneOpts.EvaluateSkinning(uc.Scene, uc.Error, 0.0,
                    uc.Opts.LoadExternalFiles && uc.Opts.EvaluateCaches, cacheOpts);
            }

            // PORT-ONLY (PORTING_NOTES "哨兵索引数组"): every operation that relies on the
            // shared zero/consecutive buffer *identity* (ufbxi_patch_index_pointer,
            // ufbxi_flip_attrib_winding, ufbxi_finalize_mesh_material, evaluate_skinning) has
            // run by here, so the sentinel lists are materialized into right-sized per-field
            // copies. Content is identical to C's shared buffers; only the port-side `Length`
            // (which C expresses as the list `count`) needed the real per-mesh sizes.
            UfbxiSceneUpdate.MaterializeSentinelIndexLists(uc);

            // C: pop warnings to metadata + resolve their element ids (ufbx.c:25375-25377).
            uc.Scene.Metadata.Warnings = UfbxiWarnings.PopWarnings(uc.Warnings,
                uc.Scene.Metadata.HasWarning);
            UfbxiFail.CheckNoDesc(UfbxiSceneOpts.ResolveWarningElements(uc),
                "ufbxi_resolve_warning_elements(uc)");

            // C: copy local data to the scene (ufbx.c:25379-25385).
            uc.Scene.Metadata.Version = uc.Version;
            uc.Scene.Metadata.Ascii = uc.FromAscii;
            uc.Scene.Metadata.BigEndian = uc.FileBigEndian;
            uc.Scene.Metadata.GeometryIgnored = uc.Opts.IgnoreGeometry;
            uc.Scene.Metadata.AnimationIgnored = uc.Opts.IgnoreAnimation;
            uc.Scene.Metadata.EmbeddedIgnored = uc.Opts.IgnoreEmbedded;

            // C: ufbx.c:25387-25430 (`ufbxi_scene_imp` bookkeeping, memory stats and the
            // `element->scene` back-pointer sweep). The arena/allocator stats have no C#
            // counterpart, but the sweep does and evaluation needs it: `ufbxi_extrapolate_curve`
            // reads `curve->element.scene->metadata.ktime_second` (ufbx.c:26013), so every
            // element gets its back-pointer here, exactly like C 25411-25413.
            {
                UfbxElement[] elements = uc.TmpElementPtrs.ToArray();
                for (int i = 0; i < elements.Length; i++) {
                    elements[i].Scene = uc.Scene;
                }
            }
        }

        internal static void NotPorted(string what)
        {
            throw new UfbxiReaderNotPortedException(what);
        }
    }

    internal static class UfbxiLoad
    {
        // C: #define UFBXI_MIN_FILE_FORMAT_LOOKAHEAD 32 (ufbx.c:58)
        internal const int MinFileFormatLookahead = 32;

        // C: #define UFBXI_BINARY_MAGIC_SIZE 22 / UFBXI_BINARY_HEADER_SIZE 27 (ufbx.c:9395-9396)
        internal const int BinaryMagicSize = 22;
        internal const int BinaryHeaderSize = 27;

        // C: static const char ufbxi_binary_magic[] = "Kaydara FBX Binary  \x00\x1a"
        // (ufbx.c:9397). Raw-byte string: 20 characters, then 0x00 and 0x1a.
        static readonly string BinaryMagicString = MakeBinaryMagic();

        static string MakeBinaryMagic()
        {
            const string text = "Kaydara FBX Binary  ";
            char[] chars = new char[BinaryMagicSize];
            for (int i = 0; i < text.Length; i++) chars[i] = text[i];
            chars[text.Length] = '\0';
            chars[text.Length + 1] = (char)0x1a;
            return new string(chars);
        }

        // C: #define UFBX_PATH_SEPARATOR '\\' on Windows (ufbx.c:601-605); the reference
        // binaries are Windows builds, and `UfbxLoadOpts.CreateDefault()` uses the same value.
        internal const char PathSeparatorChar = '\\';

        // C: SIZE_MAX
        internal const ulong MaxSizeT = ulong.MaxValue;

        // C: #define UFBXI_SYNTHETIC_ID_START (UFBXI_POINTER_ID_START +
        // UFBXI_MAXIMUM_FAST_POINTER_ID) = 0x8000000000000000 + 0x4000000000000000
        // (ufbx.c:12218-12223), assigned at ufbx.c:25547.
        internal const ulong SyntheticIdStart = 0xC000000000000000;

        // C: #define UFBXI_LEGACY_MAX_VERSION 6000 -- `uc->version < 6000` picks
        // `ufbxi_read_legacy_root()` (ufbx.c:25303-25305).
        internal const uint LegacyVersionLimit = 6000;

        // C: the `default_desc` of `ufbxi_fix_error_type(&uc->error, "Failed to load", p_error)`
        // (ufbx.c:25623).
        internal const string DefaultErrorDescription = "Failed to load";

        // C: static const char ufbxi_zero_size_buffer[] (ufbx.c:3623-3625) -- a non-NULL
        // zero-length buffer, used to tell "empty" from "allocation failed".
        static readonly byte[] ZeroSizeBuffer = new byte[0];

        // C: `const char *ufbxi_empty_char` (ufbx.c:3370) == the port's string.Empty.
        const string EmptyChar = "";

        // ------------------------------------------------------------------
        // ufbxi_match(): the compile-time pattern matcher used by format sniffing
        // ------------------------------------------------------------------

        // C: ufbxi_match() (ufbx.c:11081-11089). `str` is a raw-byte string slice
        // (PORTING_NOTES.md "raw-byte 字符串"): 1 char == 1 byte, so a C `char` comparison and
        // a `char` comparison agree for every byte value (all patterns are ASCII, so a byte
        // >= 0x80 matches nothing but `.` and `\S`, exactly like C's negative `char`).
        internal static bool Match(string data, int offset, int length, string pattern)
        {
            int str = offset;
            int end = offset + length;
            int fmt = 0;
            if (MatchImp(data, ref str, end, pattern, ref fmt)) {
                return str == end;
            }
            return false;
        }

        static char Pat(string pattern, int index)
        {
            // C: the pattern literals are NUL-terminated, so reading the terminator yields 0
            // and anything past it is never reached by these patterns.
            if (index < 0) index = 0;
            return index < pattern.Length ? pattern[index] : '\0';
        }

        // C: ufbxi_match_skip() (ufbx.c:10880-10912). Returns the index of the terminating
        // '|' (when `alternation`) or of the closing ')'/NUL.
        static int MatchSkip(string fmt, int fmtIo, bool alternation)
        {
            int fmtPos = fmtIo;
            for (;;) {
                char c = Pat(fmt, fmtPos++);
                switch (c) {
                    case '(':
                        fmtPos = MatchSkip(fmt, fmtPos, false) + 1;
                        break;
                    case '\\':
                        fmtPos++;
                        break;
                    case '[':
                        c = Pat(fmt, fmtPos);
                        while (c != ']') {
                            c = Pat(fmt, fmtPos++);
                            if (c == '\\') {
                                c = Pat(fmt, fmtPos++);
                            }
                        }
                        fmtPos++;
                        break;
                    case '|':
                        if (alternation) return fmtPos - 1;
                        break;
                    case ')':
                    case '\0':
                        return fmtPos - 1;
                    default:
                        break;
                }
            }
        }

        // C: ufbxi_match_imp() (ufbx.c:10915-10215ish, exact range 10915-10076+). `p_str` and
        // `p_fmt` are C's in/out pointers, expressed as indices into `data`/`fmt`.
        static bool MatchImp(string data, ref int strIo, int end, string fmt, ref int fmtIo)
        {
            int strOriginalBegin = strIo;
            int str = strIo;
            int fmtBegin = fmtIo;
            int fmtPos = fmtIo;
            bool caseInsensitive = false;

            int count = 0;
            for (;;) {
                char c = Pat(fmt, fmtPos++);
                if (c == '\0') {
                    strIo = str;
                    fmtIo = fmtPos - 1;
                    return true;
                }

                int strBegin = str;
                int chRef = str != end ? data[str] : 0;

                if (caseInsensitive) {
                    if (chRef >= 'A' && chRef <= 'Z') {
                        chRef = (chRef - 'A') + 'a';
                    }
                }

                bool ok = false;
                switch (c) {

                    case '\\': {
                        string macro = null;
                        int macroPos = 0;
                        c = Pat(fmt, fmtPos++);
                        switch (c) {
                            case 'd':
                                macro = "[0-9]";
                                break;
                            case 'F':
                                macro = "[\\-+]?[0-9]+(\\.[0-9]+)?([eE][\\-+]?[0-9]+)?";
                                break;
                            case 's':
                                if (UfbxiAscii.IsSpace((char)chRef)) {
                                    ok = true;
                                    str++;
                                }
                                break;
                            case 'S':
                                if (!UfbxiAscii.IsSpace((char)chRef)) {
                                    ok = true;
                                    str++;
                                }
                                break;
                            case 'c':
                            case 'C':
                                caseInsensitive = c == 'c';
                                ok = true;
                                break;
                            default:
                                if (chRef == c) {
                                    ok = true;
                                    str++;
                                }
                                break;
                        }
                        if (macro != null) {
                            macroPos = 0;
                            ok = MatchImp(data, ref str, end, macro, ref macroPos);
                        }
                    } break;

                    case '[': {
                        while (Pat(fmt, fmtPos) != ']') {
                            if (Pat(fmt, fmtPos) == '\\') {
                                if (chRef == Pat(fmt, fmtPos + 1)) ok = true;
                                fmtPos += 2;
                            } else if (Pat(fmt, fmtPos + 1) == '-') {
                                if (chRef >= Pat(fmt, fmtPos) && chRef <= Pat(fmt, fmtPos + 2)) {
                                    ok = true;
                                }
                                fmtPos += 3;
                            } else {
                                if (chRef == Pat(fmt, fmtPos)) ok = true;
                                fmtPos += 1;
                            }
                        }
                        fmtPos++;
                        if (ok) str++;
                    } break;

                    case '(':
                        if (MatchImp(data, ref str, end, fmt, ref fmtPos)) {
                            ok = true;
                        }
                        break;

                    case '|':
                        fmtPos = MatchSkip(fmt, fmtPos, false);
                        ok = true;
                        break;

                    case ')':
                        strIo = str;
                        fmtIo = fmtPos;
                        return true;

                    case '.':
                        if (chRef != '\0') {
                            ok = true;
                            str++;
                        }
                        break;

                    default:
                        if (c == chRef) {
                            str++;
                            ok = true;
                        }
                        break;
                }

                bool didFail = false;
                c = Pat(fmt, fmtPos);
                switch (c) {
                    case '*':
                        fmtPos++;
                        if (ok) {
                            fmtPos = fmtBegin;
                            count++;
                            continue;
                        }
                        break;
                    case '+':
                        fmtPos++;
                        if (ok) {
                            fmtPos = fmtBegin;
                            count++;
                            continue;
                        } else if (count == 0) {
                            didFail = true;
                        }
                        break;
                    case '?':
                        fmtPos++;
                        break;
                    default:
                        didFail = !ok;
                        break;
                }

                if (didFail) {
                    fmtPos = MatchSkip(fmt, fmtPos, true);
                    if (Pat(fmt, fmtPos) == '|') {
                        fmtPos++;
                        str = strOriginalBegin;
                    } else {
                        fmtIo = MatchSkip(fmt, fmtPos, false) + 1;
                        return false;
                    }
                } else {
                    if (!ok) {
                        str = strBegin;
                    }
                }

                fmtBegin = fmtPos;
                count = 0;
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_next_line()
        // ------------------------------------------------------------------

        // C: ufbxi_next_line() (ufbx.c:10853-10876) over a `ufbx_string` buffer. `pos`/`length`
        // are C's `buf->data`/`buf->length`: the slice is consumed as lines are taken.
        static bool NextLine(string buf, ref int pos, ref int length, out int lineOff, out int lineLen, bool skipSpace)
        {
            lineOff = pos;
            lineLen = 0;
            if (length == 0) return false;

            int newline = buf.IndexOf('\n', pos, length);
            int lineLength = newline >= 0 ? (newline - pos) + 1 : length;

            lineOff = pos;
            lineLen = lineLength;
            pos += lineLength;
            length -= lineLength;

            if (skipSpace) {
                while (lineLen > 0 && UfbxiAscii.IsSpace(buf[lineOff])) {
                    lineOff++;
                    lineLen--;
                }
                while (lineLen > 0 && UfbxiAscii.IsSpace(buf[lineOff + lineLen - 1])) {
                    lineLen--;
                }
            }

            return true;
        }

        // ------------------------------------------------------------------
        // ufbxi_is_format() / ufbxi_determine_format()
        // ------------------------------------------------------------------

        // C: ufbxi_is_format() (ufbx.c:11092-11121). `window` is the sniffed prefix as a
        // raw-byte string, `size` its length in bytes.
        static bool IsFormat(string window, int size, UfbxFileFormat format)
        {
            int pos = 0;
            int length = size;
            int lineOff, lineLen;

            if (format == UfbxFileFormat.Fbx) {
                if (size >= BinaryMagicSize &&
                    string.CompareOrdinal(window, 0, BinaryMagicString, 0, BinaryMagicSize) == 0) {
                    return true;
                }

                while (NextLine(window, ref pos, ref length, out lineOff, out lineLen, true)) {
                    if (Match(window, lineOff, lineLen, ";\\s*FBX\\s*\\d+\\.\\d+\\.\\d+\\s*project\\s+file")) return true;
                    if (Match(window, lineOff, lineLen, "FBXHeaderExtension:.*")) return true;
                }
            } else if (format == UfbxFileFormat.Obj) {
                // C: the OBJ alternation, split across several string literals (ufbx.c:11105-11109)
                // which concatenates to exactly this pattern.
                const string pattern =
                    "(vn?\\s+\\F|vt)\\s+\\F\\s+\\F.*" + "|" +
                    "f\\s+[\\-/0-9]+\\s+[\\-/0-9]+\\s*[\\-/0-9]+.*" + "|" +
                    "(usemtl|mtllib)\\s+\\S.*";
                while (NextLine(window, ref pos, ref length, out lineOff, out lineLen, true)) {
                    if (Match(window, lineOff, lineLen, pattern)) return true;
                }
            } else if (format == UfbxFileFormat.Mtl) {
                const string pattern = "newmtl\\s+\\S.*";
                while (NextLine(window, ref pos, ref length, out lineOff, out lineLen, true)) {
                    if (Match(window, lineOff, lineLen, pattern)) return true;
                }
            } else {
                // C: ufbxi_unreachable("Unhandled format") (ufbx.c:11117). `ufbxi_determine_format()`
                // only asks for FBX/OBJ/MTL, so this branch is never taken here either.
            }

            return false;
        }

        // C: ufbxi_determine_format() (ufbx.c:11126-11187). `ufbxi_begin_parse()` is not part of
        // it; `UfbxiDom.BeginParse` handles the header once the format is known.
        internal static void DetermineFormat(UfbxiContext uc)
        {
            UfbxFileFormat format = uc.Opts.FileFormat;
            UfbxiStream stream = uc.Stream;

            if (format == UfbxFileFormat.Unknown && !uc.Opts.NoFormatFromContent) {
                stream.PauseProgress();

                ulong lookahead = (ulong)MinFileFormatLookahead;
                while (format == UfbxFileFormat.Unknown && lookahead <= (ulong)uc.Opts.FileFormatLookahead) {
                    if (lookahead > (ulong)stream.Remaining) {
                        if (stream.Eof) break;
                        stream.Refill((int)lookahead, false);
                    }

                    ulong dataSize = lookahead < (ulong)stream.Remaining ? lookahead : (ulong)stream.Remaining;
                    UfbxiFail.CheckMsg((long)dataSize > 0, "Empty file");

                    // C: `ufbxi_is_format(uc->data, data_size, fmt)` -- the same window is
                    // re-sniffed per format, so build the raw-byte view once.
                    string window = UfbxiRawStr.FromBytes(stream.Buffer, stream.Position, (int)dataSize);

                    for (uint fmt = (uint)UfbxFileFormat.Fbx; fmt < (uint)UfbxEnumCounts.UfbxFileFormat; fmt++) {
                        if (IsFormat(window, (int)dataSize, (UfbxFileFormat)fmt)) {
                            format = (UfbxFileFormat)fmt;
                            break;
                        }
                    }

                    if (lookahead >= (ulong)uc.Opts.FileFormatLookahead) {
                        break;
                    } else if (lookahead < MaxSizeT / 2) {
                        ulong doubled = lookahead * 2;
                        lookahead = doubled < (ulong)uc.Opts.FileFormatLookahead ? doubled : (ulong)uc.Opts.FileFormatLookahead;
                    } else {
                        lookahead = MaxSizeT;
                    }
                }

                UfbxiFail.CheckNoDesc(stream.ResumeProgress(), "ufbxi_resume_progress(uc)");
            }

            if (format == UfbxFileFormat.Unknown && !uc.Opts.NoFormatFromExtension) {
                string filename = uc.Opts.Filename;
                if (filename != null && filename.Length > 0) {
                    // C: cut the filename at the last '.' so the extension is ".fbx" etc.
                    int extOff = 0;
                    int extLen = filename.Length;
                    for (int i = extLen; i > 0; i--) {
                        if (filename[i - 1] == '.') {
                            extOff = i - 1;
                            extLen -= i - 1;
                            break;
                        }
                    }

                    if (Match(filename, extOff, extLen, "\\c\\.fbx")) {
                        format = UfbxFileFormat.Fbx;
                    } else if (Match(filename, extOff, extLen, "\\c\\.obj")) {
                        format = UfbxFileFormat.Obj;
                    } else if (Match(filename, extOff, extLen, "\\c\\.mtl")) {
                        format = UfbxFileFormat.Mtl;
                    }
                }
            }

            UfbxiFail.CheckMsg(format != UfbxFileFormat.Unknown, "Unrecognized file format");
            uc.Scene.Metadata.FileFormat = format;
        }

        // ------------------------------------------------------------------
        // Setup helpers
        // ------------------------------------------------------------------

        // C: ufbxi_supports_version() (ufbx.c:15842-15845).
        internal static bool SupportsVersion(uint version)
        {
            return version >= 3000 && version <= 7700;
        }

        // C: ufbxi_fixup_opts_string() (ufbx.c:25179-25193). The port's raw-byte strings always
        // carry their own byte length, so C's `length == SIZE_MAX` ("nul-terminated, measure
        // it") case is handled by the caller that produces it (the deferred file open).
        static void FixupOptsString(UfbxiContext uc, ref string str, bool push)
        {
            if (str.Length > 0) {
                if (push) {
                    UfbxiFail.CheckNoDesc(uc.StringPool.PushStringPlaceStr(ref str, false),
                        "ufbxi_push_string_place_str(&uc->string_pool, str, false)");
                }
            } else {
                str = EmptyChar;
            }
        }

        // C: ufbxi_prop_type_names[] (ufbx.c:11432-11465), in table order (the order the map is
        // filled and therefore the pointer-id order of the pooled names).
        static readonly UfbxiPropTypeName[] PropTypeNames = new UfbxiPropTypeName[] {
            new UfbxiPropTypeName("Boolean", UfbxPropType.Boolean),
            new UfbxiPropTypeName("bool", UfbxPropType.Boolean),
            new UfbxiPropTypeName("Bool", UfbxPropType.Boolean),
            new UfbxiPropTypeName("Integer", UfbxPropType.Integer),
            new UfbxiPropTypeName("int", UfbxPropType.Integer),
            new UfbxiPropTypeName("enum", UfbxPropType.Integer),
            new UfbxiPropTypeName("Enum", UfbxPropType.Integer),
            new UfbxiPropTypeName("Visibility", UfbxPropType.Integer),
            new UfbxiPropTypeName("Visibility Inheritance", UfbxPropType.Integer),
            new UfbxiPropTypeName("KTime", UfbxPropType.Integer),
            new UfbxiPropTypeName("Number", UfbxPropType.Number),
            new UfbxiPropTypeName("double", UfbxPropType.Number),
            new UfbxiPropTypeName("Real", UfbxPropType.Number),
            new UfbxiPropTypeName("Float", UfbxPropType.Number),
            new UfbxiPropTypeName("Intensity", UfbxPropType.Number),
            new UfbxiPropTypeName("Vector", UfbxPropType.Vector),
            new UfbxiPropTypeName("Vector3D", UfbxPropType.Vector),
            new UfbxiPropTypeName("Color", UfbxPropType.Color),
            new UfbxiPropTypeName("ColorAndAlpha", UfbxPropType.ColorWithAlpha),
            new UfbxiPropTypeName("ColorRGB", UfbxPropType.Color),
            new UfbxiPropTypeName("String", UfbxPropType.String),
            new UfbxiPropTypeName("KString", UfbxPropType.String),
            new UfbxiPropTypeName("object", UfbxPropType.String),
            new UfbxiPropTypeName("DateTime", UfbxPropType.DateTime),
            new UfbxiPropTypeName("Lcl Translation", UfbxPropType.Translation),
            new UfbxiPropTypeName("Lcl Rotation", UfbxPropType.Rotation),
            new UfbxiPropTypeName("Lcl Scaling", UfbxPropType.Scaling),
            new UfbxiPropTypeName("Distance", UfbxPropType.Distance),
            new UfbxiPropTypeName("Compound", UfbxPropType.Compound),
            new UfbxiPropTypeName("Blob", UfbxPropType.Blob),
            new UfbxiPropTypeName("Reference", UfbxPropType.Reference),
        };

        // C: sizeof(ufbxi_prop_type_name) -- only feeds C's byte-size arithmetic.
        const int PropTypeNameItemSize = 16;

        // C: ufbxi_load_maps() (ufbx.c:11742-11756). Only `prop_type_map` is reachable from the
        // spine; the other seven maps `ufbxi_load()` initializes (ufbx.c:25557-25564) are
        // populated by the property/element layers that arrive with the readers.
        static void LoadMaps(UfbxiContext uc)
        {
            UfbxiMap<UfbxiPropTypeName, ulong> map = uc.PropTypeMap;
            UfbxiFail.CheckNoDesc(map.Grow((uint)PropTypeNames.Length),
                "ufbxi_map_grow(&uc->prop_type_map, ufbxi_prop_type_name, ufbxi_arraycount(ufbxi_prop_type_names))");

            for (int i = 0; i < PropTypeNames.Length; i++) {
                UfbxiPropTypeName name = PropTypeNames[i];
                int ignored;
                string pooled = uc.StringPool.PushStringImp(name.Name, 0, name.Name.Length, out ignored, false, true);
                UfbxiFail.CheckNoDesc(pooled != null, "ufbxi_push_string_imp(&uc->string_pool, name->name, strlen(name->name), NULL, false, true)");

                ulong key = UfbxiPtrIdTable.IdOf(pooled);
                uint hash = UfbxiHash.HashPtr(key);
                int index = map.Insert(hash, key);
                UfbxiFail.CheckNoDesc(index >= 0, "ufbxi_map_insert(&uc->prop_type_map, ufbxi_prop_type_name, hash, &pooled)");

                UfbxiPropTypeName entry = map.Items[index];
                entry.Type = name.Type;
                entry.Name = pooled;
                map.Items[index] = entry;
            }
        }

        // C: ufbxi_thread_pool_init() (ufbx.c:6067-6089). Returns immediately unless the user
        // supplied both `run_fn` and `wait_fn`; everything about the task ring itself lives in
        // UfbxiThreadPool (Parse/ThreadPool.cs), including the `ufbx_thread_pool_context` handed
        // to the callbacks. Note C stores the *address of its stack pool* in `uc->thread_pool`
        // and passes that same address as the context; here the shared mutable state is one
        // instance reached through `uc.ThreadPool`.
        static void ThreadPoolInit(UfbxiContext uc)
        {
            UfbxThreadOpts opts = uc.Opts.ThreadOpts;
            uc.ThreadPool = UfbxiThreadPool.Init(uc.Error, opts);
            if (uc.ThreadPool == null) return;
            uc.ThreadPoolEnabled = true;
            uc.ThreadPoolNumTasks = uc.ThreadPool.NumTasks;
        }

        // C: ufbxi_free_temp() (ufbx.c:25437-25455) -- the pieces of it that exist in the port.
        static void FreeTemp(UfbxiContext uc)
        {
            // C: `ufbxi_thread_pool_free(&uc->thread_pool)` is the first thing ufbxi_free_temp()
            // does (ufbx.c:25421-25422), so `free_fn` runs before any other cleanup and may
            // itself wait on outstanding groups.
            if (uc.ThreadPool != null) uc.ThreadPool.Free();
            if (uc.StringPool != null) uc.StringPool.TempFree();
            if (uc.PropTypeMap != null) uc.PropTypeMap.Free();
            uc.SwapArr = null;
            uc.SwapArrSize = 0;
        }

        // ------------------------------------------------------------------
        // Default open_file_cb
        // ------------------------------------------------------------------

        // C: `uc->opts.open_file_cb.fn = &ufbx_default_open_file` (ufbx.c:25540) and the
        // pointer comparison `open_file_cb.fn == &ufbx_default_open_file` (ufbx.c:25232). The
        // port keeps one canonical delegate instance so `==` (delegate value equality) is that
        // comparison.
        internal static readonly UfbxOpenFileFn DefaultOpenFile = DefaultOpenFileFn;

        // C: ufbx_default_open_file() (ufbx.c:30414-30418) -> ufbx_open_file_ctx() ->
        // ufbxi_stdio_open() -> ufbxi_fopen() (ufbx.c:6981-7065): open the path read-only and
        // wire the stdio stream into the `ufbx_stream` the caller handed us. The real work lives in
        // UfbxiStreamOpen so the public ABI and this callback cannot drift apart -- notably the
        // UTF-8 -> UTF-16 path resolution, whose failure ("Invalid UTF-8") C reports *instead of*
        // "File not found" because the description is already set by the time
        // `ufbxi_fail_msg("open_file_fn()", ...)` runs (ufbx.c:3440, 25238-25245).
        static bool DefaultOpenFileFn(object user, UfbxInputStream stream, string path, int pathLength, UfbxOpenFileInfo info)
        {
            UfbxiOpenFileStream holder = stream as UfbxiOpenFileStream;
            if (holder == null) return false;

            // C: `return ufbx_open_file_ctx(stream, info->context, path, path_len, NULL, NULL)` --
            // NULL error, so the file context's error is dropped and only `ok` reaches the caller.
            if (!UfbxiStreamOpen.DefaultOpenFileEntry(user, out UfbxInputStream input, path, pathLength, info)) {
                return false;
            }
            holder.Attach(input);
            return true;
        }

        // C: `ufbx_stream` is a struct of function pointers the open callback fills in. The
        // port's stream model is an abstract instance, so the deferred open hands the callback
        // this holder and forwards to whatever it attaches (C: `stream->read_fn` etc.).
        internal sealed class UfbxiOpenFileStream : UfbxInputStream
        {
            UfbxInputStream target;

            // C: `ok` from the callback plus a filled-in stream.
            public bool Attached => target != null;

            public void Attach(UfbxInputStream stream)
            {
                target = stream;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (target == null) return -1;   // C: read_fn == NULL
                return target.Read(buffer, offset, count);
            }

            public override bool CanSkip => target != null && target.CanSkip;

            public override bool Skip(int size)
            {
                if (target == null) return false;
                return target.Skip(size);
            }

            public override ulong Size()
            {
                if (target == null) return 0;
                return target.Size();
            }

            public override void Close()
            {
                if (target != null) target.Close();
            }
        }

        // ------------------------------------------------------------------
        // ufbxi_load_imp()
        // ------------------------------------------------------------------

        // C: ufbxi_load_imp() (ufbx.c:25212-25455), up to the reader seam. Throws instead of
        // returning 0 (PORTING_NOTES.md #3); reaching the seam throws
        // `UfbxiReaderNotPortedException` from `UfbxiToplevel`.
        internal static void LoadImp(UfbxiContext uc)
        {
            UfbxiStream stream = uc.Stream;

            // C: check for deferred failure (ufbx.c:25215). `ufbx_load_stdio()` with UFBX_NO_STDIO
            // is the only producer; it pre-fills `uc->error` and C then runs the same tail as
            // any other failure.
            if (uc.DeferredFailure) UfbxiFail.FailNoDesc("uc->deferred_failure");

            // C: deferred file open (ufbx.c:25216-25252): `ufbx_load_file()` does not open the
            // file, so it can be opened with the user's `open_file_cb` once the options are known.
            if (uc.DeferredLoad) {
                string filename = uc.LoadFilename;
                int filenameLen = uc.LoadFilenameLen;
                bool filenameNullTerminated = false;
                if (filenameLen == -1) {
                    // C: `if (filename_len == SIZE_MAX) { opts.filename_null_terminated = true;
                    //      filename_len = strlen(filename); }`
                    filenameNullTerminated = true;
                    int nul = filename.IndexOf('\0');
                    filenameLen = nul < 0 ? filename.Length : nul;
                }
                if (uc.Opts.Filename.Length == 0) {
                    uc.Opts.Filename = filename;
                }

                UfbxOpenFileInfo info = new UfbxOpenFileInfo();
                info.Type = UfbxOpenFileType.MainModel;
                info.OriginalFilename = UfbxiRawStr.ToBytes(filename, 0, filenameLen);

                UfbxiOpenFileStream opened = new UfbxiOpenFileStream();
                UfbxError openError = new UfbxError();
                bool ok;
                if (uc.Opts.OpenMainFileWithDefault || uc.Opts.OpenFileCb.Fn == DefaultOpenFile) {
                    // C: `ufbx_open_file_ctx(&stream, (ufbx_open_file_context)&uc->ator_tmp,
                    // filename, filename_len, &opts, &error)` (ufbx.c:25234-25237): the default
                    // callback, with the temp allocator as the file-context parent.
                    ok = OpenFileWithDefault(opened, filename, filenameLen, filenameNullTerminated, openError);
                } else {
                    // C: ufbxi_open_file(&uc->opts.open_file_cb, &stream, uc->load_filename,
                    // filename_len, NULL, &uc->ator_tmp, UFBX_OPEN_FILE_MAIN_MODEL)
                    // (ufbx.c:25239, 16653-16667).
                    ok = uc.Opts.OpenFileCb.Fn(uc.Opts.OpenFileCb.User, opened, filename,
                        filenameLen, info) && opened.Attached;
                }

                if (!ok) {
                    if (openError.Type != UfbxErrorType.None) {
                        // C: `uc->error = error` -- the callback reported a filled error struct.
                        AssignError(uc.Error, openError);
                    } else {
                        UfbxiPrint.SetErrInfo(uc.Error, UfbxiRawStr.ToBytes(filename, 0, filenameLen), 0, filenameLen);
                    }
                    UfbxiFail.FailMsg("open_file_fn()", "File not found");
                }

                // C: uc->read_fn / skip_fn / size_fn / close_fn / read_user (ufbx.c:25248-25252):
                // the port's `UfbxInputStream` is that quad plus its `user`.
                uc.Stream.Input = opened;
            }

            // C: report the total byte count once (ufbx.c:25254-25258).
            if (uc.Opts.ProgressCb != null && uc.Opts.ProgressCb.Fn != null &&
                stream.ProgressBytesTotal == 0 && stream.Input != null) {
                ulong total = stream.Input.Size();
                UfbxiFail.CheckNoDesc(total != ulong.MaxValue, "total != UINT64_MAX");
                stream.ProgressBytesTotal = total;
            }

            UfbxiFail.CheckNoDesc(uc.Opts.PathSeparator >= 0x20 && uc.Opts.PathSeparator <= 0x7e,
                "uc->opts.path_separator >= 0x20 && uc->opts.path_separator <= 0x7e");

            FixupOptsString(uc, ref uc.Opts.Filename, false);
            FixupOptsString(uc, ref uc.Opts.ObjMtlPath, true);
            FixupOptsString(uc, ref uc.Opts.GeometryTransformHelperName, true);
            FixupOptsString(uc, ref uc.Opts.ScaleHelperName, true);

            ThreadPoolInit(uc);

            if (!uc.Opts.AllowUnsafe) {
                UfbxiFail.CheckMsg(uc.Opts.IndexErrorHandling != UfbxIndexErrorHandling.UnsafeIgnore, "Unsafe options");
                UfbxiFail.CheckMsg(uc.Opts.UnicodeErrorHandling != UfbxUnicodeErrorHandling.UnsafeIgnore, "Unsafe options");
            } else {
                uc.Scene.Metadata.IsUnsafe = true;
            }

            if (uc.Opts.IndexErrorHandling == UfbxIndexErrorHandling.NoIndex) {
                uc.Scene.Metadata.MayContainNoIndex = true;
            }

            uc.RetainMeshParts = !uc.Opts.IgnoreGeometry && !uc.Opts.SkipMeshParts;
            uc.Scene.Metadata.MayContainMissingVertexPosition = uc.Opts.AllowMissingVertexPosition;
            uc.Scene.Metadata.MayContainBrokenElements = uc.Opts.ConnectBrokenElements;

            uc.Scene.Metadata.Creator = EmptyChar;

            uc.UnitScale = 1.0f;
            if (stream.Buffer == null) {
                // C: ufbxi_dev_assert(uc->data_begin == NULL) (ufbx.c:25287) -- a debug-only
                // assertion, not part of the reference build.
                stream.Buffer = ZeroSizeBuffer;
                stream.BeginIndex = 0;
                stream.Position = 0;
            }

            uc.RetainVertexW = (uc.Opts.RetainDom || uc.Opts.RetainVertexAttribW) && !uc.Opts.IgnoreGeometry;

            // C: ufbxi_load_strings() (ufbx.c:25294, 11407-11424) -- intern the `ufbxi_*`
            // constants. This is the point where their pointer ids become pool entries.
            UfbxiStringPool.LoadStrings(uc.StringPool);
            LoadMaps(uc);
            DetermineFormat(uc);

            UfbxFileFormat format = uc.Scene.Metadata.FileFormat;

            if (format == UfbxFileFormat.Fbx) {
                // C: ufbxi_begin_parse() -- reused from the DOM port (Parse/DomNode.cs:57).
                UfbxiDom.BeginParse(uc);
                if (uc.Version < LegacyVersionLimit) {
                    UfbxiToplevel.ReadLegacyRoot(uc);
                } else {
                    UfbxiToplevel.ReadRoot(uc);
                }

                // C: ufbxi_warnf(UFBX_WARNING_UNSUPPORTED_VERSION, "Unsupported FBX version (%u)",
                // uc->version) (ufbx.c:25307-25309), after the root is read and before the
                // metadata is updated.
                if (!SupportsVersion(uc.Version)) {
                    UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.UnsupportedVersion,
                        UfbxiWarnings.NoElementId, "Unsupported FBX version (%u)",
                        new UfbxiVaList().AddUInt(uc.Version));
                }
            } else if (format == UfbxFileFormat.Obj) {
                UfbxiToplevel.ObjLoad(uc);
            } else if (format == UfbxFileFormat.Mtl) {
                UfbxiToplevel.MtlLoad(uc);
            }

            // C: the shared tail of `ufbxi_load_imp` (ufbx.c:25320-25373): fake DOM root,
            // pre-finalize, finalize, settings, axes/units, adjust transforms, geometry
            // modification, scene update, external files, skinning. Runs for every format.
            UfbxiToplevel.SceneBuild(uc);
        }

        // C: `ufbx_open_file_ctx(&stream, ctx, path, path_len, opts, &error)` with the default
        // callback (ufbx.c:30425-30443). Split out so the deferred-open site reads like C: the
        // file context carries its own `ufbx_error`, which `ufbxi_end_file_context()` turns into
        // the caller's error with `ufbxi_fix_error_type(..., "Failed to open file", ...)`
        // (ufbx.c:6960-6975) -- and which clears the caller's error on success.
        static bool OpenFileWithDefault(UfbxiOpenFileStream stream, string path, int pathLen,
            bool filenameNullTerminated, UfbxError error)
        {
            UfbxOpenFileOpts opts = new UfbxOpenFileOpts();
            opts.FilenameNullTerminated = filenameNullTerminated;

            // C: `ctx = (ufbx_open_file_context)&uc->ator_tmp` (ufbx.c:25234) is non-NULL purely to
            // inherit the temp allocator; the port models no arenas (PORTING_NOTES.md #4), so the
            // value has no observable and is passed as zero.
            bool ok = UfbxiStreamOpen.OpenFileCtx(out UfbxInputStream input, 0, path, pathLen, opts, error);
            if (ok) {
                // C: ufbxi_stdio_init() wires the callbacks only after fopen() succeeded, so a failed
                // open leaves the loader's `ufbx_stream` empty (ufbx.c:7135-7141).
                stream.Attach(input);
            }
            return ok;
        }

        // C: `uc->error = error` (ufbx.c:25243), the struct assignment of `ufbx_error`. Field by
        // field, like every other `ufbx_error` copy in this port (UfbxiPrint.FixErrorType).
        static void AssignError(UfbxError dst, UfbxError src)
        {
            dst.Type = src.Type;
            dst.Description = src.Description;
            dst.StackSize = src.StackSize;
            dst.Stack = src.Stack;
            dst.Info = src.Info;
            dst.InfoLength = src.InfoLength;
        }

        // ------------------------------------------------------------------
        // ufbxi_load()
        // ------------------------------------------------------------------

        // C: ufbxi_load() (ufbx.c:25478-25632). The caller (`UfbxApi`) has already filled the
        // I/O window of `uc.Stream` the way `ufbx_load_memory()`/`ufbx_load_file()` do.
        internal static UfbxScene Load(UfbxiContext uc, UfbxLoadOpts userOpts, UfbxError pError)
        {
            // C: test endianness (ufbx.c:25479-25486).
            uc.LocalBigEndian = !BitConverter.IsLittleEndian;

            // C: uc->double_parse_flags = ufbxi_parse_double_init_flags() (ufbx.c:25489).
            uc.DoubleParseFlags = UfbxiAscii.ParseDoubleInitFlags;

            // C: `uc->opts = *user_opts` / `memset(&uc->opts, 0, ...)` (ufbx.c:25491-25496).
            // `ufbxi_load()` and `ufbxi_load_imp()` then mutate the copy, so the port must not
            // alias the caller's object.
            uc.Opts = userOpts != null ? CopyOpts(userOpts) : new UfbxLoadOpts();

            if (uc.Opts.FileSizeEstimate != 0) {
                uc.Stream.ProgressBytesTotal = uc.Opts.FileSizeEstimate;
            }

            if (uc.Opts.IgnoreAllContent) {
                uc.Opts.IgnoreGeometry = true;
                uc.Opts.IgnoreAnimation = true;
                uc.Opts.IgnoreEmbedded = true;
            }

            // C: `ufbx_inflate_retain inflate_retain; inflate_retain.initialized = false;`
            // (ufbx.c:25498-25499, hooked up at 25600).
            uc.InflateRetain = new UfbxInflateRetain();

            // C: ufbxi_init_ator() for temp/result (ufbx.c:25501-25502) -- allocator bookkeeping
            // only in the port (PORTING_NOTES.md #4); the limits are recorded so a future port
            // of the arenas finds them where C does.

            if (uc.Opts.ReadBufferSize == 0) {
                uc.Opts.ReadBufferSize = 0x4000;
            }
            if (uc.Opts.ReadBufferSize <= 32) {
                uc.Opts.ReadBufferSize = 32;
            }
            uc.Stream.OptReadBufferSize = uc.Opts.ReadBufferSize;

            if (uc.Opts.FileFormatLookahead == 0) {
                uc.Opts.FileFormatLookahead = 0x4000;
            } else if (uc.Opts.FileFormatLookahead < MinFileFormatLookahead) {
                uc.Opts.FileFormatLookahead = MinFileFormatLookahead;
            }

            if (uc.Opts.PathSeparator == 0) {
                uc.Opts.PathSeparator = PathSeparatorChar;
            }

            ulong progressInterval;
            if (uc.Opts.ProgressCb == null || uc.Opts.ProgressCb.Fn == null || uc.Opts.ProgressIntervalHint >= MaxSizeT) {
                progressInterval = MaxSizeT;
            } else if (uc.Opts.ProgressIntervalHint > 0) {
                progressInterval = uc.Opts.ProgressIntervalHint;
            } else {
                progressInterval = 0x4000;
            }
            uc.ProgressInterval = progressInterval;
            uc.Stream.ProgressInterval = progressInterval;

            // C reads `uc->opts.progress_cb` directly at every report site (ufbx.c:6671, 6693,
            // 25254); the port re-reads it through `UfbxiStream.ProgressCb`, which has to be
            // wired to the (already value-copied) opts here or `ufbxi_report_progress()` takes
            // its `!progress_cb.fn` early-out and no progress is ever delivered.
            uc.Stream.ProgressCb = uc.Opts.ProgressCb;

            if (uc.Opts.OpenFileCb.Fn == null) {
                uc.Opts.OpenFileCb.Fn = DefaultOpenFile;
            }

            if (uc.Opts.ThreadOpts.MemoryLimit == 0) {
                uc.Opts.ThreadOpts.MemoryLimit = 32 * 1024 * 1024;
            }

            uc.SyntheticIdCounter = SyntheticIdStart;

            // C: the string pool setup (ufbx.c:25549-25556) -- note that the constants are
            // interned later, by `ufbxi_load_strings()` inside `ufbxi_load_imp()`.
            uc.NewStringPool(uc.Error, 1024);

            // C: ufbxi_map_init(&uc->prop_type_map, ..., &ufbxi_map_cmp_const_char_ptr, NULL)
            // (ufbx.c:25557).
            uc.PropTypeMap = new UfbxiMap<UfbxiPropTypeName, ulong>(
                UfbxiMapCmps.ConstCharPtr<UfbxiPropTypeName>(p => UfbxiPtrIdTable.IdOf(p.Name)),
                PropTypeNameItemSize, uc.Error);

            // C: the remaining `ufbxi_map_init()` calls (ufbx.c:25558-25564) and the
            // `tmp_*.ator`/`unordered`/`clearable` buffer flags (ufbx.c:25566-25594) are arena
            // state that has no port-side observable; `swap_arr` (ufbx.c:25597) is the
            // zero-size buffer trick and needs none here.

            try {
                LoadImp(uc);

                // C: `if (ok) { ufbxi_clear_error(p_error); return &uc->scene_imp->scene; }`
                // (ufbx.c:25611-25615). Not reachable in the port: `LoadImp()` always ends at
                // the unported reader seam. Kept so the success wiring exists for when the
                // readers land.
                UfbxiPrint.ClearError(pError);
                return uc.Scene;
            } finally {
                // C: `if (uc->close_fn) uc->close_fn(uc->read_user); ufbxi_free_temp(uc);`
                // (ufbx.c:25603-25609) -- runs on both the success and the failure path.
                if (uc.Stream != null && uc.Stream.Input != null) {
                    uc.Stream.Input.Close();
                }
                FreeTemp(uc);
            }
        }

        // ------------------------------------------------------------------
        // Options copy
        // ------------------------------------------------------------------

        // C: `uc->opts = *user_opts` (ufbx.c:25492): a shallow struct copy in C, so the nested
        // option structs (which are embedded by value) must be copied rather than aliased --
        // `ufbxi_load()` mutates `read_buffer_size`, `file_format_lookahead`, `path_separator`,
        // `open_file_cb.fn`, `thread_opts.memory_limit` and the interned strings.
        internal static UfbxLoadOpts CopyOpts(UfbxLoadOpts src)
        {
            UfbxLoadOpts dst = new UfbxLoadOpts();
            dst.TempAllocator = CopyAllocatorOpts(src.TempAllocator);
            dst.ResultAllocator = CopyAllocatorOpts(src.ResultAllocator);
            dst.ThreadOpts = CopyThreadOpts(src.ThreadOpts);
            dst.IgnoreGeometry = src.IgnoreGeometry;
            dst.IgnoreAnimation = src.IgnoreAnimation;
            dst.IgnoreEmbedded = src.IgnoreEmbedded;
            dst.IgnoreAllContent = src.IgnoreAllContent;
            dst.EvaluateSkinning = src.EvaluateSkinning;
            dst.EvaluateCaches = src.EvaluateCaches;
            dst.LoadExternalFiles = src.LoadExternalFiles;
            dst.IgnoreMissingExternalFiles = src.IgnoreMissingExternalFiles;
            dst.SkipSkinVertices = src.SkipSkinVertices;
            dst.SkipMeshParts = src.SkipMeshParts;
            dst.CleanSkinWeights = src.CleanSkinWeights;
            dst.UseBlenderPbrMaterial = src.UseBlenderPbrMaterial;
            dst.DisableQuirks = src.DisableQuirks;
            dst.Strict = src.Strict;
            dst.ForceSingleThreadAsciiParsing = src.ForceSingleThreadAsciiParsing;
            dst.AllowUnsafe = src.AllowUnsafe;
            dst.IndexErrorHandling = src.IndexErrorHandling;
            dst.ConnectBrokenElements = src.ConnectBrokenElements;
            dst.AllowNodesOutOfRoot = src.AllowNodesOutOfRoot;
            dst.AllowMissingVertexPosition = src.AllowMissingVertexPosition;
            dst.AllowEmptyFaces = src.AllowEmptyFaces;
            dst.GenerateMissingNormals = src.GenerateMissingNormals;
            dst.OpenMainFileWithDefault = src.OpenMainFileWithDefault;
            dst.PathSeparator = src.PathSeparator;
            dst.NodeDepthLimit = src.NodeDepthLimit;
            dst.FileSizeEstimate = src.FileSizeEstimate;
            dst.ReadBufferSize = src.ReadBufferSize;
            dst.Filename = src.Filename;
            dst.RawFilename = src.RawFilename;
            dst.ProgressCb = CopyProgressCb(src.ProgressCb);
            dst.ProgressIntervalHint = src.ProgressIntervalHint;
            dst.OpenFileCb = CopyOpenFileCb(src.OpenFileCb);
            dst.GeometryTransformHandling = src.GeometryTransformHandling;
            dst.InheritModeHandling = src.InheritModeHandling;
            dst.SpaceConversion = src.SpaceConversion;
            dst.PivotHandling = src.PivotHandling;
            dst.PivotHandlingRetainEmpties = src.PivotHandlingRetainEmpties;
            dst.HandednessConversionAxis = src.HandednessConversionAxis;
            dst.HandednessConversionRetainWinding = src.HandednessConversionRetainWinding;
            dst.ReverseWinding = src.ReverseWinding;
            dst.TargetAxes = src.TargetAxes;
            dst.TargetUnitMeters = src.TargetUnitMeters;
            dst.TargetCameraAxes = src.TargetCameraAxes;
            dst.TargetLightAxes = src.TargetLightAxes;
            dst.GeometryTransformHelperName = src.GeometryTransformHelperName;
            dst.ScaleHelperName = src.ScaleHelperName;
            dst.NormalizeNormals = src.NormalizeNormals;
            dst.NormalizeTangents = src.NormalizeTangents;
            dst.UseRootTransform = src.UseRootTransform;
            dst.RootTransform = src.RootTransform;
            dst.KeyClampThreshold = src.KeyClampThreshold;
            dst.UnicodeErrorHandling = src.UnicodeErrorHandling;
            dst.RetainVertexAttribW = src.RetainVertexAttribW;
            dst.RetainDom = src.RetainDom;
            dst.FileFormat = src.FileFormat;
            dst.FileFormatLookahead = src.FileFormatLookahead;
            dst.NoFormatFromContent = src.NoFormatFromContent;
            dst.NoFormatFromExtension = src.NoFormatFromExtension;
            dst.ObjSearchMtlByFilename = src.ObjSearchMtlByFilename;
            dst.ObjMergeObjects = src.ObjMergeObjects;
            dst.ObjMergeGroups = src.ObjMergeGroups;
            dst.ObjSplitGroups = src.ObjSplitGroups;
            dst.ObjMtlPath = src.ObjMtlPath;
            dst.ObjMtlData = src.ObjMtlData;
            dst.ObjUnitMeters = src.ObjUnitMeters;
            dst.ObjAxes = src.ObjAxes;
            return dst;
        }

        static UfbxAllocatorOpts CopyAllocatorOpts(UfbxAllocatorOpts src)
        {
            if (src == null) return new UfbxAllocatorOpts();
            UfbxAllocatorOpts dst = new UfbxAllocatorOpts();
            dst.Allocator = src.Allocator;   // C: embedded `ufbx_allocator` (function pointers)
            dst.MemoryLimit = src.MemoryLimit;
            dst.AllocationLimit = src.AllocationLimit;
            dst.HugeThreshold = src.HugeThreshold;
            dst.MaxChunkSize = src.MaxChunkSize;
            return dst;
        }

        static UfbxThreadOpts CopyThreadOpts(UfbxThreadOpts src)
        {
            if (src == null) return new UfbxThreadOpts();
            UfbxThreadOpts dst = new UfbxThreadOpts();
            UfbxThreadPool pool = new UfbxThreadPool();
            if (src.Pool != null) {
                pool.InitFn = src.Pool.InitFn;
                pool.RunFn = src.Pool.RunFn;
                pool.WaitFn = src.Pool.WaitFn;
                pool.FreeFn = src.Pool.FreeFn;
                pool.User = src.Pool.User;
            }
            dst.Pool = pool;
            dst.NumTasks = src.NumTasks;
            dst.MemoryLimit = src.MemoryLimit;
            return dst;
        }

        static UfbxProgressCb CopyProgressCb(UfbxProgressCb src)
        {
            if (src == null) return new UfbxProgressCb();
            UfbxProgressCb dst = new UfbxProgressCb();
            dst.Fn = src.Fn;
            dst.User = src.User;
            return dst;
        }

        static UfbxOpenFileCb CopyOpenFileCb(UfbxOpenFileCb src)
        {
            if (src == null) return new UfbxOpenFileCb();
            UfbxOpenFileCb dst = new UfbxOpenFileCb();
            dst.Fn = src.Fn;
            dst.User = src.User;
            return dst;
        }
    }
}
