using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // Plate（板块载体）
using World.H3Grid;          // Ball（H3 球壳数据层）

namespace World.Logic;

/// <summary>
/// 板块边界算子：由逐格板块号 + 板块速度，算每条边界的**相对运动**，并把结果铺到逐格上。
///
/// 「碰撞 / 分离」是**逐条边界**的性质，不是整块板块的性质——一块板块可以跟东邻撞、跟西邻裂。
/// 每条边界取两块速度在**边界法向**上的分量之差：&lt; 0 迎面（汇聚）｜&gt; 0 背向（张裂）｜≈ 0 走滑。
/// </summary>
public static class PlateBoundaryOps
{
	/// <summary>
	/// 逐格判定「是不是板块边界」及**到边界的跳数**。
	/// 边界格 = 存在邻格属于别的板块。返回 <c>hops</c>（边界格 = 0；−1 表示未连通到任何边界）。
	/// </summary>
	public static int[] BoundaryHops(Ball ball, int[] plateOfCell)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		var neighbors = ball.CellNeighbors;
		var isBoundary = new bool[n];
		for (int i = 0; i < n; i++)
		{
			int p = plateOfCell[i];
			foreach (int j in neighbors[i])
				if (plateOfCell[j] != p) { isBoundary[i] = true; break; }
		}
		return LandMaskOps.HopsFrom(ball, i => isBoundary[i]);
	}

	/// <summary>
	/// 逐格**边界相对运动**（法向分量之差，无量纲，正值 = 张裂 / 负值 = 汇聚）。
	/// 只对边界格的邻接对投票、逐格取其邻接对的**均值**（同格的邻居可能分属不同的对），
	/// 再按邻接图向内部扩散（多源 BFS 取先到者），于是整格场连续可用。
	///
	/// 法向 = 两格方向差在球面上的切向单位向量（指向对方）；
	/// 每块速度的球面切向分量 = `VTheta * e_θ + VPhi * sinθ * e_φ`（`VPhi` 存的是 φ̇，需乘 sinθ 转成切向速率）。
	/// </summary>
	/// <returns>逐格值：边界格 = 该处相对运动；内部格 = 最近边界格的值。</returns>
	public static float[] BoundaryMotion(Ball ball, int[] plateOfCell, Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		var dirs = ball.CellDirs;
		var neighbors = ball.CellNeighbors;
		var motion = new float[n];
		var settled = new bool[n];
		var queue = new System.Collections.Generic.Queue<int>(n);

		// ① 边界格投票：逐邻接对算相对运动，取均值
		for (int i = 0; i < n; i++)
		{
			int p = plateOfCell[i];
			float sum = 0f;
			int cnt = 0;
			foreach (int j in neighbors[i])
			{
				if (plateOfCell[j] == p) continue;
				sum += RelativeMotion(dirs[i], dirs[j], plates[p], plates[plateOfCell[j]]);
				cnt++;
			}
			if (cnt == 0) continue;
			motion[i] = sum / cnt;
			settled[i] = true;
			queue.Enqueue(i);
		}

		// ② 向内部扩散（多源 BFS，先到者胜 ⇒ 确定性）
		while (queue.Count > 0)
		{
			int i = queue.Dequeue();
			foreach (int j in neighbors[i])
			{
				if (settled[j]) continue;
				motion[j] = motion[i];
				settled[j] = true;
				queue.Enqueue(j);
			}
		}
		return motion;
	}

	/// <summary>
	/// 一对相邻格的**法向相对运动**（B 相对 A）：
	/// <c>(v_B − v_A) · n̂</c>，<c>n̂</c> = A→B 的球面切向单位向量。
	/// 负 = 迎面相撞（汇聚）｜正 = 背向拉开（张裂）｜≈ 0 = 平行错动（走滑）。
	/// </summary>
	static float RelativeMotion(Vector3 dirA, Vector3 dirB, in Plate plateA, in Plate plateB)
	{
		// 切向单位向量：球面上从 A 指向 B 的方向（去掉径向分量后归一化）
		var normal = dirB - dirA * dirA.Dot(dirB);
		if (normal.LengthSquared() < 1e-12f) return 0f;   // 同格或对径点，无切向
		normal = normal.Normalized();

		return TangentVelocity(dirB, plateB).Dot(normal) - TangentVelocity(dirA, plateA).Dot(normal);
	}

	/// <summary>
	/// 板块速度在**该点**的球面切向分量（单位球上的角速率向量）：
	/// <c>v = VTheta · e_θ + VPhi · sinθ · e_φ</c>。
	/// `VPhi` 存的是 φ̇（方位角角速率），乘 sinθ 才是真正的切向速率——极点处 sinθ → 0，天然收敛。
	/// </summary>
	static Vector3 TangentVelocity(Vector3 dir, in Plate plate)
	{
		// 球面基：e_θ 沿经线向南（θ 增大方向），e_φ 沿纬线向东
		float theta = MathF.Acos(Math.Clamp(dir.Y, -1f, 1f));   // 极角
		float sinTheta = MathF.Sin(theta);
		float cosTheta = MathF.Cos(theta);
		float sinPhi = dir.Z, cosPhi = dir.X;
		float phi = MathF.Atan2(sinPhi, cosPhi);
		float cosPhi2 = MathF.Cos(phi), sinPhi2 = MathF.Sin(phi);

		var eTheta = new Vector3(cosTheta * cosPhi2, -sinTheta, cosTheta * sinPhi2);   // θ̂
		var ePhi = new Vector3(-sinPhi2, 0f, cosPhi2);                                  // φ̂

		return plate.VTheta * eTheta + plate.VPhi * sinTheta * ePhi;
	}
}
