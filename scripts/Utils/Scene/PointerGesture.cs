using Godot;

namespace World.Utils;

/// <summary>一次指针手势的意图。`None` = 与手势无关（如滚轮、非左键）。</summary>
public enum PointerIntent
{
	None,
	/// <summary>按下与抬起之间位移 ≤ 阈值 ⇒ 点选。</summary>
	Click,
	/// <summary>左键按下期间的移动，或位移超阈值后的抬起 ⇒ 拖拽。</summary>
	Drag,
}

/// <summary>
/// **指针手势判定**（普通类，非 Node）：把"这一下左键算点击还是拖拽"收成**一处**判据。
///
/// ★为什么需要它（债务清单 A-6 的修法）：原先这条判据**写了两遍**，而两处的配合
///   （"拖过 6px ⇒ 点选不触发、旋转照转"）**只活在注释里**：
///     · 点选（`RenderManager`）：按下记位 → 抬起时位移 ≤ 6px 才算点选；
///     · 相机旋转（`OrbitalCamera`）：左键按住 + 鼠标移动就转（**无阈值**）。
///   两个消费者各写一遍阈值 ⇒ 改一处忘另一处就静默不一致。现在阈值只在构造函数里定义一次。
///
/// ★每人一个实例（各持自己的按下状态）：消费者们**同时**收到同一批事件（Godot 把事件投递给
///   每个覆写了 `_UnhandledInput` 的节点）⇒ 状态不能共享，判据必须共享。
///
/// ★与 `NodeDependency` 同层同理：只用 Godot 的**类型与结构**（`Vector2` / `InputEvent` /
///   `Input.IsMouseButtonPressed`），**不碰 `GD.*` 等运行期引擎调用**（那在无宿主的单测进程里是崩溃）。
/// </summary>
public sealed class PointerGesture
{
	/// <summary>点选容差（px）：按下到抬起的位移不超过它才算"点选"，否则算"拖拽"。**唯一定义处**。</summary>
	public const float ClickSlopPx = 6f;

	readonly float _clickSlopPx;
	Vector2 _pressPos;
	bool _pressed;

	/// <param name="clickSlopPx">点选容差（px）；不传则用 <see cref="ClickSlopPx"/>。</param>
	public PointerGesture(float clickSlopPx = ClickSlopPx) => _clickSlopPx = clickSlopPx;

	/// <summary>
	/// **判读一个输入事件**。左键按下记位；左键移动或抬起时给出意图。
	/// 事件本身决定不了的事（"左键现在是否仍按着"）**从事件读**（`InputEventMouseButton.Pressed`）
	/// —— 那是本次点击的事件链自己携带的状态，不受其它手势干扰，也不再需要 `Input.IsMouseButtonPressed`。
	/// </summary>
	/// <param name="e">候选事件。</param>
	/// <param name="at">意图发生处的位置（`None` 时无意义）。</param>
	public PointerIntent Feed(InputEvent e, out Vector2 at)
	{
		at = default;
		switch (e)
		{
			case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
				if (mb.Pressed)
				{
					_pressed = true;
					_pressPos = mb.Position;
					return PointerIntent.None;      // 按下本身不是手势完成
				}
				if (!_pressed) return PointerIntent.None;   // 没经过我们记的按下（如从外部开始）⇒ 不认
				_pressed = false;
				at = mb.Position;
				return IsClick(_pressPos, mb.Position, _clickSlopPx) ? PointerIntent.Click : PointerIntent.Drag;

			case InputEventMouseMotion when _pressed:
				at = ((InputEventMouseMotion)e).Position;
				return PointerIntent.Drag;

			default:
				return PointerIntent.None;
		}
	}

	/// <summary>
	/// **点选判据本身**（纯函数，可独立单测）：按下到抬起的位移是否在容差内。
	/// ★单独暴露的理由：判据里真正会写错的是**这一行**（阈值、比较方向、距离口径），
	///   而事件链（按下/移动/抬起）需要引擎宿主才能造 ⇒ 单测只能钉住这一行，
	///   所以它必须是可独立调用的纯函数，而不是埋在 `Feed` 里的内联表达式。
	/// </summary>
	public static bool IsClick(Vector2 pressPos, Vector2 releasePos, float clickSlopPx = ClickSlopPx)
		=> pressPos.DistanceTo(releasePos) <= clickSlopPx;
}
