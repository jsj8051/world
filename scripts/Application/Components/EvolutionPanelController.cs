using System;
using System.Globalization;
using World.Data;

namespace World.Application.Components;

/// <summary>演化面板的控件可用性快照；视图照此写按钮与文字。</summary>
internal readonly record struct EvolutionPanelControls(
	bool PlayEnabled, bool PauseEnabled, bool StepEnabled, string StepText,
	bool JumpEnabled, bool SeekEditable, string StatusText, bool StatusIsError);

/// <summary>
/// 演化面板的逻辑：把演化控制状态翻译成按钮门禁与状态文字，解析跳转目标与倍率映射。
/// 不持有 Godot 控件；进度订阅与读数格式化等表现细节留在视图。
/// </summary>
internal sealed class EvolutionPanelController
{
	/// <summary>倍率下拉的注册序与地质年/秒的对应表。</summary>
	internal static readonly double[] YearsPerSecondOptions = { 100_000d, 1_000_000d, 10_000_000d };

	internal static EvolutionPanelControls Describe(EvolutionControlStatus status, bool busy)
	{
		bool canStep = !busy && status == EvolutionControlStatus.Paused;
		bool canSeek = !busy && (status == EvolutionControlStatus.Paused || status == EvolutionControlStatus.Playing);
		bool canRecover = !busy && status == EvolutionControlStatus.Faulted;
		string text = busy ? "正在计算…完成后刷新地图" : status switch
		{
			EvolutionControlStatus.Paused => "暂停",
			EvolutionControlStatus.Playing => "播放中",
			EvolutionControlStatus.Seeking => "跳转中",
			EvolutionControlStatus.Faulted => "演化失败，详情见输出",
			_ => "未初始化",
		};
		return new EvolutionPanelControls(
			PlayEnabled: canStep,
			PauseEnabled: status == EvolutionControlStatus.Playing,
			StepEnabled: canStep || canRecover,
			StepText: canRecover ? "恢复" : "单步",
			JumpEnabled: canSeek,
			SeekEditable: canSeek,
			StatusText: text,
			StatusIsError: status == EvolutionControlStatus.Faulted);
	}

	/// <summary>倍率下拉下标 → 地质年/秒；越界按最后一档处理。</summary>
	internal static double YearsPerSecond(int optionIndex)
		=> YearsPerSecondOptions[Math.Clamp(optionIndex, 0, YearsPerSecondOptions.Length - 1)];

	/// <summary>解析跳转输入：非负有限数且使用不随语言变化的数字格式。</summary>
	internal static bool TryParseSeekTarget(string text, out double targetMa)
		=> double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out targetMa)
			&& double.IsFinite(targetMa) && targetMa >= 0;
}
