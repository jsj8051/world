using System;
using System.Collections.Generic;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 世界生成空间 · 河网图（River 2B，决策 08 v2 §River2B——**水文事实层**）：
//   从 RiverNetwork 的 flow graph 派生**河流拓扑**：source / downstream / confluence /
//   tributary / trunk / outlet / accumulation。
//   ★与 RiverGeometry（几何表现层）严格分离：**不为"画得连续"修改任何汇流事实**——
//     本类只读 RiverNetwork（Downstream/IsRiver/RunoffAccum）+ FinalLand，不回写水文。
//     连续河线的做法是把线的采样沿 flow graph 走（含亚阈值格），不是改阈值、不是改图。
//   ★契约（决策 08 v2 §2）：不得依赖 FinalSpatialIndex——基础拓扑由自身 flow graph 决定
//     （反射钉子在 ArchitectureContractTests）。
//   ★降水来源契约（决策 08 v2 §2A）：水文只消费 annualPrecipMm 数组；降水由
//     PrecipitationModel 提供，旧 ClimateGenerator（World.Biome）禁止接入——
//     将来 Climate Simulation 上线时只替换输入源，本层不变。
// 节点语义（优先级自上而下判定）：
//   Outlet     沿 flow 路径到海或内流洼地（无下一河流格）——河流终止
//   Source     无上游河流格（河源）
//   Confluence ≥2 条上游河流格汇入（汇流点）
//   Trunk      干流段：从出口沿"上游累积最大者"回溯到的主干
//   Tributary  其余河流格（汇入干流的支流段）
public sealed class RiverGraph
{
	/// <summary>河流节点类型。</summary>
	public enum RiverNodeKind { Outlet, Source, Confluence, Trunk, Tributary }

	/// <summary>下一河流格（沿 flow 路径；−1 = 出口：入海或内流洼地）。</summary>
	public int[] DownstreamRiver { get; private set; } = Array.Empty<int>();
	/// <summary>汇入本格的河流格数（≥2 = 汇流点）。</summary>
	public int[] UpstreamCount { get; private set; } = Array.Empty<int>();
	/// <summary>逐格节点类型（非河流格 = −1，以 byte 存；RiverNodeKind 序见上）。</summary>
	public int[] NodeKind { get; private set; } = Array.Empty<int>();
	/// <summary>逐格径流累积（mm；River 2A 的水文事实，非河流格亦有意义）。</summary>
	public float[] AccumMm { get; private set; } = Array.Empty<float>();

	public int RiverCellCount { get; private set; }
	public int SourceCount { get; private set; }
	public int ConfluenceCount { get; private set; }
	public int OutletCount { get; private set; }
	/// <summary>河源格（无上游河流格；**孤立河——单格河——既是源也是出口**，仍在此列表）。</summary>
	public List<int> Sources { get; private set; } = new();
	/// <summary>出口格（终止于海或内流洼地）。</summary>
	public List<int> Outlets { get; private set; } = new();

	/// <summary>从水文 flow graph 派生河流拓扑（只读 RiverNetwork，不回写）。</summary>
	public void Generate(Ball ball, FinalGeography final, RiverNetwork rivers)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (rivers == null) throw new ArgumentNullException(nameof(rivers));

		int n = ball.CellDirs.Length;
		var isRiver = rivers.IsRiver;
		var down = rivers.Downstream;

		DownstreamRiver = new int[n];
		UpstreamCount = new int[n];
		NodeKind = new int[n];
		Array.Fill(DownstreamRiver, -1);
		Array.Fill(NodeKind, -1);
		AccumMm = (float[])rivers.RunoffAccum.Clone();

		// pass1：河流格 → 下一河流格（沿 flow 路径穿过亚阈值格；遇海/内流洼地 = 出口 −1）
		for (int i = 0; i < n; i++)
		{
			if (!isRiver[i]) continue;
			RiverCellCount++;
			int j = down[i];
			while (j >= 0 && !isRiver[j])
			{
				if (!final.FinalLand[j]) break;      // 入海：出口
				j = down[j];                          // 内流洼地（down = −1）亦在此退出
			}
			if (j >= 0 && isRiver[j]) DownstreamRiver[i] = j;
		}

		// pass2：上游计数 + 边表（CSR-lite：head/next，避免每格一个 List 的分配）
		var head = new int[n];
		Array.Fill(head, -1);
		var edgeFrom = new List<int>(RiverCellCount);
		var edgeNext = new List<int>(RiverCellCount);
		for (int i = 0; i < n; i++)
		{
			int d = DownstreamRiver[i];
			if (d < 0) continue;
			UpstreamCount[d]++;
			edgeFrom.Add(i);
			edgeNext.Add(head[d]);
			head[d] = edgeFrom.Count - 1;
		}

		// pass3：干流 = 从每个出口沿"上游累积最大者"回溯（其余河流格 = 支流）
		var isTrunk = new bool[n];
		var outlets = new List<int>();
		for (int i = 0; i < n; i++)
			if (isRiver[i] && DownstreamRiver[i] < 0) outlets.Add(i);
		foreach (int o in outlets)
		{
			int cur = o;
			while (cur >= 0)
			{
				isTrunk[cur] = true;
				int best = -1;
				float bestA = float.NegativeInfinity;
				for (int e = head[cur]; e >= 0; e = edgeNext[e])
				{
					int up = edgeFrom[e];
					if (AccumMm[up] > bestA) { bestA = AccumMm[up]; best = up; }
				}
				cur = best;
			}
		}

		// pass4：节点类型
		//   河源（UpstreamCount = 0）先入列表——**孤立河（单格河）既是源也是出口**，
		//   它仍必须是一条（退化的）河线的起点，否则河线覆盖会漏格；
		//   类型标注优先级：出口 > 河源 > 汇流 > 干流/支流（出口是"河流终止"的关键事实）。
		Sources = new List<int>();
		Outlets = outlets;
		for (int i = 0; i < n; i++)
		{
			if (!isRiver[i]) continue;
			bool isHead = UpstreamCount[i] == 0;
			if (isHead)
			{
				Sources.Add(i);
				SourceCount++;
			}
			if (DownstreamRiver[i] < 0)
			{
				NodeKind[i] = (int)RiverNodeKind.Outlet;
				OutletCount++;
			}
			else if (isHead)
			{
				NodeKind[i] = (int)RiverNodeKind.Source;
			}
			else if (UpstreamCount[i] >= 2)
			{
				NodeKind[i] = (int)RiverNodeKind.Confluence;
				ConfluenceCount++;
			}
			else
			{
				NodeKind[i] = isTrunk[i] ? (int)RiverNodeKind.Trunk : (int)RiverNodeKind.Tributary;
			}
		}
	}
}
