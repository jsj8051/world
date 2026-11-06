using System;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 板块阶段装配：布局定档 → 撒种子点 → 阻力场洪泛 → 逐格壳型 → 建边界集。
/// 算法在 <see cref="PlateSeedOps"/> / <see cref="PlateFloodOps"/> / <see cref="PlateCrustOps"/> /
/// <see cref="PlateBoundaryBuilder"/>；本类只按序调用并持有产物。
/// 壳型是本阶段唯一的陆海产物：陆壳格由 ① 阶段铺陆面高度、洋壳铺海底；
/// 海岸线不由本阶段决定，而是 ① 阶段高度场穿过海面的结果。
/// </summary>
public sealed class PlateManager
{
	/// <summary>逐格板块号（长度 = 格数；每格恒有归属，取值 ∈ [0, 板块数)）。</summary>
	public int[] PlateOfCell { get; private set; } = Array.Empty<int>();
	/// <summary>板块表（号即下标；欧拉矢量 / 密度）。</summary>
	public Plate[] Plates { get; private set; } = Array.Empty<Plate>();
	/// <summary>逐格壳型与陆壳省（壳型的唯一真源）。</summary>
	public CrustField Crust { get; private set; } = CrustField.Empty;
	/// <summary>逐块格数（长度 = 板块数；Σ = 格数）。</summary>
	public int[] SizeOfPlate { get; private set; } = Array.Empty<int>();

	/// <summary>板块邻接矩阵（<c>[a,b]</c> = 两块有直接相邻的格；对称、对角线 false）。
	/// 由洪泛后的逐格归属统计得到——「陆壳连成一片」靠它判定。</summary>
	public bool[,] PlateAdjacency { get; private set; } = new bool[0, 0];

	/// <summary>板块边界集（划分的派生索引；共享边 + 板块对 + 逐格关联）。</summary>
	public PlateBoundarySet Boundaries { get; private set; } = PlateBoundarySet.Empty;

	/// <summary>本次世界的陆块布局档（<see cref="ContinentLayout.Auto"/> 已按权重抽定）。</summary>
	public ContinentLayout Layout { get; private set; } = ContinentLayout.Auto;

	/// <summary>陆面基线偏移（米；群岛档 = <c>ArchipelagoBaseDropM</c>，其余 0）。① 只收这个数。</summary>
	public float LandBaseDropM { get; private set; }

	/// <summary>跑完 ⓪ 的全部步骤；产物留在本实例上，重复调用会整体换新。</summary>
	public void Run(Ball ball, WorldSpec spec)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));

		Layout = ContinentLayouts.Resolve(spec);
		Plates = PlateSeedOps.Build(spec.Seed, spec.Plate.PlateCount, spec.Plate.MinSpeed, spec.Plate.MaxSpeed);

		PlateFloodOps.Result flood = PlateFloodOps.Grow(ball, Plates, spec.Seed,
			spec.Plate.BoundaryAmplitude, spec.Plate.BoundaryWavelengthKm, spec.Plate.BoundaryOctaves);
		PlateOfCell = flood.Owner;
		SizeOfPlate = flood.Size;
		PlateAdjacency = flood.Adjacency;

		Crust = PlateCrustOps.Build(ball, spec.Seed, spec.Plate, Layout, PlateOfCell, Plates.Length,
			SizeOfPlate, PlateAdjacency);
		Boundaries = PlateBoundaryBuilder.Build(ball, PlateOfCell, Plates);

		LandBaseDropM = Layout == ContinentLayout.Archipelago ? spec.Terrain.ArchipelagoBaseDropM : 0f;
	}
}
