using World.Data.Maps;
using World.Utils;

namespace World.Application.Storage;

/// <summary>地图存档目录中的一项：相对游戏根目录的路径与文件头探测结果。</summary>
internal readonly record struct MapPackageCandidate
{
	public string FilePath { get; }
	public MapProbeResult Probe { get; }

	internal MapPackageCandidate(string filePath, MapProbeResult probe)
	{
		FilePath = RootRelativePath.Normalize(filePath);
		Probe = probe;
	}
}
