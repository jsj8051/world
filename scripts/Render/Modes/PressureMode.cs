using Godot;
using System;
using System.Collections.Generic;
using static World.Utils.ColorRamp;
using World.Logic;

namespace World.Render;

/// <summary>
/// 气压（气候）：年均或逐月海平面气压（hPa），**发散色带**固定物理域 970…1050，
/// 中点 1013 = 标准海平面气压：蓝 = 低压 / 红 = 高压。
/// 数据源 = <c>Facts.PressureMonthlyHpa</c> / <c>Facts.PressureAnnualHpa</c>；
/// 参数行 = 年均 + 逐月（与温度/风场同一套参数轴，不是 N+1 个模式实体）。
///
/// 这张图是季风重建的"先看得见"：中心由三项叠加的局部极值涌现，不靠放置。
/// </summary>
public sealed class PressureMode : MapMode
{
	readonly IWorldFactsSource _p;
	int _month;   // 0 = 年均；1..MonthCount = 月份

	public PressureMode(IWorldFactsSource p) => _p = p;

	public override string Name => "气压";

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
			return $"固定物理域 {DomainMinHpa:F0}…{DomainMaxHpa:F0} hPa（{label}）· 蓝 = 低压 / 红 = 高压";
		}
	}

	public override Color CellColorAt(int i)
	{
		var facts = _p.Facts;
		int month = Math.Clamp(_month, 0, facts.PressureMonthlyHpa.Length);
		float p = month == 0
			? facts.PressureAnnualHpa[i]
			: facts.PressureMonthlyHpa[month - 1][i];
		return RampSample(PressureStops, p);
	}

	int MonthCount => _p.Facts.PressureMonthlyHpa.Length;

	/// <summary>固定域下界（hPa）。</summary>
	public const float DomainMinHpa = 970f;

	/// <summary>固定域上界（hPa）。</summary>
	public const float DomainMaxHpa = 1050f;

	/// <summary>气压色带：中点 1013.25 附近为近中性，两侧向深蓝 / 深红发散。</summary>
	static readonly ColorStop[] PressureStops =
	{
		new(970f,  new Color(0.05f, 0.10f, 0.38f)),
		new(990f,  new Color(0.14f, 0.36f, 0.66f)),
		new(1005f, new Color(0.52f, 0.70f, 0.84f)),
		new(1013f, new Color(0.90f, 0.89f, 0.83f)),
		new(1022f, new Color(0.94f, 0.76f, 0.48f)),
		new(1036f, new Color(0.80f, 0.34f, 0.20f)),
		new(1050f, new Color(0.52f, 0.08f, 0.10f)),
	};
}
