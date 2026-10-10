# 裁决：`World.Assets` 分区 与 世界参数宿主层

> 日期：2026-10-10（同日第二轮 —— 接在 `docs/裁决-参数管理器.md` 之后）
> 触发：用户追问「`FindRoot` 为什么不是从游戏根目录去找，而是向上找」⇒ 结论是**导出这一格绕不开 Godot**，
> 于是用户拍板：「那就是说还必须要依赖 godot 了，那你把这部分内容放在新目录下吧，和 logic 和 scene 并列，
> 把参数的引入引出修改都写到新目录中，然后还要把 json 放在 res 下」。
>
> ⚠️ **后续延伸（2026-10-11，第三轮）**：用户把这句"**json 放在 res 下**"落到了实处——
> 参数 JSON 现居**项目根 `res/params/`**（`world_params.json` ＋ `world_params.user.json`），
> 读取一律从 `res/` 来；同时三个 spec 改为**可变类实例**、新增
> **`World.Assets.WorldParamManager`**（参数管理器，持有唯一实例 ＋ 改参数＝改实例＋改文件），
> 并**取消**本文件的"默认档 ⊕ 玩家档合并"与"`user://` 导出落点"。**本文件的历史内容不改写**；
> 现状见 **`docs/裁决-参数实例化与res参数目录.md`**：
>   · §4 路径选择规则（`data/world_params.json` 判据 / `user://` 落点）→ 已改成 `res/params/` 一条口径；
>   · §5 契约六条 → 现为"位置 ＋ **参数目录** ＋ 第二入口 ＋ 磁盘白名单 ＋ 引擎面唯一 ＋ 内核纯净 ＋ `[Export]`"，
>     且引擎面白名单新增 `WorldParamManager`；
>   · §6 导出收编的 `include_filter` → 已改为 `res/params/*.json`。
> **继续有效的部分**：§2 两层切分（宿主 I/O ＋ 纯函数内核）、§3 为什么内核留一半在 Logic、
> "磁盘优先保证单测 / PerfBench 零引擎调用"这条纪律。

---

## 1. 判定问题

原设计把"找文件 + 读文件 + 编解码"整块放在 `World.WorldGen.WorldParamTable`，靠 `System.IO`
从程序集位置**向上找内容根**来**回避 Godot API**。这个回避在三个格子里成立，在第四个格子里不成立：

| 宿主 | 程序集位置 | `System.IO` 向上找 | 结果 |
|---|---|---|---|
| 编辑器 / headless | `.godot/mono/temp/bin/**` | 命中**项目根**（它**就是** `res://` 的磁盘路径） | ✅ |
| 单测 / `PerfBench` | `scripts/Test/*/bin/Debug/net8.0` | 命中**仓库根** | ✅（且全程零引擎 API） |
| 导出（`data/` 放 exe 旁） | exe 目录 | 第 0 层命中 | ✅（依赖导出流程，见 §6） |
| **导出（资源进 `.pck`）** | exe 目录 | **找不到** | ❌ —— `res://` 已不是普通目录，**只有 Godot `FileAccess` 读得到** |

**"必须允许碰 Godot"这件事是真实存在的，不是兜底凑数。** 于是问题变成：把这份引擎依赖放哪。

## 2. 决定

**新开一个与 `Scene` / `Logic` 并列的顶层分区 `scripts/Assets/`（`World.Assets`）**，
按**依赖的运行时**把原 `WorldParamTable` 切成两半：

| 层 | 类型 | 目录 | 依赖的运行时 | 职责 |
|---|---|---|---|---|
| **宿主 I/O** | `World.Assets.WorldParamStore` | `scripts/Assets/Params/` | **允许 Godot** | 内容根解析 / 读 / 写 / 首跑拷贝 / 引出落盘 |
| **纯函数内核** | `World.WorldGen.WorldSpecCodec` | `scripts/Logic/WorldGen/Params/` | **零磁盘零引擎** | `JSON 文本 ⇄ WorldSpec`（`Merge` / `ToJson`） |

- 依赖**单向** `World.Assets → World.WorldGen`；逻辑层**不得**反向引用 `World.Assets`。
- 分区名不进命名空间（与 `Scene` / `Logic` 同规则）；`Params/` 是自由分组子目录。
- 原类型 `WorldParamTable` **删除**（不保留 alias）——两个家比一个旧名清楚。

## 3. 为什么内核必须留一半在 Logic

因为**没有引擎宿主的进程有两个，而且它们是"实测 > 推断"的地基**：

- `dotnet test`（298 条）——测试进程里**引擎调用是进程级崩溃**，不可捕获；
- `PerfBench`（性能基线）——普通 console，同样无宿主。

若把编解码也搬进 `World.Assets`，这两处要么改成"内联 JSON 夹具"（⇒ 再也不验证真实默认档），
要么就得在测试进程里碰 Godot。两者都是功能回退。切两半之后：

- 合并算法（缺省补齐 / 精度 / 坏数据不静默）**在没有宿主的进程里被完整覆盖**，跑的却是游戏里同一条路径；
- 引擎相关的只剩"文件在哪、能不能写"，而那部分现在有了**唯一**的家。

这也是本裁决**唯一**新增的架构约束：**引擎面（`ProjectSettings` / `FileAccess`）在参数链上只准出现在
`WorldParamStore` 一处**——由契约 `WorldParams_AreReadOnlyByTheLogicLayerGate` ③ 钉住。

## 4. 路径选择规则（**顺序即设计**）

```
内容根 = 从 AppContext.BaseDirectory 向上 ≤10 层、找到含 data/world_params.json 的那一层

默认档：  磁盘（内容根）────────── 命中 ⇒ System.IO
                └─ 未命中（⇒ 内容在 .pck 内）⇒ Godot FileAccess 读 res://data/world_params.json
玩家档：  内容根可用 ⇒ <内容根>/userdata/params/world_params.json      （现状；编辑器/headless 可写）
          内容根不可用 ⇒ user://params/world_params.json                （导出：res:// 只读，必须换可写位）
```

**磁盘优先**这一条不只是"更好"——它保证了**单测与 `PerfBench` 连一次引擎调用都不会发生**
（它们的磁盘分支必然命中）。引擎调用全部封在 `[MethodImpl(NoInlining)]` + `try/catch` 的"逃生门"方法里，
与 `World.Utils.H3.H3Native` 找 native dll 的做法同型。

## 5. 契约（`WorldParams_AreReadOnlyByTheLogicLayerGate`，六条，各带非空转自检）

| # | 钉什么 | 断言形态 |
|---|---|---|
| ① | **位置** | `WorldParamStore` ∈ `World.Assets` 且 `scripts/Assets/Params/` 存在；`WorldSpecCodec` ∈ `World.WorldGen` 且 `scripts/Logic/WorldGen/Params/` 存在；旧名 `WorldParamTable` 不得复活 |
| ①b | **第二入口** | `WorldGenPlanet.ApplyWorldSpec` / `_specOverride` 不得复活（存档恢复世界 = 写玩家档再 `Load`） |
| ② | **磁盘白名单** | 全程序集里深度引用 `System.IO` 的类型只准 `WorldParamStore` / `H3Native` / `PngWriter` |
| ③ | **引擎面唯一** | 深度引用 `ProjectSettings` / `FileAccess` 的类型只准 `WorldParamStore` / `H3Native` |
| ④ | **内核纯净** | `WorldSpecCodec` 的引用面**不得**含 `System.IO` 类型、不得含 Godot 类型 |
| ⑤ | **`[Export]` 已清** | `WorldGenPlanet` 上不得再有 4 个世界参数 `[Export]`（`ResLevel`/`Radius` 是构造参数，保留） |

**变异测试（证明非假绿，2026-10-10 实做）**：
· 给 `WorldSpecCodec.ToJson` 注入 `System.IO.File.Exists("x")` ⇒ ② 当场红（点名 `World.WorldGen.WorldSpecCodec`）；
· 给 `WorldGenPlanet.BuildSpec` 注入 `Godot.ProjectSettings.GlobalizePath("res://")` ⇒ ③ 当场红（点名 `World.WorldGen.WorldGenPlanet`）；
· 两处探针均已还原并 `grep` 校验无残留，还原后全量 298 条复绿。

> ④ 与 ②/③ 有重叠，但**不是冗余**：②/③ 是"只准这些类型"（可扩白名单），
> ④ 是"**这个类型永远不准**"（不可扩）。前者是策略，后者是内核之所以为内核的定义。

## 6. 导出收编（`export_presets.cfg`）

新增 Windows Desktop 预设，`export_filter="all_resources"` + `include_filter="data/world_params.json"`。

- **为什么不把它做成 Godot 资源**：`.json` 一旦被 Godot 的 importer 收编，`res://data/world_params.json`
  会被重映射成 `.res` ⇒ `FileAccess.Open` 那条路会**失效**。故刻意让它保持**非资源配置**（无 `.import`），
  靠 `include_filter` 进 `.pck`。
- **实测**：`--export-release "Windows Desktop"` 已被 Godot 识别为**合法预设**
  （报错只指向"本机未安装导出模板 `4.7.1.stable.mono/windows_{debug,release}_x86_64.exe`"），
  即配置本身无语法 / 字段错误。

## 7. 代价与遗留（**照实记**）

1. ⚠️ **导出仍是"配置就绪、未真机验收"**：本机没装导出模板 ⇒ `FileAccess` 读 `.pck` 内默认档这条路径
   **没有被跑通过一次**。这是本次唯一未闭环的断言，与 `docs/裁决-参数管理器.md` §八.4 同源。
2. ⚠️ **`Save(spec)` 当前零消费者**：它是"引出 / 修改"的入口（用户点名要的那部分），
   真实消费者在**存档（B0 之后）**。这是**有意**接受的一处提前建面——判断依据是它不是一个结构
   （不是注册表 / 描述符 / 字典），而是一个已经确定语义的方法；且没有它，"世界参数表存储"就名不副实。
   若后续 B0 都不需要它，应当删除而不是留着占位。
3. ⚠️ **`i < 10` 仍是隐式契约**：内容根解析的上界没有守卫（目录再搬深即饿死，前车之鉴
   `docs/裁决-Test第三分区.md` §5）。本次未顺手加钉——加"深度断言"需要额外引入"当前层级"这个事实，
   属另一件事，不在本次范围。
4. **测试从"纯内存"变成"读磁盘"**（沿用原设计的既有代价）：`WorldPreset` / `PerfBench` / 契约正向钉
   都要读 `data/world_params.json`。缓解仍是"向上找内容根"。
5. **`World.Assets` 目前只有一个成员**：不做"预留目录树"，等第二个真实消费者（存档 / 翻译表 / 素材池）
   落地时再分组。

## 8. 连带改动清单（漏一处即静默失败）

| # | 项 | 处理 |
|---|---|---|
| 1 | `WorldGenPlanet.BuildSpec` | `WorldParamTable.Load` → `WorldParamStore.Load`（+ `using World.Assets;`） |
| 2 | `WorldGenReadoutDiag` | 同上（诊断场景是 headless 验收的入口） |
| 3 | `WorldPreset`（测试薄壳） | → `WorldParamStore.Preset` |
| 4 | `WorldParamTableTests` | **改名** `WorldParamStoreTests`（`.cs` + `.cs.uid` **成对** `git mv`）；纯函数用例改调 `WorldSpecCodec` |
| 5 | `PerfBench/Program.cs` | → `WorldParamStore.Preset`（无宿主 ⇒ 走磁盘分支） |
| 6 | `ArchitectureContractTests` | 六条钉 + `NewWorldLineTypes` 里 `WorldParamTable` → `WorldSpecCodec` + `using World.Assets;` |
| 7 | `.cs.uid` | 两个新脚本须 `--headless --import` 生成并入库 |
| 8 | `docs/architecture.md` §9 | 命名空间清单加 `World.Assets`；参数入口段落改写为两层 |
| 9 | 旧文档 | `裁决-参数管理器.md` 顶部加"已被本轮延伸"指引（**不改写历史**） |

## 9. 实测证据（2026-10-10）

```bash
dotnet build                                   # 0 警告 0 错误
dotnet test scripts/Test/World.Tests/…         # 298 通过 / 0 失败
dotnet build scripts/Test/PerfBench/…          # 通过（不在 sln，须手工）
dotnet scripts/Test/PerfBench/bin/Debug/net8.0/PerfBench.dll 1   # res1 全阶段跑通 ⇒ 无宿主读档正常
bash scripts/verify.sh --fast                  # 构建 + 单测 + 主世界 + 世界生成读数 全绿
#   [WORLDGEN-TIMING] n=288122 res=4 land=29.0% regions=95 … total=2496ms   （与切分前基线一致）
```
