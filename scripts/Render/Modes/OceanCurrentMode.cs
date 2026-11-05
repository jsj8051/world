using Godot;
using System;
using System.Collections.Generic;
using static World.Utils.ColorRamp;
using World.Logic;

namespace World.Render;

/// <summary>
/// 洋流（气候）：海格的冷暖 / 强度 / 方向；陆地灰显。
///
/// 数据源 = <c>Facts.OceanCurrentWarmth</c> / <c>OceanCurrentStrength</c> / <c>OceanCurrentDirection</c>。
/// 参数行 = 冷暖（发散色带）/ 强度（顺序色带）/ 方向（罗盘色环）——同一份事实的三种判读口径，
/// 不是三套数据。固定物理域，禁止 min-max 自动拉伸。
///
/// @example
/// <code>
/// var mode = new OceanCurrentMode(sim);
/// mode.SetParameter(0);                 // 冷暖：蓝 = 寒流、红 = 暖流
/// Color c = mode.CellColorAt(i);
/// </code>
/// </summary>
public sealed class OceanCurrentMode : MapMode
{
	readonly WorldGenSimulation _p;
	int _view = 3;   // 0 = 冷暖；1 = 强度；2 = 方向；3 = 粒子（默认）

	public OceanCurrentMode(WorldGenSimulation p) => _p = p;

	public override string Name => "洋流";

	static readonly string[] Views = { "冷暖", "强度", "方向", "粒子" };

	/// <summary>"粒子"档：底图压暗，由 <see cref="CurrentFlowOverlay"/> 画出沿预积分流线运动的彗星。</summary>
	public bool ShowParticles => _view == 3;

	public override IReadOnlyList<string> ParameterOptions => Views;

	public override int ParameterIndex => Math.Clamp(_view, 0, Views.Length - 1);

	public override void SetParameter(int index) => _view = Math.Clamp(index, 0, Views.Length - 1);

	public override string ScaleCaption => _view switch
	{
		0 => "冷暖 = 洋流经向分量（蓝寒流 −1 … 红暖流 +1）· 陆地灰显",
		1 => "强度 0（弱环流 / 静水）… 1（西边界强化强流）· 陆地灰显",
		2 => "色相 = 流向（北红 · 东黄绿 · 南青 · 西紫）· 明度 = 强度 · 陆地灰显",
		_ => "粒子沿预积分流线运动：暖流偏红 / 寒流偏蓝，越亮越快（GPU 动画，底图压暗）",
	};

	public override Color CellColorAt(int i)
	{
		var facts = _p.Facts;
		if (facts.FinalLand[i]) return _view == 3 ? ParticleLandColor : LandColor;

		// 粒子档：**单一深海蓝**作底（不能按强度分档——那会在海面上画出纬度同心带），
		// 亮度留给加法混合的粒子自己去叠。
		if (_view == 3) return ParticleSeaColor;

		return _view switch
		{
			0 => RampSampleSmooth(WarmthStops, (facts.OceanCurrentWarmth[i] + 1f) * 0.5f),
			1 => RampSampleSmooth(StrengthStops, Math.Clamp(facts.OceanCurrentStrength[i], 0f, 1f)),
			_ => facts.OceanCurrentDirection[i].LengthSquared() < 1e-6f
				? WindMode.CalmColor
				: WindMode.CompassColor(facts.Ball.CellDirs[i], facts.OceanCurrentDirection[i]),
		};
	}

	/// <summary>陆地灰显（洋流是海格事实，陆地不判读）。</summary>
	public static readonly Color LandColor = new(0.30f, 0.29f, 0.27f);

	// 粒子档底图：海 = 单一深蓝、陆 = 淡沙。
	// 海面必须**一色**：按强度分两档会在球面上画出同心纬度带（2026-10-23 实机截图暴露）。
	static readonly Color ParticleSeaColor = new(0.05f, 0.10f, 0.24f);
	static readonly Color ParticleLandColor = new(0.55f, 0.52f, 0.44f);

	/// <summary>冷暖发散色带（位置 = (warmth+1)/2）。</summary>
	public static readonly ColorStop[] WarmthStops =
	{
		new(0.0f, new Color(0.16f, 0.38f, 0.78f)),   // 寒流（向赤道）
		new(0.5f, new Color(0.80f, 0.80f, 0.78f)),   // 中性
		new(1.0f, new Color(0.80f, 0.26f, 0.18f)),   // 暖流（向极）
	};

	/// <summary>强度顺序色带（位置 = strength 0–1）。</summary>
	public static readonly ColorStop[] StrengthStops =
	{
		new(0.0f, new Color(0.16f, 0.24f, 0.32f)),   // 弱环流
		new(0.5f, new Color(0.20f, 0.55f, 0.62f)),   // 中等
		new(1.0f, new Color(0.72f, 0.92f, 0.86f)),   // 西边界强化强流
	};
}
