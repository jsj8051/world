using System;

namespace World.CivSim;

/// <summary>
/// 确定性随机（SplitMix64 状态可序列化）。
/// 读档续跑无分叉的关键：Random 状态入档（.cmp v4 头部 8B），Continue 从存档状态继续消耗，
/// 与"从头跑 N+M ticks"的随机序列完全一致。
/// 继承 System.Random：现有调用点（NextDouble/Next/Next(int)）多态兼容。
/// </summary>
public sealed class DeterministicRandom : Random
{
    private ulong _state;

    public DeterministicRandom(int seed) : base(seed)
    {
        _state = (ulong)seed + 0x9E3779B97F4A7C15UL;
        NextU64();   // 预热（种子扩散）
    }

    /// <summary>从存档状态恢复（必须与写入时的状态完全一致）。</summary>
    public DeterministicRandom(ulong state) : base((int)(state & 0x7FFFFFFF))
    {
        _state = state;
    }

    /// <summary>当前状态（入档 8B）。</summary>
    public ulong State => _state;

    /// <summary>SplitMix64 单步。</summary>
    private ulong NextU64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    protected override double Sample() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    public override int Next() => (int)(NextU64() & 0x7FFFFFFF);

    public override int Next(int maxValue)
    {
        if (maxValue <= 0) return 0;
        return (int)(NextU64() % (ulong)maxValue);
    }

    public override int Next(int minValue, int maxValue)
    {
        if (maxValue <= minValue) return minValue;
        return minValue + (int)(NextU64() % (ulong)(maxValue - minValue));
    }

    public override void NextBytes(byte[] buffer)
    {
        if (buffer == null) return;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)(NextU64() >> 56);
    }
}
