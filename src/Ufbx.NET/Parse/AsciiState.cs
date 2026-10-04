// State of the ASCII FBX reader, ported from ufbx.c v0.23.1 (ufbx.c:6232-6277 plus the
// supporting buffer types). The algorithms live in Parse/Ascii.cs (`UfbxiAscii` is a
// partial class split across the two files).
//
// C pointer -> C# index mapping (PORTING_NOTES.md 类型映射 / 行为保真):
//  * C keeps the ASCII window as raw pointers `ua->src`, `ua->src_yield`, `ua->src_end`
//    into whatever buffer currently owns the read data (`uc->read_buffer`, a `ufbxi_buf`
//    chunk when a long token has to be retained, or the static `""` buffer once EOF is
//    reached without a `read_fn()`). Here that is the triple (`SrcBuffer`, `Src`,
//    `SrcEnd`) with `SrcYield` as the progress-yield boundary inside the same array.
//    `SrcBuffer` is *always* the array that `UfbxiStream.Buffer` refers to, so
//    `ua->src - uc->data_begin` == `Src - Stream.BeginIndex` exactly, which is what
//    `Refill` needs for the `uc->data_offset` accounting.
//  * C's `ufbxi_buf` is a chunked arena buffer; only the operations the ASCII reader
//    performs are reproduced (`UfbxiAsciiBuf`), per PORTING_NOTES.md #4.
//  * `UfbxiAsciiTmpStack` stands in for `uc->tmp_stack` as used by the ASCII array
//    readers: raw little-endian element bytes plus C's `num_items` item counter, which is
//    what `ufbxi_ascii_read_int_array()`/`ufbxi_ascii_read_float_array()` report through
//    `*p_num_read`. Same byte representation as `UfbxiValueArray.Data` (DOM/数组约定).

namespace Ufbx.NET
{
    // C: ufbxi_ascii_scan_state (ufbx.c:10046-10052)
    internal enum UfbxiAsciiScanState
    {
        Value,       // C: UFBXI_ASCII_SCAN_STATE_VALUE
        Whitespace,  // C: UFBXI_ASCII_SCAN_STATE_WHITESPACE
        Comment,     // C: UFBXI_ASCII_SCAN_STATE_COMMENT
        Comma,       // C: UFBXI_ASCII_SCAN_STATE_COMMA
    }

    // C: typedef struct ufbxi_ascii_token (ufbx.c:6232-6258)
    // `char *str_data` + `size_t str_len/str_cap` become a byte array plus length; the DOM
    // stores strings one char per byte (PORTING_NOTES.md DOM 约定), so the bytes are the
    // token text verbatim and can be handed to the string pool without conversion.
    internal sealed class UfbxiAsciiToken
    {
        // C: char *str_data / size_t str_cap — the string data buffer, grown on demand.
        public byte[] StrData;
        public int StrCap;

        // C: size_t str_len — length of the token text, NOT counting the '\0' that
        // `ufbxi_ascii_next_token()` pushes for numeric tokens.
        public int StrLen;

        // C: char type — either a single character token ('{', ':', ...) or one of
        // UfbxiAscii.Ascii* below.
        public char Type;

        // C: bool negative — sign of an integer token (used to reproduce `-0`).
        public bool Negative;

        // C: union { double f64; int64_t i64; size_t name_len; } value. ufbx always reads
        // exactly the member matching `Type`, so plain fields are equivalent.
        public double F64;      // C: value.f64   (UFBXI_ASCII_FLOAT)
        public long I64;        // C: value.i64   (UFBXI_ASCII_INT)
        public int NameLen;     // C: value.name_len (UFBXI_ASCII_NAME)

        // C: the struct assignment `ua->prev_token = ua->token`, which copies every
        // member including the `str_data` pointer (the caller then re-points `token` at
        // the buffer the previous `prev_token` owned, so the two tokens alternate).
        internal void CopyFrom(UfbxiAsciiToken other)
        {
            StrData = other.StrData;
            StrCap = other.StrCap;
            StrLen = other.StrLen;
            Type = other.Type;
            Negative = other.Negative;
            F64 = other.F64;
            I64 = other.I64;
            NameLen = other.NameLen;
        }

        // C: the `memset(&uc->ascii, 0, sizeof(uc->ascii))` at ufbx.c:11218 zeroes both token
        // structs, dropping their heap `str_data` buffers; the next `next_token` re-grows them
        // from scratch (the first `push_token_char` sees `str_len == str_cap == 0`).
        internal void Reset()
        {
            StrData = null;
            StrCap = 0;
            StrLen = 0;
            Type = '\0';
            Negative = false;
            F64 = 0.0;
            I64 = 0L;
            NameLen = 0;
        }
    }

    // C: typedef struct { const char *source; size_t length; } ufbxi_ascii_span
    // (ufbx.c:9656-9659). `source` is a byte range inside `Source`, valid because either
    // the source buffer is retained (`src_is_retained`) or the whole input is in memory.
    internal struct UfbxiAsciiSpan
    {
        public byte[] Source;
        public int Offset;
        public int Length;
    }

    // C: typedef struct { ... } ufbxi_ascii_array_task (ufbx.c:9960-9968)
    internal sealed class UfbxiAsciiArrayTask
    {
        // C: void *arr_data — destination element bytes; `ArrDataOffset` is C's pointer
        // expressed as an index into `ArrData` (the caller pre-advances it by the number
        // of eagerly parsed values, ufbx.c:10640).
        public byte[] ArrData;
        public int ArrDataOffset;

        // C: char arr_type ('i','l','f','d')
        public char ArrType;

        // C: size_t arr_size — number of elements left to fill (`deferred_size`).
        public int ArrSize;

        // C: const ufbxi_ascii_span *spans / size_t num_spans
        public UfbxiAsciiSpan[] Spans;
        public int NumSpans;

        // C: size_t offset — elements written so far, in element units.
        public int Offset;
    }

    // Minimal `ufbxi_buf` as used by the ASCII reader: the caller's `tmp_buf` handed to
    // `ufbxi_ascii_store_array()` (span copies + retained refill chunks) and, through
    // `UfbxiAsciiBuf`, the `ua->retain_buf`/`ua->src_buf` bookkeeping.
    internal sealed class UfbxiAsciiBuf
    {
        public byte[] Data;
        public int Pos;

        // C: ufbxi_push(buf, char, n) — append `n` bytes and return their start offset.
        // Growth mirrors `ufbxi_grow_array_size()`'s `max(old*2, n)` policy; unlike C's
        // chunk list a grow copies into a fresh array, which is invisible to callers
        // because spans keep their own array reference.
        internal int Push(int n)
        {
            if (n <= 0) return Pos;
            int needed = Pos + n;
            if (Data == null || needed > Data.Length) {
                int newCap = Data != null ? Data.Length * 2 : 0;
                if (newCap < needed) newCap = needed;
                byte[] buf = new byte[newCap];
                if (Data != null) System.Array.Copy(Data, 0, buf, 0, Pos);
                Data = buf;
            }
            int pos = Pos;
            Pos = needed;
            return pos;
        }

        // C: ufbxi_push_copy(buf, char, length, src)
        internal int PushCopy(byte[] src, int srcOffset, int length)
        {
            int pos = Push(length);
            if (length > 0) System.Array.Copy(src, srcOffset, Data, pos, length);
            return pos;
        }
    }

    // Minimal `uc->tmp_stack` for the ASCII array fast paths: raw little-endian element
    // bytes plus C's `num_items` counter (independent of element size, exactly like
    // `ufbxi_buf::num_items`, which is what `*p_num_read` is derived from).
    internal sealed class UfbxiAsciiTmpStack
    {
        public byte[] Data;
        public int Pos;
        public int NumItems;

        // C: ufbxi_push_fast(&uc->tmp_stack, size, count) — returns the byte offset.
        internal int PushSize(int size, int count)
        {
            if (count <= 0) return Pos;
            int total = size * count;
            int needed = Pos + total;
            if (Data == null || needed > Data.Length) {
                int newCap = Data != null ? Data.Length * 2 : 0;
                if (newCap < needed) newCap = needed;
                byte[] buf = new byte[newCap];
                if (Data != null) System.Array.Copy(Data, 0, buf, 0, Pos);
                Data = buf;
            }
            int pos = Pos;
            Pos = needed;
            NumItems += count;
            return pos;
        }

        // C: ufbxi_push_size_zero(&uc->tmp_stack, size, count) — used by the caller for
        // alignment padding / PAD_BEGIN.
        internal int PushSizeZero(int size, int count)
        {
            int pos = PushSize(size, count);
            System.Array.Clear(Data, pos, size * count);
            return pos;
        }

        // C: ufbxi_pop_size(&uc->tmp_stack, size, count, dst, peek == false) — take
        // `count` items off the top, optionally copying them to `dst`.
        internal void PopSize(int size, int count, byte[] dst, int dstOffset)
        {
            int total = size * count;
            if (total > 0) {
                if (dst != null) System.Array.Copy(Data, Pos - total, dst, dstOffset, total);
                Pos -= total;
            }
            NumItems -= count;
        }
    }

    // C: typedef struct ufbxi_ascii (ufbx.c:6259-6277) + the `uc->tmp_ascii_spans` buffer
    // (ufbx.c:6533) that only the ASCII reader touches.
    internal sealed partial class UfbxiAscii
    {
        // C: #define UFBXI_ASCII_END/NAME/BARE_WORD/INT/FLOAT/STRING (ufbx.c:9400-9407)
        internal const char AsciiEnd = '\0';       // C: UFBXI_ASCII_END
        internal const char AsciiName = 'N';       // C: UFBXI_ASCII_NAME
        internal const char AsciiBareWord = 'B';   // C: UFBXI_ASCII_BARE_WORD
        internal const char AsciiInt = 'I';        // C: UFBXI_ASCII_INT
        internal const char AsciiFloat = 'F';      // C: UFBXI_ASCII_FLOAT
        internal const char AsciiString = 'S';     // C: UFBXI_ASCII_STRING

        // C: size_t max_token_length — declared in the struct; ufbx.c never reads or writes it
        // (`memset(&uc->ascii, 0, ...)` at ufbx.c:11218 is the only assignment), so `ResetFromStream`
        // zeroes it for layout fidelity.
        internal int MaxTokenLength;

        // C: const char *src / *src_yield / *src_end.
        internal byte[] SrcBuffer;
        internal int Src;
        internal int SrcYield;
        internal int SrcEnd;

        // C: bool read_first_comment / found_version / parse_as_f32 / src_is_retained.
        internal bool ReadFirstComment;
        internal bool FoundVersion;
        internal bool ParseAsF32;
        internal bool SrcIsRetained;

        // C: ufbxi_buf *retain_buf / *src_buf.
        internal UfbxiAsciiBuf RetainBuf;
        internal UfbxiAsciiBuf SrcBuf;

        // C: ufbxi_ascii_token prev_token / token.
        internal readonly UfbxiAsciiToken PrevToken = new UfbxiAsciiToken();
        internal readonly UfbxiAsciiToken Token = new UfbxiAsciiToken();

        // C: ufbxi_buf tmp_ascii_spans (ufbx.c:6533) — deferred array chunks collected by
        // `StoreArray()` and consumed by the caller as `UfbxiAsciiArrayTask.Spans`.
        internal readonly System.Collections.Generic.List<UfbxiAsciiSpan> TmpAsciiSpans
            = new System.Collections.Generic.List<UfbxiAsciiSpan>();

        // C: uc->tmp_stack, as used by the ASCII array fast paths. It is the shared parse
        // stack owned by `ufbxi_context` (ufbx.c:6525): the DOM node/prop readers pop what the
        // ASCII array readers push, so both layers must use the same stack instance.
        internal UfbxiAsciiTmpStack TmpStack => Ctx.TmpStack;

        // -- Wiring to the rest of the parser (C: fields of `uc` read/written by these
        // functions). These all forward to the genuine `UfbxiStream`/`UfbxiContext` state so
        // the ASCII reader runs on exactly the IO window and options the loader drives, not on
        // a test-local copy. Set at ASCII setup, cf. ufbx.c:11216-11222 and ufbxi_load().

        // C: `uc` itself; `uc->version` and `uc->exporter` are written here (ufbx.c:9568, 9595).
        internal UfbxiContext Ctx;

        // C: the shared IO window; `Buffer/BeginIndex/Position/DataOffset` are kept in
        // sync with `src` so `ufbxi_get_read_offset()` and progress reporting work.
        internal UfbxiStream Stream;

        // C: uc->read_fn / uc->read_user (ufbx.c:6411-6412). In this port the read function is
        // the stream's `UfbxiStream.Input`; `Input == null` is `ufbx_load_memory` — the whole
        // input is already in the buffer and C's ASCII refill treats everything past it as EOF
        // (ufbx.c:9445-9449). Returns bytes read, negative on IO error (C: SIZE_MAX).
        internal UfbxInputStream InputStream => Stream.Input;

        // C: uc->opts.read_buffer_size (ufbx.c:9417, 9424) — owned by the real stream.
        internal int OptReadBufferSize => Stream.OptReadBufferSize;

        // C: uc->progress_interval (ufbx.c:9464-9467, set at ufbx.c:25531-25536) — the same
        // field the binary reader uses, so it lives on the stream, not on the ASCII state.
        internal ulong ProgressInterval => Stream.ProgressInterval;

        // C: uc->double_parse_flags (ufbx.c:6472, set at 25490) — owned by the context.
        internal uint DoubleParseFlags => Ctx.DoubleParseFlags;

        // C: `uc->exporter = UFBX_EXPORTER_BLENDER_ASCII` (ufbx.c:9595). Kept as a flag so the
        // tokenizer can report the detection, and mirrored onto `uc->exporter` by
        // `SkipWhitespace()` now that the context carries the field.
        internal bool FoundBlenderAscii;

        // C: `memset(&uc->ascii, 0, sizeof(uc->ascii))` (ufbx.c:11218) before the window
        // fields are seeded from the current buffer, then the `uc->data`/`yield_size`/
        // `data_size` seeding of ufbx.c:11219-11221.
        internal void ResetFromStream(UfbxiContext uc, UfbxiStream stream)
        {
            Ctx = uc;
            Stream = stream;
            ReadFirstComment = false;
            FoundVersion = false;
            ParseAsF32 = false;
            SrcIsRetained = false;
            RetainBuf = null;
            SrcBuf = null;
            MaxTokenLength = 0;
            FoundBlenderAscii = false;
            PrevToken.Reset();
            Token.Reset();
            TmpAsciiSpans.Clear();
            SrcBuffer = stream.Buffer;
            Src = stream.Position;                                   // C: uc->data
            SrcEnd = stream.Position + stream.Remaining + stream.YieldSize; // C: data_size + yield_size
            // C: ua->src_yield = uc->data + uc->yield_size
            SrcYield = stream.Position + stream.YieldSize;
        }
    }
}
