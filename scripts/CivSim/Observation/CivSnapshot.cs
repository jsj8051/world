using System.Collections.Generic;

namespace World.CivSim.Observation;

/// <summary>
/// 观测快照（投影层 DTO，2026-08-24 用户拍板"观测面板→文明记录"）。
/// 面板的唯一数据源：`CivOverlay.Observe(ctx)` 纯函数组装——只读拷贝，类型安全，编译器护航。
/// 设计红线（docs/设计-观测面板与文明记录.md）：
///   · 模拟层永不依赖 UI；UI 永不直达模拟内部——面板只读本快照；
///   · 加字段 = Observe 一处 + 面板一行，任何一方修改不穿透另一层；
///   · 纯数据类，零 Godot 依赖——可进单元测试。
/// </summary>
public sealed class CivSnapshot
{
    /// <summary>演化终止 tick。</summary>
    public int Tick;

    /// <summary>总人口（四舍五入到人）。</summary>
    public long TotalPop;

    /// <summary>政体 / 国家（至尊酋长）/ 正式酋邦（内部落数≥2）计数。</summary>
    public int PolityCount, StateCount, ChiefdomCount;

    /// <summary>聚落数 / 战争数（进行中）。</summary>
    public int HabitationCount, WarCount;

    /// <summary>政体列表（声望降序；国家至尊酋长也在此列）。</summary>
    public List<PolityRow> Polities = new();

    /// <summary>国家卡片（仅正式国家至尊酋长——WarPolicies 同判据，单一事实源）。</summary>
    public List<StateRow> States = new();

    /// <summary>科技卷轴（techs.csv 全表 + 持有者数）。</summary>
    public List<TechRow> Techs = new();

    /// <summary>文明事件流（文明记录⑪——tick 升序；文本在投影层派生，面板零逻辑）。</summary>
    public List<EventRow> Events = new();
}

/// <summary>政体行（政权页/列表条目）。</summary>
public struct PolityRow
{
    /// <summary>实体 Id。</summary>
    public int Id;

    /// <summary>概念阶段标签（band/tribe/chiefdom/state——派生，与策略族同判据）。</summary>
    public string Concept;

    /// <summary>人口（人）。</summary>
    public float Pop;

    /// <summary>生产方式：true=务农（新石器）。</summary>
    public bool IsFarming;

    /// <summary>领地格数（TerritoryCells 重建存在时有效；0=未知/未建）。</summary>
    public int TerritoryCells;

    /// <summary>已获科技数。</summary>
    public int TechCount;

    /// <summary>声望（Sahlins 大人物资本）。</summary>
    public float Prestige;

    /// <summary>政治归属：酋邦 Id / 国家 Id（-1=无）。</summary>
    public int ChiefdomId, StateId;

    /// <summary>占据聚落 Id（-1=游动中）。</summary>
    public int PlaceId;

    /// <summary>主导文化群 key（份额最大项；无则空串）。</summary>
    public string CultureGroup;

    /// <summary>是否酋长（BigMan+祖先宗教——谱系合法性）。</summary>
    public bool IsChief;
}

/// <summary>国家卡片行（制度化国家——都城/君主/贡赋池/成员）。</summary>
public struct StateRow
{
    /// <summary>国家 Id = 至尊酋长实体 Id。</summary>
    public int Id;

    /// <summary>首都聚落 Id（-1=无——名义国家，实物缺位）。</summary>
    public int CapitalPlaceId;

    /// <summary>君主 = 国家内声望最高成员 Id（虚拟头衔，将来 Prestige 继位）。</summary>
    public int MonarchId;

    /// <summary>贡赋池（至尊酋长 Contributed——税制化征收累积）。</summary>
    public float Pool;

    /// <summary>成员数（ChiefdomCells 成员表）。</summary>
    public int MemberCount;

    /// <summary>已获科技数。</summary>
    public int TechCount;

    /// <summary>声望。</summary>
    public float Prestige;

    /// <summary>主导文化群 key。</summary>
    public string CultureGroup;

    /// <summary>是否处于战争（进行中的 War 涉及该国）。</summary>
    public bool IsAtWar;
}

/// <summary>科技卷轴行。</summary>
public struct TechRow
{
    /// <summary>科技 key（techs.csv）。</summary>
    public string Key;

    /// <summary>显示名（techs.csv name 列）。</summary>
    public string Name;

    /// <summary>持有政体数。</summary>
    public int Holders;
}

/// <summary>文明事件行（展示端文本在投影层已派生——面板零格式化逻辑）。</summary>
public struct EventRow
{
    /// <summary>演化 tick（展示 ×100 年）。</summary>
    public int Tick;

    /// <summary>事件类型索引（EventTypes 注册表序）。</summary>
    public int TypeIndex;

    /// <summary>展示文本（如 "国家 #2 向 #5 宣战"）。</summary>
    public string Text;
}