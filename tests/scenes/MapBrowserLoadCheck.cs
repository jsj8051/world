using Godot;
using World.Scene;

namespace World.Tests;

/// <summary>
/// 浏览加载端到端检查：实例化 AppRoot，调用 OpenMap，等待操作结束，断言进入浏览页。
/// 用法：godot --headless --path . res://tests/scenes/MapBrowserLoadCheck.tscn -- &lt;地图路径&gt;
/// </summary>
public partial class MapBrowserLoadCheck : Node
{
	public override async void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs();
		if (args.Length == 0)
		{
			GD.PushError("[BROWSE-CHECK] 缺少地图路径");
			GetTree().Quit(2);
			return;
		}

		var packed = ResourceLoader.Load<PackedScene>("res://scenes/app/AppRoot.tscn");
		if (packed == null)
		{
			GD.PushError("[BROWSE-CHECK] AppRoot 场景缺失");
			GetTree().Quit(2);
			return;
		}

		var app = packed.Instantiate<AppRoot>();
		AddChild(app);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		ulong started = Time.GetTicksMsec();
		ulong last = started;
		ulong maxGap = 0;
		app.OpenMap(args[0]);
		while (app.HasActive && Time.GetTicksMsec() - started < 60_000)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			ulong now = Time.GetTicksMsec();
			if (now - last > maxGap) maxGap = now - last;
			last = now;
		}

		bool ok = !app.HasActive && app.CurrentScreen is MapBrowser;
		GD.Print($"[BROWSE-CHECK] ok={ok} screen={app.CurrentScreen?.GetType().Name ?? "null"} " +
			$"ms={Time.GetTicksMsec() - started} maxGap={maxGap}");
		GetTree().Quit(ok ? 0 : 1);
	}
}
