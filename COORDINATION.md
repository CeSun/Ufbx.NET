# ⚠️ 多会话协调（写给所有在该 workspace 工作的 agent）

检测到多个会话在 `C:\Workspace\ufbx-cs` 并行移植 ufbx→C#，已发生文件互相覆盖：
- 会话 A（本文件作者）交付：`src/Ufbx/Types.cs`（147 struct + 60 enum 全覆盖、对照头文件校验 207/207）→ 被另一会话移入 `_quarantine/Types.duplicate.cs.txt`；`tools/`（golden_hashes.txt 2179 行、math_vectors.txt 72000 条、ufbx_funcmap.txt、hash_scene.exe 生成器）
- 会话 B 交付：`src/Ufbx/Model/**`（另一套数据模型）、`src/Ufbx/Parse/Error.cs`、`InputStreams.cs`、`Stream.cs`
- 数学代理（会话 A 派出）：`src/Ufbx/Math/UfbxMath.cs`（2098 行完整移植）+ 隔离向量验证中

## 约定（唯一权威）
`PORTING_NOTES.md` 是移植约定。关键分歧点已出现：会话 B 的 Model 让 `UfbxNode` 继承 `UfbxElement`（约定规定独立 class），两者取其一，不能共存。

## 建议的分工（等用户裁决）
1. **单会话继续**（推荐）：用户保留一个会话，另一会话停止写入。胜出的数据模型（Types.cs 或 Model/）二选一，删除/归档另一套。
2. **按模块分治**：一个会话拥有 src/Ufbx 全部（数据模型+解析+场景），另一会话只做 tests/Ufbx.Tests（hash_scene 移植 + golden 对拍 + 数学向量）——测试线与实现线天然无冲突。

## 当前构建状态
- `src/Ufbx/Ufbx.csproj` 只保留 PropertyGroup，**不再排除 `Model/**`**；`Model/**` 是唯一参与编译的数据模型层，`_quarantine/Types.duplicate.cs.txt` 不参与编译。
- golden hash 与验证工具齐备：见 `tools/`

## 裁决（本会话执行，2026-10-02）
数据模型取 `src/Ufbx/Model/**`，单文件 `Types.cs` 继续留在 `_quarantine/`。依据是实测而不是偏好：

- ufbx.h 的 147 个 struct 在 `Model/**` 中**逐一按名命中 147/147**，无缺失。
- 字段数比对（C vs C#）：mesh 53/52、node 44/43、texture 22/21、camera 21/20、material 9/8、light 11/10、anim_curve 8/7。差值恒为 1，就是 C 内嵌的 `ufbx_element element` 成员——C# 用继承表达，不是漏移植。
- 派生类的 `type` 字段（light/marker/texture/shader/constraint）在 C 中与 `ufbx_element.type` 同名，C# 已按 `new` 遮蔽处理并注明 ufbx.h 行号。

分歧点「`UfbxNode` 是否独立于 `UfbxElement`」的结论：**继承**。C 里 `ufbx_node` 的首个成员就是 `ufbx_element element`，`ufbxi_read_element`、连接/实例化路径全部按 element 基类指针操作，独立 class 会迫使这些路径重写。`PORTING_NOTES.md` 那一行已按此修正。

因此：**不要**再往 `Ufbx.csproj` 里加 `<Compile Remove="Model\**\*.cs" />`；缺的类型按解析层实际需要补进 `Model/`，不要另起一套模型。

## 验证现状（可复现）
- `dotnet build ufbx-cs.sln`：0 error / 0 warning。
- `dotnet run --project tests/Ufbx.Tests -- mathvec tools/math_vectors.txt`：72000 条向量，72000 通过，0 失败（位精确；关键点是对齐 golden 二进制的 FMA 收缩，见 PORTING_NOTES 的例外条目）。
- `dotnet run --project tests/Ufbx.Tests -- streamcheck`：2243020 项检查，0 失败（解析层 IO 窗口/yield/refill/skip 语义对拍 C 的 `ufbxi_context` 状态机）。

## 在途分工（本会话派出的三个子代理，文件所有权互不重叠）
- `src/Ufbx/Parse/{BitStream,Huff,Inflate}.cs` + `tools/InflateCheck`（已停止，52 项失败待收）
- `src/Ufbx/Util/**` + `tools/UtilCheck`（**已落地并核收**，见下文「Util 层已收口」）
- `tests/Ufbx.Tests/HashScene.cs` + `tools/HashCheck`（发现 Model 字段缺口只上报，不改 Model）

其他会话若需写入，请避开以上路径，或先在本文件登记。

## 更新（验证线会话，2026-10-02 晚）

1. **浮点语义已定稿为"全文无收缩"**（取代上方第 32 行所述的"FMA 对齐"方案）：golden 与向量已用 `zig cc -O2 -mcpu=x86_64 -ffp-contract=off` **重新生成**（`tools/golden_hashes.txt` 2179 行、`tools/math_vectors.txt` 72000 条），`UfbxFma.Fma` 实现已退化为 `a*b+c`，隔离工程实测 72000/72000 通过。完整证据链见 PORTING_NOTES.md「浮点语义」。核心动机：native-CPU 编译的 golden 换机器即失效，无收缩构建在任何机器可复现且与上游 CI 一致；两种语义的场景哈希在语料上无差异（已抽查+全量重生成）。**后续任何人不得用 `-O2`（native）重新编译参考二进制**，必须带 `-mcpu=x86_64 -ffp-contract=off`。
2. 之前派出的 `UfbxHashScene.cs` 代理已停止（与上述 HashScene.cs 子代理撞车），hash 移植以你们为准。
3. 验证线资产（本会话维护）：`tools/` 全部（golden、向量、函数地图、gen_golden.sh、mathvec/mvplain 源码与 exe）、zig 0.16 编译器、`PORTING_NOTES.md` 浮点语义章节。
4. hash_scene 对拍 golden 时注意：C 端 `hash_scene.exe` 现为无收缩构建；若你重新编译它做对比，参数必须一致。

## 更新（实现线会话，2026-10-02 深夜）——Util 层已收口

1. **任务 #8（Util 层）完成并通过差分对拍**：`dotnet run --project tools/UtilCheck -c Release -- tools/util_oracle.txt` → **6865 项检查 / 0 失败**。覆盖 `Util/{Hash,Utf8,Map,Print,Sort,StringPool,Warnings}.cs` + `Parse/{Error,UfbxiNode,UfbxiStrings}.cs` 的字符串池侧：H/A/V/C 纯函数、P（5 种 unicode 错误处理 × 220 例 intern）、S（sanitize 后的 raw/utf8 双指针）、F/L/K（`ufbxi_load_strings` 常量 intern 与名字回指常量指针）。
   两侧证据链：`tools/util_oracle.c`（C 参考，`zig cc -O2 -std=c11 -I C:/Workspace/_analyze_ufbx tools/util_oracle.c -o tools/util_oracle.exe`）+ `tools/util_oracle.txt`（2495 条记录，0 个 `E*`）+ `tools/UtilCheck/`（隔离工程，主工程被在途文件写坏时仍能构建）。
   两轮变异对照已确认这套断言不是空转：把 `Utf8.cs` 的 Underscore 替换成 `'X'` → 181 项数据失败；把 rank 比较的 `<` 改成 `>` → 1492 项失败；把 `UfbxiStrings.All` 相邻两项互换 → 5 项（digest/L 位置/升序）失败。全部已还原。
2. **两条新硬化规则已写进 PORTING_NOTES.md**（后续任何依赖指针序的代码都要遵守）：
   - 字符串指针的地址序**只在同一个 pool 内**等于分配序，跨 pool 不可比（实测反例：`S` pool 的 8128B chunk 排在分配更晚的 `K` pool 4032B chunk 之后）。oracle 用 `GRP_*`、`UtilCheck` 用 `RankedIds[grp]` 分组算 rank；把分组打平成单一 rank 域会产生 1274 项失败，说明这条不是纸面约定。
   - `UfbxiStrings.All` 现在逐条等于 ufbx.c:5583-5886 的 `ufbxi_strings[]` 表序（原先按 `ufbxi_Str_*` 声明序生成，相邻项有互换）。ufbx.c:26617-26640 用线性扫描把 prop_name 归一到常量，依赖该表在 `ufbxi_str_less` 下严格升序；顺序错则 `name == ufbxi_XXX` 一类的指针比较静默失效。已加 `L table strictly sorted` 检查守门。
3. **`src/Ufbx/Util/**`、`Parse/UfbxiStrings.cs`、`tools/util_oracle.*`、`tools/UtilCheck/` 归本会话所有**，其他会话/子代理不要写入；需要新增池侧入口就在 COORDINATION 里登记需求。
4. 之前两个后台子代理（utils、inflate）都在轮次上限处停止：utils 的产出已由本会话核收+补完（即上面第 1 条）；`tools/InflateCheck` 现状 **2759 向量 / 2707 通过 / 52 失败**（含 empty-input、zlib 对照、progress 驱动、bit-flip 模糊等几组），任务 #7 仍开放。ASCII 子代理报 1121/1121，但它的验证工程用了 `UfbxiStream`/`UfbxiContext` 的**测试本地替身**，替身必须换成真实类型后才算数，这项核收尚未做。

## 更新（DOM 线会话，2026-10-02 深夜 → 10-03）——二进制 DOM 已收口

1. **二进制 DOM 全量对拍通过**：`dotnet run --project tools/DomCheck -c Release`（默认读 `tools/dom_oracle.txt`，语料根 `C:\Workspace\_analyze_ufbx`）→ **11 个二进制文件 / 9471 条 `N` + 21997 条 `V` + 9471 条 `D` + 11 条 `S`/`T`，0 条分歧**；端口输出落 `tools/dom_port.txt`（CRLF，与 oracle 同格式，可直接 diff）。覆盖到的语义：27 字节头 + 3000/2000 的 **legacy 路径**（`ufbxi_parse_legacy_toplevel` 递归 + `retain_dom_node` 递归）、7400/7500 的 32/64 位头、**big-endian**（`ufbxi_swap_endian(_array)` 后数组按小端落盘）、DEFLATE 数组与其 **截断失败样本 fi 15**（`S 15 0 1 16 0 "Bad DEFLATE data" 0 basis -` 逐字节命中）、名字是否回指 `ufbxi_*` 常量（`UfbxiPtrIdTable.IsStatic` ↔ oracle 的 `is_static_ptr`）、`ARRAY_BLOB` 的 `[8B LE size][bytes]` 打包、以及 `ufbx_dom_*` 六个访问器的 count+digest（`D` 记录）。
   两侧证据链：`tools/dom_oracle.c`（公开 API + `retain_dom=true`）/ `tools/dom_oracle.txt`（74585 行）/ `tools/DomCheck/`（net8 隔离工程，编译 `src/Ufbx/**` 全量）。语料清单在运行时从 `dom_oracle.c` 的 `g_default_files[]` 解析，两侧不可能漂移。
2. **驱动等价性**（写进 `tools/DomCheck/Program.cs` 头部注释）：C 的名字驱动 `ufbxi_parse_toplevel()`（11266-11326）+ `retain_toplevel/retain_toplevel_child`（10809-10849）总是把挂起的 children 补给**上一个**被 retain 的顶层节点，所以 children 只可能按文件序挂载 → 顺序驱动的等价驱动成立。version<6000 走 legacy 分支（递归解析，children 由 `retain_dom_node` 递归 retain）。**不要**为了省事把两者合并成"统一递归"：现代路径顶层 children 在 depth 0 解析、legacy 在 depth+1，`depth < UFBXI_MAX_NODE_DEPTH` 的允许深度差 1 层。
3. **harness 抓到 1 个真 bug（已修，规则已进库）**：`uc->retain_vertex_w` 是**从 opts 派生**的状态（ufbx.c:25292 `(retain_dom || retain_vertex_attrib_w) && !ignore_geometry`），不是 opts 直通。缺了它 `NormalsW/BinormalsW/TangentsW` 在 `retain_dom` 下被判成 `'-'`（ignored）而不是 `'r'`，fi 13 的 507 个 double 直接消失。现在由 `UfbxiContext.InitDerivedOpts()` 承担，**移植 `ufbxi_load()` 的人必须在 `InitStringPool()` 之前调它**。
4. **待解决的移植口径（已登记，勿在别处各自发明）**：错误描述链路。C 在 `UFBXI_FEATURE_ERROR_STACK==0` 下，`ufbxi_check(cond)`/`ufbxi_fail(desc)` **不写** `error.description`（走 `ufbxi_fail_no_msg`→`fail_imp_err(err,NULL,...)`），最终由 `ufbxi_fix_error_type(&uc->error, "Failed to load", p_error)`（25623）填默认串；只有 `ufbxi_check_msg`/`ufbxi_error_msg` 才带描述。本项目的 `UfbxParseError.Message` 目前一律是 C 的**描述文本**，对 msg 型站点正确，对纯 check 站点会把条件文本当描述上报（当前语料只有 fi 15 失败且它是 msg 型，所以对拍通过）。收口点在公开 API 层的顶层 catch：需要区分两类失败，或让 `UfbxiFail.Check(cond, ...)` 传条件串并由 `$desc\0cond` 约定解出描述。
5. **ASCII DOM 缺口**：oracle 的 26 个文件里 15 个是 ASCII，本轮按 C 自己的判据（22 字节二进制 magic）跳过并在输出里逐个列出。缺的是 `ufbxi_ascii_parse_node`（ufbx.c:10236-10520）——`Parse/Ascii.cs` 已有 tokenizer/数组快路，但没有 node/prop 装配入口。这是 golden 全量对拍的前置项（语料里 ASCII 文件占比很大）。
6. **本轮已核收的子代理结果**：ASCII 线在把测试替身换成真实 `UfbxiStream`/`UfbxiContext` 后 **1737/1737 通过**（任务 #13 关）；数值线 `ufbxi_parse_double`/`ufbxi_f64_to_i64` oracle **280169/280169 零分歧**，并独立确认了 `BigintShiftLeft` 的 `ufbxi_maybe_uninit` 残留值语义＝"共享复用缓冲区，读残留值"（任务 #15 关）。inflate 子代理再次在轮次上限停止，`tools/InflateCheck/Program.cs`（现在只有 16 行）与 `tools/inflate_oracle.c` 可能处于**改了一半**的状态，接手前先读不要直接覆盖。
7. **文件所有权（本轮登记）**：`src/Ufbx/Parse/DomNode.cs`、`src/Ufbx/Model/UfbxDomApi.cs`、`tools/DomCheck/`、`tools/dom_oracle.*`、`tools/dom_port.txt` 归 DOM 线。`src/Ufbx/Parse/UfbxiContext.cs` 是**共享文件**（本轮加了 `InitDerivedOpts()`）——其他会话/子代理改它之前先在本文件登记，避免再次互相覆盖。

## 更新（DOM 线会话，2026-10-03）——ASCII DOM 已收口，取代上一节第 5 条

1. **全 26 个语料文件零分歧**：`dotnet run --project tools/DomCheck -c Release -- tools/dom_oracle.txt` → **26 个文件（11 二进制 + 15 ASCII）/ 16120 条 `N` + 42293 条 `V` + 16120 条 `D` + 26 条 `S`/`T`，0 条分歧**；`tools/dom_port.txt` 与 `tools/dom_oracle.txt` 行数逐行相等（各 74585 行）。上一节第 5 条的"按 magic 跳过 ASCII"已删除，harness 现在两种格式都跑，并把**端口的格式判定**（`uc.FromAscii`，来自 `ufbxi_begin_parse` 的 magic 比较）与独立扫描的 magic 对照，不一致就打 `FORMAT MISMATCH`（当前 0 条）。
2. **新增文件**：`src/Ufbx/Parse/AsciiDomNode.cs`（`ufbxi_ascii_parse_node` ufbx.c:10280-10690 + `ufbxi_setup_base64`/`ufbxi_decode_base64` + tmp_stack 的 bool/byte/i32/i64/f32/f64 压栈 helper）。**改动**：`Parse/DomNode.cs` 的 `UfbxiDom` 变成 `partial`，原 `ParseNode` 更名 `ParseNodeBinary`，新增按 `uc.FromAscii` 分派的 `ParseNode`——移植加载入口的人只要调 `UfbxiDom.ParseNode` 就同时覆盖两种格式，C 侧对应 `ufbxi_parse_toplevel`/`ufbxi_parse_legacy_toplevel`/`ufbxi_parse_toplevel_child_imp` 三处的同一分派。
3. **口径记录（都写进了文件头注释）**：C 的多路复用 `tmp_stack` 在端口里分成类型化 node 栈 + 单一字节栈 `UfbxiContext.TmpStack`；`'s'/'S'/'C'` 字符串数组的元素**不进字节栈**（C 按 `sizeof(ufbx_string)=16` 进栈），改由局部 `List<string>` 承接，但 8 字节对齐 helper 与 `PAD_BEGIN` 的 4 个零槽仍按 C 压/弹，好让栈深账目可核对。线程化路径（`UFBXI_MIN_THREADED_ASCII_VALUES`、`deferred_size`、`ufbxi_ascii_array_task_*`）在端口里恒不可达，故未移植——`deferred_size` 恒为 0 是字符串/数组尾部分支能简化的前提。
4. **harness 抓到第 2 个真 bug（已修）**：`arr_error`（坏 base64）在 C 里是**整数组丢弃**（`ufbxi_pop_size(..., NULL, false); num_values = 0;`），端口原先只在数值分支处理，字符串分支照收，于是 fi 7 的 `BinaryData: "Yes"`（3 字节，`len%4!=0`）和 fi 8 的 `synthetic_base64_parse_7700_ascii.fbx` 共 72 条记录多出一个 blob 元素。现在字符串分支同样按 `arr_error` 清空。注意 C 的 `size = num_values - 4` 在 `PAD_BEGIN`+`arr_error` 下会 size_t 下溢，端口用显式报错守卫（该组合恒不可达：pad 只用于数值数组类型）。
5. **回归确认**：`dotnet build ufbx-cs.sln -c Release` 0 warning / 0 error；UtilCheck 6865/6865、AsciiCheck 1737/1737、NumericCheck 280169/280169、mathvec 72000/72000、streamcheck 2243020/0 全绿。**oracle 未重新生成**，因此不涉及 `-O2` 重编译风险。
6. **C# 语言坑（供后续移植参考）**：switch 的**最后一个** `case`/`default` 段若以一条普通语句结尾而没有 `break;`，编译报 **CS8070「控件无法从最终用例标签脱离开关」**（不是 CS0163），而调用的 `UfbxiFail.Fail()` 虽然必抛但签名是 `void`，编译器不认它是终结语句——照 C 的 `default: ufbxi_fail(...)` 直译时要补 `break;`。
7. **下一步（DOM 线未认领）**：加载入口 `ufbxi_load`（25280-25630）+ `ufbxi_determine_format` + 公开 `UfbxApi.LoadMemory/LoadFile/FreeScene`，以及仍然开放的 inflate（#7/#12，fi 15 的 DEFLATE 失败样本本轮逐字节命中，但数组解压路径要等 inflate 收口）。上一节第 4 条的错误描述口径**仍未收口**，收口点在公开 API 层的顶层 catch。
8. **inflate 线已核收（任务 #7/#12 关）**：`dotnet run --project tools/InflateCheck -c Release` → **4613 向量 / 0 失败**（含 8024 次 bit-flip 模糊与 585 D / 13 B / 11 A 条 C oracle 答案），端口侧走的是真实 `UfbxiInflate.UfbxInflate()`，`src/` 里没有任何 `DeflateStream`/`System.IO.Compression`（只有 harness 用它生成对照数据）。`{BitStream,Huff,Inflate}.cs` 共 1593 行。核收做了**变异对照**证明断言不空转：把 `DeflateLengthLut[1]`（长度符号 4）的 extra bits 由 0 改 1 → 40 条失败；反证 harness 有效。同时确认 `DeflateLengthLut`/`DeflateDistLut`/`DeflateCodeLengthPermutation` 与 ufbx.c:1854-1869 逐字节相同。变异已全部还原（两处 LUT 改动都改回原值，还原后 InflateCheck 4613/4613、DomCheck 26 文件 0 分歧）。
   **一处覆盖缺口留给你**：只改 `DeflateLengthLut[9]`（长度符号 13，即 11/12 共用一条码）时 4613 条**全绿**，说明现有语料里这个码的 extra bit 从未被真正消费；码表本身与 C 一致所以风险低，但若要进一步收紧请补一条含长度 12 的向量（不要在 `src/` 里改实现来凑）。

## 更新（DOM 线会话，2026-10-03）——DOM 语料放宽到 75 文件，并定位到 DOM↔场景构建的别名耦合

1. **语料放宽**：`tools/dom_oracle.c` 的 `g_default_files[]` 由 26 追加到 **75**（只追加，`fi` 索引不变；原 74585 行前缀逐字节复现）。oracle 已用**规定的** zig 命令行重生成：`zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx tools/dom_oracle.c -o tools/dom_oracle.exe`。现 `tools/dom_oracle.txt` = **529708 行 / 75 个 `S` / 144197 `N` / 241164 `V` / 144197 `D` / 75 `T`**。放宽目的是让新写的 ASCII 解析器见到更多形状（合成特性文件 + 真实 DCC 导出：max/maya/blender/houdini/hugetextures/fbxsdk 等）；**`data/fuzz/*.fbx` 刻意排除**——它们的分歧来自尚未收口的错误描述口径，而不是 DOM，混进来只会掩掉真信号。
2. **现状**：`dotnet run --project tools/DomCheck -c Release -- tools/dom_oracle.txt` → 75 文件（31 二进制 / 44 ASCII），**7 条分歧记录 / 3 个文件**，全部已定位（见下）。这 7 条现在由 harness 的 `KnownDivergences` 账本记账，账本要求**端口当前产出的记录内容逐字符命中 `ExpectPort`** 才允许豁免（换了新 bug 就照常 FAIL），并且任何一条一旦不再分歧就打 `LEDGER STALE` 逼删除——账本不会替活着的 bug 长期背书。
3. **分歧 A（6 条，fi 32 node 197/207、fi 55 node 387，都是 `PointsIndex` 的 `V`+`D`）＝根因已定案，不是解析 bug**：C 的 DOM blob 直接指向 `arr->data`（ufbx.c:10754），而 element reader 用的是同一块缓冲。`ufbxi_read_line()`（13914-13948）**就地**改写 `line->point_indices.data`，把负的结束标记 `~ix` 化并夹越界索引，于是文件里的 `-1` 在 C 的 DOM 里是 `0`、`-5` 是 `4`。对照：`ufbxi_read_mesh()`（13460-13466）、legacy mesh（16196-16202）和 `ufbxi_indexer_indices()`（12707-12719）在 `retain_dom`/不持有缓冲时**先复制再改**，所以 `PolygonVertexIndex` 的 DOM 保持文件字节。规则已写进 `PORTING_NOTES.md`「DOM/数组数据约定」最后一条：**element reader 必须逐点照抄 C 的复制/不复制判断**。收口点在 `ufbxi_read_line` 的移植，不在 DOM 层——**任何人不要去 `Parse/BinaryArray.cs`/`DomNode.cs` 里"修"这 6 条**。
4. **分歧 B（1 条，fi 64 `synthetic_bad_inf_nan_fail_7500_ascii.fbx` 的 `S`）＝就是上一节第 7 条与更早第 4 条登记的错误描述口径**：C 的纯 `ufbxi_check()` 站点不写 `error.description`，最终由 `ufbxi_fix_error_type(&uc->error, "Failed to load", p_error)`（25623）填默认串；端口把条件文本 `end == token->str_data + token->str_len - 1` 当描述上报。**站点已精确定位，交给做「逐模块 call-site 审计」的那条线一次做完，别在 DOM 线零散改**：`src/Ufbx/Parse/Ascii.cs:588-591` 与 `597-599`（该文件目前 0 处使用 `UfbxiFail`，全是直接 `throw new UfbxParseError(...)`），对应 C 的 ufbx.c:9789 与 9794，两处都是 **plain** `ufbxi_check(end == token->str_data + token->str_len - 1)`，所以应改成 `UfbxiFail.FailNoDesc("end == ...")`（`HasDescription=false`）。改完 fi 64 的 `S` 记录会变成 `S 64 0 1 14 0 4661696c656420746f206c6f6164 ...`（"Failed to load"），与 C 逐字节相同，账本里的 `S 64 -` 随之以 `LEDGER STALE` 报出并删除。附带口径确认：**error description 不进 golden 哈希**（`test/hash_scene.c` 只把 `error.description` 打到 stderr，golden 第二列是 `error.type`），所以这条影响的是 API 保真与 DOM 对拍，不是验收主通路。
5. **harness 侧修掉一个假阳性**：原先 `BuildDom` 抛出时 `uc` 为 null ⇒ `parsedAscii=false`，于是「ASCII 文件 + 加载失败」被误报成 `FORMAT MISMATCH`。现在 context 在建好那一刻就写进 `slot[0]`，只有 `ok || uc.FromAscii` 才判定格式（binary 文件在 header 就失败时格式未知，跳过而不是误报）；`FORMAT MISMATCH` 与 `LEDGER STALE` 现在都计入失败。
6. **所有权/在途提示**：`Parse/UfbxiContext.cs` 仍是有冲突风险的**共享文件**——load-spine 子代理已在其中加了 `PropTypeMap`（ufbx.c:6503）字段，且引用的 `UfbxiPropTypeName` 类型尚未落地，**当前 `dotnet build ufbx-cs.sln` 因此 1 error（CS0246, UfbxiContext.cs:263）**。这不是 DOM 线改坏的，DOM 线的 `tools/DomCheck/Program.cs` 改动已就位但因该 build 中断无法复跑。其他会话请避开 `UfbxiContext.cs`，或先在此登记。

## 更新（load-spine 核收会话，2026-10-02/03）——加载主干差分对拍已建通，错误描述口径已收口

1. **新证据链（本轮建立，认领人：load-spine 核收线）**：`tools/load_oracle.c`+`.exe`+`.txt`、`tools/load_corpus.txt`、`tools/loadcheck_inputs/`、`tools/LoadCheck/`、`tools/load_port.txt`、`tools/load_check.txt`。
   结果：`dotnet run --project tools/LoadCheck -c Release` → **2946 文件 / 0 条分歧 / LOAD CHECK PASS**；端口记录落 `tools/load_port.txt`（与 oracle 同 16 列、CRLF、行号一一对应）。
   分类：C 成功 1107（744 FBX/312 OBJ/51 MTL）；端口到 seam 2057（read_root 1530、obj_load 452、mtl_load 54、read_legacy_root 21）；端口在 spine 内失败 889 条，**889 条错误契约逐字节命中（parity 889，seam underrun 0）**。
2. **oracle 驱动方式**（决定了可比字段）：直接调内部 `ufbxi_load()`（`uc` 零初始化 + `deferred_load/load_filename/load_filename_len=SIZE_MAX`，等价 `ufbx_load_file_len`），这样失败样本也能读到 `file_format/from_ascii/file_big_endian/sure_fbx/version`——公开 API 在 25380-25382 才把 version/ascii/big_endian 抄进 metadata，而这三行**在 seam 之后**。构建命令必须带 `-mcpu=x86_64 -ffp-contract=off`（见上文硬化规则）。
3. **「past-seam certificate」判据**（把 950 条「C 在未移植的 reader 里失败」变成可机械证明的豁免，而不是人工清单）：按 C 自己的写入点反推——`file_format` 只在 11184（`ufbxi_determine_format` 末尾，失败即 pre-seam）、`file_big_endian` 只在 11200、`sure_fbx` 只在 11211/11228、`version` 在 11208/11230 与 seam 之后的 9568/10455/11996、`from_ascii` 在 11215 与 16863。于是 `format==FBX && (sure_fbx || (ascii && version>0))` 证明 `ufbxi_begin_parse` 已返回、C 失败在 `ufbxi_read_*`；`format==OBJ|MTL` 证明分派已到达 obj/mtl_load；其余（尤其 `format==UNKNOWN` 与 `version==0 && !sure_fbx`）必须逐字节复现。反向同样检查：端口早退而 C 已过 seam ⇒ `seam underrun`。
4. **`ascii`/`version` 两列只在「双方都在 spine 内失败」时严格**：506 条 `ascii` 差异来自 `ufbxi_obj_init`（16863，seam 之后才置 `from_ascii`），15 条 `version` 差异来自 `ufbxi_read_header_extension`（11993-11997：`max6/max7/max2009` 那批文件头字节实测 `b8 0b 00 00`=3000，真版本 5000/5800 要到 header extension 才知道）。这两类现在打 INFO + `VERSION-DIVERGES` 单列，不计失败。
5. **语料放宽**：`tools/load_corpus.txt` = `find data -type f | LC_ALL=C sort` 的 2937 条 **＋9 条**（清单不允许注释行，两侧逐行原样读取）：`tools/loadcheck_inputs/`（0 字节 .fbx/.obj、22 字节恰好等于二进制 magic 但填不满 27 字节头、只有版本注释的 25 字节 ASCII、2 字节 `just_1a.bin`）+ 目录 `data`/`data/` + 两个不存在路径，覆盖 file-open 失败、`check_msg` 的 "Empty file"、plain 检查的截断头、以及 `%u` unsupported-version 重写。**`data/` 里不放合成文件**（共享语料，重生成 golden 会受影响）。
   harness 传路径的口径：`Directory.SetCurrentDirectory(ufbx 根)` 后把**语料原串**交给端口（`error.info` 对 file-open 失败就是路径本身，之前传绝对路径导致 4 条假分歧）。
6. **本轮修掉两个真分歧（都由新增合成样本暴露）**：
   a. `Parse/Stream.cs` 的 `ufbxi_refill()` EOF 分支：C 是 `ufbxi_check_return(!uc->eof, NULL)`（**plain**，不写描述），端口原先报 `"Truncated file"`/`TruncatedFile` ⇒ `magic_truncated.fbx`、`header_only_ascii.fbx` 得到 type 8 而 C 是 type 23 "Unsupported version"+info `"0"`。现已按 C 的两类形态逐站点审计完 IO 模块：plain = `!uc->eof`、`read_result <= to_read`、`uc->read_fn`（`ReadTo`）、`read_result != 0`（`ReadTo`）；带描述 = `Empty file`/`Truncated file`/`IO error`/`Cancelled`（含 `ufbxi_skip_bytes` 的三处 `check_msg`）。
   b. `Parse/Ascii.cs` 的 6 个 plain 站点（9789/9794 的数字 token `end == ...`、三处 `c != '\0'`、`num_read <= dst_size`）改成 `UfbxiFail.FailNoDesc`——**这就是上一节「分歧 B」登记的那条**，fi 64 的 `S` 记录现在与 C 逐字节相同（`S 64 0 1 14 0 4661696c656420746f206c6f6164 ...` = "Failed to load"），DOM 账本里的 `S 64 -` 条目已按该节预告删除。
   两处都做了变异对照：把 plain 站点的条件文本改掉 ⇒ 两侧 harness 仍 PASS（证明纯 check 站点确实不携带描述）；把 EOF 站点改回带描述的 throw ⇒ LoadCheck **FAIL，2 文件 / 14 条分歧**。
7. **harness 侧的必要配套**：`tools/DomCheck/Program.cs` 的顶层 catch 原先无条件 `error.Description = e.Message`（它自己的注释写明「DOM 语料只到 msg 型站点」），现在按 `UfbxParseError.HasDescription` 分流，与 `UfbxApi.ReportFailure` 同一口径。
8. **收口了上一节第 6 条的 build 断裂**：`UfbxiPropTypeName`（C 的 `ufbxi_prop_type_name`，ufbx.c:11426）从 `Parse/Load.cs` 移到新文件 **`src/Ufbx/Parse/PropTypeName.cs`**，并给 `tools/AsciiCheck/AsciiCheck.csproj` 加了一条 `<Compile Include>`（该工程用裁剪闭包，看不到 Load.cs，于是共享文件 `UfbxiContext.cs:263` 的 `UfbxiMap<UfbxiPropTypeName,ulong>` 报 CS0246）。今后**凡被 `UfbxiContext.cs` 引用的类型不要放进只在闭包外可见的文件**。
9. **回归确认（oracle 一律未重生成，无 `-O2` 风险）**：`dotnet build ufbx-cs.sln -c Release` 0 warning / 0 error；LoadCheck 2946/0；DomCheck 75 文件 **0 分歧 / 账本剩 6 条（全部是 `ufbxi_read_line` 的 PointsIndex 就地改写，见更早第 3 条——收口点在 element reader 移植，不是 DOM）**，且 75 个 `S`+`T` 记录与 `tools/dom_oracle.txt` 逐字节相同；UtilCheck 6865/6865、AsciiCheck 1737/1737、NumericCheck 280169/280169、InflateCheck 4613/4613、streamcheck 2243020/0、mathvec 72000/72000。
10. **所有权（本轮登记）**：`src/Ufbx/Parse/{Load.cs,Stream.cs,PropTypeName.cs,Ascii.cs}`、`src/Ufbx/Api/`、`tools/load_oracle.*`、`tools/LoadCheck/`、`tools/load_corpus.txt`、`tools/loadcheck_inputs/`、`tools/load_port.txt`、`tools/load_check.txt` 归 load-spine 核收线；`tools/DomCheck/Program.cs` 只改了上述两处（账本条目 + catch 口径）。`UfbxiContext.cs` 仍按共享文件处理，本轮未改。
11. **下一步（无人认领，按 funcmap 依赖序）**：seam 之后的 `ufbxi_read_root`（15847）+ definitions/objects/connections/properties/elements → scene build；LoadCheck 的 2057 条 pending 记录就是入口清单（`stopped at ufbxi_read_root: 1530` 等）。`ufbxi_read_line` 移植时必须按 PORTING_NOTES「DOM/数组数据约定」最后一条判断复制/不复制，否则 DOM 那 6 条账本会变成真分歧。

## 路线与认领（load-spine 核收会话，2026-10-03）——seam 之后的分层推进

验收目标是 `tools/golden_hashes.txt` 的场景哈希，它要求 seam 之后约 1 万行 C 全部落地（`test/hash_scene.exe` 哈希的是**完整构建**的场景 + 第 0..9 帧求值）。因此按「每一层先有对拍证据、再铺实现」推进，分四层：

| 层 | C 范围（ufbx.c） | 端口新文件 | 证据 |
|---|---|---|---|
| S1 管道层：properties / fbx_id / connect / element 装配 + `ufbxi_read_root` | 11466-11930、12225-12596、12597-12662、15830-15989 | `Parse/{Properties,FbxId,Connections,ReadElement,Root}.cs` | `tools/graph_oracle.*` + `tools/GraphCheck/`（post-`read_root` 的 element/prop/connection 记录级对拍；C 侧全量可用，端口可增量比对） |
| S2 对象 reader：mesh/nurbs/line/bone/skin/anim/material/…（12597-15830 的叶子） | 各 `ufbxi_read_*` | `Parse/Objects*.cs`、`Parse/Geometry*.cs` | GraphCheck 覆盖到 object/connections 记录 |
| S3 场景构建：`ufbxi_pre_finalize_scene`(18115)、`resolve_connections`(18664)、`linearize_nodes`(18913)、`fetch_*`、`ufbxi_finalize_scene`(21644)、`ufbxi_update_scene`(23809) | 16486-23936 | `Parse/{SceneBuild,SceneFinalize,SceneUpdate}.cs` | golden 哈希（frame 0） |
| S4 动画求值 + 哈希：`ufbxi_evaluate_*`(25697-)、`test/hash_scene.c` | — | `Parse/{Evaluate,HashScene}.cs` | golden 全量 2179 行（任务 #9） |

本轮认领：`src/Ufbx/Parse/{Properties,FbxId,Connections,ReadElement,Root,SceneBuild,SceneFinalize,SceneUpdate,Evaluate,HashScene}.cs`（尚未创建）、`tools/graph_oracle.*`、`tools/GraphCheck/`。S2 的叶子 reader 等 S1 管道落地后再按 mesh/anim/material 切给并行子代理，各自独立文件、**不得**改共享文件 `Parse/UfbxiContext.cs`（需要新字段就在 COORDINATION 登记由本线统一加）。

## 交接（load-spine 核收会话 → 接手任务 #20 的 agent，2026-10-02）

**状态**：任务 #20（S1 证据链）进行中；本轮只完成设计侦查，**没有新文件落地**。#21/#22 未开始。本节记录的全部结论都已对着 ufbx v0.23.1（冻结）核过，接手者直接沿用，不要重新推。

1. **停点**＝`ufbxi_load_imp` 中 `ufbxi_pre_finalize_scene(uc)`（ufbx.c:25328）**之前**。此处 `uc->tmp_*` 与 `uc->result`（props 在此）全活；`ufbxi_free_temp`/`ufbxi_free_result` 要等 `ufbxi_load_imp` 返回后才跑，所以在停点读结构安全。
2. **场景图数据面**（post-read_root）：`uc->tmp_element_ptrs`（`ufbx_element**`，push 序）、`uc->tmp_element_fbx_ids[i]`（与前者同位对齐）、`uc->num_elements`、`uc->tmp_connections`（`ufbxi_tmp_connection { uint64 src, dst; ufbx_string src_prop, dst_prop; }`，ufbx.c:6300-6306）。元素 props 在 `uc->result`。
3. **结构定义**：`ufbx_element`（ufbx.h:763-774）、`ufbx_props`（ufbx.h:567-572）、`ufbx_prop`（ufbx.h:542-560）。prop flag 位：REAL 0x100000 / VEC2 0x200000 / VEC3 0x400000 / VEC4 0x800000 / INT 0x1000000 / STR 0x2000000 / BLOB 0x4000000（ufbx.h:527-537）。
4. **dump prop 值不要用 union 原始字节**：legacy 路径（`ufbxi_read_legacy_prop` 15989 / `ufbxi_read_legacy_props` 16051）用栈上**未初始化**的 `ufbx_prop tmp_props[]`，会混进垃圾。按 flags 分派：`value_int` / 按 REAL<<k 打 k+1 个 float 的**位模式**（memcpy 到 uint32 后 8 位 hex）/ `value_str` / `value_blob`，四类独立 token、缺省 `-`、列固定。
5. **前缀复刻**：逐行抄 `ufbxi_load()` init（25482-25607；注意 `synthetic_id_counter=UFBXI_SYNTHETIC_ID_START` 25547、字符串池/map/buf ator、opts 默认值、`ufbxi_load_strings/load_maps/determine_format/begin_parse`）+ `ufbxi_load_imp` 25215-25306。**不要**用宏拦截 `ufbxi_pre_finalize_scene`：函数式宏会把定义行一起改名、object-like 宏会把调用点一起改名（两条路都验过），老老实实复制；C 冻结版可接受。
6. **记录语法建议**（与既有 oracle 同风格：空格 token、FNV-1a-64 basis cbf29ce484222325、≤64 字节内联 hex 否则 `-`）：`G <fi> <version> <ascii> <num_elements> <num_connections>`、`E <fi> <elem_id> <type> <typed_id> <fbx_id> <name:len digest hex> <num_props>`、`P <fi> <elem_id> <ix> <name...> <type> <flags> <key> <int|-> <reals|-> <str|-> <blob|->`、`C <fi> <ix> <src> <dst> <src_prop...> <dst_prop...>`。名字回指常量池的按 `is_static_ptr` 打标（DOM 线有 `UfbxiPtrIdTable.IsStatic` 可对）。
7. **version<6000 走 `ufbxi_read_legacy_root`（16423），S1 不含**：oracle 照跑并打 `G <fi> legacy ...`，GraphCheck 当前对 legacy 记录 SKIP，S2 落地后转严。
8. **语料**：从 `tools/load_port.txt` 筛 `read_root` 停点的 1530 条 ＋ 合成 6100/7100/7400/7500/7700 ASCII/二进制；不含 `data/fuzz/*`。oracle 在 `C:\Workspace\_analyze_ufbx` 下运行、路径原串传（同 LoadCheck 口径）。
9. **变异对照是 #20 的验收项**：改 dump 任一项的选择逻辑（例如关掉 props 去重、把 fbx_id 打成 elem_id）⇒ GraphCheck 必须 FAIL，否则断言空转。
10. **构建/运行（原样）**：`zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx tools/graph_oracle.c -o tools/graph_oracle.exe`；`cd C:/Workspace/_analyze_ufbx && C:/Workspace/ufbx-cs/tools/graph_oracle.exe > C:/Workspace/ufbx-cs/tools/graph_oracle.txt`。
11. **端口侧**：S1 落地前 GraphCheck 用 PENDING 口径——端口抛 `UfbxiReaderNotPortedException`（`Parse/Load.cs` 的 `UfbxiToplevel.ReadRoot` 还是 seam）⇒ 记 SKIP 但统计条数，不计失败；#21 落地后转严，并反向查「端口早到/晚到」两类偏差（沿用 LoadCheck 的 certificate 思路，见上一节第 3 条）。
12. **所有权移交**：`tools/graph_oracle.*`、`tools/GraphCheck/`、`Parse/{Properties,FbxId,Connections,ReadElement,Root}.cs` 整条移交接手者。共享文件 `Parse/UfbxiContext.cs` 仍 append-only + 先登记。
13. **约束不变**：禁止用 `-O2`（native）重编译任何参考二进制（必须带 `-mcpu=x86_64 -ffp-contract=off`）；不写 `_analyze_ufbx/data`；workspace 非 git 仓库；不改其他线的文件；新增失败站点必须按 C 的宏形态选 `UfbxiFail.CheckMsg/FailMsg` 或 `CheckNoDesc/FailNoDesc`。

## 更新（编排会话，2026-10-03）——S1 收口、S2 全部 reader 落地、错误形态审计收口

1. **S1 管道层（任务 #20/#21）已收口**：`Parse/{Properties,FbxId,Connections,ReadElement,Root}.cs` 落地（Root 含 `ufbxi_read_root` + legacy 骨架 + `ufbxi_parse_legacy_toplevel` 11375-11406）；证据链 `tools/graph_oracle.c/.txt`（24MB）+ `tools/GraphCheck/` 建成。GraphCheck PENDING 口径下 0 意外失败（S2 落地前）。
2. **S2 全部 element reader 已落地并由编排线统一接线**（ReadElement.cs/Root.cs 的 NotPorted 桩全部替换）：
   - geometry：`Parse/Geometry.cs`（12662-14108，含 read_mesh/read_line 就地改写 + process_indices）
   - anim：`Parse/AnimReader.cs`（14109-14647 + 15317-15768，8000 条 TCB/auto-tangent 向量对拍 8000/8000，oracle `tools/animcurve_oracle.c` + `tools/AnimCurveCheck/`）
   - objects/legacy：`Parse/Objects.cs` + `Parse/Legacy.cs`（14537-15316 + 15989-16499）
   - 新增 partial：`Parse/UfbxiContext.{Geometry,Anim,Objects}.cs`（**UfbxiContext 已改 partial**，后续模块各自 append 自己的字段文件，不再改本体）
   - 接线后 GraphCheck：`FAIL 0`，705 文件直达 `ufbxi_pre/finalize_scene` seam；剩余桩只有 `Load.cs` 的 obj/mtl/pre-finalize_scene（S3）。
3. **GraphCheck 口径修正（编排线）**：oracle 对 version<6000 只打 `G <fi> legacy ...` 且**不输出 E/P/C**（graph_oracle.c 注释已声明），但 harness 原先把 legacy 记录当普通记录比对 ⇒ 把 2 个合成 fail 样本误判为「PORT FAILS IN SPINE」。现 RunPort 对 legacy 只比对**成功/失败结局**（从 G 记录第 7 列读 C 的 error type，0=C 接受），计数单列 `LEGACY (outcome-only)`；legacy 的内容级比对不在 GraphCheck 范围（由 LoadCheck 的错误契约 + 最终 golden 覆盖）。
4. **错误形态逐站点审计已收口（新一轮代理，全树）**：把 `Parse/{DomNode,AsciiDomNode,BinaryArray,UfbxiContext,UfbxiContext.Geometry,UfbxiNode,Ascii,Stream,Error}.cs` 与 `Util/StringPool.cs` 的错误站点逐条对 C 宏形态改写（plain ⇒ `CheckNoDesc/FailNoDesc`；msg ⇒ `CheckMsg/FailMsg`），并把「port-only 的 int 上限/分配上限 guard」改成复现 C 的下游检查（例如 `DomNode` 的 DEFLATE `Bad DEFLATE data` 走 9220 的 msg、`Truncated file` 走 6705 的 plain `!uc->eof` 先行）。**现在全树不存在 legacy `UfbxiFail.Check/Fail(` 或散落的 `throw new UfbxParseError`**（只 `Parse/Error.cs` 收敛点内部保留）。
   验证：LoadCheck 2946 文件 **0 分歧 PASS**（idx4 ascii INFO 506 条为已知口径）；DomCheck PASS（账本 6 条 KNOWN 未删）；GraphCheck PASS；UtilCheck 6865/6865；AsciiCheck 1737/1737；mathvec 72000/72000；变异对照（Msg→NoDesc、NoDesc→Check 各一处）⇒ LoadCheck FAIL 17 文件/51 记录，还原后 PASS。
5. **登记的潜在分歧（未触发，交后续处理）**：`Parse/Geometry.cs` 的 `MaterializeU32/MaterializeReals`（`new uint[count]`）在 `count > Array.MaxLength` 时会抛 `OutOfMemoryException/OverflowException`，而 C 用 `ufbxi_alloc_size` 成功分配后由下游检查失败。当前语料未触发（LoadCheck 0 分歧），但应按下游站点语义改写。
6. **遗留临时产物待删**（诊断用，不在交付范围）：`tools/_VerboseLC/`、`tools/_bigtest/`、`tools/_*.txt`、`C:/tmp/{dump,fbxstr}.py`。
7. **所有权（本轮登记）**：`Parse/{Properties,FbxId,Connections,ReadElement,Root,Geometry,AnimReader,Objects,Legacy}.cs`、`Parse/UfbxiContext.*.cs`、`tools/{graph_oracle.*,GraphCheck,animcurve_oracle.*,AnimCurveCheck,S2GeomCheck,S1SelfCheck}/` 归编排线。`tools/GraphCheck/Program.cs` 本轮改了 legacy 口径（见第 3 条）。
8. **下一步（S3，已开工）**：OBJ/MTL loader（ufbx.c 16776-18090）+ 场景构建（16535-16775、18091-23935）→ 之后 update_scene/evaluate/utils/hash 才能跑 golden。

## 更新（编排会话，2026-10-03 22:15）——S3b 代理丢失产出，缺口与接力指令已登记

1. **已核实的落地状态**（构建全绿，`dotnet build ufbx-cs.sln -c Release` 0 错）：
   - 已落地并自检：S4a（`Parse/GeometryCache.cs` 87KB、`Parse/SceneOpts.cs` 27KB、`Parse/UfbxiContext.Cache.cs`，oracle `tools/s4a_oracle.*` + `tools/S4aCheck/`）；S4c（`Parse/Topology.cs` 55KB、`Parse/Subdivide.cs` 122KB，oracle `tools/s4c_oracle.*` + `tools/S4cCheck/`；**`ufbxi_finalize_mesh_material` 落在 Subdivide.cs:531**，勿重复移植）。
   - **S3b（19366-21643）与 S3c（21632-23935）完全缺失**：`ufbxi_fetch_maps`/`finalize_shader_texture`/`deduplicate_textures`/`fetch_file_textures`/`modify_geometry`/`postprocess_scene`/`validate_indices` 与 `ufbxi_finalize_scene`/`update_node`/`update_scene` 全链均无实现；前一个 S3b 代理无任何产出（无 SceneFinalize.cs、无 tools/s3b_oracle.*）。当前端口仍停在 `Load.cs:93` 的 `ufbxi_finalize_scene` seam。
   - 现状可用：**S4a 已代为落地 `AxisMatrix`（SceneOpts.cs:161）与 `RoundIfNear`（SceneOpts.cs:52）**，S3c 直接调用即可。
2. **剩余桩**（全部待接）：`Load.cs:93`（S3b/S3c）；`SceneOpts.cs:370`（`ufbx_compute_topology` → 应接 `UfbxTopology.ComputeTopology`，S4c 已落地）；`SceneOpts.cs:163` 为过期桩（AxisMatrix 已实现，接力者应确认并删除该死代码）。
3. **接力指令**：`C:/Workspace/ufbx-cs/HANDOFF_S3bc.md`——单代理完成 S3b+S3c（两段互相依赖，不拆给两个代理），含精确函数清单/行号、文件所有权、Load.cs seam 的完整驱动序列、既有接口速查表、硬性验证项（LoadCheck/GraphCheck/DomCheck/mathvec + s3bc oracle + 变异对照 + golden 首跑）。
4. **golden 口径（已确认，务必传递）**：`test/hash_scene.c` 用 `load_external_files/evaluate_caches/evaluate_skinning/target_axes=right_handed_y_up/target_unit_meters=1.0`，frame>0 走 `ufbx_evaluate_scene`。故 S4a（已落地）与 S4b（未开始）都是 golden 必经路径；S4b 仍需独立波次。
5. **所有权**：`Parse/{SceneFinalize,SceneUpdate}.cs`、`Parse/UfbxiContext.{SceneFinalize,SceneUpdate}.cs` 归接力者；`Parse/Load.cs` 的 seam 与 `tests/Ufbx.Tests/SceneProvider.cs` 的接线在接力任务书中授权。

### 更正（22:20）——上条 item 1 关于 AxisMatrix 的判断有误
`Parse/SceneOpts.cs:161-164` 的 `UfbxiSceneOpts.AxisMatrix` **是活桩**（throw `s4a-ufbxi_axis_matrix`，ufbx.c:23659-23677 未移植，S3c 范围），不是"已落地"。接力者必须实现它（建议实现在 `Parse/SceneUpdate.cs`，桩体改一行转发）。已落地且可直接调用的是：`RoundIfNear`（SceneOpts.cs:52）、`MirrorMatrix/MirrorMatrixDst/MirrorMatrixSrc`（SceneOpts.cs:107-150）。另 `Parse/SceneOpts.cs:370` 的 `ufbx_compute_topology` 桩应接 `UfbxTopology.ComputeTopology`（Topology.cs:1110，S4c 已落地）。HANDOFF_S3bc.md 已同步更正，并放开三处最小 diff 许可（Load.cs seam + SceneOpts.cs 两处桩体 + tests/SceneProvider.cs）。

## 更新（S4b 线会话，2026-10-04）——S4b-1 差分已收口；oracle 数学绑定是本轮唯一根因

1. **S4b-1 现在 ALL MATCH**：`cd C:/Workspace/_analyze_ufbx && dotnet C:/Workspace/ufbx-cs/tools/S4bCheck/bin/Release/net8.0/S4bCheck.dll C:/Workspace/ufbx-cs/tools/s4b_oracle.txt C:/Workspace/ufbx-cs/tools/s4b_corpus.txt` → **11461 行 / 0 分歧**（30 个带动画文件 × 38 个时间点，覆盖 `ufbx_evaluate_props[_flags]`、`ufbx_evaluate_prop[_flags]`、`ufbx_evaluate_transform[_flags]`、`ufbx_evaluate_blend_weight`、`ufbx_evaluate_curve[_flags]`、`ufbx_evaluate_anim_value_*`、层权重/`might_contain_id`、`ufbx_find_*`）。
2. **之前 243 条 P/Q/R 分歧是 oracle 侧的假阳性，不是移植缺陷**。根因与逐条证据见 `PORTING_NOTES.md`「第二条地基规则」：`#include "ufbx.c"` 而不定义 `UFBX_EXTERNAL_MATH` 时，`UFBX_MATH_PREFIX` 展开为**空**宏（ufbx.c:238-277），`ufbx_atan2` 直接粘成 CRT 的 `atan2`（zig cc → windows-gnu → UCRT/Intel IML），而 goldens 的生成器 `test/hash_scene.c:284` 自己就定义了 `UFBX_EXTERNAL_MATH` 并链 `extra/ufbx_math.c`（`misc/run_tests.py:1541`）。端口 `UfbxMath` 是 `extra/ufbx_math.c` 的移植 ⇒ 与 goldens 同源，与旧 oracle 不同源。
3. **已落地的修正**：`tools/s4b_oracle.c` 在 `#include "ufbx.c"` 之前加了 `#define UFBX_EXTERNAL_MATH`（带完整解释注释）+ 构建行加 `extra/ufbx_math.c`；`tools/s4b_oracle.exe/.txt` 已按新配置重建/重生成。新增证据工具 `tools/_s4b_mathref.c`（CRT vs ufbx_math 逐函数偏差实测：atan2 12.1%、pow 4.1%、cos 0.27%、sin 0.26%、atan 0.03%，sqrt/fabs/rint/tan/copysign/fmin/fmax/nextafter 为 0）。
4. **其他 11 个 oracle 仍是 CRT 绑定**（animcurve/ascii/dom/graph/inflate/load/numeric/s3/s3bc/s4a/s4c/util，均未加该宏）。判定：**当前无害**，因为它们的 harness 全部 0 分歧，而端口实现的是 ufbx_math 语义 ⇒ 这些语料没触到会漂移的函数。**但任何重新生成/扩语料前必须先补 `#define UFBX_EXTERNAL_MATH` + 链 `extra/ufbx_math.c`**，否则会复现本轮的假阳性。后续波次若顺手，请把该宏补进各 oracle 源文件（2 行/文件，无需重跑）。
5. **变异对照已做**（证明差分不空转）：对 `Parse/Evaluate.cs` 的 additive `compose_rotation` 分支做两处独立变异——(a) `Slerp(Identity,b,weight*(1+1e-7))`、(b) 整条四元数往返换成朴素 `result += value*weight`——**各自都使 S4bCheck 从 0 分歧变成 310 分歧**（310 = 该路径的敏感度足迹：任何扰动都会翻转这些 P/Q/R rollup）。两处均已还原，还原后 0 分歧、`dotnet build S4bCheck` 0 警告。
6. **回归确认**：`dotnet run --project tests/Ufbx.Tests -c Release -- goldens tools/golden_hashes.txt` → **2179 files, 905 matched, 0 mismatched, 1274 load-errors**（1274 全部是 `frame>0`，需要 `ufbx_evaluate_scene` ⇒ 正是 S4b-2 的范围，不是回归）。`Parse/Load.cs` 的 element→scene 回指清扫无副作用。
7. **所有权（本轮登记）**：`tools/s4b_oracle.{c,exe,txt}`、`tools/S4bCheck/`、`tools/_s4b_mathref.c`、`src/Ufbx/Parse/Evaluate.cs` 的求值段、`PORTING_NOTES.md`「浮点语义」新增小节归 S4b 线。`tools/_s4b_{trace,atan,atan2,mathvec,math2,probe}.c` 是本轮诊断遗留，已无用（`_s4b_math2.c` 的假设被第 2 条推翻），可在收口后删除。
8. **下一步（S4b-2，已开始规划）**：场景深拷贝（替掉 `ufbxi_translate_element*` 26078-26111 的引用图 clone）→ `ufbxi_evaluate_imp`（26113-26454）→ `ufbxi_evaluate_scene`（26454-26520）→ 公开 `ufbx_evaluate_scene`（31186-31378），再把 `tests/Ufbx.Tests/SceneProvider.cs` 的 `frame>0` 接上跑 1274 条 golden。

## 更新（S4b-2 线会话，2026-10-04）——ufbx_evaluate_scene 落地，golden 2179/2179 全绿

1. **交付**：
   - `src/Ufbx/Parse/EvaluateScene.cs`（**新**，506 行）：`ufbxi_eval_context`（26054-26076）+
     `ufbxi_evaluate_imp`（26113-26452）+ `ufbxi_evaluate_scene`（26454-26491）。C 的地址重base
     （`ufbxi_translate_element`，靠 `dst_element + (p - src_element)` 算术）在端口里是
     `Dictionary<UfbxElement,UfbxElement>`；C 的逐元素 `memcpy(dst, src, ufbx_element_type_size[type])` 是
     `UfbxElement.CloneElement()`（`MemberwiseClone`，运行时类型天然正确）。文件头注释列了 5 条
     端口形态差异（两阶段 clone-vs-translate 为何与 C 的单循环等价、`ufbxi_check_err` 的 OOM 尾在托管侧
     不可达、refcount/`result_memory_used` 等统计无对应且 golden 不哈希、per-element 连接是**拷贝**而非
     view、C 不重建的列表故意继续指向源场景）。
   - `Api/UfbxApi.cs`：`UfbxApi.EvaluateScene`（C: `ufbx_evaluate_scene`，31186-31200）。`opts == null`
     按 C 的 `memset` 语义处理（skinning/caches/flags 全关），不是"用默认值"。
   - 模型层 clone 原语：`UfbxElement.CloneElement`/`UfbxConnection.Clone`、`UfbxProps/UfbxScene/UfbxAnim/
     UfbxShaderTexture.CloneShallow`、`UfbxMaterial{Fbx,Pbr}Maps.CloneMaps`、`UfbxMaterialFeatures.CloneFeatures`、
     以及 map/feature/material_texture/texture_layer/shader_texture_input/blend_keyframe/anim_prop/
     constraint_target/bone_pose 的 `Clone()`。每个都带 ufbx.c 行号，并注明**为什么必须拷贝**
     （C 把该结构 inline 在宿主 struct 里，memcpy 后 `ufbxi_fetch_maps`/`update_blend_channel`/
     `update_shader_texture` 会写回去）。
   - `Parse/SceneUpdate.cs`：`MaterializeTypedList`（42 路 typed list 具体化）与 `CastArray` 提为
     `internal`，evaluate 复用同一份而不是复制那张 42 case 表。
   - `tests/Ufbx.Tests/SceneProvider.cs`：`frame > 0` 接线（哈希 state 而非源场景，且 `FreeScene(src)`），
     `LoaderNotPortedException` 已无抛出点。
2. **验收**：`goldens tools/golden_hashes.txt` → **2179 files, 2179 matched, 0 mismatched, 0 load-errors**
   （上一轮 905 matched / 1274 pending）。`S4bCheck` → **11761 行 / 0 分歧**。
   `streamcheck` 2243020 checks / 3 failed（既有 3 条 ErrorType，非本轮回归）。`dotnet build ufbx-cs.sln -c Release` 0 错 0 警。
3. **差分工具扩容**：`tools/s4b_oracle.c` 现在 `#include "test/hash_scene.h"` 并用 golden 自己的
   `ufbxt_hash_scene()`，新增两类记录：
   - `V <fi> <frame> <hash>`：`ufbx_evaluate_scene(scene, NULL, time_begin + frame/fps, NULL, &error)` 后哈希
     评估态场景，frame = i*i (i=1..9)，**逐位等于 `tools/golden_hashes.txt` 里对应的 golden**
     （已抽验：`data/maya_anim_extrapolation_7700_binary.fbx` 9/9 条一致）。 ⇒ oracle 配置正确性也被
     顺带证明，且 V 就是 golden 本身。
   - `W <fi> <hash>`：V 扫描跑完后重新哈希**源**场景（C 侧全是 push 新内存，源场景必然不变），
     用于抓"clone 与源共享可变存储"这类别名缺陷。
   - `tools/S4bCheck.csproj` 把 `tests/Ufbx.Tests/HashScene.cs` 编进对拍器 ⇒ V 用的哈希与 golden 验收
     是同一份转写，不存在"两个 hasher 各自错"的可能。
4. **变异对照**（证明差分不空转，均已还原）：
   - M1 `numAnimated = 0`（跳过属性求值）⇒ **261/270** 条 V 分歧（9 条不变的是无动画文件）。
   - M2 跳过 `UfbxiSceneUpdate.UpdateScene` ⇒ **240** 条分歧，且分歧起点正好是第一条 V 记录。
   - M3 把 `material.Features` 改成与源共享（删掉 `CloneFeatures()`）⇒ **V/W 全 0 分歧（盲区）**。
     根因：`ufbxt_hash_element_ref_imp` 一律按 `element_id` 哈希元素引用，而 clone 与源同 id；且本语料的
     材质属性在评估时刻的求值结果与加载态一致。**结论：clone 的拷贝规则只能由 C 的 memcpy 语义论证，
     golden 证不了**（与 s3bc 的纹理去重盲区同类）。要覆盖它需要"同一源场景多次评估到不同时间、逐次对
     golden"的差分。已在 `golden-blind-spots` 记忆里登记。
5. **`prop_overrides` 滑窗当前是死代码**：`ufbx_anim.prop_overrides` 只由 `ufbx_create_anim`
   （26590-26650）填充，加载场景恒 0 条（oracle `S` 记录第 9 列全 0），所以
   `EvaluateElementProps` 里的窗口切片（必须切：`ufbx_evaluate_prop_flags_len` 见到非 0 override 数就
   早退，传整表会静默丢掉动画）要等 `ufbx_create_anim` 落地才有语料。
6. **所有权（本轮登记）**：`src/Ufbx/Parse/EvaluateScene.cs`、`Api/UfbxApi.cs` 的 evaluate 段、
   `Model/**` 新增的 Clone* 原语、`Parse/SceneUpdate.cs` 的 `MaterializeTypedList/CastArray` 可见性改造、
   `tests/Ufbx.Tests/SceneProvider.cs`、`tools/s4b_oracle.{c,exe,txt,err}`、`tools/S4bCheck/{Program.cs,S4bCheck.csproj}` 归 S4b 线。
   `src/Ufbx/Parse/Evaluate.cs`（S4b-1）不动。

## 更新（公开门面线会话，2026-10-04）——ufbx_as_* / 拓扑与顶点访问器 ABI 落地；S4b 差分扩到 X/Y

1. **交付**：
   - `src/Ufbx/Api/UfbxAs.cs`（**新**）：42 个 `ufbx_as_*` 降型（C: ufbx.c:33042-33083 / ufbx.h:5773-5814）。
     刻意判 `element.Type` 而不是运行时类型（C 判 discriminant 后强转）。
   - `src/Ufbx/Api/UfbxTopologyApi.cs`（**新**）：纯转发门面，28 个入口 = 拓扑 7 catch + 7 plain
     （C: ufbx.c:32400/32485/32492/32502/32509/32542/32593 + 33173-33187 + 32588/32622）
     + 顶点访问器 5 catch + 5 plain（C: ufbx.c:33001-33040 / ufbx.h:5757-5769）。
     `ufbx_panic*` → `ref UfbxPanic`；plain 形式不带 panic（内部用 scratch panic，见 Topology.cs 文件头）。
     **不**包含 `ufbx_find_face_index`（32389-32398，属 find_* 组，未移植）与 `ufbx_generate_indices`（在 `UfbxGeometryApi`）。
   - `src/Ufbx/Parse/Topology.cs`：新增 5 个 catch 访问器主体与 3 个此前缺的 inline plain 访问器
     （`GetVertexVec2/Vec4/WVec3`），并抽出 `IndicesCount()`/`ValueCount<T>()`：C 的非存在属性是
     NULL `values`/`indices` ⇒ count 0，端口是 null 数组 ⇒ 同样 0（越界检查因此照常触发，与 C 一致）。
     `CatchGetVertexWVec3` 保留 C 的细节：**界用 `values.count`，读的是 `values_w[ix]`**（ufbx.c:33038）。
2. **验收**：`goldens` **2179/2179、0 分歧、0 load-error**；`S4bCheck` **11821 行 / 0 分歧**（新增 X 30 + Y 30）；
   `S4cCheck` **332068 记录 / 0 失败**（拓扑段无回归）；`dotnet build ufbx-cs.sln -c Release` 0 错 0 警；
   `streamcheck` 仍是既有 3 条 ErrorType（归错误形态线）。
3. **差分扩容（`tools/s4b_oracle.c` + `tools/S4bCheck`）**：
   - `X <fi> <z> <i>`：语料里每张 mesh 的 7 个顶点属性（crease/uv/position/normal/tangent/bitangent/color）
     用 catch **和** plain 两种形式读**每一个下标**，外加 `index == indices.count` 的越界探针；每次读把
     `did_panic` + `message_length` + message 字节一起折进哈希。⇒ 这是端口 panic 管线
     （`ufbxi_panicf_imp`/`ufbxi_vprint`/`ufbxi_vsnprintf`）在整条差分里的**第一次真实执行**。
   - `Y <fi> <z> <i>`：合成属性扫描（`dump_synth_access`），覆盖语料不可能产生的两条分支：下标为
     `UFBX_NO_INDEX`（C 读 `values.data[-1]`，即 ufbx.c:28003 的零 guard 元素；C 侧真的分配了 guard，
     端口侧由"返回零"建模），以及"对 `values` 越界但对 `values_w` 合法"的下标。**只跑 catch 形式**：
     plain 内联版无任何边界检查，越界读在 C 是 UB、在端口不可表示（端口的 `Values.Length` 就是 C 的
     `values.count`）。
   - oracle 重建已按 PORTING_NOTES 的强制命令行（`-DUFBX_EXTERNAL_MATH` + `extra/ufbx_math.c`），
     且**先证明过**：重建成品里除新增 X/Y 之外，11761 条既有记录与重建前逐字节相同。
4. **变异对照（均已还原）**：
   - M-A `CatchGetVertexWVec3` 的界改成 `values_w.count` ⇒ **只有 Y 分歧（30 条），X 全绿**。
     根因：语料 `values_w` 恒空（要 `ufbx_load_opts.retain_vertex_attrib_w` 才填，ufbx.c:12908-12921），
     所以这条 C 细节**只能靠合成语料**覆盖 —— 这也正是加 Y 的理由。
   - M-B panic 文案改一个词 ⇒ **只有 Y 分歧** ⇒ panic 字符串确实进了哈希（不是只哈希了布尔位）。
   - M-C 5 个 catch 的界 `ix < values.count` 改成 `ix <=` ⇒ 端口在第一条 Y 记录上抛
     `IndexOutOfRangeException` ⇒ 越界下标条目确实被读到（Y 的 `idx` 里含 `8 == NumValues`）。
   - 残留盲区（登记不处理）：哨兵 `indices`（`IndicesCount → int.MaxValue`）在公开场景里不可达
     （finalize 时 `ufbxi_patch_index_pointer`，ufbx.c:22044+ 已换成真数组），所以那条分支只有代码论证。
5. **门面命名约定（本轮固化）**：每个 C 前缀一个 public static class —— `ufbx_evaluate_*`→`UfbxEvaluate`、
   `ufbx_dom_*`→`UfbxDom`、`ufbx_as_*`→`UfbxAs`、几何/细分→`UfbxGeometryApi`、load/evaluate 入口→`UfbxApi`、
   拓扑/法线/顶点访问器→`UfbxTopologyApi`；方法名取 C 名去掉前缀的 PascalCase（catch 形式保留 `Catch` 前缀），
   每个转发都带 `// C: <cname> (ufbx.c/ufbx.h:<range>)`。新门面只做转发，函数体仍留在 `Parse/**` 的 internal 类里，
   这样既有差分（S4c/S4b/S3bc）继续覆盖同一份实现。
6. **未移植公开 ABI 盘点与顺序（本轮结论，替代 S4b-2 第 8 条"下一步"）**：
   - **纯门面（内部已移植，只差 public）**：inflate（`UfbxiInflate.UfbxInflate`，Inflate.cs:478）、
     geometry cache 的 load/read/sample（GeometryCache.cs:1477/1701）、`ufbx_scene_*` opts 位（SceneOpts.cs:405+）、
     find_* 场景查找（SceneFinalize.cs:2556/2587/2594/2618/2677/2700/2724）、math 转发（Math/Types.cs:75-755）、
     `default_open_file`（Load.cs:763）、14 个非 `_len` 的 strlen 转发、refcount 空操作
     （retain/free_scene 30596、anim 31228/31239、baked_anim 31299/31309、geometry_cache 32674/32685、
     line_curve 32367/32378、`ufbx_is_thread_safe` 30505）。**下一波按这个顺序继续清门面。**
   - **需要新 C 体**：`ufbx_create_anim`（31202-31226 → `ufbxi_create_anim_imp` 26560-26676，顺带解锁
     `prop_overrides` 滑窗）、scene `find_*`（30720-30820）、bake 链（31250-31297 → 27715-27773 + 约 26700-27800，
     `evaluate_baked_*` 31348-31411；注意 v0.23.1 **没有** `ufbx_bake_scene`）、
     load/stream/stdio/open_memory（30414-30570，内部 25212-25455 与 6981-7235）、`ufbx_format_error`
     （30606-30642）、thread pool（32984-32995 + `ufbxi_thread_pool_execute` 6009-6017）、
     skinning（`ufbxi_evaluate_skinning` 26413-26419，仍是 NotPorted）。
7. **所有权（本轮登记）**：`src/Ufbx/Api/UfbxAs.cs`、`src/Ufbx/Api/UfbxTopologyApi.cs`、
   `src/Ufbx/Parse/Topology.cs` 的顶点访问器段与 `IndicesCount/ValueCount`、
   `tools/s4b_oracle.{c,exe,txt}` 的 X/Y 段、`tools/S4bCheck/Program.cs` 的 Acc*/DumpVertexAccess/DumpSynthAccess
   归公开门面线；`tools/S4cCheck` 与 `Parse/Subdivide.cs` 未改。

## 更新（公开门面线会话 2，2026-10-04）——find_*/get_* 场景查找 ABI 落地；S4b 差分扩到 F（8 段/文件）

1. **交付**：
   - `src/Ufbx/Parse/SceneFind.cs`（**新**，`internal static class UfbxiSceneFind`）：ufbx.c:30738-30833
     的全部 9 个函数体（`find_element_len`、`get_prop_element`→复用 SceneFinalize、`find_prop_element_len`、
     `find_node_len`、`find_anim_stack_len`、`find_material_len`、`find_anim_prop_len`、`find_anim_props`、
     `get_compatible_matrix_for_normals`），加 `SafeString()` ＝ C 的 `ufbxi_safe_string`（5030-5034）物化版。
     三条表示差异写在文件头：
     - `ufbxi_macro_lower_bound_eq/_upper_bound_eq` **不重复誊写**，改用 `Parse/SceneBuild.cs:1160/1178`
       已有的那份（为此把两者由 private 提为 `internal`）；
     - C 的 `anim_props` 按 `ufbx_element*` 指针序排序，端口序代理是 `ElementId`
       （SceneBuild.cs:1527-1532 排序时用的就是它，`ufbxi_find_anim_prop_start` 亦然）；
     - C 的 `ufbx_anim_prop_list`/`ufbx_shader_prop_binding_list` 是 `{ data, count }` **视图**，端口返回
       拷贝切片，`null` ≡ `{ NULL, 0 }`（沿用 `FindShaderPropBindings` 的既有口径）。
       C 的 `length == SIZE_MAX`（"按 strlen 量"）不建模：那种串在 C 里也永远等不到真 interned 名，
       所以端口在这一点上没有可观测差异（文件头已注明）。
   - `src/Ufbx/Parse/Evaluate.cs`：`UfbxEvaluate` 门面新增 **①** 5 个 strlen 属性访问转发
     （`FindVec3/FindInt/FindBool/FindString/FindBlob`，C: 33152-33156）**②**查找组全区
     （C: 30720-30833 + 31413-31484 + 33157-33168）：`FindPropConcat`、`GetPropElement`、
     `FindPropElement(_len)`、`FindElement(_len)`、`FindNode(_len)`、`FindAnimStack(_len)`、`FindMaterial(_len)`、
     `FindAnimProp(_len)`、`FindAnimProps`、`GetCompatibleMatrixForNormals`、`FindPropTexture(_len)`、
     `FindShaderPropBindings(_len)`、`FindShaderProp(_len)`、`FindShaderTextureInput(_len)`、`GetBonePose`。
     每个 strlen 形式都是 `X(name) => XLen(name, name != null ? name.Length : 0, …)`，每个 `_len` 形式进
     需要整串的主体前都过 `SafeString`。类头改述为"求值段 + 它所归属的 find_*/get_* 查找组"。
     **门面只做转发**，函数体仍在 `Parse/**`，于是既有 S4b/S4c/S3bc 差分继续覆盖同一份实现。
2. **验收**：`dotnet build ufbx-cs.sln -c Release` 0 错 0 警；`goldens tools/golden_hashes.txt`
   **2179/2179、0 分歧、0 load-error**；`S4bCheck` **12061 行 / 0 分歧（ALL MATCH）**（新增 `F` 240 条 =
   30 文件 × 8 段）；`S4cCheck` **332068 记录 / 0 失败**；`streamcheck` **2243020 / 3**（既有 3 条
   ErrorType，归 `ufbx_format_error` 线，非本轮回归）。
3. **差分扩容 `F <fi> <part> <z> <i>`**（`tools/s4b_oracle.c::dump_scene_find` + `S4bCheck::DumpSceneFind`，
   发射位置在 `Y` 之后、`V` 之前）：
   - 1：每个元素用**自己的名字**在**自己的类型**下查（命中）、在**下一个类型**下查（必须不命中），
     再过 node/material/anim_stack 三个 typed shorthand 与 strlen 形式；名字取全长/短一字节/空三种长度。
   - 2：8 个"必然不存在"的名字 × 42 个元素类型 × `_len`/strlen 两种形式（纯 miss 扫描，抓"越界命中"）。
   - 3：每个元素的每个 prop × {Node, Material, Texture, AnimValue} × `find_prop_element_len`/`get_prop_element`
     （后者走 `prop->name.data` 这条路）。
   - 4：每个 anim layer 的每个 anim_prop：精确名/短一字节/空/strlen 形式/foreign 元素（比较器的
     `element != key` 分支）+ `find_anim_props` 区间形式 + 对 foreign 元素的区间形式。
   - 5：material textures（全长 + 短一字节）、material props、miss 名扫描；有 shader 时再扫
     `find_shader_prop_bindings_len` 的 count/首末条、`find_shader_prop_len`、截断 bindings 的 count、miss 名两种形式。
   - 6：`find_shader_texture_input_len` 命中后把 `name` + value 联合的 4 个 double + `value_int` +
     `value_str` + `texture` + `texture_enabled` 一起折进哈希，再加一次 miss 名探针。
   - 7：`ufbx_get_bone_pose` 用每条 bone_pose 自己的 node，再加每个 pose 用最后一个 scene node 的 foreign 探针。
   - 8：`ufbx_get_compatible_matrix_for_normals` 的 NULL（恒等式）+ 语料每个 node + **合成 node 扫描**（见第 4 条）。
4. **变异对照（全部已还原并复验 ALL MATCH）**：
   - M1 `SafeString` 忽略 `length` ⇒ `F 1/4/5` 分歧（prefix-mode 真的在裁剪名字）。
   - M2 `FindElementLen` 的 eq 谓词去掉 `a.Type == type` ⇒ 端口在 typed shorthand 的强转上抛
     `InvalidCastException`（`find_material_len` 拿到 node）⇒ 该eq 检查是活的。
   - M2b 门面 `FindElement` 的 strlen 转发写成 `name.Length - 1` ⇒ **恰好 30 条 `F <fi> 2`** 分歧，
     其它段全绿 ⇒ 段 2 的 miss 扫描专门盯这条转发。
   - M3 part 8 去掉 `geom_rot_mat`（用恒等阵代替）⇒ **第一轮 0 分歧＝盲区**：语料里没有任何节点带
     非恒等 `geometry_transform.rotation`。M3b 同段去掉 `ufbx_matrix_for_normals` ⇒ 30 条 `F 8` 分歧，
     证明该段算术确实是活的，只是轮不到 geometric rotation 进场。
     **处置**：oracle 与端口同时加合成 node 扫描（4 个 `node_to_world` × 7 个 quaternion = 28 探针/文件），
     覆盖恒等/带平移与剪切/镜像（`det < 0` ⇒ `det_sign` 取 -1）/奇异（`det == 0` ⇒ 余子式全 0）四种矩阵，
     以及非恒等 quaternion 的 `ufbx_transform_to_matrix` 加法/乘法链。重生成 baseline 后 M3 复跑 ⇒ 30 条 `F 8` 分歧，盲区关闭。
     这也是本轮唯一一处**必须靠合成语料**才能覆盖的分支（与 `Y` 的 NO_INDEX 同类，登记以免下轮误以为语料够用）。
5. **oracle 重建证据**：新 exe 仍按 PORTING_NOTES「浮点语义」第二节的强制命令行构建
   （`-DUFBX_EXTERNAL_MATH` + `extra/ufbx_math.c`；唯一告警是文件内重复 `#define`，已知良性）。
   两次重生成都用 `tr -d '\r'` 归一 CRLF 后 `diff` 逐字节核对：加 `F` 的那次 ⇒ 11821 条既有记录全同；
   扩 `F 8` 的那次 ⇒ **仅** 30 条 `F <fi> 8` 变化（`total` 从 3/… 变 31/…），其余 12031 条全同。
6. **未移植公开 ABI 盘点（替代上一轮第 6 条）**：
   - **纯门面（内部已移植，只差 public）**：inflate（`UfbxiInflate.UfbxInflate`，Inflate.cs:478）、
     geometry cache 的 load/read/sample（GeometryCache.cs:1477/1701）、`ufbx_scene_*` opts 位
     （SceneOpts.cs:405/495/520/528/550/580）、math 转发（Math/Types.cs:75-755）、
     `default_open_file`（Load.cs:763）、refcount 空操作（`retain_scene` 30596、`free/retain_anim`
     31228/31239、`retain/free_baked_anim` 31299/31309、geometry_cache 32674/32685、line_curve
     32367/32378、`ufbx_is_thread_safe` 30505）。**下一波按这个顺序继续清门面。**
   - **需要新 C 体**：`ufbx_create_anim`（31202-31226 → 26560-26676）、bake 链（31250-31297 →
     27715-27773 + 约 26700-27800；`evaluate_baked_*` 31348-31411；v0.23.1 **没有** `ufbx_bake_scene`）、
     load/stream/stdio/open_memory（30414-30570，内部 25212-25455 与 6981-7235）、`ufbx_format_error`
     （30606-30642）、thread pool（32984-32995 + 6009-6017）、skinning（26413-26419，仍是 NotPorted）。
     原清单里的"scene `find_*`（30720-30820）"**本轮已清**。
7. **所有权（本轮登记）**：`src/Ufbx/Parse/SceneFind.cs`（新）、`src/Ufbx/Parse/SceneBuild.cs` 的
   `LowerBoundEq/UpperBoundEq` 可见性、`src/Ufbx/Parse/Evaluate.cs` 的 `UfbxEvaluate` 查找组与 strlen 转发段、
   `tools/s4b_oracle.{c,exe,txt}` 的 `F` 段、`tools/S4bCheck/Program.cs` 的 `DumpSceneFind`/`H*` 辅助归公开门面线；
   `tools/S4cCheck`、`Parse/Subdivide.cs`、`Parse/Topology.cs` 未改。

## 更新（公开门面线会话 3，2026-10-04）——`ufbx_find_face_index` 落地 + 引用计数族 + `ufbx_is_thread_safe`；`F` 扩到 9 段

1. **交付**：
   - `src/Ufbx/Parse/Topology.cs`：`UfbxTopology.FindFaceIndex`（C: `ufbx_find_face_index` ufbx.c:32389-32398，
     ufbx.h:5651）——复用 `Parse/SceneBuild.cs` 的 `LowerBoundEq`（宏的 `m_linear_size` 取字面 **4**：参考构建没有
     `UFBX_DEBUG_BINARY_SEARCH`/`UFBX_REGRESSION`，`ufbxi_clamp_linear_threshold()` ufbx.c:994-998 是恒等）。
     守卫写成 **`unchecked((ulong)index) > uint.MaxValue`**，这样它和 C 的无符号 `size_t` 比较完全同义：任何落在
     `[0, UINT32_MAX]` 之外的值（含 `-1`，C 侧就是 `SIZE_MAX`）都在触碰 mesh 之前被拒。`face_ix` 用 `-1` 表示 C 的
     `SIZE_MAX`，未命中经截断 cast 正好得到 `UFBX_NO_INDEX`。门面：`UfbxTopologyApi.FindFaceIndex(UfbxMesh, long)`。
   - `src/Ufbx/Api/UfbxApi.cs`：引用计数族 `RetainScene`(30596-30604)、`Free/RetainAnim`(31228-31237/31239-31248)、
     `Free/RetainMesh`(32635-32644/32646-32655)、`Free/RetainGeometryCache`(32674-32683/32685-32694)、
     `Free/RetainLineCurve`(32367-32376/32378-32387) 登记为**有据可查的空操作**：端口没有 arena、也没有手写引用计数，
     C 的 `ufbxi_get_imp` + magic + `release/retain_ref` 在托管侧没有对应物（既有注记见 `Parse/Subdivide.cs:43-47`），
     而 C 一侧的门控条件逐个写进了注释（`anim->custom`、`mesh->subdivision_evaluated || from_tessellated_nurbs`、
     `imp->owned_by_scene`、`line_curve->from_tessellated_nurbs`）。`ufbx_is_thread_safe`(30505-30508) ⇒ `IsThreadSafe() => true`：
     这是编译期答案（`UFBXI_THREAD_SAFE`，ufbx.c:633，只有非 C11-atomics 回退分支 714-715 才为 0），
     **已用强制命令行实测参考构建返回 1**。
   - 顺手修正两处 ufbx.c 行号引用：`ufbx_free_scene` 是 **30586-30594**（原注释 30590-30600）、geometry cache 一对是
     **32674-32683 / 32685-32694**（原 32674-32684 / 32686-32696）。
2. **验收**：`dotnet build ufbx-cs.sln -c Release` 0 错 0 警；`goldens tools/golden_hashes.txt`
   **2179/2179、0 分歧、0 load-error**；`S4bCheck` **12091 行 / 0 分歧（ALL MATCH）**（`F` 由 240 条增到 270 条 =
   30 文件 × 9 段）；`S4cCheck` **332068 / 0 失败**；`streamcheck` **2243020 / 3**（既有 3 条 ErrorType，非本轮回归）。
3. **差分扩段 `F <fi> 9`**（`tools/s4b_oracle.c::dump_scene_find` + `tools/S4bCheck/Program.cs::DumpSceneFind`）：
   每个 mesh 先折 `element_id` + `num_faces` + `num_indices`，再**逐面**取 5 个探针——`index_begin`（本面首）、
   `index_begin + num_indices/2`（本面中）、`index_begin + num_indices`（**必须落到下一个面**）、`+1`（越过下一面首）、
   `index_begin - 1`（前一面末界）；mesh 级再补 `num_indices`、`num_indices + 1`、`UINT32_MAX`、`UINT32_MAX + 1`、
   `(size_t) -1`（= `SIZE_MAX`）与 `mesh == NULL`。纯整数、无 libm ⇒ 与 `F` 其余段一样 CRT 无关。
4. **变异对照（三个都已还原并复验 ALL MATCH）**：
   - M1 去掉 `> UINT32_MAX` 守卫 ⇒ 恰好 25 条 `F <fi> 9` 分歧、其它段 0 变化。**登记一处边角**：`-1` 单独
     **不能**区分（C# 截断成 `0xFFFFFFFF` 后照样查不到面，与守卫的 `NO_INDEX` 同值），真正暴露它的是
     `UINT32_MAX + 1`（截断成 0 ⇒ 返回面 0）——这正是守卫改成 `ulong` 比较、并同时保留 `-1` 探针的理由。
   - M2 `less` 谓词 `<=` → `<` ⇒ 同样 25 条 `F 9` 分歧（面边界归属翻转）。
   - M3 `eq` 谓词上界 `ix < end` → `<=` ⇒ 25 条 `F 9` 分歧 ⇒ `less`/`eq` 两侧都是活的。
   - 25 而非 30：fi 9/10/12/29 四个文件**根本没有 mesh**（`F 9` 的 `total == 0`），fi 28
     （`data/synthetic_blend_order_7500_ascii.fbx`）有 21 个 mesh 但**一个面都没有**（`total == 126 == 6 × 21`；
     该文件里连 `PolygonVertexCount` 属性都不存在，已实测）⇒ 这 5 条 part 9 记录对三个变异都不敏感，
     **part 9 的语料灵敏度是 25/30 文件**，其余靠 mesh 级与边界探针保证。
5. **oracle 重建证据**：仍按强制命令行（`-DUFBX_EXTERNAL_MATH` + `extra/ufbx_math.c`）重建。新输出去掉 30 条
   `F <fi> 9` 之后与上一版 baseline **12061 条逐字节相同**（`diff --strip-trailing-cr`）。**新增经验**：oracle 产物是
   **CRLF**（Windows 文本模式 stdout），而 `grep`/管道会归一成 LF，用普通 `diff` 对比会看到"12061 行全不同"的假象
   （已写进 PORTING_NOTES「浮点语义」）。`tools/s4b_oracle.{exe,txt}` 已替换为新版（12091 行）。
6. **未移植公开 ABI 盘点（替代上一轮第 6 条）**：把 `ufbx.h` 的 **203 个 `ufbx_abi`** 名字与端口 **365 个 public 方法名**
   做了一次整体名字核对，逐条确认剩下的"未命中"全是**命名约定**造成的假阳性——`ufbx_as_*` 42 个 ⇒ `UfbxAs.*`
   （UfbxAs.cs 里正好 42 个 `public static Ufbx*`）、`ufbx_dom_*` 10 个 ⇒ `UfbxDom.*`（UfbxDomApi.cs 里正好那 10 个）、
   `ufbx_matrix_*`/`ufbx_quat_*`/`ufbx_vec3_*`/`ufbx_transform_*`/`ufbx_coordinate_axes_valid` ⇒ 结构体上的方法、
   `ufbx_evaluate_*` ⇒ 多行声明没被抓进名字表。**结论：没有漏网的纯门面名字。**
   - **纯门面（内部已移植，只差 public）**：inflate（`UfbxiInflate.UfbxInflate`，Inflate.cs:478 +
     `ufbx_inflate_input`/`ufbx_inflate_retain`）、geometry cache 的 load/read/sample（GeometryCache.cs:1477/1701，
     外加 `ufbx_load_geometry_cache_len` 的 strlen 转发）、`ufbx_scene_*` opts 位与 blend 偏移三件
     （SceneOpts.cs:405/495/520/528/550/580；其中 `ufbx_add_blend_shape_vertex_offsets` 32070+ 与
     `ufbx_add_blend_vertex_offsets` 32091+ 的 public 形态要先定 `vertices` 缓冲的入口形状——内部签名多了一个
     `offset`，因为托管侧传的是大数组的窗口）。
   - **需要新 C 体**：`ufbx_create_anim`（31202-31226 → 26560-26676）、bake 链（31250-31297 → 27715-27773 +
     约 26700-27800；`evaluate_baked_*` 31348-31411；`retain/free_baked_anim` 31299/31309 也留在这条线，
     因为没有别的入口能造出 `ufbx_baked_anim`；v0.23.1 **没有** `ufbx_bake_scene`）、
     load/stream/stdio + `open_file`/`open_memory`/`default_open_file` 族（ufbx.h:5413-5422 ⇒ ufbx.c:30414-30570，
     内部 25212-25455 与 6981-7235）、`ufbx_format_error`（30606-30642）、thread pool（32984-32995 + 6009-6017）、
     skinning（`ufbxi_evaluate_skinning` 26413-26419，仍是 NotPorted）。
     上一轮清单里的"scene `find_*`"与"refcount 空操作 + `ufbx_is_thread_safe`"**本轮已清**。
7. **所有权（本轮登记）**：`src/Ufbx/Parse/Topology.cs` 的面查找段（本轮新增）、`src/Ufbx/Api/UfbxTopologyApi.cs` 的
   `FindFaceIndex`、`src/Ufbx/Api/UfbxApi.cs` 的引用计数族与 `IsThreadSafe`、`tools/s4b_oracle.{c,exe,txt}` 的 `F 9` 段、
   `tools/S4bCheck/Program.cs` 的 `DumpSceneFind` part 9 归公开门面线；`Parse/Subdivide.cs`、`Parse/SceneFind.cs`、
   `Parse/Evaluate.cs`、`tools/S4cCheck` 未改。**下一波按第 6 条"纯门面"顺序继续清（inflate → geometry cache →
   scene opts / blend 偏移），之后进入需要新 C 体的 bake 线。**

## 更新（公开门面线会话 4，2026-10-04）——门面批 G：inflate + geometry cache 公开 ABI 落地；S4a 扩到 1069 条（新增 `RD`/`RDO` 正控制）

1. **交付（纯门面，只转发）**：
   - `src/Ufbx/Api/UfbxInflateApi.cs`：`Inflate(dst, dstSize, input, retain)` ⇒ `UfbxiInflate.UfbxInflate`
     （C: ufbx.c:3135-3280，ufbx.h:5409；C 的 `size_t` 返回按"size_t → int"约定收窄）。
   - `src/Ufbx/Api/UfbxGeometryCacheApi.cs`：6 个转发——`LoadGeometryCache`(24726-24763 / ufbx.h:5711)、
     `LoadGeometryCacheLen`(ufbx.h:5714，先过 `UfbxiSceneFind.SafeString`)、`ReadGeometryCacheReal`(32704-32867)、
     `ReadGeometryCacheVec3`(32941-32951)、`SampleGeometryCacheReal`(32869-32939)、`SampleGeometryCacheVec3`(32953-32963)。
     `ufbxi_check_opts_ptr()`(32669) 的"忘记清零"哨兵对托管 options 不可表达（与 `UfbxApi.LoadMemory` 同注）。
   - C 体仍在 `Parse/GeometryCache.cs` / `Parse/Inflate.cs`，两个 harness 都已改走门面：`tools/InflateCheck/InflateCheck.cs:698`
     经 `UfbxInflateApi.Inflate`，`tools/S4aCheck/Program.cs:151/206` 与新的 RD 段经 `UfbxGeometryCacheApi`，
     所以**既有 998 条记录现在也在覆盖转发层本身**。
2. **移植保真修正（`src/Ufbx/Parse/GeometryCache.cs` 读/采样路径 5 处）**：
   - 读缓冲元素数 4096 → **512**（`UFBXI_GEOMETRY_CACHE_BUFFER_SIZE`，ufbx.c:62，用在 32791）。注释里固定下这条推导：
     **分块对解码值不可见**——`mirror_ix` 由 `-= num_read` re-base(32838)，跨块保持全局 mod-3 相位，被取负的元素集合恒为
     `{g : g ≡ mirror_axis-1 (mod 3)}`；可见的只有 `read_fn` 的**请求尺寸序列**与 scratch 占用，故仍与 C 取齐。
     同时区分开 32770 的那个 **4096：seek 循环的字节 scratch**，与此 buffer 无关。
   - `UfbxiCacheConsts.MaxSkipSize`：`0x7fffffff` → **`0x40000000`**（ufbx.c:54；只有 `UFBX_REGRESSION` 才在 1000-1002
     重定义为 128，参考构建不是 REGRESSION），原注释行号误写 426-433 已纠正。
   - `opts` **别名 → 值拷贝**：新增 `CopyOpts`（C 按值复制 opts：32711-32718 / 32878-32885）。端口 opts 是引用类型，
     别名会让 `sample_*` 内部写入的 `additive/use_weight/weight` **永久改坏调用方对象**；顺带把默认打开回调的守卫
     简化成 `if (opts.OpenFileCb.Fn == null)`。
   - `*_vec3` 包装的**不完整尾向量**：循环改成遍历 `numRead` 个 real（`i/3`、`i%3`，ufbx.c:32949/32961），
     原来按 `vec3Read * 3` 会在截断时丢掉那半个向量（C 的 flat cast 允许尾向量部分写入，返回值才向下取整）。
   - `CacheSkip` 无 `skip_fn` 分支：`Read(...) >= 0` → **`!= 0`**，C 测的是字节数的**真值**(24116)，只有读到 0 字节才失败。
   - 另有段头/联合体行号订正（32704-32963、32696-32702、32704-32867、32869-32939、32941-32951）。
3. **新增差分正控制（`RD` / `RDO` 各 35 条）**：动因是读/采样路径**在语料里完全无覆盖**——语料每帧最多 36 个 real
   （FR 记录）、12 条 CH 全部 `mirror_axis == NONE`、采样只做 `count=4`，于是 512 分块、mirror 取负及其跨块相位、
   `scale_factor`、两种 seek 循环、endian swap、`bytes_read == SIZE_MAX` 守卫、截断 break、additive/weight 混合、
   vec3 尾向量全是暗区。`tools/s4a_oracle.c::dump_read_cases` 用内存假文件 + 用户 `ufbx_open_file_cb` 驱动四个公开入口：
   - `RD <case> <n> <out_reals> <call_hash> <data_hash> <reads> <skips>`：`call_hash` 是 FNV-1a-64 over 每次
     `read_fn`/`skip_fn`/`close_fn` 的**请求字节数与返回字节数**（错误读折入 `SIZE_MAX`）——这是观察"分块粒度本身"
     的唯一通道；`data_hash` 折输出区**全部 1200 个 double**（含未触碰尾部）以便暴露越界写。
   - `RDO <case> <ign_tr> <add> <use_w> <fn_is_mine> <user_is_null> <weight_hex>`：调用返回后调用方 opts 必须原样完好。
   - 数据取 `((b*61+i*37+11) % 251 - 100) * 0.125` 的小二进制有理数 ⇒ float/double 都精确、CRT 无关。
   - RD 块在 `main()` 里**排在语料循环之后**，因此前 998 条与旧 baseline 逐字节相同（`diff --strip-trailing-cr` 验过）。
4. **变异对照（6 个全部咬住，全部已还原并复验 1069/1069）**：
   - M1 分块 512→4096 ⇒ **32 条**失败：只有 `call_hash` 与 `reads` 变、`data_hash` 逐条不变——第 2 条那条推导的实验证据。
   - M2 `MaxSkipSize`→0x7fffffff ⇒ **1 条**（case 22：`skips` 4→2）。
   - M3 还原 vec3 尾向量循环 ⇒ **2 条**（case 12/13 的 `data_hash`）。
   - M4 opts 别名：直接 `opts = userOpts` 会让既有 SMP 记录（传 `NULL` opts）NRE 崩在 GeometryCache.cs:1750（信号有效、
     形态很差）；改 `userOpts ?? new …` 隔离后 ⇒ **3 条 `RDO`**（case 29/33/34 的 additive/use_weight/weight 泄漏）。
   - M5 去掉 mirror re-base ⇒ **25 条**；M6 去掉 endian swap ⇒ **52 条**（RD 的 BE 案例 + 既有 SMP 记录 659-663/724-728/
     765-769/805-809…：语料 `.mc` 是大端，说明 swap 早由 SMP 覆盖，本轮只是补齐 float/double 两条路径）。
5. **验收**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；`InflateCheck` **4613/4613**；
   `S4aCheck tools/s4a_oracle.txt` **1069/1069、0 失败**；`goldens` **2179/2179**；`S4bCheck`（在
   `C:/Workspace/_analyze_ufbx` 下运行）**12091 行 / 0 分歧 ALL MATCH**；`S4cCheck` **332068 / 0**；
   `streamcheck` **2243020 / 3**（既有 3 条 ErrorType，非本轮回归）。`tools/s4a_oracle.{c,exe,txt}` 已换新版：exe 按
   s4a 强制命令（`-O2 -DNDEBUG -fno-strict-aliasing -std=c11 -mcpu=x86_64 -ffp-contract=off -I …`，**不加**
   `EXTERNAL_MATH`，它是 CRT 绑定的）重建，重跑输出与新 txt 逐字节相同。
6. **所有权（本轮登记）**：`src/Ufbx/Api/UfbxInflateApi.cs`、`src/Ufbx/Api/UfbxGeometryCacheApi.cs`、
   `src/Ufbx/Parse/GeometryCache.cs`（5 处保真修正）、`tools/s4a_oracle.{c,exe,txt}` 的 RD/RDO 块、
   `tools/S4aCheck/Program.cs` 的 `RdCases`/`RdStream`/`RdOpenCb`/`RunRdCase`/`DoRd`/`DoRdo`、
   `tools/InflateCheck/{InflateCheck.cs,InflateCheck.csproj}` 走门面那处，归公开门面线；`Parse/Inflate.cs`、
   `Parse/SceneFind.cs`、`tools/S4bCheck`、`tools/S4cCheck` 未改（后者只重编）。
   注意：`tools/InflateCheck.csproj` 是 `EnableDefaultCompileItems=false` 的隔离工程，**新增门面文件必须手工列进
   `<Compile Include>`**；`tools/S4aCheck` 用 `..\..\src\Ufbx\**\*.cs` 通配，自动纳入。
7. **纯门面剩余 + 已知边角**：只剩 scene opts 位与 blend 偏移三件（SceneOpts.cs:405/495/520/528/550/580；
   `ufbx_add_blend_shape_vertex_offsets` 32070+ / `ufbx_add_blend_vertex_offsets` 32091+ 的 public 形态要先定
   `vertices` 缓冲 + `offset` 的入口形状，因为内部签名多一个 `offset`——托管侧传的是大数组窗口）。
   `ufbxi_cache_skip` 的真值怪癖(24116)目前仅由 loader 路径覆盖，还没有专门的合成控制。RD case 33 的行内注释已按
   实际语义改正（它是"两帧通道 + mirror X 的 `sample_vec3`"，不产生不完整尾向量；尾向量探针是 case 12/13）。
   **下一波：清 scene opts / blend 偏移门面，然后进入需要新 C 体的 bake / `ufbx_create_anim` / load-stream-stdio /
   `ufbx_format_error` / thread pool / skinning 线。**

## 更新（公开门面线会话 5，2026-10-04）——门面批 H：Skinning 公开 ABI 落地；S4a 扩到 1086 条（新增 `SKC`/`BSA` 正控制）

1. **交付（纯门面，只转发）**：`src/Ufbx/Api/UfbxSkinApi.cs`（ufbx.h:5598-5622 "Skinning" 一节 7 个入口）——
   `CatchGetSkinVertexMatrix`(31936-32026 / ufbx.h:5600)、`GetSkinVertexMatrix`(ufbx.h:5601-5603，C 是 catch 形态的
   inline 包装，`panic == NULL`)、`GetBlendShapeOffsetIndex`(32028-32041)、`GetBlendShapeVertexOffset`(32043-32048)、
   `GetBlendVertexOffset`(32050-32068)、`AddBlendShapeVertexOffsets`(32070-32089)、`AddBlendVertexOffsets`(32091-32103)。
   C 体仍在 `Parse/SceneOpts.cs`（`UfbxiSceneOpts`）；`tools/S4aCheck/Program.cs` 的 SK/BSI/BSV/BVO/BAV 五段全部改走门面，
   所以**既有 1068 条记录现在也在覆盖转发层本身**。两个 adder 的公开形态不带 `offset`（C 只碰 `vertices[index]`，
   `index < num_vertices`，32082-32087），内部签名多出的 `offset` 是大数组窗口用的，public 形态固定传 0。
2. **移植保真修正（`Parse/SceneOpts.cs` 2 处，都是本轮差分逼出来的真缺陷）**：
   - **缺 panic 站点**：`ufbx_catch_get_skin_vertex_matrix` 的 `ufbxi_panicf(panic, vertex < count, "vertex (%zu)
     out of bounds (%zu)")`(31939) 端口原先没有——只有一个静默的边界检查，越界时既不写 `ufbx_panic` 也不格式化消息。
     现补 `UfbxiSceneOpts.Panicf`(SceneOpts.cs:406，与 `Parse/Topology.cs:127` 同形态的私有副本) +
     `UfbxiVaList.AddSizeT`，错误宏口径按 C 的 `ufbxi_panicf`（不是 `CheckMsg/FailMsg` 系）。
   - **`size_t vertex` 是无符号比较**：C 的两处边界测试(31939、31941)都在无符号域，端口原来收 `int vertex`，
     `-1`/`2^32` 这类索引会被当成合法下标或静默别名。公开/内部签名改成 `long vertex`，两处比较都走
     `unchecked((ulong)vertex)`；blend 三件**不收窄成同样形态**，因为 C 的第一件事就是
     `uint32_t vertex_ix = (uint32_t)vertex`(32034)，门面在这里 `unchecked((int)vertex)` 恰好保留 C 保留的位。
3. **新增差分正控制（`SKC` 12 条 + `BSA` 5 条，1069 → 1086）**：`tools/s4a_oracle.c` 新增
   `dump_skin_catch()` / `dump_blend_add_shape()`，块排在语料循环之后、`DONE` 之前，故**前 1068 条与旧 baseline
   逐字节相同**（`diff --strip-trailing-cr` 验过）。
   - `SKC <ix> <fb> <did> <msglen> <msg> <m0:h>…<m11:h>`：SK 段只走无 panic 的 inline 包装，panic 站点、`%zu`
     格式化、`did_panic` 提前返回(3390)、`total_weight <= 0` fallback 分支(31982-31988)、`rcp_weight` 归一(31990-32003)
     与其 `rcp_weight == 0` 子分支、无符号边界全是暗区。探针含 `(size_t)-1`、`2^32`、`2^32+4`（**只看返回值分不出
     收窄与否**，`2^32` 别名到 vertex 0 也得到 identity，是 `did/msglen/msg` 三列把它逼出来的）；
     `rcp_weight == 0` 一支用 `1.4916681462400413e-154`（= double 的 `UFBX_EPSILON`，ufbx.c:70-72）作权重、并把
     `dq_weight` 置 0 才可达——否则 dual-quaternion 归一会算出 `0 * infinity` 的 NaN，payload 不具可移植性。
   - `BSA <ix> <nul> <nv> <weight:h> <d0:h>…<d17:h>`：BAV 只经 `add_blend_vertex_offsets` 间接进入 shape 形态，
     故 `weight == 0.0`/`!vertices` 提前返回(32072-32073)、`index < num_vertices` 过滤(32082)、
     只在 `i < weights.count` 生效的 `offset_weights` 乘法(32083-32086)需要独立探针。
4. **变异对照（9 个，全部已还原并复验 1086/1086）**：
   - 边界测试收窄成 `(uint)vertex` ⇒ **1 条**（只有 `SKC 8`，`2^32+4` 与 `SIZE_MAX` 仍越界、消息列用的是完整
     `ulong`，所以分不开——记作该探针的粒度上限）。
   - 删掉 panic 站点 ⇒ **5 条**（`SKC 5-9`）；`%zu` 实参喂 `AddUInt` ⇒ **3 条**（`SKC 7/8/9` 的消息数字）；
     去掉 `UfbxiPrint.Panicf` 的 `did_panic` 提前返回 ⇒ **1 条**（`SKC 11`，即"预置 panic + 越界"探针，
     它是这条早退唯一的证据）；`total_weight <= 0` 改成 `< 0` ⇒ **3 条**（`SKC 0/1/10`）；
     `fabs(total) > EPSILON` 改成 `>=` ⇒ **1 条**（`SKC 3`）。
   - `index < num_vertices` 改成 `index < vertices.Length` ⇒ **1 条**（`BSA 2`）；`offset_weights` 无条件施加
     （`weights[i % count]`）⇒ **3 条**（`BSA 0/3` **加既有 `BAV`**，说明这处原来也只在 BAV 的合并结果里被覆盖）；
     删掉 `vertices == null` ⇒ `BSA 4` NRE，harness 以 EXCEPTION 中止（信号有效，形态差）。
   - **已知暗区**：删掉 `weight == 0.0` 提前返回**完全不可观测**——0.0 乘任何 offset 加进已清零缓冲仍是逐位相同的
     0.0（`BSA 1` 全 0），它是纯快路径而非语义。登记以免下轮误当成覆盖。
5. **验收**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；`S4aCheck tools/s4a_oracle.txt` **1086/1086**；
   `InflateCheck` **4613/4613**；`goldens` **2179/2179**；`S4bCheck`（`C:/Workspace/_analyze_ufbx` 下、绝对路径）
   **12091 行 / 0 分歧 ALL MATCH**；`S4cCheck` **332068 / 0**；`streamcheck` **2243020 / 3**（既有 3 条 ErrorType）；
   另补跑 `S3bcCheck`（须在 `_analyze_ufbx` 下跑，见其 Usage）**1477/1477**、`S3SceneCheck` **13546/0**、
   `NumericCheck` **280169**、`UtilCheck` **6865**、`HashCheck` **79**、`AnimCurveCheck` **8000**、
   `AsciiCheck` **1737**、`DomCheck`/`GraphCheck`(691)/`LoadCheck`/`S2GeomCheck` 全 PASS。
   `tools/s4a_oracle.{c,exe,txt}` 已换新版（exe 按 s4a 强制命令重建：**不加** `EXTERNAL_MATH`，CRT 绑定）。
6. **所有权（本轮登记）**：`src/Ufbx/Api/UfbxSkinApi.cs`（新）、`src/Ufbx/Parse/SceneOpts.cs`（panic 站点 +
   无符号 `size_t vertex`）、`tools/s4a_oracle.{c,exe,txt}` 的 SKC/BSA 块、`tools/S4aCheck/Program.cs` 的
   `SkinCatch`/`SkcFallbackMatrix`/`DoSkc`/`DoBsa`/`ToHex`/`ChkMsg` 与 BSI/SK/BSV/BVO/BAV 走门面那几处，
   归公开门面线。`Util/Print.cs` 只动过又还原（变异对照），现与备份逐字节相同。
   **纯门面一波（A-H）到此清完**：下一波需要新 C 体——bake 链（含 `retain/free_baked_anim`）、`ufbx_create_anim`、
   load/stream/stdio + `open_file/open_memory`、`ufbx_format_error`、thread pool、skinning 计算体
   （`ufbx_skin_*` / `ufbx_blend_*` 的 evaluate 侧，本节只公开了单点访问器）。

## 更新（公开门面线会话 6，2026-10-04）——批 I：第一个"新 C 体"波次 `ufbx_format_error`；UtilCheck 扩到 7003 条（新增 `FE` 正控制）

1. **交付**：
   - `src/Ufbx/Util/ErrorFormat.cs`（新）：`UfbxiErrorFormat.FormatError(byte[] dst, int dstSize, UfbxError error)`
     ← C `ufbx_format_error`（ufbx.c:30606-30642 / ufbx.h:5336），`size_t` 返回按"size_t → int"收窄。
     C 体里唯一需要新机制的是**带偏移的 snprintf**：C 写 `dst + offset, dst_size - offset`，
     端口把 `UfbxiPrintBuffer` 的 `Pos` 当绝对下标、`Length` 当窗口右端（`offset + size`），
     返回时再折回相对长度 —— `SnprintfAt` 的注释里固定了"`size >= 1` 是不变式而不是检查"这条推理：
     `offset` 只会走到 `dst_size - 1`，所以窗口永不为空；一旦为空，绝对下标写法会让结尾 `'\0'`
     落到窗口**前一字节**，而 C（长度是相对值，`length == 0` 时根本不写 NUL）什么都不写。
   - `src/Ufbx/Api/UfbxErrorApi.cs`（新）：公开门面 `FormatError`，纯转发（这一节 ABI 里唯一的函数；
     `ufbx_error`/`ufbx_error_frame`/`ufbx_error_type` 是数据，早已在 Model 里）。
     **UtilCheck 是第一个手工列入 `Api/*.cs` 的隔离 harness**（`EnableDefaultCompileItems=false`）。
   - `src/Ufbx/Enums.cs`：新增 `UfbxConstants.SourceVersion`（ufbx.c:877，与 `HeaderVersion`(ufbx.h:270)
     分列两个宏）。三段拆位 `/1000000u`、`/1000u % 1000u`、`% 1000u` 逐字照抄。
2. **为什么必须合成**：`ufbx_format_error` 在 ufbx.c 里**没有任何内部调用点**（grep 只有定义与声明），
   且参考构建 `UFBXI_FEATURE_ERROR_STACK == 0`（ufbx.c:108/170-172）把栈帧 push 整段编译掉 ⇒
   真实 error 永远 `stack_size == 0`，golden/streamcheck 看不见这条路径，帧循环更是只能手工喂。
   C 侧的 `ufbx_error` 是公开 struct，oracle 可以直接手搭再调**真函数**，所以 `FE` 记录是正控制而非近似。
3. **新差分 `FE`（69 条，`util_oracle.txt` 2495→2564 行；非 FE 前缀逐字节不变）**：
   `FE <ix> <dst_null> <dst_size> <err_null> <desc_null> <desc_hex> <info_len> <info_hex> <stack_size>
    (<line> <func_hex> <fdesc_hex>){min(stack_size,8)} <ret> <out_hex>`。
   输入自带在记录里（不把探针表抄两份），输出是**两侧都预填 0xCD** 的缓冲区前 `max(dst_size,1)` 字节，
   于是"未触碰的字节 / NUL 的位置 / 越界写"全都可比（0 填充会把 `*dst='\0'` 藏起来）。
   覆盖：签名三个 NULL 门、C 的 NULL-vs-empty `description`（`Unknown error` 回退）、
   `0 < info_length < 256` 的**两个边界**（255 走括号支、256 走 else 支，同样的 info 字节）、
   `%.*s` 精度、帧行 `%*u` 的 6 宽（短于/等于/超过）、`min(stack_size, 8)`、以及绕着两条支路
   确切长度（29 与 44）的 dst_size 阶梯 1..33 与 40..48。
4. **变异对照（12 个，全部还原；`cmp` 确认 `ErrorFormat.cs` 与备份逐字节相同）**：
   - 删 `|| dstSize == 0` ⇒ **1 条**（`FE 2`）；`error == NULL` 不写 NUL ⇒ **2 条**（`FE 3/4`）。
     这两条合起来说明：`dst_size == 0` 必须**两种 error 形态都探**，否则两个 guard 互相顶替、谁都测不到。
   - `InfoLength > 0`→`>= 0` ⇒ **33 条**；`< ErrorInfoLength`→`<=` ⇒ **2 条**（`FE 13`，即 info_len=256 那条）。
   - 去掉 `%.*s` 精度（`AddInt(info_length)`→`AddInt(-1)`）⇒ **2 条**（`FE 15`）。
     **注意**：这条一开始是 0 失败——因为记录里 info 的 hex 长度恰好等于 `info_length`，精度恒不生效；
     补了 `fe_probe_span`（数组里放 12 字节、`info_length` 设 4）才让它成为精度的唯一证据。
     登记为方法教训：**长度字段型的输入，探针必须让"数据长度 ≠ 声明长度"**。
   - 版本拆位 `/1000u % 1000u`→`/100u % 1000u` ⇒ **78 条**；`line_width` 6→4 ⇒ **14 条**；
     `SnprintfAt` 忘加 `buf.Pos = offset` ⇒ **21 条**（单调用探针全绿，只有多帧/阶梯才暴露——
     又一个"绝对下标 vs 相对下标"只能靠多次连续写入现形的例子）。
   - 去掉 `min(stack_size, 8)` ⇒ `FE 23/24` 以 `IndexOutOfRangeException` 中止（信号有效，形态差）。
     `?? Unknown error` 改成 `?? ""` ⇒ **2 条**（`FE 5`）。
   - **已知暗区 2 处**：(a) `offset = min(offset + num, dst_size - 1)` 的 min **恒不生效**
     （`num` 本身就是 `min(pos, size-1)`，加上 `offset ≤ dst_size-1` 的归纳 ⇒ 上界永远不触），
     删掉它 0 条失败；真正的语义在 `if (num > 0)` 那一步。(b) `SourceVersion` 与 `HeaderVersion`
     在本构建同值（都是 23001），互换 0 条失败——它俩只有在 header/source 版本错配时才可区分。
5. **验收**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；`UtilCheck tools/util_oracle.txt`
   **7003/7003**（原 6865 + 69×2）；`goldens` **2179/2179**；`S4aCheck` **1086/1086**；
   `S4bCheck`（`_analyze_ufbx` 下、绝对路径）**12091 行 / 0 分歧 ALL MATCH**；`S4cCheck` **332068 / 0**；
   `S3bcCheck`（同 cwd）**1477/1477**；`InflateCheck` **4613/4613**；`streamcheck` **2243020 / 3**（既有 3 条 ErrorType）；
   `S3SceneCheck` **13546 / 0**、`NumericCheck` **280169**、`HashCheck` **79**、`AnimCurveCheck` **8000**、
   `AsciiCheck` **1737**、`DomCheck`/`GraphCheck`/`LoadCheck`/`S2GeomCheck` 全 PASS。
   `tools/util_oracle.{c,exe,txt}` 已换新版：exe 用 `zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off
   -I C:/Workspace/_analyze_ufbx`（**已实测**该命令重建的非 FE 前缀与旧 `util_oracle.txt` 逐字节相同，
   故把 util_oracle.c 头部注释的命令也补成这一串；此 oracle 不绑 CRT、加不加 `EXTERNAL_MATH` 均可，本轮未加）。
6. **所有权（本轮登记）**：`src/Ufbx/Util/ErrorFormat.cs`、`src/Ufbx/Api/UfbxErrorApi.cs`、
   `src/Ufbx/Enums.cs` 的 `SourceVersion`、`tools/util_oracle.{c,exe,txt}` 的 FE 块、
   `tools/UtilCheck/{Program.cs,UtilCheck.csproj}` 的 FE 段，归公开门面线。
   `Util/Print.cs` **未改**（`SnprintfAt` 建在新文件里，用现成的 `UfbxiPrintBuffer`/`Vprint`）。
   **纯门面一波（A-H）到此清完，批 I 起进入"新 C 体"阶段。** 公开 ABI 台账（本轮用
   `grep -oE '^ufbx_abi [A-Za-z0-9_ *]+ (ufbx_[A-Za-z0-9_]+)\(' ufbx.h` 权威重数）：ufbx.h 里 **114 个 `ufbx_abi`
   函数**，其中 **92 个已有公开对应体**（含 18 个 math 件，按类型作用域挂在 `Math/Types.cs` 的 struct 上），
   剩 **22 个**分四族：**bake 链 9**（`bake_anim`、`evaluate_baked_{vec3,quat}`、`find_baked_{node,element}[by_*]`、
   `retain/free_baked_anim`）、**`create_anim` 1**、**stream/stdio/open 9**（`load_stream{,_prefix}`、
   `load_stdio{,_prefix}`、`open_file{,_ctx}`、`open_memory{,_ctx}`、`default_open_file`）、
   **thread pool 3**（`run_task`、`set_user_ptr`、`get_user_ptr`）。`ufbx_format_error` 本轮已清。
   （按 ufbx.c 函数体行数另算一版：24134 行里 22206 行的 C 名在端口出现，约 **92.0%**；这是**上限估计**——
   名字提及≠函数体已移植，`ufbx_bake_anim` 就只在 LoadOpts.cs 的注释里被提过。两份口径都登记，别当成同一指标。）
   **下一波**：bake 链（体量大头：`ufbxi_bake_node_imp` 257 行、`ufbxi_finalize_bake_times` 121、
   `ufbxi_bake_anim` 104、`ufbxi_bake_postprocess_{quat,vec3}` 100/80，均在 ufbx.c:26774-27714），
   然后 `ufbx_create_anim`（`ufbxi_create_anim_imp` 116 行，26561）、stream/stdio/open（`ufbx_open_memory_ctx` 53 行）、
   thread pool；skinning 计算体（`ufbx_skin_*` / `ufbx_blend_*` 的 evaluate 侧，本节只公开了单点访问器）。

## 更新（bake 线会话 7，2026-10-04）——批 J + 批 J-补：bake 链主体落地，bake 差分 50261 条 0 分歧；顺带定稿"规则 11：C 的 `f` 字面量会被拓宽进 `ufbx_real`"

1. **交付（批 J）**：
   - `src/Ufbx/Parse/Bake.cs`（新，**1427 行**）：`ufbxi_bake_*` 全链逐函数移植，覆盖
     `ufbxi_bake_time/prop/context`（26688-26738）、`bake_prop_less`/`cmp_bake_time`/`bake_push_time`
     （26740-26771）、`bake_times`（26773-26821）与四张属性名表（26823-26838，端口用 interned 字符串的
     引用同一性复刻 C 的指针比较）、`sort_bake_times`（26848-26853，稳定 + 32 插入块）、
     **`finalize_bake_times`（26855-26976，121 行：去重/step 后处理/时间钳制）**、
     `add/sub_epsilon`（26978-26979）、`postprocess_step`（26981-27023）、
     **`bake_postprocess_vec3`/`_quat`（27025-27105 / 27107-27207）**、`bake_time_sample_time`（27209-27218）、
     `push_resampled_times`（27220-27239）、**`bake_node_imp`（27241-27498，257 行）**、`bake_node`（27500-27513，
     含 sibling re-bake LIFO 栈）、`bake_anim_prop`/`bake_element`/两个 less/`bake_anim`（27515-27713）、
     `bake_anim_imp`（27715-27773）；公开侧 `ufbx_bake_anim`（31250-31297）、retain/free（31299-31317）、
     `find_baked_{node,element}[_by_*]`（31320-31346）、`evaluate_baked_{vec3,quat}`（31348-31411，
     含二分→8 窗口→线性扫的完整形态）。
   - `src/Ufbx/Api/UfbxBakeApi.cs`（新，70 行）：9 个 `ufbx_abi` 名字的纯转发包，文件头登记了三条**不可表达**的
     偏离：C 的 `find_*` 返回 `data[]` 内部指针（端口返回副本 ⇒ 身份与"经返回值写回"无对应物，miss = `null`）；
     空 list 时 C 读 `data[count-1]` == `data[-1]`（UB，端口索引 -1 抛）；`ufbx_assert(scene)` 在参考构建是 no-op。
   - `tools/bake_oracle.c`（新，618 行）+ `bake_oracle.exe` + `bake_oracle.txt`（**79541 行**）：
     `zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH
     -I C:/Workspace/_analyze_ufbx bake_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c -o bake_oracle.exe`
     （bake 触及 `ufbx_quat_to_euler` 的 `atan2` ⇒ **必须**带数学核，见上文「第二条地基规则」）。
   - `tools/BakeCheck/`（新，Program.cs 692 行）：记录文法 `S/B/O/T/N/Q/E/P/R/F`，两侧对同一 (文件, 变体)
     惰性 bake 一次、按同一顺序走 **30 个 `ufbx_bake_opts` 变体 × 16 个采样时间**。
   - `tools/bake_corpus.txt`（61 条 `"<mode> <path>"`）。
2. **差分每条记录证明什么**（记法沿用 S4b 的"每条 = 一个正控制"）：
   `S` 61 条＝语料加载一致（含 `scene.anim` 是否存在的总门）；`B` 1830＝`ufbx_bake_anim` 的 `!anim` 解析支、
   返回/错误对、resolved anim 身份与 `ufbx_baked_anim` 全部标量（即 `ufbxi_bake_anim` 的时间记账 27695-27710）；
   `O` 1830＝**PORTING_NOTES #9 的值拷贝控制**（调用后 opts 逐字段原样 + 五个 effective default 不落调用方）；
   `T` 29280＝**只作输入**（oracle 的 16 条采样时间表），这条设计把 `ufbx_nextafter()` 从差分里**作为规则排除**，
   同时仍钉住扫描用的 double 位型；`N`/`Q` 各 11745＝`bake_node_imp`+`finalize_bake_times`+两个 postprocess
   的逐节点三通道 constant 标志、三个 key 计数、**整条 key 列表的 FNV**（时间/三分量/flag 全进哈希）+ 首键显式值，
   以及 `evaluate_baked_{vec3,quat}` 在"键上/键的 nextafter 邻域/全局表"三组时间上的采样（恰好落键与
   adjacent-double 两支是 `time == prev->time`、`prev[-1].time == time`、STEP_LEFT/RIGHT 三条支路唯一的入口）；
   `E` 4764 / `P` 8228＝`bake_element`/`bake_anim_prop`（元素 id、prop 数、名字 FNV、constant_value、键数+全键哈希+首键）；
   `F` 1830＝四个 `find_baked_*` 扫过**每个**节点与元素（命中与未命中都要）。
   合计校验 50261 条（79541 − 29280 输入）。
3. **批 J-补：为什么必须给 bake 差分加"加载模式"这一维**。`ufbxi_bake_node_imp` 的 scale-helper 半边
   （27280-27292 平移块、27335-27348 缩放块、27477-27495 子节点重 bake）只在场景里有 `is_scale_helper`
   节点时可达，而 helper 节点由 `ufbxi_pre_finalize_scene`（18478-18539）按 `ufbx_inherit_mode_handling`
   决定——默认 `PRESERVE` 下**一个都没有**，即批 J 首版差分把这条半壁完全照不到。语料因此从 37 条扩到 61 条
   （mode0 37 / mode1 11 / mode2 9 / mode3 2 / mode4 2），实测到的模式语义：
   - mode1 `HELPER_NODES`：`maya_human_ik_7400_ascii.fbx` 出 **50 个 helper**、62 个子节点走 R1 平移支；
   - mode2 `COMPENSATE`：修复前 0 helper（**本轮唯一真根因**，见下条）；
   - mode3 `COMPENSATE_NO_FALLBACK`：按构造恒 0 helper（18492 的条件里显式 `mode != NO_FALLBACK`）；
   - mode4 `IGNORE`：0 helper——`ufbxi_read_model`（12615-12620）把 `original_inherit_mode` 重写成 `NORMAL`，
     且 18119 的 `required` 不含 IGNORE ⇒ `ufbxi_pre_finalize_scene` 可提前返回。
     另记：`ufbxi_setup_scale_helper`（12556-12594）会把源节点的 `Lcl Scaling` prop 复位成默认值。
   配套探针 `tools/_bake_helper_probe.c`（+ `_bake_helper_probe/` 端口双胞胎，DUMP_PROPS 已带
   `parent=/helper=/e=<hex>` 位型）用于静态估算 `tLive/tElse/tAnim/tSAnim/sLive/sElse/sAnim/sSAnim`。
4. **本轮唯一根因是一类新错误：C 把 `float` 字面量写进 `ufbx_real`（double）**。`ufbx.c:18169-18171` 的三个
   epsilon 是 `const ufbx_real scale_epsilon = 0.001f;` 之类，端口按十进制抄 ⇒ 阈值偏半个 float ulp：
   `(double)0.01f = 0.0099999997764825820923` **低于** double `0.01`，于是 human_ik 在 COMPENSATE 下
   `fabs(scale.x) <= compensate_epsilon` 由真翻假，helper 决策翻转，**bake 差分 4958 条**不匹配
   （首条 `S 60 2 1 155 555 0 1` vs `205 605 0 1`）。判定链是用**私有插桩副本**
   `tools/_scratch/ufbx_dbg/{ufbx.c,ufbx.h}`（不动共享参考树）打出的实测行
   `mode=2 … cs=0.010000000000000000208 eps=0.0099999997764825820923` 定下来的。
   修 `SceneBuild.cs:302-304` 后归零。同法审计全仓，另修 4 处：`ufbxi_time_mode_fps[]` 的
   `29.97f/29.97f/23.976f/59.94f`（23646/23647/23651/23655，表是 `static const ufbx_real[]`）、
   `layer->ui_color` 默认 `(0.8f,0.8f,0.8f)`（23397）、`weight > 0.99999f` 两处（22244 构建 / 25785 求值）。
   已核实为忠实（左值本就是 float 或本就无 `f`）：`0.333333f/0.0001f`（14301-14483、15364-15401，
   `float weight_*`）、`1.175494351e-38f`（`Subdivide.cs:753`）、两处 crease 的尖对照
   （29621 `-= 0.1f` vs 29828 `-= (ufbx_real)0.1`，端口 `Subdivide.cs:1596/1893` 各自照抄）、
   `0.0000001f`（28085）。**定稿为 PORTING_NOTES.md 行为保真规则 11**，含审计命令
   `grep -oE "\b[0-9]+\.[0-9]+f\b" ufbx.c | sort | uniq -c` 与"判据只看接收变量类型"的口径。
5. **变异对照（两条 rig，共 45 例，全部还原并 `cmp` 确认与备份逐字节相同，0 例 RESTORE FAILED）**：
   - `tools/_mut_bake.sh` + `_mut_bake_cases.txt`（22 例 `H_*`）+ `_mut_bake_cases2.txt`（22 例 `M_*`/`E_*`），
     汇总 `tools/_mut_bake_sweep_J2.txt`（38 例）。**咬住的 31 例**：`H_t_pick` 5333、`H_t_const` 58（加模式后由暗转亮）、
     `H_t_push` 1931、`H_s_pick` 344、`H_s_resample` 680、`H_s_push` 408、`H_s_gate`/`H_t_gate` 48645（形态是 NRE）、
     `H_stack` 17661、`H_sibling_push` 1748、`H_inherit_gate` 464、`H_index_off` 23424（IndexOutOfRange 形态）、
     `H_flags_ish` 5794、`M_dedup_xor` 9295、`M_stepkey_next` 274、`M_stepkey_prev` 213、`M_leftgate` 360、
     `M_keepflags` 129、`M_mindist` 597、`M_maxeps` 154、`M_addeps` 30、`M_subeps` 32、`M_stepdur` 228、
     `M_epsbase` 20、`M_adjacent` 9、`M_mintime` 15、`M_maxtime` 12、`M_leftret` 33、`M_rightret` 38、
     `M_qprevshift` 2、`M_vec3const` 783、**`E_compseps` 4958（反向证明本波修复是承重的）**。
   - `tools/_mut_goldens.sh` + `_mut_eps_cases.txt`（7 例，汇总 `_mut_eps_runs.txt`）：把上述 fps/ui_color/0.99999f
     四处改坏跑 `goldens` ⇒ **7 例全 0**（2179 仍全绿）。
   - **暗区分类（7 例 0 失败，逐条给结论）**：
     (a) `H_t_resample`＝**语料缺口**：t-live 支只由 mode1 human_ik 触达，探针测得 `tLive=62 tAnim=61` 但
     `animNodes=1`——62 个带 helper 父节点的子节点**自己没有**动画的 `Lcl Translation`，故
     `resample_translation` 算出却不被消费（`bake_times` 压根不调用）。该支本身由 `H_t_pick/H_t_push/H_t_const` 覆盖。
     (b) `H_s_const`＝**语料缺口**：s 支的 live 半边已由 `H_s_pick` 344 / `H_s_resample` 680 / `H_s_push` 408 覆盖，
     else 半边要"嵌套 helper 的祖先后辈被更晚 bake"，而 helper 按节点序创建 ⇒ 祖先 helper 的 `typed_id` 更小 ⇒ 先 bake，
     语料里无一行落入。
     (c) `H_no_extrap`＝**语料缺口，不是 harness 洞**：标志确被下发（oracle `case 28` 与 `case 30`
     设 `evaluate_flags = UFBX_EVALUATE_FLAG_NO_EXTRAPOLATION`，端口 `BakeCheck/Program.cs:225/231` 同步，
     `Bake.cs:896-902` 在调 `UfbxEvaluate.TransformFlags` 前 OR 进 `FlagNoExtrapolation`）；但语料里没有
     "动画区间严格落在 bake 区间内部"的节点，钳制不外推与外推取值同值。
     (d) `M_antipodal`＝**语料缺口**：quat 后处理路径本身被 `M_dedup_xor`/`M_stepkey_*`/`M_qprevshift` 覆盖，
     只是语料内相邻 bake quat 从不出现负点积（`FixAntipodal` 恒等）。
     (e) `E_scaleeps`＝**语料缺口，窗口宽仅 4.75e-11**（`(double)0.001f − 0.001`）；`scaleEpsilon` 本身在
     端口 5 个消费点（`SceneBuild.cs:403/410/453/620/662`）活跃。
     (f) `E_pivoteps`＝**harness 缺口**：bake 差分的加载 opts 只设了 `inherit_mode_handling`
     （`bake_oracle.c:574-585`），`pivot_handling` 保持默认 `RETAIN`(=0)，而 `pivotEpsilon` 唯一消费者
     `SceneBuild.cs:505-514` 在 `AdjustToPivot` 门内 ⇒ 本差分不可达（要补得加加载 opts 维度）。
     (g) 4 处加载器修正（fps 三格 + ui_color + 0.99999f×2）＝**潜伏保真修正**：审计正确、当前差分观测不到。
6. **验收（本轮实测，非引用旧日志）**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；
   `BakeCheck`（在 `_analyze_ufbx` 下、绝对路径两个参数）**records 50261 / input(T) 29280 / mismatches 0 — ALL MATCH**；
   `goldens` **2179/2179，0 load-errors**；`streamcheck` **2243020 / 3**（既有 3 条 ErrorType，属另一条线）；
   `S3bcCheck` **1477/1477**；`S4bCheck` **12091 行 / 0 分歧 ALL MATCH**；`S4cCheck` **332068 / 0**；`S4aCheck` **1086**；
   `UtilCheck` **7003**；`mathvec` **72000**；`InflateCheck` **4613**；`NumericCheck` **280169**；
   `S3SceneCheck` **13546**；`HashCheck` **79**；`AnimCurveCheck` **8000**；`AsciiCheck` **1737 PASS**；
   `DomCheck`/`GraphCheck`(691)/`LoadCheck`/`S2GeomCheck` 全 PASS。回归电池脚本：`tools/_scratch/battery.sh`
   （`run <label> <cwd> <cmd…>` + 汇总 grep，可传 label 过滤；已知瑕疵：s3bc/animcurve/ascii 的汇总行不在
   它的 grep 里，需手工补看）。
7. **所有权（本轮登记）**：`src/Ufbx/Parse/Bake.cs`、`src/Ufbx/Api/UfbxBakeApi.cs`、
   `tools/bake_oracle.{c,exe,txt}`、`tools/BakeCheck/`、`tools/bake_corpus.txt`、
   `tools/_bake_helper_probe*`、`tools/_mut_bake*`、`tools/_mut_goldens.sh`、`tools/_mut_eps_*`、
   `tools/_scratch/{battery.sh,ufbx_dbg/,*.bak}`，以及本波改动过的
   `src/Ufbx/Parse/{SceneBuild,SceneUpdate,Evaluate}.cs` 的 float 字面量行，归 bake 线。
   `SceneBuild.cs` 的 epsilon 三行**同时**是加载线的承重件（goldens 依赖），改它必须重跑 goldens + BakeCheck 两条。
8. **公开 ABI 台账更新**：bake 族 **9 个已全部落地**（`bake_anim`、`retain/free_baked_anim`、
   `find_baked_{node,element}[by_*]`、`evaluate_baked_{vec3,quat}`）。ufbx.h 的 114 个 `ufbx_abi` 里
   **101 个已有公开对应体**，剩 **13 个**三族：**`create_anim` 1**（`ufbxi_create_anim_imp` 116 行，26561）、
   **stream/stdio/open 9**（`load_stream{,_prefix}`、`load_stdio{,_prefix}`、`open_file{,_ctx}`、
   `open_memory{,_ctx}`、`default_open_file`；`ufbx_open_memory_ctx` 53 行）、
   **thread pool 3**（`run_task`、`set_user_ptr`、`get_user_ptr`）。
   **下一波**：`ufbx_create_anim` ⇒ stream/stdio/open ⇒ thread pool；skinning 计算体（`ufbx_skin_*`/`ufbx_blend_*`
   的 evaluate 侧）另计。bake 的两个可补维度已登记：`pivot_handling`（解锁 `E_pivoteps`）与
   "动画区间 ⊂ bake 区间"的语料（解锁 `H_no_extrap`/`H_t_resample`/`M_antipodal`）。

## 更新（bake 线会话 8，2026-10-04）——批 K：`ufbx_create_anim` 链落地，create_anim 差分 13676 条 0 分歧；定稿"规则 12：C 的指针身份 ≡ 引用别名，内容文法只照得到一半"

> 本节**取代上一节第 8 条**的台账数字：`create_anim` 已落地 ⇒ 114 个 `ufbx_abi` 里 **102 个**有公开对应体，
> 剩 **12 个**两族（stream/stdio/open 9、thread pool 3）。

1. **交付**：
   - `src/Ufbx/Parse/CreateAnim.cs`（新，307 行，`UfbxiCreateAnim`）：整链逐函数移植——
     `ufbxi_check_string`（26506-26518）、`ufbxi_push_anim_string`（26520-26534，含 `prev_name` 短路）、
     三个比较器 `prop_override_prop_name_less`/`prop_override_less`/`transform_override_less`（26536-26558）、
     `ufbxi_create_anim_imp`（26560-26676，116 行本体）、`ufbx_create_anim` 的 ABI 壳（31202-31226）。
     文件头登记了 4 条**不可表达**偏离：`length == SIZE_MAX ⇒ strlen` 的哨兵形态、arena 拷贝在托管侧
     只是引用（⇒ 身份不可观测）、分配器/`ufbxi_anim_imp` 引用计数与 magic 为 no-op、`(int64_t)double`
     越界在 C 是 UB。
   - `src/Ufbx/Api/UfbxApi.cs:111`：`CreateAnim(scene, opts, error)` 门面；`FreeAnim`/`RetainAnim` 的注释同步
     更新（create_anim 现在真的置 `Custom`，但那对仍 no-op，因为没有 arena）。
   - `tools/create_anim_oracle.c`（新，731 行）+ `create_anim_oracle.exe` + `create_anim_oracle.txt`
     （**21762 行**）：`zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH
     -I C:/Workspace/_analyze_ufbx create_anim_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c
     -o create_anim_oracle.exe`。`UFBX_EXTERNAL_MATH` 是**强制**的：`V` 维度的 `ufbx_evaluate_scene()` 经
     layer 合成路径触及 `ufbx_quat_to_euler()`/`ufbx_pow()`，不带该宏会绑到宿主 libm（atan2 差 1 ULP）。
   - `tools/CreateAnimCheck/`（新，Program.cs 612 行，隔离 csproj 同 BakeCheck）：流式回放，(文件,变体) 惰性
     建一次 anim 并缓存，`HashOpts` 是 oracle `hash_opts()` 的逐字节镜像（HU64/HI64/HD/HU32 小端）。
   - `tools/create_anim_corpus.txt`（34 条，无 mode 列）＝ s4b 的 30 条 + 4 条带动画/层的
     `blender_279_empty_cube_6100_ascii` / `max2009_cube_texture_6100_binary` /
     `blender_272_cube_7400_binary` / `maya_anim_diffuse_curve_7700_ascii`。
   - `tools/_mut_createanim.sh` + `tools/_mut_ca_cases.txt`（26 例）+ 汇总 `tools/_mut_ca_sweep_K.txt`。
2. **32 个变体 × 34 个文件 = 1088 次调用**；输入侧由 oracle 自己下发（`I/Ia/Iw/Ip/It` 7814 条），
   **不再在两侧各镜像一张变体表**——这个设计把"跨语言变体表漂移"整类错误排除掉，
   与 bake 的 `T` 记录、UtilCheck 的自描述 `FE` 输入同构。
3. **差分每条记录证明什么**：
   `S` 34＝加载一致（`num_nodes/num_elements/num_anim_layers/anim_present`，总门）；
   `U` 272＝每文件 8 个采样时间的表（**只作输入**：帧 -13,-1,0,0.5,1,3,7,13 相对 `anim->time_begin`，
   含区间外的外推支）；`A` 1088＝`ufbx_create_anim()` 的 NULL/非 NULL **加 `ufbx_error` 的
   description/info 逐字节**（把"Invalid UTF-8"/"Duplicate override"/"layer_ids out of bounds"/
   "override_layer_weights[] count must match…"/默认"Failed to create anim" 五种描述与错误码
   **15/22/1** 全钉住；实测 170/102/102 条失败，714 条成功）；
   `L` 465＝`anim->layers[i]` 用 `typed_id`+`element_id`+名字字节+weight 表达（不指针对拍）；
   `W` 325＝`override_layer_weights`；`P` 3502＝`prop_overrides[i]` 在**最终（求值）序**下每个字段
   （`element_id`、`_internal_key`、名字、四个 double 位型、`value_int`、`value_str`）——
   intern 的内容后果、`value.x ↔ value_int` 互填（26604-26608）、排序错序都在这一条；
   `T` 1462＝`transform_overrides` 的 `node_id` + 10 个 double 位型；
   `O` 1088＝**规则 9 的值拷贝控制**（调用前后 opts 的 FNV 必须相同）；
   `V` 5712＝端到端消费：`ufbx_evaluate_scene(scene, anim, t, NULL, &err)` + golden 生成器自己的
   `ufbxt_hash_scene()`（`test/hash_scene.h`，端口 `UfbxHashScene.HashScene`）——
   **5712 条里有 1506 个不同哈希**，证明 override 确实改掉了求值场景，而不是"建了个 anim 没人读"。
   合计校验 13676 条（21762 − 8086 输入）。
   注：`V` 两侧都传 **NULL evaluate opts**（与 `tools/s4b_oracle.c`/S4bCheck 同口径），因为
   `ufbxi_evaluate_skinning` 属 S4c 未移植（`EvaluateScene.cs:383-385` 在 `Opts.EvaluateSkinning` 为真时抛
   `UfbxiReaderNotPortedException`）；NULL opts 下 C 的默认位本就是 `evaluate_skinning = false`
   （`test/hash_scene.c:136` 同形），所以两侧走同一子图，`V` 维度不含 skinning。
4. **两条本波特有的语义结论**（已定稿为 PORTING_NOTES.md 规则 12）：
   - C 的重复检测比 `element_id` **＋ `prop_name.data` 指针**（26641-26652），端口用 `ReferenceEquals`
     ＋ `prevName` 逐引用别名复刻；名字先按名排过序 ⇒ 同名必相邻 ⇒ 指针等值 ≡ 内容等值。
   - NULL `opts` ≡ 全零 `ufbx_anim_opts`（`if (opts) ac.opts = *opts;`），实测变体 0/1 的 `V` 哈希逐条一致，
     这条等价由 `V` 而非 `A` 钉住。
5. **变异对照（26 例，全部还原并 `cmp` 确认与 `tools/_scratch/CreateAnim.cs.bak` 逐字节相同，0 例 RESTORE FAILED）**：
   - **咬住的 17 例**（数字＝13676 条里的不匹配条数）：`C_utf8_nodesc` 170（去掉描述 ⇒ 错误码 15 退化成 1）、
     `C_utf8_skipped` 170（整条 UTF-8 门被跳过 ⇒ 反而成功返回）、`C_elem_swap` 6120、`C_key_zero` 6120、
     `C_intern_blind` 1820、`C_fill_drop` 3086、`C_xform_swap` 1476、`C_xform_le` 1094、`C_fill_trunc` 1842、
     `C_custom_off` 714、`C_layer_off` 434、`C_w_copy_short` 352、`C_ignore_off` 126、`C_dup_off` 102、
     `C_w_check_ge` 68、`C_prev_off` 68、`C_layer_nodesc` 34。
     两处形态值得记住：`C_fill_trunc`（`value.x`→`float` 再截进 `long`）首条就是
     `1000000000000000000` vs `9999999843067494`（1e18 的 float 化）；`C_prev_off` 只在**非全局长名**的
     两条去重支上翻转（68 = 2 支 × 34），全局名仍由表命中兜住——正是规则 12 的指针/内容分界。
   - **不咬的 9 例，逐条给结论（都是"按构造等价"，不是语料缺口）**：
     (a) `C_name_swap`（第一次排序方向反转）＝**按构造等价**：第一次排序只建立"同名相邻"这一**分块**性质，
     方向反转不改分块；可观测次序完全由第二次排序决定，而它的平手条件
     (`element_id`, `_internal_key`, 名字) 三项全等正是去重错误的条件 ⇒ 平手序不可能被观测到。
     (b) `C_dup_content`（`ReferenceEquals`→内容相等）＝**按构造等价**，理由同上（相邻同名共享引用）。
     (c) `C_prev_init`（`prevName` 初值改成真实属性名）＝**按构造等价**：只让首条目换用一个不同对象、同字节的名字。
     (d)–(i) 六个 `Q_*` 等价控制全部如设计不咬：`Q_strcmp_strless`（C 用 `strcmp` 而端口用 `ufbxi_str_less`，
     能到这一步的名字必无 NUL ⇒ 等价）、`Q_empty_norm`（空串的 `EmptyChar` 归一）、
     `Q_w_copy_len`（拷 `weights.Length` 而非 `numLayers`，条数检查已强制二者相等）、
     `Q_layers_null`（0 层时置 `null` 而非空数组，由 `V` 的 5712 条哈希一致从**行为上**钉住）、
     `Q_opts_alias`（拷贝→别名，因端口体从不写 `ac.Opts`；`O` 钉的是"不变"而非"是拷贝"）、
     `Q_intern_tail`（永不命中表尾 `"d|Z"`，只换引用不换字节）。
     ⇒ 由此确认 create_anim 的 **intern 只有"选错实例"这一半可被内容文法照到**（`C_intern_blind` 1820 条），
     "选对实例但换对象"那一半在任何语料下都照不到；后续波次不要为它加语料，要靠论证守。
6. **验收（本轮实测）**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；
   `CreateAnimCheck` **records 13676 / input 8086 / mismatches 0 — ALL MATCH**（约 2.5s）；
   回归电池 `tools/_scratch/battery.sh` **全绿**：goldens **2179/2179（0 load-errors）**、
   mathvec 72000、util 7003、inflate 4613、dom/graph 691/load/s2geom/s3scene 13546/hash 79、
   animcurve 8000、ascii 1737、numeric 280169、s4b **12091 行 0 分歧**、s4c 332068、
   s3bc **1477/1477**、bake **50261 / 0 分歧**、createanim **13676 / 0 分歧**；
   `streamcheck` 2243020 / **3**（既有 3 条 ErrorType，属另一条线，见项目记忆）。
   电池脚本本轮修了三处：grep 补 `passed|failed|CHECK`（原先 s3bc/animcurve/ascii 的汇总行被吞）、
   `run s3bc` 补传绝对 oracle 路径（原先在 `_analyze_ufbx` 下报 `oracle not found` 后 exit=2）、
   新增 `run createanim`。汇总存 `tools/_scratch/battery_after_K.txt`。
7. **所有权（本轮登记）**：`src/Ufbx/Parse/CreateAnim.cs`、`src/Ufbx/Api/UfbxApi.cs` 的 `CreateAnim` 段、
   `tools/create_anim_oracle.{c,exe,txt}`、`tools/create_anim_corpus.txt`、`tools/CreateAnimCheck/`、
   `tools/_mut_createanim.sh`、`tools/_mut_ca_cases.txt`、`tools/_mut_ca_sweep_K.txt`、
   `tools/_scratch/{CreateAnim.cs.bak,battery.sh,battery_after_K.txt}`，归 bake/动画创建线。
8. **公开 ABI 台账（更新上一节第 8 条）**：**102/114 已有公开对应体**，剩 **12 个**两族：
   **stream/stdio/open 9**（`load_stream{,_prefix}`、`load_stdio{,_prefix}`、`open_file{,_ctx}`、
   `open_memory{,_ctx}`、`default_open_file`；`ufbx_open_memory_ctx` 53 行）、
   **thread pool 3**（`run_task`、`set_user_ptr`、`get_user_ptr`）。
   **下一波**：stream/stdio/open ⇒ thread pool；skinning 计算体（`ufbx_skin_*`/`ufbx_blend_*` 与
   `ufbxi_evaluate_skinning`，S4c）另计——批 K 的 `V` 维度因此仍不含 skinning 分支。
   批 J 留下的两个可补维度（`pivot_handling`、"动画区间 ⊂ bake 区间"语料）仍待办。

## 更新（流/stdio 线会话 9，2026-10-04）——批 L：stream/stdio/open 9 个公开 ABI 落地，stream 差分 11395 条 0 分歧；定稿 PORTING_NOTES 规则 13

> 本节**取代上一节第 8 条**的台账数字：stream/stdio/open 已落地 ⇒ 114 个 `ufbx_abi` 里 **111 个**有公开对应体，
> 剩 **3 个**（thread pool：`run_task`/`set_user_ptr`/`get_user_ptr`）。

1. **交付**：
   - `src/Ufbx/Parse/StreamOpen.cs`（新，253 行，`UfbxiStreamOpen`）：`ufbxi_file_context`（6941-6946）、
     `ufbxi_begin/end_file_context`（6948-6975）、`ufbxi_fopen`（6981-7065，**只建模 `_WIN32` 分支**）、
     `ufbxi_stdio_open`（7135-7141）、`ufbx_open_file{,_ctx}`（30420-30443）、`ufbx_open_memory{,_ctx}`
     （30445-30503）、`ufbx_default_open_file`（30414-30418）。文件头登记 6 条不可表达偏离（分配失败、
     `ctx` 只是父分配器、`_begin_zero` 断言、"失败不动调用方 `ufbx_stream`"、"只建模 `_WIN32`"、
     `ufbxi_memory_close()` 二次调用在 C 是 double free）。
   - `src/Ufbx/Parse/InputStreams.cs`：`UfbxMemoryInputStream` 补 `close_cb` 与
     `(byte[],int,UfbxCloseMemoryCb)` 构造（无 closed 守卫，对应 C 的 double free 语义），
     `UfbxFileInputStream` 补 `(FileStream, bool ownsHandle)` 构造（`close=false` 是
     `ufbx_load_stdio_prefix`，30546）。
   - `src/Ufbx/Api/UfbxApi.cs`：9 个公开门面条目 + `using System.IO;`（stdio 条目把 C 的 `void *file_void`
     拼成 `FileStream`，所以这个文件——不只是 Parse/InputStreams.cs——依赖它）。
   - `src/Ufbx/Parse/Load.cs`：`DefaultOpenFile` 委托 + `UfbxiOpenFileStream` 持有者 + 延迟打开块
     （840-888，含 `filename_len == SIZE_MAX ⇒ strlen` 与"传给回调的是**已解析**的长度"）+ **进度接线**。
   - `tools/stream_oracle.c`（新，1089 行）+ `stream_oracle.exe` + `stream_oracle.txt`（**13996 行**）：
     `zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH
     -I C:/Workspace/_analyze_ufbx stream_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c
     -o stream_oracle.exe`，再 `stream_oracle.exe --list tools/stream_corpus.txt`（**语料走文件**，
     因为 MSYS 会重编码非 ASCII argv，与 `load_oracle.c` 同口径）。
     ⚠️ **交接文档里的 `-mcpu=x86-64` 是笔误**，zig 只认 `x86_64`（其余 oracle 与本文件头一致）。
   - `tools/StreamCheck/`（新，Program.cs 950 行 + 隔离 csproj，同 BakeCheck/CreateAnimCheck）+ 
     `tools/stream_corpus.txt`（9 条，含非 ASCII 名 `synthetic_aβカ😂_7500_ascii.fbx` 与一个 `.obj`）。
   - `tools/_mut_stream.sh` + `_mut_stream_cases.txt`（31 例）+ 汇总 `tools/_mut_stream_sweep_L.txt`。
     ⚠️ **`perl` 已不在 PATH 上**，批 J/K 的 `_mut_bake.sh`/`_mut_createanim.sh` 现在会静默失败；
     `_mut_stream.sh` 改成用 `python`（`C:/Users/cesun/.workbuddy/binaries/python/versions/3.13.12/python.exe`）
     做**字节级**替换，后续波次照抄这个模板。
2. **78 个变体 × 9 个文件 = 702 次调用**；输入侧仍由 oracle 自己下发（`S/I/Ip/Id` 2808 条），
   两侧不各镜像一张变体表（与 bake 的 `T`、create_anim 的 `I` 族同构）。
   变体表分九段：REAL 内存流（0-18）、`open_memory()` 单独调用（19-22）、REAL 文件流 +
   `ufbxi_fopen()` 路径表（23-43，`PATH_VALID/VALID_MAX/MISSING/BAD_LEAD/BAD_MID/TRUNC_SEQ/
   SURROGATE/EMOJI/EMPTY/NUL_JUNK/NUL_JUNK_MAX/LONG`）、stdio（44-48）、**SCRIPT**（49-63）、
   延迟打开（64-71）、256 字节间隔的进度（72-75）、延迟打开 + 内嵌 NUL（76-77）。
   REAL 与 SCRIPT 双模：REAL 用两侧各自的 `ufbx_open_memory()`/`ufbx_open_file()`（钉
   `Parse/InputStreams.cs`），SCRIPT 用规则流（钉加载器的 IO **时序**与错误路径）。
3. **差分每条记录证明什么**（11395 条 = 13996 − 2808 输入）：
   `S` 702＝两侧输入逐字节一致（payload kind/arg/长度 + FNV 总门）；
   `A` 702＝ABI 的 NULL/非 NULL + out 流是否被接线 + **`ufbx_error` 的 description/info 逐字节**
   （把 "Invalid UTF-8"（无 info）/ "File not found"（info = 原始路径）/ "Failed to load" 三者分开）；
   `C` 702＝**调用方自己的** `ufbx_error`（成功打开会清 6971、`ufbx_load_stdio(NULL)` 完全不动 30544）；
   `K` 702＝stdio 加载后的 `FileStream.Position`（`close_fn == NULL` ⇒ ufbx 不许关句柄）；
   `M` 702＝`ufbx_close_memory_cb` 收到的东西（`no_copy` 时就是**调用方的**缓冲区 ⇒ `mutate` 变体可见）；
   `F` 45＝自定义 `open_file_cb` 收到的**已解析** `path_len`、`info->type`、`info->original_filename`；
   `R` 702 / `H` 702 / `Q` 2403＝IO 回调日志（分类计数 / (kind,arg,ret) 三元组 FNV / 短日志全量，
   日志上限 4096 条但计数与摘要不设上限）；`P` 4342＝进度回调日志（`bytes_read/bytes_total`，
   含 `ufbx_load_stream_prefix()` 起始的 `bytes_total == 0`）；
   `V` 393＝端到端：golden 生成器自己的 `ufbxt_hash_scene()`（14 个不同哈希）。
4. **本波修掉的三处真实移植缺陷**（都是差分逼出来的，不是重写）：
   - `UfbxiStream.ProgressCb` **从未被赋值** ⇒ `ufbxi_report_progress()` 永远走
     `!progress_cb.fn` 早退，**进度回调一次都不发**。C 在每个报告点直接读 `uc->opts.progress_cb`
     （6671/6693/25254），端口把 `progress_interval` 下发了却漏了下发回调本身。修在
     `Parse/Load.cs`（opts 值拷贝之后 `uc.Stream.ProgressCb = uc.Opts.ProgressCb;`）。
   - `ufbxi_fopen()` 的内嵌 NUL：C 把 `path_len` 只当**解码上界**，文件名交给 `_wfopen_s()` 按 NUL
     终止 ⇒ `"a.fbx\0junk"` 打开 `"a.fbx"`；托管 `FileStream` 无此规则，未截断时报 FILE_NOT_FOUND。
     修在 `Parse/StreamOpen.cs` 的 `Fopen()`。
   - `ufbxi_ascii_refill()`（ufbx.c:9437）把 IO 错误当成 EOF：C 写 "IO error" 后 `return '\0'`，
     分词器当 `UFBXI_ASCII_END` ⇒ 加载**仍然成功**；端口原来 throw，把可恢复的 EOF 变成硬失败。
     修在 `Parse/Ascii.cs`（写描述 + `return '\0'`，不抛）。
5. **顺带修掉的 harness 自身缺陷**：`tools/LoadCheck/Program.cs` 用 `File.ReadLines()` 读语料，
   把 UTF-8 路径解成码位 >0xff 的字符串，再喂给端口的原始字节串模型 ⇒ `PathToUtf16()` 报
   "Invalid UTF-8"。改为按**原始字节**读 + `NativeRel()` 把原始字节串转成 .NET 原生路径做
   `File.Exists()` 诊断。修完 LoadCheck 0 分歧，且比之前更强（两侧都真的走非 ASCII 路径）。
6. **变异对照（31 例，全部还原并 `cmp` 确认与 `tools/_scratch/*.bak` 逐字节相同，0 例 RESTORE FAILED）**：
   - **咬住的 25 例**（数字＝11395 条里的不匹配条数）：`C_progress_unwired` **4342**（＝全部 `P`）、
     `C_mem_read_short` 2284、`C_mem_size_zero` 2228、`C_prefix_total` 1102、`C_close_tail_off` 1079、
     `C_fopen_nodesc` 162、`C_fopen_noinfo` 144、`C_progress_interval` 166、`C_nul_trunc_off` 87、
     `C_utf8_desc` 72、`C_utf8_info` 72、`C_endfc_ok_false` 72、`C_mem_skip_fail` 59、`C_nocopy_off` 57、
     `C_close_cb_drop` 45、`C_file_size_total` 36、`C_deferred_noinfo` 36、`C_deferred_nodesc` 36、
     `C_nocopy_on` 25、`C_endfc_noclr` 18、`C_stdio_null_touch` 18、`C_file_owns_handle` 180（抛
     `ObjectDisposedException`，因为 `K` 读不到已关闭句柄的位置）、`C_stdio_close_owned` 180、
     `C_deferred_pathlen` 9（变体 76/77：`F` 的已解析长度 40 vs 74）、`C_ascii_io_throw` 7。
   - **不咬的 6 例，逐条给结论**：
     (a) `Q_endfc_defstr`（改 `ufbxi_end_file_context` 的默认串 "Failed to open file"）＝**按构造等价**：
     `ufbxi_fix_error_type()` 只在 `fc->error.description` 为空时才用默认串，而 `ufbxi_fopen()`
     的两条失败路径都先写了描述 ⇒ 默认串经 open ABI 不可达。`C_fopen_nodesc` 剥掉报告点的 `$` 前缀后
     立刻咬 162 条，从**反方向**证明同一条规则。
     (b) `Q_endfc_nullerr`（`fc.End(null, true)`）＝**按构造等价**：成功分支只调 `ufbxi_clear_error(error)`，
     对 NULL error 是 no-op。
     (c) `Q_strlen_resolution`（`path_len == SIZE_MAX` 时不用 strlen 而用整串长度）＝**被另一条规则掩盖**：
     唯一受影响的变体 33（`PATH_NUL_JUNK_MAX`）解出的长度 38 变 74，但 `_wfopen_s` 照样在 NUL 处截断
     ⇒ 结果相同。截断本身由 `C_nul_trunc_off`（87 条）钉住。
     (d) `Q_nulterm_ignored`（`filename_null_terminated` 恒 false）＝**按构造等价**：`_WIN32` 分支
     `(void)null_terminated;` 直接丢弃它（ufbx.c:6985），变体 38 专门钉这条。
     (e) `Q_ctx_ignored`（`ufbx_open_file_ctx` 的 ctx 恒 0）＝**按构造等价**：ctx 是父分配器的
     `uintptr_t`（规则 4），端口无 arena；非 NULL 的合成值是野读（oracle 早期版本据此 segfault）。
     (f) `Q_ascii_io_nodesc`（IO 错误站点不写描述）＝**按构造等价**：唯一走到它的变体 53/文件 1 加载
     **成功**，成功尾 `ufbxi_clear_error` 把描述抹掉（ufbx.c:25618）。
     ⇒ 6 例全部是"按构造等价"或"被另一条规则掩盖"，**没有一例是语料/harness 缺口**。
7. **验收（本轮实测）**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；
   `StreamCheck` **records 11395 / input 2808 / mismatches 0 — ALL MATCH**；
   回归电池 `tools/_scratch/battery.sh` 全绿，与批 K 基线逐项一致：goldens **2179/2179（0 load-errors）**、
   mathvec 72000、util 7003、s4a 1086、inflate 4613、dom/graph 691、load 0 分歧、s2geom 0、
   s3scene 13546、hash 79、animcurve 8000、ascii 1737、numeric 280169、s4b **12091 行 0 分歧**、
   s4c 332068、s3bc **1477/1477**、bake **50261 / 0 分歧**、createanim **13676 / 0 分歧**、
   **`streamcheck` 2243020 / 3 failed（既有 3 条 ErrorType，属另一条线，未变）**，
   新增 `run stream`（从 `_analyze_ufbx` 跑，oracle 与 corpus 都传绝对路径）。
   汇总存 `tools/_scratch/battery_after_L.txt`。
8. **所有权（本轮登记）**：`src/Ufbx/Parse/StreamOpen.cs`、`src/Ufbx/Parse/InputStreams.cs`、
   `src/Ufbx/Api/UfbxApi.cs` 的 stream/stdio/open 段、`src/Ufbx/Parse/Load.cs` 的延迟打开与进度接线段、
   `src/Ufbx/Parse/Ascii.cs` 的 `Refill()`、`tools/stream_oracle.{c,exe,txt}`、`tools/stream_corpus.txt`、
   `tools/stream_port.txt`、`tools/StreamCheck/`、`tools/_mut_stream.sh`、`tools/_mut_stream_cases.txt`、
   `tools/_mut_stream_sweep_L.txt`、`tools/_scratch/{StreamOpen,InputStreams,Load,Ascii,UfbxApi}.cs.bak`、
   `tools/_scratch/{battery.sh,battery_after_L.txt}`，归流/stdio 线。
9. **公开 ABI 台账（更新上一节第 8 条）**：**111/114 已有公开对应体**，剩 **3 个**：
   `ufbx_run_task` / `ufbx_set_user_ptr` / `ufbx_get_user_ptr`（thread pool）。
   **下一波**：thread pool ⇒ skinning 计算体（`ufbx_skin_*`/`ufbx_blend_*` 与 `ufbxi_evaluate_skinning`，
   S4c）⇒ 批 J 留下的两个可补维度（`pivot_handling`、"动画区间 ⊂ bake 区间"语料）。
   批 L 未覆盖（C 的 UB/不可表达，已按"按构造等价"登记）：过量供给的 `read`、`ufbx_open_memory`
   的二次 `Close`（C 是 double free）、`stream == NULL`、分配失败、非 `_WIN32` 的 `fopen` 分支、
   `UFBX_NO_STDIO`。

## 更新（线程池线会话 10，2026-10-04）——批 M：thread pool 3 个公开 ABI 落地，公开 ABI 账本 **114/114**；定稿 PORTING_NOTES 规则 14

> 本节**取代上一节第 9 条**的台账数字：thread pool 已落地 ⇒ **114/114 全部有公开对应体，本仓
> 的"未移植公开 ABI"归零**。剩下的是覆盖深度，不是门面（见第 8 条）。

1. **交付**：
   - `src/Ufbx/Parse/ThreadPool.cs`（新，~390 行）：`ufbxi_task`/`ufbxi_task_imp`/`ufbxi_task_group`/
     `ufbxi_thread_pool`（5973-6007）、`execute`（6009-6017）、`update_finished`（6019-6029）、
     `wait_imp`/`wait_group`/`wait_all`（6031-6065）、`init`（6067-6089）、`free`（6093-6108）、
     `available_tasks`（6110-6113）、`flush_group`（6115-6128）、`create_task`（6130-6151）、
     静态 `run_task`（6153-6159），以及 `ufbx_thread_pool_context` 句柄注册表。
     文件头登记不可表达项（task 数组的分配失败 #4、真实并发、NULL `imp->fn`）。
   - `src/Ufbx/Api/UfbxApi.cs`：`ThreadPoolRunTask` / `ThreadPoolSetUserPtr` /
     `ThreadPoolGetUserPtr`（三个 `ufbx_unsafe` ABI），非法 ctx 抛
     `UfbxiThreadContextException`（**不是** `UfbxParseError`：C 没有这种失败，不能变成 `ufbx_error`）。
   - `src/Ufbx/Parse/Load.cs`：`ThreadPoolInit` 改为构造真正的 `UfbxiThreadPool`（并把它的 ctx 交给
     `init_fn`，此前传的是 `default(nint)`）；`FreeTemp()` 补 `ufbxi_thread_pool_free`（25421-25422）。
   - `src/Ufbx/Parse/Objects.cs`：`ReadObjectsThreaded` 从"退化成顺序遍历"升级为 C 的批处理循环
     （15132-15237：每批先 `WaitGroup`、读上一批、`FlushGroup`，`max_tasks =
     min(num_tasks/GROUPS, available_tasks())`）。
   - `src/Ufbx/Parse/Root.cs`：`ParseToplevelChildOwned()` —— C 传非 NULL `tmp_buf` 的那种形态，
     每个子节点一个新 `UfbxiNode`（原来的 `uc.TopChild` 复用对象撑不住"整批先解析后读取"）。
   - 两个任务生产者接入环形队列：binary 的 DEFLATE 任务（`Parse/DomNode.cs`，`RunDeflateTask` 改写成
     `UfbxiDeflateTask` 载荷 + `DeflateTaskFn`，站点 9090-9131）与 ASCII 的延迟数组任务
     （`Parse/AsciiDomNode.cs`，`deferred_size` + `ufbxi_ascii_store_array()` + `AsciiArrayTaskFn`，
     站点 10565-10657）。
   - `tools/pool_oracle.c`（新，~470 行）+ `pool_oracle.exe` + `pool_oracle.txt`（**2024 行**）：
     `zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off -DUFBX_EXTERNAL_MATH
     -I C:/Workspace/_analyze_ufbx pool_oracle.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c
     -o pool_oracle.exe`，再 `pool_oracle.exe --list tools/pool_corpus.txt`（语料走文件）。
     ⚠️ **oracle 二进制不进 `battery.sh`，改了 `pool_oracle.c` 要按上面这行手工重编**。
   - `tools/PoolCheck/`（新，Program.cs ~400 行 + 隔离 csproj，同 StreamCheck）+ `tools/pool_corpus.txt`
     （8 条：3 个 binary、3 个 ascii、1 个 6100 ascii「无 post-7000 数组 ⇒ 零任务」、1 个非 ASCII 名）。
   - `tools/_mut_pool.sh` + `_mut_pool_cases.txt`（39 例）+ 汇总 `tools/_mut_pool_sweep_M.txt`
     （照抄 `_mut_stream.sh` 的 python 字节级模板；⚠️ 锚点**必须是单行**，且不能是另一处的子串 ——
     `if (!WaitImp(Group, true)) ...` 就因为 WaitAll 那份只差缩进而命中 2 次）。
2. **21 个变体 × 8 个文件 = 168 次加载**；变体表仍只在 oracle 侧（`I` 记录自描述），端口从记录重建
   输入。三个维度：`num_tasks`（0→2048 / 2048 / 64 / 8 / 4 / 2 / 1 / 65536）、pool 是否成立
   （三者齐全 / 缺 `run_fn` / 缺 `wait_fn` / 无 pool）、`run_fn` 的行为（文档式扫一遍 / `index + 2048`
   的取模 / 逆序 / 每个跑两遍），加 `init_fn`/`free_fn` 与 `user_ptr` 往返，以及两条任务失败路径
   （payload 折半 / 中间字节取反）。
   ⚠️ **`run_fn` 不执行、或只执行一部分的变体被删掉了**：那类变体的场景内容取决于
   `ufbxi_push_array_data()` 分配出来的**未初始化**字节（`barbarian` 那个文件两次运行 `E` 哈希
   就不同），C 自身都不可复现 ⇒ 按 #4 登记，不进差分。
3. **差分每条记录证明什么**（1688 条 = 2024 − 336 输入）：
   `I` 168＝变体输入自描述；`S` 168＝两侧 payload 逐字节一致（含折半/取反两种变异）；
   `B` 664＝**回调流**（24 次 `init_fn`、312 次 `run_fn`、312 次 `wait_fn`、16 次 `free_fn`，
   各自带 `max_concurrent_tasks` / `(group, start_index, count)` / `(group, max_index)`），这是把
   "批处理边界由谁决定"钉死的那一维；
   `U` 688＝`get_user_ptr` 在五个时点（init 前 / init 后 / run / wait / free）的观测：
   NULL=0、自己存进去的那个 sentinel=1、其它=2；
   `A` 168＝加载结果与 `ufbx_error` 的 type/description/info 逐字节（把任务失败
   "Threaded ASCII parse error" 与默认 "Failed to load" 分开）；
   `E` 168＝端到端：golden 生成器自己的 `ufbxt_hash_scene()`（13 个不同哈希）。
4. **本波修掉/补上的既有缺口**（不是重写，是把"退化实现"补成真的）：
   - `ufbxi_thread_pool_init()` 此前**只传了 `default(nint)` 给 `init_fn`**（真正的 ctx 从未外泄），
     且从未建过 task 数组 ⇒ 三个公开 ABI 无处落脚。
   - `ReadObjectsThreaded` 此前是"解析一个读一个"（等价于 `ufbxi_read_objects`），C 是"整批解析 →
     等上一批 → 读上一批"，两者对 `run_fn`/`wait_fn` 的参数流完全不同。
   - 两个任务生产者此前都是**创建即内联执行**，环形队列里一个任务都没有 ⇒ `ufbx_thread_pool_run_task`
     在端口侧没有任何可作用对象。
   - 顺带：`tools/AsciiCheck/AsciiCheck.csproj` 是受限文件集，新增 `Parse/ThreadPool.cs` 后要显式加进
     `<Compile Include>`（`UfbxiContext.ThreadPool` 引用了它），否则该工程编译不过。
5. **变异对照（39 例，全部还原并 `cmp` 与 `tools/_scratch/*.bak` 逐字节相同，0 例 ERROR /
   0 例 RESTORE FAILED）**：**29 例咬住、10 例按设计（或按测量）不咬**。
   - 咬住的大头（数字＝1688 条里的不匹配数）：`C_ctx_zero` / `C_ctx_notregistered` **各 1443**、
     `C_batch_flush` 1422、`C_flush_no_advance` 1296、`C_flush_no_maxindex` 1232、
     `C_create_no_ring_check` 1165、`C_ascii_threshold` 1101、`C_deflate_no_task` 1086、
     `C_exec_success_bug` 1039、`C_wait_no_waitfn` 1127、`C_batch_waitgroup` 929、`C_wait_no_update` 933、
     `C_group_count` 931、`C_batch_maxtasks` 794、`C_api_index_plus1` 561、`C_avail_tasks` 509、
     `C_ascii_no_run` 336、`C_exec_nomod` 156、`C_owned_node` 119、`C_ascii_task_offset` 43、
     `C_free_no_freefn` 64、`C_userptr_{set_sink,get_null}` 各 84、`C_init_always` 45、
     `C_numtasks_default` 9、`C_free_no_wait` 8、`C_wait_cannotfail` / `C_update_desc_empty` 各 6、
     `C_wait_desc` 3。
   - **不咬的 10 例，逐条给结论**（全部归为"按构造等价"或"被另一条规则/不可达路径掩盖"，
     **没有一例是语料或测具缺口**）：
     (a) `Q_exec_error_empty`（`task->error = ""` 改成别的串）＝**不可达**：两个 task fn 失败时
     都先写了自己的 error ⇒ `execute` 的补空串分支永不生效。
     (b) `Q_exec_fn_guard`（去掉 NULL `imp->fn` 的守卫）＝**UB**：C 无条件调用 `imp->fn`，只有
     "从未创建的槽"才会是 NULL，而三个公开 ABI 都拿不到那种槽（会被 ctx 注册表先挡掉）。
     (c) `Q_numtasks_clamp`（去掉 `INT32_MAX` 夹取）＝**不可表达**：端口的 `NumTasks` 是 `int`。
     (d) `Q_free_enabled_check` / `Q_ctx_no_remove`＝**按构造等价**：`Init` 在 pool 未启用时返回
     null ⇒ `uc.ThreadPool == null` ⇒ `Free()` 根本不会被调；ctx 注销只对"free 之后再访问"
     有意义，而那是 C 的 use-after-free。
     (e) `Q_create_index_max`（去掉 `index == INT32_MAX` 的返回 NULL）＝**不可达**：需要 2^31 个任务。
     (f) `Q_create_memset`（去掉"只在第一圈清零"）＝**按构造等价**：端口的槽是按需创建的，
     "没见过的槽"本身就等价于 C 的 `index < num_tasks`。
     (g) `Q_batch_waitall`（去掉收尾的 `wait_all`）＝**被循环自身的 wait 掩盖**：循环要跑到
     GROUPS 个连续空批才停，那时每个 group 的 `wait_index` 已等于 `max_index`。真实并发下才可见。
     (h) `Q_deflate_err_text`（改 "Cancelled" 文本）＝**不可达**：`ufbx_inflate()` 只在进度回调
     cancel 时返回 -28，而 deflate 任务跑的是 `progress_cb.fn == NULL`（ufbx.c:8918-8927）。
     (i) `Q_ascii_inline_fallback`（改内联回退的错误文本）＝**不可达**：只有"环形队列拒绝创建任务"
     才走到，而差分里没有那种变体（见第 2 条）。
6. **验收（本轮实测）**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；
   `PoolCheck` **records 1688 / input 336 / mismatches 0 — ALL MATCH**；
   `tools/_scratch/battery.sh` 与批 L 基线逐项一致：goldens **2179/2179（0 load-errors）**、
   mathvec 72000、util 7003、s4a 1086、inflate 4613、dom/graph 691、load 0 分歧、s2geom 0、
   s3scene 13546、hash 79、animcurve 8000、**ascii 1737（修了 csproj 后）**、numeric 280169、
   s4b 12091 行 0 分歧、s4c 332068、s3bc 1477、bake 50261/0、createanim 13676/0、
   stream 11395/0、**streamcheck 2243020 / 3 failed（既存，未动）**、新增 `run pool`。
   汇总存 `tools/_scratch/battery_after_M.txt`。
7. **所有权（本轮登记）**：`src/Ufbx/Parse/ThreadPool.cs`、`src/Ufbx/Parse/Objects.cs` 的
   `ReadObjectsThreaded`、`src/Ufbx/Parse/Root.cs` 的 `ParseToplevelChildOwned`、
   `src/Ufbx/Parse/DomNode.cs` 的 `UfbxiDeflateTask`/`DeflateTask*`、`src/Ufbx/Parse/AsciiDomNode.cs`
   的延迟数组分支与 `AsciiArrayTaskFn`、`src/Ufbx/Parse/Load.cs` 的 `ThreadPoolInit`/`FreeTemp` 段、
   `src/Ufbx/Api/UfbxApi.cs` 的线程池三件套、`tools/pool_oracle.{c,exe,txt}`、`tools/pool_corpus.txt`、
   `tools/pool_port.txt`、`tools/PoolCheck/`、`tools/_mut_pool.sh`、`tools/_mut_pool_cases.txt`、
   `tools/_mut_pool_sweep_M.txt`、`tools/_scratch/{ThreadPool,Objects,DomNode,AsciiDomNode,Root,Load,
   UfbxiContext,UfbxApi}.cs.bak`、`tools/_scratch/{battery.sh,battery_after_M.txt}`、
   `tools/AsciiCheck/AsciiCheck.csproj`，归线程池线。
8. **公开 ABI 账本（更新上一节第 9 条）**：**114/114**。本仓"未移植的公开 ABI"归零。
   **下一波（都是覆盖深度，不是门面）**：
   1. **skinning 求值体**：`ufbx_skin_*` / `ufbx_blend_*` 与 `ufbxi_evaluate_skinning`
      （`Parse/EvaluateScene.cs` 在 `Opts.EvaluateSkinning` 为真时抛 `UfbxiReaderNotPortedException`，
      所以批 K 的 `V` 维与所有 golden 对拍都只能走 `evaluate_skinning = false` 子图）。**这是最大的
      一块欠账**：补上之后 goldens 才能从"同一子图"升级成"全子图"。
   2. 批 J 遗留：`pivot_handling` 载入选项维度、"动画区间严格内嵌于 bake 区间"的语料、
      animated scale-helper 子节点、180° 翻转的连续四元数。
   3. 批 M 遗留（已按"按构造等价/不可复现"登记，不必补语料）：`run_fn` 不执行或只执行一部分
      （未完成任务的目标缓冲区＝arena 未初始化字节，`barbarian` 文件实测两次哈希不同）、
      `num_tasks` 大到分配失败（C 报 OUT_OF_MEMORY，#4）、真实并发交错顺序、
      `tmp_buf` 的内存门限（ufbx.c:15192）、非法/已释放的 ctx（C 是野写 / use-after-free）。

## 更新（skinning 求值线会话 11，2026-10-04）——批 N：`ufbxi_evaluate_skinning` 的 evaluate 侧落地，定稿 PORTING_NOTES 规则 15

> 本节**修正上一节第 8 条对"skinning 求值体"欠账的描述**：`ufbxi_evaluate_skinning()` 本体
> （ufbx.c:25063-25177）**早已移植完毕**（`src/Ufbx/Parse/SceneOpts.cs`），且**载入侧调用点
> (25371) 早就被 goldens 覆盖** —— `test/hash_scene.c:103` 给 `ufbx_load_opts` 设了
> `evaluate_skinning = true`，而 `test/hash_scene.h:620-622` 哈希 `skinned_is_local` /
> `skinned_position` / `skinned_normal`，所以 2179/2179 的 goldens 已经逐位证明了载入侧。
> **真正缺的只有 evaluate 侧那一处**：`ufbxi_evaluate_imp` 的 `if (ec->opts.evaluate_skinning)`
> （ufbx.c:26413-26419）在端口是 `UfbxiToplevel.NotPorted(...)`（`Parse/EvaluateScene.cs:383-385`），
> 而 golden 生成器在 `test/hash_scene.c:136` 传的是 **NULL evaluate opts**（`eval_opts` 那个
> 局部变量填了却从未被传），所以任何 golden 都到不了这里。

1. **交付（一处接线 + 两套测具，不是重写）**：
   - `src/Ufbx/Parse/EvaluateScene.cs`：把 `NotPorted("ufbxi_evaluate_skinning (S4c)")` 换成 C:26413-26419
     的本体 —— `cache_opts.open_file_cb = ec->opts.open_file_cb`、
     `ufbxi_evaluate_skinning(&ec->scene, &ec->error, ..., ec->time,
     ec->opts.load_external_files && ec->opts.evaluate_caches, &cache_opts)`。
     `ufbxi_check_err()` 那条尾巴是 arena OOM（#4），端口的 `EvaluateSkinning` 返回 void，
     故 `ufbxi_evaluate_scene()` 的 "Failed to evaluate" 分支保持不可达。
   - `tools/skin_oracle.c`（+`skin_oracle.exe/.txt`）、`tools/skin_corpus.txt`（12 条）、
     `tools/SkinCheck/{SkinCheck.csproj,Program.cs}`、`tools/_mut_skin.sh`、
     `tools/_mut_skin_cases.txt`、`tools/_mut_skin_sweep_N.txt`，以及
     `tools/_scratch/battery.sh` 新增 `run skin` 一行。
2. **差分规模与构造**：**17 变体 × 12 文件 = 221 次 (load[, evaluate])**，共
   **1453 条输出记录 + 221 条 `I` 输入记录，mismatches 0 — ALL MATCH**。
   - 语料用 `tools/_scratch/_skin_probe.exe`（`-list` 跑遍 `data/*.fbx`）**按证据挑的**，不是按文件名猜：
     `tools/_scratch/_skin_probe.txt` 记录每个文件的 `<meshes> <skin> <blend> <cache> <maxVerts>
     <maxIndices> <maxSkinPerMesh> <zeroVertDeformer>`。覆盖 skin-only / blend-only / skin+blend /
     cache deformer（带外部文件）、线性与双四元数、半蒙皮、instanced mesh（ufbx.c:25128 的
     `instances.data[0]->geometry_to_world` 兜底）、ascii 与 binary、8~1954 顶点。
   - 变体维度：`frame ∈ {-1(不 evaluate), 0, 1, 4, 9, 25}`、`load.evaluate_skinning`、
     `eval.evaluate_skinning`、`eval.evaluate_caches`、`eval.load_external_files`、
     `load.evaluate_caches`、`load.load_external_files`、`eval.evaluate_flags`。
     `frame = -1` 的两条是**载入侧对照**（等价于 golden 形状），其余 15 条走 evaluate。
   - **分辨力已实测**：`maya_game_sausage_7500_binary` 上 `eskin=0` 与 `eskin=1` 的 `V` 哈希不同
     （`bf5e56ca…` vs `758ec1b3…`），`K` 也不同（`skinned_is_local` 1→0、法线数 88→56、
     `generated_normals` 0→1）。⇒ 这 221 次不是空跑。
3. **记录文法（自描述，Port 侧不镜像变体表）**：
   `I`（10 个输入位，含 frame 与七个选项位）、`A`（stage 0=load / 1=evaluate 的返回值 +
   `ufbx_error` 逐字节，含 `info_length`/`info`）、`K`（**每个** mesh 的 `skinned_is_local` /
   `skinned_position` 的 count+FNV / `skinned_normal` 的 exists+unique+count+FNV+indices+FNV+
   `value_reals` / `generated_normals` / `num_vertices`）、`V`（`ufbxt_hash_scene()` 端到端）。
   `K` 对**所有** mesh 都发，所以"哪些 mesh 算被蒙皮"的谓词一旦写错会先变成**记录条数**不同，
   而不只是哈希不同。
   `K` 的 FNV 走**原始字节**（C 侧 `hh_bytes`；端口侧 `BitConverter.DoubleToInt64Bits` 拆 8 字节），
   因为 `ufbx_real` 在 golden 配置下是 `double`，必须让 -0.0 与 NaN 可区分。
4. **变异 33 例：26 咬 / 7 不咬 / 0 ERROR / 0 RESTORE FAILED**
   （`tools/_mut_skin_sweep_N.txt`；锚点 `.bak` 全部 `cmp` 字节一致）。逐例理由：
   - **咬住的 26 例**里最关键的 5 条：
     `C_eval_no_skin`（把 evaluate 侧整块关掉）**195 条** ← **这一条证明 evaluate 侧调用点真的被测到了**；
     `C_load_no_skin`（把载入侧整块关掉）**72 条** ← 证明载入侧也在这套差分里；
     `C_eval_time_zero`（`ec->time` 改 0.0）6 条、`C_eval_caches_off/off/on`（`load_external_files &&
     evaluate_caches` 两向）6/29 条 ← 证明 cache 路径在 evaluate 时可观测；
     `C_skip_skin`（360）/ `C_skip_blend`（75）/ `C_totalweight_zero`（360）/ `C_nonorm`（270）/
     `C_dq_replace`（120）/ `C_dq_blend_full`（60）/ `C_lerp_nodq`（60）/ `C_dqdot_skip`（4）/
     `C_firstq0_identity`（4）← 证明线性与 DQ 两条分支、权重归一化、兜底矩阵都在视野内。
   - **按设计不咬的 5 例（等价于构造 / 不可达）**：
     (a) `Q_eval_error_fresh`：传给 `EvaluateSkinning` 的 `ufbx_error` 对象换成新实例 —— 该函数
     只在自己失败时写 error，而两处调用点的失败尾都是 arena OOM（#4），不可达。
     (b) `Q_topo_bigger`：`topo` 多分一个元素 —— C 的 `ufbxi_push(buf_tmp, ufbx_topo_edge,
     max_skinned_indices)` 之后只按 `num_indices` 用，容量不可观测。
     (c) `Q_skin_sentinel`：`result_pos[0]`（C 用 `ufbxi_push(num_vertices+1); result_pos++` 造出的
     哨兵槽）写脏 —— 槽位永不回读。
     (d) `Q_blend_zero_guard`：去掉 `weight == 0.0` 的提前返回 —— 多出来的是 `offset * 0 * w`，
     对任何有限值都是 0，语料里没有 inf/nan 的 blend 偏移。
     (e) `Q_blend_outer_weight`：`weight * key->effective_weight` 丢掉外层 `weight` ——
     `ufbxi_evaluate_skinning` 是唯一带权重调用 `ufbx_add_blend_vertex_offsets` 的地方，且恒传
     **1.0**（ufbx.c:25126），乘不乘一样。
   - **预测为"咬"、实测不咬的 2 例（语料缺口，已用探针定性，不是端口错）**：
     (f) `C_skin_last_not_first`（`skin_deformers[0]` 改成 `[count-1]`）0 条：
      `tools/_scratch/_skin_probe.txt` 显示 **690 个 `data/*.fbx` 里 `maxSkinPerMesh` 全局最大 = 1**，
      即**没有任何 mesh 带 2 个以上 skin deformer**。C 自己在这一行上挂着
      `// TODO: What should we do about multiple skins??`（ufbx.c:25127），行为本就未定义。
      ⇒ 端口照抄 `[0]` 是对的，只是**现有语料造不出这个区分**（要手搓 FBX）。
     (g) `C_skin_zeroverts`（去掉 `if (mesh->num_vertices == 0) continue;`，ufbx.c:25079）0 条：
      探针显示 **`zeroVertDeformer` 全局为 0** —— 没有任何"带 deformer 且 0 顶点"的 mesh。
   - ⚠️ 过程事故（已完全修复）：首轮 sweep 在 `C_skin_normal_smooth` 的 `dotnet run` 中途被 10 分钟
     超时 SIGTERM，脚本**在替换后、还原前**被杀，`SceneOpts.cs` 留在被改状态；下一轮因此报
     `ERROR: 0 occurrences` 且 `Q_skin_sentinel` 拿到污染结果（30 条）。已 `cp` + `cmp` 从
     `tools/_scratch/SceneOpts.cs.bak` 还原并复跑这两例，最终值：`C_skin_normal_smooth` **30 条（咬）**、
     `Q_skin_sentinel` **0 条（不咬）**。**教训：sweep 必须整批后台跑，不要用会被 SIGTERM 的前台循环。**
5. **验收（本轮实测）**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；
   `SkinCheck` **records 1453 / input 221 / mismatches 0 — ALL MATCH**；
   oracle 两次运行 `diff` 一致（DETERMINISTIC）；
   `tools/_scratch/battery.sh` 与批 M 基线逐项一致：goldens **2179/2179（0 load-errors）**、
   mathvec 72000、util 7003、s4a 1086、inflate 4613、dom/graph 691、load 0 分歧、s2geom 0、
   s3scene 13546、hash 79、animcurve 8000、ascii 1737、numeric 280169、s4b 12091 行 0 分歧、
   s4c 332068、s3bc 1477、bake 50261/0、createanim 13676/0、stream 11395/0、pool 1688/0、
   **streamcheck 2243020 / 3 failed（既存，未动）**、新增 `run skin`。
   汇总存 `tools/_scratch/battery_after_N.txt`。
6. **所有权（本轮登记）**：`src/Ufbx/Parse/EvaluateScene.cs` 的 C:26413-26419 段、
   `tools/skin_oracle.{c,exe,txt}`、`tools/skin_corpus.txt`、`tools/SkinCheck/`、
   `tools/_mut_skin.sh`、`tools/_mut_skin_cases.txt`、`tools/_mut_skin_sweep_N.txt`、
   `tools/_scratch/{_skin_probe.c,_skin_probe.exe,_skin_probe.txt,_allfbx.txt}`、
   `tools/_scratch/{EvaluateScene,SceneOpts,Load}.cs.bak`、
   `tools/_scratch/{battery.sh,battery_after_N.txt}`，归 skinning 求值线。
7. **公开 ABI 账本**：仍是 **114/114**（本批不新增门面，是覆盖深度）。
   **下一波（仍是覆盖深度）**：
   1. 批 J 遗留：`pivot_handling` 载入选项维度、"动画区间严格内嵌于 bake 区间"的语料、
      animated scale-helper 子节点、180° 翻转的连续四元数。
   2. 批 N 遗留（语料缺口，见第 4 条 f/g）：**多 skin deformer 的 mesh**（C 自己的 TODO）、
      **带 deformer 且 0 顶点的 mesh** —— 两者都需要手搓 FBX，`data/` 里不存在。
   3. 批 M 遗留（按"按构造等价/不可复现"登记，不必补语料）：`run_fn` 部分执行（未完成任务＝
      arena 未初始化字节）、`num_tasks` 大到分配失败、真实并发交错、`tmp_buf` 内存门限、
      非法或已释放的 ctx。
   4. 批 L 遗留：过量供给的 `read`、`ufbx_open_memory` 二次 `Close`（C 是 double free）、
      `stream == NULL`、分配失败、非 `_WIN32` 的 `fopen` 分支、`UFBX_NO_STDIO`。

## 更新（pivot 维度线会话 12，2026-10-04）——批 O：`pivot_handling` 载入选项维度落地差分，定稿 PORTING_NOTES 规则 16

> 本节**修正上一节第 7 条第 1 项对缺口的描述**：`pivot_handling` 的**本体早已移植完毕**
> （`src/Ufbx/Parse/SceneBuild.cs:464-583`，对照 ufbx.c:18329-18457 逐行核对无差异），
> 本批**没有改一行端口代码**，只建了证明。另有一点必须先说清：**goldens 一次都不会进入
> pivot 块** —— `test/hash_scene.c` 全文没有 `pivot` / `geometry_transform_handling` /
> `inherit_mode` 字样（load opts 就是 `{0}`），而 `UFBX_PIVOT_HANDLING_RETAIN` 就是 **0**
> （ufbx.h:3717），**不存在 `UFBX_PIVOT_HANDLING_NONE`**（批 O 交接文档曾误写，已更正）。
> 且 `test/hash_scene.h` 里没有 `adjust_pre_translation`，所以端到端哈希**根本看不见**该块的
> 主要产出 ⇒ 光靠 `V` 记录是证不出任何东西的，本批的定位记录 `P` 不是锦上添花而是必需。

1. **交付（零行端口改动 + 三套测具）**：
   - `tools/pivot_oracle.c`（+`pivot_oracle.exe/.txt`）、`tools/pivot_corpus.txt`（17 条）、
     `tools/PivotCheck/{PivotCheck.csproj,Program.cs}`、`tools/_mut_pivot.sh`、
     `tools/_mut_pivot_cases.txt`、`tools/_mut_pivot_sweep_O.txt`，
     `tools/_scratch/{_pivot_probe.c,_pivot_probe.exe,_pivot_probe.txt}`，
     以及 `tools/_scratch/battery.sh` 新增 `run pivot` 一行。
2. **差分规模与构造**：**24 变体 × 17 文件 = 408 次加载**，C 侧 **4750 条记录**（`I` 408 /
   `A` 408 / `N` 408 / `P` 2134 / `G` 984 / `V` 408），
   **PivotCheck：records 4342 / input 408 / mismatches 0 — ALL MATCH**。
   - 变体维度：`pivot_handling ∈ {RETAIN, ADJUST_TO_PIVOT, ADJUST_TO_ROTATION_PIVOT}` ×
     `pivot_handling_retain_empties ∈ {0,1}` × `geometry_transform_handling ∈
     {PRESERVE, HELPER_NODES, MODIFY_GEOMETRY, MODIFY_GEOMETRY_NO_FALLBACK}`（全交叉 24 条）。
     叠 `gth` 不是顺手：`MODIFY_GEOMETRY_NO_FALLBACK` 是唯一能让 `instance_counts > 1`
     否决 `can_modify_geometry_transform` 的路，另两条否决路是 EMPTY+retain 与 skin deformer。
   - 语料用 `_pivot_probe.exe`（跑遍 `data/*.fbx`）**按证据挑的**，不是按文件名猜：
     `_pivot_probe.txt` 每文件报 `<nodes> <rpNz> <spNz> <soNz> <rotNeScl> <emptyPivot>
     <skinnedPivot> <instanced> <geoTrans> <minNzPivot> <nearEqPivots>`。690 个文件里只有
     **37 个带非零 pivot**，从中取 16 个 + 1 个负对照（`blender_279_default_7400_binary`，
     完全没有 pivot）。各分支的担纲文件：
     `maya_zero_scale_pivot_*`（Lcl Scaling 带 0 分量 → `ufbxi_pivot_div` 的 epsilon 分支）、
     `maya_instanced_pivots`（instanced）、`maya_child_pivots`（子节点累加）、
     `maya_null_pivots`（EMPTY 带 pivot → `retain_empties` 唯一可观测处）、
     `maya_slime_7500_binary` / `maya_skinned_pivot`（skin deformer 否决）、
     `maya_split_pivot` / `maya_pivot_offset`（非零 ScalingOffset）、`motionbuilder_pivot`
     （既有 geometry transform）、`maya_equal_pivot*`（rp == sp，`can_modify_pivot` 保持真）。
   - **分辨力已实测**：`maya_null_pivots_7700_ascii` 上 5 个变体的 `V` 分别是
     RETAIN `55764fa750ca291d` / ATP `88d0195683d72a0a` / ATP+retain `88d0195683d72a0a`
     （⇒ `retain_empties` 在 ATP 下惰性，符合 C：只读在 ATRP 分支里）/
     ATRP `94b69c496f29653b` / **ATRP+retain `55764fa750ca291d`（与 RETAIN 完全相同）**。
     最后这条不是 bug：`retain_empties=1` 让 EMPTY 走 `can_modify_geometry_transform=false`，
     最终门槛 `can_modify_pivot && (can_modify_geometry_transform || skip_geometry_transform)`
     两者皆假 ⇒ 整块跳过。已写进规则 16。
   - 负对照有效：`blender_279_default_7400_binary` 在 RETAIN/ATP/ATRP 下 `V` 三者相同
     ⇒ 谓词不会"乱咬"没 pivot 的文件。
3. **记录文法（自描述，Port 侧不镜像变体表）**：`I`（3 个输入位：ph / re / gth）、
   `A`（加载结果 + `ufbx_error` 逐字节）、`N`（`nodes.count` + `metadata.pivot_handling`）、
   `P`（**每个** node 的 name hex / parent typed_id / node_depth / `has_adjust_transform` /
   **`adjust_pre_translation`** / `has_geometry_transform` + geometry_transform 的
   translation+rotation+scale / **回读的 RotationPivot、ScalingPivot、ScalingOffset、
   GeometricTranslation** / `is_scale_helper` / `is_geometry_transform_helper`）、
   `G`（每 mesh 的 `num_vertices` + 顶点原始字节 FNV，因为 MODIFY_GEOMETRY 会把调整**推进
   几何本身**）、`V`（`ufbxt_hash_scene()`）。
   `P` 对**所有** node 都发，所以"哪些节点被 pivot 块碰过"的谓词写错会先变成**记录条数**不同。
   浮点一律按 **IEEE 位模式**打印（`pzd` / `BitConverter.DoubleToInt64Bits`），-0.0 与 NaN 可分。
4. **变异 40 例：32 咬 / 8 不咬 / 0 ERROR / 0 RESTORE FAILED**
   （`tools/_mut_pivot_sweep_O.txt`；锚点 `.bak` 全部 `cmp` 字节一致）。逐例理由：
   - **咬住的最关键的几条**：`C_gate_and`（门槛 `||` 改 `&&`）**1020 条**、
     `C_sort_off`（对副本排序）**942 条**、`C_block_off`（整块跳过）**680 条**、
     `C_atrp_pred_false` **680 条**、`C_scaled_offset_swap` 500、`C_pivot_nonzero_eps0` 475、
     `C_atrp_geomtr_noadd` 446 — 这些一起证明**整块 + 每个分支都在视野内**。
     `C_required_off`（`required` 门）246 条、`C_skin_guard_off` 204 条、
     `C_nofallback_off` 28 条、`C_empty_check_false` 82 条、`C_retain_invert` 64 条 /
     `C_retain_always_skip` 32 条 / `C_retain_never_skip` 32 条（三向都咬 ⇒ retain_empties
     的两个取值真的都被观测到）、`C_child_no_add` 272 条（子节点 `adjust_pre_translation`
     累加）、`C_meta_ph_retain` 272 条（`metadata.pivot_handling`）。
   - **按设计不咬的 6 例（`Q_*`）**：
     (a) `Q_props_capacity_atp` / (b) `Q_props_capacity_atrp`：`new UfbxProp[numProps+3|4]`
     多分 5 个 —— C 的 `ufbxi_push_zero` 也是多分，只把 `count` 写到 `new_prop_count`，
     端口 `Array.Resize` 到 count ⇒ **分配容量不可观测**。
     (c) `Q_dedup_twice` / (d) `Q_sort_twice`：两个操作都是幂等的。
     (e) `Q_identity_empty_guard` / (f) `Q_identity_err_init`：**恒等变换**，是测具自校验 ——
     证明 harness 不会对着没变的东西乱报分歧。
   - **预测为"咬"、实测不咬的 2 例（语料缺口，已用扩展探针定性，不是端口错）**：
     (g) `C_atp_pred_epsilon`（把 ATP 的 `!IsVec3Zero` 换成 `PivotNonzero`）**0 条**：
     扩展后的 `_pivot_probe.txt` 显示 **690 个文件里最小的非零 pivot 分量 =
     0.0079944331810126099**，比 `ufbxi_pivot_nonzero` 的阈值 0.0009765625 大 8 倍，
     **0 个文件**有落在 `(0, 0.0009765625)` 的 pivot ⇒ 两个谓词在整个语料上判定完全一致。
     端口照抄 C 的两个不同谓词（精确 `== 0.0` vs epsilon）是对的，只是**现有语料造不出区分**。
     (h) `C_eps_zero`（`pivotEpsilon` 改 0.0）**0 条**：**0 个节点**满足
     `0 < |rp-sp|₁ <= 0.001f`，所以 `err > 0.001` 与 `err > 0` 无差别。
     反向对照 `C_eps_huge`（改 1e300）**咬 452 条**，证明 `err > 0.001` 的情形确实存在
     （`rotNeScl` 全局 31），只是**没有落在那个窄区间内**。
     ⇒ 两例都属于"要手搓 FBX 才能补"的语料缺口，与批 N 的 (f)/(g) 同类。
5. **验收（本轮实测）**：`dotnet build ufbx-cs.sln -c Release` **0 错 0 警**；
   `PivotCheck` **records 4342 / input 408 / mismatches 0 — ALL MATCH**；
   oracle 两次运行 `diff` 一致（DETERMINISTIC）；
   `tools/_scratch/battery.sh` 与批 N 基线逐项一致 + 新增 `run pivot`。
   汇总存 `tools/_scratch/battery_after_O.txt`。
6. **所有权（本轮登记）**：`tools/pivot_oracle.{c,exe,txt}`、`tools/pivot_corpus.txt`、
   `tools/PivotCheck/`、`tools/_mut_pivot.sh`、`tools/_mut_pivot_cases.txt`、
   `tools/_mut_pivot_sweep_O.txt`、
   `tools/_scratch/{_pivot_probe.c,_pivot_probe.exe,_pivot_probe.txt}`、
   `tools/_scratch/{SceneBuild,SceneUpdate}.cs.bak`、
   `tools/_scratch/{battery.sh,battery_after_O.txt}`，归 pivot 维度线。
   ⚠️ 本轮**未改任何 `src/` 文件**（见开头），`SceneBuild.cs` / `SceneUpdate.cs` 的 `.bak`
   只为变异对照而备，工作树与备份 `cmp` 一致。
7. **公开 ABI 账本**：仍是 **114/114**（本批不新增门面，是覆盖深度）。
   **下一波（仍是覆盖深度）**：
   1. 批 J 遗留的另外三项（本批只做 `pivot_handling`）："动画区间严格内嵌于 bake 区间"的
      语料、animated scale-helper 子节点（端口 `Parse/SceneBuild.cs:612-639` 建 helper、
      `Parse/Bake.cs:762-775` 消费；**要先查清 goldens 是否已覆盖**——`test/hash_scene.h:458`
      哈希了 `is_scale_helper`，默认 `inherit_mode_handling = PRESERVE` 下也会建 helper，
      所以缺口很可能只在"helper 的 scale 是动画的"这一支）、180° 翻转的连续四元数
      （`ufbx_quat_dot(q, reference) < 0.0f` 在 ufbx.c:31529；**注意 ufbx.c:28570 的
      "180deg" 是耳切三角化的注释，与本项无关，别找错地方**）。
   2. 批 O 遗留（语料缺口，见第 4 条 g/h）：**pivot 分量落在 (0, 0.0009765625) 的 FBX**、
      **`0 < |rp-sp|₁ <= 0.001` 的 FBX** —— 两者都要手搓，`data/` 里不存在。
   3. 批 N 遗留（语料缺口）：多 skin deformer 的 mesh、带 deformer 且 0 顶点的 mesh。
   4. 批 M / 批 L 遗留：按"按构造等价/不可复现"登记，不必补语料。

---

## 批 P（2026-10-04）：scale helper 子节点 —— 【已完成】

**任务**：`#23 批 P：补 animated scale-helper 子节点的差分覆盖`（批 J 遗留三项之一）。

### 1. 开工前的调查（本批最重要的一步，结论推翻了交接文档的预判）

`tools/_scratch/_sh_probe.c`（只用公开 API，链接 ufbx.c 作独立 TU）跑遍全部 690 个
`data/*.fbx`，对 5 个 `inherit_mode_handling` 取值各扫一遍，结果：

| inherit_mode_handling | 建 helper 的文件 | helper 节点 | 其中 scale 带动画 | bake 侧 `!constant_scale` 命中 |
|---|---|---|---|---|
| **PRESERVE（默认）** | **0** | **0** | 0 | 0 |
| HELPER_NODES | 28 | 255 | 139 | 5 个文件 |
| COMPENSATE | 20 | 46 | 39 | 5 个文件 |
| COMPENSATE_NO_FALLBACK | 0 | 0 | 0 | 0 |
| IGNORE | 0 | 0 | 0 | 0 |

⇒ **与批 N、批 O 相反，这一波是真缺口**：`test/hash_scene.c` 传零值 load opts，
`UFBX_INHERIT_MODE_HANDLING_PRESERVE` 又正好是 0，所以 **2179 个 golden 一个 scale helper
都没建过**。端口的 helper 代码此前完全没有差分证明。

### 2. 端口代码：一行未改

`src/Ufbx/Parse/SceneBuild.cs:605-668`（建 helper 主循环 + 递归子节点）、
`src/Ufbx/Parse/ReadElement.cs:274-320`（`SetupScaleHelper`）、
`src/Ufbx/Parse/Evaluate.cs:941-972`（helper 的 scale 折进子节点 translation）、
`src/Ufbx/Parse/Bake.cs:757-777 / 825-845 / 903-940`（`scale_helper_t` / `_s`）——
逐行对照 C（ufbx.c:12556-12602 / 18478-18539 / 22969-22979 / 27280-27352 / 27421-27458）
**无差异**。本批是补证明，不是重写。

### 3. 交付

- `tools/sh_oracle.c` + `sh_oracle.exe` + `sh_oracle.txt`：`#include "ufbx.c"` + `test/hash_scene.h`，
  14 个变体（`inherit_mode_handling` 0-4 × `geometry_transform_handling` 0/1/2/3 × bake 开关）。
- `tools/sh_corpus.txt`：20 条，**按探针证据挑的**（不是按文件名猜）——18 个建 helper 的文件
  （按 helper 数 / 动画数 / bakeNC 排序）+ 2 个零 helper 的反向对照。
- `tools/ShCheck/`（`ShCheck.csproj` + `Program.cs`）。
- **`ShCheck: records 44716 / input 280 / mismatches 0 — ALL MATCH`**（14 变体 × 20 文件 = 280 次加载）。
- 记录文法：`I` 输入 / `A` 加载结果 / `N` 全场景 helper 计数 / `S` 每节点定位 / `X` 三个采样时刻的
  `ufbx_evaluate_transform()` / `V` `ufbxt_hash_scene()` / `B` 每个 baked 节点。
  - ⚠️ `X` 记录是必需的：`ufbxt_hash_scene()` 只哈希静态变换，看不见 ufbx.c:22969-22979
    （父节点的 helper scale 折进子节点 translation）在动画下的行为。
  - 分辨力实测：`maya_game_sausage_7500_binary_deform` 上 PRESERVE 6 节点 / 0 helper，
    HELPER_NODES 9 节点 / 3 helper，场景哈希 `e3b0a1e1cad8eb77` → `e633aa5ef4ff51ca`；
    bake 侧同一节点 scale 键数 21 → 2（scale 被搬进 helper）。
- 已接入总闸：`battery.sh` 新增 `run sh`。

### 4. 变异对照：35 例，26 咬 / 9 不咬 / 0 ERROR / 0 RESTORE FAILED

整批后台跑（11m29s）。脚本 `tools/_mut_sh.sh`（python 字节级替换 + `cmp` 还原），
病例 `tools/_mut_sh_cases.txt`，结果 `tools/_mut_sh_sweep_P.txt`。
锚点 `tools/_scratch/{SceneBuild,Bake,Evaluate,ReadElement}.cs.bak`，工作树与备份 `cmp` 一致。

**咬得最狠的**：`C_sh_unscaled_false` 24046、`C_sh_gate_true` 24042、`C_shr_flag` 21257、
`C_sh_setup_skip` 20635、`C_shr_connect` 18962、`C_sh_nofallback` 6411。

**⚠️ 我自己预判错了三条（已按实测更正，写进规则 17）**：交接文档和探针都判
"递归 helper 路径（ufbx.c:18500-18540）是语料缺口"，**实测不是**：
`Q_sh_recursive_setup` 688、`Q_sh_recursive_gate` 688、`Q_sh_recursive_comp` 64 全咬，
命中文件是 `motionbuilder_sausage_rrss_7700_binary.fbx`。
原因：我的探针把"递归"定义成"helper 的父节点也是 helper"（全语料 0 个），
而 C 的 `has_recursive_scale_helper` 指的是"通过递归遍历给子节点建的 helper"——两者不是一回事。
**教训：探针的判据要和被测代码的条件逐字对齐，否则会得出反向的结论。**

**9 例不咬的分类**（3 例恒等变换是测具自校验，全部 0 分歧 ✓）：

| 病例 | 分类 | 理由 |
|---|---|---|
| `Q_identity_reflect` / `Q_identity_bake` / `Q_identity_shr` | 测具自校验 | 字节恒等重写，必须 0 分歧 |
| `Q_sh_recursive_gate2` | 语料缺口 | 去掉 `child.OriginalInheritMode != Normal` 后无变化 ⇒ 递归遍历在本语料里没遇到 NORMAL 子节点 |
| `C_sh_gate_compeps` | 语料缺口 | `\|scale.x\| <= compensate_epsilon(0.01)` 这一支单独不成立；反向对照 `C_sh_comp_eps`（eps→0.5）咬 5320，说明判定在视野内，只是边界没覆盖 |
| `C_sh_constscale_anim` | 按构造等价（本语料） | `!has_constant_scale` 这一支被 `dx+dy+dz >= scale_epsilon` 掩蔽：scale 动画到能失配时，scale 本身也早已偏离 1 |
| `C_bake_t_gate` | 语料缺口 | 去掉 `!node.IsScaleHelper` 后无变化 ⇒ 没有"自身是 helper、父节点也有 helper"的 bake 目标 |
| `C_bake_t_resample` | 语料缺口（分支内被掩蔽） | 反转 `if (!scaleHelperT.ConstantScale)` 无变化；但相邻的 `C_bake_t_pushtimes` 咬 60，证明 `scale_helper_t` 那条路径整体在视野内，只是 `resample_translation` 这个标志在本语料里不改变最终时间集合 |
| `C_bake_t_const` | 不可达（公开 API） | `else` 分支（helper 不在 `baked_nodes` 里）需要 helper 被排除出 bake 集合，公开 `ufbx_bake_opts` 没有这个开关 |

### 5. 遗留

1. 上表 5 个语料缺口若要补，都得手搓 FBX（`data/` 里不存在对应形态）。
2. 批 J 遗留还剩两项（动画区间严格内嵌于 bake 区间、180° 翻转的连续四元数）→ 批 Q。
3. 批 O 遗留（语料缺口，需手搓 FBX）：pivot 分量落在 `(0, 0.0009765625)`、
   `0 < |rp-sp|₁ <= 0.001`。
4. 批 N 遗留（语料缺口）：带 2 个以上 skin deformer 的 mesh、带 deformer 且 0 顶点的 mesh。
5. 批 M / 批 L 遗留：按"按构造等价/不可复现"登记，不必补语料。

### 6. 所有权

`tools/sh_oracle.{c,exe,txt}`、`tools/sh_corpus.txt`、`tools/ShCheck/`、
`tools/_mut_sh.{sh,_cases.txt,_sweep_P.txt}`、`tools/_scratch/_sh_probe.{c,exe,txt}`、
`tools/_scratch/{SceneBuild,Bake,Evaluate,ReadElement}.cs.bak`，归 scale helper 维度线。
⚠️ 本轮**未改任何 `src/` 文件**（见第 2 节），四份 `.bak` 只为变异对照而备。

### 7. 公开 ABI 账本

仍是 **114/114**（本批不新增门面，是覆盖深度）。

---

## 批 Q（2026-10-04）：bake 侧批 J 遗留两项 —— 【已完成】

**任务**：验证「动画区间严格内嵌于 bake 区间」与「180° 翻转的连续四元数」两项到底有没有覆盖。

### 1. 前置：修好一个会让结论全部失效的工具缺陷

`tools/_mut_bake.sh` 用的是 **perl，而本机已无 perl**（`which perl` → not found）。
批 J/K 的 sweep 是在 perl 还在时跑的，历史数字仍然有效；但**任何新跑都会静默无效**。
已把 `_mut_bake.sh` 改成 python 字节级替换（与 `_mut_sh.sh` 同一套），并保留说明。
⇒ 批 J2 的 `M_antipodal | mismatches 0` 因此**重新测过**。

### 2. 两项的结论

| 项 | 结论 | 证据 |
|---|---|---|
| **"动画区间严格内嵌于 bake 区间"**（即 `ufbxi_bake_times` 的重采样/裁剪，ufbx.c:26773-26826） | **早已覆盖，无缺口** | 6 个变异全咬：`Q_start_noceil` 5015、`Q_flat_noskip` 2691、`Q_mindur_noskip` 1489、`Q_factor3` 1423、`Q_pad_zero` 851、`Q_stop_nopad` 831 |
| **180° 翻转的连续四元数**（`ufbx_quat_fix_antipodal`，ufbx.c:31527；调用点 27140-27143） | **原本没覆盖 → 已补语料闭合** | 见下 |

### 3. antipodal 这一支的定量过程（两次判据都走过弯路）

- **第一次判据（错的）**：`_q_probe.c` 用「baked 旋转关键帧与同刻 `ufbx_evaluate_transform()`
  的四元数点积 < 0」作判据，得出"30 个文件 / 75 节点 / 668 帧被翻转"。
- **第二次判据（对的）**：把 ufbx.c 复制到私有副本 `tools/_scratch/ufbx_nofix.c`，把
  `if (ufbx_quat_dot(q, reference) < 0.0f)` 改成 `if (false)`，**同一份探针分别链接原始与打过
  补丁的 ufbx.c**，逐帧打印 IEEE 位模式再 diff。
  ⇒ **默认 bake 选项下，全部 690 个文件、7710 个旋转关键帧，被翻转的帧数是 0**。
  第一次的结论是判据造出来的假阳性。
- **扩成 12 组 bake 选项重测**：翻转只出现在 **2 个文件**
  （`maya_dq_weights_7500_ascii/binary`）× **5 组稀疏采样选项**
  （`resample_rate=1.0`、`max_keyframe_segments=1`、`resample_rate=1.0 + no_resample_rotation`、
  `resample_rate=0.5 + minimum_sample_rate=1000`、`trim_start_time + resample_rate=1.0`），
  每文件 5 帧。默认 30 Hz 重采样下相邻帧夹角远小于 90°，点积恒为正。

**修复**：把这两个文件加进 `tools/bake_corpus.txt`（61 → 63 条），重生成 `bake_oracle.txt`
（79541 → 83323 行）。`BakeCheck` 在扩展语料上 **records 53083 / input(T) 30240 / mismatches 0**。
重测 `Q_antipodal_off`：**0 → 8 条**（`Q_antipodal_inv` 24995、`Q_identity_pad` 0 ✓）。

### 4. 交付

`tools/_mut_bake.sh`（perl→python）、`tools/_mut_q_cases.txt`、`tools/_mut_q_sweep_Q.txt`、
`tools/_scratch/_q_probe.{c,exe,txt}`、`tools/_scratch/{ufbx_nofix.c,_flip_probe.c,_flip_probe.exe,
_flip_probe_nofix.exe,_flip_pristine.txt,_flip_nofix.txt}`。
备份 `tools/_scratch/{bake_corpus.txt.bak,bake_oracle.txt.bak}`。

### 5. 公开 ABI 账本

仍是 **114/114**。

---

## 批 R（2026-10-04）：手搓合成 FBX 补 4 个语料缺口 —— 【已完成】

**任务**：把批 O（2 个）与批 N（2 个）遗留的"语料缺口"补掉。全部 4 个都闭合了。

### 1. 产物：2 个合成 FBX（放在 `tools/synth/`，因为参考树只读）

| 文件 | 覆盖的缺口 | 关键取值 |
|---|---|---|
| `synth_pivot_epsilon_7700_ascii.fbx` | 批 O 两个 | `RotationPivot = (0.0005, 0, 0)`、`ScalingPivot = (0,0,0)`、`ScalingOffset = (0,0,0)` |
| `synth_skin_shapes_7700_ascii.fbx` | 批 N 两个 | 一个 mesh 挂 **2 个** `Deformer::Skin`（两根骨骼位置不同）+ 一个 **0 顶点** mesh 挂 1 个 Skin |

⚠️ **语料里用的是绝对路径**：`C:/Workspace/_analyze_ufbx` 是只读共享参考树，合成文件不能
放进它的 `data/`，所以两个语料文件末尾各有一条绝对路径（已在注释里写明原因）。

⚠️ **踩到的坑**：`Deformer::` 必须连到 **Geometry** 而不是 Model —— `C: "OO", <Skin>, <Geometry>`
（照 `maya_dual_quaternion_scale_7500_ascii.fbx` 的写法）。一开始连到 Model，
`mesh->skin_deformers.count` 恒为 0，探针显示 `maxSkinPerMesh = 0`。
另外两个 Skin 的骨骼**必须在不同位置**，否则 `data[0]` 与 `data[1]` 产生完全相同的结果，
"multiple skins" 那个 site 依旧不可观测。

### 2. 闭合效果（全部"0 分歧 → 咬"）

| 变异 | 原 | 现 | 归属 |
|---|---|---|---|
| `C_atp_pred_epsilon`（把 `is_vec3_zero` 换成 `pivot_nonzero`） | 0 | **20** | 批 O：rp ∈ (0, 0.0009765625) |
| `C_eps_zero`（`pivot_epsilon → 0.0`） | 0 | **20** | 批 O：0 < \|rp-sp\|₁ ≤ 0.001 |
| `C_skin_last_not_first`（`data[0]` → `data[last]`） | 0 | **30** | 批 N：多 skin deformer |
| `C_skin_zeroverts`（去掉 `num_vertices == 0` 守卫） | 0 | **30** | 批 N：0 顶点 + deformer |

### 3. 扩展后各测具的状态（全部 0 分歧）

- `PivotCheck`: records **4488**（+146） input 432，mismatches 0
- `SkinCheck`: records **1536**（+83） input 238，mismatches 0
- `BakeCheck`: records **53083**（+2822） input(T) 30240，mismatches 0

### 4. 交付

`tools/synth/*.fbx`（2 个）、`tools/{pivot,skin}_corpus.txt`（各加 1 条）、
`tools/{pivot,skin,sh}_oracle.txt` 重生成。
备份 `tools/_scratch/{pivot_corpus.txt.bak, pivot_oracle.txt.bak2, skin_corpus.txt.bak,
skin_oracle.txt.bak2}`。

### 5. 公开 ABI 账本

仍是 **114/114**。至此，所有已知的"语料缺口"型盲区都已闭合；`COORDINATION.md` 里剩下的
只有按"按构造等价 / 不可复现"登记、明确不必补的项（批 M / 批 L 遗留）。
