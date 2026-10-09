using Godot;                       // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using System;
using System.Collections.Generic;
using System.Diagnostics;
using World.H3Grid;               // Ball（H3 球壳数据层）

namespace World.WorldGen;

/// <summary>
/// 世界生成空间 · **纯逻辑入口**（2026-10-09 用户拍板拆层；非 Godot 节点）。
///
/// ★定位：世界生成空间的"**逻辑那一半**"——独占球壳数据层与**全部**生成产物，并按固定因果
///   序列把 5 个子层跑完。原本这两件事与"Godot 接线"混在 `WorldGenPlanet : Node3D` 里，
///   现按其**依赖的运行时**切开：
///     · 本类 = 引擎无关的领域状态 + 管线序列（可在无 Godot 宿主下实例化，测试/工具可直驱）；
///     · `WorldGenPlanet` = 场景接线（导出参数 / 建视图 / 挂节点 / 打印）。
///   判据 = "这段代码需要 Godot 运行时行为吗？"——需要就留节点，不需要就归本类。
///
/// ★引擎耦合必须保持为**零**：本类只用 `Godot.Vector3` 值类型（经 `Ball` 与各子层间接），
///   **不得**出现 `GD.*` / `Node*` / `Godot.Color` / `Mathf`。凡"为了打印或场景操作"的改动
///   一律留在 `WorldGenPlanet`，不得下沉到这里。
///
/// ★实例生命周期（**重要**）：全量重算 = **复用同一实例再 `Run()`**，不要 new 新实例——
///   地图模式类持本实例引用、在 `CellColorAt` 里"现取"字段；换实例会让它们读到旧场。
///   `Run()` 内部替换的是各**字段的数组实例**，字段本身始终在当前实例上。
///
/// ★参数注入：生产分辨率/球半径/seed/大陆数/陆占比/区域粒度**由装配层传入**，本类不读任何
///   场景导出值，也不引用 `WorldGenPlanet`（依赖方向单向：`WorldGenPlanet → WorldGenSimulation`）。
/// </summary>
public sealed class WorldGenSimulation
{
	/// <summary>
	/// **球壳数据层**（H3 球壳数据层）。只读口，不授予任何生成/回写能力；
	/// 表现层判读用（格心方向 CellDirs → 切平面北向基准）。
	/// </summary>
	public Ball Ball { get; private set; }

	public ContinentLayout Layout { get; private set; }     // 大陆锚点（蓝噪声 + 属性）
	public LandSeaField Field { get; private set; }         // 连续海陆场（域扭曲 + 三尺度）
	public H3LandSeaProjector Projector { get; private set; }   // H3 投影五件套
	public GeologicalRegions Regions { get; private set; }  // 地质区域（阶段 3：两级 Voronoi + 约束式类型）
	public MountainSkeleton Mountains { get; private set; } // 山脉骨架（阶段 4：主脊+分支 → 高斯包络 × ridged 细化）
	public RegionalLandforms Landforms { get; private set; }    // 区域地貌（阶段 5：高原帽 / 盆地下挖）
	public HeightComposer Composer { get; private set; }    // 高度合成（FinalHeight 单一事实源）
	public SurfaceResolver Surface { get; private set; }    // 唯一海陆口径（Placement 阶段）
	public FinalGeography Final { get; private set; }      // 最终地理（FinalLand/Landmass/Region/Coast——世界事实层）
	public VolcanoField Volcanoes { get; private set; }    // 火山（Feature 实证第一例；决策 08 冻结后首例）
	public PrecipitationModel Precipitation { get; private set; }  // 降水模型（River 2A：World Simulation 输入）
	public Temperature Temperature { get; private set; }          // 温度场（v1：逐格能量收支，输入按纬度 cos φ）
	public RiverNetwork Rivers { get; private set; }      // 河网（World Simulation 第一下游消费者；只读 Final 层）
	public RiverGraph RiverTopology { get; private set; } // 河网图（River 2B 水文事实：source/汇流/outlet/干支流）
	public RiverGeometry RiverLines { get; private set; } // 连续河线（River 2B 几何表现：图的表达，不改图）
	public BasinGraph Basins { get; private set; }        // 流域拓扑（River 2C-A：Ocean/Endorheic + BasinId；**不判湖**）
	/// <summary>
	/// **原始地形的内流洼地分区**（D-16）。与 `Basins`（水文路由表面上的流域）**并列且不同**：
	/// 它始终由 `Composer.HeightM`（未填洼）算出，故开启填洼时 `LakeState` 的湖不会消失。
	/// </summary>
	public BasinGraph DepressionBasins { get; private set; }
	/// <summary>原始高度上的汇流（D-16：内流洼地分区的输入；**不是**水文路由）。</summary>
	public H3Hydrology RawHydro { get; private set; }
	public LakeState Lakes { get; private set; }          // 湖泊状态层（River 2C-B：水量平衡；与 Basin 并列，不改拓扑）
	public WaterTopology WaterSystem { get; private set; } // 水系拓扑（River 2C-C：水体之间的连接；组合层，不重新定义河流/湖泊）
	public FinalSpatialIndex Index { get; private set; }   // Final 空间索引（#13：nearest/distance/within 查询基础设施）
	/// <summary>显示海拔（= Composer.HeightM；信息卡/判读口）。</summary>
	public float[] DisplayElevation { get; private set; } = Array.Empty<float>();

	readonly int _seed;
	readonly int _continentCount;
	readonly float _landFraction;
	readonly float _targetRegionAreaKm2;

	readonly List<string> _stageMs = new();   // 分阶段耗时（启动卡点定位；随 WORLDGEN-TIMING 行输出，非生产指标）
	readonly Stopwatch _total = new();

	/// <summary>分阶段耗时（**非生产指标**，只喂 `[WORLDGEN-TIMING]` 行；不进任何领域计算）。
	/// 由装配层读取打印——本类不做任何输出（无 `GD.*`）。</summary>
	public IReadOnlyList<string> StageTimings => _stageMs;

	/// <summary>上一次 `Run()` 的总耗时 ms（**非生产指标**，同 <see cref="StageTimings"/>）。</summary>
	public double TotalMs => _total.Elapsed.TotalMilliseconds;

	/// <param name="resLevel">世界空间离散档（生产 = res4 冻结，由装配层传 `WorldGenPlanet.ProductionRes`）。</param>
	/// <param name="radius">球半径（表现/几何尺度；与相机取景同值时取景正确）。</param>
	/// <param name="seed">世界种子（决定全部确定性生成）。</param>
	/// <param name="continentCount">大陆锚点数（海陆场塑形用）。</param>
	/// <param name="landFraction">目标陆地占比（分位校准钉死）。</param>
	/// <param name="targetRegionAreaKm2">地质区域粒度（km²/区域）。</param>
	public WorldGenSimulation(int resLevel, float radius, int seed, int continentCount,
		float landFraction, float targetRegionAreaKm2)
	{
		Stage("Ball", () => Ball = new Ball(resLevel, radius));
		_seed = seed;
		_continentCount = continentCount;
		_landFraction = landFraction;
		_targetRegionAreaKm2 = targetRegionAreaKm2;
	}

	/// <summary>分阶段计时（启动卡点定位；只喂 WORLDGEN-TIMING 行，不改任何生成逻辑）。</summary>
	void Stage(string name, Action a)
	{
		var s = Stopwatch.StartNew();
		a();
		_stageMs.Add($"{name}={s.Elapsed.TotalMilliseconds:F0}ms");
	}

	/// <summary>
	/// **全量重算**（锚点 → 场 → 投影 → 区域 → 骨架 → 地貌 → 合成 → 最终地理 → 降水 → 温度
	/// → 河网/图/几何 → 流域/原始水文 → 湖泊/水系/空间索引）。
	/// ★只重算数据，不碰任何表现层（视图重烘与河线重建由 `WorldGenPlanet` 负责）。
	/// ★可重复调用：复用同一实例，字段数组实例被替换（见类注释的"实例生命周期"）。
	/// </summary>
	public void Run()
	{
		_total.Restart();
		_stageMs.Clear();
		Stage("Layout+Field", () =>
		{
			Layout = new ContinentLayout(_seed, _continentCount);
			Field = new LandSeaField(Layout, new LandSeaParams { Seed = _seed });
		});
		Stage("Projector", () =>
		{
			Projector = new H3LandSeaProjector();
			Projector.Generate(Ball, Field, _landFraction);
		});
		// 唯一海陆口径（决策 07 步骤③）：SurfaceResolver 收拢 Placement 阶段判断
		Stage("Regions", () =>
		{
			Surface = new SurfaceResolver(Field, Projector.ThresholdUsed, Projector.SeaSpreadUsed);
			Regions = new GeologicalRegions(_seed);
			Regions.Generate(Ball, Projector, _targetRegionAreaKm2);
		});
		Stage("Mountains", () =>
		{
			Mountains = new MountainSkeleton(_seed);
			Mountains.Generate(Ball, Regions, Surface);
		});
		Stage("Landforms", () =>
		{
			Landforms = new RegionalLandforms(_seed);
			Landforms.Generate(Ball, Regions);
		});
		Stage("Volcanoes", () =>
		{
			Volcanoes = new VolcanoField();
			Volcanoes.Place(Ball, Regions, TectonicOf(Mountains), _seed, Mountains.RangeAnchors());
		});

		Stage("HeightComposer", () =>
		{
			// 特征场列表（架构 v1 冻结：新特征 = 实现 ITerrainField + 插入本列表——
			// 合成器/FinalGeography 零改动；列表序 = 优先级序，后位覆盖先位）
			var features = new List<FeatureField>
			{
				new(Landforms, TerrainDomain.LandOnly),      // Plateau(2)/Basin(3) 组合口
				new(Mountains, TerrainDomain.LandAndSea),    // Mountain(4)：跨海（海底脊/海山）
				new(Volcanoes, TerrainDomain.LandAndSea),    // Volcano(4.5)：最局部，后位覆盖
			};
			Composer = new HeightComposer(_seed);
			Composer.Generate(Ball, Surface, Regions, features);
			DisplayElevation = Composer.HeightM;   // 最终高度（决策 07 ③）
		});
		Stage("FinalGeography", () =>
		{
			// 最终地理（决策 07 ④⑤）：FinalLand/Landmass/Region/Coast 全部由最终高度派生
			Final = new FinalGeography();
			Final.Generate(Ball, Composer, Regions);
		});
		// ── 降水生产线（P4-2：ZonalMm × MaritimeFactor；River 2A / 湖泊的输入）──
		Stage("Precipitation", () =>
		{
			Precipitation = new PrecipitationModel();
			Precipitation.Generate(Ball, Final);
		});
		// ── 温度场（v1：逐格能量收支，输入按纬度 cos φ；消费 Composer.HeightM + Ball.Geo）──
		Stage("Temperature", () =>
		{
			Temperature = new Temperature();
			Temperature.Generate(Ball, Composer, Final);
		});
		Stage("Rivers+Graph+Geometry", () =>
		{
			Rivers = new RiverNetwork();
			Rivers.Generate(Ball, Final, Composer, annualPrecipMm: Precipitation.AnnualMm);
			// River 2B：水文事实（图）与几何表现（连续河线）分离——线只表达图，不改图
			RiverTopology = new RiverGraph();
			RiverTopology.Generate(Ball, Final, Rivers);
			RiverLines = new RiverGeometry();
			RiverLines.Generate(Ball, RiverTopology, Rivers);
		});
		Stage("Basins+RawHydro", () =>
		{
			// River 2C-A：流域拓扑（只读 flow graph 的终止事实；**本轮不判湖**，Lake 属 2C-B）
			Basins = new BasinGraph();
			Basins.Generate(Ball, Final, Rivers);
			// ── D-16（2026-10-04）：**原始地形的内流洼地分区** ────────────────────────
			//   Raw FinalHeight ─┬─ DetectDepressions（本段）──→ LakeState（湖是否存在）
			//                    └─ HydrologyRoutingSurface ──→ FlowDirection/Basin/FlowAccum（水怎么走）
			// ★填洼只改变"水怎么走"，不改变"湖是否存在" ⇒ LakeState 绝不能从 `Basins`
			//   （路由表面上的流域）派生，否则开启填洼后内流湖会全部消失（实测 3,671 → 0）。
			RawHydro = new H3Hydrology();
			RawHydro.Generate(Ball, Composer.HeightM, 0f, Precipitation.AnnualMm);
			DepressionBasins = new BasinGraph();
			DepressionBasins.Generate(Ball, Final, RawHydro.Downstream, RawHydro.WeightedAccum);
		});
		Stage("Lakes+WaterSystem+SpatialIndex", () =>
		{
			// River 2C-B：湖泊状态层（水量平衡；只消费原始洼地分区/降水/地形，**不回写任何冻结层**）
			Lakes = new LakeState();
			Lakes.Generate(Ball, Final, Composer.HeightM, DepressionBasins, Precipitation.AnnualMm);
			// River 2C-C：水系拓扑（组合层——只连接既有水体，不改 River/Basin/Lake 任何事实）
			WaterSystem = new WaterTopology();
			WaterSystem.Generate(Ball, Final, Rivers, RiverTopology, Basins, Lakes);
			// Final 空间索引（#13 v1：Final World 事实的 nearest/distance/within 查询基础设施）
			var mountainAnchors = new List<Vector3>();
			foreach (var sys in Mountains.Systems) mountainAnchors.Add(sys.Anchor);
			Index = new FinalSpatialIndex(Ball, mountainAnchors,
				Volcanoes.Volcanoes.ConvertAll(v => v.Anchor));
			var riverCells = new List<int>();
			for (int i = 0; i < Rivers.IsRiver.Length; i++)
				if (Rivers.IsRiver[i]) riverCells.Add(i);
			Index.Generate(Final, riverCells);
		});
	}

	static TectonicField TectonicOf(MountainSkeleton m) => m.Tectonic;
}
