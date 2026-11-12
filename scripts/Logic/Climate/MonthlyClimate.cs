using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 月度气候：12 个月的月温 → 气压 → 诊断风场 → 水汽追踪 → 月降水，外加季风强度与月极值。
///
/// 风不做时间积分：按“地转/热成风 + 三圈纬向风带 + 海陆温差季风 + 地形”诊断生成。
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

	/// <summary>三套风事实（地面 / 低空 / 自由）：同一次月循环、同一个气压梯度。</summary>
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
		CoarseLayer coarse, CancellationToken cancellation)
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

		var elevationGradient = BuildElevationGradient(grid);

		var precipNoise = new SphericalFbmNoise(seed ^ 0x4D5E6F, spec.PrecipNoiseWavelengthKm, 4);
		var noise = new float[n];
		for (int i = 0; i < n; i++) noise[i] = precipNoise.Sample(grid.Dirs[i]);

		var annualSeaLevel = new float[n];
		var rainBands = BuildRainBands(grid, spec);

		// 气压热力项的年均偏置取已求解的月温平均，再折算到海平面。
		for (int m = 0; m < MonthCount; m++)
		{
			cancellation.ThrowIfCancellationRequested();
			for (int i = 0; i < n; i++) annualSeaLevel[i] += MonthTempC[m][i];
		}
		for (int i = 0; i < n; i++)
			annualSeaLevel[i] = SeaLevelEquivalent(annualSeaLevel[i] / MonthCount, grid, spec, i);

		Wind = BuildWindField(n);
		var pressure = PressureFieldModel.Create(grid, spec, coarse ?? CoarseLayer.Identity(grid), annualSeaLevel);
		var windSolver = WindSolver.Create(grid, spec);
		// 年基准已固定，各月独立。工人只写自己的月度数组，年平均仍按月份顺序汇总。
		int workers = n >= 10_000 ? Math.Min(MonthCount, Math.Clamp(System.Environment.ProcessorCount - 1, 1, 6)) : 1;
		if (workers == 1) BuildMonths(0);
		else Parallel.For(0, workers, new ParallelOptions { CancellationToken = cancellation }, BuildMonths);

		Finalize(grid);
		MonsoonStrength = MonsoonIndex.Compute(grid, spec, MonthPressureHpa);

		void BuildMonths(int worker)
		{
			var precipitationSolarWeight = new float[n];
			var seaLevel = new float[n];
			var pressureGradient = new Vector3[n];
			var workspace = pressure.CreateWorkspace();
			for (int m = worker; m < MonthCount; m += workers)
			{
				cancellation.ThrowIfCancellationRequested();
				float deltaRad = insolation[m].DeclinationRad;
				BuildPrecipitationSolarWeight(grid, deltaRad, precipitationSolarWeight);
				BuildMonthSeaLevelTemp(grid, spec, MonthTempC[m], seaLevel);
				pressure.BuildMonth(seaLevel, deltaRad, MonthPressureHpa[m], workspace);

				TangentField.GradientField(grid, MonthPressureHpa[m], pressureGradient);
				windSolver.SolveAll(pressureGradient,
					Wind.Surface.Monthly[m], Wind.Low.Monthly[m], Wind.Free.Monthly[m]);
				BuildMonthPrecip(grid, spec, m, deltaRad, rainBands, precipitationSolarWeight, noise, coastalWarmth,
					elevationGradient, Wind.Low.Monthly[m]);
			}
		}
	}

	WindField BuildWindField(int n) =>
		new(AllocateWindSet(WindKind.Surface, n), AllocateWindSet(WindKind.Low, n),
			AllocateWindSet(WindKind.Free, n));

	WindSet AllocateWindSet(WindKind kind, int n)
	{
		var monthly = new Vector3[MonthCount][];
		for (int m = 0; m < MonthCount; m++) monthly[m] = new Vector3[n];
		return new WindSet(kind, monthly, new Vector3[n], new float[n]);
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

	void BuildMonthPrecip(ClimateGrid grid, ClimateSpec spec, int month, float deltaRad, RainBands[] rainBands, float[] solarWeight,
		float[] noise, float[] coastalWarmth, Vector3[] elevationGradient, Vector3[] windMonth)
	{
		int n = grid.Count;
		var moisture = BuildMoisture(grid, spec, windMonth);
		float deltaDeg = deltaRad * 180f / MathF.PI;

		for (int i = 0; i < n; i++)
		{
			float p = MonthPrecipBase(grid, spec, i, rainBands[i], deltaDeg, noise, coastalWarmth);

			float monsoonWeight = moisture[i] * solarWeight[i];
			p *= 1f + spec.MonsoonPrecipBoost * monsoonWeight;

			var wind = windMonth[i];
			if (wind.LengthSquared() > 1e-9f)
			{
				float upslope = wind.Normalized().Dot(elevationGradient[i]);
				p *= 1f + Math.Clamp(upslope * spec.OrographicGain, -0.65f, 0.65f);
			}

			p = MathF.Max(0f, p);
			MonthPrecipMm[month][i] = p;
		}
	}

	/// <summary>逐格水汽权重：陆地沿风上游走到海，得到季风水汽（步数越多衰减越大）。</summary>
	/// <remarks>
	/// 上游邻格先按月份预计算（每格一次 O(6)），再沿链记忆化求步数——
	/// 逐格重走 25 步的朴素写法在 res4 是 ~1.8 亿次点积，记忆化后摊还 O(n)。
	/// </remarks>
	float[] BuildMoisture(ClimateGrid grid, ClimateSpec spec, Vector3[] windMonth)
	{
		int n = grid.Count;
		var field = new float[n];
		var upstreamOf = new int[n];
		var stepsToOcean = new int[n];

		for (int i = 0; i < n; i++)
		{
			upstreamOf[i] = -1;
			stepsToOcean[i] = grid.Land[i] ? -1 : 0;

			var wind = windMonth[i];
			if (!grid.Land[i] || wind.LengthSquared() < 1e-12f) continue;

			var upstream = -wind.Normalized();
			int best = -1;
			float bestDot = -2f;
			foreach (int nb in grid.Neighbors[i])
			{
				float dot = grid.Dirs[nb].Dot(upstream);
				if (dot > bestDot) { bestDot = dot; best = nb; }
			}
			upstreamOf[i] = best;
		}

		// 链条可能成环（风场闭环）⇒ 用"未知即 -1 + 显式栈"解析，环上整条链判为无海洋。
		var chain = new List<int>();
		for (int i = 0; i < n; i++)
		{
			if (!grid.Land[i] || stepsToOcean[i] >= 0) continue;

			chain.Clear();
			int current = i;
			int depth = 0;
			while (current >= 0 && stepsToOcean[current] < 0 && depth <= spec.MoistureTraceSteps)
			{
				chain.Add(current);
				current = upstreamOf[current];
				depth++;
			}

			bool resolved = current >= 0 && stepsToOcean[current] >= 0;
			int steps = resolved ? stepsToOcean[current] : int.MaxValue;
			for (int k = chain.Count - 1; k >= 0; k--)
			{
				steps = steps == int.MaxValue ? int.MaxValue : steps + 1;
				stepsToOcean[chain[k]] = steps;
			}
		}

		for (int i = 0; i < n; i++)
		{
			if (!grid.Land[i]) continue;
			float speed = windMonth[i].Length();
			int steps = stepsToOcean[i];
			if (speed < 1e-6f || steps <= 0 || steps > spec.MoistureTraceSteps) continue;

			// 弱风也要有季风"存在性"（0.35 底），否则乘法衰减会把整个季风区压到阈值下。
			field[i] = (0.35f + 0.65f * speed) * MathF.Exp(-steps * spec.MoistureDecayPerStep);
		}
		return field;
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
