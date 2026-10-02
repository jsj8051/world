using Godot;
using System;
using System.Collections.Generic;
using World.Camera;             // OrbitalCamera
using World.Utils.H3;           // H3（格 id → 经纬）

namespace World.NoiseWorld;

// 噪声地形实验 · 主场景运行期接线。场景树在**编辑器里按组件拼装**（NoiseWorld.tscn =
// NoisePlanet + OrbitalCamera + PanelLayer/NoiseCellPanel + PanelLayer/NoiseDock，全部实例可见可调），
// 本脚本只做运行期接线：① 相机设为当前；② 地图坞接模式表（模式 = NoiseMapMode 策略，点按钮 →
// 视图换取色函数重烘颜色纹理）；③ 左键点选格子 → 高亮 + 信息卡（按住拖转球不算点选）。
// ⚠️ 星球 Radius 导出与相机 _planetRadius 场景覆写须同值（取景/裁剪 ∝ R；两处都在
//    NoiseWorld.tscn 的实例上可见，改一处须同步另一处）。
public partial class NoiseWorldManager : Node3D
{
	NoisePlanet _planet;
	Camera3D _camera;
	NoiseCellPanel _cellPanel;
	NoiseDock _dock;
	List<NoiseMapMode> _modes;

	// 点选判定：按下记位，抬起时位移 < 6px 才算点选（拖转球是 OrbitalCamera 的手势，不抢）
	Vector2 _pressPos;
	bool _pressArmed;

	public override void _Ready()
	{
		_planet = GetNode<NoisePlanet>("NoisePlanet");
		_camera = GetNode<OrbitalCamera>("OrbitalCamera").Cam;   // 子节点 _Ready 先跑 ⇒ 相机已建
		_camera.MakeCurrent();
		_cellPanel = GetNode<NoiseCellPanel>("PanelLayer/NoiseCellPanel");

		// 地图坞：模式表 = NoiseMapMode.CreateAll（注册序 = 坞按钮序 = Id，数量不符 BindModes 当场抛）
		_modes = NoiseMapMode.CreateAll(_planet.Terrain, _planet.Plates, _planet.Climate);
		_dock = GetNode<NoiseDock>("PanelLayer/NoiseDock");
		var names = new string[_modes.Count];
		for (int i = 0; i < names.Length; i++) names[i] = _modes[i].Name;
		_dock.BindModes(names);
		_dock.ModeSelected += id =>
		{
			_planet.View.SetMode(_modes[id]);   // 换取色函数重烘颜色纹理（几何/UV 不动）
			_dock.SetMode(id);                  // 按钮高亮同步
		};
	}

	// 每帧：LOD/剔除刷新（相机动了才重判，静态省帧）
	public override void _Process(double delta)
	{
		_planet.UpdateVisibility(_camera);
	}

	// _UnhandledInput：GUI 消费过的事件不会到这里（面板不被点穿），与 OrbitalCamera 同纪律
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
		var ll = H3.CellToLatLng(cell.Value);
		float latDeg = (float)(ll.Lat * 180.0 / Math.PI);
		float lngDeg = (float)(ll.Lng * 180.0 / Math.PI);
		_planet.View.TryGetElevation(cell.Value, out float elevM);
		_cellPanel.ShowCell(cell.Value, latDeg, lngDeg, elevM);
	}
}
