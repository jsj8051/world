using System;

namespace World.Data;

/// <summary>A village identity and its H3 cell location, independent of rendering.</summary>
public sealed class Village
{
	public long Id { get; }
	public ulong CellId { get; }

	public Village(long id, ulong cellId)
	{
		if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
		if (cellId == 0) throw new ArgumentOutOfRangeException(nameof(cellId));
		Id = id;
		CellId = cellId;
	}
}
