using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.NewHexWorld.Planet;   // H3PlateManager
using World.NewHexWorld.Plate;    // H3Plate（边界描边角点集派生口）
using World.NewHexWorld.UI.Modes;

namespace World.NewHexWorld.UI.ViewModels
{
// 球视图 VM（设计入口 §2.2）：只读派生 = 当前显示模式（uniform 值 = MapMode.Id）、描边开关、
// 边界线顶点对（几何描边采点）、
// 逐格区域数据派生口（海拔/板号/陆性——View 烘 region_data 数据纹理用）、
// 全局统计（陆/洋占比、板格数/板属性）。View（BallView）订阅 Changed 后拉取刷新；本 VM 不直接
// 持有/修改 Model 数组（区域数据经此处派生口转交，不另存副本——禁令 2 合规）。
	public sealed class HexWorldViewModel
	{
		readonly Ball _ball;
		readonly H3PlateManager _plates;
		MapMode _mode;
		HashSet<(ulong cell, ulong va, ulong vb)> _boundaryEdges;   // 边界描边格边集（不随模式变；首访懒构建）

		// 视图刷新信号（显示模式 / 描边开关变化 → View 拉取刷 uniform）。
		public event Action Changed;

		public HexWorldViewModel(Ball ball, H3PlateManager plates, MapMode initialMode)
		{
			_ball = ball;
			_plates = plates;
			_mode = initialMode;
		}

		// ── 只读派生 ──

		public MapMode Mode => _mode;

		// 当前显示模式（BallView 提交为材质 display_mode uniform；取值 = MapMode.Id，shader 同表）。
		public int DisplayMode => _mode.Id;

		// 边界线层开关（当前模式是否叠加；View 提交时查）。
		public bool ShowBoundaries => _mode.ShowPlateBoundaries;

		// ── 逐格区域数据派生口（View 烘 region_data 数据纹理用；Model 数组引用直转，不另存副本）──

		public int CellCount => _plates.CellCount;
		public float[] CellElevations => _plates.Plate.Crust.Elevation;   // 海拔（米，0=海平面）
		public int[] CellPlateIds => _plates.Plate.Crust.PlateId;         // 每格归属板号
		public float[] CellTemperatures => _plates.Plate.Crust.TemperatureC;   // 年均温（°C）
		public float[] CellPrecipMmYear => _plates.Plate.Crust.PrecipMmYear;   // 年降水（mm/yr）
		public int PlateCount => _plates.NumPlates;

		// 降水自适应配色域（陆地 min-max）：View 烘 region_data2 用同一域。
		public (float Min, float Max) PrecipLandRange
			=> PrecipitationMapMode.LandRange(_plates.Plate.Crust);

		// 河流走廊（懒构建一次）：View 重建显示网格（走廊格细分）
		// 与烘焙河档共用同一口。
		H3Rivers.Corridors _riverCorridors;
		public H3Rivers.Corridors RiverCorridors
		{
			get
			{
				if (_riverCorridors == null)
					_riverCorridors = H3Rivers.Build(_ball, _plates.Plate.Crust, _plates.Plate.WaterCycle);
				return _riverCorridors;
			}
		}

		// 统一判陆口（Crust.IsLand：长英质厚 > 0；渲染数据位与信息面板同一口径）。
		public bool IsLand(int cellIndex) => _plates.Plate.Crust.IsLand(cellIndex);

// 边界格边集：(格 id, 顶点对) = 异板共享边在边界两侧格的名下记账（每边两条记录）。数据源 =
// H3Plate.ExtractBoundaryCellEdges（01 §4 派生口；集不随模式变，首访懒构建一次）。
public HashSet<(ulong cell, ulong va, ulong vb)> BoundaryCellEdges
{
	get
	{
		if (_boundaryEdges == null)
			_boundaryEdges = H3Plate.ExtractBoundaryCellEdges(_ball, _plates.Plate.Crust.PlateId);
		return _boundaryEdges;
	}
}

// 边界线顶点对全集（几何描边口）：BoundaryCellEdges 去重（每边两侧格各记一条 →
// 只留一条）+ 排序（确定性几何纪律），BallView 换算成世界坐标点串喂 SphereLines 逐边采点成线带。
List<(ulong va, ulong vb)> _boundaryVertEdges;   // 不随模式变；首访懒构建一次
public IReadOnlyList<(ulong va, ulong vb)> BoundaryVertexEdges
{
	get
	{
		if (_boundaryVertEdges == null)
			_boundaryVertEdges = BoundaryCellEdges
				.Select(e => (e.va, e.vb))
				.Distinct()
				.OrderBy(e => e.va).ThenBy(e => e.vb)
				.ToList();
		return _boundaryVertEdges;
	}
}

		// ── 全局统计（板格数 / 陆洋占比 / 板属性；逻辑层统计转只读暴露）──

		public int LandCellCount => _plates.LandCellCount;
		public int OceanCellCount => _plates.OceanCellCount;
		public int[] PlateCounts => _plates.PlateCounts;
		public bool IsPlateLand(int plate) => _plates.IsPlateLand(plate);

		// ── 命令（组装器下行；模式切换 → 广播刷新）──

		// 换当前模式策略并广播（坞 VM 事件接线；同模式幂等）。
		public void ApplyMode(MapMode mode)
		{
			if (mode.Id == _mode.Id) return;
			_mode = mode;
			Changed?.Invoke();
		}
	}
}
