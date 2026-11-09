namespace World.Data;

/// <summary>
/// 边界类型：按法向相对速率分三档，阈值与山带 / 海底共用一份活动边界口径。
/// 与 <see cref="BoundaryRole"/> 正交——Kind 说“这条边怎么动”，Role 说“这一侧扮演什么”。
/// 默认值 = <see cref="Transform"/>：漏算时退化为“不动”，而不是误判成汇聚。
/// </summary>
public enum BoundaryKind : byte
{
	/// <summary>走滑 / 不活动：|motion| 不超过活动阈值。</summary>
	Transform,
	/// <summary>汇聚：两侧迎面（motion &lt; 0）。</summary>
	Convergent,
	/// <summary>张裂：两侧背向（motion &gt; 0）。</summary>
	Divergent,
	/// <summary>不活动：演化物理场的法向与切向都低于阈值；与走滑分开，避免完全静止冒充断层。</summary>
	Inactive,
}
