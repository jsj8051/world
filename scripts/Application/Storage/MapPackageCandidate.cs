using World.Data.Maps;

namespace World.Application.Storage;

/// <summary>地图存档库中的一项：绝对路径与只读文件头探测结果。</summary>
internal readonly record struct MapPackageCandidate(string FilePath, MapProbeResult Probe);
