# 生产方式重构（并行混合经济）+ 畜牧 + 货物系统 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 生产方式从"择一"改为**并行混合经济**（hunt/herd/farm PM 表，收益权重分配土地），新增畜牧（livestock 科技 + WildLivestock 生态位）与货物系统（皮革/羊毛/秸秆，入档 .cmp v7）。

**Architecture:** FOf 重构 = Σ 各方式产出（权重 w_k = 方式潜在全地产出，份额 s_k = w_k/Σw，劳动爬坡 min(1, P/P_劳动)）；ModeModel 转农开关语义保留（IsFarming 演化字段）；畜牧派生启用（livestock 能力 + WildLivestock 位）；货物 Goods float[3] 每 tick 按 F 分量累积并入档。

**Tech Stack:** C# / Godot 4.7 mono；techs.csv 数据驱动；WildCropsSystem 同构扩展；.cmp v7（实体段 +12B）。

---

### Task 1: 基础设施——Goods 字段 + livestock 科技/能力 + WildLivestock + .cmp v7

**Files:**
- Modify: `scripts/CivSim/CivEntity.cs`（Goods 字段）
- Modify: `scripts/CivSim/TechTable.cs`（Livestock 常量）
- Modify: `data/techs.csv`（livestock 行）
- Modify: `scripts/CivSim/CapabilityTable.cs`（livestock 能力）
- Modify: `scripts/LogicGrid/GameGrid.cs`（WildLivestock + Ensure）
- Modify: `scripts/MapGen/WildCropsSystem.cs`（ComputeLivestock）
- Modify: `scripts/CivSim/CivMapArchive.cs`（v7：实体段 +12B、Peek +12）
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T19 版本更新）

- [ ] **Step 1: CivEntity 加 Goods 字段**（`CapMask` 后）

```csharp
    // ── 货物库存（副产品累积；入档 .cmp v7——每实体 3×float 12B）──
    public float[] Goods = new float[3];   // 0=皮革 1=羊毛 2=秸秆（GoodsTable 索引）
```

- [ ] **Step 2: GoodsTable 常量**（CivSimContext.cs 常量区加）

```csharp
    // ── 货物系统（2026-08-09：生产方式副产品，累积入档；贸易期接物物交换）──
    public const int GoodsLeather = 0, GoodsWool = 1, GoodsStraw = 2;   // Goods[] 索引
    public const float LeatherRate = 0.10f;   // 狩猎产出 → 皮革（★ 标定）
    public const float WoolRate = 0.15f;      // 畜牧产出 → 羊毛（★ 标定）
    public const float StrawRate = 0.05f;     // 农业产出 → 秸秆（★ 标定）
    public const float HerdMult = 2.0f;       // 畜牧单位土地产出倍率（"少许土地产生食物"；★ 标定）
```

- [ ] **Step 3: TechTable 常量 + techs.csv 行**

`TechTable.cs` 常量区（`Pottery` 后）加：
```csharp
    public const string Livestock = "livestock";
```

`data/techs.csv`（grinding 行后加）：
```
livestock,畜牧,grass,0.01,200,0.02,grinding,carry:1.1
```

- [ ] **Step 4: 能力注册**（CapabilityTable.EnsureInited 内，storage 后加）

```csharp
        Register(new Capability { Id = "livestock", Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Livestock)
            && c.Grid.EnsureWildLivestock()[e.Cell] != 0 });
```

- [ ] **Step 5: WildLivestock 生成**（GameGrid + WildCropsSystem）

`GameGrid.cs`（WildCrops 字段/Ensure 旁）加：
```csharp
    public byte[] WildLivestock;   // 野生畜牧位（1=草原可驯；确定性重建，不存档）
    public byte[] EnsureWildLivestock() => WildLivestock ??= World.MapGen.WildCropsSystem.ComputeLivestock(this, Seed);
```

`WildCropsSystem.cs`（Compute 方法后加）：
```csharp
    /// <summary>野生畜牧位（bitmask 1 位；2026-08-09）：草原类 biome（HotSteppe/ColdSteppe/TropicalSavanna/
    /// MediterraneanHot/MediterraneanCool）+ 年降水 300-1200mm → 可驯。同 WildCrops 同构：确定性重建不入档。</summary>
    public static byte[] ComputeLivestock(GameGrid g, int seed)
    {
        int n = g.N;
        var bits = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (!g.IsLandCell(i)) continue;
            var b = (Biome.BiomeType)g.Biome[i];
            bool grass = b is Biome.BiomeType.HotSteppe or Biome.BiomeType.ColdSteppe
                       or Biome.BiomeType.TropicalSavanna or Biome.BiomeType.MediterraneanHot
                       or Biome.BiomeType.MediterraneanCool;
            if (!grass) continue;
            float precip = g.Precip[i];   // mm/年（度单位存储？以现有 Precip 语义为准——若为 mm 直接用）
            if (precip >= 300f && precip <= 1200f) bits[i] = 1;
        }
        return bits;
    }
```
⚠️ 若 `g.Precip` 单位不是 mm（需要确认——Temp/Precip 字段语义见 GameGrid），按实际单位调整阈值。

- [ ] **Step 6: .cmp v7**（CivMapArchive）

- `Version = 7`（`public const ushort Version = 6;` → 7）；类文档 v6→v7 说明（+12B 货物）
- `CompatibleArchiveVersions` 注释更新（v7 说明）
- **Write 实体段**（`f.Store32((uint)e.BornTick);` 后加）：
```csharp
            for (int gi = 0; gi < 3; gi++) f.StoreFloat(e.Goods[gi]);   // 货物 3×float（v7）
```
- **Read 实体段**（`e.BornTick = (int)f.Get32();` 后加）：
```csharp
            for (int gi = 0; gi < 3; gi++) e.Goods[gi] = f.GetFloat();   // 货物（v7）
```
- **Peek per-entity skip**（`long skip = 16L * keyCount + 34 + 34 + 5 + 34 + 12;` → +12）：
```csharp
            long skip = 16L * keyCount + 34 + 34 + 5 + 34 + 12 + 12;   // keys + 份额×3 + Cell/OriginCell/BornTick + 货物3×float(v7)
```

- [ ] **Step 7: T19 版本测试更新**（CivSimDiag.cs）

`WriteBadVersion(badPath, 7);` → `8`（ver>7 拒绝）；`WriteBadVersion(badPath, 5);` → `6`（v6 旧档拒绝）；Check 数据字符串 `ver>6` → `ver>7`、`v5/v4` → `v6/v5/v4`。

- [ ] **Step 8: 构建 + 回归**

`dotnet build` → 0 错误。跑 `--arch=user://maps/map_seed100_n32.mpa --only=T01,T02,T04,T19,T26` → 全 PASS（v7 往返 + 版本拒绝 + 能力）。

- [ ] **Step 9: Commit**

```bash
git add scripts/CivSim/CivEntity.cs scripts/CivSim/CivSimContext.cs scripts/CivSim/TechTable.cs data/techs.csv scripts/CivSim/CapabilityTable.cs scripts/LogicGrid/GameGrid.cs scripts/MapGen/WildCropsSystem.cs scripts/CivSim/CivMapArchive.cs scripts/Diagnostics/CivSimDiag.cs
git commit -m "基础设施：Goods 货物字段(3×float 入档)+livestock 科技(techs.csv)+能力注册+WildLivestock 生态位(同 WildCrops 同构确定性重建);.cmp v7(实体段+12B, v6 拒);T19 更新

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: 生产方式并行重构（FOf Σ + 收益权重分配 + 货物累积）

**Files:**
- Modify: `scripts/CivSim/CivSimContext.cs`（FOf/FHunt/FHerds 重构 + 权重 + F 分量字段）
- Modify: `scripts/CivSim/CivEngine.cs`（RefreshCellState 货物累积）
- Modify: `scripts/CivSim/CivEntity.cs`（F 分量派生字段）

- [ ] **Step 1: F 分量字段**（CivEntity.cs，Goods 旁）

```csharp
    // ── 生产方式 F 分量（派生缓存：RefreshCellState 每 tick；不存档）──
    public float FHuntLast, FHerdLast, FFarmLast;   // 各方式当 tick 产出（货物分解用）
```

- [ ] **Step 2: FOf 重构为并行 Σ**（CivSimContext.cs；替换现有 `FOf` 与 `FHunt`——FHunt 保留给 e_猎 比较但内部改调用新结构）

```csharp
    /// <summary>格内活部落数（土地份额分母）。</summary>
    private int NTribes(int cell)
    {
        int n = 0;
        var tl = CellTribes != null ? CellTribes[cell] : null;
        if (tl != null)
            foreach (var o in tl)
                if (!o.Dead) n++;
        return Mathf.Max(1, n);
    }

    /// <summary>生产方式并行产出（2026-08-09 用户拍板：混合经济 + 收益权重土地分配，Vic3/EU5 PM 参考）：
    /// 部落方式集 M = {hunt} ∪ {herd if livestock能力+生态位} ∪ {farm if IsFarming}；
    /// 权重 w_k = 方式潜在全地产出（R_k×A×m_k）；土地份额 s_k = w_k/Σw；
    /// 实际 F_k = w_k×s_k×min(1, P/(LaborFrac×w_k×s_k))（份额劳动爬坡）；
    /// 总产出 = ΣF_k。单方式时退化为现 FHunt/FFarmActual 语义。</summary>
    public float FOf(CivEntity e)
    {
        float m = e.CarryMult > 0f ? e.CarryMult : TechTable.HuntingCarry(e.TechKeys);
        float A = Grid.CellAreaKm2 / NTribes(e.Cell);
        float pHunt = R[e.Cell] * A * m;
        float pHerd = CapabilityTable.Has(this, e, "livestock") ? R[e.Cell] * HerdMult * A * m : 0f;
        float pFarm = e.IsFarming ? FFarmPotential(e) : 0f;
        float sw = pHunt + pHerd + pFarm;
        float floor = ColdFloor(e);
        if (sw <= 0f) return floor;
        float fHunt = pHunt * (pHunt / sw) * Mathf.Min(1f, e.P / Mathf.Max(1f, LaborFrac * pHunt * (pHunt / sw)));
        float fHerd = pHerd * (pHerd / sw) * Mathf.Min(1f, e.P / Mathf.Max(1f, LaborFrac * pHerd * (pHerd / sw)));
        float fFarm = pFarm * (pFarm / sw) * Mathf.Min(1f, e.P / Mathf.Max(1f, LaborFrac * pFarm * (pFarm / sw)));
        e.FHuntLast = fHunt; e.FHerdLast = fHerd; e.FFarmLast = fFarm;   // 分量缓存（货物分解）
        return Mathf.Max(fHunt + fHerd + fFarm, floor);
    }
```

`FHunt(e)` 保留（生产方式选择比较用——全份额含劳动，语义同现）不动。`FFarmPotential` 保留。

⚠️ 注意：现有 `FHunt` 含劳动爬坡且用于 e_猎 比较——并行后 e_猎 比较仍用 FHunt（全份额）——但产出已并行。S2/T08 的转农开关比较逻辑不变。

- [ ] **Step 3: RefreshCellState 货物累积**（CivEngine.cs 第二遍，`e.FLast = ctx.FOf(e);` 后加）

```csharp
            // 货物累积（副产品 = 各方式 F × 副产率；2026-08-09）
            e.Goods[CivSimContext.GoodsLeather] += e.FHuntLast * CivSimContext.LeatherRate;
            e.Goods[CivSimContext.GoodsWool] += e.FHerdLast * CivSimContext.WoolRate;
            e.Goods[CivSimContext.GoodsStraw] += e.FFarmLast * CivSimContext.StrawRate;
```

- [ ] **Step 4: T30 权重分配测试**（CivSimDiag.cs，T27 后加 + 注册）

```csharp
    /// <summary>T30 收益权重土地分配（单元）：草原格 牧2×猎 → 牧份额 2/3、猎 1/3（无农）——s_k = w_k/Σw。</summary>
    private void T30_WeightAllocation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);   // 草原
        var ctx = MakeCtx(g);
        g.WildLivestock = new byte[] { 1, 0 };   // 格0 可牧
        var e = AddEntity(ctx, 0, 1000f, TechTable.Livestock, TechTable.StoneCore);
        e.IsFarming = false;
        CivEngine.RefreshCellState(ctx);
        float f = ctx.FOf(e);
        // 权重：pHunt = R×A×1.1；pHerd = R×2×A×1.1 → s_herd = 2/3
        bool herdShare = Mathf.Abs(e.FHerdLast - 2f * e.FHuntLast) < e.FHuntLast * 0.05f;
        bool huntActive = e.FHuntLast > 0f;   // 并行：猎仍产出
        Check("T30 收益权重分配", herdShare && huntActive,
            $"牧F={e.FHerdLast:F0} 猎F={e.FHuntLast:F0}（应 2:1）总={f:F0}");
    }
```
`RunScenarios()` 加 `if (Want("T30")) T30_WeightAllocation();`

⚠️ 断言依据：单部落格 A=全格；m=1.1（stone_core）；pHerd = 2×pHunt → s_herd=2/3 → fHerd = pHerd×(2/3)×min(...)；fHunt = pHunt×(1/3)×min(...)。劳动爬坡：P=1000 vs 0.1×份额潜在（0.1×2/3×pHerd≈0.07×R×A×m≈1400？P=1000 < 1400 → 劳动不足 → 两方式都受 min 限制——**比例仍 2:1**（min 用同 P，比率 = pHerd×s_herd/(pHunt×s_hunt) = (2×(2/3))/(1×(1/3)) = 4/3/1/3 = 4:1？不对——min(1, P/(0.1×P_k×s_k))：分母 0.1×P_k×s_k ∝ P_k×s_k → 比率 2×(2/3):1×(1/3) = 4:1？？让我重算：
fHerd/fHunt = [pHerd×s_herd×min(1,P/(0.1×pHerd×s_herd))] / [pHunt×s_hunt×min(1,P/(0.1×pHunt×s_hunt))]
若两者都劳动不足（P 小于两个 0.1×P×s）：min = P/(0.1×P_k×s_k) → fHerd = pHerd×s_herd×P/(0.1×pHerd×s_herd) = P/0.1 = 10P；fHunt 同 = 10P → **1:1**！劳动不足时产出 = 10×P 恒定（与方式无关——劳动爬坡主导）→ 比例失真！

所以 T30 断言要在**劳动充足**下测：P 要 ≥ 0.1×max(P_k×s_k)。pHerd = 0.3×2×62832×1.1 ≈ 41469（MakeGrid 100km：面积 62832？2 格球：4π×100²/2 = 62832 ✓，R=0.3）。s_herd=2/3 → P_劳动_herd = 0.1×41469×(2/3) ≈ 2765。P=1000 < 2765 → 劳动不足！

修 T30：P 设大（如 10000）→ 劳动充足（10000 > 2765 和 1382）→ fHerd/fHunt = pHerd×s_herd/(pHunt×s_hunt) = 2×(2/3):1×(1/3) = 4:1？？
再算：fHerd = 41469×(2/3)×1 = 27646；fHunt = 20735×(1/3)×1 = 6912 → 比率 4:1！**不是 2:1**——因为份额平方效应：w 比例 2:1 → s 2:1 → F = w×s = w²/Σw 比例 4:1。

嗯——这是设计公式的后果：F_k = w_k×s_k = w_k²/Σw——高权重方式产出超比例（4:1）。这合理吗？"土地 2/3 给牧 + 牧单产 2×" → 牧产出 = 2/3地×2倍率 = 4/3 单位 vs 猎 1/3×1 = 1/3 → 4:1 ✓ 数学正确（土地×单产）。

T30 断言：fHerd/fHunt = 4:1（劳动充足）。改断言 `Mathf.Abs(e.FHerdLast - 4f * e.FHuntLast) < e.FHuntLast * 0.1f`。P=10000。

- [ ] **Step 5: 构建 + 测试 + 回归**

`dotnet build` → 0 错误。跑 `--only=T30,S1,S2,S4` → T30 PASS；S1（纯猎部落 F=20735 应不变——单方式退化）、S2（转农开关不变）、S4 回归。
⚠️ S1 验证：纯猎部落 sw=pHunt → s=1 → fHunt = pHunt×1×min(1,P/(0.1×pHunt))——**与现 FHunt 一致**（含劳动）✓ F=20735。
⚠️ S2：场景 A 转农后 FOf 并行（农+猎）——S2 断言 IsFarming bool ✓ 不变。

- [ ] **Step 6: Commit**

```bash
git add scripts/CivSim/CivSimContext.cs scripts/CivSim/CivEngine.cs scripts/CivSim/CivEntity.cs scripts/Diagnostics/CivSimDiag.cs
git commit -m "生产方式并行重构：FOf = Σ 各方式（收益权重 s_k=w_k/Σw + 份额劳动爬坡），单方式退化兼容;F 分量缓存+货物累积(副产率皮革0.10/羊毛0.15/秸秆0.05);T30 权重分配(牧2×猎→4:1 产出)通过,S1/S2/S4 回归

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: T28 畜牧涌现 + 全量回归标定

**Files:**
- Modify: `scripts/Diagnostics/CivSimDiag.cs`（T28 + 注册）

- [ ] **Step 1: T28 测试**

```csharp
    /// <summary>T28 畜牧涌现：草原格(WildLivestock=1)+livestock 科技 → 牧产出>0；无草原格/无科技 → 牧=0。</summary>
    private void T28_LivestockEmergence()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        g.WildLivestock = new byte[] { 1, 0 };   // 格0 草原可牧；格1 无
        var herd = AddEntity(ctx, 0, 10000f, TechTable.Livestock, TechTable.StoneCore);
        var noTech = AddEntity(ctx, 1, 10000f, TechTable.StoneCore);           // 无科技（格1 也无生态位）
        CivEngine.RefreshCellState(ctx);
        bool herdActive = ctx.FOf(herd) > 0f && herd.FHerdLast > 0f;
        bool noHerd = noTech.FHerdLast == 0f;
        Check("T28 畜牧涌现", herdActive && noHerd,
            $"草原+科技 牧F={herd.FHerdLast:F0}(>0) 无生态位/无科技 牧F={noTech.FHerdLast:F0}(=0)");
    }
```
`RunScenarios()` 加 `if (Want("T28")) T28_LivestockEmergence();`

- [ ] **Step 2: 构建 + 测试 + 全量回归**

`dotnet build` → 0 错误。跑 `--only=T28,T29?`（T29 货物累积并入 T30 覆盖？——T29 单独：断言 Goods 累积）：
```csharp
    /// <summary>T29 货物累积：产出后 Goods 按副产率增加（皮革/羊毛/秸秆）。</summary>
    private void T29_GoodsAccumulation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        g.WildLivestock = new byte[] { 1, 0 };
        var e = AddEntity(ctx, 0, 10000f, TechTable.Livestock, TechTable.StoneCore, TechTable.SeedWheat, TechTable.Grinding);
        e.IsFarming = true;   // 三方式并行
        ctx.Suit[0, 0] = 1.0f;
        CivEngine.RefreshCellState(ctx);
        ctx.FOf(e);   // FLast 已算（RefreshCellState 内）
        bool leather = e.Goods[CivSimContext.GoodsLeather] > 0f;
        bool wool = e.Goods[CivSimContext.GoodsWool] > 0f;
        bool straw = e.Goods[CivSimContext.GoodsStraw] > 0f;
        Check("T29 货物累积", leather && wool && straw,
            $"皮革={e.Goods[CivSimContext.GoodsLeather]:F0} 羊毛={e.Goods[CivSimContext.GoodsWool]:F0} 秸秆={e.Goods[CivSimContext.GoodsStraw]:F0}");
    }
```
`RunScenarios()` 加 `if (Want("T29")) T29_GoodsAccumulation();`

跑 `--only=T28,T29,T30,S1,S2,S3,S4,S5,S6,T23,T24,T25,T26,T27` → 全 PASS。
再跑地图档 `--arch=user://maps/map_seed100_n32.mpa`（全量）→ 记录所有 PASS/FAIL 与演化数据（T16/T21/T22 数值可能漂移——记录；FAIL 则调参：HerdMult/副产率/劳动）。

- [ ] **Step 3: Commit**

```bash
git add scripts/Diagnostics/CivSimDiag.cs
git commit -m "T28 畜牧涌现(草原+科技→牧产出>0)/T29 货物累积(三方式副产品);全量回归标定

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: 文档同步 + 最终回归

**Files:**
- Modify: `docs/石器时代设计.md`

- [ ] **Step 1: 文档同步**

§4.2 狩猎公式后加 §4.2a：

```markdown
### 4.2a 生产方式并行（PM 表，2026-08-09 用户拍板：混合经济，Vic3/EU5 参考）

```
部落方式集 M = {hunt} ∪ {herd if livestock能力+WildLivestock位} ∪ {farm if IsFarming}
决策 = 收益权重分配：w_k = 方式潜在全地产出（R_k×A×m_k）；土地份额 s_k = w_k/Σw
实际 F_k = w_k×s_k×min(1, P/(LaborFrac×w_k×s_k))；总产出 F_i = ΣF_k
R_牧 = R × HerdMult(2.0★)；单方式时退化为原公式（纯猎/纯农兼容）
货物副产品：皮革=F_猎×0.10、羊毛=F_牧×0.15、秸秆=F_农×0.05（累积入档 .cmp v7）
```

- [ ] **Step 2: 最终全量回归 + Commit**

跑 `--arch=user://maps/map_seed100_n32.mpa`（无 --only）→ 全部记录。提交：
```bash
git add docs/石器时代设计.md
git commit -m "文档：§4.2a 生产方式并行(PM 表/收益权重/畜牧/货物副产率);.cmp v7

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec 覆盖：** §二 PM 表（Task 2）✓；§三 收益权重（Task 2）✓；§四 畜牧科技+生态位（Task 1）✓；§五 货物系统（Task 1 字段/v7 + Task 2 累积）✓；§六 测试 T28/T29/T30 + 回归（Task 2/3）✓；§七 文件（全部覆盖）✓；§八 范围边界（消费/畜种/劳动分配不做 ✓）。

**占位符扫描：** 无 TBD；T30 断言在 Step 4 内修正（劳动充足 P=10000、产出比 4:1——土地×单产平方效应已在步骤内说明）。✓

**类型一致性：** `Goods[3]`/`GoodsLeather/Wool/Straw` 常量在 Task 1 定义、Task 2 使用一致；`FHuntLast/FHerdLast/FFarmLast` Task 2 定义使用一致；`HerdMult`/`LeatherRate/WoolRate/StrawRate` Task 1 定义 Task 2 使用；`EnsureWildLivestock`/`ComputeLivestock` Task 1 定义 Task 2 能力条件使用。✓

**已知风险：** WildLivestock 的 Precip 单位需按 GameGrid 实际语义确认（Step 5 已标注）；生产方式并行改变产出结构 → T16/T21/T22 数值漂移需标定（Task 3 记录）；.cmp v7 又一次旧档拒绝（既定策略）。
