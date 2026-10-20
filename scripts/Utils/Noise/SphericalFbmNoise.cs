using System;
using Godot;                 // Vector3（纯结构体，不需要引擎运行时；测试宿主可用）

namespace World.Utils
{
	// 球面 3D 噪声 + fBm（纯 C#，零引擎依赖）：连续、各向同性、无缝的起伏基场（初始地壳的陆海起伏用它铺）。
	//
	// 结构：Perlin 梯度噪声（整数哈希 → 12 向梯度表 → 五次 fade → 三线性插值）→ fBm 叠层
	// （lacunarity 2 / gain 0.5，按振幅平方和的均方根归一）。Sample 普通 fBm（圆丘，≈[−1,1]）；
	// SampleRidged 山脊 fBm（尖脊，∈[0,1]）。均值 ≈ 0、近高斯（σ ≈ 0.29，理论极值上限 1.45）。
	//
	// 归一化用 √Σgain² 而非 Σgain：按振幅和归一时，每加一层倍频就把全场振幅多除一次（各层峰不重合，
	// 是均方叠加），「层数」这个旋钮会偷偷改振幅；均方根归一让振幅与层数解耦，层数只加细节。
	//
	// 用 3D 噪声采样单位球方向而非经纬 2D：2D 在 ±180° 经线有接缝、两极被拉伸；3D 噪声的球面切片天然无缝。
	// 频率口径 = 基波长（km）：采样点 = 单位方向 × (WorldRadiusKm / 基波长) ⇒ 球面特征间距 ≈ 基波长，换 res 不改场。
	//
	// 不用 Godot 的 FastNoiseLite：它是引擎类型，纯 .NET 测试宿主里构造会 access violation 崩掉整个测试进程。
	// 确定性：晶格整数哈希 + 每倍频种子偏移，不消耗 rng、与调用顺序无关，同一方向可任意时刻重复采样。
	public sealed class SphericalFbmNoise
	{
		/// <summary>世界半径（km）——球面上「多少 km」的唯一换算口（噪声波长、<c>SpatialScale</c> 的面积 / 距离都经它）。
		/// 由「res4 每格 5 km² × 288,122 格 = 1,440,610 km²」推出 R = √(A/4π) ≈ 338.586 km。
		/// 本类零引擎 / 零业务依赖，故自带这份常量；改「每格多大」就改这里。</summary>
		public const float WorldRadiusKm = 338.586f;

		readonly int _seed;
		readonly int _octaves;
		readonly float _lacunarity;
		readonly float _gain;
		readonly float _baseScale;       // 单位方向 → 噪声空间半径 = WorldRadiusKm / 基波长
		readonly float _normalization;   // 1 / √Σgain²ⁱ
		readonly float _ridgedNorm;      // 1 / Σgainⁱ

		/// <param name="seed">确定性种子（同种子同场）。</param>
		/// <param name="baseWavelengthKm">基波长（km）：最大一层的特征尺寸（如大陆级起伏 2000–4000 km）。</param>
		/// <param name="octaves">倍频层数（每层波长 ÷lacunarity、振幅 ×gain；层数越多细节越碎）。</param>
		/// <param name="lacunarity">倍频因子（默认 2：每层波长减半）。</param>
		/// <param name="gain">振幅衰减（默认 0.5：每层振幅减半，收敛且无格感）。</param>
		public SphericalFbmNoise(int seed, float baseWavelengthKm, int octaves = 5,
			float lacunarity = 2f, float gain = 0.5f)
		{
			if (!(baseWavelengthKm > 0f))
				throw new ArgumentOutOfRangeException(nameof(baseWavelengthKm), "基波长须为正（km）");
			if (!(lacunarity > 1f))
				throw new ArgumentOutOfRangeException(nameof(lacunarity), "倍频因子须 > 1（否则细节不增反增粗）");
			if (!(gain > 0f && gain < 1f))
				throw new ArgumentOutOfRangeException(nameof(gain), "振幅衰减须 ∈ (0,1)（否则 fBm 不收敛）");

			_seed = seed;
			_octaves = Math.Clamp(octaves, 1, 12);
			_lacunarity = lacunarity;
			_gain = gain;
			_baseScale = WorldRadiusKm / baseWavelengthKm;
			double squareSum = 0, amplitudeSum = 0, amplitude = 1;
			for (int o = 0; o < _octaves; o++)
			{
				squareSum += amplitude * amplitude;
				amplitudeSum += amplitude;
				amplitude *= gain;
			}
			_normalization = (float)(1.0 / Math.Sqrt(squareSum));
			_ridgedNorm = (float)(1.0 / amplitudeSum);
		}

		/// <summary>单位方向处的 fBm 值（≈ [−1, 1]）。入参会在内部归一化，非单位向量也可传。</summary>
		public float Sample(Vector3 unitDirection)
		{
			Vector3 p = unitDirection.Normalized();
			float x = p.X * _baseScale, y = p.Y * _baseScale, z = p.Z * _baseScale;
			float sum = 0f, amplitude = 1f;
			for (int o = 0; o < _octaves; o++)
			{
				// 每层换一版哈希（否则各层在晶格上完全同相 → 叠加成"单倍频放大"而非分形）
				sum += amplitude * Perlin(x, y, z, _seed + o * 7919);
				x *= _lacunarity;
				y *= _lacunarity;
				z *= _lacunarity;
				amplitude *= _gain;
			}
			return sum * _normalization;
		}

		/// <summary>
		/// 单位方向处的山脊噪声（ridged fBm，≈[0, 1]；大值 = 脊线）。
		/// 每层先取 <c>r = (1 − |n|)²</c>：噪声穿过零线处取极大 ⇒ 成尖脊（普通 fBm 是圆丘）；
		/// 再乘前一层留下的门控 <c>weight ← clamp(2r, 0, 1)</c>：低处的细层被压低，脊线才连续成条。
		/// 门控是纯级联 ⇒ 形状由最粗一层独断，棱长而连续、棱距 ≈ 半个基波长；棱距不到 3 倍格距会混叠成条纹。
		/// 最后按振幅和归一（不是均方根）⇒ 输出落在 [0,1]，可直接与阈值比较。
		/// </summary>
		public float SampleRidged(Vector3 unitDirection)
		{
			Vector3 p = unitDirection.Normalized();
			float x = p.X * _baseScale, y = p.Y * _baseScale, z = p.Z * _baseScale;
			float sum = 0f, amplitude = 1f, weight = 1f;
			for (int o = 0; o < _octaves; o++)
			{
				float r = 1f - MathF.Abs(Perlin(x, y, z, _seed + o * 7919));
				r *= r;
				r *= weight;
				weight = MathF.Min(1f, r * 2f);
				sum += amplitude * r;
				x *= _lacunarity;
				y *= _lacunarity;
				z *= _lacunarity;
				amplitude *= _gain;
			}
			return sum * _ridgedNorm;
		}

		/// <summary>批量取值（同 <see cref="Sample"/> 逐元素调用；顺序无关）。</summary>
		public float[] SampleAll(Vector3[] unitDirections)
		{
			var values = new float[unitDirections.Length];
			for (int i = 0; i < values.Length; i++) values[i] = Sample(unitDirections[i]);
			return values;
		}

		// ── Perlin 3D（经典 12 向梯度表） ──

		static float Perlin(float x, float y, float z, int seed)
		{
			int xi = FastFloor(x), yi = FastFloor(y), zi = FastFloor(z);
			float xf = x - xi, yf = y - yi, zf = z - zi;
			float u = Fade(xf), v = Fade(yf), w = Fade(zf);

			float n000 = Corner(xi, yi, zi, seed, xf, yf, zf);
			float n100 = Corner(xi + 1, yi, zi, seed, xf - 1f, yf, zf);
			float n010 = Corner(xi, yi + 1, zi, seed, xf, yf - 1f, zf);
			float n110 = Corner(xi + 1, yi + 1, zi, seed, xf - 1f, yf - 1f, zf);
			float n001 = Corner(xi, yi, zi + 1, seed, xf, yf, zf - 1f);
			float n101 = Corner(xi + 1, yi, zi + 1, seed, xf - 1f, yf, zf - 1f);
			float n011 = Corner(xi, yi + 1, zi + 1, seed, xf, yf - 1f, zf - 1f);
			float n111 = Corner(xi + 1, yi + 1, zi + 1, seed, xf - 1f, yf - 1f, zf - 1f);

			float x00 = n000 + (n100 - n000) * u;
			float x10 = n010 + (n110 - n010) * u;
			float x01 = n001 + (n101 - n001) * u;
			float x11 = n011 + (n111 - n011) * u;
			float y0 = x00 + (x10 - x00) * v;
			float y1 = x01 + (x11 - x01) * v;
			return y0 + (y1 - y0) * w;
		}

		// 晶格角点贡献 = 该角点梯度 · 到角点的偏移向量。12 向梯度表（±1 分量、长 √2）是 Perlin 的经典选取：
		// 方向覆盖立方体 12 条棱，无轴向偏好 → 不出现轴对齐的条纹伪影。
		static float Corner(int x, int y, int z, int seed, float dx, float dy, float dz)
		{
			int g = Hash(x, y, z, seed) % 12;
			if (g < 0) g += 12;
			int baseIndex = g * 3;
			return GradientTable[baseIndex] * dx
				+ GradientTable[baseIndex + 1] * dy
				+ GradientTable[baseIndex + 2] * dz;
		}

		static readonly float[] GradientTable =
		{
			1f, 1f, 0f,  -1f, 1f, 0f,  1f, -1f, 0f,  -1f, -1f, 0f,
			1f, 0f, 1f,  -1f, 0f, 1f,  1f, 0f, -1f,  -1f, 0f, -1f,
			0f, 1f, 1f,  0f, -1f, 1f,  0f, 1f, -1f,  0f, -1f, -1f,
		};

		// 整数哈希（SplitMix 式尾混；不依赖 rng 状态 ⇒ 与调用顺序无关）。加法 / 乘法用 unchecked 溢出回绕，
		// 结果只取决于 (x, y, z, seed) 的位模式。
		static int Hash(int x, int y, int z, int seed)
		{
			unchecked
			{
				uint h = (uint)seed * 0x9E3779B1u;
				h ^= (uint)x * 0x85EBCA6Bu;
				h = (h << 13) | (h >> 19);
				h ^= (uint)y * 0xC2B2AE35u;
				h = (h << 17) | (h >> 15);
				h ^= (uint)z * 0x27D4EB2Fu;
				h ^= h >> 15;
				h *= 0x2545F491u;
				h ^= h >> 13;
				return (int)h;
			}
		}

		// 五次 fade：6t⁵ − 15t⁴ + 10t³（两阶导在晶格处为 0 → 场二阶连续，地形无折痕）
		static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

		static int FastFloor(float value)
		{
			int i = (int)value;
			return value < i ? i - 1 : i;
		}
	}
}
