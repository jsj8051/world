using System;
using System.Collections.Generic;
using World.CivSim;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Territory;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Mechanics.State;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;

namespace World.CivSim.Concepts;

/// <summary>
/// 概念配方注册表（概念 = 机制组合，2026-08-23 拍板 v2 自由配方）。
/// 四概念配方单（声明序 = band → tribe → chiefdom → state；Union 推导按此序，确定性）：
///   band     = { Origin, GatherHunt(Harvest), Energy, Growth, SplitMigrate, Culture, Religion,
///                Trade, Conflict, Influence }                        —— 游群地基（10 机制）
///   tribe    = band ∪ { Cultivate, Territory, Mode, Invention, Spread, Settlement }   —— 农业部落
///   chiefdom = tribe ∪ { Prestige, Chiefdom, Absorption, Conflict(参数 0.5) }        —— 酋邦
///   state    = chiefdom ∪ { State, War, Conflict(参数 0.25) }                        —— 国家
/// 设计要点：
///   ① 机制积木不绑定概念层——Conflict 被 band/chiefdom/state 三配方复用（差异在参数表/策略，
///      不在机制本身）；Union 按类型去重 → 运行时仍一份实例（StoneAge 推导）。
///   ② 配方参数表（Params）是声明层 + 未来消费入口：值 = CivSimContext 现状常量（行为不变）；
///      未来"村庄+军队"等新配方 = 加一行 ConceptDef（military_mult 0.3 等参数自定义）。
///   ③ 游牧聚落/村庄/城市配方占位：需要 Herd 新积木 / Settlement 概念运行时状态——Q2 拍板暂不纳入，
///      架构天然支持（积木箱 + 配方单模型）。
/// </summary>
public static class ConceptRegistry
{
    // ── band 游群（根配方，无 Includes）──
    public static readonly ConceptDef Band = new()
    {
        Name = "band",
        Mechanisms = new (Type, Func<CivModelBase>)[]
        {
            (typeof(OriginModel), () => new OriginModel()),           // 起源播种（富饶区）
            (typeof(HarvestModel), () => new HarvestModel()),         // 采集狩猎（GatherHunt）
            (typeof(EnergyModel), () => new EnergyModel()),           // 能量核算
            (typeof(GrowthModel), () => new GrowthModel()),           // 人口增长
            (typeof(SplitMigrateModel), () => new SplitMigrateModel()), // 分裂 + 迁徙
            (typeof(CultureModel), () => new CultureModel()),         // 文化互动
            (typeof(ReligionModel), () => new ReligionModel()),       // 宗教演进（万物有灵起步）
            (typeof(TradeModel), () => new TradeModel()),             // 物物交换
            (typeof(ConflictModel), () => new ConflictModel()),       // 边境冲突（基础——无整合）
            (typeof(InfluenceModel), () => new InfluenceModel()),     // 影响力场（归属基础）
        },
        Params = Array.Empty<(string, float)>(),
    };

    // ── tribe 部落（农业定居：农田/领地/生产方式/发明/传播/聚落）──
    public static readonly ConceptDef Tribe = new()
    {
        Name = "tribe",
        Includes = new[] { "band" },
        Mechanisms = new (Type, Func<CivModelBase>)[]
        {
            (typeof(CultivateModel), () => new CultivateModel()),     // 农田开垦（土地挂钩）
            (typeof(TerritoryModel), () => new TerritoryModel()),     // 领地凝聚（连通分量）
            (typeof(ModeModel), () => new ModeModel()),               // 生产方式（猎↔农滞回）
            (typeof(InventionModel), () => new InventionModel()),     // 科技发明
            (typeof(SpreadModel), () => new SpreadModel()),           // 科技传播（邻格接触）
            (typeof(SettlementModel), () => new SettlementModel()),   // 聚落实体（定居点）
        },
        Params = Array.Empty<(string, float)>(),
    };

    // ── chiefdom 酋邦（声望/贡赋/庇护/继承/吞并 + Conflict 复用带整合参数）──
    public static readonly ConceptDef Chiefdom = new()
    {
        Name = "chiefdom",
        Includes = new[] { "tribe" },
        Mechanisms = new (Type, Func<CivModelBase>)[]
        {
            (typeof(PrestigeModel), () => new PrestigeModel()),       // 声望/贡赋/精英供养
            (typeof(ChiefdomModel), () => new ChiefdomModel()),       // 酋邦凝聚（庇护 BFS）
            (typeof(AbsorptionModel), () => new AbsorptionModel()),   // 吞并
            (typeof(ConflictModel), () => new ConflictModel()),       // 复用：同酋邦 ×0.5（酋长仲裁）
        },
        Params = new (string, float)[]
        {
            ("conflict_internal_mult", CivSimContext.InternalConflictMult),   // 内部秩序：酋长仲裁 ×0.5
            ("tribute_rate", CivSimContext.TributeRate),                      // 互惠贡赋 0.1
            ("elite_frac", CivSimContext.EliteFrac),                          // 精英供养 0.1
        },
    };

    // ── state 国家（制度化：都城/税制/继承制度化/战争 + Conflict 复用带国家参数）──
    public static readonly ConceptDef State = new()
    {
        Name = "state",
        Includes = new[] { "chiefdom" },
        Mechanisms = new (Type, Func<CivModelBase>)[]
        {
            (typeof(StateModel), () => new StateModel()),             // 国家涌现外壳（5 规范积木 AND → Assign）
            (typeof(WarModel), () => new WarModel()),                 // 战争外交（仅国家宣战——IWarPolicy）
            (typeof(ConflictModel), () => new ConflictModel()),       // 复用：同国家 ×0.25 + 王朝豁免
        },
        Params = new (string, float)[]
        {
            ("conflict_internal_mult", CivSimContext.StateInternalConflictMult),   // 内部秩序：强制力垄断 ×0.25
            ("tribute_rate", CivSimContext.StateTributeRate),                      // 税制化 0.2（×2）
            ("elite_frac", CivSimContext.StateEliteFrac),                          // 官僚供养 0.25（×2.5）
            ("war_military_mult", 1f),                                             // 常备军倍率（现状 1——未来常备军参数在此）
        },
    };

    /// <summary>全部概念（声明序——Union 推导与诊断遍历的确定性基准）。</summary>
    public static readonly ConceptDef[] All = { Band, Tribe, Chiefdom, State };

    /// <summary>按概念名取配方（未知名 → null）。</summary>
    public static ConceptDef Of(string name)
    {
        foreach (var c in All)
            if (c.Name == name) return c;
        return null;
    }

    /// <summary>全概念机制并集（注册表推导 StoneAge 用；按声明序 + 类型去重——确定性）。
    /// 新机制只需挂到任意配方 Mechanisms，本 Union 自动纳入注册表。
    /// visitedNames 每配方独立（防环检测不跨配方串扰——各配方 Includes 展开互不干扰）。</summary>
    public static List<(Type Type, Func<CivModelBase> Factory)> AllMechanismsUnion()
    {
        var result = new List<(Type, Func<CivModelBase>)>();
        var seen = new HashSet<Type>();
        foreach (var c in All)
            c.Collect(result, seen, new HashSet<string>());
        return result;
    }

    /// <summary>配方参数查询（键缺失 → fallback）。未来消费点（策略/机制）经此读参数——新配方自定义参数即生效。</summary>
    public static float ParamOf(string concept, string key, float fallback = 0f)
    {
        var def = Of(concept);
        if (def?.Params != null)
            foreach (var (k, v) in def.Params)
                if (k == key) return v;
        return fallback;
    }
}
