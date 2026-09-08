using System;
using System.Collections.Generic;

namespace World.NewHexWorld.UI.Modes
{
	// 地图模式注册表（设计入口 §2.5）：模式有序注册（Id 表，顺序 = 坞按钮序），供坞按钮遍历
	// 与 VM 查询。实例化注册——模式持有逻辑层只读引用（Crust/Ball/板统计），须由组装器在建好
	// 内容层后创建并逐个 Register；新增模式 = 新建策略类 + 此处注册一行 + 坞场景加按钮。
	public sealed class MapModeRegistry
	{
		readonly List<MapMode> _modes = new();

		// 全部模式（注册序 = 坞按钮序；按钮按下回传的 modeId 即列表下标）。
		public IReadOnlyList<MapMode> Modes => _modes;

		public void Register(MapMode mode)
		{
			if (ByIdOrNull(mode.Id) != null)
				throw new InvalidOperationException($"地图模式 Id {mode.Id}（{mode.Name}）重复注册");
			_modes.Add(mode);
		}

		public MapMode ById(int id) => ByIdOrNull(id)
			?? throw new InvalidOperationException($"地图模式 Id {id} 未注册（坞按钮与注册表不同步）");

		MapMode ByIdOrNull(int id)
		{
			foreach (var m in _modes)
				if (m.Id == id) return m;
			return null;
		}
	}
}
