using Godot;
using System.Collections.Generic;
using World.Utils;                 // NodeDependency（Require / Check 扩展）
using World.UI;             // MapDock / CellInfoCard（哑组件）
using World.Logic;             // GeologicalRegions（区域类型名）
using World.Constants;              // RegionType / ElevationBands（两个词表都在世界侧）
using World.Data;                  // PickedCell（拾取结果载体；2026-10-11 由 Render 迁入数据层）

namespace World.Scene;

/// <summary>
/// **界面管理器**（`WorldRoot > UIManager`）：界面这一块的持有者。
/// 持有 `PanelLayer`（所有面板的容器）＋ `MapDock`（坞）＋ `CellInfoCard`（信息卡）
/// ⇒ 加面板挂 `PanelLayer` 下，换整套界面只动这一个子树。
/// <para>★**不碰地图模式**（2026-10-11 收口）：模式表 / 当前模式 / 坞的按钮与参数行同步
/// 全归渲染侧（`RenderManager`），原 `MapDockController` 已删。坞→渲染的信号连接写在
/// **场景文件**里（`scenes/core/WorldGenWorld.tscn` 的 `[connection]`）⇒ 两侧代码互不 import。</para>
/// <para>信息卡订阅渲染侧的 `CellPicked` 事件；名称类文案由本层自己取——**两个词表都在世界侧**
/// （`ElevationBands` / `GeologicalRegions.TypeName`）⇒ 界面层不必向表现层要任何文案。</para>
/// </summary>
public partial class UIManager : Node3D, ITickable
{
	[Export] public NodePath PanelLayerPath { get; set; }
	[Export] public NodePath DockPath { get; set; }
	[Export] public NodePath CellCardPath { get; set; }

	/// <summary>面板层（所有面板的容器；本管理器持有它）。</summary>
	public CanvasLayer PanelLayer { get; private set; }

	/// <summary>地图坞（哑组件；按钮 / 参数行 / 图例由它自己画）。</summary>
	public MapDock Dock { get; private set; }

	/// <summary>格信息卡（哑组件；本管理器订阅渲染侧点选事件后写它）。</summary>
	public CellInfoCard CellCard { get; private set; }

	int _pickDiag;   // 点选判读打印限次

	/// <summary>
	/// **初始化**（由根管理器按序调用，幂等）：
	/// ① 解析自己持有的面板层与两个哑组件；② **订阅渲染侧的点选事件**——信息卡由界面自己写。
	/// ★必须在**渲染管理器初始化之后**调用（订阅的是它的事件）。
	/// </summary>
	public void Initialize(RenderManager render)
	{
		if (CellCard != null) return;   // 幂等

		PanelLayer = this.Require<CanvasLayer>(PanelLayerPath, nameof(PanelLayerPath));
		Dock = this.Require<MapDock>(DockPath, nameof(DockPath));
		CellCard = this.Require<CellInfoCard>(CellCardPath, nameof(CellCardPath));

		render.CellPicked += OnCellPicked;    // 上行：渲染侧点选 → 界面显示
	}

	/// <summary>点选结果 → 信息卡（`null` = 取消选中）+ 控制台判读（限次）。</summary>
	void OnCellPicked(PickedCell? picked)
	{
		if (picked == null) { CellCard?.Clear(); return; }
		var cell = picked.Value;

		// 名称类文案由界面侧取（**两个词表都在世界侧**）：
		//   档位名 ← `ElevationBands`（海拔分档，2026-10-11 由表现层上提；与海拔色带同一份档界）；
		//   区域名 ← `GeologicalRegions.TypeName`（区域类型词表）。
		// ⇒ 界面层**不必向表现层要文案**（原 `IPickedCellLabels` 窄口已随之删除）。
		string band = ElevationBands.Name(ElevationBands.Of(cell.ElevM));
		string region = cell.RegionId >= 0
			? $"区域{cell.RegionId}({GeologicalRegions.TypeName(cell.RegionType)})"
			: "区域−";
		CellCard?.ShowCell(cell, band, region);

		if (_pickDiag++ >= 20) return;   // 判读限次（信息卡常驻）
		GD.Print($"[WORLDGEN-PICK] 格 {cell.Index} lat={cell.LatDeg:F1} lng={cell.LngDeg:F1} " +
				 $"land={cell.IsLand}({cell.LandFraction:P0}) 陆块={cell.LandmassId} {region} " +
				 $"离海={cell.DistToCoast} 离岸={cell.DistToLand} elev={cell.ElevM:F0}m");
	}

	/// <summary>每帧推进（`ITickable`）：界面目前无需逐帧更新（帧序仍由根管理器统一）。</summary>
	public void Tick(double delta)
	{
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
