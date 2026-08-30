using Godot;
using System.Linq;
using World.Services;

namespace World.Diagnostics;

/// <summary>存档界面截图（2026-08-23 UI 视觉验证）：窗口模式实例化 SaveSelectMenu，渲染数帧后截图存 PNG。
/// 用法（窗口模式，非 headless——headless 无渲染管线截图全黑）：
///   Godot --path E:/godotGames/world res://scenes/diag/UiShotDiag.tscn -- --tab=map|cmp --out=user://maps/ui.png
/// 参数：--tab=map（默认）|cmp 选择标签页；--out 输出路径；--dialog=1 模拟点删除钮截确认弹窗。
/// 2026-08-23 晚：合并版界面（SaveSelectMenu），支持 --tab 与多分辨率窗口。</summary>
public partial class UiShotDiag : Node
{
    private int _frame;
    private string _outPath = "user://maps/ui_shot.png";
    private bool _showDeleteDialog;   // --dialog=1：截删除确认弹窗（遮罩+对话框）
    private bool _triggerStart;       // --start=1：模拟点「开始生成」（截生成中状态）
    private int _shotFrame = 8;       // 截图帧号（--shot-frame=N；生成完成态需等生成结束）
    private bool argsTryDumpUi;      // --dump-ui=1：截图前打印 HUD 节点 rect
    private int _tabIdx = -1;        // --tab=N：mapgen 模拟点第 N 个页签（-1=不模拟）
    private int _viewerLayer = -1;   // --viewer-layer=N：viewer 场景截图前切到指定图层（-1=不切）
    private int _civTabIdx = -1;     // --civ-tab=N：viewer 截图前模拟点 CivPanel 第 N 个页签（-1=不模拟）
    private int _civScroll = -1;     // --civ-scroll=N：截图前把 CivScroll 垂直滚动到 N 像素（-1=不动）
    private bool _cukMap;            // --cuk-map=1：截图前展开潜藏地图坞（验证展开态）
    private bool _diagRect;          // --diag-rect=1：截图前打印 CukGrip/CukDock/EpochPanel 的 rect/visible
    private bool _cukWarp;            // --cuk-warp=1：截图前把鼠标 warp 到抓手中心（触发 ProcessCukHud 展开流程）
    private bool _cukClickIcon;       // --cuk-click-icon=1：warp 到坞内首个图层图标并模拟点击（验证点击后坞不收回）
    private bool _playFlag;           // --play=1：viewer 实例化前标记游玩形态（保存按钮可见的前提）
    private bool _saveDiag;           // --save-diag=1：模拟保存流程（点保存→填槽名→确定，产真实 .sav）
    private bool _loadFlag;           // --load=1：save 场景实例化前标记加载存档模式（列 .sav）
    private bool _gameplayFlag;       // --gameplay=1：save 场景实例化前标记正式游玩模式（列含文明 .mpa/.cmp）

    public override void _Ready()
    {
        var args = DiagSceneBase.ParseUserArgs();
        // 目标场景：save（存档，默认）| mapgen | civ | main | viewer
        string scene = args.TryGetValue("scene", out var s) ? s : "save";
        if (args.TryGetValue("out", out var o)) _outPath = o;
        _showDeleteDialog = args.TryGetValue("dialog", out var d) && d == "1";
        _triggerStart = args.TryGetValue("start", out var st) && st == "1";
        if (args.TryGetValue("shot-frame", out var sf) && int.TryParse(sf, out int sfn)) _shotFrame = sfn;
        argsTryDumpUi = args.TryGetValue("dump-ui", out var du) && du == "1";
        // --viewer-layer=N：viewer 场景截图前切到指定图层（人文图层验证用）
        if (args.TryGetValue("viewer-layer", out var vl) && int.TryParse(vl, out int layerIdx))
            _viewerLayer = layerIdx;
        // --tab=N：mapgen 场景模拟点第 N 个页签（截图前延迟一帧执行，等 _Ready 注入完成）
        if (args.TryGetValue("tab", out var tb) && int.TryParse(tb, out int tabIdx))
            _tabIdx = tabIdx;
        // --civ-tab=N：viewer 场景模拟点 CivPanel 页签（截图前 2 帧——等面板渲染完成）
        if (args.TryGetValue("civ-tab", out var ct) && int.TryParse(ct, out int civTab))
            _civTabIdx = civTab;
        // --civ-scroll=N：截图前滚动 CivScroll（验证滚动可用性；配合 civ-tab 用）
        if (args.TryGetValue("civ-scroll", out var csc) && int.TryParse(csc, out int civScrollPx))
            _civScroll = civScrollPx;
        _cukMap = args.TryGetValue("cuk-map", out var cm) && cm == "1";
        _diagRect = args.TryGetValue("diag-rect", out var dr) && dr == "1";
        _cukWarp = args.TryGetValue("cuk-warp", out var cw) && cw == "1";
        _cukClickIcon = args.TryGetValue("cuk-click-icon", out var cci) && cci == "1";
        _playFlag = args.TryGetValue("play", out var pf) && pf == "1";               // viewer 进入游玩形态（保存按钮可见）
        _saveDiag = args.TryGetValue("save-diag", out var sd) && sd == "1";          // 端到端保存：点保存→填槽名→确定（产真实 .sav）
        _loadFlag = args.TryGetValue("load", out var lf) && lf == "1";               // save 场景进入「加载存档」模式（列 .sav）
        _gameplayFlag = args.TryGetValue("gameplay", out var gf) && gf == "1";     // save 场景进入「正式游玩」模式（列含文明 .mpa/.cmp）

        // 窗口尺寸：--w=1280 --h=720（不传则用命令行 --resolution 或项目默认）
        if (args.TryGetValue("w", out var w) && int.TryParse(w, out int ww))
            GetWindow().Size = new Vector2I(ww, GetWindow().Size.Y);
        if (args.TryGetValue("h", out var h) && int.TryParse(h, out int hh))
            GetWindow().Size = new Vector2I(GetWindow().Size.X, hh);

        string scenePath = scene switch
        {
            "mapgen" => "res://scenes/core/MapGenMenu.tscn",
            "civ" => "res://scenes/core/CivEvolveMenu.tscn",
            "main" => "res://scenes/core/MainMenu.tscn",
            "viewer" => "res://scenes/core/MapViewer.tscn",
            _ => "res://scenes/core/SaveSelectMenu.tscn",
        };
        // MapViewer 是 Node3D 根（挂相机/灯光），其余是 Control 菜单
        var packed = GD.Load<PackedScene>(scenePath);
        var root = packed.Instantiate();
        if (scene == "viewer" && _playFlag)
            EventBus.MarkGameplayMap();   // 实例化前标记（MapViewer._Ready 消费——游玩形态）
        else if (scene == "save" && _loadFlag)
            EventBus.RequestLoadSelect();  // 加载存档模式（SaveSelectMenu._Ready 消费——列 .sav）
        else if (scene == "save" && _gameplayFlag)
            EventBus.RequestGameplaySelect();   // 正式游玩模式（列含文明 .mpa/.cmp）
        AddChild(root);
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 3 && _tabIdx >= 0 && GetChild(0) is Control tabMenu)
        {
            // mapgen 页签模拟：调私有 ShowCategory(idx)
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var m = tabMenu.GetType().GetMethod("ShowCategory", flags);
            m?.Invoke(tabMenu, new object[] { _tabIdx });
        }
        // viewer 图层切换：截图前 30 帧切（MapViewer.Layer 公开属性；等加载/重建颜色完成）
        if (_viewerLayer >= 0 && _frame == Mathf.Max(1, _shotFrame - 30) && GetChild(0) is Node3D viewerRoot)
        {
            var layerProp = viewerRoot.GetType().GetProperty("Layer");
            layerProp?.SetValue(viewerRoot, _viewerLayer);
            GD.Print($"UiShotDiag: 已切 viewer 图层 → {_viewerLayer}");
        }
        // CivPanel 页签模拟：截图前 2 帧点（面板渲染稳定后；按钮 Pressed 触发 RenderCurrent）
        if (_civTabIdx >= 0 && _frame == Mathf.Max(1, _shotFrame - 2))
        {
            var tabs = FindChild("CivTabs", recursive: true, owned: false) as HBoxContainer;
            if (tabs != null && _civTabIdx < tabs.GetChildCount() && tabs.GetChild(_civTabIdx) is Button tabBtn)
            {
                tabBtn.EmitSignal(BaseButton.SignalName.Pressed);
                GD.Print($"UiShotDiag: 已模拟点 CivPanel 页签 → {_civTabIdx}");
            }
        }
        // CivScroll 滚动模拟：截图前 1 帧设滚动位置（验证滚动机制；内容超长时可滚到后半段）
        if (_civScroll >= 0 && _frame == Mathf.Max(1, _shotFrame - 1))
        {
            var scroll = FindChild("CivScroll", recursive: true, owned: false) as ScrollContainer;
            if (scroll != null)
            {
                scroll.ScrollVertical = _civScroll;
                GD.Print($"UiShotDiag: 已滚动 CivScroll → {_civScroll}（max={scroll.GetVScrollBar().MaxValue}）");
            }
        }
        // 潜藏地图坞展开模拟：截图前 3 帧（等布局稳定；直接置 CukDock 可见 = hover 展开态）
        if (_cukMap && _frame == Mathf.Max(1, _shotFrame - 3))
        {
            var dock = FindChild("CukDock", recursive: true, owned: false) as PanelContainer;
            if (dock == null)
                dock = FindChild("Dock", recursive: true, owned: false) as PanelContainer;
            if (dock != null)
            {
                dock.Visible = true;
                GD.Print("UiShotDiag: 已展开潜藏地图坞（--cuk-map）");
            }
        }
        // 布局诊断：打印新 UI 节点在窗口里的实际 rect（截图前 1 帧）
        if (_diagRect && _frame == Mathf.Max(1, _shotFrame - 1))
        {
            var vp = GetViewport();
            GD.Print($"UiShotDiag: viewport={vp.GetVisibleRect().Size} mouse={vp.GetMousePosition()} (物理窗口 {GetWindow().Size})");
            foreach (var nm in new[] { "CukGrip", "CukDock", "EpochPanel", "CivPanelBody" })
            {
                var n = FindChild(nm, recursive: true, owned: false) as Control;
                GD.Print($"UiShotDiag: {nm} → {(n == null ? "NULL" : $"visible={n.Visible} rect={n.GetGlobalRect()}")}");
            }
        }
        // ── 潜藏坞 hover 展开端到端验证：warp 鼠标到抓手中心 → ProcessCukHud 应自动展开 ──
        if (_cukWarp && _frame == Mathf.Max(1, _shotFrame - 12))
        {
            var grip = FindChild("CukGrip", recursive: true, owned: false) as Control;
            if (grip != null)
            {
                var vp = GetViewport();
                Vector2 c = grip.GetGlobalRect().GetCenter();
                vp.WarpMouse(c);
                GD.Print($"UiShotDiag: 鼠标 warp → {c}（ProcessCukHud 应展开坞）");
            }
        }
        if (_cukWarp && _frame == Mathf.Max(1, _shotFrame - 8))
        {
            var dock = FindChild("CukDock", recursive: true, owned: false) as Control;
            GD.Print($"UiShotDiag: warp 后 Dock visible={dock?.Visible}");
            if (dock != null && !dock.Visible)
                GD.Print("UiShotDiag: ⚠️ ProcessCukHud 未自动展开——问题复现");
        }
        // ── 点击坞内首个图层图标（验证点击后坞不收回）──
        if (_cukClickIcon && _frame == Mathf.Max(1, _shotFrame - 4))
        {
            var dock = FindChild("CukDock", recursive: true, owned: false) as Control;
            var btn = dock?.FindChildren("*", recursive: true, owned: false)
                .OfType<Button>().FirstOrDefault(b => b.ToggleMode && b.GetParent() is HBoxContainer);
            if (btn != null)
            {
                var vp = GetViewport();
                vp.WarpMouse(btn.GetGlobalRect().GetCenter());
                var press = new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = true,
                    Position = btn.GetGlobalRect().GetCenter(),
                };
                Input.ParseInputEvent(press);
                var release = new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = false,
                    Position = btn.GetGlobalRect().GetCenter(),
                };
                Input.ParseInputEvent(release);
                GD.Print($"UiShotDiag: 模拟点击图层图标 {btn.Name} @{btn.GetGlobalRect().GetCenter()}");
            }
        }
        if (_frame == 5 && _triggerStart)
        {
            // 模拟点「开始生成」：设半径 12km（n≈6）+ 大陆块数 2（n/2=3 ≥ 2 校验通过，秒级完成），再调 StartGenerate
            var menu = GetChild(0);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            if (menu.GetType().GetField("_radiusSpin", flags)?.GetValue(menu) is Godot.Range radiusSpin)
            {
                radiusSpin.Value = 12f;
                GD.Print("UiShotDiag: 半径已设为 12km（n≈6，秒级完成）");
            }
            if (menu.GetType().GetField("_continentsSpin", flags)?.GetValue(menu) is Godot.Range continentsSpin)
            {
                continentsSpin.Value = 2f;   // 防「大陆块数>n/2」校验拦截
            }
            var m = menu.GetType().GetMethod("StartGenerate", flags);
            if (m != null)
            {
                m.Invoke(menu, null);
                GD.Print("UiShotDiag: 已触发 StartGenerate");
            }
        }
        if (_frame == 4 && _showDeleteDialog)
        {
            // 触发第一个存档行的删除确认（模拟点 🗑）。行结构（2026-08-23 起）：
            // row(Button) → HBox[icon, body, btns(HBox)] → btns[进入, 🗑]，🗑 在 btns 末位。
            var list = FindChild("List", recursive: true, owned: false) as VBoxContainer;   // 节点名 List（%List），_list 是 C# 字段名
            if (list != null && list.GetChildCount() > 0)
            {
                foreach (var child in list.GetChildren())
                {
                    if (child is Button row && row.GetChild(0) is HBoxContainer h && h.GetChildCount() > 2
                        && h.GetChild(h.GetChildCount() - 1) is HBoxContainer btns && btns.GetChildCount() > 1
                        && btns.GetChild(btns.GetChildCount() - 1) is Button del)
                    {
                        del.EmitSignal(BaseButton.SignalName.Pressed);
                        break;
                    }
                }
            }
        }
        // ── 保存流程端到端：点「💾 保存」→ 弹槽名模态框 → 填名 → 确定（产真实 .sav 到 userdata/saves/）──
        if (_saveDiag && _frame == Mathf.Max(1, _shotFrame - 8))
        {
            var btn = FindChild("SaveBtn", recursive: true, owned: false) as Button;
            btn?.EmitSignal(BaseButton.SignalName.Pressed);
            GD.Print($"UiShotDiag: 点击保存按钮（{(btn != null ? "找到" : "未找到 SaveBtn")}）");
        }
        if (_saveDiag && _frame == Mathf.Max(1, _shotFrame - 5))
        {
            var input = FindChild("SaveInput", recursive: true, owned: false) as LineEdit;
            if (input != null) input.Text = "诊断存档";
            var ok = FindChild("SaveOk", recursive: true, owned: false) as Button;
            ok?.EmitSignal(BaseButton.SignalName.Pressed);
            GD.Print($"UiShotDiag: 确认保存「诊断存档」（{ok != null}）");
        }
        if (_frame == _shotFrame)   // 渲染稳定后截图
        {
            // --dump-ui=1：截图前打印 viewer HUD 各节点运行时 rect（排查错位）
            if (argsTryDumpUi)
            {
                var layer = FindChild("UiLayer", recursive: true, owned: false);
                if (layer != null)
                    foreach (Node c in layer.GetChildren())
                        if (c is Control cc)
                            GD.Print($"[UiShotDiag] HUD {c.Name}: pos={cc.Position} size={cc.Size} visible={cc.Visible}");
            }
            var img = GetViewport().GetTexture().GetImage();
            // ⚠️ 2026-08-25：Godot SavePng 用 user:// 语义（C 盘 app_userdata）——统一转 UserPaths（游戏目录旁）
            img.SavePng(UserPaths.Resolve(_outPath).Replace('\\', '/'));
            GD.Print($"UiShotDiag: 已截图 → {_outPath} ({img.GetWidth()}x{img.GetHeight()})");
            GetTree().Quit(0);
        }
    }
}