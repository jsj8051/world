using System;
using System.Collections.Generic;
using Godot;
using World.H3Grid;
using World.Utils;
using World.Utils.H3;

namespace World.Logic;

/// <summary>
/// 粗层（父格）视图：细层格 → 粗层格的映射，外加粗层自己的邻接 / 方向 / 格距 / 经纬度。
/// 长波平滑只在粗层做（res4 上 650 km 核要 27 跳，邻居数放不下），上采样与收尾平滑归气压模型。
///
/// 测试用 <see cref="Identity"/>：同一条卷积代码在合成网格上按细层口径跑，不必碰 H3。
///
/// @example
/// <code>
/// var coarse = CoarseLayer.Parent(ball, 2);      // res4 → res2
/// var identity = CoarseLayer.Identity(grid);     // 合成网格
/// </code>
/// </summary>
public sealed class CoarseLayer
{
	/// <summary>粗层格数。</summary>
	public int Count { get; }

	/// <summary>细层格 → 粗层格的下标（长度 = 细层格数）。</summary>
	public int[] CoarseOfCell { get; }

	/// <summary>粗层邻接表（下标为粗层下标）。</summary>
	public int[][] Neighbors { get; }

	/// <summary>粗层格心单位方向。</summary>
	public Vector3[] Dirs { get; }

	/// <summary>粗层格心纬度（度；北正南负）。</summary>
	public float[] LatDeg { get; }

	/// <summary>粗层格心经度（度；−180…180）。</summary>
	public float[] LonDeg { get; }

	/// <summary>粗层相邻格中心距（km；跳数 ⇄ 距离的唯一折算）。</summary>
	public double NeighborKm { get; }

	/// <summary>世界半径（km；大圆距离口径）。</summary>
	public float RadiusKm { get; }

	CoarseLayer(int count, int[] coarseOfCell, int[][] neighbors, Vector3[] dirs,
		double neighborKm, float radiusKm)
	{
		Count = count;
		CoarseOfCell = coarseOfCell;
		Neighbors = neighbors;
		Dirs = dirs;
		NeighborKm = neighborKm;
		RadiusKm = radiusKm;
		LatDeg = new float[count];
		LonDeg = new float[count];
		for (int i = 0; i < count; i++)
		{
			LatDeg[i] = MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) * 180f / MathF.PI;
			LonDeg[i] = MathF.Atan2(dirs[i].Z, dirs[i].X) * 180f / MathF.PI;
		}
	}

	/// <summary>恒等层（粗层 = 细层）：测试网格与"不需要粗化"的小世界用，卷积退化为细层口径。</summary>
	public static CoarseLayer Identity(ClimateGrid grid)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		int n = grid.Count;
		var map = new int[n];
		for (int i = 0; i < n; i++) map[i] = i;
		return new CoarseLayer(n, map, grid.Neighbors, grid.Dirs, grid.NeighborKm, grid.RadiusKm);
	}

	/// <summary>由球壳建父格层（<paramref name="targetRes"/> 必须小于球壳 res）。</summary>
	public static CoarseLayer Parent(Ball ball, int targetRes)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (targetRes < 0 || targetRes >= ball.Res)
			throw new ArgumentOutOfRangeException(nameof(targetRes), $"父格 res {targetRes} 必须小于球壳 res {ball.Res}");

		int n = ball.CellIds.Length;
		var parentOfCellIds = new ulong[n];
		var distinct = new HashSet<ulong>();
		for (int i = 0; i < n; i++)
		{
			ulong parent = H3.CellToParent(ball.CellIds[i], targetRes);
			parentOfCellIds[i] = parent;
			distinct.Add(parent);
		}

		// 按 id 排序 ⇒ 粗层下标与构建顺序无关（同一个球壳永远得到同一份索引）。
		var parentIds = new List<ulong>(distinct);
		parentIds.Sort();
		int count = parentIds.Count;
		var indexOfParent = new Dictionary<ulong, int>(count);
		for (int i = 0; i < count; i++) indexOfParent[parentIds[i]] = i;

		var coarseOfCell = new int[n];
		for (int i = 0; i < n; i++) coarseOfCell[i] = indexOfParent[parentOfCellIds[i]];

		var dirs = new Vector3[count];
		for (int i = 0; i < count; i++)
			dirs[i] = CoordUtil.LatLngToSphere(H3.CellToLatLng(parentIds[i]), 1f);

		var neighbors = new int[count][];
		for (int i = 0; i < count; i++)
		{
			ulong[] disk = H3.GridDisk(parentIds[i], 1);
			var list = new int[Math.Max(0, disk.Length - 1)];
			int k = 0;
			foreach (ulong nb in disk)
			{
				if (nb == parentIds[i]) continue;
				list[k++] = indexOfParent[nb];
			}
			Array.Sort(list);   // 确定性（与 H3 输出顺序无关）
			neighbors[i] = list;
		}

		float radiusKm = SphericalFbmNoise.WorldRadiusKm;
		return new CoarseLayer(count, coarseOfCell, neighbors, dirs,
			AverageNeighborKm(count, neighbors, dirs, radiusKm), radiusKm);
	}

	/// <summary>粗层格心距（km）：取所有有向边的算术平均，避免单条五边形边影响核半径。</summary>
	static double AverageNeighborKm(int count, int[][] neighbors, Vector3[] dirs, float radiusKm)
	{
		double sum = 0;
		long pairs = 0;
		for (int i = 0; i < count; i++)
		{
			foreach (int nb in neighbors[i])
			{
				sum += GreatCircleKm(dirs[i], dirs[nb], radiusKm);
				pairs++;
			}
		}
		return pairs > 0 ? sum / pairs : 1.0;
	}

	/// <summary>大圆距离（km）。</summary>
	public static float GreatCircleKm(Vector3 a, Vector3 b, float radiusKm)
	{
		float dot = Math.Clamp(a.Dot(b), -1f, 1f);
		return MathF.Acos(dot) * radiusKm;
	}
}
