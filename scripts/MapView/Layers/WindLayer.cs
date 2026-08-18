using Godot;
using System.Collections.Generic;
using World.HexPlanet;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>图层 4 风场：浅色底（统一风场箭头由覆盖层 3D 网格显示，月份滑块切换）。
/// 覆盖层 = 季风月风箭头（2026-08-16：密集 3 倍 lat 步 12°→4°；每环经度点数随 cos(lat) 递减）。</summary>
public sealed class WindLayer : MapLayer
{
    public override int Id => 4;
    public override string Name => "风场";
    public override LayerCategory Category => LayerCategory.Climate;
    public override string IconSvg => "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M4 8 L18 8 M4 14 L22 14 M4 20 L14 20' stroke='#eee' stroke-width='2'/><path d='M20 4 L25 8 L20 12 Z' fill='#eee'/></svg>";
    public override bool HasOverlay => true;
    public override bool UsesMonth => true;

    public override Color ColorOf(LayerContext ctx, HexTile tile)
        => PaleBase(ctx, tile.Id);

    /// <summary>季风月风箭头网格（原 MapViewer.BuildMonsoonArrows；方向 = 当月季风环流风，稀疏采样。
    /// 无风（海洋/非季风区）不画）。⚠️ 月风场异步重算——未就绪返回 null，就绪后 MapViewer 补建。</summary>
    public override Node3D BuildOverlay(LayerContext ctx, MapViewer host)
    {
        host.EnsureMonthWind();   // 幂等：触发异步重算（未启动则启动）
        var monthWind = ctx.MonthWind;
        if (monthWind == null) return null;   // 未就绪 → ApplyMonthWind 后补建
        int month = ctx.Month;

        const float arrowLen = 0.045f;    // 小箭头（0.07 原值；只标方向，不随强度缩放）
        const float tailW = 0.016f;
        float radius = ctx.RadiusKm * MapViewer.OverlayLiftFactor;   // 浮在球面上方防 z-fighting

        var verts = new List<Vector3>();
        var indices = new List<int>();

        // ⚠️ 2026-08-16：密集 3 倍（lat 步 12°→4°）；每环经度点数随 cos(lat) 递减（极区少点）
        for (float lat = -88f; lat <= 88f; lat += 4f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(8, Mathf.RoundToInt(36 * cosLa));
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                var dir = new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo));
                int vid = ctx.Map.NearestVertex(dir);
                var wind = monthWind[month][vid];
                if (wind.LengthSquared() < 1e-9f) continue;   // 无风区不画
                var wDir = wind.Normalized();                 // 只标记方向
                var side = dir.Cross(wDir).Normalized();

                Vector3 tailC = dir - wDir * arrowLen * 0.35f;
                Vector3 tip = dir + wDir * arrowLen * 0.65f;
                Vector3 t1 = (tailC + side * tailW).Normalized() * radius;
                Vector3 t2 = (tailC - side * tailW).Normalized() * radius;
                Vector3 tipS = tip.Normalized() * radius;

                int baseIdx = verts.Count;
                verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // 青蓝色（海风色；与盛行风橙色区分）
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.25f, 0.78f, 0.92f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>月份切换：重建箭头（若已建——几何基于当月风场）。</summary>
    public override void OnMonthChanged(LayerContext ctx, int month)
        => ctx.RequestOverlayRebuild?.Invoke();

    public override void BuildLegend(LegendBuilder b, LayerContext ctx)
    {
        b.Text("→ 箭头 = 盛行风向（月风场）");
        b.Text("疏密 = 风速强度");
        b.Text("月份滑块切换 1-12 月");
    }
}
