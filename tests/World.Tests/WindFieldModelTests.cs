using System;
using NUnit.Framework;
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
}
