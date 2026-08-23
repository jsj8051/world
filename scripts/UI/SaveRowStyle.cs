using Godot;

namespace World.UI;

/// <summary>
/// 存档界面共享控件工厂（2026-08-23 存档界面视觉升级）。
/// 深空卡片风格：圆角 12 卡片行 + 图标块 + 等宽元数据 + 悬停高亮 + 红字删除按钮。
/// MapSelectMenu / CmpSelectMenu 共用，避免两处手写 StyleBox 漂移。
/// 纯静态工厂：每个 StyleBox 是共享实例（UI 无状态，安全复用）。
/// </summary>
public static class SaveRowStyle
{
    // ── 色板（与原型 HTML 一致）──
    public static readonly Color Bg = new(0.027f, 0.043f, 0.071f);          // #070b12
    public static readonly Color Bg2 = new(0.047f, 0.071f, 0.125f);         // #0c1220
    public static readonly Color Card = new(0.067f, 0.102f, 0.173f);        // #111a2c
    public static readonly Color CardHover = new(0.086f, 0.133f, 0.227f);   // #16223a
    public static readonly Color Border = new(0.118f, 0.173f, 0.278f);      // #1e2c47
    public static readonly Color BorderHi = new(0.173f, 0.251f, 0.400f);    // #2c4066
    public static readonly Color Fg = new(0.859f, 0.902f, 0.961f);          // #dbe6f5
    public static readonly Color Muted = new(0.490f, 0.545f, 0.643f);       // #7d8ba4
    public static readonly Color Faint = new(0.318f, 0.376f, 0.490f);       // #51607d
    public static readonly Color Accent = new(0.302f, 0.639f, 1.0f);        // #4da3ff
    public static readonly Color AccentDim = new(0.302f, 0.639f, 1.0f, 0.14f);
    public static readonly Color Red = new(1.0f, 0.365f, 0.365f);           // #ff5d5d
    public static readonly Color RedDim = new(1.0f, 0.365f, 0.365f, 0.12f);
    public static readonly Color Yellow = new(0.91f, 0.714f, 0.30f);        // #e8b64c
    public static readonly Color YellowDim = new(0.91f, 0.714f, 0.30f, 0.12f);
    public static readonly Color Gold = Yellow;                            // 含文明徽标（同金色系）
    public static readonly Color GoldDim = new(0.91f, 0.714f, 0.30f, 0.16f);

    private static SystemFont _mono;   // 等宽字体（元数据用），懒加载一次

    /// <summary>等宽字体（Cascadia/Consolas/Courier 回退链）。</summary>
    public static SystemFont MonoFont()
    {
        if (_mono == null)
            _mono = new SystemFont { FontNames = new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New" } };
        return _mono;
    }

    /// <summary>存档行卡片（normal/hover/pressed/focus 全套）。</summary>
    public static StyleBoxFlat CardStyle()
    {
        var s = Base(Border);
        s.BgColor = Card;
        return s;
    }

    public static StyleBoxFlat CardHoverStyle()
    {
        var s = Base(BorderHi);
        s.BgColor = CardHover;
        return s;
    }

    /// <summary>行内图标块（方形圆角，默认洋流蓝；文明用金色由调用方换）。</summary>
    public static StyleBoxFlat IconStyle()
    {
        var s = Base(new Color(0.302f, 0.639f, 1.0f, 0.25f));
        s.BgColor = AccentDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>坏档图标块（红）。</summary>
    public static StyleBoxFlat IconStyleRed()
    {
        var s = Base(new Color(1f, 0.365f, 0.365f, 0.3f));
        s.BgColor = RedDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>版本不符图标块（黄）。</summary>
    public static StyleBoxFlat IconStyleYellow()
    {
        var s = Base(new Color(0.91f, 0.714f, 0.30f, 0.3f));
        s.BgColor = YellowDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>含文明图标块（金）。</summary>
    public static StyleBoxFlat IconStyleGold()
    {
        var s = Base(new Color(0.91f, 0.714f, 0.30f, 0.35f));
        s.BgColor = GoldDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>删除按钮（红字 + 红底 hover）。</summary>
    public static StyleBoxFlat DangerNormal()
    {
        var s = Base(new Color(1f, 0.365f, 0.365f, 0.28f));
        s.BgColor = new Color(0f, 0f, 0f, 0f);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    public static StyleBoxFlat DangerHover()
    {
        var s = Base(Red);
        s.BgColor = RedDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    /// <summary>主按钮（进入/开始游戏，accent 蓝）。</summary>
    public static StyleBoxFlat PrimaryNormal()
    {
        var s = Base(new Color(0.302f, 0.639f, 1.0f, 0.4f));
        s.BgColor = new Color(0.302f, 0.639f, 1.0f, 0.12f);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    public static StyleBoxFlat PrimaryHover()
    {
        var s = Base(Accent);
        s.BgColor = new Color(0.302f, 0.639f, 1.0f, 0.26f);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    /// <summary>通用按钮（返回/取消）。</summary>
    public static StyleBoxFlat GhostNormal()
    {
        var s = Base(BorderHi);
        s.BgColor = Bg2;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    public static StyleBoxFlat GhostHover()
    {
        var s = Base(Accent);
        s.BgColor = new Color(0.102f, 0.165f, 0.271f);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    /// <summary>空状态虚线面板（用 2px 边框模拟）。</summary>
    public static StyleBoxFlat EmptyPanel()
    {
        var s = Base(BorderHi);
        s.BgColor = new Color(0f, 0f, 0f, 0f);
        s.SetBorderWidthAll(2);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 12;
        return s;
    }

    /// <summary>对话框面板（删除确认弹窗）。</summary>
    public static StyleBoxFlat DialogPanel()
    {
        var s = Base(BorderHi);
        s.BgColor = Bg2;
        s.SetCornerRadiusAll(14);
        return s;
    }

    /// <summary>顶部 logo 图标块（渐变方块 + 圆角，星球徽章）。</summary>
    public static StyleBoxFlat LogoStyle()
    {
        var s = Base(new Color(0.302f, 0.639f, 1.0f, 0.4f));
        s.BgColor = new Color(0.114f, 0.227f, 0.388f);   // #1d3a63
        s.SetCornerRadiusAll(10);
        s.ContentMarginLeft = s.ContentMarginRight = s.ContentMarginTop = s.ContentMarginBottom = 6;
        return s;
    }

    /// <summary>标题旁计数徽章（胶囊：圆角全圆 + 底色）。</summary>
    public static StyleBoxFlat CountBadge()
    {
        var s = Base(Border);
        s.BgColor = Card;
        s.SetCornerRadiusAll(999);
        s.ContentMarginLeft = s.ContentMarginRight = 14;
        s.ContentMarginTop = s.ContentMarginBottom = 4;
        return s;
    }

    /// <summary>删除后 Toast（底部胶囊提示）。</summary>
    public static StyleBoxFlat ToastStyle()
    {
        var s = Base(BorderHi);
        s.BgColor = Card;
        s.SetCornerRadiusAll(999);
        s.ContentMarginLeft = s.ContentMarginRight = 22;
        s.ContentMarginTop = s.ContentMarginBottom = 10;
        s.ShadowColor = new Color(0f, 0f, 0f, 0.45f);
        s.ShadowSize = 12;
        return s;
    }

    /// <summary>删除弹窗遮罩（半透明黑 + 极淡蓝）。</summary>
    public static readonly Color DimOverlay = new(0.016f, 0.027f, 0.051f, 0.72f);

    /// <summary>居中窗口框（照搬 HTML .window：渐变底色 + 边框 + 阴影 + 圆角 18）。</summary>
    public static StyleBoxFlat WindowStyle()
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(0.035f, 0.055f, 0.10f),   // 窗口底色（深蓝黑）
            BorderColor = BorderHi,
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize = 40,
            ShadowOffset = new Vector2(0, 14),
            AntiAliasing = true,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(16);
        return s;
    }

    /// <summary>标签页 active（顶部 2px accent 条 + 底色 + 上下边框）。</summary>
    public static StyleBoxFlat TabActive()
    {
        var s = Base(new Color(0.302f, 0.639f, 1.0f, 0.6f));
        s.BgColor = new Color(0.035f, 0.055f, 0.10f);
        s.SetCornerRadiusAll(10);
        s.ContentMarginLeft = s.ContentMarginRight = 16;
        s.ContentMarginTop = s.ContentMarginBottom = 8;
        return s;
    }

    /// <summary>标签页 inactive（透明底 + 细边框）。</summary>
    public static StyleBoxFlat TabInactive()
    {
        var s = Base(new Color(0.118f, 0.173f, 0.278f, 0.6f));
        s.BgColor = new Color(0f, 0f, 0f, 0f);
        s.SetCornerRadiusAll(10);
        s.ContentMarginLeft = s.ContentMarginRight = 16;
        s.ContentMarginTop = s.ContentMarginBottom = 8;
        return s;
    }

    /// <summary>标签页 inactive hover（微亮）。</summary>
    public static StyleBoxFlat TabInactiveHover()
    {
        var s = Base(new Color(0.173f, 0.251f, 0.400f, 0.8f));
        s.BgColor = new Color(0.067f, 0.102f, 0.173f, 0.5f);
        s.SetCornerRadiusAll(10);
        s.ContentMarginLeft = s.ContentMarginRight = 16;
        s.ContentMarginTop = s.ContentMarginBottom = 8;
        return s;
    }

    /// <summary>底框：1px 边框 + 圆角，半透明外框（悬停选中行加 accent 外框用）。</summary>
    public static StyleBoxFlat EnterOutline()
    {
        var s = Base(Accent);
        s.BgColor = new Color(0f, 0f, 0f, 0f);
        s.SetBorderWidthAll(2);
        s.SetCornerRadiusAll(12);
        return s;
    }

    private static StyleBoxFlat Base(Color border)
    {
        var s = new StyleBoxFlat
        {
            BorderColor = border,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(12);
        s.AntiAliasing = true;
        return s;
    }
}