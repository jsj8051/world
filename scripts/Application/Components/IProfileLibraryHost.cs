using System;
using World.Data.Generation;

namespace World.Application.Components;

/// <summary>选择一张命名配置的结果：直接选中、需要确认（草稿未应用）或被拒绝（草稿非法）。</summary>
internal enum ProfileSelectOutcome
{
	Selected,
	ConfirmationRequired,
	Rejected,
}

/// <summary>
/// 配置库组件之外的宿主能力：读取列表与选中项，并执行载入、保存、重命名、删除。
/// 页面视图实现本接口，把草稿同步与编辑器回填留在页面层。
/// </summary>
internal interface IProfileLibraryHost
{
	GenerationProfile[] Profiles { get; }
	Guid? SelectedProfileId { get; }
	bool HasUnappliedChanges { get; }

	/// <summary>把编辑器当前值同步到草稿；非法输入返回 false 并已报告错误。</summary>
	bool TrySyncDraft();

	/// <summary>同步草稿后尝试选择；草稿非法时返回 <see cref="ProfileSelectOutcome.Rejected"/>。</summary>
	ProfileSelectOutcome TrySelectProfile(Guid id);

	/// <summary>已确认替换草稿后直接载入。</summary>
	void LoadProfile(Guid id);

	void SaveProfile(string name, bool saveAsNew);
	void RenameSelectedProfile(string name);
	void DeleteProfile(Guid id);
}
