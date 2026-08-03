namespace World.Biome;

/// <summary>
/// 生物群系类型（策略地图色块风，非写实）。分类依据：海拔 × 温度 × 降水
/// （Whittaker 简化）。byte 便于直接写入存档。
/// </summary>
public enum BiomeType : byte
{
    DeepOcean = 0,          // 深海（e < -0.1）
    Ocean = 1,              // 浅海/海洋
    IceCap = 2,             // 冰原/极地冰盖（极地或高山冰雪）
    Tundra = 3,             // 苔原（寒带半湿润）
    Taiga = 4,              // 针叶林（寒带湿润）
    ColdDesert = 5,         // 寒漠（寒带干旱）
    TemperateForest = 6,    // 温带森林（温带湿润）
    TemperateGrassland = 7, // 温带草原（温带半干旱/半湿润）
    Desert = 8,             // 荒漠（温带/热带干旱）
    Savanna = 9,            // 稀树草原（热带半干旱）
    TropicalForest = 10,    // 热带雨林（热带湿润）
    TropicalDryForest = 11, // 热带疏林/季雨林（热带半湿润）
    Alpine = 12,            // 高山（高海拔，温度不够低）
    Riparian = 13,          // 河岸带（2026-08-02：沿岸陆地格，河湖湿润 → 翠绿绿洲线）
}
