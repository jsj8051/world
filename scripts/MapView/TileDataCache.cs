using Godot;
using System.Collections.Generic;

namespace World.MapView;

/// <summary>每格图层值缓存（2026-08-21 策略模式重构 M1：原 MapViewer 的 _tile* 字段束独立成类）。
/// 预计算一次（PrecomputeTileValues），切图层 O(1) 查表；策略类经 LayerContext.Cache 访问。</summary>
public sealed class TileDataCache
{
    public float[] TileElev;        // 每格归一化海拔 0..1
    public float[] TileTemp;        // 每格温度 °C
    public float[] TilePrecip;      // 每格降水 mm
    public byte[] TileBiome;        // 每格 biome
    public Vector3[] TileWind;      // 每格盛行风向（单位切向量，盛行风图层用）
    public byte[] TileLake;         // 每格湖泊标记（0/1；最近顶点直读）
    public int[] TileWatershed;     // 每格流域 id（-1=海洋；读档后从 flow 现场算，不存档）
    public byte[] TileMineral;      // 每格矿藏（(富度<<4)|矿种；0=无）
    public byte[] TileSoil;         // 每格土壤肥力 1-5（0=海洋）
    public byte[] TileMonsoon;      // 每格季风强度 0-255（v3.7；0=无/海洋）
    public byte[] TileMonthPrecip;  // 每格当月降水比例 0-255（v3.8 月降水图层；月份切换时刷新）
    public byte[] TileMonthTemp;    // 每格当月温度 −60~60°C→0-255（v3.8 月温度图层；月份切换时刷新）
    // 文明图层（.cmp 游玩地图；v2 部落模型：人口/文化/部落/科技）
    public float[] TilePop;         // 每格总人口（Σ 部落，0=无人/海洋）
    public int[] TileCulture;       // 每格主导文化 key 的 FNV 哈希（0=无；完整 32 位 → 每文化独立色）
    public byte[] TileCultureGroup; // 每格主导文化群（0=无）
    public int[] TileReligion;      // 每格主导宗教派别 key 的 FNV 哈希（0=无；relig_N 每派别独立色）
    public int[] TileTribe;         // 每格主导部落 id（-1=无）
    public int[] TilePower;         // 每格主导势力 id（2026-08-17：最高聚合——酋邦>部落>band；高位域标记）
    public byte[] TilePolity;       // 每格主导势力政体类型（2026-08-17：0=独立band 1=部落 2=酋邦）
    public byte[] TileTechEpoch;    // 每格主导部落最高技术时代 0-4
    public int[] TileTerritory;     // 每格主导 band 的领地（语言群 key 完整哈希；0=无领地）
    public byte[] TileSettlement;   // 每格聚落（2026-08-19 阶段3：0=无 1=新村 2=村庄 3=城镇 4=城市 5=废墟）
    // 身份族系映射（2026-08-19 族系分色图例：文化/派别 → 语言群 hash；惰性建一次）
    public Dictionary<int, int> CultGroup;
    public Dictionary<int, int> SectGroup;
    // 独立势力/势力范围调色板（2026-08-16 终版：最远点采样——任意两势力色距有下界，见 PowerPalette）
    public Dictionary<int, Color> PowerPalette;
    public Dictionary<int, Color> TerritoryPalette;
    // 自适应色带端点（人口：log 压缩 + 分位数裁剪；降水：用户拍板最低到最高归一化）
    public float PopLogMin, PopLogMax;      // 人口图层自适应色带端点
    public float PopMax;                    // 驻扎格人口最大值（图例"最高"标注；0=无人口数据）
    public float PrecipMin, PrecipMax;      // 陆地年降水 min/max（加载时统计）
    public float MonthPrecipMin, MonthPrecipMax; // 陆地当月月降水 min/max（RefreshMonthPrecip 统计）
    public float HSea = 0.5f;               // 视觉海平面（归一化海拔；IsSea 判定用）
}
