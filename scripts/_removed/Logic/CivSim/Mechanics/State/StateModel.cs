using System.Collections.Generic;
using World.CivSim;
using World.CivSim.Events;

namespace World.CivSim.Mechanics.State;

// ══════════════════════════════════════════════════════════════════
// ①k 国家涌现机制外壳（Order 49，2026-08-16 阶段4；docs/阶段4设计-国家涌现.md；用户拍板 1A2A3A4A）。
//   酋邦 → 国家 = **制度化**（无规模阈值——性质跃迁非体积达标）。
//   涌现条件（AND，全部用已入档持久字段 → 纯派生不存档，读档续跑无分叉）：
//     ① 都城：至尊酋长（ChiefdomId==Id 且 IsChief）占据聚落，且为治理中心（IsCity——2026-08-23 功能定性，旧 Level 门槛已删）
//     ③ 贡赋盈余：贡赋池（Σ 成员 Contributed）≥ 酋邦总人口 × StateTributePerCap
//     ④ 存续：Tick − 都城.BornTick ≥ StateDwellTicks（都城实体存续——场所比人长寿）
//   ⚠️ 2026-08-24 用户拍板：国家 = **三条件**——"首都即可承担行政网络"，城邦即国家默认形态；
//     次级中心（决策层级）从硬条件移除（回归普通机制，非概念门槛；概念轴保持 band/tribe/chiefdom/state 四级）。
//   ⚠️ 2026-08-23 概念 = 机制组合（Phase 1）：判定拆为规范积木（Specification）+ 写入积木
//     （StateCapitalCheck / StateTributePoolCheck / StateDwellCheck / StateAssign）——本类退化为机制外壳，
//     执行端 = StateAssign（"State = AND(①③④) → Assign"）。
//   执行位置：Order 49（Chiefdom 46 之后——读最新 ChiefdomId/成员表；Conflict 75 之前——冲突豁免生效）。
//   滞后 1 tick 语义（PrestigeModel 25 读 StateId 为上一 tick 值）：SettleDerived 重建值 ≡ 演化末写入值
//     （同输入同公式）→ 读档续跑无分叉（T04 验证）。
// ══════════════════════════════════════════════════════════════════
public sealed class StateModel : CivModelBase
{
    public override string Name => "国家涌现";
    public override int Order => 49;

    /// <summary>机制外壳：执行端 = StateAssign 规范积木（清空 + 三判定 AND + 写入）。
    /// ⚠️ 事件旁路（2026-08-24 ⑪）：Rebuild 前后国家集 diff——只发事件，不改机制（StateAssign 纯函数不动）。</summary>
    protected override void Apply(CivSimContext ctx)
    {
        var before = StateSet(ctx);
        StateAssign.Rebuild(ctx);
        var after = StateSet(ctx);
        foreach (var id in after)
            if (!before.Contains(id))
                ctx.Events.Add(new CivEventRecord(ctx.Tick, EventTypes.StateEmerge, id));
        foreach (var id in before)
            if (!after.Contains(id))
                ctx.Events.Add(new CivEventRecord(ctx.Tick, EventTypes.StateGone, id));
    }

    /// <summary>国家集合 = 至尊酋长且正式国家（WarPolicies.Of 同判据——单一事实源；O(P) 旁路快照）。</summary>
    private static HashSet<int> StateSet(CivSimContext ctx)
    {
        var set = new HashSet<int>();
        foreach (var e in ctx.Polities)
            if (!e.Dead && e.IsChief && e.StateId == e.Id && e.StateSize >= 2) set.Add(e.Id);
        return set;
    }
}
