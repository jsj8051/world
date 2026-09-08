using System.Collections.Generic;
using Godot;
using World.MapView;                       // TileInfoEntry
using World.NewHexWorld.Plate;             // Crust

namespace World.NewHexWorld.UI.Modes
{
	// 模式 0 海陆（默认模式；设计入口 §2.5 清单）：格色 = 海陆二元色（FelsicThick>0 判陆）。
	// 海陆 = 板块属性 → 本模式即"板块海陆观"，叠加板块边界线（海岸线即板边界线）。
	// 色值初值 = 用户拍板方案 A（09-07）：与海拔色带同源衔接（海 = 老树 SeaColor 同款深蓝、
	// 陆 = 海拔带 500m 浅绿）——判读后随时调这两常量。
	public sealed class LandSeaMapMode : MapMode
	{
		/// <summary>海洋色（方案 A：老树 MapLayerColors.SeaColor 同款深蓝）。</summary>
		public static readonly Color OceanColor = new(0.10f, 0.22f, 0.48f);
		/// <summary>陆地色（方案 A：海拔色带 500m 停点同款浅绿——陆 800m 平均落点观感衔接）。</summary>
		public static readonly Color LandColor = new(0.58f, 0.78f, 0.32f);

		readonly Crust _crust;

		public LandSeaMapMode(Crust crust)
		{
			_crust = crust;
		}

		public override int Id => 0;
		public override string Name => "海陆";
		public override bool ShowPlateBoundaries => true;

		// 判陆 = Crust.IsLand 统一口（长英质厚 > 0；洋性板恒 0 → 二元跳变无中间态）
		public override Color CellColorAt(int cellIndex)
			=> _crust.IsLand(cellIndex) ? LandColor : OceanColor;

		public override IReadOnlyList<TileInfoEntry> TileInfo(int cellIndex)
		{
			bool land = _crust.IsLand(cellIndex);
			return new List<TileInfoEntry>
			{
				new("类型", land ? "陆地" : "海洋", CellColorAt(cellIndex)),
			};
		}
	}
}
