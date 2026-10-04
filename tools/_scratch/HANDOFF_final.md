# 交接指令：ufbx-cs 移植 —— 收工状态（批 P / Q / R 全部完成后）

> 交接时点：2026-10-04（批 P、批 Q、批 R 三波全部收口后）。本文自带全部上下文，
> 不依赖任何历史会话。**先读 `COORDINATION.md` 的批 N / O / P / Q / R 五节与
> `PORTING_NOTES.md` 规则 15-19**，再往下做。

## 0. 总纲（不可改）

- 长期目标（用户原话）：**「继续移植，目的全部移植完成」** —— 把 `C:/Workspace/_analyze_ufbx/ufbx.{c,h}`
  v0.23.1 的**全部公开 ABI** 移植到纯 C# 工程 `C:\Workspace\ufbx-cs\src\Ufbx.NET`。
- **公开 ABI 账本：114/114**（批 M 达成，之后每一波都没变）。⇒ 门面已封顶。
- **只读** `C:/Workspace/_analyze_ufbx`（共享参考树），任何插桩都在私有副本做
  （`tools/_scratch/ufbx_nofix.c` 是批 Q 用过的一个例子）。
- 每一波交付物 = **C oracle + C# 差分 harness + 变异对照**，变异结束必须 `cmp` 证明逐字节还原。
- 盲区分类：**equivalent-by-construction**（论证写进 PORTING_NOTES）vs **corpus/harness gap**（要补）。
- Oracle 编译命令（唯一被认可的配置）：

  ```
  zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx
  ```

  触及 sin/cos/atan2/pow 追加 `-DUFBX_EXTERNAL_MATH` + `C:/Workspace/_analyze_ufbx/extra/ufbx_math.c`。
- 回归总闸：`bash tools/_scratch/battery.sh`（约 4-5 分钟）。

## 1. 完成度（2026-10-04 定稿）

| 口径 | 数值 | 依据 |
|---|---|---|
| 公开 ABI 门面 | **100%**（114/114） | 人工核定，不要信 `tools/_scratch/abi_scan.sh` 的自动扫描 |
| C 函数体映射 | ~97% | ufbx.c 883 个列首函数定义，856 个在端口有对应；差的 27 个绝大多数是 arena/分配器内部（规则 4 明确不移植） |
| 端到端行为保真 | 100% | goldens 2179/2179 + 24 个差分 harness 全 0 分歧 |
| **综合** | **约 95%** | 剩下的不是"代码没写"，是规则豁免 + 未钉证据 |

## 2. 当前回归基线（`tools/_scratch/battery_after_R.txt`，3m42s，exit=0）

- `goldens: 2179 files, 2179 matched, 0 mismatched, 0 load-errors`
- `streamcheck 2243020 / 3 failed` ← **既存，不属于任何一波**，不要"顺手修"
- `ShCheck: 44716 / 0`（批 P）　`PivotCheck: 4488 / 0`（批 O+批 R）
- `BakeCheck: 53083 / 0`（批 J+批 Q）　`SkinCheck: 1536 / 0`（批 N+批 R）
- `PoolCheck: 1688 / 0`（批 M）　`StreamCheck: 11395 / 0`（批 L）
- `CreateAnimCheck: 13676 / 0`（批 K）
- 其余：mathvec 72000、util 7003、numeric 280169、s4c 332068、s4b 12091、s3scene 13546、
  s3bc 1477、animcurve 8000、ascii 1737、inflate 4613、hash 79、s4a 1086、dom/graph/load/s2geom 全 0

## 3. 已经闭合的覆盖缺口（批 N → 批 R 五波）

| 波 | 对象 | 结论 |
|---|---|---|
| 批 N | `ufbxi_evaluate_skinning` 的 evaluate 侧调用点 | 只有 evaluate 侧缺一处接线；载入侧早被 goldens 逐位证明 |
| 批 O | `pivot_handling` / `retain_empties` | 端口一行未改；goldens 走不到（RETAIN 就是 0） |
| 批 P | scale helper 子节点 | 端口一行未改；**真缺口** —— PRESERVE 下 690 个文件建 0 个 helper |
| 批 Q | bake 的 antipodal 四元数 + `ufbxi_bake_times` | 前者补 2 个语料闭合；后者**本来就覆盖**（6 个变异全咬） |
| 批 R | 4 个语料缺口 | 手搓 2 个合成 FBX，**4 个变异全部从 0 条变成咬**（20/20/30/30） |

## 4. 剩下的（只有"按构造豁免"，没有已知的语料缺口了）

1. **批 M 遗留**（规则 14）：`run_fn` 部分执行（未完成任务的目标缓冲 = arena 未初始化字节，
   C 自己两次跑哈希都不一样）、`num_tasks` 大到分配失败、真实并发交错顺序、
   `tmp_buf` 的内存门限、非法或已释放的 ctx（C 是野写 / use-after-free）。
2. **批 L 遗留**（规则 13）：过量供给的 `read`、`ufbx_open_memory` 二次 `Close`（C 是 double
   free）、`stream == NULL`、分配失败、非 `_WIN32` 的 `fopen` 分支、`UFBX_NO_STDIO`。
3. **规则 4**：arena / 分配器内部（`ufbxi_alloc_size` / `ufbxi_push_size_*` / `ufbxi_free_*` /
   `ufbxi_realloc_size`）不移植 —— 托管 GC 没有对应物。
4. 批 P 的 5 个语料缺口若要继续补，仍需手搓 FBX（`C_bake_t_gate` 的"自身是 helper 且父节点
   也有 helper 的 bake 目标"、`Q_sh_recursive_gate2` 的 NORMAL-inherit-mode 递归子节点等）。
   `C_bake_t_const` 的 else 分支在公开 API 下不可达。
5. 非 ASCII 路径、多线程并发等环境因素已由批 L / 批 M 处理过。

## 5. 后续若还要开工

**没有已知的欠账了。** 若用户要求继续，可选方向（都要先做调查，别假设是缺口）：
- 把 `_analyze_ufbx/test/` 里其他 golden 生成器（不止 `hash_scene.c`）也接进来做端到端对拍。
- 扩大现有 24 个 harness 的语料规模（现在每个 harness 只挑了 12-20 个有代表性的文件）。
- 性能对拍（C 参考构建 vs C# 的耗时/分配），这是目前完全没做的一个维度。

## 6. 上手第一件事

读 `COORDINATION.md` 最后五节 + `PORTING_NOTES.md` 规则 15-19，然后**先跑
`bash tools/_scratch/battery.sh` 复现第 2 节的基线**。任何改动前先确认它仍然是绿的。
