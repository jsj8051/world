using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 14 独立势力（2026-08-17）：每势力独立色——**最远点采样调色板**（2026-08-16 定案）。
/// 最高聚合层显示：酋邦（跨部落联盟）> 部落（领地≥2）> 独立 band；带边界 A 通道构建（NeedsPowerBorders）。</summary>
public sealed class PowerLayer : MapLayer
{
    public override int Id => 14;
    public override string Name => "独立势力";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M6 20 L6 9 L11 14 L14 6 L17 14 L22 9 L22 20 Z M4 23 H24' stroke='#fd8' stroke-width='2' fill='none' stroke-linejoin='miter' stroke-linecap='round'/></svg>";
    public override bool NeedsPowerBorders => true;

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TilePower[id] == 0) return SeaColor;
        int powerId = ctx.Cache.TilePower[id];
        if (powerId == 0) return new Color(0.25f, 0.25f, 0.28f);
        if (ctx.Cache.PowerPalette != null && ctx.Cache.PowerPalette.TryGetValue(powerId, out var pc)) return pc;
        return PowerColor(powerId);   // 兜底（理论不触发——调色板覆盖全部显示 id）
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Row(new Color(0.25f, 0.25f, 0.28f), "无人 / 海洋");
        b.Text("每独立势力一种颜色（两两可区分）");
        b.Text("酋邦（跨部落联盟）> 部落（领地≥2）> 独立 band");
    }
}
