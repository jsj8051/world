# 审查报告 · P4-2 降水事实（WorldGen）与 ④ provenance 对照

> 路线 ⑧ · Phase 4 · 第 2 项。日期 2026-10-06。
> 上游：`docs/审查报告-P4-1-温度事实与provenance对照.md`（⑦）。
> 本轮**只做 WorldGen 事实生产**：**不接 CivSim、不写 Bridge、不实现 `SubsistenceReadout`、不重标定 `R`、不做 Biome**。

---

## §〇 结论（一句话）

`PrecipitationModel.AnnualMm` 已从**"相对量级占位（`MaxMm=2400` × 无量纲系数）"** 改写为
**绝对量纲降水事实（mm/年）**；④ 对照判定 = **`ADAPTER_REQUIRED`**（自 `SEMANTICALLY_DIFFERENT` 升级），
**该判定由实测给出，不是预设**。★最关键的实测结论：

> **Legacy 陆地把降水的整体量级做成了新事实的 2.5×（陆 mean 2,460 vs 967 mm/年），
> 但二者的 `MiamiNpp` 分布中位数几乎相同（1260 vs 1262）。
> ⇒ MiamiNpp 对"绝对量级"的敏感度被 **饱和** + **`k` 中位数再归一化** 双重吸收；
> 真正变的是"**谁在限制**"：降水侧受限比例 **26.1% → 81.3%**。**
>
> ★**2026-10-06 后续更正（独立佐证 A/B，12 seed × res2/res4）**：上表读数来自**简化链路**（非生产链路），
> 且「`k` 中位数再归一化吸收量级」是**代数恒等式**而非经验发现。
> **本节结论请按 §4.5 末尾的"★后续更正"块阅读** → `docs/审查报告-P4-2-独立佐证AB.md`。

---

## §一 交付物与边界

| 项 | 内容 |
|---|---|
| 生产代码 | `scripts/WorldGen/Simulation/PrecipitationModel.cs`（**原地升级**，不新增第二个降水源） |
| 接线 | `WorldGenPlanet.Regenerate()` 未改调用（签名不变）；`[WORLDGEN-TIMING]` 增读数 `meanP=` |
| 测试 | `tests/World.Tests/PrecipitationModelTests.cs` 重写为 **17 条**（原 10 条） |
| 引用面 | `RiverNetworkTests` 1 行、`tests/PerfBench/Program.cs` 2 处（**纯诊断**，非生产） |
| 取证工具 | `scratch/p4_2_legacy_r.py`（Legacy 侧 R 同构复算，只读） |
| 全量单元 | **510 → 517 PASS / 0 FAIL** |
| 地图套件 | **79 PASS / 0 FAIL**（Legacy 侧不受影响） |
| Headless res4 | `n=288122 res=4 land=29.0% regions=95 meanT=13.1C meanP=1098mm/年 basins=6971 lakes=3439 thr=0.636 19221 ms`，**零 ERROR** |

### ★为什么是"原地升级"而不是"新增一个绝对量纲模型"

防膨胀三问第 1/2 问：`PrecipitationModel.AnnualMm` **已经是** WorldGen 水文的唯一降水输入
（`RiverNetwork` / `H3Hydrology.cellWeight` / `LakeState` 三处消费者 + `ArchitectureContractTests` 的
`Hydrology_DoesNotReferenceLegacyClimateLine` 明文契约"唯一降水输入 = `PrecipitationModel`"）。
新增第二个降水类会**同时**违反"能否由既有事实表达"与"唯一事实源"，并让三处消费者面临"该读谁"的歧义。
⇒ 升级 `AnnualMm` 的**语义与数值口径**，而不是加层。

---

## §二 事实是什么（P4-2 边界 ①）

### 2.1 定义

```
PrecipitationFact[i] = ZonalMm(|lat_i|)  ×  MaritimeFactor(lat_i, FinalDistToCoast[i])

MaritimeFactor = InteriorRatio(lat) + (1 − InteriorRatio(lat)) · exp(−内陆km / CoastDecayKm)
```

- **`ZonalMm`** ＝ 纬向年平均降水（**mm/年**，地球量级）：ITCZ 峰 → 副热带干槽（±25°）→ 温带次峰（±55°）→ 极地低值。
- **`InteriorRatio`** ＝ 同纬度"深内陆 / 海岸"降水的渐近比（无量纲）：副热带最低（下沉气流），赤道最高（对流性）。
- **`CoastDecayKm`** ＝ 450 km（**几何尺度 B**，D-14 已从 hop 改成 km；**本轮不动**，见 §五-3）。

### 2.2 单位（P4-2 边界 ①：不使用"归一化降水量"冒充绝对量）

| | 改前（占位） | 改后（事实） |
|---|---|---|
| 式子 | `MaxMm(2400) × LatFactor(0~1) × CoastFactor(0.6~1)` | `ZonalMm(lat)[mm/年] × MaritimeFactor(0.12~1)` |
| 语义 | **相对湿润度**（无量纲系数乘积） | **年降水总量** |
| `AnnualMm = 1800` 意味着 | "1800 个相对单位"（无物理含义） | **1800 mm/年**（地球温带海洋性陆地的量级） |
| 暴露的接口 | `MaxMm` / `LatFactor`（归一化名） | `ZonalMm` / `MaritimeFactor` / `InteriorRatio`（带单位/物理含义） |

⇒ **验收检查 B（绝对量纲自证）**：`PrecipitationFact = 1800` **就是 1800 mm/年**。
由 `ZonalProfile_IsAbsoluteMm_EarthLike` + `Field_IsAbsoluteMm_NotNormalizedIndex` 两条钉死
（后者判据：干端必须进入**数百 mm 以下**、湿端必须达到**两千 mm 以上**、干湿比 > 8——归一化场不可能同时满足）。

### 2.3 空间 identity 与生产粒度（P4-2 边界 ②）

- 逐格成对 `ball.CellIds`（H3 CellId）**逐位对齐**；生产档 `ProductionRes = res4`。
- **不经过 res3**：模型按传入 `Ball` 的 res 直接产出，代码中**无任何 res 分支**。
- 消费面**只有 Final 层**：`FinalLand`、`FinalDistToCoast`（+ `Ball.CellDirs` 作纬度载体）。
- **不读 CivSim / 不读 Legacy `.mpa` / 不认识 `GameGrid` / 不从 CivSim 反推**。

### 2.4 边界 / 缺失语义

- **每个格都有值**（海格 = 海面降水，陆格 = 陆地降水）⇒ **无 0 / −1 / null 缺失语义**。
- ★**与 Legacy 有意不同**：Legacy `PREC` 把 **64.88% 的格（几乎全是海格）填 0**——
  那是"人类只用陆地降水"的**建模产物**，不是一个"降水量事实"。本事实按物理给海格真值。
- 海格**不参与**大陆性（离岸距离对海面无意义）⇒ `MaritimeFactor ≡ 1`，取纬向基准。
- **不表达**（已钉清的边界，不是悄悄留的窟窿）：**地形雨**（orographic，需 `HeightComposer` 输入）、季风、洋流、季节循环 → 后者是 `P4-5 Seasonal Climate` 的课题。

### 2.5 退化解（永久原则 4）

`CoastFactor(1, ·, ·) ≡ 1.0`：**全陆临海**世界（`FinalDistToCoast ≡ 1` ⇒ 内陆深度 0 km ⇒ `exp(0)=1`）
⇒ **逐点退化到 D-14 修复前的行为**（那时因字段选错恰好恒 1.0），也等价于"无大陆性的纯纬向场"。
由 `CoastFactor_DegeneratesToUnity_WhenAllLandIsCoastal`（对 4 个纬度）钉死。

### 2.6 尺度口径（永久原则 2）

`hop → km = √3 × SpatialScale.CellEdgeKm`（随 res 变）。`CoastDecayKm` / `InteriorRatio` 都是
**几何 B / 无量纲比例**，**不是** hop。由 `CoastDecayLength_IsKm_NotHops` 双向钉死（正证 res3/res4 同物理深度同系数；反证写死 10 跳会漂移 2.6×）。

---

## §三 实现与验收（实测）

### 3.1 测试清单（17 条，全绿）

| # | 测试 | 钉住的判据 |
|---|---|---|
| 1 | `ZonalProfile_IsAbsoluteMm_EarthLike` | 赤道 2,200 / 副热带 700~1,000 / 极地 100~400 **mm/年** |
| 2 | `Field_IsAbsoluteMm_NotNormalizedIndex` | 陆格 min < 400、max > 1,800、干湿比 > 8（归一化场不可能） |
| 3 | `GlobalMean_IsEarthLike_ArithmeticMean` | `MeanMm` = **算术平均**（不乘 `cos(lat)`）；落在 800~1,400 |
| 4 | `Ocean_HasRealPrecipitation_NotZeroFilled` | 海格**严格等于** `ZonalMm(lat)`，且**不填 0** |
| 5 | `Land_IsNeverWetterThanOcean_AtSameLatitude` | `MaritimeFactor ≤ 1` |
| 6 | `DryTail_ReachesArid_AndWetTail_ReachesHumid` | **P1/P5/P50/P95 尾部**（检查 C）+ P95 > 3×P5 |
| 7 | `AridLand_Exists_AndIsSubtropicalInterior` | 干旱格须来自**内陆**且**纬度高于**湿润格（物理归因） |
| 8 | `CoastWeighting_ActuallyApplies_AndTracksDistanceToCoast` | D-14 回归钉（临海 > 内陆） |
| 9 | `CoastDecayLength_IsKm_NotHops` | D-14 回归钉（km 不是 hop） |
| 10 | `CoastFactor_Monotone_AndBoundedByInteriorRatio` | 单调不增；下界 = 该纬度 `InteriorRatio` |
| 11 | `CoastalPenetration_IsPureExponential_AndLatitudeIndependent` | 闭式重算 `exp(−(d−1)·k/CoastDecayKm)` |
| 12 | `InteriorRatio_IsLatitudeDependent_SubtropicalDriest` | 副热带最干且 ∈ [0.02, 0.30] |
| 13 | `CoastFactor_DegeneratesToUnity_WhenAllLandIsCoastal` | ★退化解 |
| 14 | `CoastFactor_RejectsNonPositiveKmPerHop` | 非法参数 |
| 15 | `AnnualMm_InAbsoluteRange_NoNaN_AndMeanPositive` | 有界 + 无 NaN + 均值 > 0 |
| 16 | `EquatorialBand_IsWetterThanSubtropicalDryBand` | 纬度带语义 |
| 17 | `SameSeed_BitwiseIdentical` | 确定性 |

### 3.2 ★实测：旧占位场 → 新绝对量纲场（res2 / seed 42，n = 5,882；陆 1,744 / 海 4,138）

| 量（mm/年） | 旧占位场 | **新事实** | 地球参考 |
|---|---|---|---|
| 全球均值（算术） | 1,424 | **1,106** | ~990 |
| 陆格 mean | 1,280 | **967** | ~715 |
| 海格 mean | 1,485 | **1,165** | ~1,120 |
| 陆格 **min** | 588.9 | **120.9** | 荒漠 < 100 |
| 陆格 P1 | 627.7 | **166.9** | — |
| 陆格 P5 | 701.9 | **275.4** | — |
| 陆格 P50 | 1,210.0 | **882.3** | — |
| 陆格 P95 | 2,178.0 | **1,934.4** | — |
| 陆格 max | 2,399.4 | **2,199.2** | — |
| 陆格干湿比 max/min | 4.1× | **18.2×** | ~100× |

★**旧场的致命处就是 `min = 588.9`**：它恰好等于 `0.40(副热带) × 2400 × 0.6(单一底板)`——
**世界上不存在干旱区**，`MiamiNpp(588.9) = 972` ⇒ 全陆生产力下限被钉在"相当肥沃"，
而 Legacy 陆格 min = 182.7 mm（真实荒漠）。**绝对量纲若只做线性缩放，等于把一个"没有荒漠的星球"盖章成绝对量。**

### 3.3 ★口径陷阱（本轮实测踩到，记录以免重犯）

1. **`MeanMm` 必须是算术平均**（H3 等积格）。再乘 `cos(lat)` = 面积雅可比**二次加权**（P4-1 已实测虚高 3.75 °C，同源陷阱）。
2. **"整体缩放" 与 "形状改变" 的水文后果完全不同**（见 §五）：`RunoffThreshold = meanPrecip × 阈值格数`
   使**整体缩放不改变 `IsRiver`**，但**形状改变一定改变**。做降水改动前必须先分清是哪一种。
3. **`R` 的量级由 `k` 中位数再归一化吸收** ⇒ "降水绝对量级是否敏感"**不能**用 `R` 的均值/中位数回答，
   必须看 **MiamiNpp 的饱和形状**与**限制侧比例**（§四-3）。
4. 测试里若写"平均纬度低于/高于"这类方向性断言，**先跑一次看方向**——
   本轮就写反过一次（干旱格 26.96° > 湿润格 3.94°，即"副热带干槽 vs ITCZ"），由实测纠正。

---

## §四 ④ provenance 对照：`Legacy PREC ↕ New PrecipitationFact`

### 4.1 Legacy 口径复原（源码级，非推断）

旧生成器 `scripts/Biome/ClimateGenerator.ComputePrecipitation`（已随 A 线清退 `6e7fc94`，从 `6e7fc94^` 复原）：

```
equatorBand = 1400·exp(−lat²/(2·12²))        polarFront = 550·exp(−(lat−62)²/(2·14²))
subCenter   = 26 + (tiltDeg−23.4)·0.12       subtropical = 1 − 0.85·exp(−(lat−subCenter)²/(2·9²))
baseP = (60 + equatorBand + polarFront) · subtropical
baseP *= 1 + (insolation−1)·0.30             baseP *= 1 + noise·0.12
海洋: baseP *= 1 + warm·str·0.25             陆地: baseP *= 1 + min(elevNorm,1)·0.4
风场: baseP *= 1 + maritime·0.55 ; *= 1 + clamp(windComp, −0.65, 0.65)
```

**关键特征**：① 含 **噪声/洋流/风场/倾角** 项（不可逐位复现、生产者已清退）；
② **海格输出 0**（实测 64.88% 的格为 0）；③ 海拔用**归一化** `elevNorm`。

Legacy 侧**消费方式**（源码级）：`CivEngine.BuildLayer1` →
`R[i] = MiamiNpp(Temp, Precip) × (WaterRich ? 1.5 : 1)`，且 `Temp ≤ −5.5 ⇒ R = 0`；
最后 **`k = TargetMedianDensity / median(R_raw)`、`R[i] *= k`**。

### 4.2 五项判据逐项

| 判据 | 结论 | 依据 |
|---|---|---|
| **分类定义** | **同** | 都是"逐格年降水总量" |
| **空间粒度** | **不同** | Legacy `n=40,962`（非等积球面网格）；新 = H3 **res4 等积格** |
| **计算时点** | **不同** | Legacy = 离线气候生成器（已删）；新 = `WorldGenPlanet.Regenerate()` 内的事实生产 |
| **缺失·边界语义** | **不同（最大差异之一）** | Legacy **海格填 0**（64.88% 的格）；新 = **每格有真值** |
| **数值范围·单位** | 单位**同**（mm/年）；范围**不同** | 陆 mean 2,460 → 967（**2.5×**）；陆 max 7,600 → 2,199 |

### 4.3 实测对照表

Legacy：`userdata/maps/map_seed42_n64_r128.mpa`（n = 40,962，陆 11,679 = 28.5%）。
新：res2/seed42（n = 5,882，陆 1,744 = 29.6%）。
⚠️ 两者是**不同世界线、不同分辨率**的对照（同 P4-1 ④ 的口径声明），不是"同一世界换实现"。

| 量 | Legacy `PREC` | 新 `PrecipitationFact` | 比 |
|---|---|---|---|
| 陆 mean | 2,460 | 967 | **0.39** |
| 陆 min | 183 | 121 | 0.66 |
| 陆 P1 | 424 | 167 | 0.39 |
| 陆 P5 | 545 | 275 | 0.50 |
| 陆 P25 | 1,237 | 518 | 0.42 |
| 陆 P50 | 2,417 | 882 | 0.37 |
| 陆 P75 | 3,500 | 1,359 | 0.39 |
| 陆 P95 | 4,853 | 1,934 | 0.40 |
| 陆 P99 | 5,596 | 2,146 | 0.38 |
| 陆 max | 7,600 | 2,199 | 0.29 |
| 海 mean | **241**（65% 为 0） | **1,165** | 4.8× |
| 全球 mean | 873（含 0 填充） | 1,106 | 1.27 |

**读法**：陆地上是**一整条均匀的 ~0.4 缩放**（不是尾部畸变），尾部略窄（0.29~0.66）；
海洋则是**语义级不同**（0 填充 vs 真值），使"全球均值"这个口径在两代之间几乎不可比。

### 4.4 ★ 判定 = `ADAPTER_REQUIRED`（自 `SEMANTICALLY_DIFFERENT` 升级）

**为什么不是 `EQUIVALENT`**：陆地质 2.5×、海格缺失语义不同、尾部宽度不同、粒度不同。
**为什么不是 `SEMANTICALLY_DIFFERENT`**：**同物理量、同单位**，且下游**同一套非线性**（`MiamiNpp` 降水侧）——
不是"两个不相干的量"。**为什么不是 `SOURCE_MISSING`**：生产者已存在（本轮交付）。
⇒ **`ADAPTER_REQUIRED`**。

★**适配项是什么（由实测决定，不是预设）**：

| 适配项 | 实测依据 | 处理位置 |
|---|---|---|
| **A1 换世界** | 两代是不同世界线，不是字段换算 | 桥落地时（`⑪`） |
| **A2 `R` 联合重标定** | 见 4.5：**中位数几乎不动（1260 → 1262）**，但**限制侧翻转** | **下一步**（用户已定顺序） |
| **A3 冰盖阈值复核** | 冰盖格占比 **22.14% → 6.31%**（新世界显著更宜居） | 同上（`├ 冰盖阈值复核`） |
| **A4 不复制旧分布** | 两代都不可逐位复现对方；旧值含噪声/洋流项 | ★**禁止**为对齐旧分布给新事实加噪声 |

### 4.5 ★ 检查 D：与温度联合后的 `MiamiNpp` 响应面（这是本轮最有信息量的实测）

`R_raw = MiamiNpp(T, P)`（剔冰盖；**不含** CivSim 的 `WaterRich ×1.5` 水因子，水因子是 CivSim 侧机制不是气候事实）。

| 量 | Legacy | 新事实 | 读法 |
|---|---|---|---|
| 冰盖格（T ≤ −5.5） | **22.14%** of land | **6.31%** | 旧世界更冷、可生存区更小 |
| **降水侧受限**（NppP < NppT） | **26.1%** | **81.3%** | ★**限制侧翻转**：旧世界"温度限制"，新世界"降水限制" |
| `NppT` mean / P50 | 1,497 / 1,532 | 1,843 / 1,987 | 新世界更暖 |
| `NppP` mean / P50 | 2,059 / 2,224 | 1,348 / 1,349 | 新世界更干（但在荒漠以外仍不构成约束） |
| `R_raw` P25 / **P50** / P75 | 852 / **1,260** / 1,815 | 772 / **1,262** / 1,804 | ★**几乎重合** |
| `R_raw` P5 / P95 | 465 / 2,430 | 444 / 2,165 | 差 < 12% |
| `R_raw` max | 2,647 | 2,303 | 新世界湿端略低 |
| `R_raw` mean | 1,343 | 1,262 | −6% |
| `R_raw` < 300（饥荒级） | 0.0% | 0.8% | 无"大面积饥荒" |
| `R_raw` > 2000（近饱和） | 18.6% | 13.9% | 无"大面积过肥沃"（**反比旧世界更少**） |

**检查 D 的四问逐条回答**（用户点名）：

| 问 | 实测结论 |
|---|---|
| 大面积饥荒？ | **否**。`R_raw < 300` 仅 **0.8%**（且集中在副热带深内陆） |
| 过肥沃？ | **否，且比 Legacy 更少**（`>2000`：13.9% vs 18.6%） |
| 原本可生存区突然消失？ | **否，方向相反**：冰盖格 **22.14% → 6.31%**，可生存区**变大** |
| 热湿组合异常放大？ | **否**。最热湿格（20~30 °C × ≥2000 mm，57 格）均值 2,256，被 `nppP` 饱和封顶（max 2,303），无放大 |
| 高寒高降水 / 低温干旱失真？ | **否**。`−6 °C 以下 × ≥2000 mm` = **0 格**（新世界无"冷湿"陆格）；`−6~0 °C × ≥1000 mm` = 29 格均值 528；`0~10 °C × <200 mm` = 7 格均值 329 ✅ 全部按物理正确地低 |

**★ 结论（供下一步直接引用）**：`R` 的**整体量级不需要重标定**（P50 差 0.2%），
需要重标定的是 **空间格局**（限制侧从温度翻到降水）与 **冰盖阈值**。
**这恰好推翻了"降水绝对量级一定高度敏感"的直觉预设**——而这是实测给出的，不是推理给出的。

> ★★**后续更正（2026-10-06 · 独立佐证 A/B · 12 seed × res2/res4）——本节上述读数与结论须按此块阅读**
>
> **① 链路口径**：本节全部读数来自**简化链路**（`MountainSkeleton(baseSigmaKm: 520f)`、**无** `VolcanoField`、
> 区域粒度 8e6），**不是** `WorldGenPlanet` 的生产链路（区域 **4e6**、sigma **160**、**含**火山）。
> 口径自检已证明：定义无误（`test` 链路逐位复现本节 6.31 / 81.3 / 1262 / 0.8 / 13.89），
> **差异 100% 来自链路**。
>
> **② 三条锚的跨 seed 复核**：
> | 本节锚 | 生产链路 12 seed | 判定 |
> |---|---|---|
> | 限制侧 26.1% → **81.3%** | 68.98% ~ 96.29%（**12/12 都是降水受限**） | ✅ **稳健** |
> | 冰盖 22.14% → **6.31%** | **3.97% ~ 25.09%**（均值 **14.0%**），与 Legacy **重叠** | ❌ **不成立**；6.31% 是 seed42 偏暖端样本 |
> | `R_raw` P50 **1260 → 1262** | **854 ~ 1586** | ⚠️ 见 ③ |
>
> **③ "中位数归一化后量级稳定"是代数恒等式**：`k = TMD/med(R_raw)`、`R = R_raw·k`
> ⇒ `med(R) ≡ TargetMedianDensity`，对**任何**输入恒成立（实测 24/24 组 `|med_prod×k − 0.1| ≤ 1.4E-17`）。
> ⇒ **不能**用"量级稳定"证明"不需重标定"；`k` 吸收不掉的是**形状**：
> `R_raw` P95/P5 跨 seed **3.92~6.34**、`R>2000%` **6.22~18.43**、陆地 P50 **580~996**。
>
> **④ 冰盖漂移已归因**：`r(ice%land, 陆 ≥60° 占比) = 0.968/0.958`（res2/res4），
> `ice/高纬陆 = 1.28/1.25`，同 world 换 res 只差 ~7% ⇒ **大陆布局的自然方差**，非 `MiamiNpp`/温度公式缺陷。
>
> **⑤ 修正后的结论**：`R` 的**量级由 `k` 定义掉**（恒等式）；**可重标定的是「形状（尾部宽度）+ 冰盖阈值」**；
> 且**未达进入重标定的门槛**（三件事未全部跨 seed 成立）。
> 详见 `docs/审查报告-P4-2-独立佐证AB.md`。

---

## §五 新债务 / 漂移（四问模板）

### 5.1 水文漂移（实测：同最终地形，只换降水场）

| 量 | 旧场 | 新场 | 说明 |
|---|---|---|---|
| `meanPrecip`（全体格） | 1,424 | 1,106 | 0.78 |
| `RunoffThreshold` | 56,970 | 44,240 | **比 = 0.777 ≡ meanPrecip 比** ⇒ ★**自锚定机制精确成立** |
| 河格数 | 33 | 27 | res2 河流本就稀少（D-11 已知尺度适配缺陷） |
| 河格集合 Jaccard | — | **76.5%** | ⇒ **23.5% 的河格发生翻转** |

★**归因**：翻转**不是量级造成的**（阈值精确随 `meanPrecip` 缩放，自锚定把整体缩放消掉），
**是形状造成的**（陆地相对海洋变干 ⇒ 陆地径流占比下降）。这条必须写进下一步的验收口径。

### 5.2 未改动的项（避免范围膨胀，永久原则 11）

- `CoastDecayKm = 450` **不动**（几何 B 既有冻结值；改它会连带移动水文）。
- `RiverThresholdAreaKm2 = 70,800` / `RiverThresholdCells = 40` **不动**。
- `LakeState` 的 `VolumeKm3` 与绝对 mm **线性相关** ⇒ 新场的湖水量会变，但**湖的判定结构未改**。

---

## §六 与 Phase 4 其余项的关系 / 下一步

```
P4-1 Temperature ✅ ──┐
                      ├──► Temperature + Precipitation ──► R / MiamiNpp 联合重标定（← 下一步）
P4-2 Precipitation ✅ ┘                                        ├── 冰盖阈值复核（22.14% → 6.31%）
                                                               └── 生计响应复核（限制侧 26% → 81%）
                                                                        │
                                                                        ▼
                                                          P4-3 Biome（上游气候事实的消费者）
```

★**边界**（用户明确）：**P4-2 完成后不要顺手做 Biome**。先把 `Temperature + Precipitation`
作为**一个完整的气候事实基础**，联合完成 `R` 的重标定与 provenance 复核，再进 P4-3 ——
否则 Biome 会在气候事实还没稳的时候被建一次，然后返工。

**已可交付给下一步的三条实测锚**（★**已由 2026-10-06 独立佐证 A/B 复核修正**，见下）：
1. `R` 中位数不动（1260 → 1262）⇒ ~~不要做整体缩放~~ ⇒ ★**改为**：量级由 `k` **定义**掉（恒等式），可重标定的是**形状**。
2. 限制侧翻转（26.1% → 81.3%）⇒ 重标定的对象是**空间格局**。★**A/B 复核：稳健**（生产链路 12/12 seed ∈ [69.0%, 96.3%]）。
3. 冰盖 22.14% → 6.31% ⇒ ~~冰盖阈值必须复核~~ ⇒ ★**改为**：生产链路冰盖 ∈ [3.97%, 25.09%]（均值 14.0%），
   与 Legacy **重叠**，6.31% 是 seed42 偏暖端样本；阈值复核须按**分布**评估。

⇒ **门槛判定：未达进入 `R`/`MiamiNpp` 重标定的门槛**（详见 `docs/审查报告-P4-2-独立佐证AB.md` §七）。

---

## §七 边界声明（★ 供后续架构决策引用）

本轮**只做 WorldGen 事实生产**：

- **未接 CivSim**：`World.CivSim` 零改动；`CivSim_DoesNotReferenceWorldGen` 契约仍绿。
- **未写 Bridge**：`WorldGen → CivSim` 桥仍不存在（`BLOCKED` 状态不变）。
- **未实现 `SubsistenceReadout`**。
- **未重标定 `R` / `MiamiNpp`**（用户明确顺序：`P4-1 → P4-2 → 然后` 才联合重标定）。
- **未做 Biome / Soil / Seasonal Climate**。
- **未动 O1–O4**（存档格式 / hop→km 数值 / `HumanInputGrid` namespace / 复用 `Ball(res4)`）。
- ★**未把新事实做成旧字段的复制品**：两代在 4.2 的五项判据上有 4 项不同；差异**被记录**，不被抹平。

---

## 附 · 关键文件

| 文件 | 角色 |
|---|---|
| `scripts/WorldGen/Simulation/PrecipitationModel.cs` | **降水事实**（本轮交付） |
| `scripts/WorldGen/Simulation/TemperatureModel.cs` | 温度事实（P4-1） |
| `scripts/WorldGen/Composition/WorldGenPlanet.cs` | 接线（`Final → Temperature → Precipitation → Rivers`，读数 `meanT=` / `meanP=`） |
| `scripts/WorldGen/Simulation/RiverNetwork.cs` | 唯一径流消费者（**自锚定阈值**在此） |
| `scripts/WorldGen/Simulation/LakeState.cs` | 水量平衡消费者（`VolumeKm3` 与绝对 mm 线性） |
| `tests/World.Tests/PrecipitationModelTests.cs` | 17 条护栏 |
| `tests/PerfBench/Program.cs` | **纯诊断**（D-11 判据矩阵 / C1′ 反事实；本轮同步 2 处） |
| `scratch/p4_2_legacy_r.py` | Legacy 侧 `R` 同构复算（只读取证） |
| `scratch/inspect_mpa.py` | `.mpa` 只读段表解析 |
| `docs/审查报告-P4-1-温度事实与provenance对照.md` | 上游（⑦） |
| `docs/decisions/0005-natural-input-bridge.md` | ADR（§⑦ / §⑧） |
