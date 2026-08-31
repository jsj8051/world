using Godot;
using System;

using World.MapView;
using World.Services;

namespace World.UI;

/// <summary>
/// 归航地图坞组件（2026-08-31 拆分：原 MapViewer.Ui.cs 的 CukHud 逻辑独立成组件）。
/// 四层模型：表现+容器在 scenes/ui/CukDock.tscn（CukDock/DockBox/HeadRow/CatRow/LayerRow + sb_* 样式），
/// 本组件只做逻辑层——分类/图层按钮的行内状态机 + 坞滑出滑入动画（鼠标位置驱动）。
/// 数据：图层按钮由 LayerRegistry（静态注册表）驱动；_layer 归属 MapViewer（经 SetLayer 下行同步，
/// 不反向持有）；用户点图层 → LayerSelected 信号上行，MapViewer 处理实际切换。
/// </summary>
/// <remarks>
/// 2026-08-31 显式状态模型重构（用户拍板，替换原 _expanded 布尔 + 隐式 tween 起点）：
/// 五元状态随时可推导——current（实时读 offset）、collapsed/expanded（由面板高派生）、
/// direction（由 _target 判定：== ExpandedPos → 滑出 / == CollapsedPos → 滑入）。
/// 动画唯一路径：from = CurrentPos() → to = target，中途任何变化都从真实位置续滑，无隐式起点。
/// </remarks>
public partial class CukHud : PanelContainer
{
	/// <summary>图层选择信号（上行：用户点击图层按钮 → MapViewer 订阅后 Layer = id）。</summary>
	[Signal] public delegate void LayerSelectedEventHandler(int layerId);

	// ── 场景骨架（%唯一名，见 MapViewer.tscn CukDock 段）──
	private Button[] _catButtons;        // 3 个分类按钮（场景预置：地理/气候/人文，同 ButtonGroup 互斥）
	private Button[] _layerButtons;      // 17 个图层按钮（按 LayerRegistry 动态生成，图标/提示辞来自策略）

	// ── 坞状态机（2026-08-31 显式五元：current/collapsed/expanded/_target 方向）──
	private int _currentLayer;           // 当前图层镜像（MapViewer.SetLayer 下行同步；日志用）
	private LayerCategory _category;     // 当前分类（UI 显示态；点分类只切显示不改 _layer——用户拍板）
	private Vector2? _target;            // 动画目标坐标；null=已静止。方向由它判定（==ExpandedPos 滑出 / ==CollapsedPos 滑入）
	private Tween _tween;                // 滑出/滑入动画（新动画前 Kill 旧的防串台）
	private const float CukHeadH = 30f;  // 标题条高（场景 HeadRow CustomMinimumSize 同值；收起时唯一露出部分）

	private const float CukSlideDur = 0.25f; // 滑出/滑入时长（2026-08-31 0.15→0.25：原 0.15s+Cubic 起步陡，
											 //   渲染帧率 ~30fps 时仅 4-5 采样点，每帧大跳 → 用户报"三段式收起"）

	private static readonly string[] CatNames = { "地理", "气候", "人文" };

	public override void _Ready()
	{
		SetupCategoryButtons();   // 分类按钮行：3 个互斥按钮，点分类只切显示（用户拍板）
		SetupLayerButtons();      // 图层按钮行：17 个按钮按 LayerRegistry 动态生成
		InitDockState();          // 初始同步 + 初始收起（布局未稳按最小高算，稳定后补正）
		GD.Print($"[CukHud] 绑定完成 dock={Name}");
	}

	/// <summary>分类按钮行绑定（场景预置 %CatGeo/%CatClim/%CatHum，同 ButtonGroup 互斥）：
	/// 点分类只切显示不改 _layer（用户拍板）。</summary>
	private void SetupCategoryButtons()
	{
		_catButtons = new[] { GetNode<Button>("%CatGeo"), GetNode<Button>("%CatClim"), GetNode<Button>("%CatHum") };
		for (int i = 0; i < _catButtons.Length; i++)
		{
			int cat = i; // 闭包捕获
			_catButtons[i].Pressed += () =>
			{
				_category = (LayerCategory)cat;
				ShowCategoryButtons();   // 只切显示，不改 _layer（用户拍板）
				LogService.Log("CukHud", $"category={CatNames[cat]} layer仍={LayerRegistry.Of(_currentLayer).Name}");
			};
		}
	}

	/// <summary>图层按钮行绑定（行容器在场景 %LayerRow——CukDock/DockBox 内；
	/// 按钮按 LayerRegistry 动态生成，图标/提示辞来自策略；样式从场景分类按钮取单一来源）。</summary>
	private void SetupLayerButtons()
	{
		var hbox = GetNode<HBoxContainer>("%LayerRow");
		var group = new ButtonGroup();
		_layerButtons = new Button[LayerRegistry.All.Count];
		for (int i = 0; i < LayerRegistry.All.Count; i++)
		{
			int idx = i; // 闭包捕获
			var btn = new Button
			{
				Icon = MakeLayerIcon(i),
				TooltipText = LayerRegistry.All[i].Name,
				ToggleMode = true,
				ButtonGroup = group,
				CustomMinimumSize = new Vector2(42, 38),
				IconAlignment = HorizontalAlignment.Center,
				// ⚠️ 2026-08-23 场景化修复：行矩形被锚点 Offset 拉高（-90~0）时，默认 Fill
				//   会把 38px 图标按钮撑满 90px → 盖住下方分类行。ShrinkCenter 保持原高。
				SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
			};
			// ⚠️ 2026-08-31 场景化统一：样式从场景分类按钮取（单一来源 = scenes/ui/CukDock.tscn sub_resource），
			//   不再 C# new StyleBoxFlat——此前代码 pressed=金色 与场景 pressed=暗红 同坞分裂
			var src = _catButtons[0];
			btn.AddThemeStyleboxOverride("normal", src.GetThemeStylebox("normal"));
			btn.AddThemeStyleboxOverride("hover", src.GetThemeStylebox("hover"));
			btn.AddThemeStyleboxOverride("pressed", src.GetThemeStylebox("pressed"));
			btn.AddThemeStyleboxOverride("focus", src.GetThemeStylebox("focus"));
			btn.Pressed += () => { _currentLayer = idx; EmitSignal(SignalName.LayerSelected, idx); };
			hbox.AddChild(btn);
			_layerButtons[i] = btn;
		}
	}

	/// <summary>初始状态：图层/分类同步 + 坞初始收起（只露标题条；布局未稳先按最小高算，
	/// 首次布局稳定后 _Process 补正精确值）。</summary>
	private void InitDockState()
	{
		// 初始同步（外部默认 Layer=0 → 分类=Geo；组件自持按钮态）
		SetLayer(_currentLayer);

		// 初始收起（只露标题条）：布局未稳先按最小高算，首次布局稳定后补正精确值
		ApplyCukDockTo(CollapsedPos(), instant: true);
	}

	/// <summary>每帧坞状态：指针在面板矩形内 → 目标=展开；移出 → 目标=收起（2026-08-31 用户拍板：不做 0.4s 防抖延迟）。
	/// 显式状态判定：动画中且目标没变 → 不打扰；动画中目标变了（反复进出）→ 从 current 折返；
	/// 静止已对齐 → 无事；静止未对齐且意图没变（布局漂移）→ instant 补正；意图变了（鼠标移入/移出）→ 动画。
	/// 矩形判定跟随真实绘制区域（含坞内按钮），不依赖 hover 信号的子控件坑。
	/// ⚠️ 收起态面板大部分在屏幕外（视口裁剪不绘制即可见）——GetGlobalRect 含屏幕外部分，但鼠标
	///    坐标只在窗口内，等效"只露标题条"；鼠标无法触及屏幕外区域。</summary>
	public override void _Process(double delta)
	{
		if (PanelH() <= 1f) return;   // 布局未就绪（高度拿不到）跳过本帧；布局稳定后首次判定自然补正

		var mouse = GetViewport().GetMousePosition();
		bool inZone = GetGlobalRect().HasPoint(mouse);
		Vector2 tgt = inZone ? ExpandedPos() : CollapsedPos();

		if (_tween != null)
		{
			// 动画进行中：目标没变 → 不打扰（同目标重启会每帧 kill+重建 → 卡顿）；变了 → 从 current 折返
			if (_target != tgt) AnimateTo(tgt);
			return;
		}
		if (At(tgt)) { _target = null; return; }   // 静止且已对齐 → 无事

		// 静止未对齐：当前静止态与本次意图一致（如布局高度变化导致坐标漂移）→ instant 补正，不播幽灵动画；
		// 意图变了（鼠标移入/移出）→ 正常动画
		if (NearerExpanded() == inZone) ApplyCukDockTo(tgt, instant: true);
		else AnimateTo(tgt);
	}

	/// <summary>下行：外部（Inspector/代码）改 Layer 时同步按钮态（MapViewer.Layer setter 调用）。
	/// 分类跟随：选中按钮必须在可见集合内；再 ShowCategoryButtons 收敛可见性。</summary>
	public void SetLayer(int layerId)
	{
		if (_layerButtons == null) return;
		_currentLayer = layerId;
		_category = LayerRegistry.Of(layerId).Category;
		for (int i = 0; i < _layerButtons.Length; i++)
			_layerButtons[i].ButtonPressed = i == layerId;
		ShowCategoryButtons();
	}

	/// <summary>按当前分类刷新图层按钮可见性 + 分类按钮按下态（不改 _layer；位置由 DockBox 容器管）。</summary>
	private void ShowCategoryButtons()
	{
		if (_layerButtons == null) return;
		for (int i = 0; i < _layerButtons.Length; i++)
			_layerButtons[i].Visible = LayerRegistry.All[i].Category == _category;
		for (int i = 0; i < _catButtons.Length; i++)
			_catButtons[i].ButtonPressed = (int)_category == i;
		// ⚠️ 2026-08-25 修复：此处不再手动定位 LayerRow（旧 OffsetLeft/Right/Top/Bottom 已删）。
		//   旧实现是屏幕底部锚定行（LayerRow 为 UiLayer 直接子节点）时水平居中的手段；
		//   归航 HUD 重设计把 LayerRow 移入 CukDock/DockBox 容器后，残留 offset 会把整行图标
		//   甩出坞面板（anchors=0 → 相对 DockBox 左上角 ±halfW / -90~-50 的空中）——点击图层
		//   图标/分类按钮触发本方法即"所有图标飞出来"（用户报）。同分类切换时按钮 Visible
		//   不变 → 容器不重新布局 → 飞出成持久态。位置与居中已由 DockBox 接管（场景
		//   CatRow/LayerRow 均设 size_flags_horizontal=4 ShrinkCenter），此处只切可见性。
	}

	// ──────────────────────────────────────────────
	// 坞滑出/滑入（2026-08-31 显式状态模型重构：唯一路径 from=CurrentPos → to=target）
	// ──────────────────────────────────────────────

	/// <summary>面板内容声明高（= GetCombinedMinimumSize.Y：自身 CustomMinimumSize + 子控件最小尺寸合并）。
	/// ⚠️ 2026-08-31 用户拍板：不用 Size.Y——布局结算值会被 minimum-size clamp 写回撑大
	/// （收起态底边在屏外无边界 → "target 跟着撑大值走"的正反馈漂移）；声明值由内容推导、
	/// 不经布局写回 → 恒定。我们写出的 offset 差恒 = 此值 → rect 高恰好 = min → 布局永不 clamp。</summary>
	private float PanelH() => GetCombinedMinimumSize().Y;

	/// <summary>当前 offset 坐标——实时读属性，永不缓存（显式状态模型的"当前坐标"）。
	/// 中途任何变化都以它为动画起点，保证折返/补正永远从真实位置续滑。</summary>
	private Vector2 CurrentPos() => new Vector2(OffsetTop, OffsetBottom);

	/// <summary>展开坐标（全露）：顶边 -h、底边 0——CenterBottom 锚（top=bottom=1.0）下
	/// rect.top = 屏高 + offset_top，底边贴屏底 → 整个 h 高度的面板都在屏内。</summary>
	private Vector2 ExpandedPos() => new Vector2(-PanelH(), 0f);

	/// <summary>收起坐标（只露标题条）：顶边 -CukHeadH、底边 +(h-30) 正号埋入屏下——面板高不变
	/// （offset_bottom - offset_top 恒 = h），整体下移到只留 30px 标题条可见。
	/// ⚠️ 2026-08-31 符号勘误：负号使 rect 倒置、面板浮在屏上（用户报"坞浮在空中"），正号才对。</summary>
	private Vector2 CollapsedPos() => new Vector2(-CukHeadH, PanelH() - CukHeadH);

	/// <summary>当前 offset 是否已对齐目标坐标（差 &lt; eps）。布局未就绪（高不可用）按已对齐处理，
	/// Size 可用后自然补正一次。anchor 固定 1.0 → 窗口 resize 时 rect 自动跟随，无需补正。</summary>
	private bool At(Vector2 p, float eps = 1f)
	{
		if (PanelH() <= 1f) return true;
		return Mathf.Abs(OffsetTop - p.X) < eps && Mathf.Abs(OffsetBottom - p.Y) < eps;
	}

	/// <summary>当前静止位置离展开态近还是收起态近（补正判定用：意图没变的漂移 = instant 补正，
	/// 意图变了 = 动画）。静止态只可能是两者之一，距离平方小于等于即判近。</summary>
	private bool NearerExpanded()
	{
		var c = CurrentPos();
		return c.DistanceSquaredTo(ExpandedPos()) <= c.DistanceSquaredTo(CollapsedPos());
	}

	/// <summary>把面板动画到目标坐标——显式状态模型的唯一路径：from = CurrentPos()（实时读，永真）→ to = target。
	/// instant=true 直接跳（_Ready 初始收起/布局漂移补正）；false 走 CukSlideDur 匀速滑出/滑入。
	/// 已在目标位 → 无操作；动画中换目标 → kill 旧 tween，新 tween 从属性当前值续滑（折返不跳变）。</summary>
	private void ApplyCukDockTo(Vector2 target, bool instant)
	{
		if (At(target))   // 已在目标位（含动画刚结束）→ 无操作，仅清状态
		{
			_tween?.Kill();
			_tween = null;
			_target = null;
			return;
		}
		if (instant)
		{
			_tween?.Kill();
			_tween = null;
			OffsetTop = target.X;      // CenterBottom 锚下 Position setter 不可靠，必须用 Offset
			OffsetBottom = target.Y;
			_target = null;
			return;
		}
		_tween?.Kill();
		var tw = CreateTween();
		tw.SetProcessMode(Tween.TweenProcessMode.Physics);
		tw.SetTrans(Tween.TransitionType.Linear);
		tw.Parallel();
		tw.TweenProperty(this, "offset_top", target.X, CukSlideDur);
		tw.TweenProperty(this, "offset_bottom", target.Y, CukSlideDur);
		tw.Finished += () => { _tween = null; _target = null; };
		_tween = tw;
		_target = target;
	}

	/// <summary>滑出/滑入动画入口（交互动画，恒不 instant）。</summary>
	private void AnimateTo(Vector2 target) => ApplyCukDockTo(target, instant: false);

	/// <summary>图层按钮 SVG 图标（2026-08-21 M4：SVG 随策略走——IconSvg 属性；顺带修复聚落按钮越界）。
	/// 纯直线 M/L/H/V/Z（thorvg 不支持 Q/T/A 曲线）。</summary>
	private static Texture2D MakeLayerIcon(int idx)
	{
		try
		{
			var bytes = System.Text.Encoding.UTF8.GetBytes(LayerRegistry.All[idx].IconSvg);
			var img = new Image();   // LoadSvgFromBuffer 是实例方法（返回 Error）
			if (img.LoadSvgFromBuffer(bytes) != Error.Ok)
			{
				LogService.LogErr("CukHud", $"SVG icon {idx} load failed");
				return null;
			}
			img.Resize(28, 28, Image.Interpolation.Bilinear);
			return ImageTexture.CreateFromImage(img);
		}
		catch (System.Exception e)
		{
			LogService.LogErr("CukHud", $"SVG icon {idx} failed: {e.Message}");
			return null;
		}
	}
}
