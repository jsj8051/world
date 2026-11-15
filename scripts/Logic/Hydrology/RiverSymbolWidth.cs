namespace World.Logic;

// 河流符号宽度（表现层常量）：只做符号粗细，不表达物理河宽。
// res4 格边长 ≈26.1 km，真实 10 m / 200 m / 800 m 在全球远景（球直径 1000 px，约 40 km/px）下均 < 0.03 px
// ⇒ 符号宽度是制图夸张，与米数没有换算关系，WidthM / DepthM / BankGeometry / Floodplain / 分汊一律不建。
// ★当前渲染固定用最粗档（用户 2026-10-04 视觉决策）；分档表与相机感知补偿已随世界空间兜底路径一并删除，
//   若将来要恢复按径流量变粗细，从 git 历史取回 Classify/HalfWidthWorld 即可，不要在渲染层另造一份。
public static class RiverSymbolWidth
{
	/// <summary>笔画宽度（px）：屏幕空间路径固定取此值，由 shader 按 px 展开 ⇒ 恒视觉宽度。</summary>
	public const float UniformWidthPx = 3.8f;
}
