using System;

namespace World.Services;

/// <summary>
/// 跨场景事件总线（L2 服务层，ADR-0002）。
/// 场景间通信的唯一通道：发布/订阅解耦，替代 ViewerLauncher 静态传值。
/// 线程纪律：进度事件由后台线程发布、主线程消费（沿用项目既有 volatile 模式）。
/// </summary>
public static class EventBus
{
    /// <summary>请求打开地图查看器（携带存档路径）。</summary>
    public static event Action<string> MapViewRequested;

    /// <summary>请求进入文明演化（携带要预选的 .mpa 路径；缺失则让演化页自己列全部）。</summary>
    public static event Action<string> CivEvolveRequested;

    /// <summary>请求进入"正式游玩"选图（主菜单游玩按钮 → SaveSelectMenu 游玩模式）。</summary>
    public static event Action GameplaySelectRequested;

    /// <summary>本次地图请求标记为"正式游玩"（MapViewer 消费——浏览/游玩形态切换）。</summary>
    public static event Action GameplayMapRequested;

    /// <summary>请求进入"加载存档"列表（主菜单加载按钮 → SaveSelectMenu 存档模式）。</summary>
    public static event Action LoadSelectRequested;

    /// <summary>生成进度（0..1）。</summary>
    public static event Action<float> GenerationProgress;

    /// <summary>生成完成（是否成功, 存档路径）。</summary>
    public static event Action<bool, string> GenerationFinished;

    // ── 第二阶段"正式游玩"模式标记（2026-08-25：主菜单双入口——查看地图 / 正式游玩）──
    // 待消费布尔（场景切换时序：发布时新场景尚未实例化，事件会错过——同 _pendingMapViewPath 模式）：
    //   RequestGameplaySelect → SaveSelectMenu._Ready 决定列表模式（.cmp 游戏档）
    //   MarkGameplayMap → MapViewer._Ready 决定浏览/游玩形态（选国家/活世界驱动为下一刀）
    private static bool _pendingPlaySelect;
    private static bool _pendingMapPlay;
    private static bool _pendingLoadSelect;

    /// <summary>请求进入"正式游玩"选图（主菜单游玩按钮 → SaveSelectMenu 游玩模式）。</summary>
    public static void RequestGameplaySelect()
    {
        _pendingPlaySelect = true;
        GameplaySelectRequested?.Invoke();
    }

    /// <summary>SaveSelectMenu._Ready 消费游玩选图标记（取后清空；false = 浏览模式）。</summary>
    public static bool ConsumeGameplaySelect()
    {
        var v = _pendingPlaySelect;
        _pendingPlaySelect = false;
        return v;
    }

    /// <summary>标记本次地图请求为"正式游玩"（MapViewer 消费；路径仍走 RequestMapView）。</summary>
    public static void MarkGameplayMap()
    {
        _pendingMapPlay = true;
        GameplayMapRequested?.Invoke();
    }

    /// <summary>MapViewer._Ready 消费游玩标记（取后清空）。</summary>
    public static bool ConsumeGameplayMap()
    {
        var v = _pendingMapPlay;
        _pendingMapPlay = false;
        return v;
    }

    /// <summary>请求进入"加载存档"（主菜单加载按钮 → SaveSelectMenu 存档模式 .sav 列表）。</summary>
    public static void RequestLoadSelect()
    {
        _pendingLoadSelect = true;
        LoadSelectRequested?.Invoke();
    }

    /// <summary>SaveSelectMenu._Ready 消费加载存档标记（取后清空；false = 浏览/游玩模式）。</summary>
    public static bool ConsumeLoadSelect()
    {
        var v = _pendingLoadSelect;
        _pendingLoadSelect = false;
        return v;
    }

    // 场景切换时序：发布时新场景尚未实例化，事件会错过——
    // 保留"待消费值"（语义同旧 ViewerLauncher.PendingPath），MapViewer._Ready 消费。
    private static string _pendingMapViewPath;

    /// <summary>请求查看地图：记录待消费路径并广播。</summary>
    public static void RequestMapView(string path)
    {
        _pendingMapViewPath = path;
        MapViewRequested?.Invoke(path);
    }

    /// <summary>MapViewer._Ready 消费待消费路径（取后清空）。</summary>
    public static string ConsumeMapViewRequest()
    {
        var p = _pendingMapViewPath;
        _pendingMapViewPath = null;
        return p;
    }

    // 文明演化衔接：生成页「文明演化」按钮 → 演化页 _Ready 消费（预选刚生成的 .mpa）
    private static string _pendingCivEvolvePath;

    /// <summary>请求文明演化：记录待预选 .mpa 路径并广播。</summary>
    public static void RequestCivEvolve(string mapPath = null)
    {
        _pendingCivEvolvePath = mapPath;
        CivEvolveRequested?.Invoke(mapPath);
    }

    /// <summary>CivEvolveMenu._Ready 消费待预选路径（取后清空）。</summary>
    public static string ConsumeCivEvolveRequest()
    {
        var p = _pendingCivEvolvePath;
        _pendingCivEvolvePath = null;
        return p;
    }

    // ── 选国场景衔接（2026-08-31：SaveSelectMenu 游玩模式 → NationSelect）──
    // 待消费对（场景切换时序：发布时新场景尚未实例化，事件会错过——同 _pendingMapViewPath 模式）：
    //   RequestNationSelect → NationSelect._Ready 消费路径+标记
    //   RequestPlayerBind → 策略层消费玩家绑定政权（-1=无）
    private static string _pendingNationSelectPath;
    private static bool _pendingNationSelectFlag;
    private static int _pendingPlayerBind = -1;

    /// <summary>请求选国（SaveSelectMenu 游玩模式进入选国）：记录待消费路径与标记。</summary>
    public static void RequestNationSelect(string path)
    {
        _pendingNationSelectPath = path;
        _pendingNationSelectFlag = true;
    }

    /// <summary>NationSelect._Ready 消费待选国路径（取后清空）。</summary>
    public static string ConsumeNationSelectPath()
    {
        var p = _pendingNationSelectPath;
        _pendingNationSelectPath = null;
        return p;
    }

    /// <summary>NationSelect._Ready 消费选国标记（取后清空；false = 非选国进入）。</summary>
    public static bool ConsumeNationSelectFlag()
    {
        var v = _pendingNationSelectFlag;
        _pendingNationSelectFlag = false;
        return v;
    }

    /// <summary>请求把当前形态绑定到某政权（选国选中后写；策略层读——-1=未绑定）。</summary>
    public static void RequestPlayerBind(int stateId)
    {
        _pendingPlayerBind = stateId;
    }

    /// <summary>消费玩家绑定政权 Id（取后复位 -1）。</summary>
    public static int ConsumePlayerBind()
    {
        var v = _pendingPlayerBind;
        _pendingPlayerBind = -1;
        return v;
    }

    public static void PublishProgress(float value) => GenerationProgress?.Invoke(value);

    public static void PublishFinished(bool ok, string path) => GenerationFinished?.Invoke(ok, path);
}
