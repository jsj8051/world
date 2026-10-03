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
// 语义（决策 07 步骤⑤——**目标绝对高度**替代增量）：高原帽不再"额外 +1150m"，
//   而是"这里倾向 2050m 台地"（PlateauTargetM + 顶面 ±12% 起伏 = Flatness 语义）；
//   盆地不再"下挖 700m"，而是"这里倾向 350m 洼地"。合成 = lerp(当前, Target, Influence)
//   ——高度语义单一来源（调 Target 不再牵动基座）。
//   HIGHLAND/PLAIN/COASTAL/RIFT 暂不生成大尺度场（基线 + 细节噪声呈现；裂谷条带留后续批次）。
// 性能：与 MountainSkeleton 同构的角帽预筛——每区域一个帽，帽外截断。
/// <summary>
/// 区域地貌场：PLATEAU/盆地目标场（influence + target 绝对高，ITerrainField 语义）。
/// </summary>
public sealed class RegionalLandforms
{
	/// <summary>逐格高原影响度 ∈[0,1]（PlateauAt 的 H3 采样）。</summary>
	public float[] PlateauInfluence { get; private set; } = Array.Empty<float>();
	/// <summary>逐格高原目标绝对高度（米；影响度 0 处无意义）。</summary>
	public float[] PlateauTargetM { get; private set; } = Array.Empty<float>();
	/// <summary>逐格盆地影响度 ∈[0,1]。</summary>
	public float[] BasinInfluence { get; private set; } = Array.Empty<float>();
	/// <summary>逐格盆地目标绝对高度（米）。</summary>
	public float[] BasinTargetM { get; private set; } = Array.Empty<float>();
	/// <summary>实际生成下挖场的盆地数（按陆块；每陆块上限 MaxBasinsPerLandmass——类型可为
	/// BASIN 但超上限的区域不生成场，决策 §七防内流盆地泛滥）。</summary>
	public System.Collections.Generic.Dictionary<int, int> AppliedBasinsPerLandmass { get; private set; } = new();

	// ── 场性格旋钮（地球量级；面板接线走 S5）──
	public const float PlateauTargetElevM = 2050f;    // 高原台面**目标绝对高度**（决策 07 步骤⑤）
	public const float BasinTargetElevM = 350f;       // 盆地**目标绝对高度**（洼地）
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
		var caps = new List<Cap>();
		foreach (var region in regions.Regions)
		{
			if (region.Type == RegionType.Plateau)
			{
				caps.Add(MakeCap(region, PlateauTargetElevM, PlateauMinSigmaKm, isPlateau: true));
			}
			else if (region.Type == RegionType.Basin)
			{
				int used = basinCount.GetValueOrDefault(region.Landmass);
				if (used >= MaxBasinsPerLandmass) continue;   // 超上限：类型保留、场不生成
				basinCount[region.Landmass] = used + 1;
				AppliedBasinsPerLandmass[region.Landmass] = used + 1;
				caps.Add(MakeCap(region, BasinTargetElevM, BasinMinSigmaKm, isPlateau: false));
			}
		}
		_caps = caps;

		// 逐格数组 = 连续查询口的 H3 采样（海格不参与——门控在采样处）
		PlateauInfluence = new float[n];
		PlateauTargetM = new float[n];
		BasinInfluence = new float[n];
		BasinTargetM = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (regionOfCell[i] < 0) continue;
			(PlateauInfluence[i], PlateauTargetM[i]) = PlateauAt(dirs[i]);
			(BasinInfluence[i], BasinTargetM[i]) = BasinAt(dirs[i]);
		}
	}

	List<Cap> _caps = new();

	/// <summary>连续查询（ITerrainField 组合口）：高原→盆地按序 lerp 后的等效（影响度, 目标）。
	/// 等效合成：I* = 1−(1−i1)(1−i2)，T* = (t1·i1·(1−i2) + t2·i2)/I*（顺序语义保留）。</summary>
	public (float influence, float targetM) SampleAt(Vector3 dir)
	{
		var (i1, t1) = PlateauAt(dir);
		var (i2, t2) = BasinAt(dir);
		float influence = 1f - (1f - i1) * (1f - i2);
		if (influence <= 0f) return (0f, 0f);
		float target = (t1 * i1 * (1f - i2) + t2 * i2) / influence;
		return (influence, target);
	}

	/// <summary>连续查询：高原目标场（影响度 ∈[0,1]，目标绝对高 = 台面 × 顶面起伏 ±12%）。</summary>
	public (float influence, float targetM) PlateauAt(Vector3 dir)
	{
		float union = 0f, wSum = 0f, tAcc = 0f;
		foreach (var cap in _caps)
		{
			if (!cap.IsPlateau) continue;
			if (dir.Dot(cap.Center) < MathF.Cos(cap.RadiusRad)) continue;   // 帽外截断
			float sigmaRad = cap.SigmaKm / SphericalFbmNoise.EarthRadiusKm;
			float d = MathF.Acos(Math.Clamp(dir.Dot(cap.Anchor), -1f, 1f));
			float env = MathF.Exp(-(d * d) / (sigmaRad * sigmaRad));
			if (env <= 0f) continue;
			float top = 1f + 0.12f * _topNoise.Sample(dir);   // 顶面起伏 ±12%：台面不是完美平顶
			union = union + (1f - union) * env;
			wSum += env;
			tAcc += env * cap.HeightM * top;
		}
		return union <= 0f ? (0f, 0f) : (union, tAcc / wSum);
	}

	/// <summary>连续查询：盆地目标场（洼地目标绝对高）。</summary>
	public (float influence, float targetM) BasinAt(Vector3 dir)
	{
		float union = 0f, wSum = 0f, tAcc = 0f;
		foreach (var cap in _caps)
		{
			if (cap.IsPlateau) continue;
			if (dir.Dot(cap.Center) < MathF.Cos(cap.RadiusRad)) continue;
			float sigmaRad = cap.SigmaKm / SphericalFbmNoise.EarthRadiusKm;
			float d = MathF.Acos(Math.Clamp(dir.Dot(cap.Anchor), -1f, 1f));
			float env = MathF.Exp(-(d * d) / (sigmaRad * sigmaRad));
			if (env <= 0f) continue;
			float top = 1f + 0.12f * _topNoise.Sample(dir);
			union = union + (1f - union) * env;
			wSum += env;
			tAcc += env * cap.HeightM * top;
		}
		return union <= 0f ? (0f, 0f) : (union, tAcc / wSum);
	}

	Cap MakeCap(GeologicalRegions.Region region, float heightM, float minSigmaKm, bool isPlateau)
	{
		// σ = max(区域等效半径 ×1.25, 类型下限)：大面积影响场非斑点（决策 05v2 §十五）
		float equivRadiusKm = MathF.Sqrt(region.AreaKm2 / MathF.PI);
		float sigmaKm = MathF.Max(equivRadiusKm * CapRadiusFactor, minSigmaKm);
		// 帽角半径 = 质心到区域最远格的估计（等效圆近似）+ 2σ 余量
		float radiusRad = (equivRadiusKm + 2f * sigmaKm) / SphericalFbmNoise.EarthRadiusKm + 0.01f;
		return new Cap(region.Seed, sigmaKm, radiusRad, heightM, isPlateau);
	}

	readonly record struct Cap(Vector3 Anchor, float SigmaKm, float RadiusRad, float HeightM, bool IsPlateau)
	{
		public Vector3 Center => Anchor;   // 预筛中心 = 锚（帽以锚为心）
	}
}
