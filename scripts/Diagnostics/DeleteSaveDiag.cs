using Godot;
using World.Services;

namespace World.Diagnostics;

/// <summary>ArchiveService.DeleteSave 验证（2026-08-23 存档删除功能）：
/// 写一个临时档 → 删除 → 断言文件不存在；再删一次（幂等，文件不存在不报错）。
/// headless 跑：--quit-after 300。退出码：失败 → 1，通过 → 0。</summary>
public partial class DeleteSaveDiag : Node
{
    public override void _Ready()
    {
        string path = "user://maps/tmp_deletesave_test.mpa";
        string abs = ProjectSettings.GlobalizePath(path);
        int fail = 0;

        // 1. 造一个临时文件
        try
        {
            System.IO.File.WriteAllText(abs, "test");
        }
        catch (System.Exception ex)
        {
            GD.Print($"DeleteSaveDiag: 写临时文件失败 {ex.Message}");
            GetTree().Quit(1);
            return;
        }
        if (!System.IO.File.Exists(abs)) { GD.Print("DeleteSaveDiag: 临时文件未创建"); fail++; }

        // 2. 删除 → 应不存在
        ArchiveService.DeleteSave(path);
        if (System.IO.File.Exists(abs)) { GD.Print("DeleteSaveDiag: 删除后文件仍存在——失败"); fail++; }
        else GD.Print("DeleteSaveDiag: 删除成功，文件不存在 ✓");

        // 3. 幂等：再删一次不抛异常
        try { ArchiveService.DeleteSave(path); GD.Print("DeleteSaveDiag: 二次删除幂等 ✓"); }
        catch (System.Exception ex) { GD.Print($"DeleteSaveDiag: 二次删除抛异常 {ex.Message}"); fail++; }

        // 4. 删不存在的 user:// 路径也不抛
        try { ArchiveService.DeleteSave("user://maps/tmp_never_existed_xyz.mpa"); GD.Print("DeleteSaveDiag: 删不存在路径幂等 ✓"); }
        catch (System.Exception ex) { GD.Print($"DeleteSaveDiag: 删不存在路径抛异常 {ex.Message}"); fail++; }

        GD.Print(fail == 0 ? "DeleteSaveDiag: 全部通过" : $"DeleteSaveDiag: {fail} 项失败");
        GetTree().Quit(fail == 0 ? 0 : 1);
    }
}