using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Biome;
using World.MapGen;
using World.HexPlanet;
using World.PlanetLOD;
using World.Surface;

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
            if (_layer == value) return;
            _layer = value;
            SyncLayerButtons(); // UI 存在时同步按钮按下态（不触发重建）
            if (IsInsideTree()) RebuildColors();
        }
    }
    private int _layer;

    // ── 几何缓存（GridN 变化时失效）──
    private List<HexTile> _tiles;
    private GeometryData _geometry;
    private volatile bool _geometryReady; // 后台写（BuildAll 内），主线程 RebuildColors 读

    // ── 异步生成状态 ──
    private Task<MeshData> _buildTask;
    private volatile float _progress;   // 0..1，后台线程写、主线程读
    private volatile string _phase = ""; // 当前阶段文字
    private int _buildVersion;           // 递增；过期任务的 FinishGenerate 直接丢弃

    // ── 进度条 UI ──
    private CanvasLayer _uiLayer;
    private PanelContainer _panel;
    private ProgressBar _bar;
    private Label _label;
    private Button[] _layerButtons;

    private static readonly string[] LayerNames = { "海拔", "温度", "降水", "生物群系" };

    public override void _Ready()
    {
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
        _ => "biome",
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
        _progress = 0f;
        _phase = "准备生成";
        ShowProgress();

        _buildTask = Task.Run(() => BuildAll(_map, version));
        _buildTask.ContinueWith(t =>
        {
            // 线程池回调里只做线程安全的事：失败打印 + CallDeferred 回主线程
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] build failed: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }

    /// <summary>后台线程：纯数据构建（不碰任何 Godot 对象）。</summary>
    private MeshData BuildAll(MapData map, int version)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _phase = "细分二十面体";
        _progress = 0.05f;
        Icosahedron.Subdivide(GridN, RadiusKm, out var verts, out var indices);
        if (version != _buildVersion) return default;
        _progress = 0.2f;

        _phase = "构建格子拓扑 (Goldberg dual)";
        var mesh = new SubdividedMesh(verts, indices);
        var tiles = new GoldbergBuilder(mesh, RadiusKm, p => _progress = 0.2f + p * 0.3f).Tiles;
        if (version != _buildVersion) return default;
        _progress = 0.5f;
        GD.Print($"[MapViewer] grid n={GridN} tiles={tiles.Count} (expect 10n²+2={10 * GridN * GridN + 2})");

        _phase = "构建几何";
        Func<Vector3, float> elevAt = _ => 0f;
        var geometry = ChunkMeshBuilder.BuildGeometry(tiles, elevAt, RadiusKm, 0f,
            p => _progress = 0.5f + p * 0.3f);
        if (version != _buildVersion) return default;

        // 几何就绪 → 缓存（图层切换直接复用），再算颜色
        _tiles = tiles;
        _geometry = geometry;
        _geometryReady = true;
        _progress = 0.8f;

        _phase = "采样并着色";
        var colors = ChunkMeshBuilder.BuildColors(tiles, MakeColorFn(map), geometry,
            p => _progress = 0.8f + p * 0.2f);

        _progress = 1f;
        _phase = "完成";
        GD.Print($"[MapViewer] data built: {geometry.Indices.Length / 3} tris in {sw.ElapsedMilliseconds}ms");
        return new MeshData
        {
            Verts = geometry.Verts,
            Normals = geometry.Normals,
            Colors = colors,
            Indices = geometry.Indices
        };
    }

    /// <summary>图层 → 颜色函数（v2 存档查表，v1 存档回退 ClimateGenerator 实时算）。</summary>
    private Func<HexTile, Color> MakeColorFn(MapData map)
    {
        var climate = new ClimateGenerator(map.Seed);
        return t =>
        {
            float h = map.SampleElevation(t.Center); // 归一化海拔 0..1
            switch (Layer)
            {
                case 1: // 温度
                    {
                        float temp = map.Temp != null
                            ? map.SampleTemperature(t.Center)
                            : climate.ComputeTemperature(t.Center, h * 2f - 1f);
                        return BiomeColors.TemperatureToColor(temp);
                    }
                case 2: // 降水
                    {
                        float p = map.Precip != null
                            ? map.SamplePrecipitation(t.Center)
                            : climate.ComputePrecipitation(t.Center, h * 2f - 1f);
                        return BiomeColors.PrecipitationToColor(p);
                    }
                case 3: // biome
                    {
                        if (map.Biome != null)
                            return BiomeColors.BiomeToColor(map.SampleBiome(t.Center));
                        float eNorm = h * 2f - 1f;
                        float temp = climate.ComputeTemperature(t.Center, h * 2f - 1f);
                        float p = climate.ComputePrecipitation(t.Center, h * 2f - 1f);
                        return BiomeColors.BiomeToColor(BiomeClassifier.Classify(eNorm, temp, p));
                    }
                default: // 海拔
                    return PlanetColors.ElevationToColor(-0.2f + 1.2f * h);
            }
        };
    }

    /// <summary>切图层：几何缓存命中 → 只重算颜色（秒级）；无缓存（首次/GridN 刚变）→ 全量。</summary>
    private void RebuildColors()
    {
        if (!_geometryReady || _tiles == null)
        {
            Generate();
            return;
        }

        int version = ++_buildVersion;
        _progress = 0f;
        _phase = "重算颜色";
        ShowProgress();
        _buildTask = Task.Run(() => BuildColorsTask(_map, version));
        _buildTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] recolor failed: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }

    /// <summary>后台线程：只重算颜色（几何复用缓存）。</summary>
    private MeshData BuildColorsTask(MapData map, int version)
    {
        var geometry = _geometry; // 已就绪（_geometryReady 保证，不碰 Godot 对象）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var colors = ChunkMeshBuilder.BuildColors(_tiles, MakeColorFn(map), geometry,
            p => _progress = 0.05f + p * 0.9f);
        _progress = 1f;
        GD.Print($"[MapViewer] recolored layer={Layer} in {sw.ElapsedMilliseconds}ms");
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
                GD.PrintErr("[MapViewer] build returned no data (cancelled?)");
                return;
            }
            var shader = GD.Load<Shader>("res://shaders/planet_detail.gdshader");
            var mi = new MeshInstance3D
            {
                Mesh = ChunkMeshBuilder.CreateMesh(data),
                MaterialOverride = new ShaderMaterial { Shader = shader }
            };
            AddChild(mi);
            GD.Print($"[MapViewer] sphere ready: {data.Indices.Length / 3} tris");
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
        hbox.Position = new Vector2(-220, -50);                    // 相对锚点偏移（4 按钮 ≈ 440 宽，居中）
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
