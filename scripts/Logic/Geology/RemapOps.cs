using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 陆地高度的 hypsometric remap（海面以上）：对相对高度做一条保号、单调的曲线，
/// 让低地更平、山地相对更高，同时不改变「哪一格是陆」。
/// <c>x = (h − sea) / (href − sea)</c>（href = 陆地 p99.5，避免单峰定标）；
/// <c>y = x^gamma</c>（x ≤ 1）或 <c>1 + (x − 1)·tail</c>（x &gt; 1，压住最高峰），
/// <c>h' = sea + (href − sea)·y</c>。
/// 只作用于陆壳且 <c>h &gt; sea</c> 的格：<c>x = 0</c> 原样保留 ⇒ 海岸线（h = sea 的穿越）逐格不变，
/// 洋壳（含岛弧）不参与。
/// </summary>
public static class RemapOps
{
	/// <summary>href 取陆地高度的这一分位（避免用单峰定标）。</summary>
	const double RefQuantile = 0.995;

	/// <summary>gamma 与 1 的差小于它 ⇒ 视为不改（直接关断）。</summary>
	const float GammaEpsilon = 1e-4f;

	/// <summary>就地重映射陆面高度（<paramref name="heightM"/> 的陆壳格；其余格不动）。</summary>
	/// <param name="ball">球壳数据层（只用于判空与长度）。</param>
	/// <param name="spec">本阶段配置（<see cref="TerrainSpec.HypsometricGamma"/> /
	/// <see cref="TerrainSpec.HypsometricTailCompress"/> 与海面）。</param>
	/// <param name="heightM">逐格高度（陆壳格 = 陆面合成高度；洋壳格不参与）。</param>
	public static void ApplyLand(Ball ball, TerrainSpec spec, float[] heightM, bool[] continental)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		if (continental == null) throw new ArgumentNullException(nameof(continental));

		float gamma = spec.HypsometricGamma;
		if (!(gamma > 0f) || MathF.Abs(gamma - 1f) <= GammaEpsilon) return;
		float tailCompress = spec.HypsometricTailCompress;

		int n = heightM.Length;
		if (continental.Length != n)
			throw new ArgumentException($"陆壳掩码长度 {continental.Length} ≠ 格数 {n}", nameof(continental));

		float seaM = spec.SeaLevelM;

		var samples = new List<float>(n / 2);
		for (int i = 0; i < n; i++)
			if (continental[i] && heightM[i] > seaM) samples.Add(heightM[i]);
		if (samples.Count == 0) return;
		samples.Sort();
		int idx = (int)Math.Floor(RefQuantile * samples.Count);
		float href = samples[Math.Clamp(idx, 0, samples.Count - 1)];
		float span = href - seaM;
		if (!(span > 1e-3f)) return;

		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			float h = heightM[i];
			if (!(h > seaM)) return;

			float x = (h - seaM) / span;
			float y = x <= 1f ? MathF.Pow(x, gamma) : 1f + (x - 1f) * tailCompress;
			heightM[i] = seaM + span * y;
		});
	}
}
