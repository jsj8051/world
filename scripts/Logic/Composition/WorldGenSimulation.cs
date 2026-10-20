using World.H3Grid;
using World.Data;

namespace World.Logic;

/// <summary>
/// 世界生成空间 · 纯逻辑入口（非 Godot 节点）：独占球壳数据层与六个阶段管线，按固定因果序驱动。
/// 引擎耦合必须为零——不得出现 <c>GD.*</c> / <c>Node*</c> / <c>Godot.Color</c> / <c>Mathf</c>。
/// 全量重算复用同一实例再 <c>Run()</c>，不要 new；参数一律由 <c>Run()</c> 接收。
/// </summary>
public sealed class WorldGenSimulation
{
	/// <summary>球壳数据层（H3）。只读口，供表现层判读。</summary>
	public Ball Ball { get; private set; }

	/// <summary>⓪ 板块（划分 + 壳型）。</summary>
	public PlatePipeline Plate { get; } = new();
	/// <summary>① 地形（最终高度）。</summary>
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
	public WorldGenSimulation(int resLevel, float radius)
	{
		Ball = new Ball(resLevel, radius);
	}

	/// <summary>全量重算。六阶段按固定因果序执行，顺序不可换（后段吃前段产物）：
	/// ⓪ 板块（含壳型）→ ① 地形 → ② 世界事实 → ③ 气候 → ④ 水文 → ⑤ 空间索引。
	/// 本方法是唯一把全量 <see cref="WorldSpec"/> 交给 ⓪ 的地方（⓪ 要定布局档），其余阶段按段切片下传。</summary>
	/// <param name="spec">世界定义（见 <see cref="WorldSpec"/>）。</param>
	public void Run(WorldSpec spec)
	{
		Plate.Run(Ball, spec);
		float seaM = spec.Terrain.SeaLevelM;                                        // 海陆基准（② / ④ 共用）
		Terrain.Run(Ball, spec.Seed, spec.Terrain, Plate.PlateOfCell, Plate.Plates, Plate.LandBaseDropM);
		Facts.Run(Ball, Terrain.HeightM, seaM);
		Climate.Run(Ball, Facts.Final, Terrain.HeightM);
		Hydrology.Run(Ball, Facts.Final, Terrain.HeightM, seaM, Climate.Precipitation);
		Index.Run(Ball, Facts.Final, Hydrology.Rivers);
	}
}
