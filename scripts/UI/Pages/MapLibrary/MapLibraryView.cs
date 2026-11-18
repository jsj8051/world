using System;
using System.Collections.Generic;
using Godot;
using World.Application.Components;
using World.Application.Storage;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>
/// 地图库页面宿主：标题与返回按钮，内容交给 <see cref="MapCatalogPanel"/>。
/// 存档列表、选择与加载意图归 <see cref="MapCatalogController"/>。
/// </summary>
public partial class MapLibraryView : Control, IMapCatalogActions
{
	[Export] public NodePath CatalogPath { get; set; }
	[Export] public NodePath BackButtonPath { get; set; }

	IGeographyMapCatalog _repository;
	IMapCatalogActions _actions;
	MapCatalogController _controller;
	MapCatalogPanel _catalog;

	internal static MapLibraryView Create(IGeographyMapCatalog repository = null, IMapCatalogActions actions = null)
	{
		var page = GD.Load<PackedScene>("res://scenes/pages/MapLibrary/MapLibraryPage.tscn").Instantiate<MapLibraryView>();
		page._repository = repository;
		page._actions = actions;
		return page;
	}

	/// <summary>注入测试或自定义存档库；默认使用游戏存档目录。</summary>
	internal void SetRepository(IGeographyMapCatalog repository)
		=> _repository = repository ?? throw new ArgumentNullException(nameof(repository));

	/// <summary>注入测试或自定义页面动作；默认由本页面处理导航。</summary>
	internal void SetActions(IMapCatalogActions actions) => _actions = actions;

	public override void _Ready()
	{
		_catalog = this.Require<MapCatalogPanel>(CatalogPath, nameof(CatalogPath));
		var back = this.Require<Button>(BackButtonPath, nameof(BackButtonPath));

		_repository ??= World.Infrastructure.Maps.GameMapCatalog.CreateDefault();
		_controller = new MapCatalogController(_repository, _actions ?? this);
		back.Pressed += () => _controller.BackToMainMenu();
		_catalog.Bind(_controller);
		_controller.Refresh();
	}

	void IMapCatalogActions.BackToMainMenu()
		=> SceneReplacement.Replace(this, MainMenuView.Create());

	void IMapCatalogActions.LoadMap(string path)
		=> SceneReplacement.Replace(this, MapBrowsingHost.Create(_repository, path));

	internal void ReportLoadFailure(string message) => _controller.ReportLoadFailure(message);

	public override void _ExitTree() => _controller = null;

	/// <summary>编辑器依赖校验：页面只校验目录组件与返回按钮。</summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<MapCatalogPanel>(CatalogPath, nameof(CatalogPath), warnings);
		this.Check<Button>(BackButtonPath, nameof(BackButtonPath), warnings);
		return warnings.ToArray();
	}
}
