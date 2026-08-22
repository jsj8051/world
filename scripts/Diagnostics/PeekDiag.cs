using Godot;
using System.Collections.Generic;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>Peek 轻量头部读取验证（2026-08-23）：对 user://maps 下所有 .mpa 存档，
/// 对比 Peek 与全量 Read 的 seed/顶点数/海拔范围一致性，并统计两种方式的总耗时。
/// headless 跑：-- --quit-after 600（全部档 Peek 毫秒级，无需长超时）。
/// 退出码：任一档不一致 → 1；全部一致 → 0。</summary>
public partial class PeekDiag : Node
{
    public override void _Ready()
    {
        var files = new List<string>();
        using var dir = DirAccess.Open("user://maps");
        if (dir == null)
        {
            GD.Print("PeekDiag: 无法打开 user://maps");
            GetTree().Quit(1);
            return;
        }
        dir.ListDirBegin();
        while (true)
        {
            string f = dir.GetNext();
            if (f == "") break;
            if (!dir.CurrentIsDir() && f.EndsWith(".mpa"))
                files.Add(f);
        }
        dir.ListDirEnd();
        files.Sort();

        if (files.Count == 0)
        {
            GD.Print("PeekDiag: 没有 .mpa 存档");
            GetTree().Quit(1);
            return;
        }

        int fail = 0;
        var swPeek = new System.Diagnostics.Stopwatch();
        var swRead = new System.Diagnostics.Stopwatch();
        foreach (var f in files)
        {
            string path = "user://maps/" + f;

            swPeek.Start();
            bool okPeek = MapArchive.Peek(path, out int seed, out int vc, out int h,
                                          out float minE, out float maxE, out ushort ver);
            swPeek.Stop();

            swRead.Start();
            bool okRead = MapArchive.Read(path, out var map);
            swRead.Stop();

            bool ok = okPeek && okRead;
            if (ok)
            {
                bool spherical = ver >= 3;
                int rVc = spherical ? map.Verts.Length : map.Width;
                int rH = spherical ? 0 : map.Height;
                ok = seed == map.Seed
                     && vc == rVc && h == rH
                     && minE == map.MinElev && maxE == map.MaxElev
                     && spherical == map.IsSpherical;
            }
            string status = ok ? "一致" : $"不一致(peek={okPeek} read={okRead})";
            if (!ok) fail++;
            GD.Print($"PeekDiag: {f} ver={ver} seed={seed} 顶点={vc}×{h} elev[{minE:F0},{maxE:F0}]  {status}");
        }
        GD.Print($"PeekDiag: {files.Count} 档 Peek 总耗时 {swPeek.ElapsedMilliseconds}ms | 全量 Read 总耗时 {swRead.ElapsedMilliseconds}ms");
        GetTree().Quit(fail == 0 ? 0 : 1);
    }
}
