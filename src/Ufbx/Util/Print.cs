using System;
using System.Collections.Generic;

namespace Ufbx
{
    // Ported from ufbx.c v0.23.1 "Printf" + "Errors" helpers
    // (ufbx.c:3286-3386, 3388-3407, 3415-3448, 3502-3560, 3563-3616).
    //
    // ufbx implements the *tiny* printf subset it needs, not the libc one: the only
    // conversions are `%u`, `%zu`, `%s` and `%%`, with optional `*` min-width and `.*`
    // max-width taken as `int` arguments (ufbx.c:3322-3366). Anything else is
    // `ufbxi_unreachable("Bad printf format")`, which is a no-op in a release build
    // (ufbx.c:1034) and simply appends nothing. Output must match byte-for-byte because
    // these strings land in `ufbx_error.info` and in warning descriptions.

    // C: ufbxi_print_buffer (ufbx.c:3286-3290).
    internal sealed class UfbxiPrintBuffer
    {
        public byte[] Dst;    // C: char *dst
        public int Length;    // C: size_t length
        public int Pos;       // C: size_t pos

        public UfbxiPrintBuffer(byte[] dst, int length)
        {
            Dst = dst;
            Length = length;
            Pos = 0;
        }
    }

    // C: va_list as consumed by ufbxi_vprint() (ufbx.c:3322). x64 (MSVC/MinGW) gives every
    // variadic argument a 64-bit slot and reads it back with the width the format asks for,
    // so `%u` sees the low 32 bits of the slot and `%*` sees `(size_t)(int)` of those bits;
    // `ReadIntAsSizeT`/`ReadUInt32` below reproduce exactly that.
    internal sealed class UfbxiVaList
    {
        const int KindInt32 = 0;
        const int KindUInt32 = 1;
        const int KindSizeT = 2;
        const int KindString = 3;

        struct Arg
        {
            public int Kind;
            public ulong Value;
            public string Str;
        }

        readonly List<Arg> _args = new List<Arg>();
        int _pos;

        // C: an `int` argument.
        public UfbxiVaList AddInt(int value)
        {
            _args.Add(new Arg { Kind = KindInt32, Value = unchecked((ulong)(long)value) });
            return this;
        }

        // C: a `uint32_t` argument.
        public UfbxiVaList AddUInt(uint value)
        {
            _args.Add(new Arg { Kind = KindUInt32, Value = value });
            return this;
        }

        // C: a `size_t` / `uint64_t` argument.
        public UfbxiVaList AddSizeT(ulong value)
        {
            _args.Add(new Arg { Kind = KindSizeT, Value = value });
            return this;
        }

        // C: a `const char*` argument.
        public UfbxiVaList AddStr(string value)
        {
            _args.Add(new Arg { Kind = KindString, Str = value });
            return this;
        }

        public void Reset()
        {
            _pos = 0;
        }

        ulong Next()
        {
            // C: reading past the argument list is undefined behaviour; hand back zeroes
            // rather than throwing so format/argument mismatches degrade like C does.
            if (_pos >= _args.Count) return 0;
            return _args[_pos++].Value;
        }

        // C: min_width/max_width = (size_t)va_arg(args, int) (ufbx.c:3330, 3335): read 32
        // bits, interpret as `int`, then sign-extend to `size_t` (a negative width becomes a
        // huge one, exactly like C).
        public ulong ReadIntAsSizeT()
        {
            return unchecked((ulong)(long)unchecked((int)(uint)Next()));
        }

        // C: (uint64_t)va_arg(args, uint32_t) for `%u` (ufbx.c:3351).
        public ulong ReadUInt32()
        {
            return unchecked((uint)Next());
        }

        // C: (uint64_t)va_arg(args, size_t) for `%zu` (ufbx.c:3351).
        public ulong ReadSizeT()
        {
            return Next();
        }

        // C: va_arg(args, const char*) for `%s` (ufbx.c:3348). An integral slot read as a
        // pointer would be a wild pointer in C, so hand back null and let it fail the same way.
        public string ReadString()
        {
            if (_pos >= _args.Count) { _pos++; return null; }
            Arg arg = _args[_pos++];
            return arg.Kind == KindString ? arg.Str : null;
        }
    }

    internal static class UfbxiPrint
    {
        // C: UFBXI_PRINT_UNSIGNED / UFBXI_PRINT_STRING / UFBXI_PRINT_SIZE_T (ufbx.c:3292-3294)
        const uint PrintUnsigned = 0x1;
        const uint PrintString = 0x2;
        const uint PrintSizeT = 0x10;

        // C: `char buffer[96]` in ufbxi_vprint (ufbx.c:3324).
        const int FormatIntScratch = 96;

        // C: ufbx_panic_handler() (ufbx.c:388-397) ignores the message unless the user
        // overrides the macro; the port exposes a hook that is null by default.
        internal static Action<string> PanicHandler;

        // C: reads `fmt[p]`, treating the end of the string as the NUL terminator the C loop
        // still tests (`for (const char *p = fmt; *p;)`, ufbx.c:3325).
        static char FmtAt(string fmt, int p)
        {
            return p < fmt.Length ? fmt[p] : '\0';
        }

        // C: ufbxi_print_append (ufbx.c:3296-3309) over a NUL-terminated byte buffer.
        internal static void PrintAppend(UfbxiPrintBuffer buf, ulong minWidth, ulong maxWidth, byte[] str, int offset)
        {
            ulong width = 0;
            for (width = 0; width < maxWidth; width++) {
                if (offset + (int)width >= str.Length) break;
                if (str[offset + (int)width] == 0) break;
            }
            ulong pad = minWidth > width ? minWidth - width : 0;
            for (ulong i = 0; i < pad; i++) {
                // C keeps spinning once the buffer is full; breaking here is output-identical.
                if (buf.Pos >= buf.Length) break;
                buf.Dst[buf.Pos++] = (byte)' ';
            }
            for (ulong i = 0; i < width; i++) {
                if (buf.Pos >= buf.Length) break;
                buf.Dst[buf.Pos++] = str[offset + (int)i];
            }
        }

        // C: ufbxi_print_append (ufbx.c:3296-3309) over a NUL-terminated raw-byte string.
        internal static void PrintAppend(UfbxiPrintBuffer buf, ulong minWidth, ulong maxWidth, string str)
        {
            ulong width = 0;
            for (width = 0; width < maxWidth; width++) {
                if (str == null) throw new NullReferenceException("ufbxi_print_append(NULL)");
                if (width >= (ulong)str.Length) break;
                if (str[(int)width] == '\0') break;
            }
            ulong pad = minWidth > width ? minWidth - width : 0;
            for (ulong i = 0; i < pad; i++) {
                if (buf.Pos >= buf.Length) break;
                buf.Dst[buf.Pos++] = (byte)' ';
            }
            for (ulong i = 0; i < width; i++) {
                if (buf.Pos >= buf.Length) break;
                buf.Dst[buf.Pos++] = (byte)str[(int)i];
            }
        }

        // C: ufbxi_print_format_int (ufbx.c:3311-3320): writes the decimal digits backwards
        // into a NUL-terminated buffer and returns the offset of the first digit.
        // `long` is 32-bit on the x64 Windows golden build, so this is the unsigned 64-bit
        // path (`%zu`/`%u` only); there is no signed conversion in ufbxi_vprint at all.
        internal static int PrintFormatInt(byte[] buffer, ulong value)
        {
            int p = buffer.Length;
            buffer[--p] = 0;
            do {
                uint digit = unchecked((uint)(value % 10));
                value = value / 10;
                buffer[--p] = (byte)('0' + digit);
            } while (value > 0);
            return p;
        }

        // C: ufbxi_vprint (ufbx.c:3322-3366).
        internal static void Vprint(UfbxiPrintBuffer buf, string fmt, UfbxiVaList args)
        {
            byte[] buffer = new byte[FormatIntScratch]; // C: char buffer[96] (uninit)
            int p = 0;
            while (FmtAt(fmt, p) != '\0') {
                if (FmtAt(fmt, p) == '%') {
                    p++;
                    if (FmtAt(fmt, p) != '%') {
                        ulong minWidth = 0;
                        ulong maxWidth = ulong.MaxValue; // C: SIZE_MAX
                        if (FmtAt(fmt, p) == '*') {
                            p++;
                            minWidth = args.ReadIntAsSizeT();
                        }
                        if (FmtAt(fmt, p) == '.') {
                            // C: ufbxi_dev_assert(p[1] == '*') — only `.*` precision exists.
                            p += 2;
                            maxWidth = args.ReadIntAsSizeT();
                        }
                        uint flags = 0;
                        switch (FmtAt(fmt, p)) {
                        case 'z': p++; flags |= PrintSizeT; break;
                        default: break;
                        }
                        switch (FmtAt(fmt, p)) {
                        case 'u': flags |= PrintUnsigned; break;
                        case 's': flags |= PrintString; break;
                        default: break;
                        }
                        p++;

                        if ((flags & PrintString) != 0) {
                            string str = args.ReadString();
                            PrintAppend(buf, minWidth, maxWidth, str);
                        } else if ((flags & PrintUnsigned) != 0) {
                            ulong value = (flags & PrintSizeT) != 0 ? args.ReadSizeT() : args.ReadUInt32();
                            int start = PrintFormatInt(buffer, value);
                            PrintAppend(buf, minWidth, maxWidth, buffer, start);
                        }
                        // C: ufbxi_unreachable("Bad printf format") is ufbx_assert(0), a no-op
                        // in release builds (ufbx.c:1034, 3355): nothing is appended.
                    } else {
                        if (buf.Pos < buf.Length) buf.Dst[buf.Pos++] = (byte)FmtAt(fmt, p);
                        p++;
                    }
                } else {
                    if (buf.Pos < buf.Length) buf.Dst[buf.Pos++] = (byte)FmtAt(fmt, p);
                    p++;
                }
            }
            if (buf.Length != 0 && buf.Dst != null) {
                int end = buf.Pos <= buf.Length - 1 ? buf.Pos : buf.Length - 1;
                buf.Dst[end] = 0;
            }
        }

        // C: ufbxi_vsnprintf (ufbx.c:3372-3377).
        internal static int Vsnprintf(byte[] buf, int bufSize, string fmt, UfbxiVaList args)
        {
            UfbxiPrintBuffer buffer = new UfbxiPrintBuffer(buf, bufSize);
            Vprint(buffer, fmt, args);
            // C: (int)ufbxi_min_sz(buffer.pos, buf_size - 1); `buf_size - 1` wraps to SIZE_MAX
            // when buf_size == 0, and the (int) cast truncates.
            ulong max = unchecked((ulong)bufSize - 1ul);
            ulong pos = unchecked((ulong)buffer.Pos);
            ulong min = pos < max ? pos : max;
            return unchecked((int)(long)min);
        }

        // C: ufbxi_snprintf (ufbx.c:3379-3386) — the va_start/va_end plumbing disappears.
        internal static int Snprintf(byte[] buf, int bufSize, string fmt, UfbxiVaList args)
        {
            return Vsnprintf(buf, bufSize, fmt, args);
        }

        // C: ufbxi_panicf_imp (ufbx.c:3388-3407), non-NULL `panic`.
        internal static void Panicf(ref UfbxPanic panic, string fmt, UfbxiVaList args)
        {
            if (panic.DidPanic) return;
            panic.DidPanic = true;
            byte[] message = new byte[UfbxConstants.PanicMessageLength];
            int len = Vsnprintf(message, message.Length, fmt, args);
            panic.MessageLength = len;
            panic.Message = UfbxiRawStr.FromBytes(message, 0, len < 0 ? 0 : len);
        }

        // C: ufbxi_panicf_imp (ufbx.c:3388-3407), NULL `panic` → ufbx_panic_handler().
        internal static void Panicf(string fmt, UfbxiVaList args)
        {
            byte[] message = new byte[UfbxConstants.PanicMessageLength];
            int len = Vsnprintf(message, message.Length, fmt, args);
            string str = UfbxiRawStr.FromBytes(message, 0, len < 0 ? 0 : len);
            if (PanicHandler != null) PanicHandler(str);
        }

        // C: ufbxi_fail_imp_err (ufbx.c:3415-3448) in the golden configuration, i.e.
        // UFBXI_FEATURE_ERROR_STACK == 0 (ufbx.c:170-172): the error stack push and the
        // `cond + strlen(cond) + 1` skip of the description part are both compiled out, and
        // `func`/`line` are ignored. Returns C's `0`; callers turn that into a failure.
        internal static void FailImpErr(UfbxError err, string cond, string func, uint line)
        {
            if (cond != null && cond.Length > 0 && cond[0] == '$') {
                // C: `if (!err->description.data)` — "" is this port's NULL ufbx_string.
                if (string.IsNullOrEmpty(err.Description)) {
                    int nul = cond.IndexOf('\0', 1);
                    err.Description = nul < 0 ? cond.Substring(1) : cond.Substring(1, nul - 1);
                }
            }
            // C: ufbxi_ignore(func); ufbxi_ignore(line);
        }

        // C: ufbxi_set_err_info (ufbx.c:3502-3512). `length == -1` is this port's SIZE_MAX
        // sentinel, which makes C take strlen(data) instead.
        internal static void SetErrInfo(UfbxError err, byte[] data, int offset, int length)
        {
            if (err == null) return;

            if (length == -1) {
                // C: `if (length == SIZE_MAX) length = strlen(data)`
                int nul = offset;
                while (nul < data.Length && data[nul] != 0) nul++;
                length = nul - offset;
            }

            int toCopy = Math.Min(UfbxConstants.ErrorInfoLength - 1, length);
            byte[] info = new byte[toCopy];
            if (toCopy > 0) {
                Array.Copy(data, offset, info, 0, toCopy);
            }
            // C: err->info[to_copy] = '\0' — implicit in the string representation.
            UfbxiUtf8.CleanStringUtf8(info, 0, toCopy);
            err.Info = UfbxiRawStr.FromBytes(info, 0, toCopy);
            err.InfoLength = toCopy;
        }

        // C: ufbxi_set_err_info (ufbx.c:3502-3512) with a NUL-terminated raw-byte string
        // (the `length == SIZE_MAX` path of the byte overload above).
        internal static void SetErrInfo(UfbxError err, string data)
        {
            if (err == null) return;
            int nul = data.IndexOf('\0');
            int length = nul < 0 ? data.Length : nul;
            SetErrInfo(err, UfbxiRawStr.ToBytes(data, 0, length), 0, length);
        }

        // C: ufbxi_fmt_err_info (ufbx.c:3514-3523).
        internal static void FmtErrInfo(UfbxError err, string fmt, UfbxiVaList args)
        {
            if (err == null) return;

            byte[] info = new byte[UfbxConstants.ErrorInfoLength];
            int len = Vsnprintf(info, info.Length, fmt, args);
            if (len < 0) len = 0;
            if (len > info.Length) len = info.Length;
            UfbxiUtf8.CleanStringUtf8(info, 0, len);
            err.InfoLength = len;
            err.Info = UfbxiRawStr.FromBytes(info, 0, len);
        }

        // C: ufbxi_clear_error (ufbx.c:3525-3535). `ufbxi_empty_char` (ufbx.c:3370) is the
        // single canonical empty string, matching `string.Empty`.
        internal static void ClearError(UfbxError err)
        {
            if (err == null) return;

            err.Type = UfbxErrorType.None;
            err.Description = string.Empty;
            err.StackSize = 0;
            err.Info = string.Empty;
            err.InfoLength = 0;
        }

        // C: ufbxi_fix_error_type (ufbx.c:3563-3616): derive `ufbx_error.type` from the
        // description string and copy the internal error into the user's `p_error`.
        internal static void FixErrorType(UfbxError error, string defaultDesc, UfbxError pError)
        {
            string desc = error.Description;
            if (string.IsNullOrEmpty(desc)) desc = defaultDesc;
            error.Type = UfbxErrorType.Unknown;
            if (desc == "Out of memory") {
                error.Type = UfbxErrorType.OutOfMemory;
            } else if (desc == "Memory limit exceeded") {
                error.Type = UfbxErrorType.MemoryLimit;
            } else if (desc == "Allocation limit exceeded") {
                error.Type = UfbxErrorType.AllocationLimit;
            } else if (desc == "Truncated file") {
                error.Type = UfbxErrorType.TruncatedFile;
            } else if (desc == "IO error") {
                error.Type = UfbxErrorType.Io;
            } else if (desc == "Cancelled") {
                error.Type = UfbxErrorType.Cancelled;
            } else if (desc == "Unrecognized file format") {
                error.Type = UfbxErrorType.UnrecognizedFileFormat;
            } else if (desc == "File not found") {
                error.Type = UfbxErrorType.FileNotFound;
            } else if (desc == "Empty file") {
                error.Type = UfbxErrorType.EmptyFile;
            } else if (desc == "External file not found") {
                error.Type = UfbxErrorType.ExternalFileNotFound;
            } else if (desc == "Uninitialized options") {
                error.Type = UfbxErrorType.UninitializedOptions;
            } else if (desc == "Zero vertex size") {
                error.Type = UfbxErrorType.ZeroVertexSize;
            } else if (desc == "Truncated vertex stream") {
                error.Type = UfbxErrorType.TruncatedVertexStream;
            } else if (desc == "Invalid UTF-8") {
                error.Type = UfbxErrorType.InvalidUtf8;
            } else if (desc == "Feature disabled") {
                error.Type = UfbxErrorType.FeatureDisabled;
            } else if (desc == "Bad NURBS geometry") {
                error.Type = UfbxErrorType.BadNurbs;
            } else if (desc == "Bad index") {
                error.Type = UfbxErrorType.BadIndex;
            } else if (desc == "Node depth limit exceeded") {
                error.Type = UfbxErrorType.NodeDepthLimit;
            } else if (desc == "Threaded ASCII parse error") {
                error.Type = UfbxErrorType.ThreadedAsciiParse;
            } else if (desc == "Unsafe options") {
                error.Type = UfbxErrorType.UnsafeOptions;
            } else if (desc == "Duplicate override") {
                error.Type = UfbxErrorType.DuplicateOverride;
            }
            error.Description = desc;
            if (pError != null) {
                pError.Type = error.Type;
                pError.Description = error.Description;
                pError.StackSize = error.StackSize;
                Array.Copy(error.Stack, pError.Stack, error.Stack.Length);
                pError.Info = error.Info;
                pError.InfoLength = error.InfoLength;
            }
        }
    }
}
