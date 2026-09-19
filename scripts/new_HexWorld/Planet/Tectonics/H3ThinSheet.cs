using System;
using Godot;

namespace World.NewHexWorld.Plate
{
	// 粘塑性薄席速度解算器（设计-07 P1 核心）：B 方案 §4 的落地。
	//
	// 未知量 = 逐格切向速度（格心切面 2 分量，格内基 tu/tv）；方程 = 逐边粘性耦合 + GPE 梯度体力
	// + 可选牵引（板片账户的 P2 挂载口）：
	//    Σ_edges K_ij·(v_j − v_i)_切 = F_i ，   K_ij = 2·μ_yr·h·width/d ，   F_i = −∇E_GPE·A_i
	// 塑性 = 逐边应力帽：边缘力 |K·Δv| 超过整合屈服力 Y ⇒ 削减该边 K（粘度帽法，避免显式屈服面）——
	// 屈服边缘可滑移、未屈服边缘近似刚性，造山带/转换断层的"涌现"入口（P3 接变形加厚与损伤）。
	//
	// 诚实登记的离散化简化（游戏档）：
	//   · 粘性弹簧网络只保留剪切主项，不含完整张量结构与压力泊松项——薄席方程的降阶近似；
	//   · 屈服判据用整合力（N/m），不是逐深度屈服包络；
	//   · 格宽/格距取地球物理半径口径（与渲染球半径无关，H3PlateMotion.EarthRadiusKm 同款纪律）；
	//   · 六边格梯度恢复常数 2/3（线性 GPE 场精确）；切面基逐格独立，跨边的微小非共面分量在基投影时丢弃。
	// 解法：Picard 外层（重算边缘应力帽）× 内层对角预条件 CG（矩阵自由、固定预算、残差判敛）。
	// 确定性：串行、遍历序 = Ball 邻接表序（构造期排序）、固定迭代数与容差 ⇒ 同输入逐位同输出。
	public sealed class H3ThinSheet
	{
		// ── 旋钮（判读后可调；默认档 = 2026-09-19 res3 影子/驱动标定，见设计-07 §0.0）──
		public float SheetViscosityPaS = 3e23f;      // 薄席有效粘度（文献薄席档 1e21–1e23 的顶端：老冷岩石圈档）
		public float SheetThicknessM = 1e5f;         // 薄席厚度 h（~100 km 岩石圈）
		public float YieldForcePerLengthN = 3e12f;   // 边缘整合屈服力（岩石圈整合强度 1e12–1e13 N/m）
		/// <summary>底层拖曳系数（N·yr/m³）：单位面积岩石圈的地幔黏滞阻力 c_b，按【格面积】缩放
		/// （拖曳 = c_b·A·v）——速度 = ∇GPE/c_b，与分辨率无关（旧"×平均边刚度"口径速度 ∝ 格宽²，
		/// res1/res3 差 40×，离散伪影已废）。它同时是刚体旋转零模的规范锚（消奇异）与板速尺度的
		/// 物理定标者：旧模型 v = F·δ/(η·A) 的阻力项在 B 方案里的身份就是它。</summary>
		public float BasalDragNyrPerM3 = 7e6f;
		/// <summary>塑性粘度下限（× 粘性刚度）：完全塑性（帽后 K→0）在准静态解下退化（速度尺度无界）——
		/// 标准粘塑性正则化：屈服边保留一份 ductile 底粘度，速率依赖由它兜底。
		/// 0.1 = 屈服边最多软化 10×（res1 影子首跑实测：1e-3 会让缝边流到 km/yr 量级）。</summary>
		public float PlasticStressFloorFraction = 0.1f;
		public int PicardIterations = 4;             // 应力帽外层迭代
		public int CgMaxIterations = 400;
		public float CgRelativeTolerance = 1e-3f;

		// ── 输出（Solve 后有效）──
		public Vector3[] Velocity;                   // 逐格切向速度（m/yr）
		public float[] StrainRate;                   // 逐格平均边缘剪应变率（1/yr；P3 变形加厚/损伤的输入）
		public int LastCgIterations { get; private set; }
		public float LastCgResidual { get; private set; }

		const float SecondsPerYear = 3.1558e7f;

		readonly Ball _ball;
		readonly int _n;
		readonly float _cellWidthM;                  // 地球物理口径格宽
		readonly float _cellAreaM2;
		readonly Vector3[] _tu, _tv;                 // 逐格切面基
		readonly Vector3[][] _edgeUnit;              // 逐有向边：切向单位向量（i→j，边缘中点切面）
		readonly float[][] _edgeDist;                // 逐有向边：物理格距（m）
		readonly float[][] _edgeK;                   // 逐有向边：刚度（N·yr/m；对向槽位同值）
		readonly Vector3[] _vel, _f, _body;          // CG/装配工作区
		float _gauge;                                // 旋转规范锚刚度（N·yr/m；每次 Solve 重算）
		float[] _ca, _cb, _ra, _rb, _pa, _pb, _apa, _apb, _za, _zb, _rhsA, _rhsB;

		public H3ThinSheet(Ball ball)
		{
			_ball = ball;
			_n = ball.CellIds.Length;
			float radiusM = H3PlateMotion.EarthRadiusKm * 1000f;
			float sceneToM = radiusM / ball.Radius;          // 场景格距 → 物理格距（方向量不受缩放）
			_cellAreaM2 = 4f * MathF.PI * radiusM * radiusM / _n;
			_cellWidthM = MathF.Sqrt(_cellAreaM2);

			Velocity = new Vector3[_n];
			StrainRate = new float[_n];
			_tu = new Vector3[_n];
			_tv = new Vector3[_n];
			_vel = new Vector3[_n];
			_f = new Vector3[_n];
			_body = new Vector3[_n];
			_edgeUnit = new Vector3[_n][];
			_edgeDist = new float[_n][];
			_edgeK = new float[_n][];

			var dirs = ball.CellDirs;
			var centers = ball.CellCenters;
			for (int i = 0; i < _n; i++)
			{
				Vector3 up = MathF.Abs(dirs[i].Y) < 0.98f ? Vector3.Up : Vector3.Right;
				_tu[i] = up.Cross(dirs[i]).Normalized();
				_tv[i] = dirs[i].Cross(_tu[i]);

				var neighbors = ball.CellNeighbors[i];
				int m = neighbors.Length;
				_edgeUnit[i] = new Vector3[m];
				_edgeDist[i] = new float[m];
				_edgeK[i] = new float[m];
				for (int k = 0; k < m; k++)
				{
					int j = neighbors[k];
					Vector3 dir = centers[j] - centers[i];
					float dScene = dir.Length();
					_edgeDist[i][k] = dScene * sceneToM;
					Vector3 mid = (centers[i] + centers[j]) * 0.5f;
					Vector3 radial = mid.Normalized();
					Vector3 t = dir - radial * dir.Dot(radial);
					_edgeUnit[i][k] = t.Normalized();
				}
			}
		}

		/// <summary>解一步准静态速度场。gpe = 逐格 GPE（H3Gpe.ComputeInto 的产物，N）；
		/// cellTraction = 可选逐格切向牵引（N；P2 接板片账户，测试用）。可重复调用（幂等，无内部时间态）。</summary>
		public void Solve(float[] gpe, Vector3[]? cellTraction = null)
		{
			ComputeBodyForces(gpe, cellTraction);

			// 初始刚度 = 纯粘性；规范锚 = 底层拖曳（按格面积，分辨率无关）；Picard：解 → 应力帽 → 再解
			float muYr = SheetViscosityPaS / SecondsPerYear;      // 换到"年"时间口径（v 存 m/yr）
			for (int i = 0; i < _n; i++)
				for (int k = 0; k < _edgeK[i].Length; k++)
					_edgeK[i][k] = 2f * muYr * SheetThicknessM * _cellWidthM / _edgeDist[i][k];
			_gauge = BasalDragNyrPerM3 * _cellAreaM2;             // 拖曳 = c_b·A·v（消旋转零模 + 定板速）

			Array.Clear(_ca ??= new float[_n]);
			Array.Clear(_cb ??= new float[_n]);
			for (int pic = 0; pic < PicardIterations; pic++)
			{
				SolveCg();
				CapEdgesByYield();
			}

			for (int i = 0; i < _n; i++) Velocity[i] = _vel[i];
			ComputeStrainRates();
		}

		// ── 体力：F_i = −∇E·A（六边格梯度恢复：线性场精确）──
		void ComputeBodyForces(float[] gpe, Vector3[]? cellTraction)
		{
			var neighbors = _ball.CellNeighbors;
			for (int i = 0; i < _n; i++)
			{
				Vector3 grad = Vector3.Zero;
				for (int k = 0; k < neighbors[i].Length; k++)
				{
					int j = neighbors[i][k];
					grad += (gpe[j] - gpe[i]) * _edgeUnit[i][k] / _edgeDist[i][k];
				}
				grad *= 2f / 3f;
				_body[i] = -grad * _cellAreaM2;
				if (cellTraction != null) _body[i] += cellTraction[i];
			}
		}

		// ── 对角预条件 CG（矩阵自由；固定预算 + 相对残差判敛；串行 ⇒ 确定性）──
		void SolveCg()
		{
			_ra ??= new float[_n];
			_rb ??= new float[_n];
			_pa ??= new float[_n];
			_pb ??= new float[_n];
			_apa ??= new float[_n];
			_apb ??= new float[_n];
			_za ??= new float[_n];
			_zb ??= new float[_n];
			_rhsA ??= new float[_n];
			_rhsB ??= new float[_n];

			for (int i = 0; i < _n; i++)
			{
				_rhsA[i] = _body[i].Dot(_tu[i]);
				_rhsB[i] = _body[i].Dot(_tv[i]);
			}

			Apply(_ca, _cb, _ra, _rb);
			float rsOld = 0f, diagSum = 0f;
			for (int i = 0; i < _n; i++)
			{
				_ra[i] = _rhsA[i] - _ra[i];
				_rb[i] = _rhsB[i] - _rb[i];
				float d = Diagonal(i) + 1e-30f;
				_za[i] = _ra[i] / d;
				_zb[i] = _rb[i] / d;
				rsOld += _ra[i] * _za[i] + _rb[i] * _zb[i];
				_pa[i] = _za[i];
				_pb[i] = _zb[i];
				diagSum += d;
			}
			float rs0 = rsOld;
			if (rs0 <= 0f) { LastCgIterations = 0; LastCgResidual = 0f; return; }

			float tol = CgRelativeTolerance * CgRelativeTolerance;
			int iter = 0;
			for (; iter < CgMaxIterations; iter++)
			{
				Apply(_pa, _pb, _apa, _apb);
				float pAp = 0f;
				for (int i = 0; i < _n; i++) pAp += _pa[i] * _apa[i] + _pb[i] * _apb[i];
				if (pAp <= 0f) break;
				float alpha = rsOld / pAp;
				float rsNew = 0f;
				for (int i = 0; i < _n; i++)
				{
					_ca[i] += alpha * _pa[i];
					_cb[i] += alpha * _pb[i];
					_ra[i] -= alpha * _apa[i];
					_rb[i] -= alpha * _apb[i];
				}
				for (int i = 0; i < _n; i++)
				{
					float d = Diagonal(i) + 1e-30f;
					_za[i] = _ra[i] / d;
					_zb[i] = _rb[i] / d;
					rsNew += _ra[i] * _za[i] + _rb[i] * _zb[i];
				}
				if (rsNew < tol * rs0) { iter++; break; }
				float beta = rsNew / rsOld;
				for (int i = 0; i < _n; i++)
				{
					_pa[i] = _za[i] + beta * _pa[i];
					_pb[i] = _zb[i] + beta * _pb[i];
				}
				rsOld = rsNew;
			}

			Apply(_ca, _cb, _ra, _rb);
			for (int i = 0; i < _n; i++) _vel[i] = _tu[i] * _ca[i] + _tv[i] * _cb[i];
			LastCgIterations = iter;
			LastCgResidual = MathF.Sqrt(rs0 > 0f ? rsOld / rs0 : 0f);
		}

		// A·x：纯算子（只有边缘刚度；体力只进右端项 rhs，见 SolveCg）。逐边一次（j > i），对向槽位同值 ⇒ 对称
		void Apply(float[] a, float[] b, float[] ra, float[] rb)
		{
			var neighbors = _ball.CellNeighbors;
			for (int i = 0; i < _n; i++)
			{
				_vel[i] = _tu[i] * a[i] + _tv[i] * b[i];
				_f[i] = Vector3.Zero;
			}
			for (int i = 0; i < _n; i++)
			{
				var nb = neighbors[i];
				for (int k = 0; k < nb.Length; k++)
				{
					int j = nb[k];
					if (j <= i) continue;
					Vector3 dv = _vel[j] - _vel[i];
					Vector3 radial = (_ball.CellCenters[i] + _ball.CellCenters[j]).Normalized();
					Vector3 t = dv - radial * dv.Dot(radial);
					Vector3 force = _edgeK[i][k] * t;
					// 拉普拉斯口径（正定）：ΣK(v_i−v_j) = F_i——粘滞阻力与外力平衡，
					// 力梯度方向 = −(v_j−v_i) 项。写反成拖拽号则 A 负定，CG 不收敛（踩过，测试钉住）。
					_f[i] -= force;
					_f[j] += force;
				}
			}
			for (int i = 0; i < _n; i++)
			{
				_f[i] += _gauge * _vel[i];               // 旋转规范锚（弱正定化）
				ra[i] = _f[i].Dot(_tu[i]);
				rb[i] = _f[i].Dot(_tv[i]);
			}
		}

		// 对角线（Jacobi 预条件）：本格各边刚度之和 + 规范锚
		float Diagonal(int i)
		{
			float sum = _gauge;
			for (int k = 0; k < _edgeK[i].Length; k++) sum += _edgeK[i][k];
			return sum;
		}

		// 逐边应力帽：|K·Δv| 超 Y ⇒ 该边 K 等比削减（对向槽位同步）——屈服边缘可滑移
		void CapEdgesByYield()
		{
			var neighbors = _ball.CellNeighbors;
			for (int i = 0; i < _n; i++)
			{
				var nb = neighbors[i];
				for (int k = 0; k < nb.Length; k++)
				{
					int j = nb[k];
					if (j <= i) continue;
					Vector3 dv = _vel[j] - _vel[i];
					Vector3 radial = (_ball.CellCenters[i] + _ball.CellCenters[j]).Normalized();
					Vector3 t = dv - radial * dv.Dot(radial);
					float stress = _edgeK[i][k] * t.Length();
					if (stress > YieldForcePerLengthN)
					{
						float scale = MathF.Max(YieldForcePerLengthN / stress, PlasticStressFloorFraction);
						_edgeK[i][k] *= scale;
						int back = FindBackSlot(j, i);
						if (back >= 0) _edgeK[j][back] *= scale;
					}
				}
			}
		}

		int FindBackSlot(int j, int i)
		{
			var nb = _ball.CellNeighbors[j];
			for (int k = 0; k < nb.Length; k++)
				if (nb[k] == i) return k;
			return -1;
		}

		// 平均边缘剪应变率（1/yr）：逐格 Σ|Δv_切|/d 的均值——量级判读口，P3 的变形加厚/损伤输入
		void ComputeStrainRates()
		{
			var neighbors = _ball.CellNeighbors;
			for (int i = 0; i < _n; i++)
			{
				var nb = neighbors[i];
				float sum = 0f;
				for (int k = 0; k < nb.Length; k++)
				{
					Vector3 dv = Velocity[nb[k]] - Velocity[i];
					Vector3 radial = (_ball.CellCenters[i] + _ball.CellCenters[nb[k]]).Normalized();
					Vector3 t = dv - radial * dv.Dot(radial);
					sum += t.Length() / _edgeDist[i][k];
				}
				StrainRate[i] = nb.Length > 0 ? sum / nb.Length : 0f;
			}
		}

		// ── 影子判读口（设计-07 P1）：薄席速度场 vs 旧运动学速度场的对比统计 ──

		/// <summary>影子对比统计（诊断报告用，不设门槛——P2 切驱动前的基线读数）。</summary>
		public struct ShadowStats
		{
			public float MeanSpeedOldCmPerYr;    // 旧运动学逐格速度均值（rad/My 口径换算）
			public float MeanSpeedNewCmPerYr;    // 薄席逐格速度均值（m/yr 口径换算）
			public float DirectionAgreement;     // 方向一致率：双方速度都 ≥ 阈值的格中，点积 &gt; 0 的比例
			public int ComparedCells;            // 参与方向对比的格数
		}

		/// <summary>对比两套速度场。oldRadPerMy = 旧运动学逐格速度（H3PlateMotion.Velocity，rad/My）；
		/// newMPerYr = 薄席速度（Velocity，m/yr）；minSpeedCmPerYr = 方向对比的速度门槛（两套场的
		/// 内部格速度都近零，方向无意义）。纯函数。</summary>
		public static ShadowStats CompareWithKinematics(Vector3[] oldRadPerMy, Vector3[] newMPerYr,
			float minSpeedCmPerYr = 0.1f)
		{
			int n = Math.Min(oldRadPerMy.Length, newMPerYr.Length);
			double sumOld = 0, sumNew = 0;
			int agree = 0, compared = 0;
			for (int i = 0; i < n; i++)
			{
				float speedOld = oldRadPerMy[i].Length() * H3PlateMotion.EarthRadiusKm * 0.1f;  // rad/My → cm/yr
				float speedNew = newMPerYr[i].Length() * 100f;                                   // m/yr → cm/yr
				sumOld += speedOld;
				sumNew += speedNew;
				if (speedOld >= minSpeedCmPerYr && speedNew >= minSpeedCmPerYr)
				{
					compared++;
					if (oldRadPerMy[i].Dot(newMPerYr[i]) > 0f) agree++;
				}
			}
			return new ShadowStats
			{
				MeanSpeedOldCmPerYr = n > 0 ? (float)(sumOld / n) : 0f,
				MeanSpeedNewCmPerYr = n > 0 ? (float)(sumNew / n) : 0f,
				DirectionAgreement = compared > 0 ? agree / (float)compared : 0f,
				ComparedCells = compared,
			};
		}
	}
}
