using Godot;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.Military;

// ══════════════════════════════════════════════════════════════════
// 开战动机纯函数积木（2026-08-23 战争结算 v2——用户拍板"该打的才打"：
//   旧开战条件 = 纯可行性判定（冷却/相邻/有钱），零动机 → 无差别开战 = 混乱；
//   根因修法 = 动机门 + 关系调节 + 实力门槛，而不是"单 tick 限 1 场"护栏）。
// 全部判定确定性、无 Rng（T70 式直接断言）；Rng 只在 DeclareWars 最终概率掷。
// 政体差异走策略族（WarAimPolicies.Of）——本类只承载规则，贫血实体零新字段。
// ══════════════════════════════════════════════════════════════════
public static class WarAims
{
    /// <summary>动机门：a 对 b 是否存在任何开战动机（领土野心/资源压力/军事优势/世仇）。</summary>
    public static bool HasAnyMotive(CivSimContext ctx, Polity a, Polity b) =>
        HasTerritorialAim(ctx, a, b) || HasResourcePressure(ctx, a)
        || HasMilitaryAdvantage(ctx, a, b) || HasGrudge(ctx, a, b);

    /// <summary>领土野心：对方成员数 ≥ 本国×WarAimTerritoryRatio（想吃掉更大的邻居——扩张对象）。</summary>
    public static bool HasTerritorialAim(CivSimContext ctx, Polity a, Polity b)
    {
        int ma = MembersOf(ctx, a.Id).Count;
        int mb = MembersOf(ctx, b.Id).Count;
        return mb >= ma * CivSimContext.WarAimTerritoryRatio;
    }

    /// <summary>资源压力：本国饥荒或人口超载（饿/挤——生存战争，与 ConflictModel 压力判定同式）。</summary>
    public static bool HasResourcePressure(CivSimContext ctx, Polity a) =>
        ctx.IsStarving(a) || a.P > CivSimContext.SplitPop;

    /// <summary>军事优势：本国军力 ≥ 对方×WarAimPowerRatio（机会主义——弱肉强食）。</summary>
    public static bool HasMilitaryAdvantage(CivSimContext ctx, Polity a, Polity b) =>
        PowerOf(ctx, a.Id) >= PowerOf(ctx, b.Id) * CivSimContext.WarAimPowerRatio;

    /// <summary>世仇：a 曾被 b 的国家征服（ConqueredBy 效忠痕迹——仇恨记忆，Annex 时写入）。</summary>
    public static bool HasGrudge(CivSimContext ctx, Polity a, Polity b) =>
        a.ConqueredBy == b.Id;

    /// <summary>关系调节乘数：同文化群主导 ×WarRelationCultureMult；商路节点（有贸易）×WarRelationTradeMult；
    /// 被对方征服过 ×WarRelationGrudgeMult。连乘——亲缘/利益降战意，仇恨升战意。</summary>
    public static float RelationMult(CivSimContext ctx, Polity a, Polity b)
    {
        float m = 1f;
        if (SameCultureGroup(a, b)) m *= CivSimContext.WarRelationCultureMult;
        if (HasTradeNode(ctx, a) || HasTradeNode(ctx, b)) m *= CivSimContext.WarRelationTradeMult;
        if (a.ConqueredBy == b.Id || b.ConqueredBy == a.Id) m *= CivSimContext.WarRelationGrudgeMult;
        return m;
    }

    /// <summary>实力门槛乘数：弱挑战方（本国军力 < 对方×WarPowerGapRatio）概率 ×WarPowerGapMult——
    /// 打不过不敢打；资源压力豁免（饿急了也打，生存优先）。</summary>
    public static float PowerGapMult(CivSimContext ctx, Polity a, Polity b)
    {
        if (HasResourcePressure(ctx, a)) return 1f;   // 生存战争豁免
        if (PowerOf(ctx, a.Id) < PowerOf(ctx, b.Id) * CivSimContext.WarPowerGapRatio)
            return CivSimContext.WarPowerGapMult;
        return 1f;
    }

    // ──────────────────────────────────────────────
    // 私有辅助（确定性：成员表 = 酋邦成员表——国家是酋邦的制度化）
    // ──────────────────────────────────────────────

    /// <summary>国家基础军力 = Σ 成员 P×MilitMult（动机判定用基础口径——不含都城/城墙加成，纯战争潜力）。</summary>
    private static float PowerOf(CivSimContext ctx, int stateId)
    {
        float f = 0f;
        var members = MembersOf(ctx, stateId);
        for (int k = 0; k < members.Count; k++)
        {
            var m = FindById(ctx, members[k]);
            if (m != null && !m.Dead) f += m.P * TechTable.MilitaryMult(m.TechKeys);
        }
        return f;
    }

    private static System.Collections.Generic.List<int> MembersOf(CivSimContext ctx, int stateId)
    {
        if (ctx.ChiefdomCells == null || stateId < 0 || stateId >= ctx.ChiefdomCells.Length) return EmptyList;
        return ctx.ChiefdomCells[stateId];
    }

    private static readonly System.Collections.Generic.List<int> EmptyList = new();

    private static Polity FindById(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == id && !ctx.Polities[i].Dead) return ctx.Polities[i];
        return null;
    }

    /// <summary>同文化群主导（CultureGroupShare top-2 份额场主导 key 相同——亲缘判定，慢变身份）。</summary>
    private static bool SameCultureGroup(Polity a, Polity b)
    {
        string ka = DominantKey(a.CultureGroupShare);
        return ka != null && ka == DominantKey(b.CultureGroupShare);
    }

    /// <summary>主导 key = 份额最大条目的 Key（null = 无身份）。</summary>
    private static string DominantKey(ShareEntry[] share)
    {
        if (share == null || share.Length == 0) return null;
        ShareEntry best = share[0];
        for (int i = 1; i < share.Length; i++)
            if (share[i].Frac > best.Frac) best = share[i];
        return best.Frac > 0 ? best.Key : null;
    }

    /// <summary>商路节点（贸易纽带代理）：占据聚落 HasMarket——活跃贸易（TradeModel 每 tick 扫描写入）。</summary>
    private static bool HasTradeNode(CivSimContext ctx, Polity e)
    {
        var st = ctx.HabitationOf(e);
        return st != null && st.HasMarket;
    }
}
