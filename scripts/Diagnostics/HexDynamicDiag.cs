using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;               // MaterialDensity（判读换算走表）

namespace World.Diagnostics;

/// <summary>动态板块运动数值判读（设计-03）。headless 可跑，纯逻辑层无渲染。
/// 用法：
///   Godot --headless --path E:/godotGames/world --quit-after 400000 res://scenes/diag/HexDynamicDiag.tscn
///       -- --res=3 --seed=42 --plates=8 --run=600 --step=4
/// 报告项（对照判读，不设"算完成"门槛）：陆占比 / 海平面 / 位移极值 / 板数演化 /
/// 空洞·异板混合·同板多源·被钳制格数 / 顶死实测·顶死预测·幻影F占比（碰撞减速缺口量测）/
/// 平流离散化残差与俯冲回地幔质量（对账）/ 增生楔记账 / 最大板占比（超大陆讨论输入）/ 耗时。</summary>
public partial class HexDynamicDiag : Node
{
	/// <summary>判读换算用的密度表（质量↔厚度换算统一走表，不再散落硬编码）。</summary>
	static readonly MaterialDensity DiagMaterial = new MaterialDensity();
	int _res = 3;
	int _plates = 8;
	int _seed = 42;
	float _run = H3DynamicTectonics.DefaultRunMy;
	float _step = 4f;
	float _oceanScale = 1f;
	// 初始起伏旋钮：null = **不覆盖**（用 H3DynamicTectonics 的默认档）——诊断里不复制魔数，
	// 否则改类默认后诊断还在跑旧值（本文件曾因此把 550 m 的默认覆盖成 1200 m 跑出同样的数）。
	float? _landRelief;
	float? _oceanRelief;
	float? _reliefWaveKm;
	int _flexureIterations = -1;      // <0 = 用产品默认（24）；0 = 关闭挠曲（A/B 对照）
	float _precipScale = 1f;          // 降水倍率（0 = 无水世界：水蚀/输沙归零，只剩风+重力）
	float _yieldRate = -1f;           // 屈服流速率覆盖（-1 = 用默认档；P4 平衡点判读 A/B）
	// 影子薄席（设计-07 P1）：--shadow=1 开启——旧运动学旁并行解薄席速度场，只记不改。
	bool _shadow;
	// 薄席驱动（设计-07 P2）：--sheet=1 开启——平流方向/节拍改吃薄席，旧力平衡退位。
	bool _sheetDriving;
	// 通量化运输（设计-07 P3）：--flux=1 开启——跳格平流/空洞填充退位，改走 upwind 通量。
	bool _fluxTransport;
	H3ThinSheet? _shadowSheet;
	float[]? _shadowGpe, _shadowElev;
	double _shadowSumOld, _shadowSumNew, _shadowSumAgree;
	int _shadowSamples;

	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		if (args.TryGetValue("res", out var value)) _res = int.Parse(value);
		if (args.TryGetValue("plates", out value)) _plates = int.Parse(value);
		if (args.TryGetValue("seed", out value)) _seed = int.Parse(value);
		if (args.TryGetValue("run", out value)) _run = float.Parse(value);
		if (args.TryGetValue("step", out value)) _step = float.Parse(value);
		if (args.TryGetValue("ocean", out value)) _oceanScale = float.Parse(value);
		if (args.TryGetValue("landRelief", out value)) _landRelief = float.Parse(value);
		if (args.TryGetValue("oceanRelief", out value)) _oceanRelief = float.Parse(value);
		if (args.TryGetValue("reliefWave", out value)) _reliefWaveKm = float.Parse(value);
		// 挠曲开关（06 §3 批次 B 的 A/B 对照）：--flexure=0 关掉（位移 = 纯 Airy），默认开。
		if (args.TryGetValue("flexure", out value)) _flexureIterations = int.Parse(value);
		// 降水倍率（v1.20 四介质侵蚀）：--precip=0 = **无水世界**（干世界 ⇒ 水蚀严格 0、只剩风+重力）。
		if (args.TryGetValue("precip", out value)) _precipScale = float.Parse(value);
		// 影子薄席（设计-07 P1）：--shadow=1 开启（默认关）。
		if (args.TryGetValue("shadow", out value)) _shadow = value != "0";
		// 薄席驱动（设计-07 P2）：--sheet=1 切驱动（默认关）。
		if (args.TryGetValue("sheet", out value)) _sheetDriving = value != "0";
		// 通量化运输（设计-07 P3）：--flux=1 开启（默认关；须与 --sheet 同开）。
		if (args.TryGetValue("flux", out value)) _fluxTransport = value != "0";
		// 屈服流速率（P4 平衡点判读）：--yield=0.1/0.2 覆盖默认 0.05——max 山高 = 来流量/速率。
		if (args.TryGetValue("yield", out value)) _yieldRate = float.Parse(value);

		var ball = new Ball(_res, 1f);
		GD.Print($"=== HexDynamicDiag res={_res} P={_plates} seed={_seed} N={ball.CellIds.Length} "
			+ $"run={_run}My step={_step}My ocean={_oceanScale}（水量 = 全球等效水层 {H3Isostasy.GlobalWaterLayerM * _oceanScale:F0} m）===");

		var stopwatchSplit = Stopwatch.StartNew();
		var plateSplitter = new H3Plate(ball);
		int[] plateOfCell = plateSplitter.SplitIntoPlates(_plates, _seed);      // 初始分板（加权随机生长）
		stopwatchSplit.Stop();

		var sim = new H3DynamicTectonics(ball)
		{
			RunMy = _run,
			StepMy = _step,
			OceanScale = _oceanScale,
		};
		if (_landRelief.HasValue) sim.LandReliefAmplitudeM = _landRelief.Value;
		if (_oceanRelief.HasValue) sim.OceanReliefAmplitudeM = _oceanRelief.Value;
		if (_reliefWaveKm.HasValue) sim.ReliefBaseWavelengthKm = _reliefWaveKm.Value;
		if (_flexureIterations >= 0) sim.FlexureIterations = _flexureIterations;
		sim.PrecipitationScale = _precipScale;
		if (_yieldRate > 0f) sim.YieldFlowRate = _yieldRate;
		sim.EnableThinSheetDriving = _sheetDriving;
		sim.EnableFluxTransport = _fluxTransport;
		// 周期重启的重分板委托（05 §6.3）：与生产路径 `H3Plate.BeginCreatePlates` 同款注入——
		// 不注入则重启**自动旁路**（04 批次 4 的设计），诊断就看不到 platec 式周期重启。
		sim.Repartition = k => plateSplitter.SplitIntoPlates(k, _seed + 977 * sim.RestartCount);
		GD.Print($"[参数] 初始起伏 陆±{sim.LandReliefAmplitudeM:F0}m 洋±{sim.OceanReliefAmplitudeM:F0}m "
			+ $"基波长 {sim.ReliefBaseWavelengthKm:F0}km / {sim.ReliefOctaves} 层（水量 = 全球等效水层 {H3Isostasy.GlobalWaterLayerM * sim.OceanScale:F0}m）");
		var stopwatch = Stopwatch.StartNew();
		sim.Initialize(plateOfCell, _seed);
		{
			int assigned = 0;
			foreach (int p in sim.Fields.PlateId) if (p >= 0) assigned++;
			GD.Print($"[探针] 初始化后归属格数 = {assigned} / {sim.Fields.Count}");
		}
		ReportInitialRelief(ball, sim);
		Report("初始", ball, sim);
		ReportEmbeddedClusters("初始(运动前)", ball, sim);
		if (_shadow)
		{
			_shadowGpe = new float[ball.CellIds.Length];
			_shadowElev = new float[ball.CellIds.Length];
			_shadowSheet = new H3ThinSheet(ball);
			GD.Print("[影子薄席] 已启用：每 25 步采样，与旧运动学只比不改（设计-07 P1）");
		}
		int steps = Math.Max(1, (int)(_run / _step));
		// 全程平均口径（04 批次 6 治乱用）：逐步快照的抽样噪声太大（起跳相位使计数在 0 与数百间跳），
		// 改/不改的对照必须看**每步平均**——否则两个不同动力学轨迹的抽样点没法比。
		double sumHole = 0, sumMixed = 0, sumOverflow = 0, sumHoleFill = 0,
			sumNewCrust = 0, sumStray = 0, sumMinority = 0, sumIsolated = 0, sumSpreading = 0,
			sumYieldBled = 0, sumYieldToMantle = 0, sumArcReturned = 0, sumFelsicScraped = 0;
		int sumAbove3Km = 0, sumLandCells = 0;
		for (int s = 0; s < steps; s++)
		{
			sim.Step();
			sumHole += sim.HoleCellsLastStep;
			sumMixed += sim.MixedCellsLastStep;
			sumOverflow += sim.OverflowCellsLastStep;
			sumHoleFill += sim.Advection.HoleFillAveragedCount;
			sumYieldBled += sim.YieldBledMassLastStep;
			sumYieldToMantle += sim.YieldToMantleMassLastStep;
			sumArcReturned += sim.FelsicArcReturnedCum;
			sumFelsicScraped += sim.FelsicScrapedCum;
			sumNewCrust += sim.Advection.NewCrustFilledCount;
			sumSpreading += sim.Advection.SpreadingFilledCount;
			sumStray += sim.StrayFragmentCellsLastStep;
			sumMinority += sim.LocalMinorityCellsLastStep;
			sumIsolated += sim.IsolatedPlateCellsLastStep;
			for (int i = 0; i < sim.Displacement.Length; i++)
				if (sim.Fields.IsLand(i))
				{
					sumLandCells++;
					if (sim.Displacement[i] - sim.SeaLevel > 3000f) sumAbove3Km++;
				}
			if (s % 25 == 0 || s == steps - 1) Report($"step {s + 1}/{steps} ({(s + 1) * _step:F0}My)", ball, sim);

			// 影子薄席采样：GPE（当时位移-海平面）→ 解算 → 与旧运动学对比（只记不改）
			if (_shadow && s % 25 == 0)
			{
				for (int i = 0; i < _shadowElev!.Length; i++)
					_shadowElev[i] = sim.Displacement[i] - sim.SeaLevel;
				H3Gpe.ComputeInto(sim.Fields, _shadowElev, DiagMaterial, _shadowGpe!);
				_shadowSheet!.Solve(_shadowGpe);
				var stats = H3ThinSheet.CompareWithKinematics(sim.Motion.Velocity, _shadowSheet.Velocity);
				_shadowSumOld += stats.MeanSpeedOldCmPerYr;
				_shadowSumNew += stats.MeanSpeedNewCmPerYr;
				_shadowSumAgree += stats.DirectionAgreement;
				_shadowSamples++;
				GD.Print($"[影子薄席 step {s + 1}] 旧 {stats.MeanSpeedOldCmPerYr:F2} cm/yr ｜ 薄席 {stats.MeanSpeedNewCmPerYr:F2} cm/yr ｜ 方向一致率 {stats.DirectionAgreement:P0}（对比格 {stats.ComparedCells}）");
			}
		}
		GD.Print($"[全程平均/步] 空洞={sumHole / steps:F1} 异板混合={sumMixed / steps:F1} 同板多源={sumOverflow / steps:F1} "
			+ $"｜ 治乱 空洞加权平均填充={sumHoleFill / steps:F1} 屈服流出={sumYieldBled / steps:E2}（回地幔 {sumYieldToMantle / steps:E2}）"
			+ $"填新壳兜底={sumNewCrust / steps:F2} 碎片并入={sumStray / steps:F2} "
			+ $"｜ 扩张 洋底注壳={sumSpreading / steps:F2}"
			+ $"｜ 归属 孤立格={sumIsolated / steps:F2} 强少数格={sumMinority / steps:F1} "
			+ $"｜ 陆格 >3km={sumLandCells / (double)steps:F1} 中 {(sumLandCells > 0 ? sumAbove3Km / (double)sumLandCells : 0):P1}");
		if (_shadow && _shadowSamples > 0)
			GD.Print($"[影子薄席均值 / {_shadowSamples} 样本] 旧运动学 {_shadowSumOld / _shadowSamples:F2} cm/yr ｜ "
				+ $"薄席 {_shadowSumNew / _shadowSamples:F2} cm/yr ｜ 方向一致率 {_shadowSumAgree / _shadowSamples:P0}"
				+ $"——P2 切驱动的基线读数（判读口，不设门槛）");
		ReportEmbeddedClusters($"终态({steps * _step:F0}My)", ball, sim, traceHistory: true);
		stopwatch.Stop();

		GD.Print($"[耗时] 初始分板 {stopwatchSplit.ElapsedMilliseconds}ms ｜ 模拟 {stopwatch.ElapsedMilliseconds}ms"
			+ $"（{steps} 步，{stopwatch.ElapsedMilliseconds / (double)steps:F1} ms/步）");
		GetTree().Quit(0);
	}

	/// <summary>初始地形判读（v1.10 新增）：陆/洋海拔区间与**相邻同板同相格的海拔差分位数**。
	/// 后者是"起伏是否连续"的判据——逐格白噪声下相邻差会是千 m 级台阶，fBm 下应是百 m 内小量。</summary>
	void ReportInitialRelief(Ball ball, H3DynamicTectonics sim)
	{
		var fields = sim.Fields;
		float[] displacement = sim.Displacement;
		float sea = sim.SeaLevel;
		float landMin = float.MaxValue, landMax = float.MinValue;
		float oceanMin = float.MaxValue, oceanMax = float.MinValue;
		double landSum = 0, oceanSum = 0;
		int land = 0, ocean = 0;
		for (int i = 0; i < fields.Count; i++)
		{
			float elevation = displacement[i] - sea;
			if (fields.IsLand(i))
			{
				land++;
				landSum += elevation;
				if (elevation < landMin) landMin = elevation;
				if (elevation > landMax) landMax = elevation;
			}
			else
			{
				ocean++;
				oceanSum += elevation;
				if (elevation < oceanMin) oceanMin = elevation;
				if (elevation > oceanMax) oceanMax = elevation;
			}
		}

		var steps = new List<float>();
		for (int i = 0; i < fields.Count; i++)
			foreach (int nb in ball.CellNeighbors[i])
			{
				if (nb <= i || fields.PlateId[nb] != fields.PlateId[i]) continue;   // 只比同板（跨板是壳类型跳变）
				if (fields.IsLand(nb) != fields.IsLand(i)) continue;
				steps.Add(MathF.Abs(displacement[nb] - displacement[i]));
			}
		steps.Sort();
		float p50 = steps.Count > 0 ? steps[steps.Count / 2] : 0f;
		float p90 = steps.Count > 0 ? steps[(int)(steps.Count * 0.9f)] : 0f;
		float worst = steps.Count > 0 ? steps[^1] : 0f;

		GD.Print($"[初始地形] 陆 {land} 格（{land / (float)fields.Count:P1}）海拔 "
			+ $"[{landMin:F0},{landMax:F0}]m 均值 {landSum / Math.Max(land, 1):F0}m ｜ "
			+ $"洋 {ocean} 格 海底水位 [{oceanMin:F0},{oceanMax:F0}]m 均值 {oceanSum / Math.Max(ocean, 1):F0}m ｜ 海平面 {sea:F0}m");
		GD.Print($"[初始地形] 相邻同板格海拔差（连续性判据，v1.10 fBm 起伏）｜ p50={p50:F0}m p90={p90:F0}m max={worst:F0}m");
	}

	void Report(string label, Ball ball, H3DynamicTectonics sim)
	{
		float[] displacement = sim.Displacement;
		float min = float.MaxValue, max = float.MinValue;
		foreach (float d in displacement)
		{
			if (d < min) min = d;
			if (d > max) max = d;
		}
		// 陆格均值（介质率/降水的读数用）：海平面以上才算陆
		float MeanOverLand(float[] field)
		{
			double sum = 0;
			int cnt = 0;
			for (int i = 0; i < field.Length; i++)
			{
				if (!sim.Fields.IsLand(i)) continue;
				sum += field[i];
				cnt++;
			}
			return cnt > 0 ? (float)(sum / cnt) : 0f;
		}
		// 相邻同板同相格海拔差（连续性判据；与 [初始地形] 同一口径 —— 逐步看"台阶"长没长出来）
		// + 挠曲读数（06 §3 批次 B）：区域支撑的直接量测（削低/抬升/残差）。
		var steps = new List<float>();
		int worstA = -1, worstB = -1;
		float worstDiff = -1f;
		for (int i = 0; i < sim.Fields.Count; i++)
			foreach (int nb in ball.CellNeighbors[i])
			{
				if (nb <= i || sim.Fields.PlateId[nb] != sim.Fields.PlateId[i]) continue;
				if (sim.Fields.IsLand(nb) != sim.Fields.IsLand(i)) continue;
				float diff = MathF.Abs(displacement[nb] - displacement[i]);
				steps.Add(diff);
				if (diff > worstDiff) { worstDiff = diff; worstA = i; worstB = nb; }
			}
		if (steps.Count > 0)
		{
			steps.Sort();
			GD.Print($"[{label}] 相邻差 ｜ p50={steps[steps.Count / 2]:F0} p90={steps[(int)(steps.Count * 0.9f)]:F0} "
				+ $"max={steps[^1]:F0} m ｜ 挠曲 削低={sim.FlexurePeakReductionM:F0} 抬升={sim.FlexurePeakUpliftM:F0} "
				+ $"残差={sim.FlexureResidualM:F2} m");
			// 三介质侵蚀（v1.23：水蚀退役归河道）+ 风沙/河流两条搬运链的读数
			var sf = sim.Surface;
			double movedTotal = sf.WindMovedMassLastStep
				+ sf.GravityMovedMassLastStep + sf.GlacierMovedMassLastStep;
			if (movedTotal > 0)
				GD.Print($"[{label}] 侵蚀介质 ｜ 风={sf.WindMovedMassLastStep / movedTotal:P0}"
					+ $" 重力={sf.GravityMovedMassLastStep / movedTotal:P0}"
					+ $" 冰川={sf.GlacierMovedMassLastStep / movedTotal:P0}"
					+ $" ｜ 风化={sf.WeatheredMassLastStep:E2}（门=水+风+冰）"
					+ $" ｜ 介质率（陆格均值）水={MeanOverLand(sf.WaterRatePerCell):F2}"
					+ $" 风={MeanOverLand(sf.WindRatePerCell):F2} 冰={MeanOverLand(sf.GlacierRatePerCell):F2}"
					+ $" K={MeanOverLand(sf.RockErodibilityPerCell):F3}"
					+ $" ｜ 闭合 λ={sim.PrecipitationClosureFactor:F2}"
					+ $" 降水={sf.PrecipDomainMeanMmYearLastStep:F0}mm/yr 产流={sf.RunoffDomainMeanMmYearLastStep:F0}"
					+ $" AI={sf.AridityIndexDomainMeanLastStep:F2} 零产流格={sf.ZeroRunoffCellFractionLastStep:P0}");
			if (sim.EnableAeolian && sim.Aeolian.ErodedMassLastStep > 0)
				GD.Print($"[{label}] 风沙 ｜ 蚀={sim.Aeolian.ErodedMassLastStep:E2} 淤={sim.Aeolian.DepositedMassLastStep:E2}"
					+ $" 入海={sim.Aeolian.DeliveredToOceanLastStep:E2}");
			if (sim.EnableFluvial && sim.Fluvial.ErodedMassLastStep > 0)
				GD.Print($"[{label}] 河流 ｜ 蚀={sim.Fluvial.ErodedMassLastStep:E2} 淤={sim.Fluvial.DepositedMassLastStep:E2}"
					+ $" 入海={sim.Fluvial.DeliveredToOceanLastStep:E2}"
					+ $" 流速均值={MeanOverLand(sim.Fluvial.VelocityPerCell):F2}m/s 剪切均值={MeanOverLand(sim.Fluvial.ShearPerCell):F0}Pa");
			if (worstDiff > 1500f)
			{
				var material = new World.Tectonics.MaterialDensity();
				GD.Print($"[{label}] 最大台阶明细 ｜ 格{worstA}({(sim.Fields.IsLand(worstA) ? "陆" : "洋")},"
					+ $"板{sim.Fields.PlateId[worstA]},位移{displacement[worstA]:F0},厚{sim.Fields.Thickness(worstA, material):F0},"
					+ $"长英质{(sim.Fields.FelsicPlutonic[worstA] + sim.Fields.FelsicVolcanic[worstA]) / material.FelsicPlutonic:F0},"
					+ $"镁铁质{(sim.Fields.MaficVolcanic[worstA] + sim.Fields.MaficPlutonic[worstA]) / material.MaficVolcanicMin:F0},"
					+ $"age{sim.Fields.Age[worstA]:F0})"
					+ $" ↔ 格{worstB}({(sim.Fields.IsLand(worstB) ? "陆" : "洋")},板{sim.Fields.PlateId[worstB]},"
					+ $"位移{displacement[worstB]:F0},厚{sim.Fields.Thickness(worstB, material):F0},"
					+ $"长英质{(sim.Fields.FelsicPlutonic[worstB] + sim.Fields.FelsicVolcanic[worstB]) / material.FelsicPlutonic:F0},"
					+ $"镁铁质{(sim.Fields.MaficVolcanic[worstB] + sim.Fields.MaficPlutonic[worstB]) / material.MaficVolcanicMin:F0},"
					+ $"age{sim.Fields.Age[worstB]:F0})"
					+ $" ｜ Δ={worstDiff:F0} m"
					// 三个低密度池的厚度（"凭空 80 km 柱"的定位用：只有它们能在总厚度里藏出巨量）
					+ $"\n[{label}]   低密度池厚度 ｜ 格{worstB}: 沉积={sim.Fields.Sediment[worstB] / material.Sediment:F0}"
					+ $" 沉积岩={sim.Fields.Sedimentary[worstB] / material.Sedimentary:F0}"
					+ $" 变质={sim.Fields.Metamorphic[worstB] / material.Metamorphic:F0} m");
			}
		}
		// 地形高度分布（判读"碰撞造山是不是都顶在厚度帽上"）：陆海拔分位 + 高山格占比
		var landElev = new List<float>();
		for (int i = 0; i < displacement.Length; i++)
			if (sim.Fields.IsLand(i)) landElev.Add(displacement[i] - sim.SeaLevel);
		landElev.Sort();
		if (landElev.Count > 0)
		{
			float Quantile(float fraction) => landElev[Math.Clamp((int)(landElev.Count * fraction), 0, landElev.Count - 1)];
			int above3 = 0, above5 = 0;
			foreach (float e in landElev)
			{
				if (e > 3000f) above3++;
				if (e > 5000f) above5++;
			}
			GD.Print($"[{label}] 地形 ｜ 陆海拔 p50={Quantile(0.5f):F0} p90={Quantile(0.9f):F0} "
				+ $"p99={Quantile(0.99f):F0} max={landElev[^1]:F0} m ｜ "
				+ $"陆格 >3km {above3 / (float)landElev.Count:P1} >5km {above5 / (float)landElev.Count:P1}");
		}
		// 热状态（设计-05）：势温演化 + 黏度响应 + 热收支（缺口 = Urey 比悖论的登记口）
		var thermal = sim.Thermal;
		GD.Print($"[{label}] 热状态 ｜ 势温 {thermal.PotentialTemperatureK:F0} K"
			+ $"（降 {H3ThermalState.ReferencePotentialTemperatureK - thermal.PotentialTemperatureK:F1}）"
			+ $" ｜ η ×{thermal.MantleViscosityPaS / H3ThermalState.ReferenceViscosityPaS:F2}"
			+ $" ｜ 降温率 {thermal.DeclineKPerGa:F0} K/Ga"
			+ $" ｜ 放射性生热 {thermal.RadiogenicHeatTW:F1} TW ｜ 地表散热 {thermal.HeatLossTW:F1} TW"
			+ $"（洋 {thermal.OceanHeatFlowTW:F1} / 陆 {thermal.ContinentalHeatFlowTW:F1}）"
			+ $" ｜ 收支缺口 {thermal.HeatBalanceGapTW:F1} TW");
		GD.Print($"[{label}] 板={sim.PlateCount} 陆={sim.LandFractionLastStep * 100f:F1}% 海平面={sim.SeaLevel:F0}m "			+ $"位移[{min:F0},{max:F0}]m ｜ 空洞={sim.HoleCellsLastStep} 异板混合={sim.MixedCellsLastStep} "
			+ $"同板多源={sim.OverflowCellsLastStep} 速度钳制={sim.ClampedCellsLastStep} 陆内补料={sim.ResampledCellsLastStep} "
			+ $"｜ 治乱 空洞加权平均填充={sim.Advection.HoleFillAveragedCount} 填新壳兜底={sim.Advection.NewCrustFilledCount} "
			+ $"屈服流 超帽格={sim.YieldCellsLastStep} 流出={sim.YieldBledMassLastStep:E2}（回地幔 {sim.YieldToMantleMassLastStep:E2}）碎片并入={sim.StrayFragmentCellsLastStep} ｜ "
			+ $"速度 规定={sim.MeanPrescribedSpeedKmPerMyLastStep * 0.1f:F2} 实到={sim.MeanRealizedSpeedKmPerMyLastStep * 0.1f:F2} "
			+ $"拟合ω={sim.MeanPlateSpeedKmPerMyLastStep * 0.1f:F2} cm/yr ｜ "
			+ $"顶死实测={sim.JammedCellsLastStep} 顶死预测={sim.JamContactCellsLastStep} "
			+ $"幻影F={sim.PhantomForceFractionLastStep:P0} 均速={sim.MeanPlateSpeedKmPerMyLastStep * 0.1f:F2}cm/yr ｜ "
			+ $"回地幔={sim.RecycledToMantleLastStep:E2} "
			+ $"屈服={sim.YieldBledMassLastStep:E2}(落{sim.YieldPlacedMassLastStep:E2}) "
			+ $"陆壳收支 刮削累计={sim.FelsicScrapedCum:E2} 弧回流累计={sim.FelsicArcReturnedCum:E2} "
			+ $"守恒销毁累计={sim.RecycledConservedTotal + sim.ConservedYieldToMantleCum:E2}（伺服本步 {sim.ContinentalServoPlacedLastStep:E2}） "
			+ $"最大板占比={sim.LargestPlateFractionLastStep * 100f:F1}% ｜ "
			+ $"生命周期 缝合={sim.SutureCount} 裂解={sim.SplitCount} 重启={sim.RestartCount}"
			+ $"（周期步={sim.CycleStepCount} 距陆碰撞={sim.StepsSinceContinentalCollision}步 "
			+ $"动能比={sim.MomentumRatioLastStep:P0}）"
			+ (sim.LastRestartReason.Length > 0 ? $"［最近重启：{sim.LastRestartReason}］" : "")
			+ (sim.SuturedPairLastStep is (int s, int b) ? $"（本步缝合 {s}→{b}）" : "")
			+ (sim.SplitPlateLastStep >= 0 ? $"（本步裂解 板{sim.SplitPlateLastStep}）" : ""));
		GD.Print($"[{label}] 归属拓扑 ｜ 无主格={sim.OwnerlessCellsLastStep} 孤立格={sim.IsolatedPlateCellsLastStep} "
			+ $"强少数格={sim.LocalMinorityCellsLastStep} 连通分量={sim.PlateComponentCountLastStep}（板数 {sim.PlateCount}）"
			+ $" 最大分量占比={sim.LargestComponentFractionLastStep * 100f:F1}%");

		// 通量注壳判读（P3 第二批.5 预算闭环）：脊上增生不得超过海沟销毁——白化球复发先看这行
		if (sim.Flux is { } flux)
			GD.Print($"[{label}] 通量注壳 ｜ 预算={flux.InjectionBudgetLastStep:E2} 需求={flux.InjectionDemandLastStep:E2} "
				+ $"实注={flux.CreatedMassLastStep:E2} 脊格={flux.RidgeCellsLastStep} 俯冲边={flux.SubductEdgesLastStep} "
				+ $"弧回流={flux.ArcFelsicReturnedMassLastStep:E2}");

		// 逐板顶死明细（缺口量测：谁在顶着等密碰撞带、幻影力占比、真实板速还剩多少）
		var motion = sim.Motion;
		if (motion?.PlateJamContacts == null) return;
		var parts = new List<string>();
		for (int p = 0; p < motion.PlateJamContacts.Length; p++)
		{
			if (motion.PlateJamContacts[p] == 0) continue;
			float gross = motion.PlateGrossForceN[p];
			float phantom = motion.PlatePhantomForceN[p];
			float speedCmPerYr = motion.PlateOmega[p].Length() * H3PlateMotion.EarthRadiusKm / 10f;
			parts.Add($"板{p} 顶死{motion.PlateJamContacts[p]}格 "
				+ $"F幻影={(gross > 0f ? phantom / gross : 0f):P0} 速={speedCmPerYr:F2}cm/yr");
		}
		if (parts.Count > 0)
			GD.Print($"[{label}] 顶死明细 ｜ {string.Join("，", parts)}");

		// 04 批次 2 力分解（净板缘负浮力 / 板片账户拉力 / 洋脊推力 / 造山阻力 / 拟合板速）
		// ⚠️ 表长不齐：板片/力分解表由 `EnsureSlabTableFor` 按"平流当步出现的最大板号"扩容，
		// 而 `PlateGrossForceN/PlateOmega` 只由 `EnsureRotationTables` 按"运动当步存在的板"扩容
		// ——后者可能更短（04 批次 6 实测：`力分解` 段越界崩在 PlateGrossForceN）。取各表最小值。
		if (motion.PlateSlabPullN == null) return;
		int forceTableLength = motion.PlateSlabPullN.Length;
		forceTableLength = Math.Min(forceTableLength, motion.PlateRidgePushN?.Length ?? 0);
		forceTableLength = Math.Min(forceTableLength, motion.PlateOrogenResistN?.Length ?? 0);
		forceTableLength = Math.Min(forceTableLength, motion.PlateGrossForceN?.Length ?? 0);
		forceTableLength = Math.Min(forceTableLength, motion.PlatePhantomForceN?.Length ?? 0);
		forceTableLength = Math.Min(forceTableLength, motion.PlateOmega?.Length ?? 0);
		var forceParts = new List<string>();
		double slabAccountTotal = 0;
		for (int p = 0; p < forceTableLength; p++)
		{
			float edge = motion.PlateGrossForceN[p] - motion.PlatePhantomForceN[p];
			float slab = motion.PlateSlabPullN[p];
			float ridge = motion.PlateRidgePushN[p];
			float resist = motion.PlateOrogenResistN[p];
			if (edge <= 0f && slab <= 0f && ridge <= 0f && resist <= 0f) continue;
			slabAccountTotal += motion.SlabMassOf(p);
			float speedCmPerYr = motion.PlateOmega[p].Length() * H3PlateMotion.EarthRadiusKm / 10f;
			forceParts.Add($"板{p} 板缘{edge:E1} 板片{slab:E1}(m={motion.SlabMassOf(p):E1},|d|={motion.SlabDirOf(p).Length():E1}) 洋脊{ridge:E1} 阻力{resist:E1} 速{speedCmPerYr:F2}");
		}
		if (forceParts.Count > 0)
			GD.Print($"[{label}] 力分解[N] ｜ {string.Join("｜", forceParts)}（板片账户Σ={slabAccountTotal:E1} kg/m²口径）");
	}

	/// <summary>嵌入簇溯源：把"强少数格"（本板邻居数 < 某他板邻居数）按同板连通聚成簇，
	/// 每簇报：板号、格数、包围板、物质指纹（长英质/镁铁质厚度、年龄），并和包围板的物质对照。
	/// 用于回答"板 X 内部怎么有几格板 Y"：簇在**运动前**就存在 = 初始分板的几何残留；
	/// 运动后新出现 = 搬运/填充事件所置。top 12 簇，按格数降序。</summary>
	void ReportEmbeddedClusters(string label, Ball ball, H3DynamicTectonics sim, bool traceHistory = false)
	{
		int n = ball.CellIds.Length;
		var neighbors = ball.CellNeighbors;
		var plateId = sim.Fields.PlateId;
		var fields = sim.Fields;

		var minority = new bool[n];
		var otherCounts = new Dictionary<int, int>();
		for (int i = 0; i < n; i++)
		{
			int p = plateId[i];
			if (p < 0) continue;
			int own = 0;
			otherCounts.Clear();
			foreach (int nb in neighbors[i])
			{
				int q = plateId[nb];
				if (q < 0) continue;
				if (q == p) own++;
				else otherCounts[q] = otherCounts.GetValueOrDefault(q) + 1;
			}
			int maxOther = 0;
			foreach (var kv in otherCounts) maxOther = Math.Max(maxOther, kv.Value);
			minority[i] = own < maxOther;
		}

		// 同板连通聚簇（只串强少数格）
		var visited = new bool[n];
		var stack = new List<int>();
		var clusters = new List<(int plate, int size, int surroundPlate, double felsicM, double maficM, double ageM)>();
		for (int i = 0; i < n; i++)
		{
			if (!minority[i] || visited[i]) continue;
			int plate = plateId[i];
			stack.Clear();
			stack.Add(i);
			visited[i] = true;
			var surround = new Dictionary<int, int>();
			double felsic = 0, mafic = 0, age = 0;
			int size = 0;
			while (stack.Count > 0)
			{
				int c = stack[^1];
				stack.RemoveAt(stack.Count - 1);
				size++;
				felsic += (fields.FelsicPlutonic[c] + fields.FelsicVolcanic[c]) / DiagMaterial.FelsicPlutonic;
				mafic += (fields.MaficVolcanic[c] + fields.MaficPlutonic[c]) / DiagMaterial.MaficVolcanicMin;
				age += fields.Age[c];
				foreach (int nb in neighbors[c])
				{
					if (plateId[nb] == plate && minority[nb] && !visited[nb])
					{
						visited[nb] = true;
						stack.Add(nb);
					}
					else if (plateId[nb] != plate && plateId[nb] >= 0)
						surround[plateId[nb]] = surround.GetValueOrDefault(plateId[nb]) + 1;
				}
			}
			int surroundPlate = -1, surroundMax = 0;
			foreach (var kv in surround)
				if (kv.Value > surroundMax) { surroundMax = kv.Value; surroundPlate = kv.Key; }
			clusters.Add((plate, size, surroundPlate, felsic / size, mafic / size, age / size));
		}
		clusters.Sort((a, b) => b.size.CompareTo(a.size));
		GD.Print($"[{label}] 嵌入簇共 {clusters.Count} 个（强少数格 {clusters.Sum(c => c.size)} 格）：");
		foreach (var c in clusters.Take(12))
			GD.Print($"  板{c.plate}({c.size}格) 嵌于 板{c.surroundPlate} ｜ "
				+ $"簇: felsic={c.felsicM:F1}km mafic={c.maficM:F1}km age={c.ageM:F0}My");

		// 行车记录仪：终态簇格子的换色历史（step, from→to, kind）
		if (!traceHistory) return;
		var events = sim.ChangeEvents;
		var kindNames = new[] { "arrival翻色", "填新壳", "陆内重采样", "海底连续填充" };
		// ⚠️ `kind` 是新事件种类就继续加：表没跟上时**退化成 kindN** 而不是抛 IndexOutOfRange
		//（2026-09-18 实测：第 5 类事件让整段终态溯源报告崩掉，连带后面的读数一起没了）。
		var histogram = events.GroupBy(e => e.kind).Select(g =>
			$"{(g.Key >= 0 && g.Key < kindNames.Length ? kindNames[g.Key] : $"kind{g.Key}")}×{g.Count()}");
		int totalSteps = events.Count == 0 ? 1 : events.Max(e => e.step) + 1;
		GD.Print($"[溯源] 全程换色事件共 {events.Count} 条（平均 {events.Count / (double)totalSteps:F2} 条/步）：{string.Join("，", histogram)}");
		foreach (var c in clusters.Take(6))
		{
			var cells = new List<int>();
			for (int i = 0; i < n; i++)
				if (minority[i] && plateId[i] == c.plate) cells.Add(i);
			GD.Print($"  ── 板{c.plate}({c.size}格) 嵌于 板{c.surroundPlate} 的换色史（前 6 格）：");
			foreach (int cell in cells.Take(6))
			{
				var trail = events.Where(e => e.cell == cell).ToList();
				string trailText = trail.Count == 0
					? "从未换色（初始即如此）"
					: string.Join(" → ", trail.Select(e => $"step{e.step}:{e.from}→{e.to}(kind{e.kind})"));
				GD.Print($"    格 {cell}: {trailText}");
			}
		}
	}
}
