
using World.CivSim.Entities;

namespace World.CivSim.Policies;

/// <summary>
/// 开战动机策略（策略模式——对象差异多态，2026-08-23 战争结算 v2）。
/// 同一 DeclareWars 对不同政治体对象的开战倾向差异由策略实现承载（现状：国家=标准动机门；
/// 未来"和平主义村庄"等新概念 = 加实现返回 0）——机制体内零"if 你是国家 else"分支。
/// 查表 WarAimPolicies.Of(a, b)（对级策略——动机是两国关系判定，同 ConflictPolicies.Of(a,b) 模式）。
/// </summary>
public interface IWarAimPolicy
{
    /// <summary>开战概率乘数（0 = 无动机不打；>0 = 动机门过 + 关系/实力调节）。纯函数无 Rng。</summary>
    float AimMult(CivSimContext ctx, Polity a, Polity b);
}
