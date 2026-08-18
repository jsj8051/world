using Godot;
using World.HexPlanet;

namespace World.MapView.Layers;

/// <summary>图层 2 降水：陆地自适应色带（用户拍板：最低到最高归一化，不用固定 2000mm）。</summary>
public sealed class PrecipitationLayer : MapLayer
{
    public override int Id => 2;
    public override string Name => "降水";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L19 13 L14 23 L9 13 Z' fill='#eee'/><path d='M6 20 L4 25 M22 20 L24 25' stroke='#eee' stroke-width='2'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        float x = Mathf.Clamp((ctx.Cache.TilePrecip[id] - ctx.Cache.PrecipMin) / (ctx.Cache.PrecipMax - ctx.Cache.PrecipMin), 0f, 1f);
        return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Gradient(
            new[] { new Color(0.90f, 0.80f, 0.40f), new Color(0.10f, 0.30f, 0.70f) },
            $"{ctx.Cache.PrecipMin:F0}mm", $"{ctx.Cache.PrecipMax:F0}mm");
        b.Text("陆地自适应色带（随地图分布）");
    }
}
