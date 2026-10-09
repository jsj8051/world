# 裁决：新建数据层 `World.Data`（目录 `scripts/Logic/Data/`）

日期：2026-10-09
状态：**已执行**（未提交）
触发：用户连问「`ContinentAnchor` 能不能直接 `static`」→「下游只用到其中一个变量、它本质是非状态类，是不是可以改 `static`」
→「它是内部类还是外部类，是不是只有 `ContinentLayout` 会用」→ **「那就把它放在数据层吧，建立一个数据层管理」
＋「命名规范唯一，不要有误解和名不副实」**
→ 三个落点问题用户选定：**顶层 `Logic/Data` → `World.Data`**｜**全部纯数据载体**｜**管理 = 补一行登记**（不建目录级契约、不建运行时管理器）
关联：`docs/裁决-Spatial改名H3Grid.md`（同日的目录/命名空间治理）、`docs/architecture.md` §9

---

## 〇 裁决表

| 项 | 改动 |
|---|---|
| 命名空间 | **新建 `World.Data`**（与 `World.WorldGen` / `World.Constants` / `World.H3Grid` / `World.Utils` 平级） |
| 目录 | **新建 `scripts/Logic/Data/`**（首层领域名逐字对应命名空间） |
| 类型 | 4 个纯数据载体**搬出**原文件，各自成文件：`ContinentAnchor` / `LandSeaParams` / `MountainRidge` / `Scale3` |
| 引用 | 在编 **30 个已跟踪文件**补 `using World.Data;`（游戏侧 7 + 测试侧 23）；新增文件 4 个（含 `.cs.uid` 4 个） |
| 契约 | 4 个类型**登记** `ArchitectureContractTests.NewWorldLineTypes`；顺手删除该文件里一份**手抄的清单副本** |
| 未做 | 目录 ↔ 命名空间钉（用户选择"补一行登记就够"）；运行时"数据管理器"（无消费者，撞防膨胀第③问，不建） |

**一句话**：这批类型此前寄生在生成器文件里（一个 `.cs` 装 2-3 个类型），既看不出"世界线有哪些数据形状"，
也让契约登记只能靠人肉点名；建层后归属明确、名字唯一、文件与类名一致。

---

## 一 入层判据（机器可验，不靠印象）

```
顶层类型（声明位于第 0 列，非嵌套）
  + 零方法（构造函数除外）
  + 零计算属性（无 `=> 表达式` 成员）
  + 不引用生成域类型（否则 `World.Data → World.WorldGen` 反向依赖成环）
```

扫描口径（`scripts/Logic/**`，排除 `_removed/`）：剥离注释与字符串后，按
`^(修饰符)* 返回类型名 成员名(` 行锚定方法（排除 ctor / `if`/`for`/`return`/`new`… 等控制关键字），
声明体按大括号配对（位置参数记录按圆括号配对）。

`readonly struct` / `record struct` **不是排除项**（`Scale3` 即是），但**嵌套**是——
这正是 `ContinentAnchor`（顶层）与 `GeoRegions.Region`（嵌套）的分野。

---

## 二 首批 4 个（入层）

| 类型 | 原文件 | 形状 |
|---|---|---|
| `ContinentAnchor` | `WorldGen/Placement/ContinentLayout.cs` | 8 字段：中心方向 + 椭圆影响参数 + 海岸性格 |
| `LandSeaParams` | `WorldGen/Placement/LandSeaFields.cs` | 11 字段：海陆场参数小表 |
| `MountainRidge` | `WorldGen/Features/MountainSkeleton.cs` | 6 字段：脊线点列 + 逐点高度 + 高斯宽度 |
| `Scale3` | `WorldGen/Features/TerrainFeature.cs` | 3 字段 readonly struct：纵向/横向/垂向尺度 |

---

## 三 边界件（**未**入层）与理由

| 类型 | 位置 | 为什么**不**纳入 |
|---|---|---|
| `GeologicalRegions.Region` | Placement/GeologicalRegions.cs | **嵌套类型**；且 `Region` 名字太泛，升到 `World.Data.Region` 反而制造歧义（违反本次"命名唯一"要求）——要动应先改名 `GeologicalRegion`，属独立裁决 |
| `FeatureField` | Composition/HeightComposer.cs | 顶层零方法，但字段类型是 `ITerrainField` / `TerrainDomain`（**生成域类型**）⇒ 入层会造成 `World.Data → World.WorldGen` 反向依赖，与 `WorldGen → Data` 成环 |
| `MountainSystemFeature` / `PlateauFeature` / `BasinFeature` / `VolcanoFeature` | Features/* | 是 `TerrainFeature` 的**子类**（行为家族成员），不是数据载体（`MountainSystemFeature` 是空壳子类，故扫描显示"零成员"） |
| `Thermal` / `MapSeaColor` | Constant/ , Scene/Render/Constants/ | `static class` **常量族**，已有归属（`World.Constants` / `World.Render.Constants`） |
| `ColorStop` / `LatLng` | Utils/Color/ColorRamp.cs , Utils/H3/H3.cs | **嵌套**类型，且属各自工具 API 的形状 |
| `RiverLineOverlay` | Scene/Render/Overlays/ | `MeshInstance3D` **Godot 节点**（表现层）；其命名空间属"刻意例外"，见 §五 |

---

## 四 命名收口（用户要求："唯一、不误解、不名不副实"）

1. **4 处 `文件名 ≠ 类名` 已修**：被搬迁类型的寄生关系解除，4 个新文件各自 1 类型。
2. **删除一份手抄的清单副本**：`ArchitectureContractTests.NewWorldLine_DoesNotDependOnLegacyWorldLine`
   原本内联一份**手抄的 `NewWorldLineTypes` 子集**（21 个 `typeof(...)`）——与已删的 `MapMode.Id` 同类冗余
   （同一事实写两遍：加类型要记得改两处，漏一处就是**静默的覆盖缺口**）。现改为直接复用
   `NewWorldLineTypes`（是其超集，且被旧线类型守卫共用）⇒ **一处真相源**。
3. **残留债（登记在案，未动）**：`SphericalField.cs`(6 类型) / `RegionalLandforms.cs`(3) /
   `LandSeaFields.cs`(2) / `MountainSkeleton.cs`(2) / `GeologicalRegions.cs`(2) / `VolcanoField.cs`(2) /
   `HeightComposer.cs`(2) / `ColorRamp.cs`(2) / `H3.cs`(2) / `H3Native.cs`(2)——
   收口属**独立批次**（变更边界不同，勿与生成语义改动混做）。

---

## 五 守卫边界（本次**未**改，属风险提示）

仓库有两条**按命名空间判定**的守卫：

- `FinalWorldFacts_OnlyConsumedByWorldGen`：豁免"ns ∈ `World.WorldGen*`"的消费方；
- 表现层契约：新线表现依赖必须且只能落在 `World.Render` 体系。

⇒ 类型搬出 `World.WorldGen` 后，若将来有**表现层**类型消费 `World.Data` 的载体，
上述守卫**不会**把它判成"表现层引用了生成域"（因为名字里没有 `World.WorldGen`）——
即**按 ns 判定的守卫存在被绕过的缝**。

**本次未改契约**，理由：① 当前**零**表现层消费者（`Scene/**` 全域未引用这 4 个类型）；
② 改契约属独立变更边界。**建议后续**：把这批守卫的判定集从"名字前缀匹配"改为显式的
"世界线侧命名空间集合 = {`World.WorldGen`, `World.Data`}"。同类既有现象：`Scene/Render/Overlays/`
与 6 个地图模式的**命名空间刻意写成 `World.WorldGen`**（为满足上面的豁免），是同一根因的另一面。

---

## 六 验证（本次实测）

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 警告 / 0 错误** |
| `dotnet test scripts/Test/World.Tests` | **281 PASS / 0 失败**（= 基线，未增减） |
| `dotnet build scripts/Test/PerfBench` | 0/0（**不在 `world.sln`**，须手工构建） |
| headless 世界生成 | `n=288122 res=4 land=29.0% regions=95 meanP=1098mm/年 meanT=76.0C ridges=71 basins=6971(endorheic 0) lakes=3439 thr=0.636` —— **逐字段同基线** |
| `WORLDGEN-READY` / `Cannot load C# script` / 任何 `ERROR` | `WORLDGEN-READY ViewCtor=5ms BuildChunks=988ms RiverLinesBuild=31ms`，**零 ERROR** |
| `.cs.uid` | 4 个新文件均已由 `--headless --import` 生成（`.cs`/`.uid` 成对） |

> ★口径提醒（同 `裁决-Spatial改名H3Grid.md`）：纯"换命名空间 + 搬文件"的重构，
> **build 与单测全绿证明不了什么**（它们本来就该绿）。真正的兜底是 **headless 实机读数逐字段比对**。

---

## 七 工作区状态（提醒）

本批改动与**开工前既有的未提交改动**（六阶段管线重构：`Sim.Terrain.*` / `_p.Facts.Final` 等，
8 个文件）**共处一个工作区**；其中 `LandSeaPipeline.cs` 两边都碰（本批只加了 `using World.Data;`）。
提交时**须按变更边界分开**（原则 13）。

---

## 八 回滚

```bash
git checkout -- scripts/Logic/WorldGen/...      # 撤回到原文件（类型回到寄生位置）
rm -rf scripts/Logic/Data/                      # 数据层目录（含 .cs.uid）
# 再撤 using 与契约登记即可；本批无 git mv，故不涉及目录搬家式回滚
```
