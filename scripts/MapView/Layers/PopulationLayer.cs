using Godot;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 12 人口：无人（采集格）+ 16 档等比色块（log 分位，与地图同色；驻扎格人口）。
/// log 压缩 + P1/P99 分位自适应色带（无人=暗灰；黄→橙红）。</summary>
public sealed class PopulationLayer : MapLayer
{
    public override int Id => 12;
    public override string Name => "人口";
    public override LayerCategory Category => LayerCategory.Human;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><circle cx='9' cy='7' r='3' fill='#fd8'/><circle cx='19' cy='7' r='3' fill='#fd8'/><circle cx='14' cy='15' r='3' fill='#fd8'/><path d='M9 13 L9 25 M19 13 L19 25 M14 21 L14 25' stroke='#fd8' stroke-width='2.5' stroke-linecap='round'/></svg>";

    public override Color ColorOf(LayerContext ctx, HexTile tile)
    {
        int id = tile.Id;
        if (ctx.IsSea(id) && ctx.Cache.TilePop[ctx.TileIndex.FaceToVertex(id)] <= 0f) return SeaColor;   // ⚠️ 显示海（真海）；近海逻辑陆地=陆地底
        float p = ctx.Cache.TilePop[ctx.TileIndex.FaceToVertex(id)];
        if (p <= 0f) return new Color(0.25f, 0.25f, 0.28f);   // 无人陆地
        float x = Mathf.Clamp((Mathf.Log(p + 1f) - ctx.Cache.PopLogMin) / (ctx.Cache.PopLogMax - ctx.Cache.PopLogMin), 0f, 1f);
        return new Color(0.95f, 0.75f, 0.25f).Lerp(new Color(0.80f, 0.15f, 0.05f), x);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        var lo = new Color(0.95f, 0.75f, 0.25f);
        var hi = new Color(0.80f, 0.15f, 0.05f);
        b.Row(new Color(0.25f, 0.25f, 0.28f), "无人（采集格 / 海洋）");
        if (ctx.Cache.PopMax <= 0f)
        {
            b.Text("（无人口数据）");
            return;
        }
        for (int i = 0; i <= 15; i++)
        {
            float x = i / 15f;
            float p = Mathf.Exp(ctx.Cache.PopLogMin + x * (ctx.Cache.PopLogMax - ctx.Cache.PopLogMin)) - 1f;
            // ⚠️ 2026-08-17 用户反馈"人口怎么还能是小数"：人口物理上是整数——
            //   模型层 P 是 float（连续宏观增长），显示层取整（<1 显示 "<1" 防与无人灰混淆）
            string label = i == 15 ? $"≥ {FmtPop(p)}（最高 {FmtPop(ctx.Cache.PopMax)}）" : FmtPop(p);
            b.Row(lo.Lerp(hi, x), label);
        }
        b.Text("驻扎格人口（人/格）· log 分位自适应");
    }

    /// <summary>人口显示取整（2026-08-17 用户反馈小数）：&lt;1 显示 "&lt;1"（防与无人灰混淆），≥1 整数。</summary>
    private static string FmtPop(float p) => p < 1f ? "<1" : $"{p:F0}";
}
