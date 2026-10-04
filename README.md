# ufbx-cs

**ufbx v0.23.1 的纯 C# 移植** —— 无原生依赖、无 P/Invoke、无第三方包，
目标框架 `netstandard2.1`（内部仅在指针对齐读取处使用 `unsafe`）。

---

## 1. 移植来源

| 项 | 值 |
|---|---|
| 上游项目 | [ufbx](https://github.com/ufbx/ufbx) —— 单文件 FBX 加载库 |
| 上游作者 | Samuli Raivio (c) 2020 |
| 移植版本 | **v0.23.1**（`UFBX_HEADER_VERSION == ufbx_pack_version(0, 23, 1)`，参考树同值；`UFBX_SOURCE_VERSION` 与它是两个独立宏，本端口照样分列） |
| 移植依据的源文件 | `ufbx.c`（33 212 行）+ `ufbx.h`（6 073 行），逐句移植，绝大多数函数在注释里标注了对应的 `ufbx.c:行号` |
| 参考树位置 | `C:/Workspace/_analyze_ufbx` —— **只读，且不在本仓库内**（体积与许可的原因不予 vendoring） |
| 上游许可 | MIT / Public Domain (Unlicense) 二选一 |
| 本仓库许可 | 与上游完全相同，见 [`LICENSE`](LICENSE) |
| 移植产物规模 | `src/Ufbx` 88 个 `.cs`、约 47 100 行 |

移植不是"照 API 重写一遍"，而是**逐条语义对齐**：C 的 `ufbxi_check` / `ufbxi_fail_msg` 错误口径、
`arena` 分配失败路径、`union` 基类指针技巧、`f` 后缀向 `ufbx_real` 的拓宽、`zlib/inflate` 错误码、
以及 UB 站点的处理，全部按 ufbx.c 原文搬过来，绝大多数位置在注释里标了对应的 `ufbx.c:行号`。

---

## 2. 完成状态

| 口径 | 数值 | 依据 |
|---|---|---|
| 公开 ABI 门面 | **100%（114/114）** | 人工核定的 ABI 账本（见 `COORDINATION.md`），**不要信 `tools/_scratch/abi_scan.sh` 的自动扫描** |
| C 函数体映射 | **约 97%（856/883）** | ufbx.c 列首函数定义 883 个，端口有对应体 856 个；差的 27 个几乎全是 arena/分配器内部（`PORTING_NOTES.md` 规则 4 明确不移植） |
| 端到端行为保真 | **100%** | golden 哈希 **2179/2179** 与 C 版 `test/hash_scene.exe` 逐位相同、0 载入错误；24 个差分 harness 全部 0 分歧 |
| **综合** | **约 95%** | 剩下的是"按构造豁免"与"已实现但未被差分证据钉住的分支"，**不是没写** |

最近一波全量回归（3 分 42 秒，`exit=0`，存证在 `tools/_scratch/battery_after_R.txt`）：

```
goldens:      2179 files, 2179 matched, 0 mismatched, 0 load-errors
ShCheck:      44716 records / 0        PivotCheck:    4488 / 0
BakeCheck:    53083 / 0                SkinCheck:    1536 / 0
PoolCheck:     1688 / 0                StreamCheck: 11395 / 0
CreateAnimCheck: 13676 / 0             dom / graph / load / s2geom: all PASS
numeric 280169 · s4c 332068 · mathvec 72000 · s3scene 13546 · util 7003
```

唯一的红项是 `streamcheck: 2243020 checks, 3 failed`（truncated type / io type）—— 这是另一条线的
既存问题，与移植主体无关，历次波次都刻意没有动它。

---

## 3. 目录结构

```
src/Ufbx/                 ← 移植本体（唯一需要关心的产物）
    Api/                  ← 公开入口：Load*/Evaluate*/Bake*/ ThreadPool / Error / Inflate
    Math/                 ← UfbxMath，移植自 upstream `extra/ufbx_math.c`
    Model/                ← ufbx.h 的数据模型，按元素类别拆分
        Elements/         ← Mesh / Node / Skin / Animation ...
        Opts/             ← UfbxLoadOpts 等，含 C 语义的 ==0 默认值
    Parse/                ← Binary / Ascii tokenizer / Inflate / Error / Stream
    Properties/           ← P 属性表
    Util/                 ← ErrorFormat 等
    Enums.cs Types.cs ...
    Ufbx.csproj           ← TargetFramework netstandard2.1, LangVersion 9.0, AllowUnsafeBlocks

tests/Ufbx.Tests/         ← golden 对比（HashScene.cs）+ 自查 CLI（Program.cs）

tools/                    ← 差分验证工装（不是交付物的一部分，见 §5）
    <X>Oracle.c           ← C 侧期望值生成器：#include "ufbx.c" + test/hash_scene.h
    <X>Check/             ← C# 侧复现器，把 Oracle 的记录原样重放并比对
    <x>_oracle.txt        ← Oracle 输出（大体积生成物，**不入仓库**，见 §5）
    <x>_corpus.txt        ← 该 harness 选用的语料清单
    _mut_*.sh             ← 变异对照脚本（字节级打补丁 + cmp 还原证明）
    _mut_*_cases.txt      ← 变异用例表
    _mut_*_sweep_*.txt    ← 变异扫描结果 = "这行代码真的被差分证据钉住了"的凭证
    synth/                ← 手工搓出来的合成 FBX，补现实语料覆盖不到的分支
    _scratch/             ← 探针源码、交接文档、battery.sh

COORDINATION.md           ← 进度台账与所有权：每一波做了什么、结论是什么
PORTING_NOTES.md          ← **先读这个**：19 条硬性移植约定（命名/类型/浮点/错误/流/线程池...）
HANDOFF_S3bc.md           ← 早期一波的详细交接
```

---

## 4. 构建与使用

```bash
dotnet build ufbx-cs.sln -c Release
```

作为库引用 `src/Ufbx/Ufbx.csproj` 即可，命名空间统一为 `Ufbx`：

```csharp
using Ufbx;

var error = new UfbxError();
var opts  = new UfbxLoadOpts();
UfbxScene scene = UfbxApi.LoadFile("model.fbx", opts, error);
if (scene == null)
{
    // 与 C 的 `ufbx_error` 同口径：type 由 description 反推得到
    Console.Error.WriteLine($"{error.Type}: {error.Description}");
    return;
}

Console.WriteLine($"nodes={scene.Nodes.Length} meshes={scene.Meshes.Length} root={scene.RootNode?.Name}");
```

约定要点：`opts` 是引用类型但**语义是按值传入**（`PORTING_NOTES.md` 规则 9，C 里 `*user_opts` 会被拷到本地副本后再改），
端口在各入口做了 `CopyOpts`，调用返回后你传进去的对象字段保持原样。

---

## 5. 怎么验证正确性

三层，一层比一层严。前两层是"C 说什么，C# 说什么，逐条比对"；第三层回答"这条比对真的有能力区分吗"。

### 第 1 层：golden 哈希（端到端）

`upstream test/hash_scene.c` 会对整个场景做 FNV-1a-64 哈希。用参考树编译出 `test/hash_scene.exe`，
跑遍 `data/` 下的 2179 个文件得到 `tools/golden_hashes.txt`；C# 侧用 `tests/Ufbx.Tests` 复算同一个哈希：

```bash
dotnet run --project tests/Ufbx.Tests -c Release -- goldens tools/golden_hashes.txt
# => goldens: 2179 files, 2179 matched, 0 mismatched, 0 load-errors
```

`tools/gen_golden.sh` 负责重新生成基线（需要参考树里已构建好的 `test/hash_scene.exe`）。

### 第 2 层：24 个差分 harness（定位级）

golden 只能看到"整场景哈希"，看不全（举例：`test/hash_scene.c` 传给 `ufbx_evaluate_scene()` 的是 NULL，
所以 evaluate 侧任何分支一个 golden 都到不了）。每个 harness 因此自己吐出含 **IEEE 位模式、typed-id、
父指针、error code** 的定位记录：C 的 oracle 打一份期望值，C# 侧重放同一份输入，逐条比。

```bash
dotnet run --project tools/ShCheck -c Release -- tools/sh_oracle.txt tools/sh_corpus.txt
```

### 第 3 层：变异对照（武器是否有效）

删一行端口代码，差分 harness **必须**报错；不报错 = 那条代码没人管着（`_mut_*_sweep_*.txt` 里记为 `inert`）。
每个变异用 python 做字节级替换（perl 已不在这台机器的 PATH 上，早年脚本因此静默失效，
见 `PORTING_NOTES.md` 规则 18），跑完必须从备份还原并用 `cmp` 证明逐字节相同：

```bash
bash tools/_mut_sh.sh <tag> <repo-relative-path> <old> <new>
```

### 一键回归

```bash
bash tools/_scratch/battery.sh            # 全部
bash tools/_scratch/battery.sh bake       # 只看 label 含 bake 的
```

> **注意**：`battery.sh` 和各 harness 把 `C:/Workspace/ufbx-cs` 与 `C:/Workspace/_analyze_ufbx`
> 两个绝对路径写死在了源码与脚本里。换机器需要先全局替换这两个路径。
> `*_oracle.txt` 不进仓库（单个最大 44 MB），需要用 oracle 重建——标准编译命令：
>
> ```bash
> zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
>        tools/<x>_oracle.c -o tools/<x>_oracle.exe
> ```
>
> 触及 `sin/cos/atan2/pow` 的追加 `-DUFBX_EXTERNAL_MATH` 与 `extra/ufbx_math.c`。
> （`-mcpu` 只认 `x86_64`，写成 `x86-64` 会报错。）

由于 MSYS 会重编码非 ASCII 的 argv，语料一律走 `--list <corpus.txt>` 文件传入；
C# 侧按**原始字节**读（`UfbxiRawStr`：1 char == 1 byte），不要改成 `File.ReadLines()`。

---

## 6. 已知边界（按构造豁免，不是 bug）

1. **规则 4**：不移植 arena / 分配器内部（`ufbxi_alloc_size`、`ufbxi_push_size_*`、`ufbxi_free_*`、
   `ufbxi_realloc_size`）。托管 GC 没有对应物，这也是 C 函数体映射停在 97% 的唯一主要原因。
2. **规则 13**（流与 IO）：过量供给的 `read`、`ufbx_open_memory` 的二次 `Close`
   （C 是 double free）、分配失败、非 `_WIN32` 的 `fopen` 分支、`UFBX_NO_STDIO`。
3. **规则 14**（线程池）：`run_fn` 部分执行后留下的未完成目标缓冲是 arena 未初始化字节 —— **C 自己跑两次
   哈希都不一样**，无 fidelity 可言；另有真实并发交错顺序、非法/已释放的 `ctx`（C 是野写）。
4. **规则 18**：`ufbx_quat_fix_antipodal` 在默认 bake 选项下不可达 —— 这是用打过补丁的 ufbx.c 做 A/B 差分
   实测出来的，不是估算出来的。

> 动工前请先读 `PORTING_NOTES.md`。里面 19 条规则每一条都是**被失败钉出来的**：比如默认
> `INHERIT_MODE_HANDLING_PRESERVE`（== 0）下 690 个 FBX 建 0 个 scale helper，所以某些区块 golden
> 一辈子都够不到；又比如"探针的判定谓词必须和被测代码的条件逐字同形"，否则会把 coverage gap 判成"No gap"。

---

## 7. 后续可做

`PORTING_NOTES.md` 与 `COORDINATION.md` 之外没有已知的欠账了。若要继续加深覆盖，三个方向：

- 把 `_analyze_ufbx/test/` 里除 `hash_scene.c` 之外的 golden 生成器也接进来做端到端对拍；
- 扩大现有 24 个 harness 的语料规模（现在每个只挑了 12–20 个有代表性的文件）；
- **性能对拍**（C 参考构建 vs C# 的耗时/分配）—— 这个维度目前完全是空白。

---

## 8. 许可

MIT / Public Domain 二选一，与上游 ufbx 完全一致，详见 [`LICENSE`](LICENSE)。
上游版权归 Samuli Raivio (c) 2020 所有，本移植保留其版权声明。
