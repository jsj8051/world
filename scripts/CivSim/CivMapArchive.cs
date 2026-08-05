using Godot;
using World.LogicGrid;

namespace World.CivSim;

/// <summary>
/// 游玩地图存档 .cmp（文明演化输出；自包含——自然层快照 + 文明层，独立于 .mpa/.gmp）。
/// 源自然地图只读，演化不修改任何自然字段。
///
/// v3（2026-08-05，文化分层 + 宗教）：
///   [4B]  magic "CMP1"
///   [2B]  version = 3
///   [4B]  civSeed | [2B] epoch | [4B] finalTick | [4B] tickYears | [4B] originCount
///   GameMapArchive.WriteBody（自然层，与 .gmp 布局一致）
///   部落表（部落=格内社会单元，一格多部落）：
///     [4B] tribeCount
///     每个：[4B] id | [4B] cell | [4B] population(float) | [1B] culture | [1B] cultureGroup
///           [1B] religion | [8B] techFlags(ulong) | [4B] originCell | [4B] bornTick
///   统计：[4B] fissions | [4B] absorptions | [4B] merges | [4B] migrations | [8B] tradeContacts
/// </summary>
public static class CivMapArchive
{
    public const string Magic = "CMP1";
    public const ushort Version = 3;

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
        f.Store8((byte)'C'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(Version);
        f.Store32((uint)ctx.Seed);
        f.Store16((ushort)ctx.Epoch.Kind);
        f.Store32((uint)result.FinalTick);
        f.Store32((uint)ctx.Epoch.TickYears);
        f.Store32((uint)ctx.OriginCount);
        GameMapArchive.WriteBody(f, grid);      // 自然层（只读源，原样快照）
        int alive = 0;
        for (int k = 0; k < ctx.Tribes.Count; k++) if (!ctx.Tribes[k].Dead) alive++;
        f.Store32((uint)alive);
        foreach (var t in ctx.Tribes)
        {
            if (t.Dead) continue;
            f.Store32((uint)t.Id);
            f.Store32((uint)t.Cell);
            f.StoreFloat(t.Population);
            f.Store8(t.Culture);
            f.Store8(t.CultureGroup);
            f.Store8(t.Religion);
            f.Store64(t.TechFlags);
            f.Store32((uint)t.OriginCell);
            f.Store32((uint)t.BornTick);
        }
        f.Store32((uint)ctx.Fissions);
        f.Store32((uint)ctx.Absorptions);
        f.Store32((uint)ctx.Merges);
        f.Store32((uint)ctx.Migrations);
        f.Store64((ulong)ctx.TradeContacts);
        f.Store32((uint)ctx.CultureGroupCount);
        if (log)
            GD.Print($"[CivMapArchive] wrote v{Version} {path} (epoch={ctx.Epoch.Name} ticks={result.FinalTick} " +
                     $"tribes={ctx.Tribes.Count} pop={ctx.TotalPopulation():F0} fission={ctx.Fissions} absorb={ctx.Absorptions})");
        return true;
    }

    /// <summary>读 .cmp → （自然层 GameGrid + 文明结果）。自然层与源一致（只读保证）。</summary>
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
            GD.PrintErr($"[CivMapArchive] unsupported version {ver} in {path} (need {Version}，v1 旧档请重新演化)");
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

        int tribeCount = (int)f.Get32();
        var tribes = new System.Collections.Generic.List<Tribe>(tribeCount);
        var cellTribes = new System.Collections.Generic.List<Tribe>[n];
        for (int i = 0; i < n; i++) cellTribes[i] = new System.Collections.Generic.List<Tribe>();
        for (int k = 0; k < tribeCount; k++)
        {
            var t = new Tribe
            {
                Id = (int)f.Get32(),
                Cell = (int)f.Get32(),
                Population = f.GetFloat(),
                Culture = f.Get8(),
                CultureGroup = f.Get8(),
                Religion = f.Get8(),
                TechFlags = f.Get64(),
                OriginCell = (int)f.Get32(),
                BornTick = (int)f.Get32(),
            };
            tribes.Add(t);
            cellTribes[t.Cell].Add(t);
        }
        int fissions = (int)f.Get32();
        int absorptions = (int)f.Get32();
        int merges = (int)f.Get32();
        int migrations = (int)f.Get32();
        long tradeContacts = (long)f.Get64();
        int cultureGroupCount = (int)f.Get32();

        var ctx = new CivSimContext
        {
            Grid = g,
            CellTribes = cellTribes,
            Tribes = tribes,
            Seed = civSeed,
            OriginCount = originCount,
            Epoch = new EpochDefinition(epochKind, epochKind.ToString(), finalTick, tickYears),
            BaseK = new float[n],
            CellK = new float[n],
            CellPop = new float[n],
            Fissions = fissions,
            Absorptions = absorptions,
            Merges = merges,
            Migrations = migrations,
            TradeContacts = tradeContacts,
            CultureGroupCount = cultureGroupCount,
        };
        // 重建承载场 + 每格状态
        float cellArea = g.CellAreaKm2;
        for (int i = 0; i < n; i++)
        {
            float dens = g.IsLandCell(i) ? CivSimContext.CarrierDensityPerKm2(g.Biome[i]) : 0f;
            if (dens > 0f && (g.RiverLevel[i] > 0 || g.LakeLevel[i] > 0)) dens *= 1.5f;
            ctx.BaseK[i] = dens * cellArea;
        }
        CivEngine.RefreshCellState(ctx);

        result = new CivSimResult { Context = ctx, FinalTick = finalTick };
        grid = g;
        return true;
    }
}
