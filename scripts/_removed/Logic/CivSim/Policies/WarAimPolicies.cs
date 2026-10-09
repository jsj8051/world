using World.CivSim.Entities;
using World.CivSim.Mechanics.Military;

namespace World.CivSim.Policies;

/// <summary>默认开战动机策略：非国家——无宣战动机（兜底；非国家本就不进 DeclareWars 国家集合）。</summary>
public sealed class DefaultWarAimPolicy : IWarAimPolicy
{
    public float AimMult(CivSimContext ctx, Polity a, Polity b) => 0f;
}

/// <summary>国家开战动机策略：动机门（领土/压力/优势/世仇——WarAims 纯函数）× 关系调节 × 实力门槛。
/// 该打的才打（用户拍板 2026-08-23）：无动机 → 0（不开战）；有动机 → 亲缘/贸易降战意、仇恨升战意、
/// 弱方不敢打（生存压力豁免）。</summary>
public sealed class StateWarAimPolicy : IWarAimPolicy
{
    public float AimMult(CivSimContext ctx, Polity a, Polity b)
    {
        if (!WarAims.HasAnyMotive(ctx, a, b)) return 0f;
        return WarAims.RelationMult(ctx, a, b) * WarAims.PowerGapMult(ctx, a, b);
    }
}

/// <summary>开战动机策略查表工厂（2026-08-23；确定性——按 a（挑战方）政体派生状态，无 Rng）。</summary>
public static class WarAimPolicies
{
    public static readonly IWarAimPolicy Default = new DefaultWarAimPolicy();
    public static readonly IWarAimPolicy State = new StateWarAimPolicy();

    /// <summary>按挑战方政治体取策略：至尊酋长且正式国家（StateId==Id 且 Size≥2）→ State；其余 → Default。</summary>
    public static IWarAimPolicy Of(Polity a, Polity b) =>
        a != null && !a.Dead && a.IsChief && a.StateId == a.Id && a.StateSize >= 2 ? State : Default;
}
