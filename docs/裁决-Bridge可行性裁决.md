# 裁决 · ⑤ Bridge 可行性裁决（Natural Input Bridge）

> 2026-10-06 · **纯裁决**（零代码改动 · **不实现 Bridge** · **不补 CivSim 事实** · 不做 `LakeLevel` Adapter）
> 输入：④ `docs/审查报告-WorldHumanInputBridge-语义等价性取证.md`
> 输出（本轮范围）：**A** Bridge 可行性裁决 · **B** H3 / Legacy 载体取证 · **C** Phase 4 前置任务清单
> 依据：ADR-0005 §B1–B4、C-4 接线审查（D1–D4 迁移对照基准）。

---

## §〇 裁决（一句话）

# `Natural Input Bridge = BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED`

- **当前不实现 Natural Input Bridge。**
- 这**不是取消 Bridge**，而是把它从"马上编码"降级为一个**有前置条件的架构阶段**：先生产事实、先定载体契约。
- 两个**互相独立**的阻断项（任一未解则 Bridge 不可开工）：
  1. **对岸无事实** —— 驱动模拟的 7 项自然输入在新线 `SOURCE_MISSING`（§四）；
  2. **载体契约未定** —— "CivSim 跑在哪张网格的哪种 identity 上"未决（§三）。

**路线图（⑤ 之后）**：

```
④ Provenance                  ✅ 已完成（2026-10-06）
        │
        ▼
⑤ Bridge Feasibility           ← 本裁决：BLOCKED
        │
        ├── Elev ─────────────────┐            （语义等价，但载体未定 ⇒ 仍不可接）
        │                         │
        ├── LakeLevel → Adapter ──┤
        │                         ▼
        │                  Natural Input Bridge
        │                  （暂不实现 · 前置条件未满足）
        │
        └── Temp / Precip / Biome / Soil / Season
                    │
                    ▼
              WorldGen Phase 4
              （先生产事实）
```

---

## §一 判据与口径

### 1.1 三类路线（对 ④ 的 15 项输入）

| 类别 | 含义 | ⑤ 的处理 |
|---|---|---|
| **可直接桥接** | 语义等价（④ `EQUIVALENT`） | 纳入 Bridge 候选 |
| **可通过明确适配桥接** | 有对应事实，形态/粒度需一层显式适配（④ `ADAPTER_REQUIRED`） | 记录为 **Adapter**，**先不实现** |
| **事实缺失 / 语义不等价** | ④ `SOURCE_MISSING` / `SEMANTICALLY_DIFFERENT` | **阻断 Bridge**，转 **WorldGen Phase 4** |
| **Legacy-only** | ④ `LEGACY_ONLY`（零消费者） | **不进入 Bridge**；另行做消费者取证 |

### 1.2 载体三契约（§三 的判据）

一个空间载体要能当 CivSim 的"世界"，必须同时满足三件套：

| 契约 | 含义 | 不满足的后果 |
|---|---|---|
| **Identity** | 每格有一个**稳定、可持久化**的 id，且能当稠密数组下标 | 存档无法恢复"哪个实体在哪格" |
| **Topology** | 提供邻接表（**且"跳"的物理含义明确**） | 扩散/BFS/迁移/领地半径全部语义漂移 |
| **Metric** | 提供格面积与两点距离（km） | 产出/承载/接触半径公式量级错 |

> ★ **"字段语义等价" ≠ "载体等价"**：④ 判 `Elev` 为 `EQUIVALENT`（五项判据全同），
> **但这不足以推出 `FinalHeight[cell] → GameGrid.Elev[cell]`**——
> 因为箭头两端是**两张不同的网格**，identity / topology / metric 三项都不同（§三）。

---

## §二 A · Bridge 可行性裁决

### 2.1 逐项归属

| 路线 | 输入 | 依据（④） | ⑤ 处理 |
|---|---|---|---|
| **可直接桥接** | `Elev` | `EQUIVALENT`（`Composer.HeightM`） | 纳入 Bridge 候选 |
| **可适配桥接** | `LakeLevel` | `ADAPTER_REQUIRED`（`LakeState` 逐湖对象 → 逐格掩码） | 记录为 Adapter，**先不实现** |
| **阻断 → Phase 4** | `Temp` `Biome` `SoilLevel` `MonthTemp` `MonthPrecip` | `SOURCE_MISSING` | 转 Phase 4 |
| **阻断 → Phase 4** | `Precip` | `SEMANTICALLY_DIFFERENT`（`AnnualMm` 相对量级 ≠ Legacy 实际 mm） | 转 Phase 4（含**绝对量纲**问题） |
| **阻断 → Phase 4（级联）** | `WildCrops` `WildLivestock` | `SOURCE_MISSING`（派生但输入缺失） | 输入补齐后自动恢复 |
| **不进入 Bridge** | `RiverLevel` `RiverFlow` `RiverVolume` `MineralLevel` `MonsoonLevel`（+`Current*`/`Psi`） | `LEGACY_ONLY`（零消费者） | 另做消费者取证 |

### 2.2 裁定

> **① 当前不实现 Natural Input Bridge。**
> **② 只有 `Elev` 与 `LakeLevel` 两项"有资格"进入 Bridge 设计；但"有资格" ≠ "现在可接"**——
> 二者**仍然被载体契约阻断**（§三）：语义等价不能单独完成 Bridge。
> **③ 其余 13 项在 Phase 4 交付前，Bridge 无论如何都不可能完成**（对岸没有事实）。
> **④ 不实现 `LakeLevel` Adapter**：虽然已有合理候选（`LakeOfBasin[DepressionIdOf[i]] >= 0`），
> 但**载体契约未定**，提前实现极易做成一次性的投影代码，随后被载体裁决推翻。

---

## §三 B · H3 / Legacy 载体取证

> ★ 本节是**语义裁决**，不是写映射代码。目标是确认：Bridge 的空间 identity 是什么、映射方向怎么定。

### 3.1 CivSim 到底需要什么样的空间？（实测其 API 面）

**结论：CivSim 需要的既不是"逐格字段集"，也不是纯拓扑，而是一个空间容器 —— {Identity + Topology + Metric} 三件套齐备。**

| 契约 | CivSim 实际用法（实测消费点） |
|---|---|
| **Identity** | `cell` = **稠密 int `0..N−1`**，直接当数组下标：`R[cell]` / `CellPop[cell]` / `CellPolities[cell]`；`Polity.Cell` / `OriginCell` / `Habitation.Cell` 均为 `int`；不变量 `CellPolities[i].Cell == i`（`CivSimContext` 自检），`0 ≤ Cell < N` 到处校验 |
| **Topology** | `Grid.Neighbors[c]`（变长 ~6）：陆地连通分量 BFS（`OriginModel.ComputeContinents`）、文化/宗教/领地扩散（`SpreadModel`/`CultureModel`/`ReligionModel`/`TerritoryModel`）、迁移候选（`SplitMigrateModel`）、战争路径（`WarModel`）、`ctx.BfsRadius`（`CivSimContext:1027-1043`）、`WaterRich`/`LakeFishAccess` 的邻格判水 |
| **Metric** | `Grid.CellAreaKm2`（= `4πR²/N`）进入**生产公式**：`yPot = R×A×m`、`A = CellAreaKm2`、`reachKm`；`Grid.DistKm(a,b)`（大圆，`Verts` 点积 × R）：起源间距、贸易/战争接触半径 |

**★ 关键：hop 常量（拓扑量）被当物理半径用**（永久原则 2 的现场）：

```
InfluenceRadius = 6      ChiefReach = 12      ColonizeRadius = 6      OriginDistMin = 12   （单位：格步）
        └─ reachKm  = (2×InfluenceRadius + 1) × √CellAreaKm2
        └─ minDistKm = OriginDistMin × √CellAreaKm2
```

### 3.2 Legacy 载体契约（`GameGrid` · `World.LogicGrid`）

| 契约 | 实测 |
|---|---|
| **Identity** | **稠密下标 `0..N−1`**，来自 `.mpa` 顶点序（`Verts` 数组序）；`N = 10n²+2`（n=64 ⇒ **40,962**） |
| **拓扑** | `Neighbors` = 桶内**球面距离 < 1.5×平均格距**（`BuildNeighbors`）⇒ **距离阈值定义**，变长 |
| **度量** | `CellAreaKm2 = 4πR²/N`（均匀近似）；`DistKm` 大圆（`Verts` 点积 × `RadiusKm`） |
| **持久化** | **cell index 入档**：`LAND` = `Cultivation[n]` + `CellOwner[n]` + `LockedUntil[n]`；`TRIB` 每实体含 `Cell` / `OriginCell`（`CivArchiveSchema`）；`STTL` 每聚落含 `Cell`（`CivMapArchive`） |

### 3.3 新线载体契约（`Ball` / H3 · `World.Spatial`）

| 契约 | 实测 |
|---|---|
| **Identity** | **H3 cell id：64-bit 稀疏**（含 res 位）；但 `Ball` **已经提供稠密下标**——`CellIds[i]` + `CellIndexOf(id)` ⟺ `_cellIdToIndex` 字典 ⇒ **dense ↔ id 双向映射现成** |
| **拓扑** | `CellNeighbors` = `H3.GridDisk(k=1)` 去自身（**拓扑环-1**）⇒ **恰好 6（五边形 5）**，与 res 无关 |
| **度量** | `CellDirs`/`CellCenters`（几何）；格面积/距离走 `SpatialScale`（`CellAreaKm2` 等积口径、`DistanceKm` haversine）—— **不在 `Ball` 内**，是独立层 |
| **持久化** | **新线尚未定义"哪个事实按哪个 identity 入档"**（`WorldGen` 目前不产出存档，桥未接线） |

### 3.4 载体裁决 —— 回答五个关键问题

| # | 问题 | 裁决 |
|---|---|---|
| **Q1** | CivSim 需要逐格逻辑输入，还是某种拓扑结构？ | **两者都要，且是一个容器**：{稠密 identity + 邻接拓扑 + 度量}。缺任一项都不能当 CivSim 的世界 |
| **Q2** | Legacy `GameGrid` 的 cell identity 是否被当成**稳定 ID**？ | **是。** `Polity.Cell` / `OriginCell` / `Habitation.Cell` 为 `int32` **入档**，并有不变量 `CellPolities[i].Cell == i`。identity = **稠密下标，且是持久化契约** |
| **Q3** | 新 H3 cell 能否成为新的 CivSim 空间 identity？ | **能作"底层几何身份"，但不能直接作"索引身份"。** H3 id 稀疏（64-bit）；但 `Ball` 已提供 dense↔id 映射 ⇒ **技术上可行**。代价 = **identity 的来源变了**（Legacy：`.mpa` 顶点序；H3：`Ball` 的 `CellIds` 构建序）⇒ 所有已入档的 cell 引用需**重映射**，且 identity 稳定性改由 `Ball` 构建确定性担保 |
| **Q4** | 若不能直接，是否需独立 `HumanInputGrid` / projection layer？ | **需要。** 因为载体**三件套的定义都不同**（identity 来源 / 邻接定义 / hop 物理含义）。 ⇒ Bridge 的正确形态是 **`WorldGen facts → HumanInputGrid`**（CivSim 侧的空间容器），**不是** `FinalHeight[cell] → GameGrid.Elev[cell]` |
| **Q5** | H3 res4 → CivSim 所需粒度是否有降采样/聚合问题？ | **存在，且方向未决**（见 §3.5） |

### 3.5 粒度对照（实测计算）

| 载体 | 格数 N | 格面积 km² | √A (km) | `reachKm`=13√A | `minDistKm`=12√A |
|---|---|---|---|---|---|
| **Legacy n=64** | **40,962** | 12,452 | 111.6 | **1,451 km** | **1,339 km** |
| **H3 res3** | 41,162 | 12,392 | 111.3 | 1,447 km | 1,336 km |
| **H3 res4**（世界生产档） | 288,122 | 1,770 | 42.1 | **547 km** | **505 km** |

**三条实测结论**：

1. **H3 res3 ≈ Legacy n=64**（格数 41,162 vs 40,962，差 0.5%；格面积 12,392 vs 12,452，差 0.5%）
   —— res3 是**天然粒度匹配档**，其 `reachKm`/`minDistKm` 与 Legacy **几乎相同**。
2. **世界生产档 = res4 是 Legacy 的 7.0× 细化**（288,122 / 40,962）⇒ 若 CivSim 直接跑 res4：
   - 存档 `LAND`/`NATR` 体量 **×7**；
   - `reachKm` **1,451 → 547 km（÷2.65）**、`minDistKm` **1,339 → 505 km（÷2.65）**
     ⇒ **所有 hop 常量（`InfluenceRadius`/`ChiefReach`/`ColonizeRadius`/`OriginDistMin`）的物理含义静默改变**（永久原则 2）。
3. ⇒ **降采样/聚合问题真实存在，且是双向未决**：
   - **(a) CivSim 跑 res3**：粒度天然匹配，但需 **res4 → res3 聚合/降采样**，且"聚合哪些事实、聚合规则是什么"**未定义**；
   - **(b) CivSim 跑 res4**：不用聚合，但成本 ×7 + **全部 hop 常量须重标定**。

### 3.6 载体结论

> **Bridge 的空间 identity 与映射方向 = 未定。**
> 具体地：**CivSim 是否迁到 H3、迁到哪一档 res、identity 用 dense index 还是 H3 id、
> hop 常量是否重标定 —— 四项全部未决，且它们互相耦合**（选 res4 就必须同时解决 hop 重标定与聚合）。

**⇒ 这直接证明 ④ 的结论需要加强**：`Elev` 判 `EQUIVALENT` **不足够**；
必须落成 **`WorldGen facts → HumanInputGrid`** 这一层，而不是字段级直连。

---

## §四 C · Phase 4 前置任务清单（WorldGen 侧事实生产缺口）

> 这些是 **WorldGen 的事实生产任务**，**不是** CivSim 的补丁（ADR-0005 §B4）。
> 每项须自带**验收口径**（ADR §3 前置四问的第四问）。

| # | 任务 | 须产出的事实 | 解锁的 Legacy 输入 | 验收口径（须可测） |
|---|---|---|---|---|
| **P4-1** | **Temperature** | 年均温场（°C） | `Temp`（R 的根） | ① 与 Legacy `TEMP` 量级可比（含 `≤−5.5 °C` 冰盖阈值语义）；② `R[]` 中位标定锚可复现 |
| **P4-2** | **Precipitation（绝对量纲）** | 年降水场（**真实 mm 量级**） | `Precip` | ① 量纲与 Legacy `PREC`（实测 max 10662 mm）可比，**不再是 `MaxMm=2400` 上限**；② `MiamiNpp` 重标定后 `R[]` 不漂移 |
| **P4-3** | **Biome** | 群系分类（与 `BiomeType` 对齐） | `Biome` | ① 覆盖 CivSim 用到的四类语义（水/寒/通行/猎物）；② 分类定义与 Legacy 逐类对照表 |
| **P4-4** | **Soil** | 土壤等级（1–5） | `SoilLevel` | ① 与 `AlluvFactor`（4→×2、5→×3）及 `InventionModel >=3` 门槛对齐；② 空间分布合理性判读 |
| **P4-5** | **Seasonal Climate** | 月温场 + 月降水场（季节循环） | `MonthTemp` / `MonthPrecip` | ① 12 月份量守恒到年量；② `WarWeather` 分类与 `WildCrops.Suitability` 的季节项可复现 |

**级联恢复（无需额外任务）**：P4-1..P4-5 交付后，`WildCrops` / `WildLivestock` **自动恢复**
（其派生函数 `WildCropsSystem.Compute` / `ComputeLivestock` 本身不需要改）。

> ⚠️ **P4-2 是本清单里最容易被低估的一项**：`PrecipitationModel` 现有输出是**水文驱动的相对量级**
> （注释自陈），把它当"降水事实"直接接给 `MiamiNpp` 会**隐性改变整个 `R[]` 量级**。
> 必须先解决**绝对量纲**，再谈接线。

---

## §五 状态与下一步

### 5.1 正式状态

```
Natural Input Bridge = BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED
```

### 5.2 本轮明确**不做**（克制边界）

- ❌ 不实现 `LakeLevel` Adapter（载体契约未定 ⇒ 会做成一次性投影代码）；
- ❌ 不写 Bridge 的任何代码 / 接口 / 映射；
- ❌ 不在 CivSim 侧补任何环境/生态事实（§B4）；
- ❌ 不改任何生产公式；
- ❌ **不提前清退 `scripts/LogicGrid/`** —— `GameGrid` 有 **21 个生产消费者**（~90 处运行 API 访问，
  如 `N`/`CellAreaKm2`/`Neighbors`/`DistKm`/`Biome`），**它不是存档**；清退必须与 Bridge 同批；
- ❌ **不实现 Legacy `.gmp` / `.mpa` 读档兼容层**（2026-10-07 用户拍板：不需要读旧档，不做旧档转换）
  —— 详见 **⑥ `裁决-HumanInputGrid载体契约.md` §八之二**。

### 5.3 解除阻断的条件（两个都必须满足）

| 阻断项 | 解除条件 |
|---|---|
| **MISSING WORLD FACTS** | Phase 4 的 **P4-1..P4-5** 交付（至少 `Temp` + `Precip` 绝对量纲，才能让 `R` 在新世界立足） |
| **CARRIER CONTRACT UNDECIDED** | 独立裁决 **载体契约**：① CivSim 是否迁 H3；② 迁到哪档 res（res3 天然匹配 vs res4 生产档）；③ identity = dense index 还是 H3 id；④ hop 常量是否重标定 —— **四项须一起定** |

### 5.4 下一步候选（未拍板，供选择）

1. **先做载体裁决（⑥）**：把 §3.4/§3.5 的四项未决**单独裁决**（纯设计，不写代码）——它是 `Elev` 也能接的前提。
2. **先做 Phase 4 的第一项（Temperature）**：没有 `Temp` 就没有 `R`，Bridge 无可谈。
3. **先补 ③ `SubsistenceReadout`**（观测能力，独立 commit）——不依赖以上任一项。

---

## 附 · 取证命令与关键文件

```bash
# 1) CivSim 空间 API 面（identity / topology / metric）
grep -rn "\.Neighbors\|CellAreaKm2\|\.DistKm\|BfsRadius" --include="*.cs" scripts/CivSim
# 2) cell identity 是否入档（LAND/TRIB/STTL）
grep -rn "\"Cell\"\|CellOwner\|Cultivation\[" --include="*.cs" scripts/CivSim scripts/LogicGrid
# 3) hop 常量（拓扑量被当物理半径）
grep -rn "InfluenceRadius\|ChiefReach\|ColonizeRadius\|OriginDistMin" --include="*.cs" scripts/CivSim
# 4) 新线载体
#    scripts/Spatial/Ball/Ball.cs（CellIds/CellNeighbors/CellIndexOf）
#    scripts/WorldGen/Simulation/SpatialScale.cs（CellAreaKm2/DistanceKm）
```

**关键文件**：
- `scripts/Spatial/Ball/Ball.cs`（新线载体：H3 id + dense 下标 + 邻接）
- `scripts/LogicGrid/GameGrid.cs`（Legacy 载体：dense 下标 + 距离阈值邻接 + 度量）
- `scripts/LogicGrid/GameMapArchive.cs`（Legacy 载体持久化契约）
- `scripts/CivSim/Archive/CivMapArchive.cs` + `CivArchiveSchema.cs`（**cell index 入档**的直接证据）
- `scripts/CivSim/Engine/CivSimContext.cs`（hop 常量 + `BfsRadius` + `CellAreaKm2` 消费）
- `scripts/CivSim/Mechanics/Society/OriginModel.cs`（陆地连通分量 BFS + 起源间距）
