// Public façade: the error-formatting entry point.
// Pure forwarding layer over `UfbxiErrorFormat` (Util/ErrorFormat.cs) -- the C body lives
// there, this file only mirrors the `ufbx_` prefix of the C ABI (ufbx.h:5336).
//
// `ufbx_format_error()` is the whole "Errors" section of the ABI that is a function: the
// rest (`ufbx_error`, `ufbx_error_frame`, `ufbx_error_type`) is plain data, ported as
// `UfbxError` / `UfbxErrorFrame` (Model/Opts/RuntimeOpts.cs:183-205) and the
// `UfbxErrorType` enum.
//
// C returns `size_t` (the number of bytes of text, always <= dst_size - 1); the port returns
// `int` per PORTING_NOTES.md "size_t -> int". `dst` is written as raw bytes and is always
// NUL-terminated inside `dst_size` whenever anything is written, including the
// `error == null` case, which writes just the terminator and returns 0.
//
// Deviations from C that cannot be expressed here:
// - C's `ufbxi_check_opts_ptr()`-style "forgot to clear the opts" sentinel does not exist for
//   managed options/errors; `error` may be a fresh `UfbxError` (`Description == ""`, which
//   prints as nothing) where C's zero-initialised struct would print "Unknown error"
//   (`Description == null` is this port's C-NULL).
// - C reads `frame->function.data` / `frame->description.data` straight into `%s`; a NULL
//   there is undefined behaviour in C and a NullReferenceException here.

namespace Ufbx
{
    public static class UfbxErrorApi
    {
        // C: ufbx_format_error (ufbx.c:30606-30642, ufbx.h:5336).
        public static int FormatError(byte[] dst, int dstSize, UfbxError error)
            => UfbxiErrorFormat.FormatError(dst, dstSize, error);
    }
}
