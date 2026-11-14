using Godot;
using System;                      // MathF（对数式显示变换）
using System.Collections.Generic;
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（取色方式，复用不复制）
using World.Logic;                   // IWorldFactsSource（世界事实）

namespace World.Render;

/// <summary>
/// 降水（气候）：年累计和逐月分别使用固定量程，共用对数色带。
/// 数据源 = <c>Facts.PrecipitationMm</c> / <c>Facts.PrecipitationMonthlyMm</c>；
/// 海洋也有降水（海面降水真值），不做陆地掩膜。
/// </summary>
public sealed class PrecipitationMode : MapMode
{
	readonly IWorldFactsSource _p;
	int _month;   // 0 = 年累计；1..MonthCount = 月份

	public PrecipitationMode(IWorldFactsSource p) => _p = p;

	public override string Name => "降水";

	public override IReadOnlyList<string> ParameterOptions
	{
		get
		{
			int months = MonthCount;
			var options = new string[months + 1];
			options[0] = "年累计";
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
			return month == 0
				? $"年累计 · 0–{AnnualDomainMm:0} mm/年（对数显示）"
				: $"{month} 月 · 0–{MonthlyDomainMm:0} mm/月（对数显示）";
		}
	}

	public override Color CellColorAt(int i)
	{
		var facts = _p.Facts;
		int month = Math.Clamp(_month, 0, facts.PrecipitationMonthlyMm.Length);
		float position = month == 0
			? PrecipNorm(facts.PrecipitationMm[i])
			: PrecipNorm(facts.PrecipitationMonthlyMm[month - 1][i] * (AnnualDomainMm / MonthlyDomainMm));
		return RampSampleSmooth(PrecipStops, position);
	}

	int MonthCount => _p.Facts.PrecipitationMonthlyMm.Length;

	// 月值按量程比例取同一色带的位置。各月共享固定量程，换月时可直接比较。
	const float AnnualDomainMm = 3000f;
	const float MonthlyDomainMm = 400f;
	const float PrecipLogK = 50f;

	/// <summary>年降水色带坐标（对数式；负值夹 0、超域夹 1）。</summary>
	public static float PrecipNorm(float mm) => Math.Clamp(
		MathF.Log(1f + MathF.Max(mm, 0f) / PrecipLogK) / MathF.Log(1f + AnnualDomainMm / PrecipLogK),
		0f, 1f);

	/// <summary>降水色带（位置 = PrecipNorm(物理 mm)，干沙 → 枯黄 → 草绿 → 深绿 → 青 → 深蓝）。</summary>
	public static readonly ColorStop[] PrecipStops =
	{
		new(PrecipNorm(0),    new Color(0.85f, 0.72f, 0.42f)),   // 干沙（极端干旱）
		new(PrecipNorm(150),  new Color(0.80f, 0.73f, 0.38f)),   // 枯黄（荒漠/草原过渡）
		new(PrecipNorm(400),  new Color(0.60f, 0.72f, 0.36f)),   // 草黄绿（草原）
		new(PrecipNorm(900),  new Color(0.30f, 0.62f, 0.36f)),   // 绿（疏林/季雨）
		new(PrecipNorm(1800), new Color(0.16f, 0.46f, 0.56f)),   // 青（雨林边缘）
		new(PrecipNorm(3000), new Color(0.10f, 0.22f, 0.58f)),   // 深蓝（极端湿润）
	};
}
