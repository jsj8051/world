# docs 文档索引

> 中世纪 4X 国家策略 · Godot 4.7.1 mono · .NET 8 · C# 12
> 生成与游玩解耦（地图存档，参考 DF/Civ）；双层架构：纹理层与逻辑网格同口径（每格 5 km²，用户选星球半径，n 派生）
> 2026-08-23 整理：删除已完全取代的历史文档（文明演化v1/阶段2 重构/派生状态架构化）；
> 实体命名现状：**Polity**（政体）/ **Habitation**（聚集地）——旧文中的"部落/Tribe/Settlement 类"为历史名词。

## 必读（现状文档）

| 文档 | 内容 |
|---|---|
| [架构设计.md](架构设计.md) | 总体架构、目录结构、模块职责、生成管线、存档层（.mpa v9/.cmp v17/.gmp v3 段表）、技术决策记录 |
| [architecture.md](architecture.md) | 历史架构演进记录（2026-08 重构基线） |
| [开发规范.md](开发规范.md) | 命名/目录/git 约定、headless 验证流程、性能红线、陷阱清单 |
| [索引.md](索引.md) | **代码索引**：按目录列出脚本/场景/着色器/数据/插件的职责、关键类与交互 |
| [存档段表格式设计.md](存档段表格式设计.md) | 段表容器（ChunkWriter/ChunkReader）、布局、版本判定（.mpa v9 / .cmp v17 / .gmp v3）、验证配方 |

## 世界生成（new_HexWorld —— H3 六边形新世界）

> ⚠️ **2026-09-30：动态板块模拟世界线代码已整体删除**（`scripts/new_HexWorld/` 的 Planet/UI/组装层、NewBall 场景、H3 诊断与单测、World.Bench）。本节设计文档（地壳运动系列 -01~-12）**已于 2026-10-08 随文档清退从仓库移除**，语义不再对应任何现存代码。现役世界生成 = `scripts/noise_world/`（纯噪声地形，主场景 `NoiseWorld.tscn`）；`Ball` H3 球壳数据层保留（noise_world 复用）。

## 世界生成空间（WorldGenSpace —— 新决策框架）

| 文档 | 内容 |
|---|---|
| [world.md](newdecision/world.md) | **决策原典**（2026-10-02 入库，外部建议原文）：「连续世界模型 + H3离散世界 + 多尺度模拟」——场函数 → 连续高度 → H3 采样 → 图上模拟 |
| [设计-世界生成空间-01-框架立项.md](newdecision/设计-世界生成空间-01-框架立项.md) | **框架落地记录（2026-10-02，W1 ✅）**：三层 → 代码映射（SphericalField 组合子族 / ElevationFieldStack / H3TerrainSampler / H3Hydrology）、红线 5 条（20 测试钉死）、与噪声地形线并存关系、分期 W2-W6 |
| [设计-世界生成空间-02-海陆结构.md](newdecision/设计-世界生成空间-02-海陆结构.md) | **海陆结构落地（2026-10-02，阶段 1+2 ✅）**：大陆锚点（蓝噪声）+ 加权 Voronoi 影响场 + 域扭曲/三尺度轮廓细化 → H3 投影五件套（Land/ContinentId/离海/离岸/可见海拔）；分位校准钉死海陆比；**主场景已切 `WorldGenWorld.tscn`**（res4 29.0% 陆实测） |
| [设计-世界生成空间-03-地质区域.md](newdecision/设计-世界生成空间-03-地质区域.md) | **地质区域 v2（2026-10-02，阶段 3 ✅ 二次决策重做）**：20 步 pipeline——Poisson Disk 种子（面积口径 ×random 抖动）+ SizeNoise 空间连续权重 + 连续扭曲 Region Field + Lloyd×2 + Features 全套 + 打分/全局配额/Softmax 概率选型 + RIFT 硬约束；大陆归属同系扭曲化（数学圆弧消失）；res4 实测 32 区域 |
| [设计-世界生成空间-04-山脉骨架.md](newdecision/设计-世界生成空间-04-山脉骨架.md) | **山脉骨架落地（2026-10-02，阶段 4 ✅）**：MOUNTAIN 区域 → 蜿蜒主脊 + 斜向分支（球面曲线，位置合成无漂移）→ 角距高斯包络 exp(−d²/σ²) × 脊高 → ridged 噪声乘性细化（噪声只细化不定位）；角帽预筛（增量 ~0.1 s）；逐格公式对照测试 |
| [设计-世界生成空间-05-区域地貌与高度合成.md](newdecision/设计-世界生成空间-05-区域地貌与高度合成.md) | **区域地貌与高度合成落地（2026-10-02，阶段 5+6 ✅）**：合成总公式 Height = continent + mountain + plateau − basin + regionalNoise（HeightComposer 唯一海拔出处 + 1 pass 图上平滑）；高原帽/盆地下挖区域场（盆地每陆块 ≤2 防内流盆地泛滥）；"雪山圆盘"三连根因修正（基线压低 940/区域偏移退役/峰谷比 2.2:1） |
| [设计-世界生成空间-06-特征与形态层.md](newdecision/设计-世界生成空间-06-特征与形态层.md) | **Field + Feature + Morphology 架构落地（2026-10-03）**：「Field 回答哪里容易发生什么，Feature 回答长成什么样」——TectonicField（主轴/强度连续场/强度加权选址）上收环境背景、TerrainFeature 基类（Scale3 纵横垂三元组）、MountainSkeleton 定位为 MountainRange 的 Morphology（IHeightContribution 统一口）；新特征接入 = 身份+形态+贡献三步零总架构改动 |

| 文档 | 内容 |
|---|---|
| [设计-模拟层级指导方针.md](设计-模拟层级指导方针.md) | **层级唯一权威（2026-09-22 新建）**：什么叫一层（5 条判据）、四类性质（状态/派生/过程/事件）、五条横切约束（守恒·单值性·时间一致·尺度自洽·机制无重叠）、契约四要素（量·单位·时刻·口径）、禁止清单 8 条 |


> 已删除（2026-09-22）：`设计-new_HexWorld-地壳运动-02-板块运动与地形要素生成.md`（静态路线，代码 `PlateMotion.cs` / `PlateBoundary.cs` / `LandformGenerator.cs` 已随入口 v12.3 删除）。

## 人文层设计（阶段设计——现行机制）

| 文档 | 内容 |
|---|---|
| [阶段3设计-聚集地.md](阶段3设计-聚集地.md) | **Habitation 实体 + 功能定性**：村庄/集镇/城市 = 职能条件（HasAdmin/Market/Ritual），v17 存档；camp 形态占位 |
| [阶段3设计-贸易机制.md](阶段3设计-贸易机制.md) | 物物交换（Order 55）：比较优势出口、领接触、市场条件（商路节点→集镇） |
| [阶段3设计-存储衰变机制.md](阶段3设计-存储衰变机制.md) | 随身池/粮仓（Polity.Stocks + Habitation.Stocks）、存储容量、衰变 |
| [阶段4设计-国家涌现.md](阶段4设计-国家涌现.md) | 酋邦→国家：四条件涌现（都城 IsCity / 次级中心 IsMarketTown / 贡赋 / 存续）、机制差异、崩溃 |
| [阶段5设计-军事征服.md](阶段5设计-军事征服.md) | 战争=外交状态：宣战资格、会战、吞并/割地、断交；城市要塞（IsCity） |
| [阶段6设计-古典时代机制.md](阶段6设计-古典时代机制.md) | 古典时代 5 机制解锁器（writing/coinage/standing_army/law_code/sail）——设计稿 |
| [设计-观测面板与文明记录.md](设计-观测面板与文明记录.md) | 观测面板 → 文明记录（2026-08-24 拍板；战争/主体/关系层搁置） |
| [设计-UX归航HUD改造.md](设计-UX归航HUD改造.md) | UX 布局改造：观测左上 / 潜藏底部 / 图例右下 / 时间右上（2026-08-24 拍板） |
| [设计-国家选择界面.md](设计-国家选择界面.md) | 国家选择界面 v3：地图点选 + 图层可切换（**设计稿，未拍板未实施**） |

### ★ 架构边界：自然输入桥（2026-10-06）

| 文档 | 内容 |
|---|---|
| [decisions/0005-natural-input-bridge.md](decisions/0005-natural-input-bridge.md) | **ADR-0005（已采纳 · 方向 + 边界条款 + 可行性裁决 + 载体契约冻结）**：人类演化层**不再新建**（CivSim = 已完成内核）；真问题 = **World → Human Input Bridge**；逐层迁移四阶段（Geography→Hydrology→Precipitation→Temp/Biome/Soil）；`ResourceStock`/`fish`/Riparian 冻结；`SubsistenceReadout` 定位 = 观测能力。**§不变量（I1–I5）**：CivSim 基础粒度 = H3 **res4** / 身份 = CellId / 六邻接 / hop→km / 区域不是 Cell；**§边界条款（C-6）**：B1–B4；**§迁移对照基准**：D1–D4（非清理对象）；**§执行契约**：R2/R3 + `ProductionRes_IsRes4_Frozen`；**§⑤**：`BLOCKED`；**§⑥**：载体契约方向已定；**§⑦**：Phase 4 · P4-1 温度事实已完成；**§⑧**：P4-2 降水事实（绝对量纲 mm/年）已完成 + **§⑧b** 独立佐证 A/B（判定 = **未达重标定门槛**）+ **§⑨** 路线 A：R 形状与冰盖分布评估（判定 = **不需改形状**；**原则 14**：禁止以 Legacy 单世界分布为目标）+ **§⑩** P4-3a Biome 语义契约 + **★依赖倒置冻结**（Seasonal Climate = Biome 上游事实；P4-5 已升格为 P4-3 硬前置）+ **§⑪** P4-5 季节气候 v1.1（★冻结条款 4：可达性是生产者责任，禁止改 Biome 判据换出现率；★冻结条款 5：三层验收，不得用③出现率反向驱动模型；解析判据 D⇔amp≥13、w⇔S>9/11；v1 的 6/24 类恒 0 已排除；新常数 SeasonalContinentalityScaleKm=500 与年均温 1200 口径分离并已证成）+ **§⑫ Climate 架构契约（2026-10-07 路线二次反转）**：P4-5 第一步 = 冻结 `ClimateForcing/Parameters/State/Solver` 四概念与耦合图（时间步长 = 月、洋流 = 诊断场、雪冰反馈 = v2、solver 单向），既有三模型定为**占位气候事实**不删除 + **§⑭ P4-5b 太阳强迫 + 海陆热响应已完成**（`MonthlyTemperature` 上线：monMean≡meanT、monAmpT=9.3 °C 与地球同量级；下一步 P4-5c 水循环）+ **§⑮ Ocean Circulation 架构契约（方向反转条款 7）** + **§⑯ P4-5c 耦合 Solver 骨架 + 最小水循环已完成**（568 PASS；下一步 = P4-5d 洋流热输送）|
| [审查报告-CivSim人类演化层取证.md](审查报告-CivSim人类演化层取证.md) | **完整取证（2026-10-06，纯只读）**：实体/演化阶段/生计/人口/聚落/文化/政治/入档-派生分类/自然输入来源/债务/测试诊断 + **「已实现但易被重复设计」18 项** |
| [审查报告-C4-人类演化层接线审查.md](审查报告-C4-人类演化层接线审查.md) | **C-4 接线审查（2026-10-06，纯只读）**：人类演化层 = `World.CivSim` = **领域消费者**（非事实层）；Final 层输入量 = 0、桥尚不存在；反向依赖 0 双向 / 回写 0 / 越层读取 0；重复计算 D1–D5；**契约缺口 G1–G4**；建议 R1–R5（ADR §边界条款 / §执行契约 的直接依据） |
| [审查报告-CivSim自然输入Provenance.md](审查报告-CivSim自然输入Provenance.md) | **Legacy Climate/Biome Provenance Debt**：CivSim 气候类输入全部来自遗留 `.mpa`，新线无重建路径；逐项 Source/可重建性对账 |
| [审查报告-WorldHumanInputBridge-语义等价性取证.md](审查报告-WorldHumanInputBridge-语义等价性取证.md) | **④ 逐项语义等价性取证（2026-10-06，纯只读 · 不实现 Bridge）**：15 字段按五项判据（定义/粒度/时点/边界/范围）打五标签（EQUIVALENT/ADAPTER_REQUIRED/SEMANTICALLY_DIFFERENT/SOURCE_MISSING/LEGACY_ONLY）；**结论 = 可桥接仅 2 项（Elev 等价 / LakeLevel 需适配），其余 13 项不可桥接**；导出 Phase 4 生产任务清单（Temperature/Precipitation 气候版/Biome/Soil） |
| [裁决-Bridge可行性裁决.md](裁决-Bridge可行性裁决.md) | **⑤ Bridge 可行性裁决（2026-10-06，纯裁决 · 不实现 Bridge）**：裁决 = **`BLOCKED BY MISSING WORLD FACTS + CARRIER CONTRACT UNDECIDED`**；**载体取证**（CivSim 需 {identity+topology+metric} 三件套；cell index **入档**；`H3 res3 ≈ Legacy n=64`，res4 = 7× 细化；`reachKm` 1,451→547 km ÷2.65）；结论 = Bridge 形态须为 `WorldGen facts → HumanInputGrid`；Phase 4 任务 P4-1..P4-5 + 验收口径 |
| [裁决-HumanInputGrid载体契约.md](裁决-HumanInputGrid载体契约.md) | **⑥ HumanInputGrid 载体契约（2026-10-06，纯裁决 · 方向已定）**：**H3 res4 冻结**为基础粒度（不再讨论 res3）；链路 = `H3 res4 CellId → dense runtime index → topology/metric → 区域聚合`；四维裁决（Identity/Topology/Metric/Resolution）+ **附加契约「区域不是 Cell」**；正式**不变量 I1–I5**；状态更新 = `BLOCKED BY MISSING WORLD FACTS`（载体已 DECIDED）；开放子项 O1–O4 |
| [设计-NaturalInputBridge实现设计.md](设计-NaturalInputBridge实现设计.md) | **⑬ Bridge 实现设计（2026-10-07，纯设计 · 零代码改动 · 不实现 B1 · ★Architecture Baseline v1 冻结）**：四层形态 = `WorldGen 事实层 → Bridge 投影层(World.Bridge) → HumanInputGrid → CivSim`；★**载体三项禁令**（`HumanInputGrid` **不重新生成世界事实 / 不反推缺失月度数据 / 不承载 Legacy-only 变量**）——防 Bridge 退化为"第二套 WorldGen"；★**R2 用 `ReferencedTypesDeep` 深度传递扫描** ⇒ `HumanInputGrid` **绝不能**放 `World.WorldGen.*`（`StartsWith` 会命中 `World.WorldGen.Bridge`），推荐 `World.Bridge`；★**用户拍板两条**：① **B1 不提前**（严守 `Phase 4 完成 → B0 → B1 → B2 → B3 → B4`；写设计 ≠ 写 Bridge 代码）；② **Biome/Soil 升格为 B2 重点审计项**，须**「逐类映射 + 逐项验证」**，**不得只验数值范围或总量**、必须验**类别形状与空间分布**（依据：`IsColdZone` 只认 4 类 / `TerrainCost` 只分 5 档 / `PreyFrac` 只分 3 档 ⇒ 总量全对也可能出现"山脉变畅通"的静默语义错）；**Month\* 走 9 标量 → 4 个派生量**（`WinterShare`/`MaxMonthTemp`/`MinMonthPrecip`/`WetDryRatio`），**不建 `byte[12][n]`**；B2 逐项投影口径表（River\*/Mineral/Monsoon **不投影**）+ **12 条测试钉子 N1–N12** + 新增开放项 **O5**（分消费者阈值）+ **★v1 冻结含 5 处契约修正**：① Jaccard 须**地理重映射**（§7.3.1）② R3 **届时升格** `Consumers_Are_Whitelisted`（§12.1）③ `WildCrops` **惰性持有化**（§3.3.1）④ **9 标量双证明分离**（§4.1）⑤ O5 分消费者阈值 + **N12** identity 稳定 |
| [审查报告-P4-2-独立佐证AB.md](审查报告-P4-2-独立佐证AB.md) | **⑧b P4-2 独立佐证 A/B（2026-10-06，只读诊断 · 门槛验证）**：12 seed × res2/res4 = 24 组；**判定 = 未达进入 `R`/`MiamiNpp` 重标定的门槛**。① 限制侧翻转 **✅稳健**（生产链路 12/12 seed ∈ [69.0%, 96.3%]，Legacy 26.1%）；② 冰盖下降 **❌不成立**（生产链路 3.97%~25.09%、均值 14.0%，与 Legacy 22.14% **重叠**；6.31% 是 seed42 偏暖端样本）；③「量级稳定」是**代数恒等式** `med(R) ≡ TargetMedianDensity`（24/24 组差 ≤1.4E-17）⇒ 不可作证据；真正有信息量的是**形状**（`R_raw` P95/P5 3.92~6.34、`R>2000%` 6.22~18.43、陆地 P50 580~996）。★**冰盖漂移已归因** = 世界生成器自然方差（`r(ice%, 陆≥60°%) = 0.968/0.958`，`ice/高纬陆` 1.28/1.25），**非模型缺陷**。★**口径发现**：报告 §4.5 读数来自**简化链路**（sigma 520 / 无火山 / 区域 8e6），非生产链路（4e6 / 160 / 含火山）——已证明是链路差异而非定义差异，并已回填更正块。`scripts/WorldGen/` **生产代码 0 改动** |
| [裁决-P4-3-Biome事实定义.md](裁决-P4-3-Biome事实定义.md) | **⑩ P4-3a · Biome 语义契约（2026-10-07，只读取证 · 不生产）**：①**分类语义 = 气候型 Köppen–Geiger**（沿用 `BiomeType` byte 24 值；★游戏标签 `grass`/`plain`/`mediterranean`/…→`BiomeType` 的映射必须留在 `CivSimContext.EnvMatches`，**不得**进 Biome 判据 ⇒ 守 B4、防"为 WildLivestock 设计 Biome"的反向驱动）；②**输入** = `T_ann`+`P_ann`+海陆/海拔/离岸+河湖，**不依赖 Soil ⇒ 与 P4-4 无输入循环**，★**`MonthTemp/MonthPrecip` = P4-5 前置缺口**；③**Legacy provenance 五项判据** ⇒ 预期 `SEMANTICALLY_DIFFERENT → ADAPTER_REQUIRED`（不预设结论），★附带取证：**`BiomeType.cs` 注释漂移**——注释写 `20×(T+14)`，Legacy 实际用 **`k=10`**（实测 desert 12/12、steppe **541/541** 全满足；按注释则 steppe **0/541** ⇒ `ComputeLivestock` 全空）；④**恢复条件**：**`WildLivestock` ✅可恢复，`WildCrops` 5 种卡在 P4-5**（需 `WinterShare`/`MaxMonthTemp`/`MinMonthPrecip`/`WetDryRatio`）⇒ P4-3 的真实收益**不是** WildCrops。★**24 类逐一登记**：10/24 年均可判、14/24 需季节量（覆盖 Legacy 陆地 **~85%**）。★★**依赖倒置（已冻结）**：14/24 类判据依赖月度 ⇒ **Seasonal Climate 是 Biome 的上游事实生产阶段**；**禁止在 Biome 内从年均反推月度**；P4-3 拆 `P4-3a`（✅完成）/ `P4-3b`（⛔ 阻断至 P4-5 后）。`scripts/WorldGen/` **生产代码 0 改动** |
| [审查报告-R形状与冰盖分布评估.md](审查报告-R形状与冰盖分布评估.md) | **⑨ 路线 A · R 形状与冰盖分布评估（2026-10-06，只读诊断）**：12 seed × res2/res4；**判定 = 不需要改变模型响应形状 ⇒ `R`/`MiamiNpp` 重标定关闭 ⇒ 直接进 P4-3 Biome**。① **无形状断裂**（新线 `R_raw` **P95/P5 = 3.92~6.33**，Legacy 单世界 **5.23** 落包络正中）；② **冰盖漂移 = 大陆布局非阈值**（干净纬度阶跃：`<50°` 无冰 / `65–70°` 24-24 全冻 / 方差全在 **50–60° 过渡带**；同世界扫阈值 ±1°C 只改 **0.0~1.6 pp**，跨 seed 漂移 **4.83%→25.09%** ⇒ **效应比 ≈100×**；seed21 阈值斜率 = **0.00**）；③ **无肥沃膨胀**（新线 `R>2000%` 6.22~18.43 vs Legacy 18.6，高尾**更窄**）。★**Legacy 8 条锚 5 条 ∈ 新世界包络**，出界的 2 条同源且方向为"Legacy 更宽"。★**唯一真实漂移**（`k` 吸收不掉）：`R<600%` 极差 107~125%、`R>2000%` 极差 89~94% ⇒ **R 阈值占比类验收必须跨 seed**。**§七 新原则 14**：禁止以 Legacy 单个世界的分布作为新世界 `R` 或冰盖阈值的目标分布。`scripts/WorldGen/` **生产代码 0 改动**（未动阈值） |
| [审查报告-P4-2-降水事实与provenance对照.md](审查报告-P4-2-降水事实与provenance对照.md) | **⑧ P4-2 降水事实（2026-10-06，WorldGen 事实生产）**：`PrecipitationModel.AnnualMm` 由**占位相对量级**（`MaxMm=2400`×无量纲系数）**原地升级**为**绝对量纲 mm/年事实**（`ZonalMm(lat)` × `MaritimeFactor`；`ContinentalFloor` 单一常数 → 纬度 LUT `InteriorRatio`）；实测全球均值 **1,106 mm/年**（地球 ~990）、陆格 min **120.9**（旧占位 588.9 ⇒ 曾有「无荒漠」缺陷）；**不接 CivSim / 不写 Bridge / 不重标定 `R`**；17 条护栏 + 全量 510→**517 PASS** + 地图 79 PASS + Headless res4 `meanP=1098mm/年` 零 ERROR；④ 对照判定 = `Precip` **`SEMANTICALLY_DIFFERENT` → `ADAPTER_REQUIRED`**；★★**最关键实测推翻预设**：Legacy 陆地降水是新事实 **2.5×**，但 `R_raw=MiamiNpp(T,P)` 的 **P50 几乎不动（1260 vs 1262）** ⇒ `k` 中位数再归一化 + MiamiNpp 饱和双重吸收绝对量级；变的是**限制侧（26.1%→81.3%）**与**冰盖（22.14%→6.31%）** |
| [审查报告-HumanSimulation-v0.1与CivSim重叠判定.md](审查报告-HumanSimulation-v0.1与CivSim重叠判定.md) | 提案 85% 已被现有 CivSim 实现；T90 过采判据实测（档位 A/B）、① FishPotential 落地记录 |

## 环境与反馈

| 文档 | 内容 |
|---|---|
| [气候反馈环.md](气候反馈环.md) | 气候子系统的反馈回路设计（有效） |
| [自然因素影响图谱.md](自然因素影响图谱.md) | 自然因素 → 人文影响的映射图谱 |

## 外部参考文献（学习笔记）

| 文档 | 内容 |
|---|---|
| [参考文献/](参考文献/README.md) | **噪声生成球面地图**相关外部教程中文版（2026-09-30 整理）：Red Blob 球面生成 / Red Blob 噪声地形（逐节完整编译）、Godot FastNoiseLite 全译（CC BY 3.0）、iquilez 噪声文章集（fBM·域扭曲·Voronoise·解析导数）、Azgaar FMG Wiki 翻译（MIT）；每篇附原文链接与版权状态 |

## 历史档案（保留参考——语义不再同步当前代码）

| 文档 | 内容 |
|---|---|
| [石器时代设计.md](石器时代设计.md) | ⚠️ 历史：石器时代（游群/采集/狩猎/科技）领域设计总览——"Tribe"为旧实体名，现为 Polity |
| [tectonics-port.md](tectonics-port.md) | ⚠️ 历史：tectonics.js → C# 移植方案（"等距柱状采样"段已过时，现为球面直通） |
| [tectonics-ref/](tectonics-ref/) | 原版 tectonics.js 源码参考（58 文件，CC-BY-4.0，含原 LICENSE） |
| [screenshots/](screenshots/) | 各阶段验证截图（侵蚀对照、海拔色带、洋流环、biome 等，40 张） |
| [功能清单与结构重规划.md](功能清单与结构重规划.md) | ⚠️ 历史：noiseworld 时期 `scripts/` 全量清单与重规划（结构已多轮演变） |
| [架构设计-历史-2026-08.md](架构设计-历史-2026-08.md) | ⚠️ 历史：2026-08 架构设计存档（语义冻结于当时快照） |
| [审查报告-2026-10-03.md](审查报告-2026-10-03.md) | ⚠️ 历史：noiseworld 全面审查（M-1..M-10 债务清单，多数已随清退/重构消化） |
| [审查报告-C1-SphereLines定性.md](审查报告-C1-SphereLines定性.md) | 历史：C-1 球面线条渲染定性 |
| [审查报告-C2-ParallelLoops取证.md](审查报告-C2-ParallelLoops取证.md) | 历史：C-2 并行循环取证 |
| [审查报告-C3-悬空shader定性.md](审查报告-C3-悬空shader定性.md) | 历史：C-3 悬空 shader 定性 |
| [审查报告-D-Legacy逐项决策.md](审查报告-D-Legacy逐项决策.md) | 历史：D 步 Legacy 逐项决策（Goldberg 闭包清退依据，2026-10-06 已执行） |
| [审查报告-测试夹具依赖审计.md](审查报告-测试夹具依赖审计.md) | 历史：测试夹具依赖审计 |
| [搬迁矩阵-WorldGen六子层.md](搬迁矩阵-WorldGen六子层.md) | 历史：WorldGen 六子层搬迁矩阵（搬迁已完成） |
| [迁移评估报告-noise_world-to-worldgen.md](迁移评估报告-noise_world-to-worldgen.md) | 历史：noise_world → worldgen 迁移评估 |
| [迁移评估报告-strategy-to-world.md](迁移评估报告-strategy-to-world.md) | 历史：strategy → world 迁移评估 |
| [ADR-0001](decisions/0001-test-and-split-decisions.md) · [ADR-0002](decisions/0002-service-layer-design.md)（**已作废**） · [ADR-0003](decisions/0003-diag-scene-base.md)（仍有效） · [ADR-0004](decisions/0004-log-service-migration.md)（**已作废**） | 历史 ADR：测试基建与拆分 / 服务层（EventBus·LogService·ArchiveService）/ 诊断场景基类 DiagSceneBase / 日志全量收编 LogService。★2026-10-09 `World.Services` 整删 ⇒ **0002 / 0004 作废**（`EventBus`/`ArchiveService` 早随旧 UI 清退，`LogService` 零生产消费者）；ADR-0003 仍有效（`DiagSceneBase` 保留）。见 `裁决-Services删除.md` |

> 已删除（2026-08-23，被完全取代）：`文明演化v1.md`、`阶段2设计-一格一实体重构.md`、`阶段3设计-派生状态架构化.md`。

## 快速入口

- 主场景：`res://scenes/core/NoiseWorld.tscn`（project.godot main_scene；noise_world 纯噪声世界）
- 生成器：`res://scenes/core/MapGen.tscn`（headless：`--headless --quit-after 400`）
- 查看器：`res://scenes/core/MapViewer.tscn`（键盘切图层）
- 诊断场景：`res://scenes/diag/`（★2026-10-09 大扫除后仅剩 **1 个**：`H3SmokeDiag.tscn`，headless 可跑——见 `裁决-Services删除.md`）
- 单元测试：`dotnet test tests/World.Tests/World.Tests.csproj`（new_HexWorld/老树全量）

> 注：`docs/` 含 `.gdignore`（Godot 不导入本目录）；截图只作记录，非游戏资源。