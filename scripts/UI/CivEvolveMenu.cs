using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.CivSim;
using World.Diagnostics;
using World.LogicGrid;
using World.MapView;
using World.Services;

namespace World.UI;

/// <summary>
/// 文明演化界面：选自然地图(.mpa/.gmp) → seed/起源数 → 后台演化（CivEngine.Run，纯 C#）→
/// 写 .cmp 游玩存档 + 摘要（部落/人口/时代分布/耗时）→ 开始游戏（MapViewer 加载 .cmp）。
/// 静态骨架（背景/窗口框/标题/状态栏/列表区/按钮/进度条）在 CivEvolveMenu.tscn 场景中定义；
/// 脚本做动态部分：地图列表卡片注入、参数 SpinBox/滑块、演化逻辑。
/// 地图列表行：卡片样式（选中高亮 accent 外框，与存档界面行风格统一）。
/// 演化在后台线程（n=64 约 30 秒），UI 不卡；读档/写档在主线程（Godot FileAccess 非线程安全）。
/// </summary>
public partial class CivEvolveMenu : Control
{
    private const string MapsDir = "user://maps/";

    private Button _backBtn;
    private Button _evolveBtn;
    private Button _playBtn;
    private Label _status;
    private Label _statusDot;
    private ProgressTextBar _bar;     // 进度条（自绘文本：阶段+百分比一体）
    private VBoxContainer _listBox;
    private HBoxContainer _paramRow;
    private Control _barWrap;
    private SpinBox _seedSpin;
    private SpinBox _originsSpin;
    private string _selectedPath;      // 选中的自然地图
    private string _selectedName;
    private string _cmpOutPath;
    private Button _selectedBtn;

    private bool _evolving;
    private GameGrid _grid;            // 演化输入（主线程读档）
    private int _evolveSeed;
    private int _evolveOrigins;
    private CivSimResult _pendingResult;   // 后台线程写、主线程 _Process 读
    private string _pendingError;
    private volatile float _progress;      // 后台线程写（tick 级）、主线程 _Process 读

    public override void _Ready()
    {
        // 根 Control 强制全屏（防场景根未自动拉伸）
        SetAnchorsPreset(LayoutPreset.FullRect);

        // 取场景节点
        _backBtn = GetNode<Button>("%BackBtn");
        _evolveBtn = GetNode<Button>("%EvolveBtn");
        _playBtn = GetNode<Button>("%PlayBtn");
        _status = GetNode<Label>("%Status");
        _statusDot = GetNode<Label>("%StatusDot");
        _listBox = GetNode<VBoxContainer>("%ListBox");
        _paramRow = GetNode<HBoxContainer>("%ParamRow");
        _bar = GetNode<ProgressTextBar>("%Bar");
        _barWrap = GetNode<Control>("%BarWrap");

        // 事件绑定
        _backBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MainMenu.tscn");
        _evolveBtn.Pressed += OnEvolvePressed;
        _playBtn.Pressed += OnPlayPressed;
        _barWrap.Visible = false;

        // 状态点呼吸动画
        var tween = CreateTween().SetLoops();
        tween.TweenProperty(_statusDot, "modulate:a", 0.35f, 1.2f);
        tween.TweenProperty(_statusDot, "modulate:a", 1f, 1.2f);

        // ── 参数区（注入 ParamRow）──
        var seedBox = new VBoxContainer();
        seedBox.AddThemeConstantOverride("separation", 4);
        var seedLabel = new Label { Text = "演化种子" };
        seedLabel.AddThemeFontSizeOverride("font_size", 13);
        seedLabel.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
        _seedSpin = new SpinBox { Value = 42, MinValue = 1, MaxValue = 999999 };
        _seedSpin.GetLineEdit().CustomMinimumSize = new Vector2(120, 0);
        seedBox.AddChild(seedLabel);
        seedBox.AddChild(_seedSpin);
        _paramRow.AddChild(seedBox);

        var originsBox = new VBoxContainer();
        originsBox.AddThemeConstantOverride("separation", 4);
        var originsLabel = new Label { Text = "起源部落数" };
        originsLabel.AddThemeFontSizeOverride("font_size", 13);
        originsLabel.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
        var originsRow = new HBoxContainer();
        originsRow.AddThemeConstantOverride("separation", 10);
        _originsSpin = new SpinBox { Value = 3, MinValue = 1, MaxValue = 6 };
        _originsSpin.GetLineEdit().CustomMinimumSize = new Vector2(60, 0);
        var originsSlider = new HSlider { MinValue = 1, MaxValue = 6, Step = 1, Value = 3, CustomMinimumSize = new Vector2(140, 0) };
        _originsSpin.ValueChanged += v => originsSlider.Value = v;
        originsSlider.ValueChanged += v => _originsSpin.Value = v;
        originsRow.AddChild(originsSlider);
        originsRow.AddChild(_originsSpin);
        originsBox.AddChild(originsLabel);
        originsBox.AddChild(originsRow);
        _paramRow.AddChild(originsBox);

        RefreshList();

        // headless 自动流程（--auto：选第一张图并演化，验证完整链路）
        var ua = OS.GetCmdlineUserArgs();
        for (int i = 0; i < ua.Length; i++)
            if (ua[i] == "--auto")
                CallDeferred(nameof(AutoEvolve));
    }

    private void AutoEvolve()
    {
        var first = _listBox.GetChild<Button>(0);
        if (first != null)
        {
            SelectMap(first.Text, first);
            OnEvolvePressed();
        }
    }

    private void RefreshList()
    {
        foreach (Node c in _listBox.GetChildren()) c.QueueFree();
        var files = new List<string>();
        foreach (var f in DirAccess.GetFilesAt(MapsDir))
            if (f.EndsWith(".mpa") || f.EndsWith(".gmp"))
                files.Add(f);
        files.Sort();
        if (files.Count == 0)
        {
            var emptyTip = new Label { Text = "（没有自然地图——先到「生成地图」创建）" };
            emptyTip.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
            emptyTip.HorizontalAlignment = HorizontalAlignment.Center;
            _listBox.AddChild(emptyTip);
            return;
        }
        foreach (var f in files)
        {
            var btn = new Button
            {
                Text = f,
                Alignment = HorizontalAlignment.Left,
                ToggleMode = true,
                CustomMinimumSize = new Vector2(0, 48),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            btn.AddThemeFontSizeOverride("font_size", 15);
            // 卡片样式（normal=卡片；选中/悬停=accent 外框）
            btn.AddThemeStyleboxOverride("normal", SaveRowStyle.CardStyle());
            btn.AddThemeStyleboxOverride("hover", SaveRowStyle.CardHoverStyle());
            btn.AddThemeStyleboxOverride("pressed", SaveRowStyle.CardHoverStyle());
            btn.AddThemeStyleboxOverride("focus", SaveRowStyle.CardStyle());
            string captured = f;   // 闭包捕获
            btn.Pressed += () => SelectMap(captured, btn);
            _listBox.AddChild(btn);
        }
    }

    private void SelectMap(string name, Button btn)
    {
        _selectedName = name;
        _selectedPath = MapsDir + name;
        if (_selectedBtn != null)
        {
            _selectedBtn.ButtonPressed = false;
            _selectedBtn.AddThemeStyleboxOverride("normal", SaveRowStyle.CardStyle());
            _selectedBtn.AddThemeStyleboxOverride("hover", SaveRowStyle.CardHoverStyle());
            _selectedBtn.AddThemeStyleboxOverride("pressed", SaveRowStyle.CardHoverStyle());
            _selectedBtn.AddThemeStyleboxOverride("focus", SaveRowStyle.CardStyle());
        }
        _selectedBtn = btn;
        // 选中高亮（accent 外框）
        btn.AddThemeStyleboxOverride("normal", SaveRowStyle.EnterOutline());
        btn.AddThemeStyleboxOverride("hover", SaveRowStyle.EnterOutline());
        btn.AddThemeStyleboxOverride("pressed", SaveRowStyle.EnterOutline());
        btn.AddThemeStyleboxOverride("focus", SaveRowStyle.EnterOutline());
        _status.Text = $"已选 {name} → 输出 {name.GetBaseName()}.cmp";
    }

    private void OnEvolvePressed()
    {
        if (_evolving) return;
        if (string.IsNullOrEmpty(_selectedPath))
        {
            _status.Text = "请先选择一张自然地图。";
            return;
        }
        _cmpOutPath = MapsDir + _selectedName.GetBaseName() + ".cmp";
        _evolveSeed = (int)_seedSpin.Value;
        _evolveOrigins = (int)_originsSpin.Value;

        // 主线程：读自然地图（Godot FileAccess 非线程安全）+ 预加载技术表
        if (!ArchiveDiag.TryLoad(_selectedPath, out var diag))
        {
            _status.Text = "读取自然地图失败。";
            return;
        }
        _grid = GameGrid.FromMapData(diag.Map);
        TechTable.Load();

        _evolving = true;
        _pendingResult = null;
        _pendingError = null;
        _progress = 0f;
        _bar.Value = 0;
        _bar.Prefix = $"（演化中… n={_grid.N}，{(_grid.N >= 10000 ? "约 30 秒" : "约 2 秒")}）";   // 阶段+自绘百分比一体
        _bar.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        _barWrap.Visible = true;
        _evolveBtn.Disabled = _playBtn.Disabled = _backBtn.Disabled = true;
        _status.Text = "";
        LogService.Log("CivEvolveMenu", $"演化开始 {_selectedName} seed={_evolveSeed} origins={_evolveOrigins}");

        // 后台线程：纯 C# 演化（无 Godot API 调用；TechTable 已加载；onProgress 写 volatile）
        var grid = _grid;
        int seed = _evolveSeed, origins = _evolveOrigins;
        Task.Run(() =>
        {
            try
            {
                var r = CivEngine.Run(grid, seed, origins, p => _progress = p);
                _pendingResult = r;
            }
            catch (Exception e)
            {
                _pendingError = e.ToString();
            }
        });
    }

    public override void _Process(double delta)
    {
        if (_evolving && _bar != null)
            _bar.Value = _progress * 100f;   // 后台线程写 volatile，主线程刷 UI；百分比由进度条自带

        if (!_evolving) return;
        if (_pendingResult == null && _pendingError == null) return;
        _evolving = false;

        if (_pendingError != null)
        {
            _status.Text = "演化失败：" + _pendingError;
            _bar.Prefix = "（演化失败）";
            _bar.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
            _evolveBtn.Disabled = _playBtn.Disabled = _backBtn.Disabled = false;
            return;
        }

        // 主线程：写 .cmp + 摘要
        var result = _pendingResult;
        bool ok = CivMapArchive.Write(_cmpOutPath, _grid, result);
        if (!ok)
        {
            _status.Text = "写 .cmp 失败。";
            _bar.Prefix = "（写档失败）";
            _bar.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
            _evolveBtn.Disabled = _playBtn.Disabled = _backBtn.Disabled = false;
            return;
        }

        var c = result.Context;
        int occ = 0;
        for (int i = 0; i < _grid.N; i++) if (c.CellPop[i] > 0f) occ++;
        var relDist = new int[5];
        int agri = 0;
        foreach (var t in c.Tribes)
        {
            int relIdx = World.CivSim.ShareField.ReligionIndex(World.CivSim.ShareField.DomReligion(t.ReligionShare));
            if (relIdx >= 0) relDist[relIdx]++;
            if (t.IsFarming) agri++;
        }
        // 进度条 100%（自绘文本：阶段+百分比一体）；演化摘要显示在顶部状态栏（进度条底下不再显示）
        _bar.Value = 100;
        _bar.Prefix = "（演化完成）";
        _bar.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.6f));
        _status.Text = $"✓ 演化完成：{result.FinalTick} tick × {World.CivSim.CivSimContext.TickYears} 年 = {result.FinalTick * World.CivSim.CivSimContext.TickYears} 年\n" +
                       $"部落 {c.Tribes.Count} · 总人口 {c.TotalPopulation():F0} · 覆盖 {occ}/{_grid.N} 格 · 农业部落 {agri}\n" +
                       $"宗教: 萨满{relDist[1]} 祖先{relDist[2]} 多神{relDist[3]} 一神{relDist[4]} · 文化key {c.CultureKeyCount} 个\n" +
                       $"→ 已保存 {_cmpOutPath}";
        _playBtn.Disabled = false;
        _evolveBtn.Disabled = _backBtn.Disabled = false;
        LogService.Log("CivEvolveMenu", $"演化完成 → {_cmpOutPath} (tribes={c.Tribes.Count} pop={c.TotalPopulation():F0} agri={agri})");
    }

    private void OnPlayPressed()
    {
        if (string.IsNullOrEmpty(_cmpOutPath)) return;
        EventBus.RequestMapView(_cmpOutPath);
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }
}

internal static class VBoxExt
{
    /// <summary>把多个子控件加入 VBox（链式构建用）。</summary>
    public static VBoxContainer AddChildren(this VBoxContainer box, params Control[] children)
    {
        foreach (var ch in children) box.AddChild(ch);
        return box;
    }
}