using System;
using System.Collections.Generic;
using Godot;                    // 仅 Vector3 结构体（纯值类型）
using World.NewHexWorld;        // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 河流几何（River 2B，决策 08 v2 §River2B——**表现层**）：
//   连续河线 = RiverGraph 的**几何表达**：每条线 = 一个河源沿 flow graph 到出口的完整路径。
//   ★不改水文：本类只读 RiverGraph + RiverNetwork 的 Downstream——**不调整阈值、不回写图**。
//     连续性来自"沿 flow graph 逐格采样（含中间的亚阈值格）"，因此相邻点必为 H3 相邻格，
//     而"河流存在性"仍完全由 RiverNetwork 的阈值判据决定（水文事实与几何表现分离）。
//   ★2B 的分层目的：河流存在（mask）与河流怎么画（line）不再混在一个 mask 里；
//     干流/支流的粗细、河口的表达属于本层，汇流事实属于 RiverGraph。
//
// ★★Resolution 契约（2026-10-04 冻结，**有测试钉死**，见 RiverSymbolWidthTests）：
//   Lines      = **单位球心方向向量** ⇒ 描述连续地理几何，**不依赖 H3 resolution**，
//                可叠加到任意 res 的地表网格上（半径由渲染方给定）。
//   LineCells  = **H3 格索引** ⇒ 该几何在 <see cref="SourceRes"/> 下对应的 H3 覆盖索引，
//                **仅对生成它的 resolution 有效**；换 res 必须重算，不得跨分辨率当作事实复用
//                （汇流累积定义在 H3 邻接图上，换 res 河网结构本就不同）。
//   ⇒ 想要的"河流不绑分辨率"只成立于**几何坐标**，不成立于**拓扑结果**；
//     能守住的是"生产档 res4 冻结 ⇒ 河网事实唯一，表现层只读不重算"。
public sealed class RiverGeometry
{
	/// <summary>
	/// 生成本几何的 H3 分辨率（<see cref="LineCells"/> 的绑定 res；−1 = 未生成）。
	/// </summary>
	public int SourceRes { get; private set; } = -1;

	/// <summary>每条线的格序列（源 → 出口；相邻元素必为 H3 相邻格）。</summary>
	public List<int[]> LineCells { get; private set; } = new();
	/// <summary>每条线的折线点（单位球心方向；渲染按半径缩放）。</summary>
	public List<Vector3[]> Lines { get; private set; } = new();
	/// <summary>每条线终点的出口格（−1 = 内流洼地终止无以河流格表达的出口）。</summary>
	public List<int> LineOutlet { get; private set; } = new();

	/// <summary>由河流图生成连续河线（每个河源一条，回溯到出口）。</summary>
	public void Generate(Ball ball, RiverGraph graph, RiverNetwork rivers)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (graph == null) throw new ArgumentNullException(nameof(graph));
		if (rivers == null) throw new ArgumentNullException(nameof(rivers));

		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		var down = rivers.Downstream;
		SourceRes = ball.Res;          // ★LineCells 绑定到生成它的分辨率（res 契约；见类注释）
		LineCells = new List<int[]>();
		Lines = new List<Vector3[]>();
		LineOutlet = new List<int>();

		foreach (int src in graph.Sources)
		{
			var cells = new List<int> { src };
			int cur = src;
			// 沿河流图走到出口；每两步之间补上 flow graph 上的亚阈值中间格 ⇒ 几何连续
			while (cells.Count <= n)
			{
				int nxt = graph.DownstreamRiver[cur];
				if (nxt < 0) break;
				for (int w = down[cur]; w >= 0 && w != nxt; w = down[w]) cells.Add(w);
				cells.Add(nxt);
				cur = nxt;
			}
			var pts = new Vector3[cells.Count];
			for (int k = 0; k < cells.Count; k++) pts[k] = dirs[cells[k]];
			LineCells.Add(cells.ToArray());
			Lines.Add(pts);
			LineOutlet.Add(cur);
		}
	}
}
