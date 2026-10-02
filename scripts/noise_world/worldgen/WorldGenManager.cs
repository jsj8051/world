using Godot;
using System;
using System.Collections.Generic;
using World.Camera;             // OrbitalCamera
using World.Utils.H3;           // H3（格 id → 经纬）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 主场景运行期接线（WorldGenWorld.tscn = WorldGenPlanet + OrbitalCamera，编辑器拼装）：
// ① 相机设为当前；② 每帧 LOD/剔除刷新；③ 左键点选 → 高亮 + 控制台打印格判读
// （海陆/大陆号/离海/离岸——阶段 1 五件套的直接判读口；信息卡 UI 走后续批次）。
// ⚠️ 星球 Radius 导出与相机 _planetRadius 场景覆写须同值（取景/裁剪 ∝ R）。
public partial class WorldGenManager : Node3D
{
	WorldGenPlanet _planet;
	Camera3D _camera;
	int _pickDiag;   // 点选打印限次

	// 点选判定：按下记位，抬起时位移 < 6px 才算点选（拖转球是 OrbitalCamera 的手势，不抢）
	Vector2 _pressPos;
	bool _pressArmed;

	public override void _Ready()
	{
		_planet = GetNode<WorldGenPlanet>("WorldGenPlanet");
		_camera = GetNode<OrbitalCamera>("OrbitalCamera").Cam;   // 子节点 _Ready 先跑 ⇒ 相机已建
		_camera.MakeCurrent();
	}

	public override void _Process(double delta) => _planet.UpdateVisibility(_camera);

	// _UnhandledInput：GUI 消费过的事件不会到这里（与 NoiseWorldManager 同纪律）
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
			return;
		}
		_planet.View.HighlightCell(cell);

		// 阶段 1 五件套判读（控制台；信息卡 UI 后续批次）
		int i = _planet.View.PickCellIndex(cell.Value);
		if (i < 0 || _pickDiag++ >= 20) return;
		var p = _planet.Projector;
		var ll = H3.CellToLatLng(cell.Value);
		GD.Print($"[WORLDGEN-PICK] 格 {i} lat={ll.Lat * 180 / Math.PI:F1} lng={ll.Lng * 180 / Math.PI:F1} " +
				 $"land={p.Land[i]} continent={(p.ContinentId[i] >= 0 ? p.ContinentId[i].ToString() : "−")} " +
				 $"离海={p.DistToCoast[i]} 离岸={p.DistToLand[i]} elev={p.ElevationM[i]:F0}m raw={p.Raw[i]:F3}");
	}
}
