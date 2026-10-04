// Default UfbxInputStream implementations, ported from ufbx.c v0.23.1
// "File IO" (ufbx.c:6981-7160) and "Memory IO" (ufbx.c:7180-7235).
//
// PORTING_NOTES.md #8 requires built-in file/memory streams behind the abstract
// UfbxInputStream. `CanSkip` reproduces C's optional `skip_fn`: the stdio stream provides
// a real seek, the memory stream does not (C: ufbxi_memory_skip exists but
// ufbxi_memory_stream is wired with it — see below), so the chunked-read fallback in
// UfbxiStream.SkipBytes stays reachable exactly as in C.

using System;
using System.IO;

namespace Ufbx.NET
{
    // C: ufbxi_memory_stream (ufbx.c:7191-7235).
    internal sealed class UfbxMemoryInputStream : UfbxInputStream
    {
        readonly byte[] data;
        readonly int size;
        int position;

        // C: `ufbx_close_memory_cb close_cb` (ufbx.c:7190) -- set only by ufbx_open_memory().
        UfbxCloseMemoryCb closeCb;

        public UfbxMemoryInputStream(byte[] data, int size)
        {
            this.data = data;
            this.size = size;
        }

        public UfbxMemoryInputStream(byte[] data, int size, UfbxCloseMemoryCb closeCb)
            : this(data, size)
        {
            this.closeCb = closeCb;
        }

        // C: ufbxi_memory_read() — a short read is the amount actually left, EOF is
        // signalled by 0 (which ufbxi_refill turns into `eof = true`).
        public override int Read(byte[] buffer, int offset, int count)
        {
            int toRead = size - position < count ? size - position : count;
            if (toRead > 0)
            {
                Array.Copy(data, position, buffer, offset, toRead);
            }
            position += toRead;
            return toRead;
        }

        // C: ufbxi_memory_skip().
        public override bool CanSkip => true;

        public override bool Skip(int skipSize)
        {
            if (size - position < skipSize) return false;
            position += skipSize;
            return true;
        }

        // C: ufbxi_memory_size().
        public override ulong Size()
        {
            return (ulong)size;
        }

        // C: ufbxi_memory_close() (ufbx.c:7223-7237): `close_cb.fn(close_cb.user, stream->data,
        // stream->size)` first, then the arena free. `stream->data` is the caller's buffer for
        // `no_copy` and the internal copy otherwise, so what the callback receives identifies
        // which of the two the stream ended up owning. C has no guard against calling this
        // twice (the second `ufbxi_free` would be a double free), so neither has the port: the
        // callback fires on every Close(), exactly as C's does.
        public override void Close()
        {
            if (closeCb != null && closeCb.Fn != null) {
                closeCb.Fn(closeCb.User, data, size);
            }
            // C: the `self_size` free of the metadata block (and of `data_copy`) -- managed.
        }
    }

    // C: ufbxi_stdio_stream / ufbxi_stdio_init (ufbx.c:7082-7125).
    internal sealed class UfbxFileInputStream : UfbxInputStream
    {
        readonly FileStream file;
        readonly bool ownsFile;

        // C: ufbxi_fopen() (ufbx.c:6981) — on Windows the UTF-8 path is converted to
        // UTF-16 before opening; .NET already takes a UTF-16 string, so `path` here is
        // the *already resolved* path from UfbxiStreamOpen.PathToUtf16(), not raw bytes.
        public UfbxFileInputStream(string path)
        {
            file = OpenFile(path);
            ownsFile = true;
        }

        // C: ufbxi_stdio_init(stream, fp, close=false) — the caller keeps owning the FILE*.
        public UfbxFileInputStream(FileStream file)
        {
            this.file = file;
            ownsFile = false;
        }

        // C: ufbxi_stdio_init(stream, fp, close=true) via ufbxi_stdio_open() (ufbx.c:7135-7141)
        // and ufbx_load_stdio() — ufbx owns the handle and closes it with the stream.
        public UfbxFileInputStream(FileStream file, bool ownsHandle)
        {
            this.file = file;
            this.ownsFile = ownsHandle;
        }
        static FileStream OpenFile(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                0x4000, FileOptions.SequentialScan);
        }

        // C: ufbxi_stdio_read() — SIZE_MAX (mapped to -1) when `ferror()` is set.
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return file.Read(buffer, offset, count);
            }
            catch (IOException)
            {
                return -1;
            }
        }

        // C: ufbxi_stdio_skip() — `fseek(SEEK_CUR)` succeeds even past EOF, which is why
        // ufbxi_skip_bytes() probes a single byte afterwards (ufbx.c:6852-6858).
        public override bool CanSkip => true;

        public override bool Skip(int skipSize)
        {
            try
            {
                file.Seek(skipSize, SeekOrigin.Current);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        // C: ufbxi_stdio_size() — 0 if the size cannot be determined.
        public override ulong Size()
        {
            try
            {
                long begin = file.Position;
                long end = file.Seek(0, SeekOrigin.End);
                file.Seek(begin, SeekOrigin.Begin);
                if (begin < end) return (ulong)(end - begin);
            }
            catch (IOException)
            {
            }
            return 0;
        }

        // C: ufbxi_stdio_close() — only wired when ufbx owns the FILE*.
        public override void Close()
        {
            if (ownsFile)
            {
                file.Dispose();
            }
        }
    }
}
