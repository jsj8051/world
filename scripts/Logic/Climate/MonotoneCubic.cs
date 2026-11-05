using System;

namespace World.Logic;

/// <summary>
/// 分段三次 Hermite（PCHIP / Fritsch–Carlson 单调保形）：节点处 C1、段内不越过两端点值。
/// 气压廓线必须用它——普通三次样条会在相邻节点之间过冲，过冲就是多出来的局部极值，
/// 在气压场上会闭合出一圈圈不该存在的等压线。
///
/// @example
/// <code>
/// var slopes = MonotoneCubic.Slopes(xs, ys);
/// float p = MonotoneCubic.Evaluate(xs, ys, slopes, latDeg);
/// </code>
/// </summary>
internal static class MonotoneCubic
{
	/// <summary>节点斜率（单调保形）：同号段用加权调和平均，异号段（局部极值）取 0。</summary>
	public static float[] Slopes(float[] xs, float[] ys)
	{
		if (xs == null) throw new ArgumentNullException(nameof(xs));
		if (ys == null) throw new ArgumentNullException(nameof(ys));
		int n = xs.Length;
		if (n < 2) throw new ArgumentException("至少需要 2 个节点", nameof(xs));
		if (ys.Length != n) throw new ArgumentException("xs 与 ys 长度不一致", nameof(ys));

		var h = new float[n - 1];
		var d = new float[n - 1];
		for (int k = 0; k < n - 1; k++)
		{
			h[k] = xs[k + 1] - xs[k];
			if (h[k] <= 0f) throw new ArgumentException("节点必须严格递增", nameof(xs));
			d[k] = (ys[k + 1] - ys[k]) / h[k];
		}

		var m = new float[n];
		m[0] = d[0];
		m[n - 1] = d[n - 2];
		for (int k = 1; k < n - 1; k++)
		{
			if (d[k - 1] * d[k] <= 0f) { m[k] = 0f; continue; }
			float w1 = 2f * h[k] + h[k - 1];
			float w2 = h[k] + 2f * h[k - 1];
			m[k] = (w1 + w2) / (w1 / d[k - 1] + w2 / d[k]);
		}
		return m;
	}

	/// <summary>求值（越界钳到端点；线性找段——本工程节点数 ≤ 13，不值得二分）。</summary>
	public static float Evaluate(float[] xs, float[] ys, float[] slopes, float x)
	{
		int n = xs.Length;
		if (x <= xs[0]) return ys[0];
		if (x >= xs[n - 1]) return ys[n - 1];

		int k = 0;
		while (k < n - 2 && x >= xs[k + 1]) k++;

		float h = xs[k + 1] - xs[k];
		float t = (x - xs[k]) / h;
		float t2 = t * t;
		float t3 = t2 * t;
		float h00 = 2f * t3 - 3f * t2 + 1f;
		float h10 = t3 - 2f * t2 + t;
		float h01 = -2f * t3 + 3f * t2;
		float h11 = t3 - t2;
		return h00 * ys[k] + h10 * h * slopes[k] + h01 * ys[k + 1] + h11 * h * slopes[k + 1];
	}
}
