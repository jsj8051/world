using Godot;
using World.Data;                  // PickedCell（拾取结果载体；2026-10-11 由本文件迁入数据层）
using World.Utils.H3;              // H3（格 id → 经纬）
using World.Logic;              // WorldGenSimulation（世界事实来源）＋ GeologicalRegions.RegionType
using World.Constants;              // RegionType（区域类型词表；2026-10-11 由 Logic 上提）

namespace World.Render;

/// <summary>
/// **格拾取**（P0，2026-10-11 从 `WorldManager` 抽出、同日随点选一起归 `RenderManager`）：
/// 屏幕坐标 → 一格的世界事实。只依赖"视图 + 相机 + 世界事实"；
/// **不持有节点、不写界面、不打印** ⇒ 可脱离管理器单独用 / 单测。
/// ★世界事实**每次现取**（`Sim.Run()` 会换数组实例，缓存数组引用会读到旧场）。
/// ★相机**可换**（<see cref="SetCamera"/>）：它由宿主在每次点选前现取活动相机后喂进来，
///   所以本类不会因为"相机被换掉"而拿着陈旧的一台。
/// </summary>
public sealed class WorldPicker
{
	readonly BallView _view;
	readonly WorldGenSimulation _sim;
	Camera3D _camera;                 // 可换：宿主每次点选前现取（不焊死构造时那一台）

	public WorldPicker(BallView view, Camera3D camera, WorldGenSimulation sim)
	{
		_view = view;
		_camera = camera;
		_sim = sim;
	}

	/// <summary>换一台相机（宿主现取活动相机后调；射线要用它）。</summary>
	public void SetCamera(Camera3D camera) => _camera = camera;

	/// <summary>命中一格 ⇒ 填好 <paramref name="cell"/> 并返回 true；点空 / 点在球外 ⇒ false。</summary>
	public bool TryPick(Vector2 screenPos, out PickedCell cell)
	{
		cell = default;
		if (_view == null || _camera == null || _sim == null) return false;

		ulong? id = _view.PickCell(screenPos, _camera);
		if (id == null) return false;
		int i = _view.PickCellIndex(id.Value);
		if (i < 0) return false;

		var facts = _sim.Facts.Final;          // 现取（不复用引用）
		var regions = _sim.Terrain.Regions;
		var ll = H3.CellToLatLng(id.Value);
		int rid = facts.FinalRegionOfCell[i];

		cell = new PickedCell
		{
			CellId = id.Value,
			Index = i,
			LatDeg = (float)(ll.Lat * 180 / System.Math.PI),
			LngDeg = (float)(ll.Lng * 180 / System.Math.PI),
			ElevM = _sim.Terrain.DisplayElevation[i],     // 现取
			IsLand = facts.FinalLand[i],
			LandFraction = facts.FinalLandFraction[i],
			LandmassId = facts.FinalLandmassId[i],
			DistToCoast = facts.FinalDistToCoast[i],
			DistToLand = facts.FinalDistToLand[i],
			RegionId = rid,
			RegionType = rid >= 0 ? regions.Regions[rid].Type : default,
		};
		return true;
	}

	/// <summary>把高亮切到某格（`null` = 清除）。</summary>
	public void Highlight(ulong? cellId) => _view?.HighlightCell(cellId);
}
