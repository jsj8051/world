using Godot;
using World.Biome;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 3 生物群系：BiomeType 固定色（BiomeColors）。</summary>
public sealed class BiomeLayer : MapLayer
{
    public override int Id => 3;
    public override string Name => "生物群系";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L20 12 L17 12 L22 20 L6 20 L11 12 L8 12 Z' fill='#eee'/><rect x='12.5' y='20' width='3' height='6' fill='#eee'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
        => BiomeColors.BiomeToColor((BiomeType)ctx.Cache.TileBiome[tile.Id]);

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        for (int i = 0; i < BiomeNames.Length; i++)
        {
            if (string.IsNullOrEmpty(BiomeNames[i])) continue;
            b.Row(BiomeColors.BiomeToColor((BiomeType)i), BiomeNames[i]);
        }
    }
}
