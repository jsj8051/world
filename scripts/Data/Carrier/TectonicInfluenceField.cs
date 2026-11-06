using System;

namespace World.Data;

/// <summary>单一构造通道的逐格最近来源；SourceIndex 引用同次生成的 Sources。</summary>
public sealed class TectonicInfluenceField
{
    /// <summary>该通道没有任何来源：数组长度 0，<see cref="SourceIndex"/> 全为空；消费方须先看 <see cref="Sources"/>。</summary>
    public static TectonicInfluenceField Empty(TectonicChannel channel) =>
        new(channel, Array.Empty<TectonicSource>(), Array.Empty<float>(), Array.Empty<int>());

    public TectonicChannel Channel { get; }
    public TectonicSource[] Sources { get; }
    public float[] DistanceKm { get; }
    public int[] SourceIndex { get; }

    public TectonicInfluenceField(TectonicChannel channel, TectonicSource[] sources,
        float[] distanceKm, int[] sourceIndex)
    {
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));
        DistanceKm = distanceKm ?? throw new ArgumentNullException(nameof(distanceKm));
        SourceIndex = sourceIndex ?? throw new ArgumentNullException(nameof(sourceIndex));
        if (distanceKm.Length != sourceIndex.Length)
            throw new ArgumentException("距离与来源数组长度不一致", nameof(sourceIndex));
        for (int i = 0; i < sources.Length; i++)
            if (sources[i].Channel != channel)
                throw new ArgumentException("来源通道与影响场不一致", nameof(sources));
        for (int i = 0; i < sourceIndex.Length; i++)
            if (sourceIndex[i] < -1 || sourceIndex[i] >= sources.Length)
                throw new ArgumentException("来源索引越界", nameof(sourceIndex));
        Channel = channel;
    }
}
