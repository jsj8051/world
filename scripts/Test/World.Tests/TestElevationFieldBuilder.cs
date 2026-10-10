using Godot;
using World.Logic;

namespace World.Tests;

/// <summary>
/// **测试夹具**：可采样测试地形（决策 08 §9 · 清退判定的第三层）。
///
/// ★为什么是"提取夹具"而不是"把 ElevationFieldStack 搬进 tests 改名保留"：
///   测试为构造输入而引用某个生产类型，**不等于该生产类型仍是必要的抽象**。
///   搬运式保留 = 旧结构换个位置继续存活，正是清退要消除的形态。
///   正确做法是**提取测试真正需要的最小能力**（下面只有一条），
///   让夹具只存在于测试层，生产抽象可以被自由处置。
///
/// ★提取出的最小能力（5 条 H3TerrainSamplerTests 实际用到的全部）：
///   ① 有一个 `Sample(dir)` 可调用的连续场；
///   ② **确定性**（同参数两次构造逐位同）；
///   ③ 振幅量级**已知**（测试断言"采样管线不得放大 / 泄漏 Ball 状态"用的就是
///      这个量级论证：容差 0.1 m ⇔ 方向浮点差 ~1e-5 rad × 3000 m ≈ dm 级）。
///   测试**不需要**域扭曲、不需要 ridged 整形、不需要多尺度组合——
///   那些是 `ElevationFieldStack` 的生产语义，与采样器测试无关。
/// </summary>
static class TestElevationFieldBuilder
{
	/// <summary>造一个确定性测试高程场（米域，零均值附近，振幅 ≈ ±<paramref name="ampM"/>）。
	/// <para>实现 = `FbmField`（值域 ≈[-1,1]）套一层 `WeightedSumField`，权重 =振幅。
	/// 用**生产场族自己**而不是自定义测试类——否则会形成"用测试实现验证测试夹具"的闭环。</para>
	/// </summary>
	public static SphericalField Build(int seed = 42, float ampM = 3000f)
		=> new WeightedSumField((new FbmField(seed, 4000f, 4), ampM));
}
