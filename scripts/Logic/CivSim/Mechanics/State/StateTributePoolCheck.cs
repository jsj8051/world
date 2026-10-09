using System.Collections.Generic;
using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.State;

/// <summary>
/// 国家涌现 ③ 贡赋盈余判定（规范积木，纯函数）。
/// 条件：贡赋池（Σ 成员 Contributed）≥ 酋邦总人口 × StateTributePerCap——剩余集中（Childe）。
/// </summary>
public static class StateTributePoolCheck
{
    /// <summary>③ 贡赋盈余判定：贡赋池 ≥ 人口 × 线。</summary>
    public static bool Check(List<int> members, Polity[] byId)
    {
        float pop = 0f, pool = 0f;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            Polity m = byId[mid];
            if (m == null || m.Dead) continue;
            pop += m.P;
            pool += m.Contributed;
        }
        return pop > 0f && pool >= pop * CivSimContext.StateTributePerCap;
    }
}
