# 交接指令：ufbx-cs 移植 —— 批 M（thread pool 公开 ABI 族，最后 3 个）

> **状态：已完成（2026-10-04）。** 结论见 `COORDINATION.md` 最后一节
> 「更新（线程池线会话 10，2026-10-04）」与 `PORTING_NOTES.md` 规则 14（135 行起）。
> 结果：`tools/PoolCheck` **1688 条 0 分歧**（21 变体 × 8 文件）；变异 39 例
> （29 咬 / 10 按设计不咬 / 0 ERROR / 0 RESTORE FAILED，17 个锚点全部 `cmp` 还原）；
> 电池与批 L 基线逐项一致（`tools/_scratch/battery_after_M.txt`，goldens 2179/2179，
> `streamcheck 2243020 / 3 failed` 仍是另一条线的既存问题）。
> ABI 账本 111/114 → **114/114：公开 ABI 全部有对应体，归零**。
> **下一波：`tools/_scratch/HANDOFF_batch_N.md`（`ufbxi_evaluate_skinning` 求值体）。
> 本文保留作历史记录。**
>
> 交接时点：2026-10-04（批 L 收尾后）。本文自带全部上下文，不依赖任何历史会话。
> 任务台账：`#20 批 M：移植 thread pool 公开 ABI 族（3 个）`（`#19 批 L` 已完成，见本文第 1 节）。

## 0. 总纲（不可改）

- 长期目标（用户原话）：**「继续移植，目的全部移植完成」** —— 把 `C:/Workspace/_analyze_ufbx/ufbx.{c,h}` v0.23.1 的**全部公开 ABI** 移植到纯 C# 工程 `C:\Workspace\ufbx-cs\src\Ufbx.NET`。
- **只读** `C:/Workspace/_analyze_ufbx`（共享参考树），任何插桩都在私有副本 `tools/_scratch/ufbx_dbg/` 做。
- 每一波交付物 = **C oracle + C# 差分 harness + 变异对照（mutation controls）**，变异结束必须用 `cmp` 证明文件逐字节还原。
- 盲区要分类：**equivalent-by-construction（等价于构造，论证写进 PORTING_NOTES）** 还是 **corpus/harness gap（语料或测具缺口，要补）**。
- 收尾在 `COORDINATION.md` 追加一节 + 更新 ABI 账本；必要时在 `PORTING_NOTES.md` 加编号规则。
- 安全线：不提交任何可能含密钥的文件（.env / credentials 等）；不可逆或影响共享状态的操作先问用户；不删别人的在途文件（挪进 `tools/_scratch/`）。
- Oracle 编译命令（唯一被认可的配置）：

  ```
  zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx
  ```

  ⚠️ 批 L 交接文档里写的是 `-mcpu=x86-64`，**那是笔误** —— zig 只认 `x86_64`，其余 oracle 文件头都是后者。
  触及 sin/cos/atan2/pow 的 oracle 追加 `-DUFBX_EXTERNAL_MATH` + `C:/Workspace/_analyze_ufbx/extra/ufbx_math.c`。
- C# 构建：`dotnet build ufbx.net.sln -c Release`。回归总闸：`bash tools/_scratch/battery.sh`。
- ⚠️ **`perl` 已不在 PATH 上**，批 J/K 的 `tools/_mut_bake.sh` / `tools/_mut_createanim.sh` 现在会静默失败。
  变异脚本请照抄 **`tools/_mut_stream.sh`**（用 `python` 做**字节级**替换 + `cmp` 还原证明）；
  锚点备份放 `tools/_scratch/<basename>.bak`（现有：Bake/CreateAnim/Evaluate/SceneBuild/SceneUpdate/
  UfbxBakeApi/StreamOpen/InputStreams/Load/Ascii/UfbxApi 共 11 个）。

## 1. 批 L 结果摘要（本波起点）

批 L（`stream / stdio / open` 9 个 ABI）已收口，完整记录在 `COORDINATION.md` 最后一节
「更新（流/stdio 线会话 9，2026-10-04）」与 `PORTING_NOTES.md` 规则 13。**先读这两处**，再往下做。

- `tools/StreamCheck`：**records 11395 / input 2808 / mismatches 0 — ALL MATCH**（78 变体 × 9 文件）。
- 变异 31 例：25 例咬住、6 例按设计不咬（全部归为"按构造等价"或"被另一条规则掩盖"）、0 例 RESTORE FAILED。
- 批 L 修掉的三处真实缺陷（差分逼出来的，不是重写）：`UfbxiStream.ProgressCb` 从未接线（进度回调一次不发）、
  `ufbxi_fopen()` 的内嵌 NUL 未按 `_wfopen_s` 截断、`ufbxi_ascii_refill()` 把 IO 错误当成硬失败（C 是 `return '\0'` = EOF）。

## 2. 当前基线（接手后先复现，确认没退化）

跑 `bash tools/_scratch/battery.sh`，逐条比对（存 `tools/_scratch/battery_after_L.txt` 的是批 L 收尾值）：

- `goldens: 2179 files, 2179 matched, 0 mismatched, 0 load-errors`
- `streamcheck 2243020 / 3 failed` ← **这 3 条 ErrorType 失败是另一条线的既存问题，不属于你**，不要"顺手修"。
- `StreamCheck: records 11395 input 2808 mismatches 0`
- `CreateAnimCheck: records 13676 input 8086 mismatches 0`
- `BakeCheck: records 50261 input(T) 29280 mismatches 0`
- `s4b 12091 行 / 0`；`s4c 332068`；`S3BC 1477 PASS`；mathvec 72000；util 7003；inflate 4613；
  dom/graph 691；load 0 分歧；s2geom 0；s3scene 13546；hash 79；animcurve 8000；ascii 1737；numeric 280169；s4a 1086。
- ABI 账本（人工核定，`tools/_scratch/abi_scan.sh` 的自动扫描不可信：它先抓到返回类型、再用 PascalCase 猜名，
  会对不上 `ufbx_as_mesh`→`Api/UfbxAs.cs:23`、`ufbx_quat_to_euler`→`Math/Types.cs:276`、`ufbx_dom_find*`→
  `Model/UfbxDomApi.cs:134/148`、`ufbx_evaluate_transform*`→`Parse/Evaluate.cs:904/1010`、`ufbx_load_file_len`→
  `Api/UfbxApi.cs:70`）：**111/114 已有公开对应，剩 3 个 = 线程池（本批）**。

## 3. 本批任务：3 个公开 ABI

`ufbx_thread_pool_run_task`、`ufbx_thread_pool_set_user_ptr`、`ufbx_thread_pool_get_user_ptr`。
三个都标 `ufbx_unsafe`（`ufbx.h:5747/5751/5752`），因为它们唯一的入口参数 `ufbx_thread_pool_context ctx`
（`typedef uintptr_t`，`ufbx.h:4614`）是**不透明的句柄**：C 直接把它转型成内部 `ufbxi_thread_pool*` 并解引用。

C 侧权威行号（照这个读，别自己找）：

| C 语义 | ufbx.c |
|---|---|
| `ufbxi_thread_pool_run_task` | 32984-32987 |
| `ufbxi_thread_pool_set_user_ptr` | 32989-32993 |
| `ufbxi_thread_pool_get_user_ptr` | 32995-32999 |
| `ufbxi_thread_pool` 结构 | 5988-6007（`user_ptr` 字段在此） |
| `ufbxi_thread_pool_execute`（run_task 的本体） | 6009-6017 |
| `ufbxi_thread_pool_update_finished` | 6019+ |
| `ufbxi_thread_pool_init` | 6067-6089 |
| `ufbxi_task` / `ufbxi_task_imp` / `ufbxi_task_group` | 5973-5986 |

`ufbx.h` 侧：`ufbx_thread_pool_context` 4614、`ufbx_thread_pool_info` 4617-4619、`init_fn/run_fn/wait_fn/free_fn`
4623/4629/4633/4636、`ufbx_thread_pool` 4650-4656（**run_fn 与 wait_fn 必需**，init/free 可选）、
`ufbx_thread_opts` 4663（`ufbx_thread_pool pool` 内嵌）。

### 三个函数的 C 本体（就这么多）

```c
ufbx_abi void ufbx_thread_pool_run_task(ufbx_thread_pool_context ctx, uint32_t index) {
	ufbxi_thread_pool_execute((ufbxi_thread_pool*)ctx, index);          // 32986
}
ufbx_abi void ufbx_thread_pool_set_user_ptr(ufbx_thread_pool_context ctx, void *user) {
	ufbxi_thread_pool *pool = (ufbxi_thread_pool*)ctx; pool->user_ptr = user;   // 32991-32992
}
ufbx_abi void *ufbx_thread_pool_get_user_ptr(ufbx_thread_pool_context ctx) {
	ufbxi_thread_pool *pool = (ufbxi_thread_pool*)ctx; return pool->user_ptr;   // 32997-32998
}
```

```c
static void ufbxi_thread_pool_execute(ufbxi_thread_pool *pool, uint32_t index) {   // 6009-6017
	ufbxi_task_imp *imp = &pool->tasks[index % pool->num_tasks];
	if (imp->fn(&imp->task)) imp->task.error = NULL;
	else if (!imp->task.error) imp->task.error = "";
}
```

### 关键语义陷阱（差分必须能钉住）

1. **`ctx` 是 `uintptr_t` 句柄，不是对象引用**。C 无条件转型解引用 ⇒ **`ctx == 0` 是空指针解引用（UB，不可测）**，
   差分只能传"由 ufbx 自己给出的" ctx（即 `init_fn`/`run_fn` 回调收到的那个 `ctx` 实参）。
   这与批 L 的 `ufbx_open_file_context` 是同一类问题（PORTING_NOTES #4），但那里 ctx 只是父分配器可以安全地传 NULL，
   **这里不行** —— 任何合成值都是野写。
2. **`run_task` 的 `index` 取模**：`index % pool->num_tasks`，不是 `index` 本身。
   `num_tasks = opts.thread_opts.num_tasks`，默认 **2048**（端口 `Load.cs:729-730` 已照抄）。
3. **`run_task` 的副作用是"把某个 task 跑完并清/设 `task.error`"**：成功 ⇒ `error = NULL`；
   失败且原本 `error == NULL` ⇒ `error = ""`（空串而非 NULL）。**这是本批唯一真正可观测的语义**，
   而且它只在"任务失败"时才看得到差别（成功路径 error 本来就是 NULL）。
4. **`set/get_user_ptr` 是纯存取**：`get` 未设置时返回 NULL（`ufbx.h:5750`）。`user_ptr` 是 `ufbxi_thread_pool`
   的一个字段，与 `ufbx_thread_pool.user` **不是同一个东西** —— 后者是调用方给回调的 `void *user`，
   前者是任务线程用来存自己状态的槽位（`ufbx.h:4613` 的 HINT）。
5. **端口现状**：`Model/Opts/RuntimeOpts.cs:229-262` 已有 `UfbxThreadPoolInfo` / 四个委托 /
   `UfbxThreadPool`；`Parse/Load.cs:718-741` 的 `ThreadPoolInit` 已实现"只有 run_fn 且 wait_fn 都给了才启用"、
   `num_tasks` 默认 2048、`init_fn` 返回 false 即失败；`Parse/Root.cs:313` 已按 `ThreadPoolEnabled`
   走 `ReadObjectsThreaded`（退化为顺序遍历，PORTING_NOTES #7）。
   ⇒ **本批不是从零写**，是把"退化实现"升级成"真的有 task 数组 + 真的能 execute"，并补三个公开入口。
6. 端口没有 `ufbxi_thread_pool` 实例可给 ⇒ `ctx` 在端口侧必须有一个**真实对象**支撑。
   建议：`UfbxiThreadPool`（internal sealed）持有 `tasks[]`、`numTasks`、`userPtr`；`ctx` 用
   `nint`，由 `GCHandle` 或 `UfbxiPtrIdTable`（见 PORTING_NOTES「C 指针序」与 `UfbxiPtrIdTable.IdOf`）
   映射到实例 —— **后者是本项目已有的指针身份机制，优先复用**（规则 12：C 的指针身份 ≡ 引用别名）。

## 4. 下一步（按序）

1. **先复现第 2 节基线**，确认批 L 没有留下退化。
2. 读 `COORDINATION.md` 批 L 一节 + `PORTING_NOTES.md` 规则 13（流族）与 #4/#7/#8/规则 12，
   读 `src/Ufbx.NET/Parse/Load.cs:718-741`、`src/Ufbx.NET/Parse/Root.cs:300-330`、`src/Ufbx.NET/Model/Opts/RuntimeOpts.cs:217-266`、
   `src/Ufbx.NET/Parse/ReadElement.cs:560-575`，**确认现有退化实现是否已经正确**，再动手。
3. 写 `tools/pool_oracle.c`（zig cc 按第 0 节唯一配置编译）+ `tools/pool_corpus.txt` + `tools/PoolCheck/`
   （C# 差分 harness，隔离 csproj 照抄 `tools/StreamCheck/StreamCheck.csproj`），
   在 `tools/_scratch/battery.sh` 加一行 `run pool ...`（**oracle/corpus 绝对路径都要传全** —— s3bc 那行曾因漏传 oracle 而 `exit=2`）。
   迭代到 **0 mismatches**。
4. 变异对照 ~25-30 例（含 inert `Q_*` 对照组），每例 `cmp` 还原证明，盲区分类写清。
5. 跑 `tools/_scratch/battery.sh`，与第 2 节基线逐条比对（`streamcheck` 那 3 条既存失败除外）。
6. 登记：`COORDINATION.md` 新节（批 M），ABI 账本 **111/114 → 114/114**；
   若变异结论支持，加一条 `PORTING_NOTES.md` 规则。
7. 更新本文（或改写为批 N 交接），并把项目记忆 `project-public-facade-wave.md` / `project-wave-status.md` 同步。

### 差分测具设计建议（可改，但要说明理由）

能观测的只有三条：`set/get` 的往返、`get` 未设置时为 NULL、`run_task` 之后 task 的 error 状态。
建议 oracle 侧：

- 用**真的** `ufbx_thread_opts`（`num_tasks` 取 1/4/8/2048，`memory_limit` 给足）加载一个语料文件，
  在 `init_fn`/`run_fn`/`wait_fn`/`free_fn` 里记录 (group, start_index, count)，并在 `run_fn` 里
  回调 `ufbx_thread_pool_run_task(ctx, i)` —— C 的文档示例（`ufbx.h:4641-4647`）就是这么用的。
- 记录文法自描述（oracle 自带变体表，像批 L 的 `I` 族 / 批 K 的 `I/Ia/Iw/Ip/It`），
  端口侧从记录重建输入，**不要跨语言镜像变体清单**。
- 至少要有的记录：`S`（两侧输入一致）、`A`（入口返回值/是否抛）、`G`（`get_user_ptr` 往返，含未设置时）、
  `E`（`run_task` 之后场景是否仍加载成功 + `ufbxt_hash_scene()` 端到端哈希）。
- **并发顺序不可复现**，所以 `run_fn` 的记录要按 `(group, start_index, count)` **排序后**再摘要，
  或者干脆只记聚合量（总次数、index 集合的 FNV）—— 别把线程调度当可观测。

**明确不测**（C 侧 UB / 不可表达）：`ctx == 0` 或任意合成值（野写）、越界 `index`（`index % num_tasks` 之后
仍在数组内，但语料 task 数不足时的越界）、真正的并行交错顺序、`ufbx_thread_pool_free_fn` 之后的再访问、
`UFBX_NO_THREADS` / `UFBXI_THREAD_SAFE == 0` 的分支。

## 5. 之后的波次（总纲下继续）

**114/114 之后就没有"未移植的公开 ABI"了**，剩下的是覆盖深度，不是门面：

1. **skinning 求值体**：`ufbx_skin_*` / `ufbx_blend_*` 与 `ufbxi_evaluate_skinning`
   （当前 `Parse/EvaluateScene.cs` 在 `Opts.EvaluateSkinning` 为真时抛 `UfbxiReaderNotPortedException`，
   所以批 K 的 `V` 维度与所有 golden 对拍都只能传 NULL evaluate opts，走 `evaluate_skinning = false` 子图）。
   **这是最大的一块欠账**：补上之后 goldens 才能从"同一子图"升级成"全子图"。
2. 批 J 遗留覆盖缺口：`pivot_handling` 载入选项维度、"动画区间严格内嵌于 bake 区间"的语料、
   animated scale-helper 子节点、180° 翻转的连续四元数。
3. 批 L 遗留（已按"按构造等价"登记，不必补语料）：过量供给的 `read`、`ufbx_open_memory` 的二次 `Close`
   （C 是 double free）、`stream == NULL`、分配失败、非 `_WIN32` 的 `fopen` 分支、`UFBX_NO_STDIO`。

## 6. 上手第一件事

读 `COORDINATION.md` 最新一节（批 L）+ `PORTING_NOTES.md` 规则 13 与 #3/#4/#7/#8/规则 12，
读 `src/Ufbx.NET/Parse/Load.cs:718-741`、`src/Ufbx.NET/Parse/Root.cs:300-330`、
`src/Ufbx.NET/Model/Opts/RuntimeOpts.cs:217-266`、`src/Ufbx.NET/Parse/ReadElement.cs:560-575`，
然后从第 4 节第 1 步开始。**不要重写已有实现**，先确认它是否已经正确。
