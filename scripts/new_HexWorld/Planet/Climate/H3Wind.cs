using System;
using Godot;
using World.Utils;                          // LatLng 经纬（弧度）
using World.Utils.H3;                       // H3.LatLngToCell（方向 → 格原生查询）

namespace World.NewHexWorld.Plate
{
	// 盛行风场：三圈环流 + 科里奥利偏转的解析风。风是一个场——每格一个切向速度矢量（m/s），
	// 方向与大小一体：单位方向（环流带 + 科氏，步不变，缓存进 UpwindStencil）× 速率
	//（m/s = 带基速 × 自转档^0.5 × 海拔项，每步随海拔重算走 SpeedField）。
	// 另附两个风的气候效应所需的上风向采样模板（海洋湿润度 / 地形雨影，消费方 H3Precipitation）；
	// 方向性风沙搬运（H3AeolianTransport）沿同一场走。
	//
	// 模型：
	//   - 三圈环流带（按 |lat|）：Hadley(0-30°) / Ferrel(30-60°) / Polar(60-90°)；地面经向风
	//     Hadley/极地 → 向赤道、Ferrel → 向极地，南北半球镜像。
	//   - 科里奥利：东向分量 = 经向 × sin|lat| × speed^0.7 ×（半球 × 自转方向符号）——
	//     顺转自转北半球右偏（信风为东北风、中纬西风带），金星式逆转（prograde=false）全场镜像。
	//   - 海洋湿润度（MaritimeScore）：沿上风向采 10 点，海洋贡献按 exp(−d/L) 衰减加权，
	//     L = 0.15 rad × speed^0.5（地球 1× ≈ 1000 km 内贡献 37%），采样范围 3L 截 0.25-0.9 rad；
	//     占比映射到 −1（上风向全陆，干燥大陆风）~ +1（全海，湿润海洋风）。
	//   - 地形雨影：下风向 0.12 rad 与上风向 0.12 rad 两点的海拔差 ×5 截 ±0.65（增益挂归一化
	//     跨度口径——归一由消费方做，本类只出落格）。
	//
	// 行星档显式参数逐层传（旋钮最终上行 BallManager）。模板（UpwindStencil）只依赖
	// (Ball, 行星档)：上风向采样落格下标跨演化步不变——一次构建缓存复用，热路径每步只做
	// O(10n) 数组 gather。与老树的刻意差异：极点纬向基退化时返回零向量（不出 NaN）；
	// 采样落格用 H3.LatLngToCell 原生查询。
	public static class H3Wind
	{
		// ── 行星档（地球默认；将来 BallManager 出旋钮时上行到那里）──
		public const float DefaultRotationSpeed = 1f;   // 自转速度（相对地球 24h = 1.0；0.2 金星式慢 ~ 5 木星式快）
		public const bool DefaultPrograde = true;       // 自转方向（true = 顺转地球式自西向东）

		const float HadleyEdgeRad = 30f * MathF.PI / 180f;   // 三圈环流带边界（按 |lat|）
		const float FerrelEdgeRad = 60f * MathF.PI / 180f;
		const float CoriolisExponent = 0.7f;            // 偏转 ∝ speed^0.7（亚线性标定）
		const float MoistureScaleRad = 0.15f;           // 地球 1× 水汽衰减尺度 L（~1000 km 内贡献 37%）
		const int MaritimeSamples = 10;                 // 上风向采样点数
		const float RainShadowRad = 0.12f;              // 雨影上下风采样角距（~6.9°，跨过顶点胞才有效）

		// ── 风速度场旋钮（地球档 = 地表低空盛行风量级）──
		/// <summary>环流带基速（m/s）：信风 ~7、西风带 ~12（最强，归一参考档）、极地东风 ~8。</summary>
		public const float HadleyBaseSpeedMS = 7f;
		public const float FerrelBaseSpeedMS = 12f;
		public const float PolarBaseSpeedMS = 8f;
		/// <summary>风速归一参考（m/s）= 西风带海平面档：风介质权重 1.0 锚在这（H3SurfaceProcesses 直读 |u| 时除以它）。</summary>
		public const float ReferenceWindSpeedMS = FerrelBaseSpeedMS;
		const float SpeedRotationExponent = 0.5f;       // 速率 ∝ 自转档^0.5（亚线性）
		/// <summary>海拔项：速率 ×(1 + 增益·z/参考高)。自由大气风速随海拔增（8 km 山顶 ×1.5）。</summary>
		public const float ElevationWindGain = 0.5f;
		public const float ElevationWindRefM = 8000f;
		const float ElevationWindCapM = 10000f;         // 海拔输入截幅（厚度帽 Airy 当量 ~9 km 之上不再增速）

		/// <summary>环流带类型（按 |lat| 三分）。</summary>
		public enum Belt { Hadley, Ferrel, Polar }

		/// <summary>纬度（度）→ 环流带。30°/60° 断点归上带。</summary>
		public static Belt BeltAt(float latDeg)
		{
			float a = MathF.Abs(latDeg);
			if (a < 30f) return Belt.Hadley;
			if (a < 60f) return Belt.Ferrel;
			return Belt.Polar;
		}

		/// <summary>盛行风向：<paramref name="dir"/> 单位方向 →
		/// 切向单位向量（指向下风向）。极点纬向基退化 → 返回零向量（气候项按中性处理，见头注）。</summary>
		public static Vector3 WindAt(Vector3 dir, bool prograde = DefaultPrograde, float rotationSpeed = DefaultRotationSpeed)
		{
			float lat = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
			bool north = lat >= 0f;
			float absLat = MathF.Abs(lat);

			// 切向基：东 = 纬度增加方向的经向圈（赤道面内），北 = dir × 东
			Vector3 east = new Vector3(-dir.Z, 0f, dir.X);
			if (east.LengthSquared() < 1e-12f) return Vector3.Zero;   // 极点：东向基不存在，风向未定
			east = east.Normalized();
			Vector3 northDir = dir.Cross(east).Normalized();

			// 环流水平分量：Hadley/极地 → 向赤道（-1），Ferrel → 向极地（+1）；南北半球镜像
			float towardPole = absLat < HadleyEdgeRad ? -1f : (absLat < FerrelEdgeRad ? 1f : -1f);
			float mer = north ? towardPole : -towardPole;

			// 科里奥利偏转（右手系推导）：偏转强度 = sin|lat| × speed^0.7；
			// 北半球顺转右偏（信风东北 / 西风带），南半球左偏，逆转自转全场镜像
			float coriolisHemi = (north ? 1f : -1f) * (prograde ? 1f : -1f);
			float deflect = MathF.Sin(absLat) * MathF.Pow(MathF.Max(rotationSpeed, 0f), CoriolisExponent);
			Vector3 wind = northDir * mer + east * (mer * deflect * coriolisHemi);

			return wind.LengthSquared() > 1e-9f ? wind.Normalized() : Vector3.Zero;
		}

		/// <summary>环流带基速（m/s）——<see cref="SpeedMS"/> 的带档分量。</summary>
		public static float BeltBaseSpeedMS(Belt belt) => belt switch
		{
			Belt.Hadley => HadleyBaseSpeedMS,
			Belt.Ferrel => FerrelBaseSpeedMS,
			_ => PolarBaseSpeedMS,
		};

		/// <summary>风速（m/s，标量大小）= 带基速 × 自转档^0.5 × 海拔项。
		/// <paramref name="elevationM"/> = 海拔（m，水下传 0 即海平面风速）。</summary>
		public static float SpeedMS(Belt belt, float rotationSpeed, float elevationM)
		{
			float z = Math.Clamp(MathF.Max(elevationM, 0f), 0f, ElevationWindCapM);
			float rot = MathF.Pow(MathF.Max(rotationSpeed, 1e-3f), SpeedRotationExponent);
			return BeltBaseSpeedMS(belt) * rot * (1f + ElevationWindGain * z / ElevationWindRefM);
		}

		/// <summary>风速度矢量（m/s；切向）= <see cref="WindAt"/> 的方向 × <see cref="SpeedMS"/> 的大小
		/// ——**同一个场**的单格口径。极点方向退化 → 零向量（速度未定，消费方按无风处理）。</summary>
		public static Vector3 VelocityAt(Vector3 dir, float elevationM,
			bool prograde = DefaultPrograde, float rotationSpeed = DefaultRotationSpeed)
		{
			float lat = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
			float speed = SpeedMS(BeltAt(lat * (180f / MathF.PI)), rotationSpeed, elevationM);
			return WindAt(dir, prograde, rotationSpeed) * speed;
		}

		/// <summary>上风向采样模板：每格的海洋湿润度采样落格（10 点 × exp(−d/L) 权重）与雨影
		/// 上下风向落格。只依赖 (Ball, 行星档)——构建一次由持有方缓存，跨演化步只读复用。</summary>
		public sealed class UpwindStencil
		{
			/// <summary>格数（下标域 = Ball.CellIds）。</summary>
			public readonly int Count;
			/// <summary>[格][MaritimeSamples] 上风向采样点落格下标。</summary>
			public readonly int[][] MaritimeCell;
			/// <summary>[MaritimeSamples] 距离衰减权重 exp(−d/L)（与格无关，只依赖角距）。</summary>
			public readonly float[] MaritimeWeight;
			/// <summary>[格] 雨影下风向落格（dir + wind × 0.12 rad）。</summary>
			public readonly int[] RainShadowDownwind;
			/// <summary>[格] 雨影上风向落格（dir − wind × 0.12 rad）。</summary>
			public readonly int[] RainShadowUpwind;
			/// <summary>[格] 单位风向（切向，步不变的速度场方向分量）：
			/// 极点 = 零向量。</summary>
			public readonly Vector3[] Direction;
			/// <summary>[格] 环流带（步不变；速度场带基速的查表键）。</summary>
			public readonly Belt[] Belts;
			/// <summary>[格] 风沙落格邻居（方向性风沙搬运用）：与本格风向点积最大的邻居；风向过切
			/// （点积 &lt; cos60°，风基本沿边吹不进邻格）或极点退化 = **−1**（沙没有搬运方向，就地不动）。
			/// 只依赖 (Ball, 行星档) ⇒ 跟模板一起缓存。</summary>
			public readonly int[] AeolianDownwind;

			internal UpwindStencil(int count, int[][] maritimeCell, float[] maritimeWeight,
				int[] rainShadowDownwind, int[] rainShadowUpwind,
				Vector3[] direction, Belt[] belts, int[] aeolianDownwind)
			{
				Count = count;
				MaritimeCell = maritimeCell;
				MaritimeWeight = maritimeWeight;
				RainShadowDownwind = rainShadowDownwind;
				RainShadowUpwind = rainShadowUpwind;
				Direction = direction;
				Belts = belts;
				AeolianDownwind = aeolianDownwind;
			}
		}

		/// <summary>构建上风向模板（逐格 12 次 H3 原生方向→格查询；res5 约 2×10⁷ 次调用是秒级
		/// 一次性成本，热路径由持有方缓存——<see cref="UpwindStencil"/> 头注）。
		/// <paramref name="rotationSpeed"/> 同时定水汽衰减尺度（L ∝ speed^0.5，快自转水汽走得远）。</summary>
		public static UpwindStencil BuildStencil(Ball ball, bool prograde, float rotationSpeed)
		{
			int n = ball.CellIds.Length;
			float speed = MathF.Max(rotationSpeed, 1e-3f);   // 防除零（0 档不支援——潮汐锁定无风带）
			float scaleL = MoistureScaleRad * MathF.Pow(speed, 0.5f);
			float range = Math.Clamp(3f * scaleL, 0.25f, 0.9f);
			var weights = new float[MaritimeSamples];
			for (int k = 0; k < MaritimeSamples; k++)
				weights[k] = MathF.Exp(-(range * (k + 1) / MaritimeSamples) / scaleL);

			var maritimeCell = new int[n][];
			var downwind = new int[n];
			var upwind = new int[n];
			var direction = new Vector3[n];
			var belts = new Belt[n];
			var aeolianDownwind = new int[n];
			var neighbors = ball.CellNeighbors;
			const float tangentialCos = 0.5f;   // cos60°：风向过切（与"指向邻居的方向"夹角 > 60°）不落格
			for (int i = 0; i < n; i++)
			{
				Vector3 dir = ball.CellDirs[i];
				Vector3 wind = WindAt(dir, prograde, rotationSpeed);
				float latDeg = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) * (180f / MathF.PI);
				belts[i] = BeltAt(latDeg);
				direction[i] = wind;

				// 风沙落格邻居：把邻居格心方向投影到本格切平面、归一化后与风向比**方位**——
				// 与风向最对齐的邻居当选；过切（方位夹角 > 60°，风基本沿边吹不进邻格）/极点 = -1
				int bestNb = -1;
				float bestDot = tangentialCos;
				if (wind.LengthSquared() > 1e-9f)
				{
					foreach (int nb in neighbors[i])
					{
						Vector3 rel = ball.CellDirs[nb] - dir * dir.Dot(ball.CellDirs[nb]);
						float len = rel.Length();
						if (len < 1e-9f) continue;
						float d = rel.Dot(wind) / len;
						if (d > bestDot) { bestDot = d; bestNb = nb; }
					}
				}
				aeolianDownwind[i] = bestNb;

				var samples = new int[MaritimeSamples];
				for (int k = 0; k < MaritimeSamples; k++)
				{
					float d = range * (k + 1) / MaritimeSamples;
					samples[k] = CellAt(ball, (dir - wind * d).Normalized());   // 上风向
				}
				maritimeCell[i] = samples;
				downwind[i] = CellAt(ball, (dir + wind * RainShadowRad).Normalized());
				upwind[i] = CellAt(ball, (dir - wind * RainShadowRad).Normalized());
			}
			return new UpwindStencil(n, maritimeCell, weights, downwind, upwind, direction, belts, aeolianDownwind);
		}

		/// <summary>风速度场的大小分量（逐格 |u|，m/s）：带基速 × 自转档^0.5 × 海拔项。
		/// 方向分量 = 模板缓存的 <see cref="UpwindStencil.Direction"/>——两者拼成同一个速度矢量场；
		/// 单格口径 = <see cref="VelocityAt"/>。
		/// 每步 O(n) 纯乘、零分配（调用方复用缓冲）：<paramref name="elevationM"/> = 当步海拔
		/// （水下传 0），<paramref name="result"/> 逐格 |u|（m/s）。</summary>
		public static void SpeedField(UpwindStencil stencil, float[] elevationM, float rotationSpeed, float[] result)
		{
			float rot = MathF.Pow(MathF.Max(rotationSpeed, 1e-3f), SpeedRotationExponent);
			ParallelLoops.For(stencil.Count, i =>
			{
				float z = Math.Clamp(MathF.Max(elevationM[i], 0f), 0f, ElevationWindCapM);
				result[i] = BeltBaseSpeedMS(stencil.Belts[i]) * rot * (1f + ElevationWindGain * z / ElevationWindRefM);
			});
		}

		/// <summary>单格海洋湿润度：−1（上风向全陆，干）~
		/// +1（全海，湿）。纯 gather：isLand 当步掩码 × 模板落格，演化步零分配。</summary>
		public static float MaritimeScore(UpwindStencil stencil, int cell, bool[] isLand)
		{
			int[] cells = stencil.MaritimeCell[cell];
			float sumW = 0f, sumOcean = 0f;
			for (int k = 0; k < cells.Length; k++)
			{
				sumW += stencil.MaritimeWeight[k];
				if (!isLand[cells[k]]) sumOcean += stencil.MaritimeWeight[k];
			}
			return sumW > 1e-9f ? (sumOcean / sumW - 0.5f) * 2f : -1f;
		}

		/// <summary><see cref="MaritimeScore"/> 的全场版（整层出图/月化层用）。</summary>
		public static void MaritimeScores(UpwindStencil stencil, bool[] isLand, float[] result)
		{
			for (int i = 0; i < stencil.Count; i++)
				result[i] = MaritimeScore(stencil, i, isLand);
		}

		// 球面方向 → 所在格下标：CoordUtil.LatLngToSphere 的逆（x=cosφcosλ / y=sinφ / z=cosφsinλ）
		// → H3 原生查格（Ball 收全量 res 格，必命中；查不到即抛与 Ball.CellIndexOf 同纪律）。
		static int CellAt(Ball ball, Vector3 dir)
		{
			var latLng = new LatLng(MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)), MathF.Atan2(dir.Z, dir.X));
			return ball.CellIndexOf(H3.LatLngToCell(latLng, ball.Res));
		}
	}
}
