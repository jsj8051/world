using System;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 板块种子点：球面均匀随机撒点，每点附切向速率、运动方位、密度，并由切向速度反解欧拉矢量。
/// 纯函数，随机序列只走 <c>Plate_Seeds</c> / <c>Plate_Density</c> 两条独立流。
/// </summary>
public static class PlateSeedOps
{
	/// <summary>撒 <paramref name="plateCount"/> 个种子点。</summary>
	/// <param name="seed">世界种子。</param>
	/// <param name="plateCount">板块数（≥ 1）。</param>
	/// <param name="minSpeed">切向速率下界（含）。</param>
	/// <param name="maxSpeed">切向速率上界（不含）。</param>
	public static Plate[] Build(int seed, int plateCount, float minSpeed, float maxSpeed)
	{
		if (plateCount < 1)
			throw new ArgumentOutOfRangeException(nameof(plateCount),
				$"板块数须 ≥ 1（实得 {plateCount}）——0 块会让每格都没有归属，本阶段不产出那种世界");

		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Seeds));
		var densRnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Density));
		var plates = new Plate[plateCount];
		for (int p = 0; p < plateCount; p++)
		{
			var point = SpherePoint.At(CoordUtil.UniformSphere(rnd));
			float v = minSpeed + (maxSpeed - minSpeed) * (float)rnd.NextDouble();
			float psi = MathF.Tau * (float)rnd.NextDouble();     // 切平面内的运动方位
			var vTan = v * point.Direction(psi);
			plates[p] = new Plate
			{
				PlateId = p,
				SeedTheta = point.Theta,
				SeedPhi = point.Phi,
				Omega = point.Dir.Cross(vTan),    // ω ⊥ dir ⇒ `ω × dir` 恰为该切向速度
				Density = (float)densRnd.NextDouble(),
			};
		}
		return plates;
	}
}
