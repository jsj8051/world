using System.Linq;
using World.Data;

namespace World.Logic;

/// <summary>世界生成阶段使用的逐格地表数据。</summary>
internal sealed record WorldSurfaceInput(int[] PlateOfCell, Plate[] Plates, CrustField Crust,
	float[] HeightM, PlateBoundarySet Boundaries, PlateBoundaryFields Boundary)
{
	public static WorldSurfaceInput FromEvolution(EvolutionSimulation solver, EvolutionFrame frame, EvolutionSpec spec)
	{
		CrustProjection projection = frame.Projection;
		var plates = solver.Plates.ToArray();
		var crust = new CrustField((CrustType[])projection.Crust.Type.Clone(),
			(int[])projection.Crust.ProvinceId.Clone(), (CrustProvince[])projection.Crust.Provinces.Clone());
		var boundary = PlateBoundaryFields.FromEvolution(projection.PlateBoundaries, projection.Boundary,
			plates, spec.ActiveSpeedKmPerMa);
		return new WorldSurfaceInput(projection.PlateOfCell.ToArray(), plates, crust,
			projection.HeightM.ToArray(), projection.PlateBoundaries, boundary);
	}
}
