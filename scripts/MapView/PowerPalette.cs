using Godot;
using System.Collections.Generic;

namespace World.MapView;

/// <summary>
/// 调色板构建（2026-08-16 用户"所有独立势力颜色都要不一样"终版；势力范围图层同用——2026-08-16）：
/// **最远点采样**——在避开海蓝相的 HSL 候选网格上贪心挑选：每新势力取与已选颜色
/// （含海色/无势力灰两个虚拟锚点）最小 RGB 距离最大的候选。性质：
///   • 任意两势力颜色距离有下界（实测 291 势力最小色距 ≥0.1，远超 0.05 肉眼阈值）；
///   • 与海色、无势力灰天然可区分（锚点参与最远点选择）；
///   • 确定性：同 id 集 → 同秩 → 同色（候选网格顺序固定 + 并列取小索引）。
/// 为什么不用散列/排序秩黄金角：hue=φ×id 与 hue=φ×秩 在斐波那契距 id/秩对上必近撞色相
/// （散列实测最小色距 0.011；排序秩实测 0.039——仍 <0.05 肉眼阈值，两势力看似同色）。
/// 候选网格：96 色相步（3.75°，排除海蓝相 0.48-0.72）× 12 饱和 × 10 明度 ≈ 8.6 千候选
/// ——势力范围层领地可达 ~1500 个（旧 48 步网格 2368 候选在 1500 规模下色距跌破阈值）。
/// 明度上限 0.75：0.85 会产出 RGB 全 >0.7 的近白色（势力范围层"全白"症状，用户反馈 2026-08-16）。
/// </summary>
public static class PowerPalette
{
    /// <summary>海洋统一色（与 MapViewer.SeaColor 同值；锚点之一——势力色必须与海色可分）。</summary>
    public static readonly Color SeaColor = new Color(0.10f, 0.22f, 0.48f);

    /// <summary>构建势力 id（任意顺序，内部排序定秩）→ 颜色的调色板。n=0 返回空表。</summary>
    public static Dictionary<int, Color> Build(IReadOnlyCollection<int> ids)
    {
        var sorted = new List<int>(ids);
        sorted.Sort();
        int n = sorted.Count;
        var pal = new Dictionary<int, Color>(n);
        if (n == 0) return pal;

        // ── 候选网格：hue 避开海蓝相（0.48-0.72，同 AvoidSeaHue 语义）；sat/lig 覆盖亮色域 ──
        //   明度 0.30-0.75（上限 0.75：更高会出 RGB 全 >0.7 的近白色——势力范围层"全白"症状）
        var cands = new List<Color>(8640);
        for (int i = 0; i < 96; i++)
        {
            float h = i / 96f;   // 3.75° 步长
            if (h >= 0.48f && h <= 0.72f) continue;   // 排除蓝-青相（海色区间）
            for (int j = 0; j < 12; j++)
            {
                float s = 0.40f + 0.05f * j;   // 0.40-0.95
                for (int k = 0; k < 10; k++)
                    cands.Add(HslToRgb(h, s, 0.30f + 0.05f * k));   // lig 0.30-0.75
            }
        }

        // ── 最远点采样：先算每候选到锚点（海色/无势力灰）的最小距离，再逐次挑"最小距离最大"者 ──
        var anchors = new List<Color> { SeaColor, new Color(0.25f, 0.25f, 0.28f) };
        var minD = new float[cands.Count];
        for (int c = 0; c < cands.Count; c++)
        {
            float md = float.MaxValue;
            for (int a = 0; a < anchors.Count; a++)
                md = Mathf.Min(md, Dist(cands[c], anchors[a]));
            minD[c] = md;
        }
        var chosen = new Color[n];
        for (int r = 0; r < n; r++)
        {
            int best = -1;
            for (int c = 0; c < cands.Count; c++)
                if (minD[c] > 0f && (best < 0 || minD[c] > minD[best]))
                    best = c;
            if (best < 0)
                best = r % cands.Count;   // 候选耗尽兜底（理论不触发：候选 2368 ≫ 势力数）
            chosen[r] = cands[best];
            for (int c = 0; c < cands.Count; c++)
                minD[c] = Mathf.Min(minD[c], Dist(cands[c], cands[best]));
        }

        for (int r = 0; r < n; r++) pal[sorted[r]] = chosen[r];
        return pal;
    }

    /// <summary>L1 RGB 距离（|ΔR|+|ΔG|+|ΔB|；与探针碰撞检查同口径）。</summary>
    public static float Dist(Color a, Color b)
        => Mathf.Abs(a.R - b.R) + Mathf.Abs(a.G - b.G) + Mathf.Abs(a.B - b.B);

    private static Color HslToRgb(float h, float s, float l)
    {
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float H2R(float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
        return new Color(H2R(h + 1f / 3f), H2R(h), H2R(h - 1f / 3f));
    }
}
