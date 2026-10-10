namespace World.Constants;

/// <summary>
/// 生物群系类型 —— **只放柯本气候分类（Köppen–Geiger climate classification）**。
///
/// ★词表边界（2026-10-09 用户拍板）：**仅保留柯本气候型**。本次删除了全部非柯本附加类：
///   `DeepOcean=0` / `Ocean=1`（水面）、`Alpine=12`（高山）、`Riparian=13`（河岸带）、
///   `FrigidOcean=30` / `TropicalOcean=31`（海洋型）。
///   ⇒ 本枚举现为**陆格气候型**词汇，**不再**自称"覆盖海格的全格生物群系事实"。
///   ⚠️ 若将来 BiomeFact 需要海格 / 地形附加类，**另立事实**，不要回填本枚举
///     （裁决见 `docs/裁决-Domain解散与BiomeType归Constant.md`；
///      所修订的上游语义契约见 `docs/裁决-P4-3-Biome事实定义.md`）。
///
/// ★位置（2026-10-09）：由 `scripts/Logic/Domain/BiomeType.cs`（`World.Domain`）迁入
///   `scripts/Logic/Constant/`，命名空间 `World.Constants`（与 `Constant/Planet/Thermal.cs` 同族）。
///   `World.Domain` 随本次一并解散——另三件 `PowerPalette` / `Calendar` / `BiomeColors` 已删除
///   （色带职责归地图模式；月数常量就地内联；势力调色板待重新实现）。
///
/// ★编号一律**不重排**：编号即语义，且曾按 byte 写入存档 ⇒ 删掉的编号成为**空缺**。
///   历史：4–11（Taiga/ColdDesert/TemperateForest/…）为 2026-08-07 删除的化石值；
///   0/1/12/13/30/31 为 2026-10-09 本次删除。
///
/// ★B 组判定常数 = **k = 10**：沙漠 `P &lt; 10·(T+14)`；半干旱 `10·(T+14) ≤ P &lt; 20·(T+14)`。
///   本条修正自旧注释写的 k = 20——旧注释与 Legacy 实现不符，按之实现会让 `BSh`/`BSk`
///   恒产 0 格（取证见 `docs/裁决-P4-3-Biome事实定义.md` §3.3）。
/// </summary>
public enum BiomeType : byte
{
    // ── Köppen E 组（极地）──
    IceCap = 2,                // EF  冰原/极地冰盖：最热月 < 0 °C
    Tundra = 3,                // ET  苔原：最热月 0~10 °C

    // ── Köppen A 组（热带）──
    TropicalRainforest = 14,   // Af  热带雨林：最冷月 ≥18°C，最干月 ≥60mm
    TropicalMonsoon = 15,      // Am  热带季风林：最冷月 ≥18°C，干季短、年雨量支撑
    TropicalSavanna = 16,      // Aw  热带稀树草原：最冷月 ≥18°C，干季明显（冬干）

    // ── Köppen B 组（干旱；判据用 k = 10）──
    HotDesert = 17,            // BWh 热带/亚热带沙漠：P < 10×(T+14)，年均 ≥18°C
    ColdDesertKoppen = 18,     // BWk 冷沙漠：P < 10×(T+14)，年均 <18°C
    HotSteppe = 19,            // BSh 热带半干旱草原：10×(T+14) ≤ P < 20×(T+14)，年均 ≥18°C
    ColdSteppe = 20,           // BSk 冷半干旱草原：同上，年均 <18°C

    // ── Köppen C 组（温带）──
    HumidSubtropical = 21,     // Cfa 湿润亚热带：最冷月 >−3°C，最热月 ≥22°C，全年湿
    Oceanic = 22,              // Cfb 海洋性温带：最冷月 >−3°C，最热月 <22°C，全年湿
    MonsoonSubtropical = 23,   // Cwa 冬干亚热带：最冷月 >−3°C，最热月 ≥22°C，冬干
    MediterraneanHot = 24,     // Csa 地中海（热夏）：最冷月 >−3°C，最热月 ≥22°C，夏干
    MediterraneanCool = 25,    // Csb 地中海（凉夏）：最冷月 >−3°C，最热月 <22°C，夏干

    // ── Köppen D 组（大陆性）──
    ContinentalHot = 26,       // Dfa 湿润大陆（热夏）：最冷月 ≤−3°C，最热月 ≥22°C
    ContinentalWarm = 27,      // Dfb 湿润大陆（暖夏）：最冷月 ≤−3°C，最热月 10~22°C
    Subarctic = 28,            // Dfc 亚寒带针叶林：最冷月 ≤−15°C，最热月 10~22°C
    ContinentalDry = 29,       // Dwa 冬干大陆：最冷月 ≤−3°C，冬干
}
