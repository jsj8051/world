# world 项目架构文档（世界生成空间 v1）

> 本文档是项目的"宪法"：定义分层、依赖规则与工程纪律。
> 新代码违反本文件即视为架构债，review 时一票否决。
> 更新本文档需走 ADR（`docs/decisions/`）或决策文档（`docs/newdecision/`）。

> ★**2026-10-05 整篇重写**。旧版描述的是**已删除**的架构（`MapGen` / `Biome` / `MapView` /
> `Tectonics` 四个命名空间、L0-L3 四层、`PlanetPipeline` 五阶段），那些代码在决策 07 §10
> 与决策 08 §4 已全部清退。**架构文档整篇描述已删类型= 本仓库最大的文档断层**，本次修正。

---

## 1. 项目是什么

Godot 4.7（C# / .NET 8）程序化行星生成 + 文明演化模拟。
**世界生成**在 H3 六边形球面网格上**确定性**运行，产出分层事实（陆块 / 海岸 / 区域 / 海拔），
再由水文、气候等下游系统消费；表现层只画，不生成。

主入口场景：`scenes/core/WorldGenWorld.tscn`。

---

## 2. 世界生成五层（v1 冻结面 · 永久）

```
Placement（生成依据）→ Final（世界事实）→ SpatialIndex（查询）→ World Simulation（世界如何运行）→ 表现层（怎么画）
```

| 层 | 回答 | 代表类型 | 命名空间 |
|---|---|---|---|
| **Placement** | 世界如何生成 | `ContinentLayout` / `LandSeaField` / `H3LandSeaProjector` / `SurfaceResolver` / `GeologicalRegions` / `TectonicField` / `MountainSkeleton` / `RegionalLandforms` / `VolcanoField` / `HeightComposer` | `World.WorldGen` |
| **Final** | 世界究竟是什么 | **`FinalGeography`**（FinalLand / FinalLandmassId / FinalDistToCoast / FinalRegionOfCell） | 同上 |
| **SpatialIndex** | 如何高效询问 | **`FinalSpatialIndex`**（nearest / distance / within） | 同上 |
| **World Simulation** | 这个世界如何运行 | `PrecipitationModel` → `RiverNetwork` → `RiverGraph` / `BasinGraph` / `LakeState` → `WaterTopology` | 同上 |
| **表现层** | 怎么画 | `BallView` / `MapMode` / `CellQuery` / `MapDock` / `CellInfoCard` / `RiverLineOverlay` | `World.Render` / `World.Render.UI` |

装配序（依赖序）唯一接线点：`worldgen/WorldGenPlanet.Regenerate()`。

**冻结日期 2026-10-03**（用户拍板 "Terrain Genesis Architecture v1 冻结"）。
详见 `docs/newdecision/设计-世界生成空间-08-架构冻结与旧线清退.md`。

### 2.1 Field / Feature / Morphology 三层

| 层 | 回答 | 不回答 |
|---|---|---|
| **Field** | 哪里容易发生什么 | 任何形状 |
| **Feature** | 具体生成了什么（离散对象，自带尺度 / 走向 / 影响场） | 为什么长在这里 |
| **Morphology** | 这东西具体长成什么样 | — |

组合器只认 `ITerrainField` 口；高度合成是**绝对高度 lerp**
（`FinalHeight = lerp(当前, TargetM, Influence)`），**不是 "+N 米增量"**。

### 2.2 四个水系统概念不互相吞并（永久）

`RiverGraph`（河流内部拓扑）/ `BasinGraph`（流域归属与终止，**不判湖**）/
`LakeState`（湖泊状态）/ `WaterTopology`（水体之间的连接）。

---

## 3. 依赖规则（宪法条文 · 仍然有效）

1. **依赖只向下**：表现层 → World Simulation → SpatialIndex → Final → Placement → 基础设施。
   反向引用（模型里出现 UI、Final 里读 Placement）禁止。
2. **Final 层是唯一世界事实源**。下游（World Simulation / 表现层）**只消费 Final**，
   不看生成依据。`FinalGeography` 不得引用任何 Placement 投影器或具体 Feature 类。
3. **允许 Godot 数学类型**（`Vector3` 等值类型），纯 C# 更佳——可测试性是硬指标。
4. **单类单文件**；拆大类用 `partial` 分片，新文件放**同目录**、命名 `原类名.职责.cs`。
5. 一切随机性走 `DeterministicRandom`；**禁止裸 `System.Random` 与时间种子**。
6. **日志统一走 `LogService`**；后台线程禁止调用（调试残留见 ADR-0004）。
7. **单一事实源**：任何"面积 / 距离 / 海陆比例 / 颜色表"只允许有一处权威定义，
   不建平行真相源（`SpatialScale` 是唯一面积 / 距离口径；`SurfaceResolver` 是唯一海陆口径）。

### 3.0 ★清退判定的三层（2026-10-05 实测修正）

清退一个类型前，**"有没有引用"不是充分判据**。要分三层问：

| 层 | 问题 | 处置 |
|---|---|---|
| ① 有没有引用 | 有无`.cs` / `.tscn` / 资产引用边 | 有 ⇒ 先查清是什么性质 |
| ② 什么类型的引用 | 生产语义依赖 / 测试夹具依赖 / 工具诊断依赖 | 夹具体 ⇒ 提取夹具，不留生产抽象 |
| ③ 代表真实语义吗 | 该类型是否仍是必要的领域/运行时抽象 | 不是 ⇒ 可删 |

★**实测案例（`ElevationFieldStack`）**：
我曾判它"零调用"——**那是错的**。准确表述是**零生产语义消费者，但有 8 条测试造场消费者**
（5 条 `H3TerrainSamplerTests` 借它造输入，测的其实是采样器；生产侧采样器有真实消费者
`H3LandSeaProjector` / `LandSeaFields`）。

若当初直接删文件，那 8 条测试会**直接编译失败**。正确顺序：
1. **先剥离测试造场职责**——提取 `TestElevationFieldBuilder`（只含测试真正需要的最小能力：
   可采样 / 确定性 / 振幅已知），**禁止**把旧抽象搬进 tests 改名保留（那是换位置继续存活）；
2. **再删生产侧**——此时生产引用归零，才真正可删。

⚠️ 由此产生两条禁令：
- **不要**为了删除成立而修改被测组件的生产设计（采样器本身没架构问题）；
- **不要**为了测试方便而把旧抽象留在生产代码（形成"测试方便 → 生产保留历史抽象"，
  正是要消除的形态）。

★**由此提炼的永久规则**：
> **测试可以依赖生产抽象来验证生产语义，
> 但不应仅因"测试造数据方便"而成为某生产抽象的唯一消费者。**

**2026-10-05 全库审计结果**（`docs/审查报告-测试夹具依赖审计.md`）：
A 档全部（测的就是它自己 / 真实上游生产链）、**B 档 0 个**、C 档 3 个
（`SphereLines` / `ParallelLoops` / `MathUtils`——**均为纯零引用死代码，非测试造成**）。
⇒ 本项目其余部分**没有再犯** `ElevationFieldStack` 那个陷阱。

★**审计"零引用"的方法学陷阱（实测）**：只搜 `new Xxx()` 会**大量误报**
（本次初扫 50 个候选 → 逐个核实后只剩 3 个）。误报来源三类：
`.tscn` 场景挂载 / 静态方法调用 / 基类与接口被继承。
⇒ 必须同时扫 **`.cs` + `.tscn` + 静态调用 + 继承链**，否则会把活类型误判成死代码。

### 3.0.1 ★判据闭环 ≠ 自动关单（2026-10-05 实测，两例）

**现象**：判据已经满足、证据已经闭环，但债务台账里的状态**不会自动变**。

| 例 | 判据闭环时间 | 台账更新时间 | 挂账时长 |
|---|---|---|---|
| 审查报告 L-6 / L-8（`SphereLines` / `ParallelLoops`） | 2026-10-03 | 2026-10-05 | 2 天 |
| 决策 07 §9 的 **S-6**（世界生成零并行） | 2026-10-05 取证 | 2026-10-05 | 当天 |

★**准确的规律**（2026-10-05 修正过一次，初版结论过重）：
> **死代码/债务建议只有在恰好被某个清退顺带覆盖时才会执行。**
> 不是"判据与执行普遍脱钩"，而是**"没被顺带带走的那几条会永久悬空"**。

⇒ **两条机制要求**：
1. **债务表必须有状态列**（`✅ CLOSED` / `⏸ 待处理` / `⏸ 待判断`），
   且**关单时必须回去改那一行**。判据闭环只是**获得关单资格**，不是关单本身。
2. **能契约化的判据就契约化**（变成可执行断言 ⇒ CI 会发现），
   **不能契约化的（性能/体验类）必须靠台账状态列**。

> C-2 的额外教训：**关单依据要写"它从未接线 + 与本项无因果依赖"**，
> 不能写"它解决过本项所以可删" —— 前者是查证结论，后者是想当然的因果假设。

### 3.1 架构契约测试（边界的可执行形式）

`tests/World.Tests/ArchitectureContractTests.cs` —— **20 条**，反射扫描
"方法参数 / 返回 + 字段 / 属性"。新增子系统时把类型加进 `NewWorldLineTypes` 单字段
（漏扫= 契约静默失效，比不写更危险）。

三类钉，各有其不可替代的作用：

| 类别 | 代表 | 作用 |
|---|---|---|
| **方向** | `NewWorldLine_DoesNotDependOnLegacyWorldLine` | 防重新耦合 |
| **结果** | `LegacyWorldGenerationChain_IsGone` / `LegacyNoiseWorldLine_IsGone` | 防"没人用但还留着" |
| **行为** | `EmptyFeatureList_EqualsPureBaseline` | 防"影响来自未注册的地方" |

★**方向与结果两者都要有**：只有方向 ⇒ 旧代码删不掉；只有结果 ⇒ 重新引入测不出来。

### 3.2 表现层依赖白名单（唯一跨层豁免）

新线命名空间~~`World.NoiseWorld.WorldGen`~~ 曾是旧线 `World.NoiseWorld` 的**子命名空间**
（★2026-10-05 已重构为 `World.WorldGen`，两者现为**平级**）
⇒ C# 作用域让新线不加 `using` 就能看到父命名空间类型。决策 08 §4.4 已把这条例外
**正式编码**（`NewWorldLine_MayDependOnApprovedRenderContracts`）。

**当前状态（2026-10-05 P1 后）**：`World.NoiseWorld` 根命名空间**已彻底清空**，
4 类表现资产全部迁入 `World.Render` / `World.Render.UI` ⇒
这条豁免**事实上已关闭**，新线只准依赖 Render 层。

---

## 4. 确定性纪律（本项目的命根子）

- 生成 / 模拟全部由 `DeterministicRandom` 驱动（SplitMix64），状态可序列化。
- **层种子按固定次序派生**（`worldgen/SeedDerivation.cs` 集中 13 个标签，`seed ^ tag`）
  ⇒ 新增子系统不挪别的层的场。**标签唯一性有测试钉**（防复制粘贴导致两子系统同流）。
- **连续优先**：每层暴露 `Sample(dir)` 连续查询口，逐格数组降级为"该连续函数在格心的一次采样"。
- 文明模拟：**读档续跑 = 从存档随机状态继续消耗，与从头跑 N+M ticks 完全一致**。

---

## 5. 尺度纪律（D-10 · 永久）

三类参数**本体不同，不得混用**：

| 类 | 内容 | 单位 | 与 `res` 的关系 |
|---|---|---|---|
| **A 拓扑** | BFS 距离 / hop / 邻居数 / 图深度 | cell / hop | **强相关** |
| **B 几何** | 面积 / 距离 / 山脉宽度 / 河长 | km / km² / km³ | 无关 |
| **C 物理** | 降水 / 蒸发 / 径流 / 水量 | mm / mm·a⁻¹ / km³ | 无关 |

> ★**res 只决定"一个格子有多大"；物理参数决定"现实世界有多大"；两者不得混用。**

- 面积默认 = `SpatialScale.CellAreaKm2`（**等积** `4πR²/n`）；H3 精确面积只进判读 / 渲染
- 物理距离 = `SpatialScale.DistanceKm`（唯一）；`HopDistanceKm` **禁止进入模拟**
- **`SpatialScale` 新增成员必须配"与独立实现逐点对照"测试**（不是只看公式自洽）
  ——依据：`DistanceKm` 初版把弧度又乘了一次 `d2r`，距离缩小 57 倍，
  **静态看公式完全正常**，是逐邻居实测照出来的。

### 5.1 四项 FROZEN（不得被任何子系统反向绑架）

| 项 | 值 |
|---|---|
| `ProductionRes` | **4** |
| `DefaultUseDepressionFill` | `true` |
| 河流阈值 | **70,800 km²**（工程标定值，**不是**自然常数） |
| `CoastDecayKm` | **450km**（= 10 跳 × 45.2 km 的**跳距**口径，勿与格边长混） |

### 5.2 双高度语义（D-11 · 永久）

```
FinalHeight（地貌 / 渲染 / 判读 + LakeState 原始洼地语义）
     └── (填洼后) HydrologyRoutingHeight ──→ FlowDirection / Basin / FlowAccum
```

填洼是**水文计算的派生预处理**，不是世界地形被填平；**绝不回写 `FinalHeight`**。
`DepressionCount` / `FillDepthM` 始终基于**原始**高度。

---

## 6. 表现层渲染规范（永久）

> **地图表现层不得通过 world-space radial lift 实现图层排序；需要分层时，几何保持真实表面位置，
> 排序通过 depth / NDC bias、stencil 或专用overlay pass 实现。**

三个概念必须分离：`R 上的真实轮廓`（位置偏移 0）｜`screen-space expand`（视觉宽度）｜
`NDC depth bias`（渲染顺序）。

- ⚠️ 严禁 `radius * 1.0005f` 这类径向抬升：掠射角下会换算成横向屏幕位移
  （实测旧写法在 1.02R 最近视距的屏幕边缘位移 14.6px ≈ 10% 格宽）
- **顶点色 `COLOR.a` 会被夹到 `[0,1]`** ⇒ 宽度 / 非颜色量走 `UV2` / `CUSTOM0`
- Godot 4 RenderingDevice 在 D3D12/Vulkan 下用 **reversed-Z** ⇒ `POSITION.z += bias * POSITION.w`
  （写 `-=` 会被地形遮住）
- **质心偏移不能判定"有没有浮起"**（径向缩放 ≈ 以屏幕中心为原点的均匀放大）

---

## 7. 防膨胀三问（建任何"层"之前必过）

1. **能否由既有事实表达？**（能 ⇒ 加字段 / 查询，**不加层**）
2. **能否由组合层表达？**（能 ⇒ 放 `WaterTopology` 一类，不新建实体层）
3. **现在有真实消费者吗？**（没有 ⇒ **不建**）

> ★"旧代码质量很高"**不是**迁移理由。危险链条：
> 旧系统有好算法 → 新系统没有 → 迁过来 → **新系统复杂度上升** → 开始解释为什么存在。
> 资产价值用**文档封存**，不用代码承载（见 `docs/newdecision/封存-NoiseWorld设计史料.md`）。

### 7.1 停退化的元原则

1. **退化解原则**：新物理量 / 权重 / 场进入既有算法，必须有可验证退化解
   （新输入设常量时输出**精确**退化到旧公式）。
2. **原子事实优先于分类**：分类不得覆盖事实。
3. **契约三件套**（清退 / 隔离 / 分层）：结果（类型已不存在）+ 方向（A 不引用 B）+
   边界（保留者依赖白名单）。三者齐备才算收口。
4. **守语义边界，不追求单向依赖树**：`Archive → CivSim`、`Domain → LogicGrid`
   等有意保留的引用不算违规。

---

## 8. 工程质量

- **提交门槛**：`.githooks/pre-commit`（build + 单元测试；
  安装 `git config core.hooksPath .githooks`）。
- **测试**：`tests/World.Tests`（NUnit，**417 条 `[Test]`**）+
  本地执行器 `tests/World.Tests.Local` + 性能台 `tests/PerfBench`。
  - 纪律：**只用 `[Test]`**（不写 `[TestCase]` 参数化）；**不写文件**；
    **不触碰 `GD.*` / `LogService`**。
  - 注意：`[Test]` 特性数 ≠ 用例数（参数化会展开）。
- **底层类固定门槛**：`SpatialScale` 等底层类新增成员须配"与独立实现逐点对照"测试。
- **规范**：`.editorconfig` + `dotnet format`（CI 强制校验）。
- **CI**：GitHub Actions —— `dotnet build` → `dotnet test` → `dotnet format --verify`。

---

## 9. 命名与目录约定

- 命名空间：`World.<领域>`。**当前实际清单**（按 `namespace` 判，**不按目录**）：
  `World.Render(.UI)` / `World.WorldGen` / `World.Domain` / `World.Archive` /
  `World.CivSim.*` / `World.LogicGrid` / `World.HexPlanet` / `World.NewHexWorld` /
  `World.PlanetLOD` / `World.Surface` / `World.Utils(.H3)` / `World.Services` /
  `World.Camera` / `World.Diagnostics` / `World.Gameplay`。
- ★**按类型语义定位，不按目录名定位**（D-3 切分原则）。
  已实证：`scripts/CivSim/Engine/CivSimContext.cs` 的命名空间是 `World.CivSim`（子目录不进命名空间）。
- 文件名 = 类名；`partial` 分片用 `原类名.职责.cs` 后缀放同目录。
- ⚠️ **架构依赖图 = `.cs` 引用边 + `.tscn` 场景挂载边 + 其他资产引用边**。
  Godot 的**场景挂载本身就是运行时依赖**，只扫 `.cs` 会漏（决策 08 §4.3 实测：
  首版报告因此漏掉主入口挂的 `NoiseCellPanel` / `NoiseDock`）。
  ⇒ **静态正确 ≠ 运行时正确**：清退必须以实机回归收口（决策 08 §7）。
- ⚠️ **改类名 / 移动文件时有四处名字要同步**（2026-10-05 实测补全，括号内是发现该条的阶段）：
  ① `ext_resource` **脚本路径**（P1 清退）
  ② 父场景里的**实例名 / 节点名**（P1 清退）
  ③ **被改文件自己的根节点名**（P1 收尾；不影响运行、只影响可读性，最易漏）
  ④ **跨文件移动时，引用它的父场景里的场景路径**（namespace 重构）
     ——`WorldGenPlanet.tscn` 移到 `scenes/worldgen/` 后，
     `WorldGenWorld.tscn` 里的 `PackedScene` 路径仍指向旧目录。
  ★最稳的做法：移动后按"**谁引用了被移动的东西**"反向扫一遍
  （`grep -rn "<旧路径片段>" scenes/ scripts/ tests/`），而不是只改自己那一处。
- Godot 4.4+ 的 `.uid` 文件**一律入库**（与场景 `ext_resource` 的 `uid=` 引用配套）。

---

## 10. 清退记录（两条旧线 · 已完成）

| 线 | 范围 | 状态 |钉住它的测试 |
|---|---|---|---|
| **A 线**（更早） | `World.Biome` / `MapGen` / `MapView` / `Tectonics`（206 `.cs` + 31 场景） | ✅ 已清退 | `LegacyWorldGenerationChain_IsGone` |
| **B 线**（噪声单体） | `World.NoiseWorld`（`NoiseTerrain` / `NoisePlates` / `NoiseClimate` / `TerrainNoiseParams` / UI + 场景） | ✅ 已清退 | `LegacyNoiseWorldLine_IsGone` |

**A 线保留项**（按类型语义而非目录切分，7 项重命名）：
`MapArchive` / `FieldCodec` → `World.Archive`；
`BiomeType` / `BiomeColors` / `PowerPalette` / `WildCropsSystem` → `World.Domain`；
`SphereGrid` → `World.HexPlanet`。

**B 线保留项**（4 类纯表现资产迁入 Render 层）：
`NoiseBallView` → `Render.BallView`（+ `CellQuery` 拆出高亮 / 拾取纯函数）、
`NoiseMapMode` → `Render.MapMode`（+ `ElevationBandMode`）、
`NoiseDock` → `Render.UI.MapDock`、`NoiseCellPanel` → `Render.UI.CellInfoCard`。

**暂缓项**（无消费者 ⇒ 不迁）：地球拟合 LUT / Fritsch–Carlson 样条 / `PropagateMax`
⇒ 知识封存于 `docs/newdecision/封存-NoiseWorld设计史料.md`。

**namespace 重构（✅ 2026-10-05 已完成，独立提交）**：
`World.NoiseWorld.WorldGen` → **`World.WorldGen`**，目录
`scripts/noise_world/worldgen/` → `scripts/worldgen/`，场景
`scenes/noise_world/` → `scenes/worldgen/`。
纯结构重构：34 个 `.cs` + 34 个 `.uid` 全部 `git mv`（68 条rename 记录），
不碰任何生成逻辑 / 渲染逻辑 / 参数体系 / 算法资产。
由 `NewWorldLine_NamespaceIsWorldGen` 契约钉住名字 + 目录 + 旧命名空间不得复活。

---

> 红线：每次提交可编译可运行；重构期间不加新功能；一次只拆一个文件。
> **先加新东西 → 改消费者 → 跑测试 → 再删旧东西**（反序会炸编译，已实证：
> `ElevationBandMode.BandName` 曾被 `BallView` 静态引用）。
