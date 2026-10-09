using System.Collections.Generic;
using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.State;

/// <summary>
/// 国家涌现 ① 都城判定（规范积木，纯函数——可独立测试/复用）。
/// 条件：至尊酋长（IsChief）占据聚落（PlaceId → Habitation 且 OccupantId 是自己），
/// 且聚落是城市（IsCity = HasAdmin——2026-08-23 功能定性：酋邦中心即城市，无 Level 门槛）。
/// </summary>
public static class StateCapitalCheck
{
    /// <summary>解析酋长的都城聚落（PlaceId → Habitation；未占据/废墟 = null；供 ②④ 复用）。</summary>
    public static Habitation Of(Polity chief, Dictionary<int, Habitation> settleById)
    {
        if (chief == null || chief.PlaceId < 0 || settleById == null) return null;
        if (!settleById.TryGetValue(chief.PlaceId, out var s)) return null;
        return s.OccupantId == chief.Id ? s : null;
    }

    /// <summary>① 都城判定：酋长占据聚落且已是治理中心（IsCity——酋邦中心 → 城市条件涌现）。</summary>
    public static bool Check(CivSimContext ctx, Polity chief, Dictionary<int, Habitation> settleById)
    {
        var capital = Of(chief, settleById);
        return capital != null && capital.IsCity;
    }
}
