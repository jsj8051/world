using Godot;
using World.Biome;
using World.HexPlanet;
using World.MapGen;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 11 月温度：当月均温色块（MonthTemp −60~60°C→0-255；月份滑块切换）。
/// 月份切换（RefreshMonthTemp + 重算颜色）M3 接入。</summary>
public sealed class MonthTempLayer : MapLayer
{
    public override int Id => 11;
    public override string Name => "月温度";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L14 14 L18 14 L18 20 L10 20 L10 14 L14 14 M10 20 L18 20 M10 17 L18 17' stroke='#fa6' stroke-width='2.5' fill='none'/><circle cx='14' cy='23' r='4' fill='none' stroke='#fa6' stroke-width='2'/><path d='M14 20 A4 4 0 0 1 14 26 Z' fill='#fa6'/></svg>";
    public override bool UsesMonth => true;

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.Cache.TileMonthTemp == null || ctx.Map == null || ctx.Map.MonthTemp == null)
            return ctx.IsSea(id)
                ? SeaColor
                : new Color(0.72f, 0.70f, 0.58f);
        float tC = FieldCodec.ByteToTemp(ctx.Cache.TileMonthTemp[id]);   // byte → °C
        return BiomeColors.TemperatureToColor(tC);
    }

    /// <summary>月份切换：刷新当月温度缓存 + 重算颜色（原滑块回调分支）。</summary>
    public override void OnMonthChanged(LayerContext ctx, int month)
    {
        ctx.RefreshMonthTemp();
        ctx.RequestRecolor?.Invoke();
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Gradient(
            new[] { new Color(0.08f, 0.12f, 0.45f), new Color(0.22f, 0.52f, 0.72f), new Color(0.38f, 0.72f, 0.42f), new Color(0.92f, 0.78f, 0.28f), new Color(0.88f, 0.30f, 0.15f) },
            "-60°C", "+60°C");
        b.Text("当月均温");
    }
}
