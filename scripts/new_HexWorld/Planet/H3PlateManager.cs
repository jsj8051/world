using System;
using World.NewHexWorld.Plate;

namespace World.NewHexWorld.Planet
{
	// 内容编排（板块层，设计-03）：H3Plate 动态生成（初始分板 → 600 My 板块演化 → 六场写回）
	// + 板统计（板格数 / 陆洋格数 / 板缘分类边数）；派生查询口供 UI/VM 使用。
	// seed 决定整颗星球的板块格局与地形。
	//
	// ⚠️ 03 起两处口径变更：
	//   ① 陆性不再是"板属性"——动态模型里一块板可以同时有陆有洋（碰撞/裂谷会改陆性），
	//      所以 `LandCellCount` 按**逐格地壳**统计（`Crust.IsLand`），`IsPlateLand` 只作 UI 派生显示；
	//   ② 地形派生口从"逐格地形省份"（02 的参数化带）换成"逐格板缘分类"（动态速度场口径）。
	public class H3PlateManager
	{
		H3Plate _plate;
		int[] _plateCounts;        // 每板块格数（Init 后有效；信息面板"占比"用）
		int _landCellCount;        // 陆格数（逐格地壳判陆）
		int[] _boundaryKindEdgeCounts;   // 板缘四分类边数（下标 = PlateBoundaryKind）

		// 板块场（PlateId 等六场，下标与 Ball.CellIds 对齐）+ 动态模拟本体。
		public H3Plate Plate => _plate;
		public H3DynamicTectonics Simulation => _plate.Simulation;
		public H3PlateBoundary Boundary => _plate.Boundary;

		// 每板块格数（下标 = 板 Id）。
		public int[] PlateCounts => _plateCounts;

		public int NumPlates { get; private set; }

		// 陆/洋格数（占比 = 值 / 总格数；总格数取 PlateId.Length）。
		public int LandCellCount => _landCellCount;
		public int OceanCellCount => _plate.Crust.PlateId.Length - _landCellCount;
		public int CellCount => _plate.Crust.PlateId.Length;

		// 板陆性查询（派生统计：该板过半格为陆 → true；动态模型下仅供显示）。
		public bool IsPlateLand(int plate) => _plate.Plates[plate].IsLand;

		// 格归属板号（越界即抛：下标须来自 Ball 对齐）。
		public int PlateOfCell(int cellIndex) => _plate.Crust.PlateId[cellIndex];

		// ── 板缘派生口（03 §6：喂格信息面板 + 作后续"海沟/弧地形带"批次的挂载口）──

		// 该格主导板缘的运动学类型（0 = 非板缘格）。
		public PlateBoundaryKind KindOfCell(int cellIndex) => _plate.Boundary.Kind[cellIndex];

		// 该格主导边的相对速率（km/My；cm/yr 换算 ×0.1）。
		public float RelativeSpeedKmPerMyOfCell(int cellIndex) => _plate.Boundary.RelativeSpeedKmPerMy[cellIndex];

		// 到最近板缘的格数（板缘格 = 0；-1 = 全球无板缘）。
		public int NearestBoundaryDistanceOfCell(int cellIndex) => _plate.Boundary.NearestBoundaryDistanceCells[cellIndex];

		// 最近板缘的类型（板内格显示用）。
		public PlateBoundaryKind NearestBoundaryKindOfCell(int cellIndex) => _plate.Boundary.NearestBoundaryKind[cellIndex];

		// 板缘四分类边数（统计/诊断用）。
		public int[] BoundaryKindEdgeCounts => _boundaryKindEdgeCounts;

		// 建板 + 初始分板 + 板块演化 + 六场写回 + 统计。seed 定初始分板与初始地壳，全局定局。
		public void Init(Ball ball, int numPlates, int seed, float oceanFraction = 0.6f,
			float landOceanNoiseBlend = 0.7f)
		{
			NumPlates = numPlates;
			_plate = new H3Plate(ball) { LandOceanNoiseBlend = landOceanNoiseBlend };
			_plate.CreatePlates(numPlates, seed, oceanFraction);
			CollectStatistics();
		}

		// ── 分帧生成（04 批次 5）：Begin → 每帧 Advance → Finish；产物与同步 Init 逐位一致 ──

		/// <summary>分帧阶段一：分板 + 模拟初始化（不跑时间步）。</summary>
		public void BeginInit(Ball ball, int numPlates, int seed, float oceanFraction = 0.6f,
			float landOceanNoiseBlend = 0.7f)
		{
			NumPlates = numPlates;
			_plate = new H3Plate(ball) { LandOceanNoiseBlend = landOceanNoiseBlend };
			_plate.BeginCreatePlates(numPlates, seed, oceanFraction);
		}

		/// <summary>分帧阶段二：推进至多 maxSteps 步；返回是否仍需继续。</summary>
		public bool AdvanceInit(int maxSteps = 8) => _plate.AdvanceCreation(maxSteps);

		/// <summary>生成进度 ∈ [0,1]。</summary>
		public float InitProgress => _plate.CreationProgress;

		/// <summary>分帧阶段三：地形带 + 写回 + 板缘分类 + 统计。</summary>
		public void FinishInit()
		{
			_plate.FinishCreatePlates();
			CollectStatistics();
		}

		void CollectStatistics()
		{
			// 表长按终态板表取（04 批次 4：裂解的新板号可 ≥ 初始板数；NumPlates 仍报初始板数）
			int tableLength = Math.Max(NumPlates, _plate.Plates.Length);
			_plateCounts = new int[tableLength];
			_landCellCount = 0;
			var crust = _plate.Crust;
			for (int i = 0; i < crust.PlateId.Length; i++)
			{
				int p = crust.PlateId[i];
				if (p >= 0 && p < tableLength) _plateCounts[p]++;
				if (crust.IsLand(i)) _landCellCount++;
			}
			_boundaryKindEdgeCounts = _plate.Boundary.KindEdgeCounts;
		}
	}
}
