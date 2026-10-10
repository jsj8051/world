using System;
using Godot;                 // Vector3（纯结构体，不需要引擎运行时；测试宿主可用）

namespace World.Utils
{
	// 球面 3D 噪声 + fBm 分形（纯 C#，**零引擎依赖**）：给世界生成提供连续、各向同性、无缝的起伏基场
	// （初始地壳的陆地/洋底起伏用它铺；将来山带/矿脉/区域扰动也可复用）。
	//
	// 结构：Perlin 梯度噪声（晶格角点整数哈希 → 12 向梯度表 → 五次 fade → 三线性插值）
	//       → fBm 倍频叠加（lacunarity 2 / gain 0.5，**按振幅平方和的均方根归一**）。
	//       值域 ≈ [−1, 1]，均值 ≈ 0，分布近高斯（σ ≈ 0.29；实测峰值 ≈ 1.1 × 标称幅度——res3/seed42 洋底起伏
	//       实测 +396 m / 标称 350 m；理论极值上限 1.45）。
	//
	// ⚠️ 归一化为什么用 **√Σgain²** 而不是 Σgain（两者都常见）：按振幅**和**归一时，每加一层
	//   倍频，全场振幅就被多除一次（各层峰不重合，是均方叠加）——6 层场只剩 σ 的 0.6 倍，
	//   "层数"这个旋钮会偷偷改振幅，同一个"起伏半幅"在不同层数下含义不同。均方根归一让
	//   **振幅与层数解耦**：层数只加细节（斜率/粗糙度涨 ≈ √层数），振幅旋钮在任何层数下同义。
	//
	// ⚠️ 为什么用 **3D** 噪声采样单位球方向（而不是经纬 2D 噪声）：
	//   球面没有边界——2D 经纬噪声在 ±180° 经线处有接缝、在两极被拉伸；3D 噪声只在 R³ 里取值，
	//   球面只是它的一张切片，天然无缝、无极点畸变。这也正是 fBm 在球上"接得干净"的唯一做法。
	//
	// 频率口径 = **基波长（km）**（项目纪律：以物理尺度给参数，不给"噪声频率"这种无量纲魔数）：
	//   采样点 = 单位方向 × (R_earth / 基波长)，于是噪声空间里相邻两个"特征"相隔 1 个单位
	//   ⇒ 球面上相邻两个特征相隔 ≈ 基波长 km。换 res / 换半径不改场，只改采样密度。
	//
	// ⚠️ 为什么自写而不用 Godot 的 FastNoiseLite（同 SphericalBlobNoise 的教训，2026-09-11 实测）：
	//   它是**引擎类型**，纯 .NET 测试宿主里构造会 access violation 崩掉整个测试进程；逻辑层的
	//   单测纪律要求"无引擎宿主也能跑"。
	//
	// 确定性：晶格整数哈希 + 每倍频种子偏移，**不消耗任何 rng、与调用顺序无关**——
	// 同种子同方向逐位一致（同一方向可在任意时刻重复采样）。
	public sealed class SphericalFbmNoise
	{
		/// <summary>基准半径（km）。与 `H3PlateMotion.EarthRadiusKm` 同值；本类零引擎/零业务依赖，
		/// 不反向引用板块模块，故自带一份常量（改其一须同步）。</summary>
		public const float EarthRadiusKm = 6371f;

		readonly int _seed;
		readonly int _octaves;
		readonly float _lacunarity;
		readonly float _gain;
		readonly float _baseScale;       // 单位方向 → 噪声空间半径 = R_earth / 基波长
		readonly float _normalization;   // 1 / √Σgain²ⁱ（均方根归一：振幅与层数解耦）

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
			_baseScale = EarthRadiusKm / baseWavelengthKm;
			double squareSum = 0, amplitude = 1;
			for (int o = 0; o < _octaves; o++) { squareSum += amplitude * amplitude; amplitude *= gain; }
			_normalization = (float)(1.0 / Math.Sqrt(squareSum));
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

		/// <summary>批量取值（同 <see cref="Sample"/> 逐元素调用；顺序无关）。</summary>
		public float[] SampleAll(Vector3[] unitDirections)
		{
			var values = new float[unitDirections.Length];
			for (int i = 0; i < values.Length; i++) values[i] = Sample(unitDirections[i]);
			return values;
		}

		// ── Perlin 3D（经典 12 向梯度表；五次 fade 保证二阶导连续 —— 地形场不要有折痕）──

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

		// 晶格角点贡献 = 该角点梯度 · 到角点的偏移向量。12 向梯度表（±1 分量、长 √2）是 Perlin
		// 经典选取：张成的方向覆盖立方体的 12 条棱，无轴向偏好 → 不会出现轴对齐的条纹伪影。
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

		// 整数哈希（SplitMix 式尾混；不依赖 rng 状态 ⇒ 与调用顺序无关）。加法/乘法用 unchecked
		// 溢出回绕，结果只取决于 (x, y, z, seed) 的位模式。
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
