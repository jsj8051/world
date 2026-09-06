using Godot;
using System;
using System.Collections.Generic;
using World.MapView;              // TileInfoEntry（格信息条目结构）
using World.NewHexWorld.Plate;    // H3Plate
using World.NewHexWorld.Planet;   // H3PlateManager
using World.NewHexWorld.UI;       // HexDock / HexInfoPanel
using World.Utils.H3;

namespace World.NewHexWorld
{
	// 场景 Root 编排：内容层（H3Plate 板块）→ BallView 染色 + UI（HexDock 坞 / HexInfoPanel 格信息）+ 点击拾取。
	// 地图模式：0=板块地图（坞内唯一模式；将来加模式在 OnModeSelected 扩展）。
	public partial class BallManager : Node3D
	{
		[Export] public int NumPlates = 12;      // 板块数（Voronoi 种子数）
		[Export] public int Seed = 42;           // 星球种子（同 seed 永远同一板块格局）

		Camera3D _camera;                        // 子节点：相机（拾取射线源）
		BallView _ballView;                      // 子节点：球视图（Ball/BallMesh/染色/拾取）
		H3PlateManager _plates;                  // 内容层：板块场
		HexDock _dock;                           // 地图模式坞（UiLayer 下）
		HexInfoPanel _infoPanel;                 // 格信息面板（UiLayer 下）
		int _mode;                               // 当前地图模式（0=板块地图）

		public override void _Ready()
		{
			_camera = GetNode<Camera3D>("Camera3D");
			_camera.Position = new Vector3(0f, 0f, 3f);   // 球半径 1 → 摆到球外
			_camera.LookAt(Vector3.Zero, Vector3.Up);

			// 内容：Voronoi 分板块（BallView._Ready 已先建好 Ball → BallData 可用）
			_ballView = GetNode<BallView>("Ball");
			_plates = new H3PlateManager();
			_plates.Init(_ballView.BallData, NumPlates, Seed);
			ShowPlateMap();

			// UI：模式坞 + 格信息面板（场景挂在 UiLayer 下）
			var uiLayer = GetNode<CanvasLayer>("UiLayer");
			_dock = uiLayer.GetNode<HexDock>("HexDock");
			_dock.ModeSelected += OnModeSelected;
			_dock.SetMode(0);
			_infoPanel = uiLayer.GetNode<HexInfoPanel>("HexInfoPanel");
		}

		// ── 地图模式（0=板块地图；将来加模式在此扩展）──

		private void OnModeSelected(int modeId)
		{
			_mode = modeId;
			if (modeId == 0) ShowPlateMap();   // 板块地图（当前唯一模式，重染幂等）
		}

		// 板块地图：套用"每格按板块着色 + 边界暗化"的配色方案。
		private void ShowPlateMap() => _ballView.ApplyColorScheme(PlateMapColorAt);

		// 板块地图配色：该格所属板块色；边界格（任一邻居异板）暗化 → 汇聚带一眼可见。
		private Color PlateMapColorAt(ulong cell)
		{
			int i = _ballView.BallData.CellIndexOf(cell);
			int plate = _plates.Plate.PlateId[i];
			Color col = PlateColor(plate);
			return IsPlateBoundary(i) ? col * 0.45f : col;
		}

		// 板块基准色：板号 → HSV 均布色相。
		private Color PlateColor(int plate) => Color.FromHsv(plate / (float)NumPlates, 0.8f, 0.9f);

		// 边界判定：格 i 是否有任一邻居属于别的板块。
		private bool IsPlateBoundary(int cellIndex)
		{
			var ball = _ballView.BallData;
			int plate = _plates.Plate.PlateId[cellIndex];
			foreach (int j in ball.CellNeighbors[cellIndex])
				if (_plates.Plate.PlateId[j] != plate) return true;
			return false;
		}

		// ── 点击拾取：射线 → 球面格 → 填信息面板 ──

		public override void _UnhandledInput(InputEvent @event)
		{
			if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } ev)
			{
				ulong? cell = _ballView.PickCell(ev.Position, _camera);
				if (cell is ulong id) _infoPanel.ShowAt(BuildCellInfoEntries(id));
				else _infoPanel.HidePanel();   // 点到空白（球外）→ 隐藏
			}
		}

		// 组装该格格信息条目：格子号 / 板块(色块) / 板块大小与占比 / 经纬度（纯数据，显示交面板）。
		private List<TileInfoEntry> BuildCellInfoEntries(ulong cell)
		{
			var ball = _ballView.BallData;
			int i = ball.CellIndexOf(cell);
			int plate = _plates.Plate.PlateId[i];
			int cellCount = ball.CellIds.Length;
			float sharePct = _plates.PlateCounts[plate] * 100f / cellCount;

			var ll = H3.CellToLatLng(cell);   // 弧度 → 度（显示用）
			double latDeg = ll.Lat * 180.0 / Math.PI;
			double lngDeg = ll.Lng * 180.0 / Math.PI;

			return new List<TileInfoEntry>
			{
				new("格子", H3.H3ToString(cell), PlateMapColorAt(cell)),   // 色块 = 该格当前颜色
				new("板块", plate.ToString(), PlateColor(plate)),
				new("板块格数", _plates.PlateCounts[plate].ToString()),
				new("全球占比", $"{sharePct:F1}%"),
				new("经纬度", $"{latDeg:F1}°, {lngDeg:F1}°"),
			};
		}
	}
}
