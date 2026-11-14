using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using World.Application.Storage;
using World.Data.Generation;

namespace World.Infrastructure.Generation;

/// <summary>严格版本化配置 JSON；不依赖引擎与文件系统。</summary>
internal sealed class GenerationProfileJson
{
	static readonly JsonSerializerOptions Options = CreateOptions();
	static JsonSerializerOptions CreateOptions()
	{
		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
		};
		options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
		return options;
	}

	public GenerationProfileCatalog ReadCatalog(string json)
	{
		var catalog = JsonSerializer.Deserialize<GenerationProfileCatalog>(json, Options)
			?? throw new JsonException("配置目录不能是 null。");
		catalog.Validate();
		return catalog;
	}

	public string WriteCatalog(GenerationProfileCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		catalog.Validate();
		return JsonSerializer.Serialize(catalog, Options);
	}
}

/// <summary>命名配置目录的 JSON 文件实现；不解析 Godot 路径。</summary>
public sealed class FileGenerationProfileStore : IFileStorage<GenerationProfileCatalog>
{
	readonly GenerationProfileJson _json = new();

	public GenerationProfileCatalog Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default)
	{
		string target = Resolve(path);
		return File.Exists(target)
			? _json.ReadCatalog(File.ReadAllText(target, Encoding.UTF8)) : new GenerationProfileCatalog();
	}

	public void Export(string path, GenerationProfileCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		AtomicTextFile.Write(Resolve(path), _json.WriteCatalog(catalog));
	}

	static string Resolve(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置路径不能为空。", nameof(path));
		return Path.GetFullPath(path);
	}
}

/// <summary>同目录临时文件先写入并刷盘，再原子发布；失败时不截断目标文件。</summary>
public static class AtomicTextFile
{
	public static void Write(string path, string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		string target = Path.GetFullPath(path);
		string directory = Path.GetDirectoryName(target);
		Directory.CreateDirectory(directory);
		string temporary = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				byte[] bytes = new UTF8Encoding(false).GetBytes(text);
				stream.Write(bytes);
				stream.Flush(flushToDisk: true);
			}
			if (File.Exists(target)) File.Replace(temporary, target, null);
			else File.Move(temporary, target);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}
}
