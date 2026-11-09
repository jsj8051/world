using System;

namespace World.Data;

/// <summary>
/// 地壳演化配置：时间步、物理速率、壳厚、密度、火山与数值容差。
/// 属性初值 = 出厂档，须与 <c>res/params/world_params.json</c> 的 <c>Evolution</c> 段同值。
/// 只放能改变演化结果的旋钮；运行期时间与库存不在这里。
/// </summary>
public sealed class EvolutionSpec
{
	/// <summary>固定提交步长（年）。出厂 100,000。位移较大的步自动拆成多个构造子步。</summary>
	public long StepYears { get; set; } = 100_000;

	/// <summary>零步地幔库存（等效材料 km³）。出厂 1e10；不是真实地幔体积。</summary>
	public double InitialMantleVolumeKm3 { get; set; } = 10_000_000_000d;

	/// <summary>旧无量纲 <c>Plate.Omega</c> 转物理角速度的比例（rad/Ma）。出厂 0.01。</summary>
	public double LegacyOmegaScaleRadPerMa { get; set; } = 0.01d;

	/// <summary>构造子步最大位移占最小邻格距的比例。出厂 0.25。</summary>
	public double MaxDisplacementFraction { get; set; } = 0.25d;

	/// <summary>初始陆壳厚度（km）。出厂 35。</summary>
	public double InitialContinentalThicknessKm { get; set; } = 35d;

	/// <summary>初始洋壳厚度（km）。出厂 7。</summary>
	public double InitialOceanicThicknessKm { get; set; } = 7d;

	/// <summary>新生洋壳厚度（km）。出厂 7。</summary>
	public double NewOceanicThicknessKm { get; set; } = 7d;

	/// <summary>零步统一海底年龄（Ma）。出厂 80；不作历史反演。</summary>
	public double InitialOceanAgeMa { get; set; } = 80d;

	/// <summary>新生洋壳参考高程（m）。出厂 −2500。</summary>
	public double NewOceanElevationM { get; set; } = -2500d;

	/// <summary>活动边界阈值（km/Ma）。出厂 1。</summary>
	public double ActiveSpeedKmPerMa { get; set; } = 1d;

	/// <summary>伸展破裂阈值（累计应变）。出厂 1。</summary>
	public double RiftBreakupStrain { get; set; } = 1d;

	/// <summary>陆壳密度（kg/m³）。出厂 2800。</summary>
	public double ContinentalDensityKgM3 { get; set; } = 2800d;

	/// <summary>洋壳密度（kg/m³）。出厂 2900，须不小于陆壳且小于地幔。</summary>
	public double OceanicDensityKgM3 { get; set; } = 2900d;

	/// <summary>地幔密度（kg/m³）。出厂 3300，用于均衡换算。</summary>
	public double MantleDensityKgM3 { get; set; } = 3300d;

	/// <summary>冷却沉降系数（m/√Ma）。出厂 350；只计相对参考年龄的增量。</summary>
	public double CoolingDepthMPerSqrtMa { get; set; } = 350d;

	/// <summary>冷却沉降的年龄封顶（Ma）。出厂 150；材料实际年龄不封顶。</summary>
	public double MaxCoolingAgeMa { get; set; } = 150d;

	/// <summary>洋中脊火山厚度增生速率（km/Ma）。出厂 0.02。</summary>
	public double RidgeVolcanicRateKmPerMa { get; set; } = 0.02d;

	/// <summary>活动洋中脊的单侧地形过渡距离（km）。出厂 180。</summary>
	public double OceanRidgeReachKm { get; set; } = 180d;

	/// <summary>俯冲弧火山厚度增生速率（km/Ma）。出厂 0.01。</summary>
	public double ArcVolcanicRateKmPerMa { get; set; } = 0.01d;

	/// <summary>火山增生中计入局部地形的份额 ∈ [0,1]。出厂 0.2。</summary>
	public double VolcanicReliefFraction { get; set; } = 0.2d;

	/// <summary>俯冲弧作用宽度（km）。出厂 200。</summary>
	public double ArcReachKm { get; set; } = 200d;

	/// <summary>海沟非均衡地形尺度（m）。出厂 3000。</summary>
	public double TrenchReliefScaleM { get; set; } = 3000d;

	/// <summary>海沟作用带宽（km）。出厂 100。</summary>
	public double TrenchReachKm { get; set; } = 100d;

	/// <summary>裂谷非均衡地形尺度（m）。出厂 1000。</summary>
	public double RiftReliefScaleM { get; set; } = 1000d;

	/// <summary>构造地形历史响应时间（Ma）。出厂 1。</summary>
	public double TectonicRelaxationMa { get; set; } = 1d;

	/// <summary>体积绝对容差（km³）。出厂 0.001。</summary>
	public double VolumeAbsoluteToleranceKm3 { get; set; } = 0.001d;

	/// <summary>体积相对容差。出厂 1e-8。</summary>
	public double VolumeRelativeTolerance { get; set; } = 1e-8d;

	/// <summary>单格覆盖面积相对容差。出厂 1e-8。</summary>
	public double CoverageRelativeTolerance { get; set; } = 1e-8d;

	/// <summary>球面几何角度容差（rad）。出厂 1e-10。</summary>
	public double GeometryAngularToleranceRad { get; set; } = 1e-10d;

	/// <summary>复制全部公开可写属性到 <paramref name="target"/>（不动实例身份）。</summary>
	/// <example><code>spec.CopyTo(frozen.Evolution);</code></example>
	public void CopyTo(EvolutionSpec target) => SpecCopy.CopyProperties(this, target);

	/// <summary>
	/// 校验全部标量范围与跨字段约束；非法即抛，不做静默截断。
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">数值越界或不是有限值。</exception>
	/// <exception cref="ArgumentException">跨字段关系不成立（如密度排序）。</exception>
	/// <example><code>new EvolutionSpec().Validate();</code></example>
	public void Validate()
	{
		Require(StepYears > 0, nameof(StepYears), "必须大于 0");
		Require(InitialMantleVolumeKm3 >= 0, nameof(InitialMantleVolumeKm3), "不得为负");
		Require(LegacyOmegaScaleRadPerMa > 0, nameof(LegacyOmegaScaleRadPerMa), "必须大于 0");
		Require(MaxDisplacementFraction > 0 && MaxDisplacementFraction <= 0.25,
			nameof(MaxDisplacementFraction), "须在 (0, 0.25]");
		Require(InitialContinentalThicknessKm > 0, nameof(InitialContinentalThicknessKm), "必须大于 0");
		Require(InitialOceanicThicknessKm > 0, nameof(InitialOceanicThicknessKm), "必须大于 0");
		Require(NewOceanicThicknessKm > 0, nameof(NewOceanicThicknessKm), "必须大于 0");
		Require(InitialOceanAgeMa >= 0, nameof(InitialOceanAgeMa), "不得为负");
		Require(double.IsFinite(NewOceanElevationM), nameof(NewOceanElevationM), "必须是有限数");
		Require(ActiveSpeedKmPerMa > 0, nameof(ActiveSpeedKmPerMa), "必须大于 0");
		Require(RiftBreakupStrain > 0, nameof(RiftBreakupStrain), "必须大于 0");
		Require(ContinentalDensityKgM3 > 0, nameof(ContinentalDensityKgM3), "必须大于 0");
		Require(double.IsFinite(OceanicDensityKgM3), nameof(OceanicDensityKgM3), "必须是有限数");
		Require(double.IsFinite(MantleDensityKgM3), nameof(MantleDensityKgM3), "必须是有限数");
		Require(CoolingDepthMPerSqrtMa >= 0, nameof(CoolingDepthMPerSqrtMa), "不得为负");
		Require(MaxCoolingAgeMa > 0, nameof(MaxCoolingAgeMa), "必须大于 0");
		Require(RidgeVolcanicRateKmPerMa >= 0, nameof(RidgeVolcanicRateKmPerMa), "不得为负");
		Require(double.IsFinite(OceanRidgeReachKm) && OceanRidgeReachKm > 0, nameof(OceanRidgeReachKm), "必须是正的有限数");
		Require(ArcVolcanicRateKmPerMa >= 0, nameof(ArcVolcanicRateKmPerMa), "不得为负");
		Require(VolcanicReliefFraction >= 0 && VolcanicReliefFraction <= 1,
			nameof(VolcanicReliefFraction), "须在 [0, 1]");
		Require(ArcReachKm > 0, nameof(ArcReachKm), "必须大于 0");
		Require(TrenchReliefScaleM >= 0, nameof(TrenchReliefScaleM), "不得为负");
		Require(TrenchReachKm > 0, nameof(TrenchReachKm), "必须大于 0");
		Require(RiftReliefScaleM >= 0, nameof(RiftReliefScaleM), "不得为负");
		Require(TectonicRelaxationMa > 0, nameof(TectonicRelaxationMa), "必须大于 0");
		Require(VolumeAbsoluteToleranceKm3 >= 0, nameof(VolumeAbsoluteToleranceKm3), "不得为负");
		Require(VolumeRelativeTolerance >= 0 && VolumeRelativeTolerance <= 1e-4,
			nameof(VolumeRelativeTolerance), "须在 [0, 1e-4]");
		Require(CoverageRelativeTolerance > 0 && CoverageRelativeTolerance <= 1e-4,
			nameof(CoverageRelativeTolerance), "须在 (0, 1e-4]");
		Require(GeometryAngularToleranceRad > 0 && GeometryAngularToleranceRad <= 1e-6,
			nameof(GeometryAngularToleranceRad), "须在 (0, 1e-6]");

		if (OceanicDensityKgM3 < ContinentalDensityKgM3)
			throw new ArgumentException(
				$"洋壳密度 {OceanicDensityKgM3} 不得小于陆壳密度 {ContinentalDensityKgM3}", nameof(OceanicDensityKgM3));
		if (MantleDensityKgM3 <= OceanicDensityKgM3)
			throw new ArgumentException(
				$"地幔密度 {MantleDensityKgM3} 必须大于洋壳密度 {OceanicDensityKgM3}", nameof(MantleDensityKgM3));
	}

	static void Require(bool condition, string name, string rule)
	{
		if (!condition) throw new ArgumentOutOfRangeException(name, $"{name} {rule}");
	}
}
