using System;
using System.Threading;
using World.Utils;

namespace World.Application.Storage;

/// <summary>从游戏根目录下的相对路径读取调用方指定的数据类型。</summary>
/// <typeparam name="TData">由注入的文件存储实现读取的数据类型。</typeparam>
public sealed class FileLoadComponent<TData>
{
	readonly IFileStorage<TData> _storage;
	readonly string _gameRootDirectory;

	public FileLoadComponent(IFileStorage<TData> storage, string gameRootDirectory)
	{
		_storage = storage ?? throw new ArgumentNullException(nameof(storage));
		_gameRootDirectory = RootRelativePath.NormalizeRoot(gameRootDirectory);
	}

	public TData Load(string relativePath, IFileImportObserver observer = null,
		CancellationToken cancellation = default)
	{
		string path = RootRelativePath.Resolve(_gameRootDirectory, relativePath);
		return _storage.Import(path, observer, cancellation);
	}
}
