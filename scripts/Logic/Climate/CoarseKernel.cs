using System;

namespace World.Logic;

/// <summary>
/// 固定核的一次卷积（单遍、无迭代、无收敛判据）：在粗层上把短波抹成 650 km / 300 km 尺度的场。
/// 权重按大圆距离取高斯，逐格按行归一化补回截断尾部；核在构建期一次算好（CSR 存储）。
///
/// 为什么不做 SOR/多遍邻居平均：SOR 要遍数/收敛判据/热启动；多遍平均的扩散长度 ≈ √P 格，
/// 650 km 在 res4 要 ~180 遍。一次卷积把"积分"这一步彻底去掉。
///
/// @example
/// <code>
/// var kernel = CoarseKernel.Build(layer, sigmaKm: 650f, radiusHops: 7);
/// kernel.Convolve(source, destination);
/// </code>
/// </summary>
public sealed class CoarseKernel
{
	readonly int[] _start;      // 每格的邻域在 _index/_weight 里的起点（长度 = Count + 1）
	readonly int[] _index;
	readonly float[] _weight;

	/// <summary>粗层格数。</summary>
	public int Count => _start.Length - 1;

	CoarseKernel(int[] start, int[] index, float[] weight)
	{
		_start = start;
		_index = index;
		_weight = weight;
	}

	/// <summary>
	/// 建核：从每格做半径 <paramref name="radiusHops"/> 的 BFS，权重 = exp(−½·(d/σ)²)。
	/// 核半径按跳数给（km 折算在调用方），截断尾部由行归一化补回。
	/// </summary>
	public static CoarseKernel Build(CoarseLayer layer, float sigmaKm, int radiusHops)
	{
		if (layer == null) throw new ArgumentNullException(nameof(layer));
		if (!(sigmaKm > 0f)) throw new ArgumentOutOfRangeException(nameof(sigmaKm));
		if (radiusHops < 0) throw new ArgumentOutOfRangeException(nameof(radiusHops));

		int n = layer.Count;
		var start = new int[n + 1];
		var index = new System.Collections.Generic.List<int>(n * 64);
		var weight = new System.Collections.Generic.List<float>(n * 64);

		var stamp = new int[n];
		var hops = new int[n];
		var queue = new int[n];
		int mark = 0;

		for (int i = 0; i < n; i++)
		{
			mark++;
			int head = 0, tail = 0;
			queue[tail++] = i;
			stamp[i] = mark;
			hops[i] = 0;

			start[i] = index.Count;
			double sum = 0;
			while (head < tail)
			{
				int cell = queue[head++];
				float d = hops[cell] == 0
					? 0f
					: CoarseLayer.GreatCircleKm(layer.Dirs[i], layer.Dirs[cell], layer.RadiusKm);
				double w = Math.Exp(-0.5 * (d / sigmaKm) * (d / sigmaKm));
				index.Add(cell);
				weight.Add((float)w);
				sum += w;

				if (hops[cell] >= radiusHops) continue;
				foreach (int nb in layer.Neighbors[cell])
				{
					if (stamp[nb] == mark) continue;
					stamp[nb] = mark;
					hops[nb] = hops[cell] + 1;
					queue[tail++] = nb;
				}
			}

			// 行归一化：把被半径截掉的尾部补回（否则边缘格整体偏弱）
			if (sum > 0)
			{
				for (int k = start[i]; k < index.Count; k++)
					weight[k] = (float)(weight[k] / sum);
			}
		}
		start[n] = index.Count;

		return new CoarseKernel(start, index.ToArray(), weight.ToArray());
	}

	/// <summary>单遍卷积：<paramref name="destination"/> 与 <paramref name="source"/> 不得同引用。</summary>
	public void Convolve(float[] source, float[] destination)
	{
		if (source == null) throw new ArgumentNullException(nameof(source));
		if (destination == null) throw new ArgumentNullException(nameof(destination));
		int n = Count;
		if (source.Length != n) throw new ArgumentException("源场与粗层格数不一致", nameof(source));
		if (destination.Length != n) throw new ArgumentException("目标场与粗层格数不一致", nameof(destination));
		if (ReferenceEquals(source, destination)) throw new ArgumentException("卷积不能原地进行");

		for (int i = 0; i < n; i++)
		{
			float sum = 0f;
			int end = _start[i + 1];
			for (int k = _start[i]; k < end; k++)
				sum += _weight[k] * source[_index[k]];
			destination[i] = sum;
		}
	}
}
