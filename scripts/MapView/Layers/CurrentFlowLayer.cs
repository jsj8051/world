using Godot;
using World.HexPlanet;
using World.Services;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 5 洋流（2026-08-21 M4 命名统一：CurrentFlow 家族——策略 CurrentFlowLayer +
/// 画法组件 CurrentFlow，同目录配对；浅色底 + 整体流图+箭头 3D 网格，2026-08-21 用户拍板 v3）。</summary>
public sealed class CurrentFlowLayer : MapLayer
{
    public override int Id => 5;
    public override string Name => "洋流";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M2 10 L6 6 L10 10 L14 6 L18 10 L22 6 L26 10 M2 18 L6 14 L10 18 L14 14 L18 18 L22 14 L26 18' stroke='#eee' stroke-width='2' fill='none'/></svg>";
    public override bool HasOverlay => true;

    public override Color ColorOf(LayerContext ctx, HexTile tile)
        => PaleBase(ctx, tile.Id);

    /// <summary>洋流流图网格（原 MapViewer.BuildCurrentFlow；CurrentFlow 组件——整体流图+箭头）。</summary>
    public override Node3D BuildOverlay(LayerContext ctx, MapViewer host)
    {
        // ⚠️ 2026-08-21：流线法只需要 CurrentDirs 段（v3.1+ 存档）；Psi 仅旧环方法需要。
        if (ctx.Map == null || ctx.Map.CurrentDirs == null)
        {
            LogService.Log("MapViewer", "current flow skipped: 存档无洋流场（需 v3.1+ 地图存档）");
            return null;
        }
        var flow = new CurrentFlow();
        flow.Build(ctx.Map, ctx.RadiusKm);
        return flow;
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Row(new Color(1f, 0.45f, 0.15f), "暖流");
        b.Row(new Color(0.25f, 0.55f, 1f), "寒流");
        b.Text("粒子沿流线流动；速度/亮度 = 流速");
    }
}
