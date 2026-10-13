using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // Region（区域元数据载体）/ Plate（板块载体）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;
using World.Constants;       // GeologyClass（区域类别词表——常量层，与 BiomeType 同族）

namespace World.Logic;

/// <summary>
/// 地质区域图：全球逐格八类划分 + 同类连通区域编号。**只是图**：不产高度、不驱动任何地形内容。
///
/// 陆：Plain / Basin / Coastline / Mountain / Rift（后两者按**板块边界相对运动**分）。
/// 海：ShallowSea / DeepSea / Ridge。
/// 同格竞争的优先级：海岸线 &gt; 碰撞带 / 张裂带 &gt; 盆地 &gt; 平原。
///
/// 「碰撞 / 分离」是**逐条边界**的性质：边界法向相对运动 &lt; 0 迎面 ⇒ 山脉（陆）/ 俯冲带（海）；
/// &gt; 0 背向 ⇒ 裂谷（陆）/ 洋中脊（海）。走滑边界（≈ 0）不产生地形，落回平原 / 深海。
/// 其余几何（海岸线 / 大陆架 / 盆地围限）由「到海陆 / 到板块边界」的距离决定。
/// </summary>
public sealed class GeologicalRegionManager
{
	/// <summary>逐格类别（八类；全球覆盖——海格也有类别）。</summary>
	public GeologyClass[] ClassOfCell { get; private set; } = Array.Empty<GeologyClass>();
	/// <summary>逐格区域号（同类的连通片；**全球覆盖**，海格也有区域号）。</summary>
	public int[] RegionOfCell { get; private set; } = Array.Empty<int>();
	/// <summary>区域表（Id 升序；区域号即下标）。</summary>
	public Region[] Regions { get; private set; } = Array.Empty<Region>();

	// ── 带类的"位置与尺度"旋钮（**物理量 km**；按本 res 的一跳折算成跳数——见 Band）──
	//   口径锚点 = 生产档 res4（CellEdgeKm ≈ 26 km）：海岸线 105 km ≈ 4 格（抖动 ⇒ 3–5 格）；
	//   浅海 157 km ≈ 6 格（抖动 ⇒ 4–8 格，比海岸线宽）。
	public const float CoastlineBaseKm = 105f, CoastlineJitterKm = 26f;
	public const float ShallowSeaBaseKm = 157f, ShallowSeaJitterKm = 52f;

	// ── 构造带的宽度与强度 ──
	/// <summary>山脉带宽基准（km）——**碰撞最强处**的缝合带半宽；碰撞越弱越窄。</summary>
	public const float MountainWidthKm = 160f;
	public const float MountainWidthJitterKm = 50f;
	/// <summary>分隔"强碰撞"与"弱碰撞"的相对运动幅度（无量纲）：达到它即视为全强度。</summary>
	public const float StrongMotion = 8f;
	/// <summary>走滑判定阈（|相对运动| 低于它 ⇒ 平行错动，不成地形）。</summary>
	public const float StrikeSlipEps = 0.6f;

	/// <summary>裂谷带宽基准（km；张裂侧）与洋中脊带宽基准（km）。</summary>
	public const float RiftWidthKm = 120f, RidgeWidthKm = 110f;

	// ── 盆地围限：距**缝合带**超过它（km）才算块体内部 ⇒ 才可能成盆地（盆地被造山带围限）──
	public const float BasinMarginKm = 350f;
	/// <summary>盆地判据的低频场阈值（在块体内部再挑一部分，避免"所有内陆都成盆地"）。</summary>
	public const float BasinThreshold = 0.15f;
	/// <summary>碎斑下限（**面积口径**，km²）：更小的带 / 块孤立小块回退为平原 / 深海。</summary>
	public const float MinPatchAreaKm2 = 5_000f;

	// 判定场的尺度（km / 八度）
	const float WidthWavelengthKm = 2500f;           // 带宽抖动：沿岸尺度
	const float BasinWavelengthKm = 1800f;           // 盆地块：克拉通内部尺度
	const float MountainWidthWavelengthKm = 3000f;   // 缝合带宽度沿走向的起伏

	readonly int _seed;
	readonly SphericalFbmNoise _widthNoise, _basinNoise, _sutureWidthNoise;

	public GeologicalRegionManager(int seed)
	{
		_seed = seed;
		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Regions_Noise));
		_widthNoise = new SphericalFbmNoise(rnd.Next(), WidthWavelengthKm, 2);
		_basinNoise = new SphericalFbmNoise(rnd.Next(), BasinWavelengthKm, 2);
		_sutureWidthNoise = new SphericalFbmNoise(rnd.Next(), MountainWidthWavelengthKm, 2);
	}

	/// <summary>枚举 → 短名（词表在常量层、文案在消费侧——与 `BiomeType` 同一处理）。</summary>
	static readonly string[] ClassNames =
		{ "Plain", "Basin", "Coastline", "Mountain", "Rift", "ShallowSea", "DeepSea", "Ridge" };
	public static string ClassName(GeologyClass c) => ClassNames[(int)c];

	/// <summary>
	/// 全量生成：掩码 + 板块划分 → 八类逐格类别 → 同类连通区域编号。
	/// </summary>
	/// <param name="ball">球壳数据层（提供邻接图与格心方向）。</param>
	/// <param name="placementLand">上游板块阶段的陆地掩码（本图只读它，不产生陆海判断）。</param>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号（本图用它定缝合带位置）。</param>
	/// <param name="plates">上游板块阶段的板块表（提供速度分量，算边界相对运动）。</param>
	public void Generate(Ball ball, bool[] placementLand, int[] plateOfCell, Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (placementLand == null) throw new ArgumentNullException(nameof(placementLand));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		int n = ball.CellDirs.Length;
		if (placementLand.Length != n)
			throw new ArgumentException($"掩码长度 {placementLand.Length} ≠ 格数 {n}", nameof(placementLand));
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		var dirs = ball.CellDirs;
		var scale = SpatialScale.Of(ball);
		float hopKm = (float)scale.CellEdgeKm;   // 物理量 → 跳数的折算口

		// 双向图距：陆格离海、海格离岸（掩码的纯函数）
		int[] distToSea = LandMaskOps.HopsFrom(ball, i => !placementLand[i]);
		int[] distToLand = LandMaskOps.HopsFrom(ball, i => placementLand[i]);
		// 逐格到板块边界跳数（边界格 = 0）
		int[] boundaryHops = PlateBoundaryOps.BoundaryHops(ball, plateOfCell);
		// 逐格边界相对运动（< 0 碰撞 / > 0 张裂 / ≈ 0 走滑）
		float[] motion = PlateBoundaryOps.BoundaryMotion(ball, plateOfCell, plates);

		// ── ① 缝合带距离（km）：逐格沿邻接图扩散，取「最近的强碰撞边界」的物理距离 ──
		float[] sutureKm = BuildSutureDistance(ball, distToSea.Length, boundaryHops, motion, hopKm);

		// ── ② 逐格初判（八类）──
		ClassOfCell = new GeologyClass[n];
		for (int i = 0; i < n; i++)
		{
			var d = dirs[i];
			// 带类的**逐格带宽（跳数）**：碰撞越强带越宽（`MountainWidthKm` 是全强度时半宽）
			float strength = MathF.Min(1f, MathF.Abs(motion[i]) / StrongMotion);
			if (placementLand[i])
			{
				int coastW = Band(hopKm, CoastlineBaseKm, CoastlineJitterKm, _widthNoise.Sample(d));
				if (distToSea[i] <= coastW) { ClassOfCell[i] = GeologyClass.Coastline; continue; }

				if (motion[i] < -StrikeSlipEps)
				{
					int mtnW = Band(hopKm, MountainWidthKm * strength, MountainWidthJitterKm, _sutureWidthNoise.Sample(d));
					if (sutureKm[i] <= (mtnW + 0.5f) * hopKm) { ClassOfCell[i] = GeologyClass.Mountain; continue; }
				}
				else if (motion[i] > StrikeSlipEps)
				{
					int riftW = Band(hopKm, RiftWidthKm * strength, MountainWidthJitterKm, _sutureWidthNoise.Sample(d));
					if (sutureKm[i] <= (riftW + 0.5f) * hopKm) { ClassOfCell[i] = GeologyClass.Rift; continue; }
				}

				if (sutureKm[i] > BasinMarginKm && _basinNoise.Sample(d) > BasinThreshold)
				{
					ClassOfCell[i] = GeologyClass.Basin;
					continue;
				}
				ClassOfCell[i] = GeologyClass.Plain;
			}
			else
			{
				if (motion[i] > StrikeSlipEps)
				{
					int ridgeW = Band(hopKm, RidgeWidthKm * strength, MountainWidthJitterKm, _sutureWidthNoise.Sample(d));
					if (sutureKm[i] <= (ridgeW + 0.5f) * hopKm) { ClassOfCell[i] = GeologyClass.Ridge; continue; }
				}
				int shelfW = Band(hopKm, ShallowSeaBaseKm, ShallowSeaJitterKm, _widthNoise.Sample(d));
				ClassOfCell[i] = distToLand[i] <= shelfW ? GeologyClass.ShallowSea : GeologyClass.DeepSea;
			}
		}

		// ── ③ 碎斑滤除（"块状"类别；带类不滤）──
		//   下限按**面积**折算成格数（res 无关；res4 ⇒ 3 格，res1 ⇒ 1 格 = 不滤）
		int minPatchCells = Math.Max(1, (int)MathF.Round(MinPatchAreaKm2 / (float)scale.CellAreaKm2));
		CullSpecks(ball, GeologyClass.Mountain, minPatchCells, GeologyClass.Plain);
		CullSpecks(ball, GeologyClass.Basin, minPatchCells, GeologyClass.Plain);
		CullSpecks(ball, GeologyClass.Ridge, minPatchCells, GeologyClass.DeepSea);

		// ── ④ 同类连通 → 区域编号 + 区域表 ──
		int[] regionOfCell = new int[n];
		Array.Fill(regionOfCell, -1);
		var regions = new List<Region>();
		float cellAreaKm2 = (float)scale.CellAreaKm2;
		for (int c = 0; c < ClassNames.Length; c++)
		{
			var cls = (GeologyClass)c;
			var (compOfCell, compCount) = LandMaskOps.Components(ball, i => ClassOfCell[i] == cls);
			var cellCount = new int[compCount];
			for (int i = 0; i < n; i++)
				if (compOfCell[i] >= 0) cellCount[compOfCell[i]]++;
			var idOfComp = new int[compCount];
			for (int k = 0; k < compCount; k++)
			{
				idOfComp[k] = regions.Count;
				regions.Add(new Region { Id = regions.Count, Class = cls, CellCount = cellCount[k], AreaKm2 = cellCount[k] * cellAreaKm2 });
			}
			for (int i = 0; i < n; i++)
				if (compOfCell[i] >= 0) regionOfCell[i] = idOfComp[compOfCell[i]];
		}
		RegionOfCell = regionOfCell;
		Regions = regions.ToArray();
	}

	/// <summary>
	/// **缝合带距离**（km）：逐格取「最近的**碰撞型**边界格」的图距 × 一跳物理长度。
	/// 只有碰撞边界（相对运动 &lt; −走滑阈）参与——张裂带另有自己的宽度判据，不共用这条。
	/// 无碰撞边界的格 = +∞（只有平原 / 盆地 / 海岸线）。
	/// </summary>
	static float[] BuildSutureDistance(Ball ball, int n, int[] boundaryHops, float[] motion, float hopKm)
	{
		var source = new bool[n];
		for (int i = 0; i < n; i++)
			source[i] = boundaryHops[i] == 0 && motion[i] < -StrikeSlipEps;
		int[] hops = LandMaskOps.HopsFrom(ball, i => source[i]);

		var km = new float[n];
		for (int i = 0; i < n; i++)
			km[i] = hops[i] < 0 ? float.PositiveInfinity : hops[i] * hopKm;
		return km;
	}

	/// <summary>
	/// 带类的"位置与尺度"：**物理带宽（km）** ± 抖动×低频场，再按本 res 的一跳折算成跳数
	/// （四舍五入，下界 ≥1——比一格还窄的带按一格算，这是离散化的必然下界）。
	/// </summary>
	static int Band(float hopKm, float baseKm, float jitterKm, float noiseSigned)
	{
		float wKm = baseKm + jitterKm * noiseSigned;
		int w = (int)MathF.Round(wKm / hopKm);
		return w < 1 ? 1 : w;
	}

	/// <summary>把该类别里格数 &lt; <paramref name="minPatchCells"/> 的连通小块回退为 <paramref name="fallback"/>。</summary>
	void CullSpecks(Ball ball, GeologyClass cls, int minPatchCells, GeologyClass fallback)
	{
		var (compOfCell, compCount) = LandMaskOps.Components(ball, i => ClassOfCell[i] == cls);
		if (compCount == 0) return;
		var cellCount = new int[compCount];
		for (int i = 0; i < compOfCell.Length; i++)
			if (compOfCell[i] >= 0) cellCount[compOfCell[i]]++;

		var small = new bool[compCount];
		for (int k = 0; k < compCount; k++) small[k] = cellCount[k] < minPatchCells;
		for (int i = 0; i < compOfCell.Length; i++)
			if (compOfCell[i] >= 0 && small[compOfCell[i]]) ClassOfCell[i] = fallback;
	}
}
