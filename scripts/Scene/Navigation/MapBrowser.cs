using Godot;
using World.Client;
using World.Data.Maps;
using World.Logic;
using World.Render;
using World.UI;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 只读地图浏览：用导入结果装配画面与拾取，不创建生成器、演化会话或导出按钮。
/// 页面工厂在入树前注入已校验事实，页面宿主再注入生命周期上下文。
/// </summary>
public partial class MapBrowser : Node3D, IAppPage
{
	/// <summary>取景相机（场景内实例）。</summary>
	[Export] public NodePath CameraPath { get; set; }

	/// <summary>渲染管理器。</summary>
	[Export] public NodePath RenderManagerPath { get; set; }

	/// <summary>界面管理器。</summary>
	[Export] public NodePath UIManagerPath { get; set; }

	/// <summary>地图坞（渲染侧模式按钮的宿主）。</summary>
	[Export] public NodePath DockPath { get; set; }

	/// <summary>返回地图库按钮。</summary>
	[Export] public NodePath BackButtonPath { get; set; }

	/// <summary>地图名称标签。</summary>
	[Export] public NodePath TitleLabelPath { get; set; }

	/// <summary>共享应用菜单。</summary>
	[Export] public NodePath AppMenuPath { get; set; }

	ImportedWorldFactsSource _source;
	IPageNavigator _nav;
	bool _initialized;
	bool _building;
	LoadOperation _buildOperation;
	LoadOperationBinding _buildBinding;
	PageContext _pageContext;

	/// <summary>注入已校验的导入事实；必须在加入场景树之前调用。</summary>
	internal void SetSource(ImportedWorldFactsSource source) => _source = source;

	/// <summary>注入页面加载操作；渲染分帧完成后由本页完成。</summary>
	internal void SetBuildOperation(LoadOperation operation) => _buildOperation = operation;

	void IAppPage.Configure(PageContext context)
	{
		_pageContext = context;
		_nav = context?.Navigation;
		if (_buildOperation != null && context != null)
			_buildBinding = new LoadOperationBinding(_buildOperation, context.PageId, context.Lifetime);
	}

	void IAppPage.OnHidden()
	{
		_initialized = false;
		_building = false;
		_buildBinding = null;
		_buildOperation?.Cancel();
	}

	public override void _Ready()
	{
		var camera = this.Require<OrbitalCamera>(CameraPath, nameof(CameraPath));
		var render = this.Require<RenderManager>(RenderManagerPath, nameof(RenderManagerPath));
		var ui = this.Require<UIManager>(UIManagerPath, nameof(UIManagerPath));
		var dock = this.Require<PanelContainer>(DockPath, nameof(DockPath));
		var title = this.Require<Label>(TitleLabelPath, nameof(TitleLabelPath));
		var menu = this.Require<AppMenu>(AppMenuPath, nameof(AppMenuPath));
		menu.Configure("浏览地图", new AppMenuItem("back", "返回地图库"));
		menu.ItemPressed += id =>
		{
			if (id == "back") _nav?.Navigate(new MapLibraryRequest());
		};

		if (_source == null)
		{
			title.Text = "没有地图数据";
			return;
		}

		title.Text = _source.Snapshot.Manifest.Title;
		render.SetDock(dock);
		var modes = WorldGenMapModes.CreateAll(_source);
		render.InitializeIncremental(_source, modes);
		render.FrameLand(camera);
		ui.Initialize(render, null, _source);
		_building = true;
		_initialized = true;
		GD.Print($"[MAP] 已浏览导入地图：{_source.Snapshot.Manifest.Title} " +
			$"cells={_source.Facts.Ball.CellIds.Length}");
	}

	public override void _Process(double delta)
	{
		var render = GetNodeOrNull<RenderManager>(RenderManagerPath);
		if (render == null) return;
		if (_building)
		{
			if (_buildOperation != null && !LoadCommitGuard.CanCommit(_buildBinding?.Key ?? default,
				_buildBinding, _pageContext?.PageId ?? System.Guid.Empty))
			{
				_building = false;
				return;
			}
			bool done = render.StepBuild(8.0);
			_buildOperation?.Report(0.75f + 0.25f * render.BuildProgress, render.BuildStage);
			if (done)
			{
				_building = false;
				_buildOperation?.Complete();
			}
		}
		render.Tick(delta);
	}

	public override void _ExitTree()
	{
		if (_building) _buildOperation?.Cancel();
		if (_initialized) GD.Print("[MAP] 关闭导入地图浏览");
	}
}