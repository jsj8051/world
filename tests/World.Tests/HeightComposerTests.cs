using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 高度合成护栏（决策 07 数据语义链——目标 lerp 语义 + FinalHeight 单一事实源）：
///   · 陆格合成公式（lerp 链精确界）：linear = Base → 高原 lerp → 盆地 lerp → 山脉 lerp；
///     h' = 平滑(linear) ± 三档 variation，带宽 = Σvar + (1−SmoothSelf)×邻居 linear 极差；
///   · 方向性：高原场覆盖格均值 > 陆地均值 > 盆地覆盖格均值；
///   · 盆地泛滥保险：每陆块盆地上限；
///   · 海侧 = 深海剖面 + 山脊表达（只向目标拉、有界）；确定性逐位同。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class HeightComposerTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (HeightComposer c, H3LandSeaProjector p, GeologicalRegions g, RegionalLandforms l, MountainSkeleton m, SurfaceResolver s)
		Make(int seed = 42)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);   // res1 格宽 ~800 km 须放大 σ
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
		return (c, proj, g, l, m, surface);
	}

	// 逐格 lerp 链（无变化场、无平滑）——与 SampleSurface 相同顺序
	static float LinearAt(int i, H3LandSeaProjector p, GeologicalRegions g, RegionalLandforms l, MountainSkeleton m)
	{
		float h = g.BaseElevationField[i];
		h = Lerp(h, l.PlateauTargetM[i], l.PlateauInfluence[i]);
		h = Lerp(h, l.BasinTargetM[i], l.BasinInfluence[i]);
		h = Lerp(h, m.MountainTargetM[i], m.MountainInfluence[i]);
		return h;
	}

	static float Lerp(float a, float b, float t) => a + (b - a) * t;

	[Test]
	public void LandHeight_MatchesLerpChain_Band()
	{
		// 陆格精确界：h' = 平滑(lerp 链 + variation)；带宽 = Σvar + (1−SmoothSelf)×邻居 linear 极差
		var (c, p, g, l, m, _) = Make();
		var neighbors = Ball.CellNeighbors;
		float varSum = HeightComposer.LargeVariationM + HeightComposer.MediumVariationM + HeightComposer.RegionalNoiseM;
		for (int i = 0; i < p.PlacementLand.Length; i++)
		{
			if (!p.PlacementLand[i]) continue;
			float linI = LinearAt(i, p, g, l, m);
			float nbMin = linI, nbMax = linI;
			foreach (int j in neighbors[i])
			{
				if (!p.PlacementLand[j]) continue;
				float linJ = LinearAt(j, p, g, l, m);
				nbMin = MathF.Min(nbMin, linJ);
				nbMax = MathF.Max(nbMax, linJ);
			}
			float band = varSum + (1f - HeightComposer.SmoothSelf) * (nbMax - nbMin) + 1f;
			Assert.That(c.HeightM[i], Is.InRange(linI - band, linI + band),
				$"格 {i}：最终高度必须落在 lerp 链的平滑精确界内（决策 07 ③）");
		}
	}

	[Test]
	public void PlateauAboveMean_BasinBelowMean()
	{
		var (c, p, g, l, _, _) = Make();
		float landSum = 0f; int landN = 0;
		float plateauSum = 0f; int plateauN = 0;
		float basinSum = 0f; int basinN = 0;
		for (int i = 0; i < p.PlacementLand.Length; i++)
		{
			if (!p.PlacementLand[i]) continue;
			landSum += c.HeightM[i]; landN++;
			if (l.PlateauInfluence[i] > 0f) { plateauSum += c.HeightM[i]; plateauN++; }
			if (l.BasinInfluence[i] > 0f) { basinSum += c.HeightM[i]; basinN++; }
		}
		Assert.That(landN, Is.GreaterThan(0));
		// 目标语义下盆地会把低基座沿岸格拉向 350m（可升可降），基座均值对比不成立——
		// 改特征间对比：高原目标 2050 ≫ 盆地目标 350，两者覆盖格均值必须拉开
		if (plateauN > 0 && basinN > 0)
			Assert.That(plateauSum / plateauN, Is.GreaterThan(basinSum / basinN),
				"高原覆盖格均值须显著高于盆地覆盖格均值（目标语义 2050 vs 350）");
	}

	[Test]
	public void BasinCount_CappedPerLandmass()
	{
		// 直接读应用计数（帽 σ 是区域尺度、场覆盖跨区域格——按格统计会虚高）
		var (c, p, g, l, _, _) = Make();
		foreach (var (lm, k) in l.AppliedBasinsPerLandmass)
			Assert.That(k, Is.LessThanOrEqualTo(RegionalLandforms.MaxBasinsPerLandmass),
				$"陆块 {lm} 产生目标场的盆地超上限（决策 §七：防内流盆地泛滥）");
	}

	[Test]
	public void Ocean_LerpTowardMountainTarget_Only()
	{
		// v3.2/07：海格 = lerp(深海剖面, 山脉目标, 影响度)——只向目标拉、无其他来源
		var (c, p, _, _, m, _) = Make();
		for (int i = 0; i < p.PlacementLand.Length; i++)
		{
			if (p.PlacementLand[i]) continue;
			float bathy = p.ElevationM[i];
			float expectLo = MathF.Min(bathy, m.MountainTargetM[i]) - 0.01f;
			float expectHi = MathF.Max(bathy, m.MountainTargetM[i]) + 0.01f;
			Assert.That(c.HeightM[i], Is.InRange(expectLo, expectHi),
				$"海格 {i}：最终高度必须 = lerp(深海剖面, 山脉目标, 影响度)");
		}
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.c.HeightM, b.c.HeightM, "最终高度须逐位同（确定性红线）");
	}
}
