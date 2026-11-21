using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>Draws relief around this round's resolved contacts, using the pre-merge plate sides.</summary>
internal static class SculptingRelief
{
	enum Profile { Suture, LandArc, OceanArc, Trench, Rift, Ridge }

	internal static void Apply(Ball ball, TerrainSpec spec, float[] heights,
		SculptingSurfaceTransportModule.Result transport, CancellationToken cancellation)
	{
		if (transport.Coverage == null) return;
		var surface = transport.Surface;
		var owners = transport.ContactOwners;
		var contacts = transport.Contacts;
		var strengths = new float[6][];
		var seeds = new List<int>[strengths.Length];
		for (int i = 0; i < strengths.Length; i++)
		{
			strengths[i] = new float[heights.Length];
			seeds[i] = new List<int>();
		}
		for (int cell = 0; cell < heights.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			var contact = contacts[cell];
			if (contact == SculptingCrustContact.None) continue;
			if (contact is SculptingCrustContact.ContinentalStretch or SculptingCrustContact.OceanSpreading)
			{
				var opening = transport.Coverage.Openings[cell].Value;
				var profile = contact == SculptingCrustContact.OceanSpreading ? Profile.Ridge : Profile.Rift;
				float balance = MathF.Abs(opening.FirstDistance - opening.SecondDistance);
				Mark(profile, cell, (.25f + .75f / (1 + balance)) * Strength(opening.Width));
				continue;
			}
			var impact = transport.Coverage.Collisions[cell];
			float force = Strength(impact.Strength);
			var uplift = contact == SculptingCrustContact.ContinentalCollision ? Profile.Suture
				: contact == SculptingCrustContact.ContinentalMargin ? Profile.LandArc : Profile.OceanArc;
			bool onSuture = false;
			foreach (int neighbor in ball.CellNeighbors[cell])
			{
				int other = owners[neighbor];
				if (other == owners[cell] || !IsOpponent(impact, other)) continue;
				if (contact == SculptingCrustContact.ContinentalCollision && surface.CrustTypes[neighbor] == CrustType.Continental)
				{
					Mark(Profile.Suture, neighbor, force);
					onSuture = true;
				}
				if (contact == SculptingCrustContact.ContinentalMargin && surface.CrustTypes[neighbor] == CrustType.Oceanic)
				{
					Mark(Profile.Trench, neighbor, force);
					onSuture = true;
				}
			}
			Mark(uplift, cell, onSuture ? force : .45f * force);
		}

		float spacing = (float)SpatialScale.Of(ball).NeighborCenterKm;
		var adjustments = new float[heights.Length];
		var noise = new SphericalFbmNoise(3817, 1850, 3);
		Paint(Profile.Suture, spec.MountainUpperBandKm, .18f * spec.MountainPeakM);
		Paint(Profile.LandArc, spec.MountainArcBandKm, .16f * spec.MountainPeakM * spec.MountainArcCoef, .24f);
		Paint(Profile.OceanArc, spec.MountainArcBandKm, .09f * spec.MountainPeakM);
		Paint(Profile.Trench, spec.TrenchBandKm, -.30f * spec.TrenchDepthM);
		Paint(Profile.Rift, spec.RiftBandKm, -.22f * spec.RiftDepthM);
		Paint(Profile.Ridge, spec.RidgeBandKm, .24f * spec.RidgeCrestRiseM);
		float scale = TerrainManager.HeightScale(spec), sea = surface.SeaLevelM;
		float floor = Math.Min(sea, (spec.OceanDeepestM - 3 * Math.Max(0, spec.TrenchDepthM)) * scale);
		float ceiling = sea + Math.Max(1000, spec.MountainPeakM * scale);
		for (int cell = 0; cell < heights.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			float height = Math.Clamp(heights[cell] + scale * adjustments[cell], Math.Min(floor, heights[cell]),
				Math.Max(ceiling, heights[cell]));
			heights[cell] = contacts[cell] == SculptingCrustContact.OceanSpreading
				? Math.Min(height, MathF.BitDecrement(sea)) : height;
		}

		void Mark(Profile profile, int cell, float strength)
		{
			var field = strengths[(int)profile];
			if (field[cell] == 0) seeds[(int)profile].Add(cell);
			field[cell] = Math.Max(field[cell], strength);
		}

		void Paint(Profile profile, float configuredWidth, float amplitude, float inlandPeak = 0)
		{
			var sources = seeds[(int)profile];
			if (sources.Count == 0 || configuredWidth <= 0 || amplitude == 0) return;
			float width = Math.Max(configuredWidth, 2.2f * spacing);
			var distance = DistanceFieldOps.Build(ball, sources, cell => strengths[(int)profile][cell], 1.28f * width,
				(from, to) => surface.CrustTypes[from] == surface.CrustTypes[to]
					&& (profile is Profile.Suture or Profile.Rift or Profile.Ridge || owners[from] == owners[to]));
			for (int cell = 0; cell < heights.Length; cell++)
			{
				if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
				if (distance.Src[cell] < 0) continue;
				float variation = noise.Sample(ball.CellDirs[cell]);
				float position = distance.DistKm[cell] / (width * (1 + .26f * variation));
				float fade = Math.Clamp(1 - MathF.Abs(position - inlandPeak) / (1 - inlandPeak), 0, 1);
				float falloff = fade * fade * (3 - 2 * fade);
				adjustments[cell] += amplitude * distance.Strength[cell] * falloff * (1 + .22f * variation);
			}
		}
	}

	static float Strength(float motion) => Math.Clamp(.35f + .22f * motion, .35f, 1.7f);

	static bool IsOpponent(SculptingCoverage.Collision impact, int plate)
	{
		foreach (var opponent in impact.Opponents)
			if (opponent.Plate == plate) return true;
		return false;
	}
}
