using Godot;
using World.HexPlanet;
using World.MapGen;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 8 矿藏：矿种固定色 × 富度明度（贫暗/富中/巨型亮）；无矿淡地形底。</summary>
public sealed class MineralLayer : MapLayer
{
    public override int Id => 8;
    public override string Name => "矿藏";
    public override LayerCategory Category => LayerCategory.Geo;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 2 L24 9 L22 20 L14 26 L6 20 L4 9 Z M14 2 L14 26 M4 9 L14 14 L24 9 M6 20 L14 14 L22 20' stroke='#fd8' stroke-width='1.5' fill='none'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        byte m = ctx.Cache.TileMineral[id];
        if (m == 0)
        {
            return ctx.IsSea(id)
                ? SeaColor
                : new Color(0.55f, 0.52f, 0.42f);
        }
        var baseC = MineralColors[MineralSystem.TypeOf(m) % MineralColors.Length];
        float bright = MineralSystem.RichnessOf(m) switch { 1 => 0.55f, 2 => 0.78f, _ => 1.0f };
        return baseC * bright;
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        for (int m = 1; m < MineralSystem.Names.Length; m++)
            b.Row(MineralColors[m], MineralSystem.Names[m]);
        b.Text("明度 = 富度（贫暗/富中/巨型亮）");
    }
}
