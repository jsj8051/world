using System.Threading;

namespace World.Application.Storage;

/// <summary>导入较大文件时的分块进度；不需要分块的实现可以忽略。</summary>
public interface IFileImportObserver
{
	void Report(int completed, int total);
}

/// <summary>
/// 文件管理边界：调用方只给路径与文档对象，格式、大小限制、压缩与原子写入都由实现方决定。
/// 让页面和用例不依赖具体介质，替换存储只换实现。
/// </summary>
public interface IFileStorage<TDocument>
{
	TDocument Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default);
	void Export(string path, TDocument document);
}
