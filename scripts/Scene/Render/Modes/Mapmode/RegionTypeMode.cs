using Godot;
using World.Render;                // MapMode（表现层契约基类）
using World.Render.Constants;      // MapSeaColor（跨模式共享海格底色）

namespace World.WorldGen;

/// <summary>地质区域类型（自然地理 · 注册序 = Id = 1）。七类固定色；主判读图——用户拍板的地图语义。
/// 只做"地理结构分类"，不混入海拔/温度/植被/土壤语义。
/// 收口（§07 D-1）：地图语义一律读 **Final 口径**（最终区域归属；Placement 版仅供生成内部）。</summary>
public sealed class RegionTypeMode : MapMode
{
	readonly WorldGenSimulation _p;
	public RegionTypeMode(WorldGenSimulation p) => _p = p;
	public override string Name => "地质区域";
	public override Color CellColorAt(int i)
	{
		// 收口（§07 D-1）：地图语义一律读 **Final 口径**（最终区域归属；Placement 版仅供生成内部）
		int r = _p.Final.FinalRegionOfCell[i];
		if (r < 0) return MapSeaColor.Deep;   // 海格统一深蓝底（共享底色，画面连续）
		return _p.Regions.Regions[r].Type switch
		{
			RegionType.Plain => new Color(0.45f, 0.68f, 0.32f),   // 绿（平原）
			RegionType.Highland => new Color(0.78f, 0.66f, 0.36f),   // 黄褐（高地）
			RegionType.Basin => new Color(0.45f, 0.52f, 0.60f),   // 蓝灰（盆地）
			RegionType.Mountain => new Color(0.52f, 0.40f, 0.28f),   // 深棕（山地）
			RegionType.Plateau => new Color(0.85f, 0.55f, 0.30f),   // 橙（高原）
			RegionType.Rift => new Color(0.68f, 0.32f, 0.52f),   // 紫红（裂谷）
			RegionType.Coastal => new Color(0.55f, 0.78f, 0.55f),   // 青绿（沿岸）
			_ => Colors.Magenta,
		};
	}
}
