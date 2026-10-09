using World.H3Grid;               // Ball（H3 球壳数据层）
using World.Data;                 // WorldSpec（世界定义：纯数据形状，数据层）

namespace World.WorldGen;

/// <summary>
/// 世界生成空间 · **纯逻辑入口**（非 Godot 节点）：独占球壳数据层 + **六个阶段管线**，
/// 按固定因果序驱动它们。
///
/// ★拆层判据（2026-10-09）：需要 Godot 运行时行为的留 `WorldGenPlanet`，否则归本类。
///   引擎耦合必须为零——不得出现 `GD.*` / `Node*` / `Godot.Color` / `Mathf`。
/// ★实例生命周期：全量重算 = **复用同一实例再 `Run()`**，不要 new——地图模式持本实例引用、
///   在 `CellColorAt` 里现取字段。`Run()` 替换的是各**阶段对象内部的数组实例**。
/// ★参数一律由 `Run()` 接收（本类不自留参数字段），由装配层传入；依赖方向单向
///   `WorldGenPlanet → WorldGenSimulation`。诊断指标（计时等）不进本类——原则 7。
/// ★阶段类只**聚合引用**：逐格数组仍归各子系统类持有（SoA），阶段不复制数据（架构 §2）。
/// </summary>
public sealed class WorldGenSimulation
{
	/// <summary>球壳数据层（H3）。只读口，不授予任何生成/回写能力；
	/// 表现层判读用（格心方向 CellDirs → 切平面北向基准）。</summary>
	public Ball Ball { get; private set; }

	/// <summary>① 海陆骨架（生成依据）。</summary>
	public LandSeaPipeline LandSea { get; } = new();
	/// <summary>② 区域与地形（生成依据 + 最终高度）。</summary>
	public TerrainPipeline Terrain { get; } = new();
	/// <summary>③ 世界事实（唯一事实源）。</summary>
	public FactsPipeline Facts { get; } = new();
	/// <summary>④ 气候（降水 / 温度）。</summary>
	public ClimatePipeline Climate { get; } = new();
	/// <summary>⑤ 水文（河网 / 流域 / 湖泊 / 水系）。</summary>
	public HydrologyPipeline Hydrology { get; } = new();
	/// <summary>⑥ 空间索引。</summary>
	public IndexPipeline Index { get; } = new();

	/// <param name="resLevel">世界空间离散档（生产 = res4 冻结，由装配层传 `WorldGenPlanet.ProductionRes`）。</param>
	/// <param name="radius">球半径（表现/几何尺度；与相机取景同值时取景正确）。</param>
	public WorldGenSimulation(int resLevel, float radius)
	{
		Ball = new Ball(resLevel, radius);
	}

	/// <summary>
	/// **全量重算**。六个阶段按**固定因果序**执行——顺序不可换（后段吃前段产物）：
	/// ① 海陆骨架 → ② 区域地形 → ③ 世界事实 → ④ 气候 → ⑤ 水文 → ⑥ 空间索引。
	/// ★阶段 = **执行分组**（对应冻结的 5 层：Placement / Final / SpatialIndex / World Simulation），
	///   **不是新的数据归属**——各段产物仍归产出它的类持有（SoA，见 `docs/architecture.md` §2）。
	/// ★只重算数据，不碰表现层（视图重烘与河线重建由 `WorldGenPlanet` 负责）。
	/// ★可重复调用：复用同一实例，各阶段内部重建产物。
	///
	/// ★**本方法是唯一持有全量 `WorldSpec` 的地方**（2026-10-10）。它负责按**两层**把参数拆开下传：
	///   · 全局层 `spec.Seed` → **按值展开成 `int`**，逐阶段显式传（想查"哪些阶段用了随机"：
	///     `grep spec.Seed` 即得）；
	///   · 阶段层 `spec.LandSea` / `spec.Terrain` → **按段切片**，只给对应阶段。
	///   ⇒ **阶段类永远收不到 `WorldSpec`**（否则 `ClimatePipeline` 就能读 `spec.Terrain.*`，
	///     阶段边界会从"编译器保证"降级成"靠纪律"）。新增阶段参数照此模式加一段即可。
	/// </summary>
	/// <param name="spec">世界定义（身份 + 分阶段配置，见 <see cref="WorldSpec"/>）。</param>
	public void Run(WorldSpec spec)
	{
		LandSea.Run(Ball, spec.Seed, spec.LandSea);                                 // ①
		Terrain.Run(Ball, spec.Seed, spec.Terrain,                                  // ②
			LandSea.Projector, LandSea.Surface);
		Facts.Run(Ball, Terrain.Composer, Terrain.Regions);                         // ③
		Climate.Run(Ball, Facts.Final, Terrain.Composer);                           // ④
		Hydrology.Run(Ball, Facts.Final, Terrain.Composer, Climate.Precipitation);  // ⑤
		Index.Run(Ball, Facts.Final, Terrain.Mountains, Terrain.Volcanoes,          // ⑥
			Hydrology.Rivers);
	}
}
