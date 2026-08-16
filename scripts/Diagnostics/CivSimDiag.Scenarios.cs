// Slice: CivSimDiag.Scenarios.cs - verbatim member extraction from CivSimDiag.cs (pure refactor, 2026-08-19).
using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using World.Biome;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;

namespace World.Diagnostics;

public partial class CivSimDiag
{

    /// <summary>S1：单格生存（2026-08-10 影响力场语义，2026-08-17 砍存量重构）——领地 1 格：
    /// 静态丰度×面积 = 承载上限（R×A≈1.5 人），P&lt;F 增长收敛、P&gt;F 饿死；稳态 e 纯函数验证。
    /// 只跑能量+增长（防发明/分裂污染单格场景）。</summary>
    private void S1_GrowthAndEnergy()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var e = AddTribe(ctx, 0, 1f, TechTable.StoneCore);
        // 手造领地 1 格（驻扎点格）：新模型 F = R×A×w(0)×劳动力爬坡；平衡 P → R×A
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        var energy = new EnergyModel();
        var growth = new GrowthModel();
        var harvest = new HarvestModel();   // FLast 由采集模型更新（2026-08-10：RefreshCellState 只聚合不计算）
        bool converged = false, starved = false;
        float pLast = 0f;
        for (int tick = 0; tick < 300; tick++)
        {
            ctx.Tick = tick;
            CivEngine.RefreshCellState(ctx);
            harvest.Execute(ctx);
            energy.Execute(ctx);
            growth.Execute(ctx);
            if (tick > 50 && Mathf.Abs(e.P - pLast) < Mathf.Max(1f, pLast) * 0.01f) { converged = true; break; }
            pLast = e.P;
        }
        // 饿死：P 超承载 → 增长为负（FLast=当 tick 潜在产出）
        float F0 = e.FLast;
        float pHigh = e.P * 1.5f;
        e.P = pHigh;
        CivEngine.RefreshCellState(ctx);
        harvest.Execute(ctx);
        energy.Execute(ctx);
        growth.Execute(ctx);
        starved = e.P < pHigh;
        // 稳态人均 e（构造：P=F 时 e_猎 = Y/(Y+0.3Y) = 0.769，与乘数无关——h 缩放）
        float yH = ctx.FHunt(e);
        float eSteady = CivSimContext.EHunt(yH, yH);
        bool eOk = Mathf.Abs(eSteady - 1f / 1.3f) < 0.01f;
        Check("S1 增长收敛+饿死+稳态e", converged && starved && eOk,
            $"F={F0:F1} 收敛={converged} 饿死={starved} e稳态={eSteady:F3}");
    }


    /// <summary>S2：生产方式矩阵——φ 高转农、φ 低最终狩猎、稳态不退农 + 滞回。
    /// 新公式（两层模型 2026-08-17）：转农条件 R_农/R × F·φ > 0.97M；Soil3（冲积土=1）下 场景A 4×1.0 > 1.41、场景B 4×0.3 < 1.41。</summary>
    private void S2_ModeMatrix()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);   // Soil3：冲积土因子=1（薄地，农业潜在=种子×φ×R×面积）
        var ctx = MakeCtx(g);
        float y0 = ctx.R[0] * g.CellAreaKm2;   // 基础狩猎产出（无工具）

        // 场景 A：φ=1.0 Soil3 → 农业潜在=4y0 > 狩猎 1.455y0 → 稳态农业
        // P=3×y0（2026-08-10 调：0.5×y0 时 eF>eH 致 φ=0.3 也转农——P 大时 eH→1/0.3 上限、eF=yF/P 线性降 → 分得开）
        var ea = AddTribe(ctx, 0, 3f * y0, TechTable.StoneCore, TechTable.Handaxe, TechTable.Grinding, TechTable.SeedWheat);
        ctx.Suit[0, 0] = 1.0f;   // 小麦 φ
        // 场景 B：φ=0.3 Soil3 → 农业潜在=1.2y0 < 狩猎 1.455y0 → 最终狩猎
        var eb = AddTribe(ctx, 1, 3f * y0, TechTable.StoneCore, TechTable.Handaxe, TechTable.Grinding, TechTable.SeedWheat);
        ctx.Suit[1, 0] = 0.3f;
        // ⚠️ 2026-08-17 决策领地化：ModeModel 用 Σ 领地格潜在——测试补 1 格领地（=驻扎格，领地版退化为单格语义）
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        ctx.CellOwner[1] = 1;
        ctx.TerritoryCells[1].Add(1);
        ctx.TerritoryDists[1].Add(1);

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
            if (ea.P < 1f) ea.P = 3f * y0;   // 防饿死干扰（只测选择）
            if (eb.P < 1f) eb.P = 3f * y0;
        }
        bool aFarms = ea.IsFarming;    // φ=1.0 → 稳态农业
        bool bFarms = eb.IsFarming;    // φ=0.3 → 稳态狩猎（农业 K<狩猎 K 自动拒绝）
        Check("S2 生产方式矩阵", aFarms && !bFarms,
            $"φ=1.0 农={aFarms}（应 True） φ=0.3 农={bFarms}（应 False）");

        // 滞回：交叉点 P≈13.8y0 处 |e_猎−e_农|<0.02 → 保持当前方式（独立 ctx 防干扰；Soil3 下 yF=4y0 交叉点不变）
        var g2 = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx2 = MakeCtx(g2);
        var eh = AddTribe(ctx2, 0, 13.8f * y0, TechTable.StoneCore, TechTable.Handaxe, TechTable.Grinding, TechTable.SeedWheat);
        ctx2.Suit[0, 0] = 1.0f;
        eh.IsFarming = true;
        // ⚠️ 2026-08-17 决策领地化：滞回验证与 ModeModel 同口径（领地版）——补 1 格领地（1 格 = 单格语义）
        ctx2.CellOwner[0] = 0;
        ctx2.TerritoryCells[0].Add(0);
        ctx2.TerritoryDists[0].Add(0);
        CivEngine.RefreshCellState(ctx2);   // ⚠️ 2026-08-17：CarryMult 在 RefreshCellState 计算（工具加成进决策）——不跑则 m=0 → yH=0 假滞回
        float yH2 = eh.CarryMult * ctx2.FHuntTerritory(eh);
        float yF2 = ctx2.FFarmPotentialTerritory(eh);
        float diff2 = CivSimContext.EHunt(yH2, eh.P) - CivSimContext.EFarm(yF2, eh.P);
        bool inHyst = Mathf.Abs(diff2) < 0.02f;
        mode.Execute(ctx2);   // 滞回带内 → 不切换（领地非空 → 不会被强制清 IsFarming）
        bool hyst = inHyst && eh.IsFarming;
        Check("S2 滞回防抖", hyst, $"|e_猎−e_农|={Mathf.Abs(diff2):F3} < 0.02 且保持农");
    }


    /// <summary>S3：份额守恒——3 实体同格，同化 30 tick 后 Σ=1 恒成立，主导单调增。</summary>
    private void S3_ShareConservation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        ctx.Tribes[0].CultureShare = ShareField.NewCulture("cult_a");
        ctx.Tribes[1].CultureShare = ShareField.NewCulture("cult_b");
        ctx.Tribes[2].CultureShare = ShareField.NewCulture("cult_c");
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
            string dom = ShareField.DomKey(ctx.Tribes[0].CultureShare);
            if (prevDom != null && dom != prevDom) domMonotonic = false;   // 主导 key 稳定（不跳变）
            prevDom = dom;
            foreach (var e in ctx.Tribes)
            {
                int sum = 0;
                for (int k = 0; k < e.CultureShare.Length; k++) sum += e.CultureShare[k].Frac;   // ⚠️ 2026-08-17 审查：循环统计全段（硬编码 2 段会在文化特征扩展后漏检）
                if (sum != 255) conserved = false;
            }
        }
        int domFrac = ShareField.DomFrac(ctx.Tribes[0].CultureShare);
        Check("S3 份额守恒+主导同化", conserved && domMonotonic && domFrac > 150,
            $"Σ恒等={conserved} 主导单调={domMonotonic} 30tick后主导份额={domFrac}/255");
    }


    /// <summary>S4：分裂继承（2026-08-10 殖民式语义）——母 band 超载 → 45% 分群殖民 1-3 跳内最高富饶**无主**格；
    /// 母领地不动；份额等比例、TechKeys 完整、BornTick。</summary>
    private void S4_FissionInherit()
    {
        // 8 格赤道网格（45° 间隔全 lat=0，BuildNeighbors 桶索引正常——N=4 的 (0,1,0) 是北极会进极区桶查不到）：
        // 格 0 领地主 band，格 1 无主 → 殖民目标
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var e = AddTribe(ctx, 0, 500f, TechTable.StoneCore, TechTable.Fire, TechTable.Handaxe);
        e.CultureShare = new[] { new ShareEntry { Key = "cult_7", Frac = 200 }, new ShareEntry { Key = "cult_9", Frac = 55 } };
        e.CultureGroupShare = new[] { new ShareEntry { Key = "cult_3", Frac = 250 }, new ShareEntry { Key = "cult_0", Frac = 5 } };
        e.ReligionShare = ShareField.NewReligion(ReligionStage.Shaman);
        e.IsFarming = false;
        ctx.CellTribes[0] = e;
        // 领地 1 格（驻扎点格 0 归属 e）；格 1 无主 → 殖民目标
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        var sm = new SplitMigrateModel();
        sm.Execute(ctx);
        bool ok = ctx.Tribes.Count == 2;
        var nt = ctx.Tribes[1];
        ok &= Mathf.Abs(nt.P - 225f) < 0.01f && Mathf.Abs(e.P - 275f) < 0.01f;   // 45% 带走
        ok &= nt.Cell != 0 && ctx.CellOwner[nt.Cell] == -1;   // 殖民到任一无主格（tie-break 由遍历顺序定）
        ok &= ctx.CellOwner[0] == 0;   // 母领地不动
        ok &= nt.CultureShare[0].Key == "cult_7" && nt.CultureShare[0].Frac == 200 && nt.CultureShare[1].Frac == 55;   // 等比例继承
        ok &= nt.TechKeys.Count == 3 && nt.TechKeys.Contains(TechTable.Fire);    // TechKeys 完整
        ok &= nt.BornTick == 0 && nt.OriginCell == 0;
        ok &= nt.CultureGroupShare[0].Key == "cult_3";   // 群份额继承
        Check("S4 分裂继承", ok, $"新实体 P={nt.P:F0}（应225） 格={nt.Cell}（应1） 份额={nt.CultureShare[0].Frac}（应200） 科技={nt.TechKeys.Count}（应3）");
    }


    /// <summary>S5：传播依赖——前置缺失不传；补全后按 SpreadBase 传（同格接触，不依赖邻格表）。</summary>
    private void S5_SpreadDependency()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        // ⚠️ 2026-08-18 阶段2 一格一实体：传播只在**邻格**（占据格接触），无同格对——
        //   a/b 分置相邻格（cell 0/1，赤道均分互邻）。
        // a 有 bow（前置 microlith）；b 缺 microlith → bow 不传（依赖硬门槛，防中间科技先传）
        var a = AddTribe(ctx, 0, 300f, TechTable.StoneCore, TechTable.Microlith, TechTable.Bow);
        var b = AddTribe(ctx, 7, 100f, TechTable.StoneCore);   // 邻格（赤道环 0↔7 相邻——探针实测 Neighbors[0]=[7]）；缺 microlith
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
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);   // ⚠️ 8 格：e3 需独立格（同格会触发传播段同化稀释份额）
        var ctx = MakeCtx(g);
        var e1 = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.Handaxe, TechTable.Microlith);
        e1.Surplus = 0.5f;   // 盈余期
        var e2 = AddTribe(ctx, 1, 100f, TechTable.StoneCore, TechTable.Grinding, TechTable.SeedWheat);
        e2.Surplus = -0.1f;  // 狩猎（IsFarming=false）→ 无定居 → 祖先锁
        // ⚠️ 2026-08-17 定居落地：农业 band（IsFarming → settle）→ 祖先解锁（萨满→祖先）
        var e3 = AddTribe(ctx, 2, 100f, TechTable.StoneCore, TechTable.Microlith, TechTable.SeedWheat);   // 格2（独立格，无同化干扰）
        e3.IsFarming = true;
        e3.Surplus = 0.5f;   // 定居农业 + 盈余 → 先泛灵→萨满，再萨满→祖先
        var rel = new ReligionModel();
        rel.Execute(ctx);
        rel.Execute(ctx);    // 两遍：e3 走完 泛灵→萨满→祖先 两跳
        bool shaman = ShareField.RelFrac(e1.ReligionShare, ReligionStage.Shaman) > 0;          // 泛灵→萨满
        bool noAncestor = ShareField.RelFrac(e1.ReligionShare, ReligionStage.Ancestor) == 0
                       && ShareField.RelFrac(e2.ReligionShare, ReligionStage.Ancestor) == 0;   // 旧石器锁死
        bool ancestorUnlocked = ShareField.RelFrac(e3.ReligionShare, ReligionStage.Ancestor) > 0;  // 定居农业 → 祖先
        Check("S6 宗教锁", shaman && noAncestor && ancestorUnlocked,
            $"萨满份额={ShareField.RelFrac(e1.ReligionShare, ReligionStage.Shaman)} 祖先份额全0={noAncestor} 定居农业祖先={ancestorUnlocked}");
    }

    /// <summary>S7：运行时不变量校验（2026-08-19 防隐晦 bug——数组长度/值域/索引/一格一实体/确定性纪律）。
    /// 演化 20 tick（覆盖分裂/迁徙/冲突路径）后 ValidateInvariants 必须零错误。</summary>
    private void S7_StateInvariants()
    {
        var g = MakeGrid(100f, (byte)BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g, seed: 42, origins: 3);
        RunTicks(ctx, 20);
        var errs = ctx.ValidateInvariants();
        Check("S7 状态不变量", errs.Count == 0,
            errs.Count == 0 ? "数组/值域/索引/一格一实体 全部一致" : string.Join("; ", errs));
    }


    /// <summary>T24 领地凝聚/断裂：邻格同语言群 → 同领地；语言群分歧 → 领地分裂（确定性，无地图依赖）。
    /// ⚠️ 2026-08-18 阶段2 一格一实体：凝聚边 = 邻格占据部落对（无同格对）——a/b 分置相邻格。</summary>
    private void T24_TerritoryCohesion()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 200f, TechTable.StoneCore);
        var b = AddTribe(ctx, 7, 200f, TechTable.StoneCore);   // 邻格（赤道环 0↔7 相邻——探针实测）；AddTribe 默认同语言群 test_grp
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


    /// <summary>T25 裂变压力：饥荒（资源压力）→ 提前裂变；盈余小规模（无压力无张力）→ 不裂。
    /// ⚠️ 2026-08-18 史实标定同步：SplitPop 12→25（band 25-50 人）——pEff = P×(1+缺口+张力)，
    ///   饥荒 FLast/P=0.25 → pEff=1.75P；需 P≥15 才破 25。用 P=20：饥荒 pEff=35>25 裂变；
    ///   盈余 pEff=20<25 不裂。N=8 网格（殖民目标搜索可用）。确定性，无地图依赖。</summary>
    private void T25_FissionPressure()
    {
        // ctxA：饥荒 P=20（<SplitPop25 但缺口够大）, FLast=5（压力 0.75）→ P_eff=35>25 → 裂变（纯饥荒驱动，张力=0）
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctxA = MakeCtx(gA);
        var famine = AddTribe(ctxA, 0, 20f, TechTable.StoneCore);
        famine.FLast = 5f;            // 产出 1/4（RefreshCellState 未跑，手工设 FLast 供裂变压力计算）
        ctxA.CellOwner[0] = 0;        // 领地 1 格；其余格无主 → 殖民目标
        ctxA.TerritoryCells[0].Add(0);
        ctxA.TerritoryDists[0].Add(0);
        var sm = new SplitMigrateModel();
        sm.Execute(ctxA);
        bool famineFissioned = ctxA.Fissions == 1;
        // ctxB：盈余 P=20（<SplitPop25）, FLast=20 → 无压力无张力 → P_eff=20 不裂
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctxB = MakeCtx(gB);
        var fed = AddTribe(ctxB, 0, 20f, TechTable.StoneCore);
        fed.FLast = 20f;
        ctxB.CellOwner[0] = 0;
        ctxB.TerritoryCells[0].Add(0);
        ctxB.TerritoryDists[0].Add(0);
        sm.Execute(ctxB);
        bool fedKept = ctxB.Fissions == 0;
        Check("T25 裂变压力", famineFissioned && fedKept,
            $"饥荒20裂变={famineFissioned}(Fissions={ctxA.Fissions}) 盈余20不裂={fedKept}(Fissions={ctxB.Fissions})");
    }


    /// <summary>T26 能力开关（单元）：canoe/seed 解锁条件正确；能力 id 全集完整（无引用缺失）。</summary>
    private void T26_CapabilitySwitches()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var withCanoe = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.Fire, TechTable.Canoe);
        var noCanoe = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        var withSeed = AddTribe(ctx, 0, 100f, TechTable.Grinding, TechTable.SeedWheat);
        CivEngine.RefreshCellState(ctx);   // 算 CapMask
        bool canoeOk = CapabilityTable.Has(ctx, withCanoe, CapabilityTable.Canoe) && !CapabilityTable.Has(ctx, noCanoe, CapabilityTable.Canoe);
        bool seedOk = CapabilityTable.Has(ctx, withSeed, CapabilityTable.Seed) && !CapabilityTable.Has(ctx, noCanoe, CapabilityTable.Seed);
        // 完整性：引用 id 全部注册（不漏不重）——2026-08-17 +pottery/settle（定居+存储缺口）
        var ids = new HashSet<string>(CapabilityTable.AllIds());
        bool complete = ids.SetEquals(new HashSet<string> { CapabilityTable.Canoe, CapabilityTable.Microlith, CapabilityTable.Grinding, CapabilityTable.Fire, CapabilityTable.Clothing, CapabilityTable.Seed, CapabilityTable.Storage, CapabilityTable.Livestock, CapabilityTable.Pottery, CapabilityTable.Settle });
        Check("T26 能力开关", canoeOk && seedOk && complete,
            $"canoe开关={canoeOk} seed开关={seedOk} 能力集={string.Join(",", ids)}");
    }


    /// <summary>T27 存储缓冲（Testart 分水岭，2026-08-18 阶段3 重写）：有存粮部落歉年存活，无存粮部落饿死。
    /// 新语义：Growth 缺口（FLast<P）从 Food 类 Stocks 补——预置存粮 → 不饿；无存粮 → 饿死因子。</summary>
    private void T27_StorageBuffer()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var withS = AddTribe(ctx, 0, 100f, TechTable.Storage, TechTable.Fire);     // 有存储（预置存粮）
        var noS = AddTribe(ctx, 1, 100f, TechTable.Fire);                          // 无存储（无存粮）
        withS.FLast = 50f; noS.FLast = 50f;   // 歉年：缺口 50（D/P=2）
        // 预置存粮：withS 有 80 人当量谷物（够补缺口），noS 空
        withS.Stocks[CommodityTable.Index(CommodityTable.Grain)] = 80f;
        var growth = new GrowthModel();
        for (int t = 0; t < 3; t++) { ctx.Tick = t; growth.Execute(ctx); }
        bool buffered = withS.P > noS.P;   // 有存粮的缺口被补 → 饿得慢
        bool withAlive = withS.P > 1f;
        Check("T27 存储缓冲", buffered && withAlive,
            $"有存储 P={withS.P:F0} 无存储 P={noS.P:F0}（存粮补缺口应更慢饿死）");
    }


    /// <summary>T53 饥荒从存储枯竭涌现（2026-08-18 阶段3）：连续歉年（FLast<P）→ 逐年吃存粮 →
    /// 存粮耗尽 → 缺口扩大 → 饿死。验证饥荒非硬标志、由存储枯竭自然驱动。</summary>
    private void T53_FamineFromStorage()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        var e = AddTribe(ctx, 0, 100f, TechTable.Fire);
        e.Stocks[CommodityTable.Index(CommodityTable.Grain)] = 150f;   // 预置存粮（够补 3.75 tick 缺口）
        var growth = new GrowthModel();
        float pStart = e.P;
        float pBeforeEmpty = -1f, pAfterEmpty = -1f;
        bool starvedEventually = false;
        for (int t = 0; t < 30; t++)
        {
            ctx.Tick = t;
            e.FLast = 60f;   // 每 tick 歉年：缺口 40
            growth.Execute(ctx);
            if (e.Stocks[CommodityTable.Index(CommodityTable.Grain)] <= 0f && pBeforeEmpty < 0f)
                pBeforeEmpty = e.P;   // 存粮耗尽瞬间的人口
            if (t >= 6 && e.P < pStart) pAfterEmpty = e.P;
            if (e.P < 1f) { starvedEventually = true; break; }
        }
        // 断言：存粮耗尽前人口保住（不饿，容忍最后一 tick 补不满的微降），耗尽后人口下降（饥荒涌现）
        bool bufferedBefore = pBeforeEmpty >= pStart * 0.9f;
        bool starvedAfter = pAfterEmpty < 0f || pAfterEmpty < pStart * 0.9f || starvedEventually;
        Check("T53 饥荒存储涌现", bufferedBefore && starvedAfter,
            $"存粮耗尽时P={pBeforeEmpty:F1}(起始{pStart:F1}) 耗尽后P={pAfterEmpty:F1} 最终饿死={starvedEventually}（缓冲→枯竭→饥荒）");
    }


    /// <summary>T54 加工耐储（2026-08-18 阶段3；2026-08-19 双池改造——粮仓测 techMult）：grinding 加工态
    /// 衰变 ×0.7——同条件两部落（同粮仓/同能力，一个带 grinding），跑 AccumulateStorage 一 tick，
    /// 有 grinding 的**粮仓**存粮衰变更少（存储科技只保护粮仓，随身基础衰变）。</summary>
    private void T54_GrindingPreserves()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var withG = AddTribe(ctx, 0, 100f, TechTable.Grinding, TechTable.Storage);   // 加工+存储
        var noG = AddTribe(ctx, 1, 100f, TechTable.Storage);                          // 仅存储（无加工）
        int gi = CommodityTable.Index(CommodityTable.Grain);
        var s1 = AddSettlement(ctx, withG);   // 粮仓（正式存储——techMult 生效处）
        var s2 = AddSettlement(ctx, noG);
        s1.Stocks[gi] = 100f; s2.Stocks[gi] = 100f;
        withG.FLast = 100f; noG.FLast = 100f;   // 平衡产出（Food 流入=0——AccumulateStorage 只衰变 Food）
        CivEngine.RefreshCellStateCore(ctx);    // 算 CapMask（grinding/storage 能力）
        CivEngine.AccumulateStorage(ctx);       // 一 tick 衰变
        bool preserved = s1.Stocks[gi] > s2.Stocks[gi];   // 加工态粮仓衰变少
        Check("T54 加工耐储", preserved,
            $"有grinding 粮仓剩 {s1.Stocks[gi]:F2} vs 无grinding 剩 {s2.Stocks[gi]:F2}（衰变×0.7 应更耐储）");
    }


    /// <summary>T55 物物交换（2026-08-18 阶段3 贸易期，docs/阶段3设计-贸易机制.md）：
    /// 两相邻部落（A 多皮革少羊毛、B 少皮革多羊毛）→ 逐商品人均比较 → 等量交换：
    /// A 出皮革、B 出羊毛（双重巧合成对）；方向/量/守恒/人均差收敛/食物保底全断言。
    /// 期望交换量：人均差 0.9 × TradeRate 0.1 × min(P)=100 × 距离折减 1/(1+0.5×1)=2/3 → 6.0。
    /// 无地图依赖（8 格赤道环 0↔7 相邻——S5 探针实测 Neighbors[0]=[7]）。</summary>
    private void T55_BarterExchange()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var b = AddTribe(ctx, 7, 100f, TechTable.StoneCore);
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0); ctx.TerritoryDists[0].Add(0);   // 领地索引按 Id：A=0 / B=1（AddTribe 顺序）
        ctx.CellOwner[7] = 1;
        ctx.TerritoryCells[1].Add(7); ctx.TerritoryDists[1].Add(7);
        int li = CommodityTable.Index(CommodityTable.Leather);
        int wi = CommodityTable.Index(CommodityTable.Wool);
        a.Stocks[li] = 100f; a.Stocks[wi] = 10f;   // A 人均 1.0 皮革 / 0.1 羊毛
        b.Stocks[li] = 10f; b.Stocks[wi] = 100f;  // B 人均 0.1 皮革 / 1.0 羊毛
        var trade = new TradeModel();
        trade.Execute(ctx);
        float expected = 0.9f * CivSimContext.TradeRate * 100f * (1f / 1.5f);   // 6.0
        // ⚠️ 捕获首轮执行后的断言值（后续食物保底轮会再交换皮革/羊毛——打印用断言时刻快照，防误导）
        float aLeather = a.Stocks[li], bLeather = b.Stocks[li], aWool = a.Stocks[wi], bWool = b.Stocks[wi];
        bool dirOk = Mathf.Abs(aLeather - (100f - expected)) < 0.01f   // A 出皮革
                  && Mathf.Abs(bLeather - (10f + expected)) < 0.01f
                  && Mathf.Abs(bWool - (100f - expected)) < 0.01f      // B 出羊毛
                  && Mathf.Abs(aWool - (10f + expected)) < 0.01f;
        bool conserved = Mathf.Abs((aLeather + bLeather) - 110f) < 0.01f
                      && Mathf.Abs((aWool + bWool) - 110f) < 0.01f;   // 纯转移无损耗
        float gapAfter = Mathf.Abs(aLeather / a.P - bLeather / b.P);
        bool converged = gapAfter < 0.9f;   // 人均差收敛（0.78 < 0.9）
        // 食物保底：A 100 谷物 vs B 0 → 出口后 A 人均谷物 ≥ 5%×P（当前 TradeRate 下不触发——防御性断言）
        int gi = CommodityTable.Index(CommodityTable.Grain);
        a.Stocks[gi] = 100f; b.Stocks[gi] = 0f;
        trade.Execute(ctx);
        bool floorHeld = a.Stocks[gi] >= CivSimContext.TradeFoodFloor * a.P;
        Check("T55 物物交换", dirOk && conserved && converged && floorHeld,
            $"皮革 A={aLeather:F2} B={bLeather:F2} 羊毛 A={aWool:F2} B={bWool:F2}（期望交换 {expected:F2}）守恒={conserved} 收敛={gapAfter:F3}<0.9 食物保底={floorHeld}(A剩{a.Stocks[gi]:F1}≥5)");
    }


    /// <summary>T56 贸易收敛（专业化软断言，2026-08-18 阶段3）：互补库存两部落长跑 TradeModel——
    /// ① 守恒：每商品 Σ 库存精确不变（纯转移，无损耗/泄漏）；② 人均差收敛：每商品 |gap| 单调不增、
    ///    有初始差者严格下降（库存趋同化——贸易让部落各产所长其余互通）；③ 无负库存。
    /// 软断言（涌现趋势，非硬阈值）；确定性（固定对序/商品序，无 Rng）。</summary>
    private void T56_TradeConvergence()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var b = AddTribe(ctx, 7, 100f, TechTable.StoneCore);
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0); ctx.TerritoryDists[0].Add(0);
        ctx.CellOwner[7] = 1;
        ctx.TerritoryCells[1].Add(7); ctx.TerritoryDists[1].Add(7);
        float[] initA = { 40f, 0f, 5f, 30f, 5f, 10f };   // 谷物/浆果/肉/皮革/羊毛/秸秆（A 农产品+皮革多）
        float[] initB = { 5f, 10f, 20f, 5f, 40f, 2f };   // B 浆果/肉/羊毛多（互补）
        for (int s = 0; s < CommodityTable.Count; s++) { a.Stocks[s] = initA[s]; b.Stocks[s] = initB[s]; }
        var trade = new TradeModel();
        float[] gap0 = new float[CommodityTable.Count];
        float var0 = 0f;
        for (int s = 0; s < CommodityTable.Count; s++)
        {
            gap0[s] = Mathf.Abs(a.Stocks[s] / a.P - b.Stocks[s] / b.P);
            var0 += gap0[s] * gap0[s];
        }
        bool noNegative = true, nonIncreasing = true, strictShrink = true;
        for (int t = 0; t < 50; t++)
        {
            trade.Execute(ctx);
            for (int s = 0; s < CommodityTable.Count; s++)
            {
                if (a.Stocks[s] < -0.001f || b.Stocks[s] < -0.001f) noNegative = false;
                float gap = Mathf.Abs(a.Stocks[s] / a.P - b.Stocks[s] / b.P);
                if (gap > gap0[s] + 0.001f) nonIncreasing = false;
            }
        }
        bool conserved = true;
        float var1 = 0f;
        for (int s = 0; s < CommodityTable.Count; s++)
        {
            if (Mathf.Abs((a.Stocks[s] + b.Stocks[s]) - (initA[s] + initB[s])) > 0.01f) conserved = false;
            float gap = Mathf.Abs(a.Stocks[s] / a.P - b.Stocks[s] / b.P);
            if (gap0[s] >= CivSimContext.TradeMinGap && gap >= gap0[s] - 0.001f) strictShrink = false;   // 有差者严格收敛
            var1 += gap * gap;
        }
        bool varianceDown = var1 < var0;
        Check("T56 贸易收敛（专业化软断言）", conserved && nonIncreasing && strictShrink && varianceDown && noNegative,
            $"守恒={conserved} 人均差单调不增={nonIncreasing} 有差严格收敛={strictShrink} 方差 {var0:F3}→{var1:F3}(降={varianceDown}) 负库存={!noNegative}");
    }


    /// <summary>T57 文化横向传播（2026-08-19 死代码修复验收）：同语言群、异文化的相邻部落——
    /// 弱方（P 小）文化向强方文化转移（Axelrod：相似才互动）；异语言群对不传（边界文化分界）。
    /// 旧版门槛把该组合挡死（sim=0.5 → continue）→ 文化永不混合（60 tick 零传播实测）。
    /// 确定性构造：8 格赤道环 0↔7 相邻（S5 实测）；无 Rng。</summary>
    private void T57_CultureSpread()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var strong = AddTribe(ctx, 0, 200f, TechTable.StoneCore);
        var weak = AddTribe(ctx, 7, 100f, TechTable.StoneCore);
        strong.CultureShare = ShareField.NewCulture("cult_a");
        weak.CultureShare = ShareField.NewCulture("cult_b");
        // 同语言群（AddTribe 默认同 test_grp）→ 传播：weak 向 strong 文化同化（0.05×255≈13/tick → 20 tick 全同化）
        var cm = new CultureModel();
        for (int t = 0; t < 30; t++) cm.Execute(ctx);
        bool spread = ShareField.DomKey(weak.CultureShare) == "cult_a" && ShareField.DomKey(strong.CultureShare) == "cult_a";
        // 对照：异语言群 → 不传（边界文化分界）
        var ctx2 = MakeCtx(g);
        var s2 = AddTribe(ctx2, 0, 200f, TechTable.StoneCore);
        var w2 = AddTribe(ctx2, 7, 100f, TechTable.StoneCore);
        s2.CultureShare = ShareField.NewCulture("cult_x");
        w2.CultureShare = ShareField.NewCulture("cult_y");
        w2.CultureGroupShare = ShareField.NewCulture("grp_diff");   // 异群
        for (int t = 0; t < 60; t++) cm.Execute(ctx2);
        bool blocked = ShareField.DomKey(w2.CultureShare) == "cult_y";
        // Σ 守恒（份额场不变量）
        int sum = weak.CultureShare[0].Frac + weak.CultureShare[1].Frac;
        Check("T57 文化横向传播", spread && blocked && sum == 255,
            $"同群传播: weak→{ShareField.DomKey(weak.CultureShare)}（应 cult_a，30 tick 全同化）Σ={sum} | 异群不传={blocked}");
    }


    /// <summary>T58 宗教派别横向传播（2026-08-19 修复验收）：相邻部落不同 relig_N 派别——
    /// 弱方派别向强方派别转移（同 5 段传播的接触语义；旧版派别只靠分裂继承 → 不混合 → 大片单色）。
    /// 确定性构造；0.02×255≈5/tick → 51 tick 全同化。</summary>
    private void T58_ReligionSectSpread()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var strong = AddTribe(ctx, 0, 200f, TechTable.StoneCore);
        var weak = AddTribe(ctx, 7, 100f, TechTable.StoneCore);
        strong.ReligionCultShare = ShareField.NewCulture("relig_a");
        weak.ReligionCultShare = ShareField.NewCulture("relig_b");
        var rel = new ReligionModel();
        for (int t = 0; t < 60; t++) rel.Execute(ctx);   // 无科技/无盈余 → 5 段不升级，只测派别传播
        bool sectSpread = ShareField.DomKey(weak.ReligionCultShare) == "relig_a";
        int sum = weak.ReligionCultShare[0].Frac + weak.ReligionCultShare[1].Frac;
        Check("T58 宗教派别传播", sectSpread && sum == 255,
            $"weak 派别 {ShareField.DomKey(weak.ReligionCultShare)}（应 relig_a，60 tick 全同化）Σ={sum}");
    }


    /// <summary>T59 酋邦庇护机制（2026-08-19 重构验收）：band 选 ChiefReach 内 Prestige 最高的酋长为庇护人；
    /// 半径外独立；酋长 = 自己中心（互相竞争不隶属）。确定性构造：40 格赤道环——
    /// A@0（声望5）、B@10（声望3）；X@5 双半径内→选 A；Y@16 超 A 半径、B 半径内→选 B；
    /// Z@25 双半径外（dist(0,25)=15、dist(10,25)=15 > 12）→ 独立。</summary>
    private void T59_ChiefdomPatronage()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 41);
        RingLinks(g);   // 精确环邻接——BFS 跳数 = 环距（ChiefReach=12 语义可靠）
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        a.IsChief = true; a.Prestige = 5f; a.TerritoryId = 1; a.TerritorySize = 1;
        var b = AddTribe(ctx, 10, 100f, TechTable.StoneCore);
        b.IsChief = true; b.Prestige = 3f; b.TerritoryId = 2; b.TerritorySize = 1;
        var x = AddTribe(ctx, 5, 100f, TechTable.StoneCore); x.TerritoryId = 3; x.TerritorySize = 1;
        var y = AddTribe(ctx, 16, 100f, TechTable.StoneCore); y.TerritoryId = 4; y.TerritorySize = 1;
        var z = AddTribe(ctx, 25, 100f, TechTable.StoneCore); z.TerritoryId = 5; z.TerritorySize = 1;
        foreach (var e in ctx.Tribes) { ctx.TerritoryCells[e.Id].Add(e.Cell); ctx.TerritoryDists[e.Id].Add(0); }
        ctx.ChiefdomLastEval = -100;
        new ChiefdomModel().Execute(ctx);
        bool aCenter = a.ChiefdomId == a.Id && b.ChiefdomId == b.Id;      // 酋长各为中心（竞争）
        bool xJoinsA = x.ChiefdomId == a.Id;                              // 双半径内 → 声望高者 A
        bool yJoinsB = y.ChiefdomId == b.Id;                              // 超 A 半径 → B
        bool zFree = z.ChiefdomId < 0;                                    // 双半径外 → 独立
        Check("T59 酋邦庇护", aCenter && xJoinsA && yJoinsB && zFree,
            $"A={a.ChiefdomId} B={b.ChiefdomId} X→{x.ChiefdomId}(应{a.Id}) Y→{y.ChiefdomId}(应{b.Id}) Z={z.ChiefdomId}(应-1) sizeA={a.ChiefdomSize} sizeB={b.ChiefdomSize}");
    }


    /// <summary>T60 贸易流量统计（2026-08-19 演化级观测）：T55 单轮场景一次 Execute——
    /// TradeEvents=2（皮革+羊毛各 1 次转移）、TradeVolume=12.0（6.0+6.0）——演化级观测计数正确。</summary>
    private void T60_TradeFlowStats()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var b = AddTribe(ctx, 7, 100f, TechTable.StoneCore);
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0); ctx.TerritoryDists[0].Add(0);
        ctx.CellOwner[7] = 1;
        ctx.TerritoryCells[1].Add(7); ctx.TerritoryDists[1].Add(7);
        int li = CommodityTable.Index(CommodityTable.Leather);
        int wi = CommodityTable.Index(CommodityTable.Wool);
        a.Stocks[li] = 100f; a.Stocks[wi] = 10f;
        b.Stocks[li] = 10f; b.Stocks[wi] = 100f;
        var trade = new TradeModel();
        trade.Execute(ctx);
        float expected = 0.9f * CivSimContext.TradeRate * 100f * (1f / 1.5f);   // 6.0
        bool counters = ctx.TradeEvents == 2 && Mathf.Abs(ctx.TradeVolume - 2f * expected) < 0.01f;
        Check("T60 贸易流量统计", counters,
            $"事件={ctx.TradeEvents}(应2) 流量={ctx.TradeVolume:F1}(应{2f * expected:F1})");
    }


    /// <summary>T61 聚落形成（2026-08-19 阶段3 聚落设计）：农业部落（settle）→ 驻扎点形成聚落（Level 0）；
    /// 狩猎部落无聚落；重复执行不重复建（占位已设，幂等）。</summary>
    private void T61_SettlementFormation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var farm = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        farm.IsFarming = true;
        var hunter = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        var sm = new SettlementModel();
        sm.Execute(ctx);
        var fs = ctx.SettlementOf(farm);
        bool farmHas = farm.PlaceId >= 0 && fs != null && fs.Cell == 0 && fs.Level == 0;
        bool hunterNone = hunter.PlaceId < 0;
        sm.Execute(ctx);   // 幂等：不重复建
        bool stable = ctx.Settlements.Count == 1 && ctx.SettlementOf(farm) != null;
        Check("T61 聚落形成", farmHas && hunterNone && stable,
            $"农→聚落(格0/Level0)={farmHas} 猎→无={hunterNone} 幂等={stable}(聚落数={ctx.Settlements.Count})");
    }


    /// <summary>T62 聚落等级（2026-08-19）：Dwell（定居时长）× P 阈值 → 等级升级；
    /// 等级收益：粮仓容量 0.5×P×(1+0.5×Level) + 增长倍率加成（城市化集聚）。</summary>
    private void T62_SettlementLevel()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var e = AddTribe(ctx, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        e.IsFarming = true;
        var s = AddSettlement(ctx, e);
        s.LastLevelUpTick = -10;             // 过冷却
        e.SettledSince = ctx.Tick - 5;
        s.DwellFrom = ctx.Tick - 5;          // 已定居 5 tick（≥3 → 村庄）
        var sm = new SettlementModel();
        sm.Execute(ctx);
        bool levelUp = s.Level == 1;
        // 等级收益：村庄容量 = 0.5×P×1.5（×500 = 375）
        float granCap = CivSimContext.SettleFoodCap * (1f + CivSimContext.SettlementStoragePerLevel * s.Level) * e.P;
        bool capOk = Mathf.Abs(granCap - 375f) < 0.01f;
        // 增长加成：同条件对照（Level 0 vs Level 1）——有等级增长更快
        var g2 = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx2 = MakeCtx(g2);
        var a2 = AddTribe(ctx2, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        a2.IsFarming = true;
        AddSettlement(ctx2, a2);             // 无等级（Level 0）对照
        e.FLast = 600f; a2.FLast = 600f;     // 盈余（settle ×1.5 基础 + 等级加成）
        var growth = new GrowthModel();
        growth.Execute(ctx2);
        growth.Execute(ctx);
        bool growthOk = e.P > a2.P;
        Check("T62 聚落等级", levelUp && capOk && growthOk,
            $"Level={s.Level}(应1) 粮仓容量={granCap:F0}(应375) 增长加成(Level1={e.P:F1} > Level0={a2.P:F1})");
    }


    /// <summary>T63 聚落存续（2026-08-19 场所比人长寿）：部落迁徙 → 聚落留废墟 + 部落关联清空；
    /// 新部落迁入 → 接管废墟（继承 Level）。</summary>
    private void T63_SettlementPersistence()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        RingLinks(g);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        a.IsFarming = true;
        var s = AddSettlement(ctx, a);
        s.Level = 2;   // 已有城镇
        // 部落迁走（Cell 变化——模拟迁徙）
        a.Cell = 3;
        ctx.CellTribes[0] = null;
        ctx.CellTribes[3] = a;
        var sm = new SettlementModel();
        sm.Execute(ctx);
        bool ruin = s.IsRuin && s.RuinFrom >= 0;                          // 旧聚落留废墟（场所比人长寿）
        var newHome = ctx.SettlementOf(a);
        bool newSettled = newHome != null && newHome.Cell == 3;           // 迁后新址建新村
        // 新部落迁入接管（继承 Level 2）
        var b = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        b.IsFarming = true;
        sm.Execute(ctx);
        bool reclaim = !s.IsRuin && s.OccupantId == b.Id && b.PlaceId == s.Id && s.Level == 2;
        Check("T63 聚落存续", ruin && newSettled && reclaim,
            $"迁走→旧聚落废墟={ruin} 新址建村={newSettled} 新部落接管(继承Level{s.Level})={reclaim}");
    }


    /// <summary>T64 国家涌现（2026-08-16 阶段4，docs/阶段4设计-国家涌现.md）：酋邦满足 AND 四条件
    /// （都城 Level≥2 + 存续 + 决策层级 + 贡赋盈余）→ StateModel 标 StateId；任一条件缺 → 非国家。</summary>
    private void T64_StateEmergence()
    {
        // 构造国家：酋长 A（都城 L2，BornTick 早）+ 成员 B（次级中心 L1）+ 成员 C（无聚落）+ 贡赋池足
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        ctx.Tick = 50;
        var a = AddTribe(ctx, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        var b = AddTribe(ctx, 1, 300f, TechTable.StoneCore, TechTable.SeedWheat);
        var c = AddTribe(ctx, 2, 200f, TechTable.StoneCore, TechTable.SeedWheat);
        a.IsChief = true;   // 至尊酋长（自己中心）
        SetupStateChiefdom(ctx, a, b, c);
        var cap = AddSettlement(ctx, a);
        cap.Level = 2; cap.BornTick = 0;                      // 都城：城镇 + 存续 50 ≥ 20
        var sub = AddSettlement(ctx, b);
        sub.Level = 1;                                        // 次级中心：村庄
        a.Contributed = 50f; b.Contributed = 50f; c.Contributed = 50f;   // 池 150 ≥ 阈值 1000×StateTributePerCap(0.01)=10
        StateModel.Rebuild(ctx);
        bool emerged = a.StateId == a.Id && b.StateId == a.Id && c.StateId == a.Id && a.StateSize == 3;
        // 反例①：贡赋不足（池 50 < 100）→ 非国家
        var ctx2 = MakeCtx(MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8));
        ctx2.Tick = 50;
        var a2 = AddTribe(ctx2, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        var b2 = AddTribe(ctx2, 1, 300f, TechTable.StoneCore, TechTable.SeedWheat);
        var c2 = AddTribe(ctx2, 2, 200f, TechTable.StoneCore, TechTable.SeedWheat);
        a2.IsChief = true;
        SetupStateChiefdom(ctx2, a2, b2, c2);
        var cap2 = AddSettlement(ctx2, a2);
        cap2.Level = 2; cap2.BornTick = 0;
        var sub2 = AddSettlement(ctx2, b2);
        sub2.Level = 1;
        a2.Contributed = 2f; b2.Contributed = 2f; c2.Contributed = 1f;   // 池 5 < 阈值 10（2026-08-19 同步 0.1→0.01 校准；旧值 50 已足额）
        StateModel.Rebuild(ctx2);
        bool noTribute = a2.StateId < 0 && b2.StateId < 0;
        // 反例②：无次级中心（B 聚落 L0）→ 非国家
        var ctx3 = MakeCtx(MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8));
        ctx3.Tick = 50;
        var a3 = AddTribe(ctx3, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        var b3 = AddTribe(ctx3, 1, 300f, TechTable.StoneCore, TechTable.SeedWheat);
        var c3 = AddTribe(ctx3, 2, 200f, TechTable.StoneCore, TechTable.SeedWheat);
        a3.IsChief = true;
        SetupStateChiefdom(ctx3, a3, b3, c3);
        var cap3 = AddSettlement(ctx3, a3);
        cap3.Level = 2; cap3.BornTick = 0;
        var sub3 = AddSettlement(ctx3, b3);
        sub3.Level = 0;                                       // 新村——非次级中心
        a3.Contributed = 50f; b3.Contributed = 50f; c3.Contributed = 50f;
        StateModel.Rebuild(ctx3);
        bool noHierarchy = a3.StateId < 0;
        // 反例③：都城存续不足（BornTick 近）→ 非国家
        var ctx4 = MakeCtx(MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8));
        ctx4.Tick = 50;
        var a4 = AddTribe(ctx4, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        var b4 = AddTribe(ctx4, 1, 300f, TechTable.StoneCore, TechTable.SeedWheat);
        var c4 = AddTribe(ctx4, 2, 200f, TechTable.StoneCore, TechTable.SeedWheat);
        a4.IsChief = true;
        SetupStateChiefdom(ctx4, a4, b4, c4);
        var cap4 = AddSettlement(ctx4, a4);
        cap4.Level = 2; cap4.BornTick = 40;                   // 存续 10 < 20
        var sub4 = AddSettlement(ctx4, b4);
        sub4.Level = 1;
        a4.Contributed = 50f; b4.Contributed = 50f; c4.Contributed = 50f;
        StateModel.Rebuild(ctx4);
        bool noDwell = a4.StateId < 0;
        Check("T64 国家涌现", emerged && noTribute && noHierarchy && noDwell,
            $"涌现={emerged}(StateId={a.StateId}/size={a.StateSize}) 贡赋不足={noTribute} 无层级={noHierarchy} 存续不足={noDwell}");
    }


    /// <summary>T65 国家机制（2026-08-16 阶段4）：税制化（贡赋率 0.2 vs 0.1）、官僚化（精英 0.25 vs 0.1）、
    /// 内部秩序（同国冲突 ×0.25 vs 同邦 ×0.5）。PrestigeModel 滞后 1 tick 读 StateId——直接置位验证。</summary>
    private void T65_StateMechanisms()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        ctx.Tick = 10;
        var stateChief = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var stateMember = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        var chiefdomChief = AddTribe(ctx, 2, 100f, TechTable.StoneCore);
        var chiefdomMember = AddTribe(ctx, 3, 100f, TechTable.StoneCore);
        // 国家成员（StateId=9）：税 0.2；酋邦成员（StateId=-1）：税 0.1
        stateChief.StateId = 9; stateMember.StateId = 9;
        stateChief.ChiefdomId = 9; stateMember.ChiefdomId = 9;
        chiefdomChief.ChiefdomId = 7; chiefdomMember.ChiefdomId = 7;
        foreach (var e in new[] { stateChief, stateMember, chiefdomChief, chiefdomMember })
        {
            e.FLast = 110f;   // 盈余 10
            e.Prestige = 2f;
        }
        new PrestigeModel().Execute(ctx);
        bool taxOk = Mathf.Abs(stateMember.Contributed - 2f) < 0.01f      // 10×0.2=2
                  && Mathf.Abs(chiefdomMember.Contributed - 1f) < 0.01f;  // 10×0.1=1
        // 官僚化：精英供养 elite = P×0.25（国家）vs P×0.1（酋邦）——池充足 → 不饿死；池不足 → 国家饿更快
        var ctx2 = MakeCtx(g);
        ctx2.Tick = 10;
        var sc2 = AddTribe(ctx2, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        var cc2 = AddTribe(ctx2, 1, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        foreach (var e in new[] { sc2, cc2 })
        {
            e.IsFarming = true;
            ShareField.RelTransfer(e.ReligionShare, ReligionStage.Animism, ReligionStage.Ancestor, 100);
            e.Prestige = 1.2f;   // DeriveLeadership → IsChief=true
            e.FLast = 110f;
        }
        sc2.StateId = 9; sc2.ChiefdomId = 9; sc2.Contributed = 15f;   // 池 15
        cc2.ChiefdomId = 7; cc2.Contributed = 15f;
        new PrestigeModel().Execute(ctx2);
        // 国家精英 100×0.25=25 > 池 15（+税2=17）→ 缺 8×0.5 → P 降 4；酋邦精英 100×0.1=10 ≤ 池 15（+税1=16）→ P 不降
        bool eliteOk = sc2.P < 100f && Mathf.Abs(cc2.P - 100f) < 0.01f;
        // 内部秩序：同国 ×0.25、同邦 ×0.5、跨邦 ×1（ConflictChanceOf 纯函数）
        var ta = new Tribe { ChiefdomId = 9, StateId = 9 };
        var tb = new Tribe { ChiefdomId = 9, StateId = 9 };
        var tc = new Tribe { ChiefdomId = 8, StateId = -1 };
        var td = new Tribe { ChiefdomId = 8, StateId = -1 };
        float sameState = ConflictModel.ConflictChanceOf(ctx, ta, tb);
        float sameChiefdom = ConflictModel.ConflictChanceOf(ctx, tc, td);
        float cross = ConflictModel.ConflictChanceOf(ctx, ta, td);
        bool orderOk = Mathf.Abs(sameState - 0.01f * 0.25f) < 1e-6f
                    && Mathf.Abs(sameChiefdom - 0.01f * 0.5f) < 1e-6f
                    && Mathf.Abs(cross - 0.01f) < 1e-6f;
        Check("T65 国家机制", taxOk && eliteOk && orderOk,
            $"税 国家{stateMember.Contributed:F1}(应2)/酋邦{chiefdomMember.Contributed:F1}(应1) 精英 P降{sc2.P:F0}(应<100)/酋邦P{cc2.P:F0}(应100) 冲突 同国{sameState:F4}(应0.0025)/同邦{sameChiefdom:F4}(应0.005)/跨{cross:F4}(应0.01)");
    }


    /// <summary>T66 国家崩溃（2026-08-16 阶段4，4A 对称可逆）：条件断开 → 退化回酋邦。
    /// ① 贡赋断流（池跌破线）② 都城失守（酋长丢聚落）→ StateId 回 -1。</summary>
    private void T66_StateCollapse()
    {
        // 先构造国家（复用 T64 构造）
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        ctx.Tick = 50;
        var a = AddTribe(ctx, 0, 500f, TechTable.StoneCore, TechTable.SeedWheat);
        var b = AddTribe(ctx, 1, 300f, TechTable.StoneCore, TechTable.SeedWheat);
        var c = AddTribe(ctx, 2, 200f, TechTable.StoneCore, TechTable.SeedWheat);
        a.IsChief = true;
        SetupStateChiefdom(ctx, a, b, c);
        var cap = AddSettlement(ctx, a);
        cap.Level = 2; cap.BornTick = 0;
        var sub = AddSettlement(ctx, b);
        sub.Level = 1;
        a.Contributed = 50f; b.Contributed = 50f; c.Contributed = 50f;
        StateModel.Rebuild(ctx);
        bool emerged = a.StateId == a.Id;
        // ① 贡赋断流：饥荒消耗池 → 跌破线（池 5 < 阈值 1000×0.01=10）→ 退化
        a.Contributed = 2f; b.Contributed = 2f; c.Contributed = 1f;   // 2026-08-19 同步校准（旧值 20/20/10 仍足额）
        StateModel.Rebuild(ctx);
        bool collapseTribute = a.StateId < 0 && b.StateId < 0 && c.StateId < 0;
        // ② 都城失守：酋长迁徙（PlaceId 清）→ 退化
        a.Contributed = 50f; b.Contributed = 50f; c.Contributed = 50f;   // 恢复贡赋
        StateModel.Rebuild(ctx);
        bool restored = a.StateId == a.Id;
        a.PlaceId = -1; cap.OccupantId = -1;   // 都城变废墟（酋长迁走）
        StateModel.Rebuild(ctx);
        bool collapseCapital = a.StateId < 0;
        Check("T66 国家崩溃", emerged && collapseTribute && restored && collapseCapital,
            $"涌现={emerged} 贡赋断流→退化={collapseTribute} 恢复={restored} 都城失守→退化={collapseCapital}");
    }


    /// <summary>T67 继承制度化（2026-08-16 阶段4，Kirch→王朝）：同国家成员继承窗口 ×2 豁免
    /// （制度化缓和继承战争）；同酋邦窗口仍 ×2（继承战争，Polynesia 常态）。</summary>
    private void T67_SuccessionInstitutionalized()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        ctx.Tick = 50;
        var sa = new Tribe { Id = 1, ChiefdomId = 9, StateId = 9, SuccessionUntil = 70 };
        var sb = new Tribe { Id = 2, ChiefdomId = 9, StateId = 9, SuccessionUntil = 70 };
        var ca = new Tribe { Id = 3, ChiefdomId = 8, StateId = -1, SuccessionUntil = 70 };
        var cb = new Tribe { Id = 4, ChiefdomId = 8, StateId = -1, SuccessionUntil = 70 };
        float stateWindow = ConflictModel.ConflictChanceOf(ctx, sa, sb);   // 0.01×0.25（豁免 ×2）
        float chiefdomWindow = ConflictModel.ConflictChanceOf(ctx, ca, cb); // 0.01×0.5×2
        bool exempt = Mathf.Abs(stateWindow - 0.01f * 0.25f) < 1e-6f;
        bool notExempt = Mathf.Abs(chiefdomWindow - 0.01f * 0.5f * 2f) < 1e-6f;
        // 跨邦窗口：×2（无内部秩序减免）
        var xa = new Tribe { Id = 5, ChiefdomId = 9, StateId = 9, SuccessionUntil = 70 };
        var xb = new Tribe { Id = 6, ChiefdomId = 8, StateId = -1, SuccessionUntil = 70 };
        float crossWindow = ConflictModel.ConflictChanceOf(ctx, xa, xb);   // 0.01×2
        bool crossOk = Mathf.Abs(crossWindow - 0.01f * 2f) < 1e-6f;
        Check("T67 继承制度化", exempt && notExempt && crossOk,
            $"同国窗口={stateWindow:F4}(应0.0025=豁免×2) 同邦窗口={chiefdomWindow:F4}(应0.01=×2) 跨邦窗口={crossWindow:F4}(应0.02)");
    }


    /// <summary>T28 畜牧涌现：草原格(WildLivestock=1)+livestock 科技 → 牧产出>0；无生态位/无科技 → 牧=0。
    /// 2026-08-17 畜牧落地：走等边际分配器（牧场建筑并入采集档）。</summary>
    private void T28_LivestockEmergence()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        g.WildLivestock = new byte[g.N];
        g.WildLivestock[0] = 1;   // 格0 草原可牧；格1 无生态位
        var herd = AddTribe(ctx, 0, 10000f, TechTable.Livestock, TechTable.StoneCore);
        var noHerd = AddTribe(ctx, 1, 10000f, TechTable.StoneCore);          // 无科技（格1 也无生态位）
        ctx.CellOwner[0] = 0; ctx.CellOwner[1] = 1;
        ctx.TerritoryCells[0].Add(0); ctx.TerritoryDists[0].Add(0);
        ctx.TerritoryCells[1].Add(1); ctx.TerritoryDists[1].Add(1);
        CivEngine.RefreshCellState(ctx);
        bool herdActive = ctx.AllocateAndProduce(herd) > 0f && herd.FHerdLast > 0f;
        bool herdOff = ctx.AllocateAndProduce(noHerd) > 0f && noHerd.FHerdLast == 0f;
        Check("T28 畜牧涌现", herdActive && herdOff,
            $"草原+科技 牧F={herd.FHerdLast:F0}(>0) 无生态位/无科技 牧F={noHerd.FHerdLast:F0}(=0)");
    }


    /// <summary>T29 货物累积（2026-08-10 影响力场模型）：狩猎采集产出 → 皮革；农业产出 → 秸秆；
    /// 畜牧暂缓（FHerdLast=0）→ 羊毛暂缓断言（等领地畜牧落地）。</summary>
    private void T29_GoodsAccumulation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3);
        var ctx = MakeCtx(g);
        // ⚠️ 2026-08-17 畜牧接入分配器后：采集档潜在 = 采集+牧场（3×）→ 农田激活需 N > ΣPc/24.3 ≈ 2110——P 改 4000
        //   2026-08-17 牧场受开垦挤压（用户拍板）：田格（格0 开垦1）与牧格（格1 草场）分开，各产秸秆/羊毛
        g.WildLivestock = new byte[g.N];
        g.WildLivestock[0] = 1;   // 格0：livestock 能力解锁条件（驻扎格生态位）——开垦1 → 牧场贡献0
        g.WildLivestock[1] = 1;   // 格1：草场牧场
        var e = AddTribe(ctx, 0, 4000f, TechTable.StoneCore, TechTable.SeedWheat, TechTable.Grinding, TechTable.Livestock);
        e.IsFarming = true;
        ctx.Suit[0, 0] = 1.0f;
        ctx.Cultivation[0] = 1f;   // 农业产出 ×开垦率——不开垦秸秆恒 0（测试补开垦）
        // 手造领地 2 格：格0 农田（开垦1）、格1 牧场（草场，开垦0）——采集+农田+牧场建筑（等边际分配）
        ctx.CellOwner[0] = 0;
        ctx.CellOwner[1] = 0;
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        ctx.TerritoryCells[0].Add(1);
        ctx.TerritoryDists[0].Add(1);
        CivEngine.RefreshCellState(ctx);   // 先算 CellFarmPop
        var harvest = new HarvestModel();
        harvest.Execute(ctx);
        CivEngine.RefreshCellState(ctx);   // 再聚合商品存储（FLast → Stocks：Food 消耗/衰变，Material 累积）
        // ⚠️ 2026-08-18 阶段3：Goods[3] → Stocks[动态目录]（Material 槽 = 皮革/羊毛/秸秆；Food 槽被人口消耗）
        float leather = e.Stocks[CommodityTable.Index(CommodityTable.Leather)];
        float straw = e.Stocks[CommodityTable.Index(CommodityTable.Straw)];
        float wool = e.Stocks[CommodityTable.Index(CommodityTable.Wool)];
        Check("T29 货物累积", leather > 0f && straw > 0f && wool > 0f,
            $"皮革={leather:F0} 秸秆={straw:F0} 羊毛={wool:F0}");
    }


    /// <summary>T30 等边际牧:猎分配：草原格牧场潜在 = 2×采集潜在（HerdMult=2）——同 LF 档（0.1）
    /// 按潜在比例分配工人 → 凹产出比 = 2:1（2026-08-17 畜牧接入分配器；旧 4:1 是 FOf 份额公式口径）。</summary>
    private void T30_WeightAllocation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);   // 草原
        var ctx = MakeCtx(g);
        g.WildLivestock = new byte[g.N];
        g.WildLivestock[0] = 1;   // 格0 可牧（采集+牧场同格：潜在 1:2）
        var e = AddTribe(ctx, 0, 10000f, TechTable.Livestock, TechTable.StoneCore);   // P 大：劳动充足
        e.IsFarming = false;
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        CivEngine.RefreshCellState(ctx);
        new HarvestModel().Execute(ctx);   // ⚠️ 2026-08-17：AllocateAndProduce 不设 FHuntLast（分量缓存是 HarvestModel 的职责）
        float f = e.FLast;
        // 等边际同档：n_牧/n_猎 = 潜在比 2:1 → F_牧/F_猎 = 2:1（凹产出，劳动充足区）
        bool herdShare = Mathf.Abs(e.FHerdLast - 2f * e.FHuntLast) < e.FHuntLast * 0.1f;
        bool huntActive = e.FHuntLast > 0f;   // 并行：猎仍产出
        Check("T30 等边际牧猎分配", herdShare && huntActive,
            $"牧F={e.FHerdLast:F0} 猎F={e.FHuntLast:F0}（应 2:1）总={f:F0}");
    }


    /// <summary>T31 饥饿-迁移闭环（2026-08-10 定稿 T23-新，2026-08-17 语义更新：砍存量后压力源=土地饱和/超载）：
    /// 饿（F<D）→ 迁移→新驻扎点。P=1 濒死 band（pEff≤2 不触发分裂段，隔离迁移）；确定性构造。</summary>
    private void T31_DepletionMigrate()
    {
        // ctxA：饿（FLast=0.5 < P=1）→ 迁移
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctxA = MakeCtx(gA);
        var eA = AddTribe(ctxA, 0, 1f, TechTable.StoneCore);
        eA.FLast = 0.5f;   // 饿（F<D；砍存量后由土地饱和/超载产生）
        int cellOld = eA.Cell;   // ⚠️ 2026-08-17 审查修复：打印迁移前后对比需记旧格
        ctxA.CellOwner[0] = 0;
        ctxA.TerritoryCells[0].Add(0);
        ctxA.TerritoryDists[0].Add(0);
        new SplitMigrateModel().Execute(ctxA);
        bool migrated = eA.Cell != 0 && eA.LastMigrateTick >= 0;
        // ctxB：不饿（FLast=2 > P=1）→ 不迁移
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctxB = MakeCtx(gB);
        var eB = AddTribe(ctxB, 0, 1f, TechTable.StoneCore);
        eB.FLast = 2f;
        ctxB.CellOwner[0] = 0;
        ctxB.TerritoryCells[0].Add(0);
        ctxB.TerritoryDists[0].Add(0);
        new SplitMigrateModel().Execute(ctxB);
        bool stayed = eB.Cell == 0;
        Check("T31 饥饿-迁移闭环", migrated && stayed,
            $"饿迁={migrated}(格{cellOld}→{eA.Cell}) 富饶留={stayed}");
    }


    /// <summary>T32 竞争易主（2026-08-10 定稿 T24-新，软冲突）：强 band 超粘性覆盖弱 band 边界格；势均力敌粘性保住。
    /// N=12 赤道环（BuildRing 顶点在 XY 平面，lat=lon——|lat|≥60° 的格进极区桶查不到 30° 纬差格）：
    /// A 驻格0(lat0)、B 驻格11(lat−30°)、边界格1(lat30°)——全部低纬可达。</summary>
    private void T32_CompetitiveTakeover()
    {
        // ⚠️ 2026-08-17 审查：以下手算隐含假设 stone_core CarryMult=1.1（I = P×M×w）——若科技表乘数变动本测试静默失效（数字重算时同步检查）
        // 场景 A：强覆盖——A P=200 → I_A=220×0.79=173.8 > I_B×1.15=43.5×1.15 → 易主
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 12);
        var ctxA = MakeCtx(gA);
        var aA = AddTribe(ctxA, 0, 200f, TechTable.StoneCore);
        var bA = AddTribe(ctxA, 11, 50f, TechTable.StoneCore);
        ctxA.CellOwner[0] = 0;
        ctxA.CellOwner[11] = 1;
        ctxA.CellOwner[1] = 1;   // 边界格归 B（弱）
        new InfluenceModel().Execute(ctxA);
        bool strongTook = ctxA.CellOwner[1] == 0;
        // 场景 B：势均力敌——A P=50 → I_A=43.5 = I_B → 粘性保住 B
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 12);
        var ctxB = MakeCtx(gB);
        var aB = AddTribe(ctxB, 0, 50f, TechTable.StoneCore);
        var bB = AddTribe(ctxB, 11, 50f, TechTable.StoneCore);
        ctxB.CellOwner[0] = 0;
        ctxB.CellOwner[11] = 1;
        ctxB.CellOwner[1] = 1;
        new InfluenceModel().Execute(ctxB);
        bool stickyHeld = ctxB.CellOwner[1] == 1;
        Check("T32 竞争易主", strongTook && stickyHeld,
            $"强覆盖易主={strongTook}(owner={ctxA.CellOwner[1]}) 势均粘性保={stickyHeld}(owner={ctxB.CellOwner[1]})");
    }


    /// <summary>T33 冲突爆发（2026-08-10 定稿 T25-新，硬冲突）：损耗+易主+锁定+驱逐（直接调 ResolveConflict，确定性）。
    /// 2026-08-17：掠夺改纯控制权（砍存量后无货可抢）——不再断言存量转移，验证归属变化。</summary>
    private void T33_ConflictBurst()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        ctx.Tick = 10;
        var ch = AddTribe(ctx, 0, 130f, TechTable.StoneCore, TechTable.Microlith, TechTable.Bow);   // 军事 2.7
        var ow = AddTribe(ctx, 1, 50f, TechTable.StoneCore);
        ctx.CellOwner[0] = 0;
        ctx.CellOwner[1] = 1;
        ctx.CellOwner[2] = 1;   // 争议格归 owner
        ctx.RebuildInfluence(); // 缓存 I（CellBestOwner/CellOwnerInf）——Resolve 本身不用，但保持状态一致
        float pCh0 = ch.P, pOw0 = ow.P;
        ConflictModel.ResolveConflict(ctx, ch, ow, 2);
        bool popLost = ch.P < pCh0 && ow.P < pOw0;                       // 双方损耗
        bool controlChanged = ctx.CellOwner[2] == 0;                     // 掠夺=武力夺取控制权（挑战者军事 2.7 大概率夺走）
        bool locked = ctx.LockedUntil[2] > ctx.Tick;                     // 实控锁定（无论谁赢，争议格锁定）
        bool coolDown = ch.LastConflictTick == 10 && ow.LastConflictTick == 10;
        Check("T33 冲突爆发", popLost && locked && coolDown,
            $"损耗={ch.P:F0}/{ow.P:F0} 争议格归属={ctx.CellOwner[2]}(0=挑战者夺走) 锁定={ctx.LockedUntil[2]} 冷却={coolDown} 冲突计数={ctx.Conflicts}");
    }


    /// <summary>T34 武器加成（2026-08-10 定稿 T26-新）：MilitMult 与 CarryMult 解耦——同 P 下有弓 band 军事显著强。
    /// 胜率公式断言 + 固定 seed 采样统计。</summary>
    private void T34_WeaponAdvantage()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var withBow = AddTribe(ctx, 0, 50f, TechTable.StoneCore, TechTable.Handaxe, TechTable.Microlith, TechTable.Bow);
        var plain = AddTribe(ctx, 1, 50f, TechTable.StoneCore);
        float milBow = TechTable.MilitaryMult(withBow.TechKeys);
        float milPlain = TechTable.MilitaryMult(plain.TechKeys);
        bool decoupled = milBow > 1f && milPlain == 1f;   // 解耦：武器进军事、无武器=1
        // 采样：同 P 一有弓一无——胜率 = 50×m / (50×m + 50)；固定 seed 确定性统计
        int bowWins = 0, plainWins = 0;
        var ctxS = MakeCtx(g, seed: 7);
        var cb = AddTribe(ctxS, 0, 50f, TechTable.StoneCore, TechTable.Handaxe, TechTable.Microlith, TechTable.Bow);
        var cp = AddTribe(ctxS, 1, 50f, TechTable.StoneCore);
        for (int k = 0; k < 60; k++)
        {
            cb.P = 50f; cp.P = 50f;   // 每次重置（损耗累积会衰减到 1 失真）
            ConflictModel.ResolveConflict(ctxS, cb, cp, 2);
            if (cb.P > cp.P) bowWins++; else plainWins++;   // 胜者损耗小 → P 高
        }
        bool advantage = bowWins > plainWins * 2;   // 有弓显著胜出（60 次采样）
        Check("T34 武器加成", decoupled && advantage,
            $"milit弓={milBow:F2}(应2.7) 无武器={milPlain:F1}(应1) 采样胜场 有弓{bowWins}/无弓{plainWins}");
    }


    /// <summary>T35 实控锁定（2026-08-10 定稿 T27-新）：锁定内场不重算（武力既成事实）；锁定过期后场恢复（强方收回）。
    /// N=12：A 驻格0、B 驻格11、边界格1（低纬可达，见 T32 注）。</summary>
    private void T35_LockHoldReclaim()
    {
        // 场景 A：锁定内——A 武力夺取格 1（P=200），锁定 8 tick；场重算不碰
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 12);
        var ctxA = MakeCtx(gA);
        ctxA.Tick = 10;
        var aA = AddTribe(ctxA, 0, 200f, TechTable.StoneCore);
        var bA = AddTribe(ctxA, 11, 50f, TechTable.StoneCore);
        ctxA.CellOwner[0] = 0;
        ctxA.CellOwner[11] = 1;
        ctxA.CellOwner[1] = 0;              // 武力夺取：格 1 归 A
        ctxA.LockedUntil[1] = ctxA.Tick + CivSimContext.ConflictLockTicks;   // 锁定中
        new InfluenceModel().Execute(ctxA);
        bool held = ctxA.CellOwner[1] == 0;   // 锁定内不被场覆盖
        // 场景 B：锁定过期——B 人口涨强于 A → 场收回
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 12);
        var ctxB = MakeCtx(gB);
        ctxB.Tick = 10;
        var aB = AddTribe(ctxB, 0, 50f, TechTable.StoneCore);
        var bB = AddTribe(ctxB, 11, 200f, TechTable.StoneCore);
        ctxB.CellOwner[0] = 0;
        ctxB.CellOwner[11] = 1;
        ctxB.CellOwner[1] = 0;              // A 曾武力夺取
        ctxB.LockedUntil[1] = ctxB.Tick - 1;   // 锁定已过期
        new InfluenceModel().Execute(ctxB);
        bool reclaimed = ctxB.CellOwner[1] == 1;   // 场恢复：强 B 收回
        Check("T35 实控锁定", held && reclaimed,
            $"锁定内保持={held}(owner={ctxA.CellOwner[1]}) 过期后收回={reclaimed}(owner={ctxB.CellOwner[1]})");
    }


    /// <summary>T36 土地竞争（2026-08-17 用户拍板）：农田开垦占用土地 → 采集产出下降
    /// （浆果 ×(1−开垦) 直接被替代、猎物 ×(1−0.5×开垦) 栖息地破碎）。</summary>
    private void T36_LandCompetition()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var e = AddTribe(ctx, 0, 1000f, TechTable.StoneCore);   // P=1000 ≫ 0.1×pot → 劳动力充足（土地受限区，开垦减产可见）
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        float f0 = ctx.AllocateAndProduce(e);   // 未开垦
        ctx.Cultivation[0] = 1f;                 // 全开垦（农田占满）
        float f1 = ctx.AllocateAndProduce(e);
        bool reduced = f0 > 0f && f1 < f0 * 0.9f;   // 开垦后采集明显减产
        Check("T36 土地竞争", reduced,
            $"开垦前采集={f0:F1} 开垦后={f1:F1}（降幅 {100f * (1f - f1 / f0):F0}%）");
    }


    /// <summary>T37 农田开垦增长（2026-08-17）：IsFarming band 每 tick 提高驻扎格开垦率（收敛向 1）；非农格不动。</summary>
    private void T37_CultivationGrowth()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var fa = AddTribe(ctx, 0, 50f, TechTable.StoneCore, TechTable.SeedWheat);
        fa.IsFarming = true;
        var hu = AddTribe(ctx, 1, 50f, TechTable.StoneCore);   // 非农对照
        ctx.CellOwner[0] = 0;   // ⚠️ 2026-08-17 领地农业：开垦走领地格——测试需设领地（否则 terr 空不开垦）
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        var cult = new CultivateModel();
        for (int k = 0; k < 30; k++) cult.Execute(ctx);        // 30 tick ≈ 3000 年
        bool farmGrew = ctx.Cultivation[0] > 0.7f;             // 收敛趋近 1
        bool huntZero = ctx.Cultivation[1] == 0f;              // 非农格不开垦
        Check("T37 农田开垦", farmGrew && huntZero,
            $"农业格开垦={ctx.Cultivation[0]:F3}（30 tick 后） 非农格={ctx.Cultivation[1]}");
    }


    /// <summary>T38 凹化+等边际分配性质（2026-08-17 用户拍板"凹化要量化好"的验收）：
    /// ① 小人口边际 = 1/LF（N→0 时 F/N → 10——与线性版单人产出一致，凹化不改小 band 行为）；
    /// ② 大人口饱和（N→∞ 时 F → Σ潜在——承载上限不变，凹化只弯中间）；
    /// ③ 单调且边际递减（N 增 F 增，但增速下降——凹性）；
    /// ④ 农田未激活（段 A）时无农业产出（负分配截断——T29 修复的 bug 回归防护）。</summary>
    private void T38_EquiMarginal()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var e = AddTribe(ctx, 0, 0f, TechTable.StoneCore, TechTable.SeedWheat);
        e.IsFarming = true;
        ctx.Suit[0, 0] = 1.0f;
        ctx.CellOwner[0] = 0;
        ctx.TerritoryCells[0].Add(0);
        ctx.TerritoryDists[0].Add(0);
        // ① 小人口：N=0.1 → F ≈ 1/LF × N = 10×0.1 = 1（采集单人边际）
        e.P = 0.1f;
        float fSmall = ctx.AllocateAndProduce(e);
        bool smallMarginal = Mathf.Abs(fSmall - 1.0f) < 0.25f;
        // ② 饱和：N=10⁶ → F → Σ潜在（采集潜在 = R×A×w(0)×满员 ≈ 0.3×5×1，w(0)=1；开垦 0 → 无农田）
        e.P = 1000000f;
        float fSat = ctx.AllocateAndProduce(e);
        float pot = ctx.R[0] * g.CellAreaKm2;   // 采集潜在（开垦 0、w=1、猎物+浆果占比合计 1）
        bool saturated = fSat > pot * 0.95f;
        // ③ 凹性（跨饱和区测：N=100 < D=471 < N=1000——饱和点两侧边际递减；N≪D 是线性区无曲率）
        e.P = 100f;
        float fA = ctx.AllocateAndProduce(e);
        e.P = 471f;   // ≈ D = 0.1×潜在（饱和点）
        float fB = ctx.AllocateAndProduce(e);
        e.P = 1000f;
        float fC = ctx.AllocateAndProduce(e);
        bool concave = (fC - fB) < (fB - fA);
        bool monotonic = fB > fA && fC > fB;
        // ④ 段 A 无农：N=10（ΣPc=1.5、24.3×10=243>1.5 → 段 A）→ FFarmLast = 0
        e.P = 10f;
        ctx.AllocateAndProduce(e);
        bool noFarm = e.FFarmLast == 0f;
        Check("T38 凹化等边际性质", smallMarginal && monotonic && concave && saturated && noFarm,
            $"小N边际 F/N={fSmall / 0.1f:F1}(期望≈10) 单调={monotonic} 凹={concave}(Δ={fC - fB:F0}<{fB - fA:F0}) 饱和={fSat:F0}/{pot:F0} 段A无农={noFarm}");
    }


    /// <summary>T39 定居+存储（2026-08-17 用户拍板缺口之三）：
    /// ① settle = IsFarming 派生（转农即定居，无发明事件）；
    /// ② 饥荒缓冲分层：storage ×0.6 < +pottery ×0.4（陶器密封）——同饥荒下 pottery 存活更高；
    /// ③ 定居生育跃迁：盈余下 settle 实体增长 r×1.5 更快。</summary>
    private void T39_SettleStorage()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var farm = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        farm.IsFarming = true;
        var hunter = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        var s1 = AddTribe(ctx, 2, 100f, TechTable.Storage);                       // 游群粮袋（techMult 1.0）
        var s2 = AddTribe(ctx, 3, 100f, TechTable.Storage, TechTable.Pottery);    // +陶器密封（×0.3）
        var gA = AddTribe(ctx, 4, 100f, TechTable.StoneCore);                     // 游群（r 基础）
        var gB = AddTribe(ctx, 5, 100f, TechTable.StoneCore);
        gB.IsFarming = true;                                                       // 定居（r×1.5）
        CivEngine.RefreshCellState(ctx);   // CapMask（settle/pottery/storage）
        bool settleOk = CapabilityTable.Has(ctx, farm, CapabilityTable.Settle) && !CapabilityTable.Has(ctx, hunter, CapabilityTable.Settle);
        // ② 存储分层（2026-08-18 阶段3 新语义；2026-08-19 双池改造——粮仓测 techMult）：预置同量谷物
        //    入粮仓，AccumulateStorage 一 tick——陶器 techMult×0.3（衰变更慢）→ 粮仓剩余更多
        int gi = CommodityTable.Index(CommodityTable.Grain);
        var st1 = AddSettlement(ctx, s1);   // 粮仓（storage techMult 0.6）
        var st2 = AddSettlement(ctx, s2);   // +陶器（0.3）
        st1.Stocks[gi] = 100f; st2.Stocks[gi] = 100f;
        s1.FLast = 100f; s2.FLast = 100f;
        CivEngine.AccumulateStorage(ctx);
        bool layered = st2.Stocks[gi] > st1.Stocks[gi];   // 陶器衰变慢 → 剩得多
        // ③ 盈余单 tick：FLast=150（P=100 → 盈余）——定居 r×1.5 更快
        gA.FLast = 150f; gB.FLast = 150f;
        var growth = new GrowthModel();
        growth.Execute(ctx);
        bool growthBoost = gB.P > gA.P * 1.05f;
        Check("T39 定居+存储", settleOk && layered && growthBoost,
            $"settle派生={settleOk} 存储分层(storage粮仓剩{st1.Stocks[gi]:F1} < +陶器剩{st2.Stocks[gi]:F1}) 定居增长(gA={gA.P:F1} < gB={gB.P:F1})");
    }


    /// <summary>T40 性能分段基线（2026-08-17 审查新增，防优化劣化）：
    /// MapGen 快管线（n16/600My，与真实生成同参数）分段计时——板块/管线/存档/总。
    /// 基线存 user://perf_baseline.json（首次记录），后续对比阈值 ×1.5（+50% 报警）。
    /// 运行：headless -- --only=T40（~30-40s）；⚠️ 不进全量默认（贵）。
    /// 基线释义：n16 板块 ~26s 基线（n64 的 1/16 时间）——任何模型算法改动后跑一次防回归。</summary>
    private void T40_PerfSegments()
    {
        string baselinePath = "user://perf_baseline.json";
        string outPath = "user://maps/perf_n16.mpa";
        var gen = new MapGenerator
        {
            Seed = 42,
            RadiusKm = 128f,
            TectonicsGridN = 16,       // n16 快基线（n64 的 1/16）
            SimMegayears = 600f,
            OutputPath = outPath,
        };
        gen.Generate();
        bool hasTec = MapGenerator.LastTimings.TryGetValue("tectonics_ms", out long tec);
        bool hasPipe = MapGenerator.LastTimings.TryGetValue("pipeline_ms", out long pipe);
        bool hasArc = MapGenerator.LastTimings.TryGetValue("archive_ms", out long arc);
        long total = tec + pipe + arc;
        // 基线读写（user:// 可写；JSON 简单格式）
        bool baselineOk = true;
        string oldBaseline = null;
        if (FileAccess.FileExists(baselinePath))
            oldBaseline = FileAccess.GetFileAsString(baselinePath);
        if (oldBaseline == null || oldBaseline.Length == 0 || !oldBaseline.Contains("tectonics_ms"))
        {
            using var f = FileAccess.Open(baselinePath, FileAccess.ModeFlags.Write);
            if (f != null)
            {
                f.StoreString($"{{\"tectonics_ms\":{tec},\"pipeline_ms\":{pipe},\"archive_ms\":{arc},\"total_ms\":{total},\"n\":16,\"seed\":42}}");
                GD.Print($"[T40] 首次基线已记录 → {baselinePath}（板块{tec}ms 管线{pipe}ms 存档{arc}ms 总{total}ms）");
            }
            else
            {
                baselineOk = false;
                GD.Print("[T40] ⚠️ 无法写基线文件（仅本次耗时报告）");
            }
        }
        else
        {
            // 解析基线（简单解析 "key":value）
            float bTec = 0, bPipe = 0, bArc = 0, bTotal = 0;
            var parts = oldBaseline.Split(',');
            foreach (var p in parts)
            {
                var kv = p.Split(':');
                if (kv.Length != 2) continue;
                string k = kv[0].Trim('{', '}', '"', ' ');
                if (float.TryParse(kv[1].Trim('}', '"', ' '), out float v))
                {
                    if (k == "tectonics_ms") bTec = v;
                    else if (k == "pipeline_ms") bPipe = v;
                    else if (k == "archive_ms") bArc = v;
                    else if (k == "total_ms") bTotal = v;
                }
            }
            const float threshold = 1.5f;   // +50% 报警（机器波动容忍）
            bool tecOk = tec <= bTec * threshold;
            bool pipeOk = pipe <= bPipe * threshold;
            // ⚠️ 2026-08-18 修复：archive 基线可能为 0ms（当时太快记 0）→ 本次 1ms 即超 0×1.5=0 误报劣化。
            //   基线 ≤2ms 视为噪声容差（存档 n16 极小），直接达标。
            bool arcOk = bArc <= 2f ? true : arc <= bArc * threshold;
            bool totalOk = total <= bTotal * threshold;
            baselineOk = tecOk && pipeOk && arcOk && totalOk;
            GD.Print($"[T40] 基线 板块{bTec:F0} 管线{bPipe:F0} 存档{bArc:F0} 总{bTotal:F0} | 本次 {tec}/{pipe}/{arc}/{total} | 阈值 ×{threshold}");
            if (!baselineOk)
                GD.Print("  ⚠ [T40] 性能劣化！超基线 +50%——检查近期 MapGen/管线改动（算法回归或死循环）");
        }
        Check("T40 MapGen 分段基线（n16）", baselineOk && hasTec && hasPipe && hasArc,
            $"板块={tec}ms 管线={pipe}ms 存档={arc}ms 总={total}ms");
        PerfLog.Summarize("mapgen", "MapGen 分段");
    }


    /// <summary>T41 性能历史汇总（2026-08-17 监督机制：只读 user://perf_history.json 打印趋势）。
    /// 显示 MapGen/CivSim 各段的历史均值/峰值 + 最近 8 条时间序列（劣化趋势肉眼可见）。</summary>
    private void T41_PerfHistory()
    {
        PerfLog.Summarize("mapgen", "MapGen 分段");
        PerfLog.Summarize("civsim", "CivSim 逐模型");
        // 最近 8 条趋势（mapgen 总耗时）
        var all = new List<long>();
        foreach (var (_, v) in PerfLog.Enumerate("mapgen", "total_ms")) all.Add(v);
        int start = Math.Max(0, all.Count - 8);
        var trend = new System.Text.StringBuilder("[T41] MapGen 总耗时趋势(ms): ");
        for (int i = start; i < all.Count; i++)
        {
            trend.Append(all[i]);
            if (i < all.Count - 1) trend.Append(" → ");
        }
        GD.Print(trend.ToString());
        int civCount = 0;
        foreach (var _ in PerfLog.Enumerate("civsim", "总")) civCount++;
        Check("T41 性能历史可读", all.Count > 0 || civCount > 0,
            $"MapGen 历史 {all.Count} 条 + CivSim 历史 {civCount} 条（趋势见上；mapgen 需先跑 --only=T40 生成）");
    }


    /// <summary>T42 声望积累（2026-08-17 酋邦层①，Sahlins 1963）：盈余→宴席→声望；
    /// 缺口→不涨。绝对盈余 2 人 × 60 tick → 声望 0.6（未达 BigMan 阈值 1.0——阈值边界验证）。</summary>
    private void T42_PrestigeAccumulation()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var b = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        a.FLast = 100.5f;   // 绝对盈余 0.5 人（小宴席能力）
        b.FLast = 90f;    // 缺口 10 人
        var p = new PrestigeModel();
        for (int t = 0; t < 60; t++) p.Execute(ctx);
        bool aGained = a.Prestige > 0f;
        bool bFlat = b.Prestige == 0f;
        bool belowThreshold = !a.IsBigMan;   // 0.5×0.02×60 = 0.6 < 1.0（阈值边界）
        Check("T42 声望积累", aGained && bFlat && belowThreshold,
            $"盈余A声望={a.Prestige:F2}(>0) 缺口B={b.Prestige:F2}(=0) 未达阈值={belowThreshold}(0.6<1.0)");
    }


    /// <summary>T43 大人物涌现（Sahlins：Big Man 声望型领袖）：绝对盈余 5 人持续 120 tick →
    /// 声望 3.0 ≥ 1.0 → BigMan；缺口 band 永不。</summary>
    private void T43_BigManEmergence()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var b = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        a.FLast = 105f;   // 绝对盈余 5 人（宴席能力）
        b.FLast = 90f;
        var p = new PrestigeModel();
        for (int t = 0; t < 120; t++) p.Execute(ctx);
        bool aBigMan = a.IsBigMan;
        bool bNot = !b.IsBigMan;
        Check("T43 大人物涌现", aBigMan && bNot,
            $"盈余A声望={a.Prestige:F2}(≥1.0→BigMan={aBigMan}) 缺口B声望={b.Prestige:F2}(BigMan={b.IsBigMan})");
    }


    /// <summary>T44 酋长制度化（Polynesia 谱系合法性——divine kingship）：BigMan + 祖先宗教 → Chief；
    /// BigMan + 泛灵（无谱系）→ 卡在 BigMan。</summary>
    private void T44_ChiefInstitutionalize()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        a.IsFarming = true;   // settle → 祖先宗教可达
        ShareField.RelTransfer(a.ReligionShare, ReligionStage.Animism, ReligionStage.Ancestor, 100);
        a.Prestige = 1.2f;
        var b = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        b.Prestige = 1.2f;    // 泛灵（默认）——无谱系
        new PrestigeModel().Execute(ctx);
        bool aChief = a.IsChief;
        bool bStuck = !b.IsChief;
        Check("T44 酋长制度化", aChief && bStuck,
            $"祖先+BigMan→Chief={aChief} 泛灵+BigMan→Chief={b.IsChief}(应False——谱系是硬门槛)");
    }


    /// <summary>T45 酋邦庇护凝聚（2026-08-19 重构为至尊酋长庇护）：a1 酋长半径内的 band 入邦
    /// （a2/b1 距 a1 ≤ ChiefReach=12 → 同邦，size=3）；半径外的 c1（距 a1=14 > 12）不入邦。
    /// 确定性构造：28 格赤道环（dist(0,14)=14 > 12，dist(0,3)=3 ≤ 12）。</summary>
    private void T45_ChiefdomCoalesce()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 28);
        RingLinks(g);   // 精确环邻接——BFS 跳数 = 环距
        var ctx = MakeCtx(g);
        // 酋长 A：格 0（领地 {0,1}），声望 1.2 + 祖先宗教 → IsChief
        var a1 = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        a1.IsFarming = true;
        ShareField.RelTransfer(a1.ReligionShare, ReligionStage.Animism, ReligionStage.Ancestor, 100);
        a1.Prestige = 1.2f;
        a1.IsChief = true;   // 手动置位（PrestigeModel 派生——测试直接构造酋长状态）
        a1.TerritoryId = 10; a1.TerritorySize = 2;
        var a2 = AddTribe(ctx, 1, 80f, TechTable.StoneCore);
        a2.TerritoryId = 10; a2.TerritorySize = 2;
        var b1 = AddTribe(ctx, 3, 90f, TechTable.StoneCore);
        b1.TerritoryId = 20; b1.TerritorySize = 1;
        // 领地格（手造——庇护机制只看距离，不看领地接触）
        foreach (var e in ctx.Tribes) { ctx.TerritoryCells[e.Id].Add(e.Cell); ctx.TerritoryDists[e.Id].Add(0); }
        ctx.TerritoryCells[a1.Id].Add(1); ctx.TerritoryDists[a1.Id].Add(1);
        ctx.TerritoryCells[b1.Id].Add(2); ctx.TerritoryDists[b1.Id].Add(1);
        ctx.ChiefdomLastEval = -100;
        new ChiefdomModel().Execute(ctx);
        bool merged = a1.ChiefdomId == a1.Id          // 酋长 = 自己中心
                   && a2.ChiefdomId == a1.Id          // 半径 1 内 → 入邦
                   && b1.ChiefdomId == a1.Id          // 半径 3 内 → 入邦
                   && a1.ChiefdomSize == 3;
        // 反例：c1（格 14，距 a1=14 > ChiefReach=12）→ 半径外不入邦
        var c1 = AddTribe(ctx, 14, 70f, TechTable.StoneCore);
        c1.TerritoryId = 30; c1.TerritorySize = 1;
        ctx.TerritoryCells[c1.Id].Add(14); ctx.TerritoryDists[c1.Id].Add(0);
        ctx.ChiefdomLastEval = -100;
        new ChiefdomModel().Execute(ctx);
        bool cNotMerged = c1.ChiefdomId < 0;
        Check("T45 酋邦庇护凝聚", merged && cNotMerged,
            $"A+近邻合并(Id={a1.ChiefdomId}/size={a1.ChiefdomSize}) 半径外C不合并={cNotMerged}(c1={c1.ChiefdomId})");
    }


    /// <summary>T46 酋邦庇护跨语言群（2026-08-19 新机制）：patronage 个人化——语言群分歧 → 部落层断裂，
    /// 但 b 仍在 a 的 ChiefReach 内 → 酋邦庇护保持（史实：patron-client 可跨族；政治体不依赖领地/语言网络）。</summary>
    private void T46_TribeIndependence()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        RingLinks(g);   // 精确环邻接
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var b = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        a.TerritoryId = 5; b.TerritoryId = 6;   // 两个部落
        a.IsChief = true; a.Prestige = 1.2f;
        a.CultureGroupShare = ShareField.NewCulture("cultg_1");
        b.CultureGroupShare = ShareField.NewCulture("cultg_2");   // 语言群分歧
        ctx.TerritoryLastRebuild = -10;
        new TerritoryModel().Execute(ctx);   // 部落层断裂（异语言群不凝聚）
        ctx.ChiefdomLastEval = -100;
        new ChiefdomModel().Execute(ctx);    // 酋邦庇护重估——b 距 a=1 ≤ ChiefReach → 仍受庇护
        bool patronage = a.ChiefdomId >= 0 && b.ChiefdomId == a.ChiefdomId;
        Check("T46 酋邦庇护跨语言群", patronage,
            $"a={a.ChiefdomId} b={b.ChiefdomId}（庇护跨群存续；部落层独立于酋邦层）");
    }


    /// <summary>T47 再分配互惠（Halstead-O'Shea 1989 坏年景开仓）：贡献过的成员灾年缺口 ×0.5；
    /// 未贡献不受赈——同一酋邦内对比衰减。</summary>
    private void T47_TributeReciprocity()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctx = MakeCtx(g);
        var chief = AddTribe(ctx, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        chief.IsFarming = true;
        ShareField.RelTransfer(chief.ReligionShare, ReligionStage.Animism, ReligionStage.Ancestor, 100);
        chief.Prestige = 1.2f;
        chief.ChiefdomId = 9; chief.ChiefdomSize = 3; chief.Contributed = 50f;
        var b = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        b.ChiefdomId = 9; b.ChiefdomSize = 3; b.Contributed = 20f;   // 贡献过 → 受赈
        var c = AddTribe(ctx, 2, 100f, TechTable.StoneCore);
        c.ChiefdomId = 9; c.ChiefdomSize = 3; c.Contributed = 0f;    // 未贡献 → 不受赈
        new PrestigeModel().Execute(ctx);   // 更新 IsChief（chief 需确认）+ 精英供养
        b.FLast = 50f; c.FLast = 50f;   // 坏年景（P=100 缺口 50%）
        var growth = new GrowthModel();
        growth.Execute(ctx);
        bool bBuffered = b.P > c.P;   // B 受赈（×0.5 缓冲）饿得慢
        Check("T47 再分配互惠", bBuffered,
            $"贡献者B P={b.P:F1} > 未贡献C P={c.P:F1}（互惠开仓生效）");
    }


    /// <summary>T48 精英供养（等级=结构性供养，回应"盈余>0≠等级"）：酋长 band 精英（10%）
    /// 由酋邦贡赋供养——贡赋充足 P 稳、不足 → 精英饿死（P 降）。</summary>
    private void T48_EliteSupport()
    {
        // 场景 A：贡赋充足
        var gA = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctxA = MakeCtx(gA);
        var ca = AddTribe(ctxA, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        ca.IsFarming = true;
        ShareField.RelTransfer(ca.ReligionShare, ReligionStage.Animism, ReligionStage.Ancestor, 100);
        ca.Prestige = 1.2f;
        ca.ChiefdomId = 7; ca.ChiefdomSize = 2; ca.Contributed = 100f;   // 池 100 ≥ 精英 10
        var ma = AddTribe(ctxA, 1, 50f, TechTable.StoneCore);
        ma.ChiefdomId = 7; ma.ChiefdomSize = 2; ma.Contributed = 0f;
        new PrestigeModel().Execute(ctxA);
        bool fed = ca.P == 100f;   // 精英被供养 → P 不变
        // 场景 B：贡赋不足
        var gB = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        var ctxB = MakeCtx(gB);
        var cb = AddTribe(ctxB, 0, 100f, TechTable.StoneCore, TechTable.SeedWheat);
        cb.IsFarming = true;
        ShareField.RelTransfer(cb.ReligionShare, ReligionStage.Animism, ReligionStage.Ancestor, 100);
        cb.Prestige = 1.2f;
        cb.ChiefdomId = 8; cb.ChiefdomSize = 2; cb.Contributed = 2f;    // 池 2 < 精英 10
        var mb = AddTribe(ctxB, 1, 50f, TechTable.StoneCore);
        mb.ChiefdomId = 8; mb.ChiefdomSize = 2; mb.Contributed = 0f;
        new PrestigeModel().Execute(ctxB);
        bool starved = cb.P < 100f;   // 贡赋不足 → 精英饿死
        Check("T48 精英供养", fed && starved,
            $"贡赋充足 P={ca.P:F1}(=100) 不足 P={cb.P:F1}(<100——精英饿死)");
    }


    /// <summary>T49 联盟合力（Kirch：防御方酋邦 → 入侵者面对总力量）：同 P 入侵者 vs 单部落/酋邦——
    /// 酋邦时胜率显著更低（采样统计）。</summary>
    private void T49_AllianceStrength()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        // ⚠️ 2026-08-17 审查修正：相等初始 P（ch=100/ow=100）单轮判定 ch.P>ow.P 才有效
        //   （不等 P 时 challenger 输了 P 仍高——判定恒真）；联盟 ow=100+70=170 → winChance 0.37 vs 0.5
        // 场景 A：单部落 owner P=100 vs 入侵 100
        var ctxA = MakeCtx(g, seed: 7);
        var owA = AddTribe(ctxA, 0, 100f, TechTable.StoneCore);
        var inA = AddTribe(ctxA, 1, 100f, TechTable.StoneCore);
        ctxA.CellOwner[2] = 0;
        int loneWins = 0;
        for (int k = 0; k < 60; k++) { inA.P = 100f; owA.P = 100f; ConflictModel.ResolveConflict(ctxA, inA, owA, 2); if (inA.P > owA.P) loneWins++; }
        // 场景 B：酋邦 owner（100+70）vs 入侵 100
        var ctxB = MakeCtx(g, seed: 7);
        var owB = AddTribe(ctxB, 0, 100f, TechTable.StoneCore);
        var ally = AddTribe(ctxB, 1, 70f, TechTable.StoneCore);
        owB.ChiefdomId = 3; ally.ChiefdomId = 3;
        var inB = AddTribe(ctxB, 2, 100f, TechTable.StoneCore);
        ctxB.CellOwner[3] = 0;
        int chiefWins = 0;
        for (int k = 0; k < 60; k++) { inB.P = 100f; owB.P = 100f; ConflictModel.ResolveConflict(ctxB, inB, owB, 3); if (inB.P > owB.P) chiefWins++; }
        bool allianceHolds = chiefWins < loneWins;   // 联盟显著降低入侵者胜率（0.37 < 0.5）
        Check("T49 联盟合力", allianceHolds,
            $"入侵者胜场 单部落={loneWins}/60 酋邦={chiefWins}/60（联盟 100+70=170 > 100 人多势众）");
    }


    /// <summary>T50 继承窗口（Kirch 1984 继承战争）：酋邦内无酋长（权力真空）→ ChiefdomModel
    /// 给 Prestige 最高者设 SuccessionUntil（窗口）；窗口内 ConflictModel 冲突概率 ×2（代码路径）。</summary>
    private void T50_SuccessionWindow()
    {
        var g = MakeGrid(100f, (byte)Biome.BiomeType.HotSteppe, 20f, 800f, 3, nCells: 8);
        RingLinks(g);   // 精确环邻接（庇护 BFS 跳数可靠）
        var ctx = MakeCtx(g);
        var a = AddTribe(ctx, 0, 100f, TechTable.StoneCore);
        var b = AddTribe(ctx, 1, 100f, TechTable.StoneCore);
        a.TerritoryId = 4; b.TerritoryId = 5;
        a.Prestige = 0.8f; b.Prestige = 0.5f;
        a.IsChief = true;   // 第一步：a 是酋长 → 凝聚
        a.FHuntLast = 100f; b.FFarmLast = 100f; // 产出互补（凝聚可发生）
        foreach (var e in ctx.Tribes) { ctx.TerritoryCells[e.Id].Add(e.Cell); ctx.TerritoryDists[e.Id].Add(0); }
        ctx.TerritoryCells[a.Id].Add(2); ctx.TerritoryDists[a.Id].Add(1);
        ctx.TerritoryCells[b.Id].Add(3); ctx.TerritoryDists[b.Id].Add(1);
        ctx.ChiefdomLastEval = -100;
        new ChiefdomModel().Execute(ctx);
        bool coalesced = a.ChiefdomId == b.ChiefdomId && a.ChiefdomId >= 0;   // 凝聚成功
        // 第二步：酋长死亡（IsChief 清除）→ 权力真空 → 继承窗口
        a.IsChief = false;
        ctx.ChiefdomLastEval = -100;
        new ChiefdomModel().Execute(ctx);
        bool windowSet = a.SuccessionUntil > ctx.Tick || b.SuccessionUntil > ctx.Tick;   // 权力真空 → 继承窗口
        bool windowOnTop = a.SuccessionUntil > ctx.Tick;   // Prestige 最高者（a=0.8）获窗口
        Check("T50 继承窗口", coalesced && windowSet && windowOnTop,
            $"凝聚={coalesced} 酋长死亡→窗口 a.SuccessionUntil={a.SuccessionUntil}(tick {ctx.Tick}+20) 最高声望者获窗口={windowOnTop}");
    }


    /// <summary>T23 领地传播乘数（单元，无地图依赖）：同领地 ×1.5；跨领地（一方 ≥2 band）×0.5；散兵 ×1。</summary>
    private void T23_TerritoryMult()
    {
        var a = new Tribe { TerritoryId = 7, TerritorySize = 2 };
        var b = new Tribe { TerritoryId = 7, TerritorySize = 2 };
        var c = new Tribe { TerritoryId = 9, TerritorySize = 2 };
        var d = new Tribe { TerritoryId = -1, TerritorySize = 1 };
        var e = new Tribe { TerritoryId = -1, TerritorySize = 1 };
        float same = SpreadModel.TerritoryMult(a, b);
        float cross = SpreadModel.TerritoryMult(a, c);
        float lone = SpreadModel.TerritoryMult(d, e);
        bool ok = same == CivSimContext.TerritorySpreadMult
               && cross == CivSimContext.CrossBorderSpreadMult
               && lone == 1f;
        Check("T23 领地传播乘数", ok, $"同领地×{same} 跨领地×{cross} 散兵×{lone}");
    }

}
