namespace World.Data;

/// <summary>一个陆壳省：生长核、来源板块与格数。可跨板块，<see cref="SeedPlateId"/> 只记核的位置。</summary>
public readonly struct CrustProvince
{
    public readonly int Id;
    public readonly CrustType Type;
    public readonly int SeedCell;
    public readonly int SeedPlateId;
    public readonly int CellCount;

    public CrustProvince(int id, CrustType type, int seedCell, int seedPlateId, int cellCount)
    {
        Id = id;
        Type = type;
        SeedCell = seedCell;
        SeedPlateId = seedPlateId;
        CellCount = cellCount;
    }
}
