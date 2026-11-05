using System;
using Godot;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 年基温：纬度基准曲线 + 倾角/辐照修正 + 大陆性噪声 + 沿岸洋流 + 海拔递减。
///
/// 这是**月度温度公式的 base 项**（无季节），年均温由 12 个月的均值涌现，不由本类直接给出。
/// 曲线标定：赤道 ≈ +30 °C、极地 ≈ −22 °C（`52·cos^1.1 − 22`），保证"赤道热、两极冷"的量级先对。
///
/// 已知边界：没有任何经向热输送（无大气/海洋热输运项）——极地温度完全由本地平衡决定，
/// 因此比真实地球更冷；季节环流由 <see cref="MonthlyClimate"/> 的季风项补偿，不在此处伪造。
///
/// @example
/// <code>
/// var baseTemp = new TemperatureModel();
/// baseTemp.Compute(grid, spec, ocean.BuildCoastalWarmth(grid), seed: 42);
/// float t = baseTemp.AnnualC[i];
/// </code>
/// </summary>
public sealed class TemperatureModel
{
	/// <summary>逐格年基温（°C；与 <c>grid.Dirs</c> 逐位对齐）。</summary>
	public float[] AnnualC { get; private set; } = Array.Empty<float>();

	/// <summary>全球算术均值（°C；H3 等积格 ⇒ 算术均值即全球均值）。</summary>
	public float MeanC { get; private set; }

	/// <summary>计算年基温；<paramref name="coastalWarmth"/> 来自 <see cref="OceanCurrentModel.BuildCoastalWarmth"/>。</summary>
	public void Compute(ClimateGrid grid, ClimateSpec spec, float[] coastalWarmth, int seed)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (spec == null) throw new ArgumentNullException(nameof(spec));

		int n = grid.Count;
		AnnualC = new float[n];
		var noise = new SphericalFbmNoise(seed ^ 0x1A2B3C, spec.ContinentalityWavelengthKm, 3);

		float tiltDelta = (spec.ObliquityDeg - 23.4f) / 46.6f;
		float insolationDelta = spec.Insolation - 1f;
		bool applyTilt = MathF.Abs(spec.ObliquityDeg - 23.4f) > 0.5f;
		bool applyInsolation = MathF.Abs(insolationDelta) > 0.01f;

		float sum = 0f;
		for (int i = 0; i < n; i++)
		{
			var dir = grid.Dirs[i];
			float latRad = grid.LatRad(i);
			float cosLat = MathF.Max(0f, MathF.Cos(latRad));

			float t = spec.EquatorBaseC * MathF.Pow(cosLat, spec.LatitudeExponent) + spec.PolarBaseC;

			// 倾角越大 → 高纬夏季直射但冬季极夜更长，年均净降温；赤道不受影响。
			if (applyTilt)
			{
				float highLat = MathF.Max(0f, MathF.Abs(grid.LatDeg(i)) - 45f) / 45f;
				t -= highLat * tiltDelta * spec.TiltCoolingC;
			}

			// 近恒星：赤道直射升温多、极地斜射升温少 ⇒ 不只整体平移，温差同时被拉开。
			if (applyInsolation)
				t += insolationDelta * (spec.InsolationGlobalC + spec.InsolationLatitudeC * cosLat);

			t += noise.Sample(dir) * spec.ContinentalityNoiseC;

			if (coastalWarmth != null) t += coastalWarmth[i] * spec.OceanCurrentWarmthC;

			// 只对海平面以上递减：海底深度不影响气温。
			t -= spec.LapseRateCPerKm * (MathF.Max(0f, grid.HeightM[i]) * 1e-3f);

			AnnualC[i] = t;
			sum += t;
		}
		MeanC = sum / n;
	}

	/// <summary>
	/// 湿润降温闭环（温度 → 风 → 降水 → 温度）：`T = T₀ − k·P/(1+g)`。
	///
	/// 一步解析收敛：降水多 = 蒸发强 + 云反照率高 ⇒ 湿润区更凉（亚马逊 vs 撒哈拉同纬度差 2–4 °C）。
	/// 负反馈自我抑制由 `1+g` 承担（凉 → 海陆温差小 → 风弱 → 雨少 → 凉得少）。
	/// 必须作用在**月温度公式的 base 上**、且在月循环之前——否则月度场读不到修正。
	/// </summary>
	public static void ApplyWetCooling(float[] annualBaseC, float[] annualPrecipMm, ClimateSpec spec)
	{
		if (annualBaseC == null) throw new ArgumentNullException(nameof(annualBaseC));
		if (annualPrecipMm == null || annualPrecipMm.Length != annualBaseC.Length) return;
		float k = spec.WetCoolingCPerMm / (1f + spec.WetCoolingFeedback);

		for (int i = 0; i < annualBaseC.Length; i++)
			if (annualPrecipMm[i] > 0f)
				annualBaseC[i] -= k * annualPrecipMm[i];
	}
}
