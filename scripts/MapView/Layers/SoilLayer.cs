using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 9 土壤肥力：5 档色带（深绿=肥沃 → 灰=贫瘠）；海洋深蓝。</summary>
public sealed class SoilLayer : MapLayer
{
    public override int Id => 9;
    public override string Name => "土壤";
    public override LayerCategory Category => LayerCategory.Geo;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M3 6 H25 M3 12 H25 M3 18 H25 M3 24 H25' stroke='#8a6' stroke-width='3' fill='none'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        byte s = ctx.Cache.TileSoil[id];
        if (s == 0)
            return SeaColor;   // 海洋
        return SoilColors[Mathf.Clamp(s, 1, 5)];
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        for (int s = 1; s <= 5; s++)
            b.Row(SoilColors[s], SoilNames[s]);
    }
}
