using System.Threading;
using World.Application.Storage;
using World.Data.Maps;

namespace World.Infrastructure.Maps;

/// <summary>地图包文件的导入导出：读取时上报分块进度，写出时拒绝覆盖已有文件。</summary>
internal sealed class MapPackageFileStorage : IFileStorage<GeographySnapshot>
{
	public GeographySnapshot Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default)
	{
		GeographySnapshot snapshot = MapPackageReader.Read(path,
			(done, total) => observer?.Report(done, total), cancellation);
		return snapshot;
	}

	public void Export(string path, GeographySnapshot document)
	{
		if (document == null) throw new System.ArgumentNullException(nameof(document));
		MapPackageWriter.Write(document, path);
	}
}
