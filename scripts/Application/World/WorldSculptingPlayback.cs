using System;
using System.Threading;
using System.Threading.Tasks;
using World.Data;
using World.Logic;

namespace World.Application.World;

/// <summary>在后台顺序准备完整雕刻轮，在调用线程提交并发布。</summary>
internal sealed class WorldSculptingPlayback(WorldGenSimulation world, WorldSculptingGeneration sculpting)
{
	readonly record struct Result(WorldFacts Source, WorldSculptingGeneration.Candidate Candidate, Exception Failure);

	readonly CancellationTokenSource _stop = new();
	Task<Result> _work;
	bool _stopped;
	double _pendingRounds;
	double _roundsPerSecond = 1;
	long _seekTarget;

	internal bool Busy => _work != null || Status == EvolutionControlStatus.Seeking;
	internal EvolutionControlStatus Status { get; private set; } = EvolutionControlStatus.Paused;

	internal void Play()
	{
		if (!Busy && Status == EvolutionControlStatus.Paused) Status = EvolutionControlStatus.Playing;
	}

	internal void Pause()
	{
		if (Status != EvolutionControlStatus.Playing) return;
		Status = EvolutionControlStatus.Paused;
		_pendingRounds = 0;
	}

	internal void ConfirmFault()
	{
		if (Status == EvolutionControlStatus.Faulted) Status = EvolutionControlStatus.Paused;
	}

	internal void SetSpeed(double roundsPerSecond)
	{
		if (!double.IsFinite(roundsPerSecond) || roundsPerSecond <= 0)
			throw new ArgumentOutOfRangeException(nameof(roundsPerSecond));
		_roundsPerSecond = roundsPerSecond;
	}

	internal void Step()
	{
		if (!Busy && Status == EvolutionControlStatus.Paused) Start();
	}

	internal string Seek(long targetRound)
	{
		if (Busy) return "正在计算，请等当前操作完成。";
		if (Status is not (EvolutionControlStatus.Paused or EvolutionControlStatus.Playing))
			return "当前状态无法跳转。";
		long current = sculpting.CompletedRounds;
		if (targetRound < current) return $"只能向前跳转。当前为第 {current} 轮。";
		if (targetRound == current) return $"目标是当前轮（第 {current} 轮）。";

		_pendingRounds = 0;
		_seekTarget = targetRound;
		Status = EvolutionControlStatus.Seeking;
		Start();
		return null;
	}

	internal void Stop()
	{
		if (_stopped) return;
		_stopped = true;
		_stop.Cancel();
		if (_work == null || _work.IsCompleted) _stop.Dispose();
		else _ = _work.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
		Status = EvolutionControlStatus.Uninitialized;
	}

	internal WorldFacts Tick(double elapsedSeconds, Action<Exception> reportFailure)
	{
		if (_stopped) return null;
		if (Status == EvolutionControlStatus.Playing)
			_pendingRounds = Math.Min(_pendingRounds + elapsedSeconds * _roundsPerSecond, 1);

		if (_work != null)
		{
			if (!_work.IsCompleted) return null;
			var result = _work.GetAwaiter().GetResult();
			_work = null;
			if (!ReferenceEquals(result.Source, world.Facts))
			{
				Stop();
				return null;
			}
			if (result.Failure != null)
			{
				_pendingRounds = 0;
				Status = EvolutionControlStatus.Faulted;
				reportFailure(result.Failure);
				return null;
			}

			sculpting.Commit(result.Candidate);
			world.Publish(result.Candidate.Presentation);
			if (Status == EvolutionControlStatus.Seeking && sculpting.CompletedRounds == _seekTarget)
				Status = EvolutionControlStatus.Paused;
			return result.Candidate.Presentation.Facts;
		}

		if (Status == EvolutionControlStatus.Seeking) Start();
		else if (Status == EvolutionControlStatus.Playing && _pendingRounds >= 1)
		{
			_pendingRounds = 0;
			Start();
		}
		return null;
	}

	void Start()
	{
		WorldFacts source = world.Facts;
		CancellationToken cancellation = _stop.Token;
		_work = Task.Run(() =>
		{
			try { return new Result(source, sculpting.PrepareNext(cancellation), null); }
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return default; }
			catch (Exception error) { return new Result(source, null, error); }
		});
	}
}
