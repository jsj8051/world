using Godot;
using System;
using System.Collections.Generic;
using World.CivSim;

namespace World.UI;

/// <summary>
/// 读取文明存档界面：列出 user://maps/ 下所有 .cmp 游玩地图，点击进入 MapViewer（开始游戏，
/// 显示自然图层 + 文明图层：人口/文化/部落）。
/// 显示：文件名 + seed + 纪元 + 演化时长 + 总人口 + 部落数。
/// 2026-08-07：损坏存档也展示（红字 ⚠️），可选中但禁止进入（点击提示无法进入）。
/// </summary>
public partial class CmpSelectMenu : Control
{
    private VBoxContainer _list;
    private Label _status;
    private readonly HashSet<string> _broken = new();   // 损坏存档路径（可展示可选，但禁止进入）

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
        _broken.Clear();
        foreach (var f in files)
        {
            string path = "user://maps/" + f;
            string info = Describe(path);
            bool broken = info == null;
            if (broken) _broken.Add(path);
            var btn = new Button
            {
                Text = broken ? $"⚠️ {f}   （存档已损坏，无法进入）" : $"📜 {f}   {info}",
                CustomMinimumSize = new Vector2(740, 56),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", 18);
            if (broken) btn.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.45f));   // 坏档红字
            string captured = path;   // 闭包捕获
            btn.Pressed += () => EnterGame(captured);
            _list.AddChild(btn);
        }
        if (_broken.Count > 0)
            _status.Text += $"（{_broken.Count} 个损坏，无法进入）";
    }

    /// <summary>读取 .cmp 头部信息（seed/时长/人口/实体数）。
    /// 返回 null = 存档损坏（读取失败/版本拒绝/读取异常）——展示但不允许进入。</summary>
    private string Describe(string path)
    {
        try
        {
            if (!CivMapArchive.Read(path, out var grid, out var result))
                return null;   // 读取失败/版本拒绝（旧档/化石）→ 损坏
            float pop = result.Context.TotalPopulation();
            return $"seed={result.Context.Seed} · 石器时代 · " +
                   $"{result.FinalTick * World.CivSim.CivSimContext.TickYears} 年 · 人口 {pop:F0} · 部落 {result.Context.Entities.Count}";
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CmpSelectMenu] 存档损坏 {path}: {ex}");
            return null;   // 读取异常（旧档格式不兼容等）→ 损坏
        }
    }

    private void EnterGame(string path)
    {
        if (_broken.Contains(path))
        {
            _status.Text = "⚠️ 该存档已损坏，无法进入。请重新生成/演化。";
            GD.Print($"[CmpSelectMenu] 拒绝进入损坏存档 {path}");
            return;
        }
        ViewerLauncher.PendingPath = path;
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }
}
