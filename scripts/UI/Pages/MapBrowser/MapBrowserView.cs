using System;
using Godot;
using World.Application.Pages;
using World.Render;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>显示已经装配的地图并转发返回意图。</summary>
public partial class MapBrowserView : Node3D, IMapBrowserPageActions
{
	[Export] public NodePath CameraPath { get; set; }
	[Export] public NodePath RenderManagerPath { get; set; }
	[Export] public NodePath CellInfoCardPath { get; set; }
	[Export] public NodePath DockPath { get; set; }
	[Export] public NodePath PanelLayerPath { get; set; }
	[Export] public NodePath TitleLabelPath { get; set; }
	[Export] public NodePath StatusLabelPath { get; set; }
	[Export] public NodePath AppMenuPath { get; set; }

	IMapBrowserPageActions _actions;
	MapBrowserPresentation _presentation;
	AppMenu _menu;
	Label _title;
	Label _status;

	internal static MapBrowserView Create(IMapBrowserPageActions actions)
	{
		var page = ResourceLoader.Load<PackedScene>("res://scenes/pages/MapBrowser/MapBrowserPage.tscn")
			.Instantiate<MapBrowserView>();
		page._actions = actions;
		return page;
	}

	internal IMapBrowserPresentation PreparePresentation() => _presentation = new MapBrowserPresentation(
		this.Require<RenderManager>(RenderManagerPath, nameof(RenderManagerPath)),
		this.Require<OrbitalCamera>(CameraPath, nameof(CameraPath)),
		this.Require<CellInfoCard>(CellInfoCardPath, nameof(CellInfoCardPath)),
		this.Require<MapDock>(DockPath, nameof(DockPath)),
		this.Require<CanvasLayer>(PanelLayerPath, nameof(PanelLayerPath)));

	internal void Activate(string title)
	{
		if (_presentation == null)
			throw new InvalidOperationException("地图画面尚未就绪。");
		_title.Text = title;
		_presentation.Show();
		_title.Show();
		_menu.Visible = true;
	}

	public override void _Ready()
	{
		_title = this.Require<Label>(TitleLabelPath, nameof(TitleLabelPath));
		_status = this.Require<Label>(StatusLabelPath, nameof(StatusLabelPath));
		_menu = this.Require<AppMenu>(AppMenuPath, nameof(AppMenuPath));
		_menu.Configure("浏览地图", new AppMenuItem("back", "返回地图库"));
		_menu.ItemPressed += OnMenuItemPressed;
		bool standalone = _actions == null;
		_title.Visible = standalone;
		_status.Visible = standalone;
		_menu.Visible = standalone;
	}

	internal void ShowStatus(string message)
	{
		_status.Text = message;
		_status.Show();
	}

	void IMapBrowserPageActions.BackToMapLibrary() => ReturnToLibrary();

	void ReturnToLibrary()
	{
		if (_actions == null) SceneReplacement.Replace(this, MapBrowsingHost.Create());
		else _actions.BackToMapLibrary();
	}

	void OnMenuItemPressed(string id)
	{
		if (id == "back") ReturnToLibrary();
	}

	public override void _ExitTree()
	{
		if (_menu != null) _menu.ItemPressed -= OnMenuItemPressed;
	}
}
