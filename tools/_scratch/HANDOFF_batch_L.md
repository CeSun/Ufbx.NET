# 交接指令：ufbx-cs 移植 —— 批 L（stream / stdio / open 公开 ABI 族）

> **状态：已完成（2026-10-04）。** 结论见 `COORDINATION.md` 最后一节
> 「更新（流/stdio 线会话 9，2026-10-04）」与 `PORTING_NOTES.md` 规则 13。
> 结果：`tools/StreamCheck` 11395 条 0 分歧；变异 31 例（25 咬 / 6 按设计不咬 / 0 RESTORE FAILED）；
> 电池与批 K 基线逐项一致。ABI 账本 102/114 → **111/114**。
> **下一波：`tools/_scratch/HANDOFF_batch_M.md`（thread pool，最后 3 个）。本文保留作历史记录。**
>
> 交接时点：2026-10-04。本文自带全部上下文，不依赖任何历史会话。
> 任务台账：`#19 批 L：移植 stream/stdio/open 公开 ABI 族（9 个）`（`#18 批 K` 已完成）。

## 0. 总纲（不可改）

- 长期目标（用户原话）：**「继续移植，目的全部移植完成」** —— 把 `C:/Workspace/_analyze_ufbx/ufbx.{c,h}` v0.23.1 的**全部公开 ABI** 移植到纯 C# 工程 `C:\Workspace\ufbx-cs\src\Ufbx.NET`。
- **只读** `C:/Workspace/_analyze_ufbx`（共享参考树），任何插桩都在私有副本 `tools/_scratch/ufbx_dbg/` 做。
- 每一波交付物 = **C oracle + C# 差分 harness + 变异对照（mutation controls）**，变异结束必须用 `cmp` 证明文件逐字节还原。
- 盲区要分类：**equivalent-by-construction（等价于构造，论证写进 PORTING_NOTES）** 还是 **corpus/harness gap（语料或测具缺口，要补）**。
- 收尾在 `COORDINATION.md` 追加一节 + 更新 ABI 账本；必要时在 `PORTING_NOTES.md` 加编号规则。
- 安全线：不提交任何可能含密钥的文件（.env / credentials 等）；不可逆或影响共享状态的操作先问用户；不删别人的在途文件（挪进 `tools/_scratch/`）。
- Oracle 编译命令（唯一被认可的配置）：

  ```
  zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86-64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx
  ```

  触及 sin/cos/atan2/pow 的 oracle 追加 `-DUFBX_EXTERNAL_MATH` + `C:/Workspace/_analyze_ufbx/extra/ufbx_math.c`。
- C# 构建：`dotnet build ufbx.net.sln -c Release`。回归总闸：`bash tools/_scratch/battery.sh`。

## 1. 当前基线（接手前先复现，确认没退化）

- `CreateAnimCheck: records 13676 input 8086 mismatches 0 / ALL MATCH`
- `BakeCheck: records 50261 input(T) 29280 mismatches 0 / ALL MATCH`
- `goldens: 2179 files, 2179 matched, 0 mismatched, 0 load-errors`
- `S3BC CHECK PASS records checked: 1477`
- `s4b 12091 / 0`，`s4c 332068`
- `streamcheck 2243020 / 3 failed` ← **这 3 条 ErrorType 失败是另一条线的既存问题，不属于你**，不要"顺手修"。
- ABI 账本（人工核定，`tools/_scratch/abi_scan.sh` 的自动扫描不可信：它先抓到返回类型、再用 PascalCase 猜名，会对不上 `ufbx_as_mesh`→`Api/UfbxAs.cs:23`、`ufbx_quat_to_euler`→`Math/Types.cs:276`、`ufbx_dom_find*`→`Model/UfbxDomApi.cs:134/148`、`ufbx_evaluate_transform*`→`Parse/Evaluate.cs:904/1010`、`ufbx_load_file_len`→`Api/UfbxApi.cs:70`）：**102/114 已有公开对应，剩 12 个 = stream/stdio/open 9 个（本批）+ 线程池 3 个（下一波）**。

## 2. 本批任务：9 个公开 ABI

`ufbx_load_stream`、`ufbx_load_stream_prefix`、`ufbx_load_stdio`、`ufbx_load_stdio_prefix`、`ufbx_open_file`、`ufbx_open_file_ctx`、`ufbx_open_memory`、`ufbx_open_memory_ctx`、`ufbx_default_open_file`。

C 侧权威行号（照这个读，别自己找）：

| C 语义 | ufbx.c |
|---|---|
| `ufbxi_file_context` / begin / end | 6941-6946 / 6948-6958 / 6960-6975 |
| `ufbxi_fopen`（只 `_WIN32` 分支） | 6981-7065（转换循环 6996-7031，失败报错 7060-7062） |
| `ufbxi_stdio_read/skip/size/close` | 7082 / 7089 / 7098 / 7120 |
| `ufbxi_stdio_init` / `stdio_open` | 7126-7134 / 7135-7141 |
| `ufbxi_memory_stream` / read/skip/size/close | 7186-7198 / 7200 / 7209 / 7217 / 7223 |
| loader IO：`refill` / `skip_bytes` / `read_to` | 6702-6767 / 6837-6882 / 6884-6920 |
| deferred open（`uc.error = error` 那段） | 25213-25252 |
| load 尾部 close（成功/失败都执行） | 25609-25613 |
| 公开 ABI 主体 | 30414-30584 |

`ufbx.h` 侧：`ufbx_stream` 4155-4163（read 必需，skip/size/close 可选，`void *user`；read 返回 SIZE_MAX 表示 IO 错误，size 返回 0 = 未知 / UINT64_MAX = 错误）、`ufbx_open_file_opts` 4204-4214、`ufbx_close_memory_cb` 4219-4226、`ufbx_open_memory_opts` 4229-4246、ABI 声明 5305-5325 与 5413-5422（`open_file_ctx`/`open_memory_ctx` 标 `ufbx_unsafe`）。

### 关键语义陷阱（差分必须能钉住）

1. `ufbxi_end_file_context`：失败走 `ufbxi_fix_error_type(&fc.error, "Failed to open file", error)`；**成功走 `ufbxi_clear_error(error)`** —— 即打开成功会把调用方那个脏的 `ufbx_error` 清空。
2. `"Failed to open file"` **不在**类型表里 ⇒ 无描述失败时得到 `UNKNOWN` + 该描述。
3. `_WIN32` 分支的 UTF-8→UTF-16 解码器是**故意宽松**的（放行游离代理对，"the Windows file system encoding allows them as well"），坏首字节 ⇒ `"Invalid UTF-8"` 且**不设 info payload**；打开失败才 `set_err_info(path)` + `"File not found"`。`null_terminated` 在该分支被 `(void)` 掉。
4. 因此 `ufbx_load_file` 遇到非法 UTF-8 路径，最终错误类型是 **INVALID_UTF8**（因为 `ufbxi_fail_msg("open_file_fn()","File not found")` 不覆盖已设描述，ufbx.c:3440 + 25238-25245），不是 FILE_NOT_FOUND。
5. `ufbxi_stdio_size` 返回**剩余字节**（并 rewind+fsetpos 清 error/EOF）；`ufbxi_memory_size` 返回**总大小**。两者口径不同，别搞混。
6. `ufbxi_stdio_skip` 的 `fseek` 越过 EOF 也"成功" ⇒ `ufbxi_skip_bytes` 必须做单字节探针读（"Truncated file" / "IO error"）。
7. `ufbx_load_stdio_prefix`：`if (!file_void) return NULL;` **不写任何 error**；`ufbxi_stdio_init(&stream, file_void, false)` ⇒ `close_fn = NULL` ⇒ **不得关闭句柄**（差分要 pin 住 load 后的文件游标位置：C 用 `ftell`，C# 用 `FileStream.Position`）。
8. `ufbx_load_stream_prefix`：`data_begin = data = prefix; data_size = prefix_size`，拷 4 个回调 + `read_user`，**不设 `progress_bytes_total`**（与 `ufbx_load_memory` 不同，后者设为 `size`）。
9. `ufbx_open_memory_ctx`：`copy_size = no_copy ? 0 : data_size`；`no_copy` ⇒ **别名调用方缓冲区**（可观察）；总是接 `close_fn = ufbxi_memory_close`；`close_cb.fn(user, stream->data, stream->size)` 先回调后 free；`opts == NULL` ⇒ **全零 opts**（不是"默认值"）。
10. `ufbx_default_open_file` 无条件解引用 `info->context`，并传 **NULL opts 和 NULL error**（错误被吞，只有 bool）。
11. loader 侧错误口径：`ufbxi_check_msg`（"Empty file"/"Truncated file"/"IO error"/"Cancelled"）带描述；plain `ufbxi_check`（`!uc->eof`、`read_result <= to_read`、`uc->read_fn`、`read_result != 0`）不带描述，落到 `"Failed to load"` 默认串 ⇒ 端口对应 `UfbxiFail.CheckNoDesc`。

## 3. 本批已完成的代码改动（**尚未编译通过**）

- `src/Ufbx.NET/Parse/InputStreams.cs`：`UfbxMemoryInputStream` 加 `UfbxCloseMemoryCb closeCb` 字段 + `(byte[], int, UfbxCloseMemoryCb)` 构造 + `Close()` 里按 C 顺序触发回调（**故意不加 closed 守卫**，C 二次调用是 double free，不可测）；`UfbxFileInputStream` 加 `(FileStream file, bool ownsHandle)` 构造，并说明 `(string path)` 构造收到的是**已解析好的 UTF-16 路径**（原构造直接 `opensFile`，保留）。
- `src/Ufbx.NET/Parse/StreamOpen.cs`（新文件，~240 行，头部列了 6 条不可表达的偏差）：`UfbxiFileContext.Begin/End`、`PathToUtf16`、`Fopen`、`StdioOpen`（internal）、`OpenFileCtx`、`OpenFile`、`DefaultOpenFileEntry`、`OpenMemoryCtx`、`OpenMemory`。
- `src/Ufbx.NET/Parse/Load.cs`：`DefaultOpenFileFn` 与 `OpenFileWithDefault` 改为走 `UfbxiStreamOpen`（顺手修掉"非法 UTF-8 路径被误报成 FILE_NOT_FOUND"这个既存保真缺口）；deferred open 的自定义回调分支改为传**已解析的** `filenameLen`（对齐 C:25239），不再传 `uc.LoadFilenameLen`。
- `src/Ufbx.NET/Api/UfbxApi.cs`：加了 9 个公开入口 `LoadStream / LoadStreamPrefix / LoadStdio / LoadStdioPrefix / DefaultOpenFile / OpenFile / OpenFileCtx / OpenMemory / OpenMemoryCtx`，签名口径：C 的 `ufbx_stream *stream` 出参 ⇒ `out UfbxInputStream`；`ufbx_open_file_context` ⇒ `nint ctx`（收下并忽略，不建模 allocator，PORTING_NOTES #4）；`void *file_void` ⇒ `FileStream`。

## 4. 下一步（按序）

1. **先让它编译**：`UfbxApi.cs` 用到 `FileStream` 但文件里**还没加 `using System.IO;`**（此前 src 只有 `InputStreams.cs` 依赖 `System.IO`，加在 Api 文件里是有意的）。跑 `dotnet build ufbx.net.sln -c Release` 清干净，再跑一遍第 1 节基线确认没退化。
2. 写 `tools/stream_oracle.c`（zig cc 按第 0 节唯一配置编译）+ `tools/stream_corpus.txt` + `tools/StreamCheck/`（C# 差分 harness），并在 `tools/_scratch/battery.sh` 里加一行 `run stream ...`（**注意**：s3bc 那行曾因漏传 oracle 路径而 `exit=2`，新增行要把 oracle/corpus 绝对路径都传全），迭代到 **0 mismatches**。
3. 变异对照 ~26 例（含 inert `Q_*` 对照组），每例 `cmp` 还原证明，盲区分类写清。变异锚点参照 `tools/_scratch/CreateAnim.cs.bak` 的做法。
4. 跑 `tools/_scratch/battery.sh`，与第 1 节基线逐条比对（`streamcheck` 那 3 条既存失败除外）。
5. 登记：`COORDINATION.md` 新节（批 L），ABI 账本 **102/114 → 111/114，剩 3 个（线程池）**；若变异结论支持，加一条 `PORTING_NOTES.md` 规则，候选：**「`ufbxi_end_file_context` 成功即 `clear_error`；失败默认串 "Failed to open file" 不在类型表里 ⇒ UNKNOWN + 该描述」**。
6. 更新 `tools/_scratch/HANDOFF_batch_L.md` 本身（或改写为批 M 交接），并把项目记忆 `project-public-facade-wave.md` / `project-wave-status.md` 同步。

### 差分测具设计（已定，别重新发明）

两侧都用**规则化 stream 包装器**，由 oracle 输出读日志摘要行 `H <fi> <vi> <calls> <FNV digest>`，调用数 ≤64 时输出完整 `Q` 明细行；两种模式 —— **REAL**（用端口自己的 memory/file stream ⇒ 钉住 `InputStreams.cs`）与 **SCRIPT**（端口重放同一规则 ⇒ 钉住 loader 的 IO 时序与错误路径）。记录文法：`S/I/A/M/R/P/K/C/V/H/Q`。oracle 自带变体表（像批 K 的 `I/Ia/Iw/Ip/It`）以免跨语言镜像变体清单。

**明确不测**（C 侧 UB / double free）：供过于求的 read（C 只有溢出后才查 `read_result <= to_read`）、memory stream 二次 Close、`stream == NULL`、alloc 失败、非 `_WIN32` fopen 分支、`UFBX_NO_STDIO`。

## 5. 之后的波次（总纲下继续）

线程池（`ufbx_run_task` / `ufbx_set_user_ptr` / `ufbx_get_user_ptr`）→ skinning 求值体（`ufbx_skin_*` / `ufbx_blend_*`，含 `ufbxi_evaluate_skinning`，当前 `EvaluateScene.cs:381-385` 在 `EvaluateSkinning` 为真时抛异常）→ 批 J 遗留覆盖缺口（`pivot_handling` 载入选项维度、动画区间严格内嵌于 bake 区间的语料、animated scale-helper 子节点、180° 翻转的连续四元数）。

## 6. 上手第一件事

读 `COORDINATION.md` 最新一节（批 K）+ `PORTING_NOTES.md`（尤其 #3 错误口径、#4 allocator 不建模、#8 stream 抽象、规则 12），读 `src/Ufbx.NET/Api/UfbxApi.cs`、`src/Ufbx.NET/Parse/StreamOpen.cs`、`src/Ufbx.NET/Parse/Load.cs:750-1010`、`src/Ufbx.NET/Parse/Stream.cs`、`src/Ufbx.NET/Util/Print.cs:300-432`，然后从第 4 节第 1 步开始。**不要重写已有实现**，先确认它是否已经正确。
