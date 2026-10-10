using System;
using System.Collections.Generic;

namespace World.Logic;

// 世界生成空间 · 河流**符号宽度**分级（表现层**纯函数**；生产档只做"符号粗细系统"）：
//
// ★★这不是物理河宽。真实河宽在生产档 res4（格边长 ≈26.1 km，实测）上是**亚格量**：
//     10 m / 200 m / 800 m = 格边长的 0.04% / 0.8% / 3%；全球远景视图下 800 m 巨河 ≈ 0.06 px。
//     而本表的笔画反而比真实河宽大 1~2 个数量级（见 <see cref="StrokeWidthPx"/>）⇒ 它只能是视觉等级。
//     ⇒ 生产档**不表达**物理河宽：WidthM / DepthM / BankGeometry / Floodplain / 分汊 一律不建。
//
// ★纯函数、零副作用：只读"河段的径流累积"，**不写** RiverNetwork / RiverGraph / RiverGeometry 任何字段。
// ★离散档位（不做连续映射）：避免 1.37 / 1.42 / 1.51 px 这种看起来像噪声的抖动。
// ★宽度属于**河段（segment）**，不属于节点：NodeKind（Trunk/Tributary/Source/Outlet/Confluence）
//     只负责颜色与连接样式，**不参与粗细**——否则单调性不成立（小干流的河源 vs 大支流的河口会反转）。
// ★分位而非固定阈值：免标定，且不随降水模型量级漂移；代价是档位含义是"本世界内的相对等级"。
public static class RiverSymbolWidth
{
	/// <summary>档位数（0 = 最细）。</summary>
	public const int ClassCount = 5;

	/// <summary>
	/// 笔画宽度（px），**参考口径** = 全球远景视图下星球直径 <see cref="ReferenceGlobePx"/> px。
	/// 世界单位换算：<c>半宽 = px × Radius / ReferenceGlobePx</c>（见 <see cref="HalfWidthWorld"/>）。
	/// ⚠️ 与真实河宽的落差（球半径口径）：800 m = 1.26e-4 R；Class 0 全宽 = 1.8e-3 R（**14×**）、
	///    Class 4 全宽 = 7.6e-3 R（**61×**）⇒ 绝不可当作米数用于任何水文 / 流量计算。
	/// </summary>
	public static readonly float[] StrokeWidthPx = { 0.9f, 1.3f, 1.9f, 2.7f, 3.8f };

	/// <summary>
	/// **v2 屏幕空间路径当前固定使用最粗档**（用户 2026-10-04 视觉决策："一直是最大号的粗度就行"）。
	/// ⇒ 渲染不再按档位变化粗细，取 <see cref="StrokeWidthPx"/> 的最大值。
	///
	/// ⚠️ 分档能力（`Classify` / `QuantileBreaks` / 单调性）**保留**，因为它仍被 v1.x 兜底路径
	///    （`HalfWidthWorld` / `SoftHalfWidthWorld`）消费，不是死代码。
	///    将来若要恢复"粗细表达径流量"，只需把这里换回 `StrokeWidthPx[cls]`。
	/// </summary>
	public static float UniformWidthPx => StrokeWidthPx[StrokeWidthPx.Length - 1];

	/// <summary>
	/// `RiverSoftEdge`（**不是** RiverBank——真实地理河岸在 res4 是亚格量，不存在）在笔画**每侧**外扩的额外宽度
	/// （px，同 <see cref="ReferenceGlobePx"/> 口径）。纯视觉过渡带，无地理含义。
	/// </summary>
	/// ↑v1.1 重标定（实机截图：1.1 px 时"软边比河宽"，喧宾夺主）⇒ 收到 0.5。
	public const float SoftEdgeMarginPx = 0.5f;

	/// <summary>参考视图口径：星球直径像素数（px ⇄ 世界单位换算的锚）。</summary>
	public const float ReferenceGlobePx = 1000f;

	/// <summary>分位断点（0..1）：把河段累积量切成 <see cref="ClassCount"/> 档（升序）。</summary>
	public static readonly float[] QuantileBreaks = { 0.50f, 0.75f, 0.90f, 0.98f };

	/// <summary>
	/// 河段累积量 → 符号宽度档位（**纯函数**）。
	/// 单调性保证：断点取自升序分位 ⇒ 档位随累积量单调不减（A &lt; B ⇒ class(A) ≤ class(B)）。
	/// </summary>
	public static int[] Classify(IReadOnlyList<float> segAccumMm)
	{
		int n = segAccumMm == null ? 0 : segAccumMm.Count;
		var cls = new int[n];
		if (n == 0) return cls;

		var sorted = new float[n];
		for (int i = 0; i < n; i++) sorted[i] = segAccumMm[i];
		Array.Sort(sorted);

		var brk = new float[QuantileBreaks.Length];
		for (int j = 0; j < brk.Length; j++)
			brk[j] = sorted[(int)Math.Floor(QuantileBreaks[j] * (n - 1))];

		for (int i = 0; i < n; i++)
		{
			int c = 0;
			// 严格大于：**并列值落入较低档** ⇒ 全等输入 ⇒ 全档 0（"没有差异就没有等级"）
			while (c < brk.Length && segAccumMm[i] > brk[c]) c++;
			cls[i] = c;
		}
		return cls;
	}

	/// <summary>
	/// 档位 → 笔画**半宽**（世界单位）。
	/// <paramref name="zoomComp"/> = 相机感知补偿（v1.2；默认 1 = 全球远景参考视图，即 v1/v1.1 行为）。
	/// </summary>
	public static float HalfWidthWorld(float radius, int cls, float zoomComp = 1f)
	{
		if (cls < 0) cls = 0;
		if (cls >= StrokeWidthPx.Length) cls = StrokeWidthPx.Length - 1;
		return StrokeWidthPx[cls] * radius / ReferenceGlobePx * ClampComp(zoomComp);
	}

	/// <summary>档位 → 软边**半宽**（世界单位）= 笔画半宽 + 每侧外扩。</summary>
	public static float SoftHalfWidthWorld(float radius, int cls, float zoomComp = 1f)
		=> HalfWidthWorld(radius, cls, zoomComp) + SoftEdgeMarginPx * radius / ReferenceGlobePx * ClampComp(zoomComp);

	// ── v1.2：Camera-aware Symbol Width（★不是 LOD，不换几何、不换数据）──────────
	//
	// 背景：v1/v1.1 的半宽固定为世界空间量 ⇒ 屏幕像素宽随 zoom 线性变粗
	//       （全球 1.3 px → 区域 3 px → 拉近 8 px = 截图里"河流肥成高速公路"的根因）。
	// 做法：只把**表现宽度**乘一个随 zoom 收缩的补偿系数；几何拓扑、chain、档位分类全部不变。
	//   zoom = 当前"世界单位/像素"相对参考视图的倒数 = uRef / uNow（zoom=1 ⇒ 全球参考视图）
	//   补偿 = clamp(zoom^(Alpha−1), CompMin, 1)
	//     Alpha = 1 ⇒ 恒 px（完美，但要 shader）；Alpha = 0 ⇒ 世界空间固定（v1 行为，会失控）
	//     取 Alpha = 0.35 ⇒ 部分补偿：既不失控，也不假装自己是恒 px。
	//
	// ★★契约（产品层，2026-10-04 拍板）：
	//   生产河流支持**全球至区域级**地图缩放；河流宽度**始终**是视觉符号宽度、不是物理河宽，
	//   并在 [MinSupportedZoom, MaxSupportedZoom] 内做相机感知补偿；**超出该范围不承诺近景河道表现**
	//   （⇒ 不引入真实河宽 / 局部高 res 水文 / GPU shader ribbon）。

	/// <summary>补偿指数：1 = 恒 px，0 = 世界空间固定（v1 行为）。</summary>
	public const float ZoomAlpha = 0.35f;
	/// <summary>补偿下限：无论怎么拉近，不得细于基准的 50%（避免细到看不见）。</summary>
	public const float CompMin = 0.5f;
	/// <summary>正式支持的 zoom 下界（比全球远景还远）。</summary>
	public const float MinSupportedZoom = 0.6f;
	/// <summary>正式支持的 zoom 上界（区域级上限；超出不承诺近景河道表现）。</summary>
	public const float MaxSupportedZoom = 8.0f;

	/// <summary>
	/// zoom → 宽度补偿系数（**纯函数**、单调不增、值域 [CompMin, 1]）。
	/// zoom 先被夹到正式支持区间 ⇒ 超出范围的行为是**确定**的（停在边界值），不是"随它去"。
	/// </summary>
	public static float ZoomCompensation(float zoom)
	{
		if (!float.IsFinite(zoom) || zoom <= 0f) return 1f;
		float z = Math.Clamp(zoom, MinSupportedZoom, MaxSupportedZoom);
		float c = (float)Math.Pow(z, ZoomAlpha - 1f);
		if (!float.IsFinite(c)) return CompMin;
		return Math.Clamp(c, CompMin, 1f);
	}

	static float ClampComp(float c) =>
		!float.IsFinite(c) ? 1f : Math.Clamp(c, CompMin, 1f);
}
