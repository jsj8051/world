using Godot;
using System;

namespace World.Tectonics
{
	/// <summary>
	/// 板块物理（tectonics.js Tectonophysics.js 的 C# 移植，2026-08-02，M2）。
	///
	/// 板块速度模型（Schellart 2010）：
	///   概念：板块像泡沫垫浮在水池，一侧挂铅块（俯冲板片）下沉，拖曳力平衡 → 终端速度。
	///   v = S·F·(WLT)^(2/3) / (18·c·μ)
	///   W/L/T = 俯冲带宽/长/厚，S = 形状参数，c = 板片倾角常数，μ = 地幔粘度
	///
	/// 流程（guess_plate_velocity）：
	///   1. buoyancy（浮力，N/m³，≤0）→ lateral_speed（rad/My）= buoyancy × 换算系数
	///   2. velocity = boundary_normal × lateral_speed（每格速度场）
	///   3. 刚性体拟合：速度场 → 绕质心角速度 + 绕世界中心角速度（加权平均）
	///   4. 旋转向量 × 时间 → 旋转矩阵（Matrix3x3.FromRotationVector）
	///
	/// 源码参考：docs/tectonics-ref/noncompiled/academics/Tectonophysics.js
	/// 常量来源：World.js material_viscosity/material_density（docs/tectonics-ref/noncompiled/models/World.js）
	/// </summary>
	public static class Tectonophysics
	{
		// Schellart 2010 常量（JS 原版硬编码，单位 m）
		const float Width = 300e3f;        // 俯冲带宽（m）
		const float Length = 600e3f;       // 俯冲带长（m）
		const float Thickness = 100e3f;    // 板片厚（m）
		const float ShapeParameter = 0.725f;
		const float SlabDipAngleConstant = 4.025f;
		const float WorldRadius = 6371e3f; // 地球半径（m）；内部物理标度固定（2026-08-10 统一为 6371，与存档默认一致；板块输出为 rad 角度格局，与星球实际半径无关）

		/// <summary>每牛顿的横向速度系数（rad/My per N），只依赖常量。
		/// ⚠️ 2026-08-02 标定：JS 原版缺 MEGAYEAR 因子导致速度比真实小 ~12 个数量级
		/// （板块不动，实测 disp 全程不变）。真实板块速度 ~2-10cm/年 ≈ 3e-3~1.6e-2 rad/My，
		/// 老洋壳负浮力 ~2200 N/m³ → k = 5e-3/2200 ≈ 2.3e-6。结构保留原版，仅修正量级。</summary>
		public static float LateralSpeedPerForce(float mantleViscosity)
		{
			float effectiveArea = Mathf.Pow(Thickness * Length * Width, 2f / 3f); // m²
			float k = effectiveArea / mantleViscosity / 18f;
			k *= ShapeParameter;
			k /= SlabDipAngleConstant;
			k /= WorldRadius;          // → rad/My per N
			k *= 1e12f;                // 标定：补 MEGAYEAR² 量级缺口（实测校准）
			return k;
		}

		/// <summary>
		/// 猜测板块速度场：velocity = boundary_normal × buoyancy × k。
		/// 对应 JS guess_plate_velocity。
		/// </summary>
		public static Vector3[] GuessPlateVelocity(
			SphereGrid grid, Vector3[] boundaryNormal, float[] buoyancy,
			float mantleViscosity, Vector3[] result)
		{
			float k = LateralSpeedPerForce(mantleViscosity);
			for (int i = 0; i < grid.VertexCount; i++)
				result[i] = boundaryNormal[i] * (buoyancy[i] * k);
			return result;
		}

		/// <summary>
		/// 板块质心（质量加权，球面）。对应 JS get_plate_center_of_mass。
		/// </summary>
		public static Vector3 GetPlateCenterOfMass(
			SphereGrid grid, float[] mass, byte[] mask)
		{
			Vector3 sum = Vector3.Zero;
			double wsum = 0;
			for (int i = 0; i < grid.VertexCount; i++)
			{
				if (mask[i] == 0) continue;
				sum += grid.Vertices[i] * mass[i];
				wsum += mass[i];
			}
			return wsum > 0 ? sum / (float)wsum : Vector3.Zero;
		}

		/// <summary>
		/// 板块旋转矩阵（刚性体拟合）。
		/// 对应 JS get_plate_rotation_matrix3x3。
		///
		/// 球面上刚体运动 = 绕世界中心旋转（线性运动）+ 绕板块质心旋转（角运动）。
		/// 每格速度场 → 两种角速度 → 加权平均（|v|>阈值 的格参与）→ 旋转向量 → 矩阵。
		/// </summary>
		public static float[] GetPlateRotationMatrix3x3(
			SphereGrid grid, Vector3[] plateVelocity, Vector3 centerOfPlate,
			float seconds)
		{
			int n = grid.VertexCount;
			// 绕质心角速度：ω_c = (v × (pos - c)) / |pos - c|²
			// 绕世界中心角速度：ω_w = v × pos（pos 是单位向量）
			Vector3 sumC = Vector3.Zero, sumW = Vector3.Zero;
			double wsum = 0;
			for (int i = 0; i < n; i++)
			{
				Vector3 v = plateVelocity[i];
				if (v.LengthSquared() < 3e-18f * 3e-18f) continue; // is_pulled 阈值
				Vector3 offset = grid.Vertices[i] - centerOfPlate;
				float d2 = offset.LengthSquared();
				if (d2 < 1e-12f) continue;
				Vector3 wc = v.Cross(offset) / d2;          // 绕质心角速度
				Vector3 ww = v.Cross(grid.Vertices[i]);     // 绕世界中心（单位半径）
				sumC += wc;
				sumW += ww;
				wsum += 1;
			}
			if (wsum < 1) return MatrixOps.Identity();

			Vector3 avgC = sumC / (float)wsum;
			Vector3 avgW = sumW / (float)wsum;
			Vector3 rotC = avgC * seconds;
			Vector3 rotW = avgW * seconds;

			var mC = MatrixOps.FromRotationVector(-rotC);
			var mW = MatrixOps.FromRotationVector(-rotW);
			var result = MatrixOps.MultMatrix(mC, mW);
			// NaN 防护（JS 原版：检测 NaN 回退 Identity）
			if (float.IsNaN(result[0])) return MatrixOps.Identity();
			return result;
		}

		/// <summary>mask 边界法线（mask 梯度归一化）。对应 Plate.boundary_normal Memo。</summary>
		public static Vector3[] GetBoundaryNormal(SphereGrid grid, byte[] mask, Vector3[] result)
		{
			// 用 FieldOps.Gradient（邻居差值）→ 归一化
			var maskF = new float[mask.Length];
			for (int i = 0; i < mask.Length; i++) maskF[i] = mask[i];
			FieldOps.Gradient(grid, maskF, result);
			for (int i = 0; i < result.Length; i++)
			{
				float len = result[i].Length();
				if (len > 1e-9f) result[i] /= len;
				else result[i] = Vector3.Zero;   // 板内部梯度≈0
			}
			return result;
		}

		/// <summary>
		/// 板块分割（Tectonophysics.guess_plate_map + VectorImageAnalysis.image_segmentation 移植，M3-4）。
		///
		/// 用软流圈角速度场做洪水填充分割：
		///   1. 从角速度最大（旋转最快）的未占用格开始（max_id in occupied）
		///   2. 沿邻居扩散（searched 防重），方向余弦 > cos60°(0.5) 才归组
		///   3. 相似格：magnitude 清零 + occupied 移除；不相似格保留（后续 start 可选）
		///   4. 每组 > minSegmentSize 才记为板块
		///
		/// ⚠️ 2026-08-02 修复：零向量 Normalized() → NaN → 填充瞬间停止（分割 0 块）。
		///   JS 里 similarity 对零向量也是 NaN，但 JS 数组操作不崩；C# 必须显式跳过。
		///
		/// 输出：plateMap（每格 → 板块 id，0=未占用）
		/// </summary>
		public static int[] GuessPlateMap(SphereGrid grid, Vector3[] angularVelocity, int segmentNum, int minSegmentSize)
		{
			int n = grid.VertexCount;
			var plateMap = new int[n];
			var occupied = new bool[n];
			Array.Fill(occupied, true);

			var magnitude = new float[n];
			for (int i = 0; i < n; i++) magnitude[i] = angularVelocity[i].Length();

			int maxIterations = 2 * segmentNum;
			int segment = 1;
			for (int j = 0; segment < segmentNum && j < maxIterations; j++)
			{
				// 未占用中 magnitude 最大处
				int start = -1;
				float maxMag = -1f;
				for (int i = 0; i < n; i++)
					if (occupied[i] && magnitude[i] > maxMag) { maxMag = magnitude[i]; start = i; }
				if (start < 0 || maxMag <= 1e-12f) break;

				// 洪水填充（magic_wand_select）
				var inSegment = new bool[n];
				var searched = new bool[n];
				var queue = new System.Collections.Generic.Queue<int>();
				queue.Enqueue(start);
				searched[start] = true;
				Vector3 startDir = angularVelocity[start] / maxMag;   // 起点方向（单位向量）

				while (queue.Count > 0)
				{
					int id = queue.Dequeue();
					// 相似度：与起点方向夹角余弦（零向量跳过 → 不相似）
					float len = magnitude[id];
					bool similar = false;
					if (len > 1e-12f)
						similar = angularVelocity[id].Dot(startDir) / len > 0.5f;   // cos60°
					if (!similar) continue;   // 不相似 = 墙，挡住洪水（不扩散）
					inSegment[id] = true;
					magnitude[id] = 0;    // 对应 fill_f32(magnitude, 0, segment)
					occupied[id] = false; // 对应 fill_ui8(occupied, 0, segment)
					// 只从相似格扩散（JS 原版：is_similar 才 push 邻居）
					foreach (int nb in grid.Neighbors[id])
					{
						if (!searched[nb])
						{
							searched[nb] = true;
							queue.Enqueue(nb);
						}
					}
				}

				// 计数并保存
				int count = 0;
				for (int i = 0; i < n; i++) if (inSegment[i]) count++;
				if (count > minSegmentSize)
				{
					for (int i = 0; i < n; i++)
						if (inSegment[i]) plateMap[i] = segment;
					segment++;
				}
			}

			// ── 后处理（原版 guess_plate_map step 2）──
			// 对每块板：膨胀 5 层 + closing（先膨胀后腐蚀）→ 填平空洞、平滑边界，
			// 与已占用区域取差避免重叠，填回。
			// 消除洪水填充未覆盖的空洞（否则板块图出现灰色无主区）。
			// ⚠️ 2026-08-02 修复：closing 必须 = dilate(5)+dilate(5)+erode(5)（净扩张 5 层）。
			//   旧实现 dilate(5)+erode(5) = 缩回原状 → 空洞没填 → ResetPlates 后
			//   未覆盖格 felsic=0 → Merge 后地形崩（maxDisp 2222→641m，land 5.9%）。
			var segments = new byte[n];
			var isOccupied = new byte[n];
			for (int i = 0; i < n; i++) isOccupied[i] = plateMap[i] > 0 ? (byte)1 : (byte)0;
			for (int pid = 1; pid <= segment - 1; pid++)
			{
				for (int i = 0; i < n; i++) segments[i] = plateMap[i] == pid ? (byte)1 : (byte)0;
				// 原版：dilation(5) 后 closing(5) = dilate(5)+dilate(5)+erode(5) = 净扩张 5 层
				var expanded = FieldOps.Dilate(grid, segments, 5);
				var closed = FieldOps.Erode(grid, FieldOps.Dilate(grid, expanded, 5), 5);
				// 与已占用取差（不覆盖其他板），填回并更新占用
				for (int i = 0; i < n; i++)
					if (closed[i] == 1 && isOccupied[i] == 0)
					{
						plateMap[i] = pid;
						isOccupied[i] = 1;
					}
			}

			// ── 兜底：剩余未覆盖格分配给最近板块（最近邻，BFS 多源）──
			// 净扩张 5 层仍可能留空洞（板块间缝隙宽时），最后一轮 BFS 从已占用
			// 格向外逐层扩散，保证 100% 覆盖（无灰色无主区）。
			int uncovered = 0;
			for (int i = 0; i < n; i++) if (plateMap[i] == 0) uncovered++;
			if (uncovered > 0)
			{
				var queue = new System.Collections.Generic.Queue<int>();
				var dist = new int[n];
				Array.Fill(dist, int.MaxValue);
				for (int i = 0; i < n; i++)
					if (plateMap[i] > 0) { queue.Enqueue(i); dist[i] = 0; }
				while (queue.Count > 0)
				{
					int id = queue.Dequeue();
					foreach (int nb in grid.Neighbors[id])
					{
						if (dist[nb] != int.MaxValue) continue;
						plateMap[nb] = plateMap[id];
						dist[nb] = dist[id] + 1;
						queue.Enqueue(nb);
					}
				}
			}
			GD.Print($"[Tectonics] 分割后处理: 空洞 {uncovered} 格 → BFS 兜底填平");
			return plateMap;
		}

		/// <summary>角速度场：ω = v × pos（对应 JS cross_vector_field(v, pos)）。</summary>
		public static void CrossToAngularVelocity(Vector3[] velocity, Vector3[] pos, Vector3[] result)
		{
			for (int i = 0; i < velocity.Length; i++)
				result[i] = velocity[i].Cross(pos[i]);
		}
	}
}
