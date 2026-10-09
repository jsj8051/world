
namespace World.CivSim.Policies;

/// <summary>
/// 边境冲突策略（策略模式——机制内对象差异多态，2026-08-23 概念 = 机制组合 Phase 1）。
/// 同一 ConflictModel 对不同政治体对象的行为差异（内部秩序/继承豁免）由策略实现承载，
/// 机制体内零"if 你是酋邦 else 你是国家"分支——查表 ConflictPolicies.Of 取策略。
/// </summary>
public interface IConflictPolicy
{
    /// <summary>同政治体内部冲突倍率（内部秩序——Weber 强制力垄断；默认 1 = 无整合）。</summary>
    float InternalMult { get; }

    /// <summary>继承窗口豁免（同国不内战——王朝制度化，Kirch；默认 false）。</summary>
    bool SuccessionExempt { get; }
}
