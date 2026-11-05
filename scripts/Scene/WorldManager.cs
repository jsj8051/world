using Godot;
using World.Data;
using World.Utils;
using World.Render;
using World.Logic;

namespace World.Scene;

/// <summary>
/// 世界管理器：世界事实的唯一入口。持有 <see cref="Sim"/> 与生产分辨率，不镜像任何数据。
/// 不持相机、不接收输入、不发事件，也没有逐帧工作（剔除与点选归 <see cref="RenderManager"/>）。
/// </summary>
public partial class WorldManager : Node3D
{
	/// <summary>
	/// 世界生产分辨率 = res4（冻结）：res4 是世界本身的空间离散尺度
	/// （288,122 格、每格 1,770 km²、格边长 ≈26.1 km），各模拟系统都应适配这个尺度。
	/// 不得靠降低生产分辨率规避子系统的尺度适配缺陷——正确处置是让子系统适配 res4，
	/// 否则滑向"不工作就换档"的架构倒退。res1~res4 同时用作诊断实验档，不是从中挑生产档。
	/// </summary>
	public const int ProductionRes = 4;

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
		Sim = new WorldGenSimulation(ProductionRes, PlanetGeometry.ProductionRadius);
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

	/// <summary>按当前世界定义重跑全链生成。只算数据；算完须由装配层调 <c>RenderManager.RebakeView()</c> 重烘表现。</summary>
	public void Regenerate()
	{
		Sim.Run(_spec);
	}

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
