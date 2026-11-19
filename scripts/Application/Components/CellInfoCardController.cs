using World.Application.MapViewing;
using World.Data;
using World.Logic;

namespace World.Application.Components;

/// <summary>根据选中格和当前展示模式准备卡片内容。</summary>
internal static class CellInfoCardController
{
	internal static MapSelectionContent DescribeSelection(PickedCell? picked, bool showCrustDetails,
		IWorldFactsSource factsSource)
	{
		if (picked is not PickedCell cell) return null;

		string bandName = ElevationBands.Name(ElevationBands.Of(cell.ElevM));
		WorldFacts facts = showCrustDetails ? factsSource?.Facts : null;
		CrustCellDetails crust = facts == null ? null : CrustCellQuery.Read(facts, cell.Index);
		return CellInfoCardFormatter.Describe(cell, bandName, crust);
	}
}
