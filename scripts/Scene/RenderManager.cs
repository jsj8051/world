using Godot;
using System.Collections.Generic;
using World.Data;                  // PickedCell（点选结果载体）
using World.Logic;                 // WorldGenSimulation（世界事实来源）
using World.Render;                // WorldView / WorldPicker / MapMode（表现层）
using World.UI;                    // MapDock（哑组件：坞的面板本体）
using World.Utils;                 // NodeDependency / PointerGesture

namespace World.Scene;

/// <summary>
/// **渲染管理器**（`WorldRoot > RenderManager`）：3D 世界表现这一块的持有者，**也是点选与地图模式的发起方**。
/// 持有 `WorldView`（球面网格 + 河线 + 高亮）、`WorldPicker`（屏幕坐标 → 一格世界事实），
/// 以及**坞的模式侧**（模式表 / 当前模式 / 四点同步）。
///
/// ★**地图模式归本类**（2026-10-11 收口）：模式 = "同一批世界事实换一种取色"，
///   所以"当前是哪个模式"与"切模式要同步哪几处"都属于表现侧。原 `MapDockController`
///   （非 Node 的驱动器）已删除——它的全部工作在这里就地完成。
///
/// ★**坞与本类之间用 Godot 信号连接，连接写在场景文件里**
///   （`scenes/core/WorldGenWorld.tscn` 的 `[connection]`）⇒ 两侧代码**零编译期依赖**：
///   本类不认识 `MapDock` 类型（只经 `PanelContainer` 节点调用），坞也不认识本类。
///   ★之所以要这样：坞与渲染在分层上是**平级的两个引擎侧层**，"谁能引用谁"没有正当答案；
///   而**场景连接是元数据、不是代码边**——中介由场景文件担任，不必再包一层类。
///
/// 点选通道：`_UnhandledInput`（GUI 消费过的不到这里）→ `Picker.TryPick` →
/// `View.SetHighlight` 就地高亮 ＋ <see cref="CellPicked"/> 给界面侧订阅。
/// 相机**按需现取**（每帧 `GetViewport().GetCamera3D()`）。不生成世界、不取参数。
/// </summary>
public partial class RenderManager : Node3D, ITickable
{
	[Export] public NodePath WorldViewPath { get; set; }

	/// <summary>世界视图（本管理器内部持有：供拾取、取色、剔除与重烘；**不外借**）。</summary>
	WorldView View { get; set; }

	/// <summary>
	/// **接上地图坞**（由根管理器在装配时调用；坞是界面子树里的节点，路径由根管理器解析）。
	/// ★参数类型刻意是 `PanelContainer`（坞的基类）而非 `MapDock`：本管理器的源码**不 import `World.UI`**，
	///   对坞的调用一律经 `Node.Call`（与场景连接同源——"由场景/引擎按名字牵线"而不是编译期依赖）。
	/// </summary>
	public void SetDock(PanelContainer dock) => _dock = dock;

	/// <summary>格拾取器（视图就绪后建；点选与将来"点哪儿看哪儿"的面板都用它）。</summary>
	public WorldPicker Picker { get; private set; }

	/// <summary>
	/// **点选结果出口**（`null` = 点空 / 取消选中）：一份自足的 `PickedCell`（经纬 / 海拔 /
	/// 海陆 / 附块 / 区域 / 各距离），界面侧订阅后直接写信息卡 ⇒ 不必回头找世界侧补数据。
	/// 高亮不在这里——它在 <see cref="PickAt"/> 里就地完成（本管理器就是发起方）。
	/// </summary>
	public event System.Action<PickedCell?> CellPicked;

	// ── 地图模式（原 MapDockController 的职责）──────────────────────────
	PanelContainer _dock;              // 坞（哑组件；本类只调它的下行口，不认识它的类型）
	List<MapMode> _modes;              // 模式表（注册序 = 坞按钮序 = 模式身份）
	int _currentMode = -1;             // 当前模式下标（参数信号回查用；ApplyMode 写入）

	// 手势判据（唯一处 = `PointerGesture`，阈值只定义一次）：本管理器一个实例，只关心 Click。
	readonly PointerGesture _gesture = new();

	/// <summary>
	/// **初始化**（由根管理器按序调用，幂等）：① 解析视图与坞；② 装配世界表现；
	/// ③ 建拾取器；④ 绑定模式表并进入初始模式。
	/// ★必须在**世界管理器初始化之后**（世界事实 `Sim` 要先跑出来）、
	/// 且必须在**界面管理器初始化之前**（界面只订阅事件，但坞要在建卡之前就绪）。
	/// </summary>
	/// <param name="sim">世界事实来源（世界管理器已跑完生成的那一个实例）。</param>
	/// <param name="modes">模式表（根管理器建一次；本类持有并据此驱动坞）。</param>
	public void Initialize(WorldGenSimulation sim, List<MapMode> modes)
	{
		if (View != null) return;   // 幂等
		if (sim == null) throw new System.ArgumentNullException(nameof(sim), "世界必须先初始化");
		if (modes == null || modes.Count == 0) throw new System.ArgumentNullException(nameof(modes), "模式表为空");
		if (_dock == null) throw new System.InvalidOperationException("[RENDERMANAGER] 坞未接上（先调 SetDock）");

		View = this.Require<WorldView>(WorldViewPath, nameof(WorldViewPath));
		_modes = modes;

		View.Build(sim, modes[0]);
		// ★拾取器由视图自造（`BallView` 保持私有：它是球面渲染件的内部件，不外借给装配层）。
		Picker = View.CreatePicker(GetViewport().GetCamera3D());

		// 模式侧：按钮文案下行（模式 `Name` 是单一事实源；数量不符时坞 `BindModes` 当场抛）
		// + 初始模式 = 注册序首项（`WorldGenMapModes.CreateAll` 注册序[0] = 海拔）。
		ApplyModeNames();
		ApplyMode(0);
	}

	/// <summary>
	/// 每帧推进（`ITickable`）：剔除 / LOD / 高亮宽度。
	/// ★相机**按需现取活动相机**，不存字段、不由根管理器注入：`OrbitalCamera._Ready` 已把自建的
	///   `Camera3D` 设为 `Current` ⇒ "当前相机"是**场景树里的事实**。
	/// ★**不缓存**（有意）：Godot 节点释放是延迟的，"已排队释放但尚未删除"期间
	///   `IsInstanceValid` 仍为真 ⇒ 缓存字段会拿死相机去射线，**点选静默失灵**。
	/// </summary>
	public void Tick(double delta)
	{
		var cam = GetViewport()?.GetCamera3D();
		Picker?.SetCamera(cam);            // 顺手喂拾取器（同一次取，零额外开销）
		View?.UpdateVisibility(cam);
	}

	/// <summary>
	/// **重算后的重烘口**（世界侧 `WorldManager.Regenerate()` 之后由装配层调）：
	/// 转发给 `WorldView.Rebake`——重新绑定海拔源 + 重烘颜色 + 重建河线。
	/// ⚠️ 当前**零调用者**：与 `Regenerate()` 同属"改参数即时生效"尚未接线的两半，
	///    接线方式与验收见 `docs/债务清单-架构与分层.md` 的 **A-8**（别另开通道）。
	/// </summary>
	public void RebakeView() => View?.Rebake();

	// ── 坞信号的处理口（**连接写在场景文件里**：WorldGenWorld.tscn 的 [connection]）────
	// ★这两个方法**必须是 public**：Godot 的 C# 桥只把公开方法暴露给引擎（场景连接按名字解析）。
	// ★参数是**一个整数**（模式下标 / 参数下标）——坞只发数字，语义由本类解释，
	//   这正是"哑组件 + 驱动方"的分工，也是坞不必认识 `MapMode` 的原因。

	/// <summary>坞点了模式按钮（`MapDock.ModeSelected`）⇒ 切取色 + 同步坞高亮/参数行/图例。</summary>
	public void OnDockModeSelected(int modeId)
	{
		if (_modes == null || modeId < 0 || modeId >= _modes.Count) return;
		ApplyMode(modeId);
	}

	/// <summary>坞点了参数按钮（`MapDock.ParameterSelected`）⇒ 换"取哪个值"（不碰生产公式）+ 重烘 + 刷新图例。</summary>
	public void OnDockParameterSelected(int index)
	{
		if (_modes == null || _currentMode < 0) return;
		var mode = _modes[_currentMode];
		mode.SetParameter(index);
		View?.RefreshColors();             // 固定物理域 ⇒ 无需 BeginBake 重算，直接重烘
		_dock?.Call("SetLegend", mode.ScaleCaption);
	}

	/// <summary>**唯一的模式切换路径**：视图取色 + 坞高亮 + 参数行 + 图例（四处单点同步，杜绝各自为政）。</summary>
	void ApplyMode(int id)
	{
		_currentMode = id;
		var mode = _modes[id];
		View?.SetMode(mode);               // 换取色函数重烘颜色纹理（几何/UV 不动）
		_dock?.Call("SetMode", id);        // 按钮高亮同步
		_dock?.Call("SetParameterOptions", AsArray(mode.ParameterOptions), mode.ParameterIndex);
		_dock?.Call("SetLegend", mode.ScaleCaption);
	}

	/// <summary>
	/// `IReadOnlyList&lt;string&gt;` → `string[]`（`Call` 的 Variant 不收接口类型）；
	/// `null` 原样传（坞把它当"无参数行 ⇒ 隐藏"，语义不能换成空数组）。
	/// </summary>
	static string[] AsArray(IReadOnlyList<string> list)
		=> list == null ? null : System.Linq.Enumerable.ToArray(list);

	/// <summary>
	/// 按钮文案下行（模式名是单一事实源）。★经 `Call` 而非直接调用：本类不 import 坞的类型。
	/// ⚠️ `IReadOnlyList&lt;string&gt;` **不能**直接过 `Call`（Variant 不认接口类型）⇒ 先成数组。
	/// </summary>
	void ApplyModeNames()
	{
		var names = new string[_modes.Count];
		for (int i = 0; i < names.Length; i++) names[i] = _modes[i].Name;
		_dock?.Call("BindModes", names);
	}

	// ── 点选（本管理器做手势判定 + 拾取 + 高亮；数据出口给订阅方）──────────

	// _UnhandledInput：**GUI 消费过的事件不会到这里**（Godot 原生投递顺序保证 ⇒ 不抢 GUI 的输入）。
	// 手势判定交给 `PointerGesture`（与相机共用同一份判据）；本管理器只对"点选"作反应，
	// 拖拽留给相机去转球（Godot 把同一批事件投递给每个覆写者 ⇒ 各人读各人的意图）。
	public override void _UnhandledInput(InputEvent @event)
	{
		if (_gesture.Feed(@event, out var at) == PointerIntent.Click)
			PickAt(at);
	}

	void PickAt(Vector2 screenPos)
	{
		if (Picker == null) return;

		if (!Picker.TryPick(screenPos, out var cell))
		{
			View?.SetHighlight(null);     // 表现：清高亮
			CellPicked?.Invoke(null);     // 数据：取消选中
			return;
		}
		View?.SetHighlight(cell.CellId);  // 表现：高亮该格（就地，不绕事件）
		CellPicked?.Invoke(cell);         // 数据：格子事实交给订阅方自行显示
	}

	// ── 依赖解析 / 校验（判据收在 `World.Utils.NodeDependency`，四个管理器共用一份）────

	/// <summary>编辑器依赖校验（同 <see cref="WorldRoot"/> 的自文档化机制）。</summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<WorldView>(WorldViewPath, nameof(WorldViewPath), warnings);
		// 坞由根管理器解析后经 `SetDock` 注入（本类不持它的 NodePath）⇒ 这里报"未接上"即可。
		if (_dock == null) warnings.Add("坞未接上（根管理器应先调 SetDock）");
		return warnings.ToArray();
	}
}
