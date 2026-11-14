using Godot;
using System.Collections.Generic;
using World.Client;
using World.Data;
using World.Logic;
using World.Render;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 渲染管理器（`GeographyEvolutionView > RenderManager`）：3D 世界表现的持有者，也是点选与地图模式的发起方。
/// 持有 <c>WorldView</c>（球面网格 + 河线 + 高亮）、<c>WorldPicker</c>（屏幕坐标 → 一格世界事实）
/// 与坞的模式侧（模式表 / 当前模式 / 四处同步）。地图模式归本类：模式 = 同一批世界事实换一种取色。
/// 坞→本类的信号连接写在 `scenes/render/WorldPresentation.tscn` 的 [connection] 里，本类→坞一律经
/// <c>Node.Call</c> ⇒ 两侧不引用彼此的类型：本类不认识 MapDock，坞也不认识本类。
/// 点选通道：<c>_UnhandledInput</c>（GUI 消费过的不到这里）→ <c>Picker.TryPick</c> →
/// <c>View.SetHighlight</c> 就地高亮 ＋ <see cref="CellPicked"/> 给界面侧订阅。
/// 相机按需现取（每帧 <c>GetViewport().GetCamera3D()</c>）。不生成世界、不取参数。
/// </summary>
public partial class RenderManager : Node3D, ITickable
{
	[Export] public NodePath WorldViewPath { get; set; }

	[ExportGroup("2D 出图")]
	/// <summary>2D 地形图导出宽（像素；默认见 <see cref="MapExporter.DefaultWidth"/>）。</summary>
	[Export] public int MapExportWidth { get; set; } = MapExporter.DefaultWidth;

	/// <summary>2D 地形图导出高（像素；默认见 <see cref="MapExporter.DefaultHeight"/>）。</summary>
	[Export] public int MapExportHeight { get; set; } = MapExporter.DefaultHeight;

	/// <summary>世界视图（本管理器内部持有：供拾取、取色、剔除与重烘；不外借）。</summary>
	WorldView View { get; set; }

	/// <summary>世界事实来源（初始化时注入；生成或已导入；初始取景要读 <c>Facts.FinalLand</c>）。</summary>
	IWorldFactsSource _source;

	/// <summary>
	/// 接上地图坞（由根管理器在装配时调用；坞是界面子树里的节点，路径由根管理器解析）。
	/// 参数类型刻意是 <c>PanelContainer</c>（坞的基类）而非 <c>MapDock</c>：对坞的调用一律经
	/// <c>Node.Call</c>——与场景连接同源，按名字牵线而不是绑类型。
	/// </summary>
	public void SetDock(PanelContainer dock) => _dock = dock;

	/// <summary>格拾取器（视图就绪后建；点选与将来"点哪儿看哪儿"的面板都用它）。</summary>
	public WorldPicker Picker { get; private set; }

	/// <summary>换帧期间禁用输入：发布新事实到重烘完成之间不接受点选/拖拽。</summary>
	public bool SwapActive { get; private set; }

	/// <summary>进入换帧：禁用输入，准备发布新事实。</summary>
	public void BeginSwap() => SwapActive = true;

	/// <summary>换帧结束：恢复输入。</summary>
	public void EndSwap() => SwapActive = false;

	/// <summary>
	/// 点选结果出口（<c>null</c> = 点空 / 取消选中）：一份自足的 <c>PickedCell</c>（经纬 / 海拔 /
	/// 海陆 / 附块 / 各距离），界面侧订阅后直接写信息卡 ⇒ 不必回头找世界侧补数据。
	/// 高亮不在这里——它在 <see cref="PickAt"/> 里就地完成（本管理器就是发起方）。
	/// </summary>
	public event System.Action<PickedCell?> CellPicked;
	internal bool ShowCrustDetails => _currentMode >= 0 && _modes[_currentMode] is CrustMode;
	ulong? _selectedCellId;

	// ── 地图模式（模式表 / 当前模式 / 坞同步）──
	PanelContainer _dock;              // 坞（哑组件；本类只调它的下行口，不认识它的类型）
	List<MapMode> _modes;              // 模式表（注册序 = 坞按钮序 = 模式身份）
	int _currentMode = -1;             // 当前模式下标（参数信号回查；ApplyMode 写入）

	// 手势判据只有 PointerGesture 一份；本管理器一个实例，只关心 Click。
	readonly PointerGesture _gesture = new();

	/// <summary>
	/// 初始化（由根管理器按序调用，幂等）：① 解析视图与坞；② 装配世界表现；
	/// ③ 建拾取器；④ 绑定模式表并进入初始模式。
	/// 必须在世界管理器初始化之后（世界事实要先跑出来），且在界面管理器初始化之前（坞要先就绪）。
	/// </summary>
	/// <param name="source">世界事实来源（生成或已导入的实例；调用方持有其生命周期）。</param>
	/// <param name="modes">模式表（根管理器建一次；本类持有并据此驱动坞）。</param>
	public void Initialize(IWorldFactsSource source, List<MapMode> modes)
	{
		InitializeIncremental(source, modes);
		while (!StepBuild(1000)) { }
		GD.Print($"[MAP] F12 / Ctrl+E 导出 2D 地形图（{MapExportWidth}×{MapExportHeight}，游戏目录/map_exports）");
	}

	bool _appliedInitial;

	/// <summary>
	/// 分帧初始化（由页面/根管理器每帧调 <see cref="StepBuild"/>）：
	/// 只建外壳、拾取器与坞模式名；网格/纹理/河线由 <see cref="StepBuild"/> 推进。
	/// </summary>
	public void InitializeIncremental(IWorldFactsSource source, List<MapMode> modes)
	{
		if (View != null) return;
		if (source == null) throw new System.ArgumentNullException(nameof(source), "世界必须先初始化");
		if (modes == null || modes.Count == 0) throw new System.ArgumentNullException(nameof(modes), "模式表为空");
		if (_dock == null) throw new System.InvalidOperationException("[RENDERMANAGER] 坞未接上（先调 SetDock）");

		View = this.Require<WorldView>(WorldViewPath, nameof(WorldViewPath));
		_modes = modes;
		_source = source;

		View.BeginBuild(source, modes[0]);
		Picker = View.CreatePicker(GetViewport().GetCamera3D());

		ApplyModeNames();
		_appliedInitial = false;
	}

	/// <summary>在毫秒预算内推进装配；返回是否完成。首次完成时同步坞的初始模式。</summary>
	public bool StepBuild(double budgetMs)
	{
		if (View == null) return true;
		bool done = View.StepBuild(budgetMs);
		if (done && !_appliedInitial)
		{
			ApplyInitialMode();
			_appliedInitial = true;
		}
		return done;
	}

	/// <summary>分帧装配进度 0–1。</summary>
	public float BuildProgress => View?.BuildProgress ?? 1f;

	/// <summary>分帧装配当前阶段文字。</summary>
	public string BuildStage => View?.BuildStage ?? "完成";

	void ApplyInitialMode()
	{
		_currentMode = 0;
		var mode = _modes[0];
		_dock?.Call("SetMode", 0);
		_dock?.Call("SetVariantOptions", AsArray(mode.VariantOptions), mode.VariantIndex);
		_dock?.Call("SetParameterOptions", AsArray(mode.ParameterOptions), mode.ParameterIndex);
		_dock?.Call("SetLegend", mode.ScaleCaption);
		RefreshSelection();
	}

	/// <summary>
	/// 初始取景对准陆地质心：第一眼看到陆地，而不是随机半球的大洋。
	/// 取景只依赖最终事实（<c>FinalLand</c> + 格心方向），不改任何世界数据。
	/// </summary>
	public void FrameLand(OrbitalCamera camera)
	{
		if (camera == null) return;
		var facts = _source?.Facts;
		if (facts == null) return;
		var dirs = facts.Ball.CellDirs;
		if (facts.FinalLand.Length != dirs.Length) return;

		Vector3 sum = Vector3.Zero;
		int count = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			if (!facts.FinalLand[i]) continue;
			sum += dirs[i];
			count++;
		}
		if (count > 0) camera.LookAtPoint(sum.Normalized());
	}

	/// <summary>
	/// 每帧推进（ITickable）：剔除 / LOD / 高亮宽度。
	/// 相机按需现取活动相机，不存字段、不由根管理器注入——<c>OrbitalCamera._Ready</c> 已把自建
	/// <c>Camera3D</c> 设为 <c>Current</c>，"当前相机"是场景树里的事实。
	/// 不缓存是有意的：Godot 节点释放延迟，"已排队释放但尚未删除"期间 <c>IsInstanceValid</c> 仍为真
	/// ⇒ 缓存字段会拿死相机去射线，点选静默失灵。
	/// </summary>
	public void Tick(double delta)
	{
		var cam = GetViewport()?.GetCamera3D();
		Picker?.SetCamera(cam);            // 顺手喂拾取器（同一次取，零额外开销）
		View?.UpdateVisibility(cam);
	}

	/// <summary>
	/// 世界发布后由装配层调用：
	/// 转发给 <c>WorldView.Rebake</c>——重新绑定海拔源 + 重烘颜色 + 重建河线。
	/// </summary>
	public void RebakeView()
	{
		long timing = WorldBuildProfile.Start();
		View?.Rebake();
		RefreshSelection();
		WorldBuildProfile.Mark(ref timing, "map-rebake");
	}

	// ── 坞信号的处理口（连接写在 WorldPresentation.tscn 的 [connection] 里）──
	// 这两个方法必须是 public：Godot 的 C# 桥只把公开方法暴露给引擎（场景连接按名字解析）。
	// 参数是一个整数（模式下标 / 参数下标）：坞只发数字，语义由本类解释——
	// 这是"哑组件 + 驱动方"的分工，也是坞不必认识 MapMode 的原因。

	/// <summary>坞点了模式按钮（<c>MapDock.ModeSelected</c>）⇒ 切取色 + 同步坞高亮/参数行/图例。</summary>
	public void OnDockModeSelected(int modeId)
	{
		if (_modes == null || modeId < 0 || modeId >= _modes.Count) return;
		ApplyMode(modeId);
	}

	/// <summary>坞点了参数按钮（<c>MapDock.ParameterSelected</c>）⇒ 换"取哪个值"（不碰生产公式）+ 重烘 + 刷新图例。</summary>
	public void OnDockParameterSelected(int index)
	{
		if (_modes == null || _currentMode < 0) return;
		var mode = _modes[_currentMode];
		mode.SetParameter(index);
		View?.RefreshColors();             // 固定物理域 ⇒ 无需 BeginBake 重算，直接重烘
		_dock?.Call("SetLegend", mode.ScaleCaption);
	}

	/// <summary>坞点了变体按钮（<c>MapDock.VariantSelected</c>）⇒ 换"取哪一套"（与参数轴正交）+ 重烘 + 刷新图例。</summary>
	public void OnDockVariantSelected(int index)
	{
		if (_modes == null || _currentMode < 0) return;
		var mode = _modes[_currentMode];
		mode.SetVariant(index);
		View?.RefreshColors();
		_dock?.Call("SetLegend", mode.ScaleCaption);
	}

	/// <summary>唯一的模式切换路径：视图取色 + 坞高亮 + 参数行 + 图例（四处单点同步，杜绝各自为政）。</summary>
	void ApplyMode(int id)
	{
		_currentMode = id;
		var mode = _modes[id];
		View?.SetMode(mode);               // 换取色函数重烘颜色纹理（几何/UV 不动）
		_dock?.Call("SetMode", id);        // 按钮高亮同步
		_dock?.Call("SetVariantOptions", AsArray(mode.VariantOptions), mode.VariantIndex);
		_dock?.Call("SetParameterOptions", AsArray(mode.ParameterOptions), mode.ParameterIndex);
		_dock?.Call("SetLegend", mode.ScaleCaption);
		RefreshSelection();
	}

	/// <summary>
	/// <c>IReadOnlyList&lt;string&gt;</c> → <c>string[]</c>（<c>Call</c> 的 Variant 不收接口类型）；
	/// <c>null</c> 原样传（坞把它当"无参数行 ⇒ 隐藏"，语义不能换成空数组）。
	/// </summary>
	static string[] AsArray(IReadOnlyList<string> list)
		=> list == null ? null : System.Linq.Enumerable.ToArray(list);

	/// <summary>
	/// 按钮文案下行（模式名是单一事实源）。经 <c>Call</c> 而非直接调用：本类不引用坞的类型。
	/// <c>IReadOnlyList&lt;string&gt;</c> 不能直接过 <c>Call</c>（Variant 不认接口类型）⇒ 先成数组。
	/// </summary>
	void ApplyModeNames()
	{
		var names = new string[_modes.Count];
		for (int i = 0; i < names.Length; i++) names[i] = _modes[i].Name;
		_dock?.Call("BindModes", names);
	}

	// ── 点选（本管理器做手势判定 + 拾取 + 高亮；数据出口给订阅方）──

	// _UnhandledInput：GUI 消费过的事件不会到这里（Godot 原生投递顺序保证，不抢 GUI 的输入）。
	// 手势判定交给 PointerGesture（与相机共用一份判据）；本管理器只对"点选"作反应，
	// 拖拽留给相机去转球（Godot 把同一批事件投递给每个覆写者，各人读各人的意图）。
	public override void _UnhandledInput(InputEvent @event)
	{
		if (SwapActive) return;
		if (@event is InputEventKey key && key.Pressed && !key.Echo
			&& (key.Keycode == Key.F12 || (key.CtrlPressed && key.Keycode == Key.E)))
		{
			ExportTerrainMap();
			return;
		}
		if (_gesture.Feed(@event, out var at) == PointerIntent.Click)
			PickAt(at);
	}

	void ExportTerrainMap()
	{
		try { ExportTerrainMapNow(); }
		catch (System.Exception e) { GD.PushError($"[MAP] 导出失败：{e.Message}"); }
	}

	/// <summary>导出当前地图模式的 2D 图片并返回绝对路径；世界未就绪或写入失败时抛。</summary>
	public string ExportTerrainMapNow()
	{
		if (_source?.Facts == null || _modes == null || _modes.Count == 0)
			throw new System.InvalidOperationException("世界尚未就绪");
		int terrain = _modes.FindIndex(m => m is TerrainMode);
		var mode = terrain >= 0 ? _modes[terrain] : _modes[System.Math.Max(0, _currentMode)];

		GD.Print($"[MAP] 开始导出 2D 地形图（{mode.Name}，{MapExportWidth}×{MapExportHeight}）...");
		var watch = System.Diagnostics.Stopwatch.StartNew();
		string path = MapExporter.ExportTerrain(_source, mode, MapExportWidth, MapExportHeight);
		GD.Print($"[MAP] 导出完成（{watch.ElapsedMilliseconds} ms）：{path}");
		return path;
	}

	void PickAt(Vector2 screenPos)
	{
		if (Picker == null) return;

		if (!Picker.TryPick(screenPos, out var cell))
		{
			_selectedCellId = null;
			View?.SetHighlight(null);     // 表现：清高亮
			CellPicked?.Invoke(null);     // 数据：取消选中
			return;
		}
		_selectedCellId = cell.CellId;
		View?.SetHighlight(cell.CellId);  // 表现：高亮该格（就地，不绕事件）
		CellPicked?.Invoke(cell);         // 数据：格子事实交给订阅方自行显示
	}

	void RefreshSelection()
	{
		if (_selectedCellId is not ulong id) return;
		if (Picker.TryReadCell(id, out var cell))
		{
			CellPicked?.Invoke(cell);
			return;
		}
		_selectedCellId = null;
		View?.SetHighlight(null);
		CellPicked?.Invoke(null);
	}

	// ── 依赖解析 / 校验（判据收在 World.Utils.NodeDependency，四个管理器共用一份）──

	/// <summary>编辑器依赖校验（同 <see cref="GeographyEvolutionView"/> 的自文档化机制）。</summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<WorldView>(WorldViewPath, nameof(WorldViewPath), warnings);
		// 坞由根管理器解析后经 SetDock 注入（本类不持它的 NodePath）⇒ 这里报"未接上"即可。
		if (_dock == null) warnings.Add("坞未接上（根管理器应先调 SetDock）");
		return warnings.ToArray();
	}
}
