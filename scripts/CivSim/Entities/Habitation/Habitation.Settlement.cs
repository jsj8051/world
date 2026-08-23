namespace World.CivSim.Entities;

/// <summary>
/// 聚落形态分区（Habitation.Settlement——settlement 形态专属状态；camp 形态下闲置）。
/// ⚠️ 2026-08-23 功能定性重设计：Level/LastLevelUpTick 已删除（用户拍板 D3）——
/// 村庄/集镇/城市 = 职能条件（用户拍板："有了什么条件才能做什么；集镇/城市也是一个条件"），
/// 非人口等级阶梯（旧 Dwell×P 升阶 = 拿人口结果当定义，已移除）。
/// 粮仓（Stocks）是聚落属性（用户拍板"存粮迁移到聚落"）：容量 = settle 容量 ×(1+0.5×TownTier)。
/// </summary>
public partial class Habitation
{
    /// <summary>粮仓（场所属性；索引 = CommodityTable.Index(id)）。</summary>
    public float[] Stocks = CommodityTable.NewStocks();
}