using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.CivSim;
using World.Diagnostics;
using World.LogicGrid;
using World.MapView;

namespace World.UI;

/// <summary>
/// 文明演化界面：选自然地图(.mpa/.gmp) → seed/起源数 → 后台演化（CivEngine.Run，纯 C#）→
/// 写 .cmp 游玩存档 + 摘要（部落/人口/时代分布/耗时）→ 开始游戏（MapViewer 加载 .cmp）。
/// 演化在后台线程（n=64 约 30 秒），UI 不卡；读档/写档在主线程（Godot FileAccess 非线程安全）。
/// </summary>
public partial class CivEvolveMenu : Control
{
    private const string MapsDir = "user://maps/";

    private Button _backBtn;
    private VBoxContainer _listBox;
    private SpinBox _seedSpin;
    private SpinBox _originsSpin;
    private Button _evolveBtn;
    private Button _playBtn;
    private Label _status;
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
    private ProgressBar _bar;
    private volatile float _progress;      // 后台线程写（tick 级）、主线程 _Process 读

    public override void _Ready()
    {
        var bg = new ColorRect { Color = new Color(0.06f, 0.08f, 0.12f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.Alignment = BoxContainer.AlignmentMode.Center;
        root.AddThemeConstantOverride("separation", 14);
        root.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(root);

        // 居中列（960px，项目 UI 规范）——⚠️ 2026-08-06：SizeFlagsHorizontal=ShrinkCenter，
        // 否则 VBox 子控件被拉伸到全宽、内容左对齐（用户反馈整体偏左）
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(960, 0) };
        col.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        col.AddThemeConstantOverride("separation", 14);
        col.MouseFilter = MouseFilterEnum.Ignore;
        root.AddChild(col);

        var title = new Label { Text = "🌱  文明演化", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 40);
        col.AddChild(title);
        var subtitle = new Label
        {
            Text = "从自然地图演化出游玩地图（石器→新石器→…部落异步推进；自然地图只读，输出 .cmp）",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subtitle.AddThemeFontSizeOverride("font_size", 16);
        col.AddChild(subtitle);

        // ── 自然地图选择 ──
        col.AddChild(new Label { Text = "选择自然地图（.mpa/.gmp）：" });
        var listScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 220) };
        listScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        col.AddChild(listScroll);
        _listBox = new VBoxContainer();
        _listBox.AddThemeConstantOverride("separation", 6);
        listScroll.AddChild(_listBox);
        RefreshList();

        // ── 参数行 ──
        var paramRow = new HBoxContainer { CustomMinimumSize = new Vector2(0, 0) };
        paramRow.AddThemeConstantOverride("separation", 24);
        col.AddChild(paramRow);

        var seedLabel = new Label { Text = "演化种子" };
        _seedSpin = new SpinBox { Value = 42, MinValue = 1, MaxValue = 999999 };
        _seedSpin.GetLineEdit().CustomMinimumSize = new Vector2(120, 0);
        paramRow.AddChild(new VBoxContainer { }.AddChildren(seedLabel, _seedSpin));

        var originsLabel = new Label { Text = "起源部落数" };
        var originsBox = new HBoxContainer();
        _originsSpin = new SpinBox { Value = 3, MinValue = 1, MaxValue = 6 };
        _originsSpin.GetLineEdit().CustomMinimumSize = new Vector2(60, 0);
        var originsSlider = new HSlider { MinValue = 1, MaxValue = 6, Step = 1, Value = 3, CustomMinimumSize = new Vector2(180, 0) };
        _originsSpin.ValueChanged += v => originsSlider.Value = v;
        originsSlider.ValueChanged += v => _originsSpin.Value = v;
        originsBox.AddChild(originsSlider);
        originsBox.AddChild(_originsSpin);
        paramRow.AddChild(new VBoxContainer { }.AddChildren(originsLabel, originsBox));

        // ── 按钮行 ──
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 16);
        col.AddChild(btnRow);

        _evolveBtn = new Button { Text = "▶  开始演化", CustomMinimumSize = new Vector2(200, 48) };
        _evolveBtn.Pressed += OnEvolvePressed;
        btnRow.AddChild(_evolveBtn);

        _playBtn = new Button { Text = "🎮  开始游戏", CustomMinimumSize = new Vector2(200, 48), Disabled = true };
        _playBtn.Pressed += OnPlayPressed;
        btnRow.AddChild(_playBtn);

        _backBtn = new Button { Text = "←  返回主菜单", CustomMinimumSize = new Vector2(200, 48) };
        _backBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MainMenu.tscn");
        btnRow.AddChild(_backBtn);

        _status = new Label { Text = "选择一张自然地图后开始演化。", HorizontalAlignment = HorizontalAlignment.Center };
        col.AddChild(_status);

        // 演化进度条（后台线程写 volatile，主线程 _Process 刷新）
        _bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, ShowPercentage = true };
        _bar.CustomMinimumSize = new Vector2(0, 24);
        col.AddChild(_bar);

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
            _listBox.AddChild(new Label { Text = "（没有自然地图——先到「生成地图」创建）" });
            return;
        }
        foreach (var f in files)
        {
            var btn = new Button { Text = f, Alignment = HorizontalAlignment.Left, ToggleMode = true };
            btn.Pressed += () => SelectMap(f, btn);
            _listBox.AddChild(btn);
        }
    }

    private void SelectMap(string name, Button btn)
    {
        _selectedName = name;
        _selectedPath = MapsDir + name;
        if (_selectedBtn != null) _selectedBtn.ButtonPressed = false;
        _selectedBtn = btn;
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
        _evolveBtn.Disabled = _playBtn.Disabled = _backBtn.Disabled = true;
        _status.Text = $"演化中…（n={_grid.N}，{(_grid.N >= 10000 ? "约 30 秒" : "约 2 秒")}，{_evolveSeed} tick × 100 年）";
        GD.Print($"[CivEvolveMenu] 演化开始 {_selectedName} seed={_evolveSeed} origins={_evolveOrigins}");

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
            _bar.Value = _progress * 100f;   // 后台线程写 volatile，主线程刷 UI

        if (!_evolving) return;
        if (_pendingResult == null && _pendingError == null) return;
        _evolving = false;

        if (_pendingError != null)
        {
            _status.Text = "演化失败：" + _pendingError;
            _evolveBtn.Disabled = _playBtn.Disabled = _backBtn.Disabled = false;
            return;
        }

        // 主线程：写 .cmp + 摘要
        var result = _pendingResult;
        bool ok = CivMapArchive.Write(_cmpOutPath, _grid, result);
        if (!ok)
        {
            _status.Text = "写 .cmp 失败。";
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
        _status.Text = $"✓ 演化完成：{result.FinalTick} tick × {World.CivSim.CivSimContext.TickYears} 年 = {result.FinalTick * World.CivSim.CivSimContext.TickYears} 年\n" +
                       $"部落 {c.Tribes.Count} · 总人口 {c.TotalPopulation():F0} · 覆盖 {occ}/{_grid.N} 格 · 农业部落 {agri}\n" +
                       $"宗教: 萨满{relDist[1]} 祖先{relDist[2]} 多神{relDist[3]} 一神{relDist[4]} · 文化key {c.CultureKeyCount} 个\n" +
                       $"→ 已保存 {_cmpOutPath}";
        _playBtn.Disabled = false;
        _evolveBtn.Disabled = _backBtn.Disabled = false;
        GD.Print($"[CivEvolveMenu] 演化完成 → {_cmpOutPath} (tribes={c.Tribes.Count} pop={c.TotalPopulation():F0} agri={agri})");
    }

    private void OnPlayPressed()
    {
        if (string.IsNullOrEmpty(_cmpOutPath)) return;
        ViewerLauncher.PendingPath = _cmpOutPath;
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
