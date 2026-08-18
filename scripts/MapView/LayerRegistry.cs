using System.Collections.Generic;

namespace World.MapView;

/// <summary>地图图层策略注册表（2026-08-21 策略模式重构 M2）：索引/Id → 策略实例。
/// 新图层 = 新建策略类 + 此表加一行（索引须与 Id 一致；顺序即 UI 按钮顺序）。</summary>
public static class LayerRegistry
{
    public static readonly IReadOnlyList<MapLayer> All = new MapLayer[]
    {
        new Layers.ElevationLayer(),      // 0 海拔
        new Layers.TemperatureLayer(),    // 1 温度
        new Layers.PrecipitationLayer(),  // 2 降水
        new Layers.BiomeLayer(),          // 3 生物群系
        new Layers.WindLayer(),           // 4 风场
        new Layers.CurrentFlowLayer(),    // 5 洋流（策略；画法组件 CurrentFlow 同目录）
        new Layers.RiverLayer(),          // 6 河流
        new Layers.WatershedLayer(),      // 7 流域
        new Layers.MineralLayer(),        // 8 矿藏
        new Layers.SoilLayer(),           // 9 土壤
        new Layers.MonthPrecipLayer(),    // 10 月降水
        new Layers.MonthTempLayer(),      // 11 月温度
        new Layers.PopulationLayer(),     // 12 人口
        new Layers.CultureLayer(),        // 13 文化
        new Layers.PowerLayer(),          // 14 独立势力
        new Layers.TechLayer(),           // 15 科技
        new Layers.ReligionLayer(),       // 16 宗教
        new Layers.TerritoryLayer(),      // 17 势力范围
        new Layers.PolityLayer(),         // 18 政体
        new Layers.SettlementLayer(),     // 19 聚落
    };

    public static MapLayer Of(int id) => All[id];
}
