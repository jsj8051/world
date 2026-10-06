# 功能迁移对照清单：`strategy`（GDScript 板块构造原型） → `world`（Godot C# 分层世界生成）

> **路径注记（2026-10-06 目录治理后）**：本文写作时新项目主链位于 `scripts/worldgen/`。
> A/B/C 三步目录治理已完成，当前实际路径为 `scripts/WorldGen/` 六子层
> （`Placement` / `Features` / `Discretization` / `Final` / `Simulation` / `Composition`），
> 其中 `WorldGenMapModes.cs` → `Render/Modes/`、
> `RiverLineOverlay.cs` / `SnowOverlay.cs` → `Render/Overlays/`。
> 文中代码位置一律为 `world` 仓库的相对路径，请按上述新路径查找；
> 表格中标注 `worldgen/` 的是**写作当时的路径写法**，为便于对照未逐行重写。

- 日期：2026-10-06
- 范围：`E:/godotGames/strategy`（旧） → `E:/godotGames/world`（新）
- 性质：**纯分析，未改动任何代码文件**
- 基线：`world` @ `2c67bad`（main 分支，304 commits）

---

## 0. 一个必须先说清的口径问题

`E:/godotGames/` 下有 6 个 Godot 相关工程，容易混淆。经时间线与内容比对，本次对照的两个对象是：

| 项目 | 角色 | 依据 |
|---|---|---|
| **`strategy`** | **旧项目** | 提交区间 2026-07-21 ~ 07-24（13 commits，末条 `aae64b5`）。内容为 GDScript 星球板块构造原型，git 历史中含 `plate_tectonics.gd` / `plate_forces.gd` / `particle_system.gd` |
| **`world`** | **新项目** | 提交区间 2026-07-31 起至今（304 commits，远程 `github.com/jsj8051/world`）。Godot 4 + C#/.NET 8，五层架构 |

> ⚠️ **旧项目工作区已被清空**：`strategy/` 当前只剩 `project.godot` + `icon.svg` + `.vscode/`，全部 15 个 `.gd`（约 2280 行）与 4 个设计文档**只存在于 git 历史**（`git show aae64b5:<path>`）。本清单的所有旧项目代码位置均以 `aae64b5` 的历史路径为准。

排除的干扰项：`world-create`（GDExtension C++，仅 3 文件 453 行，只有一个 `Icosahedron` + `TerrainWorld`）、`dungeon-explorer`（地牢探索）、`sotE`（SotE 独立分支）、`data-editor`（Python web）。

> **注意**：另有一个**同名易混点**——`world` 自己的 `docs/tectonics-port.md` 记录的"板块构造移植"对象是第三方 JS 库 `tectonics.js`（CC-BY-4.0），**不是** `strategy`。该项目 2026-09-30 已把板块代码全删，只留 17 篇 `docs/地壳运动/` 档案。详见 §5.1。

---

## 1. 旧项目模块划分与职责

`strategy` 是**单场景、单目标的物理原型**：给定种子 → 生成 N 个板块 → 按受力算角速度 → 质心旋转 + 格子重分配 → 边界类型判定 → 粒子级地壳增删 → 边界处堆出地形。核心是一次"板块会动、地形跟着变"的定性验证，不追求生产规模。

| # | 模块 | 职责 | 关键文件（`strategy` @ `aae64b5`） | 行数 |
|---|---|---|---|---|
| L1 | **网格与数学底座** | 经纬规则格 ECS 容器（SoA 组件）+ 球面三角函数 | `script/core/planet_grid.gd`<br>`script/core/planet_math.gd` | 202<br>71 |
| L2 | **板块生成** | 递归大圆弧切割（按板块面积加权随机选板，穿过质心画大圆弧） | `script/core/plate_generator_recursive.gd` | 94 |
| L3 | **板块数据** | 板块对象：omega / 质心 / 面积 / 地壳年龄 / 地壳厚度 / 密度 / 边界 id | `script/tectonics/plate.gd` | 47 |
| L4 | **板块受力** | 四种力：洋脊推力、板片拉力、地幔拖曳阻尼、碰撞阻力（SI 单位，公式 2–5） | `script/tectonics/plate_forces.gd` | 123 |
| L5 | **边界图** | 邻接表建边界（右邻 + 下邻异板块对聚合）、切向法向、中位数中点 | `script/tectonics/boundary_graph.gd` | 214 |
| L6 | **边界对象** | 6 态状态机容器（RIFTING/SPREADING/MATURE/SUBDUCTING/CLOSING/SUTURED）+ ODE 桩 | `script/tectonics/boundary.gd` | 59 |
| L7 | **线性求解器** | 稠密 Cholesky 分解解 `K·Ω = F`（+ 共轭梯度备用） | `script/solver/linear_solver.gd` | 133 |
| L8 | **板块主控** | 装配 `3N×3N` 刚度矩阵 + `3N` 外力力矩 → 求解 omega → 归一化 → 自适应子步长推进 | `script/tectonics/plate_tectonics.gd` | 550 |
| L9 | **粒子系统** | 地壳碎片示踪点：漂移、空间哈希、边界检测、**裂谷/俯冲地壳增删**、年龄累积、均衡补偿 | `script/particle/particle_system.gd` | 587 |
| L10 | **并查集** | 板块连通性（为阶段 4 合并准备） | `script/core/union_find.gd` | 62 |
| L11 | **演示/可视化** | 4 个覆盖层（地形/边界/质心/法向/中点）+ 5 种着色模式 + 键盘仿真控制 | `script/scene/planet_demo.gd` | 636 |
| L12 | **相机** | 球体环绕相机（拖拽旋转 + 滚轮缩放） | `script/scene/camera_orbital.gd` | 70 |
| L13 | **球壳网格** | UV 球面 mesh 生成 | `script/scene/planet_sphere.gd` | 22 |

**旧项目完成度自评**：按其 `docs/superpowers/plans/` 的 5 阶段规划，**阶段 0–2.5 已实现**（结构化板块、边界图、力矩平衡、质心旋转重分配），**阶段 3–5 未实现**（边界 ODE 演化、板块合并/裂解、真实地形替换）。代码里留了大量"阶段 N 完整实现"的空桩与注释。物理标定也未做——`OMEGA_SCALE = 1e16` 是把真实 `rad/s` 硬放大到可见量级的技巧，`TARGET_MAX_OMEGA = 0.3` 同样是"让画面能动"的工程值。

---

## 2. 逐项功能对照总表

> 图例：✅ 已实现 ｜ 🟡 部分实现 ｜ ❌ 未实现 ｜ ⛔ 已废弃

### 2.1 板块构造与运动（strategy 的核心，world 完全没有）

| # | 旧项目功能 | 旧代码位置 | 新项目状态 | 新代码位置 | 差异点 / 依赖 / 难度 |
|---|---|---|---|---|---|
| 1 | 板块划分（Voronoi/递归切割产生离散板块） | `core/plate_generator_recursive.gd` | ❌ | — | 新项目**不做板块身份**。海陆由 `ContinentLayout` 蓝噪声锚点 + `LandSeaField` 连续场 + 分位切分决定（`worldgen/ContinentLayout.cs` / `LandSeaFields.cs` / `H3LandSeaProjector.cs`）。**范式不同，非缺陷**：新项目要的是"大陆长什么样"，旧项目要的是"大陆为什么在动"。<br>难度：**中**。若要引入板块身份需在 Placement 之上加一层，且与"连续场定海陆"口径冲突 |
| 2 | 板块角速度求解（力矩平衡 `K·Ω = F` + Cholesky） | `tectonics/plate_tectonics.gd:203-292`、`solver/linear_solver.gd` | ❌ | — | 全库 grep `Plate`/`omega`/`torque`/`Cholesky` **零命中**。<br>难度：**高**。依赖项 3–6 全部要先有 |
| 3 | 四种受力（洋脊推力 / 板片拉力 / 地幔拖曳 / 碰撞阻力） | `tectonics/plate_forces.gd`（公式 2–5，含 SI 常量表） | ❌ | — | 新项目**无任何受力模型**。`TectonicField` 只做静态强度场（`IntensityAt(dir)` 返回 [0,1]），不是力。<br>难度：**高**。`slab_pull` 依赖 `boundary.slab_length`，而 `boundary.gd:44` 的 `update_ode()` **本身是空桩** → 力模型的输入不存在 |
| 4 | 边界图（邻接表 + 切向法向 + 中点） | `tectonics/boundary_graph.gd` | ❌ | — | 无对应物。新项目最近的类是 `BasinGraph`（水系拓扑，`worldgen/BasinGraph.cs`），语义完全不同（"水汇向哪" vs "板块交界在哪"）。<br>难度：**中**。算法本身可移植，但见风险 R2 |
| 5 | 边界状态机（6 态：裂谷/扩张/成熟/俯冲/闭合/缝合） | `tectonics/boundary.gd:5-11` | ❌ | — | 枚举与逻辑均只在旧项目。<br>难度：**高**。状态机本体的 `check_transition()` / `update_ode()` 在旧项目**就是空桩**（`boundary.gd:44,49`），迁过去也只是迁一个枚举壳 |
| 6 | 板块运动推进（质心四元数旋转 + 格子重分配） | `tectonics/plate_tectonics.gd:56-142` | ❌ | — | 新项目**无时间维度**。`Regenerate()` 一次性算出静态世界，无 tick 循环（`worldgen/WorldGenPlanet.cs:92-179`）。<br>难度：**高**。新项目整体是"给定种子 → 静态快照"架构，接时间步需要先引入状态持久化范式 |
| 7 | 地壳物质属性（厚度 / 密度 / 年龄） | `core/planet_grid.gd:28-32`、`tectonics/plate.gd:13-17` | ❌ | — | 全库无地壳物质场。存档段 `SOIL`/`MINE` 字段是**旧档遗留，生成器已删**。<br>难度：**高**。这是岩石圈物理的载体，缺它则 3/9 都无法真跑 |
| 8 | 岩石圈均衡补偿（isostasy） | `particle/particle_system.gd:22` `ISOSTASY_FACTOR = 1-ρc/ρm` | ❌ | — | 无。<br>难度：**中**。概念简单，但需要地形—密度耦合通道 |
| 9 | 地壳增删（裂谷造新壳 / 俯冲消减） | `particle/particle_system.gd:364-440` | ❌ | — | 无。旧项目靠"粒子注入/移除 + 物质搬运"实现。<br>难度：**高** |
| 10 | 地壳年龄累积 | `particle/particle_system.gd:493` `_advance_age` | ❌ | — | 无。<br>难度：**中** |

### 2.2 地形与地貌（两边都有，思路不同）

| # | 旧项目功能 | 旧代码位置 | 新项目状态 | 新代码位置 | 差异点 / 依赖 / 难度 |
|---|---|---|---|---|---|
| 11 | 地形网格生成 | `plate_tectonics.gd:499-538` `_compute_terrain`（随机基准 + 边界高斯隆起） | ✅（更完备） | `worldgen/HeightComposer.cs` + `MountainSkeleton.cs` + `RegionalLandforms.cs` + `VolcanoField.cs` | **新项目大幅超越**。旧项目只有"边界附近隆起"一种机制；新项目有 5 类 Feature 走 `ITerrainField` 契约合成（后位覆盖先位）。<br>无迁移需求 |
| 12 | 造山/地形响应 | `particle_system.gd` `UPLIFT_RATE` | 🟡 | `worldgen/MountainSkeleton.cs`（398 行） | 新项目有山脉骨架（主脊样条 + 支脉树 + 高斯包络 + 排斥场），但是**静态摆放**，无 `uplift` 时间演化、无均衡。形态学更精致，物理更弱。 |
| 13 | 火山 | ❌（旧项目**没有**火山） | ✅（有缺陷） | `worldgen/VolcanoField.cs` | **旧项目完全无此功能**。新项目有径向锥火山 Feature。⚠️ 已知缺陷 D-12：res1=1/res2=0/res3=1/res4=0 个，数量跨 res 不稳。**无可迁移内容** |
| 14 | 高原/盆地 | ❌ | ✅ | `worldgen/RegionalLandforms.cs` | 旧项目无。**无可迁移内容** |
| 15 | 地质区域分区 | ❌ | ✅ | `worldgen/GeologicalRegions.cs`（444 行，7 种区域类型） | 旧项目无。**无可迁移内容** |

### 2.3 空间网格与底座

| # | 旧项目功能 | 旧代码位置 | 新项目状态 | 新代码位置 | 差异点 / 依赖 / 难度 |
|---|---|---|---|---|---|
| 16 | 球面网格 | **经纬规则格** `planet_grid.gd:44-58`（`lat×lon`，`neighbors()` 4 邻域，**极点不环绕**） | ✅（不同） | **H3 六边形** `new_HexWorld/Ball/Ball.cs` + `Utils/H3/H3.cs` | **架构级不兼容，是全部迁移难度的根源**。经纬格在极点退化、格子物理尺寸随纬度剧烈变化；H3 六边形无极点奇点、6 邻域。旧项目代码里 `planet_forces.gd:_boundary_length_m` 专门用 `cos(lat)` 修正格长——这类纬度补丁在 H3 上**全部不需要**。 |
| 17 | 格边长/面积口径 | `planet_grid.gd` 隐式（含 `cos(lat)` 修正） | ✅（更严格） | `worldgen/SpatialScale.cs`（179 行，四口径：等积面积 / haversine 距离 / `CellEdgeKm`） | 新项目把口径显式化为单一真相源。**迁旧代码时不要迁它的 `cos(lat)` 修正**，那是被新项目 `SpatialScale` 取代的东西 |
| 18 | 确定性随机 | `RandomNumberGenerator` + `rng.seed`（GDScript 内置，非确定性序列保证） | ✅（更严格） | `Utils/DeterministicRandom.cs`（SplitMix64）+ `worldgen/SeedDerivation.cs`（魔数表） | 新项目随机纪律是硬约束（`SeedDerivation.Tags_AreUnique` 测试钉死）。迁移时**不能照抄 `rng.seed`**，须换成 `DeterministicRandom` + 派生子流 |
| 19 | 球面三角/网格工具 | `core/planet_math.gd`（lat/lon↔cartesian、角距离、UV 球生成） | ✅ | `new_HexWorld/Ball/Ball.cs`、`HexPlanet/*` | `angular_distance` 已被 `SpatialScale.DistanceKm`（haversine）取代。**无可迁移需求** |

### 2.3b 离散化桥（**迁移的关键接缝，此前遗漏**）

| # | 旧项目功能 | 旧代码位置 | 新项目状态 | 新代码位置 | 差异点 / 依赖 / 难度 |
|---|---|---|---|---|---|
| 19b | **连续场 → H3 逐格离散化** | ❌（旧项目**没有这一层**：`planet_grid.gd` 是规则格，`_compute_terrain` 直接遍历格子写 `elevations`） | ✅ | `worldgen/H3TerrainSampler.cs`（`SampleField(SphericalField, Mode)`，`CenterWeight=0.5` 默认格心+6角点加权） | **这是迁移方案里最重要的一块新项目资产，此前遗漏。** 它是"连续世界"与"H3 世界"之间**唯一的桥**（文件头注释原话），设计目的是让场本身不知道 H3 存在 ⇒ 换 res 不需要动生成算法。<br>⚠️ **生产消费者只有 `H3LandSeaProjector.cs:55`**（海陆场离散化）；`HeightComposer` / `MountainSkeleton` 等地形特征**不走**它。<br>迁移含义：板块构造若要接入，**正确形态是"板块运动产出一个 `SphericalField`，再经 `H3TerrainSampler` 落到 H3"**，而不是旧项目那种"遍历格子直接写 elevations"。这恰好是绕开风险 R1（Final 被上游回写）的合法路径。 |

### 2.4 大气圈 / 气候 / 水文（旧项目完全没有）

| # | 旧项目功能 | 旧代码位置 | 新项目状态 | 新代码位置 | 差异点 / 依赖 / 难度 |
|---|---|---|---|---|---|
| 20 | 气温 | ❌ | ❌ | — | 新项目也无（`BiomeColors.TemperatureToColor` 仅色带，存档 `TEMP` 段字段是旧档遗留） |
| 21 | 降水 | ❌ | 🟡 | `worldgen/PrecipitationModel.cs` | 新项目是纬度带 LUT × 海岸加权，注释自陈"将来气候模拟上线后被替换" |
| 22 | 生物群系 | ❌ | 🟡（仅词汇） | `Domain/BiomeType.cs`（Köppen 24 值） | **只有枚举无生成器**（旧 `BiomeClassifier` 已清退）。迁移时易误判为"有能力" |
| 23 | 河流/水文 | ❌ | ✅（冻结） | `worldgen/RiverNetwork.cs` / `RiverGraph.cs` / `RiverGeometry.cs` / `RiverPresentationSpline.cs` / `HydrologyRoutingSurface.cs` / `H3Hydrology.cs` | 新项目独有，且已冻结（River v2.0 三层契约）。**旧项目无任何可迁内容** |
| 24 | 湖泊/流域 | ❌ | ✅ | `worldgen/LakeState.cs` / `BasinGraph.cs` / `WaterTopology.cs` | 同上 |
| 25 | 侵蚀/沉积 | ❌（仅 `BinaryMorphology` 概念） | ❌ | — | 两边都无。旧项目 `particle_system` 的地壳搬运不是侵蚀。⚠️ 勿误认为缺口——新项目已明确冻结不做 |
| 26 | 风场/洋流 | ❌ | ❌ | — | 两边都无（旧 `WindField`/`OceanCurrent` 已随 world 的 A 线清退） |

### 2.5 表现层与工具

| # | 旧项目功能 | 旧代码位置 | 新项目状态 | 新代码位置 | 差异点 / 依赖 / 难度 |
|---|---|---|---|---|---|
| 27 | 球体渲染 | `scene/planet_sphere.gd` + `planet_demo.gd:253-337` | ✅（更完备） | `Render/BallView.cs`（402 行，LOD 分块 + 背面剔除 + 颜色纹理）+ `shaders/` | 新项目是单半径 Unshaded 平光 + 122 基块 × 2 档 LOD。旧项目是直接 mesh 三角化。<br>**无迁移需求** |
| 28 | 叠加覆盖层（多图层同时显示） | `planet_demo.gd` 4 层叠加（边界/质心/法向/中点）+ 5 色模式 | 🟡（精简） | `Render/MapMode.cs` + `worldgen/WorldGenMapModes.cs`（**仅 4 模式**：海拔/海陆场/地质区域/离岸距离）+ `Render/UI/MapDock.cs` | 新项目砍掉了旧世界线的 20 图层查看器（2026-10-02）。<br>⚠️ 但**旧项目的板块可视化图层（边界线/法向/中点/质心）**在旧项目清退后**已无对应物**——若将来要可视化板块，需要新建图层，不是"迁移" |
| 29 | 轨道相机 | `scene/camera_orbital.gd`（70 行） | ✅（更完备） | `Camera/OrbitalCamera.cs`（139 行，含 `SetPlanetRadius`） | 新项目额外处理了按星球半径缩放轨道与裁剪。**无可迁移需求** |
| 30 | 仿真时间控制（播放/暂停/步进） | `planet_demo.gd:51-53` `_sim_running` / `_sim_time` / `_sim_step_count` + `_unhandled_input` | ❌ | — | 新项目无时间控制（无仿真循环）。<br>难度：**低**（UI 层面），但**依赖项 2/6 先有**才有意可指 |
| 31 | 离线批处理 | `planet_demo.gd:84` `_run_offline_batch` | ✅ | `tests/PerfBench/Program.cs`（`--d11`/`--probe`/`--converge`） | 能力已由测试工具链覆盖 |
| 32 | 截图/像素级诊断 | ❌ | ✅（新项目独有） | `Utils/PngWriter.cs` + `Diagnostics/RiverShotDiag.cs`（1159 行） | **新项目远超旧项目**。无迁移需求 |
| 33 | 存档/序列化 | ❌ | ✅（新项目独有） | `Archive/MapArchive.cs`（`.mpa` v9，17 段）+ `LogicGrid/GameMapArchive.cs`（`.gmp` v3）+ `CivSim/Archive/CivMapArchive.cs`（`.cmp` v17） | 旧项目无持久化。⚠️ 新项目 worldgen 主链**不写档**（见风险 R4） |
| 34 | 测试框架 | ❌（**零测试**） | ✅ | `tests/World.Tests/`（47 文件）+ `World.Tests.Local/`（反射执行器 + 看门狗）+ `ArchitectureContractTests.cs`（22 条依赖契约） | **新项目独有且强**。迁移旧代码时**必须**为新模块补契约测试，见风险 R1 |

### 2.6 人文模拟与游戏层（旧项目完全没有）

| # | 旧项目功能 | 新项目状态 | 新代码位置 |
|---|---|---|---|
| 35 | 文明演化 | ✅（62 文件 / 22 机制） | `scripts/CivSim/`（`Engine/CivEngine.cs` 等） |
| 36 | 玩家命令层 | ✅（逻辑有、**UI 未做**） | `scripts/Gameplay/`（3 文件；`SaveArchive.cs` 已清退） |
| 37 | 人口/物种适宜性 | ✅ | `Domain/WildCropsSystem.cs` |
| 38 | 玩家存档 `.sav` | ⛔ | `UserPaths.SaveDir` 仍在，但 `SaveArchive.cs` 已清退 |

---

## 3. 汇总统计

| 状态 | 数量 | 项目 |
|---|---|---|
| ✅ 已实现（新项目更强或旧项目没有） | 8 | 地形合成 11、网格 16、口径 17、随机 18、数学 19、渲染 27、相机 29、离线批处理 31 |
| ✅ 新项目独有（旧项目完全没有） | 8 | **离散化桥 19b**、诊断 32、存档 33、测试 34、文明 35、玩家层 36、物种 37、玩家存档 38 |
| 🟡 部分实现 | 5 | 造山 12、火山 13、生物群系 22、降水 21、叠加层 28 |
| ❌ 未实现（旧项目有、新项目无） | 12 | 板块划分 1、角速度求解 2、受力 3、边界图 4、状态机 5、运动推进 6、地壳物质 7、均衡 8、地壳增删 9、年龄 10、时间控制 30 |
| ⛔ 已废弃 | 1 | 玩家存档 38 |

> **新增一行意味着风险 R1 的对策变了**：原报告说"让 `HeightComposer` 通过 `ITerrainField` 消费"，但板块运动是**多步过程**，Feature 契约是**单次采样的静态场**，二者不同构。真正对得上的接缝是 `H3TerrainSampler`（19b）——但它的生产消费者目前只有海陆场，**尚无"多步状态场 → H3"的先例**，需在 P0 决策时一并考虑。

### 3.1 `scripts/` 覆盖度自检

新项目 `scripts/` 共 **14 个一级目录 / 37 个目录 / 151 个 `.cs`**（已排除 `obj/`、`bin/`）。本报告覆盖情况：

| 目录 | .cs | 覆盖 | 目录 | .cs | 覆盖 |
|---|---|---|---|---|---|
| `worldgen/` | 33 | ✅ 全覆盖 | `CivSim/` | 62 | ✅ 按子域归类（Engine/Entities/Mechanics/Archive/Observation/Policies/Tables/Concepts/Support/Events） |
| `Utils/` | 9 | ✅ 全覆盖 | `Diagnostics/` | 18 | ✅ 全覆盖 |
| `Domain/` | 5 | ✅ | `HexPlanet/` | 5 | ✅ 标为旧线遗留 |
| `Render/` | 5 | ✅ 含 `UI/` | `Archive/` | 2 | ✅ |
| `Gameplay/` | 3 | ✅ | `Services/` | 2 | ✅ |
| `new_HexWorld/` | 1 | ✅ `Ball/Ball.cs` | `Camera/` | 1 | ✅ |
| `PlanetLOD/` | 1 | ✅ 标为旧线遗留 | `Surface/` | 1 | ✅ 标为旧线遗留 |

**勘误记录**（初版三处数字有误，已在正文修正）：初版称 `worldgen` 34（实 33）、`Diagnostics` 15（实 18）、`HexPlanet` 4（实 5）；且初版**遗漏** `worldgen/H3TerrainSampler.cs` 与 `Utils/CoordUtil.cs` 两个文件——后者已补入风险 R4。

**关键结论**：**旧项目 `strategy` 的全部业务价值集中在"板块会动"这一件事上，而这正是新项目完全没有、且当前架构不打算做的部分。** 其余 12 个模块新项目要么更强、要么无关。

---

## 4. 迁移优先级建议

> 排序依据：先做"能独立验证的地基"，再做"依赖它的机制"，最后做"需要架构决策的"。**不建议整体照搬旧项目**——它是原型，物理未标定，多处逻辑本身是空桩。

| 优先级 | 事项 | 理由 | 前置依赖 | 难度 |
|---|---|---|---|---|
| **P0（先决策，不写代码）** | 决定新项目**要不要**板块构造 | 这是**架构级决策**，不是移植题。两条路：① 维持现状（`TectonicField` 只当"山脉选址原因"，板块身份永不引入）——与新项目现有五层架构自洽；② 引入板块身份——需重开 Placement 层，且与"连续场定海陆"口径冲突，需先解决二者谁是真相源 | 无 | 决策成本 ≫ 实现成本 |
| **P1** | 若决定引入，**先只迁 3 个纯工具类** | `UnionFind`（62 行）、`LinearSolver`（133 行，Cholesky + CG）、`BoundaryGraph` 的建图算法。这三者**无板块物理依赖、无网格依赖**，可独立移植 + 独立测试。旧项目这 262 行代码质量是好的（含"力矩单位 N·km vs 阻尼 N·m·s"这类注释级别的单位警觉） | P0 决策 | **低** |
| **P2** | 迁网格无关的**受力公式** | `PlateForces` 公式 2–5 + SI 常量表。**但必须先补 `Boundary.update_ode`/`check_transition`（旧项目是空桩）**，否则 `slab_pull` 无输入、`collision_resist` 无 `v_normal`。等于"迁公式 + 自己补状态机" | P1 | **中** |
| **P3** | 网格层适配（**工作量最大、被低估的一项**） | 经纬格 → H3 六边形。需要重写：`neighbors()` 4→6 邻域、`plate_centroid()`（旧实现 O(N) 两次遍历，H3 下 N=288,122 会爆）、`_reassign_cells_to_plates()`（O(N·P) 点积，res4 下 ~3.5M 次/步）、`_segment_midpoint()` 的经度算式。**旧项目所有 `cos(lat)` 长度修正须丢弃**（H3 六边形无极点奇点、格边长近等积）。<br>✅ **好消息**：`H3TerrainSampler`（§2.3b / 19b）已把"连续场 → H3 逐格"这一步抽象好了，板块构造只需产出 `SphericalField`，**不必自己碰 H3 遍历**。这把 P3 从"整个网格层重写"收缩为"只需重写拓扑类（centroid / 邻居 / 边界建图）" | P1 | **中高**（原估"高"，修正为中高） |
| **P4** | 决定是否保留"质心旋转 + 格子重分配"范式 | 旧项目的聪明之处：只旋转 12 个质心、格子不动再按最近质心重分配 ⇒ 网格永远规则无缝、边界自然移动。代价是**格子不携带物质身份**（这正是它做不了真实岩石圈的原因）。新项目若要更物理，应改走"格子随板块刚体旋转"（tectonics.js 路线）。**这是范式选择，不是移植** | P3 | **高** |
| **P5** | 边界可视化图层 | 若 P0 决定"要"，则需新建（不是迁移）——新项目渲染层已砍掉多图层机制，得挂在 `MapMode` 上或走 `BallView` 颜色纹理通路 | P0 | 中 |
| **P6** | 时间控制 UI | 纯粹是 UI 层，但**有意可指的前提是有仿真循环**（P4）。不要先做 | P4 | 低 |
| ❌ **不建议做** | 板块身份直接决定海陆 | 旧项目 B 线（`NoiseWorld`）曾用"板块身份定海陆"范式，world 已把它判为缺陷并清退（`ArchitectureContractTests.FinalGeography_DoesNotReferencePlacementProjector` 钉死）。**不要在板块层重建它** | — | 禁止 |

---

## 5. 需要注意的风险点

### R1 · 迁移会绕过新项目的架构护栏（**最高风险**）

新项目有 22 条 `ArchitectureContractTests` 反射级依赖契约，其中多条直接约束板块相关行为：

- `Hydrology_DoesNotDependOnSpatialIndexOrPlacement` — 水文只准读 Final 事实。板块若接进 Placement，**可能让海洋陆块在跨 res 下"各自移动"，从而使水文诊断失稳**
- `FinalGeography_DoesNotReferencePlacementProjector` — Final 是唯一世界事实源
- `HeightComposer_DoesNotReferenceConcreteFeatures` — 合成器不认识具体特征

**风险**：旧项目 `plate_tectonics.gd` 的 `_compute_terrain()` **直接写 `grid.elevations`**，即"板块运动直接改地形"。这在新项目是**明确禁止**的方向（Final 层不可被上游回写，参见水文路由层"填洼绝不回写 FinalHeight"的永久契约）。
**对策**：把板块构造放在 Placement 层，产出"构造强度/板块年龄"这类**场**，让 `HeightComposer` 通过 `ITerrainField` 消费，**或**让板块运动产出 `SphericalField` 后经 **`worldgen/H3TerrainSampler.cs`** 离散化落盘 —— 二者都不是"运动代码直接写 Final 高度"。
**这会显著改变迁移形态——迁过来的是"场的生产方式"，不是"计算过程"。**
（⚠️ 见 §2.3b 第 19b 项：`H3TerrainSampler` 是新项目已有的、专门为这件事准备的桥，旧项目完全没有对应物。）

### R2 · 旧项目多处逻辑本身是空桩，别把桩当实现

| 位置 | 状态 |
|---|---|
| `boundary.gd:44` `update_ode()` | `pass`（空） |
| `boundary.gd:49` `check_transition()` | 直接 `return type`（恒等） |
| `plate.gd` `merge()` | 注释明写"阶段 4 完整实现，此处基础版" |
| `PlateGeneratorRecursive` | 切割时 `plate_sizes` 与 `active_plates` 有同步 bug（`append` 后又 `has` 检查再 `append`），多次切割后 `plate_sizes` 与真实归属可能失配 |
| `plate_tectonics.gd:159-160` | 注释自陈"每步重算地形会清掉演化，阶段 2.5 暂接受这个简化（地形不演化）" |

**风险**：把 `update_ode`/`check_transition` 当"已实现的机制"迁过去，实际只得到一个枚举壳 + 恒等函数，运行时表现为"边界永远不变"，但看起来"接上了"。
**对策**：逐条标注"迁的是桩还是实现"，P2 阶段明确把两个空桩列为**需自己补**的工作项。

### R3 · 物理量级未标定，迁过来会"能动但不对"

旧项目两处硬编码放大：
- `OMEGA_SCALE = 1e16`（把真实 `rad/s` 的 ~1e-16 放大到可见量级）
- `TARGET_MAX_OMEGA = 0.3`（求解后再归一化到这个上限）

且 `plate_tectonics.gd:262-263` 有已注明的单位不一致：`r` 用 km、力用 N、力矩 N·km，而刚度矩阵是 N·m·s。

**风险**：这套代码**能跑出画面**恰恰因为它放弃了绝对数值真实性。迁到新项目后，若下游（地形/水文）开始消费这些量，量纲错误会静默传播。
**对策**：迁移时要么保留放大系数并在文件头显式标注"定性演示，不可作物理量引用"，要么老老实实按 SI 实现并接受"几乎不动"（那是真实的地球）。

### R4 · 新项目侧的既有债务会叠加

- **D-12 火山缺陷未修**（res1=1/res2=0/res3=1/res4=0）。`VolcanoField.Place` 接收 `TectonicField` 参数，但**火山放置是 `VolcanoField` 自持的**（`TectonicField` 只提供强度查询）。若把板块强度场喂进火山放置，会在 D-12 未修的前提下叠加新变量，导致归因困难。**先修 D-12 再动火山**（且注意 0D 点特征应用"个数"统计量，不用跨 res 收敛框架）
- **`SnowOverlay` 未接线**（`worldgen/SnowOverlay.cs` 存在，但 `WorldGenPlanet.Regenerate()` 从不构造；仅 `tests/World.Tests/ArchitectureContractTests.cs:51,192` 白名单 + `tests/World.Tests.Local/Program.cs:327,552` 构造测试夹具）
- **`CoordUtil` 逆映射已删**（`Utils/CoordUtil.cs` 末行注释："`SphereToLatLng` 已删：v1.7 平流改一步一格 CA 后无调用者"）。只剩 `LatLngToSphere` 正向，7 个调用点（`Ball.cs:63,68` / `BallView.cs:226` / `CellQuery.cs:57,135` / `CellHighlightDiag.cs:75`）。⚠️ 若板块构造需要"从格子反查经纬度"，得自己补逆映射——**别假设它还在**
- **两套球面网格并存**：`worldgen` 走 H3（`Ball`），而 `LogicGrid`/`CivSim`/`HexPlanet` 走 Goldberg（`10n²+2`），两者**未打通**。若板块数据要喂给 CivSim，会立刻撞上这个断层
- **worldgen 主链不写档**：`Regenerate()` 无任何存档调用（worldgen 的 `using` 里无 `World.Archive`）。板块运动是多步过程，不落档意味着**无法保存/复现中间态**——迁移前需先决定快照策略

### R5 · 术语同名不同物（沟通事故高发区）

| 旧项目术语 | 新项目同名物 | 区别 |
|---|---|---|
| `Boundary`（板块边界） | `Boundary` / `Edge` | 新项目**无板块边界**概念；其 `FinalSpatialIndex` 的 within 查询、测试里的 boundary 均指"格邻居边界" |
| `CrustType`（地壳类型） | `CrustThinning` 等 | 新项目 `Plateau`/`Basin` 等是**地貌区**，非地壳物质 |
| `terrain`（板块运动直接写出的高程） | `HeightComposer`（多 Feature 合成） | **写入者 vs 合成者**的根本差异，见 R1 |
| `omega` | 无 | 旧项目有，新项目无 |
| `plume` / `hotspot` | `VolcanoField` | 概念相近但新项目是径向高斯锥，无热柱/岩石圈成因 |

**对策**：迁移文档与代码注释里**统一加旧项目前缀**（如 `st:` / `LegacyPlate`），避免与新项目同名类型混读。

### R6 · 旧项目许可证与来源

`strategy` 是本仓库自有代码，**无第三方许可约束**。但需注意：world 的 `docs/tectonics-port.md` 记录了**另一条**第三方路线（`tectonics.js`，CC-BY-4.0，58 个 JS 文件在 `docs/tectonics-ref/`）。若后续板块构造改走 tectonics.js 路线而非 strategy 路线，则**署名义务激活**（需保留来源标注）。两条路线的代码**不可混用**（`strategy` 是"质心旋转+格子重分配"，tectonics.js 是"格子随板块刚体旋转"）。

---

## 6. 一句话结论

旧项目 `strategy` 是一个**已验证"板块会动"的物理原型**，而新项目 `world` 是一个**刻意的静态分层世界生成器**。二者**不是"旧版与新版"，而是两个不同目标的项目**。

`strategy` 的 12 项"新项目没有"的能力里，**9 项依赖一个当前不存在的前提（板块物质身份）**、**2 项本身在旧项目就是空桩**、**1 项是低价值 UI**。真正值得迁移的是 **3 个纯工具类（约 262 行）+ 1 套受力公式**，而它们只在 P0 决策"新项目要引入板块构造"之后才有意义。

**在那个决策做出之前，建议不做任何代码迁移**——按项目永久原则第 1 条（防膨胀三问）之第三问：**现在有真实消费者吗？答案是没有。**

---

*本清单为纯分析产物，未创建/修改/删除任何代码文件。旧项目代码位置以 `strategy` @ `aae64b5` 历史路径为准（工作区已清空，需 `git show aae64b5:<path>` 取回）。*
