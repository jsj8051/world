using Godot;
using System.Collections.Generic;
using World.CivSim;

namespace World.UI;

/// <summary>
/// 读取文明存档界面：列出 user://maps/ 下所有 .cmp 游玩地图，点击进入 MapViewer（开始游戏，
/// 显示自然图层 + 文明图层：人口/文化/部落）。
/// 显示：文件名 + seed + 纪元 + 演化时长 + 总人口 + 部落数。
/// </summary>
public partial class CmpSelectMenu : Control
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
            Text = "🎮  读取文明存档",
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
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(760, 420) };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        _list = new VBoxContainer { CustomMinimumSize = new Vector2(740, 0) };
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
            _status.Text = "还没有文明存档。请先「生成地图」→ 运行文明演化（CivSimDiag）产出 .cmp。";
            return;
        }
        dir.ListDirBegin();
        while (true)
        {
            string f = dir.GetNext();
            if (f == "") break;
            if (!dir.CurrentIsDir() && f.EndsWith(".cmp"))
                files.Add(f);
        }
        dir.ListDirEnd();
        files.Sort();

        if (files.Count == 0)
        {
            _status.Text = "还没有文明存档。请先「生成地图」→ 运行文明演化（CivSimDiag）产出 .cmp。";
            return;
        }

        _status.Text = $"找到 {files.Count} 个文明存档：";
        GD.Print($"[CmpSelectMenu] found {files.Count} civ maps");
        foreach (var f in files)
        {
            string path = "user://maps/" + f;
            string info = Describe(path);
            var btn = new Button
            {
                Text = $"📜 {f}   {info}",
                CustomMinimumSize = new Vector2(740, 56),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", 18);
            string captured = path;   // 闭包捕获
            btn.Pressed += () => EnterGame(captured);
            _list.AddChild(btn);
        }
    }

    /// <summary>读取 .cmp 头部信息（seed/纪元/时长/人口/部落数），失败返回空。</summary>
    private string Describe(string path)
    {
        if (!CivMapArchive.Read(path, out var grid, out var result))
            return "(读取失败)";
        float pop = result.Context.TotalPopulation();
        return $"seed={result.Context.Seed} · {result.Context.Epoch.Name} · " +
               $"{result.FinalTick * result.Context.Epoch.TickYears} 年 · 人口 {pop:F0} · 部落 {result.Context.Tribes.Count}";
    }

    private void EnterGame(string path)
    {
        ViewerLauncher.PendingPath = path;
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }
}
