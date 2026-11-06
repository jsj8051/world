using System;

namespace World.Data;

/// <summary>逐构造通道的影响场集合；缺失通道取 <see cref="TectonicInfluenceField.Empty"/>。</summary>
public sealed class TectonicInfluenceSet
{
    readonly TectonicInfluenceField[] _fields;

    public TectonicInfluenceSet(TectonicInfluenceField[] fields)
    {
        if (fields == null) throw new ArgumentNullException(nameof(fields));
        int count = Enum.GetValues<TectonicChannel>().Length;
        if (fields.Length != count)
            throw new ArgumentException($"影响场数 {fields.Length} ≠ 通道数 {count}", nameof(fields));
        for (int i = 0; i < count; i++)
            if ((int)fields[i].Channel != i)
                throw new ArgumentException($"下标 {i} 的通道不是 {fields[i].Channel}", nameof(fields));
        _fields = fields;
    }

    /// <summary>该通道是否有来源；无来源时 <see cref="Get"/> 返回空场。</summary>
    public bool Has(TectonicChannel channel) => _fields[(int)channel].Sources.Length > 0;

    public TectonicInfluenceField Get(TectonicChannel channel) => _fields[(int)channel];
}
