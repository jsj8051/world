using System;
using System.Collections.Generic;
using World.NewHexWorld.UI.Modes;

namespace World.NewHexWorld.UI.ViewModels
{
	// 坞 VM（设计入口 §2.2）：模式列表（注册表派生）+ 当前模式（坞的交互状态）。
	// 模式切换 = Select(modeId) → ModeChanged 事件广播；组装器接线：球视图 VM 重投影 +
	// 格信息 VM 换策略 + 坞按钮高亮下行。事件为 C# 轻量事件（无框架，用户拍板）。
	public sealed class DockViewModel
	{
		readonly MapModeRegistry _registry;
		int _currentModeId;

		public DockViewModel(MapModeRegistry registry, int initialModeId = 0)
		{
			_registry = registry;
			_currentModeId = initialModeId;
		}

		// 模式变更信号（参数 = 新模式；组装器订阅转发各 VM/View）。
		public event Action<MapMode> ModeChanged;

		// 模式列表（注册序 = 坞按钮序；派生自注册表，无副本）。
		public IReadOnlyList<MapMode> Modes => _registry.Modes;

		// 当前模式策略（坞选中；派生）。
		public MapMode CurrentMode => _registry.ById(_currentModeId);

		// 选择模式（坞按钮上抛路径）；同模式重复选择幂等不广播。
		public void Select(int modeId)
		{
			MapMode mode = _registry.ById(modeId);
			if (mode.Id == _currentModeId) return;
			_currentModeId = mode.Id;
			ModeChanged?.Invoke(mode);
		}
	}
}
