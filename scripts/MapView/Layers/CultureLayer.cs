using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 13 文化：同语言群同色系（hue=群，深浅=具体文化）——2026-08-19 修复"大量飞地"：
/// 分裂漂变产生数百微文化（n128 实测 581 种）→ 每文化独立色=彩虹孤岛；
/// 按语言群分色系 → 相关文化可见相关（同族同色渐变），族域连贯无飞地。
/// 2026-08-19 定案：统一着色（无定居亮/领地淡深浅区分——用户"直接补齐"）。</summary>
public sealed class CultureLayer : MapLayer
{
    public override int Id => 13;
    public override string Name => "文化";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M12 3 L12 25 M12 6 L24 6 L21 11 L24 16 L12 16' fill='#fa6' stroke='#fa6' stroke-width='1.5' stroke-linejoin='miter'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TileCulture[id] == 0) return SeaColor;
        int cult = ctx.Cache.TileCulture[id];
        if (cult == 0) return new Color(0.25f, 0.25f, 0.28f);
        int grp = ctx.Cache.TileTerritory != null && id < ctx.Cache.TileTerritory.Length ? ctx.Cache.TileTerritory[id] : 0;
        return FamilyColor(grp, cult, 0.55f, 0.20f);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Text("同语言群同色系（深浅=具体文化，族域连贯）");
        ctx.EnsureIdentityCaches();
        b.Dynamic(ctx.Cache.TileCulture,
            c => FamilyColor(ctx.Cache.CultGroup.TryGetValue(c, out var g) ? g : c, c, 0.60f, 0.25f), "文化");
    }
}
