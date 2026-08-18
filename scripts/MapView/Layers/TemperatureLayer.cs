using Godot;
using World.Biome;
using World.HexPlanet;

namespace World.MapView.Layers;

/// <summary>图层 1 温度：分段色带（极寒/冰点/0-15°/宜居/高温）。</summary>
public sealed class TemperatureLayer : MapLayer
{
    public override int Id => 1;
    public override string Name => "温度";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L14 14 L18 14 L18 20 L10 20 L10 14 L14 14 M10 20 L18 20 M10 17 L18 17' stroke='#eee' stroke-width='2.5' fill='none'/><path d='M11 21 L17 21 L17 24 L11 24 Z' fill='#eee'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
        => BiomeColors.TemperatureToColor(ctx.Cache.TileTemp[tile.Id]);

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Gradient(
            new[] { new Color(0.08f, 0.12f, 0.45f), new Color(0.22f, 0.52f, 0.72f), new Color(0.38f, 0.72f, 0.42f), new Color(0.92f, 0.78f, 0.28f), new Color(0.88f, 0.30f, 0.15f) },
            "-85°C", "+45°C");
        b.Text("分段色带：极寒/冰点/0-15°/宜居/高温");
    }
}
