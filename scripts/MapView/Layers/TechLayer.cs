using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 15 科技：主导部落最高技术时代色带（石器棕→新石器绿→青铜橙→铁器蓝→古典紫）。</summary>
public sealed class TechLayer : MapLayer
{
    public override int Id => 15;
    public override string Name => "科技";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><circle cx='14' cy='11' r='7' fill='none' stroke='#8f8' stroke-width='2'/><path d='M11 19 H17 M12.5 23 H15.5 M14 16 V19' stroke='#8f8' stroke-width='2' stroke-linecap='round'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TileBand[id] < 0) return SeaColor;
        if (ctx.Cache.TileBand[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
        byte ep = ctx.Cache.TileTechEpoch[id];
        return ep == 0 ? new Color(0.55f, 0.42f, 0.28f)   // 石器：棕（有基础技术，非"无"）
            : TechEpochColors[Mathf.Clamp(ep - 1, 0, TechEpochColors.Length - 1)];
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        for (int e = 0; e <= 4; e++)
        {
            var col = e == 0 ? new Color(0.55f, 0.42f, 0.28f) : TechEpochColors[e - 1];
            b.Row(col, TechEpochNames[e]);
        }
    }
}
