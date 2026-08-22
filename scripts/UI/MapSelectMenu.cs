using Godot;
using System.Collections.Generic;
using World.MapGen;
using World.Services;

namespace World.UI;

/// <summary>
/// 进入游戏界面：列出 user://maps/ 下所有 .mpa 存档，点击进入 MapViewer。
/// 显示：文件名 + seed + 顶点数/分辨率 + 海拔范围。
/// </summary>
public partial class MapSelectMenu : Control
{
    private VBoxContainer _list;
    private Label _status;

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
            var btn = new Button
            {
                Text = $"📁 {f}   {info}",
                CustomMinimumSize = new Vector2(620, 56),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", 18);
            string captured = path;   // 闭包捕获
            btn.Pressed += () => EnterViewer(captured);
            _list.AddChild(btn);
        }
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
