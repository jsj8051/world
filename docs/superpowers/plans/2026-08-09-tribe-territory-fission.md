# 部落领地（凝聚体）与裂变压力 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让"部落控制多远"从参数变成涌现现象——领地 = band 凝聚体（连通分量，纯派生不入档），裂变由资源压力+内部张力驱动。

**Architecture:** 新增 `TerritoryModel`（Order 45，每 10 tick 用 Union-Find 重算连通分量，凝聚边 = 同格/邻格 + 同语言群），把 `CivEntity.TerritoryId/TerritorySize` 作为派生字段填充；`SplitMigrateModel` 分裂条件从纯 `P>SplitPop` 改为 `P_eff=P×(1+压力+张力)>SplitPop`；`SpreadModel` 按同/跨领地乘传播；`CivMapArchive.Read` 读档后重算领地（确定性，.cmp v5 格式不变）。

**Tech Stack:** C# / Godot 4.7 mono；现有 CivModels 注册表模式（Order 排序）；TDD 走 CivSimDiag 测试（S/T 构造场景）。

**⚠️ 与 spec 的差异（实施时以本计划为准）：** spec 参数表 SplitPop=400 是文档滞后——**代码实际 SplitPop=300**（CivSimContext.cs:61，2026-08-07 400→300）。本计划全部按 300 标定，Task 6 同步修正 spec。

---

## 文件结构

| 文件 | 责任 |
|---|---|
| `scripts/CivSim/CivEntity.cs` | 加派生字段 `TerritoryId` / `TerritorySize`（不存档） |
| `scripts/CivSim/CivSimContext.cs` | 加裂变/领地常量 + `TerritoryLastRebuild` |
| `scripts/CivSim/CivModels.cs` | 新增 `TerritoryModel`；改 `SplitMigrateModel`（裂变压力、漂变抑制）；改 `SpreadModel`（领地乘数） |
| `scripts/CivSim/CivEngine.cs` | 无（注册表在 CivModels） |
| `scripts/CivSim/CivModelRegistry.cs`（CivModels.cs 内） | StoneAge() 注册 TerritoryModel |
| `scripts/CivSim/CivMapArchive.cs` | Read 末尾 `TerritoryModel.Rebuild(ctx)` |
| `scripts/Diagnostics/CivSimDiag.cs` | 新测试 T22-T25；ArchiveChecks/T02/T04 领地确定性；EntitiesEqual 加字段 |
| `scripts/MapView/MapViewer.cs` | 势力范围图层（第 18 图层，人文类） |
| `docs/石器时代设计.md` | §5.2 同步（邻格分裂 + 裂变压力 + 领地层） |
| `docs/superpowers/specs/2026-08-09-tribe-territory-fission-design.md` | 参数表 400→300 修正 |

**测试运行方式**（headless 在编辑器运行时会被 .godot/mono 锁卡死）：
- 首选 headless：`"D:/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64.exe" --headless --path E:/godotGames/world -- --only=...`（编辑器未运行时）
- 备选（编辑器运行中）：临时场景 `res://scenes/diag/DiagRun.tscn`（脚本 `scripts/Diagnostics/DiagRun.cs`，`_ONLY` 常量改筛选，MCP run_project 跑，看 editor_panel 日志），验证后删除

---

### Task 1: 领地数据结构 + TerritoryModel（凝聚重算）+ 注册 + 读档重建

**Files:**
- Modify: `scripts/CivSim/CivEntity.cs:55`（BornTick 后加字段）
- Modify: `scripts/CivSim/CivSimContext.cs:61-63`（常量区）
- Modify: `scripts/CivSim/CivModels.cs`（新增 TerritoryModel 类，放 SpreadModel 前；注册表 StoneAge 加注册）
- Modify: `scripts/CivSim/CivMapArchive.cs:275`（Read 里 BuildLayer1/RefreshCellState 后加重算）
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T24 测试 + RunScenarios 注册）

- [ ] **Step 1: 写失败测试 T24（凝聚/断裂）**

在 `CivSimDiag.cs` 的 `S6_ReligionLock()` 方法之后加：

```csharp
    /// <summary>T24 领地凝聚/断裂：同格同语言群 → 同领地；语言群分歧 → 领地分裂（确定性，无地图依赖）。</summary>
    private void T24_TerritoryCohesion()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var a = AddEntity(ctx, 0, 200f, TechTable.StoneCore);
        var b = AddEntity(ctx, 0, 200f, TechTable.StoneCore);   // 同格；AddEntity 默认同语言群 test_grp
        ctx.TerritoryLastRebuild = -10;   // 越过频率守卫（Tick=0，0-(-10)=10 ≥ 10）
        new TerritoryModel().Execute(ctx);
        bool united = a.TerritoryId == b.TerritoryId && a.TerritorySize == 2;
        b.CultureGroupShare = ShareField.NewCulture("cultg_999");   // 语言群分歧
        ctx.TerritoryLastRebuild = -10;
        new TerritoryModel().Execute(ctx);
        bool split = a.TerritoryId != b.TerritoryId && a.TerritorySize == 1 && b.TerritorySize == 1;
        Check("T24 领地凝聚/断裂", united && split,
            $"凝聚(同id={a.TerritoryId == b.TerritoryId},size={a.TerritorySize}) 断裂(异id,size=1)");
    }
```

在 `RunScenarios()` 的 `if (Want("S6")) S6_ReligionLock();` 后加：

```csharp
        // 无地图依赖的 T 测试（构造场景风格，S 段注册）
        if (Want("T24")) T24_TerritoryCohesion();
```

- [ ] **Step 2: 运行测试确认失败**

Run: `--only=T24`（headless 或 DiagRun）
Expected: FAIL——`TerritoryModel` 不存在（编译错误）或 TerritoryId 全 -1。

- [ ] **Step 3: 实现领地字段与 TerritoryModel**

在 `CivEntity.cs` 的 `BornTick` 字段（约 55 行）后加：

```csharp
    // ── 领地派生状态（TerritoryModel 凝聚重算填充；不存档——从实体表确定性重算，读档后重建）──
    public int TerritoryId = -1;     // 领地 id = 分量内最小实体 Id（连通分量标号，确定性）
    public int TerritorySize = 1;    // 领地内 band 数（≥2 = 正式领地，触发加成）
```

在 `CivSimContext.cs` 常量区（`SplitShare` 附近）加：

```csharp
    public const float FissionTensionStart = 300f;   // 规模张力起算点（= SplitPop；裂变压力机制 2026-08-09）
    public const float FissionTensionSpan = 250f;    // 张力封顶跨度（300+250=550 → 张力 1.0）
    public const int TerritoryRebuildEvery = 10;     // 凝聚重算间隔 tick（Union-Find，~35 万边/次）
    public const float TerritorySpreadMult = 1.5f;   // 同领地传播乘数（领地整合加成）
    public const float CrossBorderSpreadMult = 0.5f; // 跨领地边界传播乘数（软冲突）
    public const float TerritoryDriftDiv = 0.5f;     // 领地内分裂漂变概率减半（凝聚自稳）
```

在 `CivSimContext.cs` 字段区（`FirstFarmTick` 后）加：

```csharp
    public int TerritoryLastRebuild = -1;   // 最近凝聚重算 tick（TerritoryModel 频率守卫）
```

在 `CivModels.cs` 的 `SpreadModel` 类之前（⑥ 科技传播注释块前）加新类：

```csharp
// ══════════════════════════════════════════════════════════════════
// ⑤ 领地凝聚（Order 45）：band 凝聚体 = 连通分量（每 TerritoryRebuildEvery tick 重算）。
//    凝聚边 = 同格 band 对 或 邻格格代表对 + CultureGroupShare 主导 key 相同 + 双方存活。
//    分量标号 = 分量最小实体 Id（确定性：读档重建 → 续跑无分叉）。纯派生，不入档。
//    距离衰减 = 接触衰减：远格接触少 → 漂变分群 → 边断（零新常量，全部涌现）。
// ══════════════════════════════════════════════════════════════════
public sealed class TerritoryModel : CivModelBase
{
    public override string Name => "领地凝聚";
    public override int Order => 45;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick - ctx.TerritoryLastRebuild < CivSimContext.TerritoryRebuildEvery) return;
        ctx.TerritoryLastRebuild = ctx.Tick;
        Rebuild(ctx);
    }

    /// <summary>重建全部实体领地（读档入口也调用——派生状态从存档确定性重算）。</summary>
    public static void Rebuild(CivSimContext ctx)
    {
        var parent = new Dictionary<int, int>();   // 实体 Id → 并查集父
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        foreach (var e in ctx.Entities)
            if (!e.Dead) parent[e.Id] = e.Id;
        // 同格凝聚边：格内 band 两两，同语言群 → 凝聚
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            for (int a = 0; a < list.Count; a++)
            {
                var ea = list[a];
                if (ea.Dead) continue;
                for (int b = a + 1; b < list.Count; b++)
                {
                    var eb = list[b];
                    if (eb.Dead) continue;
                    if (ShareField.DomKey(ea.CultureGroupShare) == ShareField.DomKey(eb.CultureGroupShare))
                        Union(ea.Id, eb.Id);
                }
            }
        }
        // 邻格凝聚边：格代表对（格内 P 最大）× 邻格代表，同语言群 → 凝聚（其余 band 经同格边挂靠）
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count == 0) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var nbList = ctx.CellTribes[nb];
                if (nbList.Count == 0) continue;
                var repA = MaxPop(list);
                var repB = MaxPop(nbList);
                if (repA == null || repB == null) continue;
                if (ShareField.DomKey(repA.CultureGroupShare) == ShareField.DomKey(repB.CultureGroupShare))
                    Union(repA.Id, repB.Id);
            }
        }
        // 填分量：标号 = 分量最小实体 Id（确定性）；size = 分量实体数
        var sizes = new Dictionary<int, int>();
        var mins = new Dictionary<int, int>();
        foreach (var e in ctx.Entities)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            sizes[root] = sizes.TryGetValue(root, out var v) ? v + 1 : 1;
            if (!mins.TryGetValue(root, out var m) || e.Id < m) mins[root] = e.Id;
        }
        foreach (var e in ctx.Entities)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            e.TerritoryId = mins[root];
            e.TerritorySize = sizes[root];
        }
    }

    private static CivEntity MaxPop(List<CivEntity> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }
}
```

注册（`CivModelRegistry.StoneAge()` 里 `.Register(new SpreadModel())` 之前加）：

```csharp
            .Register(new TerritoryModel())
```

读档重建（`CivMapArchive.cs` Read 末尾 `CivEngine.RefreshCellState(ctx);` 后加）：

```csharp
        TerritoryModel.Rebuild(ctx);   // 领地派生状态重建（确定性；.cmp v5 格式不变）
```

- [ ] **Step 4: 运行测试确认通过**

Run: `--only=T24`
Expected: PASS——凝聚(同id,size=2) 断裂(异id,size=1)

- [ ] **Step 5: Commit**

```bash
git add scripts/CivSim/CivEntity.cs scripts/CivSim/CivSimContext.cs scripts/CivSim/CivModels.cs scripts/CivSim/CivMapArchive.cs scripts/Diagnostics/CivSimDiag.cs
git commit -m "领地层：TerritoryModel(Order45,每10tick Union-Find连通分量)——凝聚边=同格/邻格代表对+同语言群;分量标号=最小实体Id(确定性,读档重建,cmp v5不变);T24 凝聚/断裂通过"
```

---

### Task 2: 裂变压力（资源压力 + 内部张力）

**Files:**
- Modify: `scripts/CivSim/CivModels.cs`（SplitMigrateModel 分裂条件）
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T25 + 注册）

- [ ] **Step 1: 写失败测试 T25**

在 `T24_TerritoryCohesion()` 后加：

```csharp
    /// <summary>T25 裂变压力：大规模(张力1.0)→裂变；盈余小规模(无张力无压力)→不裂（确定性，无地图依赖）。</summary>
    private void T25_FissionPressure()
    {
        // ctxA：P=1000 → 张力 1.0 → P_eff=2000>300 → 必裂；分裂目标=邻格（格1 无人）
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctxA = MakeCtx(gA);
        AddEntity(ctxA, 0, 1000f, TechTable.StoneCore);
        var sm = new SplitMigrateModel();
        sm.Execute(ctxA);
        bool bigFissioned = ctxA.Fissions == 1;
        // ctxB：P=300 盈余（F≈20735 远大于 P）→ 无压力无张力 → P_eff=300 不裂
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctxB = MakeCtx(gB);
        AddEntity(ctxB, 0, 300f, TechTable.StoneCore);
        sm.Execute(ctxB);
        bool smallKept = ctxB.Fissions == 0;
        Check("T25 裂变压力", bigFissioned && smallKept,
            $"大规模分裂={bigFissioned}(Fissions={ctxA.Fissions}) 盈余300不裂={smallKept}(Fissions={ctxB.Fissions})");
    }
```

`RunScenarios()` 里 `if (Want("T24")) T24_TerritoryCohesion();` 后加 `if (Want("T25")) T25_FissionPressure();`

- [ ] **Step 2: 运行测试确认失败**

Run: `--only=T25`
Expected: FAIL——P=1000 实体用旧逻辑也分裂（Fissions==1 会过？不——旧逻辑 P>300 也分裂，所以 bigFissioned 过，但 smallKept 也过（300 不裂）——**旧逻辑下 T25 可能全过**。确认失败方式：断言必须体现新逻辑——`P=250 盈余` 在旧逻辑不裂、新逻辑也不裂——不行。改为断言**新逻辑特有的行为**：盈余 250 人（P_eff=250）不裂 + 饥荒 250 人（F=125）裂变——旧逻辑两者都不裂（P 都 <300）→ 新逻辑下饥荒 250 裂 → 测试在旧代码下 FAIL ✓

修正测试（用下面代码替换 Step 1 的 T25 实现）：

```csharp
    private void T25_FissionPressure()
    {
        // ctxA：饥荒 P=250, F=125（压力 0.5）→ P_eff=375>300 → 裂变（旧逻辑 P<300 不裂 → 此测试在旧代码下 FAIL）
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctxA = MakeCtx(gA);
        var famine = AddEntity(ctxA, 0, 250f, TechTable.StoneCore);
        ctxA.CellF[0] = 125f;          // 压低格产出（RefreshCellState 未跑，直接设）
        famine.FLast = 125f;           // FLast 供裂变压力计算
        var sm = new SplitMigrateModel();
        sm.Execute(ctxA);
        bool famineFissioned = ctxA.Fissions == 1;
        // ctxB：盈余 P=300（FLast 大）→ 无压力无张力 → P_eff=300 不裂
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctxB = MakeCtx(gB);
        var fed = AddEntity(ctxB, 0, 300f, TechTable.StoneCore);
        fed.FLast = 600f;
        sm.Execute(ctxB);
        bool fedKept = ctxB.Fissions == 0;
        Check("T25 裂变压力", famineFissioned && fedKept,
            $"饥荒250裂变={famineFissioned}(Fissions={ctxA.Fissions}) 盈余300不裂={fedKept}(Fissions={ctxB.Fissions})");
    }
```

⚠️ 关键前提：`SplitMigrateModel.Execute` 里饱和迁徙用 `ctx.CellF[i]`（格产出）与 `ctx.CellPop[i]`（格人口）——ctxA 里 CellPop 未设（RefreshCellState 未跑）→ CellPop[0]=0 → 饱和迁徙判定 `P < MigrateThreshold * F` → 0 < 0.75×125 ✓ 不饱和。探路用 `ctx.Rng` ✓。分裂判定用 `t.FLast`（新实现）✓。旧代码 `t.P <= SplitPop continue` → 250 ≤ 300 → 跳过 → Fissions=0 → famineFissioned=false → **测试 FAIL** ✓ TDD 正确。

- [ ] **Step 3: 实现裂变压力**

`SplitMigrateModel.Execute` 分裂循环开头（`foreach (var t in snapshot)` 内，`if (t.Dead) continue;` 后）：

原文：
```csharp
            if (t.Dead) continue;
            if (t.P <= CivSimContext.SplitPop) continue;
```

改为：
```csharp
            if (t.Dead) continue;
            // 裂变压力（2026-08-09 用户拍板：资源压力+内部张力涌现，替代纯 P>SplitPop）：
            //   P_eff = P × (1 + 资源压力 + 内部张力)
            //   资源压力 = max(0, 1 − F/P)：饥荒缺口 → 提前裂变求存
            //   内部张力 = min(1, (P−300)/250)：超 SplitPop 后规模压力线性升，550 封顶
            float tension = Mathf.Min(1f, (t.P - CivSimContext.FissionTensionStart) / CivSimContext.FissionTensionSpan);
            float pEff = t.P * (1f + Mathf.Max(0f, 1f - t.FLast / t.P) + tension);
            if (pEff <= CivSimContext.SplitPop) continue;
```

（`t.FLast` 由 `RefreshCellState` 在 ExecuteAll 前填好；Task 2 测试里手工设值。）

- [ ] **Step 4: 运行测试确认通过**

Run: `--only=T25`
Expected: PASS——饥荒250裂变=True 盈余300不裂=True

- [ ] **Step 5: Commit**

```bash
git add scripts/CivSim/CivModels.cs scripts/Diagnostics/CivSimDiag.cs
git commit -m "裂变压力：P_eff=P×(1+max(0,1−F/P)+min(1,(P−300)/250))>SplitPop 替代纯阈值——饥荒期提前分家求存、盈余可长大;T25 通过"
```

---

### Task 3: 领地传播乘数 + 漂变抑制

**Files:**
- Modify: `scripts/CivSim/CivModels.cs`（SpreadModel.SpreadTech + TerritoryMult；SplitMigrateModel 漂变三处）
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T23 + 注册）

- [ ] **Step 1: 写失败测试 T23（TerritoryMult 单元）**

在 `T25_FissionPressure()` 后加：

```csharp
    /// <summary>T23 领地传播乘数（单元，无地图依赖）：同领地 ×1.5；跨领地（一方 ≥2 band）×0.5；散兵 ×1。</summary>
    private void T23_TerritoryMult()
    {
        var a = new CivEntity { TerritoryId = 7, TerritorySize = 2 };
        var b = new CivEntity { TerritoryId = 7, TerritorySize = 2 };
        var c = new CivEntity { TerritoryId = 9, TerritorySize = 2 };
        var d = new CivEntity { TerritoryId = -1, TerritorySize = 1 };
        var e = new CivEntity { TerritoryId = -1, TerritorySize = 1 };
        float same = SpreadModel.TerritoryMult(a, b);
        float cross = SpreadModel.TerritoryMult(a, c);
        float lone = SpreadModel.TerritoryMult(d, e);
        bool ok = same == CivSimContext.TerritorySpreadMult
               && cross == CivSimContext.CrossBorderSpreadMult
               && lone == 1f;
        Check("T23 领地传播乘数", ok, $"同领地×{same} 跨领地×{cross} 散兵×{lone}");
    }
```

`RunScenarios()` 里 `if (Want("T25")) ...` 后加 `if (Want("T23")) T23_TerritoryMult();`

- [ ] **Step 2: 运行测试确认失败**

Run: `--only=T23`
Expected: FAIL——`TerritoryMult` 未定义（编译错误）。

- [ ] **Step 3: 实现传播乘数与漂变抑制**

`SpreadModel.SpreadTech` 方法体开头加乘数（`private void SpreadTech(...)` 内第一行）：

```csharp
        float terr = TerritoryMult(from, to);   // 领地乘数（同领地×1.5 / 跨领地×0.5 / 散兵×1）
```

`SpreadTech` 内概率行 `float p = t.SpreadBase * border;` 改为：

```csharp
            float p = t.SpreadBase * border * terr;
```

`SpreadModel` 类内（`MaxPop` 方法后）加：

```csharp
    /// <summary>领地传播乘数：同领地 ×1.5（整合加成）；至少一方是正式领地（≥2 band）→ ×0.5（跨边界软冲突）；散兵部落间 ×1（BorderCost 已有）。</summary>
    internal static float TerritoryMult(CivEntity a, CivEntity b)
    {
        if (a.TerritoryId >= 0 && a.TerritoryId == b.TerritoryId) return CivSimContext.TerritorySpreadMult;
        if (a.TerritorySize >= 2 || b.TerritorySize >= 2) return CivSimContext.CrossBorderSpreadMult;
        return 1f;
    }
```

`SplitMigrateModel` 漂变三处（`if (ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)` ×3，分别在文化/文化群/宗教派别分化注释后）——在分裂循环里（`if (t.Dead) continue;` 与裂变压力判定之后）加一次：

```csharp
            // 领地自稳：母 band 在 ≥2 band 领地内 → 分裂漂变概率减半（凝聚抑制方言漂变）
            float drift = t.TerritorySize >= 2
                ? CivSimContext.CultureDriftChance * CivSimContext.TerritoryDriftDiv
                : CivSimContext.CultureDriftChance;
```

并把三处 `ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance` 改为 `ctx.Rng.NextDouble() < drift`。

- [ ] **Step 4: 运行测试确认通过**

Run: `--only=T23,T24,T25`
Expected: 全部 PASS（T24/T25 回归不受影响）

- [ ] **Step 5: Commit**

```bash
git add scripts/CivSim/CivModels.cs scripts/Diagnostics/CivSimDiag.cs
git commit -m "领地加成：同领地传播×1.5/跨领地×0.5(软冲突边界)/散兵×1;领地内分裂漂变减半(凝聚自稳);T23 乘数单元通过"
```

---

### Task 4: T22 领地涌现 + 存档往返领地确定性（T02/T04 扩展）

**Files:**
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T22、ArchiveChecks、EntitiesEqual、needEvol 列表）

- [ ] **Step 1: 写测试 T22 + 扩展 EntitiesEqual + ArchiveChecks 强制重算**

`RunMapTests` 的 `needEvol` 列表 `WantAny("T03", "T05", ...` 里加 `"T22"`（改成 `"T03", "T05", "T08", "T09", "T10", "T11", "T13", "T14", "T15", "T16", "T17", "T21", "T22", "存档"`）。

`RunMapTests` 末尾（T21 调用后）加：

```csharp
        if (Want("T22")) T22_TerritoryEmergence(c);
```

在 `T21_PopGradient` 方法后加：

```csharp
    /// <summary>T22 领地涌现（演化后）：存在 ≥2 band 的凝聚体（领地 = 现实地域部落；需演化 r1）。
    /// ⚠️ 若 FAIL：领地碎片化过重（漂变 5% 全碎）——调 TerritoryRebuildEvery 或 TerritoryDriftDiv。</summary>
    private void T22_TerritoryEmergence(CivSimContext c)
    {
        var ids = new HashSet<int>();
        int inTribe = 0;
        foreach (var e in c.Entities)
            if (e.TerritorySize >= 2) { ids.Add(e.TerritoryId); inTribe++; }
        bool emerged = ids.Count >= 1;
        Check("T22 领地涌现", emerged, $"领地 {ids.Count} 个（≥2 band），成员 band {inTribe} 个");
    }
```

`EntitiesEqual`（约 811 行）的字段对比行加领地字段：

```csharp
            if (x.Id != y.Id || x.Cell != y.Cell || x.P != y.P || x.IsFarming != y.IsFarming
                || x.OriginCell != y.OriginCell || x.BornTick != y.BornTick
                || x.TerritoryId != y.TerritoryId || x.TerritorySize != y.TerritorySize)
```

`ArchiveChecks` 的 Read 成功块内（`rtOk = EntitiesEqual(c, rBack.Context);` 前）加强制重算（领地是滞后重算的派生状态——对比前两边强制 Rebuild，验证"同状态→同领地"确定性）：

```csharp
                // 领地是滞后重算的派生状态——对比前强制重算（确定性：同状态 → 同领地）
                TerritoryModel.Rebuild(c);
                TerritoryModel.Rebuild(rBack.Context);
                rtOk = EntitiesEqual(c, rBack.Context);
```

`ArchiveChecks` 的 T04 段（`contOk = EntitiesEqual(rBack.Context, ctxFull);` 前）同样加：

```csharp
                TerritoryModel.Rebuild(rBack.Context);
                TerritoryModel.Rebuild(ctxFull);
                contOk = EntitiesEqual(rBack.Context, ctxFull);
```

- [ ] **Step 2: 运行测试确认失败**

Run: `--only=T22`（需要 `--arch=user://maps/map_seed100_n32.mpa`）
Expected: FAIL——T22 断言失败（旧代码 TerritorySize 全 1）或编译错误。若 T22 意外 PASS（演化碎不了）也接受，继续 Step 3。

- [ ] **Step 3: 运行全量存档往返回归**

Run: `--only=T01,T02,T04,存档`
Expected: 全 PASS（Task 1 的 Read 重建 + 本 Task 的强制 Rebuild 已生效）。若 FAIL 检查 `CivMapArchive.Read` 是否已调 `TerritoryModel.Rebuild`（Task 1 Step 3）。

- [ ] **Step 4: Commit**

```bash
git add scripts/Diagnostics/CivSimDiag.cs
git commit -m "T22 领地涌现验收;T02/T04 存档往返加领地确定性(对比前强制 Rebuild,验证同状态→同领地)"
```

---

### Task 5: MapViewer 势力范围图层（第 18 图层，人文类）

**Files:**
- Modify: `scripts/MapView/MapViewer.cs`

- [ ] **Step 1: 图层注册**

`LayerCats` 数组末尾（`LayerCat.Human,     // 16 宗教` 后）加：

```csharp
        LayerCat.Human,     // 17 势力范围
```

`LayerNames` 数组末尾加 `"势力范围"`（改成 `... "科技", "宗教", "势力范围" }`）。

`_tileTribe` 缓存声明（约 454 行）后加：

```csharp
        _tileTerritory = new int[n];
```

字段声明区（`_tileCultureGroup` 附近）加：

```csharp
    private int[] _tileTerritory;   // 每格主导 band 的领地（语言群 key 完整哈希；0=无领地）
```

- [ ] **Step 2: 填充**

`MapViewer.cs` 文明图层填充块（`_tileCulture[i] = ...` 约 509 行，`_tileTribe[i] = dom.Id;` 后）加：

```csharp
                    _tileTerritory[i] = World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(dom.CultureGroupShare));
```

（完整 32 位哈希——与 byte 截断的 `_tileCultureGroup` 区分；同领地必同语言群 → 同领地同色。）

- [ ] **Step 3: 上色分支**

上色 switch（`case 13: // 文化` 分支后）加：

```csharp
                                case 17: // 势力范围：每领地独立色（语言群 key 完整哈希 → 黄金角 HSL；0=无人/无领地灰）
                                    int terr = _tileTerritory[id];
                                    if (terr == 0 || _tilePop[id] <= 0f) color = new Color(0.30f, 0.32f, 0.36f);
                                    else { double hue = GoldenHue(terr); color = Color.FromHsv((float)hue, 0.55f, 0.85f); }
                                    break;
```

（参考 `case 13` 的现有实现结构——`GoldenHue` 已存在，若 case 13 用别的上色函数则照抄其模式。）

- [ ] **Step 4: 图例分支**

图例 switch（`case 13: // 文化：动态条目` 附近）加：

```csharp
            case 17: // 势力范围：静态说明
                AddLegendText("势力范围：每领地（语言群凝聚体）独立色；灰=无人/无领地");
                break;
```

（`AddLegendText` 为现有图例辅助方法名——若实际名不同，照 `case 12` 的模式调用。）

- [ ] **Step 5: 构建 + 运行验证**

Run: `dotnet build world.csproj` → 0 错误；`--map=user://maps/map_seed100_n32.cmp` 打开 MapViewer，切到第 18 图层（编辑器内验证或 `--only` 无需——直接跑场景）。
Expected: 势力范围图层显示领地色块（同领地同色、边界清晰、无领地格灰色）。

- [ ] **Step 6: Commit**

```bash
git add scripts/MapView/MapViewer.cs
git commit -m "MapViewer 第18图层 势力范围：每格按主导band语言群完整哈希上色(同领地同色);图例说明"
```

---

### Task 6: 文档同步（设计文档 + spec 参数修正）

**Files:**
- Modify: `docs/石器时代设计.md`
- Modify: `docs/superpowers/specs/2026-08-09-tribe-territory-fission-design.md`

- [ ] **Step 1: 修正 spec 参数（SplitPop 400 → 300）**

`2026-08-09-tribe-territory-fission-design.md`：
- §五 裂变公式注释与情形表：`P_eff > 400` → `P_eff > 300`；情形表重算：盈余 P=300（tension 0）不裂 / 盈余 P=350（tension 0.2，P_eff=420）裂 / 饥荒 P=250 F=125（P_eff=375）裂 / 巨型 P=800 连裂至回落 ~300。
- §九 参数表 `SplitPop | 400（不变）` → `SplitPop | 300（不变，2026-08-07 已降）`。

- [ ] **Step 2: 更新 `docs/石器时代设计.md`**

§5.2 分裂节（`### 5.2 分裂（segmentary lineage，Order 80 前段）`）替换为：

```markdown
### 5.2 分裂（segmentary lineage，Order 80 前段；2026-08-09 裂变压力 + 邻格优先）

```
裂变压力（用户拍板 2026-08-09：资源压力+内部张力涌现，替代纯 P>SplitPop）：
  P_eff = P × (1 + 资源压力 + 内部张力) > SplitPop=300（★）→ 裂变
  资源压力 = max(0, 1 − F/P)：饥荒缺口 → 提前裂变求存
  内部张力 = min(1.0, (P−300)/250)：超 SplitPop 后规模压力线性升，550 封顶
分裂目标（代码现状，文档 2026-08-09 同步）：无人陆地邻格优先 → 低密度邻格
  → canoe 跨 1 格海；无目标且母格未满 → 留母格（邻格扩散 = 领地形成引擎）
新部落带走 45%（SplitShare ★），身份份额等比例继承（人口分走，文化/文化群/宗教随人口走）
TechKeys 完整继承（分裂瞬间技术相同，此后各自发明/学习——不因同源共享）
文化群分化：分裂时 5% 概率生成新文化群 id（方言→语言群，漂变）；母 band 在 ≥2 band
  领地内 → 概率减半 2.5%（凝聚自稳，2026-08-09）
格内上限 MaxTribesPerCell = 8（★ 性能 + 社会密度双约束，超限不分裂、迁徙优先）
快照遍历（新部落下 tick 再判，防同 tick 连锁）
```

并新增一节（§5.2 后）：

```markdown
### 5.2a 领地层：band 凝聚体（2026-08-09，纯派生不入档）

```
凝聚边（每 TerritoryRebuildEvery=10 tick 重算，Union-Find）：
  同格 band 两两 或 邻格格代表对（格内 P 最大）× 邻格代表
  + CultureGroupShare 主导 key 相同 + 双方存活 → 连通分量 = 领地
分量标号 = 分量最小实体 Id（确定性：读档重建 → 续跑无分叉；.cmp v5 格式不变）
领地加成（分量 ≥2 band）：
  · 同领地技术传播 ×1.5（整合）；跨领地传播 ×0.5（边界软冲突，无武力）
  · 分裂漂变概率 5% → 2.5%（凝聚自稳）
距离衰减 = 接触衰减：远格接触少 → 漂变分群 → 凝聚边断（无资格线/无硬上限，全涌现）
吞并 = 同化自然延伸：弱 band 语言群份额被吞没 → 主导 key 变更 → 重算时并入领地
```

- [ ] **Step 3: Commit**

```bash
git add docs/石器时代设计.md docs/superpowers/specs/2026-08-09-tribe-territory-fission-design.md
git commit -m "文档同步：§5.2 邻格优先分裂+裂变压力(300基准)+领地层§5.2a;spec 参数表 SplitPop 400→300 修正"
```

---

### Task 7: 全量回归 + 最终提交

**Files:** 无（验证 + 提交）

- [ ] **Step 1: 全量 S 场景回归**

Run: `--only=S1,S2,S3,S4,S5,S6,T23,T24,T25`
Expected: 全 PASS（S4 分裂断言：P=500 盈余 P_eff=500×(1+0+0.8)=900>300 仍分 ✓）

- [ ] **Step 2: 全量 T 地图回归**

Run: `--arch=user://maps/map_seed100_n32.mpa --only=T01,T02,T03,T04,T05,T08,T09,T10,T11,T13,T14,T15,T16,T17,T21,T22,T19,存档`
Expected: 全 PASS。已知可接受 FAIL：无（若 T22 FAIL → 按 T22 注释调参；若 T16/T10 等演化指标漂移 → 记录并标定参数，不硬改断言）。

- [ ] **Step 3: 全量默认跑（无 --only）**

Run: `--arch=user://maps/map_seed100_n32.mpa`
Expected: 与 Step 2 相同集合（T18 性能可能 FAIL——已知热点在传播/宗教等既有模型，与领地机制无关则记录）。

- [ ] **Step 4: 最终提交（如有未提交改动）**

```bash
git status  # 确认工作区干净
git log --oneline -8   # 领地系列提交序列可见
```

---

## Self-Review（计划自审）

**Spec 覆盖：** §一 现实标定（文档 Task 6 写入）；§二 凝聚机制（Task 1）；§三 加成/边界（Task 3）；§四 吞并（同化自然延伸，无代码——设计即实现，Task 1 的凝聚重算天然支持）；§五 裂变压力（Task 2）；§六 可视化（Task 5）；§七 存档不变（Task 1 Read 重建 + Task 4 确定性验证）；§八 文档同步（Task 6）；§九 参数表（Task 1-3 常量 + Task 6 spec 修正）；§十 测试（Task 1-4）；§十一 范围边界（无代码——已明确不做）。✓

**占位符扫描：** 无 TBD/TODO；Task 5 的 `case 13` 模式引用有说明（GoldenHue/AddLegendText 已确认存在或说明替代方案）。✓

**类型一致性：** `TerritoryId/TerritorySize`（int）在 Task 1 定义、Task 3/4/5 使用一致；`TerritoryModel.Rebuild` static 在 Task 1 定义、Task 1（Read）/Task 4（ArchiveChecks）调用一致；`SpreadModel.TerritoryMult` internal static 在 Task 3 定义、T23 调用一致；`FissionTensionStart/Span`、`TerritorySpreadMult/CrossBorderSpreadMult/TerritoryDriftDiv/TerritoryRebuildEvery` 常量名在 Task 1 定义、Task 2/3 使用一致。✓

**已知风险：** T22 可能因漂变碎片化 FAIL（任务内已注明调参路径）；T16/T10 演化指标可能随分裂更频漂移（Task 7 记录不硬改）。
