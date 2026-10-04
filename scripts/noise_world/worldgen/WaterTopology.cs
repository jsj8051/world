using System;
using System.Collections.Generic;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 水系拓扑（River 2C-C，决策 08 v2 §2C-C——**WaterSystem 的组合层**）：
//   回答"**水从哪里来、经过什么水体、最终去哪里**"，**不重新定义河流或湖泊本身**。
//
// ★四个概念不互相吞并（永久原则）：
//   RiverGraph   = 河流**内部**拓扑（source/汇流/outlet/干支流）
//   BasinGraph   = 流域归属与终止（Ocean/Endorheic + BasinId）
//   LakeState    = 湖泊状态（水量平衡后的水面/面积/蓄量/溢出）
//   WaterTopology= 上述**水体之间的连接**（River→Lake→River→…→Ocean）
//
// ★**Lake 的 OutletCell 不直接变成 RiverGraph 的节点**：
//   LakeState ──OutletCell──> WaterTopology ──> "出口之后的 downstream water body"。
//   RiverGraph 完全不需要知道 Lake 的存在（契约 + 测试双向钉死）。
// ★只读：本层不回写 RiverNetwork / RiverGraph / BasinGraph / LakeState 的任何字段。
//   `lakes` 允许为 null（无湖泊层）——此时**退化**为既有 River → Ocean / Endorheic 终止结果。
// v1 节点：River（= 一个河系，以其出口格标识）/ Lake / Ocean（单一海洋水体）/ EndorheicSink（按流域）。
//   每个节点最多一条出边（`Next`），连接发生的格记在 `ViaCell`（可追溯）。
public sealed class WaterTopology
{
	/// <summary>水体节点类型。</summary>
	public enum BodyKind { River, Lake, Ocean, EndorheicSink }

	public int NodeCount { get; private set; }
	/// <summary>节点类型（int 化 BodyKind）。</summary>
	public int[] KindOf { get; private set; } = Array.Empty<int>();
	/// <summary>节点引用：River = 出口格；Lake = 湖下标；Ocean = −1；EndorheicSink = 流域号。</summary>
	public int[] RefOf { get; private set; } = Array.Empty<int>();
	/// <summary>出边目标节点（−1 = 终端：海洋 / 封闭湖 / 内流洼地）。</summary>
	public int[] Next { get; private set; } = Array.Empty<int>();
	/// <summary>连接发生的格（−1 = 无出边）——**可追溯性**事实。</summary>
	public int[] ViaCell { get; private set; } = Array.Empty<int>();

	/// <summary>河流格 → 所属河系节点（非河流格 = −1）。</summary>
	public int[] NodeOfRiverCell { get; private set; } = Array.Empty<int>();
	/// <summary>湖下标 → 湖泊节点。</summary>
	public int[] NodeOfLake { get; private set; } = Array.Empty<int>();
	/// <summary>海洋节点（单一；为 −1 表示本世界没有节点）。</summary>
	public int OceanNode { get; private set; } = -1;

	public int RiverCount { get; private set; }
	public int LakeNodeCount { get; private set; }
	public int SinkCount { get; private set; }

	/// <summary>组合水体连接（只读全部输入层；lakes 可为 null = 无湖泊层）。</summary>
	public void Generate(Ball ball, FinalGeography final, RiverNetwork rivers, RiverGraph graph,
		BasinGraph basins, LakeState lakes)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (rivers == null) throw new ArgumentNullException(nameof(rivers));
		if (graph == null) throw new ArgumentNullException(nameof(graph));
		if (basins == null) throw new ArgumentNullException(nameof(basins));

		int n = ball.CellDirs.Length;
		var kinds = new List<int>();
		var refs = new List<int>();
		var next = new List<int>();
		var via = new List<int>();
		var nodeOfRiverCell = new int[n];
		Array.Fill(nodeOfRiverCell, -1);

		// ── 节点 1：河系（每个河网出口 = 一个河系；出口格为身份）──
		for (int k = 0; k < graph.Outlets.Count; k++)
		{
			int outlet = graph.Outlets[k];
			kinds.Add((int)BodyKind.River);
			refs.Add(outlet);
			next.Add(-1);
			via.Add(-1);
		}
		RiverCount = graph.Outlets.Count;

		// 河流格 → 河系节点（沿河网图走到出口，路径压缩）
		var sysOf = new int[n];
		Array.Fill(sysOf, -1);
		var path = new List<int>();
		for (int i = 0; i < n; i++)
		{
			if (!rivers.IsRiver[i] || sysOf[i] >= 0) continue;
			path.Clear();
			int cur = i;
			while (sysOf[cur] < 0)
			{
				path.Add(cur);
				int d = graph.DownstreamRiver[cur];
				if (d < 0) break;
				cur = d;
			}
			int sys = sysOf[cur] >= 0 ? sysOf[cur] : cur;
			foreach (int p in path) sysOf[p] = sys;
		}
		// 出口格 → 节点号
		var nodeOfOutlet = new Dictionary<int, int>();
		for (int k = 0; k < graph.Outlets.Count; k++) nodeOfOutlet[graph.Outlets[k]] = k;
		for (int i = 0; i < n; i++)
			if (rivers.IsRiver[i] && nodeOfOutlet.TryGetValue(sysOf[i], out int nd)) nodeOfRiverCell[i] = nd;

		// ── 节点 2：湖泊（每个 LakeState 湖一个）──
		int lakeCount = lakes?.LakeCount ?? 0;
		var nodeOfLake = new int[lakeCount];
		for (int k = 0; k < lakeCount; k++)
		{
			nodeOfLake[k] = kinds.Count;
			kinds.Add((int)BodyKind.Lake);
			refs.Add(k);
			next.Add(-1);
			via.Add(-1);
		}
		LakeNodeCount = lakeCount;

		// ── 节点 3/4：海洋（单一）+ 内流洼地（按流域，按需创建）──
		var sinkNodeOfBasin = new Dictionary<int, int>();
		int SinkNode(int bid)
		{
			if (sinkNodeOfBasin.TryGetValue(bid, out int sn)) return sn;
			sn = kinds.Count;
			sinkNodeOfBasin[bid] = sn;
			kinds.Add((int)BodyKind.EndorheicSink);
			refs.Add(bid);
			next.Add(-1);
			via.Add(-1);
			return sn;
		}

		// ── 边 1：River → {Lake | Ocean | EndorheicSink}（终止事实取自 BasinGraph）──
		for (int k = 0; k < graph.Outlets.Count; k++)
		{
			int outlet = graph.Outlets[k];
			int bid = basins.BasinId[outlet];
			if (bid < 0) continue;                       // 理论不可达（河流只在陆上）
			int terminal = basins.Outlet[bid];
			// ★D-16：湖必须按**原始洼地分区**接（lakes.DepressionIdOf），不能用路由分区
			//   basins.BasinId——填洼开启后路由侧内流流域归零，那样会接不到湖。
			int did = lakes != null && outlet < lakes.DepressionIdOf.Length ? lakes.DepressionIdOf[outlet] : -1;
			int lake = did >= 0 && did < lakes.LakeOfBasin.Length ? lakes.LakeOfBasin[did] : -1;
			if (lake >= 0)
			{
				next[k] = nodeOfLake[lake];              // 河流注入该流域的湖
				via[k] = terminal;
			}
			else if (basins.TerminatesAtOcean[outlet])
			{
				// 海洋节点按需创建（全世界的海是同一个水体）
				if (OceanNode < 0)
				{
					OceanNode = kinds.Count;
					kinds.Add((int)BodyKind.Ocean);
					refs.Add(-1);
					next.Add(-1);
					via.Add(-1);
				}
				next[k] = OceanNode;
				int d = rivers.Downstream[terminal];
				via[k] = d >= 0 ? d : terminal;          // 入海点（海格）
			}
			else
			{
				next[k] = SinkNode(bid);                 // 内流洼地（终止，无出边）
				via[k] = terminal;
			}
		}

		// ── 边 2：Lake → 出口之后的 downstream water body（**不走 RiverGraph**）──
		//   从溢出格沿 flow graph 下行：遇河流格 ⇒ River；遇海 ⇒ Ocean；遇内流洼地 ⇒ 该流域的湖或洼地
		for (int k = 0; k < lakeCount; k++)
		{
			int node = nodeOfLake[k];
			int s = lakes.OutletCell[k];
			if (s < 0) continue;                          // 未溢出 = 封闭湖（终端）
			int cur = s;
			int guard = 0;
			while (cur >= 0 && guard++ <= n)
			{
				if (rivers.IsRiver[cur] && nodeOfRiverCell[cur] >= 0)
				{
					next[node] = nodeOfRiverCell[cur];    // ★真实下游河流（不是"恰好邻接"的河）
					via[node] = cur;
					break;
				}
				if (!final.FinalLand[cur])
				{
					if (OceanNode < 0)
					{
						OceanNode = kinds.Count;
						kinds.Add((int)BodyKind.Ocean);
						refs.Add(-1);
						next.Add(-1);
						via.Add(-1);
					}
					next[node] = OceanNode;
					via[node] = cur;
					break;
				}
				int d = rivers.Downstream[cur];
				if (d < 0)                                 // 内流洼地：看它所属流域有没有湖
				{
					// ★D-16：同上——按**原始洼地分区**接湖
					int d2 = lakes != null && cur < lakes.DepressionIdOf.Length ? lakes.DepressionIdOf[cur] : -1;
					int lk2 = d2 >= 0 && d2 < lakes.LakeOfBasin.Length ? lakes.LakeOfBasin[d2] : -1;
					next[node] = lk2 >= 0 ? nodeOfLake[lk2] : SinkNode(d2 >= 0 ? d2 : basins.BasinId[cur]);
					via[node] = cur;
					break;
				}
				cur = d;
			}
		}

		NodeCount = kinds.Count;
		KindOf = kinds.ToArray();
		RefOf = refs.ToArray();
		Next = next.ToArray();
		ViaCell = via.ToArray();
		NodeOfRiverCell = nodeOfRiverCell;
		NodeOfLake = nodeOfLake;
		SinkCount = sinkNodeOfBasin.Count;
	}

	/// <summary>从某节点沿出边走到终端（自带访问集，遇环则停——环本身由测试禁止）。</summary>
	public List<int> TraceFrom(int node)
	{
		var res = new List<int>();
		var seen = new HashSet<int>();
		int cur = node;
		while (cur >= 0 && cur < NodeCount && seen.Add(cur))
		{
			res.Add(cur);
			cur = Next[cur];
		}
		return res;
	}

	/// <summary>从任意河源格出发的完整水系路径（River → … → 终端水体）。</summary>
	public List<int> TraceFromRiverSource(int sourceCell) =>
		sourceCell >= 0 && sourceCell < NodeOfRiverCell.Length && NodeOfRiverCell[sourceCell] >= 0
			? TraceFrom(NodeOfRiverCell[sourceCell])
			: new List<int>();
}
