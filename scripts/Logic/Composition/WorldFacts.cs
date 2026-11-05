using System;
using Godot;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 一次全链生成的只读快照：世界事实的唯一取数口（下游只认它，不认阶段怎么分）。
/// 只持有各阶段产物的引用——不存数据、不含算法；每次 <see cref="WorldGenSimulation.Run"/>
/// 重新捕获一份，同一快照里的场必来自同一次生成。捕获即校验：五段齐备、逐格数组与
/// <c>Ball.CellIds</c> 等长，否则抛。
/// </summary>
/// <example><code>
/// sim.Run(spec);
/// float h  = sim.Facts.HeightM[i];           // ① 地形
/// int   d  = sim.Facts.FinalDistToCoast[i];  // ① 世界事实
/// float mm = sim.Facts.PrecipitationMm[i];   // ② 气候
/// </code></example>
public sealed class WorldFacts
{
	/// <summary>球壳数据层（所有逐格数组的下标就是它的 <c>CellIds</c> 序）。</summary>
	public Ball Ball { get; }

	// ── ⓪ 板块 ──

	/// <summary>逐格板块号（全球覆盖，格恒有归属）。</summary>
	public int[] PlateOfCell { get; }

	/// <summary>板块表（壳型 / 欧拉矢量 / 密度）。</summary>
	public Plate[] Plates { get; }

	/// <summary>板块边界集（共享边 + 板块对 + 逐格关联）。</summary>
	public PlateBoundarySet PlateBoundaries { get; }

	/// <summary>板块边界场（逐格运动 / 角色 / 跳数 + 逐边类型）。</summary>
	public PlateBoundaryFields BoundaryFields { get; }

	// ── ① 地形 ──

	/// <summary>逐格最终海拔（米）。</summary>
	public float[] HeightM { get; }

	/// <summary>最终陆海掩码（<c>HeightM &gt; SeaLevelM</c>）。</summary>
	public bool[] FinalLand { get; }

	/// <summary>陆占比 ∈[0,1]（格 + 6 邻居陆占比）。</summary>
	public float[] FinalLandFraction { get; }

	/// <summary>陆块归属（海格 = −1）。</summary>
	public int[] FinalLandmassId { get; }

	public int FinalLandmassCount { get; }

	/// <summary>陆格离海跳数（海格 = 0）。</summary>
	public int[] FinalDistToCoast { get; }

	/// <summary>海格离岸跳数（陆格 = 0）。</summary>
	public int[] FinalDistToLand { get; }

	// ── ② 气候 ──

	/// <summary>逐格年降水（mm/年；= Σ 12 月）。</summary>
	public float[] PrecipitationMm { get; }

	/// <summary>逐月降水（mm/月）：<c>[month][cell]</c>。</summary>
	public float[][] PrecipitationMonthlyMm { get; }

	/// <summary>逐格年均温（℃；= mean 12 月）。</summary>
	public float[] TemperatureC { get; }

	/// <summary>逐月气压（hPa，绝对量）：<c>[month][cell]</c>。气压图的唯一数据源。</summary>
	public float[][] PressureMonthlyHpa { get; }

	/// <summary>逐格年均气压（hPa；= mean 12 月）。</summary>
	public float[] PressureAnnualHpa { get; }

	/// <summary>逐月温度（℃）：<c>[month][cell]</c>，month ∈ [0, <c>TemperatureMonthlyC.Length</c>)。</summary>
	public float[][] TemperatureMonthlyC { get; }

	/// <summary>最热月均温（℃）。</summary>
	public float[] HotMonthC { get; }

	/// <summary>最冷月均温（℃）。</summary>
	public float[] ColdMonthC { get; }

	/// <summary>最干月降水（mm）。</summary>
	public float[] DryMonthMm { get; }

	/// <summary>最干月月份（0–11）。</summary>
	public int[] DryMonthIndex { get; }

	/// <summary>三套风事实（地面 / 低空 / 自由）：同一个气压梯度、三种摩擦闭合，各含逐月 + 年合成。</summary>
	public WindField Wind { get; }

	/// <summary>季风强度 ∈ [0,1]。</summary>
	public float[] MonsoonStrength { get; }

	/// <summary>洋流方向（单位切向量；陆地 / 涡旋中心为零）。</summary>
	public Vector3[] OceanCurrentDirection { get; }

	/// <summary>洋流冷暖 ∈ [−1,1]（+ 暖流 / − 寒流 / 0 陆地）。</summary>
	public float[] OceanCurrentWarmth { get; }

	/// <summary>洋流强度 ∈ [0,1]（0 = 陆地 / 无流）。</summary>
	public float[] OceanCurrentStrength { get; }

	// ── ③ 水文（多字段的图/状态对象按其自身取数，不在此逐字段展开）──

	/// <summary>河网（汇流 / 河格）。</summary>
	public RiverNetwork Rivers { get; }

	/// <summary>河网图（水文事实）。</summary>
	public RiverGraph RiverTopology { get; }

	/// <summary>连续河线（图的表达，不改图）。</summary>
	public RiverGeometry RiverLines { get; }

	/// <summary>流域拓扑（不判湖）。</summary>
	public BasinGraph Basins { get; }

	/// <summary>湖泊状态层（水量平衡）。</summary>
	public LakeState Lakes { get; }

	/// <summary>水系拓扑（组合层）。</summary>
	public WaterTopology WaterSystem { get; }

	// ── ④ 空间索引 ──

	/// <summary>世界事实的 nearest / distance 查询基础设施。</summary>
	public FinalSpatialIndex SpatialIndex { get; }

	// 只由 WorldGenSimulation.Run 捕获：外部拿到的快照必然对应一次跑完的全链。
	internal WorldFacts(Ball ball, PlatePipeline plate, TerrainPipeline terrain, ClimatePipeline climate,
		HydrologyPipeline hydrology, IndexPipeline index)
	{
		Ball = ball ?? throw new ArgumentNullException(nameof(ball));
		int n = Ball.CellIds.Length;

		PlateOfCell = Aligned(plate.PlateOfCell, n, nameof(PlateOfCell));
		Plates = Present(plate.Plates, nameof(Plates));
		PlateBoundaries = Present(plate.Boundaries, "板块边界集");
		BoundaryFields = Present(plate.Boundary, "板块边界场");
		if (PlateBoundaries.CellEdgeStart.Length != n + 1)
			throw new InvalidOperationException(
				$"世界事实 板块边界集 关联表长度 {PlateBoundaries.CellEdgeStart.Length} ≠ 格数+1 {n + 1}");
		if (BoundaryFields.CellMotion.Length != n)
			throw new InvalidOperationException(
				$"世界事实 板块边界场 格数 {BoundaryFields.CellMotion.Length} ≠ {n}");

		HeightM = Aligned(terrain.HeightM, n, nameof(HeightM));
		FinalLand = Aligned(terrain.FinalLand, n, nameof(FinalLand));
		FinalLandFraction = Aligned(terrain.FinalLandFraction, n, nameof(FinalLandFraction));
		FinalLandmassId = Aligned(terrain.FinalLandmassId, n, nameof(FinalLandmassId));
		FinalLandmassCount = terrain.FinalLandmassCount;
		FinalDistToCoast = Aligned(terrain.FinalDistToCoast, n, nameof(FinalDistToCoast));
		FinalDistToLand = Aligned(terrain.FinalDistToLand, n, nameof(FinalDistToLand));

		var precipitation = Present(climate.Precipitation, "降水场");
		PrecipitationMm = Aligned(precipitation.AnnualMm, n, nameof(PrecipitationMm));
		PrecipitationMonthlyMm = AlignedMonthly(precipitation.MonthlyMm, n, "PrecipitationMonthlyMm");

		var temperature = Present(climate.Temperature, "温度场");
		TemperatureC = Aligned(temperature.AnnualC, n, nameof(TemperatureC));
		TemperatureMonthlyC = AlignedMonthly(temperature.MonthlyC, n, "TemperatureMonthlyC");
		HotMonthC = Aligned(temperature.HotMonthC, n, nameof(HotMonthC));
		ColdMonthC = Aligned(temperature.ColdMonthC, n, nameof(ColdMonthC));
		DryMonthMm = Aligned(climate.DryMonthMm, n, nameof(DryMonthMm));
		DryMonthIndex = Aligned(climate.DryMonthIndex, n, nameof(DryMonthIndex));

		var pressure = Present(climate.Pressure, "气压场");
		PressureMonthlyHpa = AlignedMonthly(pressure.MonthlyHpa, n, "PressureMonthlyHpa");
		PressureAnnualHpa = Aligned(pressure.AnnualHpa, n, nameof(PressureAnnualHpa));

		Wind = Present(climate.Wind, "风场");
		if (Wind.Surface.Annual.Length != n)
			throw new InvalidOperationException($"世界事实 Wind 格数 {Wind.Surface.Annual.Length} ≠ {n}");
		if (Wind.MonthCount != TemperatureMonthlyC.Length)
			throw new InvalidOperationException(
				$"世界事实 Wind 月数 {Wind.MonthCount} ≠ 温度月数 {TemperatureMonthlyC.Length}");

		MonsoonStrength = Aligned(climate.MonsoonStrength, n, nameof(MonsoonStrength));

		var ocean = Present(climate.OceanCurrents, "洋流场");
		OceanCurrentDirection = Aligned(ocean.Direction, n, nameof(OceanCurrentDirection));
		OceanCurrentWarmth = Aligned(ocean.Warmth, n, nameof(OceanCurrentWarmth));
		OceanCurrentStrength = Aligned(ocean.Strength, n, nameof(OceanCurrentStrength));

		Rivers = Present(hydrology.Rivers, "河网");
		RiverTopology = Present(hydrology.RiverTopology, "河网图");
		RiverLines = Present(hydrology.RiverLines, "连续河线");
		Basins = Present(hydrology.Basins, "流域拓扑");
		Lakes = Present(hydrology.Lakes, "湖泊状态");
		WaterSystem = Present(hydrology.WaterSystem, "水系拓扑");

		SpatialIndex = Present(index.Index, "空间索引");
	}

	static T[] Aligned<T>(T[] field, int cellCount, string name)
	{
		if (field == null) throw new InvalidOperationException($"世界事实缺 {name}：对应阶段未产出");
		if (field.Length != cellCount)
			throw new InvalidOperationException($"世界事实 {name} 长度 {field.Length} ≠ 格数 {cellCount}");
		return field;
	}

	/// <summary>逐月场对齐校验（外层按月、内层逐格；空月表也算缺事实）。</summary>
	static T[][] AlignedMonthly<T>(T[][] field, int cellCount, string name)
	{
		if (field == null || field.Length == 0)
			throw new InvalidOperationException($"世界事实缺 {name}：对应阶段未产出");
		for (int m = 0; m < field.Length; m++)
		{
			if (field[m] == null || field[m].Length != cellCount)
				throw new InvalidOperationException(
					$"世界事实 {name}[{m}] 长度 {field[m]?.Length ?? 0} ≠ 格数 {cellCount}");
		}
		return field;
	}

	static T Present<T>(T value, string name) where T : class
		=> value ?? throw new InvalidOperationException($"世界事实缺 {name}：对应阶段未产出");
}
