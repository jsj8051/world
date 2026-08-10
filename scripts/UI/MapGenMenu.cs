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
    private SpinBox _platesBox;
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
    private SpinBox _radiusSpin;    // 星球半径 km（主输入；n 派生，2026-08-10 口径：每格 5 km²）
    private Label _derivedLabel;    // 半径 → n/顶点数/实际半径/格面积/耗时 派生显示
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
        // 星球大小主输入（2026-08-10 口径定案：每格固定 5 km²，n 由半径派生）
        _generateGrid.AddChild(MakeSliderRow("星球半径(km)", 8f, 511f, 1f, 16f, 128f, out _radiusSpin));
        _generateGrid.AddChild(MakeRow("初始板块数", _platesBox = MakeSpin(2, 32, 8, 1)));
        _generateGrid.AddChild(MakeSliderRow("模拟时长(My)", 100f, 2000f, 1f, 50f, 600f, out _mySpin));

        // 半径 → 网格派生显示（跟随输入实时刷新；与生成参数页同显同隐）
        _derivedLabel = new Label
        {
            CustomMinimumSize = new Vector2(960, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _derivedLabel.AddThemeFontSizeOverride("font_size", 17);
        _derivedLabel.AddThemeColorOverride("font_color", new Color(0.72f, 0.85f, 0.72f));
        content.AddChild(_derivedLabel);
        var radiusRef = _radiusSpin;
        radiusRef.ValueChanged += _ => UpdateDerived();
        UpdateDerived();

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
        _derivedLabel.Visible = idx == 0;   // 半径派生信息跟生成参数页
        void SetTab(Button tab, bool active) =>
            tab.AddThemeColorOverride("font_color", active
                ? new Color(1f, 0.85f, 0.5f)      // 选中：亮金
                : new Color(0.72f, 0.78f, 0.9f)); // 未选中：灰蓝
        SetTab(_genTab, idx == 0);
        SetTab(_planetTab, idx == 1);
        SetTab(_terrainTab, idx == 2);
    }

    // ── 星球大小 ↔ 网格分辨率（2026-08-10 口径定案：每格固定 5 km²，用户选半径，n 派生）──

    /// <summary>半径(km) → 网格 n（四舍五入到最近细分档；顶点 = 10n²+2）。
    /// 推导：4πR² = (10n²)·5 → n = √(4πR²/50)。</summary>
    private static int RadiusToGridN(float radiusKm)
    {
        double areaKm2 = 4.0 * Math.PI * radiusKm * radiusKm;
        return (int)Math.Round(Math.Sqrt(areaKm2 / 50.0), MidpointRounding.AwayFromZero);
    }

    /// <summary>网格 n → 实际半径(km)（反算；保证 4πR²/(10n²) ≈ 5 km²/格，口径自洽）。</summary>
    private static float GridNToRadius(int n) => (float)Math.Sqrt(50.0 * n * n / (4.0 * Math.PI));

    /// <summary>耗时预估（板块模拟实测基线：n=16 秒级 / 32≈30s / 64≈3min / 128≈12min，×4 关系）。</summary>
    private static string EstimateTime(int n)
    {
        if (n <= 16) return "预计约 30 秒";
        if (n <= 32) return "预计约 1 分钟";
        if (n <= 64) return "预计约 3 分钟";
        if (n <= 128) return "预计约 12 分钟";
        return "预计 40 分钟以上（红线档，慎用）";
    }

    /// <summary>半径输入 → 派生信息实时刷新（n / 顶点数 / 实际半径 / 每格面积 / 耗时）。</summary>
    private void UpdateDerived()
    {
        int n = RadiusToGridN((float)_radiusSpin.Value);
        if (n < 4 || n > 256)
        {
            _derivedLabel.Text = n < 4
                ? "⚠️ 半径过小：细分下限 n≥4（≈8 km），请加大半径"
                : $"⚠️ 半径过大：n={n}（顶点 {10L * n * n + 2:N0}）超性能红线，请 ≤ 511 km（n=256）";
            _derivedLabel.AddThemeColorOverride("font_color", new Color(1f, 0.6f, 0.5f));
            return;
        }
        float r = GridNToRadius(n);
        long verts = 10L * n * n + 2;
        float cellArea = 4f * Mathf.Pi * r * r / verts;
        _derivedLabel.AddThemeColorOverride("font_color", new Color(0.72f, 0.85f, 0.72f));
        _derivedLabel.Text = $"→ 网格 n={n}（{verts:N0} 格）｜实际半径 {r:F1} km｜每格 ≈{cellArea:F2} km²｜{EstimateTime(n)}";
    }

    // ── 生成逻辑 ──

    private void StartGenerate()
    {
        if (_generating) return;

        // 防御校验：只拦会引发 bug 的极端值（内存/算法可行性），其余随意
        int n = RadiusToGridN((float)_radiusSpin.Value);
        float radiusKm = GridNToRadius(n);   // 实际半径（口径自洽：4πR'²/N ≈ 5 km²/格）
        int plates = (int)_platesBox.Value;
        if (n > 256)
        {
            _status.Text = $"❌ 半径 {_radiusSpin.Value:F0} km 过大 → 网格 n={n}（顶点 {10L * n * n + 2:N0}）——内存/时间不可行，请 ≤ 511 km";
            return;
        }
        if (n < 4)
        {
            _status.Text = $"❌ 半径 {_radiusSpin.Value:F0} km 过小 → 网格 n={n}（Icosahedron 细分需 n≥4），请 ≥ 8 km";
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
        _lastOutPath = $"user://maps/map_seed{seed}_n{n}_r{radiusKm:F0}.mpa";

        _gen = new MapGenerator
        {
            Seed = seed,
            TectonicsGridN = n,
            RadiusKm = radiusKm,           // 星球半径（口径：每格 5 km²；n 由半径派生）
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
        GD.Print($"[MapGenMenu] 开始生成 seed={seed} R={radiusKm:F0}km n={n} plates={plates} {my}My 自转={(prograde ? "顺转" : "逆转")} 倾角={tilt}° 距离={distAu}AU 速度={speed}× 水量={oceanScale}× 周期={scCycle}My 侵蚀={erosionScale}× → {_lastOutPath}");
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
