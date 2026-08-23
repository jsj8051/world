using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 16 宗教：同语言群同色系（hue=群，深浅=具体派别）——2026-08-19 与 13 同修"大量飞地"。</summary>
public sealed class ReligionLayer : MapLayer
{
    public override int Id => 16;
    public override string Name => "宗教";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 4 L24 22 L4 22 Z M8 22 L8 26 M12 22 L12 26 M16 22 L16 26 M20 22 L20 26' stroke='#8f8' stroke-width='2' fill='none' stroke-linecap='round'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TilePolity[id] < 0) return SeaColor;
        if (ctx.Cache.TilePolity[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
        int rel = ctx.Cache.TileReligion[id];
        if (rel == 0) return new Color(0.25f, 0.25f, 0.28f);
        int grp = ctx.Cache.TileTerritory != null && id < ctx.Cache.TileTerritory.Length ? ctx.Cache.TileTerritory[id] : 0;
        return FamilyColor(grp, rel, 0.55f, 0.20f);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Text("同语言群同色系（深浅=具体派别）");
        ctx.EnsureIdentityCaches();
        b.Dynamic(ctx.Cache.TileReligion,
            r => FamilyColor(ctx.Cache.SectGroup.TryGetValue(r, out var g) ? g : r, r, 0.60f, 0.25f), "派别");
    }
}
