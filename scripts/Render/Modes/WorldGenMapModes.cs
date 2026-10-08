using Godot;
using System;
using System.Collections.Generic;
using World.Render;                // MapMode（决策 08 §4.4 表现层保留资产）
using static World.Utils.ColorRamp;        // ColorStop / RampSampleSmooth（取色方式，复用不复制）

namespace World.WorldGen;

// 世界生成空间 · 地图模式表（地图坞数据源）。
//
// ★设计原则（2026-10-07 地图坞契约拍板）：
//   地图模式 = 一个可独立判读的世界字段/分类事实的视觉投影，必须满足四件事：
//   ① 有明确世界语义 ② 有稳定数据源 ③ 不反向依赖生成过程 ④ 适合逐格解释。
//   不是"把所有内部数据可视化一遍"——RiverGeometry 等线状符号走 Overlay（RiverLineOverlay），
//   CivSim 势力等未稳定语义不进坞（有数据 ≠ 应该成为地图语义）。
//
// 注册序 = 坞按钮序 = Id，按判读链排布：自然地理 → 气候 → 生成诊断（垫底）。
//   ★"诊断垫底、不与世界语义混排"这个约定**只靠本注册顺序 + 注释维持**（原先有 `MapModeGroup`
//   成员承载，因无生产消费者已删，2026-10-08）；新增诊断模式请继续排到表末尾并注明。
//   判读链：海拔 → 地质区域 → 离岸距离 → 降水 → 温度 →（P4-4 后土壤）→（P4-3b 后 Biome）——
//   各模式只读**各自的生产事实**，坞内不做模式间互相计算。
//   ★季节气候/洋流/风场/水汽/耦合降水模式已随三大体系删除（2026-10-07 用户拍板全部删除
//   重做）；温度已重做 v1（2026-10-08 逐格能量收支）并**按新事实重新注册**（Id=4）。
//   ⚠️ 新增模式必须同步 `scenes/render/MapDock.tscn` 的 ModeRow 按钮（数量不符 BindModes 即抛）。
//
// ★色带纪律：物理固定域，**禁止 min-max 自动拉伸**（否则跑 100 个世界互不可比）；
//   长尾量（降水）允许"对数式显示变换"——只变换显示采样，**数据本身不改、生产公式不动**。
// 模式持 WorldGenPlanet 引用、CellColorAt 时**现取**当前数据——Regenerate 会换数组实例，
//   持实例引用会读到旧场（读属性不存数组引用）。
// 海格统一深蓝底（与海拔色带深海档同色，画面连续）。
public static class WorldGenMapModes
{
	/// <summary>全部模式（注册序 = 坞按钮序 = Id）。「大陆」势力图已删（2026-10-02 用户拍板）：
	///   锚点 Voronoi 归属是连续场内部塑形判据，不是地图语义——地图语义 = 陆块连通分量（投影层）。</summary>
	public static List<MapMode> CreateAll(WorldGenPlanet planet) => new()
	{
		new ElevationMode(planet),        // 0 自然地理
		new RegionTypeMode(planet),       // 1 自然地理
		new CoastDistanceMode(planet),    // 2 自然地理
		new PrecipitationMode(planet),    // 3 气候
		new TemperatureMode(planet),      // 4 气候
		new LandSeaMode(planet),          // 5 生成诊断（垫底）
	};

	const float OceanR = 0.05f, OceanG = 0.16f, OceanB = 0.42f;   // 深海底色（= ElevationColor 深海档）

	// ── 降水色带（Sequential · 固定物理域 0–3000 mm/年 · 对数式显示变换）──
	// 长尾分布：线性域会让湿润区挤在色带一端；对数变换只作用于**显示采样**（mm → [0,1]），
	// 停点位置仍按物理量落位，跨世界可比。数据与生产公式零改动。

	const float PrecipDomainMm = 3000f;   // 固定物理域上界（全球实测均 1,106 mm/年；>3000 夹末色）
	const float PrecipLogK = 50f;         // 对数软化常数（t = ln(1+p/K) / ln(1+P/K)）

	/// <summary>降水 mm → [0,1] 显示归一（对数式；负值夹 0、超域夹 1）。仅显示变换，不回写数据。</summary>
	public static float PrecipNorm(float mm) => System.Math.Clamp(
		MathF.Log(1f + MathF.Max(mm, 0f) / PrecipLogK) / MathF.Log(1f + PrecipDomainMm / PrecipLogK),
		0f, 1f);

	/// <summary>降水色带（位置 = PrecipNorm(物理 mm)，干沙 → 枯黄 → 草绿 → 深绿 → 青 → 深蓝）。</summary>
	public static readonly ColorStop[] PrecipStops =
	{
		new(PrecipNorm(0),    new Color(0.85f, 0.72f, 0.42f)),   // 干沙（极端干旱）
		new(PrecipNorm(150),  new Color(0.80f, 0.73f, 0.38f)),   // 枯黄（荒漠/草原过渡）
		new(PrecipNorm(400),  new Color(0.60f, 0.72f, 0.36f)),   // 草黄绿（草原）
		new(PrecipNorm(900),  new Color(0.30f, 0.62f, 0.36f)),   // 绿（疏林/季雨）
		new(PrecipNorm(1800), new Color(0.16f, 0.46f, 0.56f)),   // 青（雨林边缘）
		new(PrecipNorm(3000), new Color(0.10f, 0.22f, 0.58f)),   // 深蓝（极端湿润）
	};

	// ── 温度色带（Sequential · 固定物理域 −40…+90 °C · 线性，无显示变换）──
	// ★为什么本模式不直接复用 Domain.BiomeColors.TempStops：那张色带的域是 **−85…+45 °C**
	//   （地球尺度，供未来 Biome 用）；而本模型是"理想黑体无温室"，赤道 ≈ +87 °C、全球均
	//   +76 °C ⇒ 用 −85…45 会把纬度 < 56° 全部夹到红色，判读不出梯度。故**地图专用色带**独立
	//   定义在此（与 PrecipStops 同构：地图色带归地图模式），BiomeColors 保持不动。
	//   域端固定 ⇒ 跨世界可比（禁 min-max 自动拉伸），超域由 RampSampleSmooth 夹端色。

	/// <summary>固定物理域下界（°C）。★取 −40 而非模型理论下界 −273：`cos^0.25` 极平坦 ⇒
	/// 陆温集中 +0…+87（|纬度|&lt;70°），若域下界取 −90，色带有一半浪费在几乎无格的极端区间
	/// ⇒ 整图挤在暖端（实测）。更冷者夹本域首色（极区小面积，夹色可接受、如实）。</summary>
	public const float TempDomainMinC = -40f;
	/// <summary>固定物理域上界（°C；模型赤道 ≈ +87）。</summary>
	public const float TempDomainMaxC = 90f;

	/// <summary>温度色带（位置 = 物理 °C，严寒深蓝 → 冷蓝 → 温和绿 → 暖黄绿 → 热橙 → 酷热深红）。
	/// ★停点按模型实际分布布点：`T_eq = 360 K · cos(φ)^0.25` 极平坦 ⇒ 纬度 60° 仍有 +30 °C，
	///   陆温集中 **+0…+87**（|纬度|&lt;70°）⇒ 该区间放 5 个停点，否则整图只落色带上段（实测全红）。</summary>
	public static readonly ColorStop[] TemperatureStops =
	{
		new(-40f, new Color(0.08f, 0.12f, 0.42f)),   // 严寒（深蓝；更低夹此色）
		new(-10f, new Color(0.16f, 0.38f, 0.68f)),   // 冷（蓝）
		new(20f,  new Color(0.34f, 0.70f, 0.44f)),   // 温和（绿）
		new(45f,  new Color(0.76f, 0.76f, 0.28f)),   // 暖（黄绿）
		new(70f,  new Color(0.92f, 0.56f, 0.18f)),   // 热（橙）
		new(90f,  new Color(0.78f, 0.16f, 0.12f)),   // 酷热（深红）
	};

	// ── 发散色带（原始连续场共用 · 海陆场诊断模式用）──
	// ★沿革：原挂在抽象基类 `MapMode` 上（B 线 `NoiseMapMode` "基类 + 两色带常量"直接搬入的
	//   遗留）；2026-10-08 归位——**基类只装契约，色带归消费它的模式**（与 PrecipStops /
	//   TemperatureStops 同构：色带内聚本文件）。取色方式统一由 `World.Utils.ColorRamp` 提供。

	/// <summary>发散色带（负 = 蓝 / 0 = 纸白 / 正 = 棕红；域 −1…+1）。
	/// 消费者 = <see cref="LandSeaMode"/>：把 `Projector.Raw` 除以 0.8 归一到色带域后取色。</summary>
	public static readonly ColorStop[] DivergingStops =
	{
		new(-1f, new Color(0.14f, 0.28f, 0.52f)),   // 满 位（负侧）
		new(0f,  new Color(0.93f, 0.91f, 0.86f)),   // 中性（纸白）
		new(1f,  new Color(0.62f, 0.30f, 0.18f)),   // 满 位（正侧）
	};

	// ── 海拔分档色/名（9 档硬色阶 · 档界即等高线）──
	// ★沿革：原为独立的 `Render/ElevationBandMode`（`: MapMode`，B 线迁移资产）。2026-10-08 解耦后
	//   Render 侧已无消费者、且全仓无人再实例化它（BallView 是最后一个）⇒ 其 `MapMode` 面成死代码，
	//   遂撤销该类，只把**色带 + 档位名两张表**并入海拔模式所在类（与 `PrecipNorm` 同构：模式专属
	//   派生表作为本类 public static 成员）。两表共用同一阈值表 ⇒ 画面与文字口径恒一致。

	/// <summary>海拔 → 分档色（9 档硬色阶，含海陆；档界即等高线）。海拔模式的**陆地**取色来源。</summary>
	public static Color ElevationColor(float m) => m switch
	{
		< -3000f => new Color(0.05f, 0.16f, 0.42f),   // 深海
		< -1000f => new Color(0.08f, 0.25f, 0.55f),   // 海洋
		< -200f => new Color(0.15f, 0.40f, 0.68f),   // 浅海
		< 0f => new Color(0.55f, 0.75f, 0.80f),   // 岸棚
		< 300f => new Color(0.45f, 0.68f, 0.32f),   // 低地
		< 900f => new Color(0.30f, 0.58f, 0.24f),   // 平原
		< 1800f => new Color(0.38f, 0.50f, 0.26f),   // 高地
		< 2800f => new Color(0.52f, 0.45f, 0.34f),   // 山地
		_ => new Color(0.94f, 0.94f, 0.96f),   // 雪线
	};

	/// <summary>海拔 → 档位名（与 <see cref="ElevationColor"/> 同一阈值表）。消费者 = 宿主装配层
	/// （<c>WorldGenManager</c> 取此喂格信息卡；信息卡为哑组件，不认识海拔语义）。</summary>
	public static string ElevationBandName(float m) => m switch
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

	// ── 模式实现 ──────────────────────────────────────────────

	/// <summary>海拔：FinalHeight（Composer.HeightM 单一表面真相）→ DisplayElevation（显示层别名，
	/// 同一数组非第二份数据）→ <see cref="ElevationColor"/>。色带/档位名单一事实源 = 上方两表。</summary>
	sealed class ElevationMode : MapMode
	{
		readonly WorldGenPlanet _p;
		public ElevationMode(WorldGenPlanet p) => _p = p;
		public override int Id => 0;
		public override string Name => "海拔";
		public override Color CellColorAt(int i) =>
			_p.Final.FinalLand[i]
				? ElevationColor(_p.DisplayElevation[i])   // 陆 = 合成海拔分档（HeightComposer 唯一出处）
				: new Color(OceanR, OceanG, OceanB);
	}

	/// <summary>【生成诊断】海陆结构场原值（连续 raw）：发散色带（负 = 蓝 / 0 ≈ 海岸线 = 纸白 / 正 = 棕红）。
	/// 回答的是"生成器的原始连续场长什么样"（Raw Projector → Feature → FinalHeight → FinalLandMask
	/// 链路取证用），**不是**最终海陆事实——那由 FinalHeight > 0 / FinalLandMask 表达（海拔/区域模式已覆盖）。
	/// 故注册序垫底（诊断垫底约定靠 CreateAll 表序维持），不与世界语义混排。
	/// 色带 = 本文件 `DivergingStops`（原挂 `MapMode` 基类，2026-10-08 随色带归属收口移入）。</summary>
	sealed class LandSeaMode : MapMode
	{
		readonly WorldGenPlanet _p;
		public LandSeaMode(WorldGenPlanet p) => _p = p;
		public override int Id => 5;
		public override string Name => "海陆场";
		public override string ScaleCaption => "生成诊断 · Projector.Raw（非世界事实）";
		public override Color CellColorAt(int i) => RampSampleSmooth(DivergingStops, _p.Projector.Raw[i] / 0.8f);
	}

	/// <summary>地质区域类型（七类固定色；主判读图——用户拍板的地图语义）。
	/// 只做"地理结构分类"，不混入海拔/温度/植被/土壤语义。</summary>
	sealed class RegionTypeMode : MapMode
	{
		readonly WorldGenPlanet _p;
		public RegionTypeMode(WorldGenPlanet p) => _p = p;
		public override int Id => 1;
		public override string Name => "地质区域";
		public override Color CellColorAt(int i)
		{
			// 收口（§07 D-1）：地图语义一律读 **Final 口径**（最终区域归属；Placement 版仅供生成内部）
			int r = _p.Final.FinalRegionOfCell[i];
			if (r < 0) return new Color(OceanR, OceanG, OceanB);
			return _p.Regions.Regions[r].Type switch
			{
				RegionType.Plain => new Color(0.45f, 0.68f, 0.32f),   // 绿（平原）
				RegionType.Highland => new Color(0.78f, 0.66f, 0.36f),   // 黄褐（高地）
				RegionType.Basin => new Color(0.45f, 0.52f, 0.60f),   // 蓝灰（盆地）
				RegionType.Mountain => new Color(0.52f, 0.40f, 0.28f),   // 深棕（山地）
				RegionType.Plateau => new Color(0.85f, 0.55f, 0.30f),   // 橙（高原）
				RegionType.Rift => new Color(0.68f, 0.32f, 0.52f),   // 紫红（裂谷）
				RegionType.Coastal => new Color(0.55f, 0.78f, 0.55f),   // 青绿（沿岸）
				_ => Colors.Magenta,
			};
		}
	}

	/// <summary>离岸距离：语义 = "离海有多远"（km 感知）；跳数只是内部实现。
	/// 12 跳满域按 res4 实测量级标定（格边 ≈26 km ⇒ 满域 ≈ 312 km），换分辨率/网格时此处同步重标。</summary>
	sealed class CoastDistanceMode : MapMode
	{
		readonly WorldGenPlanet _p;
		public CoastDistanceMode(WorldGenPlanet p) => _p = p;
		public override int Id => 2;
		public override string Name => "离岸距离";
		public override string ScaleCaption => "距海渐变 · 12 跳满域（res4 ≈ 312 km）";
		public override Color CellColorAt(int i)
		{
			// 收口（§07 D-1）：离岸距离同样读 **Final 口径**（FinalDistToCoast/Land 由 FinalLand 派生）
			var f = _p.Final;
			int d = f.FinalLand[i] ? f.FinalDistToCoast[i] : f.FinalDistToLand[i];
			float t = System.Math.Clamp(d / 12f, 0f, 1f);   // 12 跳满域（res4 实测量级）
			return f.FinalLand[i]
				? new Color(0.93f - 0.45f * t, 0.78f - 0.30f * t, 0.32f + 0.15f * t)   // 陆：近岸米白 → 内陆深棕
				: new Color(0.55f - 0.50f * t, 0.75f - 0.59f * t, 0.80f - 0.38f * t);  // 海：近岸青白 → 深海蓝
		}
	}

	/// <summary>降水（年均；Sequential 固定域 0–3000 mm + 对数式显示变换）。
	/// ★月度降水场尚无可靠生产数据——**不为 UI 提前造月度数据**，有数据后再扩参数。</summary>
	sealed class PrecipitationMode : MapMode
	{
		readonly WorldGenPlanet _p;
		public PrecipitationMode(WorldGenPlanet p) => _p = p;
		public override int Id => 3;
		public override string Name => "降水";
		public override string ScaleCaption => "固定物理域 0–3000 mm/年（对数显示）";
		public override Color CellColorAt(int i) =>
			RampSampleSmooth(PrecipStops, PrecipNorm(_p.Precipitation.AnnualMm[i]));
	}

	/// <summary>温度（逐格年均；Sequential 固定域 −40…+90 °C · 线性）。
	/// 数据源 = `Temperature.CellTemperatureC`（逐格能量收支的年均值，陆海同口径）。
	/// ★月度温度场尚不存在 ⇒ **不为 UI 提前造月度数据**（同降水模式）；有月度事实后再加参数行。
	/// ★色带为本模式专用（不复用 BiomeColors.TempStops，理由见上方 TemperatureStops 注释）。</summary>
	sealed class TemperatureMode : MapMode
	{
		readonly WorldGenPlanet _p;
		public TemperatureMode(WorldGenPlanet p) => _p = p;
		public override int Id => 4;
		public override string Name => "温度";
		public override string ScaleCaption => $"固定物理域 {TempDomainMinC:F0}…+{TempDomainMaxC:F0} °C（年均 · 不随世界拉伸）";
		public override Color CellColorAt(int i) =>
			RampSampleSmooth(TemperatureStops, _p.Temperature.CellTemperatureC[i]);
	}
}
