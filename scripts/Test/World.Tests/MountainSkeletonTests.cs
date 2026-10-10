using System;
using NUnit.Framework;
using Godot;
using World.H3Grid;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 山脉骨架 v3.2 护栏（目标绝对高度语义，决策 07 步骤⑤）：
///   · System 数量由陆块尺度定（非 MOUNTAIN 区域数）；Range/Ridge 宽度层级；
///   · 主脊轴向峰-垭起伏（构造保证 ≥1.9×）；
///   · InfluenceAt：影响度 ∈[0,1]、沿距离单调衰减；目标高 ∈ 轴向高度包络；
///   · 确定性逐位同。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class MountainSkeletonTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
	static Ball Ball => SharedBall.Value;

	static (MountainSkeleton m, GeologicalRegions g, SurfaceResolver s) Make(int seed = 42, float targetAreaKm2 = 8_000_000f)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, seed, WorldPreset.Earth.LandSea);
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, targetAreaKm2);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		// res1 格宽 ~800 km >> 生产 σ 160 km——测试球放大骨架性格（σ×3.25）才能采到山带；
		// 生产 res4（格宽 ~42 km）走默认地球量级参数
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(Ball, g, surface);
		return (m, g, surface);
	}

	[Test]
	public void MountainSystems_ScaleWithLandmass_NotRegionCount()
	{
		// 决策 05v2 §一/§三：System 数量由大陆尺度定（k = clamp(√面积/2000 × jitter, 0, 4)），
		// 不再由 MOUNTAIN 区域数一对一决定——山脉是跨区域的全局结构。
		var (m, g, _) = Make();
		int mainRanges = 0;
		foreach (var r in m.Ridges) if (!r.IsBranch) mainRanges++;
		Assert.That(mainRanges, Is.GreaterThan(0), "有山世界须至少一条主 Range");
		Assert.That(mainRanges, Is.LessThanOrEqualTo(7 * 4 + 1),
			"主 Range 数 ≤ 每陆块上限 4（跨区域 System 语义，非逐区域短脊）");
		// 宽度层级纪律（决策 05v2 §二）：主 Range（山脉带）显著宽于支 Ridge（脊线）。
		float mainMinSigma = float.PositiveInfinity, branchMaxSigma = 0f;
		foreach (var r in m.Ridges)
		{
			if (r.IsBranch) branchMaxSigma = MathF.Max(branchMaxSigma, r.SigmaKm);
			else mainMinSigma = MathF.Min(mainMinSigma, r.SigmaKm);
		}
		Assert.That(mainMinSigma, Is.GreaterThanOrEqualTo(100f), "主 Range σ 须在山脉带量级（v3.1 σ 160×抖动 0.8 = 128）");
		if (branchMaxSigma > 0f)
			Assert.That(mainMinSigma, Is.GreaterThanOrEqualTo(branchMaxSigma * 1.15f),
				$"主 Range σ({mainMinSigma:F0}) 须显著宽于支脉 σ({branchMaxSigma:F0})（跨系最坏比 1.21 取整）");
	}

	[Test]
	public void MainRange_HasAxialPeaksAndCols()
	{
		// v3 核心：主脊沿轴必须有峰-垭起伏（宏观 profile 0.45~1.0 构造保证）——
		// 消灭"整条脊线同一海拔 → 连续雪带"的根因
		var (m, _, _) = Make();
		Assert.That(m.Ridges.Length, Is.GreaterThan(0));
		int checkedRanges = 0;
		foreach (var r in m.Ridges)
		{
			if (r.IsBranch) continue;
			float hMin = float.PositiveInfinity, hMax = 0f;
			foreach (float hp in r.PointHeightM) { hMin = MathF.Min(hMin, hp); hMax = MathF.Max(hMax, hp); }
			Assert.That(hMax / hMin, Is.GreaterThanOrEqualTo(1.9f),
				$"主脊沿轴目标高度起伏 {hMax / hMin:F2}× 不足——峰垭结构缺失");
			checkedRanges++;
		}
		Assert.That(checkedRanges, Is.GreaterThan(0));
	}

	[Test]
	public void Influence_InUnitRange_TargetWithinProfileEnvelope()
	{
		var (m, _, _) = Make();
		float tMin = float.PositiveInfinity, tMax = 0f;
		foreach (var r in m.Ridges)
			foreach (float hp in r.PointHeightM) { tMin = MathF.Min(tMin, hp); tMax = MathF.Max(tMax, hp); }
		var dirs = Ball.CellDirs;
		for (int i = 0; i < dirs.Length; i++)
		{
			var (inf, tgt) = m.InfluenceAt(dirs[i]);
			Assert.That(inf, Is.InRange(0f, 1f), $"格 {i}：影响度必须 ∈[0,1]（含海洋表达式）");
			if (inf > 0f)
				Assert.That(tgt, Is.InRange(tMin * 0.92f - 1f, tMax * 1.08f + 1f),
					$"格 {i}：目标高必须落在轴向高度 × detail 包络内（峰高单一来源）");
		}
	}

	[Test]
	public void Influence_FarFromAllRidges_DecaysToNearZero()
	{
		// 远场衰减：距**所有**脊点 ≥3σ 的格，影响度 < 0.2（近场的非单调来自邻近支脉
		// 并集——真实行为；远场衰减才是包络 + 海洋表达式的 invariant）
		var (m, _, _) = Make();
		float sigmaMaxRad = 0f;
		foreach (var r in m.Ridges) sigmaMaxRad = MathF.Max(sigmaMaxRad, r.SigmaKm / 6371f);
		float farRad = 3f * sigmaMaxRad;
		var dirs = Ball.CellDirs;
		int checkedFar = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			float dMin = float.PositiveInfinity;
			foreach (var ridge in m.Ridges)
				foreach (var p in ridge.Points)
					dMin = MathF.Min(dMin, MathF.Acos(Math.Clamp(dirs[i].Dot(p), -1f, 1f)));
			if (dMin < farRad) continue;
			Assert.That(m.InfluenceAt(dirs[i]).influence, Is.LessThan(0.2f),
				$"格 {i}：距所有脊点 ≥3σ 影响度须趋零（远场衰减）");
			checkedFar++;
		}
		Assert.That(checkedFar, Is.GreaterThan(0), "须存在远离全部山脊的格");
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		Assert.That(b.m.Ridges.Length, Is.EqualTo(a.m.Ridges.Length));
		for (int r = 0; r < a.m.Ridges.Length; r++)
		{
			Assert.That(b.m.Ridges[r].PointHeightM, Is.EqualTo(a.m.Ridges[r].PointHeightM), $"脊 {r} 轴向高度须逐位同");
			Assert.That(b.m.Ridges[r].Points.Length, Is.EqualTo(a.m.Ridges[r].Points.Length));
			for (int p = 0; p < a.m.Ridges[r].Points.Length; p++)
				Assert.That(b.m.Ridges[r].Points[p], Is.EqualTo(a.m.Ridges[r].Points[p]), $"脊 {r} 点 {p} 须逐位同");
		}
		CollectionAssert.AreEqual(a.m.MountainInfluence, b.m.MountainInfluence, "影响度场须逐位同");
		CollectionAssert.AreEqual(a.m.MountainTargetM, b.m.MountainTargetM, "目标场须逐位同");
	}
}
