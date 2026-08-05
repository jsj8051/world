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
    private GridContainer _generateGrid;   // 分类：生成参数（两列）
    private GridContainer _planetGrid;     // 分类：星球物理（两列）
    private GridContainer _terrainGrid;    // 分类：地形与水量（两列）
    private Button _genTab;                // 分类页签
    private Button _planetTab;
    private Button _terrainTab;
    private bool _generating;
    private OptionButton _rotBox;   // 自转方向（枚举）
    private SpinBox _gridNSpin;     // 网格分辨率 n（滑动+输入，4~128）
    private SpinBox _tiltSpin;      // 轴向倾角（滑动+输入）
    private SpinBox _distSpin;      // 距太阳距离
    private SpinBox _speedSpin;     // 自转速度
    private SpinBox _oceanSpin;     // 海洋水量
    private SpinBox _scCycleSpin;   // 超级大陆周期
    private SpinBox _erosionSpin;   // 侵蚀强度
    private SpinBox _mySpin;        // 模拟时长（滑动+输入）

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

        // ── 分类页签（固定顶部，水平居中）──
        var tabGroup = new ButtonGroup();
        var tabRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        tabRow.AddThemeConstantOverride("separation", 14);
        tabRow.Alignment = BoxContainer.AlignmentMode.Center;
        root.AddChild(tabRow);

        _genTab = MakeTabBtn("⚙ 生成参数", tabGroup);
        _genTab.Pressed += () => ShowCategory(0);
        tabRow.AddChild(_genTab);

        _planetTab = MakeTabBtn("🪐 星球物理", tabGroup);
        _planetTab.Pressed += () => ShowCategory(1);
        tabRow.AddChild(_planetTab);

        _terrainTab = MakeTabBtn("🌍 地形与水量", tabGroup);
        _terrainTab.Pressed += () => ShowCategory(2);
        tabRow.AddChild(_terrainTab);

        // ── 滚动选项区：固定宽度 960 + 水平居中，固定高度 + 内部滚动 ──
        // （不 fill 全宽：否则 GridContainer 被拉成巨列、参数堆左上角不对称）
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(960, 240),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        root.AddChild(scroll);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(content);

        // 分类 1：生成参数（两列，列宽自动均分 480+480）
        _generateGrid = new GridContainer { Columns = 2 };
        _generateGrid.AddThemeConstantOverride("h_separation", 16);
        _generateGrid.AddThemeConstantOverride("v_separation", 12);
        content.AddChild(_generateGrid);

        _generateGrid.AddChild(MakeRow("种子（Seed）", _seedBox = MakeSpin(0, 999999, 42, 1)));
        _generateGrid.AddChild(MakeSliderRow("网格分辨率 n", 4f, 128f, 1f, 8f, 32f, out _gridNSpin));
        _generateGrid.AddChild(MakeRow("初始板块数", _platesBox = MakeSpin(2, 32, 8, 1)));
        _generateGrid.AddChild(MakeSliderRow("模拟时长(My)", 100f, 2000f, 1f, 50f, 600f, out _mySpin));

        // 分类 2：星球物理（两列）
        _planetGrid = new GridContainer { Columns = 2, Visible = false };
        _planetGrid.AddThemeConstantOverride("h_separation", 16);
        _planetGrid.AddThemeConstantOverride("v_separation", 12);
        content.AddChild(_planetGrid);

        _planetGrid.AddChild(MakeRow("自转方向", _rotBox = MakeRotationOption()));
        _planetGrid.AddChild(MakeSliderRow("轴向倾角(°)", 0f, 90f, 0.1f, 5f, 23.4f, out _tiltSpin));
        _planetGrid.AddChild(MakeSliderRow("距太阳距离(AU)", 0.7f, 1.5f, 0.01f, 0.05f, 1.0f, out _distSpin));
        _planetGrid.AddChild(MakeSliderRow("自转速度(×)", 0.2f, 5f, 0.01f, 0.5f, 1.0f, out _speedSpin));

        // 分类 3：地形与水量（两列）
        _terrainGrid = new GridContainer { Columns = 2, Visible = false };
        _terrainGrid.AddThemeConstantOverride("h_separation", 16);
        _terrainGrid.AddThemeConstantOverride("v_separation", 12);
        content.AddChild(_terrainGrid);

        _terrainGrid.AddChild(MakeSliderRow("海洋水量(×)", 0.5f, 1.5f, 0.01f, 0.1f, 1.0f, out _oceanSpin));
        _terrainGrid.AddChild(MakeSliderRow("大陆周期(My)", 60f, 400f, 1f, 25f, 150f, out _scCycleSpin));
        _terrainGrid.AddChild(MakeSliderRow("侵蚀强度(×)", 0.5f, 2f, 0.01f, 0.25f, 1.0f, out _erosionSpin));

        // ── 按钮区（固定底部，水平居中，不被选项挤走）──
        var btnRow = new HBoxContainer { CustomMinimumSize = new Vector2(480, 0), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        btnRow.AddThemeConstantOverride("separation", 16);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        root.AddChild(btnRow);

        _backBtn = MakeBtn("← 返回", 24);
        _backBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MainMenu.tscn");
        btnRow.AddChild(_backBtn);

        _startBtn = MakeBtn("开始生成", 24);
        _startBtn.Pressed += StartGenerate;
        btnRow.AddChild(_startBtn);

        // ── 进度区（生成时显示，水平居中）──
        _bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(960, 30),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            Visible = false,
        };
        root.AddChild(_bar);

        _status = new Label
        {
            Text = "选择参数后点击「开始生成」。生成需数分钟，可在后台进行。",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(960, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _status.AddThemeFontSizeOverride("font_size", 18);
        _status.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        root.AddChild(_status);

        // 默认显示第一个分类
        _genTab.ButtonPressed = true;
        ShowCategory(0);
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
            CustomMinimumSize = new Vector2(160, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.AddThemeFontSizeOverride("font_size", 20);
        row.AddChild(lbl);
        field.CustomMinimumSize = new Vector2(240, 40);
        field.SizeFlagsHorizontal = SizeFlags.ExpandFill;   // 拉伸到行尾，与滑动行右侧对齐
        row.AddChild(field);
        return row;
    }

    /// <summary>
    /// 滑动条 + 手动输入联动行：label + HSlider + SpinBox。
    /// step = 值对齐精度（Range 会把 set_value 对齐到 step 倍数，故要精确输入必须设小）；
    /// arrowStep = 箭头按钮增量（CustomArrowStep，与对齐精度分离）。
    /// 拖动滑块 ↔ 输入数值双向同步（设相同值不触发事件，无死循环）。
    /// </summary>
    private HBoxContainer MakeSliderRow(string label, float min, float max, float step, float arrowStep, float val, out SpinBox spin)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(480, 44) };
        row.AddThemeConstantOverride("separation", 12);
        var lbl = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(160, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.AddThemeFontSizeOverride("font_size", 20);
        row.AddChild(lbl);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = val,
            CustomMinimumSize = new Vector2(100, 40),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,   // 吃掉中间空间
        };
        row.AddChild(slider);

        spin = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = step,               // 对齐精度（决定手动输入能精确到什么值）
            Value = val,
            AllowGreater = true,       // 手动输入可超过滑块上限（用户自由；极端值只是慢/结果极端，不崩）
            AllowLesser = false,       // 下限保守：负数无物理意义
            CustomMinimumSize = new Vector2(100, 40),
        };
        spin.CustomArrowStep = arrowStep;   // 箭头增量（可大于对齐精度，快跳）
        spin.AddThemeFontSizeOverride("font_size", 16);
        row.AddChild(spin);

        // ⚠️ out 参数不能进 lambda：先存局部变量再捕获
        // ⚠️ 守卫 syncing：输入超上限值 → slider 被 Range clamp 到 max → 触发 slider.ValueChanged
        //   → 若不拦截会把 spin 拉回上限。守卫让回写方向不被级联触发。
        var spinRef = spin;
        var syncing = false;
        slider.ValueChanged += v =>
        {
            if (syncing) return;
            syncing = true;
            spinRef.Value = v;      // 滑块 → 输入框
            syncing = false;
        };
        spinRef.ValueChanged += v =>
        {
            if (syncing) return;
            syncing = true;
            slider.Value = v;       // 输入框 → 滑块（超上限被 clamp，回写由守卫忽略）
            syncing = false;
        };
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
            AllowGreater = true,   // 上限放开（板块数/种子），下限保守
            AllowLesser = false,
        };
        return sb;
    }

    private OptionButton MakeRotationOption()
    {
        var ob = new OptionButton();
        ob.AddItem("顺转（地球式，自西向东）", 1);
        ob.AddItem("逆转（金星式，自东向西）", 0);
        ob.Selected = 0;   // 顺转默认
        return ob;
    }

    private Button MakeBtn(string text, int size)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(180, 52) };
        b.AddThemeFontSizeOverride("font_size", size);
        return b;
    }

    /// <summary>分类页签按钮（toggle 互斥组，选中态高亮）。</summary>
    private Button MakeTabBtn(string text, ButtonGroup group)
    {
        var b = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonGroup = group,
            CustomMinimumSize = new Vector2(180, 46),
        };
        b.AddThemeFontSizeOverride("font_size", 20);
        return b;
    }

    /// <summary>切换选项分类：只显示选中分类的网格，页签高亮跟随。</summary>
    private void ShowCategory(int idx)
    {
        _generateGrid.Visible = idx == 0;
        _planetGrid.Visible = idx == 1;
        _terrainGrid.Visible = idx == 2;
        void SetTab(Button tab, bool active) =>
            tab.AddThemeColorOverride("font_color", active
                ? new Color(1f, 0.85f, 0.5f)      // 选中：亮金
                : new Color(0.72f, 0.78f, 0.9f)); // 未选中：灰蓝
        SetTab(_genTab, idx == 0);
        SetTab(_planetTab, idx == 1);
        SetTab(_terrainTab, idx == 2);
    }

    // ── 生成逻辑 ──

    private void StartGenerate()
    {
        if (_generating) return;

        // 防御校验：只拦会引发 bug 的极端值（内存/算法可行性），其余随意
        int n = (int)_gridNSpin.Value;
        int plates = (int)_platesBox.Value;
        if (n > 256)
        {
            _status.Text = $"❌ 网格分辨率 n={n} 过大（顶点 {10L * n * n + 2:N0}）——内存/时间不可行，请 ≤ 256";
            return;
        }
        if (n < 4)
        {
            _status.Text = $"❌ 网格分辨率 n={n} 过小（Icosahedron 细分需 n≥4），请 ≥ 4";
            return;
        }
        if (plates > 64)
        {
            _status.Text = $"❌ 初始板块数 {plates} 过多——每板顶点过少无法模拟，请 ≤ 64";
            return;
        }
        if (plates < 2)
        {
            _status.Text = $"❌ 初始板块数 {plates} 过少——至少 2 块板才有边界，请 ≥ 2";
            return;
        }

        _generating = true;
        _startBtn.Disabled = true;
        _backBtn.Disabled = true;

        _progress = 0f;
        _bar.Visible = true;
        _status.Text = "生成中…（板块模拟阶段）";

        int seed = (int)_seedBox.Value;
        float my = (float)_mySpin.Value;            // 模拟时长 My（滑块/输入）
        bool prograde = _rotBox.GetSelectedId() == 1;
        float tilt = (float)_tiltSpin.Value;        // 轴向倾角 °
        float distAu = (float)_distSpin.Value;      // 距日 AU
        float insolation = 1f / (distAu * distAu);  // 能量 ∝ 1/d²
        float speed = (float)_speedSpin.Value;      // 自转速度 ×
        float oceanScale = (float)_oceanSpin.Value; // 海洋水量 ×
        int scCycle = (int)_scCycleSpin.Value;      // 超级大陆周期 My
        float erosionScale = (float)_erosionSpin.Value; // 侵蚀强度 ×
        _lastOutPath = $"user://maps/map_seed{seed}_n{n}.mpa";

        _gen = new MapGenerator
        {
            Seed = seed,
            TectonicsGridN = n,
            NumPlates = plates,
            SimMegayears = my,
            SimStepMy = 2f,
            ProgradeRotation = prograde,   // 自转方向 → 盛行风
            AxialTilt = tilt,              // 轴向倾角 → 季节/温度带
            Insolation = insolation,       // 距太阳距离 → 全球温度
            RotationSpeed = speed,         // 自转速度 → 科里奥利强度
            OceanScale = oceanScale,       // 海洋水量 → 海陆比
            SupercontinentCycleMy = scCycle, // 超级大陆周期
            ErosionScale = erosionScale,   // 侵蚀强度 → 地貌
            OutputPath = _lastOutPath,
            ExportPreview = false,   // UI 模式不导出 PNG，省时间
            AutoQuit = false,
        };
        _gen.SetAsyncDoneCallback(OnGenerateDone);
        _gen.GenerateAsync(
            p => _progress = p,     // 后台线程写 volatile（线程安全）
            (ok, path) => { });     // 实际完成回调走 SetAsyncDoneCallback（主线程）
        GD.Print($"[MapGenMenu] 开始生成 seed={seed} n={n} plates={plates} {my}My 自转={(prograde ? "顺转" : "逆转")} 倾角={tilt}° 距离={distAu}AU 速度={speed}× 水量={oceanScale}× 周期={scCycle}My 侵蚀={erosionScale}× → {_lastOutPath}");
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
            // 生成完可直接查看（水平居中，与整体列对齐）
            var viewBtn = MakeBtn("▶ 查看地图", 22);
            viewBtn.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
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
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }
}

/// <summary>场景间传递"要查看的存档路径"（MapViewer._Ready 读取后清空）。</summary>
public static class ViewerLauncher
{
    public static string PendingPath;
}
