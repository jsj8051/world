using Godot;
using World.HexPlanet;
using World.MapGen;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 10 月降水：与总降水同一自适应色带（当月陆地 min-max 归一化；月份滑块切换）。
/// ⚠️ 2026-08-16 v3（用户拍板）：与总降水同色带同统计方式；×12 换算回年尺度
///   → 非季风区≈年降水色，季风区 7 月深蓝 / 1 月枯黄；min-max 自适应当月分布。
/// 月份切换（RefreshMonthPrecip + 重算颜色）M3 接入。</summary>
public sealed class MonthPrecipLayer : MapLayer
{
    public override int Id => 10;
    public override string Name => "月降水";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><rect x='4' y='6' width='20' height='18' fill='none' stroke='#7cf' stroke-width='2'/><path d='M4 12 H24 M9 3 V9 M19 3 V9' stroke='#7cf' stroke-width='2'/><path d='M8 18 H14 M8 22 H20' stroke='#7cf' stroke-width='2'/></svg>";
    public override bool UsesMonth => true;

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.Cache.TileMonthPrecip == null || ctx.Map == null || ctx.Map.MonthPrecip == null)
            return ctx.IsSea(id)
                ? SeaColor
                : new Color(0.72f, 0.70f, 0.58f);
        if (ctx.IsSea(id)) return SeaColor;
        float mm = FieldCodec.ByteMonthPrecipToMm(ctx.Cache.TileMonthPrecip[id], ctx.Cache.TilePrecip[id]) * 12f;   // 等效年尺度（比例×年降水×12）
        float x = Mathf.Clamp((mm - ctx.Cache.MonthPrecipMin) / (ctx.Cache.MonthPrecipMax - ctx.Cache.MonthPrecipMin), 0f, 1f);
        return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    }

    /// <summary>月份切换：刷新当月降水缓存 + 重算颜色（原滑块回调分支）。</summary>
    public override void OnMonthChanged(LayerContext ctx, int month)
    {
        ctx.RefreshMonthPrecip();
        ctx.RequestRecolor?.Invoke();
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Gradient(
            new[] { new Color(0.90f, 0.80f, 0.40f), new Color(0.10f, 0.30f, 0.70f) },
            $"{ctx.Cache.MonthPrecipMin:F0}mm", $"{ctx.Cache.MonthPrecipMax:F0}mm");
        b.Text("当月降水（×12 年尺度色带）");
    }
}
