// Buffered reader core, ported from ufbx.c v0.23.1 "IO" section (ufbx.c:6664-6920).
//
// C works with raw pointers into a read buffer; this port keeps the identical state
// machine but expresses `data_begin`/`data` as (Buffer, BeginIndex, Position) so the
// refill/yield/progress accounting stays bit-for-bit equivalent:
//   * `Remaining` is C's `data_size`: valid bytes after the yield window.
//   * `YieldSize` is C's `yield_size`: bytes immediately available at `Position`.
//   * Total readable bytes at `Position` == YieldSize + Remaining.
// Both Pause/Resume/Yield move bytes between the two counters exactly like C.
//
// Errors: every `ufbxi_check*` that produced C's `return NULL`/`return 0` throws
// UfbxParseError (PORTING_NOTES.md #3). The IO module is audited for C's two failure
// forms (Parse/Error.cs): the `ufbxi_check_msg` sites ("Empty file", "Truncated file",
// "IO error", "Cancelled") carry the message as `error.description`, while the plain
// `ufbxi_check` sites (`!uc->eof`, `read_result <= to_read`, `uc->read_fn`,
// `read_result != 0`) carry none and surface as the "Failed to load" default, so they
// go through `UfbxiFail.CheckNoDesc`.

namespace Ufbx.NET
{
    internal sealed class UfbxiStream
    {
        // C: UFBXI_MAX_SKIP_SIZE (ufbx.c:54), the non-UFBXI_SMALL_MEMORY value.
        const int MaxSkipSize = 0x40000000;

        // C: uc->data_offset — bytes consumed before the current buffer.
        public ulong DataOffset;

        // C: uc->read_buffer / uc->read_buffer_size (grows on demand in Refill).
        public byte[] ReadBuffer;
        public int ReadBufferSize;

        // C: uc->data_begin / uc->data / uc->data_size / uc->yield_size.
        public byte[] Buffer;
        public int BeginIndex;
        public int Position;
        public int Remaining;
        public int YieldSize;

        // C: uc->eof.
        public bool Eof;

        // C: uc->read_user / skip_fn / size_fn / close_fn — NULL means "whole input is
        // already in Buffer" (ufbx_load_memory), matching C's `read_fn == NULL` path.
        public UfbxInputStream Input;

        // C: uc->opts.progress_cb / progress_bytes_total / progress_interval /
        //    latest_progress_bytes / progress_timer.
        public UfbxProgressCb ProgressCb;
        public ulong ProgressBytesTotal;
        public ulong ProgressInterval;
        public ulong LatestProgressBytes;
        public long ProgressTimer;

        // C: uc->opts.read_buffer_size, clamped by ufbxi_load() (ufbx.c:25514-25519).
        public int OptReadBufferSize = 0x4000;

        // C: ufbxi_get_read_offset() (ufbx.c:6664).
        public ulong GetReadOffset()
        {
            return DataOffset + (ulong)(Position - BeginIndex);
        }

        // C: ufbxi_report_progress() (ufbx.c:6669).
        public bool ReportProgress()
        {
            if (ProgressCb == null || ProgressCb.Fn == null) return true;

            ulong readOffset = GetReadOffset();
            LatestProgressBytes = readOffset;

            UfbxProgress progress = new UfbxProgress();
            progress.BytesRead = readOffset;
            progress.BytesTotal = ProgressBytesTotal;
            if (progress.BytesTotal < progress.BytesRead)
            {
                progress.BytesTotal = progress.BytesRead;
            }

            ProgressTimer = 1024;
            UfbxProgressResult result = ProgressCb.Fn(ProgressCb.User, progress);
            if (result == UfbxProgressResult.Cancel)
            {
                // C: ufbxi_check_msg(result != UFBX_PROGRESS_CANCEL, "Cancelled") (ufbx.c:6686).
                UfbxiFail.FailMsg("result != UFBX_PROGRESS_CANCEL", "Cancelled");
            }
            return true;
        }

        // C: ufbxi_progress() (ufbx.c:6691).
        public bool Progress(int workUnits)
        {
            if (ProgressCb == null || ProgressCb.Fn == null) return true;
            long left = ProgressTimer - workUnits;
            ProgressTimer = left;
            if (left > 0) return true;
            return ReportProgress();
        }

        // C: ufbxi_refill() (ufbx.c:6702).
        // Returns the new index of the readable data in `Buffer` (C: the returned pointer).
        public int Refill(int size, bool requireSize)
        {
            // C: `ufbxi_check_return(!uc->eof, NULL)` -- a plain failure: reading past EOF does
            // NOT write "Truncated file" as the description; only the `require_size` checks below
            // do (ufbx.c:6705-6712, 6755-6759).
            UfbxiFail.CheckNoDesc(!Eof, "!uc->eof");

            if (requireSize)
            {
                if (Input == null && Remaining <= 0)
                {
                    // C: ufbxi_check_return_msg(uc->read_fn || uc->data_size > 0, NULL, "Empty file")
                    UfbxiFail.FailMsg("uc->read_fn || uc->data_size > 0", "Empty file");
                }
                if (Input == null)
                {
                    // C: ufbxi_check_return_msg(uc->read_fn, NULL, "Truncated file")
                    UfbxiFail.FailMsg("uc->read_fn", "Truncated file");
                }
            }
            else if (Input == null)
            {
                Eof = true;
                return Position;
            }

            // Grow the read buffer if necessary. C defers freeing the old buffer until
            // after the copy; the GC makes that explicit step unnecessary (PORTING_NOTES #4).
            if (size > ReadBufferSize)
            {
                int newSize = size > OptReadBufferSize ? size : OptReadBufferSize;
                if (ReadBufferSize * 2 > newSize) newSize = ReadBufferSize * 2;
                ReadBuffer = new byte[newSize];
                ReadBufferSize = newSize;
            }

            // Copy the remains of the previous buffer to the beginning of the new one.
            byte[] buffer = ReadBuffer;
            int dataSize = Remaining;
            if (dataSize > 0)
            {
                System.Array.Copy(Buffer, Position, buffer, 0, dataSize);
            }

            // Fill the rest of the buffer with user data.
            int dataCapacity = ReadBufferSize;
            while (dataSize < dataCapacity)
            {
                int toRead = dataCapacity - dataSize;
                int readResult = Input.Read(buffer, dataSize, toRead);
                if (readResult < 0)
                {
                    // C: ufbxi_check_return_msg(read_result != SIZE_MAX, NULL, "IO error")
                    UfbxiFail.FailMsg("read_result != SIZE_MAX", "IO error");
                }
                // C: `ufbxi_check(read_result <= to_read)` -- plain, no description.
                UfbxiFail.CheckNoDesc(readResult <= toRead, "read_result <= to_read");
                dataSize += readResult;
                if (readResult == 0)
                {
                    Eof = true;
                    break;
                }
            }

            if (requireSize)
            {
                if (DataOffset == 0)
                {
                    if (dataSize <= 0)
                    {
                        // C: ufbxi_check_return_msg(data_size > 0, NULL, "Empty file")
                        UfbxiFail.FailMsg("data_size > 0", "Empty file");
                    }
                }
                if (dataSize < size)
                {
                    // C: ufbxi_check_return_msg(data_size >= size, NULL, "Truncated file")
                    UfbxiFail.FailMsg("data_size >= size", "Truncated file");
                }
            }

            DataOffset += (ulong)(Position - BeginIndex);
            Buffer = buffer;
            BeginIndex = 0;
            Position = 0;
            Remaining = dataSize;

            return 0;
        }

        // C: ufbxi_pause_progress() (ufbx.c:6769).
        public void PauseProgress()
        {
            Remaining += YieldSize;
            YieldSize = 0;
        }

        // C: ufbxi_min_sz() against a `size_t` bound that can be SIZE_MAX
        // (`progress_interval` without a progress callback).
        static int MinOf(int value, ulong bound)
        {
            return bound >= (ulong)value ? value : (int)bound;
        }

        // C: ufbxi_resume_progress() (ufbx.c:6775).
        public bool ResumeProgress()
        {
            YieldSize = MinOf(Remaining, ProgressInterval);
            Remaining -= YieldSize;

            if (GetReadOffset() - LatestProgressBytes >= ProgressInterval)
            {
                if (!ReportProgress()) return false;
            }

            return true;
        }

        // C: ufbxi_yield() (ufbx.c:6787).
        int Yield(int size)
        {
            int ret;
            Remaining += YieldSize;
            if (Remaining >= size)
            {
                ret = Position;
            }
            else
            {
                ret = Refill(size, true);
            }
            ulong bound = (ulong)size > ProgressInterval ? (ulong)size : ProgressInterval;
            YieldSize = MinOf(Remaining, bound);
            Remaining -= YieldSize;

            ReportProgress();
            return ret;
        }

        // C: ufbxi_peek_bytes() (ufbx.c:6803).
        // The returned index stays valid only until the next Yield/Refill, exactly like
        // C's returned pointer; callers must copy or consume before re-peeking.
        public int PeekBytes(int size)
        {
            if (YieldSize >= size)
            {
                return Position;
            }
            else
            {
                return Yield(size);
            }
        }

        // C: ufbxi_read_bytes() (ufbx.c:6812).
        public int ReadBytes(int size)
        {
            int ret;
            if (YieldSize >= size)
            {
                ret = Position;
            }
            else
            {
                ret = Yield(size);
            }

            YieldSize -= size;
            Position = ret + size;
            return ret;
        }

        // C: ufbxi_consume_bytes() (ufbx.c:6829).
        // Bytes must have been checked first with PeekBytes().
        public void ConsumeBytes(int size)
        {
            YieldSize -= size;
            Position += size;
        }

        // C: ufbxi_skip_bytes() (ufbx.c:6837).
        public void SkipBytes(ulong size)
        {
            if (Input != null && Input.CanSkip)
            {
                PauseProgress();

                if (size > (ulong)Remaining)
                {
                    size -= (ulong)Remaining;
                    Position += Remaining;
                    Remaining = 0;

                    DataOffset += size;
                    while (size >= MaxSkipSize)
                    {
                        size -= MaxSkipSize;
                        if (!Input.Skip(MaxSkipSize - 1))
                        {
                            // C: ufbxi_check_msg(uc->skip_fn(...), "Truncated file")
                            UfbxiFail.FailMsg("uc->skip_fn(uc->read_user, UFBXI_MAX_SKIP_SIZE - 1)", "Truncated file");
                        }

                        // Check that we can read at least one byte in case the file is broken
                        // and causes us to seek indefinitely forwards as `fseek()` does not
                        // report if we hit EOF...
                        byte[] singleByte = new byte[1];
                        int numRead = Input.Read(singleByte, 0, 1);
                        if (numRead > 1)
                        {
                            // C: ufbxi_check_msg(num_read <= 1, "IO error")
                            UfbxiFail.FailMsg("num_read <= 1", "IO error");
                        }
                        if (numRead != 1)
                        {
                            // C: ufbxi_check_msg(num_read == 1, "Truncated file")
                            UfbxiFail.FailMsg("num_read == 1", "Truncated file");
                        }
                    }

                    if (size > 0)
                    {
                        if (!Input.Skip((int)size))
                        {
                            // C: ufbxi_check_msg(uc->skip_fn(uc->read_user, (size_t)size), "Truncated file")
                            UfbxiFail.FailMsg("uc->skip_fn(uc->read_user, (size_t)size)", "Truncated file");
                        }
                    }
                }
                else
                {
                    Position += (int)size;
                    Remaining -= (int)size;
                }

                ResumeProgress();
            }
            else
            {
                // Read and discard bytes in reasonable chunks.
                int currentBufferSize = ReadBuffer != null ? ReadBufferSize : 0;
                ulong skipSize = (ulong)currentBufferSize > (ulong)OptReadBufferSize
                    ? (ulong)currentBufferSize
                    : (ulong)OptReadBufferSize;
                while (size > 0)
                {
                    ulong toSkip = size < skipSize ? size : skipSize;
                    ReadBytes((int)toSkip);
                    size -= toSkip;
                }
            }
        }

        // C: ufbxi_read_to() (ufbx.c:6884).
        public void ReadTo(byte[] dst, int dstOffset, int size)
        {
            PauseProgress();

            // Copy data from the current buffer first.
            int len = Remaining < size ? Remaining : size;
            if (len > 0)
            {
                System.Array.Copy(Buffer, Position, dst, dstOffset, len);
            }
            Position += len;
            Remaining -= len;
            dstOffset += len;
            size -= len;

            // If there's data left to copy try to read from user IO.
            if (size > 0)
            {
                DataOffset += (ulong)(Position - BeginIndex);

                Buffer = null;
                BeginIndex = 0;
                Position = 0;
                Remaining = 0;
                // C: `ufbxi_check(uc->read_fn)` -- plain, no description.
                UfbxiFail.CheckNoDesc(Input != null, "uc->read_fn");

                while (size > 0)
                {
                    int readResult = Input.Read(dst, dstOffset, size);
                    if (readResult < 0)
                    {
                        // C: ufbxi_check_return_msg(read_result != SIZE_MAX, NULL, "IO error")
                        UfbxiFail.FailMsg("read_result != SIZE_MAX", "IO error");
                    }
                    // C: `ufbxi_check(read_result != 0)` -- plain, no description.
                    UfbxiFail.CheckNoDesc(readResult != 0, "read_result != 0");

                    dstOffset += readResult;
                    size -= readResult;
                    DataOffset += (ulong)readResult;
                }
            }

            ResumeProgress();
        }
    }
}
