using Godot;
using System;
using System.Collections.Generic;
using World.CivSim;
using World.MapGen;
using World.Services;

namespace World.UI;

/// <summary>
/// 存档选择界面（2026-08-23 单存档化）：只列 .mpa（自然或含文明——CIVI 段标记），
/// 不再区分自然/文明两类存档。静态骨架（背景/窗口框/标题/列表容器/返回/确认框/遮罩/Toast）
/// 在 SaveSelectMenu.tscn 场景中定义；脚本只做动态部分：存档行生成、删除确认、Toast。
/// 每行标记 🌍 含文明（CIVI 段）或 🗺 纯自然；点击统一进 MapViewer（读档自动启用文明图层）。
/// ⚠️ 2026-08-25 第二阶段双模式：浏览模式（默认，列 .mpa → MapViewer 查看）/
///    游玩模式（EventBus.RequestGameplaySelect 触发，列 .cmp 游戏档 → 进游玩——EU4 式）。
/// </summary>
public partial class SaveSelectMenu : Control
{
    private const string ExtMap = ".mpa";
    private const string ExtGame = ".cmp";
    private bool _playMode;      // 游玩模式（主菜单「正式游玩」进入——列 .cmp 游戏档）
    private Label _title;
    private Label _status;
    private Label _statusDot;
    private Label _count;
    private VBoxContainer _list;
    private CenterContainer _confirmBox;   // 删除确认模态框根（自定义样式，与主界面同风格）
    private Label _confirmTitle;
    private Label _confirmBody;
    private Button _confirmCancel;
    private Button _confirmDel;
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
        _confirmBox = GetNode<CenterContainer>("%ConfirmBox");
        _confirmTitle = GetNode<Label>("%ConfirmTitle");
        _confirmBody = GetNode<Label>("%ConfirmBody");
        _confirmCancel = GetNode<Button>("%CancelBtn");
        _confirmDel = GetNode<Button>("%DelBtn");
        _dim = GetNode<ColorRect>("%Dim");
        _toast = GetNode<PanelContainer>("%Toast");
        _toastText = GetNode<Label>("%ToastText");

        // 删除确认（自定义模态框：与主界面同风格，不用引擎默认 ConfirmationDialog）
        _confirmCancel.Pressed += HideDim;
        _confirmDel.Pressed += ConfirmDelete;
        ApplyDanger(_confirmDel);   // 危险红 StyleBox 全套（场景已给红字）

        // 返回按钮
        GetNode<Button>("Win/V/FootPad/FootRow/BackBtn").Pressed +=
            () => GetTree().ChangeSceneToFile("res://scenes/core/MainMenu.tscn");

        // ⚠️ 2026-08-25 游玩模式（主菜单「正式游玩」→ RequestGameplaySelect → 列 .cmp 游戏档）
        _playMode = EventBus.ConsumeGameplaySelect();

        // 状态点呼吸动画
        var tween = CreateTween().SetLoops();
        tween.TweenProperty(_statusDot, "modulate:a", 0.35f, 1.2f);
        tween.TweenProperty(_statusDot, "modulate:a", 1f, 1.2f);

        RefreshList();
    }

    /// <summary>ESC 关闭删除确认框（旧 ConfirmationDialog 的 dialog_close_on_escape 行为）。</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_confirmBox.Visible && @event.IsActionPressed("ui_cancel"))
        {
            HideDim();
            GetViewport().SetInputAsHandled();
        }
    }

    private void RefreshList()
    {
        foreach (Node c in _list.GetChildren())
            c.QueueFree();

        string ext = _playMode ? ExtGame : ExtMap;
        _title.Text = _playMode ? "⚔  正式游玩" : "🌍  世界存档";
        string emptyHint = _playMode
            ? "请先到主菜单「创建世界」生成并演化出国家（.cmp 游戏档）"
            : "请先到主菜单「创建世界」生成";

        var files = new List<string>();
        using var dir = DirAccess.Open("user://maps");
        if (dir == null)
        {
            ShowEmpty(_playMode ? "还没有游戏存档" : "还没有世界存档", emptyHint, _playMode ? "⚔" : "🌍");
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
            ShowEmpty(_playMode ? "还没有游戏存档" : "还没有世界存档", emptyHint, _playMode ? "⚔" : "🌍");
            return;
        }

        _count.Text = files.Count.ToString();
        _broken.Clear();
        _locked.Clear();

        foreach (var f in files)
        {
            string path = "user://maps/" + f;
            bool hasCiv = false;
            var (info, broken, locked) = DescribeMap(path, out hasCiv);
            if (broken) _broken.Add(path);
            else if (locked) _locked.Add(path);

            // ── 行卡片：整行可点（进入），右侧按钮组 ──
            var row = new Button { Text = "", CustomMinimumSize = new Vector2(0, 64) };
            row.AddThemeStyleboxOverride("normal", SaveRowStyle.CardStyle());
            row.AddThemeStyleboxOverride("hover", SaveRowStyle.CardHoverStyle());
            row.AddThemeStyleboxOverride("pressed", SaveRowStyle.CardHoverStyle());
            row.AddThemeStyleboxOverride("focus", SaveRowStyle.CardStyle());
            string captured = path;   // 闭包捕获
            row.Pressed += () => EnterSelected(captured, broken, locked);

            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 12);
            h.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(h);

            // 图标块（🌍 含文明 / 🗺 纯自然 / ⚠ 损坏）
            var icon = new PanelContainer { CustomMinimumSize = new Vector2(40, 40) };
            icon.AddThemeStyleboxOverride("panel",
                broken ? SaveRowStyle.IconStyleRed()
                     : locked ? SaveRowStyle.IconStyleYellow()
                     : hasCiv ? SaveRowStyle.IconStyleGold()
                     : SaveRowStyle.IconStyle());
            icon.MouseFilter = MouseFilterEnum.Ignore;
            var iconLabel = new Label
            {
                Text = broken || locked ? "⚠" : (hasCiv ? "🌍" : "🗺"),
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
            name.AddThemeColorOverride("font_color", broken ? SaveRowStyle.Red : locked ? SaveRowStyle.Yellow : SaveRowStyle.Fg);
            name.MouseFilter = MouseFilterEnum.Ignore;
            nameRow.AddChild(name);

            // 文明徽标（含文明 = 🌍 徽标）
            if (!broken && !locked)
            {
                var civBadge = new Label
                {
                    Text = hasCiv ? "含文明" : "纯自然",
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                };
                civBadge.AddThemeFontSizeOverride("font_size", 11);
                civBadge.AddThemeColorOverride("font_color", hasCiv ? SaveRowStyle.Gold : SaveRowStyle.Muted);
                civBadge.MouseFilter = MouseFilterEnum.Ignore;
                nameRow.AddChild(civBadge);
            }

            var meta = new Label
            {
                Text = info ?? "(存档已损坏，无法进入)",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            meta.AddThemeFontSizeOverride("font_size", 13);
            meta.AddThemeFontOverride("font", SaveRowStyle.MonoFont());
            meta.AddThemeColorOverride("font_color", broken ? SaveRowStyle.Red : locked ? SaveRowStyle.Yellow : SaveRowStyle.Muted);
            meta.MouseFilter = MouseFilterEnum.Ignore;
            meta.VerticalAlignment = VerticalAlignment.Center;
            nameRow.AddChild(meta);

            // 按钮组：进入 + 删除
            var btns = new HBoxContainer();
            btns.AddThemeConstantOverride("separation", 8);
            h.AddChild(btns);

            var enterBtn = new Button
            {
                Text = "▶ 进入",
                CustomMinimumSize = new Vector2(92, 40),
            };
            enterBtn.AddThemeFontSizeOverride("font_size", 14);
            ApplyPrimary(enterBtn);
            if (broken || locked) enterBtn.Disabled = true;
            enterBtn.Pressed += () => EnterSelected(captured, broken, locked);
            btns.AddChild(enterBtn);

            var delBtn = new Button { Text = "🗑", CustomMinimumSize = new Vector2(48, 40) };
            delBtn.AddThemeFontSizeOverride("font_size", 15);
            ApplyDanger(delBtn);
            delBtn.Pressed += () => RequestDelete(captured, f, _playMode ? "游戏档" : "世界存档");
            btns.AddChild(delBtn);

            _list.AddChild(row);
        }

        int bad = _broken.Count, lck = _locked.Count;
        if (bad + lck > 0)
            SetStatus($"找到 {files.Count} 个{( _playMode ? "游戏档" : "世界存档")}（{bad} 损坏 + {lck} 版本不符，无法进入；可删除清理）", SaveRowStyle.Yellow);
        else if (_playMode)
            SetStatus($"找到 {files.Count} 个游戏档（.cmp——含国家与文明），点击进入游玩，或点 🗑 删除", SaveRowStyle.Accent);
        else
            SetStatus($"找到 {files.Count} 个世界存档：🌍=含文明 🗺=纯自然，点击进入，或点 🗑 删除", SaveRowStyle.Accent);
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

    /// <summary>读取存档头部信息（浏览模式：.mpa 经 MapArchive.Peek；游玩模式：.cmp 经 CivMapArchive.Peek）。
    /// 返回 (info, broken, locked)：broken=Peek 失败（损坏），locked=版本不支持。</summary>
    private (string info, bool broken, bool locked) DescribeMap(string path, out bool hasCiv)
    {
        hasCiv = false;
        if (_playMode)
        {
            // .cmp 游戏档：seed/tick/人口/实体（段表直达 HEAD+TRIB，毫秒级）
            if (!CivMapArchive.Peek(path, out int seed, out int tick, out float pop,
                    out int entities, out ushort ver, out var status))
                return status == ArchiveVersionStatus.Current
                    ? (null, true, false)                     // status=Current 仍失败 → 损坏
                    : ($"不支持版本 v{ver}（当前 v{CivMapArchive.Version}，请重新演化生成）", false, true);
            hasCiv = true;   // .cmp 恒含文明（游戏档）
            return ($"🌍 seed={seed} · tick {tick} · 人口 {pop:F0} · 势力 {entities}", false, false);
        }
        if (!MapArchive.Peek(path, out int seed2, out int vertexCount, out int height,
                             out float minElev, out float maxElev, out ushort ver2, out hasCiv))
            return (null, true, false);   // 打不开/损坏
        if (ver2 < 6 || ver2 > MapArchive.Version)
            return ($"不支持版本 v{ver2}（当前 v{MapArchive.Version}，请重新生成）", false, true);
        string civ = hasCiv ? "🌍 含文明" : "";
        return ver2 >= 3
            ? ($"{civ} seed={seed2} · {vertexCount} 顶点 · elev[{minElev:F0},{maxElev:F0}]m", false, false)
            : ($"{civ} seed={seed2} · {vertexCount}×{height} · elev[{minElev:F0},{maxElev:F0}]m", false, false);
    }

    private void RequestDelete(string path, string fileName, string kind)
    {
        _pendingDelete = path;
        _confirmTitle.Text = $"🗑 删除{kind}";
        _confirmBody.Text = $"确定删除{kind}「{fileName}」？\n此操作不可恢复。";
        _dim.Visible = true;
        _confirmBox.Visible = true;
    }

    private void HideDim()
    {
        _dim.Visible = false;
        _confirmBox.Visible = false;
    }

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

    private void EnterSelected(string path, bool broken, bool locked)
    {
        if (broken)
        {
            SetStatus("⚠️ 该存档已损坏，无法进入。请重新生成，或删除清理。", SaveRowStyle.Red);
            LogService.Log("SaveSelectMenu", $"拒绝进入损坏存档 {path}");
            return;
        }
        if (locked)
        {
            SetStatus("⚠️ 该存档版本与本游戏不兼容，无法进入。请用当前版本重新生成。", SaveRowStyle.Yellow);
            LogService.Log("SaveSelectMenu", $"拒绝进入版本不符存档 {path}");
            return;
        }
        EventBus.RequestMapView(path);
        if (_playMode) EventBus.MarkGameplayMap();   // 游玩模式标记（MapViewer 消费——浏览/游玩形态）
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }

    // ── 按钮外观 ──

    private static void ApplyPrimary(Button b)
    {
        b.AddThemeStyleboxOverride("normal", SaveRowStyle.PrimaryNormal());
        b.AddThemeStyleboxOverride("hover", SaveRowStyle.PrimaryHover());
        b.AddThemeStyleboxOverride("pressed", SaveRowStyle.PrimaryHover());
        b.AddThemeStyleboxOverride("focus", SaveRowStyle.PrimaryNormal());
        b.AddThemeColorOverride("font_color", SaveRowStyle.Fg);
        b.AddThemeColorOverride("font_hover_color", SaveRowStyle.Fg);
        b.AddThemeColorOverride("font_pressed_color", SaveRowStyle.Fg);
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