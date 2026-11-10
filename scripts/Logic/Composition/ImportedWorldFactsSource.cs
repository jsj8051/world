using World.Data.Maps;

namespace World.Logic;

internal sealed class ImportedWorldFactsSource : IWorldFactsSource
{
	public GeographySnapshot Snapshot { get; }
	public WorldFacts Facts { get; }

	internal ImportedWorldFactsSource(GeographySnapshot snapshot, WorldFacts facts)
	{
		Snapshot = MapStructure.Present(snapshot, "imported.snapshot");
		Facts = MapStructure.Present(facts, "imported.facts");
		MapStructure.Require(facts.Evolution == null, "imported.facts.evolution",
			"An imported result must not retain an evolution session or frame.");
		MapStructure.Require(facts.Ball.Res == snapshot.Manifest.H3Resolution
			&& facts.Ball.Radius == snapshot.Manifest.RenderRadius, "imported.facts.grid",
			"Runtime grid scale must match the snapshot.", MapIssueCode.InvalidShape);
		MapStructure.Length(facts.Ball.CellIds.Length, snapshot.Grid.CellIds.Count, "imported.facts.cellIds");
		MapStructure.Length(facts.HeightM.Length, snapshot.Terrain.HeightM.Count, "imported.facts.heightM");
		for (int i = 0; i < snapshot.Grid.CellIds.Count; i++)
		{
			MapStructure.Require(facts.Ball.CellIds[i] == snapshot.Grid.CellIds[i], "imported.facts.cellIds",
				"Runtime cell ordering must match the file ordering.", MapIssueCode.InvalidReference, i);
			MapStructure.Require(System.BitConverter.SingleToInt32Bits(facts.HeightM[i])
				== System.BitConverter.SingleToInt32Bits(snapshot.Terrain.HeightM[i]), "imported.facts.heightM",
				"Runtime elevations must preserve saved values exactly.", index: i);
		}
	}
}