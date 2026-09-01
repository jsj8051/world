using Godot;
using World.HexPlanet;
using static World.Utils.ColorRamp;

namespace World.MapView.Layers;

/// <summary>图层 2 降水：陆地自适应色带（用户拍板：最低到最高归一化，不用固定 2000mm）。
/// 2026-08-31 色带定义内聚本层（MonthPrecipLayer 月份视图引用 PrecipStops）。</summary>
public sealed class PrecipitationLayer : MapLayer
{
    public override int Id => 2;
    public override string Name => "降水";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L19 13 L14 23 L9 13 Z' fill='#eee'/><path d='M6 20 L4 25 M22 20 L24 25' stroke='#eee' stroke-width='2'/></svg>";

    // ── 降水色带（2026-08-31：内聚本层；MonthPrecipLayer 引用本定义）──

    /// <summary>降水连续色带（位置=0..1 归一化——调用方按陆地 min-max 自适应归一化；用户拍板：
    /// 不用固定 2000mm。【改色带】= 编辑点位/颜色，或加中间停点做多跨度渐变。</summary>
    public static readonly ColorStop[] PrecipStops =
    {
        new(0f, new Color(0.90f, 0.80f, 0.40f)),  // 少雨黄
        new(1f, new Color(0.10f, 0.30f, 0.70f)),  // 多雨蓝
    };

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        float x = Mathf.Clamp((ctx.Cache.TilePrecip[id] - ctx.Cache.PrecipMin) / (ctx.Cache.PrecipMax - ctx.Cache.PrecipMin), 0f, 1f);
        return RampSample(PrecipStops, x);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Gradient(RampLegendColors(PrecipStops), $"{ctx.Cache.PrecipMin:F0}mm", $"{ctx.Cache.PrecipMax:F0}mm");
        b.Text("陆地自适应色带（随地图分布）");
    }
}
