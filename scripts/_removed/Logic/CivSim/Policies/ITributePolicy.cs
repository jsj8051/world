
namespace World.CivSim.Policies;

/// <summary>
/// 贡赋策略（策略模式——机制内对象差异多态，2026-08-23 概念 = 机制组合 Phase 1）。
/// PrestigeModel 对酋邦/国家的贡赋与精英供养差异（税制化 ×2 / 官僚化 ×2.5）由策略实现承载。
/// </summary>
public interface ITributePolicy
{
    /// <summary>贡赋率（税 vs 互惠贡赋——国家税制化 ×2；Earle 实物税）。</summary>
    float TributeRate { get; }

    /// <summary>酋长精英供养比例（国家官僚化 ×2.5——官僚体系更庞大）。</summary>
    float EliteFrac { get; }
}
