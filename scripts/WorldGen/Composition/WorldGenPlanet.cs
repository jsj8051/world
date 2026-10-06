using Godot;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using World.Spatial;        // Ball（H3 球壳数据层）
using World.Render;             // BallView（决策 08 §4.4 表现层保留资产）

namespace World.WorldGen;

// 世界生成空间 · 星球组件（阶段 1-4 落地场景件）：数据层（Ball + 大陆布局 + 海陆场 + H3 投影
// + 地质区域 + 山脉骨架）+ 视图层（Render.BallView——海拔源直供构造）。自包含、不含相机。
// 显示海拔 = 投影基线 + 区域类型偏移（阶段 3 占位调制）+ 山脉骨架加成（阶段 4，决策 04）。
public partial class WorldGenPlanet : Node3D
{
	[ExportGroup("星球")]
	/// <summary>
	/// **世界生产分辨率 = res4（冻结）**（用户 2026-10-04 拍板，§07 §5.1 F）。
	/// res4 是**世界本身的空间离散尺度**（288,122 格，格边长 ≈26 km），
	/// 地质 / 地貌 / 水文 / 火山 / 人文等**各模拟系统都应适配这个尺度**。
	///
	/// ★**不得通过降低生产分辨率来规避某个子系统的尺度适配缺陷**（§07 §5.5）：
	/// 实测当前水文模型的**有效工作尺度集中在 res2 附近**（res4 河流格仅占陆地 0.98%），
	/// 但该差异被认定为**水文模型的尺度适配缺陷（D-11）**，
	/// 正确处置是**让水文适配 res4**，而不是把世界降级到 res2。
	/// 否则会滑向"发现某模块在 res4 不工作 → 换 res3 → 另一个不工作 → 换 res2"的架构倒退。
	///
	/// res1~res4 同时用作**诊断实验档**（跑尺度行为测试），不是从中挑生产档。
	/// </summary>
	public const int ProductionRes = 4;

	[Export(PropertyHint.Enum, "res2 (5.9k 格)/res3 (41k 格)/res4 (288k 格)")]
	public int ResLevel = ProductionRes;   // 生产默认 = 世界生产分辨率（诊断时可手动切档）
	[Export] public float Radius = 2.0f;        // 球半径（与轨道相机 _planetRadius 同值时取景正确）
	[Export] public int ContinentCount = 7;     // 大陆锚点数（蓝噪声撒布；海陆场塑形用，地图量=陆块连通分量）
	[Export(PropertyHint.Range, "0.02,0.9,0.01")]
	public float LandFraction = 0.29f;          // 目标陆地占比（分位校准钉死）
	[Export] public int Seed = 42;
	[Export] public float TargetRegionAreaKm2 = GeologicalRegions.TargetRegionAreaKm2;   // 地质区域粒度（km²/区域）

	[ExportGroup("LOD 与剔除")]
	[Export] public float LodNearRatio = 6f;         // 相机距 < 球半径×此值 ⇒ 高分辨率面
	[Export] public float BackfaceCullRatio = 3.0f;

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
	public BallView View { get; private set; }              // 视图（Render.BallView：LOD/剔除/拾取）
	/// <summary>显示海拔（= Composer.HeightM；信息卡/判读口）。</summary>
	public float[] DisplayElevation { get; private set; } = Array.Empty<float>();

	Ball _ball;
	int _timingDiag;   // 生成耗时打印限次
	RiverLineOverlay _riverLines;   // 连续河线叠加（表现层消费端；不改水文/拓扑）

	public override void _Ready()
	{
		_ball = new Ball(ResLevel, Radius);
		Regenerate();
		View = new BallView(_ball, DisplayElevation,
			lodNearRatio: LodNearRatio, backfaceCullRatio: BackfaceCullRatio);
		AddChild(View);
		View.BuildChunks();
		// 连续河线叠加（表现层；消费 RiverGeometry，不改图）
		_riverLines = new RiverLineOverlay { Name = "RiverLines" };
		View.AddChild(_riverLines);
		_riverLines.Build(_ball, RiverLines, RiverTopology);
	}

	/// <summary>全量重算（锚点 → 场 → 投影 → 区域 → 骨架 → 地貌 → 合成）+ 重烘颜色纹理。</summary>
	public void Regenerate()
	{
		var sw = Stopwatch.StartNew();
		Layout = new ContinentLayout(Seed, ContinentCount);
		Field = new LandSeaField(Layout, new LandSeaParams { Seed = Seed });
		Projector = new H3LandSeaProjector();
		Projector.Generate(_ball, Field, LandFraction);
		// 唯一海陆口径（决策 07 步骤③）：SurfaceResolver 收拢 Placement 阶段判断
		Surface = new SurfaceResolver(Field, Projector.ThresholdUsed, Projector.SeaSpreadUsed);
		Regions = new GeologicalRegions(Seed);
		Regions.Generate(_ball, Projector, TargetRegionAreaKm2);
		Mountains = new MountainSkeleton(Seed);
		Mountains.Generate(_ball, Regions, Surface);
		Landforms = new RegionalLandforms(Seed);
		Landforms.Generate(_ball, Regions);
		Volcanoes = new VolcanoField();
		Volcanoes.Place(_ball, Regions, TectonicOf(Mountains), Seed, Mountains.RangeAnchors());

		// 特征场列表（架构 v1 冻结：新特征 = 实现 ITerrainField + 插入本列表——
		// 合成器/FinalGeography 零改动；列表序 = 优先级序，后位覆盖先位）
		var features = new List<FeatureField>
		{
			new(Landforms, TerrainDomain.LandOnly),      // Plateau(2)/Basin(3) 组合口
			new(Mountains, TerrainDomain.LandAndSea),    // Mountain(4)：跨海（海底脊/海山）
			new(Volcanoes, TerrainDomain.LandAndSea),    // Volcano(4.5)：最局部，后位覆盖
		};
		Composer = new HeightComposer(Seed);
		Composer.Generate(_ball, Surface, Regions, features);
		DisplayElevation = Composer.HeightM;   // 最终高度（决策 07 ③）
		// 最终地理（决策 07 ④⑤）：FinalLand/Landmass/Region/Coast 全部由最终高度派生
		Final = new FinalGeography();
		Final.Generate(_ball, Composer, Regions);
		Precipitation = new PrecipitationModel();
		Precipitation.Generate(_ball, Final);
		Rivers = new RiverNetwork();
		Rivers.Generate(_ball, Final, Composer, annualPrecipMm: Precipitation.AnnualMm);
		// River 2B：水文事实（图）与几何表现（连续河线）分离——线只表达图，不改图
		RiverTopology = new RiverGraph();
		RiverTopology.Generate(_ball, Final, Rivers);
		RiverLines = new RiverGeometry();
		RiverLines.Generate(_ball, RiverTopology, Rivers);
		// River 2C-A：流域拓扑（只读 flow graph 的终止事实；**本轮不判湖**，Lake 属 2C-B）
		Basins = new BasinGraph();
		Basins.Generate(_ball, Final, Rivers);
		// ── D-16（2026-10-04）：**原始地形的内流洼地分区** ────────────────────────
		//   Raw FinalHeight ─┬─ DetectDepressions（本段）──→ LakeState（湖是否存在）
		//                    └─ HydrologyRoutingSurface ──→ FlowDirection/Basin/FlowAccum（水怎么走）
		// ★填洼只改变"水怎么走"，不改变"湖是否存在" ⇒ LakeState 绝不能从 `Basins`
		//   （路由表面上的流域）派生，否则开启填洼后内流湖会全部消失（实测 3,671 → 0）。
		RawHydro = new H3Hydrology();
		RawHydro.Generate(_ball, Composer.HeightM, 0f, Precipitation.AnnualMm);
		DepressionBasins = new BasinGraph();
		DepressionBasins.Generate(_ball, Final, RawHydro.Downstream, RawHydro.WeightedAccum);
		// River 2C-B：湖泊状态层（水量平衡；只消费原始洼地分区/降水/地形，**不回写任何冻结层**）
		Lakes = new LakeState();
		Lakes.Generate(_ball, Final, Composer.HeightM, DepressionBasins, Precipitation.AnnualMm);
		// River 2C-C：水系拓扑（组合层——只连接既有水体，不改 River/Basin/Lake 任何事实）
		WaterSystem = new WaterTopology();
		WaterSystem.Generate(_ball, Final, Rivers, RiverTopology, Basins, Lakes);
		// Final 空间索引（#13 v1：Final World 事实的 nearest/distance/within 查询基础设施）
		var mountainAnchors = new System.Collections.Generic.List<Godot.Vector3>();
		foreach (var sys in Mountains.Systems) mountainAnchors.Add(sys.Anchor);
		Index = new FinalSpatialIndex(_ball, mountainAnchors,
			Volcanoes.Volcanoes.ConvertAll(v => v.Anchor));
		var riverCells = new System.Collections.Generic.List<int>();
		for (int i = 0; i < Rivers.IsRiver.Length; i++)
			if (Rivers.IsRiver[i]) riverCells.Add(i);
		Index.Generate(Final, riverCells);


		if (_timingDiag++ < 3)
			GD.Print($"[WORLDGEN-TIMING] n={_ball.CellIds.Length} res={_ball.Res} " +
					 $"land={Projector.LandFraction:P1} regions={Regions.Regions.Length} " +
					 $"ridges={Mountains.Ridges.Length} " +
					 // 2C-A 判读读数：流域总数 / 内流流域数（内流 = 湖泊候选，2C-B 才判定）
					 $"basins={Basins.BasinCount}(endorheic {Basins.EndorheicBasinCount}) " +
					 $"lakes={Lakes.LakeCount} " +
					 $"thr={Projector.ThresholdUsed:F3} {sw.Elapsed.TotalMilliseconds:F0} ms");
		if (View != null)
		{
			// 重算换了海拔数组实例 ⇒ 视图海拔源重绑后重烘（几何/UV 不动；海拔 = 合成版）
			View.SetElevationSource(DisplayElevation);
			View.RefreshColors();
		}
		// 表现层消费端（River 2B 收尾）：河线叠加只 RiverGeometry.Lines + RiverGraph.NodeKind，
		// 不改任何水文/拓扑事实
		_riverLines?.Build(_ball, RiverLines, RiverTopology);
	}

	static TectonicField TectonicOf(MountainSkeleton m) => m.Tectonic;

	/// <summary>每帧剔除 + LOD 刷新（宿主传当前相机）。</summary>
	public void UpdateVisibility(Camera3D camera)
	{
		View?.UpdateVisibility(camera);
		// v1.2：相机只影响**表现宽度**（单向：Camera → 表现层），不改 chain/档位/任何水文事实
		_riverLines?.UpdateCameraScale(camera);
	}
}
