using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 区域地貌场（阶段 5，决策 05）：山脉解决"高地在哪里"，本层解决"其他地方是什么地形"。
//   PLATEAU 区域 → **高原帽**：区域尺度高斯帽（σ = 区域等效半径 ×1.25，整个区域抬成台地、
//   边缘平滑降坡——"┌──┐ 但不是完美平顶"：顶面叠中频噪声 ±18% 起伏）；
//   BASIN 区域 → **盆地下挖**：同构反向（边缘陡、中心凹——"\＿＿/"），深度 BasinDepthM。
//   决策 §七警告：**盆地不要大量生成**（否则河流系统出现大量孤立内流盆地）——配额已压 0.06，
//   此处再加每陆块硬上限 MaxBasinsPerLandmass = 2（超出配额的 BASIN 区域不生成场，只保留类型标签）。
// 语义：区域类型是**场的驱动器**（控制图），不再直接加常数偏移（TypeElevOffsetM 已退役）——
//   HIGHLAND/PLAIN/COASTAL/RIFT 暂不生成大尺度高度场（基线 + 细节噪声呈现；裂谷条带留后续批次）。
// 性能：与 MountainSkeleton 同构的角帽预筛——每区域一个帽，帽外截断。
/// <summary>
/// 区域地貌场：PLATEAU 高原抬升场 + BASIN 盆地下挖场（逐格米域，陆格非零）。
/// </summary>
public sealed class RegionalLandforms
{
	/// <summary>高原抬升（米；PLATEAU 区域帽）。</summary>
	public float[] PlateauAddM { get; private set; } = Array.Empty<float>();
	/// <summary>盆地下挖（米，正值；合成时取 −BasinDipM；BASIN 区域帽）。</summary>
	public float[] BasinDipM { get; private set; } = Array.Empty<float>();
	/// <summary>实际生成下挖场的盆地数（按陆块；每陆块上限 MaxBasinsPerLandmass——类型可为
	/// BASIN 但超上限的区域不生成场，决策 §七防内流盆地泛滥）。</summary>
	public System.Collections.Generic.Dictionary<int, int> AppliedBasinsPerLandmass { get; private set; } = new();

	// ── 场性格旋钮（地球量级；面板接线走 S5）──
	public const float PlateauHeightM = 1150f;    // 高原台面高度（基线 ~940 之上 → 台面 ~2000 高地档）
	public const float BasinDepthM = 700f;        // 盆地中心下挖（区域基座 ~150-600 → 盆底 ~100 量级）
	public const int MaxBasinsPerLandmass = 2;    // 每陆块盆地上限（决策 §七：防内流盆地泛滥）
	public const float PlateauMinSigmaKm = 1200f; // 高原帽 σ 下限（决策 05v2：大面积影响场非斑点）
	public const float BasinMinSigmaKm = 1000f;   // 盆地帽 σ 下限
	const float CapRadiusFactor = 1.25f;          // 帽 σ = max(区域等效半径 × 此值, 类型下限)

	readonly int _seed;
	readonly SphericalFbmNoise _topNoise;         // 顶面起伏（中频：台面不平/盆底不光滑）

	public RegionalLandforms(int seed)
	{
		_seed = seed;
		var rnd = new DeterministicRandom(seed ^ 0x71A0);
		_topNoise = new SphericalFbmNoise(rnd.Next(), 350f, 2);   // 顶面纹理波长 ~ 台地内部结构
	}

	/// <summary>生成区域地貌场。regions 须已 Generate。</summary>
	public void Generate(Ball ball, GeologicalRegions regions)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (regions == null) throw new ArgumentNullException(nameof(regions));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		var regionOfCell = regions.RegionOfCell;

		// 每陆块盆地计数（决策 §七硬上限；按区域号序先到先得——确定性）
		var basinCount = new Dictionary<int, int>();
		var capOf = new Dictionary<int, Cap>();   // 区域号 → 帽（plateau 正 / basin 负语义由场数组承载）
		foreach (var region in regions.Regions)
		{
			if (region.Type == RegionType.Plateau)
			{
				capOf[region.Id] = MakeCap(region, PlateauHeightM, PlateauMinSigmaKm);
			}
			else if (region.Type == RegionType.Basin)
			{
				int used = basinCount.GetValueOrDefault(region.Landmass);
				if (used >= MaxBasinsPerLandmass) continue;   // 超上限：类型保留、场不生成
				basinCount[region.Landmass] = used + 1;
				AppliedBasinsPerLandmass[region.Landmass] = used + 1;
				capOf[region.Id] = MakeCap(region, BasinDepthM, BasinMinSigmaKm);
			}
		}

		PlateauAddM = new float[n];
		BasinDipM = new float[n];
		foreach (var (regionId, cap) in capOf)
		{
			var add = regions.Regions[regionId].Type == RegionType.Plateau ? PlateauAddM : BasinDipM;
			float sigmaRad = cap.SigmaKm / SphericalFbmNoise.EarthRadiusKm;
			float capCos = MathF.Cos(cap.RadiusRad);
			for (int i = 0; i < n; i++)
			{
				if (regionOfCell[i] < 0) continue;                          // 海格不参与
				if (dirs[i].Dot(cap.Center) < capCos) continue;             // 帽外截断
				float d = MathF.Acos(Math.Clamp(dirs[i].Dot(cap.Anchor), -1f, 1f));
				float env = MathF.Exp(-(d * d) / (sigmaRad * sigmaRad));
				// 顶面起伏 ±18%：台面不是完美平顶（决策 05 §七 高原）
				float top = 1f + 0.18f * _topNoise.Sample(dirs[i]);
				add[i] += env * cap.HeightM * top;
			}
		}
	}

	Cap MakeCap(GeologicalRegions.Region region, float heightM, float minSigmaKm)
	{
		// σ = max(区域等效半径 ×1.25, 类型下限)：大面积影响场非斑点（决策 05v2 §十五）
		float equivRadiusKm = MathF.Sqrt(region.AreaKm2 / MathF.PI);
		float sigmaKm = MathF.Max(equivRadiusKm * CapRadiusFactor, minSigmaKm);
		// 帽角半径 = 质心到区域最远格的估计（等效圆近似）+ 2σ 余量
		float radiusRad = (equivRadiusKm + 2f * sigmaKm) / SphericalFbmNoise.EarthRadiusKm + 0.01f;
		return new Cap(region.Seed, sigmaKm, radiusRad, heightM);
	}

	readonly record struct Cap(Vector3 Anchor, float SigmaKm, float RadiusRad, float HeightM)
	{
		public Vector3 Center => Anchor;   // 预筛中心 = 锚（帽以锚为心）
	}
}
