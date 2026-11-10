using Godot;
using System;
using System.IO;
using World.Data;
using World.Data.Maps;
using World.Data.Work;
using World.Utils;
using World.Render;
using World.Logic;

namespace World.Scene;

/// <summary>
/// 场景的世界入口，持有生成实例和可选演化会话。渲染通过世界事实快照读数据。
/// </summary>
public partial class WorldManager : Node3D, ITickable
{
	/// <summary>
	/// 世界生产分辨率 = res4（冻结）：res4 是世界本身的空间离散尺度
	/// （288,122 格、每格 1,770 km²、格边长 ≈26.1 km），各模拟系统都应适配这个尺度。
	/// 不得靠降低生产分辨率规避子系统的尺度适配缺陷——正确处置是让子系统适配 res4，
	/// 否则滑向"不工作就换档"的架构倒退。res1~res4 同时用作诊断实验档，不是从中挑生产档。
	/// </summary>
	public const int ProductionRes = 4;

	/// <summary>生成和演化共用的 H3 分辨率。生产场景使用 res4。</summary>
	[Export(PropertyHint.Range, "0,4,1")] public int WorldResolution { get; set; } = ProductionRes;

	/// <summary>是否启用演化会话，主场景显式启用。</summary>
	[Export] public bool EnableEvolution { get; set; }

	/// <summary>生成后直接演化到指定步，再装配选定时代的完整世界。</summary>
	[Export(PropertyHint.Range, "0,100000,1,or_greater")] public int InitialEvolutionSteps { get; set; }

	WorldEvolutionSession _evolution;
	public EvolutionFrame EvolutionFrame => Sim?.Facts?.Evolution;
	public bool EvolutionBusy => _evolution?.Busy == true;
	public EvolutionControlStatus EvolutionStatus => _evolution?.Status ?? EvolutionControlStatus.Uninitialized;

	/// <summary>当前/最近一次演化计算的信号源；供演化面板的监控器订阅。</summary>
	internal IWorkSignalSource EvolutionSource => _evolution;
	public event Action<WorldFacts> WorldPublished;

	/// <summary>
	/// 纯逻辑入口（引擎无关：球壳数据层 + 全部生成产物 + 管线序列）。全部世界事实都从
	/// <c>Sim.Facts</c> 取（一次生成一份只读快照），不镜像。
	/// 全量重算 = <c>Sim.Run()</c>，复用同一实例——地图模式持它引用并现取字段。
	/// </summary>
	public WorldGenSimulation Sim { get; private set; }

	/// <summary>已初始化过（<see cref="Initialize"/> 的幂等判据；根管理器据此可安全重复调用）。</summary>
	public bool IsInitialized { get; private set; }

	/// <summary>
	/// 世界定义实例（由根管理器注入）：参数实例归 <c>World.Params.WorldParams</c> 管，
	/// 本层不读盘、不造副本。它一直被传给 <c>Sim.Run</c> ⇒ 参数一改、再 <c>Regenerate()</c> 即是新世界。
	/// </summary>
	WorldSpec _spec;

	/// <summary>
	/// 显式初始化：建逻辑入口 + 跑一次全链生成，幂等。
	/// 必须在渲染 / 界面管理器之前——模式表与初始取色都读 <see cref="Sim"/>。
	/// </summary>
	/// <param name="spec">世界定义实例（根管理器从参数管理器取来注入；世界定义只有一份）。</param>
	public void Initialize(WorldSpec spec)
	{
		if (IsInitialized) return;

		_spec = spec;
		Sim = new WorldGenSimulation(WorldResolution, PlanetGeometry.ProductionRadius);
		Regenerate();                // 世界生成（重活在这一步）
		LogGenerationSummary();      // 尺度 / 高程 / 气温的一行体检
		IsInitialized = true;
	}

	/// <summary>
	/// 生成后的一行体检：尺度、陆比与陆地均高（地球 30.6% / 546 m）、气温（全球 / 陆地 / 极区）。
	/// 判读口径，不参与生成；数值偏离量级即参数或尺度失配。
	/// </summary>
	void LogGenerationSummary()
	{
		var facts = Sim.Facts;
		var scale = SpatialScale.Of(Sim.Ball);
		var height = facts.HeightM;
		var land = facts.FinalLand;
		var temperature = facts.TemperatureC;
		var latitude = Sim.Ball.Geo.LatRad;
		int n = height.Length;

		double landHeightSum = 0.0, temperatureSum = 0.0, landTemperatureSum = 0.0, polarTemperatureSum = 0.0;
		int landCount = 0, polarCount = 0;
		float low = height[0], high = height[0], cold = temperature[0], warm = temperature[0];
		for (int i = 0; i < n; i++)
		{
			if (height[i] < low) low = height[i];
			if (height[i] > high) high = height[i];
			if (land[i])
			{
				landCount++;
				landHeightSum += height[i];
				landTemperatureSum += temperature[i];
			}

			temperatureSum += temperature[i];
			if (temperature[i] < cold) cold = temperature[i];
			if (temperature[i] > warm) warm = temperature[i];
			if (System.MathF.Abs(latitude[i]) > 1.309f) { polarCount++; polarTemperatureSum += temperature[i]; }
		}

		GD.Print($"[WORLD] R={SphericalFbmNoise.WorldRadiusKm:F0} km  cells={n}  " +
			$"cell={scale.CellAreaKm2:F0} km²  spacing={scale.NeighborCenterKm:F1} km");
		GD.Print($"[TERRAIN] height={low:F0}…{high:F0} m  land={100.0 * landCount / n:F1}%  " +
			$"landMean={landHeightSum / System.Math.Max(1, landCount):F0} m（地球 30.6% / 546 m）");
		GD.Print($"[CLIMATE] temp={temperatureSum / n:F1} °C  land={landTemperatureSum / System.Math.Max(1, landCount):F1}  " +
			$"polar={polarTemperatureSum / System.Math.Max(1, polarCount):F1}  range={cold:F0}…{warm:F0}");
	}

	/// <summary>
	/// 主线程接收工作线程产出的生成结果：替换 Sim、发布事实、建立演化会话。
	/// 不做生成计算；调用点唯一（WorldRoot 的生成轮询）。
	/// </summary>
	internal void Adopt(WorldGenerationResult result)
	{
		if (result == null) throw new ArgumentNullException(nameof(result));
		_evolution?.Stop();
		_spec = result.Spec;
		Sim = result.Simulation;
		Sim.Publish(result.State);
		_evolution = result.Solver != null ? new WorldEvolutionSession(Sim, result.Solver) : null;
		LogGenerationSummary();
		IsInitialized = true;
		WorldPublished?.Invoke(Sim.Facts);
	}

	/// <summary>按当前定义准备完整世界，成功后替换旧会话并发布快照。</summary>
	public void Regenerate()
	{
		WorldGenSimulation.State candidate;
		WorldEvolutionSession nextSession = null;
		if (EnableEvolution)
		{
			var solver = Sim.CreateEvolutionSolver(_spec, out var initialFrame);
			var frame = solver.CalculateToStep(initialFrame, InitialEvolutionSteps);
			candidate = Sim.PrepareEvolution(solver, frame);
			nextSession = new WorldEvolutionSession(Sim, solver);
			GD.Print($"[EVOLVE] 已接入：res={frame.Definition.H3Resolution} " +
				$"cells={frame.Material.CellCount} slots={frame.Material.OccupiedSlotCount} " +
				$"stepYears={frame.Clock.StepYears} substeps={solver.TectonicSubstepCount}");
		}
		else candidate = Sim.PrepareStatic(_spec);

		_evolution?.Stop();
		Sim.Publish(candidate);
		_evolution = nextSession;
		if (IsInitialized) WorldPublished?.Invoke(Sim.Facts);
	}

	public void PlayEvolution() => _evolution?.Play();
	public void PauseEvolution() => _evolution?.Pause();
	public void StepEvolutionOnce() => _evolution?.Step();
	public void ConfirmEvolutionFault() => _evolution?.ConfirmFault();

	/// <summary>
	/// 把当前已发布的世界事实导出为 <c>.wmap</c> 地图包，返回绝对路径。
	/// 只读一份快照引用，导出期间演化继续推进也不会混入不同时刻的字段。
	/// </summary>
	public string ExportMapData()
	{
		WorldFacts facts = Sim?.Facts ?? throw new InvalidOperationException("世界尚未生成");
		string directory = ProjectSettings.GlobalizePath("user://maps");
		Directory.CreateDirectory(directory);
		string file = Path.Combine(directory, $"map_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wmap");
		DateTimeOffset now = DateTimeOffset.UtcNow;
		var snapshot = GeographySnapshotCapture.Capture(facts, Guid.NewGuid(),
			$"世界 {DateTime.Now:yyyy-MM-dd HH:mm}", string.Empty, now, "0.5.0");
		MapPackageWriter.Write(snapshot, file);
		return file;
	}

	public string SeekEvolutionToMa(double targetMa) =>
		_evolution == null ? "地质演化尚未初始化。" : _evolution.Seek(targetMa);

	public void SetEvolutionSpeed(double yearsPerSecond)
	{
		try { _evolution?.SetSpeed(yearsPerSecond); }
		catch (ArgumentOutOfRangeException e) { GD.PushWarning($"[EVOLVE] {e.Message}"); }
	}

	public void Tick(double delta)
	{
		var facts = _evolution?.Tick(delta, failure => GD.PushError($"[EVOLVE] 推进失败：{failure.Message}"));
		if (facts != null) WorldPublished?.Invoke(facts);
	}

	public override void _ExitTree() => _evolution?.Stop();

	// 本类没有依赖：世界本体就是它自己。但 _GetConfigurationWarnings 的汇总机制仍需存在
	// （根管理器会遍历三个子管理器调用它）⇒ 保留空实现，写明"无依赖"是结论而不是忘了填。

	/// <summary>编辑器依赖校验：本管理器无子依赖（世界本体即自身）⇒ 恒无警告。</summary>
	public override string[] _GetConfigurationWarnings() => System.Array.Empty<string>();

	/// <summary>
	/// 取景半径一致性校验（编辑器与根管理器调用）：取景与裁剪都 ∝ 球半径
	/// （<c>OrbitalCamera</c> 的取景半径 vs <c>PlanetGeometry.ProductionRadius</c>），不一致时画面会错位。
	/// 两侧缺省取同一个常量 ⇒ 运行期不会漂移；本方法只服务"有人在场景里手改 <c>_planetRadius</c>"这一种失真。
	/// 暂留本类：调用方只有根管理器一处（它同时看得见相机与球半径）。
	/// </summary>
	public static string[] RadiusMismatchWarnings(float cameraRadius, float planetRadius)
		=> Mathf.IsEqualApprox(cameraRadius, planetRadius)
			? System.Array.Empty<string>()
			: new[] { $"取景半径不一致：相机 {cameraRadius} ≠ 球半径 {planetRadius}（取景/裁剪 ∝ R）" };
}
