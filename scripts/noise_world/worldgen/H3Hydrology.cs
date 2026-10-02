using System;
using System.Linq;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · H3 图上模拟层（决策原典 §七）：水文直接长在 H3 邻接图上——
//   「你的世界就是一张巨大的六边形图」。本类是框架第一刀：单流向 + 汇流累积。
// 流向（单流向，原典伪代码 find lowest neighbor）：
//   海格（elev ≤ seaLevel）= 出口（Downstream −1，径流入海即终止）；
//   陆格 = **严格更低**的邻居中最低者（平局取格索引最小 ⇒ 确定性；等高不走边 ⇒ 链上高程严格递减 ⇒ 无环）；
//   无严格更低邻居 = 内流洼地（Downstream −1；框架期只登记，priority-flood 填洼/湖面扁平化留后续批次）。
// 汇流累积：按高程降序处理（严格降 ⇒ 该序即拓扑序），每格把累积传给下游；海格不传递。
//   FlowAccum[i] = 上游格数 + 1（含自身雨量）；河流判读（accum > 阈值）由消费方定。
// 确定性红线：排序键 =（高程降序, 格索引升序）——同高程格的处理序固定，无调度依赖。
/// <summary>
/// H3 水文（框架版）：单流向 + 汇流累积。输入逐格海拔（米，海平面 0 口径）与 Ball 邻接。
/// </summary>
public sealed class H3Hydrology
{
	/// <summary>下游格索引；−1 = 出口（海格 / 内流洼地）。</summary>
	public int[] Downstream { get; private set; } = Array.Empty<int>();

	/// <summary>汇流累积（上游格数 + 1，含自身）；海格恒 1（不入河流账）。</summary>
	public int[] FlowAccum { get; private set; } = Array.Empty<int>();

	/// <summary>海格掩码（elev ≤ seaLevel）。</summary>
	public bool[] IsOcean { get; private set; } = Array.Empty<bool>();

	/// <summary>内流洼地格数（陆格中无严格更低邻居者；湖泊/内流河语义的候选集，框架期只计数）。</summary>
	public int SinkCount { get; private set; }

	public void Generate(Ball ball, float[] elevationM, float seaLevelM = 0f)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (elevationM == null) throw new ArgumentNullException(nameof(elevationM));
		int n = ball.CellDirs.Length;
		if (elevationM.Length != n)
			throw new ArgumentException($"海拔数组长度 {elevationM.Length} ≠ 格数 {n}", nameof(elevationM));

		var neighbors = ball.CellNeighbors;
		Downstream = new int[n];
		FlowAccum = new int[n];
		IsOcean = new bool[n];
		SinkCount = 0;

		// pass1：海陆掩码 + 单流向（严格更低；平局取格索引最小）
		for (int i = 0; i < n; i++)
		{
			FlowAccum[i] = 1;
			if (elevationM[i] <= seaLevelM)
			{
				IsOcean[i] = true;
				Downstream[i] = -1;
				continue;
			}
			int best = -1;
			float bestElev = float.PositiveInfinity;
			foreach (int j in neighbors[i])
			{
				if (elevationM[j] >= elevationM[i]) continue;   // 只走严格下降边（⇒ 无环）
				if (elevationM[j] < bestElev || (elevationM[j] == bestElev && j < best))
				{
					bestElev = elevationM[j];
					best = j;
				}
			}
			Downstream[i] = best;
			if (best < 0) SinkCount++;
		}

		// pass2：高程降序（同高程按格索引升序）= 拓扑序（陆→陆边严格降 ⇒ 无环）；海格不传递
		var order = Enumerable.Range(0, n)
			.OrderByDescending(i => elevationM[i])
			.ThenBy(i => i)
			.ToArray();
		foreach (int i in order)
		{
			int d = Downstream[i];
			if (d >= 0 && !IsOcean[d]) FlowAccum[d] += FlowAccum[i];
		}
	}
}
