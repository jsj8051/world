using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 陆面自由板偏置（freeboard）：把「陆壳被浮力托在海面之上」这一条补进陆面高度。
/// 距离源是洋壳格——离洋壳越远加得越多，海岸带（距洋壳不足 <see cref="TerrainSpec.FreeboardSpanKm"/>）几乎不加。
/// 于是海面切的是「内部高、边缘低」的陆面：淹没集中到大陆边缘（成片宽陆架 + 少数内海），
/// 而不是在一片半沉的陆面上按噪声随机切开一堆碎水洼。
/// 三档变化场管「形状」，本项管「陆壳整体被托多高」——两者相加才是 <see cref="LandFieldOps"/> 的陆面基底。
/// </summary>
public static class FreeboardOps
{
	/// <summary>逐格偏置（米；生成基准，未乘 <see cref="TerrainSpec.VerticalScale"/>）。只对陆壳格非零。</summary>
	public static float[] Build(Ball ball, TerrainSpec spec, bool[] continental)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (continental == null) throw new ArgumentNullException(nameof(continental));

		return Build(ball.CellDirs, ball.CellNeighbors, continental, spec.FreeboardSpanKm, spec.FreeboardLiftM);
	}

	/// <summary>
	/// 只依赖格心方向与邻接表的内核（不依赖 H3，可单测）。
	/// 偏置 = <c>liftM × smoothstep(0, spanKm, 距最近洋壳格的 km)</c>；洋壳格恒 0，全陆世界恒 0（没有海岸可言）。
	/// <paramref name="liftM"/> 或 <paramref name="spanKm"/> ≤ 0 ⇒ 全 0（关断）。
	/// </summary>
	public static float[] Build(Vector3[] dirs, int[][] neighbors, bool[] continental, float spanKm, float liftM)
	{
		if (dirs == null) throw new ArgumentNullException(nameof(dirs));
		if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
		if (continental == null) throw new ArgumentNullException(nameof(continental));

		int n = dirs.Length;
		if (continental.Length != n)
			throw new ArgumentException($"陆壳掩码长度 {continental.Length} ≠ 格数 {n}", nameof(continental));
		if (neighbors.Length != n)
			throw new ArgumentException($"邻接表长度 {neighbors.Length} ≠ 格数 {n}", nameof(neighbors));

		var lift = new float[n];
		if (!(liftM > 0f) || !(spanKm > 0f)) return lift;

		var seeds = new List<DistanceFieldOps.Seed>();
		for (int i = 0; i < n; i++)
		{
			if (continental[i]) continue;
			foreach (int neighbor in neighbors[i])
			{
				if (!continental[neighbor]) continue;
				seeds.Add(new DistanceFieldOps.Seed(i, i));
				break;
			}
		}
		if (seeds.Count == 0) return lift;

		// 截断半径取斜坡长：斜坡之外一律取截断值 ⇒ t = 1，正是满值，不必再走远。
		DistanceFieldOps.SeedField dist = DistanceFieldOps.BuildSeeds(dirs, neighbors, seeds, spanKm,
			(_, to) => continental[to]);
		for (int i = 0; i < n; i++)
		{
			if (!continental[i]) continue;
			lift[i] = liftM * MathOps.Smoothstep(0f, spanKm, dist.DistKm[i]);
		}

		return lift;
	}
}
