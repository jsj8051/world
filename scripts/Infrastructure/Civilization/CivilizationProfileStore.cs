using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using World.Application.Storage;
using World.Data.Civilization;
using World.Infrastructure.Generation;

namespace World.Infrastructure.Civilization;

/// <summary>文明档案目录的严格 JSON 编解码。</summary>
internal sealed class CivilizationProfileJson
{
	static readonly JsonSerializerOptions Options = CreateOptions();

	static JsonSerializerOptions CreateOptions()
	{
		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		};
		options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
		return options;
	}

	internal CivilizationProfileCatalog ReadCatalog(string json)
	{
		CivilizationProfileCatalog catalog = JsonSerializer.Deserialize<CivilizationProfileCatalog>(json, Options)
			?? throw new JsonException("文明配置目录不能是 null。");
		catalog.Validate();
		return catalog;
	}

	internal string WriteCatalog(CivilizationProfileCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		catalog.Validate();
		return JsonSerializer.Serialize(catalog, Options);
	}
}

/// <summary>文明配置目录 JSON 文件存储；使用共享原子文本写入实现。</summary>
internal sealed class FileCivilizationProfileStore : IFileStorage<CivilizationProfileCatalog>
{
	readonly CivilizationProfileJson _json = new();

	public CivilizationProfileCatalog Import(string path, IFileImportObserver observer = null,
		CancellationToken cancellation = default)
	{
		string target = Resolve(path);
		return File.Exists(target)
			? _json.ReadCatalog(File.ReadAllText(target, Encoding.UTF8))
			: new CivilizationProfileCatalog();
	}

	public void Export(string path, CivilizationProfileCatalog catalog)
		=> AtomicTextFile.Write(Resolve(path), _json.WriteCatalog(catalog));

	static string Resolve(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
			throw new ArgumentException("文件存储需要已解析的绝对路径。", nameof(path));
		return Path.GetFullPath(path);
	}
}
