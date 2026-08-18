using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 7 流域：每流域独立颜色（黄金角）；海洋浅蓝、边缘排水区灰绿。</summary>
public sealed class WatershedLayer : MapLayer
{
    public override int Id => 7;
    public override string Name => "流域";
    public override LayerCategory Category => LayerCategory.Geo;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L7 13 L4 24 M14 3 L21 13 L24 24 M14 3 L14 24' stroke='#8f8' stroke-width='2' fill='none' stroke-linecap='round'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        int ws = ctx.Cache.TileWatershed[id];
        if (ws < 0)
            return ctx.IsSea(id)
                ? SeaColor   // 海洋
                : new Color(0.60f, 0.58f, 0.50f);  // 边缘排水区（直接入海，非河）
        return HslToRgb(GoldenHue(ws), 0.55f, 0.62f);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Text("每流域独立颜色");
        b.Text("海洋/边缘排水区 = 浅蓝/灰绿");
    }
}
