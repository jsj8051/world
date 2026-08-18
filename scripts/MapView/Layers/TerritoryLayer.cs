using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 17 势力范围：每领地独立色（最远点采样调色板——2026-08-16 修复"全白"：
/// 旧版明度 0.85 近白 + 散列近撞色；无领地/无人灰）。</summary>
public sealed class TerritoryLayer : MapLayer
{
    public override int Id => 17;
    public override string Name => "势力范围";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L24 9 L24 19 L14 25 L4 19 L4 9 Z' stroke='#fd8' stroke-width='2' fill='none' stroke-linejoin='miter'/><path d='M14 3 L14 25 M4 9 L24 19 M24 9 L4 19' stroke='#fd8' stroke-width='1.5' fill='none' stroke-linecap='round'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TileTerritory[id] == 0) return SeaColor;
        int terr = ctx.Cache.TileTerritory[id];
        // ⚠️ 2026-08-17：领地按归属显示全领地（不能再用人口判"无人"——
        //   人口图层已改只在驻扎格显示，采集格人口=0）
        if (terr == 0) return new Color(0.30f, 0.32f, 0.36f);
        if (ctx.Cache.TerritoryPalette != null && ctx.Cache.TerritoryPalette.TryGetValue(terr, out var tc)) return tc;
        return HslToRgb(AvoidSeaHue(GoldenHue(terr)), 0.55f, 0.62f);   // 兜底（理论不触发）
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Row(new Color(0.30f, 0.32f, 0.36f), "无领地");
        b.Text("每领地独立颜色（两两可区分）");
        b.Text("同领地必同语言群 → 同领地同色");
    }
}
