using System;
using System.Collections.Generic;

namespace World.Logic;

// 河流符号宽度（表现层纯函数）：只做符号粗细分级，不表达物理河宽——res4 格边长 ≈1.39 km，
// 真实 10 m / 200 m / 800 m = 格边长的 0.7% / 14% / 58%；全球远景（球直径 1000 px）下
// 800 m 巨河 ≈ 1 px，本表档位 0.9~3.8 px ⇒ 符号与米数没有换算关系，WidthM / DepthM /
// BankGeometry / Floodplain / 分汊一律不建。
// 纯函数、零副作用：只读河段径流累积，不写 RiverNetwork / RiverGraph / RiverGeometry 任何字段。
// 宽度属河段而不属节点（NodeKind 只管颜色与连接样式），否则单调性不成立（小干流河源 vs 大支流河口反转）；
// 分位而非固定阈值 ⇒ 免标定、不随降水量级漂移；离散档位 ⇒ 避免 1.37 / 1.42 / 1.51 px 的抖动。
public static class RiverSymbolWidth
{
	/// <summary>档位数（0 = 最细）。</summary>
	public const int ClassCount = 5;

	/// <summary>笔画宽度（px；参考口径 = 全球远景下星球直径 <see cref="ReferenceGlobePx"/> px）。
	/// 世界单位换算：半宽 = px × Radius / ReferenceGlobePx（见 <see cref="HalfWidthWorld"/>）。
	/// 这是屏幕符号、与真实河宽无换算关系，绝不可当米数用于任何水文 / 流量计算。</summary>
	public static readonly float[] StrokeWidthPx = { 0.9f, 1.3f, 1.9f, 2.7f, 3.8f };

	/// <summary>屏幕空间路径当前固定用最粗档（渲染不按档位变粗细，取 <see cref="StrokeWidthPx"/> 最大值）。
	/// 分档能力（<c>Classify</c> / <c>QuantileBreaks</c> / 单调性）仍被 <c>HalfWidthWorld</c> /
	/// <c>SoftHalfWidthWorld</c> 消费，不是死代码；恢复粗细表只需换回 <c>StrokeWidthPx[cls]</c>。</summary>
	public static float UniformWidthPx => StrokeWidthPx[StrokeWidthPx.Length - 1];

	/// <summary>软边（<c>RiverSoftEdge</c>，不是地理河岸——res4 下河岸是亚格量）每侧外扩的额外宽度
	/// （px，同 <see cref="ReferenceGlobePx"/> 口径），纯视觉过渡带、无地理含义。取 0.5：
	/// 1.1 px 时软边比河宽，喧宾夺主。</summary>
	public const float SoftEdgeMarginPx = 0.5f;

	/// <summary>参考视图口径：星球直径像素数（px ⇄ 世界单位换算的锚）。</summary>
	public const float ReferenceGlobePx = 1000f;

	/// <summary>分位断点（0..1）：把河段累积量切成 <see cref="ClassCount"/> 档（升序）。</summary>
	public static readonly float[] QuantileBreaks = { 0.50f, 0.75f, 0.90f, 0.98f };

	/// <summary>河段累积量 → 符号宽度档位（纯函数）。断点取自升序分位 ⇒ 档位随累积量单调不减
	/// （A &lt; B ⇒ class(A) ≤ class(B)）。</summary>
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
			// 严格大于 ⇒ 并列值落入较低档；全等输入全档 0（没有差异就没有等级）。
			while (c < brk.Length && segAccumMm[i] > brk[c]) c++;
			cls[i] = c;
		}
		return cls;
	}

	/// <summary>档位 → 笔画半宽（世界单位）。<paramref name="zoomComp"/> = 相机感知补偿
	/// （默认 1 = 全球远景参考视图）。</summary>
	public static float HalfWidthWorld(float radius, int cls, float zoomComp = 1f)
	{
		if (cls < 0) cls = 0;
		if (cls >= StrokeWidthPx.Length) cls = StrokeWidthPx.Length - 1;
		return StrokeWidthPx[cls] * radius / ReferenceGlobePx * ClampComp(zoomComp);
	}

	/// <summary>档位 → 软边半宽（世界单位）= 笔画半宽 + 每侧外扩。</summary>
	public static float SoftHalfWidthWorld(float radius, int cls, float zoomComp = 1f)
		=> HalfWidthWorld(radius, cls, zoomComp) + SoftEdgeMarginPx * radius / ReferenceGlobePx * ClampComp(zoomComp);

	// ── 相机感知补偿：不是 LOD，不换几何、不换数据 ──
	// 只把表现宽度乘一个随 zoom 收缩的系数（zoom = 参考视图世界单位/像素 ÷ 当前），
	// 补偿 = clamp(zoom^(Alpha−1), CompMin, 1)；几何拓扑、chain、档位分类全部不变。
	// 生产支持全球至区域级缩放，超出 [MinSupportedZoom, MaxSupportedZoom] 不承诺近景河道表现。

	/// <summary>补偿指数：1 = 恒 px，0 = 世界空间固定（会失控）。</summary>
	public const float ZoomAlpha = 0.35f;
	/// <summary>补偿下限：无论怎么拉近，不得细于基准的 50%（避免细到看不见）。</summary>
	public const float CompMin = 0.5f;
	/// <summary>正式支持的 zoom 下界（比全球远景还远）。</summary>
	public const float MinSupportedZoom = 0.6f;
	/// <summary>正式支持的 zoom 上界（区域级上限；超出不承诺近景河道表现）。</summary>
	public const float MaxSupportedZoom = 8.0f;

	/// <summary>zoom → 宽度补偿系数（纯函数、单调不增、值域 [CompMin, 1]）。
	/// zoom 先被夹到支持区间 ⇒ 超出范围的行为确定（停在边界值），不是随它去。</summary>
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
