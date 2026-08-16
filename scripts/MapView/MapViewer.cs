using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Biome;
using World.Camera;
using World.HexPlanet;
using World.MapGen;
using World.PlanetLOD;
using World.Services;
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
    private TileIndex _tileIndex;   // ⚠️ 2026-08-19：显示格↔逻辑格映射收敛（原散落 _tileVerts 数组；63km 错位案根治）
    // 文明图层（.cmp 游玩地图；v2 部落模型：人口/文化/部落/科技）
    private World.CivSim.CivSimContext _civCtx;   // 文明演化上下文（null=纯自然地图）
    private float[] _tilePop;       // 每格总人口（Σ 部落，0=无人/海洋）
    private int[] _tileCulture;     // 每格主导文化 key 的 FNV 哈希（0=无；完整 32 位 → 每文化独立色）
    private byte[] _tileCultureGroup; // 每格主导文化群（0=无）
    private int[] _tileReligion;    // 每格主导宗教派别 key 的 FNV 哈希（0=无；relig_N 每派别独立色）
    private int[] _tileTribe;       // 每格主导部落 id（-1=无）
    private int[] _tilePower;       // 每格主导势力 id（2026-08-17：最高聚合——酋邦>部落>band；高位域标记）
    private Dictionary<int, Color> _powerPalette; // 独立势力调色板（2026-08-16 终版：最远点采样——任意两势力色距有下界，见 PowerPalette）
    private Dictionary<int, Color> _territoryPalette; // 势力范围调色板（2026-08-16：同 PowerPalette 最远点采样——旧版明度 0.85 全白 + 散列近撞色）
    private byte[] _tilePolity;     // 每格主导势力政体类型（2026-08-17：0=独立band 1=部落 2=酋邦）
    private byte[] _tileTechEpoch;  // 每格主导部落最高技术时代 0-4
    private int[] _tileTerritory;   // 每格主导 band 的领地（语言群 key 完整哈希；0=无领地）
    private byte[] _tileSettlement; // 每格聚落（2026-08-19 阶段3：0=无 1=新村 2=村庄 3=城镇 4=城市 5=废墟）
    // 身份族系映射（2026-08-19 族系分色图例：文化/派别 → 语言群 hash；惰性建一次）
    private Dictionary<int, int> _cultGroup;
    private Dictionary<int, int> _sectGroup;
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
        LayerCat.Human,     // 19 聚落（2026-08-19 阶段3 聚落设计）
    };

    private static readonly string[] LayerNames = { "海拔", "温度", "降水", "生物群系", "风场", "洋流", "河流", "流域", "矿藏", "土壤", "月降水", "月温度", "人口", "文化", "独立势力", "科技", "宗教", "势力范围", "政体", "聚落" };

    /// <summary>实体 → 势力 id（最高聚合层：酋邦>部落≥2>独立 band；高位域标记防跨域撞色）。</summary>
    private static int PowerIdOf(World.CivSim.Tribe e)
    {
        if (e.ChiefdomId >= 0) return unchecked((int)0x80000000) | (e.ChiefdomId & 0x3FFFFFFF);
        if (e.TerritorySize >= 2) return unchecked((int)0x40000000) | (e.TerritoryId & 0x3FFFFFFF);
        // ⚠️ 2026-08-18 修复：band 也进独立域（0x20000000）——实体 Id 从 0 分配（NextTribeId 起始 0），
        //   Id=0 的起源 band 若返回原值 0，与 _tilePower==0 的"无势力"哨兵冲突 →
        //   独立势力/政体图层把它显示成灰色（无势力），人口图层却正常 → 两层冲突。
        //   域值非 0 保证与哨兵彻底隔离（部落 0x40000000 / 酋邦 0x80000000 之上再分一层）。
        return unchecked((int)0x20000000) | (e.Id & 0x3FFFFFFF);
    }

    /// <summary>实体 → 政体类型（0=独立 band 1=部落 2=酋邦 3=国家——2026-08-16 阶段4）。</summary>
    private static byte PolityOf(World.CivSim.Tribe e)
    {
        if (e.StateId >= 0) return 3;   // 国家（制度化酋邦——优先级最高）
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

    /// <summary>聚落图层色（2026-08-19 阶段3：索引 = _tileSettlement 值 − 1——0 新村 1 村庄 2 城镇 3 城市 4 废墟）。</summary>
    private static readonly Color[] SettlementLevelColors =
    {
        new(0.72f, 0.55f, 0.35f),  // 新村/营地：棕
        new(0.35f, 0.72f, 0.35f),  // 村庄：绿
        new(0.95f, 0.65f, 0.25f),  // 城镇：橙
        new(0.85f, 0.25f, 0.20f),  // 城市：红
        new(0.45f, 0.45f, 0.50f),  // 废墟：灰
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
        // 支持从 UI 菜单进入时指定存档路径（EventBus 待消费请求，ADR-0002）
        string pending = EventBus.ConsumeMapViewRequest();
        if (!string.IsNullOrEmpty(pending))
        {
            _mapPath = pending;
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
        19 => "聚落",
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
                         $"epoch=石器时代 ticks={civResult.FinalTick} pop={civResult.Context.TotalPopulation():F0} entities={civResult.Context.Tribes.Count})");
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
        _tilePop = new float[n];
        _tileCulture = new int[n];
        _tileCultureGroup = new byte[n];
        _tileReligion = new int[n];
        _tileTribe = new int[n];
        _tilePower = new int[n];     // 独立势力 id（2026-08-17；0=无）
        _tilePolity = new byte[n];   // 政体类型（2026-08-17；0=band 1=部落 2=酋邦）
        _tileTechEpoch = new byte[n];
        _tileTerritory = new int[n];
        _tileSettlement = new byte[n];
        System.Array.Fill(_tileTribe, -1);
        bool hasCiv = _civCtx != null;
        // 2026-08-10 影响力场模型（v8）：band 实体只在驻扎点格，领地=归属格——文明图层改为
        // **归属格主导**（每格查 CellOwner → 该 band 的文化/宗教/部落/科技；人口=领地均摊，5 km² 量级）
        var civIdMap = new System.Collections.Generic.Dictionary<int, World.CivSim.Tribe>();
        if (hasCiv)
            foreach (var ce in _civCtx.Tribes)
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
        // ⚠️ 2026-08-19：映射收敛——TileIndex 主线程预构建（面→顶点 + 顶点→面反查；63km 错位案根治）
        _tileIndex = new TileIndex(map, centers);
        // 聚落索引（2026-08-19 阶段3：Cell → 聚落；按逻辑格查——平行循环内 FaceToVertex 后查）
        var settlementByCell = new Dictionary<int, byte>();
        if (hasCiv && _civCtx.Settlements != null)
            foreach (var s in _civCtx.Settlements)
                if (s.Cell >= 0 && s.Cell < n)
                    settlementByCell[s.Cell] = (byte)(s.IsRuin ? 5 : (s.Level + 1));   // 1-4=等级 5=废墟

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
            int vid = _tileIndex.FaceToVertex(i);   // 显示格→逻辑顶点（映射收敛，2026-08-19）
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
                int vid2 = _tileIndex.FaceToVertex(i);
                int ownerId = _civCtx.CellOwner != null ? _civCtx.CellOwner[vid2] : -1;
                if (ownerId >= 0 && civIdMap.TryGetValue(ownerId, out var dom))
                {
                    // ⚠️ 2026-08-17：人口图层不在这里写——领地格 = 采集格（无常住人口），
                    //   人口只在驻扎格（Tribe.Cell）显示该 band 的 P（并行循环后实体表直写）。
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
                    // 聚落（2026-08-19 阶段3：Cell → 等级/废墟；1-4=新村~城市 5=废墟）
                    if (settlementByCell.TryGetValue(vid2, out byte slevel)) _tileSettlement[i] = slevel;
                }
            }
            // ⚠️ 2026-08-16：每 64 格报一次进度（并行 For 内；不调取消——检查在外部）
            if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
                progress(done / (float)n);
        });
        progress?.Invoke(1f);
        // ⚠️ 2026-08-16 独立势力调色板：**最远点采样**（须在 MakeColorFn 使用前构建——BuildColors 查表）。
        //   散列式（hue=φ×id）与排序秩黄金角（hue=φ×r）在斐波那契距 id/秩对上必近撞色相——
        //   探针实测散列最小色距 0.011、排序秩 0.039（均 <0.05 肉眼阈值，两势力看似同色）。
        //   最远点采样：候选网格避开海蓝相 + 海色/无势力灰为锚点 → 任意两势力色距有下界（291 势力实测 ≥0.1）。
        //   确定性：同档 → 同 id 集 → 同秩 → 同色（候选顺序固定 + 并列取小索引）。
        if (_tilePower != null)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < n; i++) if (_tilePower[i] != 0) set.Add(_tilePower[i]);
            _powerPalette = PowerPalette.Build(set);
        }
        // ⚠️ 2026-08-16 势力范围调色板（同 PowerPalette 最远点采样）：旧版 HslToRgb(GoldenHue,0.55,0.85)
        //   明度 0.85 → 所有领地色 RGB 挤在 0.77-0.93 近白区间（用户反馈"势力范围地图全是白的"）；
        //   且黄金角散列在 ~1500 领地规模下斐波那契距 key 对近撞色相。改为最远点采样调色板
        //   （任意两领地色距有下界 + 与海色/无领地灰可分）。
        if (_tileTerritory != null)
        {
            var tset = new HashSet<int>();
            for (int i = 0; i < n; i++) if (_tileTerritory[i] != 0) tset.Add(_tileTerritory[i]);
            _territoryPalette = PowerPalette.Build(tset);
        }
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
            // ⚠️ 2026-08-19 语义定案（用户"浅深区分有人无人不需要，直接补齐"）：人文图层**全部按归属者
            //   （区域）身份统一着色**——定居格不再做亮度强调（无 _tileSettled 分级）；定居位置由人口图层承担。
            //   归属者统一 → 身份差异恒 0，结构上不可能出飞地（08-19 飞地修复保留）。
            for (int e = 0; e < _civCtx.Tribes.Count; e++)
            {
                var ce = _civCtx.Tribes[e];
                if (ce.Dead || ce.Cell < 0 || ce.Cell >= n) continue;
                if (_civCtx.R != null && _civCtx.R[ce.Cell] <= 0f) continue;   // 逻辑陆地（与模拟一致）
                _tilePop[ce.Cell] += ce.P;   // 驻扎格实有人口（营地）——只有人的格显示人
            }
        }   // end if(hasCiv)
        // 人口图层自适应归一化（相对本图分布——分位数模型，用户拍板风格）：
        // log(p+1) 压缩重尾 + 有人陆地格 P1/P99 分位为色带端点 → 单格超大城市不拉爆、
        // 最小聚落也有可见色（旧版 log(全局max) 归一：全球最大值单点把其余全压成近黑色）
        var popLog = new System.Collections.Generic.List<float>();
        _popMax = 0f;
        for (int i = 0; i < n; i++)
        {
            if (_tilePop[_tileIndex.FaceToVertex(i)] <= 0f) continue;     // 无人格不入带（人口按顶点写——显示格 i 读其顶点）
            popLog.Add(Mathf.Log(_tilePop[_tileIndex.FaceToVertex(i)] + 1f));
            if (_tilePop[_tileIndex.FaceToVertex(i)] > _popMax) _popMax = _tilePop[_tileIndex.FaceToVertex(i)];
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
    private volatile bool _pendingRecolor;   // 构建中切图层 → 完成后自动重算

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
        int vid = _tileIndex != null ? _tileIndex.FaceToVertex(i) : i;   // ⚠️ 2026-08-18：显示格→逻辑格（顶点）映射（2026-08-19 收敛 TileIndex）
        float elevM2 = _map.Elev != null ? _map.Elev[vid] : (_tileElev[i] - _hSea) * (_map.MaxElev - _map.MinElev);   // 实际海拔（米）
        GD.Print($"  elev={_tileElev[i]:F3} 海拔={elevM2:F0}m pop={_tilePop[vid]:F1} power={_tilePower[i]} polity={_tilePolity[i]} tribe={_tileTribe[i]} terr={_tileTerritory[i]} culture={_tileCulture[i]} religion={_tileReligion[i]}");
        // ⚠️ 2026-08-18：势力统计——该格所属势力总格数/有人格数（当场判断"无人口势力" vs "采集格无人"）
        if (_tilePower[i] != 0)
        {
            int pow = _tilePower[i], pCells = 0, pPop = 0;
            for (int j = 0; j < _tiles.Count; j++)
                if (_tilePower[j] == pow) { pCells++; if (_tilePop[_tileIndex.FaceToVertex(j)] > 0f) pPop++; }
            GD.Print($"  势力{pow}: 共{pCells}格 / 有人口{pPop}格（pPop=0 ⇒ 无人口势力=异常；pPop>0 ⇒ 本格是采集格=设计）");
        }
        if (_civCtx != null)
        {
            GD.Print($"  CellOwner={(_civCtx.CellOwner != null ? _civCtx.CellOwner[vid] : -1)} R={(_civCtx.R != null ? _civCtx.R[vid] : -1f):F2} LockedUntil={(_civCtx.LockedUntil != null ? _civCtx.LockedUntil[vid] : 0)}");
            foreach (var ce in _civCtx.Tribes)
                if (!ce.Dead && ce.Cell == vid)
                    GD.Print($"  驻扎实体={ce.Id} P={ce.P:F1} CarryMult={ce.CarryMult:F2} TSize={ce.TerritorySize} TerrId={ce.TerritoryId} ChiefdomId={ce.ChiefdomId} Prestige={ce.Prestige:F2}");
            // ⚠️ 2026-08-18：该格所属势力（CellOwner）的驻扎格 + 位置 + 可达距离（BFS 走逻辑陆地）
            int ownerId = _civCtx.CellOwner != null ? _civCtx.CellOwner[vid] : -1;
            World.CivSim.Tribe owner = null;
            foreach (var ce in _civCtx.Tribes)
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
}

// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
// Slices (2026-08-19 pure refactor: partial class, behavior unchanged):
//   MapViewer.Colors.cs   - coloring (MakeColorFn/RebuildColors/BuildColorsTask/PowerColor/FamilyColor/MakeLayerIcon)
//   MapViewer.Visuals.cs  - overlay geometry (monsoon/current arrows, rings, rivers, month refreshes, IsDisplaySea)
//   MapViewer.Ui.cs       - UI/legend (progress, EnsureUi, layer buttons, legend rows, BuildIdentityCaches, FmtPop)
// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
