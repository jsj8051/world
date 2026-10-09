using System;
using Godot;
using NUnit.Framework;
using World.Utils;

namespace World.Tests;

/// <summary>
/// 球面 3D 噪声 + fBm 分形（`SphericalFbmNoise`）测试。断言的测量项：
///   · 确定性：同种子同方向逐位一致，且**与调用顺序无关**（纯哈希、无 rng 状态）；
///   · 无缝 + 连续：球面上小角步长的值差 ≪ 大角步长（分形场；白噪声下两者相等）；
///   · 值域 ≈ [−1,1]、非恒定、均值 ≈ 0（有结构而不是常数场）；
///   · 倍频语义：层数越多，相邻取值的高频细节越强；
///   · 参数守卫：非法基波长 / 倍频因子 / 振幅衰减当场抛。
/// 纪律（同其余逻辑层测试）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// 本类纯托管（Godot.Vector3 是结构体），**无引擎宿主即可跑**——这正是自写噪声而非用
/// FastNoiseLite 的原因，本文件就是该纪律的回归判据。
/// </summary>
public class SphericalFbmNoiseTests
{
    // 球面均匀方向（Fibonacci 球：确定性、无极点堆积）
    static Vector3[] FibonacciSphere(int count)
    {
        var dirs = new Vector3[count];
        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < count; i++)
        {
            float z = 1f - 2f * (i + 0.5f) / count;
            float radius = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            float theta = golden * i;
            dirs[i] = new Vector3(radius * MathF.Cos(theta), z, radius * MathF.Sin(theta));
        }
        return dirs;
    }

    // 把方向沿某条切向转 angleRad 弧度（用于"相邻方向"连续性取样）
    static Vector3 Rotated(Vector3 dir, float angleRad)
    {
        Vector3 helper = MathF.Abs(dir.Y) < 0.9f ? new Vector3(0f, 1f, 0f) : new Vector3(1f, 0f, 0f);
        Vector3 tangent = dir.Cross(helper).Normalized();
        return (dir * MathF.Cos(angleRad) + tangent * MathF.Sin(angleRad)).Normalized();
    }

    // 0.01 弧度 ≈ 64 km（球半径 6371 km）——远小于基波长，是"相邻样本"
    const float SmallAngleRad = 0.01f;
    const float WideAngleRad = 1.5f;      // ≈ 9545 km，远大于基波长 → 去相关

    [Test]
    public void Sample_SameSeedSameDirection_BitwiseIdentical()
    {
        var a = new SphericalFbmNoise(42, 2400f, 5);
        var b = new SphericalFbmNoise(42, 2400f, 5);
        foreach (var dir in FibonacciSphere(256))
            Assert.AreEqual(a.Sample(dir), b.Sample(dir), "同种子同方向必须逐位一致");
    }

    [Test]
    public void Sample_DifferentSeed_DifferentField()
    {
        var a = new SphericalFbmNoise(42, 2400f, 5);
        var b = new SphericalFbmNoise(43, 2400f, 5);
        int different = 0;
        foreach (var dir in FibonacciSphere(256))
            if (MathF.Abs(a.Sample(dir) - b.Sample(dir)) > 1e-4f) different++;
        Assert.Greater(different, 200, "不同 seed 应给出不同的场（绝大多数方向取值不同）");
    }

    [Test]
    public void Sample_IsOrderIndependent_SameValueWhenResampled()
    {
        var noise = new SphericalFbmNoise(7, 1800f, 4);
        var dirs = FibonacciSphere(128);
        var forward = new float[dirs.Length];
        for (int i = 0; i < dirs.Length; i++) forward[i] = noise.Sample(dirs[i]);

        // 逆序采样（并穿插采样别的方向，确认场没有"上次调用"的残留状态）
        var backward = new float[dirs.Length];
        for (int i = dirs.Length - 1; i >= 0; i--)
        {
            noise.Sample(dirs[(i + 37) % dirs.Length]);
            noise.Sample(new Vector3(0f, 0f, 1f));
            backward[i] = noise.Sample(dirs[i]);
        }
        CollectionAssert.AreEqual(forward, backward, "采样结果不得依赖调用顺序");
    }

    [Test]
    public void Sample_SmallStepDiffersFarLessThanWideStep_SeamlessAndContinuous()
    {
        var noise = new SphericalFbmNoise(2026, 2400f, 5);
        double smallSum = 0, wideSum = 0;
        var dirs = FibonacciSphere(512);
        foreach (var dir in dirs)
        {
            smallSum += MathF.Abs(noise.Sample(Rotated(dir, SmallAngleRad)) - noise.Sample(dir));
            wideSum += MathF.Abs(noise.Sample(Rotated(dir, WideAngleRad)) - noise.Sample(dir));
        }
        double small = smallSum / dirs.Length, wide = wideSum / dirs.Length;
        Assert.Less(small, 0.08, $"相邻（64 km）取值差过大 = 场不连续（{small:F3}）");
        Assert.Greater(wide, 0.25, $"大跨距取值应去相关（{wide:F3}）");
        Assert.Less(small, wide * 0.2, "相邻差应远小于跨距差（白噪声下两者同量级）");
    }

    [Test]
    public void Sample_StaysWithinUnitRange_HasStructureAndZeroMean()
    {
        var noise = new SphericalFbmNoise(99, 2400f, 5);
        float min = float.MaxValue, max = float.MinValue;
        double sum = 0;
        int strong = 0;
        var dirs = FibonacciSphere(2000);
        foreach (var dir in dirs)
        {
            float v = noise.Sample(dir);
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            if (MathF.Abs(v) > 0.05f) strong++;
        }
        Assert.GreaterOrEqual(min, -1.3f, "下界越界（容差留给 Perlin 理论极值）");
        Assert.LessOrEqual(max, 1.3f, "上界越界");
        Assert.Greater(max - min, 0.9f, $"场跨度过小（{max - min:F2}）= 近似常数场");
        Assert.Less(MathF.Abs((float)(sum / dirs.Length)), 0.15f, "全场均值应 ≈ 0（起伏双向对称）");
        Assert.Greater(strong, dirs.Length * 0.5, "过半方向应有可测量的取值（不是到处趋零）");
    }

    [Test]
    public void Sample_MoreOctaves_AddsHighFrequencyDetail()
    {
        var dirs = FibonacciSphere(256);
        double fine = MeanNeighborDifference(new SphericalFbmNoise(5, 4000f, octaves: 1), dirs);
        double fractal = MeanNeighborDifference(new SphericalFbmNoise(5, 4000f, octaves: 6), dirs);
        Assert.Greater(fractal, fine * 1.5,
            $"倍频层数增加应叠加高频细节（1 层 {fine:F4} vs 6 层 {fractal:F4}）");
    }

    static double MeanNeighborDifference(SphericalFbmNoise noise, Vector3[] dirs)
    {
        double sum = 0;
        foreach (var dir in dirs)
            sum += MathF.Abs(noise.Sample(Rotated(dir, 0.02f)) - noise.Sample(dir));
        return sum / dirs.Length;
    }

    [Test]
    public void Constructor_InvalidArguments_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SphericalFbmNoise(1, 0f), "基波长须为正");
        Assert.Throws<ArgumentOutOfRangeException>(() => new SphericalFbmNoise(1, -100f), "基波长须为正");
        Assert.Throws<ArgumentOutOfRangeException>(() => new SphericalFbmNoise(1, 1000f, 5, 1f), "倍频因子须 > 1");
        Assert.Throws<ArgumentOutOfRangeException>(() => new SphericalFbmNoise(1, 1000f, 5, 2f, 1f), "振幅衰减须 < 1");
        Assert.Throws<ArgumentOutOfRangeException>(() => new SphericalFbmNoise(1, 1000f, 5, 2f, 0f), "振幅衰减须 > 0");
        Assert.DoesNotThrow(() => new SphericalFbmNoise(1, 1000f, 0), "层数非法应被钳到下限而不是抛");
    }
}
