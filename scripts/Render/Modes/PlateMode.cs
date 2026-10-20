using Godot;
using World.Logic;                   // WorldGenSimulation（世界事实来源）

namespace World.Render;

/// <summary>板块图（自然地理 · 成因层）：逐格板块号 → **一块一色**。
/// 数据源 = ⓪ 板块阶段的 `PlateOfCell`（全球覆盖、每格恒有归属）——**几何划分本身**。
/// 本模式**只涂划分，不涂壳型**（一块板块的陆海身份见 `Plate.Crust`，洋壳也归板块）。
///
/// ★为何色相只由**板块号**决定：本模式是**划分图**，判读的问题只有"这两格是不是同一块"。
///   用"种子点经纬/方向"映射颜色看似更自然，实则会让两个相邻板块拿到相近色（种子挨得近时）
///   ——判读最需要区分的恰是相邻块。故取黄金角（137.5°）步进散开色相：任意两块色相不同，
///   且相邻号之间散得最开（黄金角的性质）。**与世界无关的固定映射** ⇒ 同参数跨世界可比。
///
/// ★与生成的关系：本模式**只读**板块归属（`CellColorAt` 里现取，不在构造期存数组引用——
///   `Run()` 会换数组实例），不反向依赖生成过程（地图坞契约 ③）。</summary>
public sealed class PlateMode : MapMode
{
	readonly WorldGenSimulation _p;
	public PlateMode(WorldGenSimulation p) => _p = p;
	public override string Name => "板块";

	/// <summary>色标说明（坞图例行）。板块图没有数值域，故声明的是"颜色怎么来"。</summary>
	public override string ScaleCaption => "一块一色 · 色相按板块号黄金角散开（非数值域）";

	public override Color CellColorAt(int i)
	{
		int p = _p.Plate.PlateOfCell[i];
		if (p < 0) return Colors.Magenta;      // 理论不可达（⓪ 阶段全球覆盖）——防"没跑就涂色"的静默
		return PlateColor(p);
	}

	// ── 色相散开（模式专属派生表内聚本文件；与 `ElevationMode.ElevationColor` 同归属规则）──
	/// <summary>黄金角共轭（frac(1/φ)）：色相步进 137.5°。</summary>
	const float GoldenAngleHue = 0.6180339887f;
	/// <summary>饱和度：偏低些（0.55）以免整屏刺眼，同时保住块与块的分界。</summary>
	const float PlateSaturation = 0.55f;
	/// <summary>明度：统一取值 ⇒ 各板块**只差色相**，没有"越亮越重要"的错误暗示。</summary>
	const float PlateValue = 0.88f;

	/// <summary>板块号 → 固定色（纯函数：同号恒同色，与种子/世界无关）。</summary>
	public static Color PlateColor(int plateId)
	{
		float hue = (plateId * GoldenAngleHue) % 1f;
		if (hue < 0f) hue += 1f;               // 负号兜底（本阶段不产负数，但纯函数不该有隐含前提）
		return Color.FromHsv(hue, PlateSaturation, PlateValue);
	}
}
