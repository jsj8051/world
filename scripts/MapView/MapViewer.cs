using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Biome;
using World.MapGen;
using World.HexPlanet;
using World.PlanetLOD;
using World.Surface;
using World.UI;
using World.Camera;

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

    [Export] public float RadiusKm = MapArchive.DefaultRadiusKm;   // 星球半径（默认地球 6371；读档后按存档口径覆盖）

    /// <summary>覆盖层（箭头/河流等线状几何）球面浮高系数：RadiusKm×此值，防 z-fighting。
    /// 曾散落 1.01f/1.012f 四处（2026-08-19 统一——差异 0.2% 无视觉意义）。</summary>
    public const float OverlayLiftFactor = 1.01f;

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
            RebuildLegend();   // 图例跟随图层（null 保护在方法内）
            if (_monsoonArrows != null)
                _monsoonArrows.Visible = (value == 4);
            if (_currentArrows != null)
                _currentArrows.Visible = (value == 5);
            if (_riverMesh != null)
                _riverMesh.Visible = (value == 6);
            if (_monthSlider != null)
                _monthSlider.Visible = (value == 4 || value == 10 || value == 11);
            // ⚠️ 2026-08-16：季风月风场异步化后，FinishGenerate 时箭头可能还没建；
            //   切到风场/月降水/月温度图层时若已就绪则补建（ApplyMonthWind 也会补）
            if ((value == 4 || value == 10 || value == 11) && _monsoonArrows == null && _monthWind != null)
                BuildMonsoonArrows();
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
    private int[] _tileCulture;     // 每格主导文化 key 的 FNV 哈希（0=无；完整 32 位 → 每文化独立色）
    private byte[] _tileCultureGroup; // 每格主导文化群（0=无）
    private int[] _tileReligion;    // 每格主导宗教派别 key 的 FNV 哈希（0=无；relig_N 每派别独立色）
    private int[] _tileTribe;       // 每格主导部落 id（-1=无）
    private int[] _tilePower;       // 每格主导势力 id（2026-08-17：最高聚合——酋邦>部落>band；高位域标记）
    private byte[] _tilePolity;     // 每格主导势力政体类型（2026-08-17：0=独立band 1=部落 2=酋邦）
    private byte[] _tileTechEpoch;  // 每格主导部落最高技术时代 0-4
    private int[] _tileTerritory;   // 每格主导 band 的领地（语言群 key 完整哈希；0=无领地）
    private float _popLogMin, _popLogMax;   // 人口图层自适应色带端点（log 压缩 + 分位数裁剪）
    private float _popMax;                  // 驻扎格人口最大值（图例"最高"标注；0=无人口数据）
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
    private string _buildDiag = "";      // BuildAll 阶段计时（后台线程写，FinishGenerate 打印）

    // ── 进度条 UI ──
    private CanvasLayer _uiLayer;
    private PanelContainer _panel;
    private ProgressBar _bar;
    private Label _label;
    private Button[] _layerButtons;

    /// <summary>图层分类（用户拍板 2026-08：17 图层分 地理/气候/人文 三类；切换分类不改当前图层）。</summary>
    private enum LayerCat { Geo, Climate, Human }
    private static readonly LayerCat[] LayerCats =
    {
        LayerCat.Geo,       // 0 海拔
        LayerCat.Climate,   // 1 温度
        LayerCat.Climate,   // 2 降水
        LayerCat.Climate,   // 3 生物群系
        LayerCat.Climate,   // 4 风场
        LayerCat.Climate,   // 5 洋流
        LayerCat.Geo,       // 6 河流
        LayerCat.Geo,       // 7 流域
        LayerCat.Geo,       // 8 矿藏
        LayerCat.Geo,       // 9 土壤
        LayerCat.Climate,   // 10 月降水
        LayerCat.Climate,   // 11 月温度
        LayerCat.Human,     // 12 人口
        LayerCat.Human,     // 13 文化
        LayerCat.Human,     // 14 独立势力
        LayerCat.Human,     // 15 科技
        LayerCat.Human,     // 16 宗教
        LayerCat.Human,     // 17 势力范围
        LayerCat.Human,     // 18 政体
    };

    private static readonly string[] LayerNames = { "海拔", "温度", "降水", "生物群系", "风场", "洋流", "河流", "流域", "矿藏", "土壤", "月降水", "月温度", "人口", "文化", "独立势力", "科技", "宗教", "势力范围", "政体" };

    /// <summary>实体 → 势力 id（最高聚合层：酋邦>部落≥2>独立 band；高位域标记防跨域撞色）。</summary>
    private static int PowerIdOf(World.CivSim.CivEntity e)
    {
        if (e.ChiefdomId >= 0) return unchecked((int)0x80000000) | (e.ChiefdomId & 0x3FFFFFFF);
        if (e.TerritorySize >= 2) return unchecked((int)0x40000000) | (e.TerritoryId & 0x3FFFFFFF);
        // ⚠️ 2026-08-18 修复：band 也进独立域（0x20000000）——实体 Id 从 0 分配（NextEntityId 起始 0），
        //   Id=0 的起源 band 若返回原值 0，与 _tilePower==0 的"无势力"哨兵冲突 →
        //   独立势力/政体图层把它显示成灰色（无势力），人口图层却正常 → 两层冲突。
        //   域值非 0 保证与哨兵彻底隔离（部落 0x40000000 / 酋邦 0x80000000 之上再分一层）。
        return unchecked((int)0x20000000) | (e.Id & 0x3FFFFFFF);
    }

    /// <summary>实体 → 政体类型（0=独立 band 1=部落 2=酋邦）。</summary>
    private static byte PolityOf(World.CivSim.CivEntity e)
    {
        if (e.ChiefdomId >= 0) return 2;
        if (e.TerritorySize >= 2) return 1;
        return 0;
    }
    private static readonly string[] CatNames = { "地理", "气候", "人文" };
    private LayerCat _category;      // 当前分类（默认 Geo=0=地理，用户拍板）
    private Button[] _catButtons;    // 3 个分类按钮（最底下一排）
    private HBoxContainer _layerRow; // 图层按钮行容器（分类切换时重算居中）

    // ── 图例面板（月份滑块左侧，固定大小，内容超出滚动；2026-08-08）──
    private PanelContainer _legendPanel;
    private Label _legendTitle;      // 图例标题（图层名）
    private VBoxContainer _legendBox; // 图例条目容器（ScrollContainer 内）
    private VBoxContainer _legendFooter; // 图例说明文字区（滚动区外，常驻面板底部——2026-08-17 用户拍板）

    /// <summary>部落图层调色板（部落标签取色；高区分度 8 色循环——文化层已改每文化独立色，勿复用）。</summary>
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
        14 => "独立势力",
        15 => "科技",
        16 => "宗教",
        17 => "势力范围",
        18 => "政体",
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
                         $"epoch=石器时代 ticks={civResult.FinalTick} pop={civResult.Context.TotalPopulation():F0} entities={civResult.Context.Entities.Count})");
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
                int simN = Icosahedron.GridNFromVertexCount(_map.Verts.Length);
                if (simN >= 8 && simN <= 512 && simN != _gridN)
                {
                    GD.Print($"[MapViewer] 存档模拟 n={simN}（{_map.Verts.Length} 顶点）→ GridN 对齐 {simN}");
                    _gridN = simN;
                }
            }

            // 星球半径：读档口径（.mpa v5 头 / .cmp 快照；旧档默认地球 6371）。
            // 显示几何 + 相机轨道距离全 ∝ R，读档后必须应用，否则小星球按地球半径显示。
            if (Mathf.Abs(RadiusKm - _map.RadiusKm) > 1e-3f)
            {
                RadiusKm = _map.RadiusKm;
                GetNode<OrbitalCamera>("OrbitalCamera")?.SetPlanetRadius(RadiusKm);
                GD.Print($"[MapViewer] 星球半径 R={RadiusKm:F0} km（存档口径）");
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
        var _diag = new System.Text.StringBuilder();   // 阶段计时（后台累计，FinishGenerate 打印）

        // ⚠️ 2026-08-16 进度条重设计：阶段化区间（0-90% 后台构建；90-100% FinishGenerate 收尾）。
        //   每阶段有真实回调（预计算也补了），消除"卡在某 %"盲区；FinishGenerate 结束时才 100%。
        _phase = "细分二十面体";
        _progress = 0.02f;
        Icosahedron.Subdivide(GridN, RadiusKm, out var verts, out var indices);
        if (version != _buildVersion || token.IsCancellationRequested) return default;
        _progress = 0.05f;
        _diag.Append($"细分={sw.ElapsedMilliseconds}ms ");

        _phase = "构建格子拓扑 (Goldberg dual)";
        var mesh = new SubdividedMesh(verts, indices);
        var tiles = new GoldbergBuilder(mesh, RadiusKm, p => _progress = 0.05f + p * 0.10f).Tiles;
        if (version != _buildVersion || token.IsCancellationRequested) return default;
        _progress = 0.15f;
        _diag.Append($"拓扑={sw.ElapsedMilliseconds}ms ");
        // ⚠️ 后台线程禁止 GD.Print（Godot 线程不安全 → 编辑器卡死）——日志移主线程 FinishGenerate

        _phase = "构建几何";
        Func<Vector3, float> elevAt = _ => 0f;
        var geometry = ChunkMeshBuilder.BuildGeometry(tiles, elevAt, RadiusKm, 0f,
            p => _progress = 0.15f + p * 0.20f);
        if (version != _buildVersion || token.IsCancellationRequested) return default;
        _progress = 0.35f;
        _diag.Append($"几何={sw.ElapsedMilliseconds}ms ");

        // 几何就绪 → 缓存（图层切换直接复用），再算颜色
        _tiles = tiles;
        _geometry = geometry;
        _geometryReady = true;

        // 预计算每格图层值（v3 球面一次采样；切图层 O(1) 查表）
        _phase = "预计算图层值";
        PrecomputeTileValues(map, tiles, token, p => _progress = 0.35f + p * 0.30f);
        if (version != _buildVersion || token.IsCancellationRequested) return default;
        _progress = 0.65f;
        _diag.Append($"预计算={sw.ElapsedMilliseconds}ms ");

        _phase = "采样并着色";
        var colors = ChunkMeshBuilder.BuildColors(tiles, MakeColorFn(layer), geometry,
            p =>
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                _progress = 0.65f + p * 0.25f;
            });
        _progress = 0.90f;
        _diag.Append($"着色={sw.ElapsedMilliseconds}ms 总计={sw.ElapsedMilliseconds}ms");
        _buildDiag = _diag.ToString();
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
    ///   GridN=256 = 65 万格 × 4 次采样，串行 ~25s，并行 ~5s。
    /// ⚠️ 2026-08-16：补进度回调——之前无回调是进度条 80% 盲区（看似卡住的元凶之一）。</summary>
    private void PrecomputeTileValues(MapData map, List<HexTile> tiles, System.Threading.CancellationToken token, Action<float> progress = null)
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
        _tileCulture = new int[n];
        _tileCultureGroup = new byte[n];
        _tileReligion = new int[n];
        _tileTribe = new int[n];
        _tilePower = new int[n];     // 独立势力 id（2026-08-17；0=无）
        _tilePolity = new byte[n];   // 政体类型（2026-08-17；0=band 1=部落 2=酋邦）
        _tileTechEpoch = new byte[n];
        _tileTerritory = new int[n];
        System.Array.Fill(_tileTribe, -1);
        bool hasCiv = _civCtx != null;
        // 2026-08-10 影响力场模型（v8）：band 实体只在驻扎点格，领地=归属格——文明图层改为
        // **归属格主导**（每格查 CellOwner → 该 band 的文化/宗教/部落/科技；人口=领地均摊，5 km² 量级）
        var civIdMap = new System.Collections.Generic.Dictionary<int, World.CivSim.CivEntity>();
        if (hasCiv)
            foreach (var ce in _civCtx.Entities)
                if (!ce.Dead) civIdMap[ce.Id] = ce;
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
        int done = 0;
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
            // 文明图层（.cmp v8 影响力场模型：归属格主导——每格查 CellOwner，领地全显示）
            if (hasCiv)
            {
                // ⚠️ 2026-08-18 最底层索引修复（用户点击验证：可达距离 2 跳 vs pos 差 63km）：
                //   显示格（Goldberg 面）编号 ≠ 逻辑格（Icosahedron 顶点）编号——两套排序不同！
                //   实测：逻辑 Verts[6376]↔[8393] DistKm=4.0（2 跳正确）但显示 Center 差 63.4km。
                //   修复：所有逻辑数据按 vid（_tileVerts[i]——面 i 的最近顶点=该面的逻辑格）查——
                //   显示位置与逻辑位置一致（旧版 CellOwner[i] 用面编号查顶点数组→63km 错位）。
                int vid2 = _tileVerts[i];
                int ownerId = _civCtx.CellOwner != null ? _civCtx.CellOwner[vid2] : -1;
                if (ownerId >= 0 && civIdMap.TryGetValue(ownerId, out var dom))
                {
                    // ⚠️ 2026-08-17：人口图层不在这里写——领地格 = 采集格（无常住人口），
                    //   人口只在驻扎格（CivEntity.Cell）显示该 band 的 P（并行循环后实体表直写）。
                    //   旧"领地均摊 P/领地格数"让每个归属格都有人口，与"大部分是采集格"矛盾（用户反馈）。
                    _tileCulture[i] = World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(dom.CultureShare));
                    _tileCultureGroup[i] = (byte)(World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(dom.CultureGroupShare)) & 0xFF);
                    // ⚠️ 2026-08-07 宗教图层改显示"具体派别"（relig_N，每摇篮/每次漂变独立）——
                    //    旧版显示 5 段发展带（万物有灵→一神教），石器时代全在段 0 → 全图一色（用户反馈）
                    _tileReligion[i] = World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(dom.ReligionCultShare));
                    _tileTribe[i] = dom.Id;
                    _tileTechEpoch[i] = (byte)dom.Epoch;   // 0=旧石器 1=新石器（反应性标签）
                    // 独立势力（2026-08-18 v4 回影响力场——用户确认：按影响力算难出飞地、
                    //   且不可能被中立隔离（中立只在圈外——同势力格都在圈内）。v3 的 BFS 2 跳
                    //   人为切圈是魔法数字——废弃。飞地=强邻切通道的少数构型——统计验证）
                    _tilePower[i] = PowerIdOf(dom);
                    _tilePolity[i] = PolityOf(dom);
                    // 势力范围：主导 band 的语言群 key 完整 32 位哈希（同领地必同语言群 → 同领地同色；
                    //    与 byte 截断的 _tileCultureGroup 区分，防 8 位撞色）
                    _tileTerritory[i] = World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(dom.CultureGroupShare));
                }
            }
            // ⚠️ 2026-08-16：每 64 格报一次进度（并行 For 内；不调取消——检查在外部）
            if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
                progress(done / (float)n);
        });
        progress?.Invoke(1f);
        // ⚠️ 2026-08-17：人口 = 驻扎格人口（实体表直写，每 band 只点亮 1 格）。
        //   并行循环按归属格走无法区分驻扎格（冲突边缘：驻扎格归属可能与驻扎者不同），
        //   实体表最直接——领地格（采集格）保持 0 = 无人。
        //   同格共住合法（分裂/驱逐瞬态：模拟无格容量上限）→ 求和 = 该格真实总人口。
        if (hasCiv)
        {
            // ⚠️ 2026-08-17 语义修正 v4（用户拍板）：人口格子就显示有人口的格子——
            //   人口 = 驻扎格 P（营地实有人口）；领地格（采集格）无人即无人（物理正确——
            //   采集者活动范围≠定居点）。不做活动人口分散（v3 被否：所有领地格造淡人口不真实）。
            //   势力色块 = CellOwner 影响力场（v4——用户确认：按影响力算难出飞地、中立只在圈外）。
            //   保留 v2 修复：驻扎格归势力（弱 band 驻扎格被强邻覆盖时显示自己势力——人口格必有势力色）。
            // ⚠️ 2026-08-18 索引修复：顶点→显示面反查表（_tileVerts[j] 是面 j 的逻辑格——
            //   bestByCell 的 ce.Cell 是顶点编号——写显示格必须经反查）
            var facesOf = new System.Collections.Generic.List<int>[n];
            for (int j = 0; j < n; j++)
            {
                int vj = _tileVerts[j];
                if (facesOf[vj] == null) facesOf[vj] = new System.Collections.Generic.List<int>(2);
                facesOf[vj].Add(j);
            }
            var bestByCell = new Dictionary<int, World.CivSim.CivEntity>();
            for (int e = 0; e < _civCtx.Entities.Count; e++)
            {
                var ce = _civCtx.Entities[e];
                if (ce.Dead || ce.Cell < 0 || ce.Cell >= n) continue;
                if (_civCtx.R != null && _civCtx.R[ce.Cell] <= 0f) continue;   // 逻辑陆地
                if (!bestByCell.TryGetValue(ce.Cell, out var cur) || ce.P > cur.P) bestByCell[ce.Cell] = ce;
            }
            foreach (var kv in bestByCell)
            {
                // ⚠️ 2026-08-18 索引修复：ce.Cell 是逻辑格（顶点）编号——显示格是面编号（不同序）！
                //   _tilePower[kv.Key]（顶点编号当显示格索引）→ 显示错位 63km（3177 显示别处顶点的势力）。
                //   通过顶点→面反查表写全部映射面（驻扎格显示在正确位置）。
                if (facesOf[kv.Key] == null) continue;
                int powB = PowerIdOf(kv.Value);
                byte polB = PolityOf(kv.Value);
                foreach (var f in facesOf[kv.Key]) { _tilePower[f] = powB; _tilePolity[f] = polB; }
            }
            for (int e = 0; e < _civCtx.Entities.Count; e++)
            {
                var ce = _civCtx.Entities[e];
                if (ce.Dead || ce.Cell < 0 || ce.Cell >= n) continue;
                if (_civCtx.R != null && _civCtx.R[ce.Cell] <= 0f) continue;   // 逻辑陆地（与模拟一致）
                _tilePop[ce.Cell] += ce.P;   // 驻扎格实有人口（营地）——只有人的格显示人
            }
            }
        // 人口图层自适应归一化（相对本图分布——分位数模型，用户拍板风格）：
        // log(p+1) 压缩重尾 + 有人陆地格 P1/P99 分位为色带端点 → 单格超大城市不拉爆、
        // 最小聚落也有可见色（旧版 log(全局max) 归一：全球最大值单点把其余全压成近黑色）
        var popLog = new System.Collections.Generic.List<float>();
        _popMax = 0f;
        for (int i = 0; i < n; i++)
        {
            if (_tilePop[_tileVerts[i]] <= 0f) continue;     // 无人格不入带（人口按顶点写——显示格 i 读其顶点）
            popLog.Add(Mathf.Log(_tilePop[_tileVerts[i]] + 1f));
            if (_tilePop[_tileVerts[i]] > _popMax) _popMax = _tilePop[_tileVerts[i]];
        }
        if (popLog.Count >= 2)
        {
            popLog.Sort();
            int p1 = popLog.Count / 100;
            int p99 = popLog.Count - 1 - popLog.Count / 100;
            _popLogMin = popLog[p1];
            _popLogMax = Mathf.Max(_popLogMin + 1f, popLog[p99]);   // 防退化（全同值）
        }
        else { _popLogMin = 0f; _popLogMax = 1f; }   // 无人口数据 → 全图灰
        // ⚠️ 2026-08-16：年降水自适应色带 min/max（用户拍板：最低到最高归一化，不用固定 2000mm）
        _precipMin = float.MaxValue;
        _precipMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (IsDisplaySea(i)) continue;   // ⚠️ 2026-08-17：统一海陆判定（只统计陆地格）
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
    					bool ocean = IsDisplaySea(id);   // ⚠️ 2026-08-17：统一海陆判定（近海逻辑陆地=陆地）
    					return ocean ? SeaColor : new Color(0.72f, 0.68f, 0.55f);
    				}
    			case 7: // 流域：每流域独立颜色（黄金角）；海洋浅蓝、边缘排水区灰绿
    				{
    					int ws = _tileWatershed[id];
    					if (ws < 0)
    						return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    							? SeaColor   // 海洋
    							: new Color(0.60f, 0.58f, 0.50f);  // 边缘排水区（直接入海，非河）
    										return HslToRgb(GoldenHue(ws), 0.55f, 0.62f);
    				}
    			case 8: // 矿藏：矿种固定色 × 富度明度（贫暗/富中/巨型亮）；无矿淡地形底
    				{
    					byte m = _tileMineral[id];
    					if (m == 0)
    					{
    						return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    							? SeaColor
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
    			return SeaColor;   // 海洋
    			return SoilColors[Mathf.Clamp(s, 1, 5)];
    			}
    																			case 10: // 月降水：和总降水同一自适应色带（当月陆地 min-max 归一化；月份滑块切换）
    																				{   // ⚠️ 2026-08-16 v3（用户拍板）：与总降水同色带同统计方式；×12 换算回年尺度
    																					//   → 非季风区≈年降水色，季风区 7 月深蓝 / 1 月枯黄；min-max 自适应当月分布
    																					if (_tileMonthPrecip == null || _map == null || _map.MonthPrecip == null)
    																						return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    																							? SeaColor
    																							: new Color(0.72f, 0.70f, 0.58f);
    																					if (IsDisplaySea(id)) return SeaColor;
    																					float mm = _tileMonthPrecip[id] / 255f * _tilePrecip[id] * 12f;   // 等效年尺度
    																					float x = Mathf.Clamp((mm - _monthPrecipMin) / (_monthPrecipMax - _monthPrecipMin), 0f, 1f);
    																					return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    																				}
    						case 11: // 月温度：当月均温色块（MonthTemp −60~60°C→0-255；月份滑块切换）
    							{
    								if (_tileMonthTemp == null || _map == null || _map.MonthTemp == null)
    									return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    										? SeaColor
    										: new Color(0.72f, 0.70f, 0.58f);
    								float tC = _tileMonthTemp[id] / 255f * 120f - 60f;   // byte → °C
    								return BiomeColors.TemperatureToColor(tC);
    								}
    								case 12: // 人口：log 压缩 + P1/P99 分位自适应色带（无人=暗灰；黄→橙红）
    								{
    								    if (IsDisplaySea(id) && _tilePop[_tileVerts[id]] <= 0f) return SeaColor;   // ⚠️ 显示海（真海）；近海逻辑陆地=陆地底
    								    float p = _tilePop[_tileVerts[id]];
    								    if (p <= 0f) return new Color(0.25f, 0.25f, 0.28f);   // 无人陆地
    								    float x = Mathf.Clamp((Mathf.Log(p + 1f) - _popLogMin) / (_popLogMax - _popLogMin), 0f, 1f);
    								    return new Color(0.95f, 0.75f, 0.25f).Lerp(new Color(0.80f, 0.15f, 0.05f), x);
    								}
    								case 13: // 文化：每文化独立颜色（key FNV 哈希 → 黄金角 HSL；无 8 色取模上限）
    								{
    								    if (IsDisplaySea(id) && _tileCulture[id] == 0) return SeaColor;
    								    int cult = _tileCulture[id];
    								    if (cult == 0) return new Color(0.25f, 0.25f, 0.28f);
    								    							    return HslToRgb(GoldenHue(cult), 0.55f, 0.62f);
    								}
    								case 14: // 独立势力（2026-08-17）：每势力独立色（黄金角 HSL）——
    								    //   最高聚合层显示：酋邦（跨部落联盟）> 部落（领地≥2）> 独立 band
    								    {
    								        if (IsDisplaySea(id) && _tilePower[id] == 0) return SeaColor;
    								        int powerId = _tilePower[id];
    								        if (powerId == 0) return new Color(0.25f, 0.25f, 0.28f);
    								        return HslToRgb(AvoidSeaHue(GoldenHue(powerId)), 0.55f, 0.62f);   // ⚠️ 避开海蓝（用户要求区分）
    								    }
    								    case 15: // 科技：主导部落最高技术时代色带（石器棕→新石器绿→青铜橙→铁器蓝→古典紫）
    								    {
    								        if (IsDisplaySea(id) && _tileTribe[id] < 0) return SeaColor;
    								        if (_tileTribe[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
    								        byte ep = _tileTechEpoch[id];
    								        if (ep == 0) return new Color(0.55f, 0.42f, 0.28f);   // 石器：棕（有基础技术，非"无"）
    								        return TechEpochColors[Mathf.Clamp(ep - 1, 0, TechEpochColors.Length - 1)];
    								    }
    								case 16: // 宗教：每宗教派别独立颜色（relig_N key 哈希 → 黄金角 HSL；不再按 5 阶段色带）
    								{
    								    if (IsDisplaySea(id) && _tileTribe[id] < 0) return SeaColor;
    								    if (_tileTribe[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
    								    int rel = _tileReligion[id];
    								    if (rel == 0) return new Color(0.25f, 0.25f, 0.28f);
    								    							    return HslToRgb(GoldenHue(rel), 0.55f, 0.62f);
    								}
    								case 17: // 势力范围：每领地独立色（语言群 key 完整哈希 → 黄金角 HSL；无领地/无人灰）
    								{
    								    if (IsDisplaySea(id) && _tileTerritory[id] == 0) return SeaColor;
    								    int terr = _tileTerritory[id];
    								    // ⚠️ 2026-08-17：领地按归属显示全领地（不能再用人口判"无人"——
    								    //   人口图层已改只在驻扎格显示，采集格人口=0）
    								    if (terr == 0) return new Color(0.30f, 0.32f, 0.36f);
    								    return HslToRgb(GoldenHue(terr), 0.55f, 0.85f);
    								}
    								case 18: // 政体（2026-08-17）：独立势力基础上按政体类型分色——
    								    //   band=灰蓝 部落=绿 酋邦=红橙——纯政体色（2026-08-18 用户：部落为何多色——
    								    //   去掉势力微扰——政体地图=政体类型色，势力区分看独立势力图层 14）
    								    {
    								        if (IsDisplaySea(id) && _tilePower[id] == 0) return SeaColor;
    								        int powerId = _tilePower[id];
    								        if (powerId == 0) return new Color(0.25f, 0.25f, 0.28f);
    								        float hue = _tilePolity[id] switch
    								        {
    								            2 => 0.045f,   // 酋邦：红橙
    								            1 => 0.35f,    // 部落：绿
    								            _ => 0.60f,    // band：灰蓝
    								        };
    								        return HslToRgb(hue, 0.45f, 0.55f);   // 无微扰——同类纯色
    								        }
    								        default: // 海拔（2026-08-18 用户拍板）：按实际米分色——
    								            //   海：<-200m 深海（深蓝）/ -200~0m 浅海（亮蓝——大陆架 200m 等深线）
    								            //   陆：连续色带（0m→最高——沙→绿→棕→白——无分段）
    								        {
    								        	float h = _tileElev[id];
    								        	int vidE = _tileVerts != null ? _tileVerts[id] : id;
    								        	float elevM = _map.Elev != null ? _map.Elev[vidE] : (h - _hSea) * (_map.MaxElev - _map.MinElev);   // 米（0=海平面）
    								        	if (IsDisplaySea(id))
    								        	{
    								        		// ⚠️ 2026-08-18 海冰（用户：两极应该冰盖不是海洋）：温度 ≤-5°C 的海 = 海冰（极地冰盖——白）。
    								        		//   注意：此为【显示层】海冰判据（-5°C，地形定案 08-18），不同于 BiomeClassifier.SeaIceTempC（-2°C，柯本 FrigidOcean 分类）——两者语义不同，勿合并。
    								        		float seaTemp = _map.Temp != null ? _map.Temp[vidE] : 15f;
    								        		if (seaTemp <= -5f) return new Color(0.92f, 0.95f, 1.00f);   // 海冰（白——极地冰盖）
    								        		if (elevM < -200f) return new Color(0.01f, 0.05f, 0.18f);   // 深海 <-200m
    								        		return new Color(0.20f, 0.45f, 0.68f);                      // 浅海 -200~0m（大陆架）
    								        	}
    								        	// 陆地：海拔色带（沙/绿/棕按米）——雪（白）由实际温度驱动（2026-08-18 用户：雪线按实际温度）
								        	//   0°C 以下全白（雪线=0°C 等温线——纬度/气候决定——非固定 3300m）；0~2°C 渐变
								        	float tempC = _map.Temp != null ? _map.Temp[vidE] : 15f;
								        	Color baseC;
								        	if (elevM <= 0f) baseC = new Color(0.76f, 0.70f, 0.50f);
								        	else if (elevM < 100f) baseC = new Color(0.76f, 0.70f, 0.50f).Lerp(new Color(0.30f, 0.65f, 0.10f), elevM / 100f);
								        	else if (elevM < 800f) baseC = new Color(0.30f, 0.65f, 0.10f).Lerp(new Color(0.60f, 0.50f, 0.35f), (elevM - 100f) / 700f);
								        	else baseC = new Color(0.60f, 0.50f, 0.35f);
								        	float snowT = Mathf.Clamp(1f - tempC / 2f, 0f, 1f);   // ≤0°C 全白；0~2°C 渐变；>2°C 无雪
								        	return baseC.Lerp(new Color(0.95f, 0.97f, 1.00f), snowT);
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

    /// <summary>主线程：把后台构建好的数据包成 ArrayMesh 并挂载。
    /// ⚠️ 2026-08-16 进度条重设计：BuildAll 到 90%，这里收尾到 100%（100% = 真正完成，
    ///   消除"进度满但主线程还有活"的未响应感）。</summary>
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
            _phase = "创建网格";
            _progress = 0.90f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var mi = new MeshInstance3D
            {
                Mesh = ChunkMeshBuilder.CreateMesh(data),
                MaterialOverride = new ShaderMaterial { Shader = shader }
            };
            AddChild(mi);
            _planetMesh = mi;
            GD.Print($"[MapViewer] sphere ready: {data.Indices.Length / 3} tris (tiles={_tiles?.Count ?? 0}) (CreateMesh {sw.ElapsedMilliseconds}ms)");
            GD.Print($"[MapViewer] BuildAll 阶段耗时: {_buildDiag}");

            // 统一风场箭头网格（图层 4 显示；热成风：信风/西风/季风一体，月份滑块切换）
            // ⚠️ 2026-08-16：EnsureMonthWind 已异步化——此调用只触发后台计算，不阻塞主线程
            _phase = "构建季风风场（后台）";
            _progress = 0.94f;
            sw.Restart();
            BuildMonsoonArrows();
            GD.Print($"[MapViewer] 收尾: 季风箭头 {sw.ElapsedMilliseconds}ms");
            // 月降水缓存（图层 11 显示；当前月）
            RefreshMonthPrecip();
            // 月温度缓存（图层 12 显示；当前月）
            RefreshMonthTemp();
            GD.Print($"[MapViewer] 收尾: 月缓存 {sw.ElapsedMilliseconds}ms");
            // 洋流箭头网格（图层 5 显示；暖流红/寒流蓝）
            _phase = "构建洋流箭头";
            _progress = 0.97f;
            sw.Restart();
            BuildCurrentArrows();
            GD.Print($"[MapViewer] 收尾: 洋流箭头 {sw.ElapsedMilliseconds}ms");
            // 河流网格（图层 6 显示；每条河独立颜色，支流汇合截断）
            _phase = "构建河流";
            _progress = 0.99f;
            sw.Restart();
            BuildRivers();
            GD.Print($"[MapViewer] 收尾耗时: 河流 {sw.ElapsedMilliseconds}ms");

            // 构建中切了图层 → 自动应用最新图层（几何已就绪，走快速重算）
            if (_pendingRecolor)
            {
                _pendingRecolor = false;
                RebuildColors();
            }
            _progress = 1f;
            _phase = "完成";
            // 图例动态条目（文化/宗教）在 BuildAll 后才就绪 → 生成完成补刷一次
            RebuildLegend();
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
    /// 用存档的海陆/年温/年降水 + 倾角（v3.8 头部）现场跑 MonsoonSystem。
    /// ⚠️ 2026-08-16：异步化（后台 Task.Run）——n=128 时 MonsoonSystem 数亿次主线程计算
    ///   让 FinishGenerate 卡 100% 几十秒。MonsoonSystem 是纯计算（不碰引擎 API，线程安全）；
    ///   完成后 CallDeferred 回主线程应用。</summary>
    private bool _monthWindStarted;                 // 防重复启动
    private volatile Vector3[][] _monthWindPending; // 后台写、主线程 ApplyMonthWind 读
    private void EnsureMonthWind()
    {
        if (_monthWind != null || _monthWindStarted || _map == null || _map.Verts == null) return;
        _monthWindStarted = true;
        var map = _map;   // 快照引用（后台线程只读字段，主线程不再改 _map）
        System.Threading.Tasks.Task.Run(() =>
        {
            var nb = map.BuildNeighbors();
            if (nb == null) return;
            int n = map.Verts.Length;
            float span = Mathf.Max(-map.MinElev, map.MaxElev);
            var eNorm = new float[n];
            for (int i = 0; i < n; i++)
                eNorm[i] = span > 1e-6f ? map.Elev[i] / span : 0f;
            MonsoonSystem.Compute(map.Verts, nb, eNorm, map.Elev, map.Temp, map.Precip, map.AxialTilt, map.RotationSpeed,
                new ClimateGenerator(map.Seed, map.AxialTilt, 1f),
                out var mons, out _, out _, out _, out _, out _, out var mw, out var mt, out _,
                radiusKm: map.RadiusKm);
            _monthWindPending = mw;   // 后台线程写字段，主线程 CallDeferred 后读
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] 季风月风场计算失败: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(ApplyMonthWind));   // 回主线程应用（含失败路径清 pending）
        });
    }

    private void ApplyMonthWind()
    {
        var mw = _monthWindPending;
        _monthWindPending = null;
        if (mw == null) return;
        _monthWind = mw;
        GD.Print($"[MapViewer] 季风月风场重算完成（{_map?.Verts.Length} 顶点，倾角 {_map?.AxialTilt}°）");
        // 若当前已是风场/月降水/月温度图层，补建箭头（异步完成前可能已跳过）
        if (Layer == 4 || Layer == 10 || Layer == 11)
            BuildMonsoonArrows();
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
        float radius = RadiusKm * OverlayLiftFactor;   // 浮在球面上方防 z-fighting

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
            if (IsDisplaySea(i)) continue;   // ⚠️ 2026-08-17：统一海陆判定（只统计陆地格）
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

        // v4 档：流函数 psi 在 → 水位法提取"每环最外圈"（用户拍板形态：环状洋流每环一条外圈，
        // 弱流也显示，不按强度筛选）；旧档（无 psi）回退下方稀疏箭头。
        if (_map.Psi != null)
        {
            BuildCurrentRingsFromPsi();
            return;
        }

        float radius = RadiusKm * OverlayLiftFactor;

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

    // ── 洋流"每环最外圈"（水位法；v4 存档 psi；用户拍板形态——环状洋流每环一条外圈，
    //    弱流也显示，不按强度筛选）──
    //    原理：ψ 局部极值 = 环流中心；从极值逐层扩张（水位下降/上升），区域边界 = ψ 等值线；
    //    区域扩张到贴大陆前最后一层边界 = 该环流圈最外圈。画边界格箭头（方向=CurrentDirs）。
    private void BuildCurrentRingsFromPsi()
    {
        float radius = RadiusKm * OverlayLiftFactor;
        var verts = new System.Collections.Generic.List<Vector3>();
        var colors = new System.Collections.Generic.List<Color>();
        var indices = new System.Collections.Generic.List<int>();

        var psi = _map.Psi;
        int n = psi.Length;
        var eNorm = new float[n];
        float range = Mathf.Max(-_map.MinElev, _map.MaxElev);
        for (int i = 0; i < n; i++) eNorm[i] = range > 1e-6f ? _map.Elev[i] / range : 0f;
        var dirs = _map.CurrentDirs;
        var nbsAll = _map.BuildNeighbors();   // 现场重建邻接（存档不存拓扑）

        // 1. ψ 局部极值点（海洋格 = 环流中心）
        var seeds = new System.Collections.Generic.List<int>();
        for (int i = 0; i < n; i++)
        {
            if (eNorm[i] >= 0f) continue;
            var nbs = nbsAll[i];
            if (nbs == null || nbs.Length < 3) continue;
            bool isMax = true, isMin = true;
            foreach (var nb in nbs)
            {
                if (psi[nb] > psi[i]) isMax = false;
                if (psi[nb] < psi[i]) isMin = false;
            }
            if (isMax || isMin) seeds.Add(i);
        }

        // 2. 水位法：极值 → 逐层扩张 → 贴大陆前最后一层边界 = 最外圈
        // ⚠️ 2026-08-16 性能修复：n=128 时 seeds 数千、每层 Array.Clear(seen,0,n)+全扫 consumed
        //   = O(seeds×30×n) 千亿次 → 卡 361 秒。改为：
        //   · seen 用 int stamp（每层 stamp++ 即"清空"，O(1) 而非 O(n)）
        //   · consumed 只标记本层 BFS 访问过的格（O(区域) 而非 O(n)）
        //   · seeds 按 |psi| 降序（强环流先占区域，弱极值快速被跳过）
        //   行为不变（环数/箭头数与 02:47 版本一致）。
        var consumed = new bool[n];
        int ringCount = 0, arrowTotal = 0;
        var queue = new System.Collections.Generic.Queue<int>();
        var seenStamp = new int[n];
        int stamp = 0;
        var regionCells = new System.Collections.Generic.List<int>();
        const int layers = 30;
        // seeds 按 |psi| 从大到小：强环流中心先处理 → 弱极值通常已被 consumed 跳过
        seeds.Sort((a, b) => Mathf.Abs(psi[b]).CompareTo(Mathf.Abs(psi[a])));
        foreach (var seed in seeds)
        {
            if (consumed[seed]) continue;
            bool isMax = true;
            foreach (var nb in nbsAll[seed]) if (psi[nb] > psi[seed]) { isMax = false; break; }
            float level0 = psi[seed];
            float step = (level0 - 0f) / layers;   // 极大值降向 0 / 极小值升向 0

            var boundary = new System.Collections.Generic.List<int>();
            var lastBoundary = new System.Collections.Generic.List<int>();
            for (int l = 1; l <= layers; l++)
            {
                float level = isMax ? level0 - step * l : level0 + step * l;
                // BFS 连通区：ψ 满足（极大 ≥ level / 极小 ≤ level）的海洋格
                queue.Clear();
                stamp++;
                regionCells.Clear();
                queue.Enqueue(seed); seenStamp[seed] = stamp;
                int regionCount = 0;
                bool touchesLand = false;
                boundary.Clear();
                while (queue.Count > 0)
                {
                    int c = queue.Dequeue();
                    regionCount++;
                    regionCells.Add(c);
                    bool onBoundary = false;
                    foreach (var nb in nbsAll[c])
                    {
                        if (eNorm[nb] >= 0f) { touchesLand = true; continue; }   // 邻接陆地
                        bool inR = isMax ? psi[nb] >= level : psi[nb] <= level;
                        if (inR)
                        {
                            if (seenStamp[nb] != stamp) { seenStamp[nb] = stamp; queue.Enqueue(nb); }
                        }
                        else onBoundary = true;   // 邻接区域外海洋 = 等值线边界
                    }
                    if (onBoundary) boundary.Add(c);
                }
                if (regionCount == 0) break;
                if (touchesLand) { boundary = lastBoundary; break; }   // 贴岸 → 最外圈 = 上一层
                lastBoundary.Clear();
                lastBoundary.AddRange(boundary);
                foreach (var ci in regionCells) consumed[ci] = true;   // 标记本环区域（只扫访问过的格）
                if (boundary.Count == 0) break;   // 区域填满整个海洋盆（无闭合等值线）
            }
            if (boundary.Count >= 8)
            {
                int drawn = 0;
                const float arrowLen = 0.028f, arrowTailW = 0.012f;
                foreach (var c in boundary)
                {
                    var d = dirs[c];
                    if (d.LengthSquared() < 1e-9f) continue;
                    var pos = _map.Verts[c];
                    var wDir = d.Normalized();
                    var side = pos.Cross(wDir).Normalized();
                    Vector3 tailC = pos - wDir * arrowLen * 0.35f;
                    Vector3 tip = pos + wDir * arrowLen * 0.65f;
                    Vector3 t1 = (tailC + side * arrowTailW).Normalized() * radius;
                    Vector3 t2 = (tailC - side * arrowTailW).Normalized() * radius;
                    Vector3 tipS = tip.Normalized() * radius;
                    float w = _map.CurrentWarmth[c];
                    Color col = w > 0.05f ? new Color(1f, 0.45f, 0.2f)
                        : w < -0.05f ? new Color(0.25f, 0.55f, 1f)
                        : new Color(0.85f, 0.85f, 0.85f);
                    int ai = verts.Count;
                    verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                    colors.Add(col); colors.Add(col); colors.Add(col);
                    indices.Add(ai); indices.Add(ai + 1); indices.Add(ai + 2);
                    drawn++;
                }
                if (drawn > 0) { ringCount++; arrowTotal += drawn; }
            }
        }

        if (verts.Count == 0)
        {
            GD.Print("[MapViewer] current rings: 无闭合环流圈（psi 水位法）");
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
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
        GD.Print($"[MapViewer] 洋流环：最外圈 {ringCount} 个，箭头 {arrowTotal}（水位法，v4 psi）");
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

        float radius = RadiusKm * OverlayLiftFactor;   // 略高于球面，避免 z-fighting
        var vertList = new System.Collections.Generic.List<Vector3>();
        var colorList = new System.Collections.Generic.List<Color>();
        var indexList = new System.Collections.Generic.List<int>();

        // 主河先画（长→短），支流遇已画顶点截断（汇合点）
        var painted = new System.Collections.Generic.HashSet<int>();
        paths.Sort((a, b) => b.Length.CompareTo(a.Length));
        // ⚠️ 2026-08-06：河宽按分辨率缩放——固定 halfW 在 n=128 格距减半时相对粗 2 倍。
        //   统一按格距比例：halfW = 格距 × 0.13（n=64 时即原 0.004）
        int simN = Icosahedron.GridNFromVertexCount(n);
        float gridArc = Mathf.Tau / (Mathf.Sqrt(10f) * Mathf.Max(8, simN));
        float halfW = gridArc * 0.13f;   // 河宽 ≈ 0.26 格距（观感统一，随分辨率缩放）
        int riverCount = 0;
        foreach (var path in paths)
        {
            // 每条河独立颜色：HSL 色相黄金角循环（相邻河差异最大）
            float hue = GoldenHue(riverCount);
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

    /// <summary>海洋统一色（2026-08-18 用户要求与势力色区分）：深蓝——明确海，
    /// 与势力色（亮色/避开蓝相）一眼可分。各图层判海返回统一用此色。</summary>
    private static readonly Color SeaColor = new Color(0.10f, 0.22f, 0.48f);

    /// <summary>势力色避开海洋蓝（2026-08-18 用户要求）：蓝-青相区间（0.48-0.72）映射到
    /// 暖色/绿黄——势力色块与海色不撞（黄金角散列原可能出亮蓝——7401 #69a6d3 与海混淆）。</summary>
    static float AvoidSeaHue(float hue)
    {
        if (hue >= 0.48f && hue <= 0.72f)
            return hue < 0.60f ? hue + 0.35f : hue - 0.35f;   // 0.48-0.60→0.83-0.95（紫红）; 0.60-0.72→0.25-0.37（绿黄）
        return hue;
    }

    /// <summary>整数 key/流域 id → 黄金角色相（double 计算防 float 精度坍缩——int 32 位 × float 在 2^31 量级
    /// 只剩 22 档色相，不同 key 同色；double 52 位尾数全展开 360 档，2026-08-07）。</summary>
    static float GoldenHue(long id)
    {
        return (float)((id * 0.6180339887498949) % 1.0);
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
            // ⚠️ 2026-08-16：去掉 ShowPercentage——内嵌百分比与 _label 双显示取整不一致
            //   （用户见 89/88 两个数字）。只保留条，数字统一由 _label 显示。
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(420, 26)
        };
        vbox.AddChild(_bar);

        _panel.Visible = false;

        // ── 分类按钮行（最底下一排：地理/气候/人文）──
        // 2026-08 用户拍板：17 图层分三类；点分类只切换上方子按钮显示，不改变当前图层。
        var catGroup = new ButtonGroup();
        var catBox = new HBoxContainer();
        catBox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom); // 锚点底部居中
        catBox.Position = new Vector2(-128, -44);   // 3×80px + 8px 间距居中
        catBox.AddThemeConstantOverride("separation", 8);
        _uiLayer.AddChild(catBox);

        _catButtons = new Button[CatNames.Length];
        for (int i = 0; i < CatNames.Length; i++)
        {
            int cat = i; // 闭包捕获
            var btn = new Button
            {
                Text = CatNames[i],
                ToggleMode = true,
                ButtonGroup = catGroup,
                CustomMinimumSize = new Vector2(80, 32),
            };
            btn.Pressed += () =>
            {
                _category = (LayerCat)cat;
                ShowCategoryButtons();   // 只切显示，不改 _layer（用户拍板）
                GD.Print($"[MapViewer] category={CatNames[cat]} layer仍={LayerName(_layer)}");
            };
            catBox.AddChild(btn);
            _catButtons[i] = btn;
        }

        // ── 图层按钮行（分类按钮上方；只显示当前分类的子图层，其余隐藏）──
        // ⚠️ 2026-08-02 v3：SVG 图标按钮。只显示 4 个的真相 = 后 3 个 SVG 用了
        //   Q/T/A 曲线命令，thorvg 解析器不支持 → 加载失败空白（非宽度问题）。
        //   全部图标已重写为纯直线命令 M/L/H/V/Z。悬停 TooltipText 显示中文名。
        var group = new ButtonGroup();
        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom); // 锚点底部居中
        hbox.Position = new Vector2(-21f * LayerNames.Length, -84); // 占位，SyncLayerButtons 重算
        _uiLayer.AddChild(hbox);
        _layerRow = hbox;

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

        // ── 月份滑块（右下角；图层 10/11 显示；1-12 月切换季风箭头/月降水）──
        var monthRow = new HBoxContainer();
        monthRow.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        monthRow.Position = new Vector2(-300, -44);   // 右下角（用户拍板 2026-08）
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

        // ── 图例面板（月份滑块左侧，固定大小，内容超出滚动）──
        // 2026-08-08：图例 = 当前图层颜色说明；放右下角月份滑块左边。
        // 固定大小 236×320；ScrollContainer 垂直滚动（生物群系 22 条目/文化动态条目必然超界）。
        _legendPanel = new PanelContainer();
        _legendPanel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        // ⚠️ 必须在 AddChild 前设 Position（入树后 Position setter 会用父尺寸反推 offset → 屏幕外）
        // 2026-08-17 修复链：BottomRight 锚点下 Position 的 y 是【顶缘】偏移——必须 = -(底边距+面板高)。
        //   ① 原 (-560,-52) 顶缘在底上 52px → 面板 268px 裁出屏幕（用户报"图例太矮"）；
        //   ② 加高 500 → 用户嫌高 → 缩半 250（内容滚动）；
        //   ③ 用户要求锚定到底部 → 底边距 0，完全贴屏幕底（滑块在右侧水平分离不冲突）。
        _legendPanel.Position = new Vector2(-560, -250);
        _legendPanel.CustomMinimumSize = new Vector2(236, 250);
        _legendPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.08f, 0.12f, 0.85f),
            BorderColor = new Color(0.35f, 0.40f, 0.50f, 0.9f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        _uiLayer.AddChild(_legendPanel);

        var legendVBox = new VBoxContainer();
        legendVBox.AddThemeConstantOverride("separation", 4);
        _legendPanel.AddChild(legendVBox);

        _legendTitle = new Label
        {
            Text = LayerNames[0],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 26),
        };
        _legendTitle.AddThemeFontSizeOverride("font_size", 17);
        _legendTitle.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.85f));
        legendVBox.AddChild(_legendTitle);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            // 2026-08-17：面板高度随内容自适应（上限 250）→ scroll min 只保底，不撑大面板
            CustomMinimumSize = new Vector2(220, 40),
            // ⚠️ 2026-08-17 用户反馈"底下留白"：VBox 不拉伸非 expand 控件 → 固定 250 面板里
            //   内容不足时余白堆在底部。ExpandFill = 滚动区动态吃掉全部剩余高度（无留白）。
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        legendVBox.AddChild(scroll);
        // ⚠️ 2026-08-17：图例区滚轮只滚图例——ScrollContainer 滚到底不消费事件 → 穿透到
        //   3D 相机 _UnhandledInput → 地图缩放（用户报"滚动到底后再滚会导致地图缩放"）。
        //   在内容区（scroll+footer+标题）统一消费滚轮：滚动正常，滚到底/在说明文字上都不穿透。
        legendVBox.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed
                && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
                legendVBox.AcceptEvent();   // Control.AcceptEvent（C# 里 InputEvent 无此方法）
        };

        _legendBox = new VBoxContainer();
        _legendBox.AddThemeConstantOverride("separation", 3);
        _legendBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_legendBox);

        // 常驻说明文字区（滚动区外）：AddLegendText 的灰色说明行固定显示在面板底部
        _legendFooter = new VBoxContainer();
        _legendFooter.AddThemeConstantOverride("separation", 2);
        _legendFooter.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        legendVBox.AddChild(_legendFooter);

        RebuildLegend();   // 初始图层图例
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
        // 14 独立势力：王冠（三个尖顶+底座，直线）——势力最高聚合
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M6 20 L6 9 L11 14 L14 6 L17 14 L22 9 L22 20 Z M4 23 H24' stroke='#fd8' stroke-width='2' fill='none' stroke-linejoin='miter' stroke-linecap='round'/></svg>",
        // 15 科技：灯泡（灯丝+底座，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><circle cx='14' cy='11' r='7' fill='none' stroke='#8f8' stroke-width='2'/><path d='M11 19 H17 M12.5 23 H15.5 M14 16 V19' stroke='#8f8' stroke-width='2' stroke-linecap='round'/></svg>",
        // 16 宗教：神庙（三角顶+立柱，直线）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 4 L24 22 L4 22 Z M8 22 L8 26 M12 22 L12 26 M16 22 L16 26 M20 22 L20 26' stroke='#8f8' stroke-width='2' fill='none' stroke-linecap='round'/></svg>",
        // 17 势力范围：领地边界（六边形 + 内部边界线，直线；每领地独立色）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M14 3 L24 9 L24 19 L14 25 L4 19 L4 9 Z' stroke='#fd8' stroke-width='2' fill='none' stroke-linejoin='miter'/><path d='M14 3 L14 25 M4 9 L24 19 M24 9 L4 19' stroke='#fd8' stroke-width='1.5' fill='none' stroke-linecap='round'/></svg>",
        // 18 政体：上升阶梯（组织化程度递增——band→部落→酋邦）
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 28 28'><path d='M4 24 H24 M4 24 V19 H13 V14 H20 V9 H24 V5' stroke='#f8a' stroke-width='2.5' fill='none' stroke-linejoin='miter' stroke-linecap='round'/><path d='M4 22 H24' stroke='#f8a' stroke-width='1' opacity='0.4'/></svg>",
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

    /// <summary>显示海陆判定（2026-08-17）：视觉海（byte 量化 elev<hSea）且逻辑非陆地
    /// （R≤0 或无 civ）才判海；近海格（elev<hSea 但 R>0 逻辑可居）显示陆地/数据色——
    /// 人口点不落在"视觉海水"上（byte 量化误差——R>0 是模拟权威）。
    /// ⚠️ 2026-08-18：R 是逻辑格（顶点）数组——id 是显示格——按 _tileVerts[id] 查。</summary>
    private bool IsDisplaySea(int id)
        => _tileElev[id] < _hSea && (_civCtx?.R == null || _civCtx.R[_tileVerts[id]] <= 0f);

    /// <summary>点击诊断（2026-08-17 用户要求）：左键点击地图格 → 日志打印位置/颜色/势力/人口等
    /// 全量诊断信息——定位异常势力色块/人口格的具体实例。</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left
            && _map != null && _tiles != null)
        {
            var cam = GetNode<OrbitalCamera>("OrbitalCamera")?.Cam;
            if (cam == null) return;
            var from = cam.ProjectRayOrigin(mb.Position);
            var dir = cam.ProjectRayNormal(mb.Position);
            // 射线-球面求交（球心=原点）
            float r = RadiusKm;
            float a = dir.Dot(dir);
            float b = 2f * from.Dot(dir);
            float c = from.Dot(from) - r * r;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return;
            float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
            if (t < 0f) t = (-b + Mathf.Sqrt(disc)) / (2f * a);
            if (t < 0f) return;
            var hit = from + dir * t;
            // 最近格中心（O(n) 一次点击——40962 距离比较 ~1ms）
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < _tiles.Count; i++)
            {
                float d = (_tiles[i].Center - hit).LengthSquared();
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) ClickDebug(best);
        }
    }

    /// <summary>格诊断打印（位置/颜色/势力/人口/实体——逐字段全量）。</summary>
    private void ClickDebug(int i)
    {
        var col = MakeColorFn(_layer)(_tiles[i]);
        GD.Print($"[CLICK] 格={i} 图层={LayerNames[_layer]} 颜色=#{col.ToHtml()} pos=({_tiles[i].Center.X:F2},{_tiles[i].Center.Y:F2},{_tiles[i].Center.Z:F2})");
        int vid = _tileVerts != null ? _tileVerts[i] : i;   // ⚠️ 2026-08-18：显示格→逻辑格（顶点）映射
        float elevM2 = _map.Elev != null ? _map.Elev[vid] : (_tileElev[i] - _hSea) * (_map.MaxElev - _map.MinElev);   // 实际海拔（米）
        GD.Print($"  elev={_tileElev[i]:F3} 海拔={elevM2:F0}m pop={_tilePop[vid]:F1} power={_tilePower[i]} polity={_tilePolity[i]} tribe={_tileTribe[i]} terr={_tileTerritory[i]} culture={_tileCulture[i]} religion={_tileReligion[i]}");
        // ⚠️ 2026-08-18：势力统计——该格所属势力总格数/有人格数（当场判断"无人口势力" vs "采集格无人"）
        if (_tilePower[i] != 0)
        {
            int pow = _tilePower[i], pCells = 0, pPop = 0;
            for (int j = 0; j < _tiles.Count; j++)
                if (_tilePower[j] == pow) { pCells++; if (_tilePop[_tileVerts[j]] > 0f) pPop++; }
            GD.Print($"  势力{pow}: 共{pCells}格 / 有人口{pPop}格（pPop=0 ⇒ 无人口势力=异常；pPop>0 ⇒ 本格是采集格=设计）");
        }
        if (_civCtx != null)
        {
            GD.Print($"  CellOwner={(_civCtx.CellOwner != null ? _civCtx.CellOwner[vid] : -1)} R={(_civCtx.R != null ? _civCtx.R[vid] : -1f):F2} LockedUntil={(_civCtx.LockedUntil != null ? _civCtx.LockedUntil[vid] : 0)}");
            foreach (var ce in _civCtx.Entities)
                if (!ce.Dead && ce.Cell == vid)
                    GD.Print($"  驻扎实体={ce.Id} P={ce.P:F1} CarryMult={ce.CarryMult:F2} TSize={ce.TerritorySize} TerrId={ce.TerritoryId} ChiefdomId={ce.ChiefdomId} Prestige={ce.Prestige:F2}");
            // ⚠️ 2026-08-18：该格所属势力（CellOwner）的驻扎格 + 位置 + 可达距离（BFS 走逻辑陆地）
            int ownerId = _civCtx.CellOwner != null ? _civCtx.CellOwner[vid] : -1;
            World.CivSim.CivEntity owner = null;
            foreach (var ce in _civCtx.Entities)
                if (!ce.Dead && ce.Id == ownerId) { owner = ce; break; }
            if (owner != null && owner.Cell >= 0 && owner.Cell < _tiles.Count && _tiles != null)
            {
                int dist = -1;
                if (_civCtx.Grid?.Neighbors != null && _civCtx.R != null)
                {
                    var distArr = new int[_tiles.Count];
                    System.Array.Fill(distArr, -1);
                    var q = new System.Collections.Generic.Queue<int>();
                    distArr[vid] = 0; q.Enqueue(vid);
                    while (q.Count > 0)
                    {
                        int c = q.Dequeue();
                        if (c == owner.Cell) { dist = distArr[c]; break; }
                        foreach (var nn in _civCtx.Grid.Neighbors[c])
                        {
                            if (_civCtx.R[nn] <= 0f) continue;   // 只走逻辑陆地（与影响圈一致）
                            if (distArr[nn] < 0) { distArr[nn] = distArr[c] + 1; q.Enqueue(nn); }
                        }
                    }
                }
                var pc = _civCtx.Grid != null && owner.Cell < _civCtx.Grid.Verts.Length
                    ? _civCtx.Grid.Verts[owner.Cell] * RadiusKm   // ⚠️ 逻辑格位置（顶点×R）——面编号错位
                    : _tiles[owner.Cell].Center;
                GD.Print($"  该势力驻扎格={owner.Cell}（P={owner.P:F1}）可达距离={dist}跳 pos=({pc.X:F2},{pc.Y:F2},{pc.Z:F2})");
            }
            else if (owner == null && ownerId >= 0)
                GD.Print($"  CellOwner={ownerId} 无存活实体（残留）");
        }
    }

    /// <summary>同步图层按钮的按下态与可见性（键盘/Inspector/分类切换时 UI 跟随）。
    /// 可见性 = 只显示当前分类的子按钮；按下态跟随 _layer；行位置按可见按钮数重算居中。
    /// ⚠️ 分类跟随：外部（Inspector/代码）直接改 Layer 时自动切到其所属分类，
    ///   保证选中按钮可见；但 UI 点分类按钮走 ShowCategoryButtons（不改 _layer）。</summary>
    private void SyncLayerButtons()
    {
        if (_layerButtons == null)
            return;
        // 外部改 Layer → 分类跟随（选中按钮必须在可见集合内）
        _category = LayerCats[_layer];
        for (int i = 0; i < _layerButtons.Length; i++)
            _layerButtons[i].ButtonPressed = i == _layer;
        ShowCategoryButtons();
    }

    /// <summary>按当前分类刷新图层按钮可见性 + 分类按钮按下态 + 行居中（不改 _layer）。</summary>
    private void ShowCategoryButtons()
    {
        if (_layerButtons == null)
            return;
        int visible = 0;
        for (int i = 0; i < _layerButtons.Length; i++)
        {
            bool show = LayerCats[i] == _category;
            _layerButtons[i].Visible = show;
            if (show) visible++;
        }
        for (int i = 0; i < _catButtons.Length; i++)
            _catButtons[i].ButtonPressed = (int)_category == i;
        // 42px/按钮 + 4px separation 居中；可见按钮数变化时重算。
        // ⚠️ 必须用 Offset（相对 anchor 的原始偏移）而非 Position——Position setter 会用
        //   父尺寸反推 offset（offset = pos - anchor×parentSize），AddChild 后调用会把
        //   rect 起点推到屏幕外（实测 global=(-113,-84)，2026-08-08）。
        float halfW = 21f * visible + 2f * (visible - 1);
        _layerRow.OffsetLeft = -halfW;
        _layerRow.OffsetTop = -84;
    }

    // ── 图例 ──

    /// <summary>生物群系显示名（索引=BiomeType 值；0-31 全覆盖）。</summary>
    private static readonly string[] BiomeNames =
    {
        "深海", "海洋", "冰原(EF)", "苔原(ET)", "", "", "", "", "", "", "", "",
        "高山", "河岸带",
        "热带雨林(Af)", "热带季风林(Am)", "热带稀树草原(Aw)",
        "热沙漠(BWh)", "冷沙漠(BWk)", "热半干旱草原(BSh)", "冷半干旱草原(BSk)",
        "湿润亚热带(Cfa)", "海洋性温带(Cfb)", "冬干亚热带(Cwa)",
        "地中海热夏(Csa)", "地中海凉夏(Csb)",
        "湿润大陆热夏(Dfa)", "湿润大陆暖夏(Dfb)", "亚寒带针叶林(Dfc)", "冬干大陆(Dwa)",
        "极地海洋", "热带海洋",
    };

    /// <summary>土壤肥力名（索引 1-5）。</summary>
    private static readonly string[] SoilNames = { "", "贫瘠", "差", "中", "好", "肥沃" };

    /// <summary>科技时代名（索引 0=石器 1-4=TechEpochColors）。</summary>
    private static readonly string[] TechEpochNames = { "石器", "新石器", "青铜", "铁器", "古典" };

    /// <summary>重建图例（当前图层颜色说明；内容超出固定面板 → ScrollContainer 滚动）。</summary>
    private void RebuildLegend()
    {
        if (_legendBox == null || _legendPanel == null) return;   // UI 未建（EnsureUi 前）或已释放
        // 清空旧条目（RemoveChild 立即脱离树 + QueueFree 帧末释放——纯 QueueFree 会残留到帧末）
        foreach (Node c in _legendBox.GetChildren())
        {
            _legendBox.RemoveChild(c);
            c.QueueFree();
        }
        if (_legendFooter != null)
            foreach (Node c in _legendFooter.GetChildren())
            {
                _legendFooter.RemoveChild(c);
                c.QueueFree();
            }
        _legendTitle.Text = LayerNames[_layer];

        switch (_layer)
        {
            case 0: // 海拔（2026-08-18）：海 <-200m 深海 / -200~0m 浅海；陆地连续色带
                AddLegendGradient(
                    new[] { new Color(0.01f, 0.05f, 0.18f), new Color(0.20f, 0.45f, 0.68f),
                            new Color(0.70f, 0.65f, 0.40f), new Color(0.30f, 0.65f, 0.10f),
                            new Color(0.60f, 0.50f, 0.35f), new Color(0.95f, 0.97f, 1.00f) },
                    "深海<-200m", "最高");
                AddLegendText("海：<-200m 深海 / -200~0m 浅海（大陆架）；陆：连续色带（实际米）");
                break;
            case 1: // 温度：分段色带
                AddLegendGradient(
                    new[] { new Color(0.08f, 0.12f, 0.45f), new Color(0.22f, 0.52f, 0.72f), new Color(0.38f, 0.72f, 0.42f), new Color(0.92f, 0.78f, 0.28f), new Color(0.88f, 0.30f, 0.15f) },
                    "-85°C", "+45°C");
                AddLegendText("分段色带：极寒/冰点/0-15°/宜居/高温");
                break;
            case 2: // 降水
                AddLegendGradient(
                    new[] { new Color(0.90f, 0.80f, 0.40f), new Color(0.10f, 0.30f, 0.70f) },
                    $"{_precipMin:F0}mm", $"{_precipMax:F0}mm");
                AddLegendText("陆地自适应色带（随地图分布）");
                break;
            case 3: // 生物群系
                for (int b = 0; b < BiomeNames.Length; b++)
                {
                    if (string.IsNullOrEmpty(BiomeNames[b])) continue;
                    AddLegendRow(BiomeColors.BiomeToColor((BiomeType)b), BiomeNames[b]);
                }
                break;
            case 4: // 风场
                AddLegendText("→ 箭头 = 盛行风向（月风场）");
                AddLegendText("疏密 = 风速强度");
                AddLegendText("月份滑块切换 1-12 月");
                break;
            case 5: // 洋流
                AddLegendRow(new Color(0.95f, 0.35f, 0.25f), "暖流");
                AddLegendRow(new Color(0.25f, 0.55f, 0.95f), "寒流");
                AddLegendText("箭头大小 = 流速");
                break;
            case 6: // 河流
                AddLegendRow(new Color(0.25f, 0.45f, 0.75f), "湖泊");
                AddLegendRow(new Color(0.35f, 0.70f, 1.00f), "河流");
                AddLegendText("干涸盆地（盐湖）不显示");
                break;
            case 7: // 流域
                AddLegendText("每流域独立颜色");
                AddLegendText("海洋/边缘排水区 = 浅蓝/灰绿");
                break;
            case 8: // 矿藏
                for (int m = 1; m < MineralSystem.Names.Length; m++)
                    AddLegendRow(MineralColors[m], MineralSystem.Names[m]);
                AddLegendText("明度 = 富度（贫暗/富中/巨型亮）");
                break;
            case 9: // 土壤
                for (int s = 1; s <= 5; s++)
                    AddLegendRow(SoilColors[s], SoilNames[s]);
                break;
            case 10: // 月降水
                AddLegendGradient(
                    new[] { new Color(0.90f, 0.80f, 0.40f), new Color(0.10f, 0.30f, 0.70f) },
                    $"{_monthPrecipMin:F0}mm", $"{_monthPrecipMax:F0}mm");
                AddLegendText("当月降水（×12 年尺度色带）");
                break;
            case 11: // 月温度
                AddLegendGradient(
                    new[] { new Color(0.08f, 0.12f, 0.45f), new Color(0.22f, 0.52f, 0.72f), new Color(0.38f, 0.72f, 0.42f), new Color(0.92f, 0.78f, 0.28f), new Color(0.88f, 0.30f, 0.15f) },
                    "-60°C", "+60°C");
                AddLegendText("当月均温");
                break;
            case 12: // 人口：无人（采集格）+ 16 档等比色块（log 分位，与地图同色；驻扎格人口）
            {
                var lo = new Color(0.95f, 0.75f, 0.25f);
                var hi = new Color(0.80f, 0.15f, 0.05f);
                AddLegendRow(new Color(0.25f, 0.25f, 0.28f), "无人（采集格 / 海洋）");
                if (_popMax <= 0f)
                {
                    AddLegendText("（无人口数据）");
                    break;
                }
                for (int i = 0; i <= 15; i++)
                {
                    float x = i / 15f;
                    float p = Mathf.Exp(_popLogMin + x * (_popLogMax - _popLogMin)) - 1f;
                    // ⚠️ 2026-08-17 用户反馈"人口怎么还能是小数"：人口物理上是整数——
                    //   模型层 P 是 float（连续宏观增长），显示层取整（<1 显示 "<1" 防与无人灰混淆）
                    string label = i == 15 ? $"≥ {FmtPop(p)}（最高 {FmtPop(_popMax)}）" : FmtPop(p);
                    AddLegendRow(lo.Lerp(hi, x), label);
                }
                AddLegendText("驻扎格人口（人/格）· log 分位自适应");
                break;
            }
            case 13: // 文化：动态条目（每文化独立色，按覆盖格数排序，滚动查看）
                AddLegendText("每文化独立颜色（金色角散列）");
                AddLegendDynamic(_tileCulture, c => HslToRgb(GoldenHue(c), 0.55f, 0.62f), "文化");
                break;
            case 14: // 独立势力（2026-08-17）：每势力独立色——最高聚合层（酋邦>部落>band）
                AddLegendRow(new Color(0.25f, 0.25f, 0.28f), "无人 / 海洋");
                AddLegendText("每独立势力一种颜色（黄金角散列）");
                AddLegendText("酋邦（跨部落联盟）> 部落（领地≥2）> 独立 band");
                break;
            case 15: // 科技
                for (int e = 0; e <= 4; e++)
                {
                    var col = e == 0 ? new Color(0.55f, 0.42f, 0.28f) : TechEpochColors[e - 1];
                    AddLegendRow(col, TechEpochNames[e]);
                }
                break;
            case 16: // 宗教：动态条目
                AddLegendText("每宗教派别独立颜色");
                AddLegendDynamic(_tileReligion, r => HslToRgb(GoldenHue(r), 0.55f, 0.62f), "派别");
                break;
            case 17: // 势力范围：静态说明（每领地独立色，动态条目过多故仅说明）
                AddLegendRow(new Color(0.30f, 0.32f, 0.36f), "无领地");
                AddLegendText("每领地独立颜色（语言群 key 完整哈希）");
                AddLegendText("同领地必同语言群 → 同领地同色");
                break;
            case 18: // 政体（2026-08-17）：独立势力基础上按政体类型分色
                AddLegendRow(HslToRgb(0.60f, 0.30f, 0.55f), "独立 band（无组织）");
                AddLegendRow(HslToRgb(0.35f, 0.50f, 0.55f), "部落（领地凝聚）");
                AddLegendRow(HslToRgb(0.045f, 0.58f, 0.55f), "酋邦（联盟+酋长）");
                AddLegendText("同类政体同色系；势力间色相微扰可辨");
                break;
        }
        // ⚠️ 2026-08-17 用户拍板：图例数量不足时面板高度自适应缩短（上限 250，贴底锚定）。
        //   内容高 = 色块行 min 高 + 行间隙；footer 常驻文字也计入；clamp [120, 250]。
        float contentH = 0f;
        for (int i = 0; i < _legendBox.GetChildCount(); i++)
            if (_legendBox.GetChild(i) is Control cc) contentH += cc.GetCombinedMinimumSize().Y;
        contentH += Mathf.Max(0, _legendBox.GetChildCount() - 1) * 3;
        float footH = 0f;
        for (int i = 0; i < _legendFooter.GetChildCount(); i++)
            if (_legendFooter.GetChild(i) is Control cc) footH += cc.GetCombinedMinimumSize().Y;
        footH += Mathf.Max(0, _legendFooter.GetChildCount() - 1) * 2;
        float panelH = Mathf.Clamp(26 + 4 + contentH + 4 + footH + 12, 120f, 250f);
        _legendPanel.CustomMinimumSize = new Vector2(236, panelH);
        // 贴底：BottomRight 锚点下 OffsetTop = -高（已入树必须用 Offset，Position setter 会飞屏）
        _legendPanel.OffsetTop = -panelH;
    }

    /// <summary>人口显示取整（2026-08-17 用户反馈小数）：<1 显示 "<1"（防与无人灰混淆），≥1 整数。</summary>
    private static string FmtPop(float p) => p < 1f ? "<1" : $"{p:F0}";

    /// <summary>图例条目：色块 + 文字（横向）。</summary>
    private void AddLegendRow(Color c, string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var swatch = new ColorRect
        {
            Color = c,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(swatch);
        var lab = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        lab.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(lab);
        _legendBox.AddChild(row);
    }

    /// <summary>图例条目：渐变色带 + 两端标注。</summary>
    private void AddLegendGradient(Color[] stops, string low, string high)
    {
        // Offsets 动态生成（段数任意；均匀分布）
        var offs = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++)
            offs[i] = stops.Length > 1 ? i / (float)(stops.Length - 1) : 0f;
        var bar = new GradientTexture2D
        {
            Gradient = new Gradient { Offsets = offs, Colors = stops },
            Fill = GradientTexture2D.FillEnum.Linear,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(1, 0),
            Width = 180, Height = 14,
        };
        var tr = new TextureRect
        {
            Texture = bar,
            CustomMinimumSize = new Vector2(180, 14),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        _legendBox.AddChild(tr);
        var labels = new HBoxContainer();
        var lo = new Label { Text = low }; lo.AddThemeFontSizeOverride("font_size", 12);
        var hi = new Label { Text = high, HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hi.AddThemeFontSizeOverride("font_size", 12);
        labels.AddChild(lo);
        labels.AddChild(hi);
        _legendBox.AddChild(labels);
    }

    /// <summary>图例条目：纯说明文字（小字号、浅灰）——输出到滚动区外的常驻底部区
    /// （2026-08-17 用户拍板：说明文字固定显示，不随条目滚动）。</summary>
    private void AddLegendText(string text)
    {
        if (_legendFooter == null) return;
        var lab = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lab.AddThemeFontSizeOverride("font_size", 12);
        lab.AddThemeColorOverride("font_color", new Color(0.75f, 0.78f, 0.85f));
        _legendFooter.AddChild(lab);
    }

    /// <summary>图例动态条目：统计数组中出现过的 key，按覆盖格数降序显示前 12 个（超出滚动查看）。</summary>
    private void AddLegendDynamic(int[] tileKeys, System.Func<int, Color> colorOf, string kind)
    {
        if (tileKeys == null)
        {
            AddLegendText($"（{kind}数据未加载）");
            return;
        }
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        for (int i = 0; i < tileKeys.Length; i++)
        {
            int k = tileKeys[i];
            if (k == 0) continue;
            counts[k] = counts.TryGetValue(k, out int v) ? v + 1 : 1;
        }
        if (counts.Count == 0)
        {
            AddLegendText("（无）");
            return;
        }
        var sorted = new System.Collections.Generic.List<KeyValuePair<int, int>>(counts);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
        int shown = Mathf.Min(12, sorted.Count);
        for (int i = 0; i < shown; i++)
            AddLegendRow(colorOf(sorted[i].Key), $"{kind} {sorted[i].Key}（{sorted[i].Value}格）");
        if (sorted.Count > shown)
            AddLegendText($"…共 {sorted.Count} 个{kind}（滚动查看）");
    }
}
