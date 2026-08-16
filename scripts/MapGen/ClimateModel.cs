using Godot;
using System;
using System.Text;

namespace World.MapGen;

/// <summary>
/// 气候系统抽象模型注册表（2026-08-16 用户拍板）。
///
/// 统一基类：ModelBase（名称/量级/验证/依赖）——场（FieldBase）和环（LoopBase）
/// 都是它的角色子类，继承同一个基类。本类 = 单一注册表：场+环混合注册，
/// Verify() 多态统一遍历（一个数组、一种遍历、一套输出）。
///
/// 不变量：
///   1. 流水线单向：每场只算一次（闭环修正都在源头场入口，下游结构不变）
///   2. 复杂度 O(环数)：环与环不嵌套，每环独立决策、独立验证
///   3. 封版判据：环清单完备 + 每环有决策 → 复杂度冻结
/// 新增因素先查 Models：已处理的环不重复加，低于精度的环不加。
/// </summary>
public static class ClimateModel
{
    /// <summary>
    /// 气候模型注册表（Stage1 气候场 + 全流水线场 + 环）。
    /// 场绑定 PlanetPipeline 数据（构造传 pipe），环是决策实例。
    /// </summary>
    public static Model.ModelBase[] Models(PlanetPipeline pipe) => new Model.ModelBase[]
    {
        // ── 气候场（Stage1，IFieldRole）──
        new Model.ElevationField(pipe),
        new Model.ErosionDepositionField(pipe),   // 侵蚀堆积（板块，诊断场）
        new Model.ContinentalShelfField(pipe),    // 大陆架平台（2026-08-18：近岸 ≤4 跳 -150m，4-6 坡——被动大陆边缘）
        new Model.IceSheetField(pipe),            // 冰盖（2026-08-18：极地海→冰盖陆地——R=0 无生产力但可通行）
        new Model.TemperatureField(pipe),         // 气候基准（静态，月公式 base）
        new Model.PrecipField(pipe),              // 年降水估算（湿润降温用；月降水后覆盖为 Σ月）
        new Model.AnnualTempField(pipe),          // 年均温 = mean(月温度)（月→年涌现）
        new Model.MonthTempField(pipe),
        new Model.UnifiedWindField(pipe),
        new Model.MonthPrecipField(pipe),
        new Model.CurrentField(pipe),
        new Model.MonsoonField(pipe),
        new Model.BiomeField(pipe),
        // ── 全流水线场（Stage2/4/5，IFieldRole）──
        new Model.RiverField(pipe),
        new Model.LakeField(pipe),
        new Model.MineralField(pipe),
        new Model.SoilField(pipe),
        // ── 环（ILoopRole）──
        new Model.WetCoolingLoop(pipe),  // Closed（Apply 已实现）
        new Model.ThermalCurrentLoop(),  // Closed
        new Model.IceAlbedoLoop(),       // Cut
        new Model.VegetationLoop(),      // Cut
        new Model.CloudRadiationLoop(),  // Ignored
        new Model.GreenhouseLoop(),      // Ignored
        new Model.ErosionLoop(),         // Cut
        new Model.CurrentPrecipLoop(),   // Cut
    };

    /// <summary>
    /// 全流水线执行（2026-08-16 抽象框架迁移）：依赖拓扑排序 → 场 Compute + 环 Apply → 校验。
    /// PlanetPipeline.Run 只注入环境（Sim/P/Grid/ENorm/Climate…），计算全部由这里驱动。
    /// </summary>
    public static void Run(PlanetPipeline pipe, Action<float> onProgress = null)
    {
        var models = Models(pipe);
        var order = TopoSort(models);
        int total = Math.Max(1, order.Length);
        int i = 0;
        foreach (var m in order)
        {
            if (m is Model.IFieldRole f) f.Compute();
            else if (m is Model.ILoopRole lr) lr.Apply();   // Closed 环执行；Cut/Ignored no-op
            onProgress?.Invoke((i + 1f) / total);   // 每场/环完成上报（进度条不再死停在管线 0%）
            i++;
        }
        ValidateAll(pipe);
    }

    /// <summary>Kahn 拓扑排序（依赖名 = 被依赖场/环的 Name；环存在 → 抛错防静默错序）。</summary>
    private static Model.ModelBase[] TopoSort(Model.ModelBase[] models)
    {
        var byName = new System.Collections.Generic.Dictionary<string, Model.ModelBase>();
        foreach (var m in models) byName[m.Name] = m;
        var indeg = new System.Collections.Generic.Dictionary<Model.ModelBase, int>();
        var dependents = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Model.ModelBase>>();
        foreach (var m in models)
        {
            indeg[m] = 0;
            foreach (var d in m.DependsOn())
            {
                if (!byName.ContainsKey(d))
                    throw new InvalidOperationException($"[ClimateModel] 依赖名未注册：'{d}'（{m.Name} 依赖）");
                indeg[m]++;
                if (!dependents.ContainsKey(d)) dependents[d] = new System.Collections.Generic.List<Model.ModelBase>();
                dependents[d].Add(m);
            }
        }
        var queue = new System.Collections.Generic.Queue<Model.ModelBase>();
        foreach (var m in models) if (indeg[m] == 0) queue.Enqueue(m);
        var order = new System.Collections.Generic.List<Model.ModelBase>();
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            order.Add(m);
            if (dependents.TryGetValue(m.Name, out var deps))
                foreach (var d in deps)
                    if (--indeg[d] == 0) queue.Enqueue(d);
        }
        if (order.Count != models.Length)
            throw new InvalidOperationException($"[ClimateModel] 依赖环：{models.Length - order.Count} 个模型未排序（检查 DependsOn 成环）");
        return order.ToArray();
    }

    /// <summary>
    /// 全流水线校验（Run 末尾调用）：全部模型（13 场 + 8 环）。
    /// </summary>
    public static void ValidateAll(PlanetPipeline pipe)
    {
        Print(pipe, "全流水线模型");
    }

    private static void Print(PlanetPipeline pipe, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ClimateModel] {title}状态（对照 docs/气候反馈环.md）：");
        foreach (var m in Models(pipe))
            sb.AppendLine($"  {m.ToString(),-50} {(m.Verify() ? "✅" : "⚠️")}");
        GD.Print(sb.ToString().TrimEnd());
        int closed = 0, cut = 0, ignored = 0, fields = 0;
        foreach (var m in Models(pipe))
        {
            if (m is Model.ILoopRole l)
            {
                if (l.Status == "Closed") closed++;
                else if (l.Status == "Cut") cut++;
                else ignored++;
            }
            else fields++;
        }
        GD.Print($"[ClimateModel] 模型 {fields} 场 + {closed + cut + ignored} 环（Closed {closed} / Cut {cut} / Ignored {ignored}）— 封版（2026-08-16）");
    }
}
