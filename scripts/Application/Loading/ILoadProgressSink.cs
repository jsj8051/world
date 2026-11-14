namespace World.Application.Loading;

/// <summary>页面加载创建操作的入口；同一时刻最多一个非终态操作。</summary>
internal interface ILoadProgressSink
{
	LoadOperation Begin(LoadFlow flow, string title);
}
