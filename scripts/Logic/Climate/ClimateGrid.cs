using System;
using System.Collections.Generic;
using Godot;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 气候阶段的网格视图：把 ⓪① 的产物压成气候模型要的最小口径（方向 / 邻接 / 海陆 / 海拔 / 尺度）。
///
/// 气候模型全部按「格下标数组」工作，不认 <see cref="Ball"/>、不认阶段产物——
/// 这样内核可在纯托管环境里用合成网格测试（见 <see cref="FromArrays"/>）。
///
/// @example
/// <code>
/// var grid = ClimateGrid.From(ball, terrain, spec.Terrain.SeaLevelM);
/// var dirs = grid.Dirs;            // 格心单位方向
/// var lat  = grid.LatRad(i);       // 弧度，北正南负
/// </code>
/// </summary>
public sealed class ClimateGrid
{
	/// <summary>格心单位方向（纬度 / 风向 / 梯度全从它算）。</summary>
	public Vector3[] Dirs { get; }

	/// <summary>邻接表（六边形 6 / 五边形 5；图上的跳数口径一切走它）。</summary>
	public int[][] Neighbors { get; }

	/// <summary>陆格掩码（来自 ① 的 <c>FinalLand</c>，不由高度符号二次判断）。</summary>
	public bool[] Land { get; }

	/// <summary>最终海拔（米；海平面 = 0，负值为海底）。</summary>
	public float[] HeightM { get; }

	/// <summary>归一化海拔 ∈ [−1, 1]：0 = 海平面、+1 = 世界最高点、−1 = 最深处。</summary>
	public float[] ElevNorm { get; }

	/// <summary>海格离岸跳数（陆格 = 0）。</summary>
	public int[] DistToLand { get; }

	/// <summary>陆格离海跳数（海格 = 0）。</summary>
	public int[] DistToCoast { get; }

	/// <summary>世界半径（km；距离 / 波长的换算口径）。</summary>
	public float RadiusKm { get; }

	/// <summary>相邻格中心距（km；跳数 ⇄ 距离的唯一折算，随 res 变）。</summary>
	public double NeighborKm { get; }

	/// <summary>格数。</summary>
	public int Count => Dirs.Length;

	ClimateGrid(Vector3[] dirs, int[][] neighbors, bool[] land, float[] heightM,
		float radiusKm, double neighborKm)
	{
		Dirs = dirs;
		Neighbors = neighbors;
		Land = land;
		HeightM = heightM;
		RadiusKm = radiusKm;
		NeighborKm = neighborKm;
		ElevNorm = NormalizeHeights(heightM);
		DistToCoast = BfsFrom(land, neighbors, sourceLand: false);
		DistToLand = BfsFrom(land, neighbors, sourceLand: true);
	}

	/// <summary>由球壳数据层 + ① 地形阶段建立气候视图。</summary>
	public static ClimateGrid From(Ball ball, TerrainPipeline terrain, float seaLevelM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (terrain == null) throw new ArgumentNullException(nameof(terrain));

		int n = ball.CellDirs.Length;
		var heights = new float[n];
		for (int i = 0; i < n; i++) heights[i] = terrain.HeightM[i] - seaLevelM;

		return new ClimateGrid(ball.CellDirs, ball.CellNeighbors, terrain.FinalLand, heights,
			SphericalFbmNoise.WorldRadiusKm, SpatialScale.Of(ball).NeighborCenterKm);
	}

	/// <summary>由裸数组建立气候视图（测试 / 诊断用；不做任何校验以外的推断）。</summary>
	/// <param name="neighborKm">相邻格中心距（km）；合成网格给任意正值即可，只影响 km 折算。</param>
	public static ClimateGrid FromArrays(Vector3[] dirs, int[][] neighbors, bool[] land, float[] heightM,
		float radiusKm = 0f, double neighborKm = 1.0)
	{
		if (dirs == null) throw new ArgumentNullException(nameof(dirs));
		if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
		if (land == null) throw new ArgumentNullException(nameof(land));
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		int n = dirs.Length;
		if (neighbors.Length != n || land.Length != n || heightM.Length != n)
			throw new ArgumentException("directions / neighbors / land / height 必须等长");
		if (!(neighborKm > 0)) throw new ArgumentOutOfRangeException(nameof(neighborKm));
		return new ClimateGrid(dirs, neighbors, land, heightM,
			radiusKm > 0f ? radiusKm : SphericalFbmNoise.WorldRadiusKm, neighborKm);
	}

	/// <summary>格心纬度（弧度；北正南负，±π/2）。</summary>
	public float LatRad(int i) => MathF.Asin(Math.Clamp(Dirs[i].Y, -1f, 1f));

	/// <summary>格心纬度（度）。</summary>
	public float LatDeg(int i) => LatRad(i) * 180f / MathF.PI;

	/// <summary>海格方向上的最近岸距衰减（0.08 rad ≈ 500 km）：陆地格借邻居海格的洋流冷暖。</summary>
	public const float CoastalDecayRad = 0.08f;

	/// <summary>多源 BFS：<paramref name="sourceLand"/> = false 时从海格出发（得陆格离海跳数），反之亦然。</summary>
	static int[] BfsFrom(bool[] land, int[][] neighbors, bool sourceLand)
	{
		int n = land.Length;
		var distance = new int[n];
		var queue = new Queue<int>(n);

		for (int i = 0; i < n; i++)
		{
			if (land[i] == sourceLand) { distance[i] = 0; queue.Enqueue(i); }
			else distance[i] = int.MaxValue;
		}

		while (queue.Count > 0)
		{
			int i = queue.Dequeue();
			foreach (int nb in neighbors[i])
			{
				if (distance[nb] != int.MaxValue) continue;
				distance[nb] = distance[i] + 1;
				queue.Enqueue(nb);
			}
		}

		// 全陆 / 全海世界：另一端没有源 ⇒ 距离无定义，用 0 表示"没有对岸"。
		for (int i = 0; i < n; i++) if (distance[i] == int.MaxValue) distance[i] = 0;
		return distance;
	}

	static float[] NormalizeHeights(float[] heightM)
	{
		float span = 1f;
		foreach (float h in heightM) span = MathF.Max(span, MathF.Abs(h));

		// 反向 `span` 只用于极端平坦世界（全世界 ≤1 m）：此时归一化会放大数值噪声，
		// 宁可让全部格都贴近 0（= 无高差）。旧链 ElevSpan 同口径。
		var norm = new float[heightM.Length];
		for (int i = 0; i < norm.Length; i++) norm[i] = Math.Clamp(heightM[i] / span, -1f, 1f);
		return norm;
	}
}
