using System;
using System.Collections.Generic;
using World.Render;      // MapMode（表现层契约基类）/ BallView

namespace World.Render.UI;

// 地图坞驱动器（2026-10-08 抽出）：把一份**模式表**绑到坞（按钮文案）与视图（取色函数），
// 并独占维护"当前模式 / 当前参数"状态。原先这段接线塞在 `WorldGenManager` 里，与相机、
// 拾取信息卡混住（`int _currentMode` 甚至声明在两个方法中间——状态放错家的典型信号）。
//
// ★为什么独立成类而不是留在装配层：
//   ① `_modes` / `_currentMode` 是**坞的状态机**，不是"场景装配"的状态；
//   ② 依赖方向干净：本类只认 `MapMode` **抽象**（`World.Render`）+ `MapDock` + `BallView`，
//      **不认识任何 WorldGen 具体类型**——`World.Render` 不得引用 `World.WorldGen`（ADR-0005）。
//      那句"认识世界生成"的 `WorldGenMapModes.CreateAll(planet)` 留在 WorldGenManager，
//      本类只吃结果（`IReadOnlyList<MapMode>`）。
//
// ★为什么不重演 `Render.MapPalette`（纯转发薄壳，2026-10-08 已删）：本类**独占状态 + 契约**——
//   模式表、当前模式镜像、两个信号订阅、BindModes 数量断言、参数行/图例同步、重烘触发。
//   它不是"把 A 的字段转给 B"，而是"坞的行为本体"；只搬 lambda 不搬状态才会退化成薄壳。
//
// ★构造即建立初始一致状态（坞高亮 = 视图取色 = 参数行 = 图例），单点权威——不依赖装配层
//   隐式假设"View 已被注入 modes[0]"。代价 = View 侧多一次 O(n) 重烘（毫秒级，启动期）。
//   （海拔模式 ScaleCaption/ParameterOptions 皆 null ⇒ 初始图例/参数行仍隐藏，坞侧零行为变化。）
public sealed class MapDockController
{
	readonly MapDock _dock;
	readonly BallView _view;
	readonly IReadOnlyList<MapMode> _modes;
	int _currentMode = -1;   // 当前模式 id 镜像（参数信号回查用；ApplyMode 写入）

	public MapDockController(MapDock dock, BallView view, IReadOnlyList<MapMode> modes)
	{
		_dock = dock ?? throw new ArgumentNullException(nameof(dock));
		_view = view ?? throw new ArgumentNullException(nameof(view));
		_modes = modes ?? throw new ArgumentNullException(nameof(modes));
		if (_modes.Count == 0) throw new ArgumentException("模式表为空", nameof(modes));

		// 下行①：按钮文案（模式 Name 是单一事实源；数量不符时坞 BindModes 当场抛）
		var names = new string[_modes.Count];
		for (int i = 0; i < names.Length; i++) names[i] = _modes[i].Name;
		_dock.BindModes(names);

		// 上行：坞只发 id / 下标，由本类负责解释（哑组件的另一半）
		_dock.ModeSelected += OnModeSelected;
		_dock.ParameterSelected += OnParameterSelected;

		ApplyMode(0);   // 初始模式 = 注册序首项（WorldGenMapModes.CreateAll 注册序[0] = 海拔）
	}

	// 上行：点模式按钮 → 切视图取色 + 同步坞高亮/参数行/图例
	void OnModeSelected(int id)
	{
		if (id < 0 || id >= _modes.Count) return;
		ApplyMode(id);
	}

	// 上行：点参数按钮 → 让该模式换"取哪个值"（不碰生产公式）→ 重烘 + 刷新图例
	void OnParameterSelected(int index)
	{
		if (_currentMode < 0) return;
		var mode = _modes[_currentMode];
		mode.SetParameter(index);
		_view.RefreshColors();       // 固定物理域 ⇒ 无需 BeginBake 重算，直接重烘
		_dock.SetLegend(mode.ScaleCaption);
	}

	// 唯一模式切换路径：视图取色 + 坞高亮 + 参数行 + 图例（四处单点同步，杜绝各自为政）
	void ApplyMode(int id)
	{
		_currentMode = id;
		var mode = _modes[id];
		_view.SetMode(mode);        // 换取色函数重烘颜色纹理（几何/UV 不动）
		_dock.SetMode(id);          // 按钮高亮同步
		_dock.SetParameterOptions(mode.ParameterOptions, mode.ParameterIndex);
		_dock.SetLegend(mode.ScaleCaption);
	}
}
