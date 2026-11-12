using System;
using Godot;
using World.UI;

namespace World.Scene;

public partial class WorldRoot
{
	/// <summary>共享应用菜单（左上按钮 + 居中面板）。</summary>
	[Export] public NodePath AppMenuPath { get; set; }

	IPageNavigator _pageNav;
	INoticeSink _pageNotice;
	ILoadProgressSink _progressSink;
	PageContext _pageContext;

	void IAppPage.Configure(PageContext context)
	{
		_pageContext = context;
		_pageNav = context?.Navigation;
		_pageNotice = context?.Notice;
		_progressSink = context?.Progress;
	}

	void IAppPage.OnHidden()
	{
		_generationBinding = null;
		_generationMonitor?.Dispose();
		_generationMonitor = null;
		_generationOperation?.Cancel();
		_renderBuilding = false;
		Ticks.Clear();
		World?.PauseEvolution();
	}

	partial void OnMenusReady()
	{
		var menu = GetNodeOrNull<AppMenu>(AppMenuPath);
		if (menu == null) return;
		menu.Configure("菜单",
			new AppMenuItem("export_map", "导出地图数据"),
			new AppMenuItem("export_image", "导出图片"),
			new AppMenuItem("back", "返回主界面"));
		menu.ItemPressed += id =>
		{
			switch (id)
			{
				case "export_map": OnExportMapPressed(); break;
				case "export_image": OnExportImagePressed(); break;
				case "back": OnBackToMenuPressed(); break;
			}
		};
	}

	/// <summary>场景信号入口：返回主界面（独立 F6 运行时无应用根，保持原场景）。</summary>
	public void OnBackToMenuPressed() => _pageNav?.Navigate(new MainMenuRequest());

	/// <summary>场景信号入口：导出当前已发布地图数据。</summary>
	public void OnExportMapPressed()
	{
		try
		{
			_pageNotice?.Notify("正在导出地图数据…");
			string path = World.ExportMapData();
			_pageNotice?.Notify($"地图数据已导出：{path}", NoticeKind.Success);
			GD.Print($"[MAP] 地图数据已导出：{path}");
		}
		catch (Exception e)
		{
			_pageNotice?.Notify($"地图数据导出失败：{e.Message}", NoticeKind.Error);
			GD.PushError($"[MAP] 地图数据导出失败：{e}");
		}
	}

	/// <summary>场景信号入口：导出当前地图模式的 2D 图片。</summary>
	public void OnExportImagePressed()
	{
		try
		{
			_pageNotice?.Notify("正在导出地图图片…");
			string path = Render.ExportTerrainMapNow();
			_pageNotice?.Notify($"地图图片已导出：{path}", NoticeKind.Success);
		}
		catch (Exception e)
		{
			_pageNotice?.Notify($"地图图片导出失败：{e.Message}", NoticeKind.Error);
			GD.PushError($"[MAP] 地图图片导出失败：{e}");
		}
	}
}