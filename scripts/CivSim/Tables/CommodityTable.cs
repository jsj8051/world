using System;
using System.Collections.Generic;

using World.CivSim.Entities;
namespace World.CivSim;

/// <summary>商品大类：Food 参与饥荒（人口消耗），Material 只存+衰变（贸易备货）。</summary>
public enum CommodityKind { Food, Material }

/// <summary>
/// 商品目录注册表（2026-08-18 阶段3 存储/衰变机制）。
/// 动态扩展：加新商品 = 注册一行（+存档版本 bump，读旧档补 0）——不碰机制代码。
/// 每商品：Kind（是否食物）、BaseDecay（**商品自身年衰变率**——"特定食物耐存储"：
/// 谷物耐储远优于浆果/肉，这是农业→定居→文明因果链的涌现基础）、Consumed（是否被人口消耗）、
/// Produce（每 tick 流入，从 Band 的 F 分量提取）。
/// 实际衰变 = BaseDecay × techMult（techMult 按 storage/pottery/settle/grinding 分层，见 CivEngine.AccumulateStorage）。
/// </summary>
public static class CommodityTable
{
    public sealed class CommodityDef
    {
        public string Id;
        public string Name;
        public CommodityKind Kind;
        public float BaseDecay;      // 年衰变率（商品自身；技术分层在其上 × techMult）
        public bool Consumed;        // Food：被人口消耗（P 人当量·年/人）
        public Func<Band, float> Produce;   // 每 tick 流入（人当量·年/tick 口径 → 年步进内均摊）
    }

    public const string Grain = "grain";
    public const string Berry = "berry";
    public const string Meat = "meat";
    public const string Leather = "leather";
    public const string Wool = "wool";
    public const string Straw = "straw";

    private static readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public static readonly CommodityDef[] All =
    {
        // ── Food（参与饥荒；消耗 = P 人当量·年/人）──
        new() { Id = Grain,   Name = "谷物",   Kind = CommodityKind.Food,     BaseDecay = 0.08f, Consumed = true,
                Produce = e => e.FFarmLast },                                 // 农业谷物（耐储——新石器革命核心）
        new() { Id = Berry,   Name = "浆果",   Kind = CommodityKind.Food,     BaseDecay = 0.5f,  Consumed = true,
                Produce = e => e.FBerryLast },                                // 鲜果（不耐储，数天-数周）
        new() { Id = Meat,    Name = "猎物/奶肉", Kind = CommodityKind.Food,  BaseDecay = 0.4f,  Consumed = true,
                Produce = e => Math.Max(0f, e.FHuntLast - e.FBerryLast) + e.FHerdLast },        // 猎物（除浆果）+ 畜牧奶肉
        // ── Material（只存+衰变；贸易备货）──
        new() { Id = Leather, Name = "皮革",   Kind = CommodityKind.Material, BaseDecay = 0.03f, Consumed = false,
                Produce = e => e.FHuntLast * CivSimContext.LeatherRate },
        new() { Id = Wool,    Name = "羊毛",   Kind = CommodityKind.Material, BaseDecay = 0.02f, Consumed = false,
                Produce = e => e.FHerdLast * CivSimContext.WoolRate },
        new() { Id = Straw,   Name = "秸秆",   Kind = CommodityKind.Material, BaseDecay = 0.01f, Consumed = false,
                Produce = e => e.FFarmLast * CivSimContext.StrawRate },
    };

    static CommodityTable()
    {
        for (int i = 0; i < All.Length; i++) _index[All[i].Id] = i;
    }

    public static int Count => All.Length;

    public static int Index(string id) => _index.TryGetValue(id, out int i) ? i : -1;

    /// <summary>每部落库存数组（长度=目录数；惰性建）。读档/构造用。</summary>
    public static float[] NewStocks()
    {
        var a = new float[Count];
        return a;
    }
}
