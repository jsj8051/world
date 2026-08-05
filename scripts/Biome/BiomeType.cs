namespace World.Biome;

/// <summary>
/// 生物群系类型（策略地图色块风，非写实）。
/// 0-13：早期绝对阈值分类（2026-08-16 前，保留供旧存档兼容，新生成不再产生）；
/// 14+ ：柯本气候分类细类（2026-08-16，Köppen–Geiger 判据 + 季节/干湿季推导）。
/// byte 直接写入存档（0-31 全部有效）。
/// </summary>
public enum BiomeType : byte
{
    DeepOcean = 0,          // 深海（e < -0.1）
    Ocean = 1,              // 温带海洋（中纬度海水）
    IceCap = 2,             // 冰原/极地冰盖（EF，最热月 < 0°C；或高山冰雪）
    Tundra = 3,             // 苔原（ET，最热月 0~10°C）
    Taiga = 4,              // 针叶林（旧值；新判据 → Subarctic）
    ColdDesert = 5,         // 寒漠（旧值；新判据 → ColdDesertKoppen）
    TemperateForest = 6,    // 温带森林（旧值；新判据 → HumidSubtropical/Oceanic）
    TemperateGrassland = 7, // 温带草原（旧值；新判据 → HotSteppe/ColdSteppe）
    Desert = 8,             // 荒漠（旧值；新判据 → HotDesert）
    Savanna = 9,            // 稀树草原（旧值；新判据 → TropicalSavanna）
    TropicalForest = 10,    // 热带雨林（旧值；新判据 → TropicalRainforest）
    TropicalDryForest = 11, // 热带疏林（旧值；新判据 → TropicalMonsoon）
    Alpine = 12,            // 高山（高海拔，温度不够低）
    Riparian = 13,          // 河岸带（沿岸陆地格，河湖湿润 → 翠绿绿洲线）

    // ── 柯本气候分类细类（Köppen–Geiger，2026-08-16）──
    TropicalRainforest = 14,   // Af  热带雨林：最冷月≥18°C，最干月≥60mm
    TropicalMonsoon = 15,      // Am  热带季风林：最冷月≥18°C，干季短、年雨量支撑
    TropicalSavanna = 16,      // Aw  热带稀树草原：最冷月≥18°C，干季明显（冬干）
    HotDesert = 17,            // BWh 热带/亚热带沙漠：年降水 < 20×(T+14)，年均≥18°C
    ColdDesertKoppen = 18,     // BWk 冷沙漠：年降水 < 20×(T+14)，年均<18°C
    HotSteppe = 19,            // BSh 热带半干旱草原：20×(T+14) ≤ P < 30×(T+14)，年均≥18°C
    ColdSteppe = 20,           // BSk 冷半干旱草原：同上，年均<18°C
    HumidSubtropical = 21,     // Cfa 湿润亚热带：最冷月>-3°C，最热月≥22°C，全年湿
    Oceanic = 22,              // Cfb 海洋性温带：最冷月>-3°C，最热月<22°C，全年湿
    MonsoonSubtropical = 23,   // Cwa 冬干亚热带：最冷月>-3°C，最热月≥22°C，冬干
    MediterraneanHot = 24,     // Csa 地中海（热夏）：最冷月>-3°C，最热月≥22°C，夏干
    MediterraneanCool = 25,    // Csb 地中海（凉夏）：最冷月>-3°C，最热月<22°C，夏干
    ContinentalHot = 26,       // Dfa 湿润大陆（热夏）：最冷月≤-3°C，最热月≥22°C
    ContinentalWarm = 27,      // Dfb 湿润大陆（暖夏）：最冷月≤-3°C，最热月10~22°C
    Subarctic = 28,            // Dfc 亚寒带针叶林：最冷月≤-15°C，最热月10~22°C
    ContinentalDry = 29,       // Dwa 冬干大陆：最冷月≤-3°C，冬干
    FrigidOcean = 30,          // 极地海洋（海冰带，年均<-2°C）
    TropicalOcean = 31,        // 热带海洋（年均≥18°C）
}
