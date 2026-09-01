using Godot;
using World.Biome;
using World.HexPlanet;
using static World.Utils.ColorRamp;

namespace World.MapView.Layers;

/// <summary>图层 1 温度：分段色带（极寒/冰点/0-15°/宜居/高温）。
/// 2026-08-31 色带定义内聚气候模块 BiomeColors.TempStops（温度层与月温度层共用），
/// 本层只做取色与图例（图例与画面同源 RampLegendColors）。</summary>
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
        b.Gradient(RampLegendColors(BiomeColors.TempStops), "-85°C", "+45°C");
        b.Text("分段色带：极寒/冰点/0-15°/宜居/高温");
    }
}
