// Public façade: the standalone geometry-cache entry points (ufbx.h:5711-5728).
// Pure forwarding layer -- the C bodies live in Parse/GeometryCache.cs
// (`UfbxiGeometryCacheLoader.LoadGeometryCache` = `ufbxi_load_geometry_cache` ufbx.c:24726-24763,
// `UfbxiGeometryCacheSample` = ufbx.c:32704-32963); this file only mirrors the `ufbx_` prefix of the
// C ABI, so the S4a differential keeps covering the same implementations.
//
// Conventions:
//  * The NUL-terminated form forwards to the `_len` form with the string's own length, and the `_len`
//    form goes through `ufbxi_safe_string()` first (Api convention, PORTING_NOTES.md "文件布局").
//    C's `ufbx_string` is a (data, length) view; the port's strings carry no separate length, so
//    `UfbxiSceneFind.SafeString` (Parse/SceneFind.cs:34-40) materializes that prefix -- which is what
//    the cache loader needs anyway, since it opens the file by name.
//  * `ufbxi_check_opts_ptr()` at the two load entry points (ufbx.c:32669) is the forgotten-zeroing
//    sentinel and is not representable for the port's options type (same note as `UfbxApi.LoadMemory`).
//  * C returns `size_t` from the read/sample entries; `int` covers every count this port can address
//    (PORTING_NOTES.md "size_t -> int"). C's `ufbx_assert(data)` followed by `if (!data) return 0;`
//    is the port's `data == null` guard, so a null buffer reports "nothing read".
//  * `ufbx_free_geometry_cache`/`ufbx_retain_geometry_cache` are the refcount no-ops in `UfbxApi`.

namespace Ufbx
{
    public static class UfbxGeometryCacheApi
    {
        // ------------------------------------------------------------------
        // Loading (ufbx.c:32657-32672)
        // ------------------------------------------------------------------

        // C: ufbx_load_geometry_cache (ufbx.c:32657-32663, ufbx.h:5711).
        public static UfbxGeometryCache LoadGeometryCache(string filename, UfbxGeometryCacheOpts opts, UfbxError error)
            => LoadGeometryCacheLen(filename, filename != null ? filename.Length : 0, opts, error);

        // C: ufbx_load_geometry_cache_len (ufbx.c:32665-32672, ufbx.h:5714).
        public static UfbxGeometryCache LoadGeometryCacheLen(string filename, int filenameLen, UfbxGeometryCacheOpts opts, UfbxError error)
            => UfbxiGeometryCacheLoader.LoadGeometryCache(UfbxiSceneFind.SafeString(filename, filenameLen), opts, error);

        // ------------------------------------------------------------------
        // Reading and sampling frames (ufbx.c:32704-32963)
        // ------------------------------------------------------------------

        // C: ufbx_read_geometry_cache_real (ufbx.c:32704-32867, ufbx.h:5724).
        public static int ReadGeometryCacheReal(UfbxCacheFrame frame, double[] data, int count, UfbxGeometryCacheDataOpts opts)
            => UfbxiGeometryCacheSample.ReadGeometryCacheReal(frame, data, 0, count, opts);

        // C: ufbx_read_geometry_cache_vec3 (ufbx.c:32941-32951, ufbx.h:5725). `count` is vec3
        // elements; C reinterprets the buffer as `count * 3` reals.
        public static int ReadGeometryCacheVec3(UfbxCacheFrame frame, UfbxVec3[] data, int count, UfbxGeometryCacheDataOpts opts)
            => UfbxiGeometryCacheSample.ReadGeometryCacheVec3(frame, data, 0, count, opts);

        // C: ufbx_sample_geometry_cache_real (ufbx.c:32869-32939, ufbx.h:5727).
        public static int SampleGeometryCacheReal(UfbxCacheChannel channel, double time, double[] data, int count, UfbxGeometryCacheDataOpts opts)
            => UfbxiGeometryCacheSample.SampleGeometryCacheReal(channel, time, data, 0, count, opts);

        // C: ufbx_sample_geometry_cache_vec3 (ufbx.c:32953-32963, ufbx.h:5728).
        public static int SampleGeometryCacheVec3(UfbxCacheChannel channel, double time, UfbxVec3[] data, int count, UfbxGeometryCacheDataOpts opts)
            => UfbxiGeometryCacheSample.SampleGeometryCacheVec3(channel, time, data, 0, count, opts);
    }
}
