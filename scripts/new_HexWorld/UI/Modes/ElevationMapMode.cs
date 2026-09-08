using System.Collections.Generic;
using Godot;
using World.MapView;                       // TileInfoEntry
using World.NewHexWorld.Plate;             // Crust
using static World.Utils.ColorRamp;        // RampSampleSmooth（通用连续色带算法，复用不复制）

namespace World.NewHexWorld.UI.Modes
{
	// 模式 1 海拔（设计-01 §5A）：照搬老树 ElevationLayer（2026-08-31 ISO 9241-307 版色带）——
	// 色带停点与分带函数迁至此文件内聚（新世界独立演进，老树零改动）；连续色带算法用
	// World.Utils.ColorRamp.RampSampleSmooth（同位置双停点 = 0m 海陆硬台阶）。
	// 数据源 = Crust.Elevation（米，0=海平面）。本阶段初值两级常量（陆 +800 / 洋 −3700）→
	// 色带呈"两档主体色 + 0m 硬台阶"；将来要素生成器加起伏后自然出连续层次（同源无需改）。
	// 老树海冰判据（温度 ≤−5°C）不搬——new_HexWorld 无温度场。
	public sealed class ElevationMapMode : MapMode
	{
		/// <summary>海拔连续色带（位置=米，0=海平面；升序：海沟 → 海面 → 高山）。停点照搬老树
		/// ElevationLayer.ElevationStops（2026-08-31 用户拍板版）：海洋冷色系越深越暗
		/// （-8000 墨紫黑 / -6000 靛蓝 #172B4F / -2000 深蓝 #2F5B8A / -200 大陆架灰蓝 #7BA5C4 /
		/// -50 潮间带 #C6DCEB / 0 海面青白 #E7F0F6）；0m 硬台阶色相切 → 天蓝 (0.45,0.75,0.90)；
		/// 陆地暖色系越高越亮（500 浅绿 / 2000 金黄 / 5000 赭石 / 6000 白，>6000 恒白）。
		/// 【改色带】= 编辑本表（同位置双停点 = 硬台阶，异位置 = 平滑渐变）。</summary>
		public static readonly ColorStop[] ElevationStops =
		{
			new(-8000f, new Color(0.043f, 0.055f, 0.133f)),  // 海沟/深渊最暗（墨紫黑）
			new(-6000f, new Color(0.090f, 0.169f, 0.310f)),  // 深海平原底（靛蓝 #172B4F）
			new(-2000f, new Color(0.184f, 0.357f, 0.541f)),  // 大陆坡底（深蓝偏靛 #2F5B8A）
			new(-200f,  new Color(0.482f, 0.647f, 0.769f)),  // 大陆架（灰蓝 #7BA5C4）
			new(-50f,   new Color(0.776f, 0.863f, 0.922f)),  // 潮间带（浅灰蓝 #C6DCEB）
			new(0f,     new Color(0.906f, 0.941f, 0.965f)),  // 海面（青白 #E7F0F6——低饱和灰蓝）
			new(0f,     new Color(0.45f, 0.75f, 0.90f)),     // └ 0m 硬台阶：海洋青白 → 陆地天蓝（色相切）
			new(500f,   new Color(0.58f, 0.78f, 0.32f)),     // 浅绿（低海拔末端 / 中海拔起点）
			new(2000f,  new Color(0.93f, 0.78f, 0.25f)),     // 金黄（中海拔末端）
			new(5000f,  new Color(0.55f, 0.36f, 0.20f)),     // 赭石（高海拔末端）
			new(6000f,  new Color(0.98f, 0.99f, 1.00f)),     // 纯白（极高山区高光；>6000m 恒白）
		};

		readonly Crust _crust;

		public ElevationMapMode(Crust crust)
		{
			_crust = crust;
		}

		public override int Id => 1;
		public override string Name => "海拔";
		public override bool ShowPlateBoundaries => true;

		// 取色 = 平滑采样（海洋/陆地同一函数；0m 双停点自然出硬台阶——海岸线即 0m 线，与边界线层双重视觉对照）
		public override Color CellColorAt(int cellIndex)
			=> RampSampleSmooth(ElevationStops, _crust.Elevation[cellIndex]);

		/// <summary>海拔分带名（与停点断点同源，照搬老树 ElevationLayer.ElevationZoneName）：海
		/// &lt;-6000 海沟 / -2000~-6000 深海平原 / -200~-2000 大陆坡 / -50~-200 大陆架 / 0~-50 潮间带；
		/// 陆 0~500 低海拔 / 500~2000 中海拔 / 2000~5000 高海拔 / &gt;5000 极高山区。
		/// 边界归属右侧段（与色带半开区间一致）。</summary>
		public static string ElevationZoneName(float elevM)
		{
			if (elevM < 0f)
			{
				if (elevM < -6000f) return "海沟";
				if (elevM < -2000f) return "深海平原";
				if (elevM < -200f) return "大陆坡";
				if (elevM < -50f) return "大陆架";
				return "潮间带";
			}
			if (elevM < 500f) return "低海拔";
			if (elevM < 2000f) return "中海拔";
			if (elevM < 5000f) return "高海拔";
			return "极高山区";
		}

		public override IReadOnlyList<TileInfoEntry> TileInfo(int cellIndex)
		{
			float elevM = _crust.Elevation[cellIndex];
			bool land = _crust.IsLand(cellIndex);   // 判陆 = Crust.IsLand 统一口（海拔只用于取色/显示，不做陆海判定）
			return new List<TileInfoEntry>
			{
				new("高度", $"{elevM:F0} m（0=海平面）", CellColorAt(cellIndex)),
				new("类型", (land ? "陆地" : "海洋") + " · " + ElevationZoneName(elevM)),
			};
		}
	}
}
