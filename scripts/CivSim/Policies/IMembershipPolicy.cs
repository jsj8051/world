using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Policies;

/// <summary>
/// 酋邦成员归属策略（策略模式——机制内对象差异多态，2026-08-23 概念 = 机制组合 Phase 1）。
/// ChiefdomModel ④ 归属分配对三类对象的差异（被征服效忠 / 酋长自中心 / 自由成员庇护人）
/// 由策略实现承载，机制体内零身份 if-else 链——查表 MembershipPolicies.Of 取策略后多态 Assign。
/// </summary>
public interface IMembershipPolicy
{
    /// <summary>执行归属分配：返回本实体归入的酋长 Id（-1 = 独立）。副作用：写 ChiefdomId 等归属字段。</summary>
    int Assign(CivSimContext ctx, Polity e, Polity[] byId, int[] bestChief);
}
