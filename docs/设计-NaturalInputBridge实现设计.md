# 设计 · ⑬ Natural Input Bridge 实现设计

> 2026-10-07 · **纯设计**（零代码改动 · **不实现 Bridge** · **不写 B1** · 不补 CivSim 事实 · 不改生产公式）
> 前置：④ `审查报告-WorldHumanInputBridge-语义等价性取证.md` · ⑤ `裁决-Bridge可行性裁决.md`
> 　　　⑥ `裁决-HumanInputGrid载体契约.md`（含 §八之二） · `ADR-0005`
> 状态：**`BLOCKED BY MISSING WORLD FACTS`**（载体契约已 DECIDED；剩 P4-3b Biome 生产 / P4-4 Soil）
> 本轮用户拍板：**① B1 不提前**（严守阶段边界）；**② Biome / Soil 升格为 B2 重点审计项**
> **版本：⑬ Natural Input Bridge Architecture Baseline v1（2026-10-07 冻结）**
> ——修正 5 处契约表达后冻结：① Jaccard 比较域须**地理重映射**（§7.3.1）② R3 届时升格**白名单语义**（§12.1）
> ③ `WildCrops` **惰性持有化**（§3.3.1）④ **9 标量双证明分离**（§4.1）⑤ O5 **分消费者阈值**；
> 补 B1 钉子 **N12**（identity 稳定）。**设计冻结，等待 Phase 4 完成后进入 B0。**

---

## §〇 一句话设计

```
WorldGen 事实层（H3 res4） ──Bridge 投影层──> HumanInputGrid（空间容器） ──> CivSim（21 个消费者）
        World.WorldGen.*                        World.Bridge                    World.CivSim.*
        └────────── R2 禁区：World.CivSim 不得引用（深度传递）──────────┘
```

**Bridge = 一组逐事实投影函数 + 一个载体 `HumanInputGrid`。它不是第二个 WorldGen，也不是 CivSim 的补丁。**

---

## §一 目标与边界

### 1.1 目标

把 WorldGen 生产的自然事实，按 ④ 的逐项语义判定，投影到 CivSim 唯一认识的空间容器 `HumanInputGrid` 上，
使 CivSim 在 **H3 res4** 上继续跑通四级演化（band → tribe → chiefdom → state）。

### 1.2 ★载体三项禁令（本轮新增 · 永久）

> **`HumanInputGrid` 是 CivSim 输入事实的兼容载体。它：**
> 1. **不重新生成世界事实**；
> 2. **不反推缺失的月度数据**；
> 3. **不承载 Legacy-only 变量**。

**这条原则防的是**：Bridge 在实现过程中被逐渐做成"第二套 WorldGen"——
一旦 `HumanInputGrid` 开始自己算气候、自己补季节、自己造字段，
Bridge 就从"投影层"退化为"又一个事实生产源"，且**这个源与 WorldGen 无契约约束、无收敛验证、无跨 seed 基线**。

| 禁令 | 具体表现（实现时须拒绝） |
|---|---|
| **不重新生成世界事实** | 不在 `HumanInputGrid` / Bridge 内调用任何 **WorldGen 自然事实生产者**（气候/地理 `*Model`）；不缓存"顺手算一下"的场；不做"新线没有就用经验公式补一个"。★`WildCropsSystem` 派生函数**只允许 Bridge 投影层调用**（§3.3.1），**载体永远不调** |
| **不反推缺失月度数据** | 不从年均温/年降水反推 12 个月剖面（`P(m)=(P/12)(1+S·cos…)` 形态）；月度信息**只能**来自 P4-5 的 9 标量（§八） |
| **不承载 Legacy-only 变量** | `RiverLevel` / `RiverFlow` / `RiverVolume` / `MineralLevel` / `MonsoonLevel` / `CurrentDirs` / `Psi` **不进** `HumanInputGrid`（④ 判 `LEGACY_ONLY`，零消费者） |

> ★ 与 ADR-0005 **B4** 同源：**生态/自然事实必须由 WorldGen 事实层生产**。
> `HumanInputGrid` 若缺某项输入，正确的处置是**回 Phase 4 补生产者**，而不是在桥里就地补。

### 1.3 明确不做

- ❌ 不实现 B1 载体骨架（用户拍板：不提前，见 §五·B1）
- ❌ 不写任何 Bridge / `HumanInputGrid` 代码
- ❌ 不补 CivSim 侧的环境/生态事实
- ❌ 不重标定 hop→km 数值（属 O2，须实测）
- ❌ 不实现 Legacy `.gmp` / `.mpa` 读档兼容层（⑥ §八之二）

---

## §二 R2 / 命名空间硬约束

### 2.1 实测：R2 是**深度传递**扫描

`tests/World.Tests/ArchitectureContractTests.cs:185` 用的是 `ReferencedTypesDeep`，**不是**浅层的 `ReferencedTypes`：

```csharp
foreach (var t in civTypes)
    foreach (var r in ReferencedTypesDeep(t))     // ← 传递闭包
        if (IsWorldGen(r)) offending.Add(...);
```

`IsWorldGen` 判定为 `ns == "World.WorldGen" || ns.StartsWith("World.WorldGen.")`。

### 2.2 ⇒ 命名空间的硬边界

| 层 | 建议命名空间 | 能否引用 `World.WorldGen.*` | R2 是否命中 |
|---|---|---|---|
| WorldGen 事实层 | `World.WorldGen.*` | — | （本体） |
| **Bridge 投影层** | **`World.Bridge`** | ✅ **可以**（它不是 `World.CivSim`） | 否 |
| **`HumanInputGrid`** | **`World.Bridge`** 或 `World.H3Grid` | ⚠️ 见下 | 否 |
| CivSim | `World.CivSim.*` | ❌ **禁止**（R2） | 是 |

**★ 关键推论**：`HumanInputGrid` **绝不能**放 `World.WorldGen` 或 `World.WorldGen.*`——
`StartsWith` 会连 `World.WorldGen.Bridge` 一起命中 ⇒ CivSim 一引用就 FAIL。

**推荐**：`World.Bridge.HumanInputGrid`。
（备选 `World.H3Grid.HumanInputGrid`：`World.H3Grid` 不在 R2 黑名单，且 `Ball` 已在此；
但会让"投影层"与"载体"分居两个命名空间，语义上不如同置于 `World.Bridge` 清晰。）

> ★2026-10-09 勘误 + 改名：该备选命名空间**原名 `World.Spatial`**，当日已改名为 **`World.H3Grid`**
> （理由 = 避与 `SpatialScale` / `FinalSpatialIndex` 混淆——**这两者其实都在 `World.WorldGen`，
> 并不在本命名空间**；此处"`SpatialScale` 已在此"是原文笔误，一并更正）。
> O3 的实质（`World.Bridge` vs 该 H3 网格命名空间）不变，只是名字换了。
> 见 `docs/裁决-Spatial改名H3Grid.md`。

### 2.3 待办（实现 B1 时）

- 若最终选 `World.H3Grid`，须按 ADR §B2 纪律**显式改执行契约白名单**（⑥ 开放项 O3）。
- `NewWorldLineTypes` 是各契约的公共扫描面 —— **`HumanInputGrid` 落地后必须登记**，否则契约静默失效（永久纪律）。

---

## §三 `HumanInputGrid` 设计

### 3.1 定位

**CivSim 侧的空间容器**，满足 ⑤ §三 的三件套 `{Identity + Topology + Metric}`，并承载 ④ 判定的投影结果。

它**取代 `GameGrid` 的角色**，但**不是 `GameGrid` 的子类或适配器**——它是新的载体，只是 API 面刻意对齐。

### 3.2 ★ API 面对齐 `GameGrid`（关键设计决策）

让 CivSim 的 **21 个生产文件、~90 处成员访问**做到"换类型即可、不改调用点"。

| 契约 | 成员（与 `GameGrid` 同名同义） | 来源（H3 res4 口径） |
|---|---|---|
| **Identity** | `int N`；`ulong[] CellIds`；`int CellIndexOf(ulong)` | `Ball`（dense 仅运行时；世界身份 = CellId） |
| **Topology** | `int[][] Neighbors` | `Ball.CellNeighbors` = `GridDisk(k=1)`（六边形 6 / 五边形 5） |
| **Metric** | `double CellAreaKm2`；`double DistKm(int a, int b)` | `SpatialScale`（等积口径 + haversine） |
| **自然事实** | `float[] Elev` / `Temp` / `Precip` | Bridge 投影（§六） |
| | `byte[] Biome` / `SoilLevel` / `LakeLevel` | Bridge 投影（§七 审计重点） |
| **季节** | **不提供 `byte[12][n]`**；改为 4 个派生标量 | P4-5 的 9 标量（§八） |
| **派生** | `bool IsLandCell(int)` / `bool IsCoast(int)` | 同上（`Elev > 0`；邻格含海） |
| | `byte[] WildCrops` / `byte[] WildLivestock`（**只读持有**） | **Bridge 投影时已生成**（§3.3.1 冻结解释） |

> ⚠️ **不提供**：`RiverLevel` / `RiverFlow` / `RiverVolume` / `MineralLevel` / `MonsoonLevel` / `CurrentDirs` / `Psi`（§一·禁令 3）。

### 3.3 形态示意（签名，非实现）

```
namespace World.Bridge;

public sealed class HumanInputGrid
{
    public int N { get; }                       // == 288,122（ProductionRes = 4）
    public ulong[] CellIds { get; }             // 世界身份
    public int[][] Neighbors { get; }           // GridDisk(k=1)
    public double CellAreaKm2 { get; }
    public double DistKm(int a, int b);

    public float[] Elev, Temp, Precip;
    public byte[] Biome, SoilLevel, LakeLevel;

    public float[] WinterShare, MaxMonthTempC, MinMonthPrecipMm, WetDryRatio;   // ← 4 派生量，非 12 数组

    public bool IsLandCell(int cell);
    public bool IsCoast(int cell);

    // ★ 只读持有（§3.3.1 冻结解释）：Bridge 投影时已生成，载体不 Compute
    public byte[] WildCrops { get; }        // null = 尚未投影（B2 前的合法状态）
    public byte[] WildLivestock { get; }
}
```

### 3.3.1 ★ 惰性访问的冻结解释（禁令 1 的边界判例 · 2026-10-07 修正）

`WildCrops` / `WildLivestock` 的**生成方 = Bridge 投影层**：B2 时由投影器调用
`WildCropsSystem.Compute` / `ComputeLivestock`，把结果**写入**载体；`HumanInputGrid` **只持有 / 返回**。

```
Bridge 投影（B2）──调用──> WildCropsSystem.Compute ──写入──> HumanInputGrid.WildCrops（只读持有）
                                                                │
                                                CivSim 读（不触发任何计算）
```

**禁止的形态**：`HumanInputGrid.EnsureWildCrops()` 内部调用 `Compute` ——
那会让载体承担计算行为，成为第二个事实生产入口（即使只是"派生"事实）。
**冻结解释**：`Ensure*` 类成员**只能是惰性访问 / 缓存 materialization，不能是生产**。

**与 Legacy 的差异（有意为之）**：`GameGrid.EnsureWildCrops()` 是读档后惰性重建；
新载体不做——重建职责移交 Bridge，**时点前移到投影时**。CivSim 读到 `null` = "B2 没投影完"，属配置错误而非惰性触发。

### 3.4 与 `GameGrid` 的不可互换点（实现时勿踩）

| 项 | `GameGrid`（Legacy） | `HumanInputGrid`（H3 res4） | 后果 |
|---|---|---|---|
| Identity 来源 | `.mpa` 顶点序 | `Ball` 构建序 + CellId | 已入档 cell 引用**须重映射** |
| 邻接定义 | 距离阈值 `1.5×平均格距` | 拓扑环 `GridDisk(k=1)` | ⑥ §二：**度数相近也不可互换** |
| 度量 | `4πR²/N`（均匀近似）+ 点积大圆 | `SpatialScale`（等积 + haversine） | 数值不同，勿直接搬运 |
| 格数 | 40,962（n=64） | 288,122（**×7.04**） | 成本 ×7；hop 常量须重标定 |

---

## §四 P4-3b / P4-4 前置依赖

> **Bridge 的 7 项投影里，2 项至今没有生产者。** 这是唯一剩余阻断项。

| 前置 | 须产出 | 解锁的投影 | 现状 |
|---|---|---|---|
| **P4-3b** Biome **生产** | 逐格 `BiomeType`（Köppen 气候型，P4-3a 已定语义） | `Biome` | ❌ 未生产（P4-3a 只定语义，**不生产**） |
| **P4-4** Soil | 逐格 `SoilLevel` 1–5（0 = 海洋） | `SoilLevel` | ❌ 未生产 |

**级联恢复（无需额外任务）**：P4-3b + P4-4 交付后，`WildCrops` / `WildLivestock` **自动恢复**
（其派生函数 `WildCropsSystem.Compute` / `ComputeLivestock` 本身不需要改；
★**调用方 = Bridge 投影层**，不是载体——见 §3.3.1）。

### 4.1 ★ 双证明分离（9 标量充分性是两个不同命题 · 2026-10-07 修正）

> **已证明（A）**：9 标量足够支持 **CivSim 当前消费者**
> （`WarWeather` / `WildCrops.Suitability` 的 `WinterShare`/`MaxMonthTemp`/`MinMonthPrecip`/`WetDryRatio`）——§八 的依据。
> **不因此自动成立（B）**：9 标量足够让 **P4-3b 正确生产 Biome**。

A 与 B 是**两个层次的证明**：A 的对象是"CivSim 消费面"；B 的对象是"Köppen 分类器的判据输入"。
A 成立不代表 B 自动成立——若未来 Köppen 分类依赖更细的月度模式，就会**重新撞上"不得从年均反推月度"的冻结条款**。

**⇒ 前置条件（本轮钉死）**：**P4-3b 必须在自己的生产者层证明其输入充分性**，
**不得把"CivSim 端 9 标量零覆盖损失"当作 Biome 生产充分性的证明**。
（P4-5 裁决 §三若已覆盖 24 类 Köppen 判据则 B 已部分成立——但该论证的**复述责任在 P4-3b 落地文档**，不在本设计。）

**当前路线位置**（ADR-0005）：

```
⑫ Climate 架构契约 ✅ → ★P4-5b Solar Forcing + 海陆热响应 ← 主线
   → P4-5c 水循环 → P4-5d 洋流输送 → P4-5e 对齐验收
   → P4-3b Biome 生产 → P4-4 Soil → ⑬ Bridge
```

⇒ **⑬ Bridge 开工条件 = P4-5e 对齐验收 + P4-3b + P4-4 全部完成。**

---

## §五 B0 ～ B4 分阶段实施与验收

> ★ **用户 2026-10-07 拍板：严格执行 `Phase 4 完成 → B0 → B1 → B2 → B3 → B4`，B1 不提前。**
> 理由：B1 技术上可独立做、且能尽早暴露 R2/命名空间问题，
> 但当前已存在「Phase 4 期间禁写 Bridge」的过程约束——**为局部收益破坏阶段边界，代价大于收益**。
> 写本设计文档**不违反**该约束：**写设计 ≠ 写 Bridge 生产代码**。

| 阶段 | 内容 | 前置 | 验收（须可测） |
|---|---|---|---|
| **B0** | P4-3b Biome 生产 + P4-4 Soil | P4-5e 对齐完成 | Biome 与 P4-3a 语义表逐类对照；Soil 1–5 分布合理 |
| **B1** | `HumanInputGrid` 载体骨架（三件套 + I1–I5 钉子） | B0 | `N == 288,122`；每格邻居 6（五边形 5）；`CellAreaKm2` 与 `SpatialScale` 一致；**`CellIds[i] → CellIndexOf(CellIds[i]) == i` 全量可逆、CellId 全局唯一、同参数构建序稳定**（N12）；R2 仍绿 |
| **B2** | 逐项投影（§六） | B1 | 每项自带口径；**Biome / Soil 走 §七 重点审计** |
| **B3** | 接线 + **同批 hop→km 重标定** | B2 | R2 仍绿；同 seed 四级演化跑通；无 ÷2.65 静默缩放 |
| **B4** | 清退（§十） | B3 | 全量测试不回归 |

### 5.1 B1 被"压后"的风险与对冲

| 风险 | 对冲（**现在就写进文档，等 B1 落地时兑现**） |
|---|---|
| R2 / 命名空间冲突发现太晚 | §二 已把判定规则与推荐命名空间钉死；B1 开工第一步即跑 `ArchitectureContractTests` |
| O3（命名空间）未决阻塞 B1 | O3 只影响"选 `World.Bridge` 还是 `World.H3Grid`"；两者都已论证合法 ⇒ 不阻塞 |
| 载体三件套与 `Ball` 不一致 | B1 验收直接对照 `Ball` / `SpatialScale`，不另立标准 |
| **dense ↔ CellId 错位**（B3/B4 涉及存档 / CivSim state / 地图引用时的**隐蔽**错位源，比"邻居数不对"更难发现） | **N12**（§12）：全量可逆 + 唯一 + 构建序稳定，**B1 即立** |

### 5.2 B3 必须与重标定同批

I4（⑥ §三）：hop 不表示固定物理距离。**不在 B3 同批改，就会静默缩放 2.65×**：

| 参数 | Legacy 拓扑值 | Legacy 物理含义 | res4 等效 hop | 建议新形态 |
|---|---|---|---|---|
| `InfluenceRadius` | 6 | ≈ 670 km | ≈ 16 | `InfluenceRadiusKm ≈ 670` |
| `ColonizeRadius` | 6 | ≈ 670 km | ≈ 16 | `ColonizeRadiusKm ≈ 670` |
| `ChiefReach` | 12 | ≈ 1,339 km | ≈ 32 | `ChiefReachKm ≈ 1,339` |
| `OriginDistMin` | 12 | ≈ 1,339 km | ≈ 32 | `OriginDistMinKm ≈ 1,339` |

（数值属 O2，**待实测拍板**；上表为"保留 Legacy 物理含义"的建议值。）

---

## §六 B2 逐项投影口径

> 依据 ④ 的 15 项判定。**每项一个投影函数 + 各自验收口径**；不做"批量字段拷贝"。

| # | 目标字段 | ④ 判定 | 投影来源 | 验收口径 |
|---|---|---|---|---|
| ① | `Elev` | **EQUIVALENT** | `Composer.HeightM`（`FinalLand` = `HeightM > 0`） | 海陆掩码与 `FinalLand` **逐格一致**（无阈值漂移） |
| ② | `Temp` | ADAPTER | `TemperatureModel.AnnualMeanC` | 与 Legacy `TEMP` **量级同阶**；`≤ −5.5 °C` 冰盖阈值语义可复现 |
| ③ | `Precip` | ADAPTER | `PrecipitationModel.AnnualMm`（绝对量纲 mm/a） | 量纲 = mm/年；与 Legacy `PREC`（max 10662）同阶 |
| ④ | `Biome` | 待 P4-3b | Köppen 分类器 → `BiomeType` | **§七 重点审计** |
| ⑤ | `SoilLevel` | 待 P4-4 | 土壤模型 → 1–5（0 = 海洋） | **§七 重点审计** |
| ⑥ | `LakeLevel` | ADAPTER | `LakeState` 逐湖对象 → 逐格掩码（⑤ 候选：`LakeOfBasin[DepressionIdOf[i]] >= 0`） | 逐格布尔；湖格总数与 `LakeState` 湖对象覆盖一致 |
| ⑦⑧ | 季节 4 派生量 | ADAPTER | P4-5 的 9 标量 | **§八** |
| ⑨–⑬ | River* / Mineral / Monsoon | **LEGACY_ONLY** | **不投影**（零消费者） | — |
| ⑭⑮ | `WildCrops` / `WildLivestock` | 级联 | `WildCropsSystem`（**调用方 = Bridge 投影层**，§3.3.1；载体只读持有） | 与 Legacy 同 seed 对照 |

**★ 通用纪律**：
1. 每个投影函数**只做形态/单位转换**，不做语义补全（§一·禁令 1）。
2. 投影结果必须**逐格可追溯**到某个 WorldGen 事实；查不到来源的字段 = 违规。
3. 每项投影独立 commit（永久原则 13：变更边界不同 ⇒ 分开 commit）。

---

## §七 Biome / Soil 语义映射与验证（★ B2 重点审计项）

> **用户 2026-10-07 拍板**：Biome / Soil **不是普通字段投影**，必须作为 B2 的**重点审计项**。
> 载体层已较稳定，真正的风险在语义转换：
> `WorldGen Biome/Soil → HumanInputGrid Biome/Soil → CivSim 使用语义`。
> 明确写成 **「逐类映射 + 逐项验证」**；**不能只验证数值范围或总量**，
> **必须验证类别形状和空间分布是否保持**。

### 7.1 为什么"总量/范围验证"不够（实测依据）

CivSim 对 `Biome` 的消费是 **按类别的离散 switch / 集合判定**，不是连续量：

| 消费点 | 实现 | 语义敏感度 |
|---|---|---|
| `IsColdZone`（`CivSimContext:344`） | `b is IceCap or Tundra or Subarctic or Alpine` | **只认 4 类** |
| `TerrainCost`（`:352`） | 海 / `IceCap` / `Alpine` / `HotDesert`+`ColdDesertKoppen` / default | **只分 5 档** |
| `PreyFrac`（`:669`） | Steppe×2 + Savanna → 0.7；雨林/季风/湿润 → 0.35；default 0.5 | **只分 3 档** |
| `WaterRich`（`:328`） | `Biome == Riparian \|\| LakeLevel > 0`（含邻格） | **只认 Riparian** |
| `EnvMatches`（`:550`） | 含 `"coldzone"` 键 | 复用 `IsColdZone` |

**⇒ 致命场景**：若 Köppen 分类器把某批 `Tundra` 判成 `Subarctic`，
`IsColdZone` 结果不变（两者都在集合内）——**但 `TerrainCost` 会从 `default 1.0` 掉到 `0.2/0.3` 档吗？不会**；
反之若把 `Alpine` 判成 `Tundra`，`TerrainCost` 从 `0.2` 跳到 `1.0` ⇒ **山脉变得畅通**，
而**类别总数、温度范围、降水总量全都看不出异常**。

这就是必须验证**类别形状与空间分布**的原因。

### 7.2 Biome 逐类映射表（填写时机 = B2 开工）

`BiomeType` 共 **24 类**（`scripts/Domain/BiomeType.cs`）：
`DeepOcean 0` / `Ocean 1` / `IceCap 2` / `Tundra 3` / `Alpine 12` / `Riparian 13`
+ 18 个 Köppen 细类（14–31）。

| `BiomeType` | Köppen | P4-3b 判据（待填） | CivSim 语义归属 | 审计优先级 |
|---|---|---|---|---|
| `IceCap` | EF | | `IsColdZone` ✓ · `TerrainCost` 火/皮毛 | **P0** |
| `Tundra` | ET | | `IsColdZone` ✓ | **P0** |
| `Subarctic` | Dfc | | `IsColdZone` ✓ | **P0** |
| `Alpine` | —（高海拔） | | `IsColdZone` ✓ · `TerrainCost 0.2` | **P0** |
| `Riparian` | —（河岸） | | `WaterRich` ✓ · `×1.5` R 加成 | **P0**（★当前恒 0，见 §七·注） |
| `HotDesert` | BWh | | `TerrainCost 0.3` | **P0** |
| `ColdDesertKoppen` | BWk | | `TerrainCost 0.3` | **P0** |
| `HotSteppe` / `ColdSteppe` | BSh / BSk | | `PreyFrac 0.7` | P1 |
| `TropicalSavanna` | Aw | | `PreyFrac 0.7` | P1 |
| `TropicalRainforest` / `TropicalMonsoon` / `MonsoonSubtropical` / `HumidSubtropical` / `Oceanic` | Af/Am/Cwa/Cfa/Cfb | | `PreyFrac 0.35` | P1 |
| `DeepOcean` / `Ocean` / `FrigidOcean` / `TropicalOcean` | — | | `IsLandCell = false` | P2 |
| 其余 Dfa/Dfb/Dwa/Csa/Csb | | | default 档 | P2 |

> ★ **Riparian 注**：④ 实测 Legacy `BIOM` **不含** `Alpine(12)` 与 `Riparian(13)`；
> ② Riparian 已裁决**取消不做**（被清退气候链留下的幽灵接口）。
> ⇒ `Biome == Riparian` 恒 false 是**合法退化状态，不是 bug**；
> `WaterRich` 的唯一水因子实际是 **湖泊**。B2 审计**不得**把 Riparian 当作"缺了个分支要补"。

### 7.3 Biome 验证口径（三层，缺一不可）

| 层 | 验证内容 | 判据 |
|---|---|---|
| **① 逐类覆盖** | 24 类各自的格数占比 | 与 Legacy 同 seed 对照；**P0 类不得恒 0**（除已裁决的 Riparian/Alpine） |
| **② 类别形状** | **`IsColdZone` 格集合**、**`TerrainCost` 各档格集合**、**`PreyFrac` 各档格集合** | **经地理域重映射后**（§7.3.1）比较**空间位置集合**的 Jaccard ≥ 各自阈值（O5 分消费者定，§11.1） |
| **③ 空间分布** | 上述集合的**纬度剖面**与**连通性** | 冰盖应在高纬连续成片，不得碎斑；沙漠应在副热带带狀；山脉应呈线状骨架 |
| **④ 跨 seed** | ①–③ 在 **12 seed** 上复算 | ★ `R` 与冰盖占比跨 seed 方差极大（4.8%~25%）⇒ **单 seed 结论一律不作数** |

#### 7.3.1 ★ Jaccard 比较域：必须先做地理重映射（2026-10-07 修正）

Legacy（n=40,962）与 H3 res4（n=288,122）的 dense index **不是同一空间域**，
且 §3.4 已裁决两者 identity **不可互换** ⇒ **不能直接对 dense index 集合做 Jaccard**。

```
Legacy GameGrid ──geographical remap──> 共同地理域（球面位置）
                                              ↑
H3 res4 CellIds ──（CellCenters）─────────────┘
                                              ↓
                            与 HumanInputGrid 对照（按空间位置，非按 index）
```

**比较的是"空间位置集合"，不是"dense index 集合"**：
Legacy 侧取每格的球面位置（`Verts[i]` 单位向量）落进的 res4 cell；新线侧就是 cell 本身。
只有这样，§3.4（identity 不可互换）与 §7.3（可与 Legacy 对照）才完全一致。

**两类统计量的区分**：
- **占比 / 分布类**（各档格数占比、纬度剖面）——无量纲比例，**可直接比**；
- **逐格集合类**（`IsColdZone` 集合、`TerrainCost` 各档、Soil 4/5 位置）——**必须先走本节重映射**。

⚠️ **重映射是有损的**（1 Legacy 格 ≈ 7.04 个 res4 格 ⇒ 一对多展开）⇒
Legacy 侧集合按"质心落格"或"覆盖格"两种口径**之一**投影；
**口径必须在 B2 审计文档里显式写明，全程只用一种**。

### 7.4 Soil 逐项映射与验证

CivSim 对 `SoilLevel` 的消费**只有两处，但都是阶跃**：

| 消费点 | 实现 | 语义 |
|---|---|---|
| `AlluvFactor`（`CivSimContext:341`） | `4 → ×2`、`5 → ×3`、`≤3 → ×1` | **只看 4/5 两档** ⇒ 农业产出尖峰完全由 4/5 的空间位置决定 |
| 农业产出（`:410`/`:453`/`:939`/`:992`） | `rAgri = R × IrrigFactor × AlluvFactor(SoilLevel)` | 与 `R` 相乘 ⇒ **误差会被放大** |
| `InventionModel.cs:50` | `SoilLevel[e.Cell] >= 3` | **驯化能力门槛** ⇒ 决定哪些格可解锁农业 |

**验证口径**：

| 层 | 内容 |
|---|---|
| ① 分档占比 | 1–5 各档占比 + 0（海洋）占比，与 Legacy 对照（占比可直接比，无需重映射） |
| ② **4/5 的空间位置** | 是否落在冲积/河谷/低地；**不得均匀随机散布**；与 Legacy 的 4/5 集合对照**须走 §7.3.1 地理重映射** |
| ③ **`>= 3` 的陆格占比** | 直接决定驯化可达范围；这是**真正的功能性判据**，不是"分布好不好看" |
| ④ 跨 seed | 同 §7.3 ④ |

> ⚠️ 陷阱：Soil 的**均值/中位数看起来合理**，但 4/5 若散在荒漠，`AlluvFactor` 会在无农业价值的格上给 ×3
> ⇒ `rAgri` 峰值错位。**必须看空间共位（Soil 4/5 ∩ 高 R ∩ 近水）**，不能只看边际分布。

### 7.5 审计纪律

- Biome / Soil 的投影与验证**各自独立 commit**，不与其它字段混在一起。
- 审计结果落 `docs/审查报告-Bridge-BiomeSoil语义验证.md`（B2 时新建），**必须含跨 seed 表与纬度剖面**。
- 任一 P0 类验证不通过 ⇒ **回 Phase 4 修生产者**，**禁止在桥里做补偿映射**（§一·禁令 1）。

---

## §八 Month* 9 标量 → 4 个 CivSim 派生量

> ★ **不重建 `byte[12][n]`。**

### 8.1 为什么不建 12 月数组

- `GameGrid` 是 `MonthTemp` / `MonthPrecip` 两条 `byte[12][n]`；
- 但 P4-5 **只产 9 标量/格**（`裁决-P4-5-季节气候最小充分事实集.md` §三），
  且对 **CivSim 消费面**已证零覆盖损失（**§4.1 证明 A**；证明 B 归 P4-3b，不在此处）；
- ADR-0005 已冻结：**Seasonal Climate 是 Biome 上游事实，禁止从年均反推月度**（依赖倒置）；
- ⇒ 建 12 月数组 = 必然反推 = 违反禁令 2（§一）。

### 8.2 4 个派生量的来源（实测对照）

| CivSim 用量 | P4-5 标量 | 说明 |
|---|---|---|
| `WinterShare` | `WinterPrecipShare` | 冬半年降水占比 ∈ [0,1] |
| `MaxMonthTemp` | `HottestMonthMeanC` | 最热月均温 |
| `MinMonthPrecip` | `DriestMonthPrecipMm` | 最干月降水 |
| `WetDryRatio` | `WettestMonthPrecipMm / DriestMonthPrecipMm` | 干湿比（除零须守卫） |

P4-5 现有字段（`SeasonalClimateModel`）：`ColdestMonthMeanC` / `HottestMonthMeanC` /
`DriestMonthPrecipMm` / `WettestMonthPrecipMm` / `SummerMin/MaxMonthPrecipMm` / `WinterMin/MaxMonthPrecipMm` / `WinterPrecipShare`。

### 8.3 验收

1. 4 个派生量与"由完整 12 月剖面直接计算"的结果**逐格一致**（可用离线完整剖面做对照基准）。
2. **退化解**（永久原则 4）：季节振幅 → 0 时，`MaxMonthTemp ≡ MinMonthTemp ≡ T_ann`、各降水标量 ≡ `P_ann / 12` 比例 ⇒ **精确退化**到无季节世界。
3. 不产生任何"看起来像月度数组"的中间结构。

---

## §九 Lake / River / Mineral / Monsoon 的 Legacy 处理

| 字段 | ④ 判定 | 处置 | 依据 |
|---|---|---|---|
| `LakeLevel` | **ADAPTER** | **投影**：逐湖 → 逐格布尔掩码 | 唯一有消费者的水因子（`WaterRich`） |
| `RiverLevel` / `RiverFlow` / `RiverVolume` | **LEGACY_ONLY** | **不投影，不承载** | 零消费者；★河流对 CivSim 完全无影响 |
| `MineralLevel` | **LEGACY_ONLY** | **不投影** | 零消费者（无 MiningModel） |
| `MonsoonLevel` | **LEGACY_ONLY** | **不投影** | 零消费者 |
| `CurrentDirs` / `Psi`（洋流/流函数） | **LEGACY_ONLY** | **不投影** | 洋流已随 A 线清退 |

> ★ **`LakeLevel` 的语义是"布尔"**：`WildCropsSystem.RiceSuit` **只当布尔用**，
> `WaterRich` 也只判 `> 0`。⇒ Bridge **不需要**还原 Legacy 的逐格水位数值，只需掩码；
> 若日后需要水位，应回 WorldGen 补事实，不在桥里造。

> ⚠️ **River 的陷阱**：④ 判 `LEGACY_ONLY` 的理由是"零消费者"，
> 但新线 `RiverNetwork.IsRiver` 是**存在**的。⇒ 若将来某个 CivSim 功能要消费河流，
> 必须**先回 ④ 重判**，不能在 B2 顺手加进去（防膨胀三问）。

---

## §十 清退范围

> 依据 ⑥ §八之二（用户拍板：**不读旧档、不保留 Legacy 兼容层**）。
> ⚠️ **清退必须与 B3 同批**：`GameGrid` 不是存档而是**运行网格**，
> `CivEngine.Run(grid, …)` 外部注入且唯一构造入口来自存档 ⇒ **提前删 = CivSim 能编译但跑不起来**。

| 类 / 文件 | 处置 |
|---|---|
| `GameMapArchive.cs` / `ArchiveLayout.cs` / `GameGrid.FromMapData` / `.gmp` 路径 | **删** |
| `CivMapArchive` 的 NATR 段读写 | **改**（换 `HumanInputGrid` 载体） |
| `GameGrid` **字段族** | **不是删，是迁移** ⇒ 进 `HumanInputGrid` |
| `GameGrid.BuildNeighbors`（1.5×格距阈值） | **废** ⇒ `Ball.CellNeighbors` |
| `CellAreaKm2 = 4πR²/N` / 点积 `DistKm` | **废** ⇒ `SpatialScale` |
| `SphereGrid.cs` | **删**（H3 无对应物；唯一消费者 `ArchiveDiag:117` 改用 `Ball`） |
| `10n²+2` 结构校验（`GameMapArchive:159`） | **废** ⇒ `H3.GetNumCells(res)` |
| `Icosahedron.cs` | ★2026-10-09 **已删**（用户拍板）：原拟保留因其 `Subdivide` 是 CivSim 测试的 42 顶点夹具，但 CivSim 已整体移出、旧 icosahedron 网格退役 ⇒ 删除；回归时夹具改走 H3 / `HumanInputGrid`——见 `docs/裁决-Legacy载体簇删除.md` §八 |

---

## §十一 开放项与禁止事项

### 11.1 开放项（★ 不擅自定）

| # | 子项 | 状态 |
|---|---|---|
| **O1** | 存档 cell 引用存 **CellId（64-bit）** 还是 **dense index + 冻结构建序**？ | ⑥ §八-O1 保留；**因"不保留旧档"而简化**——只需定新格式，不必兼顾兼容 |
| **O2** | hop→km 的**具体数值**：保留 Legacy 物理含义还是在新世界重标定？ | 属**标定**，须实测（B3 同批处理） |
| **O3** | `HumanInputGrid` 归属 `World.Bridge` 还是 `World.H3Grid`？ | 两者均已论证合法；若选 `World.H3Grid` 须显式改 ADR §B2 白名单 |
| **O4** | 是否复用生产 `Ball(res4)` 实例（而非新建第二套） | 实现选择；原则是"不新建第二套 identity/topology 来源" |
| **O5**（新增） | Biome / Soil 的**类别形状 Jaccard 验收阈值**？ | **按消费者敏感度分别实测，不设单一全局阈值**（2026-10-07 修正）。水文 Jaccard 76.5% 只作**经验参考量级**，不作隐含标准——理由：河网是**线性结构**、ColdZone 是**面积集合**、Desert 是**纬向带状**、TerrainCost 是**离散分档**、Soil 4/5 是**局部热点**，对局部偏移的敏感度完全不同（沙漠带整体偏 2°，肉眼仍"正确"但 Jaccard 大降；热点随机散一点，Jaccard 好看但功能已坏）。⇒ `ColdZone` / `TerrainCost` / `PreyFrac` / `Soil4/5` **各自一条阈值**，B2 时分别实测定 |

### 11.2 禁止事项（实现时的一票否决清单）

1. **禁止**在 Bridge / `HumanInputGrid` 内调用任何 WorldGen `*Model.Compute`（禁令 1）。
2. **禁止**从年均温/年降水反推 12 月剖面（禁令 2；依赖倒置已冻结）。
3. **禁止**把 `River*` / `MineralLevel` / `MonsoonLevel` / `CurrentDirs` / `Psi` 放进载体（禁令 3）。
4. **禁止**让 `World.CivSim.*` 引用 `World.WorldGen.*`（R2，深度传递）。
5. **禁止**为降成本把 CivSim 降到 res3/res2（I1；已有 `ProductionRes_IsRes4_Frozen` 钉子）。
6. **禁止**在 B3 之外单独改 hop 常量（I4；必须与接线同批）。
7. **禁止**凭"某类格数为 0"就补一个分支——先查是否被裁决为合法退化（Riparian 教训）。
8. **禁止**在桥里做"数值不好看就调一下"的补偿映射 ⇒ 回 Phase 4 修生产者。

---

## §十二 架构契约与测试钉子

| # | 钉子 | 时机 | 说明 |
|---|---|---|---|
| **N1** | `CivSim_DoesNotReferenceWorldGen`（R2）**持续保持绿** | B1–B4 每阶段 | 现有契约；`HumanInputGrid` 落地后**必须登记 `NewWorldLineTypes`**，否则契约静默失效 |
| **N2** | R3 届时升格为 **`FinalWorldFacts_Consumers_Are_Whitelisted`**（§12.1） | **B2 接线时** | 白名单 = `WorldGen ✅ / World.Bridge ✅ / World.CivSim ❌ / 其他层 ❌` |
| **N3** | `HumanInputGrid_Granularity_Equals_ProductionRes` | **B1** | 断言 `N == 288,122` 且粒度 == `WorldGenPlanet.ProductionRes`（补 I1 的 CivSim 侧钉子） |
| **N4** | `HumanInputGrid_Topology_IsGridDisk1` | **B1** | 断言每格邻居 6（五边形 5）、对称、与 `Ball.CellNeighbors` 逐格一致（I3） |
| **N5** | `HumanInputGrid_Metric_MatchesSpatialScale` | **B1** | `CellAreaKm2` / `DistKm` 与 `SpatialScale` 逐点对照（永久原则 5） |
| **N6** | `Bridge_Biome_ColdZoneSet_Jaccard` | **B2** | §7.3 ② + **§7.3.1 地理重映射**：`IsColdZone` **空间位置集合**与 Legacy 的 Jaccard（跨 seed；阈值属 O5 分消费者） |
| **N7** | `Bridge_Biome_TerrainCostTiers_Jaccard` | **B2** | 同上，按 `TerrainCost` 5 档分别算（同样走 §7.3.1 重映射） |
| **N8** | `Bridge_Soil_AlluvCoLocation` | **B2** | §7.4：验证 Soil 4/5 ∩ 高 R ∩ 近水的共位率（防峰值错位） |
| **N9** | `Bridge_Season_Degradation` | **B2** | §8.3 ②：振幅 → 0 时精确退化到无季节世界（永久原则 4） |
| **N10** | `Bridge_NoLegacyOnlyFields` | **B1** | 反射断言 `HumanInputGrid` **不含** River*/Mineral/Monsoon/CurrentDirs/Psi（禁令 3） |
| **N11** | `CivSim_RadiusParams_AreKm` | **B3** | 断言影响半径类参数名/单位已改 km（I4） |
| **N12** | **`HumanInputGrid_CellIdentity_IsStable`** | **B1** | ① `CellIds[i] → CellIndexOf(CellIds[i]) == i` **全量可逆**；② 所有 CellId **全局唯一**；③ 同参数 `Ball` 构建**顺序稳定**（可复现）。★B3/B4 一旦涉及存档 / CivSim state / 地图引用，identity 错位比"邻居数不对"**更隐蔽**——此钉子优先级高于 N4 |

> ★ **灵敏度对照**：新增任一契约后**必须做灵敏度对照**（永久纪律）——
> 即故意造一个违规实现，确认测试**会红**；否则契约等于没写。

### 12.1 ★ R3 的白名单升级（B2 接线时执行，现在不动 · 2026-10-07 修正）

**实测**（`ArchitectureContractTests.cs:216-247`）：R3 当前白名单 = **字面只有 `World.WorldGen.*`**，
且其失败信息已预留程序出口——"若确为正式接线，请显式扩展本测试的白名单并留下决策记录（C-4 §6.2 G2）"。

**问题**：按字面语义，**Bridge 一接入 R3 就会红**——与"Bridge 是唯一合法的世界事实出口"直接矛盾。
**要修的不是实现，是契约语义**（B2 接线时同步执行）：

```
FinalWorldFacts_OnlyConsumedByWorldGen      →    FinalWorldFacts_Consumers_Are_Whitelisted
（白名单 = {World.WorldGen.*}）                   （白名单 = {World.WorldGen.*, World.Bridge.*}）
```

**语义**：Bridge 是被架构契约**明确授权**的 WorldGen → CivSim 出口；
但 `World.CivSim` 本体仍不得触碰 WorldGen（**R2 不变，两契约各管一边**：R2 管"CivSim 不得越界"，本条管"Final 事实不得被私接"）。

⚠️ **现在不改测试**——Bridge 尚不存在，R3 当前保持绿是正确的；
本节是**届时执行的操作记录**（升格须留决策记录，满足 G2 的程序要求）。

---

## 附 · 关键文件与依据

- `docs/审查报告-WorldHumanInputBridge-语义等价性取证.md`（④：15 项输入判定）
- `docs/裁决-Bridge可行性裁决.md`（⑤：可行性 + 载体取证 + 2.65× 实测）
- `docs/裁决-HumanInputGrid载体契约.md`（⑥：I1–I5 + §八之二 清退清单）
- `docs/裁决-P4-5-季节气候最小充分事实集.md`（9 标量的充分性证明）
- `docs/裁决-P4-3-Biome事实定义.md`（Biome 语义 = Köppen 气候型）
- `tests/World.Tests/ArchitectureContractTests.cs`（R2 / R3 / `NewWorldLineTypes`）
- `scripts/Logic/H3Grid/Ball.cs`（H3 identity + dense↔id + 六邻接）
- `scripts/WorldGen/Simulation/SpatialScale.cs`（等积面积 + haversine）
- `scripts/Domain/BiomeType.cs`（24 类定义）
- `scripts/CivSim/Engine/CivSimContext.cs`（Biome/Soil 消费语义实测点：`:328` `:341` `:344` `:352` `:669`）
- `scripts/CivSim/Mechanics/Culture/InventionModel.cs:50`（`SoilLevel >= 3` 门槛）
