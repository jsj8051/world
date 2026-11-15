using System;
using System.Collections.Generic;
using Godot;
using World.Data;
using World.Logic;
using World.Render;

namespace World.Application.Layers;

/// <summary>
/// 村庄图层的装配者：把会话数据（村庄列表 + 当前阶段）翻译成渲染图层的存在与否。
/// 位于应用层而非渲染层——阶段（文明演化 / 正式游玩）是会话语义，渲染层只负责"给定数据怎么画"。
/// 只在 <see cref="IsApplicable"/> 成立时创建 <see cref="VillageOverlay"/>，否则立刻释放节点；
/// 数据变化由 <see cref="IMapLayerDataSource{T}"/> 的 Changed 驱动，不持有业务数据副本。
/// </summary>
internal sealed class VillageLayerController : IMapLayer, IDisposable
{
	readonly Node _host;
	readonly IMapLayerDataSource<IReadOnlyList<Village>> _villages;
	readonly IMapLayerDataSource<WorldStage> _stage;
	WorldFacts _facts;
	MapMode _mode;
	VillageOverlay _overlay;
	bool _disposed;

	internal VillageLayerController(Node host,
		IMapLayerDataSource<IReadOnlyList<Village>> villages,
		IMapLayerDataSource<WorldStage> stage)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_villages = villages ?? throw new ArgumentNullException(nameof(villages));
		_stage = stage ?? throw new ArgumentNullException(nameof(stage));
		_villages.Changed += Refresh;
		_stage.Changed += Refresh;
	}

	/// <summary>显示条件：有村庄 + 处于文明演化/正式游玩 + 当前是地形模式。</summary>
	internal static bool IsApplicable(WorldStage stage, MapMode mode, int villageCount)
		=> villageCount > 0
			&& stage is WorldStage.CivilizationEvolution or WorldStage.Playing
			&& mode is TerrainMode;

	public void SetContext(WorldFacts facts, MapMode mode)
	{
		if (_disposed) throw new ObjectDisposedException(nameof(VillageLayerController));
		bool factsChanged = !ReferenceEquals(_facts, facts);
		_facts = facts;
		_mode = mode;
		SyncOverlay(factsChanged);
	}

	void Refresh() => SyncOverlay(true);

	void SyncOverlay(bool dataChanged)
	{
		if (_disposed) return;
		var villages = _villages.Current;
		if (_facts == null || !IsApplicable(_stage.Current, _mode, villages?.Count ?? 0))
		{
			ReleaseOverlay();
			return;
		}
		if (_overlay == null)
		{
			_overlay = new VillageOverlay { Name = "Villages" };
			_host.AddChild(_overlay);
			dataChanged = true;
		}
		if (dataChanged) _overlay.SetPoints(_facts, villages);
	}

	void ReleaseOverlay()
	{
		if (_overlay == null) return;
		// Detach immediately so a same-frame reactivation cannot draw the retired node.
		_host.RemoveChild(_overlay);
		_overlay.QueueFree();
		_overlay = null;
	}

	public void UpdateCamera(Camera3D camera) => _overlay?.UpdateCamera(camera);

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_villages.Changed -= Refresh;
		_stage.Changed -= Refresh;
		ReleaseOverlay();
		_facts = null;
	}
}
