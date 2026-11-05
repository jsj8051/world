using System;
using World.Data;

namespace World.Logic;

/// <summary>
/// 季风指数（气压口径）：同一格冬夏的**海陆气压差是否反号**，以及反号幅度。
///
/// <code>
/// Δp_i(m) = p_i(m) − 同纬海洋平均 p(m)
/// 强度    = clamp((max Δp − min Δp) / MonsoonStrengthScale, 0, 1)  仅当 max/min 异号
/// </code>
/// 符号门控是必须的：没有它，年均偏置（低纬陆冷 / 中纬陆暖）会被误判成季风。
/// 风向反转**不进定义**（只做因果验证：气压反号 → 风向反转）。
///
/// @example
/// <code>
/// var strength = MonsoonIndex.Compute(grid, spec, monthly.MonthPressureHpa);
/// </code>
/// </summary>
public static class MonsoonIndex
{
	/// <summary>逐格季风强度 ∈ [0,1]；海格恒 0，无季节 / 不反号的陆格恒 0。</summary>
	public static float[] Compute(ClimateGrid grid, ClimateSpec spec, float[][] pressureMonthlyHpa)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (pressureMonthlyHpa == null || pressureMonthlyHpa.Length == 0)
			throw new ArgumentException("气压场没有月份", nameof(pressureMonthlyHpa));

		int months = pressureMonthlyHpa.Length;
		int n = grid.Count;
		for (int m = 0; m < months; m++)
		{
			if (pressureMonthlyHpa[m] == null || pressureMonthlyHpa[m].Length != n)
				throw new ArgumentException($"第 {m} 月气压数组长度与格数 {n} 不一致", nameof(pressureMonthlyHpa));
		}

		int buckets = Math.Max(1, spec.LatitudeBuckets);
		var oceanMean = new float[months][];
		for (int m = 0; m < months; m++)
			oceanMean[m] = LatitudeBands.MeanByBand(grid, pressureMonthlyHpa[m], buckets, oceanOnly: true);

		float scale = spec.MonsoonStrengthScale > 0f ? spec.MonsoonStrengthScale : 1f;
		var strength = new float[n];

		for (int i = 0; i < n; i++)
		{
			if (!grid.Land[i]) continue;

			int band = LatitudeBands.BucketOf(grid.LatDeg(i), buckets);
			float peak = 0f, trough = 0f;
			bool any = false;

			for (int m = 0; m < months; m++)
			{
				float mean = oceanMean[m][band];
				if (float.IsNaN(mean)) continue;   // 该纬度带没有海：这张表不参与
				float delta = pressureMonthlyHpa[m][i] - mean;
				if (!any)
				{
					peak = delta;
					trough = delta;
					any = true;
				}
				else
				{
					if (delta > peak) peak = delta;
					if (delta < trough) trough = delta;
				}
			}

			if (!any) continue;
			bool flips = (peak > 0f && trough < 0f) || (peak < 0f && trough > 0f);
			if (!flips) continue;

			strength[i] = Math.Clamp((peak - trough) / scale, 0f, 1f);
		}
		return strength;
	}
}
