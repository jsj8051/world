using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using World.Application.Geography;
using World.Application.Loading;
using World.Application.Export;
using World.Infrastructure.Export;
using World.Application.Pages;
using World.Application.Storage;
using World.Application.World;
using World.Data;
using World.Data.Generation;
using World.Data.Maps;
using World.Infrastructure.Maps;
using World.Logic;
using World.Params;
using World.Params.Generation;
using World.Render;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>
/// 地理演化页面根（`GeographyEvolutionPage.tscn`）：世界表现子树的装配点、生成生命周期与页面 UI。
/// 初始化 <see cref="WorldSession"/>、<see cref="WorldPresentation"/> 和页面组件，
/// 转发每帧 Tick，把页面交互交给 <see cref="GeographyEvolutionController"/>；
/// 演化命令、保存/导出与返回不在此类里实现。
/// </summary>
public partial class GeographyEvolutionView : Node3D, IMainMenuReturn
{
	[ExportGroup("页面依赖（NodePath）")]
	[Export] public NodePath MapViewerPath { get; set; }

	/// <summary>共享应用菜单（左上按钮 + 居中面板）。</summary>
	[Export] public NodePath AppMenuPath { get; set; }
	[Export] public NodePath AppStatusBarPath { get; set; }

	/// <summary>演化控制面板（页面场景必须提供）。</summary>
	[Export] public NodePath EvolutionPanelPath { get; set; }

	WorldGenerationResult _generatedWorld;
	Action<Exception> _buildFailure;
	FileSaveComponent<GeographySnapshot> _mapSaver = new(new MapPackageFileStorage(), global::World.Infrastructure.GamePaths.GameDirectory);
	IMainMenuReturn _mainMenu;
	bool _renderBuilding;

	MapViewer _viewer;
	TerrainMapExport _mapExport;
	AppMenu _menu;
	GeographyEvolutionController _controller;

	readonly List<ITickable> _ticks = new();

	/// <summary>页面拥有的世界会话（世界本体：持 <c>Sim</c> 世界事实）。</summary>
	public WorldSession World { get; private set; }

	/// <summary>渲染管理器（3D 世界表现子树的持有者）。</summary>
	public WorldPresentation Render { get; private set; }

	/// <summary>参数管理器（不进场景树）。</summary>
	public WorldParams Params { get; private set; }

	/// <summary>演化读数面板（仅世界含演化时存在）。</summary>
	internal EvolutionPanel EvolutionReadout { get; private set; }

	internal static GeographyEvolutionView Create(WorldGenerationResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		var page = ResourceLoader.Load<PackedScene>("res://scenes/pages/GeographyEvolution/GeographyEvolutionPage.tscn")
			.Instantiate<GeographyEvolutionView>();
		page.SetGeneratedWorld(result, error => page.ReturnToConfiguration(error));
		return page;
	}

	void IMainMenuReturn.BackToMainMenu()
		=> SceneReplacement.Replace(this, MainMenuView.Create());

	void ReturnToConfiguration(Exception error)
	{
		var page = GenerationConfigView.Create();
		page.ReportGenerationFailure(error);
		SceneReplacement.Replace(this, page);
	}

	/// <summary>入树前注入已生成的世界与装配失败回调；装配完成由本页收尾。</summary>
	internal void SetGeneratedWorld(WorldGenerationResult result, Action<Exception> onBuildFailure)
	{
		_generatedWorld = result ?? throw new ArgumentNullException(nameof(result));
		_buildFailure = onBuildFailure;
	}

	internal void SetMapSaver(FileSaveComponent<GeographySnapshot> saver)
		=> _mapSaver = saver ?? throw new ArgumentNullException(nameof(saver));

	/// <summary>应用装配在入树前注入返回主界面的端口；独立运行时为空，菜单项不跳转。</summary>
	internal void SetMainMenuReturn(IMainMenuReturn mainMenu) => _mainMenu = mainMenu;

	/// <summary>
	/// 初始化顺序即依赖序：参数 → 世界事实 → 渲染表现 → 页面 UI。
	/// 应用内由宿主注入已生成的世界（分帧装配），独立运行走同步路径。
	/// </summary>
	public override void _Ready()
	{
		_mainMenu ??= this;
		World = new WorldSession(
			message => GD.Print(message),
			error => GD.PushError($"[EVOLVE] 推进失败：{error.Message}"));
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

		World.Initialize(Params.Active);
		_ticks.Add(World);

		_viewer.Configure(GeographyMapViewerContent.Create(World.Sim));
		while (!Render.StepBuild(1000)) { }
		_viewer.SetActive(true);
		World.WorldPublished += OnWorldPublished;
		_ticks.Add(Render);

		OnUiReady();

		GD.Print($"[TERRAIN-EVOLUTION] 页面会话就绪：子系统 {_ticks.Count} 个" +
				 $"（世界会话={nameof(WorldSession)} 渲染={Render.Name}）");
	}

	void BindManagers()
	{
		_viewer = this.Require<MapViewer>(MapViewerPath, nameof(MapViewerPath));
		Render = _viewer.Presentation;
	}

	void LoadParams()
	{
		Params = new WorldParams();
		Params.Reload();
		foreach (var problem in Params.LoadProblems) GD.PushWarning($"[WORLDGEN-PARAMS] {problem}");
	}

	/// <summary>接管宿主生成好的世界：先绑管理器与参数，再把画面装配交给分帧 <see cref="_Process"/>。</summary>
	MapLoadingView _assemblyLoading;

	void AdoptGeneratedWorld(WorldGenerationResult result)
	{
		_assemblyLoading = MapLoadingView.Create();
		AddChild(_assemblyLoading);
		_assemblyLoading.Begin();
		_assemblyLoading.ShowProgress(0, "正在装配地图");
		BindManagers();
		Params = new WorldParams();
		result.Spec.CopyTo(Params.Active);

		World.Adopt(result);
		_viewer.Configure(GeographyMapViewerContent.Create(World.Sim));
		_viewer.SetActive(true);
		World.WorldPublished += OnWorldPublished;
		_ticks.Add(World);
		_ticks.Add(Render);

		OnUiReady();
		_renderBuilding = true;

		GD.Print($"[TERRAIN-EVOLUTION] 页面会话就绪：子系统 {_ticks.Count} 个" +
				 $"（世界会话={nameof(WorldSession)} 渲染={Render.Name}）");
	}

	void FailBuild(Exception error)
	{
		_ticks.Clear();
		_renderBuilding = false;
		Exception root = error is AggregateException aggregate ? aggregate.GetBaseException() : error;
		GD.PushError($"[TERRAIN-EVOLUTION] 装配失败：{root}");
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
			bool done = Render.StepBuild(8.0);
			if (done)
			{
				_renderBuilding = false;
				_assemblyLoading?.QueueFree();
				_assemblyLoading = null;
			}
		}
		foreach (var tick in _ticks) tick.Tick(delta);
		TickUi(delta);
	}

	public override void _ExitTree()
	{
		OnUiHidden();
		if (World != null) World.WorldPublished -= OnWorldPublished;
		_renderBuilding = false;
		_ticks.Clear();
		World?.Dispose();
		World = null;
	}

	internal string SaveMap()
	{
		WorldFacts facts = World?.Sim?.Facts ?? throw new InvalidOperationException("世界尚未生成");
		var snapshot = GeographySnapshotCapture.Capture(facts, Guid.NewGuid(),
			$"世界 {DateTime.Now:yyyy-MM-dd HH:mm}", string.Empty, DateTimeOffset.UtcNow, "0.5.0");
		string relativePath = System.IO.Path.Combine("userdata", "saves", $"map_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wmap");
		return _mapSaver.Save(snapshot, relativePath);
	}

	void OnUiReady()
	{
		_mapExport = new TerrainMapExport(World.Sim, Render.Modes.Modes, new PngMapImageWriter());
		var notice = this.Require<AppStatusBar>(AppStatusBarPath, nameof(AppStatusBarPath));
		_controller = new GeographyEvolutionController(World,
			new GeographyEvolutionPageActions(SaveMap, _mapExport.Export,
				_mainMenu, notice.Notify));
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
	}

	public override void _UnhandledInput(InputEvent input)
	{
		if (_mapExport == null || Render.SwapActive || !MapExportShortcut.Matches(input)) return;
		_controller?.ExportMapImage();
		GetViewport().SetInputAsHandled();
	}

	void OnUiHidden()
	{
		_viewer?.ReleaseBindings();
		_mapExport = null;
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
		if (World?.SculptingRoundIndex == null)
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
	void OnSeekRequested(long targetRound) => _controller?.Seek(targetRound);
	void OnSpeedChanged(double roundsPerSecond) => _controller?.SetSpeed(roundsPerSecond);

	void RefreshEvolutionUi()
	{
		if (_controller == null || EvolutionReadout == null) return;
		EvolutionReadout.SetStatus(_controller.Status, _controller.IsBusy);
		EvolutionReadout.SetFeedback(_controller.Feedback, _controller.FeedbackIsError);
	}

	/// <summary>校验页面依赖并汇总世界和渲染节点的配置警告。</summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<MapViewer>(MapViewerPath, nameof(MapViewerPath), warnings);
		this.Check<EvolutionPanel>(EvolutionPanelPath, nameof(EvolutionPanelPath), warnings);
		this.Check<AppStatusBar>(AppStatusBarPath, nameof(AppStatusBarPath), warnings);
		if (AppMenuPath != null && !AppMenuPath.IsEmpty)
			this.Check<AppMenu>(AppMenuPath, nameof(AppMenuPath), warnings);

		foreach (var node in new Node[]
		{
			GetNodeOrNull(MapViewerPath),
		})
		{
			if (node == null) continue;
			foreach (var warning in node._GetConfigurationWarnings()) warnings.Add($"{node.Name}: {warning}");
		}

		return warnings.ToArray();
	}
}
