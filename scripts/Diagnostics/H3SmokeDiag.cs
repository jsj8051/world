using System;
using Godot;
using World.Utils.H3;

namespace World.Diagnostics;

/// <summary>
/// H3 原生库在 Godot 宿主进程下的加载/调用冒烟诊断（headless 可跑）。
/// 单元测试已覆盖纯托管侧的互操正确性；本诊断专验【Godot mono 进程里 h3.dll 能被
/// H3Native 解析器找到并正确调用】——这是 DLL 部署路径问题的唯一暴露面。
/// 通过：GD.Print 断言 + 退出码 0；失败：退出码 1。
/// </summary>
public partial class H3SmokeDiag : Node
{
    private const double DegToRad = Math.PI / 180.0;

    public override void _Ready()
    {
        int fail = 0;

        try
        {
            // 1) H3 官方示例向量（与 H3ApiTests 同一断言）：旧金山某点，res 9
            ulong cell = H3.LatLngToCell(
                new LatLng(37.775938728915946 * DegToRad, -122.41795063018799 * DegToRad), 9);
            bool ok = cell == 0x8928308280fffffUL;
            GD.Print($"H3SmokeDiag: 官方向量 res9 = {H3.H3ToString(cell)}（期望 8928308280fffff，{ok}）");
            if (!ok) fail++;

            // 2) 全局统计：res 5 格子总数 = 2 + 120×7^5 = 2,016,842
            long count = H3.GetNumCells(5);
            GD.Print($"H3SmokeDiag: GetNumCells(5) = {count}（期望 2016842）");
            if (count != 2016842) fail++;

            // 3) k-ring：赤道 0° 点 res 5 的邻居环
            ulong origin = H3.LatLngToCell(new LatLng(0, 0), 5);
            ulong[] disk = H3.GridDisk(origin, 1);
            GD.Print($"H3SmokeDiag: GridDisk(origin,1) = {disk.Length} 格（期望 7）");
            if (disk.Length != 7) fail++;
        }
        catch (Exception e)
        {
            GD.Print($"H3SmokeDiag: 调用异常 {e.GetType().Name}: {e.Message}");
            fail++;
        }

        GD.Print(fail == 0 ? "H3SmokeDiag: 全部通过" : $"H3SmokeDiag: {fail} 处失败");
        GetTree().Quit(fail == 0 ? 0 : 1);
    }
}