using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using World.Application.Storage;
using World.Data.Maps;

namespace World.Infrastructure.Maps;

/// <summary>地图存档库的磁盘实现：固定目录、自动命名、列表探测与包读取。</summary>
internal sealed class GameMapRepository : IGeographyMapRepository
{
	readonly IFileStorage<GeographySnapshot> _storage;
	readonly string _directory;

	internal GameMapRepository(IFileStorage<GeographySnapshot> storage, string directory)
	{
		_storage = storage ?? throw new ArgumentNullException(nameof(storage));
		if (string.IsNullOrWhiteSpace(directory))
			throw new ArgumentException("地图存档目录不能为空。", nameof(directory));
		_directory = directory;
	}

	/// <summary>生产默认：游戏目录下 userdata/saves，地图包编解码仍由 MapPackageFileStorage 负责。</summary>
	internal static GameMapRepository CreateDefault()
		=> new(new MapPackageFileStorage(), GamePaths.MapDirectory);

	public string Directory => _directory;

	public string Save(GeographySnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		System.IO.Directory.CreateDirectory(_directory);
		string path = Path.Combine(_directory, $"map_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wmap");
		_storage.Export(path, snapshot);
		return path;
	}

	public GeographySnapshot Load(string path, IFileImportObserver observer = null,
		CancellationToken cancellation = default)
		=> _storage.Import(path, observer, cancellation);

	public IReadOnlyList<MapPackageCandidate> List()
	{
		System.IO.Directory.CreateDirectory(_directory);
		var candidates = new List<MapPackageCandidate>();
		foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.wmap"))
		{
			string fullPath = Path.GetFullPath(path);
			candidates.Add(new MapPackageCandidate(fullPath, MapPackageProbe.Probe(fullPath)));
		}
		return candidates;
	}
}
