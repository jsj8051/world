using System;
using System.Collections.Generic;

namespace World.Data;

/// <summary>
/// 板块边界集：把「相邻异板块格对」的共享边物化出来，并按板块对 / 逐格两个方向建索引。
/// 语义上是逐格板块号的<b>派生索引</b>，不是第二真源——只能由 <c>PlateBoundaryBuilder</c> 建，
/// 建表末尾用 <see cref="Validate"/> 对逐格板块号校验。
///
/// 排列是确定性的：边按板块对分组，组内按网格扫描序；逐格关联按该格邻居原序填
/// （浮点求和顺序即语义，勿重排）。
/// </summary>
public sealed class PlateBoundarySet
{
	/// <summary>空表（未建边界时的占位；不参与查询之外的任何计算）。</summary>
	public static readonly PlateBoundarySet Empty = new(0,
		Array.Empty<PlateBoundaryEdge>(), Array.Empty<PlateBoundaryPair>(), new[] { 0 }, Array.Empty<int>());

	/// <summary>板块数（板块号 ∈ [0, PlateCount)）。</summary>
	public int PlateCount { get; }

	/// <summary>全部共享边；按板块对分组，组内连续。</summary>
	public PlateBoundaryEdge[] Edges { get; }

	/// <summary>按板块对索引的边界（长度 PlateCount²；下标 = 小板块号 × PlateCount + 大板块号）。</summary>
	public PlateBoundaryPair[] Pairs { get; }

	/// <summary>逐格边界边起始下标（长度 = 格数 + 1；CSR 偏移）。</summary>
	public int[] CellEdgeStart { get; }

	/// <summary>逐格边界边下标（长度 = 2 × 边数；每条边被两侧各记一次）。</summary>
	public int[] CellEdgeIndex { get; }

	internal PlateBoundarySet(int plateCount, PlateBoundaryEdge[] edges, PlateBoundaryPair[] pairs,
		int[] cellEdgeStart, int[] cellEdgeIndex)
	{
		PlateCount = plateCount;
		Edges = edges;
		Pairs = pairs;
		CellEdgeStart = cellEdgeStart;
		CellEdgeIndex = cellEdgeIndex;
	}

	/// <summary>该格是否在板块边界上（至少有一条异板块邻居）。</summary>
	public bool IsBoundaryCell(int cell) =>
		(uint)cell < (uint)(CellEdgeStart.Length - 1)
		&& CellEdgeStart[cell] != CellEdgeStart[cell + 1];

	/// <summary>该格沾到的全部边界边下标（按邻居原序；非边界格返回空）。</summary>
	public ReadOnlySpan<int> EdgesOfCell(int cell)
	{
		if ((uint)cell >= (uint)(CellEdgeStart.Length - 1)) return ReadOnlySpan<int>.Empty;
		int start = CellEdgeStart[cell];
		return CellEdgeIndex.AsSpan(start, CellEdgeStart[cell + 1] - start);
	}

	/// <summary>取一对板块之间的边界。<paramref name="a"/> 与 <paramref name="b"/> 顺序无关；
	/// 返回值的 <c>RoleA</c> / <c>RoleB</c> 恒按 <c>PlateA</c>（较小号）/ <c>PlateB</c>（较大号）归属。</summary>
	public bool TryGetPair(int a, int b, out PlateBoundaryPair pair)
	{
		pair = default;
		if (PlateCount < 1 || a == b) return false;
		if ((uint)a >= (uint)PlateCount || (uint)b >= (uint)PlateCount) return false;
		int key = (a < b ? a : b) * PlateCount + (a < b ? b : a);
		pair = Pairs[key];
		return pair.Exists;
	}

	/// <summary>
	/// 校验边界集与逐格板块号一致：端点 / 板块号 / 唯一性 / 板块对分组 / 逐格关联。
	/// 不通过即抛——它只会是建表 bug，不该静默返回 false。
	/// </summary>
	public void Validate(int cellCount, int[] plateOfCell)
	{
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (cellCount < 0) throw new ArgumentOutOfRangeException(nameof(cellCount));
		if (plateOfCell.Length != cellCount)
			throw new InvalidOperationException($"边界集校验：板块号长度 {plateOfCell.Length} ≠ 格数 {cellCount}");
		if (CellEdgeStart.Length != cellCount + 1)
			throw new InvalidOperationException(
				$"边界集校验：CellEdgeStart 长度 {CellEdgeStart.Length} ≠ 格数+1 {cellCount + 1}");
		if (CellEdgeIndex.Length != 2 * Edges.Length)
			throw new InvalidOperationException(
				$"边界集校验：CellEdgeIndex {CellEdgeIndex.Length} ≠ 2×边数 {2 * Edges.Length}");
		if (Pairs.Length != PlateCount * PlateCount)
			throw new InvalidOperationException(
				$"边界集校验：Pairs 长度 {Pairs.Length} ≠ 板块数² {PlateCount * PlateCount}");

		var seen = new HashSet<long>();
		foreach (PlateBoundaryEdge edge in Edges)
		{
			if (edge.CellA < 0 || edge.CellB >= cellCount || edge.CellA >= edge.CellB)
				throw new InvalidOperationException($"边界集校验：边端点非法 ({edge.CellA},{edge.CellB})");
			if (edge.PlateA == edge.PlateB)
				throw new InvalidOperationException($"边界集校验：边 ({edge.CellA},{edge.CellB}) 两侧同板块");
			if (plateOfCell[edge.CellA] != edge.PlateA || plateOfCell[edge.CellB] != edge.PlateB)
				throw new InvalidOperationException(
					$"边界集校验：边 ({edge.CellA},{edge.CellB}) 的板块号与逐格归属不符");
			if (!seen.Add(((long)edge.CellA << 32) | (uint)edge.CellB))
				throw new InvalidOperationException($"边界集校验：边 ({edge.CellA},{edge.CellB}) 重复");
		}

		int pairEdgeSum = 0;
		for (int key = 0; key < Pairs.Length; key++)
		{
			PlateBoundaryPair pair = Pairs[key];
			if (!pair.Exists) continue;
			if (pair.EdgeStart < 0 || pair.EdgeStart + pair.EdgeCount > Edges.Length)
				throw new InvalidOperationException($"边界集校验：板块对 {key} 的边区间越界");
			if (pair.PlateA != key / PlateCount || pair.PlateB != key % PlateCount)
				throw new InvalidOperationException($"边界集校验：板块对 {key} 的板块号与下标不符");
			for (int k = pair.EdgeStart; k < pair.EdgeEnd; k++)
			{
				PlateBoundaryEdge edge = Edges[k];
				bool samePair = (edge.PlateA == pair.PlateA && edge.PlateB == pair.PlateB)
					|| (edge.PlateA == pair.PlateB && edge.PlateB == pair.PlateA);
				if (!samePair)
					throw new InvalidOperationException(
						$"边界集校验：边 ({edge.CellA},{edge.CellB}) 落在错误的板块对区间 {key}");
			}
			pairEdgeSum += pair.EdgeCount;
		}
		if (pairEdgeSum != Edges.Length)
			throw new InvalidOperationException($"边界集校验：板块对边数和 {pairEdgeSum} ≠ 边数 {Edges.Length}");

		int incidence = 0;
		for (int cell = 0; cell < cellCount; cell++)
		{
			int start = CellEdgeStart[cell];
			int end = CellEdgeStart[cell + 1];
			if (start < 0 || end < start || end > CellEdgeIndex.Length)
				throw new InvalidOperationException($"边界集校验：格 {cell} 的关联区间越界");
			for (int k = start; k < end; k++)
			{
				int edgeIndex = CellEdgeIndex[k];
				if (edgeIndex < 0 || edgeIndex >= Edges.Length)
					throw new InvalidOperationException($"边界集校验：格 {cell} 引用了不存在的边 {edgeIndex}");
				if (!Edges[edgeIndex].Contains(cell))
					throw new InvalidOperationException($"边界集校验：格 {cell} 引用了不含它的边 {edgeIndex}");
				for (int q = start; q < k; q++)
					if (CellEdgeIndex[q] == edgeIndex)
						throw new InvalidOperationException($"边界集校验：格 {cell} 重复引用边 {edgeIndex}");
			}
			incidence += end - start;
		}
		if (incidence != CellEdgeIndex.Length)
			throw new InvalidOperationException(
				$"边界集校验：逐格关联总数 {incidence} ≠ 关联表长度 {CellEdgeIndex.Length}");
	}
}
