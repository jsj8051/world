using System;

namespace World.Data;

/// <summary>演化时钟：整数步号与固定步长；墙钟时间不在这里。</summary>
public sealed class EvolutionClock
{
	/// <summary>已提交的地质步数，从 0 开始。</summary>
	public long StepIndex { get; }
	/// <summary>固定步长（年），初始化后冻结。</summary>
	public long StepYears { get; }
	/// <summary>当前地质时间（Ma），由步号与步长派生。</summary>
	public double TimeMa => StepIndex * (double)StepYears / 1_000_000d;

	/// <summary>建立时钟；步号为负或步长非正即抛。</summary>
	/// <exception cref="ArgumentOutOfRangeException">StepIndex &lt; 0 或 StepYears ≤ 0。</exception>
	/// <example><code>var c = new EvolutionClock(0, 100000);</code></example>
	public EvolutionClock(long stepIndex, long stepYears)
	{
		if (stepIndex < 0) throw new ArgumentOutOfRangeException(nameof(stepIndex), "步号不得为负");
		if (stepYears <= 0) throw new ArgumentOutOfRangeException(nameof(stepYears), "步长必须大于 0");
		StepIndex = stepIndex;
		StepYears = stepYears;
	}

	/// <summary>把年数换算成整步数并校验 long 范围，向上取整（不足一步也算一步）。</summary>
	/// <exception cref="OverflowException">步数超出 long 表示范围。</exception>
	/// <example><code>long steps = clock.StepsFor(250000); // 3</code></example>
	public long StepsFor(long years)
	{
		if (years < 0) throw new ArgumentOutOfRangeException(nameof(years), "年数不得为负");
		long whole = years / StepYears;
		if (years % StepYears != 0) whole = checked(whole + 1);
		return whole;
	}
}
