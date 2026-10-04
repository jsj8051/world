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
/// ★永久验收原则（#13 BFS label bug 沉淀，决策 08 v2 §3.6）：**所有 nearest 查询必须同时
///   验证"距离正确"与"对象身份（label）正确"**——BFS 结构对而 label 错时 distance 全对、
///   "是谁"失效（实测河流 label 恒 0 ⇒ NearestRiver 恒返回格 0）。本文件四类事实各自钉死：
///   陆块（dist+id）/ 河流（dist+cell）/ 山锚（distRad+anchor）/ 区域（id 处于最小图距）。
/// 架构契约（禁引用 Placement/Feature 生成器、River 不自依赖索引）在 ArchitectureContractTests。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class SpatialIndexTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	/// <summary>构造一个完整 Final 世界 + 索引。返回第 4 项 = 河流格真值（身份验收的独立基准，
	/// 不从索引自身反推——否则 label 验收会退化成自证）。</summary>
	static (FinalSpatialIndex idx, FinalGeography f, MountainSkeleton m, List<int> riverCells)
		Make(int seed = 42, bool withRiver = true)
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
		return (idx, f, m, riverCells);
	}

	[Test]
	public void NearestLandmass_LandIsSelf_SeaIsNearest()
	{
		var (idx, f, _, _) = Make();
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
		var (idx, f, _, _) = Make();
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
		var (idx, _, _, _) = Make(withRiver: true);
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

	/// <summary>
	/// ★永久验收原则的核心钉子（#13 BFS label bug）：**距离 + 身份双验**。
	/// 历史上 label 恒 0 时全部 dist 仍正确、NearestRiver 恒返回格 0——只测距离抓不到。
	/// 本例以 RiverNetwork 输出的河流格真值为独立基准（不从索引反推），逐格对照：
	/// ① dist = 该格到最近河流格的最小跳数；② label 是真实河流格；③ label 自身处于该最小跳数。
	/// </summary>
	[Test]
	public void NearestRiver_MatchesBruteForce_IdentityAndDistance()
	{
		var (idx, f, _, riverCells) = Make(withRiver: true);
		int n = f.FinalLand.Length;
		Assert.That(riverCells.Count, Is.GreaterThan(0), "须存在河流格");
		var riverSet = new HashSet<int>(riverCells);

		int checkedCells = 0;
		for (int i = 0; i < n; i += 7)
		{
			var dist = BfsFrom(Ball, n, i);
			int dMin = int.MaxValue;
			foreach (int rc in riverCells)
				if (dist[rc] >= 0 && dist[rc] < dMin) dMin = dist[rc];

			// ① 距离正确
			Assert.That(idx.NearestRiverDistHops[i], Is.EqualTo(dMin),
				$"格 {i}：最近河流距离须与暴力 BFS 一致（索引 {idx.NearestRiverDistHops[i]} vs 暴力 {dMin}）");
			// ② 身份是真实河流对象
			int lbl = idx.NearestRiverCell[i];
			Assert.That(riverSet.Contains(lbl), Is.True,
				$"格 {i}：最近河流 label 必须指向真实河流格（实得 {lbl}）——label 语义失效的典型症状");
			// ③ 身份正确（不是"某个河流格"，而是"最近的那个河流格"）
			Assert.That(dist[lbl], Is.EqualTo(dMin),
				$"格 {i}：label 指向的河流格须真的处于最小图距（label={lbl}，其距 {dist[lbl]} vs 最小 {dMin}）");
			checkedCells++;
		}
		Assert.That(checkedCells, Is.GreaterThan(0));
	}

	/// <summary>区域身份验收（v1 未暴露 NearestRegionDistHops，故只验身份：label 指向的
	/// 区域必须处于真实最小图距——等价的双验口径，不引入新 API 以免破坏 v1 冻结面）。</summary>
	[Test]
	public void NearestRegion_MatchesBruteForce_Identity()
	{
		var (idx, f, _, _) = Make();
		int n = f.FinalLand.Length;
		var regions = new List<int>();
		var seen = new HashSet<int>();
		for (int i = 0; i < n; i++)
			if (f.FinalRegionOfCell[i] >= 0 && seen.Add(f.FinalRegionOfCell[i]))
				regions.Add(f.FinalRegionOfCell[i]);
		Assert.That(regions.Count, Is.GreaterThan(0), "须存在区域");

		for (int i = 0; i < n; i += 11)
		{
			var dist = BfsFrom(Ball, n, i);
			int best = int.MaxValue, dLabel = int.MaxValue;
			int lbl = idx.NearestRegionId[i];
			for (int j = 0; j < n; j++)
			{
				int r = f.FinalRegionOfCell[j];
				if (r < 0 || dist[j] < 0) continue;
				if (dist[j] < best) best = dist[j];
				if (r == lbl && dist[j] < dLabel) dLabel = dist[j];
			}
			Assert.That(dLabel, Is.EqualTo(best),
				$"格 {i}：最近区域身份须处于最小图距（label={lbl}，其距 {dLabel} vs 最小 {best}）");
		}
	}

	[Test]
	public void NoRiver_WorldReturnsMinusOne()
	{
		var (idx, _, _, _) = Make(withRiver: false);
		foreach (int d in idx.NearestRiverDistHops) Assert.That(d, Is.EqualTo(-1), "无河流世界：距离场恒 −1");
		var q = Vector3.Up;
		Assert.That(idx.NearestRiver(q), Is.EqualTo(-1));
		Assert.That(idx.WithinRiver(q, 5), Is.False);
	}

	[Test]
	public void NearestMountain_MatchesBruteForce()
	{
		var (idx, _, m, _) = Make();
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

	/// <summary>单源 BFS 距离场：返回 src 到每格的跳数（不可达 = −1）——最近性验证的独立基准。</summary>
	static int[] BfsFrom(Ball ball, int n, int src)
	{
		var dist = new int[n];
		Array.Fill(dist, -1);
		var q = new Queue<int>();
		dist[src] = 0;
		q.Enqueue(src);
		var nbrs = ball.CellNeighbors;
		while (q.Count > 0)
		{
			int i = q.Dequeue();
			foreach (int j in nbrs[i])
				if (dist[j] < 0) { dist[j] = dist[i] + 1; q.Enqueue(j); }
		}
		return dist;
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
