using System;
using System.Collections.Generic;
using Godot;

namespace World.Utils;

/// <summary>
/// 通用数值核：平滑阶跃、高斯带、已排序样本的分位。
/// 收录口径：**没有世界语义的纯函数/纯数据类型**一律放 Utils（不限使用处数）；带板块/气候/河流等
/// 领域语义的实现留在它的语义拥有者旁边。
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

	/// <summary>
	/// 球面角度核：两个单位方向的夹角余弦的四次方（重合 = 1，垂直 = 0，反向 = 1，故调用方需保证同向）。
	/// 四次方在"最近格权重占优"和"邻域混合足够平滑"之间取折中：二次方会拉偏方向，更高次退化成单格硬切换。
	/// </summary>
	public static float CosFourthWeight(Vector3 dirA, Vector3 dirB)
	{
		float cos = Mathf.Clamp(dirA.Dot(dirB), -1f, 1f);
		float w = cos * cos;
		return w * w;
	}

	/// <summary>
	/// 两端平滑淡出权重：<c>t ∈ [0,1]</c> 表示沿线段的位置，两端各 <paramref name="edge"/> 宽度的区间内
	/// 用 smoothstep 收到 0，中段为 1。用于让线头/线尾自然消失，而不是被齐平切断。
	/// </summary>
	public static float EndTaper(float t, float edge)
	{
		if (!(edge > 0f)) return 1f;
		float head = Mathf.Clamp(t / edge, 0f, 1f);
		float tail = Mathf.Clamp((1f - t) / edge, 0f, 1f);
		return head * head * (3f - 2f * head) * tail * tail * (3f - 2f * tail);
	}

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
