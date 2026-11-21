using World.Data;

namespace World.Logic;

/// <summary>世界生成阶段使用的逐格地表数据。</summary>
internal sealed record WorldSurfaceInput(int[] PlateOfCell, Plate[] Plates, CrustField Crust,
	float[] HeightM, PlateBoundarySet Boundaries, PlateBoundaryFields Boundary);
