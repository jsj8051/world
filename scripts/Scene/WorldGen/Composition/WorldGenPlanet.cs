using Godot;
using System.Collections.Generic;
using System.Diagnostics;
using World.H3Grid;              // Ball（H3 球壳数据层）
using World.Data;                // WorldSpec / LandSeaSpec / TerrainSpec（世界定义：纯数据形状）
using World.Render;               // BallView（决策 08 §4.4 表现层保留资产）

namespace World.WorldGen;

/// <summary>
/// 世界生成空间 · 星球组件（**场景接线层**）：只做"把逻辑入口接到 Godot"——
/// 问参数管理器要参数 → 建 <see cref="WorldGenSimulation"/> → `Run()` → 用结果建视图/河线叠加 → 挂节点 → 打印。
///
/// ★2026-10-09 拆层（用户拍板）：本类原先把"领域状态与管线"和"Godot 接线"混在一起；现按
///   **依赖的运行时**切开——引擎无关的那一半全部下沉到 <see cref="WorldGenSimulation"/>
///   （纯 C#，非节点，可在无 Godot 宿主下实例化）。本类此后**只保留**：
///     ① `[Export]` 场景参数（只剩 `ResLevel` / `Radius` 这类**构造参数**；见下方注释）；
///     ② `Node3D` 生命周期与 Node 树操作（`AddChild`）；
///     ③ 表现层装配（`BallView` / `RiverLineOverlay`）与重烘；
///     ④ 诊断打印（`GD.Print`）。
///   ⇒ 判据：**这段代码需要 Godot 运行时行为吗？** 需要留本类，不需要归 `WorldGenSimulation`。
///
/// ★依赖方向（单向）：`WorldGenPlanet → WorldGenSimulation`。逻辑入口**不认识**本类，
///   也不读任何 `[Export]` 值——世界定义由本类经 <see cref="BuildSpec"/> 显式传入。
/// ⚠️ 星球 Radius 导出与相机 _planetRadius 场景覆写须同值（取景/裁剪 ∝ R）。
/// </summary>
public partial class WorldGenPlanet : Node3D
{
	[ExportGroup("星球")]
	/// <summary>
	/// **世界生产分辨率 = res4（冻结）**（§07 §5.1 F）。res4 是**世界本身的空间离散尺度**
	/// （288,122 格，格边长 ≈26 km），各模拟系统都应适配这个尺度。
	/// ★**不得通过降低生产分辨率来规避某个子系统的尺度适配缺陷**（§07 §5.5）——
	/// 正确处置是让子系统适配 res4（如水文 D-11），否则滑向"不工作就换档"的架构倒退。
	/// res1~res4 同时用作**诊断实验档**（跑尺度行为测试），不是从中挑生产档。
	/// </summary>
	public const int ProductionRes = 4;

	[Export(PropertyHint.Enum, "res2 (5.9k 格)/res3 (41k 格)/res4 (288k 格)")]
	public int ResLevel = ProductionRes;   // 生产默认 = 世界生产分辨率（诊断时可手动切档）
	[Export] public float Radius = 2.0f;        // 球半径（与轨道相机 _planetRadius 同值时取景正确）

	[ExportGroup("LOD 与剔除")]
	[Export] public float LodNearRatio = 6f;         // 相机距 < 球半径×此值 ⇒ 高分辨率面
	[Export] public float BackfaceCullRatio = 3.0f;

	/// <summary>
	/// **纯逻辑入口**（引擎无关：球壳数据层 + 全部生成产物 + 管线序列）。
	/// 全部世界事实（`Final` / `Regions` / `DisplayElevation` / …）都从本入口取：
	/// `planet.Sim.Facts.Final`，而不是在节点上镜像一份。
	/// ★全量重算 = `Sim.Run()`（**复用同一实例**——地图模式持它引用并现取字段）。
	/// </summary>
	public WorldGenSimulation Sim { get; private set; }

	/// <summary>视图（Render.BallView：LOD/剔除/拾取）。表现层资产，由本类建并挂进 Node 树。</summary>
	public BallView View { get; private set; }

	int _timingDiag;   // 生成耗时打印限次
	RiverLineOverlay _riverLines;   // 连续河线叠加（表现层消费端；不改水文/拓扑）

	public override void _Ready()
	{
		Sim = new WorldGenSimulation(ResLevel, Radius);   // 参数不驻留：随 Run() 注入
		Regenerate();
		// 初始地图模式 = 坞首项（WorldGenMapModes 注册序[0] = 海拔）：BallView 不内置默认，
		// 由装配层注入 ⇒ 与 MapDock 初始高亮一致（旧版内置 ElevationBandMode 会与坞高亮不符）。
		var initialMode = WorldGenMapModes.CreateAll(Sim)[0];
		var sView = Stopwatch.StartNew();
		View = new BallView(Sim.Ball, Sim.Terrain.DisplayElevation, initialMode,
			lodNearRatio: LodNearRatio, backfaceCullRatio: BackfaceCullRatio);
		AddChild(View);
		sView.Stop();
		var viewMs = new List<string> { $"ViewCtor={sView.Elapsed.TotalMilliseconds:F0}ms" };

		var sChunks = Stopwatch.StartNew();
		View.BuildChunks();
		sChunks.Stop();
		viewMs.Add($"BuildChunks={sChunks.Elapsed.TotalMilliseconds:F0}ms");

		// 连续河线叠加（表现层；消费 RiverGeometry，不改图）
		_riverLines = new RiverLineOverlay { Name = "RiverLines" };
		View.AddChild(_riverLines);
		var sRiver = Stopwatch.StartNew();
		_riverLines.Build(Sim.Ball, Sim.Hydrology.RiverLines, Sim.Hydrology.RiverTopology);
		sRiver.Stop();
		viewMs.Add($"RiverLinesBuild={sRiver.Elapsed.TotalMilliseconds:F0}ms");
	}
	WorldSpec BuildSpec()
	{
		var spec = WorldParamTable.Load(out var problems);
		foreach (var p in problems) GD.PushWarning($"[WORLDGEN-PARAMS] {p}");
		return spec;
	}

	/// <summary>
	/// 全量重算 + 表现层重烘：把装配层的导出参数经 <see cref="WorldGenSimulation.Run"/> 注入管线，
	/// 然后本层负责把新数组重绑给视图并重建河线（几何/UV 不动）。
	/// ★计时归本层（诊断侧）——纯逻辑入口不持有任何计时设施（原则 7）。
	/// </summary>
	public void Regenerate()
	{
		var sw = Stopwatch.StartNew();
		Sim.Run(BuildSpec());
		sw.Stop();

		if (View != null)
		{
			// 重算换了海拔数组实例 ⇒ 视图海拔源重绑后重烘（几何/UV 不动；海拔 = 合成版）
			View.SetElevationSource(Sim.Terrain.DisplayElevation);
			View.RefreshColors();
		}
		// 表现层消费端（River 2B 收尾）：河线叠加只 RiverGeometry.Lines + RiverGraph.NodeKind，
		// 不改任何水文/拓扑事实
		_riverLines?.Build(Sim.Ball, Sim.Hydrology.RiverLines, Sim.Hydrology.RiverTopology);
	}

	/// <summary>每帧剔除 + LOD 刷新（宿主传当前相机）。</summary>
	public void UpdateVisibility(Camera3D camera)
	{
		View?.UpdateVisibility(camera);
		// v1.2：相机只影响**表现宽度**（单向：Camera → 表现层），不改 chain/档位/任何水文事实
		_riverLines?.UpdateCameraScale(camera);
	}
}
