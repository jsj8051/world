using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using World.Data.Generation;

namespace World.Params.Generation;

/// <summary>严格版本化配置 JSON；不依赖引擎与文件系统。</summary>
public static class GenerationProfileJson
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

	public static GenerationProfileCatalog ReadCatalog(string json)
	{
		var catalog = JsonSerializer.Deserialize<GenerationProfileCatalog>(json, Options)
			?? throw new JsonException("配置目录不能是 null。");
		catalog.Validate();
		return catalog;
	}

	public static string WriteCatalog(GenerationProfileCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		catalog.Validate();
		return JsonSerializer.Serialize(catalog, Options);
	}

	public static GenerationProfileFile ReadFile(string json)
	{
		var file = JsonSerializer.Deserialize<GenerationProfileFile>(json, Options)
			?? throw new JsonException("配置文件不能是 null。");
		file.Validate();
		return file;
	}

	public static string WriteFile(GenerationProfileFile file)
	{
		ArgumentNullException.ThrowIfNull(file);
		file.Validate();
		return JsonSerializer.Serialize(file, Options);
	}
}

/// <summary>从外部注入路径读写命名配置目录；不解析 Godot 路径。</summary>
public sealed class FileGenerationProfileStore : IGenerationProfileStore
{
	readonly string _path;
	public FileGenerationProfileStore(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置路径不能为空。", nameof(path));
		_path = Path.GetFullPath(path);
	}

	public GenerationProfileCatalog Load() => File.Exists(_path)
		? GenerationProfileJson.ReadCatalog(File.ReadAllText(_path, Encoding.UTF8)) : new GenerationProfileCatalog();

	public void Save(GenerationProfileCatalog catalog) => AtomicTextFile.Write(_path, GenerationProfileJson.WriteCatalog(catalog));
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
