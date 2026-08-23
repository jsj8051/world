using World.CivSim.Entities;

namespace World.CivSim.Policies;

/// <summary>
/// 军事战争策略（策略模式——机制内对象差异多态，2026-08-23 概念 = 机制组合 Phase 1）。
/// 现状（P1 拍板）：仅国家可宣战——策略区分国家/非国家；
/// 未来（Phase 2 概念配方）：村庄民兵 / 城防军 / 常备军 的军事强度差异在此接线（MilitaryMult）。
/// </summary>
public interface IWarPolicy
{
    /// <summary>宣战资格（默认 false——仅国家，P1 拍板）。</summary>
    bool CanDeclareWar(Band e);

    /// <summary>军事强度倍率（现状恒 1——MilitMult 由武器科技提供；未来村庄 0.3×/城防 1×/常备 2× 在此接线）。</summary>
    float MilitaryMult(Band e);
}
