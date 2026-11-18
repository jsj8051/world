using System;
using System.Linq;
using Godot;
using World.Application.Components;
using World.UI.Pages;

namespace World.Tests;

/// <summary>验证地图库读档宿主的加载、成功、失败及退出清理。</summary>
public partial class MapBrowserLoadCheck : Node
{
	public override async void _Ready()
	{
		try
		{
			string[] args = OS.GetCmdlineUserArgs();
			if (args.Length == 0) throw new InvalidOperationException("缺少地图路径。");
			PageShellCheck.Run();
			GD.Print($"[MAP-BROWSER-CONTROLLER] PASS {MapBrowserControllerCheck.Run()} assertions");
			var library = MapLibraryView.Create();
			AddChild(library);
			((IMapCatalogActions)library).LoadMap(args[0]);
			Require(library.IsInsideTree(), "Replacement must be deferred.");
			for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			var host = GetChildren().OfType<MapBrowsingHost>().SingleOrDefault();
			Require(host != null && GetChildCount() == 1, "The browsing host must replace the library.");
			if (args.Contains("--cancel"))
			{
				RemoveChild(host);
				host.QueueFree();
				for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Require(GetChildCount() == 0, "Late loading results must not recreate a page after exit.");
				GD.Print("[BROWSE-CHECK] PASS cancel during loading and release host");
				GetTree().Quit();
				return;
			}

			bool expectFailure = args.Contains("--expect-failure");
			ulong started = Time.GetTicksMsec();
			while (Time.GetTicksMsec() - started < 60_000)
			{
				bool loading = host.GetChildren().OfType<MapLoadingView>().Any();
				bool browser = host.GetChildren().OfType<MapBrowserView>().Any();
				bool returnedToLibrary = host.GetChildren().OfType<MapLibraryView>().Any();
				if (!loading && (expectFailure ? returnedToLibrary : browser))
				{
					Require(GetTree().CurrentScene == this && host.GetChildCount() == 1, "Only the destination page may remain mounted.");
					GD.Print($"[BROWSE-CHECK] PASS {(expectFailure ? "failure returns to library" : "relative-path map loading and presentation ready")}");
					GetTree().Quit();
					return;
				}
				if (!expectFailure && returnedToLibrary) throw new InvalidOperationException("Map loading returned to library unexpectedly.");
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			}
			throw new TimeoutException("Map browsing did not reach its destination.");
		}
		catch (Exception error) { GD.PushError($"[BROWSE-CHECK] {error}"); GetTree().Quit(1); }
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
