using System;
using System.Threading;
using World.Data.Generation;
using World.Logic;

namespace World.Scene;

/// <summary>宿主提示的语义等级（颜色与去向由宿主决定）。</summary>
public enum NoticeKind { Info, Success, Error }

/// <summary>根屏幕之间的跳转：只回答“去哪一页”，不含加载与失败分支。</summary>
internal interface IPageNavigator
{
	void Navigate<TRequest>(TRequest request) where TRequest : PageRequest;
}

/// <summary>请求宿主打开一张地图：起异步读包与装配流程，结果页面由宿主决定。</summary>
internal interface IMapOpener
{
	void OpenMap(string path);
}

/// <summary>请求宿主开始一次世界生成。</summary>
internal interface IGenerationStarter
{
	void StartGeography(GenerationConfig config);
}

/// <summary>单向反馈通道：只写不读，不改变导航状态。</summary>
internal interface INoticeSink
{
	void Notify(string message, NoticeKind kind = NoticeKind.Info);
}

/// <summary>生成页面入树前接收已应用配置，不依赖配置页节点或档案存储。</summary>
internal interface IGenerationPage
{
	void ConfigureGeneration(GenerationConfig config, Action<Exception> onFailure);
}

/// <summary>
/// 页面生命周期上下文：本页标识、生命周期与可用宿主能力。
/// 能力要么全给（宿主装配），要么全不给（独立运行，<see cref="IsStandalone"/>）；
/// 只给一部分视为装配错误，构造时即拒绝。
/// </summary>
internal sealed class PageContext
{
	public PageContext(CancellationToken lifetime,
		IPageNavigator navigation = null, IMapOpener mapOpener = null,
		IGenerationStarter generation = null, INoticeSink notice = null,
		ILoadProgressSink progress = null)
	{
		int provided = (navigation == null ? 0 : 1) + (mapOpener == null ? 0 : 1)
			+ (generation == null ? 0 : 1) + (notice == null ? 0 : 1);
		if (provided != 0 && provided != 4)
			throw new ArgumentException("页面能力必须全给或全不给。", nameof(navigation));

		PageId = Guid.NewGuid();
		Lifetime = lifetime;
		Navigation = navigation;
		MapOpener = mapOpener;
		Generation = generation;
		Notice = notice;
		Progress = progress;
	}

	public Guid PageId { get; }
	public CancellationToken Lifetime { get; }
	public IPageNavigator Navigation { get; }
	public IMapOpener MapOpener { get; }
	public IGenerationStarter Generation { get; }
	public INoticeSink Notice { get; }
	public ILoadProgressSink Progress { get; }
	public bool IsStandalone => Navigation == null;
}

/// <summary>页面契约：入树前由宿主注入上下文，离开前收到一次清理回调。</summary>
internal interface IAppPage
{
	void Configure(PageContext context);
	void OnHidden();
}
