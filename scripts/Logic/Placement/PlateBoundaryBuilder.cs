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
		if (p < 1) throw new InvalidOperationException("板块表为空：先 PlateSeedOps.Build");

		var neighbors = ball.CellNeighbors;
		int slots = p * p;

		// ── ① 计数：逐格边数 + 逐板块对边数 ──
		var edgesPerCell = new int[n];
		var edgesPerPair = new int[slots];
		int edgeCount = 0;
		for (int i = 0; i < n; i++)
		{
			int a = plateOfCell[i];
			RequirePlate(a, i, p);
			foreach (int j in neighbors[i])
			{
				if (j < i) continue;                       // 规范序：每条边只数一次
				int b = plateOfCell[j];
				if (a == b) continue;
				RequirePlate(b, j, p);

				edgesPerCell[i]++;
				edgesPerCell[j]++;
				edgesPerPair[PairKey(a, b, p)]++;
				edgeCount++;
			}
		}

		int[] pairOffset = Prefix(edgesPerPair);
		int[] cellOffset = Prefix(edgesPerCell);

		// ── ② 定边：写进各板块对的连续区间 ──
		var edges = new PlateBoundaryEdge[edgeCount];
		var pairCursor = new int[slots];
		Array.Copy(pairOffset, pairCursor, slots);
		var edgeIndex = new Dictionary<long, int>(edgeCount);
		for (int i = 0; i < n; i++)
		{
			int a = plateOfCell[i];
			foreach (int j in neighbors[i])
			{
				if (j < i) continue;
				int b = plateOfCell[j];
				if (a == b) continue;

				int at = pairCursor[PairKey(a, b, p)]++;
				edges[at] = new PlateBoundaryEdge(i, j, a, b);
				edgeIndex[EdgeKey(i, j)] = at;
			}
		}

		// ── ③ 板块对 + 逐格关联（按各格邻居原序，浮点求和顺序即语义）──
		var pairs = new PlateBoundaryPair[slots];
		for (int a = 0; a < p; a++)
		{
			for (int b = a + 1; b < p; b++)
			{
				int key = PairKey(a, b, p);
				if (edgesPerPair[key] == 0) continue;
				var (roleA, roleB) = PlateBoundaryOps.PairRoles(plates[a], plates[b]);
				pairs[key] = new PlateBoundaryPair(a, b, roleA, roleB, pairOffset[key], edgesPerPair[key]);
			}
		}

		var cellEdgeIndex = new int[2 * edgeCount];
		var cellCursor = new int[n];
		Array.Copy(cellOffset, cellCursor, n);
		for (int i = 0; i < n; i++)
		{
			foreach (int j in neighbors[i])
			{
				if (plateOfCell[j] == plateOfCell[i]) continue;
				if (!edgeIndex.TryGetValue(EdgeKey(i, j), out int at))
					throw new InvalidOperationException($"边界边 ({i},{j}) 未建表（邻接表不一致？）");
				cellEdgeIndex[cellCursor[i]++] = at;
			}
		}

		var set = new PlateBoundarySet(p, edges, pairs, cellOffset, cellEdgeIndex);
		set.Validate(n, plateOfCell);
		return set;
	}

	/// <summary>板块对键：小号 × 板块数 + 大号（与遍历顺序无关的唯一格）。</summary>
	static int PairKey(int a, int b, int p) => (a < b ? a : b) * p + (a < b ? b : a);

	/// <summary>无向边键：两个格号打包成一个 long。</summary>
	static long EdgeKey(int i, int j) => ((long)Math.Min(i, j) << 32) | (uint)Math.Max(i, j);

	/// <summary>计数数组 → 前缀和偏移（长度 +1，末位 = 总数）。</summary>
	static int[] Prefix(int[] counts)
	{
		var offset = new int[counts.Length + 1];
		for (int i = 0; i < counts.Length; i++) offset[i + 1] = offset[i] + counts[i];
		return offset;
	}

	/// <summary>板块号越界即抛。</summary>
	static void RequirePlate(int plate, int cell, int plateCount)
	{
		if ((uint)plate >= (uint)plateCount)
			throw new InvalidOperationException($"格 {cell} 的板块号 {plate} 越界（板块数 {plateCount}）");
	}
}
