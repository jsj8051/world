using Godot;
using World.Logic;

namespace World.Render;

/// <summary>离岸距离（自然地理）：语义 = "离海有多远"（km 感知），跳数只是内部实现。
/// 12 跳满域（邻格中心距 2.469 km ⇒ 满域 ≈ 30 km ≈ 世界半径的 1/11），换世界尺度时此处同步重标。
/// 读 Final 口径（<c>FinalDistToCoast</c> / <c>FinalDistToLand</c>，由 <c>FinalLand</c> 派生）。</summary>
public sealed class CoastDistanceMode : MapMode
{
	readonly WorldGenSimulation _p;
	public CoastDistanceMode(WorldGenSimulation p) => _p = p;
	public override string Name => "离岸距离";
	public override string ScaleCaption => "距海渐变 · 12 跳满域（≈30 km）";
	public override Color CellColorAt(int i)
	{
		var f = _p.Facts.Final;
		int d = f.FinalLand[i] ? f.FinalDistToCoast[i] : f.FinalDistToLand[i];
		float t = System.Math.Clamp(d / 12f, 0f, 1f);
		return f.FinalLand[i]
			? new Color(0.93f - 0.45f * t, 0.78f - 0.30f * t, 0.32f + 0.15f * t)   // 陆：近岸米白 → 内陆深棕
			: new Color(0.55f - 0.50f * t, 0.75f - 0.59f * t, 0.80f - 0.38f * t);  // 海：近岸青白 → 深海蓝
	}
}
