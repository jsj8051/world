using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;

namespace World.Logic;

/// <summary>
/// 球面域扭曲：用一层低频噪声生成切向位移，把采样方向 <c>p</c> 推到
/// <c>p' = normalize(p + A·tangentNoise(p))</c>，再在 <c>p'</c> 处取地形场
/// ⇒ 海岸线 / 大陆轮廓 / 山带走向不再沿 Voronoi 边界走直线，而是出现弯曲、湾岬与岛链。
/// 位移必须去掉径向分量（否则归一化会吃掉实际幅度），长度限制在 1 以内 ⇒ 不产生自交 / 折叠。
/// x / y / z 三个分量各用独立种子流，球面天然无缝。
/// 纯函数：同种子同方向逐位一致，不消耗 rng、与调用顺序无关。
/// </summary>
public static class DomainWarpOps
{
	/// <summary>一次域扭曲的实例（三张独立的球面 fBm 场 + 已换算成弧度的幅度）。</summary>
	public sealed class Warp
	{
		readonly SphericalFbmNoise _x;
		readonly SphericalFbmNoise _y;
		readonly SphericalFbmNoise _z;
		readonly float _amplitudeRad;

		internal Warp(SphericalFbmNoise x, SphericalFbmNoise y, SphericalFbmNoise z, float amplitudeRad)
		{
			_x = x;
			_y = y;
			_z = z;
			_amplitudeRad = amplitudeRad;
		}

		/// <summary>把单位方向推到扭曲后的方向；幅度 ≤ 0 时原样返回（关断）。</summary>
		public Vector3 Apply(Vector3 dir)
		{
			Vector3 p = dir.Normalized();
			if (!(_amplitudeRad > 0f)) return p;

			Vector3 offset = new(_x.Sample(p), _y.Sample(p), _z.Sample(p));
			offset -= p * offset.Dot(p);                 // 只留切向
			float len = offset.Length();
			if (len > 1f) offset /= len;                 // 限制幅度，防折叠
			return (p + offset * _amplitudeRad).Normalized();
		}
	}

	/// <summary>建一次域扭曲。三个 salt 必须互不相同（见 <see cref="SeedDerivation"/>）。</summary>
	/// <param name="amplitudeKm">位移幅度（km；0 = 关断）。</param>
	/// <param name="wavelengthKm">位移场的基波长（km）。</param>
	/// <param name="octaves">倍频层数（调用方按 <see cref="NoiseSampling"/> 截断）。</param>
	public static Warp Create(int seed, int saltX, int saltY, int saltZ, float amplitudeKm,
		float wavelengthKm, int octaves)
	{
		if (!(wavelengthKm > 0f))
			throw new ArgumentOutOfRangeException(nameof(wavelengthKm), "域扭曲的波长须为正（km）");

		float ampKm = MathF.Max(0f, amplitudeKm);
		float ampRad = ampKm / SphericalFbmNoise.WorldRadiusKm;
		return new Warp(
			new SphericalFbmNoise(SeedDerivation.Derive(seed, saltX), wavelengthKm, octaves),
			new SphericalFbmNoise(SeedDerivation.Derive(seed, saltY), wavelengthKm, octaves),
			new SphericalFbmNoise(SeedDerivation.Derive(seed, saltZ), wavelengthKm, octaves),
			ampRad);
	}
}
