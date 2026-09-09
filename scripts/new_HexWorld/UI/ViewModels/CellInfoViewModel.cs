using System;
using System.Collections.Generic;
using Godot;
using World.MapView;                       // TileInfoEntry（标签/值/色块；跨世界复用）
using World.NewHexWorld.Planet;             // H3PlateManager
using World.NewHexWorld.UI.Modes;
using World.Utils.H3;                       // H3ToString / CellToLatLng（门面）

namespace World.NewHexWorld.UI.ViewModels
{
	// 格信息面板 VM（设计入口 §2.2/§2.6）：拾取格（cell id 输入）→ 组装条目（通用行 + 当前模式
	// 策略行）；模式切换时若面板正显示则按新模式重建条目。条目 = 只读派生（查逻辑层场/板统计），
	// 经 EntriesChanged 事件交给面板 View 渲染（null = 无选中 → 隐藏）。
	public sealed class CellInfoViewModel
	{
		readonly Ball _ball;
		readonly H3PlateManager _plates;
		MapMode _mode;
		int _selectedIndex = -1;             // 当前选中格下标（-1 = 无选中，面板隐藏）

		// 条目就绪信号：非 null = 显示该组条目；null = 无选中（View 隐藏面板）。
		public event Action<IReadOnlyList<TileInfoEntry>> EntriesChanged;

		public CellInfoViewModel(Ball ball, H3PlateManager plates, MapMode initialMode)
		{
			_ball = ball;
			_plates = plates;
			_mode = initialMode;
		}

		// 模式切换下行（组装器接坞 VM 事件）；面板正显示 → 按新模式重建条目。
		public void SetMode(MapMode mode)
		{
			if (mode.Id == _mode.Id) return;
			_mode = mode;
			if (_selectedIndex >= 0) Publish();
		}

		// 拾取格（点击路由；cell id → 下标，查不到即抛——id 域错误当场暴露）。
		public void ShowCell(ulong cellId)
		{
			_selectedIndex = _ball.CellIndexOf(cellId);
			Publish();
		}

		// 清空选中（点击空白；面板隐藏）。
		public void Hide() => EntriesChanged?.Invoke(null);

		// 组装条目并广播：通用行（格子号[swatch=当前模式色] / 所属板块[swatch=板色] / 经纬度）
			// + 当前模式策略特有行（MapMode.TileInfo——海拔:高度+类型·分带；板块:格数+占比）。
		void Publish()
		{
			int i = _selectedIndex;
			int plate = _plates.PlateOfCell(i);
			ulong cellId = _ball.CellIds[i];
			var ll = H3.CellToLatLng(cellId);   // 弧度 → 度（显示用）
			double latDeg = ll.Lat * 180.0 / Math.PI;
			double lngDeg = ll.Lng * 180.0 / Math.PI;

			var entries = new List<TileInfoEntry>
			{
				new("格子", H3.H3ToString(cellId), _mode.CellColorAt(i)),          // 色块 = 该格当前模式色
				new("板块", plate.ToString(), PlateMapMode.PlateColor(plate, _plates.NumPlates)),
				new("经纬度", $"{latDeg:F1}°, {lngDeg:F1}°"),
			};
			entries.AddRange(_mode.TileInfo(i));
			EntriesChanged?.Invoke(entries);
		}
	}
}
