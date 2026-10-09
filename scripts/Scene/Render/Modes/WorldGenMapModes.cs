using System.Collections.Generic;
using World.Render;                // MapMode（表现层契约基类）

namespace World.WorldGen;

// 世界生成空间 · 地图模式注册表（地图坞数据源）。
//
// ★设计原则（2026-10-07 地图坞契约拍板）：
//   地图模式 = 一个可独立判读的世界字段/分类事实的视觉投影，必须满足四件事：
//   ① 有明确世界语义 ② 有稳定数据源 ③ 不反向依赖生成过程 ④ 适合逐格解释。
//   不是"把所有内部数据可视化一遍"——RiverGeometry 等线状符号走 Overlay（RiverLineOverlay），
//   CivSim 势力等未稳定语义不进坞（有数据 ≠ 应该成为地图语义）。
//
// ★注册序 = 坞按钮序 = **模式身份（下标）**，按判读链排布：自然地理 → 气候 → 生成诊断（垫底）。
//   模式类**不自报 Id**（2026-10-09 删除冗余的 `MapMode.Id`）⇒ 增删模式只改本表行序，零连带。
//   ★"诊断垫底、不与世界语义混排"这个约定**只靠本注册顺序 + 注释维持**（原先有 `MapModeGroup`
//   成员承载，因无生产消费者已删，2026-10-08）；新增诊断模式请继续排到表末尾并注明。
//   判读链：海拔 → 地质区域 → 离岸距离 → 降水 → 温度 →（P4-4 后土壤）→（P4-3b 后 Biome）——
//   各模式只读**各自的生产事实**，坞内不做模式间互相计算。
//   ★季节气候/洋流/风场/水汽/耦合降水模式已随三大体系删除（2026-10-07 用户拍板全部删除
//   重做）；温度已重做 v1（2026-10-08 逐格能量收支）并**按新事实重新注册**（Id=4）。
//   ⚠️ 新增模式必须同步 `scenes/render/MapDock.tscn` 的 ModeRow 按钮（数量不符 BindModes 即抛）。
//
// ★文件切分（2026-10-08）：本类只留**注册表**；每个模式实现各自独立成文件——
//   海拔 `ElevationMode`｜地质区域 `RegionTypeMode`｜离岸距离 `CoastDistanceMode`
//   ｜降水 `PrecipitationMode`｜温度 `TemperatureMode`｜海陆场 `LandSeaMode`（诊断）。
//   模式专属色带/派生表随模式走（色带内聚其消费方文件）：Precip 表在 PrecipitationMode，
//   Temperature 表在 TemperatureMode，Diverging 表在 LandSeaMode，海拔分档色/名表在 ElevationMode；
//   两模式共用的海格底色见 `Render/Constants/MapSeaColor.cs`（跨模式共享常量的固定住处）。
//
// ★色带纪律：物理固定域，**禁止 min-max 自动拉伸**（否则跑 100 个世界互不可比）；
//   长尾量（降水）允许"对数式显示变换"——只变换显示采样，**数据本身不改、生产公式不动**。
// 模式持 WorldGenSimulation 引用（**纯逻辑入口，非 Godot 节点**）、CellColorAt 时**现取**当前
//   数据——`Run()` 会换数组实例，持实例引用会读到旧场（读属性不存数组引用）。
public static class WorldGenMapModes
{
	/// <summary>全部模式（注册序 = 坞按钮序 = Id）。「大陆」势力图已删（2026-10-02 用户拍板）：
	///   锚点 Voronoi 归属是连续场内部塑形判据，不是地图语义——地图语义 = 陆块连通分量（投影层）。</summary>
	public static List<MapMode> CreateAll(WorldGenSimulation sim) => new()
	{
		// 列表序 = 坞按钮序 = 模式身份（下标）。增删/换序**只改这里**，不必碰任何模式类。
		new ElevationMode(sim),        // 自然地理
		new RegionTypeMode(sim),       // 自然地理
		new CoastDistanceMode(sim),    // 自然地理
		new PrecipitationMode(sim),    // 气候
		new TemperatureMode(sim),      // 气候
		new LandSeaMode(sim),          // 生成诊断（垫底，不与世界语义混排）
	};
}
