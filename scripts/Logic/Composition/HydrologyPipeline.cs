using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **⑤ 水文阶段**（World Simulation·水文）：河网 → 河图/河线 → 流域 → 原始水文 → 湖泊 → 水系。
///
/// ★四个水系统概念**不互相吞并**（架构 §2.2）：河网拓扑 / 流域归属 / 湖泊状态 / 水体连接。
/// ★`Basins`（路由表面上的流域）与 `DepressionBasins`（原始地形洼地）**并列且不同**——
///   填洼只改变"水怎么走"，不改变"湖是否存在"。
/// </summary>
public sealed class HydrologyPipeline
{
	public RiverNetwork Rivers { get; private set; }      // 河网
	public RiverGraph RiverTopology { get; private set; } // 河网图（River 2B 水文事实）
	public RiverGeometry RiverLines { get; private set; } // 连续河线（图的表达，不改图）
	public BasinGraph Basins { get; private set; }        // 流域拓扑（River 2C-A；**不判湖**）
	public H3Hydrology RawHydro { get; private set; }     // 原始高度上的汇流（D-16）
	/// <summary>原始地形的内流洼地分区（D-16：湖泊存在的判据来源）。</summary>
	public BasinGraph DepressionBasins { get; private set; }
	public LakeState Lakes { get; private set; }          // 湖泊状态层（River 2C-B：水量平衡）
	public WaterTopology WaterSystem { get; private set; } // 水系拓扑（River 2C-C：组合层）

	public void Run(Ball ball, FinalGeography final, HeightComposer composer,
		PrecipitationModel precipitation)
	{
		Rivers = new RiverNetwork();
		Rivers.Generate(ball, final, composer, annualPrecipMm: precipitation.AnnualMm);
		// River 2B：水文事实（图）与几何表现（连续河线）分离——线只表达图，不改图
		RiverTopology = new RiverGraph();
		RiverTopology.Generate(ball, final, Rivers);
		RiverLines = new RiverGeometry();
		RiverLines.Generate(ball, RiverTopology, Rivers);

		// River 2C-A：流域拓扑（只读 flow graph 的终止事实；本轮不判湖，Lake 属 2C-B）
		Basins = new BasinGraph();
		Basins.Generate(ball, final, Rivers);

		// ── D-16（2026-10-04）：原始地形的内流洼地分区 ──
		//   Raw FinalHeight ─┬─ DetectDepressions ──→ LakeState（湖是否存在）
		//                    └─ HydrologyRoutingSurface ──→ Flow/Basin/Accum（水怎么走）
		// ★填洼只改变"水怎么走"，不改变"湖是否存在" ⇒ LakeState 绝不能从 `Basins`
		//   （路由表面上的流域）派生，否则开启填洼后内流湖会全部消失（实测 3,671 → 0）。
		RawHydro = new H3Hydrology();
		RawHydro.Generate(ball, composer.HeightM, 0f, precipitation.AnnualMm);
		DepressionBasins = new BasinGraph();
		DepressionBasins.Generate(ball, final, RawHydro.Downstream, RawHydro.WeightedAccum);

		// River 2C-B：湖泊状态层（水量平衡；只消费原始洼地分区/降水/地形，不回写任何冻结层）
		Lakes = new LakeState();
		Lakes.Generate(ball, final, composer.HeightM, DepressionBasins, precipitation.AnnualMm);
		// River 2C-C：水系拓扑（组合层——只连接既有水体，不改 River/Basin/Lake 任何事实）
		WaterSystem = new WaterTopology();
		WaterSystem.Generate(ball, final, Rivers, RiverTopology, Basins, Lakes);
	}
}
