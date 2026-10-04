# S4a 移植报告：geometry cache + 外部文件 + 坐标轴/单位转换 + skinning 求值入口

范围（ufbx v0.23.1，冻结源 `C:\Workspace\_analyze_ufbx\ufbx.c`）：23936-24953（geometry cache +
external files）、24954-25211（axes/units/bezier/warning）、25063-25177（`ufbxi_evaluate_skinning`
时 t=0 求值入口）、31936-32103（`ufbx_get_skin_vertex_matrix` + blend offset helpers）、
32704-32963（geometry cache 采样 API）、7301-7674（geometry cache 依赖的 XML 解析器）、
1830-1846（`ufbxi_parse_uint32_radix`）。

## 1. 实现清单（C# 名 ↔ ufbx.c 行）

### 1.1 `src/Ufbx/Parse/GeometryCache.cs`（新增，1946 行）

XML 解析器（cache_load_xml 依赖，范围外但必须自带）：

| C# | ufbx.c |
|---|---|
| `UfbxiXmlAttrib` / `UfbxiXmlTag` / `UfbxiXmlDocument` / `UfbxiXmlContext` / `UfbxiXmlOpts` | 7247-7289 |
| `UfbxiXmlCtype`（`ufbxi_xml_ctype[256]` 生成表） | 7301-7304 |
| `UfbxiXml.Refill` | 7306-7315 |
| `UfbxiXml.Advance` | 7317-7320 |
| `UfbxiXml.PushTokenChar` | 7322-7329 |
| `UfbxiXml.Accept` | 7331-7339 |
| `UfbxiXml.SkipWhile` | 7341-7346 |
| `UfbxiXml.SkipUntilString` | 7348-7380 |
| `UfbxiXml.ReadUntil` | 7382-7457 |
| `UfbxiXml.ParseTag` | 7460-7578 |
| `UfbxiXml.ParseRoot` | 7580-7604 |
| `UfbxiXml.LoadXml` | 7614-7648 |
| `UfbxiXml.FreeXml` | 7650-7654 |
| `UfbxiXml.FindChild` | 7656-7664 |
| `UfbxiXml.FindAttrib` | 7666-7674 |

基础类型与读取器：

| C# | ufbx.c |
|---|---|
| `UfbxiParseRadix.Uint32` | 1830-1846 |
| `UfbxiCacheRead.U32` | 804-812 |
| `UfbxiCacheRead.F32` | 824-829 |
| `UfbxiCacheRead.F64` | 831-836 |
| `UfbxiCacheConsts`（`ufbxi_cache_data_format_size[]`） | 24162-24164 |

geometry cache 主链（`UfbxiGeometryCacheLoader`）：

| C# | ufbx.c |
|---|---|
| `UpdateSceneSettingsObj` | 23936-23947 |
| `CacheRead` | 24040-24082 |
| `CacheSkip` | 24084-24120 |
| `McTag` / `CacheMcReadTag` / `CacheMcReadU32` / `CacheMcReadU64` | 24124-24160 |
| `CacheLoadMc` | 24166-24247 |
| `CacheLoadPc2` | 24249-24296 |
| `TmpChannelLess` | 24298-24303 |
| `CacheSortTmpChannels` | 24305-24310 |
| `CacheLoadXmlImp` | 24312-24401 |
| `CacheLoadXml` | 24403-24419 |
| `CacheLoadFile` | 24421-24444 |
| `CacheTryOpenFile` | 24446-24462 |
| `CacheLoadFrameFiles` | 24464-24547 |
| `CmpCacheFrameLess` | 24549-24559 |
| `CacheSortFrames` | 24561-24566 |
| `InterpretationNames[]` | 24568-24577 |
| `CacheSetupChannels` | 24579-24641 |
| `CacheLoadImp` | 24644-24698 |
| `CacheLoad` | 24700-24724 |
| `LoadGeometryCache` | 24726-24763 |
| `FreeGeometryCacheImp` | 24765-24769 |
| `LessExternalFile` | 24810-24819 |
| `LoadExternalCache` | 24821-24875 |
| `FindExternalFile` | 24877-24884 |
| `LoadExternalFiles` | 24886-24952 |

geometry cache 采样 API（`UfbxiGeometryCacheSample`，25063 的 `evaluate_skinning` 会调用）：

| C# | ufbx.c |
|---|---|
| `ReadGeometryCacheReal` | 32704-32867 |
| `ReadGeometryCacheVec3` | 32941-32951 |
| `SampleGeometryCacheReal` | 32869-32939 |
| `SampleGeometryCacheVec3` | 32953-32963 |
| `ufbxi_geometry_cache_buffer` 联合体 | 32696-32702（以独立 `readBuf` 字节数组 + `bufferDst` double 暂存复现） |

类型：`UfbxiGeometryCacheImp`(23953-23960)、`UfbxiCacheTmpChannel`(23964-23973)、
`UfbxiCacheXmlType`/`UfbxiCacheXmlFormat`(23975-23985)、`UfbxiCacheContext`(23987-24038)、
`UfbxiExternalFileType`(24797-24799)、`UfbxiExternalFile`(24801-24808)。

### 1.2 `src/Ufbx/Parse/SceneOpts.cs`（新增，587 行）

| C# | ufbx.c |
|---|---|
| `Pow10Targets[]` | 23883-23890 |
| `RoundIfNear` | 23892-23904 |
| `MulQuat`（= `UfbxQuat.Mul`） | 22665-22673 |
| `AddWeightedVec3` | 22675-22680 |
| `AddWeightedQuat` | 22682-22688 |
| `AddWeightedMat` | 22690-22696 |
| `MirrorMatrixDst` | 23496-23503 |
| `MirrorMatrixSrc` | 23505-23512 |
| `MirrorMatrix` | 23514-23519 |
| `AxisMatrix`（**桩**，S3c 所有） | 23659-23677 |
| `TransformToAxes` | 24954-24989 |
| `ScaleUnits` | 24991-25018 |
| `FindCubicBezierT` | 25022-25061 |
| `EvaluateSkinning` | 25063-25177 |
| `CatchGetSkinVertexMatrix`（= `ufbx_catch_get_skin_vertex_matrix`） | 31936-32026 |
| `GetSkinVertexMatrix`（C 的 inline 包装，`panic == NULL`） | 31936-32026 |
| `GetBlendShapeOffsetIndex` | 32028-32041 |
| `GetBlendShapeVertexOffset` | 32043-32048 |
| `GetBlendVertexOffset` | 32050-32068 |
| `AddBlendShapeVertexOffsets` | 32070-32089 |
| `AddBlendVertexOffsets` | 32091-32103 |
| `ResolveWarningElements` | 25195-25210 |

注：`ufbxi_fixup_opts_string`(25179-25193) 在 `src/Ufbx/Parse/Load.cs:549`（`UfbxiLoad.FixupOptsString`）
已存在且被实际 load 路径调用（Load.cs:811-814）；为避免重复定义，本范围未再复制一份。

### 1.3 `src/Ufbx/Parse/UfbxiContext.Cache.cs`（新增，27 行）

`internal sealed partial class UfbxiContext` 新增字段：

- `internal UfbxMirrorAxis MirrorAxis;` — C `ufbx_mirror_axis mirror_axis`(6489)
- `internal UfbxMatrix AxisMatrix;` — C `ufbx_matrix axis_matrix`(6497)

字段名与其它 `UfbxiContext.<Module>.cs` 无冲突。

### 1.4 `src/Ufbx/Api/UfbxGeometryCacheApi.cs`（新增，公开门面，只转发）

`ufbx.h:5711-5728` 的 6 个入口全部落到 1.1 的同一批 C 体上，转发层不含实现：

| C# | ufbx.c | 转发到 |
|---|---|---|
| `LoadGeometryCache` / `LoadGeometryCacheLen` | 24726-24763（`_len` 见 ufbx.h:5714） | `UfbxiGeometryCacheLoader.LoadGeometryCache` |
| `ReadGeometryCacheReal` | 32704-32867 | `UfbxiGeometryCacheSample.ReadGeometryCacheReal` |
| `ReadGeometryCacheVec3` | 32941-32951 | 同上（`count` 是 vec3 元素，real 数为 `count*3`） |
| `SampleGeometryCacheReal` | 32869-32939 | `UfbxiGeometryCacheSample.SampleGeometryCacheReal` |
| `SampleGeometryCacheVec3` | 32953-32963 | 同上 |

约定：NUL 结尾形态以字符串自身长度转发给 `_len` 形态，`_len` 形态先过 `UfbxiSceneFind.SafeString`(34-40)；
`ufbxi_check_opts_ptr()`(32669) 的清零哨兵对托管 options 不可表达（同 `UfbxApi.LoadMemory` 注）；
C 的 `size_t` 返回值收窄成 `int`（PORTING_NOTES「size_t → int」）。`ufbx_{free,retain}_geometry_cache`
是 `UfbxApi` 里有据可查的引用计数空操作。

### 1.5 `src/Ufbx/Api/UfbxSkinApi.cs`（新增，公开门面，只转发）

`ufbx.h:5598-5622`「Skinning」一节的 7 个入口，函数体仍全部在 1.2 的 `UfbxiSceneOpts` 里：

| C# | ufbx.c | 备注 |
|---|---|---|
| `CatchGetSkinVertexMatrix` | 31936-32026 | `ufbx_panic*` ⇒ `ref UfbxPanic`（调用方持有存储，C 不分配） |
| `GetSkinVertexMatrix` | 31936-32026 | C 是 catch 形态的 inline 包装(ufbx.h:5601-5603)，内部用 scratch panic |
| `GetBlendShapeOffsetIndex` | 32028-32041 | `vertex` 在这里**先收窄**：C 的第一件事就是 `(uint32_t)vertex`(32034) |
| `GetBlendShapeVertexOffset` / `GetBlendVertexOffset` | 32043-32048 / 32050-32068 | 同上（转调 `offset_index`） |
| `AddBlendShapeVertexOffsets` / `AddBlendVertexOffsets` | 32070-32089 / 32091-32103 | 公开形态不带 `offset`；内部的 `offset` 是大数组窗口用的，这里固定传 0 |

`size_t vertex` 在 skin 访问器里**保持 `long` 并在内部做无符号比较**（C 的 31939/31941 两处都是无符号域），
这一点由 `SKC` 的 `2^32` / `SIZE_MAX` 探针把守，见 4.3。

## 2. s4a oracle 结果

Oracle：`tools/s4a_oracle.c`（`#include "ufbx.c"`，直接调用内部 `ufbxi_*` / 公开 API）。
构建（zig，与其它 oracle 一致）：

```
zig cc -O2 -DNDEBUG -fno-strict-aliasing -std=c11 -mcpu=x86_64 -ffp-contract=off \
    -I C:/Workspace/_analyze_ufbx tools/s4a_oracle.c -o tools/s4a_oracle.exe
```

样本：`tools/s4a_inputs/`（**未写入** `_analyze_ufbx/data`）：

- `synth_a.pc2`、`synth_b.pc2` — `tools/gen_pc2.py` 合成
- `sine_mcsd_oversample/{cache.xml,cache.mc}` — OneFile/mcc
- `sine_mxmd_oversample/cache.xml` + 23 个 `cacheFrame*.mcx` — OneFilePerFrame/mcx（含 Tick 帧）
- `sine_mcmf_undersample/cache.xml` + 15 个 `cacheFrame*.mc`
- `sine_xml_parse/cache.xml` + `cacheFrame1.mc`

Oracle 输出 `tools/s4a_oracle.txt`（1086 条记录，可复现且逐字节一致）：

| 记录 | 数量 | 覆盖 |
|---|---|---|
| BZ | 560 | `FindCubicBezierT`（要求 ≥200 ✓） |
| FR | 344 | 每帧 time/format/encoding/offset/count/elem_bytes/total_bytes |
| SMP | 60 | `SampleGeometryCacheVec3` |
| RD | 35 | `ufbx_{read,sample}_geometry_cache_{real,vec3}` 合成正控制（见 4.2） |
| RDO | 35 | 上述调用返回后**调用方 opts 必须原样完好** |
| CH | 12 | 通道 name/interp/interp_name/mirror/scale/frames |
| LOAD | 7 | 7 个 cache 顶层加载（要求 ≥5 ✓） |
| SKC | 12 | `ufbx_catch_get_skin_vertex_matrix` + 活的 `ufbx_panic`（见 4.3） |
| BSI/BSV/BVO/BAV | 5/5/1/1 | blend offset helpers |
| BSA | 5 | `ufbx_add_blend_shape_vertex_offsets` 自身的四条入口（见 4.3） |
| SK | 3 | `GetSkinVertexMatrix`（线性 + 双四元数） |
| DONE | 1 | 结束标记 |

**S4aCheck 结果：`S4A CHECK PASS`，1086 / 1086 通过，0 失败。**
运行：`dotnet run --project tools/S4aCheck -c Release -- tools/s4a_oracle.txt`（从仓库根运行）。
`RD`/`RDO` 块由 `dump_read_cases()` 在语料循环**之后**输出，`SKC`/`BSA` 由 `dump_skin_catch()` /
`dump_blend_add_shape()` 紧随其后、`DONE` 之前输出，所以**前 1068 条与 1069 条时代的 baseline 逐字节相同**
（`diff --strip-trailing-cr`，oracle 产物是 CRLF，普通 `diff` 会给出假的全不同）。
harness 侧的 `cache`/`sample` 调用（`Program.cs` 的 `DoLoad`/`DoSmp`/`RunRdCase`）走 `UfbxGeometryCacheApi`，
`SK`/`SKC`/`BSI`/`BSV`/`BVO`/`BAV`/`BSA` 走 `UfbxSkinApi`，
转发层本身因此在语料记录、合成正控制两条路上同时被覆盖。

## 3. 回归结果（只读，原生工具）

```
LoadCheck  -> LOAD CHECK PASS   files=2946  files with divergence=0  divergent records=0
GraphCheck -> GRAPH CHECK PASS  FAIL (unexpected)=0  PASS (strict compared)=691
DomCheck   -> DOM CHECK PASS    files=75   files with divergence=0  divergent records=0
```

门面批 H 后补跑的整条回归（2026-10-04，会话 5）：

```
dotnet build ufbx-cs.sln -c Release                0 警告 / 0 错误
goldens tools/golden_hashes.txt                   2179 files, 2179 matched, 0 mismatched
streamcheck                                        2243020 checks, 3 failed（既有 ErrorType 三条）
S4aCheck tools/s4a_oracle.txt                      1086 / 1086, 0 fail
InflateCheck                                       4613 / 4613
S4bCheck（在 C:/Workspace/_analyze_ufbx 下）       12091 行 / 0 分歧 ALL MATCH
S4cCheck tools/s4c_oracle.txt                      332068 / 0
S3bcCheck（同上必须在 _analyze_ufbx 下跑）         1477 / 1477
S3SceneCheck 13546/0 · NumericCheck 280169 · UtilCheck 6865 · HashCheck 79
AnimCurveCheck 8000 · AsciiCheck 1737 · S2GeomCheck PASS
```

模块本身编译：`dotnet build src/Ufbx/Ufbx.csproj -c Release` → **0 警告 0 错误**。

## 4. 变异测试

### 4.1 loader 路径（既有）

变异：`CacheLoadPc2` 中 `double framesPerSample = UfbxiCacheRead.F32(header, 24)` → `(header, 20)`
（`ufbx.c:24271` 字段偏移错位一字）。

结果：`S4A CHECK FAIL`，7 条 FR/SMP 记录发散（frame time 与采样值全部偏离）。
还原后重新 `S4A CHECK PASS`。变异点已复原，源码无残留。

### 4.2 读/采样路径的六个变异（本轮新建，全部咬住、全部已还原）

语料对这条路径是暗的（每帧 ≤36 real、12 条 CH 全部 `mirror_axis == NONE`、采样只做 `count=4`），
所以 `RD`/`RDO` 是**为这些暗区专门造的正控制**；六个变异逐个把实现推回"读起来也合理但偏离 C"的写法：

| 变异 | 失败条数 | 观察到的量 |
|---|---|---|
| M1 分块 512 → 4096 | 32 | 只有 `call_hash` 与 `reads` 变，**`data_hash` 逐条不变** |
| M2 `MaxSkipSize` → 0x7fffffff | 1 | case 22 的 `skips` 从 4 变 2 |
| M3 vec3 尾向量循环回退成 `vec3Read*3` | 2 | case 12/13 的 `data_hash` |
| M4 opts 改回别名共享 | 3 | `RDO` case 29/33/34：`additive`/`use_weight`/`weight` 泄漏回调用方 |
| M5 去掉 `mirror_ix -= num_read` re-base | 25 | mirror 案例的 `data_hash` |
| M6 去掉 endian swap | 52 | RD 的 BE 案例 **+ 既有 SMP 记录**（语料 `.mc` 是大端） |

M1 那行就是本轮最重要的一条实证：**分块粒度在解码值里不可见**（`mirror_ix` re-base 保住全局 mod-3 相位，
被取负的元素恒为 `{g : g ≡ mirror_axis-1 (mod 3)}`），只能由调用日志的尺寸序列观察到——所以 `call_hash`
必须折入每次 `read_fn`/`skip_fn` 的请求字节数与返回字节数，而不是只折最终数据。
M4 有两种形态：直接 `opts = userOpts` 会让既有 SMP 记录（`opts == NULL`）NRE 崩在 `GeometryCache.cs:1750`，
信号有效但形态很差；改用 `userOpts ?? new UfbxGeometryCacheDataOpts()` 才能把发散隔离到 `RDO` 的字段上。
M6 顺带说明 endian swap 早已由 SMP 覆盖，本轮补的是 float/double 两条路径与合成 BE 布局。

全部还原后：`S4A CHECK PASS`（1069/1069），`grep` 确认源码无 `M1`…`M6` 残留。

### 4.3 skinning 公开形态的九个变异（门面批 H，`SKC`/`BSA` 正控制）

`SKC`/`BSA` 是为本节两处**真实缺陷**（缺 panic 站点、`size_t vertex` 的有/无符号）造的正控制；
九个变异逐个把实现推回"看起来合理但偏离 C"的写法，全部已还原：

| 变异 | 失败条数 | 观察到的量 |
|---|---|---|
| MH1 边界测试先收窄成 `(uint)vertex` | 1 | 只有 `SKC 8`（`2^32`）——`SKC 7/9` 的消息列写的是完整 `ulong`，分不开 |
| MH2 删掉 `ufbxi_panicf` 站点 | 5 | `SKC 5-9` 的 `did/msglen/msg` 三列（返回值仍是 identity，看不见） |
| MH3 `%zu` 实参改喂 `AddUInt` | 3 | `SKC 7/8/9` 消息里的数字（`18446744073709551615` → `4294967295`…） |
| MH4 去掉 `Panicf` 的 `did_panic` 提前返回 | 1 | `SKC 11`（预置 panic + 越界）——这条早退在该探针之前**无任何覆盖** |
| MH5 `total_weight <= 0` 改成 `< 0` | 3 | `SKC 0/1/10`：fallback/identity 被零矩阵替代 |
| MH6 `fabs(total) > EPSILON` 改成 `>=` | 1 | `SKC 3`（权重恰为 double 的 `UFBX_EPSILON`，`rcp_weight` 从 0 变成 `6.7e153`） |
| MH7 `index < num_vertices` 改成 `index < vertices.Length` | 1 | `BSA 2`（`nv=4`、缓冲长 6，第 5 号顶点被越界写入） |
| MH8 `offset_weights` 无条件施加（`weights[i % count]`） | 3 | `BSA 0/3` **+ 既有 `BAV`**（说明这条乘法原本只在 BAV 的合并结果里被动覆盖） |
| MH9 删掉 `vertices == null` 提前返回 | 崩溃 | `BSA 4` NRE，harness 以 `EXCEPTION` 中止（信号有效、形态差） |

**已知暗区（诚实登记）**：删掉 `if (weight == 0.0) return;` **完全不可观测**——`1086/1086` 仍全绿。
`BSA 1` 的缓冲已清零，`0.0 * offset` 加进去逐位还是 `+0.0`（`-0.0` 也一样，`0.0 + -0.0 = +0.0`），
所以那条判断是纯快路径而非语义；不要把它当成已覆盖的行为。

全部还原：`Parse/SceneOpts.cs`、`Util/Print.cs` 与变异前备份 `diff` 逐字节相同，
`S4A CHECK PASS`（**1086/1086**）。

## 5. S4c / S3c 桩的接线点

本范围内为实现 golden 正确性所需的、属于其它代理的未移植函数，以
`UfbxiReaderNotPortedException` 精确桩出，并在桩文本里写明归属与行号：

1. `UfbxiSceneOpts.AxisMatrix` → `throw new UfbxiReaderNotPortedException("s4a-ufbxi_axis_matrix (S3c-owned, ufbx.c:23659)")`
   - 位置：`src/Ufbx/Parse/SceneOpts.cs:163`
   - 被 `TransformToAxes`(24954) 与 `UpdateAdjustTransforms`(S3c, 23682) 调用。
   - S3c 移植后：用真实实现替换此桩，其余调用点无需改动。
2. `UfbxiSceneOpts.EvaluateSkinning` 内的 `ufbx_compute_topology` 调用点 →
   `throw new UfbxiReaderNotPortedException("s4a-ufbx_compute_topology (S4c-owned, ufbx.c:33176)")`
   - 位置：`src/Ufbx/Parse/SceneOpts.cs:370`
   - 对应 C 的 `if (!cached_normals) { ufbx_compute_topology(...); ufbx_generate_normal_mapping(...); }`
     分支（仅当 mesh 无缓存法线、需要重算法线时进入）。S4c 移植 `ufbx_compute_topology`、
     `ufbx_generate_normal_mapping`(32588)、`ufbx_compute_normals`(32622) 后，用真实调用替换。

`EvaluateSkinning` 本体（position/offset 逐顶点 blend、skin matrix、双四元数、cache 采样）
已完整移植，未桩化；仅上述 S4c 分支保留桩。

**编排者接线点**（本范围入口，尚未接入 `SceneBuild`，与 COORDINATION.md 约定一致）：

- `UfbxiSceneOpts.EvaluateSkinning(scene, error, time, loadCaches, cacheOpts)` ← C `ufbxi_evaluate_skinning`(25063)
- `UfbxiSceneOpts.TransformToAxes(ref uc, ref transform, dstAxes)` ← 24954
- `UfbxiSceneOpts.ScaleUnits(ref uc, ref transform, unitMeters)` ← 24991
- `UfbxiGeometryCacheLoader.LoadExternalFiles(uc, begin, count)` ← 24886
- `UfbxiSceneOpts.ResolveWarningElements(uc)` ← 25195
- `UfbxGeometryCacheApi.LoadGeometryCache/LoadGeometryCacheLen`（公开 ABI，转发到
  `UfbxiGeometryCacheLoader.LoadGeometryCache`）← 24726；读/采样四个公开入口同样已由
  `UfbxGeometryCacheApi` 转发（见 1.4），不再是"仅内部可达"。
- `UfbxSkinApi.CatchGetSkinVertexMatrix/GetSkinVertexMatrix` + blend 五件（公开 ABI，转发到
  `UfbxiSceneOpts`）← 31936-32103 / ufbx.h:5598-5622（见 1.5）。注意这里公开的是**单点访问器**；
  load 时的整顶点蒙皮 `UfbxiSceneOpts.EvaluateSkinning`(25063) 仍待编排线接入 `SceneBuild`。

## 6. 发现的行为陷阱（附行号）

1. **XML token 缓冲区语义**（`ufbxi_xml_push_token_char`, ufbx.c:7322-7329）
   - C 中 `xc->tok` 是复用 scratch，`tok_len = 0` 表示回退并**就地覆写**（`tok[tok_len++] = c`）。
   - 初版移植用 `List<byte>.Add` 追加，`tok_len=0` 后仍继续 append，导致第二次 token 读到
     陈旧字节：`<?xml version="1.0"?>` 之后的 `Autodesk_Cache_File` 被打成
     `.0"?>\n Autodesk_Ca`，`FindChild` 全程失败，xml cache 全部加载成 0 通道 / 0 帧。
   - 修正：`PushTokenChar` 改为在索引 `TokLen` 覆写（必要时 grow），复现 C 的原地覆写语义。
2. **空字符串 vs NULL**（`ufbxi_cache_setup_channels`, ufbx.c:24594-24595）
   - `chan->name = frame->channel`；`frame->channel` 对 pc2 及 `chan->interpretation_name`
     初始化都是 `ufbxi_empty_char`(3370) 即**长度 0、data 非 NULL**的空串，不是 NULL。
   - oracle 的 `puts_hex` 对 `len==0` 打印 `-`；harness 若把 `-` 解码成 `null` 会与移植的
     `""` 判不等。已将 harness 的 `HexToStr("-")` 定为空串（对齐 C 空串语义）。
3. **`cc->channel_name` 初值**（`ufbxi_cache_load_imp`, ufbx.c:24649）
   - C 显式 `cc->channel_name.data = ufbxi_empty_char;`，即 pc2 的默认通道名为**空串**而非 NULL。
   - 移植设 `cc.ChannelName = ""`，与 oracle pc2 `CH 0 - …` 一致。
4. **`cc->pos/pos_end` 与 XML prefix**（`ufbxi_cache_load_xml`, ufbx.c:24409-24410）
   - 进入 XML 时窗口是刚读入的 16 字节 header 前缀，`prefix_length = pos_end - pos = 16`。
     移植用 `Window/WindowLength` 表达；实现中 `WindowLength` 未写值时退化为 0，等价于直接
     从流 refill（流位置已在 16 字节之后），行为与 C 一致——但这是一个**隐含依赖**，若将来
     改写前缀来源需注意同步 `BufferPos/WindowLength`。
5. **Frame 排序/分组的指针同一性**（`ufbxi_cmp_cache_frame_less`, ufbx.c:24549-24559；
   `ufbxi_cache_setup_channels`, 24589-24594）
   - C 用 `frame->channel.data` 指针相等来分组同通道帧并做 channel 比较（依赖字符串池驻留）。
   - 移植以字符串**引用相等**（`ReferenceEquals`）复现；`CacheLoadFrameFiles` 里每帧
     `frame.Channel` 均取自同一 `cc.ChannelName` 实例，故分组成立。若将来对通道名重新分配
     而非驻留，分组会错误。
6. **`FindCubicBezierT` 必须逐字转写**（ufbx.c:25022-25061）
   - 含 3 次手写展开的 Newton-Raphson + `eps = 8.881784197001252e-16` 提前退出 + 最多 4 次
     额外迭代。任何 `math`/提前收敛改写都会破坏 560 条 BZ 向量中的边界用例。移植逐字复刻，
     全部 BZ 通过。
7. **`UFBX_EPSILON`（double）= `1.4916681462400413e-154`**（ufbx.c:70-72；端口 `UfbxMathConsts.Epsilon`）
   - `ufbx_catch_get_skin_vertex_matrix` 的 `rcp_weight == 0` 子分支(31991)只在
     `total_weight > 0` 且 `fabs(total_weight) > UFBX_EPSILON` 为**假**时进入。这个 epsilon 比直觉小得多
     （不是 2^-60 一类），所以要用 `1.4916681462400413e-154` 字面量当权重才探得到；早期用 `2^-60` 的探针
     落进的是**归一化**分支，看起来"通过"其实什么都没测（`SKC 3`，见 4.3 MH6）。
8. **`dq_weight` 必须留 0 才不让探针失去意义**（31992-31996 + 32004-32020）
   - 一旦 `dq_weight > 0`，`rcp_weight == 0` 会把 0 乘进 dual-quaternion 归一的 `1/sqrt(0)` 路径，
     得到 `0 * infinity` 的 NaN；NaN payload 是实现相关的（CRT/libm 各异），golden 就不该依赖它。
     `SKC 3` 因此把 `dq_weight` 设为 0，只留矩阵那一支。
9. **无符号 `size_t` 边界在返回值里经常不可见**（31939/31941）
   - `2^32` 若先收窄成 `uint32_t` 会别名到 vertex 0，而 vertex 0 恰好也返回 identity ⇒ 返回值一致。
     必须把 `panic` 的 `did_panic`/`message_length`/`message` 三列打进记录，才能区分"越界 panic"与
     "静默读到合法顶点"（`SKC 7/8/9`，见 4.3 MH1/MH2）。
