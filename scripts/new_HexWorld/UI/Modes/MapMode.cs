using System;
using System.Collections.Generic;
using Godot;
using World.MapView;   // TileInfoEntry（纯数据结构：标签/值/色块，跨世界复用）

namespace World.NewHexWorld.UI.Modes
{
	// 地图模式策略（设计入口 §2.5，照搬老树 MapLayer 策略形态，语义对齐"地图模式"）：
	// 每模式 = 一个策略类（UI 层，位于 VM 与 View 之间、由 VM 调用）——提供格信息条目与
	// CellColorAt 取色（2026-09-09 材质覆盖方案后取色只喂信息面板色块；球面渲染改片元侧
	// 按 region_data 数据纹理派生，不再逐格投影）。新增模式 = 新建策略类 + MapModeRegistry 注册一行
	// + sphere_region_material.gdshader 加显示分支 + HexDock 坞场景加一个按钮，不碰 View/VM 主体。
	// 数据流：本策略只【读】注入的逻辑层场（Crust/Ball/板统计）派生显示数据，绝不写。
	public abstract class MapMode
	{
		public abstract int Id { get; }         // 模式号（坞按钮按下传回；0 起；同值 = 材质 display_mode uniform）

		public abstract string Name { get; }    // 模式名（坞按钮文案单一事实源——HexDock.BindModes 取此填按钮）

		/// <summary>该模式是否叠加板块边界描边带层（现存两模式都叠；板块模式 09-09 起弃板色自带界）。</summary>
		public abstract bool ShowPlateBoundaries { get; }

		/// <summary>每格显示色（信息面板色块用；cellIndex 与逻辑层场下标对齐；从场派生，禁读/写游戏状态）。</summary>
		public abstract Color CellColorAt(int cellIndex);

		/// <summary>格信息条目（模式特有行；通用行由格信息 VM 拼）。只填数据不拼"标签：值"文本。</summary>
		public virtual IReadOnlyList<TileInfoEntry> TileInfo(int cellIndex) => Array.Empty<TileInfoEntry>();
	}
}
