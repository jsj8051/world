using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 连续场层（决策原典 docs/newdecision/world.md「World Generation Space」）：
//   场 = 空间函数 Sample(单位方向)——**不建二维数组、不依赖 H3**；H3 只是后继采样（离散化）的结果。
//   输入口径 = 球面单位方向向量（原典示意 Field(x,y)，落地球面化：3D 噪声直采 |p|=1，无接缝无极点畸变）。
// 红线（沿噪声地形线纪律）：同种子同输入**逐位同**；Sample 无状态不耗 rng；
//   层种子由 ElevationFieldStack 经 DeterministicRandom 按**固定次序**派生（开关/增删场不挪别的层的场）。
// 组合子风格：基本场（FbmField）+ 整形（RidgedField）+ 域扭曲（WarpedField）+ 加权和（WeightedSumField），
//   任意嵌套——决策原典的「大陆场/山脉场/盆地场… → Continuous Height」全部是这一族的具体配置。
/// <summary>
/// 连续球面标量场（世界生成空间的"空间函数"）：输入球面单位方向，输出场值。
/// 值域口径由子类注明（无量纲 ≈[-1,1] / [0,1] / 米）；海拔米域 = ElevationFieldStack。
/// </summary>
public abstract class SphericalField
{
	/// <summary>采样空间函数：任意球面位置的场值（与 H3 分辨率无关——分辨率解耦即本口本身）。</summary>
	public abstract float Sample(Vector3 dir);

	/// <summary>批量采样（顺序与 dirs 对齐；逐位 = 逐点循环 Sample，无并行重排 ⇒ 确定性）。</summary>
	public float[] SampleAll(Vector3[] dirs)
	{
		var r = new float[dirs.Length];
		for (int i = 0; i < dirs.Length; i++) r[i] = Sample(dirs[i]);
		return r;
	}
}

/// <summary>fBm 基本场：SphericalFbmNoise 的场包装，值域 ≈[-1,1]（振幅和截断）。</summary>
public sealed class FbmField : SphericalField
{
	readonly SphericalFbmNoise _noise;

	public FbmField(int seed, float baseWavelengthKm, int octaves,
		float lacunarity = 2f, float gain = 0.5f) =>
		_noise = new SphericalFbmNoise(seed, baseWavelengthKm, octaves, lacunarity, gain);

	public override float Sample(Vector3 dir) => _noise.Sample(dir);
}

/// <summary>ridged 整形场：inner（≈[-1,1]）→ (1−|inner|)^power ∈[0,1]——山脊链形态（脊=1，谷=0）。</summary>
public sealed class RidgedField : SphericalField
{
	readonly SphericalField _inner;
	readonly float _power;

	public RidgedField(SphericalField inner, float power) { _inner = inner; _power = power; }

	public override float Sample(Vector3 dir)
	{
		float n = _inner.Sample(dir);
		return MathF.Pow(1f - MathF.Abs(n), _power);
	}
}

/// <summary>域扭曲组合子：Sample(dir) = inner.Sample(normalize(dir + amp·w(dir)))，
/// w = 三路矢量场（各 ≈[-1,1]），amp 以公里标定（弧长口径 → 弧度）。amp=0 时逐位等价于 inner 直采。</summary>
public sealed class WarpedField : SphericalField
{
	readonly SphericalField _inner, _wx, _wy, _wz;
	readonly float _ampRad;

	public WarpedField(SphericalField inner, SphericalField warpX, SphericalField warpY,
		SphericalField warpZ, float amplitudeKm) =>
		(_inner, _wx, _wy, _wz, _ampRad) =
		(inner, warpX, warpY, warpZ, amplitudeKm / SphericalFbmNoise.EarthRadiusKm);

	public override float Sample(Vector3 dir)
	{
		if (_ampRad == 0f) return _inner.Sample(dir);   // 幅度 0：逐位退化直采（不归一化——归一化的末位浮点差会污染采样方向）
		var w = new Vector3(_wx.Sample(dir), _wy.Sample(dir), _wz.Sample(dir));
		return _inner.Sample((dir + w * _ampRad).Normalized());
	}
}

/// <summary>加权和组合：Σ wᵢ·fᵢ(dir)——负权重即减法（原典「− Basin」）；权重 0 的项直接跳过。
/// 线性组合 ⇒ 采样次序固定为数组序，逐位确定。</summary>
public sealed class WeightedSumField : SphericalField
{
	readonly (SphericalField field, float weight)[] _terms;

	public WeightedSumField(params (SphericalField field, float weight)[] terms)
	{
		if (terms == null || terms.Length == 0)
			throw new ArgumentException("WeightedSumField 至少需要一个组合项");
		_terms = terms;
	}

	public override float Sample(Vector3 dir)
	{
		float sum = 0f;
		for (int i = 0; i < _terms.Length; i++)
		{
			float w = _terms[i].weight;
			if (w == 0f) continue;
			sum += w * _terms[i].field.Sample(dir);
		}
		return sum;
	}
}
