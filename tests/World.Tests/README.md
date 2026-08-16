# tests/（国家常备军）

NUnit 单元测试项目，引用 `world.csproj`（游戏程序集）。覆盖**单元测试**（单类纯函数/机制契约）与
**模块测试**（跨类不变量：确定性、守恒、往返、结构性质）。

> 当前规模（2026-08 实测）：**463 用例全部通过**（本地执行器，含全部模块）。

## 测试范围（重要）

- ✅ 只测 **L0 纯模型/纯函数**：不触碰 Godot 原生调用（`GD.*`、`LogService`、`FileAccess`、
  `DirAccess`、`FastNoiseLite`、`StringName/Variant`、节点类等），因此可直接 `dotnet test` 运行，
  无需 Godot 引擎。
- ⚠️ **探针实测（2026-08）**：Godot 数学类型（`Vector3`/`Color`/`Mathf`/`Basis`/`Quaternion`/
  `Transform3D` 等）为纯托管实现，无引擎安全可用；而**任何引擎原生调用在测试进程 = 进程级崩溃
  0xC0000005（不可捕获，杀死整个测试运行）**——测试路径必须绝对避免，包括被测试方法内部间接调用
  （读源码确认；某方法内部无条件调用引擎 API 时不调用该方法）。
- ✅ 确定性不变量：同 seed 同序列、读档续跑无分叉（`DeterministicRandom` 状态往返、`CivEngine.Continue`
  与"从头跑 N+k"逐项一致 = T04）、同输入两次演化逐项一致。
- ❌ 不做重集成测试：headless 回归（构造全流程、存档往返）由 `scripts/verify.sh` 承担——
  那是既有体系，测试项目不与它重复。
- ❌ 引擎依赖路径（存档读写 `FileAccess`、`CivEngine.Run` 的 `TechTable.Load`/`PerfLog`/`GD.Print`、
  Tectonics 全模拟入口 `Run`/`GenerateInitialCrust` 等的 `FastNoiseLite`/无条件日志、
  `MonsoonSystem.Compute` 需要的 `ClimateGenerator` **引擎噪声实现**等）不可测——测试文件中以注释说明
  跳过原因；`ClimateModel.Run`/`PlanetPipeline` 场编排经注入纯托管噪声的 `ClimateModel.Run` 复现覆盖。

> ⚠️ **引擎适配器重构（2026-08，替代早期"测试缝"）**：核心逻辑与引擎依赖分离——
> - `ISphericalNoise`（纯托管接口）+ `FastNoiseLiteNoise`（引擎适配器）：`ClimateGenerator` 构造注入
>   噪声实现（`null` = 生产默认引擎实现，行为不变）；测试注入恒零/确定性实现即可测物理公式与
>   季风/管线。
> - `ClimateModel.Run`/`PlanetPipeline.Run` 路径**无日志**（纯计算+校验）；模型状态报告抽为
>   `ClimateModel.PrintReport`，由生产 {MapGenerator 同步路径} 在 Run 后按需调用。
> - `Icosahedron.Subdivide` 为纯几何函数（日志已移除，`log` 参数已删除）——无需任何缝。
> 测试一律 `Icosahedron.Subdivide(n, r, out v, out i)` 直调。

> 🐞 测试挖出的生产缺陷（已修/待复盘）：
> - ✅ 【已修】`ClimateGenerator.ComputeTemperature` 极点 float 量化：`cos(asin(1))` 略负 →
>   `Pow(负底,1.1)=NaN` → 极点温度全链路 NaN（原仅靠 `SanitizeNaNs` 抹 0°C，值错误）。已 `Max(0, cosLat)`，
>   极点温度恢复 = −22°C。
> - ⚠️ 【已修】`TectonicsSimulation.Convergent.UpdateSubducted` 的"消减移除"分支曾是**死路径**（`justInside`
>   用了 mask **外扩**层 `Margin`，与 `willStay`（被俯冲**内部**层）交集落在板外 → 高密度老洋壳永不消减
>   回地幔、`Accretion` 恒 0、造山带闭环拿不到物质）。已改为 mask **内**边界层
>   （`mask − Erode(mask,1)`：mask 中与板外相邻的格 = 埋板前缘），负浮力埋板边缘按设计消减并守恒入
>   `Accretion`；测试现为移除正测（`removed>0`、`Accretion>0`、消减守恒）。
>   ⚠️ 该修复改变板块模拟结果（消减开始回收老洋壳）——**建议重跑 `scripts/verify.sh` headless 回归**复核。

## 覆盖图景（tests/World.Tests/）

| 文件 | 覆盖 |
|---|---|
| `DeterministicRandomTests.cs` | 确定性基石：同 seed 同序列、状态往返续跑、边界 |
| `CivSimModelTests.cs` | CivSimContext 静态纯函数：Miami NPP、冲积因子、收益判据、影响力/产出权重查表、猎物比例、冷区 |
| `CivSimMechanicTests.cs` | 注册表/商品目录/能力表；Growth/Origin/SplitMigrate/Settlement/Energy/Cultivate/Trade/War 单模型隔离；Territory/Chiefdom/State 纯派生重建；模块确定性（等价 tick 循环两次逐项对比 + 多 seed 分叉） |
| `BiomeTests.cs` / `BiomeClimateTests.cs` | BiomeClassifier 柯本分类全带表（含南北半球干季/阈值边界）；BiomeType 序列化值；BiomeColors 色板全覆盖/插值；WindField 环流带/切平面/自转翻转/海陆分；OceanCurrent 小网格不变量 |
| `HexPlanetTests.cs` | Icosahedron 顶点公式/反推/大 n long 安全/Subdivide 计数球面唯一性（纯几何函数，无日志直调）；SubdividedMesh 去重/三角邻居；GoldbergBuilder 五边形/六边形经典计数（12/12+30/…）、邻居对称、手工 icosahedron 全五边形 |
| `MapGenTests.cs` | FieldCodec 往返/钳位；WildCrops 确定性/斑块/畜牧；Soil 查表；Mineral 编解码（含 &0x03 掩码）/海洋 0；River 流向单调/链式流域成河/确定性/RebuildPaths；ClimateModel 注册表结构与 Verify |
| `LogicGridTests.cs` | ArchiveLayout 布局字节数（v1/v2、大 n long 安全、字段表反射对照）；GameGrid 邻接/海陆/距离/面积/OverrideNeighbors 钩子/野生资源/ToMapData 往返 + 邻接重建一致（模块测试） |
| `ServicesTests.cs` | EventBus 发布订阅/消费语义；PlanetColors 端点/边界；PowerPalette 最远点采样色距/顺序无关；TileIndex 面↔顶点不变量/缓存 |
| `TectonicsTests.cs` | FieldOps 场运算/形态学/插值/梯度/扩散；MatrixOps 正交/逆/旋转向量；Crust 池访问/质量/厚度/密度/浮力/均衡位移/AddDelta/ModelErosion 等守恒；Plate 映射/重采样/Move；SphereGrid 邻接/最近邻；Tectonophysics 纯函数；TectonicsSimulation ctor/MergePlatesToMaster 守恒/ApplySurfaceProcesses/SyncWorldToPlates 模块测试 |
| `GridFeatureVerifyTests.cs` | 演示验证：构造 n=2 演示网格（42 胞：北半球陆地/南半球海洋）→ **逐个功能单独验证**（邻接/海陆/距离面积/层1生产力/野生作物/畜牧/土壤/矿藏/河流/洋流/风场/存档布局/往返），每个功能一个独立 `[Test]` |
| `CivSimMechanics2Tests.cs` | **CivSim 模拟补全**：CivEngine 纯静态（RefreshCellState 三部曲/AccumulateStorage 衰变与容量/RecomputeProduction/DeriveLeadership/SettleDerived 幂等）；单模型隔离 Harvest/Influence/Absorption/Mode/Invention/Spread/Prestige/Culture/Religion/Conflict/War 守卫；模块确定性 `CivEngine.Continue` 续跑 = 从头跑 N+k（T04） |
| `TectonicsScenariosTests.cs` | **Tectonics 深场景 + River 迭代模拟**：真实裂谷（洞 → 新洋壳/mask 扩展/Merge 一致）；UpdateSubducted 深埋变质与多板矿化事件计数（⚠️ 移除分支为死路径，见上缺陷记录）；ApplyAccretion 手工增生分派+复位；TryMergeCollidingPlates 阈值 NoOp；River ComputeIterative 多轮演化确定性/ApplyErosionDepositionV2 输沙上下限/MarkRiversLakes 关卡/ComputeWatersheds 三类归属 |
| `ClimateSimTests.cs` | **气候模拟链**（注入纯托管 ZeroNoise）：ClimateGenerator 温度/降水/月基准公式（纬度带、海拔 6°C/km、倾角、辐照度、洋流修正）；MonsoonSystem.Compute 完整季风不变量（12 月 Σ=1、tHot≥tCold、季风∈[0,1]、确定性）；ClimateModel.Run 拓扑执行 17 场+8 环；管线端到端（42 顶点全场有限/无 NaN/minmax 序/确定性/物理合理性） |

## 运行

CI / 正常本机环境：

```bash
dotnet test tests/World.Tests/World.Tests.csproj
```

受限/离线环境（无法启动 vstest testhost 或访问 nuget.org）——用本地执行器跑**同一套**测试：

```bash
dotnet build tests/World.Tests.Local/World.Tests.Local.csproj -m:1 -nodeReuse:false -p:NuGetAudit=false
dotnet run --project tests/World.Tests.Local/World.Tests.Local.csproj --no-build
```

> 两个坑（2026-08 实测）：
> 1. 本机只有 .NET 6/10 运行时，无 8.0 → 本地执行器 csproj 已设 `RollForward=LatestMajor`；
>    `dotnet test` 路径可临时 `$env:DOTNET_ROLL_FORWARD='LatestMajor'`。
> 2. 受限沙箱禁 MSBuild 工作进程管道 → 构建必须 `-m:1 -nodeReuse:false`。
> 3. 沙箱内 vstest testhost 无法启动（父进程句柄被禁）→ 本环境验证用本地执行器。

> NuGet 包版本（NUnit 3.14.0 / NUnit3TestAdapter 4.5.0 / Test.Sdk 17.9.0）
> 与本机 `~/.nuget/packages` 缓存一致，离线可还原；联网环境从 nuget.org 还原同版本。

## 约定

- 新测试必须能回答"这个函数保证什么"，而不是"这个函数怎么算的"（避免测试复刻实现）。
- 改公式/查表（如 `InfluenceWeight`）时必须同步改本项目的断言——查表值即契约。
- 本地执行器只支持 `[Test]`/`[TestCase(字面量)]`：禁用 `[SetUp]`/`[TearDown]`/`[TestCaseSource]`/
  `[Theory]`；不用 `Assert.Pass`/`Ignore`/`Warn`（本地执行器会记 FAIL）。
- 测试必须确定性、快速（合成小网格，n≤8）、不写文件、不联网；浮点断言带容差。
- 静态可变状态（如 `WindField.Prograde`）在测试内改后必须还原（try/finally）。
- 有失败测试存在时，本地执行器可能把先前失败附加进后续失败消息（NUnit 作用域未重置）——
  修复所有真实失败后即消失；定位时看每个测试的**首个**失败项。
