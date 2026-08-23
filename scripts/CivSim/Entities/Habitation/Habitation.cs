namespace World.CivSim.Entities;

/// <summary>
/// 聚集地形态（2026-08-23 聚集地统一推导：Camp/Settlement 不是并列类，是 Habitation 的形态）。
/// 互斥形态——单值判定：camp（营地：随迁/拆营/无粮仓无等级）↔ settlement（聚落：固定/粮仓/等级/废墟）。
/// </summary>
public enum HabitationKind
{
    /// <summary>营地（旧石器 band 宿营地形态——nomadic 概念落地时启用；随迁徙流动、拆营走人、无废墟）。</summary>
    Camp = 0,

    /// <summary>聚落（农业定居形态——现状唯一激活形态：固定格/粮仓/等级 0-3/废墟接管）。</summary>
    Settlement = 1,
}

/// <summary>
/// 聚集地实体（2026-08-23 聚集地统一：原 Settlement 类正名为 Habitation——唯一聚集地实体，
/// 形态 = camp/settlement（见 HabitationKind）；用户推导"聚集地 = 一个东西装配不同功能"）。
/// **物理场所**：独立于社会单元（Polity）存在——场所比人长寿。
/// 形态（2026-08-23 拍板）：camp = 随部落流动；settlement = 固定格（现状唯一激活——nomadic 未落地）。
/// 分区：本文件核心 + Habitation.Settlement.cs（聚落形态专属）+ Habitation.Camp.cs（营地形态占位）。
/// 存档 v13 新段（STTL——段名 = Settlement 遗产名，格式标识不改）；旧档（v12）无聚集地。
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