namespace World.Data;

/// <summary>
/// 一对板块之间的整条边界：边在边界集里连续存放（<see cref="EdgeStart"/> / <see cref="EdgeCount"/>）。
/// 只按板块对聚合，<b>不保证连通</b>——两块板块可以在两处相接，仍算同一条“边界”。
/// 纯几何索引；运动与角色由板块属性派生，不缓存在此。
/// </summary>
public readonly struct PlateBoundaryPair
{
	/// <summary>板块号较小的一侧。</summary>
	public readonly int PlateA;
	/// <summary>板块号较大的一侧。</summary>
	public readonly int PlateB;

	/// <summary>本对板块的边在边界集 <c>Edges</c> 里的起始下标。</summary>
	public readonly int EdgeStart;
	/// <summary>本对板块的边数（0 = 两块不接壤）。</summary>
	public readonly int EdgeCount;

	internal PlateBoundaryPair(int plateA, int plateB, int edgeStart, int edgeCount)
	{
		PlateA = plateA;
		PlateB = plateB;
		EdgeStart = edgeStart;
		EdgeCount = edgeCount;
	}

	/// <summary>两块板块之间是否真的有边界。</summary>
	public bool Exists => EdgeCount > 0;

	/// <summary>本对板块边界的结束下标（不含）。</summary>
	public int EdgeEnd => EdgeStart + EdgeCount;
}
