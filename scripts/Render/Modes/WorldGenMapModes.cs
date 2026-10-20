using System.Collections.Generic;
using World.Logic;

namespace World.Render;

/// <summary>
/// 世界生成空间的地图模式注册表（地图坞数据源）。
/// 一个模式 = 一个可独立判读的世界字段/分类事实的视觉投影：有明确世界语义、有稳定数据源、
/// 不反向依赖生成过程、适合逐格解释；线状符号走 <see cref="RiverLineOverlay"/>，未稳定语义不进坞。
/// 注册序 = 坞按钮序 = 模式身份（下标），模式类不自报 Id ⇒ 增删/换序只改本表。
/// 新增/删除模式必须同步 `scenes/render/MapDock.tscn` 的 ModeRow 按钮（数量不符 BindModes 即抛）。
/// 色带用物理固定域，禁止 min-max 自动拉伸（否则跑 100 个世界互不可比）；
/// 长尾量（降水）只做对数式显示变换，数据本身与生产公式不动。
/// </summary>
public static class WorldGenMapModes
{
	/// <summary>全部模式（注册序 = 坞按钮序 = 模式身份）。「板块」画的是 ⓪ 板块阶段真正产出的
	/// 板块划分事实（<c>PlateOfCell</c>，全球覆盖、每格恒有归属）——只涂划分，不涂壳型（洋壳也归板块）。</summary>
	public static List<MapMode> CreateAll(WorldGenSimulation sim) => new()
	{
		// 列表序 = 坞按钮序 = 模式身份（下标）。增删/换序只改这里，不必碰任何模式类。
		// 模式持 sim 引用但每次现取字段：Run() 会换数组实例，存数组引用会读到旧场。
		new TerrainMode(sim),          // 自然地理（初始画面：350 m 等高带高度图）
		new PlateMode(sim),            // 成因层（⓪ 板块划分）
		new ElevationMode(sim),        // 自然地理（纯海拔判读）
		new CoastDistanceMode(sim),    // 自然地理
		new PrecipitationMode(sim),    // 气候
		new TemperatureMode(sim),      // 气候
	};
}
