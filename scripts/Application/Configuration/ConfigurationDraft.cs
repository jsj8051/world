using System;

namespace World.Application.Configuration;

/// <summary>领域配置行为策略：提供配置默认值、复制、校验与相等判断。</summary>
internal interface IConfigurationStrategy<TConfig> where TConfig : class
{
	TConfig CreateDefault();
	TConfig Copy(TConfig config);
	void Validate(TConfig config);
	bool AreEqual(TConfig left, TConfig right);
}

/// <summary>策略驱动的配置草稿与已应用状态；持有值副本，不泄漏可变引用。</summary>
internal sealed class ConfigurationDraft<TConfig> where TConfig : class
{
	readonly IConfigurationStrategy<TConfig> _strategy;
	TConfig _draft;
	TConfig _applied;

	internal ConfigurationDraft(IConfigurationStrategy<TConfig> strategy)
	{
		_strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
		_draft = CopyValidated(_strategy.CreateDefault());
		_applied = _strategy.Copy(_draft);
	}

	internal TConfig Draft => _strategy.Copy(_draft);
	internal TConfig Applied => _strategy.Copy(_applied);
	internal bool HasUnappliedChanges => !_strategy.AreEqual(_draft, _applied);

	internal void Edit(TConfig config) => _draft = CopyValidated(config);

	internal void Apply() => _applied = CopyValidated(_draft);

	internal void SetDraft(TConfig config) => _draft = CopyValidated(config);

	internal void ResetDraft() => _draft = CopyValidated(_strategy.CreateDefault());

	internal TConfig GetApplied()
	{
		if (HasUnappliedChanges)
			throw new InvalidOperationException("存在未应用的修改，请先点击应用。");
		return _strategy.Copy(_applied);
	}

	TConfig CopyValidated(TConfig config)
	{
		if (config == null) throw new ArgumentNullException(nameof(config));
		_strategy.Validate(config);
		return _strategy.Copy(config);
	}
}
