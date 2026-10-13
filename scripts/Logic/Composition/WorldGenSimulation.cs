using World.H3Grid;               // Ball（H3 球壳数据层）
using World.Data;                 // WorldSpec（世界定义参数实例，数据层；唯一实例由参数管理器持有）

namespace World.Logic;

/// <summary>
/// 世界生成空间 · **纯逻辑入口**（非 Godot 节点）：独占球壳数据层 + **六个阶段管线**，
/// 按固定因果序驱动它们。
///
/// 引擎耦合必须为零——不得出现 `GD.*` / `Node*` / `Godot.Color` / `Mathf`。
/// 全量重算 = 复用同一实例再 `Run()`，不要 new。参数一律由 `Run()` 接收。
/// </summary>
public sealed class WorldGenSimulation
{
	/// <summary>球壳数据层（H3）。只读口，供表现层判读。</summary>
	public Ball Ball { get; private set; }

	/// <summary>⓪ 板块（划分 + 陆地掩码）。</summary>
	public PlatePipeline Plate { get; } = new();
	/// <summary>① 区域与地形（地质区域图 + 最终高度）。</summary>
	public TerrainPipeline Terrain { get; } = new();
	/// <summary>② 世界事实（唯一事实源）。</summary>
	public FactsPipeline Facts { get; } = new();
	/// <summary>③ 气候（降水 / 温度）。</summary>
	public ClimatePipeline Climate { get; } = new();
	/// <summary>④ 水文（河网 / 流域 / 湖泊 / 水系）。</summary>
	public HydrologyPipeline Hydrology { get; } = new();
	/// <summary>⑤ 空间索引。</summary>
	public IndexPipeline Index { get; } = new();

	/// <param name="resLevel">世界空间离散档（生产 = res4 冻结）。</param>
	/// <param name="radius">球半径。</param>
	public WorldGenSimulation(int resLevel, float radius)
	{
		Ball = new Ball(resLevel, radius);
	}

	/// <summary>
	/// 全量重算。六个阶段按固定因果序执行——顺序不可换（后段吃前段产物）：
	/// ⓪ 板块（含陆地）→ ① 区域地形 → ② 世界事实 → ③ 气候 → ④ 水文 → ⑤ 空间索引。
	/// 本方法是唯一持有全量 `WorldSpec` 的地方：`spec.Seed` 按值展开成 `int` 逐阶段传，
	/// 各阶段配置按段切片下传（阶段类收不到 `WorldSpec`）。
	/// </summary>
	/// <param name="spec">世界定义（见 <see cref="WorldSpec"/>）。</param>
	public void Run(WorldSpec spec)
	{
		Plate.Run(Ball, spec.Seed, spec.Plate);                                     // ⓪（板块划分 + 陆地掩码）
		Terrain.Run(Ball, spec.Seed, spec.Terrain, Plate.PlacementLand,            // ①
			Plate.PlateOfCell, Plate.Plates);
		Facts.Run(Ball, Terrain.Composer, Terrain.Regions);                         // ②
		Climate.Run(Ball, Facts.Final, Terrain.Composer);                           // ③
		Hydrology.Run(Ball, Facts.Final, Terrain.Composer, Climate.Precipitation);  // ④
		Index.Run(Ball, Facts.Final, Hydrology.Rivers);                             // ⑤
	}
}
