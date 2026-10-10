using NUnit.Framework;
using World.Data;                 // WorldSpec（世界定义：参数实例）
using World.Params;               // WorldParams（参数管理器实例）＋ WorldParamStore（参数文件 I/O）

namespace World.Tests;

/// <summary>
/// **测试用的世界默认档**——经参数管理器读正库默认档 `res/params/world_params.json`。
///
/// ★为什么不在这里再写一份字面量：默认世界**只**存在于那份数据文件里，测试自己写一份就又是"第二个源"，
///   还会掩盖"正库档被改坏"——而"档被改坏"正是本类要暴露的事（逐值守卫见 `WorldSpecTests`）。
/// ★**读到的是独立副本**（本类自建一个 `WorldParams` 实例并 `Reload`）而**不是**生产那个管理器
///   持有的实例：测试要拿它改字段做用例，也不能被用户档或别处的写盘影响（实例化之后这是天然的）。
/// ★静态构造器一次性读入（不用字段初始化器：那会依赖声明顺序，历史上踩过坑）。
/// </summary>
public static class WorldPreset
{
	/// <summary>正库默认档组装成的世界定义（"地球档"）——**独立副本**，供测试自由改。</summary>
	public static WorldSpec Earth { get; }

	static WorldPreset()
	{
		var params_ = new WorldParams();
		params_.Reload();

		Assert.That(params_.Active, Is.Not.Null,
			"正库默认档必须能装配成 WorldSpec，实得：" + string.Join(" | ", params_.LoadProblems));
		Assert.That(params_.LoadProblems, Is.Empty,
			"正库默认档必须干净可用，实得：" + string.Join(" | ", params_.LoadProblems));
		Earth = params_.Active;
	}
}
