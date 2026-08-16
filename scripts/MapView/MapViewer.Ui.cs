// Slice: MapViewer.Ui.cs - verbatim member extraction from MapViewer.cs (pure refactor, 2026-08-19).
using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Biome;
using World.MapGen;
using World.HexPlanet;
using World.PlanetLOD;
using World.Surface;
using World.UI;
using World.Camera;

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
                _category = (LayerCat)cat;
                ShowCategoryButtons();   // 只切显示，不改 _layer（用户拍板）
                GD.Print($"[MapViewer] category={CatNames[cat]} layer仍={LayerName(_layer)}");
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
        hbox.Position = new Vector2(-21f * LayerNames.Length, -84); // 占位，SyncLayerButtons 重算
        _uiLayer.AddChild(hbox);
        _layerRow = hbox;

        _layerButtons = new Button[LayerNames.Length];
        for (int i = 0; i < LayerNames.Length; i++)
        {
            int idx = i; // 闭包捕获
            var btn = new Button
            {
                Icon = MakeLayerIcon(i),
                TooltipText = LayerNames[i],
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
            // 风场图层：重建箭头；月降水/月温度图层：刷新缓存 + 重算颜色
            if (Layer == 4) BuildMonsoonArrows();
            else if (Layer == 10) { RefreshMonthPrecip(); RebuildColors(); }
            else if (Layer == 11) { RefreshMonthTemp(); RebuildColors(); }
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
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        _uiLayer.AddChild(_legendPanel);

        var legendVBox = new VBoxContainer();
        legendVBox.AddThemeConstantOverride("separation", 4);
        _legendPanel.AddChild(legendVBox);

        _legendTitle = new Label
        {
            Text = LayerNames[0],
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
        _category = LayerCats[_layer];
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
            bool show = LayerCats[i] == _category;
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
        _legendTitle.Text = LayerNames[_layer];

        switch (_layer)
        {
            case 0: // 海拔（2026-08-18）：海 <-200m 深海 / -200~0m 浅海；陆地连续色带
                AddLegendGradient(
                    new[] { new Color(0.01f, 0.05f, 0.18f), new Color(0.20f, 0.45f, 0.68f),
                            new Color(0.70f, 0.65f, 0.40f), new Color(0.30f, 0.65f, 0.10f),
                            new Color(0.60f, 0.50f, 0.35f), new Color(0.95f, 0.97f, 1.00f) },
                    "深海<-200m", "最高");
                AddLegendText("海：<-200m 深海 / -200~0m 浅海（大陆架）；陆：连续色带（实际米）");
                break;
            case 1: // 温度：分段色带
                AddLegendGradient(
                    new[] { new Color(0.08f, 0.12f, 0.45f), new Color(0.22f, 0.52f, 0.72f), new Color(0.38f, 0.72f, 0.42f), new Color(0.92f, 0.78f, 0.28f), new Color(0.88f, 0.30f, 0.15f) },
                    "-85°C", "+45°C");
                AddLegendText("分段色带：极寒/冰点/0-15°/宜居/高温");
                break;
            case 2: // 降水
                AddLegendGradient(
                    new[] { new Color(0.90f, 0.80f, 0.40f), new Color(0.10f, 0.30f, 0.70f) },
                    $"{_precipMin:F0}mm", $"{_precipMax:F0}mm");
                AddLegendText("陆地自适应色带（随地图分布）");
                break;
            case 3: // 生物群系
                for (int b = 0; b < BiomeNames.Length; b++)
                {
                    if (string.IsNullOrEmpty(BiomeNames[b])) continue;
                    AddLegendRow(BiomeColors.BiomeToColor((BiomeType)b), BiomeNames[b]);
                }
                break;
            case 4: // 风场
                AddLegendText("→ 箭头 = 盛行风向（月风场）");
                AddLegendText("疏密 = 风速强度");
                AddLegendText("月份滑块切换 1-12 月");
                break;
            case 5: // 洋流
                AddLegendRow(new Color(0.95f, 0.35f, 0.25f), "暖流");
                AddLegendRow(new Color(0.25f, 0.55f, 0.95f), "寒流");
                AddLegendText("箭头大小 = 流速");
                break;
            case 6: // 河流
                AddLegendRow(new Color(0.25f, 0.45f, 0.75f), "湖泊");
                AddLegendRow(new Color(0.35f, 0.70f, 1.00f), "河流");
                AddLegendText("干涸盆地（盐湖）不显示");
                break;
            case 7: // 流域
                AddLegendText("每流域独立颜色");
                AddLegendText("海洋/边缘排水区 = 浅蓝/灰绿");
                break;
            case 8: // 矿藏
                for (int m = 1; m < MineralSystem.Names.Length; m++)
                    AddLegendRow(MineralColors[m], MineralSystem.Names[m]);
                AddLegendText("明度 = 富度（贫暗/富中/巨型亮）");
                break;
            case 9: // 土壤
                for (int s = 1; s <= 5; s++)
                    AddLegendRow(SoilColors[s], SoilNames[s]);
                break;
            case 10: // 月降水
                AddLegendGradient(
                    new[] { new Color(0.90f, 0.80f, 0.40f), new Color(0.10f, 0.30f, 0.70f) },
                    $"{_monthPrecipMin:F0}mm", $"{_monthPrecipMax:F0}mm");
                AddLegendText("当月降水（×12 年尺度色带）");
                break;
            case 11: // 月温度
                AddLegendGradient(
                    new[] { new Color(0.08f, 0.12f, 0.45f), new Color(0.22f, 0.52f, 0.72f), new Color(0.38f, 0.72f, 0.42f), new Color(0.92f, 0.78f, 0.28f), new Color(0.88f, 0.30f, 0.15f) },
                    "-60°C", "+60°C");
                AddLegendText("当月均温");
                break;
            case 12: // 人口：无人（采集格）+ 16 档等比色块（log 分位，与地图同色；驻扎格人口）
            {
                var lo = new Color(0.95f, 0.75f, 0.25f);
                var hi = new Color(0.80f, 0.15f, 0.05f);
                AddLegendRow(new Color(0.25f, 0.25f, 0.28f), "无人（采集格 / 海洋）");
                if (_popMax <= 0f)
                {
                    AddLegendText("（无人口数据）");
                    break;
                }
                for (int i = 0; i <= 15; i++)
                {
                    float x = i / 15f;
                    float p = Mathf.Exp(_popLogMin + x * (_popLogMax - _popLogMin)) - 1f;
                    // ⚠️ 2026-08-17 用户反馈"人口怎么还能是小数"：人口物理上是整数——
                    //   模型层 P 是 float（连续宏观增长），显示层取整（<1 显示 "<1" 防与无人灰混淆）
                    string label = i == 15 ? $"≥ {FmtPop(p)}（最高 {FmtPop(_popMax)}）" : FmtPop(p);
                    AddLegendRow(lo.Lerp(hi, x), label);
                }
                AddLegendText("驻扎格人口（人/格）· log 分位自适应");
                break;
            }
            case 13: // 文化：动态条目（同语言群同色系——族色相 + 文化深浅；按覆盖格数排序，滚动查看）
                AddLegendText("同语言群同色系（深浅=具体文化，族域连贯）");
                BuildIdentityCaches();
                AddLegendDynamic(_tileCulture, c => FamilyColor(_cultGroup.TryGetValue(c, out var g) ? g : c, c, 0.60f, 0.25f), "文化");
                break;
            case 14: // 独立势力（2026-08-17）：每势力独立色——最高聚合层（酋邦>部落>band）
                AddLegendRow(new Color(0.25f, 0.25f, 0.28f), "无人 / 海洋");
                AddLegendText("每独立势力一种颜色（两两可区分）");
                AddLegendText("酋邦（跨部落联盟）> 部落（领地≥2）> 独立 band");
                break;
            case 15: // 科技
                for (int e = 0; e <= 4; e++)
                {
                    var col = e == 0 ? new Color(0.55f, 0.42f, 0.28f) : TechEpochColors[e - 1];
                    AddLegendRow(col, TechEpochNames[e]);
                }
                break;
            case 16: // 宗教：动态条目（同语言群同色系——族色相 + 派别深浅）
                AddLegendText("同语言群同色系（深浅=具体派别）");
                BuildIdentityCaches();
                AddLegendDynamic(_tileReligion, r => FamilyColor(_sectGroup.TryGetValue(r, out var g) ? g : r, r, 0.60f, 0.25f), "派别");
                break;
            case 17: // 势力范围：静态说明（每领地独立色，动态条目过多故仅说明）
                AddLegendRow(new Color(0.30f, 0.32f, 0.36f), "无领地");
                AddLegendText("每领地独立颜色（两两可区分）");
                AddLegendText("同领地必同语言群 → 同领地同色");
                break;
            case 18: // 政体（2026-08-17）：独立势力基础上按政体类型分色
                AddLegendRow(HslToRgb(0.60f, 0.30f, 0.55f), "独立 band（无组织）");
                AddLegendRow(HslToRgb(0.35f, 0.50f, 0.55f), "部落（领地凝聚）");
                AddLegendRow(HslToRgb(0.045f, 0.58f, 0.55f), "酋邦（联盟+酋长）");
                AddLegendRow(HslToRgb(0.12f, 0.45f, 0.55f), "国家（都城+官僚，2026-08-16 阶段4）");
                AddLegendText("同类政体同色系；势力间色相微扰可辨");
                break;
            case 19: // 聚落（2026-08-19 阶段3 聚落设计）
                AddLegendRow(SettlementLevelColors[0], "新村/营地");
                AddLegendRow(SettlementLevelColors[1], "村庄");
                AddLegendRow(SettlementLevelColors[2], "城镇");
                AddLegendRow(SettlementLevelColors[3], "城市");
                AddLegendRow(SettlementLevelColors[4], "废墟");
                AddLegendText("农业部落（settle）驻扎点固化；场所比人长寿，新部落可接管");
                break;
        }
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


    /// <summary>图例条目：色块 + 文字（横向）。</summary>
    private void AddLegendRow(Color c, string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var swatch = new ColorRect
        {
            Color = c,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(swatch);
        var lab = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        lab.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(lab);
        _legendBox.AddChild(row);
    }


    /// <summary>图例条目：渐变色带 + 两端标注。</summary>
    private void AddLegendGradient(Color[] stops, string low, string high)
    {
        // Offsets 动态生成（段数任意；均匀分布）
        var offs = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++)
            offs[i] = stops.Length > 1 ? i / (float)(stops.Length - 1) : 0f;
        var bar = new GradientTexture2D
        {
            Gradient = new Gradient { Offsets = offs, Colors = stops },
            Fill = GradientTexture2D.FillEnum.Linear,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(1, 0),
            Width = 180, Height = 14,
        };
        var tr = new TextureRect
        {
            Texture = bar,
            CustomMinimumSize = new Vector2(180, 14),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        _legendBox.AddChild(tr);
        var labels = new HBoxContainer();
        var lo = new Label { Text = low }; lo.AddThemeFontSizeOverride("font_size", 12);
        var hi = new Label { Text = high, HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hi.AddThemeFontSizeOverride("font_size", 12);
        labels.AddChild(lo);
        labels.AddChild(hi);
        _legendBox.AddChild(labels);
    }


    /// <summary>图例条目：纯说明文字（小字号、浅灰）——输出到滚动区外的常驻底部区
    /// （2026-08-17 用户拍板：说明文字固定显示，不随条目滚动）。</summary>
    private void AddLegendText(string text)
    {
        if (_legendFooter == null) return;
        var lab = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lab.AddThemeFontSizeOverride("font_size", 12);
        lab.AddThemeColorOverride("font_color", new Color(0.75f, 0.78f, 0.85f));
        _legendFooter.AddChild(lab);
    }


    /// <summary>图例动态条目：统计数组中出现过的 key，按覆盖格数降序显示前 12 个（超出滚动查看）。</summary>
    private void AddLegendDynamic(int[] tileKeys, System.Func<int, Color> colorOf, string kind)
    {
        if (tileKeys == null)
        {
            AddLegendText($"（{kind}数据未加载）");
            return;
        }
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        for (int i = 0; i < tileKeys.Length; i++)
        {
            int k = tileKeys[i];
            if (k == 0) continue;
            counts[k] = counts.TryGetValue(k, out int v) ? v + 1 : 1;
        }
        if (counts.Count == 0)
        {
            AddLegendText("（无）");
            return;
        }
        var sorted = new System.Collections.Generic.List<KeyValuePair<int, int>>(counts);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
        int shown = Mathf.Min(12, sorted.Count);
        for (int i = 0; i < shown; i++)
            AddLegendRow(colorOf(sorted[i].Key), $"{kind} {sorted[i].Key}（{sorted[i].Value}格）");
        if (sorted.Count > shown)
            AddLegendText($"…共 {sorted.Count} 个{kind}（滚动查看）");
    }


    /// <summary>文化/宗教派别 → 语言群 映射（图例族系取色用；惰性建一次——实体表只读）。</summary>
    private void BuildIdentityCaches()
    {
        if (_cultGroup != null || _civCtx == null) return;
        _cultGroup = new Dictionary<int, int>();
        _sectGroup = new Dictionary<int, int>();
        foreach (var e in _civCtx.Tribes)
        {
            if (e.Dead) continue;
            int c = World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(e.CultureShare));
            int r = World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(e.ReligionCultShare));
            int g = World.CivSim.ShareField.KeyHash(World.CivSim.ShareField.DomKey(e.CultureGroupShare));
            if (c != 0 && !_cultGroup.ContainsKey(c)) _cultGroup[c] = g;
            if (r != 0 && !_sectGroup.ContainsKey(r)) _sectGroup[r] = g;
        }
    }

}
