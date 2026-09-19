using System;
using Godot;
using World.Utils;                          // SphericalFbmNoise（纯 C# 球面噪声，零引擎依赖）

namespace World.NewHexWorld.Plate
{
	// 年均温场：终态一次计算（构造演化跑完、海平面定局后）。纯 C# 噪声（SphericalFbmNoise，
	// 零引擎依赖）；极点 cosLat 下限 0 防 NaN：
	//   T = 纬度基准（52·cos¹·¹lat − 22，赤道 ≈ +30 / 极地 ≈ −22）
	//   T = 纬度基准（52·cos¹·¹lat − 22，赤道 ≈ +30 / 极地 ≈ −22）
	//       + 轴倾角高纬修正（|lat|>45° 线性，地球 23.4° 档 = 0）
	//       + 恒星辐照度修正（全局 + 赤道加权两项；1.0 = 地球档 = 0）
	//       + 大陆性噪声（±7°C，基波长 1 万 km fBm 三倍频）
	//       − 直减率 6.0°C/km × max(0, 海拔)（海拔来自 Crust.Elevation，米，0=海平面）。
	// 未立项项（回来时在此扩，不另起炉灶）：洋流冷暖（±5°C，需先有洋流场）、
	// 季风/月循环、冰雪反照率（冰冻圈批次）。
	public static class H3Climate
	{
		// ── 行星档（地球默认；将来 BallManager 出旋钮时上行到那里）──
		public const float DefaultAxialTiltDeg = 23.4f;   // 轴向倾角（度）：23.4 = 地球档（修正 = 0）
		public const float DefaultInsolation = 1f;        // 恒星辐照度（相对地球 1AU = 1.0）

		const float LatBaseAmpC = 52f;       // 纬度基准振幅（赤道 ≈ +30°C）
		const float LatBaseOffsetC = -22f;   // 纬度基准截距（极地基准 −22°C）
		const float LatExponent = 1.1f;      // 高纬加速降温指数
		const float LapseRateCKm = 6.0f;     // 直减率（°C/km）
		const float NoiseAmpC = 7f;          // 大陆性噪声半幅（±7°C）
		const float NoiseWavelengthKm = 10000f;  // 噪声基波长（低频大陆性差异）
		const int NoiseOctaves = 3;
		const int NoiseSeedSalt = 0x5A17;    // 温度噪声种子盐（与构造/起伏噪声错开，防同相）

		// ── 潜在蒸散 PET（供侵蚀的水介质「产流」口径用，见 H3SurfaceProcesses）──
		/// <summary>PET 参考值（mm/yr）与参考温度（°C）：`PET(T) = PetRefMmPerYear · 2^((T−PetRefC)/10)`。
		/// 依据：饱和水汽压随温度 ~7%/°C（Clausius-Clapeyron ⇒ 每升 10°C 约翻倍），
		/// 锚点 = 地球陆地年均温 ~10°C 的陆地平均 PET ≈ 700 mm/yr。
		/// 这是"水介质强度"的唯一标定旋钮（产流 = f(P, PET)）。</summary>
		public const float PetRefMmPerYear = 700f;
		public const float PetRefC = 10f;

		/// <summary>潜在蒸散（mm/yr）：随温度指数增长（C-C 式每 10°C 翻倍）。纯函数、与格无关（只吃温度）。</summary>
		public static float PetMmPerYear(float tempC)
			=> PetRefMmPerYear * MathF.Pow(2f, (tempC - PetRefC) / 10f);

		/// <summary>湿润指数（0 = 极干 ~ 1 = 极湿）：`AI = P/(P+PET)`。
		/// ⚠️ **有界**（不依赖任何归一常数）——风蚀的干燥度就用 `1 − AI = PET/(P+PET)`。</summary>
		public static float AridityIndex(float precipMmYear, float tempC)
			=> AridityIndexWithPet(precipMmYear, PetMmPerYear(tempC));

		/// <summary><see cref="AridityIndex"/> 的 PET 直给版：逐格热路径先算一次 PET，
		/// 湿润指数与产流共用（免每格重复 2 次 Pow——Pow 是逐格介质率的主导浮点成本）。</summary>
		public static float AridityIndexWithPet(float precipMmYear, float pet)
		{
			float p = MathF.Max(precipMmYear, 0f);
			return p + pet > 0f ? p / (p + pet) : 0f;
		}

		/// <summary>**产流**（mm/yr）= 降水扣掉实际蒸散后的余量，Budyko/Turc-Pike 形式：
		/// `R = P · (1 − 1/√(1 + (P/PET)²))`。
		/// 为什么用它当"水介质"：`P → 0` 或 `PET ≫ P`（干旱）时 **R 严格 → 0** ⇒ "没有水就没有水蚀"是
		/// **物理结论**而不是人工下限；湿润区 `R → P`；量纲是真实水通量 ⇒ 不需要任何"除以本行星均值"
		/// 的归一化（那种归一化会把绝对水量抹掉——一个只有地球 1/3 雨量的世界照样按地球强度被蚀）。
		/// 校验：P/PET = 1 → R = 0.29P；P/PET = 2 → R = 0.55P（地球陆地 P/PET ≈ 1.3 ⇒ R/P ≈ 0.4 ✓）。</summary>
		public static float RunoffMmPerYear(float precipMmYear, float tempC)
			=> RunoffMmPerYearWithPet(precipMmYear, PetMmPerYear(tempC));

		/// <summary><see cref="RunoffMmPerYear"/> 的 PET 直给版（与 <see cref="AridityIndexWithPet"/>
		/// 配对：一次 PET 两处用）。</summary>
		public static float RunoffMmPerYearWithPet(float precipMmYear, float pet)
		{
			float p = MathF.Max(precipMmYear, 0f);
			if (p <= 0f) return 0f;
			float petClamped = MathF.Max(pet, 1e-3f);
			float ratio = p / petClamped;
			return p * (1f - 1f / MathF.Sqrt(1f + ratio * ratio));
		}

		/// <summary>**地表裸露度**（1 = 全裸/沙漠 ~ 0.1 = 密林；风蚀的 `(1−V)` 代理）：
		/// 由 AI 分档插值（§18 的 biome 裸露锚点：沙漠 1.0 / 草原 0.6 / 森林 0.1）。
		/// ⚠️ 演化期**没有 biome 层**（气候层是终态一次算的）⇒ 用 AI 当代理，这正是"V 由 P、T、AI 决定"
		/// 的最低成本形态；真 biome 层落地后改为直接读 V（登记）。</summary>
		public static float BarenessFromAridityIndex(float ai)
		{
			const float desertAi = 0.3f, forestAi = 0.6f;
			if (ai <= desertAi) return 1f;
			if (ai >= forestAi) return 0.1f;
			return 1f + (0.1f - 1f) * ((ai - desertAi) / (forestAi - desertAi));
		}

		/// <summary>算年均温场（°C，下标与 Ball.CellIds / Crust 各场对齐）。纯函数：只读入参、
		/// 不消耗 rng、同入参逐位一致（噪声是哈希采样，与调用序无关）。</summary>
		public static float[] Compute(Ball ball, Crust crust, int seed,
			float axialTiltDeg = DefaultAxialTiltDeg, float insolation = DefaultInsolation)
		{
			int n = ball.CellIds.Length;
			return ComputeInto(ball, crust.Elevation, seed, axialTiltDeg, insolation, new float[n]);
		}

		/// <summary>**数组口径**（演化步无 <see cref="Crust"/>；地表过程每步按当时的海拔重算温度，
		/// 见 <see cref="H3SurfaceProcesses"/> 的冰川介质门）：<paramref name="elevAboveSeaM"/> = 海拔
		/// （m，相对海平面；直减率只吃正海拔，同 <see cref="Compute(Ball,Crust,int,float,float)"/>）。
		/// 与降水层同款分层（<see cref="H3Precipitation.ComputeInto"/>）：
		/// **纬度骨架 + 倾角/辐照修正 + fBm 噪声 = 步不变量**（按 ball 弱缓存，见
		/// <see cref="GetOrBuildSkeleton"/>），每步只剩一个直减项 ⇒ O(n) 一次乘减、零分配。
		/// ⚠️ 与 <see cref="Compute(Ball,Crust,int,float,float)"/> 共用**同一个骨架函数** ⇒ 演化期温度
		/// 与终态气候层逐位同口径（不会出现"演化期冰川在 A 处、终态出图在 B 处"）。</summary>
		public static float[] ComputeInto(Ball ball, float[] elevAboveSeaM, int seed,
			float axialTiltDeg, float insolation, float[] result)
		{
			int n = ball.CellIds.Length;
			float[] skeletonC = GetOrBuildSkeleton(ball, seed, axialTiltDeg, insolation);
			ParallelLoops.For(n, i =>
				result[i] = skeletonC[i] - LapseRateCKm * MathF.Max(0f, elevAboveSeaM[i]) / 1000f);
			return result;
		}

		/// <summary>步不变量骨架（°C）：纬度基准 + 轴倾角修正 + 恒星辐照修正 + 大陆性噪声。
		/// 只依赖 (ball, seed, 倾角, 辐照) ⇒ 一次构建、跨演化步只读复用（同 H3Precipitation 的纪律：
		/// res5 = 2M 格每步重采 3 倍频 fBm 是 6×10⁶ 次晶格哈希/步的无谓常数）。</summary>
		static float[] GetOrBuildSkeleton(Ball ball, int seed, float axialTiltDeg, float insolation)
		{
			var skeleton = _skeletonCache.GetValue(ball, _ => new ClimateSkeleton());
			int n = ball.CellIds.Length;
			if (skeleton.BaseC != null && skeleton.BaseC.Length == n
				&& skeleton.SeedKey == seed && skeleton.TiltDeg == axialTiltDeg
				&& skeleton.Insolation == insolation)
				return skeleton.BaseC;

			var centers = ball.CellCenters;
			var noise = new SphericalFbmNoise(seed ^ NoiseSeedSalt, NoiseWavelengthKm, NoiseOctaves);
			bool tiltActive = MathF.Abs(axialTiltDeg - 23.4f) > 0.5f;
			bool insolActive = MathF.Abs(insolation - 1f) > 0.01f;

			var baseC = skeleton.BaseC != null && skeleton.BaseC.Length == n ? skeleton.BaseC : new float[n];
			for (int i = 0; i < n; i++)
			{
				Vector3 dir = centers[i].Normalized();
				float lat = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));   // -π/2..π/2
				// 极点浮点修复：float π/2 量化略超真值 → cos 微负 →
				// Pow(负底,1.1)=NaN。cosLat 下限 0 → 极点 baseT = −22（正确极地温）。
				float cosLat = MathF.Max(0f, MathF.Cos(lat));

				float t = LatBaseAmpC * MathF.Pow(cosLat, LatExponent) + LatBaseOffsetC;

				// 轴倾角修正（相对地球档偏差）：高纬（45°→0、90°→1）× 倾角偏差 × 最大 25°C
				if (tiltActive)
				{
					float latDeg = MathF.Abs(lat) * (180f / MathF.PI);
					float highLat = MathF.Max(0f, latDeg - 45f) / 45f;
					float tiltDelta = (axialTiltDeg - 23.4f) / 46.6f;
					t -= highLat * tiltDelta * 25f;
				}

				// 恒星辐照度修正：全局项 + cosLat 纬度加权项（直射的赤道升温多 → 温差随辐照增大）
				if (insolActive)
				{
					float dIns = insolation - 1f;
					t += dIns * 24f + dIns * cosLat * 12f;
				}

				t += noise.Sample(dir) * NoiseAmpC;
				baseC[i] = t;
			}
			skeleton.BaseC = baseC;
			skeleton.SeedKey = seed;
			skeleton.TiltDeg = axialTiltDeg;
			skeleton.Insolation = insolation;
			return baseC;
		}

		sealed class ClimateSkeleton
		{
			public float[] BaseC;
			public int SeedKey;
			public float TiltDeg, Insolation;
		}

		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ball, ClimateSkeleton> _skeletonCache
			= new System.Runtime.CompilerServices.ConditionalWeakTable<Ball, ClimateSkeleton>();
	}
}
