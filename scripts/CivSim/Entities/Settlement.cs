namespace World.CivSim.Entities;

/// <summary>
/// 聚落实体（2026-08-19 阶段3 聚落设计，docs/阶段3设计-聚落实体.md）。
/// **物理场所**：独立于社会单元（Band）存在——农业部落（settle）的驻扎点固化为聚落；
/// 场所比人长寿：部落迁徙/灭绝 → 聚落留为废墟（OccupantId=-1），新部落迁入可接管继承等级。
/// 等级 0=新村/营地 1=村庄 2=城镇 3=城市（Dwell×P 阈值驱动，确定性纯函数——读档续跑无分叉）。
/// 粮仓（Stocks）是聚落属性（用户拍板"存粮迁移到聚落"）：容量 = settle 容量 ×(1+0.5×Level)。
/// 存档 v13 新段；旧档（v12）无聚落（仅新演化生成——用户拍板）。
/// </summary>
public sealed class Settlement
{
    public int Id;                 // 分配器 NextSettlementId（确定性）
    public int Cell;               // 所在格（**固定**——聚落不迁移）
    public int BornTick;           // 形成 tick
    public int Level;              // 0=新村/营地 1=村庄 2=城镇 3=城市
    public int LastLevelUpTick;    // 最近升级 tick（等级冷却，防跳级抖动）
    public int DwellFrom;          // 当前占据者定居起点 tick（= 占据部落 SettledSince）
    public int OccupantId = -1;    // 占据部落 Id（-1=废墟/空置）
    public int RuinFrom = -1;      // 废墟起点 tick（-1=非废墟；v1 不做废墟衰变——考古 mound 语义）
    public float[] Stocks = CommodityTable.NewStocks();   // 粮仓（场所属性；索引 = CommodityTable.Index(id)）

    /// <summary>是否废墟（无占据者）。</summary>
    public bool IsRuin => OccupantId < 0;
}
