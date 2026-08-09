# 能力开关系统 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 能力开关系统——Capability 声明式解锁条件 + CapMask 位图缓存，模型查 `CapabilityTable.Has(ctx, e, id)` 替代散落的 `TechKeys.Contains`，加新能力不碰其他模型。

**Architecture:** 静态注册表（CapabilityTable.cs）+ 每 tick 缓存位掩码（CivEntity.CapMask，RefreshCellState 算）+ 模型查询式接入。查询式（开关集中、效果留模型），storage 试点 = 无状态饿死缓冲（不升存档版本）。

**Tech Stack:** C# / Godot 4.7 mono；CivModelRegistry 模式；测试走 CivSimDiag（--only 筛选已就绪）。

---

### Task 1: CapabilityTable + CapMask 缓存 + T26

**Files:**
- Create: `scripts/CivSim/CapabilityTable.cs`
- Modify: `scripts/CivSim/CivEntity.cs`（CapMask 字段）
- Modify: `scripts/CivSim/CivEngine.cs`（RefreshCellState 缓存）
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T26 + 注册）

- [ ] **Step 1: 创建 `scripts/CivSim/CapabilityTable.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace World.CivSim;

/// <summary>能力开关系统（2026-08-09 用户拍板：查询式——解锁条件声明集中，效果留模型）。
/// 加新能力 = Register 一条（条件 lambda，可组合科技/状态/环境）；模型查 Has(ctx, e, id) → O(1) 位测试
/// （CapMask 每 tick 缓存，RefreshCellState）。上限 32 能力（uint 位图）。</summary>
public static class CapabilityTable
{
    public sealed class Capability
    {
        public string Id;
        public Func<CivEntity, CivSimContext, bool> Unlocked;
    }

    private static readonly List<Capability> _caps = new();
    private static readonly Dictionary<string, uint> _bits = new();
    private static bool _inited;

    public static void Register(Capability cap)
    {
        if (_bits.ContainsKey(cap.Id)) throw new InvalidOperationException($"重复能力 id: {cap.Id}");
        if (_caps.Count >= 32) throw new InvalidOperationException("能力数超 32 上限（uint 位图）");
        _bits[cap.Id] = 1u << _caps.Count;
        _caps.Add(cap);
    }

    /// <summary>惰性初始化（首查时注册内置能力；幂等）。</summary>
    private static void EnsureInited()
    {
        if (_inited) return;
        _inited = true;
        Register(new Capability { Id = "canoe",     Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Canoe) });
        Register(new Capability { Id = "microlith", Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Microlith) });
        Register(new Capability { Id = "grinding",  Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Grinding) });
        Register(new Capability { Id = "fire",      Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Fire) });
        Register(new Capability { Id = "clothing",  Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Clothing) });
        Register(new Capability { Id = "seed",      Unlocked = (e, c) => TechTable.HeldSeeds(e.TechKeys).Count > 0 });
        Register(new Capability { Id = "storage",   Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Storage) });
    }

    /// <summary>实体能力位掩码（RefreshCellState 每 tick 缓存；条件含环境——同 tick 内环境稳定）。</summary>
    public static uint MaskOf(CivSimContext ctx, CivEntity e)
    {
        EnsureInited();
        uint mask = 0;
        for (int i = 0; i < _caps.Count; i++)
            if (_caps[i].Unlocked(e, ctx)) mask |= 1u << i;
        return mask;
    }

    public static bool Has(CivSimContext ctx, CivEntity e, string id)
    {
        EnsureInited();
        return _bits.TryGetValue(id, out uint bit) && (e.CapMask & bit) != 0;
    }

    /// <summary>诊断：能力 id 全集（T26 完整性断言用）。</summary>
    public static IReadOnlyList<string> AllIds()
    {
        EnsureInited();
        var r = new List<string>();
        foreach (var c in _caps) r.Add(c.Id);
        return r;
    }
}
```

- [ ] **Step 2: CivEntity 加字段**（`BornTick` 后，领地字段旁）

```csharp
    public uint CapMask;         // 能力位图缓存（CapabilityTable.MaskOf；RefreshCellState 每 tick；不存档——读档重建）
```

- [ ] **Step 3: RefreshCellState 缓存**（CivEngine.cs 第一遍循环，`e.CarryMult = ...` 行后加）

```csharp
            e.CapMask = CapabilityTable.MaskOf(ctx, e);
```

- [ ] **Step 4: T26 单元测试**（CivSimDiag.cs `T25_FissionPressure()` 后加，RunScenarios 注册）

```csharp
    /// <summary>T26 能力开关（单元）：canoe/seed 解锁条件正确；能力 id 全集完整（无引用缺失）。</summary>
    private void T26_CapabilitySwitches()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var withCanoe = AddEntity(ctx, 0, 100f, TechTable.StoneCore, TechTable.Fire, TechTable.Canoe);
        var noCanoe = AddEntity(ctx, 1, 100f, TechTable.StoneCore);
        var withSeed = AddEntity(ctx, 0, 100f, TechTable.Grinding, TechTable.SeedWheat);
        CivEngine.RefreshCellState(ctx);   // 算 CapMask
        bool canoeOk = CapabilityTable.Has(ctx, withCanoe, "canoe") && !CapabilityTable.Has(ctx, noCanoe, "canoe");
        bool seedOk = CapabilityTable.Has(ctx, withSeed, "seed") && !CapabilityTable.Has(ctx, noCanoe, "seed");
        // 完整性：引用 id 全部注册（不漏不重）
        var ids = new HashSet<string>(CapabilityTable.AllIds());
        bool complete = ids.SetEquals(new HashSet<string> { "canoe", "microlith", "grinding", "fire", "clothing", "seed", "storage" });
        Check("T26 能力开关", canoeOk && seedOk && complete,
            $"canoe开关={canoeOk} seed开关={seedOk} 能力集={string.Join(",", ids)}");
    }
```

`RunScenarios()` 加 `if (Want("T26")) T26_CapabilitySwitches();`

- [ ] **Step 5: 构建 + 测试**

`dotnet build world.csproj` → 0 错误。跑：
```
cd E:/godotGames/world && "D:/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64.exe" --headless --path E:/godotGames/world res://scenes/diag/CivSimDiag.tscn -- --only=T26
```
Expected: `PASS T26 能力开关`（1P/0F）。

- [ ] **Step 6: Commit**

```bash
git add scripts/CivSim/CapabilityTable.cs scripts/CivSim/CivEntity.cs scripts/CivSim/CivEngine.cs scripts/Diagnostics/CivSimDiag.cs
git commit -m "能力开关系统：CapabilityTable（声明式解锁条件+uint位图缓存）+ CapMask（RefreshCellState 每tick）+ 7 内置能力（canoe/microlith/grinding/fire/clothing/seed/storage）；T26 单元通过

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: 迁移 6 处耦合 → Has 查询

**Files:**
- Modify: `scripts/CivSim/CivModels.cs`（ModeModel/InventionModel/ReligionModel/SplitMigrateModel）
- Modify: `scripts/CivSim/CivSimContext.cs`（ColdFloor 签名改）

- [ ] **Step 1: ModeModel 种子判定**（`bool hasSeed = TechTable.HeldSeeds(e.TechKeys).Count > 0;`）

```csharp
            bool hasSeed = CapabilityTable.Has(ctx, e, "seed");
```

- [ ] **Step 2: InventionModel grinding 软前置**（`bool grindOk = e.TechKeys.Contains(TechTable.Grinding);`）

```csharp
            bool grindOk = CapabilityTable.Has(ctx, e, "grinding");
```

- [ ] **Step 3: ReligionModel 萨满条件**（`if (e.Surplus > 0f && e.TechKeys.Contains(TechTable.Microlith))`）

```csharp
            if (e.Surplus > 0f && CapabilityTable.Has(ctx, e, "microlith"))
```

- [ ] **Step 4: SplitMigrateModel 跨海两处**（分裂 `bool canoe = t.TechKeys.Contains(TechTable.Canoe);` 与迁徙 `bool canoe = mover.TechKeys.Contains(TechTable.Canoe);`）

```csharp
        bool canoe = CapabilityTable.Has(ctx, t, "canoe");
```
```csharp
        bool canoe = CapabilityTable.Has(ctx, mover, "canoe");
```

- [ ] **Step 5: ColdFloor 迁移**（CivSimContext.cs；签名从 `ColdFloor(int cell, HashSet<string> keys)` 改 `ColdFloor(CivEntity e)`，内部查能力；FOf 调用处同步）

```csharp
    /// <summary>寒冷区 F 下限（§4.5：火 → 0.05·面积×3；皮毛 → 再 ×3——空间层被技术解锁）。</summary>
    public float ColdFloor(CivEntity e)
    {
        if (!IsColdZone((BiomeType)Grid.Biome[e.Cell])) return 0f;
        if (!CapabilityTable.Has(this, e, "fire")) return 0f;
        float area = Grid.CellAreaKm2;
        float floor = 0.05f * area * 3f;
        if (CapabilityTable.Has(this, e, "clothing")) floor *= 3f;
        return floor;
    }
```
FOf 调用处：
```csharp
    public float FOf(CivEntity e) =>
        Mathf.Max(e.IsFarming ? FFarmActual(e) : FHunt(e), ColdFloor(e));
```
（若 FOf 现为 `ColdFloor(e.Cell, e.TechKeys)` 则改为 `ColdFloor(e)`。）

- [ ] **Step 6: 构建 + 全量回归**

`dotnet build` → 0 错误。跑 `--only=S1,S2,S3,S4,S5,S6,T23,T24,T25,T26` → 全 PASS（等价迁移，行为不变）。再跑地图档 `--arch=user://maps/map_seed100_n32.mpa --only=T01,T02,T04,T08,T14,T21,T22` → 全 PASS。

- [ ] **Step 7: Commit**

```bash
git add scripts/CivSim/CivModels.cs scripts/CivSim/CivSimContext.cs
git commit -m "迁移 6 处耦合到能力查询：ModeModel seed/InventionModel grinding/ReligionModel microlith/SplitMigrate canoe×2/ColdFloor fire+clothing（签名改 CivEntity e）——等价替换，回归全 PASS

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: storage 试点效果（饿死缓冲）

**Files:**
- Modify: `scripts/CivSim/CivModels.cs`（GrowthModel 饿死衰减）
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T27 + 注册）

- [ ] **Step 1: T27 测试**

```csharp
    /// <summary>T27 存储缓冲（Testart 分水岭）：有 storage 部落饿死衰减慢（缺口 ×0.6），无 storage 正常饿死。</summary>
    private void T27_StorageBuffer()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var withS = AddEntity(ctx, 0, 1000f, TechTable.Storage, TechTable.Fire);     // 有存储
        var noS = AddEntity(ctx, 1, 1000f, TechTable.Fire);                          // 无存储
        withS.FLast = 500f; noS.FLast = 500f;   // 缺口 50%（D/F = 2 → 负增长）
        var growth = new GrowthModel();
        for (int t = 0; t < 3; t++) { ctx.Tick = t; growth.Execute(ctx); }
        bool buffered = withS.P > noS.P;   // 有存储的饿死更慢
        bool stillAlive = withS.P > 1f && noS.P > 1f;
        Check("T27 存储缓冲", buffered && stillAlive, $"有存储 P={withS.P:F0} 无存储 P={noS.P:F0}（缺口×0.6 应更慢）");
    }
```
`RunScenarios()` 加 `if (Want("T27")) T27_StorageBuffer();`

- [ ] **Step 2: 实现饿死缓冲**（GrowthModel.Execute 内 `e.P *= factor;` 前加）

```csharp
            // 存储缓冲（Testart 分水岭，2026-08-09）：有 storage 的部落饿死缺口衰减 ×0.6
            //   （无状态效果——不引盈余池入档，读档续跑无分叉；宏观等效饥荒缓冲）
            if (factor < 1f && CapabilityTable.Has(ctx, e, "storage"))
                factor = 1f + (factor - 1f) * CivSimContext.StorageFamineRelief;
```
常量（CivSimContext.cs 常量区）：
```csharp
    public const float StorageFamineRelief = 0.6f;   // 存储饿死缓冲：缺口衰减系数（Testart 分水岭 2026-08-09）
```

- [ ] **Step 3: 构建 + 测试 + 回归**

`dotnet build` → 0 错误。跑 `--only=T27,S1,S4` → T27 PASS（有存储 P > 无存储）、S1/S4 回归（S1 无 storage 科技 → 不变 ✓）。再跑地图档 `--arch=... --only=T01,T02,T04,T08,T14,T21` 记录数据（storage 效果可能改变演化人口/农业——若 T08/T14 漂移，记录数值；T21 目标 >50/≥10 应仍满足）。

- [ ] **Step 4: Commit**

```bash
git add scripts/CivSim/CivModels.cs scripts/CivSim/CivSimContext.cs scripts/Diagnostics/CivSimDiag.cs
git commit -m "storage 试点：饿死缓冲（缺口衰减×0.6，无状态效果避免存档升级；Testart 分水岭激活）；T27 通过

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: 文档同步 + 全量回归

**Files:**
- Modify: `docs/石器时代设计.md`

- [ ] **Step 1: 文档同步**

`docs/石器时代设计.md` 科技一节（§八 techs 表附近）加能力系统说明：

```markdown
### 8.x 能力开关系统（2026-08-09，查询式）

```
能力 = 声明式解锁开关（CapabilityTable.cs）：
  Id + 解锁条件 lambda（科技/状态/环境任意组合）
  缓存：CivEntity.CapMask（uint 位图，RefreshCellState 每 tick）
  查询：CapabilityTable.Has(ctx, e, id) —— 模型效果留原地（查询后计算）
内置能力：canoe / microlith / grinding / fire / clothing / seed / storage
storage 效果（Testart 分水岭激活）：饿死缺口衰减 ×0.6（无状态，读档无分叉）
加新能力（畜牧/贸易/宗教链等）= Register 一条 + 模型查 HasCap，不碰其他模型
上限 32 能力（uint）；依赖链/乘数链不属能力（非开关型）
```
```

- [ ] **Step 2: 全量回归**

跑 `--arch=user://maps/map_seed100_n32.mpa`（无 --only）→ 记录全部 PASS/FAIL 与人口/演化数据。已知可接受漂移：T08/T14/T21 数值（storage 缓冲影响饿死动态）。

- [ ] **Step 3: Commit**

```bash
git add docs/石器时代设计.md
git commit -m "文档：§8.x 能力开关系统（查询式设计、内置 7 能力、storage 效果、上限 32）

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec 覆盖：** §一 能力系统（Task 1）✓；§二 迁移清单 6 处（Task 2——spec 列 7 能力含 storage 试点，storage 在 Task 3）✓；§三 storage 试点（Task 3）✓；§四 测试 T26/T27 + 回归（Task 1/3/4）✓；§五 范围边界（依赖链/乘数链不迁移 ✓；能力 UI/收益选择不做 ✓）；§六 文件（CapabilityTable 新建 ✓、CivEntity/CivEngine/CivModels/CivSimContext/CivSimDiag/文档 ✓）。无缺口。

**占位符扫描：** 无 TBD；所有步骤含完整代码。✓

**类型一致性：** `CapabilityTable.Has(ctx, e, id)` 签名在 Task 1 定义、Task 2/3 使用一致；`CivEntity.CapMask` uint 一致；`ColdFloor(CivEntity e)` 签名在 Task 2 改、FOf 调用同步；`StorageFamineRelief` 在 Task 3 定义使用一致。✓

**已知风险：** T27 断言"有存储 P > 无存储"依赖饿死速率差（3 tick 内 1000→?）；storage 效果会改变演化动态（T08/T14/T21 标定记录）。Task 4 回归时记录。
