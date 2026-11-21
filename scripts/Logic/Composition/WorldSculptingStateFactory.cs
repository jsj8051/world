using System;
using System.Linq;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>以现有完整世界快照为新雕刻会话的参考世界；复制持久地表，不携带旧物理时钟和角速度。</summary>
public static class WorldSculptingStateFactory
{
	/// <summary>将当前已生成/导入地表定为参考姿态零点。世界身份由调用方提供。</summary>
	/// <example><code>var state = WorldSculptingStateFactory.FromFacts(sim.Facts, Guid.NewGuid());</code></example>
	public static WorldSculptingState FromFacts(WorldFacts facts, Guid worldId)
	{
		if (facts == null) throw new ArgumentNullException(nameof(facts));
		return FromSurface(facts.Ball, facts.Plates, facts.PlateOfCell, facts.Crust, facts.HeightM, facts.SeaLevelM, worldId);
	}

	internal static WorldSculptingState FromSurface(Ball ball, Plate[] plateTable, int[] plateOfCell,
		CrustField crust, float[] heights, float seaLevelM, Guid worldId)
	{
		var plates = new SculptingPlateState[plateTable.Length];
		var activePlates = plateOfCell.ToHashSet();
		for (int i = 0; i < plates.Length; i++)
		{
			Plate plate = plateTable[i];
			if (plate.PlateId != i) throw new ArgumentException("来源板块表 ID 不连续", nameof(plateTable));
			double sin = Math.Sin(plate.SeedTheta);
			var seed = new SpherePointD(sin * Math.Cos(plate.SeedPhi), Math.Cos(plate.SeedTheta),
				sin * Math.Sin(plate.SeedPhi));
			var state = new SculptingPlateState(i, plate.Density, seed, RotationD.Identity);
			plates[i] = activePlates.Contains(i) ? state : state.Extinguish();
		}
		var surface = new SculptingSurfaceState(ball.Res, ball.CellIds, plateOfCell,
			heights, crust.Type, crust.ProvinceId, crust.Provinces,
			plates.Select(plate => plate.Orientation), seaLevelM);
		return new WorldSculptingState(worldId, ball.Res, ball.Radius, plates, surface);
	}
}
