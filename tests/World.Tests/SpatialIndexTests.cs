using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · Final 空间索引护栏（#13，决策 08 v2）：
///   · 语义层：索引结果与暴力遍历一致（最近陆块/区域/河流/山锚对照）；
///   · API 层：nearest/distance/within 行为（陆格自身 0 距、海格最近陆、河流 within）；
///   · 确定性逐位同。
/// 架构契约（禁引用 Placement/Feature 生成器）在 ArchitectureContractTests。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class SpatialIndexTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (FinalSpatialIndex idx, FinalGeography f, MountainSkeleton m) Make(int seed = 42, bool withRiver = true)
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
		var riverCells = new List<int>();
		if (withRiver)
		{
			var r = new RiverNetwork();
			r.Generate(Ball, f, c, riverThresholdCells: 12);
			for (int i = 0; i < r.IsRiver.Length; i++) if (r.IsRiver[i]) riverCells.Add(i);
		}
		var mAnchors = new List<Vector3>();
		foreach (var sys in m.Systems) mAnchors.Add(sys.Anchor);
		var idx = new FinalSpatialIndex(Ball, mAnchors, Array.Empty<Vector3>());
		idx.Generate(f, riverCells);
		return (idx, f, m);
	}

	[Test]
	public void NearestLandmass_LandIsSelf_SeaIsNearest()
	{
		var (idx, f, _) = Make();
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (f.FinalLand[i])
			{
				Assert.That(idx.NearestLandmassId[i], Is.EqualTo(f.FinalLandmassId[i]), $"陆格 {i}：最近陆块 = 自身");
				Assert.That(idx.NearestLandmassDistHops[i], Is.EqualTo(0), $"陆格 {i}：距自身陆块 0 跳");
			}
			else
			{
				Assert.That(idx.NearestLandmassId[i], Is.GreaterThanOrEqualTo(0), $"海格 {i}：须有最近陆块");
				Assert.That(idx.NearestLandmassDistHops[i], Is.GreaterThanOrEqualTo(1), $"海格 {i}：距最近陆块 ≥1 跳");
			}
		}
	}

	[Test]
	public void NearestLandmass_MatchesBruteForce()
	{
		// 验收 ①：索引结果 = 暴力遍历（对每海格，各陆块最小跳数的 argmin 一致）
		var (idx, f, _) = Make();
		var neighbors = Ball.CellNeighbors;
		int n = f.FinalLand.Length;

		// 暴力：对每陆块做独立 BFS 得最小跳数（抽样格对照）
		var sample = new List<int>();
		for (int i = 0; i < n; i += 17) sample.Add(i);

		foreach (int i in sample)
		{
			if (f.FinalLand[i]) continue;   // 陆格自身已精确测过
			int bestLm = -1, bestDist = int.MaxValue;
			foreach (var (lm, _) in EnumerateLandmasses(f))
			{
				int d = BruteForceHopDistance(Ball, n, i, cell => f.FinalLand[cell] && f.FinalLandmassId[cell] == lm);
				if (d >= 0 && d < bestDist) { bestDist = d; bestLm = lm; }
			}
			Assert.That(idx.NearestLandmassId[i], Is.EqualTo(bestLm),
				$"海格 {i}：索引最近陆块须与暴力遍历一致");
			Assert.That(idx.NearestLandmassDistHops[i], Is.EqualTo(bestDist),
				$"海格 {i}：索引距离须与暴力遍历一致");
		}
		Assert.That(sample.Count, Is.GreaterThan(0));
	}

	[Test]
	public void NearestRiver_Behavior()
	{
		var (idx, _, _) = Make(withRiver: true);
		int riverCell = -1;
		for (int i = 0; i < idx.NearestRiverCell.Length; i++)
			if (idx.NearestRiverCell[i] == i) { riverCell = i; break; }
		Assert.That(riverCell, Is.GreaterThanOrEqualTo(0), "须存在河流格");
		// 河流格自身：距离 0、within 成立
		var dir = Ball.CellDirs[riverCell];
		Assert.That(idx.NearestRiver(dir), Is.EqualTo(riverCell));
		Assert.That(idx.DistanceToNearestRiver(dir), Is.EqualTo(0));
		Assert.That(idx.WithinRiver(dir, 0), Is.True);
	}

	[Test]
	public void NoRiver_WorldReturnsMinusOne()
	{
		var (idx, _, _) = Make(withRiver: false);
		foreach (int d in idx.NearestRiverDistHops) Assert.That(d, Is.EqualTo(-1), "无河流世界：距离场恒 −1");
		var q = Vector3.Up;
		Assert.That(idx.NearestRiver(q), Is.EqualTo(-1));
		Assert.That(idx.WithinRiver(q, 5), Is.False);
	}

	[Test]
	public void NearestMountain_MatchesBruteForce()
	{
		var (idx, _, m) = Make();
		if (m.Systems.Count == 0) return;
		var anchors = new List<Vector3>();
		foreach (var sys in m.Systems) anchors.Add(sys.Anchor);
		var dirs = Ball.CellDirs;
		for (int i = 0; i < dirs.Length; i += 37)
		{
			var hit = idx.NearestMountain(dirs[i]);
			Assert.That(hit, Is.Not.Null);
			float best = float.PositiveInfinity;
			Vector3 bestA = default;
			foreach (var a in anchors)
			{
				float d = MathF.Acos(Math.Clamp(dirs[i].Dot(a), -1f, 1f));
				if (d < best) { best = d; bestA = a; }
			}
			Assert.That(hit.Value.anchor, Is.EqualTo(bestA), $"格 {i}：最近山锚须与暴力一致");
			Assert.That(hit.Value.distRad, Is.EqualTo(best).Within(1e-4f));
		}
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.idx.NearestLandmassId, b.idx.NearestLandmassId);
		CollectionAssert.AreEqual(a.idx.NearestLandmassDistHops, b.idx.NearestLandmassDistHops);
		CollectionAssert.AreEqual(a.idx.NearestRegionId, b.idx.NearestRegionId);
		CollectionAssert.AreEqual(a.idx.NearestRiverCell, b.idx.NearestRiverCell);
	}

	static System.Collections.Generic.IEnumerable<(int lm, int cells)> EnumerateLandmasses(FinalGeography f)
	{
		var seen = new HashSet<int>();
		for (int i = 0; i < f.FinalLand.Length; i++)
			if (f.FinalLand[i] && seen.Add(f.FinalLandmassId[i]))
				yield return (f.FinalLandmassId[i], 0);
	}

	// 暴力单源 BFS（源 = 谓词命中的全部格；返回目标格的跳数，不可达 = −1）
	static int BruteForceHopDistance(Ball ball, int n, int target, Func<int, bool> source)
	{
		var dist = new int[n];
		Array.Fill(dist, -1);
		var q = new Queue<int>();
		for (int i = 0; i < n; i++)
			if (source(i)) { dist[i] = 0; q.Enqueue(i); }
		var nbrs = ball.CellNeighbors;
		while (q.Count > 0)
		{
			int i = q.Dequeue();
			if (i == target) return dist[i];
			foreach (int j in nbrs[i])
				if (dist[j] < 0) { dist[j] = dist[i] + 1; q.Enqueue(j); }
		}
		return -1;
	}
}
