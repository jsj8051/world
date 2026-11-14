using System;
using Godot;
using World.Data;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 风求解器：带线性摩擦的定常解（气压 → 风）。每格一次局部代数运算，不迭代、无 CFL、无奇异点。
/// 输入是 <see cref="TangentField.Gradient"/> 的原始邻域和；求解器按格心距归一化成 Pa/m 再代入。
///
/// <code>
/// f k×v = −∇p/ρ − r·v
/// u = −( r ∂x p + f ∂y p ) / ( ρ (r² + f²) )
/// v = (  f ∂x p − r ∂y p ) / ( ρ (r² + f²) )
/// </code>
///
/// 三个性质：f→0 时退化为顺梯度直吹（分母恒正，不需要夹 f）；偏转角 arctan(r/f) 从地转过渡到
/// 摩擦流；f 变号自动处理跨赤道（南半球东南信风过赤道转西南风）。风带与季风不再是两套东西——
/// 它们都从同一个 p 的梯度里出来。
///
/// 纬度基 / 科氏参数 / 摩擦系数在 <see cref="Create"/> 一次算好（逐月 12 次复用）。
///
/// @example
/// <code>
/// var solver = WindSolver.Create(grid, spec);
/// TangentField.GradientField(grid, pressureMonth, gradient);
/// solver.SolveSurface(gradient, wind, speed);
/// </code>
/// </summary>
internal sealed class LegacyWindSolver
{
	/// <summary>空气密度（kg/m³；只作为把气压梯度换算成风速的量纲常数）。</summary>
	const float AirDensityKgM3 = 1.2f;

	/// <summary>地球自转角速度（s⁻¹）；<c>RotationSpeed</c> 是它的倍率。</summary>
	const float OmegaEarth = 7.2921e-5f;

	/// <summary>hPa → Pa。</summary>
	const float HpaToPa = 100f;

	/// <summary>地形减速上限（2,500 m 以上不再继续衰减）。</summary>
	const float TerrainBlock = 0.45f;
	const float TerrainFullM = 2500f;

	readonly ClimateGrid _grid;
	readonly Vector3[] _east;
	readonly Vector3[] _north;
	readonly float[] _coriolis;
	readonly float[] _surfaceFriction;
	readonly float[] _gradientToPaPerM;
	readonly float _referenceSpeedMs;
	readonly float _lowFactor;
	readonly float _freeFactor;

	LegacyWindSolver(ClimateGrid grid, ClimateSpec spec)
	{
		if (!(spec.FrictionSurface > 0f))
			throw new ArgumentOutOfRangeException(nameof(spec), "FrictionSurface 必须为正（r 在分母）");
		if (!(spec.WindSpeedRefMs > 0f))
			throw new ArgumentOutOfRangeException(nameof(spec), "WindSpeedRefMs 必须为正");
		if (!(spec.FrictionFreeFactor > 0f) || spec.FrictionFreeFactor > spec.FrictionLowFactor
			|| spec.FrictionLowFactor > 1f)
			throw new ArgumentOutOfRangeException(nameof(spec),
				"摩擦倍数必须满足 0 < FrictionFreeFactor ≤ FrictionLowFactor ≤ 1（否则三套风的强弱序会反）");

		_grid = grid;
		_referenceSpeedMs = spec.WindSpeedRefMs;
		_lowFactor = spec.FrictionLowFactor;
		_freeFactor = spec.FrictionFreeFactor;

		int n = grid.Count;
		_east = new Vector3[n];
		_north = new Vector3[n];
		_coriolis = new float[n];
		_surfaceFriction = new float[n];
		_gradientToPaPerM = new float[n];

		float radiusM = grid.RadiusKm * 1000f;
		float spin = spec.RotationSpeed * (spec.Prograde ? 1f : -1f);
		for (int i = 0; i < n; i++)
		{
			var dir = grid.Dirs[i];
			var north = TangentField.NorthTangent(dir);
			_north[i] = north;
			_east[i] = TangentField.EastTangent(dir, north);
			_coriolis[i] = 2f * OmegaEarth * MathF.Sin(grid.LatRad(i)) * spin;
			_surfaceFriction[i] = spec.FrictionSurface * (grid.Land[i] ? spec.FrictionLandFactor : 1f);

			// TangentField.Gradient 返回的是"邻域和"口径：Σ (f_j−f_i)·ê = d·M·∇f，各向同性时 M = (邻居数/2)·I。
			// 换算成物理梯度：∇f = raw / (½Σ|r_j−r_i|)（六边形 = 3d、四邻接 = 2d，五边形自动折中）。
			double edgeSum = 0;
			foreach (int nb in grid.Neighbors[i]) edgeSum += (grid.Dirs[nb] - dir).Length();
			float normalizer = (float)(0.5 * edgeSum);
			_gradientToPaPerM[i] = normalizer > 1e-6f ? HpaToPa / (normalizer * radiusM) : 0f;
		}
	}

	/// <summary>建求解器（纬度基 / 科氏参数 / 摩擦系数只算一次）。</summary>
	public static LegacyWindSolver Create(ClimateGrid grid, ClimateSpec spec)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		return new LegacyWindSolver(grid, spec);
	}

	/// <summary>
	/// 三套风一次算完：同一个梯度 × 三个摩擦闭合（r、r·低空倍数、r·自由倍数）。
	/// 地形减速只乘地面风；软饱和在出口各做一次。
	/// </summary>
	public void SolveAll(Vector3[] pressureGradient, Vector3[] surface, Vector3[] low, Vector3[] free)
	{
		if (pressureGradient == null) throw new ArgumentNullException(nameof(pressureGradient));
		if (surface == null) throw new ArgumentNullException(nameof(surface));
		if (low == null) throw new ArgumentNullException(nameof(low));
		if (free == null) throw new ArgumentNullException(nameof(free));

		int n = _grid.Count;
		if (pressureGradient.Length != n || surface.Length != n || low.Length != n || free.Length != n)
			throw new ArgumentException("梯度 / 三套风数组必须与格数等长");

		for (int i = 0; i < n; i++)
		{
			float toPaPerM = _gradientToPaPerM[i];
			float f = _coriolis[i];
			float r = _surfaceFriction[i];
			float gx = pressureGradient[i].Dot(_east[i]) * toPaPerM;
			float gy = pressureGradient[i].Dot(_north[i]) * toPaPerM;

			float terrain = 1f - TerrainBlock * SmoothStep(0f, TerrainFullM, MathF.Max(0f, _grid.HeightM[i]));
			surface[i] = SolveClosure(f, r, gx, gy, terrain, _east[i], _north[i]);
			low[i] = SolveClosure(f, r * _lowFactor, gx, gy, 1f, _east[i], _north[i]);
			free[i] = SolveClosure(f, r * _freeFactor, gx, gy, 1f, _east[i], _north[i]);
		}
	}

	/// <summary>单套地面风（测试 / 需要单份时用；内部临时分配两份空数组）。</summary>
	public void SolveSurface(Vector3[] pressureGradient, Vector3[] wind, float[] speed)
	{
		if (wind == null) throw new ArgumentNullException(nameof(wind));
		if (speed == null) throw new ArgumentNullException(nameof(speed));
		if (speed.Length != wind.Length) throw new ArgumentException("风 / 风速数组必须等长");

		var scratch = new Vector3[wind.Length];
		SolveAll(pressureGradient, wind, scratch, scratch);
		for (int i = 0; i < wind.Length; i++) speed[i] = wind[i].Length();
	}

	/// <summary>一个摩擦闭合的解（切平面局部代数，无迭代）。</summary>
	Vector3 SolveClosure(float f, float r, float gx, float gy, float terrain, Vector3 east, Vector3 north)
	{
		var (u, v) = SolveComponents(f, r, gx, gy);
		float magnitude = MathF.Sqrt(u * u + v * v);
		if (magnitude < 1e-9f) return Vector3.Zero;

		float s = (1f - MathF.Exp(-magnitude / _referenceSpeedMs)) * terrain;
		return (east * u + north * v) / magnitude * s;
	}

	// Physical velocity is used for mass flux before the display-speed mapping.
	internal void SolveSurfacePhysical(Vector3[] pressureGradient, Vector3[] velocity)
		=> SolvePhysical(pressureGradient, velocity, 1f, applyTerrain: true);

	internal void SolveLowPhysical(Vector3[] pressureGradient, Vector3[] velocity)
		=> SolvePhysical(pressureGradient, velocity, _lowFactor, applyTerrain: false);

	void SolvePhysical(Vector3[] pressureGradient, Vector3[] velocity, float frictionFactor, bool applyTerrain)
	{
		for (int i = 0; i < _grid.Count; i++)
		{
			float gx = pressureGradient[i].Dot(_east[i]) * _gradientToPaPerM[i];
			float gy = pressureGradient[i].Dot(_north[i]) * _gradientToPaPerM[i];
			var (u, v) = SolveComponents(_coriolis[i], _surfaceFriction[i] * frictionFactor, gx, gy);
			float terrain = applyTerrain
				? 1f - TerrainBlock * SmoothStep(0f, TerrainFullM, MathF.Max(0f, _grid.HeightM[i])) : 1f;
			velocity[i] = (_east[i] * u + _north[i] * v) * terrain;
		}
	}

	static (float East, float North) SolveComponents(float f, float r, float gx, float gy)
	{
		float denom = r * r + f * f;
		return (-(r * gx + f * gy) / (AirDensityKgM3 * denom),
			(f * gx - r * gy) / (AirDensityKgM3 * denom));
	}

	static float SmoothStep(float edge0, float edge1, float x)
	{
		float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
		return t * t * (3f - 2f * t);
	}
}
