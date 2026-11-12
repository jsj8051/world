using Godot;
using World.Logic;
using World.Utils;

namespace World.Render;

/// <summary>
/// 地形模式。最终格心海拔通过固定的连续色带映射为一格一色。
/// </summary>
public sealed class TerrainMode : MapMode
{
	readonly IWorldFactsSource _p;

	public TerrainMode(IWorldFactsSource p) => _p = p;

	public override string Name => "地形";

	public override string ScaleCaption => "最终格心海拔 · 连续色带 −11,200 到 +8,500 m";

	public override Color CellColorAt(int i) => ColorAtElevation(_p.Facts.HeightM[i]);

	internal static Color ColorAtElevation(float heightM) => ColorRamp.RampSampleSmooth(Stops, heightM);

	/// <summary>连续海拔色带（表现层；与判读档解耦）。从深渊到最高峰共 16 个停点，位置覆盖 −11,200…8,500 m。</summary>
	static readonly ColorRamp.ColorStop[] Stops =
	{
		new(-11200f, new Color(0.02f, 0.05f, 0.18f)),   // 深渊
		new(-6000f,  new Color(0.03f, 0.10f, 0.30f)),   // 深海
		new(-3200f,  new Color(0.05f, 0.17f, 0.42f)),   // 深海
		new(-1060f,  new Color(0.08f, 0.28f, 0.58f)),   // 大洋
		new(-360f,   new Color(0.13f, 0.42f, 0.68f)),   // 浅海
		new(-120f,   new Color(0.20f, 0.48f, 0.62f)),   // 岸棚
		new(-20f,    new Color(0.30f, 0.55f, 0.55f)),   // 岸线浅水
		new(0f,      new Color(0.62f, 0.56f, 0.38f)),   // 沙滩
		new(80f,     new Color(0.50f, 0.66f, 0.34f)),   // 低地
		new(200f,    new Color(0.40f, 0.60f, 0.28f)),   // 平原
		new(360f,    new Color(0.32f, 0.52f, 0.24f)),   // 平原
		new(1060f,   new Color(0.58f, 0.58f, 0.26f)),   // 高地：黄绿
		new(2000f,   new Color(0.60f, 0.46f, 0.22f)),   // 山地：橙棕
		new(3600f,   new Color(0.54f, 0.38f, 0.18f)),   // 高山：棕
		new(6000f,   new Color(0.46f, 0.30f, 0.14f)),   // 高山：深棕
		new(8500f,   new Color(0.36f, 0.22f, 0.10f)),   // 最高峰：最深棕（单调加深，不用白）
	};
}
