# 裁决：删除 `World.Services`（服务层）及其连带诊断簇

日期：2026-10-09
状态：**已执行**（commit `20e9d96` 代码 / 本文件为文档侧）
触发：用户「services下的文件干什么的」→「删掉吧」→ 范围确认「**整个 Services/（含连带）**」
关联：`docs/裁决-Domain解散与BiomeType归Constant.md`、`docs/裁决-Legacy载体簇删除.md`、
ADR-0002（**本裁决后作废**）、ADR-0004（**本裁决后作废**）、ADR-0003（仍有效，`DiagSceneBase` 保留）

---

## 〇 裁决表

| 对象 | 裁决 | 依据 |
|---|---|---|
| `Logic/Services/LogService.cs` | **删除** | `GD.Print` 的 16 行薄包装；**零生产消费者** |
| `Logic/Services/UserPaths.cs` | **删除** | 在编消费者只有 2 个截图诊断；`MigrateLegacyData()` **零调用者** |
| `Test/Diagnostics/FieldCompare.cs` | **删除（连带）** | 唯一依赖 = `LogService.LogErr`；且**本身零调用者** |
| `Test/Diagnostics/RiverShotDiag.cs` + `.tscn` | **删除（连带）** | `UserPaths.Resolve` ×3 |
| `Test/Diagnostics/MapModeShotDiag.cs` + `.tscn` | **删除（连带）** | `UserPaths.Resolve` ×1 |
| `Test/Diagnostics/CellHighlightDiag.cs` + `.tscn` | **删除（随批）** | 实测**仅一条死 `using World.Services;`**，无真实依赖（见 §二 注） |
| `Test/Diagnostics/DiagSceneBase.cs` | **保留** | 非 Services 连带；`_removed/CivSimDiag` 的基类 + 未来诊断统一基类（ADR-0003） |
| `Test/Diagnostics/H3SmokeDiag.cs` + `.tscn` | **保留** | 不依赖 Services；`h3.dll` 部署问题的唯一暴露面 |
| ADR-0002 / ADR-0004 | **标作废** | 其唯一在编载体 `LogService` 已删 |

**一句话**：`World.Services` 是**已被架空的服务层残骸**——三个服务里 `EventBus` / `ArchiveService`
早已随旧 UI 清退消失，只剩 `LogService`（零生产消费者）与 `UserPaths`（只服务截图诊断）。

---

## 一 实测事实（删除依据，非估计）

### 1.1 在编消费者图（`Grep` 全仓，排除 `_removed/` 与 `bin/obj`）

| 被消费项 | 在编消费者 | 说明 |
|---|---|---|
| `LogService` | `Test/Diagnostics/FieldCompare.cs` ×4 | **唯一**；而 `FieldCompare` 自身零调用者 |
| `UserPaths` | `RiverShotDiag.cs` ×3、`MapModeShotDiag.cs` ×1 | 全部是**窗口模式截图**诊断 |
| `LogService` / `UserPaths` | `CellHighlightDiag.cs` ×0 | 文件头有一条 `using World.Services;`，正文**无任何使用** ⇒ 死 using |

**`WorldGen`（世界生成主链）与 `Render`（地图模式）对 `Services` 零引用。**

### 1.2 三条"零消费者"证据

1. `LogService.Log/LogErr` 在**生产代码里零调用**。ADR-0002 计划把它做成统一日志出口（收编 274 处
   `GD.Print`），ADR-0004 更进一步"全量收编"——**但全量收编从未真正落地**；旧代码被清退后，
   调用点连残骸都没剩下。
2. `FieldCompare.` 在全仓 `scripts/` 下 **0 命中**（含 `_removed/`）。它是当年各 diag 共用的
   "NaN 感知 diff"工具库，消费者（`CivSimDiag` / `TectonicsTest` / `LogicGridDiag` / `MonsoonDiag` …）
   全部已删/已移出 ⇒ 它**独立于本次删除也已是死码**。
3. `UserPaths.MigrateLegacyData()` 的**唯一调用点是 `MainMenu._Ready`**，而 `MainMenu` 已随旧 UI 清退
   消失 ⇒ 该方法**零生产调用者**。这正是备忘里"**存档三层都不通**"的另一面：
   路径基建还在，但既没有存档菜单、也没有存档格式。

### 1.3 为什么"连带"落在诊断目录

`Services` 的编译面消费者**一个都不在生产链上**，全部落在 `scripts/Test/Diagnostics/`。
所以"删 Services"在物理上等价于"删掉三个窗口模式截图诊断 + 一个死码工具库"。这也是为何
本次删除**不触碰** `world.csproj`（`scripts/Test/**` 的排除纪律见备忘，诊断目录不能整目录排除）。

---

## 二 删除范围（精确）

```
scripts/Logic/Services/LogService.cs   (+ .cs.uid)
scripts/Logic/Services/UserPaths.cs    (+ .cs.uid)
        └─ 目录 scripts/Logic/Services/ 消失 ⇒ 命名空间 World.Services 消失
scripts/Test/Diagnostics/FieldCompare.cs          (+ .cs.uid)
scripts/Test/Diagnostics/RiverShotDiag.cs         (+ .cs.uid)
scripts/Test/Diagnostics/MapModeShotDiag.cs       (+ .cs.uid)
scripts/Test/Diagnostics/CellHighlightDiag.cs     (+ .cs.uid)
scenes/diag/RiverShotDiag.tscn
scenes/diag/MapModeShotDiag.tscn
scenes/diag/CellHighlightDiag.tscn
```

删后实测：
- `scripts/Logic/` = `Constant / Spatial / Utils / WorldGen`（4 个子目录）
- `scripts/Test/Diagnostics/` = `DiagSceneBase.cs` + `H3SmokeDiag.cs`（各带 `.uid`）
- `scenes/diag/` = `H3SmokeDiag.tscn`（1 个）

> **注（`CellHighlightDiag`）**：在给用户的范围确认里它被列为 `Services` 的消费者；本裁决执行时
> 复核发现该文件对 `Services` **只有一条死 `using`**，并非真消费者。仍按已确认范围**随批删除**
> （三件截图诊断同批退出，避免"兄弟全走、留一个需人工判读的残余"）。若日后要恢复，它**不需要**
> 恢复 `Services`——只需 `git checkout` 该 `.cs` + `.tscn` 并删掉那条死 using。
> **源码级回归未丢失**：`CellHighlightRingTests`（源码级禁止 radial lift）等单测**原样保留**。

---

## 三 保留项与理由

| 保留 | 理由 |
|---|---|
| `Test/Diagnostics/DiagSceneBase.cs` | ① 三个子类删净后**零在编消费者**，但它是 `_removed/Test/Diagnostics/CivSimDiag.cs` 的基类；② 是未来诊断场景的统一基类（ADR-0003），成本 58 行、无外部依赖；③ **不属** `Services` 连带 |
| `Test/Diagnostics/H3SmokeDiag.cs` + 场景 | 不依赖 `Services`；且是 **`h3.dll` 在 Godot mono 宿主下部署路径问题的唯一暴露面**——`scripts/verify.sh` 仍把它作为 headless 回归组 |
| `scripts/Test/World.Tests/README.md` 的"不触碰引擎原生调用"纪律 | 纪律本身不变（只是举例里的 `LogService` 已不存在，已改措辞） |

---

## 四 代价与消解

| 代价 | 消解 |
|---|---|
| **失去 PNG 截图 / 像素级视觉诊断能力**（`RiverShotDiag` 1159 行 + `MapModeShotDiag` + `CellHighlightDiag` 447 行） | 三者均**必须在窗口模式**运行（`--headless` 的 dummy 渲染服务器 `GetViewport().GetTexture()` 取不到帧）+ 需人工判读 ⇒ 本来就不进 CI。源码级回归由既有单测承担：`CellHighlightRingTests` / `RiverSymbolWidthTests` / `RiverRibbonGeometryTests` / `RiverPresentationSplineTests` / `RiverGraphTests` / `RiverNetworkTests` |
| **`scripts/_removed/` 内 CivSim 的回归路径变长** | 恢复清单已更新（`scripts/_removed/README.md`）：除移回目录 + 删 csproj 两行外，**还需恢复 `scripts/Logic/Services/`**（`CivSimDiag.cs` 用 `LogService` + `DiagSceneBase`；`Logic/CivSim/Tables/TechTable.cs` 用 `LogService`）。**`DiagSceneBase` 未删 ⇒ 该项无需恢复** |
| **写盘路径再无唯一汇聚点** | 本来也只服务诊断。**接存档的自然时机 = 自然输入桥（Bridge B0）之后**——届时按当时需求重建（大概率不是 `UserPaths` 这份实现） |
| **`--headless` 不再覆盖诊断场景组** | `verify.sh` 早在 2026-10-09 早先的重标定里就只剩"主世界 + H3 冒烟"两组，且**已不含**这三个场景 ⇒ 本次无需改 `verify.sh` |

---

## 五 git 恢复（若需回滚）

```bash
# 整体回滚（代码侧）
git revert 20e9d96

# 只恢复某一件
git checkout 20e9d96^ -- scripts/Test/Diagnostics/CellHighlightDiag.cs \
                          scripts/Test/Diagnostics/CellHighlightDiag.cs.uid \
                          scenes/diag/CellHighlightDiag.tscn
#   ↑ CellHighlightDiag 不需要 Services；若含 River/MapMode/FieldCompare，
#     则必须同时恢复 scripts/Logic/Services/{LogService,UserPaths}.cs（+ .uid）
```

同时须把 `ArchitectureContractTests.ServicesNamespace_IsGone` 一并改回（否则契约立刻变红）。

---

## 六 契约与文档连带（本次已做）

| 文件 | 改动 |
|---|---|
| `Test/World.Tests/ArchitectureContractTests.cs` | **新增 `ServicesNamespace_IsGone`**：① `World.Services` 命名空间须消失；② `LogService`/`UserPaths`/`FieldCompare` 三个类型名不得复活；③ `scripts/Logic/Services` 目录须不存在；④ **悬空场景钉**——三个 `.tscn` 须已删、`H3SmokeDiag.tscn` 须在。类头契约清单补第 ⑦ 条；`LegacyCarrierCluster_IsDeleted` 一处过期附注（"World.Domain 本身保留"）修正 |
| `docs/architecture.md` §9 | 命名空间清单去掉 `World.Services`，补注 |
| `docs/decisions/0002-service-layer-design.md` | 状态 → **已作废**（服务层三件均已不存在） |
| `docs/decisions/0004-log-service-migration.md` | 状态 → **已作废**（载体 `LogService` 已删） |
| `docs/README.md` | ADR 索引行注明 0002 / 0004 已作废 |
| `docs/索引.md` | §4.6 `Services` 行、§4.8 `Diagnostics` 段、§8 "用户路径"行、§1 速览行 —— 标注/重写为当前真相 |
| `docs/架构设计.md` §5/§6/§7 | 直接失效的 5 行（`Services/UserPaths`、`World.Services`、`DiagSceneBase` 截图基线两行）修正 |
| `docs/裁决-Domain解散与BiomeType归Constant.md` | `FieldCompare` 就地内联 `MonthsPerYear` 的注记补"该文件其后亦整删" |
| `scripts/_removed/README.md` | 回归清单补"还需恢复 `Services/`" |
| `scripts/Test/World.Tests/README.md` | 测试纪律举例里的 `LogService` 措辞更新 |

---

## 七 文档债（本次**未**修，登记在案）

`docs/索引.md` 与 `docs/架构设计.md` 是**整体早于多轮重构**的历史盘点（前者仍描述
`scripts/Services/`、`scripts/Biome/`、`noise_world`、"219 个 .cs"；后者 §5 存档三格式 / §6 CivSim
保留区 / §1 依赖白名单 均描述已删除的东西）。本次只做**与删除直接相关的定点修正**，
不做整篇重写（原则 11：清退不范围膨胀）。二者应在一次专门的"文档盘点"动作里收口——
优先以 `docs/architecture.md` 为现行唯一宪法。

同类既存债（承接 `裁决-Domain解散与BiomeType归Constant.md` §六）：
`docs/decisions/0005-natural-input-bridge.md`（多处仍称 `World.Domain`）、
`docs/设计-NaturalInputBridge实现设计.md`（`BiomeType`「共 24 类」）、
`docs/功能清单与结构重规划.md`。

---

## 八 验证（本次实测）

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 警告 / 0 错误** |
| `dotnet test scripts/Test/World.Tests` | **280 PASS**（279 + 新增 1）/ 0 失败 |
| `dotnet build scripts/Test/PerfBench` | 0 警告 / 0 错误（该工程不在 `world.sln`，须手工构建） |
| headless 世界生成 | `n=288122 res=4 land=29.0% regions=95 meanP=1098mm/年 meanT=76.0C ridges=71 basins=6971(endorheic 0) lakes=3439 thr=0.636` —— **逐字段同基线，零漂移** |
| `Cannot load C# script` / `SCRIPT ERROR` | **0 命中**（本次删的是**有 `.tscn` 挂载**的诊断场景，此项为关键验收） |
