# 裁决：`scripts/` 两分区（`Scene/` + `Logic/`）

**日期**：2026-10-09 · **性质**：目录结构治理（零功能变更） · **裁决人**：用户

---

## 1. 裁决

`scripts/` 之下建立**两个顶层分区**：

| 分区 | 含义 | 实测文件数 |
|---|---|---|
| `scripts/Scene/` | **需要 Godot 节点生命周期 / 挂进场景树**的代码 | **39** |
| `scripts/Logic/` | 其余全部（可在无 Godot 宿主下被驱动的领域代码） | **123** |

**依赖方向 = 单侧**：`Scene → Logic` 允许；`Logic → Scene` **禁止**。

---

## 2. 判定规则（唯一一条）

> **这个文件需要 Godot 的节点生命周期吗？需要 ⇒ `Scene/`；不需要 ⇒ `Logic/`。**

推论：
- `Mathf` / `Vector3` / `Color` 等**纯值类型与纯函数库不算"需要节点生命周期"** ⇒ 用了它们不改变归属（它们属"逻辑侧的引擎 API 残留"，由后续"去引擎依赖"工作清理，不由本次目录治理解决）。
- `LogService` / `UserPaths` / `FileAccess` 这类**引擎绑定但被逻辑侧消费**的基础设施 ⇒ 判 `Logic/`——否则会出现 `Logic → Scene` 反向边。**这是本规则唯一的硬约束来源。**

---

## 3. 归属矩阵

| 现路径 | 文件 | 归向 | 依据 |
|---|---|---|---|
| `Camera/` | 1 | `Scene/` | `OrbitalCamera : Node3D` |
| `Render/` | 16 | `Scene/` | `BallView : Node3D`、`MapDock`/`CellInfoCard : Control`、模式/叠加层/色板 |
| `Diagnostics/`（除 `PerfLog.cs`） | 20 | `Scene/` | 全部是诊断场景脚本（`Node` 派生） |
| `WorldGen/Composition/` | 3 | **拆** | `WorldGenPlanet`/`WorldGenManager : Node3D` → `Scene/`；`WorldGenSimulation`（纯逻辑入口）→ `Logic/` |
| `Diagnostics/PerfLog.cs` | 1 | `Logic/` | 被 `CivEngine`（逻辑）消费 ⇒ 判 Scene 会造出反向边 |
| `CivSim/` | 62 | `Logic/` | 演化逻辑 |
| `WorldGen/`（余下五子层） | 29 | `Logic/` | 生成逻辑 |
| `Utils/` | 9 | `Logic/` | 工具层（`ColorRamp`/`PngWriter` 的 `Color`/`FileAccess` 用法属引擎 API 残留） |
| `Domain/` | 5 | `Logic/` | 领域词汇与领域模拟 |
| `LogicGrid/` `HexPlanet/` | 3 + 2 | `Logic/` | 网格几何与存档 |
| `Spatial/` `Planet/` `Gameplay/` `Archive/` `Services/` | 2 + 2 + 3 + 2 + 2 | `Logic/` | 球壳数据层 / 常数 / 命令层 / 持久化 / 通用服务 |

> ⚠️ **这不是"搬目录"，而是"按文件重排"**：14 个一级目录里有 6 个横跨两侧（`WorldGen`、`Utils`、`Domain`、`Archive`、`Diagnostics`、`LogicGrid`）。
>
> ⚠️ **2026-10-11 后续变更**：本表的"两分区"（`Scene` ↔ `Logic`）已被
> **`重整方案-按架构分层.md` 的八层**取代 —— 表内 `Utils/` 那行（"归 `Logic/`"）作废：
> `Utils/` 现为**与 `Logic/` 平级的顶层**；`Logic/Data|Constant` 已独立成 `Data/`。
> 另：`Utils/` 的 Godot 边界也已放开（可用类型、禁运行期调用），见重整方案 §二。

---

## 4. 命名空间策略：中性容器（**零 namespace 改动**）

`scripts/{Scene,Logic}/` **不参与 namespace**（作用等同多数工程的 `src/`）。其下路径仍**逐字对应** `World.<领域>`：

```
scripts/Logic/WorldGen/Simulation/H3Hydrology.cs   →  namespace World.WorldGen
scripts/Scene/Render/UI/MapDock.cs                 →  namespace World.Render
```

理由：改名会连带撼动一批按命名空间字符串匹配的架构契约
（`FinalWorldFacts_OnlyConsumedByWorldGen` 的 `ns ∈ World.WorldGen*`、
`CivSim_DoesNotReferenceWorldGen` 的 `World.CivSim*`、
`NewWorldLine_MayDependOnApprovedRenderContracts` 的 `World.Render` 白名单、
`NewWorldLineTypes` 登记表等），成本高一个数量级且无对应收益。

**代价（已知并接受）**：`World.WorldGen` 与 `World.Diagnostics` 两个命名空间**横跨两侧**（逻辑侧 + 装配/诊断侧）。这是中性命名的必然结果，非缺陷。

---

## 5. 三处连带项（漏一处即静默失败）

1. **`.tscn` 脚本路径**：16 个场景用 `path="res://scripts/..."`（无 `uid=` 形式）。路径错时 **C# 编译与 523 条单测全绿**，Godot 只是丢脚本 —— **必须 `--headless` 实机验收**。
2. **`.cs` + `.cs.uid` 成对搬**：uid 由 Godot 生成，拆开或丢失即断引用；搬后须 `--headless --import` 刷新 `uid_cache.bin`。
3. **测试里的源码路径断言**（2 处）：
   - `ArchitectureContractTests.FindRepoDir("scripts", "Logic", "WorldGen")`（目录↔namespace 一致性钉）
   - `CellHighlightRingTests.FindRenderSources()`（radial-lift 禁令的源码扫描目标）

> 本次执行教训：`git mv`/`mv` 目录级操作在 **Godot 编辑器运行时**会被子目录句柄挡住（`Permission denied`），需退化为文件级移动 + 重试。

---

## 6. 验证证据（全绿）

| 检查 | 结果 |
|---|---|
| `.cs` / `.cs.uid` 数量 | 162 / 162，**全部配对**，uid **唯一且格式合法**（0 非法） |
| 与 `git HEAD` 文件集合对账 | 161 → 162，**零丢失**；新增仅 `WorldGenSimulation.cs` |
| `Scene/` 引用的 16 条脚本路径 | 逐条存在性校验 **16/16 ok** |
| `dotnet build` | 0 警告 0 错误 |
| `dotnet test tests/World.Tests` | **523 PASS / 0 失败** |
| `tests/PerfBench`（不在 sln，手工构建） | 0 错误 |
| `--headless` 实机主场景 | **零 ERROR**，`[WORLDGEN-TIMING]` 读数与历史基线**逐字段一致**（`n=288122 res=4 land=29.0% regions=95 meanP=1098 meanT=76.0C ridges=71 basins=6971 lakes=3439`） |
| 跨侧依赖方向 | `Scene → Logic` **242** 处 / `Logic → Scene` **0** 处 |

---

## 7. 未做 / 后续

- **本次不产生编译期隔离**：`Logic/` 内仍有 53 个文件使用 Godot API（`Mathf` 34 / `Godot.Color` 一批 / `FileAccess` 4）。目录治理买到的是**边界可见**；真正的强制隔离需要"去 `Vector3` + `Mathf`"（对应本日讨论的路线 C，用户未选）。
- **尚无契约测试钉住 `Logic → Scene` 禁令**（当前实测为 0）。可行实现：磁盘扫描 `scripts/Logic/**/*.cs` 的 `using`/全限定引用，对照 `scripts/Scene/**` 的命名空间集合。建议作为下一步独立变更（原则 13：变更边界不同 ⇒ 分开 commit）。
