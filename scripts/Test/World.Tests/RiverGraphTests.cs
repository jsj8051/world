using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Godot;
using World.H3Grid;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 河网图护栏（River 2B，决策 08 v2）——**水文事实与几何表现分离**：
///   · 图不改水文（河流格 ⊆ FinalLand、IsRiver 前后不变、沿链严格降高）；
///   · 拓扑语义（source / confluence / trunk / tributary / outlet）自洽；
///   · 连续河线只表达图（覆盖全部河流格、相邻点必 H3 相邻、起于源止于出口、累积单调不减）；
///   · 退化解原则（永久架构原则）：均匀降水 ⇒ 图拓扑精确退化到格数判据。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class RiverGraphTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (RiverNetwork r, RiverGraph g, RiverGeometry geo, FinalGeography f, HeightComposer c)
		Make(int seed = 42, int threshold = 12, float[] precip = null)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, seed, WorldPreset.Earth.LandSea);
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(Ball, g, surface);
		var l = new RegionalLandforms(seed);
		l.Generate(Ball, g);
		var features = new List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		};
		var c = new HeightComposer(seed);
		c.Generate(Ball, surface, g, features);
		var f = new FinalGeography();
		f.Generate(Ball, c, g);
		var r = new RiverNetwork();
		r.Generate(Ball, f, c, riverThresholdCells: threshold, annualPrecipMm: precip);
		var graph = new RiverGraph();
		graph.Generate(Ball, f, r);
		var geo = new RiverGeometry();
		geo.Generate(Ball, graph, r);
		return (r, graph, geo, f, c);
	}

	[Test]
	public void Graph_DoesNotAlterHydrologyFacts()
	{
		// 2B 第一红线：**图不为"画得连续"改汇流事实**——
		// ① 河流格 ⊆ FinalLand；② 建图前后 IsRiver 逐格不变；③ 沿河流链高度严格下降
		var (r, graph, _, f, c) = Make();
		var before = (bool[])r.IsRiver.Clone();
		var graph2 = new RiverGraph();
		graph2.Generate(Ball, f, r);   // 幂等：再建一次也不改变水文

		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (r.IsRiver[i]) Assert.That(f.FinalLand[i], Is.True, $"格 {i}：河流格必须落在 FinalLand 上");
			Assert.That(r.IsRiver[i], Is.EqualTo(before[i]), $"格 {i}：建图不得改动水文掩码");
			int d = graph.DownstreamRiver[i];
			if (r.IsRiver[i] && d >= 0)
			{
				Assert.That(r.IsRiver[d], Is.True, $"格 {i}：下游河流格必须是河流");
				Assert.That(r.RunoffAccum[d], Is.GreaterThanOrEqualTo(r.RunoffAccum[i]),
					$"格 {i}：径流累积沿链不得减少（汇流事实）");
				// ★D-11 Batch A：降高必须在**水文路由表面**上判（不是 FinalHeight）——
				//   填洼会抬升闭合洼地，FinalHeight 上可能出现"看上去上坡"的边。
				var surf = r.Routing.RoutingHeightM;
				int w = r.Downstream[i];
				while (w >= 0 && w != d) { Assert.That(surf[w], Is.LessThan(surf[i])); w = r.Downstream[w]; }
				Assert.That(surf[d], Is.LessThan(surf[i]), $"格 {i}：河流链须严格降高（路由表面口径）");
			}
		}
		Assert.That(graph.RiverCellCount, Is.EqualTo(graph2.RiverCellCount), "建图幂等");
	}

	[Test]
	public void Outlets_TerminateAtSeaOrClosedBasin()
	{
		// 出口语义：沿 flow graph 走到海格或内流洼地（无更低邻居）——与 RiverNetwork 四问 ② 同源
		var (r, graph, _, f, _) = Make();
		Assert.That(graph.OutletCount, Is.GreaterThan(0), "须存在河流出口");
		foreach (int o in graph.Outlets)
		{
			Assert.That(r.IsRiver[o], Is.True, $"格 {o}：出口须是河流格");
			int w = r.Downstream[o];
			while (w >= 0 && f.FinalLand[w]) w = r.Downstream[w];
			// 终止条件：入海（走到海格）或内流洼地（陆格无下游）
			bool toSea = w >= 0 && !f.FinalLand[w];
			bool closedBasin = r.Downstream[o] < 0 ||
				(r.Downstream[o] >= 0 && f.FinalLand[r.Downstream[o]] && WalkEndsAtSink(r, f, o));
			Assert.That(toSea || closedBasin, Is.True, $"格 {o}：出口须终止于海或内流洼地");
		}
	}

	static bool WalkEndsAtSink(RiverNetwork r, FinalGeography f, int from)
	{
		int w = r.Downstream[from];
		while (w >= 0 && f.FinalLand[w])
		{
			if (r.Downstream[w] < 0) return true;   // 陆格无下游 = 内流洼地
			w = r.Downstream[w];
		}
		return false;
	}

	[Test]
	public void NodeKinds_AreSelfConsistent()
	{
		// 拓扑语义自洽：类型优先级 出口 > 河源 > 汇流 > 干流/支流；计数与列表一致
		//（threshold 3：res1 仅 ~250 陆格且破碎，需低阈值才能出现多格链与汇流点——
		//  拓扑语义验证用，非生产阈值；生产 res4 默认 40 格）
		var (r, graph, _, _, _) = Make(threshold: 3);
		Assert.That(graph.RiverCellCount, Is.GreaterThan(0), "须存在河流格");
		Assert.That(graph.SourceCount, Is.EqualTo(graph.Sources.Count));
		Assert.That(graph.SourceCount, Is.GreaterThan(0), "须存在河源");
		int confluences = 0;
		for (int i = 0; i < graph.NodeKind.Length; i++)
		{
			if (!r.IsRiver[i]) { Assert.That(graph.NodeKind[i], Is.EqualTo(-1), $"格 {i}：非河流格无节点类型"); continue; }
			if (graph.DownstreamRiver[i] < 0)
				Assert.That(graph.NodeKind[i], Is.EqualTo((int)RiverGraph.RiverNodeKind.Outlet));
			else if (graph.UpstreamCount[i] == 0)
				Assert.That(graph.NodeKind[i], Is.EqualTo((int)RiverGraph.RiverNodeKind.Source));
			else if (graph.UpstreamCount[i] >= 2)
			{
				Assert.That(graph.NodeKind[i], Is.EqualTo((int)RiverGraph.RiverNodeKind.Confluence));
				confluences++;
			}
		}
		int maxUp = 0;
		for (int i = 0; i < graph.UpstreamCount.Length; i++) maxUp = Math.Max(maxUp, graph.UpstreamCount[i]);
		Assert.That(confluences, Is.EqualTo(graph.ConfluenceCount));
		Assert.That(graph.ConfluenceCount, Is.GreaterThan(0),
			$"须存在汇流点（支流自然汇合的产物）：cells={graph.RiverCellCount} src={graph.SourceCount} " +
			$"out={graph.OutletCount} maxUp={maxUp}");
	}

	[Test]
	public void Geometry_LinesCoverEveryRiverCell_AndStayContinuous()
	{
		// 连续河线 = 图的几何表达：① 覆盖全部河流格；② 相邻点必 H3 相邻（几何连续）；
		// ③ 起于河源、止于出口；④ 径流沿程单调不减
		var (r, graph, geo, _, _) = Make(threshold: 6);
		Assert.That(geo.LineCells.Count, Is.EqualTo(graph.SourceCount), "每个河源一条线");

		var covered = new HashSet<int>();
		var nbrs = Ball.CellNeighbors;
		for (int li = 0; li < geo.LineCells.Count; li++)
		{
			var cells = geo.LineCells[li];
			Assert.That(cells.Length, Is.GreaterThanOrEqualTo(1), $"线 {li}：至少一个点（孤立河 = 单格河）");
			Assert.That(graph.UpstreamCount[cells[0]], Is.EqualTo(0), $"线 {li}：起点须是河源");
			Assert.That(graph.DownstreamRiver[cells[^1]], Is.EqualTo(-1), $"线 {li}：终点须是出口");
			for (int k = 0; k < cells.Length; k++)
			{
				covered.Add(cells[k]);
				if (k == 0) continue;
				Assert.That(nbrs[cells[k - 1]].Contains(cells[k]), Is.True,
					$"线 {li}：第 {k} 点须与前一点 H3 相邻（几何连续）");
				Assert.That(r.RunoffAccum[cells[k]], Is.GreaterThanOrEqualTo(r.RunoffAccum[cells[k - 1]]),
					$"线 {li}：径流沿程不得减少");
			}
			Assert.That(geo.Lines[li].Length, Is.EqualTo(cells.Length), $"线 {li}：点与格一一对应");
		}
		for (int i = 0; i < r.IsRiver.Length; i++)
			if (r.IsRiver[i]) Assert.That(covered.Contains(i), Is.True, $"格 {i}：河流格须被某条河线覆盖");
	}

	[Test]
	public void UniformPrecip_GraphTopologyDegradesToCellCountCriterion()
	{
		// ★永久架构原则（退化解）：新物理量进入既有算法时，必须存在可验证的退化解——
		// 把降水设成常量 ⇒ 河网图拓扑须精确退化到"格数判据"的图（不只掩码等价，拓扑也要等价）
		var (_, gCells, _, _, _) = Make(threshold: 12, precip: null);
		var uniform = Enumerable.Repeat(100f, Ball.CellDirs.Length).ToArray();
		var (_, gWet, _, _, _) = Make(threshold: 12, precip: uniform);

		Assert.That(gWet.RiverCellCount, Is.EqualTo(gCells.RiverCellCount), "均匀降水：河流格数须与格数判据一致");
		for (int i = 0; i < gCells.DownstreamRiver.Length; i++)
			Assert.That(gWet.DownstreamRiver[i], Is.EqualTo(gCells.DownstreamRiver[i]),
				$"格 {i}：均匀降水的河网图拓扑须与格数判据完全一致（退化等价）");
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.g.DownstreamRiver, b.g.DownstreamRiver);
		CollectionAssert.AreEqual(a.g.NodeKind, b.g.NodeKind);
		Assert.That(a.geo.LineCells.Count, Is.EqualTo(b.geo.LineCells.Count));
	}
}
