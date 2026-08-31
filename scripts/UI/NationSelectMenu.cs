using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using World.CivSim.Observation;
using World.MapView;
using World.Services;

namespace World.UI;

/// <summary>
/// 选国场景导演（2026-08-31，设计稿见 docs/设计-国家选择界面.md）。
/// 场景职责：root 实例化 MapViewer.tscn（「Map」子节点——3D 星球全屏 + 图层坞/图例复用），
/// 本脚本只做导演/接线，单内聚不拆组件：
///   路径（EventBus 消费，--nationPath= 可覆盖 headless 冒烟）→ map.MapPath 赋值触发读档 →
///   NationMapReady 重建右侧羊皮纸列表（国家置顶分组）→ 地图 TilePicked / 列表行点击双向绑定
///   （SelectPolity：SetSelection 相机转向+高亮 + 详情卡 + 开始按钮激活）→
///   开始游玩 = RequestMapView + RequestPlayerBind + MarkGameplayMap → 重进 MapViewer 正式形态。
/// 状态/Toast/行卡片样式参照 SaveSelectMenu（SaveRowStyle 工厂全套）；行选中态手动管理（不用 ButtonGroup）。
/// </summary>
public partial class NationSelectMenu : Control
{
    // ── 场景节点（tscn unique_name_in_owner 标记 %名）──
    private Label _title;
    private Label _count;
    private Label _status;
    private Label _statusDot;
    private VBoxContainer _list;
    private Button _startBtn;
    private Label _detailTitle;
    private Label _detailBody;
    private Button _backBtn;
    private PanelContainer _toast;
    private Label _toastText;
    private Label _hintLabel;

    // ── 选国状态 ──
    private MapViewer _map;                                   // 3D 星球（MapViewer.tscn 实例）
    private string _path = "";                                // 地图存档路径（ConsumeNationSelectPath → --nationPath 覆盖）
    private int _selectedId = -1;                             // 当前选中政权 Id（-1=未选）
    private CivSnapshot _snap;                                // 读档快照（OnReady 填充；行/详情卡数据源）
    private readonly Dictionary<int, Button> _rowsById = new();   // 政权 Id → 列表行（选中态重刷用）

    public override void _Ready()
    {
        // 根 Control 强制全屏（防场景根未自动拉伸 → 锚点归零、侧栏落角落）
        SetAnchorsPreset(LayoutPreset.FullRect);

        // 取场景节点（unique_name_in_owner 标记 %名）
        _title = GetNode<Label>("%Title");
        _count = GetNode<Label>("%Count");
        _status = GetNode<Label>("%Status");
        _statusDot = GetNode<Label>("%StatusDot");
        _list = GetNode<VBoxContainer>("%List");
        _startBtn = GetNode<Button>("%StartBtn");
        _detailTitle = GetNode<Label>("%DetailTitle");
        _detailBody = GetNode<Label>("%DetailBody");
        _backBtn = GetNode<Button>("%BackBtn");
        _toast = GetNode<PanelContainer>("%Toast");
        _toastText = GetNode<Label>("%ToastText");
        _hintLabel = GetNode<Label>("%HintLabel");

        _backBtn.Pressed += OnBackPressed;
        _startBtn.Pressed += OnStartPressed;
        _startBtn.Disabled = true;   // 未选中任何政权前不可开始

        // 3D 星球（场景实例名 Map）：订阅读档完成/地图点选/不可点选信号（C# 事件式订阅 Godot 信号）
        _map = GetNode<MapViewer>("Map");
        _map.NationMapReady += OnReady;
        _map.TilePicked += OnPicked;
        _map.PickBlocked += OnBlocked;

        // 路径：SaveSelectMenu 游玩模式经 RequestNationSelect 设置 → 这里消费；--nationPath= 前缀匹配可覆盖（headless 冒烟）
        _path = EventBus.ConsumeNationSelectPath();
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            const string prefix = "--nationPath=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                _path = arg[prefix.Length..];
                break;
            }
        }
        if (string.IsNullOrEmpty(_path))
        {
            SetStatus("未指定地图（请从「正式游玩」进入）", SaveRowStyle.Yellow);
            LogService.Log("NationSelectMenu", "未指定地图路径：ConsumeNationSelectPath 为空且无 --nationPath 命令行参数");
            return;   // 不崩：侧栏留空，返回按钮可用
        }

        _title.Text = $"选择国家 · {_path.GetFile()}";
        SetStatus("正在载入文明…", SaveRowStyle.Accent);

        // 状态点呼吸动画（同 SaveSelectMenu）
        var tween = CreateTween().SetLoops();
        tween.TweenProperty(_statusDot, "modulate:a", 0.35f, 1.2f);
        tween.TweenProperty(_statusDot, "modulate:a", 1f, 1.2f);

        _map.MapPath = _path;   // 赋值触发读档；完成后 NationMapReady → OnReady
    }

    /// <summary>读档完成（信号无参）：拉快照重建列表（国家置顶分组）并刷新状态。</summary>
    private void OnReady()
    {
        var snap = _map.GetNationSnapshot();
        if (snap == null) return;
        _snap = snap;
        ResetSelection();

        // 清旧列表（防御性：NationMapReady 可能重发）
        foreach (Node c in _list.GetChildren())
            c.QueueFree();
        _rowsById.Clear();

        var stateIds = new HashSet<int>();
        foreach (var st in snap.States)
            stateIds.Add(st.Id);

        // 分组①：国家（States 成员数降序已是）
        AddGroupHeader($"🏛 国家（{snap.States.Count}）");
        foreach (var st in snap.States)
            AddStateRow(st);

        // 分组②：其他政权（Polities 排除国家；保留原声望降序）
        var others = new List<PolityRow>();
        foreach (var p in snap.Polities)
            if (!stateIds.Contains(p.Id))
                others.Add(p);

        AddGroupHeader($"🌾 其他政权（{others.Count}）");
        foreach (var p in others)
            AddPolityRow(p);

        _count.Text = (snap.States.Count + others.Count).ToString();

        if (snap.States.Count + others.Count == 0)
        {
            SetStatus("该地图暂无政权，请返回选择其他地图", SaveRowStyle.Yellow);
            ShowToast("⚠️ 该地图没有可选择的政权");
        }
        else
        {
            LogService.Log("NationSelect", $"读档完成: 国家 {snap.States.Count} / 其他政权 {others.Count} / 总人口 {snap.TotalPop:N0}");   // headless 冒烟断言锚点
            SetStatus($"读档完成：{snap.States.Count} 国家 / {others.Count} 其他政权 · 总人口 {snap.TotalPop:N0}", SaveRowStyle.Accent);
            ShowToast("🌍 单击地图或列表选择政权（政体/独立势力/势力范围/人口图层可点选）");
        }
    }

    /// <summary>地图点选命中 → 改选（地图→列表双向绑定）。</summary>
    private void OnPicked(int polityId)
    {
        SelectPolity(polityId);
    }

    /// <summary>当前图层非可点选白名单 → 状态行提示（不动当前选择）。</summary>
    private void OnBlocked()
    {
        SetStatus("当前图层为浏览模式——切到 政体/独立势力/势力范围/人口 图层可点击选择", SaveRowStyle.Yellow);
    }

    /// <summary>选中/换选：清旧行高亮 → 记录 → 地图 SetSelection（相机转向+高亮）→ 详情卡 → 开始按钮激活。</summary>
    private void SelectPolity(int id)
    {
        if (_snap == null || id < 0)
            return;

        // 清旧行高亮
        if (_rowsById.TryGetValue(_selectedId, out var oldRow))
        {
            oldRow.AddThemeStyleboxOverride("normal", SaveRowStyle.CardStyle());
            oldRow.AddThemeStyleboxOverride("focus", SaveRowStyle.CardStyle());
        }
        _selectedId = id;

        // 行 + 详情卡数据（国家优先）
        string label = "政权";
        string title = "";
        string body = "";
        if (TryFindState(id, out var st))
        {
            label = "国家";
            title = $"👑 国家 #{id}";
            body = $"都城 {Cap(st.CapitalPlaceId)} · 贡赋池 {st.Pool:F0} · 成员 {st.MemberCount} · 科技 {st.TechCount}\n" +
                   $"声望 {st.Prestige:F0} · 文化群 {Grp(st.CultureGroup)} · {(st.IsAtWar ? "⚔ 交战中" : "🕊 和平")}";
        }
        else if (TryFindPolity(id, out var p))
        {
            title = $"{ConceptEmoji(p.Concept)} {PolityName(p)}";
            var parts = new StringBuilder($"人口 {p.Pop:F0} · 领地 {p.TerritoryCells} 格 · 科技 {p.TechCount}");
            parts.Append($" · {(p.IsFarming ? "🌾 务农" : "🪓 渔猎")}");
            if (p.ChiefdomId >= 0 && !p.IsChief)
                parts.Append($" · 隶属酋邦 #{p.ChiefdomId}");
            if (p.PlaceId >= 0)
                parts.Append($" · @ 聚落 #{p.PlaceId}");
            body = parts.ToString();
        }
        else
        {
            return;   // 快照里找不到（防御）
        }

        // 行选中态（金框：CardHover 底 + 2px 金边）
        if (_rowsById.TryGetValue(id, out var row))
        {
            row.AddThemeStyleboxOverride("normal", SelectedStyle());
            row.AddThemeStyleboxOverride("focus", SelectedStyle());
        }

        _map.SetSelection(id);
        _detailTitle.Text = title;
        _detailBody.Text = body;
        _startBtn.Disabled = false;
        _startBtn.Text = $"▶ 开始游玩：{label} #{id}";
        _hintLabel.Text = $"已选 {label} #{id}——确认后开始游玩";
    }

    private void OnStartPressed()
    {
        if (_selectedId < 0)
            return;
        EventBus.RequestMapView(_path);
        EventBus.RequestPlayerBind(_selectedId);
        EventBus.MarkGameplayMap();
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }

    private void OnBackPressed()
    {
        EventBus.RequestGameplaySelect();   // 回游玩模式列表——防消费丢失回退浏览模式
        GetTree().ChangeSceneToFile("res://scenes/core/SaveSelectMenu.tscn");
    }

    // ── 列表重建 ──

    private void AddGroupHeader(string text)
    {
        var h = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 30),
            VerticalAlignment = VerticalAlignment.Center,
        };
        h.AddThemeFontSizeOverride("font_size", 15);
        h.AddThemeColorOverride("font_color", SaveRowStyle.Gold);
        h.MouseFilter = MouseFilterEnum.Ignore;
        _list.AddChild(h);
    }

    private void AddStateRow(in StateRow st)
    {
        var row = MakeRow("👑", $"国家 #{st.Id}", StateMeta(st), st.IsAtWar ? "⚔ 交战中" : "", SaveRowStyle.Red);
        int id = st.Id;
        row.Pressed += () => SelectPolity(id);
        _rowsById[id] = row;
        _list.AddChild(row);
    }

    private void AddPolityRow(in PolityRow p)
    {
        var row = MakeRow(ConceptEmoji(p.Concept), PolityName(p), PolityMeta(p), "", default);
        int id = p.Id;
        row.Pressed += () => SelectPolity(id);
        _rowsById[id] = row;
        _list.AddChild(row);
    }

    /// <summary>政权行卡片（行高 70）：首行 概念徽标(18px) + 标识(17px) + 右侧角标(11px)；次行 muted mono 元数据。</summary>
    private Button MakeRow(string emoji, string name, string meta, string corner, Color cornerColor)
    {
        var row = new Button { Text = "", CustomMinimumSize = new Vector2(0, 70) };
        row.AddThemeStyleboxOverride("normal", SaveRowStyle.CardStyle());
        row.AddThemeStyleboxOverride("hover", SaveRowStyle.CardHoverStyle());
        row.AddThemeStyleboxOverride("pressed", SaveRowStyle.CardHoverStyle());
        row.AddThemeStyleboxOverride("focus", SaveRowStyle.CardStyle());

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        v.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(v);

        var line1 = new HBoxContainer();
        line1.AddThemeConstantOverride("separation", 8);
        line1.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(line1);

        var badge = new Label { Text = emoji, CustomMinimumSize = new Vector2(26, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.AddThemeFontSizeOverride("font_size", 18);
        badge.MouseFilter = MouseFilterEnum.Ignore;
        line1.AddChild(badge);

        var nameL = new Label { Text = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        nameL.AddThemeFontSizeOverride("font_size", 17);
        nameL.MouseFilter = MouseFilterEnum.Ignore;
        line1.AddChild(nameL);

        if (corner.Length > 0)
        {
            var cornerL = new Label
            {
                Text = corner,
                SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cornerL.AddThemeFontSizeOverride("font_size", 11);
            cornerL.AddThemeColorOverride("font_color", cornerColor);
            cornerL.MouseFilter = MouseFilterEnum.Ignore;
            line1.AddChild(cornerL);
        }

        var metaL = new Label { Text = meta };
        metaL.AddThemeFontSizeOverride("font_size", 13);
        metaL.AddThemeFontOverride("font", SaveRowStyle.MonoFont());
        metaL.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
        metaL.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(metaL);

        return row;
    }

    private static string StateMeta(in StateRow st)
    {
        return $"都城 {Cap(st.CapitalPlaceId)} · 贡赋 {st.Pool:F0} · 成员 {st.MemberCount} · 科技 {st.TechCount}";
    }

    private static string PolityMeta(in PolityRow p)
    {
        var sb = new StringBuilder($"人口 {p.Pop:F0} · {(p.IsFarming ? "务农" : "渔猎")} · 领地 {p.TerritoryCells} 格 · 科技 {p.TechCount}");
        if (p.ChiefdomId >= 0 && !p.IsChief)
            sb.Append($" · 隶属酋邦 #{p.ChiefdomId}");
        if (p.PlaceId >= 0)
            sb.Append(" · @ 聚落");
        return sb.ToString();
    }

    private static string PolityName(in PolityRow p)
    {
        return p.Concept switch
        {
            "chiefdom" => $"酋邦 #{p.Id}" + (p.StateId >= 0 ? "（成员）" : ""),
            "tribe" => $"部落 #{p.Id}",
            "band" => $"游群 #{p.Id}",
            _ => $"政权 #{p.Id}",
        };
    }

    private static string ConceptEmoji(string concept) => concept switch
    {
        "state" => "👑",
        "chiefdom" => "🛡",
        "tribe" => "🌾",
        "band" => "🪓",
        _ => "❔",
    };

    private bool TryFindState(int id, out StateRow st)
    {
        foreach (var x in _snap.States)
            if (x.Id == id) { st = x; return true; }
        st = default;
        return false;
    }

    private bool TryFindPolity(int id, out PolityRow p)
    {
        foreach (var x in _snap.Polities)
            if (x.Id == id) { p = x; return true; }
        p = default;
        return false;
    }

    /// <summary>聚落 Id 显示（-1=无）。</summary>
    private static string Cap(int placeId) => placeId < 0 ? "无" : $"#{placeId}";

    private static string Grp(string culture) => culture.Length == 0 ? "未知" : culture;

    /// <summary>清空选中态（新图重载时行全部重建，仅重置状态与按钮）。</summary>
    private void ResetSelection()
    {
        _selectedId = -1;
        _startBtn.Disabled = true;
        _startBtn.Text = "▶ 开始游玩";
        _detailTitle.Text = "未选择政权";
        _detailBody.Text = "点击地图或上方列表选择一个政权";
        _hintLabel.Text = "🌍 拖拽旋转 · 滚轮缩放 · 点击地图上的政权来选择";
    }

    // ── 选中态 / 状态 / Toast ──

    /// <summary>选中行金框（CardHover 底 + 2px 金边，参照 SaveRowStyle.CardHoverStyle 自定义）。</summary>
    private static StyleBoxFlat SelectedStyle()
    {
        var s = new StyleBoxFlat
        {
            BgColor = SaveRowStyle.CardHover,
            BorderColor = SaveRowStyle.Accent,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        s.SetBorderWidthAll(2);
        s.SetCornerRadiusAll(12);
        s.AntiAliasing = true;
        return s;
    }

    private void SetStatus(string text, Color dotColor)
    {
        _status.Text = text;
        _statusDot.AddThemeColorOverride("font_color", dotColor);
    }

    private void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.Visible = true;
        _toast.Modulate = new Color(1f, 1f, 1f, 0f);
        var tween = CreateTween();
        tween.TweenProperty(_toast, "modulate:a", 1f, 0.18f);
        tween.TweenInterval(2.0f);
        tween.TweenProperty(_toast, "modulate:a", 0f, 0.35f);
        tween.TweenCallback(Callable.From(() => _toast.Visible = false));
    }
}