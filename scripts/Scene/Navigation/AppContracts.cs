using System;
using System.Threading;
using World.Data.Generation;
using World.Logic;

namespace World.Scene;

/// <summary>宿主提示的语义等级（颜色与去向由宿主决定）。</summary>
public enum NoticeKind { Info, Success, Error }

/// <summary>页面可用的宿主能力：导航与跨页面提示。页面不依赖 <see cref="AppRoot"/> 具体类型。</summary>
internal interface IPageHost
{
	void ShowMainMenu();
	void ShowGeography();
	void StartGeography(GenerationConfig config);
	void ShowMapLibrary();
	void OpenMap(string path);
	void ShowMapBrowser(ImportedWorldFactsSource source);
	void Notify(string message, NoticeKind kind = NoticeKind.Info);
}

/// <summary>生成页面入树前接收已应用配置，不依赖配置页节点或档案存储。</summary>
internal interface IGenerationPage
{
	void ConfigureGeneration(GenerationConfig config, Action<Exception> onFailure);
}

/// <summary>页面生命周期上下文：宿主、本页取消令牌。F6 单跑时 Host 为 null。</summary>
internal sealed class PageContext
{
	public IPageHost Host { get; }
	public CancellationToken Lifetime { get; }
	public ILoadProgressSink Progress { get; }
	public bool IsStandalone => Host == null;

	public PageContext(IPageHost host, CancellationToken lifetime, ILoadProgressSink progress = null)
	{
		Host = host;
		Lifetime = lifetime;
		Progress = progress;
	}
}

/// <summary>页面契约：入树前由宿主注入上下文，离开前收到一次清理回调。</summary>
internal interface IAppPage
{
	void Configure(PageContext context);
	void OnHidden();
}