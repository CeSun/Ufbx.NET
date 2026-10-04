using System;

namespace Ufbx
{
    // Ported from ufbx.c v0.23.1 `ufbx_format_error()` (ufbx.c:30606-30642, ufbx.h:5336).
    //
    // This is the only public entry point of the error layer: ufbx itself never calls it
    // (there is no other reference to the symbol in ufbx.c), so no golden and no load path
    // exercises it -- the C reference build's error stack is compiled out too
    // (UFBXI_FEATURE_ERROR_STACK == 0, ufbx.c:170-172), which means the `stack[]` loop below
    // is reachable only through a hand-built `ufbx_error`. See tools/util_oracle.c `FE`
    // records and tools/UtilCheck for the synthetic positive control.
    internal static class UfbxiErrorFormat
    {
        // C: the `error->description.data ? ... : "Unknown error"` fallback (ufbx.c:30621,
        // 30627). NOTE: C distinguishes a NULL `data` from an empty one, so the port maps
        // C's NULL to `null` and C's `ufbxi_empty_char` to `""`; an empty description
        // prints as nothing, exactly like C.
        const string UnknownError = "Unknown error";

        // C: `int line_width = 6;` (ufbx.c:30633) -- the `%*u` min-width of a stack frame's
        // source line.
        const int LineWidth = 6;

        // C: ufbx_format_error (ufbx.c:30606-30642). `dst_size` is a `size_t`; the port keeps
        // the "size_t -> int" narrowing of PORTING_NOTES.md, and the `size_t` return is
        // narrowed the same way -- it is bounded by `dst_size - 1`, so it cannot overflow.
        internal static int FormatError(byte[] dst, int dstSize, UfbxError error)
        {
            if (dst == null || dstSize == 0) return 0;
            if (error == null) { dst[0] = 0; return 0; }

            int offset = 0;

            {
                string desc = error.Description ?? UnknownError;
                int num;
                if (error.InfoLength > 0 && error.InfoLength < UfbxConstants.ErrorInfoLength) {
                    // C: `%.*s` over `error->info` with precision `(int) error->info_length`
                    // (ufbx.c:30622) -- `info` is a NUL-terminated array, so an embedded NUL
                    // shorter than info_length ends the text early in C as well.
                    num = SnprintfAt(dst, offset, dstSize - offset,
                        "ufbx v%u.%u.%u error: %s (%.*s)\n",
                        new UfbxiVaList()
                            .AddUInt(UfbxConstants.SourceVersion / 1000000u)
                            .AddUInt(UfbxConstants.SourceVersion / 1000u % 1000u)
                            .AddUInt(UfbxConstants.SourceVersion % 1000u)
                            .AddStr(desc)
                            .AddInt(error.InfoLength)
                            .AddStr(error.Info));
                } else {
                    num = SnprintfAt(dst, offset, dstSize - offset,
                        "ufbx v%u.%u.%u error: %s\n",
                        new UfbxiVaList()
                            .AddUInt(UfbxConstants.SourceVersion / 1000000u)
                            .AddUInt(UfbxConstants.SourceVersion / 1000u % 1000u)
                            .AddUInt(UfbxConstants.SourceVersion % 1000u)
                            .AddStr(desc));
                }

                // C: `if (num > 0) offset = ufbxi_min_sz(offset + (size_t)num, dst_size - 1);`
                // Kept literally: ufbxi_snprintf() never returns more than its window minus
                // one, so the min() is a tautology here -- but the load-bearing part is the
                // `> 0` test, which leaves `offset` at 0 when nothing was written.
                if (num > 0) offset = Math.Min(offset + num, dstSize - 1);
            }

            // C: `size_t stack_size = ufbxi_min_sz(error->stack_size, UFBX_ERROR_STACK_MAX_DEPTH)`
            // (ufbx.c:30631). `StackSize` is the port's `uint` mirror of C's `uint32_t`.
            uint stackSize = Math.Min(error.StackSize, (uint)UfbxConstants.ErrorStackMaxDepth);
            for (uint i = 0; i < stackSize; i++) {
                UfbxErrorFrame frame = error.Stack[i];
                // C: passes `frame->function.data` / `frame->description.data` to `%s` with no
                // NULL check (ufbx.c:30635) -- NULL is undefined behaviour there (glibc prints
                // "(null)"), the port throws a NullReferenceException, matching the C debug
                // build's `ufbx_assert`.
                int num = SnprintfAt(dst, offset, dstSize - offset, "%*u:%s: %s\n",
                    new UfbxiVaList()
                        .AddInt(LineWidth)
                        .AddUInt(frame.SourceLine)
                        .AddStr(frame.Function)
                        .AddStr(frame.Description));
                if (num > 0) offset = Math.Min(offset + num, dstSize - 1);
            }

            return offset;
        }

        // C: ufbxi_snprintf(dst + offset, dst_size - offset, ...) (ufbx.c:3379-3386) at a byte
        // offset into `dst`. C does this with pointer arithmetic; the port hands the print
        // buffer an absolute start index and `offset + size` as its exclusive end, which makes
        // both the append bound and the trailing `'\0'` land on the same bytes as C's
        // `dst[offset + ...]`. The return value is made relative to the window again,
        // reproducing ufbxi_vsnprintf's `(int) min(pos, buf_size - 1)`.
        //
        // `size >= 1` is an invariant, not a check: `offset` only ever moves to
        // `min(offset + num, dst_size - 1)`, so the window `dst_size - offset` is never empty.
        // It matters because an empty window would make the absolute end `offset + size` equal
        // `offset` and let ufbxi_vprint()'s NUL write land one byte *before* the window, where
        // C (whose buffer length is relative, so `length == 0` skips the NUL entirely) writes
        // nothing.
        static int SnprintfAt(byte[] dst, int offset, int size, string fmt, UfbxiVaList args)
        {
            UfbxiPrintBuffer buf = new UfbxiPrintBuffer(dst, offset + size);
            buf.Pos = offset;
            UfbxiPrint.Vprint(buf, fmt, args);
            ulong max = unchecked((ulong)size - 1ul);
            ulong pos = unchecked((ulong)(buf.Pos - offset));
            ulong min = pos < max ? pos : max;
            return unchecked((int)(long)min);
        }
    }
}
