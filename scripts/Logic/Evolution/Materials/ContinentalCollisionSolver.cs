using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;
using static World.Logic.ViscousContactSolver;

namespace World.Logic;

internal sealed record ContinentalDeformation(
	IReadOnlyDictionary<int, ContinentalDeformation.FaceMotion> Faces,
	IReadOnlyDictionary<int, ContinentalDeformation.CellMotion> Cells)
{
	internal readonly record struct FaceMotion(int PlateA, int PlateB, double RateA, double RateB);
	internal readonly record struct CellMotion(int Plate, double ConvergenceKm2PerMa);
}

/// <summary>用陆壳占地、厚度和当前碰撞边组装局部黏性网络，输出共享边的面积通量。</summary>
internal static class ContinentalCollisionSolver
{
	const double DecayLengthKm = 150;
	const double EffectiveViscosityPaS = 1e22;
	const double GravityMPerS2 = 9.81;
	const double MetersPerSecondPerKmPerMa = 1000d / (1e6 * 365.25 * 86400);
	readonly record struct Contact(int Face, int PlateA, int PlateB, int NodeA, int NodeB, double ResistanceA,
		double ResistanceB, double SpeedA, double SpeedB);
	readonly record struct InteriorFace(int Face, int Plate, int NodeA, int NodeB);

	public static ContinentalDeformation Solve(EvolutionGrid grid, EvolutionCrust material, CrustProjection projection,
		IReadOnlyList<PlateMotion> motions, EvolutionSpec spec, CancellationToken cancellation)
	{
		var activeEdges = new List<PlateBoundaryEdge>();
		var distance = new Dictionary<int, double>();
		var pending = new PriorityQueue<int, double>();
		for (int i = 0; i < projection.PlateBoundaries.Edges.Length; i++)
		{
			if (projection.Boundary.ConvergenceKinds[i] != ConvergenceKind.Collision) continue;
			var edge = projection.PlateBoundaries.Edges[i];
			activeEdges.Add(edge);
			Visit(edge.CellA, 0);
			Visit(edge.CellB, 0);
		}
		if (activeEdges.Count == 0) return null;

		double reach = Math.Max(3 * DecayLengthKm, 1.25 * grid.MinNeighborDistanceKm);
		while (pending.TryDequeue(out int cell, out double km))
		{
			if (km != distance[cell]) continue;
			cancellation.ThrowIfCancellationRequested();
			int plate = projection.PlateOfCell[cell];
			foreach (int neighbor in grid.Neighbors[cell])
			{
				if (!SameContinentalPlate(neighbor, plate)) continue;
				double next = km + DistanceKm(cell, neighbor);
				if (next <= reach) Visit(neighbor, next);
			}
		}

		var cellIndices = new int[distance.Count];
		distance.Keys.CopyTo(cellIndices, 0);
		Array.Sort(cellIndices);
		var nodeByCell = new Dictionary<int, int>(cellIndices.Length);
		var nodes = new Node[cellIndices.Length];
		var links = new List<Link>();
		var contacts = new List<Contact>();
		var interior = new List<InteriorFace>();
		var strengths = new double[cellIndices.Length];
		var pressureByCell = new Dictionary<int, double>(cellIndices.Length);
		double referenceThicknessKm = spec.InitialContinentalThicknessKm;
		// 薄片近似下的重力势能差。归一到黏性阻力使用的 η·H₀ 和 km/Ma 单位。
		double pressureScale = 0.5 * spec.ContinentalDensityKgM3 * GravityMPerS2
			* (1 - spec.ContinentalDensityKgM3 / spec.MantleDensityKgM3) * 1e6
			/ (EffectiveViscosityPaS * referenceThicknessKm * MetersPerSecondPerKmPerMa);
		for (int i = 0; i < nodes.Length; i++)
		{
			int cell = cellIndices[i];
			nodeByCell.Add(cell, i);
			strengths[i] = Strength(cell, projection.PlateOfCell[cell]);
			nodes[i] = new Node(Velocity.From(grid.Centers[cell]),
				strengths[i] * grid.AreasKm2[cell] / (DecayLengthKm * DecayLengthKm));
		}
		foreach (var edge in activeEdges)
		{
			int a = nodeByCell[edge.CellA], b = nodeByCell[edge.CellB];
			var (start, end) = grid.FacesStartingAt(edge.CellA);
			for (int faceIndex = start; faceIndex < end; faceIndex++)
			{
				var face = grid.Faces[faceIndex];
				if (face.B != edge.CellB) continue;
				// 接触面的共享法向速度可以消元。两段半格黏性阻力以串联方式组合。
				double scale = 2 * face.LengthKm / DistanceKm(face.A, face.B);
				double resistanceA = scale * strengths[a], resistanceB = scale * strengths[b];
				double speedA = face.Rate(motions[edge.PlateA].OmegaRadPerMa) / face.LengthKm;
				double speedB = face.Rate(motions[edge.PlateB].OmegaRadPerMa) / face.LengthKm;
				contacts.Add(new Contact(faceIndex, edge.PlateA, edge.PlateB, a, b, resistanceA, resistanceB, speedA, speedB));
				links.Add(new Link(a, b, resistanceA * resistanceB / (resistanceA + resistanceB),
					Velocity.From(face.Outward), speedA - speedB));
				AddPressureGradient(face, a, b);
			}
		}
		for (int i = 0; i < nodes.Length; i++)
		{
			int cell = cellIndices[i], plate = projection.PlateOfCell[cell];
			foreach (int neighbor in grid.Neighbors[cell])
			{
				if (!SameContinentalPlate(neighbor, plate)) continue;
				bool included = nodeByCell.TryGetValue(neighbor, out int other);
				if (included && cell > neighbor) continue;
				int first = Math.Min(cell, neighbor), last = Math.Max(cell, neighbor);
				var (start, end) = grid.FacesStartingAt(first);
				double spacing = DistanceKm(cell, neighbor);
				double strengthB = included ? strengths[other] : Strength(neighbor, plate);
				for (int faceIndex = start; faceIndex < end; faceIndex++)
				{
					var face = grid.Faces[faceIndex];
					if (face.B != last) continue;
					double weight = 2 * strengths[i] * strengthB / (strengths[i] + strengthB) * face.LengthKm / spacing;
					links.Add(new Link(i, included ? other : -1, weight));
					AddPressureGradient(face, cell == first ? i : included ? other : -1,
						cell == first ? included ? other : -1 : i);
					if (included) interior.Add(new InteriorFace(faceIndex, plate, i, other));
				}
			}
		}

		Velocity[] corrections = ViscousContactSolver.Solve(nodes, links, cancellation);
		var faces = new Dictionary<int, ContinentalDeformation.FaceMotion>(contacts.Count + interior.Count);
		var convergence = new double[nodes.Length];
		foreach (var edge in interior)
		{
			var face = grid.Faces[edge.Face];
			double baseline = face.Rate(motions[edge.Plate].OmegaRadPerMa);
			double change = 0.5 * (corrections[edge.NodeA] + corrections[edge.NodeB]).Dot(Velocity.From(face.Outward)) * face.LengthKm;
			if (change == 0) continue;
			faces.Add(edge.Face, new ContinentalDeformation.FaceMotion(edge.Plate, -1, baseline + change, 0));
			convergence[edge.NodeA] -= change;
			convergence[edge.NodeB] += change;
		}
		foreach (var contact in contacts)
		{
			var face = grid.Faces[contact.Face];
			Velocity normal = Velocity.From(face.Outward);
			double speedA = contact.SpeedA + corrections[contact.NodeA].Dot(normal);
			double speedB = contact.SpeedB + corrections[contact.NodeB].Dot(normal);
			if (speedA > speedB)
			{
				double shared = (contact.ResistanceA * speedA + contact.ResistanceB * speedB)
					/ (contact.ResistanceA + contact.ResistanceB);
				speedA = speedB = shared;
			}
			faces.Add(contact.Face, new ContinentalDeformation.FaceMotion(contact.PlateA, contact.PlateB,
				speedA * face.LengthKm, speedB * face.LengthKm));
			convergence[contact.NodeA] += (contact.SpeedA - speedA) * face.LengthKm;
			convergence[contact.NodeB] += (speedB - contact.SpeedB) * face.LengthKm;
		}
		var cells = new Dictionary<int, ContinentalDeformation.CellMotion>(nodes.Length);
		for (int i = 0; i < nodes.Length; i++)
			cells.Add(cellIndices[i], new ContinentalDeformation.CellMotion(projection.PlateOfCell[cellIndices[i]], convergence[i]));
		return new ContinentalDeformation(faces, cells);

		void Visit(int cell, double km)
		{
			if (distance.TryGetValue(cell, out double previous) && previous <= km) return;
			distance[cell] = km;
			pending.Enqueue(cell, km);
		}

		bool SameContinentalPlate(int cell, int plate) =>
			projection.PlateOfCell[cell] == plate && material.DominantType(cell, plate) == CrustType.Continental;

		void AddPressureGradient(EvolutionFace face, int nodeA, int nodeB)
		{
			double gradient = 0.5 * face.LengthKm * (Pressure(face.A) - Pressure(face.B));
			Velocity force = Velocity.From(face.Outward) * gradient;
			if (nodeA >= 0) nodes[nodeA] = nodes[nodeA] with { DrivingForce = nodes[nodeA].DrivingForce + force };
			if (nodeB >= 0) nodes[nodeB] = nodes[nodeB] with { DrivingForce = nodes[nodeB].DrivingForce + force };
		}

		double Pressure(int cell)
		{
			if (pressureByCell.TryGetValue(cell, out double pressure)) return pressure;
			double anomalyVolume = 0;
			foreach (ref readonly var entry in material.Rows.Row(cell))
				if (entry.Key % 2 == (int)CrustType.Continental)
					anomalyVolume += entry.Slot.Volume - entry.Slot.ReferenceThicknessArea;
			double thickness = Math.Max(0, referenceThicknessKm + anomalyVolume / grid.AreasKm2[cell]);
			pressure = pressureScale * (thickness - referenceThicknessKm) * (thickness + referenceThicknessKm);
			pressureByCell.Add(cell, pressure);
			return pressure;
		}

		double Strength(int cell, int plate) =>
			Math.Max(1e-6, material.Get(cell, plate, CrustType.Continental).Thickness / referenceThicknessKm);

		double DistanceKm(int a, int b)
		{
			var p = grid.Centers[a];
			var q = grid.Centers[b];
			return Math.Acos(Math.Clamp(p.X * q.X + p.Y * q.Y + p.Z * q.Z, -1, 1)) * grid.RadiusKm;
		}
	}
}
