# 接力指令：ufbx → C# 移植 S3b + S3c（场景终结与更新全链）

> 用途：把下面 `---` 之间的整段内容原样作为 prompt 交给下一个 agent（单代理完成两个范围，避免跨代理死锁）。

---

你在 C:\Workspace\ufbx-cs 工作（Windows，bash shell）。任务：把 ufbx v0.23.1（C 源 C:\Workspace\_analyze_ufbx\ufbx.c，冻结版）**剩余的场景终结（S3b）与场景更新（S3c）两段**移植为纯 C#。前一个代理在这两个范围**没有任何产出**（18766-19366 已有、19366-23935 为空），你是唯一负责人；其他并行代理已完成 S4a/S4c，不要碰它们的文件。

## 必读（动手前按序读完，约定是硬性的）
1. C:\Workspace\ufbx-cs\PORTING_NOTES.md 全文（命名/类型映射、行为保真、「C 指针序 ≡ 分配序」、DOM/数组约定、错误形态口径、禁止事项）
2. C:\Workspace\ufbx-cs\COORDINATION.md 全文，重点最后三节（S1/S2 收口、错误形态审计、S3 开工）与本文件末尾的「既有接口速查」
3. 已落地代码的实际形态：src/Ufbx.NET/Parse/{SceneBuild.cs（S3a，已含 pre_finalize_scene/resolve_connections/linearize_nodes/fetch_*/sort_*/finalize_mesh，**直接调用**）、SceneOpts.cs（S4a：TransformToAxes/ScaleUnits/FindCubicBezierT/AxisMatrix/evaluate_skinning 已落地）、GeometryCache.cs（S4a：cache/external files）、Topology.cs + Subdivide.cs（S4c：compute_topology/generate_normal_mapping/compute_normals/tessellate/subdivide/generate_indices/**FinalizeMeshMaterial**）}、Parse/Load.cs（seam 在 `ufbxi_finalize_scene`）、Parse/UfbxiContext*.cs（partial 约定）

## 你的移植范围（C ufbx.c）
### A. S3b：19366-21643（场景终结中段）
按序：
`ufbxi_update_factor`(20095)、**`ufbxi_fetch_maps`(20123-20242)**、`ufbxi_add_constraint_prop`(20243)、`ufbxi_finalize_nurbs_basis`(20267)、`ufbxi_finalize_lod_group`(20316)、**`ufbxi_generate_normals`(20366-20406)**、`ufbxi_push_prop_prefix`(20407)、`ufbxi_shader_texture_find_prefix`(20431)、`ufbxi_update_shader_texture`(20498)、`ufbxi_finalize_shader_texture`(20539)、`ufbxi_propagate_main_textures`(20694)、`ufbxi_insert_texture_file`(20759)、`ufbxi_pop_texture_files`(20804)、`ufbxi_ordered_texture_less_texture`(20826)、`ufbxi_ordered_texture_less_order`(20833)、`ufbxi_deduplicate_textures`(20840)、`ufbxi_fetch_file_textures`(20878)、`ufbxi_get_geometry_transform_node`(21011)、`ufbxi_mirror_vec3_list`(21020)、`ufbxi_scale_vec3_list`(21035)、`ufbxi_transform_vec3_list`(21051)、`ufbxi_normalize_vec3_list`(21065)、`ufbxi_flip_attrib_winding`(21075)、`ufbxi_flip_winding`(21111)、**`ufbxi_modify_geometry`(21111-21335)**、`ufbxi_postprocess_scene`(21336)、`ufbxi_next_path_segment`(21360)、`ufbxi_absolute_to_relative_path`(21370)、`ufbxi_resolve_filenames`(21440)、`ufbxi_file_content_less`(21455)、`ufbxi_sort_file_contents`(21461)、`ufbxi_push_file_content`(21469)、`ufbxi_fetch_file_content`(21479)、`ufbxi_resolve_file_content`(21510)、`ufbxi_validate_indices`(21533)、`ufbxi_material_part_usage_less`(21551)。
**注意**：`ufbxi_finalize_mesh_material`(21564-21643) **已由 S4c 移植**在 `Parse/Subdivide.cs:531`（`UfbxiSubdivide.FinalizeMeshMaterial(ufbx_mesh)`）——**不要重复移植**，直接调用。
（19366-20094 是无名区间，先读源码确认内容并逐函数移植，不要跳过。）

### B. S3c：21632-23935（finalize_scene + 更新全链）
按序：
`ufbxi_push_anim`(21632)、**`ufbxi_finalize_scene`(21644-22631，大函数)**、数学/变换辅助：`add_translate`(22631)、`sub_translate`(22638)、`mul_scale`(22645)、`mul_scale_real`(22655)、`mul_quat`(22665)、`add_weighted_vec3/quat/mat`(22675-22698)、`mul_rotate`(22698)、`mul_rotate_quat`(22714)、`mul_inv_rotate`(22729)、`mirror_translation`(22748)、`mirror_rotation`(22754)、`get_geometry_transform`(22761)、`get_rotation`(22789)、`get_scale`(22820)、`get_transform`(22839)、`get_texture_transform`(22910)、`get_constraint_transform`(22941)、`update_node`(22958)、`update_light`(23047)、`update_camera`(23087)、`update_bone`(23257)、`update_line_curve`(23269)、`update_pose`(23274)、`update_skin_cluster`(23292)、`update_blend_channel`(23302)、`update_material`(23347)、`update_texture`(23354)、`update_anim_stack`(23374)、`update_display_layer`(23393)、`find_bool3`(23400)、`update_constraint`(23419)、`update_anim`(23493)、`update_initial_clusters`(23526)、`find_axis`(23624)、`update_adjust_transforms`(23679)、**`update_scene`(23809)**、`update_scene_metadata`(23872)、`round_if_near`(23892)、`update_scene_settings`(23906)。
**注意（三处接口，别搞错）**：
- `ufbxi_axis_matrix`(23659-23677)：**未实现**，S4a 在 `Parse/SceneOpts.cs:161-164` 留了桩并给了签名 `static bool AxisMatrix(ref UfbxMatrix mat, UfbxCoordinateAxes src, UfbxCoordinateAxes dst)`（返回 C 的 bool「是否改变」）。你需要实现它（归你的 S3c 范围），建议实现在 `Parse/SceneUpdate.cs`，并把 `SceneOpts.cs` 那个桩体改成一行调用（`return UfbxiSceneUpdate.AxisMatrix(ref mat, src, dst);`）——**这是允许的最小 diff**。golden 的 `target_axes=right_handed_y_up` 会走到它。
- `ufbxi_round_if_near`(23892) 与 `ufbxi_mirror_matrix*`(23500-23519) **已在 `Parse/SceneOpts.cs` 落地**（`RoundIfNear` 在 52 行、`MirrorMatrix*` 在 107-150 行）——不要重复移植，直接调用。
- `Parse/SceneOpts.cs:370` 的 `ufbx_compute_topology` 桩：S4c 已在 `Parse/Topology.cs:1110` 落地 `UfbxTopology.ComputeTopology(mesh, topo, numTopo)`（含 `CatchComputeTopology`/`GenerateNormalMapping`/`CatchGenerateNormalMapping`/`ComputeNormals`/`CatchComputeNormals`），把桩体替换为调用即可（同样是最小 diff）。
范围外（S4a/S4b，遇到就桩化+报告）：`ufbxi_update_scene_settings_obj`(23936)、cache/external files(23936-24953)、`ufbxi_evaluate_*`(26078+)。

## 文件所有权（只能写这些）
- `src/Ufbx.NET/Parse/SceneFinalize.cs`（新建；S3b 主体）
- `src/Ufbx.NET/Parse/UfbxiContext.SceneFinalize.cs`（新建，`internal sealed partial class UfbxiContext`，append 本模块字段；先读 Parse/UfbxiContext.cs 与既有 partial 避免重名）
- `src/Ufbx.NET/Parse/SceneUpdate.cs`（新建；S3c 主体）
- `src/Ufbx.NET/Parse/UfbxiContext.SceneUpdate.cs`（新建，同上）
- **允许改的既有文件（仅这三处，最小 diff）**：
  - `src/Ufbx.NET/Parse/Load.cs` 的 `UfbxiToplevel.SceneBuild` seam —— 按 C 的 `ufbxi_load_imp` 尾段（ufbx.c:25328-25353）把驱动序列接完整：
  `PreFinalizeScene(uc)` → `UfbxiSceneFinalize.FinalizeScene(uc)` → `UfbxiSceneUpdate.UpdateSceneSettings(uc.Scene.Settings)` →（OBJ 分支 `UpdateSceneSettingsObj`）→（opts.target_axes 有效则 `UfbxiSceneOpts.TransformToAxes`）→（opts.target_unit_meters > 0 则 `UfbxiSceneOpts.ScaleUnits`）→ `UfbxiSceneUpdate.UpdateAdjustTransforms` → `UfbxiSceneFinalize.ModifyGeometry(uc)` → `UfbxiSceneFinalize.PostprocessScene(uc)` → `UfbxiSceneUpdate.UpdateScene(uc.Scene, true, null, 0)` →（`!uc.Scene.Anim` 时补 zero anim）→（opts.load_external_files 则 `UfbxiGeometryCache.LoadExternalFiles`）→（opts.evaluate_skinning 则 `UfbxiSceneOpts.EvaluateSkinning(...)`，**先接 S4a 已有的方法**）→ 之后由编排者接 warnings/metadata 收尾。改动用 `cp` 备份 + `cmp` 确认只改这一处函数体。
  - `src/Ufbx.NET/Parse/SceneOpts.cs:161-164` 的 `AxisMatrix` 桩体 → 一行调用你在 SceneUpdate.cs 的实现（见上文「注意（三处接口）」）。
  - `src/Ufbx.NET/Parse/SceneOpts.cs:370` 的 `ufbx_compute_topology` 桩体 → `UfbxTopology.ComputeTopology(...)`。若 evaluate_skinning 主体还有别的 S4c 桩（`GenerateNormalMapping`/`ComputeNormals`），一并接上；接不上的在报告里列出。
- **禁止改**：`SceneOpts.cs` 的**其余任何部分**（仅允许上述两处桩体；若发现 SceneOpts.cs 内还有别的桩属于你的范围，在报告里列出后由编排者决定）、SceneBuild.cs、GeometryCache.cs、Topology.cs、Subdivide.cs、ReadElement.cs、Root.cs、Geometry.cs、Objects.cs、Legacy.cs、AnimReader.cs、ObjLoader.cs、SceneFiles.cs、UfbxiContext.cs 本体、Model/**、Math/**、Util/**、tools/**、tests/**（`tests/Ufbx.NET.Tests/SceneProvider.cs` 例外，见「验证 5」）。
- 若 S4a/S4c 文件里的桩需要接线（例如 `SceneOpts.cs:370` 的 `ufbx_compute_topology` 桩），**不要自己改**——在报告里列出「文件:行 → 应改为调用 `UfbxTopology.ComputeTopology(...)`」，由编排者统一接。

## 关键口径（违反必挂）
- **指针序 ⟺ ElementId 序**；字符串指针序只在同 pool 内；map（prop_type_map/group_map/texture_file_map 等）**只查找不迭代**。
- **哈希敏感顺序**：`fetch_maps` 的 map 物化顺序、shader texture 的 prefix 匹配与去重（`deduplicate_textures`/`finalize_shader_texture`/`propagate_main_textures`）、`file_contents` 排序、`finalize_scene` 里各类 ptr 数组顺序、`update_scene` 的遍历顺序——全部进 golden，逐行照抄比较子与 tie-break。
- **modify_geometry / update_scene 是浮点密集**：`transform_vec3_list`/`normalize_vec3_list`/`update_factor`/`get_transform`（矩阵分解）等必须用 `UfbxMath`，禁止 `System.Math/MathF`，禁止收缩/重排求值顺序（FMA 站点用 `UfbxMath.Fma` 标记）。
- **golden 的 opts**（`test/hash_scene.c`）：`load_external_files=true`、`ignore_missing_external_files=true`、`evaluate_caches=true`、`evaluate_skinning=true`、`target_axes=right_handed_y_up`、`target_unit_meters=1.0`，frame>0 走 `ufbx_evaluate_scene`。⇒ `generate_normals` 走的是 `opts.generate_missing_normals`（默认 false，不跑），但 **transform_to_axes/scale_units/evaluate_skinning 会跑**，你的 `ModifyGeometry`/`UpdateScene` 必须在这条路径上正确。
- 错误站点：plain `ufbxi_check`/`ufbxi_fail` ⇒ `UfbxiFail.CheckNoDesc/FailNoDesc`（cond 文本照抄 C）；`ufbxi_check_msg`/`ufbxi_fail_msg` ⇒ `CheckMsg/FailMsg`（文案逐字）；`ufbxi_warnf` ⇒ 现有 warning 通道（Util/Warnings.cs）。
- 哨兵索引数组（`UfbxiGeometry.SentinelIndexZero/Consecutive`）：真实 count 在 `mesh.NumFaces/NumIndices`，识别必须用 `ReferenceEquals`（C 19284-19291 指针识别、22044-22081 补丁）。
- 每个函数注释标 ufbx.c 行号；netstandard2.1/C#9；禁止 record/init。

## 构建与验证（硬性）
1. 构建：`dotnet build src/Ufbx.NET/Ufbx.NET.csproj -c Release` 0 warning 0 error；结束时 `dotnet build ufbx.net.sln -c Release` 全绿。
2. **回归必须保持全绿**（改 seam 前先跑一次作基线，接线后再跑）：
   - `dotnet run --project tools/LoadCheck -c Release` ⇒ `LOAD CHECK PASS`、`divergent records: 0`
   - `dotnet run --project tools/GraphCheck -c Release -- tools/graph_oracle.txt` ⇒ FAIL 0（接线后 `stopped at ufbxi_pre/finalize_scene` 计数应显著下降，报告前后对比）
   - `dotnet run --project tools/DomCheck -c Release -- tools/dom_oracle.txt` ⇒ PASS（账本 6 条 KNOWN 不许删）
   - `dotnet run --project tests/Ufbx.NET.Tests -c Release -- mathvec tools/math_vectors.txt` ⇒ 72000/72000
3. **自证 oracle（硬性）**：写 `tools/s3bc_oracle.c`（复用 `tools/s3_oracle.c` / `tools/s4a_oracle.c` 的 `#include "ufbx.c"` 骨架；编译必须 `zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx tools/s3bc_oracle.c -o tools/s3bc_oracle.exe`，**严禁**省略参数）对 ≥10 个真实文件（从 C:\Workspace\_analyze_ufbx\data 里挑 ASCII + 二进制 + 有纹理/材质的）dump：`fetch_maps` 后的 material.textures 顺序与文件内容列表、`finalize_scene` 后的 node 层级/顺序、`update_node` 的 local/world transform 位模式、`update_scene` 后 mesh 的几何变换结果（顶点 hex）。建隔离工程 `tools/S3bcCheck/`（模板抄 `tools/S4aCheck/`）逐位对拍，验收全过。
4. **变异对照（≥1）**：改一处（如 `update_factor` 的权重顺序、或 `ordered_texture_less` 的比较方向）⇒ 你的 oracle 必须 FAIL，然后还原。
5. **端到端首跑**：接线完成后，把 `tests/Ufbx.NET.Tests/SceneProvider.cs` 的 `Load` 实现成镜像 `load_scene()`（C:\Workspace\_analyze_ufbx\test\hash_scene.c:97-146：opts 同上 + frame>0 走 `UfbxApi` 的 evaluate；文件里注释已写明契约）——**这是本任务唯一允许改的 tests/ 文件**，然后在 `dotnet run --project tests/Ufbx.NET.Tests -c Release -- goldens tools/golden_hashes.txt` 上跑一次，把 **PASS/FAIL 计数与前 20 条不匹配样本**写进报告（未达 100% 是正常的，S4b evaluate 未落地；但 frame=0 的条目应当开始出现匹配，这是 S3c 正确性的最强信号）。

## 报告格式
1. 实现函数清单（C# 名 ↔ ufbx.c 行号），分 S3b/S3c 两段；
2. 新增/修改文件清单（含行数）与 `UfbxiContext.*.cs` 追加字段；
3. 三项 harness 前后对比（LoadCheck/GraphCheck/DomCheck + mathvec）；
4. s3bc oracle 结果与变异对照结果；
5. golden 首跑结果（PASS/FAIL 计数、前 20 条不匹配样本、按 frame=0 / frame>0 分组统计）；
6. 发现的行为陷阱（逐条，注明 ufbx.c 行号）；
7. 需要编排者处理的桩/接线清单（文件:行 → 建议调用），含 `SceneOpts.cs:370` 的 compute_topology 桩（应接 `UfbxTopology.ComputeTopology`）。

---

## 既有接口速查（省得你再翻）

| 你要用的 | 位置 |
|---|---|
| `UfbxiSceneBuild.PreFinalizeScene(uc)` / `ResolveConnections` / `LinearizeNodes` / `Fetch*` / `Sort*` / `FinalizeMesh` | Parse/SceneBuild.cs |
| `UfbxiSceneOpts.TransformToAxes(uc, dstAxes)` / `ScaleUnits(uc, meters)` / `FindCubicBezierT(p1,p2,x0)` / `AxisMatrix(ref m, src, dst)` / `MirrorMatrix*` / `RoundIfNear` / `EvaluateSkinning(...)` | Parse/SceneOpts.cs |
| `UfbxiGeometryCache.*`（cache 解析、external files） | Parse/GeometryCache.cs |
| `UfbxTopology.ComputeTopology(mesh, topo, numTopo)` / `GenerateNormalMapping(...)` / `ComputeNormals(...)` / `Catch*` / `TriangulateFace` | Parse/Topology.cs |
| `UfbxiSubdivide.FinalizeMeshMaterial(mesh)` / `SubdivideMesh*` / `TessellateNurbs*` / `GenerateIndicesImp` | Parse/Subdivide.cs |
| `UfbxiObj.ObjLoad(uc)` / `MtlLoad(uc)` | Parse/ObjLoader.cs |
| `UfbxiSceneFiles.*`（路径解析/open_file） | Parse/SceneFiles.cs |
| 错误形态 `UfbxiFail.CheckNoDesc/FailNoDesc/CheckMsg/FailMsg` | Parse/Error.cs |
| 现有 oracle 骨架 | tools/{s3_oracle.c,s4a_oracle.c,s4c_oracle.c} + tools/{S3SceneCheck,S4aCheck,S4cCheck}/ |
