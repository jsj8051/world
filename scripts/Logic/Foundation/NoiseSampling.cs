using System;

namespace World.Logic;

/// <summary>
/// 噪声采样下限守卫：按格距把倍频层数截到「采得到」的范围。
///
/// 判据：任何噪声场最细的倍频层必须 ≥ 3 倍邻格中心距（res4 ≈ 2.469 km ⇒ ≥ 7.41 km），
/// 更细的层在六边形格上采不到，只会沿格轴混叠成条纹。
/// 普通 fBm 第 o 层波长 = 基波长 / 2^(o−1)；ridged 脊距 ≈ 基波长 / 2^o（脊距 ≥ 3 格 ⇒ 最细层 ≥ 6 格）。
/// 本类只做纯计算：是否警告由调用方决定，不打印、不改 spec。
/// </summary>
public static class NoiseSampling
{
	/// <summary>在给定基波长与最短可采波长下，普通 fBm 最多能叠几层（≥ 1）。</summary>
	public static int SafeOctaves(float baseWavelengthKm, double minWavelengthKm)
	{
		if (!(baseWavelengthKm > 0f) || !(minWavelengthKm > 0.0)) return 1;
		int octaves = 1;
		while (octaves < 12 && baseWavelengthKm / (1 << octaves) >= minWavelengthKm) octaves++;
		return octaves;
	}

	/// <summary>同上，但把倍频层数截到请求值以内（请求合法时原样返回）。</summary>
	public static int ClampOctaves(int requested, float baseWavelengthKm, double minWavelengthKm)
	{
		int safe = SafeOctaves(baseWavelengthKm, minWavelengthKm);
		return Math.Clamp(requested, 1, safe);
	}

	/// <summary>ridged 的安全层数（脊距 ≥ 3 × 格距）。</summary>
	public static int SafeRidgedOctaves(float baseWavelengthKm, double cellKm)
	{
		if (!(cellKm > 0.0)) return 1;
		return SafeOctaves(baseWavelengthKm, 6.0 * cellKm);
	}

	/// <summary>同上，但把层数截到请求值以内。</summary>
	public static int ClampRidgedOctaves(int requested, float baseWavelengthKm, double cellKm)
	{
		int safe = SafeRidgedOctaves(baseWavelengthKm, cellKm);
		return Math.Clamp(requested, 1, safe);
	}
}
