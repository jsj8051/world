using System;
using System.Collections.Generic;

namespace World.CivSim;

/// <summary>能力开关系统（2026-08-09 用户拍板：查询式——解锁条件声明集中，效果留模型）。
/// 加新能力 = Register 一条（条件 lambda，可组合科技/状态/环境）；模型查 Has(ctx, e, id) → O(1) 位测试
/// （CapMask 每 tick 缓存，RefreshCellState）。上限 32 能力（uint 位图）。</summary>
public static class CapabilityTable
{
    public sealed class Capability
    {
        public string Id;
        public Func<CivEntity, CivSimContext, bool> Unlocked;
    }

    private static readonly List<Capability> _caps = new();
    private static readonly Dictionary<string, uint> _bits = new();
    private static bool _inited;

    public static void Register(Capability cap)
    {
        if (_bits.ContainsKey(cap.Id)) throw new InvalidOperationException($"重复能力 id: {cap.Id}");
        if (_caps.Count >= 32) throw new InvalidOperationException("能力数超 32 上限（uint 位图）");
        _bits[cap.Id] = 1u << _caps.Count;
        _caps.Add(cap);
    }

    /// <summary>惰性初始化（首查时注册内置能力；幂等）。</summary>
    private static void EnsureInited()
    {
        if (_inited) return;
        _inited = true;
        Register(new Capability { Id = "canoe",     Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Canoe) });
        Register(new Capability { Id = "microlith", Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Microlith) });
        Register(new Capability { Id = "grinding",  Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Grinding) });
        Register(new Capability { Id = "fire",      Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Fire) });
        Register(new Capability { Id = "clothing",  Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Clothing) });
        Register(new Capability { Id = "seed",      Unlocked = (e, c) => TechTable.HeldSeeds(e.TechKeys).Count > 0 });
        Register(new Capability { Id = "storage",   Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Storage) });
        Register(new Capability { Id = "livestock", Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Livestock)
            && c.Grid.EnsureWildLivestock()[e.Cell] != 0 });
        Register(new Capability { Id = "pottery",   Unlocked = (e, c) => e.TechKeys.Contains(TechTable.Pottery) });
        // 定居 = 农业派生（2026-08-17 用户拍板"定居+存储"：谷物农业需守田 → 定居，史实；非科技——
        //   无"发明"事件，转农即定居；旧石器（无农）天然无定居）
        Register(new Capability { Id = "settle",    Unlocked = (e, c) => e.IsFarming });
    }

    /// <summary>实体能力位掩码（RefreshCellState 每 tick 缓存；条件含环境——同 tick 内环境稳定）。</summary>
    public static uint MaskOf(CivSimContext ctx, CivEntity e)
    {
        EnsureInited();
        uint mask = 0;
        for (int i = 0; i < _caps.Count; i++)
            if (_caps[i].Unlocked(e, ctx)) mask |= 1u << i;
        return mask;
    }

    public static bool Has(CivSimContext ctx, CivEntity e, string id)
    {
        EnsureInited();
        // 惰性兜底：CapMask 未缓存（手动构造场景/未跑 RefreshCellState）时即时算一次并回填——
        //   演化中 RefreshCellState 每 tick 已全量算，此处仅在未初始化时触发（确定性：同输入同掩码）
        if (e.CapMask == 0) e.CapMask = MaskOf(ctx, e);
        return _bits.TryGetValue(id, out uint bit) && (e.CapMask & bit) != 0;
    }

    /// <summary>诊断：能力 id 全集（T26 完整性断言用）。</summary>
    public static IReadOnlyList<string> AllIds()
    {
        EnsureInited();
        var r = new List<string>();
        foreach (var c in _caps) r.Add(c.Id);
        return r;
    }
}
