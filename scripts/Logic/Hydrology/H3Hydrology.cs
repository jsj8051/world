using System;
using System.Collections.Generic;
using System.Linq;
using World.H3Grid;    // Ball（H3 球壳数据层）

namespace World.Logic;

// 世界生成空间 · H3 图上模拟层（决策原典 §七）：水文直接长在 H3 邻接图上——
//   「你的世界就是一张巨大的六边形图」。本类是框架第一刀：单流向 + 汇流累积。
// 流向（单流向，原典伪代码 find lowest neighbor）：
//   海格（elev ≤ seaLevel）= 出口（Downstream −1，径流入海即终止）；
//   陆格 = **严格更低**的邻居中最低者（平局取格索引最小 ⇒ 确定性；等高不走边 ⇒ 链上高程严格递减 ⇒ 无环）；
//   无严格更低邻居 = 内流洼地（Downstream −1；框架期只登记，priority-flood 填洼/湖面扁平化留后续批次）。
// 汇流累积：上游支流全部完成后汇总该格；海格不传递，也不接收累积。
//   FlowAccum[i] = 上游格数 + 1（含自身雨量）；河流判读（accum > 阈值）由消费方定。
// 逐格支流仍按（高程降序、格索引升序）累加，保留浮点加法顺序。
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

	/// <summary>径流加权累积（River 2A：权重 = 逐格年降水 mm；null = 未启用）。</summary>
	public float[] WeightedAccum { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// 水文（单流向 + 汇流累积）。cellWeight 可选：逐格径流权重（River 2A = 年降水 mm）——
	/// null = 均匀（每格 1，纯格数累积，W1 行为不变）。
	/// </summary>
	public void Generate(Ball ball, float[] elevationM, float seaLevelM, float[] cellWeight = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (elevationM == null) throw new ArgumentNullException(nameof(elevationM));
		int n = ball.CellDirs.Length;
		if (elevationM.Length != n)
			throw new ArgumentException($"海拔数组长度 {elevationM.Length} ≠ 格数 {n}", nameof(elevationM));

		var neighbors = ball.CellNeighbors;
		Downstream = new int[n];
		FlowAccum = new int[n];
		WeightedAccum = new float[n];
		IsOcean = new bool[n];
		SinkCount = 0;
		bool unorderedHeight = false;

		// pass1：海陆掩码 + 单流向（严格更低；平局取格索引最小）
		for (int i = 0; i < n; i++)
		{
			FlowAccum[i] = 1;
			WeightedAccum[i] = cellWeight != null ? cellWeight[i] : 1f;
			unorderedHeight |= float.IsNaN(elevationM[i]);
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

		if (unorderedHeight)
		{
			// NaN does not define a strictly descending edge. Preserve its historical sort order.
			foreach (int cell in Enumerable.Range(0, n).OrderByDescending(i => elevationM[i]))
			{
				int target = Downstream[cell];
				if (target < 0 || IsOcean[target]) continue;
				FlowAccum[target] += FlowAccum[cell];
				WeightedAccum[target] += WeightedAccum[cell];
			}
			return;
		}

		AccumulateTributaries(elevationM);
	}

	void AccumulateTributaries(float[] elevationM)
	{
		int n = Downstream.Length;
		var firstUpstream = new int[n];
		var nextUpstream = new int[n];
		var pending = new int[n];
		Array.Fill(firstUpstream, -1);
		for (int source = 0; source < n; source++)
		{
			int target = Downstream[source];
			if (target < 0 || IsOcean[target]) continue;
			pending[target]++;
			// H3 bounds this insertion by the local degree. Equal heights retain source index order.
			ref int link = ref firstUpstream[target];
			while (link >= 0 && elevationM[link] >= elevationM[source]) link = ref nextUpstream[link];
			nextUpstream[source] = link;
			link = source;
		}

		var ready = new Queue<int>();
		for (int cell = 0; cell < n; cell++)
		{
			int target = Downstream[cell];
			if (pending[cell] == 0 && target >= 0 && !IsOcean[target]) ready.Enqueue(cell);
		}
		while (ready.TryDequeue(out int cell))
		{
			for (int source = firstUpstream[cell]; source >= 0; source = nextUpstream[source])
			{
				FlowAccum[cell] += FlowAccum[source];
				WeightedAccum[cell] += WeightedAccum[source];
			}
			int target = Downstream[cell];
			if (target >= 0 && !IsOcean[target] && --pending[target] == 0) ready.Enqueue(target);
		}
	}
}
