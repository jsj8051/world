using System;
using System.Diagnostics;
using Godot;
using World.Data;       // WorldSpec（世界定义）
using World.WorldGen;   // WorldGenSimulation / WorldParamTable / WorldGenPlanet

namespace World.Diagnostics;

/// <summary>
/// **世界生成判读读数**（headless 可跑）——2026-10-10 用户拍板：把 `[WORLDGEN-*]` 判读
/// 从生产类 `WorldGenPlanet` 搬到这里（"生产类不掺诊断"）。
///
/// ▍搬走的是什么
/// 原先三个读数散在 `WorldGenPlanet._Ready` / `BuildSpec` / `Regenerate` 里：
///   · `[WORLDGEN-PARAMS]` 参数表生效值 + 读表报告；
///   · `[WORLDGEN-TIMING]` 全链事实摘要（n/res/land/regions/meanP/meanT/ridges/basins/lakes/thr）；
///   · `[WORLDGEN-READY]` 表现层构建耗时（ViewCtor / BuildChunks / RiverLinesBuild）。
///
/// ▍为什么读数等价
/// 前两个**只读 `Sim` 的字段**，与 Godot 完全无关 ⇒ 本诊断自行建
/// `WorldGenSimulation(res, radius)` 跑同一份世界定义，**逐字等价**（同 res / 同 radius / 同 spec）。
/// ⚠️ `[WORLDGEN-READY]`（表现层耗时）**不再产出**：它测的是 `BallView` / `RiverLineOverlay`
///    构建，属表现层；本诊断刻意只建逻辑侧（不建 View）⇒ 该读数随生产类一起消失。
///    要它就去主场景跑（Godot 编辑器 / headless 均可），那是表现层的自然宿主。
///
/// ▍用法
/// <code>
/// Godot --headless --path . res://scenes/diag/WorldGenReadoutDiag.tscn
/// Godot --headless --path . res://scenes/diag/WorldGenReadoutDiag.tscn -- --res=2 --radius=2.0
/// </code>
/// 默认档 = `WorldGenPlanet.ProductionRes`（res4）+ radius 2.0（与主场景一致）。
/// 退出码：0 = 读数健全；1 = 不健全（NaN/Inf / 陆占比越界 / 区域数为 0）。
///
/// ▍保留的"生产侧"内容（**有意不搬**）
/// `WorldGenPlanet.BuildSpec` 仍会用 `GD.PushWarning` 报**参数表读表报告**（坏档 / 首跑拷出）——
/// 那是**错误可见性**（生产可靠性），不是判读读数；搬走会让"坏档静默"，属功能回退。
/// </summary>
public partial class WorldGenReadoutDiag : DiagSceneBase
{
	public override void _Ready()
	{
		var args = ParseUserArgs();
		int res = args.TryGetValue("res", out var r) && int.TryParse(r, out var rv)
			? rv : WorldGenPlanet.ProductionRes;
		float radius = args.TryGetValue("radius", out var rad) && float.TryParse(rad, out var radv)
			? radv : 2.0f;

		// ── 世界定义（与生产同一入口，不另开通道）──
		var spec = WorldParamTable.Load(out var problems);
		foreach (var p in problems) GD.Print($"  [WORLDGEN-PARAMS] {p}");
		GD.Print($"[WORLDGEN-PARAMS] seed={spec.Seed} 陆块={spec.LandSea.ContinentCount} " +
				 $"陆占比={spec.LandSea.LandFraction:P1}");

		// ── 全链生成 ──
		var sim = new WorldGenSimulation(res, radius);
		var sw = Stopwatch.StartNew();
		sim.Run(spec);
		sw.Stop();

		int cells = sim.Ball.CellIds.Length;
		int regions = sim.Terrain.Regions.Regions.Length;
		double land = sim.LandSea.Projector.LandFraction;
		double meanP = sim.Climate.Precipitation.MeanMm;
		double meanT = sim.Climate.Temperature.MeanC;
		int ridges = sim.Terrain.Mountains.Ridges.Length;
		int basins = sim.Hydrology.Basins.BasinCount;
		int endorheic = sim.Hydrology.Basins.EndorheicBasinCount;
		int lakes = sim.Hydrology.Lakes.LakeCount;
		float thr = sim.LandSea.Projector.ThresholdUsed;

		GD.Print($"[WORLDGEN-TIMING] n={cells} res={sim.Ball.Res} " +
				 $"land={land:P1} regions={regions} " +
				 // P4-2 判读读数：降水事实的全球均值（等积格 ⇒ 算术平均；不是生产输入）
				 $"meanP={meanP:F0}mm/年 " +
				 $"meanT={meanT:F1}C " +
				 $"ridges={ridges} " +
				 // 2C-A 判读读数：流域总数 / 内流流域数（内流 = 湖泊候选，2C-B 才判定）
				 $"basins={basins}(endorheic {endorheic}) " +
				 $"lakes={lakes} " +
				 $"thr={thr:F3} total={sw.Elapsed.TotalMilliseconds:F0}ms");

		// ── 健全性（给 verify.sh 一个真的失败面，而不只是打印）──
		bool finite = !double.IsNaN(meanP) && !double.IsInfinity(meanP)
			&& !double.IsNaN(meanT) && !double.IsInfinity(meanT)
			&& !float.IsNaN(thr) && !float.IsInfinity(thr);
		bool ok = cells > 0 && regions > 0 && land > 0.0 && land < 1.0 && finite;
		Report("WorldGenReadout", ok, $"res={res} n={cells} land={land:P1} regions={regions}");
		Quit(ok ? 0 : 1);
	}
}
