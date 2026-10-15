using Godot;

namespace World.Render.Constants;

// 地图海格统一底色（深蓝）——"陆地语义"类地图模式的共同海面渲染：
//   海格不分类，一律压成同一底色，使画面连续、陆上信息不被海色抢读。
// 取值 = 海拔色带的**深海档**（`ElevationMode.ElevationColor` 首档），故海陆分界处颜色连续。
//
// ★为什么独立成文件：「各常量独立成文件、不塞汇总类」是既定约定（2026-10-08 星球常数裁决；
//   汇总薄壳 `Render.MapPalette` 亦因纯转发被删）。原为 `WorldGenMapModes` 内的 `OceanR/G/B`
//   三常量，2026-10-08 随地图模式拆分独立成档。★不是取色工具层：取色方式统一由
//   `World.Utils.ColorRamp` 提供，本类只提供一个共享色值。
//
// ★归置（2026-10-09）：从 `scripts/Render/Modes/Mapmode/` 移到 `scripts/Render/Constants/`
//   ⇒ namespace `World.Logic` → `World.Render.Constants`（目录 = namespace）。
internal static class MapSeaColor
{
	/// <summary>海格统一底色（深蓝 = 海拔色带深海档）。</summary>
	public static readonly Color Deep = new(0.05f, 0.16f, 0.42f);
}
