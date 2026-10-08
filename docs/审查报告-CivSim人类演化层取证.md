# 审查报告 · `scripts/CivSim/` 人类演化层完整取证

> 2026-10-06 · 触发：用户要求「把现有 `scripts/CivSim/` 的人类演化层完整取证，对齐
> 自然世界输入 → 资源潜力 → 生计/生产 → 食物 → 人口 → 聚落 → 社会组织 → 政治实体 → 历史演化 这条链」。
> 性质：**纯取证，零代码改动**。方法：全量读 `scripts/CivSim/**`（55 个 .cs / 7622 行）+ 诊断 + 测试。
> 目的：**避免重复设计已经存在的东西**——找出「现有系统里真正缺的那一块最小能力」。

---

## 〇、一句话结论

CivSim 的人类演化层**不是 v0.1 的雏形**，而是一台**已经跑通的四级演化机**
（`band → tribe → chiefdom → state`，22 个机制按 Order 0–80 每 tick 串联），
人口 / 生计 / 聚集地 / 文化 / 宗教 / 科技 / 贸易 / 酋邦 / 国家 / 战争 / 事件链**全部已有实现**。

⇒ 用户这条链的 **9 个环节里 8 个已有实现**，唯一真正空的一环是
**"资源潜力 → 生计"的完整性**（海洋渔业 ⇒ 已于 2026-10-06 以 `FishFrac` 零点补，`ResourceStock` 实测无消费者已冻结）。
⇒ **正确做法不是新建 `PopulationGroup → Subsistence` 骨架**，而是先明确"缺什么消费者"。

---

## 一、已存在的实体 / 数据结构

| 类型 | 文件 | 语义 | 备注 |
|---|---|---|---|
| `Polity` | `Entities/Polity/{Polity,Polity.Chiefdom,Polity.State}.cs` | **唯一社会实体**（部落）；一格一实体 | partial 三分区（Core/Chiefdom/State）与存档字节序对齐；贫血模型，行为全在 Mechanics |
| `Habitation` | `Entities/Habitation/{Habitation,Habitation.Settlement,Habitation.Camp}.cs` | **唯一聚集地实体**（场所）；场所比人长寿 | `KindOf()` 派生 camp/settlement；职能条件 `HasAdmin/HasMarket/HasRitual` |
| `StateEntity` | `Entities/StateEntity.cs` | **国家档案**（制度层持久状态） | Id/CapitalHabId/MonarchId/Treasury/Stability/Legitimacy/BornTick |
| `War` | `Entities/War.cs` | **外交状态**（交战 / 朝贡期） | StateIdA/B、Defender、WinsA/B、TributeTo/From、TributesLeft |
| `CivEventRecord` | `Events/CivEvents.cs` | **事件旁路记录** | Tick/TypeIndex/SubjectId/TargetId/Value；纯 append，不入模拟分支 |
| `CivSimContext` | `Engine/CivSimContext.cs`（1052 行） | 一次演化的全部状态 | 场数组 + 实体表 + 统计 + 全部 ★ 定稿常量 |
| `CivModelBase` / `CivModelRegistry` | `Engine/` | 机制积木基类 + 注册表 | Order 排序执行；`StoneAge()` 由 ConceptRegistry Union 推导 |
| `CivSnapshot` / `CivOverlay` | `Observation/` | **观测投影层**（只读 DTO + 纯函数组装） | 面板唯一数据源；零 Godot 依赖，可单测 |
| `PlayerSession` / `PlayerCommand(s)` | `Gameplay/` | **玩家会话 + 命令注入** | EU4 式绑定国家；null = 纯自动演化 |

**纯派生（非实体但结构化存在）**：`TerritoryId/Size`（并查集连通分量）、`ChiefdomId/Size`（庇护 BFS）、
`StateId/Size`（三条件 AND）、`CellOwner`（影响力场 argmax）、领地格列表 `TerritoryCells/Dists`。

---

## 二、已存在的演化阶段

### 2.1 概念配方（`Concepts/ConceptRegistry.cs`，2026-08-23 拍板「概念 = 机制组合」）

```
band     = { Origin, Harvest, Energy, Growth, SplitMigrate, Culture, Religion, Trade, Conflict, Influence }        10 机制
tribe    = band ∪ { Cultivate, Territory, Mode, Invention, Spread, Habitation }                                    +6
chiefdom = tribe ∪ { Prestige, Chiefdom, Absorption, Conflict(参数 0.5) }                                          +4
state    = chiefdom ∪ { State, StateMechanism, War, Conflict(参数 0.25) }                                          +4
```

- `AllMechanismsUnion()` 按声明序 + 类型去重 ⇒ **运行时 22 个机制实例**（`StoneAge()`）。
- 机制**不绑定概念层**：`Conflict` 被 band/chiefdom/state 三配方复用，差异只在 `Params`（内部冲突倍率 / 贡赋率 / 精英比例 / 开战动机门）。
- 配方参数表 `Params` 是**声明层 + 未来消费入口**，现状值 = `CivSimContext` 常量（行为不变）。

### 2.2 时代标签

`EpochKind`（`Entities/Polity/Polity.cs`）= **反应性统计标签**，不驱动机制：
`IsFarming ? Neolithic : StoneAge`。无硬切换——实体异步进入（金字塔分布）。

### 2.3 机制执行序（Order）

| Order | 机制 | Name |
|---|---|---|
| 0 | `OriginModel` | 起源播种（富饶区 30% 池 + 格距 ≥12 格 + 不同大陆优先） |
| 6 | `CultivateModel` | 农田开垦（领地格 +5%(1−k)） |
| 8 | `InfluenceModel` | 影响力归属（场 argmax + 粘性 1.15） |
| 9 | `HarvestModel` | 采集收获（等边际分配 + 分量缓存） |
| 10 | `EnergyModel` | 能量核算（e=Y/P、s=e−1） |
| 20 | `GrowthModel` | 人口增长（exp(r(1−D/F))） |
| 25 | `PrestigeModel` | 声望积累（宴席 → BigMan → 酋长） |
| 30 | `ModeModel` | 生产方式选择（猎↔农滞回） |
| 40 | `InventionModel` | 科技发明（Kremer + 种子压力） |
| 45 | `TerritoryModel` | 领地凝聚（每 10 tick 并查集） |
| 46 | `ChiefdomModel` | 酋邦凝聚（庇护 BFS，每 10 tick） |
| 47 | `AbsorptionModel` | 吞并（每 10 tick） |
| 48 | `HabitationModel` | 聚落（形成/接管废墟/职能条件） |
| 49 | `StateModel` | 国家涌现（三条件 AND → `StateAssign.Rebuild`） |
| 50 | `SpreadModel` | 科技传播（邻格接触） |
| 51 | `WarModel` | 战争外交（宣战/会战/瘟疫/天气/吞并/朝贡/割地） |
| 52 | `StateMechanism` | 国家制度（国库/稳定度/合法性/君主更替/崩盘） |
| 55 | `TradeModel` | 物物交换（比较优势 + 距离衰减 + 食物保底） |
| 60 | `CultureModel` | 文化互动（Axelrod 邻格） |
| 70 | `ReligionModel` | 宗教演进（泛灵→萨满→祖先 + 派别传播） |
| 75 | `ConflictModel` | 边境冲突（粘性僵持窗口 + 武力易主 + 实控锁定） |
| 80 | `SplitMigrateModel` | 分裂迁移（裂变压力 + 领地继承 + 饥饿/探路迁徙） |

**终止条件**：`FirstFarmTick + 100` ticks 结束；兜底 `MaxTicksNoAgri = 500` 无农停止。
`TickYears = 100` ⇒ 全场约 15k–16.4k 年。

---

## 三、生计 / 生产 / 资源分别做到哪

### 3.1 资源潜力（静态层，先于人类存在）

| 量 | 实现 | 口径 |
|---|---|---|
| `R[]` | `CivEngine.BuildLayer1`：`MiamiNpp(Temp,Precip) × 水因子 × k` | 人/km²；k 相对标定 → **陆地中位 0.1 人/km²**（Binford 狩猎采集锚） |
| 水因子 | `WaterRich(cell)`：`Biome==Riparian || LakeLevel>0 || 邻湿地` → ×1.5 | ⚠️ **不读 `RiverLevel`** |
| 采集三分 | `PreyFrac(biome)` + `FishFrac(biome,isCoast,isRiparian,hasLake)` + `ForageShares` | `猎物+浆果+水产 ≡ 1` |
| 种子适宜度 | `Suit[,]` ← `WildCropsSystem.Suitability`（Temp/Precip/Month*/Biome） | 派生缓存，不入档 |
| 野生物种位 | `WildCrops`（5 位）/ `WildLivestock`（1 位） | 派生，读档惰性重建 |
| 开垦率 | `Cultivation[]` | 0~1；土地竞争载体（采集 ×(1−开垦)、农田 ×开垦） |
| 冲积/灌溉 | `AlluvFactor(soil)`（Soil5×3/4×2）+ `IrrigFactor`（近水 ×5） | ⚠️ `IrrigFactor` 因 Riparian=0 只剩湖泊路径 |

### 3.2 生产（三方式并行 + 劳动力最优分配）

- **方式集** M = `{hunt} ∪ {herd if Livestock 能力 + 生态位} ∪ {farm if IsFarming}`。
- **最优分配 = 等边际闭式 water-filling**（`AllocateAndProduce`）：
  - 凹化 `F_i(n) = P_i·n/(D_i+n)`，`D_i = LF·P_i`
  - LF 两档：采集/牧场 `LaborFrac=0.1`、农田 `LaborFracFarm=0.2`
  - 段 A（仅采集档）/ 段 B（采集+农田）分段闭式解 `√μ`，每格 `n_i = √LF·P_i/√μ − LF·P_i`
- **产出层级**：领地潜在（`FHuntTerritory/FFarmPotentialTerritory/FHerdTerritory`）vs 实际产出（`AllocateAndProduce`）。
- **决策口径**：`ModeModel` 用**领地潜在**比较 `e_猎/e_农`（滞回 0.02），非驻扎格单格。
- 密度权重分离：`InfluenceWeight`（归属，紧支撑 6 步）≠ `ProductionWeight`（产出，(2d+1)(1−d/5) 环形面积）。

### 3.3 食物 / 商品 / 存储

- **`CommodityTable` 6 商品**：`grain`（谷，衰变 0.08）/ `berry`（0.5）/ `meat`（0.4）/ `leather`（0.03）/ `wool`（0.02）/ `straw`（0.01）。
- **双池**：`Polity.Stocks`（随身，容量 0.06P/0.02P）+ `Habitation.Stocks`（粮仓，0.5P/0.2P ×城镇级）。
- **衰变**：随身基础年率；粮仓 × `techMult`（storage 1 / +pottery 0.6 / +settle 0.3 / +grinding 0.15）。
- **食物保底**：贸易出口后人均 ≥ `TradeFoodFloor(0.05)×P`。

---

## 四、人口增长 / 死亡 / 迁徙如何计算

| 过程 | 机制 | 公式 / 规则 |
|---|---|---|
| **增长** | `GrowthModel`（O20） | `P *= exp(rEff·(1 − P/F))`；`r = 0.5/tick`；定居能力 ×1.5；城镇级 ×(1+0.25·Tier)；酋邦赈济缺口 ×0.5 |
| **饥荒** | 同上 | 缺口先吃**随身**、再吃**粮仓**；**易腐先吃**（`FoodIdxByDecayDesc`），谷物留底 |
| **死亡** | 同上 | `F ≤ 0` → `P *= StarveMult(0.7)` 慢性饿死；`P < 1` → `Dead = true` + 事件 |
| **分裂** | `SplitMigrateModel`（O80） | 裂变压力 `pEff = P·(1+max(0,1−F/P)+tension)`；`> SplitPop(25)` 且领地 >1 格 → 子体拿**领地远半**，`SplitShare 0.45` |
| **饥饿迁徙** | 同上 | `IsStarving`（F<P）→ 1–3 跳内最高 R **无主**格；冷却 `MigrateCooldown 8` |
| **探路迁徙** | 同上 | `ScoutChance 0.02/tick`、`P≥100`、迁出 30% |
| **殖民/扩张** | 同上 | `PickMigrateTarget` BFS 6 跳；`ColonizeScore = cost×(1+0.3·R/RMax)`（距离主导 + 肥度微偏好） |
| **战争减员** | `WarModel` | 会战败方 ×(1−`WarLoss 0.03`)；瘟疫 ×(1−5~10%)；天气（严寒/雨季/干旱）额外损耗 |
| **冲突减员** | `ConflictModel` | 胜者 ×0.92、败者 ×0.80；`ConflictExpelChance 0.6` 驱逐 |
| **吞并** | `AbsorptionModel` | 无地可逃 → 并入 `overlord.P += e.P×0.5`，自身 Dead |

---

## 五、聚落 / 文化 / 部落 / Polity 是否已有

**全部已有。**

| 层 | 实现 | 关键机制 |
|---|---|---|
| 部落 **Polity** | `Polity` | 一格一实体；人口 `P`；科技 `TechKeys`；文化/宗教份额场 |
| 领地 | `TerritoryModel` + `CellOwner` | 影响力场加权 Voronoi；并查集连通分量（同语言群 + 邻格） |
| **聚落 Habitation** | `HabitationModel` | camp/settlement；废墟接管；**职能条件** `HasAdmin`（酋邦中心/都城）→ 城市、`HasMarket`（贸易伙伴 ≥2）/`HasRitual`（主导派教 <0.7）→ 集镇、无 → 村庄 |
| **文化** | `CultureModel` | `CultureShare`（top-2 份额）+ `CultureGroupShare`（语言群，独立 key 空间）；Axelrod 邻格（**仅同语言群**）+ 分裂 5% 漂变 |
| **宗教** | `ReligionModel` | `ReligionShare`（5 段：泛灵/萨满/祖先/…）+ `ReligionCultShare`（top-2 派别）；升级链盈余+细石器/定居；只向高阶传播 |
| **酋邦 Chiefdom** | `ChiefdomModel` | **庇护 BFS**（至尊酋长 `ChiefReach=12` 内）；继承窗口危机；`Prestige/Tribute` 策略 |
| **国家 State** | `StateModel` + `StateAssign` + `StateMechanism` | 三条件 AND（都城 IsCity + 贡赋池 ≥ 人口×0.01 + 都城存续 ≥20 tick）→ 制度实体（国库/稳定度/合法性/君主） |
| **战争 War** | `WarModel` + `WarAims` + `WarWeather` | 宣战动机门（领土野心/军事优势/怨恨/亲缘/贸易纽带）+ 会战 + 瘟疫 + 天气 + 吞并/朝贡/割地/断交 |
| **历史演化** | `CivEventRecord` + `EventTypes` | 12 类事件（文明纪元/国家涌现/崩溃/宣战/吞并/朝贡/停战/发明/科技传入/分裂/政权覆灭/聚落升级） |

---

## 六、哪些是运行时状态 / 派生量 / 入档

### 6.1 存档（v17 段表格式，`CivMapArchive`）

**10 段**：`HEAD`（固定字段）· `NATR`（自然层全量快照）· `TRIB`（实体段，**`CivArchiveSchema` 声明式清单驱动 + 反射校验**）·
`LAND` · `STTL`（聚落）· `WARS`（战争）· `EVNT`（事件）· `STAT`（国家档案）· `PLAY`（玩家会话）· `REFS`（来源地图引用）。
**段缺失语义 = 该系统不存在 ⇒ 现场派生 / 空列表**（旧档兼容策略）。
`Version = 17`；`ClassifyVersion` 只接受 17（旧档全删，用户 2026-08-23 拍板）。

### 6.2 `Polity` 字段三分类

| 分类 | 字段 |
|---|---|
| **入档**（TRIB 段，清单驱动） | `Id` `P` `IsFarming` `TechKeys`(内联变长) `CultureShare` `CultureGroupShare` `ReligionShare` `ReligionCultShare` `Cell` `OriginCell` `BornTick` `LastMigrateTick` `LastSplitTick` `LastConflictTick` `Stocks` `SettledSince` `PlaceId` `Prestige` `Contributed` `SuccessionUntil` `ConqueredBy` `LastWarTick` |
| **派生**（`SettleDerived` 重建，不存档） | `FLast` `FHuntLast` `FHerdLast` `FFarmLast` `FBerryLast` `FFishLast` `EPerCap` `Surplus` `CarryMult` `CapMask` `TerritoryId` `TerritorySize` `ChiefdomId` `ChiefdomSize` `StateId` `StateSize` `IsBigMan` `IsChief` |
| **上下文场** | 入档概念：`CellOwner`（LAND）/ `Cultivation`（LAND） | 
| **上下文场（派生）** | `R` `CellF` `CellPop` `CellFarmPop` `CellPolities` `CellBest*` `CellOwnerInf` `TerritoryCells/Dists` `BfsStamp` `LockedUntil` `WildCrops` `Suit` `RMax` |
| **⚠️ 不入档的守卫（已知例外）** | `TerritoryLastRebuild` `ChiefdomLastEval` `AbsorptionLastEval` `ChiefdomLastEval` —— **T04 靠人工对齐** |

### 6.3 `SettleDerived` 边界重建依赖序（唯一重算入口）

```
① RefreshCellStateCore → ② RebuildInfluence → ③ RecomputeProduction → ③b DeriveLeadership
→ ④ TerritoryModel.Rebuild → ⑤ ChiefdomModel.Rebuild → ⑤b StateAssign.Rebuild → ⑥ RefreshCellStateF
```
调用点：读档后 / `Run` 结尾 / `Continue` 开头 —— **三条路径同一函数**（消除"重算各写一套"缺陷）。

---

## 七、当前自然输入分别来自 New WorldGen / Legacy MPA / CivSim 自己派生

> 依据：`docs/审查报告-CivSim自然输入Provenance.md`（2026-10-06 纯取证）。

| 输入 | 消费点 | 新线生产者 | 有效 Source | 可重建 |
|---|---|---|---|---|
| `Elev` | `IsLandCell`/`IsCoast`/`PotatoSuit` | ✅ `Composer.HeightM` | Legacy | ⚠️ 可算但**未接线**且世界不同 |
| **`Temp`** | `BuildLayer1`（R 的根）、`ClimateSim`、`WarWeather`、`WildCrops` | ❌ **无温度模型** | **Legacy** | ❌ |
| **`Precip`** | 同上 | ⚠️ `PrecipitationModel.AnnualMm`（未接线，`MaxMm=2400` vs Legacy 10662） | **Legacy** | ❌ 语义不等价 |
| **`Biome`** | `WaterRich`/`TerrainCost`/`EnvMatches`/`IsColdZone`/`ForageSharesAt`/`WildCrops` | ❌ **无分类器** | **Legacy** | ❌ |
| **`MonthTemp`** | `Suitability`、`WarWeather` | ❌ | **Legacy** | ❌ |
| **`MonthPrecip`** | 同上 | ❌ | **Legacy** | ❌ |
| **`SoilLevel`** | `AlluvFactor`、`InventionModel`(≥3 门槛) | ❌ **无土壤模型** | **Legacy** | ❌ |
| `LakeLevel` | `WaterRich`/`LakeFishAccess`/`RiceSuit` | ✅ `LakeState.Lakes` | Legacy | ✅ 未接线 |
| `River*` | **零消费者** | ✅ `RiverNetwork` | Legacy | — |
| `MineralLevel` / `MonsoonLevel` / `Current*` | **零消费者** | ❌ | Legacy | — |
| `WildCrops` / `WildLivestock` | 驯化门槛 / 畜牧能力 | **Derived** | Derived | ✅ 不入档 |
| `RadiusKm`/`CellAreaKm2`/`Neighbors`/`Seed` | 几何与确定性基座 | ⚠️ 新线 `SpatialScale`+`Ball` 未接线 | Legacy HEAD | ⚠️ 部分 |

**派生链（R 的根）**：
```
Legacy .mpa ─┬─ ELEV → IsLandCell ─┐
             ├─ TEMP ─┐            │
             ├─ PREC ─┴→ MiamiNpp × WaterRich(×1.5) → R
             ├─ BIOM ─┬→ WaterRich(Biome==Riparian ‖ LakeLevel>0)
             ├─ LAKE ─┘
             ├─ SOIL → AlluvFactor
             └─ MTMP/MPRC → WildCrops.Suitability → WildCrops
```

**★ 河流对 CivSim 完全无影响**：`WaterRich` 不读 `RiverLevel`；而 `Riparian` 在一切现存档恒为 0
（实测 `map_seed42_n64_r128.mpa` BIOM 值 = 0–3 + 14…31，**不含 12/13**）⇒ **唯一水因子来源是湖泊**。

**当前 Human Simulation 是 Legacy-Data-Driven 模拟，不是 New-WorldGen-Driven 模拟。**
（"何时允许摆脱 `.mpa`"是**独立架构课题**，单独立项。）

---

## 八、当前明确的 TODO / FAIL / 架构债务

| # | 项 | 性质 | 证据 |
|---|---|---|---|
| 1 | **Legacy Climate/Biome Provenance Debt** | **最大架构债**：`Temp`/`Biome`/`MonthTemp`/`MonthPrecip`/`SoilLevel` 不可重建 | §七 |
| 2 | **`scripts/verify.sh` 恒 ❌** | 引用的 `TectonicsTest.tscn`/`MonsoonDiag.tscn` 不存在；`regress_v9_n64.mpa` 路径失效 ⇒ 唯一"一键回归"失效 | 2026-10-06 实测 |
| 3 | **T04 靠人工对齐 + 短程验证** | `TerritoryLastRebuild`/`ChiefdomLastEval`/`AbsorptionLastEval` 不入档（读档端 −1 vs 内存端末值）；只续跑 20 tick ⇒ **PASS ≠ 全字段已入档** | `T04_Continuation` 注释 |
| 4 | **`CivSimContext.FOf()` / `ColdFloor()` 是死代码** | 零调用者；且只写 `FHunt/FHerd/FFarm`，**不写 `FBerry/FFish`** ⇒ 若复活会成为新双计源 | `grep "FOf("` 仅命中自身 |
| 5 | **`Habitation.Camp` 形态行为未接线** | `KindOf()` 派生有了，但 camp 的随迁/拆营行为待 nomadic 概念落地 | `Habitation.cs:79` |
| 6 | **ConceptRegistry 占位配方** | 游牧聚落 / 村庄 / 城市配方未纳入（需 Herd 新积木） | `ConceptRegistry.cs:26` |
| 7 | **宗教链未完成** | `祖先 → 多神 / 多神 → 一神` 未实现（注释："后续阶段"） | `ReligionModel.cs:44` |
| 8 | **文档/代码漂移** | `OriginModel` 头注释写 "P=100"，代码用 `CivSimContext.OriginPop = 10f` | `OriginModel.cs:16` |
| 9 | **CivSim 无游戏内生产入口** | 主场景 `WorldGenWorld.tscn` 不跑 CivSim；只经诊断 `GameGrid.FromMapData(.mpa)` | Provenance §二 |
| 10 | **T90/T91 不进全量默认** | `WantExplicit` gate ⇒ 常规回归不覆盖过采判据/渔业验收 | `CivSimDiag.cs:224-229` |
| 11 | **`fish` 商品 / `ResourceStock` 冻结** | 触存档格式（Version 17→18 + Peek 183→187）⇒ 故意延迟 | 审查报告 §7.4 |
| 12 | **选区颜色未定** | 与河流/道路/边界/地质/地图模式一起定统一 `Selection Accent` | MEMORY |

**无已知 FAIL**：全量单测 496 PASS、地图 T 套件 79 PASS（T04/T04b 均 PASS）。

---

## 九、已有测试、诊断工具与可复现实验

### 9.1 单元测试（CivSim 相关 7 文件 / 156 项特性）

| 文件 | 特性数 |
|---|---|
| `CivSimModelTests.cs` | 40 |
| `CivSimMechanicTests.cs` | 40 |
| `CivSimMechanics2Tests.cs` | 33（含 ① FishPotential 4 项） |
| `CivSimWarTests.cs` | 19 |
| `CivOverlayTests.cs` | 10 |
| `CivEventTests.cs` | 8 |
| `ConceptRegistryTests.cs` | 6 |

全仓单元测试 **496 PASS / 0 FAIL**。

### 9.2 诊断工具（`scenes/diag/*.tscn` + `scripts/Diagnostics/`）

| 工具 | 用途 |
|---|---|
| **`CivSimDiag`** | 主测试套件：**S1–S7** 构造场景 + **T01–T22 / T52** 地图测试 + **T90**（过采判据）+ **T91**（渔业验收）；支持 `--only/--skip/--arch/--seed/--origins/--out` |
| `HumanLayersProbe` | 人文图层（文化/宗教/势力连通块、飞地、国家诊断、战争、扩张、色碰撞）+ 导出等距柱状 PNG |
| `PopTraceDiag` | 承载分析（陆地/R 分布/每实体 P/F/领地） |
| `EvolveCmp` | 重新演化 `.mpa` → 新 `.cmp` |
| `ArchiveDiag` / `PeekDiag` | 存档路径解析 / 段表窥视 |
| `CiviDiag` / `LogicGridDiag` / `FieldCompare` | 其他 |

### 9.3 可复现命令

```bash
dotnet build
dotnet test tests/World.Tests/World.Tests.csproj                          # 单元 496
GODOT=/d/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe
ARCH=user://maps/map_seed42_n64_r128.mpa                                  # n=40962

$GODOT --headless --path . res://scenes/diag/CivSimDiag.tscn -- --arch=$ARCH              # 全量 79
$GODOT --headless --path . res://scenes/diag/CivSimDiag.tscn -- --arch=$ARCH --only=T90   # 过采
$GODOT --headless --path . res://scenes/diag/CivSimDiag.tscn -- --arch=$ARCH --only=T91   # 渔业
$GODOT --headless --path . res://scenes/diag/HumanLayersProbe.tscn -- --map=user://maps/map_seed42_n128.cmp
```

---

## 十、★「已经实现但我们刚才没意识到」的清单

> 这一节是对齐这条链时**最容易重复设计**的部分。

| # | 我们可能假设"要新建"的 | 实际状态 |
|---|---|---|
| 1 | 「`SubsistenceProfile` 份额启发式（`New = Old×0.8 + Opp×0.2`）」 | **已有更强形态**：等边际闭式 water-filling（最优解，非启发式）。现状缺的只是"读出"，不是"行为" |
| 2 | 「`Settlement` 人口等级 / 阈值升阶」 | **已重设计为职能条件系统**（`HasAdmin/HasMarket/HasRitual` → 村庄/集镇/城市），旧 `Level` 已删（v17） |
| 3 | 「部落 / 酋邦 / 国家」 | **四级全有**，且酋邦=个人庇护网络、国家=**制度实体**（EU4 式国库/稳定度/合法性/君主更替/崩盘） |
| 4 | 「政治实体」 | 已有 `StateEntity` 档案 + `War` 外交状态 + 策略多态层（`WarPolicies`/`MembershipPolicies`/`ConflictPolicies`/`TributePolicies`/`WarAimPolicies`） |
| 5 | 「历史演化 / 时间线」 | 已有 **`CivEventRecord` 12 类事件流**（纯旁路，同 seed 逐比特一致） |
| 6 | 「聚落」 | 已有 `Habitation`（场所比人长寿 + 废墟接管 + 粮仓） |
| 7 | 「文化 / 语言群 / 宗教」 | 已有**份额场**（Σ=1，top-2 存储），含「文化标签」与「文化群」**双 key 空间**，非标签 |
| 8 | 「科技」 | 已有 **`techs.csv` 数据驱动金字塔**（石器→新石器→青铜）+ 依赖链 + Kremer 发明 + 邻格传播 + Rogers S 涌现 |
| 9 | 「领地 / 生产场地」 | 已有**影响力场涌现的加权 Voronoi**（非硬边界）+ 归属核与产出核**分离** |
| 10 | 「贸易」 | 已有比较优势物物交换 + 距离衰减 + 食物保底 + **交战国断交封锁** |
| 11 | 「资源存量」 | **已实测无消费者**（T90 档位 A/B，从未越过 1）⇒ 冻结，不是"缺" |
| 12 | 「海洋渔业」 | **已补**（`FishFrac`，零点状态，退化解逐格精确） |
| 13 | 「玩家介入 / 实际游玩」 | 已有 `PlayerSession` + `PlayerCommands`（EU4 式税率覆盖/提稳定/宣战命令注入） |
| 14 | 「观测面板」 | 已有 `CivSnapshot`/`CivOverlay` 投影层（模拟层永不依赖 UI） |
| 15 | 「能力开关系统」 | 已有 `CapabilityTable`（uint 32 位图 + 条件 lambda + 每 tick 缓存） |
| 16 | 「存档格式」 | 已是**段表格式 + 声明式清单 + 反射校验**（改字段名会让清单过期 → 测试红） |
| 17 | 「跨海迁徙 / 地形突破」 | 已有（`canoe` 解锁海洋、`fire`/`clothing` 解锁冰原，`TerrainCost`） |
| 18 | 「气候相似度（轴向效应）」 | 已有 `ClimateSim`（温差 25°C 满衰减 / 降水差 800mm 满衰减）× `TerrainCost` = `BorderCost` |

---

## 十一、结论：真正缺的"最小能力"

**证据链给出的判断：这条链不需要新建 `PopulationGroup → Subsistence` 骨架，也不需要新层。**

三个候选，按"是否有真实消费者"排序：

### 候选 A（**推荐，零状态零风险**）：生计构成读出 `SubsistenceReadout`

- **现状**：`AllocateAndProduce` 已算出 `fHunt/fHerd/fFarm/fBerry/fFish`（分量缓存），但**没有归一化读出**。
- **缺什么**：一个**纯派生函数**把分量归一化成 `Σ ≈ 1` 的份额视图（**不存档、不进决策、不出现在任何公式右侧**）。
- **价值**：让"生计分配 Σ ≈ 1"这类断言立刻可测；也是观测面板"这个部落在吃什么"的唯一数据源。
- **边界**：不动 water-filling，不动存档，不动任何公式。

### 候选 B：把"缺什么消费者"钉死（**做之前先做**）

- 已钉死两条：`ResourceStock`（无"存量被挖空"消费者）、`fish` 商品（无"跨 tick/跨主体"消费者）。
- **新引入任何"状态"之前**，必须先点名它的消费者是谁 —— 这是第 1 / 12 条永久原则的直接应用。

### 候选 C（工程前置，非能力）：T04 守卫收敛 / 加长续航

- 只要将来**真要新增状态字段**，必须先把"三个不入档的凝聚守卫"收敛或明确豁免，并加长 T04 续航跨度。
- 本轮（① FishPotential）**零新增状态**，故不阻塞。

---

## 附：取证覆盖

| 项 | 覆盖 |
|---|---|
| `scripts/CivSim/**` | 55 个 `.cs` / 7622 行，全量读 |
| 诊断 | `CivSimDiag*`（5 分片）、`HumanLayersProbe`、`PopTraceDiag`、`EvolveCmp` 等 |
| 测试 | 7 个 CivSim/Concept 测试文件 |
| 文档 | `审查报告-HumanSimulation-v0.1与CivSim重叠判定.md`、`审查报告-CivSim自然输入Provenance.md`、`阶段6设计-古典时代机制.md`、`石器时代设计.md` |
| 复现 | 见 §9.3 |
