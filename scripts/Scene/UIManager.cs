using Godot;
using System.Collections.Generic;
using World.Data;
using World.Logic;
using World.Render;
using World.UI.Components;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 界面管理器（世界表现子树的界面部分）：持有面板层、地图坞与格信息卡。
/// 只做展示层装配与点选反射；页面级控件（演化面板、页面菜单）由页面自己持有，
/// 不从这里按世界状态分支创建。
/// </summary>
public partial class UIManager : Node3D
{
	[Export] public NodePath PanelLayerPath { get; set; }
	[Export] public NodePath DockPath { get; set; }
	[Export] public NodePath CellCardPath { get; set; }

	/// <summary>面板层（所有页面面板的容器；本管理器持有）。</summary>
	public CanvasLayer PanelLayer { get; private set; }

	/// <summary>地图坞（哑组件；按钮 / 参数行 / 图例由它自己画）。</summary>
	public MapDock Dock { get; private set; }

	/// <summary>格信息卡（哑组件；本管理器订阅渲染侧点选事件后写它）。</summary>
	public CellInfoCard CellCard { get; private set; }

	IWorldFactsSource _source;

	/// <summary>
	/// 解析面板层与两个哑组件，并订阅渲染侧的点选事件。
	/// 必须在渲染管理器初始化之后调用（订阅的是它的事件），且幂等。
	/// </summary>
	public void Initialize(RenderManager render, IWorldFactsSource source = null)
	{
		if (CellCard != null) return;
		_source = source;

		PanelLayer = this.Require<CanvasLayer>(PanelLayerPath, nameof(PanelLayerPath));
		Dock = this.Require<MapDock>(DockPath, nameof(DockPath));
		CellCard = this.Require<CellInfoCard>(CellCardPath, nameof(CellCardPath));

		render.CellPicked += picked => OnCellPicked(picked, render.ShowCrustDetails);
	}

	/// <summary>点选或刷新已选格。地壳模式额外读取当前事实源。</summary>
	void OnCellPicked(PickedCell? picked, bool showCrust)
	{
		if (picked == null) { CellCard?.Clear(); return; }
		var cell = picked.Value;

		string band = ElevationBands.Name(ElevationBands.Of(cell.ElevM));
		var facts = _source?.Facts;
		var crust = showCrust && facts != null ? CrustCellQuery.Read(facts, cell.Index) : null;
		CellCard?.ShowCell(cell, band, crust);
	}

	/// <summary>编辑器依赖校验（同 <see cref="GeographyEvolutionView"/> 的自文档化机制）。</summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<CanvasLayer>(PanelLayerPath, nameof(PanelLayerPath), warnings);
		this.Check<MapDock>(DockPath, nameof(DockPath), warnings);
		this.Check<CellInfoCard>(CellCardPath, nameof(CellCardPath), warnings);
		return warnings.ToArray();
	}
}
