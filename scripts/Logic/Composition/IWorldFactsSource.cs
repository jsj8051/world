namespace World.Logic;

/// <summary>
/// 只读世界事实来源：生成会话与导入地图结果都实现它；消费方不拿生成器或参数。
/// </summary>
public interface IWorldFactsSource
{
	WorldFacts Facts { get; }
}