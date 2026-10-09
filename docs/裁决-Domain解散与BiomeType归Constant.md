# 裁决 · `World.Domain` 解散 与 `BiomeType` 归 `World.Constants`

> 2026-10-09 · 用户拍板：「**PowerPalette 和 Calendar 可以删除，然后 biometype 放到 constant 中，
> 只放柯本分类的生物群系，然后 biomecolors 应该是地图模式做的事情，这里先删除吧**」
> 前置：`docs/裁决-Legacy载体簇删除.md`（同日删除 Legacy 载体簇后，`World.Domain` 只余 4 件）
> 关联：`docs/裁决-P4-3-Biome事实定义.md`（本文**修订**其 §一 / §5.1 的 24 值词表）

---

## §〇 裁决（一句话）

**解散 `World.Domain`**（D-3 切分清退留下的"领域词汇保留区"）：`BiomeType` 迁 `World.Constants`
并**裁剪为仅柯本气候型**（18 值）；`PowerPalette` / `Calendar` / `BiomeColors` **删除**。

| 类型 | 处置 | 去处 / 理由 |
|---|---|---|
| `BiomeType` | **迁移 + 裁剪** | → `scripts/Logic/Constant/BiomeType.cs`，`namespace World.Constants`；词表裁到 2,3,14…29 |
| `BiomeColors` | **删除** | 色带职责归**地图模式**（`ElevationMode` / `TemperatureMode` / `PrecipitationMode` 各自持有）；本类运行期**零生产消费者**，只剩测试在借它测 `ColorRamp` |
| `Calendar` | **删除** | `MonthsPerYear = 12` 的唯一消费者是诊断 `Test/Diagnostics/FieldCompare.cs`（就地内联；★该文件当晚亦随 `World.Services` 连带删除——见 `裁决-Services删除.md`） |
| `PowerPalette` | **删除** | 唯一消费者 = 测试 `ServicesTests`；势力图层的真实调用方 `CivEngine` 已随 CivSim 移出 ⇒ 待**重新实现** |

⇒ 命名空间 `World.Domain` **整体消失**；目录 `scripts/Logic/Domain/` 删除。

---

## §一 词表裁剪：为什么"只放柯本"

原 **24 值** = 柯本气候型 `14–29` + `IceCap(2, EF)` / `Tundra(3, ET)`
**+ 6 个非柯本附加类**：`DeepOcean(0)` / `Ocean(1)` / `FrigidOcean(30)` / `TropicalOcean(31)`（水面）、
`Alpine(12)`（高山）、`Riparian(13)`（河岸带）。

**裁剪依据**：
1. **类别与语义不符**：枚举名为"生物群系类型"，但只有柯本类是**气候型分类**（判据 = 温度/降水的分布）；水面/地形类不属柯本体系。
2. **消费者已消失**：`Riparian` 的命名消费者 `WildCropsSystem.RiceSuit`（×1.3）随载体簇删除；`Alpine` 的命名消费者 `CivSimContext.IsColdZone` / 移动成本随 CivSim 移出；水面类的消费者亦已不在。
3. **编号不重排**：编号即语义且曾按 `byte` 写档 ⇒ 删掉的编号成为**空缺**，不重排。

**与 P4-3a 的冲突（已显式确认）**：`docs/裁决-P4-3-Biome事实定义.md` §一 曾冻结
"`BiomeFact` = 柯本气候型 **+ 水面/地形附加类**，沿用 24 值"，§5.1 曾**逐类登记 24 值**。
本次裁剪**直接命中那 6 类** ⇒ 用户确认「同步修订 P4-3 文档」（已在顶部加修订横幅）。

⚠️ **将来 P4-3b 若确需覆盖海格 / 地形的"全格生物群系事实"，应另立事实类型，不得回填本枚举**
——否则同格既有"气候型"又有"水面型"，语义重叠。

---

## §二 顺带修正：B 组判据注释漂移（k = 20 → k = 10）

`docs/裁决-P4-3-Biome事实定义.md` §3.3 记有一条**文档债**：`BiomeType.cs` 旧注释把 B 组写成
`P < 20·(T+14)` / `20·(T+14) ≤ P < 30·(T+14)`；而 Legacy 实测用 **k = 10**
（`P < 10·(T+14)` 沙漠；`10·(T+14) ≤ P < 20·(T+14)` 半干旱；取证 12/12 + 541/541 = 100%）。
⇒ 本次迁移**一并把注释改对**（`HotDesert` / `ColdDesertKoppen` / `HotSteppe` / `ColdSteppe` 四行）。

---

## §三 顺带采纳：`World.Planet` → `World.Constants`

`Constant/Planet/Thermal.cs` 本在 `namespace World.Planet`。按用户拍板统一常量族命名空间为
**`World.Constants`**（同一批提交）：
- `Thermal.cs` 命名空间改名；
- 唯一消费者 `WorldGen/Simulation/Temperature/Temperature.cs` 的 `using World.Planet;` → `World.Constants`。
⇒ `scripts/Logic/Constant/` 下现有两件同族常量：`Planet/Thermal.cs`（热常数）与 `BiomeType.cs`（气候型词汇）。

> ⚠️ 与 `scripts/Scene/Render/Constants/`（`World.Render.Constants`）**不是同一命名空间**，勿混。

---

## §四 连带改动（代码 / 测试）

| 文件 | 改动 |
|---|---|
| `scripts/Logic/Constant/BiomeType.cs` | 新位置（`git mv` 保 `.uid`）；`namespace World.Constants`；裁剪为 18 值；B 组注释 k=10 |
| `scripts/Logic/Domain/**` | 4 件全部离开 ⇒ **目录删除** |
| `scripts/Logic/WorldGen/Simulation/Temperature/Temperature.cs` | `using World.Planet` → `World.Constants`（+ 注释） |
| `scripts/Test/World.Tests/ServicesTests.cs` | **删除**（内容只剩 `PowerPalette` 测试） |
| `scripts/Test/World.Tests/BiomeTests.cs` | **重写**为 `World.Constants.BiomeType` 词表钉。★原文件所有方法**漏了 `[Test]`**（NUnit 从不执行 ⇒ 长期"绿"是假象）——本次补回 |
| `scripts/Test/World.Tests/ColorRampTests.cs` | 改用**夹具色带**测 `RampSample`（原借 `BiomeColors.TempStops`） |
| `scripts/Test/World.Tests/WorldGenMapModeTests.cs` | 撤除 `BiomeColors.TemperatureToColor` 域端 clamp 钉（被测对象已删） |
| `scripts/Test/Diagnostics/FieldCompare.cs` | 就地内联 `MonthsPerYear = 12`（★2026-10-09 晚：该文件其后随 `World.Services` 连带删除，**内联成果一并作废**——见 `裁决-Services删除.md`） |
| `scripts/Test/World.Tests/ArchitectureContractTests.cs` | 新增**结果钉** `DomainNamespace_IsDissolved`；同步 `World.Domain` 相关注释 |
| 注释同步 | `Logic/Utils/Color/ColorRamp.cs`、`Scene/Render/MapMode.cs`、`Scene/Render/Modes/Mapmode/TemperatureMode.cs`、`Scene/Render/Constants/MapSeaColor.cs` |

---

## §五 git 恢复

`BiomeType.cs` / `BiomeColors.cs` / `Calendar.cs` / `PowerPalette.cs` / `ServicesTests.cs` 均**已入库**，可随时取回：

```bash
git log --oneline -1 -- scripts/Logic/Domain/BiomeColors.cs
git checkout <删除前 commit>^ -- scripts/Logic/Domain/BiomeColors.cs
```

> ⚠️ 取回后契约 `DomainNamespace_IsDissolved` 会立刻变红——有意设计（删除是显式决策，回归也须显式）。

---

## §六 文档债（本次**不**覆盖）

多数提到 `World.Domain` / `BiomeColors` / `PowerPalette` 的文档是**带日期的取证/历史报告**
（`审查报告-*` / `架构设计-历史-2026-08.md` / `迁移评估报告-*` / `docs/newdecision/*`）——
**按纪律不改**（它们是当时的证据，不是现行描述）。

**仍属"现行"、但本次未逐条改的文档**（记为债）：

| 文档 | 待改点 |
|---|---|
| `docs/架构设计.md` | §1 基础设施框图（含 `World.Domain`）、§2 目录结构（**整篇早于三分区重构 `0a1408e`，腐化更早**） |
| `docs/decisions/0005-natural-input-bridge.md` | 措辞以 `World.Domain` 作"非事实之家"的举例 |
| `docs/设计-NaturalInputBridge实现设计.md` | §BiomeType「共 **24** 类」 |
| `docs/功能清单与结构重规划.md` / `docs/索引.md` | 目录清单含 `Domain/` |

> ★**为什么不一次改完**：上述文档的目录结构早在**三分区重构（`0a1408e`）**时就已失真，
> 属**独立文档债**（同 `Test/World.Tests/README.md` 的其余腐化）；本次变更边界 = `World.Domain` 解散，
> 不把"整篇文档重写"混进来（**永久原则 13**：变更边界不同 ⇒ 分开 commit）。
