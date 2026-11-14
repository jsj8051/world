using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 月度气候：12 个月的月温 → 气压 → 诊断风场 → 水汽追踪 → 月降水，外加季风强度与月极值。
///
/// 三档风按气压强迫、科氏力和各自的摩擦条件求定常环流。
/// 月/年均场只保留风带、季风和准静止热力结构；冷锋/瞬变涡旋属于天气尺度，不在此生成。
///
/// 月温使用全天平均辐照与海陆热惯性。气压带和降水带共享同一套月太阳赤纬。
/// 月降水是**绝对量**（mm/月），年降水 = Σ12 月 —— 年值由月值涌现，不做"年值分配"。
///
/// @example
/// <code>
/// var monthly = new MonthlyClimate();
/// monthly.Compute(grid, spec, baseTempC, ocean.BuildCoastalWarmth(grid), seed);
/// float t = monthly.MonthTempC[6][i];     // 7 月
/// float mm = monthly.AnnualPrecipMm[i];   // Σ 12 月
/// </code>
/// </summary>
public sealed class MonthlyClimate
{
	readonly record struct RainBands(float Latitude, float PolarFront, float Subtropical);

	/// <summary>月数（与 <see cref="ClimateSpec.Months"/> 一致；本模型按 12 标定）。</summary>
	public int MonthCount { get; private set; } = 12;

	/// <summary>逐月温度（°C）：<c>[month][cell]</c>。</summary>
	public float[][] MonthTempC { get; private set; } = Array.Empty<float[]>();

	/// <summary>同一次月循环生成的地面、低空和自由风。</summary>
	public WindField Wind { get; private set; }

	/// <summary>逐月降水（mm/月）。</summary>
	public float[][] MonthPrecipMm { get; private set; } = Array.Empty<float[]>();

	/// <summary>年均温（°C）= 12 个月算术均值（涌现，不是独立推导）。</summary>
	public float[] AnnualTempC { get; private set; } = Array.Empty<float>();

	/// <summary>年降水（mm/年）= Σ 12 月。</summary>
	public float[] AnnualPrecipMm { get; private set; } = Array.Empty<float>();

	/// <summary>季风强度 ∈ [0, 1]（冬夏水汽×辐射反差；无季节星球恒 0）。</summary>
	public float[] MonsoonStrength { get; private set; } = Array.Empty<float>();

	/// <summary>逐月海平面气压（hPa，绝对量）：<c>[month][cell]</c>。</summary>
	public float[][] MonthPressureHpa { get; private set; } = Array.Empty<float[]>();

	/// <summary>年均气压（hPa）= 12 个月算术均值（涌现，不另算一套）。</summary>
	public float[] AnnualPressureHpa { get; private set; } = Array.Empty<float>();

	/// <summary>最热月均温（°C；柯本 A/E 分界用）。</summary>
	public float[] HotMonthC { get; private set; } = Array.Empty<float>();

	/// <summary>最冷月均温（°C；柯本 A/D 分界用）。</summary>
	public float[] ColdMonthC { get; private set; } = Array.Empty<float>();

	/// <summary>最干月降水（mm）。</summary>
	public float[] DryMonthMm { get; private set; } = Array.Empty<float>();

	/// <summary>最干月所在月份（0–11；柯本干季季节判定用）。</summary>
	public int[] DryMonthIndex { get; private set; } = Array.Empty<int>();

	/// <summary>计算全部月度场。</summary>
	/// <param name="baseTempC">年基温（°C；已含湿润降温修正）。</param>
	/// <param name="coastalWarmth">沿岸洋流冷暖（降水修正用；可为 null）。</param>
	/// <param name="coarse">粗层视图（长波平滑用；null = 恒等层，合成网格口径）。</param>
	public void Compute(ClimateGrid grid, ClimateSpec spec, float[] baseTempC, float[] coastalWarmth, int seed,
		CoarseLayer coarse = null)
		=> Compute(grid, spec, baseTempC, coastalWarmth, seed, coarse, CancellationToken.None);

	internal void Compute(ClimateGrid grid, ClimateSpec spec, float[] baseTempC, float[] coastalWarmth, int seed,
		CoarseLayer coarse, CancellationToken cancellation, ConservativeWindSolver windSolver = null)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (baseTempC == null || baseTempC.Length != grid.Count)
			throw new ArgumentException("年基温与格数不一致", nameof(baseTempC));

		int n = grid.Count;
		MonthCount = Math.Max(1, spec.Months);
		Allocate(n);

		var insolation = MonthlyInsolation.BuildYear(MonthCount, spec.ObliquityDeg);
		MonthlyTemperatureModel.Compute(grid, spec, baseTempC, insolation, MonthTempC, cancellation);

		var precipitation = new PrecipitationInputs(grid, spec, coastalWarmth, seed);
		var annualSeaLevel = new float[n];

		// 气压热力项的年均偏置取已求解的月温平均，再折算到海平面。
		for (int m = 0; m < MonthCount; m++)
		{
			cancellation.ThrowIfCancellationRequested();
			for (int i = 0; i < n; i++) annualSeaLevel[i] += MonthTempC[m][i];
		}
		for (int i = 0; i < n; i++)
			annualSeaLevel[i] = SeaLevelEquivalent(annualSeaLevel[i] / MonthCount, grid, spec, i);

		var surfaceWind = new Vector3[MonthCount][];
		var lowWind = new Vector3[MonthCount][];
		var freeWind = new Vector3[MonthCount][];
		var pressure = PressureFieldModel.Create(grid, spec, coarse ?? CoarseLayer.Identity(grid), annualSeaLevel);
		windSolver ??= new ConservativeWindSolver(null, grid, spec, cancellation);
		var precipitationSolarWeight = new float[n];
		var seaLevel = new float[n];
		var workspace = pressure.CreateWorkspace();
		var target = new float[n];
		var upstream = new int[n];
		var transportSpeed = new float[n];
		for (int m = 0; m < MonthCount; m++)
		{
			cancellation.ThrowIfCancellationRequested();
			float deltaRad = insolation[m].DeclinationRad;
			BuildMonthSeaLevelTemp(grid, spec, MonthTempC[m], seaLevel);
			pressure.BuildMonth(seaLevel, deltaRad, MonthPressureHpa[m], workspace, thermalHpa: target);
			for (int i = 0; i < n; i++)
				target[i] = MonthPressureHpa[m][i] + (ConservativeWindSolver.ThermalGainMultiplier - 1f) * target[i];
			var winds = windSolver.SolveMonth(target, upstream, transportSpeed, cancellation);
			surfaceWind[m] = winds.Surface;
			lowWind[m] = winds.Low;
			freeWind[m] = winds.Free;
			precipitation.BuildMonth(deltaRad, precipitationSolarWeight, winds.Low, MonthPrecipMm[m], upstream, transportSpeed);
		}

		Wind = new WindField(new WindSet(WindKind.Surface, surfaceWind, new Vector3[n], new float[n]),
			new WindSet(WindKind.Low, lowWind, new Vector3[n], new float[n]),
			new WindSet(WindKind.Free, freeWind, new Vector3[n], new float[n]));
		Finalize(grid);
		MonsoonStrength = MonsoonIndex.Compute(grid, spec, MonthPressureHpa);
	}

	internal static float[][] ComputePrecipitationFromWind(ClimateGrid grid, ClimateSpec spec, float[] coastalWarmth,
		int seed, Vector3[][] monthlyLowWind, int[][] upstreamByMonth = null, float[][] transportSpeedByMonth = null)
	{
		var precipitation = new PrecipitationInputs(grid, spec, coastalWarmth, seed);
		var insolation = MonthlyInsolation.BuildYear(monthlyLowWind.Length, spec.ObliquityDeg);
		var solarWeight = new float[grid.Count];
		var monthly = new float[monthlyLowWind.Length][];
		for (int month = 0; month < monthly.Length; month++)
		{
			monthly[month] = new float[grid.Count];
			precipitation.BuildMonth(insolation[month].DeclinationRad, solarWeight, monthlyLowWind[month], monthly[month],
				upstreamByMonth?[month], transportSpeedByMonth?[month]);
		}
		return monthly;
	}

	void Allocate(int n)
	{
		MonthTempC = new float[MonthCount][];
		MonthPrecipMm = new float[MonthCount][];
		MonthPressureHpa = new float[MonthCount][];
		for (int m = 0; m < MonthCount; m++)
		{
			MonthTempC[m] = new float[n];
			MonthPrecipMm[m] = new float[n];
			MonthPressureHpa[m] = new float[n];
		}

		AnnualTempC = new float[n];
		AnnualPrecipMm = new float[n];
		AnnualPressureHpa = new float[n];
		MonsoonStrength = new float[n];
		HotMonthC = new float[n];
		ColdMonthC = new float[n];
		DryMonthMm = new float[n];
		DryMonthIndex = new int[n];
	}

	/// <summary>季风增雨权重按当地正午太阳高度标定。</summary>
	static void BuildPrecipitationSolarWeight(ClimateGrid grid, float deltaRad, float[] solarWeight)
	{
		for (int i = 0; i < grid.Count; i++)
			solarWeight[i] = MathF.Max(0f, MathF.Cos(grid.LatRad(i) - deltaRad));
	}

	/// <summary>把月地表温度折算为气压计算使用的海平面等效温度。</summary>
	static void BuildMonthSeaLevelTemp(ClimateGrid grid, ClimateSpec spec, float[] tempC, float[] seaLevelTemp)
	{
		for (int i = 0; i < grid.Count; i++)
			seaLevelTemp[i] = SeaLevelEquivalent(tempC[i], grid, spec, i);
	}

	/// <summary>海平面等效温度：陆地按海拔回补（海格与海平面以下不补），回补率取 spec 的递减率。</summary>
	static float SeaLevelEquivalent(float tempC, ClimateGrid grid, ClimateSpec spec, int i) =>
		tempC + (grid.Land[i] && grid.HeightM[i] > 0f ? spec.LapseRateCPerKm / 1000f * grid.HeightM[i] : 0f);

	sealed class PrecipitationInputs
	{
		readonly ClimateGrid _grid;
		readonly ClimateSpec _spec;
		readonly float[] _coastalWarmth;
		readonly Vector3[] _elevationGradient;
		readonly float[] _noise;
		readonly RainBands[] _rainBands;

		public PrecipitationInputs(ClimateGrid grid, ClimateSpec spec, float[] coastalWarmth, int seed)
		{
			_grid = grid;
			_spec = spec;
			_coastalWarmth = coastalWarmth;
			_elevationGradient = BuildElevationGradient(grid);
			var precipNoise = new SphericalFbmNoise(seed ^ 0x4D5E6F, spec.PrecipNoiseWavelengthKm, 4);
			_noise = new float[grid.Count];
			for (int i = 0; i < grid.Count; i++) _noise[i] = precipNoise.Sample(grid.Dirs[i]);
			_rainBands = BuildRainBands(grid, spec);
		}

		public void BuildMonth(float deltaRad, float[] solarWeight, Vector3[] windMonth, float[] destination,
			int[] upstream = null, float[] transportSpeed = null)
		{
			BuildPrecipitationSolarWeight(_grid, deltaRad, solarWeight);
			var moisture = BuildMoisture(_grid, _spec, windMonth, upstream, transportSpeed);
			float deltaDeg = deltaRad * 180f / MathF.PI;

			for (int i = 0; i < _grid.Count; i++)
			{
				float p = MonthPrecipBase(_grid, _spec, i, _rainBands[i], deltaDeg, _noise, _coastalWarmth);

				float monsoonWeight = moisture[i] * solarWeight[i];
				p *= 1f + _spec.MonsoonPrecipBoost * monsoonWeight;

				var wind = windMonth[i];
				if (wind.LengthSquared() > 1e-9f)
				{
					float upslope = wind.Normalized().Dot(_elevationGradient[i]);
					p *= 1f + Math.Clamp(upslope * _spec.OrographicGain, -0.65f, 0.65f);
				}

				destination[i] = MathF.Max(0f, p);
			}
		}
	}

	/// <summary>从海格沿每月的下游边传播，按到海的跳数衰减水汽权重。</summary>
	static float[] BuildMoisture(ClimateGrid grid, ClimateSpec spec, Vector3[] windMonth,
		int[] suppliedUpstream, float[] transportSpeed)
	{
		int n = grid.Count;
		var field = new float[n];
		var firstDownstream = new int[n];
		var nextDownstream = new int[n];
		Array.Fill(firstDownstream, -1);
		var queue = new Queue<int>();
		for (int i = 0; i < n; i++)
		{
			if (!grid.Land[i])
			{
				queue.Enqueue(i);
				continue;
			}

			int upstream = suppliedUpstream?[i] ?? FindUpstream(grid, i, windMonth[i]);
			if (upstream < 0) continue;
			nextDownstream[i] = firstDownstream[upstream];
			firstDownstream[upstream] = i;
		}

		// 每个陆格只有一个上游，因此最多入队一次，断链和闭环不会从海格传播到。
		var stepsToOcean = new int[n];
		while (queue.TryDequeue(out int source))
		{
			int steps = stepsToOcean[source] + 1;
			if (steps > spec.MoistureTraceSteps) break;
			for (int i = firstDownstream[source]; i >= 0; i = nextDownstream[i])
			{
				stepsToOcean[i] = steps;
				queue.Enqueue(i);

				float speed = transportSpeed?[i] ?? windMonth[i].Length();
				if (speed < 1e-6f) continue;
				// 弱风也保留季风水汽的基础权重，否则衰减会把整片弱风区压得过干。
				field[i] = (0.35f + 0.65f * speed) * MathF.Exp(-steps * spec.MoistureDecayPerStep);
			}
		}
		return field;
	}

	static int FindUpstream(ClimateGrid grid, int cell, Vector3 wind)
	{
		if (wind.LengthSquared() < 1e-12f) return -1;

		var upstreamDirection = -wind.Normalized();
		int upstream = -1;
		float bestDot = -2f;
		foreach (int neighbor in grid.Neighbors[cell])
		{
			float dot = grid.Dirs[neighbor].Dot(upstreamDirection);
			if (dot > bestDot)
			{
				bestDot = dot;
				upstream = neighbor;
			}
		}
		return upstream;
	}

	/// <summary>极锋和副高压制由纬度决定，所有月份共用。</summary>
	static RainBands[] BuildRainBands(ClimateGrid grid, ClimateSpec spec)
	{
		float subCenter = 26f + (spec.ObliquityDeg - 23.4f) * 0.12f;
		var bands = new RainBands[grid.Count];
		for (int i = 0; i < bands.Length; i++)
		{
			float latitude = grid.LatDeg(i);
			float latAbs = MathF.Abs(latitude);
			float polarFront = spec.PrecipPolarFrontMmPerMonth * MathF.Exp(-(latAbs - 62f) * (latAbs - 62f) / (2f * 14f * 14f));
			float subtropical = 1f - spec.PrecipSubtropicalSuppression
				* MathF.Exp(-(latAbs - subCenter) * (latAbs - subCenter) / (2f * 9f * 9f));
			bands[i] = new RainBands(latitude, polarFront, subtropical);
		}
		return bands;
	}

	/// <summary>月纬向降水骨架：月 ITCZ 摆动 + 极锋 + 副高压制 + 辐照 + 噪声 + 沿岸洋流 + 地形抬升。</summary>
	static float MonthPrecipBase(ClimateGrid grid, ClimateSpec spec, int i, RainBands bands, float deltaDeg,
		float[] noise, float[] coastalWarmth)
	{
		// ITCZ 必须用带符号纬度配带符号偏移：取绝对值会把摆动截断到正半轴，南半球雨季永久消失。
		float itczOffset = bands.Latitude - deltaDeg;
		float itcz = spec.PrecipItczMmPerMonth * MathF.Exp(-itczOffset * itczOffset / (2f * 12f * 12f));

		float p = (spec.PrecipBaseMmPerMonth + itcz + bands.PolarFront) * bands.Subtropical;
		p *= 1f + (spec.Insolation - 1f) * 0.30f;
		p *= 1f + noise[i] * spec.PrecipNoiseAmount;

		if (coastalWarmth != null) p *= 1f + coastalWarmth[i] * 0.25f;

		// 地形抬升：海拔越高越容易成雨（与风向无关的"高度基线"，风向雨影另算）。
		if (grid.Land[i] && grid.ElevNorm[i] > 0f)
			p *= 1f + MathF.Min(grid.ElevNorm[i], 1f) * 0.4f;

		return p;
	}

	void Finalize(ClimateGrid grid)
	{
		int n = grid.Count;

		for (int i = 0; i < n; i++)
		{
			float hot = float.MinValue, cold = float.MaxValue, dry = float.MaxValue;
			int dryIndex = 0;
			float tempSum = 0f, precipSum = 0f, pressureSum = 0f;

			for (int m = 0; m < MonthCount; m++)
			{
				float t = MonthTempC[m][i];
				tempSum += t;
				if (t > hot) hot = t;
				if (t < cold) cold = t;

				float p = MonthPrecipMm[m][i];
				precipSum += p;
				if (p < dry) { dry = p; dryIndex = m; }

				pressureSum += MonthPressureHpa[m][i];
			}

			AnnualTempC[i] = tempSum / MonthCount;
			AnnualPrecipMm[i] = precipSum;
			AnnualPressureHpa[i] = pressureSum / MonthCount;
			HotMonthC[i] = hot;
			ColdMonthC[i] = cold;
			DryMonthMm[i] = dry;
			DryMonthIndex[i] = dryIndex;
		}

		FinalizeWind(Wind.Surface);
		if (!ReferenceEquals(Wind.Low.Monthly, Wind.Surface.Monthly)) FinalizeWind(Wind.Low);
		if (!ReferenceEquals(Wind.Free.Monthly, Wind.Surface.Monthly)) FinalizeWind(Wind.Free);
	}

	/// <summary>年合成：矢量均值 + 标量均值风速（两者不是同一个统计量，都得存）。</summary>
	void FinalizeWind(WindSet set)
	{
		int n = set.Annual.Length;
		for (int i = 0; i < n; i++)
		{
			var sum = Vector3.Zero;
			float speedSum = 0f;
			for (int m = 0; m < MonthCount; m++)
			{
				var wind = set.Monthly[m][i];
				sum += wind;
				speedSum += wind.Length();
			}
			set.Annual[i] = sum / MonthCount;
			set.AnnualSpeed[i] = speedSum / MonthCount;
		}
	}

	/// <summary>海拔梯度 ∇h（归一化海拔）：风向点积它得到迎风坡 / 背风坡的垂直速度。</summary>
	static Vector3[] BuildElevationGradient(ClimateGrid grid)
	{
		int n = grid.Count;
		var gradient = new Vector3[n];
		for (int i = 0; i < n; i++)
		{
			var sum = Vector3.Zero;
			foreach (int nb in grid.Neighbors[i])
			{
				var edge = grid.Dirs[nb] - grid.Dirs[i];
				float len = edge.Length();
				if (len < 1e-9f) continue;
				sum += (grid.ElevNorm[nb] - grid.ElevNorm[i]) * (edge / len);
			}
			gradient[i] = sum;
		}
		return gradient;
	}
}
