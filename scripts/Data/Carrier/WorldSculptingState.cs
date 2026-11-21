using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Data;

/// <summary>真实雕刻持久状态：世界身份、网格规格、当前板块姿态和上次采样地表。不保存派生气候/水文。</summary>
public sealed class WorldSculptingState
{
	public Guid WorldId { get; }
	public int H3Resolution { get; }
	public double Radius { get; }
	public IReadOnlyList<SculptingPlateState> Plates { get; }
	public SculptingSurfaceState Surface { get; }

	/// <summary>建立持久状态；板块按 ID 连续排列，表长必须与地表采样姿态一致。轮号由 session 管理。</summary>
	/// <example><code>var state = new WorldSculptingState(id, 4, radius, plates, surface);</code></example>
	public WorldSculptingState(Guid worldId, int h3Resolution, double radius,
		IEnumerable<SculptingPlateState> plates, SculptingSurfaceState surface)
	{
		if (worldId == Guid.Empty) throw new ArgumentException("世界身份不能为空", nameof(worldId));
		if (h3Resolution is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(h3Resolution));
		if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		Surface = surface ?? throw new ArgumentNullException(nameof(surface));
		if (surface.H3Resolution != h3Resolution)
			throw new ArgumentException("世界与地表 H3 分辨率不一致", nameof(surface));
		SculptingPlateState[] copy = plates.ToArray();
		if (copy.Length == 0 || copy.Length != surface.SampledPlateOrientations.Count)
			throw new ArgumentException("板块表与地表采样姿态数不一致", nameof(plates));
		for (int i = 0; i < copy.Length; i++)
			if (copy[i] == null || copy[i].PlateId != i)
				throw new ArgumentException($"板块表必须按 ID 连续排列：{i}", nameof(plates));
		if (copy.Any(plate => plate.IsExtinct) && surface.PlateOfCell.Any(id => copy[id].IsExtinct))
			throw new ArgumentException("消亡板块不能持有地表格", nameof(surface));
		WorldId = worldId;
		H3Resolution = h3Resolution;
		Radius = radius;
		Plates = Array.AsReadOnly(copy);
	}
}
