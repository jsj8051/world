using Godot;
using World.Logic;

namespace World.Render;

/// <summary>
/// 世界视图（`RenderManager > WorldView`）：球面网格 <see cref="BallView"/> ＋ 连续河线叠加 ＋ 选中高亮。
/// 存在的理由 = 把"重算后必须重烘"收成一个方法：世界侧发布一份新事实（生成 <see cref="WorldGenSimulation.Run"/>
/// 或导入），发布后调 <see cref="Rebake"/>；两者不再是靠注释维持的约定。
/// 装配一次（<see cref="Build"/>）；相机每帧经 <see cref="UpdateVisibility"/> 注入；
/// 参数改完由 <see cref="RefreshColors"/> 重烘（几何/UV 不动）；选中由宿主喂格 id。
/// 不订阅事件、不读参数、不判定选中，世界事实一律经注入的事实源现取。
/// </summary>
public partial class WorldView : Node3D
{
	IWorldFactsSource _source;                            // 世界事实来源（生成或导入）；重新发布会换快照 ⇒ 每次现取 _source.Facts
	BallView _ballView;                                   // 球面网格（LOD / 剔除 / 拾取 / 高亮）
	readonly System.Collections.Generic.List<IMapLayer> _layers = new();
	MapMode _mode;                                        // 当前取色策略（叠加层可见性跟着它）

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
		return new WorldPicker(_ballView, camera, _source);
	}

	/// <summary>
	/// 装配整个世界表现（幂等）。世界事实经 <paramref name="source"/> 现取：
	/// 初始取色源 = <c>Facts.HeightM</c>，河线 = <c>Facts.RiverLines</c> / <c>Facts.RiverTopology</c>。
	/// </summary>
	/// <param name="source">世界事实来源（生成或已导入；调用方持有，本类不接管生命周期）。</param>
	/// <param name="initialMode">初始地图模式（装配层注入，本类不内置默认）。</param>
	public void Build(IWorldFactsSource source, MapMode initialMode)
	{
		BeginBuild(source, initialMode);
		while (!StepBuild(1000)) { }
	}

	bool _buildStarted;
	bool _riverBuilt;

	/// <summary>准备分帧装配：建外壳与叠加层，不建网格/纹理。</summary>
	public void BeginBuild(IWorldFactsSource source, MapMode initialMode)
	{
		if (_buildStarted) return;
		_source = source ?? throw new System.ArgumentNullException(nameof(source));
		if (initialMode == null) throw new System.ArgumentNullException(nameof(initialMode));

		var facts = source.Facts ?? throw new System.InvalidOperationException("世界尚未生成或导入");
		_mode = initialMode;
		_ballView = new BallView(facts.Ball, facts.HeightM, initialMode, LodNearRatio, BackfaceCullRatio);
		AddChild(_ballView);
		_ballView.BeginBuild();

		var current = new CurrentFlowOverlay { Name = "CurrentFlow", Visible = false };
		_ballView.AddChild(current);
		AddLayer(current);
		var wind = new WindFlowOverlay { Name = "WindFlow", Visible = false };
		_ballView.AddChild(wind);
		AddLayer(wind);
		var motion = new PlateMotionOverlay { Name = "PlateMotion", Visible = false };
		AddChild(motion);
		AddLayer(motion);
		SyncLayers();
		_buildStarted = true;
	}

	/// <summary>在毫秒预算内推进分帧装配；返回是否全部完成。</summary>
	public bool StepBuild(double budgetMs)
	{
		if (_ballView == null) return true;
		if (!_ballView.StepBuild(budgetMs)) return false;
		if (_riverBuilt) return true;

		_riverBuilt = true;
		if (ShowRiverLines)
		{
			var rivers = new RiverLineOverlay { Name = "RiverLines" };
			_ballView.AddChild(rivers);
			AddLayer(rivers);
		}
		SyncLayers();
		return true;
	}

	/// <summary>分帧装配进度 0–1（网格/纹理 0–0.95，河线 0.95–1）。</summary>
	public float BuildProgress => _riverBuilt ? 1f : 0.95f * (_ballView?.BuildProgress ?? 0f);

	/// <summary>分帧装配当前阶段文字。</summary>
	public string BuildStage => _riverBuilt ? "完成" : _ballView?.BuildStage ?? "画面装配";

	/// <summary>选中高亮（<c>null</c> = 清除）。宿主（渲染管理器）点选后就地调它。</summary>
	public void SetHighlight(ulong? cellId) => _ballView?.HighlightCell(cellId);

	/// <summary>
	/// 重算或导入发布后重烘（世界侧发布新事实之后的唯一正确做法）：发布换快照 ⇒
	/// 重新绑定海拔源再重烘颜色纹理，河线按新图重建一遍。
	/// 几何/UV 不重建（<c>BuildChunks</c> 与 <c>SetElevationSource</c> 成对使用）。
	/// </summary>
	public void Rebake()
	{
		if (_ballView == null || _source == null) return;
		var facts = _source.Facts;
		if (facts == null) return;
		_ballView.SetElevationSource(facts.HeightM);
		_ballView.RefreshColors();
		SyncLayers();
	}

	/// <summary>取色函数换了（地图模式 / 参数行）⇒ 只重烘颜色纹理，不动几何、不动海拔源。
	/// 风场参数是月份：换月时风丝几何在后台按新月份重积，主线程只做网格组装。</summary>
	public void RefreshColors()
	{
		_ballView?.RefreshColors();
		SyncLayers();
	}

	/// <summary>切换地图模式（取色策略）。</summary>
	public void SetMode(MapMode mode)
	{
		_mode = mode;
		_ballView?.SetMode(mode);
		SyncLayers();
	}

	/// <summary>注册一个独立装配的图层；<see cref="IMapLayer.SetContext"/> 是它唯一的数据入口。</summary>
	public void AddLayer(IMapLayer layer)
	{
		if (layer == null) throw new System.ArgumentNullException(nameof(layer));
		if (_layers.Contains(layer)) return;
		_layers.Add(layer);
		layer.SetContext(_source?.Facts, _mode);
	}

	public override void _ExitTree()
	{
		// Godot owns child-node disposal; only release non-node layer subscriptions here.
		foreach (var layer in _layers)
			if (layer is not Node && layer is System.IDisposable disposable) disposable.Dispose();
		_layers.Clear();
	}

	// 世界事实或地图模式变了 ⇒ 交给每个图层自行决定重建/显隐（图层不认识别的图层）。
	void SyncLayers()
	{
		var facts = _source?.Facts;
		foreach (var layer in _layers) layer.SetContext(facts, _mode);
	}

	/// <summary>每帧剔除 + LOD + 高亮环带屏幕宽度（相机由管理器注入）。</summary>
	public void UpdateVisibility(Camera3D camera)
	{
		_ballView?.UpdateVisibility(camera);
		foreach (var layer in _layers) layer.UpdateCamera(camera);
	}
}
