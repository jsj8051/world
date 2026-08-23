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
/// </summary>
public partial class SaveSelectMenu : Control
{
    private const string ExtMap = ".mpa";
    private Label _title;
    private Label _status;
    private Label _statusDot;
    private Label _count;
    private VBoxContainer _list;
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
        _confirm = GetNode<ConfirmationDialog>("%Confirm");
        _dim = GetNode<ColorRect>("%Dim");
        _toast = GetNode<PanelContainer>("%Toast");
        _toastText = GetNode<Label>("%ToastText");

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

    private void RefreshList()
    {
        foreach (Node c in _list.GetChildren())
            c.QueueFree();

        _title.Text = "🌍  世界存档";

        var files = new List<string>();
        using var dir = DirAccess.Open("user://maps");
        if (dir == null)
        {
            ShowEmpty("还没有世界存档", "请先到主菜单「创建世界」生成", "🌍");
            return;
        }
        dir.ListDirBegin();
        while (true)
        {
            string f = dir.GetNext();
            if (f == "") break;
            if (!dir.CurrentIsDir() && f.EndsWith(ExtMap))
                files.Add(f);
        }
        dir.ListDirEnd();
        files.Sort();

        if (files.Count == 0)
        {
            ShowEmpty("还没有世界存档", "请先到主菜单「创建世界」生成", "🌍");
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
            delBtn.Pressed += () => RequestDelete(captured, f, "世界存档");
            btns.AddChild(delBtn);

            _list.AddChild(row);
        }

        int bad = _broken.Count, lck = _locked.Count;
        if (bad + lck > 0)
            SetStatus($"找到 {files.Count} 个世界存档（{bad} 损坏 + {lck} 版本不符，无法进入；可删除清理）", SaveRowStyle.Yellow);
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

    /// <summary>读取 .mpa 头部信息（seed/顶点数/海拔范围 + 文明段标记）。
    /// 返回 (info, broken, locked)：broken=Peek 失败（损坏），locked=版本不支持。</summary>
    private (string info, bool broken, bool locked) DescribeMap(string path, out bool hasCiv)
    {
        hasCiv = false;
        if (!MapArchive.Peek(path, out int seed, out int vertexCount, out int height,
                             out float minElev, out float maxElev, out ushort ver, out hasCiv))
            return (null, true, false);   // 打不开/损坏
        if (ver < 6 || ver > MapArchive.Version)
            return ($"不支持版本 v{ver}（当前 v{MapArchive.Version}，请重新生成）", false, true);
        string civ = hasCiv ? "🌍 含文明" : "";
        return ver >= 3
            ? ($"{civ} seed={seed} · {vertexCount} 顶点 · elev[{minElev:F0},{maxElev:F0}]m", false, false)
            : ($"{civ} seed={seed} · {vertexCount}×{height} · elev[{minElev:F0},{maxElev:F0}]m", false, false);
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