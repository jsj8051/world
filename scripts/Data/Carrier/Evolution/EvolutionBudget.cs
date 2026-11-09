using System;

namespace World.Data;

/// <summary>
/// 一步演化与自零步以来的物质收支（单位 km³）。所有流量字段非负，残差为派生值；
/// 地壳与地幔库存分开校验，不允许两边误差相抵。
/// </summary>
public sealed class EvolutionBudget
{
	/// <summary>零步地壳体积。</summary>
	public double InitialCrustVolume { get; }
	/// <summary>零步地幔库存。</summary>
	public double InitialMantleVolume { get; }
	/// <summary>本步前地壳体积。</summary>
	public double PreviousCrustVolume { get; }
	/// <summary>本步后地壳体积。</summary>
	public double CurrentCrustVolume { get; }
	/// <summary>本步前地幔库存。</summary>
	public double PreviousMantleVolume { get; }
	/// <summary>本步后地幔库存；必须等于当前 <see cref="MantleReservoir"/>。</summary>
	public double CurrentMantleVolume { get; }
	/// <summary>本步俯冲入池量。</summary>
	public double StepSubducted { get; }
	/// <summary>本步其他构造回收量。</summary>
	public double StepReturned { get; }
	/// <summary>本步从池释放的新洋壳量。</summary>
	public double StepNewOceanic { get; }
	/// <summary>本步从池释放的火山增生量。</summary>
	public double StepVolcanic { get; }
	/// <summary>累计俯冲入池量。</summary>
	public double TotalSubducted { get; }
	/// <summary>累计其他回收量。</summary>
	public double TotalReturned { get; }
	/// <summary>累计新洋壳量。</summary>
	public double TotalNewOceanic { get; }
	/// <summary>累计火山增生量。</summary>
	public double TotalVolcanic { get; }

	/// <summary>本步地壳残差（有符号）。</summary>
	public double CrustResidual => CurrentCrustVolume - PreviousCrustVolume
		- StepNewOceanic - StepVolcanic + StepSubducted + StepReturned;

	/// <summary>本步地幔库存残差（有符号）。</summary>
	public double MantleResidual => CurrentMantleVolume - PreviousMantleVolume
		- StepSubducted - StepReturned + StepNewOceanic + StepVolcanic;

	/// <summary>本步地壳与库存合计残差（有符号）。</summary>
	public double TotalResidual => (CurrentCrustVolume - PreviousCrustVolume)
		+ (CurrentMantleVolume - PreviousMantleVolume);

	/// <summary>自零步以来地壳与库存合计相对初始总量的残差（有符号）。</summary>
	public double CumulativeResidual => (CurrentCrustVolume - InitialCrustVolume)
		+ (CurrentMantleVolume - InitialMantleVolume);

	/// <summary>建立账本快照；负流量或非有限值即抛。</summary>
	/// <exception cref="ArgumentOutOfRangeException">任一字段为负或非有限。</exception>
	/// <example><code>var b = EvolutionBudget.CreateInitial(1e9, 1e10);</code></example>
	public EvolutionBudget(double initialCrustVolume, double initialMantleVolume,
		double previousCrustVolume, double currentCrustVolume,
		double previousMantleVolume, double currentMantleVolume,
		double stepSubducted, double stepReturned, double stepNewOceanic, double stepVolcanic,
		double totalSubducted, double totalReturned, double totalNewOceanic, double totalVolcanic)
	{
		RequireNonNegative(initialCrustVolume, nameof(initialCrustVolume));
		RequireNonNegative(initialMantleVolume, nameof(initialMantleVolume));
		RequireNonNegative(previousCrustVolume, nameof(previousCrustVolume));
		RequireNonNegative(currentCrustVolume, nameof(currentCrustVolume));
		RequireNonNegative(previousMantleVolume, nameof(previousMantleVolume));
		RequireNonNegative(currentMantleVolume, nameof(currentMantleVolume));
		RequireNonNegative(stepSubducted, nameof(stepSubducted));
		RequireNonNegative(stepReturned, nameof(stepReturned));
		RequireNonNegative(stepNewOceanic, nameof(stepNewOceanic));
		RequireNonNegative(stepVolcanic, nameof(stepVolcanic));
		RequireNonNegative(totalSubducted, nameof(totalSubducted));
		RequireNonNegative(totalReturned, nameof(totalReturned));
		RequireNonNegative(totalNewOceanic, nameof(totalNewOceanic));
		RequireNonNegative(totalVolcanic, nameof(totalVolcanic));
		if (totalSubducted < stepSubducted || totalReturned < stepReturned
			|| totalNewOceanic < stepNewOceanic || totalVolcanic < stepVolcanic)
			throw new ArgumentException("累计流量不得小于本步流量");

		InitialCrustVolume = initialCrustVolume;
		InitialMantleVolume = initialMantleVolume;
		PreviousCrustVolume = previousCrustVolume;
		CurrentCrustVolume = currentCrustVolume;
		PreviousMantleVolume = previousMantleVolume;
		CurrentMantleVolume = currentMantleVolume;
		StepSubducted = stepSubducted;
		StepReturned = stepReturned;
		StepNewOceanic = stepNewOceanic;
		StepVolcanic = stepVolcanic;
		TotalSubducted = totalSubducted;
		TotalReturned = totalReturned;
		TotalNewOceanic = totalNewOceanic;
		TotalVolcanic = totalVolcanic;
	}

	/// <summary>零步账本：前后体积与库存相等，全部流量为 0。</summary>
	/// <example><code>var b = EvolutionBudget.CreateInitial(1e9, 1e10);</code></example>
	public static EvolutionBudget CreateInitial(double crustVolume, double mantleVolume)
		=> new(crustVolume, mantleVolume, crustVolume, crustVolume, mantleVolume, mantleVolume,
			0, 0, 0, 0, 0, 0, 0, 0);

	/// <summary>校验本步、累计与库存恒等式；容差 = 绝对值 + 相对值 × 相关体积基准。</summary>
	/// <exception cref="ArgumentException">任一残差超出容差。</exception>
	/// <example><code>budget.Validate(0.001, 1e-8);</code></example>
	public void Validate(double absoluteToleranceKm3, double relativeTolerance)
	{
		if (!(absoluteToleranceKm3 >= 0) || !double.IsFinite(absoluteToleranceKm3))
			throw new ArgumentOutOfRangeException(nameof(absoluteToleranceKm3));
		if (!(relativeTolerance >= 0) || relativeTolerance > 1e-4 || !double.IsFinite(relativeTolerance))
			throw new ArgumentOutOfRangeException(nameof(relativeTolerance));

		RequireWithin(CrustResidual, absoluteToleranceKm3, relativeTolerance, PreviousCrustVolume, "地壳本步");
		RequireWithin(MantleResidual, absoluteToleranceKm3, relativeTolerance, PreviousMantleVolume, "地幔本步");
		RequireWithin(TotalResidual, absoluteToleranceKm3, relativeTolerance,
			PreviousCrustVolume + PreviousMantleVolume, "总量本步");
		RequireWithin(CumulativeResidual, absoluteToleranceKm3, relativeTolerance,
			InitialCrustVolume + InitialMantleVolume, "累计总量");

		double expectedMantle = InitialMantleVolume + TotalSubducted + TotalReturned
			- TotalNewOceanic - TotalVolcanic;
		double tolerance = absoluteToleranceKm3 + relativeTolerance
			* Math.Max(InitialMantleVolume, Math.Abs(expectedMantle));
		if (Math.Abs(CurrentMantleVolume - expectedMantle) > tolerance)
			throw new ArgumentException(
				$"地幔库存恒等式失配：当前 {CurrentMantleVolume:G17}，应由初始与累计流量得到 {expectedMantle:G17}");
	}

	static void RequireWithin(double residual, double absolute, double relative, double reference, string label)
	{
		double tolerance = absolute + relative * Math.Abs(reference);
		if (Math.Abs(residual) > tolerance)
			throw new ArgumentException($"{label}残差 {residual:G17} 超出容差 {tolerance:G17}");
	}

	static void RequireNonNegative(double value, string name)
	{
		if (!double.IsFinite(value) || value < 0)
			throw new ArgumentOutOfRangeException(name, $"{name} 必须是非负有限数");
	}
}
