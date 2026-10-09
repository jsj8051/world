using System;
using System.Collections.Generic;
using World.CivSim;

namespace World.CivSim.Concepts;

/// <summary>
/// 概念配方定义（概念 = 机制组合，2026-08-23 拍板 v2 自由配方）。
/// 概念 = 配方单：显式选择机制积木（Mechanisms）+ 子配方引用（Includes——复用其他配方的
/// 机制集，非继承语义）+ 参数表（Params——机制消费的参数入口）。
/// 设计要点（见 桌面 概念机制组合设计.html）：
///   ① 机制积木不绑定任何概念层——同一积木可被任意配方引用（如 Conflict 在 band/chiefdom/state 都出现，
///      差异在参数/策略，不在机制本身）；
///   ② 无继承链：Includes 是配方引用（复用），概念不含下层的"全部"——每个配方显式列出自己的机制；
///   ③ 贫血模型（P6）：本表只声明"概念含哪些机制"，运行时机制照旧每 tick 独立执行、
///      由实体状态判适用条件 + 策略查表——配方不做运行时全局分派，band 异步演化不变。
/// </summary>
public sealed class ConceptDef
{
    /// <summary>概念名（配方单标识：band / tribe / chiefdom / state；未来村庄/城市/游牧聚落在此扩展）。</summary>
    public string Name;

    /// <summary>本配方直接引用的机制积木（具体类型 + 工厂；可跨配方复用——同一积木多概念引用）。</summary>
    public (Type Type, Func<CivModelBase> Factory)[] Mechanisms;

    /// <summary>子配方引用（复用其他配方的机制集；band 为根无 Includes）。无继承语义——只是机制集展开。</summary>
    public string[] Includes;

    /// <summary>配方参数表（机制消费入口；键 = 参数名，值 = CivSimContext 常量引用——现状值，未来配方可自定义）。</summary>
    public (string Key, float Value)[] Params;

    /// <summary>全部机制 = Includes 递归展开 ∪ 自身（按声明序按类型去重——确定性，与遍历序无关）。
    /// 展开结果供注册表推导（StoneAge Union）与诊断/测试断言。</summary>
    public List<(Type Type, Func<CivModelBase> Factory)> AllMechanisms()
    {
        var result = new List<(Type, Func<CivModelBase>)>();
        var seen = new HashSet<Type>();
        Collect(result, seen, new HashSet<string>());
        return result;
    }

    /// <summary>递归收集机制（Includes 先展开——子配方机制在前，自身在后；按具体类型去重）。
    /// internal：ConceptRegistry.AllMechanismsUnion 复用同一收集器（确定性同源）。
    /// visitedNames：配方名防循环引用（配方 Includes 自环/互环 → 静默跳过，不无限递归）。</summary>
    internal void Collect(List<(Type Type, Func<CivModelBase> Factory)> result, HashSet<Type> seen, HashSet<string> visitedNames)
    {
        if (Includes != null)
            foreach (var sub in Includes)
            {
                if (!visitedNames.Add(sub)) continue;   // 已展开过该配方（防环）
                ConceptRegistry.Of(sub)?.Collect(result, seen, visitedNames);
            }
        if (Mechanisms == null) return;
        foreach (var m in Mechanisms)
        {
            if (!seen.Add(m.Type)) continue;
            result.Add(m);
        }
    }
}
