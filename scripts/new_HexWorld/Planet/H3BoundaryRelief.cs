using System;
using System.Collections.Generic;
using Godot;
using World.Tectonics;                 // MaterialDensity

namespace World.NewHexWorld.Plate
{
	// 构造地形带（设计-04 批次 3；02 §5 参数化带 + testproject T5 的动态路线落地）。
	//
	// 终态一次施加（600 My 跑完后）：
	//   · **海沟**：汇聚边的洋侧（下盘）格——深度 ∝ 连续收敛率 C（无阈值，休眠边 C≈0 自然没沟），
	//     半余弦带衰减 TrenchBandCells 格；**走位移修饰**（模型无板片几何，沟 = 挠曲下弯的近似）。
	//   · **火山弧**：汇聚边的上盘侧**内陆偏移 ArcOffsetCells 格**（platec P3 的内陆增生思想 +
	//     testproject K_ARC）——**走物质**（加入火山质量，经均衡自然抬升出岛弧；守恒口径干净，
	//     质量记入创建账 ArcAddedMass）。上盘裁决：陆侧压洋侧；同为洋则慢者当上盘（老壳先潜）。
	//   · 弧物质含长英质成分 ⇒ 该格 IsLand 转 true ⇒ **大陆生长通道**（testproject T6 弧焊接）。
	// 施加后按主管线同序重算：均衡（含海平面二分）→ 挠曲 → 减海沟深度 → 复解海平面。
	public sealed class H3BoundaryRelief
	{
		// ── 常量（02 §5 带宽口径；判读后可调）──
		public const float TrenchDepthM = 2500f;            // 海沟轴深度（m；相对周边洋底）
		public const int TrenchBandCells = 2;               // 海沟带半宽（格）
		public static readonly float[] TrenchBandWeights = { 1f, 0.6f, 0.25f };   // 距轴 0/1/2 格权重
		public const float TrenchRateRefKmPerMy = 10f;      // 收敛率饱和参考（1 cm/yr）
		public const float ArcThicknessM = 2500f;           // 弧火山增厚（m；× 收敛率系数 × 带权重）
		public const int ArcOffsetCells = 2;                // 弧内陆偏移（格；板片倾角的粗近似）
		public static readonly float[] ArcBandWeights = { 0.5f, 1f, 0.5f };       // 距边 1/2/3 格权重
		public const float ArcFelsicFraction = 0.3f;        // 弧物质的长英质占比（安山质近似）
		public const float ConvergenceFloorKmPerMy = 2f;    // 汇聚活性下限（02 §3 惰性阈值同值）
		public const float EarthRadiusKm = 6371f;

		// ── 判读口 ──
		public double ArcAddedMass { get; private set; }    // 弧火山新增质量（kg/m² 口径；入创建账）
		public int TrenchAxisCells { get; private set; }
		public int ArcCells { get; private set; }
		public float[] TrenchDepthApplied;                   // 逐格实际下挖（m；判读/测试用）

		int[] _trenchDist;
		float[] _arcThicknessScratch;
		float[] _bandRate;

		/// <summary>终态施加构造地形带。改 fields（弧质量）与 isostasy.Displacement（海沟）。
		/// 速度场只读（终态刚体运动，H3PlateMotion.Velocity）。幂等前提：每代调用一次。</summary>
		public void Apply(Ball ball, H3PlateFields fields, Vector3[] velocity,
			H3Isostasy isostasy, MaterialDensity material)
		{
			int n = ball.CellIds.Length;
			var centers = ball.CellCenters;
			var neighbors = ball.CellNeighbors;
			var plateId = fields.PlateId;
			TrenchAxisCells = 0;
			ArcCells = 0;
			ArcAddedMass = 0;
			if (_trenchDist == null || _trenchDist.Length < n)
			{
				_trenchDist = new int[n];
				_arcThicknessScratch = new float[n];
				_bandRate = new float[n];
				TrenchDepthApplied = new float[n];
			}
			int[] trenchDist = _trenchDist;
			float[] arcScratch = _arcThicknessScratch;
			float[] bandRate = _bandRate;
			float[] trenchDepth = TrenchDepthApplied;
			for (int i = 0; i < n; i++)
			{
				trenchDist[i] = -1;
				arcScratch[i] = 0f;
				trenchDepth[i] = 0f;
				bandRate[i] = 0f;
			}

			// ── ① 逐异板边（j > i，序固定 ⇒ 确定性）：汇聚裁决 + 海沟轴（带轴率）+ 弧内陆链 ──
			var queue = new int[n];
			int queueTail = 0;
			for (int i = 0; i < n; i++)
			{
				int plateI = plateId[i];
				if (plateI < 0) continue;
				Vector3 centerI = centers[i];
				Vector3 radial = centerI.Normalized();
				foreach (int j in neighbors[i])
				{
					if (j <= i || plateId[j] == plateI || plateId[j] < 0) continue;

					// 连续收敛率 C（与 H3PlateBoundary 同口径：v_rel = v_i − v_j，n̂ = i→j，>0 汇聚）
					Vector3 delta = centers[j] - centerI;
					Vector3 tangential = delta - radial * delta.Dot(radial);
					if (tangential.LengthSquared() <= 1e-12f) continue;
					Vector3 normal = tangential.Normalized();
					float convergenceKmPerMy = (velocity[i] - velocity[j]).Dot(normal) * EarthRadiusKm;
					if (convergenceKmPerMy <= ConvergenceFloorKmPerMy) continue;   // 离散/走滑/休眠边：无沟无弧
					float rateScale = Math.Clamp(convergenceKmPerMy / TrenchRateRefKmPerMy, 0f, 1f);

					// 上盘裁决：陆压洋；同为洋 → 慢者当上盘（老壳先潜）
					bool landI = fields.IsLand(i), landJ = fields.IsLand(j);
					int upper, lower;
					if (landI != landJ) { upper = landI ? i : j; lower = landI ? j : i; }
					else if (velocity[i].LengthSquared() <= velocity[j].LengthSquared()) { upper = i; lower = j; }
					else { upper = j; lower = i; }

					// 海沟轴：下盘为洋才有沟（陆-陆碰撞走造山增厚，不在此）。轴率取该边收敛率的最大值。
					if (!fields.IsLand(lower))
					{
						if (trenchDist[lower] < 0)
						{
							trenchDist[lower] = 0;
							queue[queueTail++] = lower;
							TrenchAxisCells++;
						}
						bandRate[lower] = MathF.Max(bandRate[lower], rateScale);
					}

					// 弧内陆链：从上盘格背离边界走 ArcOffsetCells±1 格
					Vector3 inland = TangentialDirection(centers, upper, lower);
					int cell = upper;
					for (int d = 1; d <= ArcOffsetCells + 1; d++)
					{
						int next = H3PlateContact.FlowNeighbor(centers, neighbors, cell, inland);
						if (next < 0 || next == cell) break;
						cell = next;
						arcScratch[cell] += ArcThicknessM * ArcBandWeights[d - 1] * rateScale;
					}
				}
			}

			// ── ② 海沟带：从轴向洋侧 BFS TrenchBandCells 格（带内继承轴率；沟不延伸上陆）──
			int queueHead = 0;
			while (queueHead < queueTail)
			{
				int current = queue[queueHead++];
				if (trenchDist[current] >= TrenchBandCells) continue;
				foreach (int nb in neighbors[current])
				{
					if (trenchDist[nb] >= 0 || fields.IsLand(nb)) continue;
					trenchDist[nb] = trenchDist[current] + 1;
					bandRate[nb] = bandRate[current];
					queue[queueTail++] = nb;
				}
			}
			for (int i = 0; i < n; i++)
			{
				if (trenchDist[i] < 0) continue;
				trenchDepth[i] = MathF.Max(trenchDepth[i],
					TrenchDepthM * TrenchBandWeights[trenchDist[i]] * bandRate[i]);
			}

			// ── ③ 弧质量写入（走物质；叠加，不改归属/年龄）+ 均衡重算 + 海沟下挖 + 海平面复解 ──
			double addedMass = 0;
			int arcCells = 0;
			float maficDensity = material.MaficVolcanicMin;
			float felsicDensity = material.FelsicPlutonic;
			for (int i = 0; i < n; i++)
			{
				float thickness = arcScratch[i];
				if (thickness <= 0f) continue;
				float maficMass = thickness * (1f - ArcFelsicFraction) * maficDensity;
				float felsicMass = thickness * ArcFelsicFraction * felsicDensity;
				fields.MaficVolcanic[i] += maficMass;
				fields.FelsicVolcanic[i] += felsicMass;
				addedMass += maficMass + felsicMass;
				arcCells++;
			}
			ArcCells = arcCells;
			ArcAddedMass = addedMass;

			isostasy.ComputeDisplacement(ball, fields, material);      // 弧质量 → 均衡抬升（含海平面二分）
			isostasy.ApplyFlexure(ball);
			for (int i = 0; i < n; i++) isostasy.Displacement[i] -= trenchDepth[i];
			// 海沟下挖改变洋底体积 → 重解海平面；陆格位移必须跟着走（否则终态海拔整体偏掉这个差值）
			float previousSeaLevel = isostasy.SeaLevel;
			isostasy.SolveSeaLevelByVolume();
			isostasy.RebaseLandToSeaLevel(ball, fields, previousSeaLevel);
		}

		// u 格背离 v 的切向单位方向（内陆方向）。
		static Vector3 TangentialDirection(Vector3[] centers, int u, int v)
		{
			Vector3 delta = centers[u] - centers[v];
			Vector3 radial = centers[u].Normalized();
			Vector3 tangential = delta - radial * delta.Dot(radial);
			float length = tangential.Length();
			return length > 1e-12f ? tangential / length : Vector3.Zero;
		}
	}
}
