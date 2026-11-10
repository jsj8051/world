using Godot;
using System;
using World.Data.Generation;
using World.Params.Generation;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Client;
using World.Data;
using World.Params;
using World.Render;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 根管理器（`WorldGenWorld.tscn` 根节点）：场景入口点 + 中介者，管理器树的根。
/// 树：`WorldRoot > 相机（OrbitalCamera）＋ WorldManager（持 Sim）/ RenderManager（WorldView）
/// / UIManager（PanelLayer + 坞 + 信息卡）` ＋ 不进场景树的 WorldParams。
/// 相机由本类持有：它是自驱动的客户端外围（自建 Camera3D、自处理输入与逐帧缩放），
/// 渲染侧的剔除、拾取与初始取景都要用它 ⇒ 归中介者持有并分发，不单独建管理器。
/// 只做四件事：持管理器树（依赖写成 [Export] NodePath）、按序初始化、做中介（管理器互不认识）、
/// 按注册序转发每帧 Tick。不建 EventBus / 不做 DI / 不持有世界数据。
/// </summary>
public partial class WorldRoot : Node3D, IAppPage, IGenerationPage
{
	WorldSpec _requestedSpec;
	Action<Exception> _generationFailure;
	Task<WorldGenerationResult> _generationTask;
	LoadOperation _generationOperation;
	LoadProgressMonitor _generationMonitor;
	bool _renderBuilding;

	void IGenerationPage.ConfigureGeneration(GenerationConfig config, Action<Exception> onFailure)
	{
		string json = WorldParamStore.ReadPresetText(out var ioProblems);
		var defaults = WorldParams.ReadJson(json, out var codecProblems);
		if (defaults == null || ioProblems.Count != 0 || codecProblems.Count != 0)
			throw new InvalidOperationException("默认世界参数无法加载：" + string.Join("；", ioProblems) + string.Join("；", codecProblems));
		_requestedSpec = GenerationConfigMapper.CreateWorldSpec(config, defaults);
		_generationFailure = onFailure;
	}

	[ExportGroup("管理器（NodePath）")]
	[Export] public NodePath WorldManagerPath { get; set; }
	[Export] public NodePath RenderManagerPath { get; set; }
	[Export] public NodePath UIManagerPath { get; set; }
	[Export] public NodePath CameraPath { get; set; }
	/// <summary>地图坞节点路径。坞挂着 MapDock 脚本，本类不引用它的类型
	/// （连接走场景文件的 [connection]、调用走 Node.Call）⇒ 这里只需 PanelContainer 这一层抽象。</summary>
	[Export] public NodePath DockPath { get; set; }

	/// <summary>世界管理器（世界本体：持 <c>Sim</c> 世界事实）。</summary>
	public WorldManager World { get; private set; }

	/// <summary>渲染管理器（3D 世界表现子树的持有者）。</summary>
	public RenderManager Render { get; private set; }

	/// <summary>取景相机（本类持有的"客户端外围"；供拾取器与渲染侧逐帧剔除共用）。</summary>
	public OrbitalCamera Camera { get; private set; }

	/// <summary>界面管理器（界面子树的持有者）。</summary>
	public UIManager UI { get; private set; }

	/// <summary>参数管理器（不进场景树：它有实例、没有节点）。</summary>
	public WorldParams Params { get; private set; }

	/// <summary>每帧推进的子系统（注册序 = 帧内顺序）。</summary>
	public List<ITickable> Ticks { get; } = new();

	/// <summary>地图工具菜单的装配点（实现见 <c>WorldRoot.MapBindings.cs</c>）。</summary>
	partial void OnMenusReady();

	/// <summary>
	/// 唯一的初始化点（幂等）。顺序即依赖序，改顺序只改这里：
	/// <code>
	///   ① 参数管理器：加载 res/params/（世界定义的唯一来源）
	///   ② 世界管理器：生成世界事实（重活在这一步）※ 此时还没有画面
	///   ③ 渲染管理器：把事实装配成画面，并建好拾取器（点选归它）
	///   ④ 界面管理器：用模式表驱动地图坞，并订阅渲染侧的点选事件（要视图已存在）
	/// </code>
	/// ②③④ 的顺序不是风格：世界要先生成事实、表现才能画、渲染侧初始化即写视图取色。
	/// 三棵子树各自独立：世界只认事实，表现自行取活动相机，界面只订阅渲染侧事件并吃 PickedCell
	/// 自足数据 ⇒ 本类只负责按序喂依赖，不替它们互相认识。
	/// </summary>
	public override void _Ready()
	{
		if (_progressSink == null)
		{
			if (_generationFailure == null) { InitializeWorld(); return; }
			try { InitializeWorld(); }
			catch (Exception error)
			{
				Ticks.Clear();
				GD.PushError($"[WORLDROOT] 初始化失败：{error}");
				_generationFailure(error);
			}
			return;
		}

		try { BeginWorldGeneration(); }
		catch (Exception error) { FailGeneration(error); }
	}

	/// <summary>应用内生成：绑定管理器、起后台 Job；生成与装配由 <see cref="_Process"/> 推进。</summary>
	void BeginWorldGeneration()
	{
		World = this.Require<WorldManager>(WorldManagerPath, nameof(WorldManagerPath));
		Render = this.Require<RenderManager>(RenderManagerPath, nameof(RenderManagerPath));
		UI = this.Require<UIManager>(UIManagerPath, nameof(UIManagerPath));
		Camera = this.Require<OrbitalCamera>(CameraPath, nameof(CameraPath));

		Params = new WorldParams();
		if (_requestedSpec != null) _requestedSpec.CopyTo(Params.Active);
		else
		{
			Params.Reload();
			foreach (var p in Params.LoadProblems) GD.PushWarning($"[WORLDGEN-PARAMS] {p}");
		}

		Render.SetDock(this.Require<PanelContainer>(DockPath, nameof(DockPath)));
		_generationOperation = _progressSink.Begin(LoadFlow.WorldGeneration, "生成世界");
		LoadOperation operation = _generationOperation;
		WorldSpec spec = Params.Active;
		int resolution = World.WorldResolution;
		bool evolution = World.EnableEvolution;
		int steps = World.InitialEvolutionSteps;
		CancellationToken token = operation.Token;

		var job = new WorldGenerationJob();
		_generationMonitor = new LoadProgressMonitor(operation, LoadStagePlans.Generation, 0f, 0.70f);
		_generationMonitor.Attach(job);
		_generationTask = Task.Run(() => job.Run(spec, resolution,
			PlanetGeometry.ProductionRadius, evolution, steps, token));
	}

	void PollGeneration()
	{
		if (_generationTask == null || !_generationTask.IsCompleted) return;
		Task<WorldGenerationResult> task = _generationTask;
		_generationTask = null;
		_generationMonitor?.Dispose();
		_generationMonitor = null;

		if (task.IsCanceled) { _generationOperation?.Cancel(); return; }
		if (task.IsFaulted) { FailGeneration(task.Exception); return; }
		try { AdoptGeneratedWorld(task.Result); }
		catch (Exception error) { FailGeneration(error); }
	}

	void AdoptGeneratedWorld(WorldGenerationResult result)
	{
		World.Adopt(result);
		var modes = WorldGenMapModes.CreateAll(World.Sim);
		Render.InitializeIncremental(World.Sim, modes);
		World.WorldPublished += _ => ApplyPublishedWorld();
		Ticks.Add(World);
		Ticks.Add(Render);

		Render.FrameLand(Camera);
		UI.Initialize(Render, World);
		Ticks.Add(UI);
		OnMenusReady();
		_renderBuilding = true;

		GD.Print($"[WORLDROOT] 管理器树就绪：子系统 {Ticks.Count} 个" +
				 $"（世界={World.Name} 渲染={Render.Name} 界面={UI.Name}/{UI.Dock.Name}）");
	}

	void FailGeneration(Exception error)
	{
		Ticks.Clear();
		_renderBuilding = false;
		_generationMonitor?.Dispose();
		_generationMonitor = null;
		Exception root = error is AggregateException aggregate ? aggregate.GetBaseException() : error;
		GD.PushError($"[WORLDROOT] 生成失败：{root}");
		_generationOperation?.Fail(new LoadFailure("GenerationError", "生成世界", root.Message));
		_generationOperation = null;
		_generationFailure?.Invoke(root);
	}

	/// <summary>演化发布新帧：禁用输入 → 重烘画面 → 恢复。当前重烘仍是整段同步。</summary>
	void ApplyPublishedWorld()
	{
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

	void InitializeWorld()
	{
		World = this.Require<WorldManager>(WorldManagerPath, nameof(WorldManagerPath));
		Render = this.Require<RenderManager>(RenderManagerPath, nameof(RenderManagerPath));
		UI = this.Require<UIManager>(UIManagerPath, nameof(UIManagerPath));
		Camera = this.Require<OrbitalCamera>(CameraPath, nameof(CameraPath));

		// ① 参数（不进场景树的管理器：数据归它，节点不归它）
		Params = new WorldParams();
		if (_requestedSpec != null) _requestedSpec.CopyTo(Params.Active);
		else
		{
			Params.Reload();
			foreach (var p in Params.LoadProblems) GD.PushWarning($"[WORLDGEN-PARAMS] {p}");
		}

		// ② 世界（生成事实；表现与点选都不在这步）。演化启用时它每帧推进。
		World.Initialize(Params.Active);
		Ticks.Add(World);

		// ③ 渲染（把事实装配成画面 + 建拾取器 + 驱动地图坞的模式侧）。
		//   模式表由根管理器建一次并交给渲染侧 ⇒ 视图初始取色 = 坞首项高亮（同一份注册序）。
		//   坞的路径先设给它：坞是 UIManager 的子树，坞↔渲染的信号连接在场景文件里
		//   （scenes/core/WorldGenWorld.tscn 的 [connection]）⇒ 两侧互不引用对方的类型。
		//   相机不进这条注入链：渲染侧每帧 / 每次点选都现取活动相机。
		Render.SetDock(this.Require<PanelContainer>(DockPath, nameof(DockPath)));
		var modes = WorldGenMapModes.CreateAll(World.Sim);
		Render.Initialize(World.Sim, modes);
		World.WorldPublished += _ => ApplyPublishedWorld();
		Ticks.Add(Render);

		// 初始取景对准陆地质心（取景属表现侧，由渲染管理器读 FinalLand 后驱动相机）。
		Render.FrameLand(Camera);

		// ④ 界面（坞 + 信息卡）：只订阅渲染侧的点选事件，不碰地图模式（那是渲染侧的事）
		UI.Initialize(Render, World);
		Ticks.Add(UI);

		// 菜单依赖界面与渲染都就绪后再装配按钮。
		OnMenusReady();

		GD.Print($"[WORLDROOT] 管理器树就绪：子系统 {Ticks.Count} 个" +
				 $"（世界={World.Name} 渲染={Render.Name} 界面={UI.Name}/{UI.Dock.Name}）");
	}

	/// <summary>每帧：先轮询生成，再分帧装配首帧，最后按注册序转发 Tick。</summary>
	public override void _Process(double delta)
	{
		PollGeneration();
		if (_renderBuilding && Render != null)
		{
			bool done = Render.StepBuild(8.0);
			_generationOperation?.Report(0.70f + 0.30f * Render.BuildProgress, Render.BuildStage);
			if (done)
			{
				_renderBuilding = false;
				_generationOperation?.Complete();
				_generationOperation = null;
			}
		}
		foreach (var t in Ticks) t.Tick(delta);
	}

	public override void _ExitTree()
	{
		_generationOperation?.Cancel();
		_generationMonitor?.Dispose();
		_generationMonitor = null;
		if (_generationTask != null)
			_ = _generationTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
		World?.PauseEvolution();
	}

	// 输入不由本类转发：点选住在 RenderManager._UnhandledInput，靠 Godot 原生投递顺序
	// （GUI 先消费 ⇒ 没被消费的才到它）。若改成根节点经 _Input 转发，就得自己手写
	// "GUI 会不会消费这个事件"的判断——把引擎已经做对的事再做一遍，且必然不如它准。

	/// <summary>
	/// 编辑器依赖校验（官方自文档化机制）：本类的四条 ＋ 三个子管理器各自校验的汇总
	/// ⇒ 打开根节点就能看全整棵树的接线问题。
	/// </summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<WorldManager>(WorldManagerPath, nameof(WorldManagerPath), warnings);
		this.Check<RenderManager>(RenderManagerPath, nameof(RenderManagerPath), warnings);
		this.Check<UIManager>(UIManagerPath, nameof(UIManagerPath), warnings);
		this.Check<OrbitalCamera>(CameraPath, nameof(CameraPath), warnings);

		// 汇总子管理器的校验（它们各自也实现了 _GetConfigurationWarnings）
		foreach (var node in new Node[]
		{
			GetNodeOrNull(WorldManagerPath), GetNodeOrNull(RenderManagerPath), GetNodeOrNull(UIManagerPath),
		})
		{
			if (node == null) continue;
			foreach (var w in node._GetConfigurationWarnings()) warnings.Add($"{node.Name}: {w}");
		}

		// 取景半径一致性（相机 ∝ 球面几何：不一致时画面错位）。
		// 两侧缺省都取 PlanetGeometry.ProductionRadius 同一个常量 ⇒ 只剩"有人在场景里手改
		// _planetRadius"这一种失真，故这里只留一道廉价守卫（照出来总比静默错位好）。
		var cam = GetNodeOrNull<OrbitalCamera>(CameraPath);
		if (cam != null)
			foreach (var w in WorldManager.RadiusMismatchWarnings(
				cam.PlanetRadius, PlanetGeometry.ProductionRadius))
				warnings.Add(w);

		return warnings.ToArray();
	}
}
