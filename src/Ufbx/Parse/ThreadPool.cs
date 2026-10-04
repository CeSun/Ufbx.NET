// Thread pool: the provider-side machinery and the three `ufbx_thread_pool_*` public ABIs,
// ported from ufbx v0.23.1 ufbx.c:
//   typedef bool ufbxi_task_fn(ufbxi_task*)                            (5968)
//   struct ufbxi_task / ufbxi_task_imp / ufbxi_task_group              (5973-5986)
//   struct ufbxi_thread_pool                                           (5988-6007)
//   ufbxi_thread_pool_execute                                          (6009-6017)
//   ufbxi_thread_pool_update_finished                                  (6019-6029)
//   ufbxi_thread_pool_wait_imp / _wait_group / _wait_all               (6031-6065)
//   ufbxi_thread_pool_init                                             (6067-6089)
//   ufbxi_thread_pool_free                                             (6093-6108)
//   ufbxi_thread_pool_available_tasks                                  (6110-6113)
//   ufbxi_thread_pool_flush_group                                      (6115-6128)
//   ufbxi_thread_pool_create_task                                      (6130-6151)
//   ufbxi_thread_pool_run_task (static, distinct from the public one)  (6153-6159)
//   ufbx_thread_pool_run_task / _set_user_ptr / _get_user_ptr          (32984-32999)
//
// `ufbx.h` side: `ufbx_thread_pool_context` is a `uintptr_t` (4614) that C casts straight to
// `ufbxi_thread_pool *` and dereferences, which is why all three are `ufbx_unsafe`
// (5747/5751/5752): any value other than one ufbx itself handed out is a wild write. This port
// therefore keeps a registry of live pools and hands their own `nint` out as the context; see
// `UfbxiThreadPool.FromCtx` for the semantics of an unknown context.
//
// Unmodelled here, all recorded in PORTING_NOTES.md:
//   - The task ring is allocated from the temporary arena in C (`ufbxi_alloc`, ufbx.c:6088);
//     allocation failure is not observable in the port (PORTING_NOTES #4). The ring is modelled
//     as a sparse map so `num_tasks` itself (which drives `index % num_tasks`) stays meaningful
//     even for absurd values such as INT32_MAX.
//   - Real concurrency: `ufbxi_thread_pool_execute()` is *called* by whoever owns the context,
//     possibly from another thread, exactly like C. Nothing in ufbx synchronizes those accesses
//     beyond the group protocol, and neither does this class.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Ufbx
{
    // C: typedef bool ufbxi_task_fn(ufbxi_task *task) (ufbx.c:5968). Return false to fail the
    // task; setting `task.Error` is optional (see UfbxiThreadPool.Execute).
    internal delegate bool UfbxiTaskFn(UfbxiTask task);

    // C: struct ufbxi_task (ufbx.c:5973-5976) — the part a task function sees — plus the
    // enclosing struct ufbxi_task_imp (5978-5981). They are separate types in C only because
    // `ufbxi_task_fn*` must not be visible to task functions; one class covers both here.
    internal sealed class UfbxiTask
    {
        public object Data;      // C: void *data
        public string Error;     // C: const char *error — NULL means "no error yet"
        internal UfbxiTaskFn Fn; // C: ufbxi_task_fn *fn (ufbxi_task_imp)
    }

    // The managed stand-in for C's undefined behaviour when a `ufbx_thread_pool_context` does
    // not name a live pool (ufbx.c:32986/32991/32997 dereference it unconditionally). Like
    // `UfbxiReaderNotPortedException` it is deliberately NOT a `UfbxParseError`: C has no such
    // failure, so it must never become a `ufbx_error`.
    internal sealed class UfbxiThreadContextException : Exception
    {
        public readonly string Function;

        public UfbxiThreadContextException(string function)
            : base("ufbx_thread_pool: invalid context in " + function)
        {
            Function = function;
        }
    }

    // C: struct ufbxi_task_group (ufbx.c:5983-5986)
    internal struct UfbxiTaskGroup
    {
        public uint MaxIndex;
        public uint WaitIndex;
    }

    internal sealed class UfbxiThreadPool
    {
        // C: UFBX_THREAD_GROUP_COUNT (ufbx.h:170)
        internal const uint GroupCount = 4;

        // ------------------------------------------------------------------
        // Context registry (`ctx`)
        // ------------------------------------------------------------------

        // Every live pool gets one non-zero handle; C's equivalent is its own address. Freed at
        // `Free()`, after which the context is unresolvable — the same "use after free" C has.
        static long s_nextCtx = 1;
        static readonly ConcurrentDictionary<nint, UfbxiThreadPool> s_ctxs =
            new ConcurrentDictionary<nint, UfbxiThreadPool>();

        // The value handed to `run_fn` / `wait_fn` / `init_fn` / `free_fn` as C's
        // `(ufbx_thread_pool_context)pool` (ufbx.c:6081/6122/6036/6104).
        public nint Ctx { get; private set; }

        internal static UfbxiThreadPool FromCtx(nint ctx)
        {
            UfbxiThreadPool pool;
            if (ctx == 0 || !s_ctxs.TryGetValue(ctx, out pool)) return null;
            return pool;
        }

        // ------------------------------------------------------------------
        // State (C: struct ufbxi_thread_pool, ufbx.c:5988-6007)
        // ------------------------------------------------------------------

        UfbxThreadOpts _opts;      // C: ufbx_thread_opts opts (value copy, ufbx.c:5989/6077)
        UfbxError _error;          // C: ufbx_error *error (5991, set at 6086)
        readonly Dictionary<uint, UfbxiTask> _slots = new Dictionary<uint, UfbxiTask>();

        public bool Enabled;       // C: bool enabled (5993)
        public bool Failed;        // C: bool failed (5994)
        public string ErrorDesc;   // C: const char *error_desc (5995)
        public uint StartIndex;    // C: uint32_t start_index (5997)
        public uint ExecuteIndex;  // C: uint32_t execute_index (5998)
        public uint WaitIndex;     // C: uint32_t wait_index (5999)
        public uint Group;         // C: uint32_t group (6002)
        public uint NumTasks;      // C: uint32_t num_tasks (6005)
        public object UserPtr;     // C: void *user_ptr (5992)

        public readonly UfbxiTaskGroup[] Groups = new UfbxiTaskGroup[GroupCount];

        // ------------------------------------------------------------------
        // Ring access
        // ------------------------------------------------------------------

        // C: `&pool->tasks[index % pool->num_tasks]` (6009-6011 etc.). Slots are created on
        // demand, which stands in for C's single `ufbxi_alloc(ator, ufbxi_task_imp, num_tasks)`
        // (6088): a slot that does not exist yet is by construction one with `index < num_tasks`,
        // exactly when C's ring would still be zeroed.
        UfbxiTask Slot(uint index)
        {
            uint slot = index % NumTasks;
            UfbxiTask task;
            if (!_slots.TryGetValue(slot, out task)) {
                task = new UfbxiTask();
                _slots[slot] = task;
            }
            return task;
        }

        // ------------------------------------------------------------------
        // ufbxi_thread_pool_init (ufbx.c:6067-6089)
        // ------------------------------------------------------------------

        // Returns null when C's early `if (!(opts->pool.run_fn && opts->pool.wait_fn)) return 1;`
        // fires (6070): the pool stays disabled, `num_tasks` stays 0 and every `CreateTask()`
        // declines.
        internal static UfbxiThreadPool Init(UfbxError error, UfbxThreadOpts opts)
        {
            UfbxThreadPool pool = opts == null ? null : opts.Pool;
            if (pool == null || pool.RunFn == null || pool.WaitFn == null) return null;

            UfbxiThreadPool p = new UfbxiThreadPool();
            p._opts = opts;

            uint numTasks = (uint)opts.NumTasks;
            if (numTasks > int.MaxValue) numTasks = int.MaxValue; // C: ufbxi_min_sz(..., INT32_MAX)
            if (numTasks == 0) numTasks = 2048;                   // C: ufbx.c:6072-6074

            // C: `pool->enabled = true` (6071) precedes everything else, including init_fn.
            p.Enabled = true;

            // C: `pool->opts = *opts` (6077) happens before init_fn runs, so the callbacks see a
            // value copy that does not follow later mutations of the caller's struct.
            if (pool.InitFn != null) {
                UfbxThreadPoolInfo info = new UfbxThreadPoolInfo();
                info.MaxConcurrentTasks = numTasks;
                UfbxiFail.CheckNoDesc(pool.InitFn(pool.User, p.Ctx, info),
                    "pool->opts.pool.init_fn(pool->opts.pool.user, pool, &info)");
            }

            p._error = error;
            p.NumTasks = numTasks;
            return p;
        }

        UfbxiThreadPool()
        {
            nint ctx = (nint)System.Threading.Interlocked.Increment(ref s_nextCtx);
            Ctx = ctx;
            s_ctxs[ctx] = this;
        }

        // ------------------------------------------------------------------
        // ufbxi_thread_pool_free (ufbx.c:6093-6108)
        // ------------------------------------------------------------------

        internal void Free()
        {
            if (!Enabled) return;

            for (uint i = 0; i < GroupCount; i++) {
                Group = (Group + 1) % GroupCount;
                // C: ufbxi_ignore(ufbxi_thread_pool_wait_imp(pool, pool->group, false)) (6100):
                // never fails, because cleanup must not report a second error.
                WaitImp(Group, false);
            }

            if (_opts != null && _opts.Pool != null && _opts.Pool.FreeFn != null) {
                _opts.Pool.FreeFn(_opts.Pool.User, Ctx);
            }

            UfbxiThreadPool removed;
            s_ctxs.TryRemove(Ctx, out removed);
            Enabled = false;
        }

        // ------------------------------------------------------------------
        // Ring protocol
        // ------------------------------------------------------------------

        // C: ufbxi_thread_pool_available_tasks (ufbx.c:6110-6113)
        internal uint AvailableTasks()
        {
            return NumTasks - (StartIndex - WaitIndex);
        }

        // C: ufbxi_thread_pool_create_task (ufbx.c:6130-6151). Returns null when the ring is
        // full — including permanently when the pool is disabled (`num_tasks == 0`), which is
        // what makes C's `if (task) ... else <inline>` fallbacks (9095/10650) take the inline
        // path whenever there is no pool.
        internal UfbxiTask CreateTask(UfbxiTaskFn fn)
        {
            uint index = StartIndex;
            if (index - WaitIndex >= NumTasks) {
                // No space left — note C tests this twice (6132-6133); the second test is dead.
                return null;
            } else if (index == int.MaxValue) {
                return null;
            }

            UfbxiTask task = Slot(index);
            if (index < NumTasks) {
                // C: memset(imp, 0, sizeof(ufbxi_task_imp)) (6142-6144) — only on the first lap,
                // so a reused slot keeps whatever error the previous task left in it.
                task.Data = null;
                task.Error = null;
                task.Fn = null;
            }
            task.Fn = fn;
            return task;
        }

        // C: ufbxi_thread_pool_run_task (ufbx.c:6153-6159) — the *static* one the readers call
        // right after filling `task->data`. It only advances `start_index`; the assert that
        // `task` is the ring slot is not modelled (PORTING_NOTES.md: C asserts are no-ops).
        internal void RunTask(UfbxiTask task)
        {
            uint index = StartIndex;
            StartIndex = index + 1;
        }

        // C: ufbxi_thread_pool_flush_group (ufbx.c:6115-6128)
        internal void FlushGroup()
        {
            uint group = Group;
            uint startIndex = ExecuteIndex;
            uint count = StartIndex - startIndex;
            if (count > 0) {
                UfbxThreadPool pool = _opts == null ? null : _opts.Pool;
                if (pool != null && pool.RunFn != null) {
                    pool.RunFn(pool.User, Ctx, group, startIndex, count);
                }
                Groups[group].MaxIndex = startIndex + count;
                ExecuteIndex = startIndex + count;
            }
            Group = (group + 1) % GroupCount;
        }

        // C: ufbxi_thread_pool_update_finished (ufbx.c:6019-6029)
        void UpdateFinished(uint maxIndex)
        {
            while (WaitIndex < maxIndex) {
                UfbxiTask task = Slot(WaitIndex);
                if (!Failed && task.Error != null) {
                    Failed = true;
                    ErrorDesc = task.Error;
                }
                WaitIndex += 1;
            }
        }

        // C: ufbxi_thread_pool_wait_imp (ufbx.c:6031-6050). Returns false when the wait failed
        // and `can_fail`; nothing else inspects the return value.
        internal bool WaitImp(uint group, bool canFail)
        {
            uint maxIndex = Groups[group].MaxIndex;

            if (Groups[group].WaitIndex < maxIndex) {
                UfbxThreadPool pool = _opts == null ? null : _opts.Pool;
                if (pool != null && pool.WaitFn != null) {
                    pool.WaitFn(pool.User, Ctx, group, maxIndex);
                }
                Groups[group].WaitIndex = maxIndex;
            }
            UpdateFinished(maxIndex);

            if (Failed && canFail) {
                UfbxError error = _error;
                if (ErrorDesc != null) {
                    // C: ufbx.c:6043-6046 — the description is copied in verbatim, "" included.
                    // The "" case needs `null`/empty to survive into `ufbxi_fix_error_type()`,
                    // which is what `UfbxError.Description` empty means in this port.
                    if (error != null) error.Description = ErrorDesc;
                }
                // C: ufbxi_fail_err(error, "Task failed") (6047) — the no-message form, so no
                // description of its own is written.
                return false;
            }
            return true;
        }

        // C: ufbxi_thread_pool_wait_group (ufbx.c:6052-6056)
        internal void WaitGroup()
        {
            if (!WaitImp(Group, true)) ThrowTaskFailed();
        }

        // C: ufbxi_thread_pool_wait_all (ufbx.c:6058-6065)
        internal void WaitAll()
        {
            for (uint i = 0; i < GroupCount; i++) {
                if (!WaitImp(Group, true)) ThrowTaskFailed();
                Group = (Group + 1) % GroupCount;
            }
        }

        static void ThrowTaskFailed()
        {
            UfbxiFail.FailNoDesc("Task failed");
        }

        // ------------------------------------------------------------------
        // ufbxi_thread_pool_execute (ufbx.c:6009-6017) — the body of the public
        // `ufbx_thread_pool_run_task()`.
        // ------------------------------------------------------------------

        internal void Execute(uint index)
        {
            UfbxiTask task = Slot(index);
            // Port-only guard: C calls `imp->fn(&imp->task)` unconditionally (6013), so a slot
            // whose task was never created is a NULL call — undefined behaviour, not an error
            // site. Nothing in ufbx can produce that from a public entry point.
            UfbxiFail.CheckNoDesc(task.Fn != null, "imp->fn");
            if (task.Fn(task)) {
                task.Error = null;
            } else if (task.Error == null) {
                task.Error = string.Empty;
            }
        }
    }
}
