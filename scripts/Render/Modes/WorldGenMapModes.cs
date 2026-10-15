using System.Collections.Generic;
using World.Logic;                   // WorldGenSimulation / RiverGeometry / RiverGraph（世界事实与水文事实）

namespace World.Render;

// 世界生成空间 · 地图模式注册表（地图坞数据源）。
//
// ★设计原则（2026-10-07 地图坞契约拍板）：
//   地图模式 = 一个可独立判读的世界字段/分类事实的视觉投影，必须满足四件事：
//   ① 有明确世界语义 ② 有稳定数据源 ③ 不反向依赖生成过程 ④ 适合逐格解释。
//   不是"把所有内部数据可视化一遍"——RiverGeometry 等线状符号走 Overlay（RiverLineOverlay），
//   CivSim 势力等未稳定语义不进坞（有数据 ≠ 应该成为地图语义）。
//   ★★③ 是硬约束的实证：原"海陆场"诊断模式画的是**连续场逐格副本**——它没有世界语义，
//     只是生成器内部状态；该出口已删 ⇒ 该模式随之下线。**"数据拿得到"不等于"能进坞"**。
//
// ★注册序 = 坞按钮序 = **模式身份（下标）**，按判读链排布：成因 → 自然地理 → 气候。
//   模式类**不自报 Id** ⇒ 增删模式只改本表行序，零连带。
//   判读链：板块（成因层）→ 海拔 → 离岸距离 → 降水 → 温度。
//   各模式只读**各自的生产事实**，坞内不做模式间互相计算。
//   ⚠️ 新增/删除模式必须同步 `scenes/render/MapDock.tscn` 的 ModeRow 按钮（数量不符 BindModes 即抛）。
//
// ★文件切分：本类只留**注册表**；每个模式实现各自独立成文件——
//   板块 `PlateMode`｜海拔 `ElevationMode`｜离岸距离 `CoastDistanceMode`
//   ｜降水 `PrecipitationMode`｜温度 `TemperatureMode`。
//   模式专属色带/派生表随模式走（色带内聚其消费方文件）；
//   两模式共用的海格底色见 `Render/Constants/MapSeaColor.cs`。
//
// ★色带纪律：物理固定域，**禁止 min-max 自动拉伸**（否则跑 100 个世界互不可比）；
//   长尾量（降水）允许"对数式显示变换"——只变换显示采样，**数据本身不改、生产公式不动**。
// 模式持 WorldGenSimulation 引用（**纯逻辑入口，非 Godot 节点**）、CellColorAt 时**现取**当前
//   数据——`Run()` 会换数组实例，持实例引用会读到旧场（读属性不存数组引用）。
public static class WorldGenMapModes
{
	/// <summary>全部模式（注册序 = 坞按钮序 = Id）。「板块」画的是 ⓪ 板块阶段真正产出的
	///   **板块划分事实**（`PlateOfCell`，全球覆盖、每格恒有归属）——只涂划分，不涂壳型（洋壳也归板块）。</summary>
	public static List<MapMode> CreateAll(WorldGenSimulation sim) => new()
	{
		// 列表序 = 坞按钮序 = 模式身份（下标）。增删/换序**只改这里**，不必碰任何模式类。
		new PlateMode(sim),            // 成因层（⓪ 板块划分）
		new ElevationMode(sim),        // 自然地理
		new CoastDistanceMode(sim),    // 自然地理
		new PrecipitationMode(sim),    // 气候
		new TemperatureMode(sim),      // 气候
	};
}
