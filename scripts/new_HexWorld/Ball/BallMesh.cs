using System;
using Godot;
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


    public BallMesh()
    {

    }


    // ── 逐格数据纹理布局（全域材质的区域查找地址；本类填 UV 与 BallView 烘纹理共用同一布局）──

    // 纹理宽 = ceil(√N)（近方形；高 = ceil(N/宽)，尾行留空纹素，UV 恒指向有效格）。
    public static int DataTexWidth(int cellCount) => (int)Math.Ceiling(Math.Sqrt(cellCount));

    // 格下标 → 该格纹素中心 UV（纹素中心 + nearest 采样 → 浮点抖动也恒命中本格）。
    public static Vector2 CellDataUv(int cellIndex, int width, int height) =>
        new(((cellIndex % width) + 0.5f) / width, ((cellIndex / width) + 0.5f) / height);

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
    // UV 是格常量，共享无损。2026-09-10 描边改几何线带（World.Render.SphereLines 独立渲染）后，
    // 格面不再携带逐边旗标/切面坐标/线段端点等描边属性（v4 的 UV2/COLOR/CUSTOM0 全删）。
    public void BuildTileMeshData(Ball ball, float radius)
    {
        int n = ball.CellIds.Length;
        _displayVerts = new Vector3[7 * n - 12];    // 六边格 7 顶点 ×(n−12) + 五边格 6×12
        _displayUv = new Vector2[_displayVerts.Length];
        _displayIndices = new int[18 * n - 36];     // 索引数 = 三角数×3：六边格 18 ×(n−12) + 五边格 15×12
        int texW = DataTexWidth(n), texH = (n + texW - 1) / texW;
        int v = 0, t = 0;
        for (int i = 0; i < n; i++)
        {
            ulong cell = ball.CellIds[i];
            Vector2 uv = CellDataUv(i, texW, texH);
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
