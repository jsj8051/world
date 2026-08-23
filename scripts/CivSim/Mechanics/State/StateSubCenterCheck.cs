using System.Collections.Generic;
using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.State;

/// <summary>
/// 国家涌现 ② 决策层级判定（规范积木，纯函数）。
/// 条件：酋邦内 ≥2 个成员聚落，且存在 Level ≥ StateSubCenterLevel（1=村庄+）的非都城聚落——
/// 次级行政中心（官僚节点，Childe 城市革命的组织前提）。
/// </summary>
public static class StateSubCenterCheck
{
    /// <summary>② 决策层级判定：成员聚落 ≥2 且存在次级中心。</summary>
    public static bool Check(List<int> members, Polity[] byId, Dictionary<int, Habitation> settleById, int capitalId)
    {
        int memberHabitations = 0;
        bool hasSubCenter = false;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            Polity m = byId[mid];
            if (m == null || m.Dead) continue;
            Habitation s = m.PlaceId >= 0 && settleById.TryGetValue(m.PlaceId, out var st) ? st : null;
            if (s == null || s.OccupantId != m.Id) continue;
            memberHabitations++;
            if (s.Id != capitalId && s.Level >= CivSimContext.StateSubCenterLevel) hasSubCenter = true;
        }
        return memberHabitations >= 2 && hasSubCenter;
    }
}
