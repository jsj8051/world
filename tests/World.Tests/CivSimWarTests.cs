using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.CivSim;
using World.HexPlanet;
using World.LogicGrid;

using World.CivSim.Entities;
using World.CivSim.Mechanics.Military;
namespace World.Tests;

/// <summary>
/// 国家战争机制测试（阶段5 军事征服，docs/阶段5设计-军事征服.md）——"构造国家+战争状态 → 逐机制验证"。
/// 覆盖：会战（军力=ΣP×MilitMult，极端军力比 → 胜负不依赖 Rng，确定性）→ 败方损耗 →
///   停战（WarMaxTicks 超时移除）→ 吞并（碾压：胜场≥WarAnnexWins 且军力比≥WarPowerRatio；
///   成员 ConqueredBy 强制效忠、首领流放豁免、战利品入池、WarsAnnexed）→
///   朝贡（险胜：胜场≥WarTributeWins 且军力占优；TributeTo 模式 + 边境割地 CedeCells）→
///   朝贡转移（每 tick 贡赋、TributesLeft 归零移除）→ 宣战负向门（冷却）。
/// 约束同项目铁律：只 [Test]、无 SetUp/TestCaseSource/Pass；小网格（单位向量！）；无引擎调用。
/// 跳过（注释说明）：BattleChanceOf/CanDeclare 为 internal（无 InternalsVisibleTo）→ 经 Execute
///   行为间接验证；宣战**正门**概率门控 WarDeclareChance=0.002 → 不可确定性构造（需种子搜索），
///   只测负向门（冷却/池足等可构造项）。若需直接测 internal 纯函数可加 InternalsVisibleTo。
/// </summary>
public class CivSimWarTests
{
    private const int WarInterval = 5;

    /// <summary>星形 4 格网格：0 居中，邻 1/2/3（割地场景：败国格全部邻战胜国格）。Verts 单位化。</summary>
    private static GameGrid StarGrid()
    {
        Icosahedron.Subdivide(2, 6371f, out var verts, out _);
        int n = 4;
        var g = new GameGrid
        {
            N = n, GridN = 2, RadiusKm = 6371f,
            Verts = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(-1, 0, 0) },
            Elev = new float[n], Temp = new float[n], Precip = new float[n], Biome = new byte[n],
        };
        g.OverrideNeighbors(new[] { new[] { 1, 2, 3 }, new[] { 0 }, new[] { 0 }, new[] { 0 } });
        return g;
    }

    /// <summary>构造战争上下文：A 国（酋长 id0 + 成员 id1）、B 国（酋长 id2 + 成员 id3）。</summary>
    private static CivSimContext WarCtx(GameGrid grid)
    {
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellPolities = new Polity[grid.N],
            Polities = new List<Polity>(),
            Tick = 0,
            Rng = new DeterministicRandom(7),
            NextPolityId = 4,
            Habitations = new List<Habitation>(),
            Wars = new List<War>(),
            CellOwner = new int[grid.N],
            CellBestOwner = new int[grid.N],
            CellBestInf = new float[grid.N],
            CellOwnerInf = new float[grid.N],
            LockedUntil = new int[grid.N],
            BfsStamp = new int[grid.N],
            BfsStampValue = 1,
            R = new float[grid.N],
            CellF = new float[grid.N],
            CellPop = new float[grid.N],
            CellFarmPop = new float[grid.N],
            Cultivation = new float[grid.N],
        };
        ctx.ChiefdomCells = new List<int>[4];
        for (int i = 0; i < 4; i++) ctx.ChiefdomCells[i] = new List<int>();
        return ctx;
    }

    private static Polity AddPolity(CivSimContext ctx, int id, int cell, float p, float contributed, bool chief = false)
    {
        var e = new Polity
        {
            Id = id, Cell = cell, P = p, Contributed = contributed,
            ChiefdomId = id, StateId = chief ? id : -1, StateSize = chief ? 2 : 1,
            IsChief = chief, LastWarTick = -1,
        };
        ctx.Polities.Add(e);
        ctx.CellPolities[cell] = e;
        ctx.ChiefdomCells[id].Add(id);   // 酋长自身在其邦成员表
        if (chief)
            ctx.ChiefdomCells[id].Add(id == 0 ? 1 : 3);   // A: 成员1；B: 成员3
        return e;
    }

    private static War AddWar(CivSimContext ctx, int a, int b, int tick, int winsA = 0, int winsB = 0, int lastBattle = 0)
    {
        var w = new War { StateIdA = a, StateIdB = b, Defender = b, StartTick = tick, WinsA = winsA, WinsB = winsB, LastBattleTick = lastBattle };
        ctx.Wars.Add(w);
        return w;
    }

    // ═════════════════════════════════════════════════════════════════
    // 1. 会战：军力 = Σ P×MilitMult；极端军力比 → 胜负与 Rng 无关（确定性）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Battle_OverwhelmingWinner_AlwaysWins()
    {
        // fA = 150（100+50），fB = 0 → BattleChanceOf=1 → NextDouble()<1 恒真 → A 必胜（任意种子）
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var aChief = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var bChief = AddPolity(ctx, 2, 2, 0f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 0f, 0f);
        var w = AddWar(ctx, 0, 2, tick: 0, lastBattle: 0);
        ctx.Tick = WarInterval;   // 会战节奏到点

        new WarModel().Execute(ctx);

        Assert.AreEqual(1, w.WinsA, "碾压方必赢会战");
        Assert.AreEqual(0, w.WinsB);
        Assert.AreEqual(ctx.Tick, w.LastBattleTick, "会战时间戳更新");
        Assert.AreEqual(1, ctx.Wars.Count, "胜负未决 → 战争继续");
        // 败方损耗：P=0 保持 0；Contributed 0 保持 0（不产生负值）
        Assert.AreEqual(0f, bChief.P, 1e-6f);
        Assert.AreEqual(0f, bChief.Contributed, 1e-6f);
    }

    [Test]
    public void Battle_PowerlessSide_AlwaysLoses()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        AddPolity(ctx, 0, 0, 0f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 0f, 0f);
        AddPolity(ctx, 2, 2, 100f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 50f, 0f);
        var w = AddWar(ctx, 0, 2, tick: 0, lastBattle: 0);
        ctx.Tick = WarInterval;

        new WarModel().Execute(ctx);

        Assert.AreEqual(1, w.WinsB, "军力为 0 方必败（BattleChanceOf=0）");
        Assert.AreEqual(0, w.WinsA);
    }

    // ═════════════════════════════════════════════════════════════════
    // 2. 停战：WarMaxTicks 超时 → 战争移除
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void War_Timeout_RemovedFromList()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        AddPolity(ctx, 2, 2, 100f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 50f, 0f);
        AddWar(ctx, 0, 2, tick: 0, lastBattle: 0);
        ctx.Tick = CivSimContext.WarMaxTicks;   // 恰好 60 tick → 超时停战（先于会战判定）

        new WarModel().Execute(ctx);

        Assert.AreEqual(0, ctx.Wars.Count, "超时战争必须移除（停战）");
        Assert.False(WarModel.IsAtWar(ctx, 0, 2), "停战后不再是交战状态");
    }

    // ═════════════════════════════════════════════════════════════════
    // 3. 吞并（碾压）：胜场≥WarAnnexWins 且军力比≥WarPowerRatio →
    //    成员 ConqueredBy 效忠、首领流放豁免、战利品入池、原战争移除
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Annex_OverwhelmingWins_ConquersMembersAndPlunders()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var aChief = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var bChief = AddPolity(ctx, 2, 2, 0f, 10f, chief: true);   // 败国池 10
        var bMember = AddPolity(ctx, 3, 3, 0f, 5f);                // 成员池 5 → 总池 15
        // 预置碾压胜场（3 场）+ 本 tick 不触发会战（LastBattle=Tick）→ 直接进结算
        AddWar(ctx, 0, 2, tick: 0, winsA: CivSimContext.WarAnnexWins, lastBattle: 0);
        ctx.Tick = 0;
        ctx.Wars[0].LastBattleTick = 0;   // 已是最新 → 无新会战

        new WarModel().Execute(ctx);

        // 吞并效忠：败国成员（非首领）ConqueredBy = 战胜国酋长 Id
        Assert.AreEqual(0, bMember.ConqueredBy, "败国成员强制效忠征服者");
        Assert.AreEqual(-1, bChief.ConqueredBy, "败国首领不效忠（流放/消散）");
        // 战利品：败国池 15 × WarPlunderRate 0.5 = 7.5 入战胜国池
        Assert.AreEqual(7.5f, aChief.Contributed, 1e-4f, "战利品入池（Tilly 战争养战争）");
        Assert.AreEqual(1, ctx.WarsAnnexed);
        Assert.AreEqual(0, ctx.Wars.Count, "吞并后战争终结移除");
        // 首领流放：仍存活且格合法（PickMigrateTarget 可空转——只断言不崩溃/格有效）
        Assert.False(bChief.Dead);
        Assert.That(bChief.Cell, Is.InRange(0, grid.N - 1));
    }

    // ═════════════════════════════════════════════════════════════════
    // 4. 朝贡（险胜）：胜场≥WarTributeWins 且军力占优 → TributeTo 模式 + 边境割地（战争保留）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Tribute_CedeBorderCells_KeepsWar()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        AddPolity(ctx, 2, 2, 1f, 0f, chief: true);    // 军力 1 < 150 → 险胜
        AddPolity(ctx, 3, 3, 0f, 0f);
        // 领地：0 属 A；1/2/3 属 B（星形：1/2/3 都邻 0）
        ctx.CellOwner[0] = 0; ctx.CellOwner[1] = 2; ctx.CellOwner[2] = 2; ctx.CellOwner[3] = 2;
        AddWar(ctx, 0, 2, tick: 0, winsA: CivSimContext.WarTributeWins, lastBattle: 0);
        ctx.Tick = 0;   // 无新会战（LastBattle=Tick）→ 直接结算

        new WarModel().Execute(ctx);

        var w = ctx.Wars[0];
        Assert.AreEqual(0, w.TributeTo, "A 为朝贡受方");
        Assert.AreEqual(2, w.TributeFrom, "B 为朝贡出方");
        Assert.AreEqual(CivSimContext.WarTributeTicks, w.TributesLeft);
        Assert.AreEqual(1, ctx.Wars.Count, "朝贡期战争保留（外交状态延续）");
        // 割地：败国边境格（邻战胜国）全部易主（星形 3 格 ≤ WarCedeCells=3）
        Assert.AreEqual(0, ctx.CellOwner[1], "败国边境格 1 割让");
        Assert.AreEqual(0, ctx.CellOwner[2], "败国边境格 2 割让");
        Assert.AreEqual(0, ctx.CellOwner[3], "败国边境格 3 割让");
        Assert.AreEqual(0, ctx.CellOwner[0], "胜国格不动");
        Assert.True(WarModel.IsAtWar(ctx, 0, 2), "朝贡期仍算敌对（断交/冲突×2 依据）");
    }

    // ═════════════════════════════════════════════════════════════════
    // 5. 朝贡转移：每 tick 战败国总人口×WarTributeRate 入胜国池；TributesLeft 归零移除
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Tribute_TransfersPerTick_ThenRemoved()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var aChief = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        AddPolity(ctx, 2, 2, 100f, 200f, chief: true);   // 败国人口 200，池 200
        AddPolity(ctx, 3, 3, 100f, 100f, chief: false);
        var w = AddWar(ctx, 0, 2, tick: 0);
        w.TributeTo = 0; w.TributeFrom = 2; w.TributesLeft = 2;   // 朝贡期，剩 2 tick
        ctx.Tick = 10;

        new WarModel().Execute(ctx);
        // 每 tick 转移 amount = 200×0.005 = 1.0；按人口均摊：每人 Contributed −0.005×P
        Assert.AreEqual(199.5f, ctx.Polities[2].Contributed, 1e-3f, "败国酋长贡赋扣减");
        Assert.AreEqual(99.5f, ctx.Polities[3].Contributed, 1e-3f, "败国成员贡赋扣减");
        Assert.AreEqual(1.0f, aChief.Contributed, 1e-3f, "胜国池收到贡赋");
        Assert.AreEqual(1, w.TributesLeft, "朝贡倒数 1");
        Assert.AreEqual(1, ctx.Wars.Count);

        ctx.Tick = 11;
        new WarModel().Execute(ctx);
        Assert.AreEqual(0, ctx.Wars.Count, "朝贡归零 → 战争终结移除");
        Assert.AreEqual(2.0f, aChief.Contributed, 1e-3f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 6. 宣战负向门：冷却期内不能宣战（WarCooldownTicks）
    //    ⚠️ 宣战正门概率门控（WarDeclareChance=0.002）不可确定性构造——只测可确定的负向门
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Declare_CooldownBlocksNewWar()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var a = AddPolity(ctx, 0, 0, 100f, 50f, chief: true);   // 池足（50 ≥ 200×0.02）
        AddPolity(ctx, 1, 1, 100f, 0f);
        var b = AddPolity(ctx, 2, 2, 100f, 50f, chief: true);
        AddPolity(ctx, 3, 3, 100f, 0f);
        a.LastWarTick = 0; b.LastWarTick = 0;   // 冷却期内（30 tick）
        ctx.Tick = 0;

        new WarModel().Execute(ctx);

        Assert.AreEqual(0, ctx.Wars.Count, "冷却期内不得宣战");
        Assert.AreEqual(0, ctx.WarsDeclared);
    }

    // ═════════════════════════════════════════════════════════════════
    // 7. 战争结算 v2 动机门纯函数（WarAims——确定性直接断言，无 Rng）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Aim_TerritorialAmbition_LargerNeighbor()
    {
        var ctx = WarCtx(StarGrid());
        var a = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var b = AddPolity(ctx, 2, 2, 10f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 5f, 0f);
        // B 邦扩到 3 成员（≥ A 2×1.2=2.4）——手动加实体进 B 邦成员表（不碰 ChiefdomCells[4]）
        var m4 = new Polity { Id = 4, Cell = 1, P = 10f, Contributed = 0f, ChiefdomId = 2, StateId = -1, StateSize = 1, IsChief = false, LastWarTick = -1 };
        ctx.Polities.Add(m4);
        ctx.CellPolities[1] = m4;
        ctx.ChiefdomCells[2].Add(4);

        Assert.True(WarAims.HasTerritorialAim(ctx, a, b), "对方成员 3 ≥ 本国 2×1.2 → 领土野心");
        Assert.False(WarAims.HasTerritorialAim(ctx, b, a), "本国 3 对对方 2：不构成野心（方向性）");
    }

    [Test]
    public void Aim_ResourcePressure_HungryOrOverpopulated()
    {
        var ctx = WarCtx(StarGrid());
        var a = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var b = AddPolity(ctx, 2, 2, 10f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 5f, 0f);
        a.FLast = 0f;   // 饿（0 < 100×0.999）
        Assert.True(WarAims.HasResourcePressure(ctx, a), "饥荒 → 生存战争动机");

        a.FLast = 100f;   // 温饱
        a.P = 30f;        // 超载（> SplitPop=25）
        Assert.True(WarAims.HasResourcePressure(ctx, a), "人口超载 → 生存战争动机");

        a.P = 10f;
        Assert.False(WarAims.HasResourcePressure(ctx, a), "温饱且未超载 → 无压力");
    }

    [Test]
    public void Aim_MilitaryAdvantage_PowerfulChallenger()
    {
        var ctx = WarCtx(StarGrid());
        var a = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var b = AddPolity(ctx, 2, 2, 10f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 5f, 0f);
        // A 军力 150 ≥ B 15×1.5=22.5
        Assert.True(WarAims.HasMilitaryAdvantage(ctx, a, b), "军力 10 倍 → 机会主义动机");
        Assert.False(WarAims.HasMilitaryAdvantage(ctx, b, a), "弱方无优势");
    }

    [Test]
    public void Aim_Grudge_PreviouslyConquered()
    {
        var ctx = WarCtx(StarGrid());
        var a = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var b = AddPolity(ctx, 2, 2, 10f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 5f, 0f);
        a.ConqueredBy = b.Id;   // a 曾被 b 的国家征服（Annex 痕迹）
        Assert.True(WarAims.HasGrudge(ctx, a, b), "被征服过 → 世仇动机");
        Assert.False(WarAims.HasGrudge(ctx, b, a), "反向无仇");
    }

    [Test]
    public void Aim_RelationMult_SameCultureGroupReduces()
    {
        var ctx = WarCtx(StarGrid());
        var a = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var b = AddPolity(ctx, 2, 2, 10f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 5f, 0f);
        // 无任何关系（文化群空、无贸易、无仇恨）→ 1
        Assert.AreEqual(1f, WarAims.RelationMult(ctx, a, b), 1e-4f, "无关系 = 基准 1");
        // 同文化群主导 → ×0.5（亲缘纽带）
        a.CultureGroupShare = ShareField.NewCulture("g1");
        b.CultureGroupShare = ShareField.NewCulture("g1");
        Assert.AreEqual(CivSimContext.WarRelationCultureMult, WarAims.RelationMult(ctx, a, b), 1e-4f, "同文化群 → 战意减半");
        // 再加仇恨 → 0.5×2=1（抵消亲缘）
        a.ConqueredBy = b.Id;
        Assert.AreEqual(CivSimContext.WarRelationCultureMult * CivSimContext.WarRelationGrudgeMult, WarAims.RelationMult(ctx, a, b), 1e-4f, "仇恨记忆 ×2");
    }

    [Test]
    public void Aim_PowerGap_WeakChallengerDaresNot()
    {
        var ctx = WarCtx(StarGrid());
        var a = AddPolity(ctx, 0, 0, 10f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 10f, 0f);
        var b = AddPolity(ctx, 2, 2, 100f, 0f, chief: true);
        AddPolity(ctx, 3, 3, 50f, 0f);
        a.FLast = 10f;   // 温饱（不饿）
        // A 20 < B 150×0.5=75 → 弱方 ×0.3
        Assert.AreEqual(CivSimContext.WarPowerGapMult, WarAims.PowerGapMult(ctx, a, b), 1e-4f, "弱挑战方不敢打");
        Assert.AreEqual(1f, WarAims.PowerGapMult(ctx, b, a), 1e-4f, "强方无门槛");

        a.P = 30f;   // 超载 → 资源压力豁免
        Assert.AreEqual(1f, WarAims.PowerGapMult(ctx, a, b), 1e-4f, "生存压力豁免实力门槛");
    }

    // ═════════════════════════════════════════════════════════════════
    // 8. 战争结算 v2 天气判定（WarWeather.Classify——纯函数直接断言）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Weather_Classify_ColdRainyDryNone()
    {
        // 严寒：最冷月 −10°C（高纬/高原）
        var cold = WarWeather.Classify(-10f, 0.1f, 50f, 10f);
        Assert.AreEqual(WarWeather.Kind.Cold, cold.Kind);
        Assert.AreEqual(CivSimContext.WarColdAttackerMult, cold.AttackerMult, 1e-4f, "进攻方受阻");
        Assert.AreEqual(CivSimContext.WarColdLoss, cold.ExtraLoss, 1e-4f, "双方冻伤损耗");
        // 雨季：最大月降水比例 0.4（季风区）
        var rainy = WarWeather.Classify(5f, 0.4f, 50f, 25f);
        Assert.AreEqual(WarWeather.Kind.Rainy, rainy.Kind);
        Assert.AreEqual(CivSimContext.WarRainyAttackerMult, rainy.AttackerMult, 1e-4f, "泥泞阻进攻");
        Assert.AreEqual(0f, rainy.ExtraLoss);
        // 干旱：最干月 10mm + 年均温 28°C（旱区缺水）
        var dry = WarWeather.Classify(10f, 0.1f, 10f, 28f);
        Assert.AreEqual(WarWeather.Kind.Dry, dry.Kind);
        Assert.AreEqual(1f, dry.AttackerMult, 1e-4f);
        Assert.AreEqual(CivSimContext.WarDryLoss, dry.ExtraLoss, 1e-4f, "缺水损耗");
        // 温带宜居：全不满足 → 无天气
        var none = WarWeather.Classify(10f, 0.1f, 50f, 15f);
        Assert.AreEqual(WarWeather.Kind.None, none.Kind);
        Assert.AreEqual(1f, none.AttackerMult, 1e-4f);
        Assert.AreEqual(0f, none.ExtraLoss);
        // 严寒优先于雨季（一型互斥）
        var coldFirst = WarWeather.Classify(-10f, 0.4f, 50f, 10f);
        Assert.AreEqual(WarWeather.Kind.Cold, coldFirst.Kind, "严寒 > 雨季优先级");
    }

    // ═════════════════════════════════════════════════════════════════
    // 9. 战争结算 v2 宣战动机门（经 Execute 间接验证——纯函数已直接断言；此处验证整条链路）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Declare_NoMotive_NoWar()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        // 双方都温饱、势均力敌、成员数相同、无仇恨 → **双向都无动机**（v2 双向评估：
        // 任一方向有动机即开战，此构造两方向都无 → aimMult=0 直接跳过，无 Rng 参与）
        var a = AddPolity(ctx, 0, 0, 10f, 50f, chief: true);
        AddPolity(ctx, 1, 1, 10f, 0f);
        var b = AddPolity(ctx, 2, 2, 10f, 50f, chief: true);
        AddPolity(ctx, 3, 3, 10f, 0f);
        a.FLast = 10f; b.FLast = 10f;   // 双方温饱（不触发饥荒；P=10<SplitPop 不超载 → 无资源压力）
        ctx.EnsureTerritory();
        ctx.TerritoryOf(a).Add(a.Cell);
        ctx.TerritoryOf(b).Add(2);
        ctx.Tick = CivSimContext.WarCooldownTicks;   // 30：避开冷却门抢先（LastWarTick=-1 时 0−(−1)=1<30 直接拦截）

        new WarModel().Execute(ctx);

        Assert.AreEqual(0, ctx.Wars.Count, "双方无动机 → 不开战（aimMult=0 直接跳过，无 Rng 参与）");
        Assert.AreEqual(0, ctx.WarsDeclared);
    }

    // ⚠️ 以下两个宣战正门测试依赖 DeterministicRandom(7) 固定序列（0.002/tick 概率累积到守卫上限内必然命中）。
    //   种子脆弱：未来任何新增 Rng 消费点都会改变序列——若测试翻车先重验种子，勿直接调守卫上限。

    [Test]
    public void Declare_Motive_EventuallyWars()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        // A 军事优势（150 ≥ 15×1.5）→ 有动机；池足、相邻
        AddPolity(ctx, 0, 0, 100f, 50f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        AddPolity(ctx, 2, 2, 10f, 50f, chief: true);
        AddPolity(ctx, 3, 3, 5f, 0f);
        ctx.EnsureTerritory();
        ctx.TerritoryOf(ctx.Polities[0]).Add(0);
        ctx.TerritoryOf(ctx.Polities[2]).Add(2);

        int guard = 0;
        while (ctx.WarsDeclared == 0 && guard < 3000)
        {
            ctx.Tick = guard + 1;
            new WarModel().Execute(ctx);
            guard++;
        }

        Assert.Greater(ctx.WarsDeclared, 0, "有动机国家最终宣战（种子确定——0.002/tick 期望 6 场/3000 tick）");
    }

    [Test]
    public void Declare_MultiplePairs_EventuallyMultipleWars()
    {
        var grid = StarGrid();
        grid.OverrideNeighbors(new[] { new[] { 1, 3 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 2, 0 } });   // 环状：0-1-2-3-0
        var ctx = WarCtx(grid);
        ctx.NextPolityId = 8;
        ctx.ChiefdomCells = new List<int>[8];
        for (int i = 0; i < 8; i++) ctx.ChiefdomCells[i] = new List<int>();
        // 国 A{0,4} B{1,5}（强）C{2,6} D{3,7}（弱）——强方对弱方有军事优势动机；A-B/C-D 势均不宣
        void Make(int id, int cell, float p, float pool, int chiefdom, bool chief)
        {
            var e = new Polity
            {
                Id = id, Cell = cell, P = p, Contributed = pool, ChiefdomId = chiefdom,
                StateId = chief ? id : -1, StateSize = chief ? 2 : 1, IsChief = chief, LastWarTick = -1,
            };
            ctx.Polities.Add(e);
            ctx.CellPolities[cell] = e;
            ctx.ChiefdomCells[chiefdom].Add(id);
        }
        Make(0, 0, 100f, 50f, 0, true);  Make(4, 0, 50f, 0f, 0, false);
        Make(1, 1, 100f, 50f, 1, true);  Make(5, 1, 50f, 0f, 1, false);
        Make(2, 2, 10f, 50f, 2, true);   Make(6, 2, 5f, 0f, 2, false);
        Make(3, 3, 10f, 50f, 3, true);   Make(7, 3, 5f, 0f, 3, false);
        ctx.EnsureTerritory();
        for (int i = 0; i < 4; i++) ctx.TerritoryOf(ctx.Polities[i]).Add(i);

        int guard = 0;
        while (ctx.WarsDeclared < 2 && guard < 4000)
        {
            ctx.Tick = guard + 1;
            new WarModel().Execute(ctx);
            guard++;
        }

        Assert.GreaterOrEqual(ctx.WarsDeclared, 2, "多对独立国家对各自宣战（去单 tick 限 1 场后多场战争可发生）");
    }

    // ═════════════════════════════════════════════════════════════════
    // 10. 战争结算 v2 声望影响（吞并胜利者 Prestige↑ 败者↓——接 Sahlins 声望体系）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Annex_WinnerGainsPrestige_LoserLoses()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var aChief = AddPolity(ctx, 0, 0, 100f, 0f, chief: true);
        AddPolity(ctx, 1, 1, 50f, 0f);
        var bChief = AddPolity(ctx, 2, 2, 0f, 10f, chief: true);
        AddPolity(ctx, 3, 3, 0f, 5f);
        AddWar(ctx, 0, 2, tick: 0, winsA: CivSimContext.WarAnnexWins, lastBattle: 0);
        ctx.Tick = 0;
        ctx.Wars[0].LastBattleTick = 0;   // 无新会战 → 直接结算（硬路径：军力比 ∞ ≥ 碾压线）

        new WarModel().Execute(ctx);

        // 净胜场差 3 → 胜者 Prestige +0.5×3=1.5；败者 clamp 0
        Assert.AreEqual(CivSimContext.WarPrestigeGain * CivSimContext.WarAnnexWins, aChief.Prestige, 1e-4f, "吞并胜利者声望上升");
        Assert.AreEqual(0f, bChief.Prestige, 1e-4f, "败者声望扣减 clamp 0");
    }
}