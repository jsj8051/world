using Godot;
using System;
using System.Collections.Generic;
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（通用连续色带算法，复用不复制）

namespace World.NoiseWorld;

// 噪声世界地图模式（地图坞数据源）：每模式 = 逐格取色函数，View 烘颜色纹理时统一 SrgbToLinear。
// 数据源 = NoiseTerrain 的分层缓存（只读暴露口：ElevationM / Quantile / ContinentRaw / RidgeRaw /
// DetailRaw）；模式只读，不写任何状态。
// 色带表内聚各模式类（画面单一事实源）；两处自适应域（连续高度）经 BeginBake 在每次重烘前刷新。
// 取色纪律沿旧 new_HexWorld MapMode：ColorRamp.RampSampleSmooth 复用不复制，断点归右侧段。
public abstract class NoiseMapMode
{
	public abstract int Id { get; }            // 模式号（坞按钮下标 = 注册序）
	public abstract string Name { get; }       // 按钮文案单一事实源
	public abstract Color CellColorAt(int i);  // sRGB 意图色（视图烘纹理时统一转 linear）

	/// <summary>每次重烘颜色纹理前调用一次（自适应域模式在此刷新 min-max；O(n) 扫一遍可忽略）。</summary>
	public virtual void BeginBake() { }

	/// <summary>全部模式（注册序 = 坞按钮序 = Id；加模式 = 加一个类 + 场景 ModeRow 加一个按钮）。</summary>
	public static List<NoiseMapMode> CreateAll(NoiseTerrain terrain, NoisePlates plates, NoiseClimate climate) => new()
	{
		new ElevationBandMode(terrain),
		new ContinuousElevationMode(terrain),
		new QuantileMode(terrain),
		new ContinentFieldMode(terrain),
		new RidgeFieldMode(terrain),
		new DetailFieldMode(terrain),
		new PlateMode(plates),
		new ClimateTemperatureMode(climate),
		new ClimatePrecipMode(climate),
		new ClimateBiomeMode(climate),
	};

	/// <summary>发散色带（原始场共用：负 = 蓝、0 = 纸白、正 = 棕红）——L0 大陆场 / L3 细节层同表。</summary>
	public static readonly ColorStop[] DivergingStops =
	{
		new(-1f, new Color(0.14f, 0.28f, 0.52f)),   // 满 位（负侧）
		new(0f,  new Color(0.93f, 0.91f, 0.86f)),   // 中性（纸白）
		new(1f,  new Color(0.62f, 0.30f, 0.18f)),   // 满 位（正侧）
	};

	/// <summary>分位色带（topo 惯例：低分位深蓝 → 高分位红）。</summary>
	public static readonly ColorStop[] QuantileStops =
	{
		new(0f,    new Color(0.16f, 0.30f, 0.55f)),  // 深蓝（全球最低）
		new(0.35f, new Color(0.25f, 0.60f, 0.62f)),  // 青
		new(0.5f,  new Color(0.45f, 0.70f, 0.35f)),  // 绿（≈ 海陆分位线附近）
		new(0.7f,  new Color(0.88f, 0.78f, 0.30f)),  // 黄
		new(1f,    new Color(0.78f, 0.30f, 0.16f)),  // 红（全球最高）
	};
}

// 模式 0 海拔（默认）：9 档硬色阶，档界即等高线。原 NoiseBallView.ElevationColor 迁入
//（单一事实源跟着模式走；视图 BandName 委托本类，格信息面板口径不变）。
public sealed class ElevationBandMode : NoiseMapMode
{
	readonly float[] _elevation;
	public ElevationBandMode(NoiseTerrain t) : this(t.ElevationM) { }
	/// <summary>海拔源抽象为逐格数组：worldgen（世界生成空间）线复用同一色带与视图。</summary>
	public ElevationBandMode(float[] elevationM) => _elevation = elevationM;

	public override int Id => 0;
	public override string Name => "海拔";
	public override Color CellColorAt(int i) => ElevationColor(_elevation[i]);

	internal static Color ElevationColor(float m) => m switch
	{
		< -3000f => new Color(0.05f, 0.16f, 0.42f),   // 深海
		< -1000f => new Color(0.08f, 0.25f, 0.55f),   // 海洋
		< -200f  => new Color(0.15f, 0.40f, 0.68f),   // 浅海
		< 0f     => new Color(0.55f, 0.75f, 0.80f),   // 岸棚
		< 300f   => new Color(0.45f, 0.68f, 0.32f),   // 低地
		< 900f   => new Color(0.30f, 0.58f, 0.24f),   // 平原
		< 1800f  => new Color(0.38f, 0.50f, 0.26f),   // 高地
		< 2800f  => new Color(0.52f, 0.45f, 0.34f),   // 山地
		_        => new Color(0.94f, 0.94f, 0.96f),   // 雪线
	};

	/// <summary>海拔 → 档位名（与 ElevationColor 同一阈值表，判读口径一致）。</summary>
	public static string BandName(float m) => m switch
	{
		< -3000f => "深海",
		< -1000f => "大洋",
		< -200f => "浅海",
		< 0f => "岸棚",
		< 300f => "低地",
		< 900f => "平原",
		< 1800f => "高地",
		< 2800f => "山地",
		_ => "雪线",
	};
}

// 模式 1 连续高度：海拔实测 min-max 自适应灰阶，无分档量化——看梯度、噪声纹理本身与样条形状
//（调参判读主视图；分档模式看"地貌"，本模式看"场"）。
public sealed class ContinuousElevationMode : NoiseMapMode
{
	readonly NoiseTerrain _terrain;
	float _min, _max;

	public ContinuousElevationMode(NoiseTerrain t) => _terrain = t;

	public override int Id => 1;
	public override string Name => "连续高度";

	public override void BeginBake()
	{
		_min = float.MaxValue; _max = float.MinValue;
		foreach (float e in _terrain.ElevationM)
		{
			if (e < _min) _min = e;
			if (e > _max) _max = e;
		}
		if (_max <= _min) { _min = 0f; _max = 1f; }   // 全平退化域
	}

	public override Color CellColorAt(int i)
	{
		float t = (_terrain.ElevationM[i] - _min) / (_max - _min);
		float g = 0.06f + 0.90f * t;                  // 黑（最低）→ 白（最高），留边防纯黑/纯白
		return new Color(g, g, g);
	}
}

// 模式 2 秩场：域内秩（带符号 ∈[-1,1]；陆 + / 洋 −，身份定侧）——每域内地形起伏的"源场"长相，
// 与海拔模式对照可判读"哪些噪声结构长成了山/洋盆"。
public sealed class QuantileMode : NoiseMapMode
{
	readonly NoiseTerrain _terrain;
	public QuantileMode(NoiseTerrain t) => _terrain = t;

	public override int Id => 2;
	public override string Name => "秩场";
	public override Color CellColorAt(int i)
		=> RampSampleSmooth(QuantileStops, (_terrain.DomainRank[i] + 1f) * 0.5f);   // [-1,1] → [0,1]
}

// 模式 3 L0 大陆场：低频 fBm 原始值 ≈[-1,1]（扭曲坐标上采样）——大陆斑块的"源场"长相比。
public sealed class ContinentFieldMode : NoiseMapMode
{
	readonly NoiseTerrain _terrain;
	public ContinentFieldMode(NoiseTerrain t) => _terrain = t;

	public override int Id => 3;
	public override string Name => "L0 大陆";
	public override Color CellColorAt(int i) => RampSampleSmooth(DivergingStops, _terrain.ContinentRaw[i]);
}

// 模式 4 L2 山系带：ridged 整形值 (1−|n|)^RidgePower ∈[0,1]（与段3合成用同一式）——山链在哪、
// 脊线多锐。灰阶：黑 = 无山，白 = 脊线。
public sealed class RidgeFieldMode : NoiseMapMode
{
	readonly NoiseTerrain _terrain;
	public RidgeFieldMode(NoiseTerrain t) => _terrain = t;

	public override int Id => 4;
	public override string Name => "L2 山带";

	public override Color CellColorAt(int i)
	{
		float shaped = MathF.Pow(1f - MathF.Abs(_terrain.RidgeRaw[i]), _terrain.Params.RidgePower);
		float g = 0.05f + 0.92f * shaped;
		return new Color(g, g, g);
	}
}

// 模式 5 L3 细节层：细节 fBm 原始值 ≈[-1,1]——碎质感的源场（海岸摆动/米域细节共用此场）。
public sealed class DetailFieldMode : NoiseMapMode
{
	readonly NoiseTerrain _terrain;
	public DetailFieldMode(NoiseTerrain t) => _terrain = t;

	public override int Id => 5;
	public override string Name => "L3 细节";
	public override Color CellColorAt(int i) => RampSampleSmooth(DivergingStops, _terrain.DetailRaw[i]);
}

// 模式 6 板块：NoisePlates 划分的板块归属（抽种子 + 加权随机生长，洪泛家族随机填充变体）→
// 板号 HSV 均布色相纯色铺满（原 PlateMapMode 同式）；板缘 = 色相突变自然可读。
public sealed class PlateMode : NoiseMapMode
{
	readonly NoisePlates _plates;
	public PlateMode(NoisePlates p) => _plates = p;

	public override int Id => 6;
	public override string Name => "板块";
	public override Color CellColorAt(int cellIndex)
		=> Color.FromHsv(_plates.PlateOfCell[cellIndex] / (float)_plates.NumPlates, 0.8f, 0.9f);
}

// 模式 7 温度：年均温连续色带（−35..+40°C）——纬度基准 + 海拔直减 + 大陆性噪声（NoiseClimate）。
public sealed class ClimateTemperatureMode : NoiseMapMode
{
	readonly NoiseClimate _climate;
	public ClimateTemperatureMode(NoiseClimate c) => _climate = c;

	public override int Id => 7;
	public override string Name => "温度";
	public override Color CellColorAt(int i)
		=> RampSampleSmooth(TempStops, _climate.TemperatureC[i]);

	/// <summary>温度色带（位置 = °C，升序）：深蓝（极寒）→ 蓝 → 青（冰点）→ 绿（温凉）→ 黄（暖）→ 红（炎热）。</summary>
	public static readonly ColorStop[] TempStops =
	{
		new(-35f, new Color(0.08f, 0.12f, 0.45f)),
		new(-15f, new Color(0.15f, 0.30f, 0.65f)),
		new(0f,   new Color(0.30f, 0.55f, 0.75f)),
		new(10f,  new Color(0.35f, 0.70f, 0.45f)),
		new(20f,  new Color(0.85f, 0.80f, 0.30f)),
		new(30f,  new Color(0.90f, 0.50f, 0.20f)),
		new(40f,  new Color(0.85f, 0.20f, 0.12f)),
	};
}

// 模式 8 降水：年降水连续色带（0..3000mm）——ITCZ/极锋/副高 + 盛行风/雨影（NoiseClimate）。
public sealed class ClimatePrecipMode : NoiseMapMode
{
	readonly NoiseClimate _climate;
	public ClimatePrecipMode(NoiseClimate c) => _climate = c;

	public override int Id => 8;
	public override string Name => "降水";
	public override Color CellColorAt(int i)
		=> RampSampleSmooth(PrecipStops, _climate.PrecipMmYear[i]);

	/// <summary>降水色带（位置 = mm/yr，升序）：黄（干旱）→ 绿 → 青 → 蓝（多雨）。</summary>
	public static readonly ColorStop[] PrecipStops =
	{
		new(0f,    new Color(0.85f, 0.75f, 0.40f)),
		new(500f,  new Color(0.60f, 0.75f, 0.35f)),
		new(1000f, new Color(0.25f, 0.60f, 0.35f)),
		new(2000f, new Color(0.15f, 0.50f, 0.60f)),
		new(3000f, new Color(0.10f, 0.35f, 0.70f)),
	};
}

// 模式 9 生物群系：简化柯本分类色块（温度 × 降水，NoiseClimate.BiomeFor）。
public sealed class ClimateBiomeMode : NoiseMapMode
{
	readonly NoiseClimate _climate;
	public ClimateBiomeMode(NoiseClimate c) => _climate = c;

	public override int Id => 9;
	public override string Name => "生物群系";
	public override Color CellColorAt(int i) => NoiseClimate.BiomeColors[_climate.Biome[i]];
}
