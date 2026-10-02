using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 地质区域护栏（阶段 3，决策 03）：
///   · 分区：陆格全覆盖、海格 −1；区域不跨大陆（嵌套性——两级 Voronoi 的结构保证）；
///     区域数 = 各大陆面积定（≥min/大陆，≤max/大陆）；确定性逐位同。
///   · 类型：七类约束式分配（非纯随机）——COASTAL 区域平均离海 ≤ 全体中位（沿岸确实是近海者）；
///     MOUNTAIN 存在时必有高地系邻居（邻接调制）；HIGHLAND/MOUNTAIN 合成海拔均值 > PLAIN（区域调制上画面）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class GeologicalRegionsTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格（链路验证档）
	static Ball Ball => SharedBall.Value;

	// res1 842 格太小（~245 陆格），区域粒度按比例缩小 ⇒ 每大陆 2 区域可成立
	static (GeologicalRegions g, H3LandSeaProjector p) Make(int seed = 42, float cellsPerRegion = 120f)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, cellsPerRegion);
		return (g, proj);
	}

	[Test]
	public void RegionOfCell_LandFullyCovered_OceanNegativeOne()
	{
		var (g, p) = Make();
		Assert.That(g.RegionOfCell.Length, Is.EqualTo(p.Land.Length));
		for (int i = 0; i < p.Land.Length; i++)
		{
			if (p.Land[i]) Assert.That(g.RegionOfCell[i], Is.InRange(0, g.Regions.Length - 1), $"陆格 {i} 须有区域");
			else Assert.That(g.RegionOfCell[i], Is.EqualTo(-1), $"海格 {i} 须为 −1");
		}
	}

	[Test]
	public void Regions_NeverSpanContinents()
	{
		var (g, p) = Make();
		for (int r = 0; r < g.Regions.Length; r++)
		{
			Assert.That(g.Regions[r].Continent, Is.InRange(0, p.ContinentCount - 1));
			// 嵌套性直接验证：区域内格的大陆号 == 区域登记大陆号
			for (int i = 0; i < p.Land.Length; i++)
				if (g.RegionOfCell[i] == r)
					Assert.That(p.ContinentId[i], Is.EqualTo(g.Regions[r].Continent),
						$"区域 {r} 含他大陆格——两级 Voronoi 嵌套被破坏");
		}
	}

	[Test]
	public void RegionCount_PerContinentBounded_AndNonEmpty()
	{
		var (g, _) = Make();
		Assert.That(g.Regions.Length, Is.GreaterThanOrEqualTo(1), "至少一个区域");
		var perContinent = new Dictionary<int, int>();
		foreach (var r in g.Regions)
			perContinent[r.Continent] = perContinent.GetValueOrDefault(r.Continent) + 1;
		foreach (var (c, k) in perContinent)
		{
			Assert.That(k, Is.GreaterThanOrEqualTo(1), $"大陆 {c} 至少 1 区域");
			Assert.That(k, Is.LessThanOrEqualTo(GeologicalRegions.MaxRegionsPerContinent),
				$"大陆 {c} 区域数超上限");
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
			Assert.That(b.g.Regions[r].Centroid, Is.EqualTo(a.g.Regions[r].Centroid));
		}
		CollectionAssert.AreEqual(a.g.ElevationM, b.g.ElevationM, "合成海拔须逐位同");
	}

	[Test]
	public void Types_AreFromTheSevenKindTable()
	{
		var (g, _) = Make();
		foreach (var r in g.Regions)
			Assert.That(Enum.IsDefined(r.Type), "类型必须来自七类表");
		// 小球 + 小粒度下应出现 ≥2 种类型（约束式打分有区分度，防全 Plain 的假通过）
		var distinct = new HashSet<RegionType>();
		foreach (var r in g.Regions) distinct.Add(r.Type);
		Assert.That(distinct.Count, Is.GreaterThanOrEqualTo(2), "类型应有多样性（随机权重 + 约束）");
	}

	[Test]
	public void CoastalRegions_AreTheNearSeaOnes()
	{
		var (g, _) = Make();
		var coastal = new List<float>();
		var all = new List<float>();
		foreach (var r in g.Regions) { all.Add(r.AvgDistToCoast); if (r.Type == RegionType.Coastal) coastal.Add(r.AvgDistToCoast); }
		if (coastal.Count == 0) return;   // 本种子无 COASTAL 区域（打分决定，非必然）——沿岸约束未被触发，无可校验项
		all.Sort();
		float median = all[all.Count / 2];
		foreach (float d in coastal)
			Assert.That(d, Is.LessThanOrEqualTo(median + 1f),
				"COASTAL 区域平均离海须不高于全体中位（沿岸约束生效）");
	}

	[Test]
	public void Mountains_AdjacentToHighlandFamily_WhenPresent()
	{
		var (g, p) = Make();
		// 邻接表
		var neighbors = Ball.CellNeighbors;
		var adj = new HashSet<int>[g.Regions.Length];
		for (int r = 0; r < adj.Length; r++) adj[r] = new HashSet<int>();
		for (int i = 0; i < p.Land.Length; i++)
		{
			int ri = g.RegionOfCell[i];
			if (ri < 0) continue;
			foreach (int j in neighbors[i])
			{
				int rj = g.RegionOfCell[j];
				if (rj >= 0 && rj != ri) { adj[ri].Add(rj); adj[rj].Add(ri); }
			}
		}
		for (int r = 0; r < g.Regions.Length; r++)
		{
			if (g.Regions[r].Type != RegionType.Mountain) continue;
			bool hasHighlandFamily = false;
			foreach (int a in adj[r])
				if (g.Regions[a].Type is RegionType.Highland or RegionType.Plateau or RegionType.Mountain)
					hasHighlandFamily = true;
			Assert.That(hasHighlandFamily, Is.True,
				"MOUNTAIN 区域须邻接高地系（邻接调制的语义）");
		}
	}

	[Test]
	public void Elevation_Composite_Viewable_ByRegionType()
	{
		var (g, p) = Make();
		for (int i = 0; i < p.Land.Length; i++)
		{
			if (!p.Land[i]) continue;
			float expect = p.ElevationM[i] + GeologicalRegions.TypeElevOffsetM[(int)g.Regions[g.RegionOfCell[i]].Type];
			Assert.That(g.ElevationM[i], Is.EqualTo(expect).Within(0.5f),
				$"格 {i}：合成海拔 = 投影海拔 + 区域偏移");
		}
		// 高地系区域的海拔均值 > 平原系（区域感上画面的可观察量）
		float highSum = 0f, plainSum = 0f;
		int highN = 0, plainN = 0;
		for (int i = 0; i < p.Land.Length; i++)
		{
			if (!p.Land[i]) continue;
			var t = g.Regions[g.RegionOfCell[i]].Type;
			if (t is RegionType.Highland or RegionType.Mountain or RegionType.Plateau) { highSum += g.ElevationM[i]; highN++; }
			else if (t is RegionType.Plain or RegionType.Coastal) { plainSum += g.ElevationM[i]; plainN++; }
		}
		if (highN > 0 && plainN > 0)
			Assert.That(highSum / highN, Is.GreaterThan(plainSum / plainN),
				"高地系区域平均海拔须高于平原系（区域调制可见）");
	}
}
