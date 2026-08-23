using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Policies;

/// <summary>默认冲突策略：无政治整合（跨邦），继承窗口无豁免。</summary>
public sealed class DefaultConflictPolicy : IConflictPolicy
{
    public float InternalMult => 1f;
    public bool SuccessionExempt => false;
}

/// <summary>酋邦冲突策略：酋长仲裁（内部倍率 ×InternalConflictMult），继承窗口不豁免（继承战争）。</summary>
public sealed class ChiefdomConflictPolicy : IConflictPolicy
{
    public float InternalMult => CivSimContext.InternalConflictMult;
    public bool SuccessionExempt => false;
}

/// <summary>国家冲突策略：内部秩序（×StateInternalConflictMult——强制力垄断）+ 王朝继承豁免。</summary>
public sealed class StateConflictPolicy : IConflictPolicy
{
    public float InternalMult => CivSimContext.StateInternalConflictMult;
    public bool SuccessionExempt => true;
}

/// <summary>冲突策略查表工厂（2026-08-23；确定性——查 StateId/ChiefdomId 派生状态，无 Rng、无 switch 链）。</summary>
public static class ConflictPolicies
{
    public static readonly IConflictPolicy Default = new DefaultConflictPolicy();
    public static readonly IConflictPolicy Chiefdom = new ChiefdomConflictPolicy();
    public static readonly IConflictPolicy State = new StateConflictPolicy();

    /// <summary>按双方政治体关系取策略：同国家 → State（内部秩序+王朝豁免）；同酋邦 → Chiefdom；其余 → Default。</summary>
    public static IConflictPolicy Of(Band a, Band b)
    {
        if (a.ChiefdomId >= 0 && a.ChiefdomId == b.ChiefdomId)
        {
            if (a.StateId >= 0 && a.StateId == b.StateId) return State;
            return Chiefdom;
        }
        return Default;
    }
}
