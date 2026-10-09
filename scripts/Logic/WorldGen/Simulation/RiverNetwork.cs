using System;
using World.H3Grid;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 世界生成空间 · 水文层（World Simulation 的**第一个下游消费者**，决策 08 v2）：
//   架构边界验证口——本类**只依赖 Final 世界事实**（FinalLand 掩码 + 最终高度数组），
//   不读 PlacementLand、不知道任何 LandformFeature/投影器/噪声的存在。
//   生成阶段的实现细节对它不可见——这是"Final World + Consumers"架构的成立性检验。
// 第一版（刻意克制，决策 08 v2 §3）：Final 地形 → 单流向 → 汇流累积 → 河流掩码。
//   只回答四件事：① 河流只在 FinalLand 上运行；② 海洋是最终汇出口；③ 高程梯度稳定
//   产生下坡流；④ 多条支流自然汇合。
// River 2A（决策 08 v2 §River2A）：降水 → 径流——PrecipitationModel（Final 层只读的
//   World Simulation 输入）→ H3Hydrology 逐格径流权重 → **RunoffAccum**（mm 加权汇流）。
//   河流判据升级为径流阈值（均雨 × 格数等效）；无降水数据时退化为格数判据（W1 行为）。
// ★契约（决策 08 v2 §2）：本类不得依赖 FinalSpatialIndex——基础水文拓扑由自身 flow
//   graph 决定，SpatialIndex 只做二级查询（反射钉子在 ArchitectureContractTests）。
// 复用：H3Hydrology（W1 基建激活）——单流向（严格降高、平局取格索引最小）+ 拓扑序汇流。
//   海口判定 elev ≤ 0 与 FinalLand（HeightM > 0 = 陆）严格互补 ⇒ 无第二套海陆判断。
/// <summary>
/// 河网（水文第一版）：Final 地形上的单流向汇流网络 + 河流掩码。
/// </summary>
public sealed class RiverNetwork
{
	/// <summary>下游格索引（−1 = 出口：入海或内流洼地）。</summary>
	public int[] Downstream { get; private set; } = Array.Empty<int>();
	/// <summary>汇流累积（上游格数 + 1，含自身；海格恒 1）。**拓扑量**，res 一变含义就变。</summary>
	public int[] FlowAccum { get; private set; } = Array.Empty<int>();
	/// <summary>
	/// 汇水面积（**几何量**，km²）= <see cref="FlowAccum"/> × 格面积（收口 D-10）。
	/// 这就是水文里的 <i>catchment area</i>——判断"这条河有多大流域"的**唯一**正确口径。
	/// </summary>
	public double[] FlowAccumKm2 { get; private set; } = Array.Empty<double>();
	/// <summary>河流掩码（FinalLand ∧ 汇流达到阈值）。</summary>
	public bool[] IsRiver { get; private set; } = Array.Empty<bool>();
	/// <summary>内流洼地格数（无更低邻居且不临海的陆格——湖泊语义候选，本版只计数）。</summary>
	public int SinkCount { get; private set; }
	/// <summary>径流加权汇流（mm；River 2A——权重 = 逐格年降水；无降水输入时 = 格数）。
	/// **这是"等效水深"不是体积**——要体积用 <see cref="RunoffAccumKm3"/>。</summary>
	public float[] RunoffAccum { get; private set; } = Array.Empty<float>();
	/// <summary>径流累积体积（**物理量**，km³/年）= <see cref="RunoffAccum"/> × 格面积 / 1e6。</summary>
	public double[] RunoffAccumKm3 { get; private set; } = Array.Empty<double>();
	/// <summary>径流河流阈值（mm；= 均雨 × 阈值格数等效）。</summary>
	public float RunoffThreshold { get; private set; }
	/// <summary>本次实际生效的阈值（**几何量**，km² 汇水面积）——跨 res 可比的唯一口径。</summary>
	public double RiverThresholdKm2 { get; private set; }
	/// <summary>
	/// **水文路由表面**（D-11 Batch A）：本类在它上面跑汇流，而不是在 `composer.HeightM` 上。
	/// 默认（开关关闭）时它与源高度**逐点相同** ⇒ 既有行为零漂移。
	/// </summary>
	public HydrologyRoutingSurface Routing { get; private set; } = new();

	/// <summary>河流判定阈值（**拓扑量**：汇流累积格数）。
	/// ⚠️ 收口 D-10 警告：该参数**只在固定 res 下有意义**——40 格在 res1（842 格，每格
	/// 60.6 万 km²）= 2,431 万 km² 流域，在 res4 = 7.08 万 km²，<b>差 34 倍</b>。
	/// 新代码请用 <c>riverThresholdAreaKm2</c>（km²）。</summary>
	public const int RiverThresholdCells = 40;

	/// <summary>河流判定阈值的**物理口径**（km² 汇水面积）：70,800 km² ≈ 黄河流域量级
	/// （res4 下 ≈ 40 格，与旧默认阈值等价 ⇒ 平滑迁移；res1 下只需 1 格即达标）。</summary>
	public const double RiverThresholdAreaKm2 = 70_800.0;

	/// <summary>生成河网。
	/// <paramref name="riverThresholdCells"/> 是**拓扑口径**（格数）；传
	/// <paramref name="riverThresholdAreaKm2"/> 则改用**物理口径**（km²），后者优先。
	/// 二者在同一 res 下判据**完全等价**（× 格面积是等价变换）⇒ 迁移零行为漂移，
	/// 跨 res 时才显出"物理阈值才是尺度无关的那个"。
	/// </summary>
	public void Generate(Ball ball, FinalGeography final, HeightComposer composer,
		int riverThresholdCells = RiverThresholdCells, float[] annualPrecipMm = null,
		double? riverThresholdAreaKm2 = null, bool? useDepressionFill = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (composer == null) throw new ArgumentNullException(nameof(composer));

		// ── 水文路由表面（D-11 Batch A）─────────────────────────────────────────
		// ★在**派生表面**上跑汇流，而不是直接用 `composer.HeightM`：
		//   Final 地形事实保持不动（地貌/渲染/LakeState 原始洼地语义不受影响），
		//   填洼只作用于水文路由。开关关闭 ⇒ RoutingHeightM 与源高度逐点相同 ⇒ 零漂移。
		Routing = new HydrologyRoutingSurface();
		Routing.Generate(ball, composer.HeightM, 0f, useDepressionFill);

		// H3Hydrology 的海判定（elev ≤ seaLevel=0）与 FinalLand（HeightM > 0 = 陆）严格互补
		var hydro = new H3Hydrology();
		// 权重 = 逐格年降水（River 2A）：无降水输入时 cellWeight=null ⇒ WeightedAccum 退化为
		// 上游格数（与 FlowAccum 同义），W1 行为不漂移
		hydro.Generate(ball, Routing.RoutingHeightM, 0f, annualPrecipMm);
		Downstream = hydro.Downstream;
		FlowAccum = hydro.FlowAccum;
		RunoffAccum = hydro.WeightedAccum;
		SinkCount = hydro.SinkCount;

		// ── 阈值归一到物理口径（收口 D-10）────────────────────────────────
		// 内部判据统一用 km²；格数入口在此乘一次格面积换算。
		// ★退化为等价变换：同 res 下 `FlowAccum ≥ T格` ⟺ `FlowAccum × A ≥ T格 × A`，
		//   所以走面积口径与旧格数口径**逐格完全一致**（退化解测试钉死这一点）。
		double cellKm2 = SpatialScale.Of(ball).CellAreaKm2;
		double thresholdKm2 = riverThresholdAreaKm2 ?? riverThresholdCells * cellKm2;
		RiverThresholdKm2 = thresholdKm2;

		// 河流判据（River 2A）：有降水 → 径流阈值（均雨 × 阈值格数等效）；无 → 退化格数判据。
		// 两种情形都换算到"等效格数"再比——因为 RunoffAccum 的单位是 mm（等效水深），
		// 它的"格数当量" = 阈值面积 / 格面积。
		float meanPrecip = 1f;
		if (annualPrecipMm != null)
		{
			float sum = 0f;
			for (int i = 0; i < annualPrecipMm.Length; i++) sum += annualPrecipMm[i];
			meanPrecip = sum / annualPrecipMm.Length;
		}
		double thresholdCellsEq = thresholdKm2 / cellKm2;      // 物理阈值 → 等效格数
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
