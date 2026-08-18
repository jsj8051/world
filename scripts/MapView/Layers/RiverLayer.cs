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

    /// <summary>河流网格（原 MapViewer.BuildRivers；每条河独立颜色，支流汇合截断）。</summary>
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
        var vertList = new List<Vector3>();
        var colorList = new List<Color>();
        var indexList = new List<int>();

        // 主河先画（长→短），支流遇已画顶点截断（汇合点）
        var painted = new HashSet<int>();
        paths.Sort((a, b) => b.Length.CompareTo(a.Length));
        // ⚠️ 2026-08-06：河宽按分辨率缩放——固定 halfW 在 n=128 格距减半时相对粗 2 倍。
        //   统一按格距比例：halfW = 格距 × 0.13（n=64 时即原 0.004）
        int simN = Icosahedron.GridNFromVertexCount(n);
        float gridArc = Mathf.Tau / (Mathf.Sqrt(10f) * Mathf.Max(8, simN));
        float halfW = gridArc * 0.13f;   // 河宽 ≈ 0.26 格距（观感统一，随分辨率缩放）
        int riverCount = 0;
        foreach (var path in paths)
        {
            // 每条河独立颜色：HSL 色相黄金角循环（相邻河差异最大）
            float hue = GoldenHue(riverCount);
            var c = HslToRgb(hue, 0.9f, 0.55f);
            riverCount++;
            bool drawn = false;
            for (int i = 0; i < path.Length - 1; i++)
            {
                int va = path[i], vb = path[i + 1];
                if (painted.Contains(va)) break;   // 遇汇合点 → 支流段结束
                painted.Add(va);
                Vector3 a = verts[va], b = verts[vb];
                Vector3 seg = b - a;
                if (seg.LengthSquared() < 1e-12f) continue;
                Vector3 side = seg.Cross(a).Normalized();
                Vector3 l0 = (a + side * halfW).Normalized() * radius;
                Vector3 r0 = (a - side * halfW).Normalized() * radius;
                Vector3 l1 = (b + side * halfW).Normalized() * radius;
                Vector3 r1 = (b - side * halfW).Normalized() * radius;
                int bi = vertList.Count;
                vertList.Add(l0); vertList.Add(r0); vertList.Add(l1); vertList.Add(r1);
                colorList.Add(c); colorList.Add(c); colorList.Add(c); colorList.Add(c);
                indexList.Add(bi); indexList.Add(bi + 1); indexList.Add(bi + 2);
                indexList.Add(bi + 1); indexList.Add(bi + 3); indexList.Add(bi + 2);
                drawn = true;
            }
            if (!drawn) riverCount--;   // 全被截断（纯支流无独有段）→ 不计
        }

        if (vertList.Count == 0)
        {
            LogService.Log("MapViewer", "rivers: 无可见河道");
            return null;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertList.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colorList.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indexList.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Row(new Color(0.25f, 0.45f, 0.75f), "湖泊");
        b.Row(new Color(0.35f, 0.70f, 1.00f), "河流");
        b.Text("干涸盆地（盐湖）不显示");
    }
}
