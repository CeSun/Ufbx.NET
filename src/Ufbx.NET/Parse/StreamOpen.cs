// Ported from ufbx.c v0.23.1 "File IO" / "Memory IO" and the public open ABI:
//   ufbxi_file_context                     (ufbx.c:6941-6946)
//   ufbxi_begin_file_context()             (ufbx.c:6948-6958)
//   ufbxi_end_file_context()               (ufbx.c:6960-6975)
//   ufbxi_fopen()                          (ufbx.c:6981-7065)
//   ufbxi_stdio_init()/stdio_open()        (ufbx.c:7126-7141)
//   ufbxi_memory_stream / memory_close()   (ufbx.c:7186-7237)
//   ufbx_default_open_file()               (ufbx.c:30414-30418)
//   ufbx_open_file()/_ctx()                (ufbx.c:30420-30443)
//   ufbx_open_memory()/_ctx()              (ufbx.c:30445-30503)
//
// Deviations that cannot be expressed in managed code (same style as Parse/CreateAnim.cs):
//  * `ufbxi_alloc()` failure (`ufbx_open_memory_ctx()` returning false, ufbx.c:30468-30471) is
//    not representable; the managed allocation either succeeds or throws OutOfMemoryException.
//  * `ufbx_open_file_context` is an opaque `uintptr_t` carrying a `ufbxi_allocator*`. The port has
//    no arenas and does not model allocator callbacks (PORTING_NOTES.md #4), so `ctx` is accepted
//    and ignored. It is only ever non-NULL when a caller echoes back
//    `ufbx_open_file_info.context`, and the only observable consequence in C is *which* allocator
//    the metadata block and the copied blob are freed through (`ufbx.c:30488-30492`, 30530-30535).
//  * `ufbx_assert(opts->_begin_zero == 0 && opts->_end_zero == 0)` (ufbx.c:30457) and the
//    `ufbxi_check_opts_ptr()` sentinel test are not representable: the port's option classes always
//    initialize their fields.
//  * `ufbx_open_file_ctx()` leaves the caller's `ufbx_stream` *untouched* on failure
//    (`ufbxi_stdio_open()` only wires the callbacks once the file exists); the port assigns `null`,
//    which is the closest managed form and is what every port-side consumer tests.
//  * Only the `_WIN32` branch of `ufbxi_fopen()` (ufbx.c:6984-7040) is modelled -- that is what the
//    reference build compiles on this host -- so `filename_null_terminated` has no effect here (C
//    ignores it in that branch too) and the non-Windows 256-byte `copy_buf` path has no counterpart.
//  * `ufbxi_memory_close()` double-call is a double free in C; the port re-invokes `close_cb`
//    instead, which is the only part of it that C observes before the abort.

using System;
using System.IO;
using System.Text;

namespace Ufbx.NET
{
    // C: ufbxi_file_context (ufbx.c:6941-6946): its own `ufbx_error` plus the allocator pair.
    internal sealed class UfbxiFileContext
    {
        public UfbxError Error = new UfbxError();

        // C: ufbxi_begin_file_context(fc, ctx, ator_opts) (ufbx.c:6948-6958). The `memset()` is the
        // field initializers; the `ctx ? parent_ator : ufbxi_init_ator()` choice has no port-side
        // observable (deviation note at the top of this file).
        internal static UfbxiFileContext Begin(nint ctx, UfbxAllocatorOpts atorOpts)
        {
            return new UfbxiFileContext();
        }

        // C: ufbxi_end_file_context(fc, error, ok) (ufbx.c:6960-6975): the allocator transplant/free
        // is dropped, and what remains is the error hand-off -- a success *clears* the caller's
        // error struct, a failure re-types the context's error under the "Failed to open file"
        // default. Note the asymmetry: the clear happens on success even though the context error is
        // untouched, so `ufbx_open_file()` on a good path resets a dirty `ufbx_error`.
        internal void End(UfbxError pError, bool ok)
        {
            if (pError == null) return;
            if (!ok) {
                UfbxiPrint.FixErrorType(Error, "Failed to open file", pError);
            } else {
                UfbxiPrint.ClearError(pError);
            }
        }
    }

    internal static class UfbxiStreamOpen
    {
        // C: the `_WIN32` half of ufbxi_fopen() (ufbx.c:6996-7031): convert the UTF-8 path bytes to
        // UTF-16 with C's deliberately lax decoder, which accepts stray surrogates ("the Windows
        // file system encoding allows them as well") and, for a sequence truncated by `path_len`,
        // just stops consuming continuation bytes.
        // Returns null for a bad lead byte (C: reports "Invalid UTF-8").
        internal static string PathToUtf16(string path, int length)
        {
            StringBuilder sb = new StringBuilder(length);
            int i = 0;
            while (i < length) {
                uint code;
                // C: `char c = path[i++]` -- signed char; only the low 8 bits matter for the masks.
                int c = path[i++] & 0xff;
                if ((c & 0x80) == 0) {
                    code = (uint)c;
                } else if ((c & 0xe0) == 0xc0) {
                    code = (uint)(c & 0x1f);
                    if (i < length) code = code << 6 | (uint)(path[i++] & 0x3f);
                } else if ((c & 0xf0) == 0xe0) {
                    code = (uint)(c & 0x0f);
                    if (i < length) code = code << 6 | (uint)(path[i++] & 0x3f);
                    if (i < length) code = code << 6 | (uint)(path[i++] & 0x3f);
                } else if ((c & 0xf8) == 0xf0) {
                    code = (uint)(c & 0x07);
                    if (i < length) code = code << 6 | (uint)(path[i++] & 0x3f);
                    if (i < length) code = code << 6 | (uint)(path[i++] & 0x3f);
                    if (i < length) code = code << 6 | (uint)(path[i++] & 0x3f);
                } else {
                    // C: "Bad UTF-8 character, fail early." (ufbx.c:7014-7021)
                    return null;
                }

                if (code < 0x10000) {
                    sb.Append((char)code);
                } else {
                    code -= 0x10000;
                    sb.Append((char)(0xd800 + (code >> 10)));
                    sb.Append((char)(0xdc00 + (code & 0x3ff)));
                }
            }
            return sb.ToString();
        }

        // C: ufbxi_fopen() (ufbx.c:6981-7065) -- returns NULL and reports into the file context.
        // The 256-byte stack buffer vs `ufbxi_alloc()` choice (6986-6991) has no observable.
        static FileStream Fopen(UfbxiFileContext fc, string path, int pathLen)
        {
            string utf16 = PathToUtf16(path, pathLen);
            if (utf16 == null) {
                // C: ufbxi_report_err_msg(&fc->error, "file", "Invalid UTF-8") (ufbx.c:7019). Note
                // that this site does NOT set the info payload; only the open failure below does.
                UfbxiPrint.FailImpErr(fc.Error, "$" + "Invalid UTF-8" + "\0" + "file",
                    "ufbxi_fopen", 7019);
                return null;
            }

            // C hands `wpath` to `_wfopen_s()` as a NUL-terminated `wchar_t*` (ufbx.c:7033-7035),
            // so an embedded U+0000 -- which the lax decoder above happily produces for an
            // interior '\0' byte -- TRUNCATES the path. `path_len` therefore bounds the decode but
            // not the filename, which is why a path like "a.fbx\0junk" opens "a.fbx". Managed
            // `FileStream` has no such rule, so the truncation has to be made explicit here.
            int nul = utf16.IndexOf('\0');
            if (nul >= 0) utf16 = utf16.Substring(0, nul);

            FileStream file;
            try {
                file = new FileStream(utf16, FileMode.Open, FileAccess.Read, FileShare.Read,
                    0x4000, FileOptions.None);
            } catch (Exception) {
                file = null;
            }
            if (file == null) {
                // C: ufbxi_set_err_info(&fc->error, path, path_len) then
                // ufbxi_report_err_msg(&fc->error, "file", "File not found") (ufbx.c:7060-7062).
                UfbxiPrint.SetErrInfo(fc.Error, UfbxiRawStr.ToBytes(path, 0, pathLen), 0, pathLen);
                UfbxiPrint.FailImpErr(fc.Error, "$" + "File not found" + "\0" + "file",
                    "ufbxi_fopen", 7062);
                return null;
            }
            return file;
        }

        // C: ufbxi_stdio_open() (ufbx.c:7135-7141): fopen, then ufbxi_stdio_init(stream, file, true)
        // -- `close == true`, so ufbx owns (and will fclose()) the handle. Also the shared body of
        // the deferred open in `ufbxi_load_imp()` (via `ufbx_default_open_file`), which is why it is
        // internal rather than private.
        internal static UfbxInputStream StdioOpen(UfbxiFileContext fc, string path, int pathLen,
            bool nullTerminated)
        {
            FileStream file = Fopen(fc, path, pathLen);
            if (file == null) return null;
            return new UfbxFileInputStream(file, true);
        }

        // C: ufbx_open_file_ctx(ufbx_stream *stream, ufbx_open_file_context ctx, const char *path,
        // size_t path_len, const ufbx_open_file_opts *opts, ufbx_error *error) (ufbx.c:30425-30443).
        internal static bool OpenFileCtx(out UfbxInputStream stream, nint ctx, string path,
            int pathLen, UfbxOpenFileOpts opts, UfbxError pError)
        {
            UfbxiFileContext fc = UfbxiFileContext.Begin(ctx, null);

            // C: `if (path_len == SIZE_MAX) path_len = strlen(path)` (ufbx.c:30430).
            int length = pathLen;
            if (length == -1) {
                int nul = path.IndexOf('\0');
                length = nul < 0 ? path.Length : nul;
            }

            UfbxInputStream opened = null;
            // C: `ok = ufbxi_stdio_open(&fc, stream, path, path_len,
            //      opts ? opts->filename_null_terminated : false)` (ufbx.c:30432).
            opened = StdioOpen(fc, path, length, opts != null && opts.FilenameNullTerminated);

            bool ok = opened != null;
            stream = opened;
            fc.End(pError, ok);
            return ok;
        }

        // C: ufbx_open_file() (ufbx.c:30420-30423) -- ctx == NULL.
        internal static bool OpenFile(out UfbxInputStream stream, string path, int pathLen,
            UfbxOpenFileOpts opts, UfbxError pError)
        {
            return OpenFileCtx(out stream, 0, path, pathLen, opts, pError);
        }

        // C: ufbx_default_open_file(void *user, ufbx_stream *stream, const char *path,
        // size_t path_len, const ufbx_open_file_info *info) (ufbx.c:30414-30418): forwards
        // `info->context` (C dereferences `info` unconditionally) and passes NULL opts *and* NULL
        // error, so the file context's error is dropped -- the caller only learns success/failure,
        // while `path_len` (which the caller may spell as SIZE_MAX/-1) is still resolved by the
        // strlen rule inside OpenFileCtx().
        internal static bool DefaultOpenFileEntry(object user, out UfbxInputStream stream,
            string path, int pathLen, UfbxOpenFileInfo info)
        {
            return OpenFileCtx(out stream, info.Context, path, pathLen, null, null);
        }

        // C: ufbx_open_memory_ctx(ufbx_stream *stream, ufbx_open_file_context ctx, const void *data,
        // size_t data_size, const ufbx_open_memory_opts *opts, ufbx_error *error)
        // (ufbx.c:30450-30503).
        internal static bool OpenMemoryCtx(out UfbxInputStream stream, nint ctx, byte[] data,
            int dataSize, UfbxOpenMemoryOpts opts, UfbxError pError)
        {
            // C: `if (!opts) { memset(&local_opts, 0, ...); opts = &local_opts; }` (30452-30456) --
            // a NULL opts is the all-zero struct, not "the defaults".
            bool noCopy = opts != null && opts.NoCopy;
            UfbxCloseMemoryCb closeCb = opts != null ? opts.CloseCb : null;

            UfbxiFileContext fc = UfbxiFileContext.Begin(ctx, opts != null ? opts.Allocator : null);

            // C: `copy_size = opts->no_copy ? 0 : data_size` and the flexible-array `memcpy`
            // (30462, 30480-30485): with `no_copy` the stream references the caller's buffer, so
            // later writes through the caller's pointer are visible to the reader; otherwise the
            // stream owns a copy of exactly `data_size` bytes.
            byte[] owned = data;
            if (!noCopy) {
                owned = new byte[dataSize];
                if (dataSize > 0) {
                    Array.Copy(data, 0, owned, 0, dataSize);
                }
            }

            // C: `ufbxi_align_to_mask(sizeof(ufbxi_memory_stream) + copy_size, 7)`, the
            // `ufbxi_alloc()`, the `memset()` of the header and the allocator transplant
            // (30465-30492) are arena bookkeeping with no port-side observable.
            stream = new UfbxMemoryInputStream(owned, dataSize, closeCb);

            // C: the four memory callbacks + `user = mem` (30494-30498), then
            // `ufbxi_end_file_context(&fc, error, true)` (30500).
            fc.End(pError, true);
            return true;
        }

        // C: ufbx_open_memory() (ufbx.c:30445-30448) -- ctx == NULL.
        internal static bool OpenMemory(out UfbxInputStream stream, byte[] data, int dataSize,
            UfbxOpenMemoryOpts opts, UfbxError pError)
        {
            return OpenMemoryCtx(out stream, 0, data, dataSize, opts, pError);
        }
    }
}
