using Godot;

namespace World.Render.Constants;

// 地图海格统一底色（深蓝）——"陆地语义"类地图模式（海拔 / 地质区域）的共同海面渲染：
//   海格不分类，一律压成同一底色，使画面连续、陆上信息不被海色抢读。
// 取值 = 海拔色带的**深海档**（`ElevationMode.ElevationColor` 首档），故海陆分界处颜色连续。
//
// ★为什么独立成文件：本常量有**两个真实消费者**（海拔模式 + 地质区域模式），任何一边都不宜
//   认领它（会造成兄弟模式间的伪依赖）。原为 `WorldGenMapModes` 内的 `OceanR/G/B` 三常量，
//   2026-10-08 随地图模式拆分独立成档（与 `scripts/Planet/Thermal.cs` 同风格：共享常量
//   各自独立成文件，不塞进汇总类）。★不是取色工具层：取色方式统一由 `World.Utils.ColorRamp`
//   提供，本类只提供一个共享色值。
//
// ★归置（2026-10-09）：从 `scripts/Render/Modes/Mapmode/` 移到 `scripts/Render/Constants/`
//   ⇒ namespace `World.WorldGen` → `World.Render.Constants`（目录 = namespace）。理由：
//   `Modes/Mapmode/` 的单一职责是"单个模式实现"，跨模式共享的常量混在里面会被误读成某模式的
//   私有物；本目录是**跨模式共享常量**的固定住处。★仍**不建汇总配色类**——"各常量独立成文件"
//   这条不变（2026-10-08 星球常数裁决；`Render.MapPalette` 汇总薄壳亦因纯转发被删），只改归置。
internal static class MapSeaColor
{
	/// <summary>海格统一底色（深蓝 = 海拔色带深海档）。</summary>
	public static readonly Color Deep = new(0.05f, 0.16f, 0.42f);
}
