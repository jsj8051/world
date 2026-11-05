using System;
using Godot;
using World.Data;

namespace World.Logic;

/// <summary>
/// 表层洋流：流函数法 + 多因素驱动（风应力旋度、科里奥利 β、热成风、海岸边界）。
///
/// 解一次球面泊松方程 <c>∇²ψ = curl(τ)</c>（陆地 ψ=0 作为绕流边界），ψ 的等值线就是流线：
/// 信风带推出副热带环流（gyre），大陆边界让洋流绕行，西边界自然强化。
/// 方向 = ψ 梯度旋转 90°，冷暖 = 经向热输送（向极暖流 / 向赤道寒流，∝ 经向温度梯度），
/// 强度随 |∇ψ| 增大。
///
/// 这是**诊断法**：没有时间步进、没有盐度/温跃层，只回答"表层水往哪流、冷暖如何"。
///
/// @example
/// <code>
/// var ocean = new OceanCurrentModel();
/// ocean.Compute(grid, spec, PrevailingWind.BuildAll(grid.Dirs, 1f, true), oceanTempC: null);
/// Vector3 flow = ocean.Direction[i];          // 海格才有值，陆地为零
/// </code>
/// </summary>
public sealed class OceanCurrentModel
{
	/// <summary>逐格洋流方向（单位切向量；陆地 / 涡旋中心为零）。</summary>
	public Vector3[] Direction { get; private set; } = Array.Empty<Vector3>();

	/// <summary>逐格冷暖 ∈ [−1, 1]：+1 向极暖流、−1 向赤道寒流、0 = 陆地或静水。
	/// 幅度已乘经向温度梯度权重（∝ |sin lat|）⇒ 赤道附近恒趋 0（那里"向极"无定义）。</summary>
	public float[] Warmth { get; private set; } = Array.Empty<float>();

	/// <summary>逐格强度 ∈ [0.3, 1]（0 = 陆地 / 无流）：西边界强化流满值，开阔大洋弱流 0.3。</summary>
	public float[] Strength { get; private set; } = Array.Empty<float>();

	/// <summary>流函数 ψ（等值线即流线；诊断 / 显示用，不是温度输入）。</summary>
	public float[] Psi { get; private set; } = Array.Empty<float>();

	/// <summary>SOR 实际用掉的迭代数（未收敛时诊断用）。</summary>
	public int IterationsUsed { get; private set; }

	/// <summary>最后一次求解的残差上界（诊断用；&gt;1e-4 说明 ψ 尚未成形，洋流会偏弱）。</summary>
	public float Residual { get; private set; }

	/// <summary>风应力旋度的降噪遍数（格尺度抖动不是洋流）。</summary>
	const int CurlSmoothPasses = 8;

	/// <summary>流函数的降噪遍数（未收敛残差留在格尺度上）。</summary>
	const int PsiSmoothPasses = 4;

	/// <summary>求解洋流场。</summary>
	/// <param name="grid">气候网格视图。</param>
	/// <param name="spec">气候配置（洋流组旋钮）。</param>
	/// <param name="wind">驱动风场（逐格切向向量；null = 用盛行风）。</param>
	/// <param name="oceanTempC">海洋温度场（热成风项；null = 只用风应力）。</param>
	/// <param name="initialPsi">上一次求解的 ψ（热启动：第二遍只改右端项；null = 从零起）。</param>
	/// <param name="iterationCap">迭代上限（0 = 按 <see cref="ClimateSpec.OceanIterations"/> × √格数 推，封顶 600）。
	/// 显式给值时以它为准：第一遍只喂沿岸冷暖（120 遍足够），第二遍是存档场（300 遍）。</param>
	public void Compute(ClimateGrid grid, ClimateSpec spec, Vector3[] wind, float[] oceanTempC,
		float[] initialPsi = null, int iterationCap = 0)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (spec == null) throw new ArgumentNullException(nameof(spec));

		int n = grid.Count;
		Direction = new Vector3[n];
		Warmth = new float[n];
		Strength = new float[n];
		Psi = new float[n];
		var land = grid.Land;
		var neighbors = grid.Neighbors;

		var stress = wind ?? PrevailingWind.BuildAll(grid.Dirs, spec.RotationSpeed, spec.Prograde);
		var thermalWind = ThermalWindGradient(grid, oceanTempC, spec.OceanThermalWindWeight);

		if (initialPsi != null && initialPsi.Length == n)
			Array.Copy(initialPsi, Psi, n);

		// 风应力旋度：沿邻居多边形环路积分 ∮ wind·dl，再除以环面积（用邻居数近似）。
		// β 效应（Sverdrup）低纬放大：curl /= cos(lat)^betaScale —— 副热带环流主轴因此偏赤道侧。
		var curl = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (land[i]) continue;
			var nbs = neighbors[i];
			if (nbs == null || nbs.Length < 3) continue;

			float c = 0f;
			for (int j = 0; j < nbs.Length; j++)
			{
				int a = nbs[j];
				int b = nbs[(j + 1) % nbs.Length];
				var local = stress[a] + thermalWind[a];
				c += local.Dot(grid.Dirs[b] - grid.Dirs[a]);
			}
			curl[i] = c / nbs.Length;

			if (spec.OceanBetaScale > 0f)
			{
				float beta = MathF.Max(MathF.Cos(grid.LatRad(i)), 0.15f);
				curl[i] /= MathF.Pow(beta, spec.OceanBetaScale);
			}
		}

		// 降噪：离散环路积分在不规则五/六边形上带格尺度抖动，而洋流是千公里级的场。
		// 不抹掉它，ψ 会解成一堆小涡（粒子图上像一团乱麻）。纯数值处理，不改物理假设。
		SmoothInPlace(curl, grid, land, CurlSmoothPasses);

		// 分辨率越大，一次 SOR 只能把信息推过一格 ⇒ 域直径变大后需要的遍数 ∝ √n。
		// ⚠️ res4（537 跳半径）下 600 遍仍到不了 1e-5（实测残差 ~1e-4），所以 ψ 是**准静态近似**
		//    而不是收敛解：环流形态与方向可信，强度绝对值偏弱。要精确解须换多重网格。
		int targetIterations = iterationCap > 0
			? iterationCap
			: (int)(spec.OceanIterations * MathF.Sqrt(n / 10242f));
		targetIterations = Math.Clamp(targetIterations, spec.OceanIterations, 600);

		float omega = spec.OceanSorOmega;
		float maxErr = 0f;
		IterationsUsed = 0;
		for (int iter = 0; iter < targetIterations; iter++)
		{
			maxErr = 0f;
			for (int i = 0; i < n; i++)
			{
				if (land[i]) { Psi[i] = 0f; continue; }
				var nbs = neighbors[i];
				if (nbs == null || nbs.Length < 3) continue;

				float sum = 0f;
				foreach (int nb in nbs) sum += Psi[nb];
				float gaussSeidel = (sum - curl[i]) / nbs.Length;
				float next = Psi[i] + omega * (gaussSeidel - Psi[i]);
				float err = MathF.Abs(next - Psi[i]);
				if (err > maxErr) maxErr = err;
				Psi[i] = next;
			}
			IterationsUsed = iter + 1;
			if (maxErr < 1e-5f) break;
		}
		Residual = maxErr;

		// ψ 再降噪：不收敛的残差留在格尺度上，直接求梯度会得到抖动方向。
		SmoothInPlace(Psi, grid, land, PsiSmoothPasses);

		// 方向与强度：ψ 梯度旋转 90°（∇ψ × r）就是等值线切向；|∇ψ| 越大流越强（西边界强化由此涌现）。
		float gridRatio = MathF.Max(0.5f, MathF.Sqrt(n / 10242f));
		for (int i = 0; i < n; i++)
		{
			if (land[i]) continue;
			var nbs = neighbors[i];
			if (nbs == null || nbs.Length < 3) continue;

			var gradient = Vector3.Zero;
			foreach (int nb in nbs)
			{
				var edge = grid.Dirs[nb] - grid.Dirs[i];
				float len = edge.Length();
				if (len < 1e-9f) continue;
				gradient += (Psi[nb] - Psi[i]) * (edge / len);
			}

			var gyre = gradient.Cross(grid.Dirs[i]);
			float gyreWeight = MathF.Min(1f, gradient.Length() * spec.OceanStrengthGain * gridRatio);
			if (gyre.LengthSquared() > 1e-12f)
			{
				Direction[i] = gyre.Normalized();
				Strength[i] = 0.3f + gyreWeight * 0.7f;
			}
		}

		// 冷暖 = 经向热输送：向极流（水来自暖处）= 暖流，向赤道流（水来自冷处）= 寒流。
		// 输送量 ∝ 流速 × 经向温度梯度，而梯度 ∝ |sin lat|（赤道为 0、向极增大）——
		// 所以必须乘这个权重：只取向极的**符号**会让"向极"在赤道翻转，同一条洋流在两侧
		// 一暖一冷，海温沿赤道整圈出现 ±5 °C 台阶（2026-10-23 实机截图暴露的横贯断裂）。
		// 归一化到 45°（该纬度冷暖判读最强）⇒ 中高纬读数不变，只收掉热带与赤道的假台阶。
		float gradientNorm = 1f / MathF.Sin(MathF.PI / 4f);
		for (int i = 0; i < n; i++)
		{
			if (land[i] || Direction[i].LengthSquared() < 1e-12f) continue;

			var r = grid.Dirs[i];
			float lat = grid.LatRad(i);
			float lon = MathF.Atan2(r.Z, r.X);
			float sinLat = MathF.Sin(lat), cosLat = MathF.Cos(lat);

			// 北向切向量（解析式；极点处退化成任意水平方向，仍然可用）。
			var north = new Vector3(-sinLat * MathF.Cos(lon), cosLat, -sinLat * MathF.Sin(lon));
			var towardPole = lat >= 0f ? north : -north;

			float transport = MathF.Min(1f, MathF.Abs(sinLat) * gradientNorm);
			float poleComponent = Direction[i].Dot(towardPole);
			Warmth[i] = Math.Clamp(poleComponent * 3f * transport, -1f, 1f);
		}
	}

	/// <summary>
	/// 沿岸冷暖：海格取自身，陆格借最近海格邻居（距离越远衰减越快，e 折 ≈ 500 km）。
	/// 温度模型用它把"暖流沿岸增温 / 寒流沿岸降温"落到陆地上。
	/// </summary>
	public float[] BuildCoastalWarmth(ClimateGrid grid)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		int n = grid.Count;
		var warmth = new float[n];

		for (int i = 0; i < n; i++)
		{
			if (!grid.Land[i])
			{
				warmth[i] = Warmth[i] * Strength[i];
				continue;
			}

			float best = 0f, bestDistance = float.MaxValue;
			foreach (int nb in grid.Neighbors[i])
			{
				if (grid.Land[nb] || Warmth[nb] == 0f) continue;
				float distance = MathF.Acos(Math.Clamp(grid.Dirs[i].Dot(grid.Dirs[nb]), -1f, 1f));
				if (distance < bestDistance) { bestDistance = distance; best = Warmth[nb] * Strength[nb]; }
			}
			if (bestDistance < float.MaxValue)
				warmth[i] = best * MathF.Exp(-bestDistance / ClimateGrid.CoastalDecayRad);
		}
		return warmth;
	}

	/// <summary>海洋格上的原地邻居平均（陆地保持 0）：给 curl / ψ 降噪用。</summary>
	static void SmoothInPlace(float[] field, ClimateGrid grid, bool[] land, int passes)
	{
		if (passes <= 0) return;
		int n = field.Length;
		var buffer = new float[n];

		for (int pass = 0; pass < passes; pass++)
		{
			for (int i = 0; i < n; i++)
			{
				if (land[i]) { buffer[i] = 0f; continue; }
				var nbs = grid.Neighbors[i];
				if (nbs == null || nbs.Length < 3) { buffer[i] = field[i]; continue; }

				float sum = field[i];
				int count = 1;
				foreach (int nb in nbs) { sum += field[nb]; count++; }
				buffer[i] = sum / count;
			}
			Array.Copy(buffer, field, n);
		}
	}

	static Vector3[] ThermalWindGradient(ClimateGrid grid, float[] oceanTempC, float weight)
	{
		int n = grid.Count;
		var gradient = new Vector3[n];
		if (oceanTempC == null || weight == 0f) return gradient;

		// 温度梯度旋转 90°（∇T × r）= 地转流的切向；只在海格上生效。
		for (int i = 0; i < n; i++)
		{
			if (grid.Land[i] || oceanTempC[i] == 0f) continue;
			var local = Vector3.Zero;
			foreach (int nb in grid.Neighbors[i])
			{
				var edge = grid.Dirs[nb] - grid.Dirs[i];
				float len = edge.Length();
				if (len < 1e-9f) continue;
				local += (oceanTempC[nb] - oceanTempC[i]) * (edge / len);
			}
			if (local.LengthSquared() > 1e-12f)
				gradient[i] = local.Cross(grid.Dirs[i]).Normalized() * weight;
		}
		return gradient;
	}
}
