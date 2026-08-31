using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 18 政体（2026-08-17）：独立势力基础上按政体类型分色——
/// band=灰蓝 部落=绿 酋邦=红橙 国家=金（2026-08-16 阶段4 国家涌现）。
/// 纯政体色（2026-08-18 用户：部落为何多色——去掉势力微扰——
/// 政体地图=政体类型色，势力区分看独立势力图层 14）。</summary>
public sealed class PolityLayer : MapLayer
{
    public override int Id => 18;
    public override string Name => "政体";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M4 24 H24 M4 24 V19 H13 V14 H20 V9 H24 V5' stroke='#f8a' stroke-width='2.5' fill='none' stroke-linejoin='miter' stroke-linecap='round'/><path d='M4 22 H24' stroke='#f8a' stroke-width='1' opacity='0.4'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TilePower[id] == 0) return SeaColor;
        int powerId = ctx.Cache.TilePower[id];
        if (powerId == 0) return new Color(0.25f, 0.25f, 0.28f);
        if (ctx.SelectionId >= 0)
        {
            // 选国形态：选中政权高亮，其余政体压暗（NationSelectMenu 写 SelectionId 后重绘）
            if (powerId == ctx.SelectionId) return HslToRgb(0.12f, 0.65f, 0.62f);
            return ColorOfBase(ctx, id, powerId) * 0.55f;
        }
        return ColorOfBase(ctx, id, powerId);
    }

    /// <summary>基础政体色（原 hue switch 四色逻辑；选国形态压暗时复用取色）。</summary>
    private static Color ColorOfBase(LayerContext ctx, int id, int powerId)
    {
        float hue = ctx.Cache.TilePolityKind[id] switch
        {
            3 => 0.12f,    // 国家：金（王权/官僚——制度化）
            2 => 0.045f,   // 酋邦：红橙
            1 => 0.35f,    // 部落：绿
            _ => 0.60f,    // band：灰蓝
        };
        return HslToRgb(hue, 0.45f, 0.55f);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Row(HslToRgb(0.60f, 0.30f, 0.55f), "独立 band（无组织）");
        b.Row(HslToRgb(0.35f, 0.50f, 0.55f), "部落（领地凝聚）");
        b.Row(HslToRgb(0.045f, 0.58f, 0.55f), "酋邦（联盟+酋长）");
        b.Row(HslToRgb(0.12f, 0.45f, 0.55f), "国家（都城+官僚，2026-08-16 阶段4）");
        b.Text("同类政体同色系；势力间色相微扰可辨");
    }
}
