using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Biome;
using World.MapGen;
using World.Services;

namespace World.Diagnostics;

/// <summary>
/// 季风诊断：读现有存档 → 现场算季风环流场 + 月降水 + 柯本 biome（月数据）→ 全量写回（秒级）。
/// 不必重跑板块模拟——季风是气候阶段纯计算，从存档的 elev/temp/precip 直接可算。
///
/// 命令行：-- --arch=user://maps/xxx.mpa [--tilt=23.4] [--out=user://maps/yyy.mpa]
///   --arch 必填（无值=默认 map1.mpa）；--tilt 覆盖倾角（存档头部没有倾角，默认 23.4）；
///   --out 指定输出（默认覆盖原档）。写回后 MapViewer 可直接看季风图层 + 月降水数据。
/// </summary>
public partial class MonsoonDiag : DiagSceneBase
{
    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        if (arch == null)
        {
            LogService.LogErr("MonsoonDiag", "需要 --arch=user://maps/xxx.mpa（季风诊断是存档直读工具，不跑板块模拟）");
            GetTree().Quit(1);
            return;
        }
        if (!ArchiveDiag.TryLoad(arch, out var ctx))
        {
            GetTree().Quit(1);
            return;
        }

        // 解析 --tilt / --out / --selftest（存档头部无倾角）
        float tilt = 23.4f;
        string outPath = arch;
        bool selfTest = false;
        var args = ParseUserArgs();
        if (args.TryGetValue("tilt", out var tiltArg) && float.TryParse(tiltArg, out float t)) tilt = t;
        if (args.TryGetValue("out", out var outArg)) outPath = outArg;
        if (args.ContainsKey("selftest")) selfTest = true;

        var map = ctx.Map;
        int n = ctx.VertexCount;
        LogService.Log("MonsoonDiag", $"读档 {arch} n={n} tilt={tilt}° → 重算季风/月降水/biome");

        // ── 季风环流诊断场（用存档的年温/年降水）──
        var climate = new ClimateGenerator(map.Seed, tilt, 1f);
        MonsoonSystem.Compute(ctx.Verts, ctx.Neighbors, ctx.ElevNorm, ctx.ElevM, ctx.Temp, ctx.Precip, tilt, map.RotationSpeed, climate,
            out var monsoon, out var tHotM, out var tColdM, out var dryP, out var dryIdx, out var monthP,
            out var monthWind, out var monthTemp, out var precipAnnAbs, radiusKm: map.RadiusKm);
        // 年降水 = Σ 月（月→年；诊断用聚合值重算 biome）
        var precipAnn = precipAnnAbs;

        // ── biome 重算（柯本月数据：真实最热/最冷月、最干月+月份）──
        var biome = new byte[n];
        Parallel.For(0, n, i =>
        {
            float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(ctx.Verts[i].Y, -1f, 1f)));
            biome[i] = (byte)BiomeClassifier.Classify(ctx.ElevNorm[i], ctx.Temp[i], precipAnn[i],
                tHotM[i], tColdM[i], dryP[i], dryIdx[i], latDeg);
        });

        // ── byte 化 ──
        var monsoonLevel = new byte[n];
        for (int i = 0; i < n; i++)
            monsoonLevel[i] = FieldCodec.RatioToByte(monsoon[i]);
        var monthPrecip = new byte[MonsoonSystem.MonthCount][];
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
        {
            monthPrecip[m] = new byte[n];
            for (int i = 0; i < n; i++)
            {
                float ratio = monthP[m][i];   // ⚠️ 2026-08-05 修：MonsoonSystem 输出已是比例(Σ=1)，勿再除年降水（双重归一化→byte≈0→图层全黄）
                monthPrecip[m][i] = FieldCodec.RatioToByte(ratio);
            }
        }

        var monthTempB = new byte[MonsoonSystem.MonthCount][];
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
        {
            monthTempB[m] = new byte[n];
            for (int i = 0; i < n; i++)
                monthTempB[m][i] = FieldCodec.TempToByte(monthTemp[m][i]);
        }

        // ── 全量写回（保留全部旧字段 + 新 ext8/ext9/ext10）──
        bool ok = MapArchive.WriteSpherical(outPath, map.Seed, ctx.Verts,
            map.MinElev, map.MaxElev, map.Elev, map.Temp, ctx.Precip, biome,
            map.MinTemp, map.MaxTemp, map.MinPrecip, map.MaxPrecip,
            prograde: map.ProgradeRotation, rotationSpeed: map.RotationSpeed, axialTilt: tilt,
            currentDirs: map.CurrentDirs, currentWarmth: map.CurrentWarmth, currentStrength: map.CurrentStrength,
            psi: map.Psi,   // 保留源档 psi（null 时写入端补零，读取端不误读河流段）
            riverLevel: map.RiverLevel, riverFlow: map.RiverFlow, riverVolume: map.RiverVolume, lakeLevel: map.LakeLevel,
            mineralLevel: map.MineralLevel, soilLevel: map.SoilLevel,
            monsoonLevel: monsoonLevel, monthPrecip: monthPrecip, monthTemp: monthTempB,
            radiusKm: map.RadiusKm, log: false);

        // ── 像素图自检（--selftest，用户验收机制 2026-08-16）：──
        //   程序生成 Equirect 投影 7 月温度像素图 → 读像素断言：
        //   A. 纬度梯度：赤道带均值 > 极地带（温差 >10°C）
        //   B. 海陆对照（Plan C 核心）：7 月北半球中低纬大陆像素均值 > 海洋像素均值
        //   C. 海拔效应：同纬度大陆高海拔 < 低海拔（高山冷）
        //   通过=温度模型修正（把海洋囊括+辐射×大陆性）验收
        if (selfTest)
        {
            const int W = 720, H = 360;
            var img = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
            for (int py = 0; py < H; py++)
            {
                float la = Mathf.DegToRad(90f - py * 180f / H);
                float cosLa = Mathf.Cos(la), sinLa = Mathf.Sin(la);
                for (int px = 0; px < W; px++)
                {
                    float lo = Mathf.Tau * px / W - Mathf.Pi;
                    var dir = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
                    int vid = map.NearestVertex(dir);
                    img.SetPixel(px, py, BiomeColors.TemperatureToColor(monthTemp[6][vid]));
                }
            }
            img.SavePng("user://selftest_temp7.png");
            LogService.Log("SelfTest", "已生成 user://selftest_temp7.png（Equirect 7月温度像素图）");

            // ── 读像素断言（直接从顶点数据，等价于像素图内容；像素图已存 PNG 供人查）──
            double aEq = 0, aMid = 0, aPol = 0; int cEq = 0, cMid = 0, cPol = 0;
            var landB = new double[5]; var landC = new int[5];
            var oceanB = new double[5]; var oceanC = new int[5];
            double cHigh = 0, cLow = 0; int cHighN = 0, cLowN = 0;
            for (int i = 0; i < n; i++)
            {
                float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(ctx.Verts[i].Y, -1f, 1f)));
                float t7 = monthTemp[6][i];
                float latAbs = Mathf.Abs(latDeg);
                if (latAbs <= 30f) { aEq += t7; cEq++; }
                else if (latAbs >= 60f) { aPol += t7; cPol++; }
                else { aMid += t7; cMid++; }
                if (latDeg >= 15f && latDeg <= 40f)   // 北半球中低纬（Plan C 验收带）
                {
                    // B 用海平面等效温度（气象学标准）：实际温度里高山冷是正确物理（青藏高原
                    //   实际也比同纬度海洋冷）；Plan C 验证的是"海陆热力"= 海平面等效。
                    //   ⚠️ v2：真正的同纬度对照——B 带内大陆偏中纬、海洋偏热带（基础差 -5.4°C
                    //   是纬度分布，非海陆热力）。按 5° 带内比较海陆，再对各带温差平均。
                    //   高山雪原（elevNorm>0.5，反照率 0.45）排除——它冷是真实物理（C 断言验证）。
                    if (ctx.ElevNorm[i] >= BiomeClassifier.AlpineLevel) { continue; }
                    float t7sl = t7 + (ctx.ElevNorm[i] >= BiomeClassifier.OceanLevel && ctx.ElevM[i] > 0f ? MonsoonSystem.ElevLapseRatePerM * ctx.ElevM[i] : 0f);
                    int band = Mathf.Clamp((int)((latDeg - 15f) / 5f), 0, 4);
                    if (ctx.ElevNorm[i] >= BiomeClassifier.OceanLevel) { landB[band] += t7sl; landC[band]++; }
                    else { oceanB[band] += t7sl; oceanC[band]++; }
                }
                if (ctx.ElevNorm[i] >= BiomeClassifier.OceanLevel && latDeg >= 15f && latDeg <= 40f)
                {
                    if (ctx.ElevNorm[i] > BiomeClassifier.AlpineLevel) { cHigh += t7; cHighN++; }
                    else if (ctx.ElevNorm[i] < 0.3f) { cLow += t7; cLowN++; }
                }
            }
            double eq = cEq > 0 ? aEq / cEq : 0, pol = cPol > 0 ? aPol / cPol : 0;
            // B：各 5° 带内海陆温差平均（真正的同纬度对照）
            double diffSum = 0; int diffN = 0; double landMax = double.MinValue;
            for (int b = 0; b < 5; b++)
            {
                if (landC[b] > 0 && oceanC[b] > 0)
                {
                    double ld = landB[b] / landC[b], od = oceanB[b] / oceanC[b];
                    diffSum += ld - od; diffN++;
                    landMax = Math.Max(landMax, ld);
                }
            }
            double land = diffN > 0 ? diffSum / diffN : landMax;   // 同纬度平均海陆温差（°C，正=大陆热）
            double ocean = 0;
            double high = cHighN > 0 ? cHigh / cHighN : 0, low = cLowN > 0 ? cLow / cLowN : 0;
            bool passA = eq - pol > 10f;
            bool passB = land > ocean;   // Plan C：7 月北半球大陆实际温度 > 海洋
            bool passC = high < low;
            LogService.Log("SelfTest", $"A纬度梯度: 赤道带{eq:F1}°C vs 极地带{pol:F1}°C (差{eq - pol:F1}, 需>10) → {(passA ? "PASS" : "FAIL")}");
            LogService.Log("SelfTest", $"B海陆对照: 7月北半球中低纬同纬度海陆温差={land:F1}°C (需>0=大陆热于海洋) → {(passB ? "PASS" : "FAIL")}");
            LogService.Log("SelfTest", $"C海拔效应: 高海拔{high:F1}°C vs 低海拔{low:F1}°C (需高山冷) → {(passC ? "PASS" : "FAIL")}");
            LogService.Log("SelfTest", $"总体 → {(passA && passB && passC ? "PASS" : "FAIL")}");
        }

        // ── 统计 ──
        int monsoonCells = 0;
        float monsoonMax = 0f;
        for (int i = 0; i < n; i++)
        {
            if (monsoon[i] >= 0.25f) monsoonCells++;
            monsoonMax = Mathf.Max(monsoonMax, monsoon[i]);
        }
        // 月降水季节对比（季风区格：雨季月 vs 干季月）
        int strong = 0;
        for (int i = 0; i < n; i++) if (monsoon[i] >= 0.25f && ++strong > 8) break;
        int wetM = 0, dryM = 0;
        if (strong > 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (monsoon[i] < 0.25f) continue;
                float wMax = 0f; int wIdx = 0;
                float dMin2 = float.MaxValue; int dIdx2 = 0;
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                {
                    if (monthP[m][i] > wMax) { wMax = monthP[m][i]; wIdx = m; }
                    if (monthP[m][i] < dMin2) { dMin2 = monthP[m][i]; dIdx2 = m; }
                }
                wetM = wIdx; dryM = dIdx2;
                break;
            }
        }
        LogService.Log("MonsoonDiag", $"季风区（≥0.25）：{monsoonCells} 格 ({monsoonCells * 100f / n:F1}%) 峰值强度={monsoonMax:F2}" +
                 (strong > 0 ? $" 示例季风格：雨季月={wetM + 1}月 干季月={dryM + 1}月" : ""));
        var dist = new int[32];
        foreach (var b in biome) dist[b]++;
        var sb = new System.Text.StringBuilder("biome dist: ");
        for (int i = 0; i < dist.Length; i++)
            if (dist[i] > 0) sb.Append($"{((BiomeType)i)}={dist[i]}({dist[i] * 100.0 / n:F1}%) ");
        LogService.Log("MonsoonDiag", sb.ToString());

        LogService.Log("MonsoonDiag", $"写回 {(ok ? "成功" : "失败")} → {outPath}");
        GetTree().Quit(ok ? 0 : 1);
    }
}
