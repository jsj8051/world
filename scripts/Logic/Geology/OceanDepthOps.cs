using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // TerrainSpec（海底旋钮）+ BoundaryRole（上盘 / 下盘）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;           // SphericalFbmNoise（深海丘陵 / 大陆架 / 岛弧串噪声）

namespace World.Logic;

/// <summary>
/// 海底深度算子：给**洋壳**的格算出海底深度（负值），另给出岛弧成岛的附加高度。
/// 陆壳的格一律不参与——它们由 ① 的陆面（基线 + 三档 + 山脉）定。
///
/// 三条来源互补（都吃 <see cref="PlateBoundaryOps"/> 已产出的边界场）：
/// · **洋中脊**：到大洋里**张裂边界**的距离 = 洋壳年龄 ⇒ 深度 **∝ √年龄**，
///   脊顶约 −2700 m、最老洋底约 −5400 m；脊顶再按张裂强度隆起一道；
/// · **大陆架 / 大陆坡**：按**离岸距离**，近岸一段浅（−30 → −180 m），
///   出架后顺着大陆坡下到深海；被动边缘宽、俯冲带一侧窄（由汇聚强度调制）；
/// · **岛弧**：**上盘**（<see cref="BoundaryRole.Arc"/>）的洋壳在海沟后方一条窄带上隆起成海底脊；
///   沿带噪声的峰处再叠一层高度 ⇒ 抬过海面即成**一串岛**（弧形岛链）。
///
/// 以及 **海沟**：**俯冲侧**（<see cref="BoundaryRole.Slab"/>）的洋壳、紧贴边界挖一道窄而深的槽——
/// 「先平滑海底、再挖海沟」，故海沟不会被平滑抹平。
///
/// 深度只在此处受钳制：上界 <see cref="TerrainSpec.OceanShallowestM"/>（最浅的海）、
/// 下界 <see cref="TerrainSpec.OceanDeepestM"/>（最深的海）。
/// </summary>
public static class OceanDepthOps
{
	/// <summary>活动边界的判定阈值（法向相对速率，无量纲）：|motion| 超过它才算一条汇聚 / 张裂边界。</summary>
	const float ActiveMotion = 0.25f;

	/// <summary>强度归一尺度：<c>strength = clamp(|motion| / 本值, 0, 1)</c>。</summary>
	const float MotionScale = 0.9f;

	/// <summary>平滑权重 ≈ 6/7 —— 等价于「自身 + 6 个邻居 取平均」（球面网格上九成以上的格是 6 邻居）。</summary>
	const float BlurWeight = 6f / 7f;

	// 海底噪声的波长 / 层数：这些是**纹理尺度**（形状常量），与 `TerrainManager.HopToEdgeRatio` 同类，
	// 不进 `TerrainSpec`（那里放的是能改画面的物理旋钮：深度、宽度、高度）。
	const float SeaLargeWavelengthKm = 7680f;
	const int SeaLargeOctaves = 5;
	const float SeaFineWavelengthKm = 1707f;
	const int SeaFineOctaves = 5;
	const float ShelfNoiseWavelengthKm = 9600f;
	const int ShelfNoiseOctaves = 3;
	const float ArcBeadWavelengthKm = 741f;
	const float ArcIsleWavelengthKm = 1958f;
	const int ArcIsleOctaves = 4;

	/// <summary>结果。<c>DepthM</c>：洋壳格的海底深度（恒 ≤ <see cref="TerrainSpec.OceanShallowestM"/>；陆壳格不用）；
	/// <c>ArcIslandM</c>：岛弧成岛的附加高度（≥ 0；其余格 = 0）。</summary>
	public readonly struct Field
	{
		public readonly float[] DepthM;
		public readonly float[] ArcIslandM;

		internal Field(float[] depthM, float[] arcIslandM)
		{
			DepthM = depthM;
			ArcIslandM = arcIslandM;
		}
	}

	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">本阶段配置（海底段旋钮）。</param>
	/// <param name="plateOfCell">逐格板块号（判边界格用）。</param>
	/// <param name="continental">逐格是否陆壳。</param>
	/// <param name="land">逐格是否**露出水面**（陆壳 且 合成高度 &gt; 海面）——离岸距离的源。</param>
	/// <param name="motion">逐格边界法向相对速率（&lt; 0 汇聚 / &gt; 0 张裂）。</param>
	/// <param name="role">逐格汇聚角色（见 <see cref="BoundaryRole"/>）。</param>
	public static Field Build(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell,
		bool[] continental, bool[] land, float[] motion, BoundaryRole[] role)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (continental == null) throw new ArgumentNullException(nameof(continental));
		if (land == null) throw new ArgumentNullException(nameof(land));
		if (motion == null) throw new ArgumentNullException(nameof(motion));
		if (role == null) throw new ArgumentNullException(nameof(role));

		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		var neighbors = ball.CellNeighbors;

		// ── 源集：活动边界格（汇聚 / 张裂）──
		var convSources = new List<int>();
		var divSources = new List<int>();
		for (int i = 0; i < n; i++)
		{
			int p = plateOfCell[i];
			int[] nb = neighbors[i];
			bool boundary = false;
			for (int k = 0; k < nb.Length; k++)
				if (plateOfCell[nb[k]] != p) { boundary = true; break; }
			if (!boundary) continue;

			float m = motion[i];
			if (m < -ActiveMotion) convSources.Add(i);
			else if (m > ActiveMotion) divSources.Add(i);
		}

		// ── 三个真实距离场（km）：到汇聚边界 / 到大洋张裂边界 / 到陆地 ──
		// 截断半径 = 该场最远的有效尺度（带之外 / 年龄满值之后都不再改变结果），越远越省。
		var conv = DistanceFieldOps.Build(ball, convSources,
			i => MathF.Min(1f, -motion[i] / MotionScale),
			4f * spec.ActiveMarginBandKm);

		// 洋中脊只在大洋里：大陆里的张裂是裂谷，不算洋中脊
		var ridgeSources = new List<int>();
		foreach (int i in divSources)
			if (!continental[i]) ridgeSources.Add(i);
		var ridge = DistanceFieldOps.Build(ball, ridgeSources,
			i => MathF.Min(1f, motion[i] / MotionScale),
			MathF.Max(spec.RidgeAgeSpanKm, 4f * spec.RidgeBandKm));

		var coastSources = new List<int>();
		for (int i = 0; i < n; i++)
			if (land[i]) coastSources.Add(i);
		var coast = DistanceFieldOps.Build(ball, coastSources, _ => 0f,
			spec.ShelfMaxWidthKm + spec.SlopeSpanKm);

		// ── 噪声场 ──
		var seaLarge = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_Sea), SeaLargeWavelengthKm, SeaLargeOctaves);
		var seaFine = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_SeaFine), SeaFineWavelengthKm, SeaFineOctaves);
		var shelfNoise = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_Shelf), ShelfNoiseWavelengthKm, ShelfNoiseOctaves);
		var beadNoise = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_ArcBead), ArcBeadWavelengthKm, 1);
		var isleNoise = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_ArcIsle), ArcIsleWavelengthKm, ArcIsleOctaves);

		// ── 逐格海底剖面（只算洋壳；陆壳格留 0，下面也当"岸"用）──
		var depth = new float[n];
		var arcIsland = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i]) return;
			Vector3 dir = dirs[i];

			// ① 洋壳年龄 → 深度剖面（∝ √年龄）＋ 脊顶隆起 ＋ 深海丘陵
			float rs = ridge.Strength[i];
			float age = MathF.Min(1f, ridge.DistKm[i] / spec.RidgeAgeSpanKm);
			float d = spec.RidgeCrestDepthM - spec.RidgeAgeDepthM * MathF.Sqrt(age)
				+ spec.RidgeCrestRiseM * rs * Band(ridge.DistKm[i], spec.RidgeBandKm);
			d += spec.AbyssHillLargeM * seaLarge.Sample(dir)
				+ spec.AbyssHillFineM * seaFine.Sample(dir);

			// ② 大陆架 / 大陆坡：离岸一段浅，出架后顺着坡下到深海
			float active = conv.Strength[i] * Band(conv.DistKm[i], spec.ActiveMarginBandKm);
			float shelfWidth = (spec.ShelfMinWidthKm
					+ (spec.ShelfMaxWidthKm - spec.ShelfMinWidthKm) * (1f - active)
						* (0.35f + 0.65f * Smoothstep(-0.4f, 0.5f, shelfNoise.Sample(dir))))
				* (coast.DistKm[i] < spec.MarginWidenKm ? 1.3f : 0.7f);
			if (shelfWidth < 1f) shelfWidth = 1f;

			float dc = coast.DistKm[i];
			float shelf = dc < shelfWidth
				? spec.ShelfShallowestM + (spec.ShelfDeepestM - spec.ShelfShallowestM) * (dc / shelfWidth)
				: spec.ShelfDeepestM + (spec.SlopeBottomM - spec.ShelfDeepestM)
					* Smoothstep(0f, spec.SlopeSpanKm, dc - shelfWidth);
			d = MathF.Max(d, shelf);

			// ③ 岛弧：上盘的洋壳在海沟后方一条窄带上隆起成海底脊
			float cs = conv.Strength[i];
			float cd = conv.DistKm[i];
			float arcBand = Band(cd - spec.ArcOffsetKm, spec.ArcBandKm);
			bool overriding = role[i] == BoundaryRole.Arc;
			if (overriding)
				d = MathF.Max(d, spec.ArcCrestDepthM - spec.ArcRiseM * (1f - cs * arcBand));

			depth[i] = MathF.Min(spec.OceanShallowestM, d);

			// ④ 岛弧成岛：沿带的 bead 峰处再叠一层高度 ⇒ 抬过海面即成一串岛
			if (overriding)
			{
				float beads = Smoothstep(-0.35f, 0.45f,
					beadNoise.Sample(dir) + 0.35f * isleNoise.Sample(dir));
				arcIsland[i] = MathF.Max(0f, spec.ArcIslandPeakM * cs * arcBand * beads);
			}
		});

		// ── 平滑海底，去掉距离场的折线痕迹；海沟**在平滑之后**再挖（窄而深，不被抹平）──
		FieldBlurOps.Blur(ball, depth, BlurWeight, spec.OceanSmoothPasses);

		var trench = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i]) return;
			if (role[i] != BoundaryRole.Slab) return;
			trench[i] = (spec.TrenchDepthM + spec.TrenchConvDepthM * conv.Strength[i])
				* conv.Strength[i]
				* Band(conv.DistKm[i], spec.TrenchBandKm)
				* Smoothstep(spec.TrenchMinCoastKm, spec.TrenchFullCoastKm, coast.DistKm[i]);
		});
		FieldBlurOps.Blur(ball, trench, BlurWeight, spec.TrenchSmoothPasses);

		for (int i = 0; i < n; i++)
		{
			if (continental[i]) continue;
			depth[i] = Math.Clamp(depth[i] - trench[i], spec.OceanDeepestM, spec.OceanShallowestM);
		}

		return new Field(depth, arcIsland);
	}

	/// <summary>高斯带：<c>exp(−(d/w)²)</c>；<c>|d/w| &gt; 4</c> ⇒ 0（远场截断）。<c>w ≤ 0</c> ⇒ 0（关断）。</summary>
	static float Band(float d, float w)
	{
		if (!(w > 0f)) return 0f;
		float t = d / w;
		if (t > 4f || t < -4f) return 0f;
		return MathF.Exp(-t * t);
	}

	/// <summary>平滑阶跃：<c>x ≤ a</c> ⇒ 0、<c>x ≥ b</c> ⇒ 1，中间二阶连续。<c>b ≤ a</c> ⇒ 硬阶跃。</summary>
	static float Smoothstep(float a, float b, float x)
	{
		if (!(b > a)) return x >= b ? 1f : 0f;
		float t = (x - a) / (b - a);
		if (t <= 0f) return 0f;
		if (t >= 1f) return 1f;
		return t * t * (3f - 2f * t);
	}
}
