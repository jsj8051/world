using Godot;
using System;
using System.Collections.Generic;
using World.MapGen;
using World.Services;

namespace World.UI;

/// <summary>
/// 进入游戏界面：列出 user://maps/ 下所有 .mpa 存档，点击进入 MapViewer。
/// 显示：文件名 + seed + 顶点数/分辨率 + 海拔范围。
/// 2026-08-23：每行加「删除」按钮（ConfirmationDialog 确认后 File.Delete + 刷新列表）。
/// </summary>
public partial class MapSelectMenu : Control
{
    private VBoxContainer _list;
    private Label _status;
    private ConfirmationDialog _confirm;   // 删除确认对话框（复用，避免反复建节点）
    private string _pendingDelete = "";     // 待删除存档路径（确认后执行）

    public override void _Ready()
    {
        var bg = new ColorRect { Color = new Color(0.06f, 0.08f, 0.12f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;   // 不拦截点击
        AddChild(bg);

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.Alignment = BoxContainer.AlignmentMode.Center;
        root.AddThemeConstantOverride("separation", 14);
        root.MouseFilter = MouseFilterEnum.Ignore;  // 容器不拦截，子控件自己响应
        AddChild(root);

        var title = new Label
        {
            Text = "▶  选择地图",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 40);
        root.AddChild(title);

        _status = new Label
        {
            Text = "正在扫描存档…",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _status.AddThemeFontSizeOverride("font_size", 18);
        root.AddChild(_status);

        // 列表（可滚动）
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(640, 420) };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        _list = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        _list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_list);
        root.AddChild(scroll);

        var backBtn = new Button { Text = "← 返回", CustomMinimumSize = new Vector2(180, 48) };
        backBtn.AddThemeFontSizeOverride("font_size", 22);
        backBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MainMenu.tscn");
        root.AddChild(backBtn);

        // 删除确认对话框（复用）
        _confirm = new ConfirmationDialog
        {
            Title = "删除地图存档",
            OkButtonText = "删除",
            CancelButtonText = "取消",
            DialogText = "",
        };
        _confirm.Confirmed += ConfirmDelete;
        AddChild(_confirm);

        RefreshList();
    }

    private void RefreshList()
    {
        foreach (Node c in _list.GetChildren())
            c.QueueFree();

        var files = new List<string>();
        using var dir = DirAccess.Open("user://maps");
        if (dir == null)
        {
            _status.Text = "还没有地图存档。请先到主菜单「生成地图」。";
            return;
        }
        dir.ListDirBegin();
        while (true)
        {
            string f = dir.GetNext();
            if (f == "") break;
            if (!dir.CurrentIsDir() && f.EndsWith(".mpa"))
                files.Add(f);
        }
        dir.ListDirEnd();
        files.Sort();

        if (files.Count == 0)
        {
            _status.Text = "还没有地图存档。请先到主菜单「生成地图」。";
            return;
        }

        _status.Text = $"找到 {files.Count} 个地图存档：";
        foreach (var f in files)
        {
            string path = "user://maps/" + f;
            string info = Describe(path);

            // 行容器：进入按钮（占满剩余宽）+ 删除按钮（固定宽）
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.CustomMinimumSize = new Vector2(620, 56);

            var btn = new Button
            {
                Text = $"📁 {f}   {info}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,   // 进入按钮吃满剩余宽度
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", 18);
            string captured = path;   // 闭包捕获
            btn.Pressed += () => EnterViewer(captured);

            var delBtn = new Button
            {
                Text = "🗑 删除",
                CustomMinimumSize = new Vector2(96, 56),
            };
            delBtn.AddThemeFontSizeOverride("font_size", 16);
            delBtn.AddThemeColorOverride("font_color", new Color(1f, 0.6f, 0.5f));   // 删除 = 警示红
            delBtn.Pressed += () => RequestDelete(captured, f);

            row.AddChild(btn);
            row.AddChild(delBtn);
            _list.AddChild(row);
        }
    }

    /// <summary>请求删除：弹确认对话框（防误删），确认后 ExecuteDelete。</summary>
    private void RequestDelete(string path, string fileName)
    {
        _pendingDelete = path;
        _confirm.DialogText = $"确定删除地图存档「{fileName}」？此操作不可恢复。";
        _confirm.PopupCentered();
    }

    private void ConfirmDelete()
    {
        if (_pendingDelete.Length == 0) return;
        string path = _pendingDelete;
        _pendingDelete = "";
        try
        {
            ArchiveService.DeleteSave(path);
            _status.Text = $"🗑 已删除 {path.GetFile()}";
            LogService.Log("MapSelectMenu", $"删除存档 {path}");
        }
        catch (Exception ex)
        {
            _status.Text = $"⚠️ 删除失败：{ex.Message}";
            LogService.LogErr("MapSelectMenu", $"删除失败 {path}: {ex}");
        }
        RefreshList();
    }

    /// <summary>读取存档头信息（seed/顶点数/海拔范围），失败返回空。
    /// 2026-08-23：改 MapArchive.Peek 轻量读——原来全量 Read 每个档几十 MB（42 档 532MB 主线程反序列化
    /// + 每档建桶索引）→ 进界面卡 10s+；Peek 只读头部毫秒级。</summary>
    private string Describe(string path)
    {
        if (!MapArchive.Peek(path, out int seed, out int vertexCount, out int height,
                             out float minElev, out float maxElev, out ushort ver))
            return "(读取失败)";
        return ver >= 3
            ? $"seed={seed} · {vertexCount} 顶点 · elev[{minElev:F0},{maxElev:F0}]m"
            : $"seed={seed} · {vertexCount}×{height} · elev[{minElev:F0},{maxElev:F0}]m";
    }

    private void EnterViewer(string path)
    {
        EventBus.RequestMapView(path);
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }
}
