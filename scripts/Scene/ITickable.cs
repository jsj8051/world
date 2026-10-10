namespace World.Scene;

/// <summary>
/// **每帧推进契约**：根管理器按注册序转发 `Tick`，仅此而已。
/// 接口里**故意只有 `Tick`**——各管理器初始化所需依赖各不相同（世界要世界定义、界面要模式表），
/// 无参 `Initialize()` 只能被实现成空壳；初始化本就该由父上下文显式注入依赖。
/// 输入事件与通知不走这里：前者自己实现 `_UnhandledInput`，后者自己 `EmitSignal` 由父节点连接。
/// </summary>
public interface ITickable
{
	/// <summary>每帧推进（由父管理器按注册序转发；不要自己实现 `_Process`，否则帧内顺序不可控）。</summary>
	void Tick(double delta);
}
