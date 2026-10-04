# 交接指令：ufbx-cs 移植 —— 批 O（`pivot_handling` 载入选项维度：批 J 遗留的覆盖缺口）

> **状态：已完成（2026-10-04）。** 结论见 `COORDINATION.md` 最后一节
> 「更新（pivot 维度线会话 12，2026-10-04）」与 `PORTING_NOTES.md` 规则 16。
> 结果：`tools/PivotCheck` **4342 条 0 分歧**（24 变体 × 17 文件）；变异 40 例
> （32 咬 / 8 不咬 / 0 ERROR / 0 RESTORE FAILED，锚点全部 `cmp` 还原）；
> 电池与批 N 基线逐项一致（`tools/_scratch/battery_after_O.txt`）。
>
> ⚠️ **两点更正，接手请以 `COORDINATION.md` 批 O 一节的开头为准**：
> 1. **本批没有改一行端口代码** —— 本体早已移植完毕且逐行核对无差异，批 O 只是**补证明**。
> 2. 本文 §3.1 把默认值写成 `UFBX_PIVOT_HANDLING_NONE` 是**错的**：枚举第一个值是
>    **`UFBX_PIVOT_HANDLING_RETAIN`（= 0）**，没有 NONE。另外 §3.3 第 3 条建议的
>    `scene->node_depth` / `adjust_mirror_axis` 里，真正关键的是
>    **`adjust_pre_translation`**（`test/hash_scene.h` 根本没哈希它）。
>
> 交接时点：2026-10-04（批 N 收尾后）。本文自带全部上下文，不依赖任何历史会话。
> 任务台账：`#22 批 O：补 `pivot_handling` / `pivot_handling_retain_empties` 的差分覆盖`
> （`#21 批 N` 已完成，见本文第 1 节）。

## 0. 总纲（不可改）

- 长期目标（用户原话）：**「继续移植，目的全部移植完成」** —— 把 `C:/Workspace/_analyze_ufbx/ufbx.{c,h}`
  v0.23.1 的**全部公开 ABI** 移植到纯 C# 工程 `C:\Workspace\ufbx-cs\src\Ufbx.NET`。
  ⚠️ **公开 ABI 账本已在批 M 达到 114/114**，批 N 也没变。⇒ **本批是覆盖深度**，不是门面。
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

  触及 sin/cos/atan2/pow 追加 `-DUFBX_EXTERNAL_MATH` + `C:/Workspace/_analyze_ufbx/extra/ufbx_math.c`。
- C# 构建：`dotnet build ufbx.net.sln -c Release`。回归总闸：`bash tools/_scratch/battery.sh`（约 5 分钟）。
- ⚠️ **`perl` 已不在 PATH 上**，批 J/K 的 `tools/_mut_bake.sh` / `tools/_mut_createanim.sh` 会静默失败。
  变异脚本照抄 **`tools/_mut_skin.sh`**（python **字节级**替换 + `cmp` 还原）。锚点**必须单行**且
  **不能是另一处的子串**；锚点备份 `tools/_scratch/<basename>.bak`。
- ⚠️ **变异 sweep 必须整批后台跑**（`run_in_background`）。批 N 用前台 `while read` 循环跑 33 例，
  在第 32 例的 `dotnet run` 中途被 10 分钟超时 SIGTERM，**脚本在"已替换、未还原"的状态被杀**，
  源文件留在被改状态，下一轮报 `ERROR: 0 occurrences` 并污染了后续结果。必须避免。
- ⚠️ **oracle `.exe` 不进 `battery.sh`**：改了 `tools/*.c` 要按文件头那行 zig 命令手工重编再重跑
  `.txt`。`tools/SkinCheck` 等隔离工程是 `EnableDefaultCompileItems=false`，**新增源文件要手工加
  `<Compile Include>`**（批 M 加 `Parse/ThreadPool.cs` 时踩过）。
- ⚠️ **不要把 oracle `.exe` 拷进 `C:/Workspace/_analyze_ufbx`**：那棵树是只读的共享参考树。
  把 CWD 设到那里、用**绝对路径**调用 `C:/Workspace/ufbx-cs/tools/<name>_oracle.exe` 即可
  （批 N 实测输出逐字节相同）。
- ⚠️ 隔离 harness 的语料路径是**相对 `_analyze_ufbx`** 的，必须从 `C:/Workspace/_analyze_ufbx` 跑，
  且 oracle/corpus 两个参数都传**绝对路径**。

## 1. 批 N 结果摘要（本波起点）

批 N（`ufbxi_evaluate_skinning` 的 evaluate 侧）已收口。完整记录在 `COORDINATION.md` 最后一节与
`PORTING_NOTES.md` 规则 15。**先读这两处**，再往下做。

- `tools/SkinCheck`：**records 1453 / input 221 / mismatches 0 — ALL MATCH**（17 变体 × 12 文件）。
- 变异 33 例：26 咬 / 7 不咬（5 例按设计不咬 + 2 例语料缺口）/ 0 ERROR / 0 RESTORE FAILED。
- 交付是**一处接线**：`Parse/EvaluateScene.cs` 里把 `NotPorted("ufbxi_evaluate_skinning (S4c)")`
  换成 C:26413-26419 的本体。**不是重写** —— 本体早在 `Parse/SceneOpts.cs` 里。
- 语料是**按证据挑的**：`tools/_scratch/_skin_probe.{c,exe,txt}` 跑遍 `data/*.fbx` 报出每个文件的
  deformer 构成与 `maxSkinPerMesh` / `zeroVertDeformer`。

## 2. 当前基线（接手后先复现）

跑 `bash tools/_scratch/battery.sh`，逐条比对（`tools/_scratch/battery_after_N.txt` 是批 N 收尾值）：

- `goldens: 2179 files, 2179 matched, 0 mismatched, 0 load-errors`
- `streamcheck 2243020 / 3 failed` ← **既存，不属于你**，不要"顺手修"
- `SkinCheck: records 1453 input 221 mismatches 0`
- `PoolCheck: records 1688 input 336 mismatches 0`
- `StreamCheck: records 11395 input 2808 mismatches 0`
- `CreateAnimCheck: records 13676 input 8086 mismatches 0`
- `BakeCheck: records 50261 input(T) 29280 mismatches 0`
- `s4b 12091 行 / 0`；`s4c 332068`；`S3BC 1477 PASS`；mathvec 72000；util 7003；inflate 4613；
  dom/graph 691；load 0 分歧；s2geom 0；s3scene 13546；hash 79；animcurve 8000；ascii 1737；
  numeric 280169；s4a 1086。

## 3. 本批任务：`pivot_handling` 维度

### 3.1 为什么是它

`pivot_handling` 是 `ufbx_load_opts` 的一个**三值枚举**（`UFBX_PIVOT_HANDLING_*`，`ufbx.h:4823`），
加上配套的 `pivot_handling_retain_empties`（`ufbx.h:4826`）。端口**已经移植**：

- `src/Ufbx.NET/Enums.cs:949` `UfbxPivotHandling`（枚举值 3 个，`UfbxEnumCounts.UfbxPivotHandling = 3`）
- `src/Ufbx.NET/Model/Opts/LoadOpts.cs:77-78` 两个选项字段
- `src/Ufbx.NET/Parse/SceneBuild.cs:266-267 / 465-…` 分支本体（对应 C:18120 / 18329-18398）
- `src/Ufbx.NET/Parse/Load.cs:1169-1170` 拷贝；`src/Ufbx.NET/Model/UfbxScene.cs:102` 与 C:23736 的
  `scene->metadata.pivot_handling = uc->opts.pivot_handling`

但**没有任何差分覆盖它**：goldens 用 `{0}` 的 load opts（默认 `UFBX_PIVOT_HANDLING_NONE`），
`tools/LoadCheck`、`tools/S3bcCheck`、`tools/BakeCheck`、`tools/SkinCheck` 也都没有这一维。
⇒ 这是"移植了但没证明"的一块，**本批把维度建起来**。

### 3.2 C 侧权威行号（照这个读）

| C 语义 | ufbx.c |
|---|---|
| `required = true` 的判定（决定是否解析 pivot 相关属性） | 18120 |
| `ufbxi_read_pivot_*` / pivot 处理主体 | 18329-18398 |
| `ADJUST_TO_PIVOT` 分支 | 18339-18341, 18369-18386 |
| `ADJUST_TO_ROTATION_PIVOT` 分支（含 `retain_empties`） | 18341-18348, 18398 |
| `scene->metadata.pivot_handling` | 23736 |

`ufbx.h` 侧：枚举 4815-4823、`pivot_handling_retain_empties` 4826、`ufbx_scene_settings/metadata`
里的 `pivot_handling`。

⚠️ 行号请以 `grep -n "pivot_handling" C:/Workspace/_analyze_ufbx/ufbx.c` 自己复核一遍，**写回本文**。

### 3.3 建议步骤

1. 先复现第 2 节基线。
2. **先确认现有移植是对的**：读 `Parse/SceneBuild.cs:260-300 / 460-520`，逐行对照 C:18120 / 18329-18398。
   本批不是重写，是**补证明**；如果差分一上来就分歧，先判断是移植错还是测具错。
3. 写 `tools/pivot_oracle.c`（`#include "ufbx.c"` + `test/hash_scene.h`，照抄 `tools/skin_oracle.c`
   的结构：`I` 自描述输入记录 + `A` 结果 + `V` 端到端哈希 + 若干定位记录）+ `tools/pivot_corpus.txt`
   + `tools/PivotCheck/`（照抄 `tools/SkinCheck/SkinCheck.csproj`），在 `battery.sh` 加 `run pivot`。
   迭代到 **0 mismatches**。
   - 变体维度建议（3×2=6 起步，可再叠 `space_conversion` / `target_axes` / `geometry_transform_handling`）：
     `pivot_handling ∈ {NONE, ADJUST_TO_PIVOT, ADJUST_TO_ROTATION_PIVOT}` ×
     `pivot_handling_retain_empties ∈ {0, 1}`。
   - 语料建议（`data/` 里名字带 pivot 的，用批 N 那种探针先筛一遍**真的带 pivot 属性**的）：
     `maya_advanced_skinned_pivot_7700_{ascii,binary}.fbx`、`maya_child_pivots_{6100,7700}_ascii.fbx`、
     `maya_equal_pivot_7700_ascii.fbx`、`maya_equal_pivot_scale_7700_ascii.fbx`、
     `maya_anim_pivot_rotate_7700_ascii.fbx`、`maya_skinned_pivot_7700_ascii.fbx`、
     `maya_transformed_skin_7700_binary.fbx`、`maya_poses_7700_ascii.fbx`。
   - **定位记录建议**：每个 node 的 `geometry_transform` 是否被改写、`parent` 链、`is_scale_helper`、
     `adjust_mirror_axis`、`node_depth`，以及 `scene->metadata.pivot_handling`。
     不要只靠 `V`（端到端哈希）——它只能告诉你"不同"，不能告诉你"哪里不同"。
4. 变异对照 ~25-30 例（含 `Q_*` 惰性对照），每例 `cmp` 还原证明，盲区分类写清。
5. 跑 `battery.sh`，与第 2 节基线逐条比对。
6. 登记：`COORDINATION.md` 新节（批 O）；若变异结论支持，加 `PORTING_NOTES.md` 规则 16。
7. 更新本文（或改写为批 P 交接），并把工作区记忆同步。

## 4. 之后的波次

1. 批 J 遗留的另外三项（本批只做 `pivot_handling`）：
   - "动画区间严格内嵌于 bake 区间"的语料
   - animated scale-helper 子节点（端口已有 `UfbxNode.ScaleHelper` / `IsScaleHelper` /
     `UfbxTransformFlag.IgnoreScaleHelper`，`Parse/Bake.cs:763` 有 `hasScaleHelperT`）
   - 180° 翻转的连续四元数
2. 批 N 遗留（**语料缺口，需要手搓 FBX**，`data/` 里不存在，见批 N 一节第 4 条 f/g）：
   带 2 个以上 skin deformer 的 mesh（C 自己的 `// TODO`，ufbx.c:25127）、
   带 deformer 且 0 顶点的 mesh（ufbx.c:25079）。
3. 批 M 遗留（按"按构造等价/不可复现"登记，不必补语料）：`run_fn` 部分执行（未完成任务＝
   arena 未初始化字节）、`num_tasks` 大到分配失败、真实并发交错顺序、`tmp_buf` 内存门限、
   非法或已释放的 ctx。
4. 批 L 遗留：过量供给的 `read`、`ufbx_open_memory` 二次 `Close`（C 是 double free）、
   `stream == NULL`、分配失败、非 `_WIN32` 的 `fopen` 分支、`UFBX_NO_STDIO`。

## 5. 上手第一件事

读 `COORDINATION.md` 最后一节（批 N）+ `PORTING_NOTES.md` 规则 15 与 #4 / 规则 12，
读 `src/Ufbx.NET/Parse/SceneBuild.cs:260-300` 与 `460-520` 对上 C:18120 / 18329-18398，
然后从第 3.3 节第 2 步开始。**不要重写已有实现**，先确认它是否已经正确。
