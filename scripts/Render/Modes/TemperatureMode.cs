using Godot;
using System;
using System.Collections.Generic;
using static World.Utils.ColorRamp;
using World.Logic;

namespace World.Render;

/// <summary>
/// 温度（气候）：年均或逐月温度 °C，Sequential 固定物理域 −40…+40 · 线性。
/// 数据源 = <c>Facts.TemperatureC</c> / <c>Facts.TemperatureMonthlyC</c>；
/// 参数行 = 年均 + 逐月（月数随 <c>ClimateSpec.Months</c>，同一个模式，不是 N+1 个模式实体）。
/// 固定物理域 ⇒ 跨世界可比，不做 min-max 自动拉伸。
/// </summary>
public sealed class TemperatureMode : MapMode
{
	readonly WorldGenSimulation _p;
	int _month;   // 0 = 年均；1..MonthCount = 月份

	public TemperatureMode(WorldGenSimulation p) => _p = p;

	public override string Name => "温度";

	public override IReadOnlyList<string> ParameterOptions
	{
		get
		{
			int months = MonthCount;
			var options = new string[months + 1];
			options[0] = "年均";
			for (int m = 0; m < months; m++) options[m + 1] = $"{m + 1}月";
			return options;
		}
	}

	public override int ParameterIndex => Math.Clamp(_month, 0, MonthCount);

	public override void SetParameter(int index) => _month = Math.Clamp(index, 0, MonthCount);

	public override string ScaleCaption
	{
		get
		{
			int month = Math.Clamp(_month, 0, MonthCount);
			string label = month == 0 ? "年均" : $"{month} 月";
			return $"固定物理域 {TempDomainMinC:F0}…+{TempDomainMaxC:F0} °C（{label}）";
		}
	}

	public override Color CellColorAt(int i)
	{
		var facts = _p.Facts;
		int month = Math.Clamp(_month, 0, facts.TemperatureMonthlyC.Length);
		float t = month == 0
			? facts.TemperatureC[i]
			: facts.TemperatureMonthlyC[month - 1][i];
		return RampSampleSmooth(TemperatureStops, t);
	}

	int MonthCount => _p.Facts.TemperatureMonthlyC.Length;

	/// <summary>固定域下界（°C）。</summary>
	public const float TempDomainMinC = -40f;
	/// <summary>固定域上界（°C）。</summary>
	public const float TempDomainMaxC = 40f;

	/// <summary>温度色带（位置 = 物理 °C；严寒深蓝 → 冷蓝 → 凉青 → 温和绿 → 暖黄 → 热橙 → 酷热红）。</summary>
	static readonly ColorStop[] TemperatureStops =
	{
		new(-40f, new Color(0.08f, 0.12f, 0.42f)),
		new(-20f, new Color(0.16f, 0.38f, 0.68f)),
		new(0f,   new Color(0.34f, 0.70f, 0.70f)),
		new(10f,  new Color(0.34f, 0.70f, 0.44f)),
		new(20f,  new Color(0.76f, 0.76f, 0.28f)),
		new(30f,  new Color(0.92f, 0.56f, 0.18f)),
		new(40f,  new Color(0.78f, 0.16f, 0.12f)),
	};
}
