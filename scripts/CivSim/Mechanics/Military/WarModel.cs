// 职责：军事战争（Order 51）——阶段5 军事征服（docs/阶段5设计-军事征服.md；2026-08-23 概念=机制组合迁移至 Mechanics/Military/）。
using Godot;
using System;
using System.Collections.Generic;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Policies;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Military;
namespace World.CivSim.Mechanics.Military;

// ══════════════════════════════════════════════════════════════════
// ①m 军事战争（Order 51，2026-08-19 阶段5；用户拍板 P1 仅国家 / P3 战争=外交状态 / P4 战果分档）：
//    战争 = 两个国家之间的**持续外交状态**（War 段入档 v14——过程状态不可派生重建），
//    不是瞬时事件。生命周期：宣战（领地相邻+池足+概率门控+冷却）→ 会战（WarBattleInterval
//    节奏，军力 = Σ 成员 P×MilitMult × 都城加成 × 城墙防御加成）→ 战果分档结算：
//      碾压（胜场 ≥ WarAnnexWins 且军力比 ≥ WarPowerRatio）→ 吞并（成员 ConqueredBy 强制
//      效忠征服者——ChiefdomModel 下 tick 重建生效；首领流放；战利品入池；原国家消失）；
//      险胜（胜场 ≥ WarTributeWins）→ 朝贡（TributeTo 模式：每 tick 转移贡赋 + 边境割地）；
//      超时（WarMaxTicks）→ 停战（无赔偿）。
//    接线：ConflictModel（交战国冲突 ×2）、TradeModel（交战国断交）、
//      ChiefdomModel（ConqueredBy 强制归属）、StateModel（吞并后原国自动消失）。
//    确定性：会战胜负走 Rng（种子确定性）；其余纯函数/固定遍历序——读档续跑无分叉（T04 覆盖）。
// ══════════════════════════════════════════════════════════════════
public sealed class WarModel : CivModelBase
{
    public override string Name => "军事战争";
    public override int Order => 51;

    protected override void Apply(CivSimContext ctx)
    {
        // ── ① 现有战争：朝贡转移 / 停战 / 会战 / 结算（倒序遍历——结算可能移除）──
        for (int i = ctx.Wars.Count - 1; i >= 0; i--)
        {
            var w = ctx.Wars[i];
            if (w.IsTribute) { ProcessTribute(ctx, w); continue; }
            if (ctx.Tick - w.StartTick >= CivSimContext.WarMaxTicks) { ctx.Wars.RemoveAt(i); continue; }   // 停战
            if (ctx.Tick - w.LastBattleTick >= CivSimContext.WarBattleInterval)
                Battle(ctx, w);
            if (TryResolve(ctx, w)) ctx.Wars.RemoveAt(i);   // 吞并移除；朝贡转 Tribute 模式保留
        }
        // ── ② 宣战（低频门控；单 tick 至多 1 场——爆炸防护）──
        DeclareWars(ctx);
    }

    /// <summary>该国家当前是否处于战争（交战或朝贡期——敌对关系，断交/冲突×2 依据）。</summary>
    public static bool IsAtWar(CivSimContext ctx, int stateA, int stateB)
    {
        if (stateA < 0 || stateB < 0 || stateA == stateB) return false;
        for (int i = 0; i < ctx.Wars.Count; i++)
        {
            var w = ctx.Wars[i];
            if (w.Involves(stateA) && w.Involves(stateB)) return true;
        }
        return false;
    }

    /// <summary>会战胜率（纯函数——T71 直接断言，免概率采样噪声；fA=挑战方军力，fB=守方军力）。</summary>
    internal static float BattleChanceOf(float fA, float fB) => fA / Mathf.Max(0.0001f, fA + fB);

    // ──────────────────────────────────────────────
    // 会战
    // ──────────────────────────────────────────────
    private static void Battle(CivSimContext ctx, War w)
    {
        w.LastBattleTick = ctx.Tick;
        var byId = IdIndex(ctx);
        var settleById = SettleIndex(ctx);
        float fA = PowerOf(ctx, w.StateIdA, w, byId, settleById);
        float fB = PowerOf(ctx, w.StateIdB, w, byId, settleById);
        bool aWins = ctx.Rng.NextDouble() < BattleChanceOf(fA, fB);
        if (aWins) { w.WinsA++; ApplyLoss(ctx, w.StateIdB, byId); }
        else { w.WinsB++; ApplyLoss(ctx, w.StateIdA, byId); }
    }

    /// <summary>国家军力 = Σ 成员 P×MilitMult × (1+WarCapitalBonus×都城Level) × 防御加成（城墙，P6）。
    /// ⚠️ 每会战调 2 次（每 5 tick）——成员数百量级，O(成员) 可接受（T18 教训仅约束每 tick 路径）。</summary>
    private static float PowerOf(CivSimContext ctx, int stateId, War w, Band[] byId, Dictionary<int, Settlement> settleById)
    {
        var members = MembersOf(ctx, stateId);
        float f = 0f;
        int capitalLevel = 0, cities = 0;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            f += m.P * TechTable.MilitaryMult(m.TechKeys);
            var st = m.PlaceId >= 0 && settleById.TryGetValue(m.PlaceId, out var s) ? s : null;
            if (st == null || st.OccupantId != m.Id) continue;
            if (m.Id == stateId) capitalLevel = Math.Max(capitalLevel, st.Level);   // 都城（酋长聚落）
            if (st.Level >= 3) cities++;                                            // 城市=要塞（P6）
        }
        f *= 1f + CivSimContext.WarCapitalBonus * capitalLevel;
        if (stateId == w.Defender)
            f *= 1f + CivSimContext.WarCityDefenseBonus * cities;   // 防御方城墙加成
        return f;
    }

    /// <summary>会战败方损耗：成员人口 ×(1-WarLoss) + 贡赋池 ×(1-WarLoss)（消耗战——损耗可累积瓦解）。</summary>
    private static void ApplyLoss(CivSimContext ctx, int loserState, Band[] byId)
    {
        var members = MembersOf(ctx, loserState);
        float keep = 1f - CivSimContext.WarLoss;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            m.P *= keep;
            m.Contributed *= keep;
        }
    }

    // ──────────────────────────────────────────────
    // 结算（战果分档，P4——未来改 AI 判定）
    // ──────────────────────────────────────────────
    /// <summary>战果分档结算。返回 true = 战争已终结（移除）；false = 战争继续（或转朝贡模式保留）。</summary>
    private static bool TryResolve(CivSimContext ctx, War w)
    {
        var byId = IdIndex(ctx);
        var settleById = SettleIndex(ctx);
        float fA = PowerOf(ctx, w.StateIdA, w, byId, settleById);
        float fB = PowerOf(ctx, w.StateIdB, w, byId, settleById);
        // 吞并：碾压（胜场达标 + 当前军力比 ≥ WarPowerRatio）
        if (w.WinsA >= CivSimContext.WarAnnexWins && fA >= fB * CivSimContext.WarPowerRatio)
        {
            Annex(ctx, w, winner: w.StateIdA, loser: w.StateIdB, byId);
            return true;
        }
        if (w.WinsB >= CivSimContext.WarAnnexWins && fB >= fA * CivSimContext.WarPowerRatio)
        {
            Annex(ctx, w, winner: w.StateIdB, loser: w.StateIdA, byId);
            return true;
        }
        // 朝贡：险胜（胜场达标 + 军力占优——未达碾压线不吞并）
        if (w.WinsA >= CivSimContext.WarTributeWins && fA > fB)
        {
            w.TributeTo = w.StateIdA; w.TributeFrom = w.StateIdB;
            w.TributesLeft = CivSimContext.WarTributeTicks;
            CedeCells(ctx, w, loser: w.StateIdB, byId);
            return false;
        }
        if (w.WinsB >= CivSimContext.WarTributeWins && fB > fA)
        {
            w.TributeTo = w.StateIdB; w.TributeFrom = w.StateIdA;
            w.TributesLeft = CivSimContext.WarTributeTicks;
            CedeCells(ctx, w, loser: w.StateIdA, byId);
            return false;
        }
        return false;
    }

    /// <summary>吞并：战败国成员 ConqueredBy 强制效忠战胜国酋长（ChiefdomModel 下 tick 重建生效——
    /// 无视庇护半径）；战败国酋长流放驱逐（其国随成员归属消散）；战利品入战胜国池。
    /// ⚠️ 持久痕迹：ConqueredBy 入档（v14）；征服者死亡 → ChiefdomModel 重建清空（效忠脱落）。
    /// ⚠️ StateId 不直接改——StateModel(49) 下 tick 从 ChiefdomId 重建，原国家自动消失（崩溃路径④）。</summary>
    private static void Annex(CivSimContext ctx, War w, int winner, int loser, Band[] byId)
    {
        var members = MembersOf(ctx, loser);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            if (m.Id == loser) continue;                     // 败国首领不效忠——被流放
            m.ConqueredBy = winner;
        }
        // 首领流放：强制迁移（复用驱逐逻辑——败者饿死兜底由演化自然处理）
        var chief = loser < byId.Length ? byId[loser] : null;
        if (chief != null && !chief.Dead && chief.Cell >= 0 && chief.Cell < ctx.Grid.N)
        {
            int target = SplitMigrateModel.PickMigrateTarget(ctx, chief);
            if (target >= 0)
            {
                if (ctx.CellBands[chief.Cell] == chief) ctx.CellBands[chief.Cell] = null;
                chief.Cell = target;
                chief.LastMigrateTick = ctx.Tick;
                ctx.CellBands[target] = chief;
                ctx.Migrations++;
            }
        }
        // 战利品：战败国贡赋池 × WarPlunderRate 入战胜国池（Tilly：战争养战争）
        var winnerChief = winner < byId.Length ? byId[winner] : null;
        if (winnerChief != null && !winnerChief.Dead)
        {
            float pool = 0f;
            for (int k = 0; k < members.Count; k++)
            {
                int mid = members[k];
                if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pool += byId[mid].Contributed;
            }
            winnerChief.Contributed += pool * CivSimContext.WarPlunderRate;
        }
        ctx.WarsAnnexed++;
    }

    /// <summary>朝贡割地：战败国边境格（与战胜国领地相邻）取前 WarCedeCells 格 → CellOwner 归战胜国酋长。
    /// ⚠️ 领地重建（TerritoryModel 45）下 tick 自动整合——CellOwner 是持久字段，读档续跑无分叉。</summary>
    private static void CedeCells(CivSimContext ctx, War w, int loser, Band[] byId)
    {
        var winnerChief = w.TributeTo < byId.Length ? byId[w.TributeTo] : null;
        if (winnerChief == null || winnerChief.Dead || ctx.CellOwner == null) return;
        int n = ctx.Grid.N;
        int ceded = 0;
        for (int c = 0; c < n && ceded < CivSimContext.WarCedeCells; c++)
        {
            int o = ctx.CellOwner[c];
            if (o < 0) continue;
            // 败国格：酋长自身格 或 成员格（成员 ChiefdomId == 败国 id——国家=酋邦，同 Id 语义）
            var oBand = o < byId.Length ? byId[o] : null;
            bool isLoser = o == loser || (oBand != null && !oBand.Dead && oBand.ChiefdomId == loser);
            if (!isLoser) continue;
            // 边境判定：任一邻居属战胜国
            bool bordersWinner = false;
            foreach (int nb in ctx.Grid.Neighbors[c])
            {
                int no = ctx.CellOwner[nb];
                if (no == winnerChief.Id) { bordersWinner = true; break; }
            }
            if (!bordersWinner) continue;
            ctx.CellOwner[c] = winnerChief.Id;
            ceded++;
        }
    }

    // ──────────────────────────────────────────────
    // 朝贡转移
    // ──────────────────────────────────────────────
    /// <summary>朝贡期：每 tick 转移 战败国总人口×WarTributeRate 入战胜国池（Earle 贡赋的对外延伸）。
    /// TributesLeft 递减，归零 → 战争终结（朝贡随战争存在，停战即止——不形成持久朝贡国）。</summary>
    private static void ProcessTribute(CivSimContext ctx, War w)
    {
        var byId = IdIndex(ctx);
        var payers = MembersOf(ctx, w.TributeFrom);
        float pop = 0f;
        for (int k = 0; k < payers.Count; k++)
        {
            int mid = payers[k];
            if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pop += byId[mid].P;
        }
        float amount = pop * CivSimContext.WarTributeRate;
        // 战败国分摊（按人口比例——简化：均摊；确定性与成员表序无关）
        if (amount > 0f && payers.Count > 0)
        {
            float perCap = amount / pop;
            for (int k = 0; k < payers.Count; k++)
            {
                int mid = payers[k];
                var m = mid < byId.Length ? byId[mid] : null;
                if (m == null || m.Dead || m.P <= 0f) continue;
                m.Contributed = Mathf.Max(0f, m.Contributed - perCap * m.P);
            }
            var winnerChief = w.TributeTo < byId.Length ? byId[w.TributeTo] : null;
            if (winnerChief != null && !winnerChief.Dead) winnerChief.Contributed += amount;
        }
        if (--w.TributesLeft <= 0) ctx.Wars.Remove(w);
    }

    // ──────────────────────────────────────────────
    // 宣战
    // ──────────────────────────────────────────────
    private static void DeclareWars(CivSimContext ctx)
    {
        // 国家集合：至尊酋长（StateId == 自身 Id）且正式国家（Size ≥ 2）
        // ⚠️ 2026-08-23 概念 = 机制组合（Phase 1）：宣战资格走策略多态（WarPolicies.Of 查表——
        //   仅国家可宣战，阶段5 P1 拍板；未来村庄民兵/城防策略在此扩展）
        var states = new List<Band>();
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
            if (e.Dead) continue;
            if (!WarPolicies.Of(e).CanDeclareWar(e)) continue;
            states.Add(e);
        }
        float reachKm = (2 * CivSimContext.InfluenceRadius + 1) * Mathf.Sqrt(ctx.Grid.CellAreaKm2);
        for (int i = 0; i < states.Count; i++)
        {
            var a = states[i];
            for (int j = i + 1; j < states.Count; j++)
            {
                var b = states[j];
                if (!CanDeclare(ctx, a, b, reachKm)) continue;   // 条件判定（纯函数——T70 直接断言）
                if (ctx.Rng.NextDouble() >= CivSimContext.WarDeclareChance) continue;
                ctx.Wars.Add(new War
                {
                    StateIdA = a.Id,
                    StateIdB = b.Id,
                    Defender = b.Id,
                    StartTick = ctx.Tick,
                    LastBattleTick = ctx.Tick,
                });
                a.LastWarTick = b.LastWarTick = ctx.Tick;   // 参战冷却（双方）
                ctx.WarsDeclared++;
                return;   // 单 tick 至多 1 场（爆炸防护）
            }
        }
    }

    /// <summary>宣战条件判定（纯函数——T70 直接断言，免概率噪声）：
    /// 冷却过 + 未交战 + 领地相邻 + 宣战国贡赋池足（WarMinPoolPerCap——穷兵不打）。
    /// 概率门控（WarDeclareChance）在 DeclareWars 内部，不在本函数。</summary>
    internal static bool CanDeclare(CivSimContext ctx, Band a, Band b, float reachKm)
    {
        if (ctx.Tick - a.LastWarTick < CivSimContext.WarCooldownTicks) return false;
        if (ctx.Tick - b.LastWarTick < CivSimContext.WarCooldownTicks) return false;
        if (IsAtWar(ctx, a.Id, b.Id)) return false;
        if (ctx.Grid.DistKm(a.Cell, b.Cell) > reachKm) return false;   // 远隔无接触（预过滤）
        if (!StatesTouch(ctx, a, b)) return false;                     // 国家领地接触
        if (PoolOf(ctx, a.Id) < PopOf(ctx, a.Id) * CivSimContext.WarMinPoolPerCap) return false;   // 池足（穷兵不打）
        return true;
    }

    /// <summary>国家领地接触：任一成员对领地边界接触（TerritoryTouches——同酋邦凝聚判定）。
    /// ⚠️ 用 Id 索引（O(成员对) 一次成型——线性 Find 在成员数百时是亿级比较）。</summary>
    private static bool StatesTouch(CivSimContext ctx, Band a, Band b)
    {
        var byId = IdIndex(ctx);
        var ma = MembersOf(ctx, a.Id);
        var mb = MembersOf(ctx, b.Id);
        float reachKm = (2 * CivSimContext.InfluenceRadius + 1) * Mathf.Sqrt(ctx.Grid.CellAreaKm2);
        for (int i = 0; i < ma.Count; i++)
        {
            int xId = ma[i];
            var x = xId < byId.Length ? byId[xId] : null;
            if (x == null || x.Dead) continue;
            for (int k = 0; k < mb.Count; k++)
            {
                int yId = mb[k];
                var y = yId < byId.Length ? byId[yId] : null;
                if (y == null || y.Dead) continue;
                if (ctx.Grid.DistKm(x.Cell, y.Cell) > reachKm) continue;
                if (CivSimContext.TerritoryTouches(ctx, x, y)) return true;
            }
        }
        return false;
    }

    // ──────────────────────────────────────────────
    // 辅助（成员表 = 酋邦成员表——国家是酋邦的制度化；O(成员)）
    // ──────────────────────────────────────────────
    private static List<int> MembersOf(CivSimContext ctx, int stateId)
    {
        if (ctx.ChiefdomCells == null || stateId < 0 || stateId >= ctx.ChiefdomCells.Length) return EmptyList;
        return ctx.ChiefdomCells[stateId];
    }

    private static readonly List<int> EmptyList = new();

    private static float PopOf(CivSimContext ctx, int stateId)
    {
        float pop = 0f;
        var byId = IdIndex(ctx);
        var members = MembersOf(ctx, stateId);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pop += byId[mid].P;
        }
        return pop;
    }

    private static float PoolOf(CivSimContext ctx, int stateId)
    {
        float pool = 0f;
        var byId = IdIndex(ctx);
        var members = MembersOf(ctx, stateId);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pool += byId[mid].Contributed;
        }
        return pool;
    }

    private static Band[] IdIndex(CivSimContext ctx)
    {
        int bufLen = Math.Max(ctx.NextBandId, ctx.Bands.Count + 1);
        var byId = new Band[bufLen];
        for (int i = 0; i < ctx.Bands.Count; i++)
            if (!ctx.Bands[i].Dead && ctx.Bands[i].Id < bufLen) byId[ctx.Bands[i].Id] = ctx.Bands[i];
        return byId;
    }

    private static Dictionary<int, Settlement> SettleIndex(CivSimContext ctx)
    {
        var d = new Dictionary<int, Settlement>();
        if (ctx.Settlements != null)
            foreach (var s in ctx.Settlements)
                d[s.Id] = s;
        return d;
    }
}
