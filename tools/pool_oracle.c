// Batch M oracle: the thread pool public ABI family (`ufbx_thread_pool_run_task`,
// `_set_user_ptr`, `_get_user_ptr`) and everything those three can observe.
//
// Built with `#include "ufbx.c"`, so it runs the original
//   struct ufbxi_task / ufbxi_task_imp / ufbxi_task_group / ufbxi_thread_pool  (ufbx.c:5973-6007)
//   ufbxi_thread_pool_execute                                                  (6009-6017)
//   ufbxi_thread_pool_update_finished                                          (6019-6029)
//   ufbxi_thread_pool_wait_imp / _wait_group / _wait_all                       (6031-6065)
//   ufbxi_thread_pool_init / _free / _available_tasks / _flush_group           (6067-6128)
//   ufbxi_thread_pool_create_task / _run_task                                  (6130-6159)
//   ufbxi_deflate_task_fn + its call site in the binary reader                 (8912-8956, 9090-9131)
//   ufbxi_ascii_array_task_fn + its call site in the ASCII reader              (10145-10153, 10565-10657)
//   ufbxi_read_objects_threaded                                                (15132-15237)
//   ufbx_thread_pool_run_task / _set_user_ptr / _get_user_ptr                  (32984-32999)
// The C# twin is src/Ufbx.NET/Parse/ThreadPool.cs + src/Ufbx.NET/Parse/Objects.cs +
// src/Ufbx.NET/Parse/{DomNode,AsciiDomNode}.cs + src/Ufbx.NET/Api/UfbxApi.cs, replayed by tools/PoolCheck.
//
// Compile (the only recognised configuration, see HANDOFF_batch_M.md section 0):
//   zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH \
//       -I C:/Workspace/_analyze_ufbx pool_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c \
//       -o pool_oracle.exe
// Run from C:/Workspace/_analyze_ufbx with `--list <corpus>`.
//
// DESIGN: the variant table lives *only* here. Each variant re-emits what it fed to the loader as
// `I` records, so the harness rebuilds the same payload and thread options from those records
// instead of mirroring `k_variants[]` in a second language (same pattern as batch L's `I` family).
//
// The pool below is *deterministic*: every callback runs its work inline, on the thread that
// called it. That is one legal implementation of the contract documented at ufbx.h:4623-4647 --
// nothing requires `run_fn` to return before the tasks are done, only that `wait_fn` may assume
// every index below `max_index` has *started* (`ufbxi_thread_pool_wait_imp`, ufbx.c:6031-6050).
// Concurrency order is therefore not observable here, and must not be: the differential compares
// the callback argument streams, which are fully determined by the *loader's* task pattern.
//
// Grammar (space separated, one record per line; `<z>` = FNV-1a-64 lowercase hex, `<x>` = lowercase
// hex of raw bytes, an empty byte run is the single token `-`):
//   I <fi> <vi> <numTasks> <memLimit> <poolMode> <initMode> <runMode> <freeMode> <mutate>
//           input description of the variant (INPUT ONLY)
//   S <fi> <vi> <len> <z>          the payload bytes fed to ufbx_load_memory: proves the two sides
//                                  agree about the INPUT
//   B <fi> <vi> <seq> <kind> <a> <b> <c>
//           one callback invocation, in call order. kind: 0 init_fn (a = max_concurrent_tasks),
//           1 run_fn (a = group, b = start_index, c = count), 2 wait_fn (a = group,
//           b = max_index), 3 free_fn.
//   U <fi> <vi> <seq> <phase> <token>
//           one `ufbx_thread_pool_get_user_ptr()` observation, in call order. phase: 0 inside
//           init_fn *before* set, 1 inside init_fn *after* set, 2 inside run_fn, 3 inside
//           wait_fn, 4 inside free_fn. token: 0 = NULL, 1 = the sentinel this oracle stored,
//           2 = anything else (both a broken round trip and a bogus context land here).
//   A <fi> <vi> <ok> <type> <dlen> <dx> <ilen> <ix>
//           ufbx_load_memory()'s result and the reported `ufbx_error`, byte for byte: this is
//           what separates a task failure ("Bad DEFLATE data" / "Threaded ASCII parse error") from
//           the "Failed to load" default `ufbxi_fix_error_type()` substitutes (ufbx.c:25622).
//   E <fi> <vi> <ok> <z>
//           end to end: the golden generator's own `ufbxt_hash_scene()` of the loaded scene.
//
// NOT COVERED (undefined behaviour / not representable, see PORTING_NOTES.md): a context that
// ufbx did not hand out (`ctx == 0` or any synthetic value is a dereference of a wild pointer,
// ufbx.c:32986), a context used after `free_fn` ran, real concurrent interleaving, and the
// `num_tasks`-independent memory limit test at ufbx.c:15192.

#if !defined(UFBX_EXTERNAL_MATH)
#define UFBX_EXTERNAL_MATH
#endif
#include "ufbx.c"

// The golden generator's own scene hasher, included exactly as `test/hash_scene.c:6` does.
#include "test/hash_scene.h"

// ------------------------------------------------------------------
// Output helpers
// ------------------------------------------------------------------

static const uint64_t FNV_BASIS = UINT64_C(0xcbf29ce484222325);
static const uint64_t FNV_PRIME = UINT64_C(0x100000001b3);

static uint64_t hh_bytes(uint64_t h, const void *data, size_t size)
{
	const uint8_t *p = (const uint8_t*) data;
	for (size_t i = 0; i < size; i++) {
		h ^= (uint64_t) p[i];
		h *= FNV_PRIME;
	}
	return h;
}

static void pz(uint64_t v) { printf(" %016llx", (unsigned long long) v); }

static void phex(const char *data, size_t length)
{
	printf(" %zu ", length);
	if (length == 0) { printf("-"); return; }
	if (!data) { printf("null"); return; }
	for (size_t i = 0; i < length; i++) printf("%02x", (unsigned char) data[i]);
}

// ------------------------------------------------------------------
// Payload
// ------------------------------------------------------------------

typedef struct {
	uint8_t *data;
	size_t size;
} blob;

static FILE *fopen_utf8(const char *path, size_t path_len)
{
	ufbxi_file_context fc; // ufbxi_uninit
	ufbxi_begin_file_context(&fc, (ufbx_open_file_context) NULL, NULL);
	FILE *f = ufbxi_fopen(&fc, path, path_len, true);
	ufbxi_end_file_context(&fc, NULL, f != NULL);
	return f;
}

static blob read_whole(const char *path, size_t path_len)
{
	blob b = { 0, 0 };
	FILE *f = fopen_utf8(path, path_len);
	if (!f) return b;
	if (fseek(f, 0, SEEK_END) == 0) {
		long n = ftell(f);
		if (n > 0) {
			b.size = (size_t) n;
			b.data = (uint8_t*) malloc(b.size + 1);
			rewind(f);
			size_t got = fread(b.data, 1, b.size, f);
			b.size = got;
			if (b.data) b.data[b.size] = 0;
		}
	}
	fclose(f);
	return b;
}

// ------------------------------------------------------------------
// Variant table
// ------------------------------------------------------------------

enum {
	// What makes it into `ufbx_thread_opts.pool`
	POOL_FULL = 0,   // init_fn? + run_fn + wait_fn + free_fn?  (per initMode/freeMode)
	POOL_NO_RUN,     // wait_fn only: `ufbxi_thread_pool_init()` returns early (ufbx.c:6070)
	POOL_NO_WAIT,    // run_fn only: same early return, so nothing is threaded
	POOL_NONE,       // no pool at all: the ordinary sequential load, no callback ever fires
};

enum {
	INIT_NONE = 0,   // `init_fn == NULL`
	INIT_SET,        // init_fn: observe the unset slot, store the sentinel, observe it back
	INIT_FAIL,       // init_fn: observe, store the sentinel, return false -> the load fails
};

enum {
	RUN_EXEC = 0,    // for i in [start, start+count): ufbx_thread_pool_run_task(ctx, i)
	RUN_NONE,        // return without running anything: the arrays stay as the arena left them
	RUN_MOD,         // run_task(ctx, i + num_tasks): the same slots through `index % num_tasks`
	RUN_REVERSE,     // run the range back to front
	RUN_TWICE,       // run every index twice
	RUN_HALF,        // run only the first index of the range
};

enum {
	FREE_NONE = 0,
	FREE_GET,        // free_fn observes the slot once more before returning
};

enum {
	MUT_NONE = 0,
	MUT_TRUNC,       // cut the payload in half: exercises the truncated read paths
	MUT_BYTEFLIP,    // flip a byte in the middle of the file
};

typedef struct {
	size_t num_tasks;
	size_t mem_limit;
	int pool_mode;
	int init_mode;
	int run_mode;
	int free_mode;
	int mutate;
} variant;

#define V(numTasks, memLimit, pool, init, run, freeFn, mut) \
	{ (numTasks), (memLimit), (pool), (init), (run), (freeFn), (mut) }

// NOTE ON `RUN_NONE`: a `run_fn` that returns without executing anything is legal, but what the
// scene then contains is whatever the *result arena* happened to hold for those array bytes
// (`ufbxi_push_array_data()`, ufbx.c:8867) — uninitialised memory, not zeros. That is
// unobservable in principle (PORTING_NOTES.md #4) and measurably non-deterministic in practice:
// with `data/blender_293_barbarian_7400_binary.fbx` two runs of this oracle produce different
// `E` hashes. The differential therefore covers "the pool declines" (no run_fn / no wait_fn,
// which ufbx answers by not threading at all) rather than "the user declines to work".
static const variant k_variants[] = {
	// -- No pool: the baseline every threaded variant must agree with.
	V(0,      1073741824, POOL_NONE,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 0 */
	V(2048,   1073741824, POOL_NO_RUN,  INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 1 */
	V(2048,   1073741824, POOL_NO_WAIT, INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 2 */

	// -- Enabled pools: the documented usage (ufbx.h:4641-4647) at several ring sizes. The batch
	// bound is `num_tasks / UFBX_THREAD_GROUP_COUNT` (ufbx.c:15191), so a small ring is what makes
	// more than one `run_fn` call happen.
	V(0,      1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 3 */
	V(2048,   1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 4 */
	V(64,     1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 5 */
	V(8,      1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 6 */
	V(4,      1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 7 */
	V(2,      1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 8 */
	V(1,      1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 9 */
	// Allocatable by C (`sizeof(ufbxi_task_imp) * 65536`), unlike `INT32_MAX` -- which runs into
	// the allocation failure this port does not model (PORTING_NOTES.md #4) and reports
	// UFBX_ERROR_OUT_OF_MEMORY with info "temp" (ufbx.c:6088). `ufbxi_min_sz(num_tasks, INT32_MAX)`
	// (ufbx.c:6071) is therefore not reachable either: anything beyond ~2^31 is how you get there.
	V(65536,  1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 10 */

	// -- `run_fn` that does something other than the documented sweep. RUN_MOD adds 2048 to the
	// index, which only lands on a *live* slot when `num_tasks` divides 2048 or is 1 -- otherwise
	// `ufbxi_thread_pool_execute()` calls a NULL `imp->fn` (a segfault in C, undefined behaviour,
	// not modelled). That is why the two sizes here are 2048 and 1.
	V(2048,   1073741824, POOL_FULL,    INIT_NONE, RUN_MOD,     FREE_NONE, MUT_NONE),      /* 11 */
	V(1,      1073741824, POOL_FULL,    INIT_NONE, RUN_MOD,     FREE_NONE, MUT_NONE),      /* 12 */
	V(2048,   1073741824, POOL_FULL,    INIT_NONE, RUN_REVERSE, FREE_NONE, MUT_NONE),      /* 13 */
	V(2048,   1073741824, POOL_FULL,    INIT_NONE, RUN_TWICE,   FREE_NONE, MUT_NONE),      /* 14 */

	// -- user_ptr round trip, in every callback that receives a context.
	V(2048,   1073741824, POOL_FULL,    INIT_SET,  RUN_EXEC,    FREE_GET,  MUT_NONE),      /* 15 */
	V(64,     1073741824, POOL_FULL,    INIT_SET,  RUN_EXEC,    FREE_GET,  MUT_NONE),      /* 16 */
	V(2048,   1073741824, POOL_FULL,    INIT_FAIL, RUN_EXEC,    FREE_NONE, MUT_NONE),      /* 17 */

	// -- Task failures (the only place `task->error` becomes observable, ufbx.c:6043-6047).
	V(2048,   1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_BYTEFLIP),  /* 18 */
	V(64,     1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_BYTEFLIP),  /* 19 */
	V(2048,   1073741824, POOL_FULL,    INIT_NONE, RUN_EXEC,    FREE_NONE, MUT_TRUNC),     /* 20 */
};

static const size_t k_num_variants = sizeof(k_variants) / sizeof(k_variants[0]);

// ------------------------------------------------------------------
// The pool
// ------------------------------------------------------------------

static int s_sentinel;

typedef struct {
	int fi, vi;
	int seq;
	int phase;
} log_state;

static log_state s_log;

// The single object ever stored through `ufbx_thread_pool_set_user_ptr()`.
static void *sentinel_ptr(void) { return &s_sentinel; }

static void emit_user_ptr(ufbx_thread_pool_context ctx, int phase)
{
	void *p = ufbx_thread_pool_get_user_ptr(ctx);
	int token = 2;
	if (p == NULL) token = 0;
	else if (p == sentinel_ptr()) token = 1;
	printf("U %d %d %d %d %d\n", s_log.fi, s_log.vi, s_log.seq++, phase, token);
}

static const variant *s_cur;

static bool my_init_fn(void *user, ufbx_thread_pool_context ctx, const ufbx_thread_pool_info *info)
{
	(void) user;
	printf("B %d %d %d 0 %u 0 0\n", s_log.fi, s_log.vi, s_log.seq++, info->max_concurrent_tasks);
	if (s_cur->init_mode == INIT_NONE) return true;

	emit_user_ptr(ctx, 0);
	ufbx_thread_pool_set_user_ptr(ctx, sentinel_ptr());
	emit_user_ptr(ctx, 1);
	return s_cur->init_mode != INIT_FAIL;
}

static void run_range(ufbx_thread_pool_context ctx, uint32_t group, uint32_t start, uint32_t count)
{
	switch (s_cur->run_mode) {
	case RUN_EXEC:
	case RUN_TWICE:
		for (uint32_t i = 0; i < count; i++) {
			ufbx_thread_pool_run_task(ctx, start + i);
			if (s_cur->run_mode == RUN_TWICE) ufbx_thread_pool_run_task(ctx, start + i);
		}
		break;
	case RUN_REVERSE:
		for (uint32_t i = count; i-- > 0; ) ufbx_thread_pool_run_task(ctx, start + i);
		break;
	case RUN_MOD:
		for (uint32_t i = 0; i < count; i++) {
			// `index % num_tasks` (ufbx.c:6011): adding num_tasks changes nothing observable,
			// and with num_tasks == 1 any index at all lands on slot 0.
			ufbx_thread_pool_run_task(ctx, start + i + 2048);
		}
		break;
	case RUN_HALF:
		if (count > 0) ufbx_thread_pool_run_task(ctx, start);
		break;
	case RUN_NONE:
	default:
		break;
	}
}

static void my_run_fn(void *user, ufbx_thread_pool_context ctx, uint32_t group, uint32_t start_index, uint32_t count)
{
	(void) user;
	printf("B %d %d %d 1 %u %u %u\n", s_log.fi, s_log.vi, s_log.seq++, group, start_index, count);
	emit_user_ptr(ctx, 2);
	run_range(ctx, group, start_index, count);
}

static void my_wait_fn(void *user, ufbx_thread_pool_context ctx, uint32_t group, uint32_t max_index)
{
	(void) user;
	printf("B %d %d %d 2 %u %u 0\n", s_log.fi, s_log.vi, s_log.seq++, group, max_index);
	emit_user_ptr(ctx, 3);
}

static void my_free_fn(void *user, ufbx_thread_pool_context ctx)
{
	(void) user;
	printf("B %d %d %d 3 0 0 0\n", s_log.fi, s_log.vi, s_log.seq++);
	if (s_cur->free_mode == FREE_GET) emit_user_ptr(ctx, 4);
}

// ------------------------------------------------------------------
// main
// ------------------------------------------------------------------

int main(int argc, char **argv)
{
	const char *list_path = NULL;
	for (int i = 1; i < argc; i++) {
		if (!strcmp(argv[i], "--list") && i + 1 < argc) list_path = argv[++i];
	}
	if (!list_path) { fprintf(stderr, "usage: pool_oracle --list <corpus.txt>\n"); return 2; }

	// The corpus is read as raw bytes: MSYS re-encodes non-ASCII argv, so filenames must come
	// out of a file (same convention as load_oracle.c / stream_oracle.c).
	blob list = { 0, 0 };
	{
		FILE *f = fopen(list_path, "rb");
		if (!f) { fprintf(stderr, "cannot read corpus list: %s\n", list_path); return 2; }
		fseek(f, 0, SEEK_END);
		long n = ftell(f);
		if (n > 0) {
			list.size = (size_t) n;
			list.data = (uint8_t*) malloc(list.size + 1);
			rewind(f);
			list.size = fread(list.data, 1, list.size, f);
			list.data[list.size] = 0;
		}
		fclose(f);
	}

	const char *paths[64];
	size_t path_lens[64];
	size_t num_paths = 0;
	{
		size_t begin = 0;
		for (size_t i = 0; i <= list.size; i++) {
			if (i < list.size && list.data[i] != '\n') continue;
			size_t end = i;
			if (end > begin && list.data[end - 1] == '\r') end--;
			if (end > begin && list.data[begin] != '#') {
				paths[num_paths] = (const char*) &list.data[begin];
				path_lens[num_paths] = end - begin;
				num_paths++;
				if (num_paths == 64) break;
			}
			begin = i + 1;
		}
	}
	if (num_paths == 0) { fprintf(stderr, "empty corpus\n"); return 2; }

	for (size_t fi = 0; fi < num_paths; fi++) {
		blob file = read_whole(paths[fi], path_lens[fi]);
		if (!file.data || file.size == 0) {
			fprintf(stderr, "cannot read: %.*s\n", (int) path_lens[fi], paths[fi]);
			return 2;
		}

		for (size_t vi = 0; vi < k_num_variants; vi++) {
			const variant *v = &k_variants[vi];
			s_cur = v;
			s_log.fi = (int) fi;
			s_log.vi = (int) vi;
			s_log.seq = 0;
			s_log.phase = 0;

			size_t size = file.size;
			uint8_t *bytes = (uint8_t*) malloc(size + 1);
			memcpy(bytes, file.data, size);
			bytes[size] = 0;
			if (v->mutate == MUT_TRUNC) {
				size = size / 2;
			} else if (v->mutate == MUT_BYTEFLIP) {
				size_t at = size / 2;
				bytes[at] = (uint8_t) (bytes[at] ^ 0xff);
			}

			// Input description first, exactly like every other oracle in this project.
			printf("I %zu %zu %zu %zu %d %d %d %d %d\n", fi, vi, v->num_tasks, v->mem_limit,
				v->pool_mode, v->init_mode, v->run_mode, v->free_mode, v->mutate);
			printf("S %zu %zu %zu", fi, vi, size);
			pz(hh_bytes(FNV_BASIS, bytes, size));
			printf("\n");

			ufbx_load_opts opts;
			memset(&opts, 0, sizeof(opts));
			if (v->pool_mode != POOL_NONE) {
				opts.thread_opts.num_tasks = v->num_tasks;
				opts.thread_opts.memory_limit = v->mem_limit;
				if (v->pool_mode != POOL_NO_WAIT) opts.thread_opts.pool.wait_fn = &my_wait_fn;
				if (v->pool_mode != POOL_NO_RUN) opts.thread_opts.pool.run_fn = &my_run_fn;
				if (v->init_mode != INIT_NONE) opts.thread_opts.pool.init_fn = &my_init_fn;
				if (v->free_mode != FREE_NONE) opts.thread_opts.pool.free_fn = &my_free_fn;
			}

			ufbx_error error;
			memset(&error, 0, sizeof(error));
			ufbx_scene *scene = ufbx_load_memory(bytes, size, &opts, &error);

			printf("A %zu %zu %d %d", fi, vi, scene != NULL ? 1 : 0, (int) error.type);
			phex(error.description.data, error.description.length);
			phex(error.info, error.info_length);
			printf("\n");

			printf("E %zu %zu %d", fi, vi, scene != NULL ? 1 : 0);
			pz(scene != NULL ? ufbxt_hash_scene(scene, NULL) : 0);
			printf("\n");

			ufbx_free_scene(scene);
			free(bytes);
		}

		free(file.data);
	}

	return 0;
}
