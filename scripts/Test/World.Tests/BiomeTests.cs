using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.Constants;

namespace World.Tests;

/// <summary>
/// `World.Constants.BiomeType` 词表契约（2026-10-09 重写）。
///
/// ★本文件前身是「Biome 模块补充测试」（BiomeClassifier / BiomeColors / MonsoonSystem 等）——
///   那些被测类型随 D-3 清退与本次 `World.Domain` 解散**已全部不存在**；且原文件所有测试方法
///   都**漏了 `[Test]`**（NUnit 从不执行 ⇒ 长期"绿"是假象）。本次一并修正：只钉枚举本身，
///   并补回 `[Test]`，让钉子真正生效。
///
/// 钉住的事实（改枚举即须同步改此处）：
///   ① byte 基类型（曾按 byte 写入存档；值域 0–31）；
///   ② 词表边界 = **仅柯本气候型**（2026-10-09 用户拍板）：恰为 2,3,14…29 共 18 个值；
///   ③ 无重复值。
/// </summary>
public class BiomeTests
{
    /// <summary>词表边界（Köppen-only）：EF/ET + A/B/C/D 细类；删非柯本附加类后恰为此集。</summary>
    static readonly byte[] ExpectedKoppenValues =
    {
        2, 3,                       // EF / ET
        14, 15, 16,                 // Af / Am / Aw
        17, 18, 19, 20,             // BWh / BWk / BSh / BSk
        21, 22, 23, 24, 25,         // Cfa / Cfb / Cwa / Csa / Csb
        26, 27, 28, 29,             // Dfa / Dfb / Dfc / Dwa
    };

    [Test]
    public void BiomeType_IsByte_AndEveryValueDistinct()
    {
        // 曾按 byte 直接写入存档 ⇒ 基类型必须 byte
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(BiomeType)));

        var seen = new HashSet<byte>();
        foreach (var b in Enum.GetValues<BiomeType>())
        {
            byte v = (byte)b;
            Assert.LessOrEqual(v, 31, $"值 {v} 超出历史存档范围 0–31");
            Assert.True(seen.Add(v), $"枚举值 {v} 重复（序列化歧义）");
        }
    }

    [Test]
    public void BiomeType_IsKoppenOnly_ExactlyExpectedSet()
    {
        var actual = new List<byte>();
        foreach (var b in Enum.GetValues<BiomeType>()) actual.Add((byte)b);
        actual.Sort();

        Assert.AreEqual(ExpectedKoppenValues.Length, actual.Count,
            "BiomeType 词表必须恰为柯本气候型 2,3,14…29（18 值）——" +
            "回填非柯本附加类（DeepOcean 0 / Ocean 1 / Alpine 12 / Riparian 13 / FrigidOcean 30 / " +
            "TropicalOcean 31）会破坏词表边界契约；若确需海格/地形附加类，应另立事实" +
            "（见 docs/裁决-Domain解散与BiomeType归Constant.md）");
        for (int i = 0; i < ExpectedKoppenValues.Length; i++)
            Assert.AreEqual(ExpectedKoppenValues[i], actual[i],
                $"第 {i} 个枚举值应为 {ExpectedKoppenValues[i]}（词表按值升序）");
    }
}
