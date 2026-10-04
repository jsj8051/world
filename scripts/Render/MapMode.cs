using Godot;
using System.Collections.Generic;
using static World.Utils.ColorRamp;        // ColorStop（通用连续色带算法，复用不复制）

namespace World.Render;

// 地图模式策略抽象（决策 08 §4.4 表现层保留资产 · 从 `World.NoiseWorld.NoiseMapMode` 迁入）。
//
// ★迁移原因：这一层是**纯表现抽象**，生成语义为零——它只回答"格 i 该画什么颜色"，
//   不认识任何生成类型。正因为如此它才被批准从 B 线旧单体回收，而不是随 B 线清退。
//   类名去掉 `Noise` 前缀：新架构已无"噪声线"概念（`NoiseTerrain`/`NoisePlates` 等已清退）。
//
// 取色纪律沿旧 new_HexWorld MapMode：ColorRamp.RampSampleSmooth 复用不复制，断点归右侧段。
//模式类**只读**，不写任何状态；`BeginBake` 是唯一的重烘钩子（自适应域模式在此刷新 min-max）。
public abstract class MapMode
{
	public abstract int Id { get; }            // 模式号（坞按钮下标 = 注册序）
	public abstract string Name { get; }       // 按钮文案单一事实源
	public abstract Color CellColorAt(int i);  // sRGB 意图色（视图烘纹理时统一转 linear）

	/// <summary>每次重烘颜色纹理前调用一次（自适应域模式在此刷新 min-max；O(n) 扫一遍可忽略）。</summary>
	public virtual void BeginBake() { }

	/// <summary>发散色带（原始场共用：负 = 蓝、0 = 纸白、正 = 棕红）。</summary>
	public static readonly ColorStop[] DivergingStops =
	{
		new(-1f, new Color(0.14f, 0.28f, 0.52f)),   // 满 位（负侧）
		new(0f,  new Color(0.93f, 0.91f, 0.86f)),   // 中性（纸白）
		new(1f,  new Color(0.62f, 0.30f, 0.18f)),   // 满 位（正侧）
	};

	/// <summary>分位色带（topo 惯例：低分位深蓝 → 高分位红）。</summary>
	public static readonly ColorStop[] QuantileStops =
	{
		new(0f,    new Color(0.16f, 0.30f, 0.55f)),  // 深蓝（全球最低）
		new(0.35f, new Color(0.25f, 0.60f, 0.62f)),  // 青
		new(0.5f,  new Color(0.45f, 0.70f, 0.35f)),  // 绿（≈ 海陆分位线附近）
		new(0.7f,  new Color(0.88f, 0.78f, 0.30f)),  // 黄
		new(1f,    new Color(0.78f, 0.30f, 0.16f)),  // 红（全球最高）
	};
}

/// <summary>海拔分档（默认模式）：9 档硬色阶，档界即等高线。
/// <para>色带与档位名的**单一事实源**跟着模式走；<see cref="BallView.BandName"/> 委托本类，
/// 格信息面板与画面口径一致。</para>
/// <para>原名 `NoiseMapModes.ElevationBandMode`；构造器曾有一个吃 `NoiseTerrain` 的重载，
/// 已随 B 线清退删除——现在只接受逐格海拔数组（worldgen 线供给）。</para></summary>
public sealed class ElevationBandMode : MapMode
{
	readonly float[] _elevation;

	/// <summary>海拔源抽象为逐格数组：世界生成空间线与任何其他线复用同一色带与视图。</summary>
	public ElevationBandMode(float[] elevationM) => _elevation = elevationM;

	public override int Id => 0;
	public override string Name => "海拔";
	public override Color CellColorAt(int i) => ElevationColor(_elevation[i]);

	public static Color ElevationColor(float m) => m switch
	{
		< -3000f => new Color(0.05f, 0.16f, 0.42f),   // 深海
		< -1000f => new Color(0.08f, 0.25f, 0.55f),   // 海洋
		< -200f  => new Color(0.15f, 0.40f, 0.68f),   // 浅海
		< 0f     => new Color(0.55f, 0.75f, 0.80f),   // 岸棚
		< 300f   => new Color(0.45f, 0.68f, 0.32f),   // 低地
		< 900f   => new Color(0.30f, 0.58f, 0.24f),   // 平原
		< 1800f  => new Color(0.38f, 0.50f, 0.26f),   // 高地
		< 2800f  => new Color(0.52f, 0.45f, 0.34f),   // 山地
		_        => new Color(0.94f, 0.94f, 0.96f),   // 雪线
	};

	/// <summary>海拔 → 档位名（与 <see cref="ElevationColor"/> 同一阈值表，判读口径一致）。</summary>
	public static string BandName(float m) => m switch
	{
		< -3000f => "深海",
		< -1000f => "大洋",
		< -200f => "浅海",
		< 0f => "岸棚",
		< 300f => "低地",
		< 900f => "平原",
		< 1800f => "高地",
		< 2800f => "山地",
		_ => "雪线",
	};
}
