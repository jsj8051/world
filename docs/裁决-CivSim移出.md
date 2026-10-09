# 裁决 · CivSim 移出程序集（等待正确时机回归）

> 2026-10-09。**用户拍板**：「将 civsim 移出去，等待正确时机回归，现在不需要。」

## 0 一句话

把 CivSim 及其**直接依赖**从编译面移出（移入工程内 `scripts/_removed/`，**不删除**），
使世界生成线可以独立演进；CivSim 的回归时机由用户决定，回归操作已固化为可执行清单。

## 1 决策与三选项

| 选项 | 内容 | 结果 |
|---|---|---|
| 移出方式 | ①`git rm`（靠历史保留）②移到工程外目录 ③**移到工程内 + csproj 排除** | **③ 采纳** |
| 移出范围 | ①**CivSim + 直接依赖** ②再含 Legacy 支撑簇 ③只移 CivSim 本目录 | **① 采纳** |
| 前置提交 | ①**先提交分区重构** ②一起改 | **① 采纳** |

- 「移到工程内 + csproj 排除」= 文件留在仓库、结构原样、只是不进编译 ⇒ **可逆性最高**（移回原位即可），
  代价是工程树里留一处 `_removed/`（由 README + csproj 注释显式标注，不会成为"隐形死代码"）。
- 未采纳 ②（含 Legacy 支撑簇）的理由：`GameGrid` / `WildCropsSystem` / `MapData` / `FieldCodec` /
  `PerfLog` / `HexPlanet` 目前**仍有非 CivSim 引用面**（测试 + `GameGrid` 自身往返方法），
  且它们属"自然层/基础设施"语义，不在"CivSim"名义范围内 ⇒ 清退不范围膨胀。

## 2 依赖闭包（实测，2026-10-09）

grep 口径：`World.CivSim` / `CivEngine` / `CivSimResult` / `CivSimContext` / `Polity` / `Habitation` /
`CivOverlay` / `CivSnapshot` / `TechTable` / `CommodityTable` / `CapabilityTable` / `ConceptRegistry`。

### 2.1 移出（本次实际执行）

| 原位置 | 现位置 | 体量 |
|---|---|---|
| `scripts/Logic/CivSim/` | `scripts/_removed/Logic/CivSim/` | **60 .cs** |
| `scripts/Logic/Gameplay/` | `scripts/_removed/Logic/Gameplay/` | **3 .cs**（PlayerCommand / PlayerCommands / PlayerSession） |
| `scripts/Test/Diagnostics/CivSimDiag{,.Builders,.Scenarios}.cs` | `scripts/_removed/Test/Diagnostics/` | **3 .cs** |
| `scenes/diag/CivSimDiag.tscn` | `scripts/_removed/scenes/diag/` | 1 tscn |
| 9 个 CivSim 单测（`CivEvent` / `CivOverlay` / `CivSimMechanic` / `CivSimMechanics2` / `CivSimModel` / `CivSimWar` / `ConceptRegistry` / `PlayerCommands` / `StateMechanism`） | `scripts/_removed/Test/World.Tests/` | **9 .cs** |

合计 **75 个 .cs**（+ 配对 `.cs.uid`）。

**为什么 `Gameplay` 属于 CivSim**：`PlayerCommands` / `PlayerSession` 直接 `using World.CivSim`，
且唯一消费者是 `CivEngine`（`CivEngine.cs:131 PlayerCommands.ApplyPending(ctx)`；
`CivSimContext.cs:99 public World.Gameplay.PlayerSession Player;`）——移除 CivSim 后它零消费者。

### 2.2 留在原位（有非 CivSim 引用面）

`Logic/LogicGrid/GameGrid.cs`（CivSim 自然输入，但其 `FromMapData`/`ToMapData` 往返方法被单测使用）、
`Logic/Domain/WildCropsSystem.cs`、`Logic/Archive/{MapData,FieldCodec}.cs`、
`Logic/Diagnostics/PerfLog.cs`、`Logic/HexPlanet/`。

### 2.3 仅注释提及（不动）

`Domain/Calendar.cs`、`WorldGen/Simulation/PrecipitationModel.cs`、`Scene/Render/Modes/WorldGenMapModes.cs`、
`Test/Diagnostics/DiagSceneBase.cs`。

### 2.4 影响面：**对世界生成本体为零**

全仓唯一引用 CivSim 的场景 = `scenes/diag/CivSimDiag.tscn`；**没有任何生产场景接线**，
主场景 `scenes/core/WorldGenWorld.tscn` 与 CivSim 无交集。

## 3 落地改动（连带项清单）

1. **`world.csproj`**：新增 `<Compile Remove="scripts/_removed/**/*.cs" />` + `<None Remove="scripts/_removed/**" />`
   （附注释说明"移出 ≠ 删除"与回归操作）。
2. **`Test/World.Tests/ArchitectureContractTests.cs`**：
   - `RetainedDomainConsumers_OnlyDependOnAllowedNamespaces`：白名单删 `"World.CivSim"` / `"World.Gameplay"`；
     被扫消费者删 `typeof(World.CivSim.CivSimContext)`。
   - `CivSim_DoesNotReferenceWorldGen`（方向守卫，全程序集 + IL 深度）→ 改写为
     **`CivSimAndGameplay_AreMovedOut`**（结果钉 + 位置钉）：程序集内不得再有
     `World.CivSim*` / `World.Gameplay*` 类型；原目录须不存在、`scripts/_removed/Logic/CivSim` 须存在。
     理由：被测类型移出后方向守卫无从扫描（`Assert.That(civTypes, Is.Not.Empty)` 会直接失败）。
   - 文档注释同步（⑥ 条目、`MapData`/`FinalWorldFacts` 段落里对 CivSim 的提及）。
   - `ReferencedTypesDeep` / `IlRefs` / `OperandTokens` / `OpCodeMap` 这套 IL 深度扫描**有意保留**
     （当前无契约使用）——待办「Logic ⟂ Scene」契约可直接复用；已在其注释中显式标注。
3. **死 `using` 清理**（三处只剩 using、无类型使用）：
   `DeterministicRandomTests.cs`（测的是 `World.Utils.DeterministicRandom`）、
   `BiomeTests.cs`、`LogicGridTests.cs`。
4. **`Logic/Archive/MapData.cs`**：删 `public World.CivSim.CivSimResult Civilization;`——
   该字段**全仓零读零写**（grep `.Civilization` 无命中），且类型随 CivSim 移出。
5. **`scripts/verify.sh`**：移除 `CivSimDiag(构造场景)` 回归组（宿主场景已移出）；现只跑
   主世界 + `H3SmokeDiag`。
6. **`.github/workflows/ci.yml`**：`perf-baseline` 作业（载体 = `CivSimDiag.tscn --only=T40`）
   随宿主移出而**停用**，原定义以注释形式留在原处并注明可用 `scripts/Test/PerfBench` 重建。

## 4 验证（实测）

| 项 | 结果 |
|---|---|
| `dotnet build world.csproj` | **0 警告 0 错误** |
| `dotnet test scripts/Test/World.Tests` | **345 PASS / 0 FAIL**（原 506 ⇒ 减 161，即 9 个 CivSim 测试文件的用例） |
| `dotnet build scripts/Test/PerfBench` | 0 错误 |
| `dotnet build scripts/Test/World.Tests.Local` | 0 错误 |
| 残留引用 grep（排除 `scripts/_removed/`） | 仅注释/文档，**无代码引用** |

## 5 回归操作

见 `scripts/_removed/README.md`（可执行清单）。要点：
目录 `git mv` 回原位 → 删 csproj 两行排除 → 恢复契约测试的两条白名单行与
`typeof(World.CivSim.CivSimContext)` → 恢复方向守卫（见 commit `0a1408e` 之前版本）→
`godot --headless --import` → build + test。

## 6 与既有边界的关系

- **ADR-0005 不变**：CivSim 回归后仍是「领域消费者」，其自然输入**必须**经
  `World → Human Input Bridge`（四阶段逐层迁移）接入，不得直接 `using World.WorldGen`。
  移出前钉住它的 `CivSim_DoesNotReferenceWorldGen` 方向守卫，回归时应一并恢复。
- **路线不变**：`Phase 4 禁接 CivSim、禁写 Bridge` —— 本条裁决不改变该边界，
  反而把"CivSim 尚未接入"从"纪律"落到"编译期事实"（类型不在程序集内）。
