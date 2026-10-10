using Godot;
using System;                      // MathF（对数式显示变换）
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（取色方式，复用不复制）
using World.Logic;                   // WorldGenSimulation / RiverGeometry / RiverGraph（世界事实与水文事实）

namespace World.Render;

/// <summary>降水（气候 · 注册序 = Id = 3）：年均 mm/年，Sequential 固定物理域 0–3000 mm + 对数式显示变换。
/// ★月度降水场尚无可靠生产数据——**不为 UI 提前造月度数据**，有数据后再扩参数行。</summary>
public sealed class PrecipitationMode : MapMode
{
	readonly WorldGenSimulation _p;
	public PrecipitationMode(WorldGenSimulation p) => _p = p;
	public override string Name => "降水";
	public override string ScaleCaption => "固定物理域 0–3000 mm/年（对数显示）";
	public override Color CellColorAt(int i) =>
		RampSampleSmooth(PrecipStops, PrecipNorm(_p.Climate.Precipitation.AnnualMm[i]));

	// ── 降水色带（Sequential · 固定物理域 0–3000 mm/年 · 对数式显示变换）──
	// 长尾分布：线性域会让湿润区挤在色带一端；对数变换只作用于**显示采样**（mm → [0,1]），
	// 停点位置仍按物理量落位，跨世界可比。数据与生产公式零改动。
	// ★色带内聚本文件（= 消费它的模式）；物理固定域，**禁止 min-max 自动拉伸**。

	const float PrecipDomainMm = 3000f;   // 固定物理域上界（全球实测均 1,106 mm/年；>3000 夹末色）
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
