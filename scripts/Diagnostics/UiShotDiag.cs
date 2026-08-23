using Godot;

namespace World.Diagnostics;

/// <summary>存档界面截图（2026-08-23 UI 视觉验证）：窗口模式实例化 SaveSelectMenu，渲染数帧后截图存 PNG。
/// 用法（窗口模式，非 headless——headless 无渲染管线截图全黑）：
///   Godot --path E:/godotGames/world res://scenes/diag/UiShotDiag.tscn -- --tab=map|cmp --out=user://maps/ui.png
/// 参数：--tab=map（默认）|cmp 选择标签页；--out 输出路径；--dialog=1 模拟点删除钮截确认弹窗。
/// 2026-08-23 晚：合并版界面（SaveSelectMenu），支持 --tab 与多分辨率窗口。</summary>
public partial class UiShotDiag : Node
{
    private int _frame;
    private string _outPath = "user://maps/ui_shot.png";
    private bool _showDeleteDialog;   // --dialog=1：截删除确认弹窗（遮罩+对话框）
    private bool _triggerStart;       // --start=1：模拟点「开始生成」（截生成中状态）
    private int _shotFrame = 8;       // 截图帧号（--shot-frame=N；生成完成态需等生成结束）
    private bool argsTryDumpUi;      // --dump-ui=1：截图前打印 HUD 节点 rect

    public override void _Ready()
    {
        var args = DiagSceneBase.ParseUserArgs();
        // 目标场景：save（存档，默认）| mapgen | civ | main | viewer
        string scene = args.TryGetValue("scene", out var s) ? s : "save";
        if (args.TryGetValue("out", out var o)) _outPath = o;
        _showDeleteDialog = args.TryGetValue("dialog", out var d) && d == "1";
        _triggerStart = args.TryGetValue("start", out var st) && st == "1";
        if (args.TryGetValue("shot-frame", out var sf) && int.TryParse(sf, out int sfn)) _shotFrame = sfn;
        argsTryDumpUi = args.TryGetValue("dump-ui", out var du) && du == "1";

        // 窗口尺寸：--w=1280 --h=720（不传则用命令行 --resolution 或项目默认）
        if (args.TryGetValue("w", out var w) && int.TryParse(w, out int ww))
            GetWindow().Size = new Vector2I(ww, GetWindow().Size.Y);
        if (args.TryGetValue("h", out var h) && int.TryParse(h, out int hh))
            GetWindow().Size = new Vector2I(GetWindow().Size.X, hh);

        string scenePath = scene switch
        {
            "mapgen" => "res://scenes/core/MapGenMenu.tscn",
            "civ" => "res://scenes/core/CivEvolveMenu.tscn",
            "main" => "res://scenes/core/MainMenu.tscn",
            "viewer" => "res://scenes/core/MapViewer.tscn",
            _ => "res://scenes/core/SaveSelectMenu.tscn",
        };
        // MapViewer 是 Node3D 根（挂相机/灯光），其余是 Control 菜单
        var packed = GD.Load<PackedScene>(scenePath);
        var root = packed.Instantiate();
        AddChild(root);
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 5 && _triggerStart)
        {
            // 模拟点「开始生成」：设半径 12km（n≈6）+ 大陆块数 2（n/2=3 ≥ 2 校验通过，秒级完成），再调 StartGenerate
            var menu = GetChild(0);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            if (menu.GetType().GetField("_radiusSpin", flags)?.GetValue(menu) is Godot.Range radiusSpin)
            {
                radiusSpin.Value = 12f;
                GD.Print("UiShotDiag: 半径已设为 12km（n≈6，秒级完成）");
            }
            if (menu.GetType().GetField("_continentsSpin", flags)?.GetValue(menu) is Godot.Range continentsSpin)
            {
                continentsSpin.Value = 2f;   // 防「大陆块数>n/2」校验拦截
            }
            var m = menu.GetType().GetMethod("StartGenerate", flags);
            if (m != null)
            {
                m.Invoke(menu, null);
                GD.Print("UiShotDiag: 已触发 StartGenerate");
            }
        }
        if (_frame == 4 && _showDeleteDialog)
        {
            // 触发第一个存档行的删除确认（模拟点 🗑：找 _list 第一行的最后一个按钮）
            var list = FindChild("_list", recursive: true, owned: false) as VBoxContainer;
            if (list != null && list.GetChildCount() > 0)
            {
                foreach (var child in list.GetChildren())
                {
                    if (child is Button row && row.GetChildCount() > 0 && row.GetChild(0) is HBoxContainer h && h.GetChildCount() > 1
                        && h.GetChild(h.GetChildCount() - 1) is Button del)
                    {
                        del.EmitSignal(BaseButton.SignalName.Pressed);
                        break;
                    }
                }
            }
        }
        if (_frame == _shotFrame)   // 渲染稳定后截图
        {
            // --dump-ui=1：截图前打印 viewer HUD 各节点运行时 rect（排查错位）
            if (argsTryDumpUi)
            {
                var layer = FindChild("UiLayer", recursive: true, owned: false);
                if (layer != null)
                    foreach (Node c in layer.GetChildren())
                        if (c is Control cc)
                            GD.Print($"[UiShotDiag] HUD {c.Name}: pos={cc.Position} size={cc.Size} visible={cc.Visible}");
            }
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(_outPath);
            GD.Print($"UiShotDiag: 已截图 → {_outPath} ({img.GetWidth()}x{img.GetHeight()})");
            GetTree().Quit(0);
        }
    }
}