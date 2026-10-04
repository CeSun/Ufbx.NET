// Runtime callback / IO / error / allocator types ported from ufbx v0.23.1 (ufbx.h).
//
// Ownership: opts / runtime callback layer (see task scope).
// Conventions (PORTING_NOTES.md #28/#37/#38/#39):
//  - opts and callback holders are `class`; fields keep C-equivalent zero defaults
//    (strings are "" per the ufbx_string -> string mapping, nested value-struct
//    fields are `new`-initialized to emulate embedded zeroed structs).
//  - C function-pointer typedefs -> `public delegate` types with matching signatures.
//  - `void *user` contexts -> `object`; `uintptr_t` contexts -> `nint`.
//  - `size_t` -> `int`; `uint64_t` -> `ulong`.
//  - Allocator callbacks are retained but ignored by the port (PORTING_NOTES #4).
//  - `_begin_zero` / `_end_zero` sentinels (used by C to detect non-zero-cleared
//    options structs) are omitted: C# fields cannot be left uninitialized.

using System;

namespace Ufbx
{
    // -- Memory callbacks (C: ufbx.h:4076-4137)

    // C: typedef void *ufbx_alloc_fn(void *user, size_t size); (ufbx.h:4082)
    // Allocate `size` bytes, must be at least 8 byte aligned.
    public delegate object UfbxAllocFn(object user, int size);

    // C: typedef void *ufbx_realloc_fn(void *user, void *old_ptr, size_t old_size, size_t new_size); (ufbx.h:4088)
    public delegate object UfbxReallocFn(object user, object oldPtr, int oldSize, int newSize);

    // C: typedef void ufbx_free_fn(void *user, void *ptr, size_t size); (ufbx.h:4091)
    public delegate void UfbxFreeFn(object user, object ptr, int size);

    // C: typedef void ufbx_free_allocator_fn(void *user); (ufbx.h:4094)
    public delegate void UfbxFreeAllocatorFn(object user);

    // Allocator callbacks and user context (C: ufbx_allocator, ufbx.h:4100-4107)
    public class UfbxAllocator
    {
        public UfbxAllocFn AllocFn;                 // C: alloc_fn
        public UfbxReallocFn ReallocFn;             // C: realloc_fn
        public UfbxFreeFn FreeFn;                   // C: free_fn
        public UfbxFreeAllocatorFn FreeAllocatorFn; // C: free_allocator_fn
        public object User;                         // C: void *user
    }

    // C: ufbx_allocator_opts (ufbx.h:4109-4137)
    public class UfbxAllocatorOpts
    {
        public UfbxAllocator Allocator = new UfbxAllocator(); // C: allocator (embedded struct)
        public int MemoryLimit;                               // C: memory_limit (size_t)
        public int AllocationLimit;                           // C: allocation_limit (size_t)
        public int HugeThreshold;                             // C: huge_threshold (size_t)
        public int MaxChunkSize;                              // C: max_chunk_size (size_t)

        // Effective defaults applied by ufbxi_init_ator (C: ufbx.c:6935-6937):
        //   memory_limit     0 -> SIZE_MAX  (no limit; kept 0 here)
        //   allocation_limit 0 -> SIZE_MAX  (no limit; kept 0 here)
        //   huge_threshold   0 -> 0x100000  (1MB,   ufbx.c:6936)
        //   max_chunk_size   0 -> 0x1000000 (16MB,  ufbx.c:6937)
        // NOTE: Allocator opts are retained but ignored by the C# port (PORTING_NOTES #4).
        public static UfbxAllocatorOpts CreateDefault()
        {
            var opts = new UfbxAllocatorOpts();
            opts.HugeThreshold = 0x100000;
            opts.MaxChunkSize = 0x1000000;
            return opts;
        }
    }

    // -- IO callbacks (C: ufbx.h:4139-4163)

    // C: typedef size_t ufbx_read_fn(void *user, void *data, size_t size); (ufbx.h:4143)
    // Try to read up to `size` bytes to `data`, return the amount of read bytes.
    // Return -1 (C: SIZE_MAX) to indicate an IO error.
    public delegate int UfbxReadFn(object user, byte[] data, int size);

    // C: typedef bool ufbx_skip_fn(void *user, size_t size); (ufbx.h:4146)
    public delegate bool UfbxSkipFn(object user, int size);

    // C: typedef uint64_t ufbx_size_fn(void *user); (ufbx.h:4150)
    // Return 0 if unknown, ulong.MaxValue (C: UINT64_MAX) if error.
    public delegate ulong UfbxSizeFn(object user);

    // C: typedef void ufbx_close_fn(void *user); (ufbx.h:4153)
    public delegate void UfbxCloseFn(object user);

    // C: ufbx_stream (ufbx.h:4155-4163).
    // Per PORTING_NOTES.md #8 the callback quad + `user` context is mapped to an abstract
    // class: the instance itself holds the state that would live in `user`.
    public abstract class UfbxInputStream
    {
        // C: void *user (ufbx.h:4156) — opaque context, unused here since subclasses
        // carry their own state.
        public object User;

        // C: ufbx_read_fn *read_fn (ufbx.h:4157) — Required.
        // Read up to `count` bytes into `buffer` starting at `offset`, return the amount of
        // bytes read, negative on IO error (C: SIZE_MAX). Short reads are allowed and the
        // caller loops (C: `ufbxi_refill()`), except that reading 0 bytes means EOF.
        public abstract int Read(byte[] buffer, int offset, int count);

        // C: ufbx_skip_fn *skip_fn (ufbx.h:4158) — Optional in C: `ufbxi_skip_bytes()`
        // (ufbx.c:6837) takes a chunked-read path when `skip_fn` is NULL, which changes
        // how the buffered reader accounts for skipped bytes. Implementations that provide
        // a real seek must set `CanSkip` to true.
        public virtual bool CanSkip => false;

        // Returns false on error.
        public virtual bool Skip(int size)
        {
            byte[] scratch = new byte[4096];
            while (size > 0)
            {
                int chunk = size > scratch.Length ? scratch.Length : size;
                int read = Read(scratch, 0, chunk);
                if (read <= 0) return false;
                size -= read;
            }
            return true;
        }

        // C: ufbx_size_fn *size_fn (ufbx.h:4159) — Optional.
        // Return 0 if unknown, ulong.MaxValue (C: UINT64_MAX) if error.
        public virtual ulong Size() => 0;

        // C: ufbx_close_fn *close_fn (ufbx.h:4160) — Optional.
        public virtual void Close() { }
    }

    // -- External file opening (C: ufbx.h:4165-4214)

    // C: typedef uintptr_t ufbx_open_file_context; (ufbx.h:4175)
    // (Used directly as `nint` below; uintptr_t -> nint.)

    // C: ufbx_open_file_info (ufbx.h:4177-4189)
    public struct UfbxOpenFileInfo
    {
        public nint Context;                 // C: ufbx_open_file_context context (uintptr_t)
        public UfbxOpenFileType Type;        // C: type
        public byte[] OriginalFilename;      // C: ufbx_blob original_filename
    }

    // C: typedef bool ufbx_open_file_fn(void *user, ufbx_stream *stream,
    //        const char *path, size_t path_len, const ufbx_open_file_info *info); (ufbx.h:4192)
    public delegate bool UfbxOpenFileFn(object user, UfbxInputStream stream, string path, int pathLength, UfbxOpenFileInfo info);

    // C: ufbx_open_file_cb (ufbx.h:4194-4201)
    public class UfbxOpenFileCb
    {
        public UfbxOpenFileFn Fn;   // C: fn
        public object User;         // C: void *user
    }

    // Options for `ufbx_open_file()` (C: ufbx_open_file_opts, ufbx.h:4204-4214)
    public class UfbxOpenFileOpts
    {
        public UfbxAllocatorOpts Allocator = new UfbxAllocatorOpts(); // C: allocator
        public bool FilenameNullTerminated;                           // C: ufbx_unsafe bool filename_null_terminated
    }

    // -- Memory streams (C: ufbx.h:4216-4246)

    // C: typedef void ufbx_close_memory_fn(void *user, void *data, size_t data_size); (ufbx.h:4217)
    public delegate void UfbxCloseMemoryFn(object user, byte[] data, int dataSize);

    // C: ufbx_close_memory_cb (ufbx.h:4219-4226)
    public class UfbxCloseMemoryCb
    {
        public UfbxCloseMemoryFn Fn; // C: fn
        public object User;          // C: void *user
    }

    // Options for `ufbx_open_memory()` (C: ufbx_open_memory_opts, ufbx.h:4229-4246)
    public class UfbxOpenMemoryOpts
    {
        public UfbxAllocatorOpts Allocator = new UfbxAllocatorOpts(); // C: allocator
        public bool NoCopy;                                           // C: ufbx_unsafe bool no_copy
        public UfbxCloseMemoryCb CloseCb = new UfbxCloseMemoryCb();   // C: close_cb (embedded struct)
    }

    // -- Errors (C: ufbx.h:4248-4268, 4348-4368)

    // Detailed error stack frame. (C: ufbx_error_frame, ufbx.h:4250-4254)
    // NOTE (C): requires UFBX_ENABLE_ERROR_STACK.
    public struct UfbxErrorFrame
    {
        public uint SourceLine;   // C: source_line
        public string Function;   // C: ufbx_string function
        public string Description; // C: ufbx_string description
    }

    // Error description with detailed stack trace. (C: ufbx_error, ufbx.h:4350-4368)
    public class UfbxError
    {
        public UfbxErrorType Type;              // C: type
        public string Description = "";         // C: ufbx_string description

        public uint StackSize;                  // C: stack_size
        public UfbxErrorFrame[] Stack =
            new UfbxErrorFrame[UfbxConstants.ErrorStackMaxDepth]; // C: stack[UFBX_ERROR_STACK_MAX_DEPTH]

        public int InfoLength;                  // C: info_length (size_t)
        public string Info = "";                // C: char info[UFBX_ERROR_INFO_LENGTH] (NULL-terminated UTF-8)
    }

    // -- Progress callbacks (C: ufbx.h:4370-4402)

    // Loading progress information. (C: ufbx_progress, ufbx.h:4373-4376)
    public struct UfbxProgress
    {
        public ulong BytesRead;   // C: bytes_read (uint64_t)
        public ulong BytesTotal;  // C: bytes_total (uint64_t)
    }

    // C: typedef ufbx_progress_result ufbx_progress_fn(void *user, const ufbx_progress *progress); (ufbx.h:4393)
    public delegate UfbxProgressResult UfbxProgressFn(object user, UfbxProgress progress);

    // C: ufbx_progress_cb (ufbx.h:4395-4402)
    public class UfbxProgressCb
    {
        public UfbxProgressFn Fn; // C: fn
        public object User;       // C: void *user
    }

    // -- Thread pool (C: ufbx.h:4609-4675)

    // C: typedef uintptr_t ufbx_thread_pool_context; (ufbx.h:4614)
    // (Used directly as `nint` in the delegates below.)

    // Thread pool creation information from ufbx. (C: ufbx_thread_pool_info, ufbx.h:4617-4619)
    public struct UfbxThreadPoolInfo
    {
        public uint MaxConcurrentTasks; // C: max_concurrent_tasks
    }

    // C: typedef bool ufbx_thread_pool_init_fn(void *user, ufbx_thread_pool_context ctx,
    //        const ufbx_thread_pool_info *info); (ufbx.h:4623)
    public delegate bool UfbxThreadPoolInitFn(object user, nint ctx, UfbxThreadPoolInfo info);

    // C: typedef void ufbx_thread_pool_run_fn(void *user, ufbx_thread_pool_context ctx,
    //        uint32_t group, uint32_t start_index, uint32_t count); (ufbx.h:4629)
    public delegate void UfbxThreadPoolRunFn(object user, nint ctx, uint group, uint startIndex, uint count);

    // C: typedef void ufbx_thread_pool_wait_fn(void *user, ufbx_thread_pool_context ctx,
    //        uint32_t group, uint32_t max_index); (ufbx.h:4633)
    public delegate void UfbxThreadPoolWaitFn(object user, nint ctx, uint group, uint maxIndex);

    // C: typedef void ufbx_thread_pool_free_fn(void *user, ufbx_thread_pool_context ctx); (ufbx.h:4636)
    public delegate void UfbxThreadPoolFreeFn(object user, nint ctx);

    // Thread pool interface. (C: ufbx_thread_pool, ufbx.h:4650-4656)
    public class UfbxThreadPool
    {
        public UfbxThreadPoolInitFn InitFn; // C: init_fn (optional)
        public UfbxThreadPoolRunFn RunFn;   // C: run_fn (required)
        public UfbxThreadPoolWaitFn WaitFn; // C: wait_fn (required)
        public UfbxThreadPoolFreeFn FreeFn; // C: free_fn (optional)
        public object User;                 // C: void *user
    }

    // Thread pool options. (C: ufbx_thread_opts, ufbx.h:4659-4675)
    public class UfbxThreadOpts
    {
        public UfbxThreadPool Pool = new UfbxThreadPool(); // C: pool (embedded struct)
        public int NumTasks;                               // C: num_tasks (size_t), Default: 2048
        public int MemoryLimit;                            // C: memory_limit (size_t), Default: 32MB

        // Effective defaults applied by the C implementation:
        //   num_tasks     0 -> 2048      (ufbx.c:6072-6074)
        //   memory_limit  0 -> 32MB      (ufbx.c:25543-25545)
        public static UfbxThreadOpts CreateDefault()
        {
            var opts = new UfbxThreadOpts();
            opts.NumTasks = 2048;
            opts.MemoryLimit = 32 * 1024 * 1024;
            return opts;
        }
    }

    // -- Panic (C: ufbx_panic, ufbx.h:5240-5244)

    public struct UfbxPanic
    {
        public bool DidPanic;      // C: did_panic
        public int MessageLength;  // C: message_length (size_t)
        public string Message;     // C: char message[UFBX_PANIC_MESSAGE_LENGTH]
    }
}
