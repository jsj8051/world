using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>每轮按显式世界轴旋转指令累积板块姿态，不积分速度、不拆物理时间子步，也不重画地表。</summary>
public sealed class PlateMovementModule : IWorldSculptingModule<WorldSculptingState, WorldSculptingModuleOutput>
{
	public const string ModuleId = "plate.movement";
	public const string PosesFact = "plate.poses";
	readonly PlateRotation[] _rotations;

	public string Id => ModuleId;
	public IReadOnlyCollection<string> Dependencies { get; } = Array.Empty<string>();
	public IReadOnlyCollection<string> RequiredFacts { get; } = Array.Empty<string>();
	public IReadOnlyCollection<string> ProducedFacts { get; } = Array.AsReadOnly(new[] { PosesFact });

	/// <summary>冻结逐板块每轮旋转计划；必须覆盖整个板块表，静止板块须显式提供零角度指令。</summary>
	/// <example><code>var module = new PlateMovementModule(rotations);</code></example>
	public PlateMovementModule(IEnumerable<PlateRotation> rotations)
	{
		if (rotations == null) throw new ArgumentNullException(nameof(rotations));
		_rotations = rotations.ToArray();
		if (_rotations.Length == 0) throw new ArgumentException("旋转计划不能为空", nameof(rotations));
		for (int i = 0; i < _rotations.Length; i++)
			if (_rotations[i] == null || _rotations[i].PlateId != i)
				throw new ArgumentException($"旋转计划必须按板块 ID 连续排列：{i}", nameof(rotations));
	}

	/// <summary>从来源姿态计算本轮新姿态；旋转按世界坐标左乘，不读取旧物理 Omega。</summary>
	public void Execute(WorldSculptingRoundData<WorldSculptingState, WorldSculptingModuleOutput> data,
		CancellationToken cancellation)
	{
		long profile = WorldBuildProfile.Start();
		var source = data.Source;
		if (_rotations.Length > source.Plates.Count) throw new ArgumentException("旋转计划与世界板块数不一致");
		var plates = new SculptingPlateState[source.Plates.Count];
		var rotations = new PlateRotation[plates.Length];
		for (int i = 0; i < plates.Length; i++)
		{
			cancellation.ThrowIfCancellationRequested();
			SculptingPlateState plate = source.Plates[i];
			PlateRotation move = plate.RotationOverride ?? (i < _rotations.Length ? _rotations[i]
				: throw new ArgumentException($"板块 {i} 缺少旋转计划"));
			rotations[i] = move;
			RotationD orientation = plate.IsExtinct || move.AngleRadians == 0 ? plate.Orientation
				: RotationOps.Compose(RotationOps.FromAxisAngle(move.Axis, move.AngleRadians), plate.Orientation);
			plates[i] = plate.WithOrientation(orientation);
		}
		cancellation.ThrowIfCancellationRequested();
		data.Write(new PlateMovementResult(source, data.Round, plates, rotations));
		WorldBuildProfile.Mark(ref profile, "sculpting plate poses");
	}
}
