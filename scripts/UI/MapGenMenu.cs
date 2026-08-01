using Godot;
using System;
using World.MapGen;

namespace World.UI;

/// <summary>
/// 生成地图界面：参数选择（种子/网格分辨率/板块数/模拟时长）→ 后台生成（进度条）→ 完成。
/// 生成在后台线程跑（MapGenerator.GenerateAsync），UI 不卡。
/// </summary>
public partial class MapGenMenu : Control
{
    private SpinBox _seedBox;
    private OptionButton _gridNBox;
    private SpinBox _platesBox;
    private OptionButton _myBox;
    private Button _startBtn;
    private Button _backBtn;
    private ProgressBar _bar;
    private Label _status;
    private VBoxContainer _form;
    private bool _generating;

    private MapGenerator _gen;
    private volatile float _progress;   // 后台线程写、主线程 _Process 读
    private string _lastOutPath;

    public override void _Ready()
    {
        // 背景
        var bg = new ColorRect { Color = new Color(0.06f, 0.08f, 0.12f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;   // 不拦截点击
        AddChild(bg);

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.Alignment = BoxContainer.AlignmentMode.Center;
        root.AddThemeConstantOverride("separation", 16);
        root.MouseFilter = MouseFilterEnum.Ignore;  // 容器不拦截，子控件自己响应
        AddChild(root);

        var title = new Label
        {
            Text = "🛠  生成地图",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 40);
        root.AddChild(title);

        _form = new VBoxContainer();
        _form.CustomMinimumSize = new Vector2(480, 0);
        _form.AddThemeConstantOverride("separation", 12);
        root.AddChild(_form);

        // ── 参数行 ──
        _form.AddChild(MakeRow("种子（Seed）", _seedBox = MakeSpin(0, 999999, 42, 1)));
        _form.AddChild(MakeRow("网格分辨率（顶点数）", _gridNBox = MakeGridOption()));

        var platesRow = MakeRow("初始板块数", _platesBox = MakeSpin(2, 32, 8, 1));
        _form.AddChild(platesRow);

        _form.AddChild(MakeRow("模拟时长", _myBox = MakeMyOption()));

        // ── 按钮区 ──
        var btnRow = new HBoxContainer { CustomMinimumSize = new Vector2(480, 0) };
        btnRow.AddThemeConstantOverride("separation", 16);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        _form.AddChild(btnRow);

        _backBtn = MakeBtn("← 返回", 24);
        _backBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
        btnRow.AddChild(_backBtn);

        _startBtn = MakeBtn("开始生成", 24);
        _startBtn.Pressed += StartGenerate;
        btnRow.AddChild(_startBtn);

        // ── 进度区（生成时显示）──
        _bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(480, 30),
            Visible = false,
        };
        root.AddChild(_bar);

        _status = new Label
        {
            Text = "选择参数后点击「开始生成」。生成需数分钟，可在后台进行。",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(560, 0),
        };
        _status.AddThemeFontSizeOverride("font_size", 18);
        _status.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        root.AddChild(_status);
    }

    public override void _Process(double delta)
    {
        // 后台线程写 volatile 进度 → 主线程更新进度条（Godot Control 属性非线程安全）
        if (_bar.Visible)
            _bar.Value = _progress * 100f;
    }

    // ── UI 构建辅助 ──

    private HBoxContainer MakeRow(string label, Control field)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(480, 44) };
        row.AddThemeConstantOverride("separation", 12);
        var lbl = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(220, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.AddThemeFontSizeOverride("font_size", 20);
        row.AddChild(lbl);
        field.CustomMinimumSize = new Vector2(240, 40);
        row.AddChild(field);
        return row;
    }

    private SpinBox MakeSpin(int min, int max, int val, int step)
    {
        var sb = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Value = val,
            Step = step,
            AllowGreater = false,
            AllowLesser = false,
        };
        return sb;
    }

    private OptionButton MakeGridOption()
    {
        var ob = new OptionButton();
        ob.AddItem("n=16 · 2,562 顶点（快）", 16);
        ob.AddItem("n=32 · 10,242 顶点（推荐）", 32);
        ob.AddItem("n=64 · 40,962 顶点（精细，慢）", 64);
        ob.Selected = 1;   // n=32 默认
        return ob;
    }

    private OptionButton MakeMyOption()
    {
        var ob = new OptionButton();
        ob.AddItem("300 百万年（快）", 300);
        ob.AddItem("600 百万年（推荐）", 600);
        ob.AddItem("1200 百万年（漫长）", 1200);
        ob.Selected = 1;
        return ob;
    }

    private Button MakeBtn(string text, int size)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(180, 52) };
        b.AddThemeFontSizeOverride("font_size", size);
        return b;
    }

    // ── 生成逻辑 ──

    private void StartGenerate()
    {
        if (_generating) return;
        _generating = true;
        _startBtn.Disabled = true;
        _backBtn.Disabled = true;

        _progress = 0f;
        _bar.Visible = true;
        _status.Text = "生成中…（板块模拟阶段）";

        int seed = (int)_seedBox.Value;
        int n = _gridNBox.GetSelectedId();
        int plates = (int)_platesBox.Value;
        int my = _myBox.GetSelectedId();
        _lastOutPath = $"user://maps/map_seed{seed}_n{n}.mpa";

        _gen = new MapGenerator
        {
            Seed = seed,
            TectonicsGridN = n,
            NumPlates = plates,
            SimMegayears = my,
            SimStepMy = 2f,
            OutputPath = _lastOutPath,
            ExportPreview = false,   // UI 模式不导出 PNG，省时间
            AutoQuit = false,
        };
        _gen.SetAsyncDoneCallback(OnGenerateDone);
        _gen.GenerateAsync(
            p => _progress = p,     // 后台线程写 volatile（线程安全）
            (ok, path) => { });     // 实际完成回调走 SetAsyncDoneCallback（主线程）
        GD.Print($"[MapGenMenu] 开始生成 seed={seed} n={n} plates={plates} {my}My → {_lastOutPath}");
    }

    private void OnGenerateDone(bool ok, string path)
    {
        _generating = false;
        _startBtn.Disabled = false;
        _backBtn.Disabled = false;
        _bar.Visible = false;

        if (ok)
        {
            _status.Text = "✅ 生成完成！存档：" + path.GetFile() + "（已保存，可返回主菜单进入游戏）";
            _status.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.6f));
            // 生成完可直接查看
            var viewBtn = MakeBtn("▶ 查看地图", 22);
            viewBtn.Pressed += () => EnterViewer(path);
            _status.GetParent().AddChild(viewBtn);
            GD.Print($"[MapGenMenu] 生成完成: {path}");
        }
        else
        {
            _status.Text = "❌ 生成失败，请查看控制台日志。";
            _status.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
        }
    }

    private void EnterViewer(string path)
    {
        // MapViewer 场景加载后自动读默认 map1.mpa；这里通过全局单例传路径。
        // 简单方案：MapViewer 支持命令行/user args 不可行（运行时），
        // 用静态字段传递：MapViewer 的 MapPath 属性在 _Ready 前可被设置。
        ViewerLauncher.PendingPath = path;
        GetTree().ChangeSceneToFile("res://scenes/MapViewer.tscn");
    }
}

/// <summary>场景间传递"要查看的存档路径"（MapViewer._Ready 读取后清空）。</summary>
public static class ViewerLauncher
{
    public static string PendingPath;
}
