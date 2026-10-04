using Godot;
using System;
using System.Collections.Generic;
using World.Camera;             // OrbitalCamera
using World.Render;             // BallView / MapMode（决策 08 §4.4 表现层保留资产）
using World.Render.UI;          // MapDock / CellInfoCard
using World.Utils.H3;           // H3（格 id → 经纬）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 主场景运行期接线（WorldGenWorld.tscn = WorldGenPlanet + OrbitalCamera +
// PanelLayer/CellInfoCard + PanelLayer/MapDock，编辑器拼装）：
// ① 相机设为当前；② 地图坞接模式表（模式持 planet 现取数据，点按钮 → 视图重烘颜色纹理）；
// ③ 每帧 LOD/剔除刷新；④ 左键点选 → 高亮 + 信息卡（海陆/大陆/区域类型/距离/海拔）。
// ⚠️ 星球 Radius 导出与相机 _planetRadius 场景覆写须同值（取景/裁剪 ∝ R）。
public partial class WorldGenManager : Node3D
{
	WorldGenPlanet _planet;
	Camera3D _camera;
	CellInfoCard _cellPanel;
	MapDock _dock;
	List<MapMode> _modes;
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

		// 地图坞：模式表 = WorldGenMapModes.CreateAll（模式持 planet 现取数据 ⇒ Regenerate 换实例安全）
		_modes = WorldGenMapModes.CreateAll(_planet);
		_dock = GetNode<MapDock>("PanelLayer/MapDock");
		var names = new string[_modes.Count];
		for (int i = 0; i < names.Length; i++) names[i] = _modes[i].Name;
		_dock.BindModes(names);
		_dock.ModeSelected += id =>
		{
			_planet.View.SetMode(_modes[id]);   // 换取色函数重烘颜色纹理（几何/UV 不动）
			_dock.SetMode(id);                  // 按钮高亮同步
		};
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
		var f = _planet.Final;
		var g = _planet.Regions;
		var ll = H3.CellToLatLng(cell.Value);
		float latDeg = (float)(ll.Lat * 180 / Math.PI);
		float lngDeg = (float)(ll.Lng * 180 / Math.PI);
		_cellPanel.ShowCell(cell.Value, latDeg, lngDeg, _planet.DisplayElevation[i]);

		if (_pickDiag++ >= 20) return;   // 控制台判读限次（信息卡常驻）
		// 收口（§07 D-1）：判读口与地图模式同读 Final 口径（同一行里其它字段本来就都是 Final）
		int rid = f.FinalRegionOfCell[i];
		string region = rid >= 0
			? $"区域{rid}({GeologicalRegions.TypeName(g.Regions[rid].Type)})"
			: "区域−";
		GD.Print($"[WORLDGEN-PICK] 格 {i} lat={latDeg:F1} lng={lngDeg:F1} " +
				 $"land={f.FinalLand[i]}({f.FinalLandFraction[i]:P0}) 陆块={f.FinalLandmassId[i]} {region} " +
				 $"离海={f.FinalDistToCoast[i]} 离岸={f.FinalDistToLand[i]} elev={_planet.DisplayElevation[i]:F0}m");
	}
}
