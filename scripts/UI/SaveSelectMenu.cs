using Godot;
using System;
using System.Collections.Generic;
using World.CivSim;
using World.MapGen;
using World.Services;

namespace World.UI;

/// <summary>
/// 存档选择界面（2026-08-23 合并版）：选择地图（.mpa）与读取文明存档（.cmp）统一为一个界面，
/// 顶部标签页切换。静态骨架（背景/窗口框/标题/标签页/状态栏/列表容器/返回/确认框/遮罩/Toast）
/// 在 SaveSelectMenu.tscn 场景中定义（编辑器/MCP 可见节点树）；脚本只做动态部分：
/// 存档行按钮生成、标签切换、删除确认、Toast。
/// 初始标签由 MainMenu 设置 <see cref="InitialTab"/>（"map" / "cmp"）后切入场景。
/// </summary>
public partial class SaveSelectMenu : Control
{
    /// <summary>进入场景前由 MainMenu 设置初始标签（"map"=选择地图 / "cmp"=读取文明存档）。</summary>
    public static string InitialTab = "map";

    private const string TabMap = "map";
    private const string TabCmp = "cmp";
    private const string ExtMap = ".mpa";
    private const string ExtCmp = ".cmp";

    private string _tab = TabMap;

    // 场景节点引用（静态骨架在 .tscn 定义）
    private Label _title;
    private Label _status;
    private Label _statusDot;
    private Label _count;
    private VBoxContainer _list;
    private Button _tabMapBtn;
    private Button _tabCmpBtn;
    private ConfirmationDialog _confirm;
    private ColorRect _dim;
    private PanelContainer _toast;
    private Label _toastText;

    private readonly HashSet<string> _broken = new();   // 损坏存档路径（可展示可选，但禁止进入）
    private readonly HashSet<string> _locked = new();   // 版本不符存档路径（旧版本/过新，可展示可选，但禁止进入）
    private string _pendingDelete = "";     // 待删除存档路径（确认后执行）

    public override void _Ready()
    {
        // 根 Control 强制全屏（防场景根未自动拉伸 → CenterContainer 锚点归零、框落左上角）
        SetAnchorsPreset(LayoutPreset.FullRect);

        // 取场景节点（unique_name_in_owner 标记 %名）
        _title = GetNode<Label>("%Title");
        _status = GetNode<Label>("%Status");
        _statusDot = GetNode<Label>("%StatusDot");
        _count = GetNode<Label>("%Count");
        _list = GetNode<VBoxContainer>("%List");
        _tabMapBtn = GetNode<Button>("%TabMap");
        _tabCmpBtn = GetNode<Button>("%TabCmp");
        _confirm = GetNode<ConfirmationDialog>("%Confirm");
        _dim = GetNode<ColorRect>("%Dim");
        _toast = GetNode<PanelContainer>("%Toast");
        _toastText = GetNode<Label>("%ToastText");

        _tab = InitialTab == TabCmp ? TabCmp : TabMap;

        // 标签按钮事件 + 初始高亮
        _tabMapBtn.Pressed += () => SwitchTab(TabMap);
        _tabCmpBtn.Pressed += () => SwitchTab(TabCmp);
        RefreshTabStyle();

        // 删除确认
        _confirm.GetOkButton().AddThemeColorOverride("font_color", SaveRowStyle.Red);
        _confirm.Confirmed += ConfirmDelete;
        _confirm.Canceled += HideDim;
        _confirm.CloseRequested += HideDim;

        // 返回按钮
        GetNode<Button>("Win/V/FootPad/FootRow/BackBtn").Pressed +=
            () => GetTree().ChangeSceneToFile("res://scenes/core/MainMenu.tscn");

        // 状态点呼吸动画
        var tween = CreateTween().SetLoops();
        tween.TweenProperty(_statusDot, "modulate:a", 0.35f, 1.2f);
        tween.TweenProperty(_statusDot, "modulate:a", 1f, 1.2f);

        RefreshList();
    }

    private void SwitchTab(string tab)
    {
        if (tab == _tab) return;
        _tab = tab;
        RefreshTabStyle();
        RefreshList();
    }

    /// <summary>标签高亮（active 顶条；inactive 灰字细框）。</summary>
    private void RefreshTabStyle()
    {
        StyleButton(_tabMapBtn, _tab == TabMap);
        StyleButton(_tabCmpBtn, _tab == TabCmp);
    }

    private static void StyleButton(Button b, bool active)
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

    private void RefreshList()
    {
        foreach (Node c in _list.GetChildren())
            c.QueueFree();

        bool isMap = _tab == TabMap;
        string ext = isMap ? ExtMap : ExtCmp;
        _title.Text = isMap ? "▶  选择地图" : "🎮  读取文明存档";

        var files = new List<string>();
        using var dir = DirAccess.Open("user://maps");
        if (dir == null)
        {
            ShowEmpty(isMap ? "还没有地图存档" : "还没有文明存档",
                isMap ? "请先到主菜单「生成地图」" : "请先「生成地图」→ 运行文明演化（CivSimDiag）产出 .cmp",
                isMap ? "🗺" : "🏺");
            return;
        }
        dir.ListDirBegin();
        while (true)
        {
            string f = dir.GetNext();
            if (f == "") break;
            if (!dir.CurrentIsDir() && f.EndsWith(ext))
                files.Add(f);
        }
        dir.ListDirEnd();
        files.Sort();

        if (files.Count == 0)
        {
            ShowEmpty(isMap ? "还没有地图存档" : "还没有文明存档",
                isMap ? "请先到主菜单「生成地图」" : "请先「生成地图」→ 运行文明演化（CivSimDiag）产出 .cmp",
                isMap ? "🗺" : "🏺");
            return;
        }

        _count.Text = files.Count.ToString();
        _broken.Clear();
        _locked.Clear();

        foreach (var f in files)
        {
            string path = "user://maps/" + f;
            string info;
            bool isBroken = false, isLocked = false;
            if (isMap)
            {
                info = DescribeMap(path);
            }
            else
            {
                info = DescribeCmp(path, out bool b, out bool l);
                isBroken = info == null;   // 损坏（Peek 失败且非版本问题）
                isLocked = !isBroken && l; // 版本不符（可展示不可进）
            }
            if (isBroken) _broken.Add(path);
            else if (isLocked) _locked.Add(path);

            // ── 行卡片：整行可点（进入），右侧按钮组 ──
            var row = new Button { Text = "", CustomMinimumSize = new Vector2(0, 64) };
            row.AddThemeStyleboxOverride("normal", SaveRowStyle.CardStyle());
            row.AddThemeStyleboxOverride("hover", SaveRowStyle.CardHoverStyle());
            row.AddThemeStyleboxOverride("pressed", SaveRowStyle.CardHoverStyle());
            row.AddThemeStyleboxOverride("focus", SaveRowStyle.CardStyle());
            string captured = path;   // 闭包捕获
            row.Pressed += () => EnterSelected(captured, isMap, isBroken, isLocked);

            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 12);
            h.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(h);

            // 图标块
            var icon = new PanelContainer { CustomMinimumSize = new Vector2(40, 40) };
            icon.AddThemeStyleboxOverride("panel",
                isBroken ? SaveRowStyle.IconStyleRed()
                     : isLocked ? SaveRowStyle.IconStyleYellow()
                     : SaveRowStyle.IconStyle());
            icon.MouseFilter = MouseFilterEnum.Ignore;
            var iconLabel = new Label
            {
                Text = isBroken || isLocked ? "⚠" : (isMap ? "🗺" : "📜"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconLabel.AddThemeFontSizeOverride("font_size", 20);
            iconLabel.MouseFilter = MouseFilterEnum.Ignore;
            icon.AddChild(iconLabel);
            h.AddChild(icon);

            // 文本块
            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", 2);
            body.MouseFilter = MouseFilterEnum.Ignore;
            body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            h.AddChild(body);

            var nameRow = new HBoxContainer();
            nameRow.MouseFilter = MouseFilterEnum.Ignore;
            body.AddChild(nameRow);

            var name = new Label { Text = f };
            name.AddThemeFontSizeOverride("font_size", 17);
            name.AddThemeColorOverride("font_color", isBroken ? SaveRowStyle.Red : isLocked ? SaveRowStyle.Yellow : SaveRowStyle.Fg);
            name.MouseFilter = MouseFilterEnum.Ignore;
            nameRow.AddChild(name);

            if (isBroken || isLocked)
            {
                var badge = new Label
                {
                    Text = isBroken ? " 已损坏" : " 版本不符",
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                };
                badge.AddThemeFontSizeOverride("font_size", 11);
                badge.AddThemeColorOverride("font_color", isBroken ? SaveRowStyle.Red : SaveRowStyle.Yellow);
                badge.MouseFilter = MouseFilterEnum.Ignore;
                nameRow.AddChild(badge);
            }

            var meta = new Label
            {
                Text = info ?? "(存档已损坏，无法进入)",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            meta.AddThemeFontSizeOverride("font_size", 13);
            meta.AddThemeFontOverride("font", SaveRowStyle.MonoFont());
            meta.AddThemeColorOverride("font_color", isBroken ? SaveRowStyle.Red : isLocked ? SaveRowStyle.Yellow : SaveRowStyle.Muted);
            meta.MouseFilter = MouseFilterEnum.Ignore;
            meta.VerticalAlignment = VerticalAlignment.Center;
            nameRow.AddChild(meta);

            // 按钮组：进入 + 删除
            var btns = new HBoxContainer();
            btns.AddThemeConstantOverride("separation", 8);
            h.AddChild(btns);

            var enterBtn = new Button
            {
                Text = isMap ? "▶ 进入" : "▶ 开始游戏",
                CustomMinimumSize = new Vector2(isMap ? 92 : 108, 40),
            };
            enterBtn.AddThemeFontSizeOverride("font_size", 14);
            ApplyPrimary(enterBtn);
            if (isBroken || isLocked) enterBtn.Disabled = true;
            enterBtn.Pressed += () => EnterSelected(captured, isMap, isBroken, isLocked);
            btns.AddChild(enterBtn);

            var delBtn = new Button { Text = "🗑", CustomMinimumSize = new Vector2(48, 40) };
            delBtn.AddThemeFontSizeOverride("font_size", 15);
            ApplyDanger(delBtn);
            delBtn.Pressed += () => RequestDelete(captured, f, isMap ? "地图存档" : "文明存档");
            btns.AddChild(delBtn);

            _list.AddChild(row);
        }

        int bad = _broken.Count, lck = _locked.Count;
        if (!isMap && bad + lck > 0)
            SetStatus($"找到 {files.Count} 个文明存档（{bad} 个损坏 + {lck} 个版本不符，无法进入；可删除清理）", SaveRowStyle.Yellow);
        else
            SetStatus($"找到 {files.Count} 个{(isMap ? "地图" : "文明")}存档：点击进入，或点 🗑 删除", SaveRowStyle.Accent);
    }

    /// <summary>空状态：窗口框内居中引导面板（大图标 + 文案；返回按钮在框底已有）。</summary>
    private void ShowEmpty(string title, string sub, string icon)
    {
        SetStatus("还没有存档。", SaveRowStyle.Yellow);
        _count.Text = "0";
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", SaveRowStyle.EmptyPanel());
        panel.CustomMinimumSize = new Vector2(560, 220);
        panel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        panel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _list.AddChild(panel);

        var v = new VBoxContainer();
        v.Alignment = BoxContainer.AlignmentMode.Center;
        v.AddThemeConstantOverride("separation", 10);
        v.SetAnchorsPreset(LayoutPreset.FullRect);
        panel.AddChild(v);

        var big = new Label { Text = icon, HorizontalAlignment = HorizontalAlignment.Center };
        big.AddThemeFontSizeOverride("font_size", 42);
        v.AddChild(big);

        var t1 = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
        t1.AddThemeFontSizeOverride("font_size", 17);
        t1.AddThemeColorOverride("font_color", SaveRowStyle.Fg);
        v.AddChild(t1);

        var t2 = new Label { Text = sub, HorizontalAlignment = HorizontalAlignment.Center };
        t2.AddThemeFontSizeOverride("font_size", 13);
        t2.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
        v.AddChild(t2);
    }

    private void SetStatus(string text, Color dotColor)
    {
        _status.Text = text;
        _statusDot.AddThemeColorOverride("font_color", dotColor);
    }

    /// <summary>读取 .mpa 头部信息（seed/顶点数/海拔范围）。</summary>
    private string DescribeMap(string path)
    {
        if (!MapArchive.Peek(path, out int seed, out int vertexCount, out int height,
                             out float minElev, out float maxElev, out ushort ver))
            return "(读取失败)";
        return ver >= 3
            ? $"seed={seed} · {vertexCount} 顶点 · elev[{minElev:F0},{maxElev:F0}]m"
            : $"seed={seed} · {vertexCount}×{height} · elev[{minElev:F0},{maxElev:F0}]m";
    }

    /// <summary>读取 .cmp 轻量摘要；返回 (Info, locked)。损坏 → Info=null。</summary>
    private string DescribeCmp(string path, out bool broken, out bool locked)
    {
        broken = false;
        locked = false;
        try
        {
            if (!CivMapArchive.Peek(path, out int seed, out int tick, out float pop, out int entities,
                                     out ushort aVer, out var st))
            {
                if (st == ArchiveVersionStatus.Older)
                {
                    locked = true;
                    return ($"旧版本存档 v{aVer}，当前仅支持 v{CivMapArchive.Version}（请重新演化生成新档）");
                }
                if (st == ArchiveVersionStatus.Newer)
                {
                    locked = true;
                    return ($"存档版本过新 v{aVer}（需要 v{CivMapArchive.Version}，请升级游戏）");
                }
                broken = true;
                return null;   // 真损坏
            }
            return ($"seed={seed} · 石器时代 · " +
                    $"{tick * World.CivSim.CivSimContext.TickYears} 年 · 人口 {pop:F0} · 部落 {entities}");
        }
        catch (Exception ex)
        {
            LogService.LogErr("SaveSelectMenu", $"存档异常 {path}: {ex}");
            broken = true;
            return null;   // 读取异常 → 损坏
        }
    }

    private void RequestDelete(string path, string fileName, string kind)
    {
        _pendingDelete = path;
        _dim.Visible = true;
        _confirm.Title = $"删除{kind}";
        _confirm.DialogText = $"确定删除{kind}「{fileName}」？\n此操作不可恢复。";
        _confirm.PopupCentered();
    }

    private void HideDim() => _dim.Visible = false;

    private void ConfirmDelete()
    {
        if (_pendingDelete.Length == 0) return;
        string path = _pendingDelete;
        _pendingDelete = "";
        HideDim();
        _broken.Remove(path);
        _locked.Remove(path);
        bool ok = true;
        try
        {
            ArchiveService.DeleteSave(path);
            LogService.Log("SaveSelectMenu", $"删除存档 {path}");
        }
        catch (Exception ex)
        {
            ok = false;
            LogService.LogErr("SaveSelectMenu", $"删除失败 {path}: {ex}");
        }
        ShowToast(ok ? $"🗑 已删除 {path.GetFile()}" : "⚠️ 删除失败（详情见日志）");
        RefreshList();
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

    private void EnterSelected(string path, bool isMap, bool broken, bool locked)
    {
        if (isMap) { EnterViewer(path); return; }
        if (broken)
        {
            SetStatus("⚠️ 该存档已损坏，无法进入。请重新生成/演化，或删除清理。", SaveRowStyle.Red);
            LogService.Log("SaveSelectMenu", $"拒绝进入损坏存档 {path}");
            return;
        }
        if (locked)
        {
            SetStatus("⚠️ 该存档版本与本游戏不兼容，无法进入。请用当前版本重新演化。", SaveRowStyle.Yellow);
            LogService.Log("SaveSelectMenu", $"拒绝进入版本不符存档 {path}");
            return;
        }
        EventBus.RequestMapView(path);
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }

    private void EnterViewer(string path)
    {
        EventBus.RequestMapView(path);
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }

    // ── 按钮外观 ──

    private static void ApplyPrimary(Button b)
    {
        b.AddThemeStyleboxOverride("normal", SaveRowStyle.PrimaryNormal());
        b.AddThemeStyleboxOverride("hover", SaveRowStyle.PrimaryHover());
        b.AddThemeStyleboxOverride("pressed", SaveRowStyle.PrimaryHover());
        b.AddThemeStyleboxOverride("focus", SaveRowStyle.PrimaryNormal());
        b.AddThemeColorOverride("font_color", SaveRowStyle.Accent);
        b.AddThemeColorOverride("font_hover_color", SaveRowStyle.Bg2);
        b.AddThemeColorOverride("font_pressed_color", SaveRowStyle.Bg2);
    }

    private static void ApplyDanger(Button b)
    {
        b.AddThemeStyleboxOverride("normal", SaveRowStyle.DangerNormal());
        b.AddThemeStyleboxOverride("hover", SaveRowStyle.DangerHover());
        b.AddThemeStyleboxOverride("pressed", SaveRowStyle.DangerHover());
        b.AddThemeStyleboxOverride("focus", SaveRowStyle.DangerNormal());
        b.AddThemeColorOverride("font_color", SaveRowStyle.Red);
        b.AddThemeColorOverride("font_hover_color", SaveRowStyle.Red);
    }
}