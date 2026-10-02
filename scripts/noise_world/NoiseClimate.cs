using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3/Color 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;     // Ball（H3 球壳数据层）
using World.Utils;           // SphericalFbmNoise / DeterministicRandom
using World.Utils.H3;

namespace World.NoiseWorld;

// 气候层（D 派生；自老树 ClimateGenerator/WindField/BiomeClassifier 迁移到 H3 噪声世界，2026-10-02）：
//   温度 = 纬度基准（52cos^1.1 − 22，赤道 ≈+30 / 极地 ≈−22）+ 大陆性噪声 ±7°C + 海拔直减 −6°C/km。
//   风   = 三圈环流（信风/西风/极地东风）+ 科里奥利偏转（WindField 公式原样；顺转地球式 ×1.0）。
//   降水 = 纬度带（ITCZ σ12° ×1400 + 60° 极锋 ×550 + 26° 副高压制 0.85）× 噪声 ±12%
//          × 盛行风湿润度 ±55%（上风向 10 点指数衰减采样，L≈0.15rad）× 雨影 ±65%。
//   生物群系 = 年均温 × 年降水简化柯本（年度口径；月季风/雨季干季为后续月化步骤）。
// 海陆判定 = NoiseTerrain 海拔符号（身份定海陆，"是什么就是什么"）。
// 确定性：全部随机走 SphericalFbmNoise（同种子同方向逐位同）⇒ 同 seed 同网格逐位同。
public sealed class NoiseClimate
{
	// ── 生物群系表（byte id → 名称/颜色；地图模式与格信息单一事实源）──
	public const byte BiomeOcean = 0;
	public const byte BiomeTropicalOcean = 1;
	public const byte BiomeFrigidOcean = 2;
	public const byte BiomeIceCap = 3;
	public const byte BiomeTundra = 4;
	public const byte BiomeHotDesert = 5;
	public const byte BiomeColdDesert = 6;
	public const byte BiomeSavanna = 7;
	public const byte BiomeSteppe = 8;
	public const byte BiomeBorealForest = 9;
	public const byte BiomeTemperateForest = 10;
	public const byte BiomeTropicalSeasonal = 11;
	public const byte BiomeTropicalRainforest = 12;

	public static readonly string[] BiomeNames =
	{
		"海洋", "热带海洋", "海冰", "冰原", "苔原", "热带荒漠", "寒带荒漠",
		"稀树草原", "温带草原", "针叶林", "温带森林", "热带季风林", "热带雨林",
	};

	public static readonly Color[] BiomeColors =
	{
		new(0.13f, 0.28f, 0.52f),   // 海洋
		new(0.05f, 0.35f, 0.65f),   // 热带海洋
		new(0.75f, 0.85f, 0.88f),   // 海冰
		new(0.95f, 0.96f, 0.99f),   // 冰原
		new(0.65f, 0.70f, 0.62f),   // 苔原
		new(0.90f, 0.78f, 0.45f),   // 热带荒漠
		new(0.75f, 0.68f, 0.52f),   // 寒带荒漠
		new(0.78f, 0.82f, 0.40f),   // 稀树草原
		new(0.65f, 0.75f, 0.42f),   // 温带草原
		new(0.22f, 0.42f, 0.30f),   // 针叶林
		new(0.28f, 0.55f, 0.28f),   // 温带森林
		new(0.45f, 0.68f, 0.25f),   // 热带季风林
		new(0.13f, 0.48f, 0.18f),   // 热带雨林
	};

	public const float SeaIceTempC = -2f;        // 海水冰点（海冰档阈值）
	public const float TropicalMinTempC = 18f;   // 热带档阈值
	public const float LapseCPerKm = 6.0f;       // 海拔直减率（用户标定）

	float[] _temperatureC = Array.Empty<float>();
	float[] _precipMmYear = Array.Empty<float>();
	Vector3[] _windDir = Array.Empty<Vector3>();
	byte[] _biome = Array.Empty<byte>();

	/// <summary>年均温（°C；洋格 = 海面温度）。</summary>
	public float[] TemperatureC => _temperatureC;
	/// <summary>年降水（mm/yr）。</summary>
	public float[] PrecipMmYear => _precipMmYear;
	/// <summary>盛行风向（切向单位向量，指向下风向）。</summary>
	public Vector3[] WindDir => _windDir;
	/// <summary>生物群系 id（NoiseClimate.Biome* 常量；地图着色/格信息用）。</summary>
	public byte[] Biome => _biome;

	/// <summary>全量重算（依赖：地形海拔 + 球壳方向；elev 变了温度/降水随之重算）。</summary>
	public void Generate(Ball ball, NoiseTerrain terrain, int seed)
	{
		int n = ball.CellIds.Length;
		if (_temperatureC.Length != n) _temperatureC = new float[n];
		if (_precipMmYear.Length != n) _precipMmYear = new float[n];
		if (_windDir.Length != n) _windDir = new Vector3[n];
		if (_biome.Length != n) _biome = new byte[n];

		var dirs = ball.CellDirs;
		var elevArr = terrain.ElevationM;
		var tempNoise = new SphericalFbmNoise(seed + 101, 9000f, 2);   // 大陆尺度温度噪声
		var precipNoise = new SphericalFbmNoise(seed + 102, 4000f, 2); // 降水调制噪声

		// 格 id → 下标（雨影/湿润度沿任意方向采样海拔用）
		var idx = new Dictionary<ulong, int>(n);
		for (int i = 0; i < n; i++) idx[ball.CellIds[i]] = i;
		float SampleElevM(Vector3 dir)
		{
			var cell = H3.LatLngToCell(new LatLng(Math.Asin(Math.Clamp(dir.Y, -1.0, 1.0)),
				Math.Atan2(dir.Z, dir.X)), ball.Res);
			return idx.TryGetValue(cell, out int i) ? elevArr[i] : 0f;
		}

		for (int i = 0; i < n; i++)
		{
			Vector3 dir = dirs[i];
			float lat = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
			float latDeg = MathF.Abs(lat * 180f / MathF.PI);
			float cosLat = MathF.Max(0f, MathF.Cos(lat));              // 极点下限 0（防 cos(asin(1)) 略负 → NaN）
			float elevM = elevArr[i];
			bool isLand = elevM > 0f;

			// ── 温度：纬度基准 + 大陆性噪声 ±7°C + 海拔直减 −6°C/km ──
			float baseT = 52f * MathF.Pow(cosLat, 1.1f) - 22f;         // 赤道 ≈+30 / 极地 ≈−22
			baseT += tempNoise.Sample(dir) * 7f;
			float elevKm = MathF.Max(0f, elevM) / 1000f;
			_temperatureC[i] = baseT - LapseCPerKm * elevKm;

			// ── 风向：三圈环流 + 科里奥利偏转（WindField.WindAt 原样）──
			_windDir[i] = WindAt(dir);

			// ── 降水：纬度带骨架 × 噪声 × 抬升 × 盛行风/雨影（陆格）──
			float equatorBand = 1400f * MathF.Exp(-latDeg * latDeg / (2f * 12f * 12f));      // ITCZ σ=12°
			float polarFront = 550f * MathF.Exp(-(latDeg - 62f) * (latDeg - 62f) / (2f * 14f * 14f));
			float subtropical = 1f - 0.85f * MathF.Exp(-(latDeg - 26f) * (latDeg - 26f) / (2f * 9f * 9f));
			float precip = (60f + equatorBand + polarFront) * subtropical;
			precip *= 1f + precipNoise.Sample(dir) * 0.12f;              // 噪声 ±12%（小扰动）
			if (isLand)
				precip *= 1f + MathF.Min(elevM / 10000f, 1f) * 0.4f;     // 地形抬升增雨

			if (isLand)
			{
				// 盛行风湿润度（±55%）：上风向 10 点指数衰减采样，海洋贡献占比
				float maritime = MaritimeScore(dir, dir => SampleElevM(dir) < 0f);
				precip *= 1f + maritime * 0.55f;

				// 雨影（±65%）：下风向海拔 > 上风向 ⇒ 风爬坡增雨；反之背风减雨
				Vector3 wind = _windDir[i];
				float windComp = (SampleElevM((dir + wind * 0.12f).Normalized())       // 下风向
					- SampleElevM((dir - wind * 0.12f).Normalized())) * 5f;
				precip *= 1f + Math.Clamp(windComp, -0.65f, 0.65f);
			}
			else
			{
				precip *= 1.55f;                               // 洋格 = 水汽源（湿润顶格；跳过逐点采样）
			}
			_precipMmYear[i] = MathF.Max(0f, precip);

			// ── 生物群系：年均温 × 年降水简化柯本 ──
			_biome[i] = BiomeFor(_temperatureC[i], _precipMmYear[i], isLand);
		}
	}

	/// <summary>生物群系判定（年度简化柯本；月季风/干湿季为后续月化步骤）。</summary>
	public static byte BiomeFor(float tempC, float precipMm, bool isLand)
	{
		if (!isLand)
		{
			if (tempC < SeaIceTempC) return BiomeFrigidOcean;
			return tempC >= TropicalMinTempC ? BiomeTropicalOcean : BiomeOcean;
		}
		if (tempC < -10f) return BiomeIceCap;                  // 冰原（极地/高山雪线以上自然涌现）
		if (tempC < 0f) return BiomeTundra;                    // 苔原
		if (precipMm < 250f) return tempC >= 18f ? BiomeHotDesert : BiomeColdDesert;
		if (precipMm < 500f) return tempC >= 20f ? BiomeSavanna : BiomeSteppe;
		if (tempC < 5f) return BiomeBorealForest;              // 针叶林（寒温带）
		if (tempC < 15f) return BiomeTemperateForest;          // 温带森林
		if (precipMm < 1500f) return BiomeTropicalSeasonal;    // 热带季风林/稀树草原过渡
		return BiomeTropicalRainforest;                        // 热带雨林（ITCZ 带）
	}

	// ── 风场（WindField.WindAt 原样迁移；顺转地球式 ×1.0 标定）──

	/// <summary>球面点 → 盛行风向（切向单位向量，指向下风向）。三圈环流 + 科里奥利偏转。</summary>
	public static Vector3 WindAt(Vector3 dir)
	{
		dir = dir.Normalized();
		float lat = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
		bool north = lat >= 0f;
		float a = MathF.Abs(lat * 180f / MathF.PI);

		// 切向基：东 = 经度增加方向，北 = 纬度增加方向
		var east = new Vector3(-dir.Z, 0f, dir.X).Normalized();
		var northDir = dir.Cross(east).Normalized();

		// 环流水平分量：Hadley(0-30°)→赤道，Ferrel(30-60°)→极地，Polar→赤道
		float towardPole = a < 30f ? -1f : a < 60f ? 1f : -1f;
		float mer = north ? towardPole : -towardPole;          // 南北半球镜像

		// 科里奥利偏转：北半球顺转 → 向极地东偏（西风带）、向赤道西偏（东北信风）；
		// 南半球左偏。偏转强度 = sin|lat| × 速度^0.7（亚线性标定，速度 = 1.0 地球档）。
		float coriolisHemi = north ? 1f : -1f;
		float deflect = MathF.Sin(MathF.Abs(lat));             // × RotationSpeed^0.7（1.0 档）
		var wind = northDir * mer + east * (mer * deflect * coriolisHemi);
		return wind.LengthSquared() > 1e-9f ? wind.Normalized() : east;
	}

	/// <summary>风"从海洋来"的程度：−1 纯大陆风（干）~ +1 纯海洋风（湿）。
	/// 沿上风向 10 点指数衰减采样（L≈0.15rad，地球档），海洋点按权重贡献。</summary>
	public float MaritimeScore(Vector3 dir, System.Func<Vector3, bool> isOcean)
	{
		var wind = WindAt(dir);
		float L = 0.15f;                                       // 水汽衰减尺度（rad；地球 1× 档）
		float range = Math.Clamp(3f * L, 0.25f, 0.9f);
		const int M = 10;
		float sumW = 0f, sumOcean = 0f;
		for (int i = 1; i <= M; i++)
		{
			float d = range * i / M;
			Vector3 up = (dir - wind * d).Normalized();        // 上风向
			float w = MathF.Exp(-d / L);
			sumW += w;
			if (isOcean(up)) sumOcean += w;
		}
		float score = sumW > 1e-9f ? sumOcean / sumW : 0f;
		return (score - 0.5f) * 2f;
	}
}
