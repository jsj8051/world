using System;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 河网：Final 地形上的单流向汇流网络 + 河流掩码。
/// 只依赖 Final 世界事实（FinalLand 掩码 + 最终高度），不读 ⓪ 的板块划分 / 壳型，也不知道 ① 的噪声场与构造项。
/// 流程：Final 地形 → 单流向（严格降高、平局取格索引最小）→ 汇流累积 → 河流掩码；
/// 有降水时按径流（mm）判河，无降水时退化为格数判据，两者在同 res 下等价。
/// 海洋是最终出口：elev ≤ seaLevel 与 FinalLand 严格互补，没有第二套海陆判断。
/// </summary>
public sealed class RiverNetwork
{
	/// <summary>下游格索引（−1 = 出口：入海或内流洼地）。</summary>
	public int[] Downstream { get; private set; } = Array.Empty<int>();
	/// <summary>汇流累积（上游格数 + 1，含自身；海格恒 1）。拓扑量，res 一变含义就变。</summary>
	public int[] FlowAccum { get; private set; } = Array.Empty<int>();
	/// <summary>汇水面积（几何量，km²）= <see cref="FlowAccum"/> × 格面积。
	/// 这是水文里的 <i>catchment area</i>——判断"这条河有多大流域"的唯一正确口径。</summary>
	public double[] FlowAccumKm2 { get; private set; } = Array.Empty<double>();
	/// <summary>河流掩码（FinalLand ∧ 汇流达到阈值）。</summary>
	public bool[] IsRiver { get; private set; } = Array.Empty<bool>();
	/// <summary>内流洼地格数（无更低邻居且不临海的陆格；湖泊语义候选，只计数）。</summary>
	public int SinkCount { get; private set; }
	/// <summary>径流加权汇流（mm；权重 = 逐格年降水；无降水输入时 = 格数）。
	/// 这是"等效水深"不是体积——要体积用 <see cref="RunoffAccumKm3"/>。</summary>
	public float[] RunoffAccum { get; private set; } = Array.Empty<float>();
	/// <summary>径流累积体积（物理量，km³/年）= <see cref="RunoffAccum"/> × 格面积 / 1e6。</summary>
	public double[] RunoffAccumKm3 { get; private set; } = Array.Empty<double>();
	/// <summary>径流河流阈值（mm；= 均雨 × 阈值格数等效）。</summary>
	public float RunoffThreshold { get; private set; }
	/// <summary>本次实际生效的阈值（几何量，km² 汇水面积）——跨 res 可比的唯一口径。</summary>
	public double RiverThresholdKm2 { get; private set; }
	/// <summary>水文路由表面：本类在它上面跑汇流，而不是在 <c>composer.HeightM</c> 上。
	/// 开关关闭时与源高度逐点相同 ⇒ 既有行为零漂移。</summary>
	public HydrologyRoutingSurface Routing { get; private set; } = new();

	/// <summary>河流判定阈值（拓扑量：汇流累积格数）。只在固定 res 下有意义：40 格在 res1
	/// （842 格，每格 1,711 km²）是大陆级流域，在 res4（每格 5 km²）= 200 km²，差 342 倍。
	/// 新代码请用 <c>riverThresholdAreaKm2</c>（km²）。</summary>
	public const int RiverThresholdCells = 40;

	/// <summary>河流判定阈值的物理口径（km²）：200 km² = 40 格 × 5 km²/格
	/// （res4 下与拓扑口径等价；res1 下 1 格即达标）。</summary>
	public const double RiverThresholdAreaKm2 = 200.0;

	/// <summary>生成河网。<paramref name="riverThresholdCells"/> 是拓扑口径（格数），
	/// <paramref name="riverThresholdAreaKm2"/> 是物理口径（km²），后者优先。
	/// 同 res 下两者完全等价（× 格面积是等价变换）；跨 res 才显出物理阈值尺度无关。</summary>
	public void Generate(Ball ball, FinalGeography final, float[] heightM, float seaLevelM,
		int riverThresholdCells = RiverThresholdCells, float[] annualPrecipMm = null,
		double? riverThresholdAreaKm2 = null, bool? useDepressionFill = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));

		// ── 水文路由表面 ──
		// 在派生表面上跑汇流而非最终高度：Final 地形事实不动（地貌 / 渲染 / 原始洼地语义不受影响），
		// 填洼只作用于路由；开关关闭 ⇒ RoutingHeightM 与源高度逐点相同 ⇒ 零漂移。
		Routing = new HydrologyRoutingSurface();
		Routing.Generate(ball, heightM, seaLevelM, useDepressionFill);

		var hydro = new H3Hydrology();
		// 权重 = 逐格年降水；无降水输入时 WeightedAccum 退化为上游格数（同 FlowAccum），行为不漂移。
		hydro.Generate(ball, Routing.RoutingHeightM, seaLevelM, annualPrecipMm);
		Downstream = hydro.Downstream;
		FlowAccum = hydro.FlowAccum;
		RunoffAccum = hydro.WeightedAccum;
		SinkCount = hydro.SinkCount;

		// ── 阈值归一到物理口径 ──
		// 内部判据统一用 km²；同 res 下面积口径与格数口径逐格完全一致（等价变换），跨 res 才显示尺度差异。
		double cellKm2 = SpatialScale.Of(ball).CellAreaKm2;
		double thresholdKm2 = riverThresholdAreaKm2 ?? riverThresholdCells * cellKm2;
		RiverThresholdKm2 = thresholdKm2;

		// 河流判据：有降水 → 径流阈值（均雨 × 阈值格数等效）；无 → 格数判据。
		// 两种情形都换算到"等效格数"再比——RunoffAccum 是 mm（等效水深），格数当量 = 阈值面积 / 格面积。
		float meanPrecip = 1f;
		if (annualPrecipMm != null)
		{
			float sum = 0f;
			for (int i = 0; i < annualPrecipMm.Length; i++) sum += annualPrecipMm[i];
			meanPrecip = sum / annualPrecipMm.Length;
		}
		double thresholdCellsEq = thresholdKm2 / cellKm2;
		RunoffThreshold = meanPrecip * (float)thresholdCellsEq;

		int n = Downstream.Length;
		FlowAccumKm2 = new double[n];
		RunoffAccumKm3 = new double[n];
		for (int i = 0; i < n; i++)
		{
			FlowAccumKm2[i] = FlowAccum[i] * cellKm2;
			RunoffAccumKm3[i] = RunoffAccum[i] * cellKm2 / 1e6;   // mm × km² → km³
		}

		IsRiver = new bool[n];
		for (int i = 0; i < n; i++)
			IsRiver[i] = final.FinalLand[i] && (annualPrecipMm != null
				? RunoffAccum[i] >= RunoffThreshold
				: FlowAccum[i] >= thresholdCellsEq);
	}
}
