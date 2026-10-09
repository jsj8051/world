# ADR-0005：自然输入桥（World → Human Input Bridge）与人类演化层边界

日期：2026-10-06
状态：**已采纳（方向 + 边界条款 + 可行性裁决 + 载体契约冻结 + Phase 4 推进中）**——C-6 固化 §边界条款；④（逐项语义等价性）/ ⑤（可行性裁决 = `BLOCKED`）/ ⑥（载体契约 = **H3 res4 冻结**，见 §不变量 I1–I5）/ ⑦ **P4-1 温度事实**（见 §⑦）/ ⑧ **P4-2 降水事实**（见 §⑧）/ ⑧b **独立佐证 A/B**（见 §⑧ 更正块；**门槛未达**）/ ⑨ **路线 A：R 形状与冰盖分布评估**（见 §⑨；**判定 = 不需要改形状**）/ ⑩ **P4-3a Biome 语义取证**（见 §⑩）/ ⑪ **P4-5 季节气候 v1.1 可达性修复**（见 §⑪；**6/24 恒 0 幽灵接口已排除**，含冻结条款 4/5）已落档。
★**依赖倒置已冻结（2026-10-07）**：`Seasonal Climate` 属于 `Biome` 的**上游事实生产阶段**，**不得**在 Biome 内部从年均气候反推月度/季节事实；`P4-5 Seasonal Climate` 已**升格为 P4-3 的硬前置**。
**状态（2026-10-07 二次反转）：`P4-5 Seasonal Climate` ✅ v1.1 已完成（§⑪），但路线已修正（§⑫）**。
★★**P4-5 的第一步由"继续实现 `SeasonalClimateModel`"改为「**先冻结 Climate 架构契约**」**（用户拍板 2026-10-07）：
温度/降水/洋流/蒸发/季节之间存在**耦合**；若继续单独实现季节模型，会造出 `Annual T/P → 人工生成月度 T/P`，
将来加入洋流与水汽输送时需**重写**。`SeasonalClimateModel` 当前的 `P(m) = (P/12)(1+S·cos…)` **就是这种反推形态**。
⇒ **新主线 = `⑫ Climate Architecture Contract`（P4-5a，纯设计）→ P4-5b Solar Forcing + 海陆热响应 → 水循环 → 洋流输送 → 季节气候 → Biome**。
**状态（2026-10-07）：`⑫ P4-5a 契约 ✅ → ⑭ P4-5b ✅ 已冻结 → ⑯ P4-5c 耦合 Solver 骨架 ✅ 已冻结（用户终审通过）`**（多分量 `ClimateState` + 迭代骨架 + 最小链 T→蒸发→水汽→降水；**568 PASS**，四条硬验收一次全绿；Headless `monMean=13.1C monAmpT=9.3C` **基座读数逐字未变** + `wv=18.7mm prec=749mm/年`；见 §⑯）。★**P4-5c 终审三冻结物**：F1 **568 PASS = 新 Climate 测试基线**（后续不得因洋流热输送重解释 P4-5b 温度语义）｜F2 `wv/prec` = **机制产物非目标值**（地球尺度对标归 P4-5e）｜F3 **26.3 s = 性能基线**（P4-5d 只记"基线→增量"，不重猜总成本）；详见报告 §六。
**⑮ Ocean Circulation 架构契约 ✅（2026-10-07 纯设计）——洋流与 P4-5c 并行推进，方向反转 ⑫ 条款 7（见 §⑮）**；
**★P4-5d 洋流热输送 ✅ 已冻结（2026-10-07 用户终审：不再加机制，直接封存）**：D2 `OceanCurrentField`（并行会话，Stommel SOR）+ D3 接线（第二个 tendency 接入 `CoupledClimateSolver`）——海格邻接通量逐对反对称 ⇒ 零和重分配**结构性**成立（离散算子性质，非事后补偿）；`oceanHeatGain` 默认 0 = **逐位退化 P4-5c**；`dToc=+0.0000C`（全球年均守恒，条款 10 强形式）+ 陆格温度逐位不动；Headless `cur=1.3km/月` 全程 14488 ms（增量 ≈ 0，无性能回归）；见 §⑮ D3 补记与 `docs/审查报告-P4-5d-洋流热输送接线.md` §七。★**测试计数归属**：P4-5d 本轮相关 **600/600 通过**；全量 600/602，剩余 2 FAIL = **WindField ⑰ 开发中模板对照**（不计入 P4-5d 结论）；
下一步 = **⑱ P4-5e 对齐验收**（用户排定五步：① O-C5 水分量去留·最优先【先定死水分量所有权，防重复归属】→ ② O-O2 洋流速率地球锚【速率锚+输运效应量级，≠ 逐格匹配地球】→ ③ PrecipitationModel 形状对照【禁反向校准】→ ④ 跨 seed → ⑤ 全量 Headless 收口；见 Climate 契约 §十三）。**★① O-C5 所有权裁决已落（2026-10-07，纯取证零改动）**：降水生产事实唯一源（现役）= `PrecipitationModel.AnnualMm`（消费者白名单 = Rivers/H3Hydrology/LakeState/SeasonalClimateModel/渲染）；`ClimateState.WaterVapor/Precipitation` = Solver 机制产物/诊断态（零生产消费者 = 登记状态；WaterVapor 保留为未来平流插槽）；**禁双源/禁混读**，换源 = 一次性（③④通过后），验收 = 既有结论不漂移（跨 seed）；报告 `docs/审查报告-P4-5e-O-C5-水分量所有权取证.md` R1–R5（含白名单契约钉子建议 N1–N3）。**R1 表述精化（用户）**：= "当前生产消费者唯一认可的降水事实源"，非"项目里只有一个降水数值"（ClimateState.Precipitation 仍是耦合 Solver 机制状态，只是无生产消费者）。**★② O-O2 取证已落（2026-10-07，零参数改动）**：三锚实测（分布 p50–p98 低地球内区带 ~5–15×、max 0.22 m/s 低强流核 ~3–10×、对比度 337 结构健康；输运 dTmx 0.24 °C 同向偏弱）；**标定对象建议 = 主 τ₀（2–4×）＋ 辅 r（≤2×）**，H/归一化不动；取值待用户拍板；报告 `docs/取证报告-P4-5e-O-O2-洋流速率地球锚.md`。
`P4-3b Biome 事实生产` ⏳ **暂缓**（等 P4-5e 对齐验收）；`SeasonalClimateModel` 过渡期**只修 bug、不新增物理机制**。
后续 `P4-4 Soil` → ⑬ Bridge。
⑬ **Bridge 实现设计**（见 §⑬；2026-10-07 纯设计 · 零代码）：四层形态 + **载体三项禁令**（不重新生成事实 / 不反推月度 / 不承载 Legacy-only）+ ★R2 深度传递扫描 ⇒ `HumanInputGrid` 不得置于 `World.WorldGen.*` + 用户拍板 **B1 不提前**（严守 `Phase 4 → B0 → B1 → B2 → B3 → B4`）+ **Biome/Soil 升格为 B2 重点审计项**（逐类映射 + 逐项验证，验类别形状与空间分布）+ Month* 走 **9 标量 → 4 派生量**（不建 12 月数组）+ 新增开放项 **O5**（Jaccard 阈值）。
关联：

- `docs/审查报告-CivSim人类演化层取证.md`（本 ADR 的直接依据，2026-10-06）
- `docs/审查报告-C4-人类演化层接线审查.md`（**§边界条款 / §迁移对照基准的直接证据**，2026-10-06）
- `docs/审查报告-CivSim自然输入Provenance.md`（自然输入逐项来源对账）
- `docs/审查报告-WorldHumanInputBridge-语义等价性取证.md`（**④** 逐项语义等价性 + 五标签判定）
- `docs/裁决-Bridge可行性裁决.md`（**⑤** 可行性裁决 + H3/Legacy 载体取证 + Phase 4 任务清单）
- `docs/裁决-HumanInputGrid载体契约.md`（**⑥** 载体契约 + 不变量 I1–I5）
- `docs/审查报告-P4-1-温度事实与provenance对照.md`（**⑦ P4-1** 温度事实落地 + `Temp` 判定 `ADAPTER_REQUIRED`）
- `docs/审查报告-P4-2-降水事实与provenance对照.md`（**⑧ P4-2** 降水事实绝对量纲化 + `Precip` 判定 `ADAPTER_REQUIRED` + `R`/`MiamiNpp` 联合重标定前置取证）
- `docs/审查报告-P4-2-独立佐证AB.md`（**⑧b** 12 seed × res2/res4 门槛验证；**判定 = 未达门槛**；口径更正 + 冰盖漂移归因 + `med(R)≡TMD` 恒等式）
- `docs/审查报告-R形状与冰盖分布评估.md`（**⑨ 路线 A** R 响应形状 + 冰盖分布 + 阈值敏感性；**判定 = 不需要改形状**；含 §七 新原则 14）
- `docs/裁决-P4-3-Biome事实定义.md`（**⑩ P4-3a** Biome 语义契约：①分类语义 ②输入来源 ③provenance 五项判据 ④WildCrops/WildLivestock 恢复条件；**24 类逐一登记**；★依赖倒置拍板）
- `docs/裁决-P4-5-季节气候最小充分事实集.md`（**P4-5** 从 24 类判据反推的最小充分事实集 = **9 标量/格**，非 24 条月度数组）
- `docs/裁决-Climate架构契约.md`（**⑫ P4-5a** Climate 架构契约（**纯设计**）：`ClimateForcing`/`ClimateParameters`/`ClimateState`/`ClimateSolver` 四概念 + 五问裁决 + 耦合图 + 阶段 P4-5a..f + 冻结条款 6–9 + 开放项 O-C1..6）
- `docs/审查报告-P4-5c-耦合Solver骨架与最小水循环.md`（**⑯ P4-5c** 耦合 Solver 骨架 + 最小水循环：多分量 `ClimateState`（`MonthlyField` 通用槽位）+ 迭代骨架 `State₀→tendencies→State₁→converged?` + 最小链 T→蒸发→水汽→降水（通量形式扩散严格守恒、库项解析积分）；**四条硬验收一次全绿**（依赖/收敛/退化/水量边界）+ 条款 10 执行钉；陆格蒸发 ≡ 0 = 登记的能力边界）
- `docs/审查报告-P4-5b-太阳强迫与海陆热响应.md`（**⑭ P4-5b** 太阳强迫 + 海陆热响应：`ClimateForcing`（TOA）/`ClimateParameters`（Gain 0.0786 / τ陆 1.0 / τ海 8.0，标定自地球站点）/`MonthlyTemperature`（ClimateState 第一批分量）/`ThermalResponseSolver`（闭式周期解）；**异常驱动** ⇒ 年均严格不变、Gain=0 逐点退化；554 PASS + Headless `monMean≡meanT`、`monAmpT=9.3C`）
- `docs/裁决-OceanCirculation架构契约.md`（**⑮ Ocean Circulation 架构契约（2026-10-07，纯设计 · 零代码 · 与 P4-5c 并行）**：洋流生产方向**反转** ⑫ 条款 7 = `World Facts → Oceanography → Climate` 单向；新子层 `Oceanography`（`OceanRegions`/`OceanCurrentField`/`OceanCurrentModel`）+ **条款 12**（BasinGraph 水文语义分离，不得复用）/**条款 13**（洋流单向）/**条款 14**（稳态场无时间积分）；v1 不做真实海洋模拟；阶段 D0–D3，D1/D2 与 P4-5c 零依赖并行）
- `docs/审查报告-HumanSimulation-v0.1与CivSim重叠判定.md`（85% 重叠判定 + T90/T91 实测）
- `docs/阶段6设计-古典时代机制.md`（国家 = 制度实体，已落地）

---

## 背景

《Human Simulation v0.1》提案曾规划一条 **Resource → Production → Population → Society → State** 的新建路线
（`PopulationGroup` / `SubsistenceProfile` / `Settlement` / `Society` / `Polity` 等新对象）。

取证（`审查报告-CivSim人类演化层取证.md`）证明：**这条路线的 9 个环节里 8 个已由现有 `scripts/CivSim/` 实现**，
且形态**强于提案**（等边际闭式 water-filling > 份额×惯性启发式）。现有内核已是一台**跑通的四级演化机**：

```
band → tribe → chiefdom → state        （ConceptRegistry 四配方 → 22 机制，Order 0-80）
```

同时取证确认了**第二个、也是真正的边界问题**：

- `CivSim` 的气候/群系类输入（`Temp` / `Precip` / `Biome` / `MonthTemp` / `MonthPrecip` / `SoilLevel`）
  **全部来自遗留 `.mpa` 快照**，新 WorldGen 线**无任何重建路径**；
- 而 `R[]`（整个模拟的量级之根）= `MiamiNpp(Temp, Precip) × 水因子` ⇒ **直接挂在 Legacy 输入上**。
- 新线已有等价物（`HeightM` / `Precipitation.AnnualMm` / `LakeState` / `RiverNetwork`）**均未接线**。

⇒ 当前形态是 **Legacy-Data-Driven 模拟**，不是 **New-WorldGen-Driven 模拟**。

**这一条边界（"地球怎么接到人类社会前面"）才是当前真正待解的问题，不是"人类社会应该有哪些系统"。**

---

## 决策

### 1. 人类演化层**不再新建**（CivSim = 已完成的演化内核）

**明确不建**：`PopulationGroup` / `SubsistenceProfile` / `Settlement` / `Society` / `Polity` 等提案新对象。
它们已分别由下列既有结构承担：

| 提案对象 | 现有实现 |
|---|---|
| `PopulationGroup` | `Polity`（一格一实体，含 `P`/`Stocks`/科技/文化/宗教/领地） |
| `PopulationInventory` / `FoodStock` | `Polity.Stocks`（随身池）+ `Habitation.Stocks`（粮仓）+ `CommodityTable` 6 商品 |
| `SubsistenceProfile` | **涌现**（water-filling 的 `n_i → F_i` 分量），缺的只是"读出" |
| `ProductionProcess` / `Labor` 池 | `AllocateAndProduce`（等边际闭式，LF 两档） |
| `ProductionSite = MobileGroupRange` | 影响力场归属格集（加权 Voronoi）+ 领地 |
| `Settlement` | `Habitation`（职能条件系统：Admin/Market/Ritual） |
| `Society` | `Chiefdom`（庇护网络）/ 文化群份额场 |
| 政治实体 | `StateEntity`（制度实体：国库/稳定度/合法性/君主） |

**理由**：项目第 1 条永久原则（防膨胀三问）第三问"现在有真实消费者吗"——这些对象**没有尚未被满足的消费者**。

### 2. 独立立项：**World → Human Input Bridge**

**名称**：`World → Human Input Bridge`（**不叫** Climate / Riparian / Fish）。
它是一个**独立项目**，职责不是"补某一个缺口"，而是：

> 让 `Geography` / `Hydrology` / `Climate` / `Ecology` / `Resources`
> **成为 Human Simulation 的自然输入**。

**非目标（明确不做）**：

- 不重写气候层来"一次性解决"；
- 不把 Legacy `.mpa` 当成最终方案（但也**不在本 ADR 内废除**它）；
- 不在桥未定之前改动 `CivSim` 的任何生产公式。

### 3. 逐层迁移（四阶段）——不要一次重建整个气候层

```
Phase 1   WorldGen Geography        → CivSim
Phase 2   WorldGen Hydrology        → CivSim
Phase 3   WorldGen Precipitation    → CivSim
Phase 4   Temperature → Biome → Soil → Resource Potential
```

最终形态：

```
WorldGen → Natural Environment → Resource Potential → CivSim → Population → Society → State → History
```

**每阶段的前置问题（统一模板，逐阶段回答）**：

1. 新线这一层**是否真的产出了**与 Legacy 语义等价的场？（否则不是"接线"而是"换世界"）
2. 接上去后 `R[]` 的量级是否漂移？如何重新标定？
3. 接线是否引入新的"不可重建输入"？（禁止把债务换了个位置）
4. 验收口径 = 什么？（不得只写"跑通了"）

### 4. 冻结项（维持现状，不开工）

| 项 | 状态 | 判据 |
|---|---|---|
| `ResourceStock` + 再生 + 扣减 | **冻结** | T90 实测档位 A/B，从未在任何区域/分位数越过 1 ⇒ 无消费者 |
| `fish` 商品注册 | **冻结** → Future Economy Integration | 触存档格式（`Version` 17→18 + `Peek` 183→187）；无"跨 tick/跨主体"消费者 |
| ② Riparian（WorldGen 群系分类器） | **取消，不做** | 不是"缺一条分支"，而是被清退的整条气候链留下的幽灵接口 |
| 物理河宽 / 河岸 / flow animation / LOD2-3 | 冻结 | River v2.0 契约 |

### 5. 观测增强：`SubsistenceReadout`（**观测能力，非模拟能力**）

- **性质**：纯派生读出——把 `AllocateAndProduce` **已经算出**的
  `fHunt / fHerd / fFarm / fBerry / fFish` 归一化成份额视图（`Σ ≈ 1`）。
- **红线**：**不入档 / 不进决策 / 不参与任何公式**（不替换 water-filling）。
- **实现注意**：`FHuntLast` 已含浆果与水产 ⇒ 猎物分量 = `FHuntLast − FBerryLast − FFishLast`
  （与 `CommodityTable.meat` 同式，否则鱼被双计）。
- **价值**：回答"这个部落究竟靠什么活"；为未来地图观察层提供事实；使"生计 Σ≈1"可测。
- **边界（sink-only）**：见 §B3——判读层是**汇（sink）**不是**源（source）**；
  只能**消费**既有世界事实与人类模拟事实，**不得生产 / 回写 / 定义环境事实**。
- **变更纪律**：独立变更（第 13 条原则），单独 commit。

### 6. 前置工程债（不阻塞，但记录在案）

| # | 债 | 说明 |
|---|---|---|
| 1 | **T04 持久化守卫风险** | `TerritoryLastRebuild` / `ChiefdomLastEval` / `AbsorptionLastEval` 不入档，T04 靠**人工对齐** + 只续跑 20 tick ⇒ **PASS ≠ 全字段已入档**。**任何"新增状态字段"之前必须先收敛此债** |
| 2 | `scripts/verify.sh` 恒 ❌ | 2 个场景文件 + 1 个地图路径不存在 ⇒ 唯一"一键回归"失效（独立 chore commit） |
| 3 | `CivSimContext.FOf()` / `ColdFloor()` 死代码 | 零调用者；且只写 `FHunt/FHerd/FFarm`，**不写 `FBerry/FFish`** ⇒ 复活即新双计源 |
| 4 | `Habitation.Camp` 形态行为未接线 | `KindOf()` 派生已有，随迁/拆营待 nomadic 概念 |
| 5 | 宗教链未完成 | 祖先 → 多神 / 多神 → 一神 |
| 6 | 文档漂移 | `OriginModel` 注释 "P=100" vs 代码 `OriginPop = 10f` |
| 7 | CivSim 无游戏内生产入口 | 主场景不跑 CivSim，只经诊断 `GameGrid.FromMapData(.mpa)` |

**无已知 FAIL**：全量单元 **510 PASS**（2026-10-06：R2/R3 两条契约后 496→498；⑦ P4-1 后 498→510）/ 地图 T 套件 79 PASS。

---

## 不变量（Invariants · 冻结 · 2026-10-06）

> **性质**：这是本 ADR 的**正式不变量**——不是"约定"，是**改了必须重走架构决策**的冻结项。
> 依据：`docs/裁决-HumanInputGrid载体契约.md`（⑥ 载体契约）。
> **意图**：任何"为了性能把 CivSim 偷偷降到 res3/res2"或"悄悄引入第二套空间索引"的改动，
> 都必须**显式解冻本表**，而不是在代码里改一个常量。

| # | 不变量 | 可执行钉子 |
|---|---|---|
| **I1** | **CivSim 基础空间粒度 = `WorldGenPlanet.ProductionRes` = H3 res4** | 世界档位侧 ✅ `SpatialScaleTests.ProductionRes_IsRes4_Frozen`（反射读原始常量，`IsLiteral` 且 `== 4`）；CivSim 侧 ⏳ 随 `HumanInputGrid` 补 |
| **I2** | 基础空间身份 = **H3 res4 CellId**；dense index 仅作**运行时索引**，不得作为世界身份 | ⏳ 随 `HumanInputGrid` 补 |
| **I3** | 基础邻接 = **H3 res4 六邻接**；不重建 Legacy n=64 邻接 | ⏳ 随 `HumanInputGrid` 补 |
| **I4** | **hop 不表示固定物理距离**；影响半径类参数以 **km**（几何尺度 B）表达 | ⏳ 随重标定补 |
| **I5** | **区域不是 Cell**：`Province`/`Country`/`Polity`/`Civilization` 只能引用 res4 cell 集合，**不得成为基础空间索引** | ⏳ 随 `HumanInputGrid` 补 |

**依据链**：I1/I4 是项目永久原则 **3**（生产分辨率不得被子系统反向绑架）与 **2**（三类参数：hop 是拓扑量、km 是几何量）
在本 ADR 的落地；I5 保证"基础空间索引唯一"，防止区域层反向定义基础单元。

---

## 边界条款（C-6 冻结 · 2026-10-06）

> **来源**：`docs/审查报告-C4-人类演化层接线审查.md`（已**实测验证**）。
> 本节是**结果**（边界是什么）；对应的**方向守卫**（不靠人记、靠测试拦）见 §执行契约。
> **性质**：正式固化，取代"我们口头说过"。

### B1. `World.CivSim` 是**领域消费者**，不是世界事实生产者

- `World.CivSim`（= 人类演化层，`scripts/CivSim/`）**消费世界、不生成世界、不拥有世界事实**；
  它在既有架构里的正式身份已由契约 `RetainedDomainConsumers_OnlyDependOnAllowedNamespaces` 钉死。
- 代码里**没有**"人类演化层 = 世界事实层"这个概念（C-4 §1.2）——"人类演化层"是本轮对话引入的**称呼**，
  指的就是 `World.CivSim`。
- **推论**：不要把它当"世界事实层"设计。它**不是** `Temperature` / `Biome` / `Soil` / `Resource Potential`
  / `Ecology` 的归属地。**任何"环境/生态事实"都不应落在它（或 `World.Domain`）里。**

### B2. 当前**不存在** `WorldGen → CivSim` 桥——这是**迁移面的缺口**，不是允许 CivSim 自行生产事实的理由

- **实测**：`CivSim` 的输入全部来自遗留 `.mpa`（`GameGrid`）；对 `FinalGeography` / `FinalSpatialIndex`
  的**输入量 = 0**；`scripts/WorldGen/Final/` 之外的仓内消费者 **= 0（单测除外）**。
- ⇒ 桥**尚不存在，连桩都没有**。这是**已知状态（Known State），不是缺陷**——
  但同样**不是**"就地补事实 / 私下接一根线"的许可证。
- **推论（桥的设计纪律）**：在桥正式设计（§3 的 Phase 1–4 逐层迁移）之前，**不允许出现私接消费者**。
  任何把 `Final*` 事实接进 `World.CivSim` / `World.Domain` / `Render` / `UI` / 其他外部层的改动，
  都必须**显式修改 §执行契约的白名单**并留下决策记录——白名单就是"必须被显式改的表"。
- **反面表述**：桥的**正确形态**是 §2 定义的 `World → Human Input Bridge`（让 WorldGen 的
  `Geography / Hydrology / Climate / Ecology / Resources` **成为** HumanSim 的自然输入），
  **不是**"让 CivSim 自己长出气候/生态"。

### B3. 判读层是 **sink**，不是 **source**（R5 · 只钉方向）

> **`SubsistenceReadout` 属于判读/消费侧，只能消费既有世界事实与人类模拟事实；
> 不得生产、回写或定义环境事实。**

- 判读层（`SubsistenceReadout` 及同类）是**汇（sink）**，不是**源（source）**：
  它**可以读取**环境事实与人类演化事实，但**不得成为这些事实的生产者**。
- 对应既有原则：**事实只能由事实层写；消费者只能读。**
- ★**本条只钉方向，本轮刻意保持克制**：
  - **不创建 `SubsistenceReadout.cs`**；
  - **不**接 `FinalGeography` / `FinalSpatialIndex` / 任何 CivSim 桥；
  - 实现与其契约形态（"判读层类型不得出现在任何事实生产类型的**被引用集**里"）
    随 §当前路线 ③ **落地时一并补**（C-4 §7 · R5）。
- 依据：C-4 §5.4。

### B4. 环境 / 生态事实**必须由 WorldGen 对应事实层生产**

- **现状（职责错位，不是"人类演化层承担了世界职责"）**：生态事实链事实上落在消费者侧——
  `World.Domain/WildCropsSystem`（`Suitability` / `ComputeLivestock`）+ `CivSimContext`
  （`WaterRich` / `PreyFrac` / `FishFrac` / `AlluvFactor` / `IrrigFactor`）；新线侧对应物**不存在**。
  其成因是两点：**生产者缺失**（如 `BiomeType.Riparian` 全仓只读不写 ⇒ 相关分支恒不命中）
  + **消费者越位**（为把模拟跑起来，CivSim 不得不自算世界事实）。
- **正确归属**：`Temperature → Biome → Soil → Resource Potential` 属 **WorldGen 事实层**，
  是 §3 **Phase 4 的交付物**（`Riparian` 属此类，已拍板**取消不做**——它不是"缺一条分支"，
  而是被清退的整条气候链留下的幽灵接口）。
- **禁止**：由人类演化层（或其紧邻的 `World.Domain`）补这些事实——
  否则构成 **事实层 ← 决策层 的反向依赖**，正是 B3 要挡的东西。
- 依据：C-4 §5。

---

## 迁移对照基准（D1–D4 · 保留，不是清理对象）

C-4 实测发现 5 项"重复计算"，其中 **D1–D4 四项是"两条世界线各自的等价能力"**，
**D5 单独归类**（职责错位，按 §B4 处理，不在本表）。

| # | 能力 | Legacy 侧（A） | 新线侧（B） | 性质 |
|---|---|---|---|---|
| **D1** | 海陆谓词 | `GameGrid.IsLandCell` = `Elev > 0` | `FinalGeography.FinalLand` = `HeightM > 0` | 同义、**不同实现、不同世界线**——口径必须分开记（混用即"换世界"） |
| **D2** | 陆地连通分量 | `OriginModel.ComputeContinents`（BFS flood fill） | `FinalGeography.FinalLandmassId` / `H3LandSeaProjector.LandmassId` | 同算法共 **3 份实现** |
| **D3** | 邻接表 | `GameGrid.BuildNeighbors`（`World.LogicGrid`） | `Ball.BuildNeighbors`（`World.H3Grid`） | 两套 H3 邻接（`GameMapArchive` 明确"邻接不存档"） |
| **D4** | 距离场 / 邻域查询 | `CivSimContext.BfsRadius`（O(n)/调用） | `FinalSpatialIndex`（预计算）+ `HydrologyRoutingSurface` | 新线已把距离场做成基础设施，CivSim 仍自驱 BFS |

**裁定**：

1. **不修复、不抽公共实现**（违反项目第 11 条：停止条件 / 不得范围膨胀）。
2. 它们**不是"重复实现"**，而是"**桥未接线**"的症状：新线算了一遍（`FinalLand` / `LandmassId` /
   邻接 / 距离场），旧线又算了一遍（`Elev` / `ComputeContinents` / `BuildNeighbors` / `BfsRadius`），
   而**两者互不相识**。
3. **保留为迁移阶段的"行为对照基准"**：逐层迁移（Phase 1–4）时，用 Legacy 侧结果作为新线侧行为的
   **对照口径**——"接上去后 `R[]` 是否漂移"这类问题，正是靠它来判。
4. **以后真正做 Bridge 时**，再逐项决定：哪些能力**迁移**（新线成为唯一实现）、
   哪些**适配**（保留双实现 + 显式转换）。**现在不预判。**

> D5（生态位派生职责错位）**不在此表**——它是 §B4，不是"等价能力"。

---

## ⑤ Bridge 可行性裁决（2026-10-06 · **BLOCKED**）

> **来源**：`docs/裁决-Bridge可行性裁决.md`（纯裁决，零代码改动）。
> **性质**：正式冻结"什么时候可以开工 Bridge"的前置条件——把 Bridge 从"马上编码"变为**有前置条件的架构阶段**。

### 裁决

```
Natural Input Bridge = BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED
```

**两个互相独立的阻断项**（任一未解则 Bridge 不可开工）：

| # | 阻断项 | 内容 |
|---|---|---|
| 1 | **MISSING WORLD FACTS** | 驱动模拟的 7 项自然输入在新线 `SOURCE_MISSING` / `SEMANTICALLY_DIFFERENT`（`Temp`/`Biome`/`SoilLevel`/`MonthTemp`/`MonthPrecip` 缺失、`Precip` 量纲不等价、`WildCrops`/`WildLivestock` 级联） |
| 2 | **CARRIER CONTRACT UNDECIDED** | CivSim 的空间载体契约未定（见下）；**`Elev` 虽判 `EQUIVALENT`，仍被此项阻断** |

### 载体契约（四项未决 · 须一起定）

- **Identity**：Legacy cell index = **稠密下标且入档**（`Polity.Cell`/`OriginCell`/`Habitation.Cell` + `LAND` 三数组），不变量 `CellPolities[i].Cell == i`；H3 id 稀疏但 `Ball` 已提供 dense↔id 映射。
  ⇒ **CivSim 是否迁 H3？identity 用 dense index 还是 H3 id？**
- **粒度**：`H3 res3`（41,162 格）≈ **Legacy n=64**（40,962 格）；**世界生产档 res4 = 7.0× 细化**。
  ⇒ **CivSim 跑 res3（需 res4→res3 聚合）还是 res4（成本×7）？**
- **hop 物理含义**：`reachKm = 13√A` —— Legacy/res3 **≈1,450 km** vs res4 **547 km（÷2.65）**；`minDistKm` 同理。
  ⇒ **`InfluenceRadius`/`ChiefReach`/`ColonizeRadius`/`OriginDistMin` 是否重标定？**（永久原则 2）

### 衍生结论（固化）

- **Bridge 的空间形态 = `WorldGen facts → HumanInputGrid`**（CivSim 侧的载体容器），
  **不是**字段级直连 `FinalHeight[cell] → GameGrid.Elev[cell]`——**"空间载体不等价时，字段语义等价仍不足以完成 Bridge"**。
- **不实现 `LakeLevel` Adapter**（载体未定 ⇒ 会做成一次性投影代码）。
- **Phase 4 前置任务（WorldGen 侧）**：`P4-1 Temperature` / `P4-2 Precipitation（绝对量纲）` /
  `P4-3 Biome` / `P4-4 Soil` / `P4-5 Seasonal Climate`；交付后 `WildCrops`/`WildLivestock` **自动恢复**。

---

## ⑥ HumanInputGrid 载体契约（2026-10-06 · **方向已定**）

> **来源**：`docs/裁决-HumanInputGrid载体契约.md`（纯裁决，零代码改动）。
> **范围**：⑤ 说"载体契约未定"；⑥ **只裁决链路**——**不再讨论"res3 还是 res4"**（已由生产原则决定）。

**链路（冻结）**：

```
H3 res4 CellId（世界身份） → dense runtime index（仅运行时） → CivSim topology / metric → 上层区域聚合
```

| 维度 | 裁决 |
|---|---|
| **① Identity** | 基础空间身份 = **H3 res4 CellId**；dense index 仅运行时（`CellIndexOf` 映射），**不得当世界身份** |
| **② Topology** | 基础邻接 = **H3 res4 六邻接**；**不重建** Legacy n=64 邻接；BFS/扩散/迁移/领地距离先在 res4 topology 上跑 |
| **③ Metric** | hop **只是拓扑距离**；`InfluenceRadius`/`ChiefReach`/`ColonizeRadius`/`OriginDistMin` 改为 **km**（如 `ColonizeRadiusKm`），消除 ⑤ 实测的 **2.65× 静默缩放**（`reachKm` 1,451→547 km） |
| **④ Resolution** | **`HumanInputGrid` = H3 res4 = `ProductionRes`** ⇒ 不再出现 `WorldGen=res4 / CivSim=res3` 的隐式尺度转换 |

**★ 附加契约（区域不是 Cell）**：任何 `Province` / `Country` / `Polity` / `Civilization`
只能是 **res4 CellId 的集合或引用**，**不得成为 CivSim 的基础空间索引**。
（实测一致：`GameGrid.Province`/`Country` = 逐格标签、`CellOwner` = 逐格归属 ⇒ 已是"区域 = cell 集合"。
★ 区分："Polity 的**领地**是 cell 集合" ≠ "`Polity.Cell` 变多格"——**"一格一实体"（驻格唯一）仍成立**。）

⇒ **正式不变量 I1–I5** 见 §不变量。**状态更新**：`BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED`
→ **`BLOCKED BY MISSING WORLD FACTS`**（载体契约已 DECIDED，唯一剩余阻断项 = Phase 4 事实生产）。

**开放子项（未拍板，不擅自定）**：O1 存档 cell 引用存 CellId 还是 dense index（存档格式）；
O2 hop→km 的**具体数值**（保留 Legacy 物理含义 vs 新世界重标定）；O3 `HumanInputGrid` 的归属命名空间与契约钉子
（若 `World.CivSim` 直引 `World.H3Grid` ⇒ 须显式改 §执行契约白名单）；O4 是否复用生产 `Ball(res4)` 实例。

---

## ⑦ Phase 4 · P4-1 温度事实（2026-10-06 · **已完成**）

> **来源**：`docs/审查报告-P4-1-温度事实与provenance对照.md`。
> **性质**：**WorldGen 事实生产**——不接 CivSim、不写 Bridge、不实现 `SubsistenceReadout`。

### 交付

```
WorldGen Climate └── TemperatureFact[H3 res4]         （scripts/WorldGen/Simulation/TemperatureModel.cs）
T[i] = ZonalAnnualC(|lat|)                            // ① 纬度（太阳辐射）
     − 6.5 °C/km × max(0, HeightM)/1000               // ② 海拔（ISA 物理递减率；海格自动不参与）
     + LandSeaDeltaC(|lat|) × ContinentalFrac(离岸)    // ③ 海陆·大陆性（符号随纬度翻转；仅陆格）
```

- **载体**：逐格数组 ⟷ `ball.CellIds`（**H3 CellId**）⟷ `ball.CellDirs`；**生产档 = `ProductionRes` = res4**（代码中**无任何 res 档位判断**）。
- **接线**：`WorldGenPlanet.Regenerate()`：`Final → **Temperature** → Precipitation → Rivers`；并加判读读数 `meanT=`。
- **单位**：°C（区间量纲 ⇒ 三项**加法**组合）⇒ 可直接与 `Temp ≤ −5.5 °C ⇒ R = 0`（冰盖）判据比较。
- **边界/缺失**：**每格都有温度**（无 −1/null）；海格 = 海表温度；**无 NaN**（Legacy 极点 `Pow(负底,1.1)` 曾 NaN 后被抹成 0 °C）。
- **退化解**：`ContinentalFrac(distToCoast = 1) ≡ 0` ⇒ 全陆临海世界精确退化为"无大陆性的纯海洋性世界"。
- **尺度**：`ContinentalityScaleKm = 1200` 是**几何尺度 B**（km）；hop→km 走 `√3 × CellEdgeKm`（随 res 变）——**非** `hop × 常数`。
- **测试**：`TemperatureModelTests` 12 条；全量单元 **510 PASS / 0 FAIL**（原 498，+12）。
  `TemperatureModel` 已登记进 `ArchitectureContractTests.NewWorldLineTypes`（防漏扫）。

### ★ 判定：`Temp` **`SOURCE_MISSING` → `ADAPTER_REQUIRED`**

**不是 `EQUIVALENT`**（三项实质分歧）：① Legacy 含 ±7 °C **随机项** + ±5 °C **洋流项**（后者生产者已随 A 线清退）
⇒ 新事实不可能也不应逐格复刻；② 海拔口径不同（`elevNorm × 10 km` vs **真实米**，实测冷尾 **−62.76 vs −29.54**）；
③ 纬度曲线锚点不同（`52·cos^1.1 − 22` = +30/−22 vs LUT +27/−30）。

**但也不是"量纲不可比"**（与 `Precip` 的区别）：**同一个物理量、同一单位**；
且**冰盖阈值两侧都可达**（新 **11.4%** / Legacy **11.9%** 格 ≤ −5.5 °C）⇒ 该判据在新世界上**仍有区分力**。

**适配层（须显式设计，当前不存在）**：**A1 换世界** + **A2 与 P4-2 一起重标定 `R`**（`MiamiNpp` 的输出分布会变，
且 `Precip` 同期也在换 ⇒ 不能分开标定）+ **A3 冰盖阈值复核**（不预支结论）+
**A4 承认不可复刻的成分**（不得为对齐旧分布而给新事实加噪声——那就是让新世界事实迁就旧字段）。

**④ 表更新**：② `Temp` → `ADAPTER_REQUIRED`；⑦ `MonthTemp` **仍 `SOURCE_MISSING`**（本项只做年均，季节属 P4-5）。

---

## ⑧ Phase 4 · P4-2 降水事实（2026-10-06 · **已完成**）

> **来源**：`docs/审查报告-P4-2-降水事实与provenance对照.md`。
> **性质**：**WorldGen 事实生产**（绝对量纲 + 与 MiamiNpp/R 的联合重标定**前置取证**）——
> **不接 CivSim、不写 Bridge、不重标定 `R`、不做 Biome**。

### 交付

```
WorldGen Climate └── PrecipitationFact[H3 res4]        （scripts/WorldGen/Simulation/PrecipitationModel.cs）
P[i] = ZonalMm(|lat|)[mm/年] × MaritimeFactor(lat, 离岸)
MaritimeFactor = InteriorRatio(lat) + (1 − InteriorRatio(lat)) · exp(−内陆km / CoastDecayKm)
```

- **载体/接线**：同 P4-1（逐格 ⟷ H3 res4 CellId，**无 res 档位判断**）；
  `Regenerate()` 调用签名不变；判读读数增 `meanP=`。
- **★占位 → 绝对量纲**：改前 `MaxMm(2400) × LatFactor(0~1) × CoastFactor(0.6~1)` 是**相对湿润度**；
  改后 `ZonalMm` 是**地球纬向年平均降水（mm/年）**，乘数是**无量纲比例** ⇒ `= 1800` **就是 1800 mm/年**。
  `MaxMm` / `LatFactor` 已**删除**；`ContinentalFloor` 由**单一常数**升级为**纬度相关 LUT `InteriorRatio`**。
- **边界/缺失**：**每格都有值**（海格 = 海面降水真值）；★**与 Legacy 有意不同**——Legacy 把 64.88% 的格（几乎全是海格）**填 0**。
- **为什么不新增第二个降水模型**：`AnnualMm` **已经是** WorldGen 水文的唯一降水输入
  （`RiverNetwork` / `H3Hydrology.cellWeight` / `LakeState` + 契约 `Hydrology_DoesNotReferenceLegacyClimateLine`）
  ⇒ 防膨胀三问第 1 问（能否由既有事实表达）+ 唯一事实源。**原地升级，不加层。**
- **测试**：`PrecipitationModelTests` **17 条**；全量单元 **517 PASS / 0 FAIL**（510 → 517）；
  地图套件 **79 PASS / 0 FAIL**；Headless res4 `meanP=1098mm/年` 零 ERROR。

### ★ 判定：`Precip` **`SEMANTICALLY_DIFFERENT` → `ADAPTER_REQUIRED`**

**为什么不是 `EQUIVALENT`**：陆地质 **2.5×**（mean 2,460 → 967）、**海格缺失语义不同**（0 填充 vs 真值）、
尾部宽度不同（干湿比 41× → 18×）、粒度不同。
**为什么不是 `SEMANTICALLY_DIFFERENT`**：**同物理量、同单位**，且下游**同一套非线性**（`MiamiNpp` 降水侧）。
⇒ **`ADAPTER_REQUIRED`**。**适配项**：A1 换世界｜**A2 `R` 联合重标定（下一步）**｜A3 冰盖阈值复核｜
**A4 禁止为对齐旧分布给新事实加噪声**。

### ★★ 最关键实测（推翻了"绝对量级高度敏感"的直觉预设）

```
Legacy 陆地降水 mean 2,460 mm/年  vs  新事实 967 mm/年   ⇒ 量级差 2.5×
但  R_raw = MiamiNpp(T, P)  的 P50：          1,260  vs  1,262   ⇒ 差 0.2%
```

原因有**两条**，都必须在后续决策里记住：

1. **`k` 中位数再归一化**：`CivEngine.BuildLayer1` 用 `k = TargetMedianDensity / median(R_raw)`、
   `R[i] *= k` ⇒ **`R` 的绝对量级被中位数锚吸收**，降水整体缩放不会线性传导到 `R`。
2. **`MiamiNpp` 强烈饱和**：`nppP(2400) = 2390/3000 = 80%` 饱和 ⇒ 高雨端几乎无区分度。

**真正变化的是"谁在限制"**：

| | Legacy | 新事实 |
|---|---|---|
| 降水侧受限（NppP < NppT） | **26.1%** | **81.3%** ← ★限制侧翻转 |
| 冰盖格（T ≤ −5.5） | **22.14%** | **6.31%** ★见下更正 |
| `R_raw` < 300（饥荒级） | 0.0% | 0.8% |
| `R_raw` > 2000（近饱和） | 18.6% | 13.9% |

⇒ ~~**`R` 整体量级不需要重标定；需要重标定的是空间格局与冰盖阈值。**~~

> ★★**后续更正（2026-10-06 · 独立佐证 A/B · 12 seed × res2/res4）——本块以上读数与结论按此阅读**
> `docs/审查报告-P4-2-独立佐证AB.md`
>
> 1. **链路口径**：本表读数来自**简化链路**（sigma 520、无火山、区域 8e6），**非**生产链路（4e6 / 160 / 含火山）。
>    定义已自检无误（`test` 链路逐位复现 6.31 / 81.3 / 1262），差异 100% 来自链路。
> 2. **限制侧翻转 = 稳健**：生产链路 12/12 seed ∈ [69.0%, 96.3%]（Legacy 26.1%）✅
> 3. **冰盖下降 = 不成立**：生产链路 **3.97% ~ 25.09%**（均值 14.0%），与 Legacy **重叠**；
>    6.31% 是 seed42 偏暖端样本。漂移已归因 = **大陆布局的自然方差**
>    （`r(ice%, 陆≥60°%) = 0.968/0.958`，`ice/高纬陆 = 1.28/1.25`，同 world 换 res 只差 ~7%）⇒ 非模型缺陷。
> 4. **"量级稳定"是代数恒等式**：`k = TMD/med(R_raw)`、`R = R_raw·k ⇒ med(R) ≡ TMD`（24/24 组 `|med_prod×k−0.1| ≤ 1.4E-17`）
>    ⇒ **不能**作为"不需重标定"的证据。`k` 吸收不掉的是**形状**：
>    `R_raw` P95/P5 跨 seed **3.92~6.34**、`R>2000%` **6.22~18.43**、陆地 P50 **580~996**。
> 5. **修正后结论**：`R` 的量级**由 `k` 定义掉**；可重标定的是 **「形状（尾部宽度）+ 冰盖阈值」**；
>    且**未达进入重标定的门槛**（三件事未全部跨 seed 成立）。
>
> ⇒ **下一步（修正）= 先落口径更正（docs-only）→ 再按"分布"评估形状重标定与冰盖阈值。**
> ★该"按分布评估"已于同日完成 = **§⑨ 路线 A**，判定 **不需要改形状** ⇒ **重标定关闭，直接进 P4-3 Biome**。

### 新债务（四问模板）

- ★**水文漂移 = 形状导致，非量级导致**。实测：`RunoffThreshold` 精确随 `meanPrecip` 缩放（比 0.777 ≡ `meanPrecip` 比）
  ⇒ **自锚定机制成立，整体缩放不改 `IsRiver`**；但陆地相对变干改变**形状** ⇒ 河格集合 **Jaccard 76.5%**（23.5% 翻转）。
- `CoastDecayKm` / `RiverThresholdAreaKm2` / `RiverThresholdCells` **均未动**（几何 B 既有冻结值；避免范围膨胀）。

**④ 表更新**：③ `Precip` → `ADAPTER_REQUIRED`（原 `SEMANTICALLY_DIFFERENT`）。

---

## ⑨ 路线 A：R 形状与冰盖分布评估（2026-10-06 · **已完成** · 判定 = 不需要改形状）

> **交付**：`docs/审查报告-R形状与冰盖分布评估.md`；诊断 `--p42shape`（`tests/PerfBench/Program.cs`，只读）；
> 原始 `scratch/p4_2_shape_n12.txt`；对照 `scratch/p4_2_shape_cmp.py` / `scratch/p4_2_legacy_r.py`。
> **边界**：`scripts/WorldGen/` 生产代码 **0 改动**；`MiamiNpp` / `R` / **冰盖阈值** 全部未动。
> **用户改写**：不校准绝对量级、只评估响应形状；不把目标冰盖占比当常数、只评估阈值在**世界生成器分布**下是否合理。

### 交付口径（用户点名）

- **R 形状**：`R_raw` P5/P50/P95 · P95/P5 · R>2000 · Land R P50 · Warm-land R 分布。
- **冰盖**：`ice%` · `ice/高纬陆` · 冰盖空间分布（按 `|lat|` 带）· 阈值敏感性。
- **核心是跨 seed 的分布，不是单点。**

### ★ 三条结论

| # | 结论 | 实测 |
|---|---|---|
| ① | **无"形状断裂"** | 新线 `R_raw` **P95/P5 = 3.92~6.33**（12 seed × res2/res4），Legacy 单世界 = **5.23** ⇒ 正落包络**中部** |
| ② | **冰盖漂移 = 大陆布局，非阈值** | 冰盖是干净纬度阶跃（`<50°` 无冰、`65–70°` 24/24 全冻、方差全在 **50–60° 过渡带**）；同世界扫阈值 ±1°C 只改 **0.0~1.6 pp**，跨 seed 漂移 **4.83%→25.09%（±135%）** ⇒ **效应比 ≈ 100×** |
| ③ | **无"异常肥沃膨胀"** | 新线 `R>2000%` **6.22~18.43** vs Legacy **18.6** ⇒ 高尾**更窄**（方向相反） |

★ **唯一真实结构漂移**（`k` 吸收不掉、必须跨 seed 验收）：`R<600%` 极差 **107~125%**、`R>2000%` 极差 **89~94%**。
★ **Legacy 单世界落在新世界包络内**：8 条锚中 5 条严格 ∈，2 条（`R_raw P95` +9.8%、`R>2000%` +0.17pp）同源且方向为"Legacy 更宽"。

### 判定

- **不需要改变模型响应形状 ⇒ 不进入 `R` / `MiamiNpp` 联合重标定**（⑧b 门槛"未达"未获新证据推翻）。
- **不冻结"目标冰盖占比"**；**不动 `−5.5°C` 阈值**（固定世界上其表现完全正常；seed21 阈值斜率 = 0.00 为极端例证）。
- ⇒ **`P4-3 Biome` 解锁（下一步）**。

### ★ 新原则 14（本 ADR 正式冻结）

> ### **禁止以 Legacy 单个世界的分布作为新世界 `R` 或冰盖阈值的目标分布；参数评估必须针对跨 seed 的新世界分布及其机制语义。**

- **为什么**：Legacy `.mpa` 是**单个世界的快照**。① 其 `ice% = 22.14%` 若被当成"健康值"，会掩盖真实新世界 `ice%` 是 **3.97%~25.09% 的族**；② 其 `R_raw P95 = 2430` 高于新世界 max **9.8%**，若当作靶会驱动一次**不必要的上尾放宽**。
- **怎么用才对**：只把 Legacy 的**响应曲线形状**（阈值斜率 1.595 %/°C、对比度 `P95/P5 = 5.23`）当**量级参照**（判"是否同阶"）；**不**把它的**点值**当目标。
- **与既有原则关系**：这是防膨胀三问中**"有无真实消费者"**在**参数验收**上的对偶形态——**目标必须是一个真实的、跨 seed 的分布**，而不是某个单世界算出来的数。
- **配套纪律**：任何基于 `R` 阈值占比（`R>2000%` / `R<600%` / `R<300%`）的验收**必须跨 seed**；单世界读数不构成验收。

**④ 表更新**：`R` / `MiamiNpp` 重标定项 → **关闭（无需求）**；冰盖阈值复核 → 作为**诊断分支**保留，**不立项**。

---

## ⑩ P4-3a · Biome 语义取证 + ★依赖倒置冻结（2026-10-07 · P4-3a 完成）

> **交付**：`docs/裁决-P4-3-Biome事实定义.md`（四项裁决 + 24 类登记表）、`docs/裁决-P4-5-季节气候最小充分事实集.md`、
> Legacy 只读取证 `scratch/p4_3_biom_forensics.py` → `scratch/p4_3_biom.txt`。
> **边界**：`scripts/WorldGen/` 生产代码 **0 改动**；不接 CivSim；不做 Bridge / HumanInputGrid / SubsistenceReadout / Soil；不加噪声。

### 四项裁决

| # | 裁决 |
|---|---|
| ① **分类语义** | `BiomeFact` = **气候型（Köppen–Geiger）+ 水面/地形附加类**，沿用 `World.Domain.BiomeType`（byte 24 值），不新建体系。不是植被型/生态系统型/游戏用途分类。★**游戏标签（`grass`/`plain`/`mediterranean`/…→`BiomeType` 集合）映射必须留在 `CivSimContext.EnvMatches`，不得进 `BiomeFact` 判据**（守 B4、防反向驱动）。 |
| ② **输入来源** | `T_ann` + `P_ann` + 海陆 + 海拔 + 离岸 + 河 + 湖。**不依赖 Soil** ⇒ **与 P4-4 无输入循环**，P4-4 是独立缺口（不阻塞）。★**`MonthTemp`/`MonthPrecip` = P4-5 前置缺口**。 |
| ③ **Legacy provenance** | 五项判据逐条落表；预期 **`SEMANTICALLY_DIFFERENT` → `ADAPTER_REQUIRED`**（不预设结论，生产后复核）。★附带取证：`BiomeType.cs` 注释写 `20×(T+14)`，**Legacy 实际用 `k=10`**（实测 desert 12/12、steppe 541/541 全满足；按注释写则 steppe **0/541**）⇒ 按注释实现会让 `ComputeLivestock` 全空。 |
| ④ **恢复条件** | `WildLivestock` ✅ 可恢复（`BSh/BSk` 年判；`Aw/Csa/Csb` 待 P4-5）。**`WildCrops` 5 种卡在 P4-5**（需 `WinterShare`/`MaxMonthTemp`/`MinMonthPrecip`/`WetDryRatio`）⇒ **P4-3 的真实收益不是 WildCrops**。 |

### ★★ 依赖倒置（本 ADR 正式冻结）

> **P4-3 dependency inversion：若 Biome 判据依赖 seasonal climate，则 Seasonal Climate 属于 Biome 的
> 上游事实生产阶段；不得在 Biome 内部从年均气候反推月度/季节事实。**

**触发事实**：`BiomeType` 的 **14 / 24 个类**（`IceCap`/`Tundra`/A 组/C 组/D 组）判据是**最冷月 / 最热月 / 干湿季**；
新线当前**只有年均 T 与年均 P**（`scripts/WorldGen/` 内 **零月度数据**，已实测确认）。

**为什么"Biome 内近似月度"被否决（B4）**：
```
Annual T/P → Biome 内部估算 Monthly T/P → Biome
```
= **分类层自己生产它所依赖的气候事实** ⇒ 违反 B4（判读/分类层只能消费，不能成为 source）；
且 P4-5 完成后会残留**两套月度气候来源**。

**为什么"先做 10 类"被否决**：14/24 ≈ 58% 类数占比，但覆盖 Legacy 陆地 ≈ **85%**
⇒ 类数占比**严重低估**真实影响 ⇒ 会产出**表面完整、语义不完整**的 `BiomeFact`。
⇒ **Biome 生产整体阻断，直到 Seasonal Climate 可用。**

**P4-3 拆两段**：`P4-3a` 语义契约 ✅ **已完成** ｜ `P4-3b` 事实生产 ⏳ **P4-5 之后**。

**依赖方向冻结**：
```
WorldGen ├─ Temperature ├─ Precipitation └─ Seasonal Climate
                        ↓ Biome ↓ WildCrops / WildLivestock ↓ HumanInputGrid / CivSim
```
**不得**出现 `CivSim 需要 Biome → Biome 需要月度 → Biome 自己补月度` 的反向驱动。

### P4-5 最小充分事实集（下一步主线的第一步）

从下游真实判据反推（**不是**先建月度场再裁剪）：
`ColdestMonthMeanC` / `HottestMonthMeanC` / `DriestMonthPrecipMm` / `WettestMonthPrecipMm` /
`SummerMinMonthPrecipMm` / `SummerMaxMonthPrecipMm` / `WinterMinMonthPrecipMm` / `WinterMaxMonthPrecipMm` / `WinterPrecipShare`
= **9 标量/格**（而非 24 条月度数组），**完整覆盖** Biome 24/24 类 + WildCrops 5/5 + WildLivestock。
★已知未来需求（**不在本轮预付**）：Legacy `MonthTemp`/`MonthPrecip` 是 CivSim `SOURCE_MISSING` 输入，
⑬ Bridge 若要求逐月字段，**那时再扩**——现在没有真实消费者（防膨胀三问第三问）。

**④ 表更新**：`Biome` 判定登记为 **`SEMANTICALLY_DIFFERENT` → `ADAPTER_REQUIRED`**（待 P4-3b 生产后复核）；
`MonthTemp`/`MonthPrecip` 维持 **`SOURCE_MISSING`**（P4-5 后转为可生产）。

---

## ⑪ P4-5 Seasonal Climate：可达性与修复（2026-10-07 · **已完成 v1.1**）

**交付**：`docs/审查报告-P4-5季节气候可达性与修复.md`；
生产改动**只有** `scripts/WorldGen/Simulation/SeasonalClimateModel.cs`（+ 接线、测试、契约登记、文档）。

### ★冻结条款 4：可达性是事实生产者的责任（producer expressiveness）

> **生产者必须保证：每一种被 `BiomeType` 定义合法、且机制上应可出现的季节型，
> 在 `SeasonalClimateModel` 的输出空间中具有非空可达域。**
> **禁止**为了让某类出现而去改 `BiomeType` 判据 / 阈值 / 分类顺序——那是把事实层缺失转嫁给消费者，违反 B4。

### ★冻结条款 5：三层验收，且不得用第 ③ 层反向驱动模型

| 层级 | 定义 | 落点 |
|---|---|---|
| **① 数学可达性** | 每个受影响类存在**一组合法输入**使判据成立 | `SeasonalClimateModelTests.Reachability_*`（6 条 `[Test]`） |
| **② 生产可达性** | 真实 res4 输入域下不是"只存在于理论极端" | `--p45reach` R3（双 seed ALL-OK） |
| **③ 世界出现率** | 仅观察 seed 分布 | `--p45shape`（**不得拿它要求模型制造某个比例**） |

### 关键解析结论（可复用）

- `Tcold = T_ann − amp/2`、`Thot = T_ann + amp/2`（单频谐波 + 月采样取到 ±1）
- **`D 组（Tcold ≤ −3 ∧ Thot ≥ 10）可达 ⇔ amp ≥ 13 °C`**（窗口宽度 = `amp − 13`）
- **`w`（冬干）判据 ⇔ `S > 9/11 = 0.8182`**

### v1 缺陷与 v1.1 修复

| 根因 | v1 | v1.1 |
|---|---|---|
| 降水季节对比度**上限** | `S_max = 0.55×1.3 = 0.715 < 0.818` ⇒ **Aw/Cwa/Dwa 数学不可达**（与 L、cf 无关） | `PrecipSeasonalityBands` 抬升到地球实测站点值，季风带高 `S` 延伸到 ~42°（北京 39.9°N = Dwa，`S≈0.97`） |
| 温度年振幅 LUT | 两端均低于地球（30°：12 vs Phoenix 22；45°：21 vs Beijing 30.4；海洋 51.9°：5.55 vs Valentia 8.4） | 校正到地球实测 |
| 大陆性响应**口径** | 复用 `ContinentalityScaleKm = 1200`（那是**年均温海陆差**的尺度） | 新增 `SeasonalContinentalityScaleKm = 500`（地球断面反解 358/473/518/594 km） |

**"1200 不行"的扫参证据**：`L=1200` 只在 blanket `amp×1.8` 下 ALL-OK，而 ×1.8 令 90° 大陆性振幅 = 48.6 °C（地球 ~30）⇒ 荒谬；
`L=500 ∧ amp×1.0` 直接 ALL-OK。⇒ 新常数必要性已证成，**不新建更多常数**。

### 结果

| 层级 | v1 | v1.1 |
|---|---|---|
| ① 数学可达性 | Dwa/Aw/Cwa ❌ | **6/6 ✅** |
| ② 生产可达性 | Dfa/Dfc/Dwa/Aw/Cwa 0.000% | **6/6 非 0** |
| ③ 出现率（陆地 %） | D 0.00~3.64%、**w 恒 0.00%** | D **0.25~17.79%**、w **11.73~22.74%** |

⇒ **24 个 `BiomeType` 无一类是恒 0 的幽灵接口**（与 ② Riparian 同型故障已排除）。
③ 层量级与地球同阶（非点值目标，遵守永久原则 14）。

**验证**：539 PASS / 0 FAIL（+8）；Headless 实机 `ampT=4.2C → 7.4C`，零 ERROR；
`ArchitectureContractTests.NewWorldLineTypes` 已登记 `SeasonalClimateModel`（漏登记 = 契约静默失效）。

### 边界重申

允许改：季节 T 表达能力 / 季节 P 表达能力 / 9 个输出对应的生成逻辑 / 可达性测试与诊断。
**禁止**：`BiomeType` 判据、`Biome` 分类顺序、Legacy 分布拟合、为覆盖率加噪声、Soil、CivSim、Bridge、年均 `TemperatureFact`。
**9 个输出契约不扩**（内部生成过程可以更丰富，对外仍只暴露 9 标量/格）。

**收口定义**：本轮 = **"从结构不可达 → 结构可达"**。不是"从结构可达 → 分布与 Legacy 一样"。
P4-3b Biome 实测后若某类仍偏少，那是新的生态/气候空间分布问题，**不是 P4-5 的表达能力问题**。

---

## ⑫ Climate 架构契约（2026-10-07 · **路线反转** · P4-5a 纯设计）

完整契约见 `docs/裁决-Climate架构契约.md`；本节只登记**裁决与冻结条款**。

### 为什么反转（用户拍板 2026-10-07）

现有三个气候生产者（§⑦ 温度 / §⑧ 降水 / §⑪ 季节）**各自为政**，且**源码注释自己就声明是占位实现**
（"无洋流项（无生产者）"、"正式气候模拟上线后由湿度/环流过程取代"、"不表达地形雨/季风/洋流/季节循环"）。
★**结构性缺陷**：`SeasonalClimateModel` 是 `P(m) = (P/12)(1 + S·cos…)` —— **月度由年均反推**；
正确方向应是**从月度状态聚合出年均与季节统计**。这正是用户担心的"人工生成月度"的当前形态。

⇒ **第一步不是填节点，而是先钉住这张耦合图。** `scripts/WorldGen/` 生产代码 **0 改动**。

### 四概念（★裁决）

```
World Facts + Forcing → ClimateSolver → ClimateState → Climate Facts
```

| 概念 | 定义 | 关键性质 |
|---|---|---|
| `ClimateForcing` | 天文（纬度×月 → 太阳辐射）+ 世界事实（Elev/Land-Ocean/CoastDist/Mountain/OceanGeometry/Hydrology·Ice） | **函数**，无状态、无记忆、不参与迭代 |
| `ClimateParameters` | 物理常数 + 工程标定（**几何 B / 物理 C**，跨 res 不变） | **集中**，不得散落各 Model；新增须有关断值（退化解） |
| `ClimateState` | 逐格 × **逐月**：SST / AirTemperature / OceanCurrent(诊断) / Moisture / Precipitation / Snow-Ice | ★**内部时间步长 = 月**；是否存 12 条数组**本契约不拍死**（O-C1） |
| `ClimateSolver` | 迭代/松弛 State 至收敛的过程 | **单向**：不回写世界事实层、不被 Biome/CivSim 调用、无随机项 |

### 五问裁决

① 时间 = **月**（年均/季节统计 = 聚合产物）｜② 空间 = **H3 res4**（承 I1，不做 res3 中转）
③ 强迫 = 纬度 + 逐月辐射 + 地形 + 海陆（+ 离岸）+ 海洋几何
④ 状态 = SST / T / Moisture / Precipitation / Snow-Ice（**记录不反馈**）
⑤ v1 反馈 = **辐射→温度→蒸发→水汽→降水** 核心环 + **海洋热惯性 / 洋流热输送 / 地形抬升** 三项；**雪冰反照率 = v2**

### 关键单项裁决

- **洋流 = 诊断场（B）**：由 SST 单向诊断，**不给洋流独立时间积分**（收敛性可控）；升级到"迭代状态（C）"须新消费者举证。洋流**不进世界事实层**、不持久化。
- **既有三模型不删除**：正式地位 = **占位气候事实（Placeholder Climate Facts）**；`Annual T/P` = 12 月聚合的当前近似，9 标量 = 月度 State 降维的当前近似。
  ★**退化/对齐契约**：P4-5e 上线时须**同量级同口径**，沿用 ⑨ 的**形状**判据（不用单点值对齐，承原则 14）。
- **过渡期冻结**：`ClimateSolver` 落地前，`SeasonalClimateModel` **只修 bug、不新增物理机制**。
- ★**口径教训**：`1200`（年均温海陆差）与 `500`（年较差）已被实测证明是**两个响应函数**，曾被当成同一个"大陆性"直觉 ⇒ `ClimateParameters` 必须显式命名响应函数。

### ★冻结条款 6–9（新增）

- **6 时间方向**：气候状态内部步长 = 月；**禁止**任何气候生产者"由年均值反推月度"（与 §⑩ 依赖倒置互为表里）。
- **7 洋流定性**：诊断场（v1）；v1→v2 须新消费者举证。
- **8 雪冰**：反照率反馈**不在 v1 核心闭环**；`Snow/Ice` 可产出但**不得回写温度**。
- **9 solver 单向**：`Facts + Forcing → Solver → State → Facts`；不回写事实层，不被 Biome/CivSim 调用。

### 阶段与硬约束

`P4-5a 契约（✅ 本轮）→ P4-5b Solar Forcing + 海陆热响应 → P4-5c 水循环 + 地形抬升 → P4-5d 海洋热惯性 + 洋流输送 → P4-5e Climate Facts 投影与对齐验收 → P4-5f（v2）雪冰反照率`。
每一步：**不接 Biome、不接 CivSim、不删既有生产者**（除非 P4-5e 对齐通过）。

---

## ⑬ Bridge 实现设计（2026-10-07 · **纯设计** · 零代码 · 不实现 B1）

> **来源**：`docs/设计-NaturalInputBridge实现设计.md`（纯设计，零代码改动）。
> **性质**：把 ⑤⑥ 的载体裁决落成**可实施的分阶段方案**；本轮**不写任何 Bridge / `HumanInputGrid` 代码**。
> **版本：Architecture Baseline v1（2026-10-07 冻结）**——修正 5 处契约表达后冻结，等待 Phase 4 完成后进入 B0：
> ① Jaccard 须**地理重映射**（Legacy 与 res4 不同空间域，dense index 不可直比；§7.3.1）
> ② R3 **届时升格** `FinalWorldFacts_Consumers_Are_Whitelisted`（白名单 +`World.Bridge`；实测当前白名单字面只有 WorldGen ⇒ Bridge 一接入即红；§12.1）
> ③ `WildCrops`/`WildLivestock` **惰性持有化**（生成方 = Bridge 投影层；载体不 Compute；`Ensure*` 只能是 materialization；§3.3.1）
> ④ **9 标量双证明分离**（A=CivSim 消费面已证 ≠ B=Biome 生产充分性；P4-3b 须自证；§4.1）
> ⑤ O5 **分消费者阈值**（不设全局单一 Jaccard 阈值；河网 76.5% 只作经验量级）
> ＋ 补 B1 钉子 **N12** `HumanInputGrid_CellIdentity_IsStable`（`CellIds[i]→CellIndexOf==i` 全量可逆 + 唯一 + 构建序稳定——B3/B4 存档错位的隐蔽源）。

### 形态（四层）

```
WorldGen 事实层（World.WorldGen.*） → Bridge 投影层（World.Bridge）
   → HumanInputGrid（空间容器） → CivSim（21 个生产消费者）
        └── R2 禁区：World.CivSim 不得引用（深度传递）──┘
```

### ★ 载体三项禁令（本轮新增 · 永久）

> **`HumanInputGrid` 是 CivSim 输入事实的兼容载体。它：**
> **1. 不重新生成世界事实**；**2. 不反推缺失的月度数据**；**3. 不承载 Legacy-only 变量**。

防的是 Bridge 被逐渐做成**第二套 WorldGen**——一旦它开始自己算气候、自己补季节、自己造字段，
就退化为又一个无契约约束、无收敛验证、无跨 seed 基线的事实生产源（与 §B4 同源）。

### ★ R2 是深度传递扫描（实测）

`ArchitectureContractTests.cs:185` 用 `ReferencedTypesDeep`（传递闭包），
`IsWorldGen` = `ns == "World.WorldGen" || ns.StartsWith("World.WorldGen.")`
⇒ **`HumanInputGrid` 绝不能放 `World.WorldGen` 或 `World.WorldGen.*`**（连 `World.WorldGen.Bridge` 都会命中）。
推荐 `World.Bridge.HumanInputGrid`（备选 `World.H3Grid`，须显式改白名单）。

### ★ 本轮两条用户拍板

1. **B1 不提前**：严格执行 `Phase 4 完成 → B0 → B1 → B2 → B3 → B4`。
   B1 技术上可独立做且能尽早暴露 R2/命名空间问题，但当前已有「Phase 4 期间禁写 Bridge」的过程约束，
   **为局部收益破坏阶段边界，代价大于收益**。（写设计文档 ≠ 写 Bridge 生产代码，故本轮合法。）
2. **Biome / Soil 升格为 B2 重点审计项**，不是普通字段投影。
   真正的风险在语义转换 `WorldGen → HumanInputGrid → CivSim 使用语义`；
   必须 **「逐类映射 + 逐项验证」**，**不得只验证数值范围或总量**——**必须验证类别形状与空间分布是否保持**。
   依据：`IsColdZone` 只认 4 类、`TerrainCost` 只分 5 档、`PreyFrac` 只分 3 档
   ⇒ **类别总数/温度范围/降水总量全对，也可能出现"山脉变畅通"这类静默语义错**。

### ★ Month* 不建 12 月数组

P4-5 只产 **9 标量/格** ⇒ Bridge 暴露 **4 个 CivSim 派生量**：
`WinterShare`←`WinterPrecipShare`、`MaxMonthTemp`←`HottestMonthMeanC`、
`MinMonthPrecip`←`DriestMonthPrecipMm`、`WetDryRatio`←`Wettest/Driest`。
（兑现 §⑩ 的依赖倒置冻结；违反即触三项禁令第 2 条。）

### 阶段与硬约束

`Phase 4 完成（含 P4-5e 对齐 + P4-3b Biome 生产 + P4-4 Soil）→ B0 → B1 载体骨架 → B2 逐项投影
→ B3 接线 + 同批 hop→km 重标定 → B4 清退`。
**B3 必须与重标定同批**（I4）：分批改会造成 `reachKm` 1,451 → 547 km 的 **÷2.65 静默缩放**。

### 清退（B4）

依据 ⑥ §八之二（不读旧档、不保留 Legacy 兼容层）。
⚠️ **清退必须与 B3 同批**：`GameGrid` 不是存档而是**运行网格**，`CivEngine.Run(grid,…)` 外部注入且唯一构造入口来自存档
⇒ **提前删 = CivSim 能编译但跑不起来**。

### 新增开放项

**O5**：Biome / Soil 的**类别形状 Jaccard 验收阈值**（须实测；参照水文 Jaccard 76.5% 量级，B2 时定）。

---

## ⑭ P4-5b · 太阳强迫 + 海陆热响应（2026-10-07 · **已完成并冻结**）

完整报告见 `docs/审查报告-P4-5b-太阳强迫与海陆热响应.md`。范围（用户拍板，刻意窄）：
`Latitude + Month → Solar Forcing → Land/Ocean Thermal Response → Monthly Temperature`；
**不做**降水/水汽/地形抬升（P4-5c）、洋流热输送（P4-5d）、**雪冰反照率（v2）**、Biome/CivSim；**不拍**任何大陆性数值。

### ★收口拍板（用户 2026-10-07：通过、冻结、不再扩功能）

- **冻结的是分层边界**：State 只存事实｜Forcing 无状态强迫｜Parameters 响应规律｜Solver 求解；闭式周期稳态解在无耦合下成立，P4-5c 同插槽换迭代松弛（架构连续性）。
- **异常驱动 = 本轮最关键决定**：DC（年均基准）/ AC（季节结构）拆开 ⇒ 本阶段不可能偷改绝对温度基准。
- **海陆 τ = 能力边界非缺陷**：内陆/海岸季节差异不存在是登记的边界；禁止提前补 continentality。
- **四条近似不提前补**（日地距离/大气衰减/雪冰/海拔-振幅）——P4-5b 的科学问题（"纯辐射 + 海陆热惯性能否产生合理月循环"）已回答 = **可以**。
- **P4-5c 重点 = Coupled Solver 框架**（ClimateState 多分量 + 迭代求解），不是水循环公式。
- **新增冻结条款 10**：`T_ann` 年均锚**不可**被 Moisture/Precipitation 等后续分量重定义（年均与季节结构 = 两个层级）。
- **新增冻结条款 11**：`MeanAnnualRangeC`（`monAmpT`）= **逐格年较差的面积加权平均**（语义 A），非"全球平均序列年较差"（语义 B，被半球反相抵消）；口径变更须显式裁决。

### ★P4-5c 目标收窄（用户拍板 2026-10-07，契约 §九）

三个架构问题（**不做**"大气模拟器"）：
① **多分量 `ClimateState`**——先证明一格能稳定保存 Annual/Monthly Temperature + Moisture + Precipitation[12]，槽位可扩展；
② **统一迭代接口**——`State(t) → tendencies → State(t+1) → Convergence?`；P4-5b 闭式解 = 无耦合的特殊 Solver，P4-5c 同插槽换通用求解框架；
③ **只验证"耦合成立"**——最小链 `Temperature → Evaporation → Moisture → Precipitation`，验收 = 状态确实被改变 + 守恒/边界/退化；地形抬升/水汽输送/洋流/雪冰继续后置。
**阶段分界**：P4-5b = 季节温度可由物理强迫 + 热惯性产生 ✅；P4-5c = 多分量可在同一 State/Solver 框架中耦合。

### 交付

- **四概念首次落地**：`ClimateForcing`（TOA 逐月日射，纯天文函数）｜`ClimateParameters`（Gain **0.0786** / τ陆 **1.0 月** / τ海 **8.0 月**，全部**标定自地球站点**：45° 陆 amp=30（北京 30.4/芝加哥 28）、陆峰 7 月、陆/海比 **4.01**（地球 ≈4.0））｜`MonthlyTemperature`（**`ClimateState` 第一批分量**，month-major，13.8 MB @ res4）｜`ThermalResponseSolver`（一阶热容对周期强迫的**闭式稳态解**，O(12)/格——无耦合 ⇒ 闭式 = solver 退化形态，P4-5c 起换迭代松弛）。
- ★**异常驱动**：`Teq(m) = T_ann + Gain·(S(φ,m) − S̄(φ))` ⇒ **年均温度严格不变**（DC 增益 = 1）⇒ **不移动** `R`/冰盖/降水的既有结论；`Gain=0` ⇒ 12 个月**逐点精确**退化（永久原则 4）。
- ★**能量守恒锚**：面积加权全球年均 TOA = **340.24999** vs `S0/4 = 340.25`（0.03 ppm）。
- `NewWorldLineTypes` 补登记 4 类型。

### 验收

- **554 PASS / 0 FAIL**（+15 条护栏：天文 7 + 求解器 8；含退化解逐点、DC=1、陆峰 7 月/海峰 9 月、南半球翻转 1 月、40–50° 陆 amp ∈ 30±8）。
- **Headless 实机**（res4，`--quit-after 120`）：`monMean=13.1C ≡ meanT=13.1C`（**DC=1 实机确认**）、`monAmpT=9.3C`（地球全球面积加权 ≈8.7 ⇒ 同量级）；9.3 s。
- 对照占位实现 `ampT=7.4`：差异预期内（真实辐射强迫 vs LUT）；**形状对齐是 P4-5e 的验收题**。

### 修掉的坑（证据链）

1. **极点 `tan` 翻符号**：float π/2 略大于真实 π/2 ⇒ `tan(90°)` 为**负**巨大值 ⇒ 北极 1 月被误判极昼（−490 W/m²）。修复 = 纬度夹到极点内侧 1e-7 rad。
2. **Headless 主场景不自动退出** ⇒ 需 `--quit-after N`（曾挂 41 min）。

### 已知近似（登记）

无大陆性（契约推迟拍板）｜无雪冰反照率 ⇒ ≥75° 年较差偏高 ~20%（v2）｜海拔不改振幅｜无日地距离因子（±3.4%）｜赤道 TOA 双峰 ⇒ 滤波后峰值 10 月。

---

## ⑮ Ocean Circulation 架构契约（2026-10-07 · **纯设计** · 与 P4-5c 并行 · 零代码）

完整契约见 `docs/裁决-OceanCirculation架构契约.md`。用户拍板：洋流与 P4-5c 水循环**并行**推进（D 线），先冻结架构。

- **★与 ⑫ 条款 7 的关系 = 方向反转修订**：旧裁决"洋流 = 由 SST 诊断的场"（`OceanCurrent ← SST`）改为
  **`World Facts（几何 + 风带强迫）→ 洋流场 → Climate 消费`**——洋流 v1 不读 Climate 任何状态（真并行）；
  保留不变的部分：**仍不给独立时间积分**（升级须新消费者举证）。因果链变为
  `纬度 → 基础温度带 →（洋流热输送）→ 海温修正 → 沿海温度 → 降水`，先单向打破气候—洋流循环依赖。
- **★条款 12（海盆语义分离）**：`BasinGraph.BasinId` = 水文流域语义（海格 = −1，海是出口不是成员），
  **不得**被洋流复用或修改；海洋连通域唯一生产者 = 新类型 `OceanRegions`（新子层 `scripts/WorldGen/Oceanography/`）。
- **★条款 13（洋流单向）**：`World Facts → Oceanography → Climate`；Oceanography 不引用 Climate；Climate 可读洋流场但不得回写/迭代。
- **★条款 14（稳态场无时间积分）**：v1 = 稳态表层环流场（纬度风带 + 科氏 + 海盆连通 + 海岸形状），**不做** Navier–Stokes / 热盐环流 / 深层海洋 / 密度反馈；Gain=0 ⇒ 逐点退化（原则 4）；条款 10 适用（洋流修正不得回改 `T_ann` 锚）。
- **阶段**：D0 契约 ✅ → D1 `OceanRegions`（可立即开工）→ D2 `OceanCurrentField` 稳态原型 → D3 Climate 接线（等 P4-5c Solver 框架稳定）；与 P4-5c 分开 commit（原则 13）。
- Legacy `CurrentDirs` = 零消费者遗留字段，禁止当来源（承 ⑫ §一）。
- **★终审拍板（2026-10-07）：D0 = 通过、冻结**。**★D1 已落地（2026-10-07，唯一新文件 `scripts/WorldGen/Oceanography/OceanRegions.cs`）**：海格连通分量（flood fill，格索引升序确定性编号）+ `MemberCount`/`AreaKm2`（拓扑 vs 几何，收口 D-10）+ 全陆/全海退化；6 条护栏；**未接线 WorldGenPlanet**（消费面 = D2，届时再接）；`NewWorldLineTypes` 已登记。
**★D2 已落地（2026-10-07，`OceanCurrentField.cs` + `OceanCurrentModel.cs`）**：Stommel (1948) 流函数松弛 `r∇²ψ + β∂ψ/∂x = curl_z(τ)/ρ₀`（陆格 ψ=0 Dirichlet；donor-cell 迎风；SOR 确定性）→ `u = (1/H)·n̂×∇ψ` → Direction/Speed/Strength（§3.3 语义）；**实现修正 = β 平流项必须进方程**（略去则流环中心漂到 ~55° 而非副热带 ~30°），O-O3 收窄为"不做额外近岸修正"； forcing = 分段半余弦风应力瓣（零交叉 ±30°/±60° 地球锚）；贴岸全切向投影（POCS 16 轮）+ 停滞点判别（投影抹除 >99.99% ⇒ 置零）钉死测试钉子 8；10 条护栏（forcing 零交叉/旋度符号/f·β 定义式/ψ 半球结构/场不变量/量级锚/切向钉子/确定性/全陆退化/全海不变量），**590 PASS**；未接线 WorldGenPlanet（消费面 = D3）。★D3 守恒口径补钉（用户 2026-10-07）：守恒测试须落在**海格间通量**（`CurrentField → 邻接通量 → A→B → 温度 tendency`）上，Direction+Speed 向量场本身不保证离散 H3 网格通量守恒。三条收紧补入契约：① **D2 驱动语义 = 风应力旋度 + β 效应**（禁止"局地风向 + 科氏偏转"的退化实现——那是纬向流，不形成副热带环流圈）；② **海岸边界 = `FinalLandMask + CellNeighbors` 本地派生**（法向/切向），`FinalDistToLand` 仅作距离辅助，**不新增世界事实层**；③ **Direction（切平面单位向量，`·radial=0`、`|Direction|=1`）/ Speed（km/月，≥0）/ Strength（无量纲环流强度，≥0）语义锁死**，陆格 invalid/zero。＋ **D3 输运纪律**：洋流热输送 = **重分配**（全球积分近零和），禁止 local source，否则 Gain=0 退化与守恒测试名存实亡。**D2 可开工；唯一等待项 = D3**。不因洋流顺手建 OceanTemperature/海冰/深层环流/海洋热容量。
- **★D2 已落地（2026-10-07，并行会话）**：`OceanCurrentModel`（Stommel (1948) 算子 SOR 流函数松弛：`r∇²ψ + β·∂ψ/∂x = curlτ/ρ₀`，陆格 Dirichlet ψ=0；β 平流项进方程钉住副热带流环；donor-cell 迎风）+ `OceanCurrentField`（Direction/Speed/Strength，§3.3 语义）；10 条护栏。
- **★D3 已落地（2026-10-07，P4-5d 接线）**：`CoupledClimateSolver.Solve` 增 `oceanHeatGain`（**默认 0 = P4-5c 冻结基线逐位**）+ `oceanCurrents` 强迫项参数；通量 `q_ij = 0.5(v_i+v_j)·n̂_ij·edge·H·gain` **逐对反对称** ⇒ 迎风平流 tendency **零和精确**（重分配，非 local source）；CFL 夹子（0.25/月，全局等比缩放保零和）；陆格不参与（海-陆热输送 = 0，温度逐位不动）；`OceanHeatTransportGain=1` 生产值经 `WorldGenPlanet` 显式传入。6 条护栏全绿 + Headless `dToc=+0.0000C`（条款 10 强形式）；报告 `docs/审查报告-P4-5d-洋流热输送接线.md`。**D 线 D0–D3 全部完成，P4-5d 收口；开放项 O-O1（逐月风应力，等 ⑰ 风场汇合）/ O-O2（地球锚标定，归 P4-5e）。**

---

## ⑯ P4-5c 耦合 Solver 骨架 + 最小水循环（2026-10-07 · **纯架构升级** · 568 PASS）

完整报告见 `docs/审查报告-P4-5c-耦合Solver骨架与最小水循环.md`。
**性质裁定（用户，契约 §九）**：P4-5c **不是又一次气候学参数大标定**——P4-5b 的 Gain/τ/T_ann/MonthlyTemperature 语义与诊断口径全部视为冻结基座，开工时不重新打开；本轮只验证"耦合成立"。

**三个架构问题的落地**：

1. **多分量 `ClimateState`**：`Temperature`（P4-5b 冻结分量原样持有）+ `WaterVapor` + `Precipitation`（`MonthlyField<float>` 通用逐格×月槽位，`At/AnnualTotalAt/AnnualMeanAt/GlobalAnnualMean`）＋全局读数（`GlobalAnnualEvapMm/GlobalAnnualPrecipMm/GlobalMeanWaterVaporMm/SpinupYearsUsed/Converged`）。槽位可扩展。
2. **统一迭代接口**：`State₀ → evaluate tendencies → State₁ → Converged?`（`CoupledClimateSolver.Solve`，`MaxSpinupYears=60`、`ConvergenceTolMm=1e-3`）。P4-5b 的闭式解 = **无耦合特殊形态**，温度分量复用 `ThermalResponseSolver`，同插槽换通用框架。
3. **最小耦合链（T→蒸发→水汽→降水）**：
   - 蒸发 `E = EvapGainMmPerMonthC(6) × T⁺`（仅海格且 T>0；**陆格 ≡ 0 = 能力边界**，登记待 P4-5e+ 补土壤水/植被）；
   - 水汽扩散 = **通量形式** Jacobi 扩散（`k = 1/(6·τ_mix)`，τ_mix=2 月；邻接通量配对相消 ⇒ **严格守恒**）；
   - 库项 `−W/τ_p`（τ_p=0.3 月）用**解析积分**（显式欧拉 dt/τ_p=3.3>2 会发散；decay=exp(−1/τ_p) ⇒ 无条件稳定），`wNew<0` 非负保护；
   - 降水 = **收支记账** `P = W + Src − W'`。

**四条硬验收（全绿）**：① 依赖成立（Gain=0 ⇒ 蒸发/水汽/降水逐点 = 0，温度精确退化）② 迭代收敛（spin-up 数年即达 1e-3）③ 退化成立（条款 10 执行钉：`T_ann` 年均锚逐字不变）④ 水量边界（降水 ≈ 蒸发 ± 相对容差 1e-3；非负保护引入小偏差）。

**实机读数（Headless res4）**：`monMean=13.1C monAmpT=9.3C`（**P4-5b 基座逐字未变**）+ `wv=18.7mm prec=749mm/年`，spin-up 成本 26.3 s。

**测试**：`CoupledClimateSolverTests` 7 条（四硬验收 + 条款 10 钉 + 空间梯度涌现）；全量 **568 PASS / 0 FAIL**。`NewWorldLineTypes` 登记 `MonthlyField/ClimateState/CoupledClimateSolver`。

**不做的**：地形抬升/水汽输送风场/雪冰/陆面过程/参数标定（数值均为量级占位，标定推迟到 P4-5e 对齐验收）。
**★终审冻结（用户 2026-10-07）**：报告 §六 三冻结物 F1/F2/F3（568 PASS 测试基线｜机制产物非目标值｜26.3 s 性能基线）；编号裁定确认 **ADR-0005 = 编号权威**（⑮ Ocean｜⑯ P4-5c｜⑰ WindField），并行会话编号冲突以 ADR 为准。**P4-5d 目标收窄**（契约 §十二）：回答"海洋能否成为空间热输送通道并反馈给温度"；依赖方向 `Solar Forcing → Temperature ← Ocean Heat Transport ← Ocean circulation field` 单向不可逆；**禁止 Ocean Current 重定义太阳强迫或 T_ann**（条款 10）；第一轮先机制和边界、不求地球数值。

---

## 执行契约（方向守卫 · 2026-10-06）

边界条款对应的**可执行钉子**（`tests/World.Tests/ArchitectureContractTests.cs`）——
把"我们记得"变成"加依赖就变红"：

| 契约 | 钉住 | 归属条款 | 状态 |
|---|---|---|---|
| `CivSim_DoesNotReferenceWorldGen` | `World.CivSim*` 全体类型 ⟂ `World.WorldGen*`（**全程序集 + 成员 + 方法体 IL** 扫描） | B1 / B2 | ✅ 2026-10-06（R2；灵敏度对照已验） |
| `FinalWorldFacts_OnlyConsumedByWorldGen` | `FinalGeography` / `FinalSpatialIndex` 的消费者白名单 = **WorldGen 内部**（`World.Tests` 是独立程序集，天然不在扫描面内） | B2 | ✅ 2026-10-06（R3；灵敏度对照已验） |
| `ProductionRes_IsRes4_Frozen` | `WorldGenPlanet.ProductionRes` 反射读**原始常量** = 4 且 `IsLiteral` | **I1** | ✅ 已有（`SpatialScaleTests`，2026-10-04） |
| CivSim 基础粒度 = `ProductionRes` 方向钉 | `HumanInputGrid` 基础粒度必须 = `ProductionRes`（不得另立档位 / 引第二套空间索引） | I1 / I2 / I3 / I5 | ⏳ **随 `HumanInputGrid` 落地时补** |
| hop→km 方向钉 | 影响半径类参数以 **km** 表达（不得以 hop 硬编码固定 km 换算） | I4 | ⏳ **随重标定补** |
| 判读层 sink-only 方向钉 | 判读层类型不得进入事实生产类型的**被引用集** | B3 | ⏳ **随 ③ `SubsistenceReadout` 落地时补** |
| （扫描面纪律）新事实类型必须登记 | `ArchitectureContractTests.NewWorldLineTypes` 是各条契约的公共扫描面；新增世界事实类型时不登记 ⇒ **漏扫 = 契约静默失效** | B2 / §⑨ | ✅ 2026-10-06（`TemperatureModel` 已登记；⑧ P4-2 **原地升级**既有 `PrecipitationModel`，无新类型） |

> 全量单元测试：**568 PASS**（⑦ P4-1 后 498→510；⑧ P4-2 后 510→517；⑭ P4-5b 后 561；⑯ P4-5c 后 568），0 FAIL。
> 地图套件（`CivSimDiag --arch=…`）：**79 PASS / 0 FAIL**（⑧ 后复测）。

---

## 当前路线（用户 2026-10-06 拍板）

```
① FishPotential                      ✅ 已完成（2026-10-06，独立 commit）
② Riparian                           ❌ 取消，不重建
③ SubsistenceReadout                 ← 低风险观测增强（观测能力，非模拟能力；随其落地补 B3 契约）
④ World → Human Input Provenance     ✅ 已完成（2026-10-06，逐项语义等价性 + 五标签）
⑤ Bridge Feasibility（可行性裁决）    ✅ 已完成 —— 裁决 = BLOCKED（对岸无事实 + 载体未定）
⑥ HumanInputGrid 载体契约            ✅ 已完成 —— 方向已定（H3 res4 冻结 + I1–I5 不变量）
⑦ Phase 4 事实生产                   ← 解除 Bridge 阻断的**唯一剩余前置**
   ├─ P4-1 Temperature               ✅ **已完成**（2026-10-06；`Temp`：`SOURCE_MISSING` → `ADAPTER_REQUIRED`）
   ├─ P4-2 Precipitation（绝对量纲）   ✅ **已完成**（2026-10-06；`Precip`：`SEMANTICALLY_DIFFERENT` → `ADAPTER_REQUIRED`）
   ├─ ⑧b 独立佐证 A/B（12 seed × res2/res4） ✅ **已完成**（2026-10-06）—— **门槛判定 = 未达**
   │    └─ 限制侧翻转 ✅稳健 / 冰盖下降 ❌不成立（已归因 = 自然方差）/ "量级稳" = 代数恒等式
   ├─ ⑨ 路线 A：R 形状 + 冰盖分布评估     ✅ **已完成**（2026-10-06）—— **判定 = 不需改形状**
   │    └─ 对比度落 Legacy 包络正中 / 冰盖漂移 = 大陆布局（阈值效应 ×100 更小）/ 无肥沃膨胀
   ├─ R / MiamiNpp 联合重标定              ⛔ **关闭（无需求）**——⑨ 判定无需改响应形状
   │    └─ 形状 ✅无变化（P95/P5 ∈ Legacy 包络）｜冰盖阈值 ✅不动 `−5.5°C`｜生计响应 ✅已证稳健
   ├─ ⑩ P4-3a Biome 语义契约          ✅ **已完成**（2026-10-07）—— **不生产**
   │    └─ ①气候型 Köppen ②输入契约 ③provenance ④恢复条件 + 24 类逐一登记
   ├─ ⛔ P4-3b Biome 事实生产           ← **阻断中**（放行条件 = P4-5 的 9 标量可用）
   ├─ ★P4-5 Seasonal Climate          ← **下一步主线**（依赖倒置后升格；最小集 = 9 标量/格）
   │    └─ 阻断项：14/24 Biome 类 + WildCrops 5 种（否则 BiomeFact 语义不完整）
   ├─ P4-4 Soil                       ← 与 P4-3 **无输入循环**（Biome 不依赖 Soil）
   └─ ⑬ Natural Input Bridge          ← 吸收 ④ 的 ⑦⑧（`MonthTemp` / `MonthPrecip`）
```

**C-6 合并落档（2026-10-06）**：✅ 已完成 —— R1 §边界条款 / R3 桥面白名单契约 /
R4 §迁移对照基准 / R5 §B3（只钉方向，不建 `SubsistenceReadout.cs`）。
**生产代码 0 改动 · 世界生成桥 0 接线 · `SubsistenceReadout` 0 实现。**

**④ / ⑤ / ⑥ 落档（2026-10-06）**：✅ 已完成（三份纯只读文档，**生产代码 0 改动**）。
④ = 15 项输入逐项语义等价性（可桥接仅 `Elev`；`LakeLevel` 需适配）。
⑤ = 可行性裁决 = **BLOCKED**：Bridge **暂不实现**；正确形态 = `WorldGen facts → HumanInputGrid`。
⑥ = 载体契约 = **方向已定**：`H3 res4` 冻结（§不变量 I1–I5）；链路 = `CellId → dense index → topology/metric → 区域聚合`。

**⑦ P4-1 落档（2026-10-06）**：✅ 已完成（**生产代码 = 1 个新事实类型 + 1 处接线**；**无 Bridge / 无 CivSim 改动**）。
`TemperatureModel`（`WorldGen Climate └── TemperatureFact[H3 res4]`）+ `TemperatureModelTests` 12 条；
全量单元 498→**510 PASS**。`Temp` 判定 = **`ADAPTER_REQUIRED`**（见 §⑦）。

**⑧ P4-2 落档（2026-10-06）**：✅ 已完成（**生产代码 = 1 个既有类的原地升级 + 1 处读数**；**无 Bridge / 无 CivSim 改动 / 无新类型**）。
`PrecipitationModel` 占位语义 → **绝对量纲（mm/年）**；`PrecipitationModelTests` 重写为 **17 条**；
全量单元 510→**517 PASS**，地图套件 **79 PASS**。`Precip` 判定 = **`ADAPTER_REQUIRED`**（见 §⑧）。
★**最关键实测**：Legacy 陆地降水是新事实的 **2.5×**，但 `R_raw` 的 **P50 几乎相同（1260 vs 1262）**
⇒ `k` 中位数再归一化 + `MiamiNpp` 饱和**双重吸收绝对量级**；变的是**限制侧**（降水受限 26.1% → 81.3%）与**冰盖**（22.14% → 6.31%）。
★**此后经独立佐证 A/B 修正**（见 §⑧ 更正块 + `docs/审查报告-P4-2-独立佐证AB.md`）：
上表来自**简化链路**；生产链路 12 seed 下 —— 限制侧 **69.0~96.3%（12/12 稳健）**、
冰盖 **3.97~25.09%（与 Legacy 重叠 ⇒ "下降"不成立）**、`R_raw` P50 **854~1586**、
且「量级稳」是 `med(R) ≡ TMD` 的**代数恒等式**。

**下一阶段（用户冻结顺序 · 2026-10-07 **二次反转**）**：`P4-2 ✅ → ⑧b A/B（✅ 未达门槛）→ ⑨ 路线 A（✅ 不需改形状）→ ⑩ P4-3a Biome 语义取证（✅ 完成，不生产）→ ⑪ P4-5 季节气候 v1.1（✅ 完成）→ ⑫ Climate Architecture Contract（✅ P4-5a 纯设计）→ ⑭ P4-5b 太阳强迫 + 海陆热响应（✅ 完成）→ **⑯ P4-5c 耦合 Solver 骨架 + 最小水循环（✅ 完成，568 PASS）∥ ⑮ Ocean Circulation D0–D1（并行线 ✅）→ P4-5d 洋流热输送（D2/D3 线，下一步主线）→ P4-5e 对齐验收 → P4-3b Biome 生产 → P4-4 Soil → ⑬ Bridge**`。
★**为什么反转**：`BiomeType` 的 **14/24 类**判据依赖月度/季节，而这些 14 类覆盖 Legacy 陆地 **~85%**；
⇒ Seasonal Climate 是 Biome 的**上游事实生产阶段**，不是"Biome 之后的附加项"。
★**冻结条款（§⑩）**：**不得在 Biome 内部从年均气候反推月度/季节事实**（否则分类层成为 source，违反 B4，并留下两套月度来源）。
★**进入重标定的门槛（用户设定）**：三件事（量级不动 / 格局变化 / 冰盖下降）须**跨 seed 全部成立**；
实测 **未全部成立**（量级那条是恒等式、冰盖那条不成立）⇒ **未达门槛**；
⑨ 进一步证明**形状本身也不需要改**（对比度落 Legacy 包络正中、阈值效应比 seed 漂移小 ~100×）⇒ **重标定关闭，直接进 P4-3**。
★**P4-2 完成后不得顺手做 Biome 的旧约束已解除**：Biome 是上游气候事实的消费者，气候两轴（温度 ⑦ / 降水 ⑧）经 ⑧b + ⑨ 复核**已稳**。
Bridge 的剩余阻断项 = **Phase 4 其余事实生产**（`Biome` / `Soil` / 季节气候）。
每项重走：消费者 → 事实定义 → 验收口径 → ④ 对照（判定不得反向修改事实）。
★**参数验收纪律（原则 14）**：禁止以 Legacy 单个世界的分布作为新世界 `R` 或冰盖阈值的目标分布；
`R` 阈值占比类验收**必须跨 seed**。

约束：`ResourceStock` 与 `fish` 商品**继续冻结**；不重写 `CivSim` 生产公式。

---

## 备选方案与否决理由

| 备选 | 否决理由 |
|---|---|
| 现在重写 Climate / Biome（新线补气候层） | 会同时改"世界事实"与"人类模拟"两个边界（违反第 13 条）；且当前无真实消费者指向"必须立刻换气候"。**应逐层迁移，不该一次重建** |
| 按提案新建 `PopulationGroup → Subsistence` | 与既有 8/9 实现重复；违反防膨胀三问第三问 |
| 一次性把新线全部接到 `GameGrid` | 新线是另一套生成器、另一套尺度、另一套降水模型（`AnnualMm` `MaxMm=2400` vs Legacy 10632 mm）⇒ 接上去 = **换世界**，`R[]` 量级与全部标定作废，且无法区分"哪个变化是谁造成的" |
| 直接把 Legacy `.mpa` 定为最终方案 | 违背项目目标"从地球演化出的自然环境一路演化出人类历史"；且 `Temp/Biome/Soil` 一旦档丢失即不可重建 |
| 把 `SubsistenceReadout` 做成模拟状态 | 无消费者（决策已由 water-filling 承担）⇒ 只是冗余状态 + 存档负担 |

---

## 后果

**正面**：

- 复用一台**已经成熟的演化机**（496 单元 + 79 地图测试），不重造 `Population → Society → State`；
- 把工程焦点从"人类社会应该有哪些系统"（已解决）转到**"地球如何成为它的输入"**（真问题）；
- 逐层迁移 ⇒ 每阶段可验收、可回退，且"哪个变化是谁造成的"始终可归因。

**负面 / 风险**：

- 桥未完成前，Human Simulation 一直**绑定 Legacy `.mpa`**——`.mpa` 是可重建性的单点；
- Phase 1–3 中任一层若"语义不等价"，会退化为"换世界"而非"接线"（须用前置四问挡住）；
- 逐层迁移周期长，期间两套世界线（Legacy / New）并存，可能出现口径混淆（须在每阶段文档钉死当前 `Source`）。

---

## 待拍板

1. ~~**④ 的产物形态**~~ ✅ 已定（2026-10-06）：先补全"逐项语义等价性"取证（`审查报告-WorldHumanInputBridge-语义等价性取证.md`），不扩写为桥设计。
2. ~~**载体契约裁决（⑥）**~~ ✅ 已定（2026-10-06）：**H3 res4 冻结**（§不变量 I1–I5）；剩余**开放子项**：
   **O1** 存档 cell 引用存 `CellId` 还是 `dense index`（存档格式，独立变更）；
   **O2** hop→km 的**具体数值**（保留 Legacy 物理含义 ≈670/1,339 km vs 新世界重标定）；
   **O3** `HumanInputGrid` 归属命名空间与契约钉子（若 `World.CivSim` 直引 `World.H3Grid` ⇒ 须显式改白名单）；
   **O4** 是否复用生产 `Ball(res4)` 实例。
3. **③ 是否现在做**：`SubsistenceReadout` 的落点（观测层 `CivSnapshot` / 独立纯函数 / 诊断 T92）——不依赖 Bridge，可先行。
4. ~~**Phase 4 起步顺序**~~ ✅ 已定（2026-10-06，用户拍板）：**先 P4-1 Temperature，P4-1 先于 P4-2**
   ——因为 `Precip` 有最危险的语义问题（`AnnualMm` 只是相对量级占位，而 CivSim 的 `PREC` 是直接进入生产公式的真实量）；
   先把"事实是什么"这条边界立起来，降水单独解决绝对量纲 + 校准。**P4-1 已完成**（§⑦）。
   **O1/O2/O3/O4 先不抢跑**（收益低于继续推进 Phase 4）：O1 存档格式独立；O2 等真正迁 res4 运行规则时定具体 km 值；
   O3 等 `HumanInputGrid` 落地前定 namespace；O4 实现阶段再决定复用 `Ball(res4)`。
5. **Phase 1 的判定口径**：Geography 接线后 `R[]` 是否重新标定？标定锚点是否仍为"陆地中位 0.1 人/km²"？
6. **Legacy `.mpa` 的退出条件**：什么条件下允许 HumanSim 不再要求 Legacy 气候输入（独立课题）。
