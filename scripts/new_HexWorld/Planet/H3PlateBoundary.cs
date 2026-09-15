using System;
using System.Collections.Generic;
using Godot;
using World.Utils;

namespace World.NewHexWorld.Plate
{
	// 板缘类型（设计-03；沿用 02 §3 的运动学分类口径，但输入从"抽的 Ω"换成**动态模拟的真实速度场**）。
	public enum PlateBoundaryKind
	{
		Inert = 0,        // 惰性（两板近乎同动，无相对运动）
		Convergent = 1,   // 汇聚
		Divergent = 2,    // 离散
		Transform = 3,    // 转换（相对速度近乎平行边界）
	}

	// 逐格板缘分类（设计-03 §6）：只读**动态模拟的产物**（每格归属板 + 逐格速度），
	// 给 UI 格信息行，并作为后续批次「02 参数化地形带（海沟/弧）」的挂载口（03 §5 改进 8 的留口）。
	//
	// 与 02 的 `PlateBoundary` 的区别：02 那边分类的是"抽出来的欧拉极 Ω"（静态、一次定局）；
	// 这边分类的是**每一步真实算出来的速度场**——所以板缘类型会随时间变化（汇聚边能转成转换边），
	// 这是动态路线的应有行为。
	public sealed class H3PlateBoundary
	{
		public const float CosTransformBand = 0.20f;      // |cosα| 小于此 → 转换（02 §3 同值）
		public const float InertSpeedKmPerMy = 2f;        // 惰性阈值（02 §3 同值：0.2 cm/yr）
		public const float EarthRadiusKm = 6371f;

		public PlateBoundaryKind[] Kind;                  // 每格的主导板缘类型
		public float[] RelativeSpeedKmPerMy;              // 每格主导边的相对速率
		/// <summary>每格主导边的**带符号连续收敛率**（km/My；>0 = 汇聚，<0 = 离散；04 批次 3）。
		/// 效应类消费者（海沟/弧地形带）读连续量，不用离散四分类——机制上避开
		/// "速度低于阈值 → 无边界 → 无效应"的死锁（testproject T4 的无阈值思想）。</summary>
		public float[] ConvergenceKmPerMy;
		/// <summary>每格主导边的切向（走滑）速率幅值（km/My；04 批次 3）。</summary>
		public float[] TangentialKmPerMy;
		public int[] NearestBoundaryDistanceCells;        // 距最近板缘格数（板内格用；板缘格 = 0）
		public PlateBoundaryKind[] NearestBoundaryKind;   // 最近板缘的类型

		public int BoundaryCellCount { get; private set; }
		public int EdgeCount { get; private set; }
		public int[] KindEdgeCounts { get; private set; } = new int[4];   // 四分类边数（判读用）

		readonly int _cellCount;
		readonly float[] _dominantNormalSpeed;

		public H3PlateBoundary(int cellCount)
		{
			_cellCount = cellCount;
			Kind = new PlateBoundaryKind[cellCount];
			RelativeSpeedKmPerMy = new float[cellCount];
			ConvergenceKmPerMy = new float[cellCount];
			TangentialKmPerMy = new float[cellCount];
			NearestBoundaryDistanceCells = new int[cellCount];
			NearestBoundaryKind = new PlateBoundaryKind[cellCount];
			_dominantNormalSpeed = new float[cellCount];
		}

		/// <summary>按当前归属板 + 速度场重算逐格板缘分类（每步调用；O(N)）。</summary>
		public void Build(Ball ball, H3PlateFields fields, Vector3[] cellVelocity)
		{
			int n = ball.CellIds.Length;
			var centers = ball.CellCenters;
			var neighbors = ball.CellNeighbors;
			var plateId = fields.PlateId;

			Array.Clear(Kind, 0, n);
			Array.Clear(RelativeSpeedKmPerMy, 0, n);
			Array.Clear(_dominantNormalSpeed, 0, n);
			Array.Clear(KindEdgeCounts, 0, 4);
			BoundaryCellCount = 0;
			EdgeCount = 0;

			// ① 逐边分类（异板邻居对，每条只处理一次：j > i）
			for (int i = 0; i < n; i++)
			{
				int plateI = plateId[i];
				if (plateI < 0) continue;
				Vector3 centerI = centers[i];
				Vector3 radial = centerI.Normalized();
				foreach (int j in neighbors[i])
				{
					if (j <= i || plateId[j] == plateI || plateId[j] < 0) continue;
					EdgeCount++;

					// 边界法向 n̂：i → j，投影到切平面（纯径向差在球面上不代表"朝外"）
					Vector3 delta = centers[j] - centerI;
					Vector3 tangential = delta - radial * delta.Dot(radial);
					Vector3 normal = tangential.LengthSquared() > 1e-12f ? tangential.Normalized() : delta.Normalized();

					// 相对速度（rad/My → km/My）：**02 §2.3 约定 v_rel = v_i − v_j，v_n > 0 ⟺ 相向 ⟺ 汇聚**
					// ⚠️ 2026-09-11 符号修正：原写成 `cellVelocity[j] - cellVelocity[i]`（= v_j − v_i），
					// 于是"汇聚"被判成 Divergent、"离散"被判成 Convergent —— UI 格信息行的板缘类型一直是反的。
					Vector3 relative = cellVelocity[i] - cellVelocity[j];
					float speedKmPerMy = relative.Length() * EarthRadiusKm;
					float normalSpeedKmPerMy = relative.Dot(normal) * EarthRadiusKm;
					float tangentialKmPerMy = MathF.Sqrt(MathF.Max(
						speedKmPerMy * speedKmPerMy - normalSpeedKmPerMy * normalSpeedKmPerMy, 0f));
					PlateBoundaryKind kind = Classify(speedKmPerMy, normalSpeedKmPerMy);
					KindEdgeCounts[(int)kind]++;

					AssignDominant(i, kind, normalSpeedKmPerMy, speedKmPerMy, tangentialKmPerMy);
					AssignDominant(j, kind, normalSpeedKmPerMy, speedKmPerMy, tangentialKmPerMy);
				}
			}

			// ② 距最近板缘的距离/类型（板缘格 d = 0；多源 BFS，队列序固定 ⇒ 确定性）
			for (int i = 0; i < n; i++) NearestBoundaryDistanceCells[i] = -1;
			var queue = new Queue<int>();
			for (int i = 0; i < n; i++)
			{
				if (_dominantNormalSpeed[i] <= 0f) continue;    // 板内格（无支配边）
				BoundaryCellCount++;
				NearestBoundaryDistanceCells[i] = 0;
				NearestBoundaryKind[i] = Kind[i];
				queue.Enqueue(i);
			}
			while (queue.Count > 0)
			{
				int current = queue.Dequeue();
				foreach (int next in neighbors[current])
				{
					if (NearestBoundaryDistanceCells[next] >= 0) continue;
					NearestBoundaryDistanceCells[next] = NearestBoundaryDistanceCells[current] + 1;
					NearestBoundaryKind[next] = NearestBoundaryKind[current];
					queue.Enqueue(next);
				}
			}
		}

		// 逐格取主导边（|v_n| 最大者；平局保先到者 —— 遍历序固定 ⇒ 确定性）。顺带记带符号
		// 收敛率与切向率（04 批次 3 连续速率场）。
		void AssignDominant(int cell, PlateBoundaryKind kind, float normalSpeedKmPerMy,
			float speedKmPerMy, float tangentialKmPerMy)
		{
			float magnitude = MathF.Abs(normalSpeedKmPerMy);
			if (_dominantNormalSpeed[cell] > 0f && magnitude <= _dominantNormalSpeed[cell]) return;
			_dominantNormalSpeed[cell] = magnitude;
			Kind[cell] = kind;
			RelativeSpeedKmPerMy[cell] = speedKmPerMy;
			ConvergenceKmPerMy[cell] = normalSpeedKmPerMy;
			TangentialKmPerMy[cell] = tangentialKmPerMy;
		}

		static PlateBoundaryKind Classify(float speedKmPerMy, float normalSpeedKmPerMy)
		{
			if (speedKmPerMy < InertSpeedKmPerMy) return PlateBoundaryKind.Inert;
			float cosine = speedKmPerMy > 1e-9f ? normalSpeedKmPerMy / speedKmPerMy : 0f;
			if (cosine >= CosTransformBand) return PlateBoundaryKind.Convergent;
			if (cosine <= -CosTransformBand) return PlateBoundaryKind.Divergent;
			return PlateBoundaryKind.Transform;
		}
	}
}
