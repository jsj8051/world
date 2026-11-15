using System;
using System.Collections.Generic;
using Godot;

namespace World.H3Grid;

/// <summary>
/// 球面最近格索引：按经纬分桶，查询时只扫 3×3 桶，避免每次采样都走 H3 原生 <c>LatLngToCell</c>。
/// 存在理由 = 流线积分每步要问几十次"这个点属于哪一格"，原生调用在这个量级上太贵。
/// 构造后只读，可跨线程查询（后台积分线程直接复用）。与 <see cref="BallGeoIndex"/> 同族：
/// 后者缓存逐格经纬度、只提供精确点→格换算；本类提供高频近似邻近查询。
/// </summary>
public sealed class NearestCellIndex
{
	const int BucketLat = 128, BucketLon = 256;

	readonly Vector3[] _dirs;
	readonly int[][] _neighbors;
	readonly int[][] _buckets;

	public NearestCellIndex(Ball ball)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		_dirs = ball.CellDirs;
		_neighbors = ball.CellNeighbors;
		_buckets = BuildBuckets(_dirs);
	}

	/// <summary>球面点最近的格下标（3×3 桶内线性搜索）。</summary>
	public int Nearest(Vector3 p)
	{
		var (by, bx) = BucketOf(p);
		int best = -1;
		float bestDistance = float.MaxValue;

		for (int dy = -1; dy <= 1; dy++)
		{
			int y = (by + dy + BucketLat) % BucketLat;
			for (int dx = -1; dx <= 1; dx++)
			{
				int x = (bx + dx + BucketLon) % BucketLon;
				foreach (int cell in _buckets[y * BucketLon + x])
				{
					float distance = (_dirs[cell] - p).LengthSquared();
					if (distance < bestDistance) { bestDistance = distance; best = cell; }
				}
			}
		}
		return best;
	}

	/// <summary>
	/// 已知上一格时只查本格 + 1 环（步长小于半格 ⇒ 最近格不会跳出 1 环）。
	/// 超过 <paramref name="maxJumpArc"/> 说明点确实跳远了（换流线/换分支），退回 <see cref="Nearest"/> 全局查询。
	/// </summary>
	public int Refine(Vector3 p, int cell, float maxJumpArc)
	{
		if (cell < 0) return Nearest(p);
		int best = cell;
		float bestDistance = (_dirs[cell] - p).LengthSquared();
		foreach (int nb in _neighbors[cell])
		{
			float distance = (_dirs[nb] - p).LengthSquared();
			if (distance < bestDistance) { bestDistance = distance; best = nb; }
		}
		return bestDistance <= maxJumpArc * maxJumpArc ? best : Nearest(p);
	}

	static int[][] BuildBuckets(Vector3[] dirs)
	{
		var buckets = new int[BucketLat * BucketLon][];
		var lists = new List<int>[BucketLat * BucketLon];
		for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>(4);

		for (int i = 0; i < dirs.Length; i++)
		{
			var (by, bx) = BucketOf(dirs[i]);
			lists[by * BucketLon + bx].Add(i);
		}
		for (int i = 0; i < lists.Length; i++) buckets[i] = lists[i].ToArray();
		return buckets;
	}

	static (int by, int bx) BucketOf(Vector3 p)
	{
		float lat = MathF.Asin(Math.Clamp(p.Y, -1f, 1f));
		float lon = MathF.Atan2(p.Z, p.X);
		int by = Math.Clamp((int)((lat + MathF.PI / 2f) / MathF.PI * BucketLat), 0, BucketLat - 1);
		int bx = Math.Clamp((int)((lon + MathF.PI) / MathF.Tau * BucketLon), 0, BucketLon - 1);
		return (by, bx);
	}
}
