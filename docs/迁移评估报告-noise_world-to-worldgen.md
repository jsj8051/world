# 迁移评估报告：`World.NoiseWorld` 旧单体噪声线 → 新分层世界生成架构

- 日期：2026-10-05
- 范围：`scripts/noise_world/*.cs`（namespace `World.NoiseWorld`，11 个文件）→ `scripts/noise_world/worldgen/`（namespace `World.NoiseWorld.WorldGen`，34 个文件）
- 性质：**纯分析，未改动任何代码**
- 基线：`8d8b956 feat(worldgen): #13 Final 空间索引 v1（681 测试绿）`

---

## 0. 一个必须先说清的口径问题

仓库里同时存在**两条**"旧线"，容易被混为一谈：

| 线 | 位置 | 状态 |
|---|---|---|
| **A线：已清退的更早旧线** | `scripts/MapGen/`、`Biome/`、`MapView/`、`Tectonics/`、`UI/` | **已从工作区删除**（暂存区 206 个 `.cs` + 31 场景，`git status` 237 项 `D`）；契约测试 `LegacyWorldGenerationChain_IsGone` 断言四命名空间零类型存活 |
| **B线：本次评估对象** | `scripts/noise_world/` 根目录 11 个 `Noise*` 文件 | **仍物理存在、仍在服役**。`scenes/core/NoiseWorld.tscn` 存活；但主入口 `project.godot:15` 已切到 `WorldGenWorld.tscn` |

决策文档（`01-框架立项.md:34`）把B线称作"**噪声地形线**"，定位为"现役渲染链不动"的并行线，**全库无任何文档把它当作待迁移对象讨论**。

所以本报告的定位是：**B线是 A线清退之后残留的最后一块过渡资产，需要一次决策（迁移 or 废弃），而这次决策尚未做过。**

---

## 1. 旧项目背景

### 1.1 解决什么问题 / 目标场景

B线是一个**球形星球程序化地形实验器**：给定种子，在 H3 球面网格上生成一块星球的板块划分、海拔、气候、生物群系，并实时渲染成可旋转、可点选、可换地图模式的球体。

服务对象是**判读与调参**，不是生产模拟。核心工作流是：改参数 → 观察球面 → 判断地形像不像地球 → 再改。`TerrainNoiseParams` 的字段声明顺序直接就是面板行顺序（`TerrainNoiseParams.cs:9`），面板由 `[NoiseParam]` 特性**反射自动生成**——手写数十个控件必错，这是旧线最重要的工程决策。

### 1.2 技术栈

| 项 | 内容 |
|---|---|
| 引擎 | Godot 4.7（Forward+），主入口 `WorldGenWorld.tscn` |
| 语言 | C# / .NET 8 / `LangVersion=latest` / `Nullable=disable` |
| 空间底座 | H3 球面网格（`scripts/new_HexWorld/Ball/`）+ `World.Utils.H3`（P/Invoke 到 `native/h3`） |
| 噪声 | `World.Utils.SphericalFbmNoise` / `SphericalBlobNoise`（自研球面 fBm） |
| 随机 | `World.Utils.DeterministicRandom` —— **纪律要求的唯一随机源** |
| 渲染 | `NoiseBallView`（自研 LOD 网格 + 颜色纹理烘焙 + 射线拾取） |
| 测试 | NUnit 3.14 + `tests/World.Tests`（473 条 `[Test]`）+ `tests/PerfBench` |
| 配置 | 特性反射面板（`NoiseParamAttribute`） |

### 1.3 整体架构：L0–L5 分层噪声栈

```
NoisePlates（板块划分：粗 Voronoi + 吞并 + 面积拟合）
     ↓ 陆/洋身份（每板一枚，硬钳 ±1m）
NoiseTerrain
     L0 大陆场   低频 fBm（域扭曲坐标上采样）
     L1 域扭曲   三路低频矢量场扭采样坐标 ⇒ 海岸线弯曲
     ↓ 域内秩（陆域/洋域各自求秩）
     L2 山系带   ridged (1−|n|)^k × 碰撞带掩码
     L3 细节层   中高频 fBm（米域小振幅）
     L4 组合权重 → L5 单调样条（Fritsch–Carlson）→ 米域
     ↓
NoiseClimate（温度 / 风 / 降水 / 生物群系）
     ↓
NoiseBallView（渲染） ← NoiseMapModes（着色策略）
```

### 1.4 一个关键语义：身份定海陆

B线最核心的设计决策（`NoiseTerrain.cs:8-13`，"是什么就是什么"第一步，2026-09-30）：

> **海陆由板块身份定死——陆板全陆、洋板全洋；L0 噪声只在陆/洋各自域内求秩得起伏。海拔符号在米域硬钳 ±1m ⇒ 恒等于板块身份。**

这条不变量由测试钉死。它与新架构的 `SurfaceResolver`（唯一海陆口径，海陆比= 分位校准）是**根本不同的两种语义**：

| | B线（旧） | 新架构 |
|---|---|---|
| 海陆来源 | 板块身份（离散、每板全同） | 连续场分位校准（`H3LandSeaProjector`，`LandFraction=0.29`） |
| 陆地占比口径 | **陆板数占比**（面积跟随吞并结果） | **面积占比**（分位校准，可控） |
| 一致性 | 陆板整块必全陆，可能出现"一块板全是沙漠"的非连续 | 连续场，可分出半岛/海湾 |

---

## 2. 旧实现要点

### 2.1 核心模块划分

| 文件 | 行数 | 职责 | 质量评价 |
|---|---|---|---|
| `NoisePlates.cs` | 471 | 板块划分（纯分割，**无构造模拟**） | ★★★★★ 文档密度最高，八次迭代史完整留存 |
| `NoiseTerrain.cs` | 614 | L0–L5 噪声栈 + 分段缓存 | ★★★★ 缓存设计好，但单类职责过重 |
| `NoiseClimate.cs` | 214 | 温度/风/降水/群系 | ★★★ 公式可用，判据粗糙 |
| `NoiseBallView.cs` | 22KB | LOD 网格 + 颜色烘焙 + 拾取 + 高亮 | ★★★★ 新线**仍在复用** |
| `NoiseMapModes.cs` | 10KB | 地图模式抽象 + 5 个模式 + 色带 | ★★★★ 抽象仍在复用 |
| `TerrainNoiseParams.cs` | 144 | 参数单一事实源 + `[NoiseParam]` 特性 | ★★★★★ 面板机制是本仓库独一份 |
| `NoiseWorldManager.cs` | 86 | 运行期接线（相机/模式坞/点选） | ★★★ |
| `NoisePlanet.cs` | ~90 | 组件装配（Ball+Terrain+Plates+Climate+View） | ★★★ |
| `NoiseDock/CellPanel/ParamPanel.cs` | ~13KB | 调参面板 UI | ★★ 仅 B线自用 |

### 2.2 值得保留的设计（按价值排序）

**① 分段快照缓存（`NoiseTerrain.cs:57-95, 109-171`）**
四个 `struct` 快照（`RawSnap`/`DomainSnap`/`BeltSnap`/`ElevSnap`）+ 逐字段快照对比，判据是"上游真变更才重算"。诚实直写、不用脏标记猜测。参数面板每次微调都路过这里，没有它根本不能用。

**② 确定性纪律：`NoiseParamPanel` 之外的一切随机走 `DeterministicRandom` 固定偏移**
`NoisePlates.cs:26-27` 列出全部偏移：撒点 `+5333` / 重采样 `+31·attempt` / 身份 `+999` / 运动向量 `+7919`。`NoiseTerrain.cs:190-193` 的层种子按**固定次序**派生（L0→W1→W2→W3→山带→细节），**与开关状态无关** ⇒ 开关一层不挪别的层的场。这条纪律新线的 `SeedDerivation` 继承并成文了。

**③ 地球实测拟合 LUT（`NoiseTerrain.cs:501-511`）**
- 海洋深度剖面 13 点：数据源 = OpenTopodata ETOPO1 1° 全球采样（64800 点，NOAA 公有领域）
- 陆地高程分布 13 点：25% 陆地 <200m、46.5% <500m、长尾 6094m（珠峰量级）
- 秩（L0 噪声在陆域内的秩）= 面积分位 ⇒ **噪声秩直接对应面积分位**

这是全仓库**唯一带实测数据来源标注**的常量表。

**④ Fritsch–Carlson 单调三次切线（`NoiseTerrain.cs:530-539, 590-598`）**
切线取调和加权平均，符号一致时置 0，结点严格递增 ⇒ 斜率恒正、无除零。同时提供 `SplineInverse`（反解）让"结点高度可调但地球分布不变"。新架构完全没有等价的样条系统。

**⑤ `max` 传播的收敛性论证（`NoiseTerrain.cs:461-487`）**
碰撞带扩散用 `PropagateMax`：每跳 ×0.8、≤ width 跳、邻格取 max。注释明写"max 松弛的收敛点与处理序无关 ⇒ 确定性；carry 逐跳严格缩减 ⇒ 必然终止"。新线 `FinalSpatialIndex` 用的是 BFS（只能做单源距离场），**做不了带权多源max 扩散**。

**⑥ 板块划分的八次迭代史 + 护栏体系（`NoisePlates.cs:10-27`）**
逐条记录每次失败（"三修 加权 Dijkstra ⇒ 大圆弧"、"五修 扭曲 Voronoi ⇒ 圆斑 + 两巨板"），并沉淀两条护栏：
- **面积拟合迭代**（`:162-173`）：速率按 `(目标/实际)^0.5` 校准 5 次 ⇒ 份额贴合地球式层级
- **分水岭检测**（`:384-455` `HasFineBottleneck`）：深核 BFS 连通分量 + 流域归属 ⇒ 检测沙漏/窄颈病态

**⑦ 退化护栏模式（`NoisePlates.cs:82-216`）**
最多 8 次重采样，份额与形状双护栏，形状合格优先于份额最优。

**⑧ `[NoiseParam]` 反射面板 + 导出 JSON 同结构**（`TerrainNoiseParams.cs:5-10, 121-143`）
声明即文档、声明即面板、声明即存档 ⇒ 不存在"面板值与数据不一致"。`int` 自动取整、`skipPanel` 支持。

**⑨ `NoiseMapMode` 抽象（`NoiseMapModes.cs:13-56`）**
`Id`/`Name`/`CellColorAt`/`BeginBake` 四成员极小口；`CreateAll` 注册序 = 坞按钮序 = Id，数量不符当场抛。**新线 `WorldGenMapModes` 直接继承了它。**

★**现状（2026-10-09）**：新线已**删除 `Id`** —— 模式身份 = `WorldGenMapModes.CreateAll` 返回列表的**下标**，各模式类不再手写 `Id => N`（唯一消费者是一句诊断打印，已改印下标；诊断 `--mode=` 本就按 `Name` 定位）。存活成员 = `Name`/`CellColorAt`/`BeginBake` + 参数三件套（`ParameterOptions`/`ParameterIndex`/`SetParameter`）+ `ScaleCaption`。

**⑩ `NoiseBallView` 的两个构造函数（`:45, 49`）**
一个吃 `NoiseTerrain`（旧线），一个吃 `float[] elevationM`（新线）。这个双构造函数是两条线之间**唯一的桥**，也是现在还活着的依赖点。

### 2.3 主要业务流程

`NoisePlanet.Regenerate()`：`NoisePlates.Generate` → `NoiseTerrain.Generate` → `NoiseBallView.BuildChunks` → `NoiseClimate.Generate` → `NoiseMapMode.BeginBake` → 颜色纹理重烘。

`NoiseTerrain.Generate` 四段：`SampleRawFields`（段1，三路噪声同扭曲坐标）→ `BuildDomainRanks`（段2，域内秩 + 离岸 BFS + 洋中脊）→ `BuildPlateBelt`（段1e，碰撞带源 + max 扩散）→ `MapQuantileToElevation`（段3，样条 + 硬钳）。

---

## 3. 可迁移清单

| # | 内容 | 文件路径 | 分类 | 判断依据 |
|---|---|---|---|---|
| 1 | `NoiseBallView` LOD/剔除/拾取/高亮 | `scripts/noise_world/NoiseBallView.cs` | **需适配改造** | 新线**已在复用**（`WorldGenPlanet.cs:80` 用 `float[]` 重载）。要拆成 `BallViewCore`（渲染+拾取）与旧线专属部分；`PickCell/TryGetElevation/HighlightCell` 已被新线消费，不能直接删 |
| 2 | `NoiseMapMode` 抽象 + `ColorRamp` 工具 | `NoiseMapModes.cs:13-56` | **需适配改造** | 新线 `WorldGenMapModes.cs:26` 已 `sealed class ElevationMode : NoiseMapMode` 继承。基类本身零依赖，可直接留在旧文件；5 个旧模式（`ElevationBandMode` 等吃 `NoiseTerrain`）要废弃 |
| 3 | 地球实测拟合 LUT（高程/深度分布） | `NoiseTerrain.cs:501-511` | **可直接复用** | 纯`static readonly float[]` + 数据来源注释，**零依赖**。新线 `HeightComposer` 目前用 lerp 链+ 3 档变化噪声，没有地球分布先验。这是B线对新线最有价值的单件资产 |
| 4 | `DeterministicRandom` 固定偏移派生模式 | `NoisePlates.cs:26-27`、`NoiseTerrain.cs:190-193` | **可直接复用** | 纪律层面已被新线 `SeedDerivation.cs` 继承并成文（13 个标签 + `seed ^ tag`）。旧线的"偏移常量表"可作为 `SeedDerivation` 的补充参考 |
| 5 | `max` 传播带权扩散 `PropagateMax` | `NoiseTerrain.cs:463-487` | **需适配改造** | 新线 `FinalSpatialIndex` 只有 BFS 距离场，**无法表达带权多源 max 扩散**。若将来要做"构造强度向内陆衰减""迎风坡衰减"这类非距离场语义，需要它。判定为"现在无消费者 ⇒ 不建"（防膨胀三问第3 问），故列需适配而非直接复用 |
| 6 | 板块划分 `NoisePlates` | `NoisePlates.cs` | **建议重写或废弃** | 新架构走的是 `TectonicField`（连续场，"哪里容易"）+ `ContinentLayout`（锚点蓝噪声）路线，与"离散板块 + 陆洋身份硬钳"是**两种范式**。且"板块"本身是新架构刻意不建模的对象（决策 05/06 无任何板块类型） |
| 7 | 面积拟合迭代 + 分水岭护栏 | `NoisePlates.cs:162-173, 384-455` | **建议重写或废弃** | 算法本身可迁移（`(target/actual)^0.5` 迭代校准是通用技巧），但**没有消费者**——新架构无"离散份额分配"需求。`GeologicalRegions` 用的是 Voronoi + Lloyd 松弛，机制不同 |
| 8 | Fritsch–Carlson 单调样条 + `SplineInverse` | `NoiseTerrain.cs:530-598, 361-368` | **需适配改造** | 新架构高度链是**离散结点 lerp**（`HeightComposer`），无样条。若要恢复"地球高程曲线"表达需移植；`SplineInverse`（反解保分布）尤其值得保留——它让参数可调而不动分布 |
| 9 | `[NoiseParam]` 反射面板机制 | `TerrainNoiseParams.cs:121-143` | **建议重写或废弃** | 新架构参数组织是 `[Export]`（8 个）+ `const` 冻结值 + params 类**四种并存**，没有统一面板需求。且新架构工作方式已是"改 const 跑测试"，不是"拖滑块看球"。若要保留，需适配为 `[Export]` 或 editor plugin 形态 |
| 10 | 地形参数集`TerrainNoiseParams` | `TerrainNoiseParams.cs:11-119` | **建议重写或废弃** | `ElevationFieldStack.cs:72-74` 明文："现役 `TerrainNoiseParams` 是噪声地形线的单一事实源，本表不与之共享实例"。即新架构**明确不共享**。其中 `LandFractionTarget=0.29` 概念已由 `WorldGenPlanet.LandFraction` 接管 |
| 11 | 碰撞带 `BuildPlateBelt`（单侧造山/俯冲/海沟） | `NoiseTerrain.cs:386-459` | **建议重写或废弃** | 强依赖板块身份。新架构的 `TectonicField` 不产出离散边界，无`omega[plate]` 无"异板边"。**无消费者** |
| 12 | `NoiseClimate` 温度/风/降水/群系 | `NoiseClimate.cs` | **建议重写或废弃** | 与新线 `PrecipitationModel.cs` **功能重叠但口径不同**：新线是"纬度带 × 海岸加权，只消费 Final"；旧线是"纬度带 × 噪声 × 抬升 × 盛行风 + 雨影"，依赖 `NoiseTerrain.ElevationM` 符号判海陆。且 `Hydrology_DoesNotReferenceLegacyClimateLine` 契约测试已把"降水来源"在新线内拍板 |
| 13 | 生物群系表 + 简化柯本 | `NoiseClimate.cs:20-56, 151-166` | **需适配改造** | `BiomeType` 枚举已迁到 `scripts/Domain/BiomeType.cs`（git `R`），`BiomeColors` 已迁到 `Domain/BiomeColors.cs`。**但旧线这里有一套 13 项的 `BiomeColors` 字面量数组，与 `Domain/BiomeColors` 重复**——需核对哪份是权威 |
| 14 | 域扭曲大陆场 L0/L1 + 域内秩 | `NoiseTerrain.cs:183-222, 228-333` | **建议重写或废弃** | 新架构 `LandSeaField`（域扭曲 + 三尺度）+ `H3LandSeaProjector`（分位校准）已覆盖，且**去掉了"域内秩 + 身份硬钳"这个前提**。算法血缘相近但语义已变（秩 vs 分位） |
| 15 | 分段快照缓存 | `NoiseTerrain.cs:57-95` | **需适配改造** | 模式值得保留（多子系统的部分重算），但新架构是**一次性 `Regenerate()` 全链重建**（`WorldGenPlanet.cs:91-158`），无交互式微调 ⇒ **当前无消费者**。若将来加实时调参面板需要它 |
| 16 | `NoiseDock`/`NoiseCellPanel`/`NoiseParamPanel` UI | `NoiseCellPanel.cs` 等 | **建议重写或废弃** | 仅 B线自用。新线已有 `WorldGenDock.tscn` + `WorldGenManager`。`NoiseCellPanel` 的"格信息卡"职能已由 `WorldGenManager` 接管 |
| 17 | `NoiseWorldManager` 运行期接线 | `NoiseWorldManager.cs` | **建议重写或废弃** | 逻辑已被 `WorldGenManager.cs` 重写（旧版用 `NoiseMapMode.CreateAll(terrain, plates, climate)`，新版用 `CreateAll(planet)`）。这是"已经重写过一遍"的例子 |
| 18 | `NoisePlanet` 组件装配 | `NoisePlanet.cs` | **建议重写或废弃** | 已被 `WorldGenPlanet.cs` 完全取代（后者20 个子系统的依赖序装配）。注意 `NoisePlanet.ResLevel=4` / `Radius=2.0` 与 `WorldGenPlanet` 的同名 `[Export]` 是**两套独立配置**，迁移时会踩 |
| 19 | 洋中脊检测（离散边界为源） | `NoiseTerrain.cs:265-305` | **需适配改造** | 思想（离散板块边界作为洋中脊源）在新架构无对应物——新架构不建模板块。若将来做 `FinalGeography` 的洋中脊系统，需要"从离散边界源做max 扩散"的模式 |
| 20 | `NoiseParamAttribute` + JSON 导出 | `TerrainNoiseParams.cs:121-143` | **可直接复用** | 特性本身零依赖、零耦合，可整体拷进任何新参数类。但**要有消费者**才值得（当前新架构无面板需求）⇒ 严格说属"需适配改造"，此处单列因其可移植性 |

**统计**：可直接复用 2 项、需适配改造 8 项、建议重写或废弃 10 项（共 20 项，含拆分计）。

---

## 4. 新旧结合方案

### 4.1 现状：唯一活的依赖点

```
scripts/noise_world/worldgen/WorldGenPlanet.cs:68,80
    public NoiseBallView View { get; private set; }
    View = new NoiseBallView(_ball, DisplayElevation, ...)   ← float[] 重载
> **路径注记（2026-10-06 目录治理后）**：本报告评估 B 线清退时，文中路径为
> `scripts/noise_world/worldgen/`（B 线）与 `scripts/worldgen/`（当时的新线位置）。
> 2026-10-06 完成 A/B/C 目录治理后，新线主链位于 `scripts/WorldGen/` 六子层，
> 其中 `WorldGenMapModes.cs` → `Render/Modes/`、
> `RiverLineOverlay.cs` / `SnowOverlay.cs` → `Render/Overlays/`。
> 下文保留评估当时的历史路径与决策语境，不改写历史事实。

scripts/noise_world/worldgen/WorldGenManager.cs:18,19,32,36
    NoiseCellPanel _cellPanel;  NoiseDock _dock;
scripts/noise_world/worldgen/WorldGenMapModes.cs:16,26,39,45,49,75
    CreateAll(WorldGenPlanet) → 4 个 sealed class : NoiseMapMode
```

**⚠️ 修订（2026-10-05，由契约测试 `NewWorldLine_MayDependOnApprovedRenderContracts` 实测抓出）**：
本报告初版断言新线只依赖旧线 **2 类**表现资产（`NoiseBallView` + `NoiseMapMode`），
**这是错的**。主入口场景 `WorldGenWorld.tscn` 的面板层实际挂的是旧线脚本：

```
scenes/core/WorldGenWorld.tscn:6  ext_resource → scenes/noise_world/NoiseCellPanel.tscn
scenes/core/WorldGenWorld.tscn:7  ext_resource → scenes/noise_world/WorldGenDock.tscn
    NoiseCellPanel.tscn:3 → scripts/noise_world/NoiseCellPanel.cs
    WorldGenDock.tscn:3  → scripts/noise_world/NoiseDock.cs   （仅场景根节点改名）
```

⇒ B 线实际有 **4 类**表现资产被新线消费。经核实两者均为**纯哑组件**（生成语义为零）：
- `NoiseDock` 只发 `ModeSelected(int)` 信号 + 按鼠标更新按钮高亮，不认识任何生成类型
- `NoiseCellPanel` 只被喂 `ShowCell(id, lat, lng, elevM)`，不感知星球与相机

**教训**：我初版用 `grep "NoiseWorldManager|NoiseBallView"` 找引用，漏掉了**场景文件里的
`.tscn` 引用**——`WorldGenManager` 引的是 `NoiseDock`/`NoiseCellPanel`，不在我的搜索词里。
**依赖扫描必须同时覆盖 C# 与 `.tscn`**，因为 Godot 的场景挂载本身就是引用边。

`NoiseParamPanel` 则确认为纯清退对象（唯一消费者是旧场景 `NoiseWorld.tscn`）。

### 4.2 接入位置

| 内容 | 目标位置 | 需要的调整 |
|---|---|---|
| `NoiseBallView` | `scripts/Render/` 或就留 `noise_world/` 但改名 `BallView` | ① 类名去掉 `Noise` 前缀（新线已无"噪声线"概念）；② 两个构造函数收敛为一个（`float[]` + 高度语义参数）；③ 拆出 `PickCell/TryGetElevation/HighlightCell`（新线消费）到独立 `CellQuery`；④ 内部 `ElevationBandMode.BandName` 静态引用需换掉 |
| `NoiseMapMode` | `scripts/Render/` 或 `worldgen/` | 基类（4 成员 + `DivergingStops`/`QuantileStops`）可直接搬；旧 5 个吃 `NoiseTerrain` 的模式删除；`CreateAll(terrain, plates, climate)` 签名废弃（已被 `CreateAll(planet)` 取代） |
| **`NoiseDock`** | `scripts/Render/UI/` 或保留原位改名 | ★报告初版遗漏（见 §4.1 修订）。去 `Noise` 前缀；`BindModes`/`SetMode`/`ModeSelected` 接口可原样保留。**注意 `WorldGenDock.tscn` 只是场景根节点改名，脚本仍是 `NoiseDock.cs`** —— 改类名要同步改 `.tscn` 的 ext_resource 路径 |
| **`NoiseCellPanel`** | 同上 | ★报告初版遗漏。同上；`ShowCell` 签名不变。内部调`NoiseBallView.BandName`，随 BallView 迁移而改 |
| 地球拟合 LUT | `scripts/noise_world/worldgen/` 新文件（如 `EarthDistribution.cs`） | 纯 `static readonly float[]`，建议加注释指向数据源；**但需先过"现在有真实消费者吗"**——`HeightComposer` 当前不用它 |
| `SplineInverse` + Fritsch–Carlson | 若采用，放入 `worldgen/` | 需要先决定是否恢复"地球高程曲线"表达；当前 `HeightComposer` 的 lerp 链不需要 |
| `DeterministicRandom` 偏移模式 | 已在 `SeedDerivation.cs` 落实 | 只需补充旧线的偏移常量表作参考，无需迁代码 |

### 4.3 依赖冲突与兼容性风险

**风险1：命名空间误导（高）**
旧线`World.NoiseWorld` 与新线 `World.NoiseWorld.WorldGen` 是**父子命名空间**。C# 里 `World.NoiseWorld` 内的类型能直接看到 `World.NoiseWorld.WorldGen` 的类型，反之不行。这造成：
- 新线文件只要在 `namespace World.NoiseWorld.WorldGen` 里，**不加 `using` 就能直接用 `NoiseBallView` / `NoiseMapMode`**（`WorldGenMapModes.cs` 就是这么干的）
- ⇒ 架构契约测试 `NewWorldLine_DoesNotDependOnLegacyWorldLine`（`ArchitectureContractTests.cs:40`）用"不得引用 4 个旧命名空间"来把关，但**旧线根命名空间 `World.NoiseWorld` 不在禁列**（因为它同时是新线的父命名空间）

**这是一个真实的契约漏洞**：新线对 `Noise*` 表现层的依赖是"合法"的，但没有被任何测试标记为"这是有意保留的例外"。

**风险2：`TerrainNoiseParams` 双事实源（高）**
`ElevationFieldStack.cs:74` 明文声明"不与之共享实例"。但两者都暴露 `LandFraction`语义（旧线 `LandFractionTarget=0.29`，新线 `WorldGenPlanet.LandFraction`）。若将来有人误接，**海陆比会被静默改掉**——因为旧线是"陆板数占比"、新线是"面积分位占比"，同0.29 在两种口径下**结果完全不同**。

**风险3：配置分散（中高）**
`NoisePlanet`（旧）与 `WorldGenPlanet`（新）各有独立 `ResLevel` / `Radius` / `LodNearRatio` / `BackfaceCullRatio`。`NoiseWorldManager.cs:13-14` 自己都留了警告："星球 Radius 导出与相机 `_planetRadius` 场景覆写须同值；改一处须同步另一处"。迁移时**极易漏掉相机侧**。

**风险4：存档格式（低）**
旧线参数导出 JSON 的结构 = `TerrainNoiseParams` 字段树。若面板机制废弃，`userdata/` 下的旧 JSON 无人能读。需先确认有没有真实存档依赖。

**风险5：`BiomeColors` 双份（中）**
`NoiseClimate.cs:41-56` 有一份13 项 `Color[]` 字面量；`scripts/Domain/BiomeColors.cs` 是从 `Biome/BiomeColors.cs` 迁来的。两者**可能不一致**（色值不同则地图颜色与旧存档对不上）。需实测比对。

### 4.4 建议的收口形态（不是唯一解）

```
scripts/Render/BallView.cs          ← NoiseBallView 去噪前缀，核心保留
scripts/Render/CellQuery.cs         ← 拆出 PickCell/TryGetElevation/HighlightCell
scripts/Render/MapMode.cs           ← NoiseMapMode 基类 + 两个色带常量
scripts/Render/UI/Dock.cs           ← NoiseDock 去噪前缀
scripts/Render/UI/CellPanel.cs      ← NoiseCellPanel 去噪前缀
scripts/noise_world/worldgen/EarthDistribution.cs  ← 可选，LUT 常量
─────────────────────────────
scripts/noise_world/NoiseTerrain.cs    ✗ 删（新线 TectonicField+LandSeaField+HeightComposer 已覆盖）
scripts/noise_world/NoisePlates.cs     ✗ 删（新架构不建模板块）
scripts/noise_world/NoiseClimate.cs    ✗ 删（降水/温度由 PrecipitationModel + 未建）
scripts/noise_world/TerrainNoiseParams.cs ✗ 删（参数已四分）
scripts/noise_world/NoisePlanet.cs    ✗ 删（已被 WorldGenPlanet 取代）
scripts/noise_world/NoiseWorldManager.cs ✗ 删（已被 WorldGenManager 重写）
scripts/noise_world/NoiseParamPanel.cs    ✗ 删（唯一消费者是旧场景）
scripts/noise_world/NoiseMapModes.cs  △ 只留基类+色带，删 5 个吃 NoiseTerrain 的模式
scenes/core/NoiseWorld.tscn           ✗ 删
scenes/noise_world/NoiseParamPanel.tscn ✗ 删
```

注意：这个收口动作会**移除 `World.NoiseWorld` 根命名空间下的全部类型**，届时新线的父命名空间 `World.NoiseWorld.WorldGen` 就没有父了——**那时才应该考虑把新线整体搬到 `scripts/worldgen/` 并用 `World.WorldGen`**。这一步是独立的第二阶段决策，不建议和第一步混做。

---

## 5. 优先级与工作量预估

单位：人时（含实测验证，不含纯敲代码时间）。依据：本次已通读全部核心文件并实测了依赖边界。

### P0— 决策与信息补齐（不写代码）

| 项 | 内容 | 工作量 | 理由 |
|---|---|---|---|
| P0-1 | **B线去留拍板**：迁移 or 废弃。`scripts/noise_world/` 根 11 文件 + `NoiseWorld.tscn` 的最终归属 | 0.5h | 一切后续工作的前提。决策 07 §12.1 的四步清退路径（①非生产→②非 Final 真相源→③有限兼容→④最终删除）**对 B线只走完了①**（主入口已切），②③④ 全未做 |
| P0-2 | 实测 `BiomeColors` 两份是否一致；确认 `userdata/` 旧 JSON 有无真实读取方| 0.5h | 风险4/5 未验证，不能凭推断下结论 |
| P0-3 | 把 `ElevationFieldStack.cs:74` 的"不共享实例"声明升级为**契约测试** | 1h | 双事实源目前只有注释保护，无测试保护 |
| P0-4 | 补`newdecision/08`（代码/测试多处引用"决策 08 v2"但文件不存在） | 1h | 决策链断裂 |

### P1 — 表现层收口（推荐主线）

| 项 | 内容 | 工作量 | 风险 |
|---|---|---|---|
| P1-1 | `NoiseBallView` → `scripts/Render/BallView.cs`，收敛为单构造函数 | 3h | 中：改完必须跑渲染实测（`NoiseShotDiag` / `RiverShotDiag` 已有基线可对比） |
| P1-2 | 拆 `CellQuery`（拾取/高亮/海拔查询） | 2h | 低：`CellHighlightRingTests` 已钉住高亮行为 |
| P1-3 | `NoiseMapMode` 基类 → `scripts/Render/MapMode.cs`；删 5 个旧模式 | 2h | 低：`WorldGenMapModes` 已继承，改动面小 |
| P1-4 | 删旧线生成层+ UI + `NoiseWorld.tscn` | 2h | **中**：需确认无残留引用；`project.godot` 主入口已是新线，风险可控 |
| P1-5 | 更新宪法文档（`docs/architecture.md`、`docs/架构设计.md`、`docs/开发规范.md` 三份**整篇描述已删除类型**） | 3h | 低，但**价值高**——这是本仓库最大的文档断层 |

### P2 — 算法资产择机回收（**须先有消费者**）

| 项 | 内容 | 工作量 | 前置条件 |
|---|---|---|---|
| P2-1 | 地球拟合 LUT → `EarthDistribution.cs` | 1.5h | 必须先确认 `HeightComposer` 要不要引入地球分布先验。**当前无消费者 ⇒ 建议不做** |
| P2-2 | `SplineInverse` + Fritsch–Carlson 移植 | 4h | 同上；且要重新设计接入点（当前 lerp 链不需样条） |
| P2-3 | `PropagateMax` 带权扩散 | 2h | 需要出现"非距离场的带权多源扩散"真实需求 |

**合计**：P0 约 3h / P1 约 12h / P2 约 7.5h。

**强烈建议**：P2 全部**暂缓**。依据是项目已冻结的**防膨胀三问**——第3 问"现在有真实消费者吗"，P2 三项全部答"没有"。这与记忆里River v2.1"会退回已经正确 → 为了更漂亮继续增加系统"的教训是同一条。

---

## 6. 副作用与回归风险

### 6.1 迁移过程中的副作用

1. **父命名空间消失的连锁**：删掉根命名空间全部类型后，`World.NoiseWorld.WorldGen` 会成为"没有父的子命名空间"。虽然合法，但语义上奇怪，且若后续要做 namespace 级测试或打包会别扭。**建议把这一步单独排期。**

2. **`BiomeColors` 色值漂移**：若发现两份不一致，改哪份都会改变地图颜色——而地图颜色是对判读最敏感的表面。必须先实测再决定，不能凭代码顺序猜。

3. **`ElevationBandMode.BandName` 被静态引用**：`NoiseBallView.cs:295` 直接调`ElevationBandMode.BandName(m)`。拆文件时若先删旧模式会编译失败——顺序上要"先加后删"。

4. **相机侧 Radius 失同步**：`NoiseWorldManager.cs:13-14` 的警告是实测教训（取景/裁剪 ∝ R）。删旧场景时若`OrbitalCamera._planetRadius` 还残留旧覆写，会表现为"取景错但不报错"——**沉默失败**，测试抓不到。

5. **`WorldGenPlanet` 的 `[Export]` 与旧面板并存期**：若迁移中途两个场景都存在，两套 `ResLevel/Radius` 会同时出现在 Inspector 里，**改错的那个不报错**。

### 6.2 回归风险点与现有护栏

| 风险 | 现有护栏 | 是否够 |
|---|---|---|
| 表现层改动破坏渲染 | `NoiseShotDiag` / `RiverShotDiag`（`--aim=longest\|confluence\|outlet\|bend`） | **够**——基线已冻结，有量化判据 |
| 高亮行为回退 | `tests/World.Tests/CellHighlightRingTests.cs` | 够 |
| 生成层回退 | 473 条 `[Test]` + `ArchitectureContractTests` 16 条 | 够——但注意**新线不测旧线**，删旧线不影响新线测试 |
| 契约漏洞被扩大 | 无 | **不够**——见 4.3 风险1，建议 P0-3 补测试 |
| 地图颜色漂移 | 无对比基线 | **不够**——建议 P0-2 先做逐色比对并留基线 |
| 存档不兼容 | 无 | **不够**——但需先确认有无真实存档（P0-2） |

### 6.3 三个最容易被误判的点

1. **不要为"统一参数"而统一。** 旧线 `[NoiseParam]` 面板与新线 `[Export]`+`const` 并存是**结果不同、纪律不同**的两套工作流（拖滑块看球vs 改 const 跑测试）。强行统一是范围膨胀。

2. **不要把旧线的地球 LUT 当"新线缺的东西"急着补。** 新架构高度链是 `lerp + 三档变化噪声 + MinLandElevationM 保底`（`HeightComposer.cs:33-37`），这是**决策产物**（见记忆：河流阈值 70,800 km² 是工程标定值而非自然常数）。换 LUT = 推翻已冻结决策。

3. **不要复活"板块"。** 新架构从 TectonicField 到 FinalGeography，`RegionType` 枚举 7 值里也没有板块。`NoisePlates` 是旧范式的产物，复活它= 引入一个已清退的并行真相源。

---

## 附：核对要点（本次已实测，非推断）

- `scripts/noise_world/` 根 11 个 `.cs`，namespace 统一 `World.NoiseWorld`
- 主入口 `project.godot:15` = `res://scenes/core/WorldGenWorld.tscn`（非 `NoiseWorld.tscn`）
- 新线引用旧线的**全部位置**已用grep 穷举：仅 `WorldGenPlanet.cs:68,80`、`WorldGenManager.cs:20`、`WorldGenMapModes.cs` 6 处
- `scenes/core/NoiseWorld.tscn` + 3 个 `scenes/noise_world/Noise*.tscn` 仍存在
- `scripts/Constant/` 是**空目录**（无代码）
- git暂存区：237 项 `D`（含 206 `.cs`）、7 项 `R`、20 项 `A`——**未 commit**，回滚方式 `git restore --staged . && checkout .`
- 测试规模：473 条 `[Test]`（`tests/World.Tests` 49 文件），历史峰值 683

**本次评估未修改任何代码或文件（报告本身除外）。**