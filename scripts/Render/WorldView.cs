using Godot;
using World.Logic;    // WorldGenSimulation（世界事实来源，只读消费）

namespace World.Render;

/// <summary>
/// **世界视图**（`RenderManager > WorldView`）：一整个 3D 世界表现的持有者——
/// 球面网格视图 <see cref="BallView"/> ＋ 连续河线叠加 ＋ 选中高亮。
/// <para>★它存在的理由 = 把"重算后必须重烘"这条不变量**收成一个方法**：世界那边只管
/// <see cref="WorldGenSimulation.Run"/>，跑完调 <see cref="Rebake"/>，两边不再是靠注释维持的约定。</para>
/// <para>装配一次（<see cref="Build"/>，重活）；相机每帧由 <see cref="UpdateVisibility"/> 注入；
/// 参数面板改完由 <see cref="RefreshColors"/> 重烘（几何/UV 不动）；选中由宿主喂格 id
/// （<see cref="SetHighlight"/>）——本类**不订阅任何事件**，只被持有它的管理器直呼。
/// 不读参数、不判定选中、不持世界定义实例——世界事实一律经构造注入的那一个 `sim` 现取。</para>
/// </summary>
public partial class WorldView : Node3D
{
	WorldGenSimulation _sim;                              // 世界事实来源（现取字段；Run() 会换数组实例）
	BallView _ballView;                                   // 球面网格（LOD / 剔除 / 拾取 / 高亮）
	RiverLineOverlay _riverLines;                         // 连续河线叠加（只读 RiverGeometry，不改水文）

	[ExportGroup("LOD 与剔除")]
	[Export] public float LodNearRatio { get; set; } = 6f;
	[Export] public float BackfaceCullRatio { get; set; } = 3.0f;

	/// <summary>
	/// **造一个格拾取器**（供持有它的渲染管理器建）：把本视图的球面网格 + 相机 + 世界事实接起来。
	/// ★由视图自己造而不是外借 `BallView`：拾取本来就是"问视图命中哪一格"，
	///   视图最清楚怎么把自己的几何与事实源接上 ⇒ `BallView` 得以保持私有（A-1 收口）。
	/// </summary>
	public WorldPicker CreatePicker(Camera3D camera)
	{
		if (_ballView == null) throw new System.InvalidOperationException("世界视图尚未 Build，无法建拾取器");
		return new WorldPicker(_ballView, camera, _sim);
	}

	/// <summary>
	/// **装配整个世界表现**（幂等）。世界生成产物经 <paramref name="sim"/> 现取：
	/// 初始取色源 = `sim.Terrain.DisplayElevation`，河线 = `sim.Hydrology.RiverLines`。
	/// </summary>
	/// <param name="sim">世界事实来源（调用方持有，本类不接管生命周期）。</param>
	/// <param name="initialMode">初始地图模式（装配层注入，本类不内置默认）。</param>
	public void Build(WorldGenSimulation sim, MapMode initialMode)
	{
		if (_ballView != null) return;   // 幂等
		_sim = sim ?? throw new System.ArgumentNullException(nameof(sim));
		if (initialMode == null) throw new System.ArgumentNullException(nameof(initialMode));

		_ballView = new BallView(sim.Ball, sim.Terrain.DisplayElevation, initialMode, LodNearRatio, BackfaceCullRatio);
		AddChild(_ballView);
		_ballView.BuildChunks();

		_riverLines = new RiverLineOverlay { Name = "RiverLines" };
		_ballView.AddChild(_riverLines);
		_riverLines.Build(sim.Ball, sim.Hydrology.RiverLines, sim.Hydrology.RiverTopology);
	}

	/// <summary>选中高亮（`null` = 清除）。宿主（渲染管理器）点选后就地调它。</summary>
	public void SetHighlight(ulong? cellId) => _ballView?.HighlightCell(cellId);

	/// <summary>
	/// **重算后重烘**（世界侧 `Sim.Run()` 之后的唯一正确做法）：海拔数组换了实例 ⇒ 重新绑定
	/// 海拔源再重烘颜色纹理。★几何/UV 不得重建（`BuildChunks` 与 `SetElevationSource` 必须成对），
	/// 河线则按新图重建一遍。
	/// </summary>
	public void Rebake()
	{
		if (_ballView == null || _sim == null) return;
		_ballView.SetElevationSource(_sim.Terrain.DisplayElevation);
		_ballView.RefreshColors();
		_riverLines?.Build(_sim.Ball, _sim.Hydrology.RiverLines, _sim.Hydrology.RiverTopology);
	}

	/// <summary>取色函数换了（地图模式 / 参数行）⇒ 只重烘颜色纹理，不动几何、不动海拔源。</summary>
	public void RefreshColors() => _ballView?.RefreshColors();

	/// <summary>切换地图模式（取色策略）。</summary>
	public void SetMode(MapMode mode) => _ballView?.SetMode(mode);

	/// <summary>每帧剔除 + LOD + 高亮环带屏幕宽度（相机由管理器注入）。</summary>
	public void UpdateVisibility(Camera3D camera)
	{
		_ballView?.UpdateVisibility(camera);
		// 相机只影响**表现宽度**（单向：Camera → 表现层），不改任何水文事实
		_riverLines?.UpdateCameraScale(camera);
	}
}
