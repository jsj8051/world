using System;
using System.Threading;
using World.Application.Generation;
using World.Application.Loading;
using World.Application.Pages;
using World.Data;
using World.H3Grid;
using World.Logic;
using World.Render;
using World.Utils;

namespace World.Application.World;

/// <summary>地形生成页拥有的世界会话：持有模拟、演化和当前事实，不依赖 Godot 场景树。</summary>
public sealed class WorldSession : ITickable, IGeographyEvolutionControl, IDisposable
{
	readonly Action<string> _log;
	readonly Action<Exception> _reportEvolutionFailure;
	WorldSculptingPlayback _playback;
	WorldSpec _spec;
	bool _disposed;

	public long? SculptingRoundIndex => Sim?.Facts?.SculptingRoundIndex;
	public bool EvolutionBusy => _playback?.Busy == true;
	public EvolutionControlStatus EvolutionStatus => _playback?.Status ?? EvolutionControlStatus.Uninitialized;
	public event Action<WorldFacts> WorldPublished;
	public WorldGenSimulation Sim { get; private set; }
	public bool IsInitialized { get; private set; }

	public WorldSession(Action<string> log = null, Action<Exception> reportEvolutionFailure = null)
	{
		_log = log;
		_reportEvolutionFailure = reportEvolutionFailure;
	}

	internal void Initialize(WorldSpec spec)
	{
		ThrowIfDisposed();
		if (IsInitialized) return;
		ArgumentNullException.ThrowIfNull(spec);
		var result = new WorldGenerationJob().Run(spec, WorldGenerationPresets.Production, CancellationToken.None);
		Install(result);
	}

	void LogGenerationSummary()
	{
		var facts = Sim.Facts;
		var scale = SpatialScale.Of(Sim.Ball);
		var height = facts.HeightM;
		var land = facts.FinalLand;
		var temperature = facts.TemperatureC;
		var latitude = Sim.Ball.Geo.LatRad;
		int n = height.Length;
		double landHeightSum = 0.0, temperatureSum = 0.0, landTemperatureSum = 0.0, polarTemperatureSum = 0.0;
		int landCount = 0, polarCount = 0;
		float low = height[0], high = height[0], cold = temperature[0], warm = temperature[0];
		for (int i = 0; i < n; i++)
		{
			if (height[i] < low) low = height[i];
			if (height[i] > high) high = height[i];
			if (land[i])
			{
				landCount++;
				landHeightSum += height[i];
				landTemperatureSum += temperature[i];
			}
			temperatureSum += temperature[i];
			if (temperature[i] < cold) cold = temperature[i];
			if (temperature[i] > warm) warm = temperature[i];
			if (MathF.Abs(latitude[i]) > 1.309f) { polarCount++; polarTemperatureSum += temperature[i]; }
		}
		_log?.Invoke($"[WORLD] R={SphericalFbmNoise.WorldRadiusKm:F0} km  cells={n}  cell={scale.CellAreaKm2:F0} km²  spacing={scale.NeighborCenterKm:F1} km");
		_log?.Invoke($"[TERRAIN] height={low:F0}…{high:F0} m  land={100.0 * landCount / n:F1}%  landMean={landHeightSum / Math.Max(1, landCount):F0} m（地球 30.6% / 546 m）");
		_log?.Invoke($"[CLIMATE] temp={temperatureSum / n:F1} °C  land={landTemperatureSum / Math.Max(1, landCount):F1}  polar={polarTemperatureSum / Math.Max(1, polarCount):F1}  range={cold:F0}…{warm:F0}");
	}

	internal void Adopt(WorldGenerationResult result)
	{
		ThrowIfDisposed();
		ArgumentNullException.ThrowIfNull(result);
		Install(result);
		WorldPublished?.Invoke(Sim.Facts);
	}

	void Install(WorldGenerationResult result)
	{
		_playback?.Stop();
		_spec = result.Spec;
		Sim = result.Simulation;
		Sim.Publish(result.State);
		_playback = new WorldSculptingPlayback(Sim, result.Sculpting);
		LogGenerationSummary();
		IsInitialized = true;
	}

	public void Regenerate()
	{
		ThrowIfDisposed();
		if (!IsInitialized) throw new InvalidOperationException("世界尚未初始化。");
		WorldGenerationOptions options = WorldGenerationPresets.Production;
		var result = new WorldGenerationJob().Run(Sim, _spec, options.InitialSculptingRounds,
			CancellationToken.None);
		Adopt(result);
	}

	public void PauseEvolution() => _playback?.Pause();

	public void Tick(double delta)
	{
		ThrowIfDisposed();
		var facts = _playback?.Tick(delta, error => _reportEvolutionFailure?.Invoke(error));
		if (facts != null) WorldPublished?.Invoke(facts);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_playback?.Stop();
		_playback = null;
		WorldPublished = null;
	}

	void ThrowIfDisposed()
	{
		if (_disposed) throw new ObjectDisposedException(nameof(WorldSession));
	}

	EvolutionControlStatus IGeographyEvolutionControl.Status => EvolutionStatus;
	bool IGeographyEvolutionControl.Busy => EvolutionBusy;
	void IGeographyEvolutionControl.Play() => _playback?.Play();
	void IGeographyEvolutionControl.Pause() => PauseEvolution();
	void IGeographyEvolutionControl.Step() => _playback?.Step();
	void IGeographyEvolutionControl.ConfirmFault() => _playback?.ConfirmFault();
	string IGeographyEvolutionControl.Seek(long targetRound)
		=> _playback == null ? "世界雕刻尚未初始化。" : _playback.Seek(targetRound);
	void IGeographyEvolutionControl.SetSpeed(double roundsPerSecond)
	{
		try { _playback?.SetSpeed(roundsPerSecond); }
		catch (ArgumentOutOfRangeException error) { _log?.Invoke($"[EVOLVE] {error.Message}"); }
	}
}
