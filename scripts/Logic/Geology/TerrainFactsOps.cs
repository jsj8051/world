using System;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 由最终高度派生世界事实：陆海掩码 → 陆占比 → 陆块分量 → 双向海岸距离。纯派生，不改高度。
/// 口径唯一：<c>Land = HeightM &gt; SeaLevelM</c>，其余全部由它 BFS / 连通分量而来。
/// </summary>
public static class TerrainFactsOps
{
	/// <summary>结果：六份逐格事实 + 陆块数。</summary>
	public readonly struct Field
	{
		public readonly bool[] Land;
		public readonly float[] LandFraction;
		public readonly int[] LandmassId;
		public readonly int LandmassCount;
		public readonly int[] DistToCoast;
		public readonly int[] DistToLand;

		internal Field(bool[] land, float[] landFraction, int[] landmassId, int landmassCount,
			int[] distToCoast, int[] distToLand)
		{
			Land = land;
			LandFraction = landFraction;
			LandmassId = landmassId;
			LandmassCount = landmassCount;
			DistToCoast = distToCoast;
			DistToLand = distToLand;
		}
	}

	/// <summary>由最终高度派生全部世界事实。</summary>
	public static Field Build(Ball ball, float[] heightM, float seaLevelM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		int n = ball.CellDirs.Length;
		if (heightM.Length != n)
			throw new ArgumentException($"高度长度 {heightM.Length} ≠ 格数 {n}", nameof(heightM));

		var land = new bool[n];
		for (int i = 0; i < n; i++) land[i] = heightM[i] > seaLevelM;

		var landFraction = new float[n];
		var neighbors = ball.CellNeighbors;
		for (int i = 0; i < n; i++)
		{
			float sum = land[i] ? 1f : 0f;
			int count = 1;
			foreach (int j in neighbors[i])
			{
				sum += land[j] ? 1f : 0f;
				count++;
			}
			landFraction[i] = sum / count;
		}

		(int[] landmassId, int landmassCount) = LandMaskOps.Components(ball, i => land[i]);
		int[] distToCoast = LandMaskOps.HopsFrom(ball, i => !land[i]);
		int[] distToLand = LandMaskOps.HopsFrom(ball, i => land[i]);
		return new Field(land, landFraction, landmassId, landmassCount, distToCoast, distToLand);
	}
}
