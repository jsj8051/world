using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;

namespace World.WorldGen;

// 世界生成空间 · 海陆结构连续场（阶段 1 影响场 + 阶段 2 轮廓细化，决策 02 §2/§3）：
//   ContinentInfluenceField = Σmax 锚点影响（椭圆衰减 × 权重）——加权 Voronoi 的连续版，
//     AnchorAt(dir) = 影响最大锚点（连续场内部塑形判据；地图量 = 陆块连通分量）。
//   LandSeaField = influence + Large(低频) + Medium(中频×coastComplexity) + Small(高频×coastComplexity)，
//     采样坐标先过域扭曲（阶段 2 的海岸弯曲）。**只管海陆结构，不生成高度**（决策 02 §0）。
//     连续场口径：> 0 = 陆；精确海陆比由离散层分位校准（H3LandSeaProjector，"目标占比"滑块的承接）。
// 确定性红线：同种子同方向逐位同（噪声种子经 DeterministicRandom 固定次序派生：W→low→med→small）。
/// <summary>
/// 大陆影响场：逐点 = 各锚点「椭圆角距衰减 × 权重」的最大值；携带归属（加权 Voronoi）。
/// </summary>
public sealed class ContinentInfluenceField
{
	readonly ContinentLayout _layout;

	public ContinentInfluenceField(ContinentLayout layout) =>
		_layout = layout ?? throw new ArgumentNullException(nameof(layout));

	public ContinentLayout Layout => _layout;

	/// <summary>影响值 ∈[0, ~maxWeight]（锚点中心 ≈ weight，影响半径外 → 0）。</summary>
	public float Sample(Vector3 dir) => Eval(dir, out _);

	/// <summary>影响值 + 归属锚点下标（归属 = 归一化 smooth argmax）。</summary>
	public float SampleWithAnchor(Vector3 dir, out int anchorIndex) => Eval(dir, out anchorIndex);

	/// <summary>归属锚点（平滑度最大者；布局非空 ⇒ 恒有归属）。</summary>
	public int AnchorAt(Vector3 dir) { Eval(dir, out int a); return a; }

	// 影响值 = max(weight × smoothstep)（强度语义：锚点权重高 = 势力强）。
	// 归属 = argmax smoothstep（**归一化**：自身中心恒 1、他锚 < 1）——若用未归一化影响判归属，
	// 长条大邻的尾巴能覆盖邻居锚点中心 ⇒ 该锚点失去全部领土（实测踩坑：Stretch 2.2 + 间距 0.4 rad
	// 时邻居尾部 ≈0.8 > 远端锚点的自占份额）。归一化后每个锚点中心恒归属自己（Voronoi site 保证）。
	float Eval(Vector3 dir, out int best)
	{
		best = 0;
		float bestInf = -1f, bestSite = -1f;
		var anchors = _layout.Anchors;
		for (int i = 0; i < anchors.Length; i++)
		{
			var a = anchors[i];
			// 椭圆角距：p−c 的切平面分量投到 U/V 轴、按拉伸缩小 ⇒ 长轴方向影响更远
			Vector3 delta = dir - a.Dir * dir.Dot(a.Dir);          // p 在 c 切平面的投影
			float du = delta.Dot(a.AxisU) / a.StretchU;
			float dv = delta.Dot(a.AxisV) / a.StretchV;
			float dRad = MathF.Sqrt(du * du + dv * dv);            // 有效角距（rad）
			float rRad = a.RadiusKm / SphericalFbmNoise.EarthRadiusKm;
			float t = Math.Clamp(1f - dRad / rRad, 0f, 1f);
			float smooth = t * t * (3f - 2f * t);                  // smoothstep 衰减（边缘平滑归零）
			float inf = a.Weight * smooth;
			if (inf > bestInf) bestInf = inf;
			if (smooth > bestSite) { bestSite = smooth; best = i; }
		}
		return bestInf;
	}
}

/// <summary>
/// 海陆结构场（连续）：> 0 = 陆。影响场 + 域扭曲 + 大/中/小三尺度轮廓调制；
/// 中/小尺度幅度按归属锚点的 CoastComplexity 调制（每个大陆自己的海岸性格）。
/// 挂进 SphericalField 族 ⇒ H3TerrainSampler 的多点采样直接可用。
/// </summary>
public sealed class LandSeaField : SphericalField
{
	public ContinentInfluenceField Influence { get; }
	public LandSeaParams Params { get; }

	readonly SphericalFbmNoise _w1, _w2, _w3;   // 域扭曲三路
	readonly SphericalFbmNoise _low, _med, _small;

	public LandSeaField(ContinentLayout layout, LandSeaParams p)
	{
		Influence = new ContinentInfluenceField(layout);
		Params = p;
		var rnd = new DeterministicRandom(p.Seed);
		_w1 = new SphericalFbmNoise(rnd.Next(), p.WarpWavelengthKm, p.WarpOctaves);
		_w2 = new SphericalFbmNoise(rnd.Next(), p.WarpWavelengthKm, p.WarpOctaves);
		_w3 = new SphericalFbmNoise(rnd.Next(), p.WarpWavelengthKm, p.WarpOctaves);
		_low = new SphericalFbmNoise(rnd.Next(), p.LowWavelengthKm, p.LowOctaves);
		_med = new SphericalFbmNoise(rnd.Next(), p.MediumWavelengthKm, 3);
		_small = new SphericalFbmNoise(rnd.Next(), p.SmallWavelengthKm, 2);
	}

	/// <summary>海陆标量场（无纲量）：> 0 = 陆，幅值 = 距海岸的"深度"（离散层映射可见海拔用）。</summary>
	public override float Sample(Vector3 dir)
	{
		var p = Params;
		Vector3 d = SampleWarpDir(dir);

		float v = Influence.SampleWithAnchor(d, out int a);
		var anchor = Influence.Layout.Anchors[a];
		v += p.LowAmplitude * _low.Sample(d);                                 // Large：大陆整体形状
		v += p.MediumAmplitude * anchor.CoastComplexity * _med.Sample(d);     // Medium：半岛 / 海湾
		v += p.SmallAmplitude * anchor.CoastComplexity * _small.Sample(d);    // Small：海岸线细节
		return v;
	}

	/// <summary>扭曲坐标查询（域扭曲后的采样方向）：大陆归属等"同一扭曲系"的下游判据共用——
	/// 分界弯曲与海岸线同源，不再出现未扭曲 Voronoi 的数学圆弧（决策 03 v2 §九：先连续场再采样）。</summary>
	public Vector3 SampleWarpDir(Vector3 dir)
	{
		var p = Params;
		if (p.WarpAmplitudeKm <= 0f) return dir;
		var w = new Vector3(_w1.Sample(dir), _w2.Sample(dir), _w3.Sample(dir));
		return (dir + w * (p.WarpAmplitudeKm / SphericalFbmNoise.EarthRadiusKm)).Normalized();
	}
}

/// <summary>海陆结构场参数（框架期独立小表；接线面板/JSON 走后续批次）。</summary>
public sealed class LandSeaParams
{
	public int Seed = 42;

	// ── 域扭曲（阶段 2 海岸弯曲）──
	public float WarpWavelengthKm = 3000f;
	public int WarpOctaves = 3;
	public float WarpAmplitudeKm = 600f;

	// ── 三尺度轮廓调制（阶段 2：Large=大陆整体形状 / Medium=半岛海湾 / Small=海岸细节）──
	public float LowWavelengthKm = 5000f;
	public int LowOctaves = 2;
	public float LowAmplitude = 0.35f;

	public float MediumWavelengthKm = 1200f;
	public float MediumAmplitude = 0.25f;

	public float SmallWavelengthKm = 300f;
	public float SmallAmplitude = 0.12f;
}
