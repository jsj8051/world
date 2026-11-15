using System;

namespace World.Application.Loading;

/// <summary>失败码、阶段与消息；只读，由流程转给提示层。</summary>
internal sealed class LoadFailure
{
	internal const int MaxCodeLength = 64;
	internal const int MaxStageLength = 80;
	internal const int MaxMessageLength = 500;

	public LoadFailure(string code, string stage, string message)
	{
		if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
			throw new ArgumentException($"失败码必须为 1–{MaxCodeLength} 个字符。", nameof(code));
		if (stage == null || stage.Length > MaxStageLength)
			throw new ArgumentException($"阶段文字不能超过 {MaxStageLength} 个字符。", nameof(stage));
		if (string.IsNullOrWhiteSpace(message) || message.Length > MaxMessageLength)
			throw new ArgumentException($"失败消息必须为 1–{MaxMessageLength} 个字符。", nameof(message));

		Code = code;
		Stage = stage;
		Message = message;
	}

	public string Code { get; }
	public string Stage { get; }
	public string Message { get; }
}
