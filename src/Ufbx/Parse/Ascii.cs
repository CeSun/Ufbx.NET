// ASCII FBX tokenizer and scalar/array-element parsers, ported from ufbx.c v0.23.1
// ufbx.c:9400-10220 (see tools/ufbx_funcmap.txt). `UfbxiAscii` is the `uc->ascii` state
// (Parse/AsciiState.cs); the algorithms are instance methods of the same partial class so
// that C's `ufbxi_ascii_*(uc, ...)` call sites read as `ua->...` in one place.
//
// Faithfulness notes worth keeping in mind before editing:
//  * C's `ua->src`/`src_yield`/`src_end` are pointers; here they are indices into
//    `SrcBuffer`, which is always the array currently referenced by `Stream.Buffer`, so
//    `ua->src - uc->data_begin` == `Src - Stream.BeginIndex` (used by `Refill()` for the
//    `uc->data_offset` accounting that drives `ufbxi_get_read_offset()`/progress).
//  * `Refill()` replaces the whole read window (it does *not* preserve leftover bytes like
//    the binary `ufbxi_refill()` does) and issues exactly ONE read, so a short read
//    truncates the window - C's "TODO: Very unoptimal for non-full-size reads" quirk.
//  * After EOF without a read function C parks the window on the static `""` buffer with
//    one byte of extent; `EmptyBuffer` reproduces that byte-exactly.
//  * C's two failure forms are reproduced per site (Parse/Error.cs): `ufbxi_check_msg`
//    sites throw with the message as `error.description`, plain `ufbxi_check`/`ufbxi_fail`
//    sites go through `UfbxiFail.FailNoDesc` and write nothing, so the API reports the
//    "Failed to load" default (audited 2026-10-03 against ufbx.c:9400-10220).
//  * The C thread-pool array tasks (`ufbxi_ascii_array_task_*`) are ported as the
//    single-threaded `pool == NULL` path (PORTING_NOTES.md #7); the chunk/stride iteration
//    order is preserved verbatim because it determines both the resulting bytes and where
//    an error is reported.
//  * No FP contraction anywhere (PORTING_NOTES.md 浮点语义): doubles/floats are only
//    produced through `UfbxiNumeric.ParseDouble` and plain assignments/casts.

using System;
using System.Buffers.Binary;

namespace Ufbx
{
    internal sealed partial class UfbxiAscii
    {
        // C: the `static const char ufbxi_ascii_empty[] = ""` equivalent: after a refill
        // with no read function the window points here with `src_end = src + 1`.
        static readonly byte[] EmptyBuffer = new byte[1];

        // C: `ufbxi_parse_double_init_flags()` (ufbx.c:1795-1805). The runtime probe checks
        // that arithmetic is evaluated in double precision; C# always evaluates `double`
        // operations as IEEE-754 double, so the fast path is always permitted.
        internal const uint ParseDoubleInitFlags = UfbxiNumeric.ParseDoubleAllowFastPath;

        // C: task->error / ufbxi_check_msg(...) for the deferred ASCII array task.
        internal const string ThreadedAsciiParseError = "Threaded ASCII parse error";

        // C: ufbxi_space_mask (ufbx.c:9526-9531) plus `ufbxi_static_assert(space_codepoint)`:
        // only the four characters below are whitespace and all of them are <= 32.
        const uint SpaceMask =
            (1u << ((int)' ' - 1)) |
            (1u << ((int)'\t' - 1)) |
            (1u << ((int)'\r' - 1)) |
            (1u << ((int)'\n' - 1));

        // ------------------------------------------------------------------
        // Byte/character helpers for the window
        // ------------------------------------------------------------------

        // C: ufbxi_forceinline bool ufbxi_is_space(char c) (ufbx.c:9541-9545)
        internal static bool IsSpace(char c)
        {
            uint v = unchecked((uint)(byte)c - 1u);
            return v < 32u && ((SpaceMask >> (int)v) & 0x1u) != 0;
        }

        static bool IsSpace(byte c)
        {
            uint v = (uint)c - 1u;
            return v < 32u && ((SpaceMask >> (int)v) & 0x1u) != 0;
        }

        // Read a byte of the current window. Used where C dereferences a pointer past the
        // declared range (see the notes in the array-task parsers); a position beyond the
        // array yields 0, which is neither whitespace nor a delimiter, so the scanning
        // loops terminate the way they do for the NUL-terminated token buffer in C.
        static byte At(byte[] buf, int index)
        {
            return (uint)index < (uint)buf.Length ? buf[index] : (byte)0;
        }

        // C: memchr(ua->src, dst, ufbxi_to_size(ua->src_yield - ua->src))
        static int IndexOf(byte[] buf, int start, int count, byte value)
        {
            int end = start + count;
            for (int i = start; i < end; i++) {
                if (buf[i] == value) return i;
            }
            return -1;
        }

        static void WriteInt32Le(byte[] dst, int offset, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(offset), value);

        static void WriteInt64Le(byte[] dst, int offset, long value)
            => BinaryPrimitives.WriteInt64LittleEndian(dst.AsSpan(offset), value);

        static void WriteFloatLe(byte[] dst, int offset, float value)
            => BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(offset), UfbxBitUtil.SingleToBits(value));

        static void WriteDoubleLe(byte[] dst, int offset, double value)
            => BinaryPrimitives.WriteUInt64LittleEndian(dst.AsSpan(offset), unchecked((ulong)UfbxBitUtil.BitsOf(value)));

        // ------------------------------------------------------------------
        // ufbxi_parse_int64 (ufbx.c:1807-1829) - not part of Parse/Numeric.cs
        // ------------------------------------------------------------------

        // Returns the digit count consumed through `pEnd` (relative to `start`), or
        // `pEnd = -1` for C's `*end = NULL` failure. `abs_val` wraps as in C's uint64_t.
        internal static long ParseInt64(byte[] str, int start, out int pEnd)
        {
            ulong absVal = 0;
            bool negative = At(str, start) == (byte)'-';
            bool positive = At(str, start) == (byte)'+';

            int initLen = negative || positive ? 1 : 0;
            int len = initLen;
            for (; len < 30; len++) {
                byte c = At(str, start + len);
                if (!(c >= (byte)'0' && c <= (byte)'9')) break;
                absVal = 10 * absVal + (ulong)(c - (byte)'0');
            }
            if (len == 30 || len == initLen) {
                pEnd = -1;
                return 0;
            }

            pEnd = len;
            return negative ? unchecked((long)(0UL - absVal)) : unchecked((long)absVal);
        }

        // ------------------------------------------------------------------
        // Window refill / yield / peek / next
        // ------------------------------------------------------------------

        // C: char ufbxi_ascii_refill(ufbxi_context *uc) (ufbx.c:9408-9451)
        internal char Refill()
        {
            // C: uc->data_offset += ufbxi_to_size(ua->src - uc->data_begin)
            Stream.DataOffset += (ulong)(Src - Stream.BeginIndex);

            if (InputStream != null) {
                byte[] dstBuffer;
                int dstOffset;
                int dstSize;

                if (RetainBuf != null) {
                    // Long tokens must survive the refill: read into the retained buffer.
                    dstSize = OptReadBufferSize;
                    dstOffset = RetainBuf.Push(dstSize);
                    dstBuffer = RetainBuf.Data;
                    SrcIsRetained = true;
                    SrcBuf = RetainBuf;
                } else {
                    // Grow the read buffer if necessary (C: ufbxi_grow_array, i.e.
                    // new_cap = max(old_cap * 2, n)); an allocation failure is an OOM
                    // exception here instead of C's `return '\0'`.
                    if (Stream.ReadBufferSize < OptReadBufferSize) {
                        int newSize = Stream.ReadBufferSize * 2;
                        if (newSize < OptReadBufferSize) newSize = OptReadBufferSize;
                        byte[] grown = new byte[newSize];
                        if (Stream.ReadBuffer != null) {
                            Array.Copy(Stream.ReadBuffer, 0, grown, 0, Stream.ReadBufferSize < newSize ? Stream.ReadBufferSize : newSize);
                        }
                        Stream.ReadBuffer = grown;
                        Stream.ReadBufferSize = newSize;
                    }
                    dstBuffer = Stream.ReadBuffer;
                    dstSize = Stream.ReadBufferSize;
                    dstOffset = 0;
                    SrcIsRetained = false;
                    SrcBuf = null;
                }

                // Read user data, return '\0' on EOF
                // TODO: Very unoptimal for non-full-size reads in some cases
                int numRead = InputStream.Read(dstBuffer, dstOffset, dstSize);
                if (numRead < 0) {
                    // C: ufbxi_check_return_msg(num_read != SIZE_MAX, '\0', "IO error")
                    // (ufbx.c:9437). This is a `check_return_*`, NOT a failure propagation:
                    // C writes "IO error" into `uc->error` and then returns '\0', which the
                    // tokenizer reads as UFBXI_ASCII_END -- i.e. a read_fn that fails mid-file
                    // SILENTLY TRUNCATES the ASCII stream, and the load still succeeds (the
                    // success tail clears the error again, ufbx.c:25618). Throwing here turned
                    // a recoverable EOF into "IO error", which is the one divergence the
                    // scripted-stream variants of batch L found; only a read_fn that fails
                    // *inside* `ufbxi_ascii_refill()` can reach it, because every other read
                    // site propagates (ufbx.c:6745, 6907).
                    if (Ctx != null && Ctx.Error != null && string.IsNullOrEmpty(Ctx.Error.Description)) {
                        Ctx.Error.Description = "IO error";
                    }
                    return '\0';
                }
                if (numRead > dstSize) {
                    // C: ufbxi_check_return(num_read <= dst_size, '\0') -- plain, so no
                    // description is written; the value returned is still EOF, not a failure.
                    return '\0';
                }
                if (numRead == 0) return '\0';

                // C: uc->data = uc->data_begin = ua->src = dst_buffer
                Stream.Buffer = dstBuffer;
                Stream.BeginIndex = dstOffset;
                Stream.Position = dstOffset;
                SrcBuffer = dstBuffer;
                Src = dstOffset;
                SrcEnd = dstOffset + numRead;
                return (char)SrcBuffer[Src];
            } else {
                // If the user didn't specify a `read_fn()` treat anything
                // past the initial data buffer as EOF.
                Stream.Buffer = EmptyBuffer;
                Stream.BeginIndex = 0;
                Stream.Position = 0;
                SrcBuffer = EmptyBuffer;
                Src = 0;
                SrcEnd = 1;
                return '\0';
            }
        }

        // C: char ufbxi_ascii_yield(ufbxi_context *uc) (ufbx.c:9453-9474)
        internal char Yield()
        {
            char ret;
            if (Src == SrcEnd) {
                ret = Refill();
            } else {
                ret = (char)At(SrcBuffer, Src);
            }

            if ((ulong)(SrcEnd - Src) < ProgressInterval) {
                SrcYield = SrcEnd;
            } else {
                SrcYield = Src + (int)ProgressInterval;
            }

            // TODO: Unify these properly
            // C: uc->data = ua->src; ufbxi_check_return(ufbxi_report_progress(uc), '\0');
            Stream.Position = Src;
            if (!Stream.ReportProgress()) return '\0';
            return ret;
        }

        // C: char ufbxi_ascii_peek(ufbxi_context *uc) (ufbx.c:9476-9481)
        internal char Peek()
        {
            if (Src == SrcYield) return Yield();
            return (char)At(SrcBuffer, Src);
        }

        // C: char ufbxi_ascii_next(ufbxi_context *uc) (ufbx.c:9483-9490)
        internal char Next()
        {
            if (Src == SrcYield) return Yield();
            Src++;
            if (Src == SrcYield) return Yield();
            return (char)At(SrcBuffer, Src);
        }

        // ------------------------------------------------------------------
        // Version comment
        // ------------------------------------------------------------------

        // C: uint32_t ufbxi_ascii_parse_version(ufbxi_context *uc) (ufbx.c:9492-9524)
        internal uint ParseVersion()
        {
            byte[] digits = new byte[3];
            uint numDigits = 0;

            char c = Next();

            const string fmt = " FBX ?.?.?";
            uint ix = 0;
            while (numDigits < 3) {
                // C reads the NUL terminator at fmt[10] here; it is unreachable (the third
                // '?' both fills `num_digits` and ends the loop), and running past it would
                // read out of bounds, so bail out deterministically instead.
                if (ix > (uint)fmt.Length) return 0;
                char reference = ix < (uint)fmt.Length ? fmt[(int)ix] : '\0';
                ix++;
                switch (reference) {

                // Digit
                case '?':
                    if (c < '0' || c > '9') return 0;
                    digits[numDigits++] = (byte)(c - '0');
                    c = Next();
                    break;

                // Whitespace
                case ' ':
                    while (c == ' ' || c == '\t') {
                        c = Next();
                    }
                    break;

                // Literal character
                default:
                    if (c != reference) return 0;
                    c = Next();
                    break;
                }
            }

            if (numDigits != 3) return 0;
            return 1000u * digits[0] + 100u * digits[1] + 10u * digits[2];
        }

        // ------------------------------------------------------------------
        // Whitespace / comments
        // ------------------------------------------------------------------

        // C: char ufbxi_ascii_skip_whitespace(ufbxi_context *uc) (ufbx.c:9547-9605)
        internal char SkipWhitespace()
        {
            // Ignore whitespace
            char c = Peek();
            for (;;) {
                while (IsSpace(c)) {
                    c = Next();
                }

                // Line comment
                if (c == ';') {

                    bool readMagic = false;
                    // FBX ASCII files begin with a magic comment of form "; FBX 7.7.0 project file"
                    // Try to extract the version number from the magic comment
                    if (!ReadFirstComment) {
                        ReadFirstComment = true;
                        uint version = ParseVersion();
                        if (version != 0) {
                            Ctx.Version = version;
                            FoundVersion = true;
                            readMagic = true;
                        }
                    }

                    c = Next();
                    while (c != '\n' && c != '\0') {
                        c = Next();
                    }
                    c = Next();

                    // Try to determine if this is a Blender 6100 ASCII file
                    if (readMagic) {
                        if (c == ';') {
                            byte[] line = new byte[32];
                            int lineLen = 0;

                            c = Next();
                            while (c != '\n' && c != '\0') {
                                if (lineLen < line.Length) {
                                    line[lineLen++] = (byte)c;
                                }
                                c = Next();
                            }

                            if (lineLen >= 19 && IsCreatedByBlender(line)) {
                                // C: uc->exporter = UFBX_EXPORTER_BLENDER_ASCII (ufbx.c:9595).
                                // Written straight onto the shared context now that
                                // `ufbxi_context` carries the field; the flag stays for callers
                                // that inspect just the ASCII state.
                                Ctx.Exporter = UfbxExporter.BlenderAscii;
                                FoundBlenderAscii = true;
                            }
                        }
                    }

                } else {
                    break;
                }
            }
            return c;
        }

        // C: !memcmp(line, " Created by Blender", 19) (ufbx.c:9593-9595)
        static bool IsCreatedByBlender(byte[] line)
        {
            const string needle = " Created by Blender";
            for (int i = 0; i < 19; i++) {
                if (line[i] != (byte)needle[i]) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Token string buffers
        // ------------------------------------------------------------------

        // C: ufbxi_ascii_push_token_char(uc, token, c) (ufbx.c:9607-9618)
        static void PushTokenChar(UfbxiAsciiToken token, char c)
        {
            // Grow the string data buffer if necessary
            if (token.StrLen == token.StrCap) {
                int len = token.StrLen + 1 > 256 ? token.StrLen + 1 : 256;
                Grow(ref token.StrData, ref token.StrCap, len);
            }

            token.StrData[token.StrLen++] = (byte)c;
        }

        // C: ufbxi_ascii_push_token_string(uc, token, data, length) (ufbx.c:9620-9632)
        // Note C's `>=` capacity test: an exactly-fitting append still grows the buffer.
        static void PushTokenString(UfbxiAsciiToken token, byte[] data, int offset, int length)
        {
            if (token.StrLen + length >= token.StrCap) {
                int len = token.StrLen + length > 256 ? token.StrLen + length : 256;
                Grow(ref token.StrData, ref token.StrCap, len);
            }

            if (length > 0) Array.Copy(data, offset, token.StrData, token.StrLen, length);
            token.StrLen += length;
        }

        // C: ufbxi_grow_array(&uc->ator_tmp, &token->str_data, &token->str_cap, len)
        // (ufbx.c:3772-3790): new capacity is `max(old_cap * 2, n)` and the old contents
        // are preserved, exactly like `realloc()`.
        static void Grow(ref byte[] pData, ref int pCap, int n)
        {
            if (n <= pCap) return;
            int newCap = pCap * 2;
            if (newCap < n) newCap = n;
            byte[] buf = new byte[newCap];
            if (pData != null) {
                int copy = pCap < pData.Length ? pCap : pData.Length;
                Array.Copy(pData, 0, buf, 0, copy);
            }
            pData = buf;
            pCap = newCap;
        }

        // ------------------------------------------------------------------
        // Skipping / deferred array storage
        // ------------------------------------------------------------------

        // C: int ufbxi_ascii_skip_until(ufbxi_context *uc, char dst) (ufbx.c:9634-9652)
        internal void SkipUntil(char dst)
        {
            byte value = (byte)dst;
            for (;;) {
                int buffered = SrcYield - Src;
                int match = IndexOf(SrcBuffer, Src, buffered, value);
                if (match >= 0) {
                    Src = match;
                    break;
                } else {
                    Src += buffered;
                }
                if (buffered == 0) {
                    char c = Yield();
                    if (c != '\0') continue;
                    // C: ufbxi_check(c != '\0')
                    UfbxiFail.FailNoDesc("c != '\\0'");
                }
            }
        }

        // C: int ufbxi_ascii_store_array(ufbxi_context *uc, ufbxi_buf *tmp_buf)
        // (ufbx.c:9661-9703). Collects the remaining bytes of an array up to and including
        // the closing '}' into `TmpAsciiSpans` for deferred (threaded in C, inline here)
        // parsing. `tmpBuf` owns the copies made for a non-retained window.
        internal void StoreArray(UfbxiAsciiBuf tmpBuf)
        {
            RetainBuf = tmpBuf;

            for (;;) {
                int buffered = SrcYield - Src;
                if (buffered == 0) {
                    char c = Yield();
                    if (c == '\0') {
                        // C: ufbxi_check(c != '\0')
                        UfbxiFail.FailNoDesc("c != '\\0'");
                    }
                    continue;
                }

                int begin = Src;
                int end;
                int match = IndexOf(SrcBuffer, begin, buffered, (byte)'}');
                if (match >= 0) {
                    end = match;
                } else {
                    end = begin + buffered;
                }
                Src = end;

                int length = end - begin;
                // Store the trailing '}' for parsing
                if (match >= 0) length += 1;

                UfbxiAsciiSpan span = new UfbxiAsciiSpan();
                if (SrcIsRetained || InputStream == null) {
                    span.Source = SrcBuffer;
                    span.Offset = begin;
                    span.Length = length;
                } else {
                    int pos = tmpBuf.PushCopy(SrcBuffer, begin, length);
                    span.Source = tmpBuf.Data;
                    span.Offset = pos;
                    span.Length = length;
                }
                TmpAsciiSpans.Add(span);

                if (match >= 0) break;
            }

            RetainBuf = null;
        }

        // C: int ufbxi_ascii_try_ignore_string(ufbxi_context *uc, ufbxi_ascii_token *token)
        // (ufbx.c:9705-9731). Consumes a quoted string without storing it, used for arrays
        // whose content is ignored (`Content:` with `ignore_embedded`).
        internal bool TryIgnoreString()
        {
            char c = SkipWhitespace();
            Token.StrLen = 0;

            if (c == '"') {
                SwapTokenBuffers();

                Token.Type = AsciiString;
                // Skip opening quote
                Next();
                SkipUntil('"');
                // Skip closing quote
                Next();
                return true;
            }

            return false;
        }

        // Replace `prev_token` with `token` but swap the buffers so `token` uses
        // the now-unused string buffer of the old `prev_token`.
        void SwapTokenBuffers()
        {
            byte[] swapData = PrevToken.StrData;
            int swapCap = PrevToken.StrCap;
            PrevToken.CopyFrom(Token);
            Token.StrData = swapData;
            Token.StrCap = swapCap;
        }

        // ------------------------------------------------------------------
        // Token scanner
        // ------------------------------------------------------------------

        // C: int ufbxi_ascii_next_token(ufbxi_context *uc, ufbxi_ascii_token *token)
        // (ufbx.c:9733-9891). Always called with `&ua->token`, so the token parameter of C
        // is simply `Token` here.
        internal void NextToken()
        {
            SwapTokenBuffers();

            char c = SkipWhitespace();
            Token.StrLen = 0;

            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_') {
                Token.Type = AsciiBareWord;
                while ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '(' || c == ')') {
                    PushTokenChar(Token, c);
                    c = Next();
                }

                // Skip whitespace to find if there's a following ':'
                c = SkipWhitespace();
                if (c == ':') {
                    Token.NameLen = Token.StrLen;
                    Token.Type = AsciiName;
                    // String-pool placement hook: the interned name is produced by
                    // `InternName()` (C: `ufbxi_push_string(&uc->string_pool,
                    // prev_token.str_data, prev_token.str_len, NULL, true)` at
                    // ufbx.c:10308), called by `ufbxi_ascii_parse_node()`; the raw bytes stay
                    // owned by the tokenizer scratch buffer so interning copies them.
                    Next();
                }
            } else if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.') {
                Token.Type = AsciiInt;

                Token.Negative = c == '-';
                while ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') {
                    if (c == '.' || c == 'e' || c == 'E') {
                        Token.Type = AsciiFloat;
                    }
                    PushTokenChar(Token, c);
                    c = Next();
                }

                bool nanLike = false;
                while ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '#' || c == '(' || c == ')') {
                    nanLike = true;
                    PushTokenChar(Token, c);
                    c = Next();
                }
                PushTokenChar(Token, '\0');
                if (nanLike) {
                    Token.Type = AsciiFloat;
                }

                if (Token.Type == AsciiInt) {
                    long value = ParseInt64(Token.StrData, 0, out int end);
                    if (end != Token.StrLen - 1) {
                        // C: ufbxi_check(end == token->str_data + token->str_len - 1) -- plain, so
                        // no description is written (ufbx.c:9789).
                        UfbxiFail.FailNoDesc("end == token->str_data + token->str_len - 1");
                    }
                    Token.I64 = value;
                } else if (Token.Type == AsciiFloat) {
                    uint flags = DoubleParseFlags;
                    if (ParseAsF32) flags = UfbxiNumeric.ParseDoubleAsBinary32;
                    double value = UfbxiNumeric.ParseDouble(new ReadOnlySpan<byte>(Token.StrData, 0, Token.StrLen), out int end, flags);
                    if (end != Token.StrLen - 1) {
                        // C: ufbxi_check(...) -- plain (ufbx.c:9794).
                        UfbxiFail.FailNoDesc("end == token->str_data + token->str_len - 1");
                    }
                    Token.F64 = value;
                }
            } else if (c == '"') {
                Token.Type = AsciiString;
                c = Next();
                while (c != '"') {

                    // Optimized string parsing for non-special characters
                    if (Src + 1 < SrcYield) {
                        int begin = Src;
                        int end = SrcYield;
                        int quot = IndexOf(SrcBuffer, begin, end - begin, (byte)'"');
                        if (quot >= 0) end = quot;
                        int esc = IndexOf(SrcBuffer, begin, end - begin, (byte)'&');
                        if (esc >= 0) end = esc;

                        if (begin < end) {
                            PushTokenString(Token, SrcBuffer, begin, end - begin);
                            Src = end;
                            c = Peek();
                            continue;
                        }
                    }

                    // Escape XML-like elements, funny enough there is no way to escape '&'
                    // itself, there is no `&amp`.
                    // '&quot;' -> '"'
                    // '&cr;' -> '\r'
                    // '&lf;' -> '\n'
                    if (c == '&') {
                        string entity;
                        char replacement;

                        c = Next();
                        switch (c) {
                        case 'q':
                            entity = "&quot;";
                            replacement = '"';
                            break;
                        case 'c':
                            entity = "&cr;";
                            replacement = '\r';
                            break;
                        case 'l':
                            entity = "&lf;";
                            replacement = '\n';
                            break;
                        default:
                            // As '&' is not escaped in any way just map '&' -> '&'
                            entity = "&";
                            replacement = '&';
                            break;
                        }

                        int step = 1;

                        // `entity` is a NULL terminated string longer than a single character
                        for (; step < entity.Length; step++) {
                            if (c != entity[step]) break;
                            c = Next();
                        }

                        if (step == entity.Length) {
                            // Full match: Push the replacement character
                            PushTokenChar(Token, replacement);
                        } else {
                            // Partial match: Push the prefix we have skipped already
                            for (int i = 0; i < step; i++) {
                                PushTokenChar(Token, entity[i]);
                            }
                        }
                        continue;
                    }

                    if (c == '\0') {
                        // C: ufbxi_check(c != '\0')
                        UfbxiFail.FailNoDesc("c != '\\0'");
                    }
                    PushTokenChar(Token, c);
                    c = Next();
                }
                // Skip closing quote
                char next = Next();

                // Check if the next character is ':', in some legacy FBX files we have names with
                // spaces, like `"Transport Tool Settings": { ... }`
                if (next == ':') {
                    Token.NameLen = Token.StrLen;
                    Token.Type = AsciiName;
                    // String-pool placement hook: see `InternName()` and the bare-word branch.
                    Next();
                }

            } else {
                // Single character token
                Token.Type = c;
                Next();
            }
        }

        // C: int ufbxi_ascii_accept(ufbxi_context *uc, char type) (ufbx.c:9893-9902)
        internal bool Accept(char type)
        {
            if (Token.Type == type) {
                NextToken();
                return true;
            } else {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // String-pool hooks (wired to Util/StringPool.cs)
        // ------------------------------------------------------------------

        // C: `ufbxi_push_string(&uc->string_pool, ua->prev_token.str_data,
        // ua->prev_token.str_len, NULL, true)` at ufbx.c:10308 — intern a NAME token. The
        // token bytes are raw one-char-per-byte (DOM 约定), so the pool gets the file bytes
        // verbatim (`raw: true` -> no sanitize). The result is the canonical pooled instance,
        // so `ReferenceEquals(name, UfbxiStrings.X)` reproduces C's `name == ufbxi_X`.
        internal string InternName(UfbxiAsciiToken tok)
        {
            string str = UfbxiRawStr.FromBytes(tok.StrData, 0, tok.StrLen);
            int ignored;
            return Ctx.StringPool.PushString(str, 0, tok.StrLen, out ignored, true);
        }

        // C: `ufbxi_push_string_place_str(&uc->string_pool, v, raw)` at ufbx.c:10409 — intern
        // a STRING token used as an 's'/'S'/'C' array value, shortening `length` if the pool
        // sanitizes. `raw` is `arr_type == 's'`.
        internal bool InternStringValue(ref string str, bool raw)
        {
            return Ctx.StringPool.PushStringPlaceStr(ref str, raw);
        }

        // ------------------------------------------------------------------
        // Array element fast paths (inline parsing over the current window)
        // ------------------------------------------------------------------

        // C: int ufbxi_ascii_read_int_array(ufbxi_context *uc, char type, size_t *p_num_read)
        // (ufbx.c:9905-9959). `type` is 'i' or 'l'; values go to `TmpStack` as raw
        // little-endian elements, which is what `UfbxiValueArray.Data` holds.
        internal void ReadIntArray(char type, out int numRead)
        {
            numRead = 0;
            if (ParseAsF32) return;
            int initialItems = TmpStack.NumItems;

            long val;
            if (Token.Type == AsciiInt) {
                val = Token.I64;
            } else {
                return;
            }

            int src = Src;
            int end = SrcYield;
            int srcScan = src;

            for (;;) {

                // Skip '\s*,\s*' between array elements. If we don't find a comma after an
                // element don't push it as we can't be 100% certain whether it's a part of
                // the array.
                while (srcScan != end && IsSpace(At(SrcBuffer, srcScan))) srcScan++;
                if (srcScan == end || At(SrcBuffer, srcScan) != (byte)',') break;
                srcScan++;
                while (srcScan != end && IsSpace(At(SrcBuffer, srcScan))) srcScan++;

                // Found comma, commit to the position and push the previous value to the array
                src = srcScan;
                if (type == 'i') {
                    int pos = TmpStack.PushSize(4, 1);
                    WriteInt32Le(TmpStack.Data, pos, unchecked((int)val));
                } else if (type == 'l') {
                    int pos = TmpStack.PushSize(8, 1);
                    WriteInt64Le(TmpStack.Data, pos, val);
                }

                // Try to parse the next value, we don't commit this until we find a comma
                // after it above.
                int left = end - srcScan;
                if (left < 32) break;

                val = ParseInt64(SrcBuffer, srcScan, out int next);
                if (next < 0) break;
                srcScan = srcScan + next;
            }

            // Resume conventional parsing if we moved `src`.
            if (src != Src) {
                Src = src;
                NextToken();
            }

            numRead = TmpStack.NumItems - initialItems;
        }

        // C: int ufbxi_ascii_read_float_array(ufbxi_context *uc, char type, size_t *p_num_read)
        // (ufbx.c:10155-10218). `type` is 'd' or 'f'.
        internal void ReadFloatArray(char type, out int numRead)
        {
            numRead = 0;
            if (ParseAsF32) return;

            double val;
            if (Token.Type == AsciiFloat) {
                val = Token.F64;
            } else if (Token.Type == AsciiInt) {
                double fsign = Token.I64 == 0 && Token.Negative ? -1.0 : 1.0;
                val = (double)Token.I64 * fsign;
            } else {
                return;
            }

            int src = Src;
            int end = SrcYield;

            uint parseFlags = DoubleParseFlags;

            int initialItems = TmpStack.NumItems;
            int srcScan = src;
            for (;;) {

                // Skip '\s*,\s*' between array elements. If we don't find a comma after an
                // element don't push it as we can't be 100% certain whether it's a part of
                // the array.
                while (srcScan != end && IsSpace(At(SrcBuffer, srcScan))) srcScan++;
                if (srcScan == end || At(SrcBuffer, srcScan) != (byte)',') break;
                srcScan++;
                while (srcScan != end && IsSpace(At(SrcBuffer, srcScan))) srcScan++;

                // Found comma, commit to the position and push the previous value to the array
                src = srcScan;
                if (type == 'd') {
                    int pos = TmpStack.PushSize(8, 1);
                    WriteDoubleLe(TmpStack.Data, pos, val);
                } else if (type == 'f') {
                    int pos = TmpStack.PushSize(4, 1);
                    WriteFloatLe(TmpStack.Data, pos, (float)val);
                }

                // Try to parse the next value, we don't commit this until we find a comma
                // after it above.
                int left = end - srcScan;
                int sliceStart = srcScan < SrcBuffer.Length ? srcScan : SrcBuffer.Length;
                int sliceLen = left > 0 ? left : 0;
                double value = UfbxiNumeric.ParseDouble(new ReadOnlySpan<byte>(SrcBuffer, sliceStart, sliceLen), out int numEnd, parseFlags);
                if (numEnd < 0) numEnd = 0;
                int numEndAbs = srcScan + numEnd;
                if (numEndAbs == srcScan || numEndAbs >= end) {
                    break;
                }

                val = value;
                srcScan = numEndAbs;
            }

            // Resume conventional parsing if we moved `src`.
            if (src != Src) {
                Src = src;
                NextToken();
            }

            numRead = TmpStack.NumItems - initialItems;
        }

        // ------------------------------------------------------------------
        // Deferred (in C: threaded) array parsing
        // ------------------------------------------------------------------

        // C: const char *ufbxi_ascii_array_task_parse_floats(t, src, src_end, parse_flags)
        // (ufbx.c:9970-10003). Returns the next parse offset into `srcBuf`, or -1 for
        // C's NULL.
        static int ArrayTaskParseFloats(UfbxiAsciiArrayTask t, byte[] srcBuf, int src, int srcEnd, uint parseFlags)
        {
            int offset = t.Offset;
            bool isFloat = t.ArrType == 'f';
            bool isDouble = t.ArrType == 'd';
            // Port-only guard: C asserts `dst_float || dst_double` (ufbx.c:9975, a no-op/abort
            // assert, not an error site). Unreachable with no thread pool (no deferred arrays).
            UfbxiFail.CheckNoDesc(isFloat || isDouble, "dst_float || dst_double");
            int dst = t.ArrDataOffset + offset * (isFloat ? 4 : 8);
            int srcBegin = src;

            while (src != srcEnd) {
                // C dereferences without a range check here; see `At()`.
                while (IsSpace(At(srcBuf, src))) src++;

                // Try to parse the next value, we don't commit this until we find a comma
                // after it above.
                int sliceStart = src >= 0 && src < srcBuf.Length ? src : (srcBuf.Length > 0 ? srcBuf.Length : 0);
                int sliceLen = srcEnd - src > 0 ? srcEnd - src : 0;
                if (sliceStart + sliceLen > srcBuf.Length) sliceLen = srcBuf.Length - sliceStart;
                int numEnd;
                double val = UfbxiNumeric.ParseDouble(new ReadOnlySpan<byte>(srcBuf, sliceStart, sliceLen), out numEnd, parseFlags);
                // C: if (!num_end) return src_begin; (`ufbxi_parse_double` always sets it)
                if (numEnd < 0) return srcBegin;
                src = sliceStart + numEnd;

                while (IsSpace(At(srcBuf, src))) src++;
                if (At(srcBuf, src) != (byte)',') break;
                src++;
                srcBegin = src;

                if ((uint)offset >= (uint)t.ArrSize) return -1;
                if (isDouble) {
                    WriteDoubleLe(t.ArrData, dst, val);
                    dst += 8;
                } else {
                    WriteFloatLe(t.ArrData, dst, (float)val);
                    dst += 4;
                }
                offset++;
            }

            t.Offset = offset;
            return srcBegin;
        }

        // C: const char *ufbxi_ascii_array_task_parse_ints(t, src, src_end)
        // (ufbx.c:10005-10035)
        static int ArrayTaskParseInts(UfbxiAsciiArrayTask t, byte[] srcBuf, int src, int srcEnd)
        {
            int offset = t.Offset;
            bool is32 = t.ArrType == 'i';
            bool is64 = t.ArrType == 'l';
            // Port-only guard: C asserts `dst32 || dst64` (ufbx.c:10010, a no-op/abort assert,
            // not an error site). Unreachable with no thread pool (no deferred arrays).
            UfbxiFail.CheckNoDesc(is32 || is64, "dst32 || dst64");
            int dst = t.ArrDataOffset + offset * (is32 ? 4 : 8);
            int srcBegin = src;

            while (src != srcEnd) {
                while (IsSpace(At(srcBuf, src))) src++;

                long val = ParseInt64(srcBuf, src, out int next);
                if (next < 0) return -1;
                src = src + next;

                while (IsSpace(At(srcBuf, src))) src++;
                if (At(srcBuf, src) != (byte)',') break;
                src++;
                srcBegin = src;

                if ((uint)offset >= (uint)t.ArrSize) return -1;
                if (is32) {
                    WriteInt32Le(t.ArrData, dst, unchecked((int)val));
                    dst += 4;
                } else {
                    WriteInt64Le(t.ArrData, dst, val);
                    dst += 8;
                }
                offset++;
            }

            t.Offset = offset;
            return srcBegin;
        }

        // C: const char *ufbxi_ascii_array_task_parse(t, src, src_end) (ufbx.c:10037-10044)
        static int ArrayTaskParse(UfbxiAsciiArrayTask t, byte[] srcBuf, int src, int srcEnd)
        {
            if (t.ArrType == 'f' || t.ArrType == 'd') {
                uint flags = ParseDoubleInitFlags;
                return ArrayTaskParseFloats(t, srcBuf, src, srcEnd, flags);
            } else {
                return ArrayTaskParseInts(t, srcBuf, src, srcEnd);
            }
        }

        // C: bool ufbxi_ascii_array_task_imp(ufbxi_ascii_array_task *t)
        // (ufbx.c:10054-10143). Ported literally, including the whitespace/comment state
        // machine that may span several `Spans` and the 128-byte value staging buffer.
        internal static bool ArrayTaskImp(UfbxiAsciiArrayTask t)
        {
            // Temporary buffer for parsing between spans
            byte[] buffer = new byte[128]; // ufbxi_uninit (C# zero-fills; see notes below)
            int bufferLen = 0;
            bool bufferValue = false;

            UfbxiAsciiScanState state = UfbxiAsciiScanState.Whitespace;
            for (int spanIx = 0; spanIx < t.NumSpans; spanIx++) {
                UfbxiAsciiSpan span = t.Spans[spanIx];
                byte[] srcBuf = span.Source;
                int src = span.Offset;
                int end = src + span.Length;

                while (src != end) {

                    // State machine for skipping whitespace and comments, potentially
                    // between multiple spans.
                    while (src != end) {
                        char c = (char)At(srcBuf, src);
                        if (state == UfbxiAsciiScanState.Value) {
                            if (bufferLen >= buffer.Length - 1) return false;
                            if (c == '"') {
                                return false;
                            } else if (c == ';' || IsSpace(c)) {
                                state = UfbxiAsciiScanState.Whitespace;
                                buffer[bufferLen] = (byte)' ';
                                bufferLen++;
                            } else if (c == ',' || c == '}') {
                                state = UfbxiAsciiScanState.Comma;
                                buffer[bufferLen] = (byte)',';
                                bufferLen++;
                                src++;
                                break;
                            } else {
                                bufferValue = true;
                                buffer[bufferLen] = (byte)c;
                                bufferLen++;
                                src++;
                            }
                        } else if (state == UfbxiAsciiScanState.Whitespace) {
                            if (c == ';') {
                                state = UfbxiAsciiScanState.Comment;
                            } else if (IsSpace(c)) {
                                src++;
                            } else {
                                state = UfbxiAsciiScanState.Value;
                            }
                        } else if (state == UfbxiAsciiScanState.Comment) {
                            if (c == '\n') {
                                state = UfbxiAsciiScanState.Whitespace;
                            } else {
                                src++;
                            }
                        } else if (state == UfbxiAsciiScanState.Comma) {
                            state = UfbxiAsciiScanState.Whitespace;
                        }
                    }

                    if (state == UfbxiAsciiScanState.Comma) {
                        // Parse a value from the buffer
                        if (bufferValue) {
                            int bufferEnd = ArrayTaskParse(t, buffer, 0, bufferLen);
                            if (bufferEnd < 0 || bufferEnd == 0) {
                                return false;
                            }
                        }

                        // If not at end, we are past the last comma, so try to find a
                        // safe range to parse.
                        if (src != end) {
                            int parseEnd = end;
                            while (parseEnd > src) {
                                if (srcBuf[parseEnd - 1] == (byte)',') break;
                                parseEnd--;
                            }
                            if (src < parseEnd) {
                                src = ArrayTaskParse(t, srcBuf, src, parseEnd);
                                if (src < 0) return false;
                            }
                        }

                        bufferLen = 0;
                        bufferValue = false;
                    }
                }
            }

            if (t.Offset != t.ArrSize) return false;

            return true;
        }

        // C: bool ufbxi_ascii_array_task_fn(ufbxi_task *task) (ufbx.c:10145-10153).
        // PORTING_NOTES.md #7: with `pool == NULL` C never creates a task and instead runs
        // `ufbxi_check_msg(ufbxi_ascii_array_task_imp(&t), "Threaded ASCII parse error")`
        // inline (ufbx.c:10653-10656), so this only keeps the error text reachable.
        internal static bool ArrayTaskFn(UfbxiAsciiArrayTask task, out string error)
        {
            if (!ArrayTaskImp(task)) {
                error = ThreadedAsciiParseError;
                return false;
            }
            error = null;
            return true;
        }
    }
}
