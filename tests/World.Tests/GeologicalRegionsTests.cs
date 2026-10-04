using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 地质区域 v2 护栏（决策 03 v2：先划分区域再定类型的完整 pipeline）：
///   · 分区：陆格全覆盖、海格 −1；区域不跨大陆（嵌套性）；区域数 = 面积口径有界；确定性逐位同。
///   · 类型：七类约束式 + Softmax + 配额——类型多样；RIFT 硬约束（必邻接高地系）；
///     COASTAL 是近海者；区域类型 ≠ 地形类型（合成海拔仅为占位调制，逐格精确）。
///   · v2 结构：连续扭曲 Region Field（Lloyd 后种子收敛、区域不破碎——邻接图有限连通）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class GeologicalRegionsTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格（链路验证档）
	static Ball Ball => SharedBall.Value;

	// res1 842 格（~60 万 km²/格）：目标区域面积 800 万 km² ⇒ 每大陆 2-3 区域，每区域 12-17 格
	static (GeologicalRegions g, H3LandSeaProjector p) Make(int seed = 42, float targetAreaKm2 = 8_000_000f)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, targetAreaKm2);
		return (g, proj);
	}

	[Test]
	public void RegionOfCell_LandFullyCovered_OceanNegativeOne()
	{
		var (g, p) = Make();
		Assert.That(g.RegionOfCell.Length, Is.EqualTo(p.PlacementLand.Length));
		for (int i = 0; i < p.PlacementLand.Length; i++)
		{
			if (p.PlacementLand[i]) Assert.That(g.RegionOfCell[i], Is.InRange(0, g.Regions.Length - 1), $"陆格 {i} 须有区域");
			else Assert.That(g.RegionOfCell[i], Is.EqualTo(-1), $"海格 {i} 须为 −1");
		}
	}

	[Test]
	public void Regions_NeverSpanContinents()
	{
		var (g, p) = Make();
		for (int r = 0; r < g.Regions.Length; r++)
		{
			Assert.That(g.Regions[r].Landmass, Is.InRange(0, p.LandmassCount - 1));
			for (int i = 0; i < p.PlacementLand.Length; i++)
				if (g.RegionOfCell[i] == r)
					Assert.That(p.LandmassId[i], Is.EqualTo(g.Regions[r].Landmass),
						$"区域 {r} 含他陆块格——两级归属嵌套被破坏");
		}
	}

	[Test]
	public void RegionCount_AreaDriven_Bounded()
	{
		var (g, _) = Make();
		Assert.That(g.Regions.Length, Is.GreaterThanOrEqualTo(1));
		var perLandmass = new Dictionary<int, int>();
		foreach (var r in g.Regions)
			perLandmass[r.Landmass] = perLandmass.GetValueOrDefault(r.Landmass) + 1;
		foreach (var (c, k) in perLandmass)
		{
			Assert.That(k, Is.GreaterThanOrEqualTo(1), $"陆块 {c} 至少 1 区域");
			Assert.That(k, Is.LessThanOrEqualTo(GeologicalRegions.MaxRegionsPerContinent), $"陆块 {c} 区域数超上限");
		}
		// 面积口径：更大的目标面积 ⇒ 更少区域（数量旋钮的真实性）
		var (gCoarse, _) = Make(targetAreaKm2: 40_000_000f);
		Assert.That(gCoarse.Regions.Length, Is.LessThan(g.Regions.Length),
			"目标区域面积 ×5 ⇒ 区域数应显著减少（面积口径生效）");
	}

	[Test]
	public void Features_PopulatedWithPhysicalUnits()
	{
		var (g, _) = Make();
		foreach (var r in g.Regions)
		{
			Assert.That(r.AreaKm2, Is.GreaterThan(0f), $"区域 {r.Id} 面积须为正");
			Assert.That(r.LatitudeRad, Is.InRange(0f, MathF.PI / 2f));
			Assert.That(r.CoastDistanceKm, Is.GreaterThanOrEqualTo(0f));
			Assert.That(r.Continentality, Is.InRange(0f, 1f));
			Assert.That(r.Weight, Is.InRange(0.7f, 1.3f), "SizeNoise 权重须在标称值域内");
			Assert.That(r.Neighbors.Length, Is.GreaterThanOrEqualTo(0));
		}
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.g.RegionOfCell, b.g.RegionOfCell, "逐格区域号须逐位同");
		Assert.That(b.g.Regions.Length, Is.EqualTo(a.g.Regions.Length));
		for (int r = 0; r < a.g.Regions.Length; r++)
		{
			Assert.That(b.g.Regions[r].Type, Is.EqualTo(a.g.Regions[r].Type), $"区域 {r} 类型须同");
			Assert.That(b.g.Regions[r].Seed, Is.EqualTo(a.g.Regions[r].Seed), $"区域 {r} Lloyd 种子须逐位同");
			Assert.That(b.g.Regions[r].Weight, Is.EqualTo(a.g.Regions[r].Weight));
		}
	}

	[Test]
	public void Types_AreFromTheSevenKindTable_AndDiverse()
	{
		var (g, _) = Make();
		var distinct = new HashSet<RegionType>();
		foreach (var r in g.Regions)
		{
			Assert.That(Enum.IsDefined(r.Type), "类型必须来自七类表");
			distinct.Add(r.Type);
		}
		Assert.That(distinct.Count, Is.GreaterThanOrEqualTo(2), "Softmax + 配额须产生类型多样性（防全 Plain 假通过）");
	}

	[Test]
	public void Rift_HardConstraint_AdjacentToHighlandFamily()
	{
		var (g, _) = Make();
		var adj = BuildAdjacency(g);
		for (int r = 0; r < g.Regions.Length; r++)
		{
			if (g.Regions[r].Type != RegionType.Rift) continue;
			bool hasHighlandFamily = false;
			foreach (int a in adj[r])
				if (g.Regions[a].Type is RegionType.Highland or RegionType.Mountain or RegionType.Basin)
					hasHighlandFamily = true;
			Assert.That(hasHighlandFamily, Is.True, $"RIFT 区域 {r} 必须邻接高地系（硬约束）");
		}
	}

	[Test]
	public void CoastalRegions_AreTheNearSeaOnes()
	{
		var (g, _) = Make();
		var coastal = new List<float>();
		var all = new List<float>();
		foreach (var r in g.Regions) { all.Add(r.CoastDistanceKm); if (r.Type == RegionType.Coastal) coastal.Add(r.CoastDistanceKm); }
		if (coastal.Count == 0) return;   // 本种子无 COASTAL（Softmax 概率语义，非必然）
		all.Sort();
		float median = all[all.Count / 2];
		foreach (float d in coastal)
			Assert.That(d, Is.LessThanOrEqualTo(median + 1f), "COASTAL 区域平均离海须不高于全体中位（沿岸约束生效）");
	}

	// 邻接表辅助（不是测试：此处曾遗留一个孤立的 [Test]，NUnit 会把私有辅助当测试报
	// "Method is not public"——已移除；新增测试时勿在辅助方法前留悬空特性）
	Dictionary<int, HashSet<int>> BuildAdjacency(GeologicalRegions g)
	{
		var neighbors = Ball.CellNeighbors;
		var adj = new Dictionary<int, HashSet<int>>();
		for (int i = 0; i < g.RegionOfCell.Length; i++)
		{
			int ri = g.RegionOfCell[i];
			if (ri < 0) continue;
			if (!adj.ContainsKey(ri)) adj[ri] = new HashSet<int>();
			foreach (int j in neighbors[i])
			{
				int rj = g.RegionOfCell[j];
				if (rj < 0 || rj == ri) continue;
				adj[ri].Add(rj);
				if (!adj.ContainsKey(rj)) adj[rj] = new HashSet<int>();
				adj[rj].Add(ri);
			}
		}
		return adj;
	}
}
