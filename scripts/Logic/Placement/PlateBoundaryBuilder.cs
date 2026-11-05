using System;
using System.Collections.Generic;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 板块边界建表：从逐格板块号物化共享边（相邻异板块格对），按板块对分组并建逐格关联。
/// 三趟 O(格数 + 边数)：计数 → 定边 → 填索引；排列只依赖网格扫描序，与线程 / 调用次序无关。
/// 须在壳型派定之后调（<see cref="PlateBoundaryPair"/> 的两侧角色依赖壳型 / 密度）。
/// </summary>
public static class PlateBoundaryBuilder
{
	/// <summary>建边界集并自检；不自检通过即抛，不产出半成品。</summary>
	public static PlateBoundarySet Build(Ball ball, int[] plateOfCell, Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));

		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));
		int p = plates.Length;
		if (p < 1) throw new InvalidOperationException("板块表为空：先 BuildSeeds");

		var neighbors = ball.CellNeighbors;
		var degree = new int[n];
		var pairCount = new int[p * p];
		int edgeCount = 0;

		// ── ① 计数：逐格出入度 + 逐板块对边数 ──
		for (int i = 0; i < n; i++)
		{
			int a = plateOfCell[i];
			if ((uint)a >= (uint)p)
				throw new InvalidOperationException($"格 {i} 的板块号 {a} 越界（板块数 {p}）");
			foreach (int j in neighbors[i])
			{
				if (j < i) continue;                       // 规范序：每条边只数一次
				int b = plateOfCell[j];
				if (a == b) continue;
				if ((uint)b >= (uint)p)
					throw new InvalidOperationException($"格 {j} 的板块号 {b} 越界（板块数 {p}）");
				degree[i]++;
				degree[j]++;
				pairCount[(a < b ? a : b) * p + (a < b ? b : a)]++;
				edgeCount++;
			}
		}

		var pairStart = new int[p * p + 1];
		for (int key = 0; key < p * p; key++) pairStart[key + 1] = pairStart[key] + pairCount[key];

		var cellStart = new int[n + 1];
		for (int i = 0; i < n; i++) cellStart[i + 1] = cellStart[i] + degree[i];

		// ── ② 定边：写进各板块对的连续区间 ──
		var edges = new PlateBoundaryEdge[edgeCount];
		var cursor = new int[p * p];
		Array.Copy(pairStart, cursor, p * p);
		var edgeLookup = new Dictionary<long, int>(edgeCount);
		for (int i = 0; i < n; i++)
		{
			int a = plateOfCell[i];
			foreach (int j in neighbors[i])
			{
				if (j < i) continue;
				int b = plateOfCell[j];
				if (a == b) continue;

				int key = (a < b ? a : b) * p + (a < b ? b : a);
				int index = cursor[key]++;
				edges[index] = new PlateBoundaryEdge(i, j, a, b);
				edgeLookup[((long)i << 32) | (uint)j] = index;
			}
		}

		// ── ③ 板块对 + 逐格关联（按各格邻居原序，浮点求和顺序即语义）──
		var pairs = new PlateBoundaryPair[p * p];
		for (int a = 0; a < p; a++)
		{
			for (int b = a + 1; b < p; b++)
			{
				int key = a * p + b;
				if (pairCount[key] == 0) continue;
				(BoundaryRole roleA, BoundaryRole roleB) = PlateBoundaryOps.PairRoles(plates[a], plates[b]);
				pairs[key] = new PlateBoundaryPair(a, b, roleA, roleB, pairStart[key], pairCount[key]);
			}
		}

		var cellEdgeIndex = new int[2 * edgeCount];
		var fill = new int[n];
		Array.Copy(cellStart, fill, n);
		for (int i = 0; i < n; i++)
		{
			foreach (int j in neighbors[i])
			{
				if (plateOfCell[j] == plateOfCell[i]) continue;
				long key = ((long)Math.Min(i, j) << 32) | (uint)Math.Max(i, j);
				if (!edgeLookup.TryGetValue(key, out int index))
					throw new InvalidOperationException($"边界边 ({i},{j}) 未建表（邻接表不一致？）");
				cellEdgeIndex[fill[i]++] = index;
			}
		}

		var set = new PlateBoundarySet(p, edges, pairs, cellStart, cellEdgeIndex);
		set.Validate(n, plateOfCell);
		return set;
	}
}
