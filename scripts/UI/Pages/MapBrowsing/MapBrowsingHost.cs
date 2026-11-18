using System;
using Godot;
using World.Application.Components;
using World.Application.Pages;
using World.Application.Storage;
using World.Infrastructure.Maps;
using World.Utils;

namespace World.UI.Pages;

/// <summary>地图库、加载页和浏览页的父节点，持有页面切换上下文及本次浏览会话。</summary>
internal partial class MapBrowsingHost : Node, IMapCatalogActions, IMapBrowserPageActions
{
	IGeographyMapCatalog _repository;
	Node _activePage;
	string _selectedPath;
	MapBrowserView _browser;
	MapBrowserController _controller;
	bool _returnToLibrary;

	internal static MapBrowsingHost Create(IGeographyMapCatalog repository = null, string selectedPath = null)
	{
		if (selectedPath != null) selectedPath = RootRelativePath.Normalize(selectedPath);
		return new MapBrowsingHost
		{
			Name = nameof(MapBrowsingHost),
			_repository = repository ?? GameMapCatalog.CreateDefault(),
			_selectedPath = selectedPath,
		};
	}

	public override void _Ready()
	{
		if (_selectedPath == null) ShowLibrary();
		else ShowLoading(MapLoadingView.Create());
	}

	void IMapCatalogActions.LoadMap(string path)
	{
		path = RootRelativePath.Normalize(path);
		if (_activePage is not MapLibraryView || _selectedPath != null) return;
		var loading = MapLoadingView.Create();
		_selectedPath = path;
		Callable.From(() =>
		{
			if (!IsInstanceValid(this) || !IsInsideTree())
			{
				loading.Free();
				return;
			}
			ShowLoading(loading);
		}).CallDeferred();
	}

	void IMapCatalogActions.BackToMainMenu()
	{
		if (_activePage is not MapLibraryView) return;
		SceneReplacement.Replace(this, MainMenuView.Create());
		SetProcess(false);
	}

	void IMapBrowserPageActions.BackToMapLibrary()
	{
		if (_activePage is not MapBrowserView) return;
		_returnToLibrary = true;
		SetProcess(true);
	}

	public override void _Process(double delta)
	{
		if (_returnToLibrary)
		{
			ShowLibrary();
			return;
		}
		if (_activePage is MapLoadingView loading)
		{
			TickLoading(loading, delta);
			return;
		}
		if (_activePage is not MapBrowserView browser) return;
		_controller.Tick(delta);
		if (_controller.State == MapBrowserState.Failed)
		{
			browser.ShowStatus(_controller.Stage);
			SetProcess(false);
		}
	}

	void TickLoading(MapLoadingView loading, double delta)
	{
		try
		{
			if (_controller == null) BeginLoading();
			_controller.Tick(delta);
			switch (_controller.State)
			{
				case MapBrowserState.Ready:
					_browser.Activate(_controller.Title);
					SwitchPage(_browser);
					break;
				case MapBrowserState.Failed: ShowLibrary($"无法加载地图：{_controller.Stage}"); break;
				case MapBrowserState.Closed: ShowLibrary("地图加载已取消。"); break;
				default:
					break;
			}
		}
		catch (Exception error)
		{
			ShowLibrary($"无法加载地图：{error.Message}");
		}
	}

	void ShowLoading(MapLoadingView loading)
	{
		SwitchPage(loading);
		loading.Begin();
		SetProcess(true);
	}

	void BeginLoading()
	{
		// 提前入树以装配地图。完成时保留这棵子树，避免渲染资源和订阅随退出清理。
		_browser = MapBrowserView.Create(this);
		AddChild(_browser);
		var loader = new FileLoadComponent<World.Data.Maps.GeographySnapshot>(
			new MapPackageFileStorage(), World.Infrastructure.GamePaths.GameDirectory);
		_controller = new MapBrowserController(_selectedPath, loader);
		_controller.Start(_browser.PreparePresentation());
	}

	void ShowLibrary(string loadFailure = null)
	{
		SetProcess(false);
		_returnToLibrary = false;
		_selectedPath = null;
		ReleaseSession();
		var library = MapLibraryView.Create(_repository, this);
		SwitchPage(library);
		if (loadFailure != null) library.ReportLoadFailure(loadFailure);
	}

	void SwitchPage(Node next)
	{
		if (_activePage != null)
		{
			RemoveChild(_activePage);
			_activePage.QueueFree();
		}
		_activePage = next;
		if (next.GetParent() == null) AddChild(next);
	}

	void ReleaseSession()
	{
		_controller?.Dispose();
		_controller = null;
		if (_browser != _activePage && IsInstanceValid(_browser)) _browser.QueueFree();
		_browser = null;
	}

	public override void _ExitTree() => ReleaseSession();
}
