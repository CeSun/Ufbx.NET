// Public façade: the standalone DEFLATE entry point.
// Pure forwarding layer over `UfbxiInflate` (Parse/Inflate.cs) -- the C body lives there,
// this file only mirrors the `ufbx_` prefix of the C ABI (ufbx.h:5409).
//
// `ufbx_inflate()` is the only inflate symbol in the public surface: `ufbx_inflate_input`
// (ufbx.h:4410-4442) and `ufbx_inflate_retain` (ufbx.h:4446-4449) are plain data, ported as
// `UfbxInflateInput` / `UfbxInflateRetain` (Model/GeometryValueTypes.cs:51-82), and
// `ufbxi_inflate_init_retain()` (ufbx.c:3097-3104) is internal -- `ufbx_inflate()` initializes
// the retain itself.
//
// C returns `ptrdiff_t` (the number of bytes written, or one of the negative error codes listed at
// ufbx.c:3106-3134); the port returns `int`, which covers every buffer size it can address
// (PORTING_NOTES.md "size_t -> int"). Both arguments are required: C casts and dereferences `input`
// and `retain` without a NULL check (ufbx.c:3137, 3216), so `null` is undefined behaviour there and
// a `NullReferenceException` here.

namespace Ufbx
{
    public static class UfbxInflateApi
    {
        // C: ufbx_inflate (ufbx.c:3135-3280, ufbx.h:5409).
        public static int Inflate(byte[] dst, int dstSize, UfbxInflateInput input, UfbxInflateRetain retain)
            => UfbxiInflate.UfbxInflate(dst, dstSize, input, retain);
    }
}
