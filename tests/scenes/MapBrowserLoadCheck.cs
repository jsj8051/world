using System;
using System.Linq;
using System.Reflection;
using Godot;
using World.Application.Components;
using World.Application.Pages;
using World.UI.Pages;

namespace World.Tests;

/// <summary>页面自行打开浏览页，等待控制器就绪或失败，测试根节点保持不变。</summary>
public partial class MapBrowserLoadCheck : Node
{
	public override async void _Ready()
	{
		try
		{
			string[] args = OS.GetCmdlineUserArgs();
			if (args.Length == 0)
			{
				GD.PushError("[BROWSE-CHECK] 缺少地图路径");
				GetTree().Quit(2);
				return;
			}

			PageShellCheck.Run();
			GD.Print($"[MAP-BROWSER-CONTROLLER] PASS {MapBrowserControllerCheck.Run()} assertions");
			var library = MapLibraryView.Create();
			AddChild(library);
			ulong started = Time.GetTicksMsec();
			ulong last = started;
			ulong maxGap = 0;
			((IMapCatalogActions)library).LoadMap(args[0]);
			Require(GetChildren().OfType<MapLibraryView>().SingleOrDefault() == library,
				"The library must stay mounted until deferred replacement.");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			var page = GetChildren().OfType<MapBrowserView>().SingleOrDefault();
			Require(page != null && !GodotObject.IsInstanceValid(library), "The browser did not replace the library under the test runner.");
			var controller = typeof(MapBrowserView).GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)
				?.GetValue(page) as MapBrowserController;
			Require(controller != null, "The browser has no injected controller.");
			if (Array.IndexOf(args, "--cancel") >= 0)
			{
				Require(controller.IsBusy, "Cancellation check must start during loading or building.");
				((IMapBrowserPageActions)page).BackToMapLibrary();
				for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Require(controller.State == MapBrowserState.Closed && !GodotObject.IsInstanceValid(page),
					"Leaving must close the controller and release its page.");
				Require(GetChildren().OfType<MapLibraryView>().Count() == 1 && GetChildCount() == 1,
					"Late loading results must not reopen the browser.");
				GD.Print("[BROWSE-CHECK] PASS cancel during loading and return to library");
				GetTree().Quit();
				return;
			}
			while (controller.State is not (MapBrowserState.Ready or MapBrowserState.Failed)
				&& Time.GetTicksMsec() - started < 60_000)
			{
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				ulong now = Time.GetTicksMsec();
				if (now - last > maxGap) maxGap = now - last;
				last = now;
			}

			bool expectFailure = args.Length > 1 && args[1] == "--expect-failure";
			bool ok = GetChildren().OfType<MapBrowserView>().SingleOrDefault() == page
				&& GetTree().CurrentScene == this && GetChildCount() == 1
				&& controller.State == (expectFailure ? MapBrowserState.Failed : MapBrowserState.Ready);
			GD.Print($"[BROWSE-CHECK] ok={ok} state={controller.State} " +
				$"stage={controller.Stage} ms={Time.GetTicksMsec() - started} maxGap={maxGap}");
			GetTree().Quit(ok ? 0 : 1);
		}
		catch (Exception error) { GD.PushError($"[BROWSE-CHECK] {error}"); GetTree().Quit(1); }
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
