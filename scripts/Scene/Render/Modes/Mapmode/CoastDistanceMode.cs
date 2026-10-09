using Godot;
using World.Render;                // MapMode（表现层契约基类）

namespace World.WorldGen;

/// <summary>离岸距离（自然地理 · 注册序 = Id = 2）：语义 = "离海有多远"（km 感知）；跳数只是内部实现。
/// 12 跳满域按 res4 实测量级标定（格边 ≈26 km ⇒ 满域 ≈ 312 km），换分辨率/网格时此处同步重标。
/// 收口（§07 D-1）：离岸距离同样读 **Final 口径**（FinalDistToCoast/Land 由 FinalLand 派生）。</summary>
public sealed class CoastDistanceMode : MapMode
{
	readonly WorldGenSimulation _p;
	public CoastDistanceMode(WorldGenSimulation p) => _p = p;
	public override string Name => "离岸距离";
	public override string ScaleCaption => "距海渐变 · 12 跳满域（res4 ≈ 312 km）";
	public override Color CellColorAt(int i)
	{
		var f = _p.Facts.Final;
		int d = f.FinalLand[i] ? f.FinalDistToCoast[i] : f.FinalDistToLand[i];
		float t = System.Math.Clamp(d / 12f, 0f, 1f);   // 12 跳满域（res4 实测量级）
		return f.FinalLand[i]
			? new Color(0.93f - 0.45f * t, 0.78f - 0.30f * t, 0.32f + 0.15f * t)   // 陆：近岸米白 → 内陆深棕
			: new Color(0.55f - 0.50f * t, 0.75f - 0.59f * t, 0.80f - 0.38f * t);  // 海：近岸青白 → 深海蓝
	}
}
