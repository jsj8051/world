using Godot;
using System.Collections.Generic;
using World.HexPlanet;
using static World.Utils.ColorRamp;

namespace World.MapView.Layers;

/// <summary>图层 0 海拔（2026-08-21 策略模式重构 M2；色带 2026-08-31 ISO 9241-307 改版）。
/// 海陆分带（用户拍板 08-31 晚）：
///   海洋冷色系（越深越暗）：0~-50 潮间带灰蓝→青白；-50~-200 大陆架灰蓝；-200~-2000 大陆坡深蓝偏靛；
///     -2000~-6000 深海平原靛蓝；&lt;-6000 海沟/深渊墨紫黑。
///   陆地暖色系（越高越亮）：0~500m 天蓝→浅绿；500~2000m 浅绿→金黄；2000~5000m 金黄→赭石；&gt;5000m 白。
/// 设计原则（用户拍板）：色相错开（海洋低饱和灰蓝 vs 陆地天蓝，0m 处硬台阶）；
/// 冷暖对比（冷海 vs 暖陆，海陆边界清晰）；明度表深度（海洋下沉变暗 / 陆地抬升变亮对称）；
/// 海洋避开绿黄色相（不与陆地中海拔草绿/金黄混淆）。
/// 段内过渡三次贝塞尔（Catmull-Rom 平滑）；同位置双停点 = 硬台阶（海陆 0m 色相切）。
/// 旧"温度雪线"叠加已移除：白 = 极高山区（海拔维）；海冰判据（温度 ≤-5°C 的海）保留。
/// 【改色带】= 编辑 ElevationStops 点位（Pos=米，同位置双停点=硬台阶，异位置=平滑渐变），图例自动同源。</summary>
public sealed class ElevationLayer : MapLayer
{
    public override int Id => 0;
    public override string Name => "海拔";
    public override LayerCategory Category => LayerCategory.Geo;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M3 23 L11 8 L16 17 L19 12 L25 23 Z' fill='#eee'/></svg>";

    // ── 海拔色带（2026-08-31 ISO 9241-307 风格 + 海洋冷色分带；ColorOf 与 BuildLegend 同源）──

    /// <summary>海拔连续色带（位置=米，0=海平面；升序：海沟 → 海面 → 高山）。
    /// 海洋（冷色系，越深越暗）：-8000m 墨紫黑 &lt;-8000 恒此色；-6000m 靛蓝（#172B4F）；
    /// -2000m 深蓝（#2F5B8A）；-200m 大陆架灰蓝（#7BA5C4）；-50m 潮间带灰蓝（#C6DCEB）；
    /// 0m 海面青白（#E7F0F6）。陆地（暖色系，越高越亮）：0m 硬台阶→天蓝；500m 浅绿；
    /// 2000m 金黄；5000m 赭石；6000m 白（&gt;6000m 恒白）。</summary>
    public static readonly ColorStop[] ElevationStops =
    {
        new(-8000f, new Color(0.043f, 0.055f, 0.133f)),  // 海沟/深渊最暗（墨紫黑 <-6000 段末）
        new(-6000f, new Color(0.090f, 0.169f, 0.310f)),  // 深海平原底（靛蓝 #172B4F）
        new(-2000f, new Color(0.184f, 0.357f, 0.541f)),  // 大陆坡底（深蓝偏靛 #2F5B8A）
        new(-200f,  new Color(0.482f, 0.647f, 0.769f)),  // 大陆架（灰蓝 #7BA5C4）
        new(-50f,   new Color(0.776f, 0.863f, 0.922f)),  // 潮间带（浅灰蓝 #C6DCEB）
        new(0f,     new Color(0.906f, 0.941f, 0.965f)),  // 海面（青白 #E7F0F6——低饱和灰蓝）
        new(0f,     new Color(0.45f, 0.75f, 0.90f)),     // └ 0m 硬台阶：海洋青白 → 陆地天蓝（色相错开）
        new(500f,   new Color(0.58f, 0.78f, 0.32f)),     // 浅绿（低海拔末端 / 中海拔起点）
        new(2000f,  new Color(0.93f, 0.78f, 0.25f)),     // 金黄（中海拔末端）
        new(5000f,  new Color(0.55f, 0.36f, 0.20f)),     // 赭石（高海拔末端）
        new(6000f,  new Color(0.98f, 0.99f, 1.00f)),     // 纯白（极高山区高光；>6000m 恒白）
    };

    /// <summary>海冰色（显示层判据：温度 ≤-5°C 的海；⚠️ 不同于 BiomeClassifier.SeaIceTempC(-2°C)
    /// 的柯本分类语义——两套判据勿合并；覆盖极地海面，独立于海洋深度色带）。</summary>
    public static readonly Color SeaIceColor = new Color(0.92f, 0.95f, 1.00f);

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        float h = ctx.Cache.TileElev[id];
        int vidE = ctx.TileIndex != null ? ctx.TileIndex.FaceToVertex(id) : id;
        float elevM = ctx.Map.Elev != null ? ctx.Map.Elev[vidE] : (h - ctx.Cache.HSea) * (ctx.Map.MaxElev - ctx.Map.MinElev);   // 米（0=海平面）
        if (ctx.IsSea(id))
        {
            // ⚠️ 2026-08-18 海冰（用户：两极应该冰盖不是海洋）：温度 ≤-5°C 的海 = 海冰（极地冰盖——白）。
            //   注意：此为【显示层】海冰判据（-5°C，地形定案 08-18），不同于 BiomeClassifier.SeaIceTempC（-2°C，柯本 FrigidOcean 分类）——两者语义不同，勿合并。
            float seaTemp = ctx.Map.Temp != null ? ctx.Map.Temp[vidE] : 15f;
            if (seaTemp <= -5f) return SeaIceColor;   // 海冰（白——极地冰盖）
            return RampSampleSmooth(ElevationStops, elevM); // 海洋冷色带：灰蓝→靛→墨紫黑（越深越暗）
        }
        return RampSampleSmooth(ElevationStops, elevM);   // 陆区：天蓝→浅绿→金黄→赭石→白（>6000m 纯白）
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        // 图例与画面同源：RampLegendColors(色带段首色)——11 档连续渐变（海 6 档 + 陆 5 档）
        b.Gradient(RampLegendColors(ElevationStops), "海沟<-8000m", "最高");
        b.Text("海 0→-8000m：灰蓝→靛→墨紫黑（越深越暗，冷色系）；陆：天蓝→浅绿→金黄→赭石→白（越高越亮）");
    }

    // ── 格信息面板（2026-09-01）：海拔层信息行 + 分带名（与 ElevationStops 断点同源）──

    /// <summary>海拔分带名（与 ElevationStops 断点一致）：海 0~-50 潮间带 / -50~-200 大陆架 /
    /// -200~-2000 大陆坡 / -2000~-6000 深海平原 / &lt;-6000 海沟；陆 0~500 低海拔 / 500~2000 中海拔 /
    /// 2000~5000 高海拔 / &gt;5000 极高山区。边界归属右侧段（与色带半开区间一致）。</summary>
    public static string ElevationZoneName(float elevM)
    {
        if (elevM < 0f)
        {
            if (elevM < -6000f) return "海沟";
            if (elevM < -2000f) return "深海平原";
            if (elevM < -200f) return "大陆坡";
            if (elevM < -50f) return "大陆架";
            return "潮间带";
        }
        if (elevM < 500f) return "低海拔";
        if (elevM < 2000f) return "中海拔";
        if (elevM < 5000f) return "高海拔";
        return "极高山区";
    }

    public override IReadOnlyList<TileInfoEntry> TileInfo(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        float h = ctx.Cache.TileElev[id];
        int vidE = ctx.TileIndex != null ? ctx.TileIndex.FaceToVertex(id) : id;
        float elevM = ctx.Map.Elev != null ? ctx.Map.Elev[vidE] : (h - ctx.Cache.HSea) * (ctx.Map.MaxElev - ctx.Map.MinElev);   // 米（0=海平面）
        bool sea = ctx.IsSea(id);
        // 结构化条目（只填数据；swatch=该格当前图层颜色；显示文本由面板拼"标签：值"）
        var list = new System.Collections.Generic.List<TileInfoEntry>
        {
            new("高度", $"{elevM:F0} m（0=海平面）", ColorOf(ctx, tile)),
            new("类型", (sea ? "海洋" : "陆地") + " · " + ElevationZoneName(elevM)),
        };
        if (ctx.Map.Temp != null)
        {
            float tempC = ctx.Map.Temp[vidE];
            list.Add(new("温度", $"{tempC:F0}°C"));
            if (sea && tempC <= -5f) list.Add(new("状态", "海冰（极地冰盖）"));
        }
        return list;
    }
}
