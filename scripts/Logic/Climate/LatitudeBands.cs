using System;

namespace World.Logic;

/// <summary>
/// 纬度分带统计：把 [-90, 90] 等分成 N 带，给气压（全格均值）、季风（只取海格均值）与热赤道
/// 提供同一套分桶口径。空带返回 <see cref="float.NaN"/>，由调用方决定是跳过还是回退。
///
/// @example
/// <code>
/// var bandMean = LatitudeBands.MeanByBand(grid, tempC, spec.LatitudeBuckets);
/// var oceanMean = LatitudeBands.MeanByBand(grid, tempC, spec.LatitudeBuckets, oceanOnly: true);
/// </code>
/// </summary>
public static class LatitudeBands
{
	/// <summary>纬度（度）→ 带下标；越界钳到首尾带。</summary>
	public static int BucketOf(float latDeg, int buckets) =>
		Math.Clamp((int)((latDeg + 90f) / 180f * buckets), 0, buckets - 1);

	/// <summary>按纬度带求均值；<paramref name="oceanOnly"/> = true 时跳过陆格（海格均值）。</summary>
	public static float[] MeanByBand(ClimateGrid grid, float[] field, int buckets, bool oceanOnly = false)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (field == null) throw new ArgumentNullException(nameof(field));
		if (field.Length != grid.Count) throw new ArgumentException("场与格数不一致", nameof(field));
		if (buckets < 1) throw new ArgumentOutOfRangeException(nameof(buckets));

		var sums = new double[buckets];
		var counts = new int[buckets];
		Accumulate(grid, field, buckets, oceanOnly, sums, counts);

		var means = new float[buckets];
		for (int b = 0; b < buckets; b++)
			means[b] = counts[b] > 0 ? (float)(sums[b] / counts[b]) : float.NaN;
		return means;
	}

	/// <summary>热赤道：纬向平均温度最高的带中心（±90°）；3 带平滑避免单带噪声。</summary>
	public static float ThermalEquatorDeg(ClimateGrid grid, float[] field, int buckets)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (field == null) throw new ArgumentNullException(nameof(field));
		if (buckets < 1) throw new ArgumentOutOfRangeException(nameof(buckets));

		var sums = new double[buckets];
		var counts = new int[buckets];
		Accumulate(grid, field, buckets, oceanOnly: false, sums, counts);

		float bestMean = float.MinValue;
		int best = 0;
		for (int b = 0; b < buckets; b++)
		{
			float sum = 0f;
			int count = 0;
			for (int k = -1; k <= 1; k++)
			{
				int bb = b + k;
				if (bb < 0 || bb >= buckets || counts[bb] == 0) continue;
				sum += (float)(sums[bb] / counts[bb]);
				count++;
			}
			if (count == 0) continue;

			float mean = sum / count;
			if (mean > bestMean) { bestMean = mean; best = b; }
		}
		return -90f + (best + 0.5f) * 180f / buckets;
	}

	static void Accumulate(ClimateGrid grid, float[] field, int buckets, bool oceanOnly,
		double[] sums, int[] counts)
	{
		for (int i = 0; i < grid.Count; i++)
		{
			if (oceanOnly && grid.Land[i]) continue;
			int b = BucketOf(grid.LatDeg(i), buckets);
			sums[b] += field[i];
			counts[b]++;
		}
	}
}
