using System.Collections.Generic;
using Godot;
using World.MapView;                       // TileInfoEntry
using World.NewHexWorld.Plate;             // Crust

namespace World.NewHexWorld.UI.Modes
{
	// 模式 1 板块（设计入口 §2.5 清单）：板色 = 板号 HSV 均布色相，纯色铺满。
	// 边界观感（2026-09-09 用户拍板）：弃旧"边界格整格暗化 ×0.45"（暗带一格一格），
	// 改叠加边界描边带（ShowPlateBoundaries = true，与其他模式同一套骑缝细线）。
	public sealed class PlateMapMode : MapMode
	{
		readonly Crust _crust;
		readonly int _plateCount;          // 板块总数（板色均布分母）
		readonly int[] _plateCellCounts;   // 每板块格数（信息面板"板块格数/占比"）

		public PlateMapMode(Crust crust, int plateCount, int[] plateCellCounts)
		{
			_crust = crust;
			_plateCount = plateCount;
			_plateCellCounts = plateCellCounts;
		}

		public override int Id => 1;
		public override string Name => "板块";
		public override bool ShowPlateBoundaries => true;   // 边界描边带（09-09 拍板，弃板色自带界）

		/// <summary>板号 → 基准色：HSV 均布色相（照旧实现；供本模式与格信息面板通用行共用）。</summary>
		public static Color PlateColor(int plate, int plateCount)
			=> Color.FromHsv(plate / (float)plateCount, 0.8f, 0.9f);

		// 板块地图配色：该格所属板块纯色（边界观感由描边带层负责，取色不掺暗化）
		public override Color CellColorAt(int cellIndex)
			=> PlateColor(_crust.PlateId[cellIndex], _plateCount);

		public override IReadOnlyList<TileInfoEntry> TileInfo(int cellIndex)
		{
			int plate = _crust.PlateId[cellIndex];
			float sharePct = _plateCellCounts[plate] * 100f / _crust.PlateId.Length;
			return new List<TileInfoEntry>
			{
				new("板块格数", _plateCellCounts[plate].ToString()),
				new("全球占比", $"{sharePct:F1}%"),
			};
		}
	}
}
