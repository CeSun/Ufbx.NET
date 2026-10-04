# 交接指令：ufbx-cs 移植 —— 批 N（skinning 求值体：最大的一块欠账）

> **状态：已完成（2026-10-04）。** 结论见 `COORDINATION.md` 最后一节
> 「更新（skinning 求值线会话 11，2026-10-04）」与 `PORTING_NOTES.md` 规则 15。
> 结果：`tools/SkinCheck` **1453 条 0 分歧**（17 变体 × 12 文件）；变异 33 例
> （26 咬 / 7 不咬 / 0 ERROR / 0 RESTORE FAILED，锚点全部 `cmp` 还原）；
> 电池与批 M 基线逐项一致（`tools/_scratch/battery_after_N.txt`，goldens 2179/2179，
> `streamcheck 2243020 / 3 failed` 仍是另一条线的既存问题）。
>
> ⚠️ **本文第 3.1 节的现状描述有偏差，接手请以 `COORDINATION.md` 批 N 一节的开头为准**：
> `ufbxi_evaluate_skinning()` 本体**早已移植完毕**（`src/Ufbx/Parse/SceneOpts.cs`），且**载入侧
> 调用点 (ufbx.c:25371) 早就被 2179 个 golden 覆盖**（`test/hash_scene.c:103` 设了
> `evaluate_skinning = true`，`test/hash_scene.h:620-622` 哈希 skinned 三件套）。
> 真正缺的只有 **evaluate 侧一处**（ufbx.c:26413-26419），因为 `test/hash_scene.c:136` 传的是
> NULL evaluate opts。本批交付的就是那一处接线 + 覆盖它的差分。
> **下一波：`tools/_scratch/HANDOFF_batch_O.md`（`pivot_handling` 载入选项维度）。**
> 本文保留作历史记录。
>
> 交接时点：2026-10-04（批 M 收尾后）。本文自带全部上下文，不依赖任何历史会话。
> 任务台账：`#21 批 N：移植 ufbxi_evaluate_skinning 与 skinning 相关求值`（`#20 批 M` 已完成，
> 见本文第 1 节；`#19 批 L` 见 `COORDINATION.md` 倒数第二节）。

## 0. 总纲（不可改）

- 长期目标（用户原话）：**「继续移植，目的全部移植完成」** —— 把 `C:/Workspace/_analyze_ufbx/ufbx.{c,h}`
  v0.23.1 的**全部公开 ABI** 移植到纯 C# 工程 `C:\Workspace\ufbx-cs\src\Ufbx`。
  ⚠️ **公开 ABI 账本已在批 M 达到 114/114**（见 `COORDINATION.md` 最新一节第 8 条）。
  ⇒ **本批不再有"未移植的公开 ABI"**，任务是**覆盖深度**：把 goldens 与所有差分从
  「同一子图」升级成「全子图」。
- **只读** `C:/Workspace/_analyze_ufbx`（共享参考树），任何插桩都在私有副本 `tools/_scratch/ufbx_dbg/` 做。
- 每一波交付物 = **C oracle + C# 差分 harness + 变异对照（mutation controls）**，变异结束必须用 `cmp`
  证明文件逐字节还原。
- 盲区要分类：**equivalent-by-construction（等价于构造，论证写进 PORTING_NOTES）** 还是
  **corpus/harness gap（语料或测具缺口，要补）**。
- 收尾在 `COORDINATION.md` 追加一节 + 更新相关账本；必要时在 `PORTING_NOTES.md` 加编号规则。
- 安全线：不提交任何可能含密钥的文件；不可逆或影响共享状态的操作先问用户；不删别人的在途文件。
- Oracle 编译命令（唯一被认可的配置）：

  ```
  zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx
  ```

  ⚠️ 批 L 交接文档里的 `-mcpu=x86-64` 是**笔误**，zig 只认 `x86_64`。
  触及 sin/cos/atan2/pow 追加 `-DUFBX_EXTERNAL_MATH` + `C:/Workspace/_analyze_ufbx/extra/ufbx_math.c`。
- C# 构建：`dotnet build ufbx-cs.sln -c Release`。回归总闸：`bash tools/_scratch/battery.sh`。
- ⚠️ **`perl` 已不在 PATH 上**，批 J/K 的 `tools/_mut_bake.sh` / `tools/_mut_createanim.sh` 会静默失败。
  变异脚本照抄 **`tools/_mut_pool.sh`**（python **字节级**替换 + `cmp` 还原证明）。
  两个踩过的坑：(1) 锚点**必须单行**（argv 传不了换行）；(2) 锚点不能是另一处的**子串**
  （`if (!WaitImp(Group, true)) ...` 因 WaitAll 那份只差缩进而命中 2 次）。
  锚点备份 `tools/_scratch/<basename>.bak`（批 M 现有：ThreadPool/Objects/DomNode/AsciiDomNode/Root/
  Load/UfbxiContext/UfbxApi 共 8 个；批 L 遗留的 5 个中 3 个已被覆盖，勿再用于旧波次）。
- ⚠️ **`tools/pool_oracle.exe` 不进 `battery.sh`**：改了 `tools/pool_oracle.c` 要按它文件头那行
  zig 命令手工重编再重跑 `pool_oracle.txt`。同理适用于你新建的 oracle。
- ⚠️ **受限工程要手工加 `<Compile Include>`**：`tools/AsciiCheck/AsciiCheck.csproj` 这类
  `EnableDefaultCompileItems=false` 的工程不会自动纳入新文件（批 M 加 `Parse/ThreadPool.cs` 时踩过）。

## 1. 批 M 结果摘要（本波起点）

批 M（thread pool 3 个公开 ABI）已收口，完整记录在 `COORDINATION.md` 最后一节与
`PORTING_NOTES.md` 规则 14。**先读这两处**，再往下做。

- `tools/PoolCheck`：**records 1688 / input 336 / mismatches 0 — ALL MATCH**（21 变体 × 8 文件）。
- 变异 39 例：29 咬、10 不咬（全部归为"按构造等价"或"不可达路径/被另一条规则掩盖"）、0 RESTORE FAILED。
- 账本 **114/114**。修掉的三处既有缺口：`ThreadPoolInit` 从未把 ctx 外泄给 `init_fn`、
  `ReadObjectsThreaded` 是"解析一个读一个"而非 C 的批处理循环、两个任务生产者此前创建即内联执行
  （环形队列里一个任务都没有）。

## 2. 当前基线（接手后先复现）

跑 `bash tools/_scratch/battery.sh`，逐条比对（`tools/_scratch/battery_after_M.txt` 是批 M 收尾值）：

- `goldens: 2179 files, 2179 matched, 0 mismatched, 0 load-errors`
- `streamcheck 2243020 / 3 failed` ← **既存，不属于你**，不要"顺手修"
- `PoolCheck: records 1688 input 336 mismatches 0`
- `StreamCheck: records 11395 input 2808 mismatches 0`
- `CreateAnimCheck: records 13676 input 8086 mismatches 0`
- `BakeCheck: records 50261 input(T) 29280 mismatches 0`
- `s4b 12091 行 / 0`；`s4c 332068`；`S3BC 1477 PASS`；mathvec 72000；util 7003；inflate 4613；
  dom/graph 691；load 0 分歧；s2geom 0；s3scene 13546；hash 79；animcurve 8000；ascii 1737；
  numeric 280169；s4a 1086。

## 3. 本批任务：skinning 求值体

### 3.1 现状（为什么这是最大的欠账）

- `src/Ufbx/Api/UfbxSkinApi.cs` **已有** skinning 的**访问器**（`ufbx_get_skin_vertex_matrix`、
  `ufbx_get_blend_shape_offset_index`、`ufbx_get_blend_shape_vertex_offset`、
  `ufbx_get_blend_vertex_offset`、`ufbx_add_blend_shape_vertex_offsets`、
  `ufbx_add_blend_vertex_offsets` 等七件），它们读的是 `UfbxSkinDeformer`/`UfbxBlendShape` 上的
  **已有数据**。
- 但**装载路径**没有：`ufbxi_evaluate_skinning()`（ufbx.c:25063-…）从未被移植。因此
  `Parse/EvaluateScene.cs` 在 `Opts.EvaluateSkinning` 为真时抛 `UfbxiReaderNotPortedException`，
  ⇒ **批 K 的 `V` 维、以及全部 golden 对拍，都只能传 NULL evaluate opts / 走
  `evaluate_skinning = false` 子图**。
  换句话说：goldens 现在证明的是"两侧在同一子图下逐位一致"，**还没证明全子图**。

### 3.2 C 侧权威行号（照这个读）

| C 语义 | ufbx.c |
|---|---|
| `ufbxi_evaluate_skinning` | 25063-…（调用点 25371 在 `ufbxi_load_imp`、26417 在 geometry cache） |
| `ufbx_skin_vertex` 的分配 | 21892 |
| skin 顶点矩阵 / 权重的消费点 | 19349、29536、31942 |
| blend shape / deformer 相关 | 用 `grep -n "blend\|skin" ufbx.c` 自行补全后**写回本文** |

`ufbx.h` 侧：skinning 一节（`ufbx_skin_deformer` / `ufbx_skin_vertex` / `ufbx_skin_weight` /
`ufbx_blend_deformer` / `ufbx_blend_shape` / `ufbx_blend_channel` / `ufbx_blend_shape_frame`）。

### 3.3 建议的拆解顺序（可改，写回理由）

1. 先把 **`ufbxi_evaluate_skinning()` 的最小可用骨架**移植出来，让 `Opts.EvaluateSkinning = true`
   不再抛异常 —— 这一步就能把 goldens 从"子图"推进到"全子图"，**收益最大**，也是顺手暴露后续所有
   skin 相关分歧的一步。
2. 然后按 golden 的分歧点逐个补：权重归一化、`UFBX_SKINNING_METHOD`、blend shape 的
   `Add`/`Lerp` 模式、deformer 链（skin → blend 顺序）。
3. 最后补 `ufbx_load_geometry_cache` / `ufbx_read_geometry_cache_*` 那条同样调
   `ufbxi_evaluate_skinning` 的路径（26417）。

### 3.4 差分测具设计建议

- 语料：goldens 里的 skinned 文件（`maya_dq_weights_7500_ascii.fbx`、
  `maya_advanced_skinned_pivot_7700_ascii.fbx` 等；`data/` 下有专门的 skin/blend 系列）。
- 记录文法照抄批 M 的自描述风格（`I` 输入记录 + 若干输出记录），**变体表只留在 oracle 里**。
- 至少要有：`S`（payload 一致）、`A`（加载结果与 `ufbx_error` 逐字节）、
  `V`（`ufbxt_hash_scene()` 端到端），再加一层 skin 专用记录（每个 skin deformer 的
  vertex/weight 数、前 N 个顶点的矩阵摘要）。
- ⚠️ 浮点口径遵循 PORTING_NOTES「浮点语义」：禁止改写求值顺序 / 合并表达式。

## 4. 之后的波次

1. 批 J 遗留覆盖缺口：`pivot_handling` 载入选项维度、"动画区间严格内嵌于 bake 区间"的语料、
   animated scale-helper 子节点、180° 翻转的连续四元数。
2. 批 M 遗留（已按"按构造等价/不可复现"登记，不必补语料）：`run_fn` 不执行或只执行一部分
   （未完成任务的目标缓冲区＝arena 未初始化字节，`barbarian` 文件实测两次哈希不同）、
   `num_tasks` 大到分配失败（C 报 OUT_OF_MEMORY，#4）、真实并发交错顺序、
   `tmp_buf` 的内存门限（ufbx.c:15192）、非法/已释放的 ctx（C 是野写 / use-after-free）。
3. 批 L 遗留：过量供给的 `read`、`ufbx_open_memory` 的二次 `Close`（C 是 double free）、
   `stream == NULL`、分配失败、非 `_WIN32` 的 `fopen` 分支、`UFBX_NO_STDIO`。

## 5. 上手第一件事

读 `COORDINATION.md` 最新一节（批 M）+ `PORTING_NOTES.md` 规则 13/14 与 #3/#4/#7/#8/规则 12，
读 `src/Ufbx/Api/UfbxSkinApi.cs`、`src/Ufbx/Parse/EvaluateScene.cs`、
`src/Ufbx/Parse/Load.cs:180-195`（`UfbxiReaderNotPortedException` 的抛出点），
然后从第 2 节的基线复现开始。**不要重写已有实现**，先确认它是否已经正确。
