using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Policies;

/// <summary>被征服者归属策略：效忠 ConqueredBy 征服者（无视庇护半径——Tilly 战争制造国家）。</summary>
public sealed class ConqueredMemberPolicy : IMembershipPolicy
{
    public int Assign(CivSimContext ctx, Band e, Band[] byId, int[] bestChief) => e.ConqueredBy;
}

/// <summary>酋长归属策略：自己 = 自己酋邦的中心（ChiefdomId = 自身 Id）。</summary>
public sealed class ChiefMemberPolicy : IMembershipPolicy
{
    public int Assign(CivSimContext ctx, Band e, Band[] byId, int[] bestChief) => e.Id;
}

/// <summary>自由成员归属策略：选 ChiefReach 内 Prestige 最高的酋长（庇护人；-1 = 独立）。</summary>
public sealed class FreeMemberPolicy : IMembershipPolicy
{
    public int Assign(CivSimContext ctx, Band e, Band[] byId, int[] bestChief)
        => e.Id < bestChief.Length ? bestChief[e.Id] : -1;
}

/// <summary>酋邦归属策略查表工厂（2026-08-23；确定性——按实体状态选择，无 Rng）。
/// 被征服者先校验效忠对象有效性（conqueror 存活 + 酋长 + 有领地）——失效 → 脱落（ConqueredBy=-1）回退正常分支。</summary>
public static class MembershipPolicies
{
    public static readonly IMembershipPolicy Conquered = new ConqueredMemberPolicy();
    public static readonly IMembershipPolicy Chief = new ChiefMemberPolicy();
    public static readonly IMembershipPolicy Free = new FreeMemberPolicy();

    /// <summary>按实体状态取归属策略：被征服效忠有效 → Conquered；酋长 → Chief；否则自由成员 → Free。</summary>
    public static IMembershipPolicy Of(CivSimContext ctx, Band e, Band[] byId)
    {
        if (e.ConqueredBy >= 0)
        {
            if (e.ConqueredBy < byId.Length)
            {
                var conqueror = byId[e.ConqueredBy];
                if (conqueror != null && !conqueror.Dead && conqueror.IsChief && conqueror.TerritoryId >= 0)
                    return Conquered;   // 效忠有效
            }
            e.ConqueredBy = -1;   // 效忠对象失效 → 脱落（回正常凝聚）
        }
        return e.IsChief ? Chief : Free;
    }
}
