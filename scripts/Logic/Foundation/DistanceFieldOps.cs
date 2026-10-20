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

		var dirs = ball.CellDirs;
		var neighbors = ball.CellNeighbors;

		var dist = new double[n];
		Array.Fill(dist, double.PositiveInfinity);
		var strength = new float[n];
		var src = new int[n];
		Array.Fill(src, -1);

		var heap = new PriorityQueue<int, double>(Math.Max(4, sources.Count));
		foreach (int s in sources)
		{
			if (s < 0 || s >= n) continue;
			if (dist[s] == 0.0) continue;               // 重复源
			dist[s] = 0.0;
			strength[s] = strengthOf(s);
			src[s] = s;
			heap.Enqueue(s, 0.0);
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
				strength[j] = strength[i];
				src[j] = src[i];
				heap.Enqueue(j, nd);
			}
		}

		var distKm = new float[n];
		for (int i = 0; i < n; i++)
			distKm[i] = double.IsPositiveInfinity(dist[i]) ? (float)capKm : (float)dist[i];
		return new Field(distKm, strength, src);
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
