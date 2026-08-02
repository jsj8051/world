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
            if (_windArrows != null)
                _windArrows.Visible = (value == 4);
            if (_currentArrows != null)
                _currentArrows.Visible = (value == 5);
            if (_riverMesh != null)
                _riverMesh.Visible = (value == 6);
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

    // 盛行风箭头（图层 4 显示；稀疏采样网格，非每格）
    private MeshInstance3D _windArrows;
    // 洋流箭头（图层 5 显示；红=暖流 蓝=寒流）
    private MeshInstance3D _currentArrows;
    // 河流（图层 6 显示；每条河独立颜色，支流汇合截断）
    private MeshInstance3D _riverMesh;

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

    private static readonly string[] LayerNames = { "海拔", "温度", "降水", "生物群系", "盛行风", "洋流", "河流" };

    public override void _Ready()
    {
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
        4 => "盛行风",
        5 => "洋流",
        _ => "河流",
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
            if (!MapArchive.Read(_mapPath, out var map))
            {
                GD.PrintErr($"[MapViewer] failed to load {_mapPath}");
                return;
            }
            _map = map;
            _mapLoaded = true;
            GD.Print($"[MapViewer] loaded seed={map.Seed} {map.Width}x{map.Height} elev[{map.MinElev:F3},{map.MaxElev:F3}]");

            // ⚠️ 2026-08-02：GridN 对齐生成时的模拟 n（用户要求"游戏看的格子数=生成用的格子数"）。
            //   球面存档顶点数 = 10n²+2（Icosahedron 细分）→ 反推 n = sqrt((verts-2)/10)。
            //   Goldberg hex 格数 = 10×GridN²+2 → GridN=n 时两者恰好相等（10242 格/10242 顶点）。
            if (map.IsSpherical && map.Verts != null)
            {
                int simN = (int)Mathf.Round(Mathf.Sqrt((map.Verts.Length - 2) / 10f));
                if (simN >= 8 && simN <= 512 && simN != _gridN)
                {
                    GD.Print($"[MapViewer] 存档模拟 n={simN}（{map.Verts.Length} 顶点）→ GridN 对齐 {simN}");
                    _gridN = simN;
                }
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
        bool hasTemp = map.Temp != null, hasPrecip = map.Precip != null, hasBiome = map.Biome != null;
        float range = map.MaxElev - map.MinElev;
        float hSea = range > 1e-6f ? -map.MinElev / range : 0.5f;
        var elevArr = _tileElev;   // 局部引用（后台线程安全：不同下标不同位置）
        var tempArr = _tileTemp;
        var precipArr = _tilePrecip;
        var biomeArr = _tileBiome;
        var windArr = _tileWind;
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
            elevArr[i] = map.NormalizedElev(map.Elev[vid]);
            tempArr[i] = hasTemp ? map.Temp[vid] : 0f;
            precipArr[i] = hasPrecip ? map.Precip[vid] : 0f;
            biomeArr[i] = hasBiome ? map.Biome[vid] : (byte)BiomeType.DeepOcean;
            windArr[i] = World.Biome.WindField.WindAt(c);
        });
        _hSea = hSea;
    }
    private float _hSea = 0.5f;

    /// <summary>图层 → 颜色函数（查预计算缓存，零采样）。
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
    			case 2: // 降水
    				return BiomeColors.PrecipitationToColor(_tilePrecip[id]);
    			case 3: // biome
    				return BiomeColors.BiomeToColor((BiomeType)_tileBiome[id]);
    			case 4: // 盛行风：浅色底（箭头由 _windArrows 3D 网格显示）
    			case 5: // 洋流：浅色底（箭头由 _currentArrows 3D 网格显示）
    			case 6: // 河流：浅色底（河道由 _riverMesh 3D 网格显示）
    				{
    					// 淡色底：海洋浅蓝、陆地浅黄绿（低对比，突出箭头）
    					float h = _tileElev[id];
    					bool ocean = h < _hSea;
    					return ocean ? new Color(0.45f, 0.55f, 0.70f) : new Color(0.72f, 0.68f, 0.55f);
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

            // 盛行风箭头网格（图层 4 显示；稀疏采样，非每格）
            BuildWindArrows();
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

    // ── 盛行风箭头网格（图层 4 显示）──
    // 稀疏采样：纬度每 ~12° 一环，每环按经度均匀分布（极区环点数少）。
    // 每个箭头 = 实体三角形（ArrayMesh + unshaded 亮橙），贴球面。
    // ⚠️ 2026-08-02 v2：线条(1px)正对相机退化成点→屏幕中间看不到；
    //   白色线条与浅色底接近→看不清。改实体三角形+亮橙 unshaded 材质。
    private void BuildWindArrows()
    {
        if (_windArrows != null)
        {
            _windArrows.QueueFree();
            _windArrows = null;
        }
        if (_tileWind == null || _tiles == null) return;

        World.Biome.WindField.Prograde = _map.ProgradeRotation;   // 自转方向 → 风向
        World.Biome.WindField.RotationSpeed = _map.RotationSpeed; // 自转速度 → 科里奥利强度
        const float arrowLen = 0.09f;    // 箭头长度（球面弧比例，半径 1）
        const float tailW = 0.035f;      // 尾半宽
        // ⚠️ 浮在球面上方 1%（半径×1.01）：顶点与球面同一半径会 z-fighting
        //   （深度冲突，球面把箭头盖掉 → 完全看不到，2026-08-02 修复）
        float radius = RadiusKm * 1.01f;

        var verts = new System.Collections.Generic.List<Vector3>();
        var indices = new System.Collections.Generic.List<int>();

        // 纬度环采样（±84°，步 12°→15 环）；每环经度点数随 cos(lat) 递减（极区少点）
        for (float lat = -84f; lat <= 84f; lat += 12f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(8, Mathf.RoundToInt(36 * cosLa));   // 赤道 36，极区 8
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                var dir = new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo));
                var wind = World.Biome.WindField.WindAt(dir);   // 单位切向量（指向下风向）
                var side = dir.Cross(wind).Normalized();        // 垂直风向的切向

                // 箭头三角形：尾(宽) → 尖(窄)，在球面切平面内
                // ⚠️ 先构建平面三角形（含侧向偏移）再投影回球面——直接 Normalized
                //   会把侧移(0.035 vs 半径 6330)吃掉 → 三点重合退化成线
                Vector3 tailC = dir - wind * arrowLen * 0.35f;
                Vector3 tip = dir + wind * arrowLen * 0.65f;
                Vector3 t1 = (tailC + side * tailW).Normalized() * radius;
                Vector3 t2 = (tailC - side * tailW).Normalized() * radius;
                Vector3 tipS = tip.Normalized() * radius;

                int baseIdx = verts.Count;
                verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            }
        }

        // ArrayMesh：位置 + 索引
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // unshaded 亮橙材质（不随光照变暗；双面渲染）
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.55f, 0.15f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        _windArrows = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 4),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_windArrows);
        GD.Print($"[MapViewer] wind arrows built: {verts.Count / 3} arrows");
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
        int ringCount = 0;

        // ── 种子点：均匀球面网格（10° 环 × 36 经度）──
        var seeds = new System.Collections.Generic.List<Vector3>();
        for (float lat = -85f; lat <= 85f; lat += 10f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(8, Mathf.RoundToInt(36 * cosLa));
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                seeds.Add(new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo)));
            }
        }

        // ── 阶段1：追踪所有闭合环（不绘制）──
        //   ⚠️ 2026-08-02 v6：去重改【环带重叠检测】——原中心点距离对不规则环流圈
        //   （被大陆挤压成非圆）不可靠，内外圈中心算偏 → 重复画同一环。
        var rings = new System.Collections.Generic.List<System.Collections.Generic.List<Vector3>>();
        foreach (var seed in seeds)
        {
            var line = new System.Collections.Generic.List<Vector3> { seed };
            Vector3 pos = seed;
            bool closed = false;
            bool valid = true;
            for (int s = 0; s < 400; s++)
            {
                int id = _map.NearestVertex(pos);
                if (_map.SampleSpherical(pos, _map.Elev) >= 0f) { valid = false; break; }  // 碰陆地
                Vector3 dir = _map.CurrentDirs[id];
                if (dir.LengthSquared() < 1e-9f) { valid = false; break; }                 // 无洋流
                Vector3 next = (pos + dir * 0.025f).Normalized();
                // 闭合检测：回到起点附近（环流圈闭合）
                if (line.Count > 15 && (next - seed).Length() < 0.08f) { closed = true; break; }
                line.Add(next);
                pos = next;
            }
            if (!valid || !closed || line.Count < 25) continue;   // 只收闭合环
            rings.Add(line);
        }

        // ── 阶段2：重叠去重（每环只留最外层 = 周长最大）──
        //   环带重叠检测：环 A 采样点是否接近环 B 任一点（< 0.18 rad ≈ 10°）
        //   → 视为描述同一环流圈，保留周长大的（最外层）
        rings.Sort((a, b) => b.Count.CompareTo(a.Count));   // 周长降序（大环优先）
        var kept = new System.Collections.Generic.List<System.Collections.Generic.List<Vector3>>();
        bool Overlaps(System.Collections.Generic.List<Vector3> a, System.Collections.Generic.List<Vector3> b)
        {
            int hit = 0, total = 0;
            int stepA = Mathf.Max(1, a.Count / 12);   // 采样 12 点
            foreach (var pa in a)
            {
                if (total++ >= 12) break;
                foreach (var pb in b)
                {
                    if (Mathf.Acos(Mathf.Clamp(pa.Dot(pb), -1f, 1f)) < 0.18f) { hit++; break; }
                }
            }
            return total > 0 && hit >= total / 2;   // 一半点接近 → 同一环
        }
        foreach (var r in rings)
        {
            bool dup = false;
            foreach (var k in kept)
            {
                if (Overlaps(r, k)) { dup = true; break; }
            }
            if (!dup) kept.Add(r);
        }
        ringCount = kept.Count;
        GD.Print($"[MapViewer] 洋流环：追踪 {rings.Count} 个，去重后保留 {kept.Count} 个");

        // ── 阶段3：绘制保留的环（离散小箭头串）──
        //   每 6 步一个独立箭头（间隔 6×0.025=0.15 rad ≈ 8.6°），方向 = 流动方向。
        //   ⚠️ 2026-08-02 v7：颜色改【每箭头按位置冷暖】——环平均会让整个环流圈
        //   一个色，丢失"环流圈西暖东寒"（湾流暖/加那利寒是同一环的两侧）。
        const float arrowLen = 0.028f;
        const float arrowTailW = 0.013f;
        foreach (var line in kept)
        {
            int n = line.Count;
            for (int i = 0; i < n; i += 6)
            {
                Vector3 a = line[i];
                Vector3 b = line[(i + 6) % n];   // 闭合：尾部连回首部
                Vector3 seg = b - a;
                if (seg.LengthSquared() < 1e-12f) continue;
                Vector3 dir = seg.Normalized();
                Vector3 side = seg.Cross(a).Normalized();

                // 每箭头独立冷暖色（该位置顶点的洋流冷暖）
                int id = _map.NearestVertex(a);
                float w = _map.CurrentWarmth[id];
                Color ringColor = w > 0.05f ? new Color(1f, 0.45f, 0.2f)
                    : w < -0.05f ? new Color(0.25f, 0.55f, 1f)
                    : new Color(0.85f, 0.85f, 0.85f);

                // 独立小箭头（三角形，指向流动方向）
                Vector3 tailC = a - dir * arrowLen * 0.35f;
                Vector3 tip = a + dir * arrowLen * 0.65f;
                Vector3 t1 = (tailC + side * arrowTailW).Normalized() * radius;
                Vector3 t2 = (tailC - side * arrowTailW).Normalized() * radius;
                Vector3 tipS = tip.Normalized() * radius;
                int ai = verts.Count;
                verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                colors.Add(ringColor); colors.Add(ringColor); colors.Add(ringColor);
                indices.Add(ai); indices.Add(ai + 1); indices.Add(ai + 2);
            }
        }

        if (verts.Count == 0)
        {
            GD.Print("[MapViewer] current arrows: 无闭合环流圈");
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
        GD.Print($"[MapViewer] current arrows built: {ringCount} 闭合环流圈 (from archive)");
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
        const float halfW = 0.004f;   // 河宽（球面弧比例 ≈ 25km 观感）
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
    }

    // ── 图层 SVG 图标（纯直线命令 M/L/H/V/Z——thorvg 不支持 Q/T/A 曲线）──
    private static readonly string[] LayerSvgs =
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
    };

    private static Texture2D MakeLayerIcon(int idx)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(LayerSvgs[idx]);
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
