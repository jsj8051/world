# 生产方式重构：并行混合经济（PM 表）+ 畜牧 + 货物系统 设计

> 2026-08-09 定稿。用户拍板：生产方式**并行并存**（混合经济，非 argmax 择一）；参考 Victoria 3 / EU5 生产方式（PM）机制；决策 = 环境过滤 + **收益权重分配**；副产品 = **货物系统 + 入档（v7）**。

## 一、现状与动机

- 现状：ModeModel argmax(e_猎, e_农) **择一**——部落要么猎要么农。现实是混合经济（农民养猪打猎、牧民采集）。
- 目标：生产方式 = 可解锁的投入产出组合（PM 表），部落**并行启用**全部可用方式，土地按**收益权重**分配，产出 = Σ 各方式（食物 + 货物副产品）。
- 新内容：**畜牧**（livestock 科技 + WildLivestock 生态位——"少许土地产生食物"）、**货物系统**（皮革/羊毛/秸秆，入档 v7，为贸易铺路）。

## 二、生产方式表（PM，Vic3/EU5 参考）

```
id      解锁条件                        产出（食物 + 货物）
hunt    默认（恒可用）                    F_猎 + 皮革
herd    livestock 科技 + WildLivestock 位  F_牧（奶）+ 羊毛
farm    种子科技 + φ>0（IsFarming 开关）   F_农 + 秸秆
```

- **并行启用**：部落方式集 M = {hunt} ∪ {herd if 解锁} ∪ {farm if IsFarming}
- 猎恒在；牧**派生**（无演化字段——livestock 能力 + 生态位位 → 权重>0 即启用）；农用现有 IsFarming 演化字段（转农条件保留现有 e_农>e_猎 逻辑——S2/T08/T14 语义不变）

## 三、决策机制：收益权重分配（边际均衡近似）

```
部落方式集 M = 环境可用 ∩ 科技可用（无草原不能牧、无种子不能农——硬过滤）
权重 w_k = 方式潜在产出 = R_k × A_i × m_k        （土地全给该方式时的产出）
土地份额 s_k = w_k / Σw                          （收益高的方式拿更多地）
各方式实际产出 F_k = R_k × A_i × s_k × m_k × min(1, P / P_劳动_k)
  P_劳动_k = 0.1 × R_k × A_i × s_k × m_k          （份额劳动的劳动力爬坡）
部落总产出 F_i = Σ F_k                            （进增长模型）

涌现：
  草原格（R_牧 = 2R）→ 牧拿 2/3 地 —— 混牧倾斜（"少许土地产食物"）
  河谷格（农 90×）→ 农拿 ~99% —— 农业主导（压倒性倾斜而非择一）
  无草原/无种子 → 对应方式权重 0 不启用
  小部落 → 高收益方式劳动不足产出受限（长大才能充分利用）
```

R_牧 = R × HerdMult（★ 初值 2.0——草原放牧单位土地产出 2× 狩猎，标定）。

## 四、畜牧：livestock 科技 + WildLivestock

```
techs.csv 加：livestock 畜牧 | env=grass(草原) | k=0.01, P_ref=200 | 前置 grinding | 发明+传播
WildLivestock[n]：byte bitmask（同 WildCrops 同构——确定性重建不入档，WildCropsSystem 扩展）
  生成：草原/稀树草原 biome + 降水 300-1200mm → 位=1（可驯牛/羊/猪——本期不区分畜种，统一 livestock）
  解锁畜牧 = CapabilityTable 注册 "livestock"（科技 + 格内 WildLivestock 位）
```

## 五、货物系统（入档 v7）

```
GoodsTable：固定 3 种货物（定长，入档简单）
  leather 皮革（猎） wool 羊毛（牧） straw 秸秆（农）
CivEntity.Goods：float[3]（每实体累积库存，初始 0）
产出：每 tick F_k 产出后 Goods[货物] += F_k × 副产率
  副产率（★ 标定）：皮革 0.10、羊毛 0.15、秸秆 0.05（× 对应方式 F_k）
用途（当前）：只累积（无消费/贸易模型——贸易期接物物交换）
```

**存档 .cmp v7**：实体段每实体 +12B（Goods 3×float）。头部不变（38B）。v6 旧档拒绝（格式变更，既定策略）。Peek per-entity skip 公式 +12。

## 六、测试

| 测试 | 内容 |
|---|---|
| T28 畜牧涌现 | 草原格（WildLivestock=1）+ livestock 科技部落 → 牧权重>0、F_牧>0；无草原格 → 权重 0 |
| T29 货物累积 | 部落产出后 Goods 增加（皮革/羊毛/秸秆按副产率） |
| T30 权重分配 | 构造草原格（牧2×猎）→ 土地份额 2/3:1/3（断言 s 比例） |
| S2/T08/T14 | 语义保留（转农开关不变）→ 应 PASS（回归） |
| T16/T21/T22 | 产出结构变化（草原牧涌现）→ 数值标定记录 |
| T04/T19 | v7 存档往返 + 版本拒绝（ver>7、v6 拒） |

## 七、文件

| 文件 | 责任 |
|---|---|
| `scripts/CivSim/CivModels.cs` | ModeModel 重构（并行启用+权重分配+Σ）；GrowthModel 不变 |
| `scripts/CivSim/CivSimContext.cs` | FOf 重构（Σ 方式）；R_牧/HerdMult；副产率常量；FHerds 方法 |
| `scripts/CivSim/CivEntity.cs` | +Goods float[3]（入档） |
| `scripts/CivSim/CapabilityTable.cs` | +livestock 能力注册（科技+生态位） |
| `scripts/CivSim/TechTable.cs` | +livestock 科技（techs.csv 数据或代码） |
| `scripts/CivSim/WildCropsSystem.cs` | +WildLivestock 生成（同构扩展） |
| `scripts/CivSim/CivMapArchive.cs` | v7：实体段 +12B；Peek skip +12 |
| `scripts/Diagnostics/CivSimDiag.cs` | T28/T29/T30；T19 版本更新 |
| `docs/石器时代设计.md` | §4.2a 生产方式 PM 表/决策机制；货物；v7 |

## 八、范围边界

- 不做：货物消费/贸易/市场（本期只累积）；畜种区分（牛/羊/猪统一）；牧业文化/宗教效果
- 不做：劳动力在方式间的精细分配（全 P 对各方式劳动爬坡——宏观近似）
- IsFarming 转农开关语义保留（不改为权重自动启农——FirstFarmTick/T08 稳定）
- v7 存档：v6 档作废（既定旧档放弃策略）
