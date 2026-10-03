using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 雪线渐变叠加（决策 04 v2 §二）：硬阈值 2800m → smoothstep(局部雪线 ±250m)——
//   局部雪线 = 2800 + 气候噪声 ±300m（真实世界雪线随纬度/湿度起伏）。
//   属**表现层**：必须在最终高度之后求值（先雪线后 detail 会再度整脊白化）；
//   渲染器把分档色向雪白 lerp(alpha)。场景色带采纳走后续批次（当前两处批量渲染器已接）。
/// <summary>
/// 渐变雪线：alpha ∈[0,1]（0 = 无雪、1 = 全雪），含局部雪线噪声。
/// </summary>
public sealed class SnowOverlay
{
	public const float BaseSnowlineM = 2800f;
	public const float LocalSpreadM = 300f;    // 局部雪线起伏
	public const float BandM = 250f;           // smoothstep 半宽

	readonly SphericalFbmNoise _climate;

	public SnowOverlay(int seed)
	{
		var rnd = new DeterministicRandom(seed ^ 0x5E0B);
		_climate = new SphericalFbmNoise(rnd.Next(), 2500f, 2);
	}

	/// <summary>积雪 alpha（连续，同海拔不同位置可不同——局部雪线）。</summary>
	public float Alpha(float elevationM, Vector3 dir)
	{
		float localSnowline = BaseSnowlineM + LocalSpreadM * _climate.Sample(dir);
		float t = Math.Clamp((elevationM - (localSnowline - BandM)) / (2f * BandM), 0f, 1f);
		return t * t * (3f - 2f * t);
	}
}
