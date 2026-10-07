using System;
using World.Spatial;        // Ball / Vector3（只读引用既有事实，不生成）

namespace World.WorldGen;

// 世界生成空间 · **地形输入视图（⑯ WindField 批次 3）**——设计 §4.4（W-M4 大尺度阻挡/绕流）。
//
//   ★性质：既有事实的**只读引用视图**（W2 合规：HeightM 来自 `HeightComposer.HeightM`，
//     邻接来自 `Ball.CellNeighbors`，方向向量来自 `Ball.CellDirs`）——不生成、不缓存任何新事实。
//   ★方向向量口径（钉死）：`X = cosφ·cosλ, Y = sinφ, Z = cosφ·sinλ`（= `CoordUtil.LatLngToSphere`
//     归一化；asin(Y) = 纬度）。罗盘方位角推导：北切向 = normalize(N − (N·d)d)，N = (0,1,0)；
//     东切向 = normalize(cross(d, 北切向))（λ 增 = +Z = 正东）；bearing = atan2(v·东, v·北)。
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

	public WindTerrain(float[] heightM, int[][] neighbors, float[] dirX, float[] dirY, float[] dirZ)
	{
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
		int n = heightM.Length;
		if (neighbors.Length != n) throw new ArgumentException("neighbors 长度须与 heightM 一致", nameof(neighbors));
		if (dirX == null || dirY == null || dirZ == null ||
			dirX.Length != n || dirY.Length != n || dirZ.Length != n)
			throw new ArgumentException("方向向量分量长度须与 heightM 一致");
		HeightM = heightM;
		Neighbors = neighbors;
		DirX = dirX;
		DirY = dirY;
		DirZ = dirZ;
	}

	/// <summary>由 Ball 装配（生产路径；heightM = `HeightComposer.HeightM`）。</summary>
	public static WindTerrain FromBall(Ball ball, float[] heightM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		int n = ball.CellDirs.Length;
		if (heightM == null || heightM.Length != n)
			throw new ArgumentException($"heightM 长度须为 {n}（= ball.CellDirs.Length）", nameof(heightM));
		var dirs = ball.CellDirs;
		var dx = new float[n];
		var dy = new float[n];
		var dz = new float[n];
		for (int i = 0; i < n; i++)
		{
			var d = dirs[i].Normalized();          // 防御性归一化（构造期已归一，此处保口径）
			dx[i] = d.X;
			dy[i] = d.Y;
			dz[i] = d.Z;
		}
		return new WindTerrain(heightM, ball.CellNeighbors, dx, dy, dz);
	}
}
