using System;
using System.Collections.Generic;
using System.Linq;
using World.Utils;

namespace World.Data;

/// <summary>本轮全部板块的新姿态；地表采样、归属和高度不在此模块修改。</summary>
public sealed class PlateMovementResult : WorldSculptingModuleOutput
{
	public IReadOnlyList<SculptingPlateState> Plates { get; }
	internal IReadOnlyList<RotationD> InverseSurfaceMoves { get; }
	internal IReadOnlyList<PlateRotation> Rotations { get; }

	/// <summary>复制新板块表，并要求与来源世界的板块 ID 一一对应。</summary>
	internal PlateMovementResult(WorldSculptingState source, WorldSculptingRound round,
		IEnumerable<SculptingPlateState> plates, PlateRotation[] rotations) : base(source, round)
	{
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		SculptingPlateState[] copy = plates.ToArray();
		if (copy.Length != source.Plates.Count) throw new ArgumentException("移动结果板块数不一致", nameof(plates));
		for (int i = 0; i < copy.Length; i++)
			if (copy[i] == null || copy[i].PlateId != i
				|| copy[i].Density != source.Plates[i].Density
				|| !ReferenceEquals(copy[i].ReferenceSeed, source.Plates[i].ReferenceSeed))
				throw new ArgumentException($"移动不能修改板块身份或参考定义：{i}", nameof(plates));
		Plates = Array.AsReadOnly(copy);
		Rotations = Array.AsReadOnly(rotations);
		InverseSurfaceMoves = Array.AsReadOnly(copy.Select((plate, id) =>
			RotationOps.Compose(source.Surface.SampledPlateOrientations[id],
				RotationOps.Inverse(plate.Orientation))).ToArray());
	}
}
