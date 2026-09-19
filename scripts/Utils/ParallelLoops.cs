using System;
using System.Threading.Tasks;

namespace World.Utils;

// 确定性并行地图循环（数据结构/算法之外的第三条路：多线程吃满多核）。
//
// 只用于**地图型**核函数：每个输出元素恰好被一个迭代写一次、迭代间无读写耦合
//（Laplacian、逐格物理量缓存、驱动速度场、降水/温度场这类）。逐元素浮点算式与
// 串行版逐位一致，因此同入参结果与串行版逐位相同——模拟的"同 seed 逐位可复现"
// 纪律不受并行影响。**约简型**循环（Σ、float 累加）不许走这里：求和顺序随调度变化
// 会破坏逐位一致（确需并行的整型计数用线程局部 + 整数合并——整数加法精确且可结合）。
//
// 门槛：格数低于 <see cref="MinCellsForParallel"/> 或单核时直跑串行——并行调度的
// 固定开销（分区 + 线程池往返 ~几十 µs）在小场上反而变慢。res3（41 162 格）起并行。
public static class ParallelLoops
{
	/// <summary>并行门槛（格数）：res3 = 41 162 格起净收益为正；res1/res2（测试与判读小档）串行。</summary>
	public const int MinCellsForParallel = 1 << 15;   // 32 768

	/// <summary>并行 worker 数（≤1 = 串行；进程内模拟可在测试宿主与 Godot 主线程两种环境跑）。</summary>
	public static int WorkerCount => Math.Clamp(Environment.ProcessorCount, 1, 64);

	/// <summary>地图型并行 for：body(i) 对每个 i 独立读写（写只落 i 自己的输出位）。</summary>
	public static void For(int count, Action<int> body)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (count == 0) return;
		if (count < MinCellsForParallel || WorkerCount <= 1)
		{
			for (int i = 0; i < count; i++) body(i);
			return;
		}
		Parallel.For(0, count, body);
	}

	/// <summary>带线程局部约简的地图型并行 for（**仅限精确可结合的合并**：整数计数、min/max）。
	/// <paramref name="combine"/> 按 worker 各调一次、锁内串行执行——合并结果只由合并运算的
	/// 可结合性决定，与调度无关（⚠️ 浮点 Σ 不可结合，仍不许走本口；无锁 combine 会被
	/// Parallel.For 在多 worker 上并发调用，连 max 都可能丢更新——本竞态实测抓到过）。</summary>
	public static void ForWithLocal<TLocal>(int count, Func<TLocal> localInit,
		Func<int, ParallelLoopState, TLocal, TLocal> body, Action<TLocal> combine)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (count == 0) return;
		if (count < MinCellsForParallel || WorkerCount <= 1)
		{
			TLocal local = localInit();
			for (int i = 0; i < count; i++) local = body(i, null, local);
			combine(local);
			return;
		}
		object combineGate = new();
		Parallel.For(0, count, localInit, body,
			local => { lock (combineGate) combine(local); });
	}
}
