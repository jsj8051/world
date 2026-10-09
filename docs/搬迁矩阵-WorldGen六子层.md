# A 步搬迁矩阵：`worldgen/` → `WorldGen/` 6 子层

> ✅ **状态：已执行完毕（2026-10-06）**。下表为执行前的规划原文，保留作为决策依据。
> 实际执行结果与规划的三处偏差见文末「执行结果对账」。
>
> ⚠️ **后续（2026-10-09）**：这六子层**已重排为八子层**——`Simulation/` 拆为 `Climate/` + `Hydrology/`，
> 新增 `Foundation/`，`SurfaceResolver` → `Placement/`，`HeightComposer` → `Composition/`。
> **本文保留 2026-10-06 原貌**（历史记录，不回改）；现状见 `docs/裁决-WorldGen子层重排.md`
> 与 `docs/architecture.md` §目录治理。

- 日期：2026-10-06
- 性质：搬迁规划 + 执行结果对账
- 基线：`2c67bad`（main）｜测试基线：**457 个 `[Test]` 特性**
- 依据：`docs/功能清单与结构重规划.md` §3.1，采纳评审四点修正

---

## 0. 评审意见采纳记录

| 评审修正 | 处置 | 依据 |
|---|---|---|
| `Services/` 不进 `Foundation/` | ✅ 采纳。`LogService` 依赖 `GD.Print`、`UserPaths` 管 `userdata/` 路径 ⇒ 宿主/应用层语义，非纯基础设施 | `Services/LogService.cs:6`、`Services/UserPaths.cs:4` |
| `Render/` **不改名** `Rendering/` | ✅ 采纳。改名会牵动 `namespace World.Render → World.Rendering`，而契约 7 的 `renderLayer = {"World.Render", "World.Render.UI"}` 按**名字**匹配 | `ArchitectureContractTests.cs:225` |
| Overlay 不一文件一目录 | ✅ 采纳。`RiverLineOverlay` + `SnowOverlay` 合并入 `Render/Overlays/` | 各目录 1 文件 = 纯层级噪声 |
| `Legacy/` 不整包接收 | ✅ 采纳。`HexPlanet` 仍是 `LogicGrid`/`CivSim` 有效依赖，不属 Legacy | 见 §4 |
| **`Simulation 13+1` 数字错** | ✅ 采纳并修正。实测 **15 个**（不含 `SnowOverlay`），报告写"13+1" = 14 ❌ | `ls scripts/worldgen/*.cs` 过滤实测 |

**不变的部分**（评审明确要求保持不动）：`CivSim/`(62) / `Diagnostics/`(18) / `Domain/`(5) / `Gameplay/`(3) / `LogicGrid/`(3)。

---

## 1. A 步范围与总原则

**总原则：目录治理，不做逻辑重构。** 只允许四类动作：

1. 移动文件
2. 必要时改 namespace / using
3. 修文档路径 + `.tscn` 路径
4. 跑全测试

⛔ 不做：接口重命名 / 类合并 / 依赖重构 / 功能清退 / 参数调整。

**A 步只动 33 个 `worldgen/` 文件 + 2 个 `.tscn`**。不碰 `Render/`、不碰 `Foundation/`、不碰 `Services/`。

---

## 2. 33 文件 → 6 子层 搬迁矩阵

> **namespace 全部保持 `World.WorldGen` 不变**（见 §5.1 契约 9 硬约束）⇒ 本步**零 using 改动、零 namespace 改动**。

### 2.1 `Placement/` — 5 文件（世界的"原因"）

| 现路径 | 新路径 | namespace | using 改动 |
|---|---|---|---|
| `worldgen/ContinentLayout.cs` | `WorldGen/Placement/ContinentLayout.cs` | 不变 | 无 |
| `worldgen/LandSeaFields.cs` | `WorldGen/Placement/LandSeaFields.cs` | 不变 | 无 |
| `worldgen/GeologicalRegions.cs` | `WorldGen/Placement/GeologicalRegions.cs` | 不变 | 无 |
| `worldgen/TectonicField.cs` | `WorldGen/Placement/TectonicField.cs` | 不变 | 无 |
| `worldgen/SeedDerivation.cs` | `WorldGen/Placement/SeedDerivation.cs` | 不变 | 无 |

⚠️ **`H3LandSeaProjector.cs` 归 Placement 而非 Simulation** —— 契约 4 `FinalGeography_DoesNotReferencePlacementProjector`（`:145`）明确把 `H3LandSeaProjector` 称为"Placement 投影器"。它虽消费 `H3TerrainSampler`，但归属由契约定义，不由调用关系定义。

### 2.2 `Features/` — 5 文件（地貌特征 + 合成器）

| 现路径 | 新路径 | namespace | using 改动 |
|---|---|---|---|
| `worldgen/TerrainFeature.cs` | `WorldGen/Features/TerrainFeature.cs` | 不变 | 无 |
| `worldgen/MountainSkeleton.cs` | `WorldGen/Features/MountainSkeleton.cs` | 不变 | 无 |
| `worldgen/RegionalLandforms.cs` | `WorldGen/Features/RegionalLandforms.cs` | 不变 | 无 |
| `worldgen/VolcanoField.cs` | `WorldGen/Features/VolcanoField.cs` | 不变 | 无 |
| `worldgen/HeightComposer.cs` | `WorldGen/Features/HeightComposer.cs` | 不变 | 无 |

### 2.3 `Discretization/` — 2 文件（含 1 个从 Simulation 归位）

| 现路径 | 新路径 | namespace | using 改动 |
|---|---|---|---|
| `worldgen/H3TerrainSampler.cs` | `WorldGen/Discretization/H3TerrainSampler.cs` | 不变 | 无 |
| `worldgen/H3LandSeaProjector.cs` | `WorldGen/Discretization/H3LandSeaProjector.cs` | 不变 | 无 |

⚠️ **两难取舍，需拍板**：`H3LandSeaProjector` 同时
- 契约 4 视其为 **Placement 投影器**（`:145`）
- 功能上是 **Placement 场 → H3 的离散化器**（消费 `H3TerrainSampler`）

**本矩阵按"契约语义"归 `Discretization/`**（与它实际扮演的角色一致：连续场→H3）。若你认为契约命名优先，改归 `Placement/` 也不影响任何代码——**这是纯目录选择，两边都对**。此项列入 §7 待拍板。

### 2.4 `Final/` — 2 文件（唯一世界事实源）

| 现路径 | 新路径 | namespace | using 改动 |
|---|---|---|---|
| `worldgen/FinalGeography.cs` | `WorldGen/Final/FinalGeography.cs` | 不变 | 无 |
| `worldgen/FinalSpatialIndex.cs` | `WorldGen/Final/FinalSpatialIndex.cs` | 不变 | 无 |

### 2.5 `Simulation/` — **14 文件**（不是 15，`SnowOverlay` 归 Render）

| 现路径 | 新路径 | namespace | using 改动 |
|---|---|---|---|
| `worldgen/SphericalField.cs` | `WorldGen/Simulation/SphericalField.cs` | 不变 | 无 |
| `worldgen/SpatialScale.cs` | `WorldGen/Simulation/SpatialScale.cs` | 不变 | 无 |
| `worldgen/SurfaceResolver.cs` | `WorldGen/Simulation/SurfaceResolver.cs` | 不变 | 无 |
| `worldgen/PrecipitationModel.cs` | `WorldGen/Simulation/PrecipitationModel.cs` | 不变 | 无 |
| `worldgen/H3Hydrology.cs` | `WorldGen/Simulation/H3Hydrology.cs` | 不变 | 无 |
| `worldgen/HydrologyRoutingSurface.cs` | `WorldGen/Simulation/HydrologyRoutingSurface.cs` | 不变 | 无 |
| `worldgen/RiverNetwork.cs` | `WorldGen/Simulation/RiverNetwork.cs` | 不变 | 无 |
| `worldgen/RiverGraph.cs` | `WorldGen/Simulation/RiverGraph.cs` | 不变 | 无 |
| `worldgen/RiverGeometry.cs` | `WorldGen/Simulation/RiverGeometry.cs` | 不变 | 无 |
| `worldgen/RiverPresentationSpline.cs` | `WorldGen/Simulation/RiverPresentationSpline.cs` | 不变 | 无（唯一无外部 using 的文件之一） |
| `worldgen/RiverSymbolWidth.cs` | `WorldGen/Simulation/RiverSymbolWidth.cs` | 不变 | 无 |
| `worldgen/BasinGraph.cs` | `WorldGen/Simulation/BasinGraph.cs` | 不变 | 无 |
| `worldgen/LakeState.cs` | `WorldGen/Simulation/LakeState.cs` | 不变 | 无 |
| `worldgen/WaterTopology.cs` | `WorldGen/Simulation/WaterTopology.cs` | 不变 | 无 |

**14 个明细**：`SphericalField` `SpatialScale` `SurfaceResolver` `PrecipitationModel` `H3Hydrology` `HydrologyRoutingSurface` `River{Network,Graph,Geometry,PresentationSpline,SymbolWidth}` `BasinGraph` `LakeState` `WaterTopology`

> **数字对账**：原 `worldgen/` 33 = Placement 5 + Features 5 + Discretization 2 + Final 2 + Simulation 14 + Composition 2 + **移出 3**（`WorldGenMapModes`/`RiverLineOverlay`/`SnowOverlay` 属 B 步 Render）。**5+5+2+2+14+2+3 = 33 ✓**

### 2.6 `Composition/` — 2 文件（唯一装配点）

| 现路径 | 新路径 | namespace | using 改动 |
|---|---|---|---|
| `worldgen/WorldGenPlanet.cs` | `WorldGen/Composition/WorldGenPlanet.cs` | 不变 | 无 |
| `worldgen/WorldGenManager.cs` | `WorldGen/Composition/WorldGenManager.cs` | 不变 | 无 |

⚠️ 这两个是 **Godot 节点类**（`WorldGenPlanet : Node3D` / `WorldGenManager : Node3D`），是 `.tscn` 唯一引用的两个 `worldgen` 文件（见 §3）。

### 2.7 移出（A 步不动，B 步处理）

| 文件 | 目标 | 备注 |
|---|---|---|
| `worldgen/WorldGenMapModes.cs` | `Render/Modes/` | ⚠️ 依赖 `World.Render`（`:3`），是表现层 |
| `worldgen/RiverLineOverlay.cs` | `Render/Overlays/` | `MeshInstance3D` |
| `worldgen/SnowOverlay.cs` | `Render/Overlays/` | 注释自称"雪线属表现层" |

---

## 3. ⚠️ 必改：`.tscn` 硬编码脚本路径（原报告漏项）

**15 个 `.tscn` 用 `path="res://..."` 硬编码引用脚本**，移动文件后 Godot 会因路径失效丢脚本。实测全部引用：

| `.tscn` 引用的脚本 | 本步是否移动 |
|---|---|
| `res://scripts/worldgen/WorldGenManager.cs` | ✅ **必须改** |
| `res://scripts/worldgen/WorldGenPlanet.cs` | ✅ **必须改** |
| `res://scripts/Camera/OrbitalCamera.cs` | ❌ 不动 |
| `res://scripts/Render/UI/CellInfoCard.cs` | ❌ 不动 |
| `res://scripts/Render/UI/MapDock.cs` | ❌ 不动 |
| `res://scripts/Diagnostics/*.cs`（10 个） | ❌ 不动 |

⇒ **本步只需改 2 处**（`WorldGenManager` / `WorldGenPlanet`），改动面极小。

Godot 通常会在编辑器内自动重写 `.tscn` 的 `uid`，但**命令行/无人值守场景下不会** ⇒ 必须手工同步。涉及文件：
- `scenes/core/WorldGenWorld.tscn`（主入口，`project.godot:15` 指向它）

---

## 4. 本步**不动**的目录（含理由）

| 目录 | 为何不动 |
|---|---|
| `HexPlanet/` (5) | ⚠️ **不是 Legacy**。`SphereGrid` ← `Diagnostics/ArchiveDiag.cs:117`；`GoldbergBuilder` ← `tests/HexPlanetTests.cs`；`Icosahedron` → `SubdividedMesh` → 且 `LogicGrid/GameGrid` 假定同款几何。`CivSim` 依赖白名单**有意包含** `HexPlanet`（永久原则第 10 条） |
| `PlanetLOD/ChunkMeshBuilder.cs` | 真正零调用（连测试都没有）⇒ **E 步独立清退决策**，不在 A 步顺手做（停止条件原则第 11 条） |
| `Surface/PlanetColors.cs` | 仅 `tests/ServicesTests.cs:28-31` 使用 ⇒ 同上，E 步处置 |
| `Utils/` (9) | C 步处理 |
| `Services/` (2) | 保持独立（评审修正①） |
| `CivSim/`(62) `Diagnostics/`(18) `Domain/`(5) `Gameplay/`(3) `LogicGrid/`(3) | 评审明确要求不动 |

---

## 5. 风险矩阵（含实测证据）

### 5.1 🟢 零 namespace 改动 —— 但这是**硬约束**，不是自由裁量

**契约 9 `NewWorldLine_NamespaceIsWorldGen`**（`ArchitectureContractTests.cs:317-330`）断言：

```csharp
var core = new[] { typeof(FinalGeography), typeof(HeightComposer), typeof(FinalSpatialIndex),
                   typeof(RiverNetwork), typeof(WaterTopology), typeof(WorldGenPlanet) };
var wrong = core.Where(t => t.Namespace != "World.WorldGen")   // ← 硬编码字符串
```

⇒ **本步绝不能改 namespace**，否则 6 个核心类型的 namespace 变了，这条契约**立即失败**。

**推论（重要）**：A/B/C/F 四步全部是"纯物理移动"。原报告 §3.2 提到"可能需要改 namespace"，在 WorldGen 这一步**不成立**——namespace 已被契约锁死。

### 5.2 🟡 `.tscn` 路径（见 §3）— 唯一必改项，2 处

### 5.3 🟢 编译配置零改动

`world.csproj` 用 `Godot.NET.Sdk/4.7.1`（SDK-style 自动 glob），**无显式 `<Compile Include>`** ⇒ 移动文件无需改 csproj。

### 5.4 🟡 using 零改动

实测 33 文件的 namespace 外部依赖只有 4 种：`World.NewHexWorld`(18) / `World.Utils`(11) / `World.Utils.H3`(4) / `World.Render`+`World.Camera`+`World.Render.UI`(仅 `WorldGenManager`)。
⇒ 本步**这些目标目录全部不动** ⇒ **零 using 改动**。

### 5.5 🟡 docs 路径引用需同步

`docs/架构设计.md` §1 五层图、`docs/newdecision/设计-世界生成空间-01~08` 决策条款直接引用类名与目录。移动后需更新路径表述。
⚠️ 注意：决策文档引用**类名**（如 `FinalGeography`）的部分**不受影响**；只引用**目录路径**（如 `scripts/worldgen/`）的需改。

### 5.6 🟢 `.uid` 文件自动跟随

Godot 4 的 `.uid` 旁文件会随 `.cs` 一起移动（由文件系统操作保持），无需重建。

### 5.7 🟡 后续步骤的量级修正（E 步比原报告估的大 6 倍）

实测 `World.NewHexWorld` 被 **24 个文件 `using`**（`worldgen/` 内 21 + `Render/BallView` + `Render/CellQuery` + 定义 1）。

| 步骤 | namespace 改动面 |
|---|---|
| A（WorldGen 6 子层） | **0** |
| B（Render 归位） | **0** |
| C / C2（Foundation / Persistence） | **0** |
| **E（`Spatial/Ball`）** | **24 处 using + 1 处 namespace** |

⇒ 原报告 §3.2 估"引用 4"**严重低估**。E 步必须独立成批，**不要与 A–C 混做**（否则一次改 24 处 using，验收时无法归因是移动引入还是 namespace 引入）。

---

## 6. 执行步骤与验收清单

### 6.1 执行步骤

```
1. git add -A 现有变更（保持"边界确认后再提交"的习惯）
2. mkdir -p WorldGen/{Placement,Features,Discretization,Final,Simulation,Composition}
3. git mv 33 个文件（3 个表现层文件本步保留原位）
4. 改 2 处 .tscn 路径：
   scenes/core/WorldGenWorld.tscn
     res://scripts/worldgen/WorldGenManager.cs → res://scripts/WorldGen/Composition/WorldGenManager.cs
     res://scripts/worldgen/WorldGenPlanet.cs  → res://scripts/WorldGen/Composition/WorldGenPlanet.cs
5. 检查 .uid 是否跟随（git status 应显示 rename 而非 delete+add）
6. 跑全测试：dotnet test 或本地执行器
7. 更新 docs 里的目录路径引用
```

### 6.2 验收清单

| # | 验收项 | 通过标准 |
|---|---|---|
| 1 | 测试总数 | **457 个 `[Test]` 全绿**（与基线一致，零增减） |
| 2 | namespace 契约 | 契约 9 `NewWorldLine_NamespaceIsWorldGen` 通过 |
| 3 | 全部 21 条契约 | `ArchitectureContractTests` 全绿（实测文件内有 21 个 `[Test]` 方法，非文档所称 22） |
| 4 | 文件总数 | `scripts/` 仍 151 个 `.cs`（A 步不增不减） |
| 5 | `worldgen/` 消失 | `ls scripts/worldgen` = 不存在 |
| 6 | 6 子层文件数 | Placement 5 / Features 5 / Discretization 2 / Final 2 / Simulation 14 / Composition 2 = **30** + 留原位 3 = 33 |
| 7 | `.tscn` 引用 | `grep -c 'res://scripts/WorldGen' scenes/core/WorldGenWorld.tscn` = 2 |
| 8 | 编译 | `dotnet build` 零错误零新增警告 |
| 9 | **实机验收** | Godot 打开 `WorldGenWorld.tscn` → `WorldGenPlanet`/`WorldGenManager` 脚本**不为空**（防 .tscn 路径漏改的静默失败） |
| 10 | 幂等性 | 主场景能跑出与移动前**逐像素一致**的星球（同 seed ⇒ 确定性） |

⚠️ **第 9 项最容易被忽略**：`.tscn` 路径写错时 Godot 可能在编辑器里静默丢脚本，测试却全绿（因为 C# 侧编译不受影响）。必须实机确认。

---

## 7. 待拍板（1 项，无阻塞）

**`H3LandSeaProjector.cs` 归 `Discretization/` 还是 `Placement/`？**

| 视角 | 归属 | 理由 |
|---|---|---|
| 契约语义 | `Placement/` | 契约 4（`:145`）称它 "Placement 投影器" |
| 功能语义 | `Discretization/` | 它消费 `H3TerrainSampler`，实际角色是"连续场→H3 离散化" |

本矩阵按**功能语义**归 `Discretization/`。**无论选哪个都不影响任何代码**（纯目录选择），且不影响 `Simulation = 14` 的数字。若改归 `Placement/`，则 `Placement 6` / `Discretization 1`，总数仍 33。

---

## 8. 执行结果对账（2026-10-06 实际执行）

**提交**：`1eb6ecd`(A) → `6410d49`(B) → `88bf2d2`(C)

### 8.1 规划 vs 实际：四处偏差

| # | 规划 | 实际 | 原因 |
|---|---|---|---|
| 1 | 只改 2 处 `.tscn` | 实际改 **2 处文件、但路径更深一层** | 矩阵 §3 漏了 `scenes/worldgen/WorldGenPlanet.tscn`；且替换须补上新增的 `Composition/` 子层，否则路径不存在 |
| 2 | 顶层 `worldgen/` → `WorldGen/` 未在矩阵中 | 实际执行了两步法改名 | Windows 大小写不敏感：`git mv worldgen WorldGen` 单步会被识别为"无变化"，须 `worldgen → __WorldGenTmp → WorldGen` |
| 3 | 契约测试未在矩阵中 | 实际改了 `ArchitectureContractTests` 3 处 | 契约有 3 处硬编码 `scripts/worldgen/`，即"防代码与目录脱节"护栏，目录改名不改它会测试失败 |
| 4 | 矩阵未含 B/C 步 | 实际 A/B/C 连续执行 | B（Render 归位）、C（Utils→Foundation）沿用同一套验收模板 |

### 8.2 累计验收（相对基线 `2c67bad`）

```
73 files changed, 0 insertions(+), 0 deletions(-)
73 个变更全部 R100（100% 相似度 = 纯路径移动，零内容变更）
```

- `dotnet build`：**0 警告 / 0 错误**
- `dotnet test`：**511 通过 / 0 失败**
- `scripts/` 总 `.cs`：**151**（零增删）
- Godot 4.7.1 headless：加载正常、**零 ERROR**、`[WORLDGEN-TIMING]` 数值与迁移前**逐字一致**
  （`n=288122 res=4 land=29.0% regions=95 ridges=71 basins=6971(endorheic 0) lakes=3439 thr=0.636`）

### 8.3 执行中新增的架构护栏

`ArchitectureContractTests.NewWorldLine_NamespaceIsWorldGen` 原先只用
`FindRepoDir("scripts", "WorldGen")` 定位目录，但 `Directory.Exists` 在 Windows 上
**大小写不敏感** ⇒ 写 `worldgen` 也会命中，契约会失去"防脱节"作用。
已追加大小写严格钉：

```csharp
var actualDirName = new DirectoryInfo(worldgenDir).Name;
Assert.That(actualDirName, Is.EqualTo("WorldGen"), ...);
```

### 8.4 后续（✅ 已全部完成）

- **B 步** `6410d49`：3 个表现层文件归位 `Render/{Modes,Overlays}/`（6 R100，0 ins/0 del）
- **C 步** `88bf2d2`：`Utils/` 7 个根级文件按职责归入 `Foundation` 六子层（7 R100，0 ins/0 del）
  - 与矩阵原提案的差异：`ColorRamp` → `Color/`、`ArchiveChunk` → `Archive/`
    （不塞 `IO/`——前者是通用色带算法不含业务色，后者是 persistence format 而非 IO）
- **D 步** `9f2dc52`(D-A) / `f4cc534`(D-B) / `c69142e`(D-C)：
  Legacy 逐项取证与清退（HexPlanet 闭包 + PlanetColors）
  —— 详见 `docs/审查报告-D-Legacy逐项决策.md`
- **E 步** `139b898`：`new_HexWorld/Ball` → `Spatial/Ball`，
  namespace `World.NewHexWorld` → `World.Spatial` + 全部引用同步
  - ⚠️ **口径勘误**：本矩阵 §3.2 / §5.7 记「24 处 using」，那仅指 **`scripts/` 侧**。
    实测总量 = **46 处 using（scripts 24 + tests 22）+ 3 处全限定名 = 49 处引用**，
    另加 1 处 namespace 声明。差异来源：早期统计只扫 `scripts/`，
    未含 `tests/` 侧与 `World.NewHexWorld.Ball` 全限定名形式。

**五步完成后**：`scripts/` 一级目录 15 → **13**，`.cs` 151 → **146**，
`.cs`/`.cs.uid` 配平 195/195、零孤儿；测试 511 → 492（D 步预期减少 19）。

---

*本节为执行后追加。§1–§7 为执行前规划原文，保留作为决策依据；其中"尚未执行"的状态描述已由本节覆盖。所有"实测"结论均附命令依据。*
