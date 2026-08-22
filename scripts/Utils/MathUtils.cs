using Godot;

namespace World.Utils;

/// <summary>通用数学工具（2026-08-21：插值/曲线等图形数学工具集中地——图层渲染、诊断共用）。</summary>
public static class MathUtils
{
    /// <summary>Catmull-Rom 样条单点：t∈[0,1] 时位于 P1→P2 之间（精确经过 P1/P2，
    /// 端点切线由相邻点决定：P1 处 ∥ P2−P0，P2 处 ∥ P3−P1 → 相邻段 C1 连续无折角）。
    /// 三次多项式：P(t) = 0.5[(2P1) + (−P0+P2)t + (2P0−5P1+4P2−P3)t² + (−P0+3P1−3P2+P3)t³]。</summary>
    /// <param name="p0">段起点前一个控制点——决定段起点（P1 处）的切线方向（P2−P0）；两端用自身复制。</param>
    /// <param name="p1">段起点：t=0 时曲线精确经过此点。</param>
    /// <param name="p2">段终点：t=1 时曲线精确经过此点。</param>
    /// <param name="p3">段终点后一个控制点——决定段终点（P2 处）的切线方向（P3−P1）；两端用自身复制。</param>
    /// <param name="t">段内参数 0~1（0=P1，1=P2；通常取 1/(subdivisions+1) 的整数倍均匀采样）。</param>
    public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    /// <summary>Catmull-Rom 填充版（零分配）：把控制点列平滑插值写入 dest——
    /// 每对相邻点之间插入 subdivisions 个子点（端点保留，段与段共享端点）。
    /// 返回写入点数 = 1 + (points.Length−1) × (subdivisions+1)；dest 容量须足够。
    /// 球面点列注意：插值在 3D 弦上进行，调用方自行归一化投影回球面。</summary>
    /// <param name="points">控制点列（长度 ≥2；曲线经过全部点）。</param>
    /// <param name="subdivisions">每段插入的子点数（≥1；越大曲线越平滑，输出点数/开销同比增大）。
    /// 例：12 点 + subdivisions=2 → 输出 1+11×3 = 34 点。</param>
    /// <param name="dest">输出缓冲（预分配复用，避免每帧 GC——调用方按
    /// 1 + (points.Length−1)×(subdivisions+1) 大小分配一次）。</param>
    /// <returns>实际写入 dest 的点数（= 1 + (points.Length−1)×(subdivisions+1)）。</returns>
    public static int CatmullRomFill(Vector3[] points, int subdivisions, Vector3[] dest)
    {
        int segs = points.Length - 1;
        int step = subdivisions + 1;
        dest[0] = points[0];
        int o = 1;
        for (int i = 0; i < segs; i++)
        {
            Vector3 p0 = points[Mathf.Max(0, i - 1)];
            Vector3 p1 = points[i];
            Vector3 p2 = points[i + 1];
            Vector3 p3 = points[Mathf.Min(points.Length - 1, i + 2)];
            for (int s = 1; s <= subdivisions; s++)
                dest[o++] = CatmullRom(p0, p1, p2, p3, s / (float)step);
            dest[o++] = p2;   // 段终点 = 下一段起点（最后一段终点 = 最后控制点）
        }
        return o;
    }
}
