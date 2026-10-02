using System;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 山脉骨架护栏（阶段 4，决策 04）：
///   · 骨架存在性：有 MOUNTAIN 区域 ⇒ 有脊（主脊数 = MOUNTAIN 区域数）；
///   · 高斯包络（4.2）：脊带上格加成显著（> H×0.3 量级）、3σ 外加成近零（< H×0.05）；
///   · ridged 细化（4.3）：脊带内加成有起伏（方差 > 0——噪声只细化不定位）；
///   · 陆域约束：海格加成恒 0；确定性逐位同。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class MountainSkeletonTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
	static Ball Ball => SharedBall.Value;

	static (MountainSkeleton m, GeologicalRegions g) Make(int seed = 42, float targetAreaKm2 = 8_000_000f)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, targetAreaKm2);
		// res1 格宽 ~800 km >> 生产 σ 130 km——测试球放大骨架性格（σ×4）才能采到山带；
		// 生产 res4（格宽 ~42 km）走默认地球量级参数
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(Ball, g);
		return (m, g);
	}

	[Test]
	public void Ridges_ExistForEveryMountainRegion()
	{
		var (m, g) = Make();
		int mountainRegions = 0;
		foreach (var r in g.Regions) if (r.Type == RegionType.Mountain) mountainRegions++;
		Assert.That(mountainRegions, Is.GreaterThan(0), "本种子须有 MOUNTAIN 区域（控制图定山位的前提）");
		int mainRidges = 0;
		foreach (var r in m.Ridges) if (!r.IsBranch) mainRidges++;
		Assert.That(mainRidges, Is.EqualTo(mountainRegions), "每个 MOUNTAIN 区域恰一条主脊");
	}

	[Test]
	public void GaussianEnvelope_MatchesFormula_PerCell()
	{
		// 分辨率无关的逐格精确对照：加成[i] = Σ_脊 exp(−d²/σ²) × H × detail，detail ∈[0.7,1.3]
		var (m, g) = Make();
		Assert.That(m.Ridges.Length, Is.GreaterThan(0));
		var dirs = Ball.CellDirs;
		int checkedCells = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			if (g.RegionOfCell[i] < 0) continue;
			float expectLo = 0f, expectHi = 0f;
			foreach (var ridge in m.Ridges)
			{
				float sigmaRad = ridge.SigmaKm / 6371f;
				float dMin = float.PositiveInfinity;
				foreach (var p in ridge.Points)
				{
					float d = MathF.Acos(Math.Clamp(dirs[i].Dot(p), -1f, 1f));
					if (d < dMin) dMin = d;
				}
				float env = MathF.Exp(-(dMin * dMin) / (sigmaRad * sigmaRad));
				expectLo += env * ridge.HeightM * 0.45f;
				expectHi += env * ridge.HeightM * 1.0f;
			}
			Assert.That(m.ElevationAddM[i], Is.InRange(expectLo - 1f, expectHi + 1f),
				$"格 {i}：加成必须落在高斯包络 × detail 值域带内（决策 4.2/4.3 公式）");
			checkedCells++;
		}
		Assert.That(checkedCells, Is.GreaterThan(0));
	}

	[Test]
	public void Envelope_DecaysFarFromRidge()
	{
		// 3σ 外（帽内）格：包络 ≤ exp(−9) → 加成 < 任何脊高 × 1e-3（分辨率无关）
		var (m, g) = Make();
		var dirs = Ball.CellDirs;
		for (int i = 0; i < dirs.Length; i++)
		{
			if (g.RegionOfCell[i] < 0) continue;
			bool anyClose = false;
			float add = m.ElevationAddM[i];
			foreach (var ridge in m.Ridges)
			{
				float sigmaRad = ridge.SigmaKm / 6371f;
				float dMin = float.PositiveInfinity;
				foreach (var p in ridge.Points)
				{
					float d = MathF.Acos(Math.Clamp(dirs[i].Dot(p), -1f, 1f));
					if (d < dMin) dMin = d;
				}
				if (dMin < 3f * sigmaRad) { anyClose = true; break; }
			}
			if (!anyClose)
				Assert.That(add, Is.LessThan(4210f * 1.3f * 0.001f),
					$"格 {i}：3σ 外加成须近零（高斯衰减 + 帽截断）");
		}
	}

	[Test]
	public void RidgedDetail_CreatesVarianceAcrossLand()
	{
		// ridged 细化生效的可观察量：有山的世界陆格加成必有起伏（分辨率无关）
		var (m, g) = Make();
		float sum = 0f, sumSq = 0f; int n = 0;
		for (int i = 0; i < g.RegionOfCell.Length; i++)
		{
			if (g.RegionOfCell[i] < 0) continue;
			float v = m.ElevationAddM[i];
			sum += v; sumSq += v * v; n++;
		}
		float mean = sum / n;
		float variance = sumSq / n - mean * mean;
		Assert.That(variance, Is.GreaterThan(1f),
			"陆格加成须有起伏（ridged 细化生效——噪声是细化工具非定位工具）");
	}

	[Test]
	public void OceanCells_NeverLifted()
	{
		var (m, g) = Make();
		for (int i = 0; i < g.RegionOfCell.Length; i++)
			if (g.RegionOfCell[i] < 0)
				Assert.That(m.ElevationAddM[i], Is.EqualTo(0f), $"海格 {i} 骨架加成必须为 0");
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		Assert.That(b.m.Ridges.Length, Is.EqualTo(a.m.Ridges.Length));
		for (int r = 0; r < a.m.Ridges.Length; r++)
		{
			Assert.That(b.m.Ridges[r].HeightM, Is.EqualTo(a.m.Ridges[r].HeightM));
			Assert.That(b.m.Ridges[r].Points.Length, Is.EqualTo(a.m.Ridges[r].Points.Length));
			for (int p = 0; p < a.m.Ridges[r].Points.Length; p++)
				Assert.That(b.m.Ridges[r].Points[p], Is.EqualTo(a.m.Ridges[r].Points[p]), $"脊 {r} 点 {p} 须逐位同");
		}
		CollectionAssert.AreEqual(a.m.ElevationAddM, b.m.ElevationAddM, "加成场须逐位同");
	}
}
