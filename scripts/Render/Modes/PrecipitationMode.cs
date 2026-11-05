using Godot;
using System;                      // MathF（对数式显示变换）
using System.Collections.Generic;
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（取色方式，复用不复制）
using World.Logic;                   // WorldGenSimulation（世界事实）

namespace World.Render;

/// <summary>
/// 降水（气候）：年均或逐月 mm，Sequential 固定物理域 0–3000 mm/年 + 对数式显示变换。
/// 数据源 = <c>Facts.PrecipitationMm</c> / <c>Facts.PrecipitationMonthlyMm</c>；
/// 参数行 = 年均 + 逐月（同上限 3000 mm/月，月值本就在同一量纲内）。
/// 海洋也有降水（海面降水真值），不做陆地掩膜。
/// </summary>
public sealed class PrecipitationMode : MapMode
{
	readonly WorldGenSimulation _p;
	int _month;   // 0 = 年均；1..MonthCount = 月份

	public PrecipitationMode(WorldGenSimulation p) => _p = p;

	public override string Name => "降水";

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
			return $"固定物理域 0–3000 mm（对数显示 · {label}）";
		}
	}

	public override Color CellColorAt(int i)
	{
		var facts = _p.Facts;
		int month = Math.Clamp(_month, 0, facts.PrecipitationMonthlyMm.Length);
		float mm = month == 0 ? facts.PrecipitationMm[i] : facts.PrecipitationMonthlyMm[month - 1][i];
		return RampSampleSmooth(PrecipStops, PrecipNorm(mm));
	}

	int MonthCount => _p.Facts.PrecipitationMonthlyMm.Length;

	// ── 降水色带（Sequential · 固定物理域 0–3000 mm/年 · 对数式显示变换）──
	// 长尾分布：线性域会让湿润区挤在色带一端；对数变换只作用于**显示采样**（mm → [0,1]），
	// 停点位置仍按物理量落位，跨世界可比。数据与生产公式零改动。
	// ★色带内聚本文件（= 消费它的模式）；物理固定域，**禁止 min-max 自动拉伸**。

	const float PrecipDomainMm = 3000f;   // 固定物理域上界（全球实测均 ~1,100 mm/年；>3000 夹末色）
	const float PrecipLogK = 50f;         // 对数软化常数（t = ln(1+p/K) / ln(1+P/K)）

	/// <summary>降水 mm → [0,1] 显示归一（对数式；负值夹 0、超域夹 1）。仅显示变换，不回写数据。</summary>
	public static float PrecipNorm(float mm) => System.Math.Clamp(
		MathF.Log(1f + MathF.Max(mm, 0f) / PrecipLogK) / MathF.Log(1f + PrecipDomainMm / PrecipLogK),
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
