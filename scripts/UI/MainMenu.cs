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

        // 创建世界（生成 + 文明演化 一条龙：生成页内勾选演化开关，一步产出含文明的 .mpa）
        GetNode<Button>("%GenBtn").Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MapGenMenu.tscn");

        // 进入世界（读取存档：单列表 .mpa，🌍 含文明 / 🗺 纯自然 标记）
        GetNode<Button>("%ViewBtn").Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/SaveSelectMenu.tscn");

        LogService.Log("MainMenu", "ready");
    }
}