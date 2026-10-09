# 裁决 · WorldGen 子层重排（六子层 → 八子层）

- 日期：2026-10-09
- 性质：纯目录治理（零逻辑重构）+ 决策记录
- 触发：用户提出「worldgen 是不是可以整理一下，现在的目录文件分配不合理」
- 拍板：用户选 **方案 A（八子层）**，新建层取名 **`Foundation/`**
- 基线：`4b97261`（main）｜测试基线 **281 PASS**
- 前身：`docs/搬迁矩阵-WorldGen六子层.md`（2026-10-06，六子层的由来）

---

## 1. 动机：六子层与内容脱节

2026-10-06 的六子层是按**当时的依赖 DAG** 切的：
`Placement` / `Features` / `Discretization` / `Final` / `Simulation` / `Composition`。
此后**水文（River 2A/2B/2C-A/B/C）+ 气候（温度 v1、降水）** 陆续落地，
全部塞进了 `Simulation/` ⇒ 该层涨到 **15 文件（占全树一半）**，成了杂物抽屉。

诊断出的**五处错配**（均有源码自述为据，非主观判断）：

| # | 错配 | 证据 |
|---|---|---|
| 1 | `Simulation/` 混装**四类互不相干**的东西：气候 2 · 水文 10 · 尺度/场基建 2 · Placement 解算器 1 | `SpatialScale.cs` 自述「世界生成空间 · **空间尺度 / 单位层**」；`SphericalField.cs` 自述「连续场层…**不依赖 H3**」；`SurfaceResolver.cs` 自述「Placement 阶段口（RawAt/OceanFactorAt/BathymetryAt）= **地貌生成依据**」 |
| 2 | `Composition/` 只装管线入口，真**合成器** `HeightComposer` 却在 `Features/` | 目录名与实际内容撞车 |
| 3 | `Features/` 把**契约层与实现**混放 | `TerrainFeature.cs` 一个文件含 `Scale3` + `TerrainFeature` 基类 + `ITerrainField` + `FeatureField` + `TerrainDomain`，与 3 个具体特征并列 |
| 4 | `Final/` 内 `FinalSpatialIndex` **不在 Final 层** | 其注释自述「真实位置在 **Final 与水文之后**」（消费河流格列表） |
| 5 | `Discretization/` 只有 2 文件，且 `H3LandSeaProjector` 归属**两可** | 搬迁矩阵 §7 自承认（契约语义=Placement / 功能语义=Discretization） |

> 判据不是"层多不好"，而是**目录名与其中内容是否自洽**（契合永久原则 8「物理目录 ≠ 架构边界」的推论）。

## 2. 方案（用户拍板 A）

`Simulation/` 解体，新增 `Foundation/` / `Climate/` / `Hydrology/` 三层，得八子层：

| 子层 | 文件数 | 内容 |
|---|---|---|
| `Composition/` | 2 | 管线入口 `WorldGenSimulation` + 高度合成器 `HeightComposer` |
| `Foundation/` | 2 | 连续场抽象 `SphericalField` + 空间尺度口径 `SpatialScale` |
| `Placement/` | 6 | 大陆布局 / 海陆场 / 地质区域 / 构造场 / 种子派生 + 地表状态口径 `SurfaceResolver` |
| `Discretization/` | 2 | `H3TerrainSampler` + `H3LandSeaProjector` |
| `Features/` | 4 | 特征契约 `TerrainFeature` + 山脉骨架 / 区域地貌 / 火山 |
| `Final/` | 2 | `FinalGeography` + `FinalSpatialIndex` |
| `Climate/` | 2 | `PrecipitationModel` + `Temperature` |
| `Hydrology/` | 10 | `H3Hydrology` `HydrologyRoutingSurface` `River{Network,Graph,Geometry,PresentationSpline,SymbolWidth}` `BasinGraph` `LakeState` `WaterTopology` |

合计 **30 文件**（总数不变，无新增/无删除）。

**为什么 `Foundation/` 不叫别的**：`SphericalField`（空间函数抽象）与 `SpatialScale`（空间尺度/单位口径）
都是**跨阶段共享的底座**——`SpatialScale` 被 `PrecipitationModel` / `RiverNetwork` / `LakeState` /
`BasinGraph` 及水文链共同消费；`SphericalField` 被 `LandSeaFields`（Placement）与 `H3TerrainSampler`
（Discretization）共同消费。二者都不是任何单一阶段的私有物。名称沿用项目旧址 `Utils/` 六子层的用词习惯。

**刻意不拆 `Final/`**：`FinalSpatialIndex` 虽在时序上晚于水文，但它索引的是 Final 世界事实
（且契约 `FinalWorldFacts_OnlyConsumedByWorldGen` 白名单按 `World.WorldGen*` 整体豁免），
单立一层会撞上"1 文件目录 = 纯层级噪声"的先例判决 ⇒ **维持现状，仅在本文记录该错配**。

## 3. 搬迁矩阵（16 个类，+16 个 `.uid`）

| 文件 | 原路径 | 新路径 |
|---|---|---|
| `HeightComposer.cs` | `Features/` | `Composition/` |
| `SurfaceResolver.cs` | `Simulation/` | `Placement/` |
| `SphericalField.cs` | `Simulation/` | `Foundation/` |
| `SpatialScale.cs` | `Simulation/` | `Foundation/` |
| `PrecipitationModel.cs` | `Simulation/` | `Climate/` |
| `Temperature.cs` | `Simulation/Temperature/` | `Climate/`（去掉 `Temperature/` 一层） |
| `H3Hydrology.cs` | `Simulation/` | `Hydrology/` |
| `HydrologyRoutingSurface.cs` | `Simulation/` | `Hydrology/` |
| `RiverNetwork.cs` | `Simulation/` | `Hydrology/` |
| `RiverGraph.cs` | `Simulation/` | `Hydrology/` |
| `RiverGeometry.cs` | `Simulation/` | `Hydrology/` |
| `RiverPresentationSpline.cs` | `Simulation/` | `Hydrology/` |
| `RiverSymbolWidth.cs` | `Simulation/` | `Hydrology/` |
| `BasinGraph.cs` | `Simulation/` | `Hydrology/` |
| `LakeState.cs` | `Simulation/` | `Hydrology/` |
| `WaterTopology.cs` | `Simulation/` | `Hydrology/` |

搬迁后 `Simulation/` 与 `Simulation/Temperature/` **两个目录均删除**（"Simulation" 不再是目录名）。

## 4. ★关键使能事实：为什么这是纯 `git mv`

三点实测（搬迁前）：

1. **这 30 个类全部是 `namespace World.WorldGen` 的非 Godot-Node 类**
   ⇒ 子目录是**自由分组**，不参与 namespace（目录层级规则 2026-10-09 钉下）
   ⇒ **零 `using` 改动、零 namespace 改动**。
2. **0 个 `.tscn` 指向 `scripts/Logic/WorldGen/**`**——唯二两个场景
   （`scenes/core/WorldGenWorld.tscn` / `scenes/worldgen/WorldGenPlanet.tscn`）
   引用的都是 **Scene 侧** `scripts/Scene/WorldGen/Composition/*` ⇒ **无"编译绿、运行炸"风险**。
3. **0 处测试断言子目录路径**——`ArchitectureContractTests.cs:416` 唯一出现
   `scripts/Logic/WorldGen/Final/` 的一处在**注释**里；实际断言只到
   `FindRepoDir("scripts","Logic","WorldGen")`（顶层，不含子层）⇒ 无测试连带。
4. `world.csproj` 走 SDK 通配 ⇒ **免改**；`scripts/_removed/` 内零引用。

## 5. 验收（相对基线 `4b97261`）

- `dotnet build`：**0 警告 / 0 错误**（15 个 rename，内容零变更）
- `dotnet build scripts/Test/PerfBench/PerfBench.csproj`：**0/0**（不在 `world.sln`，须手工构建）
- `dotnet build scripts/Test/World.Tests.Local/World.Tests.Local.csproj`：**0/0**（同上）
- `dotnet test scripts/Test/World.Tests/World.Tests.csproj`：**281 通过 / 0 失败**
- Godot 4.7.1 headless：加载正常、**零 ERROR / 零 `Cannot load`**，
  `[WORLDGEN-TIMING] n=288122 res=4 land=29.0% regions=95 meanP=1098mm/年 meanT=76.0C
  ridges=71 basins=6971(endorheic 0) lakes=3439 thr=0.636`
  —— **逐字段同基线，零漂移**。

## 6. 刻意未做（原则 11：清退不范围膨胀）

- **不加新契约**：八子层的名字**不被任何契约引用**（首层 `WorldGen/` 已由
  `NewWorldLine_NamespaceIsWorldGen` 钉住目录名与大小写）；为"子层名"再加一条钉
  会撞防膨胀第③问（无真实消费者）。若将来有人再从子层读路径，届时再补。
- **不拆 `Final/`**（理由见 §2 末尾）。
- **`Features/TerrainFeature.cs` 保持单文件**：契约与实现虽混放，但拆文件属"逻辑重构"，
  超出目录治理边界；本批只记录，不动。
- **历史文档不动**：`docs/搬迁矩阵-WorldGen六子层.md` 是 2026-10-06 的**执行记录**，
  保留原貌（仅加指向本文的横幅），不按现状回改。

## 7. 提交

按永久原则 13「变更边界不同 ⇒ 分开 commit」：代码（`git mv`，15 条 R100）与文档各一次。
