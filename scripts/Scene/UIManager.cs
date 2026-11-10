using Godot;
using System.Collections.Generic;
using World.Logic;
using World.Utils;                 // NodeDependency（Require / Check 扩展）
using World.UI;             // MapDock / CellInfoCard（哑组件）
using World.Data;                  // PickedCell / ElevationBands（载体与词表）

namespace World.Scene;

/// <summary>
/// **界面管理器**（`WorldRoot > UIManager`）：界面这一块的持有者。
/// 持有 `PanelLayer`（所有面板的容器）＋ `MapDock`（坞）＋ `CellInfoCard`（信息卡）
/// ⇒ 加面板挂 `PanelLayer` 下，换整套界面只动这一个子树。
/// <para>★**不碰地图模式**：模式表 / 当前模式 / 坞的按钮与参数行同步全归渲染侧
/// （`RenderManager`）。坞→渲染的信号连接写在**场景文件**里
/// （`scenes/core/WorldGenWorld.tscn` 的 `[connection]`）⇒ 两侧代码互不 import。</para>
/// <para>信息卡订阅渲染侧的 `CellPicked` 事件；名称类文案由本层自己取——**词表在世界侧**
/// （`ElevationBands`）⇒ 界面层不必向表现层要任何文案。</para>
/// </summary>
public partial class UIManager : Node3D, ITickable
{
	const string EvolutionPanelScene = "res://scenes/render/EvolutionPanel.tscn";

	[Export] public NodePath PanelLayerPath { get; set; }
	[Export] public NodePath DockPath { get; set; }
	[Export] public NodePath CellCardPath { get; set; }

	/// <summary>面板层（所有面板的容器；本管理器持有它）。</summary>
	public CanvasLayer PanelLayer { get; private set; }

	/// <summary>地图坞（哑组件；按钮 / 参数行 / 图例由它自己画）。</summary>
	public MapDock Dock { get; private set; }

	/// <summary>格信息卡（哑组件；本管理器订阅渲染侧点选事件后写它）。</summary>
	public CellInfoCard CellCard { get; private set; }

	/// <summary>演化控制面板（仅演化场景创建；静态主场景为 null）。</summary>
	public GeologyPanel Geology { get; private set; }

	WorldManager _world;
	IWorldFactsSource _source;

	/// <summary>
	/// **初始化**（由根管理器按序调用，幂等）：
	/// ① 解析自己持有的面板层与两个哑组件；② **订阅渲染侧的点选事件**——信息卡由界面自己写；
	/// ③ 若世界启用了演化，创建控制面板并订阅提交事件。
	/// ★必须在**渲染管理器初始化之后**调用（订阅的是它的事件）。
	/// </summary>
	public void Initialize(RenderManager render, WorldManager world, IWorldFactsSource source = null)
	{
		if (CellCard != null) return;   // 幂等
		_world = world;
		_source = source ?? world?.Sim;

		PanelLayer = this.Require<CanvasLayer>(PanelLayerPath, nameof(PanelLayerPath));
		Dock = this.Require<MapDock>(DockPath, nameof(DockPath));
		CellCard = this.Require<CellInfoCard>(CellCardPath, nameof(CellCardPath));

		render.CellPicked += picked => OnCellPicked(picked, render.ShowCrustDetails);

		Geology = PanelLayer.GetNodeOrNull<GeologyPanel>("GeologyPanel");
		if (world == null) return;
		if (Geology != null) BindGeologyPanel();
		world.WorldPublished += RefreshGeology;
		RefreshGeology(world.Sim.Facts);
	}

	void RefreshGeology(WorldFacts facts)
	{
		if (facts.Evolution == null)
		{
			Geology?.Hide();
			return;
		}
		if (Geology == null)
		{
			var packed = ResourceLoader.Load<PackedScene>(EvolutionPanelScene);
			if (packed == null)
			{
				GD.PushError($"[UI] 演化面板场景缺失：{EvolutionPanelScene}");
				return;
			}
			Geology = packed.Instantiate<GeologyPanel>();
			Geology.Name = "GeologyPanel";
			PanelLayer.AddChild(Geology);
			BindGeologyPanel();
		}
		Geology.Show();
		Geology.Refresh(facts);
		RefreshEvolutionStatus();
	}

	void BindGeologyPanel()
	{
		Geology.PlayPressed += () => RunEvolutionCommand(_world.PlayEvolution);
		Geology.PausePressed += () => RunEvolutionCommand(_world.PauseEvolution);
		Geology.StepPressed += () => RunEvolutionCommand(_world.StepEvolutionOnce);
		Geology.RecoverPressed += () => RunEvolutionCommand(_world.ConfirmEvolutionFault);
		Geology.SeekRequested += target =>
		{
			Geology.SetFeedback(_world.SeekEvolutionToMa(target));
			RefreshEvolutionStatus();
		};
		Geology.SpeedChanged += _world.SetEvolutionSpeed;
	}

	void RunEvolutionCommand(System.Action command)
	{
		Geology.SetFeedback(null);
		command();
		RefreshEvolutionStatus();
	}

	/// <summary>点选或刷新已选格。地壳模式额外读取当前已发布帧。</summary>
	void OnCellPicked(PickedCell? picked, bool showCrust)
	{
		if (picked == null) { CellCard?.Clear(); return; }
		var cell = picked.Value;

		// 名称类文案由界面侧取（**词表在世界侧**）：
		//   档位名 ← `ElevationBands`（海拔分档；与海拔色带同一份档界）。
		string band = ElevationBands.Name(ElevationBands.Of(cell.ElevM));
		var facts = _source?.Facts;
		var crust = showCrust && facts != null ? CrustCellQuery.Read(facts, cell.Index) : null;
		CellCard?.ShowCell(cell, band, crust);
	}

	/// <summary>每帧推进（`ITickable`）：显示后台演化计算状态与进度。</summary>
	public void Tick(double delta)
	{
		RefreshEvolutionStatus();
		Geology?.SetEvolutionSignals(_world?.EvolutionSource, _world?.EvolutionBusy == true);
	}

	void RefreshEvolutionStatus()
	{
		if (Geology != null && _world != null) Geology.SetStatus(_world.EvolutionStatus, _world.EvolutionBusy);
	}

	// ── 依赖解析 / 校验（判据收在 `World.Utils.NodeDependency`，四个管理器共用一份）────

	/// <summary>编辑器依赖校验（同 <see cref="WorldRoot"/> 的自文档化机制）。</summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<CanvasLayer>(PanelLayerPath, nameof(PanelLayerPath), warnings);
		this.Check<MapDock>(DockPath, nameof(DockPath), warnings);
		this.Check<CellInfoCard>(CellCardPath, nameof(CellCardPath), warnings);
		return warnings.ToArray();
	}
}
