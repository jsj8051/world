// Slice: MapViewer.Ui.cs - verbatim member extraction from MapViewer.cs (pure refactor, 2026-08-19).
using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Biome;
using World.Camera;
using World.HexPlanet;
using World.MapGen;
using World.PlanetLOD;
using World.Services;
using World.Surface;
using World.UI;
using static World.MapView.MapLayerColors;

namespace World.MapView;

public partial class MapViewer
{

    // ── 进度条 UI ──

    private void ShowProgress()
    {
        EnsureUi();
        _panel.Visible = true;
        _bar.Value = 0;
    }


    private void HideProgress()
    {
        if (_panel != null)
            _panel.Visible = false;
    }


    private void EnsureUi()
    {
        if (_uiLayer != null)
            return;

        _uiLayer = GetNode<CanvasLayer>("UiLayer");   // 场景骨架 CanvasLayer（Layer=100）

        // ── 静态骨架来自场景 MapViewer.tscn（%ProgressPanel / %CatRow / %LayerRow / %MonthRow / %LegendPanel）──
        _panel = GetNode<PanelContainer>("%ProgressPanel");   // 进度面板（默认隐藏）
        _label = GetNode<Label>("%ProgressLabel");
        _bar = GetNode<ProgressBar>("%ProgressBar");

        // ── 分类按钮行（场景预置 3 个：地理/气候/人文，ToggleGroup 互斥）──
        _catButtons = new[] { GetNode<Button>("%CatGeo"), GetNode<Button>("%CatClim"), GetNode<Button>("%CatHum") };
        for (int i = 0; i < _catButtons.Length; i++)
        {
            int cat = i; // 闭包捕获
            _catButtons[i].ToggleMode = true;
            _catButtons[i].Pressed += () =>
            {
                _category = (LayerCategory)cat;
                ShowCategoryButtons();   // 只切显示，不改 _layer（用户拍板）
                LogService.Log("MapViewer", $"category={CatNames[cat]} layer仍={LayerName(_layer)}");
            };
        }

        // ── 图层按钮行（行容器在场景 %LayerRow；17 个按钮按 LayerRegistry 动态生成）──
        var hbox = GetNode<HBoxContainer>("%LayerRow");
        _layerRow = hbox;
        var group = new ButtonGroup();
        _layerButtons = new Button[LayerRegistry.All.Count];
        for (int i = 0; i < LayerRegistry.All.Count; i++)
        {
            int idx = i; // 闭包捕获
            var btn = new Button
            {
                Icon = MakeLayerIcon(i),
                TooltipText = LayerRegistry.All[i].Name,
                ToggleMode = true,
                ButtonGroup = group,
                CustomMinimumSize = new Vector2(42, 38),
                IconAlignment = HorizontalAlignment.Center,
            };
            btn.AddThemeStyleboxOverride("normal", HudBtnStyle());
            btn.AddThemeStyleboxOverride("hover", HudBtnHoverStyle());
            btn.AddThemeStyleboxOverride("pressed", HudBtnPressedStyle());
            btn.AddThemeStyleboxOverride("focus", HudBtnStyle());
            btn.Pressed += () => Layer = idx;
            hbox.AddChild(btn);
            _layerButtons[i] = btn;
        }
        SyncLayerButtons();

        // ── 月份滑块（场景 %MonthRow，右下角；图层 10/11 显示；1-12 月切换季风箭头/月降水）──
        var monthRow = GetNode<HBoxContainer>("%MonthRow");
        _monthSlider = GetNode<HSlider>("%MonthSlider");
        _monthSlider.Value = _month + 1;
        _monthLabel = GetNode<Label>("%MonthLabel");
        _monthSlider.ValueChanged += v =>
        {
            int m = (int)v - 1;
            if (m == _month) return;
            _month = m;
            _monthLabel.Text = $"{m + 1} 月";
            // 2026-08-21 M3 策略化：UsesMonth 层的 OnMonthChanged 处理（风场重建箭头/月降水/月温度刷新+重算）
            if (_ctx != null)
            {
                _ctx.Month = m;   // ⚠️ 上下文快照同步（策略 BuildOverlay/刷新方法读 ctx.Month）
                var strat = LayerRegistry.Of(Layer);
                if (strat.UsesMonth)
                    strat.OnMonthChanged(_ctx, m);
            }
        };
        _monthSlider.Visible = false;   // 默认隐藏，进季风/月降水图层才显示

        // ── 图例面板（场景 %LegendPanel 外壳；标题/滚动/条目/footer 动态重建）──
        _legendPanel = GetNode<PanelContainer>("%LegendPanel");   // 固定 236×250 外壳，高度自适应由 RebuildLegend 调
        _legendTitle = GetNode<Label>("%LegendTitle");
        var legendVBox = _legendPanel.GetChild<VBoxContainer>(0);
        var scroll = GetNode<ScrollContainer>("%LegendScroll");
        _legendBox = GetNode<VBoxContainer>("%LegendBox");
        _legendFooter = GetNode<VBoxContainer>("%LegendFooter");
        // ⚠️ 2026-08-17：图例区滚轮只滚图例——ScrollContainer 滚到底不消费事件 → 穿透到
        //   3D 相机 _UnhandledInput → 地图缩放（用户报"滚动到底后再滚会导致地图缩放"）。
        //   在内容区（scroll+footer+标题）统一消费滚轮：滚动正常，滚到底/在说明文字上都不穿透。
        legendVBox.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed
                && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
                legendVBox.AcceptEvent();   // Control.AcceptEvent（C# 里 InputEvent 无此方法）
        };

        RebuildLegend();   // 初始图层图例
    }


    /// <summary>同步图层按钮的按下态与可见性（键盘/Inspector/分类切换时 UI 跟随）。
    /// 可见性 = 只显示当前分类的子按钮；按下态跟随 _layer；行位置按可见按钮数重算居中。
    /// ⚠️ 分类跟随：外部（Inspector/代码）直接改 Layer 时自动切到其所属分类，
    ///   保证选中按钮可见；但 UI 点分类按钮走 ShowCategoryButtons（不改 _layer）。</summary>
    private void SyncLayerButtons()
    {
        if (_layerButtons == null)
            return;
        // 外部改 Layer → 分类跟随（选中按钮必须在可见集合内）
        _category = LayerRegistry.Of(_layer).Category;
        for (int i = 0; i < _layerButtons.Length; i++)
            _layerButtons[i].ButtonPressed = i == _layer;
        ShowCategoryButtons();
    }


    /// <summary>按当前分类刷新图层按钮可见性 + 分类按钮按下态 + 行居中（不改 _layer）。</summary>
    private void ShowCategoryButtons()
    {
        if (_layerButtons == null)
            return;
        int visible = 0;
        for (int i = 0; i < _layerButtons.Length; i++)
        {
            bool show = LayerRegistry.All[i].Category == _category;
            _layerButtons[i].Visible = show;
            if (show) visible++;
        }
        for (int i = 0; i < _catButtons.Length; i++)
            _catButtons[i].ButtonPressed = (int)_category == i;
        // 42px/按钮 + 4px separation 居中；可见按钮数变化时重算。
        // ⚠️ 必须用 Offset（相对 anchor 的原始偏移）而非 Position——Position setter 会用
        //   父尺寸反推 offset（offset = pos - anchor×parentSize），AddChild 后调用会把
        //   rect 起点推到屏幕外（实测 global=(-113,-84)，2026-08-08）。
        float halfW = 21f * visible + 2f * (visible - 1);
        _layerRow.OffsetLeft = -halfW;
        _layerRow.OffsetTop = -84;
    }


    /// <summary>重建图例（当前图层颜色说明；内容超出固定面板 → ScrollContainer 滚动）。</summary>
    private void RebuildLegend()
    {
        if (_legendBox == null || _legendPanel == null) return;   // UI 未建（EnsureUi 前）或已释放
        // 清空旧条目（RemoveChild 立即脱离树 + QueueFree 帧末释放——纯 QueueFree 会残留到帧末）
        foreach (Node c in _legendBox.GetChildren())
        {
            _legendBox.RemoveChild(c);
            c.QueueFree();
        }
        if (_legendFooter != null)
            foreach (Node c in _legendFooter.GetChildren())
            {
                _legendFooter.RemoveChild(c);
                c.QueueFree();
            }
        // 2026-08-21 M3 策略化：图例条目由当前层策略 BuildLegend 提供（原 20 分支 switch 删除）
        var strat = LayerRegistry.Of(_layer);
        _legendTitle.Text = strat.Name;
        var builder = new LegendBuilder(_legendBox, _legendFooter);
        if (_ctx != null)
            strat.BuildLegend(builder, _ctx);
        else
            builder.Text("（生成中…）");   // ⚠️ M1 回归防护：构建前 _ctx/_cache 未就绪（原 case 13/16 的 NRE 隐患统一在此挡）

        // ⚠️ 2026-08-17 用户拍板：图例数量不足时面板高度自适应缩短（上限 250，贴底锚定）。
        //   内容高 = 色块行 min 高 + 行间隙；footer 常驻文字也计入；clamp [120, 250]。
        float contentH = 0f;
        for (int i = 0; i < _legendBox.GetChildCount(); i++)
            if (_legendBox.GetChild(i) is Control cc) contentH += cc.GetCombinedMinimumSize().Y;
        contentH += Mathf.Max(0, _legendBox.GetChildCount() - 1) * 3;
        float footH = 0f;
        for (int i = 0; i < _legendFooter.GetChildCount(); i++)
            if (_legendFooter.GetChild(i) is Control cc) footH += cc.GetCombinedMinimumSize().Y;
        footH += Mathf.Max(0, _legendFooter.GetChildCount() - 1) * 2;
        float panelH = Mathf.Clamp(26 + 4 + contentH + 4 + footH + 12, 120f, 250f);
        _legendPanel.CustomMinimumSize = new Vector2(236, panelH);
        // 贴底：BottomRight 锚点下 OffsetTop = -高（已入树必须用 Offset，Position setter 会飞屏）
        _legendPanel.OffsetTop = -panelH;
    }


    /// <summary>HUD 按钮样式（深空卡片风格，复用 SaveRowStyle 色板）。</summary>
    private static StyleBoxFlat HudBtnStyle()
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(0.0667f, 0.102f, 0.1725f, 0.92f),
            BorderColor = new Color(0.1176f, 0.1725f, 0.2784f, 0.9f),
            AntiAliasing = true,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(8);
        return s;
    }

    private static StyleBoxFlat HudBtnHoverStyle()
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(0.086f, 0.133f, 0.227f, 0.95f),
            BorderColor = new Color(0.1725f, 0.251f, 0.4f, 1f),
            AntiAliasing = true,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(8);
        return s;
    }

    private static StyleBoxFlat HudBtnPressedStyle()
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(0.114f, 0.227f, 0.388f, 1f),
            BorderColor = new Color(0.302f, 0.639f, 1f, 0.9f),
            AntiAliasing = true,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(8);
        return s;
    }

    /// <summary>人口显示取整（2026-08-17 用户反馈小数）：<1 显示 "<1"（防与无人灰混淆），≥1 整数。</summary>
    private static string FmtPop(float p) => p < 1f ? "<1" : $"{p:F0}";


    /// <summary>文化/宗教派别 → 语言群 映射（2026-08-21 M2：实现迁移至 LayerContext.EnsureIdentityCaches）。</summary>
    private void BuildIdentityCaches() => _ctx?.EnsureIdentityCaches();

}
