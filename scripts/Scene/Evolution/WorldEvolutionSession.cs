using System;
using System.Threading;
using System.Threading.Tasks;
using World.Data;
using World.Data.Work;
using World.Logic;

namespace World.Scene;

/// <summary>独占播放与后台请求状态。计算线程只产出候选快照，由场景线程提交。
/// 进度只通过 <see cref="Signal"/> 发出；不认识 LoadOperation/UI。</summary>
internal sealed class WorldEvolutionSession(WorldGenSimulation world, EvolutionSimulation solver) : IWorkSignalSource
{
	readonly record struct Result(WorldFacts Source, WorldGenSimulation.State Candidate, Exception Failure);

	readonly CancellationTokenSource _stop = new();
	Task<Result> _work;
	double _pendingYears, _yearsPerSecond = 100_000;

	public event Action<WorkSignal> Signal;

	EvolutionClock Clock => world.Facts.Evolution.Clock;
	public bool Busy => _work != null;
	public EvolutionControlStatus Status { get; private set; } = EvolutionControlStatus.Paused;

	public void Play()
	{
		if (!Busy && !_stop.IsCancellationRequested && Status == EvolutionControlStatus.Paused)
			Status = EvolutionControlStatus.Playing;
	}

	public void Pause()
	{
		if (Status != EvolutionControlStatus.Playing) return;
		Status = EvolutionControlStatus.Paused;
		_pendingYears = 0;
	}

	public void ConfirmFault()
	{
		if (!_stop.IsCancellationRequested && Status == EvolutionControlStatus.Faulted)
			Status = EvolutionControlStatus.Paused;
	}

	public void SetSpeed(double yearsPerSecond)
	{
		if (!(yearsPerSecond > 0) || !double.IsFinite(yearsPerSecond))
			throw new ArgumentOutOfRangeException(nameof(yearsPerSecond));
		_yearsPerSecond = yearsPerSecond;
	}

	public void Step()
	{
		if (!Busy && !_stop.IsCancellationRequested && Status == EvolutionControlStatus.Paused)
			Start(checked(Clock.StepIndex + 1));
	}

	public string Seek(double targetMa)
	{
		if (Busy) return "正在计算，请等当前操作完成。";
		if (_stop.IsCancellationRequested || Status is not (EvolutionControlStatus.Paused or EvolutionControlStatus.Playing))
			return "当前状态无法跳转。";
		EvolutionClock clock = Clock;
		double step = Math.Round(targetMa * 1_000_000d / clock.StepYears);
		if (!(targetMa >= 0) || !(step < (double)long.MaxValue))
			return "目标时间超出可用范围。";
		if (step < clock.StepIndex)
			return $"只能向前跳转。当前时间为 {clock.TimeMa:F2} Ma。";
		if (step == clock.StepIndex)
			return $"目标对应当前步（{clock.TimeMa:F2} Ma）。";

		Pause();
		Status = EvolutionControlStatus.Seeking;
		Start((long)step);
		return null;
	}

	public void Stop()
	{
		if (_stop.IsCancellationRequested) return;
		_stop.Cancel();
		if (_work == null || _work.IsCompleted) _stop.Dispose();
		else _ = _work.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
		Status = EvolutionControlStatus.Uninitialized;
		_pendingYears = 0;
	}

	public WorldFacts Tick(double delta, Action<Exception> reportFailure)
	{
		if (Status == EvolutionControlStatus.Playing)
		{
			double stepYears = solver.Definition.StepYears;
			_pendingYears = Math.Min(_pendingYears + delta * _yearsPerSecond, 2 * stepYears);
		}

		if (_work != null)
		{
			if (!_work.IsCompleted) return null;
			Result result = _work.GetAwaiter().GetResult();
			_work = null;
			if (_stop.IsCancellationRequested) return null;
			if (!ReferenceEquals(result.Source, world.Facts))
			{
				Stop();
				return null;
			}
			if (result.Failure != null)
			{
				Pause();
				Status = EvolutionControlStatus.Faulted;
				Signal?.Invoke(WorkSignal.Failed(result.Failure.Message));
				reportFailure(result.Failure);
				return null;
			}

			if (Status == EvolutionControlStatus.Seeking) Status = EvolutionControlStatus.Paused;
			world.Publish(result.Candidate);
			Signal?.Invoke(WorkSignal.Finished());
			return result.Candidate.Facts;
		}

		if (Status == EvolutionControlStatus.Playing && _pendingYears >= Clock.StepYears)
		{
			EvolutionClock clock = Clock;
			_pendingYears -= clock.StepYears;
			Start(checked(clock.StepIndex + 1));
		}
		return null;
	}

	void Start(long targetStep)
	{
		WorldFacts source = world.Facts;
		CancellationToken stop = _stop.Token;
		_work = Task.Run(() =>
		{
			try
			{
				long timing = WorldBuildProfile.Start();
				var frame = solver.CalculateToStep(source.Evolution, targetStep, stop,
					(done, all) =>
					{
						if (all <= 1) Signal?.Invoke(WorkSignal.Started("step", $"构造步进 {done}/{all}"));
						else Signal?.Invoke(WorkSignal.Unit("step", $"构造步进 {done}/{all}", done, all));
					});
				WorldBuildProfile.Mark(ref timing, "tectonic-steps");
				Signal?.Invoke(WorkSignal.Started("rebuild", "世界重建"));
				return new Result(source, world.PrepareEvolution(solver, frame, stop), null);
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested) { return default; }
			catch (Exception error) { return new Result(source, null, error); }
		});
	}
}
