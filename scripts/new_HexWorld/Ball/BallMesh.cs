using System;
using System.Collections.Generic;
using Godot;
using World.NewHexWorld.Plate;   // H3Rivers（河流走廊）
using World.Utils;               // CoordUtil（LatLng → 球面点）
using World.Utils.H3;

namespace World.NewHexWorld;

public class BallMesh
{

    public Vector3[] DisplayVerts => _displayVerts;    // 渲染用：显示网格顶点
    public int[] DisplayIndices => _displayIndices;    // 渲染用：三角索引
    public Vector2[] DisplayUv => _displayUv;          // 渲染用：UV = 格纹素中心（region_data 查找）
    Vector3[] _displayVerts;    // 渲染网格：每格 1 格心 + m 角点（格内顶点共享）
    Vector2[] _displayUv;       // 逐顶点 UV：格数据纹理纹素中心（块内同值 → 插值恒定）
    int[] _displayIndices;      // 三角索引

    public float MeanInradius { get; private set; } = 0.005f;   // 六边格平均内切半径 ρ（rad，切面弦长）

    // ── 数据纹理布局（基格纹素 0..n-1 + 走廊子格纹素 n..；BuildTileMeshData 后有效）──
    public int DataTexelCount { get; private set; }     // 纹素总数 = 基格数 + 走廊子格数
    public int DataTexW { get; private set; }
    public int DataTexH { get; private set; }
    /// <summary>子格纹素 e → 父基格下标（BallView 烘数据纹理：子格场值复制父格 + 河档写 g 通道）。</summary>
    public int[] CorridorChildParent { get; private set; } = Array.Empty<int>();
    /// <summary>子格纹素 e → 河档 0..3（H3Rivers 路径追踪的产物，shader 河流材质用）。</summary>
    public byte[] CorridorChildGrade { get; private set; } = Array.Empty<byte>();


    public BallMesh()
    {

    }


    // ── 逐格数据纹理布局（全域材质的区域查找地址；本类填 UV 与 BallView 烘纹理共用同一布局）──

    // 纹理宽 = ceil(√N)（近方形；高 = ceil(N/宽)，尾行留空纹素，UV 恒指向有效格）。
    public static int DataTexWidth(int texelCount) => (int)Math.Ceiling(Math.Sqrt(texelCount));

    // 纹素下标 → 纹素中心 UV（纹素中心 + nearest 采样 → 浮点抖动也恒命中本格）。
    public static Vector2 CellDataUv(int texelIndex, int width, int height) =>
        new(((texelIndex % width) + 0.5f) / width, ((texelIndex / width) + 0.5f) / height);

    // 切线框架（CPU 侧度量用）：tu = up×ĉ 归一、tv = ĉ×tu。
    static (Vector3 Tu, Vector3 Tv) TangentFrame(Vector3 cDir)
    {
        Vector3 up = Math.Abs(cDir.Y) < 0.98f ? Vector3.Up : Vector3.Right;
        Vector3 tu = up.Cross(cDir).Normalized();
        return (tu, cDir.Cross(tu));
    }

    // 采样首个六边格实测平均内切半径 ρ（rad）：球面画线的尺度基准（线半宽 = frac × ρ × R，
    // BallView 换算成世界单位后喂 World.Render.SphereLines）。
    public void ComputeCellMetrics(Ball ball)
    {
        for (int i = 0; i < ball.CellIds.Length; i++)
        {
            ulong[] vids = H3.CellToVertexes(ball.CellIds[i]);
            if (vids.Length != 6) continue;
            var (tu, tv) = TangentFrame(ball.CellCenters[i].Normalized());
            float rho = 0f;
            for (int k = 0; k < 6; k++)
                rho += ((CornerTangent(ball, vids[k], tu, tv)
                       + CornerTangent(ball, vids[(k + 1) % 6], tu, tv)) * 0.5f).Length();
            MeanInradius = Math.Max(rho / 6f, 1e-5f);
            return;
        }
    }

    static Vector2 CornerTangent(Ball ball, ulong vid, Vector3 tu, Vector3 tv)
    {
        Vector3 d = ball.VertexPositions[ball.VertexIndexOf(vid)].Normalized();
        return new Vector2(d.Dot(tu), d.Dot(tv));
    }

    // 格块布局 = 每格 [格心, 角0..角m-1] + m 个扇形三角（格心, 角k, 角k+1），格内顶点共享——
    // UV 是格常量，共享无损。描边由 World.Render.SphereLines 独立渲染，格面不携带描边属性。
    //
    // 河流走廊：corridors 非空且有子格时，走廊基格的格面替换为其 res+1 子格面
    // （H3 aperture-7；CellToBoundary = 截角后真实多边形，与邻格无缝）；子格纹素追加在基格
    // 纹素之后（序 = 基格升序 × CellToChildren 序），场值由 BallView 从父格复制 + 河档写 region2.g。
    public void BuildTileMeshData(Ball ball, float radius, H3Rivers.Corridors corridors = null)
    {
        int n = ball.CellIds.Length;
        bool hasCorridor = corridors != null && corridors.Corridor != null && corridors.ChildCount > 0;
        var plan = hasCorridor ? new Dictionary<int, LatLng[][]>() : null;   // 走廊格 → 各子格边界多边形
        var rank = hasCorridor ? new int[n] : null;        // 基格 → 走廊位次（其前走廊格数）

        // 预遍历：精确容量 + 走廊计划（子格角数要过 H3 才知道）。rank[i] = i 之前的走廊格数
        //（单遍自然算出——升序遍历时它就是当前已见走廊数）。
        long vertCount = 0, idxCount = 0, childCount = 0;
        int corridorSeen = 0;
        for (int i = 0; i < n; i++)
        {
            if (plan != null && corridors.Corridor[i])
            {
                rank[i] = corridorSeen;
                ulong[] children = corridors.Children[corridorSeen];
                var bounds = new LatLng[children.Length][];
                for (int c = 0; c < children.Length; c++)
                {
                    bounds[c] = H3.CellToBoundary(children[c]);
                    vertCount += bounds[c].Length + 1;
                    idxCount += (long)bounds[c].Length * 3;
                    childCount++;
                }
                plan[i] = bounds;
                corridorSeen++;
            }
            else
            {
                int m = H3.IsPentagon(ball.CellIds[i]) ? 5 : 6;
                vertCount += m + 1;
                idxCount += m * 3;
            }
        }

        _displayVerts = new Vector3[vertCount];
        _displayUv = new Vector2[vertCount];
        _displayIndices = new int[idxCount];
        CorridorChildParent = new int[childCount];
        CorridorChildGrade = new byte[childCount];
        DataTexelCount = n + (int)childCount;
        DataTexW = DataTexWidth(DataTexelCount);
        DataTexH = (DataTexelCount + DataTexW - 1) / DataTexW;

        int v = 0, t = 0, child = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 uv = CellDataUv(i, DataTexW, DataTexH);

            if (plan != null && plan.TryGetValue(i, out var cellBounds))
            {
                // 走廊格：逐子格建面（父格面不建；子格纹素 = n + 子格全局序）
                ulong[] children = corridors.Children[rank[i]];
                byte[] childGrades = corridors.ChildGrades[rank[i]];
                for (int c = 0; c < children.Length; c++)
                {
                    var corners = cellBounds[c];
                    _displayVerts[v] = CoordUtil.LatLngToSphere(H3.CellToLatLng(children[c]), radius);
                    _displayUv[v] = CellDataUv(n + child, DataTexW, DataTexH);
                    for (int k = 0; k < corners.Length; k++)
                    {
                        _displayVerts[v + 1 + k] = CoordUtil.LatLngToSphere(corners[k], radius);
                        _displayUv[v + 1 + k] = _displayUv[v];
                        _displayIndices[t++] = v;
                        _displayIndices[t++] = v + 1 + k;
                        _displayIndices[t++] = v + 1 + (k + 1) % corners.Length;
                    }
                    CorridorChildParent[child] = i;
                    CorridorChildGrade[child] = childGrades[c];
                    v += corners.Length + 1;
                    child++;
                }
                continue;
            }

            ulong cell = ball.CellIds[i];
            ulong[] vids = H3.CellToVertexes(cell);
            int m = vids.Length;
            Vector3 center = Vector3.Zero;
            for (int k = 0; k < m; k++)
                center += ball.VertexPositions[ball.VertexIndexOf(vids[k])];
            center = center.Normalized() * radius;

            _displayVerts[v] = center;              // 格心 = 切面原点外的球面点
            _displayUv[v] = uv;
            for (int k = 0; k < m; k++)
            {
                _displayVerts[v + 1 + k] = ball.VertexPositions[ball.VertexIndexOf(vids[k])];
                _displayUv[v + 1 + k] = uv;
                _displayIndices[t++] = v;
                _displayIndices[t++] = v + 1 + k;
                _displayIndices[t++] = v + 1 + (k + 1) % m;
            }
            v += m + 1;
        }
    }

}
