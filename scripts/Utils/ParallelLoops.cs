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

	/// <summary>批次 2b（设计 11-2 §2.4）：**固定分块**地图型并行 for——「跨运行确定性」在框架层的兜底。
	/// 与 <see cref="For"/> 的区别：块的**数量与区间只由入参决定**（块数 = workers，
	/// 块 b = [b·count/B, (b+1)·count/B)），不依赖运行时调度器的动态分区 ⇒ 即使将来有人
	/// 误把归约型循环接进来，跨运行/跨线程数的划分也恒定可复现。body 仍须地图型
	///（写只落 i 自己的输出位）——此时结果与串行版逐位同。worker 数由**调用方专用字段**
	/// 传入（各调用方专用 worker 字段），不读全局 WorkerCount，避免外溢到
	/// 其他共用调用方（设计 11-2 §5 的 ★修正纪律）。</summary>
	public static void ForFixed(int count, int workers, Action<int> body)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (count == 0) return;
		int blockCount = Math.Clamp(workers, 1, 64);
		if (count < MinCellsForParallel || blockCount <= 1)
		{
			for (int i = 0; i < count; i++) body(i);
			return;
		}
		if (blockCount > count) blockCount = count;   // 块数 ≤ 格数（空块也无害，但省一趟调度）
		Parallel.For(0, blockCount, b =>
		{
			// 固定等分（b·count/B 的整数式）：同参数下划分恒同、与线程池调度无关。
			int lo = b * count / blockCount;
			int hi = (b + 1) * count / blockCount;
			for (int i = lo; i < hi; i++) body(i);
		});
	}
}
