using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;

namespace World.Logic;

// 世界生成空间 · 地表状态解析器（决策 07 步骤③——唯一的海陆口径）：
//   此前"是不是陆"有三套实现（多点掩码 / 中心点表达式 / 逐像素渲染）——海岸线上互不相同，
//   海岸山脉出现无规律凹陷。本类收拢为**唯一实现**：所有逻辑（山脉海洋表达式、合成器、
//   测试）从这里拿海陆判断，不再各自写 isLandByXxx。
// 两阶段语义（决策 07：生成依据 vs 世界事实）：
//   Placement 阶段口（RawAt/OceanFactorAt/BathymetryAt）= **地貌生成依据**——特征抬升
//     之前的状态，供 Morphology 使用；不得进入水文/气候/生态。
//   Final 阶段（FinalLandMask 等）由 FinalGeography 在最终高度之后派生——世界事实。
/// <summary>
/// 地表状态解析器：海陆场 raw / 海洋表达式 / 深海剖面的唯一出处。
/// </summary>
public sealed class SurfaceResolver
{
	public LandSeaField Field { get; }
	public float SeaThreshold { get; }
	public float SeaSpread { get; }

	public SurfaceResolver(LandSeaField field, float seaThreshold, float seaSpread)
	{
		Field = field ?? throw new ArgumentNullException(nameof(field));
		SeaThreshold = seaThreshold;
		SeaSpread = MathF.Max(seaSpread, 1e-4f);
	}

	/// <summary>海陆场原值（连续；> SeaThreshold = 放置期陆地）。</summary>
	public float RawAt(Vector3 dir) => Field.Sample(dir);

	/// <summary>放置期陆判（单点口径；掩码的多点口径在投影器——两点各有用途，皆源于本场）。</summary>
	public bool IsPlacementLand(Vector3 dir) => RawAt(dir) > SeaThreshold;

	/// <summary>海洋表达式（唯一实现）：陆 = 1；海 = exp(−3t)，t = 归一化海深——
	/// 浅海保留高（海底脊/岛链可露）、深海趋零（构造衰减消失）；连续无台阶。</summary>
	public float OceanFactorAt(Vector3 dir)
	{
		float raw = RawAt(dir);
		if (raw > SeaThreshold) return 1f;
		float t = Math.Clamp((SeaThreshold - raw) / SeaSpread, 0f, 1f);
		return MathF.Exp(-3f * t);
	}

	/// <summary>深海剖面（米）：raw 相对阈值的超出量映射（与投影器逐格海侧同源公式）。</summary>
	public float BathymetryAt(Vector3 dir)
	{
		float raw = RawAt(dir);
		if (raw > SeaThreshold) return 0f;
		float t = Math.Clamp((SeaThreshold - raw) / SeaSpread, 0f, 1f);
		return -30f - 3200f * MathF.Pow(t, 1.2f);
	}
}
