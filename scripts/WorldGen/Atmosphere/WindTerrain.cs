using System;
using World.Spatial;        // Ball / Vector3（只读引用既有事实，不生成）

namespace World.WorldGen;

// 世界生成空间 · **地形/海岸输入视图（⑯ WindField 批次 3 地形 + 批次 4 季风输入）**——设计 §4.4/§4.5。
//
//   ★性质：既有事实的**只读引用视图**（W2 合规：HeightM 来自 `HeightComposer.HeightM`，
//     邻接来自 `Ball.CellNeighbors`，方向向量来自 `Ball.CellDirs`，距离来自 `FinalGeography
//     .FinalDistToCoast/Land`）——不生成、不缓存、不复制任何新事实（★批次 4 硬线④：
//     BFS 距离只能被消费；`DistToCoastHops` 等就是调用方传入的同一数组实例）。
//   ★方向向量口径（钉死）：`X = cosφ·cosλ, Y = sinφ, Z = cosφ·sinλ`（= `CoordUtil.LatLngToSphere`
//     归一化；asin(Y) = 纬度）。罗盘方位角推导：北切向 = normalize(N − (N·d)d)，N = (0,1,0)；
//     东切向 = normalize(cross(d, 北切向))（λ 增 = +Z = 正东）；bearing = atan2(v·东, v·北)。
//   ★海陆口径：HeightM &gt; 0 = 陆（海格 ≤ 0）——调用方传入的距离事实必须与此同口径
//     （生产 = `FinalLand` 派生的 `FinalDistToCoast/Land`）。
public sealed class WindTerrain
{
	/// <summary>逐格最终高程（米；海格 ≤ 0——屏障判定无需海陆分支）。</summary>
	public float[] HeightM { get; }

	/// <summary>六邻接表（与 `Ball.CellNeighbors` 同构）。</summary>
	public int[][] Neighbors { get; }

	/// <summary>格心单位方向向量分量（口径见类头；asin(Y) = 纬度）。</summary>
	public float[] DirX { get; }
	public float[] DirY { get; }
	public float[] DirZ { get; }

	/// <summary>
	/// 离岸距离（hop；**只读引用** `FinalGeography.FinalDistToCoast`：陆格 ≥ 1、海格 = 0）。
	/// ★null = 无季风输入 ⇒ 季风机制整体关闭（输出与无季风版逐位相等，D2 天然态）。
	/// </summary>
	public int[] DistToCoastHops { get; }

	/// <summary>
	/// 离陆距离（hop；**只读引用** `FinalGeography.FinalDistToLand`：海格 ≥ 1、陆格 = 0）。
	/// ★null = 无季风输入（同上）。
	/// </summary>
	public int[] DistToLandHops { get; }

	/// <summary>
	/// 拓扑跳数 → km 的换算（**I6 权威口径**：`√3 × SpatialScale.Of(ball).CellEdgeKm`；res4 ≈ 45.2 km）。
	/// ★0 = 未知（直接构造且未传）⇒ 禁用季风（防 hop×常数 之类野口径混入——永久原则 2）。
	/// </summary>
	public double KmPerHop { get; }

	public WindTerrain(float[] heightM, int[][] neighbors, float[] dirX, float[] dirY, float[] dirZ,
		int[] distToCoastHops = null, int[] distToLandHops = null, double kmPerHop = 0)
	{
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
		int n = heightM.Length;
		if (neighbors.Length != n) throw new ArgumentException("neighbors 长度须与 heightM 一致", nameof(neighbors));
		if (dirX == null || dirY == null || dirZ == null ||
			dirX.Length != n || dirY.Length != n || dirZ.Length != n)
			throw new ArgumentException("方向向量分量长度须与 heightM 一致");
		if (distToCoastHops != null && distToCoastHops.Length != n)
			throw new ArgumentException("distToCoastHops 长度须与 heightM 一致", nameof(distToCoastHops));
		if (distToLandHops != null && distToLandHops.Length != n)
			throw new ArgumentException("distToLandHops 长度须与 heightM 一致", nameof(distToLandHops));
		HeightM = heightM;
		Neighbors = neighbors;
		DirX = dirX;
		DirY = dirY;
		DirZ = dirZ;
		DistToCoastHops = distToCoastHops;
		DistToLandHops = distToLandHops;
		KmPerHop = kmPerHop;
	}

	/// <summary>由 Ball 装配（生产路径；heightM = `HeightComposer.HeightM`）。</summary>
	public static WindTerrain FromBall(Ball ball, float[] heightM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		int n = ball.CellDirs.Length;
		if (heightM == null || heightM.Length != n)
			throw new ArgumentException($"heightM 长度须为 {n}（= ball.CellDirs.Length）", nameof(heightM));
		CollectDirs(ball, out var dx, out var dy, out var dz);
		return new WindTerrain(heightM, ball.CellNeighbors, dx, dy, dz);
	}

	/// <summary>
	/// 由 Ball 装配 + 季风距离事实（批次 4；dist* = `FinalGeography.FinalDistToCoast/Land` 原数组引用）。
	/// KmPerHop 由 I6 权威口径自动换算（`√3 × CellEdgeKm`）。
	/// </summary>
	public static WindTerrain FromBall(Ball ball, float[] heightM, int[] distToCoastHops, int[] distToLandHops)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		int n = ball.CellDirs.Length;
		if (heightM == null || heightM.Length != n)
			throw new ArgumentException($"heightM 长度须为 {n}（= ball.CellDirs.Length）", nameof(heightM));
		CollectDirs(ball, out var dx, out var dy, out var dz);
		double kmPerHop = Math.Sqrt(3.0) * SpatialScale.Of(ball).CellEdgeKm;
		return new WindTerrain(heightM, ball.CellNeighbors, dx, dy, dz, distToCoastHops, distToLandHops, kmPerHop);
	}

	static void CollectDirs(Ball ball, out float[] dx, out float[] dy, out float[] dz)
	{
		int n = ball.CellDirs.Length;
		dx = new float[n];
		dy = new float[n];
		dz = new float[n];
		var dirs = ball.CellDirs;
		for (int i = 0; i < n; i++)
		{
			var d = dirs[i].Normalized();          // 防御性归一化（构造期已归一，此处保口径）
			dx[i] = d.X;
			dy[i] = d.Y;
			dz[i] = d.Z;
		}
	}
}
