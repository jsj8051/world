using World.CivSim.Entities;

namespace World.CivSim.Policies;

/// <summary>默认战争策略：非国家——无宣战资格。</summary>
public sealed class DefaultWarPolicy : IWarPolicy
{
    public bool CanDeclareWar(Band e) => false;
    public float MilitaryMult(Band e) => 1f;
}

/// <summary>国家战争策略：至尊酋长（正式国家）——可宣战（阶段5 P1 拍板）。</summary>
public sealed class StateWarPolicy : IWarPolicy
{
    public bool CanDeclareWar(Band e) => true;
    public float MilitaryMult(Band e) => 1f;
}

/// <summary>战争策略查表工厂（2026-08-23；确定性——按 StateId/IsChief 派生状态，无 Rng）。</summary>
public static class WarPolicies
{
    public static readonly IWarPolicy Default = new DefaultWarPolicy();
    public static readonly IWarPolicy State = new StateWarPolicy();

    /// <summary>按实体政治体取策略：至尊酋长且正式国家（StateId==Id 且 Size≥2）→ State；其余 → Default。</summary>
    public static IWarPolicy Of(Band e) =>
        e != null && !e.Dead && e.IsChief && e.StateId == e.Id && e.StateSize >= 2 ? State : Default;
}
