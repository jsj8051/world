using Godot;
using System;

using World.MapView;
using World.Services;

namespace World.UI;

/// <summary>
/// 归航地图坞组件（2026-08-31 拆分：原 MapViewer.Ui.cs 的 CukHud 逻辑独立成组件）。
/// 四层模型：表现+容器在 MapViewer.tscn（CukDock/DockBox/HeadRow/CatRow/LayerRow + sb_* 样式），
/// 本组件只做逻辑层——分类/图层按钮的行内状态机 + 坞滑出滑入动画（鼠标位置驱动）。
/// 数据：图层按钮由 LayerRegistry（静态注册表）驱动；_layer 归属 MapViewer（经 SetLayer 下行同步，
/// 不反向持有）；用户点图层 → LayerSelected 信号上行，MapViewer 处理实际切换。
/// </summary>
public partial class CukHud : PanelContainer
{
    /// <summary>图层选择信号（上行：用户点击图层按钮 → MapViewer 订阅后 Layer = id）。</summary>
    [Signal] public delegate void LayerSelectedEventHandler(int layerId);

    // ── 场景骨架（%唯一名，见 MapViewer.tscn CukDock 段）──
    private Button[] _catButtons;        // 3 个分类按钮（场景预置：地理/气候/人文，同 ButtonGroup 互斥）
    private Button[] _layerButtons;      // 17 个图层按钮（按 LayerRegistry 动态生成，图标/提示辞来自策略）

    // ── 坞状态机 ──
    private int _currentLayer;           // 当前图层镜像（MapViewer.SetLayer 下行同步；日志用）
    private LayerCategory _category;     // 当前分类（UI 显示态；点分类只切显示不改 _layer——用户拍板）
    private bool _expanded;              // 展开态（面板全露）——收起态只露标题条
    private Tween _tween;                // 滑出/滑入动画（新动画前 Kill 旧的防串台）
    private const float CukHeadH = 30f;  // 标题条高（场景 HeadRow CustomMinimumSize 同值；收起时唯一露出部分）
    private const float CukSlideDur = 0.25f; // 滑出/滑入时长（2026-08-31 0.15→0.25：原 0.15s+Cubic 起步陡，
                                             //   渲染帧率 ~30fps 时仅 4-5 采样点，每帧大跳 → 用户报"三段式收起"）

    private static readonly string[] CatNames = { "地理", "气候", "人文" };

    public override void _Ready()
    {
        // ── 分类按钮行（场景预置 3 个，ToggleGroup 互斥）──
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

        // ── 图层按钮行（行容器在场景 %LayerRow——CukDock/DockBox 内；按钮按 LayerRegistry 动态生成）──
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
            // ⚠️ 2026-08-31 场景化统一：样式从场景分类按钮取（单一来源 = MapViewer.tscn sub_resource），
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
        // 初始同步（外部默认 Layer=0 → 分类=Geo；组件自持按钮态）
        SetLayer(_currentLayer);

        // 初始收起（只露标题条）：布局未稳先按最小高算，首次布局稳定后补正精确值
        ApplyCukDockPosition(false, instant: true);
        GD.Print($"[CukHud] 绑定完成 dock={Name}");
    }

    /// <summary>每帧坞状态：指针在面板矩形内 → 滑出；移出 → 立即收回（2026-08-31 用户拍板：不做 0.4s 防抖延迟）。
    /// 矩形判定跟随真实绘制区域（含坞内按钮），不依赖 hover 信号的子控件坑。
    /// ⚠️ 收起态面板大部分在屏幕外（视口裁剪不绘制即可见）——GetGlobalRect 含屏幕外部分，但鼠标
    ///    坐标只在窗口内，等效"只露标题条"；鼠标无法触及屏幕外区域。</summary>
    public override void _Process(double delta)
    {
        var mouse = GetViewport().GetMousePosition();
        bool inZone = GetGlobalRect().HasPoint(mouse);
        if (inZone)
        {
            if (!_expanded) AnimateCukDock(true);
        }
        else if (_expanded)
        {
            AnimateCukDock(false);   // 移出 → 立即滑回（无防抖延迟）
        }
        else if (_tween == null && !CukOffsetAligned(false))
        {
            // 仅首次布局稳定/需要时才补正一次；值已对齐 → 跳过（2026-08-31 防卡顿：
            //   每帧 set offset 即使值相同也触发布局 dirty → 收起后静止态持续重排）
            ApplyCukDockPosition(false, instant: true);
        }
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
    // 坞滑出/滑入（2026-08-30 单面板改造：去抓手/去钉住，docs/设计-UX归航HUD改造.md）
    // ──────────────────────────────────────────────

    /// <summary>当前 offset 是否已精确对齐目标态（补正前置判定）：
    /// 对齐（差 &lt;1px）→ 跳过 setter；布局未就绪（高不可用）按已对齐处理，
    /// Size 可用后自然补正一次。anchor 固定 1.0 → 窗口 resize 时 rect 自动跟随，无需补正。</summary>
    private bool CukOffsetAligned(bool expand)
    {
        float h = Size.Y;
        if (h <= 1f) h = GetCombinedMinimumSize().Y;
        if (h <= 1f) return true;
        float top = expand ? -h : -CukHeadH;
        float bot = expand ? 0f : (h - CukHeadH);
        return Mathf.Abs(OffsetTop - top) < 1f && Mathf.Abs(OffsetBottom - bot) < 1f;
    }

    /// <summary>把面板 offset 对齐到目标态（expand=true 全露 / false 只露标题条）。
    /// instant=true 直接跳（初始收起/补正）；false 走 CukSlideDur 平滑滑出/滑入。
    /// 位置用 OffsetTop/OffsetBottom——CenterBottom 锚点（anchors preset 7）下 Position setter
    ///   不可靠（同 RebuildLegend 备注："已入树必须用 Offset"）。两 offset 同步增减 → 面板高不变。</summary>
    private void ApplyCukDockPosition(bool expand, bool instant)
    {
        float h = Size.Y;
        if (h <= 1f) h = GetCombinedMinimumSize().Y;   // 布局未稳兜底（标题条+分类行+图层行+间距）
        if (h <= 1f) return;
        float top = expand ? -h : -CukHeadH;
        // ⚠️ 2026-08-31 修复符号：CenterBottom 锚（top=bottom=1.0）下 rect.bottom = 屏高 + offset_bottom——
        //   收起要埋进地里只露标题条，面板底部须在屏幕下方 → offset_bottom = +(h-30)（正号）。
        //   原实现 -(h-30)（负号，设计文档旧公式照抄）→ rect.bottom=屏高-(h-30) < rect.top，
        //   面板高为负被最小尺寸撑开后浮在屏幕底部上方（用户报"坞浮在空中不贴底"）。
        float bot = expand ? 0f : (h - CukHeadH);
        if (instant)
        {
            _tween?.Kill();
            _tween = null;
            OffsetTop = top;
            OffsetBottom = bot;
            _expanded = expand;
            return;
        }
        _tween?.Kill();
        var tw = CreateTween();
        // ⚠️ 2026-08-31 三段式收起修复：Physics 节拍（60Hz 物理帧插值，渲染帧率低时动画不平滑的根因——
        //   原 Idle 模式按渲染帧采样，30fps 下 Cubic Out 起步陡 → 每帧大跳成视觉分段）+ Quad（起步缓于 Cubic）
        tw.SetProcessMode(Tween.TweenProcessMode.Physics);
        tw.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tw.Parallel();   // ⚠️ 平行模式：offset_top 与 offset_bottom 同增减 → 高度不变，纯位移
        tw.TweenProperty(this, "offset_top", top, CukSlideDur);
        tw.TweenProperty(this, "offset_bottom", bot, CukSlideDur);
        tw.Finished += () => _tween = null;
        _tween = tw;
        _expanded = expand;
    }

    /// <summary>滑出/滑入动画入口（CukSlideDur Tween）。</summary>
    private void AnimateCukDock(bool expand) => ApplyCukDockPosition(expand, instant: false);

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