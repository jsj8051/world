# 设计 · WindField 实现设计 v1

日期：2026-10-07
状态：**纯设计冻结 · 零代码**（承 `docs/裁决-WindField架构契约.md`（W1）全部条款 W1–W5、W-M1~M5、W-O1~O3、W-S1~S2）
上游：`ClimateForcing` / `ClimateParameters` / `MonthlyTemperature`（P4-5b 已冻结基座）｜`FinalGeography`｜`HeightComposer`｜`SpatialScale`
下游（本阶段不接线）：P4-5d 风应力洋流｜P4-5e 水汽输送

---

## §一 类型落位与文件

| 类型 | 文件 | 性质 |
|---|---|---|
| `WindParameters` | `scripts/WorldGen/Atmosphere/WindParameters.cs` | static，常量集中，全部显式命名 + 关断值（承 `ClimateParameters` 口径纪律） |
| `WindFieldModel` | `scripts/WorldGen/Atmosphere/WindFieldModel.cs` | static **纯函数**流水线；每一步是独立可测的内部函数 |
| `WindField` | `scripts/WorldGen/Atmosphere/WindField.cs` | **事实容器**（不叫 Model——同 `MonthlyTemperature` 先例：状态/事实不是模型） |

- 目录 `Atmosphere/` = WorldGen 新子层（与 `Oceanography/`（⑮，第 7 子层）对称；namespace 仍 `World.WorldGen`）。物理域独立成层，与 ⑮ 同逻辑。
- **不碰** `scripts/WorldGen/Simulation/` 下任何 P4-5b 文件（W2 单向派生）。
- 不登记进 CivSim 消费面（Phase 4 禁令：禁接 CivSim）。

---

## §二 输入契约（逐项钉到既有类型）

| # | 输入 | 来源（既有类型，不新增事实） | 口径 |
|---|---|---|---|
| I1 | 纬度 latRad(i) | `Ball`：权威口径 `H3.LatLng(cellId)`（**弧度**、带符号）；性能备选 `asin(normalize(CellDirs[i]).Y)`，须过对照测试（容差 1e-6 rad）后才可切换 | 弧度，−π/2..π/2 |
| I2 | 月 m | 函数输入维度 | 0..11，**m=0 = 北半球 1 月**（与 `MonthlyTemperature` 同约定） |
| I3 | 海陆掩膜 | `FinalGeography.FinalLand[i]` | bool |
| I4 | 高程 | `HeightComposer.HeightM[i]` | 米；海格 ≤ 0；海拔 km = max(0, HeightM)/1000 |
| I5 | 温度异常 | `MonthlyTemperature`：**T′(i,m) = T.At(m,i) − T.AnnualMeanAt(i)** | °C；★禁止重算 Gain/τ（契约 §3.2 特别冻结） |
| I6 | hop→km | `SpatialScale.CellEdgeKm`；hop_km = hop × √3 × CellEdgeKm | res4：26.10 → hop ≈ **45.21 km**。★A 类 hop 只在模型入口换算一次，参数一律 B 类 km |
| I7 | 离岸/离陆路径 | `FinalGeography.FinalDistToCoast`（陆格离海 hop）/ `FinalDistToLand`（海格离陆 hop） + `Ball.CellNeighbors` 路径回溯 | hop |

★全模型**唯一**的时间维度是 I2；无状态、无迭代、无前月依赖（契约 §7）。

---

## §三 流水线（6 步，每步独立可测）

```
① 纬向基础带 BeltTemplate(m, latRad)
      → belt ∈ {Trade, Westerly, PolarEasterly, Doldrums} + 经向基矢
② ITCZ 季节位移 φ_ITCZ(m)、φ_ridge(m)、φ_polar(m)        （①②实际同函数，带边界先算）
③ Coriolis 偏转 δ(latRad) → 基矢旋转 → 偏转后方向 az_belt
④ 地形阻挡/绕流（二元）→ 方向修正或速度衰减
⑤ 季风混合 v = (1−w)·v_belt + w·v_monsoon
⑥ 量化输出 → Direction / SpeedMs / MonsoonIndex
```

---

## §四 机制精确定义

### 4.1 带边界（月度函数；W-M1 + W-M2）

```
φ_ITCZ(m) = clamp( ITCZMeanDeg + ITCZAmpDeg·cos(2π(m−6)/12) + Δφ_anom(m), −15°, +15° )
Δφ_anom(m) = clamp( ITCZAnomalyGainDegPerC · (T̄′_NH(m) − T̄′_SH(m)), ±ITCZAnomalyCorrectionMaxDeg )
φ_ridge(m) = RidgeMeanDeg + RidgeShiftFactor·(φ_ITCZ(m) − ITCZMeanDeg)      // 30° 副?
φ_polar(m) = PolarFrontMeanDeg + RidgeShiftFactor·(φ_ITCZ(m) − ITCZMeanDeg) // 60° 极锋
```

- m=6 ⇒ 7 月 ⇒ cos(0)=1 ⇒ ITCZ 最北（与 I2 约定自洽；SH 冬至自动南压）。
- **半球月均异常** `T̄′_NH(m)` = 北半球全部格（陆+海）T′ 的**格算术平均**（等积格 ⇒ 算术平均 = 面积加权，同 `MonthlyTemperature.MeanC` 口径纪律；半球归属 = latRad 符号）。
- 地球锚：ITCZ 均值 ~5°N；7 月北跳、1 月南压至 ~2°S ⇒ amp 取 10°。异常修正上限 5°（"有限幅度"，契约 W-M2）。
- ★赤道两侧带边界**不对称**（φ_ITCZ ≠ 0）⇒ 带分类必须用**带符号纬度**与边界比较，不得用 |lat|。

### 4.2 带分类与经向基矢（W-M1）

按带符号 φ 与 4.1 三条边界分类（先判 Doldrums）：

| belt | 条件 | 经向基矢（旋转前，指向气流**去向**） |
|---|---|---|
| Doldrums | \|φ − φ_ITCZ(m)\| < `DoldrumHalfWidthDeg`(3°) | 无（方向候选哨兵，速度 ×0.4） |
| Trade | φ_ITCZ ≤ φ ≤ φ_ridge（同半球侧） | **朝 φ_ITCZ 一侧**（拍板 ①，见 §十二）：φ > φ_ITCZ ⇒ 正南 az=180°，否则正北 az=0° |
| Westerly | φ_ridge < φ ≤ φ_polar | **向极**：NH 正北 az=0°，SH 正南 az=180° |
| PolarEasterly | \|φ\| > φ_polar | **向赤道**：NH 正南 az=180°，SH 正北 az=0° |

物理解释：Hadley/Polar 环流低层支均向赤道，Ferrel 低层支向极——Coriolis 偏转后自然形成信风/西风/极地东风三带，不需逐带手写风向。

### 4.3 Coriolis 偏转（W-M3，确定性规则）

```
δ(φ) = DeflectionMaxDeg · tanh( CoriolisShapeK · sin|φ| )      // 75° · tanh(2.0·sin|φ|)
旋转：NH 顺时针（az += δ）；SH 逆时针（az −= δ）
```

- δ(0)=0（赤道不偏——退化锚）；单调；半球镜像对称。
- 地球锚（验收 A 的分带夹角依据，tanh 解析值，批次 1 测试钉死）：δ(20°)≈44.6°（信风与经线夹角）｜δ(45°)≈66.6°（西风带近纬向）｜δ(70°)≈71.6°（极地东风近纬向）。
- 扇区化（§4.6）后由测试钉死三带典型扇区：NH 信风 25°N → 231.6°（吹向 SW，扇区 11）；NH 西风 45°N → 66.6°（吹向 ENE，扇区 3）；NH 极风 75°N → 251.9°（吹向 WSW，扇区 12）。

### 4.4 地形阻挡/绕流（W-M4，二元）

对格 i 的偏转后方向 az_belt：

1. **前向视域**：沿 az_belt 方向、六邻接 BFS 深度 ≤ `TerrainLookaheadHops`(3) 的格集合（≈ 135 km；res4 表达上限，契约 §4 禁止更深结构）。
2. **屏障判定**：视域内存在格 j 满足 `HeightM[j] ≥ TerrainBarrierHeightM`(2000 m) **且** `HeightM[j] ≥ HeightM[i] + TerrainRelativeRiseM`(800 m)。海格 HeightM ≤ 0 永不触发（无需海陆分支，同 `TemperatureModel` 惯例）。
3. **绕行**：屏障成立 ⇒ 比较 az±`TerrainDeflectionDeg`(45°) 两侧视域内的最大屏障高度，**取较低一侧**旋转 45°（确定性平局：取逆时针侧）。两侧受阻 ⇒ 方向不变、速度 × `TerrainBlockedSpeedFactor`(0.7)。
4. **关断**：`TerrainBarrierHeightM = 1e9f` ⇒ 该步骤整体关闭，逐点等于关闭前结果减去本步效果（测试锚）。

★**雨影不在此实现**（契约 W-M4 禁止）：降水侧由 P4-5e 消费 `Direction` + 地形抬升自行修正；本步只给"绕开山"的宏观路径。

### 4.5 季风符号反转（W-M5，复用 P4-5b 异常 + 既有 BFS）

**对比信号**（陆海两式统一为"陆侧异常 − 海侧异常"）：

```
陆格：C(i,m) = T′(i,m) − T̄′_seaBand(m; i)
海格：C(i,m) = T̄′_landBand(m; i) − T′(i,m)
T̄′_seaBand / T̄′_landBand = 纬差 ≤ MonsoonSeaBandDeg(10°) 的海格/陆格 T′ 格算术平均
```

★用**纬带均值**而非"最近海格温度"：省 BFS、抗海岸噪声，且与 W-M5"陆海热响应对比"的大尺度语义一致。

**信号月**（同 `MonthlyTemperature.PeakMonthAt` 平局纪律）：
`m_warm(i) = argmax_m C(i,m)`，`m_cool(i) = argmin_m C(i,m)`，平局取小索引 ⇒ 全程确定。

**季风目标方向**（复用 I7 既有 BFS 路径，天然确定）：

- 陆格 i：沿 `FinalDistToCoast` 严格递减回溯到最近海格 j（dist−1 邻居中取**格索引最小**者）⇒ az_monsoon = 从 j 指向 i 的方位角（海→陆）。
- 海格 i：沿 `FinalDistToLand` 递减回溯到最近陆格 j ⇒ az_monsoon = 从 i 指向 j（指向陆）。
- 两岸自动衔接成同一条跨岸气流线，冬夏符号翻转由 m_warm/m_cool 的 C 符号给出：暖月 C>0 ⇒ 吹向陆侧；冷月 C<0 ⇒ 反向。

**权重与门控**：

```
Cmax(i) = max( C(i,m_warm), −C(i,m_cool) )       // 两极端月取强者
w(i) = 0                                          若 Cmax < MonsoonContrastThreshC(0.5 °C)
     = MonsoonStrength · exp(−d_km(i)/MonsoonDecayKm(1000))   否则，且 d_km ≤ MonsoonReachKm(2000)
     = 0                                          若 d_km > MonsoonReachKm
d_km：陆格 = FinalDistToCoast×hop_km；海格 = FinalDistToLand×hop_km
```

地球锚：季风域深入内陆 ~1000–2000 km（南亚季风、西非季风量级）；τ陆=1/τ海=8 月的异常差在中纬大陆 ≥5 °C ⇒ 门控 0.5 °C 只滤掉弱对比区。

### 4.6 输出量化（W-O1~O3 + §五 存储）

**方向约定（★钉死，与气象学习惯相反）**：存储的是气流**去向**方位角（运动方向），从正北顺时针 0–360°。16 扇区，扇区 s(1..16) 覆盖 [(s−1)·22.5°, s·22.5°)。**0 = 静风哨兵**：合成速度 < `CalmSpeedMs`(1.0 m/s) 或 Doldrums 带内。

- 16 扇区（非 8）理由：P4-5e 水汽平流按风向选下游邻居，8 扇区在对角带内指向偏差达 ±22.5°，会平流进错误格；byte 存储两种粒度同价。
- NH 典型扇区自检（批次 1 实测解析值）：信风 25°N = 231.6°（扇区 11）、西风 45°N = 66.6°（扇区 3）、极风 75°N = 251.9°（扇区 12）。

**MeanWindSpeedMs**（float，m/s）：

```
Speed = BaseSpeed(belt) × [land ? LandFrictionFactor(0.7) : 1.0]
        × [doldrums ? DoldrumSpeedFactor(0.4) : 1.0]
        × [terrainBlocked ? 0.7 : 1.0]        // 仅"两侧皆阻"分支
BaseSpeed：Trade 6.0｜Westerly 9.0｜PolarEasterly 4.0（m/s，海洋上；地球地面风气候态量级）
clamp [0, 25]
```

★单位 = m/s（C 类物理量纲，可直接对地球站点标定；P4-5d 消费时按 1 m/s ≈ 2592 km/月 自行换算，**禁止**在 Wind 层预乘月长）。季风混合 v1 **只改方向不改速度**（方向-only，范围纪律）。

**MonsoonReversalIndex**（float ∈ [0,1]，派生统计量，非月度存储替代品）：

```
Δθ = az_final(m_warm) 与 az_final(m_cool) 的夹角 ∈ [0°,180°]（按 16 扇区中心角计算）
MRI = (1 − cos Δθ)/2
MRI = 0    若任一极端月为静风哨兵
```

诊断阈值 **MRI ≥ 0.5（Δθ ≥ 120°）⇔ 显著季风**——v1 仅判读用，无消费者；消费者（季风气候分类）出现时再校（开放项 O-W5）。

---

## §五 存储布局（W-S1/W-S2 兑现）

```csharp
public sealed class WindField            // 与 ball.CellIds 逐位对齐；无 NaN / 无 null
{
    public int CellCount;
    public byte[]  DirectionTo;          // n；0=静风哨兵，1..16=扇区（★去向扇区，BearingTo 语义）
    public float[] SpeedMs;              // n；m/s
    public float[] MonsoonIndex;         // n；[0,1]
}
```

- res4 内存：288,122 × (1+4+4) B ≈ **2.6 MB**（对照被否决的 12 月 u/v 场 27.7 MB = 1/11；对照 `MonthlyTemperature` 13.8 MB）。
- **不建** `u[12][n]`/`v[12][n]`（契约 W-S1）；未来水汽输送若需月度风，走 `MonthlyField` 新槽位**单独提案**（W-S2）。
- 每格都有值（海格同样出方向/速度；MRI 逐格定义）。

---

## §六 参数总表（`WindParameters`；每项 = 值 + 量纲 + 锚 + 关断）

| 参数 | 值 | 量纲 | 依据 | 关断值 |
|---|---|---|---|---|
| `ItczMeanDeg` | 5 | °（B） | 地球 ITCZ 气候态均值 ~5°N | —（模板常量） |
| `ItczSeasonalAmpDeg` | 10 | °（B） | 7 月北跳/1 月南压量级 | **0** ⇒ φ_ITCZ 恒 5°N（D1） |
| `ItczAnomalyGainDegPerC` | 1.0 | °/°C（B/C） | W-M2"有限幅度修正"斜率 | **0** ⇒ 无异常修正（D1） |
| `ItczAnomalyCorrectionMaxDeg` | 5 | °（B） | 修正上限（契约 W-M2"有限幅度"） | — |
| `RidgeMeanDeg` / `PolarFrontMeanDeg` | 30 / 60 | °（B） | 地球副高脊/极锋气候态位置 | —（模板常量） |
| `RidgeShiftFactor` | 0.3 | — | 副系统随 ITCZ 同向弱漂移 | **0** ⇒ 边界全年固定 |
| `DoldrumHalfWidthDeg` | 3 | °（B） | 赤道无风带半宽量级 | — |
| `DoldrumSpeedFactor` | 0.4 | — | 无风带衰减 | — |
| `DeflectionMaxDeg` | 75 | °（B） | 高纬近纬向偏转上限 | **0** ⇒ 无 Coriolis（纯经向，测试锚） |
| `CoriolisShapeK` | 2.0 | — | tanh 形状（锚点 δ20°/45°/70°） | — |
| `TerrainBarrierHeightM` | 2000 | m（B） | 大尺度山系门槛（地球:大型山系 ≥2 km） | **1e9** ⇒ 地形关闭（D4） |
| `TerrainRelativeRiseM` | 800 | m（B） | 相对格点高程的显著抬升 | — |
| `TerrainLookaheadHops` | 3 | hop（A） | ≈135 km @ res4；res4 表达上限 | — |
| `TerrainDeflectionDeg` | 45 | °（B） | 绕流单次偏转角 | — |
| `TerrainBlockedSpeedFactor` | 0.7 | — | 两侧皆阻的摩擦衰减 | — |
| `BaseTradeSpeedMs` / `BaseWesterlySpeedMs` / `BasePolarSpeedMs` | 6 / 9 / 4 | m/s（C） | 地球地面风气候态：信风 5–8、西风 8–11、极风 3–5 | **全 0** ⇒ 静风球（极端退化） |
| `LandFrictionFactor` | 0.7 | — | 陆面摩擦（地球陆地风速 ~海面 0.6–0.8） | **1.0** ⇒ 无摩擦差异 |
| `MonsoonSeaBandDeg` | 10 | °（B） | 纬带海/陆异常均值带宽 | — |
| `MonsoonContrastThreshC` | 0.5 | °C（C） | 季风门控（滤弱对比区） | **∞** ⇒ 无季风（D2） |
| `MonsoonReachKm` | 2000 | km（B） | 季风域内陆深度上限 | — |
| `MonsoonDecayKm` | 1000 | km（B） | 内陆衰减尺度 | — |
| `MonsoonStrength` | 1.0 | — | 季风总开关 | **0** ⇒ 无季风（D2） |
| `CalmSpeedMs` | 1.0 | m/s（C） | 静风哨兵门槛 | — |

★命名纪律（`ClimateParameters` 口径教训）：每个参数显式命名所服务的响应函数；B/C/A 量纲分明；关断值经 §七退化矩阵逐点验证。

---

## §七 退化矩阵（W5 验收的逐点对照）

| # | 操作 | 期望 |
|---|---|---|
| D1 | `ItczSeasonalAmpDeg=0` 且 `ItczAnomalyGainDegPerC=0` | φ_ITCZ(m) ≡ 5°N 常数；Δφ_anom ≡ 0 |
| D2 | `MonsoonStrength=0` | w ≡ 0；`MonsoonIndex` 全 0；方向 = 纯带流 |
| D3 | **W5 全退化**：注入 T′ ≡ 0（测试侧构造零异常输入，**不改 P4-5b**）+ D1 + D2 + D4 | 纬向模板 + Coriolis；与手工构造的模板解析解**逐格逐点相等** |
| D4 | `TerrainBarrierHeightM=1e9` | 无地形分支，逐点等于流水线去掉第 ④ 步 |
| D5 | 纯函数一致性（契约验收 E） | 同输入两次全量生成，三数组**逐位相等** |

★D3 是契约 W5 的直接兑现：退化语义 = "不再产生由热异常造成的额外季节性/区域性结构"，而非"所有格严格东西向"（Coriolis 偏转保留）。

---

## §八 测试矩阵（`tests/World.Tests`；[Test] 纪律：不写文件、不碰 GD.*/LogService）

| 测试 | 断言 |
|---|---|
| `BeltClassification_MatchesLatitudeBands` | 合成纬度剖面上三带 + Doldrums 边界与 4.1/4.2 公式逐点一致 |
| `Coriolis_ZeroAtEquator_Monotonic_MirrorSymmetric` | δ(0)=0；单调；δ(φ)=δ(−φ)；三带典型扇区（11/3/12） |
| `ItczJumpsNorthInJuly_SouthInJanuary` | m=6 最北、m=0 最南；幅度 = ItczSeasonalAmpDeg |
| `ItczAnomalyShift_Bounded` | 构造 NH 全暖异常 ⇒ 北移 ≤ 5°；k=0 ⇒ 零移动 |
| `Monsoon_SummerOnshore_WinterOffshore` | 构造陆暖海冷（夏）/ 陆冷海暖（冬）异常场 ⇒ 沿岸格方向翻转且指向正确 |
| `MonsoonIndex_RangeAndReversalSemantics` | MRI∈[0,1]；180° 反转 ⇒ 1.0；静风月 ⇒ 0 |
| `Terrain_BarrierDeflectsToLowerSide_TieBreaksCounterClockwise` | 合成山系地形 ⇒ 绕行侧正确；平局取逆时针 |
| `Terrain_BelowThresholdNoEffect` | 1500 m 山系 ⇒ 与 D4 关断版逐点相等 |
| `DirectionEncoding_FlowDirectionNotMeteorological` | NH 20°S…20°N 信风扇区 = 11（吹向 SW）——钉死"去向"约定 |
| `Degradation_W5_FullOff_MatchesZonalTemplate` | D3 逐点对照（契约验收 D） |
| `PureFunction_Deterministic` | D5 逐位对照（契约验收 E） |
| `LandMaskAndCellCount_Invariants` | 数组长度 = CellCount、与 CellIds 对齐、无越界扇区、Speed ∈[0,25] |

新增生产文件后按惯例：Headless `--headless --import` 生成 `.cs.uid` 入库；**新类型若进入契约扫描面，必须登记 `NewWorldLineTypes`**（漏扫 = 契约静默失效）。

---

## §九 消费者接线口径（本阶段不实现，只冻结接口语义）

| 消费者 | 读什么 | 语义 |
|---|---|---|
| P4-5d 洋流 | `Direction` + `SpeedMs` | 风应力方向 = 下游邻居选择 + 速度量级；换算 km/月由 Oceanography 侧做（禁 Wind 层预乘） |
| P4-5e 水汽/降水 | `Direction`（迎风/背风）+ 地形抬升 | 雨影 = 降水侧修正；迎风坡 = 上风方向存在 ≥TerrainBarrierHeightM 地形 |
| 判读层 | 三数组只读 | B3 sink-only；Wind 不回写任何事实（W2） |

★CivSim 不接触 WindField（Phase 4 禁令）；Bridge 是否投影风场归 O-O5 同类问题（B2 届时定）。

---

## §十 开放项（不阻塞 v1 实现）

- **O-W1** ITCZ 陆海分曲线：v1 全纬单曲线，季风区北跳幅度偏弱；由 P4-5e 验收暴露后再议（需先有消费者证据）。
- **O-W2** δ(φ) tanh 形状标定：现锚 3 点（20°/45°/70°）；地球探空气候态资料可精化。
- **O-W3** `MonsoonSeaBandDeg=10°` 带宽敏感性：等 P4-5e 对 MRI 空间分布的判读结果。
- **O-W4** 西风带急流核心（速度纬向峰值结构）：v1 不表达；洋流西向强化（O-O3 同族）如需再做。
- **O-W5** MRI 阈值 0.5 与季风气候分类消费者挂钩时再校。
- **O-W6** `asin(CellDirs.Y)` 托管纬度口径若通过对照测试（1e-6 rad），可替换 H3.LatLng 做 O(n) 提速——实现期决定。

---

## §十一 实现顺序（冻结后开工的批次）

1. `WindParameters` + 带边界/带分类纯函数 + 测试（§八前 4 条）——零事实层依赖，纯 latRad/m 可测。
2. `WindField` 容器 + 全流水线 + 退化矩阵（D1–D5）。
3. 季风 + 地形（依赖 `FinalGeography`/`MonthlyTemperature` 注入）+ 对应测试。
4. 判读读数（三带格占比、季风区面积等 Headless 自检锚，参照 `MonthlyTemperature.MeanC` 惯例）——**判读指标不进生产**（永久原则 7）。

每批独立 commit（永久原则 13）；主线 P4-5c 提交永远优先（原则 13 后半句）。

---

## §十二 批次 1 拍板补钉（2026-10-07 用户终审，实现级口径 · 永久）

用户裁决：**⑯ 设计完成 → 契约冻结 → 批次 1 实现获准**。批次 1 = 基础解析骨架 + 契约测试，不碰季风、不碰地形、不碰存储接线；不等待 P4-5c。三条实现级规则钉死：

1. **① 赤道基矢解析极限**：Trade 基矢 = **朝 φ_ITCZ 一侧**（φ > φ_ITCZ ⇒ 180°，否则 0°）。ITCZ 越赤道的月份里赤道点仍有确定方位；**禁止** normalize(零向量)、**禁止**赤道特殊分支（W5 逐点测试安全）。Westerly/PolarEasterly 带区间天然不含赤道。
2. **② BearingTo 语义进命名**：全部方位角 API 与测试名直接携带 `BearingTo`/去向语义（`MeridionalBaseBearingToDeg` / `DeflectedBearingToDeg` / `SectorFromBearingToDeg`）——防被改回气象学"来向"口径。
3. **③ 速度与方向解耦**：基速只由带分类决定（Trade 6 / Westerly 9 / Polar 4 m/s，Doldrums = Trade×0.4）；季风/地形批次对速度的上限 = 乘衰减因子，**永不改写带基值**——保 W5、统计量与 P4-5d 风应力接口干净。

**批次 1 落地记录**：

- 新文件：`scripts/WorldGen/Atmosphere/WindParameters.cs`（批次 1 参数 + `WindBelt` 枚举；地形/季风参数**不预付**）、`WindFieldModel.cs`（纯函数：`ItczAnomalyShiftDeg` / `ItczLatDeg` / `RidgeLatDeg` / `PolarFrontLatDeg` / `ClassifyBelt` / `MeridionalBaseBearingToDeg` / `CoriolisDeflectionDeg` / `DeflectedBearingToDeg` / `SectorFromBearingToDeg` / `BeltBaseSpeedMs`）。
- 测试：`tests/World.Tests/WindFieldModelTests.cs` **6 条**（§八前 4 条 + 拍板 ①③ 各自的钉子：`MeridionalBaseBearing_BearingToSemantics_NoEquatorSpecialCase`、`BeltBaseSpeed_DecoupledFromDirection`）。**580 PASS / 0 FAIL**，build 0 警告。
- `NewWorldLineTypes` 登记 `WindParameters` / `WindFieldModel`；`.cs.uid` 已入库。
- 锚点数值修正（§4.3/§4.6/§八）：tanh 解析值 δ(20°)=44.6°、δ(45°)=66.6°、δ(70°)=71.6°；西风 45°N = 66.6° ⇒ **扇区 3**（原稿"扇区 4"为舍入示意，已更正）。
- 待办批次：批次 2 = 事实容器 + 全流水线 + 退化矩阵 D1–D5（注入式关断在彼时引入）；批次 3 = 季风 + 地形；批次 4 = 判读读数。

---

## §十三 批次 2 落地记录（2026-10-07）

**范围**（按用户批次重划：批次 2 = 容器 + 全流水线 + 退化矩阵；批次 3 = 地形；批次 4 = 季风 + 最终统计口径）：

- **新增 `WindField.cs`**（事实容器）：`DirectionTo`（byte，0=静风哨兵 + 1..16 扇区；★字段名携带 BearingTo 语义，拍板 ②）/ `SpeedMs` / `MonsoonIndex`（批次 2 恒 0 = 季风关闭态；字段先行到位以钉死数据布局，W-S2）。
- **新增 `WindTuning`**（`WindFieldModel.Tuning`）：注入式参数束，默认 = `WindParameters` 冻结常量；**只用于退化矩阵注入式关断与测试对照**，非运行时调参入口。D1（amp=0）/D5 等经此注入；D4（地形关断）随批次 3、D2（季风关断）随批次 4 各自落地。
- **全流水线 `Generate`**：双入口——`Generate(float[] latRad, MonthlyTemperature, Tuning)`（纯函数核心）+ `Generate(Ball, ...)`（I1 权威纬度 `H3.CellToLatLng` 弧度）。月度边界逐月预计算一次（`MonthBoundaries`），逐格 O(12)。
- **聚合口径钉死**：SpeedMs = 12 个月带基速算术平均（无风带月 ×0.4）；DirectionTo = 12 个月去向单位矢量圆均值扇区化，**无风带月不参与**；新增静风规则：合成矢量均值长度 < `PrevailingMinVectorLength`(0.05)（两季对吹抵消）或速度 < `CalmSpeedMs`(1.0) ⇒ 哨兵 0；全年全无风带 ⇒ 哨兵。
- **退化矩阵**：D1 ✅（amp=0 ⇒ φ_ITCZ≡5°，全年无风带格 = 静风哨兵 + 2.4 m/s）；D3 ✅（独立实现逐点对照，73 纬度全剖面，方向扇区逐点相等 + 速度 ≤1e-3）；D5 ✅（两次全量生成逐位相等）。D4/D2 随批次 3/4。
- **验收实测（res4 全世界，回答批次 2 的核心问题）**：`Ball(4)` 构造 1,065 ms｜**Generate 133 ms**（288,122 格 × 12 月）｜存储 **2.47 MB**（= 契约 W-S1 口径）｜静风格 10,217（无风带纬带，占比合理）⇒ **内存/遍历/数据布局/纯函数边界全部成立**。验收测试以 `[Explicit]` 入库（`Res4_FullWorld_Generate_Acceptance`），不进常规套件。
- **测试**：批次 2 新增 7 条（D3 对照 / D1 / D5 / 半球异常聚合 / 端到端 ITCZ 通道 / Ball 接线 + I1 双口径对照 / res4 Explicit）⇒ **常规套件 602 PASS / 0 FAIL**。
- **实测踩坑（钉进测试注释）**：① D3 独立实现的数值口径必须与生产逐位对齐（latDeg 用 double 乘、δ 取 float）——否则无风带边界 |φ−φ_ITCZ|=3° 上的格翻带成员，近对消格圆均值方向翻转 180°（扇区 5 vs 13）；② 端到端测试纬度选点避开边界刀口（13°N → 11°N，余量 ≥0.34°）。
- `NewWorldLineTypes` 登记 `WindField`；`.cs.uid` 入库。commit：`feat(worldgen): add WindField v1 batch 2`。
