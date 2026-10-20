using Godot;
using World.Logic;

namespace World.Render;

/// <summary>
/// 海拔模式：灰度高程图——逐格海拔线性映射到 sRGB 灰阶。
/// 固定域 ±4000 m（0 m 恒 0.5 中灰），不做 min-max 自适应 ⇒ 跨世界可比。
/// 纯线性的代价：低地 0–300 m 只占约 10 个 8-bit 灰阶，判读起伏请用「地形」模式。
/// </summary>
public sealed class ElevationMode : MapMode
{
	/// <summary>灰度域半幅（米）：对称域覆盖地球尺度极端（海沟约 −3900 / 峰顶约 +3900），
	/// 同时让海平面恒为 0.5。</summary>
	const float DomainHalfM = 4000f;

	readonly WorldGenSimulation _p;

	public ElevationMode(WorldGenSimulation p) => _p = p;

	public override string Name => "海拔";

	/// <summary>坞图例：声明固定域与线性口径（数值是屏幕 sRGB 灰阶，不是线性亮度）。</summary>
	public override string ScaleCaption => "海拔灰度 · 线性：−4000 m 黑 → +4000 m 白（0 m 中灰）";

	public override Color CellColorAt(int i) => ElevationColor(_p.Terrain.HeightM[i]);

	/// <summary>
	/// 海拔（米）→ sRGB 灰阶：固定域 ±4000 m 线性归一化，域外 clamp。
	/// 返回的是 sRGB 意图色，渲染层统一转 linear；0 m 恒为 0.5。
	/// </summary>
	/// <example><code>ElevationColor(0f) == new Color(0.5f, 0.5f, 0.5f)</code></example>
	public static Color ElevationColor(float m)
	{
		float v = (Mathf.Clamp(m, -DomainHalfM, DomainHalfM) + DomainHalfM) / (2f * DomainHalfM);
		return new Color(v, v, v);
	}
}
