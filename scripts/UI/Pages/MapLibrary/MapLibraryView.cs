using System;
using System.Collections.Generic;
using Godot;
using World.Application.Components;
using World.Application.Storage;
using World.Infrastructure.Maps;
using World.Application.Pages;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>
/// 地图库页面宿主：标题与返回按钮，内容交给 <see cref="MapCatalogPanel"/>。
/// 存档列表、选择与加载意图归 <see cref="MapCatalogController"/>。
/// </summary>
public partial class MapLibraryView : Control, IAppPage
{
	[Export] public NodePath CatalogPath { get; set; }
	[Export] public NodePath BackButtonPath { get; set; }

	IGeographyMapRepository _repository;
	IMapCatalogActions _actions;
	MapCatalogController _controller;
	MapCatalogPanel _catalog;

	void IAppPage.Configure(PageContext context) { }

	void IAppPage.OnHidden() => _controller = null;

	/// <summary>应用装配注入存档库；独立运行时回退到游戏目录默认实现。</summary>
	internal void SetRepository(IGeographyMapRepository repository)
		=> _repository = repository ?? throw new ArgumentNullException(nameof(repository));

	/// <summary>应用装配注入宿主动作；独立运行时为空，页面只读不跳转。</summary>
	internal void SetActions(IMapCatalogActions actions) => _actions = actions;

	public override void _Ready()
	{
		_catalog = this.Require<MapCatalogPanel>(CatalogPath, nameof(CatalogPath));
		var back = this.Require<Button>(BackButtonPath, nameof(BackButtonPath));

		_controller = new MapCatalogController(_repository ?? GameMapRepository.CreateDefault(), _actions);
		back.Pressed += () => _controller.BackToMainMenu();
		_catalog.Bind(_controller);
		_controller.Refresh();
	}

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
