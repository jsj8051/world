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

        _uiLayer = new CanvasLayer { Layer = 100 };
        AddChild(_uiLayer);

        _panel = new PanelContainer();
        _panel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _panel.Position = new Vector2(-470, -90);
        _uiLayer.AddChild(_panel);

        var vbox = new VBoxContainer();
        _panel.AddChild(vbox);

        _label = new Label { Text = "生成星球中..." };
        vbox.AddChild(_label);

        _bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            // ⚠️ 2026-08-16：去掉 ShowPercentage——内嵌百分比与 _label 双显示取整不一致
            //   （用户见 89/88 两个数字）。只保留条，数字统一由 _label 显示。
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(420, 26)
        };
        vbox.AddChild(_bar);

        _panel.Visible = false;

        // ── 分类按钮行（最底下一排：地理/气候/人文）──
        // 2026-08 用户拍板：17 图层分三类；点分类只切换上方子按钮显示，不改变当前图层。
        var catGroup = new ButtonGroup();
        var catBox = new HBoxContainer();
        catBox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom); // 锚点底部居中
        catBox.Position = new Vector2(-128, -44);   // 3×80px + 8px 间距居中
        catBox.AddThemeConstantOverride("separation", 8);
        _uiLayer.AddChild(catBox);

        _catButtons = new Button[CatNames.Length];
        for (int i = 0; i < CatNames.Length; i++)
        {
            int cat = i; // 闭包捕获
            var btn = new Button
            {
                Text = CatNames[i],
                ToggleMode = true,
                ButtonGroup = catGroup,
                CustomMinimumSize = new Vector2(80, 32),
            };
            btn.Pressed += () =>
            {
                _category = (LayerCategory)cat;
                ShowCategoryButtons();   // 只切显示，不改 _layer（用户拍板）
                LogService.Log("MapViewer", $"category={CatNames[cat]} layer仍={LayerName(_layer)}");
            };
            catBox.AddChild(btn);
            _catButtons[i] = btn;
        }

        // ── 图层按钮行（分类按钮上方；只显示当前分类的子图层，其余隐藏）──
        // ⚠️ 2026-08-02 v3：SVG 图标按钮。只显示 4 个的真相 = 后 3 个 SVG 用了
        //   Q/T/A 曲线命令，thorvg 解析器不支持 → 加载失败空白（非宽度问题）。
        //   全部图标已重写为纯直线命令 M/L/H/V/Z。悬停 TooltipText 显示中文名。
        var group = new ButtonGroup();
        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.CenterBottom); // 锚点底部居中
        hbox.Position = new Vector2(-21f * LayerRegistry.All.Count, -84); // 占位，SyncLayerButtons 重算
        _uiLayer.AddChild(hbox);
        _layerRow = hbox;

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
            btn.Pressed += () => Layer = idx;
            hbox.AddChild(btn);
            _layerButtons[i] = btn;
        }
        SyncLayerButtons();

        // ── 月份滑块（右下角；图层 10/11 显示；1-12 月切换季风箭头/月降水）──
        var monthRow = new HBoxContainer();
        monthRow.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        monthRow.Position = new Vector2(-300, -44);   // 右下角（用户拍板 2026-08）
        monthRow.AddThemeConstantOverride("separation", 8);
        _uiLayer.AddChild(monthRow);

        var mlabel = new Label { Text = "月份", VerticalAlignment = VerticalAlignment.Center };
        mlabel.AddThemeFontSizeOverride("font_size", 18);
        monthRow.AddChild(mlabel);

        _monthSlider = new HSlider
        {
            MinValue = 1,
            MaxValue = 12,
            Step = 1,
            Value = _month + 1,
            CustomMinimumSize = new Vector2(200, 34),
        };
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
        monthRow.AddChild(_monthSlider);

        _monthLabel = new Label { Text = $"{_month + 1} 月", VerticalAlignment = VerticalAlignment.Center };
        _monthLabel.AddThemeFontSizeOverride("font_size", 18);
        _monthLabel.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
        monthRow.AddChild(_monthLabel);

        _monthSlider.Visible = false;   // 默认隐藏，进季风/月降水图层才显示

        // ── 图例面板（月份滑块左侧，固定大小，内容超出滚动）──
        // 2026-08-08：图例 = 当前图层颜色说明；放右下角月份滑块左边。
        // 固定大小 236×320；ScrollContainer 垂直滚动（生物群系 22 条目/文化动态条目必然超界）。
        _legendPanel = new PanelContainer();
        _legendPanel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        // ⚠️ 必须在 AddChild 前设 Position（入树后 Position setter 会用父尺寸反推 offset → 屏幕外）
        // 2026-08-17 修复链：BottomRight 锚点下 Position 的 y 是【顶缘】偏移——必须 = -(底边距+面板高)。
        //   ① 原 (-560,-52) 顶缘在底上 52px → 面板 268px 裁出屏幕（用户报"图例太矮"）；
        //   ② 加高 500 → 用户嫌高 → 缩半 250（内容滚动）；
        //   ③ 用户要求锚定到底部 → 底边距 0，完全贴屏幕底（滑块在右侧水平分离不冲突）。
        _legendPanel.Position = new Vector2(-560, -250);
        _legendPanel.CustomMinimumSize = new Vector2(236, 250);
        _legendPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.08f, 0.12f, 0.85f),
            BorderColor = new Color(0.35f, 0.40f, 0.50f, 0.9f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        });
        _uiLayer.AddChild(_legendPanel);

        var legendVBox = new VBoxContainer();
        legendVBox.AddThemeConstantOverride("separation", 4);
        _legendPanel.AddChild(legendVBox);

        _legendTitle = new Label
        {
            Text = LayerRegistry.Of(0).Name,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 26),
        };
        _legendTitle.AddThemeFontSizeOverride("font_size", 17);
        _legendTitle.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.85f));
        legendVBox.AddChild(_legendTitle);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            // 2026-08-17：面板高度随内容自适应（上限 250）→ scroll min 只保底，不撑大面板
            CustomMinimumSize = new Vector2(220, 40),
            // ⚠️ 2026-08-17 用户反馈"底下留白"：VBox 不拉伸非 expand 控件 → 固定 250 面板里
            //   内容不足时余白堆在底部。ExpandFill = 滚动区动态吃掉全部剩余高度（无留白）。
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        legendVBox.AddChild(scroll);
        // ⚠️ 2026-08-17：图例区滚轮只滚图例——ScrollContainer 滚到底不消费事件 → 穿透到
        //   3D 相机 _UnhandledInput → 地图缩放（用户报"滚动到底后再滚会导致地图缩放"）。
        //   在内容区（scroll+footer+标题）统一消费滚轮：滚动正常，滚到底/在说明文字上都不穿透。
        legendVBox.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed
                && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
                legendVBox.AcceptEvent();   // Control.AcceptEvent（C# 里 InputEvent 无此方法）
        };

        _legendBox = new VBoxContainer();
        _legendBox.AddThemeConstantOverride("separation", 3);
        _legendBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_legendBox);

        // 常驻说明文字区（滚动区外）：AddLegendText 的灰色说明行固定显示在面板底部
        _legendFooter = new VBoxContainer();
        _legendFooter.AddThemeConstantOverride("separation", 2);
        _legendFooter.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        legendVBox.AddChild(_legendFooter);

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


    /// <summary>人口显示取整（2026-08-17 用户反馈小数）：<1 显示 "<1"（防与无人灰混淆），≥1 整数。</summary>
    private static string FmtPop(float p) => p < 1f ? "<1" : $"{p:F0}";


    /// <summary>文化/宗教派别 → 语言群 映射（2026-08-21 M2：实现迁移至 LayerContext.EnsureIdentityCaches）。</summary>
    private void BuildIdentityCaches() => _ctx?.EnsureIdentityCaches();

}
