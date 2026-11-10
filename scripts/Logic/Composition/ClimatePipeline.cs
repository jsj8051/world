using System;
using System.Runtime.CompilerServices;
using System.Threading;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// **② 气候阶段**：温度 / 气压 / 风 / 洋流 / 降水 / 季风，全部由同一支物理链推出。
///
/// 因果序（不可换：后段吃前段产物）：
/// <code>
/// 洋流第一遍（解析盛行风驱动）
///   → 年基温（纬度 + 沿岸冷暖 + 海拔 + 噪声）
///   → 湿润降温闭环（纬向估算降水，一步解析）
///   → 月循环：月温 → 气压（三项叠加）→ 诊断风场（地转 + 风带 + 季风 + 地形）→ 水汽追踪 → 月降水 / 季风
///   → 温度（年均 = mean 月）、降水（年 = Σ 月）、气压（年均 = mean 月）、风（年合成）
///   → 洋流第二遍（统一风场 + 年均温热成风）——存档场
/// </code>
///
/// ★只消费 ① 的世界事实（海陆 / 海岸距离 / 海拔），不看生成依据。
/// ★洋流第一遍与第二遍的差别是旧链的既有取舍：温度用第一遍风驱洋流（生成顺序限制），
///   第二遍才是最终存档场。差异只在沿岸 ±数 °C，登记不掩盖。
/// </summary>
public sealed class ClimatePipeline
{
	static readonly ConditionalWeakTable<Ball, CoarseLayer> PressureGrids = new();
	/// <summary>降水事实（年 = Σ 月；水文阶段 ③ 的输入）。</summary>
	public PrecipitationModel Precipitation { get; private set; } = new();

	/// <summary>温度事实（年均 + 逐月 + 月极值）。</summary>
	public Temperature Temperature { get; private set; } = new();

	/// <summary>风事实（三套视图：地面 / 低空 / 自由，各含逐月 + 年合成）。</summary>
	public WindField Wind { get; private set; }

	/// <summary>气压事实（逐月 + 年均，hPa）。</summary>
	public PressureField Pressure { get; private set; } = new();

	/// <summary>洋流事实（方向 / 冷暖 / 强度 / 流函数）。</summary>
	public OceanCurrentModel OceanCurrents { get; private set; } = new();

	/// <summary>季风强度 ∈ [0,1]（逐格）。</summary>
	public float[] MonsoonStrength { get; private set; } = Array.Empty<float>();

	/// <summary>最干月降水（mm）。</summary>
	public float[] DryMonthMm { get; private set; } = Array.Empty<float>();

	/// <summary>最干月月份（0–11）。</summary>
	public int[] DryMonthIndex { get; private set; } = Array.Empty<int>();

	/// <summary>导入地图：直接装配已保存的气候结果（不重算温度、降水、风或洋流）。</summary>
	internal void LoadSaved(PrecipitationModel precipitation, Temperature temperature, PressureField pressure,
		OceanCurrentModel oceanCurrents, WindField wind, float[] monsoonStrength, float[] dryMonthMm,
		int[] dryMonthIndex)
	{
		Precipitation = precipitation ?? throw new ArgumentNullException(nameof(precipitation));
		Temperature = temperature ?? throw new ArgumentNullException(nameof(temperature));
		Pressure = pressure ?? throw new ArgumentNullException(nameof(pressure));
		OceanCurrents = oceanCurrents ?? throw new ArgumentNullException(nameof(oceanCurrents));
		Wind = wind ?? throw new ArgumentNullException(nameof(wind));
		MonsoonStrength = monsoonStrength ?? throw new ArgumentNullException(nameof(monsoonStrength));
		DryMonthMm = dryMonthMm ?? throw new ArgumentNullException(nameof(dryMonthMm));
		DryMonthIndex = dryMonthIndex ?? throw new ArgumentNullException(nameof(dryMonthIndex));
	}

	/// <param name="seed">世界种子（温度/降水噪声按值展开，阶段不收 <see cref="WorldSpec"/>）。</param>
	public void Run(Ball ball, TerrainPipeline terrain, float seaLevelM, ClimateSpec spec, int seed)
		=> Run(ball, terrain, seaLevelM, spec, seed, CancellationToken.None);

	internal void Run(Ball ball, TerrainPipeline terrain, float seaLevelM, ClimateSpec spec, int seed,
		CancellationToken cancellation)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (terrain == null) throw new ArgumentNullException(nameof(terrain));
		if (spec == null) throw new ArgumentNullException(nameof(spec));

		long timing = WorldBuildProfile.Start();
		var grid = ClimateGrid.From(ball, terrain, seaLevelM);

		var prevailing = PrevailingWind.BuildAll(grid.Dirs, spec.RotationSpeed, spec.Prograde);
		var relaxationCells = OceanCurrentModel.BuildRelaxationCells(grid);
		WorldBuildProfile.Mark(ref timing, "climate-input");
		OceanCurrents.Compute(grid, spec, prevailing, oceanTempC: null, initialPsi: null, iterationCap: 120,
			relaxationCells, cancellation);
		WorldBuildProfile.Mark(ref timing, "ocean-initial");
		cancellation.ThrowIfCancellationRequested();
		var coastalWarmth = OceanCurrents.BuildCoastalWarmth(grid);

		var baseTemperature = new TemperatureModel();
		baseTemperature.Compute(grid, spec, coastalWarmth, seed);

		var precipEstimate = PrecipitationModel.EstimateAnnualMm(grid, coastalWarmth);
		TemperatureModel.ApplyWetCooling(baseTemperature.AnnualC, precipEstimate, spec);

		var monthly = new MonthlyClimate();
		// 长波平滑只在 res2 粗层做（res4 上 650 km 核要 27 跳，放不下）；小世界直接用细层。
		var coarse = ball.Res > 2
			? PressureGrids.GetValue(ball, static b => CoarseLayer.Parent(b, 2))
			: CoarseLayer.Identity(grid);
		WorldBuildProfile.Mark(ref timing, "monthly-input");
		monthly.Compute(grid, spec, baseTemperature.AnnualC, coastalWarmth, seed, coarse, cancellation);
		WorldBuildProfile.Mark(ref timing, "monthly-climate");
		cancellation.ThrowIfCancellationRequested();

		Temperature.Adopt(monthly);
		Precipitation.Adopt(monthly);
		Wind = monthly.Wind;
		Pressure.Adopt(monthly);
		MonsoonStrength = monthly.MonsoonStrength;
		DryMonthMm = monthly.DryMonthMm;
		DryMonthIndex = monthly.DryMonthIndex;

		// 第二遍：自由低层风年合成 + 年均温热成风，作为最终存档洋流。
		// 热启动上一遍的 ψ，并把遍数压到 300：res4 下残差 ~1e-4（准静态近似，见模型注释）。
		OceanCurrents.Compute(grid, spec, monthly.Wind.Free.Annual, monthly.AnnualTempC,
			initialPsi: OceanCurrents.Psi, iterationCap: 300, relaxationCells, cancellation);
		WorldBuildProfile.Mark(ref timing, "ocean-final");
	}
}
