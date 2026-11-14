namespace World.Application.Pages;

/// <summary>单向反馈通道：只写不读，不改变导航状态。</summary>
internal interface INoticeSink
{
	void Notify(string message, NoticeKind kind = NoticeKind.Info);
}
