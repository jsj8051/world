using System;
using System.Collections.Generic;
using Godot;
using World.MapView;                       // TileInfoEntry
using World.NewHexWorld.Plate;             // Crust
using static World.Utils.ColorRamp;        // RampSampleSmooth（通用连续色带算法，复用不复制）

namespace World.NewHexWorld.UI.Modes
{
	// 模式 3 降水：年降水连续色带（少雨黄 → 多雨蓝）；归一化——**陆地
	// min-max 自适应**（不用固定 2000mm：不同星球雨量跨度差一个量级，固定域会废掉对比度；
	// 海洋格 clamp 到域外两端）。色带定义内聚本文件（画面/信息面板/region_data2 三处同源）。
	// 数据源 = Crust.PrecipMmYear（H3Precipitation 结构 × H3WaterCycle λ 闭合）。干湿分带用
	// **绝对 mm**（地球标定），与自适应配色互不影响——颜色可比对本星格局，分带可比对地球。
	public sealed class PrecipitationMapMode : MapMode
	{
		/// <summary>降水连续色带（位置 = 陆地 min-max 自适应域的归一 0..1）：少雨黄 → 多雨蓝。
		/// 【改色带】= 编辑本表（归一域不动）。</summary>
		public static readonly ColorStop[] PrecipStops =
		{
			new(0f, new Color(0.90f, 0.80f, 0.40f)),  // 少雨黄
			new(1f, new Color(0.10f, 0.30f, 0.70f)),  // 多雨蓝
		};

		readonly Crust _crust;
		readonly float _landMin, _landMax;   // 陆地 min-max 自适应域（构造时扫一遍陆格）

		public PrecipitationMapMode(Crust crust)
		{
			_crust = crust;
			(_landMin, _landMax) = LandRange(crust);
		}

		public override int Id => 3;
		public override string Name => "降水";
		public override bool ShowPlateBoundaries => true;

		/// <summary>陆地 min-max 域（画面归一与信息面板同一口径；全陆无降水的退化域 = (0,1)）。</summary>
		public static (float min, float max) LandRange(Crust crust)
		{
			float min = float.MaxValue, max = float.MinValue;
			bool any = false;
			for (int i = 0; i < crust.PrecipMmYear.Length; i++)
			{
				if (!crust.IsLand(i)) continue;
				float p = crust.PrecipMmYear[i];
				if (p < min) min = p;
				if (p > max) max = p;
				any = true;
			}
			return !any || max <= min ? (0f, 1f) : (min, max);
		}

		// 取色 = 陆地 min-max 归一后平滑采样（洋格 clamp 到域两端；信息面板与片元色带纹理同一函数源）
		public override Color CellColorAt(int cellIndex)
			=> RampSampleSmooth(PrecipStops, Normalize(_crust.PrecipMmYear[cellIndex], _landMin, _landMax));

		/// <summary>归一口（静态单一事实源）：mm → 陆地 min-max 域内归一 [0,1]，域外 clamp
		/// （洋格）。信息面板取色、BallView 烘 region_data2 共用本式。</summary>
		public static float Normalize(float mm, float landMin, float landMax)
			=> Math.Clamp((mm - landMin) / Math.Max(landMax - landMin, 1e-6f), 0f, 1f);

		/// <summary>干湿分带（绝对 mm，地球标定；断点归右侧段，与海拔/温度分带同纪律）：
		/// &lt;250 干旱带 / 250~500 半干旱带 / 500~1500 湿润带 / ≥1500 多雨带。</summary>
		public static string AridityZoneName(float mmPerYear)
		{
			if (mmPerYear < 250f) return "干旱带";
			if (mmPerYear < 500f) return "半干旱带";
			if (mmPerYear < 1500f) return "湿润带";
			return "多雨带";
		}

		public override IReadOnlyList<TileInfoEntry> TileInfo(int cellIndex)
		{
			float mm = _crust.PrecipMmYear[cellIndex];
			return new List<TileInfoEntry>
			{
				new("年降水", $"{mm:F0} mm/yr", CellColorAt(cellIndex)),
				new("干湿", AridityZoneName(mm)),
			};
		}
	}
}
