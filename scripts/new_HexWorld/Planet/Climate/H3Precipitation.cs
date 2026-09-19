using System;
using Godot;
using World.Utils;                          // SphericalFbmNoise（纯 C# 球面噪声，零引擎依赖）

namespace World.NewHexWorld.Plate
{
	// 年降水结构场：只产"形状"不产"总量"——
	// 返回值未做水量闭合（λ=1 原始口径），全球总量由 H3WaterCycle 按洋面蒸发锚定缩放（Σ降水 = Σ蒸发）。
	// 公式：
	//   P_raw = [60 + ITCZ(σ=12°) + 极锋(62°)] × 副高压制(26°) × 辐照度 × 地形抬升
	//           × 盛行风湿润度 ±55% × 地形雨影 ±65% × 噪声(±12%)
	//   盛行风两效应（风向/采样几何全在 H3Wind，本类只挂标定）：
	//     · 海洋湿润度 ±55%：上风向 10 点海洋占比按 exp(−d/L) 衰减加权（有方向性——
	//       信风海岸湿带 / 背风沙漠出得来）；
	//     · 地形雨影 ±65%：下风向 0.12 rad 海拔 − 上风向海拔（归一化跨度口径，×5 截 ±0.65）
	//       ——风爬坡（迎风坡）增雨、风下坡（背风坡）减雨。
	// 未立项项（回来时替换/扩）：月循环（月 ITCZ 摆动 + 季风水汽，本世界年尺度先行）；
	// 洋流 ±25% 等洋流场立项后回。
	// 两个调用口：终态气候（Crust 口，H3Plate.FinishCreatePlates）与演化步热路径（数组口
	// ComputeInto，河流输沙每步重算——陆块 600 My 漂移几千公里，雨得跟着下）。盛行风模板
	// （上风向落格下标）只依赖网格 + 行星档，由持有方构建缓存传入，每步只做 O(10n) gather。
	public static class H3Precipitation
	{
		// ── 纬度带骨架 ──
		const float ItczPeakMm = 1400f;      // 赤道 ITCZ 峰值（窄带，σ=12° → 30° 副热带处几乎归零）
		const float ItczSigmaDeg = 12f;
		const float PolarFrontPeakMm = 550f; // 62° 极锋峰值
		const float PolarFrontSigmaDeg = 14f;
		const float PolarFrontCenterDeg = 62f;
		const float BaseMm = 60f;            // 基准底（极地/副极地本来也少雨）
		const float SubtropCenterDeg = 26f;  // 副高最强处（真实撒哈拉/中亚/澳洲内陆 <100 mm）
		const float SubtropSigmaDeg = 9f;
		const float SubtropDepth = 0.85f;    // 副高压制深度
		const float InsolationGain = 0.30f;  // 辐照度 → 蒸发强 → 全球降水增（±30% × (ins−1)）

		// ── 地形/盛行风/噪声 ──
		const float OrographicGain = 0.4f;   // 地形抬升增雨上限 +40%（海拔 10 km 打满）
		const float OrographicScaleM = 10000f;
		const float MaritimeGain = 0.55f;    // 盛行风湿润度增益 ±55%（主要调节项）
		const float RainShadowGain = 5f;     // 雨影海拔差增益（归一化跨度口径）
		const float RainShadowCap = 0.65f;   // 雨影截幅（迎风 +65% / 背风 −65%）
		const float NoiseAmp = 0.12f;        // 噪声调制半幅 ±12%（纬度带是骨架，噪声只是小扰动）
		const float NoiseWavelengthKm = 5000f;  // 降水噪声基波长（区域降水差异）
		const int NoiseOctaves = 4;
		const int NoiseSeedSalt = 0x4D5E6F;     // 降水噪声种子盐（与其他噪声错开，防同相）

		/// <summary>算年降水结构场（mm/yr，未闭合水量；下标与 Ball.CellIds 对齐）。纯函数：只读入参、
		/// 不消耗 rng、同入参逐位一致。盛行风按地球档（prograde/speed 缺省）。</summary>
		public static float[] Compute(Ball ball, Crust crust, int seed,
			float axialTiltDeg = H3Climate.DefaultAxialTiltDeg, float insolation = H3Climate.DefaultInsolation,
			bool prograde = H3Wind.DefaultPrograde, float rotationSpeed = H3Wind.DefaultRotationSpeed)
		{
			int n = ball.CellIds.Length;
			var isLand = new bool[n];
			for (int i = 0; i < n; i++) isLand[i] = crust.IsLand(i);
			return Compute(ball, isLand, crust.Elevation, seed, axialTiltDeg, insolation, prograde, rotationSpeed);
		}

		/// <summary>数组口径（演化步无 <see cref="Crust"/>——河流输沙每步随陆块/海拔重算，见
		/// H3FluvialTransport）：isLand = 判陆掩码，elevAboveSeaM = 海拔（m，相对海平面，负值按海）。
		/// 每次自配缓冲并现建盛行风模板（低频口）；演化步的热路径走 <see cref="ComputeInto"/>。</summary>
		public static float[] Compute(Ball ball, bool[] isLand, float[] elevAboveSeaM, int seed,
			float axialTiltDeg = H3Climate.DefaultAxialTiltDeg, float insolation = H3Climate.DefaultInsolation,
			bool prograde = H3Wind.DefaultPrograde, float rotationSpeed = H3Wind.DefaultRotationSpeed)
		{
			int n = ball.CellIds.Length;
			return ComputeInto(ball, isLand, elevAboveSeaM, seed, axialTiltDeg, insolation, prograde, rotationSpeed,
				new float[n], H3Wind.BuildStencil(ball, prograde, rotationSpeed));
		}

		/// <summary><see cref="Compute(Ball,bool[],float[],int,float,float,bool,float)"/> 的复用缓冲版
		/// （演化步每步调用，零分配）。result 由调用方持有重复使用；<paramref name="stencil"/> =
		/// 持有方缓存的盛行风模板（H3Wind.BuildStencil 产物，只依赖网格+行星档，跨步只读）。
		/// 返回 result。性能：纬度带骨架 × 辐照增益 × fBm 噪声只依赖 (ball, seed, 倾角, 辐照)，
		/// 是步不变量——按 ball 弱缓存（见 <see cref="GetOrBuildSkeleton"/>），每步只剩盛行风
		/// 模板 gather（O(12n) 数组读）+ 地形抬升/雨影两个乘法因子。</summary>
		public static float[] ComputeInto(Ball ball, bool[] isLand, float[] elevAboveSeaM, int seed,
			float axialTiltDeg, float insolation, bool prograde, float rotationSpeed,
			float[] result, H3Wind.UpwindStencil stencil)
		{
			int n = ball.CellIds.Length;
			float[] skeletonMm = GetOrBuildSkeleton(ball, seed, axialTiltDeg, insolation);

			// 归一化跨度（雨影口径挂归一化海拔；现场差分免整条归一化数组，热路径零分配）。
			// max 精确可结合可交换 + ForWithLocal 锁内串行合并 ⇒ 与串行逐位一致。
			float span = 0f;
			ParallelLoops.ForWithLocal(n,
				() => 0f,
				(i, _, localMax) => MathF.Max(localMax, MathF.Abs(elevAboveSeaM[i])),
				localMax => { if (localMax > span) span = localMax; });

			// 主循环地图型并行：写只落 result[i]；骨架/风模板/isLand/海拔在本段只读，
			// 逐格算式与串行逐位一致（并行纪律见 ParallelLoops 头注）。
			ParallelLoops.For(n, i =>
			{
				float p = skeletonMm[i];   // (基准+ITCZ+极锋) × 副高 × 辐照 × (1+噪声±12%)——步不变量

				// 地形抬升增雨（只算陆地正海拔；洋格/负海拔不回减）
				float elevM = elevAboveSeaM[i];
				if (elevM > 0f) p *= 1f + MathF.Min(elevM / OrographicScaleM, 1f) * OrographicGain;

				// ── 盛行风两效应（几何全查模板——落格下标跨步不变）──
				// 海洋湿润度 ±55%：上风向是海（湿润来风）→ 增雨，是陆（大陆风）→ 压干
				p *= 1f + H3Wind.MaritimeScore(stencil, i, isLand) * MaritimeGain;
				// 地形雨影 ±65%：下风向海拔 > 上风向 = 风爬坡（迎风坡）→ 增雨；全场平（span≈0）跳过
				if (span > 1e-6f)
				{
					float windComp = (elevAboveSeaM[stencil.RainShadowDownwind[i]]
						- elevAboveSeaM[stencil.RainShadowUpwind[i]]) / span * RainShadowGain;
					p *= 1f + Math.Clamp(windComp, -RainShadowCap, RainShadowCap);
				}

				result[i] = MathF.Max(0f, p);
			});
			return result;
		}

		/// <summary>步不变量骨架缓存（键 = ball 弱引用 + seed/倾角/辐照指纹）。乘法结合序与逐格
		/// 直算版在浮点末位可差 1 ulp（骨架先乘噪声、后乘三个因子），量级 ~1e-7 相对——气候结构
		/// 与水量闭合（λ 全场缩放）不受影响；同入参仍逐位一致（缓存命中路径完全确定）。</summary>
		sealed class PrecipSkeleton
		{
			public int SeedKey;
			public float TiltDeg, Insolation;
			public float[] BaseMm;
		}

		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ball, PrecipSkeleton> _skeletonCache
			= new System.Runtime.CompilerServices.ConditionalWeakTable<Ball, PrecipSkeleton>();

		static float[] GetOrBuildSkeleton(Ball ball, int seed, float axialTiltDeg, float insolation)
		{
			var skeleton = _skeletonCache.GetValue(ball, _ => new PrecipSkeleton());
			int n = ball.CellIds.Length;
			if (skeleton.BaseMm != null && skeleton.BaseMm.Length == n
				&& skeleton.SeedKey == seed && skeleton.TiltDeg == axialTiltDeg
				&& skeleton.Insolation == insolation)
				return skeleton.BaseMm;

			var centers = ball.CellDirs;   // 构造期归一（与 centers[i].Normalized() 逐位同值）
			var noise = new SphericalFbmNoise(seed ^ NoiseSeedSalt, NoiseWavelengthKm, NoiseOctaves);

			// 副高中心随倾角微移（×0.12：偏移大了把沙漠带推过高纬，不合物理）
			float subCenter = SubtropCenterDeg + (axialTiltDeg - 23.4f) * 0.12f;
			float insolGain = 1f + (insolation - 1f) * InsolationGain;

			var baseMm = new float[n];
			for (int i = 0; i < n; i++)
			{
				Vector3 dir = centers[i];
				float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(dir.Y, -1f, 1f))) * (180f / MathF.PI);   // 0..90（年降水对称带）

				float itcz = ItczPeakMm * MathF.Exp(-latDeg * latDeg / (2f * ItczSigmaDeg * ItczSigmaDeg));
				float polarFront = PolarFrontPeakMm * MathF.Exp(
					-(latDeg - PolarFrontCenterDeg) * (latDeg - PolarFrontCenterDeg)
						/ (2f * PolarFrontSigmaDeg * PolarFrontSigmaDeg));
				float subtropical = 1f - SubtropDepth * MathF.Exp(
					-(latDeg - subCenter) * (latDeg - subCenter) / (2f * SubtropSigmaDeg * SubtropSigmaDeg));
				float p = (BaseMm + itcz + polarFront) * subtropical * insolGain;
				baseMm[i] = p * (1f + noise.Sample(dir) * NoiseAmp);
			}

			skeleton.SeedKey = seed;
			skeleton.TiltDeg = axialTiltDeg;
			skeleton.Insolation = insolation;
			skeleton.BaseMm = baseMm;
			return baseMm;
		}
	}
}
