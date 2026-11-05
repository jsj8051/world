using System;
using System.Collections.Generic;

namespace World.Utils;

/// <summary>
/// 通用数值核：平滑阶跃、高斯带、已排序样本的分位。
/// 只收「没有世界语义、且至少两处共用」的口径；单处使用的公式留在它的语义拥有者旁边。
/// </summary>
public static class MathOps
{
	/// <summary>平滑阶跃：<c>x ≤ a</c> ⇒ 0、<c>x ≥ b</c> ⇒ 1，中间二阶连续。<c>b ≤ a</c> ⇒ 硬阶跃。</summary>
	public static float Smoothstep(float a, float b, float x)
	{
		if (!(b > a)) return x >= b ? 1f : 0f;
		float t = (x - a) / (b - a);
		if (t <= 0f) return 0f;
		if (t >= 1f) return 1f;
		return t * t * (3f - 2f * t);
	}

	/// <summary>高斯带：<c>exp(−((d−center)/w)²)</c>；<c>|t| &gt; 4</c> ⇒ 0（远场截断）。<c>w ≤ 0</c> ⇒ 0（关断）。</summary>
	public static float GaussianBand(float d, float center, float w)
	{
		if (!(w > 0f)) return 0f;
		float t = (d - center) / w;
		if (t > 4f || t < -4f) return 0f;
		return MathF.Exp(-t * t);
	}

	/// <summary>中心在 0 的高斯带（<c>exp(−(d/w)²)</c>）。</summary>
	public static float GaussianBand(float d, float w) => GaussianBand(d, 0f, w);

	/// <summary>已升序排序的样本上取分位（0~1）；空样本 ⇒ 0。</summary>
	public static float QuantileSorted(IReadOnlyList<float> sorted, double q)
	{
		if (sorted == null) throw new ArgumentNullException(nameof(sorted));
		if (sorted.Count == 0) return 0f;
		int idx = (int)Math.Floor(q * sorted.Count);
		if (idx < 0) idx = 0;
		if (idx >= sorted.Count) idx = sorted.Count - 1;
		return sorted[idx];
	}
}
