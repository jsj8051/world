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
        genBtn.Position = new Vector2(-200, -240);
        genBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MapGenMenu.tscn");
        AddChild(genBtn);

        // 读取自然地图按钮（读 .mpa/.gmp 用 MapViewer 查看自然图层；原"进入游戏"改名——它读的是自然地图）
        var viewBtn = MakeButton("🗺  读取自然地图");
        viewBtn.SetAnchorsPreset(LayoutPreset.Center);
        viewBtn.Position = new Vector2(-200, -120);
        viewBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MapSelectMenu.tscn");
        AddChild(viewBtn);

        // 文明演化按钮（选自然地图 → 演化 → 生成 .cmp 游玩存档）
        var evolveBtn = MakeButton("🌱  文明演化");
        evolveBtn.SetAnchorsPreset(LayoutPreset.Center);
        evolveBtn.Position = new Vector2(-200, 0);
        evolveBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/CivEvolveMenu.tscn");
        AddChild(evolveBtn);

        // 读取文明存档按钮（读 .cmp 游玩地图 → 开始游戏：MapViewer 显示文明图层）
        var civBtn = MakeButton("🎮  读取文明存档");
        civBtn.SetAnchorsPreset(LayoutPreset.Center);
        civBtn.Position = new Vector2(-200, 120);
        civBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/CmpSelectMenu.tscn");
        AddChild(civBtn);

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
