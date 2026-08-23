using Godot;
using World.Services;

namespace World.UI;

/// <summary>
/// 主菜单：生成地图 / 读取自然地图 / 文明演化 / 读取文明存档 四个入口。
/// 静态骨架（背景/窗口框/logo/标题/4 个卡片按钮/版本信息）在 MainMenu.tscn 场景中定义
/// （与存档/生成/演化界面同一深空卡片风格 + 动态分辨率锚点）；脚本只做入口跳转绑定。
/// 2026-08-23：由纯代码构建改为场景版（原 _Ready 全量 new 控件）。
/// </summary>
public partial class MainMenu : Control
{
    public override void _Ready()
    {
        // 根 Control 强制全屏（防场景根未自动拉伸）
        SetAnchorsPreset(LayoutPreset.FullRect);

        GetNode<Button>("%GenBtn").Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MapGenMenu.tscn");

        // 读取自然地图按钮（读 .mpa/.gmp 用 MapViewer 查看自然图层；原"进入游戏"改名——它读的是自然地图）
        GetNode<Button>("%ViewBtn").Pressed += () =>
        {
            SaveSelectMenu.InitialTab = "map";   // 合并界面：默认地图标签
            GetTree().ChangeSceneToFile("res://scenes/core/SaveSelectMenu.tscn");
        };

        // 文明演化按钮（选自然地图 → 演化 → 生成 .cmp 游玩存档）
        GetNode<Button>("%EvolveBtn").Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/CivEvolveMenu.tscn");

        // 读取文明存档按钮（读 .cmp 游玩地图 → 开始游戏：MapViewer 显示文明图层）
        GetNode<Button>("%CivBtn").Pressed += () =>
        {
            SaveSelectMenu.InitialTab = "cmp";   // 合并界面：文明存档标签
            GetTree().ChangeSceneToFile("res://scenes/core/SaveSelectMenu.tscn");
        };

        LogService.Log("MainMenu", "ready");
    }
}