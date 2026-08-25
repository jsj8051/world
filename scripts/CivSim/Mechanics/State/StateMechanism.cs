using System;
using System.Collections.Generic;
using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.State;

// ══════════════════════════════════════════════════════════════════
// ② 国家制度机制（Order 50，2026-08-25 用户拍板"先做一个通用的国家机制，像欧陆风云那样"）。
//   **EU4 式通用国家机制**：国家 = 制度实体（不是成员标记）——有自身持久状态（国库/稳定度/
//   合法性/君主），全部硬编码、无科技门槛、一视同仁作用于所有国家（通用底盘，后续文字行政/
//   货币等机制在此之上加科技门槛）。
//   每 tick（确定性，无 Rng——Rng 只存在于战争结算，T04 读档续跑无分叉）：
//     ① 自愈同步：现国家（Leader.IsChief && StateId==Id && StateSize≥2）→ 建档；档案中的亡国 → 删档
//     ② 都城 = 制度载体（P6 拍板：嫁接聚落网络——场所比人长寿，国库/稳定度不随君主死丢失）
//     ③ 君主 = 虚拟头衔（P7：Prestige 最高成员）；君主死 → 制度化推举继位（P9：国家不灭，代价 = 稳定 −1 + 合法性重置）
//     ④ 国库收支：税收（Σ成员 P × 税率 × 合法性折损）− 维持费（Σ成员 P × 官僚/军队费率）
//     ⑤ 稳定度：继承窗口/财政赤字/战争 拖累；和平盈余向 0 回归；≤ −2 → 崩盘（都城陷落 → 三条件断 → 国家自然消亡）
//     ⑥ 合法性：新君初立低 → 向基准 50 温和回归；低合法 → 税收折损
// ══════════════════════════════════════════════════════════════════
public sealed class StateMechanism : CivModelBase
{
    public override string Name => "国家制度";
    public override int Order => 52;   // StateModel(49) 与 Spread(50)/War(51) 之后——战争结算完再算国家收支/稳定度

    protected override void Apply(CivSimContext ctx) => Run(ctx);

    /// <summary>国家制度每 tick 运转（公开静态——测试直接驱动，同 StateAssign.Rebuild 模式）。</summary>
    public static void Run(CivSimContext ctx)
    {
        // ── ① 现国家集快照（与 StateModel.StateSet 同判据——单一事实源：Leader.IsChief 且正式国家）──
        var alive = new List<StateEntity>();
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead || !e.IsChief || e.StateId != e.Id || e.StateSize < 2) continue;
            var st = FindOrCreate(ctx, e);   // 无档案 → 新建（BornTick=Tick）
            if (st == null) continue;
            alive.Add(st);
            Update(ctx, st, e);
        }
        // ── ② 删档：档案里的亡国（被吞并/崩盘/条件断）——自愈同步 ──
        for (int i = ctx.States.Count - 1; i >= 0; i--)
        {
            var st = ctx.States[i];
            bool still = false;
            for (int k = 0; k < alive.Count; k++)
                if (alive[k] == st) { still = true; break; }
            if (!still) ctx.States.RemoveAt(i);
        }
    }

    /// <summary>按国家 Id 找档案；无 → 新建（初始化国库 0 / 稳定 0 / 合法 50 / 君主 = 成员 Prestige 最高）。</summary>
    private static StateEntity FindOrCreate(CivSimContext ctx, Polity leader)
    {
        for (int i = 0; i < ctx.States.Count; i++)
            if (ctx.States[i].Id == leader.Id) return ctx.States[i];

        var st = new StateEntity
        {
            Id = leader.Id,
            CapitalHabId = ctx.HabitationOf(leader)?.Id ?? -1,
            MonarchId = (TopPrestigeMember(ctx, leader.Id) ?? leader).Id,
            BornTick = ctx.Tick,
        };
        ctx.States.Add(st);
        return st;
    }

    /// <summary>每 tick 制度运转：都城同步 → 君主更替 → 国库 → 稳定度 → 合法性 → 崩盘。</summary>
    private static void Update(CivSimContext ctx, StateEntity st, Polity leader)
    {
        var capital = ctx.HabitationOf(leader);   // 都城 = 制度载体（PlaceId → 聚落；酋长占据中）
        if (capital != null) st.CapitalHabId = capital.Id;

        var members = ctx.ChiefdomCells != null && leader.Id < ctx.ChiefdomCells.Length
            ? ctx.ChiefdomCells[leader.Id] : null;

        // ── ③ 君主更替（P7/P9：君主死 → Prestige 最高成员继位——国家不灭，制度化推举）──
        Polity monarch = MonarchOf(ctx, st);
        if (monarch == null || monarch.Dead)
        {
            st.MonarchId = (TopPrestigeMember(ctx, leader.Id) ?? leader).Id;   // 继位
            st.Stability -= CivSimContext.StateMonarchDeathStabilityDrop;    // 继位混乱代价
            st.Legitimacy = CivSimContext.StateLegitimacyNewMonarch;         // 新君初立合法性低（EU4）
        }

        // ── ④ 国库收支（现金税/费——与贡赋池 Contributed 实物税并行；贡赋池仍作涌现判据）──
        float pop = PopOf(ctx, members);
        float legitMult = 0.5f + st.Legitimacy / 200f;   // 合法 [0,100] → [0.5, 1.0]（低合法难征税，EU4）
        float tax = pop * CivSimContext.StateTaxPerCap * legitMult;
        float cost = pop * CivSimContext.StateCostPerCap;
        st.Treasury += tax - cost;

        // ── ⑤ 稳定度（EU4：战争/内乱/财政危机 ↓；和平盈余缓慢回归 0）──
        bool crisis = InInheritanceCrisis(ctx, members) || st.Treasury < 0f;
        bool atWar = AtWar(ctx, st.Id);
        if (!crisis && !atWar)
            st.Stability += st.Stability < 0f ? CivSimContext.StateStabilityRecover
                           : st.Stability > 0f ? -CivSimContext.StateStabilityRecover : 0f;
        else
            st.Stability -= (crisis ? CivSimContext.StateStabilityCrisisDrop : 0f)
                          + (atWar ? CivSimContext.StateStabilityWarDrop : 0f);
        st.Stability = Math.Clamp(st.Stability, -3f, 3f);

        // ── ⑥ 合法性回归（向基准 50 温和收敛——治绩修复）──
        st.Legitimacy += (CivSimContext.StateLegitimacyBase - st.Legitimacy) * CivSimContext.StateLegitimacyRegen;

        // ── 崩盘：稳定度 ≤ −2 → 都城陷落（制度载体毁 → 下 tick 三条件断 → StateAssign 不再 Assign → 自愈删档）──
        if (st.Stability <= CivSimContext.StateCollapseStability && capital != null && !capital.IsRuin)
        {
            capital.OccupantId = -1;   // 都城被弃（内乱陷落——EU4 低稳定首都沦陷 → 国家亡）
            capital.RuinFrom = ctx.Tick;
        }
    }

    /// <summary>国家君主实体（MonarchId → Polity；无/越界 = null）。</summary>
    private static Polity MonarchOf(CivSimContext ctx, StateEntity st)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == st.MonarchId) return ctx.Polities[i];
        return null;
    }

    /// <summary>成员中 Prestige 最高者（虚拟头衔——P7；平局 → 较小 Id，确定性）。
    /// 含酋长自己（酋长通常即最有权势者）。</summary>
    private static Polity TopPrestigeMember(CivSimContext ctx, int stateId)
    {
        var list = ctx.ChiefdomCells != null && stateId < ctx.ChiefdomCells.Length ? ctx.ChiefdomCells[stateId] : null;
        Polity best = null;
        if (list != null)
            for (int i = 0; i < list.Count; i++)
            {
                var m = PolityById(ctx, list[i]);
                if (m == null || m.Dead) continue;
                if (best == null || m.Prestige > best.Prestige
                    || (m.Prestige == best.Prestige && m.Id < best.Id)) best = m;
            }
        return best;
    }

    /// <summary>成员总人口。</summary>
    private static float PopOf(CivSimContext ctx, List<int> members)
    {
        if (members == null) return 0f;
        float pop = 0f;
        for (int i = 0; i < members.Count; i++)
        {
            var m = PolityById(ctx, members[i]);
            if (m != null && !m.Dead) pop += m.P;
        }
        return pop;
    }

    /// <summary>是否处于继承窗口（任一成员 SuccessionUntil 未过——权力真空 → 内乱，Kirch）。</summary>
    private static bool InInheritanceCrisis(CivSimContext ctx, List<int> members)
    {
        if (members == null) return false;
        for (int i = 0; i < members.Count; i++)
        {
            var m = PolityById(ctx, members[i]);
            if (m != null && m.SuccessionUntil > ctx.Tick) return true;
        }
        return false;
    }

    /// <summary>该国家当前是否处于战争（交战或朝贡期——EU4：战争拖累稳定度）。</summary>
    private static bool AtWar(CivSimContext ctx, int stateId)
    {
        for (int i = 0; i < ctx.Wars.Count; i++)
            if (ctx.Wars[i].Involves(stateId)) return true;
        return false;
    }

    private static Polity PolityById(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == id) return ctx.Polities[i];
        return null;
    }
}