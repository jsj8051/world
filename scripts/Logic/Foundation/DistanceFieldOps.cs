using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 逐格图的真实距离算子：多源 Dijkstra，逐格求到最近源的大圆距离（km）、该源强度、最近源格号。
///
/// 与 <see cref="LandMaskOps.HopsFrom"/>（跳数 BFS）分工：跳数是拓扑量（随 res 变、系数有偏），
/// 本类给几何量（km，跨 res 同义）。按 km 给的带宽 / 深度剖面 / 衰减尺度必须走本类，不得用跳数 × 常数折算。
///
/// 距离用 <c>double</c> 累加并与堆优先级同精度，只在出口转 <c>float</c>：中途存 <c>float</c> 时，
/// 向下舍入会让「出堆时比 dist 大」的过期判定误伤活结点，使部分格不再向外扩展、距离系统性偏长。
/// </summary>
public static class DistanceFieldOps
{
	/// <summary>距离场：逐格到最近源的距离（km）、该源强度、该源格号。
	/// 超出 <c>capKm</c>（或与该源不连通）的格：<c>DistKm = capKm</c>、<c>Strength = 0</c>、<c>Src = −1</c>。
	/// <c>capKm ≤ 0</c> ⇒ 空场：<c>DistKm = +∞</c>、<c>Strength = 0</c>、<c>Src = −1</c>（等价于关断）。</summary>
	public readonly struct Field
	{
		public readonly float[] DistKm;
		public readonly float[] Strength;
		public readonly int[] Src;

		internal Field(float[] distKm, float[] strength, int[] src)
		{
			DistKm = distKm;
			Strength = strength;
			Src = src;
		}
	}

	/// <summary>带编号的距离场源：同一格可以由不同来源播种，编号用于回查边界侧 / 通道。</summary>
	public readonly struct Seed
	{
		public readonly int Cell;
		public readonly int Id;
		public readonly double DistKm;

		public Seed(int cell, int id, double distKm = 0.0)
		{
			Cell = cell;
			Id = id;
			DistKm = distKm;
		}
	}

	/// <summary>带编号距离场：逐格最近源的编号（−1 = 够不到）与距离（km；够不到 = <c>capKm</c>）。</summary>
	public readonly struct SeedField
	{
		public readonly float[] DistKm;
		public readonly int[] SeedId;

		internal SeedField(float[] distKm, int[] seedId)
		{
			DistKm = distKm;
			SeedId = seedId;
		}
	}

	/// <summary>多源 Dijkstra 距离场。<paramref name="strengthOf"/> 只对源格调用一次，
	/// 强度沿最近源的路径逐格继承。</summary>
	/// <param name="sources">源格（重复无所谓；越界项忽略）。</param>
	/// <param name="strengthOf">源格 → 携带强度（非源格不调用）。</param>
	/// <param name="capKm">搜索截断半径（km）：超出即停，未到达的格取 <paramref name="capKm"/>。
	/// 它只影响「够不到的远方」，不改变截止距离内的结果。<c>≤ 0</c> ⇒ 返回空场（关断）。</param>
	/// <param name="passable">边通行谓词 <c>(from, to) → 是否可走</c>（如「只在同一板块内扩散」）；
	/// <c>null</c> = 全通。被拒的边不计距离，也绝不穿越。</param>
	public static Field Build(Ball ball, IReadOnlyList<int> sources, Func<int, float> strengthOf, double capKm,
		Func<int, int, bool> passable = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (sources == null) throw new ArgumentNullException(nameof(sources));
		if (strengthOf == null) throw new ArgumentNullException(nameof(strengthOf));

		int n = ball.CellDirs.Length;
		if (!(capKm > 0)) return Empty(n);

		// 源就是格：编号 = 格号，强度按格预取一次（重复源只取首个）。
		var seeds = new List<Seed>(sources.Count);
		var strengthByCell = new float[n];
		var seen = new bool[n];
		foreach (int s in sources)
		{
			if ((uint)s >= (uint)n || seen[s]) continue;
			seen[s] = true;
			seeds.Add(new Seed(s, s));
			strengthByCell[s] = strengthOf(s);
		}

		SeedField field = BuildSeeds(ball.CellDirs, ball.CellNeighbors, seeds, capKm, passable);
		var strength = new float[n];
		for (int i = 0; i < n; i++)
		{
			int id = field.SeedId[i];
			if (id >= 0) strength[i] = strengthByCell[id];
		}
		return new Field(field.DistKm, strength, field.SeedId);
	}

	/// <summary>带编号的多源 Dijkstra（球壳入口）。同格多源取更短起始距离，等距取更小编号。</summary>
	public static SeedField BuildSeeds(Ball ball, IReadOnlyList<Seed> seeds, double capKm,
		Func<int, int, bool> passable = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		return BuildSeeds(ball.CellDirs, ball.CellNeighbors, seeds, capKm, passable);
	}

	/// <summary>
	/// 带编号的多源 Dijkstra 内核：只依赖格心方向与邻接表（不依赖 H3，可单测）。
	/// 超出 <c>capKm</c> 或与所有源不连通的格：<c>DistKm = capKm</c>、<c>SeedId = −1</c>。
	/// 同距离保持先到者（扫描顺序确定）；同格多源按起始距离、再按编号取小。
	/// </summary>
	public static SeedField BuildSeeds(Vector3[] dirs, int[][] neighbors, IReadOnlyList<Seed> seeds,
		double capKm, Func<int, int, bool> passable = null)
	{
		if (dirs == null) throw new ArgumentNullException(nameof(dirs));
		if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
		if (seeds == null) throw new ArgumentNullException(nameof(seeds));
		if (neighbors.Length != dirs.Length)
			throw new ArgumentException($"邻接表长度 {neighbors.Length} ≠ 格数 {dirs.Length}", nameof(neighbors));

		int n = dirs.Length;
		if (!(capKm > 0)) return EmptySeeds(n);

		var dist = new double[n];
		Array.Fill(dist, double.PositiveInfinity);
		var seedId = new int[n];
		Array.Fill(seedId, -1);

		var heap = new PriorityQueue<int, double>(Math.Max(4, seeds.Count));
		foreach (Seed seed in seeds)
		{
			if ((uint)seed.Cell >= (uint)n) continue;
			double d = seed.DistKm;
			if (double.IsNaN(d) || d < 0.0 || double.IsPositiveInfinity(d) || d > capKm) continue;
			if (d > dist[seed.Cell]) continue;                         // 已有更近的源
			if (d == dist[seed.Cell] && seedId[seed.Cell] >= 0 && seedId[seed.Cell] <= seed.Id) continue;
			dist[seed.Cell] = d;
			seedId[seed.Cell] = seed.Id;
			heap.Enqueue(seed.Cell, d);
		}

		while (heap.TryDequeue(out int i, out double priority))
		{
			if (priority > dist[i]) continue;            // 过期条目：这个格已被更短的路更新过
			if (priority > capKm) break;                 // 堆按距离升序 ⇒ 余下的更远，不必再扩展

			foreach (int j in neighbors[i])
			{
				if (passable != null && !passable(i, j)) continue;
				double nd = priority + EdgeKm(dirs[i], dirs[j]);
				if (nd >= dist[j] || nd > capKm) continue;
				dist[j] = nd;
				seedId[j] = seedId[i];
				heap.Enqueue(j, nd);
			}
		}

		var distKm = new float[n];
		for (int i = 0; i < n; i++)
			distKm[i] = double.IsPositiveInfinity(dist[i]) ? (float)capKm : (float)dist[i];
		return new SeedField(distKm, seedId);
	}

	static SeedField EmptySeeds(int n)
	{
		var distKm = new float[n];
		Array.Fill(distKm, float.PositiveInfinity);
		var seedId = new int[n];
		Array.Fill(seedId, -1);
		return new SeedField(distKm, seedId);
	}

	static Field Empty(int n)
	{
		var distKm = new float[n];
		Array.Fill(distKm, float.PositiveInfinity);
		var src = new int[n];
		Array.Fill(src, -1);
		return new Field(distKm, new float[n], src);
	}

	/// <summary>两格心之间的大圆距离（km）。入参是球面单位方向；非单位向量也可传（内部夹紧）。</summary>
	static double EdgeKm(Vector3 a, Vector3 b)
	{
		double dot = a.Dot(b);
		if (dot > 1.0) dot = 1.0;
		else if (dot < -1.0) dot = -1.0;
		return SphericalFbmNoise.WorldRadiusKm * Math.Acos(dot);
	}
}
