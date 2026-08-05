using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Biome;
using World.MapGen;
using World.HexPlanet;
using World.PlanetLOD;
using World.Surface;
using World.UI;

namespace World.MapView;

/// <summary>
/// 游玩阶段（hex 格子星球）：加载地图存档（海拔场）→ Goldberg hex 网格 →
/// 每格从场采样海拔 → 海拔色 + A 通道描边 → 静态单网格球体。
///
/// 策略视角：格子不平移（保持球面平面感，格子边界清晰），海拔只影响颜色。
/// 场 = 无损中间表示；格子 = 视图（n 可配置，重新导出即可）。
///
/// 生成异步化：纯数据部分（细分/Goldberg/采样/顶点构建）跑后台线程，
/// 进度通过 volatile 字段汇报给主线程 _Process 驱动进度条 UI；
/// 只有 ArrayMesh 创建和节点挂载在主线程。改 GridN 时旧任务由版本号丢弃。
/// </summary>
public partial class MapViewer : Node3D
{
    [Export]
    public string MapPath
    {
        get => _mapPath;
        set
        {
            if (_mapPath == value) return;
            _mapPath = value;
            _mapLoaded = false; // 路径变了 → 下次重建时重新读
            if (IsInsideTree()) Generate();
        }
    }
    private string _mapPath = "user://maps/map1.mpa";

    // 地图存档缓存：运行期间不变，只读一次；重建（切图层/改 GridN）复用
    private MapData _map;
    private bool _mapLoaded;

    [Export] public float RadiusKm = 6330f;

    // 改 GridN → 自动重建星球。IsInsideTree() 保证编辑器和运行时都实时响应
    // （编辑器视口不跑 _Ready，但改属性时节点已在树内，照样触发重建）。
    [Export]
    public int GridN
    {
        get => _gridN;
        set
        {
            if (_gridN == value) return;
            _gridN = value;
            _geometryReady = false; // 网格变了 → 缓存几何失效，下次必须全量重建
            if (IsInsideTree()) Generate();
        }
    }
    private int _gridN = 128;

    // 显示图层：0=海拔 1=温度 2=降水 3=biome。键盘 1/2/3/4 或 Inspector 切换。
    // 图层只影响颜色 → 几何缓存命中时仅重算颜色（秒级），不重建网格。
    [Export]
    public int Layer
    {
        get => _layer;
        set
        {
            // ⚠️ 即使 _layer == value 也要同步按钮状态：ButtonGroup+ToggleMode 下
            //   点击已选中按钮会取消选中（Pressed 仍触发），若此处直接 return，
            //   按钮 UI 会停在"未选中"状态 → 显示像切换失败。始终同步 + 只在实际变化时重建。
            bool changed = _layer != value;
            GD.Print($"[MapViewer] Layer.set {value} ({LayerName(value)}) changed={changed} geoReady={_geometryReady} pending={_pendingRecolor}");
            if (changed)
            {
                _layer = value;
                if (IsInsideTree()) RebuildColors();
            }
            SyncLayerButtons();
            if (_monsoonArrows != null)
                _monsoonArrows.Visible = (value == 4);
            if (_currentArrows != null)
                _currentArrows.Visible = (value == 5);
            if (_riverMesh != null)
                _riverMesh.Visible = (value == 6);
            if (_monthSlider != null)
                _monthSlider.Visible = (value == 4 || value == 10 || value == 11);
        }
    }
    private int _layer;

    // ── 几何缓存（GridN 变化时失效）──
    private List<HexTile> _tiles;
    private GeometryData _geometry;
    private volatile bool _geometryReady; // 后台写（BuildAll 内），主线程 RebuildColors 读
    private MeshInstance3D _planetMesh;   // 当前星球网格（切图层时挂载新网格前清旧的，防叠加）

    // ── 每格图层值缓存（v3 球面：构建时每格采样一次，切图层 O(1) 查表）──
    // ⚠️ 2026-08-02：旧版每格每次采样都线性扫描 10242 顶点（65 万格 × 2×10242 ≈ 1300 亿次），
    //   进入游戏/切图层极慢。预计算后切图层只查数组 → 秒级。
    private float[] _tileElev;    // 每格归一化海拔 0..1
    private float[] _tileTemp;    // 每格温度 °C
    private float[] _tilePrecip;  // 每格降水 mm
    private byte[] _tileBiome;    // 每格 biome
    private Vector3[] _tileWind;  // 每格盛行风向（单位切向量，盛行风图层用）
    private byte[] _tileLake;     // 每格湖泊标记（0/1；最近顶点直读）
    private int[] _tileWatershed; // 每格流域 id（-1=海洋；读档后从 flow 现场算，不存档）
    private int[] _vertexWatershed; // 每模拟顶点流域 id（现场算）
    private byte[] _tileMineral;  // 每格矿藏（(富度<<4)|矿种；0=无）
    private byte[] _tileSoil;     // 每格土壤肥力 1-5（0=海洋）
    private byte[] _tileMonsoon;  // 每格季风强度 0-255（v3.7；0=无/海洋）
    private byte[] _tileMonthPrecip; // 每格当月降水比例 0-255（v3.8 月降水图层；月份切换时刷新）
    private byte[] _tileMonthTemp;   // 每格当月温度 −60~60°C→0-255（v3.8 月温度图层；月份切换时刷新）
    private int[] _tileVerts;      // 每格最近模拟顶点 id（月降水/月温度刷新用）
    // 文明图层（.cmp 游玩地图；v2 部落模型：人口/文化/部落/科技）
    private World.CivSim.CivSimContext _civCtx;   // 文明演化上下文（null=纯自然地图）
    private float[] _tilePop;       // 每格总人口（Σ 部落，0=无人/海洋）
    private byte[] _tileCulture;    // 每格主导文化标签（0=无）
    private byte[] _tileCultureGroup; // 每格主导文化群（0=无）
    private byte[] _tileReligion;   // 每格主导宗教 0-4（万物有灵→一神教）
    private int[] _tileTribe;       // 每格主导部落 id（-1=无）
    private byte[] _tileTechEpoch;  // 每格主导部落最高技术时代 0-4
    private float _civPopMax;       // 人口图层色带上限（对数归一化用）
    // 自适应色带（用户拍板：最低到最高归一化，不用固定 2000mm）：年降水 / 当月月降水
    private float _precipMin, _precipMax;         // 陆地年降水 min/max（加载时统计）
    private float _monthPrecipMin, _monthPrecipMax; // 陆地当月月降水 min/max（RefreshMonthPrecip 统计）

    // 季风月风场（现场重算，不存档；箭头图数据源）
    private Vector3[][] _monthWind;  // [12][n] 顶点级月风（切向量，长度=强度；0=无风）
    private float[] _monsoonVerts;   // 顶点级季风强度 0-1
    private int _month = 6;          // 当前月份 0-11（默认 7 月）

    // 统一风场箭头（图层 4 显示；热成风月风场，月份滑块切换）
    private MeshInstance3D _monsoonArrows;
    // 洋流箭头（图层 5 显示；红=暖流 蓝=寒流）
    private MeshInstance3D _currentArrows;
    // 河流（图层 6 显示；每条河独立颜色，支流汇合截断）
    private MeshInstance3D _riverMesh;
    // 月份滑块（图层 4/11/12 显示；1-12 月）
    private HSlider _monthSlider;
    private Label _monthLabel;

    // ── 异步生成状态 ──
    private Task<MeshData> _buildTask;
    private System.Threading.CancellationTokenSource _cts; // 切图层/重建时取消旧任务
    private volatile float _progress;   // 0..1，后台线程写、主线程读
    private volatile string _phase = ""; // 当前阶段文字
    private int _buildVersion;           // 递增；过期任务的 FinishGenerate 直接丢弃

    // ── 进度条 UI ──
    private CanvasLayer _uiLayer;
    private PanelContainer _panel;
    private ProgressBar _bar;
    private Label _label;
    private Button[] _layerButtons;

    private static readonly string[] LayerNames = { "海拔", "温度", "降水", "生物群系", "风场", "洋流", "河流", "流域", "矿藏", "土壤", "月降水", "月温度", "人口", "文化", "部落", "科技", "宗教" };

    /// <summary>文明图层调色板（文化/部落标签取色；高区分度 8 色循环）。</summary>
    private static readonly Color[] CulturePalette =
    {
        new(0.95f, 0.30f, 0.25f),  // 红
        new(0.25f, 0.55f, 0.95f),  // 蓝
        new(0.30f, 0.80f, 0.35f),  // 绿
        new(0.95f, 0.70f, 0.20f),  // 橙
        new(0.70f, 0.40f, 0.90f),  // 紫
        new(0.20f, 0.80f, 0.80f),  // 青
        new(0.90f, 0.50f, 0.70f),  // 粉
        new(0.60f, 0.60f, 0.20f),  // 橄榄
    };

    /// <summary>科技图层时代色带（索引 0=新石器 1=青铜 2=铁器 3=古典+）。</summary>
    private static readonly Color[] TechEpochColors =
    {
        new(0.35f, 0.75f, 0.35f),  // 新石器：绿（农业）
        new(0.90f, 0.60f, 0.20f),  // 青铜：橙（冶金）
        new(0.30f, 0.50f, 0.85f),  // 铁器：蓝（铁兵）
        new(0.65f, 0.40f, 0.85f),  // 古典/中世纪：紫（帝国）
    };

    /// <summary>宗教图层色带（ReligionType 0-4）。</summary>
    private static readonly Color[] ReligionColors =
    {
        new(0.45f, 0.72f, 0.45f),  // 万物有灵：绿（旧石器泛灵论）
        new(0.75f, 0.78f, 0.30f),  // 萨满/图腾：黄绿（洞穴壁画时代）
        new(0.90f, 0.55f, 0.30f),  // 祖先崇拜：橙（新石器，哥贝克力）
        new(0.35f, 0.55f, 0.85f),  // 多神教：蓝（青铜神庙神系）
        new(0.60f, 0.35f, 0.80f),  // 一神教：紫（铁器/古典圣典宗教）
    };

    public override void _Ready()
    {
        // 支持命令行 --map=user://maps/xxx（headless 验证/快捷启动，.cmp/.mpa 均可）
        var ua = OS.GetCmdlineUserArgs();
        for (int i = 0; i < ua.Length; i++)
        {
            string a = ua[i];
            string v = a.StartsWith("--") ? a.Substring(2) : a;
            if (v.StartsWith("map=", System.StringComparison.OrdinalIgnoreCase))
                _mapPath = v.Substring(4);
        }
        // 支持从 UI 菜单进入时指定存档路径（ViewerLauncher 静态字段传参）
        if (!string.IsNullOrEmpty(ViewerLauncher.PendingPath))
        {
            _mapPath = ViewerLauncher.PendingPath;
            ViewerLauncher.PendingPath = null;
            GD.Print($"[MapViewer] pending path: {_mapPath}");
        }
        Generate();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            int layer = -1;
            if (key.Keycode == Key.Key1) layer = 0;
            else if (key.Keycode == Key.Key2) layer = 1;
            else if (key.Keycode == Key.Key3) layer = 2;
            else if (key.Keycode == Key.Key4) layer = 3;
            else if (key.Keycode == Key.Key5) layer = 4;
            else if (key.Keycode == Key.Key6) layer = 5;
            else if (key.Keycode == Key.Key7) layer = 6;
            else if (key.Keycode == Key.Key8) layer = 7;
            else if (key.Keycode == Key.Key9) layer = 8;
            else if (key.Keycode == Key.Key0) layer = 9;
            if (layer >= 0)
            {
                Layer = layer;
                GD.Print($"[MapViewer] layer={layer} ({LayerName(layer)})");
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private static string LayerName(int l) => l switch
    {
        0 => "海拔",
        1 => "温度",
        2 => "降水",
        3 => "生物群系",
        4 => "风场",
        5 => "洋流",
        6 => "河流",
        7 => "流域",
        8 => "矿藏",
        9 => "土壤",
        10 => "月降水",
        11 => "月温度",
        12 => "人口",
        13 => "文化",
        14 => "部落",
        15 => "科技",
        16 => "宗教",
        _ => "风场",
    };
    public override void _Process(double delta)
    {
        // 生成中：每帧把后台进度同步到进度条
        if (_panel != null && _panel.Visible)
        {
            _bar.Value = _progress * 100f;
            _label.Text = $"{_phase}  {_progress * 100f:F0}%";
        }
    }

    private void Generate()
    {
        // 重建时只清掉旧的星球网格（不能动场景自带的灯光/相机子节点）
        if (_planetMesh != null)
        {
            _planetMesh.QueueFree();
            _planetMesh = null;
        }

        // 地图读取用 Godot FileAccess（非线程安全）→ 必须留在主线程。
        // 已缓存（_mapLoaded）则跳过——切图层/改 GridN 不重复读 8MB 文件。
        if (!_mapLoaded)
        {
            if (_mapPath.EndsWith(".cmp", System.StringComparison.OrdinalIgnoreCase))
            {
                // 文明游玩地图：读 GameGrid + 文明演化结果 → 转 MapData 供自然图层，文明图层直读 ctx
                if (!World.CivSim.CivMapArchive.Read(_mapPath, out var grid, out var civResult))
                {
                    GD.PrintErr($"[MapViewer] failed to load civ map {_mapPath}");
                    return;
                }
                _map = grid.ToMapData();
                _civCtx = civResult.Context;
                _mapLoaded = true;
                GD.Print($"[MapViewer] loaded civ map {_mapPath} (gridN={grid.GridN} tiles={grid.N} " +
                         $"epoch={civResult.Context.Epoch.Name} ticks={civResult.FinalTick} pop={civResult.Context.TotalPopulation():F0} tribes={civResult.Context.Tribes.Count})");
            }
            else if (!MapArchive.Read(_mapPath, out var map))
            {
                GD.PrintErr($"[MapViewer] failed to load {_mapPath}");
                return;
            }
            else
            {
                _map = map;
                _civCtx = null;
                _mapLoaded = true;
                GD.Print($"[MapViewer] loaded seed={map.Seed} {map.Width}x{map.Height} elev[{map.MinElev:F3},{map.MaxElev:F3}]");
            }

            // ⚠️ 2026-08-02：GridN 对齐生成时的模拟 n（用户要求"游戏看的格子数=生成用的格子数"）。
            //   球面存档顶点数 = 10n²+2（Icosahedron 细分）→ 反推 n = sqrt((verts-2)/10)。
            //   Goldberg hex 格数 = 10×GridN²+2 → GridN=n 时两者恰好相等（10242 格/10242 顶点）。
            if (_map.IsSpherical && _map.Verts != null)
            {
                int simN = (int)Mathf.Round(Mathf.Sqrt((_map.Verts.Length - 2) / 10f));
                if (simN >= 8 && simN <= 512 && simN != _gridN)
                {
                    GD.Print($"[MapViewer] 存档模拟 n={simN}（{_map.Verts.Length} 顶点）→ GridN 对齐 {simN}");
                    _gridN = simN;
                }
            }

            // ⚠️ 2026-08-02：流域现场算（不存档——纯计算毫秒级）。
            //   用存档 Elev（相对海平面 0）+ RiverFlow → 每顶点流域 id（-1=海洋）。
            _vertexWatershed = null;
            if (_map.RiverFlow != null)
            {
                int vn2 = _map.Verts.Length;
                var eNorm = new float[vn2];
                float range = _map.MaxElev - _map.MinElev;
                for (int i = 0; i < vn2; i++)
                    eNorm[i] = range > 1e-6f ? _map.Elev[i] / range : 0f;   // 0=海平面（同生成端）
                RiverSystem.ComputeWatersheds(eNorm, _map.RiverFlow, _map.RiverLevel ?? new byte[vn2],
                    out _vertexWatershed, out _);
                int wsCount = 0;
                for (int i = 0; i < vn2; i++)
                    if (_vertexWatershed[i] > wsCount) wsCount = _vertexWatershed[i];
                GD.Print($"[MapViewer] 流域 {wsCount + 1} 个（现场算）");
            }
        }

        int version = ++_buildVersion;
        _cts?.Cancel();   // 取消旧任务（切图层/重建时旧任务立即停止）
        _cts = new System.Threading.CancellationTokenSource();
        var token = _cts.Token;
        _progress = 0f;
        _phase = "准备生成";
        ShowProgress();

        _buildTask = Task.Run(() => BuildAll(_map, version, token, _layer), token);   // _layer 主线程读（快照）
        _buildTask.ContinueWith(t =>
        {
            // 线程池回调里只做线程安全的事：失败打印 + CallDeferred 回主线程
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] build failed: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }

    /// <summary>后台线程：纯数据构建（不碰任何 Godot 对象）。
    /// ⚠️ 2026-08-02：layer 参数化快照（后台不读共享 _layer 字段）。</summary>
    private MeshData BuildAll(MapData map, int version, System.Threading.CancellationToken token, int layer)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _phase = "细分二十面体";
        _progress = 0.05f;
        Icosahedron.Subdivide(GridN, RadiusKm, out var verts, out var indices);
        if (version != _buildVersion || token.IsCancellationRequested) return default;
        _progress = 0.2f;

        _phase = "构建格子拓扑 (Goldberg dual)";
        var mesh = new SubdividedMesh(verts, indices);
        var tiles = new GoldbergBuilder(mesh, RadiusKm, p => _progress = 0.2f + p * 0.3f).Tiles;
        if (version != _buildVersion || token.IsCancellationRequested) return default;
        _progress = 0.5f;
        // ⚠️ 后台线程禁止 GD.Print（Godot 线程不安全 → 编辑器卡死）——日志移主线程 FinishGenerate

        _phase = "构建几何";
        Func<Vector3, float> elevAt = _ => 0f;
        var geometry = ChunkMeshBuilder.BuildGeometry(tiles, elevAt, RadiusKm, 0f,
            p => _progress = 0.5f + p * 0.3f);
        if (version != _buildVersion || token.IsCancellationRequested) return default;

        // 几何就绪 → 缓存（图层切换直接复用），再算颜色
        _tiles = tiles;
        _geometry = geometry;
        _geometryReady = true;
        _progress = 0.8f;

        // 预计算每格图层值（v3 球面一次采样；切图层 O(1) 查表）
        _phase = "预计算图层值";
        PrecomputeTileValues(map, tiles, token);
        if (version != _buildVersion || token.IsCancellationRequested) return default;

        _phase = "采样并着色";
        var colors = ChunkMeshBuilder.BuildColors(tiles, MakeColorFn(layer), geometry,
            p =>
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                _progress = 0.8f + p * 0.2f;
            });

        _progress = 1f;
        _phase = "完成";
        return new MeshData
        {
            Verts = geometry.Verts,
            Normals = geometry.Normals,
            Colors = colors,
            Indices = geometry.Indices
        };
    }

    /// <summary>预计算每格图层值（v3 球面：每格采样一次，之后切图层 O(1) 查表）。
    /// ⚠️ 2026-08-02：必须并行化（每格独立）+ 禁止后台线程 GD.Print（Godot 线程不安全会卡死）。
    ///   GridN=256 = 65 万格 × 4 次采样，串行 ~25s，并行 ~5s。</summary>
    private void PrecomputeTileValues(MapData map, List<HexTile> tiles, System.Threading.CancellationToken token)
    {
        int n = tiles.Count;
        _tileElev = new float[n];
        _tileTemp = new float[n];
        _tilePrecip = new float[n];
        _tileBiome = new byte[n];
        _tileWind = new Vector3[n];
        _tileLake = new byte[n];
        _tileWatershed = new int[n];
        System.Array.Fill(_tileWatershed, -1);
        _tileMineral = new byte[n];
        _tileSoil = new byte[n];
        _tileMonsoon = new byte[n];
        _tileMonthPrecip = new byte[n];
        _tileMonthTemp = new byte[n];
        _tileVerts = new int[n];
        _tilePop = new float[n];
        _tileCulture = new byte[n];
        _tileCultureGroup = new byte[n];
        _tileReligion = new byte[n];
        _tileTribe = new int[n];
        _tileTechEpoch = new byte[n];
        System.Array.Fill(_tileTribe, -1);
        bool hasCiv = _civCtx != null;
        bool hasTemp = map.Temp != null, hasPrecip = map.Precip != null, hasBiome = map.Biome != null;
        float range = map.MaxElev - map.MinElev;
        float hSea = range > 1e-6f ? -map.MinElev / range : 0.5f;
        var elevArr = _tileElev;   // 局部引用（后台线程安全：不同下标不同位置）
        var tempArr = _tileTemp;
        var precipArr = _tilePrecip;
        var biomeArr = _tileBiome;
        var windArr = _tileWind;
        var lakeArr = _tileLake;
        var wsArr = _tileWatershed;
        var minArr = _tileMineral;
        var soilArr = _tileSoil;
        var monsoonArr = _tileMonsoon;
        bool hasLake = map.LakeLevel != null;
        bool hasMineral = map.MineralLevel != null;
        var centers = new Vector3[n];
        for (int i = 0; i < n; i++) centers[i] = tiles[i].Center;

        // 盛行风图层：用存档自转方向/速度（旧存档默认顺转 1.0）
        World.Biome.WindField.Prograde = map.ProgradeRotation;
        World.Biome.WindField.RotationSpeed = map.RotationSpeed;
        System.Threading.Tasks.Parallel.For(0, n, i =>
        {
            if (token.IsCancellationRequested) return;
            var c = centers[i];
            // ⚠️ 2026-08-02 修复：最近顶点直读（无插值）——原 SampleElevation 是 Shepard
            //   插值（多顶点加权平均）→ 相邻格颜色渐变 → 观感"一团团/有插值/不是等格子"。
            //   每格 = 最近模拟顶点的真实值（crisp flat per-tile，符合用户偏好）。
            int vid = map.NearestVertex(c);
            _tileVerts[i] = vid;   // 缓存（月降水刷新用）
            elevArr[i] = map.NormalizedElev(map.Elev[vid]);
            tempArr[i] = hasTemp ? map.Temp[vid] : 0f;
            precipArr[i] = hasPrecip ? map.Precip[vid] : 0f;
            biomeArr[i] = hasBiome ? map.Biome[vid] : (byte)BiomeType.DeepOcean;
            windArr[i] = World.Biome.WindField.WindAt(c);
            lakeArr[i] = hasLake ? map.LakeLevel[vid] : (byte)0;
            wsArr[i] = _vertexWatershed != null ? _vertexWatershed[vid] : -1;
            minArr[i] = hasMineral ? map.MineralLevel[vid] : (byte)0;
            soilArr[i] = map.SoilLevel != null ? map.SoilLevel[vid] : (byte)0;
            monsoonArr[i] = map.MonsoonLevel != null ? map.MonsoonLevel[vid] : (byte)0;
            // 文明图层（.cmp：格 id = 模拟顶点 id，零重采样直读；v2 部落模型：格=容器，主导部落=人口最大）
            if (hasCiv)
            {
                _tilePop[i] = _civCtx.CellPop[vid];
                var tlist = _civCtx.CellTribes[vid];
                if (tlist.Count > 0)
                {
                    var dom = tlist[0];
                    for (int k = 1; k < tlist.Count; k++)
                        if (tlist[k].Population > dom.Population) dom = tlist[k];
                    _tileCulture[i] = dom.Culture;
                    _tileCultureGroup[i] = dom.CultureGroup;
                    _tileReligion[i] = dom.Religion;
                    _tileTribe[i] = dom.Id;
                    _tileTechEpoch[i] = (byte)World.CivSim.TechTable.MaxEpoch(dom.TechFlags);
                }
            }
        });
        // 人口图层色带上限（对数归一化；无文明数据 → 1 避免除零）
        _civPopMax = 1f;
        for (int i = 0; i < n; i++)
            if (_tilePop[i] > _civPopMax) _civPopMax = _tilePop[i];
        // ⚠️ 2026-08-16：年降水自适应色带 min/max（用户拍板：最低到最高归一化，不用固定 2000mm）
        _precipMin = float.MaxValue;
        _precipMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (elevArr[i] < hSea) continue;   // 只统计陆地格
            _precipMin = Mathf.Min(_precipMin, precipArr[i]);
            _precipMax = Mathf.Max(_precipMax, precipArr[i]);
        }
        if (_precipMax <= _precipMin) _precipMax = _precipMin + 1f;
        _hSea = hSea;
    }
    private float _hSea = 0.5f;

    /// <summary>图层 → 颜色函数（查预计算缓存，零采样）。</summary>
    /// ⚠️ 2026-08-02 大改进：参数化 layer（不读共享字段）——原内部 switch(Layer) 在后台
    ///   线程每次调用读 _layer，主线程切图层写它 → 竞态 → 偶发颜色错图层/"未切换成功"。</summary>
    private Func<HexTile, Color> MakeColorFn(int layer)
    {
    	return t =>
    	{
    		int id = t.Id;
    		switch (layer)
    		{
    			case 1: // 温度
    				return BiomeColors.TemperatureToColor(_tileTemp[id]);
    						case 2: // 降水：自适应色带（陆地 min-max 归一化，用户拍板；固定 2000mm 已被批）
    							{
    								float x = Mathf.Clamp((_tilePrecip[id] - _precipMin) / (_precipMax - _precipMin), 0f, 1f);
    								return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    							}
    			case 3: // biome
    				return BiomeColors.BiomeToColor((BiomeType)_tileBiome[id]);
    						case 4: // 风场：浅色底（统一风场箭头由 _monsoonArrows 3D 网格显示，月份滑块切换）
    						case 5: // 洋流：浅色底（箭头由 _currentArrows 3D 网格显示）
    			case 6: // 河流：浅色底（河道由 _riverMesh 3D 网格显示，湖格填湖蓝）
    				{
    					// ⚠️ 2026-08-02：湖泊 = 陆地盆地 + 水量≥阈值（RiverSystem 已过滤干湖）。
    					//   湖格单色湖蓝（用户确认：单色、放河流图层）；其他格淡色底突出河道。
    					if (_tileLake[id] > 0)
    						return new Color(0.25f, 0.45f, 0.75f);   // 湖蓝（单色）
    					float h = _tileElev[id];
    					bool ocean = h < _hSea;
    					return ocean ? new Color(0.45f, 0.55f, 0.70f) : new Color(0.72f, 0.68f, 0.55f);
    				}
    			case 7: // 流域：每流域独立颜色（黄金角）；海洋浅蓝、边缘排水区灰绿
    				{
    					int ws = _tileWatershed[id];
    					if (ws < 0)
    						return _tileElev[id] < _hSea
    							? new Color(0.45f, 0.55f, 0.70f)   // 海洋
    							: new Color(0.60f, 0.58f, 0.50f);  // 边缘排水区（直接入海，非河）
    					return HslToRgb((ws * 0.6180339887f) % 1f, 0.55f, 0.62f);
    				}
    			case 8: // 矿藏：矿种固定色 × 富度明度（贫暗/富中/巨型亮）；无矿淡地形底
    				{
    					byte m = _tileMineral[id];
    					if (m == 0)
    					{
    						float h = _tileElev[id];
    						return h < _hSea
    							? new Color(0.45f, 0.55f, 0.70f)
    							: new Color(0.55f, 0.52f, 0.42f);
    					}
    					var baseC = MineralColors[MineralSystem.TypeOf(m) % MineralColors.Length];
    					float bright = MineralSystem.RichnessOf(m) switch { 1 => 0.55f, 2 => 0.78f, _ => 1.0f };
    					return baseC * bright;
    				}
    			case 9: // 土壤肥力：5 档色带（深绿=肥沃 → 灰=贫瘠）；海洋浅蓝
    			{
    			byte s = _tileSoil[id];
    			if (s == 0)
    			return new Color(0.45f, 0.55f, 0.70f);   // 海洋
    			return SoilColors[Mathf.Clamp(s, 1, 5)];
    			}
    																			case 10: // 月降水：和总降水同一自适应色带（当月陆地 min-max 归一化；月份滑块切换）
    																				{   // ⚠️ 2026-08-16 v3（用户拍板）：与总降水同色带同统计方式；×12 换算回年尺度
    																					//   → 非季风区≈年降水色，季风区 7 月深蓝 / 1 月枯黄；min-max 自适应当月分布
    																					if (_tileMonthPrecip == null || _map == null || _map.MonthPrecip == null)
    																						return _tileElev[id] < _hSea
    																							? new Color(0.45f, 0.55f, 0.70f)
    																							: new Color(0.72f, 0.70f, 0.58f);
    																					if (_tileElev[id] < _hSea) return new Color(0.45f, 0.55f, 0.70f);
    																					float mm = _tileMonthPrecip[id] / 255f * _tilePrecip[id] * 12f;   // 等效年尺度
    																					float x = Mathf.Clamp((mm - _monthPrecipMin) / (_monthPrecipMax - _monthPrecipMin), 0f, 1f);
    																					return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    																				}
    						case 11: // 月温度：当月均温色块（MonthTemp −60~60°C→0-255；月份滑块切换）
    							{
    								if (_tileMonthTemp == null || _map == null || _map.MonthTemp == null)
    									return _tileElev[id] < _hSea
    										? new Color(0.45f, 0.55f, 0.70f)
    										: new Color(0.72f, 0.70f, 0.58f);
    								float tC = _tileMonthTemp[id] / 255f * 120f - 60f;   // byte → °C
    								return BiomeColors.TemperatureToColor(tC);
    								}
    								case 12: // 人口：对数色带（无人=暗灰；黄→橙红，对数归一化防极端值拉爆）
    								{
    								if (_tileElev[id] < _hSea) return new Color(0.45f, 0.55f, 0.70f);
    								float p = _tilePop[id];
    								if (p <= 0f) return new Color(0.25f, 0.25f, 0.28f);   // 无人陆地
    								float x = Mathf.Log(p + 1f) / Mathf.Log(_civPopMax + 1f);
    								return new Color(0.95f, 0.75f, 0.25f).Lerp(new Color(0.80f, 0.15f, 0.05f), x);
    								}
    								case 13: // 文化：标签调色板
    								    {
    								        if (_tileElev[id] < _hSea) return new Color(0.45f, 0.55f, 0.70f);
    								        byte cult = _tileCulture[id];
    								        if (cult == 0) return new Color(0.25f, 0.25f, 0.28f);
    								        return CulturePalette[cult % CulturePalette.Length];
    								    }
    								case 14: // 部落：谱系调色板
    								    {
    								        if (_tileElev[id] < _hSea) return new Color(0.45f, 0.55f, 0.70f);
    								        int tribeId = _tileTribe[id];
    								        if (tribeId < 0) return new Color(0.25f, 0.25f, 0.28f);
    								        return CulturePalette[tribeId % CulturePalette.Length];
    								    }
    								case 15: // 科技：主导部落最高技术时代色带（石器棕→新石器绿→青铜橙→铁器蓝→古典紫）
    								    {
    								        if (_tileElev[id] < _hSea) return new Color(0.45f, 0.55f, 0.70f);
    								        if (_tileTribe[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
    								        byte ep = _tileTechEpoch[id];
    								        if (ep == 0) return new Color(0.55f, 0.42f, 0.28f);   // 石器：棕（有基础技术，非"无"）
    								        return TechEpochColors[Mathf.Clamp(ep - 1, 0, TechEpochColors.Length - 1)];
    								    }
    								case 16: // 宗教：阶段色带（万物有灵绿→萨满黄绿→祖先橙→多神蓝→一神紫）
    								    {
    								        if (_tileElev[id] < _hSea) return new Color(0.45f, 0.55f, 0.70f);
    								        if (_tileTribe[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
    								        return ReligionColors[Mathf.Clamp(_tileReligion[id], 0, ReligionColors.Length - 1)];
    								    }
    			default: // 海拔
    				{
    					float h = _tileElev[id];
    					// h 是 0..1 min/max 归一化，海平面位置 = -MinElev/range（≠0.5）。
    					// 转成以海平面为 0 的 -1..1（ElevationToColor 色阶约定：-1 深海/0 海平面/1 雪顶）。
    					float e1 = (h - _hSea) / (_hSea > 0.5f ? _hSea : 1f - _hSea);
    					return PlanetColors.ElevationToColor(e1);
    				}
    		}
    	};
    }

    /// <summary>切图层：几何缓存命中 → 只重算颜色（查表，秒级）；无缓存（首次/GridN 刚变）→ 全量。
    /// ⚠️ 2026-08-02：几何未就绪时【禁止】调用 Generate()——构建中切图层会取消当前构建并重启，
    ///   快速连点=无限取消重启，几何永远构建不完 → 图层不切换。改为设置 _pendingRecolor，
    ///   等当前构建完成（FinishGenerate）后自动应用最新图层。</summary>
    private void RebuildColors()
    {
        if (!_geometryReady || _tiles == null)
        {
            _pendingRecolor = true;   // 构建完成后自动重算颜色（用最新 Layer）
            GD.Print($"[MapViewer] RebuildColors: 几何未就绪 → pendingRecolor（Layer={_layer}）");
            return;
        }

        int version = ++_buildVersion;
        int layer = _layer;   // ⚠️ 主线程快照（后台只读快照，不碰共享字段）
        _cts?.Cancel();   // 取消旧重算任务
        _cts = new System.Threading.CancellationTokenSource();
        var token = _cts.Token;
        _progress = 0f;
        _phase = "重算颜色";
        ShowProgress();
        GD.Print($"[MapViewer] RebuildColors: v{version} Layer={layer} 启动后台着色");
        _buildTask = Task.Run(() => BuildColorsTask(_map, version, token, layer), token);
        _buildTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] recolor failed: {t.Exception?.GetBaseException().Message}\n{t.Exception?.GetBaseException().StackTrace}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }
    private volatile bool _pendingRecolor;   // 构建中切图层 → 完成后自动重算

    /// <summary>后台线程：只重算颜色（查预计算缓存，零采样）。
    /// ⚠️ 2026-08-02 大改进：layer 参数化快照——后台不读 _layer 字段（消除竞态）；
    ///   进度回调查 token，取消可中断（旧任务快速让位新图层）。</summary>
    private MeshData BuildColorsTask(MapData map, int version, System.Threading.CancellationToken token, int layer)
    {
        var geometry = _geometry; // 已就绪（_geometryReady 保证，不碰 Godot 对象）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var colors = ChunkMeshBuilder.BuildColors(_tiles, MakeColorFn(layer), geometry,
            p =>
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);   // 取消中断（快速让位）
                _progress = 0.05f + p * 0.9f;
            });
        if (token.IsCancellationRequested) return default;
        _progress = 1f;
        return new MeshData
        {
            Verts = geometry.Verts,
            Normals = geometry.Normals,
            Colors = colors,
            Indices = geometry.Indices
        };
    }

    /// <summary>主线程：把后台构建好的数据包成 ArrayMesh 并挂载。</summary>
    private void FinishGenerate(int version)
    {
        GD.Print($"[MapViewer] FinishGenerate v{version} (当前 _buildVersion={_buildVersion}, Layer={_layer})");
        if (version != _buildVersion)
            return; // 用户在生成期间又改了 GridN，丢弃过期结果

        try
        {
            var data = _buildTask.Result;
            if (data.Verts == null)
            {
                GD.Print("[MapViewer] build cancelled (superseded by newer request)");
                return;
            }
            var shader = GD.Load<Shader>("res://shaders/planet_detail.gdshader");
            // ⚠️ 2026-08-02 关键修复：挂载新星球前清掉旧的——原 RebuildColors(切图层) 路径
            //   不清旧网格 → 每次切图层 AddChild 叠加一个星球 MeshInstance3D → 多个星球
            //   重叠渲染显示旧图层颜色 → "未切换成功"！
            if (_planetMesh != null)
            {
                _planetMesh.QueueFree();
                _planetMesh = null;
            }
            var mi = new MeshInstance3D
            {
                Mesh = ChunkMeshBuilder.CreateMesh(data),
                MaterialOverride = new ShaderMaterial { Shader = shader }
            };
            AddChild(mi);
            _planetMesh = mi;
            GD.Print($"[MapViewer] sphere ready: {data.Indices.Length / 3} tris (tiles={_tiles?.Count ?? 0})");

            // 统一风场箭头网格（图层 4 显示；热成风：信风/西风/季风一体，月份滑块切换）
            BuildMonsoonArrows();
            // 月降水缓存（图层 11 显示；当前月）
            RefreshMonthPrecip();
            // 月温度缓存（图层 12 显示；当前月）
            RefreshMonthTemp();
            // 洋流箭头网格（图层 5 显示；暖流红/寒流蓝）
            BuildCurrentArrows();
            // 河流网格（图层 6 显示；每条河独立颜色，支流汇合截断）
            BuildRivers();

            // 构建中切了图层 → 自动应用最新图层（几何已就绪，走快速重算）
            if (_pendingRecolor)
            {
                _pendingRecolor = false;
                RebuildColors();
            }
        }
        catch (Exception e)
        {
            // ⚠️ 2026-08-02：任务被取消（切图层/重建）时 Result 抛 AggregateException
            //   （Inner = OperationCanceledException）——正常路径，不误报。
            if (e is AggregateException ae && ae.InnerException is OperationCanceledException)
            {
                GD.Print("[MapViewer] build cancelled (superseded by newer request)");
                return;
            }
            GD.PrintErr($"[MapViewer] finish failed: {e}\n{e.StackTrace}");
        }
        finally
        {
            HideProgress();
        }
    }

    /// <summary>懒算季风月风场（读档后第一次进风场/月降水/月温度图层时算一次；不存档）。
    /// 用存档的海陆/年温/年降水 + 倾角（v3.8 头部）现场跑 MonsoonSystem。</summary>
    private void EnsureMonthWind()
    {
        if (_monthWind != null || _map == null || _map.Verts == null) return;
        var nb = _map.BuildNeighbors();
        if (nb == null) return;
        int n = _map.Verts.Length;
        float span = Mathf.Max(-_map.MinElev, _map.MaxElev);
        var eNorm = new float[n];
        for (int i = 0; i < n; i++)
            eNorm[i] = span > 1e-6f ? _map.Elev[i] / span : 0f;
        MonsoonSystem.Compute(_map.Verts, nb, eNorm, _map.Elev, _map.Temp, _map.Precip, _map.AxialTilt, _map.RotationSpeed,
            new ClimateGenerator(_map.Seed, _map.AxialTilt, 1f),
            out var mons, out _, out _, out _, out _, out _, out var mw, out var mt, out _);
        _monthWind = mw;
        _monsoonVerts = mons;
        GD.Print($"[MapViewer] 季风月风场重算完成（{n} 顶点，倾角 {_map.AxialTilt}°）");
    }

    /// <summary>季风月风箭头（图层 10 显示；方向 = 当月季风环流风，稀疏采样）。
    /// 复刻 BuildWindArrows 的箭头几何；无风（海洋/非季风区）不画。</summary>
    private void BuildMonsoonArrows()
    {
        if (_monsoonArrows != null)
        {
            _monsoonArrows.QueueFree();
            _monsoonArrows = null;
        }
        if (_tiles == null) return;
        EnsureMonthWind();
        if (_monthWind == null) return;

        const float arrowLen = 0.045f;    // 小箭头（0.07 原值；只标方向，不随强度缩放）
        const float tailW = 0.016f;
        float radius = RadiusKm * 1.01f;   // 浮在球面上方防 z-fighting

        var verts = new System.Collections.Generic.List<Vector3>();
        var indices = new System.Collections.Generic.List<int>();

        // ⚠️ 2026-08-16：密集 3 倍（lat 步 12°→4°）；每环经度点数随 cos(lat) 递减（极区少点）
        for (float lat = -88f; lat <= 88f; lat += 4f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(8, Mathf.RoundToInt(36 * cosLa));
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                var dir = new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo));
                int vid = _map.NearestVertex(dir);
                var wind = _monthWind[_month][vid];
                if (wind.LengthSquared() < 1e-9f) continue;   // 无风区不画
                var wDir = wind.Normalized();                 // 只标记方向
                var side = dir.Cross(wDir).Normalized();

                Vector3 tailC = dir - wDir * arrowLen * 0.35f;
                Vector3 tip = dir + wDir * arrowLen * 0.65f;
                Vector3 t1 = (tailC + side * tailW).Normalized() * radius;
                Vector3 t2 = (tailC - side * tailW).Normalized() * radius;
                Vector3 tipS = tip.Normalized() * radius;

                int baseIdx = verts.Count;
                verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // 青蓝色（海风色；与盛行风橙色区分）
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.25f, 0.78f, 0.92f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        _monsoonArrows = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 4),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_monsoonArrows);
        GD.Print($"[MapViewer] monsoon arrows built: {verts.Count / 3} arrows (月={_month + 1})");
    }

    /// <summary>刷新当月温度缓存（月温度图层用；月份滑块变化时调用）。</summary>
    private void RefreshMonthTemp()
    {
        if (_tileMonthTemp == null || _map == null || _map.MonthTemp == null) return;
        int n = _tileMonthTemp.Length;
        var arr = _map.MonthTemp[_month];
        for (int i = 0; i < n; i++)
            _tileMonthTemp[i] = arr != null ? arr[_tileVerts[i]] : (byte)0;
    }

    /// <summary>刷新当月降水缓存（月降水图层用；月份滑块变化时调用）。</summary>
    private void RefreshMonthPrecip()
    {
        if (_tileMonthPrecip == null || _map == null || _map.MonthPrecip == null) return;
        int n = _tileMonthPrecip.Length;
        var arr = _map.MonthPrecip[_month];
        for (int i = 0; i < n; i++)
            _tileMonthPrecip[i] = arr != null ? arr[_tileVerts[i]] : (byte)0;
        // ⚠️ 2026-08-16：自适应色带——当月陆地月降水 min/max（用户拍板：最低到最高归一化）
        _monthPrecipMin = float.MaxValue;
        _monthPrecipMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (_tileElev[i] < _hSea) continue;   // 只统计陆地格
            float mm = _tileMonthPrecip[i] / 255f * _tilePrecip[i] * 12f;   // 等效年尺度
            _monthPrecipMin = Mathf.Min(_monthPrecipMin, mm);
            _monthPrecipMax = Mathf.Max(_monthPrecipMax, mm);
        }
        if (_monthPrecipMax <= _monthPrecipMin) _monthPrecipMax = _monthPrecipMin + 1f;
    }

    // ── 洋流箭头网格（图层 5 显示）──
    // 用存档洋流场（生成时流函数法算好存 v3.1 尾部）：方向 + 冷暖。
    // ⚠️ 2026-08-02 v2：用户纠正——真实洋流图（网上）是【特定流线】不铺满海洋。
    //   从均匀种子沿洋流方向场追踪流线（streamline），只保留长度足够的流线，
    //   沿流线画箭头 → 湾流/黑潮式清晰流线束，开阔大洋空白。
    //   暖流（warmth>0.05）→ 红橙，寒流（< -0.05）→ 蓝，中性 → 灰白。
    private void BuildCurrentArrows()
    {
        if (_currentArrows != null)
        {
            _currentArrows.QueueFree();
            _currentArrows = null;
        }
        if (_map == null || _map.CurrentDirs == null || _map.CurrentWarmth == null)
        {
            GD.Print("[MapViewer] current arrows skipped: 存档无洋流段（旧版）");
            return;
        }

        float radius = RadiusKm * 1.01f;

        var verts = new System.Collections.Generic.List<Vector3>();
        var colors = new System.Collections.Generic.List<Color>();
        var indices = new System.Collections.Generic.List<int>();

        // ── 用户拍板(2026-08-06)：格点稀疏箭头——放弃闭合环追踪（n=128 追踪全失败）。
        //    稀疏采样（lat 10°≈隔 10 格）+ 强度筛选（只画主要洋流带，不铺满——网上洋流图式），
        //    箭头大小固定为星球比例（不随分辨率变——n=64/n=128 观感一致）。
        const float arrowLen = 0.045f;    // 箭头长（球面弧比例，固定）
        const float arrowTailW = 0.016f;
        int drawn = 0;
        for (float lat = -85f; lat <= 85f; lat += 10f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(8, Mathf.RoundToInt(36 * cosLa));
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                var pos = new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo));
                int vid = _map.NearestVertex(pos);
                if (_map.SampleSpherical(pos, _map.Elev) >= 0f) continue;   // 陆地不画
                var cur = _map.CurrentDirs[vid];
                if (cur.LengthSquared() < 1e-9f) continue;                   // 无洋流不画
                if (_map.CurrentStrength != null && _map.CurrentStrength[vid] < 0.35f) continue;   // 只画主要洋流带
                var wDir = cur.Normalized();
                var side = pos.Cross(wDir).Normalized();
                Vector3 tailC = pos - wDir * arrowLen * 0.35f;
                Vector3 tip = pos + wDir * arrowLen * 0.65f;
                Vector3 t1 = (tailC + side * arrowTailW).Normalized() * radius;
                Vector3 t2 = (tailC - side * arrowTailW).Normalized() * radius;
                Vector3 tipS = tip.Normalized() * radius;
                // 每箭头独立冷暖色（湾流暖 / 加那利寒是同一环两侧）
                float w = _map.CurrentWarmth[vid];
                Color c = w > 0.05f ? new Color(1f, 0.45f, 0.2f)
                    : w < -0.05f ? new Color(0.25f, 0.55f, 1f)
                    : new Color(0.85f, 0.85f, 0.85f);
                int ai = verts.Count;
                verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                colors.Add(c); colors.Add(c); colors.Add(c);
                indices.Add(ai); indices.Add(ai + 1); indices.Add(ai + 2);
                drawn++;
            }
        }
        GD.Print($"[MapViewer] current arrows built: {drawn} 箭头（格点稀疏采样，固定大小）");

        if (verts.Count == 0)
        {
            GD.Print("[MapViewer] current arrows: 无洋流数据");
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // unshaded + 顶点色
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        _currentArrows = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 5),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_currentArrows);
        GD.Print($"[MapViewer] current arrows built: {drawn} 箭头（稀疏采样，固定大小） (from archive)");
    }

    // ── 河流网格（图层 6 显示）──
    // 用存档河流（riverLevel + flow）→ RebuildPaths 重建主河道 → 每条河独立颜色
    // （HSL 黄金角），支流在汇合点截断（painted 集合），主河先画（长→短）。
    private void BuildRivers()
    {
        if (_riverMesh != null)
        {
            _riverMesh.QueueFree();
            _riverMesh = null;
        }
        if (_map == null || _map.RiverLevel == null || _map.RiverFlow == null)
        {
            GD.Print("[MapViewer] rivers skipped: 存档无河流段（旧版）");
            return;
        }

        // 归一化海拔（读档 Elev 是米 → 归一化，<0 = 海洋）
        var verts = _map.Verts;
        int n = verts.Length;
        var eNorm = new float[n];
        float range = Mathf.Max(-_map.MinElev, _map.MaxElev);
        for (int i = 0; i < n; i++) eNorm[i] = range > 1e-6f ? _map.Elev[i] / range : 0f;

        // 重建主河道（源头 → 入海/盆地）
        var paths = World.MapGen.RiverSystem.RebuildPaths(_map.RiverFlow, _map.RiverLevel, eNorm);
        if (paths.Count == 0)
        {
            GD.Print("[MapViewer] rivers: 无主河道");
            return;
        }

        float radius = RadiusKm * 1.012f;   // 略高于球面，避免 z-fighting
        var vertList = new System.Collections.Generic.List<Vector3>();
        var colorList = new System.Collections.Generic.List<Color>();
        var indexList = new System.Collections.Generic.List<int>();

        // 主河先画（长→短），支流遇已画顶点截断（汇合点）
        var painted = new System.Collections.Generic.HashSet<int>();
        paths.Sort((a, b) => b.Length.CompareTo(a.Length));
        // ⚠️ 2026-08-06：河宽按分辨率缩放——固定 halfW 在 n=128 格距减半时相对粗 2 倍。
        //   统一按格距比例：halfW = 格距 × 0.13（n=64 时即原 0.004）
        int simN = (int)Mathf.Round(Mathf.Sqrt((n - 2) / 10f));
        float gridArc = Mathf.Tau / (Mathf.Sqrt(10f) * Mathf.Max(8, simN));
        float halfW = gridArc * 0.13f;   // 河宽 ≈ 0.26 格距（观感统一，随分辨率缩放）
        int riverCount = 0;
        foreach (var path in paths)
        {
            // 每条河独立颜色：HSL 色相黄金角循环（相邻河差异最大）
            float hue = (riverCount * 0.6180339887f) % 1f;
            var c = HslToRgb(hue, 0.9f, 0.55f);
            riverCount++;
            bool drawn = false;
            for (int i = 0; i < path.Length - 1; i++)
            {
                int va = path[i], vb = path[i + 1];
                if (painted.Contains(va)) break;   // 遇汇合点 → 支流段结束
                painted.Add(va);
                Vector3 a = verts[va], b = verts[vb];
                Vector3 seg = b - a;
                if (seg.LengthSquared() < 1e-12f) continue;
                Vector3 side = seg.Cross(a).Normalized();
                Vector3 l0 = (a + side * halfW).Normalized() * radius;
                Vector3 r0 = (a - side * halfW).Normalized() * radius;
                Vector3 l1 = (b + side * halfW).Normalized() * radius;
                Vector3 r1 = (b - side * halfW).Normalized() * radius;
                int bi = vertList.Count;
                vertList.Add(l0); vertList.Add(r0); vertList.Add(l1); vertList.Add(r1);
                colorList.Add(c); colorList.Add(c); colorList.Add(c); colorList.Add(c);
                indexList.Add(bi); indexList.Add(bi + 1); indexList.Add(bi + 2);
                indexList.Add(bi + 1); indexList.Add(bi + 3); indexList.Add(bi + 2);
                drawn = true;
            }
            if (!drawn) riverCount--;   // 全被截断（纯支流无独有段）→ 不计
        }

        if (vertList.Count == 0)
        {
            GD.Print("[MapViewer] rivers: 无可见河道");
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertList.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colorList.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indexList.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        _riverMesh = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 6),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_riverMesh);
        GD.Print($"[MapViewer] rivers built: {riverCount} 条主河道 / {paths.Count} 源 (from archive)");
        // ⚠️ 2026-08-03：headless 验证构建完成即退——取消 --quit-after 800 帧空转
        //   （构建完不再等帧数；验证循环 n=16 从 ~51s 减到 ~15s）
        if (OS.HasFeature("headless"))
            GetTree().Quit();
    }

    static Color HslToRgb(float h, float s, float l)
    {
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float H2R(float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
        return new Color(H2R(h + 1f / 3f), H2R(h), H2R(h - 1f / 3f));
    }

    // ── 进度条 UI ──

    private void ShowProgress()
    {
        EnsureUi();
        _panel.Visible = true;
        _bar.Value = 0;
    }

    private void HideProgress()
    {
        if (_panel != null)
            _panel.Visible = false;
    }

    private void EnsureUi()
    {
        if (_uiLayer != null)
            return;

        _uiLayer = new CanvasLayer { Layer = 100 };
        AddChild(_uiLayer);

        _panel = new PanelContainer();
        _panel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _panel.Position = new Vector2(-470, -90);
        _uiLayer.AddChild(_panel);

        var vbox = new VBoxContainer();
        _panel.AddChild(vbox);

        _label = new Label { Text = "生成星球中..." };
        vbox.AddChild(_label);

        _bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            ShowPercentage = true,
            CustomMinimumSize = new Vector2(420, 26)
        };
        vbox.AddChild(_bar);

        _panel.Visible = false;

        // ── 图层切换按钮组（屏幕下方中间）──
        // ⚠️ 2026-08-02 v3：SVG 图标按钮（每个 42px，整排 ~294px）。
        //   只显示 4 个的真相 = 后 3 个 SVG 用了 Q/T/A 曲线命令，thorvg 解析器不支持
        //   → 加载失败空白（非宽度问题）。全部图标已重写为纯直线命令 M/L/H/V/Z。
        //   悬停 TooltipText 显示中文名。
        var group = new ButtonGroup();
        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom); // 锚点底部居中
        hbox.Position = new Vector2(-21f * LayerNames.Length, -50);   // 42px/按钮动态居中
        _uiLayer.AddChild(hbox);

        _layerButtons = new Button[LayerNames.Length];
        for (int i = 0; i < LayerNames.Length; i++)
        {
            int idx = i; // 闭包捕获
            var btn = new Button
            {
                Icon = MakeLayerIcon(i),
                TooltipText = LayerNames[i],
                ToggleMode = true,
                ButtonGroup = group,
                CustomMinimumSize = new Vector2(42, 38),
                IconAlignment = HorizontalAlignment.Center,
            };
            btn.Pressed += () => Layer = idx;
            hbox.AddChild(btn);
            _layerButtons[i] = btn;
        }
        SyncLayerButtons();

        // ── 月份滑块（图层 10/11 显示；1-12 月切换季风箭头/月降水）──
        var monthRow = new HBoxContainer();
        monthRow.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        monthRow.Position = new Vector2(-130, -100);   // 图层按钮（-50）上方
        monthRow.AddThemeConstantOverride("separation", 8);
        _uiLayer.AddChild(monthRow);

        var mlabel = new Label { Text = "月份", VerticalAlignment = VerticalAlignment.Center };
        mlabel.AddThemeFontSizeOverride("font_size", 18);
        monthRow.AddChild(mlabel);

        _monthSlider = new HSlider
        {
            MinValue = 1,
            MaxValue = 12,
            Step = 1,
            Value = _month + 1,
            CustomMinimumSize = new Vector2(200, 34),
        };
        _monthSlider.ValueChanged += v =>
        {
            int m = (int)v - 1;
            if (m == _month) return;
            _month = m;
            _monthLabel.Text = $"{m + 1} 月";
            // 风场图层：重建箭头；月降水/月温度图层：刷新缓存 + 重算颜色
            if (Layer == 4) BuildMonsoonArrows();
            else if (Layer == 10) { RefreshMonthPrecip(); RebuildColors(); }
            else if (Layer == 11) { RefreshMonthTemp(); RebuildColors(); }
        };
        monthRow.AddChild(_monthSlider);

        _monthLabel = new Label { Text = $"{_month + 1} 月", VerticalAlignment = VerticalAlignment.Center };
        _monthLabel.AddThemeFontSizeOverride("font_size", 18);
        _monthLabel.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
        monthRow.AddChild(_monthLabel);

        _monthSlider.Visible = false;   // 默认隐藏，进季风/月降水图层才显示
    }

    /// <summary>矿种固定色（索引 = MineralSystem 矿种：1铁 2铜 3锡 4金 5煤 6盐 7石料 8宝石）。
    /// 显示时 × 富度明度（贫 0.55 / 富 0.78 / 巨型 1.0）——用户确认：固定色 + 富度深浅。</summary>
    private static readonly Color[] MineralColors =
    {
        Colors.Gray,                                       // 0 无（不使用）
        new Color(0.55f, 0.50f, 0.45f),                    // 1 铁：灰褐
        new Color(0.75f, 0.45f, 0.20f),                    // 2 铜：铜橙
        new Color(0.75f, 0.75f, 0.80f),                    // 3 锡：银白
        new Color(0.95f, 0.75f, 0.15f),                    // 4 金：金黄
        new Color(0.18f, 0.18f, 0.20f),                    // 5 煤：黑
        new Color(0.95f, 0.95f, 0.90f),                    // 6 盐：白
        new Color(0.70f, 0.68f, 0.62f),                    // 7 石料：石灰
        new Color(0.62f, 0.30f, 0.78f),                    // 8 宝石：紫
    };

    /// <summary>土壤肥力 5 档色带（索引 1-5：深绿=肥沃 → 灰=贫瘠；0 不用）。</summary>
    private static readonly Color[] SoilColors =
    {
        Colors.Gray,                                       // 0 海洋（不使用）
        new Color(0.55f, 0.48f, 0.38f),                    // 1 贫瘠：灰棕
        new Color(0.62f, 0.52f, 0.36f),                    // 2 差：棕
        new Color(0.72f, 0.62f, 0.35f),                    // 3 中：黄
        new Color(0.45f, 0.68f, 0.35f),                    // 4 好：绿
        new Color(0.20f, 0.55f, 0.25f),                    // 5 肥沃：深绿
    };

    /// <summary>图层按钮 SVG 图标（纯直线 M/L/H/V/Z——thorvg 不支持 Q/T/A 曲线）。</summary>
    private static readonly string[] LayerIcons =
    {
        // 0 海拔：两座山（直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M3 23 L11 8 L16 17 L19 12 L25 23 Z' fill='#eee'/></svg>",
        // 1 温度：温度计（杆+刻度+圆泡，直线近似）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L14 14 L18 14 L18 20 L10 20 L10 14 L14 14 M10 20 L18 20 M10 17 L18 17' stroke='#eee' stroke-width='2.5' fill='none'/><path d='M11 21 L17 21 L17 24 L11 24 Z' fill='#eee'/></svg>",
        // 2 降水：菱形雨滴 + 两侧小滴（直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L19 13 L14 23 L9 13 Z' fill='#eee'/><path d='M6 20 L4 25 M22 20 L24 25' stroke='#eee' stroke-width='2'/></svg>",
        // 3 生物群系：树（三角冠+干，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L20 12 L17 12 L22 20 L6 20 L11 12 L8 12 Z' fill='#eee'/><rect x='12.5' y='20' width='3' height='6' fill='#eee'/></svg>",
        // 4 盛行风：三条横线 + 箭头（直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M4 8 L18 8 M4 14 L22 14 M4 20 L14 20' stroke='#eee' stroke-width='2'/><path d='M20 4 L25 8 L20 12 Z' fill='#eee'/></svg>",
        // 5 洋流：锯齿波浪（直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M2 10 L6 6 L10 10 L14 6 L18 10 L22 6 L26 10 M2 18 L6 14 L10 18 L14 14 L18 18 L22 14 L26 18' stroke='#eee' stroke-width='2' fill='none'/></svg>",
        // 6 河流：折线河道（直线，蓝色）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M6 2 L10 6 L8 10 L14 14 L12 18 L15 22 L14 26' stroke='#6cf' stroke-width='3' fill='none' stroke-linecap='round'/></svg>",
        // 7 流域：分水岭+两支流（直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L7 13 L4 24 M14 3 L21 13 L24 24 M14 3 L14 24' stroke='#8f8' stroke-width='2' fill='none' stroke-linecap='round'/></svg>",
        // 8 矿藏：矿石晶体（菱形，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 2 L24 9 L22 20 L14 26 L6 20 L4 9 Z M14 2 L14 26 M4 9 L14 14 L24 9 M6 20 L14 14 L22 20' stroke='#fd8' stroke-width='1.5' fill='none'/></svg>",
        // 9 土壤：层状土层（横线，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M3 6 H25 M3 12 H25 M3 18 H25 M3 24 H25' stroke='#8a6' stroke-width='3' fill='none'/></svg>",
        // 10 月降水：日历（框+挂环+月份点，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><rect x='4' y='6' width='20' height='18' fill='none' stroke='#7cf' stroke-width='2'/><path d='M4 12 H24 M9 3 V9 M19 3 V9' stroke='#7cf' stroke-width='2'/><path d='M8 18 H14 M8 22 H20' stroke='#7cf' stroke-width='2'/></svg>",
        // 11 月温度：温度计+月相环（直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L14 14 L18 14 L18 20 L10 20 L10 14 L14 14 M10 20 L18 20 M10 17 L18 17' stroke='#fa6' stroke-width='2.5' fill='none'/><circle cx='14' cy='23' r='4' fill='none' stroke='#fa6' stroke-width='2'/><path d='M14 20 A4 4 0 0 1 14 26 Z' fill='#fa6'/></svg>",
        // 12 人口：人群（三个圆头+身线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><circle cx='9' cy='7' r='3' fill='#fd8'/><circle cx='19' cy='7' r='3' fill='#fd8'/><circle cx='14' cy='15' r='3' fill='#fd8'/><path d='M9 13 L9 25 M19 13 L19 25 M14 21 L14 25' stroke='#fd8' stroke-width='2.5' stroke-linecap='round'/></svg>",
        // 13 文化：旗帜（旗杆+飘旗，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M12 3 L12 25 M12 6 L24 6 L21 11 L24 16 L12 16' fill='#fa6' stroke='#fa6' stroke-width='1.5' stroke-linejoin='miter'/></svg>",
        // 14 部落：帐篷（三角形+地面线，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 4 L24 24 L4 24 Z M8 24 L20 24 M11 13 L17 13' stroke='#8f8' stroke-width='2' fill='none' stroke-linecap='round'/></svg>",
        // 15 科技：灯泡（灯丝+底座，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><circle cx='14' cy='11' r='7' fill='none' stroke='#8f8' stroke-width='2'/><path d='M11 19 H17 M12.5 23 H15.5 M14 16 V19' stroke='#8f8' stroke-width='2' stroke-linecap='round'/></svg>",
        // 16 宗教：神庙（三角顶+立柱，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 4 L24 22 L4 22 Z M8 22 L8 26 M12 22 L12 26 M16 22 L16 26 M20 22 L20 26' stroke='#8f8' stroke-width='2' fill='none' stroke-linecap='round'/></svg>",
    };

    private static Texture2D MakeLayerIcon(int idx)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(LayerIcons[idx]);
            var img = new Image();   // LoadSvgFromBuffer 是实例方法（返回 Error）
            if (img.LoadSvgFromBuffer(bytes) != Error.Ok)
            {
                GD.PrintErr($"[MapViewer] SVG icon {idx} load failed");
                return null;
            }
            img.Resize(28, 28, Image.Interpolation.Bilinear);
            return ImageTexture.CreateFromImage(img);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[MapViewer] SVG icon {idx} failed: {e.Message}");
            return null;
        }
    }

    /// <summary>同步图层按钮的按下态（键盘/Inspector 切图层时 UI 跟随）。</summary>
    private void SyncLayerButtons()
    {
        if (_layerButtons == null)
            return;
        for (int i = 0; i < _layerButtons.Length; i++)
            _layerButtons[i].ButtonPressed = i == _layer;
    }
}
