using System;

namespace World.Data;

/// <summary>逐格壳型与陆壳省；<see cref="Type"/> 是壳型的唯一真源。</summary>
public sealed class CrustField
{
    public static readonly CrustField Empty =
        new(Array.Empty<CrustType>(), Array.Empty<int>(), Array.Empty<CrustProvince>());

    /// <summary>逐格壳型。</summary>
    public CrustType[] Type { get; }
    /// <summary>逐格陆壳省编号；洋壳 = −1。</summary>
    public int[] ProvinceId { get; }
    /// <summary>陆壳省表（下标 = 编号）。</summary>
    public CrustProvince[] Provinces { get; }

    public CrustField(CrustType[] type, int[] provinceId, CrustProvince[] provinces)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        ProvinceId = provinceId ?? throw new ArgumentNullException(nameof(provinceId));
        Provinces = provinces ?? throw new ArgumentNullException(nameof(provinces));
        if (type.Length != provinceId.Length)
            throw new ArgumentException($"壳型长度 {type.Length} ≠ 省编号长度 {provinceId.Length}", nameof(provinceId));

        var counted = new int[provinces.Length];
        for (int i = 0; i < provinceId.Length; i++)
        {
            int p = provinceId[i];
            if (p == -1)
            {
                if (type[i] != CrustType.Oceanic)
                    throw new ArgumentException($"格 {i} 是陆壳却没有省编号", nameof(provinceId));
                continue;
            }
            if ((uint)p >= (uint)provinces.Length || provinces[p].Id != p)
                throw new ArgumentException($"格 {i} 的省编号 {p} 非法", nameof(provinceId));
            if (provinces[p].Type != type[i])
                throw new ArgumentException($"格 {i} 的壳型与省 {p} 不符", nameof(type));
            counted[p]++;
        }
        for (int p = 0; p < provinces.Length; p++)
        {
            if (provinces[p].Id != p)
                throw new ArgumentException($"省 {p} 的编号不是 {p}", nameof(provinces));
            if (counted[p] != provinces[p].CellCount)
                throw new ArgumentException(
                    $"省 {p} 声明 {provinces[p].CellCount} 格，实际 {counted[p]} 格", nameof(provinces));
        }
    }
}
