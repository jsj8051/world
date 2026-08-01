using Godot;

namespace World.UI;

/// <summary>
/// 主菜单：生成地图 / 进入游戏 两个入口。
/// 纯代码构建 UI。布局：直接锚点定位（不嵌套容器，避免布局/鼠标过滤问题）。
/// </summary>
public partial class MainMenu : Control
{
    public override void _Ready()
    {
        // 背景（铺满全屏，MouseFilter.Ignore 不拦截任何点击）
        var bg = new ColorRect { Color = new Color(0.06f, 0.08f, 0.12f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        // 标题：居中顶部
        var title = new Label
        {
            Text = "🌍 世界生成器",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 120),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 64);
        title.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 1f));
        title.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(title);

        // 生成地图按钮：屏幕中央上方
        var genBtn = MakeButton("🛠  生成地图");
        genBtn.SetAnchorsPreset(LayoutPreset.Center);
        genBtn.Position = new Vector2(-200, -160);
        genBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/MapGenMenu.tscn");
        AddChild(genBtn);

        // 进入游戏按钮：屏幕中央下方
        var playBtn = MakeButton("▶  进入游戏");
        playBtn.SetAnchorsPreset(LayoutPreset.Center);
        playBtn.Position = new Vector2(-200, -40);
        playBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/MapSelectMenu.tscn");
        AddChild(playBtn);

        // 底部版本信息
        var footer = new Label
        {
            Text = "板块构造模拟 v3 · 球面直通存档 · 2026-08-02",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        footer.AddThemeFontSizeOverride("font_size", 14);
        footer.AddThemeColorOverride("font_color", new Color(0.4f, 0.45f, 0.55f));
        footer.SetAnchorsPreset(LayoutPreset.BottomWide);
        footer.Position = new Vector2(0, -60);
        AddChild(footer);

        GD.Print("[MainMenu] ready");
    }

    private Button MakeButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(400, 76),
            MouseFilter = MouseFilterEnum.Stop,
        };
        btn.AddThemeFontSizeOverride("font_size", 28);
        return btn;
    }
}
