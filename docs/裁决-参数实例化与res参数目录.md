# 裁决：参数实例化 与 `res/params/` 参数目录（第三轮收敛）

> 日期：**2026-10-11**
> 触发（用户原话）：**「把目前参数 json 文件都放在 res 下，读取全部都从 res 下读取，然后读取到的数据
> 都是各个 spec 下的类的数据，然后 scene 的 UI 更改数据是会直接修改文件中的参数和类实例，
> 并且在使用参数的时候就从实例中使用，实例都在参数管理器中管理。」**
> 关联：`docs/裁决-参数管理器.md`（第一轮：入口唯一化）｜
> `docs/裁决-Assets分区与世界参数宿主层.md`（第二轮：按运行时切两层）｜
> `docs/architecture.md` §"世界参数"｜契约 `ArchitectureContractTests.WorldParams_AreReadOnlyByTheLogicLayerGate`

---

## 〇 裁决表

| 项 | 改动 |
|---|---|
| **参数目录** | 参数 JSON **只有这一个家**：项目根 **`res/params/`**（`data/world_params.json` ＋ `userdata/params/world_params.json` 已删） |
| **读取口径** | **一律从 `res/` 来**：内容根 = 含 `res/params/` 的那一层，磁盘分支优先（无 Godot 宿主的单测进程不碰引擎），导出产物走 `Godot.FileAccess` |
| **两个文件** | **默认档** `res/params/world_params.json`（正库，随游戏走）｜**用户档** `res/params/world_params.user.json`（可写，UI 改参数就写它；删掉 = 恢复出厂；**不入库**） |
| **spec = 类实例** | `WorldSpec` / `LandSeaSpec` / `TerrainSpec`：`readonly record struct` → **可变类**；属性初值 = **出厂档**；段属性 `init`（建档时定，生效期不换实例） |
| **唯一实例** | `World.Assets.WorldParamManager.Active`——全进程一份，**身份不变**（`Reload` / `Set` 都 `CopyTo` 就地改字段，绝不换对象） |
| **改参数** | `WorldParamManager.Set("LandSea.LandFraction", "0.5")` = **改实例 ＋ 把同一份实例整份写回用户档**（一个动作两半，写盘失败会报明"内存已改、文件没改"） |
| **用参数** | 消费方从**实例**取（`WorldGenPlanet` / 诊断读 `Active`；`LandSeaField` 持段引用现取）——不再"每次读盘造副本" |
| **取消合并** | 不再"默认档 ⊕ 用户档按字段补齐"；档**写了什么就是什么**；**缺字段 ⇒ 整份拒绝**并点名缺项（既不静默按 0、也不静默补齐） |
| **新增类型** | `World.Assets.WorldParamManager`（`scripts/Assets/Params/`，引擎面白名单同步登记） |
| **删除类型** | **`World.WorldGen.WorldSpecCodec`**（`scripts/Logic/WorldGen/Params/`，连同该目录）——JSON 的选项 / 反序列化 / 序列化 / 段兜底全部并入 `WorldParamManager`；见 §1.2 |
| **内核收成内部** | （已被 §1.2 取代）`WorldSpecCodec` 于 2026-10-11 同日一度收成 `internal`，随后按用户拍板**整份删除并并入管理器**——本条保留为过程记录 |
| **删除** | `WorldSpecCodec.Merge`（合并算法）· `WorldParamStore.Load/Save/Preset`（旧三口径）· `WorldParamStore` 的 `user://` 落点分支 · `data/world_params.json` · `userdata/params/` |
| **新增护栏** | `WorldParamManagerTests`（11 条：实例身份 / 改实例即改文件 / 残档整份拒绝 / 参数名清单 / `res/params/` 位置钉…）｜契约新增 **①c 参数目录钉**（两个文件必须在 `res/params/`，旧位置不得复活）＋ **①d 正门唯一**（`WorldSpecCodec` 必须 `internal`，见 §1.1） |
| **未做** | scene 的 UI 面板本体（本轮只做"底座"，UI 下一批）｜导出产物上的写盘真机验收（见 §六） |

**一句话**：本轮把参数从"**每次读盘得一份值**"改成"**进程里就一份实例，改它就是改世界**"，
并把文件收进 `res/params/` 一处。

---

## 1.1 追问记录：`WorldSpecCodec` 为什么没被删掉（2026-10-11 同日）

用户追问链：**「这个 WorldSpecCodec 是干什么的」→「为什么不能直接读 json 序列化和反序列化」→
「PerfBench 是什么」→（删掉 PerfBench 后）「他现在没有存在意义了吧」**。
这是本仓那型熟悉的问题（"这东西为什么存在"），故把结论落成文字。

**先把事实摆开**（全仓调用点，`grep WorldSpecCodec`）：

| 调用点 | 用途 |
|---|---|
| `WorldParamManager`（**仅 3 处**：`ReadInto` ×2、`ToJson` ×1） | 加载 / 写盘 |
| `WorldPreset`（测试薄壳） | 读**只读默认档**给用例当参照物 |
| `WorldParamManagerTests`（夹具解析 ~15 处） | 用受控 JSON 测装配 / 坏档 / 缺项规则，**不碰磁盘、不碰全局状态** |

于是"删掉它"的账是这么算的：

| 部分 | 判定 | 理由 |
|---|---|---|
| **"公开的壳"** | ✅ **该没了**（本轮已做） | 游戏代码里没有任何地方该绕过管理器直连转换；公开 = 又一个"第二入口"（与 `ApplyWorldSpec` 同型，那条已被契约 ①b 钉死） |
| **"转换 + 校验规则"** | ❌ **删不掉**（只能搬家） | `JsonSerializer` 那两行可以搬进管理器，但**选项宽容度**、**缺项预检**、**失败契约**、**`ReadInto` 的原地语义**没有一处可去掉。搬过去 ≠ 变简单，只是换文件 |
| **"无宿主进程"论据** | ⚠️ **减弱但未消失** | `PerfBench` 删除后剩 `dotnet test` 一个承担者：测试进程里调引擎 API 是**进程级崩溃**（不可捕获）⇒ 内核仍须零引擎依赖 |

**真正的取舍在测试那一格**：`WorldParamManagerTests` 现在能"拿一份完整夹具 → 断言逐字段相等"，
靠的就是内核是**独立入口**。若把 `Read/ToJson/MissingFields` 删进管理器，这些用例只剩两条路：

1. 调 `WorldParamManager.Reload()` ⇒ 每次读**真实文件**（含用户档）+ 依赖全局状态 ⇒
   用例之间互相污染（NUnit 未开并行）、改一次参数就红一片；
2. 测试里自己写 `JsonSerializer` ⇒ **两份选项、两份缺项规则** ——
   正是本仓最贵的那型错误（`WorldParamRegistry`、`WorldSpecDefaults` 都因此被删）。

**落地（两手都做，各治一头）**：

- **治"公开的壳"**：`WorldSpecCodec` 改 **`internal`**，成员一并收内部；
  契约新增 **①d 正门唯一**（`WorldSpecCodec.IsPublic == false`，并反向自检
  `WorldParamManager.IsPublic == true`）——防它哪天又被公开回去；
- **治"它凭什么值得留"**：类头写明**两个理由**（测试要一份不碰磁盘/不碰全局状态的转换口；
  文件与格式分开），并写明**失效条件**——"若哪天这两个理由都不成立（测试改用引擎内框架并自带夹具机、
  或参数改走二进制存档），本类型应当删除、把规则并进管理器，而不是留着当壳"。

⇒ 结论：**类型留着，但它从"第三个家"降级为管理器的内部实现**；用户那句"没有存在意义"里的
"当公开入口的意义"确实没了，而且是被**结构性地**去掉的（不是靠注释约定）。

### 1.2 最终收口：连"内部实现"也不要了——内核整份删除（同日）

用户接着追问：**「继续修改 WorldSpecCodec 吧，他现在没有存在意义了吧」→
「就让管理器自己读 json，去反序列化好了，反正就一些 Data，也不需要校验吧，转换是什么意思」**。

三句话各自落地：

| 用户的话 | 落地 |
|---|---|
| "就让管理器自己读 json、去反序列化" | **`WorldSpecCodec` 整份删除**（`.cs` + `.uid`），`scripts/Logic/WorldGen/Params/` 目录一并消失；选项 / 反序列化 / 序列化 / 段兜底全部搬进 `WorldParamManager`（`ReadJson` / `ReadJsonInto` / `ToJson` / `JsonOptions`） |
| "也不需要校验吧" | **逐字段校验删除**：残档不再被拒——漏写的字段（含整段缺失）落回 spec 属性初值 = **出厂档值**，不报错。只留下三类"看得见"的错仍会拦：**语法错 / 字段名拼错 / 类型不匹配**（都返回 `null` 且不动现值） |
| "转换是什么意思" | 就是 `JSON 文本 → spec 类实例` 这一步。`JsonSerializer.Deserialize` 直接干得掉，唯一留下的手尾是**"只能造新对象"**——而实例化口径要求"身份不变"，故必然多一步"拷进既有实例"（`ReadJsonInto`）。这不是另一门技术，没有替代写法 |

**删除时同步改掉的四处**（漏一处即静默失效）：

1. 契约：位置钉从"内核在 `World.WorldGen`"改为"**`scripts/Logic/WorldGen/Params/` 必须不存在**"；
   `WorldSpecCodec` 进"不得复活"名单；**④ 从"内核纯净"改钉数据层**
   （`WorldSpec` / `LandSeaSpec` / `TerrainSpec` 的引用面不得含 Godot / `System.IO` 类型）——
   内核没了，"参数数据与宿主解耦"这条得换个主体钉住，否则就白丢了；
2. `WorldPreset`（测试薄壳）改调 `WorldParamManager.CreatePresetSpec`（新增：只读默认档、
   给一份**独立实例**，不与 `Active` 共享引用）；
3. `WorldParamManagerTests`：`WorldSpecCodec.*` → `WorldParamManager.*`；
   两条旧护栏（残档整份拒绝 / 缺段报错）换成把**新语义**钉死的两条
   （`PartialFile_FillsTheGapFromTheCodeDefaults` / `MissingSection_KeepsTheFactorySection`），
   并在类头留名说明"旧护栏为什么消失"——**否则日后会被当成漏测**；
4. 三份文档（`architecture.md` / 本文件 / `WorldParamStore` 类头）同步。

**代价照实记（已写进 `WorldParamManager` 类头）**：
档里**少写一个字段**不会报错，该字段就用出厂值 ⇒ 给 spec **加字段 / 改字段名**时
**必须同步改 `res/params/` 下两个档**，否则新字段永远停在出厂值、且没有任何提示。
这是"不做校验"的直接后果，属**显式接受**的取舍。

**保留下来的一条反直觉事实**（值得记）：既然段属性带初值且 `init`，
"整段缺失"其实**不会**变成 null（`System.Text.Json` 对缺席成员不赋值）⇒
管理器里那道"段是不是 null"的兜底**正常情况下走不到**，它是防"有人把段改成可空 / 去掉初值"
的护栏；`MissingSection_KeepsTheFactorySection` 同时钉住这条不变量与那个护栏的存在理由。

---

## 一 四条要求 → 四条落地（逐条对应）

| # | 用户要求 | 落地 | 落点 |
|---|---|---|---|
| 1 | 参数 json 都放在 `res` 下，读取全部从 `res` 下读 | 两个文件进 `res/params/`；内容根判据改为"含 `res/params/` 的那一层" | `WorldParamStore`（常量 + `FindContentRoot`） |
| 2 | 读到的数据是各个 spec 下的类的数据 | 三个 spec 改**可变类**（有身份、可改）；`WorldSpecCodec.Read` 返回**实例** | `scripts/Logic/Data/Spec/*.cs` |
| 3 | scene 的 UI 改数据直接改**文件中的参数和类实例** | `Set(name, value)`：就地改实例 + 整份写回用户档；UI 只发意图，磁盘仍只由 `World.Assets` 碰 | `WorldParamManager.Set` |
| 4 | 用参数时从**实例**用，实例都在**参数管理器**里管 | 唯一实例 `Active`；消费方读它 | `WorldParamManager` / `WorldGenPlanet` / `WorldGenReadoutDiag` |

**第 3 条的分工复述**（与第一轮"场景层不碰磁盘"不冲突）：
用户要的是"UI 改数据 ⇒ 文件与实例**都变**"，不是"UI 自己写文件"。
故 `Set` 一个方法同时做两半，scene 层只调它——**scnee 层零 `System.IO`、零 Godot 文件 API**，
契约 ②/③ 继续钉着（管理器虽进引擎面白名单，但自己碰 `System.IO` 会被 ② 当场点名）。

---

## 二 实例语义（本轮的核心）

### 2.1 为什么必须"有身份"

旧的 `readonly record struct` 是**值**：`Run(spec)` 传进去的是拷贝，`LandSeaField` 存下的也是拷贝。
于是"改一个参数"只能靠"**重新读盘 → 造一份新 spec → 重新生成**"——UI 想改一个数，就得触碰整条链。

改成类之后：

```
WorldParamManager.Active          ← 唯一实例（进程内一份）
   ├── LandSea : LandSeaSpec      ← 段实例也唯一（引用）
   ├── Terrain : TerrainSpec
   └── Seed    : int
        ↑ 持引用者（LandSeaField._spec / 将来的 UI 面板行）现取即最新值
```

- **`Reload()` 就地重填**（`CopyTo` 逐字段写进同一个对象）⇒ 重新读盘不会让持引用者看到旧值；
- **`Set()` 就地改一个属性** ⇒ 已经拿到段引用的消费方立刻看到新值（`ParameterChange_IsVisibleToHoldersOfTheSameInstance` 钉住）；
- **段整体不可换**（`LandSea` / `Terrain` 是 `init`）：换段 = 持引用者看成旧值，属隐蔽 bug，故从类型上堵死。

### 2.2 加载顺序（"出厂档"这一层是新的）

```
出厂档实例值（spec 属性初值）           ← 基底，恒在；文件全丢也不会静默变全 0
   ↓  res/params/world_params.json      （读不到 ⇒ 保留出厂值 + 报问题）
   ↓  res/params/world_params.user.json （存在 ⇒ 整份覆盖）
```

★属性初值**不是"顺手给个初值"**：它同时承担"默认档缺失时的世界"与"残档为何不能按 0 装载"两件事
（见 §三）。逐值守卫仍在 `WorldSpecTests`（13 个字段逐字钉住）。

---

## 三 取消合并 ⇒ "缺字段"怎么处理（本轮唯一的语义取舍）

用户选了"**直接反序列化，不补齐**"。落地时撞到一个必须处理的细节：

> `System.Text.Json` 对**缺席**的成员**不赋值** ⇒ 属性保留**属性初值**（= 出厂档值）。
> 也就是说"缺字段 = 0"**根本不会发生**，实际会变成"缺字段 = 出厂值"——**第三种说不清的语义**，
> 而且它碰巧看起来很正常（世界照跑、数值合理）⇒ 属最坏的一类静默。

三个选项摆开：

| 选项 | 行为 | 判定 |
|---|---|---|
| A 依赖库行为 | 缺字段 ⇒ 静默保留出厂值 | ❌ 等于"半个合并"，且不可见 |
| B 补零装载 | 缺字段 ⇒ 0（世界静默变样：陆占比 0、区域粒度 0） | ❌ 第一轮就定为"最坏失败模式" |
| **C 整份拒绝** | 缺字段 ⇒ **报出缺了哪些字段、现值一个都不动** | ✅ 采用 |

**C 的口径**：档要么写全（管理器写出来的恒为整份）要么不装载；
报错点名到 `LandSea.WarpWavelengthKm` 这一级，用户知道该补什么。
这与第一轮"坏数据不静默"是同一条原则，也避免"补齐"从后门回来（`PartialFile_IsRejectedWholesale_MissingFieldsNamed` 钉住）。

---

## 四 路径与读写规则

```
内容根 = 从 AppContext.BaseDirectory 向上 ≤12 层、找到含 res/params/ 的那一层

默认档：磁盘（内容根）──命中⇒ System.IO
              └─未命中（导出：内容在 .pck 内）⇒ Godot FileAccess 读 res://res/params/world_params.json
用户档：磁盘优先；写失败（导出产物里 res:// 只读）⇒ 报问题，**不回滚内存**（用户的意图已表达）
```

- **磁盘优先**保证**没有 Godot 宿主的那个进程**（`dotnet test`；测试进程里调引擎 API 是进程级崩溃）
  连一次引擎调用都不发生——这是第二轮切分的可验证后果，本轮不变；
  ⚠️ 2026-10-11 同日：原第二个无宿主进程 `scripts/Test/PerfBench`（性能基线工具，
  它也必须读真档才能量对参数）**已按用户拍板删除**（"之后重新写性能测试"）⇒ 该论据现由单测单独承担。
  这也解释了为什么"内核零引擎"这条约束不能靠"单测改用引擎内框架（如 GdUnit4）"来解除：
  只要还有一个**非 Godot 宿主**、又要读参数档的进程，编解码就必须留在无引擎依赖的那一层；
- 引擎调用仍全部封在 `[MethodImpl(NoInlining)] + try/catch` 的逃生门里（与 `H3Native` 找 native dll 同型）；
- `ContentRootMaxUpLevels = 12` 是**显式常量**（第二轮遗留的 `i < 10` 隐式契约顺手收口）。

---

## 五 验收（2026-10-11 实测）

```
dotnet build world.csproj                              → 0 警告 0 错误
dotnet test scripts/Test/World.Tests                   → 308 PASS / 0 FAIL（基线 298 全绿；净增 10 条）
Godot --headless --path . --import                     → 生成 WorldParamManager.cs.uid，无 SCRIPT ERROR
Godot --headless --path . res://scenes/diag/WorldGenReadoutDiag.tscn
                                                       → [WORLDGEN-PARAMS] 零问题；[WORLDGEN-TIMING] 逐字段同基线
```

**变异测试（证明护栏非假绿）**：把 `res/params/world_params.json` 挖掉一个字段 ⇒
`WorldSpecTests` / `PartialFile_IsRejectedWholesale_MissingFieldsNamed` 当场红并点名该字段；
把 `userdata/params/` 建回来 ⇒ 契约 **①c** 当场红。

---

## 六 代价与遗留（照实记）

1. ⚠️ **导出产物里用户档写不出去**：`res://` 在 `.pck` 内只读 ⇒ UI 改参数只能"内存生效、重启失效"。
   本轮**不让它静默**（`Set` 把失败原因作为问题返回），但"导出后仍能存盘"需要另开可写落点
   （`user://` 或存档），**不在本轮范围**；
2. ⚠️ **用户档写出来是纯 JSON、不带注释**：序列化整份实例会抹掉手写的 `//` 说明
   （文件头注释只在"没被管理器写过"之前存在；用户档已 `gitignore`，属运行产物）；
3. **scene 的 UI 面板本体未做**（用户确认"先只做前四件事"）：`Set` / `ParameterNames` 已备好口子，
   面板下一批接入；
4. **`WorldPreset.Earth` 是"独立副本"而非 `Active`**：测试要拿它改字段做用例，
   读 `Active` 会被上一条用例的写盘污染 ⇒ 有意分离；
5. **`res/params/world_params.user.json` 不入库**（`.gitignore`）：它是 UI / 管理器的写入目标，
   入库会让每次调参都变成噪声提交；默认档入库、且"出厂档实例值"在代码里兜底 ⇒ 删掉它无损失。

### 6.1 同日追加：`scripts/Test/PerfBench` 已删除（用户拍板）

用户原话：「**删掉吧，我之后重新写性能测试**」。已删：`Program.cs` ＋ `Program.cs.uid` ＋
`PerfBench.csproj`（+ `bin/`、`obj/`），并清掉 `world.csproj` 里的两条排除项。

| 项 | 处置 |
|---|---|
| 工具本体 | 删除（唯一受影响的"消费者"是它自己；它**不在** `world.sln`、**不在** `verify.sh`、无测试引用它） |
| 它的四个模式 | `--probe`（H3 尺度探针）/ `--converge`（分辨率收敛）/ `--d11`（水文逐层）/ `--p42*` 系列（历史诊断）**一并消失** |
| 历史读数 | 留在 `docs/newdecision/设计-世界生成空间-07-…md` §4 / §4.5 / §13.13 与 `docs/审查报告-C2-ParallelLoops取证.md` 里（**是记录，不是活工具**） |
| res4 收敛性判读 | 原由 `PerfBench --converge 1 2 3 4` 人工判读 ⇒ 现在**只剩 `ResolutionConvergenceTests` 的 res1–res3 自动断言**（已在该测试里留注：新工具出来后接回，判据口径 = 物理阈值 70,800 km² 跨 res 不变） |
| 对参数链的影响 | `WorldSpecCodec` 的"无宿主进程"论据**减少一个承担者**（现由 `dotnet test` 单独承担），**结论不变**：编解码仍须留在零引擎依赖的那一层，否则"还有一个非 Godot 宿主、又要读参数档的进程"这条就会重新成立 |

⚠️ 重写性能测试时的两条纪律（沿自被删工具的既有约定，别丢）：**只读**（不写文件、不改生成逻辑、
不触碰 `GD.*` / `LogService`）、**不掺引擎**（否则毫秒信号里混进引擎初始化与主循环噪声，
res 档位对比就失去意义）。

---

## 七 与既有裁决的关系

| 裁决 | 状态 |
|---|---|
| `裁决-参数管理器.md`（2026-10-10 第一轮） | **部分被本轮延伸**：入口唯一化 / 存储类 = spec 自身 / 坏数据不静默 **继续有效**；"默认档 ⊕ 玩家档补齐""`userdata/params/` 玩家档"**已被本轮取代** |
| `裁决-Assets分区与世界参数宿主层.md`（2026-10-10 第二轮） | **部分被本轮延伸**：两层切分（宿主 I/O ＋ 纯函数内核）**继续有效**；"内容根判据 = `data/world_params.json`""`user://` 导出落点"**已被本轮取代**，并新增第三层"参数管理器（实例）" |
| 契约 `WorldParams_AreReadOnlyByTheLogicLayerGate` | **本轮显式改契约**：新增 ①c 参数目录钉 ＋ ①d 正门唯一（参数链公开面只有管理器）；**④ 由"内核纯净"改钉数据层**（内核已删，§1.2）；引擎面白名单登记 `WorldParamManager`；位置钉改"`scripts/Logic/WorldGen/Params/` 必须不存在"；`WorldSpecCodec` 进删名名单；②/③/⑤ 不变 |
