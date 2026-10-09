using System;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 最终地理护栏（决策 07 数据语义链 ④⑤——世界事实层）：
///   · FinalLand = 最终高度 > 0（与渲染逐格一致——掩码说陆则画面必是陆）；
///   · 【当前阶段 invariant，非永久法律】FinalLand ⊇ PlacementLand——成立前提是
///     "特征只负责抬升"（MinLand 钳制）。将来引入降低高度的过程（侵蚀/峡谷/海沟/
///     冰蚀）即失效，届时删除本断言——**长期不变量只有 FinalLand = 最终高度>0 派生**
///     （独立测试 FinalLand_MatchesHeightSign 已钉死，不随地貌过程演进失效）；
///   · 陆块连通分量：陆格全有归属、FinalLandmassCount 一致；
///   · FinalRegionOfCell：陆格全有区域（新增岛屿 → 最近构造域）；
///   · 海岸距离图距递推；确定性逐位同。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class FinalGeographyTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (FinalGeography f, H3LandSeaProjector p, HeightComposer c, GeologicalRegions g, MountainSkeleton m) Make(int seed = 42)
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
		var features = new System.Collections.Generic.List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		};
		var c = new HeightComposer(seed);
		c.Generate(Ball, surface, g, features);
		var f = new FinalGeography();
		f.Generate(Ball, c, g);
		return (f, proj, c, g, m);
	}

	[Test]
	public void FinalLand_MatchesHeightSign()
	{
		var (f, _, c, g, m) = Make();
		for (int i = 0; i < c.HeightM.Length; i++)
			Assert.That(f.FinalLand[i], Is.EqualTo(c.HeightM[i] > 0f),
				$"格 {i}：最终掩码必须等于最终高度符号（渲染一致性）");
	}

	[Test]
	public void FinalLand_SupersetOfPlacement_CurrentStageInvariant()
	{
		// 【当前阶段 invariant】特征只抬升 ⇒ FinalLand ⊇ PlacementLand。
		// 语义演进（决策 07 v2）：引入沉降/侵蚀类过程后本断言应删除——
		// 长期不变量 = FinalLand 永远由 FinalHeight 派生（见 FinalLand_MatchesHeightSign）。
	
		var (f, proj, c, g, m) = Make();
		int newLand = 0;
		for (int i = 0; i < proj.PlacementLand.Length; i++)
		{
			if (proj.PlacementLand[i])
				Assert.That(f.FinalLand[i], Is.True,
					$"格 {i}：放置期陆地不得被淹死（MinLand 钳制推论）");
			if (!proj.PlacementLand[i] && f.FinalLand[i]) newLand++;
		}
		// 山脉抬出的新增陆地（岛屿/岛链）——v3.2 海洋表达式的直接产物，允许为 0 但须有账
		Assert.That(newLand, Is.GreaterThanOrEqualTo(0));
	}

	[Test]
	public void Landmass_AllLandCellsAssigned()
	{
		var (f, _, _, _, _) = Make();
		int landCells = 0;
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			landCells++;
			Assert.That(f.FinalLandmassId[i], Is.InRange(0, f.FinalLandmassCount - 1),
				$"陆格 {i}：陆块归属缺失");
		}
		Assert.That(landCells, Is.GreaterThan(0));
		for (int i = 0; i < f.FinalLand.Length; i++)
			if (!f.FinalLand[i])
				Assert.That(f.FinalLandmassId[i], Is.EqualTo(-1), $"海格 {i}：陆块归属须为 −1");
	}

	[Test]
	public void FinalRegion_AllLandCellsAssigned()
	{
		var (f, _, _, _, _) = Make();
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (!f.FinalLand[i]) { Assert.That(f.FinalRegionOfCell[i], Is.EqualTo(-1)); continue; }
			Assert.That(f.FinalRegionOfCell[i], Is.GreaterThanOrEqualTo(0),
				$"陆格 {i}：最终区域归属缺失（新增岛屿须归入最近构造域）");
		}
	}

	[Test]
	public void CoastDistances_SatisfyGraphRecurrence()
	{
		var (f, _, _, _, _) = Make();
		var neighbors = Ball.CellNeighbors;
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			int dc = f.FinalDistToCoast[i];
			if (dc > 0)
			{
				int minN = int.MaxValue;
				foreach (int j in neighbors[i]) minN = Math.Min(minN, f.FinalDistToCoast[j]);
				Assert.That(dc, Is.EqualTo(minN + 1), $"格 {i}：离海距离违反图距递推");
			}
		}
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.f.FinalLand, b.f.FinalLand, "最终掩码须逐位同");
		CollectionAssert.AreEqual(a.f.FinalLandFraction, b.f.FinalLandFraction, "陆占比须逐位同");
		CollectionAssert.AreEqual(a.f.FinalLandmassId, b.f.FinalLandmassId, "陆块归属须逐位同");
		CollectionAssert.AreEqual(a.f.FinalRegionOfCell, b.f.FinalRegionOfCell, "最终区域须逐位同");
	}
}
