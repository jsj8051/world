using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 组合层（决策原典 §六）：Elevation = ContinentalBase + Mountain + Plateau − Basin + Detail。
//   各项都是 SphericalField（连续、与 H3 无关），本类只做两件事：
//   ① **种子派生**：DeterministicRandom 按**固定次序**给各层派种子（Warp→大陆→山→高原→盆地→细节；
//      与参数无关、与未来增删无关的次序——增场只往尾部接，不挪已有层的场，开关不改变次序）；
//   ② **组合 wiring**：加权权和 + 域扭曲挂大陆基，输出**米域**海拔场（海平面 = 0 横切）。
// 初版场源全部 = 噪声（框架期海陆 = 米域 0 横切）。噪声地形线现役的「板块身份定海陆 + 地球 LUT 基线」
//   是语义升级，走迁移（噪声地形-02），不在本框架第一刀——两层并存，见设计文档。
// 值域契约（量·单位·口径）：Sample 输出米，海平面 0，山可到 +PeakAmpM、盆地可到 −BasinAmpM；
//   未做身份钳制（框架期无海陆身份）⇒ 下游（采样/水文）自带 seaLevel 口径。
/// <summary>
/// 海拔组合场（米域）：大陆基（域扭曲 fBm）+ 山脉（ridged）+ 高原 − 盆地 + 细节，加权和合成。
/// </summary>
public sealed class ElevationFieldStack
{
	public SphericalField Elevation { get; }
	public WorldGenParams Params { get; }

	public ElevationFieldStack(WorldGenParams p)
	{
		Params = p;
		var rnd = new DeterministicRandom(p.Seed);
		int seedW1 = rnd.Next(), seedW2 = rnd.Next(), seedW3 = rnd.Next();   // 域扭曲三路
		int seedCont = rnd.Next(), seedMt = rnd.Next(), seedPlateau = rnd.Next();
		int seedBasin = rnd.Next(), seedDetail = rnd.Next();

		var lac = p.Lacunarity;
		var gain = p.Gain;

		// 大陆基：低频 fBm，域扭曲后放大振幅到"大陆高原量级"（km 级波长 → 千米级起伏）
		var continent = new WarpedField(
			new FbmField(seedCont, p.ContinentWavelengthKm, p.ContinentOctaves, lac, gain),
			new FbmField(seedW1, p.WarpWavelengthKm, p.WarpOctaves, lac, gain),
			new FbmField(seedW2, p.WarpWavelengthKm, p.WarpOctaves, lac, gain),
			new FbmField(seedW3, p.WarpWavelengthKm, p.WarpOctaves, lac, gain),
			p.WarpAmplitudeKm);

		// 山脉：ridged（[0,1]）× 振幅——山脊链形态，波长一档细于大陆
		var mountain = new RidgedField(
			new FbmField(seedMt, p.MountainWavelengthKm, p.MountainOctaves, lac, gain), p.RidgePower);

		// 高原 / 盆地：中低频整形单项（高原抬升、盆地下挖；振幅各自独立）
		var plateau = new FbmField(seedPlateau, p.PlateauWavelengthKm, 2, lac, gain);
		var basin = new FbmField(seedBasin, p.BasinWavelengthKm, 2, lac, gain);
		// 盆地下挖取正瓣：max(0, n) 只在噪声正值区挖 ⇒ 盆地是离散洼地而非全球基线整体下移
		var basinPos = new PositiveField(basin);

		var detail = new FbmField(seedDetail, p.DetailWavelengthKm, p.DetailOctaves, lac, gain);

		Elevation = new WeightedSumField(
			(continent, p.ContinentAmpM),
			(mountain, p.MountainAmpM),
			(plateau, p.PlateauAmpM),
			(basinPos, -p.BasinAmpM),
			(detail, p.DetailAmpM));
	}
}

/// <summary>正瓣整形：inner ≈[-1,1] → max(0, inner) ∈[0,1]（盆地/湖泊等"只在正值区起作用"的场源）。</summary>
public sealed class PositiveField : SphericalField
{
	readonly SphericalField _inner;
	public PositiveField(SphericalField inner) => _inner = inner;
	public override float Sample(Vector3 dir) => MathF.Max(0f, _inner.Sample(dir));
}

/// <summary>
/// 世界生成空间参数（框架期独立小参数表；接 [NoiseParam] 面板与 JSON 导出走后续批次——
///噪声地形线的 TerrainNoiseParams 已随决策 08 §4.2 清退；本表亦零调用 ⇒ 整体待清退）。
/// </summary>
public sealed class WorldGenParams
{
	public int Seed = 42;

	// ── 大陆基 ──
	public float ContinentWavelengthKm = 9000f;
	public int ContinentOctaves = 4;
	public float ContinentAmpM = 3000f;

	// ── 域扭曲（挂大陆基）──
	public float WarpWavelengthKm = 4000f;
	public int WarpOctaves = 4;
	public float WarpAmplitudeKm = 900f;

	// ── 山脉 ──
	public float MountainWavelengthKm = 350f;
	public int MountainOctaves = 4;
	public float MountainAmpM = 2500f;
	public float RidgePower = 2f;

	// ── 高原 / 盆地 ──
	public float PlateauWavelengthKm = 2500f;
	public float PlateauAmpM = 1200f;
	public float BasinWavelengthKm = 3000f;
	public float BasinAmpM = 1500f;

	// ── 细节 ──
	public float DetailWavelengthKm = 120f;
	public int DetailOctaves = 3;
	public float DetailAmpM = 250f;

	// ── fBm 通用 ──
	public float Lacunarity = 2f;
	public float Gain = 0.5f;
}
