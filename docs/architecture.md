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
| **表现层** | 怎么画 | `BallView` / `MapMode` / `CellQuery` / `WorldView` / `WorldPicker` / `RiverLineOverlay` | `World.Render` |
| **界面层** | 面板与控件 | `MapDock` / `CellInfoCard` / `IMapModeHost` / `IPickedCellLabels`（后两者是窄口） | `World.UI` ★与 Render 平级 |

装配序（依赖序）唯一接线点：`WorldManager.Regenerate()`（六阶段因果序的唯一驱动点）；
**世界生成链之外**的接线（相机 / 坞 / 信息卡 / 每帧刷新顺序）唯一接线点：`WorldRoot`（根管理器，见下）。
★2026-10-11：`WorldGenPlanet` 已收编进 `WorldManager`（连 `Regenerate` 一起）；
点选链（手势 → 拾取 → 高亮 → 报数据）归 `RenderManager`，与"世界怎么生成"无关。

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
6. **无统一日志出口**（★2026-10-09：`LogService` 已整删，ADR-0002 / ADR-0004 一并作废）——
   L0 纯模型不打日志；诊断 / 表现层按需直调 `GD.Print`；**后台线程禁止**调用引擎输出。
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
（`SphereLines` / `ParallelLoops` / `MathUtils`——**均为纯零引用死代码，非测试造成**；
  三者已全部清退：MathUtils`dd62e67` / SphereLines（含 2 个专属 shader）/ ParallelLoops）。
⇒ 本项目其余部分**没有再犯** `ElevationFieldStack` 那个陷阱。

★**审计"零引用"的方法学陷阱（实测）**：只搜 `new Xxx()` 会**大量误报**
（本次初扫 50 个候选 → 逐个核实后只剩 3 个）。误报来源三类：
`.tscn` 场景挂载 / 静态方法调用 / 基类与接口被继承。
⇒ 必须同时扫 **`.cs` + `.tscn` + 静态调用 + 继承链**，否则会把活类型误判成死代码。

★**2026-10-05 新增第 6 类介质：类自带的专属外部资源**（shader / material / 场景资产）。
`SphereLines` 的真实依赖单元是 **6 个文件**（类 + `.uid` + 2 个 `.gdshader` + 2 个 `.uid`），
两个 shader 的**唯一消费者就是它** ⇒ 留类不留资源会造成**悬空资源**，
**比悬空类更难察觉**（编译通过、测试全绿，只是没人引用那份shader）。
⇒ **清退一个类的完整依赖单元 = 代码 + uid + 它引用的专属资源**。

★**两条防误判（2026-10-05 C-3 实测）** —— 对纯资源对象尤其重要：

| 陷阱 | 真相 |
|---|---|
| **`.godot/` 里有登记** | ❌ **不等于有人引用**。`filesystem_cache10` / `uid_cache.bin` 是**导入索引缓存**（Godot 扫描产物，随文件存在自动生成）；`editor/editor_layout.cfg` 是**本地编辑器 UI 状态**（"上次打开了哪个 shader 面板"），随编辑器关闭而变。★两者都被 `.gitignore` 排除、git 完全不跟踪。⚠️ 实测：`planet_detail` 被 `editor_layout.cfg` 记了一笔，若只查到它就下结论**会误判成"有人在用"** |
| **只搜文件路径** | ❌ **会漏**。Godot 4.4+ 的 `.tscn` 可能用 `uid=` 而非 `path=` 引用资源 ⇒ **必须查 uid 值本身**（`uid://...`） |

⇒ **清退一个资源对象的完整依赖单元 = `.gdshader` + 其 `.uid`（必须成对退出）**。
只删 `.gdshader` 会留下孤儿 `.uid`（下次导入报孤儿警告）。

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

`scripts/Test/World.Tests/ArchitectureContractTests.cs` —— **29 条**，反射扫描
"方法参数 / 返回 + 字段 / 属性"（深度面另加**方法体 IL**，见 `ReferencedTypesDeep`）。
新增子系统时把类型加进 `NewWorldLineTypes` 单字段
（漏扫 = 契约静默失效，比不写更危险）。

三类钉，各有其不可替代的作用：

| 类别 | 代表 | 作用 |
|---|---|---|
| **方向** | `NewWorldLine_DoesNotDependOnLegacyWorldLine` | 防重新耦合 |
| **结果** | `LegacyWorldGenerationChain_IsGone` / `LegacyNoiseWorldLine_IsGone` | 防"没人用但还留着" |
| **行为** | `EmptyFeatureList_EqualsPureBaseline` | 防"影响来自未注册的地方" |
| **位置 + 白名单** | `H3Grid_NamespaceMatchesDirectory` / `WorldParams_AreReadOnlyByTheLogicLayerGate` / `WorldGenReadout_LivesOnlyInDiagnosticsScene` | 防"目录↔命名空间脱节"、"又一处悄悄读盘"、"判读爬回生产类" |

★**方向与结果两者都要有**：只有方向 ⇒ 旧代码删不掉；只有结果 ⇒ 重新引入测不出来。
★**新写契约必带"非空转自检"**（如"扫到的白名单里必须含被测类型""必须扫到刻意保留的那两个 `[Export]`"）——
否则扫描器一坏，断言就变成假绿。

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
- **测试**：`scripts/Test/World.Tests`（NUnit；2026-10-09 实测 `dotnet test` **280 用例全绿**）+
  本地执行器 `scripts/Test/World.Tests.Local`。
  ⚠️ 2026-10-11：原性能台 `scripts/Test/PerfBench` 已删除（用户拍板"之后重新写性能测试"）——
  见 `docs/裁决-参数实例化与res参数目录.md` §六。
  - 纪律：**只用 `[Test]`**（不写 `[TestCase]` 参数化）；**不写文件**；
    **不触碰 `GD.*` 等引擎原生调用**（在测试进程 = 进程级崩溃，不可捕获）。
  - 注意：`[Test]` 特性数 ≠ 用例数（参数化会展开）。
- **底层类固定门槛**：`SpatialScale` 等底层类新增成员须配"与独立实现逐点对照"测试。
- **规范**：`.editorconfig` + `dotnet format`（CI 强制校验）。
- **CI**：GitHub Actions —— `dotnet build` → `dotnet test` → `dotnet format --verify`。

---

## 9. 命名与目录约定

  - 命名空间：`World.<领域>`。**当前实际清单**（按 `namespace` 判，**不按目录**；2026-10-11 八层重划分后为 12 个）：
    `World.Data` / `World.Constants` / `World.Params` / `World.Utils`（`.H3`）/ `World.H3Grid` /
    `World.Logic` / `World.Render`（`.Constants`）/ `World.UI` / `World.Client` / `World.Scene` /
    `World.Diagnostics`。
    （★旧名 `World.WorldGen` / `World.Camera` / `World.Render.UI` / `World.Render.Controllers`
    **均已取消并列入"不得复活"契约**——见 `债务清单-架构与分层.md` A-9。）
    （★2026-10-10 更晚：**新建 `World.Assets` = 资产装载（宿主适配）分区**，目录 `scripts/Assets/`
      ——与 `scripts/{Scene,Logic}/` **并列的第三个顶层分区**（分区名本身不进命名空间，与另两个同规则）。
      它是全仓**唯一**允许为"装载资产"而碰 Godot API（`ProjectSettings` / `FileAccess`）的地方；
      首个成员 = `WorldParamStore`（世界参数表的引入 / 引出 / 修改）。
      依赖**单向** `World.Assets → World.WorldGen`（适配层依赖逻辑层；逻辑层不得反向引用）。
      见 `docs/裁决-Assets分区与世界参数宿主层.md`。）
    （★2026-10-09：**新建 `World.Data` = 数据层**，目录 `scripts/Logic/Data/`——只收"纯数据载体"，
      判据（机器可验）：**顶层类型 + 零方法 + 零计算属性 + 无嵌套 + 不引用生成域类型**；
      依赖**单向** `World.WorldGen → World.Data`（载体不得引用 `World.WorldGen` 的类型，否则成环）；
      未纳入的边界件与理由见 `docs/裁决-数据层World.Data.md`。
      ★**2026-10-10 分两段**（子目录 = **自由分组**、不进命名空间；**段判据 = "谁构造它"**）：
        · `Data/Spec/`    = **世界定义 · 参数实例**——装配层 / **参数管理器**构造，按段切片喂给各阶段。
                            ★2026-10-11 起它们是**可变类**（有身份、可改），属性初值 = **出厂档**；
                            内容真相源 = `res/params/world_params.json`（由 `World.Assets.WorldParamStore`
                            读入、`WorldParamManager` 持有唯一实例）：
                            `WorldSpec` / `LandSeaSpec` / `TerrainSpec`
        · `Data/Carrier/` = **生成链内部流通的数据形状**——生成器写、下游读，不由人直接调：
                            `ContinentAnchor` / `MountainRidge` / `Scale3`
        ⚠️ 两段不是"spec vs 非 spec"而是"**外部旋钮 vs 内部形状**"：一个类型同时具备两边特征
           （如已删的 `LandSeaParams`：装配层构造 = spec 侧，却持有运行时字段 + 自带初值 = 载体侧）
           ⇒ 它**没被规范化**，正解是按段拆开，而不是硬塞进某一段。
        六个类型均已登记 `ArchitectureContractTests.NewWorldLineTypes`。
        见 `docs/裁决-海陆参数收编LandSeaSpec.md`）
    （★2026-10-09：`World.Domain` / `World.Archive` / `World.LogicGrid` / `World.CivSim.*` /
      `World.Gameplay` / `World.HexPlanet` / **`World.Services`** 已分别解散 / 删除 / 移出；
      `World.Constants` 成为常量族新家，`World.Diagnostics` 仅剩 `DiagSceneBase` + `H3SmokeDiag`——
      见 `docs/裁决-Domain解散与BiomeType归Constant.md`、`docs/裁决-Legacy载体簇删除.md`、
      `docs/裁决-Services删除.md`）
    （2026-10-06：D 步清退 `World.PlanetLOD` / `World.Surface`；
      E 步 `World.NewHexWorld` → `World.Spatial`）
    （★2026-10-09 更晚：**`World.Spatial` → `World.H3Grid`**，目录 `Logic/Spatial/Ball/` →
      `Logic/H3Grid/`。理由：`World.Spatial` 与另两个同族名易混——`SpatialScale`（尺度口径）与
      `FinalSpatialIndex`（查询索引）**均属 `World.WorldGen`**；本命名空间的真实身份是
      **H3 球面网格本体**。见 `docs/裁决-Spatial改名H3Grid.md`）
- ★**世界参数 = 一份实例 + 一个管理器 + `res/params/` 下两份 JSON**
  （2026-10-10 七轮收敛 → 更晚二层切分 → **2026-10-11 实例化 ＋ 迁入 `res/` ＋ 去内核**；
   见 `docs/裁决-参数管理器.md`、`docs/裁决-Assets分区与世界参数宿主层.md`、
   **`docs/裁决-参数实例化与res参数目录.md`**）：
  **`World.Assets.WorldParamStore`**（`scripts/Assets/Params/`，宿主 I/O：找文件 / 读 / 写）
  ＋ **`World.Assets.WorldParamManager`**（同目录，**参数管理器**：持有唯一实例 / **JSON 转换** / 改参数）
  ＝"世界定义从哪来、归谁"的**唯一**答案。
  · **为什么是两个类型**：导出后内容在 `.pck` 内 ⇒ `System.IO` 读不到参数，只有 Godot `FileAccess`
    读得到 ⇒ "怎么找 / 怎么读 / 怎么写"**必须允许碰引擎**；而"文本 ⇄ `WorldSpec` 实例"与实例持有
    必须能在**没有引擎宿主**的进程里跑（`dotnet test`——测试进程里调引擎 API 是进程级崩溃）。
    按**依赖的运行时**切开，两块各自纯正。
    ⚠️ 2026-10-11 同日两次收敛：① `PerfBench`（无宿主进程之二）删除；② **原独立的编解码内核
    `WorldSpecCodec` 删除**（用户拍板"就让管理器自己读 json、去反序列化好了，反正就一些 Data"）
    ⇒ 参数链只剩上面两个类型，**旧内核名与 `scripts/Logic/WorldGen/Params/` 目录均不得复活**
    （契约 ① / ①d 钉着）；
  · **参数 JSON 只有一个家**：项目根 **`res/params/`**（读取一律从 `res/` 来）——
    默认档 `world_params.json`（正库，随游戏走）｜用户档 `world_params.user.json`
    （**可写**：UI 改参数就写它，**整份覆盖**默认档；删掉 = 恢复出厂；不入库）；
  · **实例化（2026-10-11）**：`WorldSpec` / `LandSeaSpec` / `TerrainSpec` 由 `readonly record struct`
    改为**可变类**，唯一生效实例 = `WorldParamManager.Active`（**身份不变**：`Reload` / `Set` 都就地改字段，
    绝不换对象）；消费方（含持段引用的 `LandSeaField`）**现取即最新值**；
    ※ `JsonSerializer.Deserialize` 只会造新对象 ⇒ "读进来"必然多一步"拷进既有实例"
    （`ReadJsonInto`），这一步没有替代写法；
  · **改参数 = 改实例 ＋ 改文件**：`WorldParamManager.Set("LandSea.LandFraction", "0.5")` 一个动作两半
    ——先改内存实例，再把**同一份实例整份**写回用户档（scene 层只发意图，**磁盘仍只由 `World.Assets` 碰**）；
  · **公开口（参数链的全部对外面）**：`WorldParamStore.ReadPresetText` / `ReadUserText` / `WriteUserText`
    （宿主 I/O）｜`WorldParamManager.Active` / `Reload` / `Set` / `Save` / `ResetToFactory` /
    `ParameterNames` / `CreatePresetSpec` / `ReadJson` / `ReadJsonInto` / `ToJson` / `JsonOptions`（管理器）；
  · **路径选择规则**：**磁盘优先**（`AppContext.BaseDirectory` 向上找 ≤12 层 = 含 `res/params/` 的内容根）
    ⇒ 找不到（= 导出，内容进了 `.pck`）才走引擎（`res://` 读默认档、写用户档）。
    ⇒ 单测（唯一无宿主进程）永不进入引擎分支（引擎调用封在 `NoInlining` + `try/catch` 逃生门里，
    与 `H3Native` 找 native dll 同型）；
  · **不做字段合并、也不做逐字段校验**（2026-10-11 用户拍板"直接反序列化 …… 也不需要校验吧"）：
    用户档**整份覆盖**默认档；**漏写的字段**（含整段缺失）⇒ 落回 spec 属性初值 = **出厂档值**，
    **不报错**——这是显式接受的代价（给 spec 加/改字段时必须同步改两个档，否则新字段永远停在出厂值）；
    仍然报错且不装载的只有三类"看得见"的坏档：**语法错 / 字段名拼错 / 类型不匹配**；
    坏档 ⇒ 报问题并**保留上一层值**（默认档读不到时保留出厂档实例值）；
  · **存储类 = spec 类型自身**（不建注册表 / key 清单 / 字段类型表）；**默认值内容只在 JSON 里**
    （代码侧只有 spec 属性初值当出厂档）；
  · **场景层不碰磁盘**：`WorldGenPlanet` 已删 4 个世界参数 `[Export]`；★`ResLevel` / `Radius`
    两个构造参数**后来也删了**（2026-10-11 收为编译期常量：`WorldManager.ProductionRes` /
    `PlanetGeometry.ProductionRadius`）⇒ 该类现已被收编进 `WorldManager`，全类零 `[Export]`；
  · **第二参数入口不得复活**：`WorldGenPlanet.ApplyWorldSpec` / `_specOverride` 已删（零消费者 ＋ 它绕开
    "世界定义只有参数表一个来源"）⇒ **存档恢复世界 = 写用户档 JSON 再 `Reload`**
    （该类已随收编消失，契约改钉 `WorldManager` 上不得复活同名成员）；
  · 护栏 `ArchitectureContractTests.WorldParams_AreReadOnlyByTheLogicLayerGate`（**位置 ＋ 参数目录 ＋
    正门唯一 ＋ 第二入口 ＋ 磁盘白名单 ＋ 引擎面唯一 ＋ 数据层不沾宿主 ＋ `[Export]` 结果钉**，
    各带非空转自检；旧类型 `WorldParamTable` / `WorldSpecCodec` 进"不得复活"名单）
    ＋ `WorldParamManagerTests`（实例身份 / 改实例即改文件 / 直接反序列化语义（含"漏写=出厂值"的
    代价钉） / 位置钉）。
- ★**主场景 = 根管理器 + 子系统**（2026-10-11 建，官方 `scene_organization` 的形态落地）：
  官方两条规则的落点——①"每个游戏都该有一个**入口点**，在 Godot 里是一个 Main 节点"；
  ②"**兄弟节点只应了解自身的层级，由祖先节点来中介它们的通信与引用**"。于是：

  ```
  WorldRoot (Node3D)            ← 根管理器：入口点 + 中介者（WorldGenWorld.tscn 的根）
  ├── OrbitalCamera             ← 客户端外围（自驱动组件；世界与渲染两棵树都用它）
  ├── WorldManager              ← 子系统①/②：世界本体（持 Sim：世界生成 + 全部世界事实）
  ├── RenderManager             ← 子系统③：3D 世界表现（WorldView：BallView + 河线 + 高亮）
  │      └── WorldView          ← 场景实例；BallView / RiverLines 由它运行期 AddChild
  └── UIManager                 ← 子系统④：界面（PanelLayer + 信息卡 + 坞）
     └── PanelLayer (CanvasLayer)
        ├── CellInfoCard       ← 哑组件（由界面管理器持有并喂数据）
        └── MapDock            ← 哑组件（同上；由坞驱动器驱动）
  ```
  ★**2026-10-11 收编**：原 `WorldManager > WorldGenPlanet` 两层已并为一层（`Sim` 直接住在
  `WorldManager` 上，`WorldGenPlanet` 类与其 `.tscn` 已删）⇒ 树深 4 → 3、少一个 `[Export]`、
  少一条跨场景依赖。点选（手势/拾取/高亮）也已从世界侧整体迁往 `RenderManager`。

  · **`WorldRoot`**：`[Export] NodePath` 声明依赖（官方"Initialize a NodePath"）→ 按序
    `World.Initialize(spec)` → `Render.Initialize(Sim, modes[0])` → `UI.Initialize(render, modes)`
    → 每帧只转发 `Tick`；实现 `_GetConfigurationWarnings()`，**依赖没接好在编辑器场景面板就显示黄色警告**
    （官方自文档化机制，与 `Area2D` 缺 `CollisionShape2D` 同型）；
  · **`WorldManager`**：世界事实的唯一入口（`World.Sim`）；分辨率与球半径都是**编译期常量**
    （`ProductionRes` / `PlanetGeometry.ProductionRadius`），**零 `[Export]`**；
  · **`ITickable`**（`Tick`）：根管理器只认这一个方法，**不认识世界生成**
    ——换一个世界/界面不必改根管理器；
  · **谁拥有谁**（官方判据："移除父节点是否意味着子节点也该被移除？"）：世界事实归 `WorldManager`
    （持 `Sim`）、3D 表现归 `RenderManager`（持 `WorldView`）、界面归 `UIManager`（持 `PanelLayer`）、
    参数归 `WorldParams`——根管理器**不持有游戏数据**，只按序喂依赖并转发 `Tick`；
  · **有意不做**（都有官方依据）：不建 EventBus/全局单例（官方 `autoloads_versus_regular_nodes`：
    "修改别的系统数据的系统应定义成自己的脚本/场景，而不是 autoload"）、不做服务定位器/DI 容器
    （官方推荐的是"父上下文显式提供依赖"）、不管场景切换（只有一个主场景，YAGNI）；
  · **初始化顺序显式化**（本轮的实质收益）：原先靠"子节点 `_Ready` 先跑"这种**隐式**顺序
    （谁先谁后取决于场景树排列，挪一个节点就静默变）⇒ 现在只有 `WorldRoot._Ready` 一处决定顺序，
    且 `WorldManager` / `RenderManager` / `UIManager` 的初始化都**幂等**；
  · ⚠️ **无人自动生成世界**：`WorldManager` 不实现 `_Ready`——单独挂它不会生成，
    必须经根管理器或显式调 `Initialize()`（诊断场景走后者，见 `WorldGenReadoutDiag`，
    它根本不建表现层，自建 `WorldGenSimulation`）。
- ★**判读读数住诊断场景**（2026-10-10 用户拍板"生产类不掺判读"，见
  `docs/裁决-判读读数归诊断场景.md`）：
  生产类（原 `WorldGenPlanet`，现 `WorldManager`）**零 `GD.Print`**——`[WORLDGEN-PARAMS]` /
  `[WORLDGEN-TIMING]` / `[WORLDGEN-READY]` 三处判读全部搬到 `scripts/Test/Diagnostics/WorldGenReadoutDiag.cs`
  + `scenes/diag/WorldGenReadoutDiag.tscn`（继承 `DiagSceneBase`，带 `--res` / `--radius`，
  产出 `PASS/FAIL` + 退出码）；`verify.sh` 增"世界生成读数"组（放在 `--fast` 之内）。
  **有意保留**：根管理器里参数读表报告的 `GD.PushWarning`（坏档 / 首跑拷出的**错误可见性**，
  搬走 = 坏档静默）；点选判读 `[WORLDGEN-PICK]`（现住 `UIManager`，交互式，headless 无法复现）。
  边界判据 = **"给人看世界算得对不对"⇒搬；"给人看有没有出错"⇒留**。
  边界判据 = **"给人看世界算得对不对"⇒搬；"给人看有没有出错"⇒留**。
  ⚠️ 净损失 = 表现层构建耗时 `[WORLDGEN-READY]` 不再自动产出（要看去主场景实机跑）。
- ★**按类型语义定位，不按目录名定位**（D-3 切分原则）。
  已实证：`scripts/CivSim/Engine/CivSimContext.cs` 的命名空间是 `World.CivSim`（子目录不进命名空间）。
- 文件名 = 类名；`partial` 分片用 `原类名.职责.cs` 后缀放同目录。
  （★2026-10-09：数据层搬迁顺手收口 4 处 `文件名 ≠ 类名`——`ContinentAnchor` / `LandSeaParams` /
    `MountainRidge` / `Scale3` 此前寄生在 `ContinentLayout.cs` / `LandSeaFields.cs` /
    `MountainSkeleton.cs` / `TerrainFeature.cs` 内，现已各自成文件。
    ★其中 `LandSeaParams` 已于 **2026-10-10 并入 `LandSeaSpec` 并删除**（它自带字段初值 = 内容装在形状里），
      故本条现有的数据层四类型实为 `ContinentAnchor` / `MountainRidge` / `Scale3` + 三个 spec。）
    ⚠️ **同类债仍在**（一个 `.cs` 装多个顶层类型，文件名只对得上其一）：
    `SphericalField.cs`(6) / `RegionalLandforms.cs`(3) / `LandSeaFields.cs`(2) / `MountainSkeleton.cs`(2) /
    `GeologicalRegions.cs`(2) / `VolcanoField.cs`(2) / `HeightComposer.cs`(2) / `ColorRamp.cs`(2) /
    `H3.cs`(2) / `H3Native.cs`(2)——收口属独立批次，勿与生成语义改动混做。）
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

> ★**2026-10-09 后续**：上述 A 线保留项**绝大部分已消亡**——`World.Archive` 与 `World.Domain`
> 删除 / 解散、`SphereGrid` 删除、`FieldCodec` / `WildCropsSystem` 随载体簇删除；仅 `BiomeType` 存续，
> 已迁 `World.Constants` 并裁剪为**仅柯本气候型**。见 `docs/裁决-Domain解散与BiomeType归Constant.md`。

**B 线保留项**（4 类纯表现资产迁入 Render 层）：
`NoiseBallView` → `Render.BallView`（+ `CellQuery` 拆出高亮 / 拾取纯函数）、
`NoiseMapMode` → `Render.MapMode`（+ `ElevationBandMode`）、
`NoiseDock` → `Render.UI.MapDock`、`NoiseCellPanel` → `Render.UI.CellInfoCard`。

**暂缓项**（无消费者 ⇒ 不迁）：地球拟合 LUT / Fritsch–Carlson 样条 / `PropagateMax`
⇒ 知识封存于 `docs/newdecision/封存-NoiseWorld设计史料.md`。

**namespace 重构（✅ 2026-10-05 已完成，独立提交）**：
`World.NoiseWorld.WorldGen` → **`World.WorldGen`**，目录
`scripts/noise_world/worldgen/` → `scripts/worldgen/`（注：该目录已于次日经
A 步正规化为 `scripts/WorldGen/`，见下节），场景
`scenes/noise_world/` → `scenes/worldgen/`。
纯结构重构：34 个 `.cs` + 34 个 `.uid` 全部 `git mv`（68 条rename 记录），
不碰任何生成逻辑 / 渲染逻辑 / 参数体系 / 算法资产。
由 `NewWorldLine_NamespaceIsWorldGen` 契约钉住名字 + 目录 + 旧命名空间不得复活。

**目录治理（✅ 2026-10-06 已完成，A/B/C 三次独立提交）**：
`scripts/worldgen/` → `scripts/WorldGen/`，并按 `Regenerate()` 的依赖 DAG 拆为六个子层
（`Placement` / `Features` / `Discretization` / `Final` / `Simulation` / `Composition`）；
表现层三个文件归位到 `scripts/Render/{Modes,Overlays}/`；
`scripts/Utils/` 根级按职责归入 `Foundation` 六子层
（`Random` / `Noise` / `Math` / `IO` / `Color` / `Archive`，`H3/` 保持原位）。
累计 73 个文件全部 `git mv`（73 条 R100 = 100% 相似度），**tracked 内容语义零变化**，
namespace 一律保持 `World.WorldGen` / `World.Utils` 不变。
提交：`1eb6ecd`(A) → `6410d49`(B) → `88bf2d2`(C)。
验收：build 0 警告 0 错误 · 511 测试全绿 · Godot headless 加载零 ERROR，
`[WORLDGEN-TIMING]` 数值与迁移前逐字一致。

**WorldGen 子层重排（✅ 2026-10-09 已完成，代码/文档两次独立提交）**：
原六子层（`Placement`/`Features`/`Discretization`/`Final`/`Simulation`/`Composition`）
是按 2026-10-06 的依赖 DAG 切的；此后水文/气候/服务三块膨胀，`Simulation/` 变成**杂物抽屉**
（15 文件占全树一半，混装气候、水文、尺度/场基建、Placement 解算器）。
按"目录内容与名字自洽"重排为**八子层**：
`Simulation/` 拆为 `Climate/`(2) + `Hydrology/`(10)，`SpatialScale` + `SphericalField` 归新建
`Foundation/`(2)，`SurfaceResolver` 归 `Placement/`(6)，`HeightComposer` 归 `Composition/`(2)，
`Features/` 纯化为 4。位移 16 个 `.cs`（+ `.uid`），`Simulation/` 名称整体消失。
**namespace 一律保持 `World.WorldGen` 不变**（子目录是自由分组，不参与 namespace）⇒
**零 `using` 改动、零 `.tscn` 改动**（`Logic/WorldGen/**` 无非 Node 脚本指向）、
**零测试路径断言**（子目录名不被任何契约引用）⇒ 纯 `git mv`。
验收：三个构建项目均 0/0 · 281 测试全绿 · headless 读数逐字段同基线（零漂移）零 ERROR。
决策见 `docs/裁决-WorldGen子层重排.md`。

---

> 红线：每次提交可编译可运行；重构期间不加新功能；一次只拆一个文件。
> **先加新东西 → 改消费者 → 跑测试 → 再删旧东西**（反序会炸编译，已实证：
> `ElevationBandMode.BandName` 曾被 `BallView` 静态引用）。
