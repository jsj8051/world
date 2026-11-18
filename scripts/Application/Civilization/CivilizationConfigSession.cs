using System;
using System.Collections.Generic;
using System.Linq;
using World.Application.Configuration;
using World.Application.Storage;
using World.Data.Civilization;

namespace World.Application.Civilization;

/// <summary>文明配置草稿与命名档案会话；保存、应用、载入互相独立。</summary>
internal sealed class CivilizationConfigSession
{
	readonly FileSaveComponent<CivilizationProfileCatalog> _saver;
	readonly string _relativePath;
	readonly ConfigurationDraft<CivilizationConfig> _configuration =
		new(CivilizationConfigStrategy.Instance);
	CivilizationProfileCatalog _catalog = new();

	internal CivilizationConfig Draft => _configuration.Draft;
	internal CivilizationConfig Applied => _configuration.Applied;
	internal bool HasUnappliedChanges => _configuration.HasUnappliedChanges;
	internal Guid? SelectedProfileId { get; private set; }
	internal CivilizationProfile[] Profiles => _catalog.Profiles.Select(Copy).ToArray();
	internal string LoadProblem { get; }

	internal CivilizationConfigSession(IFileStorage<CivilizationProfileCatalog> store, string relativePath, string gameRootDirectory)
	{
		var loader = new FileLoadComponent<CivilizationProfileCatalog>(store, gameRootDirectory);
		_saver = new FileSaveComponent<CivilizationProfileCatalog>(store, gameRootDirectory);
		if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("配置相对路径不能为空。", nameof(relativePath));
		_relativePath = relativePath;
		try
		{
			CivilizationProfileCatalog loaded = loader.Load(_relativePath)
				?? throw new InvalidOperationException("文明配置目录为空。");
			loaded.Validate();
			_catalog = Copy(loaded);
		}
		catch (Exception error)
		{
			LoadProblem = $"文明配置目录加载失败，已禁止覆盖原文件：{error.Message}";
		}
		_configuration.Apply();
	}

	internal void Edit(CivilizationConfig config) => _configuration.Edit(config);
	internal void Apply() => _configuration.Apply();

	internal void Select(Guid id)
	{
		CivilizationProfile profile = Find(id);
		_configuration.SetDraft(profile.Config);
		SelectedProfileId = id;
	}

	internal void Save(string name, bool saveAsNew = false)
	{
		EnsureWritable();
		CivilizationProfileCatalog next = Copy(_catalog);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		CivilizationProfile profile;
		if (!saveAsNew && SelectedProfileId.HasValue)
		{
			profile = next.Profiles.Single(item => item.Id == SelectedProfileId.Value);
			profile.UpdatedUtc = now < profile.CreatedUtc ? profile.CreatedUtc : now;
		}
		else
		{
			profile = new CivilizationProfile { CreatedUtc = now, UpdatedUtc = now };
			next.Profiles.Add(profile);
		}
		profile.Name = name?.Trim();
		profile.Config = _configuration.Draft;
		Commit(next);
		SelectedProfileId = profile.Id;
	}

	internal void Rename(Guid id, string name)
	{
		EnsureWritable();
		Find(id);
		CivilizationProfileCatalog next = Copy(_catalog);
		CivilizationProfile profile = next.Profiles.Single(item => item.Id == id);
		profile.Name = name?.Trim();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		profile.UpdatedUtc = now < profile.CreatedUtc ? profile.CreatedUtc : now;
		Commit(next);
	}

	internal void Delete(Guid id)
	{
		EnsureWritable();
		Find(id);
		CivilizationProfileCatalog next = Copy(_catalog);
		next.Profiles.RemoveAll(profile => profile.Id == id);
		Commit(next);
		if (SelectedProfileId == id) SelectedProfileId = null;
	}

	internal void ResetDraft()
	{
		_configuration.ResetDraft();
		SelectedProfileId = null;
	}

	CivilizationProfile Find(Guid id) => _catalog.Profiles.FirstOrDefault(profile => profile.Id == id)
		?? throw new ArgumentException("文明配置档案不存在。", nameof(id));

	void EnsureWritable()
	{
		if (LoadProblem != null) throw new InvalidOperationException(LoadProblem);
	}

	void Commit(CivilizationProfileCatalog next)
	{
		next.Validate();
		_saver.Save(Copy(next), _relativePath);
		_catalog = next;
	}

	static CivilizationProfile Copy(CivilizationProfile profile) => new()
	{
		Id = profile.Id,
		Name = profile.Name,
		CreatedUtc = profile.CreatedUtc,
		UpdatedUtc = profile.UpdatedUtc,
		Config = CivilizationConfigStrategy.Instance.Copy(profile.Config),
	};

	static CivilizationProfileCatalog Copy(CivilizationProfileCatalog catalog) => new()
	{
		SchemaVersion = catalog.SchemaVersion,
		Profiles = catalog.Profiles.Select(Copy).ToList(),
	};
}
