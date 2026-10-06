# 审查报告：Human Simulation v0.1 提案与 CivSim 现状重叠判定

> 2026-10-06。审查对象：《Human Simulation v0.1 — Natural Resource → Subsistence → Population》设计草案。
> 方法：代码取证（`scripts/CivSim/`）+ 提交历史取证（`git log --grep`）+ 既有文档对账（`docs/石器时代设计.md` 等）。
> 性质：**判定 + 建议 + 实测，未改任何 CivSim 生产代码**。结论供拍板。
> **状态更新（2026-10-06 晚）：§3.2「过采判据实测」已执行完毕，结果见 §五 ⇒ 档位 A/B，`ResourceStock` 维持冻结。**

---

## 〇、一句话结论

提案的 **约 85% 已由 `scripts/CivSim/` 实现，且形态强于提案**（等边际闭式分配 > 份额×惯性启发式）。
唯一真正新增的一环——**自然资源存量 `ResourceStock` + 再生 + 扣减**——恰恰是 **2026-08-17 用户主动删除的**，删除理由原文：

> "砍存量（**人口远低于承载，可持续产量近似成立**）"
> —— commit `4345b84` 「土地挂钩 v9：砍存量再生」

⇒ **在"把四组数据结构定死"之前，必须先实测这个近似是否仍然成立。** 不成立才建；成立则建了也没有消费者。

⇒ **已实测（§五）**：三组参数两项自校验全 PASS；**没有任何区域、任何分位数出现 `Harvest/Carry` 稳定 > 1**；
可居陆地未归属率 47.2%~83.2%；判据落 **档位 A/B**。**⇒ 近似仍成立，`ResourceStock` 冻结。**
⇒ **① FishPotential 已落地并独立验收**（§七，5+4 项断言全 PASS）；唯一无争议的真缺口已补齐。
⇒ **② Riparian（`WorldGen` 分类器）留作下一笔独立变更**（§5.5 立案）；`fish` 商品注册另开一笔（触存档格式，§7.4）。


---

## 一、逐项对账

| 提案概念 | 现有实现 | 位置 | 判定 |
|---|---|---|---|
| `ResourcePotential` | `CivSimContext.R[]` = Miami NPP × 水因子 × k（静态场，人/km²，先于人类存在） | `CivEngine.Run` → `BuildLayer1`（L63） | ✅ 已有 |
| `PlantFoodPotential` / `AnimalPotential` | `PreyFrac(biome)`——草/萨瓦纳 0.7 猎物、密林湿润 0.35（其余 0.5）；浆果 = 1−占比 | `CivSimContext.cs:662` | ✅ 已有（**派生函数**，非独立场） |
| `FishPotential` | **无**。`WaterRich` 只认 Riparian / LakeLevel>0 / 邻湿地；**海洋邻接给 0 加成** | `CivSimContext.cs:321` | ❌ **真缺失** |
| `ResourceStock` / `Regeneration` | **不存在**——`ResourceModel`/`InitStock`/`RegenStocks`/`Cap`/扣减整层已删；`.cmp v9` 把 Stock 段原位换成 `Cultivation` | commit `4345b84`、`.cmp v9` | ⛔ **已拍板删除** |
| `Cultivation`（土地占用） | `Cultivation[c]` 场；采集 ×(1−开垦)、农田 ×开垦、草场被农田直接替代 | `CivSimContext.cs:37` | ✅ 已有（= 现有"竞争性占用"载体） |
| `ProductionProcess` | 采集(猎+果) / 畜牧 / 农田；`IsFarming` bool + `Livestock` 能力门 | `ModeModel.cs`、`AllocateAndProduce` | ◐ 有，但采集未拆 plant/animal，**且无 fishing** |
| `Labor` 分配 | **等边际闭式 water-filling**：凹化 `F_i(n)=P·n/(D_i+n)`、LF 两档 `LaborFrac=0.1` / `LaborFracFarm=0.2`、段 A/段 B 分段解 | `CivSimContext.cs:857-960` | ✅ 已有，**形态强于提案** |
| `ProductionSite = MobileGroupRange` | 领地 = 影响力场归属格集（加权 Voronoi，`InfluenceRadius=6`）+ `ProductionWeight(d)` LUT | `InfluenceModel.cs`、`CivSimContext.cs:650` | ✅ 已有 |
| `PopulationGroup` | `Polity`（`Id`/`Cell`/`P`/`TechKeys`/文化宗教份额）；一格一实体 | `Entities/Polity/` | ✅ 已有（命名不同） |
| `PopulationInventory` / `FoodStock` | `Polity.Stocks`（随身池）+ `Habitation.Stocks`（粮仓）；`CommodityTable` 6 商品（谷物/浆果/猎物/皮革/羊毛/秸秆），**衰变率分层** | `CommodityTable.cs`、`Polity.cs:55` | ✅ 已有，且比"统一 Food"更细 |
| `SubsistenceProfile`（份额） | 无显式份额；份额由 water-filling 的 `n_i → F_i` 分量**涌现** | `AllocateAndProduce` | ◐ **缺"读出"，不缺"行为"** |
| `FoodSecurity` / 人口增长 | `e=F/P`（`EnergyModel` O10）、`P ×= exp(r(1−D/F))`（`GrowthModel` O20）、`StarveMult` 僵尸修复 | `EnergyModel.cs`、`GrowthModel.cs` | ✅ 已有 |
| `YearTick` 九步 | 注册表 `Order 0 → 80`，22 个模型 | `CivModelRegistry.StoneAge()` | ◐ **仅第 1、5 步是新的** |
| 五层职责边界（World Facts → … → Population） | `GameGrid` 只读 → `BuildLayer1` → 22 模型按 Order 执行，**无跨系统直调** | `CivEngine.Run` | ✅ 已有，无需新建 |

**归并结论**：提案 = 现有系统 + 1 个真缺口（渔业/海岸）+ 1 个真问题（过采）+ 2 个形式退化（份额启发式、显式 Labor 池）。

> ⚠️ **顺带查出、与提案无关但影响同一条公式的独立缺口**（2026-10-06 取证）：
> **`BiomeType.Riparian` 在整个代码库中只被"读"、从不被"写"**。`scripts/WorldGen/` 下 `grep Riparian` 零命中；
> 全仓只有 6 处读取（`BiomeColors` / `Domain` / `WildCropsSystem` / `CivSimContext`）+ 2 处测试断言。
> ⇒ 后果：`WaterRich`（L321）与 `IrrigFactor` 的空间依赖**只剩 `LakeLevel>0` 一条路径**，
> `PreyFrac` 的 Riparian 分支恒不命中；实测中 **Riparian 格 = 0**（见 §五 5.5）。
> ⇒ 这不是"提案新增了什么"，而是**既有的河岸生态位从未被世界生成器点亮**——应作为独立缺陷单列，不要混进提案对账。

---

## 二、三处必须指出的问题

### 2.1 倒果为因：现象由 P/K 量级决定，不由"有没有建存量"决定

提案 §二十四 的目标是观察"资源过度利用区 / 人口衰退区"。但**这个现象能否出现，不取决于你是否建了存量机制，而取决于 `Harvest / Regeneration` 是否可能 ≥ 1**。

而现有实测恰恰相反：

- 5 km² 口径 n64 全演化（`regress_v9_n64.mpa`）：501 实体 / 7848 人 / **峰值密度 10.8 人/km²**
- `R` 标定量级：陆地中位数 0.3 人/km²（Binford 狩猎采集密度锚）
- commit 原文：**"人口远低于承载，可持续产量近似成立"**

若 `P ≪ K` 全局成立，则 `Regen > Harvest` 恒成立 ⇒ `Stock` 恒停在接近初值处 ⇒ **建出来的子系统在整场演化里一次都不会起作用**。

这正是本项目"防膨胀三问"第三问要挡的东西：**现在有真实消费者吗？**

> ⚠️ 注意这里**不是**技术不可行，而是**近似成立导致的冗余**。所以重新开放的门槛是"这个近似现在失效了吗"，不是"存量模型好不好写"。

### 2.2 YearTick 第 4/5 步拆开会制造两本账

提案的顺序：

```
4. Execute Production      → 算出 Harvest = 200
5. Extract Natural Resources → 库存只剩 120
```

若生产先算出 200、第 5 步再去扣库存时只剩 120，则"食物守恒"与"资源守恒"**必有一条破**（产出用了 200 记账，资源账只扣了 120）。

正确形态：**收获与扣减必须同一次计算**，不存在"先生产后提取"：

```
harvest = min(desired, available)
F := harvest        ← 产出即扣减后余额，一本账
```

### 2.3 现在新增 n 格状态场，是在给一条"靠人工对齐才不漏"的管子上加债

> **⚠️ 2026-10-06 实测更正**：本节初稿写的"T04 当前 FAIL 且已搁置"**与实测不符**，据实修正如下。
> 实测（`--arch=user://maps/map_seed42_n64_r128.mpa`，无 `--only`）：
> **T04 PASS**（`IsFarming 入档验证`）+ **T04b PASS**（`SettleDerived幂等=True 读档≡内存=True`），全量 **79 PASS / 0 FAIL**。
> 即"读档续跑无分叉"在当前基线上**是绿的**。此前"FAIL / 分叉点为科技发明 Rng"的记录已不成立（或已在后续阶段修复）。

但 `T04_Continuation` 的**测试形态本身**暴露出残留风险，PASS 不等于"全字段已入档"：

- 它在续跑前**手工对齐了三个不入档的凝聚频率守卫**（`TerritoryLastRebuild` / `ChiefdomLastEval` / `AbsorptionLastEval`），
  注释原文："守卫不入档——读档端 -1（首 tick 必凝聚），内存端是演化末值（错位 N tick）→ 凝聚时刻错位 → 分叉"。
- 它**只续跑 20 tick**（不是整场演化）。
- 比较用 `EntitiesEqual`，其字段覆盖面未做"全字段入档审计"。

⇒ 结论从"管子在漏水"**降级为**"管子目前不漏，但靠人工对齐补丁兜底，且只被短程 20 tick 验证过"。

存档纪律不变：**新状态字段必须入档，或可由持久字段确定性重建**。
新增一个 `float[n]` 的 `ResourceStock` 场 = 在一条靠人工对齐补丁维持一致的管子上再压 n 个字段。
⇒ **建议顺序仍是"先把不入档的守卫/字段收敛（或明确豁免并加长续航跨度），再考虑加状态"**——但这**不再是本轮实测的阻塞项**
（本轮 T90 **零新增状态**，不触碰存档）。

---

## 三、建议路径

### 3.1 立刻可做（有真实消费者、零新增状态）

**(a) 潜能层补鱼 —— 唯一真正的自然层缺口**　→ **✅ 已落地（2026-10-06，见 §七）**

现状：`WaterRich` 只认 Riparian/Lake，**海洋邻接对 R 无任何贡献**；无鱼商品。故提案目标中的"沿海人口带 / 河流人口带"**当前在模型里不可能出现**。

最小做法（不开新层、不加状态）：新增派生函数 `FishFrac(biome, isCoast, isRiparian, hasLake)`，并入既有 `AllocateAndProduce` 的采集档潜在——与 `PreyFrac` 完全同一处、同一种写法。

```text
采集档潜在 pc = R×A×w×[ (1−0.5·开垦)·PreyFrac
                      + (1−开垦)·(1−PreyFrac−FishFrac)
                      + FishFrac ]        ← 新增项
```

同时 `CommodityTable` 注册 `fish`（Food，`BaseDecay` ≈ 0.45——鲜鱼接近浆果）。

**(b) 生计构成改为派生读出（不是状态）**

把 `AllocateAndProduce` 已算出的 `fBerry / fMeat(=fHunt−fBerry) / fHerd / fFarm / fFish` 归一化成 `SubsistenceReadout`：**不存档、不进决策、不出现在任何公式右侧**。

这样提案 §十二 的"生计分配 `Σ ≈ 1`"测试项立刻成立，而决策仍由等边际闭式承担——**不要用 `NewShare = Old×0.8 + OppShare×0.2` 去替换 water-filling**，那是形式退化（启发式 vs 闭式最优）。

### 3.2 先实测（决定"要不要建存量"）— **已执行，结果见 §五**

在现有 n64 / regress 图上跑满演化，统计两个量的分布：

1. **格级占用率** `D[c]·? / Y_pot[c]`——即每格实际采集量 ÷ 静态潜在
2. **实体级饱和率** `P·c / Carry`（`Carry` = 领地承载）

判据：

- 若 `max(占用率) ≪ 1` 且无显著区域趋近 1 ⇒ **近似仍成立，冻结存量议题**，记档为"已实测证明无消费者"。
- 若存在显著区域占用率趋近/超过 1 ⇒ **存量再生才有消费者**，转入 §3.3。

这也是提案 §二十四「实验成立」的**真实验收**——把它作为先决条件，而不是把机制先建起来再赌现象出现。

### 3.3 若实测成立：存量的最小形态（避免重蹈覆辙）

**不要**建独立资源子系统，也不要新增独立 tick 段。把存量做成既有产出公式上的**一个逐格乘子** `D[c] ∈ (0,1]`：

```text
F_采集 = Σ_领地 [ R×A×(1−开垦)×w × D[c] × (n / (D_i + n)) ]
D'[c]  = clamp( D[c] + g·(1−D[c]) − h·(harvest[c] / (R[c]·A)), 0, 1 )
         g = 再生率，h = 开采率
```

关键性质：**`D[c]` 在 tick 开始时是已知标量** ⇒ 等边际闭式分配（§3.1 前提）**完全不受影响**。

> 这正是 2026-08-17 那次能安全回退的根本原因：**砍的是"账"（独立存量场），不是"式"（等边际分配）**。所以重新引入时也必须只加"账"、不动"式"。

附加约束：

- `D` 必须入档（4B 或量化 1B/格），且**必须排在 T04 结清之后**
- **只给"易过采"分量开 `D`**（猎物 / 鱼）。农业已有自己的土地账（`Cultivation`），**不要叠第二套占用账**
- 冲突掠夺语义会再次变化（v9 曾因"无存量可抢"改为纯控制权）——若要保留掠夺回收益，需重新设计

---

## 四、不需要新建的部分（避免重复造层）

| 提案要求 | 状态 |
|---|---|
| "Potential / Stock / ProductionOutput 三分，边界以后会救我们很多次" | 现有正是这个三分：`R`（Potential）/（Stock 已砍）/ `Stocks`+`Cultivation`（Output 与占用）。**缺的只有中间一环** |
| "避免 ProductionSystem ↔ PopulationSystem 互相调用形成架构纠缠" | 已满足——22 个模型由 `Order` 排序、`CivEngine` 单点驱动，无跨系统直调 |
| "PopulationGroup / PopulationInventory / SubsistenceProfile" | 分别已由 `Polity` / `Stocks` / water-filling 涌现份额承担 |
| "第一版不要模拟个人，只模拟群体" | 已满足（`Polity` 是群体，无个体） |
| "世界事实不被人类模拟反向修改（第一版全部只读）" | 已满足且是硬验收项（**T01 自然层零改动**，逐格比对） |

**提案 §二十一"第一版只做这些对象"中的 11 个对象，只有 1 个（`ResourceStock`）是新的，其余 10 个已有对应实现。**

---

## 五、T90 过采判据实测结果（2026-10-06，已执行）

> 用户拍板："先做第 3 项过采判据实测，暂不改代码。" 本节即该项交付。
> 工具：`scripts/Diagnostics/CivSimDiag.OverHarvest.cs`，触发方式 `--only=T90`（**不进全量默认**）。
> 性质：**纯只读**——不新增状态、不动等边际 water-filling、不碰存档格式、不消耗 Rng。

**运行方式（可复现）**

```bash
dotnet build -t:Rebuild
GODOT=/d/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe
ARCH=user://maps/map_seed42_n64_r128.mpa     # n=40962 格，格面积 5.00 km²，陆地 R 中位 0.100 人/km²
$GODOT --headless --path . res://scenes/diag/CivSimDiag.tscn -- \
       --arch=$ARCH --only=T90 --seed=42 --origins=3
```

**两项自校验（三组参数全 PASS，测量有效性的前提）**

| 自校验 | 内容 | 结果（三组） |
|---|---|---|
| T90a | 自驱 tick 循环 ≡ `CivEngine.Run`（同 seed 同态） | tick 153/150/164、实体 112/403/145、人口 4677.0/10624.0/3077.7 **逐位相等** |
| T90b | 逐格重算 ΣF ≡ 实体缓存 ΣFLast（相对误差 < 1e-3） | **5.92e-8 / 3.92e-8 / 6.15e-8** |

> ⚠️ 采样点必须在 **Harvest 边界**（Order ≤ 9 跑完、Order > 9 之前）。首版放在 tick 末尾，
> 因 Order 45 领地重算 / Order 80 分裂迁移会改变领地与实体集合，相对误差达 **1.42E-001（FAIL）**；
> 改到 Harvest 边界后降到 5.9e-8。**这个数字本身就是"必须在哪采样"的证据。**

### 5.1 三组参数总览

| 参数 | 可居陆地 | 归属格 | 未归属 | 归属占生态 Σ(R·A) | 开垦不可逆损失 | ≥0.95 格 | 全程 P99.9 峰值 | 档位 |
|---|---|---|---|---|---|---|---|---|
| seed42 / origins3 | 9093 | 1527 | **83.2%** | 19.2% | 9.9% | 7/1527（0.5%） | 0.995 | **A** |
| seed7 / origins5 | 9093 | 4801 | **47.2%** | 63.1% | 8.4% | 67/4801（1.4%） | 0.990 | **B** |
| seed20260806 / origins2 | 9093 | 1830 | **79.9%** | 22.7% | 5.9% | 8/1830（0.4%） | 0.990 | **A** |

对应总量（末态）：`ΣHarvest/ΣCarry` = 0.819 / 0.838 / 0.861；`ΣPop/ΣCarry` = 0.782 / 0.812 / 0.828。

### 5.2 三个分布（末态单 tick 干净样本；seed=42/起源3 为例）

| 分布 | P50 | P95 | P99 | P99.9 | max | 读法 |
|---|---|---|---|---|---|---|
| **Harvest / Carry** | 0.880 | 0.915 | 0.935 | 0.985 | 0.995 | 凹化固定点 `P*=F(P*)` 的**结构产物**，非稀缺证据 |
| **Harvest / Regen** | 2.630 | 14.454 | 25.119 | 43.652 | 53.511 | 农业把单格产出放大到 R 的数十倍 + 距离权重约定 |
| Harvest / Regen（**仅无农田格**） | 2.512 | 2.754 | 2.754 | 3.020 | **2.922** | **≤ max(ProductionWeight)=3.0** ⇒ 纯权重，不是生物量主张 |
| Population / Carry | 0.794 | 1.096 | 1.445 | 6.310 | 20.135 | 领地承载饱和的固定点 + 人口再分配代理 |

**关键**：把农田格剔除后，`Harvest/Regen` 的 max 掉到 **2.9**，正好等于 `ProductionWeight` LUT 的上界——
⇒ 那个 "≫1" **完全由两件与稀缺无关的事造成**：① 农业放大（`AgriBase` 是 R 的数十倍）② 领地距离权重（最多 3×）。

### 5.3 区域 / 生物量分组（回答"全球平均会不会吃掉局部过采"）

**区域陆地格构成**（与参数无关）：内陆 6147 / 湖泊 793 / 海岸 2153 / **河岸 0**（见 5.5）。

| 区域 | 归属率（origins3 → origins5） | Harvest/Carry P50 | P99.9 | max | 出现 ≥0.95 的格 |
|---|---|---|---|---|---|
| 内陆 | 19% → 55% | 0.880 | 0.975 | 0.974 | 2 / 35 |
| 湖泊 | 15% → 65% | 0.800 | 0.925 | 0.922 | 0 / 11 |
| 海岸 | 11% → 42% | 0.900 | 1.000 | 0.995 | 5 / 21 |
| 河岸 | — | n=0 | — | — | — |

生物量分组（高/低以陆地 R 中位 0.100 为界，低 541 格 / 高 986 格）同样**无任何一组 P99.9 显著越过 1**（高 1.000 / 低 0.975）。

> **结论：分组没有改变结论。** 无论按区域还是按生物量，**没有任何格子、任何分位数出现 `Harvest/Carry` 稳定 > 1**。
> 唯一的 ">1" 来自 `Population/Carry` 的高尾（max 20.1），即"某领地人口超过其承载"——
> 但那是**劳动稀释 + 饥饿/增长的自我修正**，不是资源耗竭信号，与 `ResourceStock` 无关。

### 5.4 结构事实（先于任何数值的第一结论）

1. **`Harvest ≤ Carry` 恒成立**（凹化 `F_i = p·n/(LF·p+n)` 单格产出不可能超过其潜在）⇒
   **「取用超过潜在」在现有模型里不可表达**。因此"过采"在本模型中**只能以"空间前沿耗尽"的形式出现**，不可能以"存量被挖空"的形式出现。
2. `Harvest/Regen > 1` 在无农田格上恒 **≤ 3.0** ⇒ 值是**领地距离权重约定**，非生物量主张。
3. 开垦造成的采集/牧场潜在不可逆损失占已利用潜在 **5.9%~9.9%** ⇒ 是**标定产物**（`Cultivation` 直接减项），非涌现逼近。
4. 人口终值（末态）：**4677（起源3）/ 10624（起源5）/ 3077.7（起源2）**，全场 15k~16.4k 年 —— **始终远低于全球承载**。
   时间序列单调上升、无崩塌段（`t=0: 30 → t=150: 4517`，起源3），**没有"资源耗竭 → 人口衰退"的形态**。

### 5.5 顺带查出的独立缺口：`BiomeType.Riparian` 从未被写入

实测直接可见：**`Riparian biome 格 = 0`**（而 `LakeLevel>0 格 = 180`）。

取证：

```bash
grep -rn "Biome\[[^]]*\] *=" scripts/ --include=*.cs      # 只有 载入/测试构造/== 比较，无写入
grep -rn "Riparian|BiomeType.(HotDesert|ColdDesertKoppen)" scripts/WorldGen/ --include=*.cs   # 零命中
grep -rn "Riparian" --include=*.cs .                     # 6 处读取 + 2 处测试断言，无写入
grep -rln "Koppen|Köppen" scripts/                       # 只命中 3 个读取方，无分类器
```

⇒ `scripts/WorldGen/` 下**没有任何 Köppen / Riparian 分类器**；`BiomeType.Riparian = 13`（`Domain/BiomeType.cs:17`）定义了但没人生成。

**后果**：
- `WaterRich`（`CivSimContext.cs:321`）只认 `Riparian || LakeLevel>0 || 邻湿地` ⇒ **只剩 Lake 一条路径**；
- `IrrigFactor`（灌溉加成）对"沿河格"恒不生效；
- `PreyFrac` 的 Riparian 分支恒不命中。

**这与提案无关**，但正落在 §3.1(a) 要改的同一条公式上 ⇒ 应在做 `FishPotential` 的**同一次**顺带补上（同属"Potential 完整性修补"），**不要**开新层。

### 5.6 判据结论

用户给定的三级判据，实测落到：

- **档位 A（`max ≪ 1`）**：seed42/origins3、seed20260806/origins2 — 过半可居陆地无人利用（83.2% / 79.9%）+ 模型结构上不可表达过采 ⇒ **冻结 Stock 议题**。
- **档位 B（局部长期接近 1）**：seed7/origins5 — 未归属 47.2%，前沿正在收缩；但开垦损失仍为个位数、**无任何区域出现稳定 > 1** ⇒ **"存在潜在约束，值得设计 Stock，但先继续观测"**，而不是现在就建账本。
- **档位 C（稳定 > 1 / 持续下降）**：**未在任何一组参数、任何一个区域、任何一个分位数上出现。**

⇒ **决定性证据不是"接近 1"，而是"从未越过 1"。** 按用户"不要简单用 `> 1`、要看连续多年"的要求，
本工具同时给出**全演化 31 个采样 tick 的池分布**与**末态单 tick**：两者的 `Harvest/Carry` P50/P95 一致（0.88/0.92），
`≥0.95` 的格在池中占 383/36711（**1.0%**）——**没有随时间的持续攀升**。

⇒ **判定：`ResourceStock` 目前没有真实消费者，维持 §〇 的"已拍板删除"状态。**
下一步按用户既定路线做 **① FishPotential（能力补全，零新增状态）**。

---

## 六、待拍板（更新）

1. ~~**存量再生**：直接实测，还是判定"近似已失效"直接进 §3.3？~~
   → **已实测：档位 A/B，无一区域越过 1 ⇒ 维持冻结**。§3.3 的 `D[c]` 最小账本**暂不实现**，仅留档备查。
2. **渔业**：是否按 §3.1(a) 立即落地（`FishFrac` + `fish` 商品）？
   → **已落地 FishFrac 部分**（§七，用户拍板 Fish 与 Riparian 分开做）。**`fish` 商品注册故意延迟**（触存档格式，见 §7.4）。
   **待拍板**：② Riparian（WorldGen 分类器）何时开？`fish` 商品是否单开第三笔？
3. **T04**：~~是否把"结清读档续跑分叉"提到新增任何状态字段之前？~~
   → **实测更正：T04/T04b 当前均 PASS（79P/0F）**，不是 FAIL。残留风险是"靠人工对齐三个不入档的凝聚守卫 + 只续跑 20 tick"。
   问题改为：**是否值得把 T04 的续跑跨度加长 / 收敛不入档守卫**（作为"将来真要加状态字段"的前置）？
   本轮实测**零新增状态**，故**不阻塞**当前路线。

---

## 七、① FishPotential 落地记录（2026-10-06，已完成）

> 用户拍板：**Fish 与 Riparian 分开，不并成一次改动，但可连续完成**。理由（原文摘要）：
> "FishPotential ↓ CivSim / Resource Potential 是现有 `CivSimContext.R[]` 的能力补全，属 CivSim 内部的零状态扩展；
>  Riparian ↓ WorldGen / Biome classification 是 WorldGen 世界事实层的缺口修复，会改变下游读到的自然环境事实，影响面明显更大。"
> ⇒ 本节点只做 ①；② Riparian 另开一笔（§5.5 已立案）。

### 7.1 改动清单（严格单一边界）

| 文件 | 改动 | 性质 |
|---|---|---|
| `CivSim/Engine/CivSimContext.cs` | `FishFracCoast/Lake/Riparian` 常量；`FishFrac(biome,isCoast,isRiparian,hasLake)`；`ForageShares`（三分，和≡1）；`LakeFishAccess`；`ForageSharesAt`；`AllocateAndProduce` 采集档三分 + `FFishLast` 输出 | **能力补全** |
| `CivSim/Entities/Polity/Polity.cs` | 加 `FFishLast`（派生缓存，**不存档**） | 派生字段 |
| `CivSim/Mechanics/Society/HarvestModel.cs` | 分量归零清单加 `FFishLast` | 同式对称 |
| `CivSim/Engine/CivEngine.cs` | `RecomputeProduction` 分量归零清单加 `FFishLast` | 同式对称 |
| `CivSim/Tables/CommodityTable.cs` | `meat` / `leather` **扣除水产**（防鱼被双计为猎物） | 记账一致性 |
| `Diagnostics/CivSimDiag.FishPotential.cs`（新） | T91 独立验收 | 诊断 |
| `Diagnostics/CivSimDiag.cs` | `--only=T91` gate（不进全量默认） | 诊断 |
| `tests/World.Tests/CivSimMechanics2Tests.cs` | +4 个单测 | 测试 |

**明确未动**：`WorldGen/`（零改动）、`WaterRich`、`IrrigFactor`、`PreyFrac`、存档格式与版本号（仍 v17）、
`CommodityTable` 的**商品目录**（未加 `fish`，见 §7.4）、`CivSimContext` 新增**状态场**（`FFishLast` 是派生缓存）。

### 7.2 关键设计

**(a) 预留四参签名，当前零依赖 Riparian**（用户指定）：

```csharp
FishFrac(biome, isCoast, isRiparian, hasLake)   // 四参预留
调用方实参：isRiparian := (biome == BiomeType.Riparian)   // 当前恒 false（Riparian 格 = 0）⇒ 不参与输出
           hasLake     := LakeLevel[c] > 0 || 邻格有湖     // 湖岸人口也能捕鱼（与 WaterRich 邻湖语义一致）
多条件并存取 max（不叠加）：海岸 0.25 / 湖泊 0.20 / 河岸 0.15（预留）
```
⇒ ② Riparian 落地后，同一行实参**自动点亮** `isRiparian` 分支，**接口无需重构**。

**(b) 三分不变量**：`猎物 + 浆果 + 水产 ≡ 1`。水产自**浆果份**划出（不追加总量），且**对开垦免疫**
（渔场不被农田直接替代）——这是"沿海/湖岸人口在农业后仍保有水产食物来源"的机制来源。

- 采集潜在 `pc = R·A·w·[(1−0.5·开垦)·猎物 + (1−开垦)·浆果 + 1·水产]`

**(c) 退化解（代数证明 + 实测双证）**：

```text
pc_新 − pc_旧 = (1−0.5k)·p + (1−k)·b + f − [(1−0.5k)·p + (1−k)(1−p)]
             = (1−k)(1−p−f) + f − (1−k)(1−p) = f·k          （p=猎物份, b=浆果份, f=水产份, k=开垦）
```
⇒ `f = 0`（纯内陆）时**逐格精确退化**；`k = 0`（未开垦）时**总量不变**（水产只重划分）。
实测残差 **5.96E-008**（= float 机器 epsilon，T91b）。

**(d) 商品分量去重**：`FLast = FHunt + FFarm + FHerd` **不变**（水产含在采集分量内 ⇒ 能量模型不动）；
但 `FHuntLast` 现在含水产 ⇒ `meat`/`leather` 必须扣除，否则鱼被同时计为猎物。
`FFishLast = 0` 时两条 lambdas 逐位退化为旧式（T91e 实测残差 **0.00E+000**）。

### 7.3 独立验收（用户给定口径逐条对照）

| 用户口径 | 验收载体 | 实测 |
|---|---|---|
| 海岸 FishPotential > 0 | T91a + 单测 `FishFrac_CoastLakeInland_Semantics` | ✅ 沿海**有鱼** |
| 湖泊 FishPotential > 0 | 同上（含**邻湖格**） | ✅ 湖泊**有鱼**（湖水格 180 + 邻湖格 1025） |
| 纯内陆不凭空产鱼 | T91a + 单测 `Harvest_FishPotentially_CoastVsInland` | ✅ 内陆产鱼 = **False**；`FFishLast == 0` |
| water-filling 不变 | T30 / T38 / T39 全绿；T91 只改**分量分解** | ✅ 未改动（同 LF 两档、同凹化闭式） |
| 退化解 | 单测显式断言 + T91b 恒等式 | ✅ 开垦=0 时海岸 ≡ 内陆 FLast |

**地图级读数（n=40962，seed=42/起源3，`--only=T91`，5 PASS / 0 FAIL）**：

```text
[格级] 陆地格=11679 沿海=2645 湖水格=180+邻湖格=1025 河岸=0 内陆=7829 有水产格=3850
[T91b] max|三分和−1| = 5.96E-008      max|pc新−pc旧−水产·开垦| = 5.96E-008（理论 0）
[T91c] 末态 tick=153 实体=103 产鱼实体=65/103 有水产领地格=385 ΣFFishLast=163.5 ΣFLast=4296（占 3.81%）
[T91d] max(FBerry+FFish−FHunt) = 0.00E+000        [T91e] max|FLast−(FHunt+FFarm+FHerd)| = 0.00E+000
```

**全量回归**：单元测试 **496 PASS / 0 FAIL**（含新增 4 项）；地图 T 套件 **79 PASS / 0 FAIL**；
`--only=T90` 复核（见 §7.5）。

### 7.4 `fish` 商品注册：**明确冻结**（→ Future Economy Integration）

报告 §3.1(a) 原稿写过"同时 `CommodityTable` 注册 `fish`（Food，`BaseDecay≈0.45`）"。**用户拍板：现在冻结论此项。**

理由——它触到**第三个边界（存档格式 + 经济系统）**：

- `CommodityTable.Count` 决定 **Stocks 段字节数**（6×4B=24 → 7×4B=28）；
- 连带需要：`Version` 17→18、`CivMapArchive.Peek` 的硬编码 `183`（含 Stations24）→ `187`、注释与 T19 口径同步；
- 且用户本次明确要求"**不新增 Stock**"。

而当前形态 `水产 → FLast → 直接供养人口` 在本模型里**自洽**：无库存、无贸易、无存档状态、
不用升版本、不增加 `CommodityTable` 的账本维度。一旦注册 `fish`，就从"能力补全"跨到了
`Simulation State + Inventory + Save Format + Compatibility + Trade/Economy` ——**不是这条线现在该承担的工作**。

⇒ **记档为 `Future Economy Integration`**：等真正做**商品库存 / 贸易系统**时**一次性引入**（那时版本本就该升）。

> ★★ **由此确立一条设计原则（正式保留）**：
> **不要因为某个资源已经被"生产并消费"，就急着把它提升成可库存商品（Commodity + Stock）。**
> **只有当该资源需要【跨 Tick 储存】/【跨主体转移】/【参与贸易】/【需要持久化】时，才把它升级为 Commodity + Stock。**
> ——这与本次冻结 `ResourceStock` 是**同一套思想**：**先证明状态有真实消费者，再引入状态。**
> （`ResourceStock` 冻结 = 没有"存量被挖空"的消费者；`fish` 商品冻结 = 没有"跨 tick/跨主体"的消费者。）


### 7.5 影响面（诚实报告）

① 让 **2645 个沿海格 + 1205 个湖泊/邻湖格**（共 3850 格）在**开垦后**多出一份对开垦免疫的食物。
直接后果是**演化轨迹分岔**（非线性系统，任何 F 变化都会改变后续分裂/吞并/战争序列）：

| 指标 | 改前 | 改后（① 后） |
|---|---|---|
| 末态实体数 | 112 | **103** |
| 末态人口 | 4677.0 | **4092.9** |
| 峰值密度 人/km² | 83.5 | 134.5 |
| 种子持有分布 | [15,22,10,4,12] | [22,23,0,0,0] |
| ΣFFishLast / ΣFLast | — | **3.81%**（水产在末态产出中的占比） |
| **直接食物注入** `Σ R·A·w·水产·开垦` | — | **78.9**，占归属格总潜在 `ΣR·A·w`=2492 的 **3.17%** |

> ⚠️ 口径提醒：**轨迹级差异（112→103、种子分布变化）不能解读为"鱼让人口减少"**——那是混沌分岔，
> 不是单调因果。① 的**可归因量级**有两条，都实测给出：
> ① 末态占比 `Σ水产/ΣFLast = 3.81%`；
> ② **与轨迹解耦的净注入** `Σ R·A·w·水产·开垦 = 78.9（占归属格总潜在 3.17%）`（旧式无水产项，故净增 ≡ 该项）。
> 方向性结论（"养鱼让沿海人口变多还是变少"）必须靠**同种子 A/B**（FishFrac 置 0 重跑同 seed）才能下，
> 别用单条轨迹比较——本次**未做** A/B（那需要给生产代码加开关，属另一变更边界）。

**T90 复核（同 seed）**：判据仍为 **档位 A**（未归属可居陆地 83.0%、开垦不可逆损失 8.7%、≥0.95 = 10/1548=0.6%、
全程 P99.9 峰值 0.995），两项自校验 PASS（T90b 残差 6.32E-008）⇒ **① 未改变"ResourceStock 无消费者"的架构判定**。
（附带：开垦损失从 10.1% 降到 8.7% —— 水产免疫开垦的直接体现；海岸 `Harvest/Regen` P50 从 9.805 降到 5.869 —— 免疫分量稀释了该比值。）

### 7.6 顺带发现（不在本节点范围）

- **`CivSimContext.FOf()` / `ColdFloor()` 是死代码**：全仓 `grep "FOf("` 仅命中自身定义，**零调用者**。
  它是 2026-08-17 等边际闭式之前的"份额劳动爬坡"旧产出式，且它只写 `FHuntLast/FHerdLast/FFarmLast`
  而**不写** `FBerryLast/FFishLast`（若被复活会成为新的双计源）。
  ⇒ 建议按 D 步「目录/代码清退三标准」走正式清退流程（不在此顺手删）。
- T90 工具的 `Sample()` 是 `AllocateAndProduce` 的**只读镜像**——本次它正确地**报错（T90b FAIL）**直到镜像同步更新，
  证明该自校验确实在守"两套实现分叉"（T04 类缺陷）。已同步。
- ⚠️ **`scripts/verify.sh` 已因仓库搬迁而失效（与本次改动无关，但是个真问题）**：
  `--path` 指向的 `res://scenes/diag/TectonicsTest.tscn` 与 `MonsoonDiag.tscn` **在 `scenes/diag/` 下不存在**；
  `LogicGridDiag` 用的 `user://maps/regress_v9_n64.mpa` **不在 `userdata/maps/`**
  （实体文件在 `runs/maps/` 与 `runs/appdata/Godot/app_userdata/world/maps/`）。
  ⇒ **本仓库当前唯一的"一键回归脚本"恒定报 ❌**，实际只有 `CivSimDiag` 一项真正跑了（58P/0F）。
  本次交付的验证因此走的是**手工五层**（构建 → `dotnet test` 496 → 全量地图 T 套件 79 → T90/T91 → 目视），
  而非该脚本。**建议单开一笔修 verify.sh**（3 处路径/场景引用），不与本节点捆绑。

### 7.7 后续路线（用户拍板，2026-10-06）

```text
现在
├─ ① FishPotential      → 独立 commit（本次）
├─ ② Riparian           → 独立 commit      （WorldGen 生物群系事实链修复）
├─ ③ scripts/verify.sh  → 独立 chore commit（验证基础设施失效修复）
└─ fish 商品            → 冻结 → Future Economy Integration
```

- **② 先于 ③**：Riparian 仍属"自然资源 → 人类生计"主线；verify.sh 是工程基础设施，随后清理。
- **② 不是"为了鱼"才存在**：它是 `WorldGen → Biome` 语义链缺了一类生态位，完成后会**同时点亮
  `WaterRich` / `IrrigFactor` / `PreyFrac` 三条既有死分支**。① 已通过四参接口预留 `isRiparian` ⇒ 落地即自然接上。
- **③ 的动机**：本次实测发现 `verify.sh` 恒 ❌（2 个场景文件 + 1 个地图路径不存在）——它本应是回归测试的标准入口。
  既然 T90b 这类"镜像护栏"已被证明有价值，**验证基础设施本身不应该处于失效状态**。

---



```bash
git log --all --oneline --grep='存量'          # → 4345b84
git log -1 --format='%B' 4345b84               # → 砍存量理由原文
# 代码位置
scripts/CivSim/Engine/CivSimContext.cs:35,295  # 砍存量/土地挂钩 决策注释
scripts/CivSim/Engine/CivSimContext.cs:321     # WaterRich（无海洋邻接）
scripts/CivSim/Engine/CivSimContext.cs:662     # PreyFrac（plant/animal 拆分）
scripts/CivSim/Engine/CivSimContext.cs:857     # AllocateAndProduce（等边际闭式）
scripts/CivSim/Engine/CivEngine.cs:63,64       # BuildLayer1 + 砍存量注释
scripts/CivSim/Tables/CommodityTable.cs        # 6 商品目录（无 fish）
scripts/CivSim/Mechanics/Society/EnergyModel.cs
scripts/CivSim/Mechanics/Society/GrowthModel.cs
scripts/CivSim/Mechanics/Territory/ModeModel.cs
docs/石器时代设计.md §14.2 / §14.5 / §14.7 / §14.9
```

### T90 实测（§五）复现清单

```bash
dotnet build -t:Rebuild
GODOT=/d/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe
ARCH=user://maps/map_seed42_n64_r128.mpa        # n=40962，5.00 km²/格，陆地R中位 0.100 人/km²
for cfg in "42 3" "7 5" "20260806 2"; do set -- $cfg
  $GODOT --headless --path . res://scenes/diag/CivSimDiag.tscn -- \
         --arch=$ARCH --only=T90 --seed=$1 --origins=$2
done
# 预期：每组 汇总：2 PASS / 0 FAIL；T90a 逐位相等；T90b 相对误差 ~1e-8
# 工具源码
scripts/Diagnostics/CivSimDiag.OverHarvest.cs        # T90 全流程（纯只读）
scripts/Diagnostics/CivSimDiag.cs:224-226            # --only=T90 gate（不进全量默认）
# Riparian 从未写入 取证
grep -rn "Biome\[[^]]*\] *=" scripts/ --include=*.cs
grep -rn "Riparian" --include=*.cs .
grep -rln "Koppen|Köppen" scripts/
```

### ① FishPotential（§七）复现清单

```bash
dotnet build
GODOT=/d/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe
ARCH=user://maps/map_seed42_n64_r128.mpa

# ① 单元契约（FishFrac 语义 / 三分不变量 / 退化解 / 湖泊可达 / 端到端海岸vs内陆）
dotnet test tests/World.Tests/World.Tests.csproj --filter "FullyQualifiedName~Fish|FullyQualifiedName~Forage"
# ② 地图级独立验收（预期 5 PASS / 0 FAIL）
$GODOT --headless --path . res://scenes/diag/CivSimDiag.tscn -- --arch=$ARCH --only=T91 --seed=42 --origins=3
# ③ 全量回归（预期 单元 496 PASS / 地图套件 79 PASS；T90/T91 不出现在无筛选跑里）
dotnet test tests/World.Tests/World.Tests.csproj
$GODOT --headless --path . res://scenes/diag/CivSimDiag.tscn -- --arch=$ARCH

# 改动面（严格边界）
scripts/CivSim/Engine/CivSimContext.cs:FishFrac/ForageShares/LakeFishAccess/AllocateAndProduce
scripts/CivSim/Entities/Polity/Polity.cs:FFishLast
scripts/CivSim/Tables/CommodityTable.cs:meat/leather 扣水产
scripts/Diagnostics/CivSimDiag.FishPotential.cs:T91
tests/World.Tests/CivSimMechanics2Tests.cs:FishFrac_*/ForageShares_*/LakeFishAccess_*/Harvest_FishPotentially_*
```

