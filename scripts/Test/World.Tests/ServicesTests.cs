using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.Domain;
using World.Services;

namespace World.Tests;

/// <summary>
/// 势力调色板 + 星球配色测试（L0 纯 C#；无引擎依赖）。
///
/// ★2026-10-06 D-B：本文件原含 3 个 `PlanetColors.ElevationToColor` 测试，
///   随 `scripts/Surface/PlanetColors.cs` 一并清退——该类型生产零消费者，
///   其"海拔归一化值 → 颜色"能力已由 `World.Utils.ColorRamp`（通用色带算法）
///   + 各处业务色带定义取代。
/// ⚠️ 保留的 `PowerPalette` 测试属 `World.Domain`（保留区），**不随本次清退**。
///
/// ★2026-10-09：随 Legacy 载体簇删除（`GameGrid` / `WildCropsSystem` / `MapData` / `FieldCodec`），
///   本文件移除了两个**从未被调用**的私有助手 `BuildMapData` / `Has`（`MapData` 已不存在）。
///   文件现只剩 `PowerPalette` 测试。
/// </summary>
public class EventBusTests
{
    [Test]
    public void Build_Empty_ReturnsEmpty()
    {
        Assert.AreEqual(0, PowerPalette.Build(new int[0]).Count);
    }

    [Test]
    public void Build_ContainsEveryId()
    {
        var ids = new List<int> { 9, 3, 7, 1, 42 };
        var pal = PowerPalette.Build(ids);
        Assert.AreEqual(ids.Count, pal.Count);
        foreach (int id in ids)
        {
            Assert.True(pal.ContainsKey(id), $"缺少势力 {id}");
            AssertColorValid(pal[id]);
        }
    }

    [Test]
    public void Build_Deterministic_IndependentOfInputOrder()
    {
        var a = new List<int> { 1, 3, 7, 42 };
        var b = new List<int> { 42, 7, 3, 1 };
        var pa = PowerPalette.Build(a);
        var pb = PowerPalette.Build(b);
        foreach (int id in a)
            Assert.AreEqual(pa[id], pb[id], $"势力 {id} 的颜色不应随输入顺序变化");
    }

    [Test]
    public void Build_ColorsWellSeparated()
    {
        foreach (int count in new[] { 5, 30, 100, 291 })
        {
            var ids = Enumerable.Range(10, count).ToList();
            var pal = PowerPalette.Build(ids);
            var colors = new List<Color>(pal.Values);
            Assert.AreEqual(count, colors.Count);
            float minDist = float.MaxValue;
            for (int i = 0; i < colors.Count; i++)
                for (int j = i + 1; j < colors.Count; j++)
                    minDist = Mathf.Min(minDist, PowerPalette.Dist(colors[i], colors[j]));
            Assert.GreaterOrEqual(minDist, 0.05f, $"count={count} 最小色距 {minDist:F4} 跌破肉眼阈值");
        }
    }

    [Test]
    public void Dist_IsL1Manhattan()
    {
        Assert.AreEqual(1f, PowerPalette.Dist(new Color(1f, 0f, 0f), new Color(0f, 0f, 0f)), 1e-6f);
        Assert.AreEqual(0.3f, PowerPalette.Dist(new Color(0f, 0.2f, 0f), new Color(0.1f, 0f, 0f)), 1e-6f);
        Assert.AreEqual(0f, PowerPalette.Dist(new Color(0.1f, 0.2f, 0.3f), new Color(0.1f, 0.2f, 0.3f)), 1e-6f);
        // 海色锚点 (0.10, 0.22, 0.48) 距黑 = 0.10+0.22+0.48 = 0.80
        Assert.AreEqual(0.8f, PowerPalette.Dist(new Color(0.10f, 0.22f, 0.48f), new Color(0f, 0f, 0f)), 1e-5f);
    }

    private static void AssertColorValid(Color c)
    {
        Assert.That(c.R, Is.InRange(0f, 1f));
        Assert.That(c.G, Is.InRange(0f, 1f));
        Assert.That(c.B, Is.InRange(0f, 1f));
        Assert.AreEqual(1f, c.A, 1e-6f);
    }

}