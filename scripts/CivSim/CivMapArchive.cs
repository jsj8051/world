using Godot;
using World.LogicGrid;

namespace World.CivSim;

/// <summary>
/// 游玩地图存档 .cmp（文明演化输出；自包含——自然层快照 + 文明层，独立于 .mpa/.gmp）。
/// 源自然地图（.mpa/.gmp）只读，演化不修改任何自然字段。
///
/// v1（2026-08-05）：
///   [4B]  magic "CMP1"
///   [2B]  version = 1
///   [4B]  civSeed（演化种子）| [2B] epoch | [4B] finalTick | [4B] tickYears | [4B] originCount
///   然后 = GameMapArchive.WriteBody（自然层 + province/country，与 .gmp 布局完全一致）
///   文明层：
///     [4B×N] cellPopulation（float）
///     [1B×N] cellCulture | [1B×N] cellTech | [4B×N] cellTribeId（-1 → 存 0）
///     [4B] tribeCount
///     每个部落：[4B] id | [4B] originCell | [1B] culture | [1B] tech | [4B] mainCell | [4B] population
/// </summary>
public static class CivMapArchive
{
    public const string Magic = "CMP1";
    public const ushort Version = 1;

    public static bool Write(string path, GameGrid grid, CivSimResult result, bool log = true)
    {
        string dir = path.GetBaseDir();
        if (dir.Length > 0 && !DirAccess.DirExistsAbsolute(dir))
            DirAccess.MakeDirRecursiveAbsolute(dir);
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[CivMapArchive] cannot open {path} for write: {FileAccess.GetOpenError()}");
            return false;
        }
        var ctx = result.Context;
        int n = grid.N;
        f.Store8((byte)'C'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(Version);
        f.Store32((uint)ctx.Seed);
        f.Store16((ushort)ctx.Epoch.Kind);
        f.Store32((uint)result.FinalTick);
        f.Store32((uint)ctx.Epoch.TickYears);
        f.Store32((uint)ctx.OriginCount);
        GameMapArchive.WriteBody(f, grid);      // 自然层（只读源，原样快照）
        for (int i = 0; i < n; i++)
        {
            f.StoreFloat(ctx.Cells[i].Population);
            f.Store8(ctx.Cells[i].Culture);
            f.Store8(ctx.Cells[i].Tech);
            f.Store32((uint)(ctx.Cells[i].TribeId + 1));   // -1 → 0
        }
        f.Store32((uint)ctx.Tribes.Count);
        foreach (var t in ctx.Tribes)
        {
            f.Store32((uint)t.Id);
            f.Store32((uint)t.OriginCell);
            f.Store8(t.Culture);
            f.Store8(t.Tech);
            f.Store32((uint)t.MainCell);
            f.StoreFloat(t.Population);
        }
        if (log)
            GD.Print($"[CivMapArchive] wrote v{Version} {path} (epoch={ctx.Epoch.Name} ticks={result.FinalTick} " +
                     $"pop={ctx.TotalPopulation:F0} tribes={ctx.Tribes.Count})");
        return true;
    }

    /// <summary>读 .cmp → （自然层 GameGrid + 文明结果）。自然层与源 .gmp 一致（只读保证）。</summary>
    public static bool Read(string path, out GameGrid grid, out CivSimResult result)
    {
        grid = null;
        result = null;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"[CivMapArchive] cannot open {path} for read: {FileAccess.GetOpenError()}");
            return false;
        }
        if (f.Get8() != 'C' || f.Get8() != 'M' || f.Get8() != 'P' || f.Get8() != '1')
        {
            GD.PrintErr($"[CivMapArchive] bad magic in {path}");
            return false;
        }
        ushort ver = f.Get16();
        if (ver != Version)
        {
            GD.PrintErr($"[CivMapArchive] unsupported version {ver} in {path} (need {Version})");
            return false;
        }
        int civSeed = (int)f.Get32();
        var epochKind = (EpochKind)f.Get16();
        int finalTick = (int)f.Get32();
        int tickYears = (int)f.Get32();
        int originCount = (int)f.Get32();
        var g = new GameGrid();
        GameMapArchive.ReadBody(f, g);
        int n = g.N;
        var cells = new CellCiv[n];
        for (int i = 0; i < n; i++)
        {
            cells[i].Population = f.GetFloat();
            cells[i].Culture = f.Get8();
            cells[i].Tech = f.Get8();
            cells[i].TribeId = (int)f.Get32() - 1;
        }
        int tribeCount = (int)f.Get32();
        var tribes = new System.Collections.Generic.List<Tribe>(tribeCount);
        for (int k = 0; k < tribeCount; k++)
        {
            tribes.Add(new Tribe
            {
                Id = (int)f.Get32(),
                OriginCell = (int)f.Get32(),
                Culture = f.Get8(),
                Tech = f.Get8(),
                MainCell = (int)f.Get32(),
                Population = f.GetFloat(),
            });
        }
        var ctx = new CivSimContext
        {
            Grid = g,
            Cells = cells,
            Tribes = tribes,
            Seed = civSeed,
            OriginCount = originCount,
            Epoch = new EpochDefinition(epochKind, epochKind.ToString(), finalTick, tickYears),
            CellK = new float[n],
            BaseK = new float[n],
        };
        result = new CivSimResult { Context = ctx, FinalTick = finalTick };
        grid = g;
        return true;
    }
}
