# 交接指令：ufbx-cs 移植 —— 批 P（animated scale-helper 子节点：批 J 遗留的覆盖缺口）

> **【已完成 · 2026-10-04】** 结果见 `COORDINATION.md` 批 P 节与 `PORTING_NOTES.md` 规则 17。
> 要点：调查证明这是**真缺口**（PRESERVE 下 690 个文件建 0 个 helper，goldens 完全没碰过）；
> 端口**一行未改**；`tools/ShCheck` **44716 条 / 0 分歧**；变异 35 例 **26 咬 / 9 不咬 / 0 ERROR
> / 0 RESTORE FAILED**。后续波次（批 Q、批 R）也已完成，见 `HANDOFF_final.md`。
>
> 交接时点：2026-10-04（批 O 收尾后）。本文自带全部上下文，不依赖任何历史会话。
> 任务台账：`#23 批 P：补 animated scale-helper 子节点的差分覆盖`
> （`#22 批 O` 已完成，见本文第 1 节）。
>
> ⚠️ **本批的第一个动作是调查，不是开工**。批 N 与批 O 都证明了同一件事：交接文档里写的
> "最大欠账"有可能是假的（批 N 的 skinning 本体早就移植完了；批 O 的 `pivot_handling` 本体
> 也早就移植完了，一行端口代码都没改）。**先花 20 分钟把第 3.1 节的三个数字测出来，再决定
> 本批到底做什么**，别把一波花在"其实早被 goldens 覆盖"的东西上。

## 0. 总纲（不可改）

- 长期目标（用户原话）：**「继续移植，目的全部移植完成」** —— 把 `C:/Workspace/_analyze_ufbx/ufbx.{c,h}`
  v0.23.1 的**全部公开 ABI** 移植到纯 C# 工程 `C:\Workspace\ufbx-cs\src\Ufbx.NET`。
  ⚠️ **公开 ABI 账本已在批 M 达到 114/114**，批 N / 批 O 都没变。⇒ **后续每一波都是覆盖深度**，
  不是门面。当前对外完成度口径见 `COORDINATION.md` 批 N 节与工作区记忆：门面 100%、
  C 函数体映射 ~97%、端到端保真 100%，综合约 95%。
- **只读** `C:/Workspace/_analyze_ufbx`（共享参考树），任何插桩都在私有副本 `tools/_scratch/ufbx_dbg/` 做。
- 每一波交付物 = **C oracle + C# 差分 harness + 变异对照（mutation controls）**，变异结束必须用 `cmp`
  证明文件逐字节还原。
- 盲区要分类：**equivalent-by-construction（等价于构造，论证写进 PORTING_NOTES）** 还是
  **corpus/harness gap（语料或测具缺口，要补）**。批 O 的两个惰性变异就是后者的样板：
  先用扩展探针把"为什么造不出这个区分"量化（例：全语料最小非零 pivot 分量 = 0.00799，
  比阈值 0.000977 大 8 倍），再写结论。
- 收尾在 `COORDINATION.md` 追加一节 + 更新相关账本；必要时在 `PORTING_NOTES.md` 加编号规则
  （已有 16 条）。
- Oracle 编译命令（唯一被认可的配置）：

  ```
  zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx
  ```

  触及 sin/cos/atan2/pow 追加 `-DUFBX_EXTERNAL_MATH` + `C:/Workspace/_analyze_ufbx/extra/ufbx_math.c`。
- C# 构建：`dotnet build ufbx.net.sln -c Release`。回归总闸：`bash tools/_scratch/battery.sh`（约 5 分钟）。
- ⚠️ **`perl` 已不在 PATH 上**，批 J/K 的 `tools/_mut_bake.sh` / `tools/_mut_createanim.sh` 会静默失败。
  变异脚本照抄 **`tools/_mut_pivot.sh`**（python **字节级**替换 + `cmp` 还原）。锚点**必须单行**且
  **不能是另一处的子串**；锚点备份 `tools/_scratch/<basename>.bak`。
  ⚠️ 锚点唯一性**先跑一遍 python `s.count(old)` 预检**再进 sweep —— 批 O 有 3 个候选锚点因为
  `canModifyGeometryTransform = false;` 出现 3 次而被迫改用带缩进的变体。
- ⚠️ **变异 sweep 必须整批后台跑**（`run_in_background`）。批 N 用前台 `while read` 循环跑 33 例，
  在第 32 例的 `dotnet run` 中途被 10 分钟超时 SIGTERM，**脚本在"已替换、未还原"的状态被杀**，
  源文件留在被改状态，污染了后续结果。批 O 整批后台跑 40 例（11m52s）安然通过。
- ⚠️ **oracle `.exe` 不进 `battery.sh`**：改了 `tools/*.c` 要按文件头那行 zig 命令手工重编再重跑
  `.txt`。`tools/PivotCheck` 等隔离工程是 `EnableDefaultCompileItems=false`，**新增源文件要手工加
  `<Compile Include>`**（批 M 加 `Parse/ThreadPool.cs` 时踩过）。
- ⚠️ **不要把 oracle `.exe` 拷进 `C:/Workspace/_analyze_ufbx`**：那棵树是只读的共享参考树。
  把 CWD 设到那里、用**绝对路径**调用 `C:/Workspace/ufbx-cs/tools/<name>_oracle.exe` 即可
  （批 N 实测输出逐字节相同）。
- ⚠️ 隔离 harness 的语料路径是**相对 `_analyze_ufbx`** 的，必须从 `C:/Workspace/_analyze_ufbx` 跑，
  且 oracle/corpus 两个参数都传**绝对路径**。
- ⚠️ 隔离 harness 跑法的两种等价形式：`dotnet run --project <csproj> -c Release -- <args>`，
  或直接 `tools/<Name>/bin/Release/net8.0/<Name>.exe <args>`（后者快得多，sweep 里 40 例用它）。

## 1. 批 O 结果摘要（本波起点）

批 O（`pivot_handling` 载入选项维度）已收口。完整记录在 `COORDINATION.md` 最后一节与
`PORTING_NOTES.md` 规则 16。**先读这两处**，再往下做。

- `tools/PivotCheck`：**records 4342 / input 408 / mismatches 0 — ALL MATCH**（24 变体 × 17 文件）。
- 变异 40 例：32 咬 / 8 不咬（6 例按设计不咬 + 2 例语料缺口，已用扩展探针量化）/ 0 ERROR / 0 RESTORE FAILED。
- **一行端口代码都没改**：本体（`src/Ufbx.NET/Parse/SceneBuild.cs:464-583`）早已移植完毕且逐行核对无差异。
- 语料是**按证据挑的**：`tools/_scratch/_pivot_probe.{c,exe,txt}` 跑遍 `data/*.fbx`，报出每个文件的
  pivot 构成；690 个文件里只有 37 个带非零 pivot。
- **最重要的方法论教训（写进规则 16）**：`test/hash_scene.h` **没有** `adjust_pre_translation`，
  所以端到端哈希（`V` 记录）**看不见** pivot 块的主要产出 ⇒ 定位记录不是锦上添花而是必需。
  **动工前先确认你要证的字段在不在 `test/hash_scene.h` 里**，不在就必须自建定位记录。

## 2. 当前基线（接手后先复现）

跑 `bash tools/_scratch/battery.sh`，逐条比对（`tools/_scratch/battery_after_O.txt` 是批 O 收尾值）：

- `goldens: 2179 files, 2179 matched, 0 mismatched, 0 load-errors`
- `streamcheck 2243020 / 3 failed` ← **既存，不属于你**，不要"顺手修"
- `PivotCheck: records 4342 input 408 mismatches 0`
- `SkinCheck: records 1453 input 221 mismatches 0`
- `PoolCheck: records 1688 input 336 mismatches 0`
- `StreamCheck: records 11395 input 2808 mismatches 0`
- `CreateAnimCheck: records 13676 input 8086 mismatches 0`
- `BakeCheck: records 50261 input(T) 29280 mismatches 0`
- `s4b 12091 行 / 0`；`s4c 332068`；`S3BC 1477`；mathvec 72000；util 7003；inflate 4613；
  dom/graph 691；load 0 分歧；s2geom 0；s3scene 13546；hash 79；animcurve 8000；ascii 1737；
  numeric 280169；s4a 1086。

## 3. 本批任务

### 3.1 先调查（这一步先做，结论决定本批做什么）

写 `tools/_scratch/_sh_probe.c`（照抄 `tools/_scratch/_pivot_probe.c` 的骨架：公开 API、
`--list` 跑遍 `data/*.fbx`、跳过 `#` 注释行、输出一列一文件的统计），测出三个数：

1. **有多少文件创建了 scale helper**（`node->is_scale_helper` 为真，或 `node->scale_helper != NULL`）。
2. 其中，**helper 的 scale 是动画的**有多少（这是 bake 侧 `Parse/Bake.cs:770`
   `if (!scaleHelperT.ConstantScale)` 那一条分支的唯一入口）。
3. 这些文件里，`node_depth` / `inherit_mode` 的分布（有没有递归 helper，即
   `Parse/SceneBuild.cs:634-639` 的 `HasRecursiveScaleHelper` 路径）。

⚠️ **别跳过这一步的理由**：`test/hash_scene.h:458` 哈希了 `is_scale_helper`，而
`ufbxi_setup_scale_helper` 的创建门限（ufbx.c:18487-18540）**没有** `inherit_mode_handling`
的前置条件（只排除 `COMPENSATE_NO_FALLBACK`），默认 `PRESERVE` 下照样建。
⇒ **场景构建侧的 scale helper 很可能早就被 2179 个 golden 证明了**。
真正的缺口大概只在 **bake 侧 + helper 的 scale 是动画的** 那一支。
如果第 2 个数是 0，**本批就该换个目标**（见第 3.4 节），不要硬做。

### 3.2 C 侧权威行号（照这个读，行号自己 `grep -n` 复核一遍并写回本文）

| C 语义 | 位置 |
|---|---|
| scale helper 创建（`has_unscaled_children` 主循环） | ufbx.c:18487-18540（端口 `SceneBuild.cs:612-639`） |
| 递归子节点 helper | ufbx.c:18500-18540（端口 `SceneBuild.cs:634-639`） |
| bake 消费 helper 的 scale | `ufbxi_bake_...` 中对应 `Parse/Bake.cs:762-775` 的那段 |
| `UFBX_TRANSFORM_FLAG_IGNORE_SCALE_HELPER` | 端口 `Parse/Bake.cs:159` `FlagIgnoreScaleHelper = 0x1u` |
| `ufbxi_pre_node` 的 `constant_scale` / `has_constant_scale` | ufbx.c:18169 附近（`scale_epsilon` 那一片） |

`ufbx.h` 侧：`ufbx_node.is_scale_helper` / `scale_helper` / `inherit_mode` / `original_inherit_mode`。

### 3.3 建议步骤

1. 复现第 2 节基线。
2. 做第 3.1 节的调查，**把结论写回本文再往下走**。
3. **先确认现有移植是对的**：读 `Parse/SceneBuild.cs:605-650` 与 `Parse/Bake.cs:755-790`，
   逐行对照 C。本批大概率又是"补证明"，不是重写；如果差分一上来就分歧，先判断是移植错还是测具错。
4. 写 `tools/sh_oracle.c`（`#include "ufbx.c"` + `test/hash_scene.h`，照抄 `tools/pivot_oracle.c`
   的结构：`I` 自描述输入 + `A` + 定位记录 + `V`）+ `tools/sh_corpus.txt` + `tools/ShCheck/`
   （照抄 `tools/PivotCheck/PivotCheck.csproj`），在 `battery.sh` 加 `run sh`。迭代到 **0 mismatches**。
   - 变体维度建议：`inherit_mode_handling ∈ {PRESERVE, HELPER_NODES, COMPENSATE,
     COMPENSATE_NO_FALLBACK, IGNORE}` × `space_conversion` / `target_axes` 按需叠加
     × bake 的 `UFBX_TRANSFORM_FLAG_IGNORE_SCALE_HELPER` 开关。
   - **定位记录建议**：每个 node 的 `is_scale_helper` / `scale_helper` 指向 / `inherit_mode` /
     `node_depth` / `constant_scale` 与否；bake 侧再给每节点发
     `baked_node.{constant_scale, constant_rotation, ...}` 与 scale key 的时间戳序列。
     ⚠️ 先查 `test/hash_scene.h` 有没有哈希这些字段，没有的必须自己发（批 O 的教训）。
5. 变异对照 ~30-40 例（含 `Q_*` 惰性对照与 2-4 例**恒等变换**作测具自校验），
   每例 `cmp` 还原证明，盲区分类写清。
6. 跑 `battery.sh`，与第 2 节基线逐条比对。
7. 登记：`COORDINATION.md` 新节（批 P）；若变异结论支持，加 `PORTING_NOTES.md` 规则 17。
8. 更新本文（或改写为批 Q 交接），并把工作区记忆同步。

### 3.4 如果第 3.1 节调查显示"没有 animated scale helper"，改做这两个之一

- **180° 翻转的连续四元数**：`ufbx.c:31529` 的 `ufbx_quat_dot(q, reference) < 0.0f`
  （批 N 的 skinning 侧同源：`ufbx.c:31962`，变异 `C_dqdot_skip` 只咬 4 条 ⇒ 覆盖很薄）。
  ⚠️ **别找错地方**：`ufbx.c:28570` 注释里的 "Angle must be less than 180deg" 是**耳切三角化**
  的，与本项无关。
- **"动画区间严格内嵌于 bake 区间"的语料**：bake 的 `PushResampledTimes` 与区间裁剪逻辑
  （`Parse/Bake.cs`）。同样**先用探针确认语料里有没有**，没有就是手搓 FBX 的活。

## 4. 之后的波次

1. 批 J 遗留里本批没做的那 1-2 项（见第 3.4 节）。
2. 批 O 遗留（**语料缺口，需手搓 FBX**，`data/` 里不存在）：
   pivot 分量落在 `(0, 0.0009765625)` 的 FBX（区分 `ufbxi_is_vec3_zero` 与 `ufbxi_pivot_nonzero`）、
   `0 < |rp-sp|₁ <= 0.001` 的 FBX（区分 `pivot_epsilon` 的具体值）。
3. 批 N 遗留（语料缺口）：带 2 个以上 skin deformer 的 mesh（C 自己的 `// TODO`，ufbx.c:25127）、
   带 deformer 且 0 顶点的 mesh（ufbx.c:25079）。
4. 批 M 遗留（按"按构造等价/不可复现"登记，不必补语料）：`run_fn` 部分执行（未完成任务＝
   arena 未初始化字节）、`num_tasks` 大到分配失败、真实并发交错顺序、`tmp_buf` 内存门限、
   非法或已释放的 ctx。
5. 批 L 遗留：过量供给的 `read`、`ufbx_open_memory` 二次 `Close`（C 是 double free）、
   `stream == NULL`、分配失败、非 `_WIN32` 的 `fopen` 分支、`UFBX_NO_STDIO`。

## 5. 上手第一件事

读 `COORDINATION.md` 最后一节（批 O）+ `PORTING_NOTES.md` 规则 16，然后**写第 3.1 节的探针**，
拿到那三个数字再决定本批做什么。**不要先写 oracle，也不要先改端口。**