using Godot;
using System;
using System.Collections.Generic;
using World.Logic;
using static World.Utils.ColorRamp;

namespace World.Render;

/// <summary>
/// 风场（气候）：**底色 = 风速**（蓝 → 青蓝 → 绿 → 黄 → 橙红，固定 0–1 物理域），
/// **白色风丝 = 风向 / 流动**（<see cref="WindFlowOverlay"/>）。
/// 数据源 = <c>Facts.Wind</c> 的三份视图；参数行 = 年均 + 逐月，变体行 = 地面 / 低空 / 自由。
///
/// 两轴正交：月份换"取哪个月"，变体换"取哪套风"。变体只换数据源，不重算、不改生成；
/// 底色与风丝必须读同一份（<see cref="SelectedKind"/> 同时交给 <see cref="WindFlowOverlay"/>）。
///
/// @example
/// <code>
/// var mode = new WindMode(sim);
/// mode.SetParameter(6);                 // 7 月
/// mode.SetVariant(2);                   // 自由低层
/// WindKind kind = mode.SelectedKind;    // 交给 WindFlowOverlay.Build
/// </code>
/// </summary>
public sealed class WindMode : MapMode
{
	static readonly WindKind[] Kinds = { WindKind.Surface, WindKind.Low, WindKind.Free };
	static readonly string[] KindNames = { "地面", "低空", "自由" };

	readonly IWorldFactsSource _p;
	int _month;    // 0 = 年均；1..MonthCount = 月份
	int _variant;  // 0 = 地面 / 1 = 低空 / 2 = 自由

	public WindMode(IWorldFactsSource p) => _p = p;

	public override string Name => "风场";

	/// <summary>当前参数对应的月份（0 = 年均；1..MonthCount = 月份）。</summary>
	public int SelectedMonth => Math.Clamp(_month, 0, MonthCount);

	/// <summary>当前风档（交给风丝层，保证底色与风丝同源）。</summary>
	public WindKind SelectedKind => Kinds[Math.Clamp(_variant, 0, Kinds.Length - 1)];

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

	public override IReadOnlyList<string> VariantOptions => KindNames;

	public override int VariantIndex => Math.Clamp(_variant, 0, Kinds.Length - 1);

	public override void SetVariant(int index) => _variant = Math.Clamp(index, 0, Kinds.Length - 1);

	public override string ScaleCaption
	{
		get
		{
			int month = Math.Clamp(_month, 0, MonthCount);
			string label = month == 0 ? "年均" : $"{month} 月";
			return $"底色 = 风速（软饱和显示 0–1：蓝 → 绿 → 黄 → 橙红）· 白色风丝 = 风向 / 流动"
				+ $"（{label} · {KindNames[VariantIndex]}）";
		}
	}

	public override Color CellColorAt(int i)
	{
		var facts = _p.Facts;
		int month = Math.Clamp(_month, 0, MonthCount);
		var set = facts.Wind.Get(SelectedKind);
		float speed = Mathf.Clamp(set.SpeedAt(month, i), 0f, 1f);
		var c = RampSampleSmooth(WindSpeedStops, speed);
		// 海面用完整速度色带；陆地往暖灰里压一点，保住海陆轮廓（参考图用白色海岸线，这里没有海岸线层）。
		return facts.FinalLand[i] ? c.Lerp(LandTint, 0.45f) : c;
	}

	int MonthCount => _p.Facts.Wind.MonthCount;

	// 固定物理域 0–1 的风速色带：静风深蓝 → 弱风蓝青 → 中风绿 → 强风黄 → 极强橙红。
	static readonly ColorStop[] WindSpeedStops =
	{
		new(0.00f, new Color(0.03f, 0.06f, 0.16f)),
		new(0.25f, new Color(0.04f, 0.22f, 0.34f)),
		new(0.50f, new Color(0.10f, 0.48f, 0.32f)),
		new(0.75f, new Color(0.55f, 0.65f, 0.20f)),
		new(1.00f, new Color(0.82f, 0.25f, 0.13f)),
	};

	static readonly Color LandTint = new(0.38f, 0.33f, 0.25f);

	/// <summary>静风色（无方向可表达）。</summary>
	public static readonly Color CalmColor = new(0.28f, 0.28f, 0.28f);

	/// <summary>风向 → 颜色：罗盘方位映射到色环，明度随风速。</summary>
	public static Color CompassColor(Vector3 dir, Vector3 wind)
	{
		float bearing = BearingRad(dir, wind);
		float hue = bearing / (2f * MathF.PI);
		float speed = Math.Clamp(wind.Length(), 0f, 1f);
		return Color.FromHsv(hue, 0.85f, 0.35f + 0.65f * speed);
	}

	/// <summary>罗盘方位角（弧度，0 = 北、π/2 = 东；顺时针）。极点附近经向退化时返回 0。</summary>
	public static float BearingRad(Vector3 dir, Vector3 wind)
	{
		var north = NorthTangent(dir, out var east);
		float u = wind.Dot(east);
		float v = wind.Dot(north);
		float bearing = MathF.Atan2(u, v);
		return bearing < 0f ? bearing + 2f * MathF.PI : bearing;
	}

	/// <summary>该点的北向切向量（同时给出东向切向量）；极点处退化，回退到任意切向。</summary>
	internal static Vector3 NorthTangent(Vector3 dir, out Vector3 east)
	{
		var r = dir.Normalized();
		var up = new Vector3(0f, 1f, 0f);
		var north = up - r * r.Dot(up);
		if (north.LengthSquared() < 1e-9f)
		{
			north = MathF.Abs(r.X) < 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 0f, 1f);
			north -= r * r.Dot(north);
		}
		north = north.Normalized();
		east = r.Cross(north).Normalized();
		return north;
	}
}
