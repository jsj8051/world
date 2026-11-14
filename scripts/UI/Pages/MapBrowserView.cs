using Godot;
using World.Application.Loading;
using World.Application.Pages;
using World.Client;
using World.Data.Maps;
using World.Logic;
using World.Render;
using World.Scene;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>
/// 地图浏览页：用导入结果装配只读画面、分帧构建并驱动相机；不创建生成器或演化会话。
/// 页面信息与返回意图交给控制器，渲染装配与构建生命周期留在这里。
/// </summary>
public partial class MapBrowserView : Node3D, IAppPage
{
	[Export] public NodePath CameraPath { get; set; }
	[Export] public NodePath RenderManagerPath { get; set; }
	[Export] public NodePath UIManagerPath { get; set; }
	[Export] public NodePath DockPath { get; set; }
	[Export] public NodePath TitleLabelPath { get; set; }
	[Export] public NodePath AppMenuPath { get; set; }

	ImportedWorldFactsSource _source;
	LoadOperation _buildOperation;
	LoadOperationBinding _buildBinding;
	PageContext _pageContext;
	IMapBrowserPageActions _actions;
	MapBrowserController _controller;
	RenderManager _render;
	bool _initialized;
	bool _building;

	/// <summary>注入已校验的导入事实；必须在加入场景树之前调用。</summary>
	internal void SetSource(ImportedWorldFactsSource source) => _source = source;

	/// <summary>注入页面加载操作；渲染分帧完成后由本页完成。</summary>
	internal void SetBuildOperation(LoadOperation operation) => _buildOperation = operation;

	/// <summary>注入宿主动作；独立运行时为空，浏览页只读不返回。</summary>
	internal void SetActions(IMapBrowserPageActions actions) => _actions = actions;

	void IAppPage.Configure(PageContext context)
	{
		_pageContext = context;
		if (_buildOperation != null && context != null)
			_buildBinding = new LoadOperationBinding(_buildOperation, context.PageId, context.Lifetime);
	}

	void IAppPage.OnHidden()
	{
		_initialized = false;
		_building = false;
		_buildBinding = null;
		_buildOperation?.Cancel();
		_controller = null;
	}

	public override void _Ready()
	{
		var camera = this.Require<OrbitalCamera>(CameraPath, nameof(CameraPath));
		_render = this.Require<RenderManager>(RenderManagerPath, nameof(RenderManagerPath));
		var ui = this.Require<UIManager>(UIManagerPath, nameof(UIManagerPath));
		var dock = this.Require<PanelContainer>(DockPath, nameof(DockPath));
		var title = this.Require<Label>(TitleLabelPath, nameof(TitleLabelPath));
		var menu = this.Require<AppMenu>(AppMenuPath, nameof(AppMenuPath));

		IMapBrowserPageActions actions = _actions;
		MapBrowserPageInfo info = _source == null
			? new MapBrowserPageInfo("没有地图数据", 0, false)
			: new MapBrowserPageInfo(_source.Snapshot.Manifest.Title,
				_source.Facts.Ball.CellIds.Length, true);
		_controller = new MapBrowserController(info, actions);

		menu.Configure("浏览地图", new AppMenuItem("back", "返回地图库"));
		menu.ItemPressed += id =>
		{
			if (id == "back") _controller?.BackToMapLibrary();
		};
		title.Text = _controller.Title;
		if (!_controller.HasMap) return;

		_render.SetDock(dock);
		var modes = WorldGenMapModes.CreateAll(_source);
		_render.InitializeIncremental(_source, modes);
		_render.FrameLand(camera);
		ui.Initialize(_render, _source);
		_building = true;
		_initialized = true;
		GD.Print($"[MAP] 已浏览导入地图：{_controller.Title} cells={_controller.CellCount}");
	}

	public override void _Process(double delta)
	{
		if (!_initialized || _render == null) return;
		if (_building)
		{
			if (_buildOperation != null && !LoadCommitGuard.CanCommit(_buildBinding,
				_pageContext?.PageId ?? System.Guid.Empty))
			{
				_building = false;
				return;
			}
			bool done = _render.StepBuild(8.0);
			_buildOperation?.Report(0.75f + 0.25f * _render.BuildProgress, _render.BuildStage);
			if (done)
			{
				_building = false;
				_buildOperation?.Complete();
			}
		}
		_render.Tick(delta);
	}

	public override void _ExitTree()
	{
		if (_building) _buildOperation?.Cancel();
		if (_initialized) GD.Print("[MAP] 关闭导入地图浏览");
	}
}
