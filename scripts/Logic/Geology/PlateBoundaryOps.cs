using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）
using World.Data;            // Plate / 边界集 / 角色 / 类型
using World.H3Grid;          // Ball（H3 球壳数据层）

namespace World.Logic;

/// <summary>
/// 板块边界算子：由边界集（<see cref="PlateBoundarySet"/>）+ 板块表算逐边 / 逐格的运动与角色。
/// 几何（谁是边界、边两侧是谁）来自边界集，本类不再扫邻居判边界。
/// 「碰撞 / 分离」是**逐条边界**的性质，不是整块板块的性质——一块板块可以跟东邻撞、跟西邻裂。
/// 每条边界取两块速度在**边界法向**上的分量之差：&lt; 0 迎面（汇聚）｜&gt; 0 背向（张裂）｜≈ 0 走滑。
/// </summary>
public static class PlateBoundaryOps
{
	/// <summary>**活动边界的判定阈值**（法向相对速率，无量纲）：<c>|motion|</c> 超过它才算一条
	/// 活动的汇聚 / 张裂边界。山带（<see cref="HighlandOps"/>）与海底（<see cref="OceanDepthOps"/>）
	/// 共用这一份口径。</summary>
	public const float ActiveMotion = 0.25f;

	/// <summary>**强度归一尺度**：<c>strength = clamp(|motion| / 本值, 0, 1)</c>。
	/// 山带与海底共用这一份口径。</summary>
	public const float MotionScale = 0.9f;

	/// <summary>
	/// 逐格「到最近板块边界的跳数」：边界格 = 0，其余按邻接图 BFS 递推。
	/// </summary>
	public static int[] Hops(Ball ball, PlateBoundarySet boundaries)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		return LandMaskOps.HopsFrom(ball, boundaries.IsBoundaryCell);
	}

	/// <summary>
	/// 逐格**边界相对运动**（法向分量之差，正值 = 张裂 / 负值 = 汇聚）。
	/// 边界格取其全部边界边的**均值**，再按邻接图向内部扩散（多源 BFS 取先到者），于是整格场连续可用。
	///
	/// 法向 = 两格方向差在球面上的切向单位向量（指向对方）；
	/// 每块的逐点速度 = 刚体转动 `ω × 格心方向`（见 <see cref="TangentVelocity"/>）。
	/// </summary>
	/// <returns>逐格值：边界格 = 该处相对运动；内部格 = 最近边界格的值。</returns>
	public static float[] Motion(Ball ball, PlateBoundarySet boundaries, Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		int n = ball.CellDirs.Length;
		if (boundaries.CellEdgeStart.Length != n + 1)
			throw new ArgumentException("边界集与球壳格数不一致", nameof(boundaries));

		var dirs = ball.CellDirs;
		var neighbors = ball.CellNeighbors;
		var motion = new float[n];
		var settled = new bool[n];
		var queue = new System.Collections.Generic.Queue<int>(n);

		// ① 边界格：逐边求和取均值
		for (int i = 0; i < n; i++)
		{
			ReadOnlySpan<int> incident = boundaries.EdgesOfCell(i);
			if (incident.Length == 0) continue;

			float sum = 0f;
			foreach (int edgeIndex in incident)
			{
				PlateBoundaryEdge edge = boundaries.Edges[edgeIndex];
				int j = edge.OtherCell(i);
				int mine = edge.CellA == i ? edge.PlateA : edge.PlateB;
				int other = edge.CellA == i ? edge.PlateB : edge.PlateA;
				sum += MotionOfEdge(dirs[i], dirs[j], plates[mine], plates[other]);
			}
			motion[i] = sum / incident.Length;
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
	/// 逐格**汇聚边界角色**（四档之一；够不到汇聚边界的格 = <see cref="BoundaryRole.None"/>）。
	///
	/// ① 边界格先按「异板块邻居的**均值** &lt; 0」判汇聚（**与 <see cref="Motion"/> 同口径**），
	///    再取其中**最负**的一对 ⇒ 定出该格的**对侧板块号**；
	/// ② 把对侧板块号沿邻接图扩散，**只在同板块内扩散**（不跨边界 ⇒ 两侧角色天然分开）；
	/// ③ 逐格按「本方板块, 对侧板块」现算角色。
	/// </summary>
	/// <returns>逐格角色（全球覆盖——无汇聚邻接的格为 <see cref="BoundaryRole.None"/>）。</returns>
	public static BoundaryRole[] Roles(Ball ball, PlateBoundarySet boundaries, int[] plateOfCell, Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		var dirs = ball.CellDirs;
		var neighbors = ball.CellNeighbors;

		// ① 边界格定对侧板块号：取唯一位于汇聚侧（相对运动 < 0）、且最负的那一对
		var otherPlate = new int[n];
		Array.Fill(otherPlate, -1);
		var settled = new bool[n];
		var queue = new System.Collections.Generic.Queue<int>(n);
		for (int i = 0; i < n; i++)
		{
			ReadOnlySpan<int> incident = boundaries.EdgesOfCell(i);
			if (incident.Length == 0) continue;

			float sum = 0f;                           // 邻接对均值（与 Motion 同口径）
			float best = 0f;                          // 最负的一对 ⇒ 定对侧板块号
			int bestPlate = -1;
			foreach (int edgeIndex in incident)
			{
				PlateBoundaryEdge edge = boundaries.Edges[edgeIndex];
				int j = edge.OtherCell(i);
				int mine = edge.CellA == i ? edge.PlateA : edge.PlateB;
				int other = edge.CellA == i ? edge.PlateB : edge.PlateA;
				float m = MotionOfEdge(dirs[i], dirs[j], plates[mine], plates[other]);
				sum += m;
				if (m < best) { best = m; bestPlate = other; }
			}
			if (!(sum / incident.Length < 0f)) continue;   // 均值汇聚才算（否则与强度口径打架）
			otherPlate[i] = bestPlate;
			settled[i] = true;
			queue.Enqueue(i);
		}

		// ② 扩散（同板块内；多源 BFS 先到者胜 ⇒ 确定性）
		while (queue.Count > 0)
		{
			int i = queue.Dequeue();
			foreach (int j in neighbors[i])
			{
				if (settled[j]) continue;
				if (plateOfCell[j] != plateOfCell[i]) continue;   // 不跨边界
				otherPlate[j] = otherPlate[i];
				settled[j] = true;
				queue.Enqueue(j);
			}
		}

		// ③ 逐格现算角色
		var role = new BoundaryRole[n];
		for (int i = 0; i < n; i++)
		{
			int other = otherPlate[i];
			if (other < 0) continue;
			role[i] = RoleOf(plates[plateOfCell[i]], plates[other]);
		}
		return role;
	}

	/// <summary>
	/// 一对相邻板块在汇聚边界上**本方**的角色（见 <see cref="BoundaryRole"/>）：
	/// 陆×陆 ⇒ 密度小的当上盘、密度大的当下盘（**两块都成山**）；
	/// 陆×洋 ⇒ 陆侧恒为 <see cref="BoundaryRole.Arc"/>、洋侧恒为 <see cref="BoundaryRole.Slab"/>；
	/// 洋×洋 ⇒ 密度小的当上盘（成岛弧）、密度大的当下盘（成海沟）。
	/// </summary>
	public static BoundaryRole RoleOf(in Plate mine, in Plate other)
	{
		bool mineContinental = mine.Crust == CrustType.Continental;
		bool otherContinental = other.Crust == CrustType.Continental;
		bool mineRides = mine.Density < other.Density;

		if (mineContinental && otherContinental)
			return mineRides ? BoundaryRole.Upper : BoundaryRole.Lower;
		if (mineContinental != otherContinental)
			return mineContinental ? BoundaryRole.Arc : BoundaryRole.Slab;
		return mineRides ? BoundaryRole.Arc : BoundaryRole.Slab;
	}

	/// <summary>一对板块各侧的角色（<see cref="RoleOf"/> 的对称入口）。</summary>
	public static (BoundaryRole a, BoundaryRole b) PairRoles(in Plate a, in Plate b)
		=> (RoleOf(a, b), RoleOf(b, a));

	/// <summary>
	/// 一对相邻格的**法向相对运动**（B 相对 A）：
	/// <c>(v_B − v_A) · n̂</c>，<c>n̂</c> = A→B 的球面切向单位向量。
	/// 负 = 迎面相撞（汇聚）｜正 = 背向拉开（张裂）｜≈ 0 = 平行错动（走滑）。
	/// </summary>
	public static float MotionOfEdge(Vector3 dirA, Vector3 dirB, in Plate plateA, in Plate plateB)
	{
		// 切向单位向量：球面上从 A 指向 B 的方向（去掉径向分量后归一化）
		var normal = dirB - dirA * dirA.Dot(dirB);
		if (normal.LengthSquared() < 1e-12f) return 0f;   // 同格或对径点，无切向
		normal = normal.Normalized();

		return TangentVelocity(dirB, plateB).Dot(normal) - TangentVelocity(dirA, plateA).Dot(normal);
	}

	/// <summary>按法向相对速率分档；阈值与活动边界共用 <see cref="ActiveMotion"/>。</summary>
	public static BoundaryKind KindOf(float motion)
	{
		if (!(MathF.Abs(motion) > ActiveMotion)) return BoundaryKind.Transform;
		return motion < 0f ? BoundaryKind.Convergent : BoundaryKind.Divergent;
	}

	/// <summary>
	/// 板块在**该点**的切向速度：刚体转动 <c>v = ω × dir</c>（结果自动与 dir 正交）。
	/// 同一板块各格共用同一个 ω ⇒ 角速度一致、线速度随位置变。
	/// </summary>
	static Vector3 TangentVelocity(Vector3 dir, in Plate plate) => plate.Omega.Cross(dir);
}
