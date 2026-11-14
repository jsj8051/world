using System.Collections.Generic;
using System.Threading;
using World.Data.Maps;

namespace World.Application.Storage;

/// <summary>
/// 地图存档库：固定游戏目录下的保存、加载与列表。
/// 文件位置由实现决定，页面只拿路径与内容，不接触目录选择或底层 zip。
/// </summary>
internal interface IGeographyMapRepository
{
	/// <summary>存档目录（游戏目录下，不依赖 user:// 的 AppData 位置）。</summary>
	string Directory { get; }

	/// <summary>保存一张地图并返回落盘路径；文件名由实现生成。</summary>
	string Save(GeographySnapshot snapshot);

	GeographySnapshot Load(string path, IFileImportObserver observer = null,
		CancellationToken cancellation = default);

	IReadOnlyList<MapPackageCandidate> List();
}
