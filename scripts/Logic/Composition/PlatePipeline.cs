using System;
using World.Data;            // PlateSpec（板块配置）+ Plate（板块载体）
using World.H3Grid;          // Ball（H3 球壳数据层）

namespace World.Logic;

/// <summary>
/// 板块阶段组装：撒种子点 → 阻力场洪泛划分 → 派定壳型 → 只在陆壳上派生锚点 → 逐格陆地掩码。
/// 只做步骤编排与产物转发，算法全在 <see cref="PlateManager"/>。
/// 产物：板块划分（<see cref="PlateOfCell"/> / <see cref="Plates"/>，含每块壳型）与陆地掩码（<see cref="PlacementLand"/>）。
/// </summary>
public sealed class PlatePipeline
{
	/// <summary>板块实现（全部产物由它持有）。</summary>
	readonly PlateManager _impl = new();

	/// <summary>逐格板块号。</summary>
	public int[] PlateOfCell => _impl.PlateOfCell;
	/// <summary>板块表（含每块壳型 <see cref="World.Data.Plate.Crust"/>）。</summary>
	public Plate[] Plates => _impl.Plates;
	/// <summary>陆地掩码（海格 false / 陆格 true）。生成依据，非世界事实。</summary>
	public bool[] PlacementLand => _impl.PlacementLand;

	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">板块配置（含板块划分与陆地场）。</param>
	public void Run(Ball ball, int seed, PlateSpec spec)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		// 撒种子点
		_impl.BuildSeeds(seed, spec.PlateCount, spec.MinSpeed, spec.MaxSpeed);
		// 阻力场洪泛划分
		_impl.GrowWithResistance(ball.CellIds, ball.Res, ball.CellDirs, ball.CellNeighbors,
			seed, spec.BoundaryAmplitude, spec.BoundaryWavelengthKm, spec.BoundaryOctaves);
		// 派定壳型（陆壳 / 洋壳），只在陆壳上派生大陆锚点
		_impl.AssignCrustTypes(seed, spec);
		_impl.BuildAnchors(seed);
		// 逐格陆地掩码
		_impl.BuildLandMask(ball, seed, spec);
	}
}
