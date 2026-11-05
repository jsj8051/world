using System;
using Godot;

namespace World.Logic;

/// <summary>
/// 行星盛行风（三圈环流 + 科里奥利偏转）：解析式，无状态，只用于「没有温度场时」的兜底风
/// （洋流第一遍的风应力）与无季风星球的基线环流。
///
/// 环流带按 |纬度| 分：Hadley(0–30°) 向赤道、Ferrel(30–60°) 向极地、极地(60–90°) 向赤道；
/// 每带再叠加科里奥利偏转（北半球右偏、南半球左偏，强度 ∝ sin|lat| × 自转速度^0.7）。
/// 这正是真实地球信风（东北/东南）、西风带、极地东风的来源，不是贴图。
///
/// @example
/// <code>
/// var wind = PrevailingWind.At(ball.CellDirs[i], rotationSpeed: 1f, prograde: true);
/// // 20°N ⇒ 指向西南（东北信风）；45°N ⇒ 指向东北（西风带）
/// </code>
/// </summary>
public static class PrevailingWind
{
	/// <summary>该方向处的盛行风（单位切向量，指向下风向）。</summary>
	/// <param name="dir">格心方向（内部归一化）。</param>
	/// <param name="rotationSpeed">自转速度（相对地球；0 = 无科里奥利，风带只剩经向）。</param>
	/// <param name="prograde">自西向东 = true（地球式）；逆转 ⇒ 偏转方向整体镜像。</param>
	public static Vector3 At(Vector3 dir, float rotationSpeed, bool prograde)
	{
		var r = dir.Normalized();
		float lat = MathF.Asin(Math.Clamp(r.Y, -1f, 1f));
		float absLat = MathF.Abs(lat);
		bool north = lat >= 0f;

		// 极点附近经向切向退化（north = up 与 r 平行）⇒ 取任意切向当"北"，风向仍连续。
		var up = new Vector3(0f, 1f, 0f);
		var northTangent = up - r * r.Dot(up);
		if (northTangent.LengthSquared() < 1e-9f)
			northTangent = MathF.Abs(r.X) < 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 0f, 1f);
		northTangent -= r * r.Dot(northTangent);
		northTangent = northTangent.Normalized();

		float towardPole = absLat < 30f ? -1f : absLat < 60f ? 1f : -1f;
		var meridional = northTangent * (north ? towardPole : -towardPole);

		// 右偏方向 = r × m（北半球）；南半球与逆转各自翻符号 ⇒ 合成一个系数。
		float hemisphere = north ? 1f : -1f;
		float spin = prograde ? 1f : -1f;
		float deflect = MathF.Sin(absLat) * MathF.Pow(MathF.Max(0f, rotationSpeed), 0.7f);
		var wind = meridional + r.Cross(meridional) * (deflect * hemisphere * spin);

		return wind.LengthSquared() > 1e-9f ? wind.Normalized() : northTangent;
	}

	/// <summary>逐格盛行风场（一次性展开；供洋流风应力使用）。</summary>
	public static Vector3[] BuildAll(Vector3[] dirs, float rotationSpeed, bool prograde)
	{
		if (dirs == null) throw new ArgumentNullException(nameof(dirs));
		var field = new Vector3[dirs.Length];
		for (int i = 0; i < dirs.Length; i++) field[i] = At(dirs[i], rotationSpeed, prograde);
		return field;
	}
}
