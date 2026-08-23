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
using static World.MapView.MapLayerColors;

using World.CivSim;
using World.CivSim.Entities;
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
///
/// 2026-08-21 策略模式重构（M1-M4）：图层逻辑（颜色/图例/覆盖层/月份回调）全部
/// 内聚到 LayerRegistry 的策略类（MapLayer 子类）；本类降级为上下文/导演——
/// 只做流程编排（加载/几何/预计算/构建/切换），不再有图层 switch。
/// 新增图层 = 新建 Layers/ 下策略类 + LayerRegistry 注册一行。数据通道见 LayerContext/TileDataCache。
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
            LogService.Log("MapViewer", $"Layer.set {value} ({LayerName(value)}) changed={changed} geoReady={_geometryReady} pending={_pendingRecolor}");
            if (changed)
            {
                _layer = value;
                if (IsInsideTree()) RebuildColors();
            }
            SyncLayerButtons();
            RebuildLegend();   // 图例跟随图层（null 保护在方法内）
            // 2026-08-21 M3 策略化：覆盖层节点由策略自持（OverlayNode）——已建则切 Visible；
            //   未建且当前层 HasOverlay → 懒建（EnsureOverlayFor 内部幂等/异步就绪判断）
            foreach (var l in LayerRegistry.All)
                if (l.OverlayNode != null)
                    l.OverlayNode.Visible = (l.Id == _layer);
            if (LayerRegistry.Of(value).HasOverlay)
                EnsureOverlayFor(value);
            // 月份滑块可见性 = 策略 UsesMonth（原硬编码 4/10/11）
            if (_monthSlider != null)
                _monthSlider.Visible = LayerRegistry.Of(value).UsesMonth;
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







    private int[] _vertexWatershed; // 每模拟顶点流域 id（现场算）





    // ── 每格图层值缓存与策略上下文（2026-08-21 策略模式重构 M1）──
    // 原 20 个 _tile* 数组/色带端点/调色板已迁移至 TileDataCache（本类经 _cache 访问；
    // 策略类经 _ctx（LayerContext）访问——数据通道收敛，后续图层策略化铺垫）。
    private TileDataCache _cache;   // 每格图层值缓存（预计算一次，切图层 O(1) 查表）
    private LayerContext _ctx;      // 图层策略上下文（IsSea 等共享判定在此）

    private TileIndex _tileIndex;   // ⚠️ 2026-08-19：显示格↔逻辑格映射收敛（原散落 _tileVerts 数组；63km 错位案根治）
    // 文明图层（.cmp 游玩地图；v2 部落模型：人口/文化/部落/科技）
    private World.CivSim.CivSimContext _civCtx;   // 文明演化上下文（null=纯自然地图）












    // 身份族系映射（2026-08-19 族系分色图例：文化/派别 → 语言群 hash；惰性建一次）




    // 自适应色带（用户拍板：最低到最高归一化，不用固定 2000mm）：年降水 / 当月月降水



    // 季风月风场（现场重算，不存档；箭头图数据源）
    private Vector3[][] _monthWind;  // [12][n] 顶点级月风（切向量，长度=强度；0=无风）
    private int _month = 6;          // 当前月份 0-11（默认 7 月）

    // 月份滑块（图层 4/10/11 显示；1-12 月）——可见性由策略 UsesMonth 决定（2026-08-21 M3）
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

    /// <summary>实体 → 势力 id（最高聚合层：酋邦>部落≥2>独立 band；高位域标记防跨域撞色）。</summary>
    private static int PowerIdOf(Band e)
    {
        if (e.ChiefdomId >= 0) return unchecked((int)0x80000000) | (e.ChiefdomId & 0x3FFFFFFF);
        if (e.TerritorySize >= 2) return unchecked((int)0x40000000) | (e.TerritoryId & 0x3FFFFFFF);
        // ⚠️ 2026-08-18 修复：band 也进独立域（0x20000000）——实体 Id 从 0 分配（NextBandId 起始 0），
        //   Id=0 的起源 band 若返回原值 0，与 _cache.TilePower==0 的"无势力"哨兵冲突 →
        //   独立势力/政体图层把它显示成灰色（无势力），人口图层却正常 → 两层冲突。
        //   域值非 0 保证与哨兵彻底隔离（部落 0x40000000 / 酋邦 0x80000000 之上再分一层）。
        return unchecked((int)0x20000000) | (e.Id & 0x3FFFFFFF);
    }

    /// <summary>实体 → 政体类型（0=独立 band 1=部落 2=酋邦 3=国家——2026-08-16 阶段4）。</summary>
    private static byte PolityOf(Band e)
    {
        if (e.StateId >= 0) return 3;   // 国家（制度化酋邦——优先级最高）
        if (e.ChiefdomId >= 0) return 2;
        if (e.TerritorySize >= 2) return 1;
        return 0;
    }
    private static readonly string[] CatNames = { "地理", "气候", "人文" };
    private LayerCategory _category;      // 当前分类（默认 Geo=0=地理，用户拍板）
    private Button[] _catButtons;    // 3 个分类按钮（最底下一排）
    private HBoxContainer _layerRow; // 图层按钮行容器（分类切换时重算居中）

    // ── 图例面板（月份滑块左侧，固定大小，内容超出滚动；2026-08-08）──
    private PanelContainer _legendPanel;
    private Label _legendTitle;      // 图例标题（图层名）
    private VBoxContainer _legendBox; // 图例条目容器（ScrollContainer 内）
    private VBoxContainer _legendFooter; // 图例说明文字区（滚动区外，常驻面板底部——2026-08-17 用户拍板）


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
            LogService.Log("MapViewer", $"pending path: {_mapPath}");
        }
        Generate();
    }

    /// <summary>图层名（2026-08-21 M3：查策略注册表——单一事实来源）。</summary>
    private static string LayerName(int l) => LayerRegistry.Of(l).Name;
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
                    LogService.LogErr("MapViewer", $"failed to load civ map {_mapPath}");
                    return;
                }
                _map = grid.ToMapData();
                _civCtx = civResult.Context;
                _mapLoaded = true;
                LogService.Log("MapViewer", $"loaded civ map {_mapPath} (gridN={grid.GridN} tiles={grid.N} " +
                         $"epoch=石器时代 ticks={civResult.FinalTick} pop={civResult.Context.TotalPopulation():F0} entities={civResult.Context.Bands.Count})");
            }
            else if (!MapArchive.Read(_mapPath, out var map))
            {
                LogService.LogErr("MapViewer", $"failed to load {_mapPath}");
                return;
            }
            else
            {
                _map = map;
                // v8 单存档化：.mpa 带 CIVI 段 = 含文明的 world（MapArchive.Read 已还原）；纯自然 = null
                _civCtx = map.Civilization?.Context;
                _mapLoaded = true;
                LogService.Log("MapViewer", $"loaded seed={map.Seed} {map.Width}x{map.Height} elev[{map.MinElev:F3},{map.MaxElev:F3}] " +
                         $"civ={(_civCtx != null ? $"yes(bands={_civCtx.Bands.Count} tick={map.Civilization.FinalTick})" : "no")}");
            }

            // ⚠️ 2026-08-02：GridN 对齐生成时的模拟 n（用户要求"游戏看的格子数=生成用的格子数"）。
            //   球面存档顶点数 = 10n²+2（Icosahedron 细分）→ 反推 n = sqrt((verts-2)/10)。
            //   Goldberg hex 格数 = 10×GridN²+2 → GridN=n 时两者恰好相等（10242 格/10242 顶点）。
            if (_map.IsSpherical && _map.Verts != null)
            {
                int simN = Icosahedron.GridNFromVertexCount(_map.Verts.Length);
                if (simN >= 8 && simN <= 512 && simN != _gridN)
                {
                    LogService.Log("MapViewer", $"存档模拟 n={simN}（{_map.Verts.Length} 顶点）→ GridN 对齐 {simN}");
                    _gridN = simN;
                }
            }

            // 星球半径：读档口径（.mpa v5 头 / .cmp 快照；旧档默认地球 6371）。
            // 显示几何 + 相机轨道距离全 ∝ R，读档后必须应用，否则小星球按地球半径显示。
            if (Mathf.Abs(RadiusKm - _map.RadiusKm) > 1e-3f)
            {
                RadiusKm = _map.RadiusKm;
                GetNode<OrbitalCamera>("OrbitalCamera")?.SetPlanetRadius(RadiusKm);
                LogService.Log("MapViewer", $"星球半径 R={RadiusKm:F0} km（存档口径）");
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
                LogService.Log("MapViewer", $"流域 {wsCount + 1} 个（现场算）");
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
                // 后台线程回调：LogService 纪律禁止，保持 GD.Print 直调（ADR-0004 §决策4）
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
        Color[] colors;
        // ⚠️ 2026-08-20：独立势力图层（14）用带边界 A 通道的颜色构建（M3：NeedsPowerBorders 策略属性）
        if (LayerRegistry.Of(layer).NeedsPowerBorders && _cache.TilePower != null)
        {
            colors = ChunkMeshBuilder.BuildColorsWithPowerBorders(tiles, MakeColorFn(layer), geometry,
                _cache.TilePower,
                p =>
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);
                    _progress = 0.65f + p * 0.25f;
                });
        }
        else
        {
            colors = ChunkMeshBuilder.BuildColors(tiles, MakeColorFn(layer), geometry,
                p =>
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);
                    _progress = 0.65f + p * 0.25f;
                });
        }
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
        _cache = new TileDataCache();   // 2026-08-21 M1：每格图层值缓存束
        _cache.TileElev = new float[n];
        _cache.TileTemp = new float[n];
        _cache.TilePrecip = new float[n];
        _cache.TileBiome = new byte[n];
        _cache.TileWind = new Vector3[n];
        _cache.TileLake = new byte[n];
        _cache.TileWatershed = new int[n];
        System.Array.Fill(_cache.TileWatershed, -1);
        _cache.TileMineral = new byte[n];
        _cache.TileSoil = new byte[n];
        _cache.TileMonsoon = new byte[n];
        _cache.TileMonthPrecip = new byte[n];
        _cache.TileMonthTemp = new byte[n];
        _cache.TilePop = new float[n];
        _cache.TileCulture = new int[n];
        _cache.TileCultureGroup = new byte[n];
        _cache.TileReligion = new int[n];
        _cache.TileBand = new int[n];
        _cache.TilePower = new int[n];     // 独立势力 id（2026-08-17；0=无）
        _cache.TilePolity = new byte[n];   // 政体类型（2026-08-17；0=band 1=部落 2=酋邦）
        _cache.TileTechEpoch = new byte[n];
        _cache.TileTerritory = new int[n];
        _cache.TileSettlement = new byte[n];
        System.Array.Fill(_cache.TileBand, -1);
        bool hasCiv = _civCtx != null;
        // 2026-08-10 影响力场模型（v8）：band 实体只在驻扎点格，领地=归属格——文明图层改为
        // **归属格主导**（每格查 CellOwner → 该 band 的文化/宗教/部落/科技；人口=领地均摊，5 km² 量级）
        var civIdMap = new System.Collections.Generic.Dictionary<int, Band>();
        if (hasCiv)
            foreach (var ce in _civCtx.Bands)
                if (!ce.Dead) civIdMap[ce.Id] = ce;
        bool hasTemp = map.Temp != null, hasPrecip = map.Precip != null, hasBiome = map.Biome != null;
        float range = map.MaxElev - map.MinElev;
        float hSea = range > 1e-6f ? -map.MinElev / range : 0.5f;
        var elevArr = _cache.TileElev;   // 局部引用（后台线程安全：不同下标不同位置）
        var tempArr = _cache.TileTemp;
        var precipArr = _cache.TilePrecip;
        var biomeArr = _cache.TileBiome;
        var windArr = _cache.TileWind;
        var lakeArr = _cache.TileLake;
        var wsArr = _cache.TileWatershed;
        var minArr = _cache.TileMineral;
        var soilArr = _cache.TileSoil;
        var monsoonArr = _cache.TileMonsoon;
        bool hasLake = map.LakeLevel != null;
        bool hasMineral = map.MineralLevel != null;
        var centers = new Vector3[n];
        for (int i = 0; i < n; i++) centers[i] = tiles[i].Center;
        // ⚠️ 2026-08-19：映射收敛——TileIndex 主线程预构建（面→顶点 + 顶点→面反查；63km 错位案根治）
        _tileIndex = new TileIndex(map, centers);
        // 2026-08-21 策略模式重构 M1：填充图层上下文（策略类数据通道；引用赋值后只读）
        // ⚠️ M3：回调闭包捕获 this——只在主线程被触发（滑块回调/ApplyMonthWind），后台只读字段
        _ctx = new LayerContext
        {
            Map = map,
            Tiles = tiles,
            TileIndex = _tileIndex,
            CivCtx = _civCtx,
            Cache = _cache,
            RadiusKm = RadiusKm,
            Month = _month,
            RequestOverlayRebuild = RecreateOverlay,
            RequestRecolor = () => RebuildColors(),
        };
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
                    //   人口只在驻扎格（Band.Cell）显示该 band 的 P（并行循环后实体表直写）。
                    //   旧"领地均摊 P/领地格数"让每个归属格都有人口，与"大部分是采集格"矛盾（用户反馈）。
                    _cache.TileCulture[i] = ShareField.KeyHash(ShareField.DomKey(dom.CultureShare));
                    _cache.TileCultureGroup[i] = (byte)(ShareField.KeyHash(ShareField.DomKey(dom.CultureGroupShare)) & 0xFF);
                    // ⚠️ 2026-08-07 宗教图层改显示"具体派别"（relig_N，每摇篮/每次漂变独立）——
                    //    旧版显示 5 段发展带（万物有灵→一神教），石器时代全在段 0 → 全图一色（用户反馈）
                    _cache.TileReligion[i] = ShareField.KeyHash(ShareField.DomKey(dom.ReligionCultShare));
                    _cache.TileBand[i] = dom.Id;
                    _cache.TileTechEpoch[i] = (byte)dom.Epoch;   // 0=旧石器 1=新石器（反应性标签）
                    // 独立势力（2026-08-18 v4 回影响力场——用户确认：按影响力算难出飞地、
                    //   且不可能被中立隔离（中立只在圈外——同势力格都在圈内）。v3 的 BFS 2 跳
                    //   人为切圈是魔法数字——废弃。飞地=强邻切通道的少数构型——统计验证）
                    _cache.TilePower[i] = PowerIdOf(dom);
                    _cache.TilePolity[i] = PolityOf(dom);
                    // 势力范围：主导 band 的语言群 key 完整 32 位哈希（同领地必同语言群 → 同领地同色；
                    //    与 byte 截断的 _cache.TileCultureGroup 区分，防 8 位撞色）
                    _cache.TileTerritory[i] = ShareField.KeyHash(ShareField.DomKey(dom.CultureGroupShare));
                    // 聚落（2026-08-19 阶段3：Cell → 等级/废墟；1-4=新村~城市 5=废墟）
                    if (settlementByCell.TryGetValue(vid2, out byte slevel)) _cache.TileSettlement[i] = slevel;
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
        if (_cache.TilePower != null)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < n; i++) if (_cache.TilePower[i] != 0) set.Add(_cache.TilePower[i]);
            _cache.PowerPalette = PowerPalette.Build(set);
        }
        // ⚠️ 2026-08-16 势力范围调色板（同 PowerPalette 最远点采样）：旧版 HslToRgb(GoldenHue,0.55,0.85)
        //   明度 0.85 → 所有领地色 RGB 挤在 0.77-0.93 近白区间（用户反馈"势力范围地图全是白的"）；
        //   且黄金角散列在 ~1500 领地规模下斐波那契距 key 对近撞色相。改为最远点采样调色板
        //   （任意两领地色距有下界 + 与海色/无领地灰可分）。
        if (_cache.TileTerritory != null)
        {
            var tset = new HashSet<int>();
            for (int i = 0; i < n; i++) if (_cache.TileTerritory[i] != 0) tset.Add(_cache.TileTerritory[i]);
            _cache.TerritoryPalette = PowerPalette.Build(tset);
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
            for (int e = 0; e < _civCtx.Bands.Count; e++)
            {
                var ce = _civCtx.Bands[e];
                if (ce.Dead || ce.Cell < 0 || ce.Cell >= n) continue;
                if (_civCtx.R != null && _civCtx.R[ce.Cell] <= 0f) continue;   // 逻辑陆地（与模拟一致）
                _cache.TilePop[ce.Cell] += ce.P;   // 驻扎格实有人口（营地）——只有人的格显示人
            }
        }   // end if(hasCiv)
        // 人口图层自适应归一化（相对本图分布——分位数模型，用户拍板风格）：
        // log(p+1) 压缩重尾 + 有人陆地格 P1/P99 分位为色带端点 → 单格超大城市不拉爆、
        // 最小聚落也有可见色（旧版 log(全局max) 归一：全球最大值单点把其余全压成近黑色）
        var popLog = new System.Collections.Generic.List<float>();
        _cache.PopMax = 0f;
        for (int i = 0; i < n; i++)
        {
            if (_cache.TilePop[_tileIndex.FaceToVertex(i)] <= 0f) continue;     // 无人格不入带（人口按顶点写——显示格 i 读其顶点）
            popLog.Add(Mathf.Log(_cache.TilePop[_tileIndex.FaceToVertex(i)] + 1f));
            if (_cache.TilePop[_tileIndex.FaceToVertex(i)] > _cache.PopMax) _cache.PopMax = _cache.TilePop[_tileIndex.FaceToVertex(i)];
        }
        if (popLog.Count >= 2)
        {
            popLog.Sort();
            int p1 = popLog.Count / 100;
            int p99 = popLog.Count - 1 - popLog.Count / 100;
            _cache.PopLogMin = popLog[p1];
            _cache.PopLogMax = Mathf.Max(_cache.PopLogMin + 1f, popLog[p99]);   // 防退化（全同值）
        }
        else { _cache.PopLogMin = 0f; _cache.PopLogMax = 1f; }   // 无人口数据 → 全图灰
        // ⚠️ 2026-08-16：年降水自适应色带 min/max（用户拍板：最低到最高归一化，不用固定 2000mm）
        _cache.PrecipMin = float.MaxValue;
        _cache.PrecipMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (_ctx.IsSea(i)) continue;   // ⚠️ 2026-08-17：统一海陆判定（只统计陆地格）
            _cache.PrecipMin = Mathf.Min(_cache.PrecipMin, precipArr[i]);
            _cache.PrecipMax = Mathf.Max(_cache.PrecipMax, precipArr[i]);
        }
        if (_cache.PrecipMax <= _cache.PrecipMin) _cache.PrecipMax = _cache.PrecipMin + 1f;
        _cache.HSea = hSea;
    }

    private volatile bool _pendingRecolor;   // 构建中切图层 → 完成后自动重算

    /// <summary>主线程：把后台构建好的数据包成 ArrayMesh 并挂载。
    /// ⚠️ 2026-08-16 进度条重设计：BuildAll 到 90%，这里收尾到 100%（100% = 真正完成，
    ///   消除"进度满但主线程还有活"的未响应感）。</summary>
    private void FinishGenerate(int version)
    {
        LogService.Log("MapViewer", $"FinishGenerate v{version} (当前 _buildVersion={_buildVersion}, Layer={_layer})");
        if (version != _buildVersion)
            return; // 用户在生成期间又改了 GridN，丢弃过期结果

        try
        {
            var data = _buildTask.Result;
            if (data.Verts == null)
            {
                LogService.Log("MapViewer", "build cancelled (superseded by newer request)");
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
            LogService.Log("MapViewer", $"sphere ready: {data.Indices.Length / 3} tris (tiles={_tiles?.Count ?? 0}) (CreateMesh {sw.ElapsedMilliseconds}ms)");
            LogService.Log("MapViewer", $"BuildAll 阶段耗时: {_buildDiag}");

            // 覆盖层（风场箭头/洋流/河流；2026-08-21 M3 策略化——BuildOverlay 由策略实现）
            // ⚠️ 2026-08-16：EnsureMonthWind 已异步化——风场层此刻可能未就绪（ApplyMonthWind 补建）
            _phase = "构建覆盖层";
            _progress = 0.94f;
            sw.Restart();
            foreach (var l in LayerRegistry.All)
                if (l.HasOverlay)
                    EnsureOverlayFor(l.Id);
            LogService.Log("MapViewer", $"收尾: 覆盖层 {sw.ElapsedMilliseconds}ms");
            // 月降水/月温度缓存（当前月；M3：迁至 LayerContext）
            _ctx.RefreshMonthPrecip();
            _ctx.RefreshMonthTemp();
            LogService.Log("MapViewer", $"收尾: 月缓存 {sw.ElapsedMilliseconds}ms");

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
            // ⚠️ 2026-08-03：headless 验证构建完成即退（原 BuildRivers 尾部；M3 策略化后统一收尾退出）
            if (OS.HasFeature("headless"))
                GetTree().Quit();
        }
        catch (Exception e)
        {
            // ⚠️ 2026-08-02：任务被取消（切图层/重建）时 Result 抛 AggregateException
            //   （Inner = OperationCanceledException）——正常路径，不误报。
            if (e is AggregateException ae && ae.InnerException is OperationCanceledException)
            {
                LogService.Log("MapViewer", "build cancelled (superseded by newer request)");
                return;
            }
            LogService.LogErr("MapViewer", $"finish failed: {e}\n{e.StackTrace}");
        }
        finally
        {
            HideProgress();
        }
    }

    /// <summary>构建指定图层覆盖层（懒建幂等；风场异步未就绪则等待 ApplyMonthWind 补建）。
    /// 2026-08-21 M3 策略化：BuildOverlay 由策略实现，节点由策略自持（OverlayNode），切图层只切 Visible。</summary>
    private void EnsureOverlayFor(int layerId)
    {
        if (_ctx == null) return;   // 构建前（_Ready 早期）不建
        var strat = LayerRegistry.Of(layerId);
        if (!strat.HasOverlay || strat.OverlayNode != null) return;
        var node = strat.BuildOverlay(_ctx, this);
        if (node == null) return;   // 数据未就绪（如风场异步中）→ 等回调补建
        strat.OverlayNode = node;
        node.Visible = (layerId == _layer);
        AddChild(node);
        LogService.Log("MapViewer", $"overlay built: {strat.Name} (id={layerId})");
    }

    /// <summary>销毁当前层覆盖层并重建（月份切换等；策略 OnMonthChanged 经 ctx.RequestOverlayRebuild 回调）。</summary>
    private void RecreateOverlay()
    {
        if (_ctx == null) return;
        var s = LayerRegistry.Of(_layer);
        if (s.OverlayNode != null)
        {
            s.OverlayNode.QueueFree();
            s.OverlayNode = null;
        }
        if (s.HasOverlay)
            EnsureOverlayFor(_layer);
    }

    /// <summary>懒算季风月风场（读档后第一次进风场/月降水/月温度图层时算一次；不存档）。
    /// 用存档的海陆/年温/年降水 + 倾角（v3.8 头部）现场跑 MonsoonSystem。
    /// ⚠️ 2026-08-16：异步化（后台 Task.Run）——n=128 时 MonsoonSystem 数亿次主线程计算
    ///   让 FinishGenerate 卡 100% 几十秒。MonsoonSystem 是纯计算（不碰引擎 API，线程安全）；
    ///   完成后 CallDeferred 回主线程应用。实现见 MapViewer.Visuals.cs（M3：internal 供 WindLayer 触发）。</summary>
    private bool _monthWindStarted;                 // 防重复启动
    private volatile Vector3[][] _monthWindPending; // 后台写、主线程 ApplyMonthWind 读

    /// <summary>图层按钮 SVG 图标（纯直线 M/L/H/V/Z——thorvg 不支持 Q/T/A 曲线）。</summary>

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
        var col = _ctx != null ? LayerRegistry.Of(_layer).ColorOf(_ctx, _tiles[i]) : Colors.Magenta;   // 2026-08-21 M3：策略取色
        LogService.Log("CLICK", $"格={i} 图层={LayerRegistry.Of(_layer).Name} 颜色=#{col.ToHtml()} pos=({_tiles[i].Center.X:F2},{_tiles[i].Center.Y:F2},{_tiles[i].Center.Z:F2})");
        int vid = _tileIndex != null ? _tileIndex.FaceToVertex(i) : i;   // ⚠️ 2026-08-18：显示格→逻辑格（顶点）映射（2026-08-19 收敛 TileIndex）
        float elevM2 = _map.Elev != null ? _map.Elev[vid] : (_cache.TileElev[i] - _cache.HSea) * (_map.MaxElev - _map.MinElev);   // 实际海拔（米）
        // 续行诊断：保持 GD.Print 直调（[CLICK] 主行的格式续行，非日志，ADR-0004 §决策5）
        GD.Print($"  elev={_cache.TileElev[i]:F3} 海拔={elevM2:F0}m pop={_cache.TilePop[vid]:F1} power={_cache.TilePower[i]} polity={_cache.TilePolity[i]} band={_cache.TileBand[i]} terr={_cache.TileTerritory[i]} culture={_cache.TileCulture[i]} religion={_cache.TileReligion[i]}");
        // ⚠️ 2026-08-18：势力统计——该格所属势力总格数/有人格数（当场判断"无人口势力" vs "采集格无人"）
        if (_cache.TilePower[i] != 0)
        {
            int pow = _cache.TilePower[i], pCells = 0, pPop = 0;
            for (int j = 0; j < _tiles.Count; j++)
                if (_cache.TilePower[j] == pow) { pCells++; if (_cache.TilePop[_tileIndex.FaceToVertex(j)] > 0f) pPop++; }
            GD.Print($"  势力{pow}: 共{pCells}格 / 有人口{pPop}格（pPop=0 ⇒ 无人口势力=异常；pPop>0 ⇒ 本格是采集格=设计）");
        }
        if (_civCtx != null)
        {
            GD.Print($"  CellOwner={(_civCtx.CellOwner != null ? _civCtx.CellOwner[vid] : -1)} R={(_civCtx.R != null ? _civCtx.R[vid] : -1f):F2} LockedUntil={(_civCtx.LockedUntil != null ? _civCtx.LockedUntil[vid] : 0)}");
            foreach (var ce in _civCtx.Bands)
                if (!ce.Dead && ce.Cell == vid)
                    GD.Print($"  驻扎实体={ce.Id} P={ce.P:F1} CarryMult={ce.CarryMult:F2} TSize={ce.TerritorySize} TerrId={ce.TerritoryId} ChiefdomId={ce.ChiefdomId} Prestige={ce.Prestige:F2}");
            // ⚠️ 2026-08-18：该格所属势力（CellOwner）的驻扎格 + 位置 + 可达距离（BFS 走逻辑陆地）
            int ownerId = _civCtx.CellOwner != null ? _civCtx.CellOwner[vid] : -1;
            Band owner = null;
            foreach (var ce in _civCtx.Bands)
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

}

// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
// Slices (2026-08-19 pure refactor: partial class, behavior unchanged):
//   MapViewer.Colors.cs   - coloring (MakeColorFn/RebuildColors/BuildColorsTask/PowerColor/FamilyColor/MakeLayerIcon)
//   MapViewer.Visuals.cs  - overlay geometry (monsoon/current arrows, rings, rivers, month refreshes, IsDisplaySea)
//   MapViewer.Ui.cs       - UI/legend (progress, EnsureUi, layer buttons, legend rows, BuildIdentityCaches, FmtPop)
// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
