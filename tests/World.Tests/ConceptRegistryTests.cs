using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.CivSim;
using World.CivSim.Concepts;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Territory;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Mechanics.State;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;

namespace World.Tests;

/// <summary>
/// 概念配方表完整性测试（概念 = 机制组合 Phase 2，2026-08-23）。
/// 约束：Union = StoneAge 注册全集（21 机制）；机制无重无漏；Includes 展开正确；参数表键完整。
/// 这些断言保证"新机制挂配方表即自动纳入注册表"的架构不静默破坏演化行为。
/// </summary>
[TestFixture]
public class ConceptRegistryTests
{
    [Test]
    public void Union_Matches_StoneAge_RegisteredSet()
    {
        // 概念表 Union 的类型集合 ≡ StoneAge 注册的机制集合（21 个——行为基准）
        var union = ConceptRegistry.AllMechanismsUnion().Select(m => m.Type).ToHashSet();
        var stoneAge = CivModelRegistry.StoneAge().SortedModels().Select(m => m.GetType()).ToHashSet();
        Assert.That(union.SetEquals(stoneAge), Is.True,
            $"Union 与 StoneAge 不一致：仅 Union={string.Join(",", union.Except(stoneAge))}；仅 StoneAge={string.Join(",", stoneAge.Except(union))}");
        Assert.That(union.Count, Is.EqualTo(21), "机制全集应为 21（现状演化模型数）");
    }

    [Test]
    public void Union_Deduplicates_ReusedMechanisms()
    {
        // v2 自由配方：同一积木可被多配方直接引用（复用是特性——Conflict 在 band/chiefdom/state）。
        // 保证：Union 按类型去重后无重复（注册表每机制仅一份实例——否则 Order 链双跑分叉）。
        var union = ConceptRegistry.AllMechanismsUnion();
        var types = union.Select(m => m.Type).ToList();
        Assert.That(types.Count, Is.EqualTo(types.Distinct().Count()), "Union 应无重复机制类型");
        Assert.That(types.Count, Is.EqualTo(21), "Union 去重后 = 21（现状演化模型数）");
        // 直接声明允许复用：band 与 chiefdom 都直接声明 Conflict——Union 只保留一份
        var bandDirect = ConceptRegistry.Of("band").Mechanisms.Select(m => m.Type).ToHashSet();
        var chiefDirect = ConceptRegistry.Of("chiefdom").Mechanisms.Select(m => m.Type).ToHashSet();
        Assert.That(bandDirect.Contains(typeof(ConflictModel)), Is.True);
        Assert.That(chiefDirect.Contains(typeof(ConflictModel)), Is.True);
    }

    [Test]
    public void Tribe_Includes_BandMechanisms()
    {
        // 子配方引用展开：tribe.AllMechanisms ⊇ band.AllMechanisms（配方复用，非继承语义）
        var band = ConceptRegistry.Of("band");
        var tribe = ConceptRegistry.Of("tribe");
        var bandTypes = band.AllMechanisms().Select(m => m.Type).ToHashSet();
        var tribeTypes = tribe.AllMechanisms().Select(m => m.Type).ToHashSet();
        Assert.That(bandTypes.IsSubsetOf(tribeTypes), Is.True, "tribe 配方应包含 band 全部机制");
        Assert.That(tribeTypes.Count, Is.EqualTo(16), "tribe = band(10) + 自身(6) = 16");
    }

    [Test]
    public void Chiefdom_State_Expand_Transitively()
    {
        // 传递展开：state.AllMechanisms 应含 band 全部 + 全链新增（10+6+3+2 = 21）
        var state = ConceptRegistry.Of("state");
        var types = state.AllMechanisms().Select(m => m.Type).ToHashSet();
        Assert.That(types.Contains(typeof(OriginModel)), Is.True, "state 经子配方引用应含 band 的 Origin");
        Assert.That(types.Contains(typeof(StateModel)), Is.True);
        Assert.That(types.Contains(typeof(WarModel)), Is.True);
        Assert.That(types.Count, Is.EqualTo(21), "state 全链 = band(10)+tribe(6)+chiefdom(3)+state(2 新增) = 21");
    }

    [Test]
    public void Conflict_Reused_Across_Concepts()
    {
        // 同一积木多概念复用：Conflict 出现在 band/chiefdom/state 三个配方（差异在参数表）
        foreach (var name in new[] { "band", "chiefdom", "state" })
        {
            var def = ConceptRegistry.Of(name);
            Assert.That(def.AllMechanisms().Any(m => m.Type == typeof(ConflictModel)), Is.True,
                $"{name} 配方应含 Conflict（复用积木）");
        }
    }

    [Test]
    public void ParamTable_Keys_Complete()
    {
        // 参数表：chiefdom/state 必须声明政体整合/贡赋/精英三键（冲突策略/贡赋策略消费的现状值来源）
        foreach (var name in new[] { "chiefdom", "state" })
        {
            var def = ConceptRegistry.Of(name);
            var keys = (def.Params ?? Array.Empty<(string, float)>()).Select(p => p.Key).ToHashSet();
            Assert.That(keys.Contains("conflict_internal_mult"), Is.True, $"{name} 参数表缺 conflict_internal_mult");
            Assert.That(keys.Contains("tribute_rate"), Is.True, $"{name} 参数表缺 tribute_rate");
            Assert.That(keys.Contains("elite_frac"), Is.True, $"{name} 参数表缺 elite_frac");
        }
        // 参数值 = 现状常量（行为基准；未来配方自定义参数时值可偏离常量）
        Assert.That(ConceptRegistry.ParamOf("state", "conflict_internal_mult"), Is.EqualTo(CivSimContext.StateInternalConflictMult));
        Assert.That(ConceptRegistry.ParamOf("state", "tribute_rate"), Is.EqualTo(CivSimContext.StateTributeRate));
        Assert.That(ConceptRegistry.ParamOf("chiefdom", "conflict_internal_mult"), Is.EqualTo(CivSimContext.InternalConflictMult));
        Assert.That(ConceptRegistry.ParamOf("unknown", "x", 42f), Is.EqualTo(42f), "未知名 → fallback");
    }
}
