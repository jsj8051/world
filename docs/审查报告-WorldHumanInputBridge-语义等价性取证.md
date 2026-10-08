# 审查报告 · World → Human Input Bridge 语义等价性取证（④）

> 2026-10-06 · 纯取证（**零代码改动 · 零桥接线**）· 路线 ④ 第一步
> 触发：C-6 冻结"谁不能偷偷接桥"后，下一步**不写 Bridge**，先定义 Bridge 的语义契约。
> 依据：`docs/审查报告-CivSim自然输入Provenance.md`（前序基础取证）+ 本轮逐项语义等价性核验。
> 目的：回答一个问题 —— **CivSim 现在消费的每一个自然输入，对应新世界线中的哪个事实？是否真的语义等价？**

---

## §〇 结论（一句话）

**逐项核验后，"可桥接的自然输入"当前只有 2 项（`Elev` = 等价；`LakeLevel` = 需适配），
其余全部卡在 `SOURCE_MISSING` 或 `SEMANTICALLY_DIFFERENT`。**
⇒ **Bridge 现在建不起来，不是因为"接口没定"，而是因为"对岸没有可桥接的事实"** ——
驱动全模拟量的 `Temp / Biome / SoilLevel / MonthTemp / MonthPrecip` 在新线**无生产者**，
而唯一的降水字段（`PrecipitationModel.AnnualMm`）与 Legacy `PREC` **不是同一个量**。

这正好是 ADR-0005 §B4 在发挥作用：**发现缺失事实时，不在 CivSim 补，而是转为 WorldGen 生产任务。**

> ★**后续更新（2026-10-06，路线 ⑦ · P4-1）**：`Temp` **已生产出事实**并升级为 `ADAPTER_REQUIRED`
> ⇒ "可桥接自然输入"由 **2 项变为 3 项**（`Elev` / `LakeLevel` / `Temp`），
> 但仍**不等于可以建 Bridge**（`Biome` / `Soil` / 季节气候仍缺失）。
> 详见 `docs/审查报告-P4-1-温度事实与provenance对照.md`；本报告其余部分为 2026-10-06 初版原始取证，保持不动。
>
> ★**后续更新（2026-10-06，路线 ⑧ · P4-2）**：`Precip` **已生产出事实**（绝对量纲 mm/年）并升级为 `ADAPTER_REQUIRED`
> ⇒ "可桥接自然输入"由 **3 项变为 4 项**（`Elev` / `LakeLevel` / `Temp` / `Precip`）。
> ★该升级**不是**"Phase 4 落地后自动通过"，而是**实测修正了一个预设**：
> `MiamiNpp` 对降水**整体量级几乎不敏感**（`R_raw` P50 1260 → 1262），敏感的是**形状/限制侧**（26.1% → 81.3%）。
> 详见 `docs/审查报告-P4-2-降水事实与provenance对照.md`。

---

## §一 取证口径与判据

### 1.1 范围

| 项 | 说明 |
|---|---|
| 消费端 | `scripts/CivSim/**`（模拟）+ `scripts/Domain/WildCropsSystem`（被 CivSim 消费的生态派生） |
| 生产端 | `scripts/WorldGen/**`；新线唯一世界事实入口 = `WorldGenPlanet.Regenerate()` |
| 输入载体 | `scripts/LogicGrid/GameGrid.cs`（Legacy `.mpa` 投影；`FromMapData` = 唯一"存档 → 模拟"桥） |
| 排除 | `Diagnostics/**`（读数）、`Archive/**` + `ArchiveLayout`（纯序列化） |

### 1.2 五项等价性判据（★ 五项全同才可判 `EQUIVALENT`）

对每一个输入，逐项比对：

1. **分类定义相同** —— 表达的是同一个物理/地理概念？
2. **空间粒度相同** —— 逐格 / 逐对象 / 逐流域？承载网格是否同一套？
3. **计算时点相同** —— 生成期一次性？还是运行时演化量？
4. **缺失·边界语义相同** —— 0 / −1 / null 代表什么？海陆边界口径是否一致？
5. **数值范围·单位相同** —— 同一量纲、可比的取值区间？

### 1.3 五标签判定体系（互斥，按"能否进入 Bridge 设计"排序）

| 标签 | 含义 | 有资格进 Bridge 设计？ |
|---|---|---|
| `EQUIVALENT` | 新线已有**语义等价**事实 | ✅ 是 |
| `ADAPTER_REQUIRED` | 新线有对应事实，但**形态/粒度/范围**需一层明确设计的适配 | ✅ 是（须先设计适配层） |
| `SEMANTICALLY_DIFFERENT` | 新线有同名/近似字段，但**语义不同** | ❌ 否（不可直接替换） |
| `SOURCE_MISSING` | 新线**目前无生产者**；属 WorldGen 待补事实 | ❌ 否（转生产任务） |
| `LEGACY_ONLY` | 仅遗留线存在、新线无生产者且无计划（含零消费者字段） | ❌ 否（不在桥接范围） |

> ★ **"字段名字类似 ⇒ 可以替换"是不允许的。** 任何箭头（`输入 ← 事实`）都必须经过上表判定后才允许成立。

---

## §二 逐项对账总表

**15 个字段 = 8 自然量 + 5 其他自然字段 + 2 派生场。**
（`MonsoonLevel` 与 `River*`/`MineralLevel` 为"CivSim 零消费者"字段，见 §三-⑨~⑬。）

| # | Legacy `GameGrid` 输入 | CivSim 当前用途（实测） | New World 来源候选 | 五项判据 | **结论** |
|---|---|---|---|---|---|
| ① | `Elev`（米；>0=陆） | `IsLandCell`/`IsCoast` → **整个模拟的海陆边界**；`WildCropsSystem.PotatoSuit`（高原加成 `Elev/2500`） | ✅ `Composer.HeightM`（`FinalLand` = `HeightM>0`） | 五项全同 | **EQUIVALENT** |
| ② | `Temp`（°C 年均） | `BuildLayer1`（Miami NPP + ≤−5.5 °C 冰盖归零 = **R 的根**）、`ClimateSim`、`WarWeather`、`WildCropsSystem` 各生态位 | ✅ **`TemperatureModel.AnnualMeanC`**（P4-1 已产出） | 单位/粒度/时点/边界同；**范围·海拔口径不同** | **`ADAPTER_REQUIRED`**（原 `SOURCE_MISSING`，2026-10-06 ⑦ 升级） |
| ③ | `Precip`（年降水 mm；Legacy max 10662） | `BuildLayer1`、`ClimateSim`、`WarWeather`、`WildCropsSystem` | ⚠️ `PrecipitationModel.AnnualMm`（**相对量级** 0~2400 → ★⑧ P4-2 已改为**绝对量纲 mm/年**） | 定义同、单位同、**范围/边界语义不同** | **SEMANTICALLY_DIFFERENT** → ★**`ADAPTER_REQUIRED`**（⑧，2026-10-06） |
| ④ | `Biome`（`BiomeType` 柯本细类） | `WaterRich`/`TerrainCost`/`IsColdZone`/`EnvMatches`/`PreyFrac`/`ForageSharesAt`、`WildCropsSystem` | ❌ **无分类器**（旧 `BiomeClassifier` 已随 A 线清退 `6e7fc94`） | 定义缺失 | **SOURCE_MISSING** |
| ⑤ | `SoilLevel`（1–5；0=海洋） | `AlluvFactor`（4→×2、5→×3，**农业产出**）、`InventionModel`（`>=3` 门槛） | ❌ **无土壤模型** | 定义缺失 | **SOURCE_MISSING** |
| ⑥ | `LakeLevel`（byte；0=无湖） | `WaterRich`、`EnvMatches "irrigation"`、`LakeFishAccess`、`WildCropsSystem.RiceSuit`（**只当布尔用**） | ✅ `LakeState`（**逐湖对象**，非逐格水位） | 定义同、**粒度/形态不同** | **ADAPTER_REQUIRED** |
| ⑦ | `MonthTemp`（`byte[12][n]`；−60~60 °C） | `WarWeather`（季节分类）、`WildCropsSystem.Suitability`（`WinterShare`/`MaxMonthTemp`） | ❌ **无季节气候模型** | 定义缺失 | **SOURCE_MISSING** |
| ⑧ | `MonthPrecip`（`byte[12][n]`；月比例） | `WarWeather`、`WildCropsSystem.Suitability`（`WinterShare`/`MinMonthPrecip`/`WetDryRatio`） | ❌ | 定义缺失 | **SOURCE_MISSING** |
| ⑨ | `RiverLevel`（byte；0=无河） | **零消费者** | ✅ `RiverNetwork.IsRiver`（但无人消费） | — | **LEGACY_ONLY** |
| ⑩ | `RiverFlow`（int；−1=无） | **零消费者** | ✅ `RiverNetwork.Downstream`（无人消费） | — | **LEGACY_ONLY** |
| ⑪ | `RiverVolume`（mm） | **零消费者** | ✅ `RiverNetwork.RunoffAccum*`（无人消费） | — | **LEGACY_ONLY** |
| ⑫ | `MineralLevel`（富度<<4\|矿种） | **零消费者**（无 MiningModel） | ❌ | — | **LEGACY_ONLY** |
| ⑬ | `MonsoonLevel`（0–255→0–1） | **零消费者** | ❌ | — | **LEGACY_ONLY** |
| ⑭ | `WildCrops`（5 位 bitmask；不存档） | `InventionModel`（作物驯化门槛） | **Derived**：`WildCropsSystem.Compute` ← ②③④⑥⑦⑧ | **输入缺失 ⇒ 无法重建** | **SOURCE_MISSING**（级联） |
| ⑮ | `WildLivestock`（1 位；不存档） | `CapabilityTable`（畜牧能力）、`FFarmTerritory` | **Derived**：`ComputeLivestock` ← ③④ | **输入缺失 ⇒ 无法重建** | **SOURCE_MISSING**（级联） |

**附：另有零消费者且无新线计划的字段** —— `CurrentDirs / CurrentWarmth / CurrentStrength / Psi`
（洋流已随 A 线清退）⇒ **LEGACY_ONLY**。几何基座 `RadiusKm / CellAreaKm2 / DistKm / Neighbors / Seed`
（新线对应 `SpatialScale` + `Ball`）⇒ **ADAPTER_REQUIRED**（载体不同，是 Bridge 的**横切**问题，不是逐项问题）。

---

## §三 逐项五项判据明细

> 判据记法：**定义** / **粒度** / **时点** / **边界** / **范围**。✅ = 同，⚠️ = 不同但可适配，❌ = 不同且不可直接替换。

### ① `Elev` —— **EQUIVALENT**

| 判据 | Legacy | New World | 判定 |
|---|---|---|---|
| 定义 | 海拔高度（米），海平面 0，>0 = 陆 | 同（`Composer.HeightM`） | ✅ |
| 粒度 | 逐格（`float[]`） | 逐格（`float[]`） | ✅（承载网格不同，见 §五横切） |
| 时点 | 生成期冻结快照 | 生成期一次性（`Regenerate`） | ✅ |
| 边界 | 海平面 0 = 海 | 同；陆格另有 `MinLandElevationM=30` 保底（不改变"0 = 海"语义） | ✅ |
| 范围·单位 | 米 | 米 | ✅ |

**结论**：五项全同 ⇒ **`EQUIVALENT`**。
⚠️ 唯一注意：新线 `HeightM` 是**另一个世界**的地形（不同大陆布局/山脉/尺度）⇒ 接上它 = **换世界**，
不是"换字段"。这属于 Bridge 的**语义**问题（"用哪个世界"），不是**适配**问题。

### ② `Temp` —— **SOURCE_MISSING**（→ Phase 4）

- `WorldGenPlanet.Regenerate()` 产出清单**不含任何温度场**；旧 `scripts/Biome/`（含 `ClimateGenerator`）已清退。
- 但 `Temp` 是 **R 的根**：`BuildLayer1` 用 `MiamiNpp(Temp, Precip)` 并令 `Temp ≤ −5.5 °C ⇒ R = 0`（冰盖）。
  ⇒ 没有温度 ⇒ **整个生产力模型无法在新世界上重建**。
- **结论**：`SOURCE_MISSING`。**不在 CivSim 补**（§B4）⇒ 转 **WorldGen Phase 4「Temperature」生产任务**。
- ★**后续更新（2026-10-06，路线 ⑦ · P4-1）**：温度事实**已产出**（`TemperatureModel` = `WorldGen Climate └── TemperatureFact[H3 res4]`）。
  判定升级为 **`ADAPTER_REQUIRED`**——**不是 `EQUIVALENT`**（旧值含 ±7 °C 随机项 + ±5 °C 洋流项、
  海拔口径为 `elevNorm × 10 km`；实测冷尾 −62.76 vs −29.54），
  但**量纲与阈值口径可比**（冰盖阈值两侧可达：新 11.4% / Legacy 11.9%）。
  详见 `docs/审查报告-P4-1-温度事实与provenance对照.md` §四。

### ③ `Precip` —— **ADAPTER_REQUIRED**（原 **SEMANTICALLY_DIFFERENT**；⑧ P4-2 升级，2026-10-06）

| 判据 | Legacy `PREC` | 新线 `PrecipitationModel.AnnualMm` | 判定 |
|---|---|---|---|
| 定义 | 年降水量（气候量，实测量级） | 年降水**相对量级**（注释自陈："气候模拟上线前为水文驱动输入"） | ⚠️ |
| 粒度 | 逐格 | 逐格 | ✅ |
| 时点 | 生成期 | 生成期 | ✅ |
| 边界 | 含洋面；陆格有真实海陆湿度梯度 | 纯 **纬度带 LUT × 海岸衰减**（`LatBands` + `CoastDecayKm=450 km` + `ContinentalFloor=0.6`），**无季节、无风带** | ⚠️ |
| 范围·单位 | max ≈ **10662 mm**（实测） | `MaxMm = 2400`（**硬上限**） | ❌ |

**结论**：字段名都叫"mm"，但一个是**气候模型输出**、一个是**水文驱动的占位合成场**（量纲不可比）。
⇒ **`SEMANTICALLY_DIFFERENT`**：Bridge **不得直接采用** `AnnualMm` 作为 `Precip`；
`MiamiNpp` 对降水量级高度敏感（指数式），直接替换 = 隐性改 `R`。
⇒ 须由 **Phase 4「Precipitation（气候版）」** 产出真正的年降水气候量，并**重标定 `MiamiNpp`**。
（重标定完成后，本项**可能升级**为 `ADAPTER_REQUIRED`——但那要等 Phase 4 落地，当前不可预支。）

> ★**后续更新（⑧ P4-2 落地，2026-10-06）** —— 上表为**改前**取证快照，保留作证据链。现状：
> - `PrecipitationModel.AnnualMm` 已由**占位相对量级**改写为**绝对量纲（mm/年）事实**：
>   `MaxMm` / `LatFactor` **已删除**，改为 `ZonalMm(lat)[mm/年] × MaritimeFactor`；
>   `ContinentalFloor = 0.6`（单一常数）→ `InteriorRatio(lat)`（纬度 LUT，副热带 0.12）。
>   实测：全球均值 **1,106 mm/年**（地球 ~990）、陆格 min **120.9**（旧占位 **588.9** ⇒ 曾有"无荒漠"缺陷）、陆格 P50 882。
> - **判定升级为 `ADAPTER_REQUIRED`**（实测，非预设）：**同物理量、同单位**，但陆地质 **2.5×**（2,460 → 967）、
>   **海格缺失语义不同**（Legacy 填 0 占 64.88%）、尾部宽度不同。
> - ★★**"高度敏感"的预设被实测修正**：`R_raw = MiamiNpp(T,P)` 的 **P50 几乎不动（1260 → 1262）**——
>   原因是 ① `BuildLayer1` 的 `k = TargetMedianDensity / median(R_raw)` **中位数再归一化**，
>   ② `MiamiNpp` 在 2,400 mm 已 **80% 饱和**。真正变化的是**限制侧**（降水受限 **26.1% → 81.3%**）与
>   **冰盖**（**22.14% → 6.31%**）⇒ **重标定的对象是空间格局，不是整体量级**。
> - **下一步** = `Temperature + Precipitation → R / MiamiNpp 联合重标定`（`├ 冰盖阈值复核 └ 生计响应复核`）。
>   详见 `docs/审查报告-P4-2-降水事实与provenance对照.md` §四。

### ④ `Biome` —— **SOURCE_MISSING**（→ Phase 4）

- 新线**无任何群系分类器**；`BiomeType` 在新线是"纯词汇无生成器"（旧分类器已删）。
- 实测佐证：现存档 `BIOM` 22 值 = `0–3` + `14…31`，不含 `12(Alpine)`/`13(Riparian)`。
- 但 `Biome` 是 CivSim 的**生态总开关**：`WaterRich`（Riparian 判水）、`TerrainCost`（通行成本）、
  `IsColdZone`（火/皮毛解锁）、`PreyFrac`（猎物占比）、`ComputeLivestock`（草原可驯）全部读它。
- **结论**：`SOURCE_MISSING` ⇒ **Phase 4「Biome」生产任务**。

### ⑤ `SoilLevel` —— **SOURCE_MISSING**（→ Phase 4）

- 新线**无土壤模型**。
- 消费点：`AlluvFactor(soil)`（冲积肥力：4→×2、5→×3，直接缩放**农业产出** `rAgri`）；
  `InventionModel` 要求 `SoilLevel >= 3` 才可能发明显著农业。
- **结论**：`SOURCE_MISSING` ⇒ **Phase 4「Soil」生产任务**。

### ⑥ `LakeLevel` —— **ADAPTER_REQUIRED**

| 判据 | Legacy | New World | 判定 |
|---|---|---|---|
| 定义 | "该格是否有湖"（>0） | "该格所属洼地是否已成湖"（`LakeOfBasin[DepressionIdOf[i]] >= 0`） | ✅ 同 |
| 粒度 | **逐格** `byte[]` | **逐湖对象**（`SurfaceElevationM/AreaKm2/VolumeKm3/OutletCell`）+ 逐格 `DepressionIdOf` | ⚠️ **不同** |
| 时点 | 生成期 | 生成期 | ✅ |
| 边界 | 0 = 无湖 | −1 = 不属任何洼地；无湖流域 `LakeOfBasin = −1` | ✅ 语义对齐 |
| 范围·单位 | byte 水位 | float 米（`SurfaceElevationM`） | ⚠️（但**消费者只用布尔** ⇒ 无碍） |

**结论**：`LakeState` 的**信息更丰富**（有面积/体积/水位），但 CivSim 需要的只是**逐格"是否成湖"布尔**。
⇒ **`ADAPTER_REQUIRED`**：适配层 = "`LakeState` → 逐格湖掩码"（`LakeOfBasin[DepressionIdOf[i]] >= 0`）。
★ **该适配层当前尚未设计** ⇒ 严格地，它**还没"明确设计过"**，故本轮**不预设**它进 Bridge；
下一轮若要开 Bridge，须先把这个导出层写成显式契约。

### ⑦ `MonthTemp` —— **SOURCE_MISSING**（→ Phase 4）

- 新线**无季节气候模型**（`PrecipitationModel` 只有年均，无月变化；温度更是零）。
- 消费点：`WarWeather`（冷/旱季分类 → 战争天气）、`WildCropsSystem.Suitability`（5 种子生态位的冬季/最热月项）。
- **结论**：`SOURCE_MISSING` ⇒ **Phase 4「Temperature（季节）」生产任务**。

### ⑧ `MonthPrecip` —— **SOURCE_MISSING**（→ Phase 4）

- 同 ⑦：新线无季节降水。消费点：`WarWeather`（最干月）、`WildCropsSystem`（雨季分明度/最干月）。
- **结论**：`SOURCE_MISSING` ⇒ **Phase 4「Precipitation（季节）」生产任务**。

### ⑨–⑬ `RiverLevel` / `RiverFlow` / `RiverVolume` / `MineralLevel` / `MonsoonLevel` —— **LEGACY_ONLY**

- 在 `scripts/CivSim` + `scripts/Domain` 中**零读取点**。
- ★**关键副作用**（前序已实测）：`WaterRich` 只认 `Biome==Riparian || LakeLevel>0`，
  **不读 RiverLevel/RiverFlow/RiverVolume** ⇒ **"河流灌溉"通道当前实际不存在**，唯一水因子是**湖泊**。
- `River*` 三项在新线**有**对应事实（`RiverNetwork`），但**同样零消费者** ⇒ 与桥无关。
- **结论**：`LEGACY_ONLY`（零消费者，不在桥接范围）。
⚠️ **清退前须另做地图渲染侧取证**（它们可能仍是渲染消费者），禁止仅凭"CivSim 零消费者"判死。

### ⑭ `WildCrops` —— **SOURCE_MISSING**（级联）

- **不存档 · 读档现场重建**：`WildCropsSystem.Compute(this, Seed)`，纯 `f(seed, 气候场)`。
- 但它的输入 = `Temp / Precip / MonthTemp / MonthPrecip / Biome / LakeLevel / Elev`（见 `Suitability`）。
  其中 **5 项是 `SOURCE_MISSING`** ⇒ 派生链**在新线上断在输入端**。
- 消费点：`InventionModel`（作物驯化门槛 `ctx.WildCrops[e.Cell]`）。
- **结论**：`SOURCE_MISSING`（**级联**）——**不是**要"在 CivSim 重算"，而是其输入补齐后**自动恢复**。

### ⑮ `WildLivestock` —— **SOURCE_MISSING**（级联）

- 同构于 ⑭：`ComputeLivestock` ← `Biome`（草原类）+ `Precip`（300–1200 mm）。
- 消费点：`CapabilityTable`（畜牧能力）、`FFarmTerritory`（牧场潜在）。
- **结论**：`SOURCE_MISSING`（级联）。

---

## §四 Legacy → New 依赖链对照（R 的根）

```
Legacy .mpa ─┬─ ELEV  → IsLandCell ─────────────────────────┐  ①  Elev    ← Composer.HeightM   ✅ EQUIVALENT
             ├─ TEMP  ─┐                                     │  ②  Temp    ← (无)               ❌ SOURCE_MISSING
             ├─ PREC  ─┴→ MiamiNpp × WaterRich(×1.5) ────────┴→ R ├─ ③  Precip ← AnnualMm(相对)   ⚠️ SEM_DIFFERENT
             ├─ BIOM  ─┬→ WaterRich(Biome==Riparian ‖ Lake>0)   │  ④  Biome   ← (无)               ❌ SOURCE_MISSING
             ├─ LAKE  ─┘                                    │  ⑥  Lake    ← LakeState          ⚠️ ADAPTER_REQ
             ├─ SOIL  ──→ AlluvFactor（农业产出）             │  ⑤  Soil    ← (无)               ❌ SOURCE_MISSING
             └─ MTMP/MPRC ─→ WildCrops.Suitability ─→ WildCrops│  ⑦⑧ Month*  ← (无)               ❌ SOURCE_MISSING
                                                               └─ ⑭⑮ 派生    ← (级联)              ❌ SOURCE_MISSING
```

**读法**：`R`（空间生产力）的两个自变量 `Temp` / `Precip` **一缺失、一语义不同** ⇒
**当前新线无法独立驱动 `R`** ⇒ 这是"Bridge 先于事实"的硬约束。

---

## §五 结论汇总

### 5.1 Bridge 候选集（唯一有资格进入 Bridge 设计的）

| 输入 | 标签 | 进入条件 |
|---|---|---|
| `Elev` | `EQUIVALENT` | 可直接桥接（但须先解决"用哪个世界"= 换世界的语义决定） |
| `LakeLevel` | `ADAPTER_REQUIRED` | **须先把"逐湖对象 → 逐格湖掩码"写成显式适配契约**（当前未设计） |

⇒ **其余 13 项全部无资格**：5 项 `SOURCE_MISSING` + 2 项级联 `SOURCE_MISSING` + 1 项 `SEMANTICALLY_DIFFERENT` + 5 项 `LEGACY_ONLY`。

### 5.2 横切问题：载体不同（不是逐项问题）

Bridge 的**根本前提**是"CivSim 跑在哪张网格上"：
Legacy = `10n²+2` 顶点胞（`GameGrid`）vs 新线 = **H3**（`Ball` / `SpatialScale`）。
几何基座（`RadiusKm / CellAreaKm2 / DistKm / Neighbors / Seed`）**载体不同** ⇒ `ADAPTER_REQUIRED`（横切）。
**这一项必须在任何逐字段桥接之前先定**——否则逐字段适配无从谈起。

### 5.3 ★ 核心判断

> **Bridge 现在不能建。** 不是接口问题，是**对岸没有可桥接的事实**：
> 驱动模拟的 5 个气候/群系/土壤输入在新线**无生产者**，唯一降水字段**语义不同**。
> **正确动作 = 把缺口登记为 WorldGen Phase 4 生产任务，而不是在 CivSim 侧算一个**（ADR-0005 §B4）。

---

## §六 Phase 4 生产任务清单（由本次取证导出）

> 这些是 **WorldGen 侧**的生产任务 —— **不是** CivSim 的补丁。

| 任务 | 产出事实 | 解锁的 Legacy 输入 | 备注 |
|---|---|---|---|
| **P4-Temperature** | 年均温场 + 月温场 | `Temp` / `MonthTemp` | R 的根；须含"冰盖阈值 −5.5 °C"可比口径 |
| **P4-Precipitation（气候版）** | 年降水 + 月降水（真实 mm 量级） | `Precip` / `MonthPrecip` | 替换 `AnnualMm` 占位场；须重标定 `MiamiNpp` |
| **P4-Biome** | 群系分类（与 `BiomeType` 对齐） | `Biome` | 生态总开关；含水/寒/通行/猎物四类语义 |
| **P4-Soil** | 土壤等级（1–5；与 `AlluvFactor` 对齐） | `SoilLevel` | 农业产出缩放 + 发明门槛 |

**完成这四项后**，级联的 `WildCrops` / `WildLivestock` **自动恢复**（其派生函数无需改）。

---

## §七 边界声明（★ 供后续架构决策引用）

1. **本报告不含任何 Bridge 实现、不含任何接口设计、不改任何生产代码。** 只做取证与判定。
2. **发现缺失事实时的正确结论**：`Bridge 暂不能提供该输入；转为 WorldGen Phase 4 的生产任务`
   —— **不是** "那就在 `CivSimContext` 算一个"（ADR-0005 §B4）。
3. **未决（不擅自行动）**：
   - Bridge 的**载体**（CivSim 迁到 H3 还是保留 Legacy 网格？）—— 独立决策，未拍板。
   - `LakeLevel` 适配层是否本轮设计 —— 未拍板。
   - ⑨–⑬ 零消费者字段是否清退 —— **须先做地图渲染侧取证**。

---

## 附 · 取证命令与关键文件

```bash
# 1) 新线产出清单（无 Temperature / Biome / Soil）
#    scripts/WorldGen/Composition/WorldGenPlanet.cs :: Regenerate()
# 2) CivSim 侧自然字段消费枚举
grep -rn "\.Temp\b\|\.Biome\b\|\.SoilLevel\b\|\.LakeLevel\b\|\.Precip\b" --include="*.cs" scripts/CivSim scripts/Domain
# 3) 零消费者字段（应无输出）
grep -rn "\.RiverLevel\b\|\.RiverFlow\b\|\.RiverVolume\b\|\.MineralLevel\b\|\.MonsoonLevel\b" --include="*.cs" scripts/CivSim scripts/Domain
# 4) 存档 biome 实测
python scratch/inspect_mpa.py userdata/maps/map_seed42_n64_r128.mpa
```

**关键文件**：
- `scripts/WorldGen/Composition/WorldGenPlanet.cs`（新线唯一入口；产出清单 = Bridge 的"事实上限"）
- `scripts/WorldGen/Simulation/PrecipitationModel.cs`（`AnnualMm` 相对量级 = ③ 的判据来源）
- `scripts/WorldGen/Simulation/LakeState.cs`（逐湖对象 = ⑥ 适配层的源；`DepressionIdOf`）
- `scripts/LogicGrid/GameGrid.cs`（输入载体；`FromMapData` = 唯一"存档 → 模拟"桥）
- `scripts/CivSim/Engine/CivEngine.cs::BuildLayer1`（R 的根）
- `scripts/Domain/WildCropsSystem.cs`（生态派生的最大消费者；`Suitability`/`ComputeLivestock`）
