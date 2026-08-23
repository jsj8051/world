using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Policies;

/// <summary>酋邦贡赋策略：互惠贡赋（低税率）+ 小精英比例。</summary>
public sealed class ChiefdomTributePolicy : ITributePolicy
{
    public float TributeRate => CivSimContext.TributeRate;
    public float EliteFrac => CivSimContext.EliteFrac;
}

/// <summary>国家贡赋策略：税制化（贡赋率 ×2）+ 官僚供养（精英比例 ×2.5）。</summary>
public sealed class StateTributePolicy : ITributePolicy
{
    public float TributeRate => CivSimContext.StateTributeRate;
    public float EliteFrac => CivSimContext.StateEliteFrac;
}

/// <summary>贡赋策略查表工厂（2026-08-23；确定性——按 StateId 派生状态，无 Rng）。</summary>
public static class TributePolicies
{
    public static readonly ITributePolicy Chiefdom = new ChiefdomTributePolicy();
    public static readonly ITributePolicy State = new StateTributePolicy();

    /// <summary>按实体政治体取策略：国家成员（StateId ≥ 0）→ State；否则酋邦互惠 → Chiefdom。</summary>
    public static ITributePolicy Of(Polity e) => e.StateId >= 0 ? State : Chiefdom;
}
