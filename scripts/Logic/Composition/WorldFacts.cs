using System;
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

	/// <summary>逐格年降水（mm/年）。</summary>
	public float[] PrecipitationMm { get; }

	/// <summary>逐格年均温（℃）。</summary>
	public float[] TemperatureC { get; }

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

		HeightM = Aligned(terrain.HeightM, n, nameof(HeightM));
		FinalLand = Aligned(terrain.FinalLand, n, nameof(FinalLand));
		FinalLandFraction = Aligned(terrain.FinalLandFraction, n, nameof(FinalLandFraction));
		FinalLandmassId = Aligned(terrain.FinalLandmassId, n, nameof(FinalLandmassId));
		FinalLandmassCount = terrain.FinalLandmassCount;
		FinalDistToCoast = Aligned(terrain.FinalDistToCoast, n, nameof(FinalDistToCoast));
		FinalDistToLand = Aligned(terrain.FinalDistToLand, n, nameof(FinalDistToLand));

		var precipitation = Present(climate.Precipitation, "降水场");
		PrecipitationMm = Aligned(precipitation.AnnualMm, n, nameof(PrecipitationMm));
		var temperature = Present(climate.Temperature, "温度场");
		TemperatureC = Aligned(temperature.CellTemperatureC, n, nameof(TemperatureC));

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

	static T Present<T>(T value, string name) where T : class
		=> value ?? throw new InvalidOperationException($"世界事实缺 {name}：对应阶段未产出");
}
