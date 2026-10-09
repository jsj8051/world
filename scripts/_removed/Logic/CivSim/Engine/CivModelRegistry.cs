using System.Collections.Generic;

using World.CivSim.Concepts;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Territory;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Mechanics.State;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;

namespace World.CivSim;

/// <summary>
/// 机制注册表（v4 纯实体模型：每机制一个模型，Order 0-80）。
/// ⚠️ 2026-08-23 概念 = 机制组合 Phase 2：StoneAge 构建 = ConceptRegistry 概念配方表 Union 推导
///   （band ∪ tribe ∪ chiefdom ∪ state = 22 机制，声明序 + 类型去重）——新机制挂配方即自动纳入。
/// </summary>
public sealed class CivModelRegistry
{
    private readonly List<CivModelBase> _models = new();

    public CivModelRegistry Register(CivModelBase m) { _models.Add(m); return this; }

    public void ExecuteAll(CivSimContext ctx)
    {
        foreach (var m in SortedModels())
            m.Execute(ctx);
    }

    /// <summary>按 Order 排序后的模型列表（诊断逐模型执行用；幂等排序）。</summary>
    public IReadOnlyList<CivModelBase> SortedModels()
    {
        _models.Sort((a, b) => a.Order.CompareTo(b.Order));
        return _models;
    }

    /// <summary>
    /// 机制注册表构建（2026-08-23 概念 = 机制组合 Phase 2）：由 ConceptRegistry 配方表 Union 推导——
    /// 全概念机制并集（band ∪ tribe ∪ chiefdom ∪ state，按声明序 + 类型去重）。
    /// 新机制只需挂到任意概念配方的 Mechanisms，本方法自动纳入；Order 排序仍由 SortedModels 保证。
    /// ⚠️ 行为不变：Union 结果 ≡ 旧硬编码 22 模型列表（诊断逐模型执行/性能历史同基准）。
    /// </summary>
    public static CivModelRegistry StoneAge()
    {
        var reg = new CivModelRegistry();
        foreach (var (_, factory) in ConceptRegistry.AllMechanismsUnion())
            reg.Register(factory());
        return reg;
    }
}
