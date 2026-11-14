namespace World.Application.Pages;

/// <summary>
/// 页面契约：入树前由宿主注入上下文，离开前收到一次清理回调。
/// <see cref="Configure"/> 在 <c>AddChild</c> 之前调用，实现只应保存上下文或注入依赖，
/// 不得访问尚未就绪的子节点；抛异常时宿主保留原页面并报告失败。
/// </summary>
internal interface IAppPage
{
	void Configure(PageContext context);
	void OnHidden();
}
