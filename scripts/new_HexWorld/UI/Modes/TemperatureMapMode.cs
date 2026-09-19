using System.Collections.Generic;
using Godot;
using World.MapView;                       // TileInfoEntry
using World.NewHexWorld.Plate;             // Crust
using static World.Utils.ColorRamp;        // RampSampleSmooth（通用连续色带算法，复用不复制）

namespace World.NewHexWorld.UI.Modes
{
	// 模式 2 温度：年均温连续色带（−85~+45°C 域）；采样 = RampSampleSmooth（与海拔模式同一平滑纪律，
	// 画面与信息面板色块同一色带源）。数据源 = Crust.TemperatureC（H3Climate 终态一次计算）。
	// 球面渲染 = 片元侧查温度色带纹理（region_data A 通道 = 温度归一，域 = 本表首尾 Pos，
	// 见 BallView.BuildRegionDataTexture）。洋流/季风调制与冰冻圈判据（≤−5°C 海冰）待后续批次。
	public sealed class TemperatureMapMode : MapMode
	{
		/// <summary>温度连续色带（位置=°C，升序）：
		/// 极寒深蓝（−85）→ 深蓝（−30）→ 冰点（0）→ 绿（15，温凉）→ 黄（30，宜居带）→ 红（45，高温），
		/// 越界 clamp 两端色。【改色带】= 编辑本表（画面/信息面板/region_data A 通道三处同源）。</summary>
		public static readonly ColorStop[] TemperatureStops =
		{
			new(-85f, new Color(0.08f, 0.12f, 0.45f)),  // 极寒
			new(-30f, new Color(0.10f, 0.28f, 0.62f)),  // 深蓝
			new(0f,   new Color(0.22f, 0.52f, 0.72f)),  // 冰点
			new(15f,  new Color(0.38f, 0.72f, 0.42f)),  // 绿（温凉）
			new(30f,  new Color(0.92f, 0.78f, 0.28f)),  // 黄（宜居带）
			new(45f,  new Color(0.88f, 0.30f, 0.15f)),  // 红（高温）
		};

		readonly Crust _crust;

		public TemperatureMapMode(Crust crust)
		{
			_crust = crust;
		}

		public override int Id => 2;
		public override string Name => "温度";
		public override bool ShowPlateBoundaries => true;

		// 取色 = 平滑采样（与海拔模式同款；信息面板色块与片元色带纹理同一函数源）
		public override Color CellColorAt(int cellIndex)
			=> RampSampleSmooth(TemperatureStops, _crust.TemperatureC[cellIndex]);

		/// <summary>温度带名（显示分带，非柯本——柯本待生物群系批次；断点归右侧段，与海拔分带同纪律）：
		/// &lt;−5 寒带 / −5~5 寒温带 / 5~15 温带 / 15~25 亚热带 / ≥25 热带。</summary>
		public static string TemperatureZoneName(float tempC)
		{
			if (tempC < -5f) return "寒带";
			if (tempC < 5f) return "寒温带";
			if (tempC < 15f) return "温带";
			if (tempC < 25f) return "亚热带";
			return "热带";
		}

		public override IReadOnlyList<TileInfoEntry> TileInfo(int cellIndex)
		{
			float tempC = _crust.TemperatureC[cellIndex];
			return new List<TileInfoEntry>
			{
				new("年均温", $"{tempC:F1} °C", CellColorAt(cellIndex)),
				new("温度带", TemperatureZoneName(tempC)),
			};
		}
	}
}
