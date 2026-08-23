using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

using World.CivSim.Entities;
namespace World.MapView.Layers;

/// <summary>图层 19 聚落（2026-08-19 阶段3 聚落设计）：新村→城市分级色 + 废墟灰；无聚落暗底。</summary>
public sealed class SettlementLayer : MapLayer
{
    public override int Id => 19;
    public override string Name => "聚落";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M4 13 L14 4 L24 13 M6 12 V24 H22 V12' stroke='#fd8' stroke-width='2' fill='none' stroke-linejoin='miter'/><path d='M12 24 V17 H16 V24 M10 15 H18' stroke='#fd8' stroke-width='2'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TileSettlement[id] == 0) return SeaColor;
        byte sl = ctx.Cache.TileSettlement[id];
        if (sl == 0) return new Color(0.22f, 0.22f, 0.25f);   // 无聚落陆地（暗底——突出聚落）
        return SettlementLevelColors[Mathf.Clamp(sl - 1, 0, SettlementLevelColors.Length - 1)];
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Row(SettlementLevelColors[0], "新村/营地");
        b.Row(SettlementLevelColors[1], "村庄");
        b.Row(SettlementLevelColors[2], "城镇");
        b.Row(SettlementLevelColors[3], "城市");
        b.Row(SettlementLevelColors[4], "废墟");
        b.Text("农业部落（settle）驻扎点固化；场所比人长寿，新部落可接管");
    }
}
