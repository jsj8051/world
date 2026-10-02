using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 高度合成护栏（阶段 5+6，决策 05）：
///   · 合成公式（§六）：陆格 Height ∈ 各层代数和 ± regionalNoise 值域带（1 pass 平滑的容差）；
///   · 方向性：PLATEAU 区域内部均值 > 陆地均值 > BASIN 区域内部均值（高原抬升/盆地下挖生效）；
///   · 盆地泛滥保险（§七）：每陆块盆地上限 MaxBasinsPerLandmass；
///   · 海格 = 投影原样（陆上合成不污染海洋）；确定性逐位同。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class HeightComposerTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (HeightComposer c, H3LandSeaProjector p, GeologicalRegions g, RegionalLandforms l, MountainSkeleton m)
		Make(int seed = 42)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);   // res1 格宽 ~800 km 须放大 σ
		m.Generate(Ball, g);
		var l = new RegionalLandforms(seed);
		l.Generate(Ball, g);
		var c = new HeightComposer(seed);
		c.Generate(Ball, proj, g, m, l);
		return (c, proj, g, l, m);
	}

	[Test]
	public void Height_MatchesCompositeFormula_Band()
	{
		// 陆格精确界：h' = 0.6·代数和 + 0.4·邻居代数和均值（± regionalNoise）
		// ⇒ 带宽 = noise ± (1−SmoothSelf) × 邻居代数和极差（盆边/山边邻居差数百米——逐格算）
		var (c, p, g, l, m) = Make();
		var neighbors = Ball.CellNeighbors;
		var linear = new float[p.Land.Length];
		for (int i = 0; i < p.Land.Length; i++)
			linear[i] = p.ElevationM[i] + m.ElevationAddM[i] + l.PlateauAddM[i] - l.BasinDipM[i];

		for (int i = 0; i < p.Land.Length; i++)
		{
			if (!p.Land[i]) continue;
			float nbMin = linear[i], nbMax = linear[i];
			foreach (int j in neighbors[i])
			{
				if (!p.Land[j]) continue;
				nbMin = MathF.Min(nbMin, linear[j]);
				nbMax = MathF.Max(nbMax, linear[j]);
			}
			float band = HeightComposer.RegionalNoiseM + (1f - HeightComposer.SmoothSelf) * (nbMax - nbMin) + 1f;
			Assert.That(c.HeightM[i], Is.InRange(linear[i] - band, linear[i] + band),
				$"格 {i}：合成海拔必须落在决策 05 §六公式的平滑精确界内");
		}
	}

	[Test]
	public void PlateauAboveMean_BasinBelowMean()
	{
		var (c, p, g, l, _) = Make();
		float landSum = 0f; int landN = 0;
		float plateauSum = 0f; int plateauN = 0;
		float basinSum = 0f; int basinN = 0;
		for (int i = 0; i < p.Land.Length; i++)
		{
			if (!p.Land[i]) continue;
			landSum += c.HeightM[i]; landN++;
			int r = g.RegionOfCell[i];
			if (r < 0) continue;
			var t = g.Regions[r].Type;
			if (t == RegionType.Plateau && l.PlateauAddM[i] > 0f) { plateauSum += c.HeightM[i]; plateauN++; }
			if (t == RegionType.Basin && l.BasinDipM[i] > 0f) { basinSum += c.HeightM[i]; basinN++; }
		}
		Assert.That(landN, Is.GreaterThan(0));
		float mean = landSum / landN;
		if (plateauN > 0)
			Assert.That(plateauSum / plateauN, Is.GreaterThan(mean),
				"高原场覆盖格的平均海拔须高于陆地均值（台地抬升生效）");
		if (basinN > 0)
			Assert.That(basinSum / basinN, Is.LessThan(mean),
				"盆地下挖覆盖格的平均海拔须低于陆地均值（洼地下挖生效）");
	}

	[Test]
	public void BasinCount_CappedPerLandmass()
	{
		// 直接读应用计数（帽 σ 是区域尺度、场覆盖跨区域格——按格统计会虚高）
		var (c, p, g, l, _) = Make();
		foreach (var (lm, k) in l.AppliedBasinsPerLandmass)
			Assert.That(k, Is.LessThanOrEqualTo(RegionalLandforms.MaxBasinsPerLandmass),
				$"陆块 {lm} 产生下挖场的盆地超上限（决策 §七：防内流盆地泛滥）");
	}

	[Test]
	public void Ocean_KeepsProjectionElevation()
	{
		var (c, p, _, _, _) = Make();
		for (int i = 0; i < p.Land.Length; i++)
			if (!p.Land[i])
				Assert.That(c.HeightM[i], Is.EqualTo(p.ElevationM[i]),
					$"海格 {i}：陆上合成不得污染海洋深度剖面");
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.c.HeightM, b.c.HeightM, "合成海拔须逐位同（确定性红线）");
	}
}
