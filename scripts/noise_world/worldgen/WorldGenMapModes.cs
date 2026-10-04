using Godot;
using System.Collections.Generic;
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（通用色带算法，复用不复制）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 地图模式表（地图坞数据源，阶段 3 补接线）：
//   模式持 WorldGenPlanet 引用、CellColorAt 时**现取**当前数据——Regenerate 会换数组实例
//   （不同于现役 NoiseTerrain 的引用稳定数组），持实例引用会读到旧场。
//   海拔模式复用 ElevationBandMode.ElevationColor（同程序集 internal，色带单一事实源不复制）。
// 海格统一深蓝底（与海拔色带深海档同色，画面连续）。
public static class WorldGenMapModes
{
	/// <summary>全部模式（注册序 = 坞按钮序 = Id）。「大陆」势力图已删（2026-10-02 用户拍板）：
	///   锚点 Voronoi 归属是连续场内部塑形判据，不是地图语义——地图语义 = 陆块连通分量（投影层）。</summary>
	public static List<NoiseMapMode> CreateAll(WorldGenPlanet planet) => new()
	{
		new ElevationMode(planet),
		new LandSeaMode(planet),
		new RegionTypeMode(planet),
		new CoastDistanceMode(planet),
	};

	const float OceanR = 0.05f, OceanG = 0.16f, OceanB = 0.42f;   // 深海底色（= ElevationColor 深海档）

	sealed class ElevationMode : NoiseMapMode
	{
		readonly WorldGenPlanet _p;
		public ElevationMode(WorldGenPlanet p) => _p = p;
		public override int Id => 0;
		public override string Name => "海拔";
		public override Color CellColorAt(int i) =>
			_p.Final.FinalLand[i]
				? ElevationBandMode.ElevationColor(_p.DisplayElevation[i])   // 陆 = 合成海拔分档（HeightComposer 唯一出处）
				: new Color(OceanR, OceanG, OceanB);
	}

	/// <summary>海陆结构场原值（连续 raw）：发散色带（负 = 蓝 / 0 ≈ 海岸线 = 纸白 / 正 = 棕红）。</summary>
	sealed class LandSeaMode : NoiseMapMode
	{
		readonly WorldGenPlanet _p;
		public LandSeaMode(WorldGenPlanet p) => _p = p;
		public override int Id => 1;
		public override string Name => "海陆场";
		public override Color CellColorAt(int i) => RampSampleSmooth(NoiseMapMode.DivergingStops, _p.Projector.Raw[i] / 0.8f);
	}

	/// <summary>地质区域类型（七类固定色；主判读图——用户拍板的地图语义）。</summary>
	sealed class RegionTypeMode : NoiseMapMode
	{
		readonly WorldGenPlanet _p;
		public RegionTypeMode(WorldGenPlanet p) => _p = p;
		public override int Id => 2;
		public override string Name => "地质区域";
		public override Color CellColorAt(int i)
		{
			// 收口（§07 D-1）：地图语义一律读 **Final 口径**（最终区域归属；Placement 版仅供生成内部）
			int r = _p.Final.FinalRegionOfCell[i];
			if (r < 0) return new Color(OceanR, OceanG, OceanB);
			return _p.Regions.Regions[r].Type switch
			{
				RegionType.Plain    => new Color(0.45f, 0.68f, 0.32f),   // 绿（平原）
				RegionType.Highland => new Color(0.78f, 0.66f, 0.36f),   // 黄褐（高地）
				RegionType.Basin    => new Color(0.45f, 0.52f, 0.60f),   // 蓝灰（盆地）
				RegionType.Mountain => new Color(0.52f, 0.40f, 0.28f),   // 深棕（山地）
				RegionType.Plateau  => new Color(0.85f, 0.55f, 0.30f),   // 橙（高原）
				RegionType.Rift     => new Color(0.68f, 0.32f, 0.52f),   // 紫红（裂谷）
				RegionType.Coastal  => new Color(0.55f, 0.78f, 0.55f),   // 青绿（沿岸）
				_ => Colors.Magenta,
			};
		}
	}

	/// <summary>离岸距离（陆 = 离海跳数内陆深度 / 海 = 离岸跳数）：近岸浅 → 远离深。</summary>
	sealed class CoastDistanceMode : NoiseMapMode
	{
		readonly WorldGenPlanet _p;
		public CoastDistanceMode(WorldGenPlanet p) => _p = p;
		public override int Id => 3;
		public override string Name => "离岸距离";
		public override Color CellColorAt(int i)
		{
			// 收口（§07 D-1）：离岸距离同样读 **Final 口径**（FinalDistToCoast/Land 由 FinalLand 派生）
			var f = _p.Final;
			int d = f.FinalLand[i] ? f.FinalDistToCoast[i] : f.FinalDistToLand[i];
			float t = System.Math.Clamp(d / 12f, 0f, 1f);   // 12 跳满域（res4 实测量级）
			return f.FinalLand[i]
				? new Color(0.93f - 0.45f * t, 0.78f - 0.30f * t, 0.32f + 0.15f * t)   // 陆：近岸米白 → 内陆深棕
				: new Color(0.55f - 0.50f * t, 0.75f - 0.59f * t, 0.80f - 0.38f * t);  // 海：近岸青白 → 深海蓝
		}
	}
}
