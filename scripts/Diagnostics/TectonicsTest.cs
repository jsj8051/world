using Godot;
using System;
using World.CivSim;   // DeterministicRandom（诊断导出也用确定性随机，2026-08-19）
using World.Diagnostics;
using World.HexPlanet;
using World.MapGen;
using World.Services;
using World.Tectonics;

namespace World.Tectonics
{
    /// <summary>
    /// tectonics.js 移植验证场景（headless）：
    ///   跑板块模拟 → 诊断打印 → 位移场导出等距柱状预览 PNG。
    /// 运行：Godot --headless res://scenes/diag/TectonicsTest.tscn --quit-after N -- --seed 42 -- --run 300
    ///
    /// 参数：
    ///   --seed N / --s N          随机种子
    ///   --plates N / --p N        板块数
    ///   --run N / --r N           模拟时长（百万年）
    ///   --step N                  步长（百万年）
    ///   --n N / --grid N          网格细分
    ///   --radius R                星球半径 km（默认 6371 地球；行星标度 √(R/R⊕)）
    ///   --init                    只看初始地壳（分割不移动）
    ///   --compare                 侵蚀 开/关 各跑一次，对比导出
    /// </summary>
    public partial class TectonicsTest : DiagSceneBase
    {
        [Export] public int GridN = 16;        // Icosahedron 细分（verts≈10n²+2）
        [Export] public int NumPlates = 8;
        [Export] public int Seed = 42;
        [Export] public float RadiusKm = MapArchive.DefaultRadiusKm;   // 星球半径（行星标度验证，2026-08-20）
        [Export] public float RunMy = 30f;     // 模拟总时长（百万年）
        [Export] public float StepMy = 2f;     // 步长（百万年）
        [Export] public bool InitOnly = false; // true=只看初始地壳（不分割/不移动）
        [Export] public bool Compare = false;  // true=侵蚀 开/关 对比
        [Export] public bool Rift = true;      // false=关闭裂谷/俯冲（对比用）

        public override void _Ready()
        {
            // headless 参数覆盖（DiagSceneBase.ParseUserArgs 统一解析，2026-08-19 迁移）
            var args = ParseUserArgs();
            if ((args.TryGetValue("seed", out var sv) || args.TryGetValue("s", out sv)) && int.TryParse(sv, out int s1)) Seed = s1;
            if ((args.TryGetValue("plates", out sv) || args.TryGetValue("p", out sv)) && int.TryParse(sv, out int s2)) NumPlates = s2;
            if ((args.TryGetValue("run", out sv) || args.TryGetValue("r", out sv)) && float.TryParse(sv, out float f1)) RunMy = f1;
            if (args.TryGetValue("step", out sv) && float.TryParse(sv, out float f2)) StepMy = f2;
            if ((args.TryGetValue("n", out sv) || args.TryGetValue("grid", out sv)) && int.TryParse(sv, out int s3)) GridN = s3;
            if (args.TryGetValue("radius", out sv) && float.TryParse(sv, out float rf)) RadiusKm = rf;
            if (args.ContainsKey("init")) InitOnly = true;
            if (args.ContainsKey("compare")) Compare = true;
            if (args.TryGetValue("rift", out sv))
            {
                if (bool.TryParse(sv, out bool b1)) Rift = b1;
                else Rift = true;
            }

            LogService.Log("TectonicsTest", $"gridN={GridN} plates={NumPlates} seed={Seed} radius={RadiusKm}km run={RunMy}My step={StepMy}My compare={Compare}");

            if (InitOnly) { RunInitOnly(); return; }
            if (Compare) { RunCompare(); return; }
            RunSingle(true, "user://tectonics_elev.png", "user://tectonics_plates.png");
            GetTree().Quit();   // ⚠️ 2026-08-19：默认路径此前漏 Quit——headless 下挂到 timeout（verify.sh 回归发现）
        }

        // ── 模式 1：只看初始地壳 ──
        private void RunInitOnly()
        {
            var sim = new TectonicsSimulation(GridN);
            sim.GlobalGrid.PrintDiagnostics();
            sim.GenerateInitialCrust(Seed, 0.6f, RadiusKm);
            sim.SplitIntoPlates(NumPlates, Seed);
            sim.MergePlatesToMaster();
            sim.ComputeDisplacement();
            float sea = sim.SolveSeaLevel(0.6f);
            LogService.Log("TectonicsTest", $"INITIAL ONLY: sealevel={sea:F0} m, land={100f * sim.LandFractionAboveSea():F1}%, " +
                     $"disp[{FieldOps.Min(sim.Displacement):F0},{FieldOps.Max(sim.Displacement):F0}]m");
            ExportEquirectPreview(sim, "user://tectonics_elev.png");
            ExportPlatePreview(sim, "user://tectonics_plates.png");
            GetTree().Quit();
        }

        // ── 模式 2：侵蚀 开/关 对比 ──
        private void RunCompare()
        {
            LogService.Log("TectonicsTest", $"=== 侵蚀关（无地表过程）===");
            var simNo = RunSingle(false, "user://tectonics_elev_noerosion.png", "user://tectonics_plates_noerosion.png");
            LogService.Log("TectonicsTest", $"=== 侵蚀开（侵蚀/风化/成岩/变质）===");
            var simYes = RunSingle(true, "user://tectonics_elev_erosion.png", "user://tectonics_plates_erosion.png");

            // 对比诊断
            float[] d0 = simNo.Displacement, d1 = simYes.Displacement;
            LogService.Log("TectonicsTest", $"对比: 无侵蚀 disp[{FieldOps.Min(d0):F0},{FieldOps.Max(d0):F0}]m " +
                     $"land={100f * simNo.LandFractionAboveSea():F1}% | 有侵蚀 disp[{FieldOps.Min(d1):F0},{FieldOps.Max(d1):F0}]m " +
                     $"land={100f * simYes.LandFractionAboveSea():F1}%");
            GetTree().Quit();
        }

        // ── 单次模拟 ──
        private TectonicsSimulation RunSingle(bool erosion, string elevPath, string platePath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sim = new TectonicsSimulation(GridN);
            sim.EnableErosion = erosion;
            sim.EnableRifting = Rift;
            sim.GlobalGrid.PrintDiagnostics();

            sim.GenerateInitialCrust(Seed, 0.6f, RadiusKm);
            LogService.Log("TectonicsTest", $"initial crust ok");
            sim.SplitIntoPlates(NumPlates, Seed);
            LogService.Log("TectonicsTest", $"plates={sim.Plates.Count}, sizes={string.Join(",", sim.Plates.ConvertAll(p => p.TileCount))}");
            sim.Run(RunMy, StepMy);
            sw.Stop();
            LogService.Log("TectonicsTest", $"run took {sw.ElapsedMilliseconds}ms");

            ExportEquirectPreview(sim, elevPath);
            ExportPlatePreview(sim, platePath);
            return sim;
        }

        // ── 预览导出 ──

        /// <summary>球面位移 → 512×256 等距柱状预览 PNG（连续高度色带，海平面基准）。</summary>
        private void ExportEquirectPreview(TectonicsSimulation sim, string path)
        {
            const int w = 512, h = 256;
            var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            var disp = sim.Displacement;
            float sea = sim.SeaLevel;

            for (int y = 0; y < h; y++)
            {
                float lat = 90f - 180f * y / (h - 1);
                for (int x = 0; x < w; x++)
                {
                    float lon = -180f + 360f * x / (w - 1);
                    Vector3 p = LatLonToUnit(lat, lon);
                    int id = sim.GlobalGrid.NearestId(p);
                    float rel = disp[id] - sea;   // 相对海平面高度（m）
                    img.SetPixel(x, y, HeightColor(rel));
                }
            }
            img.SavePng(path);
            LogService.Log("TectonicsTest", $"elev preview saved: {path} (sealevel={sea:F0}m)");
        }

        /// <summary>板块 id 预览（每板随机色 + 黑色边界线，512×256 等距柱状）。
        /// ⚠️ 2026-08-02 修复：板块 id 不连续（ResetPlates 后 id 从 1 起），
        ///   旧代码用 id 直接索引 colors[] → 越界 → 黑色图。</summary>
        private void ExportPlatePreview(TectonicsSimulation sim, string path)
        {
            const int w = 512, h = 256;
            var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            var rng = new DeterministicRandom(12345);
            // id → 颜色映射（id 可能不连续：ResetPlates 后从 1 开始）
            var colorByPlate = new System.Collections.Generic.Dictionary<int, Color>();
            foreach (var plate in sim.Plates)
                colorByPlate[plate.Id] = new Color((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            Color unoccupied = new Color(0.35f, 0.35f, 0.4f);   // 无顶层板（深灰蓝）

            var plateAt = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                float lat = 90f - 180f * y / (h - 1);
                for (int x = 0; x < w; x++)
                {
                    float lon = -180f + 360f * x / (w - 1);
                    Vector3 p = LatLonToUnit(lat, lon);
                    int id = sim.GlobalGrid.NearestId(p);
                    plateAt[y * w + x] = (int)sim.TopPlateMap[id];
                }
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = plateAt[y * w + x];
                    bool border = false;
                    if (x < w - 1 && plateAt[y * w + x + 1] != p) border = true;
                    if (y < h - 1 && plateAt[(y + 1) * w + x] != p) border = true;
                    if (x == 0 && plateAt[y * w + (w - 1)] != p) border = true;
                    if (x == w - 1 && plateAt[y * w + 0] != p) border = true;

                    if (p < 0 || !colorByPlate.TryGetValue(p, out Color c)) c = unoccupied;
                    if (border) img.SetPixel(x, y, new Color(0f, 0f, 0f));
                    else img.SetPixel(x, y, c);
                }
            }
            img.SavePng(path);
            LogService.Log("TectonicsTest", $"plate preview saved: {path}");
        }

        private static Vector3 LatLonToUnit(float lat, float lon)
        {
            float la = Mathf.DegToRad(lat), lo = Mathf.DegToRad(lon);
            return new Vector3(
                Mathf.Cos(la) * Mathf.Cos(lo),
                Mathf.Sin(la),
                Mathf.Cos(la) * Mathf.Sin(lo));
        }

        /// <summary>
        /// 连续高度色带（相对海平面 rel 米）：
        ///   深海 -11000..-200  深蓝→蓝
        ///   -200..0            浅蓝（大陆架）
        ///   0..500             绿（低地）
        ///   500..1500          黄绿→黄（丘陵）
        ///   1500..3000         棕（山地）
        ///   3000..6000         深棕→灰（高山）
        ///   >6000              白（雪线）
        /// 海平面 0 处蓝绿分界最清晰。
        /// </summary>
        private static Color HeightColor(float rel)
        {
            // 锚点表（高度 m, 颜色）
            var stops = new (float h, Color c)[]
            {
                (-11000f, new Color(0.02f, 0.06f, 0.25f)),  // 极深海
                (-4000f,  new Color(0.05f, 0.15f, 0.45f)),  // 深海
                (-200f,   new Color(0.10f, 0.35f, 0.65f)),  // 大陆坡底
                (0f,      new Color(0.35f, 0.65f, 0.85f)),  // 海平面（浅蓝）
                (200f,    new Color(0.45f, 0.75f, 0.40f)),  // 低地绿
                (800f,    new Color(0.75f, 0.78f, 0.35f)),  // 丘陵黄
                (2000f,   new Color(0.65f, 0.50f, 0.30f)),  // 山地棕
                (4000f,   new Color(0.45f, 0.35f, 0.30f)),  // 高山深棕
                (6000f,   new Color(0.85f, 0.85f, 0.88f)),  // 雪线灰白
                (9000f,   new Color(1.00f, 1.00f, 1.00f)),  // 极高山
            };
            if (rel <= stops[0].h) return stops[0].c;
            for (int i = 1; i < stops.Length; i++)
            {
                if (rel <= stops[i].h)
                {
                    float t = (rel - stops[i - 1].h) / (stops[i].h - stops[i - 1].h);
                    return stops[i - 1].c.Lerp(stops[i].c, t);
                }
            }
            return stops[stops.Length - 1].c;
        }
    }
}
