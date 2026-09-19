using System;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;

namespace World.Tests;

/// <summary>
/// 河流输沙三段护栏（气候批次 2，2026-09-17）：侵蚀→搬运→沉积（H3FluvialTransport）：
///   · 守恒：组总量严格不变，Σ侵蚀 = Σ沉积（负载本步内全部落定）；
///   · 方向：上游啃床（负载 &lt; 挟沙力），淤积集中在下游/入海口——搬运距离不再是一格；
///   · 洼地成盆：内流洼地是链终点，接住整条子汇的来沙；
///   · 确定性：同入参逐位一致；
///   · 管线：演化步开启输沙后质量账本照常闭合。
/// 地形 = 热带陆桥 + 距海跳数爬坡（同 H3WaterCycleTests 口径）；降水给常量场——单元测试
/// 只验输沙机制本身，不掺气候（Apply 直吃 precipMPerYear 数组）。
/// 纪律（同 H3DynamicRunTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3FluvialTests
{
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
    static Ball Ball => SharedBall.Value;

    const float BaseElevM = 800f;
    const float SlopePerHopM = 200f;

    // 热带陆桥（|lat|<30° 为陆）+ 距海跳数爬坡；hop[i] = 距海格数（洋格 0）。
    // slopePerHopM 可调（v1.22 水力几何测试用：τ = ρg·d·S 对坡度敏感，缓/陡对照出 τc 选择性）。
    static (bool[] isLand, float[] height, int[] hop) BuildTerrain(float slopePerHopM = SlopePerHopM)
    {
        int n = Ball.CellIds.Length;
        var isLand = new bool[n];
        var height = new float[n];
        for (int i = 0; i < n; i++)
        {
            float absLatDeg = MathF.Abs(MathF.Asin(Math.Clamp(Ball.CellCenters[i].Normalized().Y, -1f, 1f))) * 180f / MathF.PI;
            if (absLatDeg < 30f) isLand[i] = true;
        }
        var hop = new int[n];
        Array.Fill(hop, int.MaxValue);
        var queue = new int[n];
        int head = 0, tail = 0;
        for (int i = 0; i < n; i++)
        {
            if (isLand[i]) continue;
            hop[i] = 0;
            queue[tail++] = i;
        }
        while (head < tail)
        {
            int i = queue[head++];
            foreach (int nb in Ball.CellNeighbors[i])
            {
                if (hop[nb] <= hop[i] + 1) continue;
                hop[nb] = hop[i] + 1;
                queue[tail++] = nb;
            }
        }
        for (int i = 0; i < n; i++)
            if (isLand[i]) height[i] = BaseElevM + slopePerHopM * hop[i];
        return (isLand, height, hop);
    }

    static H3PlateFields MakeFields(int n)
    {
        var f = new H3PlateFields(n);
        for (int i = 0; i < n; i++) f.FelsicPlutonic[i] = 1e6f;   // 陆格统一基岩存量
        return f;
    }

    static float[] ConstantPrecip(int n, float mPerYear)
    {
        var p = new float[n];
        Array.Fill(p, mPerYear);
        return p;
    }

    static double GroupTotal(H3PlateFields f)
    {
        double sum = 0;
        foreach (var pool in f.ConservedPools())
            for (int i = 0; i < pool.Length; i++) sum += pool[i];
        return sum;
    }

    [Test]
    public void Conservation_GroupTotalInvariant_AndErosionEqualsDeposition()
    {
        var (isLand, height, _) = BuildTerrain();
        var fields = MakeFields(height.Length);
        double before = GroupTotal(fields);

        var fluvial = new H3FluvialTransport(Ball);
        fluvial.Apply(fields, height, ConstantPrecip(height.Length, 1f), scale: 1f);

        double after = GroupTotal(fields);
        Assert.That(after, Is.EqualTo(before).Within(before * 1e-5),
            "输沙只在守恒组内搬运：组总量必须严格不变");
        Assert.Greater(fluvial.ErodedMassLastStep, 0, "爬坡地形应当发生侵蚀");
        Assert.That(fluvial.DepositedMassLastStep, Is.EqualTo(fluvial.ErodedMassLastStep)
            .Within(fluvial.ErodedMassLastStep * 1e-4),
            "负载本步内必须全部落定：Σ侵蚀 = Σ沉积");
    }

    [Test]
    public void Transport_UpperReachErodes_DepositionConcentratesDownstream()
    {
        var (isLand, height, hop) = BuildTerrain();
        var fields = MakeFields(height.Length);
        var fluvial = new H3FluvialTransport(Ball);
        fluvial.Apply(fields, height, ConstantPrecip(height.Length, 1f), scale: 1f);

        // 上游（距海 ≥2 跳）啃床
        int erodedLand = 0;
        for (int i = 0; i < height.Length; i++)
            if (isLand[i] && hop[i] >= 2 && fluvial.ErodedPerCell[i] > 0) erodedLand++;
        Assert.Greater(erodedLand, 0, "上游径流格应有啃床");

        // 淤积比侵蚀更靠海：落淤格的平均跳数显著小于冲刷格
        double eroHopSum = 0; int eroCount = 0;
        double depHopSum = 0; int depCount = 0;
        for (int i = 0; i < height.Length; i++)
        {
            if (fluvial.ErodedPerCell[i] > 0) { eroHopSum += hop[i]; eroCount++; }
            if (fluvial.DepositedPerCell[i] > 0) { depHopSum += hop[i]; depCount++; }
        }
        Assert.Greater(eroCount, 0);
        Assert.Greater(depCount, 0);
        Assert.Less(depHopSum / depCount, eroHopSum / eroCount,
            "淤积应集中在下游（搬运距离 > 1 格的机制断言）");
        Assert.Greater(fluvial.DeliveredToOceanLastStep, 0, "应有人海落淤（三角洲/大陆架料）");
    }

    [Test]
    public void EndorheicPit_CatchesSubcatchment_AndDoesNotErode()
    {
        var (isLand, height, hop) = BuildTerrain();
        var fields = MakeFields(height.Length);

        // 内流洼地：任取距海 ≥3 跳的陆格压到近海面（低于全部邻居 → 链终点）
        int pit = -1;
        for (int i = 0; i < height.Length; i++)
        {
            if (isLand[i] && hop[i] >= 3) { pit = i; height[i] = 50f; break; }
        }
        Assert.GreaterOrEqual(pit, 0, "res1 热带陆桥应有距海 ≥3 跳的格");

        var fluvial = new H3FluvialTransport(Ball);
        fluvial.Apply(fields, height, ConstantPrecip(height.Length, 1f), scale: 1f);

        Assert.AreEqual(0f, fluvial.ErodedPerCell[pit], "洼地无下坡 → 坡度 0 → 只淤不冲");
        Assert.Greater(fluvial.DepositedPerCell[pit], 0, "洼地应接住子汇来沙");
        for (int i = 0; i < height.Length; i++)
        {
            if (!isLand[i] || i == pit) continue;
            Assert.LessOrEqual(fluvial.DepositedPerCell[i], fluvial.DepositedPerCell[pit],
                $"格 {i} 的落淤不应超过链终点的洼地（坡面沉积帽内才可能，量级必小）");
        }
    }

    [Test]
    public void Deterministic_SameInputsBitwiseIdentical()
    {
        var (isLand, height, _) = BuildTerrain();

        var a = MakeFields(height.Length);
        var fa = new H3FluvialTransport(Ball);
        fa.Apply(a, height, ConstantPrecip(height.Length, 1f), scale: 1f);

        var b = MakeFields(height.Length);
        var fb = new H3FluvialTransport(Ball);
        fb.Apply(b, height, ConstantPrecip(height.Length, 1f), scale: 1f);

        CollectionAssert.AreEqual(a.ConservedPools(), b.ConservedPools(), "五场逐位不一致");
        Assert.AreEqual(fa.ErodedMassLastStep, fb.ErodedMassLastStep);
        Assert.AreEqual(fa.DepositedMassLastStep, fb.DepositedMassLastStep);
        GC.KeepAlive(isLand);
    }

    // ── 水力几何（v1.22，2026-09-18：侵蚀力度 = 水量 × 水速，τ-τc 给侵蚀选择性）──

    [Test]
    public void Hydraulics_VelocityAndShearWithinPhysicalBand()
    {
        // 流速判读口：真实大河 1–2 m/s 量级——爬坡地形（res1 单格即大河）应落在物理带内，
        // 且 τ = ρg·d·S 与流速同源为正。非河道格两者恒 0。
        var (_, height, _) = BuildTerrain();
        var fields = MakeFields(height.Length);
        var fluvial = new H3FluvialTransport(Ball);
        fluvial.Apply(fields, height, ConstantPrecip(height.Length, 1f), scale: 1f);

        int flowing = 0;
        for (int i = 0; i < height.Length; i++)
        {
            if (fluvial.VelocityPerCell[i] <= 0f) continue;
            flowing++;
            Assert.Less(fluvial.VelocityPerCell[i], 20f, $"格 {i}：流速离谱（真实大河 1–2 m/s 量级）");
            Assert.Greater(fluvial.VelocityPerCell[i], 0.05f, $"格 {i}：慢到不像河");
            Assert.Greater(fluvial.ShearPerCell[i], 0f, $"格 {i}：有流速必有床面剪切力");
        }
        Assert.Greater(flowing, 0, "爬坡地形应当有流动的河");
    }

    [Test]
    public void Hydraulics_ShearGate_PerCell_NoPoolErodedBelowItsTauCritical()
    {
        // τ-τc 门的**逐格**精确口径（v1.22）：床面剪切力 ≤ 池的临界值 ⇒ 该池在该格必须分毫未动。
        // 缓坡（1 m/跳）与陡坡（200 m/跳）两套地形都验——缓坡世界的海岸格/大河口照样能凑出高 τ
        // （τ ∝ Q^0.4·S^0.8），门必须一格一池都对得上。陡坡世界同时验证"门会开"（基岩被啃）。
        var taus = new float[]
        {
            H3FluvialTransport.TauCriticalSedimentPa,
            H3FluvialTransport.TauCriticalSedimentaryPa,
            H3FluvialTransport.TauCriticalMetamorphicPa,
            H3FluvialTransport.TauCriticalFelsicPlutonicPa,
            H3FluvialTransport.TauCriticalFelsicVolcanicPa,
        };

        foreach (float slope in new float[] { 1f, 200f })
        {
            var (_, height, _) = BuildTerrain(slope);
            var fields = MakeFields(height.Length);
            var before = new float[5][];
            for (int p = 0; p < 5; p++)
                before[p] = (float[])fields.ConservedPools()[p].Clone();

            var fluvial = new H3FluvialTransport(Ball);
            fluvial.Apply(fields, height, ConstantPrecip(height.Length, 1f), 1f);

            int gateOpens = 0;
            for (int i = 0; i < height.Length; i++)
            {
                float shear = fluvial.ShearPerCell[i];
                for (int p = 0; p < 5; p++)
                {
                    float lost = before[p][i] - fields.ConservedPools()[p][i];
                    if (lost > 1e-3f)
                    {
                        Assert.Greater(shear, taus[p],
                            $"坡 {slope} m/跳：格 {i} 池 {p} 被啃但 τ = {shear} ≤ τc = {taus[p]}（剪切门漏了）");
                        if (p >= 1) gateOpens++;
                    }
                }
            }
            if (slope == 200f)
                Assert.Greater(gateOpens, 0, "陡坡世界应有基岩池被啃（剪切门打开）");
        }
    }

    [Test]
    public void Hydraulics_TauSelectivity_StrongerShearEatsHarderPools()
    {
        // 选择性的梯度口径（v1.22）：啃得动硬池（变质 80 / 深成 60）的格，τ 必然也超软池 τc；
        // τ 落在 30–80 之间的格只能啃松散/沉积岩档以下。中坡地形（20 m/跳）τ 跨越多个 τc 档。
        var (_, height, _) = BuildTerrain(20f);
        var fields = MakeFields(height.Length);
        for (int i = 0; i < fields.Count; i++)
            if (height[i] > 0f) fields.Sediment[i] = 2000f;    // 铺松散沉积

        var before = new float[5][];
        for (int p = 0; p < 5; p++)
            before[p] = (float[])fields.ConservedPools()[p].Clone();

        var fluvial = new H3FluvialTransport(Ball);
        fluvial.Apply(fields, height, ConstantPrecip(height.Length, 1f), 1f);
        Assert.Greater(fluvial.ErodedMassLastStep, 0.0, "中坡世界应有侵蚀");

        var pools = fields.ConservedPools();
        int hardEroded = 0, softOnly = 0;
        for (int i = 0; i < height.Length; i++)
        {
            float shear = fluvial.ShearPerCell[i];
            bool hard = before[2][i] - pools[2][i] > 1e-3f       // metamorphic（τc = 80）
                     || before[3][i] - pools[3][i] > 1e-3f;      // felsic plutonic（τc = 60）
            bool soft = before[0][i] - pools[0][i] > 1e-3f       // sediment（τc = 1）
                     || before[1][i] - pools[1][i] > 1e-3f;      // sedimentary（τc = 30）
            if (hard) { hardEroded++; Assert.Greater(shear, 30f, $"格 {i}：啃硬池必须 τ > 30 Pa"); }
            else if (soft) softOnly++;
        }
        Assert.Greater(hardEroded, 0, "中坡地形应当存在 τ 超基岩档的格（海岸/大河口）");
        Assert.Greater(softOnly, 0, "也应当存在只啃得动软池的格（选择性梯度）");
    }

    [Test]
    public void Pipeline_FluvialOn_MassLedgerStillCloses()
    {
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(4, 42);
        var sim = new H3DynamicTectonics(Ball) { RunMy = 12f, StepMy = 4f };   // 3 步短跑
        Assert.IsTrue(sim.EnableFluvial, "河流输沙默认应开启");
        sim.Initialize(plateOfCell, 42);
        sim.RunWithProgress(null);

        double total = sim.TotalCrustMass();
        double expected = sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal;
        Assert.Greater(sim.InitialCrustMass, 0.0);
        double relative = Math.Abs(total - expected) / sim.InitialCrustMass;
        Assert.Less(relative, 1e-4,
            $"质量账本不闭合（相对误差 {relative:G3}）：输沙必须零审计记账（组内搬运）");
    }
}
