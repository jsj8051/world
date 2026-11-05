using System;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 板块阶段组装：定布局档 → 撒种子点 → 阻力场洪泛划分 → 派定壳型 → 建边界集 → 算边界场；
/// 算法全在 <see cref="PlateManager"/> / <see cref="PlateBoundaryFields"/>。
/// 产物 = 板块划分（<see cref="PlateOfCell"/> / <see cref="Plates"/>，含壳型）+ 边界
/// （<see cref="Boundaries"/> 几何 / <see cref="Boundary"/> 运动学）+ 陆面基线偏移
/// （<see cref="LandBaseDropM"/>，① 只收这一个数）。
/// 壳型只决定 ① 铺陆面还是海底，海陆由 ① 的最终高度定。
/// </summary>
public sealed class PlatePipeline
{
	/// <summary>板块实现（全部产物由它持有）。</summary>
	readonly PlateManager _impl = new();

	/// <summary>逐格板块号。</summary>
	public int[] PlateOfCell => _impl.PlateOfCell;
	/// <summary>板块表（含每块壳型 <see cref="World.Data.Plate.Crust"/>）。</summary>
	public Plate[] Plates => _impl.Plates;

	/// <summary>板块边界集（共享边 + 板块对 + 逐格关联）。</summary>
	public PlateBoundarySet Boundaries => _impl.Boundaries;

	/// <summary>板块边界场（逐格运动 / 角色 / 跳数 + 逐边类型）。</summary>
	public PlateBoundaryFields Boundary { get; private set; } = PlateBoundaryFields.Empty;

	/// <summary>陆面基线偏移（米；群岛档 = <c>ArchipelagoBaseDropM</c>，其余 0）。
	/// ① 只收这个数，不认识布局枚举。</summary>
	public float LandBaseDropM { get; private set; }

	/// <param name="spec">世界定义（种子 + 布局档 / 四档权重 + 板块配置 + 地形配置的群岛压基线）。</param>
	public void Run(Ball ball, WorldSpec spec)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));

		var layout = ContinentLayouts.Resolve(spec);
		_impl.BuildSeeds(spec.Seed, spec.Plate.PlateCount, spec.Plate.MinSpeed, spec.Plate.MaxSpeed);
		_impl.GrowWithResistance(ball.CellIds, ball.Res, ball.CellDirs, ball.CellNeighbors,
			spec.Seed, spec.Plate.BoundaryAmplitude, spec.Plate.BoundaryWavelengthKm,
			spec.Plate.BoundaryOctaves);
		_impl.AssignCrustTypes(spec.Seed, spec.Plate, layout);
		_impl.BuildBoundaries(ball);
		Boundary = PlateBoundaryFields.Build(ball, _impl.Boundaries, _impl.PlateOfCell, _impl.Plates);

		LandBaseDropM = layout == ContinentLayout.Archipelago ? spec.Terrain.ArchipelagoBaseDropM : 0f;
	}
}
