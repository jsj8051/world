using Godot;

namespace World.MapView;

/// <summary>地图图层共享取色工具（2026-08-21 策略模式重构 M2：从 MapViewer 搬出，
/// 供 MapViewer 与各图层策略共用；文件内 using static World.MapView.MapLayerColors 后引用不变）。</summary>
public static class MapLayerColors
{
    /// <summary>HSL → RGB（标准换算）。</summary>
    public static Color HslToRgb(float h, float s, float l)
    {
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float H2R(float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
        return new Color(H2R(h + 1f / 3f), H2R(h), H2R(h - 1f / 3f));
    }

    /// <summary>海洋统一色（2026-08-18 用户要求与势力色区分）：深蓝——明确海，
    /// 与势力色（亮色/避开蓝相）一眼可分。各图层判海返回统一用此色。
    /// ⚠️ PowerPalette.SeaColor 与此同值（独立锚点，勿合并）。</summary>
    public static readonly Color SeaColor = new Color(0.10f, 0.22f, 0.48f);

    /// <summary>势力色避开海洋蓝（2026-08-18 用户要求）：蓝-青相区间（0.48-0.72）映射到
    /// 暖色/绿黄——势力色块与海色不撞（黄金角散列原可能出亮蓝——7401 #69a6d3 与海混淆）。</summary>
    public static float AvoidSeaHue(float hue)
    {
        if (hue >= 0.48f && hue <= 0.72f)
            return hue < 0.60f ? hue + 0.35f : hue - 0.35f;   // 0.48-0.60→0.83-0.95（紫红）; 0.60-0.72→0.25-0.37（绿黄）
        return hue;
    }

    /// <summary>整数 key/流域 id → 黄金角色相（double 计算防 float 精度坍缩——int 32 位 × float 在 2^31 量级
    /// 只剩 22 档色相，不同 key 同色；double 52 位尾数全展开 360 档，2026-08-07）。</summary>
    public static float GoldenHue(long id)
    {
        return (float)((id * 0.6180339887498949) % 1.0);
    }

    /// <summary>矿种固定色（索引 = MineralSystem 矿种：1铁 2铜 3锡 4金 5煤 6盐 7石料 8宝石）。
    /// 显示时 × 富度明度（贫 0.55 / 富 0.78 / 巨型 1.0）——用户确认：固定色 + 富度深浅。</summary>
    public static readonly Color[] MineralColors =
    {
        Colors.Gray,                                       // 0 无（不使用）
        new Color(0.55f, 0.50f, 0.45f),                    // 1 铁：灰褐
        new Color(0.75f, 0.45f, 0.20f),                    // 2 铜：铜橙
        new Color(0.75f, 0.75f, 0.80f),                    // 3 锡：银白
        new Color(0.95f, 0.75f, 0.15f),                    // 4 金：金黄
        new Color(0.18f, 0.18f, 0.20f),                    // 5 煤：黑
        new Color(0.95f, 0.95f, 0.90f),                    // 6 盐：白
        new Color(0.70f, 0.68f, 0.62f),                    // 7 石料：石灰
        new Color(0.62f, 0.30f, 0.78f),                    // 8 宝石：紫
    };

    /// <summary>土壤肥力 5 档色带（索引 1-5：深绿=肥沃 → 灰=贫瘠；0 不用）。</summary>
    public static readonly Color[] SoilColors =
    {
        Colors.Gray,                                       // 0 海洋（不使用）
        new Color(0.55f, 0.48f, 0.38f),                    // 1 贫瘠：灰棕
        new Color(0.62f, 0.52f, 0.36f),                    // 2 差：棕
        new Color(0.72f, 0.62f, 0.35f),                    // 3 中：黄
        new Color(0.45f, 0.68f, 0.35f),                    // 4 好：绿
        new Color(0.20f, 0.55f, 0.25f),                    // 5 肥沃：深绿
    };

    /// <summary>部落图层调色板（部落标签取色；高区分度 8 色循环——文化层已改每文化独立色，勿复用）。</summary>
    public static readonly Color[] CulturePalette =
    {
        new(0.95f, 0.30f, 0.25f),  // 红
        new(0.25f, 0.55f, 0.95f),  // 蓝
        new(0.30f, 0.80f, 0.35f),  // 绿
        new(0.95f, 0.70f, 0.20f),  // 橙
        new(0.70f, 0.40f, 0.90f),  // 紫
        new(0.20f, 0.80f, 0.80f),  // 青
        new(0.90f, 0.50f, 0.70f),  // 粉
        new(0.60f, 0.60f, 0.20f),  // 橄榄
    };

    /// <summary>科技图层时代色带（索引 0=新石器 1=青铜 2=铁器 3=古典+）。</summary>
    public static readonly Color[] TechEpochColors =
    {
        new(0.35f, 0.75f, 0.35f),  // 新石器：绿（农业）
        new(0.90f, 0.60f, 0.20f),  // 青铜：橙（冶金）
        new(0.30f, 0.50f, 0.85f),  // 铁器：蓝（铁兵）
        new(0.65f, 0.40f, 0.85f),  // 古典/中世纪：紫（帝国）
    };

    /// <summary>聚落图层色（2026-08-19 阶段3：索引 = _tileSettlement 值 − 1——0 新村 1 村庄 2 城镇 3 城市 4 废墟）。</summary>
    public static readonly Color[] SettlementLevelColors =
    {
        new(0.72f, 0.55f, 0.35f),  // 新村/营地：棕
        new(0.35f, 0.72f, 0.35f),  // 村庄：绿
        new(0.95f, 0.65f, 0.25f),  // 城镇：橙
        new(0.85f, 0.25f, 0.20f),  // 城市：红
        new(0.45f, 0.45f, 0.50f),  // 废墟：灰
    };

    /// <summary>生物群系显示名（索引=BiomeType 值；0-31 全覆盖）。</summary>
    public static readonly string[] BiomeNames =
    {
        "深海", "海洋", "冰原(EF)", "苔原(ET)", "", "", "", "", "", "", "", "",
        "高山", "河岸带",
        "热带雨林(Af)", "热带季风林(Am)", "热带稀树草原(Aw)",
        "热沙漠(BWh)", "冷沙漠(BWk)", "热半干旱草原(BSh)", "冷半干旱草原(BSk)",
        "湿润亚热带(Cfa)", "海洋性温带(Cfb)", "冬干亚热带(Cwa)",
        "地中海热夏(Csa)", "地中海凉夏(Csb)",
        "湿润大陆热夏(Dfa)", "湿润大陆暖夏(Dfb)", "亚寒带针叶林(Dfc)", "冬干大陆(Dwa)",
        "极地海洋", "热带海洋",
    };

    /// <summary>土壤肥力名（索引 1-5）。</summary>
    public static readonly string[] SoilNames = { "", "贫瘠", "差", "中", "好", "肥沃" };

    /// <summary>科技时代名（索引 0=石器 1-4=TechEpochColors）。</summary>
    public static readonly string[] TechEpochNames = { "石器", "新石器", "青铜", "铁器", "古典" };

    /// <summary>独立势力颜色**兜底散列**（2026-08-16）：主路径已改用最远点采样调色板 _powerPalette
    /// （任意两势力色距有下界）；此处仅覆盖调色板未收录的 id（理论不触发）。hue=黄金角（避开海蓝）
    /// + S/L=独立乘法散列（Knuth/素数，与色相 φ 解耦——低位段对相近 id 高度相关是原 3D 版撞色根源）。</summary>
    public static Color PowerColor(int powerId)
    {
        uint h = (uint)powerId;
        float hue = AvoidSeaHue(GoldenHue(powerId));
        uint s1 = h * 2654435761u;   // 乘法散列（uint 回绕）
        uint s2 = h * 40503u;
        float sat = 0.35f + 0.55f * (s1 >> 24) / 255f;    // 饱和度 0.35-0.90
        float lig = 0.30f + 0.50f * (s2 >> 24) / 255f;    // 明度 0.30-0.80
        return HslToRgb(hue, sat, lig);
    }

    /// <summary>族系分色（2026-08-19 "大量飞地"修复）：hue = 语言群哈希（族色相），明度 = 具体文化/派别哈希（族内深浅）。
    /// 分裂漂变产生数百微文化 → 每文化独立色=彩虹孤岛；同群同色系 → 相关文化可见相关、族域连贯（类语言族地图）。</summary>
    public static Color FamilyColor(int groupHash, int itemHash, float lightBase, float lightSpan)
    {
        float hue = GoldenHue(groupHash != 0 ? groupHash : itemHash);
        float shade = (itemHash & 0xFF) / 255f;
        return HslToRgb(hue, 0.55f, lightBase + lightSpan * shade);
    }
}
