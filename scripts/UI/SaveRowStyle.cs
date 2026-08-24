using Godot;

namespace World.UI;

/// <summary>
/// 存档界面共享控件工厂（2026-08-23 存档界面视觉升级；2026-08-24 中世纪羊皮纸主题）。
/// 羊皮纸手稿风格：暖黄纸底 + 墨色文字 + 金箔点缀 + 赭红危险色，典籍/古海图气质。
/// 2026-08-24 对比度修订：金箔/灰棕加深，保证亮底可读（用户反馈浅金太淡费劲）。
/// MapSelectMenu / CmpSelectMenu 共用，避免两处手写 StyleBox 漂移。
/// 纯静态工厂：每个 StyleBox 是共享实例（UI 无状态，安全复用）。
/// </summary>
public static class SaveRowStyle
{
    // ── 色板（羊皮纸手稿，2026-08-24 用户拍板：暖色中世纪替换深空蓝；同日对比度加深修订）──
    public static readonly Color Bg = new(0.902f, 0.843f, 0.702f);          // #e6d7b3 羊皮纸底
    public static readonly Color Bg2 = new(0.851f, 0.776f, 0.608f);         // #d9c69b 深一档羊皮纸
    public static readonly Color Card = new(0.957f, 0.925f, 0.831f);        // #f4ecd4 卷轴卡片
    public static readonly Color CardHover = new(0.984f, 0.961f, 0.878f);   // #fbf5e0 悬停
    public static readonly Color Border = new(0.490f, 0.396f, 0.220f);      // #7d6538 描边（比背景深，可辨）
    public static readonly Color BorderHi = new(0.373f, 0.290f, 0.141f);    // #5f4a24 深描边
    public static readonly Color Fg = new(0.227f, 0.173f, 0.102f);          // #3a2c1a 墨色文字
    public static readonly Color Muted = new(0.353f, 0.290f, 0.180f);       // #5a4a2e 淡墨（元数据，加深保可读）
    public static readonly Color Faint = new(0.435f, 0.373f, 0.255f);       // #6f5f41 最淡墨（版本号）
    public static readonly Color Accent = new(0.722f, 0.525f, 0.043f);      // #b8860b 深金箔（强调/描边）
    public static readonly Color AccentDim = new(0.722f, 0.525f, 0.043f, 0.22f);
    public static readonly Color Red = new(0.557f, 0.141f, 0.141f);         // #8e2424 赭红（危险）
    public static readonly Color RedDim = new(0.557f, 0.141f, 0.141f, 0.16f);
    public static readonly Color Yellow = new(0.663f, 0.482f, 0.094f);      // #a97b18 琥珀（版本不符/警示）
    public static readonly Color YellowDim = new(0.663f, 0.482f, 0.094f, 0.16f);
    public static readonly Color Gold = Yellow;                            // 文明徽标（同琥珀金系）
    public static readonly Color GoldDim = new(0.663f, 0.482f, 0.094f, 0.20f);

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

    /// <summary>行内图标块（方形圆角，默认金箔；文明用金由调用方换）。</summary>
    public static StyleBoxFlat IconStyle()
    {
        var s = Base(Accent);
        s.BgColor = AccentDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>坏档图标块（赭红）。</summary>
    public static StyleBoxFlat IconStyleRed()
    {
        var s = Base(Red);
        s.BgColor = RedDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>版本不符图标块（琥珀）。</summary>
    public static StyleBoxFlat IconStyleYellow()
    {
        var s = Base(Yellow);
        s.BgColor = YellowDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>含文明图标块（金）。</summary>
    public static StyleBoxFlat IconStyleGold()
    {
        var s = Base(Gold);
        s.BgColor = GoldDim;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 9;
        return s;
    }

    /// <summary>删除按钮（赭红字 + 赭红底 hover）。</summary>
    public static StyleBoxFlat DangerNormal()
    {
        var s = Base(Red);
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

    /// <summary>主按钮（进入/开始游戏，深金箔底）。</summary>
    public static StyleBoxFlat PrimaryNormal()
    {
        var s = Base(new Color(0.373f, 0.290f, 0.141f, 1f));
        s.BgColor = new Color(0.722f, 0.525f, 0.043f, 0.85f);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    public static StyleBoxFlat PrimaryHover()
    {
        var s = Base(new Color(0.373f, 0.290f, 0.141f, 1f));
        s.BgColor = new Color(0.788f, 0.6f, 0.12f, 1f);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    /// <summary>通用按钮（返回/取消）。</summary>
    public static StyleBoxFlat GhostNormal()
    {
        var s = Base(Border);
        s.BgColor = Bg2;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    public static StyleBoxFlat GhostHover()
    {
        var s = Base(BorderHi);
        s.BgColor = new Color(0.851f, 0.776f, 0.608f, 1f);
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = 8;
        return s;
    }

    /// <summary>空状态虚线面板（用 2px 边框模拟）。</summary>
    public static StyleBoxFlat EmptyPanel()
    {
        var s = Base(Border);
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
        var s = Base(Accent);
        s.BgColor = new Color(0.557f, 0.141f, 0.141f);   // 赭红印章
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
        s.ShadowColor = new Color(0.1f, 0.07f, 0.03f, 0.4f);
        s.ShadowSize = 12;
        return s;
    }

    /// <summary>删除弹窗遮罩（半透明暖褐）。</summary>
    public static readonly Color DimOverlay = new(0.078f, 0.059f, 0.031f, 0.62f);

    /// <summary>居中窗口框（羊皮纸窗口：底色 + 边框 + 阴影 + 圆角 18）。</summary>
    public static StyleBoxFlat WindowStyle()
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(0.851f, 0.776f, 0.608f),   // 窗口底色（深一档羊皮纸）
            BorderColor = BorderHi,
            ShadowColor = new Color(0.1f, 0.07f, 0.03f, 0.5f),
            ShadowSize = 40,
            ShadowOffset = new Vector2(0, 14),
            AntiAliasing = true,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(16);
        return s;
    }

    /// <summary>标签页 active（顶部 2px 金条 + 卡片底 + 上下边框）。</summary>
    public static StyleBoxFlat TabActive()
    {
        var s = Base(new Color(0.722f, 0.525f, 0.043f, 0.9f));
        s.BgColor = Card;
        s.SetCornerRadiusAll(10);
        s.ContentMarginLeft = s.ContentMarginRight = 16;
        s.ContentMarginTop = s.ContentMarginBottom = 8;
        return s;
    }

    /// <summary>标签页 inactive（透明底 + 细边框）。</summary>
    public static StyleBoxFlat TabInactive()
    {
        var s = Base(new Color(0.490f, 0.396f, 0.220f, 0.65f));
        s.BgColor = new Color(0f, 0f, 0f, 0f);
        s.SetCornerRadiusAll(10);
        s.ContentMarginLeft = s.ContentMarginRight = 16;
        s.ContentMarginTop = s.ContentMarginBottom = 8;
        return s;
    }

    /// <summary>标签页 inactive hover（微亮）。</summary>
    public static StyleBoxFlat TabInactiveHover()
    {
        var s = Base(new Color(0.373f, 0.290f, 0.141f, 0.8f));
        s.BgColor = new Color(0.957f, 0.925f, 0.831f, 0.55f);
        s.SetCornerRadiusAll(10);
        s.ContentMarginLeft = s.ContentMarginRight = 16;
        s.ContentMarginTop = s.ContentMarginBottom = 8;
        return s;
    }

    /// <summary>底框：1px 边框 + 圆角，金外框（悬停选中行加 accent 外框用）。</summary>
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