using Godot;
using World.Services;

namespace World.UI;

/// <summary>
/// 主菜单：创建世界 / 查看地图 / 正式游玩 三个入口。
/// 静态骨架（背景/窗口框/logo/标题/3 个卡片按钮/版本信息）在 MainMenu.tscn 场景中定义
/// （与存档/生成/演化界面同一羊皮纸卡片风格 + 动态分辨率锚点）；脚本只做入口跳转绑定。
/// 2026-08-23：由纯代码构建改为场景版（原 _Ready 全量 new 控件）。
/// 2026-08-25 第二阶段：双入口修正——查看地图（浏览）/ 正式游玩（选图 → 游玩，EU4 式）。
/// </summary>
public partial class MainMenu : Control
{
    public override void _Ready()
    {
        // 根 Control 强制全屏（防场景根未自动拉伸）
        SetAnchorsPreset(LayoutPreset.FullRect);

        // ⚠️ 2026-08-25 路径改制：一次性迁移旧 C 盘数据到游戏目录旁（不落 C 盘——用户拍板）
        UserPaths.MigrateLegacyData();

        // 创建世界（生成 + 文明演化 一条龙：生成页内勾选演化开关，一步产出含文明的 .mpa）
        GetNode<Button>("%GenBtn").Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/MapGenMenu.tscn");

        // 查看地图（浏览存档：单列表 .mpa，🌍 含文明 / 🗺 纯自然 标记 → MapViewer）
        GetNode<Button>("%ViewBtn").Pressed += () => GetTree().ChangeSceneToFile("res://scenes/core/SaveSelectMenu.tscn");

        // 正式游玩（2026-08-25 第二阶段：选 .cmp 游戏档 → 进游玩——选国家/操纵为后续刀）
        GetNode<Button>("%PlayBtn").Pressed += () =>
        {
            EventBus.RequestGameplaySelect();   // SaveSelectMenu 消费 → 游玩模式（列 .cmp）
            GetTree().ChangeSceneToFile("res://scenes/core/SaveSelectMenu.tscn");
        };

        // 加载存档（2026-08-25 地图≠存档分层：.sav 游玩进程——恢复到保存时 tick 继续玩）
        GetNode<Button>("%LoadBtn").Pressed += () =>
        {
            EventBus.RequestLoadSelect();   // SaveSelectMenu 消费 → 存档模式（列 .sav）
            GetTree().ChangeSceneToFile("res://scenes/core/SaveSelectMenu.tscn");
        };

        LogService.Log("MainMenu", "ready");
    }
}
