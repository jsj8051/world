using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）
using World.Data;            // Plate / 边界集 / 类型 / 角色

namespace World.Logic;

/// <summary>
/// 板块边界算子：由边界集（<see cref="PlateBoundarySet"/>）+ 板块表 + 逐格壳型算运动与角色。
/// 几何（谁是边界、边两侧是谁）来自边界集，本类不再扫邻居判边界；角色按两侧格的实际壳型定。
/// 「碰撞 / 分离」是**逐条边界**的性质，不是整块板块的性质——一块板块可以跟东邻撞、跟西邻裂。
/// 每条边界取两块速度在**边界法向**上的分量之差：&lt; 0 迎面（汇聚）｜&gt; 0 背向（张裂）｜≈ 0 走滑。
/// </summary>
public static class PlateBoundaryOps
{
	/// <summary>**活动边界的判定阈值**（法向相对速率，无量纲）：<c>|motion|</c> 超过它才算一条
	/// 活动的汇聚 / 张裂边界。山带（<see cref="HighlandOps"/>）与海底（<see cref="OceanDepthOps"/>）
	/// 共用这一份口径。</summary>
	public const float ActiveMotion = 0.25f;

	/// <summary>**强度归一尺度**：<c>strength = clamp(|motion| / 本值, 0, 1)</c>。
	/// 山带与海底共用这一份口径。</summary>
	public const float MotionScale = 0.9f;

	/// <summary>密度小者上盘；等密度按板块号小者，保证两侧不会同时上盘。</summary>
	public static bool Overrides(int mine, int other, Plate[] plates)
	{
		float a = plates[mine].Density, b = plates[other].Density;
		if (a < b) return true;
		if (a > b) return false;
		return mine < other;
	}

	/// <summary>陆洋边恒陆侧上盘；同壳型才看密度。</summary>
	public static bool Rides(CrustType mine, CrustType other, bool denserRides)
	{
		if (mine != other) return mine == CrustType.Continental;
		return denserRides;
	}

	/// <summary>
	/// 一对相邻格的**法向相对运动**（B 相对 A）：
	/// <c>(v_B − v_A) · n̂</c>，<c>n̂</c> = A→B 的球面切向单位向量。
	/// 负 = 迎面相撞（汇聚）｜正 = 背向拉开（张裂）｜≈ 0 = 平行错动（走滑）。
	/// </summary>
	public static float MotionOfEdge(Vector3 dirA, Vector3 dirB, in Plate plateA, in Plate plateB)
	{
		// 切向单位向量：球面上从 A 指向 B 的方向（去掉径向分量后归一化）
		var normal = dirB - dirA * dirA.Dot(dirB);
		if (normal.LengthSquared() < 1e-12f) return 0f;   // 同格或对径点，无切向
		normal = normal.Normalized();

		return TangentVelocity(dirB, plateB).Dot(normal) - TangentVelocity(dirA, plateA).Dot(normal);
	}

	/// <summary>按法向相对速率分档；阈值与活动边界共用 <see cref="ActiveMotion"/>。</summary>
	public static BoundaryKind KindOf(float motion)
	{
		if (!(MathF.Abs(motion) > ActiveMotion)) return BoundaryKind.Transform;
		return motion < 0f ? BoundaryKind.Convergent : BoundaryKind.Divergent;
	}

	/// <summary>
	/// 板块在**该点**的切向速度：刚体转动 <c>v = ω × dir</c>（结果自动与 dir 正交）。
	/// 同一板块各格共用同一个 ω ⇒ 角速度一致、线速度随位置变。
	/// </summary>
	static Vector3 TangentVelocity(Vector3 dir, in Plate plate) => plate.Omega.Cross(dir);
}
