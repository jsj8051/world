using System;
using System.Collections.Generic;
using System.Linq;
using World.Data.Generation;

namespace World.Params.Generation;

/// <summary>命名配置目录的宿主存储边界；缺失目录返回空目录，读写失败抛异常。</summary>
public interface IGenerationProfileStore
{
	GenerationProfileCatalog Load();
	void Save(GenerationProfileCatalog catalog);
}

/// <summary>跨页面配置会话。所有公开数据均为副本，应用不持久化，保存不隐式应用。</summary>
public sealed class GenerationConfigSession
{
	readonly IGenerationProfileStore _store;
	GenerationProfileCatalog _catalog = new();
	GenerationConfig _draft = new();
	GenerationConfig _applied;
	public GenerationConfig Draft => Copy(_draft);
	public GenerationConfig Applied => _applied == null ? null : Copy(_applied);
	public bool HasUnappliedChanges => _applied == null || !Equal(_draft, _applied);
	public Guid? SelectedProfileId { get; private set; }
	public IReadOnlyList<GenerationProfile> Profiles => _catalog.Profiles.Select(Copy).ToArray();
	public string LoadProblem { get; }

	public GenerationConfigSession(IGenerationProfileStore store)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		try
		{
			var loaded = store.Load() ?? throw new InvalidOperationException("配置目录为空。");
			loaded.Validate();
			_catalog = Copy(loaded);
		}
		catch (Exception e) { LoadProblem = $"配置目录加载失败，已禁止覆盖原文件：{e.Message}"; }
	}

	public void Edit(GenerationConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);
		config.Validate();
		_draft = Copy(config);
	}

	public void Apply() { _draft.Validate(); _applied = Copy(_draft); }

	public void Select(Guid id)
	{
		var profile = Find(id);
		_draft = Copy(profile.Config);
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
		profile.Config = Copy(_draft);
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

	public void Import(string json)
	{
		var file = GenerationProfileJson.ReadFile(json);
		_draft = Copy(file.Profile.Config);
		SelectedProfileId = null;
	}

	public string Export(string name)
	{
		var now = DateTimeOffset.UtcNow;
		return GenerationProfileJson.WriteFile(new GenerationProfileFile
		{
			Profile = new GenerationProfile { Name = name?.Trim(), CreatedUtc = now, UpdatedUtc = now, Config = Copy(_draft) }
		});
	}

	public GenerationConfig GetAppliedForGeneration()
	{
		if (_applied == null) throw new InvalidOperationException("请先应用配置再生成地图。");
		if (HasUnappliedChanges) throw new InvalidOperationException("存在未应用的修改，请先点击应用。");
		return Copy(_applied);
	}

	public void ResetDraft() { _draft = new GenerationConfig(); SelectedProfileId = null; }

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
		_store.Save(Copy(next));
		_catalog = next;
	}

	static bool Equal(GenerationConfig a, GenerationConfig b) =>
		a.Seed == b.Seed && a.Layout == b.Layout && a.SeaLevel == b.SeaLevel && a.PlateCount == b.PlateCount &&
		a.Relief == b.Relief && a.Temperature == b.Temperature && a.Seasonality == b.Seasonality && a.Rainfall == b.Rainfall;

	internal static GenerationConfig Copy(GenerationConfig c) => new()
	{
		Seed = c.Seed, Layout = c.Layout, SeaLevel = c.SeaLevel, PlateCount = c.PlateCount,
		Relief = c.Relief, Temperature = c.Temperature, Seasonality = c.Seasonality, Rainfall = c.Rainfall
	};
	static GenerationProfile Copy(GenerationProfile p) => new()
	{
		Id = p.Id, Name = p.Name, CreatedUtc = p.CreatedUtc, UpdatedUtc = p.UpdatedUtc, Config = Copy(p.Config)
	};
	static GenerationProfileCatalog Copy(GenerationProfileCatalog c) => new()
	{
		SchemaVersion = c.SchemaVersion, Profiles = c.Profiles.Select(Copy).ToList()
	};
}
