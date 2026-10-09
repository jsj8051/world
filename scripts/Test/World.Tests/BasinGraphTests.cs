using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Godot;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 流域拓扑护栏（River 2C-A，决策 08 v2 §3.8）：
///   · **只验 Basin，不验 Lake**——本轮刻意不出现湖的任何语义（留给 2C-B）。
///   · 拓扑：每格唯一 Basin / 同终点同 BasinId / Ocean 能追溯到海 / Endorheic 不得伪装成 Ocean；
///   · 统计：Area = 成员格数（不重复计数）、Accumulation 不凭空改变；
///   · **退化解**（永久架构原则）：加入 BasinGraph 后，既有 FlowGraph 结果必须逐格完全一致。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class BasinGraphTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (RiverNetwork r, RiverGraph g, BasinGraph b, FinalGeography f, HeightComposer c)
		Make(int seed = 42, int threshold = 12, float[] precip = null)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
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
		var basins = new BasinGraph();
		basins.Generate(Ball, f, r);
		return (r, graph, basins, f, c);
	}

	[Test]
	public void EveryLandCell_HasExactlyOneBasin()
	{
		// 拓扑 ①：每个陆格归属唯一流域；海格不是任何流域的成员（它是出口）
		var (_, _, b, f, _) = Make();
		Assert.That(b.BasinCount, Is.GreaterThan(0), "须存在流域");
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (f.FinalLand[i])
			{
				Assert.That(b.BasinId[i], Is.GreaterThanOrEqualTo(0), $"格 {i}：陆格须有流域");
				Assert.That(b.BasinId[i], Is.LessThan(b.BasinCount), $"格 {i}：流域号须在范围内");
			}
			else
			{
				Assert.That(b.BasinId[i], Is.EqualTo(-1), $"格 {i}：海格不得属于任何流域");
			}
		}
	}

	[Test]
	public void SameTerminal_SharesSameBasinId()
	{
		// 拓扑 ②：同一终止陆格 ⇒ 同一 BasinId（不同河口 = 不同流域 ⇒ 双射）
		var (_, _, b, f, _) = Make();
		var idOfTerminal = new Dictionary<int, int>();
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			int t = b.TerminalCell[i];
			if (idOfTerminal.TryGetValue(t, out int id))
				Assert.That(b.BasinId[i], Is.EqualTo(id), $"格 {i}：同终点须同流域");
			else
				idOfTerminal[t] = b.BasinId[i];
		}
		// 双射：每个流域的出口互不相同（∀b≠b' : Outlet[b] ≠ Outlet[b']）
		var seen = new HashSet<int>();
		for (int bid = 0; bid < b.BasinCount; bid++)
			Assert.That(seen.Add(b.Outlet[bid]), Is.True, $"流域 {bid}：出口须唯一（不得合并不同终点）");
		Assert.That(idOfTerminal.Count, Is.EqualTo(b.BasinCount));
	}

	[Test]
	public void OceanBasins_TraceToSea_EndorheicCannotDisguise()
	{
		// 拓扑 ③④：Ocean 流域的出口下游是海；Endorheic 流域的出口是陆格且**无下游**（内流洼地）
		var (r, _, b, f, _) = Make();
		Assert.That(b.OceanBasinCount, Is.GreaterThan(0), "须存在外流流域");
		for (int bid = 0; bid < b.BasinCount; bid++)
		{
			int o = b.Outlet[bid];
			Assert.That(f.FinalLand[o], Is.True, $"流域 {bid}：出口须是陆格");
			if (b.Kind[bid] == (int)BasinGraph.BasinKind.Ocean)
			{
				int d = r.Downstream[o];
				Assert.That(d, Is.GreaterThanOrEqualTo(0), $"流域 {bid}：外流出口须有下游格");
				Assert.That(f.FinalLand[d], Is.False, $"流域 {bid}：外流出口的下游须是海");
				Assert.That(b.TerminatesAtOcean[o], Is.True, $"流域 {bid}：原子事实须记为入海");
			}
			else
			{
				Assert.That(r.Downstream[o], Is.EqualTo(-1),
					$"流域 {bid}：内流出口须无下游（不得伪装成 Ocean）");
				Assert.That(b.TerminatesAtOcean[o], Is.False, $"流域 {bid}：原子事实须记为内流");
			}
		}
	}

	[Test]
	public void Outlet_MatchesFlowGraphTermination()
	{
		// 拓扑 ⑤：Outlet 与 FlowGraph 的终止事实一致——用独立暴力走法逐格对照
		var (r, _, b, f, _) = Make();
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			int cur = i;
			while (r.Downstream[cur] >= 0 && f.FinalLand[r.Downstream[cur]]) cur = r.Downstream[cur];
			Assert.That(b.TerminalCell[i], Is.EqualTo(cur), $"格 {i}：终止陆格须与 flow graph 一致");
			Assert.That(b.Outlet[b.BasinId[i]], Is.EqualTo(cur), $"格 {i}：所属流域的出口须 = 终止陆格");
		}
	}

	[Test]
	public void Area_EqualsMemberCount_AccumulationUnchanged()
	{
		// 统计：Area = 成员格数（Σ = 陆格总数 ⇒ 不重复计数、不漏格）；
		//       Accumulation 不凭空改变（= 出口格既有的 RunoffAccum，不重新求和）
		var (r, _, b, f, _) = Make();
		int landCells = 0;
		var recount = new int[b.BasinCount];
		for (int i = 0; i < f.FinalLand.Length; i++)
			if (f.FinalLand[i]) { landCells++; recount[b.BasinId[i]]++; }
		Assert.That(b.Area.Sum(), Is.EqualTo(landCells), "Σ Area = 全部陆格数（不重复、不遗漏）");
		for (int bid = 0; bid < b.BasinCount; bid++)
		{
			Assert.That(b.Area[bid], Is.EqualTo(recount[bid]), $"流域 {bid}：Area 须 = 成员格数");
			Assert.That(b.AccumMm[bid], Is.EqualTo(r.RunoffAccum[b.Outlet[bid]]),
				$"流域 {bid}：累积须直接取出口格既有值（不得重算）");
		}
	}

	[Test]
	public void BasinGraph_DoesNotChangeHydrology()
	{
		// ★退化解（永久架构原则）：BasinGraph 只是对既有 flow termination 做拓扑归类——
		//   加入后 RiverNetwork / RiverGraph 的全部结果必须**逐格完全一致**
		var (r, g, _, f, _) = Make();
		var downBefore = (int[])r.Downstream.Clone();
		var accumBefore = (int[])r.FlowAccum.Clone();
		var runoffBefore = (float[])r.RunoffAccum.Clone();
		var riverBefore = (bool[])r.IsRiver.Clone();
		var dsRiverBefore = (int[])g.DownstreamRiver.Clone();
		var kindBefore = (int[])g.NodeKind.Clone();

		var b = new BasinGraph();
		b.Generate(Ball, f, r);
		var g2 = new RiverGraph();
		g2.Generate(Ball, f, r);

		CollectionAssert.AreEqual(downBefore, r.Downstream, "FlowDirection 不得改变");
		CollectionAssert.AreEqual(accumBefore, r.FlowAccum, "FlowAccumulation 不得改变");
		CollectionAssert.AreEqual(runoffBefore, r.RunoffAccum, "RunoffAccum 不得改变");
		CollectionAssert.AreEqual(riverBefore, r.IsRiver, "IsRiver 不得改变");
		CollectionAssert.AreEqual(dsRiverBefore, g2.DownstreamRiver, "河网图拓扑不得改变");
		CollectionAssert.AreEqual(kindBefore, g2.NodeKind, "河网图节点分类不得改变");
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.b.BasinId, b.b.BasinId);
		CollectionAssert.AreEqual(a.b.TerminalCell, b.b.TerminalCell);
		CollectionAssert.AreEqual(a.b.Area, b.b.Area);
		Assert.That(a.b.BasinCount, Is.EqualTo(b.b.BasinCount));
	}
}
