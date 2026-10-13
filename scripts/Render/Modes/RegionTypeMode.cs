using Godot;
using World.Logic;                   // WorldGenSimulation（世界事实来源）
using World.Constants;              // GeologyClass（地质区域八类词表）

namespace World.Render;

/// <summary>地质区域图（自然地理 · 注册序 = Id = 1）。**八类固定色，全球覆盖**——
/// 平原 / 盆地 / 海岸线 / 山脉 / 裂谷（陆）+ 浅海 / 深海 / 洋中脊（海）。
/// 只做"地理结构分类"，不混入海拔/温度/植被/土壤语义。
/// 地图语义一律读 **Final 口径**（`FinalRegionOfCell`；放置版仅供生成内部）。</summary>
public sealed class RegionTypeMode : MapMode
{
	readonly WorldGenSimulation _p;
	public RegionTypeMode(WorldGenSimulation p) => _p = p;
	public override string Name => "地质区域";
	public override Color CellColorAt(int i)
	{
		int r = _p.Facts.Final.FinalRegionOfCell[i];   // 全球覆盖 ⇒ 恒 ≥ 0
		if (r < 0) return Colors.Magenta;              // 理论不可达（防未生成时静默涂色）
		return _p.Terrain.Regions.Regions[r].Class switch
		{
			GeologyClass.Plain => new Color(0.45f, 0.68f, 0.32f),   // 绿（平原）
			GeologyClass.Basin => new Color(0.45f, 0.52f, 0.60f),   // 蓝灰（盆地）
			GeologyClass.Coastline => new Color(0.85f, 0.80f, 0.45f),   // 沙黄（海岸线带）
			GeologyClass.Mountain => new Color(0.52f, 0.40f, 0.28f),   // 深棕（山脉·碰撞带）
			GeologyClass.Rift => new Color(0.62f, 0.33f, 0.42f),   // 紫红（裂谷·张裂带）
			GeologyClass.ShallowSea => new Color(0.35f, 0.62f, 0.78f),   // 浅蓝（浅海/大陆架）
			GeologyClass.DeepSea => new Color(0.10f, 0.20f, 0.42f),   // 深蓝（深海）
			GeologyClass.Ridge => new Color(0.15f, 0.45f, 0.52f),   // 青（洋中脊·张裂带）
			_ => Colors.Magenta,
		};
	}
}
