using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）
using World.Data;
using World.H3Grid;
using World.Utils;
using World.Utils.H3;

namespace World.Logic;

/// <summary>
/// 板块划分：全球一份 fBm 阻力场，所有种子点同时做加权多源洪泛（Dijkstra，累积阻力 × 跨距最小优先），
/// 谁先漫到归谁。用洪泛而非加噪 argmin ⇒ 每块连通、不出飞地；边界是累积代价等值线，被阻力场扭成蜿蜒曲线。
/// 面积重尾是有意为之，不做 Lloyd 松弛。
/// </summary>
public static class PlateFloodOps
{
	/// <summary>洪泛产物：逐格归属 + 逐块格数 + 板块邻接。</summary>
	public readonly struct Result
	{
		/// <summary>逐格板块号（全球覆盖，每格恒有归属）。</summary>
		public readonly int[] Owner;
		/// <summary>逐块格数（长度 = 板块数；Σ = 格数）。</summary>
		public readonly int[] Size;
		/// <summary>板块邻接矩阵（<c>[a,b]</c> = 两块有直接相邻的格；对称、对角线 false）。</summary>
		public readonly bool[,] Adjacency;

		internal Result(int[] owner, int[] size, bool[,] adjacency)
		{
			Owner = owner;
			Size = size;
			Adjacency = adjacency;
		}
	}

	/// <summary>多源洪泛划分。</summary>
	/// <param name="ball">球壳数据层（格 id / 方向 / 邻接）。</param>
	/// <param name="plates">种子表（只读种子点位置）。</param>
	/// <param name="seed">世界种子（走 <c>Plate_Distort</c> 噪声流）。</param>
	/// <param name="amplitude">阻力起伏幅度。≤ 0 = 均匀阻力 ⇒ 退化为测地 Voronoi。</param>
	/// <param name="baseWavelengthKm">fBm 基波长（km）。</param>
	/// <param name="octaves">fBm 倍频层数（按采样下限截断）。</param>
	public static Result Grow(Ball ball, Plate[] plates, int seed,
		float amplitude, float baseWavelengthKm, int octaves)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		if (plates.Length < 1) throw new InvalidOperationException("种子表为空：先 PlateSeedOps.Build");

		var cells = ball.CellIds;
		int res = ball.Res;
		var dirs = ball.CellDirs;
		var neighbors = ball.CellNeighbors;
		int n = cells.Length;

		// 阻力场全局一份（不是每块一份）：每格一个 > 0 的乘性跨越代价；
		// 层数按采样下限截断，与 ① 的噪声场同一条纪律。
		double minWavelengthKm = 3.0 * SpatialScale.ForRes(res, n).NeighborCenterKm;
		int safeOctaves = NoiseSampling.ClampOctaves(octaves, baseWavelengthKm, minWavelengthKm);
		float[] resistance = BuildResistanceField(dirs, seed, amplitude, baseWavelengthKm, safeOctaves);

		// ── 多源洪泛：种子代价 0，逐格扩到代价最小的邻居 ──
		var owner = new int[n];
		var cost = new double[n];
		var visited = new bool[n];
		Array.Fill(owner, -1);
		Array.Fill(cost, double.PositiveInfinity);

		var pq = new PriorityQueue<int, double>();
		for (int p = 0; p < plates.Length; p++)
		{
			int cell = SeedCellOf(cells, res, plates[p].SeedTheta, plates[p].SeedPhi);
			if (owner[cell] >= 0) continue;      // 两块种子落同格 ⇒ 后者作废（先到先得）
			owner[cell] = p;
			cost[cell] = 0.0;
			pq.Enqueue(cell, 0.0);
		}

		while (pq.TryDequeue(out int i, out double c))
		{
			if (visited[i]) continue;          // 惰性删除（同格可能被多次入队）
			visited[i] = true;

			foreach (int j in neighbors[i])
			{
				if (visited[j]) continue;
				// 跨越代价 = 两格阻力的几何均值 × 跨距：阻力高处难长过去，边界绕开
				double step = Math.Sqrt((double)resistance[i] * resistance[j])
					* SpatialScale.DistanceKm(dirs[i], dirs[j]);
				double nc = c + step;
				if (nc < cost[j])
				{
					cost[j] = nc;
					owner[j] = owner[i];
					pq.Enqueue(j, nc);
				}
			}
		}

		// 洪泛覆盖全球（网格连通 ⇒ 每格必被访问）；漏格说明邻接表不连通，须报错而非静默留 −1
		for (int i = 0; i < n; i++)
			if (owner[i] < 0)
				throw new InvalidOperationException($"格 {i} 未被洪泛覆盖（邻接表不连通？）");

		var plateSize = new int[plates.Length];
		for (int i = 0; i < n; i++) plateSize[owner[i]]++;

		// 板块邻接（跨板块相邻格对）：壳型分配要判两块陆壳是否挨着
		var adjacency = new bool[plates.Length, plates.Length];
		for (int i = 0; i < n; i++)
		{
			int a = owner[i];
			foreach (int j in neighbors[i])
			{
				int b = owner[j];
				if (a == b || adjacency[a, b]) continue;
				adjacency[a, b] = true;
				adjacency[b, a] = true;
			}
		}

		return new Result(owner, plateSize, adjacency);
	}

	/// <summary>造全球阻力场：每格一个 > 0 的乘性跨越代价（噪声 ∈ [−1,1] ⇒ 阻力恒正）。
	/// 单位阻力（amplitude ≤ 0）⇒ 纯测地 Voronoi（边界是直线弧）。</summary>
	static float[] BuildResistanceField(Vector3[] dirs, int seed,
		float amplitude, float baseWavelengthKm, int octaves)
	{
		var resistance = new float[dirs.Length];
		if (!(amplitude > 0f))
		{
			Array.Fill(resistance, 1f);   // 省掉整场噪声采样
			return resistance;
		}

		// 乘性 (1+amp)^noise：噪声 ±1 造成成倍阻力差，边界才会被"挡"得绕行
		int noiseSeed = SeedDerivation.Derive(seed, SeedDerivation.Plate_Distort);
		var noise = new SphericalFbmNoise(noiseSeed, baseWavelengthKm, octaves);
		for (int i = 0; i < resistance.Length; i++)
			resistance[i] = MathF.Pow(1f + amplitude, noise.Sample(dirs[i]));
		return resistance;
	}

	/// <summary>种子点 (θ,φ) 所在格（`LatLngToCell` 映射成 id，再查格表）。</summary>
	static int SeedCellOf(ulong[] cells, int res, float theta, float phi)
	{
		LatLng g = CoordUtil.EulerToLatLng(theta, phi);
		ulong id = H3.LatLngToCell(g, res);
		for (int i = 0; i < cells.Length; i++)
			if (cells[i] == id) return i;
		throw new InvalidOperationException($"种子点 ({theta:F4}, {phi:F4}) 落到的格不在格表（res{res}）");
	}
}
