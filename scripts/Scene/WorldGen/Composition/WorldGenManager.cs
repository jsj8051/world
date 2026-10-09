using Godot;
using System;
using World.Camera;                // OrbitalCamera
using World.Render.Controllers;    // MapDockController（非 Node 驱动类）
using World.Render.UI;             // MapDock / CellInfoCard（哑组件）
using World.Utils.H3;              // H3（格 id → 经纬）

namespace World.WorldGen;

// 世界生成空间 · 主场景运行期接线（WorldGenWorld.tscn = WorldGenPlanet + OrbitalCamera +
// PanelLayer/CellInfoCard + PanelLayer/MapDock，编辑器拼装）：
// ① 相机设为当前；② 建模式表并交给 `MapDockController`（坞驱动本体已独立成类——本层只保留
//    "认识世界生成"的那一步 `WorldGenMapModes.CreateAll`，坞的状态机不在这里）；
// ③ 每帧 LOD/剔除刷新；④ 左键点选 → 高亮 + 信息卡（海陆/大陆/区域类型/距离/海拔）。
// ★世界事实（Final/Regions/DisplayElevation）一律经 `_planet.Sim`（纯逻辑入口）取，
//   节点本身不再是事实的镜像处（2026-10-09 拆层）。
// ⚠️ 星球 Radius 导出与相机 _planetRadius 场景覆写须同值（取景/裁剪 ∝ R）。
public partial class WorldGenManager : Node3D
{
	WorldGenPlanet _planet;
	Camera3D _camera;
	CellInfoCard _cellPanel;
	MapDockController _dockController;   // 地图坞驱动器（持有 = 明确信号订阅者生命周期）
	int _pickDiag;   // 点选打印限次

	// 点选判定：按下记位，抬起时位移 < 6px 才算点选（拖转球是 OrbitalCamera 的手势，不抢）
	Vector2 _pressPos;
	bool _pressArmed;

	public override void _Ready()
	{
		_planet = GetNode<WorldGenPlanet>("WorldGenPlanet");
		_camera = GetNode<OrbitalCamera>("OrbitalCamera").Cam;   // 子节点 _Ready 先跑 ⇒ 相机已建
		_camera.MakeCurrent();
		_cellPanel = GetNode<CellInfoCard>("PanelLayer/CellInfoCard");

		// 地图坞：模式表由装配层建（只有这里认识"世界生成"这个动作），驱动归 MapDockController
		// （只认 MapMode 抽象，不认识 WorldGen——ADR-0005 依赖方向）。模式持 planet 现取数据
		// ⇒ Regenerate 换数组实例安全；构建即建立初始一致状态（坞高亮 = 视图取色 = 参数行 = 图例）。
		var dock = GetNode<MapDock>("PanelLayer/MapDock");
		_dockController = new MapDockController(dock, _planet.View, WorldGenMapModes.CreateAll(_planet.Sim));
	}

	public override void _Process(double delta) => _planet.UpdateVisibility(_camera);

	// _UnhandledInput：GUI 消费过的事件不会到这里（不抢 GUI 的输入）
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;
		if (mb.Pressed)
		{
			_pressArmed = true;
			_pressPos = mb.Position;
		}
		else if (_pressArmed)
		{
			_pressArmed = false;
			if (mb.Position.DistanceTo(_pressPos) <= 6f)
				PickAt(mb.Position);
		}
	}

	void PickAt(Vector2 screenPos)
	{
		ulong? cell = _planet.View.PickCell(screenPos, _camera);
		if (cell == null)
		{
			_planet.View.HighlightCell(null);
			_cellPanel.Clear();
			return;
		}
		_planet.View.HighlightCell(cell);

		int i = _planet.View.PickCellIndex(cell.Value);
		if (i < 0) { _cellPanel.Clear(); return; }
		var sim = _planet.Sim;   // 世界事实全部经纯逻辑入口取（节点上不再镜像一份）
		var f = sim.Final;
		var g = sim.Regions;
		var ll = H3.CellToLatLng(cell.Value);
		float latDeg = (float)(ll.Lat * 180 / Math.PI);
		float lngDeg = (float)(ll.Lng * 180 / Math.PI);
		float elevM = sim.DisplayElevation[i];
		// 档位名由宿主取（单一事实源 = ElevationMode.ElevationBandName，与其色表同阈值）——
		// 信息卡不依赖海拔语义（纯哑组件）。
		_cellPanel.ShowCell(cell.Value, latDeg, lngDeg, elevM, ElevationMode.ElevationBandName(elevM));

		if (_pickDiag++ >= 20) return;   // 控制台判读限次（信息卡常驻）
		// 收口（§07 D-1）：判读口与地图模式同读 Final 口径（同一行里其它字段本来就都是 Final）
		int rid = f.FinalRegionOfCell[i];
		string region = rid >= 0
			? $"区域{rid}({GeologicalRegions.TypeName(g.Regions[rid].Type)})"
			: "区域−";
		GD.Print($"[WORLDGEN-PICK] 格 {i} lat={latDeg:F1} lng={lngDeg:F1} " +
				 $"land={f.FinalLand[i]}({f.FinalLandFraction[i]:P0}) 陆块={f.FinalLandmassId[i]} {region} " +
				 $"离海={f.FinalDistToCoast[i]} 离岸={f.FinalDistToLand[i]} elev={sim.DisplayElevation[i]:F0}m");
	}
}
