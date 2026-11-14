using World.Data;

namespace World.Application.Pages;

/// <summary>
/// 地理演化页面需要的演化能力：状态只读、命令只表达意图。
/// 由世界侧适配；页面控制器不持有 WorldManager、节点或线程。
/// </summary>
internal interface IGeographyEvolutionControl
{
	EvolutionControlStatus Status { get; }
	bool Busy { get; }
	void Play();
	void Pause();
	void Step();
	void ConfirmFault();
	/// <summary>返回不能开始跳转的原因；开始跳转时返回 null。</summary>
	string Seek(double targetMa);
	void SetSpeed(double yearsPerSecond);
}
