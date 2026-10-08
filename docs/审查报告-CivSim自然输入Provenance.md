# 审查报告 · CivSim 自然输入 Provenance 对账

> 2026-10-06 · 纯取证（**零代码改动**）· 触发：② Riparian 取证推翻"WorldGen 生物群系事实链"假设后的延伸。
> 目的：回答一个问题 —— **Human Simulation v0.1 到底跑在"新 WorldGen 世界"上，还是跑在"遗留 .mpa 快照"上？**

---

## §〇 结论（一句话）

**CivSim 全部气候/群系类输入（`Temp` / `Precip` / `Biome` / `MonthTemp` / `MonthPrecip` / `SoilLevel`）
都来自遗留 `.mpa`（旧世界线冻结输出），新 WorldGen 线没有任何路径能重建它们；
而新线已经具备的等价物（`HeightM` / `Lakes` / `RiverNetwork`）**没有接线到 `GameGrid`**。
⇒ 当前 Human Simulation 是一条 **Legacy-Data-Driven 模拟**，不是 New-WorldGen-Driven 模拟。**

这条债务正式命名为 **Legacy Climate / Biome Provenance Debt**。

---

## §一 取证口径与方法

| 项 | 说明 |
|---|---|
| 消费端范围 | `scripts/CivSim/**`（模拟）+ `scripts/Domain/**`（被 CivSim 消费的领域系统，主要是 `WildCropsSystem`） |
| 排除 | `Diagnostics/**`（读数）、`Archive/**` + `LogicGrid/GameMapArchive`（纯序列化）、`LogicGrid/ArchiveLayout`（schema） |
| 生产端范围 | `scripts/WorldGen/**`（新线唯一世界事实入口 = `WorldGenPlanet.Regenerate()`） |
| 硬取证 | 解析真实档 `userdata/maps/map_seed42_n64_r128.mpa`（段表格式，工具 `scratch/inspect_mpa.py`） |
| 判据 | "可重建" = 存在**新线生产者**且**能写出与现有档语义一致的场**；否则 = 不可重建 |

---

## §二 对账总表

**Source 口径**：`New` = 新 WorldGen 有生产者；`Legacy` = 仅遗留 `.mpa`；`Derived` = 从其他场现场派生。

| 自然输入 | CivSim 消费点（实测） | 新线生产者 | 有效 Source | 可重建 | 入档段 |
|---|---|---|---|---|---|
| `Elev` | `IsLandCell`/`IsCoast` → **整个模拟的海陆边界**；`WildCropsSystem.PotatoSuit`（高原加成） | ✅ `Composer.HeightM` | Legacy（`ELEV`） | ⚠️ 新线可算，但**未接线**且世界不同 | `ELEV` |
| **`Temp`** | `CivEngine.BuildLayer1`（Miami NPP + ≤−5.5 °C 冰盖归零 = **R 的根**）、`ClimateSim`、`WarWeather`、`WildCropsSystem` | ❌ **无温度模型** | **Legacy** | ❌ **不可重建** | `TEMP` |
| **`Precip`** | `BuildLayer1`、`ClimateSim`、`WarWeather`、`WildCropsSystem` | ⚠️ `PrecipitationModel.AnnualMm`（仅驱动水文，**未接线**） | **Legacy** | ❌ 新线那份**语义不等价**（Legacy max 10662 mm vs 新线 `MaxMm=2400`） | `PREC` |
| **`Biome`** | `WaterRich` / `TerrainCost` / `EnvMatches` / `IsColdZone` / `ForageSharesAt`、`WildCropsSystem`（RiceSuit / ComputeLivestock） | ❌ **无分类器** | **Legacy** | ❌ **不可重建** | `BIOM` |
| **`MonthTemp`** | `WildCropsSystem.Suitability`（5 种子生态位）、`WarWeather` | ❌ | **Legacy** | ❌ | `MTMP` |
| **`MonthPrecip`** | 同上 | ❌ | **Legacy** | ❌ | `MPRC` |
| **`SoilLevel`** | `AlluvFactor`（冲积肥力）、`InventionModel`（`>=3` 门槛） | ❌ **无土壤模型** | **Legacy** | ❌ | `SOIL` |
| `LakeLevel` | `WaterRich` / `EnvMatches "irrigation"` / `LakeFishAccess` / `WildCropsSystem.RiceSuit` | ✅ `LakeState.Lakes` | Legacy（`LAKE`） | ✅ 新线**有**等价物，**未接线** | `LAKE` |
| `RiverLevel` / `RiverFlow` / `RiverVolume` | **零消费者** | ✅ `RiverNetwork` / `RiverGeometry` | Legacy | — | `RIVL/RIVF/RIVV` |
| `MineralLevel` | **零消费者**（无 MiningModel） | ❌ | Legacy | — | `MINE` |
| `MonsoonLevel` | **零消费者** | ❌ | Legacy | — | `MONO` |
| `CurrentDirs` / `CurrentWarmth` / `CurrentStrength` / `Psi` | **零消费者** | ❌（洋流已随 A 线清退） | Legacy | — | `OCEN` |
| `WildCrops`（5 位 bitmask） | `InventionModel`（作物驯化门槛） | **Derived**：`WildCropsSystem.Compute` ← Temp/Precip/Month*/Biome/LakeLevel/Elev | Derived | ✅ 不入档，读档重建 | 不存档 |
| `WildLivestock`（1 位） | `CapabilityTable`（畜牧能力）、`FFarmTerritory` | **Derived**：`ComputeLivestock` ← Biome/Precip | Derived | ✅ 不入档 | 不存档 |
| `RadiusKm` / `CellAreaKm2` / `DistKm` / `Neighbors` / `Seed` | 全体几何/确定性基座 | ⚠️ 新线有 `SpatialScale.CellEdgeKm` + `Ball`，**未接线** | Legacy（`HEAD` 头） | ⚠️ 部分 | `HEAD` |

**派生链（R 的根，决定全模拟量级）**：

```
Legacy .mpa ─┬─ ELEV  → IsLandCell ─────────────────┐
             ├─ TEMP  ─┐                             │
             ├─ PREC  ─┴→ MiamiNpp × WaterRich(×1.5) ─┴→ R  (BuildLayer1)
             ├─ BIOM  ─┬→ WaterRich(Biome==Riparian ‖ LakeLevel>0)
             ├─ LAKE  ─┘
             ├─ SOIL  ──→ AlluvFactor（农业产出）
             └─ MTMP/MPRC ─→ WildCrops.Suitability ─→ WildCrops（驯化门槛）
```

---

## §三 输入三分

### 3.1 Legacy-only，**不可重建**（＝债务本体）

`Temp` · `Biome` · `MonthTemp` · `MonthPrecip` · `SoilLevel`
⇒ 新线**完全无生产者**。这些场一旦 `.mpa` 丢失，**Human Simulation 无法重建同态世界**。

### 3.2 新线已有等价物，但**未接线**

`Elev`（← `Composer.HeightM`）· `Precip`（← `PrecipitationModel.AnnualMm`，语义不等价）· `LakeLevel`（← `LakeState`）·
`River*`（← `RiverNetwork`，且**当前零消费者**）
⇒ 技术上可接，但**接上去会改变世界**（新线是另一套生成器、另一套尺度、另一套降水模型）。

### 3.3 纯派生（不入档）

`WildCrops` · `WildLivestock` ⇒ 读档现场重建；但**其输入本身是 Legacy** ⇒ 派生也继承债务。

---

## §四 专项结论

### 4.1 Legacy Climate / Biome Provenance Debt（正式记档）

**结构**：

```
Legacy .mpa ─┬─ TEMP
             ├─ PREC        ┐
             └─ BIOM        │
                            ▼
                          CivSim ─┬─ Temp ──┐
                                  ├─ Precip ├─→ R / Suitability / Weather
                                  └─ Biome ─┘
                                        │
                                        ▼
                              Resource Potential → Production → Food → Population
```

**新 WorldGen 现状**：

```
New WorldGen ─┬─ Geography    ✅
              ├─ Hydrology    ✅
              ├─ Precipitation ✅（仅驱动水文，未进 CivSim）
              └─ Climate/Biome ❌
```

**真正的问题不是"什么时候补 Riparian"，而是**：
> **什么时候允许 Human Simulation 摆脱遗留 `.mpa` 的气候/群系输入？**
> 这是独立架构课题，应单独立项（不在本轮主线内）。

### 4.2 零消费者的 Legacy 字段（存档冗余）

`MonsoonLevel` · `MineralLevel` · `RiverLevel/RiverFlow/RiverVolume` · `CurrentDirs/Warmth/Strength/Psi`
⇒ 在 `scripts/CivSim` + `scripts/Domain` 中**零读取点**（仅被存档/诊断/比对引用）。
⇒ 这些字段目前是 `.mpa`/`.gmp` 的**载荷冗余**；是否清退属独立议题（注意：它们仍是**地图渲染**的可能消费者，须另行取证后再判）。

### 4.3 ★ 河流对 CivSim 完全没有影响（重要副作用发现）

`WaterRich` 的水因子只认 **`Biome==Riparian` 或 `LakeLevel>0`**，**不读 `RiverLevel` / `RiverFlow` / `RiverVolume`**。
而 `Riparian` 在一切现存档中恒为 0（见 §4.4）。
⇒ **"河岸/河流灌溉"这条通道当前实际不存在**：CivSim 里唯一的水因子来源是**湖泊**。
⇒ 这也解释了为什么 FishPotential 的水产只有 coast + lake 两档有效。

### 4.4 ② Riparian 取消记录（2026-10-06 用户拍板）

- **取消，不做**。不重建 `Riparian`，不重建 `Temperature → BiomeClassifier → Riparian`，不为 `isRiparian` 反向复活旧气候线。
- 理由：新 WorldGen 线**没有任何 biome 生产者**（旧 `BiomeClassifier` 已随 A 线清退，commit `6e7fc94`；
  `scripts/` 下已无 `Biome/` 目录）。Riparian 不是"缺失的一条分支"，而是**已被清退的整条气候/群系生产链留下的幽灵接口**。
- **`isRiparian` 参数保留**：`FishFrac(biome, isCoast, isRiparian, hasLake)` 表达的是"FishPotential **未来**可使用河岸生态位"；
  当前 `isRiparian == false` 是**真实世界事实缺失导致的合法退化状态，不是代码 bug**。
- **实测佐证**（`map_seed42_n64_r128.mpa`，n=40962）：BIOM 22 个值 = `0,1,2,3` + `14…31`（完整柯本细类），
  **不含 `12(Alpine)` / `13(Riparian)`** —— 即便在 64.7% 洋面 + 极湿沿海（PREC max 10662 mm）条件下，旧分类器也不产 Riparian。

---

## §五 边界声明（★ 供后续架构决策引用）

**"未来架构方向" ≠ "当前已实现架构"。** 本次取证的核心价值是把两者分开：

| | 当前**已实现** | 未来**方向**（未落地） |
|---|---|---|
| 自然世界 | New WorldGen：Geography / Hydrology / Precipitation | Climate / Biome |
| HumanSim 输入 | **Legacy .mpa 快照** | 应迁到 New WorldGen |

⇒ Human Simulation v0.1 的**准确表述**是：

> Natural World ─(**现有可用自然事实**：Legacy `.mpa` + 新线水文事实)─→ Resource Potential → Production → Food → Population
> **其中 `Temperature` / `Biome` 属 Legacy external input，不属于本轮新建的 WorldGen 能力。**

这样才不会误判"整个自然世界 → 人类资源"已经闭环。

---

## §六 待决（不擅自行动）

1. **Legacy Climate/Biome Provenance Debt** 是否单独立项（"HumanSim 气候输入迁移"）？——未拍板。
2. §4.2 的零消费者 Legacy 字段是否清退？——**须先做地图/渲染侧渲染取证**再判，禁止仅凭"CivSim 零消费者"下结论。
3. §4.3 河流无影响：是否要把 `RiverNetwork` 的真实河流事实接给 `WaterRich`？——**属未来能力，未拍板**。

---

## 附 · 取证命令与证据

```bash
# 1) 旧气候线清退（分类器删除）
git log --oneline --all --diff-filter=D --name-only | grep -iE "biome"

# 2) 新线产出清单（无 Temperature / Biome）
#    scripts/WorldGen/Composition/WorldGenPlanet.cs :: Regenerate()

# 3) 存档 biome 实测（段表解析）
python scratch/inspect_mpa.py userdata/maps/map_seed42_n64_r128.mpa

# 4) CivSim 侧自然字段消费枚举
grep -rn "\.Temp\b\|\.Biome\b\|\.SoilLevel\b\|\.LakeLevel\b" --include="*.cs" scripts/CivSim scripts/Domain

# 5) 零消费者字段
grep -rn "\.MineralLevel\b\|\.MonsoonLevel\b\|\.RiverLevel\b\|\.Psi\b" --include="*.cs" scripts/CivSim scripts/Domain
```

**关键文件**：
- `scripts/WorldGen/Composition/WorldGenPlanet.cs`（新线唯一入口）
- `scripts/WorldGen/Simulation/PrecipitationModel.cs`（只有 `AnnualMm`）
- `scripts/LogicGrid/GameGrid.cs`（`FromMapData` = 唯一的"存档 → 模拟"桥）
- `scripts/CivSim/Engine/CivEngine.cs::BuildLayer1`（R 的根）
- `scripts/Domain/WildCropsSystem.cs`（Temp/Precip/Month*/Biome/LakeLevel/Elev 的最大消费者）
- `scratch/inspect_mpa.py`（本次新增的只读取证工具）
