using Godot;
using System;
using World.Logic;
using World.Utils;

namespace World.Render;

/// <summary>
/// 地形模式（自然地理 · 注册序首位）：350 m 等高带高度图——把最终陆地海拔量化成等高带。
/// 不做山体阴影、雪线、海岸高亮或任何后处理；高山用棕 / 暗棕，不用白色。
/// 色带是表现层的 16 个停点（深海 → 浅海 → 沙滩 → 低地 → 平原 → 高地 → 山地 → 高山）：
/// 陆地每 350 m 一条带，按高度单调加深 ⇒ 一条脊只有一条最深色带。
/// 判读档（<c>ElevationBands</c>）仍归世界事实层，不参与这里的取色。
/// </summary>
public sealed class TerrainMode : MapMode
{
	/// <summary>等高带间隔（米）：高度图用 350 m 的色带把山脊 / 分支显出来（不做连续渐变）。</summary>
	const float BandM = 350f;

	readonly WorldGenSimulation _p;

	public TerrainMode(WorldGenSimulation p) => _p = p;

	public override string Name => "地形";

	public override string ScaleCaption => "350 m 等高带 · 高度图（无雪线）";

	public override Color CellColorAt(int i)
	{
		float h = _p.Terrain.HeightM[i];

		// 陆地：量化到 350 m 等高带；色带按高度单调加深，山脊 = 最深的一条带。
		// 不要交替明暗：那会在一条脊的两侧各造出一条"假脊线"。
		// 海陆判据读世界事实、不写死 0 m——海面标高变化时海岸线与事实保持一致。
		if (_p.Facts.Final.FinalLand[i])
		{
			float center = MathF.Floor(h / BandM) * BandM + BandM * 0.5f;
			return ColorRamp.RampSampleSmooth(Stops, center);
		}

		return ColorRamp.RampSampleSmooth(Stops, h);
	}

	/// <summary>连续海拔色带（表现层；与判读档解耦）。从深渊到最高峰共 16 个停点，位置随垂直倍率 ×7。</summary>
	static readonly ColorRamp.ColorStop[] Stops =
	{
		new(-3920f, new Color(0.02f, 0.05f, 0.18f)),   // 深渊
		new(-2100f, new Color(0.03f, 0.10f, 0.30f)),   // 深海
		new(-1120f, new Color(0.05f, 0.17f, 0.42f)),   // 深海
		new(-371f,  new Color(0.08f, 0.28f, 0.58f)),   // 大洋
		new(-126f,  new Color(0.13f, 0.42f, 0.68f)),   // 浅海
		new(-42f,   new Color(0.20f, 0.48f, 0.62f)),   // 岸棚
		new(-7f,    new Color(0.30f, 0.55f, 0.55f)),   // 岸线浅水
		new(0f,     new Color(0.62f, 0.56f, 0.38f)),   // 沙滩
		new(28f,    new Color(0.50f, 0.66f, 0.34f)),   // 低地
		new(70f,    new Color(0.40f, 0.60f, 0.28f)),   // 平原
		new(126f,   new Color(0.32f, 0.52f, 0.24f)),   // 平原
		new(371f,   new Color(0.58f, 0.58f, 0.26f)),   // 高地：黄绿
		new(700f,   new Color(0.60f, 0.46f, 0.22f)),   // 山地：橙棕
		new(1260f,  new Color(0.54f, 0.38f, 0.18f)),   // 高山：棕
		new(2100f,  new Color(0.46f, 0.30f, 0.14f)),   // 高山：深棕
		new(3360f,  new Color(0.36f, 0.22f, 0.10f)),   // 最高峰：最深棕（单调加深，不用白）
	};
}
