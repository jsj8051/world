using System;
using World.Utils;

namespace World.Application.Storage;

/// <summary>把调用方提供的数据写入游戏根目录下的相对路径。</summary>
/// <typeparam name="TData">由注入的文件存储实现处理的数据类型。</typeparam>
public sealed class FileSaveComponent<TData>
{
	readonly IFileStorage<TData> _storage;
	readonly string _gameRootDirectory;

	public FileSaveComponent(IFileStorage<TData> storage, string gameRootDirectory)
	{
		_storage = storage ?? throw new ArgumentNullException(nameof(storage));
		_gameRootDirectory = RootRelativePath.NormalizeRoot(gameRootDirectory);
	}

	/// <summary>写入调用方数据并返回规范化的游戏根目录相对路径。</summary>
	/// <example><code>string path = saver.Save(data, "userdata/saves/example.dat");</code></example>
	public string Save(TData data, string relativePath)
	{
		string path = RootRelativePath.Resolve(_gameRootDirectory, relativePath);
		_storage.Export(path, data);
		return RootRelativePath.Normalize(relativePath);
	}
}
