using NUnit.Framework;
using World.Data;                 // WorldSpec
using World.WorldGen;             // WorldParamTable（参数管理器）

namespace World.Tests;

/// <summary>
/// **测试 / 基准用的世界默认档**——经参数管理器读正库默认档 `data/world_params.json`。
///
/// ★为什么不在这里再写一份字面量：默认世界**只**存在于那份数据文件里（代码里没有默认值常量），
///   测试自己写一份就又是"第二个源"，还会掩盖"正库档被改坏"——而"档被改坏"正是本类要暴露的事
///   （逐值守卫见 `WorldSpecTests`）。路径解析与读盘都在 `WorldParamTable`，本类只是它的一个薄壳。
/// ★静态构造器一次性读入（不用字段初始化器：那会依赖声明顺序，历史上踩过坑）。
/// </summary>
public static class WorldPreset
{
	/// <summary>正库默认档组装成的世界定义（"地球档"）。</summary>
	public static WorldSpec Earth { get; }

	static WorldPreset()
	{
		Earth = WorldParamTable.Preset(out var problems);
		Assert.That(problems, Is.Empty,
			"正库默认档必须干净可用，实得：" + string.Join(" | ", problems));
	}
}
