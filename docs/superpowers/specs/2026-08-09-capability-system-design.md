# 能力开关系统（Capability System）设计

> 2026-08-09 定稿。查询式：开关集中声明，效果留模型。
> 动机（用户 2026-08-09 拍板）：能力效果全耦合在模型里（15 处 `TechKeys.Contains` 散落 5 个文件），加新能力要改多个模型。设计文档 v2 原意"科技解锁能力加入实体能力池"实现没跟上。
> 目标：**加新内容（畜牧/贸易/存储/宗教链）= 注册一条新 Capability + 相关模型查 HasCap——不碰其他模型**。

## 一、能力系统

```
Capability（声明式开关）：
  Id: string                    "canoe" / "storage" / "livestock" ...
  Unlocked: Func<CivEntity, CivSimContext, bool>   // 解锁条件：科技/状态/环境任意组合（lambda 内 AND/OR）

CapabilityTable（静态注册表，新文件 scripts/CivSim/CapabilityTable.cs）：
  Register(new Capability { Id = "canoe", Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Canoe) });
  ...

缓存（性能）：CivEntity.CapMask（uint 位图），RefreshCellState 每 tick 算一次（与 CarryMult 同模式）
  HasCap(e, "canoe") = (e.CapMask & bit) != 0    → O(1)
  ctx.Caps = 表 id → bit 映射（静态）

模型查询：
  if (ctx.Caps.Has(t, "canoe")) ...   // 替代 e.TechKeys.Contains(TechTable.Canoe)
```

- **上限 32 能力**（uint 位图；当前 8 个 + 新石器规划 ~10 个，够用；超了升 ulong）
- 效果**留在模型**（查询后计算）——不数据化参数
- 科技依赖链（InventionModel Requires）**不属于能力**：那是发明条件，不是能力效果，保持原样

## 二、迁移清单（现有 15 处耦合 → 7 能力）

| 能力 Id | 解锁条件 | 迁移点（现状 → 查询） |
|---|---|---|
| canoe | 科技 canoe | SplitMigrateModel 分裂跨海(:833)、迁徙跨海(:862) |
| microlith | 科技 microlith | ReligionModel 萨满条件(:605) |
| grinding | 科技 grinding | InventionModel 种子软前置(:287) |
| fire | 科技 fire | CivSimContext.ColdFloor 火下限 |
| clothing | 科技 clothing | CivSimContext.ColdFloor 皮毛下限 |
| seed | HeldSeeds.Count > 0 | ModeModel 生产方式(:237)、InventionModel 种子(:294) |
| storage | 科技 storage | **试点：新效果激活**（见 §三） |

依赖链/传播（InventionModel :275、SpreadModel :469）和 CarryMult 乘数链（HuntingCarry）**不迁移**——不是开关型能力（依赖是发明约束、乘数是纯参数）。

## 三、storage 试点效果（Testart 分水岭激活）

**存储缓冲**：有 storage 能力的部落，饿死缺口用积累盈余缓冲（防瞬间饿死）：
```
部落盈余池 S（新实体字段，不存档——从 P 推导？需定）：
  增长缺口期（F < P）：先从 S 扣，S 耗尽才饿死（P 下降）
  盈余期（F > P）：增长照旧 + 余量存入 S（上限 = StorageCap = F × 0.5）
```
效果：饥荒缓冲 → 部落存活率↑、人口波动↓ → T08/T14/T21 演化行为变化（回归标定）。
**实现简化**：盈余池用"存储当量"（float，上限 F×0.5）；饿死时 `P -= min(缺口, S)` 且 S 相应减少；盈余时 S += min(盈余, 上限)。不存档——读档重建需确定性（S 从存档状态不可推？——**若 S 不入档，读档后 S=0 → 续跑分叉**！方案：S 入档（v7？）或 S 不引入（效果改为"饿死阈值软化"：有 storage 部落 P 下限提高而非缓冲池））。

⚠️ **设计决策点**：S 若入档 → .cmp v7（又升级）；若不入档 → 用无状态效果替代（如"饿死延迟 N tick"或"P 下降速率 ×0.7"——无状态，读档无分叉，但语义弱）。
**推荐**：无状态效果——"有 storage 部落饿死衰减 ×0.6"（饥荒缓冲的宏观等效，零存档改动）。

## 四、测试

| 测试 | 内容 |
|---|---|
| T26 能力开关 | 构造实体：含 canoe → HasCap true；缺 → false；storage+状态组合条件正确（单元） |
| 回归 | 全部现有 S/T 测试（迁移是等价替换，行为应不变；storage 效果激活后 T08/T14/T21 重新标定） |
| T27 存储缓冲（试点） | 构造饥荒部落（有/无 storage）→ 断言饿死速率不同 |

## 五、范围边界

- 查询式（效果留模型），不做效果数据化
- 不迁移：依赖链、CarryMult 乘数链（非开关型）
- 能力数 ≤32（uint）
- 不做：能力 UI/诊断面板（后续）；能力"收益选择"（设计文档原意的实体主动选择——本期只做被动解锁）
- 新石器内容（畜牧/贸易/宗教链）**不在本期**——本系统是其承载框架，落地后注册即用

## 六、文件

| 文件 | 责任 |
|---|---|
| `scripts/CivSim/CapabilityTable.cs`（新） | Capability 类 + 静态注册表 + bit 映射 |
| `scripts/CivSim/CivEntity.cs` | +CapMask（uint，派生缓存，不存档） |
| `scripts/CivSim/CivEngine.cs` | RefreshCellState 算 CapMask（循环内） |
| `scripts/CivSim/CivModels.cs` | 迁移 5 处（canoe×2/microlith/grinding/seed×2）+ storage 效果（GrowthModel 饿死衰减） |
| `scripts/CivSim/CivSimContext.cs` | ColdFloor 迁移（fire/clothing）+ Caps 访问器 |
| `scripts/Diagnostics/CivSimDiag.cs` | T26/T27 |
| `docs/石器时代设计.md` | §科技 补能力系统一节 |
