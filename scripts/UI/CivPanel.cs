using Godot;
using World.CivSim.Observation;

namespace World.UI;

/// <summary>
/// 文明观测面板（2026-08-24 步骤②，docs/设计-观测面板与文明记录.md ③呈现层）。
/// 只读 CivSnapshot（投影 DTO）——**永不直达模拟内部**：加字段 = Observe 一处 + 本面板相关行，模拟重构零影响。
/// 页签：总览 / 政权 / 国家 / 科技（事件页待文明记录完成后接入）。
/// 渲染纪律：ShowSnapshot/切页签 = 整页重建（纯构建、无状态、无逐帧刷新——数据是一次性快照）。
/// 样式：SaveRowStyle 羊皮纸色板（与存档界面/主题一致，不另起色值）。
/// </summary>
public partial class CivPanel : Control
{
    // ── 场景骨架（%唯一名，见 CivPanel.tscn）──
    private PanelContainer _body;      // 面板本体（右上角；收起时隐藏）
    private Button _restoreBtn;        // 常驻小按钮（面板收起后的恢复入口）
    private VBoxContainer _list;       // 内容滚动列表（重建填充）
    private Button[] _tabButtons;      // 页签按钮（总览/政权/国家/科技）

    private CivSnapshot _snap;         // 当前快照（null=无数据）
    private int _tab;                  // 当前页签索引

    public override void _Ready()
    {
        _body = GetNode<PanelContainer>("%CivPanelBody");
        _restoreBtn = GetNode<Button>("%CivRestoreBtn");
        _list = GetNode<VBoxContainer>("%CivList");

        var row = GetNode<HBoxContainer>("%CivTabs");
        _tabButtons = new Button[row.GetChildCount()];
        for (int i = 0; i < _tabButtons.Length; i++)
        {
            var b = row.GetChild<Button>(i);
            int idx = i;   // 闭包捕获
            b.Pressed += () => { _tab = idx; RenderCurrent(); };
            _tabButtons[i] = b;
        }

        GetNode<Button>("%CivCollapseBtn").Pressed += Collapse;
        _restoreBtn.Pressed += Restore;

        Visible = false;   // 无数据不显示（MapViewer 加载 .cmp/.mpa 有文明后 ShowSnapshot）
    }

    /// <summary>入口：展示新快照（读档/演化完成路径共用）——置可见 + 渲染默认页。</summary>
    public void ShowSnapshot(CivSnapshot snap)
    {
        _snap = snap;
        _tab = 0;
        Visible = true;   // ⚠️ root 可见性是总开关（_Ready 默认隐藏；纯自然地图永不显）
        Restore();
        RenderCurrent();
    }

    // ──────────────────────────────────────────────
    // 页签渲染（整页重建：清空列表 → 按页签构建）
    // ──────────────────────────────────────────────

    private void RenderCurrent()
    {
        if (_snap == null) return;
        for (int i = 0; i < _tabButtons.Length; i++)
            _tabButtons[i].ButtonPressed = i == _tab;

        foreach (Node c in _list.GetChildren()) c.QueueFree();

        switch (_tab)
        {
            case 0: RenderOverview(); break;
            case 1: RenderPolities(); break;
            case 2: RenderStates(); break;
            case 3: RenderTechs(); break;
            case 4: RenderEvents(); break;
        }
    }

    /// <summary>总览页：纪元/年份 + 全物种计数 + 发展阶段。</summary>
    private void RenderOverview()
    {
        var s = _snap;
        bool neolithic = false;
        foreach (var p in s.Polities)
            if (p.IsFarming) { neolithic = true; break; }

        SectionTitle("世界概览");
        Row("纪元", neolithic ? "新石器（已转农）" : "旧石器 · 狩猎采集");
        Row("演化年", (s.Tick * 100L).ToString("N0") + " 年");
        Row("总人口", s.TotalPop.ToString("N0") + " 人");
        Row("政权数", s.PolityCount.ToString());
        Row("酋邦数", s.ChiefdomCount.ToString());
        Row("国家数", s.StateCount.ToString());
        Row("聚落数", s.HabitationCount.ToString());
        if (s.WarCount > 0) Row("⚔ 战事", s.WarCount + " 场进行中", SaveRowStyle.Red);

        SectionTitle("发展阶段");
        Row("文明阶段", StageName(s));
        if (s.States.Count > 0)
            Row("最强国家", $"#{s.States[0].Id}（成员 {s.States[0].MemberCount}）");
    }

    /// <summary>政权页：政体列表（快照已声望降序）——概念徽章 + 人口 + 归属。</summary>
    private void RenderPolities()
    {
        SectionTitle($"政权 · {_snap.PolityCount}");
        if (_snap.PolityCount == 0)
        {
            _list.AddChild(EmptyHint("暂存政权"));
            return;
        }
        foreach (var p in _snap.Polities)
        {
            var card = Card();
            var head = RowBox();
            head.AddChild(Badge(p.Concept));
            head.AddChild(MakeLabel($"#{p.Id} · {ConceptName(p.Concept)}", 13, SaveRowStyle.Fg, expand: true));
            head.AddChild(MakeLabel(PopText(p.Pop), 13, SaveRowStyle.Fg));
            card.AddChild(head);

            var sub = new System.Collections.Generic.List<string>(6);
            if (p.Prestige > 0.01f) sub.Add($"声望 {p.Prestige:F1}");
            if (p.TerritoryCells > 0) sub.Add($"领地 {p.TerritoryCells} 格");
            sub.Add($"科技 {p.TechCount}");
            if (p.PlaceId >= 0) sub.Add($"聚落 #{p.PlaceId}");
            if (p.StateId >= 0) sub.Add($"国家 #{p.StateId}");
            else if (p.ChiefdomId >= 0) sub.Add($"酋邦 #{p.ChiefdomId}");
            if (p.CultureGroup.Length > 0) sub.Add($"群 {p.CultureGroup}");
            card.AddChild(MakeLabel(string.Join(" · ", sub), 11, SaveRowStyle.Muted));
        }
    }

    /// <summary>国家页：国家卡片（都城/君主/贡赋池/成员/战争态）。</summary>
    private void RenderStates()
    {
        SectionTitle($"国家 · {_snap.States.Count}");
        if (_snap.States.Count == 0)
        {
            _list.AddChild(EmptyHint("尚无国家涌现（都城 + 贡赋池 + 存续 20 tick 三条件）"));
            return;
        }
        foreach (var st in _snap.States)
        {
            var card = Card();
            var head = RowBox();
            head.AddChild(Badge("state"));
            head.AddChild(MakeLabel($"国家 #{st.Id}", 13, SaveRowStyle.Fg, expand: true));
            if (st.IsAtWar) head.AddChild(MakeLabel("⚔ 交战中", 12, SaveRowStyle.Red));
            card.AddChild(head);

            RowIn(card, "都城", st.CapitalPlaceId >= 0 ? $"聚落 #{st.CapitalPlaceId}" : "无（制度缺位）");
            RowIn(card, "君主", $"#{st.MonarchId}");
            RowIn(card, "贡赋池", st.Pool.ToString("F1"));
            RowIn(card, "成员", st.MemberCount.ToString());
            RowIn(card, "科技", st.TechCount.ToString());
            RowIn(card, "声望", st.Prestige.ToString("F1"));
            if (st.CultureGroup.Length > 0) RowIn(card, "文化群", st.CultureGroup);
        }
    }

    /// <summary>科技页：全表 + 持有者数。</summary>
    private void RenderTechs()
    {
        SectionTitle($"科技卷轴 · {_snap.Techs.Count}");
        foreach (var t in _snap.Techs)
        {
            var card = Card();
            var head = RowBox();
            head.AddChild(MakeLabel(t.Name, 12, SaveRowStyle.Fg, expand: true));
            head.AddChild(MakeLabel($"{t.Holders} 家", 11, t.Holders > 0 ? SaveRowStyle.Accent : SaveRowStyle.Faint));
            card.AddChild(head);
        }
    }

    /// <summary>事件页：文明记录时间线（tick 升序；文本已在投影层派生）。</summary>
    private void RenderEvents()
    {
        SectionTitle($"文明记录 · {_snap.Events.Count}");
        if (_snap.Events.Count == 0)
        {
            _list.AddChild(EmptyHint("暂无事件（旧档无 EVNT 段 = 无历史；新演化自动记录）"));
            return;
        }
        foreach (var e in _snap.Events)
        {
            var card = Card();
            var head = RowBox();
            // 年份左对齐固定宽（tick × 100 年）——事件文本 expand
            head.AddChild(MakeLabel((e.Tick * 100L).ToString("N0") + "年", 11, SaveRowStyle.Accent, minWidth: 52));
            head.AddChild(MakeLabel(e.Text, 12, SaveRowStyle.Fg, expand: true));
            card.AddChild(head);
        }
    }

    // ──────────────────────────────────────────────
    // 收起/恢复
    // ──────────────────────────────────────────────

    private void Collapse()
    {
        _body.Visible = false;
        _restoreBtn.Visible = true;
    }

    private void Restore()
    {
        _restoreBtn.Visible = false;
        _body.Visible = true;
    }

    // ──────────────────────────────────────────────
    // 展示辅助（纯构建；实例方法——统一访问 _list）
    // ──────────────────────────────────────────────

    /// <summary>卷轴卡片：PanelContainer（羊皮纸卡片样式）直接挂列表，返回内层 VBox 供填充。</summary>
    private VBoxContainer Card()
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", SaveRowStyle.CardStyle());
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        panel.AddChild(v);
        _list.AddChild(panel);
        _list.AddChild(Spacer(4));
        return v;
    }

    private void SectionTitle(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 14);
        l.AddThemeColorOverride("font_color", SaveRowStyle.Accent);
        _list.AddChild(l);
        _list.AddChild(Spacer(2));
    }

    private void Row(string k, string v, Color? vColor = null)
    {
        var h = RowBox();
        h.AddChild(MakeLabel(k, 12, SaveRowStyle.Muted, minWidth: 88));
        h.AddChild(MakeLabel(v, 12, vColor ?? SaveRowStyle.Fg, expand: true));
        _list.AddChild(h);
        _list.AddChild(Spacer(1));
    }

    /// <summary>卡片内键值行。</summary>
    private static void RowIn(VBoxContainer card, string k, string v)
    {
        var h = RowBox();
        h.AddChild(MakeLabel(k, 11, SaveRowStyle.Muted, minWidth: 64));
        h.AddChild(MakeLabel(v, 11, SaveRowStyle.Fg, expand: true));
        card.AddChild(h);
    }

    /// <summary>概念徽章（单字色块：游/部/酋/国——语义色分档，同层浅底）。</summary>
    private static Control Badge(string concept)
    {
        var color = concept switch
        {
            "band" => SaveRowStyle.Faint,
            "tribe" => new Color(0.478f, 0.62f, 0.42f),
            "chiefdom" => SaveRowStyle.Accent,
            "state" => SaveRowStyle.Red,
            _ => SaveRowStyle.Muted,
        };
        char ch = concept switch
        {
            "band" => '游',
            "tribe" => '部',
            "chiefdom" => '酋',
            "state" => '国',
            _ => '?',
        };
        var p = new PanelContainer { CustomMinimumSize = new Vector2(26, 22) };
        p.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var sb = SaveRowStyle.IconStyle();
        sb.BgColor = new Color(color.R, color.G, color.B, 0.22f);
        sb.BorderColor = color;
        p.AddThemeStyleboxOverride("panel", sb);
        var l = new Label { Text = ch.ToString(), HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", 12);
        l.AddThemeColorOverride("font_color", color);
        p.AddChild(l);
        return p;
    }

    private static HBoxContainer RowBox()
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        return h;
    }

    private static Control Spacer(int px)
    {
        var s = new Control { CustomMinimumSize = new Vector2(0, px) };
        s.MouseFilter = Control.MouseFilterEnum.Ignore;
        return s;
    }

    private static Label MakeLabel(string text, int size, Color color, bool expand = false, float minWidth = 0f)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        if (expand) l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (minWidth > 0f) l.CustomMinimumSize = new Vector2(minWidth, 0);
        return l;
    }

    private static Label EmptyHint(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", 12);
        l.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
        return l;
    }

    private static string ConceptName(string concept) => concept switch
    {
        "band" => "游群",
        "tribe" => "部落",
        "chiefdom" => "酋邦",
        "state" => "国家",
        _ => concept,
    };

    /// <summary>文明阶段标签（按涌现深度）。</summary>
    private static string StageName(CivSnapshot s)
    {
        if (s.States.Count > 0) return "国家时代（制度化）";
        if (s.ChiefdomCount > 0) return "酋邦时代（声望整合）";
        foreach (var p in s.Polities)
            if (p.IsFarming) return "农业部落时代";
        return "游群时代（旧石器）";
    }

    private static string PopText(float p) => p < 1f ? "<1" : p.ToString("N0");
}