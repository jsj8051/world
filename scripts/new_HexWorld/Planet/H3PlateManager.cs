using World.NewHexWorld.Plate;

namespace World.NewHexWorld.Planet
{

	// 内容编排（板块层）：H3Plate + Init 分板 + 每板格数统计；显示/面板的查询口。
	public class H3PlateManager
	{
		H3Plate _plate;
		int[] _plateCounts;

		// 板块模拟（PlateId 场：每格归属板，下标与 Ball.CellIds 对齐）。
		public H3Plate Plate => _plate;

		// 每板块格数（Init 后有效；信息面板"占比"用）。
		public int[] PlateCounts => _plateCounts;

		public int NumPlates { get; private set; }

		// 建板 + Voronoi 分板块 + 统计每板格数。seed 决定整颗星球的板块格局。
		public void Init(Ball ball, int numPlates, int seed)
		{
			NumPlates = numPlates;
			_plate = new H3Plate(numPlates, ball);
			_plate.CreatePlates(seed);

			_plateCounts = new int[numPlates];
			foreach (int p in _plate.PlateId) _plateCounts[p]++;
		}
	}
}
