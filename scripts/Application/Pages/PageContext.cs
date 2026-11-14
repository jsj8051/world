using System;
using System.Threading;
using World.Application.Loading;

namespace World.Application.Pages;

/// <summary>
/// 页面生命周期上下文：本页标识、取消令牌与宿主提供的可选能力。
/// 实现页面只依赖这里的令牌与两个出口，不依赖任何页面类型或创建方式。
/// </summary>
internal sealed class PageContext
{
	internal PageContext(CancellationToken lifetime, INoticeSink notice = null, ILoadProgressSink progress = null)
	{
		PageId = Guid.NewGuid();
		Lifetime = lifetime;
		Notice = notice;
		Progress = progress;
	}

	public Guid PageId { get; }
	public CancellationToken Lifetime { get; }
	public INoticeSink Notice { get; }
	public ILoadProgressSink Progress { get; }
}
