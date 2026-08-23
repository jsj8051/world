namespace World.CivSim.Entities;

/// <summary>
/// 聚集地形态（2026-08-23 聚集地统一推导：Camp/Settlement 不是并列类，是 Habitation 的形态）。
/// 互斥形态——单值判定：camp（营地：随迁/拆营/无粮仓无等级）↔ settlement（聚落：固定/粮仓/条件/废墟）。
/// </summary>
public enum HabitationKind
{
    /// <summary>营地（旧石器 band 宿营地形态——nomadic 概念落地时启用；随迁徙流动、拆营走人、无废墟）。</summary>
    Camp = 0,

    /// <summary>聚落（农业定居形态——现状唯一激活形态：固定格/粮仓/职能条件/废墟接管）。</summary>
    Settlement = 1,
}

/// <summary>
/// 聚集地实体（2026-08-23 聚集地统一：唯一聚集地实体，形态 = camp/settlement——用户推导
/// "聚集地 = 一个东西装配不同功能"；2026-08-23 功能定性：职能条件 = 资格门控）。
/// **物理场所**：独立于社会单元（Polity）存在——场所比人长寿。
/// 条件系统（用户拍板："有了什么条件才能做什么；集镇/城市也是一个条件"）：
///   · 基础形态条件：camp/settlement（KindOf——占据者生产方式涌现，不入档派生）
///   · 职能条件：HasAdmin（治理）/ HasMarket（市场）/ HasRitual（仪式）——**入档**（2026-08-23 修正 D1：
///     删除 Level 已迫使 v17 bump；且 Order 滞后机制（Growth 早于 HabitationModel）读条件需恢复——入档消除确定性分叉）
///   · 复合条件：IsMarketTown（集镇 = 市场或仪式条件）/ IsCity（城市 = 治理条件——"集镇/城市也是一个条件"，
///     门控行为：粮仓/增长/贸易/贡赋中枢）
/// 分区：本文件核心 + Habitation.Settlement.cs（聚落形态专属：粮仓）+ Habitation.Camp.cs（营地形态占位）。
/// 存档 v17（2026-08-23 功能定性：STTL 删 Level/LastLevelUpTick + 加 3 职能条件字节；段名 STTL = Settlement 遗产名）。
/// </summary>
public partial class Habitation
{
    /// <summary>分配器 NextHabitationId（确定性）。</summary>
    public int Id;

    /// <summary>所在格（**camp 形态随部落迁移更新；settlement 形态固定**——现状恒固定）。</summary>
    public int Cell;

    /// <summary>形成 tick（场所连续性起点：camp→settlement 形态演化不重置）。</summary>
    public int BornTick;

    /// <summary>当前占据者定居起点 tick（= 占据 Polity SettledSince）。</summary>
    public int DwellFrom;

    /// <summary>占据 Polity Id（-1=废墟/空置）。</summary>
    public int OccupantId = -1;

    /// <summary>废墟起点 tick（-1=非废墟；v1 不做废墟衰变——考古 mound 语义）。</summary>
    public int RuinFrom = -1;

    /// <summary>治理职能条件（入档——2026-08-23 功能定性）：占据者为酋邦中心/国家都城（D2 拍板：
    /// 历史上酋邦中心就是城市——乌尔前期/杰里科酋长中心，故 HasAdmin 含酋邦中心 + 都城）。
    /// 派生缓存（HabitationModel 每 tick 重算）——读档恢复 + 重算覆盖（确定性无分叉）。</summary>
    public bool HasAdmin;

    /// <summary>市场职能条件（入档）：占据者为商路节点（贸易伙伴 ≥ 2——TradeModel 每 tick 扫描统计写入）。
    /// 条件成立 → 集镇资格（IsMarketTown 之一）。</summary>
    public bool HasMarket;

    /// <summary>仪式职能条件（入档）：占据者宗教多教汇聚（主导派别份额 < 阈值——圣地涌现）。
    /// 条件成立 → 集镇资格（IsMarketTown 之一）。</summary>
    public bool HasRitual;

    /// <summary>复合条件：集镇资格（"集镇也是一个条件"——市场或仪式职能任一成立）。门控集镇级行为（贸易吞吐等）。</summary>
    public bool IsMarketTown => HasMarket || HasRitual;

    /// <summary>复合条件：城市资格（"城市也是一个条件"——治理职能成立；历史上存在什么就是什么）。
    /// 门控城市级行为（贡赋中枢/战争要塞/未来城墙）。</summary>
    public bool IsCity => HasAdmin;

    /// <summary>城镇级（收益系数——替代旧 Level：村庄 0 / 集镇 1 / 城市 2；粮仓 ×(1+0.5×级)、增长 ×(1+0.25×级)）。</summary>
    public int TownTier => IsCity ? 2 : IsMarketTown ? 1 : 0;

    /// <summary>是否废墟（无占据者）。</summary>
    public bool IsRuin => OccupantId < 0;

    /// <summary>
    /// 形态判定（派生——不入档；2026-08-23 概念=涌现：形态由占据者生产方式涌现）。
    /// 纯函数：占位者是否务农驱动形态——农 → settlement（固定聚落）；游群 → camp（宿营地）。
    /// 废墟 = settlement 形态历史遗产（camp 形态拆营走人——无废墟）。
    /// 现状注意：camp 形态行为（随迁）待 nomadic 概念落地接线——当前占据者恒为务农 → 恒 settlement。
    /// </summary>
    public HabitationKind KindOf(bool occupantIsFarming) =>
        RuinFrom >= 0 || occupantIsFarming ? HabitationKind.Settlement : HabitationKind.Camp;
}