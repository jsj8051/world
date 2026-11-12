using System;
using Godot;

namespace World.Scene;

/// <summary>
/// 提示通道：第一条消息出现时才创建状态条，之后复用同一份；清空只隐藏面板。
/// 页面拿到的也是这个通道，因此提示的去向与宿主实现解耦。
/// </summary>
internal sealed class AppNoticeBoard : INoticeSink
{
	const string StatusBarScene = "res://scenes/app/AppStatusBar.tscn";

	readonly Node _owner;
	AppStatusBar _bar;

	internal AppNoticeBoard(Node owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

	public void Notify(string message, NoticeKind kind)
	{
		AppStatusBar bar = EnsureBar();
		if (bar == null)
		{
			GD.PushWarning($"[NOTICE] {kind}：{message}");
			return;
		}
		bar.ShowNotice(message, kind);
	}

	public void Clear() => _bar?.Clear();

	AppStatusBar EnsureBar()
	{
		if (_bar != null) return _bar;
		var packed = ResourceLoader.Load<PackedScene>(StatusBarScene);
		_bar = packed?.Instantiate<AppStatusBar>();
		if (_bar == null)
		{
			GD.PushError($"[NOTICE] 状态条场景缺失：{StatusBarScene}");
			return null;
		}
		_owner.AddChild(_bar);
		return _bar;
	}
}
