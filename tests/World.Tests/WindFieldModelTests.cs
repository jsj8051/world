using System;
using System.Linq;
using NUnit.Framework;
using World.Spatial;
using World.Utils.H3;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · WindField 批次 1 护栏（⑯ WindField 契约 W1–W5 + 设计 §四/§八；纯解析骨架，零事实层依赖）：
///   · 带分类：Doldrums/Trade/Westerly/PolarEasterly 边界与公式逐点一致（ITCZ 不在 0° ⇒ ★带符号纬度）；
///   · 赤道基矢（拍板 ①）：Trade = 朝 φ_ITCZ 侧，赤道点有确定方位，无零向量分支；
///   · Coriolis：δ(0)=0、|φ| 单调、半球镜像；三带典型扇区（★BearingTo 去向语义，拍板 ②）；
///   · ITCZ：7 月最北 / 1 月最南；半球热异常修正有界（±5°，W-M2"有限幅度"）；
///   · 基速解耦（拍板 ③）：速度只由带分类决定。
/// 纪律：只用 [Test]；不写文件；不碰 GD.*/LogService。
/// </summary>
public class WindFieldModelTests
{
	const float D2R = MathF.PI / 180f;

	// ── ① 带分类（设计 §8 测试 1）─────────────────────────────────────────────────

	[Test]
	public void BeltClassification_MatchesLatitudeBands()
	{
		// 7 月（m=6）：φ_ITCZ=15、脊=33、锋=63（日历项解析值）
		Assert.That(WindFieldModel.ClassifyBelt(14.9f * D2R, 6), Is.EqualTo(WindBelt.Doldrums), "ITCZ 半宽内 = 无风带");
		Assert.That(WindFieldModel.ClassifyBelt(17.1f * D2R, 6), Is.EqualTo(WindBelt.Doldrums), "半宽对称（无风带跨 φ_ITCZ 两侧）");
		Assert.That(WindFieldModel.ClassifyBelt(18.1f * D2R, 6), Is.EqualTo(WindBelt.Trade));
		Assert.That(WindFieldModel.ClassifyBelt(33.1f * D2R, 6), Is.EqualTo(WindBelt.Westerly), "越过副热带脊 ⇒ 西风带");
		Assert.That(WindFieldModel.ClassifyBelt(62.9f * D2R, 6), Is.EqualTo(WindBelt.Westerly));
		Assert.That(WindFieldModel.ClassifyBelt(63.1f * D2R, 6), Is.EqualTo(WindBelt.PolarEasterly));

		// 1 月（m=0）：φ_ITCZ=−5（南压过赤道 ⇒ 带边界不对称，必须带符号判定）
		Assert.That(WindFieldModel.ClassifyBelt(-4.9f * D2R, 0), Is.EqualTo(WindBelt.Doldrums));
		Assert.That(WindFieldModel.ClassifyBelt(-32.9f * D2R, 0), Is.EqualTo(WindBelt.Trade), "SH 镜像脊 −33 ⇒ 信风");
		Assert.That(WindFieldModel.ClassifyBelt(-33.1f * D2R, 0), Is.EqualTo(WindBelt.Westerly));
		Assert.That(WindFieldModel.ClassifyBelt(-62.9f * D2R, 0), Is.EqualTo(WindBelt.Westerly));
		Assert.That(WindFieldModel.ClassifyBelt(-63.1f * D2R, 0), Is.EqualTo(WindBelt.PolarEasterly));
		// ITCZ 越赤道后，NH 低纬仍是信风（跨赤道 Trade 区间）
		Assert.That(WindFieldModel.ClassifyBelt(10f * D2R, 0), Is.EqualTo(WindBelt.Trade), "1 月 φ_ITCZ=−5 ⇒ 赤道以北到脊 27° 全为信风");
		Assert.That(WindFieldModel.ClassifyBelt(26.9f * D2R, 0), Is.EqualTo(WindBelt.Trade));
		Assert.That(WindFieldModel.ClassifyBelt(27.1f * D2R, 0), Is.EqualTo(WindBelt.Westerly));
	}

	// ── ② Coriolis（设计 §8 测试 2；★拍板 ①② 落地处）────────────────────────────

	[Test]
	public void Coriolis_ZeroAtEquator_Monotonic_MirrorSymmetric()
	{
		// δ(0) = 0 —— 赤道不偏（退化锚）
		Assert.That(WindFieldModel.CoriolisDeflectionDeg(0f), Is.EqualTo(0f).Within(1e-6f));

		// |φ| 单调不减
		float prev = -1f;
		for (int d = 1; d <= 89; d += 2)
		{
			float v = WindFieldModel.CoriolisDeflectionDeg(d * D2R);
			Assert.That(v, Is.GreaterThan(prev), $"δ 应随 |φ| 严格递增（{d}°）");
			prev = v;
		}

		// 半球镜像：δ(−φ) = δ(φ)（偏转方向差异在 Bearing 旋转侧，不在幅值）
		Assert.That(WindFieldModel.CoriolisDeflectionDeg(-30f * D2R),
			Is.EqualTo(WindFieldModel.CoriolisDeflectionDeg(30f * D2R)).Within(1e-6f));

		// 地球锚三点（tanh 形状参数；O-W2 精化前钉死）
		Assert.That(WindFieldModel.CoriolisDeflectionDeg(20f * D2R), Is.EqualTo(44.56f).Within(0.2f));
		Assert.That(WindFieldModel.CoriolisDeflectionDeg(45f * D2R), Is.EqualTo(66.63f).Within(0.2f));
		Assert.That(WindFieldModel.CoriolisDeflectionDeg(70f * D2R), Is.EqualTo(71.58f).Within(0.2f));

		// ★三带典型扇区（BearingTo 去向方位角语义，拍板 ②）：
		//   NH 信风 25°N：基矢 180°（南）+ δ=51.6° ⇒ 231.6°（吹向 SW）⇒ 扇区 11
		float tradeAz = WindFieldModel.DeflectedBearingToDeg(
			WindFieldModel.MeridionalBaseBearingToDeg(WindBelt.Trade, 25f * D2R, 6), 25f * D2R);
		Assert.That(tradeAz, Is.EqualTo(231.64f).Within(0.2f), "NH 信风去向 = SW（扇区 11）");
		Assert.That(WindFieldModel.SectorFromBearingToDeg(tradeAz), Is.EqualTo(11));
		//   NH 西风 45°N：基矢 0°（北）+ δ=66.6° ⇒ 66.6°（吹向 ENE）⇒ 扇区 3
		float westAz = WindFieldModel.DeflectedBearingToDeg(
			WindFieldModel.MeridionalBaseBearingToDeg(WindBelt.Westerly, 45f * D2R, 6), 45f * D2R);
		Assert.That(westAz, Is.EqualTo(66.63f).Within(0.2f), "NH 西风去向 = ENE");
		Assert.That(WindFieldModel.SectorFromBearingToDeg(westAz), Is.EqualTo(3));
		//   NH 极风 75°N：基矢 180° + δ=71.9° ⇒ 251.9°（吹向 WSW）⇒ 扇区 12
		float polarAz = WindFieldModel.DeflectedBearingToDeg(
			WindFieldModel.MeridionalBaseBearingToDeg(WindBelt.PolarEasterly, 75f * D2R, 6), 75f * D2R);
		Assert.That(polarAz, Is.EqualTo(251.92f).Within(0.2f), "NH 极地东风去向 = WSW（扇区 12）");
		Assert.That(WindFieldModel.SectorFromBearingToDeg(polarAz), Is.EqualTo(12));
		//   SH 镜像：偏转反向（az − δ）；SH 信风吹向 NW（SE 信风的去向）
		Assert.That(WindFieldModel.DeflectedBearingToDeg(0f, -25f * D2R), Is.EqualTo(308.36f).Within(0.2f),
			"SH 信风 = 基矢北 − δ ⇒ 308°（吹向 NW）");
		Assert.That(WindFieldModel.DeflectedBearingToDeg(180f, -45f * D2R), Is.EqualTo(113.37f).Within(0.2f),
			"SH 西风 = 基矢南 − δ ⇒ 113°（吹向 ESE）");

		// 扇区量化边界：22.5° 是 1/2 扇区分界；0 与 360 同属扇区 1
		Assert.That(WindFieldModel.SectorFromBearingToDeg(0f), Is.EqualTo(1));
		Assert.That(WindFieldModel.SectorFromBearingToDeg(22.4f), Is.EqualTo(1));
		Assert.That(WindFieldModel.SectorFromBearingToDeg(22.5f), Is.EqualTo(2));
		Assert.That(WindFieldModel.SectorFromBearingToDeg(359.9f), Is.EqualTo(16));
	}

	// ── ③ 赤道基矢解析极限（拍板 ①；W5 赤道特殊分支预防针）────────────────────────

	[Test]
	public void MeridionalBaseBearing_BearingToSemantics_NoEquatorSpecialCase()
	{
		// 1 月（φ_ITCZ=−5）：赤道点在跨赤道 Trade 区间内 ⇒ 朝 ITCZ（正南 180°），确定无歧义
		Assert.That(WindFieldModel.ClassifyBelt(0f, 0), Is.EqualTo(WindBelt.Trade));
		Assert.That(WindFieldModel.MeridionalBaseBearingToDeg(WindBelt.Trade, 0f, 0), Is.EqualTo(180f),
			"拍板①：Trade 基矢朝 φ_ITCZ 一侧；ITCZ 在 SH ⇒ 赤道基矢 = 正南");
		// 7 月（φ_ITCZ=+15）：赤道点同样 Trade，朝北（0°）指向 ITCZ
		Assert.That(WindFieldModel.MeridionalBaseBearingToDeg(WindBelt.Trade, 0f, 6), Is.EqualTo(0f),
			"ITCZ 在 NH ⇒ 赤道基矢 = 正北（两侧解析极限连续）");
		// Doldrums 无基矢——显式抛错而不是静默给方向（静风哨兵由输出批次负责）
		Assert.Throws<InvalidOperationException>(
			() => WindFieldModel.MeridionalBaseBearingToDeg(WindBelt.Doldrums, 15f * D2R, 6));
	}

	// ── ④ ITCZ 季节位移 + 异常修正有界（设计 §8 测试 3/4）─────────────────────────

	[Test]
	public void ItczJumpsNorthInJuly_SouthInJanuary()
	{
		Assert.That(WindFieldModel.ItczLatDeg(6), Is.EqualTo(15f).Within(1e-4f), "7 月最北 = 5+10");
		Assert.That(WindFieldModel.ItczLatDeg(0), Is.EqualTo(-5f).Within(1e-4f), "1 月最南 = 5−10（南压过赤道）");
		Assert.That(WindFieldModel.ItczLatDeg(3), Is.EqualTo(5f).Within(1e-4f), "4 月/10 月过均值 5°N");
		Assert.That(WindFieldModel.ItczLatDeg(9), Is.EqualTo(5f).Within(1e-4f));

		// 脊/锋同号漂移（7 月北跳 ⇒ NH 脊/锋北移、SH 镜像边界向赤道退）
		Assert.That(WindFieldModel.RidgeLatDeg(6), Is.EqualTo(33f).Within(1e-4f));
		Assert.That(WindFieldModel.RidgeLatDeg(0), Is.EqualTo(27f).Within(1e-4f));
		Assert.That(WindFieldModel.PolarFrontLatDeg(6), Is.EqualTo(63f).Within(1e-4f));
		Assert.That(WindFieldModel.PolarFrontLatDeg(0), Is.EqualTo(57f).Within(1e-4f));

		Assert.Throws<ArgumentOutOfRangeException>(() => WindFieldModel.ItczLatDeg(12), "月索引 0..11");
	}

	[Test]
	public void ItczAnomalyShift_Bounded()
	{
		// 线性区：Gain = 1 °/°C
		Assert.That(WindFieldModel.ItczAnomalyShiftDeg(3f, 0f), Is.EqualTo(3f).Within(1e-5f), "NH 偏暖 ⇒ ITCZ 北移");
		Assert.That(WindFieldModel.ItczAnomalyShiftDeg(-2f, 2f), Is.EqualTo(-4f).Within(1e-5f));
		// ★W-M2"有限幅度"：修正钳位 ±5°
		Assert.That(WindFieldModel.ItczAnomalyShiftDeg(10f, -10f), Is.EqualTo(5f).Within(1e-5f), "20°C 差 ⇒ 钳 +5");
		Assert.That(WindFieldModel.ItczAnomalyShiftDeg(20f, -20f), Is.EqualTo(5f).Within(1e-5f));
		// 组合：日历 + 异常后总钳位 ±15°
		Assert.That(WindFieldModel.ItczLatDeg(6, 10f, 0f), Is.EqualTo(15f).Within(1e-4f),
			"15 + 10 ⇒ 总钳位 15（7 月顶格）");
		Assert.That(WindFieldModel.ItczLatDeg(0, 0f, 10f), Is.EqualTo(-10f).Within(1e-4f),
			"−5 − 5 ⇒ 1 月南压至 −10（未触及 −15 钳位）");
		// ★异常关闭路径（D1 的异常半边）：传 (0,0) ⇒ 纯日历曲线
		Assert.That(WindFieldModel.ItczLatDeg(6, 0f, 0f), Is.EqualTo(WindFieldModel.ItczLatDeg(6)).Within(1e-6f));
	}

	// ── ⑤ 基速解耦（拍板 ③）──────────────────────────────────────────────────────

	[Test]
	public void BeltBaseSpeed_DecoupledFromDirection()
	{
		Assert.That(WindFieldModel.BeltBaseSpeedMs(WindBelt.Trade), Is.EqualTo(6f));
		Assert.That(WindFieldModel.BeltBaseSpeedMs(WindBelt.Westerly), Is.EqualTo(9f));
		Assert.That(WindFieldModel.BeltBaseSpeedMs(WindBelt.PolarEasterly), Is.EqualTo(4f));
		Assert.That(WindFieldModel.BeltBaseSpeedMs(WindBelt.Doldrums), Is.EqualTo(2.4f).Within(1e-5f),
			"无风带 = 信风基速 × 0.4");
		// 拍板③的静态钉：同带不同纬度的基速恒等（速度不含纬度/方向信息；季节修正随后续批次乘因子）
		Assert.That(WindFieldModel.BeltBaseSpeedMs(WindFieldModel.ClassifyBelt(20f * D2R, 6)),
			Is.EqualTo(WindFieldModel.BeltBaseSpeedMs(WindFieldModel.ClassifyBelt(10f * D2R, 6))),
			"同为信风带 ⇒ 基速相同（速度与位置/方向解耦）");
	}

	// ══ 批次 2：事实容器 + 全流水线 + 退化矩阵（设计 §五/§七）════════════════════

	const float Rad2DegF = 180f / MathF.PI;

	/// <summary>合成纬度剖面：−90..90 步长 2.5°（73 格，覆盖全部带与静风区）。</summary>
	static float[] SyntheticLats()
	{
		var lats = new float[73];
		for (int i = 0; i < 73; i++) lats[i] = (-90f + i * 2.5f) * D2R;
		return lats;
	}

	/// <summary>合成月度温度事实（tempOf(格, 月) → °C）；用内部工厂（InternalsVisibleTo）。</summary>
	static MonthlyTemperature MakeTemp(float[] latRad, Func<int, int, float> tempOf)
	{
		var t = MonthlyTemperature.Create(latRad.Length);
		for (int i = 0; i < latRad.Length; i++)
			for (int m = 0; m < MonthlyTemperature.Months; m++)
				t.Set(m, i, tempOf(i, m));
		t.FinishReadouts();
		return t;
	}

	// ── D3：全流水线 vs 独立实现逐点对照（原则 5 + 契约验收 A/D 的解析底座）────────

	[Test]
	public void Generate_MatchesIndependentTemplateImplementation()
	{
		var lats = SyntheticLats();
		var wind = WindFieldModel.Generate(lats);

		// 独立实现：从契约公式直接重算（不复用 WindFieldModel 的流水线代码路径）。
		// ★数值口径必须与生产一致：latDeg 用 double 乘（生产 = float×double）、δ 取 float
		//   （生产 CoriolisDeflectionDeg 返回 float）——否则无风带边界 |φ−φ_ITCZ|=3° 上的
		//   格会翻带成员，近对消格的圆均值方向翻转 180°（实测踩坑 2026-10-07）。
		var expectDir = new byte[lats.Length];
		var expectSpd = new float[lats.Length];
		for (int i = 0; i < lats.Length; i++)
		{
			double latDeg = lats[i] * (180.0 / Math.PI);
			float delta = (float)(75.0 * Math.Tanh(2.0 * Math.Sin(Math.Abs((double)lats[i]))));
			double cosSum = 0, sinSum = 0, spdSum = 0;
			int vecM = 0;
			for (int m = 0; m < 12; m++)
			{
				double itcz = 5.0 + 10.0 * Math.Cos(2.0 * Math.PI * (m - 6) / 12.0);
				itcz = Math.Clamp(itcz + 0.0, -15.0, 15.0);
				double shift = 0.3 * (itcz - 5.0);
				if (Math.Abs(latDeg - itcz) < 3.0) { spdSum += 6.0 * 0.4; continue; }
				double baseAz;
				float spd;
				if (latDeg >= 0)
				{
					if (latDeg <= 30.0 + shift) { baseAz = latDeg > itcz ? 180 : 0; spd = 6f; }
					else if (latDeg <= 60.0 + shift) { baseAz = 0; spd = 9f; }
					else { baseAz = 180; spd = 4f; }
				}
				else
				{
					if (latDeg >= -30.0 + shift) { baseAz = latDeg > itcz ? 180 : 0; spd = 6f; }
					else if (latDeg >= -60.0 + shift) { baseAz = 180; spd = 9f; }
					else { baseAz = 0; spd = 4f; }
				}
				double az = (baseAz + (lats[i] >= 0 ? delta : -delta)) * Math.PI / 180.0;
				cosSum += Math.Cos(az);
				sinSum += Math.Sin(az);
				vecM++;
				spdSum += spd;
			}
			expectSpd[i] = (float)(spdSum / 12.0);
			double meanLen = Math.Sqrt(cosSum * cosSum + sinSum * sinSum) / vecM;
			expectDir[i] = meanLen < 0.05
				? (byte)0
				: (byte)(Math.Clamp((int)Math.Floor((((Math.Atan2(sinSum, cosSum) * 180.0 / Math.PI) % 360.0 + 360.0) % 360.0) / 22.5) + 1, 1, 16));
		}

		Assert.That(wind.CellCount, Is.EqualTo(lats.Length));
		Assert.That(wind.DirectionTo, Is.EqualTo(expectDir), "盛行方向扇区应与独立实现逐点一致");
		Assert.That(wind.SpeedMs, Is.EqualTo(expectSpd).Within(1e-3f), "平均风速应与独立实现逐点一致");
		Assert.That(wind.MonsoonIndex, Is.All.EqualTo(0f), "批次 2 = 季风关闭态");
		Assert.That(wind.SpeedMs, Is.All.InRange(0f, 25f));
	}

	// ── D1：日历关断（amp=0）⇒ φ_ITCZ 恒 5°N；全年全无风带 ⇒ 静风哨兵 ─────────────

	[Test]
	public void Generate_CalendarShutoff_FixedItcz_AllYearDoldrumsIsCalm()
	{
		var tuning = new WindFieldModel.Tuning { ItczSeasonalAmpDeg = 0f };
		var lats = new[] { 5f, 20f, -20f, 45f }.Select(d => d * D2R).ToArray();
		var wind = WindFieldModel.Generate(lats, null, tuning);

		// 5°N：全年 |φ−5|<3 ⇒ 全 Doldrums ⇒ 方向哨兵 + 无风带基速
		Assert.That(wind.DirectionTo[0], Is.EqualTo(0), "全年无风带 ⇒ 静风哨兵");
		Assert.That(wind.SpeedMs[0], Is.EqualTo(2.4f).Within(1e-4f));
		// 20°N：全年信风（基矢 180 + δ(20°)=44.56 ⇒ 224.56° ⇒ 扇区 10）；速度恒 6
		Assert.That(wind.DirectionTo[1], Is.EqualTo(10), "日历关断后带恒定 ⇒ 方向恒定");
		Assert.That(wind.SpeedMs[1], Is.EqualTo(6f).Within(1e-4f));
		// SH 镜像：−20° 基矢 0 − δ ⇒ 315.44° ⇒ 扇区 15
		Assert.That(wind.DirectionTo[2], Is.EqualTo(15));
		// 45°N：西风 66.63° ⇒ 扇区 3；速度 9
		Assert.That(wind.DirectionTo[3], Is.EqualTo(3));
		Assert.That(wind.SpeedMs[3], Is.EqualTo(9f).Within(1e-4f));
	}

	// ── D5：纯函数一致性（契约验收 E）────────────────────────────────────────────

	[Test]
	public void Generate_Deterministic_TwoRunsIdentical()
	{
		var lats = SyntheticLats();
		var temp = MakeTemp(lats, (i, m) =>
			15f + 8f * MathF.Cos(2f * MathF.PI * (m - 6) / 12f) * (lats[i] >= 0 ? 1f : -1f));

		var a = WindFieldModel.Generate(lats, temp);
		var b = WindFieldModel.Generate(lats, temp);
		Assert.That(b.DirectionTo, Is.EqualTo(a.DirectionTo), "方向逐位同");
		Assert.That(b.SpeedMs, Is.EqualTo(a.SpeedMs), "速度逐位同");
		Assert.That(b.MonsoonIndex, Is.EqualTo(a.MonsoonIndex), "MRI 逐位同");
	}

	// ── 输入聚合：半球异常均值直接复用 P4-5b 输出（W2/W-S2 口径）─────────────────

	[Test]
	public void HemisphericAnomalyMeans_ReuseMonthlyTemperatureOutput()
	{
		var lats = new[] { 60f, 10f, -10f, -60f }.Select(d => d * D2R).ToArray();
		// NH 格 T = 10 + 4cos(相位)，SH 格 T = 10 − 4cos ⇒ 年均 10 ⇒ T′ = ±4cos
		var temp = MakeTemp(lats, (i, m) =>
			10f + (lats[i] >= 0 ? 1f : -1f) * 4f * MathF.Cos(2f * MathF.PI * (m - 6) / 12f));

		var (nh, sh) = WindFieldModel.HemisphericAnomalyMeansC(temp, lats);
		Assert.That(nh[6], Is.EqualTo(4f).Within(1e-4f), "7 月 NH 异常均值 = +4");
		Assert.That(nh[0], Is.EqualTo(-4f).Within(1e-4f), "1 月 NH = −4");
		Assert.That(sh[6], Is.EqualTo(-4f).Within(1e-4f), "SH 反相");
		Assert.That(sh[0], Is.EqualTo(4f).Within(1e-4f));
	}

	// ── 端到端：热异常事实 → ITCZ 修正 → 风场改变（契约验收 B 的机制前提）─────────

	[Test]
	public void Generate_TemperatureAnomalyShiftsItczAndWind()
	{
		// ★11°N 而非 13°N：13° 恰好压在 |φ−φ_ITCZ|=3° 边界上（float 噪声即可翻带成员），
		//   11° 的边界余量 ≥0.34°，对数值噪声稳健（实测踩坑 2026-10-07）。
		var lats = new[] { 11f, 40f, -40f }.Select(d => d * D2R).ToArray();
		// NH 格带 +3°C 的余弦异常 ⇒ T̄′_NH(m)=3cos ⇒ ITCZ = 5 + 10cos + 3cos（7 月顶到 ±15 钳位）；
		// SH 恒 0 异常（季风机制属批次 4，此处只验 ITCZ 通道）
		var temp = MakeTemp(lats, (i, m) =>
			lats[i] >= 0
				? 20f + 3f * MathF.Cos(2f * MathF.PI * (m - 6) / 12f)
				: 15f);

		var withAnom = WindFieldModel.Generate(lats, temp);
		var calendar = WindFieldModel.Generate(lats);   // 纯日历

		// 11°N：日历版无风带月 = {4,5,7,8}（itcz 10/13.66/13.66/10）= 4 个月；
		//        异常版 7 月被钳到 15 ⇒ 无风带月 = {4,8}（itcz 11.5）= 2 个月 ⇒ 平均速度改变
		Assert.That(withAnom.SpeedMs[0], Is.Not.EqualTo(calendar.SpeedMs[0]),
			"ITCZ 北移改变 11°N 的无风带月数 ⇒ 平均速度改变（4.8 → 5.4 m/s）");
		// 两版盛行方向同为信风朝 ITCZ 侧（扇区 10）——修正改变带成员/速度，不必然改变盛行扇区
		Assert.That(withAnom.DirectionTo[0], Is.EqualTo(calendar.DirectionTo[0]));
		// 40°（两半球）：始终在带内 ⇒ 异常不改变其速度（拍板 ③：速度与方向解耦的边界验证）
		Assert.That(withAnom.SpeedMs[1], Is.EqualTo(calendar.SpeedMs[1]).Within(1e-5f));
		Assert.That(withAnom.SpeedMs[2], Is.EqualTo(calendar.SpeedMs[2]).Within(1e-5f));
	}

	// ── 球面接线：H3 权威纬度 + 布局不变量 ────────────────────────────────────────

	[Test]
	public void Generate_FromBall_LayoutInvariants()
	{
		var ball = new Ball(1, 1f);
		var wind = WindFieldModel.Generate(ball);
		Assert.That(wind.CellCount, Is.EqualTo(ball.CellDirs.Length));
		Assert.That(wind.DirectionTo.Length, Is.EqualTo(wind.CellCount));
		Assert.That(wind.SpeedMs.Length, Is.EqualTo(wind.CellCount));
		Assert.That(wind.MonsoonIndex.Length, Is.EqualTo(wind.CellCount));
		Assert.That(wind.DirectionTo, Is.All.InRange(0, 16), "扇区域 [0,16]（0=静风哨兵）");
		Assert.That(wind.SpeedMs, Is.All.InRange(0f, 25f));
		// 纬度口径对照（设计 I1 / O-W6）：H3.LatLng（弧度）vs asin(归一化方向向量 Y)
		for (int i = 0; i < ball.CellDirs.Length; i++)
		{
			var d = ball.CellDirs[i];
			float viaDirs = MathF.Asin(d.Y / d.Length());
			float viaH3 = (float)H3.CellToLatLng(ball.CellIds[i]).Lat;
			Assert.That(viaH3, Is.EqualTo(viaDirs).Within(1e-4f),
				$"格 {i}：H3.LatLng 与方向向量纬度口径应一致（弧度）");
		}
	}

	// ── res4 全世界验收（手动：Ball(4) 构造 + 流水线 ≈ 数秒；不进常规套件）────────

	[Test]
	[Explicit("res4 全世界单档验收（生产档 288,122 格）：手动运行 dotnet test --filter Res4_FullWorld")]
	public void Res4_FullWorld_Generate_Acceptance()
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var ball = new Ball(4, 1f);
		long ballMs = sw.ElapsedMilliseconds;

		sw.Restart();
		var wind = WindFieldModel.Generate(ball);
		long genMs = sw.ElapsedMilliseconds;

		Assert.That(wind.CellCount, Is.EqualTo(288_122), "生产档格数（永久原则 3）");
		Assert.That(wind.SpeedMs, Is.All.InRange(0f, 25f));
		Assert.That(wind.DirectionTo.Cast<byte>(), Is.All.InRange((byte)0, (byte)16));
		Assert.That(wind.MonsoonIndex, Is.All.EqualTo(0f));
		long bytes = wind.CellCount * (1 + 4 + 4);
		TestContext.Out.WriteLine($"[WindField res4] Ball 构造 {ballMs} ms | Generate {genMs} ms | " +
								  $"存储 {bytes / 1024.0 / 1024.0:F2} MB（契约 W-S1 口径 ≈2.6 MB）| " +
								  $"静风格 = {wind.DirectionTo.Count(d => d == 0)}");
		Assert.That(genMs, Is.LessThan(30_000), "全流水线应在秒级完成（诊断场成本约束）");
	}
}
