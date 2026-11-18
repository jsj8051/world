using System;
using System.Collections.Generic;
using System.Linq;
using World.Application.Configuration;
using World.Application.Storage;
using World.Data.Generation;

namespace World.Application.Generation;

/// <summary>跨页面配置会话。所有公开数据均为副本，应用不持久化，保存不隐式应用。
/// 会话创建即把默认配置视为已应用，进入配置页可直接生成；一旦编辑，仍需显式应用。</summary>
public sealed class GenerationConfigSession
{
	readonly FileSaveComponent<GenerationProfileCatalog> _saver;
	readonly string _relativePath;
	readonly ConfigurationDraft<GenerationConfig> _configuration;
	GenerationProfileCatalog _catalog = new();
	public GenerationConfig Draft => _configuration.Draft;
	public GenerationConfig Applied => _configuration.Applied;
	public bool HasUnappliedChanges => _configuration.HasUnappliedChanges;
	public Guid? SelectedProfileId { get; private set; }
	public IReadOnlyList<GenerationProfile> Profiles => _catalog.Profiles.Select(Copy).ToArray();
	public string LoadProblem { get; }

	/// <summary>从游戏根目录下的相对路径读取档案；损坏档案将禁止后续覆盖。</summary>
	/// <example><code>new GenerationConfigSession(store, "userdata/configs/generation_profiles.json", gameRootDirectory);</code></example>
	public GenerationConfigSession(IFileStorage<GenerationProfileCatalog> store, string relativePath, string gameRootDirectory)
	{
		var loader = new FileLoadComponent<GenerationProfileCatalog>(store, gameRootDirectory);
		_saver = new FileSaveComponent<GenerationProfileCatalog>(store, gameRootDirectory);
		if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("配置相对路径不能为空。", nameof(relativePath));
		_relativePath = relativePath;
		_configuration = new ConfigurationDraft<GenerationConfig>(GenerationConfigStrategy.Instance);
		try
		{
			var loaded = loader.Load(_relativePath) ?? throw new InvalidOperationException("配置目录为空。");
			loaded.Validate();
			_catalog = Copy(loaded);
		}
		catch (Exception e) { LoadProblem = $"配置目录加载失败，已禁止覆盖原文件：{e.Message}"; }
		// 默认配置无需用户确认即可生成；坏目录不影响这条内存路径。
		_configuration.Apply();
	}

	public void Edit(GenerationConfig config)
	{
		_configuration.Edit(config);
	}

	public void Apply() => _configuration.Apply();

	public void Select(Guid id)
	{
		var profile = Find(id);
		_configuration.SetDraft(profile.Config);
		SelectedProfileId = id;
	}

	public void Save(string name, bool saveAsNew = false)
	{
		EnsureWritable();
		var next = Copy(_catalog);
		var now = DateTimeOffset.UtcNow;
		GenerationProfile profile;
		if (!saveAsNew && SelectedProfileId.HasValue)
		{
			profile = next.Profiles.Single(p => p.Id == SelectedProfileId.Value);
			profile.UpdatedUtc = now < profile.CreatedUtc ? profile.CreatedUtc : now;
		}
		else
		{
			profile = new GenerationProfile { CreatedUtc = now, UpdatedUtc = now };
			next.Profiles.Add(profile);
		}
		profile.Name = name?.Trim();
		profile.Config = _configuration.Draft;
		Commit(next);
		SelectedProfileId = profile.Id;
	}

	public void Rename(Guid id, string name)
	{
		EnsureWritable();
		Find(id);
		var next = Copy(_catalog);
		var profile = next.Profiles.Single(p => p.Id == id);
		profile.Name = name?.Trim();
		var now = DateTimeOffset.UtcNow;
		profile.UpdatedUtc = now < profile.CreatedUtc ? profile.CreatedUtc : now;
		Commit(next);
	}

	public void Delete(Guid id)
	{
		EnsureWritable();
		Find(id);
		var next = Copy(_catalog);
		next.Profiles.RemoveAll(p => p.Id == id);
		Commit(next);
		if (SelectedProfileId == id) SelectedProfileId = null;
	}

	public GenerationConfig GetAppliedForGeneration()
	{
		return _configuration.GetApplied();
	}

	public void ResetDraft()
	{
		_configuration.ResetDraft();
		SelectedProfileId = null;
	}

	GenerationProfile Find(Guid id) => _catalog.Profiles.FirstOrDefault(p => p.Id == id)
		?? throw new ArgumentException("配置档案不存在。", nameof(id));

	void EnsureWritable()
	{
		if (LoadProblem != null) throw new InvalidOperationException(LoadProblem);
	}

	void Commit(GenerationProfileCatalog next)
	{
		next.Validate();
		// 存储实现可能持有或修改传入对象，不能让它取得会话内部对象。
		_saver.Save(Copy(next), _relativePath);
		_catalog = next;
	}

	internal static GenerationConfig Copy(GenerationConfig config) => GenerationConfigStrategy.Instance.Copy(config);
	static GenerationProfile Copy(GenerationProfile p) => new()
	{
		Id = p.Id, Name = p.Name, CreatedUtc = p.CreatedUtc, UpdatedUtc = p.UpdatedUtc, Config = Copy(p.Config)
	};
	static GenerationProfileCatalog Copy(GenerationProfileCatalog c) => new()
	{
		SchemaVersion = c.SchemaVersion, Profiles = c.Profiles.Select(Copy).ToList()
	};
}
