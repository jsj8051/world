namespace World.Data;

/// <summary>
/// 板块边界的一条边：相邻两格（分属不同板块）之间的共享边。规范序 <c>CellA &lt; CellB</c>，
/// 同一次划分下唯一且可比。纯几何——不含速度与角色：那些是欧拉矢量 / 壳型的函数，
/// 由 <see cref="PlateBoundaryPair"/> 与 <c>PlateBoundaryFields</c> 另算，故本表不随调参失效。
/// </summary>
public readonly struct PlateBoundaryEdge
{
	/// <summary>规范序左端格号（较小者）。</summary>
	public readonly int CellA;
	/// <summary>规范序右端格号（较大者）。</summary>
	public readonly int CellB;
	/// <summary>左端所属板块号（= 逐格板块号[CellA]）。</summary>
	public readonly int PlateA;
	/// <summary>右端所属板块号（= 逐格板块号[CellB]）。</summary>
	public readonly int PlateB;

	internal PlateBoundaryEdge(int cellA, int cellB, int plateA, int plateB)
	{
		CellA = cellA;
		CellB = cellB;
		PlateA = plateA;
		PlateB = plateB;
	}

	/// <summary>该格是不是这条边的一端。</summary>
	public bool Contains(int cell) => cell == CellA || cell == CellB;

	/// <summary>另一端格号。入参必须是本边的一端。</summary>
	public int OtherCell(int cell) => cell == CellA ? CellB : CellA;

	/// <summary>另一端的板块号。入参必须是本边的一端。</summary>
	public int OtherPlate(int cell) => cell == CellA ? PlateB : PlateA;
}
