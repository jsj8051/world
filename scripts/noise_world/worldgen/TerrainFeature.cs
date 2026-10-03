using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 特征抽象层（决策 06「Field + Feature + Morphology」）：
//   **Field 回答"哪里容易发生什么"（构造/环境背景），Feature 回答"具体长成什么样"
//   （离散地质对象，自带尺度与形态），Morphology 是该对象的生长规则**——
//   三者彻底分离后，加盆地/火山/峡谷/断层不再动世界生成总架构（决策 06 §十五）。
// 本文件只放三种东西共用的最小抽象：尺度三元组、特征基类、高度贡献口。
// 各形态生成器（山脉中心线+剖面+支脉树 / 盆地帽 / 火山锥…）分属自己的实现文件——
//   **不存在万能地形区域公式**，不同特征用不同参数化（决策 06 §十一）。
/// <summary>特征尺度三元组：纵（沿走向）/ 横（垂直走向）/ 垂（高度幅）——每种特征自己填。</summary>
public readonly struct Scale3
{
	public readonly float LongitudinalKm;   // 山脉 1000-3000 / 山间盆地 50-300 / 火山 5-30（决策 06 §二表）
	public readonly float TransversalKm;    // 山脉 100-400 / 盆地 20-100 / 峡谷 1-20
	public readonly float VerticalM;        // +2500（山）/ −800（盆地）/ −500（峡谷）…

	public Scale3(float longitudinalKm, float transversalKm, float verticalM)
	{
		LongitudinalKm = longitudinalKm;
		TransversalKm = transversalKm;
		VerticalM = verticalM;
	}
}

/// <summary>
/// 地形特征基类：离散地质对象的公共身份（锚点/走向/强度/尺度 + 采样预筛帽）。
/// 形态数据（中心线/剖面/支脉树/锥体…）由各 Morphology 实现自有。
/// </summary>
public abstract class TerrainFeature
{
	/// <summary>构造核心（球面单位方向）。</summary>
	public Vector3 Anchor;
	/// <summary>主走向（锚点切平面内方位角，rad；点状特征如火山不使用）。</summary>
	public float OrientationRad;
	/// <summary>构造强度读数 ∈[0,1]（TectonicField 在锚点处的取值——"为什么长在这里"）。</summary>
	public float Intensity;
	/// <summary>特征尺度（纵/横/垂）。</summary>
	public Scale3 Scale;
	/// <summary>采样预筛帽：中心方向与角半径（包络 3σ 余量口径，与各形态实现共享）。</summary>
	public Vector3 CapCenter;
	public float CapRadiusRad;

	public bool InCap(Vector3 dir) => dir.Dot(CapCenter) >= MathF.Cos(CapRadiusRad);
}

/// <summary>
/// 特征地形场口：特征以「影响度 ∈[0,1] + 目标绝对高度（米）」表达自己对地表的诉求——
/// 合成器执行 FinalHeight = lerp(当前高度, TargetM, Influence)（决策 07 步骤⑤：
/// 目标语义替代"+N 米增量"——峰高只有一个语义来源）。特征内部形态对合成器不可见。
/// 多特征叠加 = 按序 lerp（后位特征覆盖先位）。
/// </summary>
public interface ITerrainField
{
	/// <summary>任意球面方向的（影响度, 目标绝对高度）；influence = 0 时 target 无意义。</summary>
	(float influence, float targetM) SampleAt(Vector3 dir);
}
