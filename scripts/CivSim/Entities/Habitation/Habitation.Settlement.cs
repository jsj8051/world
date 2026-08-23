namespace World.CivSim.Entities;

/// <summary>
/// 聚落形态分区（Habitation.Settlement——settlement 形态专属状态；camp 形态下闲置）。
/// 等级 0=新村/营地 1=村庄 2=集镇 3=城市（Dwell×P 阈值驱动，确定性纯函数——读档续跑无分叉；
/// ⚠️ 2026-08-23 用户指出：现实中村庄/集镇/城市是功能定性而非人口等级——等级语义待重设计，见讨论记录）。
/// 粮仓（Stocks）是聚落属性（用户拍板"存粮迁移到聚落"）：容量 = settle 容量 ×(1+0.5×Level)。
/// </summary>
public partial class Habitation
{
    /// <summary>聚落等级（0=新村 1=村庄 2=集镇 3=城市——settlement 形态专属；语义待重设计）。</summary>
    public int Level;

    /// <summary>最近升级 tick（等级冷却，防跳级抖动）。</summary>
    public int LastLevelUpTick;

    /// <summary>粮仓（场所属性；索引 = CommodityTable.Index(id)）。</summary>
    public float[] Stocks = CommodityTable.NewStocks();
}