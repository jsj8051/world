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
            if (_layer != value)
            {
                _layer = value;
                if (IsInsideTree()) RebuildColors();
            }
            SyncLayerButtons();
            if (_windArrows != null)
                _windArrows.Visible = (value == 4);
        }
    }
    private int _layer;

    // ── 几何缓存（GridN 变化时失效）──
    private List<HexTile> _tiles;
    private GeometryData _geometry;
    private volatile bool _geometryReady; // 后台写（BuildAll 内），主线程 RebuildColors 读

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

    private static readonly string[] LayerNames = { "海拔", "温度", "降水", "生物群系", "盛行风" };

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
        _ => "盛行风",
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
        foreach (Node child in GetChildren())
            if (child is MeshInstance3D)
                child.QueueFree();

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
        }

        int version = ++_buildVersion;
        _cts?.Cancel();   // 取消旧任务（切图层/重建时旧任务立即停止）
        _cts = new System.Threading.CancellationTokenSource();
        var token = _cts.Token;
        _progress = 0f;
        _phase = "准备生成";
        ShowProgress();

        _buildTask = Task.Run(() => BuildAll(_map, version, token), token);
        _buildTask.ContinueWith(t =>
        {
            // 线程池回调里只做线程安全的事：失败打印 + CallDeferred 回主线程
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] build failed: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }

    /// <summary>后台线程：纯数据构建（不碰任何 Godot 对象）。</summary>
    private MeshData BuildAll(MapData map, int version, System.Threading.CancellationToken token)
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
        var colors = ChunkMeshBuilder.BuildColors(tiles, MakeColorFn(), geometry,
            p => _progress = 0.8f + p * 0.2f);

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

        // 盛行风图层：用存档自转方向（旧存档默认顺转）
        World.Biome.WindField.Prograde = map.ProgradeRotation;
        System.Threading.Tasks.Parallel.For(0, n, i =>
        {
            if (token.IsCancellationRequested) return;
            var c = centers[i];
            elevArr[i] = map.SampleElevation(c);
            tempArr[i] = hasTemp ? map.SampleTemperature(c) : 0f;
            precipArr[i] = hasPrecip ? map.SamplePrecipitation(c) : 0f;
            biomeArr[i] = hasBiome ? (byte)map.SampleBiome(c) : (byte)BiomeType.DeepOcean;
            windArr[i] = World.Biome.WindField.WindAt(c);
        });
        _hSea = hSea;
    }
    private float _hSea = 0.5f;

    /// <summary>图层 → 颜色函数（查预计算缓存，零采样）。</summary>
    private Func<HexTile, Color> MakeColorFn()
    {
        return t =>
        {
            int id = t.Id;
            switch (Layer)
            {
                case 1: // 温度
                    return BiomeColors.TemperatureToColor(_tileTemp[id]);
                case 2: // 降水
                    return BiomeColors.PrecipitationToColor(_tilePrecip[id]);
                case 3: // biome
                    return BiomeColors.BiomeToColor((BiomeType)_tileBiome[id]);
                case 4: // 盛行风：浅色底（箭头由 _windArrows 3D 网格显示）
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
            return;
        }

        int version = ++_buildVersion;
        _cts?.Cancel();   // 取消旧重算任务
        _cts = new System.Threading.CancellationTokenSource();
        var token = _cts.Token;
        _progress = 0f;
        _phase = "重算颜色";
        ShowProgress();
        _buildTask = Task.Run(() => BuildColorsTask(_map, version, token), token);
        _buildTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] recolor failed: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }
    private volatile bool _pendingRecolor;   // 构建中切图层 → 完成后自动重算

    /// <summary>后台线程：只重算颜色（查预计算缓存，零采样）。</summary>
    private MeshData BuildColorsTask(MapData map, int version, System.Threading.CancellationToken token)
    {
        var geometry = _geometry; // 已就绪（_geometryReady 保证，不碰 Godot 对象）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var colors = ChunkMeshBuilder.BuildColors(_tiles, MakeColorFn(), geometry,
            p => _progress = 0.05f + p * 0.9f);
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
            var mi = new MeshInstance3D
            {
                Mesh = ChunkMeshBuilder.CreateMesh(data),
                MaterialOverride = new ShaderMaterial { Shader = shader }
            };
            AddChild(mi);
            GD.Print($"[MapViewer] sphere ready: {data.Indices.Length / 3} tris (tiles={_tiles?.Count ?? 0})");

            // 盛行风箭头网格（图层 4 显示；稀疏采样，非每格）
            BuildWindArrows();

            // 构建中切了图层 → 自动应用最新图层（几何已就绪，走快速重算）
            if (_pendingRecolor)
            {
                _pendingRecolor = false;
                RebuildColors();
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[MapViewer] finish failed: {e.Message}");
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
        var group = new ButtonGroup();
        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom); // 锚点底部居中
        hbox.Position = new Vector2(-275, -50);                    // 相对锚点偏移（5 按钮 ≈ 550 宽，居中）
        _uiLayer.AddChild(hbox);

        _layerButtons = new Button[LayerNames.Length];
        for (int i = 0; i < LayerNames.Length; i++)
        {
            int idx = i; // 闭包捕获
            var btn = new Button
            {
                Text = LayerNames[i],
                ToggleMode = true,
                ButtonGroup = group,
                CustomMinimumSize = new Vector2(110, 38)
            };
            btn.Pressed += () => Layer = idx;
            hbox.AddChild(btn);
            _layerButtons[i] = btn;
        }
        SyncLayerButtons();
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
