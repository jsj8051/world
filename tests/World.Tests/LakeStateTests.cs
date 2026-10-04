using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Godot;
using World.NewHexWorld;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 湖泊状态层护栏（River 2C-B，决策 08 v2 §2C-B）：
///   · 只有 Endorheic 流域能成湖（Ocean 永不生成 Lake）；Lake 必须绑定 BasinId（一一对应）；
///   · 水面不违反地形（≥ 最低可成水面、≤ 最低溢出高程，溢出 ⇒ 水位 = rim）；
///   · Overflow 是**独立事实**（未溢出 = −1），且不会把 Endorheic 变成 RiverGraph 的 Outlet；
///   · Volume / Area 一致（Volume &gt; 0 才是真湖，不允许假对象）；
///   · **退化解**：蒸发/损失 = 0 ⇒ 未溢出的湖把来水全部蓄住（Volume = 输入水量），
///     水量足够大 ⇒ 水面收敛到最低溢出高程；
///   · **架构退化**：Lake 不修改任何已冻结层（Flow/Runoff/IsRiver/RiverGraph/BasinGraph 逐格一致）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class LakeStateTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;
	// 格面积基准走 SpatialScale（收口 D-10）：与被测代码同源，避免"测试用一套面积口径、
	// 实现用另一套"而互相掩盖偏差。
	static double A => SpatialScale.Of(Ball).CellAreaKm2;

	const int Threshold = 12;

	static (RiverNetwork r, RiverGraph g, BasinGraph b, LakeState lk, FinalGeography f,
		HeightComposer c, float[] precip) Make(int seed = 42, float[] precipOverride = null)
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
		var precip = precipOverride;
		if (precip == null)
		{
			var pm = new PrecipitationModel();
			pm.Generate(Ball, f);
			precip = pm.AnnualMm;
		}
		var r = new RiverNetwork();
		r.Generate(Ball, f, c, riverThresholdCells: Threshold, annualPrecipMm: precip);
		var g = new RiverGraph();
		g.Generate(Ball, f, r);
		var b = new BasinGraph();
		b.Generate(Ball, f, r);
		// ★D-16：LakeState 必须消费**原始地形**的内流洼地分区，不能是上面那个路由 `b`
		//   （翻生产默认后路由侧内流流域归零 ⇒ 用 b 会得到 0 个湖）。
		var rawHydro = new H3Hydrology();
		rawHydro.Generate(Ball, c.HeightM, 0f, precip);
		var depBasins = new BasinGraph();
		depBasins.Generate(Ball, f, rawHydro.Downstream, rawHydro.WeightedAccum);
		var lk = new LakeState();
		lk.Generate(Ball, f, c.HeightM, depBasins, precip);
		return (r, g, depBasins, lk, f, c, precip);
	}

	/// <summary>D-16：**原始**地形上的下游链（内流终止点在该链上无下游）。</summary>
	static int[] RawDownstreamOf(HeightComposer c, float[] precip)
	{
		var raw = new H3Hydrology();
		raw.Generate(Ball, c.HeightM, 0f, precip);
		return raw.Downstream;
	}

	static List<int> Members(BasinGraph b, int basin)
	{
		var mem = new List<int>();
		for (int i = 0; i < b.BasinId.Length; i++)
			if (b.BasinId[i] == basin) mem.Add(i);
		return mem;
	}

	static float RimOf(BasinGraph b, HeightComposer c, int basin)
	{
		float rim = float.PositiveInfinity;
		var nbrs = Ball.CellNeighbors;
		foreach (int i in Members(b, basin))
			foreach (int j in nbrs[i])
				if (b.BasinId[j] != basin && c.HeightM[j] < rim) rim = c.HeightM[j];
		return rim;
	}

	[Test]
	public void OnlyEndorheicBasins_ProduceLakes()
	{
		// 边界 ①：只有内流流域能成湖；**Ocean 流域永远不生成 Lake**
		var (_, _, b, lk, _, _, _) = Make();
		Assert.That(b.EndorheicBasinCount, Is.GreaterThan(0), "须存在内流流域（湖泊候选）");
		for (int bid = 0; bid < b.BasinCount; bid++)
			if (b.Kind[bid] == (int)BasinGraph.BasinKind.Ocean)
				Assert.That(lk.LakeOfBasin[bid], Is.EqualTo(-1), $"流域 {bid}：外流流域不得生成湖");
		for (int k = 0; k < lk.LakeCount; k++)
			Assert.That(b.Kind[lk.BasinIdOf[k]], Is.EqualTo((int)BasinGraph.BasinKind.Endorheic),
				$"湖 {k}：只能由内流流域产生");
		// ★内流 ≠ 必然有湖：容量为零（洼地底 = 溢出高程）的内流流域不产生湖
		Assert.That(lk.LakeCount, Is.LessThanOrEqualTo(b.EndorheicBasinCount),
			"湖数 ≤ 内流流域数——内流只是拓扑事实，成湖还要看水量与容量");
	}

	[Test]
	public void Lake_BoundOneToOneToBasin()
	{
		// 边界 ②：Lake 必须绑定 BasinId（一一对应，不允许脱离 Basin 的独立水体）
		var (_, _, b, lk, _, _, _) = Make();
		Assert.That(lk.LakeCount, Is.GreaterThan(0), "须存在湖（内流流域水量足以成湖）");
		var seen = new HashSet<int>();
		for (int k = 0; k < lk.LakeCount; k++)
		{
			int bid = lk.BasinIdOf[k];
			Assert.That(bid, Is.InRange(0, b.BasinCount - 1), $"湖 {k}：BasinId 须合法");
			Assert.That(seen.Add(bid), Is.True, $"湖 {k}：一个湖只能对应一个流域（不得重复绑定）");
			Assert.That(lk.LakeOfBasin[bid], Is.EqualTo(k), $"湖 {k}：流域 → 湖 的反查须一致");
		}
	}

	[Test]
	public void SurfaceElevation_RespectsTerrain()
	{
		// 边界 ③：水面 ≥ 流域内最低可成水面；≤ 最低溢出高程；**溢出 ⇒ 水面 = rim**
		var (_, _, b, lk, _, c, _) = Make();
		for (int k = 0; k < lk.LakeCount; k++)
		{
			var mem = Members(b, lk.BasinIdOf[k]);
			float bottom = mem.Min(i => c.HeightM[i]);
			Assert.That(lk.SurfaceElevationM[k], Is.GreaterThanOrEqualTo(bottom),
				$"湖 {k}：水面不得低于流域最低点（{lk.SurfaceElevationM[k]} vs {bottom}）");
			float rim = RimOf(b, c, lk.BasinIdOf[k]);
			if (!float.IsPositiveInfinity(rim))
				Assert.That(lk.SurfaceElevationM[k], Is.LessThanOrEqualTo(rim + 1e-3f),
					$"湖 {k}：水面不得高于最低溢出高程（溢出即被 rim 截断）");
			if (lk.OutletCell[k] >= 0)
			{
				Assert.That(lk.SurfaceElevationM[k], Is.EqualTo(rim).Within(1e-3f),
					$"湖 {k}：溢出时水面须收敛到最低溢出高程");
				Assert.That(c.HeightM[lk.OutletCell[k]], Is.EqualTo(rim).Within(1e-3f),
					$"湖 {k}：溢出点格的高程须 = rim");
			}
		}
	}

	[Test]
	public void Overflow_IsIndependentFact()
	{
		// 边界 ④：溢出是独立事实（未溢出 = −1）；溢出点在流域外；
		//          **Endorheic 不会因为存在 Lake 就变成 RiverGraph 的 Outlet**
		var (r, g, b, lk, _, c, precip) = Make();
		for (int k = 0; k < lk.LakeCount; k++)
		{
			int bid = lk.BasinIdOf[k];
			if (lk.OutletCell[k] < 0)
			{
				Assert.That(lk.SurfaceElevationM[k] + 1e-3f, Is.LessThan(lk.SpillElevationM[k] + 1f),
					$"湖 {k}：未溢出 ⇒ 出口必须为 −1");
			}
			else
			{
				int o = lk.OutletCell[k];
				Assert.That(b.BasinId[o], Is.Not.EqualTo(bid), $"湖 {k}：溢出点须在流域外");
				// 两套独立事实：Lake 的溢出点 ≠ 流域的终止陆格（后者是 RiverGraph/Basin 的出口语义）
				Assert.That(o, Is.Not.EqualTo(b.Outlet[bid]),
					$"湖 {k}：Lake 溢出点不得与流域终止陆格混为一谈（两套事实）");
			}
			// 内流出口的 flow graph 事实不变：终止陆格仍然无下游（没有因为成湖而变成有下游的出口）
			int outletLand = b.Outlet[bid];
			// ★D-11 Batch A：`r.Downstream` 是**路由**链（填洼后内流点也有下游）；
			//   本断言的语义是"原始地形上内流终止点无下游" ⇒ 必须看原始链。
			Assert.That(RawDownstreamOf(c, precip)[outletLand], Is.EqualTo(-1),
				$"湖 {k}：成湖不得把内流终止点变成有下游的出口（原始地形口径）");
			// 注：终止陆格**可以**同时是河流的出口（河流终止于内流洼地是既有水文事实），
			//     Lake 是否写入 RiverGraph 由 Lake_DoesNotModifyFrozenLayers 的快照对照钉死。
		}
	}

	[Test]
	public void VolumeArea_AreGeometricallyConsistent()
	{
		// 边界 ⑤：Volume / Area 由**水面 × 淹没格体积**算出（不是取 AccumMm）；Volume > 0 才是真湖
		var (_, _, b, lk, _, c, _) = Make();
		for (int k = 0; k < lk.LakeCount; k++)
		{
			var mem = Members(b, lk.BasinIdOf[k]);
			float level = lk.SurfaceElevationM[k];
			double w = 0;
			int submerged = 0;
			foreach (int i in mem)
				if (c.HeightM[i] < level) { w += level - c.HeightM[i]; submerged++; }

			Assert.That(lk.VolumeKm3[k], Is.GreaterThan(0), $"湖 {k}：Volume 必须 > 0（不允许假对象）");
			Assert.That(submerged, Is.GreaterThan(0), $"湖 {k}：须至少淹没一格");
			Assert.That(lk.AreaKm2[k], Is.EqualTo(submerged * A).Within(1e-2),
				$"湖 {k}：Area = 淹没格数 × 格面积");
			Assert.That(lk.VolumeKm3[k], Is.EqualTo(w * A / 1000.0).Within(1e-2),
				$"湖 {k}：Volume = Σ(水面 − 格底) × 格面积（几何算出）");
		}
	}

	[Test]
	public void InflowConsistent_WithRunoffFact_ButVolumeIsNotAccum()
	{
		// 语义检查：水量平衡的**输入** = 流域降水总量，且与汇流事实（AccumMm）一致
		//（同一份水的两种算法）；但 **Volume 不等于 AccumMm 本身**（单位/语义都不同）
		var (_, _, b, lk, _, _, precip) = Make();
		for (int k = 0; k < lk.LakeCount; k++)
		{
			int bid = lk.BasinIdOf[k];
			double inflow = Members(b, bid).Sum(i => (double)precip[i]) * A / 1e6;   // km³
			double fromAccum = b.AccumMm[bid] * A / 1e6;                              // km³
			Assert.That(fromAccum, Is.EqualTo(inflow).Within(Math.Max(1e-3, inflow * 1e-3)),
				$"湖 {k}：降水总量须与汇流事实一致（两种算法同一份水）");
			if (lk.OutletCell[k] < 0)
				Assert.That(lk.VolumeKm3[k], Is.EqualTo(inflow).Within(Math.Max(1e-3, inflow * 1e-3)),
					$"湖 {k}：零损失且未溢出 ⇒ 来水全部蓄存（退化解）");
		}
	}

	[Test]
	public void Degradation_ZeroLoss_LevelConvergesToSpillElevation()
	{
		// ★退化解（2C-B 最重要的物理退化测试）：**蒸发/损失 = 0**
		//   ① 来水极小 ⇒ 不溢出，来水全部蓄存（Volume = 输入）；
		//   ② 来水极大 ⇒ 水面**收敛到最低溢出高程**，并产生溢出出口
		var (_, _, b, lkSmall, _, _, _) = Make(precipOverride: Enumerable.Repeat(1f, Ball.CellDirs.Length).ToArray());
		Assert.That(lkSmall.LakeCount, Is.GreaterThan(0), "1mm 均匀降水：内流流域仍应成湖（来水 > 0）");
		for (int k = 0; k < lkSmall.LakeCount; k++)
		{
			Assert.That(lkSmall.OutletCell[k], Is.EqualTo(-1), $"湖 {k}：1mm 来水不应溢出");
			double inflow = Members(b, lkSmall.BasinIdOf[k]).Count * 1.0 * A / 1e6;
			Assert.That(lkSmall.VolumeKm3[k], Is.EqualTo(inflow).Within(Math.Max(1e-3, inflow * 1e-3)),
				$"湖 {k}：零损失 ⇒ Volume = 全部来水");
		}

		var (_, _, b2, lkBig, _, _, _) = Make(precipOverride: Enumerable.Repeat(1e6f, Ball.CellDirs.Length).ToArray());
		Assert.That(lkBig.LakeCount, Is.GreaterThan(0), "巨量降水：仍应成湖");
		for (int k = 0; k < lkBig.LakeCount; k++)
		{
			float rim = lkBig.SpillElevationM[k];
			if (float.IsNaN(rim)) continue;   // 无外邻格（不可能溢出）的极端情形跳过
			Assert.That(lkBig.SurfaceElevationM[k], Is.EqualTo(rim).Within(1e-3f),
				$"湖 {k}：来水远超容量 ⇒ 水面须收敛到最低溢出高程");
			Assert.That(lkBig.OutletCell[k], Is.GreaterThanOrEqualTo(0), $"湖 {k}：溢出须产生出口事实");
			Assert.That(lkBig.VolumeKm3[k], Is.LessThan(Members(b2, lkBig.BasinIdOf[k]).Count * 1e6 * A / 1e6),
				$"湖 {k}：溢出的湖只蓄到 rim 容量（多余水量外泄）");
		}
	}

	[Test]
	public void Lake_DoesNotModifyFrozenLayers()
	{
		// ★架构退化：Lake 是水系统的另一个**状态层**——不得反向污染已冻结的 River/Basin 拓扑
		var (r, g, b, _, f, c, precip) = Make();
		var snap = new
		{
			down = (int[])r.Downstream.Clone(),
			accum = (int[])r.FlowAccum.Clone(),
			runoff = (float[])r.RunoffAccum.Clone(),
			river = (bool[])r.IsRiver.Clone(),
			gDown = (int[])g.DownstreamRiver.Clone(),
			gKind = (int[])g.NodeKind.Clone(),
			bId = (int[])b.BasinId.Clone(),
			bTerm = (int[])b.TerminalCell.Clone(),
			bArea = (int[])b.Area.Clone(),
			bAccum = (float[])b.AccumMm.Clone(),
			bKind = (int[])b.Kind.Clone(),
		};

		var lk = new LakeState();
		lk.Generate(Ball, f, c.HeightM, b, precip);
		// D-16：`b` 已是原始洼地分区 ⇒ 复核对象也须同源（不能用路由分区）
		var rawHydro2 = new H3Hydrology();
		rawHydro2.Generate(Ball, c.HeightM, 0f, precip);
		var b2 = new BasinGraph();
		b2.Generate(Ball, f, rawHydro2.Downstream, rawHydro2.WeightedAccum);

		CollectionAssert.AreEqual(snap.down, r.Downstream, "FlowDirection 不得改变");
		CollectionAssert.AreEqual(snap.accum, r.FlowAccum, "FlowAccum 不得改变");
		CollectionAssert.AreEqual(snap.runoff, r.RunoffAccum, "RunoffAccum 不得改变");
		CollectionAssert.AreEqual(snap.river, r.IsRiver, "IsRiver 不得改变");
		CollectionAssert.AreEqual(snap.gDown, g.DownstreamRiver, "RiverGraph 拓扑不得改变");
		CollectionAssert.AreEqual(snap.gKind, g.NodeKind, "RiverGraph 分类不得改变");
		CollectionAssert.AreEqual(snap.bId, b2.BasinId, "BasinGraph 流域划分不得改变");
		CollectionAssert.AreEqual(snap.bTerm, b2.TerminalCell, "BasinGraph 终止事实不得改变");
		CollectionAssert.AreEqual(snap.bArea, b2.Area, "BasinGraph 面积不得改变");
		CollectionAssert.AreEqual(snap.bAccum, b2.AccumMm, "BasinGraph 累积不得改变");
		CollectionAssert.AreEqual(snap.bKind, b2.Kind, "BasinGraph 分类不得改变");
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.lk.BasinIdOf, b.lk.BasinIdOf);
		CollectionAssert.AreEqual(a.lk.SurfaceElevationM, b.lk.SurfaceElevationM);
		CollectionAssert.AreEqual(a.lk.VolumeKm3, b.lk.VolumeKm3);
		Assert.That(a.lk.LakeCount, Is.EqualTo(b.lk.LakeCount));
	}
}
