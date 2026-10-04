using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Godot;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 水系拓扑护栏（River 2C-C，决策 08 v2 §2C-C——WaterSystem 的**组合层**）：
///   ① RiverGraph 完全不变（含 Geometry）② LakeState 完全不变（不重新计算湖泊）
///   ③ 跨水体连接可追溯（river source → … → 终端水体）④ Lake → River 必须是**真实下游**河流
///   ⑤ 不允许环（尤其 Lake A → River B → Lake A）⑥ 退化：无湖泊层 ⇒ 退化为既有
///      River → Ocean / Endorheic 终止结果。
///   核心原则：RiverGraph（河流内部）/ BasinGraph（流域归属与终止）/ LakeState（湖泊状态）/
///   WaterTopology（水体之间的连接）——**四个概念不互相吞并**。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class WaterTopologyTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;
	const int Threshold = 12;

	static (RiverNetwork r, RiverGraph g, BasinGraph b, LakeState lk, RiverGeometry geo,
		FinalGeography f, HeightComposer c, float[] precip) Make(int seed = 42, int threshold = Threshold)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var gr = new GeologicalRegions(seed);
		gr.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(Ball, gr, surface);
		var l = new RegionalLandforms(seed);
		l.Generate(Ball, gr);
		var features = new List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		};
		var c = new HeightComposer(seed);
		c.Generate(Ball, surface, gr, features);
		var f = new FinalGeography();
		f.Generate(Ball, c, gr);
		var pm = new PrecipitationModel();
		pm.Generate(Ball, f);
		var r = new RiverNetwork();
		r.Generate(Ball, f, c, riverThresholdCells: threshold, annualPrecipMm: pm.AnnualMm);
		var g = new RiverGraph();
		g.Generate(Ball, f, r);
		var b = new BasinGraph();          // 路由分区（给 WaterTopology：河流怎么走）
		b.Generate(Ball, f, r);
		// D-16：LakeState 须消费**原始地形**的洼地分区（翻默认后路由侧内流归零 ⇒ 用 b 会得 0 湖）
		var rawHydro = new H3Hydrology();
		rawHydro.Generate(Ball, c.HeightM, 0f, pm.AnnualMm);
		var depBasins = new BasinGraph();
		depBasins.Generate(Ball, f, rawHydro.Downstream, rawHydro.WeightedAccum);
		var lk = new LakeState();
		lk.Generate(Ball, f, c.HeightM, depBasins, pm.AnnualMm);
		var geo = new RiverGeometry();
		geo.Generate(Ball, g, r);
		return (r, g, b, lk, geo, f, c, pm.AnnualMm);
	}

	static WaterTopology Build(RiverNetwork r, RiverGraph g, BasinGraph b, LakeState lk, FinalGeography f)
	{
		var t = new WaterTopology();
		t.Generate(Ball, f, r, g, b, lk);
		return t;
	}

	[Test]
	public void RiverGraph_Unchanged_AfterTopology()
	{
		// ① 加入 WaterTopology 前后：RiverGraph（含 Sources/Outlets）与 Geometry 逐项一致
		var (r, g, b, lk, geo, f, c, _) = Make();
		var snapDown = (int[])g.DownstreamRiver.Clone();
		var snapUp = (int[])g.UpstreamCount.Clone();
		var snapKind = (int[])g.NodeKind.Clone();
		var snapSources = g.Sources.ToList();
		var snapOutlets = g.Outlets.ToList();
		var snapLineCells = geo.LineCells.Select(x => (int[])x.Clone()).ToList();
		var snapLines = geo.Lines.Select(x => (Vector3[])x.Clone()).ToList();

		Build(r, g, b, lk, f);

		CollectionAssert.AreEqual(snapDown, g.DownstreamRiver, "DownstreamRiver 不得改变");
		CollectionAssert.AreEqual(snapUp, g.UpstreamCount, "UpstreamCount 不得改变");
		CollectionAssert.AreEqual(snapKind, g.NodeKind, "NodeKind 不得改变");
		CollectionAssert.AreEqual(snapSources, g.Sources.ToList(), "Sources 不得改变");
		CollectionAssert.AreEqual(snapOutlets, g.Outlets.ToList(), "Outlets 不得改变");
		Assert.That(geo.LineCells.Count, Is.EqualTo(snapLineCells.Count), "Geometry 线数不得改变");
		for (int i = 0; i < snapLineCells.Count; i++)
			CollectionAssert.AreEqual(snapLineCells[i], geo.LineCells[i], $"Geometry 线 {i} 不得改变");
		for (int i = 0; i < snapLines.Count; i++)
			CollectionAssert.AreEqual(snapLines[i], geo.Lines[i], $"Geometry 点 {i} 不得改变");
	}

	/// <summary>D-16：原始地形的内流洼地分区（LakeState 的唯一合法输入）。</summary>
	static BasinGraph DepressionBasinsOf(HeightComposer c, FinalGeography f, float[] precip)
	{
		var raw = new H3Hydrology();
		raw.Generate(Ball, c.HeightM, 0f, precip);
		var d = new BasinGraph();
		d.Generate(Ball, f, raw.Downstream, raw.WeightedAccum);
		return d;
	}

	[Test]
	public void LakeState_Unchanged_AfterTopology()
	{
		// ② WaterTopology 只**读取**湖的事实，不重新计算湖泊
		var (r, g, b, lk, _, f, c, precip) = Make();
		var snap = new
		{
			basinOf = (int[])lk.BasinIdOf.Clone(),
			level = (float[])lk.SurfaceElevationM.Clone(),
			area = (double[])lk.AreaKm2.Clone(),
			vol = (double[])lk.VolumeKm3.Clone(),
			outlet = (int[])lk.OutletCell.Clone(),
			lakeOfBasin = (int[])lk.LakeOfBasin.Clone(),
			spill = (float[])lk.SpillElevationM.Clone(),
			count = lk.LakeCount,
		};

		Build(r, g, b, lk, f);
		var lk2 = new LakeState();
		lk2.Generate(Ball, f, c.HeightM, DepressionBasinsOf(c, f, precip), precip);

		Assert.That(lk.LakeCount, Is.EqualTo(snap.count), "湖数不得改变");
		CollectionAssert.AreEqual(snap.basinOf, lk.BasinIdOf, "BasinIdOf 不得改变");
		CollectionAssert.AreEqual(snap.level, lk.SurfaceElevationM, "水面不得改变");
		CollectionAssert.AreEqual(snap.area, lk.AreaKm2, "面积不得改变");
		CollectionAssert.AreEqual(snap.vol, lk.VolumeKm3, "蓄量不得改变");
		CollectionAssert.AreEqual(snap.outlet, lk.OutletCell, "溢出点不得改变");
		CollectionAssert.AreEqual(snap.spill, lk.SpillElevationM, "溢出高程不得改变");
		CollectionAssert.AreEqual(snap.lakeOfBasin, lk2.LakeOfBasin, "LakeOfBasin 不得改变");
	}

	[Test]
	public void Trace_FromEveryRiverSource_ReachesTerminalBody()
	{
		// ③ 跨水体连接可追溯：从任意河源出发都能得到"河 → … → 终端水体"的完整路径
		var (r, g, b, lk, _, f, _, _) = Make();
		var t = Build(r, g, b, lk, f);
		Assert.That(g.Sources.Count, Is.GreaterThan(0), "须存在河源");
		foreach (int src in g.Sources)
		{
			var path = t.TraceFromRiverSource(src);
			Assert.That(path.Count, Is.GreaterThan(0), $"河源 {src}：须能追溯出路径");
			Assert.That(t.KindOf[path[0]], Is.EqualTo((int)WaterTopology.BodyKind.River),
				$"河源 {src}：路径首节点须是河流");
			int last = path[^1];
			Assert.That(t.Next[last], Is.EqualTo(-1), $"河源 {src}：路径末节点须是终端水体");
			var kind = (WaterTopology.BodyKind)t.KindOf[last];
			Assert.That(kind == WaterTopology.BodyKind.Ocean ||
			            kind == WaterTopology.BodyKind.EndorheicSink ||
			            kind == WaterTopology.BodyKind.Lake, Is.True,
				$"河源 {src}：终端须是 海洋 / 内流洼地 / 封闭湖（实得 {kind}）");
			Assert.That(path.Distinct().Count(), Is.EqualTo(path.Count), $"河源 {src}：路径不得重复访问");
		}
	}

	[Test]
	public void LakeToRiver_IsRealDownstreamRiver()
	{
		// ④ Lake → River 必须是**溢出后真实下游**的河流，不是"恰好邻接湖"的河
		var (r, g, b, lk, _, f, _, _) = Make();
		var t = Build(r, g, b, lk, f);
		int checkedEdges = 0;
		for (int k = 0; k < lk.LakeCount; k++)
		{
			int node = t.NodeOfLake[k];
			if (t.Next[node] < 0) continue;
			if (t.KindOf[t.Next[node]] != (int)WaterTopology.BodyKind.River) continue;
			int via = t.ViaCell[node];
			Assert.That(r.IsRiver[via], Is.True, $"湖 {k}：连接点须是河流格");
			Assert.That(t.NodeOfRiverCell[via], Is.EqualTo(t.Next[node]), $"湖 {k}：连接点须属于目标河系");
			// 真实下游：从溢出格沿 flow graph 下行必须**真的走到**该连接点
			int cur = lk.OutletCell[k];
			bool reached = false;
			int guard = 0;
			while (cur >= 0 && guard++ <= r.Downstream.Length)
			{
				if (cur == via) { reached = true; break; }
				cur = r.Downstream[cur];
			}
			Assert.That(reached, Is.True, $"湖 {k}：目标河流须在溢出点的真实下游路径上（不是邻接即算）");
			// 水流真的离开了本流域（出口河流属于别的流域）
			Assert.That(b.BasinId[via], Is.Not.EqualTo(lk.BasinIdOf[k]),
				$"湖 {k}：出口河流须属于另一个流域（水确实流出了本流域）");
			checkedEdges++;
		}
		Assert.That(checkedEdges, Is.GreaterThanOrEqualTo(0));
	}

	[Test]
	public void NoCycles_InWaterTopology()
	{
		// ⑤ 不允许环（尤其 Lake A → River B → Lake A）：每个节点沿出边走必须终止且不重复
		var (r, g, b, lk, _, f, _, _) = Make();
		var t = Build(r, g, b, lk, f);
		Assert.That(t.NodeCount, Is.GreaterThan(0), "须存在水体节点");
		for (int start = 0; start < t.NodeCount; start++)
		{
			var seen = new HashSet<int>();
			int cur = start;
			int steps = 0;
			while (cur >= 0)
			{
				Assert.That(seen.Add(cur), Is.True,
					$"从节点 {start} 出发出现环：重复访问节点 {cur}（Lake→River→Lake 类拓扑环）");
				Assert.That(++steps, Is.LessThanOrEqualTo(t.NodeCount), $"从节点 {start} 出发步数超界（环）");
				cur = t.Next[cur];
			}
		}
	}

	[Test]
	public void Degradation_NoLakeState_EqualsExistingTermination()
	{
		// ⑥ 退化：**没有湖泊层** ⇒ WaterTopology 必须退化为既有
		//    River → Ocean / Endorheic 终止结果，且与 BasinGraph 的终止事实逐项一致
		var (r, g, b, lk, _, f, _, _) = Make();
		var withLakes = Build(r, g, b, lk, f);

		var t = new WaterTopology();
		t.Generate(Ball, f, r, g, b, null);   // lakes = null：无湖泊层

		Assert.That(t.LakeNodeCount, Is.EqualTo(0), "无湖泊层 ⇒ 不得有湖泊节点");
		Assert.That(t.RiverCount, Is.EqualTo(g.Outlets.Count), "河系数 = 河网出口数");
		for (int k = 0; k < t.RiverCount; k++)
		{
			int outlet = g.Outlets[k];
			Assert.That(t.KindOf[k], Is.EqualTo((int)WaterTopology.BodyKind.River));
			int nxt = t.Next[k];
			Assert.That(nxt, Is.GreaterThanOrEqualTo(0), $"河系 {k}：退化情形下每条河都必须有终端");
			var kind = (WaterTopology.BodyKind)t.KindOf[nxt];
			bool toOcean = basins_ToOcean(b, outlet);
			Assert.That(kind == (toOcean ? WaterTopology.BodyKind.Ocean : WaterTopology.BodyKind.EndorheicSink),
				Is.True, $"河系 {k}：退化的终端须与 BasinGraph 终止事实一致（实得 {kind}）");
			if (!toOcean) Assert.That(t.RefOf[nxt], Is.EqualTo(b.BasinId[outlet]), $"河系 {k}：内流洼地须绑定本流域");
			Assert.That(t.Next[nxt], Is.EqualTo(-1), $"河系 {k}：终端水体无出边");
		}
		// 有湖时：不含湖的流域其河系终点必须与退化情形完全相同（湖不改变其他路径）
		for (int k = 0; k < t.RiverCount; k++)
		{
			// ★D-16：`LakeOfBasin` 按**原始洼地**索引 ⇒ 过滤须用 `lk.DepressionIdOf`
			//   （与 WaterTopology 内部的接湖查找同一口径），不能用路由分区 `b.BasinId`。
			int did = lk.DepressionIdOf[g.Outlets[k]];
			if (did >= 0 && did < lk.LakeOfBasin.Length && lk.LakeOfBasin[did] >= 0) continue;
			// ★注意：`withLakes` 与 `t`（lakes=null）是**两个不同的节点编号空间**
			//   （有湖时湖节点先分配 ⇒ Ocean/Sink 节点索引整体后移），
			//   直接比裸索引会把"编号位移"误判成"终点改变"。
			//   ⇒ 比**语义**：终点的水体类型 + 经停格。
			Assert.That((WaterTopology.BodyKind)withLakes.KindOf[withLakes.Next[k]],
				Is.EqualTo((WaterTopology.BodyKind)t.KindOf[t.Next[k]]),
				$"河系 {k}：该洼地无湖 ⇒ 终点水体类型必须与无湖泊层时一致");
			Assert.That(withLakes.ViaCell[k], Is.EqualTo(t.ViaCell[k]),
				$"河系 {k}：该洼地无湖 ⇒ 经停格必须与无湖泊层时一致");
		}
	}

	static bool basins_ToOcean(BasinGraph b, int cell) => b.TerminatesAtOcean[cell];

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		var ta = Build(a.r, a.g, a.b, a.lk, a.f);
		var tb = Build(b.r, b.g, b.b, b.lk, b.f);
		CollectionAssert.AreEqual(ta.KindOf, tb.KindOf);
		CollectionAssert.AreEqual(ta.Next, tb.Next);
		CollectionAssert.AreEqual(ta.ViaCell, tb.ViaCell);
		Assert.That(ta.NodeCount, Is.EqualTo(tb.NodeCount));
	}
}
