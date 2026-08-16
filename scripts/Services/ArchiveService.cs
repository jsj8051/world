using World.CivSim;
using World.LogicGrid;
using World.MapGen;

namespace World.Services;

/// <summary>
/// 存档统一入口（L2 服务层，ADR-0002）。
/// 路径命名与读写封装；版本/损坏校验语义保留在各 Archive 类内部（不动）。
/// </summary>
public static class ArchiveService
{
    /// <summary>地图存档命名约定（原散落在 MapGenMenu，统一入口）。</summary>
    public static string MapPath(int seed, int n, float radiusKm) =>
        $"user://maps/map_seed{seed}_n{n}_r{radiusKm:F0}.mpa";

    /// <summary>读地图存档（.mpa）。</summary>
    public static bool TryLoadMap(string path, out MapData map) => MapArchive.Read(path, out map);

    /// <summary>读文明存档（.cmp）。</summary>
    public static bool TryLoadCiv(string path, out GameGrid grid, out CivSimResult result) =>
        CivMapArchive.Read(path, out grid, out result);

    /// <summary>写文明存档（.cmp）。</summary>
    public static bool SaveCiv(string path, GameGrid grid, CivSimResult result, bool log = true) =>
        CivMapArchive.Write(path, grid, result, log);
}
