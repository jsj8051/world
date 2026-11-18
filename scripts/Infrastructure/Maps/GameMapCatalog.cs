using System.Collections.Generic;
using System.IO;
using World.Application.Storage;
using World.Data.Maps;
using World.Utils;

namespace World.Infrastructure.Maps;

/// <summary>地图存档目录的磁盘实现；对外只提供相对游戏根目录的路径。</summary>
internal sealed class GameMapCatalog : IGeographyMapCatalog
{
	readonly string _gameRootDirectory;
	readonly string _relativeDirectory;

	internal GameMapCatalog(string gameRootDirectory, string relativeDirectory)
	{
		_gameRootDirectory = RootRelativePath.NormalizeRoot(gameRootDirectory);
		_relativeDirectory = RootRelativePath.Normalize(relativeDirectory);
	}

	internal static GameMapCatalog CreateDefault() => new(GamePaths.GameDirectory, GamePaths.MapDirectory);

	public string Directory => _relativeDirectory;

	public IReadOnlyList<MapPackageCandidate> List()
	{
		string directory = RootRelativePath.Resolve(_gameRootDirectory, _relativeDirectory);
		System.IO.Directory.CreateDirectory(directory);
		var candidates = new List<MapPackageCandidate>();
		foreach (string path in System.IO.Directory.EnumerateFiles(directory, "*.wmap"))
		{
			string relativePath = RootRelativePath.Normalize(_relativeDirectory + "/" + Path.GetFileName(path));
			string fullPath = RootRelativePath.Resolve(_gameRootDirectory, relativePath);
			candidates.Add(new MapPackageCandidate(relativePath, MapPackageProbe.Probe(fullPath)));
		}
		return candidates;
	}
}
