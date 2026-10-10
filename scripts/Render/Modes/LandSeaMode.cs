using Godot;
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（取色方式，复用不复制）
using World.Logic;                   // WorldGenSimulation / RiverGeometry / RiverGraph（世界事实与水文事实）

namespace World.Render;

/// <summary>【生成诊断】海陆结构场原值（连续 raw）：发散色带（负 = 蓝 / 0 ≈ 海岸线 = 纸白 / 正 = 棕红）。
/// 回答的是"生成器的原始连续场长什么样"（Raw Projector → Feature → FinalHeight → FinalLandMask
/// 链路取证用），**不是**最终海陆事实——那由 FinalHeight > 0 / FinalLandMask 表达（海拔/区域模式已覆盖）。
/// 故注册序垫底（诊断垫底约定靠 `WorldGenMapModes.CreateAll` 表序维持），不与世界语义混排。</summary>
public sealed class LandSeaMode : MapMode
{
	readonly WorldGenSimulation _p;
	public LandSeaMode(WorldGenSimulation p) => _p = p;
	public override string Name => "海陆场";
	public override string ScaleCaption => "生成诊断 · Projector.Raw（非世界事实）";
	public override Color CellColorAt(int i) => RampSampleSmooth(DivergingStops, _p.LandSea.Projector.Raw[i] / 0.8f);

	// ── 发散色带（原始连续场共用 · 海陆场诊断模式用）──
	// ★沿革：原挂在抽象基类 `MapMode` 上（B 线 `NoiseMapMode` "基类 + 两色带常量"直接搬入的
	//   遗留）；2026-10-08 归位——**基类只装契约，色带归消费它的模式**（与 PrecipStops /
	//   TemperatureStops 同构：色带内聚本文件）。取色方式统一由 `World.Utils.ColorRamp` 提供。

	/// <summary>发散色带（负 = 蓝 / 0 = 纸白 / 正 = 棕红；域 −1…+1）。
	/// 消费者 = <see cref="LandSeaMode"/>：把 `Projector.Raw` 除以 0.8 归一到色带域后取色。</summary>
	public static readonly ColorStop[] DivergingStops =
	{
		new(-1f, new Color(0.14f, 0.28f, 0.52f)),   // 满 位（负侧）
		new(0f,  new Color(0.93f, 0.91f, 0.86f)),   // 中性（纸白）
		new(1f,  new Color(0.62f, 0.30f, 0.18f)),   // 满 位（正侧）
	};
}
