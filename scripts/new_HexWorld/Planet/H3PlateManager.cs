using World.NewHexWorld.Plate;

namespace World.NewHexWorld.Planet
{
	// 内容编排（板块层）：H3Plate 静态生成（胞切分+归并+海陆+两级料场/海拔）+ 板统计
	// （板格数/陆洋格数）；派生查询口供 UI/VM 使用。seed 决定整颗星球的板块格局。
	public class H3PlateManager
	{
		H3Plate _plate;
		int[] _plateCounts;        // 每板块格数（Init 后有效；信息面板"占比"用）
		int _landCellCount;        // 陆格数（陆性板格合计；LandFrac 概率 + 板大小 → 有涨落）

		// 板块静态场（PlateId 等六场，下标与 Ball.CellIds 对齐）。
		public H3Plate Plate => _plate;

		// 每板块格数（下标 = 板 Id）。
		public int[] PlateCounts => _plateCounts;

		public int NumPlates { get; private set; }

		// 陆/洋格数（占比 = 值 / 总格数；总格数取 PlateId.Length）。
		public int LandCellCount => _landCellCount;
		public int OceanCellCount => _plate.Crust.PlateId.Length - _landCellCount;
		public int CellCount => _plate.Crust.PlateId.Length;

		// 板陆性查询（海陆 = 板块属性：陆格 ⟺ 其板 IsLand）。
		public bool IsPlateLand(int plate) => _plate.Plates[plate].IsLand;

		// 格归属板号（越界即抛：下标须来自 Ball 对齐）。
		public int PlateOfCell(int cellIndex) => _plate.Crust.PlateId[cellIndex];

		// 建板 + 胞切分归并 + 海陆赋性 + 两级料场/海拔 + 统计。seed 定胞心抽取/生长/陆性，全局定局。
		public void Init(Ball ball, int numPlates, int seed, float landFrac = 1f / 3f)
		{
			NumPlates = numPlates;
			_plate = new H3Plate(ball);
			_plate.CreatePlates(numPlates, seed, landFrac);

			_plateCounts = new int[numPlates];
			_landCellCount = 0;
			var crust = _plate.Crust;
			for (int i = 0; i < crust.PlateId.Length; i++)
			{
				int p = crust.PlateId[i];
				_plateCounts[p]++;
				if (_plate.Plates[p].IsLand) _landCellCount++;
			}
		}
	}
}
