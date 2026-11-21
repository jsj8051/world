using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>根据求解选中的张裂空缺生成裂谷或洋底，并接回两侧地表。</summary>
internal static class SculptingSurfaceGaps
{
	internal sealed record Columns(int[] Owners, float[] Heights, CrustType[] CrustTypes, int[] ProvinceIds)
	{
		internal Columns(int count) : this(new int[count], new float[count], new CrustType[count], new int[count]) { }
	}

	internal static void FillNewSurface(Ball ball, Columns surface, SculptingCoverage coverage, IReadOnlyList<int> sampledCells,
		SculptingSurfaceState previous, SculptingCrustContact[] contacts, float oceanDepth, CancellationToken cancellation)
	{
		float sea = previous.SeaLevelM;
		float drowned = sea - .25f * oceanDepth;
		var survives = new bool[previous.SampledPlateOrientations.Count];
		int[] remainingShore = null;
		foreach (int source in sampledCells)
			if (source >= 0) survives[previous.PlateOfCell[source]] = true;
		for (int cell = 0; cell < surface.Owners.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			if (sampledCells[cell] >= 0) continue;
			var opening = coverage.Openings[cell].Value;
			int first = opening.First, second = opening.Second;
			int firstDistance = opening.FirstDistance, secondDistance = opening.SecondDistance;
			if (!survives[previous.PlateOfCell[first]])
			{
				if (survives[previous.PlateOfCell[second]])
				{
					(first, second) = (second, first);
					(firstDistance, secondDistance) = (secondDistance, firstDistance);
				}
				else
				{
					remainingShore ??= SculptingSurfaceSampling.ExtendShore(ball, sampledCells, cancellation);
					first = remainingShore[cell];
				}
			}
			float firstHeight = previous.HeightM[first], secondHeight = previous.HeightM[second];
			bool marine = previous.CrustTypes[first] == CrustType.Oceanic
				|| opening.Width >= 2 && firstHeight <= drowned && secondHeight <= drowned;
			surface.Owners[cell] = previous.PlateOfCell[first];
			surface.CrustTypes[cell] = marine ? CrustType.Oceanic : CrustType.Continental;
			surface.ProvinceIds[cell] = marine ? -1 : previous.CrustProvinceIds[first];
			bool blend = previous.CrustTypes[first] == previous.CrustTypes[second]
				&& (firstHeight > sea) == (secondHeight > sea);
			float mix = (float)firstDistance / (firstDistance + secondDistance);
			if (marine)
			{
				float waterline = sea - Math.Min(20, .10f * oceanDepth);
				float shore = Lerp(Math.Min(firstHeight, waterline), Math.Min(secondHeight, waterline), mix);
				float center = Math.Clamp(2 * Math.Min(firstDistance, secondDistance) - 1, 1, 5) / 5f;
				center *= Math.Clamp(opening.Width / 4f, 0, 1);
				surface.Heights[cell] = Lerp(shore, sea - oceanDepth, center * center * (3 - 2 * center));
			}
			else surface.Heights[cell] = blend ? Lerp(firstHeight, secondHeight, mix) : firstHeight;
			contacts[cell] = marine ? SculptingCrustContact.OceanSpreading : SculptingCrustContact.ContinentalStretch;
		}
	}

	static float Lerp(float first, float second, float weight) => first + weight * (second - first);

}
