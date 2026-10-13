using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.H3Grid;          // Ball / BallGeoIndex（连续点 → 格下标）

namespace World.Logic;

/// <summary>
/// 逐格图的派生算子：输入一份逐格标记（掩码 / 类别），输出它的图论派生量。
/// · <see cref="Components"/> = 连通分量编号（同标记的连通片，逐片一编号）；
/// · <see cref="HopsFrom"/> = 邻接图多源 BFS 跳数；
/// · <see cref="IsLandAt"/> = 连续点 → 最近格掩码。
/// </summary>
public static class LandMaskOps
{
	/// <summary>
	/// 连通分量编号：<paramref name="isSource"/> 为真且互相连通的格算同一片，逐片给一个自 0 起的号。
	/// 例：陆块 = <c>Components(ball, i =&gt; land[i])</c>（海格 = −1）；
	///     按类别切片 = <c>Components(ball, i =&gt; classOfCell[i] == cls)</c>。
	/// </summary>
	/// <returns><c>id</c>：逐格分量号（非该片 = −1）；<c>count</c>：分量数。</returns>
	public static (int[] id, int count) Components(Ball ball, Func<int, bool> isSource)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (isSource == null) throw new ArgumentNullException(nameof(isSource));
		int n = ball.CellDirs.Length;

		var id = new int[n];
		Array.Fill(id, -1);
		var neighbors = ball.CellNeighbors;
		int count = 0;
		var bfs = new Queue<int>(n);
		for (int seed = 0; seed < n; seed++)
		{
			if (id[seed] >= 0 || !isSource(seed)) continue;
			id[seed] = count;
			bfs.Enqueue(seed);
			while (bfs.Count > 0)
			{
				int i = bfs.Dequeue();
				foreach (int j in neighbors[i])
				{
					if (id[j] >= 0 || !isSource(j)) continue;
					id[j] = count;
					bfs.Enqueue(j);
				}
			}
			count++;
		}
		return (id, count);
	}

	/// <summary>
	/// 多源 BFS **图距**（跳数）：<paramref name="source"/> 为真的格距离 0，逐跳 +1 扩散全图。
	/// 例：离海深度 = <c>HopsFrom(ball, i =&gt; !land[i])</c>（陆格 ≥ 1，海格 = 0）。
	/// </summary>
	public static int[] HopsFrom(Ball ball, Func<int, bool> source)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (source == null) throw new ArgumentNullException(nameof(source));
		int n = ball.CellDirs.Length;
		var dist = new int[n];
		var queue = new Queue<int>(n);
		for (int i = 0; i < n; i++)
		{
			if (!source(i)) { dist[i] = -1; continue; }
			dist[i] = 0;
			queue.Enqueue(i);
		}
		var neighbors = ball.CellNeighbors;
		while (queue.Count > 0)
		{
			int i = queue.Dequeue();
			foreach (int j in neighbors[i])
				if (dist[j] < 0) { dist[j] = dist[i] + 1; queue.Enqueue(j); }
		}
		return dist;
	}

	/// <summary>
	/// 连续单位方向处的海陆（折算到**最近格**的掩码；越界方向 → false）。
	/// 掩码是逐格量，任何"连续点是不是陆"的判据都必须回到格粒度上问——这正是统一回读口。
	/// </summary>
	public static bool IsLandAt(Ball ball, bool[] land, Vector3 dir)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (land == null) throw new ArgumentNullException(nameof(land));
		int i = BallGeoIndex.CellAt(ball, dir);
		return i >= 0 && i < land.Length && land[i];
	}
}
