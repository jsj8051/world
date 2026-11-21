using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>将世界格上的地形变化交还给各板块的参考网格。</summary>
internal static class SculptingElevationWriteback
{
	readonly record struct Address(int Plate, int Cell, CrustType Crust);
	readonly record struct SurfaceEdit(int Cell, int Plate, int Former, CrustType Crust, bool Initialize,
		bool RemoveFromFormer, double Before, double After);
	readonly record struct ProjectedOverlap(int EditIndex, int Cell, double Area, bool Outgoing);

	struct CellChange
	{
		internal double EditedArea, BeforeEdit, Delta;
		internal double IncomingArea, IncomingHeight;
		internal double OutgoingArea;
	}

	internal static PlateElevationState Apply(Ball ball, PlateElevationState source, SculptingSurfaceState transported,
		SculptingSurfaceState shaped, IReadOnlyList<SculptingPlateState> plates, IReadOnlyList<int> contactOwners,
		IReadOnlyList<int> samples, IReadOnlyList<bool> missingElevations, CancellationToken cancellation)
	{
		var poses = shaped.SampledPlateOrientations;
		var references = new RotationD[plates.Count];
		var worldToPlate = new RotationD[plates.Count];
		for (int plate = 0; plate < references.Length; plate++)
		{
			RotationD reference = plate < source.PlateCount ? source.ReferencePose(plate) : poses[plate];
			references[plate] = reference;
			worldToPlate[plate] = ReferenceEquals(reference, poses[plate]) ? RotationD.Identity
				: RotationOps.Compose(reference, RotationOps.Inverse(poses[plate]));
		}

		var edits = new List<SurfaceEdit>();
		for (int cell = 0; cell < shaped.CellCount; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			int plate = shaped.PlateOfCell[cell];
			int former = contactOwners[cell];
			bool transferred = plate != former;
			bool initialized = samples[cell] < 0 || missingElevations[cell] || transferred;
			double before = transported.HeightM[cell], after = shaped.HeightM[cell];
			if (!initialized && before == after) continue;
			CrustType crust = shaped.CrustTypes[cell];
			bool removeFromFormer = transferred && samples[cell] >= 0 && !plates[former].IsExtinct;
			edits.Add(new SurfaceEdit(cell, plate, former, crust, initialized, removeFromFormer, before, after));
		}

		var geometry = edits.Count == 0 ? null : new SphericalCellOverlap(ball);
		const int batchSize = 512;
		var batches = new List<ProjectedOverlap>[(edits.Count + batchSize - 1) / batchSize];
		if (batches.Length == 1) batches[0] = ProjectBatch(0, geometry);
		else if (batches.Length > 1)
			Parallel.For(0, batches.Length, new ParallelOptions { CancellationToken = cancellation },
				geometry.CreateWorker, (batch, _, worker) =>
				{
					batches[batch] = ProjectBatch(batch, worker);
					return worker;
				}, _ => { });

		// Only the geometry is parallel. Reducing in world-cell order preserves
		// the accumulation order of signed edits regardless of worker scheduling.
		var changes = new Dictionary<Address, CellChange>();
		foreach (var batch in batches)
		{
			cancellation.ThrowIfCancellationRequested();
			foreach (var overlap in batch)
			{
				SurfaceEdit edit = edits[overlap.EditIndex];
				var address = new Address(overlap.Outgoing ? edit.Former : edit.Plate, overlap.Cell, edit.Crust);
				ref CellChange change = ref CollectionsMarshal.GetValueRefOrAddDefault(changes, address, out _);
				if (overlap.Outgoing) change.OutgoingArea += overlap.Area;
				else if (edit.Initialize)
				{
					change.IncomingArea += overlap.Area;
					change.IncomingHeight += overlap.Area * edit.After;
				}
				else
				{
					change.EditedArea += overlap.Area;
					change.BeforeEdit += overlap.Area * edit.Before;
					change.Delta += overlap.Area * (edit.After - edit.Before);
				}
			}
		}

		var editor = new PlateElevationState.Editor(source, references);
		int visited = 0;
		foreach (var (address, change) in changes)
		{
			if ((visited++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			var (plate, cell, crust) = address;
			double cellArea = geometry.CellArea(cell);
			float existing = 0;
			bool hadHeight = plate < source.PlateCount && source.TryGet(plate, cell, crust, out existing);
			if (change.IncomingArea == 0 && change.EditedArea == 0)
			{
				if (hadHeight && change.OutgoingArea >= .5 * cellArea)
					editor.Remove(plate, cell, crust);
				continue;
			}

			double height;
			if (hadHeight)
			{
				height = existing;
				if (change.IncomingArea > 0)
				{
					double fraction = Math.Min(1, change.IncomingArea / cellArea);
					height += fraction * (change.IncomingHeight / change.IncomingArea - height);
				}
			}
			else
			{
				height = (change.IncomingHeight + change.BeforeEdit) / (change.IncomingArea + change.EditedArea);
			}
			editor.Set(plate, cell, crust, (float)Math.Clamp(height + change.Delta / cellArea, float.MinValue, float.MaxValue));
		}
		for (int plate = 0; plate < plates.Count; plate++)
			if (plates[plate].IsExtinct) editor.ClearPlate(plate);
		return editor.Finish();

		List<ProjectedOverlap> ProjectBatch(int batch, SphericalCellOverlap worker)
		{
			int start = batch * batchSize;
			int end = Math.Min(start + batchSize, edits.Count);
			var projected = new List<ProjectedOverlap>(5 * (end - start));
			var overlaps = new List<SphericalCellOverlap.Overlap>();
			for (int index = start; index < end; index++)
			{
				if ((index & 63) == 0) cancellation.ThrowIfCancellationRequested();
				SurfaceEdit edit = edits[index];
				worker.Project(edit.Cell, worldToPlate[edit.Plate], overlaps);
				foreach (var overlap in overlaps)
					projected.Add(new ProjectedOverlap(index, overlap.Cell, overlap.Area, false));
				if (!edit.RemoveFromFormer) continue;
				worker.Project(edit.Cell, worldToPlate[edit.Former], overlaps);
				foreach (var overlap in overlaps)
					projected.Add(new ProjectedOverlap(index, overlap.Cell, overlap.Area, true));
			}
			return projected;
		}
	}
}
