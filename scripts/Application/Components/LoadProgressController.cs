using System;

namespace World.Application.Components;

/// <summary>
/// 进度条组件逻辑：维护总数、当前准备进行的项序号与名称，视图只按它渲染；
/// 不碰 Godot 节点，也不做线程同步（调用方在主线程驱动）。
/// </summary>
internal sealed class LoadProgressController
{
	string _title = string.Empty;
	string _stage = string.Empty;
	int _step;
	int _total;
	bool _active;

	internal bool IsActive => _active;
	internal string Title => _title;
	internal string Stage => _stage;
	internal int Step => _step;
	internal int Total => _total;

	/// <summary>初始化：总数必须是正整数，当前项从 0 开始。</summary>
	internal void Init(int total, string title)
	{
		if (total < 1) throw new ArgumentOutOfRangeException(nameof(total), total, "总数必须是正整数。");
		_title = title ?? string.Empty;
		_stage = "准备中";
		_step = 0;
		_total = total;
		_active = true;
	}

	/// <summary>更新当前准备进行的项序号与名称；夹在 0–总数之间，结束后的迟到更新被忽略。</summary>
	internal void Update(int step, string stage)
	{
		if (!_active) return;
		_step = Math.Clamp(step, 0, _total);
		_stage = stage ?? string.Empty;
	}

	/// <summary>置满并结束显示。</summary>
	internal void Complete()
	{
		if (!_active) return;
		_step = _total;
		_active = false;
	}
}
