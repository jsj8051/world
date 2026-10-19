using System;
using System.Threading.Tasks;
using World.H3Grid;          // Ball（H3 球壳数据层）

namespace World.Logic;

/// <summary>
/// 逐格标量场的**图平滑**：每遍把每格拉向"自身与邻居的加权混合"
/// （<c>u ← (1−w)·自身 + w·邻居均值</c>），重复若干遍。
///
/// 加权混合（而不是纯邻域平均）⇒ **保住极值**：纯平均会把孤峰削掉大半。
/// 无邻居的格（不应出现）原样保留；ping-pong 双缓冲 ⇒ 与线程调度无关，逐位确定性。
/// </summary>
public static class FieldBlurOps
{
	/// <summary>
	/// 就地平滑 <paramref name="field"/>。
	/// <paramref name="weight"/> ≤ 0 或 <paramref name="passes"/> ≤ 0 ⇒ 原样返回（关断）。
	/// </summary>
	/// <param name="ball">球壳数据层。</param>
	/// <param name="field">逐格场（就地改写）。</param>
	/// <param name="weight">自身→邻居的混合权重 ∈ (0,1]；越大越平（1 = 完全取邻居均值）。</param>
	/// <param name="passes">平滑遍数（≥ 1）。</param>
	public static void Blur(Ball ball, float[] field, float weight, int passes)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (field == null) throw new ArgumentNullException(nameof(field));
		if (!(weight > 0f) || passes <= 0) return;
		if (weight > 1f) weight = 1f;

		int n = field.Length;
		var neighbors = ball.CellNeighbors;
		var buffer = new float[n];
		for (int p = 0; p < passes; p++)
		{
			Parallel.For(0, n, i =>
			{
				int[] nb = neighbors[i];
				if (nb.Length == 0) { buffer[i] = field[i]; return; }
				float sum = 0f;
				foreach (int j in nb) sum += field[j];
				buffer[i] = (1f - weight) * field[i] + weight * (sum / nb.Length);
			});
			Array.Copy(buffer, field, n);
		}
	}
}
