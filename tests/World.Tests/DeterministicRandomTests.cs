using NUnit.Framework;
using World.CivSim;

namespace World.Tests;

/// <summary>
/// 确定性基石测试：同 seed 同序列；读档续跑 = 从存档状态继续，与从头跑一致（防分叉 T04 类 bug）。
/// </summary>
public class DeterministicRandomTests
{
    [Test]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var a = new DeterministicRandom(42);
        var b = new DeterministicRandom(42);
        for (int i = 0; i < 200; i++)
        {
            Assert.AreEqual(a.Next(), b.Next());
            Assert.AreEqual(a.NextDouble(), b.NextDouble());
        }
    }

    [Test]
    public void DifferentSeeds_Diverge()
    {
        var a = new DeterministicRandom(1);
        var b = new DeterministicRandom(2);
        bool anyDiff = false;
        for (int i = 0; i < 100 && !anyDiff; i++)
            anyDiff = a.Next() != b.Next();
        Assert.True(anyDiff, "两个不同种子 100 次内应出现差异");
    }

    [Test]
    public void StateRoundTrip_ContinuesExactly()
    {
        var fresh = new DeterministicRandom(7);
        for (int i = 0; i < 10; i++) fresh.Next();
        ulong state = fresh.State;

        var restored = new DeterministicRandom(state);
        var reference = new DeterministicRandom(7);
        for (int i = 0; i < 10; i++) reference.Next();

        // 存档恢复的 RNG 必须与"从头跑到底"的序列完全一致（读档续跑无分叉）
        for (int i = 0; i < 100; i++)
            Assert.AreEqual(reference.Next(), restored.Next());
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(1000)]
    public void Next_MaxValue_StaysInBounds(int max)
    {
        var rng = new DeterministicRandom(123);
        for (int i = 0; i < 10000; i++)
        {
            int v = rng.Next(max);
            Assert.That(v, Is.InRange(0, max - 1));
        }
    }

    [TestCase(3, 9)]
    [TestCase(-5, 5)]
    [TestCase(7, 7)]
    public void Next_Range_RespectsContract(int min, int max)
    {
        var rng = new DeterministicRandom(456);
        for (int i = 0; i < 10000; i++)
        {
            int v = rng.Next(min, max);
            if (max <= min) Assert.AreEqual(min, v);
            else Assert.That(v, Is.InRange(min, max - 1));
        }
    }

    [Test]
    public void NextBytes_FillsBuffer()
    {
        var rng = new DeterministicRandom(9);
        var buf = new byte[64];
        rng.NextBytes(buf);
        bool anyNonZero = false;
        foreach (var b in buf) if (b != 0) anyNonZero = true;
        Assert.True(anyNonZero, "缓冲区应被非零随机字节填充");
    }
}
