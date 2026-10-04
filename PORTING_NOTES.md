# ufbx → C# 移植约定（所有代码必须遵守）

源：ufbx v0.23.1（`C:\Workspace\_analyze_ufbx`，tag v0.23.1）
目标：纯 C# 移植，无原生依赖，netstandard2.1，C# 9.0。位精确（bit-exact）是硬要求：golden hash 来自 C 版 `test/hash_scene.exe`（见 tools/golden_hashes.txt）。

## 命名映射

| C | C# |
|---|---|
| `typedef struct ufbx_foo` | `class UfbxFoo`（或 struct，见下） |
| `typedef enum ufbx_bar` | `enum UfbxBar : int`（值名 PascalCase，前缀 UFBX_BAR_/UFBX_ 去掉） |
| `ufbx_foo_list`（data 为指针数组） | `UfbxFoo[]` |
| 值列表 `ufbx_vec3_list`/`ufbx_int32_list`/`ufbx_float_list`/`ufbx_uint32_list`/`ufbx_buffer_*_list`/`ufbx_string_list`/`ufbx_prop_list` | `UfbxVec3[]` / `int[]` / `float[]` / `uint[]` / `byte[][]` / `string[]` / `UfbxProp[]` |
| `ufbx_string {data,len}` | `string`（直接用 C# string；空串表示 NULL/空） |
| `ufbx_blob {data,size}` | `byte[]` |
| 函数 | PascalCase：`ufbx_load_file`→`UfbxApi.LoadFile`，`ufbxi_xxx` 内部→`internal static`，`ufbx_matrix_mul`→`UfbxMatrix.Mul`（或数学自由函数 `UfbxMath.Mul`） |
| 字段 | PascalCase：`node_to_parent`→`NodeToParent` |
| 枚举值 | `UFBX_ELEMENT_MESH`→`UfbxElementType.Mesh`；`UFBX_ERROR_PARSE_FAILED`→`UfbxErrorType.ParseFailed` |
| 宏常量 `UFBX_MAX_NAME_LEN` 等 | `internal const`，保持 PascalCase 或全大写（跟随习惯） |

## 类型映射

- `int32_t→int`、`uint32_t→uint`、`int64_t→long`、`uint64_t→ulong`、`size_t→int`（数组长度/索引）、`bool→bool`、`float→float`、`double→double`、`char*→string/byte 上下文相关`
- 解析层内部字节访问统一用 `ReadOnlySpan<byte>` + `BinaryPrimitives`（小端）。**注意** netstandard2.1 支持 Span，但 `BinaryPrimitives` 在 System.Memory 包中已内置于 netstandard2.1。位运算保持 C 的无符号语义（必要时强转 ulong/uint）。
- 元素类：`abstract class UfbxElement`（Name, Props, ElementId, TypedId, Instances, Type, ConnectionsSrc, ConnectionsDst, DomNode, Scene），具体类型 `UfbxMesh : UfbxElement` 等（对应 C 的 union 基类技巧）。`UfbxNode` **同样继承 `UfbxElement`**：C 的 `ufbx_node` 首个成员就是 `ufbx_element element`（ufbx.h），`ufbxi_read_element`、连接与实例化路径一律按 element 基类指针操作，独立类会强迫这些路径重写。（2026-10-02 裁决，覆盖此前的「独立类」写法。）
- 可空引用：C# 类引用天然可空。
- POD 值类型（`UfbxVec2/3/4`、`UfbxMatrix`（4x4 用 16 个 double）、`UfbxQuat`、`UfbxTransform`、`UfbxProp` 等）用 `struct`。
- opts 结构（`UfbxLoadOpts` 等）：`class`，字段带 C 等价零值默认；内部通过 getopt 辅助函数取值（忠实移植 `ufbxi_getopt_*`）。

## 行为保真（位精确关键点）

1. **浮点**：所有算术保持 C 语义；`double→float` 强转一致（IEEE round-to-nearest-even）；整数除法/取模 C99 向零截断与 C# 一致；移位按 C 无符号语义用 uint/ulong。
2. **数学函数必须用移植的 UfbxMath（源 extra/ufbx_math.c）**，不得用 System.Math（不一致风险）。工具类 `UfbxMath`（internal static）：`Sqrt/Abs/Pow/Sin/Cos/Tan/Asin/Acos/Atan/Atan2/CopySign/FMin/FMax/NextAfter/Rint/Floor/Ceil/IsNan`。
3. **错误处理**：内部用 `internal sealed class UfbxParseError : Exception` 携带 `UfbxErrorType`/描述/位置；C 的 `ufbxi_check(x)` → `if (!x) throw new UfbxParseError(...)`；`ufbxi_check_err(e,x)` 类似。公开 API 顶层 catch 并填 `UfbxError`，返回 null（与 C 语义一致），同时提供 Throw 版本。错误描述字符串尽量与 C 的 format 相同。
   3b. **两类失败站点必须区分（golden 构建 `UFBXI_FEATURE_ERROR_STACK==0`，ufbx.c:170-172）**：`ufbxi_check_msg`/`ufbxi_fail_msg` 走 `ufbxi_error_msg(cond,msg)` 才写 `error.description`；纯 `ufbxi_check`/`ufbxi_fail` 走 `ufbxi_fail_no_msg`→`ufbxi_fail_imp_err(err,NULL,NULL,0)`（ufbx.c:3415-3448, 6644-6653）**什么都不写**，条件字符串被丢弃，最终由 `ufbxi_fix_error_type(&uc->error,"Failed to load",p_error)`（25623）填默认串 + `UFBX_ERROR_UNKNOWN`，`type` 再由该表的字符串反推。端口用 `UfbxiFail.CheckMsg/FailMsg`（有描述）与 `UfbxiFail.CheckNoDesc/FailNoDesc`（无描述，`HasDescription=false`）两种形态逐站点照抄；唯一收敛点在 `UfbxApi.ReportFailure`。已审计模块：`Parse/Error.cs`、`Parse/Stream.cs`（IO 全部站点）、`Parse/Ascii.cs`（tokenizer 6 站点）。证据：`tools/load_oracle.txt`+`tools/LoadCheck`（2946 文件，889 条 pre-seam 错误逐字节）、`tools/DomCheck`（75 文件 S/T 记录与 C 逐字节相同）。
4. **内存**：不移植 arena/ufbxi_buf；用 `List<T>`/数组。分配器相关 opts 保留字段但忽略。
5. **UTF-8**：解析出的字符串用严格 UTF-8 校验解码（与 C 的 sanitize 行为一致：非法序列按 C 的替换/跳过策略——移植 `ufbxi_utf8_*` 逻辑）。
6. **deflate**：**不用 `System.IO.Compression`**，自带 inflate 已逐字移植（`Parse/Inflate.cs`，C `ufbx_inflate` ufbx.c:3135-3280），公开形态是 `UfbxInflateApi.Inflate`。FBX 数组是 zlib 流，头/校验/错误码语义按 `ufbxi_inflate_*` 照抄，`tools/InflateCheck` 4613 条向量（含 flip fuzz 的错误码直方图）验证。
7. **回调/线程**：progress → `delegate`；thread pool → `IUfbxThreadPool` 接口（初始版本传 null，单线程路径，与 C pool=NULL 行为一致）。
8. **文件流**：`abstract class UfbxInputStream`（Read/Skip/Size/Close）对齐 `ufbx_stream`；默认文件/内存实现内置。**负数 `Read` 返回值 = C 的 `SIZE_MAX`**（错误/失败读），不要折算成 0 或抛异常。
9. **C 的按值 opts 结构 ⇒ 端口必须值拷贝**（2026-10-04 定稿）：`const ufbx_*_opts *user_opts` 在 C 里都是
   `ufbx_*_opts opts = *user_opts;` 之后**只改本地副本**（geometry cache 读/采样是 ufbx.c:32711-32718 / 32878-32885，
   sample 路径会就地写 `additive/use_weight/weight`）。端口的 opts 是引用类型，直接别名 `opts = userOpts` 会
   **永久改坏调用方对象**（跨调用泄漏），且 `user_opts == NULL` 的站点在 C 是 `memset` 清零的合法输入。
   所以每个这类入口都要 `CopyOpts(userOpts)`（含回调子对象的深一层拷贝），并让差分记录复核调用返回后调用方 opts
   的字段原样（S4a 的 `RDO` 就是这个控制；把实现改回别名会立刻漏出 3 条）。
10. **`ufbx_format_error`（ufbx.c:30606-30642）的三个移植要点**（2026-10-04 定稿，`Util/ErrorFormat.cs`）：
   - **带偏移的 snprintf**：C 反复写 `ufbxi_snprintf(dst + offset, dst_size - offset, …)`。端口不给 `byte[]` 开视图，
     于是 `SnprintfAt` 用**绝对下标**构造 `UfbxiPrintBuffer`（`Pos = offset`、`Length = offset + size`），返回时再折回
     相对长度。推导要记住：`offset` 只会走到 `dst_size - 1` ⇒ 窗口永不为空；若为空，绝对写法会让结尾 `'\0'`
     落到窗口**前一字节**（C 的 `length == 0` 根本不写 NUL）。
   - **C 的 NULL 与空串是两件事**：`description.data == NULL` 打 `Unknown error`，而 `ufbxi_empty_char`（长度 0、
     data 非 NULL）打空。端口用 `null` / `""` 分别对应，`Description ?? UnknownError` 不能写成 `?? ""`。
     公开侧的坑：`new UfbxError()` 的 `Description` 是 `""`，而同形状的 C struct 清零后是 NULL。
   - **`UFBX_SOURCE_VERSION` 与 `UFBX_HEADER_VERSION` 是两个宏**（ufbx.c:877 / ufbx.h:270），本构建同值 23001，
     端口照样分列 `UfbxConstants.SourceVersion` / `HeaderVersion`；格式串里的三段拆位
     `/1000000u`、`/1000u % 1000u`、`% 1000u` 逐字照抄，不要用"已知 0/23/1"硬编码。
11. **C 的 `f` 后缀会被拓宽进 `ufbx_real`（== double），端口必须照抄后缀**（2026-10-04 定稿，批 J-补）：
   C 里 `const ufbx_real x = 0.01f;`、`arr[] = { 29.97f, … }`、`if (v < 0.999f)` 写的都是 **float32 常量再按
   常规算术转换提升成 double**，其值与十进制 double 字面量**不同**，凡在比较/阈值位置就会翻转结果。两个方向都会出错：
   - `(double)0.01f = 0.0099999997764825820923` **低于** double `0.01`；
     `(double)0.001f = 0.0010000000474974513` **高于** double `0.001`；`(double)0.999f`、`(double)0.8f`、
     `(double)0.1f`、`(double)0.99999f` 同理各偏一边。端口若"顺手"把 `0.01f` 写成 `0.01`，阈值移动约半个 float ulp。
   - 反之 C **没有** `f` 的站点必须保持十进制：`double step = 0.001;`（ufbx.c:26986）、
     `crease -= (ufbx_real)0.1;`（29828，与同族函数 29621 的 `0.1f` 正好相反，是本类最尖的对照）。
   - 判据只看**接收变量的声明类型**：`float weight_left = 0.333333f;`（14301）这类左侧是 `float` 的站点，
     端口对应字段也是 float，后缀自然一致，无需处理；只有落到 double 的才危险。
   - 审计命令：`grep -oE "\b[0-9]+\.[0-9]+f\b" ufbx.c | sort | uniq -c`，再逐个站点看左值类型。
     本构建的非精确值：`0.333333 / 0.0001 / 0.8 / 0.001 / 0.01 / 0.1 / 0.999 / 0.99999 / 0.0000001 /
     29.97 / 23.976 / 59.94 / 1.175494351e-38 / 1.192092896e-07`（`0.5/0.25/0.125/0.75/1.5/30.0/…` 精确，两可）。
   - 实测后果（唯一一次真分歧）：`ufbxi_pre_finalize_scene` 的三个 epsilon（18169-18171）按十进制写时，
     `maya_human_ik_7400_ascii.fbx` 在 `COMPENSATE` 下 `fabs(scale.x) <= compensate_epsilon` 由真翻假 ⇒
     bake 差分 **4958 条**不匹配；改回 `(double)0.01f` 后归零（反向变异 `E_compseps` 正是复现这 4958 条，作为承重证明）。
     同轮修掉另外 4 处：`ufbxi_time_mode_fps[]`（23646/23647/23651/23655 的 `29.97f/23.976f/59.94f`）、
     `layer->ui_color` 默认 `(0.8f,0.8f,0.8f)`（23397）、两处 `> 0.99999f`（22244 构建 / 25785 求值）。
     后 5 处在 goldens(2179) 与 bake(50261) 差分上都**观测不到**（语料没有落在半 ulp 窗口内的值），
     登记为"审计正确、差分潜伏"的保真修正，别当成已覆盖。
12. **C 的"指针身份"语义要用引用别名复刻，且内容文法只照得到一半**（2026-10-04 定稿，批 K）：
   `ufbxi_create_anim_imp`（ufbx.c:26560-26676）的重复检测（26641-26652）比的是 `element_id` **加
   `prop_name.data` 指针**，不是内容；指针之所以等价于内容，是因为名字先按名排过序 ⇒ 同名必相邻 ⇒
   `ufbxi_push_anim_string`（26520-26534）的 `prev_name` 短路让相邻同名共享同一块 arena 内存。
   端口对应写法是 `ReferenceEquals` + `prevName` 逐引用别名（`Parse/CreateAnim.cs`）。三条实测边界：
   - **照得到的**：合并 `ufbxi_strings[]` 时的 `Equal` 守卫。把它放宽成"只比索引界"（变异 `C_intern_blind`）
     会把**错的名字实例**写进 `prop_name` ⇒ `P` 记录 **1820 条**不匹配（`"Missing Prop Name"` 变成 `"Model"`）。
     即 intern 选错实例是内容可观测的，不需要额外探针。
   - **照不到的**：intern 选对了实例、只是对象身份不同。`C_dup_content`（`ReferenceEquals`→内容相等）、
     `C_prev_init`（`prevName` 初值给成真实属性名）、`Q_intern_tail`（永不命中表尾 `"d|Z"`）三例
     **全部 0 不匹配**，且都是**按构造等价**（相邻同名的引用共享与表尾命中都只影响引用，不影响字节），
     不是语料缺口——别指望再加语料能把它们照出来，要盯的是"下游是否只用相邻对"这个论证本身。
   - **`ufbxi_fmt_err_info` 的截断位点必须连 `clean_string_utf8` 一起移植**：`ufbxi_vsnprintf` 返回
     `min(pos, size-1)`（≤255），随后 `ufbxi_clean_string_utf8` 把被切开的尾部多字节序列**重写为 `?`**。
     前缀长度 = `8 + digits(element_id) + 7`，10 位元素 id ⇒ 前缀 25（奇数）⇒ 密集 UTF-8 长名必被切成半个序列。
     语料变体 23 `PK_SPLIT_LONG` 专钉这条（`A` 记录里 `info_len=255` 的 68 条 = 两条长名去重支）。
   - `if (opts) ac.opts = *opts;` ⇒ **NULL opts ≡ 全零 opts**（变体 1 与变体 0 的 `V` 场景哈希一致）；
     `O` 记录钉住"调用返回后调用方 opts 逐字段不变"（规则 9），但钉不住"拷贝 vs 别名"
     （`Q_opts_alias` 0 不匹配），因为端口体从不写 `ac.Opts`——引用规则 9 时把这个区分记下来。

13. **流 / stdio / open ABI 的语义陷阱（2026-10-04 定稿，批 L）**：这一族 9 个公开 ABI
   （`ufbx_load_stream{,_prefix}`、`load_stdio{,_prefix}`、`open_file{,_ctx}`、`open_memory{,_ctx}`、
   `default_open_file`）的差分是 `tools/StreamCheck`（78 变体 × 9 文件，11395 条）。五条实测结论：
   - **`ufbxi_end_file_context()`（ufbx.c:6960-6975）只有"成功即清空"那一半可观测**：
     失败走 `ufbxi_fix_error_type(&fc->error, "Failed to open file", error)`，而该函数**只在
     `fc->error.description` 仍为空时**才拿默认串替换；`ufbxi_fopen()` 的两条失败路径都先写了描述
     （"File not found" 7062 / "Invalid UTF-8" 7019）⇒ **默认串经 open ABI 不可达**
     （变异 `Q_endfc_defstr` 改掉默认串 → 0 条不匹配；变异 `C_fopen_nodesc` 剥掉报告点的 `$` 前缀后
     立刻变成 `UNKNOWN + "Failed to open file"`，162 条不匹配 ⇒ 两边都证明同一条规则）。
     真正咬住的是成功分支的 `ufbxi_clear_error(error)`（变异 `C_endfc_noclr` 18 条：变体 35/43 的
     脏 `ufbx_error` 必须被成功打开抹掉）。
   - **`ufbxi_fopen()` 的 `_WIN32` 分支（6984-7040）：`path_len` 只限定解码，不决定文件名**。
     宽容 UTF-8 解码器照 `path_len` 逐字节解出 UTF-16（含内嵌 U+0000），再交给 `_wfopen_s()`，后者按
     NUL 终止 ⇒ `"a.fbx\0junk"` 打开的是 `"a.fbx"`。托管 `FileStream` 没有这条规则，必须显式截断
     （`Parse/StreamOpen.cs` 的 `Fopen()`；变异 `C_nul_trunc_off` 87 条）。
     另外两个报告点的 info 载荷不同：**"Invalid UTF-8" 不带 info**，**"File not found" 带
     `path[0..path_len)`**——`A` 记录的 info 列把两者分开（`C_utf8_info` 72 条 / `C_fopen_noinfo` 144 条）。
   - **`ufbxi_check_return_msg(cond, ret, msg)` 不等于失败传播**：`ufbxi_ascii_refill()`（ufbx.c:9437）
     在 `read_fn` 返回 SIZE_MAX 时把 "IO error" 写进 `uc->error`，然后**返回 `'\0'`**，分词器把它当
     `UFBXI_ASCII_END` ⇒ 读失败**静默截断 ASCII 流，加载照样成功**（成功尾 25618 再把那条描述抹掉）。
     端口必须 `return '\0'`，不能 throw（`Parse/Ascii.cs`；变异 `C_ascii_io_throw` 7 条）。
     判据是"返回值在**调用方**的含义"：`ufbxi_refill`/`ufbxi_read_to`（6745/6907）的 NULL/0 会被继续
     `ufbxi_check` 传播，照旧 throw；`ufbxi_ascii_refill` 的 `'\0'` 是**数据**不是失败。
     ⇒ 新增 `ufbxi_check_return_*` 移植点时先判这一条。
   - **`uc->opts.progress_cb` 是每个报告点直接读的**（ufbx.c:6671/6693/25254）。端口把它收敛在
     `UfbxiStream.ProgressCb` 上，所以必须在 opts 值拷贝之后**显式接线**
     （`Parse/Load.cs`，`uc.Stream.ProgressCb = uc.Opts.ProgressCb;`）；漏掉则进度回调一次都不发
     （变异 `C_progress_unwired` 4342 条 = 差分里全部 `P` 记录）。
     `ufbx_load_stream_prefix()`（30569-30584）**不设** `progress_bytes_total`（`ufbx_load_memory`
     在 30517 设了）⇒ 首个 `size_fn` 查询前 `BytesTotal == BytesRead`（变异 `C_prefix_total` 1102 条）。
     另：`ufbxi_stdio_size()` 返回**剩余**字节（`end - begin`），`ufbxi_memory_size()` 返回**总**字节。
   - **`close_fn == NULL` 在端口没有对应状态**：规则 8 把回调四元组收敛成 `UfbxInputStream`，其
     `Close()` 是永远存在的虚方法，所以"不 close"（`ufbxi_stdio_init(..., close=false)`）与
     "close 了什么也不做"是同一个对象。可观测部分由 `K` 记录钉（`ufbx_load_stdio_prefix` 之后句柄仍
     开着、位置可读；变异 `C_stdio_close_owned` 180 条 = 句柄被提前 dispose 后 `K` 读不到位置）；
     oracle 侧的 `SCRIPT` 变体因此给脚本流配了一个 no-op `close_fn`，以便两侧都能钉"恰好关一次"。

14. **线程池的语义陷阱（2026-10-04 定稿，批 M）**：`ufbx_thread_pool_run_task` /
   `ufbx_thread_pool_set_user_ptr` / `ufbx_thread_pool_get_user_ptr`（ufbx.c:32984-32999）三个
   `ufbx_unsafe` 公开 ABI + 它们依赖的全部内部机制。差分是 `tools/PoolCheck`（21 变体 × 8 文件，
   1688 条）。实测结论：
   - **`ctx`（`ufbx_thread_pool_context`，ufbx.h:4614）是裸地址，不是引用**：C 无条件
     `(ufbxi_thread_pool*)ctx` 后解引用（32986/32991/32997）⇒ 除 ufbx 自己在
     `init_fn`/`run_fn`/`wait_fn`/`free_fn` 里给出的那个值外，任何值都是野读野写（连 `ctx == 0`
     也是）。端口用一张注册表把 `nint` 映回实例（`UfbxiThreadPool.FromCtx`），非法值抛
     `UfbxiThreadContextException` —— **刻意不是 `UfbxParseError`**：C 没有这种失败，它绝不能
     变成 `ufbx_error`。这与批 L 的 `ufbx_open_file_context`（#4，安全传 NULL）是同一类问题但
     **更严**：那里是"忽略"，这里是"不可构造"。
   - **`ufbxi_thread_pool_execute()` 取模**（6011）：`&pool->tasks[index % pool->num_tasks]`，
     **不是** `tasks[index]`。端口照抄（`UfbxiThreadPool.Slot`；变异 `C_exec_nomod` 156 条、
     `C_api_index_plus1` 561 条）。`num_tasks` 默认 2048（6072-6074）；
     `ufbxi_min_sz(num_tasks, INT32_MAX)`（6071）的夹取**不可达**——超过可分配规模先撞上分配失败
     （#4），C 报 `UFBX_ERROR_OUT_OF_MEMORY` / info "temp"（6088）。
   - **任务槽只在第一圈清零**（6130-6151 的 `if (index < num_tasks) memset(...)`）⇒ 复用槽保留
     上一个任务的 `error`。端口用"按需创建"的 slot 表达同一件事（没见过的槽 ⟺ `index < num_tasks`），
     所以显式 memset 那一行在端口是**构造性冗余**（变异 `Q_create_memset` 0 条 —— 不是漏测，
     是这条规则在托管侧由构造保证）。
   - **成功清 error、失败填空串**（6013-6016）：`task->error = ""` 只在 task fn 没设 error 时才
     生效，而两个 fn（`ufbxi_deflate_task_fn` 8912 / `ufbxi_ascii_array_task_fn` 10145）失败时都
     先写了自己的串 ⇒ 该分支**不可达**（变异 `Q_exec_error_empty` 0 条）。
   - **`user_ptr`（5992）与 `ufbx_thread_pool.user`（4655）是两个东西**：前者属于"持有 ctx 的那个
     工作线程"（ufbx.h:4613 的 HINT），经 set/get 存取，未设置时 get 返回 NULL（ufbx.h:5750）。
     端口按规则 12 用 `object` + 引用相等（变异 `C_userptr_set_sink` / `_get_null` 各 84 条 = 全部 `U`）。
   - **两个生产者，且都有门限**：binary 的 DEFLATE 任务（`encoding==1 && encoded_size >= 256`，
     `UFBXI_MIN_THREADED_DEFLATE_BYTES` 60，站点 9090-9131）与 ASCII 的延迟数组任务
     （`count >= 64`，`UFBXI_MIN_THREADED_ASCII_VALUES` 61，站点 10565-10657，且只作用于
     post-7000 的 `*N { ... }`）。**没有 pool ⇒ `create_task` 恒返回 NULL ⇒ 走各自的内联回退**
     （9134 起 / 10654-10656）⇒ 门限本身在没有线程池时完全不可见。
     小语料文件（<25KB 的 binary、6100 的 ascii）一个任务都不产生，这也是差分要钉的一部分。
   - **未完成的任务 = arena 未初始化字节（#4）**：`run_fn` 不执行、或只执行一部分时，那些任务的
     目标缓冲区保持 `ufbxi_push_array_data()` 分配出来的内容，**不是零**。实测
     `data/blender_293_barbarian_7400_binary.fbx` 上两次运行 `E` 哈希不同 ⇒ 该情形**连 C 自身都
     不可复现**。差分因此只覆盖"任务全部执行"与"pool 不成立（缺 run_fn 或缺 wait_fn，6070）"两种。
   - **批处理的 `max_tasks = min(num_tasks / GROUPS, available_tasks())`（15191-15193）是关于任务
     数而非节点数**；`tmp_buf` 的内存门限（15192）与收尾的 `wait_all`（15232）在"全部内联执行"
     的驱动下不可观测（后者变异 `Q_batch_waitall` 0 条）。`UFBX_THREAD_GROUP_COUNT` = 4（ufbx.h:170）。
   - **`free_fn` 由 `ufbxi_free_temp()` 调起**（25421-25422），即加载成功与失败都会跑，且在其它
     清理之前；端口挂在 `Parse/Load.cs` 的 `FreeTemp()` 上（变异 `C_free_no_freefn` 64 条）。

15. **skinning 求值的语义陷阱（2026-10-04 定稿，批 N）**：`ufbxi_evaluate_skinning()`
   （ufbx.c:25063-25177）与它的两个调用点。差分是 `tools/SkinCheck`（17 变体 × 12 文件，
   1453 条）。实测结论：
   - **两个调用点，且只有载入侧被 goldens 覆盖**：`ufbxi_load_imp` 的 25367-25373 与
     `ufbxi_evaluate_imp` 的 26413-26419。`test/hash_scene.c:103` 给 **load** opts 设了
     `evaluate_skinning = true`，而 `test/hash_scene.h:620-622` 哈希 skinned 三件套 ⇒
     **载入侧早就被 2179 个 golden 逐位证明了**；但 `test/hash_scene.c:136` 传给
     `ufbx_evaluate_scene()` 的是 **NULL**（`eval_opts` 那个局部变量填了却从未被传），
     ⇒ **evaluate 侧任何 golden 都到不了**，必须靠 `tools/SkinCheck`（变异 `C_eval_no_skin`
     195 条、`C_load_no_skin` 72 条分别钉住两侧）。
   - **两处的 `load_caches` 来自不同的 opts 结构体**：载入侧是
     `uc->opts.load_external_files && uc->opts.evaluate_caches`，evaluate 侧是
     `ec->opts.load_external_files && ec->opts.evaluate_caches`（变异 `C_eval_caches_off/on`
     6/29 条）。`cache_opts` 只装一个 `open_file_cb`，其余全零。
   - **evaluate_skinning 只写这几个字段**：`skinned_position.values.data`、
     `skinned_normal.{exists, values.data, values.count, indices.data, indices.count,
     value_reals, unique_per_vertex}`、`generated_normals`、`skinned_is_local`。
     ⚠️ **`skinned_position.values.count` 一个字都不写**（25138 只赋 `.data`）⇒ 它必须**本来就等于
     `num_vertices`**（载入时已如此）；端口用"右大小的切片"表达同一件事。
   - **先 blend 再 skin**（25124-25133）：`ufbx_add_blend_vertex_offsets()` 把偏移加到顶点上，
     **然后**皮肤矩阵变换的是"已经加过 blend 偏移"的坐标。顺序反了就错（变异 `C_skip_blend` 75 条）。
   - **只取 `skin_deformers.data[0]`**（25129），C 自己挂着
     `// TODO: What should we do about multiple skins??`（25127）。探针证实
     **690 个 `data/*.fbx` 里没有任何 mesh 带 2 个以上 skin deformer** ⇒ 该区分造不出来
     （变异 `C_skin_last_not_first` 0 条，属语料缺口）。
   - **兜底矩阵** = `instances.data[0]->geometry_to_world`，只在 `instances.count > 0` 时存在
     （25128）；`ufbx_catch_get_skin_vertex_matrix` 在 `total_weight <= 0.0` 时用它，否则用单位阵
     （变异 `C_fallback_null` 60 条、`C_nofallback` 60 条）。
   - **`topo` 是跨 mesh 共用的暂存区**（25077-25078 按 `max_skinned_indices` 一次性分配），
     容量不可观测（变异 `Q_topo_bigger` 0 条）。
   - **`ufbxi_push(num_vertices + 1); result_pos++` 造出的哨兵槽永不回读**（25096-25097，
     法线侧 25149-25150 同理）⇒ 在托管侧是构造性冗余（变异 `Q_skin_sentinel` 0 条）。
   - **`num_normals == num_vertices` 才置 `unique_per_vertex`**（25145-25147），且喂给
     `ufbx_compute_normals()` 的是 **`&mesh->skinned_position`**（刚算完的蒙皮坐标），不是原始顶点
     （25157；变异 `C_unique_never` 30 条、`C_normals_skip` 390 条、`C_skin_normal_smooth` 30 条）。
   - **cache 通道按 `interpretation` 分流**：`VERTEX_POSITION`/`POINTS` 填位置（并置
     `skinned_is_local = true`），`VERTEX_NORMAL` 填法线；两者都只在 `num_read == num_*` 时才算成功
     （25099-25121）。
   - **`if (mesh->num_vertices == 0) continue;`**（25079）：探针证实语料里不存在"带 deformer 且
     0 顶点"的 mesh（变异 `C_skin_zeroverts` 0 条，语料缺口）。
   - **错误尾不可达**：两处调用点分别是 `ufbxi_check` / `ufbxi_check_err`，失败源只有 `ufbxi_push`
     的 OOM（#4）与 `UFBXI_FEATURE_SKINNING_EVALUATION == 0`（该宏在本构建为 1，ufbx.c:90）
     ⇒ "Failed to evaluate" 与传给它的 `ufbx_error` 对象都不可观测（变异 `Q_eval_error_fresh` 0 条）。

16. **`pivot_handling` 维度的语义陷阱（2026-10-04 定稿，批 O）**：`ufbxi_pre_finalize_scene` 的
    pivot 块（ufbx.c:18329-18457）在 goldens 里**一次都不会执行**，因为 `test/hash_scene.c` 的
    load opts 是 `{0}`，而 `UFBX_PIVOT_HANDLING_RETAIN` 就是 **0**（ufbx.h:3717）—— 即"把 pivot
    计入变换"的**默认**行为。**不存在 `UFBX_PIVOT_HANDLING_NONE`**；批 O 的交接文档曾误写成 NONE，
    已更正。⇒ 批 O 的整块覆盖全靠 `tools/PivotCheck`。逐点：
   - **块的主要产出 `adjust_pre_translation` 不在 `ufbxt_hash_scene()` 里**：`test/hash_scene.h:447-451`
     只哈希 `adjust_pre_rotation` / `adjust_pre_scale` / `adjust_post_rotation` / `adjust_post_scale` /
     `adjust_mirror_axis`。⇒ **端到端哈希看不出 pivot 块改了什么**，差分必须发定位记录
     （`PivotCheck` 的 `P` 记录逐节点发 `adjust_pre_translation` 与四个被改写的属性）。
   - **两个"要不要处理"的谓词不是同一个**：`ADJUST_TO_PIVOT` 用 `ufbxi_is_vec3_zero(rotation_pivot)`
     （**精确** `== 0.0`，ufbx.c:11570-11573，用 `&` 不是 `&&`）；`ADJUST_TO_ROTATION_PIVOT` 用
     `ufbxi_pivot_nonzero()`（`|x| >= 0.0009765625`，18091）对 **rotation / scaling pivot / scaling
     offset 三者任一**。端口必须各抄各的，不要"统一成一个"。
     ⚠️ 但**现有语料区分不出这两个谓词**：690 个 `data/*.fbx` 里最小的非零 pivot 分量是
     `0.0079944331810126099`，比阈值大 8 倍，没有任何 pivot 落在 `(0, 0.0009765625)`
     ⇒ 变异 `C_atp_pred_epsilon`（换用另一个谓词）**0 条**，属**语料缺口**。
   - **`pivot_handling_retain_empties` 只在 `ADJUST_TO_ROTATION_PIVOT` 下、且只对 EMPTY 节点起作用**
     （18345-18353），两个取值都不是"保留"而是切换两个布尔：
     `!retain` ⇒ `skip_geometry_transform = true`；`retain` ⇒ `can_modify_geometry_transform = false`。
     ⚠️ **后者会让整个块不执行**（最终门槛是 `can_modify_pivot && (can_modify_geometry_transform ||
     skip_geometry_transform)`，两者皆假即跳过）⇒ 在一个"只有 EMPTY 带 pivot"的文件上，
     **`ADJUST_TO_ROTATION_PIVOT + retain_empties` 与 `RETAIN` 的场景哈希完全相同**
     （`maya_null_pivots_7700_ascii.fbx` 实测：两者都是 `55764fa750ca291d`）。这不是 bug。
   - **三条否决 `can_modify_geometry_transform` 的路**：EMPTY+retain、MODIFY_GEOMETRY_NO_FALLBACK 下
     `instance_counts > 1 || modify_not_supported`（18360-18364，语料靠
     `maya_instanced_pivots_7700_ascii.fbx`）、`pre_node->has_skin_deformer`（18367-18368，语料靠
     `maya_slime_7500_binary.fbx`，C 的注释是 "Currently, geometry transform messes up skinning"）。
   - **epsilon 一半带 `f` 一半不带**（规则 11）：`scale_epsilon`/`pivot_epsilon` 是
     `const ufbx_real ... = 0.001f`（18169-18170，拓宽后的 double），`compensate_epsilon = 0.01f`；
     而 `ufbxi_pivot_nonzero` 的 `0.0009765625`（18093）与 `ufbxi_pivot_div` 的 `0.0078125`（18099）
     是**真 double 字面量**。照抄后缀。
     ⚠️ `pivot_epsilon` 的**具体数值**在现有语料上不可分辨：`err > 0.001` 与 `err > 0` 判定一致
     （变异 `C_eps_zero` 0 条，因为**没有节点**满足 `0 < |rp-sp|₁ <= 0.001f`），但改成 1e300
     会咬 452 条（`C_eps_huge`）⇒ 判定逻辑在视野内，**只有边界没覆盖**，仍是语料缺口。
   - **`ufbxi_pivot_div` 带零缩放保护**（18098-18106）：`|initial_scale| < 0.0078125` 时不除、原样返回
     offset；此时父节点会留下非零平移，C 明确说这是预期的（18394-18396 的注释）。语料靠
     `maya_zero_scale_pivot_*`。
   - **属性改写后必须 sort + dedup**（18432-18434）：新的 synthetic prop 追加在数组**末尾**，
     `ufbxi_sort_properties()` 之后才按名字排好，`ufbxi_deduplicate_properties()` 才会吃掉被同名新
     prop 覆盖掉的旧 prop。C 只把 `count` 写到 `new_prop_count` 而分配更大（18385/18417），
     端口用 `Array.Resize` 到 count 表达同一件事 ⇒ **分配容量不可观测**（变异
     `Q_props_capacity_atp` / `_atrp` 各 0 条）。
   - **`adjust_pre_translation` 累加两处**（18440 / 18446）：节点自身 `+= rotation_pivot`，
     **所有直接子节点 `+= child_offset`**。两条路径的 `child_offset` 不同：
     `ADJUST_TO_PIVOT` 是 `-rotation_pivot`；`ADJUST_TO_ROTATION_PIVOT` 是
     `unscaled_offset - scaling_pivot`。
   - **`ufbx_assert(!skip_geometry_transform)`**（18382）在 NDEBUG 下消失，且本就不可达
     （`skip_geometry_transform` 只在 `ADJUST_TO_ROTATION_PIVOT` 下置位）。

17. **scale helper 的语义陷阱（2026-10-04 定稿，批 P）**：`ufbxi_setup_scale_helper()`
    （ufbx.c:12556-12602）、建 helper 的主循环与递归子节点循环（18478-18539）、连接重映射
    （18720-18745）、求值侧消费（22969-22979）、bake 侧消费（27280-27352 / 27421-27458）。
    与批 N、批 O 相反，这一块**是真的没被 goldens 覆盖过** —— 逐点：
   - **`inherit_mode_handling = PRESERVE`（默认值，也是 goldens 唯一用到的值）在全部 690 个
     `data/*.fbx` 里创建 0 个 scale helper**（`tools/_scratch/_sh_probe.txt`）。helper 只在
     `HELPER_NODES`（28 文件 / 255 节点）和 `COMPENSATE`（20 文件 / 46 节点）下出现；
     `COMPENSATE_NO_FALLBACK` 与 `IGNORE` 下也是 0。⇒ 这一整块**此前没有任何差分证明**，
     批 P 全靠 `tools/ShCheck`。
   - **`has_recursive_scale_helper` ≠ "helper 的父节点也是 helper"**。批 P 的探针一开始按后者
     统计，得到"全语料 0 个"并据此判成语料缺口；**实测是错的**——变异
     `Q_sh_recursive_setup` / `_gate` / `_comp` 分别咬 688 / 688 / 64 条，命中
     `motionbuilder_sausage_rrss_7700_binary.fbx`。C 的含义是"通过递归遍历给子节点建的 helper"。
     **教训：探针的判据必须和被测代码的条件逐字对齐。**
   - **`ufbxi_setup_scale_helper()` 把属性"搬走"而不是"复制"**（12582-12589）：
     `helper_props[num_props++] = *src_prop;` 之后 **`src_prop` 就地被写回默认值**
     （GeometricRotation / GeometricScaling / GeometricTranslation / Lcl Scaling 四者）。
     ⇒ helper 拿到原值（**含动画**），源节点拿到默认值。
     `src_prop->value_int = (int64_t)src_prop->value_vec3.x;` 也要照抄（变异 `C_shr_move_props` 4577 条）。
   - **`ufbxt_hash_scene()` 哈希得到 helper 的存在**（test/hash_scene.h:432 `scale_helper`、
     458 `is_scale_helper`、459 `is_scale_compensate_parent`、460 `node_depth`），但**只看静态
     变换**。求值侧 `parent->scale_helper->Lcl Scaling` 折进子节点 translation（22969-22979）
     在动画下的行为**必须自己发定位记录**（`ShCheck` 的 `X` 记录，三个采样时刻）。
   - **bake 侧两条独立路径**：`scale_helper_t`（27282，`!node->is_scale_helper && parent->scale_helper`）
     影响 **translation** 的重采样；`scale_helper_s`（27337，`node->is_scale_helper &&
     parent->inherit_scale_node->scale_helper`）影响 **scale** 的重采样。两条都在
     `UFBX_TRANSFORM_FLAG_IGNORE_SCALE_HELPER` **之外** —— 那个标志（27412）是 bake 内部
     无条件加的，与这两条无关，别混。
   - **`resample_translation` 只影响节点自身 anim prop 的重采样**，不影响
     `ufbxi_push_resampled_times()`（那条无条件执行）。批 P 的变异 `C_bake_t_pushtimes` 咬 60 条、
     而 `C_bake_t_resample` 0 条，正是这个区别。
   - **`constant_scale_t = node->parent->scale_helper->inherit_scale;` 的 `else` 分支
     （27289）在公开 API 下不可达**：它要求 helper 不在 `baked_nodes` 里，而公开
     `ufbx_bake_opts` 没有"排除某些节点"的开关。
   - 未覆盖：`ufbxi_push_synthetic_element()` / `ufbxi_push()` 分配失败（#4）。

18. **`ufbx_quat_fix_antipodal()` 在默认 bake 选项下永远不翻转（2026-10-04 定稿，批 Q）**，
    以及"如何证明一个分支到底可不可达"。逐点：
   - `ufbx_quat_fix_antipodal()`（ufbx.c:31527-31532）的翻转条件
     `ufbx_quat_dot(q, reference) < 0.0f` 在 **默认 `ufbx_bake_opts` 下、对全部 690 个
     `data/*.fbx`、7710 个旋转关键帧，命中 0 次**。原因：默认 30 Hz 重采样让相邻 baked
     旋转帧的夹角远小于 90°，点积恒为正。
   - 要让它翻转必须**稀化采样**：实测只有 `maya_dq_weights_7500_ascii/binary` 在
     `resample_rate = 1.0` / `max_keyframe_segments = 1` / `resample_rate = 0.5 +
     minimum_sample_rate = 1000` 等 5 组设置下翻转（每文件 5 帧）。这两个文件已加进
     `tools/bake_corpus.txt`，`Q_antipodal_off` 随即从 0 条变成 8 条。
   - **⚠️ 判据不能靠"推理出来的等价量"**。批 Q 第一版探针用「baked 关键帧与同刻
     `ufbx_evaluate_transform()` 四元数的点积 < 0」判断翻转，得到 668 条——**全是假阳性**。
     可信做法：把 ufbx.c 复制到私有副本（`tools/_scratch/ufbx_nofix.c`）改掉那一行，
     **同一份探针分别链接原始与补丁版**，逐帧打印 IEEE 位模式再 diff。
     推论：**"某分支没有覆盖" 和 "某分支不可达" 必须用打补丁的 C 对照来区分，不能靠估算。**
   - 对照结论：`Q_antipodal_inv`（把 `< 0.0` 改成 `> 0.0`）咬 24995 条 —— 这只能证明
     `FixAntipodal` 被调用且结果可观测，**不能**证明翻转那一支被走到。两种变异要一起看。
   - `ufbxi_bake_times()`（ufbx.c:26773-26826）的重采样/裁剪（padding、start/stop、
     factor 折半、flat 段跳过、min_duration 跳过）**已被 BakeCheck 充分覆盖**：
     6 个变异全咬（5015 / 2691 / 1489 / 1423 / 851 / 831）。⇒ 批 J 遗留的
     "动画区间严格内嵌于 bake 区间"**不是缺口**。
   - **`tools/_mut_bake.sh` 已从 perl 改成 python**（本机已无 perl）。批 J/K 的历史 sweep
     数字仍有效（那时 perl 还在），但**任何新跑若用旧脚本会静默无效**。

19. **手搓合成 FBX 的两个硬性要求（2026-10-04 定稿，批 R）**：
   - **`Deformer::` 必须连到 Geometry，不是 Model**：`C: "OO", <Skin>, <Geometry>`
     （照 `maya_dual_quaternion_scale_7500_ascii.fbx`）。连到 Model 时
     `mesh->skin_deformers.count` 恒为 0，探针会误报"形态不存在"。
   - **多个 Skin 的骨骼必须在不同位置**：否则 `skin_deformers.data[0]` 与 `.data[1]`
     产生完全相同的顶点，"TODO: multiple skins"（ufbx.c:25127）依旧不可观测。
   - 合成语料放 `tools/synth/`，**在 corpus 里用绝对路径** —— `C:/Workspace/_analyze_ufbx`
     是只读共享参考树，不能往它的 `data/` 里写。
   - 已闭合的 4 个缺口：`C_atp_pred_epsilon` 0→20、`C_eps_zero` 0→20、
     `C_skin_last_not_first` 0→30、`C_skin_zeroverts` 0→30。

## 禁止事项

- 禁止用 `System.Math`/`MathF`（除显式注明且经向量验证的场合）。
- **浮点语义——全文无 FP 收缩（见下方「浮点语义」）**：默认禁止改变求值顺序/合并浮点表达式（不要把 `a*b + c*d` 自行改写成 FMA；C# 编译器不会自动 FMA；float 算术在 C# 默认无 excess precision，与 SSE2 编译的 C 一致）。C 源码里"乘法结果直接喂给加法"的站点在 C# 写成 `UfbxMath.Fma(x, y, z)` 作为语义标记（现实现即 `a*b+c`），禁止用 `System.Math`（唯一例外 `UfbxMath.Sqrt`，等价硬件 `sqrtsd`，已向量化验证）。
- 禁止跳过防御检查（C 里的 overflow/边界检查都要保留，用等价异常或错误路径）。
- 禁止使用 record/init 属性（netstandard2.1 + Unity 兼容）。

## 浮点语义（取代旧「例外记录 #1」，2026-10-02 定稿）

**golden 参考二进制统一用无收缩编译：`zig cc -O2 -mcpu=x86_64 -ffp-contract=off`**（`test/hash_scene.exe`、`tools/mathvec.exe` 均已换用该构建，golden 与向量已按此重新生成）。

事实与决策过程：
1. 首版 golden 用 `zig cc -O2`（默认 `-mcpu=native`）编译，Clang 的 `-ffp-contract=on` 把 `x*y ± z` 收缩成硬件 VFMADD（两个 exe 各含 18 处编码），数学核呈"乘加单舍入"语义：普通移植 71979/72000（21 条 1-ULP 失配，全在 sin/cos/tan/atan），软件 FMA 后 72000/72000。
2. 但该语义依赖"恰好在本机（有 FMA 的 CPU）编译"——换台无 FMA 的机器 golden 即静默改变，出处不可复现；且上游官方 CI（MSVC x64、GCC/Clang x86-64 基线）均无收缩。
3. 实测两种语义的场景哈希在整个语料上无差异（抽查+全量重生成比对），只有 21/72000 条合成极端向量不同。
4. 故定稿：**全文无收缩**。`UfbxMath.Fma` 保留为标记站点（乘喂加），实现退化为 `a*b+c`；`tools/math_vectors.txt` 与 `golden_hashes.txt` 均由无收缩构建生成（向量 72000/72000 归零）。

**未来任何重新生成 golden/向量的 C 构建必须带 `-mcpu=x86_64 -ffp-contract=off`**，否则参考值会随宿主机 CPU 漂移。

### 第二条地基规则：参考构建必须用 ufbx 自带数学核（2026-10-04 定稿，S4b-1）

**所有 `#include "ufbx.c"` 的 oracle 必须 `#define UFBX_EXTERNAL_MATH`（写在源文件里，include 之前）并把 `extra/ufbx_math.c` 放上链接行。**
构建行统一为：

```
zig cc -O2 -DNDEBUG -std=c11 -mcpu=x86_64 -ffp-contract=off \
     -I C:/Workspace/_analyze_ufbx <oracle>.c C:/Workspace/_analyze_ufbx/extra/ufbx_math.c -o <oracle>.exe
```

依据（都是实测，不是推测）：

1. `ufbx.c:238-277`：`UFBX_EXTERNAL_MATH` **未**定义时，`UFBX_MATH_PREFIX` 被定义成**空**宏，于是 `ufbx_atan2 → ufbxi_pre_cat(, atan2) → atan2`，即宿主 CRT。zig cc 默认 target 是 `x86_64-windows-gnu`，链接 `api-ms-win-crt-math`（UCRT/Intel IML），**不是** fdlibm/LIBM。
2. 权威参考生成器本来就走软件数学核：`test/hash_scene.c:284` 自己写了 `#define UFBX_EXTERNAL_MATH`，`misc/run_tests.py:1541` 的源文件列表是 `["test/hash_scene.c", "extra/ufbx_math.c"]`。实测：现 `test/hash_scene.exe` 与新构建的 sw 版在带动画层语料上 frame 0/1/4/9 哈希**逐位相同** → `golden_hashes.txt` 是 ufbx_math 语义。`tools/mathvec.c` 同样定义了该宏（不带 ufbx_math.c 会 `undefined symbol: ufbx_pow` 链接失败）→ `math_vectors.txt` 72000 条也是 ufbx_math 语义。
3. `src/Ufbx/Math/UfbxMath.cs` 是 `extra/ufbx_math.c` 的逐行移植，因此**端口一侧本来就对齐 goldens**。此前 S4b-1 的 243 条分歧（P/Q/R，文件 3/5/6/7/17/18）是 oracle 一侧误绑 CRT 的 `atan2` 造成的假阳性，不是移植缺陷：`ufbx_quat_to_euler`（ufbx.c:31630+，仅由 `ufbxi_combine_anim_layer` 的 additive/blended `compose_rotation` 路径调用）里的 `atan2(az, ax)` 即分歧点，`atan2(3f3a819a7af95c38, 3fefffffd416aeec)`：CRT `…afb` vs ufbx_math/端口 `…afc`。
4. 两实现的系统性偏差实测（各 40000 条、mathvec 同款分布，`tools/_s4b_mathref.c`）：`atan2` 12.1%、`pow` 4.1%、`cos` 0.27%、`sin` 0.26%、`atan` 0.03%、`asin`/`floor`/`ceil` 仅 NaN-payload 边角；`sqrt/fabs/rint/tan/copysign/fmin/fmax/nextafter` 为 0。**所以只有触及 atan2/pow/sin/cos 的 oracle 有真风险**，其余历史产物（dom/graph/load/util/numeric/ascii/inflate/s3*/s4a/s4c 至今 0 分歧）说明那些路径在语料里根本没被触发。
5. 复现口径：`tools/s4b_oracle.exe/.txt` 已按本节命令重建，`S4bCheck` 现为 **12091 行 / 0 分歧（ALL MATCH）**（S4b-2 起含 `V`＝评估态 golden 哈希、`W`＝评估扫完后的源场景哈希；公开门面波起含 `X`＝语料顶点访问器 + panic 探针、`Y`＝合成属性的 NO_INDEX/越界分支、`F`＝find_*/get_* 场景查找组九段，第 9 段＝`ufbx_find_face_index`）。以后看到"1 ULP、只出现在 sin/cos/atan2/pow、且只在带动画层/缓动的文件上出现"的分歧，先查 oracle 的数学绑定，再查端口。
   - 顶点访问器不碰任何数学，所以 X/Y 在没有 `-DUFBX_EXTERNAL_MATH` 的构建里也不会漂；重建后**必须**用"既有记录逐字节相同"来确认（本轮就是这么验的：去掉 X/Y 后 11761 条与重建前完全一致；扩 `F` 的那两次重建同样逐字节验收到只剩该变的段为止——最后一次只允许 30 条 `F <fi> 8` 变化）。
   - `F` 第 8 段（`ufbx_get_compatible_matrix_for_normals`）只走 mul/add/比较（`ufbx_transform_to_matrix` ufbx.c:31836-31860、`ufbx_matrix_for_normals` 31792-31810 都没有 libm 调用），因此同样 CRT 无关；`F` 的其它段是查表与字符串比较，也不碰数学核。
   - **oracle 产物是 CRLF**（Windows 文本模式 stdout），`grep`/管道会把它归一成 LF：验收"既有记录逐字节相同"必须用 `diff --strip-trailing-cr`，否则会看到"12061 行全不同"的假象。


## 文件布局

- `src/Ufbx/Math/UfbxMath.cs` ← extra/ufbx_math.c
- `src/Ufbx/Math/Types.cs` ← ufbx.h 数学类型与内联操作
- `src/Ufbx/Enums.cs`、`Types.cs`、`Props.cs`、`Error.cs` ← ufbx.h
- `src/Ufbx/Model/**` ← ufbx.h 数据模型（**唯一被编译的模型层**，按元素类别分文件）。曾有一份单体 `Types.cs` 与其重复定义 274 个类型，已移到 `src/Ufbx/_quarantine/Types.duplicate.cs.txt`（不编译）。不要再往 csproj 里加 `<Compile Remove="Model\**\*.cs" />`；缺的类型按解析层实际需要补进 `Model/`。
- `src/Ufbx/Parse/*` ← ufbx.c 解析层
- `src/Ufbx/Scene/*` ← 场景/连接/元素构建
- `src/Ufbx/Utils/*` ← generate_indices、triangulate、topology、normals、skin、subdiv
- `src/Ufbx/Anim/*`、`Nurbs/*`、`Cache/*`、`Obj/*`
- `src/Ufbx/Api/*` ← **公开门面层**（2026-10-04 起）：一个 C 前缀一个 `public static class`，只做转发，不带函数体。
  `UfbxApi`（load/evaluate 入口 + 引用计数空操作）、`UfbxEvaluate`（`ufbx_evaluate_*`）、`UfbxDom`、
  `UfbxAs`（`ufbx_as_*` 降型）、`UfbxTopologyApi`（拓扑/法线/顶点访问器）、`UfbxGeometryApi`（细分/NURBS/generate_indices）、
  `UfbxInflateApi`（`ufbx_inflate`）、`UfbxGeometryCacheApi`（geometry cache 的 load/read/sample 六件）、
  `UfbxSkinApi`（ufbx.h "Skinning" 一节：skin 顶点矩阵 + blend shape/deformer 偏移七件）、
  `UfbxErrorApi`（`ufbx_format_error`，"Errors" 一节里唯一的函数）。
  函数体一般仍留在 `Parse/**`/`Util/**` 的 internal 类里 ⇒ 既有差分（S4b/S4c/S3bc/S4a/InflateCheck）继续覆盖同一份实现。
  **harness 也要走门面**，否则转发层本身是暗区：`tools/InflateCheck/InflateCheck.cs`、`tools/S4aCheck/Program.cs`
  与 `tools/UtilCheck/Program.cs` 都是把调用点改成 `Ufbx*Api.*` 而不是 internal 方法。
- `src/Ufbx/UfbxApi.cs` ← 公开入口
- 隔离验证工程（`tools/InflateCheck`、`tools/MathVectorCheck` 这类 `EnableDefaultCompileItems=false` 的）
  **必须手工把新增的 `Api/*.cs` 列进 `<Compile Include>`**，它们不会自动纳入；`tools/S4aCheck`、`tools/S4bCheck`、
  `tools/S4cCheck` 用 `..\..\src\Ufbx\**\*.cs` 通配，自动纳入。

## 解析层地基（已落地，后续模块按此对接）

- `Parse/Error.cs`：`UfbxParseError`（携带 `UfbxErrorType`）+ `UfbxiFail.Check/Fail`，对应 C 的 `ufbxi_check/ufbxi_fail*`（ufbx.c:3415-3450, 6638-6657）；错误文案沿用 C 的条件字符串（"Empty file"/"Truncated file"/"IO error"/"Cancelled"）。
- `Parse/Stream.cs`：`UfbxiStream` ← `ufbxi_context` 的 IO 状态机（ufbx.c:6664-6920）。C 的 `data_begin/data/data_size/yield_size` 指针语义用 `Buffer/BeginIndex/Position/Remaining/YieldSize` 复刻：`Remaining` **不含** yield 窗口，可用字节 = `YieldSize + Remaining`，`Pause/Resume/Yield` 在两计数间搬移字节的节奏与 C 一致。`PeekBytes/ReadBytes` 返回的是 `Buffer` 内的下标，且只在下次 refill/yield 前有效（等同 C 返回裸指针的约束）。
- `Parse/InputStreams.cs`：#8 要求的默认内存/文件流。`CanSkip` 对应 C 可选的 `skip_fn`（C 两条路径都在 `ufbxi_skip_bytes` 里，行为不同，必须保留）。`Read(byte[] buffer, int offset, int count)` 的 `offset` 对应 C 直接往任意指针读（`ufbxi_read_to` 需要）。
- `Model/Opts/RuntimeOpts.cs`：`UfbxInputStream`（abstract，Read/Skip/Size/Close）= C `ufbx_stream`。

## DOM/数组数据约定（2026-10-02 定稿，后续 element reader 一律按此对接）

- **`ufbx_real` = `double`**：ufbx.h:154-159 默认（未定义 `UFBX_REAL_IS_FLOAT`）取 double，golden 二进制也是 double 构建。故 `ufbxi_normalize_array_type('r')` → `'d'`，`ufbxi_array_type_size('r')` → 8；`UfbxProp.ValueReal`/`UfbxVec3` 等保持 double 字段。
- **`Parse/UfbxiNode.cs`**：`UfbxiNode` 用 `class`（C 的 `ufbxi_node*` 语义=引用），`children` 用 `UfbxiNode[]`。C 的 `ufbxi_value`/`array|vals` union 因读取始终受 `value_type_mask` 门控，用普通字段等价复刻。
- **值访问器**：C 的 `ufbxi_get_val_at(node, ix, fmt, void* v)` 在 C# 按格式符拆成带类型的 `GetValI/L/F/D/R/B/Z/S/s/C/c/Blob`（`node.GetValD(0, out double v)` 这种），每个都保留 C 的 Number/String 类型门与返回 0 分支；格式串 `"_I"` 之类在调用点拆成多次调用并跳过 `_`。
- **数组载荷 = 原始小端 `byte[]`**：`UfbxiValueArray.Data` + `Offset`（元素 0 的字节下标）+ `Size` + `Type`；`PAD_BEGIN` 的数组在同一段分配里把 4 个元素留在 `Offset` 之前，所以 C 的 `data[-1]` 读到 0 在 C# 里同样成立（负下标访问是刻意的，不是 bug）。字符串数组（'s'/'S'/'C'）用 `Strings` 字段（C 那里是 `ufbx_string[]`）。字节级表示是刻意的：`ufbxi_swap_endian*` 与 `binary_convert_array` 在 C 里就是按字节操作。
- **DOM 字符串按「1 char == 1 byte」存储**（Latin1 逐字节），因此 `raw_data`↔blob 转换无损（`UfbxiSanitizedString.ToBlob/FromBytes`）；`utf8_data` 可以是 `raw_data` 同一实例或 null。字符串池（`Util/`）必须遵守这一约定，否则 blob/非 ASCII 路径的哈希会漂。
- **`Parse/UfbxiParseState.cs`**：`UfbxiParseState`/`UfbxiArrayFlags`/`UfbxiArrayInfo` + `Update`/`IsArrayNode`/`IsRawString`，逐分支对应 ufbx.c:7974-8597；C 里 `info->flags = ...`（覆盖）与 `|= `（叠加）的差别已按分支保留。
- **`Parse/BinaryArray.cs`**：endian swap、`binary_convert_array`、pre-7000 多值数组解析、`push_array_data`、bool 数组后处理。C 的 `const char *val` 单字节读取会**符号扩展**（'B'/'C' 属性 → int/float 时按 `sbyte`），已按此实现。字符串数组特例（ufbx.c:8776-8806）等字符串池落地后补。
- `Parse/UfbxiContext.cs` 是 `ufbxi_context` 的瘦身版（C 的 arena/buf 不移植，见 #4），后续模块按需往里加字段，不要另建全局状态。
- **DOM blob 与场景构建共用同一块数组缓冲，C 的「复制再改 / 就地改」选择会进 DOM 摘要**（2026-10-03 由放宽后的 `tools/dom_oracle.txt` 语料实测确认，fi 32/55 的 `PointsIndex`）：`retain_dom` 时数组落在 `uc->result`，`ufbxi_retain_dom_node()` 直接把 `arr->data` 交给 `val->value_blob.data`（ufbx.c:10754），element reader 拿到的也是同一指针。于是：
  - `ufbxi_read_mesh()`（ufbx.c:13460-13466）和 legacy 路径（16196-16202）在 `retain_dom` 下 **先 `ufbxi_push_copy` 再** 执行「最后一个 index 取反」的修正（13484/16220）→ DOM 里的 `PolygonVertexIndex` 保持文件原字节。
  - `ufbxi_indexer_indices()`（ufbx.c:12707-12719）只在 `owns_indices==false` 且真的越界时才复制 → 越界修正同样不碰 DOM 缓冲。
  - `ufbxi_read_line()`（ufbx.c:13914-13948）**没有**任何复制：`line->point_indices.data = (uint32_t*)points_index->data` 之后就地 `data[i] = ix`（负数结束标记 `~(-1) == 0`、`~(-5) == 4`）并调 `ufbxi_fix_index` → **DOM 的 `PointsIndex` 是改写后的值，不是文件里的值**。
  > **规则**：移植 element reader 时必须逐点照抄 C 的「复制/不复制」判断，不能统一加复制、也不能统一就地改。凡是就地写 `arr->data` 的路径，`retain_dom` 下的 DOM blob 摘要随之改变；反之加了复制就与 golden 漂移。`tools/DomCheck` 用 `KnownDivergences` 账本记了这 6 条待场景构建落地的记录，`read_line` 一到就会以 `LEDGER STALE` 强制删除。

## C 指针序 ≡ 分配序（2026-10-02 核收 #8 时定稿，位精确的关键）

C 里有若干「按裸指针比较」的地方会**决定场景内容顺序**，因此必须可复现：

- `ufbxi_cmp_anim_prop_less`（ufbx.c:19293-19298）按 `a->element < b->element` 排 `anim_layer.anim_props`，`anim_props` 会被哈希。
- `ufbxi_sort_node_ptrs`（ufbx.c:18611/18976）**并不**按地址排：比较子是 `node_depth → parent->element_id → helper 标志 → element_id`，所以 node `typed_id` 只用 id 就能复现。

而元素指针的地址序 **等于** 创建序：`ufbxi_push_element_size`（ufbx.c:12348-12378）把元素从 `uc->tmp_elements` 这一个只增_buf 里 `push_zero` 出来（按 8 字节对齐），同时 `element_id = uc->num_elements++`。所以：

> **规则**：C 中 `ufbx_element *a < b` ⟺ `a.ElementId < b.ElementId`。任何按元素指针排序/二分（`ufbxi_find_anim_prop_start` ufbx.c:19332、`ufbx_find_anim_prop_len` 30793 等）在 C# 一律用 `ElementId`。
> 池化字符串的 `const char*` 序 ⟺ **同一个 pool 内**的字符串池 push 序（`UfbxiPtrIdTable` 按此建模：NULL=0、`ufbxi_empty_char`=1、`UfbxiStrings.All` 表序、之后按 intern 序）。以指针为键的 map（`prop_type_map`/`group_map`/`anim_stack_map`/`node_prop_set`/`dom_node_map`）**只做查找、不迭代**（全仓仅 `texture_file_map.size`、`map.size` 两处读 size），所以 .rodata 常量与堆字符串的相对地址谁先谁后不影响输出。
>
> **跨 pool 不可比**（2026-10-02 由 `tools/util_oracle.c` 实测出反例后补）：`ufbxi_buf` 的 chunk 走共享 allocator → CRT heap，请求超过 small 阈值时改从更高段分配，于是「后建的 pool 的第一个 chunk」地址可以**低于**「先建的 pool 的第二个 chunk」。实测：`S` pool 的 8128B chunk 排在 `K` pool 的 4032B chunk（分配更晚）之后。所以字符串指针的序**只在同一 pool 内**可复现，对拍oracle（`tools/util_oracle.c` 的 `GRP_*` / `UtilCheck` 的 `RankedIds[grp]`）必须按 pool 分组算 rank；任何依赖跨 pool 指针序的 C# 代码都是 bug。
>
> **`UfbxiStrings.All` 必须逐条按 ufbx.c:5583-5886 的 `ufbxi_strings[]` 表序**（不是 `ufbxi_Str_*` 的声明序，两者相邻项有互换）。该表在 `ufbxi_str_less` 下严格升序（ufbx.c:11418 在 UFBX_REGRESSION 下 assert），ufbx.c:26617-26640 靠这个升序做线性扫描把 prop_name 归一到常量，顺序错了会静默退化成堆拷贝（指针比较 `== ufbxi_*` 的分支全部失效）。`tools/UtilCheck` 的 `L`/`F string digest`/`L table strictly sorted` 三项守这条。

## C oracle：本机有 zig，可以「包含 ufbx.c」直接对拍内部函数

`zig 0.16.0` 在 PATH 上（`zig cc`）。之前「本机无 C 编译器」的假设是错的：`tools/mathvec.exe`、`test/hash_scene.exe` 就是用 `zig cc -O2 -mcpu=x86-64 -ffp-contract=off` 建的。

- **手法**：新建 `tools/xxx_oracle.c`，`#include "ufbx.c"`（ufbx.c 是单翻译单元，include 后所有 `static ufbxi_*` 函数都可直接调用），编译 `zig cc -O2 -DUFBX_NO_DEFLATE ... tools/xxx_oracle.c -o tools/xxx_oracle.exe`。
- 用途：`ufbxi_hash_string`/`_check_ascii`、`ufbxi_utf8_valid_length`、字符串池 sanitize、`ufbxi_vsnprintf`、`ufbxi_binary_parse_node`（DOM）等**没有公开入口**的层，全部可以逐位对拍，不必只靠自洽检查。
- 任何重新生成的 golden/向量仍需 `-mcpu=x86_64 -ffp-contract=off`（见「浮点语义」）。

## 哈希与 loader 的字符串/blob 口径（核收 #9 后定稿）

- `UfbxHashScene.HashString` **不做 UTF-8 重编码**：按 `ufbx_string` 的原始字节逐字符喂 `(byte)v[i]`，再喂长度（8 字节）与结尾 NUL（`length + 1` 字节，NUL 参与哈希）。这与「DOM 字符串 1 char == 1 byte」是同一套约定；`tools/HashCheck` 里的 `PortStr()` helper 展示了从显示文本到该表示的正确构造（"ä" → `0xC3 0xA4` 两个 char）。
- **loader 义务（#9 报告 G1/G2 + 模型缺口的补齐口径）**：
  - `UfbxShaderTextureInput.Prop/TextureProp/TextureEnabledProp` 是值类型 `UfbxProp`，C 的 `prop == NULL` 用 `Name == null` 表示。
  - `UfbxDomValue.ValueBlob` 在 `ARRAY_F32/ARRAY_F64` 时是紧凑小端元素数组；`ARRAY_BLOB` 时按 `[8 字节小端 size][size 字节载荷] × count` 打包（C 那里是 `ufbx_blob[]` 指针记录，byte[] 表达不了，哈希按这个编码展开）。
  - `scene.Anim`、`anim_layer/stack` 的 `.Anim`、`AnimValue.Curves`（长度 3）、`AnimLayer.ElementIdBitmask`（长度 4）、`Constraint.Constrain*`（长度 3）必须非 null，否则哈希走 NULL 分支会漂。

## 验证

- **语料位置**：golden 里的相对路径 `data/...` 实际在 `C:\Workspace\_analyze_ufbx\data`（918 个条目）；`tools/golden_hashes.txt` 共 2179 行 = 1974 `.fbx` + 195 `.obj` + 10 `.mtl`，**去重后 905 个文件**（同一文件可有多个 frame 用例）。验收口径是这 2179 个 (文件, frame) 用例，不是「1782 个文件」。对拍器需要可配置 data root，默认指向上面这个目录。
- golden: `tools/golden_hashes.txt`（C 生成，格式 `<hash16> <frame> <path>`）
- C# 对拍工具：`tests/Ufbx.Tests`（console，可 `--file x.fbx --frame N` 打印 hash，或全量跑 golden 比对）
- hash 算法必须忠实移植 `test/hash_scene.h/c`（UfbxHashScene）
- `dotnet run --project tests/Ufbx.Tests -c Release -- mathvec tools/math_vectors.txt`：数学库 72000 条向量逐位对拍（当前 72000/72000）。
- `dotnet run --project tests/Ufbx.Tests -c Release -- streamcheck`：`UfbxiStream` 状态机不变量/字节序列对拍（内存输入、流输入、prefix、skip 两条路径、read_to、Empty/Truncated/IO 错误文案、progress 窗口切分）。
- 主构建可能被其他在途模块打断时，用隔离工程验证自己的代码（模板：`tools/MathVectorCheck`、`tools/InflateCheck`，`EnableDefaultCompileItems=false` + 显式 `<Compile Include>`）。

