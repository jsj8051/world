using System.Collections.Generic;

namespace World.CivSim;

/// <summary>
/// 文明演化模型统一抽象基类（唯一基类 + 注册表）。
/// v4 纯实体模型：每个机制 = 一个模型，按 Order 每 tick 执行（docs/石器时代设计.md §二）。
/// </summary>
public abstract class CivModelBase
{
    public abstract string Name { get; }
    public abstract int Order { get; }
    public abstract void Execute(CivSimContext ctx);
}

/// <summary>机制注册表（v4 石器时代：9 模型，Order 0-80）。</summary>
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
            .Register(new ConflictModel())      // 边境冲突（Order 75，2026-08-10）：粘性僵局暴力出口
            .Register(new SplitMigrateModel());
    }
}

// ══════════════════════════════════════════════════════════════════
// 职责分片索引（2026-08-19 纯重构拆分：CivModels.cs 一文件一类；行为不变）。
// 本文件仅保留抽象基类 CivModelBase + 注册表 CivModelRegistry（StoneAge 构建）。
// 各演化模型类已拆至 scripts/CivSim/ 下独立文件（文件名 = 类名.cs）：
//   CultivateModel.cs  ①a 农田开垦（Order 6）
//   InfluenceModel.cs  ①b 影响力场（Order 8）
//   HarvestModel.cs    ①c 采集收获（Order 9）
//   OriginModel.cs     ①  起源播种（Order 0）
//   EnergyModel.cs     ②  能量核算（Order 10）
//   GrowthModel.cs     ③  人口增长（Order 20）
//   PrestigeModel.cs   ①g 声望积累（Order 25，酋长/贡赋/精英供养）
//   ModeModel.cs       ④  生产方式选择（Order 30）
//   InventionModel.cs  ⑤  科技发明（Order 40）
//   TerritoryModel.cs  ⑤  领地凝聚（Order 45）
//   ChiefdomModel.cs   ①h 酋邦凝聚（Order 46）
//   AbsorptionModel.cs ①i 吞并（Order 47）
//   SettlementModel.cs ①j 聚落（Order 48）
//   StateModel.cs      ①k 国家涌现（Order 49）
//   SpreadModel.cs     ⑥  科技传播（Order 50）
//   TradeModel.cs      ⑳  物物交换（Order 55）
//   CultureModel.cs    ⑦  文化互动（Order 60）
//   ReligionModel.cs   ⑧  宗教演进（Order 70）
//   ConflictModel.cs   ⑨  边境冲突（Order 75）
//   SplitMigrateModel.cs ⑨ 分裂/迁徙（Order 80）
// ══════════════════════════════════════════════════════════════════
