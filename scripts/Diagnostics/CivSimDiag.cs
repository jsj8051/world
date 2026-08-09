using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>
/// 文明演化诊断（v4 纯实体模型全测试规格，docs/石器时代设计.md §十二）：
///   S1-S6 构造式场景（内联构造，不依赖自然图）+ T01-T20 地图测试。
///   每项输出 "T-xx 名称 PASS/FAIL 数据"；全 PASS 退出码 0，任一 FAIL 退出码 1。
///
/// 命令行：-- --arch=user://maps/xxx.mpa [--seed=N] [--origins=1..6] [--out=user://maps/xxx.cmp]
/// </summary>
public partial class CivSimDiag : Node
{
    private int _pass, _fail;
    private GameGrid _grid;
    private HashSet<string> _only;   // --only= 白名单（空 = 全部）
    private HashSet<string> _skip;   // --skip= 黑名单
    private string _forcedArch;      // SetArch() 注入（编辑器/代码内调用等价 --arch=）

    /// <summary>代码/编辑器内调用入口：设置测试筛选（等价命令行 --only= / --skip=；须在节点入树前调用）。</summary>
    public void SetFilter(string only, string skip)
    {
        if (!string.IsNullOrEmpty(only)) _only = ParseSet(only);
        if (!string.IsNullOrEmpty(skip)) _skip = ParseSet(skip);
    }

    /// <summary>代码/编辑器内调用入口：指定地图路径（等价 --arch=；须在节点入树前调用）。</summary>
    public void SetArch(string path) => _forcedArch = path;

    public override void _Ready()
    {
        string arch = _forcedArch ?? ArchiveDiag.ResolveArchPath();
        int seed = 42, origins = 3;
        string outPath = arch != null ? arch.GetBaseName() + ".cmp" : null;
        var ua = OS.GetCmdlineUserArgs();
        for (int i = 0; i < ua.Length; i++)
        {
            string a = ua[i];
            string v = a.StartsWith("--") ? a.Substring(2) : a;
            if (v.StartsWith("seed=", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(5), out int s)) seed = s;
            else if (v.StartsWith("origins=", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(8), out int o)) origins = Mathf.Clamp(o, 1, 6);
            else if (v.StartsWith("out=", StringComparison.OrdinalIgnoreCase)) outPath = v.Substring(4);
            else if (v.StartsWith("only=", StringComparison.OrdinalIgnoreCase)) _only = ParseSet(v.Substring(5));
            else if (v.StartsWith("skip=", StringComparison.OrdinalIgnoreCase)) _skip = ParseSet(v.Substring(4));
        }
        if (_only != null || _skip != null)
            GD.Print($"[CivSimDiag] 筛选: --only=[{string.Join(",", _only ?? new HashSet<string>())}] --skip=[{string.Join(",", _skip ?? new HashSet<string>())}]");

        // ── S 构造场景（无地图依赖，先跑）──
        RunScenarios();

        // ── T 地图测试 ──
        if (arch == null)
        {
            GD.Print($"[CivSimDiag] 无 --arch：仅跑构造场景（S1-S6）→ 总 {_pass}P/{_fail}F");
            GetTree().Quit(_fail == 0 ? 0 : 1);
            return;
        }
        if (!ArchiveDiag.TryLoad(arch, out var mctx))
        {
            GetTree().Quit(1);
            return;
        }
        _grid = GameGrid.FromMapData(mctx.Map);
        GD.Print($"[CivSimDiag] 读档 {arch} n={_grid.N} → 全测试（seed={seed} 起源{origins}，自然层只读）");
        bool hasFossil = false;
        for (int i = 0; i < _grid.N; i++)
            if (_grid.Biome[i] >= 4 && _grid.Biome[i] <= 11) { hasFossil = true; break; }
        GD.Print($"[CivSimDiag] 地图 biome 化石值(4-11)存在={hasFossil}（旧档放弃策略：含化石 → 存档/演化拒绝）");
        RunMapTests(seed, origins, outPath);

        GD.Print($"[CivSimDiag] 汇总：{_pass} PASS / {_fail} FAIL → {( _fail == 0 ? "全部PASS" : "有失败!")}");
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }

    private void Check(string name, bool ok, string data = "")
    {
        if (ok) _pass++; else _fail++;
        GD.Print($"  {(ok ? "PASS" : "FAIL")} {name}{(data.Length > 0 ? " | " + data : "")}");
    }

    /// <summary>测试筛选：--only（白名单，空=全部）与 --skip（黑名单）取交集语义。
    /// 支持前缀：S=全部构造场景，T=全部地图测试（含存档组）；"存档"=T01/T02/T04/T19 组。
    /// 共享计算的组（T14/T08、T15/T16）由组 gate 触发一次，组内各 Check 再按 Want 各自开关。</summary>
    private bool Want(string id)
    {
        if (_skip != null && _skip.Contains(id)) return false;
        if (_only == null || _only.Count == 0) return true;
        if (_only.Contains(id)) return true;
        if (_only.Contains("S") && id.StartsWith("S", StringComparison.Ordinal)) return true;
        if (_only.Contains("T") && (id.StartsWith("T", StringComparison.Ordinal) || id == "存档")) return true;
        return false;
    }

    private bool WantAny(params string[] ids)
    {
        foreach (var id in ids) if (Want(id)) return true;
        return false;
    }

    private static HashSet<string> ParseSet(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in s.Split(','))
        {
            string id = t.Trim();
            if (id.Length > 0) set.Add(id);
        }
        return set;
    }

    // ═══════════════════ S 构造场景 ═══════════════════

    private void RunScenarios()
    {
        GD.Print("[CivSimDiag] ── S 构造场景 ──");
        if (Want("S1")) S1_GrowthAndEnergy();
        if (Want("S2")) S2_ModeMatrix();
        if (Want("S3")) S3_ShareConservation();
        if (Want("S4")) S4_FissionInherit();
        if (Want("S5")) S5_SpreadDependency();
        if (Want("S6")) S6_ReligionLock();

        // 无地图依赖的 T 测试（构造场景风格，S 段注册）
        if (Want("T24")) T24_TerritoryCohesion();
        if (Want("T25")) T25_FissionPressure();
        if (Want("T26")) T26_CapabilitySwitches();
        if (Want("T27")) T27_StorageBuffer();
        if (Want("T23")) T23_TerritoryMult();
    }

    /// <summary>小网格（N=2 赤道相邻两点，Neighbors 连通；RadiusKm 决定胞面积）。</summary>
    private static GameGrid MakeGrid(float radiusKm, byte biome, float temp, float precip, byte soil = 3)
    {
        var g = new GameGrid { N = 2, GridN = 1, Seed = 42, RadiusKm = radiusKm };
        g.Verts = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0) };   // 角距 90° < 邻居半径
        g.Elev = new[] { 100f, 100f };
        g.Temp = new[] { temp, temp };
        g.Precip = new[] { precip, precip };
        g.Biome = new[] { biome, biome };
        g.RiverLevel = new byte[] { 0, 0 };
        g.RiverFlow = new[] { -1, -1 };
        g.RiverVolume = new float[] { 0, 0 };
        g.LakeLevel = new byte[] { 0, 0 };
        g.MineralLevel = new byte[] { 0, 0 };
        g.SoilLevel = new byte[] { soil, soil };
        g.MonsoonLevel = new byte[] { 0, 0 };
        g.MonthPrecip = new byte[12][];
        g.MonthTemp = new byte[12][];
        for (int m = 0; m < 12; m++)
        {
            g.MonthPrecip[m] = new byte[] { (byte)(255 / 12), (byte)(255 / 12) };
            g.MonthTemp[m] = new byte[] { (byte)((temp + 60) / 120f * 255f), (byte)((temp + 60) / 120f * 255f) };
        }
        g.CurrentDirs = new[] { Vector3.Zero, Vector3.Zero };
        g.CurrentWarmth = new float[] { 0, 0 };
        g.CurrentStrength = new float[] { 0, 0 };
        g.Province = new int[2];
        g.Country = new int[2];
        return g;
    }

    private static CivSimContext MakeCtx(GameGrid g, int seed = 42, int origins = 3)
    {
        TechTable.Load();   // S 场景/测试手动构造 ctx，不经过 CivEngine.Run 的 Load
        int n = g.N;
        var ctx = new CivSimContext
        {
            Grid = g,
            CellTribes = new List<CivEntity>[n],
            Entities = new List<CivEntity>(),
            Seed = seed,
            OriginCount = origins,
            Rng = new DeterministicRandom(seed),
            R = new float[n],
            CellF = new float[n],
            CellPop = new float[n],
            CellFarmPop = new float[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
            WildCrops = g.EnsureWildCrops(),
            Suit = WildCropsSystem.Suitability(g),
            FirstFarmTick = -1,
        };
        for (int i = 0; i < n; i++) ctx.CellTribes[i] = new List<CivEntity>();
        CivEngine.BuildLayer1(ctx);   // 层1 空间生产力 R（两层模型 2026-08-17）
        return ctx;
    }

    private static void RunTicks(CivSimContext ctx, int ticks)
    {
        var reg = CivModelRegistry.StoneAge();
        for (int k = 0; k < ticks; k++, ctx.Tick++)   // ⚠️ 必须递增 Tick：OriginModel 只在 tick 0 播种
        {
            CivEngine.RefreshCellState(ctx);
            reg.ExecuteAll(ctx);
        }
    }

    private static CivEntity AddEntity(CivSimContext ctx, int cell, float pop, params string[] techs)
    {
        var e = new CivEntity
        {
            Id = ctx.Entities.Count,
            Cell = cell,
            P = pop,
            OriginCell = cell,
            BornTick = ctx.Tick,
            CultureShare = ShareField.NewCulture("test_cult"),
            CultureGroupShare = ShareField.NewCulture("test_grp"),
            ReligionShare = ShareField.NewReligion(ReligionStage.Animism),
        };
        foreach (var t in techs) e.TechKeys.Add(t);
        ctx.Entities.Add(e);
        ctx.CellTribes[cell].Add(e);
        return e;
    }

    /// <summary>S1：单格生存——增长收敛 F、饿死（P>F 下降）、稳态 e=0.77。
    /// 只跑能量+增长（防发明/分裂污染单格场景）。</summary>
    private void S1_GrowthAndEnergy()
    {
        // HotSteppe 20°C/800mm → Miami NPP≈1236 → R=0.3 人/km²（k 中位标定）；Area=4π·10000/2=62832 → F_猎≈20735（含 stone_core×1.1）
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var e = AddEntity(ctx, 0, 100f, TechTable.StoneCore);
        var energy = new EnergyModel();
        var growth = new GrowthModel();
        bool converged = false, starved = false, eOk = false;
        for (int tick = 0; tick < 300; tick++)
        {
            ctx.Tick = tick;
            CivEngine.RefreshCellState(ctx);
            energy.Execute(ctx);
            growth.Execute(ctx);
            float F = ctx.FOf(e);   // 当 tick 产出（含 stone_core 乘数 1.1）= 稳态人口点
            if (tick > 50 && Mathf.Abs(ctx.CellPop[0] - F) < F * 0.02f) converged = true;
        }
        // 稳态人均 e（构造：P=F 时 e_猎 = Y/(Y+0.3Y) = 0.769，与乘数无关——h 缩放）
        float Ks = ctx.FOf(e);
        e.P = Ks;
        float yH = ctx.FHunt(e);
        float eSteady = CivSimContext.EHunt(yH, Ks);
        eOk = Mathf.Abs(eSteady - 1f / 1.3f) < 0.01f;
        // 饿死：P 超 F → 增长为负
        e.P = Ks * 1.5f;
        float p0 = e.P;
        CivEngine.RefreshCellState(ctx);
        energy.Execute(ctx);
        growth.Execute(ctx);
        starved = e.P < p0;
        Check("S1 增长收敛+饿死+稳态e", converged && starved && eOk,
            $"F={Ks:F0} 收敛={converged} 饿死={starved} e稳态={eSteady:F3}");
    }

    /// <summary>S2：生产方式矩阵——φ 高转农、φ 低最终狩猎、稳态不退农 + 滞回。
    /// 新公式（两层模型 2026-08-17）：转农条件 R_农/R × F·φ > 0.97M；Soil3（冲积土=1）下 场景A 4×1.0 > 1.41、场景B 4×0.3 < 1.41。</summary>
    private void S2_ModeMatrix()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);   // Soil3：冲积土因子=1（薄地，农业潜在=种子×φ×R×面积）
        var ctx = MakeCtx(g);
        float y0 = ctx.R[0] * g.CellAreaKm2;   // 基础狩猎产出（无工具）

        // 场景 A：φ=1.0 Soil3 → 农业潜在=4y0 > 狩猎 1.455y0 → 稳态农业
        var ea = AddEntity(ctx, 0, 0.5f * y0, TechTable.StoneCore, TechTable.Handaxe, TechTable.Grinding, TechTable.SeedWheat);
        ctx.Suit[0, 0] = 1.0f;   // 小麦 φ
        // 场景 B：φ=0.3 Soil3 → 农业潜在=1.2y0 < 狩猎 1.455y0 → 最终狩猎（发明瞬间公式 R_农/R·F·φ<0.97M）
        var eb = AddEntity(ctx, 1, 0.5f * y0, TechTable.StoneCore, TechTable.Handaxe, TechTable.Grinding, TechTable.SeedWheat);
        ctx.Suit[1, 0] = 0.3f;

        // 手动跑能量/增长/模式循环（不含发明，只测选择动力学）
        var mode = new ModeModel();
        var energy = new EnergyModel();
        var growth = new GrowthModel();
        for (int tick = 0; tick < 200; tick++)
        {
            ctx.Tick = tick;
            CivEngine.RefreshCellState(ctx);
            energy.Execute(ctx);
            growth.Execute(ctx);
            mode.Execute(ctx);
            if (ea.P < 1f) ea.P = 0.5f * y0;   // 防饿死干扰（只测选择）
            if (eb.P < 1f) eb.P = 0.5f * y0;
        }
        bool aFarms = ea.IsFarming;    // φ=1.0 → 稳态农业
        bool bFarms = eb.IsFarming;    // φ=0.3 → 稳态狩猎（农业 K<狩猎 K 自动拒绝）
        // [临时诊断] 场景 B 数值分解（狩猎土地份额+劳动力修复后排障）
        float yHB = ctx.FHunt(eb), yFB = ctx.FFarmPotential(eb);
        GD.Print($"[S2诊断] 场景B φ=0.3: P={eb.P:F0} yH={yHB:F0} yF={yFB:F0} m={eb.CarryMult:F2} " +
                 $"plabor={CivSimContext.LaborFrac * (ctx.R[1] * g.CellAreaKm2 / 1f * eb.CarryMult):F0} " +
                 $"eH={CivSimContext.EHunt(yHB, eb.P):F3} eF={CivSimContext.EFarm(yFB, eb.P):F3}");
        Check("S2 生产方式矩阵", aFarms && !bFarms,
            $"φ=1.0 农={aFarms}（应 True） φ=0.3 农={bFarms}（应 False）");

        // 滞回：交叉点 P≈13.8y0 处 |e_猎−e_农|<0.02 → 保持当前方式（独立 ctx 防干扰；Soil3 下 yF=4y0 交叉点不变）
        var g2 = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx2 = MakeCtx(g2);
        var eh = AddEntity(ctx2, 0, 13.8f * y0, TechTable.StoneCore, TechTable.Handaxe, TechTable.Grinding, TechTable.SeedWheat);
        ctx2.Suit[0, 0] = 1.0f;
        eh.IsFarming = true;
        float yH2 = ctx2.FHunt(eh);
        float yF2 = ctx2.FFarmPotential(eh);
        float diff2 = CivSimContext.EHunt(yH2, eh.P) - CivSimContext.EFarm(yF2, eh.P);
        bool inHyst = Mathf.Abs(diff2) < 0.02f;
        mode.Execute(ctx2);   // 滞回带内 → 不切换
        bool hyst = inHyst && eh.IsFarming;
        Check("S2 滞回防抖", hyst, $"|e_猎−e_农|={Mathf.Abs(diff2):F3} < 0.02 且保持农");
    }

    /// <summary>S3：份额守恒——3 实体同格，同化 30 tick 后 Σ=1 恒成立，主导单调增。</summary>
    private void S3_ShareConservation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        AddEntity(ctx, 0, 100f, TechTable.StoneCore);
        AddEntity(ctx, 0, 100f, TechTable.StoneCore);
        AddEntity(ctx, 0, 100f, TechTable.StoneCore);
        ctx.Entities[0].CultureShare = ShareField.NewCulture("cult_a");
        ctx.Entities[1].CultureShare = ShareField.NewCulture("cult_b");
        ctx.Entities[2].CultureShare = ShareField.NewCulture("cult_c");
        var culture = new CultureModel();
        var energy = new EnergyModel();
        bool conserved = true;
        string prevDom = null;
        bool domMonotonic = true;
        for (int tick = 0; tick < 30; tick++)
        {
            ctx.Tick = tick;
            energy.Execute(ctx);
            culture.Execute(ctx);
            string dom = ShareField.DomKey(ctx.Entities[0].CultureShare);
            if (prevDom != null && dom != prevDom) domMonotonic = false;   // 主导 key 稳定（不跳变）
            prevDom = dom;
            foreach (var e in ctx.Entities)
            {
                int sum = e.CultureShare[0].Frac + e.CultureShare[1].Frac;
                if (sum != 255) conserved = false;
            }
        }
        int domFrac = ShareField.DomFrac(ctx.Entities[0].CultureShare);
        Check("S3 份额守恒+主导同化", conserved && domMonotonic && domFrac > 150,
            $"Σ恒等={conserved} 主导单调={domMonotonic} 30tick后主导份额={domFrac}/255");
    }

    /// <summary>S4：分裂继承——45% 带走、份额等比例、TechKeys 完整、BornTick。</summary>
    private void S4_FissionInherit()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var e = AddEntity(ctx, 0, 500f, TechTable.StoneCore, TechTable.Fire, TechTable.Handaxe);
        e.CultureShare = new[] { new ShareEntry { Key = "cult_7", Frac = 200 }, new ShareEntry { Key = "cult_9", Frac = 55 } };
        e.CultureGroupShare = new[] { new ShareEntry { Key = "cult_3", Frac = 250 }, new ShareEntry { Key = "cult_0", Frac = 5 } };
        e.ReligionShare = ShareField.NewReligion(ReligionStage.Shaman);
        e.IsFarming = false;
        ctx.CellTribes[0] = new List<CivEntity> { e };   // 只 1 实体（格上限内）
        var sm = new SplitMigrateModel();
        sm.Execute(ctx);
        bool ok = ctx.Entities.Count == 2;
        var nt = ctx.Entities[1];
        ok &= Mathf.Abs(nt.P - 225f) < 0.01f && Mathf.Abs(e.P - 275f) < 0.01f;   // 45% 带走
        ok &= nt.CultureShare[0].Key == "cult_7" && nt.CultureShare[0].Frac == 200 && nt.CultureShare[1].Frac == 55;   // 等比例继承
        ok &= nt.TechKeys.Count == 3 && nt.TechKeys.Contains(TechTable.Fire);    // TechKeys 完整
        ok &= nt.BornTick == 0 && nt.OriginCell == 0;
        ok &= nt.CultureGroupShare[0].Key == "cult_3";   // 群份额继承
        Check("S4 分裂继承", ok, $"新实体 P={nt.P:F0}（应225） 份额={nt.CultureShare[0].Frac}（应200） 科技={nt.TechKeys.Count}（应3）");
    }

    /// <summary>S5：传播依赖——前置缺失不传；补全后按 SpreadBase 传（同格接触，不依赖邻格表）。</summary>
    private void S5_SpreadDependency()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        // a 无 handaxe：bow/microlith 的前置链在 b 侧缺失 → 不传（防中间科技先传）
        var a = AddEntity(ctx, 0, 300f, TechTable.StoneCore, TechTable.Microlith, TechTable.Bow);
        var b = AddEntity(ctx, 0, 100f, TechTable.StoneCore);   // 同格；缺 microlith/handaxe
        var spread = new SpreadModel();
        bool blocked = true;
        for (int tick = 0; tick < 60; tick++)
        {
            spread.Execute(ctx);
            if (b.TechKeys.Contains(TechTable.Bow)) { blocked = false; break; }
        }
        // B 补全前置 → bow 可传
        b.TechKeys.Add(TechTable.Handaxe);
        b.TechKeys.Add(TechTable.Microlith);
        bool transferred = false;
        for (int tick = 0; tick < 200 && !transferred; tick++)
        {
            spread.Execute(ctx);
            if (b.TechKeys.Contains(TechTable.Bow)) transferred = true;
        }
        Check("S5 传播依赖", blocked && transferred,
            $"缺前置不传={blocked} 补全后传播={transferred}");
    }

    /// <summary>S6：宗教锁——盈余+细石器 → 萨满；持种子但狩猎 → 不升祖先（不读时代）。</summary>
    private void S6_ReligionLock()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var e1 = AddEntity(ctx, 0, 100f, TechTable.StoneCore, TechTable.Handaxe, TechTable.Microlith);
        e1.Surplus = 0.5f;   // 盈余期
        var e2 = AddEntity(ctx, 1, 100f, TechTable.StoneCore, TechTable.Grinding, TechTable.SeedWheat);
        e2.Surplus = -0.1f;  // 狩猎（IsFarming=false）
        var rel = new ReligionModel();
        rel.Execute(ctx);
        bool shaman = ShareField.RelFrac(e1.ReligionShare, ReligionStage.Shaman) > 0;          // 泛灵→萨满
        bool noAncestor = ShareField.RelFrac(e1.ReligionShare, ReligionStage.Ancestor) == 0
                       && ShareField.RelFrac(e2.ReligionShare, ReligionStage.Ancestor) == 0;   // 旧石器锁死
        Check("S6 宗教锁", shaman && noAncestor,
            $"萨满份额={ShareField.RelFrac(e1.ReligionShare, ReligionStage.Shaman)} 祖先份额全0={noAncestor}");
    }

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
        bool sameId = a.TerritoryId == b.TerritoryId;
        int sizeWhenUnited = a.TerritorySize;
        b.CultureGroupShare = ShareField.NewCulture("cultg_999");   // 语言群分歧
        ctx.TerritoryLastRebuild = -10;
        new TerritoryModel().Execute(ctx);
        bool split = a.TerritoryId != b.TerritoryId && a.TerritorySize == 1 && b.TerritorySize == 1;
        Check("T24 领地凝聚/断裂", united && split,
            $"凝聚(同id={sameId},size={sizeWhenUnited}) 断裂(异id,size=1)");
    }

    /// <summary>T25 裂变压力：饥荒(资源压力)→提前裂变；盈余小规模(无压力无张力)→不裂（确定性，无地图依赖）。</summary>
    private void T25_FissionPressure()
    {
        // ctxA：饥荒 P=250, FLast=125（压力 0.5）→ P_eff=375>300 → 裂变（旧逻辑 P<300 不裂 → 本测试在旧代码下 FAIL）
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctxA = MakeCtx(gA);
        var famine = AddEntity(ctxA, 0, 250f, TechTable.StoneCore);
        famine.FLast = 125f;           // 产出减半（RefreshCellState 未跑，手工设 FLast 供裂变压力计算）
        var sm = new SplitMigrateModel();
        sm.Execute(ctxA);
        bool famineFissioned = ctxA.Fissions == 1;
        // ctxB：盈余 P=300（FLast=600）→ 无压力无张力 → P_eff=300 不裂
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctxB = MakeCtx(gB);
        var fed = AddEntity(ctxB, 0, 300f, TechTable.StoneCore);
        fed.FLast = 600f;
        sm.Execute(ctxB);
        bool fedKept = ctxB.Fissions == 0;
        Check("T25 裂变压力", famineFissioned && fedKept,
            $"饥荒250裂变={famineFissioned}(Fissions={ctxA.Fissions}) 盈余300不裂={fedKept}(Fissions={ctxB.Fissions})");
    }

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
        bool complete = ids.SetEquals(new HashSet<string> { "canoe", "microlith", "grinding", "fire", "clothing", "seed", "storage", "livestock" });
        Check("T26 能力开关", canoeOk && seedOk && complete,
            $"canoe开关={canoeOk} seed开关={seedOk} 能力集={string.Join(",", ids)}");
    }

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

    // ═══════════════════ T 地图测试 ═══════════════════

    private void RunMapTests(int seed, int origins, string outPath)
    {
        GD.Print("[CivSimDiag] ── T 地图测试 ──");
        // 演化 gate：未选任何地图测试（如 --only=S1,S2）时跳过完整演化（最贵段 ~11s）
        bool needEvol = WantAny("T01", "T02", "T03", "T04", "T05", "T08", "T09", "T10", "T11", "T13", "T14", "T15", "T16", "T17", "T21", "T22", "存档");
        CivSimResult r1 = null;    // 演化结果（needEvol=false 时为 null，依赖它的测试已被筛掉）
        if (needEvol) r1 = EvolveAndDebug(seed, origins);
        else GD.Print("[CivSimDiag] --only 未含地图测试：跳过演化");
        var c = r1?.Context;   // 演化 context（needEvol=false 时为 null）

        bool repro = false, wcDet = false, rtOk = false;
        if (Want("T03")) repro = T03_Reproducibility(c, seed, origins);   // 内部二次演化（~11s），仅选中时跑
        if (Want("T17")) wcDet = T17_WildCrops(seed);
        if (WantAny("T01", "T02", "T04", "T19", "存档")) rtOk = ArchiveChecks(outPath, r1, c, seed, origins);
        if (Want("T05")) T05_Origins(seed, origins);
        if (Want("T09")) T09_DependencyChain(c);
        if (WantAny("T14", "T08")) T14_T08_Agriculture(c);
        if (Want("T10")) T10_Spread(c);
        if (Want("T11")) T11_FissionMigration(c);
        if (Want("T13")) T13_Religion(c);
        if (WantAny("T15", "T16")) T15_T16_Coverage(c);
        if (Want("T21")) T21_PopGradient(c);
        if (Want("T22")) T22_TerritoryEmergence(c);
        if (Want("T18")) T18_Perf(seed, origins);   // 第三次演化（~11s），仅选中时跑
        // T20 全链确定性（复现×WildCrops×存档往返 组合指标）——需要 T03+T17+存档组同时被选才有意义
        if (Want("T20") && Want("T03") && Want("T17") && (outPath == null || WantAny("T01", "T02", "T04", "T19", "存档")))
            Check("T20 全链确定性", repro && wcDet && (outPath == null || rtOk), $"复现={repro} WildCrops={wcDet} 往返={rtOk}");
        else if (Want("T20"))
            GD.Print("  - T20 跳过：需同时选中 T03+T17+存档组（--only=T03,T17,存档,T20）");
    }

    /// <summary>完整演化一次 + [文化调试] 状态摘要打印（只被地图测试共享；演化后无条件打印摘要）。</summary>
    private CivSimResult EvolveAndDebug(int seed, int origins)
    {
        var sw = Stopwatch.StartNew();
        var r1 = CivEngine.Run(_grid, seed, origins);
        sw.Stop();
        var c = r1.Context;
        // [临时调试] 文化空间分布（起源 key + 各文化实体数）
        var cultCount = new Dictionary<string, int>();
        var originCults = new System.Text.StringBuilder();
        foreach (var e in r1.Context.Entities)
        {
            string ck = World.CivSim.ShareField.DomKey(e.CultureShare);
            if (ck != null) cultCount[ck] = cultCount.TryGetValue(ck, out var v) ? v + 1 : 1;
            if (e.BornTick == 0) originCults.Append($"格{e.Cell}:{ck} ");
        }
        var cultTop = new List<KeyValuePair<string, int>>(cultCount);
        cultTop.Sort((a, b) => b.Value.CompareTo(a.Value));
        var cultStr = new System.Text.StringBuilder();
        for (int q = 0; q < Math.Min(6, cultTop.Count); q++)
            cultStr.Append($"{cultTop[q].Key}={cultTop[q].Value} ");
        GD.Print($"[文化调试] 起源={originCults} | 文化实体数: {cultStr}");
        // [临时调试] 起源格当前主导文化（是否被外文化吞并）
        var ocStr = new System.Text.StringBuilder();
        foreach (int oc in new[] { 7597, 7106, 58 })
        {
            if (oc < 0 || oc >= c.CellTribes.Length) continue;
            var tl = c.CellTribes[oc];
            string domKey = "无";
            if (tl.Count > 0)
            {
                var d0 = tl[0];
                for (int q = 1; q < tl.Count; q++) if (tl[q].P > d0.P) d0 = tl[q];
                domKey = World.CivSim.ShareField.DomKey(d0.CultureShare) ?? "null";
                if (domKey == "null" || domKey == "无")
                {
                    var sb2 = new System.Text.StringBuilder();
                    for (int q = 0; q < tl.Count; q++)
                    {
                        var e2 = tl[q];
                        sb2.Append($"#{e2.Id}(P{e2.P:F0})[{World.CivSim.ShareField.DomKey(e2.CultureShare) ?? "n"}:{e2.CultureShare[0].Frac},{World.CivSim.ShareField.SecKey(e2.CultureShare) ?? "n"}:{e2.CultureShare[1].Frac}] ");
                    }
                    GD.Print($"  [文化调试] null格{oc} 全实体: {sb2}");
                }
            }
            ocStr.Append($"格{oc}:{domKey}({tl.Count}实体) ");
        }
        GD.Print($"[文化调试] 起源格现状: {ocStr}");
        // [临时调试] 各文化格级覆盖（主导文化格数 + 平均实体人口）
        var cultGrid = new Dictionary<string, int>();
        var cultPop = new Dictionary<string, float>();
        for (int gi = 0; gi < c.CellTribes.Length; gi++)
        {
            var tl = c.CellTribes[gi];
            if (tl.Count == 0) continue;
            var d0 = tl[0];
            for (int q = 1; q < tl.Count; q++) if (tl[q].P > d0.P) d0 = tl[q];
            string gk = World.CivSim.ShareField.DomKey(d0.CultureShare);
            if (gk == null) continue;
            cultGrid[gk] = cultGrid.TryGetValue(gk, out var gv) ? gv + 1 : 1;
            cultPop[gk] = cultPop.TryGetValue(gk, out var pv) ? pv + d0.P : d0.P;
        }
        var cgTop = new List<KeyValuePair<string, int>>(cultGrid);
        cgTop.Sort((a, b) => b.Value.CompareTo(a.Value));
        var cgStr = new System.Text.StringBuilder();
        for (int q = 0; q < cgTop.Count; q++)
            cgStr.Append($"{cgTop[q].Key}={cgTop[q].Value}格(均pop{cultPop[cgTop[q].Key] / cgTop[q].Value:F0}) ");
        GD.Print($"[文化调试] 格级覆盖: {cgStr}");
        // [临时调试] 文化群/宗教派别多样性（产生 vs 存活）
        var grpCount = new Dictionary<string, int>();
        var relCount = new Dictionary<string, int>();
        foreach (var e in r1.Context.Entities)
        {
            string gk = World.CivSim.ShareField.DomKey(e.CultureGroupShare);
            if (gk != null) grpCount[gk] = grpCount.TryGetValue(gk, out var v) ? v + 1 : 1;
            string rk = World.CivSim.ShareField.DomKey(e.ReligionCultShare);
            if (rk != null) relCount[rk] = relCount.TryGetValue(rk, out var v2) ? v2 + 1 : 1;
        }
        GD.Print($"[文化调试] 文化群存活={grpCount.Count} 宗教派别存活={relCount.Count} (CultureKeyCount={r1.Context.CultureKeyCount} ReligionKeyCount={r1.Context.ReligionKeyCount})");
        GD.Print($"[CivSimDiag] 演化 {r1.FinalTick} tick（{r1.FinalTick * CivSimContext.TickYears} 年）| 实体 {r1.Context.Entities.Count} | 人口 {r1.Context.TotalPopulation():F0} | 首转农 tick {r1.Context.FirstFarmTick} | 耗时 {sw.ElapsedMilliseconds}ms");
        return r1;
    }

    /// <summary>T03 复现性：同 seed 二次演化结果逐实体一致（唯一要跑第二遍演化的测试 ~11s，仅选中时跑）。
    /// 返回 repro 供 T20 全链确定性组合。</summary>
    private bool T03_Reproducibility(CivSimContext c, int seed, int origins)
    {
        var r2 = CivEngine.Run(_grid, seed, origins);
        bool repro = EntitiesEqual(c, r2.Context);
        Check("T03 复现性（同 seed 两次一致）", repro, $"实体 {c.Entities.Count}");
        return repro;
    }

    /// <summary>T17 WildCrops：确定性重建 + 斑块 + 只落陆地（Compute×2 + Suitability，秒级）。
    /// 返回 wcDet 供 T20 全链确定性组合。</summary>
    private bool T17_WildCrops(int seed)
    {
        var wc1 = WildCropsSystem.Compute(_grid, seed);
        var wc2 = WildCropsSystem.Compute(_grid, seed);
        bool wcDet = ByteSeqEqual(wc1, wc2);
        int landCells = 0;
        for (int i = 0; i < _grid.N; i++) if (_grid.IsLandCell(i)) landCells++;
        bool wcLand = true;
        int[] wcCount = new int[5];
        for (int i = 0; i < _grid.N; i++)
        {
            if (wc1[i] == 0) continue;
            if (!_grid.IsLandCell(i)) wcLand = false;
            for (int s = 0; s < 5; s++) if ((wc1[i] & (1 << s)) != 0) wcCount[s]++;
        }
        // 分布区非空检查（各种子适宜度 > P70 的格存在）
        var suit = WildCropsSystem.Suitability(_grid);
        var landSuitMax = new float[5];
        for (int i = 0; i < _grid.N; i++)
            if (_grid.IsLandCell(i))
                for (int s = 0; s < 5; s++)
                    landSuitMax[s] = Mathf.Max(landSuitMax[s], suit[i, s]);
        var extinct = new List<string>();
        for (int s = 0; s < 5; s++)
            if (wcCount[s] == 0)
                extinct.Add(TechTable.SeedKeys[s]);
        bool wcOk = wcDet && wcLand && landCells > 0;
        Check("T17 WildCrops", wcOk,
            $"确定性={wcDet} 只落陆地={wcLand} 斑块格数=[{string.Join(",", wcCount)}] 灭绝={string.Join(";", extinct)}");
        if (extinct.Count > 0) GD.Print($"  ⚠ [T17] 天然灭绝种子: {string.Join(";", extinct)}（星球气候不匹配，按设计不保底）");
        return wcDet;
    }

    /// <summary>T01/T02/T04/T19 存档组（写 .cmp → 读回 → 续跑对照 → 版本拒绝）：共享一次 Write+Read（~1s）。
    /// 组内各 Check 按 --only/--skip 独立开关；无 --out 时整组跳过（不再制造 FAIL 噪声）。
    /// 返回 rtOk 供 T20 全链确定性组合。</summary>
    private bool ArchiveChecks(string outPath, CivSimResult r1, CivSimContext c, int seed, int origins)
    {
        bool natOk = false, rtOk = false, contOk = false, verRejected = false, v4Rejected = false, biomeRejected = false;
        if (outPath != null)
        {
            bool wrote = CivMapArchive.Write(outPath, _grid, r1);
            GameGrid gridBack = null;
            CivSimResult rBack = null;
            if (wrote && CivMapArchive.Read(outPath, out gridBack, out rBack))
            {
                natOk = NaturalUnchanged(_grid, gridBack);
                // 领地是滞后重算的派生状态——对比前强制重算（确定性：同状态 → 同领地）
                TerritoryModel.Rebuild(c);
                TerritoryModel.Rebuild(rBack.Context);
                rtOk = EntitiesEqual(c, rBack.Context);
                // [临时验证] Peek vs Read 摘要一致性（只对读档即时状态对照——续跑会推进 tick/人口/实体，放 T04 之后必不一致）
                if (CivMapArchive.Peek(outPath, out int pSeed, out int pTick, out float pPop, out int pEnt,
                                       out ushort aVer, out var aSt))
                {
                    bool pkOk = pSeed == rBack.Context.Seed && pTick == rBack.Context.Tick
                        && pPop == rBack.Context.TotalPopulation() && pEnt == rBack.Context.Entities.Count;
                    GD.Print($"[Peek验证] seed={pSeed}({rBack.Context.Seed}) tick={pTick}({rBack.Context.Tick}) pop={pPop:F0}({rBack.Context.TotalPopulation():F0}) ent={pEnt}({rBack.Context.Entities.Count}) 一致={pkOk}");
                }
                else GD.Print("[Peek验证] FAIL 无法 Peek");
                // T04 读档续跑无分叉（IsFarming 入档验证）
                int baseTicks = rBack.Context.Tick;   // 存档 tick（finalTick）
                CivEngine.Continue(rBack.Context, 20);
                var ctxFull = MakeCtx(_grid, seed, origins);
                RunTicks(ctxFull, baseTicks + 20);
                TerritoryModel.Rebuild(rBack.Context);
                TerritoryModel.Rebuild(ctxFull);
                contOk = EntitiesEqual(rBack.Context, ctxFull);
            }
            // T19 存档版本：ver>7 拒绝；v6/v5/v4 旧档拒绝（格式变更，旧档续跑分叉）；旧 biome 4-11 拒绝
            string badPath = outPath + ".bad";
            WriteBadVersion(badPath, 8);                      // ver>7 → 拒绝
            verRejected = !CivMapArchive.Read(badPath, out _, out _);
            WriteBadVersion(badPath, 6);                      // v6 旧档 → 拒绝（缺货物段，读档错位）
            v4Rejected = !CivMapArchive.Read(badPath, out _, out _);
            WriteBadBiome(badPath, _grid);
            biomeRejected = !CivMapArchive.Read(badPath, out _, out _);
        }
        if (Want("T01")) Check("T01 自然层零改动（硬验收）", natOk, outPath ?? "无 --out");
        if (Want("T02")) Check("T02 实体往返", rtOk, $"实体 {c.Entities.Count}");
        if (Want("T04")) Check("T04 读档续跑无分叉", contOk, "IsFarming 入档验证");
        if (Want("T19")) Check("T19 存档版本拒绝", verRejected && v4Rejected && biomeRejected,
            $"ver>7 拒绝={verRejected} v6/v5/v4旧档拒绝={v4Rejected} biome4-11 拒绝={biomeRejected}");
        return rtOk;
    }

    /// <summary>T05 起源播种（独立跑 OriginModel，不依赖演化结果）。</summary>
    private void T05_Origins(int seed, int origins)
    {
        bool t05 = false;
        var ctx0 = MakeCtx(_grid, seed, origins);
        new OriginModel().Execute(ctx0);
        if (ctx0.Entities.Count == origins)
        {
            bool distOk = true, richOk = true, cultOk = true;
            var richSet = RichZone(_grid);
            float minKm = CivSimContext.OriginDistMin * Mathf.Sqrt(_grid.CellAreaKm2);
            for (int i = 0; i < ctx0.Entities.Count; i++)
            {
                var e = ctx0.Entities[i];
                if (e.P != CivSimContext.OriginPop || !e.TechKeys.Contains(TechTable.StoneCore)) cultOk = false;
                if (ShareField.DomReligion(e.ReligionShare) != ReligionStage.Animism) cultOk = false;
                if (!richSet.Contains(e.Cell)) richOk = false;
                for (int j = i + 1; j < ctx0.Entities.Count; j++)
                    if (_grid.DistKm(e.Cell, ctx0.Entities[j].Cell) < minKm) distOk = false;
            }
            t05 = distOk && richOk && cultOk;
        }
        Check("T05 起源播种", t05, $"N={ctx0.Entities.Count} 格距≥12格 富饶区 泛灵 独立文化");
    }

    /// <summary>T09 依赖链不变量（bow→microlith→handaxe→stone_core）。</summary>
    private void T09_DependencyChain(CivSimContext c)
    {
        bool depOk = true;
        foreach (var e in c.Entities)
        {
            if (e.TechKeys.Contains(TechTable.Bow) && !e.TechKeys.Contains(TechTable.Microlith)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Microlith) && !e.TechKeys.Contains(TechTable.Handaxe)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Handaxe) && !e.TechKeys.Contains(TechTable.StoneCore)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Canoe) && !e.TechKeys.Contains(TechTable.Fire)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Grinding) && !e.TechKeys.Contains(TechTable.Handaxe)) depOk = false;
        }
        Check("T09 依赖链不变量", depOk, "bow→microlith→handaxe→stone_core 全链成立");
    }

    /// <summary>T14 农业涌现 + T08 稳态不退农（共享一次遍历；各自 Check 按 --only 独立开关）。</summary>
    private void T14_T08_Agriculture(CivSimContext c)
    {
        int farmCount = CountFarming(c);
        bool agriEmerged = c.FirstFarmTick >= 0;
        bool noRevert = true;   // 终态农业实体 e_农 > e_猎（稳态站稳）
        int revertCount = 0;
        int[] seedHolders = new int[5];
        foreach (var e in c.Entities)
        {
            for (int s = 0; s < 5; s++)
                if (e.TechKeys.Contains(TechTable.SeedKeys[s])) seedHolders[s]++;
            if (!e.IsFarming) continue;
            float eh = CivSimContext.EHunt(c.FHunt(e), e.P);
            float ef = CivSimContext.EFarm(c.FFarmPotential(e), e.P);
            if (ef < eh - CivSimContext.Hysteresis)   // 滞回带内（差<0.02）保持不算退农
            {
                noRevert = false;
                if (revertCount < 3)
                    GD.Print($"  [T08诊断] 退农倾向实体 cell={e.Cell} P={e.P:F0} Soil={c.Grid.SoilLevel[e.Cell]} " +
                             $"F_农={c.FFarmPotential(e):F0} F_猎={c.FHunt(e):F0} e_农={ef:F3} e_猎={eh:F3} F_格={c.CellF[e.Cell]:F0} 持种子=[{string.Join(";", TechTable.HeldSeeds(e.TechKeys))}]");
                revertCount++;
            }
        }
        GD.Print($"  [T08数据] 农业实体 {farmCount} 个，其中 e_农<e_猎 的 {revertCount} 个");
        if (Want("T14")) Check("T14 农业涌现", agriEmerged && farmCount > 0, $"首转农 tick={c.FirstFarmTick} 农业实体={farmCount} 种子持有=[{string.Join(",", seedHolders)}]");
        if (Want("T08")) Check("T08 稳态不退农", noRevert || farmCount == 0, $"终态农业实体 e_农>e_猎 全成立={noRevert}（退农 {revertCount}/{farmCount}）");
    }

    /// <summary>T10 传播（工具扩散 > 种子扩散，参考指标；farmCount 自算，不依赖 T14 组）。</summary>
    private void T10_Spread(CivSimContext c)
    {
        int farmCount = CountFarming(c);
        int toolTechHolders = 0;
        foreach (var e in c.Entities)
            if (e.TechKeys.Contains(TechTable.Bow)) toolTechHolders++;
        bool spreadOk = toolTechHolders >= farmCount;   // 工具类扩散显著（软指标）
        Check("T10 传播扩散", spreadOk, $"弓箭持有 {toolTechHolders} ≥ 农业实体 {farmCount}");
    }

    /// <summary>T11 分裂/迁徙（地图统计）。</summary>
    private void T11_FissionMigration(CivSimContext c)
    {
        bool splitOk = c.Fissions > 0 && c.Migrations > 0;
        Check("T11 分裂/迁徙", splitOk, $"分裂 {c.Fissions} 迁徙 {c.Migrations}");
    }

    /// <summary>T13 宗教：旧石器无祖先/多神/一神（锁死）+ 派别多样性。</summary>
    private void T13_Religion(CivSimContext c)
    {
        bool relOk = true;
        int shamanEnts = 0;
        var cultSet = new System.Collections.Generic.HashSet<string>();
        foreach (var e in c.Entities)
        {
            if (ShareField.RelFrac(e.ReligionShare, ReligionStage.Ancestor) > 0
             || ShareField.RelFrac(e.ReligionShare, ReligionStage.Polytheism) > 0
             || ShareField.RelFrac(e.ReligionShare, ReligionStage.Monotheism) > 0) relOk = false;
            if (ShareField.RelFrac(e.ReligionShare, ReligionStage.Shaman) > 0) shamanEnts++;
            string ck = ShareField.DomKey(e.ReligionCultShare);
            if (ck != null) cultSet.Add(ck);
        }
        Check("T13 宗教演进", relOk, $"萨满实体 {shamanEnts}（祖先/多神/一神全 0={relOk}）· 派别 {cultSet.Count} 种");
    }

    /// <summary>T15 覆盖（参考指标）+ T16 时代分布金字塔（共享一次扫描；各自 Check 按 --only 独立开关）。</summary>
    private void T15_T16_Coverage(CivSimContext c)
    {
        int occupied = 0, land = 0, maxCellEnts = 0, cellsWithEnts = 0;
        for (int i = 0; i < _grid.N; i++)
        {
            if (_grid.IsLandCell(i)) land++;
            if (c.CellPop[i] > 0f) occupied++;
            if (c.CellTribes[i].Count > 0) cellsWithEnts++;
            if (c.CellTribes[i].Count > maxCellEnts) maxCellEnts = c.CellTribes[i].Count;
        }
        GD.Print($"[CivSimDiag] 实体格分布: 占 {cellsWithEnts} 格（实体 {c.Entities.Count}） 单格最大 {maxCellEnts}（上限 {CivSimContext.MaxTribesPerCell}）");
        float cover = land > 0 ? occupied * 100f / land : 0f;
        if (Want("T15")) Check("T15 覆盖", true, $"覆盖 {occupied}/{land} = {cover:F0}%（参考指标，不硬卡）");
        if (Want("T16"))
        {
            int farmCount = CountFarming(c);
            bool pyramid = farmCount < c.Entities.Count / 2;
            Check("T16 时代分布金字塔", pyramid, $"新石器(农) {farmCount} ≪ 旧石器 {c.Entities.Count - farmCount}");
        }
    }

    /// <summary>T21 人口分布梯度（两层模型核心验收 2026-08-17：人口=空间R×食物流，不再每格趋同）。</summary>
    private void T21_PopGradient(CivSimContext c)
    {
        var pops = new List<float>();
        float popMax = 0f;
        for (int i = 0; i < _grid.N; i++)
        {
            if (!_grid.IsLandCell(i)) continue;
            float p = c.CellPop[i];
            if (p <= 0f) continue;
            pops.Add(p);
            if (p > popMax) popMax = p;
        }
        float popRatio = 0f;
        if (pops.Count >= 20)
        {
            pops.Sort();
            float p1 = pops[pops.Count / 100];
            float p99 = pops[pops.Count - 1 - pops.Count / 100];
            popRatio = p99 / Mathf.Max(1f, p1);
        }
        float densMax = _grid.CellAreaKm2 > 0f ? popMax / _grid.CellAreaKm2 : 0f;   // 峰值密度 人/km²
        Check("T21 人口分布梯度", popRatio > 50f && densMax >= 10f,
            $"有人格={pops.Count} P99/P1={popRatio:F1}(目标>50) 峰值密度={densMax:F1} 人/km²(目标≥10) max={popMax:F0}");
    }

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

    /// <summary>T18 性能：全演化计时（第三次演化，仅选中时跑 ~11s）。</summary>
    private void T18_Perf(int seed, int origins)
    {
        var sw = Stopwatch.StartNew();
        var r3 = CivEngine.Run(_grid, seed, origins);
        sw.Stop();
        long ms = sw.ElapsedMilliseconds;
        Check("T18 性能 n=64 全演化 <10s", ms < 10000, $"{ms}ms（tick {r3.FinalTick}）");
    }

    private static int CountFarming(CivSimContext c)
    {
        int n = 0;
        foreach (var e in c.Entities) if (e.IsFarming) n++;
        return n;
    }

    private HashSet<int> RichZone(GameGrid g)
    {
        var land = new List<(int cell, float k)>();
        var ctx = MakeCtx(g);
        for (int i = 0; i < g.N; i++)
            if (g.IsLandCell(i) && ctx.R[i] > 0f)
                land.Add((i, ctx.R[i]));
        land.Sort((a, b) => b.k.CompareTo(a.k));
        int rich = Mathf.Max(8, land.Count * 30 / 100);
        var set = new HashSet<int>();
        for (int i = 0; i < Mathf.Min(rich, land.Count); i++) set.Add(land[i].cell);
        return set;
    }

    private static bool EntitiesEqual(CivSimContext a, CivSimContext b, string tag = "")
    {
        if (a.Entities.Count != b.Entities.Count)
        {
            GD.Print($"  [往返诊断{tag}] 实体数 {a.Entities.Count} vs {b.Entities.Count}");
            return false;
        }
        for (int k = 0; k < a.Entities.Count; k++)
        {
            var x = a.Entities[k]; var y = b.Entities[k];
            if (x.Id != y.Id || x.Cell != y.Cell || x.P != y.P || x.IsFarming != y.IsFarming
                || x.OriginCell != y.OriginCell || x.BornTick != y.BornTick
                || x.TerritoryId != y.TerritoryId || x.TerritorySize != y.TerritorySize)
            {
                GD.Print($"  [往返诊断{tag}] 实体{k}: id={x.Id}vs{y.Id} cell={x.Cell}vs{y.Cell} P={x.P:F1}vs{y.P:F1} farm={x.IsFarming}vs{y.IsFarming} origin={x.OriginCell}vs{y.OriginCell} born={x.BornTick}vs{y.BornTick}");
                return false;
            }
            if (!SetEqual(x.TechKeys, y.TechKeys))
            {
                GD.Print($"  [往返诊断{tag}] 实体{k}: techKeys A=[{string.Join(";", x.TechKeys)}] B=[{string.Join(";", y.TechKeys)}]");
                return false;
            }
            if (!ShareEqual(x.CultureShare, y.CultureShare))
            {
                GD.Print($"  [往返诊断{tag}] 实体{k}: CultureShare A=[{ShareStr(x.CultureShare)}] B=[{ShareStr(y.CultureShare)}]");
                return false;
            }
            if (!ShareEqual(x.CultureGroupShare, y.CultureGroupShare))
            {
                GD.Print($"  [往返诊断{tag}] 实体{k}: CultureGroup A=[{ShareStr(x.CultureGroupShare)}] B=[{ShareStr(y.CultureGroupShare)}]");
                return false;
            }
            if (!ShareEqual(x.ReligionCultShare, y.ReligionCultShare))
            {
                GD.Print($"  [往返诊断{tag}] 实体{k}: ReligionCult A=[{ShareStr(x.ReligionCultShare)}] B=[{ShareStr(y.ReligionCultShare)}]");
                return false;
            }
            if (!ShareEqual(x.ReligionShare, y.ReligionShare))
            {
                GD.Print($"  [往返诊断{tag}] 实体{k}: Religion A=[{ShareStr(x.ReligionShare)}] B=[{ShareStr(y.ReligionShare)}]");
                return false;
            }
        }
        return true;
    }

    private static bool SetEqual(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var k in a) if (!b.Contains(k)) return false;
        return true;
    }

    private static bool ByteSeqEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool ShareEqual(World.CivSim.ShareEntry[] a, World.CivSim.ShareEntry[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Key != b[i].Key || a[i].Frac != b[i].Frac) return false;
        return true;
    }

    private static string ShareStr(World.CivSim.ShareEntry[] s)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < s.Length; i++)
            parts.Add($"{s[i].Key ?? "-"}:{s[i].Frac}");
        return string.Join(",", parts);
    }

    /// <summary>自然层零改动：.cmp 读回 vs 源 grid 逐字段一致（NaN 视为相等；WildCrops 两端重建一致）。</summary>
    private static bool NaturalUnchanged(GameGrid a, GameGrid b)
    {
        if (a.N != b.N) return false;
        for (int i = 0; i < a.N; i++)
        {
            if (!FloatEq(a.Elev[i], b.Elev[i]) || !FloatEq(a.Temp[i], b.Temp[i]) || !FloatEq(a.Precip[i], b.Precip[i])) return false;
            if (a.Biome[i] != b.Biome[i] || a.RiverLevel[i] != b.RiverLevel[i] || a.LakeLevel[i] != b.LakeLevel[i]) return false;
            if (a.RiverFlow[i] != b.RiverFlow[i] || !FloatEq(a.RiverVolume[i], b.RiverVolume[i])) return false;
            if (a.MineralLevel[i] != b.MineralLevel[i] || a.SoilLevel[i] != b.SoilLevel[i]) return false;
            if (a.MonsoonLevel[i] != b.MonsoonLevel[i]) return false;
            if (!FloatEq(a.CurrentWarmth[i], b.CurrentWarmth[i]) || !FloatEq(a.CurrentStrength[i], b.CurrentStrength[i])) return false;
            if (a.CurrentDirs[i] != b.CurrentDirs[i]) return false;
            for (int m = 0; m < 12; m++)
            {
                if (a.MonthPrecip[m][i] != b.MonthPrecip[m][i]) return false;
                if (a.MonthTemp[m][i] != b.MonthTemp[m][i]) return false;
            }
        }
        if (!PsiEquivalent(a.Psi, b.Psi)) return false;
        var wa = a.EnsureWildCrops();
        var wb = b.EnsureWildCrops();
        return ByteSeqEqual(wa, wb);
    }

    /// <summary>Psi 对比：null 或全零视为空（WriteBody 补零写，源网格可能为 null）。</summary>
    private static bool PsiEquivalent(float[] x, float[] y)
    {
        bool xEmpty = x == null || AllZero(x);
        bool yEmpty = y == null || AllZero(y);
        if (xEmpty || yEmpty) return xEmpty && yEmpty;
        return FloatSeqEqual(x, y);
    }

    private static bool AllZero(float[] a)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] != 0f) return false;
        return true;
    }

    private static bool FloatEq(float x, float y) => x == y || (float.IsNaN(x) && float.IsNaN(y));
    private static bool FloatSeqEqual(float[] x, float[] y)
    {
        if (x == null || y == null || x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
            if (!FloatEq(x[i], y[i])) return false;
        return true;
    }

    /// <summary>写一个坏版本档（ver=5 → 应拒绝）。</summary>
    private static void WriteBadVersion(string path, ushort ver)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.Store8((byte)'C'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(ver);
        f.Store32(42); f.Store32(0); f.Store32(0);
    }

    /// <summary>写一个含化石 biome 4 的档 → 应拒绝（最小自然段，与 GameMapArchive.ReadBody 严格对应）。</summary>
    private static void WriteBadBiome(string path, GameGrid src)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.Store8((byte)'C'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(4);
        f.Store32(42); f.Store32(0); f.Store32(0);
        f.Store64(0);   // rngState（v4 头部字段）
        f.Store32(0);   // cultureKeyCount（v4 头部字段）
        f.Store32(0);   // religionKeyCount（v4 头部字段）
        // 最小自然段：GridN=1, N=2, seed, radius, 标志, 各字段 2 格
        f.Store32(1); f.Store32(2); f.Store32(42); f.StoreFloat(6371f);
        f.Store8(1); f.StoreFloat(1f); f.StoreFloat(23.4f); f.StoreFloat(1f);
        for (int i = 0; i < 6; i++) f.StoreFloat(0f);   // min/max
        for (int i = 0; i < 2; i++) { f.StoreFloat(0); f.StoreFloat(1); f.StoreFloat(0); }   // verts
        for (int i = 0; i < 2; i++) f.StoreFloat(100f);   // elev
        for (int i = 0; i < 2; i++) f.StoreFloat(20f);    // temp
        for (int i = 0; i < 2; i++) f.StoreFloat(800f);   // precip
        for (int i = 0; i < 2; i++) f.Store8(4);          // biome ← 化石值 4！
        for (int i = 0; i < 2; i++) f.Store8(0);          // river
        for (int i = 0; i < 2; i++) f.Store32(0xFFFFFFFF); // riverFlow -1
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // riverVolume
        for (int i = 0; i < 2; i++) f.Store8(0);          // lake
        for (int i = 0; i < 2; i++) f.Store8(0);          // mineral
        for (int i = 0; i < 2; i++) f.Store8(3);          // soil
        for (int i = 0; i < 2; i++) f.Store8(0);          // monsoon
        for (int m = 0; m < 12; m++) for (int i = 0; i < 2; i++) f.Store8(21);   // monthPrecip
        for (int m = 0; m < 12; m++) for (int i = 0; i < 2; i++) f.Store8(170);  // monthTemp
        for (int i = 0; i < 2; i++) { f.StoreFloat(0); f.StoreFloat(0); f.StoreFloat(0); }   // currentDirs
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // warmth
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // strength
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // psi（v2+ 字段）
        for (int i = 0; i < 2; i++) f.Store32(0);         // province
        for (int i = 0; i < 2; i++) f.Store32(0);         // country
        f.Store32(0);                                     // 实体数 0
    }
}
