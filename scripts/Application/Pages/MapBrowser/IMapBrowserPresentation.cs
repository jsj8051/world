using World.Logic;

namespace World.Application.Pages;

/// <summary>浏览画面的主线程端口；流程控制器不依赖 Godot 节点。</summary>
internal interface IMapBrowserPresentation
{
	void Begin(ImportedWorldFactsSource source);
	bool StepBuild(double budgetMs);
	int BuildTotal { get; }
	int BuildStep { get; }
	string BuildStage { get; }
	void Tick(double delta);
	void Stop();
}
