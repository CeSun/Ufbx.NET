// Invariant checks for the ported buffered reader (src/Ufbx.NET/Parse/Stream.cs).
//
// The C side cannot be recompiled here, so this harness validates the state machine
// against an oracle: whatever combination of refill / yield / skip / read_to paths is
// taken, the byte sequence and the absolute read offset must match the original input.

using System;
using Ufbx.NET;

namespace Ufbx.NET.Tests
{
    internal static class StreamCheck
    {
        static int failures;
        static int checks;

        static void Check(bool condition, string what)
        {
            checks++;
            if (!condition)
            {
                failures++;
                Console.WriteLine("FAIL: " + what);
            }
        }

        // C: whole input already in uc->data (ufbx_load_memory, ufbx.c:30514-30517).
        static UfbxiStream FromMemory(byte[] data)
        {
            return new UfbxiStream
            {
                Buffer = data,
                BeginIndex = 0,
                Position = 0,
                Remaining = data.Length,
                YieldSize = 0,
                ProgressInterval = ulong.MaxValue,
            };
        }

        // C: stream input, nothing buffered yet (ufbx.c:25289 zero_size_buffer).
        static UfbxiStream FromInput(UfbxInputStream input, int readBufferSize, byte[] prefix)
        {
            var stream = new UfbxiStream
            {
                Input = input,
                OptReadBufferSize = readBufferSize,
                ReadBufferSize = prefix != null ? prefix.Length : 0,
                ProgressInterval = ulong.MaxValue,
                Buffer = prefix ?? Array.Empty<byte>(),
                BeginIndex = 0,
                Position = 0,
                Remaining = prefix?.Length ?? 0,
                YieldSize = 0,
            };
            if (prefix != null) stream.ReadBuffer = prefix;
            return stream;
        }

        class ByteArrayInput : UfbxInputStream
        {
            protected readonly byte[] data;
            protected int position;
            public bool CanSeek = true;
            public int FailAfterReads = int.MaxValue;
            public int MaxChunk = int.MaxValue;
            int reads;

            public ByteArrayInput(byte[] data) { this.data = data; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (++reads > FailAfterReads) return -1;
                int toRead = Math.Min(Math.Min(data.Length - position, count), MaxChunk);
                if (toRead > 0) Array.Copy(data, position, buffer, offset, toRead);
                position += toRead;
                return toRead;
            }

            public override bool CanSkip => CanSeek;

            public override bool Skip(int size)
            {
                if (data.Length - position < size) return false;
                position += size;
                return true;
            }

            public override ulong Size() => (ulong)data.Length;
        }

        static void VerifyInvariants(UfbxiStream s, string where)
        {
            Check(s.YieldSize >= 0 && s.Remaining >= 0, "negative window counters at " + where);
            Check(s.Position >= s.BeginIndex, "position before begin at " + where);
            Check(s.YieldSize + s.Remaining == 0 || s.Buffer != null,
                "buffered bytes without a buffer at " + where);
            if (s.Buffer != null && s.YieldSize > 0)
            {
                Check(s.Position + s.YieldSize <= s.Buffer.Length,
                    "yield window past buffer end at " + where);
            }
        }

        // Read the whole input in blocks and compare against the source bytes.
        static void ReadAllBlocks(byte[] source, UfbxiStream s, int blockSize, string label)
        {
            int consumed = 0;
            byte[] got = new byte[source.Length];
            while (consumed < source.Length)
            {
                int size = Math.Min(blockSize, source.Length - consumed);
                int index = s.ReadBytes(size);
                Array.Copy(s.Buffer, index, got, consumed, size);
                consumed += size;
                Check(s.GetReadOffset() == (ulong)consumed,
                    label + ": read offset " + s.GetReadOffset() + " != consumed " + consumed);
                VerifyInvariants(s, label + " block@" + consumed);
            }
            Check(BytesEqual(source, got), label + ": byte content mismatch");
            Check(s.GetReadOffset() == (ulong)source.Length, label + ": final offset mismatch");
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static byte[] MakeData(int length)
        {
            byte[] data = new byte[length];
            uint state = 0x12345678u;
            for (int i = 0; i < length; i++)
            {
                state = state * 1103515245u + 12345u;
                data[i] = (byte)(state >> 16);
            }
            return data;
        }

        static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T e) { return e; }
            catch (Exception e) { Console.WriteLine("wrong exception: " + e.GetType().Name + " " + e.Message); return null; }
            Console.WriteLine("no exception thrown");
            return null;
        }

        public static int Run(string[] args)
        {
            byte[] source = MakeData(70000);

            // -- 1. memory-backed input, no read_fn
            ReadAllBlocks(source, FromMemory(source), 1, "memory/1");
            ReadAllBlocks(source, FromMemory(source), 4096, "memory/4096");
            ReadAllBlocks(source, FromMemory(source), 65536, "memory/65536");

            // -- 2. stream-backed input, varying read buffer sizes and short reads
            foreach (int bufferSize in new[] { 7, 64, 1024, 0x4000, 0x100000 })
            {
                ReadAllBlocks(source, FromInput(new ByteArrayInput(source), bufferSize, null),
                    1, "stream/" + bufferSize + "/1");
                ReadAllBlocks(source, FromInput(new ByteArrayInput(source), bufferSize, null),
                    33, "stream/" + bufferSize + "/33");
                ReadAllBlocks(source, FromInput(new ByteArrayInput(source) { MaxChunk = 3 }, bufferSize, null),
                    17, "stream/" + bufferSize + "/short3");
            }

            // -- 3. prefix (C: ufbx_load_stream_prefix) followed by stream data
            {
                byte[] prefix = MakeData(1000);
                byte[] rest = MakeData(70000);
                byte[] full = new byte[prefix.Length + rest.Length];
                Array.Copy(prefix, 0, full, 0, prefix.Length);
                Array.Copy(rest, 0, full, prefix.Length, rest.Length);
                ReadAllBlocks(full, FromInput(new ByteArrayInput(rest), 0x4000, prefix), 512, "prefix/512");
            }

            // -- 4. peek + consume without advancing, then advancing
            {
                var s = FromInput(new ByteArrayInput(source), 0x4000, null);
                s.PeekBytes(16);
                s.ConsumeBytes(16);
                Check(s.GetReadOffset() == 16, "peek/consume offset");
                int index2 = s.PeekBytes(16);
                byte[] next16 = new byte[16];
                Array.Copy(s.Buffer, index2, next16, 0, 16);
                byte[] expected = new byte[16];
                Array.Copy(source, 16, expected, 0, 16);
                Check(BytesEqual(expected, next16), "peek/consume content");
                VerifyInvariants(s, "peek");
            }

            // -- 5. skip_bytes on both C paths (skip_fn present / absent)
            foreach (bool canSkip in new[] { true, false })
            {
                foreach (long skip in new long[] { 1, 5, 0x3fff, 0x4000, 0x4001, 70000 - 8 })
                {
                    var input = new ByteArrayInput(source) { CanSeek = canSkip };
                    var s = FromInput(input, 0x4000, null);
                    s.SkipBytes((ulong)skip);
                    Check(s.GetReadOffset() == (ulong)skip,
                        "skip(" + (canSkip ? "seek" : "read") + "," + skip + ") offset " + s.GetReadOffset());
                    int index = s.PeekBytes(8);
                    bool ok = true;
                    for (int i = 0; i < 8; i++)
                    {
                        if (skip + i >= source.Length) break;
                        if (s.Buffer[index + i] != source[skip + i]) ok = false;
                    }
                    Check(ok, "skip(" + (canSkip ? "seek" : "read") + "," + skip + ") lands on right byte");
                    VerifyInvariants(s, "skip");
                }
            }

            // -- 6. read_to across the buffer boundary
            foreach (int bufferSize in new[] { 64, 1024, 0x4000 })
            {
                var s = FromInput(new ByteArrayInput(source), bufferSize, null);
                byte[] dst = new byte[20000];
                s.ReadTo(dst, 0, 3);
                s.ReadTo(dst, 3, 19997);
                Check(BytesEqual(dst, SubArray(source, 0, 20000)), "read_to content/" + bufferSize);
                Check(s.GetReadOffset() == 20000, "read_to offset/" + bufferSize);
                // continue reading through the normal path after a direct read
                int index = s.ReadBytes(4);
                Check(s.Buffer[index] == source[20000], "read_to then read/" + bufferSize);
                VerifyInvariants(s, "read_to/" + bufferSize);
            }

            // -- 7. error paths and their C messages
            {
                var empty = Throws<UfbxParseError>(() => FromInput(new ByteArrayInput(Array.Empty<byte>()), 0x4000, null).PeekBytes(4));
                Check(empty != null && empty.Message == "Empty file", "empty file message: " + empty?.Message);
                Check(empty != null && empty.ErrorType == UfbxErrorType.EmptyFile, "empty file type");

                var memEmpty = Throws<UfbxParseError>(() => FromMemory(Array.Empty<byte>()).PeekBytes(4));
                Check(memEmpty != null, "empty memory input throws");

                var truncated = Throws<UfbxParseError>(() =>
                {
                    var s = FromInput(new ByteArrayInput(MakeData(100)), 0x4000, null);
                    s.ReadBytes(200);
                });
                Check(truncated != null && truncated.Message == "Truncated file",
                    "truncated message: " + truncated?.Message);
                Check(truncated != null && truncated.ErrorType == UfbxErrorType.TruncatedFile, "truncated type");

                var io = Throws<UfbxParseError>(() =>
                {
                    var input = new ByteArrayInput(source) { FailAfterReads = 2, MaxChunk = 4 };
                    FromInput(input, 0x4000, null).ReadBytes(100);
                });
                Check(io != null && io.Message == "IO error", "io message: " + io?.Message);
                Check(io != null && io.ErrorType == UfbxErrorType.Io, "io type");

                var skipPast = Throws<UfbxParseError>(() =>
                {
                    var s = FromInput(new ByteArrayInput(MakeData(1000)), 0x4000, null);
                    s.SkipBytes(5000);
                });
                Check(skipPast != null, "skip past EOF throws");
            }

            // -- 8. progress window splitting (C: progress_interval clamps yield_size)
            {
                var s = FromInput(new ByteArrayInput(source), 0x4000, null);
                s.ProgressInterval = 128;
                s.ProgressCb = new UfbxProgressCb { Fn = (user, p) => UfbxProgressResult.Continue };
                ReadAllBlocks(source, s, 100, "progress/128");
            }

            Console.WriteLine("streamcheck: " + checks + " checks, " + failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        static byte[] SubArray(byte[] data, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(data, offset, result, 0, length);
            return result;
        }
    }
}
