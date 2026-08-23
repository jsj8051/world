using Godot;
using System;
using World.CivSim;
using World.HexPlanet;
using World.MapGen;
using World.Services;

namespace World.UI;

/// <summary>
/// 生成地图界面：参数选择（种子/网格分辨率/板块数/模拟时长）→ 后台生成（进度条）→ 完成。
/// 静态骨架（背景/窗口框/页签/状态栏/滚动区/按钮/进度条）在 MapGenMenu.tscn 场景中定义
/// （编辑器/MCP 可见节点树）；脚本只做动态部分：参数表单行注入、页签切换、生成逻辑。
/// 窗口框动态分辨率：锚点 18%/13%~82%/87%（随窗口缩放），min 680×480 兜底。
/// 生成在后台线程跑（MapGenerator.GenerateAsync），UI 不卡。
/// </summary>
public partial class MapGenMenu : Control
{
    private SpinBox _seedBox;
    private SpinBox _platesBox;
    private Button _startBtn;
    private Button _backBtn;
    private Label _status;
    private Label _derivedLabel;    // 半径 → n/顶点数/实际半径/格面积/耗时 派生显示
    private GridContainer _generateGrid;   // 分类：生成参数（两列）
    private GridContainer _planetGrid;     // 分类：星球物理（两列）
    private GridContainer _terrainGrid;    // 分类：地形与水量（两列）
    private Button _genTab;                // 分类页签
    private Button _planetTab;
    private Button _terrainTab;
    private bool _generating;
    private OptionButton _rotBox;   // 自转方向（枚举）
    private SpinBox _radiusSpin;    // 星球半径 km（主输入；n 派生，2026-08-10 口径：每格 5 km²）
    private SpinBox _continentsSpin; // 大陆块数（构造格局：2=超大陆/20=碎陆）
    private SpinBox _tiltSpin;      // 轴向倾角（滑动+输入）
    private SpinBox _distSpin;      // 距太阳距离
    private SpinBox _speedSpin;     // 自转速度
    private SpinBox _oceanSpin;     // 海洋水量
    private SpinBox _scCycleSpin;   // 超级大陆周期
    private SpinBox _erosionSpin;   // 侵蚀强度
    private SpinBox _mySpin;        // 模拟时长（滑动+输入）

    // v7 单存档化：生成后自动文明演化（一条龙）
    private CheckBox _evolveCheck;      // 是否生成后自动演化文明
    private SpinBox _evolveSeedSpin;    // 演化种子
    private SpinBox _evolveOriginsSpin; // 起源部落数
    private volatile bool _evolving;    // 演化阶段进行中（后台线程写进度）

    private ProgressTextBar _bar;       // 进度条（自绘文本：阶段+百分比一体）

    private MapGenerator _gen;
    private volatile float _progress;   // 后台线程写、主线程 _Process 读
    private string _lastOutPath;
    // v7 单存档化：演化回写暂存（CallDeferred 只能传 Variant，用字段桥接）
    private string _civOutPath;
    private MapData _civMap;
    private CivSimResult _civResult;

    public override void _Ready()
    {
        // 根 Control 强制全屏（防场景根未自动拉伸 → 锚点归零、框落左上角）
        SetAnchorsPreset(LayoutPreset.FullRect);

        // 取场景节点（unique_name_in_owner 标记 %名）
        _startBtn = GetNode<Button>("%StartBtn");
        _backBtn = GetNode<Button>("%BackBtn");
        _bar = GetNode<ProgressTextBar>("%Bar");
        _status = GetNode<Label>("%Status");
        _derivedLabel = GetNode<Label>("%DerivedLabel");
        _generateGrid = GetNode<GridContainer>("%GenerateGrid");
        _planetGrid = GetNode<GridContainer>("%PlanetGrid");
        _terrainGrid = GetNode<GridContainer>("%TerrainGrid");
        _genTab = GetNode<Button>("%GenTab");
        _planetTab = GetNode<Button>("%PlanetTab");
        _terrainTab = GetNode<Button>("%TerrainTab");
        var progressWrap = GetNode<Control>("%ProgressWrap");

        // 绑定事件
        _genTab.Pressed += () => ShowCategory(0);
        _planetTab.Pressed += () => ShowCategory(1);
        _terrainTab.Pressed += () => ShowCategory(2);
        _backBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MainMenu.tscn");
        _startBtn.Pressed += StartGenerate;
        progressWrap.Visible = _bar.Visible = false;

        // 状态点呼吸动画
        var dot = GetNode<Label>("%StatusDot");
        var tween = CreateTween().SetLoops();
        tween.TweenProperty(dot, "modulate:a", 0.35f, 1.2f);
        tween.TweenProperty(dot, "modulate:a", 1f, 1.2f);

        // ── 注入参数表单（场景 grid 是空容器，行动态生成）──
        _generateGrid.AddChild(MakeRow("种子（Seed）", _seedBox = MakeSpin(0, 999999, 42, 1)));
        // 星球大小主输入（2026-08-10 口径定案：每格固定 5 km²，n 由半径派生）
        _generateGrid.AddChild(MakeSliderRow("星球半径(km)", 8f, 511f, 1f, 16f, 128f, out _radiusSpin));
        // 大陆块数（构造格局；校验 N≤n/2 防碎渣，见 StartGenerate）
        _generateGrid.AddChild(MakeSliderRow("大陆块数", 2f, 20f, 1f, 2f, 6f, out _continentsSpin));
        _generateGrid.AddChild(MakeRow("初始板块数", _platesBox = MakeSpin(2, 32, 8, 1)));
        _generateGrid.AddChild(MakeSliderRow("模拟时长(My)", 100f, 2000f, 1f, 50f, 600f, out _mySpin));
        _derivedLabel.AddThemeFontOverride("font", SaveRowStyle.MonoFont());
        var radiusRef = _radiusSpin;
        radiusRef.ValueChanged += _ => UpdateDerived();
        UpdateDerived();

        // v7 单存档化：文明演化开关 + 参数（生成完成自动衔接，写入 CIVI 段）
        _evolveCheck = new CheckBox
        {
            Text = "生成后自动演化文明（一条龙，产出含文明的 .mpa）",
            ButtonPressed = true,
        };
        _evolveCheck.AddThemeFontSizeOverride("font_size", 15);
        _generateGrid.AddChild(MakeRow("文明演化", _evolveCheck));
        _generateGrid.AddChild(MakeSliderRow("演化种子", 0f, 999999f, 1f, 10f, 42f, out _evolveSeedSpin));
        _generateGrid.AddChild(MakeSliderRow("起源部落数", 1f, 16f, 1f, 1f, 3f, out _evolveOriginsSpin));

        // 分类 2：星球物理（两列）
        _planetGrid.AddChild(MakeRow("自转方向", _rotBox = MakeRotationOption()));
        _planetGrid.AddChild(MakeSliderRow("轴向倾角(°)", 0f, 90f, 0.1f, 5f, 23.4f, out _tiltSpin));
        _planetGrid.AddChild(MakeSliderRow("距太阳距离(AU)", 0.7f, 1.5f, 0.01f, 0.05f, 1.0f, out _distSpin));
        _planetGrid.AddChild(MakeSliderRow("自转速度(×)", 0.2f, 5f, 0.01f, 0.5f, 1.0f, out _speedSpin));

        // 分类 3：地形与水量（两列）
        _terrainGrid.AddChild(MakeSliderRow("海洋水量(×)", 0.5f, 1.5f, 0.01f, 0.1f, 1.0f, out _oceanSpin));
        _terrainGrid.AddChild(MakeSliderRow("大陆周期(My)", 60f, 400f, 1f, 25f, 150f, out _scCycleSpin));
        _terrainGrid.AddChild(MakeSliderRow("侵蚀强度(×)", 0.5f, 2f, 0.01f, 0.25f, 1.0f, out _erosionSpin));

        // 默认显示第一个分类
        ShowCategory(0);
    }

    public override void _Process(double delta)
    {
        // 后台线程写 volatile 进度 → 主线程更新进度条（Godot Control 属性非线程安全）；百分比由进度条自带
        if (_bar.Visible)
        {
            _bar.Value = _progress * 100f;
            // v7 单存档化：演化阶段进度单独驱动（生成进度已 100 后复用同一条）
            if (_evolving)
                _bar.Prefix = "（演化中…文明模拟阶段）";
        }
    }

    // ── UI 构建辅助 ──

    private HBoxContainer MakeRow(string label, Control field)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 44) };
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddThemeConstantOverride("separation", 12);
        var lbl = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(150, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.AddThemeFontSizeOverride("font_size", 16);
        row.AddChild(lbl);
        field.CustomMinimumSize = new Vector2(0, 40);
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
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 44) };
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddThemeConstantOverride("separation", 12);
        var lbl = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(150, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        lbl.AddThemeFontSizeOverride("font_size", 16);
        row.AddChild(lbl);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = val,
            CustomMinimumSize = new Vector2(80, 40),
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
        spin.AddThemeFontSizeOverride("font_size", 15);
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

    /// <summary>切换选项分类：只显示选中分类的网格，页签高亮跟随。</summary>
    private void ShowCategory(int idx)
    {
        _generateGrid.Visible = idx == 0;
        _planetGrid.Visible = idx == 1;
        _terrainGrid.Visible = idx == 2;
        _derivedLabel.Visible = idx == 0;   // 半径派生信息跟生成参数页
        StyleTab(_genTab, idx == 0);
        StyleTab(_planetTab, idx == 1);
        StyleTab(_terrainTab, idx == 2);
    }

    private static void StyleTab(Button b, bool active)
    {
        if (active)
        {
            b.AddThemeStyleboxOverride("normal", SaveRowStyle.TabActive());
            b.AddThemeStyleboxOverride("hover", SaveRowStyle.TabActive());
            b.AddThemeStyleboxOverride("pressed", SaveRowStyle.TabActive());
            b.AddThemeStyleboxOverride("focus", SaveRowStyle.TabActive());
            b.AddThemeColorOverride("font_color", SaveRowStyle.Fg);
            b.AddThemeColorOverride("font_hover_color", SaveRowStyle.Fg);
        }
        else
        {
            b.AddThemeStyleboxOverride("normal", SaveRowStyle.TabInactive());
            b.AddThemeStyleboxOverride("hover", SaveRowStyle.TabInactiveHover());
            b.AddThemeStyleboxOverride("pressed", SaveRowStyle.TabInactive());
            b.AddThemeStyleboxOverride("focus", SaveRowStyle.TabInactive());
            b.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
            b.AddThemeColorOverride("font_hover_color", SaveRowStyle.Muted);
        }
    }

    // ── 星球大小 ↔ 网格分辨率（2026-08-10 口径定案：每格固定 5 km²，用户选半径，n 派生）──

    /// <summary>口径：每格面积（km²，2026-08-10 用户拍板 5km²/格；Goldberg 格数 ≈ 10n²）。</summary>
    private const double CellAreaKm2 = 5.0;

    /// <summary>半径(km) → 网格 n（四舍五入到最近细分档；顶点 = 10n²+2）。
    /// 推导：4πR² = (10n²)·5 → n = √(4πR²/50)。</summary>
    private static int RadiusToGridN(float radiusKm)
    {
        double areaKm2 = 4.0 * Math.PI * radiusKm * radiusKm;
        return (int)Math.Round(Math.Sqrt(areaKm2 / (10.0 * CellAreaKm2)), MidpointRounding.AwayFromZero);
    }

    /// <summary>网格 n → 实际半径(km)（反算；保证 4πR²/(10n²) ≈ 5 km²/格，口径自洽）。</summary>
    private static float GridNToRadius(int n) => (float)Math.Sqrt(10.0 * CellAreaKm2 * n * n / (4.0 * Math.PI));

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
                : $"⚠️ 半径过大：n={n}（顶点 {Icosahedron.VertexCountFor(n):N0}）超性能红线，请 ≤ 511 km（n=256）";
            _derivedLabel.AddThemeColorOverride("font_color", new Color(1f, 0.6f, 0.5f));
            return;
        }
        float r = GridNToRadius(n);
        long verts = Icosahedron.VertexCountFor(n);
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
            _status.Text = $"❌ 半径 {_radiusSpin.Value:F0} km 过大 → 网格 n={n}（顶点 {Icosahedron.VertexCountFor(n):N0}）——内存/时间不可行，请 ≤ 511 km";
            return;
        }
        if (n < 4)
        {
            _status.Text = $"❌ 半径 {_radiusSpin.Value:F0} km 过小 → 网格 n={n}（Icosahedron 细分需 n≥4），请 ≥ 8 km";
            return;
        }
        int continents = (int)_continentsSpin.Value;
        if (continents > n / 2)
        {
            _status.Text = $"❌ 大陆块数 {continents} 过多：n={n} 网格每块仅 ~{4 * n / continents} 格（<8 会碎成渣），" +
                           $"请 ≤ {n / 2} 或增大星球半径";
            return;
        }
        if (continents < 2)
        {
            _status.Text = "❌ 大陆块数至少 2（超大陆），请 ≥ 2";
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
        _bar.Prefix = "（生成中…板块模拟阶段）";   // 阶段文字 + 自绘百分比一体显示
        _bar.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        GetNode<Control>("%ProgressWrap").Visible = true;
        _bar.Visible = true;
        _bar.Value = 0;

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
        _lastOutPath = ArchiveService.MapPath(seed, n, radiusKm);

        _gen = new MapGenerator
        {
            Seed = seed,
            TectonicsGridN = n,
            RadiusKm = radiusKm,           // 星球半径（口径：每格 5 km²；n 由半径派生）
            NumContinents = continents,    // 大陆块数（构造格局）
            NumPlates = plates,
            SimMegayears = my,
            // SimStepMy 不显式传：跟随 MapGenerator 默认 4f（2026-08-03 已验证 2→4 质量一致；UI 曾硬编码 2f 导致与 headless 默认不一致）
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
        LogService.Log("MapGenMenu", $"开始生成 seed={seed} R={radiusKm:F0}km n={n} 大陆={continents}块 plates={plates} {my}My 自转={(prograde ? "顺转" : "逆转")} 倾角={tilt}° 距离={distAu}AU 速度={speed}× 水量={oceanScale}× 周期={scCycle}My 侵蚀={erosionScale}× → {_lastOutPath}");
    }

    private void OnGenerateDone(bool ok, string path)
    {
        _generating = false;
        _startBtn.Disabled = false;
        _backBtn.Disabled = false;

        if (ok)
        {
            // v7 单存档化：勾选了「自动演化文明」→ 无缝接入演化阶段（同一条进度条）
            if (_evolveCheck != null && _evolveCheck.ButtonPressed)
            {
                StartCivEvolution(path);
                return;   // 演化完成后 OnCivEvolutionDone 收尾
            }
            // 纯自然地图（未勾选演化）→ 直接完成态
            FinishGenerateUI(path);
        }
        else
        {
            _bar.Prefix = "（生成失败）";
            _bar.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
            _status.Text = "❌ 生成失败，请查看控制台日志。";
            _status.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
            _evolving = false;
        }
    }

    /// <summary>v7 单存档化：生成完成 → 自动读档演化文明 → 重写 .mpa（附 CIVI 段）。
    /// 读档在主线程（FileAccess/地图数据非线程安全），演化在后台线程（CivEngine 纯 C#）。</summary>
    private void StartCivEvolution(string path)
    {
        _evolving = true;
        _progress = 0f;
        // 阶段文字由 _Process 统一设（演化阶段）
        // 主线程读 .mpa（自然层全量）
        TechTable.Load();
        if (!MapArchive.Read(path, out var map))
        {
            _evolving = false;
            _status.Text = "❌ 演化失败：无法读回刚生成的 .mpa";
            _status.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
            return;
        }
        var grid = World.LogicGrid.GameGrid.FromMapData(map);
        int seed = (int)_evolveSeedSpin.Value;
        int origins = (int)_evolveOriginsSpin.Value;
        string outPath = path;
        // 后台：演化（纯 C#，无 Godot 调用）
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var result = World.CivSim.CivEngine.Run(grid, seed, origins, p => _progress = p);
                // 跑完回主线程：CIVI 重写（字段桥接——CallDeferred 只能传 Variant）
                _civOutPath = outPath;
                _civMap = map;
                _civResult = result;
                CallDeferred(nameof(OnCivEvolutionDone));
            }
            catch (Exception ex)
            {
                LogService.LogErr("MapGenMenu", $"文明演化失败: {ex}");
                _civOutPath = outPath;
                CallDeferred(nameof(OnCivEvolutionError));
            }
        });
    }

    private void OnCivEvolutionDone()
    {
        _evolving = false;
        bool ok = MapArchive.WriteSpherical(_civOutPath, _civMap, _civResult);
        if (!ok)
        {
            _status.Text = "❌ 演化完成但写档失败，请查看日志。";
            _status.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
            return;
        }
        LogService.Log("MapGenMenu", $"生成+演化完成（含文明）: {_civOutPath} (tribes={_civResult.Context.Tribes.Count} tick={_civResult.FinalTick})");
        _status.Text = $"✅ 生成+演化完成！存档：{_civOutPath.GetFile()}（含文明，可返回主菜单进入世界）";
        _status.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.6f));
        _bar.Prefix = "（生成+演化完成）";
        _bar.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.6f));
        _bar.Value = 100;
        // 可直接查看（含文明图层）
        var viewBtn = new Button { Text = "▶ 进入世界" };
        viewBtn.CustomMinimumSize = new Vector2(160, 44);
        viewBtn.AddThemeFontSizeOverride("font_size", 16);
        StylePrimary(viewBtn);
        string capturedPath = _civOutPath;   // 闭包捕获
        viewBtn.Pressed += () => EnterViewer(capturedPath);
        GetNode<HBoxContainer>("%FootRow").AddChild(viewBtn);
    }

    private void OnCivEvolutionError()
    {
        _evolving = false;
        _bar.Prefix = "（演化失败）";
        _bar.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
        _status.Text = $"❌ 文明演化失败（{_civOutPath.GetFile()} 保持纯自然地图）。";
        _status.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.5f));
        LogService.LogErr("MapGenMenu", $"文明演化失败: {_civOutPath}");
    }

    /// <summary>纯自然地图（未勾选演化）的完成态收尾。</summary>
    private void FinishGenerateUI(string path)
    {
        // 进度条 100%（自绘文本：阶段+百分比一体）；完成信息显示在顶部状态栏（进度条底下不再显示）
        _bar.Value = 100;
        _bar.Prefix = "（生成完成）";
        _bar.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.6f));
        _status.Text = $"✅ 生成完成！存档：{path.GetFile()}（已保存，可返回主菜单进入世界）";
        _status.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.6f));
        // 生成完可直接查看（水平居中，与整体列对齐）
        var viewBtn = new Button { Text = "▶ 查看地图" };
        viewBtn.CustomMinimumSize = new Vector2(160, 44);
        viewBtn.AddThemeFontSizeOverride("font_size", 16);
        StylePrimary(viewBtn);
        viewBtn.Pressed += () => EnterViewer(path);
        GetNode<HBoxContainer>("%FootRow").AddChild(viewBtn);
        LogService.Log("MapGenMenu", $"生成完成: {path}");
    }

    private static void StylePrimary(Button b)
    {
        b.AddThemeStyleboxOverride("normal", SaveRowStyle.PrimaryNormal());
        b.AddThemeStyleboxOverride("hover", SaveRowStyle.PrimaryHover());
        b.AddThemeStyleboxOverride("pressed", SaveRowStyle.PrimaryHover());
        b.AddThemeStyleboxOverride("focus", SaveRowStyle.PrimaryNormal());
        b.AddThemeColorOverride("font_color", SaveRowStyle.Accent);
        b.AddThemeColorOverride("font_hover_color", SaveRowStyle.Bg2);
        b.AddThemeColorOverride("font_pressed_color", SaveRowStyle.Bg2);
    }

    private void EnterViewer(string path)
    {
        // MapViewer 场景加载后自动读默认 map1.mpa；这里通过全局单例传路径。
        // 简单方案：MapViewer 支持命令行/user args 不可行（运行时），
        // 用静态字段传递：MapViewer 的 MapPath 属性在 _Ready 前可被设置。
        EventBus.RequestMapView(path);
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }
}