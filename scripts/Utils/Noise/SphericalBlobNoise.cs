using System;
using Godot;                 // Vector3（纯结构体，不需要引擎运行时；测试宿主可用）

namespace World.Utils
{
	// 球面低频"团噪声"（纯 C#，**零引擎依赖**）：给初始地壳生成提供空间连续的排名场。
	//
	// 结构：K 个随机团心的高斯团场取**最大**（主尺度 = 大陆块尺度）+ 高频细节团场（次尺度），
	// 按 0.65 / 0.35 加权 → 归一到 0..1。团心数直接对应"大陆块数"这个旋钮（比"噪声频率"直观）。
	//
	// ⚠️ 为什么不用 Godot 的 FastNoiseLite（2026-09-11 实测教训）：
	// 它是**引擎类型**，在 World.Tests.Local（纯 .NET 控制台宿主，未初始化引擎）里构造会
	// 直接 access violation 崩掉整个测试进程；而老 Tectonics 用 FastNoiseLite 没暴露问题是
	// 因为它的单测从没走过 GenerateInitialCrust。测试纪律要求逻辑层能在无引擎宿主下跑。
	public sealed class SphericalBlobNoise
	{
		readonly Vector3[] _mainCenters;
		readonly Vector3[] _detailCenters;
		readonly float _mainSharpness;     // 1/σ²（越大团越"尖"）
		readonly float _detailSharpness;

		/// <param name="seed">确定性种子（同种子同场）。</param>
		/// <param name="blobCount">主尺度团心数（= 大陆块数）。</param>
		/// <param name="detailFrequencyScale">细节团心数与尖锐度的倍率（默认 3.5）。</param>
		public SphericalBlobNoise(int seed, int blobCount, float detailFrequencyScale = 3.5f)
		{
			var rng = new DeterministicRandom(seed);
			int mainCount = Math.Max(1, blobCount);
			_mainCenters = RandomUnitVectors(rng, mainCount);
			_detailCenters = RandomUnitVectors(rng, Math.Max(1, (int)(mainCount * detailFrequencyScale)));

			// 团宽标定：球面上 K 个团心时每个团分到的立体角 ≈ 4π/K → 张角 θ ≈ 2/sqrt(K)，
			// 用 (1 − cosθ) ≈ θ²/2 作距离代理 → σ² ≈ 2/K。取 1.2 倍留重叠（团要连成块而不是孤岛）。
			float mainSigma = 2.4f / mainCount;
			float detailSigma = mainSigma / (detailFrequencyScale * detailFrequencyScale);
			_mainSharpness = 1f / MathF.Max(mainSigma, 1e-6f);
			_detailSharpness = 1f / MathF.Max(detailSigma, 1e-6f);
		}

		/// <summary>单个方向取值（未归一，供加权组合）。</summary>
		public float Sample(Vector3 unitDirection)
			=> 0.65f * BlobField(unitDirection, _mainCenters, _mainSharpness)
			 + 0.35f * BlobField(unitDirection, _detailCenters, _detailSharpness);

		/// <summary>批量取值并归一到 0..1（排名场直接用）。</summary>
		public float[] SampleAll(Vector3[] unitDirections)
		{
			int n = unitDirections.Length;
			var values = new float[n];
			float min = float.MaxValue, max = float.MinValue;
			for (int i = 0; i < n; i++)
			{
				float v = Sample(unitDirections[i]);
				values[i] = v;
				if (v < min) min = v;
				if (v > max) max = v;
			}
			float span = MathF.Max(max - min, 1e-6f);
			for (int i = 0; i < n; i++) values[i] = (values[i] - min) / span;
			return values;
		}

		// 高斯团场取最大：exp(−(1 − dot)/σ²)——用 (1 − dot) 作球面距离平方的代理（近处等价于 θ²/2）。
		static float BlobField(Vector3 direction, Vector3[] centers, float sharpness)
		{
			float best = 0f;
			foreach (Vector3 center in centers)
			{
				float distanceSquared = 1f - direction.Dot(center);
				float value = MathF.Exp(-distanceSquared * sharpness);
				if (value > best) best = value;
			}
			return best;
		}

		static Vector3[] RandomUnitVectors(DeterministicRandom rng, int count)
		{
			var vectors = new Vector3[count];
			for (int i = 0; i < count; i++)
			{
				// 球面均匀采样（z 均匀 → 面积均匀）
				float z = (float)(2.0 * rng.NextDouble() - 1.0);
				float angle = (float)(2.0 * Math.PI * rng.NextDouble());
				float radius = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
				vectors[i] = new Vector3(radius * MathF.Cos(angle), z, radius * MathF.Sin(angle));
			}
			return vectors;
		}
	}
}
