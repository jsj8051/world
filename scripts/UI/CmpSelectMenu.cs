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
    private readonly HashSet<string> _locked = new();   // 版本不符存档路径（旧版本/过新，可展示可选，但禁止进入）

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
        _locked.Clear();
        foreach (var f in files)
        {
            string path = "user://maps/" + f;
            var (info, enterable) = Describe(path);
            bool broken = info == null;
            bool locked = !broken && !enterable;
            if (broken) _broken.Add(path);
            else if (locked) _locked.Add(path);
            var btn = new Button
            {
                Text = broken ? $"⚠️ {f}   （存档已损坏，无法进入）"
                     : locked ? $"⚠️ {f}   {info}"
                     : $"📜 {f}   {info}",
                CustomMinimumSize = new Vector2(740, 56),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", 18);
            if (broken) btn.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.45f));    // 坏档红字
            else if (locked) btn.AddThemeColorOverride("font_color", new Color(0.9f, 0.8f, 0.4f)); // 版本不符黄字
            string captured = path;   // 闭包捕获
            btn.Pressed += () => EnterGame(captured);
            _list.AddChild(btn);
        }
        if (_broken.Count > 0 || _locked.Count > 0)
            _status.Text += $"（{_broken.Count} 个损坏 + {_locked.Count} 个版本不符，均无法进入）";
    }

    /// <summary>读取 .cmp 轻量摘要（seed/时长/人口/部落数）——Peek 跳过自然段，不重建 WildCrops/R
    /// （2026-08-17：原来全量 Read 每个 n=64 档 ~1-2s，列表多个档主线程卡死）。
    /// 返回 (Info=null → 损坏红字；Info≠null 且 Enterable=false → 版本不符黄字；可进 → 正常)。
    /// 游戏版本号展示：project.godot application/config/version（仅语义标签，兼容判断走 CompatibleArchiveVersions）。</summary>
    private (string Info, bool Enterable) Describe(string path)
    {
        try
        {
            if (!CivMapArchive.Peek(path, out int seed, out int tick, out float pop, out int entities,
                                     out ushort aVer, out var st))
            {
                if (st == ArchiveVersionStatus.Older)
                    return ($"旧版本存档 v{aVer}，当前仅支持 v{CivMapArchive.Version}（请重新演化生成新档）", false);
                if (st == ArchiveVersionStatus.Newer)
                    return ($"存档版本过新 v{aVer}（需要 v{CivMapArchive.Version}，请升级游戏）", false);
                return (null, false);   // 真损坏
            }
            return ($"seed={seed} · 石器时代 · " +
                    $"{tick * World.CivSim.CivSimContext.TickYears} 年 · 人口 {pop:F0} · 部落 {entities}",
                    true);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CmpSelectMenu] 存档异常 {path}: {ex}");
            return (null, false);   // 读取异常 → 损坏
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
        if (_locked.Contains(path))
        {
            _status.Text = "⚠️ 该存档版本与本游戏不兼容，无法进入。请用当前版本重新演化。";
            GD.Print($"[CmpSelectMenu] 拒绝进入版本不符存档 {path}");
            return;
        }
        ViewerLauncher.PendingPath = path;
        GetTree().ChangeSceneToFile("res://scenes/core/MapViewer.tscn");
    }
}
