using Godot;
using System.Collections.Generic;
using World.HexPlanet;
using World.MapGen;
using World.Services;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 6 河流：浅色底（河道由覆盖层 3D 网格显示，湖格填湖蓝）。
/// 覆盖层 = 主河道重建（riverLevel + flow → RebuildPaths）→ 每条河独立颜色（HSL 黄金角），
/// 支流在汇合点截断（painted 集合），主河先画（长→短）。</summary>
public sealed class RiverLayer : MapLayer
{
    public override int Id => 6;
    public override string Name => "河流";
    public override LayerCategory Category => LayerCategory.Geo;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M6 2 L10 6 L8 10 L14 14 L12 18 L15 22 L14 26' stroke='#6cf' stroke-width='3' fill='none' stroke-linecap='round'/></svg>";
    public override bool HasOverlay => true;

    public override Color ColorOf(LayerContext ctx, HexTile tile)
        => PaleBase(ctx, tile.Id);

    /// <summary>河流网格（原 MapViewer.BuildRivers；每条河独立颜色，支流汇合收束）。
    /// 几何画法已抽至 RiverMesh（2026-09-01：蜿蜒/渐变宽/防穿模——见组件头注释）。</summary>
    public override Node3D BuildOverlay(LayerContext ctx, MapViewer host)
    {
        if (ctx.Map == null || ctx.Map.RiverLevel == null || ctx.Map.RiverFlow == null)
        {
            LogService.Log("MapViewer", "rivers skipped: 存档无河流段（旧版）");
            return null;
        }

        // 归一化海拔（读档 Elev 是米 → 归一化，<0 = 海洋）
        var verts = ctx.Map.Verts;
        int n = verts.Length;
        var eNorm = new float[n];
        float range = Mathf.Max(-ctx.Map.MinElev, ctx.Map.MaxElev);
        for (int i = 0; i < n; i++) eNorm[i] = range > 1e-6f ? ctx.Map.Elev[i] / range : 0f;

        // 重建主河道（源头 → 入海/盆地）
        var paths = RiverSystem.RebuildPaths(ctx.Map.RiverFlow, ctx.Map.RiverLevel, eNorm);
        if (paths.Count == 0)
        {
            LogService.Log("MapViewer", "rivers: 无主河道");
            return null;
        }

        float radius = ctx.RadiusKm * MapViewer.OverlayLiftFactor;   // 略高于球面，避免 z-fighting
        // ⚠️ 2026-08-06：河宽/蜿蜒按分辨率缩放——固定绝对宽在 n=128 格距减半时相对粗 2 倍。
        //   统一按格距比例：宽 = 格距 × 系数（RiverMesh 内 HalfWMin/Max），n=64 时原 0.13 为中值档
        int simN = Icosahedron.GridNFromVertexCount(n);
        float gridArc = Mathf.Tau / (Mathf.Sqrt(10f) * Mathf.Max(8, simN));
        return RiverMesh.Build(ctx.Map, paths, radius, gridArc);
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Row(new Color(0.25f, 0.45f, 0.75f), "湖泊");
        b.Row(new Color(0.35f, 0.70f, 1.00f), "河流");
        b.Text("干涸盆地（盐湖）不显示");
    }
}
