using System;
using System.Collections.Generic;
using Godot;
using World.NewHexWorld.Planet;   // H3PlateManager
using World.NewHexWorld.Plate;    // H3Plate（边界链提取派生口）
using World.NewHexWorld.UI.Modes;

namespace World.NewHexWorld.UI.ViewModels
{
	// 球视图 VM（设计入口 §2.2）：只读派生 = 每格颜色投影（当前 MapMode 策略逐格派生）、
	// 边界描边角点集（描边渲染派生数据，2026-09-08 拍板：弃"链几何 + 深度微偏"独立线层，
	// 改老树同款边界压暗描边）、全局统计（陆/洋占比、板格数/板属性）。View（BallView）订阅
	// Changed 后拉取刷新；本 VM 不直接持有/修改 Model 数组（颜色投影经 MapMode 策略口），
	// 派生投影缓存随模式变更失效重算（禁令 2 合规）。
	public sealed class HexWorldViewModel
	{
		readonly Ball _ball;
		readonly H3PlateManager _plates;
		MapMode _mode;
		Color[] _cellColors;                 // 当前模式逐格投影缓存（下标 = 格下标，与 CellIds 对齐）
		HashSet<(ulong cell, ulong vid)> _boundaryVerts;   // 边界描边角点集（不随模式变；首访懒构建）

		// 视图刷新信号（颜色投影 / 边界线开关任一变化 → View 拉取重提交）。
		public event Action Changed;

		public HexWorldViewModel(Ball ball, H3PlateManager plates, MapMode initialMode)
		{
			_ball = ball;
			_plates = plates;
			_mode = initialMode;
			RepaintColors();
		}

		// ── 只读派生 ──

		public MapMode Mode => _mode;

		// 边界线层开关（当前模式是否叠加；View 提交时查）。
		public bool ShowBoundaries => _mode.ShowPlateBoundaries;

		// 逐格投影缓存（读口给 View 展开顶点色；勿改）。
		public Color[] CellColors => _cellColors;

	// 边界描边角点集：(格 id, 顶点 id) 对 = 异板共享边两端顶点在边界两侧格的记账。View 据此把
	// 贴边界角点的边界权重置 0，描边带宽由片元侧权重插值决定。数据源 = H3Plate.ExtractBoundaryVerts
	// （01 §4 派生口；集不随模式变，首访懒构建一次）。
	public HashSet<(ulong cell, ulong vid)> BoundaryCellVerts
	{
		get
		{
			if (_boundaryVerts == null)
				_boundaryVerts = H3Plate.ExtractBoundaryVerts(_ball, _plates.Plate.Crust.PlateId);
			return _boundaryVerts;
		}
	}

		// ── 全局统计（板格数 / 陆洋占比 / 板属性；逻辑层统计转只读暴露）──

		public int CellCount => _plates.CellCount;
		public int LandCellCount => _plates.LandCellCount;
		public int OceanCellCount => _plates.OceanCellCount;
		public int[] PlateCounts => _plates.PlateCounts;
		public bool IsPlateLand(int plate) => _plates.IsPlateLand(plate);

		// ── 命令（组装器下行；模式切换 → 重投影 → 广播）──

		// 换当前模式策略并重投影（坞 VM 事件接线；同模式幂等）。
		public void ApplyMode(MapMode mode)
		{
			if (mode.Id == _mode.Id) return;
			_mode = mode;
			RepaintColors();
			Changed?.Invoke();
		}

		// 当前模式逐格投影重算（N 格 × 模式取色；低频操作——模式切换才发生）。
		void RepaintColors()
		{
			var colors = new Color[_plates.CellCount];
			for (int i = 0; i < colors.Length; i++) colors[i] = _mode.CellColorAt(i);
			_cellColors = colors;
		}
	}
}
