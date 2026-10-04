// Compressed-data chunk/bit reader, ported from ufbx.c v0.23.1 (ufbx.c:1890-2250).
//
// C: `ufbxi_bit_stream` is the chunked reader used by the inflate/bit-stream code; it is
// *not* the same object as `ufbxi_stream` (the buffered file reader, ported in Stream.cs).
// It owns its own chunking: an initial data chunk (usually the whole compressed block) that
// is read from directly, plus a scratch `buffer` refilled through the user `read_fn()`.
//
// Pointer model (bit-exactness notes):
//   * C keeps five pointers into the current chunk (`chunk_begin` ... `chunk_real_end`).
//     All of them always address the *same* array, so the port stores one array reference
//     (`ChunkArray`) plus five `int` offsets into it. `ufbxi_bit_chunk_refill()` is the only
//     place that swaps the array, and it always resets every pointer to the array start, so
//     a caller's local `data` index stays meaningful as long as it is assigned the value the
//     refill returns (C: the returned `char*`, which is index 0 of the new array).
//   * C reads 8 bytes at `data` unconditionally (`ufbxi_read_u64`, ufbx.c:794/812) and relies
//     on `chunk_end == real_end - 8` plus the 64/128 byte zero padding in
//     `ufbxi_bit_chunk_refill()` (ufbx.c:2076-2085) to keep those loads inside the buffer.
//     The port mirrors the same invariants, so the loads are in bounds by construction.
//   * `ufbxi_wrap_shr64(a, b)` is `(a) >> (b)` on x64 (ufbx.c:909, the golden build's
//     configuration), i.e. the shift count is taken modulo 64 by the CPU; the port masks
//     explicitly, which reproduces it including the `extra_shift_base` trick that hides the
//     real shift in the low 6 bits of a value that also carries a base in bits [16:32].
//
// Configuration: the golden C build is a plain (non-UFBX_REGRESSION / non-UFBX_UBSAN) x64
// build, therefore
//   * `ufbx_assert`, `ufbxi_dev_assert`, `ufbxi_regression_assert` and
//     `ufbxi_static_assert` are no-ops and are not ported (ufbx.h:104, ufbx.c:1022-1032),
//   * `ufbxi_fast_uint` is `size_t` = 64-bit (ufbx.c:900-904),
//   * there is no `UFBXI_SMALL_MEMORY` switch in v0.23.1: the constant near ufbx.c:54 is
//     `UFBXI_MAX_SKIP_SIZE 0x40000000`, which `#if defined(UFBX_REGRESSION)` at
//     ufbx.c:1000-1002 overrides to 128. The non-regression value 0x40000000 is what the
//     golden binaries used (see Stream.cs), and nothing in this file depends on it.

namespace Ufbx
{
    internal sealed class UfbxiBitStream
    {
        // C: char local_buffer[256] (ufbx.c:1930)
        const int LocalBufferSize = 256;

        // C: `s->progress_interval = 0x4000` (ufbx.c:2126)
        const ulong DefaultProgressInterval = 0x4000;

        // C: SIZE_MAX on the 64-bit build the golden hashes were produced with.
        const ulong SizeMax = ulong.MaxValue;

        // C: size_t input_left (ufbx.c:1893) — bytes left to read from `read_fn()`
        public int InputLeft;

        // C: ufbx_read_fn *read_fn / void *read_user (ufbx.c:1896-1897)
        public UfbxReadFn ReadFn;
        public object ReadUser;

        // C: char *buffer / size_t buffer_size (ufbx.c:1901-1902) — may be `local_buffer`
        public byte[] Buffer;
        public int BufferSize;

        // C: const char *chunk_begin / *chunk_ptr / *chunk_yield / *chunk_end /
        //    *chunk_real_end (ufbx.c:1906-1910) — offsets into `ChunkArray`.
        public byte[] ChunkArray;
        public int ChunkBegin;
        public int ChunkPtr;
        public int ChunkYield;
        public int ChunkEnd;
        public int ChunkRealEnd;

        // C: size_t num_read_before_chunk (ufbx.c:1913)
        public int NumReadBeforeChunk;

        // C: uint64_t progress_bias / progress_total / size_t progress_interval
        //    (ufbx.c:1914-1916)
        public ulong ProgressBias;
        public ulong ProgressTotal;
        public ulong ProgressInterval;

        // C: uint64_t bits / size_t left (ufbx.c:1918-1919)
        public ulong Bits;
        public int Left;

        // C: ufbx_progress_cb progress_cb (ufbx.c:1922)
        public UfbxProgressCb ProgressCb;

        // C: bool seen_end (ufbx.c:1925)
        public bool SeenEnd;

        // C: int8_t stop_error (ufbx.c:1928) — -28 cancelled, -31 read past EOF
        public int StopError;

        // C: char local_buffer[256] (ufbx.c:1930)
        byte[] m_localBuffer;

        // Scratch used because the C# `ufbx_read_fn` delegate (RuntimeOpts.cs:73) has no
        // destination offset, while C reads to `s->buffer + left` (ufbx.c:2067).
        byte[] m_readScratch;

        // C: ufbxi_read_u64() (ufbx.c:794 / portable form ufbx.c:812-823), little-endian.
        public ulong ReadU64(int index)
        {
            byte[] a = ChunkArray;
            return (ulong)a[index]
                | ((ulong)a[index + 1] << 8)
                | ((ulong)a[index + 2] << 16)
                | ((ulong)a[index + 3] << 24)
                | ((ulong)a[index + 4] << 32)
                | ((ulong)a[index + 5] << 40)
                | ((ulong)a[index + 6] << 48)
                | ((ulong)a[index + 7] << 56);
        }

        // C: #define ufbxi_wrap_shr64(a, b) ((a) >> (b)) on x64 (ufbx.c:909).
        // The CPU masks the shift count to 6 bits, which the callers deliberately exploit.
        public static ulong WrapShr64(ulong a, uint b)
        {
            return a >> (int)(b & 63);
        }

        // C: ufbxi_bit_reverse() (ufbx.c:2040-2050)
        public static uint BitReverse(uint mask, uint numBits)
        {
            uint x = mask;
            x = ((x & 0xaaaa) >> 1) | ((x & 0x5555) << 1);
            x = ((x & 0xcccc) >> 2) | ((x & 0x3333) << 2);
            x = ((x & 0xf0f0) >> 4) | ((x & 0x0f0f) << 4);
            x = ((x & 0xff00) >> 8) | ((x & 0x00ff) << 8);
            return x >> (int)(16 - numBits);
        }

        // C: ufbxi_bit_chunk_refill() (ufbx.c:2052-2092)
        // Returns the new chunk pointer (C: `s->buffer`, i.e. index 0 of the new array).
        public int BitChunkRefill(int ptr)
        {
            // Copy any left-over data to the beginning of `buffer`
            int left = ChunkRealEnd - ptr;
            if (left > 0) System.Array.Copy(ChunkArray, ptr, Buffer, 0, left);

            NumReadBeforeChunk += ptr - ChunkBegin;

            // Read more user data if the user supplied a `read_fn()`, otherwise
            // we assume the initial data chunk is the whole input buffer.
            if (ReadFn != null && StopError == 0)
            {
                int toRead = InputLeft < BufferSize - left ? InputLeft : BufferSize - left;
                if (toRead > 0)
                {
                    if (m_readScratch == null || m_readScratch.Length < toRead)
                    {
                        m_readScratch = new byte[toRead];
                    }
                    int numRead = ReadFn(ReadUser, m_readScratch, toRead);
                    // TODO: IO error, should unify with (currently broken) cancel logic
                    // C compares as `size_t`, so a negative (C: SIZE_MAX) error return also
                    // fails this test.
                    if (numRead < 0 || numRead > toRead) numRead = 0;
                    InputLeft -= numRead;
                    System.Array.Copy(m_readScratch, 0, Buffer, left, numRead);
                    left += numRead;
                }
            }

            // Pad the rest with zeros, leaving at least 64 zeros of slack.
            // If we end up here again after padding once, we're reading past EOF.
            if (left < 64)
            {
                if (SeenEnd)
                {
                    StopError = -31;
                }
                SeenEnd = true;
                System.Array.Clear(Buffer, left, 128 - left);
                left = 128;
            }

            ChunkArray = Buffer;
            ChunkBegin = 0;
            ChunkPtr = 0;
            ChunkEnd = left - 8;
            ChunkRealEnd = left;
            return 0;
        }

        // C: ufbxi_bit_stream_init() (ufbx.c:2094-2146)
        public void BitStreamInit(UfbxInflateInput input)
        {
            int dataSize = input.DataSize;
            if (dataSize > input.TotalSize)
            {
                dataSize = input.TotalSize;
            }

            ReadFn = input.ReadFn;
            ReadUser = input.ReadUser;
            ProgressCb = input.ProgressCb;
            ChunkArray = input.Data;
            ChunkBegin = 0;
            ChunkPtr = 0;
            // C: ufbxi_add_ptr(data, ufbxi_max_sz(8, data_size) - 8)
            ChunkEnd = (dataSize > 8 ? dataSize : 8) - 8;
            ChunkRealEnd = dataSize;
            InputLeft = input.TotalSize - dataSize;

            // Use the user buffer if it's large enough, otherwise `local_buffer`
            if (input.Buffer != null && input.BufferSize > LocalBufferSize)
            {
                Buffer = input.Buffer;
                BufferSize = input.BufferSize;
            }
            else
            {
                if (m_localBuffer == null) m_localBuffer = new byte[LocalBufferSize];
                Buffer = m_localBuffer;
                BufferSize = LocalBufferSize;
            }
            NumReadBeforeChunk = 0;
            ProgressBias = input.ProgressSizeBefore;
            ProgressTotal = (ulong)input.TotalSize + input.ProgressSizeBefore + input.ProgressSizeAfter;
            if (ProgressCb == null || ProgressCb.Fn == null || input.ProgressIntervalHint >= SizeMax)
            {
                ProgressInterval = SizeMax;
            }
            else if (input.ProgressIntervalHint > 0)
            {
                ProgressInterval = input.ProgressIntervalHint;
            }
            else
            {
                ProgressInterval = DefaultProgressInterval;
            }
            SeenEnd = false;
            StopError = 0;

            // Clear the initial bit buffer
            Bits = 0;
            Left = 0;

            // If the initial data buffer is not large enough to be read directly
            // from refill the chunk once.
            if (dataSize < 64)
            {
                BitChunkRefill(ChunkBegin);
            }

            UpdateChunkYield(ChunkPtr);
        }

        // C: the `chunk_yield` selection duplicated at ufbx.c:2141-2145 and 2174-2178.
        // NOTE (C): with `progress_interval == SIZE_MAX` the C expression
        // `chunk_ptr + progress_interval` is wild pointer arithmetic; it is unreachable for
        // the default build because SIZE_MAX is only selected when there is no progress
        // callback (ufbx.c:2121-2122), so the `else` branch below is always taken.
        void UpdateChunkYield(int ptr)
        {
            if (ProgressCb != null && ProgressCb.Fn != null
                && (ulong)(ChunkEnd - ptr) > ProgressInterval + 8)
            {
                ChunkYield = ptr + (int)ProgressInterval;
            }
            else
            {
                ChunkYield = ChunkEnd;
            }
        }

        // C: ufbxi_bit_yield() (ufbx.c:2148-2181)
        public int BitYield(int ptr)
        {
            if (ptr > ChunkEnd)
            {
                ptr = BitChunkRefill(ptr);
            }

            if (ProgressCb != null && ProgressCb.Fn != null)
            {
                int numRead = NumReadBeforeChunk + (ptr - ChunkBegin);

                UfbxProgress progress = new UfbxProgress();
                progress.BytesRead = ProgressBias + (ulong)numRead;
                progress.BytesTotal = ProgressTotal;
                UfbxProgressResult result = ProgressCb.Fn(ProgressCb.User, progress);
                if (result == UfbxProgressResult.Cancel)
                {
                    // C: `ufbx_assert(result == CONTINUE || result == CANCEL)` (ufbx.c:2160)
                    // is a no-op in the default build; any other value would take the
                    // non-cancel path, which the enum makes unrepresentable here.
                    StopError = -28;
                    ptr = 0;
                    if (m_localBuffer == null) m_localBuffer = new byte[LocalBufferSize];
                    Buffer = m_localBuffer;
                    BufferSize = LocalBufferSize;
                    ChunkArray = m_localBuffer;
                    ChunkBegin = 0;
                    ChunkPtr = 0;
                    ChunkEnd = LocalBufferSize - 8;
                    ChunkRealEnd = LocalBufferSize;
                    System.Array.Clear(m_localBuffer, 0, LocalBufferSize);
                }
            }

            UpdateChunkYield(ptr);

            return ptr;
        }

        // C: ufbxi_bit_refill() (ufbx.c:2183-2196)
        // See https://fgiesen.wordpress.com/2018-02-20/reading-bits-in-far-too-many-ways-part-2/
        // variant 4. This branchless refill guarantees [56,63] bits to be valid in `bits`.
        public void BitRefill(ref ulong bits, ref int left, ref int data)
        {
            if (data > ChunkYield)
            {
                data = BitYield(data);
            }

            bits |= ReadU64(data) << left;
            data += (63 - left) >> 3;
            left |= 56;
        }

        // C: #define ufbxi_macro_bit_refill_fast() (ufbx.c:2199-2204), used by
        // `ufbxi_inflate_block_fast()`; expanded inline at each call site there.

        // C: ufbxi_bit_copy_bytes() (ufbx.c:2206-2250) — returns bool (C: `int` 0/1)
        public bool BitCopyBytes(byte[] dst, int dstOffset, int len)
        {
            // Copy the buffered bits first
            while (len > 0 && Left > 0)
            {
                dst[dstOffset++] = (byte)Bits;
                len -= 1;
                Bits >>= 8;
                Left -= 8;
            }

            // Copied fully from buffer
            if (len == 0)
            {
                return true;
            }

            // We need to clear the top bits as there may be data
            // read ahead past `s->left` in some cases
            Bits = 0;

            // Copy the current chunk
            int chunkLeft = ChunkRealEnd - ChunkPtr;
            if (chunkLeft >= len)
            {
                System.Array.Copy(ChunkArray, ChunkPtr, dst, dstOffset, len);
                ChunkPtr += len;
                return true;
            }
            else
            {
                System.Array.Copy(ChunkArray, ChunkPtr, dst, dstOffset, chunkLeft);
                ChunkPtr += chunkLeft;
                dstOffset += chunkLeft;
                len -= chunkLeft;
            }

            // Read extra bytes from user
            if (len > InputLeft) return false;
            int numRead = 0;
            if (ReadFn != null)
            {
                if (m_readScratch == null || m_readScratch.Length < len)
                {
                    m_readScratch = new byte[len];
                }
                numRead = ReadFn(ReadUser, m_readScratch, len);
                // C has no such clamp: a `read_fn()` that returns more than `len` makes C
                // write past `dst` (UB, ufbx.c:2246). Clamping keeps the port memory safe
                // while preserving the `num_read == len` failure result.
                if (numRead > len) numRead = len;
                System.Array.Copy(m_readScratch, 0, dst, dstOffset, numRead);
                InputLeft -= numRead;
            }
            return numRead == len;
        }
    }
}
