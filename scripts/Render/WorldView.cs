using Godot;
using World.Logic;

namespace World.Render;

/// <summary>
/// 世界视图（`RenderManager > WorldView`）：球面网格 <see cref="BallView"/> ＋ 连续河线叠加 ＋ 选中高亮。
/// 存在的理由 = 把"重算后必须重烘"收成一个方法：世界侧只跑 <see cref="WorldGenSimulation.Run"/>，
/// 跑完调 <see cref="Rebake"/>；两者不再是靠注释维持的约定。
/// 装配一次（<see cref="Build"/>）；相机每帧经 <see cref="UpdateVisibility"/> 注入；
/// 参数改完由 <see cref="RefreshColors"/> 重烘（几何/UV 不动）；选中由宿主喂格 id。
/// 不订阅事件、不读参数、不判定选中，世界事实一律经注入的 sim 现取。
/// </summary>
public partial class WorldView : Node3D
{
	WorldGenSimulation _sim;                              // 世界事实来源；Run() 会换数组实例 ⇒ 每次现取，不缓存数组
	BallView _ballView;                                   // 球面网格（LOD / 剔除 / 拾取 / 高亮）
	RiverLineOverlay _riverLines;                         // 连续河线叠加（只读 RiverGeometry，不改水文）

	/// <summary>LOD 近距比例：距离 &lt; 球半径 × 此值即用高档。出厂值 6。</summary>
	[ExportGroup("LOD 与剔除")]
	[Export] public float LodNearRatio { get; set; } = 6f;

	/// <summary>背面剔除比例（保留接口；当前剔除角限不读此值）。出厂值 3.0。</summary>
	[Export] public float BackfaceCullRatio { get; set; } = 3.0f;

	/// <summary>是否画河线叠加。关掉只影响表现（地形/水文照算），
	/// 用来单独判读地形本身，不把水文的单流向线混进画面。</summary>
	[ExportGroup("叠加层")]
	[Export] public bool ShowRiverLines { get; set; } = true;

	/// <summary>
	/// 造一个格拾取器：把本视图的球面网格、相机与世界事实接起来。
	/// 由视图自己造而不是外借 <see cref="BallView"/> 实例——拾取是"问视图命中哪一格"，
	/// 视图最清楚几何与事实源怎么接，球面网格因而只是本类的私有字段。
	/// </summary>
	public WorldPicker CreatePicker(Camera3D camera)
	{
		if (_ballView == null) throw new System.InvalidOperationException("世界视图尚未 Build，无法建拾取器");
		return new WorldPicker(_ballView, camera, _sim);
	}

	/// <summary>
	/// 装配整个世界表现（幂等）。世界生成产物经 <paramref name="sim"/> 现取：
	/// 初始取色源 = <c>sim.Terrain.HeightM</c>，河线 = <c>sim.Hydrology.RiverLines</c>。
	/// </summary>
	/// <param name="sim">世界事实来源（调用方持有，本类不接管生命周期）。</param>
	/// <param name="initialMode">初始地图模式（装配层注入，本类不内置默认）。</param>
	public void Build(WorldGenSimulation sim, MapMode initialMode)
	{
		if (_ballView != null) return;
		_sim = sim ?? throw new System.ArgumentNullException(nameof(sim));
		if (initialMode == null) throw new System.ArgumentNullException(nameof(initialMode));

		_ballView = new BallView(sim.Ball, sim.Terrain.HeightM, initialMode, LodNearRatio, BackfaceCullRatio);
		AddChild(_ballView);
		_ballView.BuildChunks();

		if (!ShowRiverLines) return;

		_riverLines = new RiverLineOverlay { Name = "RiverLines" };
		_ballView.AddChild(_riverLines);
		_riverLines.Build(sim.Ball, sim.Hydrology.RiverLines, sim.Hydrology.RiverTopology);
	}

	/// <summary>选中高亮（<c>null</c> = 清除）。宿主（渲染管理器）点选后就地调它。</summary>
	public void SetHighlight(ulong? cellId) => _ballView?.HighlightCell(cellId);

	/// <summary>
	/// 重算后重烘（世界侧 <c>Sim.Run()</c> 之后的唯一正确做法）：<c>Run</c> 会换海拔数组实例 ⇒
	/// 重新绑定海拔源再重烘颜色纹理，河线按新图重建一遍。
	/// 几何/UV 不重建（<c>BuildChunks</c> 与 <c>SetElevationSource</c> 成对使用）。
	/// </summary>
	public void Rebake()
	{
		if (_ballView == null || _sim == null) return;
		_ballView.SetElevationSource(_sim.Terrain.HeightM);
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
		// 相机只影响表现宽度（单向：Camera → 表现层），不改任何水文事实
		_riverLines?.UpdateCameraScale(camera);
	}
}
