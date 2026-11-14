using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using World.Application.Loading;
using World.Application.Pages;
using World.Application.Storage;
using World.Client;
using World.Data;
using World.Data.Generation;
using World.Data.Maps;
using World.Infrastructure.Maps;
using World.Logic;
using World.Params;
using World.Params.Generation;
using World.Render;
using World.Simulation;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>
/// 地理演化页面根（`GeographyEvolutionPage.tscn`）：世界表现子树的装配点、生成生命周期与页面 UI。
/// 只负责按序初始化 <see cref="WorldManager"/> / <see cref="RenderManager"/> / <see cref="UIManager"/>，
/// 转发每帧 Tick，把页面交互交给 <see cref="GeographyEvolutionController"/>；
/// 演化命令、保存/导出与返回不在此类里实现。
/// </summary>
public partial class GeographyEvolutionView : Node3D, IAppPage
{
	[ExportGroup("管理器（NodePath）")]
	[Export] public NodePath WorldManagerPath { get; set; }
	[Export] public NodePath RenderManagerPath { get; set; }
	[Export] public NodePath UIManagerPath { get; set; }
	[Export] public NodePath CameraPath { get; set; }
	[Export] public NodePath DockPath { get; set; }

	/// <summary>共享应用菜单（左上按钮 + 居中面板）。</summary>
	[Export] public NodePath AppMenuPath { get; set; }

	/// <summary>演化控制面板（页面场景必须提供）。</summary>
	[Export] public NodePath EvolutionPanelPath { get; set; }

	WorldGenerationResult _generatedWorld;
	LoadOperation _buildOperation;
	LoadOperationBinding _buildBinding;
	Action<Exception> _buildFailure;
	IGeographyMapRepository _mapRepository = GameMapRepository.CreateDefault();
	PageContext _pageContext;
	IMainMenuReturn _mainMenu;
	bool _renderBuilding;

	AppMenu _menu;
	GeographyEvolutionController _controller;

	readonly List<ITickable> _ticks = new();

	/// <summary>世界管理器（世界本体：持 <c>Sim</c> 世界事实）。</summary>
	public WorldManager World { get; private set; }

	/// <summary>渲染管理器（3D 世界表现子树的持有者）。</summary>
	public RenderManager Render { get; private set; }

	/// <summary>取景相机（拾取与渲染剔除共用）。</summary>
	public OrbitalCamera Camera { get; private set; }

	/// <summary>界面管理器（坞、信息卡与面板层的持有者）。</summary>
	public UIManager UI { get; private set; }

	/// <summary>参数管理器（不进场景树）。</summary>
	public WorldParams Params { get; private set; }

	/// <summary>演化读数面板（仅世界含演化时存在）。</summary>
	internal EvolutionPanel EvolutionReadout { get; private set; }

	void IAppPage.Configure(PageContext context)
	{
		_pageContext = context;
		if (_buildOperation != null && context != null)
			_buildBinding = new LoadOperationBinding(_buildOperation, context.PageId, context.Lifetime);
	}

	/// <summary>入树前注入已生成的世界、仍在进行的加载操作与装配失败回调；装配完成由本页收尾。</summary>
	internal void SetGeneratedWorld(WorldGenerationResult result, LoadOperation operation,
		Action<Exception> onBuildFailure)
	{
		_generatedWorld = result ?? throw new ArgumentNullException(nameof(result));
		_buildOperation = operation ?? throw new ArgumentNullException(nameof(operation));
		_buildFailure = onBuildFailure;
	}

	/// <summary>应用装配在入树前注入地图存档库；独立运行时保留游戏目录默认实现。</summary>
	internal void SetMapRepository(IGeographyMapRepository repository)
		=> _mapRepository = repository ?? throw new ArgumentNullException(nameof(repository));

	/// <summary>应用装配在入树前注入返回主界面的端口；独立运行时为空，菜单项不跳转。</summary>
	internal void SetMainMenuReturn(IMainMenuReturn mainMenu) => _mainMenu = mainMenu;

	/// <summary>
	/// 初始化顺序即依赖序：参数 → 世界事实 → 渲染表现 → 页面 UI。
	/// 应用内由宿主注入已生成的世界（分帧装配），独立运行走同步路径。
	/// </summary>
	public override void _Ready()
	{
		if (_generatedWorld == null)
		{
			try { InitializeWorld(); }
			catch (Exception error) { GD.PushError($"[TERRAIN-EVOLUTION] 初始化失败：{error}"); }
			return;
		}

		try { AdoptGeneratedWorld(_generatedWorld); }
		catch (Exception error) { FailBuild(error); }
	}

	void InitializeWorld()
	{
		BindManagers();
		LoadParams();

		World.Initialize(Params.Active, _mapRepository);
		_ticks.Add(World);

		Render.SetDock(this.Require<PanelContainer>(DockPath, nameof(DockPath)));
		var modes = WorldGenMapModes.CreateAll(World.Sim);
		Render.Initialize(World.Sim, modes);
		World.WorldPublished += OnWorldPublished;
		_ticks.Add(Render);
		Render.FrameLand(Camera);

		UI.Initialize(Render, World.Sim);
		OnUiReady();

		GD.Print($"[TERRAIN-EVOLUTION] 管理器树就绪：子系统 {_ticks.Count} 个" +
				 $"（世界={World.Name} 渲染={Render.Name} 界面={UI.Name}/{UI.Dock.Name}）");
	}

	void BindManagers()
	{
		World = this.Require<WorldManager>(WorldManagerPath, nameof(WorldManagerPath));
		Render = this.Require<RenderManager>(RenderManagerPath, nameof(RenderManagerPath));
		UI = this.Require<UIManager>(UIManagerPath, nameof(UIManagerPath));
		Camera = this.Require<OrbitalCamera>(CameraPath, nameof(CameraPath));
	}

	void LoadParams()
	{
		Params = new WorldParams();
		Params.Reload();
		foreach (var problem in Params.LoadProblems) GD.PushWarning($"[WORLDGEN-PARAMS] {problem}");
	}

	/// <summary>接管宿主生成好的世界：先绑管理器与参数，再把画面装配交给分帧 <see cref="_Process"/>。</summary>
	void AdoptGeneratedWorld(WorldGenerationResult result)
	{
		BindManagers();
		Params = new WorldParams();
		result.Spec.CopyTo(Params.Active);
		World.SetMapRepository(_mapRepository);
		Render.SetDock(this.Require<PanelContainer>(DockPath, nameof(DockPath)));

		World.Adopt(result);
		var modes = WorldGenMapModes.CreateAll(World.Sim);
		Render.InitializeIncremental(World.Sim, modes);
		World.WorldPublished += OnWorldPublished;
		_ticks.Add(World);
		_ticks.Add(Render);

		Render.FrameLand(Camera);
		UI.Initialize(Render, World.Sim);
		OnUiReady();
		_renderBuilding = true;

		GD.Print($"[TERRAIN-EVOLUTION] 管理器树就绪：子系统 {_ticks.Count} 个" +
				 $"（世界={World.Name} 渲染={Render.Name} 界面={UI.Name}/{UI.Dock.Name}）");
	}

	void FailBuild(Exception error)
	{
		_ticks.Clear();
		_renderBuilding = false;
		Exception root = error is AggregateException aggregate ? aggregate.GetBaseException() : error;
		GD.PushError($"[TERRAIN-EVOLUTION] 装配失败：{root}");
		// 操作由协调器拥有，页面只负责置终态并交回失败原因。
		_buildOperation?.Fail(new LoadFailure("GenerationError", "画面装配", root.Message));
		_buildOperation = null;
		_buildBinding = null;
		_buildFailure?.Invoke(root);
	}

	/// <summary>演化发布新帧：刷新面板读数，再重烘画面。</summary>
	void OnWorldPublished(WorldFacts facts)
	{
		EvolutionReadout?.Refresh(facts);
		if (_renderBuilding) return;
		ulong started = Time.GetTicksMsec();
		Render.BeginSwap();
		try { Render.RebakeView(); }
		finally
		{
			Render.EndSwap();
			GD.Print($"[SWAP] rebake-ms={Time.GetTicksMsec() - started}");
		}
	}

	/// <summary>每帧：分帧装配首帧，然后按注册序转发 Tick 并刷新页面状态。</summary>
	public override void _Process(double delta)
	{
		if (_renderBuilding && Render != null)
		{
			if (!LoadCommitGuard.CanCommit(_buildBinding, _pageContext?.PageId ?? Guid.Empty))
			{
				_renderBuilding = false;
			}
			else
			{
				bool done = Render.StepBuild(8.0);
				_buildOperation?.Report(0.70f + 0.30f * Render.BuildProgress, Render.BuildStage);
				if (done)
				{
					_renderBuilding = false;
					_buildOperation?.Complete();
					_buildOperation = null;
					_buildBinding = null;
				}
			}
		}
		foreach (var tick in _ticks) tick.Tick(delta);
		TickUi(delta);
	}

	public override void _ExitTree()
	{
		_buildBinding = null;
		_buildOperation?.Cancel();
		World?.PauseEvolution();
	}

	void IAppPage.OnHidden()
	{
		OnUiHidden();
		if (World != null) World.WorldPublished -= OnWorldPublished;
		_buildBinding = null;
		_buildOperation?.Cancel();
		_renderBuilding = false;
		_ticks.Clear();
		World?.PauseEvolution();
	}

	void OnUiReady()
	{
		_controller = new GeographyEvolutionController(World,
			new GeographyEvolutionPageActions(World.SaveMap, Render.ExportTerrainMapNow,
				_mainMenu, _pageContext?.Notice));
		_controller.StateChanged += RefreshEvolutionUi;

		BindAppMenu();
		ResolveEvolutionPanel();
		BindEvolutionPanel();
		RefreshEvolutionUi();
	}

	void TickUi(double delta)
	{
		if (_controller == null) return;
		_controller.Refresh();
		EvolutionReadout?.SetEvolutionSignals(World?.EvolutionSource, World?.EvolutionBusy == true);
	}

	void OnUiHidden()
	{
		if (_controller != null) _controller.StateChanged -= RefreshEvolutionUi;
		_controller = null;

		if (EvolutionReadout != null)
		{
			EvolutionReadout.PlayPressed -= OnPlayPressed;
			EvolutionReadout.PausePressed -= OnPausePressed;
			EvolutionReadout.StepPressed -= OnStepPressed;
			EvolutionReadout.RecoverPressed -= OnRecoverPressed;
			EvolutionReadout.SeekRequested -= OnSeekRequested;
			EvolutionReadout.SpeedChanged -= OnSpeedChanged;
			EvolutionReadout = null;
		}

		if (_menu != null) _menu.ItemPressed -= OnMenuItemPressed;
		_menu = null;
	}

	void BindAppMenu()
	{
		if (AppMenuPath == null || AppMenuPath.IsEmpty) return;
		_menu = GetNodeOrNull<AppMenu>(AppMenuPath);
		if (_menu == null) return;
		_menu.Configure("菜单",
			new AppMenuItem("save_map", "保存地图"),
			new AppMenuItem("export_image", "导出图片"),
			new AppMenuItem("back", "返回主界面"));
		_menu.ItemPressed += OnMenuItemPressed;
	}

	void OnMenuItemPressed(string id)
	{
		switch (id)
		{
			case "save_map": _controller?.SaveMap(); break;
			case "export_image": _controller?.ExportMapImage(); break;
			case "back": _controller?.BackToMainMenu(); break;
		}
	}

	void ResolveEvolutionPanel()
	{
		EvolutionReadout = this.Require<EvolutionPanel>(EvolutionPanelPath, nameof(EvolutionPanelPath));
		if (World?.Sim?.Facts?.Evolution == null)
		{
			EvolutionReadout.Hide();
			return;
		}
		EvolutionReadout.Show();
		EvolutionReadout.Refresh(World.Sim.Facts);
	}

	void BindEvolutionPanel()
	{
		if (EvolutionReadout == null || _controller == null) return;
		EvolutionReadout.PlayPressed += OnPlayPressed;
		EvolutionReadout.PausePressed += OnPausePressed;
		EvolutionReadout.StepPressed += OnStepPressed;
		EvolutionReadout.RecoverPressed += OnRecoverPressed;
		EvolutionReadout.SeekRequested += OnSeekRequested;
		EvolutionReadout.SpeedChanged += OnSpeedChanged;
	}

	void OnPlayPressed() => _controller?.Play();
	void OnPausePressed() => _controller?.Pause();
	void OnStepPressed() => _controller?.Step();
	void OnRecoverPressed() => _controller?.Recover();
	void OnSeekRequested(double targetMa) => _controller?.Seek(targetMa);
	void OnSpeedChanged(double yearsPerSecond) => _controller?.SetSpeed(yearsPerSecond);

	void RefreshEvolutionUi()
	{
		if (_controller == null || EvolutionReadout == null) return;
		EvolutionReadout.SetStatus(_controller.Status, _controller.IsBusy);
		EvolutionReadout.SetFeedback(_controller.Feedback, _controller.FeedbackIsError);
	}

	/// <summary>编辑器依赖校验：本类的路径 ＋ 三个子管理器各自的校验汇总。</summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<WorldManager>(WorldManagerPath, nameof(WorldManagerPath), warnings);
		this.Check<RenderManager>(RenderManagerPath, nameof(RenderManagerPath), warnings);
		this.Check<UIManager>(UIManagerPath, nameof(UIManagerPath), warnings);
		this.Check<OrbitalCamera>(CameraPath, nameof(CameraPath), warnings);
		this.Check<EvolutionPanel>(EvolutionPanelPath, nameof(EvolutionPanelPath), warnings);
		if (AppMenuPath != null && !AppMenuPath.IsEmpty)
			this.Check<AppMenu>(AppMenuPath, nameof(AppMenuPath), warnings);

		foreach (var node in new Node[]
		{
			GetNodeOrNull(WorldManagerPath), GetNodeOrNull(RenderManagerPath), GetNodeOrNull(UIManagerPath),
		})
		{
			if (node == null) continue;
			foreach (var warning in node._GetConfigurationWarnings()) warnings.Add($"{node.Name}: {warning}");
		}

		var camera = GetNodeOrNull<OrbitalCamera>(CameraPath);
		if (camera != null)
			foreach (var warning in WorldManager.RadiusMismatchWarnings(
				camera.PlanetRadius, PlanetGeometry.ProductionRadius))
				warnings.Add(warning);

		return warnings.ToArray();
	}
}
