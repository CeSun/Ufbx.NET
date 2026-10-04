// Public load entry points, ported from ufbx.c v0.23.1:
//   ufbx_load_memory()   (ufbx.c:30510-30518)
//   ufbx_load_file()     (ufbx.c:30520-30523) -> ufbx_load_file_len() (30525-30534)
//   ufbx_free_scene()    (ufbx.c:30590-30600)
//
// `using System.IO;` is deliberate: the stdio entry points spell C's `void *file_void`
// as a `FileStream`, so this file -- not just Parse/InputStreams.cs -- depends on it.
//
// This is the single convergence point of the port's error plumbing (PORTING_NOTES.md #3):
// `ufbxi_check`/`ufbxi_fail` are exceptions (`UfbxParseError`), and C's failure tail
// `ufbxi_fix_error_type(&uc->error, "Failed to load", p_error)` plus the "Unsupported version"
// rewrite (ufbx.c:25613-25630) is performed here, so the reported `ufbx_error` type/description/
// info match C byte for byte.
//
// ERROR 口径 (see Parse/Error.cs): `UfbxParseError.HasDescription` distinguishes C's
// `ufbxi_check_msg`/`ufbxi_fail_msg` (which write `error.description`, first failure wins --
// `if (!err->description.data)`, ufbx.c:3440ish) from the plain `ufbxi_check`/`ufbxi_fail`
// (which write nothing, so `ufbxi_fix_error_type()` substitutes its `default_desc`). C re-derives
// `type` from the description in every case, so `UfbxParseError.ErrorType` is deliberately
// ignored here.
//
// The scene-content readers are out of scope: a load that gets past the load spine throws
// `UfbxiReaderNotPortedException` (Parse/Load.cs) instead of returning a scene, so an unported
// success can never look like an empty-but-valid scene.

using System.IO;

namespace Ufbx
{
    public static class UfbxApi
    {
        // C: ufbx_load_memory(const void *data, size_t size, const ufbx_load_opts *opts,
        // ufbx_error *error) (ufbx.c:30510-30518).
        //
        // C's `ufbxi_check_opts_ptr()` guard (ufbx.c:30512, 30321-30326) tests the
        // `_begin_zero`/`_end_zero` sentinels that detect an opts struct the caller forgot to
        // clear to zero; the port's options type always initializes its fields (Model/Opts/
        // LoadOpts.cs:12-13), so that failure is not representable and `ufbxi_uninitialized_options()`
        // (ufbx.c:30310-30320) has no port-side call site.
        public static UfbxScene LoadMemory(byte[] data, int size, UfbxLoadOpts opts, UfbxError error)
        {
            UfbxiContext uc = NewContext();
            try {
                // C: uc.data_begin = uc.data = data; uc.data_size = size;
                //      uc.progress_bytes_total = size;
                // A memory load has no read_fn: the whole input is the window (Parse/Stream.cs
                // treats `Input == null` as C's NULL function pointers).
                uc.Stream.Buffer = data;
                uc.Stream.BeginIndex = 0;
                uc.Stream.Position = 0;
                uc.Stream.Remaining = size;
                uc.Stream.YieldSize = 0;
                uc.Stream.Input = null;
                uc.Stream.ProgressBytesTotal = (ulong)size;
                return UfbxiLoad.Load(uc, opts, error);
            } catch (UfbxParseError e) {
                ReportFailure(uc, e, error);
                return null;
            }
        }

        // C: ufbx_load_memory() with `size = data size`.
        public static UfbxScene LoadMemory(byte[] data, UfbxLoadOpts opts, UfbxError error)
        {
            return LoadMemory(data, data != null ? data.Length : 0, opts, error);
        }

        // C: ufbx_load_file(const char *filename, const ufbx_load_opts *opts, ufbx_error *error)
        // (ufbx.c:30520-30523) == ufbx_load_file_len(filename, SIZE_MAX, opts, error).
        // The file is not opened here: `ufbxi_load_imp()` does that once the options (and thus
        // `open_file_cb`) are known (ufbx.c:25216-25252).
        //
        // `filename` follows the raw-byte string model (1 char == 1 byte): C treats it as UTF-8,
        // so a caller porting a C `const char*` path can pass it through unchanged.
        public static UfbxScene LoadFile(string filename, UfbxLoadOpts opts, UfbxError error)
        {
            UfbxiContext uc = NewContext();
            try {
                // C: uc.deferred_load = true; uc.load_filename = filename;
                //      uc.load_filename_len = SIZE_MAX;
                uc.DeferredLoad = true;
                uc.LoadFilename = filename;
                uc.LoadFilenameLen = -1;   // C: SIZE_MAX == "nul-terminated"
                return UfbxiLoad.Load(uc, opts, error);
            } catch (UfbxParseError e) {
                ReportFailure(uc, e, error);
                return null;
            }
        }

        // C: ufbx_free_scene(ufbx_scene *scene) (ufbx.c:30586-30594): `ufbxi_get_imp()` recovers
        // the `ufbxi_scene_imp` from the scene pointer, checks `imp->magic ==
        // UFBXI_SCENE_IMP_MAGIC` and `ufbxi_release_ref()` frees the result arena once the last
        // reference is gone.
        //
        // The port has no arenas and no manual result memory (PORTING_NOTES.md #4): a scene is
        // ordinary managed memory, so there is nothing to release and neither the magic nor the
        // refcount has a C# counterpart to check (the type system rules out freeing a foreign or
        // already-freed scene). Kept as the API counterpart of C so calling code written against
        // the C surface compiles; the `if (!scene) return;` guard is the only observable.
        public static void FreeScene(UfbxScene scene)
        {
        }

        // C: ufbx_retain_scene(ufbx_scene *scene) (ufbx.c:30596-30604). C recovers the
        // `ufbxi_scene_imp` header from the scene pointer and `ufbxi_retain_ref()`s it; the managed
        // scene has no header to hold a count, so this is the same no-op as `FreeScene` above.
        public static void RetainScene(UfbxScene scene)
        {
        }

        // C: ufbx_create_anim (ufbx.c:31202-31226). `ufbx_anim` is a struct in C and the call hands
        // back `&imp->anim` -- a pointer into the arena that holds the override strings; in the port
        // it is a managed object created by UfbxiCreateAnim, so its lifetime is the GC's.
        // Returns null and fills `error` on failure, like C.
        public static UfbxAnim CreateAnim(UfbxScene scene, UfbxAnimOpts opts, UfbxError error)
            => UfbxiCreateAnim.CreateAnimEntry(scene, opts, error);

        // C: ufbx_free_anim / ufbx_retain_anim (ufbx.c:31228-31237 / 31239-31248). Both
        // early-return unless `anim->custom` -- which since batch K `UfbxApi.CreateAnim()` does set
        // (31202-31226) -- and otherwise walk the `ufbxi_anim_imp` refcount header, where C would
        // free the arena that backs the override names. The port has no such arena (PORTING_NOTES.md
        // #4), so the pair stays a no-op for scene anims *and* custom ones; `UfbxAnim.Custom`
        // mirrors the field that gates them.
        public static void FreeAnim(UfbxAnim anim)
        {
        }

        public static void RetainAnim(UfbxAnim anim)
        {
        }

        // C: ufbx_free_mesh / ufbx_retain_mesh (ufbx.c:32635-32644 / 32646-32655). Gated on
        // `mesh->subdivision_evaluated || mesh->from_tessellated_nurbs` (the two cases where C
        // allocates a `ufbxi_mesh_imp`), and no-ops like the rest of the family -- see the
        // refcount-plumbing note in Parse/Subdivide.cs.
        public static void FreeMesh(UfbxMesh mesh)
        {
        }

        public static void RetainMesh(UfbxMesh mesh)
        {
        }

        // C: ufbx_free_geometry_cache / ufbx_retain_geometry_cache (ufbx.c:32674-32683 /
        // 32685-32694). Gated on `imp->owned_by_scene`: a cache owned by a scene must not be
        // released through this entry point. The port's ownership flag lives in the cache context's
        // imp (Parse/GeometryCache.cs:606-615) and is not reachable from the public object, and
        // there is nothing to release either way.
        public static void FreeGeometryCache(UfbxGeometryCache cache)
        {
        }

        public static void RetainGeometryCache(UfbxGeometryCache cache)
        {
        }

        // C: ufbx_free_line_curve / ufbx_retain_line_curve (ufbx.c:32367-32376 / 32378-32387).
        // Gated on `line_curve->from_tessellated_nurbs` (`UfbxLineCurve.FromTessellatedNurbs`), the
        // only case where C gives the curve an imp header.
        public static void FreeLineCurve(UfbxLineCurve curve)
        {
        }

        public static void RetainLineCurve(UfbxLineCurve curve)
        {
        }

        // C: ufbx_is_thread_safe (ufbx.c:30505-30508) -- `return UFBXI_THREAD_SAFE != 0;`, i.e. a        // compile-time answer, not a runtime property: 1 when the build has C11 atomics for
        // `ufbxi_atomic_counter` (ufbx.c:633, with the 0 fallback at 714-715), 2-vs-linear thresholds
        // aside. The reference build the differentials run against takes the atomics branch --
        // probed directly: `ufbx_is_thread_safe()` == 1 with the mandated oracle flags.
        public static bool IsThreadSafe() => true;

        // C: ufbx_evaluate_scene(const ufbx_scene *scene, const ufbx_anim *anim, double time,
        // const ufbx_evaluate_opts *opts, ufbx_error *error) (ufbx.c:31186-31200).
        //
        // `ufbxi_check_opts_ptr()` (ufbx.c:31188) is the same forgotten-zeroing sentinel as in
        // LoadMemory() and not representable for the port's options type. C's
        // `#if UFBXI_FEATURE_SCENE_EVALUATION` false-branch (ufbx.c:31194-31199) reports
        // "Feature disabled" when scene evaluation is compiled out; the port always compiles it in,
        // which is C's default configuration.
        //
        // `opts == null` is not "use the defaults": C `memset()`s the local copy (ufbx.c:26456-26460),
        // so `evaluate_skinning`/`evaluate_caches`/`evaluate_flags` are all off -- which is exactly
        // what `UfbxEvaluateOpts.CreateDefault()` also gives (Model/Opts/LoadOpts.cs:160-165), the two
        // differing only in the allocator fields that have no port-side counterpart.
        public static UfbxScene EvaluateScene(UfbxScene scene, UfbxAnim anim, double time,
            UfbxEvaluateOpts opts, UfbxError error)
        {
            return UfbxiEvaluateScene.EvaluateScene(new UfbxiEvalContext(), scene, anim, time, opts, error);
        }

        // ------------------------------------------------------------------
        // Stream / stdio / open family (batch L)
        // ------------------------------------------------------------------

        // C: ufbx_load_stream(const ufbx_stream *stream, const ufbx_load_opts *opts,
        // ufbx_error *error) (ufbx.c:30564-30567) == ufbx_load_stream_prefix(stream, NULL, 0, ...).
        public static UfbxScene LoadStream(UfbxInputStream stream, UfbxLoadOpts opts, UfbxError error)
        {
            return LoadStreamPrefix(stream, null, 0, opts, error);
        }

        // C: ufbx_load_stream_prefix(const ufbx_stream *stream, const void *prefix,
        // size_t prefix_size, const ufbx_load_opts *opts, ufbx_error *error)
        // (ufbx.c:30569-30584).
        //
        // `prefix` is the "already buffered" head of the input: C seeds the read window with it
        // (`uc.data_begin = uc.data = prefix; uc.data_size = prefix_size`, 30574-30575) and reads
        // continue through `stream` from wherever the producer left it, so `prefix_size` bytes of
        // the stream are considered consumed already. The port requires `prefix_size <=
        // prefix.Length`; C would read past the array (undefined behaviour) rather than fail.
        //
        // Note what C does NOT set here, unlike ufbx_load_memory() (30517): `progress_bytes_total`
        // stays 0 until the first `size_fn` query in `ufbxi_load_imp()` (25254-25258), so a
        // progress callback sees BytesTotal == BytesRead until the size is known.
        public static UfbxScene LoadStreamPrefix(UfbxInputStream stream, byte[] prefix, int prefixSize,
            UfbxLoadOpts opts, UfbxError error)
        {
            UfbxiContext uc = NewContext();
            try {
                // C: uc.data_begin = uc.data = prefix; uc.data_size = prefix_size;
                //      uc.read_fn/skip_fn/size_fn/close_fn = stream->...; uc.read_user = stream->user
                // (ufbx.c:30572-30579). `prefix == null` is C's NULL `data_begin`/`data`, which
                // `ufbxi_load_imp()` recognizes and replaces with an empty window (Load.cs:927).
                uc.Stream.Buffer = prefix;
                uc.Stream.BeginIndex = 0;
                uc.Stream.Position = 0;
                uc.Stream.Remaining = prefixSize;
                uc.Stream.YieldSize = 0;
                uc.Stream.Input = stream;
                return UfbxiLoad.Load(uc, opts, error);
            } catch (UfbxParseError e) {
                ReportFailure(uc, e, error);
                return null;
            }
        }

        // C: ufbx_load_stdio(void *file_void, const ufbx_load_opts *opts, ufbx_error *error)
        // (ufbx.c:30536-30539) == ufbx_load_stdio_prefix(file, NULL, 0, ...).
        public static UfbxScene LoadStdio(FileStream file, UfbxLoadOpts opts, UfbxError error)
        {
            return LoadStdioPrefix(file, null, 0, opts, error);
        }

        // C: ufbx_load_stdio_prefix(void *file_void, const void *prefix, size_t prefix_size,
        // const ufbx_load_opts *opts, ufbx_error *error) (ufbx.c:30542-30562).
        //
        // Two parts of it are observable and ported exactly:
        //  * `if (!file_void) return NULL;` (30544) fails WITHOUT touching `error` -- no
        //    `ufbxi_load()` runs at all, so a dirty `ufbx_error` stays dirty.
        //  * `ufbxi_stdio_init(&stream, file_void, false)` (30546) wires `close_fn = NULL`, so the
        //    handle stays open: the caller can read the file position after the load to see how far
        //    ufbx got. That is also why `opts == NULL` (30547 `ufbx_load_stream_prefix(&stream, ...,
        //    opts, error)` passes it on) behaves as the all-zero opts of `ufbxi_load()`.
        // The `#else` UFBX_NO_STDIO branch (30551-30561, "Feature disabled" + `deferred_failure`) is
        // compiled out of the reference build, so it has no port-side counterpart.
        public static UfbxScene LoadStdioPrefix(FileStream file, byte[] prefix, int prefixSize,
            UfbxLoadOpts opts, UfbxError error)
        {
            if (file == null) return null;
            UfbxInputStream stream = new UfbxFileInputStream(file, false);
            return LoadStreamPrefix(stream, prefix, prefixSize, opts, error);
        }

        // C: ufbx_default_open_file(void *user, ufbx_stream *stream, const char *path,
        // size_t path_len, const ufbx_open_file_info *info) (ufbx.c:30414-30418).
        //
        // This is the `open_file_cb` default (ufbx.c:25540), and the one C callback a caller can
        // compare function pointers against (`open_file_cb.fn == &ufbx_default_open_file`,
        // ufbx.c:25232), which the port spells as reference equality against
        // `UfbxiLoad.DefaultOpenFile` (Parse/Load.cs:758).
        public static bool DefaultOpenFile(object user, out UfbxInputStream stream, string path,
            int pathLen, UfbxOpenFileInfo info)
        {
            return UfbxiStreamOpen.DefaultOpenFileEntry(user, out stream, path, pathLen, info);
        }

        // C: ufbx_open_file(ufbx_stream *stream, const char *path, size_t path_len,
        // const ufbx_open_file_opts *opts, ufbx_error *error) (ufbx.c:30420-30423) -- ctx == NULL.
        public static bool OpenFile(out UfbxInputStream stream, string path, int pathLen,
            UfbxOpenFileOpts opts, UfbxError error)
        {
            return UfbxiStreamOpen.OpenFile(out stream, path, pathLen, opts, error);
        }

        // C: ufbx_open_file_ctx(ufbx_stream *stream, ufbx_open_file_context ctx, const char *path,
        // size_t path_len, const ufbx_open_file_opts *opts, ufbx_error *error)
        // (ufbx.c:30425-30443). `ufbx_open_file_context` is a `uintptr_t` naming a parent allocator
        // (C: `(ufbx_open_file_context)&uc->ator_tmp`, ufbx.c:25234); the port models neither arenas
        // nor allocator callbacks (PORTING_NOTES.md #4), so `ctx` is accepted and ignored -- see
        // Parse/StreamOpen.cs for the full list of allocator-only observables this drops.
        public static bool OpenFileCtx(out UfbxInputStream stream, nint ctx, string path, int pathLen,
            UfbxOpenFileOpts opts, UfbxError error)
        {
            return UfbxiStreamOpen.OpenFileCtx(out stream, ctx, path, pathLen, opts, error);
        }

        // C: ufbx_open_memory(ufbx_stream *stream, const void *data, size_t data_size,
        // const ufbx_open_memory_opts *opts, ufbx_error *error) (ufbx.c:30445-30448) -- ctx == NULL.
        public static bool OpenMemory(out UfbxInputStream stream, byte[] data, int dataSize,
            UfbxOpenMemoryOpts opts, UfbxError error)
        {
            return UfbxiStreamOpen.OpenMemory(out stream, data, dataSize, opts, error);
        }

        // C: ufbx_open_memory_ctx(ufbx_stream *stream, ufbx_open_file_context ctx, const void *data,
        // size_t data_size, const ufbx_open_memory_opts *opts, ufbx_error *error)
        // (ufbx.c:30450-30503). Always succeeds for the port (the one `return false` is an
        // allocation failure, which managed memory cannot reproduce); always wires `close_fn`, and
        // with `opts->no_copy` the stream aliases `data` instead of copying `data_size` bytes.
        public static bool OpenMemoryCtx(out UfbxInputStream stream, nint ctx, byte[] data,
            int dataSize, UfbxOpenMemoryOpts opts, UfbxError error)
        {
            return UfbxiStreamOpen.OpenMemoryCtx(out stream, ctx, data, dataSize, opts, error);
        }

        // ------------------------------------------------------------------
        // Thread pool: the three `ufbx_unsafe` provider-side ABIs (ufbx.h:5747-5752).
        //
        // All three take `ufbx_thread_pool_context`, a `uintptr_t` C casts straight to its
        // internal `ufbxi_thread_pool *` and dereferences (ufbx.c:32986/32991/32997) — hence
        // `ufbx_unsafe`: any context ufbx itself did not hand out is a wild read/write, and so
        // is a context whose pool has already been freed. Both are undefined behaviour in C;
        // this port raises `UfbxiThreadContextException` for the first form and silently no-ops /
        // returns null for the second, which is the closest a managed port can come to "the
        // address is meaningless". The only legitimate sources of a context are the `ctx`
        // arguments of `UfbxThreadPool.InitFn/RunFn/WaitFn/FreeFn` (Parse/ThreadPool.cs).
        // ------------------------------------------------------------------

        // C: void ufbx_thread_pool_run_task(ufbx_thread_pool_context ctx, uint32_t index)
        // (ufbx.c:32984-32987) -> ufbxi_thread_pool_execute((ufbxi_thread_pool*)ctx, index)
        // (6009-6017): the task in ring slot `index % num_tasks` runs, its error is cleared on
        // success and filled with "" on failure if the task did not set one.
        public static void ThreadPoolRunTask(nint ctx, uint index)
        {
            UfbxiThreadPool pool = UfbxiThreadPool.FromCtx(ctx);
            if (pool == null) throw new UfbxiThreadContextException("ufbx_thread_pool_run_task");
            pool.Execute(index);
        }

        // C: void ufbx_thread_pool_set_user_ptr(ufbx_thread_pool_context ctx, void *user_ptr)
        // (ufbx.c:32989-32993) — a plain store into the pool, distinct from
        // `ufbx_thread_pool.user` (the `void *user` every callback receives): this slot belongs
        // to whichever worker thread owns the context (ufbx.h:4613).
        public static void ThreadPoolSetUserPtr(nint ctx, object userPtr)
        {
            UfbxiThreadPool pool = UfbxiThreadPool.FromCtx(ctx);
            if (pool == null) throw new UfbxiThreadContextException("ufbx_thread_pool_set_user_ptr");
            pool.UserPtr = userPtr;
        }

        // C: void *ufbx_thread_pool_get_user_ptr(ufbx_thread_pool_context ctx)
        // (ufbx.c:32995-32999) — returns NULL until something is set (ufbx.h:5750).
        public static object ThreadPoolGetUserPtr(nint ctx)
        {
            UfbxiThreadPool pool = UfbxiThreadPool.FromCtx(ctx);
            if (pool == null) throw new UfbxiThreadContextException("ufbx_thread_pool_get_user_ptr");
            return pool.UserPtr;
        }

        // C: the zeroed `ufbxi_context uc` of the public entry points (e.g. ufbx.c:30513-30514),
        // plus the `&uc->error` sharing the port needs: the string pool and the maps report into
        // the same `ufbx_error` instance as the context (ufbx.c:25549, 25557).
        static UfbxiContext NewContext()
        {
            UfbxiContext uc = new UfbxiContext();
            uc.Error = new UfbxError();
            uc.Stream = new UfbxiStream();
            return uc;
        }

        // C: the failure tail of `ufbxi_load()` (ufbx.c:25613-25630).
        static void ReportFailure(UfbxiContext uc, UfbxParseError e, UfbxError pError)
        {
            UfbxError err = uc.Error;

            // C: `ufbxi_fail_imp_err(err, ufbxi_error_msg(cond, msg), ...)` writes the
            // description at the failure point, but only while it is still unset; the plain
            // `ufbxi_check`/`ufbxi_fail` forms pass NULL and write nothing (ufbx.c:3415-3448).
            if (e.HasDescription && string.IsNullOrEmpty(err.Description)) {
                err.Description = e.Message;
            }

            UfbxiPrint.FixErrorType(err, UfbxiLoad.DefaultErrorDescription, pError);

            // C: unknown failure in an FBX file whose version is not supported gets rewritten to
            // UFBX_ERROR_UNSUPPORTED_VERSION with the version as the info (ufbx.c:25624-25628).
            if (pError != null && pError.Type == UfbxErrorType.Unknown &&
                uc.Scene.Metadata.FileFormat == UfbxFileFormat.Fbx &&
                !UfbxiLoad.SupportsVersion(uc.Version)) {
                pError.Description = "Unsupported version";
                pError.Type = UfbxErrorType.UnsupportedVersion;
                UfbxiPrint.FmtErrInfo(pError, "%u", new UfbxiVaList().AddUInt(uc.Version));
            }
        }
    }
}
