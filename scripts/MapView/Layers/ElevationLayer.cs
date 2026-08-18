using Godot;
using World.HexPlanet;

namespace World.MapView.Layers;

/// <summary>图层 0 海拔（2026-08-21 策略模式重构 M2）：海 <-200m 深海 / -200~0m 浅海（大陆架）；
/// 陆地连续色带（实际米）；雪线=0°C 等温线（2026-08-18 用户拍板）；海冰=温度 ≤-5°C 的海。</summary>
public sealed class ElevationLayer : MapLayer
{
    public override int Id => 0;
    public override string Name => "海拔";
    public override LayerCategory Category => LayerCategory.Geo;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M3 23 L11 8 L16 17 L19 12 L25 23 Z' fill='#eee'/></svg>";

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
            if (seaTemp <= -5f) return new Color(0.92f, 0.95f, 1.00f);   // 海冰（白——极地冰盖）
            if (elevM < -200f) return new Color(0.01f, 0.05f, 0.18f);   // 深海 <-200m
            return new Color(0.20f, 0.45f, 0.68f);                      // 浅海 -200~0m（大陆架）
        }
        // 陆地：海拔色带（沙/绿/棕按米）——雪（白）由实际温度驱动（2026-08-18 用户：雪线按实际温度）
        //   0°C 以下全白（雪线=0°C 等温线——纬度/气候决定——非固定 3300m）；0~2°C 渐变
        float tempC = ctx.Map.Temp != null ? ctx.Map.Temp[vidE] : 15f;
        Color baseC;
        if (elevM <= 0f) baseC = new Color(0.76f, 0.70f, 0.50f);
        else if (elevM < 100f) baseC = new Color(0.76f, 0.70f, 0.50f).Lerp(new Color(0.30f, 0.65f, 0.10f), elevM / 100f);
        else if (elevM < 800f) baseC = new Color(0.30f, 0.65f, 0.10f).Lerp(new Color(0.60f, 0.50f, 0.35f), (elevM - 100f) / 700f);
        else baseC = new Color(0.60f, 0.50f, 0.35f);
        float snowT = Mathf.Clamp(1f - tempC / 2f, 0f, 1f);   // ≤0°C 全白；0~2°C 渐变；>2°C 无雪
        return baseC.Lerp(new Color(0.95f, 0.97f, 1.00f), snowT);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Gradient(
            new[] { new Color(0.01f, 0.05f, 0.18f), new Color(0.20f, 0.45f, 0.68f),
                    new Color(0.70f, 0.65f, 0.40f), new Color(0.30f, 0.65f, 0.10f),
                    new Color(0.60f, 0.50f, 0.35f), new Color(0.95f, 0.97f, 1.00f) },
            "深海<-200m", "最高");
        b.Text("海：<-200m 深海 / -200~0m 浅海（大陆架）；陆：连续色带（实际米）");
    }
}
