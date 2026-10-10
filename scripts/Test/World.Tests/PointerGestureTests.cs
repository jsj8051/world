using NUnit.Framework;
using Godot;               // Vector2（纯结构体，不需引擎运行时）
using World.Utils;

namespace World.Tests;

/// <summary>
/// **指针手势判据护栏**（债务清单 A-6：判据写了两遍 ⇒ 收成一处）。
///
/// 本文件钉住 `PointerGesture` 里**真正会写错的那一行**——"按下到抬起的位移算不算点选"
/// （阈值、比较方向、距离口径）。事件链（按下 → 移动 → 抬起）需要引擎宿主才能造，
/// 故那部分不在单测范围；判据被单独暴露成纯函数 `IsClick` 正是为了这里能钉住它。
///
/// 纪律（同 SeedDerivationTests）：只用 [Test]；不写文件；**不触碰任何 `GD.*` / 引擎运行期调用**
/// （只用 `Vector2` 值类型 —— 在无 Godot 宿主的测试进程里，任何引擎调用都是进程级崩溃）。
/// </summary>
public class PointerGestureTests
{
	[Test]
	public void IsClick_TrueWithinSlop_IncludingExactlyAtSlop()
	{
		// 容差是"不超过"⇒ 边界值（恰好 6px）必须算点选。这是最容易写错的一格：
		// 写成 `<` 会让"正好 6px 的点击"变成拖拽——症状是偶发点不中，且极难复现。
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0), new Vector2(0, 0)), Is.True, "零位移必是点选");
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0), new Vector2(3, 4)), Is.True, "5px（3-4-5 直角三角形）应算点选");
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0), new Vector2(0, PointerGesture.ClickSlopPx)), Is.True,
			"恰好等于容差应算点选（判据是 ≤ 而非 <）");
		Assert.That(PointerGesture.IsClick(new Vector2(100, 100), new Vector2(100, 100 - PointerGesture.ClickSlopPx)), Is.True,
			"绝对位置无关，只看位移");
	}

	[Test]
	public void IsClick_FalseBeyondSlop_AlongAnyAxisOrDiagonal()
	{
		float over = PointerGesture.ClickSlopPx + 0.01f;
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0), new Vector2(over, 0)), Is.False, "水平超一点即拖拽");
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0), new Vector2(0, over)), Is.False, "垂直同理");
		// ★对角线用欧氏距离而非曼哈顿/切比雪夫：6px 的 x 与 6px 的 y 合起来位移 ≈8.49px ⇒ 拖拽。
		//   若有人把 DistanceTo 换成 |dx|+|dy| 或 Max(|dx|,|dy|)，这一格会红。
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0),
			new Vector2(PointerGesture.ClickSlopPx, PointerGesture.ClickSlopPx)), Is.False,
			"斜向 6+6px 的实际位移超过容差 ⇒ 必须判为拖拽（距离口径 = 欧氏）");
	}

	[Test]
	public void IsClick_HonoursCustomSlop()
	{
		// 阈值可由构造函数/参数覆盖（消费者各自可调），但仍只有一份判据。
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0), new Vector2(10, 0), 20f), Is.True,
			"容差 20px 时 10px 位移应算点选");
		Assert.That(PointerGesture.IsClick(new Vector2(0, 0), new Vector2(10, 0), 5f), Is.False,
			"容差 5px 时 10px 位移应算拖拽");
	}

	[Test]
	public void ClickSlopPx_IsTheSingleDocumentedValue()
	{
		// 阈值是"用户手感"参数：改动它应当是一次**有意的**决定（此测试让改动必须被看见）。
		// 原实现把它内联在 `RenderManager._UnhandledInput` 的 `mb.Position.DistanceTo(_pressPos) <= 6f`，
		// 且相机侧完全无阈值 ⇒ 两处不一致时无人察觉。现在它只有一个定义处。
		Assert.That(PointerGesture.ClickSlopPx, Is.EqualTo(6f),
			"点选容差 6px（回归自原 WorldManager/RenderManager 的内联值）；改它须同步复查相机拖拽手感");
	}
}
