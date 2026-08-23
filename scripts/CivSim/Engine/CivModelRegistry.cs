using System.Collections.Generic;

using World.CivSim.Entities;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Territory;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Mechanics.State;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;

namespace World.CivSim;

/// <summary>
/// 机制注册表（v4 石器时代：24 模型，Order 0-80）。
/// ⚠️ 2026-08-23 概念 = 机制组合：StoneAge 构建 = band 配方的默认机制集（Phase 2 将按概念配方分层注册）。
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

    public static CivModelRegistry StoneAge()
    {
        return new CivModelRegistry()
            .Register(new OriginModel())
            .Register(new CultivateModel())     // 农田开垦（Order 6，2026-08-17 土地挂钩）
            .Register(new InfluenceModel())     // 归属 = argmax(P×M×w(d))，粘性 1.15
            .Register(new HarvestModel())       // 领地采集（静态丰度×土地×劳动力）→ FLast
            .Register(new EnergyModel())
            .Register(new GrowthModel())
            .Register(new PrestigeModel())      // 声望/酋长（Order 25，2026-08-17 酋邦层）
            .Register(new ModeModel())
            .Register(new InventionModel())
            .Register(new SpreadModel())
            .Register(new TradeModel())      // 物物交换（Order 55，2026-08-18 阶段3 贸易期——Spread 与 Culture 之间）
            .Register(new CultureModel())
            .Register(new ReligionModel())
            .Register(new TerritoryModel())     // 领地凝聚（Order 45，2026-08-17 注册修复：此前从未注册进演化——
                                                //   TerritoryId/Size 全 -1 → 科技传播领地加成失效 + 酋邦永不凝聚）
            .Register(new ChiefdomModel())      // 酋邦凝聚（Order 46，2026-08-17 酋邦层）
            .Register(new AbsorptionModel())    // 吞并（Order 47，2026-08-17 用户拍板：驻扎格被覆盖→并入/迁走）
            .Register(new SettlementModel())    // 聚落（Order 48，2026-08-19 阶段3 聚落设计——场所实体）
            .Register(new StateModel())         // 国家涌现（Order 49，2026-08-16 阶段4——酋邦制度化，docs/阶段4设计-国家涌现.md）
            .Register(new WarModel())           // 军事战争（Order 51，2026-08-19 阶段5——战争=外交状态，docs/阶段5设计-军事征服.md）
            .Register(new ConflictModel())      // 边境冲突（Order 75，2026-08-10）：粘性僵局暴力出口
            .Register(new SplitMigrateModel());
    }
}
