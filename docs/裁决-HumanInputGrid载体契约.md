# 裁决 · ⑥ HumanInputGrid 载体契约

> 2026-10-06 · **纯裁决**（零代码 · **不实现 `HumanInputGrid`** · 不写映射）
> 前置：⑤ `docs/裁决-Bridge可行性裁决.md`（裁决 = `BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED`）
> 本轮（用户拍板）：**H3 res4 冻结为基础粒度** ⇒ ⑥ **不再讨论"res3 还是 res4"**——这一点已由生产原则决定。
> ⑥ 真正裁决的是**链路**：
> ```
> H3 res4 CellId → dense runtime index → CivSim topology / metric → 上层区域聚合
> ```

---

## §〇 裁决摘要

| 项 | 内容 |
|---|---|
| 裁决对象 | **载体链路**（不是"选哪档 res"） |
| res 档位 | **H3 res4 冻结**（= `WorldGenPlanet.ProductionRes`；已有可执行钉子，见 §四） |
| 状态变化 | `BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED` → **`BLOCKED BY MISSING WORLD FACTS`**（载体契约已 DECIDED） |
| 正式不变量 | **I1–I5**（§六）已写入 `ADR-0005 §不变量` |
| 本轮不做 | 不实现 `HumanInputGrid` / 不写桥 / 不补 CivSim 事实 / 不重标定数值（只裁形态，数值留待拍板） |

★ **为什么这一裁决成立**：它**正是项目第 3 条永久原则（"生产分辨率不得被子系统反向绑架"）的应用**——
不得因为 CivSim 在 res4 上更贵，就把 CivSim 降到 res3/res2；正确处置是**让 CivSim 适配 res4**。
（与水文的 D-11 处置同构：水文工作尺度集中在 res2，处置是让水文适配 res4，而非把世界降到 res2。）

---

## §一 ① Identity —— **冻结为 H3 res4 CellId**

**裁决**：`H3 res4 CellId` 是**世界线上的基础空间身份**；CivSim 可以用 **dense index 作运行时索引**，
但**不得把 dense index 当成世界身份**。

**链路**：

```
H3 res4 CellId（64-bit · 世界身份）
      │  Ball.CellIndexOf(cellId)        ← 已存在的 dense↔id 映射
      ▼
dense runtime index（0..N−1 · 仅运行时）
      │
      ▼
CivSim arrays（R[i] / CellPop[i] / CellPolities[i] / …）
```

**为什么这样分层**（实测依据）：

- `Ball` **已提供** dense↔id 双向映射（`CellIds[i]` + `CellIndexOf`），故"用 dense index 访问数组"零成本保留；
- 但 `Ball` 的 dense index 来自 **`Ball` 的构建序**（`GetRes0Cells().SelectMany(CellToChildren)`），
  而 Legacy 的 dense index 来自 **`.mpa` 顶点序** —— **两者不是同一个 identity**；
- CivSim 现有的 identity 用法是**持久化**的（`Polity.Cell` / `OriginCell` / `Habitation.Cell` 入档
  + `LAND` 三数组 + 不变量 `CellPolities[i].Cell == i`）⇒ **identity 必须是"世界身份"，不能是"某次构建的数组序"**。

> ★ **开放子项（待拍板，§八-O1）**：存档里 cell 引用**存 CellId（64-bit）还是存 dense index（+ 冻结构建序）**？
> 这是**存档格式**决定，`⑥` 只钉"世界身份 = CellId"这一原则，不擅自定存储形态。

---

## §二 ② Topology —— **H3 res4 六邻接**

**裁决**：基础邻接 = **`H3 res4` 的六邻接关系**（`Ball.CellNeighbors` = `GridDisk(k=1)` 去自身，
六边形 6 / 五边形 5）。**不重建 Legacy n=64 邻接**。

**为什么不能沿用 Legacy 邻接**（实测依据）：Legacy `GameGrid.BuildNeighbors` = **距离阈值定义**
（桶内球面距离 `< 1.5×平均格距`）；`Ball.CellNeighbors` = **拓扑环-1 定义**（`GridDisk(k=1)`）。
**两者是不同定义的"邻居"**，即使度数相近也不可互换。

**上层区域**由 cell 集合建立（不是新的空间载体）：

```
Province A = { res4 cell … }      ← 任意不规则集合
Country  C = { res4 cell … }      ← 是 Province/cell 的集合
```

**运行顺序**：BFS / 扩散 / 迁移 / 领地距离**首先在 res4 topology 上运行**，任何"区域级"运算都是
在 cell 结果之上的**聚合**（§五）。

---

## §三 ③ Metric —— **hop 只是拓扑距离，不再代表固定物理距离**

**裁决**：CivSim 现行 hop 常量（`InfluenceRadius` / `ChiefReach` / `ColonizeRadius` / `OriginDistMin`）
**必须改为以 km 表达**，覆盖范围按 **res4 的实际距离**判定 ⇒ **消除 ⑤ 报告实测到的 2.65× 静默缩放**。

**⑤ 实测的静默缩放（复述）**：`reachKm = (2·InfluenceRadius + 1) × √CellAreaKm2`

| 载体 | √A | `reachKm` |
|---|---|---|
| Legacy n=64 | 111.6 km | 1,451 km |
| H3 res3 | 111.3 km | 1,447 km |
| **H3 res4** | **42.1 km** | **547 km（÷2.65）** |

（即：`InfluenceRadius = 6` 这个**拓扑常量**在 res4 上物理半径缩到 0.38×，而代码里看不出任何变化。）

**形态（已裁决）**：参数写成 `*RadiusKm`（几何尺度 B，km），不再写 hop。

**数值（★ 建议 · 待拍板，§八-O2）**：若**保留 Legacy 的物理含义**（Legacy 标定锚 = √A = 111.6 km）：

| 参数（Legacy 拓扑值） | Legacy 物理含义 | res4 等效 hop 数 | 建议新形态 |
|---|---|---|---|
| `InfluenceRadius = 6` | ≈ 670 km | ≈ 16 | `InfluenceRadiusKm ≈ 670` |
| `ColonizeRadius = 6` | ≈ 670 km | ≈ 16 | `ColonizeRadiusKm ≈ 670` |
| `ChiefReach = 12` | ≈ 1,339 km | ≈ 32 | `ChiefReachKm ≈ 1,339` |
| `OriginDistMin = 12` | ≈ 1,339 km | ≈ 32 | `OriginDistMinKm ≈ 1,339` |

> 佐证：`OriginDistMin` 的注释已写 "≈1300 km"，与 Legacy 实算 1,339 km 吻合 ⇒ **注释里的意图本来就是物理距离**，
> 只是实现用 hop 表达；改 km 是**让实现追上注释**。

---

## §四 ④ Resolution —— **`HumanInputGrid = H3 res4`**

**裁决**：`HumanInputGrid` 的**基础粒度 = `WorldGenPlanet.ProductionRes` = H3 res4**。

```
WorldGen Production
      │
      ▼
   H3 res4                    ← 唯一基础空间粒度（冻结）
      │
      ▼
HumanInputGrid                ← 建立在 res4 上的逻辑输入容器
      │
      ▼
   CivSim                     ← 消费 res4 cell 与其聚合
      │
      ▼
Province / Polity / Civilization   ← res4 cell 的集合/引用
```

**永不出现**：`WorldGen = res4` 而 `CivSim = res3` 的**隐式尺度转换**。

**★ 可执行钉子（已存在，无需新建）**：`tests/World.Tests/SpatialScaleTests.cs`
→ `ProductionRes_IsRes4_Frozen()`：反射读 `WorldGenPlanet.ProductionRes` 的**原始常量**，
断言 `IsLiteral == true` 且值 `== 4`，注释明确"改动它必须用户解冻"。
⇒ **"为了性能把 CivSim 悄悄降到 res3/res2"** 这条路，**今天已有一道测试拦着世界档位**；
CivSim 侧的那一道（"基础粒度必须 = `ProductionRes`"）**随 `HumanInputGrid` 落地时补**（§八-O3）。

---

## §五 附加契约（★ 区域不是 Cell）

**裁决**（用户 2026-10-06 明示）：

> **任何 `Province` / `Country` / `Polity` / `Civilization` 都只能是 res4 CellId 的集合或对其引用，
> 不得成为 CivSim 的基础空间索引。**

含义：

- **基础空间索引只有一种** = res4 cell（`HumanInputGrid`）；
- 区域是**建立在 cell identity 之上的高层聚合**，其成员可以是任意不规则 cell 集合
  （10 格 → Province A；35 格 → Province B；200 格 → Country C，全部合法）；
- 区域**不得**反向定义"什么是基础单元"（不得引入第二套 spatial index）。

**与现有结构的一致性核对（实测 · 无冲突）**：

| 现有结构 | 实测形态 | 与本契约 |
|---|---|---|
| `GameGrid.Province` / `Country` | `int[]`（**逐格标签**，0 = 无主） | ✅ 已经是"cell 上的标签" ⇒ 即"区域 = cell 集合" |
| `CivSimContext.CellOwner[c]` | 逐格归属政体 id | ✅ 领地 = cell 集合 |
| `Polity.Cell` | **单格引用（驻格）** | ✅ 单格引用是 cell 集合的特例，不冲突 |
| 不变量 `CellPolities[i].Cell == i`（一格一实体） | `CivSimContext` 自检 | ✅ **仍成立**——见下 |

★ **一处必须说清的区分**（避免误解为"Polity 变成多格实体"）：
"Polity 由 cell 集合组成" 指的是 **Polity 的领地**（= `CellOwner` 的 cell 集合），
**不是** `Polity.Cell` 变成多格。**"一格一实体"（驻格唯一）与"领地多格"是两件事**，二者并存且互不矛盾。
⇒ res4 下，一个政体的**领地格数会自然变多**（粒度细 7×），但**驻格仍唯一**。

---

## §六 正式不变量（写入 `ADR-0005 §不变量`）

| # | 不变量 | 可执行性 |
|---|---|---|
| **I1** | **CivSim 基础空间粒度 = `WorldGenPlanet.ProductionRes` = H3 res4**（不得为性能降档；变更须重走架构决策） | 世界档位侧 ✅ 已有（`ProductionRes_IsRes4_Frozen`）；CivSim 侧 ⏳ 随 `HumanInputGrid` 补 |
| **I2** | 基础空间身份 = **H3 res4 CellId**；dense index 仅作**运行时索引**，不得作为世界身份 | ⏳ 随 `HumanInputGrid` 补 |
| **I3** | 基础邻接 = **H3 res4 六邻接**；不重建 Legacy n=64 邻接 | ⏳ 随 `HumanInputGrid` 补 |
| **I4** | **hop 不表示固定物理距离**；影响半径类参数以 **km**（几何尺度 B）表达 | ⏳ 随重标定补 |
| **I5** | **区域不得成为基础空间索引**；`Province`/`Country`/`Polity`/`Civilization` 只能引用 res4 cell 集合 | ⏳ 随 `HumanInputGrid` 补 |

---

## §七 与 ⑤ 的衔接（状态更新）

```
⑤ 裁决： Natural Input Bridge = BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED
                                    └──────────────┬──────────────┘
                                                   │  ⑥ 本轮：已 DECIDED（I1–I5）
                                                   ▼
⑥ 裁决后： Natural Input Bridge = BLOCKED BY MISSING WORLD FACTS
```

⇒ **载体契约不再阻断 Bridge；唯一剩余阻断项 = Phase 4 事实生产**
（`P4-1 Temperature` / `P4-2 Precipitation 绝对量纲` / `P4-3 Biome` / `P4-4 Soil` / `P4-5 Seasonal Climate`）。

---

## §八 开放子项（★ 不擅自定）

| # | 子项 | 为什么不在本轮定 |
|---|---|---|
| **O1** | 存档 cell 引用存 **CellId（64-bit）** 还是 **dense index + 冻结构建序**？ | 属**存档格式**决定（触 `.cmp`/`.mpa` 布局与 `Peek` 硬编码）⇒ 独立变更 |
| **O2** | hop→km 的**具体数值**：保留 Legacy 物理含义（§三建议表）还是在新世界上重标定？ | 属**标定**，须实测（且 `R[]` 重标定已在 ADR §3 前置四问里） |
| **O3** | `HumanInputGrid` 的**归属命名空间**与**契约钉子** | 它须依赖 `World.H3Grid`（`Ball`）+ WorldGen 事实；若 `World.CivSim` 直接引 `World.H3Grid`，**须显式改 §执行契约白名单**（ADR §B2 纪律）⇒ 独立决策 |
| **O4** | 是否**复用**生产 `Ball(res4)` 实例（而非新建第二套） | 属实现选择；原则是"不新建第二套 identity/topology 来源" |

---

## §八之二 · 已决约束：不保留 Legacy 读档兼容层（2026-10-07 用户拍板）

> 起因：讨论"迁移到 H3 res4 后 Legacy 是否可整体删除"时，用户明确 **不需要读旧档，直接删**。
> 实测后发现该指令的前提需修正 ⇒ 本节记录**修正后的可执行结论**（零代码改动）。

**裁决**：`HumanInputGrid` 落地时，**不实现 Legacy `.gmp` / `.mpa` 读档兼容层，不做旧档转换**。
⇒ 届时可整体清退存档层；但**清退必须与 Bridge 同批**，不得提前。

### ★ 实测修正：`GameGrid` 不是存档，是运行网格

| 项 | 实测 |
|---|---|
| CivSim 对 `GameGrid` 的 ~90 处成员访问 | **全部是运行 API**：`N`(25) / `CellAreaKm2`(16) / `Neighbors`(12) / `Biome`(7) / `DistKm`(6) / `Temp`(5) / `SoilLevel`(5) / `LakeLevel`(5) / `IsLandCell`(5) / `Precip`(4) / `MonthTemp`(3) / `MonthPrecip`(3) |
| 存档 API（`GameMapArchive.Write/Read/WriteBody/ReadBody`）消费者 | **仅 3 处**：`CivMapArchive.cs:483`（NATR 写）/ `:803`（NATR 读）/ 两个诊断 |
| ⇒ | **`GameGrid` ≠ 存档**。它满足的是 §1.2 的**空间容器三件套**，与持久化无关 |

### ⚠️ 反噬：删存档会切断 CivSim 唯一的世界来源

```
.mpa ──GameGrid.FromMapData──┐
.cmp NATR ──ReadBody─────────┴→ GameGrid ──注入──→ CivEngine.Run(grid, …)
```

`CivEngine.Run(GameGrid grid, …)` **不自己构造网格**（外部注入），而全项目唯一构造入口就是上述两条**存档**路径。
⇒ **现在删存档层 = CivSim 能编译但跑不起来**（`CivSimDiag` / `EvolveCmp` / `CiviDiag` 全部走 `FromMapData`）。

> 按永久原则 1 第三问（"现在有真实消费者吗"）：`GameGrid` 的答案是 **21 个生产文件**，不是 0。
> ⇒ **`HumanInputGrid` 落地前，`scripts/LogicGrid/` 一个文件都不能删。**

### 迁移时的净删除清单（届时执行）

| 类 | 处置 |
|---|---|
| `GameMapArchive.cs` / `ArchiveLayout.cs` / `GameGrid.FromMapData` / `.gmp` 路径 | **删**（无旧档兼容需求） |
| `CivMapArchive` 的 NATR 段读写 | **改**（换 `HumanInputGrid` 载体） |
| `GameGrid` **字段族**（Elev/Temp/Precip/Biome/River*/Lake/Mineral/Soil/Monsoon/Month*） | **不是删，是迁移** ⇒ 进 `HumanInputGrid` |
| `GameGrid.BuildNeighbors`（1.5×格距阈值） | **废** ⇒ `Ball.CellNeighbors`（GridDisk k=1） |
| `CellAreaKm2 = 4πR²/N` / 点积 `DistKm` | **废** ⇒ `SpatialScale`（等积 + haversine） |
| `SphereGrid.cs` | **删**（H3 无对应物；唯一消费者 `ArchiveDiag:117` 改用 `Ball`） |
| `10n²+2` 结构校验（`GameMapArchive:159`） | **废** ⇒ `H3.GetNumCells(res)` |
| `Icosahedron.cs` | ★2026-10-09 **已删**（用户拍板）：原拟保留因其 `Subdivide` 是 CivSim 测试的 42 顶点夹具，但 CivSim 已整体移出、旧 icosahedron 网格退役 ⇒ 删除；回归时夹具改走 H3 / `HumanInputGrid`——见 `docs/裁决-Legacy载体簇删除.md` §八 |

### 对开放子项的影响

- **O1**（存档存 CellId 还是 dense index）：**不再需要考虑旧档兼容**，只需定新格式。
- **O3**（`HumanInputGrid` 命名空间）：不因本节改变，仍须遵守 ADR §B2 白名单纪律。

---

## §九 后果 / 风险

| 方向 | 内容 |
|---|---|
| **正面** | ① 消除隐式尺度转换（WorldGen res4 ↔ CivSim res3）；② identity/topology 与全球 H3 架构一致；③ `Province`/`Country` = cell 集合 ⇒ 任意不规则区域天然合法；④ 水文的 D-11 处置范式被制度化为不变量 |
| **风险（须正面处理，不得靠降 res 规避）** | ① **成本 ×7**：CivSim 的 O(n) 逐 tick 循环（`TerritoryModel`/`SpreadModel`/`CultureModel`/`ReligionModel`）与 `BFS` 在 288,122 格上放大约 **7×**；② 存档 `LAND`/`NATR` 数组 **×7**；③ hop 常量重标定若做错，会**静默改变**社会半径（正是 I4 要挡的） |
| **纪律** | 上述风险的**唯一合法处置 = 优化 CivSim / 重标定参数**；**禁止**降世界档位（永久原则 3 + I1） |

---

## 附 · 关键文件与依据

- `scripts/Logic/H3Grid/Ball.cs`（H3 identity + dense↔id 映射 + 六邻接）
- `scripts/WorldGen/Composition/WorldGenPlanet.cs`（`ProductionRes = 4`；`Ball(res4)` 生产实例）
- `scripts/WorldGen/Simulation/SpatialScale.cs`（`CellAreaKm2` / `DistanceKm`；hop→km 纪律）
- `tests/World.Tests/SpatialScaleTests.cs` → `ProductionRes_IsRes4_Frozen()`（**已有可执行钉子**）
- `scripts/CivSim/Engine/CivSimContext.cs`（hop 常量 + `BfsRadius` + `CellAreaKm2` 消费）
- `scripts/CivSim/Archive/CivArchiveSchema.cs` + `scripts/CivSim/Archive/CivMapArchive.cs`（cell 引用入档 = identity 持久化证据）
- `docs/裁决-Bridge可行性裁决.md`（⑤：载体取证 + 2.65× 实测）
