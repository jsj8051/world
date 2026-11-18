using System.Collections.Generic;
using World.Data.Maps;

namespace World.Application.Storage;

/// <summary>地图存档目录的列表与探测能力。</summary>
internal interface IGeographyMapCatalog
{
	/// <summary>相对游戏根目录的存档目录。</summary>
	string Directory { get; }
	IReadOnlyList<MapPackageCandidate> List();
}
