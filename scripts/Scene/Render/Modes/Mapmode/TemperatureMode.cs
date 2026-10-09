using Godot;
using World.Render;                // MapMode（表现层契约基类）
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（取色方式，复用不复制）

namespace World.WorldGen;

/// <summary>温度（气候 · 注册序 = Id = 4）：逐格年均温 °C，Sequential 固定物理域 −40…+90 °C · 线性。
/// 数据源 = `Temperature.CellTemperatureC`（逐格能量收支的年均值，陆海同口径）。
/// ★月度温度场尚不存在 ⇒ **不为 UI 提前造月度数据**（同降水模式）；有月度事实后再加参数行。
/// ★色带为本模式专用（不复用 BiomeColors.TempStops，理由见下方色带注释）。</summary>
public sealed class TemperatureMode : MapMode
{
	readonly WorldGenSimulation _p;
	public TemperatureMode(WorldGenSimulation p) => _p = p;
	public override string Name => "温度";
	public override string ScaleCaption => $"固定物理域 {TempDomainMinC:F0}…+{TempDomainMaxC:F0} °C（年均 · 不随世界拉伸）";
	public override Color CellColorAt(int i) =>
		RampSampleSmooth(TemperatureStops, _p.Temperature.CellTemperatureC[i]);

	// ── 温度色带（Sequential · 固定物理域 −40…+90 °C · 线性，无显示变换）──
	// ★为什么本模式不直接复用 Domain.BiomeColors.TempStops：那张色带的域是 **−85…+45 °C**
	//   （地球尺度，供未来 Biome 用）；而本模型是"理想黑体无温室"，赤道 ≈ +87 °C、全球均
	//   +76 °C ⇒ 用 −85…45 会把纬度 < 56° 全部夹到红色，判读不出梯度。故**地图专用色带**独立
	//   定义在此（与 PrecipStops 同构：地图色带归地图模式），BiomeColors 保持不动。
	//   域端固定 ⇒ 跨世界可比（禁 min-max 自动拉伸），超域由 RampSampleSmooth 夹端色。

	/// <summary>固定物理域下界（°C）。★取 −40 而非模型理论下界 −273：`cos^0.25` 极平坦 ⇒
	/// 陆温集中 +0…+87（|纬度|&lt;70°），若域下界取 −90，色带有一半浪费在几乎无格的极端区间
	/// ⇒ 整图挤在暖端（实测）。更冷者夹本域首色（极区小面积，夹色可接受、如实）。</summary>
	public const float TempDomainMinC = -40f;
	/// <summary>固定物理域上界（°C；模型赤道 ≈ +87）。</summary>
	public const float TempDomainMaxC = 90f;

	/// <summary>温度色带（位置 = 物理 °C，严寒深蓝 → 冷蓝 → 温和绿 → 暖黄绿 → 热橙 → 酷热深红）。
	/// ★停点按模型实际分布布点：`T_eq = 360 K · cos(φ)^0.25` 极平坦 ⇒ 纬度 60° 仍有 +30 °C，
	///   陆温集中 **+0…+87**（|纬度|&lt;70°）⇒ 该区间放 5 个停点，否则整图只落色带上段（实测全红）。</summary>
	public static readonly ColorStop[] TemperatureStops =
	{
		new(-40f, new Color(0.08f, 0.12f, 0.42f)),   // 严寒（深蓝；更低夹此色）
		new(-10f, new Color(0.16f, 0.38f, 0.68f)),   // 冷（蓝）
		new(20f,  new Color(0.34f, 0.70f, 0.44f)),   // 温和（绿）
		new(45f,  new Color(0.76f, 0.76f, 0.28f)),   // 暖（黄绿）
		new(70f,  new Color(0.92f, 0.56f, 0.18f)),   // 热（橙）
		new(90f,  new Color(0.78f, 0.16f, 0.12f)),   // 酷热（深红）
	};
}
