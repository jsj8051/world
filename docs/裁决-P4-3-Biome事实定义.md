# 裁决 · P4-3 Biome 事实定义（四件事）

> 路线 ⑩ · Phase 4 · **P4-3a 语义取证轮**。日期 2026-10-06。
> 上游：⑦ P4-1 温度 ✅ / ⑧ P4-2 降水 ✅ / ⑧b A/B ✅ / ⑨ 路线 A ✅（重标定关闭）。
> 依据：`scripts/Domain/BiomeType.cs`、`scripts/Domain/WildCropsSystem.cs`、`scripts/CivSim/Engine/CivSimContext.cs`、
> `scripts/WorldGen/Simulation/{TemperatureModel,PrecipitationModel,RiverNetwork,LakeState}.cs`、
> `scripts/WorldGen/Final/FinalGeography.cs`、Legacy `.mpa` 只读取证 `scratch/p4_3_biom_forensics.py`。
> **本轮边界：`scripts/WorldGen/` 生产代码 0 改动；不接 CivSim；不实现 Bridge / HumanInputGrid / SubsistenceReadout；
> 不顺手做 Soil；不为匹配 Legacy 加随机噪声。**

---

> ## ★2026-10-09 修订（后续裁决覆盖本条的部分内容）
> 用户拍板**解散 `World.Domain`**，并把 `BiomeType` 迁到 `World.Constants` 且**裁剪为仅柯本气候型**
> （保留 `2,3,14…29` 共 **18 值**；**删除**水面 `0/1/30/31` 与地形 `12/13`）。
> ⇒ 本文档 §一 的「Köppen 气候型 **+ 水面/地形附加类**」与 §5.1 的 **24 类逐一登记表**
>   **已不再代表当前词表**（那 6 个非柯本类已不存在）。
> 修订依据与现状见 `docs/裁决-Domain解散与BiomeType归Constant.md`。
> ⚠️ 若 P4-3b 未来确需海格/地形附加类，**另立事实**，不要回填 `BiomeType`。

---

## §〇 结论（一句话）

**① 分类语义 = 气候型（Köppen–Geiger），沿用 `BiomeType` 词汇；② 输入 = T + P + 海陆/海拔/离岸 + 河湖，Soil 不依赖、月度 `MonthTemp/MonthPrecip` 是 P4-5 前置缺口；③ Legacy 判定 = `SEMANTICALLY_DIFFERENT` → `ADAPTER_REQUIRED`（不预设，生产后复核）；④ P4-3 可恢复 `WildLivestock`，`WildCrops` 卡在 P4-5。**

★**并且本轮发现一个依赖倒置**（已由你拍板，见 §〇-A）。

---

## §〇-A 用户拍板（2026-10-07）：**任务顺序反转**

> **立即暂停 P4-3 的 Biome 生产；把 P4-5 Seasonal Climate 提升为 P4-3 的硬前置；先做 P4-5，再回到 Biome。**
> **不要为了让 Biome 现在跑通，在 Biome 内从年均 T/P 猜月度气候。**

**为什么"近似月度"被否决（B4 论证）**：

```
Annual T/P → Biome 内部估算 Monthly T/P → Biome
```
= **Biome 分类器自己生产它所依赖的 Climate 事实** ⇒ 直接违反 B4（环境事实由 WorldGen 事实层生产，判读/分类层只能消费，不能成为 source）。
并且会产生一个更隐蔽的债：P4-5 真正完成时，Biome 可能继续保留那套隐藏估算 ⇒ **两套月度气候来源**。

**为什么"先做 10 类"被否决**：14/24 ≈ 58% 的类数占比，但覆盖 Legacy 陆地 ≈ **85%**
⇒ "类别数量占比"**严重低估**真实影响 ⇒ 会产出一张**表面完整、语义实际不完整**的 `BiomeFact`。
⇒ **Biome 生产整体阻断**，直到其主要判据所需的 Seasonal Climate 可用；**`BiomeFact` 第一次进入生产时就是完整事实**。

**P4-3 正式拆两段**：

| 段 | 内容 | 状态 |
|---|---|---|
| **P4-3a** | Biome **语义契约**（分类语义 / 输入契约 / provenance 判据 / **24 类逐一登记**） | ✅ **本轮完成** |
| **P4-3b** | `BiomeFact` 生产 + 测试 + 落档 | ⏳ **P4-5 之后** |

**依赖方向正式冻结**：
```
WorldGen ├─ Temperature ├─ Precipitation └─ Seasonal Climate
                        ↓
                      Biome
                        ↓
            WildCrops / WildLivestock
                        ↓
              HumanInputGrid / CivSim
```
**不得**出现 `CivSim 需要 Biome → Biome 需要月度 → Biome 自己补月度` 的反向驱动。

---

## §一 ① 分类语义：`BiomeFact` 究竟表达什么？

### 裁决

> **`BiomeFact` = 柯本气候型（Köppen–Geiger climate classification）+ 水面 / 地形附加类。
> 它是「气候型」，不是植被型、不是生态系统型、更不是游戏用途分类。沿用 `World.Domain.BiomeType`（byte，24 值），不新建分类体系。**
>
> ★**2026-10-09 修订**：`World.Domain.BiomeType` 已迁至 **`World.Constants.BiomeType`** 并
> **裁剪为 18 值（仅柯本气候型）**——"水面 / 地形附加类"（`0/1/12/13/30/31`）**已删除**。见顶部修订横幅。

### 为什么是"气候型"（证据）

`BiomeType.cs` 的取值本身就是 Köppen 代码：`14 Af / 15 Am / 16 Aw / 17 BWh / 18 BWk / 19 BSh / 20 BSk /
21 Cfa / 22 Cfb / 23 Cwa / 24 Csa / 25 Csb / 26 Dfa / 27 Dfb / 28 Dfc / 29 Dwa`。
⇒ 判据是**温度与降水的（月/年）分布**，不是植被组成。
"植被型"会是"针叶林 / 草原 / 苔原"这类词；"生态系统型"还要含生物相互作用——**都不在词汇表里**。

### ★ 守住 B4 / 防"反向驱动"的具体做法

`CivSimContext.EnvMatches`（`scripts/CivSim/Engine/CivSimContext.cs:550-575`）里有一张**游戏用途标签 → `BiomeType` 集合**的映射：

| 游戏标签 | 映射到的 `BiomeType` 集合 |
|---|---|
| `grass` | `HotSteppe` / `ColdSteppe` / `TropicalSavanna` |
| `plain` | `ContinentalHot` / `ContinentalWarm` / `ContinentalDry` / `HotSteppe` / `ColdSteppe` |
| `mediterranean` | `MediterraneanHot` / `MediterraneanCool` |
| `monsoon` | `TropicalMonsoon` / `MonsoonSubtropical` |
| `humidsubtrop` | `HumidSubtropical` / `Oceanic` / `TropicalRainforest` |
| `coldtemperate` / `coldzone` / `river` / `irrigation` | … |

⇒ **这张表必须留在 CivSim**（游戏用途层）。**`BiomeFact` 的判据里不得出现任何这类标签**——
否则就是"为了 `WildLivestock` / 发明树能用而设计 Biome"的反向驱动（你在 ① 里点名的风险）。

**形式化**：`BiomeFact = f(T, P, 海陆, 海拔, 离岸, 河湖)`，判据中**不含**任何 `
WildCrops` / `CivSim` / 游戏标签符号。

### 类的构成（4 组）

| 组 | 值 | 语义 |
|---|---|---|
| 水面 | 0 `DeepOcean` / 1 `Ocean` / 30 `FrigidOcean` / 31 `TropicalOcean` | **覆盖海格** ⇒ Biome 是**全格**事实，不只是陆格 |
| 极地 | 2 `IceCap`(EF) / 3 `Tundra`(ET) | Köppen E 组 |
| 地形/水体 | 12 `Alpine` / 13 `Riparian` | 海拔 / 河湖沿岸修正 |
| 柯本 A/B/C/D | 14–29 | 主体 |

---

## §二 ② 输入来源

### 2.1 上游清单（已确认存在 / 不存在）

| 输入 | 来源（WorldGen 内部） | 状态 |
|---|---|---|
| 年均气温 `T` | `TemperatureModel.AnnualMeanC`（⑦） | ✅ 已有 |
| 年降水 `P` | `PrecipitationModel.AnnualMm`（⑧，绝对量纲 mm/年） | ✅ 已有 |
| 海陆 | `FinalGeography.FinalLand`（= `HeightM > 0`） | ✅ 已有 |
| 海拔 | `HeightComposer.HeightM`（真实米） | ✅ 已有 |
| 离岸距离 | `FinalGeography.FinalDistToCoast`（BFS hops → km） | ✅ 已有 |
| 纬度 | `Ball.CellDirs[i].Y` → `|lat|` | ✅ 已有（载体自带） |
| 河 | `RiverNetwork.IsRiver[]` | ✅ 已有 |
| 湖 | `LakeState.BasinIdOf[]` → `LakeOfBasin[]` | ✅ 已有 |
| **`MonthTemp` / `MonthPrecip`** | — | ❌ **前置缺口 = P4-5 Seasonal Climate** |
| `Soil` | — | ❌ P4-4（**但 Biome 不依赖**，见 2.3） |

### 2.2 ★ Soil：不依赖 ⇒ P4-3 与 P4-4 **无输入循环**

- Köppen 判据**只含温度与降水**（及季相），**不含土壤**。
- `WildCropsSystem` 注释显式确认：`WheatSuit` — "钙质土偏好（**忽略：无土壤类型字段**）"；
  `MilletSuit` — "耐贫瘠（**Soil 权重低→不进适宜度**，产量 `f(Soil)` 处理）"。
⇒ **Biome 不需要 Soil** ⇒ **P4-4 仍是独立缺口**（供未来农业产量），**不阻塞 P4-3**，**无循环依赖**。

### 2.3 ★ 月度：14 / 24 个类依赖它 ⇒ 前置缺口，**不在 Biome 内自算**

| 依赖月度的类（14 个） | 判据 |
|---|---|
| 2 `IceCap` | 最热月 < 0 °C |
| 3 `Tundra` | 最热月 0~10 °C |
| 14 `Af` / 15 `Am` / 16 `Aw` | 最冷月 ≥18 °C；最干月 ≥60mm / 干季长短 / 冬干 |
| 21–25 `Cfa/Cfb/Cwa/Csa/Csb` | 最冷月 >−3 °C；最热月 ≥22 / <22；夏干 / 冬干 / 全年湿 |
| 26–29 `Dfa/Dfb/Dfc/Dwa` | 最冷月 ≤−3（Dfc ≤−15）；最热月分档；冬干 |

| **仅年均可判的类（10 个）** | 判据 |
|---|---|
| 0 `DeepOcean` / 1 `Ocean` | 深度 / 纬度（海格） |
| 30 `FrigidOcean` / 31 `TropicalOcean` | 年均 <−2 °C / ≥18 °C（海格） |
| 12 `Alpine` | 高海拔、温度不够低 |
| 13 `Riparian` | 河 / 湖沿岸 |
| 17 `BWh` / 18 `BWk` / 19 `BSh` / 20 `BSk` | **`P` 与 `10·(T+14)` / `20·(T+14)` 的比较（纯年均）** |

⇒ 按 ② 规则，**这 14 个类登记为 P4-5 前置缺口**；**禁止**在 Biome 内用"纬度 + 大陆性"临时代替月度
（那等于在 Biome 里私建一个季节气候模型，会把 P4-5 的职责提前搬进 P4-3，并在 P4-5 落地时造成两套判据）。

---

## §三 ③ Legacy provenance（沿用 ④ 五项判据）

### 3.1 五项判据逐条

| 判据 | Legacy `BIOM`（`.mpa`） | 新 `BiomeFact` | 差 |
|---|---|---|---|
| **分类定义** | Köppen–Geiger 细类 + 水面/地形附加类，共 24 值（4–11 已废） | 同为 Köppen，沿用同一 `BiomeType` | 同源 |
| **空间粒度** | Legacy n=64 → **40,962** 顶点格 | H3 **res4 → 288,122** 等积格 | **7.0×** 且载体不同（顶点 vs H3 CellId） |
| **计算时点** | 生成期算好、写入 `.mpa` 段（`GameGrid.Biome ← map.Biome`） | `WorldGenPlanet.Regenerate()` 内重算、不入档 | 时点不同 |
| **缺失 / 边界语义** | **实测 `Alpine(12)` = 0 格、`Riparian(13)` = 0 格**（`scratch/p4_3_biom.txt`） | 若按河湖事实生产 `Riparian` ⇒ **不再是恒 0** | **语义改变**（新线会补上一个旧线恒空的类） |
| **数值范围 / 单位** | `byte` 0–31（4–11 为化石值，读档报错） | 同 `byte`，同一枚举 | 一致 |

### 3.2 预期判定（**不预设结论**，生产后复核）

> **`SEMANTICALLY_DIFFERENT` → `ADAPTER_REQUIRED`** —— 与 `Precip` 同型。
> 适配项：**A1 换世界（载体 + 粒度 7.0×）｜A2 时点（不入档）｜A3 `Riparian` 由恒 0 变为有值**。
> 本轮只落判据；实际判定在 `BiomeFact` 生产后按同一张表复核，**不得反向修改判据去迁就结论**。

### 3.3 ★ 附带取证：`BiomeType.cs` 注释与 Legacy 实现**不一致**（注释漂移）

`BiomeType.cs:23-26` 写的是：

```
HotDesert  BWh : 年降水 < 20×(T+14)，年均 ≥18°C
ColdDesert BWk : 同上，年均 <18°C
HotSteppe  BSh : 20×(T+14) ≤ P < 30×(T+14)，年均 ≥18°C
ColdSteppe BSk : 同上，年均 <18°C
```

用 Legacy 自己的 `BIOM` + `TEMP` + `PRECIP` 反推实测阈值（`scratch/p4_3_biom_forensics.py` §3）：

| 阈值 k | `desert: P < k·(T+14)` | `steppe: k(T+14) ≤ P < (k+10)(T+14)` |
|---|---|---|
| **k = 10** | **12 / 12 = 100%** ✅ | **541 / 541 = 100%** ✅ |
| k = 20（注释所写） | 12 / 12 = 100% | **0 / 541 = 0%** ❌ |
| k = 30 | 12 / 12 = 100% | 0 / 541 = 0% ❌ |

⇒ **Legacy 实际用的是 `k = 10`：desert `P < 10·(T+14)`，steppe `10(T+14) ≤ P < 20(T+14)`。**
⇒ ⚠️ **若按枚举注释实现，`BSh`/`BSk` 会产出 0 格 ⇒ `WildCropsSystem.ComputeLivestock` 全空、`EnvMatches("grass")` 恒 false。**
⇒ **实现必须按 `k = 10`；同时把 `BiomeType.cs` 的注释改对**（记为一条文档债，单开 chore，不混进 P4-3 生产 commit）。★**2026-10-09 已修**——随 `BiomeType` 迁 `World.Constants` 时一并把注释改为 k = 10。

### 3.4 ★ 原则 14 在本轮的适用

Legacy 单世界（`seed42`）里 `Aw(16)` = **1 格**、`Csa(24)` = **6 格**、`Csb(25)` = **7 格**。
⇒ **不得**据此断言"新世界也不需要这几类"。草原类在本世界 97.5% 落在 `BSh`/`BSk` 是**该世界的样本**，
**不是**新世界的族分布（原则 14：Legacy 单世界分布只作对照，不作目标）。

---

## §四 ④ `WildCrops` / `WildLivestock` 恢复条件

### 4.1 实际依赖（读 `scripts/Domain/WildCropsSystem.cs` 原文）

| 派生事实 | 需要 `Biome`？ | 需要 `T`+`P`？ | **需要月度？** | 需要 `Soil`？ | 其他 |
|---|---|---|---|---|---|
| **`WildLivestock`** `ComputeLivestock` | ✅ **必须**（`BSh`/`BSk`/`Aw`/`Csa`/`Csb`） | ✅ `P ∈ [300,1200]` mm | ❌ | ❌ | `IsLandCell` |
| **`WildCrops`** 5 种 `Suitability` | 仅 `RiceSuit` 的 `Riparian` 加成 ×1.3 | ✅ | ✅ **必须** | ❌（注释明确忽略） | `Elev`（土豆高原加成）、`LakeLevel` |

`WildCrops` 的月度派生量（**全部**来自 `MonthTemp` / `MonthPrecip`）：
`WinterShare`（冬半年降水占比）· `MaxMonthTemp`（最热月）· `MinMonthPrecip`（最干月）· `WetDryRatio`（雨季分明度）。

### 4.2 ⇒ 直接回答"P4-3 完成后究竟能恢复多少旧功能"

| 旧功能 | P4-3 后 | 卡点 |
|---|---|---|
| **`WildLivestock`** | ✅ **可恢复（年判部分）** | `Aw`/`Csa`/`Csb` 三者须 P4-5 才补齐 |
| **`WildCrops`** 5 种 | ❌ **仍为 0** | **卡在 P4-5 月度**，不在 P4-3 |
| `CivSim` 侧 `Biome` 消费点（7 处） | ✅ 随 `BiomeFact` 可用 | 但**本轮不接 CivSim** |

`CivSim` 侧 7 个消费点（供 Bridge 阶段核对，本轮不动）：
`WaterRich`(Riparian) · `IsColdZone`(IceCap/Tundra/Subarctic/Alpine) · 移动成本（冰/高山 0.2、沙漠 0.3）·
寒冷惩罚 · `EnvMatches`（发明环境 tag 映射）· `ForageShares(biome,…)` · `CivEngine.BuildLayer1` 的水因子。

★ ⇒ **P4-3 的真实收益 = `WildLivestock` + 未来 `HumanInputGrid` 的 `Biome` 输入**；
**不是** `WildCrops`（它挂在 P4-5 上）。这个预期必须先对齐，否则会误判 P4-3 的完成度。

---

## §五 ★ P4-3a 交付：**24 类逐一登记**（判据 → 需要的气候事实）

> ★**2026-10-09 修订**：下表按**当时的 24 值词表**登记。现词表已裁剪为 **18 值**
> （`0/1/12/13/30/31` 六个非柯本类已删除）⇒ 下表**只有 `2,3,14–29` 行仍对应实际枚举值**；
> `0/1/12/13/30/31` 六行仅作历史登记保留。

> 你点名要的表：把 24 个 `BiomeType` **逐一登记**「年均 T/P 是否足够」还是「需要 Seasonal 的哪一部分」。
> 这张表同时是 **P4-5 最小充分事实集反推的输入**（见 §六）。

### 5.1 登记表

约定：夏半年 = **月温最高的 6 个月**、冬半年 = 月温最低的 6 个月（南北半球对称定义，与 `WildCropsSystem.WinterShare` 同口径）。

| 值 | 类 | 判据（Köppen） | 年均 T/P 足够？ | 需要的 Seasonal 量 |
|---|---|---|---|---|
| 0 | `DeepOcean` | 深度 | ✅ | — |
| 1 | `Ocean` | 中纬海面 | ✅ | — |
| 2 | `IceCap` (EF) | 最热月 < 0 °C | ❌ | `T_hot` |
| 3 | `Tundra` (ET) | 最热月 0~10 °C | ❌ | `T_hot` |
| 12 | `Alpine` | 高海拔、温度不够低 | ✅ | — |
| 13 | `Riparian` | 河 / 湖沿岸 | ✅ | — |
| 14 | `TropicalRainforest` (Af) | 最冷月 ≥18；**最干月 ≥60mm** | ❌ | `T_cold`, `P_dry` |
| 15 | `TropicalMonsoon` (Am) | 最冷月 ≥18；最干月 <60 但 ≥ `100 − P/25` | ❌ | `T_cold`, `P_dry` |
| 16 | `TropicalSavanna` (Aw) | 最冷月 ≥18；**冬干 w** | ❌ | `T_cold`, `P_w_min`, `P_s_max` |
| 17 | `HotDesert` (BWh) | `P < 10·(T+14)`；年均 ≥18 | ✅ | — |
| 18 | `ColdDesertKoppen` (BWk) | `P < 10·(T+14)`；年均 <18 | ✅ | — |
| 19 | `HotSteppe` (BSh) | `10(T+14) ≤ P < 20(T+14)`；年均 ≥18 | ✅ | — |
| 20 | `ColdSteppe` (BSk) | 同上；年均 <18 | ✅ | — |
| 21 | `HumidSubtropical` (Cfa) | 最冷月 >−3；最热月 ≥22；全年湿 f | ❌ | `T_cold`, `T_hot`, `P_s_min`, `P_w_min`, `P_s_max`, `P_w_max` |
| 22 | `Oceanic` (Cfb) | 最冷月 >−3；最热月 <22；全年湿 f | ❌ | 同 Cfa |
| 23 | `MonsoonSubtropical` (Cwa) | 最冷月 >−3；最热月 ≥22；**冬干 w** | ❌ | 同 Cfa |
| 24 | `MediterraneanHot` (Csa) | 最冷月 >−3；最热月 ≥22；**夏干 s** | ❌ | 同 Cfa |
| 25 | `MediterraneanCool` (Csb) | 最冷月 >−3；最热月 <22；**夏干 s** | ❌ | 同 Cfa |
| 26 | `ContinentalHot` (Dfa) | 最冷月 ≤−3；最热月 ≥22；全年湿 f | ❌ | 同 Cfa |
| 27 | `ContinentalWarm` (Dfb) | 最冷月 ≤−3；最热月 10~22；全年湿 f | ❌ | 同 Cfa |
| 28 | `Subarctic` (Dfc) | 最冷月 ≤−15；最热月 10~22；全年湿 f | ❌ | 同 Cfa |
| 29 | `ContinentalDry` (Dwa) | 最冷月 ≤−3；**冬干 w** | ❌ | 同 Cfa |
| 30 | `FrigidOcean` | 年均 < −2 °C（海格） | ✅ | — |
| 31 | `TropicalOcean` | 年均 ≥ 18 °C（海格） | ✅ | — |

### 5.2 汇总

| | 类数 | 类值 |
|---|---|---|
| **年均 T/P 足够** | **10 / 24** | 0, 1, 12, 13, 17, 18, 19, 20, 30, 31 |
| **需要 Seasonal** | **14 / 24** | 2, 3, 14, 15, 16, 21, 22, 23, 24, 25, 26, 27, 28, 29 |

★**类数占比 58% ≠ 影响占比**：这 14 类在 Legacy 单世界占**陆地 ~85%**
（`Cfb` 21.9% + `Dfc` 21.6% + `Tundra` 10.1% + `Af` 7.3% + `Cfa` 6.9% + `Dfa` 6.0% + `Dfb` 5.9% + …）
⇒ 这正是"先做 10 类"被否决的量化依据。

### 5.3 ⇒ P4-3b 的放行条件

> `BiomeFact` 只有在 **`T_cold` / `T_hot` / `P_dry` / `P_s_min` / `P_s_max` / `P_w_min` / `P_w_max`** 全部可由
> **`SeasonalClimateFact`（P4-5）** 提供时才开工。
> 在此之前，**不生产任何部分版本的 `BiomeFact`**。

---

## §六 边界声明

- `scripts/WorldGen/` **生产代码 0 改动**（本轮只读取证 + 文档）。
- **未接 CivSim**、**未实现** `HumanInputGrid` / Natural Input Bridge / `SubsistenceReadout`、未做 Soil。
- **未修改** `MiamiNpp` / `TargetMedianDensity` / `−5.5 °C`（⑨ 已冻结）。
- **未为匹配 Legacy 加任何随机噪声**。
- 本轮 = **P4-3a 语义取证轮**（已拍板）；`BiomeFact` 的生产/测试/落档 = **P4-3b**，放行条件见 §5.3。
- **下一步主线 = P4-5 Seasonal Climate**（最小充分事实集反推见 `docs/裁决-P4-5-季节气候最小充分事实集.md`）。

---

## 附 · 关键文件与命令

| 文件 | 角色 |
|---|---|
| `scripts/Domain/BiomeType.cs` | 分类词汇（Köppen 24 值）；**§3.3 注释漂移待修** |
| `scripts/Domain/WildCropsSystem.cs` | ④ 依赖的原始依据（`ComputeLivestock` / `Suitability`） |
| `scripts/CivSim/Engine/CivSimContext.cs` | Biome 的 7 个消费点 + `EnvMatches` 游戏标签映射（① B4 依据） |
| `scripts/WorldGen/Simulation/TemperatureModel.cs` / `PrecipitationModel.cs` | 年均 T / P（**无月度**） |
| `scripts/WorldGen/Simulation/RiverNetwork.cs` / `LakeState.cs` | `Riparian` 的输入（`IsRiver` / `BasinIdOf→LakeOfBasin`） |
| `scratch/p4_3_biom_forensics.py` → `scratch/p4_3_biom.txt` | Legacy `BIOM` 只读取证（分布 / T·P 区间 / B 组自洽性 / 恢复面） |

```bash
# Legacy BIOM 取证（只读）
C:/Users/24026/.workbuddy/binaries/python/versions/3.13.12/python.exe scratch/p4_3_biom_forensics.py
```
