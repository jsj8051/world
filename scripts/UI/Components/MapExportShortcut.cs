using Godot;

namespace World.UI.Components;

/// <summary>页面地图导出快捷键的输入匹配规则。</summary>
internal static class MapExportShortcut
{
	internal static bool Matches(InputEvent input)
		=> input is InputEventKey key && key.Pressed && !key.Echo
			&& (key.Keycode == Key.F12 || (key.CtrlPressed && key.Keycode == Key.E));
}
