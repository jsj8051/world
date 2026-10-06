using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Spatial;    // Ball（H3 球壳数据层）
using World.Utils.H3;

namespace World.WorldGen;

// 世界生成空间 · Final 空间索引（#13，决策 08 v2——"Final World 空间查询基础设施"）：
//   索引的是 **Final World 的事实**（FinalLand/FinalLandmassId/FinalRegionOfCell 的连通
//   结构 + 特征锚点点集），**不是任何生成器的内部状态**——本类对 Placement 投影器、
//   Feature 生成器、TectonicField、SurfaceResolver 零引用（ArchitectureContractTests 钉死）。
// v1 刻意窄（决策 08 v2 §#13）：
//   面状事实（陆块/区域/河流）= **多源 BFS 距离场**——预计算 O(n) 一次，逐格表查询 O(1)，
//   批量查询天然廉价（表本身就是预计算的批量结果）；
//   点状事实（山系/火山锚点）= O(K) 暴力（数量 ~10-30，暴力即最优；KD-tree 等留到 K 上千）。
// 距离口径 = **H3 图距（跳数）**，与 FinalDistToCoast/DistToLand 同口径；km 换算 = 跳数 ×
//   格宽（调用方按 res 折算）。任意方向查询经 dir → H3 格 → 表（格粒度精度）。
// 消费者（River 第二批/Settlement/Biome/道路/政治地理）只问 nearest/distance/within，
//   不需要知道内部是 BFS 场还是暴力（API 层验收 ②）。
// ★永久验收原则（#13 BFS label bug 的沉淀）：所有 nearest 查询必须**同时验证距离正确**
//   与**对象身份（label）正确**——BFS 结构对而 label 错时，distance 全对但"是谁"失效
//   （实测：河流 label 恒 0 时全部 dist 正确、NearestRiver 恒返回格 0）。现有钉子：
//   NearestLandmass_MatchesBruteForce（label+dist 双验）/ NearestMountain_MatchesBruteForce
//   （anchor+dist）/ NearestRiver_Behavior（identity+dist+within）。
// ★River 2 契约（决策 08 v2 §2）：**RiverNetwork 不得依赖 FinalSpatialIndex**——
//   基础水文拓扑由自身 flow graph 决定；SpatialIndex 只做二级操作（排序/邻近挂接/查询）。
//   反射钉子在 ArchitectureContractTests.RiverNetwork_OnlyDependsOnFinalLayer。
/// <summary>
/// Final World 空间索引：陆块/区域/河流/山系/火山的 nearest/distance/within 查询。
/// </summary>
public sealed class FinalSpatialIndex
{
	/// <summary>每格最近陆块号（含海格——海上问"最近的陆"即得；陆格 = 自身）。</summary>
	public int[] NearestLandmassId { get; private set; } = Array.Empty<int>();
	/// <summary>每格到最近陆块的图距（跳数；陆格 = 0）。</summary>
	public int[] NearestLandmassDistHops { get; private set; } = Array.Empty<int>();
	/// <summary>每格最近区域号（FinalRegionOfCell 的空间扩展——海格/新增岛屿亦有最近区域）。</summary>
	public int[] NearestRegionId { get; private set; } = Array.Empty<int>();
	/// <summary>每格最近河流格索引（−1 = 无河流数据）与其图距。</summary>
	public int[] NearestRiverCell { get; private set; } = Array.Empty<int>();
	public int[] NearestRiverDistHops { get; private set; } = Array.Empty<int>();

	readonly Ball _ball;
	readonly Vector3[] _mountainAnchors;
	readonly Vector3[] _volcanoAnchors;

	public FinalSpatialIndex(Ball ball, IReadOnlyList<Vector3> mountainAnchors,
		IReadOnlyList<Vector3> volcanoAnchors)
	{
		_ball = ball ?? throw new ArgumentNullException(nameof(ball));
		_mountainAnchors = mountainAnchors as Vector3[] ?? new List<Vector3>(mountainAnchors).ToArray();
		_volcanoAnchors = volcanoAnchors as Vector3[] ?? new List<Vector3>(volcanoAnchors).ToArray();
	}

	/// <summary>预计算全部距离场（O(n)；一次构建，此后查询 O(1)/O(K)）。</summary>
	public void Generate(FinalGeography final, IReadOnlyList<int> riverCells)
	{
		int n = _ball.CellDirs.Length;

		// 陆块：FinalLandmassId ≥ 0 的格为源（label = 陆块号），多源 BFS 溢出到海
		(NearestLandmassId, NearestLandmassDistHops) = MultiSourceBfs(
			_ball, i => (final.FinalLand[i], final.FinalLandmassId[i]));

		// 区域：FinalRegionOfCell ≥ 0 的格为源
		(NearestRegionId, _) = MultiSourceBfs(
			_ball, i => (final.FinalRegionOfCell[i] >= 0, final.FinalRegionOfCell[i]));

		// 河流：河流格为源（label = 源格索引——查询返回最近河流格索引）
		var riverSet = new HashSet<int>(riverCells);
		(NearestRiverCell, NearestRiverDistHops) = MultiSourceBfs(
			_ball, i => (riverSet.Contains(i), i));
	}

	// ── 查询 API（验收 ②：nearest / distance / within）──

	/// <summary>最近陆块号（任意方向；格粒度）。</summary>
	public int NearestLandmass(Vector3 dir) => CellAt(dir) is int i && i >= 0 ? NearestLandmassId[i] : -1;

	/// <summary>到最近陆块的图距（跳数）。</summary>
	public int DistanceToNearestLandmass(Vector3 dir) => CellAt(dir) is int i && i >= 0 ? NearestLandmassDistHops[i] : -1;

	/// <summary>最近区域号。</summary>
	public int NearestRegion(Vector3 dir) => CellAt(dir) is int i && i >= 0 ? NearestRegionId[i] : -1;

	/// <summary>最近河流格索引（−1 = 世界无河流数据）。</summary>
	public int NearestRiver(Vector3 dir) => CellAt(dir) is int i && i >= 0 ? NearestRiverCell[i] : -1;

	/// <summary>到最近河流的图距（跳数；无河流数据 = −1）。</summary>
	public int DistanceToNearestRiver(Vector3 dir) => CellAt(dir) is int i && i >= 0 ? NearestRiverDistHops[i] : -1;

	/// <summary>within 语义：到最近河流 ≤ maxHops。</summary>
	public bool WithinRiver(Vector3 dir, int maxHops) =>
		DistanceToNearestRiver(dir) is int d && d >= 0 && d <= maxHops;

	/// <summary>最近山系锚点（角距 rad；无锚点 = null）。</summary>
	public (Vector3 anchor, float distRad)? NearestMountain(Vector3 dir) => NearestOf(_mountainAnchors, dir);

	/// <summary>最近火山锚点（角距 rad；无 = null）。</summary>
	public (Vector3 anchor, float distRad)? NearestVolcano(Vector3 dir) => NearestOf(_volcanoAnchors, dir);

	/// <summary>within 语义：到最近山系锚点 ≤ maxRad。</summary>
	public bool WithinMountain(Vector3 dir, float maxRad) =>
		NearestMountain(dir) is var hit && hit.HasValue && hit.Value.distRad <= maxRad;

	private static (Vector3, float)? NearestOf(Vector3[] anchors, Vector3 dir)
	{
		if (anchors.Length == 0) return null;
		Vector3 best = default;
		float bestD = float.PositiveInfinity;
		foreach (var a in anchors)
		{
			float d = MathF.Acos(Math.Clamp(dir.Dot(a), -1f, 1f));
			if (d < bestD) { bestD = d; best = a; }
		}
		return (best, bestD);
	}

	/// <summary>方向 → 本索引格表下标（dir → H3 格 → 全局下标；任意点查询的格粒度换算）。</summary>
	private int CellAt(Vector3 dir)
	{
		double lat = Math.Asin(Math.Clamp(dir.Y, -1f, 1f));
		double lng = Math.Atan2(dir.Z, dir.X);
		ulong cell = H3.LatLngToCell(new LatLng(lat, lng), _ball.Res);
		return _ball.CellIndexOf(cell);
	}

	/// <summary>多源 BFS 距离场：源格带 label，等权边扩散——每格首次到达即最近源
	/// （label/dist 双场；BFS 队列序固定 ⇒ 确定性）。</summary>
	private static (int[] label, int[] dist) MultiSourceBfs(
		Ball ball, Func<int, (bool isSource, int label)> sourceOf)
	{
		int n = ball.CellDirs.Length;
		var label = new int[n];
		var dist = new int[n];
		Array.Fill(label, -1);
		Array.Fill(dist, -1);
		var queue = new Queue<int>(n);
		for (int i = 0; i < n; i++)
		{
			var (isSource, lb) = sourceOf(i);
			if (!isSource || lb < 0) continue;
			label[i] = lb;
			dist[i] = 0;
			queue.Enqueue(i);
		}
		var neighbors = ball.CellNeighbors;
		while (queue.Count > 0)
		{
			int i = queue.Dequeue();
			foreach (int j in neighbors[i])
			{
				if (dist[j] >= 0) continue;
				label[j] = label[i];
				dist[j] = dist[i] + 1;
				queue.Enqueue(j);
			}
		}
		return (label, dist);
	}
}
